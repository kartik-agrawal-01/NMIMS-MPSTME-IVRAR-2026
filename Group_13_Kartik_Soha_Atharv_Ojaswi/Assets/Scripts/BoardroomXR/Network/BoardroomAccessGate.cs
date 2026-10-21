using System.Collections;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;
using BoardroomXR.Auth;
using BoardroomXR.Identity;
using BoardroomXR.Logging;

namespace BoardroomXR.Network
{
    /// <summary>
    /// Server-side gate on the boardroom "confidential content" (shared
    /// whiteboard, document viewer). Any avatar without
    /// AvatarIdentity.IsVerified == true is excluded from that content.
    ///
    /// Visibility is applied on three triggers, so a revoke takes effect
    /// immediately rather than up to a whole re-check interval later:
    ///   1. when an avatar registers (AvatarIdentity.OnNetworkSpawn),
    ///   2. the instant IsVerified changes,
    ///   3. on a slow periodic sweep as a safety net.
    ///
    /// HOST MODE: Netcode cannot hide a NetworkObject from the server itself,
    /// so for the host's own client the gate toggles renderers/colliders
    /// locally instead. That is the path a single-headset demo actually uses.
    /// </summary>
    public class BoardroomAccessGate : NetworkBehaviour
    {
        public MFASettings settings;
        public SessionLogger sessionLogger;

        [Tooltip("NetworkObjects representing confidential content whose visibility this gate controls.")]
        public List<NetworkObject> gatedContent = new();

        private readonly Dictionary<ulong, AvatarIdentity> _connectedIdentities = new();
        // Must be the NetworkVariable delegate type, not Action<bool,bool>, or the
        // -= unsubscribe would not match the delegate that was added.
        private readonly Dictionary<ulong, NetworkVariable<bool>.OnValueChangedDelegate> _verifiedHandlers = new();

        // Starts false so the gate never *believes* content is visible before it has
        // gated anything. Paired with _localVisibilityApplied, which forces the first
        // ApplyLocalVisibility call through regardless of the dedupe check.
        private bool _localContentVisible;
        private bool _localVisibilityApplied;

        private void Awake()
        {
            // Fail closed, before networking exists. Hiding only in OnNetworkSpawn left the
            // confidential content RENDERED whenever StartHost() failed, because
            // OnNetworkSpawn never runs in that case. Awake always runs.
            ApplyLocalVisibility(false, "gate awake; networking not started yet");
        }

        public override void OnNetworkSpawn()
        {
            if (!IsServer)
            {
                enabled = false;
                return;
            }

            // Start hidden: nothing is confidential-visible until someone verifies.
            ApplyLocalVisibility(false, "gate spawned; no verified avatar yet");

            // Catch up on avatars that spawned BEFORE this gate. In-scene NetworkObject
            // spawn order is not guaranteed, and AvatarIdentity.OnNetworkSpawn calls
            // ServerRegisterAvatar, whose !IsServer guard silently rejects the call while
            // this gate is still unspawned. Without this sweep the registry stays empty,
            // IsVerified.OnValueChanged is never subscribed, and verification never
            // reveals the content.
            foreach (var identity in FindObjectsByType<AvatarIdentity>(
                         FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                ServerRegisterAvatar(identity.OwnerClientId, identity);
            }

            StartCoroutine(RecheckLoop());

            Log($"BoardroomAccessGate active (server). Gating {gatedContent.Count} content object(s). " +
                $"Registered avatars: {_connectedIdentities.Count}.");
        }

        public override void OnNetworkDespawn()
        {
            foreach (var kv in _verifiedHandlers)
            {
                if (_connectedIdentities.TryGetValue(kv.Key, out var id) && id != null)
                    id.IsVerified.OnValueChanged -= kv.Value;
            }
            _verifiedHandlers.Clear();
            _connectedIdentities.Clear();
        }

        /// <summary>Called from AvatarIdentity.OnNetworkSpawn on the server.</summary>
        public void ServerRegisterAvatar(ulong clientId, AvatarIdentity identity)
        {
            if (identity == null) return;

            if (!IsServer)
            {
                // Never silent: this is the path that used to drop every registration when
                // the avatar spawned first. OnNetworkSpawn's catch-up sweep re-registers.
                Log($"Registration for client {clientId} deferred: gate is not spawned yet " +
                    "(will be picked up by the catch-up sweep in OnNetworkSpawn).");
                return;
            }

            _connectedIdentities[clientId] = identity;

            // React the moment the server flips verification either way.
            NetworkVariable<bool>.OnValueChangedDelegate handler = (previous, current) =>
            {
                Log($"Verification for client {clientId} changed {previous} -> {current}.");
                ApplyVisibility(clientId, current);
            };

            if (_verifiedHandlers.TryGetValue(clientId, out var existing))
                identity.IsVerified.OnValueChanged -= existing;

            identity.IsVerified.OnValueChanged += handler;
            _verifiedHandlers[clientId] = handler;

            Log($"Registered avatar {identity.ClaimedUserIdString} (client {clientId}). " +
                $"Verified={identity.IsVerified.Value}.");

            ApplyVisibility(clientId, identity.IsVerified.Value);
        }

        public void ServerUnregisterAvatar(ulong clientId)
        {
            if (!IsServer) return;

            if (_verifiedHandlers.TryGetValue(clientId, out var handler) &&
                _connectedIdentities.TryGetValue(clientId, out var identity) && identity != null)
            {
                identity.IsVerified.OnValueChanged -= handler;
            }

            _verifiedHandlers.Remove(clientId);
            _connectedIdentities.Remove(clientId);
            Log($"Unregistered avatar for client {clientId}.");
        }

        private IEnumerator RecheckLoop()
        {
            float interval = settings != null ? settings.continuousRecheckIntervalSeconds : 20f;
            var wait = new WaitForSeconds(Mathf.Max(1f, interval));

            while (true)
            {
                yield return wait;
                foreach (var kv in _connectedIdentities)
                {
                    if (kv.Value == null) continue;
                    ApplyVisibility(kv.Key, kv.Value.IsVerified.Value);
                }
            }
        }

        private void ApplyVisibility(ulong clientId, bool verified)
        {
            bool isHostsOwnClient =
                NetworkManager != null && NetworkManager.IsHost && clientId == NetworkManager.LocalClientId;

            if (isHostsOwnClient)
            {
                // Netcode throws if you try to hide an object from the server, so the
                // host's own view is gated locally instead.
                ApplyLocalVisibility(verified, $"client {clientId} verified={verified}");
                return;
            }

            foreach (var netObj in gatedContent)
            {
                if (netObj == null || !netObj.IsSpawned) continue;

                try
                {
                    bool currentlyVisible = netObj.IsNetworkVisibleTo(clientId);
                    if (verified && !currentlyVisible) netObj.NetworkShow(clientId);
                    else if (!verified && currentlyVisible) netObj.NetworkHide(clientId);
                }
                catch (System.Exception e)
                {
                    Log($"Visibility change failed for {netObj.name} / client {clientId}: {e.Message}");
                }
            }

            Log($"Client {clientId} gated content visibility set to {verified}.");
        }

        /// <summary>Host-local gating: toggles renderers and colliders on the gated content.</summary>
        private void ApplyLocalVisibility(bool visible, string reason)
        {
            // The first call must always apply. With _localContentVisible defaulting to
            // false, a plain equality check would make the initial hide a no-op and leave
            // the renderers in whatever state the scene authored them - visible.
            if (_localVisibilityApplied && _localContentVisible == visible) return;
            _localVisibilityApplied = true;
            _localContentVisible = visible;

            int affected = 0;
            foreach (var netObj in gatedContent)
            {
                if (netObj == null) continue;

                foreach (var r in netObj.GetComponentsInChildren<Renderer>(true)) r.enabled = visible;
                foreach (var c in netObj.GetComponentsInChildren<Collider>(true)) c.enabled = visible;
                foreach (var canvas in netObj.GetComponentsInChildren<Canvas>(true)) canvas.enabled = visible;
                affected++;
            }

            Log($"Host-local gated content {(visible ? "SHOWN" : "HIDDEN")} on {affected} object(s) ({reason}).");
        }

        private void Log(string message)
        {
            if (sessionLogger != null) sessionLogger.Log(message);
            else SessionLogger.LogGlobal(message);
        }
    }
}
