# Replay mode: scope (2026-10-07)

Status: scoped, owner decisions in (below). **R0 built 2026-10-07 (PC harness; results under R0). R1 built
2026-10-07 (drawing never changes a match; results under R1). R2 built 2026-10-07 and on the Odin (the app records
local matches against bots, keeps the last 10, and plays them back checked; results under R2). R2c built 2026-10-07
(9 golden replays in `tests/replays/` + `-replaygolden`). R2b in progress 2026-10-07: DetMath built and on (sim
version 3), PC-verified; the device half (Odin, Samsung) is waiting on the Odin's battery.** Source: a replay plan from another session (pasted by the owner 2026-10-07), checked
against the code, an entity census of every arena, then measured with R0.

## Summary

- Record-and-re-run works for local bot matches. The game takes one `CampaignInput` per 60 Hz step, and all gameplay
  randomness comes from one RNG stream (`Rng2`). Recording the local player's inputs plus both RNG values replays the
  whole match, bots included.
- The owner's rule: **a replay is exact or it's refused, never quietly wrong.** Every step carries a state checksum.
  Playback checks it before drawing that step, and stops with a reason at the first mismatch.
- Exact on the recording device. On every other device, exact by construction: our own trig functions instead of
  each platform's math library, proven by a cross-device test.
- In multiplayer, gameplay doesn't depend on what's on screen: no arena has a beam platform (census below), and R0
  measured it (window size, framing and third person change nothing). The only thing drawing changed was Spire's alt
  attack rocks, which updated only while drawing; R1 places them whether or not a frame draws (`Scene.HostDrawFreeSim`).
- Menus: REPLAYS goes under multiplayer like the file screen's MOVIES button. That needs the vanilla multiplayer menu,
  its own project (P-MP). Until then, testing goes through the dev launcher.

## What held up (checked)

| Claim | Where |
|---|---|
| Fixed 60 Hz step, one input per step | `CampaignRenderer.cs:695-708` -> `MatchSession.NetStep` (`MatchSession.cs:98`) -> `CampaignHost.Step` (`CampaignHost.cs:124-178`) |
| RNG = two static u32 streams | `Utility/Rng.cs:10-11` |
| RNG only resets when the save slot changes | `SceneSetup.cs:46-51`; `StartMatch` always sets slot 0 (`MatchStart.cs:22`), so every match after the first starts where the last one left off |
| No wall clock, `System.Random` or threads in match code | MphRead's `Stopwatch`/`Task.Run` uses are timing logs, music fades, sfx and movie loading, and campaign door loads |
| The Roam camera switches off main-player logic | `IsMainPlayer => this == Main && CameraMode == Player` (`PlayerEntity.cs:211`); `IsVisible`/`IsAudible` return true outside Player mode (`EntityBase.cs:426-443`) |
| LAN snapshot: 10-byte header + 62 bytes per seat | `Net/Snapshot.cs:22-23` |
| Campaign replays are hard | door transitions load on a worker thread (`RoomEntity.cs:515`) |

## Corrections and findings

1. **The RNG split is cleaner than the plan says.** `Rng2` drives all gameplay: `PlayerAi` (44 calls),
   `PlayerProcess`, `PlayerInput`, camera shake (`PlayerCamera.cs:888`), item spawns, beam spread. `Rng1` is cosmetic
   only: `Formats/Effects.cs`, object effect jitter and idle anims (`ObjectEntity.cs:501, 529`), lightning arcs
   (`BombEntity.cs:588`, `BeamProjectileEntity.cs:840`), sound picks (`PlayerSound.cs`). The header stores both;
   `Rng2` goes in every step's checksum.
2. **MatchSim isn't the app's sim.** The app starts matches with `collectDrawItems: true` at the window size; MatchSim
   uses no draw list at 256x192 (`MatchSim.cs:317`, `MatchStart.cs:16`). Without the draw list, Spire's alt-attack
   rocks never update (`PlayerDraw.cs:45` -> `PlayerCollision.cs:133`) and effects never process
   (`Renderer.cs:3118`). R0 measures this and R1 fixes it.
3. **Beam platforms: none in multiplayer.** Entity census (2026-10-07, every room's entity file, all layers): the 27
   multiplayer arenas have no platform entities at all. The 71 campaign rooms have 245 platforms, 24 of them beam
   spawners, and 6 of those (all in Fault Line) only fire when visible (`NoBeamIfCull`, `PlatformEntity.cs:759`).
   Every other `IsVisible` caller that runs in a match is draw culling inside `GetDrawInfo` (items, jump pads,
   Halfturret, teleporters, doors), plus `ObjectEntity`'s cosmetic effects and sound volume (`IsAudible`). So in
   multiplayer, what's on screen never changes the result. The Echoes arenas weren't in the census (our own entity
   data); a mod could add beam platforms.
4. **What draw still changes in a match:** Spire's rocks update in `Draw` (`PlayerDraw.cs:45`). Effects (and
   `ObjectEntity`'s visible flag, `ObjectEntity.cs:551`) only advance in the draw pass. And `IsMainPlayer`
   depends on the camera mode. R1 moves the first two into the update step and replaces Roam with a render-only view.
5. **The weapon wheel isn't a risk.** `SetWheelPointer` multiplies by `Scene.Size` (`CampaignHost.cs:221-236`) and
   `UpdateWeaponSelect` divides by it again (`PlayerHud.cs:693-694`), so the window size cancels out in landscape.
6. **The viewed player's HUD can't reuse MphRead's.** `UpdateHud` runs for Main only and also does gameplay (weapon
   selection, `PlayerHud.cs:500`). Kill messages and most sounds are Main-only too (`MULTIPLAYER.md` section 4). It
   needs a draw-only HUD that reads the viewed player's fields.
7. **First-person view from another seat is extra work.** Other seats are drawn as third-person bodies, and the
   first-person gun animation runs for Main only. Chase cam first.
8. **LAN host input isn't a `CampaignInput`.** The remote seat takes a `NetInput`: absolute yaw/pitch, aim epoch and
   a weapon pick (`MatchNet.cs:146-149`). It only arrives on steps where a packet came in (`TryTakeInput`), and a
   silent peer is dropped on wall-clock time (`PeerSilentMs`, `MatchNet.cs:133`). Record, per step, which NetInput
   was applied (or none), plus a seat-drop event.
9. **Where devices can differ.** Basic float math (+ - * / sqrt) is exact and identical everywhere. The risks are
   (a) trig and friends (`MathF.Sin/Cos/Tan/Atan2/Acos/Asin/Pow/Exp/Log`), which call each platform's own math
   library (Windows vs Android, and Android versions differ too); ~170 call sites in sim code, plus 43 OpenTK
   rotation helpers that call `MathF` inside OpenTK; and (b) the compilers: CoreCLR on Windows, Mono (profiled AOT +
   JIT) on Android. Float-to-int conversion saturates on both in .NET 9. Whether either compiler fuses a*b+c into one
   instruction has to be tested, not assumed. No SIMD intrinsics in MphRead.
10. **Updates.** A replay re-runs the sim, so it's only exact on a build whose sim behaves the same. Handled by a sim
    version plus golden replays (R2c), not the app version, so UI-only updates keep old replays.
11. **State left over between matches.** Match statics (`GameState`, `PlayerEntity._players`, effect pools) must reset
    the same way whether a match is the first since launch or the fifth. R0 replays both in a fresh process and
    after another match.

## Exact or refused (owner rule, 2026-10-07)

- **Per-step checksum.** Each recorded step stores a 16-bit hash of the gameplay state after it: `Rng2`; each seat's
  position, speed, aim, health, ammo, weapon and flags; scores and `MatchTime`; projectiles, bombs, items,
  Halfturret, octolith. Cost: microseconds per step, ~70 KB per 10 minutes. A drifted sim fails the check within a
  step or two, and every step after.
- **Checked before drawn.** Playback compares the step's hash before rendering it. At the first mismatch it stops and
  says so ("this replay can't be played exactly on this build/device"). Every frame shown was verified, so a wrong
  frame is never on screen.
- **Refused up front** if the header doesn't match: sim version, settings that affect play (the `Bugfixes`, `Features`
  and `Cheats` values via their `Commit()` dictionaries, `Features.cs:45, 151`), mods (`ModHost`), and the arena's
  data hash. File damage: CRC32 over the file. A file cut short (crash, battery) plays up to the cut, marked
  "incomplete", because what's there is still exact.
- **Verify before watching (optional):** run the whole replay headless with no drawing, behind a progress bar, and
  remember the result per replay and build. The PC does a 10-minute match in ~8 s (0.22 ms per step,
  `extract_out/multiplayer/match_sweep.txt`). The Odin isn't measured yet. Also run it right after recording, in
  the background, so your own replays show as verified.
- **No snap-back.** Correcting drift from snapshots would hide it, so replays never use the LAN client's snap-back.
  A desync on the same build is a bug: log it and offer the crash-log share.
- **Old replays after a sim change** are refused by the sim version. Re-running inputs can't survive a sim change.
  The only thing that could is a state recording (every visible entity every step, like the LAN client's snapshots
  but complete): ~10-50x bigger files, and it needs the netcode's N2 work (bombs, Halfturret, octoliths in
  snapshots). Not planned. Keepers get video export (R7).

## Milestones

Effort: S = about one session, M = 2-4 sessions, L = more. These are rough estimates from reading the code.

**R0. Determinism harness (S). BUILT 2026-10-07.** Core `src/MphRecomp.Core/Replay/`: `ReplayState` (the per-step
checksum, plus parts for diagnosis; no allocation), `ReplayFile` (format v1: header, settings, both RNGs, play
settings, steps; Deflate + CRC-32; ~13 KB per 30 s match, so ~260 KB per 10 minutes), `ReplayRecorder` /
`ReplayPlayer` (RNG restore without SceneSetup's save-slot reset, refusal checks, per-step check). Tools
`ReplayTest.cs`: `-replaytest`, `-replayplay <file> [view=] [trace=a-b]`, `-replaysweep [view=]`,
`-drawinvariance [opponents=Spire,Spire,Spire] [record=draw]`. A scripted human plays seat 0 (move, aim, fire, jump,
morph, bombs, alt attack, zoom, weapon cycling, the wheel).
*Results (PC, Windows x64, .NET 9):*
- Record -> replay exact on **275/275** arena x mode (every playable one, the six Echoes arenas included; 30 s
  matches, 3 bots level 2; `extract_out/replay/replay_sweep_nodraw.txt`). Exact in the same process after other
  matches and in a fresh process.
- The checks catch a change: a second of aim nudged a third of the way in fails at that exact step (on foot; Samus's
  morph ball ignores aim, PlayerInput.cs ~1233, so a nudge there rightly changes nothing). The checksum includes each
  player's own camera (the ball camera steers the roll).
- **Drawing changes a match only through Spire's rocks.** Against three Spire bots, Combat Hall diverges at step
  1395 and Data Shrine at step 8392, a Spire seat first. Without drawing, the rocks stay where Spire spawned, so
  headless runs (MatchSim, and later replay verification and fast-forward) get no rock hits near Spire and phantom
  hits at the spawn point. Without Spire alt attacks reaching anyone, all four ways stay identical (Combat Hall,
  Sanctorus and Harvester, 2 minutes each, seat 0 as Spire).
- **Window size, DS framing and third person change nothing,** cosmetics included: recorded drawn, the 256x192,
  1920x1080 DS-framing and 1920x1080 third-person replays are identical to each other in every part, `Rng1` too.
- Effects (cosmetic, `Rng1`) differ between drawing and not from step 8 on, as expected.
- No-draw recordings replayed drawn in third person (`-replaysweep view=third`,
  `extract_out/replay/replay_sweep_third.txt`): **258/275 exact, 17 diverge** (from step 439-1774 of 2324). Every
  sweep match has a Spire bot (MatchSettings.Quick's bot order: Kanden, Spire, Trace), so Spire's rocks are the
  likely cause, but that's not proven per case. R1's acceptance (this sweep exact after the rock fix) settles it.
*Accept:* met for the no-draw sweep. The drawing coupling is R1's.

**R1. Drawing never changes a match (S, after R0). BUILT 2026-10-07.** Two host hooks on `Scene` (new
`MphRead/SceneHostReplay.cs`), both off by default; off = MphRead's own behaviour, unchanged. Upstream edits are
additive and host-guarded (board rule 2, logged):
- `HostDrawFreeSim` (per scene). Every frame, where the draw pass would run, each active player places Spire's rocks
  (`PlayerEntity.UpdateDrawFreeSim` in `PlayerDraw.cs`: Draw's own conditions minus the drawing-only
  `HostHideMainPlayer`), and `Draw` no longer does. Frames without a draw list also age effects (`ProcessEffects`, with
  `GetDrawItems`' own condition), so verification and fast-forward don't pile up stale effects. A drawn frame plays
  exactly as before (proven below), so switching it on in the app doesn't change live play.
- `HostView` (per scene): a render-only camera (position, target, up, field of view) for R4's spectator views. The
  view matrix, camera position, field of view and culling frustum come from it, while the camera mode stays Player,
  so the main player's own camera, `IsMainPlayer` logic and the match are untouched (Roam would switch them off).
  `Renderer.cs` `TransformCamera` / `UpdateCameraPosition` / `UpdateProjection`.
- The replay code turns `HostDrawFreeSim` on (`ReplayRecorder.Started(scene)`, `ReplayPlayer.Start`). Sim version
  1 -> 2, so R0's recordings are refused up front.
- Left as they are (cosmetic, and view-dependent on the DS too): object effects that only spawn while the object is
  visible (`ObjectEntity.cs:551`), effects spawned while drawing (e.g. ice-break bits off a visible body), single
  particles.
- Harness: a fifth view, `view=spectate` (1920x1080, `HostView` chasing the other seats: next seat every 10 s, field
  of view switching every 5 s), `-replaysweep record=draw`, and `drawfree=off` / `drawfree=recordoff`.

*Results (PC, Windows x64, .NET 9; reports `extract_out/replay/*_r1_*.txt`):*
- `-drawinvariance opponents=Spire,Spire,Spire time=180 seed=4`: Combat Hall (Spire alt attack on 1188 steps, 164
  of them with a rock near a player) and Data Shrine (216 / 37): **all five ways identical** (no draw, 256x192,
  1920x1080 DS framing, third person, spectator camera), recorded both ways (`record=draw` too). `Rng1` and the
  cosmetic entities are identical as well now.
- Control, `drawfree=off`: Combat Hall still diverges at step 1395 (as in R0), so the test still sees the coupling.
- `drawfree=recordoff` (recorded drawn without the switch, the way the app plays today; replayed with it, all five
  ways): exact in both arenas. Turning the switch on doesn't change a drawn match.
- `-replaysweep time=30`, every arena x mode (3 bots level 2): **275/275 exact in all four directions**: recorded
  without drawing and replayed without drawing, in third person (R0: 258/275; all 17 fixed, so Spire's rocks were the
  cause), from the spectator camera; and recorded drawn (as the app records) replayed without drawing (as verification
  and fast-forward will). `-replaytest` passes (A/B exact in-process and in a fresh process; the aim nudge caught at
  its step).
*Accept:* met.
- FYI Multiplayer (board): MatchSim can set `host.Scene.HostDrawFreeSim = true` after `StartMatch` to get Spire's rock
  hits right without drawing.

**Playback screen shape: always the viewer's own** (owner, 2026-10-07). In multiplayer, what's on screen never
changes the result (finding 3), so a replay plays at whatever screen shape it's watched on and the match is
identical. Only cosmetic particle randomness can differ: objects' effects draw from `Rng1`, which shifts with what's
in view. R0's `-drawinvariance` proves the rest. The campaign's on-screen rule (Fault Line's 6 beam platforms) stays as
it is today. It isn't part of replays, so there's no decision to make.

**R2. Recorder and playback for local bot matches (S-M).** The file holds: the header; per step, the local input
(digital move in 4 bits, buttons, the exact aim-delta floats, weapon pick, wheel sector) and the 16-bit checksum;
and both RNG values, restored at match start. Playback runs through the same `CampaignHost.Step`. Deflate. ~12 B
per step raw, so a few hundred KB per 10 minutes. Auto-record every match and keep the last 10 (pin to keep more).
There's an off switch. A dev launcher entry plays the newest replay for testing.
*Accept:* Odin record -> Odin playback passes every check to the end; PC the same.

*Built 2026-10-07 (on the Odin 13:21):*
- Core `Replay/ReplayLibrary.cs`: `<data>/replays/` (the last `KeepRecent` = 10, oldest removed on save) and
  `<data>/replays/pinned/` (never removed); names sort by time (`20261007-132225 Battle Combat Hall.mphreplay`);
  `Save` (temp name, then move), `Prune`, `Recent`, `Pinned`, `Newest`, `Pin`, `Unpin`.
- App `Campaign/MatchReplay.cs` (a `CampaignRenderer` partial, so Android and Windows both have it): every local
  match (dev launcher or the vanilla multiplayer menu; not LAN) is recorded when RECOMP SETTINGS "record replays" is on
  (`RecompSettings.RecordReplays`, default on), with the steps list sized for the whole match up front. Saved when the
  match ends, or marked incomplete when the screen goes first (Shutdown; under 10 s isn't kept). `--es replay
  newest|<path>` plays one back through the same `CampaignHost.Step`; the pad is ignored, the pause menu still
  pauses. Each step is checked before it's drawn; the first one that differs stops the replay with "REPLAY STOPPED at
  m:ss" (the screen goes black, nothing wrong is shown). Refusals (version, settings, damaged file) say why. The
  dev status line shows "REPLAY m:ss / m:ss, every step exact" or "recording".
- Hooks (logged, board rule 2): `MatchSession.cs` (5 one-liners), `CampaignRenderer.Shutdown` (1), RecompSettings
  (1 property), RecompMenus (1 row). Dev launcher (Multiplayer): "Play newest replay".
- Results: PC `-replaytest` now also checks the library (12 saves keep the newest 10, pin, newest plays back exact).
  **Odin** (dev APK 13:20:49): a 40 s Battle against 3 bots (seat 0 standing still) saved 2924 steps / 28 KB in 39 ms;
  played back from the dev launcher's path in a fresh process: 2924 of 2924 steps exact, the same scoreboard.
- Not done yet: the owner's own playing (a human's inputs, the gyro), long matches on the Odin, the Windows app run
  by hand, LAN (R6).

**R2b. Exact on every device (M). DONE 2026-10-07 (PC, Odin, Samsung all exact).** Our own pure-C# trig (`Sin`, `Cos`, `Tan`, `Atan2`, `Acos`, `Asin`, `Pow`,
`Exp`, `Log`, built from + - * / sqrt only) replaces the ~170 sim call sites and the 43 OpenTK rotation helpers. The
call sites switch through one redirect where possible, and the results match today's to within float rounding, so
play feels the same. Speed: about the same as the platform's library. Measure on the Odin; a step's trig is well
under 0.1 ms. Then the cross-device test: the same recordings on the PC, the Odin and the Samsung must produce the
same checksums. If any differ, the per-step hash points at the step, and a full state dump there points at the code
(e.g. a compiler fusing a*b+c).
*Accept:* PC <-> Odin <-> Samsung recordings pass every check on the other devices.

*Measured 2026-10-07 (before DetMath):* the Odin's own recording (`20261007-132225 Battle Combat Hall`, 2924 steps,
sim v2) played on the PC: **not exact from step 3**, drawn or not. The PC's step 3 is where the bots start turning and
the intro camera moves (yaw, facing, camera target and field of view: trig), shown by the new state dump.

*Built 2026-10-07 (PC-verified; the device half is pending):*
- `MphRead/Utility/DetMath.cs` (new): `DetMath` (double kernels: Taylor series after Cody-Waite reduction for
  sin/cos/tan, an exact reduction from 1e5 up (2/pi from Machin's formula in BigInteger, computed once), atan by two
  half-angle steps + series, atan2/asin/acos with C99's special cases, exp/log/pow), `DetMathF` (MathF's float API:
  the IEEE-exact members pass through, the rest go to DetMath when `HostDeterministic` is on), `DetMatrix` (OpenTK
  4.9.4's CreateRotationX/Y/Z, CreateFromAxisAngle, Matrix3 ones and Quaternion.ToEulerAngles, copied step for step
  from OpenTK's source with our trig).
- One `global using MathF = MphRead.DetMathF;` in that file sends every `MathF.` in MphRead through it, with no edits
  to the 37 files. OpenTK's helpers run System.MathF inside OpenTK, so their 52 call sites (25 files) now call
  `DetMatrix` (OpenTK's own when the switch is off), plus `EntityBase.Transform`'s ToEulerAngles. Core:
  `CampaignHost.SetWheelPointer` (the wheel's pointer, 3 calls) and `MatchStart` turns the switch on for every match.
  MphRead's own PC build keeps it off: unchanged.
- Accuracy (`-detmathtest`, 200k inputs per range, 28 ranges + special values): **100% correctly rounded** floats
  everywhere, huge arguments included (MathF on Windows: 75-99.99%, asin near 1 the worst), so DetMath equals today's
  MathF on ~99.9% of inputs and is 1 ulp off otherwise. The OpenTK copies are bit-identical to OpenTK over 100k
  inputs each.
- Speed: 30-55 ns a call vs MathF's 10-15 ns (PC, optimized), and **no measurable change per step**: 0.059 ms/step
  both ways without drawing, 0.65-0.85 ms drawn (Battle, 3 arenas, 60 s). The Odin's number is still to measure.
- Sim version 2 -> 3 (`ReplayFile.CurrentSimVersion`); the goldens were re-recorded. The LAN JOIN now carries it
  (`LanSession` protocol 4 -> 5): a host refuses a guest whose game version differs.
- Same goldens, PC, every runtime switch we can flip: exact with JIT optimization on or off, with hardware SIMD off
  (`DOTNET_EnableHWIntrinsic=0`), with AVX/FMA off, with tiered compilation / ReadyToRun / PGO off. Nothing in the sim
  depends on the CPU's vector or FMA instructions on x64.
- Diagnosis: `ReplayState.Dump` (each seat's raw floats as bits, RNGs, a hash per entity type) in the same format on
  the PC (`-replayplay <file> dump=a-b`, `-replayverify ... dump=a-b`) and the app (`--es replaydump a-b` while
  watching; `ReplayVerifyActivity --es dump a-b`), to diff two devices line by line to the first value that differs.
- Device check: `ReplayVerifyActivity` (dev builds only; Core `ReplayVerify`, the same lines as Tools
  `-replayverify`): push `tests/replays/*.mphreplay` to `<external files>/replays/golden/`, then `am start -S -n
  com.mphrecomp.app/com.mphrecomp.app.ReplayVerifyActivity`, read `logcat -s MPHReplay`. Plays every golden without
  drawing, as fast as the sim runs.
- Mono, checked from source (14:15; dotnet/runtime release/9.0, Android SDK 35.0.105 targets): the Release APK is
  profiled AOT without LLVM and without the interpreter (that's Debug only); methods outside the profile are JIT'd by
  the same mini backend. Mini's default optimizations include FLOAT32 (float math stays single precision, AOT and JIT),
  and its ARM64 backend never emits a fused multiply-add. Neither suspected Mono difference applies.
- Code the alias doesn't cover, audited: no System.MathF or double trig left in MphRead's sim (one Debug.Assert in
  RepackModel); no OpenTK quaternion / slerp / angle helpers in the sim (ExtractRotation is sqrt only). Trig in Core and
  App is drawing, input before recording (gyro), import, or Tools-side tuning. Watch item: the Echoes arena importer
  (EchoesArena spawn facings, ArenaCannons.Find) uses System.MathF on each device; an import that differed from the PC's
  in a last bit would make Echoes replays fail there (refused, not wrong). The Odin's 10-07 import matched the PC's.
- Windows app (SecondHunt.Desktop, 14:04 build, throwaway data folder unpacked from AMHE1): a bot match recorded in the
  app (Battle, Combat Hall, 30 s, 2324 steps) is exact in Tools `-replayverify` and in the app (`--replay newest`); the
  Spire x3 golden (Tools-recorded with input, rev-0 extraction) is exact drawn in the app, 5922/5922. The app's own
  recording has no player input: `--script` keys reach the menus only (in-game keys come from the real keyboard while
  the mouse is captured), and a multiplayer match spawns you on FIRE.
- One command per direction: `tests/replays/device_check.sh verify [dump=a-b]` (PC -> device) and `record`
  (device -> PC); see `tests/replays/README.md`. It doesn't install (board rules) and won't start over a test in progress.
- Arena files fingerprint (14:45, owner GO): a replay (file format 2) records CRC-32 + length of the arena's entity,
  collision and bot node files, the node file picked per mode as SceneSetup.LoadNodeData does (Capture / Outer Reach
  overrides) (Core `Replay/ReplayData.cs`). `ReplayPlayer.CheckAfterStart` refuses, before the first step and naming
  the file, when this device's differ ("this arena's collision file on this device differs from the recording's"):
  a different Echoes import or extraction is refused up front instead of failing part-way. Format 1 files load and
  play as before (nothing to compare). Checked: MPH's 55 arena files are identical in rev 0 and rev 1 (no false
  refusals across ROM versions); goldens re-recorded with it (every step byte-identical, 9/9 exact, -replayverify 9/9);
  `-replaytest` has a "changed collision file is refused" check (pass); a Capture match on Head Shot fingerprints
  mp6_CTF_node.bin; the Windows app writes and accepts it (1724/1724). Models, textures and sounds don't count.
- **Samsung (SM-S918U, Android 16, Arm64 .NET 9.0.18, the 18:57 APK) 2026-10-07 19:01-19:06: exact both ways.**
  PC -> Samsung `verify`: 8/9 goldens exact at 0.43-0.86 ms/step (PC 0.20-0.25); the 9th (`battle-pipeline-samus`)
  stopped in MatchSettings.Validate because the phone has no Echoes import (that's the device, not the sim). Samsung
  -> PC `record`: a 40 s bot match recorded on the phone (Battle, Combat Hall, 3 bots) is exact on the PC, 2924/2924.
  First cross-device result with DetMath: the gap the Odin's v2 recording showed (wrong on the PC from step 3) is gone.
  Fixes from the run: an arena that isn't on the device now refuses up front ("the arena X isn't on this device
  (import it first)", `ReplayPlayer.CheckBeforeStart`; the 18:57 APK still shows the old error); device_check.sh read
  the Samsung's two mWakefulness lines as one (the foreground gate never fired there) and its battery history, and
  gained `SLEEP=0` (home only, for a phone in use).
- **Odin (Odin2 Portal, Android 13, Arm64 .NET 9.0.18, the 18:42 APK) 2026-10-07 19:21-19:23: exact both ways.**
  PC -> Odin `verify`: 9/9 exact at 0.36-0.70 ms/step, the Echoes golden included (the Odin's Echoes import has the
  PC's files). Odin -> PC `record`: 2924/2924 exact (39 KB: held in hand, so gyro aim was recorded). The first try
  exited the app: a folder `adb shell mkdir` makes is shell's (rwxrws---) and Android 13 won't list it for the app;
  device_check.sh now chmods it, and ReplayVerifyActivity prints a line instead of dying. Installed with both fixes
  19:25, verify 9/9 again.
- **R2b done (board MR6): PC <-> Odin <-> Samsung all exact.** DetMath costs nothing visible: 0.36-0.86 ms per sim
  step on both phones (a frame is 16.7 ms).

**R2c. Sim version + golden replays (S). BUILT 2026-10-07.** A handful of recorded matches checked into the repo (inputs and settings
only, no game data, so they're ship-safe). A Tools check replays them on every build. If one fails, the sim changed
and `SimVersion` must be bumped. UI-only updates keep old replays working. It doubles as a gameplay regression test.

*Built 2026-10-07:*
- `tests/replays/` (README there): 9 goldens, ~24 KB each (`battle-combat-hall-samus`, `survival-ice-hive-noxus`,
  `capture-elder-passage-kanden`, `bounty-fault-line-weavel`, `nodes-sanctorus-trace`, `defender-harvester-sylux`,
  `primehunter-head-shot-spire`, `battle-combat-hall-spire-x3` (90 s, Spire's rocks change the match from step 1235),
  `battle-pipeline-samus` (Echoes)). Every mode family, seat 0 (the scripted human) as each of the seven hunters,
  recorded drawn at 256x192 as the app records. Specs: `Goldens` in `ReplayTest.cs`.
- Each golden starts like the first match after launch (`Menu.PreviousSaveSlot = -1`: SceneSetup resets both RNG
  streams), so re-recording from the spec gives the same file byte for byte (checked). Without that, the RNG carried
  over from the previous match in the process and a re-record came out different.
- `-replaygolden [view=] [only=]`: plays each, fails on the first step that differs, exit code 1, and says what to do.
  `-replaygolden record`: re-records them, refused while a golden of the current sim version still fails (so a logic
  change can't slip in without the version bump); `force` overrides; `seed=`/`time=`... try out a spec. The record
  report says whether a golden exercises Spire's rocks (played back without `HostDrawFreeSim`).
- **On every build: decided as an opt-in MSBuild target + the check before installs.** The goldens need the game
  files, so the check can't be a plain build step everywhere, and running it after every Tools build would hold
  `MphRead.Tools.dll` while other sessions build. So: `dotnet build src/MphRead.Tools/MphRead.Tools.csproj -c <config>
  -p:ReplayGolden=true` runs it after the build (~10 s) and fails the build on a mismatch (skipped without the desktop
  extraction), and any session whose change touches game logic runs it before installing. Owner's call whether that
  becomes a board rule.
- Results: 9/9 exact without drawing, in third person and in a fresh process; a real divergence (the Spire golden with
  `drawfree=off`) fails with exit code 1 at step 1235 and blocks `record`. The R2b change below failed all 9 (from
  steps 17-310), the version was bumped 2 -> 3 and they were re-recorded: the workflow did its job on its first use.
- Device side (R2b): the app's `ReplayVerifyActivity` plays the goldens on a device the same way (below).

**R3. Playback UI and speed (M, mostly UI; needs P-MP).** The REPLAYS screen under multiplayer. Pause, and
0.25/0.5/1/2/4x by changing steps per frame. Draw only the last step of each frame (safe after R1). Mute sound
effects at any speed other than 1x. Verified/incomplete marks, the auto-record switch, rename, delete, pin, and
share the file (Android share sheet).

R3 status (2026-10-08, Replay thread [da90a3]):
- **Playback controls BUILT** (App `Campaign/MatchReplay.cs`; hooks logged on the board 00:53-00:58): A or a tap =
  pause / play; L1 / R1 or the D-pad's left / right = 1/4x, 1/2x, 1x, 2x, 4x (steps per frame; a frame may run up
  to 4 steps per 1x to catch up); away from 1x the sound effects are muted (music plays on); paused, music and
  sound pause. No touch controls in a replay. The status line reads `REPLAY 1:23 / 3:45  2x` (or `PAUSED`) plus the
  control hint. Each frame draws once after its last step already; whether skipping the draw-list build on the
  steps before it is needed is measured on the Odin at 4x first.
- **The list's data BUILT** (Core `Replay/ReplayLibrary.cs`): `ReplayLibrary.List(dataDir)` = every replay, newest
  first, pinned and recent together, from the files' headers (no steps read): `ReplayInfo` (Path, Pinned, Recorded
  time, Title (the file name after its time), Mode, Arena (in-game name), Players (hunter, bot, nickname, team),
  LocalSeat (whose view), Seconds, Complete, PlaysHere / Refused (the reason, the same checks playback makes before
  the first step: game logic version, bot vision, arena present, arena files the same; a damaged file is listed
  with Refused set and Header null)). `Rename(path, title)` keeps the time, `Delete(path)`, `Pin` / `Unpin` (R2),
  `Newest`. `<data>` = the folder of `recomp_settings.json`. Tools `-replaytest` checks them.
- **Launch contract BUILT**: `FrontendRequestKind.PlayReplay` (Arg = the path) -> the Android and Windows shells
  play it on the campaign screen like START GAME (`--es replay <path>`) and the menus come back at
  `FrontendSession.ReturnPage()`. A replay never touches `MatchResults.Last` (no HUNTER LICENSE counting): its end
  shows the scoreboard panel; A / B / Start / a tap goes back.
- **The REPLAYS list page BUILT 2026-10-08 ~01:37** (Replay mode (cont.) [f9751f]; owner ~01:10: this thread builds
  it). Core `Frontend/RecompMenus.Replays.cs` (a partial of RecompMenus: IMPORT GAMES' rows-page machinery, the
  Controls page's frame and SENSITIVITY rows from the ROM); hooks (board cross-session row ~01:25-01:33): RecompMenus.cs
  (`partial`, `RowsPage.Entered`, `InstallReplays`, AllRowPages), MultiplayerMenu.cs (`OpenReplays`, call 93),
  FrontendSession.cs (2 wiring lines). The REPLAYS button opens "REPLAYS n/m": row 1 = the "record replays" switch
  (the same setting as RECOMP SETTINGS'), then one row a replay, newest first, six a page (L / R or the corner icons
  page): label = the arena (or a renamed title, two lines of ten letters), value = the mode (BATTLE, BOUNTY, ...,
  PRIME) or CAN'T, the band = "mode, arena, length. hunters (you). date. ends early / kept / can't play here. x:
  delete."; the orange X box (or X) deletes after the game's ask box; DELETE ALL (footer right) deletes every replay
  not kept. A / a tap on a replay opens "REPLAY" (its title as the header): WATCH (PLAY, or CAN'T + the reason in the
  game's box), KEEP yes / no (pinned/; un-keeping one with ten newer warns it goes now), RENAME (WORK IN PROGRESS until
  the name keyboard takes a caller: `RecompMenus.EditText`), DELETE. WATCH sets `PendingReturn` to the list, so the
  menus come back to REPLAYS. The folder is read as the page comes in (never at startup: the pause menu loads these
  menus too). PC-checked (-fecanvas 74 with a scratch FE_SETTINGS + 9 replays incl. a damaged one and a pinned one,
  rev 0 + rev 1): list, paging, focus after deletes, watch -> PlayReplay request, keep, X + ask box, DELETE ALL, switch.
- **Share: NOT built.** A binary file needs a content:// URI on Android (a small read-only ContentProvider for
  `<data>/replays`, a manifest addition; the crash log shares as text, so none exists yet). Windows: show the file in
  Explorer. To do after the list page exists.
- **On the Odin 2026-10-08 01:05** (device test): pause, every speed and back, 5922/5922 exact to the end; 4x = 240
  steps/s at 120 Hz with sim 2-3 ms + draw 5-6 ms a frame (no frame over 17 ms), so skipping the draw lists of a frame's
  earlier steps isn't needed there (revisit for slower phones at 60 Hz: 4 steps a frame). Fixed on the way: replays of
  imported Echoes arenas were refused (the check ran before Paths were set; now at match start).

**P-MP. Vanilla multiplayer menu (its own project, M-L; Start menu UI thread + Multiplayer).** Replace today's lobby
screens (`MatchLobby.cs`, Android `MatchLobbyActivity`, Windows `LobbyScreen`) with the game's own multiplayer pages
from the ROM's menu data, the way the start/file/options pages were rebuilt (`MenuEngine`, `FrontendSession`).
REPLAYS is a button built from the game's own pieces, like MOVIES on the file screen (`MenuLayout.cs:123`). Can run
alongside R0-R2.

**R4. Spectator cameras (M-L).** A free cam, a chase cam on any seat, and cycling between seats. Render-side room
visibility comes from that camera, and bodies are shown or hidden to suit the view. A draw-only HUD strip shows name,
health, weapon and score, and the audio listener follows the view. First-person POV from other seats comes later
(finding 7).

**R5. Seeking (M; L with savestates).** Seek = restart and run headless with no drawing, behind a progress bar
(cost as in "verify before watching"). If that's too slow on the Odin: savestates every N seconds. That's the same job
as netcode prediction tier 2 ("a generated copier of ~200 player fields", `MULTIPLAYER.md` section 4), so build it
once for both.

**R6. LAN replays (M).** Host: as in R2, plus finding 8, so host replays get full checks. Client: it doesn't run the
whole sim (the host does), so it can't re-run the match. A client recording is a view-only replay of the host's
snapshots: exact for what snapshots carry, with bombs, Halfturret and octoliths missing until the netcode sends them.
The owner's rule says exact or refused, so client recordings are off.
*Guest copy (owner asked 2026-10-07; S on top of R6 host + R2b + R3):* the host sends its replay to the guest over the
LAN session at match end, while the results screen is up. A few hundred KB goes in pieces over the existing UDP
socket: `LanSession.cs` has no reliable channel, so each piece is acked and resent if lost. It lands in the guest's
list as the host's recording and is checked like any other replay. Sent only if the guest has auto-record on. If the
guest left early or the connection dropped, the host can send it later with the Share button. Needs R2b, since the
host and guest are often different devices (until then the guest's copy is refused, never shown wrong). The replay
re-runs with the host as the main player (exactness), so the guest's own seat needs R4: chase cam first, first-person
later (finding 7). Also: add the sim version to the LAN handshake, which today checks only the ROM revision
(`LanSession.cs:157`), so builds whose gameplay differs can't play together.
*R6 BUILT 2026-10-09 (Replays track, O10; PC-verified, device test pending):* LAN and online matches are recorded by the
HOST, exact: its own input per step, plus every remote seat's `NetInput` it applies and every seat it drops
(`CampaignHost.RemoteInputTap` / `SeatDropTap`, added by Netcode O5), kept as format 19 `NetEvents` and re-applied before
the same step on playback (`ReplayPlayer.BeforeStep`). Guests don't record; at match end the host sends each guest that
has record replays on its file over the match's side channel (`IMatchNet.SendSide`, `ReplayLink.cs`: 1100-byte chunks,
acked and resent, up to 20 s), and the guest saves it as the host's recording with `OwnSeat` = its seat, so its list
shows its own result and the playback camera starts on its seat. Every seat that runs this build sends its identity
(friend code, NAME#tag, license numbers) over the same channel as the match starts, so the replay names everyone.
An online seat that drops and joins again is kept too (`SeatRejoinTap` -> a `Rejoin` event; playback calls
`CampaignHost.ReconnectSeat` at that step).
REPLAYS: rows name each opponent over their icon; the details page shows NAME#tag, and a seat with a friend code opens
its HUNTER LICENSE as it was (mockup V_C: `RecompMenus.ReplayLicense.cs`) with HEAD TO HEAD, SEE CURRENT and ADD FRIEND /
ADD RIVAL. Checks: `-replaynettest`, `-replaymock licensecheck`.

**R7. Video export (M desktop, L Android).** Use a fixed clock (one step per output frame), an offline audio mix from
`GameAudio`, and the OS encoders (Media Foundation on Windows, MediaCodec + MediaMuxer on Android), so no codec
library ships (no ffmpeg). Until then: OBS, Game Bar or Android's screen recorder.

**Out of scope:** campaign replays.

## Owner decisions (2026-10-07)

1. **Screen shape:** playback is always the viewer's own screen shape; there's nothing to pick. (An earlier draft asked
   which shape gameplay should use for "on screen" checks. Withdrawn: multiplayer has none that affect results.)
2. **Devices:** exact on the recording device; every measure to get exact on all others without costing
   performance (R2b).
3. **Drift:** exact or refused, never quietly wrong (section above).
4. **Recording:** automatic, keep the last 10, with a switch to turn it off.
5. **Menus:** a new entry under multiplayer like MOVIES, in the vanilla multiplayer menu (P-MP, its own project).

## Order

R0 -> R1 -> R2 -> R2c -> R2b, with P-MP alongside; then R3. R4 next. R5-R7 whenever they're wanted.
