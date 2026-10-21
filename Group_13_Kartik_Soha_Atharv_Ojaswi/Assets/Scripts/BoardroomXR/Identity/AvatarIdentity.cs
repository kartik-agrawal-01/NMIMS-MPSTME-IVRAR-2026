using System;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;
using BoardroomXR.Logging;
using BoardroomXR.Network;

namespace BoardroomXR.Identity
{
    /// <summary>
    /// Attached to every player avatar (Netcode for GameObjects NetworkObject).
    /// Holds the identity claim and the *server-authoritative* verification
    /// state. Clients can request verification, but only the server may ever
    /// flip IsVerified to true - this is the property that stops a client
    /// from simply spoofing "verified" locally.
    /// </summary>
    [RequireComponent(typeof(NetworkObject))]
    public class AvatarIdentity : NetworkBehaviour
    {
        [Tooltip("Corporate directory ID this avatar claims to represent (e.g. employee ID).")]
        public NetworkVariable<FixedString64Bytes> ClaimedUserId = new(
            default,
            NetworkVariableReadPermission.Everyone,
            NetworkVariableWritePermission.Owner);

        /// <summary>True only once the server has accepted all MFA factors.</summary>
        public NetworkVariable<bool> IsVerified = new(
            false,
            NetworkVariableReadPermission.Everyone,
            NetworkVariableWritePermission.Server);

        /// <summary>Server timestamp (NetworkManager.ServerTime) of last successful verification.</summary>
        public NetworkVariable<double> VerifiedAtServerTime = new(
            0,
            NetworkVariableReadPermission.Everyone,
            NetworkVariableWritePermission.Server);

        /// <summary>Incremented by SpoofDetector; a rising count without re-verification is a red flag.</summary>
        public NetworkVariable<int> AnomalyStrikes = new(
            0,
            NetworkVariableReadPermission.Everyone,
            NetworkVariableWritePermission.Server);

        /// <summary>
        /// Raised on the server the moment verification is revoked, carrying the
        /// reason. <see cref="NetworkAuthRelay"/> listens and pushes a re-auth
        /// request down to the affected client.
        /// </summary>
        public event Action<string> OnServerRevoked;

        /// <summary>
        /// Overrides the claimed ID for this run. Set before spawn (e.g. by a
        /// demo participant picker) to test more than one identity on one headset.
        /// </summary>
        public static string LocalClaimedIdOverride;

        public string ClaimedUserIdString => ClaimedUserId.Value.ToString();

        public override void OnNetworkSpawn()
        {
            if (IsOwner)
            {
                // Owner proposes an identity claim; server verifies it during MFA.
                ClaimedUserId.Value = new FixedString64Bytes(SystemInfoIdentityStub.GetLocalClaimedId());
            }

            if (IsServer)
            {
                // Make the access gate aware of this avatar. Without this the gate's
                // registry stays empty and it never gates anything.
                var gate = FindAnyObjectByType<BoardroomAccessGate>();
                if (gate != null) gate.ServerRegisterAvatar(OwnerClientId, this);
                else Debug.LogWarning("[AvatarIdentity] No BoardroomAccessGate found in scene; content gating is inactive.");
            }

            SessionLogger.LogGlobal(
                $"AvatarIdentity spawned. netId={NetworkObjectId} owner={OwnerClientId} " +
                $"isServer={IsServer} isOwner={IsOwner} claimedId={ClaimedUserIdString}");
        }

        public override void OnNetworkDespawn()
        {
            if (IsServer)
            {
                var gate = FindAnyObjectByType<BoardroomAccessGate>();
                if (gate != null) gate.ServerUnregisterAvatar(OwnerClientId);
            }
        }

        /// <summary>Server-side helper: call once every MFA factor has passed.</summary>
        public void ServerMarkVerified()
        {
            if (!IsServer) return;
            IsVerified.Value = true;
            AnomalyStrikes.Value = 0;
            VerifiedAtServerTime.Value = NetworkManager.Singleton != null
                ? NetworkManager.Singleton.ServerTime.Time
                : 0;

            SessionLogger.LogGlobal(
                $"VERIFIED {ClaimedUserIdString} at serverTime={VerifiedAtServerTime.Value:F2}. Strikes reset to 0.");
        }

        /// <summary>Server-side helper: revoke trust immediately (e.g. spoof detected).</summary>
        public void ServerRevokeVerification(string reason = "unspecified")
        {
            if (!IsServer) return;
            if (!IsVerified.Value) return; // already unverified; nothing to revoke

            IsVerified.Value = false;
            SessionLogger.LogGlobal($"REVOKED {ClaimedUserIdString}. Reason: {reason}");
            OnServerRevoked?.Invoke(reason);
        }
    }

    /// <summary>Stand-in for whatever real corporate SSO/directory lookup you wire in later.</summary>
    public static class SystemInfoIdentityStub
    {
        public static string GetLocalClaimedId()
        {
            if (!string.IsNullOrWhiteSpace(AvatarIdentity.LocalClaimedIdOverride))
                return AvatarIdentity.LocalClaimedIdOverride;

            string id = SystemInfo.deviceUniqueIdentifier;
            if (string.IsNullOrEmpty(id) || id == SystemInfo.unsupportedIdentifier) id = "LOCAL";
            return "EMP-" + id.Substring(0, Mathf.Min(8, id.Length));
        }
    }
}
