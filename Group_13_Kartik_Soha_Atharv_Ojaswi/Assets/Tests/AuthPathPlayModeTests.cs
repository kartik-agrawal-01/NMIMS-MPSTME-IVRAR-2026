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
    /// Proves the server-authoritative MFA path actually gates on real credential
    /// material, and that SampleScene starts exactly one rig's worth of auth
    /// components. Everything asserted here is read out of the shipping scene -
    /// no test-only rig is constructed - so a regression in the scene wiring
    /// fails these tests rather than passing against a synthetic stand-in.
    /// </summary>
    public class AuthPathPlayModeTests
    {
        private const string SceneName = "SampleScene";
        private const float HostStartTimeoutSeconds = 20f;

        /// <summary>
        /// Shuts Netcode down and releases the transport's UDP socket between
        /// tests. Without this, the first test leaves port 7777 bound, the next
        /// StartHost fails with "address already in use", and the failure looks
        /// like a product defect rather than a leaked host.
        /// </summary>
        [UnityTearDown]
        public IEnumerator UnityTearDown()
        {
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

            // Give the OS a moment to actually release the socket before the next bind.
            yield return new WaitForSecondsRealtime(0.5f);
            LogAssert.ignoreFailingMessages = false;
        }

        // ------------------------------------------------------------------
        // Host-dependent assertions. Ordered inside one test because
        // "provisioned BEFORE any auth attempt" is only meaningful while no
        // factor check has run yet in this process.
        // ------------------------------------------------------------------
        [UnityTest]
        public IEnumerator ServerAuthPath_ProvisionsThenValidatesRealCredentials()
        {
            // Set inside the test body, not [SetUp]: a UnityTest's log scope is
            // created when the coroutine starts, so a value set in SetUp does not
            // apply to it. XR/OpenXR and URP emit unrelated errors in batchmode;
            // they must not decide the verdict. Every check below is an explicit Assert.
            LogAssert.ignoreFailingMessages = true;

            yield return LoadSampleSceneAndStartHost();

            var coordinator = Object.FindAnyObjectByType<MFACoordinator>();
            Assert.IsNotNull(coordinator, "No MFACoordinator in SampleScene.");

            var settings = coordinator.settings;
            Assert.IsNotNull(settings, "MFACoordinator.settings is not assigned in SampleScene.");

            var identity = Object.FindAnyObjectByType<AvatarIdentity>();
            Assert.IsNotNull(identity, "No AvatarIdentity in SampleScene.");
            string userId = identity.ClaimedUserIdString;

            // ---- 1. provisioned before any auth attempt ----
            Assert.IsTrue(ServerCredentialStore.IsProvisioned,
                "ServerCredentialStore is NOT provisioned after host start. IsProvisioned is true only "
                + "when both the PIN hash and the OTP seed are present, so this means at least one is "
                + "missing and every factor check would fail closed.");

            // ---- 2. PIN: correct validates, wrong does not ----
            Assert.IsTrue(ServerCredentialStore.CheckPin(userId, settings.demoEnrolledPin),
                "The enrolled PIN did not validate against the provisioned hash.");

            string wrongPin = settings.demoEnrolledPin == "0000" ? "1111" : "0000";
            Assert.IsFalse(ServerCredentialStore.CheckPin(userId, wrongPin),
                "A wrong PIN was accepted: " + wrongPin);
            Assert.IsFalse(ServerCredentialStore.CheckPin(userId, string.Empty),
                "An empty PIN was accepted.");
            Assert.IsFalse(ServerCredentialStore.CheckPin(userId, null),
                "A null PIN was accepted.");

            // ---- 3. TOTP: current step validates, stale and garbage do not ----
            int digits = settings.otpDigits;
            int validity = settings.otpValiditySeconds;
            long step = TotpGenerator.CurrentTimeStep(validity);

            string currentCode = TotpGenerator.Generate(settings.demoOtpSeed, step, digits);
            Assert.IsTrue(ServerCredentialStore.CheckOtp(userId, currentCode),
                "The TOTP for the current step did not validate: " + currentCode);

            string staleCode = TotpGenerator.Generate(settings.demoOtpSeed, step - 5, digits);
            Assert.IsFalse(ServerCredentialStore.CheckOtp(userId, staleCode),
                "A TOTP from 5 steps ago was accepted (" + staleCode + "). Validate() allows one step "
                + "of drift either way, so 5 steps back must fail.");

            // Alphabetic, so it can never collide with a numeric TOTP, but still random.
            string randomCode = RandomAlphaCode(6);
            Assert.IsFalse(ServerCredentialStore.CheckOtp(userId, randomCode),
                "A random 6-character string was accepted as a one-time code: " + randomCode);

            // ---- 5. exactly one MFACoordinator and one SpoofDetector ----
            // Inactive included: a duplicate spawned rig's components can be disabled
            // and still be a second instance fighting the first.
            var coordinators = Object.FindObjectsByType<MFACoordinator>(
                FindObjectsInactive.Include, FindObjectsSortMode.None);
            var detectors = Object.FindObjectsByType<SpoofDetector>(
                FindObjectsInactive.Include, FindObjectsSortMode.None);

            Assert.AreEqual(1, coordinators.Length,
                "Expected exactly 1 MFACoordinator after host start, found " + coordinators.Length
                + " (" + DescribeOwners(coordinators) + "). More than one means the rig was both placed "
                + "in the scene and spawned from NetworkManager.PlayerPrefab.");

            Assert.AreEqual(1, detectors.Length,
                "Expected exactly 1 SpoofDetector after host start, found " + detectors.Length
                + " (" + DescribeOwners(detectors) + ").");
        }

        // ------------------------------------------------------------------
        // 4. Gesture factor rejects a non-matching stroke with the debug
        //    fallback template off. Scoring is local, and the server threshold
        //    comes from the same MFASettings, so both are asserted.
        // ------------------------------------------------------------------
        [UnityTest]
        public IEnumerator Gesture_NonMatchingStrokeIsRejected_WithFallbackOff()
        {
            LogAssert.ignoreFailingMessages = true;

            yield return LoadSampleSceneAndStartHost();

            var coordinator = Object.FindAnyObjectByType<MFACoordinator>();
            Assert.IsNotNull(coordinator, "No MFACoordinator in SampleScene.");

            var settings = coordinator.settings;
            Assert.IsNotNull(settings, "MFACoordinator.settings is not assigned in SampleScene.");

            Assert.IsFalse(coordinator.useTestGestureTemplateFallback,
                "MFACoordinator.useTestGestureTemplateFallback is ON in SampleScene. With it on, an "
                + "unenrolled user gets a synthetic straight-line template that almost any stroke matches.");

            var authenticator = coordinator.Gesture;
            Assert.IsNotNull(authenticator, "MFACoordinator has no GestureAuthenticator.");

            // Enrolled template: an L. Live stroke: a straight line. Scoring
            // normalises scale and position, so these stay genuinely different.
            var template = new List<Vector3>
            {
                new Vector3(0f, 0f, 0f),
                new Vector3(0f, 1f, 0f),
                new Vector3(1f, 1f, 0f),
            };
            authenticator.Initialize(settings, template);
            Assert.IsTrue(authenticator.IsEnrolled, "Template did not take; the test would be vacuous.");

            float scored = float.NaN;
            void Capture(float s) => scored = s;
            authenticator.OnGestureScored += Capture;

            try
            {
                authenticator.BeginCapture();
                for (int i = 0; i <= 32; i++)
                {
                    authenticator.RecordPoint(new Vector3(i / 32f, 0f, 0f));
                }
                authenticator.EndCaptureAndScore();
            }
            finally
            {
                authenticator.OnGestureScored -= Capture;
            }

            Assert.IsFalse(float.IsNaN(scored), "OnGestureScored never fired.");

            Assert.IsFalse(authenticator.PassesThreshold(scored),
                "A non-matching stroke scored " + scored.ToString("F3") + ", which cleared the local "
                + settings.gestureMatchThreshold.ToString("F2") + " threshold.");

            Assert.IsFalse(scored >= ServerCredentialStore.MinAcceptableGestureScore,
                "A non-matching stroke scored " + scored.ToString("F3") + ", which the SERVER would "
                + "accept (requires at least "
                + ServerCredentialStore.MinAcceptableGestureScore.ToString("F2") + ").");
        }

        // ------------------------------------------------------------------
        // helpers
        // ------------------------------------------------------------------

        /// <summary>
        /// Loads the shipping scene and waits for the scene's own
        /// NetworkHostBootstrap to bring the host up. Never calls StartHost
        /// itself, so the single-starter arrangement is what is under test.
        /// </summary>
        private static IEnumerator LoadSampleSceneAndStartHost()
        {
            yield return SceneManager.LoadSceneAsync(SceneName, LoadSceneMode.Single);
            yield return null;

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
                "Host did not start within " + HostStartTimeoutSeconds + "s (IsListening="
                + manager.IsListening + ", IsServer=" + manager.IsServer + ").");

            // Let in-scene NetworkObjects spawn so OnNetworkSpawn has run.
            for (int i = 0; i < 10; i++)
            {
                yield return null;
            }
        }

        private static string RandomAlphaCode(int length)
        {
            const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ";
            var builder = new System.Text.StringBuilder(length);
            for (int i = 0; i < length; i++)
            {
                builder.Append(alphabet[Random.Range(0, alphabet.Length)]);
            }
            return builder.ToString();
        }

        private static string DescribeOwners<T>(T[] components) where T : Component
        {
            if (components == null || components.Length == 0) return "none";

            var names = new List<string>(components.Length);
            foreach (var component in components)
            {
                names.Add(component == null
                    ? "<null>"
                    : component.gameObject.name + " (active=" + component.gameObject.activeInHierarchy + ")");
            }
            return string.Join(", ", names);
        }
    }
}
