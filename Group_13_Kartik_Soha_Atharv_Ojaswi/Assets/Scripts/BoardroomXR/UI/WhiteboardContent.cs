using System.Collections.Generic;
using UnityEngine;

namespace BoardroomXR.UI
{
    /// <summary>
    /// Puts something genuinely confidential-looking on the ConfidentialWhiteboard,
    /// so hiding it means something to a visitor: a board memo for a fictional
    /// company, a small bar chart and a "BOARD EYES ONLY" banner.
    ///
    /// Two details matter here:
    ///  - the whiteboard cube is scaled non-uniformly (1.6, 0.9, 0.05), so the
    ///    content child cancels that scale and can then be laid out in metres;
    ///  - the cube's local +Z faces the user, and a Unity TextMesh reads correctly
    ///    only when its forward points AWAY from the viewer, so the content child is
    ///    rotated 180 degrees about Y.
    ///
    /// Every frame the children's renderers are synced to the whiteboard's own
    /// renderer, so <see cref="BoardroomXR.Network.BoardroomAccessGate"/> continues
    /// to hide all of it: the gate only re-walks the hierarchy when verification
    /// changes, and children created after that would otherwise stay visible.
    /// </summary>
    public class WhiteboardContent : MonoBehaviour
    {
        public string whiteboardName = "ConfidentialWhiteboard";

        private Renderer _boardRenderer;
        private Transform _content;
        private readonly List<Renderer> _childRenderers = new();
        private readonly List<Material> _materials = new();
        private float _nextSearch;

        private void Update()
        {
            if (_content == null)
            {
                if (Time.unscaledTime < _nextSearch) return;
                _nextSearch = Time.unscaledTime + 1f;
                TryBuild();
                return;
            }

            // Follow the gate: whatever the board's own renderer is doing, the content does.
            if (_boardRenderer == null) return;
            bool visible = _boardRenderer.enabled;
            for (int i = 0; i < _childRenderers.Count; i++)
            {
                var r = _childRenderers[i];
                if (r != null && r.enabled != visible) r.enabled = visible;
            }
        }

        private void OnDestroy() => BoardroomMaterials.DestroyAll(_materials);

        private void TryBuild()
        {
            GameObject board = GameObject.Find(whiteboardName);
            if (board == null) return;
            _boardRenderer = board.GetComponent<Renderer>();
            if (_boardRenderer == null) return;

            Vector3 s = board.transform.localScale;
            if (Mathf.Abs(s.x) < 1e-4f || Mathf.Abs(s.y) < 1e-4f || Mathf.Abs(s.z) < 1e-4f) return;

            var go = new GameObject("ConfidentialContent");
            go.transform.SetParent(board.transform, false);
            // Just proud of the +Z face (the one facing the user), then un-scaled and
            // turned to face the reader.
            go.transform.localPosition = new Vector3(0f, 0f, 0.55f);
            go.transform.localRotation = Quaternion.Euler(0f, 180f, 0f);
            go.transform.localScale = new Vector3(1f / s.x, 1f / s.y, 1f / s.z);
            _content = go.transform;

            BuildMemo(_content);

            _childRenderers.Clear();
            _childRenderers.AddRange(go.GetComponentsInChildren<Renderer>(true));
            // Start in whatever state the board is in, which before verification is hidden.
            foreach (var r in _childRenderers) r.enabled = _boardRenderer.enabled;

            SpatialUiFactory.StripColliders(go);
            Debug.Log($"[BoardroomXR] Whiteboard content attached to {board.name} " +
                      $"({_childRenderers.Count} renderers, scale compensation {go.transform.localScale}).");
        }

        /// <summary>
        /// Laid out in metres on the 1.6 x 0.9 m board face: x in [-0.8, 0.8],
        /// y in [-0.45, 0.45]. All figures below are invented for the demo.
        /// </summary>
        private void BuildMemo(Transform root)
        {
            Track(SpatialUiFactory.Quad(root, "Paper", new Vector3(0f, 0f, -0.002f), new Vector2(1.56f, 0.86f),
                new Color(0.97f, 0.96f, 0.93f, 1f), out var paper), paper);
            Track(SpatialUiFactory.Quad(root, "HeaderBar", new Vector3(0f, 0.355f, -0.004f), new Vector2(1.56f, 0.10f),
                new Color(0.55f, 0.08f, 0.10f, 1f), out var header), header);

            var banner = SpatialUiFactory.Text(root, "Banner", new Vector3(0f, 0.355f, -0.008f), 0.058f, Color.white);
            banner.text = "CONFIDENTIAL  ·  BOARD EYES ONLY";

            var title = SpatialUiFactory.Text(root, "Title", new Vector3(-0.74f, 0.255f, -0.008f), 0.050f,
                new Color(0.10f, 0.12f, 0.16f), TextAnchor.MiddleLeft, TextAlignment.Left);
            title.text = "PROJECT NORTHWIND - PROPOSED ACQUISITION";

            var body = SpatialUiFactory.Text(root, "Body", new Vector3(-0.74f, 0.165f, -0.008f), 0.030f,
                new Color(0.15f, 0.17f, 0.21f), TextAnchor.UpperLeft, TextAlignment.Left);
            body.lineSpacing = 1.25f;
            body.text =
                "Target:        Helios Analytics Ltd (fictional)\n" +
                "Consideration: GBP 184m, 70% cash / 30% equity\n" +
                "Premium:       31% over 90-day VWAP\n" +
                "Synergies:     GBP 22m run-rate by FY+2\n" +
                "Board vote:    scheduled, this session\n" +
                "Circulation:   directors only - do not forward";

            var chartLabel = SpatialUiFactory.Text(root, "ChartLabel", new Vector3(0.10f, 0.165f, -0.008f), 0.026f,
                new Color(0.30f, 0.33f, 0.38f), TextAnchor.MiddleLeft, TextAlignment.Left);
            chartLabel.text = "Synergy realisation (GBP m)";

            // Bar chart: invented figures, drawn from quads.
            float[] bars = { 4f, 9f, 14f, 18f, 22f };
            string[] years = { "FY0", "FY1", "FY2", "FY3", "FY4" };
            float baseY = -0.32f, maxH = 0.36f, x0 = 0.16f, step = 0.13f;

            Track(SpatialUiFactory.Quad(root, "Axis", new Vector3(x0 + step * 2f, baseY - 0.006f, -0.006f),
                new Vector2(step * 5.2f, 0.004f), new Color(0.35f, 0.38f, 0.42f, 1f), out var axis), axis);

            for (int i = 0; i < bars.Length; i++)
            {
                float h = maxH * bars[i] / 22f;
                Track(SpatialUiFactory.Quad(root, $"Bar{i}", new Vector3(x0 + i * step, baseY + h * 0.5f, -0.006f),
                    new Vector2(0.075f, h), new Color(0.16f, 0.36f, 0.62f, 1f), out var bar), bar);

                var v = SpatialUiFactory.Text(root, $"BarValue{i}", new Vector3(x0 + i * step, baseY + h + 0.025f, -0.008f),
                    0.022f, new Color(0.20f, 0.24f, 0.30f));
                v.text = bars[i].ToString("F0");

                var y = SpatialUiFactory.Text(root, $"BarYear{i}", new Vector3(x0 + i * step, baseY - 0.030f, -0.008f),
                    0.020f, new Color(0.35f, 0.38f, 0.44f));
                y.text = years[i];
            }

            var footer = SpatialUiFactory.Text(root, "Footer", new Vector3(-0.74f, -0.395f, -0.008f), 0.022f,
                new Color(0.45f, 0.10f, 0.12f), TextAnchor.MiddleLeft, TextAlignment.Left);
            footer.text = "Visible only while continuous behavioural authentication holds. Fictional company, fictional figures.";
        }

        private void Track(MeshRenderer _, Material m)
        {
            if (m != null) _materials.Add(m);
        }
    }
}
