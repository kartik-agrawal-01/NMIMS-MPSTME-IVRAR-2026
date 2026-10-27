using UnityEngine;
using BoardroomXR.Auth;
using BoardroomXR.Biometrics;

namespace BoardroomXR.UI
{
    /// <summary>
    /// All demo audio, generated in code with <see cref="AudioClip.Create"/> - there
    /// are no audio asset files in the project, so nothing needs importing and the
    /// APK does not grow:
    ///
    ///   - a two-tone alarm when the biometric layer locks someone out,
    ///   - a soft beep for each Warning window on the way there,
    ///   - a chime as each MFA factor passes,
    ///   - an "access granted" chord when the server marks the avatar verified.
    ///
    /// Everything is 2D (spatialBlend 0), because these are system sounds rather
    /// than things in the room.
    /// </summary>
    public class ProceduralAudioCues : MonoBehaviour
    {
        private const int SampleRate = 24000;

        private AudioSource _source;
        private AudioClip _alarm, _warning, _chime, _granted, _deny;

        private MFACoordinator _mfa;
        private ContinuousBiometricEngine _boundEngine;
        private MFAStage _lastStage = MFAStage.Idle;
        private float _nextSearch, _lastWarningBeep;

        private void Awake()
        {
            _source = gameObject.AddComponent<AudioSource>();
            _source.playOnAwake = false;
            _source.spatialBlend = 0f;
            _source.volume = 0.75f;

            _alarm = BuildAlarm();
            _warning = BuildBeep(880f, 0.10f, 0.35f);
            _chime = BuildBell(1318f, 0.45f);
            _granted = BuildChord(new[] { 523.25f, 659.25f, 783.99f }, 0.85f);
            _deny = BuildBeep(196f, 0.35f, 0.5f);
        }

        private void OnDestroy()
        {
            if (_boundEngine != null)
            {
                _boundEngine.OnWindowScored -= HandleWindow;
                _boundEngine.OnLockout -= HandleLockout;
            }
            if (_mfa != null) _mfa.OnStageChanged -= HandleStage;
            foreach (var c in new[] { _alarm, _warning, _chime, _granted, _deny })
                if (c != null) Destroy(c);
        }

        private void Update()
        {
            if (Time.unscaledTime < _nextSearch) return;
            _nextSearch = Time.unscaledTime + 1f;

            if (_mfa == null)
            {
                _mfa = FindAnyObjectByType<MFACoordinator>();
                if (_mfa != null) { _mfa.OnStageChanged += HandleStage; _lastStage = _mfa.CurrentStage; }
            }

            var engine = ContinuousBiometricAuthenticator.Instance?.Engine;
            if (engine != null && engine != _boundEngine)
            {
                if (_boundEngine != null)
                {
                    _boundEngine.OnWindowScored -= HandleWindow;
                    _boundEngine.OnLockout -= HandleLockout;
                }
                _boundEngine = engine;
                _boundEngine.OnWindowScored += HandleWindow;
                _boundEngine.OnLockout += HandleLockout;
            }
        }

        // ---------------- cues ----------------

        private void HandleStage(MFAStage stage)
        {
            // Each forward step through the factors is one factor passed.
            if ((_lastStage == MFAStage.Pin && stage == MFAStage.Gesture) ||
                (_lastStage == MFAStage.Gesture && stage == MFAStage.Otp))
                Play(_chime);
            else if (stage == MFAStage.Passed) Play(_granted);
            else if (stage == MFAStage.Failed || stage == MFAStage.TimedOut) Play(_deny);

            _lastStage = stage;
        }

        private void HandleWindow(float t, BiometricState state, float raw, float smoothed, float[] f)
        {
            if (state == BiometricState.Warning && Time.unscaledTime - _lastWarningBeep > 0.3f)
            {
                _lastWarningBeep = Time.unscaledTime;
                Play(_warning, 0.45f);
            }
        }

        private void HandleLockout(string reason) => Play(_alarm, 1f);

        private void Play(AudioClip clip, float volumeScale = 1f)
        {
            if (clip == null || _source == null) return;
            _source.PlayOneShot(clip, volumeScale);
        }

        // ---------------- synthesis ----------------

        /// <summary>Two alternating tones, like a building alarm.</summary>
        private static AudioClip BuildAlarm()
        {
            const float duration = 1.6f, segment = 0.2f;
            int n = (int)(SampleRate * duration);
            var data = new float[n];
            for (int i = 0; i < n; i++)
            {
                float t = i / (float)SampleRate;
                float freq = ((int)(t / segment) % 2 == 0) ? 740f : 554f;
                float env = Envelope(t % segment, segment, 0.01f, 0.03f);
                // A little third harmonic gives it an edge without being harsh.
                data[i] = env * 0.55f * (Mathf.Sin(2f * Mathf.PI * freq * t) +
                                         0.25f * Mathf.Sin(6f * Mathf.PI * freq * t));
            }
            return Make("BXR_Alarm", data);
        }

        private static AudioClip BuildBeep(float freq, float duration, float amplitude)
        {
            int n = (int)(SampleRate * duration);
            var data = new float[n];
            for (int i = 0; i < n; i++)
            {
                float t = i / (float)SampleRate;
                data[i] = amplitude * Envelope(t, duration, 0.008f, 0.05f) * Mathf.Sin(2f * Mathf.PI * freq * t);
            }
            return Make($"BXR_Beep_{freq:F0}", data);
        }

        /// <summary>A struck-bell shape: exponential decay plus a detuned partial.</summary>
        private static AudioClip BuildBell(float freq, float duration)
        {
            int n = (int)(SampleRate * duration);
            var data = new float[n];
            for (int i = 0; i < n; i++)
            {
                float t = i / (float)SampleRate;
                float decay = Mathf.Exp(-5f * t / duration);
                data[i] = 0.5f * decay * (Mathf.Sin(2f * Mathf.PI * freq * t) +
                                          0.35f * Mathf.Sin(2f * Mathf.PI * freq * 2.76f * t));
            }
            return Make("BXR_Chime", data);
        }

        private static AudioClip BuildChord(float[] freqs, float duration)
        {
            int n = (int)(SampleRate * duration);
            var data = new float[n];
            for (int i = 0; i < n; i++)
            {
                float t = i / (float)SampleRate;
                float decay = Mathf.Exp(-2.2f * t / duration);
                float v = 0f;
                // Notes arrive in sequence, so it reads as "granted" rather than a blip.
                for (int k = 0; k < freqs.Length; k++)
                {
                    float onset = k * 0.07f;
                    if (t < onset) continue;
                    v += Mathf.Sin(2f * Mathf.PI * freqs[k] * (t - onset));
                }
                data[i] = 0.34f * decay * v / freqs.Length * Envelope(t, duration, 0.01f, 0.25f);
            }
            return Make("BXR_Granted", data);
        }

        private static float Envelope(float t, float duration, float attack, float release)
        {
            if (t < attack) return Mathf.Clamp01(t / Mathf.Max(attack, 1e-4f));
            if (t > duration - release) return Mathf.Clamp01((duration - t) / Mathf.Max(release, 1e-4f));
            return 1f;
        }

        private static AudioClip Make(string name, float[] data)
        {
            var clip = AudioClip.Create(name, data.Length, 1, SampleRate, false);
            clip.SetData(data, 0);
            return clip;
        }
    }
}
