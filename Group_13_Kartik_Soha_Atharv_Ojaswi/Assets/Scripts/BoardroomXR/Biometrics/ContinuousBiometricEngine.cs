using System;
using System.Collections.Generic;

namespace BoardroomXR.Biometrics
{
    public enum BiometricState { Idle, Enrolling, Verified, Warning, LockedOut }

    [Serializable]
    public class BiometricConfig
    {
        public float windowSeconds = 3.0f;      // sliding window length
        public float hopSeconds = 0.5f;         // how often a window is scored
        public float enrolSeconds = 60f;        // enrolment duration (study protocol: 180)
        public float matchThreshold = 0.25f;    // smoothed score below this = violation
        public float smoothing = 0.35f;         // EMA weight of the newest window score
        public int violationsForLockout = 4;    // consecutive violations -> lockout
        public float graceSeconds = 2f;         // ignore violations right after verification starts
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
            SetState(existing == null ? BiometricState.Enrolling : BiometricState.Verified);
        }

        public void Stop()
        {
            _buffer.Clear();
            if (State != BiometricState.LockedOut) SetState(BiometricState.Idle);
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
            RawScore = Template.Score(f);
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
            }
            OnWindowScored?.Invoke(t, State, RawScore, SmoothedScore, f);
        }

        private void SetState(BiometricState s)
        {
            if (State == s) return;
            State = s;
            OnStateChanged?.Invoke(s);
        }
    }
}
