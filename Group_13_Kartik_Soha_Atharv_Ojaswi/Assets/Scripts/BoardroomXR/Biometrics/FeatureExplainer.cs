using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace BoardroomXR.Biometrics
{
    /// <summary>
    /// Turns the 16 kinematic features into something a person watching the demo can
    /// act on. A z-score means nothing to an examiner; "Eye height 11 cm lower than
    /// A. Mehta's profile" does, and it is derived from exactly the same numbers the
    /// lockout decision uses - the live feature value against the enrolled mean.
    ///
    /// Each feature carries a human label, its unit, and how a difference should be
    /// worded (a length in centimetres, an angle in degrees, or a rate as a ratio).
    /// </summary>
    public static class FeatureExplainer
    {
        public enum Kind { Rate, Length, Angle }

        public class Info
        {
            public string label;      // human name
            public string unit;       // unit shown on the SOC panel
            public Kind kind;
            public string up;         // word when the live value is above the template
            public string down;       // word when it is below
            public string shortLabel; // for the 16-bar chart

            public Info(string label, string shortLabel, string unit, Kind kind, string up, string down)
            {
                this.label = label; this.shortLabel = shortLabel; this.unit = unit;
                this.kind = kind; this.up = up; this.down = down;
            }
        }

        /// <summary>Indexed exactly like <see cref="KinematicFeatures.Names"/>.</summary>
        public static readonly Info[] Table =
        {
            /* 0  head_speed_mean      */ new Info("Head movement speed",        "Head speed",      "m/s",   Kind.Rate,   "faster", "slower"),
            /* 1  head_speed_sd        */ new Info("Head speed variability",     "Head speed var",  "m/s",   Kind.Rate,   "more variable", "steadier"),
            /* 2  head_angvel_mean     */ new Info("Head turn rate",             "Head turn",       "deg/s", Kind.Rate,   "faster", "slower"),
            /* 3  head_angvel_sd       */ new Info("Head turn variability",      "Head turn var",   "deg/s", Kind.Rate,   "more variable", "steadier"),
            /* 4  head_accel_mean      */ new Info("Head acceleration",          "Head accel",      "m/s2",  Kind.Rate,   "sharper", "gentler"),
            /* 5  head_jerk_mean       */ new Info("Head motion smoothness",     "Head jerk",       "m/s3",  Kind.Rate,   "jerkier", "smoother"),
            /* 6  rhand_speed_mean     */ new Info("Right hand speed",           "R hand speed",    "m/s",   Kind.Rate,   "faster", "slower"),
            /* 7  rhand_speed_sd       */ new Info("Right hand speed variability","R hand var",     "m/s",   Kind.Rate,   "more variable", "steadier"),
            /* 8  lhand_speed_mean     */ new Info("Left hand speed",            "L hand speed",    "m/s",   Kind.Rate,   "faster", "slower"),
            /* 9  lhand_speed_sd       */ new Info("Left hand speed variability","L hand var",      "m/s",   Kind.Rate,   "more variable", "steadier"),
            /* 10 head_rhand_dist_mean */ new Info("Right arm reach",             "R arm reach",     "cm",    Kind.Length, "further out", "closer in"),
            /* 11 head_rhand_dist_sd   */ new Info("Right arm reach variability", "R reach var",     "cm",    Kind.Length, "more variable", "steadier"),
            /* 12 head_lhand_dist_mean */ new Info("Left arm reach",              "L arm reach",     "cm",    Kind.Length, "further out", "closer in"),
            /* 13 head_height_mean     */ new Info("Eye height",                  "Eye height",      "cm",    Kind.Length, "higher", "lower"),
            /* 14 head_pitch_mean      */ new Info("Head tilt",                   "Head tilt",       "deg",   Kind.Angle,  "further down", "further up"),
            /* 15 hand_span_mean       */ new Info("Hand separation",             "Hand span",       "cm",    Kind.Length, "wider apart", "closer together"),
        };

        public struct Reason
        {
            public int index;
            public float absZ;
            public string text;
        }

        /// <summary>
        /// One plain-English sentence for feature <paramref name="i"/>: how the live
        /// window differs from the enrolled profile, in that feature's own units.
        /// </summary>
        public static string Explain(int i, float value, float mean, string whose)
        {
            if (i < 0 || i >= Table.Length) return "";
            var info = Table[i];
            string owner = string.IsNullOrEmpty(whose) ? "the enrolled profile" : $"{whose}'s profile";
            bool above = value >= mean;
            string word = above ? info.up : info.down;

            switch (info.kind)
            {
                case Kind.Length:
                {
                    float cm = Mathf.Abs(value - mean) * 100f;
                    return $"{info.label} {Fmt(cm, cm < 10f ? 1 : 0)} cm {word} than {owner}";
                }
                case Kind.Angle:
                {
                    float deg = Mathf.Abs(value - mean);
                    return $"{info.label} {Fmt(deg, deg < 10f ? 1 : 0)} deg {word} than {owner}";
                }
                default:
                {
                    float m = Mathf.Abs(mean);
                    if (m < 1e-4f)
                        return $"{info.label} {word} than {owner} ({Fmt(value, 2)} vs {Fmt(mean, 2)} {info.unit})";

                    float ratio = Mathf.Abs(value) / m;
                    if (ratio >= 1.45f) return $"{info.label} {Fmt(ratio, 1)}x {word} than {owner}";
                    if (ratio <= 0.69f) return $"{info.label} {Fmt(1f / Mathf.Max(ratio, 1e-3f), 1)}x {word} than {owner}";

                    float pct = Mathf.Abs(ratio - 1f) * 100f;
                    if (pct < 1f) return $"{info.label} matches {owner}";
                    return $"{info.label} {Fmt(pct, 0)}% {word} than {owner}";
                }
            }
        }

        /// <summary>
        /// The <paramref name="count"/> features furthest from the template in the last
        /// scored window, worst first, each already phrased for a human.
        /// </summary>
        public static List<Reason> TopReasons(float[] features, float[] z, BiometricTemplate template,
                                              string whose, int count = 3, float minAbsZ = 0.75f)
        {
            var reasons = new List<Reason>();
            if (features == null || z == null || template == null) return reasons;

            var order = new List<int>(KinematicFeatures.Count);
            for (int i = 0; i < KinematicFeatures.Count; i++) order.Add(i);
            order.Sort((a, b) => Mathf.Abs(z[b]).CompareTo(Mathf.Abs(z[a])));

            foreach (int i in order)
            {
                if (reasons.Count >= count) break;
                float absZ = Mathf.Abs(z[i]);
                if (absZ < minAbsZ) break;
                reasons.Add(new Reason
                {
                    index = i,
                    absZ = absZ,
                    text = Explain(i, features[i], template.mean[i], whose)
                });
            }
            return reasons;
        }

        /// <summary>Single worst reason, or an empty string when nothing stands out.</summary>
        public static string TopReason(float[] features, float[] z, BiometricTemplate template, string whose)
        {
            var r = TopReasons(features, z, template, whose, 1);
            return r.Count > 0 ? r[0].text : "";
        }

        /// <summary>Green below 1 sigma, amber to 2.5, red beyond - used by every deviation bar.</summary>
        public static Color DeviationColor(float absZ)
        {
            if (absZ < 1f) return new Color(0.35f, 0.85f, 0.45f);
            if (absZ < 2.5f) return new Color(1f, 0.75f, 0.25f);
            return new Color(1f, 0.33f, 0.30f);
        }

        private static string Fmt(float v, int decimals) =>
            v.ToString("F" + decimals, CultureInfo.InvariantCulture);
    }
}
