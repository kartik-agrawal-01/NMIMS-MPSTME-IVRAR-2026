using System.Collections.Generic;

namespace BoardroomXR.Biometrics
{
    /// <summary>
    /// Shared ring buffer of scored windows. The SOC wall panel (last 90), the wrist
    /// display (last 30) and the laptop dashboard (last 120) all read from here, so
    /// every surface shows the same numbers the engine actually produced instead of
    /// each keeping its own copy.
    /// </summary>
    public static class BiometricHistory
    {
        public const int Capacity = 240;

        public struct Sample
        {
            public float t;
            public BiometricState state;
            public float raw;
            public float smoothed;
        }

        private static readonly List<Sample> _samples = new List<Sample>(Capacity);

        public static IReadOnlyList<Sample> Samples => _samples;
        public static int Count => _samples.Count;

        public static void Record(float t, BiometricState state, float raw, float smoothed)
        {
            _samples.Add(new Sample { t = t, state = state, raw = raw, smoothed = smoothed });
            if (_samples.Count > Capacity) _samples.RemoveRange(0, _samples.Count - Capacity);
        }

        /// <summary>Smoothed scores, oldest first, at most <paramref name="count"/> of them.</summary>
        public static List<float> RecentSmoothed(int count)
        {
            var result = new List<float>(count);
            for (int i = System.Math.Max(0, _samples.Count - count); i < _samples.Count; i++)
                result.Add(_samples[i].smoothed);
            return result;
        }

        /// <summary>Recent samples, oldest first.</summary>
        public static List<Sample> Recent(int count)
        {
            var result = new List<Sample>(count);
            for (int i = System.Math.Max(0, _samples.Count - count); i < _samples.Count; i++)
                result.Add(_samples[i]);
            return result;
        }

        public static void Clear() => _samples.Clear();
    }
}
