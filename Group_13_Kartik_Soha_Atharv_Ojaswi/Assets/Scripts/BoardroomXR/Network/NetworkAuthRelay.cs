using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using Unity.Netcode;
using UnityEngine;
using BoardroomXR.Auth;
using BoardroomXR.Identity;
using BoardroomXR.Logging;

namespace BoardroomXR.Network
{
    /// <summary>
    /// Bridges the local MFACoordinator to server-authoritative checks over
    /// Unity Netcode for GameObjects. Every factor check is a request/reply
    /// pair correlated by requestId, because ServerRpc calls cannot return a
    /// value directly - the server answers back with a ClientRpc addressed
    /// only to the requesting client.
    ///
    /// Also carries the revoke path in the other direction: when the server
    /// revokes verification (SpoofDetector), it pushes a re-auth request down
    /// to the owning client so the MFA flow actually restarts.
    ///
    /// SECURITY NOTE: the enrolled PIN hash and OTP seed live only in
    /// ServerCredentialStore, provisioned server-side at host start - this
    /// class contains no secret material.
    /// </summary>
    [RequireComponent(typeof(AvatarIdentity))]
    public class NetworkAuthRelay : NetworkBehaviour
    {
        private int _nextRequestId;
        private readonly Dictionary<int, Action<bool>> _pending = new();
        private AvatarIdentity _identity;
        private MFACoordinator _coordinator;

        /// <summary>True when RPCs can actually be issued. Callers must check before sending.</summary>
        public bool CanReachServer =>
            IsSpawned && NetworkManager != null && NetworkManager.IsListening;

        private void Awake()
        {
            _identity = GetComponent<AvatarIdentity>();
            _coordinator = GetComponent<MFACoordinator>();
        }

        public override void OnNetworkSpawn()
        {
            if (IsServer)
            {
                _identity.OnServerRevoked += HandleServerRevoked;
            }

            SessionLogger.LogGlobal(
                $"NetworkAuthRelay spawned. canReachServer={CanReachServer} isServer={IsServer} isOwner={IsOwner}");
        }

        public override void OnNetworkDespawn()
        {
            if (IsServer && _identity != null)
            {
                _identity.OnServerRevoked -= HandleServerRevoked;
            }
            _pending.Clear();
        }

        // ---------- client -> server requests ----------

        public void RequestVerifyPin(string pin, Action<bool> onResult) =>
            Send(onResult, id => VerifyPinServerRpc(id, pin));

        public void RequestVerifyGesture(float clientScore, Action<bool> onResult) =>
            Send(onResult, id => VerifyGestureServerRpc(id, clientScore));

        public void RequestVerifyOtp(string code, Action<bool> onResult) =>
            Send(onResult, id => VerifyOtpServerRpc(id, code));

        private void Send(Action<bool> onResult, Action<int> issueRpc)
        {
            if (!CanReachServer)
            {
                SessionLogger.LogGlobal("Auth request dropped: network not started / object not spawned.");
                onResult?.Invoke(false);
                return;
            }

            int id = _nextRequestId++;
            _pending[id] = onResult;
            issueRpc(id);
        }

        // ---------- server-side authoritative checks ----------
        // RequireOwnership is false because the boardroom rig is an in-scene
        // NetworkObject owned by the server; a joining client still needs to
        // be able to submit its own factors.

        [ServerRpc(RequireOwnership = false)]
        private void VerifyPinServerRpc(int requestId, string submittedPin, ServerRpcParams rpcParams = default)
        {
            string userId = _identity.ClaimedUserIdString;
            bool ok = ServerCredentialStore.CheckPin(userId, submittedPin);
            SessionLogger.LogGlobal($"[server] PIN check for {userId}: {(ok ? "accepted" : "rejected")}.");
            RespondClientRpc(requestId, ok, ToClient(rpcParams));
        }

        [ServerRpc(RequireOwnership = false)]
        private void VerifyGestureServerRpc(int requestId, float clientReportedScore, ServerRpcParams rpcParams = default)
        {
            // Never trust a client-reported score alone in production - re-derive
            // it server-side from replicated pose data. Shown simplified here:
            // the score is bounds-checked and compared to the server's own threshold.
            bool sane = clientReportedScore >= 0f && clientReportedScore <= 1f;
            bool ok = sane && clientReportedScore >= ServerCredentialStore.MinAcceptableGestureScore;

            SessionLogger.LogGlobal(
                $"[server] Gesture check for {_identity.ClaimedUserIdString}: score={clientReportedScore:F3} " +
                $"required>={ServerCredentialStore.MinAcceptableGestureScore:F3} -> {(ok ? "accepted" : "rejected")}.");

            RespondClientRpc(requestId, ok, ToClient(rpcParams));
        }

        [ServerRpc(RequireOwnership = false)]
        private void VerifyOtpServerRpc(int requestId, string submittedCode, ServerRpcParams rpcParams = default)
        {
            string userId = _identity.ClaimedUserIdString;
            bool ok = ServerCredentialStore.CheckOtp(userId, submittedCode);

            SessionLogger.LogGlobal($"[server] OTP check for {userId}: {(ok ? "accepted" : "rejected")}.");

            if (ok) _identity.ServerMarkVerified();
            RespondClientRpc(requestId, ok, ToClient(rpcParams));
        }

        [ClientRpc]
        private void RespondClientRpc(int requestId, bool success, ClientRpcParams clientRpcParams = default)
        {
            if (_pending.TryGetValue(requestId, out var cb))
            {
                _pending.Remove(requestId);
                cb(success);
            }
        }

        // ---------- server -> client revoke / re-auth ----------

        private void HandleServerRevoked(string reason)
        {
            if (!IsServer) return;
            ForceReauthClientRpc(reason ?? "unspecified", ToClient(OwnerClientId));
        }

        [ClientRpc]
        private void ForceReauthClientRpc(string reason, ClientRpcParams clientRpcParams = default)
        {
            if (_coordinator == null) _coordinator = GetComponent<MFACoordinator>();
            if (_coordinator == null)
            {
                SessionLogger.LogGlobal("Re-auth requested but no MFACoordinator on this rig.");
                return;
            }
            _coordinator.RestartSession(reason);
        }

        private static ClientRpcParams ToClient(ServerRpcParams serverParams) =>
            ToClient(serverParams.Receive.SenderClientId);

        private static ClientRpcParams ToClient(ulong clientId) => new()
        {
            Send = new ClientRpcSendParams { TargetClientIds = new[] { clientId } }
        };
    }

    /// <summary>
    /// Server-only stand-in for a real backend (corporate directory + secrets
    /// vault). Credentials are provisioned from NetworkManager.OnServerStarted
    /// (see <see cref="NetworkHostBootstrap"/>), so they exist whichever code path
    /// started the server.
    ///
    /// The PIN is stored as a salted SHA-256 hash rather than plaintext, and
    /// the OTP is validated by deriving the expected code - so both checks
    /// genuinely pass or fail on what the user actually entered.
    ///
    /// DEMO SCOPE: one enrolled credential set is shared by all user IDs.
    /// A real deployment needs a per-user lookup, rate limiting and an HSM.
    /// </summary>
    public static class ServerCredentialStore
    {
        public static float MinAcceptableGestureScore { get; private set; } = 0.75f;

        private static byte[] _pinSalt;
        private static byte[] _pinHash;
        private static string _otpSeed;
        private static int _otpDigits = 6;
        private static int _otpValiditySeconds = 30;
        private static MFASettings _provisionedFrom;

        public static bool IsProvisioned => _pinHash != null && !string.IsNullOrEmpty(_otpSeed);

        /// <summary>
        /// Server-side only. Idempotent: once provisioned from a settings asset, repeat
        /// calls with the same asset are no-ops, so every server-start path can call it
        /// unconditionally. Pass null to resolve MFASettings from the scene.
        /// </summary>
        public static void Provision(MFASettings settings)
        {
            if (settings == null) settings = FindSettingsInScene();
            if (settings == null)
            {
                Debug.LogError("[ServerCredentialStore] Cannot provision: no MFASettings found. " +
                               "Every factor check will fail.");
                return;
            }

            if (IsProvisioned && settings == _provisionedFrom) return;

            _pinSalt = new byte[16];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(_pinSalt);

            _pinHash = HashPin(settings.demoEnrolledPin, _pinSalt);
            _otpSeed = settings.demoOtpSeed;
            _otpDigits = settings.otpDigits;
            _otpValiditySeconds = settings.otpValiditySeconds;
            MinAcceptableGestureScore = settings.gestureMatchThreshold;
            _provisionedFrom = settings;

            SessionLogger.LogGlobal(
                $"[server] Credentials provisioned: PIN hash stored (salted SHA-256), " +
                $"TOTP {_otpDigits} digits / {_otpValiditySeconds}s, gesture threshold {MinAcceptableGestureScore:F2}.");
        }

        private static MFASettings FindSettingsInScene()
        {
            var coordinator = UnityEngine.Object.FindAnyObjectByType<MFACoordinator>();
            if (coordinator != null && coordinator.settings != null) return coordinator.settings;

            var gate = UnityEngine.Object.FindAnyObjectByType<BoardroomAccessGate>();
            if (gate != null && gate.settings != null) return gate.settings;

            return null;
        }

        public static bool CheckPin(string userId, string pin)
        {
            if (!IsProvisioned || string.IsNullOrEmpty(pin)) return false;

            byte[] candidate = HashPin(pin, _pinSalt);
            return FixedTimeEquals(candidate, _pinHash);
        }

        /// <summary>Local constant-time byte compare (avoids depending on CryptographicOperations).</summary>
        private static bool FixedTimeEquals(byte[] a, byte[] b)
        {
            if (a == null || b == null || a.Length != b.Length) return false;
            int diff = 0;
            for (int i = 0; i < a.Length; i++) diff |= a[i] ^ b[i];
            return diff == 0;
        }

        public static bool CheckOtp(string userId, string code) =>
            IsProvisioned && TotpGenerator.Validate(_otpSeed, code, _otpDigits, _otpValiditySeconds);

        private static byte[] HashPin(string pin, byte[] salt)
        {
            byte[] pinBytes = Encoding.UTF8.GetBytes(pin ?? string.Empty);
            byte[] combined = new byte[salt.Length + pinBytes.Length];
            Buffer.BlockCopy(salt, 0, combined, 0, salt.Length);
            Buffer.BlockCopy(pinBytes, 0, combined, salt.Length, pinBytes.Length);

            using var sha = SHA256.Create();
            return sha.ComputeHash(combined);
        }
    }
}
