using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using BoardroomXR.Biometrics;
using BoardroomXR.Auth;
using BoardroomXR.Identity;

namespace BoardroomXR.UI
{
    /// <summary>
    /// The badge reader wall on the user's left: six employee badges that can be
    /// presented to sign in as that person. Built at runtime from quads and
    /// TextMesh, with no colliders - selection goes through
    /// <see cref="SpatialButtonSystem"/>, which raycasts in code.
    ///
    /// Each badge shows its live profile state, so an examiner can see at a glance
    /// which identities already have a signature and a behavioural template.
    /// </summary>
    public class BadgeReaderWall : MonoBehaviour
    {
        // Layout, in the measured room frame: (forward, right, height) from the XR Origin.
        public float forward = 0.9f;
        public float right = -0.75f;
        public float height = 1.2f;

        public Vector2 panelSize = new Vector2(0.98f, 0.82f);
        public Vector2 badgeSize = new Vector2(0.45f, 0.19f);

        private const float LayerStep = -0.004f;   // local -Z is towards the user

        private class Card
        {
            public BadgeDirectory.Badge badge;
            public SpatialButton button;
            public TextMesh status;
        }

        private readonly List<Card> _cards = new();
        private readonly List<Material> _materials = new();
        private Transform _root;
        private AvatarIdentity _identity;
        private float _nextRefresh;
        private float _nextIdentitySearch;

        private void Update()
        {
            if (_root == null)
            {
                // Wait until the rig exists so the wall lands in the room frame.
                if (XrRigLocator.Origin == null && Camera.main == null) return;
                Build();
            }

            if (_identity == null && Time.unscaledTime >= _nextIdentitySearch)
            {
                _nextIdentitySearch = Time.unscaledTime + 1f;
                _identity = FindAnyObjectByType<AvatarIdentity>(FindObjectsInactive.Include);
            }

            if (Time.unscaledTime >= _nextRefresh)
            {
                _nextRefresh = Time.unscaledTime + 1.5f;
                RefreshStatuses();
            }
        }

        private void OnDestroy()
        {
            foreach (var m in _materials) if (m != null) Destroy(m);
            _materials.Clear();
        }

        private void Build()
        {
            Vector3 pos = XrRigLocator.Place(forward, right, height);
            var go = new GameObject("BadgeReaderWall");
            go.transform.SetParent(transform, false);
            go.transform.position = pos;
            go.transform.rotation = XrRigLocator.FacingOrigin(pos, height);
            _root = go.transform;

            // Backing panel + a thin bright edge, so it reads as a wall-mounted device.
            Track(SpatialUiFactory.Quad(_root, "Panel", Vector3.zero, panelSize,
                new Color(0.06f, 0.08f, 0.11f, 0.96f), out var panelMat), panelMat);
            Track(SpatialUiFactory.Quad(_root, "PanelEdge", new Vector3(0f, 0f, 0.002f),
                panelSize + new Vector2(0.015f, 0.015f), new Color(0.25f, 0.55f, 0.75f, 0.9f), out var edgeMat), edgeMat);

            float top = panelSize.y * 0.5f;
            var title = SpatialUiFactory.Text(_root, "Title", new Vector3(0f, top - 0.055f, LayerStep), 0.034f,
                new Color(0.75f, 0.90f, 1f));
            title.text = "BADGE READER";

            var subtitle = SpatialUiFactory.Text(_root, "Subtitle", new Vector3(0f, top - 0.105f, LayerStep), 0.019f,
                new Color(0.55f, 0.65f, 0.75f));
            subtitle.text = "Point and pull the trigger to present a badge  (editor: keys 1-6)";

            // 2 columns x 3 rows.
            float gridTop = top - 0.165f;
            for (int i = 0; i < BadgeDirectory.Badges.Count; i++)
            {
                int col = i % 2, row = i / 2;
                float x = (col == 0 ? -1f : 1f) * (badgeSize.x * 0.5f + 0.012f);
                float y = gridTop - badgeSize.y * 0.5f - row * (badgeSize.y + 0.016f);
                _cards.Add(BuildCard(BadgeDirectory.Badges[i], new Vector3(x, y, LayerStep), i));
            }

            RefreshStatuses();
        }

        private Card BuildCard(BadgeDirectory.Badge badge, Vector3 localPos, int index)
        {
            var cardGo = new GameObject($"Badge_{badge.id}");
            cardGo.transform.SetParent(_root, false);
            cardGo.transform.localPosition = localPos;
            cardGo.transform.localRotation = Quaternion.identity;

            // The button's own transform is the face plane the code raycast uses.
            var button = cardGo.AddComponent<SpatialButton>();
            button.size = badgeSize;
            button.id = badge.id;
            // Keys 1-6 pick badges in the editor, where there is no controller.
            button.shortcutKey = index < 9 ? (Key)((int)Key.Digit1 + index) : Key.None;

            Track(SpatialUiFactory.Quad(cardGo.transform, "Face", Vector3.zero, badgeSize,
                button.normalColor, out var faceMat), faceMat);
            button.BindFace(faceMat);

            // A photo placeholder square on the left, like a real pass.
            Track(SpatialUiFactory.Quad(cardGo.transform, "Photo", new Vector3(-badgeSize.x * 0.5f + 0.055f, 0.012f, LayerStep),
                new Vector2(0.07f, 0.09f), new Color(0.22f, 0.26f, 0.32f, 1f), out var photoMat), photoMat);

            float textLeft = -badgeSize.x * 0.5f + 0.105f;
            var name = SpatialUiFactory.Text(cardGo.transform, "Name", new Vector3(textLeft, 0.050f, LayerStep * 2f),
                0.028f, Color.white, TextAnchor.MiddleLeft, TextAlignment.Left);
            name.text = badge.name;

            var role = SpatialUiFactory.Text(cardGo.transform, "Role", new Vector3(textLeft, 0.014f, LayerStep * 2f),
                0.019f, new Color(0.70f, 0.80f, 0.90f), TextAnchor.MiddleLeft, TextAlignment.Left);
            role.text = $"{badge.role}  ·  {badge.id}";

            var status = SpatialUiFactory.Text(cardGo.transform, "Status", new Vector3(textLeft, -0.045f, LayerStep * 2f),
                0.0165f, new Color(0.6f, 0.6f, 0.6f), TextAnchor.MiddleLeft, TextAlignment.Left);
            status.text = "";

            var card = new Card { badge = badge, button = button, status = status };
            button.OnClicked += _ => Present(card);
            return card;
        }

        private void Present(Card card)
        {
            BadgeSwitchService.Select(card.badge.id);
            RefreshStatuses();
        }

        /// <summary>
        /// Live per-badge profile state. Read straight from the two template stores,
        /// so what the wall says is what the authenticators will actually use.
        /// </summary>
        private void RefreshStatuses()
        {
            string current = _identity != null ? _identity.ClaimedUserIdString : BadgeSwitchService.CurrentBadgeId;
            bool verified = _identity != null && _identity.IsSpawned && _identity.IsVerified.Value;

            foreach (var card in _cards)
            {
                if (card.status == null) continue;
                bool isCurrent = card.badge.id == current;

                var bio = BiometricTemplate.Load(card.badge.id);
                bool hasGesture = GestureTemplateStore.HasTemplate(card.badge.id);

                string text;
                Color color;
                if (bio != null)
                {
                    text = $"Biometric profile: {bio.windowCount} windows, adapted {bio.adaptationCount}x";
                    color = new Color(0.45f, 0.80f, 1f);
                }
                else if (hasGesture)
                {
                    text = "Gesture enrolled";
                    color = new Color(1f, 0.78f, 0.35f);
                }
                else
                {
                    text = "No profile";
                    color = new Color(0.55f, 0.55f, 0.58f);
                }

                if (isCurrent)
                {
                    text = (verified ? "CURRENTLY SIGNED IN  ·  " : "SELECTED  ·  ") + text;
                    if (verified) color = new Color(0.45f, 0.95f, 0.55f);
                }

                card.status.text = text;
                card.status.color = color;
                card.button.isActive = isCurrent;
                card.button.ApplyColor();
            }
        }

        private void Track(MeshRenderer _, Material m)
        {
            if (m != null) _materials.Add(m);
        }
    }
}
