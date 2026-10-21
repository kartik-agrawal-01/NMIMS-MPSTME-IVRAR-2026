using System.Collections.Generic;
using UnityEngine;

namespace BoardroomXR.Biometrics
{
    /// <summary>One 6-DoF pose sample of the head and both controllers.</summary>
    public struct KinematicSample
    {
        public float t;
        public Vector3 headPos;
        public Quaternion headRot;
        public Vector3 leftPos;
        public Vector3 rightPos;
        public bool hasLeft;
        public bool hasRight;
    }

    /// <summary>
    /// Turns a sliding window of head/controller samples into a fixed-length
    /// behavioural feature vector (velocity, angular velocity, acceleration,
    /// jerk and body-geometry features). Pure maths, no scene access, so the
    /// same code runs in the headset build and in the automated test runs.
    /// </summary>
    public static class KinematicFeatures
    {
        public const int Count = 16;

        public static readonly string[] Names =
        {
            "head_speed_mean",        // 0  m/s
            "head_speed_sd",          // 1
            "head_angvel_mean",       // 2  deg/s
            "head_angvel_sd",         // 3
            "head_accel_mean",        // 4  m/s^2
            "head_jerk_mean",         // 5  m/s^3
            "rhand_speed_mean",       // 6
            "rhand_speed_sd",         // 7
            "lhand_speed_mean",       // 8
            "lhand_speed_sd",         // 9
            "head_rhand_dist_mean",   // 10 m (arm reach / posture)
            "head_rhand_dist_sd",     // 11
            "head_lhand_dist_mean",   // 12
            "head_height_mean",       // 13 m (eye height)
            "head_pitch_mean",        // 14 deg
            "hand_span_mean",         // 15 m (distance between controllers)
        };

        /// <summary>Returns null if the window is too short to differentiate three times.</summary>
        public static float[] Extract(IReadOnlyList<KinematicSample> w)
        {
            int n = w.Count;
            if (n < 6) return null;

            var headSpeed = new List<float>(n);
            var headAng = new List<float>(n);
            var rSpeed = new List<float>(n);
            var lSpeed = new List<float>(n);
            var headVel = new List<Vector3>(n);
            var headVelT = new List<float>(n);

            for (int i = 1; i < n; i++)
            {
                float dt = w[i].t - w[i - 1].t;
                if (dt <= 1e-4f) continue;

                Vector3 v = (w[i].headPos - w[i - 1].headPos) / dt;
                headVel.Add(v);
                headVelT.Add(w[i].t);
                headSpeed.Add(v.magnitude);
                headAng.Add(Quaternion.Angle(w[i - 1].headRot, w[i].headRot) / dt);
                if (w[i].hasRight && w[i - 1].hasRight)
                    rSpeed.Add((w[i].rightPos - w[i - 1].rightPos).magnitude / dt);
                if (w[i].hasLeft && w[i - 1].hasLeft)
                    lSpeed.Add((w[i].leftPos - w[i - 1].leftPos).magnitude / dt);
            }
            if (headVel.Count < 4) return null;

            // Acceleration and jerk from successive velocity differences.
            var acc = new List<Vector3>(headVel.Count);
            var accT = new List<float>(headVel.Count);
            for (int i = 1; i < headVel.Count; i++)
            {
                float dt = headVelT[i] - headVelT[i - 1];
                if (dt <= 1e-4f) continue;
                acc.Add((headVel[i] - headVel[i - 1]) / dt);
                accT.Add(headVelT[i]);
            }
            float accMean = 0f;
            foreach (var a in acc) accMean += a.magnitude;
            accMean = acc.Count > 0 ? accMean / acc.Count : 0f;

            float jerkMean = 0f; int jerkN = 0;
            for (int i = 1; i < acc.Count; i++)
            {
                float dt = accT[i] - accT[i - 1];
                if (dt <= 1e-4f) continue;
                jerkMean += ((acc[i] - acc[i - 1]) / dt).magnitude;
                jerkN++;
            }
            jerkMean = jerkN > 0 ? jerkMean / jerkN : 0f;

            // Geometry features over all samples.
            var dR = new List<float>(n);
            float dL = 0f, height = 0f, pitch = 0f, span = 0f;
            int nL = 0, nSpan = 0;
            for (int i = 0; i < n; i++)
            {
                height += w[i].headPos.y;
                pitch += SignedPitch(w[i].headRot);
                if (w[i].hasRight) dR.Add(Vector3.Distance(w[i].headPos, w[i].rightPos));
                if (w[i].hasLeft) { dL += Vector3.Distance(w[i].headPos, w[i].leftPos); nL++; }
                if (w[i].hasLeft && w[i].hasRight) { span += Vector3.Distance(w[i].leftPos, w[i].rightPos); nSpan++; }
            }

            var f = new float[Count];
            MeanSd(headSpeed, out f[0], out f[1]);
            MeanSd(headAng, out f[2], out f[3]);
            f[4] = accMean;
            f[5] = jerkMean;
            MeanSd(rSpeed, out f[6], out f[7]);
            MeanSd(lSpeed, out f[8], out f[9]);
            MeanSd(dR, out f[10], out f[11]);
            f[12] = nL > 0 ? dL / nL : 0f;
            f[13] = height / n;
            f[14] = pitch / n;
            f[15] = nSpan > 0 ? span / nSpan : 0f;
            return f;
        }

        private static float SignedPitch(Quaternion q)
        {
            float x = q.eulerAngles.x;
            return x > 180f ? x - 360f : x; // positive = looking down
        }

        private static void MeanSd(List<float> v, out float mean, out float sd)
        {
            mean = 0f; sd = 0f;
            if (v.Count == 0) return;
            foreach (var x in v) mean += x;
            mean /= v.Count;
            foreach (var x in v) sd += (x - mean) * (x - mean);
            sd = Mathf.Sqrt(sd / v.Count);
        }
    }
}
