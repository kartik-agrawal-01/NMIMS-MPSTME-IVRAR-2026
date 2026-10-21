using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

namespace BoardroomXR.Biometrics
{
    /// <summary>
    /// Logs 6-DoF head and controller kinematic telemetry (position, rotation,
    /// linear velocity, angular velocity) at a fixed sample rate, to a CSV file
    /// per session. This is the raw data source for the continuous behavioural
    /// biometric authentication study: each CSV represents one
    /// enrolment/verification/impostor session and is later fed into the
    /// Python analysis pipeline (feature extraction -> classifier -> EER/ROC).
    ///
    /// Attach to a persistent manager object and assign headTransform plus the
    /// left/right controller transforms from the XR rig.
    /// </summary>
    public class KinematicTelemetryLogger : MonoBehaviour
    {
        [Header("Tracked Transforms")]
        public Transform headTransform;
        public Transform leftControllerTransform;
        public Transform rightControllerTransform;

        [Header("Sampling")]
        [Tooltip("Samples per second. Study protocol specifies 60Hz.")]
        public float sampleRateHz = 60f;

        [Header("Session Metadata (set before StartSession)")]
        public string participantId = "P00";
        [Tooltip("genuine_enrollment | genuine_verification | impostor")]
        public string sessionType = "genuine_verification";
        public int sessionIndex = 1;

        private StreamWriter _writer;
        private Coroutine _sampleRoutine;
        private float _sessionStartTime;
        private bool _isLogging;

        private Vector3 _lastHeadPos, _lastLeftPos, _lastRightPos;
        private Quaternion _lastHeadRot, _lastLeftRot, _lastRightRot;

        public string CurrentFilePath { get; private set; }

        /// <summary>True while a session is actively sampling to CSV.</summary>
        public bool IsLogging => _isLogging;

        /// <summary>Seconds since the current session started, or 0 when idle.</summary>
        public float ElapsedSeconds => _isLogging ? Time.realtimeSinceStartup - _sessionStartTime : 0f;

        /// <summary>Call to begin a new logged session. Creates a fresh CSV file.</summary>
        public void StartSession()
        {
            if (_isLogging)
            {
                Debug.LogWarning("[KinematicTelemetryLogger] Session already in progress; ignoring StartSession().");
                return;
            }

            string dir = Path.Combine(Application.persistentDataPath, "Telemetry");
            Directory.CreateDirectory(dir);
            string fileName = $"{participantId}_{sessionType}_{sessionIndex}_{DateTime.UtcNow:yyyyMMdd_HHmmss}.csv";
            CurrentFilePath = Path.Combine(dir, fileName);

            _writer = new StreamWriter(CurrentFilePath, false, Encoding.UTF8);
            _writer.WriteLine(
                "t," +
                "head_px,head_py,head_pz,head_qx,head_qy,head_qz,head_qw,head_lin_vel,head_ang_vel," +
                "lhand_px,lhand_py,lhand_pz,lhand_qx,lhand_qy,lhand_qz,lhand_qw,lhand_lin_vel,lhand_ang_vel," +
                "rhand_px,rhand_py,rhand_pz,rhand_qx,rhand_qy,rhand_qz,rhand_qw,rhand_lin_vel,rhand_ang_vel");

            _sessionStartTime = Time.realtimeSinceStartup;
            _lastHeadPos = headTransform ? headTransform.position : Vector3.zero;
            _lastHeadRot = headTransform ? headTransform.rotation : Quaternion.identity;
            _lastLeftPos = leftControllerTransform ? leftControllerTransform.position : Vector3.zero;
            _lastLeftRot = leftControllerTransform ? leftControllerTransform.rotation : Quaternion.identity;
            _lastRightPos = rightControllerTransform ? rightControllerTransform.position : Vector3.zero;
            _lastRightRot = rightControllerTransform ? rightControllerTransform.rotation : Quaternion.identity;

            _isLogging = true;
            _sampleRoutine = StartCoroutine(SampleLoop());
            Debug.Log($"[KinematicTelemetryLogger] Session started -> {CurrentFilePath}");
        }

        /// <summary>Call to end the current session and flush the CSV to disk.</summary>
        public void EndSession()
        {
            if (!_isLogging) return;
            _isLogging = false;
            if (_sampleRoutine != null) StopCoroutine(_sampleRoutine);
            _writer?.Flush();
            _writer?.Dispose();
            _writer = null;
            Debug.Log($"[KinematicTelemetryLogger] Session ended -> {CurrentFilePath}");
        }

        private IEnumerator SampleLoop()
        {
            var wait = new WaitForSeconds(1f / Mathf.Max(1f, sampleRateHz));
            while (_isLogging)
            {
                SampleOnce();
                yield return wait;
            }
        }

        private void SampleOnce()
        {
            float t = Time.realtimeSinceStartup - _sessionStartTime;
            float dt = 1f / Mathf.Max(1f, sampleRateHz);

            string headRow = SampleTransform(headTransform, ref _lastHeadPos, ref _lastHeadRot, dt);
            string lhandRow = SampleTransform(leftControllerTransform, ref _lastLeftPos, ref _lastLeftRot, dt);
            string rhandRow = SampleTransform(rightControllerTransform, ref _lastRightPos, ref _lastRightRot, dt);

            _writer.WriteLine($"{t:F4},{headRow},{lhandRow},{rhandRow}");
        }

        /// <summary>Returns "px,py,pz,qx,qy,qz,qw,linVel,angVel" for one tracked transform.</summary>
        private string SampleTransform(Transform tr, ref Vector3 lastPos, ref Quaternion lastRot, float dt)
        {
            if (tr == null) return "0,0,0,0,0,0,1,0,0";

            Vector3 pos = tr.position;
            Quaternion rot = tr.rotation;

            float linVel = (pos - lastPos).magnitude / dt;

            // Angular velocity magnitude in deg/s from the shortest rotation between samples.
            float angDeg = Quaternion.Angle(lastRot, rot);
            float angVel = angDeg / dt;

            lastPos = pos;
            lastRot = rot;

            return $"{pos.x:F5},{pos.y:F5},{pos.z:F5},{rot.x:F5},{rot.y:F5},{rot.z:F5},{rot.w:F5},{linVel:F5},{angVel:F5}";
        }

        private void OnDisable() => EndSession();
        private void OnApplicationQuit() => EndSession();
    }
}
