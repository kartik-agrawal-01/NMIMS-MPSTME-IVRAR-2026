using System.Collections.Generic;
using UnityEngine;
using BoardroomXR.Auth;

namespace BoardroomXR.UI
{
    /// <summary>
    /// Draws the signature as it is being drawn: a glowing line that follows the
    /// drawing hand while the trigger is held during factor 2, then fades out -
    /// green if the stroke passed, red if it did not. Without it, mid-air signing is
    /// guesswork, which is the single biggest usability complaint from the on-device
    /// tests.
    ///
    /// Optionally shows a faint ghost of the enrolled signature (presenter toggle,
    /// off by default) so an audience can see what "the same shape" means.
    /// </summary>
    public class GestureTrail : MonoBehaviour
    {
        public float minPointSpacing = 0.008f;
        public float fadeSeconds = 1.5f;
        public float lineWidth = 0.009f;

        /// <summary>Presenter toggle: show a faint ghost of the enrolled signature.</summary>
        public static bool ShowGhost;

        private static readonly Color Drawing = new Color(0.55f, 0.85f, 1f, 1f);
        private static readonly Color Passed = new Color(0.35f, 1f, 0.5f, 1f);
        private static readonly Color Failed = new Color(1f, 0.33f, 0.30f, 1f);

        private LineRenderer _trail, _ghost;
        private Material _trailMat, _ghostMat;
        private readonly List<Vector3> _points = new();

        private MFACoordinator _mfa;
        private GestureCaptureDriver _driver;
        private GestureAuthenticator _gesture;
        private bool _subscribed;
        private int _lastLiveCount;
        private float _fadeStart = -1f;
        private Color _endColor = Drawing;
        private float _nextSearch;
        private int _ghostSampleCount = -1;

        private void Update()
        {
            if (_trail == null) Build();

            if ((_mfa == null || _gesture == null) && Time.unscaledTime >= _nextSearch)
            {
                _nextSearch = Time.unscaledTime + 1f;
                _mfa = FindAnyObjectByType<MFACoordinator>();
                _driver = FindAnyObjectByType<GestureCaptureDriver>();
                _gesture = _mfa != null ? _mfa.Gesture : null;
                if (_gesture != null && !_subscribed)
                {
                    _gesture.OnGestureScored += HandleScored;
                    _subscribed = true;
                }
            }

            if (_mfa == null || _gesture == null) { Hide(); return; }

            bool inGestureStage = _mfa.CurrentStage == MFAStage.Gesture;
            if (!inGestureStage)
            {
                Hide();
                _lastLiveCount = 0;
                return;
            }

            UpdateGhost();

            // IsCapturing stays true for the whole stage, so the trail follows the live
            // point count instead: it only grows while the trigger is held and the hand
            // is actually moving, and it drops back to zero when a capture restarts.
            int live = _gesture.LivePointCount;
            bool growing = live > _lastLiveCount;

            if (live < _lastLiveCount && _points.Count > 0 && _fadeStart < 0f)
            {
                // Capture restarted without a score (an enrolment stroke): fade it out.
                _endColor = Drawing;
                _fadeStart = Time.unscaledTime;
            }
            _lastLiveCount = live;

            Transform hand = DrawingHand();
            if (growing && hand != null)
            {
                if (_fadeStart >= 0f) ResetTrail();
                AddPoint(hand.position);
                SetColor(Drawing);
            }

            if (_fadeStart >= 0f)
            {
                float k = 1f - Mathf.Clamp01((Time.unscaledTime - _fadeStart) / Mathf.Max(0.1f, fadeSeconds));
                var c = _endColor; c.a = k;
                SetColor(c);
                if (k <= 0f) { _points.Clear(); _trail.positionCount = 0; _fadeStart = -1f; }
            }
        }

        private void OnDestroy()
        {
            if (_gesture != null && _subscribed) _gesture.OnGestureScored -= HandleScored;
            if (_trailMat != null) Destroy(_trailMat);
            if (_ghostMat != null) Destroy(_ghostMat);
        }

        private void Build()
        {
            _trail = MakeLine("GestureTrail", Drawing, lineWidth, out _trailMat);
            _ghost = MakeLine("GestureGhost", new Color(1f, 1f, 1f, 0.22f), lineWidth * 0.6f, out _ghostMat);
            _ghost.gameObject.SetActive(false);
        }

        private LineRenderer MakeLine(string name, Color color, float width, out Material mat)
        {
            var go = new GameObject(name);
            go.transform.SetParent(transform, false);
            var lr = go.AddComponent<LineRenderer>();
            lr.useWorldSpace = true;
            lr.widthMultiplier = width;
            lr.numCapVertices = 2;
            lr.numCornerVertices = 2;
            lr.positionCount = 0;
            lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            lr.receiveShadows = false;
            mat = SpatialUiFactory.UnlitMaterial(color);
            lr.sharedMaterial = mat;
            return lr;
        }

        /// <summary>Whichever hand the capture driver is bound to.</summary>
        private Transform DrawingHand()
        {
            bool rightHand = _driver == null || _driver.useRightHand;
            return rightHand ? XrRigLocator.RightController : XrRigLocator.LeftController;
        }

        private void AddPoint(Vector3 p)
        {
            if (_points.Count > 0 && Vector3.Distance(_points[_points.Count - 1], p) < minPointSpacing) return;
            _points.Add(p);
            if (_points.Count > 512) _points.RemoveAt(0);
            _trail.positionCount = _points.Count;
            for (int i = 0; i < _points.Count; i++) _trail.SetPosition(i, _points[i]);
        }

        private void ResetTrail()
        {
            _points.Clear();
            if (_trail != null) _trail.positionCount = 0;
            _fadeStart = -1f;
        }

        private void HandleScored(float score)
        {
            bool passed = _gesture != null && _gesture.PassesThreshold(score);
            _endColor = passed ? Passed : Failed;
            SetColor(_endColor);
            _fadeStart = Time.unscaledTime;
        }

        private void SetColor(Color c)
        {
            if (_trailMat == null) return;
            _trailMat.color = c;
            _trail.startColor = _trail.endColor = c;
        }

        private void Hide()
        {
            if (_trail != null && _trail.positionCount != 0) { _trail.positionCount = 0; _points.Clear(); }
            if (_ghost != null && _ghost.gameObject.activeSelf) _ghost.gameObject.SetActive(false);
            _ghostSampleCount = -1;
        }

        /// <summary>
        /// The enrolled stroke, drawn where it was recorded. Enrolment points are in
        /// tracking space (the devicePosition binding), so they are mapped back through
        /// the XR Origin - close enough for a visual aid.
        /// </summary>
        private void UpdateGhost()
        {
            if (_ghost == null) return;
            if (!ShowGhost || _gesture == null || !_gesture.IsEnrolled)
            {
                if (_ghost.gameObject.activeSelf) _ghost.gameObject.SetActive(false);
                _ghostSampleCount = -1;
                return;
            }

            if (_ghostSampleCount == _gesture.EnrolledSamples.Count && _ghost.gameObject.activeSelf) return;
            _ghostSampleCount = _gesture.EnrolledSamples.Count;

            var stroke = _gesture.EnrolledSamples[0];
            Transform space = XrRigLocator.Origin;
            _ghost.positionCount = stroke.Count;
            for (int i = 0; i < stroke.Count; i++)
                _ghost.SetPosition(i, space != null ? space.TransformPoint(stroke[i]) : stroke[i]);
            _ghost.gameObject.SetActive(true);
        }
    }
}
