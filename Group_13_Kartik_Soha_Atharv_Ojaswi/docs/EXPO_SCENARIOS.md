# BoardroomXR — expo stall script (v5)

**Research question.** How can continuous behavioural biometric authentication leveraging head and hand
kinematic telemetry achieve EER below 5% against avatar identity-spoofing in collaborative VR enterprise
environments?

**Everything below runs on one headset.** Each visitor presents a different badge, so each gets their own
signature and their own behavioural profile.

---

## Controls cheat sheet

> **On expo day, start with `EXPO\START_EXPO.bat`.** It installs if needed, offers the profile
> reset, starts the log capture and the dashboard, and gives you a menu with `[V] New visitor`.
> The numbered steps for a non-programmer are in `EXPO\EXPO_DAY_STEPS.md`.

| Thing | How |
|---|---|
| Badge reader wall | Low on your **left**, 6 badges. Point, pull trigger. (Editor: keys `1`–`6`) |
| PIN keypad | Straight ahead, 1.6 m. PIN is **1234** |
| OTP token | Ahead and slightly right, at hand height |
| SOC trust panel | On your **right**, next to the whiteboard — trust %, graph, 16 bars, reasons, event log |
| Wrist display | Left controller — trust %, state colour, sparkline |
| Presenter console | Low on your **left-front**. Toggle with the **left controller Y button** (Editor: `Y`, buttons `F1`–`F9`) |
| Laptop dashboard | Run `LIVE_DASHBOARD.bat`, opens `http://localhost:8080` |
| Wipe all profiles | `RESET_ALL_PROFILES.bat` (asks for confirmation) |

Default operating point is **Balanced — threshold 0.15, lockout after 6 consecutive windows**.
Default enrolment is **60 s**; for the stall use the **30 s** button (`F4`).

---

## S1 — Genuine user (the baseline, ~90 s)

1. Presenter: `RESET_ALL_PROFILES.bat` if the previous visitor just finished, then hand over the headset.
2. Visitor points at a badge on the left wall and pulls the trigger — say **EMP-1001 A. Mehta (CEO)**.
   → Badge card turns green-ish and reads `SELECTED · No profile`. MFA restarts at factor 1.
3. **PIN**: enter `1234` on the keypad ahead.
   → Chime. HUD: `Step 2 of 3: Gesture`. Status: `Draw your signature (1/3)`.
4. **Signature**: hold the trigger and draw a shape in the air, release. Repeat **three times**.
   → A glowing blue trail follows the hand. Status counts `(2/3)`, `(3/3)`, then
   `Signature enrolled from 3 strokes. Now draw it once more to log in.`
5. Draw the shape once more.
   → Trail turns **green**, chime, HUD: `Step 3 of 3: One-time code`.
6. **OTP**: read the 6 digits off the token and type them on the keypad.
   → "Access granted" chord. Glass doors **glide open**. Door strip:
   `ACCESS GRANTED - BOARD MEETING IN SESSION`. The whiteboard memo becomes visible.
7. **Enrolment** starts automatically: HUD shows `Enrolling: Learning how you move: NN%` with a progress bar
   and a countdown. Tell them: *look around, use both hands normally.* (30 s with `F4`.)
   → SOC panel reads `ENROLLING`, trust column shows enrolment %.
8. When it completes: SOC panel goes **green `VERIFIED · identity confirmed every 0.5 s`**, trust ~85–95%,
   reasons line reads `Movement matches the enrolled profile.`, badge card reads
   `CURRENTLY SIGNED IN · Biometric profile: N windows, adapted 0x`.

**Say:** "From here on, nothing re-asks them for anything. The system is checking *how they move*, four times
every two seconds."

---

## S2 — Headset handover (session hijack) — the headline demo

1. With S1 verified and trust high, the visitor takes the headset off and **you** put it on (or a second
   examiner does). Do not touch any button.
2. Move naturally — you are a different height and reach, so the features diverge immediately.

**Expected, in order:**
- Wrist display and SOC go **amber** `WARNING · movement does not match`, soft beep per window,
  violation counter climbing `1/6 … 5/6`.
- HUD shows the single biggest reason in plain English, e.g. `Eye height 11 cm lower than A. Mehta's profile`.
- At 6 consecutive violations: **two-tone alarm**, red pulsing vignette, whiteboard **shreds** into particles,
  glass doors **slam shut** showing `ACCESS REVOKED - IDENTITY MISMATCH`, whiteboard content hidden,
  MFA restarts at `Step 1 of 3: PIN`.
- SOC event log and the laptop dashboard both show a red `lockout` row with the score and the top feature.

**Measured lockout latency after handover** (in-scene automated test, scripted mover):
**Strict 2.9 s, Balanced 4.7 s.** Quote "about three to five seconds".

---

## S3 — Stolen credentials (examiner shoulder-surfs the signature)

1. Examiner watches the visitor from step S1 do the PIN and the signature.
2. Visitor hands the headset over. Examiner presents **the same badge** (EMP-1001), types `1234`, draws the
   shape they just watched, reads the token, enters the code.
   → All three factors **pass**. Doors open. This is the honest point: the three factors are *knowledge,
   behaviour-as-a-secret and possession*, and all three can be stolen or observed.
3. Within a few seconds the behavioural layer locks them out exactly as in S2 — because the signature shape
   was copied, but the *way they move* was not.

**Say:** "This is the gap the project is about. MFA guards the door. Nothing guarded the room after the door."

---

## S4 — Mimicry attack (the hardest case, and we report it honestly)

Mimicry is the established threat model: the attacker has the credentials **and** gets to observe and copy the
victim's movement. During every enrolment the app records the enrolling user's own head and hand trajectory
(30 Hz, last 20 s) and stores it beside their template.

1. Make sure the victim badge has been enrolled at least once (S1).
2. Presenter console → **Start mimicry attack**.
   → A translucent ghost avatar with the victim's badge name appears 1.2 m in front, facing away, replaying the
   victim's recorded motion in a loop. HUD banner: `MIMICRY ATTACK IN PROGRESS - copy the ghost`.
3. The impostor — logged in with the victim's badge — tries to match the ghost: same posture, same head turns,
   same hand rhythm.
4. Watch the SOC panel. Nothing in the engine is changed for this scenario, so whatever happens is a result:
   - if the trust score recovers and no lockout occurs, **say so** — mimicry is a real limitation of
     behavioural biometrics, and eye height and arm reach are the hardest features for a mimic to fake;
   - if it still locks out, point at the top-3 reasons to show which features gave them away.
5. Press the button again to stop. The event log records `mimicry_start` / `mimicry_stop` with the engine state
   at the end, so the attempt is in the record either way.

If the button reports *no reference recording*, that badge has not been enrolled on this headset yet.

---

## S5 — Posture change (sit vs stand) — a stated limitation

1. Enrol (S1) **seated**. Then stand up.
2. Eye height moves 25–40 cm, so `Eye height` and both `arm reach` features spike; at Strict this is a false
   lockout waiting to happen. The SOC panel names the cause in plain English, which is the point: the system
   can explain its own false rejects.
3. Mitigations to demonstrate live:
   - Presenter console → **Relaxed** (`F3`): threshold 0.10, 8 windows.
   - Presenter console → **Adaptive: ON** (`F9`, on by default): while Verified and the raw score is ≥ 0.6, each
     feature within 1 σ of its mean is nudged by a 1% EMA, so slow drift is absorbed. The SOC panel shows
     `N windows folded in this session`, and the badge card shows `adapted Mx`.
   - Say clearly: adaptation **never** runs in Warning or LockedOut, so an impostor cannot train the template.
     That rule has its own automated test.
4. Honest framing: posture change is a genuine weakness of a 16-feature geometric + kinematic template.
   The fix in a product would be posture-conditioned templates (one per sit/stand), which we did not build.

---

## S6 — Negative paths (wrong PIN, timeout, wrong gesture)

- **Wrong PIN** (e.g. `1111`): `Incorrect PIN. Try again.` Three wrong attempts → `PIN attempts exhausted`,
  session fails. Nothing opens.
- **Wrong signature**: draw a clearly different shape at the login step → trail turns **red**,
  `Gesture did not match (0.3x of 0.65). Draw again.` Server-side check logs the rejection.
- **Inconsistent enrolment**: during the 3 strokes, deliberately draw a different shape for stroke 2 → enrolment
  restarts with `That was a different shape (0.4x). Starting again - draw your signature (1/3).`
- **Timeout**: stop interacting for 150 s → `Session timed out. Start again.`
- Point out that PIN and OTP are checked **server-side** (salted SHA-256 PIN hash; TOTP re-derived on the
  server), not in the client.

---

## S7 — Live preset switch (the FAR/FRR trade-off)

Presenter console: `F1` Strict, `F2` Balanced, `F3` Relaxed. The SOC panel's threshold line and the
`k of K` counter move immediately, on the very next scored window.

**In-engine scripted regression runs, 15 runs** — reproducible from this repo
(`BoardroomXR > Run Biometric Evaluation (R01-R15)`; 60 s enrol, seeded motion profiles,
*not human participants*):

| Preset | threshold | k | false lockouts | handovers detected | mean lockout latency |
|---|---|---|---|---|---|
| Strict | 0.25 | 4 | 0/15 | 15/15 | 2.97 s |
| Balanced | 0.15 | 6 | 0/15 | 13/15 | 5.40 s |
| Relaxed | 0.10 | 8 | 0/15 | 10/15 | 6.10 s |

Window-level EER on that scripted set: **0.43%**. Say out loud that this is optimistic — the motion is
generated from smooth seeded sinusoids, so genuine and impostor windows separate far more cleanly than real
people do. It is a regression test for the pipeline, not a human result.

**Calibrated simulation study, N=40 synthetic users (paper)** — the figures reported in the write-up:

- **Strict ≈ 65% false lockouts** over the 40 genuine sessions; **Relaxed 0 false lockouts with 95% impostor
  detection in ≈ 6.1 s** — which is why Relaxed exists and why Balanced is the default.
- **Pilot offline EER 14.1%** on real headset telemetry (single device, few sessions).
- **Simulated cross-session EER 4.7%** — below the 5% target in the research question, on simulated data only.

> **Keep the two sets apart when you quote them.** The *in-engine scripted regression runs* do not reproduce
> the 65% / 95% figures from the *calibrated simulation study*: at 15 runs they show 0/15 false lockouts at
> Strict and 10/15 detection at Relaxed. The two use different motion models and different numbers of
> sessions, so the safest phrasing at the stall is: *"our calibrated simulation study over 40 synthetic users
> put Strict at about 65% false lockouts and Relaxed at zero with 95% detection in around six seconds; the
> in-engine regression runs that ship in the build use smoother scripted motion, and there we see no false
> lockouts at any preset with detection falling from 15/15 to 10/15 as we relax the threshold."*
> Both statements are true of their own data. Do not merge them into one number.

**The trade-off in one sentence:** lowering the threshold and demanding more consecutive bad windows cuts false
lockouts (good for a two-hour board meeting) and costs you detection speed and some detections outright
(bad for a stolen headset).

---

## "How it works" — talk track

1. The room is a collaborative VR boardroom with genuinely confidential content on the whiteboard.
2. Getting in needs three factors: a PIN, a mid-air signature, and a rotating one-time code — all checked on the
   server, not the client.
3. The problem: once the door is open, nothing checks who is still wearing the headset.
4. So after login we keep sampling the head and both controllers at 60 Hz.
5. Every 3 seconds of motion becomes 16 features: speeds, turn rates, acceleration, jerk, arm reach, eye
   height, head tilt, hand span.
6. Enrolment collects about 100 overlapping windows and stores each feature's mean and spread — that is the
   person's behavioural template, roughly 1 KB, kept on the device.
7. Every half second we score the newest window against it: a diagonal-covariance Mahalanobis distance mapped
   to a 0–1 trust score, then smoothed.
8. Below threshold for k consecutive windows, the **server** revokes verification: doors slam, content is
   hidden, full MFA again.
9. The panel on the right explains *why* in real units — "eye height 11 cm lower", "right hand 2.3× faster" —
   because an authentication decision nobody can interrogate is not usable in an enterprise.
10. Two sets of numbers, deliberately kept apart: the in-engine scripted regression runs are *cleaner* than
    the calibrated simulation study — smooth seeded motion separates genuine from impostor far more easily than
    40 calibrated synthetic users do — which is exactly why their false-lockout rates differ, and why we quote
    the calibrated study, not the regression runs, when we talk about how the system behaves for real people.
11. Honest limits: posture change is our main false-reject source, mimicry is the open attack, and our
    strongest EER numbers are on simulated data, not a human study.

---

## Rehearsal checklist (run this the morning of)

- [ ] Headset charged > 80%, and plugged into the laptop with the USB cable for the dashboard.
- [ ] Guardian / boundary set for a seated or small standing area; the badge wall is at arm's length on the left.
- [ ] `INSTALL_v5.bat` → app launches, no red errors. (To rebuild the APK first: close Unity, run
      `BUILD_v5.bat`; it takes 10–25 min and wants several GB of free RAM.)
- [ ] Casting on (headset → phone/laptop) so bystanders see what the wearer sees.
- [ ] `LIVE_DASHBOARD.bat` → page shows `live · HH:MM:SS` and a moving trust number.
- [ ] Walk S1 end-to-end once yourself; confirm doors open and the memo is readable.
- [ ] Walk S2 once; confirm alarm, doors slamming, MFA restart.
- [ ] Presenter console: Y button shows/hides it; press each preset once and watch the SOC threshold line move.
- [ ] `RESET_ALL_PROFILES.bat` → every badge reads `No profile`.
- [ ] Decide the per-visitor badge order (1001 → 1006) and keep a tally, so no two visitors share a profile.
- [ ] Between visitors: either hand out the **next** badge, or run `RESET_ALL_PROFILES.bat`.

---

## If something breaks

| Symptom | Fix |
|---|---|
| Badge wall / SOC panel not there | The furniture waits for the XR rig. Look around once; if still missing, restart the app (`INSTALL_v5.bat` relaunches it). |
| Trigger does nothing on a badge | You are at the **gesture** stage — all spatial buttons are deliberately dead there so the trigger can draw. Finish or time out the signature first. |
| Signature never accepted | Say `F7` (reset this badge) and enrol again with a **bigger, slower** shape. Threshold is 0.65; tiny scribbles score badly. |
| Enrolment seems to hang | Check the HUD percentage. It needs continuous movement; standing still produces too few usable windows. 30 s preset is `F4`. |
| Genuine user keeps getting locked out | `F3` Relaxed, confirm `Adaptive: ON` (`F9`), and re-enrol in the posture they will actually use (`F6`). |
| Doors stay shut after a successful login | The doors follow the **server's** `IsVerified`. If the SOC panel says `VERIFIED` but the doors are shut, restart the app — the host did not come up cleanly. |
| Dashboard says "no data from headset" | App must be running; re-run `LIVE_DASHBOARD.bat` after re-plugging the cable; check `FIND_TEST_HEADSET.bat`. |
| Everything feels off / previous visitor's profile in use | `RESET_ALL_PROFILES.bat`, then present a badge again. |
| Need a lockout **now** for a watching examiner | Presenter console → **FORCE LOCKOUT (demo)**. It is logged as `forced_lockout`, so say it is a demo trigger, never a detection. |

**Logs to pull afterwards:** `adb shell ls /sdcard/Android/data/com.kartikagrawal.boardroomxr/files/` —
`BiometricLogs/scores_*.csv` (every scored window), `MFALogs/session_*.log`, and in logcat the
`[BioWin]` and `[BioEvt]` lines.
