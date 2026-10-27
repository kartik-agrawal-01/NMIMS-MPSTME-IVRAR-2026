using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using BoardroomXR.Identity;
using BoardroomXR.Logging;

namespace BoardroomXR.Auth
{
    /// <summary>
    /// Feeds real controller input into <see cref="GestureAuthenticator"/>.
    /// Nothing used to call RecordPoint or EndCaptureAndScore, so the MFA flow
    /// stalled at the gesture stage until the session timed out; this is the
    /// component that drives it.
    ///
    /// Hold the trigger and move the controller to draw; release to submit the
    /// stroke. If the user has no enrolled template yet, the first stroke is
    /// stored as their enrolment and they are asked to draw it again - so the
    /// comparison is always against something they genuinely drew.
    ///
    /// Bindings are created in code against the generic XR controller layout,
    /// so this needs no Inspector wiring to work on a Quest.
    /// </summary>
    public class GestureCaptureDriver : MonoBehaviour
    {
        [Tooltip("Left empty, these are found on this GameObject.")]
        public MFACoordinator coordinator;
        public GestureAuthenticator gestureAuthenticator;
        public SessionLogger sessionLogger;

        [Header("Input")]
        [Tooltip("Which hand draws the signature.")]
        public bool useRightHand = true;

        [Tooltip("Fallback pose source if the XR position binding reports nothing (e.g. in-editor without a headset).")]
        public Transform handTransformFallback;

        [Tooltip("Space the stroke is recorded in. Left empty, the XR Origin / main camera parent is used. " +
                 "Recording in rig space means walking around does not corrupt the shape.")]
        public Transform trackingSpace;

        [Header("Stroke limits")]
        [Tooltip("Shortest accepted stroke, in seconds. Guards against an accidental trigger tap.")]
        public float minStrokeSeconds = 0.25f;

        private InputAction _drawTrigger;
        private InputAction _drawGrip;
        private InputAction _drawKeyboard;
        private InputAction _handPosition;
        private bool _heldLatched;
        private bool _drawing;
        private float _strokeStartTime;
        private AvatarIdentity _identity;

        private void Awake()
        {
            if (coordinator == null) coordinator = GetComponent<MFACoordinator>();
            if (gestureAuthenticator == null) gestureAuthenticator = GetComponent<GestureAuthenticator>();
            if (sessionLogger == null) sessionLogger = SessionLogger.Active;
            _identity = GetComponent<AvatarIdentity>();

            string hand = useRightHand ? "RightHand" : "LeftHand";

            // One control per action, read as an analogue value. A single Button action
            // carrying triggerPressed + trigger + gripPressed made the Input System
            // disambiguate between three controls - two of them the same physical
            // trigger - and flip the action's phase while the finger never moved.
            // IsPressed() dropped mid-stroke and stayed true across real releases, so
            // every stroke died at 0.1-0.2s and never cleared minStrokeSeconds.
            _drawTrigger = new InputAction("MFA/GestureDrawTrigger", InputActionType.Value,
                expectedControlType: "Axis");
            _drawTrigger.AddBinding($"<XRController>{{{hand}}}/trigger");

            _drawGrip = new InputAction("MFA/GestureDrawGrip", InputActionType.Value,
                expectedControlType: "Axis");
            _drawGrip.AddBinding($"<XRController>{{{hand}}}/grip");

            // Editor convenience so the flow can be exercised without a headset.
            _drawKeyboard = new InputAction("MFA/GestureDrawKeyboard", InputActionType.Button);
            _drawKeyboard.AddBinding("<Keyboard>/space");

            _handPosition = new InputAction("MFA/GesturePosition", InputActionType.Value, expectedControlType: "Vector3");
            _handPosition.AddBinding($"<XRController>{{{hand}}}/devicePosition");
            _handPosition.AddBinding($"<XRController>{{{hand}}}/pointerPosition");
        }

        private void OnEnable()
        {
            _drawTrigger?.Enable();
            _drawGrip?.Enable();
            _drawKeyboard?.Enable();
            _handPosition?.Enable();
        }

        private void OnDisable()
        {
            _drawTrigger?.Disable();
            _drawGrip?.Disable();
            _drawKeyboard?.Disable();
            _handPosition?.Disable();
            if (_drawing) AbortStroke("component disabled");
        }

        private void OnDestroy()
        {
            _drawTrigger?.Dispose();
            _drawGrip?.Dispose();
            _drawKeyboard?.Dispose();
            _handPosition?.Dispose();
        }

        private void Update()
        {
            if (coordinator == null || gestureAuthenticator == null) return;

            // Only capture during the gesture factor; a stroke drawn at any other
            // time is not an authentication attempt.
            if (coordinator.CurrentStage != MFAStage.Gesture)
            {
                if (_drawing) AbortStroke($"stage changed to {coordinator.CurrentStage}");
                return;
            }

            bool held = ReadHeld();

            if (held && !_drawing) BeginStroke();
            else if (held && _drawing) gestureAuthenticator.RecordPoint(ReadHandPosition());
            else if (!held && _drawing) EndStroke();
        }

        /// <summary>
        /// Analogue held-state with hysteresis: presses at 0.6, releases at 0.35.
        /// A single threshold on a trigger the user is squeezing chatters, and each
        /// chatter ended the stroke.
        /// </summary>
        private bool ReadHeld()
        {
            float value = 0f;
            if (_drawTrigger != null) value = Mathf.Max(value, _drawTrigger.ReadValue<float>());
            if (_drawGrip != null) value = Mathf.Max(value, _drawGrip.ReadValue<float>());
            if (_drawKeyboard != null && _drawKeyboard.IsPressed()) value = 1f;

            _heldLatched = _heldLatched ? value > 0.35f : value >= 0.6f;
            return _heldLatched;
        }

        private void BeginStroke()
        {
            _drawing = true;
            _strokeStartTime = Time.time;
            gestureAuthenticator.BeginCapture();
            gestureAuthenticator.RecordPoint(ReadHandPosition());
            Log("Gesture stroke started (trigger held).");
        }

        private void EndStroke()
        {
            _drawing = false;

            float duration = Time.time - _strokeStartTime;
            int points = gestureAuthenticator.LivePointCount;
            int minPoints = coordinator.settings != null ? coordinator.settings.gestureMinPoints : 12;

            if (duration < minStrokeSeconds || points < minPoints)
            {
                Log($"Gesture stroke discarded: {duration:F2}s, {points} points " +
                    $"(need >={minStrokeSeconds:F2}s and >={minPoints} points). Draw again.");
                gestureAuthenticator.BeginCapture();
                return;
            }

            string userId = _identity != null ? _identity.ClaimedUserIdString : "LOCAL";

            if (!gestureAuthenticator.IsEnrolled)
            {
                HandleEnrolmentStroke(userId, duration, points);
                return;
            }

            Log($"Gesture stroke submitted: {points} points over {duration:F2}s. Scoring against enrolled template.");
            gestureAuthenticator.EndCaptureAndScore();
        }

        /// <summary>
        /// Multi-sample enrolment: collect N strokes of the same shape. Strokes after
        /// the first are checked against it, so an inconsistent enrolment is reported
        /// and restarted instead of being silently stored.
        /// </summary>
        private void HandleEnrolmentStroke(string userId, float duration, int points)
        {
            int required = Mathf.Max(1, gestureAuthenticator.RequiredEnrolmentSamples);
            var result = gestureAuthenticator.SubmitEnrolmentStroke();

            switch (result)
            {
                case GestureEnrolmentResult.TooShort:
                    Log("Enrolment failed: stroke too short. Draw again.");
                    gestureAuthenticator.BeginCapture();
                    return;

                case GestureEnrolmentResult.Rejected:
                    Log($"Enrolment stroke rejected for {userId}: only {gestureAuthenticator.LastEnrolmentConsistency:F2} " +
                        $"similar to the first stroke. Enrolment restarted.");
                    coordinator.NotifyGestureEnrolmentRestarted(gestureAuthenticator.LastEnrolmentConsistency);
                    gestureAuthenticator.BeginCapture();
                    return;

                case GestureEnrolmentResult.NeedMore:
                    Log($"Enrolment stroke {gestureAuthenticator.PendingSampleCount}/{required} accepted for {userId}: " +
                        $"{points} points, {duration:F2}s" +
                        (float.IsNaN(gestureAuthenticator.LastEnrolmentConsistency)
                            ? "." : $", match to first stroke {gestureAuthenticator.LastEnrolmentConsistency:F2}."));
                    coordinator.NotifyGestureEnrolmentProgress(gestureAuthenticator.PendingSampleCount, required);
                    gestureAuthenticator.BeginCapture();
                    return;

                default: // Complete
                    var samples = new List<List<Vector3>>(gestureAuthenticator.EnrolledSamples);
                    bool saved = GestureTemplateStore.SaveSamples(userId, samples);
                    Log($"Gesture enrolment captured for {userId}: {samples.Count} strokes " +
                        $"({string.Join("/", samples.ConvertAll(s => s.Count.ToString()))} points), persisted={saved}.");
                    coordinator.NotifyGestureEnrolled(points);
                    return;
            }
        }

        private void AbortStroke(string reason)
        {
            _drawing = false;
            gestureAuthenticator.CancelCapture();
            Log($"Gesture stroke aborted: {reason}.");
        }

        /// <summary>
        /// Controller position in rig space. Prefers the XR device binding; falls
        /// back to an assigned transform when no XR device is reporting.
        /// </summary>
        private Vector3 ReadHandPosition()
        {
            if (_handPosition != null && _handPosition.activeControl != null)
            {
                // devicePosition is already expressed in tracking space.
                return _handPosition.ReadValue<Vector3>();
            }

            if (handTransformFallback != null)
            {
                Transform space = ResolveTrackingSpace();
                return space != null
                    ? space.InverseTransformPoint(handTransformFallback.position)
                    : handTransformFallback.position;
            }

            return Vector3.zero;
        }

        private Transform ResolveTrackingSpace()
        {
            if (trackingSpace != null) return trackingSpace;
            if (Camera.main != null) trackingSpace = Camera.main.transform.parent;
            return trackingSpace;
        }

        private void Log(string message)
        {
            if (sessionLogger != null) sessionLogger.Log(message);
            else SessionLogger.LogGlobal(message);
        }
    }
}
