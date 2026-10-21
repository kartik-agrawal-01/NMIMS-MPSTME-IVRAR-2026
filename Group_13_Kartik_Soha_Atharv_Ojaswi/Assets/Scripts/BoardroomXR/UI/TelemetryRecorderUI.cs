using UnityEngine;
using UnityEngine.UI;
using BoardroomXR.Biometrics;

namespace BoardroomXR.UI
{
    /// <summary>
    /// TEST HARNESS - a small world-space control panel for driving
    /// <see cref="KinematicTelemetryLogger"/> by hand: set the session metadata,
    /// Start/Stop a recording, and watch the live status/elapsed time so you can
    /// confirm a session is actually running before you rely on the CSV.
    ///
    /// Session Type is a fixed dropdown (genuine_enrollment / genuine_verification
    /// / impostor) rather than free text, because those tokens become part of the
    /// CSV filename and the Python analysis pipeline parses them - a typo there
    /// silently drops a session from the study.
    /// </summary>
    public class TelemetryRecorderUI : MonoBehaviour
    {
        public KinematicTelemetryLogger logger;

        [Header("Controls")]
        public InputField participantIdField;
        public Dropdown sessionTypeDropdown;
        public InputField sessionIndexField;
        public Text statusText;

        /// <summary>The exact session-type tokens the analysis pipeline expects.</summary>
        public static readonly string[] SessionTypes =
        {
            "genuine_enrollment",
            "genuine_verification",
            "impostor",
        };

        private void Start() => RefreshStatus();

        public void OnStartClicked()
        {
            if (logger == null)
            {
                Debug.LogWarning("[TelemetryRecorderUI] logger not assigned.", this);
                return;
            }
            if (logger.IsLogging) return;

            if (participantIdField != null && !string.IsNullOrWhiteSpace(participantIdField.text))
                logger.participantId = participantIdField.text.Trim();

            if (sessionTypeDropdown != null && sessionTypeDropdown.options.Count > 0)
                logger.sessionType = sessionTypeDropdown.options[sessionTypeDropdown.value].text;

            if (sessionIndexField != null && int.TryParse(sessionIndexField.text, out int idx))
                logger.sessionIndex = Mathf.Max(1, idx);

            logger.StartSession();
            RefreshStatus();
        }

        public void OnStopClicked()
        {
            if (logger == null) return;
            logger.EndSession();
            RefreshStatus();
        }

        private void Update()
        {
            // Keep the elapsed timer ticking while recording.
            if (logger != null && logger.IsLogging) RefreshStatus();
        }

        private void RefreshStatus()
        {
            if (statusText == null || logger == null) return;

            if (logger.IsLogging)
                statusText.text =
                    $"Recording: {logger.participantId}_{logger.sessionType}_{logger.sessionIndex}\n" +
                    $"{logger.ElapsedSeconds:F1}s";
            else
                statusText.text = "Idle";
        }
    }
}
