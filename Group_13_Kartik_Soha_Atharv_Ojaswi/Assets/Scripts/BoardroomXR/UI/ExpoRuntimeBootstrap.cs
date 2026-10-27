using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace BoardroomXR.UI
{
    /// <summary>
    /// Creates everything the expo build adds on top of the research prototype:
    /// the spatial button system, the badge reader wall, the SOC trust panel, the
    /// wrist display, the boardroom set, the security doors and the presenter
    /// console.
    ///
    /// It bootstraps itself after scene load exactly like
    /// <see cref="BoardroomXR.Biometrics.ContinuousBiometricAuthenticator.EnsureExists"/>,
    /// so the scene file is never edited: nothing here needs an Inspector
    /// reference, and the whole set can be removed by deleting this component.
    ///
    /// The object survives scene loads, so the furniture is rebuilt against the new
    /// scene's rig whenever a scene finishes loading.
    /// </summary>
    public class ExpoRuntimeBootstrap : MonoBehaviour
    {
        public static ExpoRuntimeBootstrap Instance { get; private set; }

        private readonly List<GameObject> _parts = new();
        private bool _rebuildPending = true;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void RegisterSceneHook()
        {
            SceneManager.sceneLoaded -= OnSceneLoaded;
            SceneManager.sceneLoaded += OnSceneLoaded;
        }

        private static void OnSceneLoaded(Scene s, LoadSceneMode m)
        {
            // The rig transforms belong to the new scene; drop the cached ones.
            XrRigLocator.Invalidate();
            var b = EnsureExists();
            if (b != null) b._rebuildPending = true;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap() => EnsureExists();

        public static ExpoRuntimeBootstrap EnsureExists()
        {
            if (Instance != null) return Instance;
            var existing = FindAnyObjectByType<ExpoRuntimeBootstrap>();
            if (existing != null) return existing;

            var go = new GameObject("BoardroomXR Expo");
            DontDestroyOnLoad(go);
            return go.AddComponent<ExpoRuntimeBootstrap>();
        }

        private void Awake()
        {
            Instance = this;
            SpatialButtonSystem.EnsureExists();
        }

        private void Update()
        {
            if (!_rebuildPending) return;
            // Wait for the rig, so every piece lands in the measured room frame.
            if (XrRigLocator.Origin == null && Camera.main == null) return;
            _rebuildPending = false;
            BuildParts();
        }

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }

        /// <summary>
        /// Rebuilds the set. Each piece lives on its own child object, so one of them
        /// failing cannot stop the others from appearing.
        /// </summary>
        private void BuildParts()
        {
            foreach (var p in _parts) if (p != null) Destroy(p);
            _parts.Clear();

            AddPart<BadgeReaderWall>("BadgeReader");
            AddPart<SocTrustPanel>("SocPanel");
            AddPart<WristTrustDisplay>("WristDisplay");
            AddPart<ProceduralBoardroom>("Boardroom");
            AddPart<SecurityGlassDoors>("SecurityDoors");
            AddPart<WhiteboardContent>("WhiteboardContent");
            AddPart<LockoutEffects>("LockoutEffects");
            AddPart<GestureTrail>("GestureTrail");
            AddPart<ProceduralAudioCues>("AudioCues");
            AddPart<MimicryAttackGhost>("MimicryGhost");
            AddPart<PresenterConsole>("PresenterConsole");
            AddPart<BoardroomXR.Network.BiometricDashboardServer>("DashboardServer");
        }

        private T AddPart<T>(string name) where T : Component
        {
            var go = new GameObject(name);
            go.transform.SetParent(transform, false);
            _parts.Add(go);
            return go.AddComponent<T>();
        }
    }
}
