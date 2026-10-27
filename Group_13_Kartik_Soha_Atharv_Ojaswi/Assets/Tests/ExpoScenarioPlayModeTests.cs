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
using BoardroomXR.UI;

namespace BoardroomXR.Tests
{
    /// <summary>
    /// Covers the expo additions end to end inside the real scene: badge identity
    /// switching, per-badge multi-stroke gesture enrolment, per-badge behavioural
    /// enrolment at the quick 30 s preset, the Balanced operating point locking out
    /// a different mover, preset switching, and the rule that the adaptive template
    /// never learns while the engine is in Warning.
    /// </summary>
    public class ExpoScenarioPlayModeTests
    {
        private const string SceneName = "SampleScene";
        private static readonly string[] TouchedBadges = { "EMP-1001", "EMP-1002", "TEST-ADAPT", "TEST-FROZEN" };

        private GameObject _rig;

        /// <summary>Scripted head/hand motion, as used by the continuous biometric tests.</summary>
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
            foreach (var id in TouchedBadges)
            {
                GestureTemplateStore.Clear(id);
                BiometricTemplate.Delete(id);
                MimicryRecording.Delete(id);
            }
            AvatarIdentity.LocalClaimedIdOverride = null;
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
        public IEnumerator BadgeReaderWall_BuildsSixBadges_AndPresentingOneSwitchesIdentity()
        {
            LogAssert.ignoreFailingMessages = true;
            yield return SceneManager.LoadSceneAsync(SceneName, LoadSceneMode.Single);
            yield return null;
            yield return WaitForHost();

            var bootstrap = ExpoRuntimeBootstrap.EnsureExists();
            Assert.IsNotNull(bootstrap, "Expo bootstrap did not come up.");

            // The wall builds itself once the rig exists.
            yield return WaitFor(() => Object.FindAnyObjectByType<BadgeReaderWall>() != null, 5f);
            var wall = Object.FindAnyObjectByType<BadgeReaderWall>();
            Assert.IsNotNull(wall, "BadgeReaderWall was not created by the bootstrap.");

            yield return WaitFor(() => CountBadgeButtons() >= BadgeDirectory.Badges.Count, 5f);
            var buttons = new List<SpatialButton>(Object.FindObjectsByType<SpatialButton>());
            foreach (var badge in BadgeDirectory.Badges)
                Assert.IsTrue(buttons.Exists(b => b.id == badge.id), $"No spatial button for badge {badge.id}.");

            // No colliders may be introduced by the expo furniture: XRI rays must still
            // reach the PIN keypad and the OTP token.
            foreach (var b in buttons)
                Assert.IsEmpty(b.GetComponentsInChildren<Collider>(true), $"Badge {b.id} has a collider.");

            var identity = Object.FindAnyObjectByType<AvatarIdentity>(FindObjectsInactive.Include);
            var coordinator = Object.FindAnyObjectByType<MFACoordinator>(FindObjectsInactive.Include);
            Assert.IsNotNull(identity); Assert.IsNotNull(coordinator);

            Assert.IsTrue(BadgeSwitchService.Select("EMP-1002"),
                "Presenting a badge did not make the server see the new claimed ID.");
            yield return null;

            Assert.AreEqual("EMP-1002", identity.ClaimedUserIdString, "Claimed ID did not change.");
            Assert.AreEqual("EMP-1002", AvatarIdentity.LocalClaimedIdOverride);
            Assert.AreEqual(MFAStage.Pin, coordinator.CurrentStage, "MFA did not restart at factor 1 after the badge switch.");
        }

        [UnityTest]
        public IEnumerator Badge_ThreeStrokeGestureEnrolment_ThenLogin_ThenBiometricEnrolment_StaysVerified()
        {
            LogAssert.ignoreFailingMessages = true;
            yield return SceneManager.LoadSceneAsync(SceneName, LoadSceneMode.Single);
            yield return null;
            yield return WaitForHost();

            var auth = ContinuousBiometricAuthenticator.Instance;
            Assert.IsNotNull(auth);
            auth.config.ApplyPreset(BiometricPreset.Balanced);
            auth.SetEnrolSeconds(30f);
            Assert.AreEqual(30f, auth.config.enrolSeconds, "The 30 s quick enrolment preset did not stick.");
            auth.config.enrolSeconds = 10f;  // the preset is verified above; keep the test short

            var identity = Object.FindAnyObjectByType<AvatarIdentity>(FindObjectsInactive.Include);
            var coordinator = Object.FindAnyObjectByType<MFACoordinator>(FindObjectsInactive.Include);
            var pin = Object.FindAnyObjectByType<PinAuthenticator>(FindObjectsInactive.Include);
            var otp = Object.FindAnyObjectByType<OTPAuthenticator>(FindObjectsInactive.Include);
            var settings = coordinator.settings;

            // Fresh badge: no gesture, no behavioural template.
            GestureTemplateStore.Clear("EMP-1001");
            BiometricTemplate.Delete("EMP-1001");
            BadgeSwitchService.Select("EMP-1001");
            yield return null;
            Assert.AreEqual("EMP-1001", identity.ClaimedUserIdString);

            _rig = new GameObject("TestRig");
            var mover = _rig.AddComponent<ScriptedMover>();
            mover.head = new GameObject("TestHead").transform;
            mover.left = new GameObject("TestLeft").transform;
            mover.right = new GameObject("TestRight").transform;
            mover.head.SetParent(_rig.transform); mover.left.SetParent(_rig.transform); mover.right.SetParent(_rig.transform);
            auth.head = mover.head; auth.leftController = mover.left; auth.rightController = mover.right;

            // ---- factor 1 ----
            foreach (char d in settings.demoEnrolledPin) { pin.EnterDigit(d - '0'); yield return null; }
            yield return WaitFor(() => coordinator.CurrentStage == MFAStage.Gesture, 10f);
            Assert.AreEqual(MFAStage.Gesture, coordinator.CurrentStage);

            // ---- factor 2: three enrolment strokes of the same shape ----
            var g = coordinator.Gesture;
            Assert.IsFalse(g.IsEnrolled, "A fresh badge must start with no signature.");
            Assert.AreEqual(3, g.RequiredEnrolmentSamples);

            for (int i = 0; i < 3; i++)
            {
                g.BeginCapture();
                foreach (var p in KnownGesture(0.01f * i)) g.RecordPoint(p);
                var result = g.SubmitEnrolmentStroke();
                Assert.AreNotEqual(GestureEnrolmentResult.Rejected, result,
                    $"Enrolment stroke {i + 1} was rejected (consistency {g.LastEnrolmentConsistency:F2}).");
                Assert.AreEqual(i == 2 ? GestureEnrolmentResult.Complete : GestureEnrolmentResult.NeedMore, result);
            }
            Assert.IsTrue(g.IsEnrolled);
            Assert.AreEqual(3, g.EnrolledSamples.Count, "All three strokes must be stored.");
            Assert.IsTrue(GestureTemplateStore.SaveSamples("EMP-1001", new List<List<Vector3>>(g.EnrolledSamples)));

            var reloaded = GestureTemplateStore.LoadSamples("EMP-1001");
            Assert.IsNotNull(reloaded);
            Assert.AreEqual(3, reloaded.Count, "Multi-sample enrolment did not round-trip through the store.");

            // A stroke that is nothing like the signature must not pass.
            g.BeginCapture();
            foreach (var p in WrongGesture()) g.RecordPoint(p);
            g.EndCaptureAndScore();
            Assert.IsFalse(g.PassesThreshold(g.LastScore),
                $"A different shape scored {g.LastScore:F2} against the 3-stroke template.");
            yield return null;

            // The genuine signature passes, and the server accepts it.
            yield return WaitFor(() => coordinator.CurrentStage == MFAStage.Gesture, 3f);
            g.BeginCapture();
            foreach (var p in KnownGesture(0f)) g.RecordPoint(p);
            g.EndCaptureAndScore();
            yield return WaitFor(() => coordinator.CurrentStage == MFAStage.Otp, 10f);
            Assert.AreEqual(MFAStage.Otp, coordinator.CurrentStage, "Factor 2 did not pass with the enrolled signature.");

            // ---- factor 3 ----
            string code = otp.IsInitialized ? otp.GetCurrentCode()
                : TotpGenerator.Generate(settings.demoOtpSeed, TotpGenerator.CurrentTimeStep(settings.otpValiditySeconds), settings.otpDigits);
            coordinator.SubmitOtp(code);
            yield return WaitFor(() => identity.IsVerified.Value, 10f);
            Assert.IsTrue(identity.IsVerified.Value, "MFA did not verify the badge holder.");

            // ---- behavioural enrolment for THIS badge ----
            yield return WaitFor(() => auth.Engine.State == BiometricState.Enrolling, 3f);
            yield return WaitFor(() => auth.Engine.State == BiometricState.Verified, 25f);
            Assert.AreEqual(BiometricState.Verified, auth.Engine.State, "Behavioural enrolment did not complete.");

            var template = BiometricTemplate.Load("EMP-1001");
            Assert.IsNotNull(template, "The template was not stored against the badge ID.");
            Assert.AreEqual("EMP-1001", template.userId);

            // ---- genuine use keeps the badge verified ----
            float until = Time.realtimeSinceStartup + 8f;
            while (Time.realtimeSinceStartup < until)
            {
                Assert.AreNotEqual(BiometricState.LockedOut, auth.Engine.State,
                    $"The genuine user was locked out at the Balanced preset (score {auth.Engine.SmoothedScore:F2}).");
                yield return null;
            }

            // Explainability must be populated for the SOC panel and the dashboard.
            Assert.IsTrue(auth.Engine.HasScoredWindow);
            Assert.AreEqual(KinematicFeatures.Count, auth.Engine.LastZ.Length);
            Assert.GreaterOrEqual(auth.Engine.TopDeviatingFeature, 0);
        }

        [UnityTest]
        public IEnumerator BalancedPreset_DifferentMoverOnSameBadge_LocksOutAndRestartsMfa()
        {
            LogAssert.ignoreFailingMessages = true;
            yield return SceneManager.LoadSceneAsync(SceneName, LoadSceneMode.Single);
            yield return null;
            yield return WaitForHost();

            var auth = ContinuousBiometricAuthenticator.Instance;
            Assert.IsNotNull(auth);

            // Preset switching must move both the threshold and the violation count.
            auth.SetPreset(BiometricPreset.Strict);
            Assert.AreEqual(0.25f, auth.config.matchThreshold, 1e-4f);
            Assert.AreEqual(4, auth.config.violationsForLockout);
            auth.SetPreset(BiometricPreset.Relaxed);
            Assert.AreEqual(0.10f, auth.config.matchThreshold, 1e-4f);
            Assert.AreEqual(8, auth.config.violationsForLockout);
            auth.SetPreset(BiometricPreset.Balanced);
            Assert.AreEqual(0.15f, auth.config.matchThreshold, 1e-4f);
            Assert.AreEqual(6, auth.config.violationsForLockout);
            auth.config.enrolSeconds = 10f;

            var gate = Object.FindAnyObjectByType<BoardroomAccessGate>(FindObjectsInactive.Include);
            var identity = Object.FindAnyObjectByType<AvatarIdentity>(FindObjectsInactive.Include);
            var coordinator = Object.FindAnyObjectByType<MFACoordinator>(FindObjectsInactive.Include);
            var pin = Object.FindAnyObjectByType<PinAuthenticator>(FindObjectsInactive.Include);
            var otp = Object.FindAnyObjectByType<OTPAuthenticator>(FindObjectsInactive.Include);
            var settings = coordinator.settings;

            // One stored signature for this badge, so factor 2 verifies rather than enrols.
            GestureTemplateStore.Clear("EMP-1002");
            BiometricTemplate.Delete("EMP-1002");
            Assert.IsTrue(GestureTemplateStore.Save("EMP-1002", KnownGesture(0f)));
            BadgeSwitchService.Select("EMP-1002");
            yield return null;

            _rig = new GameObject("TestRig");
            var mover = _rig.AddComponent<ScriptedMover>();
            mover.head = new GameObject("TestHead").transform;
            mover.left = new GameObject("TestLeft").transform;
            mover.right = new GameObject("TestRight").transform;
            mover.head.SetParent(_rig.transform); mover.left.SetParent(_rig.transform); mover.right.SetParent(_rig.transform);
            auth.head = mover.head; auth.leftController = mover.left; auth.rightController = mover.right;

            foreach (char d in settings.demoEnrolledPin) { pin.EnterDigit(d - '0'); yield return null; }
            yield return WaitFor(() => coordinator.CurrentStage == MFAStage.Gesture, 10f);
            var g = coordinator.Gesture;
            g.BeginCapture(); foreach (var p in KnownGesture(0f)) g.RecordPoint(p); g.EndCaptureAndScore();
            yield return WaitFor(() => coordinator.CurrentStage == MFAStage.Otp, 10f);
            string code = otp.IsInitialized ? otp.GetCurrentCode()
                : TotpGenerator.Generate(settings.demoOtpSeed, TotpGenerator.CurrentTimeStep(settings.otpValiditySeconds), settings.otpDigits);
            coordinator.SubmitOtp(code);
            yield return WaitFor(() => identity.IsVerified.Value, 10f);
            Assert.IsTrue(identity.IsVerified.Value);

            yield return WaitFor(() => auth.Engine.State == BiometricState.Verified, 25f);
            Assert.AreEqual(BiometricState.Verified, auth.Engine.State, "Enrolment did not complete.");

            // Headset handover: a different person, same badge.
            mover.eye = 1.52f; mover.reach = 0.33f; mover.shoulder = 0.16f; mover.drop = 0.28f;
            mover.sway = 0.03f; mover.yawAmp = 45f; mover.handAmp = 0.11f; mover.speed = 1.6f;
            float handover = Time.realtimeSinceStartup;

            yield return WaitFor(() => !identity.IsVerified.Value, 15f);
            float latency = Time.realtimeSinceStartup - handover;
            Assert.IsFalse(identity.IsVerified.Value, "The impostor was never locked out at the Balanced preset.");
            Assert.AreEqual(BiometricState.LockedOut, auth.Engine.State);
            Assert.Less(latency, 12f, $"Balanced-preset lockout took {latency:F1}s.");
            Debug.Log($"[Test] Balanced preset locked out the impostor {latency:F2}s after handover " +
                      $"(threshold {auth.config.matchThreshold:F2}, k={auth.config.violationsForLockout}).");

            for (int i = 0; i < 5; i++) yield return null;
            AssertGatedContentHidden(gate);
            yield return WaitFor(() => coordinator.CurrentStage == MFAStage.Pin, 5f);
            Assert.AreEqual(MFAStage.Pin, coordinator.CurrentStage, "MFA did not restart after the lockout.");
        }

        /// <summary>
        /// The adaptive template is the one place an impostor could try to drag the
        /// profile towards themselves, so it must be provably inert outside Verified.
        /// Driven through the engine directly, with no scene, for determinism.
        /// </summary>
        [Test]
        public void AdaptiveTemplate_NeverLearnsWhileInWarning()
        {
            var cfg = new BiometricConfig();
            cfg.ApplyPreset(BiometricPreset.Balanced);
            cfg.enrolSeconds = 20f;
            cfg.adaptiveTemplate = true;

            var engine = new ContinuousBiometricEngine(cfg);
            engine.Begin("TEST-ADAPT", null, 0f);

            // Enrol on one motion profile. A little past enrolSeconds, because the
            // template is only built on the first scored window after the deadline.
            foreach (var s in Synthetic(0f, 22f, 1.70f, 0.45f, 25f, 1f)) engine.Push(s);
            Assert.AreEqual(BiometricState.Verified, engine.State, "Enrolment did not complete.");
            Assert.IsNotNull(engine.Template);

            // Genuine continuation: adaptation is expected here.
            foreach (var s in Synthetic(22f, 10f, 1.70f, 0.45f, 25f, 1f)) engine.Push(s);
            int afterGenuine = engine.Template.adaptationCount;
            Assert.Greater(afterGenuine, 0, "Adaptive template never updated during genuine use.");

            // Now a clearly different person. Record the adaptation count at the first
            // Warning window and prove it does not move while the state is not Verified.
            int atFirstWarning = -1;
            int warningWindows = 0;
            engine.OnWindowScored += (t, state, raw, smoothed, f) =>
            {
                if (state == BiometricState.Warning || state == BiometricState.LockedOut)
                {
                    if (atFirstWarning < 0) atFirstWarning = engine.Template.adaptationCount;
                    warningWindows++;
                    Assert.AreEqual(atFirstWarning, engine.Template.adaptationCount,
                        "The template adapted while the engine was in Warning or LockedOut.");
                }
            };

            foreach (var s in Synthetic(32f, 20f, 1.50f, 0.32f, 48f, 1.7f)) engine.Push(s);

            Assert.Greater(warningWindows, 0, "The impostor motion never produced a Warning window.");
            Assert.AreEqual(atFirstWarning, engine.Template.adaptationCount,
                "The template changed across the Warning/LockedOut period.");

            // And with adaptation switched off, nothing is learned at all.
            var frozenCfg = new BiometricConfig { adaptiveTemplate = false, enrolSeconds = 20f };
            frozenCfg.ApplyPreset(BiometricPreset.Balanced);
            var frozen = new ContinuousBiometricEngine(frozenCfg);
            frozen.Begin("TEST-FROZEN", null, 0f);
            foreach (var s in Synthetic(0f, 22f, 1.70f, 0.45f, 25f, 1f)) frozen.Push(s);
            foreach (var s in Synthetic(22f, 10f, 1.70f, 0.45f, 25f, 1f)) frozen.Push(s);
            Assert.AreEqual(0, frozen.Template.adaptationCount, "A frozen template must never adapt.");
            Assert.IsFalse(frozen.TemplateDirty);
        }

        [UnityTest]
        public IEnumerator DashboardServer_ServesValidStateJson()
        {
            LogAssert.ignoreFailingMessages = true;
            yield return SceneManager.LoadSceneAsync(SceneName, LoadSceneMode.Single);
            yield return null;
            yield return WaitForHost();

            yield return WaitFor(() => BiometricDashboardServer.Instance != null &&
                                       BiometricDashboardServer.Instance.IsListening, 15f);
            var server = BiometricDashboardServer.Instance;
            Assert.IsNotNull(server, "The dashboard server component was not created.");
            Assert.IsTrue(server.IsListening, "The dashboard server never started listening.");

            // Give the main thread a snapshot to serve.
            yield return new WaitForSecondsRealtime(0.5f);

            string body = null;
            string error = null;
            try { body = HttpGet("127.0.0.1", server.port, "/state"); }
            catch (System.Exception e) { error = e.Message; }

            Assert.IsNull(error, $"GET /state failed: {error}");
            Assert.IsNotNull(body);
            Assert.IsTrue(body.TrimStart().StartsWith("{"), $"/state did not return a JSON object: {body}");

            var probe = JsonUtility.FromJson<StateProbe>(body);
            Assert.IsNotNull(probe, "/state was not parseable JSON.");
            Assert.IsFalse(string.IsNullOrEmpty(probe.identity), "/state carried no identity.");
            Assert.IsFalse(string.IsNullOrEmpty(probe.state), "/state carried no biometric state.");
            Assert.IsNotNull(probe.features, "/state carried no feature array.");
            Assert.AreEqual(KinematicFeatures.Count, probe.features.Length,
                "/state must expose all 16 feature deviations.");
            Assert.IsFalse(string.IsNullOrEmpty(probe.features[0].name));

            // And the page itself must be self-contained: no external script or style.
            string page = HttpGet("127.0.0.1", server.port, "/");
            Assert.IsTrue(page.Contains("<html"), "/ did not return an HTML page.");
            Assert.IsFalse(page.Contains("http://") && page.Contains("<script src"),
                "The dashboard page must not load anything externally.");
        }

        [UnityTest]
        public IEnumerator ExpoFurniture_AddsNoColliders()
        {
            LogAssert.ignoreFailingMessages = true;
            yield return SceneManager.LoadSceneAsync(SceneName, LoadSceneMode.Single);
            yield return null;
            yield return WaitForHost();

            var bootstrap = ExpoRuntimeBootstrap.EnsureExists();
            Assert.IsNotNull(bootstrap);
            // The furniture builds over a few frames as the rig is found.
            yield return new WaitForSecondsRealtime(2.5f);

            var colliders = bootstrap.GetComponentsInChildren<Collider>(true);
            Assert.IsEmpty(colliders,
                $"The expo furniture introduced {colliders.Length} collider(s); XRI rays to the keypad would be blocked.");

            var board = GameObject.Find("ConfidentialWhiteboard");
            if (board != null)
            {
                var content = board.transform.Find("ConfidentialContent");
                if (content != null)
                    Assert.IsEmpty(content.GetComponentsInChildren<Collider>(true),
                        "The whiteboard content introduced a collider.");
            }
        }

        // ---------------- helpers ----------------

        [System.Serializable]
        private class StateProbeFeature
        {
            public string name;
            public float z;
        }

        [System.Serializable]
        private class StateProbe
        {
            public string identity;
            public string state;
            public string preset;
            public float threshold;
            public float[] scores;
            public StateProbeFeature[] features;
        }

        private static string HttpGet(string host, int port, string path)
        {
            using var client = new System.Net.Sockets.TcpClient();
            client.Connect(host, port);
            client.ReceiveTimeout = 4000;
            client.SendTimeout = 4000;

            using var stream = client.GetStream();
            byte[] request = System.Text.Encoding.ASCII.GetBytes(
                $"GET {path} HTTP/1.1\r\nHost: {host}\r\nConnection: close\r\n\r\n");
            stream.Write(request, 0, request.Length);

            using var ms = new System.IO.MemoryStream();
            var buffer = new byte[4096];
            int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0) ms.Write(buffer, 0, read);

            string response = System.Text.Encoding.UTF8.GetString(ms.ToArray());
            int split = response.IndexOf("\r\n\r\n", System.StringComparison.Ordinal);
            return split >= 0 ? response.Substring(split + 4) : response;
        }

        /// <summary>Scripted 60 Hz motion for one person, as used by the evaluation runner.</summary>
        private static List<KinematicSample> Synthetic(float t0, float duration, float eye, float reach,
                                                       float yawAmp, float speed)
        {
            var list = new List<KinematicSample>();
            int n = Mathf.RoundToInt(duration * 60f);
            for (int i = 0; i < n; i++)
            {
                float t = t0 + i / 60f;
                float tt = t * speed;
                Vector3 head = new Vector3(0.015f * Mathf.Sin(1.3f * tt), eye + 0.006f * Mathf.Sin(2.1f * tt),
                                           0.015f * Mathf.Cos(0.9f * tt));
                float yaw = yawAmp * Mathf.Sin(0.6f * tt);
                Quaternion rot = Quaternion.Euler(8f + 5f * Mathf.Sin(0.8f * tt), yaw, 0f);
                Quaternion y = Quaternion.Euler(0f, yaw, 0f);
                list.Add(new KinematicSample
                {
                    t = t,
                    headPos = head,
                    headRot = rot,
                    rightPos = head + y * new Vector3(0.20f + 0.06f * Mathf.Sin(3f * tt), -0.35f, reach),
                    leftPos = head + y * new Vector3(-0.20f, -0.35f, reach * 0.9f),
                    hasLeft = true,
                    hasRight = true,
                });
            }
            return list;
        }

        private static void AssertGatedContentHidden(BoardroomAccessGate gate)
        {
            int enabled = 0, total = 0;
            foreach (var c in gate.gatedContent)
            {
                if (c == null) continue;
                foreach (var r in c.GetComponentsInChildren<Renderer>(true)) { total++; if (r.enabled) enabled++; }
            }
            Assert.Greater(total, 0);
            Assert.AreEqual(0, enabled, $"Confidential content still visible after the lockout ({enabled}/{total}).");
        }

        private static int CountBadgeButtons()
        {
            int n = 0;
            foreach (var b in Object.FindObjectsByType<SpatialButton>())
                if (BadgeDirectory.Find(b.id) != null) n++;
            return n;
        }

        private static List<Vector3> KnownGesture(float jitter)
        {
            var pts = new List<Vector3>();
            for (int i = 0; i <= 20; i++)
            {
                float t = i / 20f;
                pts.Add(new Vector3(Mathf.Sin(t * Mathf.PI) * 0.5f + jitter * Mathf.Sin(7f * t), t * 0.4f, 0f));
            }
            return pts;
        }

        private static List<Vector3> WrongGesture()
        {
            var pts = new List<Vector3>();
            for (int i = 0; i <= 20; i++)
            {
                float t = i / 20f;
                pts.Add(new Vector3(t * 0.5f, Mathf.Abs(t - 0.5f) * 0.6f, 0f)); // a V, not an arch
            }
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
