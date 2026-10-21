using UnityEngine;
using BoardroomXR.Auth;

namespace BoardroomXR.UI
{
    /// <summary>
    /// Drives a world-space label on the virtual "token" prop with the rotating
    /// one-time code from an OTPAuthenticator.
    ///
    /// MFACoordinator initialises the authenticator when a session opens. This
    /// component keeps an optional bootstrap for the idle case, so the prop
    /// shows a live code before anyone starts authenticating - and it takes the
    /// seed from the same MFASettings the server provisions from, so the code
    /// on screen is the code the server will independently derive.
    /// </summary>
    public class OTPTokenDisplay : MonoBehaviour
    {
        public OTPAuthenticator otpAuthenticator;
        public TextMesh label;

        [Header("Bootstrap (optional, for the idle prop)")]
        [Tooltip("Initialise the authenticator if no MFA session has done so yet.")]
        public bool autoInitialize = true;
        public MFASettings settings;

        [Tooltip("Append the seconds remaining before the code rotates.")]
        public bool showCountdown = true;

        private string _currentCode = "";

        private void OnEnable()
        {
            if (otpAuthenticator != null)
                otpAuthenticator.OnCodeRotated += HandleCodeRotated;
        }

        private void Start()
        {
            if (autoInitialize && otpAuthenticator != null && settings != null && !otpAuthenticator.IsInitialized)
            {
                // Same seed the server provisions from - never a separate literal.
                otpAuthenticator.Initialize(settings, settings.demoOtpSeed);
            }
        }

        private void OnDisable()
        {
            if (otpAuthenticator != null)
                otpAuthenticator.OnCodeRotated -= HandleCodeRotated;
        }

        private void Update()
        {
            if (!showCountdown || label == null || otpAuthenticator == null) return;
            if (!otpAuthenticator.IsInitialized || string.IsNullOrEmpty(_currentCode)) return;

            int remaining = otpAuthenticator.SecondsRemaining;
            if (remaining >= 0) label.text = $"{_currentCode}\n{remaining}s";
        }

        private void HandleCodeRotated(string code)
        {
            _currentCode = code;
            if (label != null) label.text = showCountdown ? $"{code}\n{otpAuthenticator.SecondsRemaining}s" : code;
        }
    }
}
