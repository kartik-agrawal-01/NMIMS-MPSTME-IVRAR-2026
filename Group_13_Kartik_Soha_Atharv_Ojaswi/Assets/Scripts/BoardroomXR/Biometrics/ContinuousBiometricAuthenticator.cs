using System;
using System.Globalization;
using System.IO;
using System.Text;
using Unity.Netcode;
using UnityEngine;
using BoardroomXR.Identity;
using BoardroomXR.Logging;

namespace BoardroomXR.Biometrics
{
    /// <summary>
    /// Continuous behavioural biometric authentication (Group 13 research question).
    ///
    /// After the MFA door check passes, this component samples the head and both
    /// controllers, enrols the user's motion profile (first login) and then keeps
    /// scoring every 3 s window against it. If the score stays below threshold for
    /// several consecutive windows - e.g. the headset was handed to someone else -
    /// it revokes verification on the server, which hides the boardroom content and
    /// forces the full MFA sequence again.
    ///
    /// It bootstraps itself after scene load, so no scene edits are required.
    /// Runs on the host/server only (single-headset demo = host).
    /// </summary>
    public class ContinuousBiometricAuthenticator : MonoBehaviour
    {
        public static ContinuousBiometricAuthenticator Instance { get; private set; }

        [Header("Algorithm")]
        public BiometricConfig config = new BiometricConfig();
        [Tooltip("Samples per second taken from the head and controllers.")]
        public float sampleRateHz = 60f;
        [Tooltip("Re-enrol on every successful MFA login instead of reusing a stored template.")]
        public bool reEnrolOnEveryLogin = false;

        [Header("Tracked transforms (auto-found if empty)")]
        public Transform head;
        public Transform leftController;
        public Transform rightController;

        [Header("Logging")]
        public bool writeScoreCsv = true;

        public ContinuousBiometricEngine Engine { get; private set; }
        public string LastEvent { get; private set; } = "";
        public string ScoreLogPath { get; private set; }

        private AvatarIdentity _identity;
        private bool _wasVerified;
        private float _sampleAccum;
        private float _nextSearch;
        private StreamWriter _csv;
        private float _sessionStart;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void RegisterSceneHook()
        {
            UnityEngine.SceneManagement.SceneManager.sceneLoaded -= OnSceneLoaded;
            UnityEngine.SceneManagement.SceneManager.sceneLoaded += OnSceneLoaded;
        }

        private static void OnSceneLoaded(UnityEngine.SceneManagement.Scene s, UnityEngine.SceneManagement.LoadSceneMode m) => EnsureExists();

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap() => EnsureExists();

        /// <summary>Creates the persistent biometric object (authenticator + HUD) if it does not exist yet.</summary>
        public static ContinuousBiometricAuthenticator EnsureExists()
        {
            if (Instance != null) return Instance;
            var existing = FindAnyObjectByType<ContinuousBiometricAuthenticator>();
            if (existing != null) return existing;
            var go = new GameObject("ContinuousBiometrics");
            DontDestroyOnLoad(go); // survives scene reloads; re-finds the avatar and rig in the new scene
            var auth = go.AddComponent<ContinuousBiometricAuthenticator>();
            go.AddComponent<BoardroomXR.UI.BiometricStatusHUD>();
            return auth;
        }

        private void Awake()
        {
            Instance = this;
            Engine = new ContinuousBiometricEngine(config);
            Engine.OnEnrolled += HandleEnrolled;
            Engine.OnLockout += HandleLockout;
            Engine.OnWindowScored += HandleWindowScored;
            Engine.OnStateChanged += s => Log($"[Biometric] state -> {s}");
        }

        private void OnDestroy()
        {
            CloseCsv();
            if (Instance == this) Instance = null;
        }

        private bool IsServerSide =>
            NetworkManager.Singleton != null && NetworkManager.Singleton.IsListening && NetworkManager.Singleton.IsServer;

        private void Update()
        {
            if (!IsServerSide) return;

            if (Time.unscaledTime >= _nextSearch && (_identity == null || head == null || rightController == null || leftController == null))
            {
                _nextSearch = Time.unscaledTime + 1f;
                ResolveReferences();
            }
            if (_identity == null || head == null) return;

            bool verified = _identity.IsVerified.Value;
            if (verified != _wasVerified)
            {
                _wasVerified = verified;
                if (verified) BeginMonitoring();
                else Engine.Stop();
            }
            if (!verified) return;

            _sampleAccum += Time.unscaledDeltaTime;
            float step = 1f / Mathf.Max(1f, sampleRateHz);
            if (_sampleAccum < step) return;
            _sampleAccum %= step;

            Engine.Push(new KinematicSample
            {
                t = Time.unscaledTime,
                headPos = head.position,
                headRot = head.rotation,
                leftPos = leftController ? leftController.position : Vector3.zero,
                rightPos = rightController ? rightController.position : Vector3.zero,
                hasLeft = leftController != null && leftController.gameObject.activeInHierarchy,
                hasRight = rightController != null && rightController.gameObject.activeInHierarchy,
            });
        }

        private void BeginMonitoring()
        {
            string user = _identity.ClaimedUserIdString;
            BiometricTemplate existing = reEnrolOnEveryLogin ? null : BiometricTemplate.Load(user);
            Engine.Begin(user, existing, Time.unscaledTime);
            _sampleAccum = 0f;
            OpenCsv(user, existing == null ? "enrol" : "verify");
            Log(existing == null
                ? $"[Biometric] No template for {user}. ENROLLING for {config.enrolSeconds:F0}s - move naturally."
                : $"[Biometric] Template loaded for {user} ({existing.windowCount} windows). CONTINUOUS VERIFICATION active, " +
                  $"threshold {config.matchThreshold:F2}, window {config.windowSeconds:F1}s.");
        }

        private void HandleEnrolled(BiometricTemplate t)
        {
            try { t.Save(); } catch (Exception e) { Debug.LogWarning($"[Biometric] Template save failed: {e.Message}"); }
            Log($"[Biometric] ENROLLED {t.userId} from {t.windowCount} windows. Template saved to {BiometricTemplate.PathFor(t.userId)}. " +
                $"Continuous verification active.");
        }

        private void HandleLockout(string reason)
        {
            Log($"[Biometric] IMPOSTOR LOCKOUT for {Engine.UserId}: {reason}");
            if (_identity != null) _identity.ServerRevokeVerification(reason);
        }

        private void HandleWindowScored(float t, BiometricState s, float raw, float smoothed, float[] f)
        {
            // Backup copy of every window in logcat, so the data survives even if the CSV can't be pulled.
            Debug.Log(string.Format(CultureInfo.InvariantCulture,
                "[BioWin] user={0} t={1:F2} state={2} raw={3:F4} smoothed={4:F4} viol={5}",
                Engine.UserId, t - _sessionStart, s, raw, smoothed, Engine.Violations));
            if (_csv == null) return;
            var sb = new StringBuilder();
            sb.Append((t - _sessionStart).ToString("F3", CultureInfo.InvariantCulture)).Append(',')
              .Append(s).Append(',')
              .Append(raw.ToString("F4", CultureInfo.InvariantCulture)).Append(',')
              .Append(smoothed.ToString("F4", CultureInfo.InvariantCulture)).Append(',')
              .Append(Engine.Violations);
            foreach (float v in f) sb.Append(',').Append(v.ToString("F5", CultureInfo.InvariantCulture));
            _csv.WriteLine(sb.ToString());
            if (s == BiometricState.LockedOut) _csv.Flush();
        }

        /// <summary>Deletes the current user's template so the next login enrols again (demo reset).</summary>
        public void ResetTemplateForCurrentUser()
        {
            if (_identity == null) return;
            BiometricTemplate.Delete(_identity.ClaimedUserIdString);
            Log($"[Biometric] Template deleted for {_identity.ClaimedUserIdString}; next login will re-enrol.");
        }

        // ---------------- helpers ----------------

        private void ResolveReferences()
        {
            if (_identity == null) _identity = FindAnyObjectByType<AvatarIdentity>();
            if (head == null && Camera.main != null) head = Camera.main.transform;

            if (leftController == null || rightController == null)
            {
                Transform bestL = null, bestR = null; int scoreL = 0, scoreR = 0;
                foreach (var t in FindObjectsByType<Transform>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
                {
                    int sc = ControllerNameScore(t.name, out bool isLeft);
                    if (sc <= 0) continue;
                    if (isLeft && sc > scoreL) { bestL = t; scoreL = sc; }
                    if (!isLeft && sc > scoreR) { bestR = t; scoreR = sc; }
                }
                if (leftController == null) leftController = bestL;
                if (rightController == null) rightController = bestR;
                if (leftController != null && rightController != null)
                    Log($"[Biometric] Tracking head={(head != null ? head.name : "none")} left={leftController.name} right={rightController.name}");
            }
        }

        /// <summary>
        /// Ranks transform names so the real tracked controller ("Left Controller") wins over
        /// helper children (attach points, stabilised teleport origins, visuals).
        /// </summary>
        private static int ControllerNameScore(string name, out bool isLeft)
        {
            string n = name.ToLowerInvariant();
            isLeft = n.Contains("left");
            bool isRight = n.Contains("right");
            if (isLeft == isRight || !n.Contains("controller")) return 0;
            string[] reject = { "attach", "interaction", "stabiliz", "stabilis", "teleport", "visual", "model", "poke", "ray", "near", "far", "grab" };
            foreach (var r in reject) if (n.Contains(r)) return 0;
            if (n == "left controller" || n == "right controller") return 3;
            if (n == "lefthand controller" || n == "righthand controller" ||
                n == "left hand controller" || n == "right hand controller") return 2;
            return 1;
        }

        private void OpenCsv(string user, string phase)
        {
            CloseCsv();
            if (!writeScoreCsv) return;
            try
            {
                string dir = Path.Combine(Application.persistentDataPath, "BiometricLogs");
                Directory.CreateDirectory(dir);
                string safe = user;
                foreach (char c in Path.GetInvalidFileNameChars()) safe = safe.Replace(c, '_');
                ScoreLogPath = Path.Combine(dir, $"scores_{safe}_{phase}_{DateTime.UtcNow:yyyyMMdd_HHmmss}.csv");
                _csv = new StreamWriter(ScoreLogPath, false, Encoding.UTF8) { AutoFlush = false };
                _csv.WriteLine("t,state,raw_score,smoothed_score,violations," + string.Join(",", KinematicFeatures.Names));
                _sessionStart = Time.unscaledTime;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[Biometric] Could not open score log: {e.Message}");
                _csv = null;
            }
        }

        private void CloseCsv()
        {
            try { _csv?.Flush(); _csv?.Dispose(); } catch { }
            _csv = null;
        }

        private void OnApplicationPause(bool paused) { if (paused) _csv?.Flush(); }

        private void Log(string msg)
        {
            LastEvent = msg;
            SessionLogger.LogGlobal(msg);
        }
    }
}
