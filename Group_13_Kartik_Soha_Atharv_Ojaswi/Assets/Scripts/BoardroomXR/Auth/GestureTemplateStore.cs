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
        /// <summary>
        /// One enrolment stroke. JsonUtility cannot serialise List&lt;List&lt;Vector3&gt;&gt;,
        /// so each stroke is wrapped in its own serialisable object.
        /// </summary>
        [Serializable]
        public class Stroke
        {
            public List<Vector3> points = new();
        }

        [Serializable]
        private class TemplateFile
        {
            public string userId;
            public string capturedUtc;

            /// <summary>
            /// Legacy single-stroke field. Still written (as a copy of samples[0]) and
            /// still read, so enrolment files from v4 and earlier keep working.
            /// </summary>
            public List<Vector3> points = new();

            /// <summary>All enrolment strokes. Verification scores against the best match.</summary>
            public List<Stroke> samples = new();
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

        /// <summary>First enrolled stroke, or null if this user has never enrolled.</summary>
        public static List<Vector3> Load(string userId)
        {
            var samples = LoadSamples(userId);
            return samples != null && samples.Count > 0 ? samples[0] : null;
        }

        /// <summary>
        /// Every enrolled stroke for this user, or null if they have never enrolled.
        /// A file written before multi-sample enrolment existed yields a single stroke.
        /// </summary>
        public static List<List<Vector3>> LoadSamples(string userId)
        {
            string path = PathFor(userId);
            if (!File.Exists(path)) return null;

            try
            {
                var parsed = JsonUtility.FromJson<TemplateFile>(File.ReadAllText(path));
                if (parsed == null) return null;

                var result = new List<List<Vector3>>();
                if (parsed.samples != null)
                    foreach (var s in parsed.samples)
                        if (s?.points != null && s.points.Count >= 2) result.Add(s.points);

                // Legacy / single-stroke file.
                if (result.Count == 0 && parsed.points != null && parsed.points.Count >= 2)
                    result.Add(parsed.points);

                return result.Count > 0 ? result : null;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[GestureTemplateStore] Could not read template for {userId}: {e.Message}");
                return null;
            }
        }

        /// <summary>Writes a single stroke as this user's enrolled template.</summary>
        public static bool Save(string userId, List<Vector3> points)
        {
            if (points == null || points.Count < 2) return false;
            return SaveSamples(userId, new List<List<Vector3>> { points });
        }

        /// <summary>
        /// Writes every stroke the user drew during enrolment. samples[0] is also
        /// written to the legacy <c>points</c> field so an older build could still
        /// read the file.
        /// </summary>
        public static bool SaveSamples(string userId, List<List<Vector3>> samples)
        {
            if (samples == null || samples.Count == 0) return false;

            var usable = new List<Stroke>();
            foreach (var s in samples)
                if (s != null && s.Count >= 2) usable.Add(new Stroke { points = new List<Vector3>(s) });
            if (usable.Count == 0) return false;

            try
            {
                Directory.CreateDirectory(DirectoryPath);
                var payload = new TemplateFile
                {
                    userId = userId,
                    capturedUtc = DateTime.UtcNow.ToString("o"),
                    points = new List<Vector3>(usable[0].points),
                    samples = usable
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
