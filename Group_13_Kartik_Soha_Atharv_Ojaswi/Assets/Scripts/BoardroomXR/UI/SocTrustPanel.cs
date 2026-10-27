using System.Collections.Generic;
using System.Text;
using UnityEngine;
using BoardroomXR.Auth;
using BoardroomXR.Biometrics;
using BoardroomXR.Identity;
using BoardroomXR.Logging;

namespace BoardroomXR.UI
{
    /// <summary>
    /// The Security Operations wall panel, mounted to the user's right next to the
    /// confidential whiteboard. It is the explainability surface of the system: it
    /// shows not just whether the user is trusted but <i>why</i> the engine thinks so.
    ///
    ///   - identity, state, operating point, trust % and the violation counter (k of K)
    ///   - a live graph of the last 90 scored windows with the threshold line and
    ///     Warning / Locked shading
    ///   - 16 feature deviation bars (|z|, green / amber / red)
    ///   - the top three deviations in plain English, in real units
    ///   - a scrolling security event log
    ///
    /// Everything is built from quads, TextMesh and LineRenderers at runtime, with no
    /// colliders, so nothing here can block an XRI ray.
    /// </summary>
    public class SocTrustPanel : MonoBehaviour
    {
        // Room frame: (forward, right, height) from the XR Origin.
        public float forward = 2.4f;
        public float right = 1.3f;
        public float height = 1.6f;

        public Vector2 panelSize = new Vector2(1.2f, 0.8f);
        public int graphWindows = 90;

        private const float Layer = -0.004f;

        private Transform _root;
        private readonly List<Material> _materials = new();

        private TextMesh _identityText, _stateText, _stateDetailText, _trustText, _presetText, _violationText, _adaptText;
        private TextMesh[] _reasonText = new TextMesh[3];
        private TextMesh _logText;
        private LineRenderer _scoreLine, _thresholdLine;
        private Transform[] _bars = new Transform[KinematicFeatures.Count];
        private Material[] _barMats = new Material[KinematicFeatures.Count];
        private TextMesh[] _barValue = new TextMesh[KinematicFeatures.Count];
        private Material _warnShadeMat, _lockShadeMat;
        private Transform _graphArea;

        private Vector2 _graphSize;
        private AvatarIdentity _identity;
        private float _nextSearch, _nextSlowRefresh;

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

            RefreshFast();
            if (Time.unscaledTime >= _nextSlowRefresh)
            {
                _nextSlowRefresh = Time.unscaledTime + 0.25f;
                RefreshSlow();
            }
        }

        private void OnDestroy()
        {
            foreach (var m in _materials) if (m != null) Destroy(m);
            _materials.Clear();
        }

        // ---------------- construction ----------------

        private void Build()
        {
            Vector3 pos = XrRigLocator.Place(forward, right, height);
            var go = new GameObject("SocTrustPanel");
            go.transform.SetParent(transform, false);
            go.transform.position = pos;
            go.transform.rotation = XrRigLocator.FacingOrigin(pos, height);
            _root = go.transform;

            Track(SpatialUiFactory.Quad(_root, "Back", Vector3.zero, panelSize, new Color(0.04f, 0.05f, 0.07f, 0.97f), out var back), back);
            Track(SpatialUiFactory.Quad(_root, "Edge", new Vector3(0f, 0f, 0.002f), panelSize + new Vector2(0.018f, 0.018f),
                new Color(0.20f, 0.45f, 0.62f, 0.9f), out var edge), edge);

            float halfW = panelSize.x * 0.5f, halfH = panelSize.y * 0.5f;
            float left = -halfW + 0.03f;

            var title = SpatialUiFactory.Text(_root, "Title", new Vector3(left, halfH - 0.045f, Layer), 0.030f,
                new Color(0.70f, 0.88f, 1f), TextAnchor.MiddleLeft, TextAlignment.Left);
            title.text = "SECURITY OPERATIONS  ·  CONTINUOUS BEHAVIOURAL AUTHENTICATION";

            _identityText = SpatialUiFactory.Text(_root, "Identity", new Vector3(left, halfH - 0.090f, Layer), 0.024f,
                Color.white, TextAnchor.MiddleLeft, TextAlignment.Left);

            // ---- left column: the headline numbers ----
            _trustText = SpatialUiFactory.Text(_root, "Trust", new Vector3(left, halfH - 0.175f, Layer), 0.055f,
                Color.white, TextAnchor.MiddleLeft, TextAlignment.Left);
            _stateText = SpatialUiFactory.Text(_root, "State", new Vector3(left, halfH - 0.228f, Layer), 0.024f,
                Color.white, TextAnchor.MiddleLeft, TextAlignment.Left);
            _stateDetailText = SpatialUiFactory.Text(_root, "StateDetail", new Vector3(left, halfH - 0.258f, Layer), 0.0155f,
                new Color(0.70f, 0.78f, 0.86f), TextAnchor.MiddleLeft, TextAlignment.Left);
            _presetText = SpatialUiFactory.Text(_root, "Preset", new Vector3(left, halfH - 0.288f, Layer), 0.017f,
                new Color(0.65f, 0.75f, 0.85f), TextAnchor.UpperLeft, TextAlignment.Left);
            _presetText.lineSpacing = 1.2f;
            _violationText = SpatialUiFactory.Text(_root, "Violations", new Vector3(left, halfH - 0.330f, Layer), 0.017f,
                new Color(0.65f, 0.75f, 0.85f), TextAnchor.MiddleLeft, TextAlignment.Left);
            _adaptText = SpatialUiFactory.Text(_root, "Adapt", new Vector3(left, halfH - 0.358f, Layer), 0.0155f,
                new Color(0.55f, 0.65f, 0.75f), TextAnchor.UpperLeft, TextAlignment.Left);
            _adaptText.lineSpacing = 1.2f;

            // The graph lives entirely to the right of the status column (x >= 0.02),
            // so no status line can ever run underneath it.
            BuildGraph(new Vector3(0.30f, halfH - 0.235f, Layer), new Vector2(0.54f, 0.26f));
            BuildBars(new Vector3(left, -0.045f, Layer), 0.235f);
            BuildReasons(new Vector3(-0.02f, -0.045f, Layer), halfW);
            BuildEventLog(new Vector3(-0.02f, -0.185f, Layer));
        }

        private void BuildGraph(Vector3 centre, Vector2 size)
        {
            _graphSize = size;
            var area = new GameObject("Graph");
            area.transform.SetParent(_root, false);
            area.transform.localPosition = centre;
            _graphArea = area.transform;

            Track(SpatialUiFactory.Quad(_graphArea, "GraphBack", Vector3.zero, size, new Color(0.02f, 0.03f, 0.05f, 0.95f), out var gb), gb);

            // Warning band (below threshold) and lockout band (bottom), drawn as shading
            // so the graph reads at a glance from across the stall.
            Track(SpatialUiFactory.Quad(_graphArea, "WarnShade", new Vector3(0f, 0f, Layer * 0.5f),
                new Vector2(size.x, size.y * 0.3f), new Color(0.55f, 0.35f, 0.05f, 0.35f), out _warnShadeMat), _warnShadeMat);
            Track(SpatialUiFactory.Quad(_graphArea, "LockShade", new Vector3(0f, 0f, Layer * 0.6f),
                new Vector2(size.x, size.y * 0.1f), new Color(0.6f, 0.1f, 0.1f, 0.35f), out _lockShadeMat), _lockShadeMat);

            var label = SpatialUiFactory.Text(_graphArea, "GraphLabel",
                new Vector3(-size.x * 0.5f + 0.005f, size.y * 0.5f + 0.018f, Layer), 0.016f,
                new Color(0.6f, 0.7f, 0.8f), TextAnchor.MiddleLeft, TextAlignment.Left);
            label.text = $"TRUST SCORE  ·  last {graphWindows} windows";

            _thresholdLine = MakeLine("ThresholdLine", new Color(1f, 0.45f, 0.35f, 0.9f), 0.0035f, 2);
            _scoreLine = MakeLine("ScoreLine", new Color(0.45f, 0.9f, 1f, 1f), 0.005f, graphWindows);
        }

        private LineRenderer MakeLine(string name, Color color, float width, int points)
        {
            var go = new GameObject(name);
            go.transform.SetParent(_graphArea, false);
            go.transform.localPosition = new Vector3(0f, 0f, Layer);
            var lr = go.AddComponent<LineRenderer>();
            lr.useWorldSpace = false;
            lr.alignment = LineAlignment.TransformZ;   // flat on the panel, not billboarded
            lr.textureMode = LineTextureMode.Stretch;
            lr.numCapVertices = 0;
            lr.widthMultiplier = width;
            lr.positionCount = points;
            lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            lr.receiveShadows = false;
            var mat = SpatialUiFactory.UnlitMaterial(color);
            lr.sharedMaterial = mat;
            _materials.Add(mat);
            return lr;
        }

        private void BuildBars(Vector3 topLeft, float maxBarLength)
        {
            var holder = new GameObject("DeviationBars");
            holder.transform.SetParent(_root, false);
            holder.transform.localPosition = topLeft;

            var header = SpatialUiFactory.Text(holder.transform, "BarsHeader", new Vector3(0f, 0.022f, Layer), 0.017f,
                new Color(0.6f, 0.7f, 0.8f), TextAnchor.MiddleLeft, TextAlignment.Left);
            header.text = "FEATURE DEVIATION  |z|";

            const float rowH = 0.0215f;
            for (int i = 0; i < KinematicFeatures.Count; i++)
            {
                float y = -i * rowH;
                var label = SpatialUiFactory.Text(holder.transform, $"BarLabel{i}", new Vector3(0f, y, Layer), 0.0135f,
                    new Color(0.72f, 0.78f, 0.85f), TextAnchor.MiddleLeft, TextAlignment.Left);
                label.text = FeatureExplainer.Table[i].shortLabel;

                float trackX = 0.145f;
                Track(SpatialUiFactory.Quad(holder.transform, $"BarTrack{i}",
                    new Vector3(trackX + maxBarLength * 0.5f, y, Layer), new Vector2(maxBarLength, 0.011f),
                    new Color(0.12f, 0.14f, 0.17f, 0.9f), out var trackMat), trackMat);

                // Bars grow from the left: a unit-wide quad anchored at its left edge.
                var barGo = new GameObject($"Bar{i}");
                barGo.transform.SetParent(holder.transform, false);
                barGo.transform.localPosition = new Vector3(trackX, y, Layer * 2f);
                var pivot = barGo.transform;

                var fill = new GameObject("Fill");
                fill.transform.SetParent(pivot, false);
                fill.transform.localPosition = new Vector3(0.5f, 0f, 0f);  // left-anchored
                fill.transform.localScale = new Vector3(1f, 0.011f, 1f);
                fill.AddComponent<MeshFilter>().sharedMesh = SpatialUiFactory.QuadMesh;
                var mr = fill.AddComponent<MeshRenderer>();
                _barMats[i] = SpatialUiFactory.UnlitMaterial(FeatureExplainer.DeviationColor(0f));
                mr.sharedMaterial = _barMats[i];
                mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                _materials.Add(_barMats[i]);
                pivot.localScale = new Vector3(0.001f, 1f, 1f);
                _bars[i] = pivot;

                _barValue[i] = SpatialUiFactory.Text(holder.transform, $"BarValue{i}",
                    new Vector3(trackX + maxBarLength + 0.012f, y, Layer), 0.0125f,
                    new Color(0.6f, 0.66f, 0.72f), TextAnchor.MiddleLeft, TextAlignment.Left);
            }

            var scale = SpatialUiFactory.Text(holder.transform, "BarScale",
                new Vector3(0.145f, -KinematicFeatures.Count * rowH - 0.016f, Layer), 0.013f,
                new Color(0.5f, 0.56f, 0.62f), TextAnchor.MiddleLeft, TextAlignment.Left);
            scale.text = "0                    2                    4+ sigma";
        }

        private void BuildReasons(Vector3 topLeft, float halfW)
        {
            var holder = new GameObject("Reasons");
            holder.transform.SetParent(_root, false);
            holder.transform.localPosition = topLeft;

            var header = SpatialUiFactory.Text(holder.transform, "ReasonsHeader", new Vector3(0f, 0.022f, Layer), 0.017f,
                new Color(0.6f, 0.7f, 0.8f), TextAnchor.MiddleLeft, TextAlignment.Left);
            header.text = "WHY  ·  largest differences from the profile";

            for (int i = 0; i < _reasonText.Length; i++)
            {
                _reasonText[i] = SpatialUiFactory.Text(holder.transform, $"Reason{i}",
                    new Vector3(0f, -0.012f - i * 0.027f, Layer), 0.016f,
                    new Color(0.95f, 0.95f, 0.95f), TextAnchor.MiddleLeft, TextAlignment.Left);
                _reasonText[i].text = "";
            }
        }

        private void BuildEventLog(Vector3 topLeft)
        {
            var holder = new GameObject("EventLog");
            holder.transform.SetParent(_root, false);
            holder.transform.localPosition = topLeft;

            var header = SpatialUiFactory.Text(holder.transform, "LogHeader", new Vector3(0f, 0.022f, Layer), 0.017f,
                new Color(0.6f, 0.7f, 0.8f), TextAnchor.MiddleLeft, TextAlignment.Left);
            header.text = "SECURITY EVENT LOG";

            _logText = SpatialUiFactory.Text(holder.transform, "LogText", new Vector3(0f, -0.002f, Layer), 0.0135f,
                new Color(0.80f, 0.85f, 0.90f), TextAnchor.UpperLeft, TextAlignment.Left);
            _logText.lineSpacing = 1.15f;
            _logText.text = "";
        }

        // ---------------- live refresh ----------------

        private void RefreshFast()
        {
            var auth = ContinuousBiometricAuthenticator.Instance;
            var e = auth?.Engine;
            if (e == null || _trustText == null) return;

            float trust = e.State == BiometricState.Enrolling ? e.EnrolProgress : e.SmoothedScore;
            int pct = Mathf.RoundToInt(Mathf.Clamp01(trust) * 100f);
            Color stateColor = StateColor(e.State);

            _trustText.text = e.State == BiometricState.Enrolling
                ? $"ENROL {pct}%"
                : e.State == BiometricState.Idle ? "TRUST --" : $"TRUST {pct}%";
            _trustText.color = stateColor;

            _stateText.text = StateLabel(e.State);
            _stateText.color = stateColor;
            if (_stateDetailText != null)
            {
                _stateDetailText.text = StateDetail(e.State);
                _stateDetailText.color = stateColor * 0.85f;
            }
        }

        private void RefreshSlow()
        {
            var auth = ContinuousBiometricAuthenticator.Instance;
            var e = auth?.Engine;
            if (e == null || _identityText == null) return;

            string id = auth.CurrentUserId;
            var badge = BadgeDirectory.Find(id);
            bool verified = _identity != null && _identity.IsSpawned && _identity.IsVerified.Value;
            string suffix = verified ? "" : "  <color=#ff9090>(not verified)</color>";
            _identityText.text = badge != null
                ? $"{badge.name}  ·  {badge.role}  ·  {badge.id}{suffix}"
                : $"{id}{suffix}";

            _presetText.text = $"{e.Config.preset}  thr {e.Config.matchThreshold:F2}  k={e.Config.violationsForLockout}\n" +
                               $"window {e.Config.windowSeconds:F1}s / hop {e.Config.hopSeconds:F1}s";
            _violationText.text = $"Violations {e.Violations}/{e.Config.violationsForLockout}   lockouts {auth.LockoutCount}";
            _adaptText.text = e.Config.adaptiveTemplate
                ? $"Adaptive ON  ·  {e.AdaptationsThisSession} folded" +
                  (e.Template != null ? $"\n(template adapted {e.Template.adaptationCount}x total)" : "")
                : "Adaptive OFF\n(template frozen at enrolment)";

            UpdateGraph(e);
            UpdateBars(e);
            UpdateReasons(e, badge != null ? badge.name : id);
            UpdateEventLog();
        }

        private void UpdateGraph(ContinuousBiometricEngine e)
        {
            if (_scoreLine == null) return;

            float w = _graphSize.x, h = _graphSize.y;
            float x0 = -w * 0.5f, y0 = -h * 0.5f;

            // Threshold line and the shaded bands follow the live operating point.
            float thr = Mathf.Clamp01(e.Config.matchThreshold);
            _thresholdLine.positionCount = 2;
            _thresholdLine.SetPosition(0, new Vector3(x0, y0 + thr * h, 0f));
            _thresholdLine.SetPosition(1, new Vector3(x0 + w, y0 + thr * h, 0f));

            var warnShade = _graphArea.Find("WarnShade");
            if (warnShade != null)
            {
                warnShade.localScale = new Vector3(w, thr * h, 1f);
                warnShade.localPosition = new Vector3(0f, y0 + thr * h * 0.5f, Layer * 0.5f);
            }
            if (_lockShadeMat != null)
            {
                bool locked = e.State == BiometricState.LockedOut;
                _lockShadeMat.color = locked ? new Color(0.75f, 0.10f, 0.10f, 0.45f) : new Color(0.6f, 0.1f, 0.1f, 0.0f);
                var lockShade = _graphArea.Find("LockShade");
                if (lockShade != null)
                {
                    lockShade.localScale = new Vector3(w, h, 1f);
                    lockShade.localPosition = new Vector3(0f, 0f, Layer * 0.6f);
                }
            }

            var recent = BiometricHistory.Recent(graphWindows);
            if (recent.Count < 2)
            {
                _scoreLine.positionCount = 0;
                return;
            }

            _scoreLine.positionCount = recent.Count;
            for (int i = 0; i < recent.Count; i++)
            {
                float fx = recent.Count > 1 ? (float)i / (recent.Count - 1) : 0f;
                float fy = Mathf.Clamp01(recent[i].smoothed);
                _scoreLine.SetPosition(i, new Vector3(x0 + fx * w, y0 + fy * h, 0f));
            }

            var last = recent[recent.Count - 1];
            _scoreLine.startColor = _scoreLine.endColor = StateColor(last.state);
        }

        private void UpdateBars(ContinuousBiometricEngine e)
        {
            bool has = e.HasScoredWindow && e.Template != null;
            for (int i = 0; i < KinematicFeatures.Count; i++)
            {
                if (_bars[i] == null) continue;
                float absZ = has ? Mathf.Abs(e.LastZ[i]) : 0f;
                float frac = Mathf.Clamp01(absZ / 4f);
                _bars[i].localScale = new Vector3(Mathf.Max(0.001f, frac), 1f, 1f);
                if (_barMats[i] != null) _barMats[i].color = FeatureExplainer.DeviationColor(absZ);
                if (_barValue[i] != null) _barValue[i].text = has ? absZ.ToString("F1") : "-";
            }
        }

        private void UpdateReasons(ContinuousBiometricEngine e, string whose)
        {
            var reasons = e.HasScoredWindow
                ? FeatureExplainer.TopReasons(e.LastFeatures, e.LastZ, e.Template, whose, _reasonText.Length)
                : new List<FeatureExplainer.Reason>();

            for (int i = 0; i < _reasonText.Length; i++)
            {
                if (_reasonText[i] == null) continue;
                if (i < reasons.Count)
                {
                    _reasonText[i].text = $"{i + 1}. {reasons[i].text}  ({reasons[i].absZ:F1} sigma)";
                    _reasonText[i].color = FeatureExplainer.DeviationColor(reasons[i].absZ);
                }
                else
                {
                    _reasonText[i].text = i == 0 && e.State == BiometricState.Enrolling
                        ? "Learning this person's movement profile..."
                        : i == 0 ? "Movement matches the enrolled profile." : "";
                    _reasonText[i].color = new Color(0.6f, 0.7f, 0.8f);
                }
            }
        }

        private void UpdateEventLog()
        {
            if (_logText == null) return;
            var entries = BioEventLog.Recent(8);
            var sb = new StringBuilder();
            for (int i = entries.Count - 1; i >= 0; i--)   // newest first
            {
                var en = entries[i];
                sb.Append("<color=#").Append(EventColor(en.type)).Append('>')
                  .Append(en.clock).Append("  ").Append(en.type).Append("</color>");
                if (!string.IsNullOrEmpty(en.detail))
                    sb.Append("  ").Append(Truncate(en.detail, 40));
                sb.Append('\n');
            }
            if (entries.Count == 0) sb.Append("no events yet");
            _logText.text = sb.ToString();
        }

        // ---------------- helpers ----------------

        private static string EventColor(string type) => type switch
        {
            "lockout" => "ff6060",
            "gesture_fail" => "ffb060",
            "gesture_enrol_restart" => "ffb060",
            "mfa_pass" => "70ff90",
            "enrol_done" => "70ff90",
            "identity_switch" => "90d0ff",
            "preset_change" => "ffe070",
            _ => "c8d0d8",
        };

        private static string Truncate(string s, int max) =>
            string.IsNullOrEmpty(s) || s.Length <= max ? s : s.Substring(0, max - 1) + "...";

        private static Color StateColor(BiometricState s) => s switch
        {
            BiometricState.Verified => new Color(0.42f, 0.92f, 0.52f),
            BiometricState.Warning => new Color(1f, 0.74f, 0.25f),
            BiometricState.LockedOut => new Color(1f, 0.32f, 0.30f),
            BiometricState.Enrolling => new Color(0.50f, 0.78f, 1f),
            _ => new Color(0.65f, 0.70f, 0.75f),
        };

        private static string StateLabel(BiometricState s) => s switch
        {
            BiometricState.Verified => "VERIFIED",
            BiometricState.Warning => "WARNING",
            BiometricState.LockedOut => "LOCKED OUT",
            BiometricState.Enrolling => "ENROLLING",
            _ => "IDLE",
        };

        private static string StateDetail(BiometricState s) => s switch
        {
            BiometricState.Verified => "identity confirmed every 0.5 s",
            BiometricState.Warning => "movement does not match",
            BiometricState.LockedOut => "access revoked by the server",
            BiometricState.Enrolling => "learning how this person moves",
            _ => "awaiting MFA",
        };

        private void Track(MeshRenderer _, Material m)
        {
            if (m != null) _materials.Add(m);
        }
    }
}
