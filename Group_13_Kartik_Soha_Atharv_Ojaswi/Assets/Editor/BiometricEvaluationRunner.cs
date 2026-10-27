using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;
using BoardroomXR.Biometrics;

namespace BoardroomXR.EditorTools
{
    /// <summary>
    /// Automated evaluation of the continuous biometric pipeline: 15 scripted test runs
    /// (R01-R15) pushed through the SAME ContinuousBiometricEngine, KinematicFeatures and
    /// BiometricTemplate code the headset build uses.
    ///
    /// Each run: 60 s enrolment of a scripted motion profile, 45 s of the same profile in
    /// a new "session" (genuine use, with session-to-session variation), then a headset
    /// handover to a different profile for 20 s (impostor). Motion is generated from
    /// seeded parameters (eye height, arm reach, sway, look-around, hand activity), so the
    /// results are reproducible. These are automated system test runs with scripted motion,
    /// not human participants.
    ///
    /// Menu: BoardroomXR > Run Biometric Evaluation (R01-R15)
    /// Output: Tools/biometric_test_runs/ (run summary, per-window scores, EER report).
    /// </summary>
    public static class BiometricEvaluationRunner
    {
        private const int Runs = 15;
        private const int Seed = 20261019;
        private const float Hz = 60f;
        private const float EnrolS = 60f, GenuineS = 45f, ImpostorS = 20f;

        private class Profile
        {
            public float eye, reach, shoulder, drop, swayA, swayF, yawA, yawF, pitch0, pitchA, handA, handF, phase;
        }

        [MenuItem("BoardroomXR/Run Biometric Evaluation (R01-R15)")]
        public static void RunFromMenu()
        {
            string dir = Path.Combine(Path.GetDirectoryName(Application.dataPath), "Tools", "biometric_test_runs");
            string report = Run(dir);
            Debug.Log(report);
            if (!Application.isBatchMode)
                EditorUtility.DisplayDialog("Biometric evaluation complete", report, "OK");
        }

        public static string Run(string outDir)
        {
            Directory.CreateDirectory(outDir);

            // Profiles come from their own generator, so the same 15 people are used for
            // every preset and the comparison below is apples to apples.
            var profileRng = new System.Random(Seed);
            var people = Enumerable.Range(0, Runs).Select(_ => RandomProfile(profileRng)).ToList();

            var presets = new[] { BiometricPreset.Strict, BiometricPreset.Balanced, BiometricPreset.Relaxed };
            var comparison = new StringBuilder("preset,threshold,k,false_lockouts_of_15,handovers_detected_of_15," +
                                               "mean_latency_s,sd_latency_s,max_latency_s\n");
            var results = new Dictionary<BiometricPreset, PresetResult>();

            foreach (var preset in presets)
            {
                var cfg = new BiometricConfig();
                cfg.ApplyPreset(preset);
                var result = RunAllRuns(people, cfg, preset == DefaultPreset);
                results[preset] = result;

                comparison.AppendLine(string.Join(",", preset.ToString(), F(cfg.matchThreshold),
                    cfg.violationsForLockout.ToString(), result.falseLockouts.ToString(), result.detected.ToString(),
                    result.latencies.Count > 0 ? F(result.latencies.Average()) : "",
                    result.latencies.Count > 1 ? F(StdDev(result.latencies)) : "",
                    result.latencies.Count > 0 ? F(result.latencies.Max()) : ""));
            }

            var main = results[DefaultPreset];
            var mainCfg = new BiometricConfig();
            mainCfg.ApplyPreset(DefaultPreset);

            (float eer, float thr) = ComputeEer(main.genuineScores, main.impostorScores);
            float far = main.impostorScores.Count(x => x >= mainCfg.matchThreshold) / (float)Math.Max(1, main.impostorScores.Count);
            float frr = main.genuineScores.Count(x => x < mainCfg.matchThreshold) / (float)Math.Max(1, main.genuineScores.Count);

            File.WriteAllText(Path.Combine(outDir, "runs_summary.csv"), main.summary.ToString());
            File.WriteAllText(Path.Combine(outDir, "window_scores.csv"), main.windows.ToString());
            File.WriteAllText(Path.Combine(outDir, "preset_comparison.csv"), comparison.ToString());

            string lat = main.latencies.Count > 0
                ? $"{main.latencies.Average():F2} s mean, {StdDev(main.latencies):F2} s SD, max {main.latencies.Max():F2} s"
                : "n/a";

            var report = new StringBuilder();
            report.AppendLine($"Automated biometric evaluation, {Runs} scripted test runs (R01-R{Runs:00}), seed {Seed}");
            report.AppendLine($"Window {mainCfg.windowSeconds:F1} s, hop {mainCfg.hopSeconds:F1} s, enrol {EnrolS:F0} s");
            report.AppendLine($"Default operating point: {mainCfg.PresetLabel}");
            report.AppendLine($"Genuine windows: {main.genuineScores.Count}, impostor windows: {main.impostorScores.Count}");
            report.AppendLine($"Window-level EER: {eer * 100f:F2}% at threshold {thr:F3}  (a property of the scores, not of the preset)");
            report.AppendLine($"At the default threshold {mainCfg.matchThreshold:F2}: FAR {far * 100f:F2}%, FRR {frr * 100f:F2}% " +
                              "(single window, before smoothing)");
            report.AppendLine($"Handovers detected and locked out: {main.detected}/{Runs}");
            report.AppendLine($"False lockouts during genuine use: {main.falseLockouts}/{Runs}");
            report.AppendLine($"Lockout latency after handover: {lat}");
            report.AppendLine();
            report.AppendLine("Operating points (same 15 runs, same motion, only threshold and k differ):");
            report.AppendLine("  preset    thr    k   false lockouts   detected   mean latency");
            foreach (var preset in presets)
            {
                var c = new BiometricConfig();
                c.ApplyPreset(preset);
                var rr = results[preset];
                string ml = rr.latencies.Count > 0 ? $"{rr.latencies.Average():F2} s" : "n/a";
                report.AppendLine($"  {preset,-9} {c.matchThreshold,4:F2} {c.violationsForLockout,3}      " +
                                  $"{rr.falseLockouts,2}/{Runs}          {rr.detected,2}/{Runs}      {ml}");
            }
            report.AppendLine();
            report.AppendLine("These are automated system test runs with scripted motion, not human participants.");
            report.AppendLine($"Generated {DateTime.Now:yyyy-MM-dd HH:mm} by BiometricEvaluationRunner.");

            string text = report.ToString();
            File.WriteAllText(Path.Combine(outDir, "evaluation_report.txt"), text);
            return text;
        }

        private const BiometricPreset DefaultPreset = BiometricPreset.Balanced;

        private class PresetResult
        {
            public int falseLockouts, detected;
            public readonly List<float> latencies = new List<float>();
            public readonly List<float> genuineScores = new List<float>();
            public readonly List<float> impostorScores = new List<float>();
            public readonly StringBuilder summary = new StringBuilder();
            public readonly StringBuilder windows = new StringBuilder();
        }

        /// <summary>
        /// All 15 runs at one operating point. Motion is regenerated from a fresh seeded
        /// RNG each time this is called, so every preset sees identical movement and the
        /// only thing that changes between passes is the decision rule.
        /// </summary>
        private static PresetResult RunAllRuns(List<Profile> people, BiometricConfig cfg, bool collectDetail)
        {
            var rng = new System.Random(Seed + 1);
            var res = new PresetResult();
            res.summary.Append("run,enrol_windows,genuine_windows,genuine_mean_score,false_lockout_during_genuine," +
                               "impostor_profile,impostor_windows,impostor_mean_score,impostor_locked_out,lockout_latency_s\n");
            res.windows.Append("run,phase,t,raw_score\n");

            for (int r = 0; r < Runs; r++)
            {
                string id = $"R{r + 1:00}";
                int impIdx = (r + 1 + rng.Next(Runs - 1)) % Runs;
                if (impIdx == r) impIdx = (r + 1) % Runs;

                var genuineSession = Jitter(people[r], rng);
                var enrolSamples = Generate(people[r], 0f, EnrolS, rng);
                var genSamples = Generate(genuineSession, EnrolS, GenuineS, rng);
                var impSamples = Generate(Jitter(people[impIdx], rng), EnrolS + GenuineS, ImpostorS, rng);

                // --- live pipeline, exactly as on the headset ---
                var engine = new ContinuousBiometricEngine(cfg);
                bool lockedOut = false; float lockT = -1f;
                engine.OnLockout += _ => { lockedOut = true; };
                engine.Begin(id, null, 0f);
                foreach (var s in enrolSamples.Concat(genSamples).Concat(impSamples))
                {
                    engine.Push(s);
                    if (lockedOut && lockT < 0f) lockT = s.t;
                }
                float handover = EnrolS + GenuineS;
                bool falseLock = lockT >= 0f && lockT < handover;
                bool impLock = lockT >= handover;
                if (falseLock) res.falseLockouts++;
                if (impLock) { res.detected++; res.latencies.Add(lockT - handover); }

                // --- per-window scores against the template (for EER), without the lockout cut-off ---
                var tpl = engine.Template;
                var g = ScoreWindows(tpl, genSamples, cfg);
                var im = ScoreWindows(tpl, impSamples, cfg);
                res.genuineScores.AddRange(g.Select(x => x.score));
                res.impostorScores.AddRange(im.Select(x => x.score));

                if (!collectDetail) continue;
                foreach (var w in g) res.windows.AppendLine($"{id},genuine,{F(w.t)},{F(w.score)}");
                foreach (var w in im) res.windows.AppendLine($"{id},impostor,{F(w.t)},{F(w.score)}");
                res.summary.AppendLine(string.Join(",", id, tpl != null ? tpl.windowCount : 0, g.Count,
                    F(Mean(g.Select(x => x.score))), falseLock ? "Y" : "N", $"R{impIdx + 1:00}", im.Count,
                    F(Mean(im.Select(x => x.score))), impLock ? "Y" : "N", impLock ? F(lockT - handover) : ""));
            }
            return res;
        }

        // ---------------- motion generation ----------------

        private static Profile RandomProfile(System.Random r) => new Profile
        {
            eye = U(r, 1.48f, 1.80f), reach = U(r, 0.33f, 0.55f), shoulder = U(r, 0.15f, 0.24f), drop = U(r, 0.25f, 0.45f),
            swayA = U(r, 0.006f, 0.03f), swayF = U(r, 0.15f, 0.6f), yawA = U(r, 10f, 45f), yawF = U(r, 0.05f, 0.25f),
            pitch0 = U(r, -4f, 18f), pitchA = U(r, 3f, 12f), handA = U(r, 0.02f, 0.12f), handF = U(r, 0.3f, 1.2f),
            phase = U(r, 0f, 6.28f)
        };

        /// <summary>Same person on another occasion: small changes in posture and activity.</summary>
        private static Profile Jitter(Profile p, System.Random r) => new Profile
        {
            eye = p.eye + U(r, -0.012f, 0.012f), reach = p.reach * U(r, 0.96f, 1.04f), shoulder = p.shoulder * U(r, 0.97f, 1.03f),
            drop = p.drop * U(r, 0.96f, 1.04f), swayA = p.swayA * U(r, 0.85f, 1.15f), swayF = p.swayF * U(r, 0.9f, 1.1f),
            yawA = p.yawA * U(r, 0.85f, 1.15f), yawF = p.yawF * U(r, 0.9f, 1.1f), pitch0 = p.pitch0 + U(r, -2f, 2f),
            pitchA = p.pitchA * U(r, 0.85f, 1.15f), handA = p.handA * U(r, 0.85f, 1.15f), handF = p.handF * U(r, 0.9f, 1.1f),
            phase = U(r, 0f, 6.28f)
        };

        private static List<KinematicSample> Generate(Profile p, float t0, float dur, System.Random r)
        {
            var list = new List<KinematicSample>();
            int n = Mathf.RoundToInt(dur * Hz);
            for (int i = 0; i < n; i++)
            {
                float t = t0 + i / Hz;
                float ph = p.phase;
                Vector3 head = new Vector3(
                    p.swayA * Mathf.Sin(2f * Mathf.PI * p.swayF * t + ph) + N(r, 0.0015f),
                    p.eye + 0.4f * p.swayA * Mathf.Sin(2f * Mathf.PI * p.swayF * 1.7f * t + ph) + N(r, 0.0015f),
                    p.swayA * 0.7f * Mathf.Cos(2f * Mathf.PI * p.swayF * 0.8f * t + ph) + N(r, 0.0015f));
                float yaw = p.yawA * Mathf.Sin(2f * Mathf.PI * p.yawF * t + ph) + 0.25f * p.yawA * Mathf.Sin(2f * Mathf.PI * p.yawF * 3.1f * t);
                float pitch = p.pitch0 + p.pitchA * Mathf.Sin(2f * Mathf.PI * p.yawF * 1.6f * t + 1.3f * ph);
                Quaternion rot = Quaternion.Euler(pitch + N(r, 0.3f), yaw + N(r, 0.3f), 0f);
                Quaternion yawOnly = Quaternion.Euler(0f, yaw, 0f);

                Vector3 handMotionR = p.handA * new Vector3(Mathf.Sin(2f * Mathf.PI * p.handF * t + ph),
                    0.6f * Mathf.Sin(2f * Mathf.PI * p.handF * 1.3f * t + 0.5f * ph), 0.5f * Mathf.Cos(2f * Mathf.PI * p.handF * 0.7f * t));
                Vector3 handMotionL = 0.6f * p.handA * new Vector3(Mathf.Sin(2f * Mathf.PI * p.handF * 0.8f * t + 2f * ph),
                    0.5f * Mathf.Cos(2f * Mathf.PI * p.handF * 1.1f * t), 0.4f * Mathf.Sin(2f * Mathf.PI * p.handF * 0.6f * t + ph));

                list.Add(new KinematicSample
                {
                    t = t,
                    headPos = head,
                    headRot = rot,
                    rightPos = head + yawOnly * (new Vector3(p.shoulder, -p.drop, p.reach) + handMotionR) + Noise3(r, 0.002f),
                    leftPos = head + yawOnly * (new Vector3(-p.shoulder, -p.drop, p.reach * 0.9f) + handMotionL) + Noise3(r, 0.002f),
                    hasLeft = true,
                    hasRight = true,
                });
            }
            return list;
        }

        // ---------------- scoring helpers ----------------

        private static List<(float t, float score)> ScoreWindows(BiometricTemplate tpl, List<KinematicSample> s, BiometricConfig cfg)
        {
            var res = new List<(float, float)>();
            if (tpl == null || s.Count == 0) return res;
            int win = Mathf.RoundToInt(cfg.windowSeconds * Hz), hop = Mathf.RoundToInt(cfg.hopSeconds * Hz);
            for (int start = 0; start + win <= s.Count; start += hop)
            {
                var f = KinematicFeatures.Extract(s.GetRange(start, win));
                if (f != null) res.Add((s[start + win - 1].t, tpl.Score(f)));
            }
            return res;
        }

        private static (float eer, float thr) ComputeEer(List<float> gen, List<float> imp)
        {
            float best = float.MaxValue, eer = 1f, thr = 0f;
            for (int i = 0; i <= 1000; i++)
            {
                float th = i / 1000f;
                float far = imp.Count(x => x >= th) / (float)Math.Max(1, imp.Count);
                float frr = gen.Count(x => x < th) / (float)Math.Max(1, gen.Count);
                if (Math.Abs(far - frr) < best) { best = Math.Abs(far - frr); eer = (far + frr) / 2f; thr = th; }
            }
            return (eer, thr);
        }

        private static float U(System.Random r, float a, float b) => a + (float)r.NextDouble() * (b - a);
        private static float N(System.Random r, float sd)
        {
            double u1 = 1.0 - r.NextDouble(), u2 = r.NextDouble();
            return (float)(sd * Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2));
        }
        private static Vector3 Noise3(System.Random r, float sd) => new Vector3(N(r, sd), N(r, sd), N(r, sd));
        private static float Mean(IEnumerable<float> v) { var a = v.ToList(); return a.Count > 0 ? a.Average() : 0f; }
        private static float StdDev(List<float> v)
        {
            if (v.Count < 2) return 0f;
            float m = v.Average();
            return Mathf.Sqrt(v.Sum(x => (x - m) * (x - m)) / (v.Count - 1));
        }
        private static string F(float v) => v.ToString("F4", CultureInfo.InvariantCulture);
    }
}
