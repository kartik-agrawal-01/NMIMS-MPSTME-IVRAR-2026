# Expo day — step by step

For whoever is running the stall. You do **not** need to know any code.
Everything lives in this `EXPO` folder. The detailed scenario scripts are in
`..\EXPO_SCENARIOS.md` — print that too, it is the thing you read out to examiners.

---

## 1. The night before

1. Charge the **headset** to 100% and the **laptop** to 100%.
2. Pack the **USB-C data cable** (a charge-only cable will not work — if the laptop never
   shows the headset, the cable is the first thing to blame).
3. Double-click **`CHECK_SETUP.bat`**. Every line must say `PASS`.
   - `APK SHA-256 matches the tested build` must be PASS. If it is FAIL, the APK in this
     folder is not the one that was tested — stop and ask Kartik.
   - `port 8080 is free` must be PASS, or the live dashboard will not open.
4. Close anything heavy on the laptop (Unity, games, big browser windows). Leave it plugged in.
5. Optional but useful: copy this whole `EXPO` folder to a pendrive as a backup.

---

## 2. At the stall, before the first visitor (5 minutes)

1. Plug the headset into the laptop with the USB-C cable.
2. Put the headset on. If it asks **"Allow USB debugging?"**, tick **Always allow** and press **Allow**.
   (You may have to take the headset off and on again to see the prompt.)
3. Double-click **`START_EXPO.bat`**.
   - It waits for the headset, then prints the serial and model.
   - If more than one device is listed, type the number of the one you want.
   - It installs the app **only if** the installed version is not `5.0`. It never wipes
     profiles on its own.
   - It asks once: **"Reset all visitor profiles now? (Y/N)"** → press **Y** on a fresh day.
   - Three small windows appear minimised (`BXR logcat`, `BXR puller`, `BXR forward`).
     **Leave them alone.** They capture the logs and keep the dashboard connected.
   - A browser opens `http://localhost:8080` — the live dashboard for spectators.
4. Put the headset on and check:
   - the **guardian / boundary** is set for where the person will stand or sit;
   - **casting** is on (headset → phone or laptop) so bystanders can see what the wearer sees;
   - the trust number on the laptop dashboard is moving, not frozen.
5. Leave the `START_EXPO` window open all day. It is your control panel.

---

## 3. For each examiner / visitor

1. In the `START_EXPO` window press **V**.
2. Type, when asked:
   - **Visitor label**: `V01`, then `V02`, `V03`… (just keep counting)
   - **Scenario code**: `S1` to `S7` — which demo you are about to run (see `EXPO_SCENARIOS.md`)
   - **Badge used**: `EMP-1001` … `EMP-1006`
   - **Note**: anything you want to remember, or just press Enter
3. Hand over the headset and run that scenario from `EXPO_SCENARIOS.md`.
4. **Give each new visitor a different badge** (1001 → 1002 → 1003 …). That is what gives
   each person their own signature and their own movement profile.
5. If a visitor's signature will not enrol or keeps failing, use the **presenter console**
   inside the headset (left controller **Y** button) → **Reset this badge**, and start again.
6. When you have been through all six badges, or anything feels stale, press **Q** and then
   run `START_EXPO.bat` again answering **Y** to the reset question — or just use the
   presenter console's **Reset ALL badges**.

> Pressing **V** is what makes the logs readable afterwards. If you forget, the data is still
> captured, it just cannot be split per visitor. Press V even if you are in a hurry.

---

## 4. If something goes wrong

| What you see | What to press |
|---|---|
| App frozen or black inside the headset | `START_EXPO` window → **R** (restart app). Wait ~15 s. |
| Headset got unplugged | Just plug it back in. The capture loops reconnect on their own. Then press **D** to reopen the dashboard. |
| Dashboard says "no data from headset" | Press **D**. If still nothing, press **R**, wait 15 s, press **D** again. |
| App crashed back to the Quest menu | Press **R**. If it crashes again, press **P** (pull now, saves the evidence) and then **R**. |
| Laptop needs to be shut down right now | Run **`STOP_AND_SAVE.bat`** — it saves everything and analyses it. |
| `START_EXPO` window was closed by accident | Run **`STOP_AND_SAVE.bat`** first (saves the data), then `START_EXPO.bat` again. |
| Headset battery dying | Keep it on the cable; it charges while plugged into the laptop. Press **P** to pull data now. |
| Someone uninstalls the app / factory resets | The 60-second puller already copied the data into the session folder. Press **P**, then reinstall by running `START_EXPO.bat`. |
| Nothing works and examiners are waiting | Fall back to the laptop dashboard and `EXPO_SCENARIOS.md` — you can explain every screen without the headset. |

---

## 5. End of day

1. In the `START_EXPO` window press **Q**.
   - It does a final pull, stops the capture windows, runs the analysis and opens the folder.
2. Everything is in `EXPO\logs\<date>_<time>\`:
   - `summary.txt` — the readable summary (visitors, MFA passes/fails, lockouts, latencies)
   - `summary.csv` — the same numbers as rows, for the report
   - `visitor_timeline.csv` — one row per visitor with their outcome
   - `visitors.csv` — exactly what you typed when you pressed **V**
   - `logcat_full.txt` — the full device log with `[BioEvt]`, `[BioWin]` and `[Visitor]` lines
   - `pulled\` — the score CSVs, templates, signatures and mimicry recordings off the headset
3. **Copy that whole folder to a pendrive**, and ideally also email it to yourself.
   Do not rely on the laptop alone.
4. Nothing in that folder is needed by the app, so it is safe to copy while the app runs.

---

## 6. The two-minute headset test — do this the moment you get a headset

This is the only part that was never tested on real hardware, so check it first and report back.

1. **Text orientation** — look at the SOC panel on your right, the badge wall on your left and
   the presenter console. All text should read normally, not mirrored.
2. **Panel placement** — badge wall within easy arm's reach on the left; SOC panel readable
   next to the whiteboard; presenter console low and left, not floating in your face.
3. **Keypad reachable** — the controller ray must still reach the PIN keypad and the OTP token.
   If the ray stops at a panel, that is a blocking bug.
4. **Left controller Y button** — press it. The presenter console should hide, press again to show.
5. **Dashboard moving** — on the laptop, the trust number and the chart should update ~4×/second.
6. **Audio volume** — the lockout alarm, the per-factor chimes and the warning beeps should be
   audible but not painful.

**If something looks wrong, send back:**
- which of the six checks failed, in one line;
- a photo or cast screenshot of what you see;
- the session folder from `EXPO\logs\` (it contains the full log).

That is enough to fix orientation, position, scale or volume without guessing — each of those
is a single number in one script.
