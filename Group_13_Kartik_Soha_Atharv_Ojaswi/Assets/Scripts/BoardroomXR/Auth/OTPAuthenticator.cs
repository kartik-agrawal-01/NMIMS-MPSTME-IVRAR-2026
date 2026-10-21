using System;
using UnityEngine;

namespace BoardroomXR.Auth
{
    /// <summary>
    /// Factor 3 - "something you have". A TOTP-style rotating code shown on a
    /// virtual token object in the scene (e.g. a badge or phone prop). The
    /// server independently derives the expected code for the same time step
    /// through the same <see cref="TotpGenerator"/>, so the check is a real
    /// comparison: a wrong or stale code genuinely fails.
    ///
    /// NOTE: this is a classroom simulation of the TOTP concept (RFC 6238)
    /// with a shared demo seed, not a per-user provisioned secret.
    /// </summary>
    public class OTPAuthenticator : MonoBehaviour
    {
        public event Action<string> OnCodeRotated;

        private MFASettings _settings;
        private string _sharedSeed;
        private long _lastStep = long.MinValue;

        public bool IsInitialized => _settings != null && !string.IsNullOrEmpty(_sharedSeed);

        /// <summary>Seconds until the displayed code rotates. -1 if not initialised.</summary>
        public int SecondsRemaining =>
            IsInitialized ? TotpGenerator.SecondsRemaining(_settings.otpValiditySeconds) : -1;

        public void Initialize(MFASettings settings, string sharedSeed)
        {
            _settings = settings;
            _sharedSeed = sharedSeed;
            _lastStep = long.MinValue; // force an immediate rotation event so the token shows a code at once
        }

        private void Update()
        {
            if (!IsInitialized) return;

            long step = TotpGenerator.CurrentTimeStep(_settings.otpValiditySeconds);
            if (step == _lastStep) return;

            _lastStep = step;
            OnCodeRotated?.Invoke(TotpGenerator.Generate(_sharedSeed, step, _settings.otpDigits));
        }

        /// <summary>The code valid right now, or an empty string if not initialised.</summary>
        public string GetCurrentCode()
        {
            if (!IsInitialized) return string.Empty;
            long step = TotpGenerator.CurrentTimeStep(_settings.otpValiditySeconds);
            return TotpGenerator.Generate(_sharedSeed, step, _settings.otpDigits);
        }
    }
}
