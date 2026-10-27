using UnityEngine;
using BoardroomXR.Logging;

namespace BoardroomXR.Auth
{
    /// <summary>
    /// Boardroom antechamber gate. Represents the doorway the player walks through
    /// on the way from the spawn point to the PIN keypad. When the local player's
    /// XR rig enters this trigger we start the MFA session, which runs
    /// <c>PinAuthenticator.Initialize(settings)</c> so the keypad is fully wired
    /// up before the player ever taps a digit.
    ///
    /// The session can be re-entered: after a revoke or a timeout, walking back
    /// through starts a fresh session rather than doing nothing. That is why the
    /// original one-shot <c>_sessionBegun</c> latch was replaced by
    /// <see cref="retriggerCooldownSeconds"/> plus an in-flight stage check - a
    /// permanent latch would block the revoke -> re-authenticate loop.
    /// </summary>
    /// <remarks>
    /// A kinematic <see cref="Rigidbody"/> is required on this object: the XR Origin
    /// moves via a <see cref="CharacterController"/> with no Rigidbody of its own, so
    /// without a Rigidbody on the trigger side Unity never delivers OnTrigger* to a
    /// static trigger collider. The kinematic Rigidbody makes the zone an active
    /// trigger without giving it physics motion.
    /// </remarks>
    [RequireComponent(typeof(Collider))]
    [RequireComponent(typeof(Rigidbody))]
    public class BoardroomAntechamberTrigger : MonoBehaviour
    {
        [Tooltip("The MFACoordinator on MFAPlayerRig whose session should begin " +
                 "when the player crosses this threshold. Found from the scene if left empty.")]
        public MFACoordinator coordinator;

        [Tooltip("Seconds before the same player can retrigger, so standing in the doorway " +
                 "does not restart the session repeatedly.")]
        public float retriggerCooldownSeconds = 3f;

        [Tooltip("Log every trigger entry to the console. Useful while confirming the zone fires at all.")]
        public bool verboseTriggerLogging = true;

        private float _lastTriggerTime = -999f;

        private void Reset()
        {
            // Make sure the collider authored on this object is a trigger, and the
            // Rigidbody is kinematic (a trigger zone, not a falling body).
            GetComponent<Collider>().isTrigger = true;
            var rb = GetComponent<Rigidbody>();
            rb.isKinematic = true;
            rb.useGravity = false;
        }

        private void Awake()
        {
            if (coordinator == null) coordinator = FindAnyObjectByType<MFACoordinator>();
        }

        private void OnTriggerEnter(Collider other)
        {
            // Log every entry so we can confirm from the console whether the zone
            // fires at all and what walked into it.
            if (verboseTriggerLogging)
            {
                Debug.Log($"[BoardroomAntechamberTrigger] OnTriggerEnter by '{other.name}' " +
                          $"(type={other.GetType().Name}, tag={other.tag}), " +
                          $"secondsSinceLastTrigger={Time.time - _lastTriggerTime:F1}", this);
            }

            if (Time.time - _lastTriggerTime < retriggerCooldownSeconds) return;

            // The XR Origin drives locomotion through a CharacterController, which is
            // itself a Collider, so it is the collider reported here. Matching on the
            // player rig keeps hands/controllers/other colliders from triggering the
            // session prematurely.
            if (!IsPlayerRig(other))
            {
                if (verboseTriggerLogging)
                {
                    Debug.Log($"[BoardroomAntechamberTrigger] Ignoring '{other.name}' - " +
                              "not the player rig.", this);
                }
                return;
            }

            if (coordinator == null)
            {
                Debug.LogWarning(
                    "[BoardroomAntechamberTrigger] No MFACoordinator assigned or found; " +
                    "cannot begin the MFA session.", this);
                return;
            }

            // Only start when there is nothing already in flight; BeginSession
            // also guards this, but logging here makes the demo trace clearer.
            if (coordinator.CurrentStage == MFAStage.Pin ||
                coordinator.CurrentStage == MFAStage.Gesture ||
                coordinator.CurrentStage == MFAStage.Otp ||
                coordinator.CurrentStage == MFAStage.Passed) // already verified: walking through must not restart MFA
            {
                if (verboseTriggerLogging)
                {
                    Debug.Log($"[BoardroomAntechamberTrigger] Session already in progress " +
                              $"(stage={coordinator.CurrentStage}); not restarting.", this);
                }
                return;
            }

            _lastTriggerTime = Time.time;

            Debug.Log("[BoardroomAntechamberTrigger] Player entered antechamber - " +
                      "calling MFACoordinator.BeginSession().", this);
            SessionLogger.LogGlobal($"Player crossed the antechamber threshold ({other.name}); starting MFA.");

            coordinator.BeginSession();
        }

        /// <summary>
        /// The XR Origin normally drives locomotion through a CharacterController,
        /// but not every rig setup has one - so also accept any collider whose
        /// hierarchy contains the main XR camera.
        /// </summary>
        private static bool IsPlayerRig(Collider other)
        {
            if (other is CharacterController) return true;
            if (Camera.main == null) return false;
            return Camera.main.transform.IsChildOf(other.transform.root);
        }
    }
}
