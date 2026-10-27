using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace BoardroomXR.Biometrics
{
    /// <summary>One recorded pose, expressed in the recording's own start frame.</summary>
    [Serializable]
    public class MimicryFrame
    {
        public float t;
        public Vector3 head;
        public Quaternion headRot;
        public Vector3 left;
        public Vector3 right;
        public bool hasLeft;
        public bool hasRight;
    }

    /// <summary>
    /// A short recording of how an enrolled user actually moved, kept so the demo can
    /// run a <b>mimicry attack</b>: an established threat model for behavioural
    /// biometrics, where the impostor has both the victim's credentials and a chance
    /// to observe and copy their movement.
    ///
    /// Poses are stored relative to the user's own start pose (head position and yaw
    /// at the first frame), so the replay can be placed anywhere in the room.
    ///
    /// Saved next to the behavioural template, keyed by the same employee ID.
    /// </summary>
    [Serializable]
    public class MimicryRecording
    {
        public string userId;
        public string capturedUtc;
        public float hz = 30f;
        public List<MimicryFrame> frames = new List<MimicryFrame>();

        public float DurationSeconds => frames.Count > 0 ? frames[frames.Count - 1].t - frames[0].t : 0f;

        public static string PathFor(string userId)
        {
            foreach (char c in Path.GetInvalidFileNameChars()) userId = userId.Replace(c, '_');
            return Path.Combine(BiometricTemplate.Dir, $"mimicry_{userId}.json");
        }

        public bool Save()
        {
            try
            {
                Directory.CreateDirectory(BiometricTemplate.Dir);
                File.WriteAllText(PathFor(userId), JsonUtility.ToJson(this));
                return true;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[Mimicry] Could not save recording for {userId}: {e.Message}");
                return false;
            }
        }

        public static MimicryRecording Load(string userId)
        {
            string p = PathFor(userId);
            if (!File.Exists(p)) return null;
            try
            {
                var r = JsonUtility.FromJson<MimicryRecording>(File.ReadAllText(p));
                return r != null && r.frames != null && r.frames.Count >= 10 ? r : null;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[Mimicry] Could not read recording for {userId}: {e.Message}");
                return null;
            }
        }

        public static void Delete(string userId)
        {
            try
            {
                string p = PathFor(userId);
                if (File.Exists(p)) File.Delete(p);
            }
            catch (Exception e) { Debug.LogWarning($"[Mimicry] Could not delete recording: {e.Message}"); }
        }
    }

    /// <summary>
    /// Decimates live poses to 30 Hz and keeps the most recent 20 s, in the frame of
    /// reference of the first pose it saw. Fed only during enrolment, so what it
    /// captures is the genuine user's natural movement.
    /// </summary>
    public class MimicryRecorder
    {
        public const float Hz = 30f;
        public const float MaxSeconds = 20f;

        private readonly List<MimicryFrame> _frames = new List<MimicryFrame>();
        private bool _hasOrigin;
        private Vector3 _origin;
        private Quaternion _inverseYaw = Quaternion.identity;
        private float _lastSample = float.NegativeInfinity;
        private float _t0;

        public int FrameCount => _frames.Count;

        public void Reset()
        {
            _frames.Clear();
            _hasOrigin = false;
            _lastSample = float.NegativeInfinity;
        }

        public void Push(KinematicSample s)
        {
            if (s.t - _lastSample < 1f / Hz) return;
            _lastSample = s.t;

            if (!_hasOrigin)
            {
                _hasOrigin = true;
                _origin = s.headPos;
                _t0 = s.t;
                // Yaw-only inverse, so the replay can be re-aimed without tipping over.
                float yaw = s.headRot.eulerAngles.y;
                _inverseYaw = Quaternion.Euler(0f, -yaw, 0f);
            }

            _frames.Add(new MimicryFrame
            {
                t = s.t - _t0,
                head = _inverseYaw * (s.headPos - _origin),
                headRot = _inverseYaw * s.headRot,
                left = _inverseYaw * (s.leftPos - _origin),
                right = _inverseYaw * (s.rightPos - _origin),
                hasLeft = s.hasLeft,
                hasRight = s.hasRight,
            });

            // Keep only the last MaxSeconds.
            float cutoff = _frames[_frames.Count - 1].t - MaxSeconds;
            int drop = 0;
            while (drop < _frames.Count && _frames[drop].t < cutoff) drop++;
            if (drop > 0) _frames.RemoveRange(0, drop);
        }

        /// <summary>Snapshot of what has been recorded, or null if it is too short to replay.</summary>
        public MimicryRecording Build(string userId)
        {
            if (_frames.Count < 10) return null;
            var r = new MimicryRecording
            {
                userId = userId,
                capturedUtc = DateTime.UtcNow.ToString("o"),
                hz = Hz,
                frames = new List<MimicryFrame>(_frames)
            };
            return r;
        }
    }
}
