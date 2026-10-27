using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using BoardroomXR.Auth;
using BoardroomXR.Biometrics;
using BoardroomXR.Identity;
using BoardroomXR.Network;

namespace BoardroomXR.Tests
{
    /// <summary>
    /// End-to-end check of the continuous biometric layer inside the real scene:
    /// real MFA (keypad PIN, gesture, TOTP) -> server verifies -> biometric enrolment ->
    /// genuine use stays verified -> motion changes to a different person ->
    /// lockout -> server revokes -> boardroom content hidden -> MFA restarts at PIN.
    /// Head and controller motion come from scripted test transforms so the test is
    /// deterministic without a headset.
    /// </summary>
    public class ContinuousBiometricPlayModeTests
    {
        private const string SceneName = "SampleScene";

        private string _userId;
        private List<Vector3> _originalGesture;
        private bool _gestureTouched;
        private GameObject _rig;

        private class ScriptedMover : MonoBehaviour
        {
            public Transform head, left, right;
            public float eye = 1.70f, reach = 0.45f, shoulder = 0.20f, drop = 0.35f, sway = 0.015f, yawAmp = 25f, handAmp = 0.06f, speed = 1f;

            private void Update()
            {
                float t = Time.unscaledTime * speed;
                head.position = new Vector3(sway * Mathf.Sin(1.3f * t), eye + 0.4f * sway * Mathf.Sin(2.1f * t), sway * Mathf.Cos(0.9f * t));
                float yaw = yawAmp * Mathf.Sin(0.6f * t);
                head.rotation = Quaternion.Euler(8f + 5f * Mathf.Sin(0.8f * t), yaw, 0f);
                var y = Quaternion.Euler(0f, yaw, 0f);
                right.position = head.position + y * new Vector3(shoulder + handAmp * Mathf.Sin(3f * t), -drop + 0.5f * handAmp * Mathf.Sin(4f * t), reach);
                left.position = head.position + y * new Vector3(-shoulder + 0.6f * handAmp * Mathf.Sin(2.3f * t), -drop, reach * 0.9f);
            }
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            if (_gestureTouched && !string.IsNullOrEmpty(_userId))
            {
                if (_originalGesture != null) GestureTemplateStore.Save(_userId, _originalGesture);
                else GestureTemplateStore.Clear(_userId);
            }
            if (!string.IsNullOrEmpty(_userId)) BiometricTemplate.Delete(_userId);
            if (_rig != null) Object.Destroy(_rig);

            var auth = ContinuousBiometricAuthenticator.Instance;
            if (auth != null) { auth.head = null; auth.leftController = null; auth.rightController = null; }

            var manager = NetworkManager.Singleton;
            if (manager != null)
            {
                if (manager.IsListening) manager.Shutdown();
                float deadline = Time.realtimeSinceStartup + 10f;
                while (manager != null && manager.IsListening && Time.realtimeSinceStartup < deadline) yield return null;
                if (manager != null) Object.DestroyImmediate(manager.gameObject);
            }
            yield return new WaitForSecondsRealtime(0.5f);
            LogAssert.ignoreFailingMessages = false;
        }

        [UnityTest]
        public IEnumerator HandoverToDifferentMover_LocksOut_HidesContent_RestartsMfa()
        {
            LogAssert.ignoreFailingMessages = true;
            yield return SceneManager.LoadSceneAsync(SceneName, LoadSceneMode.Single);
            yield return null;

            _userId = SystemInfoIdentityStub.GetLocalClaimedId();
            _originalGesture = GestureTemplateStore.Load(_userId);
            Assert.IsTrue(GestureTemplateStore.Save(_userId, KnownGesture()));
            _gestureTouched = true;
            BiometricTemplate.Delete(_userId);

            yield return WaitForHost();

            var auth = ContinuousBiometricAuthenticator.Instance;
            Assert.IsNotNull(auth, "ContinuousBiometricAuthenticator did not bootstrap on scene load.");
            Assert.IsNotNull(Object.FindAnyObjectByType<BoardroomXR.UI.BiometricStatusHUD>(), "Status HUD did not bootstrap.");
            auth.config.enrolSeconds = 10f;  // shortened for the test; 30/60 s on the headset
            // Pin the operating point this test's timing assertions were written against,
            // so neither the persisted presenter choice nor a change of default moves it.
            // The Balanced default is covered by ExpoScenarioPlayModeTests.
            auth.config.ApplyPreset(BiometricPreset.Strict);

            _rig = new GameObject("TestRig");
            var mover = _rig.AddComponent<ScriptedMover>();
            mover.head = new GameObject("TestHead").transform;
            mover.left = new GameObject("TestLeft").transform;
            mover.right = new GameObject("TestRight").transform;
            mover.head.SetParent(_rig.transform); mover.left.SetParent(_rig.transform); mover.right.SetParent(_rig.transform);
            auth.head = mover.head; auth.leftController = mover.left; auth.rightController = mover.right;

            var gate = Object.FindAnyObjectByType<BoardroomAccessGate>(FindObjectsInactive.Include);
            var coordinator = Object.FindAnyObjectByType<MFACoordinator>(FindObjectsInactive.Include);
            var identity = Object.FindAnyObjectByType<AvatarIdentity>(FindObjectsInactive.Include);
            var pin = Object.FindAnyObjectByType<PinAuthenticator>(FindObjectsInactive.Include);
            var otp = Object.FindAnyObjectByType<OTPAuthenticator>(FindObjectsInactive.Include);
            Assert.IsNotNull(gate); Assert.IsNotNull(coordinator); Assert.IsNotNull(identity);
            Assert.IsNotNull(pin); Assert.IsNotNull(otp);
            var settings = coordinator.settings;

            // ---- real MFA ----
            foreach (char d in settings.demoEnrolledPin) { pin.EnterDigit(d - '0'); yield return null; }
            yield return WaitFor(() => coordinator.CurrentStage == MFAStage.Gesture, 10f);
            var g = coordinator.Gesture;
            g.BeginCapture(); foreach (var p in KnownGesture()) g.RecordPoint(p); g.EndCaptureAndScore();
            yield return WaitFor(() => coordinator.CurrentStage == MFAStage.Otp, 10f);
            string code = otp.IsInitialized ? otp.GetCurrentCode()
                : TotpGenerator.Generate(settings.demoOtpSeed, TotpGenerator.CurrentTimeStep(settings.otpValiditySeconds), settings.otpDigits);
            coordinator.SubmitOtp(code);
            yield return WaitFor(() => identity.IsVerified.Value, 10f);
            Assert.IsTrue(identity.IsVerified.Value, "MFA did not verify the avatar.");

            // ---- enrolment ----
            yield return WaitFor(() => auth.Engine.State == BiometricState.Enrolling, 3f);
            Assert.AreEqual(BiometricState.Enrolling, auth.Engine.State, "Biometric enrolment did not start after verification.");
            yield return WaitFor(() => auth.Engine.State == BiometricState.Verified, 20f);
            Assert.AreEqual(BiometricState.Verified, auth.Engine.State, "Enrolment did not complete.");
            Assert.IsNotNull(BiometricTemplate.Load(_userId), "Template was not saved to disk.");

            // ---- genuine use: must stay verified ----
            float until = Time.realtimeSinceStartup + 8f;
            while (Time.realtimeSinceStartup < until)
            {
                Assert.AreNotEqual(BiometricState.LockedOut, auth.Engine.State,
                    $"Genuine user was locked out (score {auth.Engine.SmoothedScore:F2}).");
                Assert.IsTrue(identity.IsVerified.Value, "Genuine user lost verification.");
                yield return null;
            }

            // ---- handover: a different person ----
            mover.eye = 1.52f; mover.reach = 0.33f; mover.shoulder = 0.16f; mover.drop = 0.28f;
            mover.sway = 0.03f; mover.yawAmp = 45f; mover.handAmp = 0.11f; mover.speed = 1.6f;
            float handover = Time.realtimeSinceStartup;

            yield return WaitFor(() => !identity.IsVerified.Value, 10f);
            float latency = Time.realtimeSinceStartup - handover;
            Assert.IsFalse(identity.IsVerified.Value, "Impostor was never locked out.");
            Assert.AreEqual(BiometricState.LockedOut, auth.Engine.State);
            Assert.Less(latency, 6f, $"Lockout took {latency:F1}s.");
            Debug.Log($"[Test] Impostor locked out {latency:F2}s after handover.");

            for (int i = 0; i < 5; i++) yield return null;
            AssertHidden(gate);
            yield return WaitFor(() => coordinator.CurrentStage == MFAStage.Pin, 5f);
            Assert.AreEqual(MFAStage.Pin, coordinator.CurrentStage, "MFA did not restart after lockout.");
        }

        // ---------------- helpers ----------------

        private static List<Vector3> KnownGesture()
        {
            var pts = new List<Vector3>();
            for (int i = 0; i <= 20; i++) { float t = i / 20f; pts.Add(new Vector3(Mathf.Sin(t * Mathf.PI) * 0.5f, t * 0.4f, 0f)); }
            return pts;
        }

        private static void AssertHidden(BoardroomAccessGate gate)
        {
            int enabled = 0, total = 0;
            foreach (var c in gate.gatedContent)
            {
                if (c == null) continue;
                foreach (var r in c.GetComponentsInChildren<Renderer>(true)) { total++; if (r.enabled) enabled++; }
            }
            Assert.Greater(total, 0);
            Assert.AreEqual(0, enabled, $"Content still visible after lockout ({enabled}/{total}).");
        }

        private static IEnumerator WaitForHost()
        {
            yield return WaitFor(() => NetworkManager.Singleton != null && NetworkManager.Singleton.IsListening && NetworkManager.Singleton.IsServer, 20f);
            Assert.IsTrue(NetworkManager.Singleton != null && NetworkManager.Singleton.IsServer, "Host did not start.");
            for (int i = 0; i < 10; i++) yield return null;
        }

        private static IEnumerator WaitFor(System.Func<bool> cond, float timeout)
        {
            float deadline = Time.realtimeSinceStartup + timeout;
            while (!cond() && Time.realtimeSinceStartup < deadline) yield return null;
        }
    }
}
