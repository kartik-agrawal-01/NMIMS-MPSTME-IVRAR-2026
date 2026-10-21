using UnityEngine;
using BoardroomXR.Auth;

namespace BoardroomXR.UI
{
    /// <summary>
    /// TEST HARNESS - a real user reads the rotating code off the token and types
    /// it in. This reads the current code straight off the OTPAuthenticator and
    /// submits it to the coordinator, so Factor 3 can be tested without retyping
    /// the rotating 6-digit number by hand.
    /// Wire a "Submit OTP (Auto-fill)" button's OnClick() to SubmitCurrentOtp().
    /// </summary>
    public class OtpTestSubmitter : MonoBehaviour
    {
        public OTPAuthenticator otpAuthenticator;
        public MFACoordinator coordinator;

        public void SubmitCurrentOtp()
        {
            if (otpAuthenticator == null || coordinator == null)
            {
                Debug.LogWarning("[OtpTestSubmitter] otpAuthenticator or coordinator not assigned.", this);
                return;
            }

            string code = otpAuthenticator.GetCurrentCode();
            Debug.Log($"[OtpTestSubmitter] Auto-filling current OTP code {code}.", this);
            coordinator.SubmitOtp(code);
        }
    }
}
