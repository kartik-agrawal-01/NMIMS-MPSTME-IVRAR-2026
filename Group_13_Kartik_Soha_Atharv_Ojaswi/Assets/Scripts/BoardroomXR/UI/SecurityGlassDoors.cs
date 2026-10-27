using System.Collections.Generic;
using UnityEngine;
using BoardroomXR.Biometrics;
using BoardroomXR.Identity;

namespace BoardroomXR.UI
{
    /// <summary>
    /// Two sliding glass panels between the PIN keypad and the confidential
    /// whiteboard. They are the physical read-out of the server's verification
    /// state: they glide open the moment the server marks the avatar verified, and
    /// slam shut and glow red the moment verification is revoked - which is what a
    /// biometric lockout does.
    ///
    /// Glass only, no colliders: the user walks (or looks) straight through, and the
    /// XRI rays to the keypad and token are never blocked.
    /// </summary>
    public class SecurityGlassDoors : MonoBehaviour
    {
        public float doorForward = 2.3f;
        public float doorHeight = 2.2f;
        public float panelWidth = 0.95f;
        public float openOffset = 1.05f;      // extra slide, outwards, when open
        public float slideSpeed = 1.3f;       // metres per second, opening
        public float slamSpeed = 6.0f;        // metres per second, closing

        private Transform _root, _leftPanel, _rightPanel;
        private TextMesh _statusText;
        private Material _leftGlass, _rightGlass, _frameMat, _revokeBarMat;
        private readonly List<Material> _materials = new();

        private AvatarIdentity _identity;
        private float _open;                  // 0 = closed, 1 = fully open
        private bool _target;                 // desired open state
        private bool _lockedLook;
        private float _nextSearch;
        private float _pulse;

        private static readonly Color GlassIdle = new Color(0.55f, 0.78f, 0.90f, 0.16f);
        private static readonly Color GlassOpen = new Color(0.45f, 0.95f, 0.70f, 0.13f);
        private static readonly Color GlassLocked = new Color(1.0f, 0.18f, 0.18f, 0.38f);

        private void Update()
        {
            if (_root == null)
            {
                if (XrRigLocator.Origin == null && Camera.main == null) return;
                Build();
            }

            if (_identity == null && Time.unscaledTime >= _nextSearch)
            {
                _nextSearch = Time.unscaledTime + 1f;
                _identity = FindAnyObjectByType<AvatarIdentity>(FindObjectsInactive.Include);
            }

            var engine = ContinuousBiometricAuthenticator.Instance?.Engine;
            bool verified = _identity != null && _identity.IsSpawned && _identity.IsVerified.Value;
            bool locked = engine != null && engine.State == BiometricState.LockedOut && !verified;

            _target = verified;
            if (_lockedLook != locked) { _lockedLook = locked; ApplyLook(); }

            // Opening glides; closing slams.
            float speed = _target ? slideSpeed : slamSpeed;
            float want = _target ? 1f : 0f;
            if (!Mathf.Approximately(_open, want))
            {
                _open = Mathf.MoveTowards(_open, want, speed * Time.unscaledDeltaTime / Mathf.Max(0.01f, openOffset));
                ApplySlide();
            }

            if (_lockedLook)
            {
                _pulse += Time.unscaledDeltaTime * 4f;
                float a = 0.28f + 0.16f * Mathf.Sin(_pulse);
                SetGlassAlpha(a);
                if (_revokeBarMat != null)
                    _revokeBarMat.color = new Color(1f, 0.15f, 0.15f, 0.55f + 0.35f * Mathf.Sin(_pulse));
            }
        }

        private void OnDestroy() => BoardroomMaterials.DestroyAll(_materials);

        private void Build()
        {
            Vector3 centre = XrRigLocator.Place(doorForward, 0f, doorHeight * 0.5f);
            var go = new GameObject("SecurityGlassDoors");
            go.transform.SetParent(transform, false);
            go.transform.position = centre;
            go.transform.rotation = Quaternion.LookRotation(XrRigLocator.Forward, Vector3.up);
            _root = go.transform;

            _frameMat = Track(BoardroomMaterials.Lit(new Color(0.26f, 0.28f, 0.32f), 0.55f, 0.35f));
            _leftGlass = Track(BoardroomMaterials.Translucent(GlassIdle));
            _rightGlass = Track(BoardroomMaterials.Translucent(GlassIdle));

            // Door frame: two jambs and a header.
            SpatialUiFactory.Primitive(PrimitiveType.Cube, _root, "JambL",
                new Vector3(-(panelWidth + 0.06f), 0f, 0f), new Vector3(0.07f, doorHeight, 0.1f), Quaternion.identity, _frameMat);
            SpatialUiFactory.Primitive(PrimitiveType.Cube, _root, "JambR",
                new Vector3(panelWidth + 0.06f, 0f, 0f), new Vector3(0.07f, doorHeight, 0.1f), Quaternion.identity, _frameMat);
            SpatialUiFactory.Primitive(PrimitiveType.Cube, _root, "Header",
                new Vector3(0f, doorHeight * 0.5f, 0f), new Vector3(2f * panelWidth + 0.2f, 0.09f, 0.12f), Quaternion.identity, _frameMat);

            _leftPanel = BuildPanel("PanelLeft", -panelWidth * 0.5f, _leftGlass);
            _rightPanel = BuildPanel("PanelRight", panelWidth * 0.5f, _rightGlass);

            // Status strip on the header, facing the user.
            var stripGo = new GameObject("StatusStrip");
            stripGo.transform.SetParent(_root, false);
            stripGo.transform.localPosition = new Vector3(0f, doorHeight * 0.5f - 0.14f, -0.09f);
            // The root's +Z already points away from the user, which is the direction a
            // Unity TextMesh must face to read correctly, so no flip is needed here.
            stripGo.transform.localRotation = Quaternion.identity;

            SpatialUiFactory.Quad(stripGo.transform, "StripBack", Vector3.zero, new Vector2(1.9f, 0.1f),
                new Color(0.05f, 0.06f, 0.08f, 0.9f), out var backMat);
            Track(backMat);
            SpatialUiFactory.Quad(stripGo.transform, "RevokeBar", new Vector3(0f, 0f, 0.002f), new Vector2(1.95f, 0.13f),
                new Color(1f, 0.15f, 0.15f, 0f), out _revokeBarMat);
            Track(_revokeBarMat);

            _statusText = SpatialUiFactory.Text(stripGo.transform, "StatusText", new Vector3(0f, 0f, -0.004f), 0.052f,
                new Color(0.7f, 0.85f, 1f));

            ApplyLook();
            ApplySlide();
            SpatialUiFactory.StripColliders(go);
        }

        private Transform BuildPanel(string name, float x, Material glass)
        {
            var panel = new GameObject(name);
            panel.transform.SetParent(_root, false);
            panel.transform.localPosition = new Vector3(x, 0f, 0f);

            SpatialUiFactory.Primitive(PrimitiveType.Cube, panel.transform, name + "Glass", Vector3.zero,
                new Vector3(panelWidth, doorHeight - 0.1f, 0.02f), Quaternion.identity, glass);
            SpatialUiFactory.Primitive(PrimitiveType.Cube, panel.transform, name + "Rail", new Vector3(0f, 0f, 0f),
                new Vector3(panelWidth, 0.04f, 0.05f), Quaternion.identity, _frameMat);
            return panel.transform;
        }

        private void ApplySlide()
        {
            if (_leftPanel == null) return;
            float slide = _open * openOffset;
            _leftPanel.localPosition = new Vector3(-panelWidth * 0.5f - slide, 0f, 0f);
            _rightPanel.localPosition = new Vector3(panelWidth * 0.5f + slide, 0f, 0f);
        }

        private void ApplyLook()
        {
            if (_statusText == null) return;

            if (_lockedLook)
            {
                _statusText.text = "ACCESS REVOKED - IDENTITY MISMATCH";
                _statusText.color = new Color(1f, 0.55f, 0.55f);
                SetGlassColor(GlassLocked);
            }
            else
            {
                bool verified = _identity != null && _identity.IsSpawned && _identity.IsVerified.Value;
                _statusText.text = verified ? "ACCESS GRANTED - BOARD MEETING IN SESSION"
                                            : "SECURE AREA - AUTHENTICATE TO ENTER";
                _statusText.color = verified ? new Color(0.6f, 1f, 0.7f) : new Color(0.7f, 0.85f, 1f);
                SetGlassColor(verified ? GlassOpen : GlassIdle);
                if (_revokeBarMat != null) _revokeBarMat.color = new Color(1f, 0.15f, 0.15f, 0f);
            }
        }

        private void SetGlassColor(Color c)
        {
            if (_leftGlass != null) _leftGlass.color = c;
            if (_rightGlass != null) _rightGlass.color = c;
        }

        private void SetGlassAlpha(float a)
        {
            if (_leftGlass == null) return;
            Color c = _leftGlass.color; c.a = a;
            SetGlassColor(c);
        }

        private Material Track(Material m)
        {
            if (m != null) _materials.Add(m);
            return m;
        }
    }
}
