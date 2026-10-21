using System.Text;
using UnityEngine;
using UnityEngine.UI;
using BoardroomXR.Auth;
using BoardroomXR.Logging;

namespace BoardroomXR.UI
{
    /// <summary>
    /// World-space canvas keypad (0-9 + Clear) driven by XR Interaction
    /// Toolkit's ray / poke interactors.
    ///
    /// The same keypad serves two factors, switching on the coordinator's
    /// current stage: digits go to the PIN during factor 1, and to the
    /// one-time code during factor 3. That means the OTP factor is reachable
    /// with the buttons already in the scene - previously nothing anywhere
    /// called MFACoordinator.SubmitOtp, so the flow could never complete.
    ///
    /// Method names Digit0()..Digit9() and Clear() are unchanged so the
    /// existing Inspector OnClick() bindings keep working.
    /// </summary>
    public class PinKeypadUI : MonoBehaviour
    {
        public PinAuthenticator pinAuthenticator;
        public Text displayLabel; // shows masked PIN progress, or the OTP being typed
        public int pinLength = 4;

        [Header("Optional - found from the scene if left empty")]
        [Tooltip("Used to tell which factor the keypad is currently feeding.")]
        public MFACoordinator coordinator;
        [Tooltip("Optional second label for prompts such as 'Enter the 6-digit code'.")]
        public Text statusLabel;
        public SessionLogger sessionLogger;

        [Header("Stage gating")]
        [Tooltip("Keypad root switched off while a factor other than PIN/OTP is active. " +
                 "Left empty, this GameObject is used.")]
        public GameObject keypadRoot;

        [Tooltip("Alpha applied while the keypad is inactive, so it reads as disabled rather than vanishing.")]
        [Range(0f, 1f)] public float inactiveAlpha = 0.35f;

        private readonly StringBuilder _otpBuffer = new();
        private int _pinEnteredCount;
        private bool _bound;
        private CanvasGroup _group;

        private int ActivePinLength => coordinator != null ? coordinator.PinLength : pinLength;
        private int ActiveOtpDigits => coordinator != null ? coordinator.OtpDigits : 6;

        private void Awake()
        {
            if (coordinator == null) coordinator = FindAnyObjectByType<MFACoordinator>();
            if (pinAuthenticator == null && coordinator != null)
                pinAuthenticator = coordinator.GetComponent<PinAuthenticator>();
            if (sessionLogger == null) sessionLogger = SessionLogger.Active;

            if (keypadRoot == null) keypadRoot = gameObject;
            _group = keypadRoot.GetComponent<CanvasGroup>();
            if (_group == null) _group = keypadRoot.AddComponent<CanvasGroup>();
        }

        private void OnEnable()
        {
            if (coordinator == null)
            {
                // No coordinator to tell us the stage; leave the keypad usable so the
                // PIN fallback path in Digit() still works.
                ApplyStageInteractivity(MFAStage.Pin);
                return;
            }

            if (!_bound)
            {
                _bound = true;

                coordinator.OnStageChanged += HandleStageChanged;
                coordinator.OnStatusMessage += HandleStatusMessage;
                if (pinAuthenticator != null) pinAuthenticator.OnPinSubmitted += HandlePinSubmitted;
            }

            ApplyStageInteractivity(coordinator.CurrentStage);
        }

        private void OnDisable()
        {
            if (!_bound) return;
            _bound = false;

            coordinator.OnStageChanged -= HandleStageChanged;
            coordinator.OnStatusMessage -= HandleStatusMessage;
            if (pinAuthenticator != null) pinAuthenticator.OnPinSubmitted -= HandlePinSubmitted;
        }

        private void HandleStageChanged(MFAStage stage)
        {
            _pinEnteredCount = 0;
            _otpBuffer.Clear();
            ApplyStageInteractivity(stage);
            RefreshDisplay();
        }

        /// <summary>
        /// The keypad only ever feeds the PIN and the OTP, so it is switched off for
        /// every other stage. Leaving it live through the gesture factor meant the same
        /// physical trigger that draws a stroke also clicked a digit button: every draw
        /// attempt logged a keypad press and ended the stroke on the button's release.
        /// Clearing blocksRaycasts lets the interactor ray pass straight through.
        /// </summary>
        private void ApplyStageInteractivity(MFAStage stage)
        {
            if (_group == null) return;

            bool usable = stage == MFAStage.Pin || stage == MFAStage.Otp;
            _group.interactable = usable;
            _group.blocksRaycasts = usable;
            _group.alpha = usable ? 1f : inactiveAlpha;
        }

        private void HandleStatusMessage(string message)
        {
            if (statusLabel != null) statusLabel.text = message;
        }

        private void HandlePinSubmitted(string _)
        {
            _pinEnteredCount = 0;
            RefreshDisplay();
        }

        // ---------------- input routing ----------------

        public void Digit(int d)
        {
            MFAStage stage = coordinator != null ? coordinator.CurrentStage : MFAStage.Pin;

            switch (stage)
            {
                case MFAStage.Otp:
                    EnterOtpDigit(d);
                    break;

                case MFAStage.Pin:
                    EnterPinDigit(d);
                    break;

                default:
                    Log($"Keypad press ignored: stage is {stage}.");
                    break;
            }
        }

        private void EnterPinDigit(int d)
        {
            if (pinAuthenticator == null) return;

            // PinAuthenticator holds the authoritative buffer and submits at full length.
            pinAuthenticator.EnterDigit(d);
            _pinEnteredCount = Mathf.Min(_pinEnteredCount + 1, ActivePinLength);
            RefreshDisplay();
        }

        private void EnterOtpDigit(int d)
        {
            int digits = ActiveOtpDigits;
            if (_otpBuffer.Length >= digits) return;

            _otpBuffer.Append(d);
            RefreshDisplay();

            // Auto-submit once the full code is entered, matching how the PIN behaves.
            if (_otpBuffer.Length == digits) SubmitOtpNow();
        }

        /// <summary>
        /// Submits the typed one-time code. Bound to auto-submit at full length;
        /// also public so a dedicated "Submit" button can call it from the Inspector.
        /// </summary>
        public void SubmitOtpNow()
        {
            if (coordinator == null || coordinator.CurrentStage != MFAStage.Otp) return;
            if (_otpBuffer.Length == 0) return;

            string code = _otpBuffer.ToString();
            _otpBuffer.Clear();
            RefreshDisplay();

            coordinator.SubmitOtp(code);
        }

        public void Clear()
        {
            MFAStage stage = coordinator != null ? coordinator.CurrentStage : MFAStage.Pin;

            if (stage == MFAStage.Otp)
            {
                _otpBuffer.Clear();
            }
            else
            {
                pinAuthenticator?.Clear();
                _pinEnteredCount = 0;
            }

            RefreshDisplay();
        }

        private void RefreshDisplay()
        {
            if (displayLabel == null) return;

            MFAStage stage = coordinator != null ? coordinator.CurrentStage : MFAStage.Pin;

            if (stage == MFAStage.Otp)
            {
                // The code is read off the token in the user's own hand, so showing
                // what they typed helps them catch a mistyped digit.
                int digits = ActiveOtpDigits;
                displayLabel.text = _otpBuffer.ToString().PadRight(digits, '_');
                return;
            }

            int len = ActivePinLength;
            displayLabel.text = new string('*', _pinEnteredCount) + new string('_', Mathf.Max(0, len - _pinEnteredCount));
        }

        private void Log(string message)
        {
            if (sessionLogger != null) sessionLogger.Log(message);
            else SessionLogger.LogGlobal(message);
        }

        public void BindButtons(Button[] digitButtons, Button clearButton)
        {
            for (int i = 0; i < digitButtons.Length; i++)
            {
                int digit = i;
                digitButtons[i].onClick.AddListener(() => Digit(digit));
            }
            if (clearButton != null) clearButton.onClick.AddListener(Clear);
        }

        // Convenience wrappers for Inspector OnClick() binding without lambdas.
        public void Digit0() => Digit(0);
        public void Digit1() => Digit(1);
        public void Digit2() => Digit(2);
        public void Digit3() => Digit(3);
        public void Digit4() => Digit(4);
        public void Digit5() => Digit(5);
        public void Digit6() => Digit(6);
        public void Digit7() => Digit(7);
        public void Digit8() => Digit(8);
        public void Digit9() => Digit(9);
    }
}
