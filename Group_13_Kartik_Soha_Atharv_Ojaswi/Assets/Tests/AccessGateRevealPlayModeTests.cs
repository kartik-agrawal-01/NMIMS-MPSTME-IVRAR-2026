using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using BoardroomXR.Auth;
using BoardroomXR.Identity;
using BoardroomXR.Network;

namespace BoardroomXR.Tests
{
    /// <summary>
    /// Covers both halves of the access gate, because they fail in opposite
    /// directions and a fix for one can silently break the other:
    ///   - fail closed: confidential content is NOT rendered before verification,
    ///     including when the host never starts;
    ///   - fail open enough: it IS actually revealed once all three factors pass.
    ///
    /// The reveal test drives the real server-authoritative path end to end - real
    /// keypad buttons for the PIN, real gesture scoring, the real TOTP - so it
    /// exercises the AvatarIdentity -> BoardroomAccessGate registration and the
    /// IsVerified.OnValueChanged subscription that actually perform the reveal.
    /// </summary>
    public class AccessGateRevealPlayModeTests
    {
        private const string SceneName = "SampleScene";
        private const float HostStartTimeoutSeconds = 20f;

        // The demo's real enrolment on this machine, saved and restored so the test
        // never leaves a synthetic gesture template behind for the live demo.
        private string _templateUserId;
        private List<Vector3> _originalTemplate;
        private bool _templateTouched;

        [UnityTearDown]
        public IEnumerator UnityTearDown()
        {
            if (_templateTouched && !string.IsNullOrEmpty(_templateUserId))
            {
                if (_originalTemplate != null) GestureTemplateStore.Save(_templateUserId, _originalTemplate);
                else GestureTemplateStore.Clear(_templateUserId);
                _templateTouched = false;
            }

            var manager = NetworkManager.Singleton;
            if (manager != null)
            {
                if (manager.IsListening) manager.Shutdown();

                float deadline = Time.realtimeSinceStartup + 10f;
                while (manager != null && manager.IsListening && Time.realtimeSinceStartup < deadline)
                {
                    yield return null;
                }

                if (manager != null) Object.DestroyImmediate(manager.gameObject);
            }

            yield return new WaitForSecondsRealtime(0.5f);
            LogAssert.ignoreFailingMessages = false;
        }

        // ------------------------------------------------------------------
        // Fail closed
        // ------------------------------------------------------------------
        [UnityTest]
        public IEnumerator GatedContent_IsHiddenBeforeVerification()
        {
            LogAssert.ignoreFailingMessages = true;

            yield return LoadSceneAndStartHost();

            var gate = Object.FindAnyObjectByType<BoardroomAccessGate>(FindObjectsInactive.Include);
            Assert.IsNotNull(gate, "No BoardroomAccessGate in SampleScene.");
            Assert.Greater(gate.gatedContent.Count, 0,
                "BoardroomAccessGate.gatedContent is empty - the gate would have nothing to hide "
                + "and the reveal moment would be invisible.");

            var identity = Object.FindAnyObjectByType<AvatarIdentity>(FindObjectsInactive.Include);
            Assert.IsNotNull(identity, "No AvatarIdentity in SampleScene.");
            Assert.IsFalse(identity.IsVerified.Value, "Avatar is already verified before any factor ran.");

            AssertGatedRenderers(gate, false,
                "Confidential content is RENDERED before verification. The gate fails open.");
        }

        /// <summary>
        /// The B16 case: OnNetworkSpawn never runs when the host fails to start, so
        /// hiding must not depend on it. Loads the scene with host auto-start disabled.
        /// </summary>
        [UnityTest]
        public IEnumerator GatedContent_IsHiddenEvenWhenHostNeverStarts()
        {
            LogAssert.ignoreFailingMessages = true;

            yield return SceneManager.LoadSceneAsync(SceneName, LoadSceneMode.Single);

            // Disable the host starter in the same frame the scene loads, before its
            // Start() runs, so networking genuinely never comes up.
            var bootstrap = Object.FindAnyObjectByType<NetworkHostBootstrap>(FindObjectsInactive.Include);
            Assert.IsNotNull(bootstrap, "No NetworkHostBootstrap in SampleScene.");
            bootstrap.startHostOnPlay = false;

            for (int i = 0; i < 10; i++) yield return null;

            var manager = NetworkManager.Singleton;
            Assert.IsFalse(manager != null && manager.IsListening,
                "Host started anyway; this test cannot exercise the never-started case.");

            var gate = Object.FindAnyObjectByType<BoardroomAccessGate>(FindObjectsInactive.Include);
            Assert.IsNotNull(gate, "No BoardroomAccessGate in SampleScene.");

            AssertGatedRenderers(gate, false,
                "With the host never started, confidential content is RENDERED. This is exactly the "
                + "B16 fail-open case: OnNetworkSpawn never ran, so nothing hid the content.");
        }

        // ------------------------------------------------------------------
        // Reveal after full verification
        // ------------------------------------------------------------------
        [UnityTest]
        public IEnumerator GatedContent_IsRevealedAfterFullMfaVerification()
        {
            LogAssert.ignoreFailingMessages = true;

            yield return SceneManager.LoadSceneAsync(SceneName, LoadSceneMode.Single);
            yield return null;

            // Pre-enrol a known gesture BEFORE the session reaches the gesture stage, so
            // factor 2 is a genuine template match rather than a first-stroke enrolment.
            // MFACoordinator loads the template in EnterGestureStage, after the PIN passes.
            _templateUserId = SystemInfoIdentityStub.GetLocalClaimedId();
            _originalTemplate = GestureTemplateStore.Load(_templateUserId);
            Assert.IsTrue(GestureTemplateStore.Save(_templateUserId, KnownGesture()),
                "Could not write the test gesture template.");
            _templateTouched = true;

            yield return WaitForHost();

            var gate = Object.FindAnyObjectByType<BoardroomAccessGate>(FindObjectsInactive.Include);
            var coordinator = Object.FindAnyObjectByType<MFACoordinator>(FindObjectsInactive.Include);
            var identity = Object.FindAnyObjectByType<AvatarIdentity>(FindObjectsInactive.Include);
            var pin = Object.FindAnyObjectByType<PinAuthenticator>(FindObjectsInactive.Include);
            var otp = Object.FindAnyObjectByType<OTPAuthenticator>(FindObjectsInactive.Include);

            Assert.IsNotNull(gate, "No BoardroomAccessGate in SampleScene.");
            Assert.IsNotNull(coordinator, "No MFACoordinator in SampleScene.");
            Assert.IsNotNull(identity, "No AvatarIdentity in SampleScene.");
            Assert.IsNotNull(pin, "No PinAuthenticator in SampleScene.");
            Assert.IsNotNull(otp, "No OTPAuthenticator in SampleScene.");

            var settings = coordinator.settings;
            Assert.IsNotNull(settings, "MFACoordinator.settings is not assigned.");

            AssertGatedRenderers(gate, false, "Content is visible before the PIN was even entered.");

            // ---- factor 1: PIN, through the real keypad ----
            Assert.AreEqual(MFAStage.Pin, coordinator.CurrentStage,
                "Session is not at the PIN stage; autoStartOnPlay may be off.");

            foreach (char digit in settings.demoEnrolledPin)
            {
                pin.EnterDigit(digit - '0');
                yield return null;
            }

            yield return WaitForStage(coordinator, MFAStage.Gesture, 10f);
            Assert.AreEqual(MFAStage.Gesture, coordinator.CurrentStage,
                "Correct PIN did not advance the session to the gesture stage. Server rejected it.");

            // ---- factor 2: gesture, same shape as the enrolled template ----
            var authenticator = coordinator.Gesture;
            Assert.IsTrue(authenticator.IsEnrolled,
                "The pre-enrolled template was not loaded, so factor 2 would enrol instead of match.");

            authenticator.BeginCapture();
            foreach (var point in KnownGesture()) authenticator.RecordPoint(point);
            authenticator.EndCaptureAndScore();

            yield return WaitForStage(coordinator, MFAStage.Otp, 10f);
            Assert.AreEqual(MFAStage.Otp, coordinator.CurrentStage,
                "A matching gesture (score " + authenticator.LastScore.ToString("F3")
                + ") did not advance the session to the OTP stage.");

            // ---- factor 3: the real rotating code ----
            string code = otp.IsInitialized
                ? otp.GetCurrentCode()
                : TotpGenerator.Generate(settings.demoOtpSeed,
                    TotpGenerator.CurrentTimeStep(settings.otpValiditySeconds), settings.otpDigits);

            Assert.IsNotEmpty(code, "Could not obtain the current one-time code.");
            coordinator.SubmitOtp(code);

            yield return WaitForStage(coordinator, MFAStage.Passed, 10f);
            Assert.AreEqual(MFAStage.Passed, coordinator.CurrentStage,
                "The correct one-time code did not pass factor 3.");

            // ---- the payoff: server marked verified AND content actually revealed ----
            float deadline = Time.realtimeSinceStartup + 5f;
            while (!identity.IsVerified.Value && Time.realtimeSinceStartup < deadline) yield return null;

            Assert.IsTrue(identity.IsVerified.Value,
                "MFA passed but the server never set AvatarIdentity.IsVerified.");

            for (int i = 0; i < 5; i++) yield return null;

            AssertGatedRenderers(gate, true,
                "All three factors passed and IsVerified is true, but the confidential content is "
                + "still HIDDEN. The reveal - the visible \"access granted\" moment - does not happen.");
        }

        // ------------------------------------------------------------------
        // helpers
        // ------------------------------------------------------------------

        /// <summary>A shape distinct enough that a straight line will not match it.</summary>
        private static List<Vector3> KnownGesture()
        {
            var points = new List<Vector3>();
            for (int i = 0; i <= 20; i++)
            {
                float t = i / 20f;
                points.Add(new Vector3(Mathf.Sin(t * Mathf.PI) * 0.5f, t * 0.4f, 0f));
            }
            return points;
        }

        private static void AssertGatedRenderers(BoardroomAccessGate gate, bool expectedEnabled, string message)
        {
            int enabled = 0;
            int total = 0;

            foreach (var content in gate.gatedContent)
            {
                if (content == null) continue;
                foreach (var renderer in content.GetComponentsInChildren<Renderer>(true))
                {
                    total++;
                    if (renderer.enabled) enabled++;
                }
            }

            Assert.Greater(total, 0, "Gated content has no Renderers, so visibility cannot be asserted.");

            if (expectedEnabled)
            {
                Assert.AreEqual(total, enabled,
                    message + " (" + enabled + " of " + total + " renderers enabled)");
            }
            else
            {
                Assert.AreEqual(0, enabled,
                    message + " (" + enabled + " of " + total + " renderers enabled)");
            }
        }

        private static IEnumerator LoadSceneAndStartHost()
        {
            yield return SceneManager.LoadSceneAsync(SceneName, LoadSceneMode.Single);
            yield return null;
            yield return WaitForHost();
        }

        private static IEnumerator WaitForHost()
        {
            float deadline = Time.realtimeSinceStartup + HostStartTimeoutSeconds;
            while (Time.realtimeSinceStartup < deadline)
            {
                var candidate = NetworkManager.Singleton;
                if (candidate != null && candidate.IsListening && candidate.IsServer) break;
                yield return null;
            }

            var manager = NetworkManager.Singleton;
            Assert.IsNotNull(manager, "No NetworkManager in SampleScene.");
            Assert.IsTrue(manager.IsListening && manager.IsServer,
                "Host did not start within " + HostStartTimeoutSeconds + "s.");

            for (int i = 0; i < 10; i++) yield return null;
        }

        private static IEnumerator WaitForStage(MFACoordinator coordinator, MFAStage stage, float timeoutSeconds)
        {
            float deadline = Time.realtimeSinceStartup + timeoutSeconds;
            while (coordinator.CurrentStage != stage && Time.realtimeSinceStartup < deadline)
            {
                yield return null;
            }
        }
    }
}
