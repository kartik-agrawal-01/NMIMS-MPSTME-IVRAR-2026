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

        /// <summary>Badge/employee ID currently being monitored, or the claimed ID if idle.</summary>
        public string CurrentUserId =>
            !string.IsNullOrEmpty(Engine?.UserId) ? Engine.UserId
            : _identity != null ? _identity.ClaimedUserIdString : "-";

        /// <summary>How many lockouts this app run has produced (expo counter).</summary>
        public int LockoutCount { get; private set; }

        /// <summary>How many monitoring sessions have been opened this app run.</summary>
        public int SessionCount { get; private set; }

        // ---- persisted presenter choices ----
        private const string PrefPreset = "BXR.Biometric.Preset";
        private const string PrefEnrolSeconds = "BXR.Biometric.EnrolSeconds";
        private const string PrefAdaptive = "BXR.Biometric.Adaptive";

        private AvatarIdentity _identity;
        private bool _wasVerified;
        private float _sampleAccum;
        private float _nextSearch;
        private StreamWriter _csv;
        private float _sessionStart;
        private int _savedAdaptations;
        private readonly MimicryRecorder _mimicry = new MimicryRecorder();

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
            LoadPersistedOperatingPoint();
            Engine = new ContinuousBiometricEngine(config);
            Engine.OnEnrolled += HandleEnrolled;
            Engine.OnLockout += HandleLockout;
            Engine.OnWindowScored += HandleWindowScored;
            Engine.OnStateChanged += s => Log($"[Biometric] state -> {s}");
        }

        private void OnDestroy()
        {
            SaveAdaptedTemplate("component destroyed");
            CloseCsv();
            if (Instance == this) Instance = null;
        }

        // ---------------- operating point (presets) ----------------

        private void LoadPersistedOperatingPoint()
        {
            try
            {
                var p = (BiometricPreset)PlayerPrefs.GetInt(PrefPreset, (int)BiometricPreset.Balanced);
                if (p < BiometricPreset.Strict || p > BiometricPreset.Relaxed) p = BiometricPreset.Balanced;
                config.ApplyPreset(p);

                float enrol = PlayerPrefs.GetFloat(PrefEnrolSeconds, config.enrolSeconds);
                if (enrol >= 5f && enrol <= 600f) config.enrolSeconds = enrol;

                config.adaptiveTemplate = PlayerPrefs.GetInt(PrefAdaptive, config.adaptiveTemplate ? 1 : 0) != 0;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[Biometric] Could not read persisted operating point: {e.Message}");
            }
        }

        /// <summary>
        /// Moves the live operating point along the FAR/FRR curve. Takes effect on the
        /// very next scored window - the engine reads these fields each time - and is
        /// remembered for the next app start.
        /// </summary>
        public void SetPreset(BiometricPreset preset)
        {
            config.ApplyPreset(preset);
            TryWritePref(() => { PlayerPrefs.SetInt(PrefPreset, (int)preset); PlayerPrefs.Save(); });
            BioEventLog.Emit("preset_change", CurrentUserId,
                $"preset={config.PresetLabel} threshold={config.matchThreshold:F2} k={config.violationsForLockout}");
            Log($"[Biometric] Operating point -> {config.PresetLabel}");
        }

        /// <summary>Enrolment length: 30 s quick (expo) or 60 s default.</summary>
        public void SetEnrolSeconds(float seconds)
        {
            config.enrolSeconds = Mathf.Clamp(seconds, 5f, 600f);
            TryWritePref(() => { PlayerPrefs.SetFloat(PrefEnrolSeconds, config.enrolSeconds); PlayerPrefs.Save(); });
            BioEventLog.Emit("enrol_length", CurrentUserId, $"{config.enrolSeconds:F0}s");
            Log($"[Biometric] Enrolment length -> {config.enrolSeconds:F0}s");
        }

        /// <summary>Turns slow-drift template adaptation on or off.</summary>
        public void SetAdaptiveTemplate(bool on)
        {
            config.adaptiveTemplate = on;
            TryWritePref(() => { PlayerPrefs.SetInt(PrefAdaptive, on ? 1 : 0); PlayerPrefs.Save(); });
            BioEventLog.Emit("adaptive_template", CurrentUserId, on ? "enabled" : "disabled");
            Log($"[Biometric] Adaptive template -> {(on ? "ON" : "OFF")}");
        }

        private static void TryWritePref(Action write)
        {
            try { write(); }
            catch (Exception e) { Debug.LogWarning($"[Biometric] PlayerPrefs write failed: {e.Message}"); }
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
                else StopMonitoring("verification ended");
            }
            if (!verified) return;

            _sampleAccum += Time.unscaledDeltaTime;
            float step = 1f / Mathf.Max(1f, sampleRateHz);
            if (_sampleAccum < step) return;
            _sampleAccum %= step;

            var sample = new KinematicSample
            {
                t = Time.unscaledTime,
                headPos = head.position,
                headRot = head.rotation,
                leftPos = leftController ? leftController.position : Vector3.zero,
                rightPos = rightController ? rightController.position : Vector3.zero,
                hasLeft = leftController != null && leftController.gameObject.activeInHierarchy,
                hasRight = rightController != null && rightController.gameObject.activeInHierarchy,
            };

            // While enrolling, also keep a short motion recording of the genuine user,
            // which the mimicry-attack scenario replays as a ghost for an impostor to copy.
            if (Engine.State == BiometricState.Enrolling) _mimicry.Push(sample);

            Engine.Push(sample);
        }

        /// <summary>DEMO ONLY: forces a lockout so a presenter can show the consequences.</summary>
        public void ForceLockoutForDemo()
        {
            if (Engine == null) return;
            BioEventLog.Emit("forced_lockout", CurrentUserId, "presenter console - demo only, not a detection");
            Engine.ForceLockout("presenter console");
        }

        /// <summary>
        /// Starts (or restarts) monitoring for whatever identity the avatar currently
        /// claims. Public so a badge switch can re-key the engine without waiting for
        /// the IsVerified edge.
        /// </summary>
        public void BeginMonitoring()
        {
            if (_identity == null) { ResolveReferences(); if (_identity == null) return; }

            string user = _identity.ClaimedUserIdString;
            BiometricTemplate existing = reEnrolOnEveryLogin ? null : BiometricTemplate.Load(user);
            Engine.Begin(user, existing, Time.unscaledTime);
            _sampleAccum = 0f;
            SessionCount++;
            _savedAdaptations = 0;
            BiometricHistory.Clear();   // the graphs show this session, not the previous person's
            _mimicry.Reset();
            OpenCsv(user, existing == null ? "enrol" : "verify");
            if (existing == null)
            {
                BioEventLog.Emit("enrol_start", user, $"{config.enrolSeconds:F0}s preset={config.PresetLabel}");
                Log($"[Biometric] No template for {user}. ENROLLING for {config.enrolSeconds:F0}s - move naturally.");
            }
            else
            {
                BioEventLog.Emit("verify_start", user,
                    $"template windows={existing.windowCount} adapted={existing.adaptationCount} preset={config.PresetLabel}");
                Log($"[Biometric] Template loaded for {user} ({existing.windowCount} windows, adapted {existing.adaptationCount}x). " +
                    $"CONTINUOUS VERIFICATION active, threshold {config.matchThreshold:F2}, window {config.windowSeconds:F1}s.");
            }
        }

        /// <summary>Stops monitoring, persisting any adaptations learned this session.</summary>
        public void StopMonitoring(string reason)
        {
            SaveAdaptedTemplate(reason);
            Engine.Stop();
            CloseCsv();
        }

        private void HandleEnrolled(BiometricTemplate t)
        {
            try { t.Save(); } catch (Exception e) { Debug.LogWarning($"[Biometric] Template save failed: {e.Message}"); }

            // Keep the motion recording alongside the template, for the mimicry scenario.
            var recording = _mimicry.Build(t.userId);
            if (recording != null && recording.Save())
                Log($"[Biometric] Mimicry reference recording saved for {t.userId}: " +
                    $"{recording.frames.Count} frames, {recording.DurationSeconds:F1}s.");

            _savedAdaptations = 0;
            Engine.MarkTemplateSaved();
            BioEventLog.Emit("enrol_done", t.userId, $"windows={t.windowCount} preset={config.PresetLabel}");
            Log($"[Biometric] ENROLLED {t.userId} from {t.windowCount} windows. Template saved to {BiometricTemplate.PathFor(t.userId)}. " +
                $"Continuous verification active.");
        }

        private void HandleLockout(string reason)
        {
            LockoutCount++;
            int top = Engine.TopDeviatingFeature;
            BioEventLog.Emit("lockout", Engine.UserId,
                $"score={Engine.SmoothedScore:F3} threshold={config.matchThreshold:F2} k={config.violationsForLockout} " +
                $"preset={config.preset} top={(top >= 0 ? KinematicFeatures.Names[top] : "n/a")}");
            Log($"[Biometric] IMPOSTOR LOCKOUT for {Engine.UserId}: {reason}");
            // Adaptations only ever happen in the Verified state, so anything learned
            // before this lockout came from the genuine user and is worth keeping.
            SaveAdaptedTemplate("lockout");
            if (_identity != null) _identity.ServerRevokeVerification(reason);
        }

        /// <summary>
        /// Writes the drift-adapted template back to disk. No-op unless adaptation is
        /// enabled and at least one trusted window was folded in since the last save.
        /// </summary>
        public void SaveAdaptedTemplate(string reason)
        {
            if (Engine == null || Engine.Template == null) return;
            if (!Engine.TemplateDirty) return;
            int added = Engine.AdaptationsThisSession - _savedAdaptations;
            if (added <= 0) { Engine.MarkTemplateSaved(); return; }

            try
            {
                Engine.Template.Save();
                _savedAdaptations = Engine.AdaptationsThisSession;
                Engine.MarkTemplateSaved();
                BioEventLog.Emit("adaptation_saved", Engine.Template.userId,
                    $"+{added} windows (total {Engine.Template.adaptationCount}) reason={reason}");
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[Biometric] Adapted template save failed: {e.Message}");
            }
        }

        private void HandleWindowScored(float t, BiometricState s, float raw, float smoothed, float[] f)
        {
            // Feeds the SOC wall panel, the wrist display and the laptop dashboard.
            BiometricHistory.Record(t - _sessionStart, s, raw, smoothed);

            // Backup copy of every window in logcat, so the data survives even if the CSV can't be pulled.
            int top = Engine.TopDeviatingFeature;
            Debug.Log(string.Format(CultureInfo.InvariantCulture,
                "[BioWin] user={0} t={1:F2} state={2} raw={3:F4} smoothed={4:F4} viol={5} preset={6} thr={7:F2} k={8} top={9} topz={10:F2}",
                Engine.UserId, t - _sessionStart, s, raw, smoothed, Engine.Violations,
                config.preset, config.matchThreshold, config.violationsForLockout,
                top >= 0 ? KinematicFeatures.Names[top] : "n/a",
                top >= 0 ? Engine.LastZ[top] : 0f));
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
            string user = CurrentUserId;
            if (string.IsNullOrEmpty(user) || user == "-") return;
            Engine.Stop();
            Engine.MarkTemplateSaved();      // drop pending adaptations; the template is going away
            BiometricTemplate.Delete(user);
            BioEventLog.Emit("template_reset", user, "biometric template deleted; next login re-enrols");
            Log($"[Biometric] Template deleted for {user}; next login will re-enrol.");
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

        private void OnApplicationPause(bool paused)
        {
            if (!paused) return;
            _csv?.Flush();
            SaveAdaptedTemplate("application paused");
        }

        private void OnApplicationQuit() => SaveAdaptedTemplate("application quit");

        private void Log(string msg)
        {
            LastEvent = msg;
            SessionLogger.LogGlobal(msg);
        }
    }
}
