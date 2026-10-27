using UnityEngine;
using BoardroomXR.Biometrics;

namespace BoardroomXR.UI
{
    /// <summary>
    /// The moment the server revokes verification, the confidential whiteboard
    /// appears to shred: a burst of paper-coloured particles at the board, created
    /// entirely in code (no particle asset, no texture).
    ///
    /// The board itself is hidden by <see cref="BoardroomXR.Network.BoardroomAccessGate"/>;
    /// this just makes the moment legible from across the stall. The red vignette
    /// lives on the head-locked HUD, which already owns the tint quad.
    /// </summary>
    public class LockoutEffects : MonoBehaviour
    {
        public string whiteboardName = "ConfidentialWhiteboard";
        public int burstCount = 160;

        private ParticleSystem _shred;
        private Material _particleMat;
        private ContinuousBiometricEngine _bound;
        private float _nextSearch;

        private void Update()
        {
            if (Time.unscaledTime < _nextSearch) return;
            _nextSearch = Time.unscaledTime + 1f;

            if (_shred == null) TryBuild();

            var engine = ContinuousBiometricAuthenticator.Instance?.Engine;
            if (engine != null && engine != _bound)
            {
                if (_bound != null) _bound.OnLockout -= HandleLockout;
                _bound = engine;
                _bound.OnLockout += HandleLockout;
            }
        }

        private void OnDestroy()
        {
            if (_bound != null) _bound.OnLockout -= HandleLockout;
            if (_particleMat != null) Destroy(_particleMat);
        }

        private void TryBuild()
        {
            var board = GameObject.Find(whiteboardName);
            if (board == null) return;

            var go = new GameObject("WhiteboardShred");
            go.transform.SetParent(transform, false);
            go.transform.position = board.transform.position;

            _shred = go.AddComponent<ParticleSystem>();
            var main = _shred.main;
            main.duration = 1.2f;
            main.loop = false;
            main.playOnAwake = false;
            main.startLifetime = new ParticleSystem.MinMaxCurve(0.8f, 2.0f);
            main.startSpeed = new ParticleSystem.MinMaxCurve(0.4f, 2.2f);
            main.startSize = new ParticleSystem.MinMaxCurve(0.012f, 0.045f);
            main.startRotation = new ParticleSystem.MinMaxCurve(0f, 2f * Mathf.PI);
            main.gravityModifier = 0.75f;
            main.maxParticles = 400;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.startColor = new ParticleSystem.MinMaxGradient(
                new Color(0.95f, 0.93f, 0.88f), new Color(0.85f, 0.35f, 0.30f));

            var emission = _shred.emission;
            emission.enabled = true;
            emission.rateOverTime = 0f;   // bursts only

            var shape = _shred.shape;
            shape.enabled = true;
            shape.shapeType = ParticleSystemShapeType.Box;
            // Matches the board face: 1.6 x 0.9 m, flat.
            shape.scale = new Vector3(1.5f, 0.85f, 0.02f);
            shape.rotation = board.transform.eulerAngles;

            var rot = _shred.rotationOverLifetime;
            rot.enabled = true;
            rot.z = new ParticleSystem.MinMaxCurve(-3f, 3f);

            var renderer = _shred.GetComponent<ParticleSystemRenderer>();
            // URP strips the built-in default particle material, so give it one of ours.
            _particleMat = SpatialUiFactory.UnlitMaterial(Color.white);
            renderer.material = _particleMat;
            renderer.renderMode = ParticleSystemRenderMode.Billboard;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;

            _shred.Stop();
        }

        private void HandleLockout(string reason)
        {
            if (_shred == null) return;
            _shred.Clear();
            _shred.Play();
            _shred.Emit(burstCount);
        }
    }
}
