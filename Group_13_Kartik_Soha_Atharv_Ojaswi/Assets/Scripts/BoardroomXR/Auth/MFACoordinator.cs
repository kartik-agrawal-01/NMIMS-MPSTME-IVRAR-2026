using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using BoardroomXR.Identity;
using BoardroomXR.Logging;
using BoardroomXR.Network;

namespace BoardroomXR.Auth
{
    public enum MFAStage { Idle, Pin, Gesture, Otp, Passed, Failed, TimedOut }

    /// <summary>
    /// Client-side orchestrator: walks the local player through the three
    /// factors in order, enforces the overall session timeout, and sends the
    /// results to the server via NetworkAuthRelay for authoritative checking.
    /// Attach one instance to the local player rig.
    ///
    /// Every stage transition here is driven by a real result coming back from
    /// the server - no stage is entered or passed by assumption.
    /// </summary>
    [RequireComponent(typeof(PinAuthenticator))]
    [RequireComponent(typeof(GestureAuthenticator))]
    [RequireComponent(typeof(OTPAuthenticator))]
    public class MFACoordinator : MonoBehaviour
    {
        public MFASettings settings;
        public NetworkAuthRelay networkRelay;
        public SessionLogger sessionLogger;

        [Tooltip("Start the MFA session as soon as play begins, instead of waiting for the player " +
                 "to walk through the antechamber trigger. Handy when demoing seated or in-editor.")]
        public bool autoStartOnPlay = true;

        [Tooltip("How many strokes a fresh signature enrolment collects. Strokes 2..N must " +
                 "match stroke 1, so one odd re-draw cannot define the user's template.")]
        [Range(1, 5)] public int gestureEnrolmentSamples = GestureAuthenticator.DefaultEnrolmentSamples;

        [Tooltip("DEBUG ONLY. If the user has no enrolled gesture, fall back to a synthetic " +
                 "straight-line template instead of running enrolment. This makes factor 2 pass " +
                 "for almost any straight-ish stroke, so it must stay OFF for a real demo or any " +
                 "measurement - it is a wiring aid, not an authentication result.")]
        public bool useTestGestureTemplateFallback;

        public event Action<MFAStage> OnStageChanged;

        /// <summary>Human-readable status for on-screen prompts. Raised alongside stage changes.</summary>
        public event Action<string> OnStatusMessage;

        private PinAuthenticator _pin;
        private GestureAuthenticator _gesture;
        private OTPAuthenticator _otp;
        private AvatarIdentity _identity;
        private Coroutine _timeoutRoutine;
        private float _sessionStartTime;
        private bool _handlersBound;

        public MFAStage CurrentStage { get; private set; } = MFAStage.Idle;
        public GestureAuthenticator Gesture => _gesture;
        public int PinLength => settings != null ? settings.pinLength : 4;
        public int OtpDigits => settings != null ? settings.otpDigits : 6;

        private void Awake()
        {
            _pin = GetComponent<PinAuthenticator>();
            _gesture = GetComponent<GestureAuthenticator>();
            _otp = GetComponent<OTPAuthenticator>();
            _identity = GetComponent<AvatarIdentity>();

            if (networkRelay == null) networkRelay = GetComponent<NetworkAuthRelay>();
            if (sessionLogger == null) sessionLogger = SessionLogger.Active;

            // Bind exactly once for the lifetime of the component. The previous version
            // subscribed inside BeginSession, so every restart stacked another handler
            // and each PIN submission fired N times.
            BindHandlers();
        }

        private void Start()
        {
            if (autoStartOnPlay && CurrentStage == MFAStage.Idle)
            {
                BeginSession();
            }
        }

        private void BindHandlers()
        {
            if (_handlersBound) return;
            _handlersBound = true;

            _pin.OnPinSubmitted += HandlePinSubmitted;
            _pin.OnAttemptsExhausted += HandleAttemptsExhausted;
            _gesture.OnGestureScored += HandleGestureScored;
        }

        private void OnDestroy()
        {
            if (!_handlersBound) return;
            _handlersBound = false;

            _pin.OnPinSubmitted -= HandlePinSubmitted;
            _pin.OnAttemptsExhausted -= HandleAttemptsExhausted;
            _gesture.OnGestureScored -= HandleGestureScored;
        }

        // ---------------- session lifecycle ----------------

        public void BeginSession()
        {
            if (CurrentStage == MFAStage.Pin || CurrentStage == MFAStage.Gesture || CurrentStage == MFAStage.Otp)
            {
                Log("BeginSession ignored: a session is already in progress.");
                return;
            }

            Debug.Log("[MFACoordinator] BeginSession() called - initializing PIN factor.", this);

            _sessionStartTime = Time.realtimeSinceStartup;
            Log("MFA session started.");

            _pin.Initialize(settings);
            InitializeOtp();

            if (_timeoutRoutine != null) StopCoroutine(_timeoutRoutine);
            _timeoutRoutine = StartCoroutine(TimeoutWatch());

            SetStage(MFAStage.Pin, $"Enter your {PinLength}-digit PIN on the keypad.");
        }

        /// <summary>
        /// Drops the user back to factor 1. Called when the server revokes
        /// verification mid-session (see SpoofDetector), so a revoke has a real
        /// consequence instead of silently flipping a bool.
        /// </summary>
        public void RestartSession(string reason)
        {
            Log($"RE-AUTHENTICATION REQUIRED: {reason}");

            if (_timeoutRoutine != null) { StopCoroutine(_timeoutRoutine); _timeoutRoutine = null; }
            _gesture.CancelCapture();
            _gesture.ResetEnrolmentProgress();
            _pin.Initialize(settings);
            CurrentStage = MFAStage.Idle;

            BeginSession();
            OnStatusMessage?.Invoke($"Access revoked: {reason}. Re-authenticate to continue.");
        }

        private void InitializeOtp()
        {
            if (settings == null) return;
            // The token starts rotating a real TOTP the moment the session opens,
            // so the code the user reads is the code the server independently derives.
            _otp.Initialize(settings, settings.demoOtpSeed);
        }

        // ---------------- factor 1: PIN ----------------

        private void HandlePinSubmitted(string pin)
        {
            if (CurrentStage != MFAStage.Pin) return;

            if (networkRelay == null || !networkRelay.CanReachServer)
            {
                Fail("Not connected: cannot verify PIN with the server.");
                return;
            }

            networkRelay.RequestVerifyPin(pin, ok =>
            {
                if (CurrentStage != MFAStage.Pin) return;

                if (ok)
                {
                    Log("Factor 1 (PIN) passed.");
                    EnterGestureStage();
                }
                else
                {
                    Log("Factor 1 (PIN) failed.");
                    _pin.NotifyFailed();
                    OnStatusMessage?.Invoke("Incorrect PIN. Try again.");
                }
            });
        }

        private void HandleAttemptsExhausted() => Fail("PIN attempts exhausted.");

        // ---------------- factor 2: gesture ----------------

        private void EnterGestureStage()
        {
            string userId = _identity != null ? _identity.ClaimedUserIdString : "LOCAL";
            var samples = GestureTemplateStore.LoadSamples(userId);

            // Factor 2 used to never be initialised here - GestureAuthenticator._settings
            // stayed null and NullRef'd the moment a captured gesture was scored.
            bool usingTestTemplate = false;
            if (samples == null && useTestGestureTemplateFallback)
            {
                samples = new List<List<Vector3>> { BuildTestGestureTemplate() };
                usingTestTemplate = true;
            }

            _gesture.RequiredEnrolmentSamples = gestureEnrolmentSamples;
            _gesture.Initialize(settings, samples);
            var template = samples != null && samples.Count > 0 ? samples[0] : null;

            if (usingTestTemplate)
            {
                Log($"WARNING: factor 2 for {userId} is using the synthetic TEST gesture template. " +
                    "Almost any straight-ish stroke will pass. This is a wiring aid, not an " +
                    "authentication result - turn useTestGestureTemplateFallback off before " +
                    "demoing or measuring anything.");
                SetStage(MFAStage.Gesture, "DEBUG: test gesture template active. Draw any stroke.");
            }
            else if (template == null)
            {
                Log($"No enrolled gesture for {userId}. Enrolling from {gestureEnrolmentSamples} strokes.");
                SetStage(MFAStage.Gesture,
                    $"Draw your signature ({1}/{gestureEnrolmentSamples}) - hold the trigger while drawing.");
            }
            else
            {
                Log($"Loaded enrolled gesture template for {userId} ({samples.Count} stroke(s), {template.Count} points in the first).");
                SetStage(MFAStage.Gesture, "Hold the trigger and draw your signature.");
            }

            _gesture.BeginCapture();
        }

        /// <summary>
        /// TEST TEMPLATE - not an enrolment. Stands in for a gesture signature so the
        /// factor-2 wiring can be exercised without enrolling first. A short straight-line
        /// path of 5 points; gesture scoring normalises scale and position, so any
        /// similarly-shaped live capture scores near 1.0.
        ///
        /// Only reachable when <see cref="useTestGestureTemplateFallback"/> is explicitly
        /// enabled. The real path is <see cref="GestureTemplateStore"/>, which compares
        /// against a stroke the user actually drew.
        /// </summary>
        private static List<Vector3> BuildTestGestureTemplate()
        {
            return new List<Vector3>
            {
                new Vector3(0.00f, 0f, 0f),
                new Vector3(0.25f, 0f, 0f),
                new Vector3(0.50f, 0f, 0f),
                new Vector3(0.75f, 0f, 0f),
                new Vector3(1.00f, 0f, 0f),
            };
        }

        /// <summary>Called by GestureCaptureDriver once all enrolment strokes have been stored.</summary>
        public void NotifyGestureEnrolled(int pointCount)
        {
            int strokes = _gesture != null ? _gesture.EnrolledSamples.Count : 1;
            Log($"Gesture ENROLLED from {strokes} live stroke(s) ({pointCount} points in the last). Draw it again to authenticate.");
            BioEventLog.Emit("gesture_enrol", CurrentUserId, $"strokes={strokes} points={pointCount}");
            OnStatusMessage?.Invoke($"Signature enrolled from {strokes} strokes. Now draw it once more to log in.");
            _gesture.BeginCapture();
        }

        /// <summary>
        /// Called by GestureCaptureDriver after each accepted enrolment stroke, so the
        /// HUD can say which stroke the user is on.
        /// </summary>
        public void NotifyGestureEnrolmentProgress(int captured, int required)
        {
            Log($"Gesture enrolment stroke {captured}/{required} accepted.");
            OnStatusMessage?.Invoke($"Draw your signature ({Mathf.Min(captured + 1, required)}/{required}) - same shape again.");
        }

        /// <summary>Called when an enrolment stroke did not match the first one and enrolment restarted.</summary>
        public void NotifyGestureEnrolmentRestarted(float consistency)
        {
            Log($"Gesture enrolment restarted: stroke matched the first one only {consistency:F2} " +
                $"(need >={GestureAuthenticator.EnrolmentConsistencyThreshold:F2}).");
            BioEventLog.Emit("gesture_enrol_restart", CurrentUserId, $"consistency={consistency:F2}");
            OnStatusMessage?.Invoke($"That was a different shape ({consistency:F2}). Starting again - " +
                                    $"draw your signature (1/{gestureEnrolmentSamples}).");
        }

        /// <summary>Identity this rig currently claims, for log lines.</summary>
        public string CurrentUserId => _identity != null ? _identity.ClaimedUserIdString : "LOCAL";

        private void HandleGestureScored(float score)
        {
            if (CurrentStage != MFAStage.Gesture) return;

            if (!_gesture.PassesThreshold(score))
            {
                float required = settings != null ? settings.gestureMatchThreshold : 1f;
                Log($"Factor 2 (Gesture) failed locally: score={score:F3}, required>={required:F3}.");
                BioEventLog.Emit("gesture_fail", CurrentUserId, $"score={score:F3} required={required:F3}");
                OnStatusMessage?.Invoke($"Gesture did not match ({score:F2} of {required:F2}). Draw again.");
                _gesture.BeginCapture(); // let the user retry within the session timeout
                return;
            }

            if (networkRelay == null || !networkRelay.CanReachServer)
            {
                Fail("Not connected: cannot verify gesture with the server.");
                return;
            }

            networkRelay.RequestVerifyGesture(score, ok =>
            {
                if (CurrentStage != MFAStage.Gesture) return;

                if (ok)
                {
                    Log($"Factor 2 (Gesture) passed, score={score:F3}.");
                    BioEventLog.Emit("gesture_pass", CurrentUserId,
                        $"score={score:F3} matchedSample={_gesture.LastMatchedSampleIndex}");
                    SetStage(MFAStage.Otp,
                        $"Read the {OtpDigits}-digit code on your token and enter it on the keypad.");
                }
                else
                {
                    Log($"Factor 2 (Gesture) rejected by server, score={score:F3}.");
                    OnStatusMessage?.Invoke("Server rejected the gesture. Draw again.");
                    _gesture.BeginCapture();
                }
            });
        }

        // ---------------- factor 3: OTP ----------------

        /// <summary>Call when the user submits the code shown on their virtual token.</summary>
        public void SubmitOtp(string code)
        {
            if (CurrentStage != MFAStage.Otp)
            {
                Log($"SubmitOtp ignored: current stage is {CurrentStage}, not Otp.");
                return;
            }

            if (networkRelay == null || !networkRelay.CanReachServer)
            {
                Fail("Not connected: cannot verify one-time code with the server.");
                return;
            }

            Log($"Factor 3 (OTP) submitted: {code.Length} digits.");
            networkRelay.RequestVerifyOtp(code, ok =>
            {
                if (CurrentStage != MFAStage.Otp) return;

                if (ok) Pass();
                else
                {
                    Log("Factor 3 (OTP) failed: code incorrect or expired.");
                    OnStatusMessage?.Invoke("One-time code incorrect or expired. Try the current code.");
                }
            });
        }

        // ---------------- outcomes ----------------

        private void Pass()
        {
            float elapsed = Time.realtimeSinceStartup - _sessionStartTime;
            SetStage(MFAStage.Passed, "Verified. Boardroom content unlocked.");
            Log($"MFA PASSED in {elapsed:F1}s.");
            BioEventLog.Emit("mfa_pass", CurrentUserId, $"all three factors in {elapsed:F1}s");
            if (_timeoutRoutine != null) { StopCoroutine(_timeoutRoutine); _timeoutRoutine = null; }
        }

        private void Fail(string reason)
        {
            SetStage(MFAStage.Failed, $"Authentication failed: {reason}");
            Log($"MFA FAILED: {reason}");
            _gesture.CancelCapture();
            if (_timeoutRoutine != null) { StopCoroutine(_timeoutRoutine); _timeoutRoutine = null; }
        }

        private IEnumerator TimeoutWatch()
        {
            float limit = settings != null ? settings.sessionTimeoutSeconds : 90f;
            yield return new WaitForSeconds(limit);

            if (CurrentStage != MFAStage.Passed && CurrentStage != MFAStage.Failed)
            {
                SetStage(MFAStage.TimedOut, "Session timed out. Start again.");
                Log($"MFA session TIMED OUT after {limit:F0}s at stage {CurrentStage}.");
                _gesture.CancelCapture();
            }
            _timeoutRoutine = null;
        }

        private void SetStage(MFAStage stage, string status = null)
        {
            CurrentStage = stage;
            OnStageChanged?.Invoke(stage);
            if (!string.IsNullOrEmpty(status)) OnStatusMessage?.Invoke(status);
        }

        private void Log(string message)
        {
            if (sessionLogger != null) sessionLogger.Log(message);
            else SessionLogger.LogGlobal(message);
        }
    }
}
