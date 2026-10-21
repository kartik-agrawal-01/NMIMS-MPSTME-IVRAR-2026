using UnityEngine;

namespace BoardroomXR.Auth
{
    /// <summary>
    /// Central, designer-tunable configuration for the simulated multi-factor
    /// authentication (MFA) pipeline. Create one instance via
    /// Assets > Create > BoardroomXR > MFA Settings and drag it onto
    /// MFACoordinator so every factor reads from the same source of truth.
    /// </summary>
    [CreateAssetMenu(fileName = "MFASettings", menuName = "BoardroomXR/MFA Settings")]
    public class MFASettings : ScriptableObject
    {
        [Header("Factor 1 - Knowledge (PIN)")]
        [Tooltip("Length of the numeric PIN the avatar must enter on the virtual keypad.")]
        public int pinLength = 4;
        [Tooltip("Max attempts before the session is locked and flagged as suspicious.")]
        public int maxPinAttempts = 3;

        [Header("Factor 2 - Behavioural (Gesture Signature)")]
        [Tooltip("How closely (0-1, 1 = perfect) a live gesture must match the stored template. " +
                 "A genuine re-draw of the same shape typically lands around 0.75-0.90; raise this " +
                 "to make the factor stricter, lower it if honest attempts are being rejected.")]
        [Range(0.5f, 1f)] public float gestureMatchThreshold = 0.75f;
        [Tooltip("Number of sample points captured along the controller path.")]
        public int gestureSampleResolution = 64;
        [Tooltip("Minimum raw samples before a stroke is considered a real attempt rather than a stray click.")]
        public int gestureMinPoints = 12;

        [Header("Factor 3 - Possession (Rotating Token / TOTP-style)")]
        [Tooltip("Seconds each simulated one-time code stays valid.")]
        public int otpValiditySeconds = 30;
        [Tooltip("Digits shown on the virtual token device.")]
        public int otpDigits = 6;

        [Header("Demo provisioning")]
        [Tooltip("Shared TOTP seed used by the virtual token and, server-side, to derive the expected code. " +
                 "Stands in for per-user enrolment; replace with a real per-user secret for anything beyond a demo.")]
        public string demoOtpSeed = "BOARDROOM-DEMO-SEED";
        [Tooltip("PIN provisioned into the server-side credential store at host startup. " +
                 "Any other PIN genuinely fails the check.")]
        public string demoEnrolledPin = "1234";

        [Header("Session & Anti-Spoofing")]
        [Tooltip("Seconds allowed to complete all factors before the session auto-fails.")]
        public float sessionTimeoutSeconds = 90f;
        [Tooltip("Standard deviations from baseline movement before SpoofDetector flags an avatar.")]
        public float behaviouralAnomalyStdDevThreshold = 2.5f;
        [Tooltip("How often BoardroomAccessGate re-checks verification on active avatars, as a safety net. " +
                 "Visibility is also applied immediately whenever IsVerified changes.")]
        public float continuousRecheckIntervalSeconds = 20f;
    }
}
