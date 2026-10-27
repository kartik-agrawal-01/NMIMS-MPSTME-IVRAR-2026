using System;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;
using BoardroomXR.Auth;
using BoardroomXR.Biometrics;
using BoardroomXR.Logging;

namespace BoardroomXR.Identity
{
    /// <summary>
    /// Presenting a badge at the reader wall switches which employee the headset
    /// claims to be. That is what lets every visitor at the stall be a separate
    /// user on one device: both the gesture template store and the behavioural
    /// template store are keyed by <see cref="AvatarIdentity.ClaimedUserIdString"/>,
    /// so a badge switch gives a genuinely separate enrolment.
    ///
    /// The order below matters:
    ///   1. revoke any live verification (the old identity must lose access),
    ///   2. publish the new claimed ID (server-authoritative checks read this),
    ///   3. stop the biometric engine so it cannot keep scoring the old template,
    ///   4. restart MFA from factor 1.
    /// </summary>
    public static class BadgeSwitchService
    {
        /// <summary>The badge most recently presented in this app run, or null.</summary>
        public static string CurrentBadgeId { get; private set; }

        /// <summary>Raised after a successful switch, with the new employee ID.</summary>
        public static event Action<string> OnBadgePresented;

        public static bool Select(string badgeId)
        {
            if (string.IsNullOrWhiteSpace(badgeId)) return false;

            var identity = UnityEngine.Object.FindAnyObjectByType<AvatarIdentity>(FindObjectsInactive.Include);
            var coordinator = UnityEngine.Object.FindAnyObjectByType<MFACoordinator>(FindObjectsInactive.Include);
            string previous = identity != null ? identity.ClaimedUserIdString : "-";

            // 1. The identity that was verified must not keep its access.
            try
            {
                if (identity != null && identity.IsSpawned && identity.IsServer && identity.IsVerified.Value)
                    identity.ServerRevokeVerification("identity switched");
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[Badge] Could not revoke previous verification: {e.Message}");
            }

            // 2. Publish the claim. The override also survives a respawn, because
            //    SystemInfoIdentityStub reads it when the avatar spawns.
            AvatarIdentity.LocalClaimedIdOverride = badgeId;
            bool published = false;
            try
            {
                if (identity != null && identity.IsSpawned && identity.IsOwner)
                {
                    identity.ClaimedUserId.Value = new FixedString64Bytes(badgeId);
                    published = true;
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[Badge] Could not publish claimed ID: {e.Message}");
            }

            // On a host the NetworkVariable write is visible to the server immediately,
            // so the PIN / gesture / OTP checks that follow use the new badge. Log what
            // the server will actually see rather than assuming it.
            string serverSees = identity != null ? identity.ClaimedUserIdString : "(no avatar)";
            bool serverAgrees = serverSees == badgeId;

            // 3. Never let the engine keep scoring the previous person's template.
            var auth = ContinuousBiometricAuthenticator.Instance;
            if (auth != null) auth.StopMonitoring($"badge switched to {badgeId}");

            CurrentBadgeId = badgeId;
            var badge = BadgeDirectory.Find(badgeId);
            BioEventLog.Emit("identity_switch", badgeId,
                $"from={previous} name={(badge != null ? badge.name : "?")} role={(badge != null ? badge.role : "?")} " +
                $"published={published} serverSees={serverSees}");

            if (!serverAgrees)
                Debug.LogWarning($"[Badge] Server still sees '{serverSees}' after presenting {badgeId}. " +
                                 "Factor checks would run against the old identity.");

            // 4. Back to factor 1 for the new person.
            if (coordinator != null) coordinator.RestartSession($"badge {badgeId} presented");
            else SessionLogger.LogGlobal($"[Badge] {badgeId} presented but no MFACoordinator was found to restart.");

            try { OnBadgePresented?.Invoke(badgeId); }
            catch (Exception e) { Debug.LogWarning($"[Badge] listener threw: {e.Message}"); }

            return serverAgrees;
        }

        /// <summary>Wipes both templates for one badge (presenter reset).</summary>
        public static void ResetBadge(string badgeId, bool gesture = true, bool biometric = true)
        {
            if (string.IsNullOrWhiteSpace(badgeId)) return;
            if (gesture) GestureTemplateStore.Clear(badgeId);
            if (biometric) BiometricTemplate.Delete(badgeId);
            BioEventLog.Emit("badge_reset", badgeId,
                $"gesture={(gesture ? "cleared" : "kept")} biometric={(biometric ? "cleared" : "kept")}");
        }

        /// <summary>Wipes every badge in the directory (between visitors).</summary>
        public static void ResetAllBadges()
        {
            foreach (var b in BadgeDirectory.Badges)
            {
                GestureTemplateStore.Clear(b.id);
                BiometricTemplate.Delete(b.id);
            }
            BioEventLog.Emit("badge_reset_all", "-", $"{BadgeDirectory.Badges.Count} badges cleared");
        }
    }
}
