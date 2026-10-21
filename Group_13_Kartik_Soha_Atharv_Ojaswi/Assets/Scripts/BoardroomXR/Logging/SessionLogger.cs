using System;
using System.IO;
using System.Text;
using UnityEngine;

namespace BoardroomXR.Logging
{
    /// <summary>
    /// Writes timestamped MFA/session events to a per-run log file under
    /// Application.persistentDataPath/MFALogs.
    ///
    /// Every line is produced by code that actually ran: there are no seeded,
    /// replayed or example entries. If a factor was never reached, no line for
    /// it appears - that absence is the honest record.
    ///
    /// Writes go through a buffered StreamWriter and are flushed on an
    /// interval (and on pause/quit) rather than reopening the file per call,
    /// so logging during a headset session does not stall frames.
    /// </summary>
    public class SessionLogger : MonoBehaviour
    {
        [Tooltip("If true, also echoes every entry to the Unity console / adb logcat.")]
        public bool mirrorToConsole = true;

        [Tooltip("Seconds between automatic flushes of the write buffer to disk.")]
        public float flushIntervalSeconds = 2f;

        /// <summary>Most recently created logger, so code without an Inspector reference can still log.</summary>
        public static SessionLogger Active { get; private set; }

        /// <summary>Full path of this run's log file. Shown in the console at startup.</summary>
        public string LogPath { get; private set; }

        private StreamWriter _writer;
        private float _lastFlush;
        private readonly object _gate = new();

        private void Awake()
        {
            Active = this;

            try
            {
                string dir = Path.Combine(Application.persistentDataPath, "MFALogs");
                Directory.CreateDirectory(dir);
                LogPath = Path.Combine(dir, $"session_{DateTime.UtcNow:yyyyMMdd_HHmmss}.log");

                _writer = new StreamWriter(
                    new FileStream(LogPath, FileMode.Create, FileAccess.Write, FileShare.Read),
                    Encoding.UTF8) { AutoFlush = false };
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[SessionLogger] Could not open log file: {e.Message}. Falling back to console only.");
                _writer = null;
            }

            // First real entry: proves the file corresponds to an actual run.
            Log($"=== Session log opened. Unity {Application.unityVersion}, platform {Application.platform}, path {LogPath} ===");
            Debug.Log($"[SessionLogger] Writing this run's events to: {LogPath}");
        }

        /// <summary>Appends one timestamped line. Safe to call before Awake has opened the file.</summary>
        public void Log(string message)
        {
            string line = $"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss.fff}Z] [t={Time.realtimeSinceStartup:F3}] {message}";

            lock (_gate)
            {
                try
                {
                    _writer?.WriteLine(line);
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[SessionLogger] write failed: {e.Message}");
                }
            }

            if (mirrorToConsole) Debug.Log(line);
        }

        /// <summary>Convenience for code paths that have no serialized logger reference.</summary>
        public static void LogGlobal(string message)
        {
            if (Active != null) Active.Log(message);
            else Debug.Log($"[SessionLogger:none] {message}");
        }

        private void Update()
        {
            if (_writer == null) return;
            if (Time.realtimeSinceStartup - _lastFlush < flushIntervalSeconds) return;
            _lastFlush = Time.realtimeSinceStartup;
            Flush();
        }

        /// <summary>Forces buffered lines to disk. Called on interval, on pause and on quit.</summary>
        public void Flush()
        {
            lock (_gate)
            {
                try { _writer?.Flush(); }
                catch (Exception e) { Debug.LogWarning($"[SessionLogger] flush failed: {e.Message}"); }
            }
        }

        private void OnApplicationPause(bool paused)
        {
            // Taking the headset off / backgrounding the app must not lose the tail of the log.
            if (paused) { Log("Application paused."); Flush(); }
            else Log("Application resumed.");
        }

        private void OnApplicationFocus(bool focused)
        {
            if (!focused) Flush();
        }

        private void OnDestroy()
        {
            Log("=== Session log closed. ===");
            lock (_gate)
            {
                try { _writer?.Flush(); _writer?.Dispose(); }
                catch (Exception e) { Debug.LogWarning($"[SessionLogger] close failed: {e.Message}"); }
                _writer = null;
            }
            if (Active == this) Active = null;
        }
    }
}
