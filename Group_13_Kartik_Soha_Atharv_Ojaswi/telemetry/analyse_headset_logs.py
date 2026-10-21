"""
Analyse the continuous-biometric logs pulled from the Quest after a test session.

Usage (from PROJECT_FINAL):
    python Tools\\analyse_headset_logs.py Tools\\headset_logs

Expects the folder created by the pull commands in WEDNESDAY_TEST_GUIDE.md:
    <folder>/BiometricLogs/scores_*.csv   (one file per login, written by the headset)
    <folder>/MFALogs/session_*.log         (MFA + lockout event log)

Writes <folder>/headset_summary.txt and headset_sessions.csv.
"""
import csv
import glob
import os
import re
import sys


def read_scores(path):
    rows = []
    with open(path, newline="", encoding="utf-8-sig") as f:
        for r in csv.DictReader(f):
            try:
                rows.append({"t": float(r["t"]), "state": r["state"],
                             "raw": float(r["raw_score"]), "smoothed": float(r["smoothed_score"])})
            except (KeyError, ValueError):
                continue
    return rows


WIN = re.compile(r"\[BioWin\] user=(\S+) t=([\d.-]+) state=(\w+) raw=([\d.]+) smoothed=([\d.]+)")


def read_logcat_sessions(folder):
    """Fallback: rebuild per-login sessions from the [BioWin] lines in logcat captures."""
    sessions, current, last_t = [], [], None
    for log in sorted(glob.glob(os.path.join(folder, "logcat*.txt"))):
        with open(log, encoding="utf-8", errors="replace") as f:
            for line in f:
                m = WIN.search(line)
                if not m:
                    continue
                t = float(m.group(2))
                if last_t is not None and t < last_t and current:
                    sessions.append((f"logcat_session_{len(sessions) + 1:02d}", current))
                    current = []
                current.append({"t": t, "state": m.group(3), "raw": float(m.group(4)), "smoothed": float(m.group(5))})
                last_t = t
    if current:
        sessions.append((f"logcat_session_{len(sessions) + 1:02d}", current))
    return sessions


def main(folder):
    files = sorted(glob.glob(os.path.join(folder, "BiometricLogs", "scores_*.csv")))
    sessions = [(os.path.basename(p), read_scores(p)) for p in files]
    if not sessions:
        sessions = read_logcat_sessions(folder)
        if sessions:
            print("No CSV score files found; using the [BioWin] backup lines from logcat.")
    if not sessions:
        print(f"No score data in {folder} (no BiometricLogs CSVs and no [BioWin] lines in logcat*.txt).")
        return
    out_rows = []
    for p, rows in sessions:
        phase = "enrol" if any(r["state"] == "Enrolling" for r in rows) else "verify"
        enrol = [r for r in rows if r["state"] == "Enrolling"]
        scored = [r for r in rows if r["state"] != "Enrolling"]
        locked = [r for r in scored if r["state"] == "LockedOut"]
        first_warn = None
        if locked:
            # start of the warning streak that ended in the lockout
            idx = scored.index(locked[0])
            j = idx
            while j > 0 and scored[j - 1]["state"] == "Warning":
                j -= 1
            first_warn = scored[j]["t"]
        genuine = [r["raw"] for r in scored if first_warn is None or r["t"] < first_warn]
        out_rows.append({
            "file": p,
            "phase": phase,
            "enrol_windows": len(enrol),
            "scored_windows": len(scored),
            "mean_score_before_lockout": round(sum(genuine) / len(genuine), 3) if genuine else "",
            "locked_out": "Y" if locked else "N",
            "lockout_at_s": round(locked[0]["t"], 2) if locked else "",
            "warning_to_lockout_s": round(locked[0]["t"] - first_warn, 2) if locked else "",
            "min_smoothed": round(min(r["smoothed"] for r in scored), 3) if scored else "",
        })

    with open(os.path.join(folder, "headset_sessions.csv"), "w", newline="", encoding="utf-8") as f:
        w = csv.DictWriter(f, fieldnames=list(out_rows[0].keys()))
        w.writeheader()
        w.writerows(out_rows)

    events = []
    for log in sorted(glob.glob(os.path.join(folder, "MFALogs", "session_*.log"))):
        with open(log, encoding="utf-8", errors="replace") as f:
            for line in f:
                if re.search(r"VERIFIED|REVOKED|IMPOSTOR LOCKOUT|ENROLLED|TIMED OUT|Incorrect|did not match", line):
                    events.append(line.strip())

    lines = [f"Headset sessions analysed: {len(out_rows)} score files from {folder}"]
    for r in out_rows:
        lines.append(f"  {r['file']}: {r['phase']}, {r['scored_windows']} scored windows, "
                     f"mean score {r['mean_score_before_lockout']}, locked out {r['locked_out']}"
                     + (f" at {r['lockout_at_s']} s ({r['warning_to_lockout_s']} s after first warning)" if r['locked_out'] == 'Y' else ""))
    lines.append("")
    lines.append(f"Key events from MFA logs ({len(events)}):")
    lines += ["  " + e for e in events]
    text = "\n".join(lines)
    with open(os.path.join(folder, "headset_summary.txt"), "w", encoding="utf-8") as f:
        f.write(text)
    print(text)


if __name__ == "__main__":
    main(sys.argv[1] if len(sys.argv) > 1 else os.path.join("Tools", "headset_logs"))
