using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;
using BoardroomXR.Auth;
using BoardroomXR.Logging;

namespace BoardroomXR.Identity
{
    /// <summary>
    /// MFA at the door is not enough on its own - a session can be hijacked
    /// *after* verification (e.g. a compromised client puppeting the same
    /// avatar). This component runs server-side for every verified avatar,
    /// comparing live head movement against a rolling baseline built during
    /// that user's own verified session. A statistically unusual jump
    /// (teleport-like motion, impossible speed) increments
    /// AvatarIdentity.AnomalyStrikes and, past a threshold, revokes
    /// verification and forces re-authentication.
    ///
    /// SCOPE NOTE: this is a within-session movement outlier detector, not an
    /// identity biometric. It answers "is this person moving unusually for
    /// themselves, right now", not "is this the enrolled person". It has no
    /// enrolment and produces no match score, so it cannot yield an EER.
    /// </summary>
    [RequireComponent(typeof(AvatarIdentity))]
    public class SpoofDetector : NetworkBehaviour
    {
        public MFASettings settings;
        public SessionLogger sessionLogger;

        [Tooltip("Head transform. Left empty, the main XR camera is used, which is the actual head pose on a headset.")]
        public Transform headTransform;

        [Tooltip("When true (default since v4) this legacy speed-outlier detector only logs and never revokes. " +
                 "On-device trials showed its 1.8 s baseline, built while the user stood still right after login, " +
                 "flagged ordinary head movement as anomalous and revoked genuine users. Identity is now enforced by " +
                 "ContinuousBiometricAuthenticator.")]
        public bool supersededByContinuousBiometrics = true;

        [Header("Strike policy")]
        [Tooltip("Strikes required before verification is revoked.")]
        public int strikesBeforeRevoke = 3;
        [Tooltip("Minimum seconds between two strikes, so one burst of motion cannot instantly revoke.")]
        public float strikeRefractorySeconds = 0.4f;
        [Tooltip("Seconds of clean movement that removes one accumulated strike. 0 disables decay.")]
        public float strikeDecaySeconds = 12f;
        [Tooltip("Speeds below this (m/s) are ignored, so sensor noise while standing still cannot strike.")]
        public float minSpeedForAnomaly = 0.35f;

        private readonly Queue<float> _recentSpeeds = new();
        private const int BaselineWindow = 90; // ~1.8s at the default 50Hz FixedUpdate rate

        private Vector3 _lastHeadPos;
        private bool _hasLastHeadPos;          // guards against a bogus first-frame speed
        private float _lastStrikeTime = -999f;
        private float _lastDecayTime;
        private AvatarIdentity _identity;
        private bool _wasVerified;

        public override void OnNetworkSpawn()
        {
            _identity = GetComponent<AvatarIdentity>();

            if (!IsServer)
            {
                // Detector is authoritative: it only runs on the server/host.
                enabled = false;
                return;
            }

            ResolveHeadTransform();
            SessionLogger.LogGlobal(
                $"SpoofDetector active (server) for {_identity.ClaimedUserIdString}. " +
                $"head={(headTransform != null ? headTransform.name : "NONE")} " +
                $"threshold={(settings != null ? settings.behaviouralAnomalyStdDevThreshold : -1f)}sd " +
                $"strikesToRevoke={strikesBeforeRevoke}");
        }

        private void ResolveHeadTransform()
        {
            // On an XR rig the main camera *is* the head (centre eye). Prefer it, because
            // a reference to the rig root would report ~0 speed while the head actually moves.
            if (Camera.main != null)
            {
                if (headTransform == null || headTransform.GetComponentInChildren<Camera>() == null)
                {
                    headTransform = Camera.main.transform;
                }
            }

            if (headTransform == null)
                Debug.LogError("[SpoofDetector] No head transform and no Camera.main; detector cannot sample motion.", this);
        }

        private void FixedUpdate()
        {
            if (!IsServer || settings == null) return;
            if (headTransform == null) { ResolveHeadTransform(); if (headTransform == null) return; }
            if (_identity == null) return;

            // Only meaningful once the user is actually verified; reset state on each transition
            // so a fresh verified session starts from a clean baseline rather than stale motion.
            bool verified = _identity.IsVerified.Value;
            if (verified != _wasVerified)
            {
                _wasVerified = verified;
                ResetBaseline();
                SessionLogger.LogGlobal($"SpoofDetector baseline reset (verified={verified}).");
            }
            if (!verified) return;

            Vector3 pos = headTransform.position;

            // First sample after a reset only establishes the reference point. Previously
            // _lastHeadPos defaulted to (0,0,0), so frame one produced a ~50-100 m/s phantom
            // speed that was enqueued and poisoned mean/stddev for the whole first window.
            if (!_hasLastHeadPos)
            {
                _lastHeadPos = pos;
                _hasLastHeadPos = true;
                return;
            }

            float speed = (pos - _lastHeadPos).magnitude / Time.fixedDeltaTime;
            _lastHeadPos = pos;

            // Score against the baseline BEFORE adding this sample to it. Enqueueing first
            // let each sample inflate its own baseline and let sustained anomalies be
            // absorbed into "normal" within a couple of seconds.
            if (_recentSpeeds.Count >= BaselineWindow)
            {
                (float mean, float stdDev) = MeanAndStdDev(_recentSpeeds);
                EvaluateSample(speed, mean, stdDev);
            }

            _recentSpeeds.Enqueue(speed);
            if (_recentSpeeds.Count > BaselineWindow) _recentSpeeds.Dequeue();

            DecayStrikes();
        }

        private void EvaluateSample(float speed, float mean, float stdDev)
        {
            if (stdDev <= 0.0001f) return;
            if (speed < minSpeedForAnomaly) return;

            float z = Mathf.Abs(speed - mean) / stdDev;
            if (z <= settings.behaviouralAnomalyStdDevThreshold) return;
            if (Time.time - _lastStrikeTime < strikeRefractorySeconds) return;

            _lastStrikeTime = Time.time;
            _lastDecayTime = Time.time;
            FlagAnomaly(speed, mean, stdDev, z);
        }

        private void FlagAnomaly(float speed, float mean, float stdDev, float z)
        {
            _identity.AnomalyStrikes.Value++;
            int strikes = _identity.AnomalyStrikes.Value;

            Log($"ANOMALY {_identity.ClaimedUserIdString}: speed={speed:F2}m/s mean={mean:F2} sd={stdDev:F2} " +
                $"z={z:F2} (threshold {settings.behaviouralAnomalyStdDevThreshold:F2}) strikes={strikes}/{strikesBeforeRevoke}");

            if (strikes >= strikesBeforeRevoke && supersededByContinuousBiometrics)
            {
                Log($"SpoofDetector would revoke {_identity.ClaimedUserIdString} ({strikes} strikes) but is superseded " +
                    "by continuous biometric authentication; strike count reset, no revoke.");
                _identity.AnomalyStrikes.Value = 0;
                ResetBaseline();
                return;
            }

            if (strikes >= strikesBeforeRevoke)
            {
                // Revoking raises AvatarIdentity.OnServerRevoked, which NetworkAuthRelay
                // turns into a client-side re-auth request. This is what makes the
                // "authenticate -> move erratically -> revoked -> re-authenticate" loop close.
                _identity.ServerRevokeVerification(
                    $"behavioural anomaly: {strikes} strikes, last z={z:F2} at {speed:F2}m/s");
                ResetBaseline();
            }
        }

        private void DecayStrikes()
        {
            if (strikeDecaySeconds <= 0f) return;
            if (_identity.AnomalyStrikes.Value <= 0) { _lastDecayTime = Time.time; return; }
            if (Time.time - _lastDecayTime < strikeDecaySeconds) return;

            _lastDecayTime = Time.time;
            _identity.AnomalyStrikes.Value--;
            Log($"Strike decayed for {_identity.ClaimedUserIdString} after {strikeDecaySeconds:F0}s clean; " +
                $"strikes now {_identity.AnomalyStrikes.Value}.");
        }

        private void ResetBaseline()
        {
            _recentSpeeds.Clear();
            _hasLastHeadPos = false;
            _lastStrikeTime = -999f;
            _lastDecayTime = Time.time;
        }

        private void Log(string message)
        {
            if (sessionLogger != null) sessionLogger.Log(message);
            else SessionLogger.LogGlobal(message);
        }

        private static (float mean, float stdDev) MeanAndStdDev(Queue<float> values)
        {
            float sum = 0f;
            foreach (var v in values) sum += v;
            float mean = sum / values.Count;

            float sqSum = 0f;
            foreach (var v in values) sqSum += (v - mean) * (v - mean);
            float stdDev = Mathf.Sqrt(sqSum / values.Count);
            return (mean, stdDev);
        }
    }
}
