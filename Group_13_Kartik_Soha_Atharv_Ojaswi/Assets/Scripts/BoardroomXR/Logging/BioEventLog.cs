using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace BoardroomXR.Logging
{
    /// <summary>
    /// One structured, machine-greppable line per security-relevant event:
    ///
    ///   [BioEvt] t=12.34 type=lockout user=EMP-1001 detail=score 0.08 &lt; 0.15 for 6 windows
    ///
    /// Everything goes to logcat (so an expo session can be reconstructed from
    /// `adb logcat` alone) and into a small in-memory ring buffer that the SOC
    /// wall panel, the wrist display and the laptop dashboard all read from.
    ///
    /// Only events that actually happened are emitted; nothing here is seeded.
    /// </summary>
    public static class BioEventLog
    {
        public const int Capacity = 64;

        public struct Entry
        {
            public float t;              // seconds since app start
            public string clock;         // local wall clock, for the on-screen log
            public string type;
            public string user;
            public string detail;

            public override string ToString() =>
                string.IsNullOrEmpty(detail) ? $"{clock} {type}" : $"{clock} {type}: {detail}";
        }

        private static readonly List<Entry> _entries = new List<Entry>(Capacity);

        /// <summary>Raised on the main thread for every emitted event.</summary>
        public static event Action<Entry> OnEvent;

        /// <summary>Oldest-first snapshot of the ring buffer.</summary>
        public static IReadOnlyList<Entry> Entries => _entries;

        public static int TotalCount { get; private set; }

        public static void Emit(string type, string user, string detail = "")
        {
            var e = new Entry
            {
                t = Time.realtimeSinceStartup,
                clock = DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture),
                type = type ?? "?",
                user = string.IsNullOrEmpty(user) ? "-" : user,
                detail = detail ?? ""
            };

            _entries.Add(e);
            if (_entries.Count > Capacity) _entries.RemoveAt(0);
            TotalCount++;

            Debug.Log(string.Format(CultureInfo.InvariantCulture,
                "[BioEvt] t={0:F2} type={1} user={2} detail={3}", e.t, e.type, e.user, e.detail));
            // Mirror into the session log file, but only if one is open - LogGlobal's
            // console fallback would print the same line to logcat twice.
            if (SessionLogger.Active != null)
                SessionLogger.Active.Log($"[BioEvt] {e.type} user={e.user} {e.detail}");

            try { OnEvent?.Invoke(e); }
            catch (Exception ex) { Debug.LogWarning($"[BioEvt] listener threw: {ex.Message}"); }
        }

        /// <summary>Most recent entries, newest last, at most <paramref name="count"/> of them.</summary>
        public static List<Entry> Recent(int count)
        {
            var result = new List<Entry>(Mathf.Min(count, _entries.Count));
            for (int i = Mathf.Max(0, _entries.Count - count); i < _entries.Count; i++) result.Add(_entries[i]);
            return result;
        }

        /// <summary>Test / demo-reset helper.</summary>
        public static void Clear()
        {
            _entries.Clear();
            TotalCount = 0;
        }
    }
}
