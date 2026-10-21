using System;
using System.Text;
using UnityEngine;

namespace BoardroomXR.Auth
{
    /// <summary>
    /// Factor 1 - "something you know". Captures digits tapped on a
    /// world-space VR keypad (see UI/PinKeypadUI.cs) and hands the completed
    /// PIN to MFACoordinator, which forwards it to the server for the actual
    /// check (the correct PIN is never stored or compared client-side).
    /// </summary>
    public class PinAuthenticator : MonoBehaviour
    {
        public event Action<string> OnPinSubmitted;
        public event Action OnAttemptsExhausted;

        private readonly StringBuilder _buffer = new();
        private int _attempts;
        private MFASettings _settings;

        /// <summary>Attempts used so far in the current session.</summary>
        public int Attempts => _attempts;

        /// <summary>Attempts left before the session is failed. -1 if not initialised.</summary>
        public int AttemptsRemaining =>
            _settings != null ? Mathf.Max(0, _settings.maxPinAttempts - _attempts) : -1;

        public void Initialize(MFASettings settings)
        {
            _settings = settings;
            _buffer.Clear();
            _attempts = 0;
        }

        /// <summary>Called by PinKeypadUI when the user taps a digit button.</summary>
        public void EnterDigit(int digit)
        {
            if (_settings == null)
            {
                Debug.LogWarning("[PinAuthenticator] Digit ignored: Initialize() has not been called.");
                return;
            }

            if (_buffer.Length >= _settings.pinLength) return;
            _buffer.Append(digit);

            if (_buffer.Length == _settings.pinLength) Submit();
        }

        public void Clear() => _buffer.Clear();

        private void Submit()
        {
            _attempts++;
            string pin = _buffer.ToString();
            _buffer.Clear();
            OnPinSubmitted?.Invoke(pin);
        }

        /// <summary>Called by MFACoordinator after the server reports a failed check.</summary>
        public void NotifyFailed()
        {
            if (_settings == null) return;
            if (_attempts >= _settings.maxPinAttempts) OnAttemptsExhausted?.Invoke();
        }
    }
}
