# Vanilla accuracy: MphRead TODOs and unintended differences

**Owner GO 2026-09-30.**

## Scope (owner's rule)

Fix or address every MphRead TODO and every UNINTENDED difference from vanilla MPH. Leave alone:
- our intentional changes (Prime-style controls, gyro, widescreen/framing, HD models, mods, our netcode), and
- MphRead's own modernization and QoL. The big one: **the game logic stays at 60 Hz** (vanilla runs it at 30 Hz).
  The work here is making the 60 Hz version behave like the 30 Hz original, not going back to 30 Hz.

We are not switching to the LLE/recomp approach. The mstan recomp stays a run-and-observe oracle only (never copy its
generated code).

## Inventory (2026-09-30, upstream MphRead in this tree)

1,438 comment lines with a TODO (`todo`, `todo?`, `sktodo`, `todo-ai`). Script: scratchpad `acc/bucket.py` of the
accuracy session (to be moved into Tools as `-accuracy todos`).

| Bucket | Lines | Gameplay effect? | Action |
|---|---|---|---|
| A. 30 -> 60 Hz conversions ("FPS stuff") | 826 | Mostly exact; a few dozen are not | Review the risky patterns (A1-A5), fix the slips |
| B. Float instead of DS fixed-point | (601 `Fixed.ToFloat` sites; 3 TODOs) | Tiny per step; edge cases | Fix only where the oracle comparison shows a visible difference |
| C. Vanilla behaviour not ported | ~45 | Yes | Port, verified against ROM code + oracle |
| D. Vanilla BUGS that MphRead notes but keeps (`todo: bugfix`) | ~15 | They ARE vanilla | Keep as is (vanilla = accurate) |
| E. Wi-Fi-only branches | 21 | Vanilla multi-console rules | Port as networked-match rules, with the Multiplayer session |
| F. No gameplay effect: names (173), code reuse/allocation, editor/repack/viewer, debug drawing, First Hunt demo, "(document)" notes | ~500 | No | No action. Renaming would churn files other sessions edit |

### A. Frame-rate conversion (the biggest bucket)

Vanilla ticks the game 30 times a second; MphRead ticks 60 and hand-converted each spot. Classified by pattern:

| Pattern | Count | Exact? |
|---|---|---|
| Frame timers doubled (`* 2`) | 473 | Yes |
| Linear steps halved (`/ 2` on an add) | 158 | Yes for constant steps |
| Every-other-frame gates (`FrameCount % 2`) | 38 | Yes (runs on the original's ticks) |
| Random durations doubled | 26 | Yes |
| Comment-only / other / "FPS stuff?" | 129 | Needs a look, one by one |
| Per-frame multiplicative decay | 2 | **No** |

Known or likely slips (fix list A1-A5; each checked against the oracle before and after):
- **A1. Player gravity integration -- FIXED 2026-09-30, measured.** The emulator shows vanilla's order per 30 Hz tick:
  `v += g; y += v`, with no gravity on the jump tick (she is still Standing). MphRead's halves (`v += g/2; y += v/2` per
  60 Hz frame) fell `g/4` behind per tick, and its jump frame's second half already had gravity: apex 2.466 vs vanilla
  2.541 (3% low; my pre-measurement math had the sign wrong). Fix in `PlayerInput.cs`: the position step adds
  `gravityStep / 4` (= g/8 per frame) and `_gravityHold` keeps the jump frame's gravity for the next frame. Now
  identical to vanilla on all 32 ticks of the arc (`-accuracy jump`). Still to measure the same way: falling off a
  ledge, the morph ball (AltAir/AltGroundGravity), jump pads, bomb jumps, Space Jump double jumps.
- **A2. Strafe/walk camera tilt -- FIXED 2026-10-01, measured.** Vanilla per tick: `tilt = (tilt + Field114) * 0.9`
  (Field114 = 6 deg for Samus; holding a strafe leans the camera toward 54 deg). MphRead per frame `+ Field114 / 2` then
  `* 0.9` leaned half as far (27 deg) and settled twice as fast. Now `+ Field114 * 0.9 / (0.9 + sqrt 0.9)` and `* sqrt 0.9`
  per frame (`_tiltAdd`, `_tiltDecay`): equal to vanilla at every tick end (`-accuracy strafe` vs `routes/probe_strafe.lua`).
- **A3. Morph ball camera follow -- FIXED 2026-10-01, measured.** `UpdateCameraThird1/2` moved `Field84` (0.2) of the
  way to the target every 60 Hz frame; vanilla does that per tick. Emulator (`routes/probe_morph.lua`, morph + roll off
  the gunship) vs `-accuracy morph`: camera-height gap kept per tick vanilla 0.80-0.85, MphRead 0.64-0.68, and MphRead
  trailed the rolling ball closer (3.3-3.6 vs 3.8-4.3 u). Now `1 - sqrt(1 - Field84)` per frame: 0.80-0.82, 3.8-4.3 u.
- **A4. Per-frame random chances** in bot AI (`PlayerAi.cs` 3437, 3486, "FPS stuff if this is called repeatedly"):
  if these run every frame (the author wasn't sure), a 1-in-N roll per 60 Hz frame happens twice as often as vanilla.
  **FIXED 2026-10-11, with the bot aim turn rate:** the rolls sit in per-frame
  `Func2_*` behaviours (dodge jumps x2, Sylux bombs) and `UpdateAggro` (spotting a cloaked player); they now roll on tick
  frames only. Same cause, bigger effect: the aim step `_aimValues` (5/15/20 deg per tick by level, ROM dword_214C750)
  was applied every 60 Hz frame, so bots turned 10/30/40 deg per tick; now half per frame (snap capped too). And levels
  2-3 led moving targets 2x (`vec /= finalSpeed` divided a per-tick lead by a per-frame beam speed). All behind
  `PlayerEntity.HostBotTickPace` (replay format 30, Settings.BotTickPace; older replays play with it off). PC check
  `-botpacetest`: turn per tick exactly 5/15/20; 1-star bot damage on a strafing player 145 -> 59 /min (8 arenas, 240 s).
  **Second pass (same day, same flag):** random state gates (func3 210 in Data4) roll once per tick; the hits a tick's
  state checks read (`_slotHits`/`_slotDamage`) are kept until a tick frame; the visibility pair / node-queue checks run
  per tick; the wall-stuck detector's `Speed.Y` threshold is vanilla's 0.0625 (Speed is per tick); two sites added
  `AltColYPos` (fx32) raw. And **Spire's Dialanche rocks** (`PlayerCollision.CheckAltAttackHit1`, any Spire) ignore damage
  invulnerability, so MphRead's every-frame check did 8 per 60 Hz frame = 2x vanilla; now once per tick. BizHawk A/B
  (Battle, Combat Hall, human Samus strafing, 1-star SpiBot, 4 min, `tools: handoffs/bot-difficulty-vs-vanilla_2026-10-11_2009-files`):
  vanilla 418 dmg/min (201 rock hits of 8, ~10 beam shots/min); ours before 764 (pace on) / 534 (off), after ~340.
  (`-botpacetest` needs `Scene.HostDrawFreeSim` on, as real matches have, or Spire's rocks are never placed.)
- **A7. Walking top speed -- FIXED 2026-10-01 (top speed exact; acceleration/stopping slightly off).** Vanilla per 30
  Hz tick: `speed *= factor; position += speed; (collision); speed += traction`, capped (Samus: traction 450, walk factor
  3604, air 3481, stand 2785, cap 983, all /4096). MphRead added the full per-tick traction every 60 Hz frame and used
  `(1 + factor) / 2` per frame: at full speed she covered 0.226 u/tick on the ground and 0.222 in the air vs vanilla 0.211 /
  0.204 (**~7% / ~9% fast**). Fix (`PlayerInput.cs`, `_walkFriction`): friction `sqrt(factor)` per frame and a position step
  of `speed * sqrt(factor) / 2`, traction unchanged -> full speed covers vanilla's distance per tick exactly
  (`-accuracy walk`: 0.204 per tick in the air, tick for tick). Left over: she reaches top speed in 2 frames vs vanilla's
  3 ticks (a one-time ~0.08 u lead) and stops ~9% shorter (~0.045 u from full speed).
  Tried and rejected: halving the traction per frame, or running the walk as a 30 Hz tick (exact on open ground), both
  stick her at the V-shaped crease in Stronghold Void A's corridor (`-accuracy ramp`, UNIT1/UNIT4_TP1 door #2 -> #4), and
  so does a true once-per-tick move + collision with vanilla's collision factors -- MphRead's collision (author's notes:
  "not accurate", "stickiness") only gets her out with the whole traction every frame. Whether vanilla climbs that crease
  walking straight is still unmeasured (needs the real game there). Morph ball and jump pads: not yet measured.
- **A8. Morph ball rolling -- FIXED 2026-10-03, measured (owner queue #4: "really fast and unruly").** Flat-ground
  route `routes/probe_roll.lua` (landing site floor, x -25.35 -> -17.8, y 0.5) vs `-accuracy roll <roll.csv>`. Vanilla per
  30 Hz tick: `speed = cap(speed + traction) * factor; position += speed` (Samus: traction 139, ground factor 3952, speed
  cap = AltMinHSpeed 1310, all /4096): from rest 0.0327, 0.0642, 0.0945 ... top speed 0.3084 u/tick at tick 11; coasting
  `* 0.9648` per tick; a wall stops it dead. MphRead added the whole traction every 60 Hz frame: 0.0661, 0.1299, 0.1914 ...,
  top speed at tick 4 (2x the acceleration, ~0.85 u ahead once at speed) and 0.3142 u/tick at the top (+1.9%). Fix
  (`PlayerInput.cs`, rolling alt forms only = Samus, Kanden, Spire, Noxus; `AltFormStrafe == 0`; not on jump pads): the
  walking scheme (sqrt friction per frame, position step `speed * sqrt(f) / 2`) plus the traction weighted
  `s / (1 + s)` per frame (s = sqrt f), which lands on vanilla's speed at every tick end. Now: 0.0327, 0.0643, 0.0948 ...,
  top speed at tick 11, 0.3085 u/tick at the top. Left over: ~0.10 u behind once at speed (sub-tick position steps) and
  coasting ~1.5% long (it starts from the per-frame top speed, 0.3142 = cap * sqrt f): stops 0.13 u further.
  The analog stick is read like the DS d-pad (`CampaignHost.Step`: 8 directions, full traction past 0.35 per axis), the
  same digital input vanilla gets, so the values (not the reading) were the difference.
  Checked, unchanged by the fix: the ball stops at the Stronghold Void crease (UNIT1_TP1 corridor, z -7.0, `-accuracy
  ramp ball`) with old and new code alike (vanilla there still unmeasured). Not measured: boost (its impulse code is
  untouched; the friction change makes a boost travel ~1.8% less than before, toward vanilla's per-tick formula), the
  strafing alt forms (Trace, Sylux, Weavel: unchanged), the ball on jump pads (unchanged).
- **A9. Walking up steep slopes -- FIXED 2026-10-03, measured (owner queue #23: "climbing slopes feels slow").** Three
  inclines on the landing site (UNIT2_LAND), walked from rest: `routes/probe_slope.lua` (the 19.3 deg slope, x -17.2 ..
  -7.2), `probe_ramp26.lua` (26.6 deg ramp, x -9.5 .. -3.5, z -22 .. -18) and `probe_ramp45.lua` (45 deg ramp off the y 13
  ledge, x -4.5 .. -6.5); the ramps are reached by position pokes (driver.lua `pokes`). Vanilla horizontal speed per tick:
  flat 0.2110, 19.3 deg 0.2029 (96%), 26.6 deg 0.1547 (73%), 45 deg 0.0673 (32%). Vanilla per tick = MphRead's ported
  collision response done once: position += speed, push out of the floor along its normal (the whole depth, also in Y),
  speed projected onto the floor and scaled by `1 + div` (uphill: `1 - sin(angle)`), then traction + cap; a model of
  that reproduces all three to <1%. MphRead scales the Y part of the push-out by 0.25 on floors with normal Y < 0.9
  (> 25.8 deg; `PlayerCollision.cs` "hack: mimic the collision response for 1 frame at 30 FPS ... compensate for halved
  gravity so we can't go up steeper slopes"), so on those she stays embedded and the full sideways push keeps moving
  her back downhill: 26.6 deg 89.7%, 45 deg 59.5% of vanilla (model: 35 deg 75%, 55 deg 47%); the 19.3 deg slope (factor
  1 already) was 100.5%. Fix: walkable floor faces (normal Y > 0.5, the standing test; biped only) keep the whole push-out:
  26.6 deg 106.1%, 45 deg 101.5%, 19.3 deg and flat unchanged, the flat top after the 26.6 deg ramp 82.6% -> 100.1%.
  Left over: +1..6% fast on 26-55 deg (largest just past 26 deg) because walking adds its whole traction every 60 Hz frame
  (A7's crease trade-off; weighting it on steep floors halves the speed instead, since the slope slowdown also runs per
  frame). Unchanged (unmeasured): edges, faces steeper than 60 deg, the morph balls (they keep the 0.25), the ceiling x4.
- **A5. Un-doubled comparisons:** a 60 Hz frame count compared against a 30 Hz constant without `* 2`. Sweep the 129
  unclear lines for this shape. (Checked and fine: `PlayerAi.cs` 3812 skips the `* 2` because vanilla divides
  `BombRefillTime` by 2 there, so the two cancel.)

Most player "FPS stuff?" lines on speeds (jump speed, speed caps) are actually fine: the player's `Speed` stays in DS
per-frame units and is halved where it is applied.

### C. Vanilla behaviour not ported (gameplay or progression)

- Player collision: lateral wall sliding "not accurate" (`PlayerCollision.cs` 817); wall-climb "stickiness"
  (`PlayerCollision.cs` 902).
- Strafe tilt not updated on some input paths (`PlayerInput.cs` 1280/1313/1330/1339).
- Progression/stats: update stats (`PlayerEntity.cs` 2011), another kills stat (2213), unlock hunter for multiplayer
  (2060), unlock the planet's multiplayer arena on first visit (`RoomEntity.cs` 55), visited connectors for the map
  (`RoomEntity.cs` 328), game timer / boss records (`GameState.cs` 519), artifact flags (`SceneSetup.cs` 14),
  multiplayer saving (`Menu.cs` 610), license stats (11 lines).
- HUD: radar lights (`PlayerHud.cs` 611/657), scan box/icon aspect (`PlayerScan.cs` 430).
- Camera sequences: clear on room change (`CamSeqEntity.cs` 23).
- Visibility: entity visibility beyond active room parts (`PlayerDraw.cs` 46).
- Process conditions: "and some global is not set" / "or forced" (`PlayerProcess.cs` 190/207); `MP1P` markers (6).
- Effects: frame-time scaling for particle speed/accel (`Renderer.cs` 2651), "frame time hack" (2423),
  beam "frame time stuff" (`BeamProjectileEntity.cs` 929), "not sure dividing by 4 is correct" (1269).
- Rumble (2): vanilla supports the DS Rumble Pak; map to controller/device vibration.
- **Measured 2026-09-30 (emulator vs MphRead):** landing on the slightly sloped landing-site floor, vanilla kills her
  horizontal speed (0.173 -> 0.033 on touchdown, small bounce) while MphRead turns the fall into a slide and ends 0.28 u
  further (collision response, cf. `PlayerCollision.cs` 817). Riding the landed, bobbing gunship, vanilla Samus slowly
  slides (~0.46 u in 10 s) while MphRead keeps her still. The bob itself matches (7.05-7.22 vs 7.10-7.26, ~160 frames).
- **Logbook off-by-one -- FIXED 2026-10-01** (reported by the UI session): new-game setup marked table positions 0-6,
  26, 28 instead of scan ids 1-7, 27, 29; counting read positions instead of record numbers (the table skips L030/L031).
  `StorySave.CheckLogbook(StringTableEntry)`; `-accuracy logbook` 4/4 (fresh game EQUIPMENT 9/27 = 33%, as on a DS).
- **Jump pad steering, measured 2026-10-07 (BizHawk, Echoes arenas thread; details in
  `docs/MP2_MULTIPLAYER_IMPORT.md` section 10) -- LEFT AS IS (owner 2026-10-08: "document but leave them for now"):**
  (1) the pad's control lock and its 5-tick minimum end 1 tick (33 ms) later in MphRead than in vanilla
  (`PlayerProcess.ActivateJumpPad`: `_jumpPadControlLock` / `_jumpPadControlLockMin`); (2) holding sideways after the
  lock, MphRead keeps more speed while turning (up to 0.32 vs vanilla 0.30 falling to 0.24; reaches 0.24 at tick 7 vs
  4), and back reverses in slightly different steps (`PlayerInput.cs` stick speed cap + `ProcessMovement` push cut).
  Applies to every pad (MPH and Echoes). Route `tools/accuracy/routes/probe_jumppad.lua`, Tools
  `-echoesarena padvanilla`.
- Not applicable to our controls (record only): touch roll/boost, left-handed mode, touch-alpha.

Each needs the vanilla code path: from the ROM's ARM9/overlay code (a PC-only disassembler, never in the app) plus
oracle observation.

### D. Kept on purpose

- `todo: bugfix` lines are vanilla bugs MphRead reproduces (e.g. Gorea 1A swapped Battlehammer/Shock Coil tracks, bot
  pickup-type load). Vanilla = accurate, so they stay.
- MphRead QoL switches that differ from vanilla (`Features.cs`), kept per the owner's rule: Bugfixes ON
  (`BetterCamSeqNodeRef`, `CorrectBountySfx`, `NoDoubleEnemyDeath`, `NoSlenchRollTimerUnderflow`); Features
  `AllowInvalidTeams`, `DelayedIdleSway`, `MaxPlayerDetail`, `AlternateHunters1P` ON. The owner can flip any of them.

## Plan

1. **Emulator side-by-side test** (owner 2026-09-30: an emulator, not the mstan recomp, is the reference; mstan stays
   an optional cross-check). BizHawk + melonDS core + Lua plays an input recording on the USA 1.1 ROM and dumps the
   player/entity structs; headless MphRead gets the same inputs from the same starting state; compare position,
   speed, health, timers every vanilla tick (every 2nd MphRead frame). MphRead's `_fieldNNN` names and
   `MemoryClasses.cs` offsets are the DS struct offsets, so the mapping is direct. Emulator side: `tools/accuracy/`.
2. **Frame-rate slips A1-A5**, player physics first. Each one: before/after numbers from step 1.
3. **Bucket C**, one item at a time, from ROM code + oracle.
4. **Bosses and enemies** (Gorea 1A/1B/2, Slench, Lesser Ithrak, Quadtroid...): oracle checks on the open lines.
5. **Bucket E** with the Multiplayer session.
6. **Bucket B** only where step 1 shows a visible divergence.
7. Our rebuilt parts (ship, star map, saves, menus, HUD, audio): differences found go to the owning session.

Every MphRead edit: one change at a time, each checked against the original.

## Dispatch: MphRead TODOs handed to the owning threads (owner's plan, 2026-10-03)

The owner's plan: this thread doesn't fix every MphRead TODO itself. It hands them to the thread that owns the area, a
handful at a time. Those threads pick them up when they're active
or after they migrate. When a thread's T items are all DONE or dropped, this thread sends that thread its next batch from
the backlog below. A thread's owner-reported items (#1-34) come first.

Inventory re-run 2026-10-03 (`grep -rni todo src/MphRead --include=*.cs`): 1,451 lines (fps 822, other 332, naming 173,
render 36, wifi 21, debug/viewer 15, ai 13, hud/ui 12, license 11, sfx/music 9, save 5, rumble 2). Line numbers below are
as of that run.

### Sent (batch 1, 2026-10-03 ~18:20)
| T | Thread | TODO lines |
|---|---|---|
| T1 | Campaign | rumble: `PlayerEntity.cs:1924`, `:2526` |
| T2 | Campaign | cutscene leftovers: `CamSeqEntity.cs:23`, `PlayerDraw.cs:61` |
| T3 | Campaign | HUD bar flashes / "radar lights": `PlayerHud.cs:611`, `:646-657` |
| T4 | UI | story-save gaps: `GameState.cs:519`, `:1385`, `:1685`, `PlayerEntity.cs:2011`, `PlayerProcess.cs:1262`, `SceneSetup.cs:14` |
| T5 | UI | visited connectors: `RoomEntity.cs:328` |
| T6 | Music | pause + escape sounds: `PlayerSound.cs:653` |
| T7 | Music | stops/fades at death, ship, match end: `PlayerSound.cs:502`, `GameState.cs:692`, `:390`, `:295` |
| T8 | Music | Gorea 1A Shock Coil shot-sound hack: `24_Gorea1A.cs:801` |
| T9 | Multiplayer | Wi-Fi damage ownership: `PlayerEntity.cs:1723`, `:1749`, `:1790`, `:1801`, `:1818`, `:2324`, `:2334`, `:2374` |
| T10 | Multiplayer | Wi-Fi weapon/item branches: `PlayerInput.cs:1094`, `:1633`, `:1658`, `PlayerProcess.cs:41`, `:213`, `:1018`, `:1038`, `:1142`, `HalfturretEntity.cs:284`, `:290`, `OctolithFlagEntity.cs:75`, `BeamProjectileEntity.cs:451`, `:962` |
| T11 | Multiplayer | license stats: `PlayerEntity.cs:1855`, `:2122`, `:2190`, `:2202`, `:2221`, `PlayerInput.cs:1044`, `PlayerProcess.cs:1063`, `GameState.cs:238`, `:274`, `:418`, `:1195` |
| T12 | Multiplayer | unlocks from adventure: `PlayerEntity.cs:2060`, `:963`, `RoomEntity.cs:55` |
| T13 | Camera FOV | scan box/icon aspect: `PlayerScan.cs:430` |
| T14 | Camera FOV | portal visibility at the wide edges: `RoomEntity.cs:1058` |
| T15 | Camera FOV | gun smoke projection: `PlayerDraw.cs:213` |
Also added as a lead on the owner's UI item #16 (thruster flame without the ship): `EntityBase.cs:462` "hide attached
effects" and `PlatformEntity.cs:831` (effects drawn while the entity is hidden / not visible).

### Backlog for later batches
- **Campaign:** effects on hidden entities (`EntityBase.cs:462`, `PlatformEntity.cs:831`, `is_visible` in
  `18_AlimbicTurret.cs:286`, `FlagBaseEntity.cs:75`, `NodeDefenseEntity.cs:353`, `OctolithFlagEntity.cs:346`; unless #16
  already covered them); texture-animation frames (`Read.cs:581`); Cretaphid beam drawn too thin (`20_CretaphidEye.cs:460`,
  `:468`); Gorea 2 "pretty jerky" (`31_Gorea2.cs:546`); abrupt room transition (`RoomEntity.cs:785`); pause should need a
  press (`PlayerInput.cs:45`); touch-input timer (`PlayerProcess.cs:444`, with #30b).
- **UI:** scan messages (`ObjectEntity.cs:607`, `PlatformEntity.cs:309`); warning message (`BeamProjectileEntity.cs:659`);
  1P dialogs (`GameState.cs:264`); data kept with the save slot (`GameState.cs:1982`); menu options commit
  (`GameState.cs:1542`); bottom-screen rank size (`PlayerHud.cs:2571`, multiplayer HUD); JP empty message
  (`PlayerDialog.cs:686`, JP ROM only: drop unless JP is supported).
- **Music:** "update SFX" in a running match (`GameState.cs:265`). Drop: hold-scan SFX (`PlayerInput.cs:129`, an MphRead
  feature), MphRead's own menu SFX scripts (`Menu.cs:2670`, not our menus).
- **Multiplayer:** multiplayer saving (`Menu.cs:610`); 1P time up (`GameState.cs:381`); affinity weapon replacement
  (`SceneSetup.cs:1078`); bot personality offsets (`AiPersonality.cs:56`); the `sktodo-ai`/`todo-ai` notes in
  `PlayerAi.cs` (mostly "confirm"/"bug?" questions: answer from the ROM, keep vanilla's behaviour).
- **Camera FOV:** free-cam roll (`Renderer.cs:357`, debug camera: likely drop).
- **Kept by this thread (FPS stuff and physics are its specialty):** A4 bot random rolls (`PlayerAi.cs:3437`, `:3486`),
  A5 the ~129 unclear FPS lines, A8 roll, `PlayerAi.cs:1266`/`:1271` (time vs occurrence hack); effect frame time
  (`Renderer.cs:2517`, `:2755`, `BeamProjectileEntity.cs:929`, `:1269`, `Effects.cs:164`); collision (`PlayerCollision.cs:817`,
  `:902`); strafe tilt on other input paths (`PlayerInput.cs:1291`, `:1324`, `:1341`, `:1350`); process conditions
  (`PlayerProcess.cs:194`, `:211`); boss/enemy "is it a bug?" questions (`24_Gorea1A.cs:1131`, `:1179`, `28_Gorea1B.cs:173`,
  `33_GoreaMeteor.cs:302`, `41_Slench.cs:1024`, `35_Voldrum.cs:61`).
- **No action (record only):** names (173), debug/viewer/editor/export/repack lines, allocation/perf/code-sharing notes,
  First Hunt (`FH`) leftovers, `MP1P` (6), playable Guardian / Guardian HUD (not vanilla), MphRead's input modernization
  (`itodo` sensitivity, stick thresholds), `todo: bugfix` (bucket D: vanilla bugs, kept).

## Gorea (2026-10-11, checked against the rev 1 code)

Gorea's code is overlay 12 (base 0x2133B80). MphRead's `Func21xxxxx` names in the Gorea files are not rev 1
addresses (around 1B, rev 1 = MphRead name - 0xA0). Scratch disassembly: `%TEMP%\gorea_re\ov12.s` (PC only, not kept).

**Rotating beam weakness: matches vanilla.** The six-phase table (`Metadata.GoreaEffectiveness`, `5, 0x81, 0x401,
0x2001, 0x4001, 0x301`) is byte-for-byte at 0x2142974. Vanilla's weapon change (0x2137C70 = `Enemy24Entity.ChangeWeapon`)
steps the weapon 0-5, points at the phase colours (table 0x2142588), changes music, arms both arms, and writes the phase's
weakness into both arms (their hurt zone sits on the shoulder joint) and Gorea 1B's chest sphere. Decoded (MphRead's own
list is in `Metadata/Enemies.cs` ~2980):

| Phase | Gorea uses (glow) | Hurts the arms / chest sphere |
|---|---|---|
| 0 | Battlehammer (green) | Volt Driver half |
| 1 | Volt Driver (yellow) | Battlehammer normal |
| 2 | Magmaul (orange) | Judicator half |
| 3 | Judicator (purple) | Magmaul normal |
| 4 | Imperialist (red) | Shock Coil half |
| 5 | Shock Coil (blue) | Imperialist double; one Imperialist hit finishes the arm (`26_GoreaArm.cs:141`) |

Power Beam does half in every phase; Missile and Omega Cannon nothing. Gorea 2's seal sphere: Omega Cannon only (`0x20000`).
**Phase colours** go on the 3 gun-tip materials per arm, `ChestCore` + `HeadFullLit`, and a darker tint on the body
(vanilla 0x2135670 = `IncrementAllMaterialColors`), and on 1B's chest sphere. The shoulder pads (`L_/R_ShoulderTarget`)
are left out in vanilla too (0x2135670, reset 0x21347F0); their own material animation (1A group 26) only pulses
white-grey, and a hit flashes them orange. Not yet confirmed by eye in BizHawk.

**TODO answers:**

| TODO | Result |
|---|---|
| `31_Gorea2.cs` "pretty jerky" (upstream :546) | **FIXED.** Hover angles stepped +4/+2 deg per 60 Hz frame and the jitter re-rolled every frame = 2x vanilla (0x213D58C: +4/+2 deg and two rolls per 30 Hz tick). Now +2/+1 per frame, rolls on even frames (`UpdateHoverPosition`) |
| `28_Gorea1B.cs` State07 (grapple swing, next to :628) | **FIXED.** Vanilla turns the grapple vector sideways, (-z, 0, x) (helper 0x2139BD0, called from 0x21399F8); MphRead used (x, 0, -z). The x0.04 (163/4096) and the keep-last-value case are vanilla (TODO :628 checked) |
| `24_Gorea1A.cs:1131`, `:1179`, `:469`, `:1301`, `:1510`; `28_Gorea1B.cs:173`; `33_GoreaMeteor.cs:302` | Checked: vanilla does the same (addresses in docs/MPHREAD_TODO.md) |
| `28_Gorea1B.cs:401` "add a bugfix" | Vanilla never writes the clamped vector back (0x213B86C-0x213BB84); MphRead does (its fix). No visible difference; left |
| `24_Gorea1A.cs` CheckTargeting, Shock Coil phase | **FIXED.** Vanilla's phase-5 fire check (0x21377FC) shoots once per 30 Hz anim tick inside [ShotCooldown, AutofireCooldown]; MphRead shot every 60 Hz frame = 2x shots, damage and shot-sound requests. Now `frame % 2 == 1` (phase 0's every-4-ticks was already right) |
| Frame rate audit, all Gorea files (2026-10-12) | Every Gorea `FPS stuff` line was checked against rev 1. **FIXED:** Gorea 1B's grapple reeled the player in 2x fast (0x213B640: the step is per 30 Hz tick; `28_Gorea1B.cs` `Func213B678` now x2); 1B's grapple swing added Gorea's speed at half rate (0x2139AEC adds the per-tick speed, `_field10` is halved again in `Func213C4DC`; now `_speed * 2`); Gorea 2's State08 timer clamp was `Max(…, 15 ticks)` where vanilla takes `min(…, 15)` (0x213F124); Gorea 2's `Func213D80C` snap distance was halved like a step (vanilla 0x555 = 1/3, 0x213D7C8); the meteor's hit shake re-rolled its jitter every 60 Hz frame (0x21413D8: three rolls per 30 Hz tick; now rolled on even frames and held). Second pass (same day), also **FIXED:** 1A's arm-regen and arm-attack timers ended half a tick early (vanilla counts N down to -1 = N + 1 ticks, 0x2134484; now `* 2 + 1`); Gorea 2's laser closed 1/24 of the gap per frame where vanilla closes 0x155/4096 per tick (0x213F614), now 1 - sqrt(1 - 341/4096) per frame (its second, fixed-length step at 0x213F758 was already right); Gorea 2's contact push (0x2140854) is horizontal in vanilla (Y zeroed) and adds between/4 once per tick, MphRead pushed in 3D and twice per tick on held contact (now `between.WithY(0) / 4 / 4` per frame); 1B's Trocra jitter rolled every frame (0x213AF64: once per tick; now rolled on even frames and held); Gorea 2 Behavior06 now also saves the return state like vanilla (0x2140088; that branch never runs, as MphRead notes) |
| Decompile slips (found in the audit) | **FIXED.** `31_Gorea2.cs` both teleport behaviours wrote `_field234` (the 22-tick timer) where vanilla writes field 0x243, the state to return to (0x213FCF4, 0x21402D4); `28_Gorea1B.cs` `UpdateGrappleDrawInt` dropped the fraction on wrap where vanilla keeps it (`and 0xFFF`, 0x213C7E8) |
| `24_Gorea1A.cs:801` Shock Coil sound hack (board T8) | Still open, hack kept. Vanilla's path: shot state 0x21357CC -> ov2 0x2103668 (uncharged shot: flags 0, no recency flag 0x80000) -> arm9 0x2073EC8 -> slot search 0x20741B8 (free slot, else take over the lowest-priority copy no higher than the new one, else the same source's oldest, else drop). A same-source copy has equal priority, so vanilla's code would restart it too, which disagrees with MphRead's author's ear. Settle by listening in BizHawk (phase 5). GameAudio's `TakeSlot` already follows that rule. (The handoff's 0x2073964 is a rev 0 address) |

Check: Tools `-endingtest` 3/3 (bad, chain, good: Gorea 1A/1B x3 and Gorea 2 run through) after both fixes. 2026-10-12: 3/3 again after the audit fixes.

**Recolours (campaign census, 2026-10-11):** every enemy, door, force field and teleporter palette on the disc is used;
the Gorea effect models (`goreaArmRegen`, `goreaMindTrick`, `goreaGrappleBeam`, `goreaLaser`) have one palette each,
so `recolor: 0` is right. Gorea's colour changes reach the Android renderer (`u_matDiffuse`/`u_matAmbient`). Fire/Ice Spawn: `39_FireSpawn.cs:87` writes the ice ambient (18, 27, 31) onto the shared model's
material 0 and never resets it; vanilla does the same (ov10 0x215F7A8-0x215F7EC). The only spawns are Council Chamber
(fire, Alinos) and Fault Line (ice, Arcterra), and each landing builds a fresh Scene (model cache cleared), so the tint
can't reach a fire spawn. No fix.

## Log

| When | What | Result |
|---|---|---|
| 2026-10-16 | Shock Coil headshots (user report: head hits killed almost instantly). Vanilla (rev 1 arm9 0x2041388-0x2041404) scales a continuous beam's damage (+0x26) AND headshot damage (+0x28) by 1/32 per tick with the same dither, splash (+0x2A) whole; MphRead (upstream too) scaled only the damage, so a Shock Coil / Platform Arc Welder hit in the head zone did 10-20 every 60 Hz frame and flagged HEADSHOT. Fixed in `BeamProjectileEntity.Spawn` (`PlayerEntity.HostContinuousHeadshots`; replays before format 38 keep the old way) | `-shockcoilheadtest` PASS: head = body (100 health in 166 frames); old: 10 frames |
| 2026-10-03 | A8 morph ball roll fixed (owner queue #4). New route `probe_roll.lua` (the landing site pops a one-time message box when the ball reaches the flat floor, ~frame 232: OK at (128, 142)), Tools `-accuracy roll`, `-accuracy ramp ball` | acceleration and top speed per tick = vanilla; regressions in board Cross-session edits |
| 2026-10-03 | Owner's plan: MphRead TODOs go to their owning threads a handful at a time. Inventory re-run (1,451 lines), batch 1 = T1-T15, backlog in "Dispatch" above. No code changed | docs only |
| 2026-10-03 21:40 | A9 steep-slope walking fixed (owner queue #23). Routes `probe_slope.lua`, `probe_ramp26.lua`, `probe_ramp45.lua`, driver `pokes`; Tools `-accuracy slope/ramp26/ramp45 <csv>`, `-accuracy steep <ROOM>|all [verts|box x0 x1 z0 z1]` (walkable steep faces) | 26.6 deg 89.7 -> 106.1%, 45 deg 59.5 -> 101.5% of vanilla; regressions in board Cross-session edits |
| 2026-10-03 21:41 | A9 on the Odin: `PlayerCollision.cs` (21:21:57) rode along in Music 5's 21:34 APK (Release MphRead.dll 21:33:26), installed 21:41 | owner to test |
| 2026-10-03 20:50 | A8 on the Odin: `PlayerInput.cs` (18:29) rode along in every shared-tree APK since 18:53 (latest 20:32, installed 20:34); no install of this thread's own | owner to test by feel |
| 2026-09-30 | Inventory + plan (this file). No code changed | - |
| 2026-10-01 | A3 morph ball camera follow fixed (`PlayerCamera.cs`). Emulator: morph icon is a bottom-screen tap at (220, 163); camera position = player +0x55C | regressions pass (board ~00:45) |
| 2026-10-01 | A2 camera tilt fixed (lean 27 -> 54 deg like vanilla). Harness: MphRead holds input until ~frame 952 of the landing, so tests now settle 1000 frames | regressions pass (board ~00:50) |
| 2026-10-01 | A7 walking top speed fixed (sqrt friction + matching position step, traction kept whole); exact 0.204 u/tick vs vanilla; crease still climbed. Experiments: half traction, 30 Hz walk tick and a full once-per-tick move+collision all stick at the crease | regressions pass (board log ~00:30) |
| 2026-10-01 | Logbook off-by-one fixed (`GameState.cs`, `Menu.cs`); A7 walking measured (7-9% fast), tick-structured fix built and verified on open ground, then reverted (slope-crease collision risk); landing slide + gunship-riding differences measured. Harness: headless tests must run with `collectDrawItems: true` (the draw pass advances model animations, and platform collision follows them; without it the gunship never bobs). Door sweep: besides run-to-run noise it intermittently throws NullReferenceException in `RoomEntity.UpdateNodeRef` (a null portal) with or without these changes | `-accuracy logbook` 4/4, jump exact, regressions pass |
| 2026-09-30 | A1 jump fixed (`PlayerInput.cs`), MphRead side `-accuracy jump` (Tools `AccuracyTest.cs`) vs BizHawk `routes/probe_buttons.lua`. Also learned: vanilla logic ticks every 2nd DS frame (positions change on even frames only); A, B, X, Y and a double tap all jump in the default controls; walk speed cap 983/4096 per tick reached in 3 ticks | apex 2.541 = vanilla, 0.000 diff over 32 ticks; regressions pass (board log ~22:20). Door sweep is nondeterministic run to run (97-100/103 with or without the fix) |
| 2026-09-30 | Emulator side, step 1a: BizHawk 2.11.1 scripted (`tools/accuracy/`): power-on -> menus -> Celestial Archives landing site, all by script; savestates per checkpoint; US 1.1 player 0 found at `0x020DAF94` (EU 1.1 - 0xA0), position tracks walking | Route verified from power-on |
