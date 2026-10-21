using System;
using System.Collections.Generic;
using UnityEngine;

namespace BoardroomXR.Auth
{
    /// <summary>
    /// Factor 2 - "something you are/do". The user "signs" in mid-air by
    /// drawing a shape with the VR controller (e.g. their initials or a
    /// personal symbol chosen at enrolment). The live path is resampled to a
    /// fixed resolution and compared to the enrolled template using a simple
    /// normalised point-distance score, in the spirit of the $1 Unistroke
    /// Recognizer.
    ///
    /// The template is whatever the user actually drew at enrolment (persisted
    /// by <see cref="GestureTemplateStore"/>); nothing here is hardcoded, and a
    /// stroke that does not match genuinely scores low and genuinely fails.
    ///
    /// Points are fed in by <see cref="GestureCaptureDriver"/> from real
    /// controller input.
    /// </summary>
    public class GestureAuthenticator : MonoBehaviour
    {
        public event Action<float> OnGestureScored; // similarity score 0-1

        private readonly List<Vector3> _livePoints = new();
        private bool _capturing;
        private MFASettings _settings;
        private List<Vector3> _enrolledTemplate;

        /// <summary>True once a template exists to compare against.</summary>
        public bool IsEnrolled => _enrolledTemplate != null && _enrolledTemplate.Count >= 2;

        /// <summary>True while a stroke is being recorded.</summary>
        public bool IsCapturing => _capturing;

        /// <summary>How many raw samples the current/last stroke contains.</summary>
        public int LivePointCount => _livePoints.Count;

        /// <summary>Score produced by the most recent <see cref="EndCaptureAndScore"/>. NaN if none yet.</summary>
        public float LastScore { get; private set; } = float.NaN;

        /// <summary>
        /// Wires in the tuning asset and the enrolled template. Pass a null
        /// template to leave this user unenrolled - the first captured stroke
        /// then becomes their template via <see cref="EnrollFromLastCapture"/>.
        /// </summary>
        public void Initialize(MFASettings settings, List<Vector3> enrolledTemplate)
        {
            _settings = settings;
            _enrolledTemplate = enrolledTemplate;
        }

        public void BeginCapture()
        {
            _livePoints.Clear();
            _capturing = true;
        }

        /// <summary>Call every frame (or fixed interval) from the XR controller pose while capturing.</summary>
        public void RecordPoint(Vector3 localControllerPosition)
        {
            if (!_capturing) return;

            // Skip samples that barely moved: they add nothing to the shape and
            // would bias the arc-length resampling toward wherever the hand paused.
            if (_livePoints.Count > 0 &&
                Vector3.Distance(_livePoints[_livePoints.Count - 1], localControllerPosition) < 0.004f)
                return;

            _livePoints.Add(localControllerPosition);
        }

        /// <summary>Stops recording without scoring. Used when a stroke is abandoned.</summary>
        public void CancelCapture()
        {
            _capturing = false;
            _livePoints.Clear();
        }

        /// <summary>
        /// Promotes the stroke just drawn into this user's enrolled template.
        /// Returns the stored points, or null if the stroke was too short.
        /// </summary>
        public List<Vector3> EnrollFromLastCapture()
        {
            _capturing = false;
            if (_livePoints.Count < 2) return null;

            _enrolledTemplate = new List<Vector3>(_livePoints);
            return _enrolledTemplate;
        }

        public void EndCaptureAndScore()
        {
            _capturing = false;
            float score = ScoreAgainstTemplate();
            LastScore = score;
            OnGestureScored?.Invoke(score);
        }

        private float ScoreAgainstTemplate()
        {
            if (_settings == null) return 0f;
            if (_enrolledTemplate == null || _enrolledTemplate.Count < 2 || _livePoints.Count < 2)
                return 0f;

            int n = Mathf.Max(2, _settings.gestureSampleResolution);
            var liveResampled = Resample(_livePoints, n);
            var templateResampled = Resample(_enrolledTemplate, n);

            int count = Mathf.Min(liveResampled.Count, templateResampled.Count);
            if (count == 0) return 0f;

            float totalDistance = 0f;
            for (int i = 0; i < count; i++)
                totalDistance += Vector3.Distance(liveResampled[i], templateResampled[i]);

            float avgDistance = totalDistance / count;
            // Both strokes are normalised into a unit box, so average per-point
            // distance is already on a 0-1 scale; invert it into a similarity.
            return Mathf.Clamp01(1f - avgDistance);
        }

        /// <summary>Returns true only if a real settings asset is present and the score clears it.</summary>
        public bool PassesThreshold(float score)
        {
            if (_settings == null)
            {
                Debug.LogWarning("[GestureAuthenticator] No MFASettings; cannot evaluate threshold, failing closed.");
                return false;
            }
            return score >= _settings.gestureMatchThreshold;
        }

        /// <summary>Resamples a raw path to N evenly spaced points and normalises scale/position.</summary>
        private List<Vector3> Resample(List<Vector3> path, int n)
        {
            float pathLength = 0f;
            for (int i = 1; i < path.Count; i++) pathLength += Vector3.Distance(path[i - 1], path[i]);

            var result = new List<Vector3> { path[0] };

            if (pathLength <= 1e-6f)
            {
                // Degenerate stroke (hand never moved): return N copies so the
                // caller still gets a well-formed list rather than an exception.
                while (result.Count < n) result.Add(path[path.Count - 1]);
                return NormalizeToUnitBox(result);
            }

            float interval = pathLength / (n - 1);
            float accumulated = 0f;

            for (int i = 1; i < path.Count; i++)
            {
                float segment = Vector3.Distance(path[i - 1], path[i]);
                Vector3 segStart = path[i - 1];

                while (accumulated + segment >= interval && result.Count < n)
                {
                    float t = (interval - accumulated) / Mathf.Max(segment, 1e-6f);
                    Vector3 newPoint = Vector3.Lerp(segStart, path[i], t);
                    result.Add(newPoint);

                    segStart = newPoint;
                    segment -= (interval - accumulated);
                    accumulated = 0f;
                }
                accumulated += segment;
            }

            while (result.Count < n) result.Add(path[path.Count - 1]);
            return NormalizeToUnitBox(result);
        }

        private List<Vector3> NormalizeToUnitBox(List<Vector3> pts)
        {
            Vector3 min = pts[0], max = pts[0];
            foreach (var p in pts) { min = Vector3.Min(min, p); max = Vector3.Max(max, p); }
            Vector3 size = max - min;
            float scale = Mathf.Max(size.x, size.y, size.z, 0.0001f);

            var normalized = new List<Vector3>(pts.Count);
            foreach (var p in pts) normalized.Add((p - min) / scale);
            return normalized;
        }
    }
}
