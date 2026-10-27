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
    /// <summary>Outcome of handing one enrolment stroke to <see cref="GestureAuthenticator.SubmitEnrolmentStroke"/>.</summary>
    public enum GestureEnrolmentResult
    {
        /// <summary>Stroke too short to be an attempt; nothing was stored.</summary>
        TooShort,
        /// <summary>Stored; more strokes still needed.</summary>
        NeedMore,
        /// <summary>Inconsistent with the first stroke; enrolment was restarted.</summary>
        Rejected,
        /// <summary>All required strokes collected; the template is ready.</summary>
        Complete,
    }

    public class GestureAuthenticator : MonoBehaviour
    {
        public event Action<float> OnGestureScored; // similarity score 0-1

        /// <summary>Strokes collected during enrolment, so one odd re-draw cannot define the user.</summary>
        public const int DefaultEnrolmentSamples = 3;

        /// <summary>
        /// How similar strokes 2 and 3 must be to stroke 1 for the enrolment to be
        /// accepted. Below this the user clearly drew something else, and enrolling it
        /// would bake an unusable template into their badge.
        /// </summary>
        public const float EnrolmentConsistencyThreshold = 0.5f;

        private readonly List<Vector3> _livePoints = new();
        private bool _capturing;
        private MFASettings _settings;
        private readonly List<List<Vector3>> _samples = new();
        private readonly List<List<Vector3>> _pendingSamples = new();

        /// <summary>True once a template exists to compare against.</summary>
        public bool IsEnrolled => _samples.Count > 0;

        /// <summary>Enrolled strokes. Verification scores against the best-matching one.</summary>
        public IReadOnlyList<List<Vector3>> EnrolledSamples => _samples;

        /// <summary>How many strokes a fresh enrolment needs.</summary>
        public int RequiredEnrolmentSamples { get; set; } = DefaultEnrolmentSamples;

        /// <summary>Strokes accepted so far in the enrolment currently in progress.</summary>
        public int PendingSampleCount => _pendingSamples.Count;

        /// <summary>Similarity of the last enrolment stroke to the first one. NaN before the second stroke.</summary>
        public float LastEnrolmentConsistency { get; private set; } = float.NaN;

        /// <summary>Which enrolled sample the last verification score came from, or -1.</summary>
        public int LastMatchedSampleIndex { get; private set; } = -1;

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
            Initialize(settings, enrolledTemplate == null ? null : new List<List<Vector3>> { enrolledTemplate });
        }

        /// <summary>
        /// Multi-sample form: every stroke the user drew at enrolment. A live stroke is
        /// scored against all of them and keeps the best match, so natural variation
        /// between re-draws does not reject the genuine user.
        /// </summary>
        public void Initialize(MFASettings settings, List<List<Vector3>> enrolledSamples)
        {
            _settings = settings;
            _samples.Clear();
            _pendingSamples.Clear();
            LastEnrolmentConsistency = float.NaN;
            LastMatchedSampleIndex = -1;
            if (enrolledSamples == null) return;
            foreach (var s in enrolledSamples)
                if (s != null && s.Count >= 2) _samples.Add(new List<Vector3>(s));
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

            _samples.Clear();
            _pendingSamples.Clear();
            _samples.Add(new List<Vector3>(_livePoints));
            return _samples[0];
        }

        /// <summary>
        /// Hands the stroke just drawn to the multi-sample enrolment. The first stroke
        /// defines the shape; later strokes must match it (<see cref="EnrolmentConsistencyThreshold"/>)
        /// or the whole enrolment restarts, so a user who draws three different things
        /// is told rather than quietly enrolled with an unusable template.
        /// </summary>
        public GestureEnrolmentResult SubmitEnrolmentStroke()
        {
            _capturing = false;
            if (_livePoints.Count < 2) return GestureEnrolmentResult.TooShort;

            var stroke = new List<Vector3>(_livePoints);

            if (_pendingSamples.Count == 0)
            {
                LastEnrolmentConsistency = float.NaN;
                _pendingSamples.Add(stroke);
            }
            else
            {
                float consistency = Similarity(stroke, _pendingSamples[0]);
                LastEnrolmentConsistency = consistency;
                if (consistency < EnrolmentConsistencyThreshold)
                {
                    _pendingSamples.Clear();
                    return GestureEnrolmentResult.Rejected;
                }
                _pendingSamples.Add(stroke);
            }

            int required = Mathf.Max(1, RequiredEnrolmentSamples);
            if (_pendingSamples.Count < required) return GestureEnrolmentResult.NeedMore;

            _samples.Clear();
            _samples.AddRange(_pendingSamples);
            _pendingSamples.Clear();
            return GestureEnrolmentResult.Complete;
        }

        /// <summary>Abandons a part-finished enrolment (badge switch, stage change).</summary>
        public void ResetEnrolmentProgress()
        {
            _pendingSamples.Clear();
            LastEnrolmentConsistency = float.NaN;
        }

        public void EndCaptureAndScore()
        {
            _capturing = false;
            float score = ScoreAgainstTemplate();
            LastScore = score;
            OnGestureScored?.Invoke(score);
        }

        /// <summary>Best match across every enrolled stroke.</summary>
        private float ScoreAgainstTemplate()
        {
            LastMatchedSampleIndex = -1;
            if (_settings == null) return 0f;
            if (_samples.Count == 0 || _livePoints.Count < 2) return 0f;

            float best = 0f;
            for (int i = 0; i < _samples.Count; i++)
            {
                float s = Similarity(_livePoints, _samples[i]);
                if (s <= best && LastMatchedSampleIndex >= 0) continue;
                best = s;
                LastMatchedSampleIndex = i;
            }
            return best;
        }

        /// <summary>
        /// $1-style similarity of two raw paths: resample both to a fixed number of
        /// evenly spaced points in a unit box, then invert the mean point distance.
        /// </summary>
        private float Similarity(List<Vector3> a, List<Vector3> b)
        {
            if (a == null || b == null || a.Count < 2 || b.Count < 2) return 0f;

            int n = Mathf.Max(2, _settings != null ? _settings.gestureSampleResolution : 64);
            var ra = Resample(a, n);
            var rb = Resample(b, n);

            int count = Mathf.Min(ra.Count, rb.Count);
            if (count == 0) return 0f;

            float totalDistance = 0f;
            for (int i = 0; i < count; i++)
                totalDistance += Vector3.Distance(ra[i], rb[i]);

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
