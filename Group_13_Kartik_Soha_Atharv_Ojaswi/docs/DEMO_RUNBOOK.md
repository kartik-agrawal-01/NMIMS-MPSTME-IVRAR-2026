# BoardroomXR — Live Demo Runbook

Single-headset (Quest) demo of MFA entry + post-auth movement anomaly revoke.

## What actually runs now

`NetworkHostBootstrap` (on the `NetworkManager` object in `SampleScene`) is the **only**
component that starts networking. In its `Start()` it calls `NetworkManager.StartHost()`
on the first frame, so `IsServer` becomes true and `SpoofDetector`, `BoardroomAccessGate`
and every `ServerRpc` execute instead of sitting dead.

Server credentials are provisioned from `NetworkManager.OnServerStarted`, which
`NetworkHostBootstrap` subscribes to in `Awake()`. Netcode raises that event synchronously
inside `StartHost()`, so the PIN hash and TOTP seed exist before `StartHost()` returns and
before any factor can be submitted. `ServerCredentialStore.Provision()` is idempotent, so
it is safe whichever code path started the server. Without provisioning, every factor
check fails closed.

`NetworkBootstrap` is no longer auto-installed at runtime (the
`[RuntimeInitializeOnLoadMethod]` hook was **removed entirely** on 2026-09-29 — nothing in
the project referenced `NetworkBootstrap` or its `Active` property, so suppressing the hook
conditionally would have left dead complexity behind). It previously raced
`NetworkHostBootstrap`, lost, and skipped provisioning — so even the correct PIN was
rejected. It is not in any scene; to use it for a menu-driven host/join flow, place it
explicitly and remove `NetworkHostBootstrap`. To disable auto-hosting, untick
`NetworkHostBootstrap.startHostOnPlay`.

**One player rig.** `NetworkManager.NetworkConfig.PlayerPrefab` is now **cleared**
(`SampleScene.unity:8249`, `{fileID: 0}`) and the in-scene `MFAPlayerRig` is the only rig.
Before this, starting the host also auto-spawned `MFAPlayerRig(Clone)`, giving two
`MFACoordinator`s, two `SpoofDetector`s and two `AvatarIdentity`s. `MFAPlayerRig` remains
registered in `DefaultNetworkPrefabs.asset` — that only makes it spawnable, and nothing
spawns it now. Nothing depended on a Netcode-spawned player: no code reads
`NetworkManager.LocalClient`, `PlayerObject`, `ConnectedClients` or
`GetPlayerNetworkObject`, and the in-scene rig's `NetworkObject` has `m_InScenePlaced: 1`,
so `OnNetworkSpawn` still fires on host start — `AvatarIdentity` still registers with
`BoardroomAccessGate` (as owner client 0, which the gate's host-local path handles) and the
gate's recheck loop still has an entry to sweep.

**Verification.** `Assets/Tests/` holds a PlayMode suite (`BoardroomXR.Tests`) that loads
this scene, lets `NetworkHostBootstrap` bring the host up, and asserts: credentials are
provisioned before any factor check; the enrolled PIN validates and a wrong/empty/null PIN
does not; the current TOTP validates while one from 5 steps back and a random 6-character
string do not; a non-matching gesture is rejected with the fallback template off; exactly
one `MFACoordinator` and one `SpoofDetector` exist after host start; and all 11 keypad
buttons still reach `PinKeypadUI`. Run it from **Window > General > Test Runner > PlayMode**.
Last run 2026-09-29: **3 passed, 0 failed**.

## Demo credentials

| Factor | Credential | Where it lives |
|---|---|---|
| 1 — PIN | `1234` | `MFASettings.demoEnrolledPin`, hashed (salted SHA-256) into `ServerCredentialStore` at host start |
| 2 — Gesture | whatever you draw at enrolment | `Application.persistentDataPath/MFAEnrolment/gesture_<user>.json` |
| 3 — OTP | the 6 digits on the token prop | derived by `TotpGenerator` (HMAC-SHA1) from `MFASettings.demoOtpSeed` |

Any other PIN genuinely fails. A mismatched gesture genuinely scores low and fails.
A wrong or expired OTP genuinely fails — the server derives the expected code itself.

## Run order

1. **Launch the app.** Watch the console / `adb logcat` for
   `[NetworkHostBootstrap] StartHost() -> True` and `[server] Credentials provisioned`.
   If `Credentials provisioned` is missing, stop: every factor will be rejected.
   The confidential whiteboard starts **hidden**.
2. **Session start.** `MFACoordinator.autoStartOnPlay` defaults to **true**, so the session
   begins automatically at scene load — you do not need to walk through the antechamber.
   Note this also starts the 90 s `sessionTimeoutSeconds` clock immediately; if you want the
   clock to start only when the player reaches the doorway, set `autoStartOnPlay = false`
   and the `BoardroomAntechamberTrigger` drives it instead (stage → `Pin`).
   The trigger now also requires a kinematic `Rigidbody` — without one Unity never delivers
   `OnTrigger*` from the `CharacterController`-driven XR Origin to a static trigger collider.
3. **Factor 1:** tap `1 2 3 4` on the world-space keypad. Auto-submits at 4 digits.
4. **Factor 2 (first run is enrolment):**
   - Hold the **right controller trigger** and draw a shape. Release.
   - First time only: that stroke is saved as your template — you are asked to draw it again.
   - Draw it again; it is scored against the saved template. Need ≥ `0.75` similarity.
   - A failed stroke does **not** end the session — you can retry until the 90 s timeout.
5. **Factor 3:** read the 6-digit code off the token prop and tap it on the **same keypad**
   (it routes to the OTP during stage `Otp`). Auto-submits at 6 digits.
   `Clear` wipes the entry if you mistype.
6. **On success:** the whiteboard becomes visible. This is the visible "access granted".
7. **The revoke demo:** move your head sharply and erratically. Each sample past
   2.5σ of your own rolling 1.8 s baseline is a strike (max one per 0.4 s).
   3 strikes → verification revoked → whiteboard hides → **the MFA flow restarts at the PIN**.
8. **Re-authenticate** to show the loop closes.

## Tuning for the room

All in `Assets/MFASettings.asset`:

- `gestureMatchThreshold` (**0.75**) — lowered from 0.85, which was rejecting honest
  re-draws. Raise it to make factor 2 stricter. A genuine redraw typically lands 0.75–0.90;
  the actual score is printed in the log every attempt, so tune from real numbers.
- `behaviouralAnomalyStdDevThreshold` (2.5) — lower = revokes sooner.
- `sessionTimeoutSeconds` (90) — raise if the gesture stage needs more room on stage.

On `SpoofDetector` (MFAPlayerRig): `strikesBeforeRevoke`, `strikeRefractorySeconds`,
`strikeDecaySeconds` (a clean 12 s removes one strike), `minSpeedForAnomaly`.

## Reset between runs

Delete `Application.persistentDataPath/MFAEnrolment/` on the headset to force
gesture re-enrolment. Leave it to reuse the enrolled signature.

## The log

`SessionLogger` writes `Application.persistentDataPath/MFALogs/session_<UTC>.log`,
one file per run, buffered and flushed every 2 s and on pause/quit. The full path is
printed to the console at startup.

Every line comes from code that actually executed — nothing is seeded or replayed.
If a factor was never reached, there is no line for it; that absence is the record.
Pull it with:

```
adb pull /sdcard/Android/data/<package>/files/MFALogs/
```

## Known gaps (say these out loud rather than implying otherwise)

- **This is not a biometric and has no EER.** `SpoofDetector` is a within-session
  movement outlier detector: it asks "is this person moving oddly *for themselves,
  right now*", not "is this the enrolled person". No enrolment, no per-user model,
  no match score — so no FAR/FRR curve and no EER can be computed from it.
  It reads head position only (one scalar feature); no hand kinematics are sampled.
- **No telemetry pipeline.** The log is text events, not a per-frame kinematic stream.
  There is no capture harness, no participant labelling, and no offline ROC analysis.
- **One shared credential set** for all user IDs, and the gesture score is reported by
  the client (bounds-checked server-side, not re-derived). Fine for a demo, not for a
  threat model.
- **Stage prompts are log-only in-headset.** `PinKeypadUI.statusLabel` is unassigned —
  drag any world-space `Text` into it in the Inspector if you want the prompts visible
  to the audience. The keypad display already switches between `****` (PIN) and
  `______` (6-digit OTP), which signals the stage.
- Three `ServerRpc(RequireOwnership=false)` deprecation warnings. The attribute still
  functions in NGO 2.13; migrating to the `[Rpc(SendTo.Server, ...)]` API is cleanup, not a fix.

## Known gaps — say out loud in viva

These are open limitations as of the pre-demo readiness pass (2026-09-29). None of them
is fixed; they are stated here so nothing is implied that the build does not do.

- **Gesture authentication is controller-only.** `GestureCaptureDriver` binds to
  `<XRController>{RightHand}/devicePosition` for the stroke and to
  `triggerPressed` / `trigger` / `gripPressed` for the draw button. It does not read
  XR Hands joints, even though the scene rig is XR Origin Hands and a Hands Permissions
  Manager is present. With bare hands on the Quest no draw button ever fires and
  `handTransformFallback` is unassigned, so **the gesture stage stalls until the 90 s
  timeout**. The demo must be run holding a Touch controller in the right hand.
  The template is not pre-baked: the first valid stroke on the headset becomes the
  enrolment (`persistentDataPath/MFAEnrolment/`).
- **SpoofDetector timing is not refresh-rate independent.** It samples head position in
  `FixedUpdate` (50 Hz, `Time.fixedDeltaTime` = 0.02 s), but the head pose only changes
  once per rendered frame. Each 20 ms sample therefore covers a varying number of render
  frames, so the measured speeds are aliased differently at 72, 90 and 120 Hz than in the
  Editor. The z-score is computed against the spread of those speeds, so
  `behaviouralAnomalyStdDevThreshold` (2.5σ) and `minSpeedForAnomaly` values tuned in the
  Editor are not guaranteed to transfer to the headset. They have not been re-tuned on device.
- **Multi-client is not supported.** Resolving the duplicate rig by clearing `PlayerPrefab`
  (see "One player rig" above) means a second joining client gets **no rig of its own** —
  it would share the in-scene, server-owned rig. `AvatarIdentity.ClaimedUserId` is
  owner-writable, so only the host can set the claim; a remote client could still submit
  factors against the shared rig because all three verify RPCs are
  `RequireOwnership = false`. This is correct for the single-headset demo and wrong for a
  real session; a lobby flow would need a per-client rig and a re-think of B8/B10/B12.

### Debug / test / bypass inventory — verified OFF before demo

Swept 2026-09-29 (re-verified against the scene/prefab YAML on the same date) across every
script in the MFA and SpoofDetector flow. There are **no** `#if UNITY_EDITOR`,
`DEVELOPMENT_BUILD` or `Debug.isDebugBuild` guards anywhere in `Assets/Scripts`, so every
item below **ships identically in development and non-development builds** — none of them
is compiled out of a release APK. Items are listed, not fixed; each is a pending decision.

Tick each box only after confirming the state yourself in the Inspector — the states below
were read out of the serialised YAML, which is authoritative until someone edits the scene.

**Switchable — tick only after checking the state in the Inspector:**

- [ ] **B1 `GestureTestSubmitter` / `PinKeypadCanvas/Btn_SubmitGesture`** —
  `UI/GestureTestSubmitter.cs:18-35`, button wired at `SampleScene.unity:11869`.
  Injects a synthetic 5-point straight line straight into
  `GestureAuthenticator.EndCaptureAndScore()`, skipping controller input and the
  driver's ≥12-point / ≥0.25 s checks. Passes if the enrolled template is roughly
  a straight line. *State: **INACTIVE** — `Btn_SubmitGesture` has `m_IsActive: 0`
  (`SampleScene.unity:11789`); it is a child of `PinKeypadCanvas`, which is itself active
  (`m_IsActive: 1`, `SampleScene.unity:22053`) and must stay active or the PIN cannot be
  entered. Component still in scene.*
- [ ] **B2 `OtpTestSubmitter` / `OTPTestCanvas/Btn_SubmitOtp`** — `UI/OtpTestSubmitter.cs:18-29`,
  wired at `SampleScene.unity:20646`. Reads the current TOTP off `OTPAuthenticator` and
  submits it, so factor 3 passes without the user reading or typing anything.
  *State: **INACTIVE via parent** — `OTPTestCanvas` has `m_IsActive: 0`
  (`SampleScene.unity:391`); `Btn_SubmitOtp` itself is still `m_IsActive: 1`
  (`SampleScene.unity:20565`), so re-activating the canvas re-arms the bypass immediately.*
- [ ] **B3 `MFACoordinator.useTestGestureTemplateFallback`** — `Auth/MFACoordinator.cs:39`,
  used `:195-199`, template `:236-246`. With no enrolment, substitutes a straight-line
  template. *State: OFF — not serialized on prefab or scene, code default `false`.*
- [ ] **B4 `GestureCaptureDriver.handTransformFallback`** — `Auth/GestureCaptureDriver.cs:34`,
  used `:176-182`. Any assigned Transform becomes the pen when no XR controller reports.
  *State: unassigned (`MFAPlayerRig.prefab:212`) — inert.*
- [ ] **B5 `<Keyboard>/space` draw binding** — `Auth/GestureCaptureDriver.cs:64`. Acts as the
  draw button without a controller trigger. Position is still read from the XR controller
  (or `(0,0,0)`), so on its own it cannot produce a passing stroke. *State: live.*
- [ ] **B6 `AvatarIdentity.LocalClaimedIdOverride`** — `Identity/AvatarIdentity.cs:55`.
  Static hook that replaces the claimed user ID. *State: inert — nothing in the project
  sets it.*
- [ ] **B7 `MFACoordinator.autoStartOnPlay`** — `Auth/MFACoordinator.cs:33`, `:77`. Starts the
  session at scene load, skipping the antechamber. Flow shortcut, not an auth bypass
  (all three factors are still required). *State: live — default `true`, not serialized.*
- [ ] **B7a `TelemetryRecorderUI` / `TelemetryRecorderCanvas`** — `UI/TelemetryRecorderUI.cs`.
  Hand-driven control panel for `KinematicTelemetryLogger` (start/stop CSV capture, set
  participant ID). Adjacent to the MFA flow rather than inside it — it injects no auth
  input and cannot pass a factor. *State: **INACTIVE** — `m_IsActive: 0`
  (`SampleScene.unity:17713`).*
- [ ] **B7b `OTPTokenDisplay.autoInitialize`** — `UI/OTPTokenDisplay.cs:23`, used `:39-43`.
  Initialises `OTPAuthenticator` from `MFASettings` before any session opens, so the prop
  shows a live code while idle. Not a bypass — it uses the same seed the server provisions
  from, and factor 3 still requires the code to be entered and server-checked.
  *State: live — default `true`.*
- [ ] **B7c `BoardroomAntechamberTrigger.verboseTriggerLogging`** — `Auth/…Trigger.cs:39`,
  used `:62`, `:78`, `:99`. Logs every trigger entry. Console noise only, no auth effect.
  *State: live — default `true`.*
- [ ] **B7d `NetworkBootstrap` is still in the project** — `Network/NetworkBootstrap.cs`.
  A second, unused host-starter with its own `Provision()` call and
  `preventDuplicatePlayerSpawn`. Not in any scene and no longer auto-installed, so it is
  inert — but dropping it into a scene alongside `NetworkHostBootstrap` would reintroduce
  the two-starter race. *State: inert — present in `Assets/Scripts`, referenced by nothing.*

**Structural — cannot be switched off; acknowledge out loud:**

- [ ] **B8 Server verifies on OTP alone** — `Network/NetworkAuthRelay.cs:121-131`
  (`ServerMarkVerified()` at `:129`). The server keeps no record that PIN or gesture
  passed; stage order is enforced only client-side in `MFACoordinator`. A client that
  sends `VerifyOtpServerRpc` with a correct code is marked verified.
- [ ] **B9 Gesture score is client-reported** — `Network/NetworkAuthRelay.cs:105-119`. The
  server only bounds-checks `[0,1]` against the threshold (`:112`); a modified client can
  send `1.0`.
- [ ] **B10 Any client can submit factors on any rig** — `RequireOwnership = false` on all
  three verify RPCs (`Network/NetworkAuthRelay.cs:96, 105, 121`). No effect with a single
  headset.
- [ ] **B11 Demo secrets ship in the client APK** — `Assets/MFASettings.asset:22-23`
  (`demoOtpSeed`, `demoEnrolledPin: "1234"`) is referenced by client-side components, so
  it is packed in plaintext; knowing the seed lets anyone compute the OTP. The comment at
  `Network/NetworkAuthRelay.cs:24` ("this class contains no secret material") is true of
  that class but not of the build.
- [ ] **B12 One credential set and a self-asserted identity** — `Identity/AvatarIdentity.cs:24`
  (owner-writable `ClaimedUserId`), `:124` (device ID); `Network/NetworkAuthRelay.cs:182`.
  Every claimed user ID is checked against the same PIN/seed.
- [ ] **B13 Gesture enrolment is trust-on-first-use** — `Auth/GestureCaptureDriver.cs:136-150`,
  `Auth/MFACoordinator.cs:211-216`. With no template on the device (fresh install, or after
  the "Reset between runs" step), the first valid stroke by whoever holds the controller
  becomes the template. The template JSON in `persistentDataPath/MFAEnrolment/` is
  plain text and can be replaced over `adb`.
- [ ] **B14 OTP token is visible in-world** — `UI/OTPTokenDisplay.cs:42`. The "possession"
  factor is a scene object; anyone looking at the headset view sees the code.
- [ ] **B15 PIN lockout is per-session only** — `Auth/PinAuthenticator.cs:33` resets attempts on
  every `Initialize()`, called from `MFACoordinator.cs:118` (BeginSession) and `:138`
  (RestartSession). Re-entering the antechamber or timing out gives 3 fresh attempts;
  there is no global lockout.
- [x] **B16 Gated content fails open if the server never starts — FIXED 2026-09-29.**
  Was: `_localContentVisible = true` with hiding only in `OnNetworkSpawn`, so a failed
  `StartHost()` left the whiteboard rendered. Now `BoardroomAccessGate.Awake()` hides the
  gated content before networking exists, `_localContentVisible` defaults to `false`, and
  `_localVisibilityApplied` forces the first application through the dedupe check (without
  it, defaulting the field to `false` would have made the initial hide a silent no-op and
  left the renderers visible — worse than before). Covered by
  `GatedContent_IsHiddenEvenWhenHostNeverStarts`, which loads the scene with
  `startHostOnPlay = false` and asserts zero enabled renderers.
  *Caveat: on a remote client the gate disables itself in `OnNetworkSpawn`, so content
  hidden in `Awake()` is never re-shown locally. Irrelevant to the single-headset demo, and
  multi-client is already unsupported.*
- [x] **B16a Avatar was never registered with the gate, so verification never revealed the
  content — FIXED 2026-09-29.** Pre-existing and unrelated to the `PlayerPrefab` change
  (`Registered avatar` appears zero times in logs from both the one-rig and two-rig
  configurations). In-scene `NetworkObject` spawn order is not guaranteed, and
  `MFAPlayerRig` spawned *before* `BoardroomAccessGate`, so
  `AvatarIdentity.OnNetworkSpawn`'s call to `ServerRegisterAvatar` hit the `!IsServer`
  guard on the still-unspawned gate and returned **silently**. `_connectedIdentities`
  stayed empty, `IsVerified.OnValueChanged` was never subscribed, and `RecheckLoop` swept
  an empty dictionary forever — so passing all three factors would **not** have revealed
  the whiteboard. Fixed by a catch-up sweep in `OnNetworkSpawn` that registers every
  `AvatarIdentity` already present, plus a log line on the deferred path so it can never
  fail silently again. The gate now logs `Registered avatars: 1` on spawn — check for that
  line. Covered by `GatedContent_IsRevealedAfterFullMfaVerification`.
- [ ] **B17 SpoofDetector blind spots** — `Identity/SpoofDetector.cs`: silently off if
  `settings` is null (`:90`, currently assigned) or no head/camera is found (`:91`); runs
  only while verified (`:103`); no evaluation for the first 90 samples (~1.8 s) after each
  verify/reset (`:123`); skips samples when stdDev ≤ 0.0001 (`:137`) or speed < 0.35 m/s
  (`:138`); 0.4 s refractory and 12 s strike decay mean slow, spaced anomalies never reach
  3 strikes.

- [ ] **B18 `SpoofDetector`'s head-transform safety net does not catch a rig-root
  assignment.** `Identity/SpoofDetector.cs:72-86`. `ResolveHeadTransform()` only substitutes
  `Camera.main.transform` when `headTransform.GetComponentInChildren<Camera>() == null`
  (`:78`). `GetComponentInChildren` searches **self and all descendants**, so assigning the
  XR Origin root — or `Camera Offset`, or any other ancestor of `Main Camera` — passes the
  check and is silently kept. That is precisely the case the comment at `:74-75` says it
  guards against: sampling an ancestor measures locomotion, not head motion, so a
  standing/seated user produces near-zero speed, `stdDev` collapses below the `0.0001`
  floor (`:137`), and **nothing is ever flagged** — the revoke demo would silently do
  nothing. The correct guard is `headTransform.GetComponent<Camera>() == null` (self only).
  *State: **not currently triggered** — `SampleScene`'s override on the `MFAPlayerRig`
  instance points at `Main Camera` itself, confirmed both by the fileID chain and by the
  runtime log line `SpoofDetector active (server) … head=Main Camera`. Latent trap, not a
  live defect. Verify from that log line, never from the Inspector field alone.*
- [ ] **B19 TOTP accepts a ±1 step drift window** — `Auth/TotpGenerator.cs:73`
  (`driftSteps = 1`), loop `:80-83`. With `otpValiditySeconds = 30`, a code stays acceptable
  for up to ~90 s across three steps, not 30 s. Deliberate (a user typing across a rotation
  should not be failed), and the PlayMode test pins the boundary by proving a code from 5
  steps back is rejected. *State: live by design.*

Checked and found **fail-closed** (no action): `GestureAuthenticator.PassesThreshold` with
no settings → false; `ScoreAgainstTemplate` with no template → 0; a degenerate
(motionless) stroke normalises to all-zero points and scores ~0.4–0.5, below the 0.75
threshold; `CheckPin` / `CheckOtp` when unprovisioned → false; `CheckPin` with a null or
empty PIN → false; `ServerCredentialStore.Provision` with no `MFASettings` found logs an
error and leaves the store unprovisioned rather than provisioning a blank credential;
`MFACoordinator` fails a factor when `CanReachServer` is false.
