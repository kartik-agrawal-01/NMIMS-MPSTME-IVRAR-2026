#!/usr/bin/env python3
"""
generate_synthetic_participants.py
==================================

SYNTHETIC DATA GENERATOR for the BoardroomXR project report.

*** THIS SCRIPT DOES NOT CONTAIN, PROCESS OR PRODUCE REAL HUMAN-SUBJECT DATA. ***

No participants were recruited or recorded. Every row it writes is simulated from
parametric distributions, with a fixed seed so the whole dataset is reproducible.
Use is limited to a report submission for which simulated data has been approved;
every CSV carries a "# SYNTHETIC DATA" first line and every row a
data_source = "synthetic" column.

One row per participant
-----------------------
The dataset is a single flat table: 15 participants, one row each, one
observation per measure. There is no per-attempt table - a participant makes one
PIN entry sequence, one genuine gesture re-draw, one wrong-gesture attempt, one
live OTP entry and one stale-OTP attempt, so every column holds a single number
for that person rather than a mean over repeats.

What IS real
------------
The *parameters* below (thresholds, timeouts, strike policy, score bands) were
read out of the shipped BoardroomXR implementation and from single-headset
(Quest) bench testing recorded in DEMO_RUNBOOK.md. The *observations* generated
around them are not real. Sources are cited per constant.

Real measured on-device data is kept separately, in
Tools/real_device_data/device_test_events.csv (data_source = "real_device"),
produced by Tools/extract_device_events.py. The two must never be combined.

Outputs (default: Tools/synthetic_data/)
----------------------------------------
    synthetic_participants.csv   one row per simulated participant (15 rows)
    synthetic_summary.csv        mean / SD / min / max per measure
    chart_gesture_scores.png     genuine vs wrong-gesture score + 0.75 threshold
    chart_time_per_factor.png    time per factor per participant
    chart_false_revokes.png      SpoofDetector false revokes per participant
    METHODOLOGY.md               paragraph to paste into the report

Reading the CSV back:
    pd.read_csv(path, comment="#")

Requires: numpy, pandas (data) + matplotlib (the three PNG charts only).
Usage:    python Tools/generate_synthetic_participants.py [--seed N] [--outdir DIR]
"""

from __future__ import annotations

import argparse
import textwrap
from pathlib import Path

import numpy as np
import pandas as pd

# --------------------------------------------------------------------------- #
# Reproducibility
# --------------------------------------------------------------------------- #

DEFAULT_SEED = 20251013          # fixed: same seed in, byte-identical data out
N_PARTICIPANTS = 15              # P01 - P15

# --------------------------------------------------------------------------- #
# REAL parameters of the implementation under test.
# These are not invented; each is the value the build actually runs with.
# --------------------------------------------------------------------------- #

# -- Factor 1, PIN -- Assets/Scripts/Scripts/Auth/MFASettings.cs
PIN_LENGTH = 4                   # MFASettings.pinLength
MAX_PIN_ATTEMPTS = 3             # MFASettings.maxPinAttempts (then session locks)

# -- Factor 2, gesture signature -- MFASettings.cs + DEMO_RUNBOOK.md
GESTURE_THRESHOLD = 0.75         # MFASettings.gestureMatchThreshold, lowered from
                                 # 0.85 during bench testing because honest
                                 # re-draws were being rejected (DEMO_RUNBOOK.md)
GESTURE_ENROLMENT_SAMPLES = 3    # strokes captured to build a template

# Score bands measured on-device (Quest, GestureAuthenticator normalised
# point-distance score, 64 resample points). Genuine re-draws of an enrolled
# stroke landed ~0.60-0.85; a different stroke landed ~0.25-0.40.
GENUINE_SCORE_LO, GENUINE_SCORE_HI = 0.60, 0.85
WRONG_SCORE_LO, WRONG_SCORE_HI = 0.25, 0.40

# -- Factor 3, rotating OTP -- MFASettings.cs
OTP_VALIDITY_SECONDS = 30        # MFASettings.otpValiditySeconds
OTP_DIGITS = 6                   # MFASettings.otpDigits

# -- Session -- MFASettings.cs
SESSION_TIMEOUT_SECONDS = 90.0   # MFASettings.sessionTimeoutSeconds

# -- Anti-spoofing -- MFASettings.cs + Assets/Scripts/Scripts/Identity/SpoofDetector.cs
ANOMALY_SD_THRESHOLD = 2.5       # MFASettings.behaviouralAnomalyStdDevThreshold
STRIKES_BEFORE_REVOKE = 3        # SpoofDetector.strikesBeforeRevoke
STRIKE_REFRACTORY_SECONDS = 0.4  # SpoofDetector.strikeRefractorySeconds
STRIKE_DECAY_SECONDS = 12.0      # SpoofDetector.strikeDecaySeconds
MIN_SPEED_FOR_ANOMALY = 0.35     # SpoofDetector.minSpeedForAnomaly (m/s)

# -- Test protocol lengths (as run on the bench) --
ERRATIC_TEST_SECONDS = 20.0      # deliberate erratic-movement test
SITTING_TEST_SECONDS = 180.0     # 3 minutes of normal seated movement

# --------------------------------------------------------------------------- #
# Simulation parameters. THESE ARE ASSUMPTIONS, not measurements.
# Times are lognormal (positive, right-skewed, as human entry times are);
# scores are clipped normals; counts are Poisson / Bernoulli.
# --------------------------------------------------------------------------- #

PIN_ENTRY_MEDIAN_S = 5.8         # median per-attempt 4-digit keypad entry
PIN_ENTRY_SIGMA = 0.28
PIN_REPROMPT_GAP_S = 0.8         # UI gap after a rejected PIN

GESTURE_DRAW_MEDIAN_S = 3.4      # median single stroke draw
GESTURE_DRAW_SIGMA = 0.22

OTP_ENTRY_MEDIAN_S = 8.4         # read 6 digits off the token, then type them
OTP_ENTRY_SIGMA = 0.26

GENUINE_MU_MEAN, GENUINE_MU_SD = 0.755, 0.035    # per-participant genuine centre
WRONG_MU_MEAN, WRONG_MU_SD = 0.325, 0.025        # per-participant wrong-stroke centre
WRONG_WITHIN_SD = 0.035

SUS_MEAN, SUS_SD = 72.0, 11.0    # System Usability Scale, 0-100

# Movement style -> SpoofDetector behaviour. Styles are a simulated trait.
MOVEMENT_STYLES = ("calm", "moderate", "fidgety")
MOVEMENT_STYLE_P = (0.40, 0.40, 0.20)
ERRATIC_TRIGGER_P = {"calm": 0.92, "moderate": 0.95, "fidgety": 0.98}
FALSE_REVOKE_LAMBDA = {"calm": 0.05, "moderate": 0.10, "fidgety": 0.22}

# --------------------------------------------------------------------------- #
# Chart palette. Validated for colour-vision-deficiency separation and for
# >= 3:1 contrast against the chart surface before use.
# --------------------------------------------------------------------------- #

C_SURFACE = "#fcfcfb"
C_INK = "#0b0b0b"
C_INK_SOFT = "#52514e"
C_GRID = "#e6e5e1"
C_AXIS = "#c9c8c3"
C_SERIES_1 = "#2a78d6"   # blue   - genuine / PIN
C_SERIES_2 = "#eb6834"   # orange - wrong gesture / gesture
C_SERIES_3 = "#4a3aa7"   # violet - OTP

SYNTHETIC_TAG = "synthetic"

YES, NO = "Y", "N"


# --------------------------------------------------------------------------- #
# Distribution helpers
# --------------------------------------------------------------------------- #

def lognormal_time(rng, median, sigma, lo, hi, size=None):
    """A positive, right-skewed duration in seconds, clipped to a sane range."""
    return np.clip(rng.lognormal(np.log(median), sigma, size), lo, hi)


def clipped_normal(rng, mean, sd, lo, hi, size=None):
    """A normal draw held inside a physically meaningful interval."""
    return np.clip(rng.normal(mean, sd, size), lo, hi)


def r2(x):
    return float(np.round(x, 2))


def r3(x):
    return float(np.round(x, 3))


def yn(flag):
    return YES if flag else NO


# --------------------------------------------------------------------------- #
# Per-participant latent traits
# --------------------------------------------------------------------------- #

def make_traits(rng, pid):
    """
    Give each simulated person a stable set of traits, so their PIN, gesture,
    OTP and spoof numbers correlate the way one person's would: a slow, less
    accurate participant is slow and less accurate across every factor. The
    traits themselves are not reported - they only shape the observations.
    """
    # >1 = slower than the cohort median, <1 = faster. Multiplies every duration.
    speed_factor = float(np.clip(rng.lognormal(0.0, 0.22), 0.60, 1.90))
    # Chance of an error-free entry on a given factor.
    accuracy = float(np.clip(rng.normal(0.80, 0.10), 0.45, 0.98))
    # How repeatably they re-draw their own stroke: tightens the score spread.
    gesture_consistency = float(np.clip(rng.normal(0.75, 0.12), 0.40, 0.98))
    movement_style = str(rng.choice(MOVEMENT_STYLES, p=MOVEMENT_STYLE_P))

    # Genuine-score centre: consistent re-drawers sit a little higher and much
    # tighter. Held inside the band observed on-device.
    genuine_mu = float(np.clip(
        rng.normal(GENUINE_MU_MEAN, GENUINE_MU_SD) + 0.10 * (gesture_consistency - 0.75),
        GENUINE_SCORE_LO + 0.04, GENUINE_SCORE_HI - 0.01))
    genuine_sd = float(np.clip(0.055 - 0.035 * (gesture_consistency - 0.40) / 0.58,
                               0.018, 0.060))
    wrong_mu = float(np.clip(rng.normal(WRONG_MU_MEAN, WRONG_MU_SD),
                             WRONG_SCORE_LO + 0.01, WRONG_SCORE_HI - 0.01))

    return {
        "participant": pid,
        "_speed": speed_factor,
        "_accuracy": accuracy,
        "_consistency": gesture_consistency,
        "_style": movement_style,
        "_genuine_mu": genuine_mu,
        "_genuine_sd": genuine_sd,
        "_wrong_mu": wrong_mu,
    }


# --------------------------------------------------------------------------- #
# Factor 1 - PIN
# --------------------------------------------------------------------------- #

def simulate_pin(rng, t):
    """
    One 4-digit keypad entry sequence, up to MAX_PIN_ATTEMPTS before the session
    locks. Reported as the attempts the participant needed and the total time
    those attempts took, including the UI re-prompt gap after a rejection.
    """
    p_reject = float(np.clip((1.0 - t["_accuracy"]) * 0.55, 0.02, 0.30))

    total_time = 0.0
    attempts = 0
    success = False

    while attempts < MAX_PIN_ATTEMPTS and not success:
        attempts += 1
        dur = float(lognormal_time(rng, PIN_ENTRY_MEDIAN_S * t["_speed"],
                                   PIN_ENTRY_SIGMA, 2.2, 25.0))
        if rng.random() < p_reject:
            total_time += dur + PIN_REPROMPT_GAP_S
        else:
            success = True
            total_time += dur

    return {
        "pin_time_seconds": r2(total_time),
        "pin_attempts": attempts,
        "_pin_success": success,
    }


# --------------------------------------------------------------------------- #
# Factor 2 - gesture signature
# --------------------------------------------------------------------------- #

def simulate_gesture(rng, t):
    """
    Three enrolment strokes build the template (reported as one enrolment score,
    their mean self-match); then one genuine re-draw and one deliberately
    different stroke are scored against it at 0.75.
    """
    # Enrolment: each stroke scored against the other two. Slightly tighter and
    # higher than a later re-draw - enrolment happens back-to-back.
    enrol = np.clip(rng.normal(t["_genuine_mu"] + 0.045, t["_genuine_sd"] * 0.8,
                               GESTURE_ENROLMENT_SAMPLES),
                    GENUINE_SCORE_LO, 0.92)

    # The genuine re-draw: the one that decides the factor.
    score = float(np.clip(rng.normal(t["_genuine_mu"], t["_genuine_sd"]),
                          GENUINE_SCORE_LO, GENUINE_SCORE_HI))
    draw_time = float(lognormal_time(rng, GESTURE_DRAW_MEDIAN_S * t["_speed"],
                                     GESTURE_DRAW_SIGMA, 1.2, 14.0))
    passed = bool(score >= GESTURE_THRESHOLD)

    # A different stroke, scored against the same template. Drawn from a band
    # that cannot reach the accept threshold - this is a control on the
    # implementation, not a measurement of a person.
    wrong = float(np.clip(rng.normal(t["_wrong_mu"], WRONG_WITHIN_SD),
                          WRONG_SCORE_LO, WRONG_SCORE_HI))

    return {
        "gesture_enrolment_score": r3(enrol.mean()),
        "gesture_score": r3(score),
        "gesture_result": "pass" if passed else "fail",
        "wrong_gesture_score": r3(wrong),
        "wrong_gesture_rejected": yn(wrong < GESTURE_THRESHOLD),
        "_gesture_time": draw_time,
        "_gesture_passed": passed,
    }


# --------------------------------------------------------------------------- #
# Factor 3 - rotating OTP
# --------------------------------------------------------------------------- #

def simulate_otp(rng, t):
    """
    One live code entry, plus one deliberate stale-code attempt. The live entry
    fails only if it over-runs the 30s validity window - a mistyped digit is
    simply retyped inside the window, so it costs time, not the factor. The
    stale attempt re-submits a code older than otpValiditySeconds, so it must be
    rejected; that is the control, not a measurement.
    """
    dur = float(lognormal_time(rng, OTP_ENTRY_MEDIAN_S * t["_speed"],
                               OTP_ENTRY_SIGMA, 3.5, 30.0))
    expired = dur >= OTP_VALIDITY_SECONDS

    stale_age = float(OTP_VALIDITY_SECONDS
                      + np.clip(rng.lognormal(np.log(9.0), 0.5), 2.0, 60.0))

    return {
        "otp_time_seconds": r2(dur),
        "stale_otp_rejected": yn(stale_age > OTP_VALIDITY_SECONDS),
        "_otp_success": not expired,
        "_stale_age": stale_age,
    }


# --------------------------------------------------------------------------- #
# SpoofDetector
# --------------------------------------------------------------------------- #

def simulate_spoof(rng, t):
    """
    Two post-authentication tests:
      1. 20s of deliberately erratic head movement - should revoke.
      2. 3 minutes of normal seated movement - should not revoke.
    """
    style = t["_style"]
    revoked = bool(rng.random() < ERRATIC_TRIGGER_P[style])
    # Most participants never strike three times inside the decay window, so
    # false revokes are mostly 0.
    false_revokes = int(min(rng.poisson(FALSE_REVOKE_LAMBDA[style]), 2))

    return {
        "revoked_in_erratic_test": yn(revoked),
        "false_revokes_seated": false_revokes,
    }


# --------------------------------------------------------------------------- #
# Assembly
# --------------------------------------------------------------------------- #

COLUMNS = [
    "participant",
    "pin_time_seconds",
    "pin_attempts",
    "gesture_enrolment_score",
    "gesture_score",
    "gesture_result",
    "wrong_gesture_score",
    "wrong_gesture_rejected",
    "otp_time_seconds",
    "stale_otp_rejected",
    "total_unlock_time_seconds",
    "revoked_in_erratic_test",
    "false_revokes_seated",
    "sus_score",
    "data_source",
]


def generate(seed):
    """
    Return (participants, factor_times): the 15-row reported table, and the
    per-factor seconds behind total_unlock_time_seconds, which the time chart
    decomposes. Only the first is written to CSV.
    """
    rng = np.random.default_rng(seed)
    rows, factor_times = [], []

    for n in range(1, N_PARTICIPANTS + 1):
        t = make_traits(rng, f"P{n:02d}")
        rec = {"participant": t["participant"]}

        rec.update(simulate_pin(rng, t))
        rec.update(simulate_gesture(rng, t))
        rec.update(simulate_otp(rng, t))

        # Total time to unlock is the three factor attempts summed; UI
        # transitions between the panels are excluded. It is recorded only for
        # participants who actually cleared all three factors inside the
        # session timeout - a blank means they did not get in.
        total = (rec["pin_time_seconds"] + rec["_gesture_time"]
                 + rec["otp_time_seconds"])
        unlocked = bool(rec["_pin_success"] and rec["_gesture_passed"]
                        and rec["_otp_success"]
                        and total <= SESSION_TIMEOUT_SECONDS)
        rec["total_unlock_time_seconds"] = r2(total) if unlocked else np.nan

        rec.update(simulate_spoof(rng, t))

        # Optional usability measure, rounded to the 2.5 granularity of a
        # 10-item SUS.
        rec["sus_score"] = r2(np.round(
            clipped_normal(rng, SUS_MEAN, SUS_SD, 25.0, 100.0) / 2.5) * 2.5)

        rec["data_source"] = SYNTHETIC_TAG

        factor_times.append({
            "participant": t["participant"],
            "pin_seconds": rec["pin_time_seconds"],
            "gesture_seconds": r2(rec["_gesture_time"]),
            "otp_seconds": rec["otp_time_seconds"],
            "unlocked": unlocked,
        })
        rows.append({k: v for k, v in rec.items() if not k.startswith("_")})

    participants = pd.DataFrame(rows)[COLUMNS]
    return participants, pd.DataFrame(factor_times)


SUMMARY_MEASURES = [
    ("pin_time_seconds", "PIN time (s)"),
    ("pin_attempts", "PIN attempts"),
    ("gesture_enrolment_score", "Gesture enrolment score"),
    ("gesture_score", "Gesture score"),
    ("wrong_gesture_score", "Wrong-gesture score"),
    ("otp_time_seconds", "OTP time (s)"),
    ("total_unlock_time_seconds", "Total unlock time (s)"),
    ("false_revokes_seated", "False revokes while seated"),
    ("sus_score", "SUS score"),
]


def summarise(participants):
    """mean / SD / min / max per measure, plus the headline rates."""
    rows = []
    for col, label in SUMMARY_MEASURES:
        s = participants[col].astype(float).dropna()
        rows.append({
            "measure": label,
            "column": col,
            "n": int(s.size),
            "mean": r2(s.mean()),
            "sd": r2(s.std(ddof=1)),
            "min": r2(s.min()),
            "max": r2(s.max()),
            "data_source": SYNTHETIC_TAG,
        })

    n = len(participants)
    rates = [
        ("Gesture genuine accept rate (%)", "rate_genuine_accept",
         100.0 * (participants.gesture_result == "pass").mean(), n),
        ("Gesture false reject rate, FRR (%)", "rate_frr",
         100.0 * (participants.gesture_result == "fail").mean(), n),
        ("Gesture false accept rate, FAR (%)", "rate_far",
         100.0 * (participants.wrong_gesture_rejected == NO).mean(), n),
        ("Wrong-gesture strokes rejected (%)", "rate_wrong_rejected",
         100.0 * (participants.wrong_gesture_rejected == YES).mean(), n),
        ("Stale OTP codes rejected (%)", "rate_stale_rejected",
         100.0 * (participants.stale_otp_rejected == YES).mean(), n),
        ("Participants completing all 3 factors (%)", "rate_unlocked",
         100.0 * participants.total_unlock_time_seconds.notna().mean(), n),
        ("Erratic test triggering a revoke (%)", "rate_erratic_revoke",
         100.0 * (participants.revoked_in_erratic_test == YES).mean(), n),
        ("Participants with 0 false revokes (%)", "rate_zero_false_revokes",
         100.0 * (participants.false_revokes_seated == 0).mean(), n),
    ]
    for label, col, value, count in rates:
        rows.append({"measure": label, "column": col, "n": int(count),
                     "mean": r2(value), "sd": np.nan, "min": np.nan, "max": np.nan,
                     "data_source": SYNTHETIC_TAG})

    return pd.DataFrame(rows)


# --------------------------------------------------------------------------- #
# Output
# --------------------------------------------------------------------------- #

def header_comment(seed):
    return (f"# SYNTHETIC DATA - generated by generate_synthetic_participants.py, "
            f"seed={seed}. Not real participants.")


def write_csv(df, path, seed):
    """CSV with the synthetic-data banner as its first line."""
    with open(path, "w", newline="", encoding="utf-8") as fh:
        fh.write(header_comment(seed) + "\n")
        df.to_csv(fh, index=False, lineterminator="\n")
    return path


# --------------------------------------------------------------------------- #
# Charts
# --------------------------------------------------------------------------- #

def style_axes(ax, x_grid=False):
    ax.set_facecolor(C_SURFACE)
    ax.set_axisbelow(True)
    ax.grid(axis="x" if x_grid else "y", color=C_GRID, linewidth=0.8)
    for side in ("top", "right"):
        ax.spines[side].set_visible(False)
    for side in ("left", "bottom"):
        ax.spines[side].set_color(C_AXIS)
        ax.spines[side].set_linewidth(0.8)
    ax.tick_params(colors=C_INK_SOFT, labelsize=9, length=3, color=C_AXIS)


def titles(ax, title, subtitle):
    """Every chart title carries the (Synthetic data) marker."""
    ax.set_title(f"{title} (Synthetic data)", color=C_INK, fontsize=13,
                 fontweight="bold", loc="left", pad=24)
    ax.text(0.0, 1.025, subtitle, transform=ax.transAxes, color=C_INK_SOFT,
            fontsize=9.5, ha="left", va="bottom")


def stamp(fig, ax, seed, y):
    fig.text(0.0, y, header_comment(seed), fontsize=7.5, color=C_INK_SOFT,
             transform=ax.transAxes)


def save(fig, path):
    import matplotlib.pyplot as plt
    fig.savefig(path, dpi=200, facecolor=C_SURFACE, bbox_inches="tight")
    plt.close(fig)
    return path


def chart_gesture_scores(participants, outdir, seed):
    """
    One row per participant: their genuine re-draw against their wrong-stroke
    control. With a single attempt each, a paired dot plot shows every
    observation rather than binning fifteen points into a histogram.
    """
    import matplotlib.pyplot as plt

    df = participants.sort_values("participant", ascending=False)
    y = np.arange(len(df))
    gen = df.gesture_score.to_numpy(float)
    wrong = df.wrong_gesture_score.to_numpy(float)

    fig, ax = plt.subplots(figsize=(8.4, 5.6), facecolor=C_SURFACE)
    ax.hlines(y, wrong, gen, color=C_AXIS, linewidth=1.2, zorder=1)
    ax.scatter(wrong, y, s=52, color=C_SERIES_2, zorder=3,
               label=f"Wrong stroke (n={len(df)})")
    ax.scatter(gen, y, s=52, color=C_SERIES_1, zorder=3,
               label=f"Genuine re-draw (n={len(df)})")

    ax.axvline(GESTURE_THRESHOLD, color=C_INK_SOFT, linewidth=1.6,
               linestyle=(0, (5, 3)), zorder=2)
    ax.annotate(f"Accept threshold {GESTURE_THRESHOLD:.2f}",
                xy=(GESTURE_THRESHOLD, len(df) - 0.4),
                xytext=(6, 0), textcoords="offset points",
                color=C_INK_SOFT, fontsize=9.5, va="top")

    accept = 100.0 * (gen >= GESTURE_THRESHOLD).mean()
    far = 100.0 * (wrong >= GESTURE_THRESHOLD).mean()
    titles(ax, "Gesture match score per participant, genuine vs wrong stroke",
           f"{accept:.0f}% of genuine re-draws clear the threshold; "
           f"{far:.0f}% of wrong strokes do. One attempt each. "
           f"Simulated, not measured.")
    ax.set_xlabel("Match score (0-1)", color=C_INK_SOFT, fontsize=10)
    ax.set_yticks(y)
    ax.set_yticklabels(df.participant, color=C_INK_SOFT)
    ax.set_xlim(0.20, 0.92)
    ax.set_ylim(-0.7, len(df) - 0.3)
    style_axes(ax, x_grid=True)
    leg = ax.legend(frameon=False, fontsize=9.5, ncol=2, loc="upper center",
                    bbox_to_anchor=(0.5, -0.09))
    for txt in leg.get_texts():
        txt.set_color(C_INK_SOFT)
    stamp(fig, ax, seed, -0.20)
    return save(fig, outdir / "chart_gesture_scores.png")


def chart_time_per_factor(factor_times, outdir, seed):
    import matplotlib.pyplot as plt

    df = factor_times.sort_values("participant", ascending=False)
    y = np.arange(len(df))
    pin = df.pin_seconds.to_numpy(float)
    ges = df.gesture_seconds.to_numpy(float)
    otp = df.otp_seconds.to_numpy(float)
    unlocked = df.unlocked.to_numpy(bool)

    fig, ax = plt.subplots(figsize=(8.4, 6.2), facecolor=C_SURFACE)
    bar = dict(height=0.62, edgecolor=C_SURFACE, linewidth=2.0)
    ax.barh(y, pin, color=C_SERIES_1, label="PIN", **bar)
    ax.barh(y, ges, left=pin, color=C_SERIES_2, label="Gesture", **bar)
    ax.barh(y, otp, left=pin + ges, color=C_SERIES_3, label="OTP", **bar)

    total = pin + ges + otp
    for yi, tv, ok in zip(y, total, unlocked):
        ax.text(tv + 0.7, yi, f"{tv:.0f}s" if ok else f"{tv:.0f}s  no unlock",
                va="center", ha="left", color=C_INK_SOFT, fontsize=9)

    titles(ax, "Time spent per authentication factor, per participant",
           f"One attempt per factor; UI transitions excluded. Total unlock time "
           f"is recorded only for the {int(unlocked.sum())} of {len(df)} who "
           f"cleared all three. Simulated, not measured.")
    ax.set_yticks(y)
    ax.set_yticklabels(df.participant, color=C_INK_SOFT)
    ax.set_xlabel("Seconds", color=C_INK_SOFT, fontsize=10)
    ax.set_xlim(0, total.max() * 1.26)
    style_axes(ax, x_grid=True)
    leg = ax.legend(frameon=False, fontsize=9.5, ncol=3, loc="upper center",
                    bbox_to_anchor=(0.5, -0.085))
    for txt in leg.get_texts():
        txt.set_color(C_INK_SOFT)
    stamp(fig, ax, seed, -0.175)
    return save(fig, outdir / "chart_time_per_factor.png")


def chart_false_revokes(participants, outdir, seed):
    import matplotlib.pyplot as plt

    df = participants.sort_values("participant")
    x = np.arange(len(df))
    v = df.false_revokes_seated.to_numpy(int)

    vmax = max(1, int(v.max()))

    fig, ax = plt.subplots(figsize=(8.4, 3.4), facecolor=C_SURFACE)
    ax.bar(x, v, width=0.6, color=C_SERIES_1)
    # Zero is the expected value here, so mark it rather than leaving a blank.
    ax.bar(x[v == 0], vmax * 0.012, width=0.6, color=C_AXIS)
    for xi, vi in zip(x[v > 0], v[v > 0]):
        ax.text(xi, vi + vmax * 0.03, str(vi), ha="center", va="bottom",
                color=C_INK_SOFT, fontsize=9)

    zero_pct = 100.0 * (v == 0).mean()
    titles(ax, "SpoofDetector false revokes in 3 minutes of normal sitting",
           f"{zero_pct:.0f}% of participants were never revoked while seated "
           f"({STRIKES_BEFORE_REVOKE} strikes revoke). Simulated, not measured.")
    ax.set_xticks(x)
    ax.set_xticklabels(df.participant, color=C_INK_SOFT)
    ax.set_yticks(np.arange(0, vmax + 1))
    ax.set_ylim(0, vmax + 0.25)
    ax.set_ylabel("False revokes", color=C_INK_SOFT, fontsize=10)
    style_axes(ax)
    stamp(fig, ax, seed, -0.25)
    return save(fig, outdir / "chart_false_revokes.png")


def make_charts(participants, factor_times, outdir, seed):
    import matplotlib
    matplotlib.use("Agg")
    return [chart_gesture_scores(participants, outdir, seed),
            chart_time_per_factor(factor_times, outdir, seed),
            chart_false_revokes(participants, outdir, seed)]


# --------------------------------------------------------------------------- #
# Methodology paragraph
# --------------------------------------------------------------------------- #

def methodology(participants, seed):
    p = participants
    accept = 100.0 * (p.gesture_result == "pass").mean()
    far = 100.0 * (p.wrong_gesture_rejected == NO).mean()
    erratic = 100.0 * (p.revoked_in_erratic_test == YES).mean()
    zero_fr = 100.0 * (p.false_revokes_seated == 0).mean()
    tt = p.total_unlock_time_seconds.dropna()

    return f"""\
# Methodology - synthetic participant dataset

*Paste the section below into the report. The dataset it describes is simulated;
the surrounding text must keep saying so.*

---

## Methodology (simulated participant data)

No human participants were recruited for this evaluation. With the module
leader's approval, the participant-level results reported here are **synthetic**:
they were generated by `Tools/generate_synthetic_participants.py`, a numpy/pandas
script that simulates {N_PARTICIPANTS} participants (P01-P{N_PARTICIPANTS:02d})
from a fixed random seed (seed = {seed}), so the dataset is fully reproducible
and can be regenerated byte-for-byte by re-running the script. Every output file
carries a `# SYNTHETIC DATA` banner as its first line and every row a
`data_source = "synthetic"` column.

The dataset is a single flat table of {len(p)} rows, **one row per participant**,
with one observation per measure: each simulated participant makes one PIN entry
sequence, one genuine gesture re-draw, one deliberately different wrong-stroke
attempt, one live OTP entry and one stale-OTP attempt, then the two
anti-spoofing tests. No column is a mean over repeated attempts, and there is no
per-attempt table, so each number in a row can be read directly as that
person's result.

The simulation is parameterised by values taken from the **real** BoardroomXR
build and from single-headset bench testing on the Quest, rather than chosen
arbitrarily. The gesture accept threshold of {GESTURE_THRESHOLD:.2f}
(`MFASettings.gestureMatchThreshold`, lowered from 0.85 during bench testing
because honest re-draws were being rejected); the match-score bands observed on
device - roughly {GENUINE_SCORE_LO:.2f}-{GENUINE_SCORE_HI:.2f} for a genuine
re-draw of an enrolled stroke and {WRONG_SCORE_LO:.2f}-{WRONG_SCORE_HI:.2f}
for a different stroke scored against the same template; the {PIN_LENGTH}-digit
PIN with a {MAX_PIN_ATTEMPTS}-attempt lockout; the {OTP_DIGITS}-digit rotating
token with a {OTP_VALIDITY_SECONDS}s validity window; the
{SESSION_TIMEOUT_SECONDS:.0f}s session timeout; and the anti-spoofing policy
({ANOMALY_SD_THRESHOLD} SD from the movement baseline,
{STRIKES_BEFORE_REVOKE} strikes to revoke, a {STRIKE_REFRACTORY_SECONDS}s
refractory period between strikes, {STRIKE_DECAY_SECONDS:.0f}s strike decay and a
{MIN_SPEED_FOR_ANOMALY} m/s noise floor) are all real configuration values of the
system under test. What is synthetic is the *per-participant observations*
generated around them.

Each simulated participant is first given a stable set of latent traits - entry
pace, input accuracy, gesture repeatability and a movement style (calm, moderate
or fidgety) - so that their results correlate across factors the way one person's
would, instead of being independent noise per measure. The traits are not
reported; they only shape the observations. Those are then drawn from plausible
parametric families rather than uniform random numbers: entry and draw times are
lognormal (positive and right-skewed, as human entry times are) and clipped to
physically sensible limits; gesture match scores are normal draws clipped to the
device-observed bands, centred per participant and tightened for the more
repeatable re-drawers; the seated false-revoke count is a Poisson draw and the
erratic-test revoke a Bernoulli one. The reported
`gesture_enrolment_score` is the mean self-match of the
{GESTURE_ENROLMENT_SAMPLES} enrolment strokes that build the template, which is
the only column summarising more than one stroke, and it describes the template
rather than an attempt. `total_unlock_time_seconds` is the three factor times
summed, excluding UI transitions between the panels, and is left blank for a
participant who did not clear all three factors inside the
{SESSION_TIMEOUT_SECONDS:.0f}s timeout - a blank means they did not get in.

Three outcomes are fixed by construction rather than sampled, because they are
controls on the implementation and not measurements of people: a code older than
the {OTP_VALIDITY_SECONDS}s validity window is always rejected; a wrong stroke is
never drawn from a distribution that could reach the accept threshold; and the
live OTP entry fails only if it over-runs the validity window, since a mistyped
digit is simply retyped inside it and so costs time rather than the factor.

Across the generated dataset, {accept:.0f}% of genuine gesture re-draws cleared
the {GESTURE_THRESHOLD:.2f} threshold and {far:.0f}% of wrong strokes did;
median total time to unlock across all three factors was {tt.median():.1f}s
(mean {tt.mean():.1f}s, SD {tt.std(ddof=1):.1f}s, n = {len(tt)} of {len(p)}
who cleared all three); the erratic-movement test triggered a revoke for
{erratic:.0f}% of participants; and {zero_fr:.0f}% recorded no false revoke
during three minutes of normal sitting. Mean SUS was
{p.sus_score.mean():.1f} (SD {p.sus_score.std(ddof=1):.1f}).

**These figures are properties of the simulation's parameters, not evidence about
real users.** They show the configured pipeline behaving as designed under
plausible inputs, and they support no claim about usability, error rates or
security with real participants; establishing those would require an actual user
study with ethical approval.

---

## Files

| File | Contents |
|---|---|
| `synthetic_participants.csv` | one row per simulated participant ({len(p)} rows) |
| `synthetic_summary.csv` | mean / SD / min / max per measure, plus headline rates |
| `chart_gesture_scores.png` | genuine vs wrong-stroke score per participant, {GESTURE_THRESHOLD:.2f} threshold |
| `chart_time_per_factor.png` | time per factor per participant |
| `chart_false_revokes.png` | false revokes per participant |

Columns of `synthetic_participants.csv`:

| Column | Meaning |
|---|---|
| `participant` | simulated participant id, P01-P{N_PARTICIPANTS:02d} |
| `pin_time_seconds` | total time across the PIN attempts made, including the re-prompt gap |
| `pin_attempts` | attempts used, 1-{MAX_PIN_ATTEMPTS} |
| `gesture_enrolment_score` | mean self-match of the {GESTURE_ENROLMENT_SAMPLES} enrolment strokes |
| `gesture_score` | match score of the genuine re-draw |
| `gesture_result` | `pass` / `fail` against the {GESTURE_THRESHOLD:.2f} threshold |
| `wrong_gesture_score` | match score of a different stroke against the same template |
| `wrong_gesture_rejected` | Y / N - Y means it stayed below the threshold |
| `otp_time_seconds` | time to read and type the {OTP_DIGITS}-digit live code |
| `stale_otp_rejected` | Y / N - a code older than {OTP_VALIDITY_SECONDS}s must be rejected |
| `total_unlock_time_seconds` | the three factor times summed; blank if they did not get in |
| `revoked_in_erratic_test` | Y / N - revoked during the {ERRATIC_TEST_SECONDS:.0f}s erratic-movement test |
| `false_revokes_seated` | revokes during {SITTING_TEST_SECONDS / 60:.0f} minutes of normal sitting |
| `sus_score` | System Usability Scale, 0-100 |
| `data_source` | always `synthetic` |

Real measured on-device events are kept separately in
`Tools/real_device_data/device_test_events.csv` (`data_source = real_device`),
extracted from the Quest `adb logcat` capture by
`Tools/extract_device_events.py`. The two datasets must not be combined.

Regenerate with `python Tools/generate_synthetic_participants.py`.
Read a CSV back with `pd.read_csv(path, comment="#")`.
"""


# --------------------------------------------------------------------------- #
# Entry point
# --------------------------------------------------------------------------- #

def main():
    ap = argparse.ArgumentParser(
        description="Generate the synthetic participant dataset for the "
                    "BoardroomXR report. Produces simulated data only.")
    ap.add_argument("--seed", type=int, default=DEFAULT_SEED,
                    help=f"random seed (default {DEFAULT_SEED})")
    ap.add_argument("--outdir", type=Path,
                    default=Path(__file__).resolve().parent / "synthetic_data",
                    help="output directory")
    args = ap.parse_args()

    outdir = args.outdir
    outdir.mkdir(parents=True, exist_ok=True)

    participants, factor_times = generate(args.seed)
    summary = summarise(participants)

    written = [
        write_csv(participants, outdir / "synthetic_participants.csv", args.seed),
        write_csv(summary, outdir / "synthetic_summary.csv", args.seed),
    ]
    written += make_charts(participants, factor_times, outdir, args.seed)

    doc = outdir / "METHODOLOGY.md"
    doc.write_text(methodology(participants, args.seed), encoding="utf-8")
    written.append(doc)

    banner = header_comment(args.seed)
    rule = "=" * max(len(banner), 78)
    print(rule)
    print(banner)
    print(f"{N_PARTICIPANTS} simulated participants, one row each. "
          f"No real human-subject data.")
    print(rule)

    show = summary.drop(columns=["data_source"]).copy()
    for col in ("mean", "sd", "min", "max"):
        show[col] = show[col].map(lambda v: "" if pd.isna(v) else f"{v:,.2f}")
    print("\nSUMMARY (synthetic data)\n")
    print(show.to_string(index=False))

    print("\nPER-PARTICIPANT (synthetic data)\n")
    print(participants.to_string(index=False))

    print("\nWritten to " + str(outdir))
    for path in written:
        print(f"  {Path(path).name}")
    print("\n" + textwrap.fill(
        "Reminder: every file above is simulated. Label it as synthetic "
        "wherever it appears in the report.", 78))


if __name__ == "__main__":
    main()
