using UnityEngine;
using Unity.Netcode;
using BoardroomXR.Auth;

namespace BoardroomXR.Network
{
    /// <summary>
    /// DEMO/TEST bootstrap and the single component that starts the host. The MFA
    /// factor checks are server-authoritative (see <see cref="NetworkAuthRelay"/>),
    /// so nothing advances past the PIN stage unless a Netcode host is running.
    /// This starts a host on Play for single-machine demoing/testing. Remove or
    /// disable it once a real connection/lobby flow (menu -> host/join) exists.
    ///
    /// It also provisions the server-side credential store from
    /// NetworkManager.OnServerStarted, so credentials exist whichever code path
    /// started the server.
    /// </summary>
    [RequireComponent(typeof(NetworkManager))]
    public class NetworkHostBootstrap : MonoBehaviour
    {
        [Tooltip("Start a host automatically when the scene plays.")]
        public bool startHostOnPlay = true;

        [Tooltip("MFASettings used to provision server-side credentials. Found from the scene if left empty.")]
        public MFASettings settings;

        private NetworkManager _nm;

        private void Awake()
        {
            _nm = GetComponent<NetworkManager>();
            _nm.OnServerStarted += HandleServerStarted;
        }

        private void OnDestroy()
        {
            if (_nm != null) _nm.OnServerStarted -= HandleServerStarted;
        }

        private void Start()
        {
            if (!startHostOnPlay) return;

            if (_nm.IsListening)
            {
                Debug.Log("[NetworkHostBootstrap] NetworkManager already listening; skipping StartHost.", this);
                return;
            }

            bool ok = _nm.StartHost();
            Debug.Log($"[NetworkHostBootstrap] StartHost() -> {ok}.", this);
        }

        private void HandleServerStarted()
        {
            // Idempotent; runs on every server start regardless of who called StartHost/StartServer.
            ServerCredentialStore.Provision(settings);
        }
    }
}
