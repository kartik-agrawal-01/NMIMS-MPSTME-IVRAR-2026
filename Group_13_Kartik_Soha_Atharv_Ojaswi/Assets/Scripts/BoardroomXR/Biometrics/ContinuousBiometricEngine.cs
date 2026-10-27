using System;
using System.Collections.Generic;

namespace BoardroomXR.Biometrics
{
    public enum BiometricState { Idle, Enrolling, Verified, Warning, LockedOut }

    /// <summary>
    /// Named operating points on the FAR/FRR trade-off curve, switchable live so a
    /// demo can be moved along the curve in front of an audience.
    ///
    /// The numbers come from the scripted simulation study in
    /// <c>BiometricEvaluationRunner</c> (15 runs, seeded motion profiles):
    ///   Strict   - fast detection, but false lockouts during genuine use.
    ///   Balanced - the default; detection in a few seconds, few false lockouts.
    ///   Relaxed  - no false lockouts over the 40 simulated genuine sessions and
    ///              95% impostor detection in about 6 s.
    /// </summary>
    public enum BiometricPreset { Strict, Balanced, Relaxed }

    [Serializable]
    public class BiometricConfig
    {
        public float windowSeconds = 3.0f;      // sliding window length
        public float hopSeconds = 0.5f;         // how often a window is scored
        public float enrolSeconds = 60f;        // enrolment duration (30 s quick preset for the expo)
        public float matchThreshold = 0.15f;    // smoothed score below this = violation (Balanced)
        public float smoothing = 0.35f;         // EMA weight of the newest window score
        public int violationsForLockout = 6;    // consecutive violations -> lockout (Balanced)
        public float graceSeconds = 2f;         // ignore violations right after verification starts

        /// <summary>Which named operating point the thresholds above currently represent.</summary>
        public BiometricPreset preset = BiometricPreset.Balanced;

        /// <summary>
        /// Slow-drift handling: fold trusted windows back into the template. Off means
        /// the template is frozen at enrolment. Never applies in Warning or LockedOut,
        /// so an impostor cannot train the template into accepting them.
        /// </summary>
        public bool adaptiveTemplate = true;

        /// <summary>EMA weight used when a trusted window is folded into the template.</summary>
        public float adaptationAlpha = 0.01f;

        public void ApplyPreset(BiometricPreset p)
        {
            preset = p;
            switch (p)
            {
                case BiometricPreset.Strict:
                    matchThreshold = 0.25f; violationsForLockout = 4; break;
                case BiometricPreset.Relaxed:
                    matchThreshold = 0.10f; violationsForLockout = 8; break;
                default:
                    matchThreshold = 0.15f; violationsForLockout = 6; break;
            }
        }

        /// <summary>"Balanced (0.15, k=6)" - for HUD, SOC panel and log lines.</summary>
        public string PresetLabel =>
            $"{preset} ({matchThreshold.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)}, k={violationsForLockout})";

        public BiometricConfig Clone() => (BiometricConfig)MemberwiseClone();
    }

    /// <summary>
    /// Scene-independent core of continuous behavioural authentication:
    /// buffers samples into a sliding window, enrols a template, then scores
    /// every window against it and decides Verified / Warning / LockedOut.
    /// The MonoBehaviour wrapper feeds it live poses; the automated test runs
    /// feed it recorded or scripted poses through exactly the same code.
    /// </summary>
    public class ContinuousBiometricEngine
    {
        public BiometricConfig Config { get; }
        public BiometricState State { get; private set; } = BiometricState.Idle;
        public BiometricTemplate Template { get; private set; }
        public string UserId { get; private set; }

        public float RawScore { get; private set; }
        public float SmoothedScore { get; private set; } = 1f;
        public int Violations { get; private set; }
        public float EnrolProgress => State == BiometricState.Enrolling && Config.enrolSeconds > 0
            ? Math.Min(1f, (_lastT - _phaseStart) / Config.enrolSeconds) : 0f;
        public float SecondsInPhase => _lastT - _phaseStart;

        /// <summary>Per-feature z-score of the most recently scored window (explainability).</summary>
        public float[] LastZ { get; } = new float[KinematicFeatures.Count];

        /// <summary>Raw feature vector of the most recently scored window.</summary>
        public float[] LastFeatures { get; } = new float[KinematicFeatures.Count];

        /// <summary>True once at least one window has been scored since <see cref="Begin"/>.</summary>
        public bool HasScoredWindow { get; private set; }

        /// <summary>Windows folded into the template during this session (adaptive mode).</summary>
        public int AdaptationsThisSession { get; private set; }

        /// <summary>True when the template has unsaved adaptations.</summary>
        public bool TemplateDirty { get; private set; }

        /// <summary>Index of the feature furthest from the template in the last window, or -1.</summary>
        public int TopDeviatingFeature
        {
            get
            {
                if (!HasScoredWindow) return -1;
                int best = 0;
                for (int i = 1; i < KinematicFeatures.Count; i++)
                    if (Math.Abs(LastZ[i]) > Math.Abs(LastZ[best])) best = i;
                return best;
            }
        }

        /// <summary>(t, state, raw, smoothed, features) for every scored window.</summary>
        public event Action<float, BiometricState, float, float, float[]> OnWindowScored;
        public event Action<BiometricTemplate> OnEnrolled;
        public event Action<string> OnLockout;
        public event Action<BiometricState> OnStateChanged;

        private readonly List<KinematicSample> _buffer = new();
        private readonly List<float[]> _enrolWindows = new();
        private float _phaseStart, _lastEval = float.NegativeInfinity, _lastT;
        private bool _smoothedInit;

        public ContinuousBiometricEngine(BiometricConfig config) { Config = config ?? new BiometricConfig(); }

        /// <summary>Start monitoring a freshly verified user. Null template = enrol first.</summary>
        public void Begin(string userId, BiometricTemplate existing, float now)
        {
            UserId = userId;
            Template = existing;
            _buffer.Clear();
            _enrolWindows.Clear();
            _phaseStart = now;
            _lastT = now;
            _lastEval = float.NegativeInfinity;
            Violations = 0;
            _smoothedInit = false;
            SmoothedScore = 1f;
            RawScore = 0f;
            HasScoredWindow = false;
            AdaptationsThisSession = 0;
            TemplateDirty = false;
            Array.Clear(LastZ, 0, LastZ.Length);
            Array.Clear(LastFeatures, 0, LastFeatures.Length);
            SetState(existing == null ? BiometricState.Enrolling : BiometricState.Verified);
        }

        public void Stop()
        {
            _buffer.Clear();
            if (State != BiometricState.LockedOut) SetState(BiometricState.Idle);
        }

        /// <summary>Called by the owner once adapted means have been written to disk.</summary>
        public void MarkTemplateSaved() => TemplateDirty = false;

        /// <summary>
        /// DEMO AFFORDANCE. Drives the engine straight to LockedOut so a presenter can
        /// show the consequences (doors, alarm, re-auth) without waiting for a real
        /// mismatch. The reason string says so, and it is logged as such, so a forced
        /// lockout can never be mistaken for a measurement.
        /// </summary>
        public void ForceLockout(string why)
        {
            if (State == BiometricState.Idle || State == BiometricState.LockedOut) return;
            SetState(BiometricState.LockedOut);
            OnLockout?.Invoke($"FORCED (demo, not a detection): {why}");
        }

        public void Push(KinematicSample s)
        {
            if (State == BiometricState.Idle || State == BiometricState.LockedOut) return;
            _lastT = s.t;
            _buffer.Add(s);

            float cutoff = s.t - Config.windowSeconds;
            int drop = 0;
            while (drop < _buffer.Count && _buffer[drop].t < cutoff) drop++;
            if (drop > 0) _buffer.RemoveRange(0, drop);

            bool windowFull = _buffer.Count > 1 && (_buffer[_buffer.Count - 1].t - _buffer[0].t) >= Config.windowSeconds * 0.95f;
            if (!windowFull || s.t - _lastEval < Config.hopSeconds) return;
            _lastEval = s.t;

            float[] f = KinematicFeatures.Extract(_buffer);
            if (f == null) return;

            if (State == BiometricState.Enrolling) { EnrolStep(f, s.t); return; }
            ScoreStep(f, s.t);
        }

        private void EnrolStep(float[] f, float t)
        {
            _enrolWindows.Add(f);
            Array.Copy(f, LastFeatures, f.Length);
            OnWindowScored?.Invoke(t, State, 0f, 0f, f);
            if (t - _phaseStart < Config.enrolSeconds) return;

            Template = BiometricTemplate.Build(UserId, _enrolWindows);
            if (Template == null) return; // not enough windows yet; keep enrolling
            OnEnrolled?.Invoke(Template);
            _phaseStart = t;
            SetState(BiometricState.Verified);
        }

        private void ScoreStep(float[] f, float t)
        {
            RawScore = Template.Score(f, LastZ);
            Array.Copy(f, LastFeatures, f.Length);
            HasScoredWindow = true;

            if (!_smoothedInit) { SmoothedScore = RawScore; _smoothedInit = true; }
            else SmoothedScore = Config.smoothing * RawScore + (1f - Config.smoothing) * SmoothedScore;

            bool inGrace = t - _phaseStart < Config.graceSeconds;
            if (SmoothedScore < Config.matchThreshold && !inGrace)
            {
                Violations++;
                if (Violations >= Config.violationsForLockout)
                {
                    SetState(BiometricState.LockedOut);
                    OnWindowScored?.Invoke(t, State, RawScore, SmoothedScore, f);
                    OnLockout?.Invoke($"continuous biometric mismatch: score {SmoothedScore:F2} < {Config.matchThreshold:F2} " +
                                      $"for {Violations} consecutive windows");
                    return;
                }
                SetState(BiometricState.Warning);
            }
            else
            {
                Violations = 0;
                SetState(BiometricState.Verified);
                MaybeAdapt(f);
            }
            OnWindowScored?.Invoke(t, State, RawScore, SmoothedScore, f);
        }

        /// <summary>
        /// Folds a trusted window into the template. Deliberately reachable only from
        /// the Verified branch above: in Warning or LockedOut the template is frozen,
        /// so motion that is already failing can never be learned.
        /// </summary>
        private void MaybeAdapt(float[] f)
        {
            if (!Config.adaptiveTemplate) return;
            if (State != BiometricState.Verified || Template == null) return;
            if (RawScore < BiometricTemplate.AdaptMinRawScore) return;

            if (Template.Adapt(f, LastZ, Config.adaptationAlpha) <= 0) return;
            AdaptationsThisSession++;
            TemplateDirty = true;
        }

        private void SetState(BiometricState s)
        {
            if (State == s) return;
            State = s;
            OnStateChanged?.Invoke(s);
        }
    }
}
