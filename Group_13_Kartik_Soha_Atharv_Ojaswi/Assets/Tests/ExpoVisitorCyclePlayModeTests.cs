using System.Collections;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using BoardroomXR.Auth;
using BoardroomXR.Biometrics;
using BoardroomXR.Identity;
using BoardroomXR.Logging;
using BoardroomXR.Network;
using BoardroomXR.UI;

namespace BoardroomXR.Tests
{
    /// <summary>
    /// Expo-day realism: several visitors in a row on ONE app run, with a profile reset
    /// in the middle, which is exactly the sequence the stall will see for six hours.
    /// Nothing may carry over between visitors - not the engine state, not the badge,
    /// not the template, not the mimicry recording.
    ///
    /// Also verifies that the evidence we promise to pull off the headset is actually
    /// written to disk during a session.
    /// </summary>
    public class ExpoVisitorCyclePlayModeTests
    {
        private const string SceneName = "SampleScene";
        private const string BadgeA = "EMP-1003";
        private const string BadgeB = "EMP-1004";

        private GameObject _rig;
        private ScriptedMover _mover;
        private AvatarIdentity _identity;
        private MFACoordinator _coordinator;
        private PinAuthenticator _pin;
        private OTPAuthenticator _otp;
        private MFASettings _settings;
        private ContinuousBiometricAuthenticator _auth;

        private class ScriptedMover : MonoBehaviour
        {
            public Transform head, left, right;
            public float eye = 1.70f, reach = 0.45f, shoulder = 0.20f, drop = 0.35f, sway = 0.015f, yawAmp = 25f, handAmp = 0.06f, speed = 1f;

            public void BecomePersonA()
            {
                eye = 1.70f; reach = 0.45f; shoulder = 0.20f; drop = 0.35f;
                sway = 0.015f; yawAmp = 25f; handAmp = 0.06f; speed = 1f;
            }

            public void BecomePersonB()
            {
                eye = 1.52f; reach = 0.33f; shoulder = 0.16f; drop = 0.28f;
                sway = 0.030f; yawAmp = 45f; handAmp = 0.11f; speed = 1.6f;
            }

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
            foreach (var id in new[] { BadgeA, BadgeB })
            {
                GestureTemplateStore.Clear(id);
                BiometricTemplate.Delete(id);
                MimicryRecording.Delete(id);
            }
            AvatarIdentity.LocalClaimedIdOverride = null;
            if (_rig != null) Object.Destroy(_rig);

            if (_auth != null) { _auth.head = null; _auth.leftController = null; _auth.rightController = null; }

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

        /// <summary>
        /// Visitor A: login, enrol, locked out by a different mover.
        /// Visitor B: fresh badge, login, enrol - must NOT inherit anything from A.
        /// Presenter "Reset ALL badges".
        /// Visitor A again: must enrol from scratch, because their template is gone.
        /// </summary>
        [UnityTest]
        public IEnumerator TwoVisitorsThenResetAll_ThirdVisitEnrolsFromScratch()
        {
            LogAssert.ignoreFailingMessages = true;
            yield return SceneManager.LoadSceneAsync(SceneName, LoadSceneMode.Single);
            yield return null;
            yield return WaitForHost();
            yield return Setup();

            // ---------- visitor A ----------
            yield return SignInAs(BadgeA);
            yield return WaitFor(() => _auth.Engine.State == BiometricState.Enrolling, 5f);
            Assert.AreEqual(BiometricState.Enrolling, _auth.Engine.State,
                "Visitor A should have no template on a cleared badge.");
            yield return WaitFor(() => _auth.Engine.State == BiometricState.Verified, 25f);
            Assert.AreEqual(BiometricState.Verified, _auth.Engine.State, "Visitor A enrolment did not finish.");

            var templateA1 = BiometricTemplate.Load(BadgeA);
            Assert.IsNotNull(templateA1, "Visitor A's template was not written.");
            Assert.AreEqual(BadgeA, templateA1.userId);
            Assert.IsNotNull(MimicryRecording.Load(BadgeA),
                "The mimicry reference recording was not saved during visitor A's enrolment.");

            // Handover to a different person on A's badge -> lockout.
            int lockoutsBefore = _auth.LockoutCount;
            _mover.BecomePersonB();
            yield return WaitFor(() => !_identity.IsVerified.Value, 15f);
            Assert.IsFalse(_identity.IsVerified.Value, "Visitor A's session was not revoked after the handover.");
            Assert.AreEqual(BiometricState.LockedOut, _auth.Engine.State);
            Assert.AreEqual(lockoutsBefore + 1, _auth.LockoutCount);
            yield return WaitFor(() => _coordinator.CurrentStage == MFAStage.Pin, 5f);

            // ---------- visitor B: a different badge on the same app run ----------
            _mover.BecomePersonB();      // visitor B really is a different person
            yield return SignInAs(BadgeB);

            Assert.AreEqual(BadgeB, _auth.Engine.UserId,
                "The engine is still monitoring the previous visitor's identity.");
            yield return WaitFor(() => _auth.Engine.State == BiometricState.Enrolling, 5f);
            Assert.AreEqual(BiometricState.Enrolling, _auth.Engine.State,
                "Visitor B must enrol from scratch, not inherit visitor A's template.");
            yield return WaitFor(() => _auth.Engine.State == BiometricState.Verified, 25f);

            var templateB = BiometricTemplate.Load(BadgeB);
            Assert.IsNotNull(templateB, "Visitor B's template was not written.");
            Assert.AreEqual(BadgeB, templateB.userId, "Visitor B's template was stored under the wrong badge.");

            // B stays verified as themselves: no stale state dragging them down.
            float until = Time.realtimeSinceStartup + 5f;
            while (Time.realtimeSinceStartup < until)
            {
                Assert.AreNotEqual(BiometricState.LockedOut, _auth.Engine.State,
                    $"Visitor B was locked out while behaving consistently (score {_auth.Engine.SmoothedScore:F2}).");
                yield return null;
            }

            // Both badges now have profiles on disk.
            Assert.IsNotNull(BiometricTemplate.Load(BadgeA));
            Assert.IsNotNull(BiometricTemplate.Load(BadgeB));

            // ---------- presenter: Reset ALL badges ----------
            // Same code path the console button uses.
            BadgeSwitchService.ResetAllBadges();
            foreach (var b in BadgeDirectory.Badges) MimicryRecording.Delete(b.id);
            _auth.ResetTemplateForCurrentUser();
            yield return null;

            foreach (var b in BadgeDirectory.Badges)
            {
                Assert.IsNull(BiometricTemplate.Load(b.id), $"{b.id} still has a behavioural template after Reset ALL.");
                Assert.IsNull(GestureTemplateStore.LoadSamples(b.id), $"{b.id} still has a signature after Reset ALL.");
                Assert.IsNull(MimicryRecording.Load(b.id), $"{b.id} still has a mimicry recording after Reset ALL.");
            }

            // ---------- visitor A again: must be a brand new enrolment ----------
            _mover.BecomePersonA();
            yield return SignInAs(BadgeA);
            yield return WaitFor(() => _auth.Engine.State == BiometricState.Enrolling, 5f);
            Assert.AreEqual(BiometricState.Enrolling, _auth.Engine.State,
                "After Reset ALL, badge A must enrol from scratch instead of loading a stale template.");
            yield return WaitFor(() => _auth.Engine.State == BiometricState.Verified, 25f);

            var templateA2 = BiometricTemplate.Load(BadgeA);
            Assert.IsNotNull(templateA2, "The re-enrolled template for badge A was not written.");
            Assert.AreNotEqual(templateA1.createdUtc, templateA2.createdUtc,
                "Badge A's template was not rebuilt - the old file survived Reset ALL.");
            Assert.AreEqual(0, templateA2.adaptationCount,
                "A freshly enrolled template must start with no adaptations.");

            Assert.GreaterOrEqual(_auth.SessionCount, 3, "Three monitoring sessions should have been opened.");
        }

        /// <summary>
        /// The evidence we tell examiners we are collecting has to exist on disk: score
        /// CSVs, the session log, the template, the mimicry recording, and [BioEvt] lines.
        /// </summary>
        [UnityTest]
        public IEnumerator SessionWritesAllLogArtefacts()
        {
            LogAssert.ignoreFailingMessages = true;
            yield return SceneManager.LoadSceneAsync(SceneName, LoadSceneMode.Single);
            yield return null;
            yield return WaitForHost();
            yield return Setup();

            int eventsBefore = BioEventLog.TotalCount;

            yield return SignInAs(BadgeA);
            yield return WaitFor(() => _auth.Engine.State == BiometricState.Verified, 30f);
            Assert.AreEqual(BiometricState.Verified, _auth.Engine.State, "Enrolment did not complete.");

            // Let a few verification windows be scored and flushed.
            yield return new WaitForSecondsRealtime(3f);
            _auth.StopMonitoring("test: flush logs");
            yield return null;

            string root = Application.persistentDataPath;

            // 1. per-window score CSV
            string scoreDir = Path.Combine(root, "BiometricLogs");
            Assert.IsTrue(Directory.Exists(scoreDir), $"BiometricLogs was not created under {root}.");
            var csvs = Directory.GetFiles(scoreDir, "scores_*.csv");
            Assert.Greater(csvs.Length, 0, "No scores_*.csv was written.");
            Assert.IsNotNull(_auth.ScoreLogPath);
            Assert.IsTrue(File.Exists(_auth.ScoreLogPath), $"The score log {_auth.ScoreLogPath} is missing.");

            string[] lines = File.ReadAllLines(_auth.ScoreLogPath);
            Assert.Greater(lines.Length, 1, "The score CSV has a header but no scored windows.");
            StringAssert.StartsWith("t,state,raw_score,smoothed_score,violations,", lines[0]);
            Assert.AreEqual(KinematicFeatures.Count + 5, lines[0].Split(',').Length,
                "The score CSV header does not carry all 16 feature columns.");

            // 2. session log
            string logDir = Path.Combine(root, "MFALogs");
            Assert.IsTrue(Directory.Exists(logDir), "MFALogs was not created.");
            Assert.Greater(Directory.GetFiles(logDir, "session_*.log").Length, 0, "No session log was written.");

            // 3. behavioural template
            string templateDir = BiometricTemplate.Dir;
            Assert.IsTrue(Directory.Exists(templateDir), "BiometricTemplates was not created.");
            Assert.IsTrue(File.Exists(BiometricTemplate.PathFor(BadgeA)),
                $"No template file for {BadgeA} at {BiometricTemplate.PathFor(BadgeA)}.");

            // 4. gesture enrolment store (written by the test's own sign-in helper)
            Assert.IsTrue(GestureTemplateStore.HasTemplate(BadgeA), "The gesture enrolment file is missing.");

            // 5. mimicry reference recording, saved next to the template
            string mimicryPath = MimicryRecording.PathFor(BadgeA);
            Assert.IsTrue(File.Exists(mimicryPath), $"No mimicry recording at {mimicryPath}.");
            var recording = MimicryRecording.Load(BadgeA);
            Assert.IsNotNull(recording, "The mimicry recording is unreadable.");
            Assert.Greater(recording.frames.Count, 10, "The mimicry recording is too short to replay.");

            // 6. structured events were emitted
            Assert.Greater(BioEventLog.TotalCount, eventsBefore,
                "No [BioEvt] lines were emitted during the session.");
            var types = new HashSet<string>();
            foreach (var e in BioEventLog.Entries) types.Add(e.type);
            Assert.IsTrue(types.Contains("mfa_pass"), "No mfa_pass event. Types seen: " + string.Join(",", types));
            Assert.IsTrue(types.Contains("enrol_start"), "No enrol_start event. Types seen: " + string.Join(",", types));
            Assert.IsTrue(types.Contains("enrol_done"), "No enrol_done event. Types seen: " + string.Join(",", types));
            Assert.IsTrue(types.Contains("identity_switch"), "No identity_switch event. Types seen: " + string.Join(",", types));

            Debug.Log($"[Test] Log artefacts verified under {root}: {csvs.Length} score CSV(s), " +
                      $"template + mimicry recording for {BadgeA}, {BioEventLog.TotalCount} BioEvt events.");
        }

        // ---------------- helpers ----------------

        private IEnumerator Setup()
        {
            _auth = ContinuousBiometricAuthenticator.Instance;
            Assert.IsNotNull(_auth, "The biometric authenticator did not bootstrap.");
            _auth.config.ApplyPreset(BiometricPreset.Balanced);
            _auth.config.enrolSeconds = 10f;   // the 30/60 s presets are covered elsewhere

            _identity = Object.FindAnyObjectByType<AvatarIdentity>(FindObjectsInactive.Include);
            _coordinator = Object.FindAnyObjectByType<MFACoordinator>(FindObjectsInactive.Include);
            _pin = Object.FindAnyObjectByType<PinAuthenticator>(FindObjectsInactive.Include);
            _otp = Object.FindAnyObjectByType<OTPAuthenticator>(FindObjectsInactive.Include);
            Assert.IsNotNull(_identity); Assert.IsNotNull(_coordinator);
            Assert.IsNotNull(_pin); Assert.IsNotNull(_otp);
            _settings = _coordinator.settings;

            _rig = new GameObject("TestRig");
            _mover = _rig.AddComponent<ScriptedMover>();
            _mover.head = new GameObject("TestHead").transform;
            _mover.left = new GameObject("TestLeft").transform;
            _mover.right = new GameObject("TestRight").transform;
            _mover.head.SetParent(_rig.transform);
            _mover.left.SetParent(_rig.transform);
            _mover.right.SetParent(_rig.transform);
            _auth.head = _mover.head; _auth.leftController = _mover.left; _auth.rightController = _mover.right;

            foreach (var id in new[] { BadgeA, BadgeB })
            {
                GestureTemplateStore.Clear(id);
                BiometricTemplate.Delete(id);
                MimicryRecording.Delete(id);
            }
            yield return null;
        }

        /// <summary>Presents a badge and walks all three factors, as a visitor would.</summary>
        private IEnumerator SignInAs(string badgeId)
        {
            // A stored signature stands in for the three enrolment strokes, which are
            // covered by ExpoScenarioPlayModeTests; here the point is the badge lifecycle.
            Assert.IsTrue(GestureTemplateStore.Save(badgeId, KnownGesture()));
            Assert.IsTrue(BadgeSwitchService.Select(badgeId), $"The server did not accept badge {badgeId}.");
            yield return null;
            Assert.AreEqual(badgeId, _identity.ClaimedUserIdString);

            yield return WaitFor(() => _coordinator.CurrentStage == MFAStage.Pin, 5f);
            foreach (char d in _settings.demoEnrolledPin) { _pin.EnterDigit(d - '0'); yield return null; }

            yield return WaitFor(() => _coordinator.CurrentStage == MFAStage.Gesture, 10f);
            var g = _coordinator.Gesture;
            g.BeginCapture();
            foreach (var p in KnownGesture()) g.RecordPoint(p);
            g.EndCaptureAndScore();

            yield return WaitFor(() => _coordinator.CurrentStage == MFAStage.Otp, 10f);
            string code = _otp.IsInitialized ? _otp.GetCurrentCode()
                : TotpGenerator.Generate(_settings.demoOtpSeed, TotpGenerator.CurrentTimeStep(_settings.otpValiditySeconds),
                                         _settings.otpDigits);
            _coordinator.SubmitOtp(code);

            yield return WaitFor(() => _identity.IsVerified.Value, 10f);
            Assert.IsTrue(_identity.IsVerified.Value, $"MFA did not verify {badgeId}.");
        }

        private static List<Vector3> KnownGesture()
        {
            var pts = new List<Vector3>();
            for (int i = 0; i <= 20; i++) { float t = i / 20f; pts.Add(new Vector3(Mathf.Sin(t * Mathf.PI) * 0.5f, t * 0.4f, 0f)); }
            return pts;
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
