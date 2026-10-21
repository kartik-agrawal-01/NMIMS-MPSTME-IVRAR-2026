using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace BoardroomXR.Auth
{
    /// <summary>
    /// Persists the per-user enrolled gesture template to disk so the gesture
    /// factor compares a live stroke against something the user actually drew
    /// earlier, rather than against a hardcoded shape.
    ///
    /// On a Quest this lands in Application.persistentDataPath, which survives
    /// app restarts, so you enrol once and every later run authenticates
    /// against that recording.
    /// </summary>
    public static class GestureTemplateStore
    {
        [Serializable]
        private class TemplateFile
        {
            public string userId;
            public string capturedUtc;
            public List<Vector3> points = new();
        }

        private static string DirectoryPath => Path.Combine(Application.persistentDataPath, "MFAEnrolment");

        private static string PathFor(string userId)
        {
            // Keep the filename filesystem-safe without losing which user it belongs to.
            var safe = new System.Text.StringBuilder();
            foreach (char c in userId ?? "unknown")
                safe.Append(char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '_');
            return Path.Combine(DirectoryPath, $"gesture_{safe}.json");
        }

        public static bool HasTemplate(string userId) => File.Exists(PathFor(userId));

        /// <summary>Returns the enrolled stroke, or null if this user has never enrolled.</summary>
        public static List<Vector3> Load(string userId)
        {
            string path = PathFor(userId);
            if (!File.Exists(path)) return null;

            try
            {
                var parsed = JsonUtility.FromJson<TemplateFile>(File.ReadAllText(path));
                if (parsed?.points == null || parsed.points.Count < 2) return null;
                return parsed.points;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[GestureTemplateStore] Could not read template for {userId}: {e.Message}");
                return null;
            }
        }

        /// <summary>Writes the stroke the user just drew as their enrolled template.</summary>
        public static bool Save(string userId, List<Vector3> points)
        {
            if (points == null || points.Count < 2) return false;

            try
            {
                Directory.CreateDirectory(DirectoryPath);
                var payload = new TemplateFile
                {
                    userId = userId,
                    capturedUtc = DateTime.UtcNow.ToString("o"),
                    points = new List<Vector3>(points)
                };
                File.WriteAllText(PathFor(userId), JsonUtility.ToJson(payload));
                return true;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[GestureTemplateStore] Could not save template for {userId}: {e.Message}");
                return false;
            }
        }

        /// <summary>Removes the enrolment so the next capture re-enrols. Used by the demo reset.</summary>
        public static void Clear(string userId)
        {
            try
            {
                string path = PathFor(userId);
                if (File.Exists(path)) File.Delete(path);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[GestureTemplateStore] Could not clear template for {userId}: {e.Message}");
            }
        }
    }
}
