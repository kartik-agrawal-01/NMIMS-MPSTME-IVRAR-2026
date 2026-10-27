using System;
using UnityEngine;

namespace BoardroomXR.UI
{
    /// <summary>
    /// A world-space button with <b>no collider</b>. The hit test is done in code
    /// against the button's own rectangle on its own plane (see
    /// <see cref="Raycast"/>), because adding colliders to the expo furniture would
    /// shadow the XRI ray interactors and break the PIN keypad and the OTP token.
    ///
    /// Used by the badge reader wall (Phase 2) and the presenter console (Phase 5).
    /// </summary>
    public class SpatialButton : MonoBehaviour
    {
        /// <summary>Button face size in metres, in the transform's local X/Y.</summary>
        public Vector2 size = new Vector2(0.2f, 0.08f);

        public Color normalColor = new Color(0.10f, 0.13f, 0.18f, 0.92f);
        public Color hoverColor = new Color(0.18f, 0.30f, 0.42f, 0.96f);
        public Color activeColor = new Color(0.12f, 0.45f, 0.30f, 0.96f);
        public Color disabledColor = new Color(0.10f, 0.10f, 0.12f, 0.55f);

        /// <summary>Raised on the trigger's rising edge while this button is hovered.</summary>
        public event Action<SpatialButton> OnClicked;

        /// <summary>Optional keyboard shortcut for in-editor testing (Key.None = none).</summary>
        public UnityEngine.InputSystem.Key shortcutKey = UnityEngine.InputSystem.Key.None;

        /// <summary>Shown as "on" (e.g. the selected preset) - tinted with activeColor.</summary>
        public bool isActive;

        /// <summary>Greyed out and not clickable.</summary>
        public bool interactable = true;

        /// <summary>Free-form tag so one handler can serve a whole panel.</summary>
        public string id;

        private Material _faceMat;
        private bool _hovered;
        private float _flashUntil;

        public bool IsHovered => _hovered;

        /// <summary>Attaches the coloured face this button tints. Called by the panel that builds it.</summary>
        public void BindFace(Material faceMaterial)
        {
            _faceMat = faceMaterial;
            ApplyColor();
        }

        private void OnEnable()
        {
            var system = SpatialButtonSystem.EnsureExists();
            if (system != null) system.Register(this);
            ApplyColor();
        }

        private void OnDisable()
        {
            _hovered = false;
            if (SpatialButtonSystem.Instance != null) SpatialButtonSystem.Instance.Unregister(this);
        }

        private void OnDestroy()
        {
            if (_faceMat != null) Destroy(_faceMat);
        }

        internal void SetHovered(bool hovered)
        {
            if (_hovered == hovered) return;
            _hovered = hovered;
            ApplyColor();
        }

        internal void Click()
        {
            if (!interactable) return;
            _flashUntil = Time.unscaledTime + 0.15f;
            ApplyColor();
            try { OnClicked?.Invoke(this); }
            catch (Exception e) { Debug.LogWarning($"[SpatialButton] handler for '{id}' threw: {e.Message}"); }
        }

        private void Update()
        {
            if (_flashUntil > 0f && Time.unscaledTime > _flashUntil)
            {
                _flashUntil = 0f;
                ApplyColor();
            }
        }

        /// <summary>Refreshes the tint after isActive / interactable changed.</summary>
        public void ApplyColor()
        {
            if (_faceMat == null) return;
            _faceMat.color = !interactable ? disabledColor
                           : _flashUntil > 0f ? Color.white
                           : isActive ? activeColor
                           : _hovered ? hoverColor
                           : normalColor;
        }

        /// <summary>
        /// Ray/rectangle intersection on this button's plane. Returns false when the
        /// ray is parallel to the face, hits from beyond <paramref name="maxDistance"/>,
        /// or lands outside the rectangle.
        /// </summary>
        public bool Raycast(Vector3 origin, Vector3 direction, float maxDistance, out Vector3 hitPoint, out float distance)
        {
            hitPoint = Vector3.zero;
            distance = 0f;

            Vector3 c = transform.position;
            Vector3 n = transform.forward;
            float denom = Vector3.Dot(direction, n);
            if (Mathf.Abs(denom) < 1e-5f) return false;   // ray runs along the face

            float t = Vector3.Dot(c - origin, n) / denom;
            if (t <= 0.02f || t > maxDistance) return false;

            Vector3 local = origin + direction * t - c;
            float u = Vector3.Dot(local, transform.right);
            float v = Vector3.Dot(local, transform.up);

            // The face is a quad scaled by `size` inside whatever scale its parents have.
            Vector3 s = transform.lossyScale;
            float halfW = 0.5f * size.x * (Mathf.Abs(s.x) < 1e-5f ? 1f : Mathf.Abs(s.x));
            float halfH = 0.5f * size.y * (Mathf.Abs(s.y) < 1e-5f ? 1f : Mathf.Abs(s.y));
            if (Mathf.Abs(u) > halfW || Mathf.Abs(v) > halfH) return false;

            hitPoint = origin + direction * t;
            distance = t;
            return true;
        }
    }
}
