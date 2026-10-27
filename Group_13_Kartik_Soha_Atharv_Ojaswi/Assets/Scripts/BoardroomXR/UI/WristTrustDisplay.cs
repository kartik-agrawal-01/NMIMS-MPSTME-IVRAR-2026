using System.Collections.Generic;
using UnityEngine;
using BoardroomXR.Biometrics;

namespace BoardroomXR.UI
{
    /// <summary>
    /// A small trust readout on the left controller, like a security badge on a
    /// wrist strap: trust %, the state colour and a 30-window sparkline. It means a
    /// wearer can check their own standing without turning to the SOC wall panel.
    ///
    /// Parented to the left controller, tilted back towards the face the way a watch
    /// sits. No colliders.
    /// </summary>
    public class WristTrustDisplay : MonoBehaviour
    {
        public Vector3 localOffset = new Vector3(0f, 0.045f, -0.055f);
        public Vector3 localEuler = new Vector3(55f, 0f, 0f);
        public Vector2 size = new Vector2(0.10f, 0.062f);
        public int sparkWindows = 30;

        private const float Layer = -0.002f;

        private Transform _root;
        private TextMesh _trust, _state;
        private LineRenderer _spark;
        private Material _panelMat, _edgeMat, _sparkMat;
        private float _nextSearch, _nextRefresh;

        private void Update()
        {
            if (_root == null)
            {
                var hand = XrRigLocator.LeftController;
                if (hand == null)
                {
                    if (Time.unscaledTime < _nextSearch) return;
                    _nextSearch = Time.unscaledTime + 1f;
                    XrRigLocator.Refresh(true);
                    return;
                }
                Build(hand);
            }

            // The controller can be re-created (tracking loss, scene reload).
            var current = XrRigLocator.LeftController;
            if (current != null && _root.parent != current) _root.SetParent(current, false);

            if (Time.unscaledTime < _nextRefresh) return;
            _nextRefresh = Time.unscaledTime + 0.2f;
            Refresh();
        }

        private void OnDestroy()
        {
            if (_panelMat != null) Destroy(_panelMat);
            if (_edgeMat != null) Destroy(_edgeMat);
            if (_sparkMat != null) Destroy(_sparkMat);
        }

        private void Build(Transform hand)
        {
            var go = new GameObject("WristTrustDisplay");
            go.transform.SetParent(hand, false);
            go.transform.localPosition = localOffset;
            go.transform.localRotation = Quaternion.Euler(localEuler);
            _root = go.transform;

            SpatialUiFactory.Quad(_root, "Edge", new Vector3(0f, 0f, 0.001f), size + new Vector2(0.006f, 0.006f),
                new Color(0.25f, 0.55f, 0.75f, 0.9f), out _edgeMat);
            SpatialUiFactory.Quad(_root, "Panel", Vector3.zero, size, new Color(0.04f, 0.06f, 0.09f, 0.95f), out _panelMat);

            _trust = SpatialUiFactory.Text(_root, "Trust", new Vector3(-size.x * 0.5f + 0.008f, size.y * 0.5f - 0.014f, Layer),
                0.019f, Color.white, TextAnchor.MiddleLeft, TextAlignment.Left);
            _state = SpatialUiFactory.Text(_root, "State", new Vector3(-size.x * 0.5f + 0.008f, size.y * 0.5f - 0.032f, Layer),
                0.011f, Color.white, TextAnchor.MiddleLeft, TextAlignment.Left);

            var sparkGo = new GameObject("Spark");
            sparkGo.transform.SetParent(_root, false);
            sparkGo.transform.localPosition = new Vector3(0f, -size.y * 0.5f + 0.012f, Layer);
            _spark = sparkGo.AddComponent<LineRenderer>();
            _spark.useWorldSpace = false;
            _spark.alignment = LineAlignment.TransformZ;
            _spark.widthMultiplier = 0.0022f;
            _spark.numCapVertices = 0;
            _spark.positionCount = 0;
            _spark.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            _sparkMat = SpatialUiFactory.UnlitMaterial(new Color(0.45f, 0.9f, 1f));
            _spark.sharedMaterial = _sparkMat;
        }

        private void Refresh()
        {
            var auth = ContinuousBiometricAuthenticator.Instance;
            var e = auth?.Engine;
            if (e == null || _trust == null) return;

            Color c = StateColor(e.State);
            if (e.State == BiometricState.Enrolling)
            {
                _trust.text = $"{Mathf.RoundToInt(e.EnrolProgress * 100f)}%";
                _state.text = "ENROLLING";
            }
            else if (e.State == BiometricState.Idle)
            {
                _trust.text = "--";
                _state.text = "NOT SIGNED IN";
            }
            else
            {
                _trust.text = $"{Mathf.RoundToInt(Mathf.Clamp01(e.SmoothedScore) * 100f)}%";
                _state.text = e.State == BiometricState.LockedOut ? "LOCKED OUT"
                            : e.State == BiometricState.Warning ? $"WARNING {e.Violations}/{e.Config.violationsForLockout}"
                            : "VERIFIED";
            }
            _trust.color = c;
            _state.color = c;

            List<float> scores = BiometricHistory.RecentSmoothed(sparkWindows);
            if (scores.Count < 2) { _spark.positionCount = 0; return; }

            float w = size.x - 0.016f, h = 0.016f;
            _spark.positionCount = scores.Count;
            for (int i = 0; i < scores.Count; i++)
            {
                float fx = -w * 0.5f + w * i / (scores.Count - 1);
                float fy = Mathf.Clamp01(scores[i]) * h;
                _spark.SetPosition(i, new Vector3(fx, fy, 0f));
            }
            if (_sparkMat != null) _sparkMat.color = c;
        }

        private static Color StateColor(BiometricState s) => s switch
        {
            BiometricState.Verified => new Color(0.42f, 0.92f, 0.52f),
            BiometricState.Warning => new Color(1f, 0.74f, 0.25f),
            BiometricState.LockedOut => new Color(1f, 0.32f, 0.30f),
            BiometricState.Enrolling => new Color(0.50f, 0.78f, 1f),
            _ => new Color(0.65f, 0.70f, 0.75f),
        };
    }
}
