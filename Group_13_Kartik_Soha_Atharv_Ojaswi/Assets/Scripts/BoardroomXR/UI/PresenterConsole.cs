using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using BoardroomXR.Auth;
using BoardroomXR.Biometrics;
using BoardroomXR.Identity;
using BoardroomXR.Logging;

namespace BoardroomXR.UI
{
    /// <summary>
    /// The presenter's control panel, low on the user's left, built from the same
    /// collider-free <see cref="SpatialButton"/> system as the badge reader. It is
    /// what makes "can you show me X?" answerable at a stall without rebuilding:
    /// operating point, enrolment length, per-badge resets, the adaptive template
    /// switch, the gesture ghost, the mimicry attack, and a clearly labelled forced
    /// lockout for demonstrating the consequences.
    ///
    /// The left controller's Y button hides and shows it; F1-F9 work in the editor.
    /// </summary>
    public class PresenterConsole : MonoBehaviour
    {
        public float forward = 0.7f;
        public float right = -0.35f;
        public float height = 0.95f;

        public Vector2 panelSize = new Vector2(0.62f, 0.58f);
        public Vector2 buttonSize = new Vector2(0.175f, 0.052f);

        private const float Layer = -0.004f;

        private Transform _root;
        private readonly List<Material> _materials = new();
        private readonly List<SpatialButton> _buttons = new();
        private SpatialButton _strict, _balanced, _relaxed, _enrol30, _enrol60, _adaptive, _ghost, _mimicry;
        private TextMesh _statusLine;
        private InputAction _toggleButton;
        private InputAction _toggleKey;
        private bool _visible = true;
        private bool _togglePressed;
        private float _nextRefresh;

        private void Awake()
        {
            // Y on the left controller. secondaryButton is Y / B depending on the hand.
            _toggleButton = new InputAction("Presenter/Toggle", InputActionType.Value, expectedControlType: "Axis");
            _toggleButton.AddBinding("<XRController>{LeftHand}/secondaryButton");

            _toggleKey = new InputAction("Presenter/ToggleKey", InputActionType.Button);
            _toggleKey.AddBinding("<Keyboard>/y");
        }

        private void OnEnable() { _toggleButton?.Enable(); _toggleKey?.Enable(); }
        private void OnDisable() { _toggleButton?.Disable(); _toggleKey?.Disable(); }

        private void OnDestroy()
        {
            _toggleButton?.Dispose();
            _toggleKey?.Dispose();
            BoardroomMaterials.DestroyAll(_materials);
        }

        private void Update()
        {
            if (_root == null)
            {
                if (XrRigLocator.Origin == null && Camera.main == null) return;
                Build();
            }

            // Rising edge on Y, or the Y key in the editor.
            float v = _toggleButton != null ? _toggleButton.ReadValue<float>() : 0f;
            bool pressed = v > 0.5f;
            if ((pressed && !_togglePressed) || (_toggleKey != null && _toggleKey.WasPressedThisFrame()))
                SetVisible(!_visible);
            _togglePressed = pressed;

            if (Time.unscaledTime >= _nextRefresh)
            {
                _nextRefresh = Time.unscaledTime + 0.3f;
                RefreshStates();
            }
        }

        private void SetVisible(bool visible)
        {
            _visible = visible;
            if (_root != null) _root.gameObject.SetActive(visible);
            SessionLogger.LogGlobal($"[Presenter] Console {(visible ? "shown" : "hidden")}.");
        }

        // ---------------- construction ----------------

        private void Build()
        {
            Vector3 pos = XrRigLocator.Place(forward, right, height);
            var go = new GameObject("PresenterConsole");
            go.transform.SetParent(transform, false);
            go.transform.position = pos;
            go.transform.rotation = XrRigLocator.FacingOrigin(pos, 1.45f);  // tilted up towards the face
            _root = go.transform;

            Track(SpatialUiFactory.Quad(_root, "Back", Vector3.zero, panelSize,
                new Color(0.05f, 0.06f, 0.09f, 0.96f), out var back), back);
            Track(SpatialUiFactory.Quad(_root, "Edge", new Vector3(0f, 0f, 0.002f), panelSize + new Vector2(0.012f, 0.012f),
                new Color(0.55f, 0.42f, 0.20f, 0.9f), out var edge), edge);

            float halfW = panelSize.x * 0.5f, halfH = panelSize.y * 0.5f;
            var title = SpatialUiFactory.Text(_root, "Title", new Vector3(0f, halfH - 0.032f, Layer), 0.026f,
                new Color(1f, 0.85f, 0.55f));
            title.text = "PRESENTER CONSOLE";

            var hint = SpatialUiFactory.Text(_root, "Hint", new Vector3(0f, halfH - 0.062f, Layer), 0.0155f,
                new Color(0.6f, 0.66f, 0.72f));
            hint.text = "left Y button hides this  ·  editor: F1-F9";

            float y = halfH - 0.105f;
            float colL = -halfW + 0.012f + buttonSize.x * 0.5f;
            float colR = halfW - 0.012f - buttonSize.x * 0.5f;
            float colM = 0f;

            Label("Operating point (FAR / FRR trade-off)", y);
            y -= 0.030f;
            _strict = Button("preset_strict", "Strict  0.25 k4", colL, y, Key.F1, _ => ApplyPreset(BiometricPreset.Strict));
            _balanced = Button("preset_balanced", "Balanced  0.15 k6", colM, y, Key.F2, _ => ApplyPreset(BiometricPreset.Balanced));
            _relaxed = Button("preset_relaxed", "Relaxed  0.10 k8", colR, y, Key.F3, _ => ApplyPreset(BiometricPreset.Relaxed));
            y -= buttonSize.y + 0.030f;

            Label("Enrolment length", y);
            y -= 0.030f;
            _enrol30 = Button("enrol_30", "30 s (quick)", colL, y, Key.F4, _ => ApplyEnrol(30f));
            _enrol60 = Button("enrol_60", "60 s (default)", colM, y, Key.F5, _ => ApplyEnrol(60f));
            _adaptive = Button("adaptive", "Adaptive: ON", colR, y, Key.F9, _ => ToggleAdaptive());
            y -= buttonSize.y + 0.030f;

            Label("Current badge", y);
            y -= 0.030f;
            Button("reenrol", "Re-enrol (biometric)", colL, y, Key.F6, _ => ReEnrolCurrent());
            Button("reset_badge", "Reset this badge", colM, y, Key.F7, _ => ResetCurrent());
            Button("reset_all", "Reset ALL badges", colR, y, Key.F8, _ => ResetAll());
            y -= buttonSize.y + 0.030f;

            Label("Attack scenarios", y);
            y -= 0.030f;
            _mimicry = Button("mimicry", "Start mimicry attack", colL, y, Key.None, _ => ToggleMimicry());
            _ghost = Button("ghost", "Gesture ghost: OFF", colM, y, Key.None, _ => ToggleGhost());
            var force = Button("force_lockout", "FORCE LOCKOUT (demo)", colR, y, Key.None, _ => ForceLockout());
            force.normalColor = new Color(0.32f, 0.07f, 0.09f, 0.95f);
            force.hoverColor = new Color(0.55f, 0.12f, 0.14f, 0.97f);
            force.ApplyColor();

            _statusLine = SpatialUiFactory.Text(_root, "Status", new Vector3(0f, -halfH + 0.026f, Layer), 0.0155f,
                new Color(0.65f, 0.75f, 0.85f));
            _statusLine.text = "";

            RefreshStates();
        }

        private void Label(string text, float y)
        {
            var t = SpatialUiFactory.Text(_root, "Label" + y.ToString("F3"),
                new Vector3(-panelSize.x * 0.5f + 0.014f, y, Layer), 0.0155f,
                new Color(0.55f, 0.70f, 0.85f), TextAnchor.MiddleLeft, TextAlignment.Left);
            t.text = text;
        }

        private SpatialButton Button(string id, string caption, float x, float y, Key shortcut,
                                     System.Action<SpatialButton> onClick)
        {
            var go = new GameObject("Btn_" + id);
            go.transform.SetParent(_root, false);
            go.transform.localPosition = new Vector3(x, y - buttonSize.y * 0.5f, Layer);
            go.transform.localRotation = Quaternion.identity;

            var b = go.AddComponent<SpatialButton>();
            b.size = buttonSize;
            b.id = id;
            b.shortcutKey = shortcut;

            Track(SpatialUiFactory.Quad(go.transform, "Face", Vector3.zero, buttonSize, b.normalColor, out var faceMat), faceMat);
            b.BindFace(faceMat);

            var label = SpatialUiFactory.Text(go.transform, "Caption", new Vector3(0f, 0f, Layer), 0.0145f, Color.white);
            label.text = caption + (shortcut != Key.None ? $"  [{shortcut}]" : "");

            b.OnClicked += _ => onClick(b);
            _buttons.Add(b);
            return b;
        }

        // ---------------- actions ----------------

        private static ContinuousBiometricAuthenticator Auth => ContinuousBiometricAuthenticator.Instance;

        private static string CurrentBadge()
        {
            var identity = FindAnyObjectByType<AvatarIdentity>(FindObjectsInactive.Include);
            if (identity != null && identity.IsSpawned)
            {
                string id = identity.ClaimedUserIdString;
                if (!string.IsNullOrEmpty(id)) return id;
            }
            return BadgeSwitchService.CurrentBadgeId ?? SystemInfoIdentityStub.GetLocalClaimedId();
        }

        private void ApplyPreset(BiometricPreset p)
        {
            if (Auth == null) return;
            Auth.SetPreset(p);
            Status($"Operating point -> {Auth.config.PresetLabel}. Takes effect on the next window.");
            RefreshStates();
        }

        private void ApplyEnrol(float seconds)
        {
            if (Auth == null) return;
            Auth.SetEnrolSeconds(seconds);
            Status($"Enrolment length -> {seconds:F0} s (applies to the next enrolment).");
            RefreshStates();
        }

        private void ToggleAdaptive()
        {
            if (Auth == null) return;
            Auth.SetAdaptiveTemplate(!Auth.config.adaptiveTemplate);
            Status(Auth.config.adaptiveTemplate
                ? "Adaptive template ON: trusted windows update the profile (never in Warning)."
                : "Adaptive template OFF: the profile is frozen at enrolment.");
            RefreshStates();
        }

        private void ToggleGhost()
        {
            GestureTrail.ShowGhost = !GestureTrail.ShowGhost;
            BioEventLog.Emit("gesture_ghost", CurrentBadge(), GestureTrail.ShowGhost ? "shown" : "hidden");
            Status(GestureTrail.ShowGhost
                ? "Gesture ghost ON: the enrolled signature is outlined during factor 2."
                : "Gesture ghost OFF.");
            RefreshStates();
        }

        private void ReEnrolCurrent()
        {
            string id = CurrentBadge();
            BiometricTemplate.Delete(id);
            MimicryRecording.Delete(id);
            if (Auth != null) Auth.ResetTemplateForCurrentUser();
            BioEventLog.Emit("reenrol_requested", id, "behavioural template deleted; next login enrols again");
            Status($"{BadgeDirectory.NameFor(id)}: behavioural template deleted. Next login re-enrols.");
        }

        private void ResetCurrent()
        {
            string id = CurrentBadge();
            BadgeSwitchService.ResetBadge(id);
            MimicryRecording.Delete(id);
            if (Auth != null) Auth.ResetTemplateForCurrentUser();
            Status($"{BadgeDirectory.NameFor(id)}: signature and behavioural profile cleared.");
        }

        private void ResetAll()
        {
            BadgeSwitchService.ResetAllBadges();
            foreach (var b in BadgeDirectory.Badges) MimicryRecording.Delete(b.id);
            if (Auth != null) Auth.ResetTemplateForCurrentUser();
            Status("All six badges cleared: no signatures, no behavioural profiles, no recordings.");
        }

        private void ToggleMimicry()
        {
            var ghost = MimicryAttackGhost.Instance;
            if (ghost == null) { Status("Mimicry component is not available."); return; }

            string id = CurrentBadge();
            bool running = ghost.Toggle(id);
            if (running)
                Status($"Mimicry attack running against {BadgeDirectory.NameFor(id)}. Copy the ghost - the engine is unchanged.");
            else if (ghost.VictimId == id && !ghost.IsRunning && MimicryRecording.Load(id) != null)
                Status("Mimicry attack stopped.");
            else
                Status($"No reference recording for {BadgeDirectory.NameFor(id)} yet - enrol as that badge first.");
            RefreshStates();
        }

        private void ForceLockout()
        {
            if (Auth == null) return;
            Auth.ForceLockoutForDemo();
            Status("FORCED lockout (demo only). Logged as forced, so it is never read as a detection.");
        }

        private void Status(string message)
        {
            if (_statusLine != null) _statusLine.text = message;
            SessionLogger.LogGlobal("[Presenter] " + message);
        }

        private void RefreshStates()
        {
            var auth = Auth;
            if (auth != null)
            {
                if (_strict != null) _strict.isActive = auth.config.preset == BiometricPreset.Strict;
                if (_balanced != null) _balanced.isActive = auth.config.preset == BiometricPreset.Balanced;
                if (_relaxed != null) _relaxed.isActive = auth.config.preset == BiometricPreset.Relaxed;
                if (_enrol30 != null) _enrol30.isActive = Mathf.Approximately(auth.config.enrolSeconds, 30f);
                if (_enrol60 != null) _enrol60.isActive = Mathf.Approximately(auth.config.enrolSeconds, 60f);
                if (_adaptive != null)
                {
                    _adaptive.isActive = auth.config.adaptiveTemplate;
                    SetCaption(_adaptive, auth.config.adaptiveTemplate ? "Adaptive: ON  [F9]" : "Adaptive: OFF  [F9]");
                }
            }

            if (_ghost != null)
            {
                _ghost.isActive = GestureTrail.ShowGhost;
                SetCaption(_ghost, GestureTrail.ShowGhost ? "Gesture ghost: ON" : "Gesture ghost: OFF");
            }

            var mim = MimicryAttackGhost.Instance;
            if (_mimicry != null)
            {
                _mimicry.isActive = mim != null && mim.IsRunning;
                SetCaption(_mimicry, mim != null && mim.IsRunning ? "Stop mimicry attack" : "Start mimicry attack");
            }

            foreach (var b in _buttons) if (b != null) b.ApplyColor();
        }

        private static void SetCaption(SpatialButton button, string caption)
        {
            var t = button.transform.Find("Caption");
            if (t == null) return;
            var tm = t.GetComponent<TextMesh>();
            if (tm != null) tm.text = caption;
        }

        private void Track(MeshRenderer _, Material m)
        {
            if (m != null) _materials.Add(m);
        }
    }
}
