using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace BoardroomXR.Biometrics
{
    /// <summary>
    /// A user's enrolled behavioural template: the mean and spread of each
    /// kinematic feature over their enrolment windows. Scoring uses a
    /// diagonal-covariance Mahalanobis distance (a full 16x16 covariance is
    /// unstable with the ~100 overlapping windows a short enrolment yields):
    ///
    ///   z_i  = (f_i - mu_i) / sigma_i          (clamped to +-zClamp)
    ///   D^2  = (1/k) * sum_i z_i^2
    ///   S    = exp(-0.5 * D^2)                  in (0, 1], 1 = identical
    /// </summary>
    [Serializable]
    public class BiometricTemplate
    {
        public string userId;
        public string createdUtc;
        public int windowCount;
        public float[] mean = new float[KinematicFeatures.Count];
        public float[] sd = new float[KinematicFeatures.Count];

        public const float ZClamp = 6f;

        // Smallest spread each feature may have, in its own units (see KinematicFeatures.Names).
        private static readonly float[] AbsFloor =
        {
            0.02f, 0.02f,   // head speed mean/sd (m/s)
            2f, 2f,         // head angular velocity mean/sd (deg/s)
            0.1f, 1f,       // head accel (m/s^2), jerk (m/s^3)
            0.03f, 0.03f,   // right hand speed mean/sd
            0.03f, 0.03f,   // left hand speed mean/sd
            0.01f, 0.005f,  // head-right hand distance mean/sd (m)
            0.01f,          // head-left hand distance (m)
            0.015f,         // eye height (m)
            1.5f,           // head pitch (deg)
            0.02f,          // hand span (m)
        };

        public static BiometricTemplate Build(string userId, List<float[]> windows)
        {
            if (windows == null || windows.Count < 5) return null;
            int k = KinematicFeatures.Count;
            var t = new BiometricTemplate
            {
                userId = userId,
                createdUtc = DateTime.UtcNow.ToString("o"),
                windowCount = windows.Count
            };
            for (int i = 0; i < k; i++)
            {
                double m = 0;
                foreach (var w in windows) m += w[i];
                m /= windows.Count;
                double v = 0;
                foreach (var w in windows) v += (w[i] - m) * (w[i] - m);
                double s = Math.Sqrt(v / Math.Max(1, windows.Count - 1));
                // Floor the spread so a feature that barely varied during enrolment
                // (e.g. eye height) cannot turn millimetre noise into a huge z-score.
                float floor = Mathf.Max(AbsFloor[i], 0.02f * Mathf.Abs((float)m));
                t.mean[i] = (float)m;
                t.sd[i] = Mathf.Max((float)s, floor);
            }
            return t;
        }

        public float Score(float[] f, float[] zOut = null)
        {
            int k = KinematicFeatures.Count;
            float d2 = 0f;
            for (int i = 0; i < k; i++)
            {
                float z = Mathf.Clamp((f[i] - mean[i]) / sd[i], -ZClamp, ZClamp);
                if (zOut != null) zOut[i] = z;
                d2 += z * z;
            }
            d2 /= k;
            return Mathf.Exp(-0.5f * d2);
        }

        // ---------- persistence ----------

        public static string Dir => Path.Combine(Application.persistentDataPath, "BiometricTemplates");

        public static string PathFor(string userId)
        {
            foreach (char c in Path.GetInvalidFileNameChars()) userId = userId.Replace(c, '_');
            return Path.Combine(Dir, $"template_{userId}.json");
        }

        public void Save()
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(PathFor(userId), JsonUtility.ToJson(this, true));
        }

        public static BiometricTemplate Load(string userId)
        {
            string p = PathFor(userId);
            if (!File.Exists(p)) return null;
            try
            {
                var t = JsonUtility.FromJson<BiometricTemplate>(File.ReadAllText(p));
                if (t?.mean == null || t.mean.Length != KinematicFeatures.Count) return null;
                return t;
            }
            catch { return null; }
        }

        public static void Delete(string userId)
        {
            string p = PathFor(userId);
            if (File.Exists(p)) File.Delete(p);
        }
    }
}
