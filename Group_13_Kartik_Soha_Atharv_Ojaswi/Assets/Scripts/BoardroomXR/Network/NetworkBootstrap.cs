using System.Collections;
using Unity.Netcode;
using UnityEngine;
using BoardroomXR.Auth;
using BoardroomXR.Logging;

namespace BoardroomXR.Network
{
    /// <summary>
    /// Starts Netcode. Nothing in this project used to call StartHost/StartClient,
    /// so no NetworkObject ever spawned, OnNetworkSpawn never fired, IsServer was
    /// always false, and SpoofDetector / BoardroomAccessGate / every ServerRpc were
    /// unreachable dead code. This is the component that turns them on.
    ///
    /// Default behaviour is a single-headset demo: start as host (server + client
    /// in one process) shortly after the scene loads. StartHostNow/StartClientNow
    /// are public so a menu button can drive it instead.
    ///
    /// It also provisions the server-side credential store, which must happen on
    /// the server before any factor check can succeed.
    ///
    /// NOT used by SampleScene: <see cref="NetworkHostBootstrap"/> on the NetworkManager
    /// is the single host starter there. This component is no longer auto-installed at
    /// runtime; place it in a scene explicitly (and remove NetworkHostBootstrap) to use it.
    /// </summary>
    [DefaultExecutionOrder(-100)]
    public class NetworkBootstrap : MonoBehaviour
    {
        public enum StartMode { AutoHost, AutoClient, Manual }

        [Header("Startup")]
        [Tooltip("AutoHost is the single-headset demo default. Manual waits for a menu button.")]
        public StartMode startMode = StartMode.AutoHost;

        [Tooltip("Seconds to wait after scene load before starting, so XR and the rig finish initialising.")]
        public float startDelaySeconds = 0.5f;

        [Header("Connection (client mode)")]
        public string serverAddress = "127.0.0.1";
        public ushort serverPort = 7777;

        [Header("Scene sanity")]
        [Tooltip("The boardroom rig is placed in the scene AND set as NetworkConfig.PlayerPrefab, which would " +
                 "spawn a second copy on start. Leave on to suppress the duplicate player spawn.")]
        public bool preventDuplicatePlayerSpawn = true;

        [Tooltip("MFASettings used to provision server-side credentials. Found from the scene if left empty.")]
        public MFASettings settings;

        public static NetworkBootstrap Active { get; private set; }

        private bool _started;

        private void Awake()
        {
            Active = this;
        }

        private void Start()
        {
            if (startMode == StartMode.Manual)
            {
                Log("NetworkBootstrap in Manual mode; waiting for StartHostNow()/StartClientNow().");
                return;
            }
            StartCoroutine(DelayedStart());
        }

        private IEnumerator DelayedStart()
        {
            yield return new WaitForSeconds(Mathf.Max(0f, startDelaySeconds));

            if (startMode == StartMode.AutoHost) StartHostNow();
            else if (startMode == StartMode.AutoClient) StartClientNow();
        }

        // ---------------- public entry points (bind these to menu buttons) ----------------

        public void StartHostNow()
        {
            if (!PrepareToStart(out var nm)) return;

            // Credentials must exist server-side before any factor is checked. Idempotent;
            // resolves MFASettings from the scene when the field is empty.
            ServerCredentialStore.Provision(settings);

            nm.OnServerStarted += HandleServerStarted;
            nm.OnClientConnectedCallback += HandleClientConnected;
            nm.OnClientDisconnectCallback += HandleClientDisconnected;

            bool ok = nm.StartHost();
            _started = ok;
            Log(ok
                ? "StartHost() succeeded. Server + local client running; IsServer is now true."
                : "StartHost() FAILED. Check the transport configuration on the NetworkManager.");
        }

        public void StartClientNow()
        {
            if (!PrepareToStart(out var nm)) return;

            nm.OnClientConnectedCallback += HandleClientConnected;
            nm.OnClientDisconnectCallback += HandleClientDisconnected;

            bool ok = nm.StartClient();
            _started = ok;
            Log(ok
                ? $"StartClient() issued toward {serverAddress}:{serverPort}."
                : "StartClient() FAILED.");
        }

        public void ShutdownNow()
        {
            var nm = NetworkManager.Singleton;
            if (nm == null || !nm.IsListening) return;

            Log("Shutting down Netcode.");
            nm.Shutdown();
            _started = false;
        }

        // ---------------- internals ----------------

        private bool PrepareToStart(out NetworkManager nm)
        {
            nm = NetworkManager.Singleton;

            if (nm == null)
            {
                Debug.LogError("[NetworkBootstrap] No NetworkManager in the scene; cannot start networking.");
                Log("Start aborted: no NetworkManager.");
                return false;
            }

            if (_started || nm.IsListening)
            {
                Log("Start ignored: Netcode is already running.");
                return false;
            }

            if (preventDuplicatePlayerSpawn && nm.NetworkConfig.PlayerPrefab != null)
            {
                // MFAPlayerRig is placed in the scene as an in-scene NetworkObject and is
                // ALSO the PlayerPrefab. Leaving both would spawn a second rig with a second
                // MFACoordinator and SpoofDetector fighting the first.
                Log($"Suppressing PlayerPrefab '{nm.NetworkConfig.PlayerPrefab.name}' spawn: " +
                    "the boardroom rig already exists in the scene.");
                nm.NetworkConfig.PlayerPrefab = null;
            }

            return true;
        }

        private void HandleServerStarted()
        {
            var nm = NetworkManager.Singleton;
            Log($"Server started. isHost={nm.IsHost} isServer={nm.IsServer} localClientId={nm.LocalClientId} " +
                $"credentialsProvisioned={ServerCredentialStore.IsProvisioned}");
        }

        private void HandleClientConnected(ulong clientId)
        {
            Log($"Client connected: {clientId}.");
        }

        private void HandleClientDisconnected(ulong clientId)
        {
            Log($"Client disconnected: {clientId}.");
        }

        private void OnDestroy()
        {
            var nm = NetworkManager.Singleton;
            if (nm != null)
            {
                nm.OnServerStarted -= HandleServerStarted;
                nm.OnClientConnectedCallback -= HandleClientConnected;
                nm.OnClientDisconnectCallback -= HandleClientDisconnected;
            }
            if (Active == this) Active = null;
        }

        private void Log(string message) => SessionLogger.LogGlobal($"[NetworkBootstrap] {message}");
    }
}
