using UnityEngine;
using BoardroomXR.Auth;
using BoardroomXR.Biometrics;

namespace BoardroomXR.UI
{
    /// <summary>
    /// Head-locked status readout inside the headset. Shows which MFA stage the
    /// user is on (the on-device test showed people pressing the keypad during
    /// the gesture stage because nothing said the stage had changed), the last
    /// MFA message, and - once logged in - the live continuous-biometric state,
    /// score and enrolment progress. On impostor lockout a red tint fills the view.
    ///
    /// Built entirely at runtime from built-in resources; no prefab or scene edit.
    /// </summary>
    public class BiometricStatusHUD : MonoBehaviour
    {
        public float distance = 1.4f;
        public Vector3 offset = new Vector3(0f, 0.36f, 0f);
        public float characterSize = 0.0085f;

        private TextMesh _text;
        private Transform _root;
        private GameObject _tint;
        private Material _tintMat;
        private MFACoordinator _mfa;
        private string _mfaMessage = "";
        private float _nextSearch;

        // Enrolment progress bar, built under the same head-locked root.
        private Transform _progressRoot, _progressFill;
        private Material _progressTrackMat, _progressFillMat;

        /// <summary>Extra line shown above everything else (mimicry attack banner).</summary>
        public static string Banner { get; set; } = "";

        private void Update()
        {
            if (_root == null) { if (Camera.main == null) return; Build(Camera.main.transform); }
            if (_mfa == null && Time.unscaledTime >= _nextSearch)
            {
                _nextSearch = Time.unscaledTime + 1f;
                _mfa = FindAnyObjectByType<MFACoordinator>();
                if (_mfa != null) _mfa.OnStatusMessage += m => _mfaMessage = m;
            }
            Refresh();
        }

        private void Build(Transform cam)
        {
            _root = new GameObject("BiometricHUD").transform;
            _root.SetParent(cam, false);
            _root.localPosition = new Vector3(offset.x, offset.y, distance);
            _root.localRotation = Quaternion.identity;

            var textGo = new GameObject("Text");
            textGo.transform.SetParent(_root, false);
            _text = textGo.AddComponent<TextMesh>();
            Font font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            _text.font = font;
            _text.GetComponent<MeshRenderer>().sharedMaterial = font.material;
            _text.fontSize = 64;
            _text.characterSize = characterSize;
            _text.anchor = TextAnchor.LowerCenter;
            _text.alignment = TextAlignment.Center;
            _text.richText = true;

            // Enrolment progress bar, just under the text block.
            var bar = new GameObject("EnrolProgress");
            bar.transform.SetParent(_root, false);
            bar.transform.localPosition = new Vector3(0f, -0.045f, 0f);
            _progressRoot = bar.transform;

            SpatialUiFactory.Quad(_progressRoot, "Track", Vector3.zero, new Vector2(0.44f, 0.018f),
                new Color(0.12f, 0.14f, 0.18f, 0.85f), out _progressTrackMat);

            var fillPivot = new GameObject("FillPivot");
            fillPivot.transform.SetParent(_progressRoot, false);
            fillPivot.transform.localPosition = new Vector3(-0.22f, 0f, -0.001f);
            var fill = new GameObject("Fill");
            fill.transform.SetParent(fillPivot.transform, false);
            fill.transform.localPosition = new Vector3(0.5f, 0f, 0f);   // grows from the left edge
            fill.transform.localScale = new Vector3(1f, 0.018f, 1f);
            fill.AddComponent<MeshFilter>().sharedMesh = SpatialUiFactory.QuadMesh;
            var fillMr = fill.AddComponent<MeshRenderer>();
            _progressFillMat = SpatialUiFactory.UnlitMaterial(new Color(0.45f, 0.78f, 1f, 0.95f));
            fillMr.sharedMaterial = _progressFillMat;
            fillMr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            fillPivot.transform.localScale = new Vector3(0.001f, 1f, 1f);
            _progressFill = fillPivot.transform;
            _progressRoot.gameObject.SetActive(false);

            // Red lockout tint: a quad just in front of the eyes.
            Shader sh = Shader.Find("Sprites/Default") ?? Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Unlit/Color");
            if (sh != null)
            {
                _tint = GameObject.CreatePrimitive(PrimitiveType.Quad);
                Destroy(_tint.GetComponent<Collider>());
                _tint.name = "LockoutTint";
                _tint.transform.SetParent(cam, false);
                _tint.transform.localPosition = new Vector3(0f, 0f, 0.35f);
                _tint.transform.localScale = new Vector3(1.2f, 1.2f, 1f);
                _tintMat = new Material(sh) { color = new Color(0.85f, 0f, 0f, 0.35f) };
                _tint.GetComponent<MeshRenderer>().sharedMaterial = _tintMat;
                _tint.SetActive(false);
            }
        }

        private void Refresh()
        {
            if (_text == null) return;
            var auth = ContinuousBiometricAuthenticator.Instance;
            var e = auth != null ? auth.Engine : null;
            var sb = new System.Text.StringBuilder();
            bool locked = false;
            bool enrolling = e != null && e.State == BiometricState.Enrolling;

            if (!string.IsNullOrEmpty(Banner))
                sb.Append("<color=#ff8c42><b>").Append(Banner).Append("</b></color>\n");

            if (_mfa != null && _mfa.CurrentStage != MFAStage.Passed)
            {
                sb.Append("<b>").Append(StageTitle(_mfa.CurrentStage)).Append("</b>\n");
                if (!string.IsNullOrEmpty(_mfaMessage)) sb.Append(_mfaMessage).Append('\n');
            }

            if (e != null)
            {
                switch (e.State)
                {
                    case BiometricState.Enrolling:
                        sb.Append("<color=#7fc8ff>Enrolling: Learning how you move: ")
                          .Append(Mathf.RoundToInt(e.EnrolProgress * 100f)).Append("%</color>\n")
                          .Append("Look around and use both controllers naturally.  ")
                          .Append(Mathf.CeilToInt(Mathf.Max(0f, e.Config.enrolSeconds - e.SecondsInPhase)))
                          .Append("s left");
                        break;
                    case BiometricState.Verified:
                        sb.Append("<color=#6fe38a>Identity verified continuously</color>  score ")
                          .Append(e.SmoothedScore.ToString("F2"));
                        break;
                    case BiometricState.Warning:
                        sb.Append("<color=#ffc04d>Movement does not match the enrolled user</color>  score ")
                          .Append(e.SmoothedScore.ToString("F2")).Append("  (")
                          .Append(e.Violations).Append('/').Append(e.Config.violationsForLockout).Append(")\n");
                        // The single biggest difference, in plain language, so the wearer
                        // can see what gave them away rather than just a falling number.
                        sb.Append("<color=#ffc04d>")
                          .Append(TopReasonText(e))
                          .Append("</color>");
                        break;
                    case BiometricState.LockedOut:
                        locked = _mfa == null || _mfa.CurrentStage != MFAStage.Passed;
                        sb.Append("<color=#ff5050><b>IMPOSTOR DETECTED - ACCESS LOCKED</b></color>\n")
                          .Append("Re-authenticate with PIN, gesture and OTP.");
                        break;
                }
            }

            _text.text = sb.ToString();
            if (_tint != null && _tint.activeSelf != locked) _tint.SetActive(locked);

            // Pulsing red vignette while locked out, so the revoke is impossible to miss
            // even if the wearer is looking away from the doors or the SOC panel.
            if (locked && _tintMat != null)
            {
                float a = 0.22f + 0.16f * Mathf.Sin(Time.unscaledTime * 6f);
                _tintMat.color = new Color(0.85f, 0f, 0f, a);
            }

            if (_progressRoot != null)
            {
                if (_progressRoot.gameObject.activeSelf != enrolling) _progressRoot.gameObject.SetActive(enrolling);
                if (enrolling && _progressFill != null)
                    _progressFill.localScale = new Vector3(Mathf.Max(0.001f, e.EnrolProgress), 1f, 1f);
            }
        }

        /// <summary>Largest deviation of the last scored window, phrased for a person.</summary>
        private static string TopReasonText(ContinuousBiometricEngine e)
        {
            if (!e.HasScoredWindow || e.Template == null) return "Checking movement...";
            var auth = ContinuousBiometricAuthenticator.Instance;
            string whose = auth != null
                ? BoardroomXR.Identity.BadgeDirectory.NameFor(auth.CurrentUserId)
                : "the enrolled user";
            string reason = FeatureExplainer.TopReason(e.LastFeatures, e.LastZ, e.Template, whose);
            return string.IsNullOrEmpty(reason) ? "Overall movement pattern differs." : reason;
        }

        private static string StageTitle(MFAStage s) => s switch
        {
            MFAStage.Pin => "Step 1 of 3: PIN",
            MFAStage.Gesture => "Step 2 of 3: Gesture (hold trigger while drawing)",
            MFAStage.Otp => "Step 3 of 3: One-time code",
            MFAStage.Failed => "Authentication failed",
            MFAStage.TimedOut => "Session timed out",
            _ => "Authentication",
        };

        private void OnDestroy()
        {
            if (_tintMat != null) Destroy(_tintMat);
            if (_progressTrackMat != null) Destroy(_progressTrackMat);
            if (_progressFillMat != null) Destroy(_progressFillMat);
        }
    }
}
