using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using BoardroomXR.Auth;

namespace BoardroomXR.UI
{
    /// <summary>
    /// Pointer handling for every <see cref="SpatialButton"/> in the scene, done
    /// entirely in code: no colliders, no physics raycasts, nothing that could
    /// shadow the XRI ray interactors used by the PIN keypad and the OTP token.
    ///
    /// Each frame it takes the aim transform of both controllers (the XRI
    /// "Ray Interactor" child when present, otherwise the controller itself),
    /// intersects that ray with each button's rectangle, highlights the nearest
    /// hit, draws a small dot where the ray lands, and fires the button on the
    /// trigger's rising edge.
    ///
    /// All buttons go dead while the MFA flow is at the gesture stage, because the
    /// trigger is then being used to draw the signature.
    /// </summary>
    public class SpatialButtonSystem : MonoBehaviour
    {
        public static SpatialButtonSystem Instance { get; private set; }

        [Tooltip("How far a controller ray reaches, in metres.")]
        public float maxRayDistance = 4f;

        [Tooltip("Trigger value that counts as a press (release below 0.4, as elsewhere in the project).")]
        public float pressThreshold = 0.6f;

        private readonly List<SpatialButton> _buttons = new();
        private InputAction _leftTrigger, _rightTrigger;
        private bool _leftHeld, _rightHeld;
        private GameObject _leftDot, _rightDot;
        private Material _leftDotMat, _rightDotMat;
        private MFACoordinator _mfa;
        private float _nextMfaSearch;

        /// <summary>False while the gesture stage owns the trigger.</summary>
        public bool InputEnabled { get; private set; } = true;

        public static SpatialButtonSystem EnsureExists()
        {
            if (Instance != null) return Instance;
            var existing = FindAnyObjectByType<SpatialButtonSystem>();
            if (existing != null) return existing;
            var go = new GameObject("SpatialButtonSystem");
            DontDestroyOnLoad(go);
            return go.AddComponent<SpatialButtonSystem>();
        }

        private void Awake()
        {
            Instance = this;

            _rightTrigger = new InputAction("SpatialUI/RightTrigger", InputActionType.Value, expectedControlType: "Axis");
            _rightTrigger.AddBinding("<XRController>{RightHand}/trigger");

            _leftTrigger = new InputAction("SpatialUI/LeftTrigger", InputActionType.Value, expectedControlType: "Axis");
            _leftTrigger.AddBinding("<XRController>{LeftHand}/trigger");
        }

        private void OnEnable()
        {
            _leftTrigger?.Enable();
            _rightTrigger?.Enable();
        }

        private void OnDisable()
        {
            _leftTrigger?.Disable();
            _rightTrigger?.Disable();
        }

        private void OnDestroy()
        {
            _leftTrigger?.Dispose();
            _rightTrigger?.Dispose();
            if (_leftDotMat != null) Destroy(_leftDotMat);
            if (_rightDotMat != null) Destroy(_rightDotMat);
            if (Instance == this) Instance = null;
        }

        public void Register(SpatialButton b)
        {
            if (b != null && !_buttons.Contains(b)) _buttons.Add(b);
        }

        public void Unregister(SpatialButton b)
        {
            _buttons.Remove(b);
        }

        private void Update()
        {
            if (_mfa == null && Time.unscaledTime >= _nextMfaSearch)
            {
                _nextMfaSearch = Time.unscaledTime + 1f;
                _mfa = FindAnyObjectByType<MFACoordinator>();
            }

            // The trigger draws the signature during factor 2; stealing it for buttons
            // there would make the gesture unusable.
            InputEnabled = _mfa == null || _mfa.CurrentStage != MFAStage.Gesture;

            if (!InputEnabled)
            {
                ClearHover();
                ShowDot(ref _leftDot, false, Vector3.zero);
                ShowDot(ref _rightDot, false, Vector3.zero);
                _leftHeld = _rightHeld = false;
                return;
            }

            SpatialButton hoveredRight = ProcessHand(false, _rightTrigger, ref _rightHeld, ref _rightDot, ref _rightDotMat);
            SpatialButton hoveredLeft = ProcessHand(true, _leftTrigger, ref _leftHeld, ref _leftDot, ref _leftDotMat);

            foreach (var b in _buttons)
            {
                if (b == null) continue;
                b.SetHovered(b == hoveredRight || b == hoveredLeft);
            }

            PollKeyboardShortcuts();
        }

        /// <summary>Raycasts one hand, updates its dot and fires a click on the trigger's rising edge.</summary>
        private SpatialButton ProcessHand(bool left, InputAction trigger, ref bool held, ref GameObject dot, ref Material dotMat)
        {
            Transform aim = XrRigLocator.AimTransform(left);
            SpatialButton hit = null;
            Vector3 hitPoint = Vector3.zero;

            if (aim != null)
            {
                float best = float.MaxValue;
                foreach (var b in _buttons)
                {
                    if (b == null || !b.gameObject.activeInHierarchy || !b.interactable) continue;
                    if (!b.Raycast(aim.position, aim.forward, maxRayDistance, out Vector3 p, out float d)) continue;
                    if (d >= best) continue;
                    best = d; hit = b; hitPoint = p;
                }
            }

            EnsureDot(ref dot, ref dotMat, left);
            ShowDot(ref dot, hit != null, hitPoint);

            float value = trigger != null ? trigger.ReadValue<float>() : 0f;
            bool nowHeld = held ? value > 0.4f : value >= pressThreshold;
            bool rising = nowHeld && !held;
            held = nowHeld;

            if (rising && hit != null) hit.Click();
            return hit;
        }

        /// <summary>Keys 1-6 pick badges, F1-F9 drive the presenter console, without a headset.</summary>
        private void PollKeyboardShortcuts()
        {
            var kb = Keyboard.current;
            if (kb == null) return;

            foreach (var b in _buttons)
            {
                if (b == null || !b.gameObject.activeInHierarchy || !b.interactable) continue;
                if (b.shortcutKey == Key.None) continue;
                var control = kb[b.shortcutKey];
                if (control != null && control.wasPressedThisFrame) b.Click();
            }
        }

        private void ClearHover()
        {
            foreach (var b in _buttons) if (b != null) b.SetHovered(false);
        }

        private void EnsureDot(ref GameObject dot, ref Material mat, bool left)
        {
            if (dot != null) return;
            dot = new GameObject(left ? "SpatialPointerDotL" : "SpatialPointerDotR");
            dot.transform.SetParent(transform, false);
            dot.AddComponent<MeshFilter>().sharedMesh = SpatialUiFactory.QuadMesh;
            mat = SpatialUiFactory.UnlitMaterial(left ? new Color(0.5f, 0.85f, 1f, 0.95f) : new Color(1f, 0.85f, 0.4f, 0.95f));
            var mr = dot.AddComponent<MeshRenderer>();
            mr.sharedMaterial = mat;
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            dot.transform.localScale = new Vector3(0.012f, 0.012f, 1f);
            dot.SetActive(false);
        }

        private void ShowDot(ref GameObject dot, bool visible, Vector3 worldPos)
        {
            if (dot == null) return;
            if (dot.activeSelf != visible) dot.SetActive(visible);
            if (!visible) return;

            // Lifted a few millimetres off the panel so it does not z-fight the face.
            Transform head = XrRigLocator.Head;
            Vector3 toViewer = head != null ? (head.position - worldPos).normalized : Vector3.up;
            dot.transform.position = worldPos + toViewer * 0.004f;
            dot.transform.rotation = Quaternion.LookRotation(-toViewer, Vector3.up);
        }
    }
}
