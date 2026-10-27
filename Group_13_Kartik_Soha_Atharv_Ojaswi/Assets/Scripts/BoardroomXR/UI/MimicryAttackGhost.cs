using System.Collections.Generic;
using UnityEngine;
using BoardroomXR.Biometrics;
using BoardroomXR.Identity;
using BoardroomXR.Logging;

namespace BoardroomXR.UI
{
    /// <summary>
    /// The mimicry attack scenario. A translucent ghost avatar - head with visor,
    /// torso, two hands and the victim's badge name floating above - stands in front
    /// of the current user, facing away, and replays the motion that was recorded
    /// while the victim enrolled.
    ///
    /// The impostor is logged in with the victim's badge (stolen credentials) and
    /// tries to copy the ghost while the engine keeps scoring them. Nothing about the
    /// engine changes for this: if the mimic survives, that is an honest result about
    /// the method's resistance to mimicry, and it is logged as one.
    /// </summary>
    public class MimicryAttackGhost : MonoBehaviour
    {
        public static MimicryAttackGhost Instance { get; private set; }

        public float standOffMetres = 1.2f;
        public float playbackSpeed = 1f;

        public bool IsRunning { get; private set; }
        public string VictimId { get; private set; }

        private Transform _root, _head, _torso, _leftHand, _rightHand;
        private TextMesh _nameLabel;
        private readonly List<Material> _materials = new();
        private MimicryRecording _recording;
        private float _playhead;
        private int _cursor;
        private float _startedAt;

        private void Awake() => Instance = this;

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
            BoardroomMaterials.DestroyAll(_materials);
            if (BiometricStatusHUD.Banner == BannerText) BiometricStatusHUD.Banner = "";
        }

        private const string BannerText = "MIMICRY ATTACK IN PROGRESS - copy the ghost";

        /// <summary>
        /// Starts the replay for whichever badge is currently presented. Returns false
        /// (and says why) when that badge has no recording yet - i.e. nobody has
        /// enrolled as them on this headset.
        /// </summary>
        public bool Begin(string victimId)
        {
            if (string.IsNullOrEmpty(victimId)) return false;

            _recording = MimicryRecording.Load(victimId);
            if (_recording == null)
            {
                SessionLogger.LogGlobal($"[Mimicry] No reference recording for {victimId}. " +
                                        "Enrol as that badge first (the recording is captured during enrolment).");
                BioEventLog.Emit("mimicry_unavailable", victimId, "no reference recording; enrol as this badge first");
                return false;
            }

            VictimId = victimId;
            if (_root == null) Build();
            Place();
            _playhead = 0f;
            _cursor = 0;
            _startedAt = Time.unscaledTime;
            IsRunning = true;
            _root.gameObject.SetActive(true);
            BiometricStatusHUD.Banner = BannerText;

            var badge = BadgeDirectory.Find(victimId);
            if (_nameLabel != null)
                _nameLabel.text = $"{(badge != null ? badge.name : victimId)}  ·  MIMICRY TARGET";

            BioEventLog.Emit("mimicry_start", victimId,
                $"replaying {_recording.frames.Count} frames / {_recording.DurationSeconds:F1}s at {playbackSpeed:F1}x");
            return true;
        }

        public void Stop()
        {
            if (!IsRunning) return;
            IsRunning = false;
            if (_root != null) _root.gameObject.SetActive(false);
            if (BiometricStatusHUD.Banner == BannerText) BiometricStatusHUD.Banner = "";

            var auth = ContinuousBiometricAuthenticator.Instance;
            string outcome = auth?.Engine != null
                ? $"engine state at stop: {auth.Engine.State}, score {auth.Engine.SmoothedScore:F2}"
                : "engine not running";
            BioEventLog.Emit("mimicry_stop", VictimId, $"{Time.unscaledTime - _startedAt:F0}s attempted; {outcome}");
        }

        /// <summary>Starts the attack, or stops it if it is already running.</summary>
        public bool Toggle(string victimId)
        {
            if (IsRunning) { Stop(); return false; }
            return Begin(victimId);
        }

        private void Update()
        {
            if (!IsRunning || _recording == null || _root == null) return;

            // The ghost keeps its stand-off from the user, so they can turn around and
            // still see what they are supposed to copy.
            Place();

            _playhead += Time.unscaledDeltaTime * Mathf.Max(0.1f, playbackSpeed);
            float duration = Mathf.Max(0.1f, _recording.DurationSeconds);
            if (_playhead > duration) { _playhead -= duration; _cursor = 0; }

            var frames = _recording.frames;
            while (_cursor < frames.Count - 2 && frames[_cursor + 1].t - frames[0].t < _playhead) _cursor++;

            var a = frames[_cursor];
            var b = frames[Mathf.Min(_cursor + 1, frames.Count - 1)];
            float span = Mathf.Max(1e-4f, b.t - a.t);
            float k = Mathf.Clamp01((_playhead - (a.t - frames[0].t)) / span);

            Vector3 head = Vector3.Lerp(a.head, b.head, k);
            Quaternion headRot = Quaternion.Slerp(a.headRot, b.headRot, k);
            Vector3 left = Vector3.Lerp(a.left, b.left, k);
            Vector3 right = Vector3.Lerp(a.right, b.right, k);

            _head.localPosition = head;
            _head.localRotation = headRot;
            _leftHand.localPosition = left;
            _rightHand.localPosition = right;
            _torso.localPosition = head + new Vector3(0f, -0.42f, 0f);
            _torso.localRotation = Quaternion.Euler(0f, headRot.eulerAngles.y, 0f);
            if (_nameLabel != null)
            {
                _nameLabel.transform.localPosition = head + new Vector3(0f, 0.35f, 0f);
                // The label reads correctly when its forward points away from the user.
                _nameLabel.transform.rotation = XrRigLocator.FacingUser(_nameLabel.transform.position);
            }
        }

        /// <summary>Stands the ghost in front of the user at eye height, facing away from them.</summary>
        private void Place()
        {
            Transform userHead = XrRigLocator.Head;
            Vector3 origin;
            Quaternion rot;

            if (userHead != null)
            {
                Vector3 fwd = userHead.forward; fwd.y = 0f;
                if (fwd.sqrMagnitude < 1e-4f) fwd = XrRigLocator.Forward;
                fwd.Normalize();
                origin = userHead.position + fwd * standOffMetres;
                rot = Quaternion.LookRotation(fwd, Vector3.up);   // facing away from the user
            }
            else
            {
                origin = XrRigLocator.Place(standOffMetres, 0f, 1.6f);
                rot = Quaternion.LookRotation(XrRigLocator.Forward, Vector3.up);
            }

            _root.position = origin;
            _root.rotation = rot;
        }

        private void Build()
        {
            var go = new GameObject("MimicryGhost");
            go.transform.SetParent(transform, false);
            _root = go.transform;

            var skin = Track(BoardroomMaterials.Translucent(new Color(0.45f, 0.85f, 1f, 0.33f)));
            var visor = Track(BoardroomMaterials.Translucent(new Color(0.10f, 0.15f, 0.25f, 0.70f)));
            var hands = Track(BoardroomMaterials.Translucent(new Color(0.70f, 0.95f, 1f, 0.45f)));

            var headGo = SpatialUiFactory.Primitive(PrimitiveType.Cube, _root, "GhostHead", Vector3.zero,
                new Vector3(0.20f, 0.24f, 0.22f), Quaternion.identity, skin);
            _head = headGo.transform;
            // Visor on the front of the head, so the facing direction is obvious.
            SpatialUiFactory.Primitive(PrimitiveType.Cube, _head, "Visor", new Vector3(0f, 0.05f, 0.52f),
                new Vector3(0.9f, 0.35f, 0.08f), Quaternion.identity, visor);

            var torsoGo = SpatialUiFactory.Primitive(PrimitiveType.Capsule, _root, "GhostTorso", Vector3.zero,
                new Vector3(0.34f, 0.34f, 0.26f), Quaternion.identity, skin);
            _torso = torsoGo.transform;

            _leftHand = SpatialUiFactory.Primitive(PrimitiveType.Sphere, _root, "GhostHandL", Vector3.zero,
                new Vector3(0.09f, 0.09f, 0.09f), Quaternion.identity, hands).transform;
            _rightHand = SpatialUiFactory.Primitive(PrimitiveType.Sphere, _root, "GhostHandR", Vector3.zero,
                new Vector3(0.09f, 0.09f, 0.09f), Quaternion.identity, hands).transform;

            _nameLabel = SpatialUiFactory.Text(_root, "GhostName", new Vector3(0f, 0.35f, 0f), 0.055f,
                new Color(1f, 0.75f, 0.45f));

            SpatialUiFactory.StripColliders(go);
            go.SetActive(false);
        }

        private Material Track(Material m)
        {
            if (m != null) _materials.Add(m);
            return m;
        }
    }
}
