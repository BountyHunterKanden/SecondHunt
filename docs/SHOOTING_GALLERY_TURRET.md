# Shooting Gallery turret + bomb slot (scope)

Owner, 2026-10-15: add the cannon in Shooting Gallery to our Echoes arena, working 1:1 the way Echoes does it.
- **Bomb slot, Samus:** works exactly as in Echoes.
- **Bomb slot, every other hunter:** the slot grabs and locks their alt form the way Echoes grabs the ball, and their
  alt-form attack "bombs" the slot. Owner, 2026-10-16: done by laying Samus's bomb. Only while held, their alt-form
  attack lays Samus's morph ball bomb in place of their own attack, and that bomb sets the slot off as hers does.
- **Sources:** read from the RETAIL dumps only, never `echoes_widescreen.iso` (patched). Anything we get working must
  work with either game as the source: retail GameCube Echoes (`brawl_extract/prime/echoes_retail.rvz`, G2ME01) or
  Metroid Prime Trilogy (`trilogy.rvz`, R3ME01, folder `MP2/`).
- **Status (2026-10-16 04:49):** T1-T5 built (import, turret, bomb slot, look, LAN/online sync) and the turret's own
  Echoes sounds (section 8); ON THE ODIN. See sections 7-8. Next: T6 (bots).

The research files are in `extract_out/multiplayer/mp2/turret/` (gitignored):
- `dump_ret/`, `dump_tri/`: script dumps of all six arenas from each disc.
- `net_ret.txt`, `net_tri.txt`: the turret's script network.
- `seq.py`: decodes SequenceTimer timings.
- `turret_re/`: the turret code module. `rso_dis.txt` is Trilogy's RSO disassembled with names; `gc/` and `wii/` hold
  the modules; `ret_eea4cf05.wpsc` is the shot.
- `slot_re/`: BallTrigger, TriggerOrientated and DamageableTrigger code from the retail DOL; `fac_ret.txt` is the
  object factory table.

## 1. What Echoes does

Coordinates below are Echoes metres, Z up. At our arena scale of 0.5926 (`recipes/arenas/M06_ShootingGallery.json`),
the turret's base (0, -27, 10) comes out at MPH (0, 5.9, 16.0).

### Objects (Default layer)

| Object | What it is |
|---|---|
| PLRT 0xf6 'Player Turret' (0,-27,10) | The turret. Limits: left/right 90°, up/down 45°. HorizSpeed 120°/s, VertSpeed 60°/s, FireRate 1.0 (seconds between shots). WeaponDamage: type 10, 75 damage, radius 10, knockback 20. Shot effect: eea4cf05 in multiplayer (6517e3b1 single player). Sounds: rotation 9869, MP projectile 9586, MP impact 1421. FlagsPlayerTurret and DamageAngle are stored but never read. |
| TRGR 0x119 'Enter man turret' | Sends ACTV to the turret on entry. Deactivates itself on enter. |
| PLAT 0x11d 'Turret Collision' | Health 100. Collision f247bb82 (DCLN), model 3801de98. Takes damage from Power, Dark, Light, Annihilator, Missile/Super, Screw Attack, Power Bomb, Charge and the charge combos. Immune to Bomb and Boost. DAMG sends XDMG to the turret; DEAD starts 'Turret Destroyed'. |
| ACTR 0x10b 'Animating Turret' | Animation set e749cb49, model ecf072bc, starting animation 8. Animations: turret_ball_up, turret_ball_down, turret_90_up/down_additive (pitch), spin X/Y/Z, still, rest, turret_shoot. Holds the muzzle and seat locators (upwardballsnap_LCTR). |
| PLAT 0x10e 'Dummy Base' + 2 waypoints | The lift, which travels between z 3.555 and z 10. |
| ACTR 0x11b 'Turret Hologram' | Animation set 0777526b (holo_spin), shown while the turret is free. 'Glow for hologram' sits with it. |
| BALT 0x194 'BallTrigger Bombslot' (0,-37.5,11) | The slot. Scale 3 (a ±1.5 m box), NoBallMovement on. Active only while someone is seated. |
| DTRG 0x185 'Bombslot Right' (0,-37.5,10.809) | Health 1. Vulnerable to Bomb and Power Bomb only. Scale 0.1. Armed while a ball sits in the slot. |
| DMGA 0x2cc 'Bombslot DamageActor' | 1000 damage to the Turret Collision platform when the slot's trigger dies. |
| TRGO 0x258 'Kick Out Ball' (0,-37.5,11) | Pushes out anyone in the slot while it isn't armed. |
| TIMR 0x17e 'Bomb Slot Reset' 3.0 s | Re-arms the slot after a bomb. |
| Slot visuals | Effects: BombSlotElectric, Arc (0.5 s). Sound: Electrical Arc. Ring model b2ed35b1. |
| Power Bomb pickup (8,-42) | Sits by the slot. MPH has no power bombs. |

### Sequences

Timings are decoded from the SequenceTimer data with `seq.py`. They are identical on both discs.

**Enter:**
- t=0: the lift rises (Raise Turret), the slot arms, a collision box goes up around the seat, forced first person
  starts, the hologram hides, and the HUD loop sound plays.
- t=0.2: the platform's health is reset.
- t=2.0: STRT to the turret, so the player gets control at 2.0 s.

**Leave** (Jump pressed):
- t=0: the lift lowers.
- t=0.039: the slot is disarmed.
- t=1.5: the turret is deactivated.
- t=4.0: the enter trigger and the hologram come back.

**Destroyed:**
- t=0: explosion, and STOP to the turret.
- t=0.01-0.11: 'Player Kill' trigger.
- t=0.028: the slot is disarmed.
- t=6.986/7.0: the enter trigger and the hologram come back.

### Turret code

GameCube keeps the turret code in `RelProd/ScriptPlayerTurret.rel`; Trilogy keeps it in
`MP2/RSO/Production/RSO_ScriptPlayerTurret.rso`, which has names.

**Getting in:**
- The occupant is whichever living player tripped the enter trigger.
- On entry, every other player within 5.0 m horizontally is killed (`KillPlayer`, credited to the occupant).
- Multiplayer only: every other player within 4.0 m horizontally and 5.0 m vertically also takes 1000 damage, and the
  occupant's power-up timers appear to be reset.
- The seated player is held at the seat locator facing the aim direction, with motion stopped. They're forced into
  first person and can't morph (PlayerHint, plus a "Prevent Morph" trigger). I found no invulnerability.

**Controls**, once seated and active:
- **Yaw:** rate control. Stick X × 120°/s, no acceleration, clamped to ±90° from the turret's fixed facing.
- **Pitch:** position control. Stick Y sets a target up to ±45°. The barrel moves toward it at 60°/s, eases in near
  the target and snaps within 0.1°.
- **Fire:** a fresh press of Fire, then 1.0 s cooldown. Holding the button doesn't autofire.
- **Exit:** a fresh press of Jump.
- While entering or leaving, the barrel recentres at 4× speed.

**The shot:**
- An energy projectile from the muzzle locator, using effect eea4cf05 in multiplayer. Effect values: lifetime 128
  frames, speed 1.0 (most likely per frame, so 60 m/s), range 51 m, no gravity, no homing.
- The shot ignores the turret's own platform.
- Damage is 75 with a 10 m splash radius and knockback 20.
- The occupant owns the shot, so the occupant gets the kills. There are no team checks in the turret code.

**Damage to the turret:**
- Damage to the turret doesn't reach the occupant. It only flashes the turret and plays the impact sound to the
  occupant.
- The turret remembers who damaged it last. When it dies, the occupant is ejected and killed, and that last damager
  gets the kill. A bomb in the slot counts.

**Normal exit** (CPlayer::SetTurretState 0x8014cc24-0x8014cd74): the eye goes where the turret's camera was (3 m ahead
along the barrel from the seat), and the world velocity is set to 30 m/s back along the barrel (flat) and 30 m/s up.
Solid comes back at once (AddMaterial 0x8014cd10); the 'Dumb Collision Box' drops on the same Leave. The turret's
DCLN doesn't exclude players (its materials: Metal, Wall, bit 27), but Retro's moves don't stop a box that starts
inside a mesh, so the player flies out through the back of the shell. Ours: the column goes off in the Leave step, and
the shell lets the leaver through until they're past its reach (`EchoesTurretEntity._leaver`).

**Seat camera** (CPlayer turret states 1/3): the locator plus the aim turn times (0, 3 m, 0), with no eye height;
Entering slides the camera there from the eye over 0.5 s (PlayerEchoesSeat.SeatCamera).

### Bomb slot code (retail DOL)

**Who it holds:** one player, the first to enter, and only while morphed.

**On entry:** the ball is snapped to the slot's centre. Every frame after that it's pulled back to the centre
(momentum = mass × (centre − pos) / dt), and NoBallMovement also zeroes its spin and force first. Rolling does
nothing. The script's attraction settings (force 40, distance 1.5) never come into play, because the entry snap
always happens first.

**While held:**
- **No unmorphing** (owner correction, 2026-10-15). The held player stays in their ball/alt form until the slot lets
  go. The code search found no unmorph check, so the lock lives somewhere it didn't look; the owner's rule stands.
- **Bombs:** allowed. Bombing is the point of the slot.
- **Boost:** it charges, but on release it only spins the ball, with no launch.

**Kick-out** (trigger flags 0x67806: morphed and unmorphed, P1-P4, impulse mode, scaled by overlap):
- Each frame, anyone inside gets an impulse of (0, 500, 0) × overlap fraction, in world space.
- It's active whenever the slot isn't armed, so nobody can sit in a slot that isn't live.

**The bomb:** the held ball's centre is 0.19 m from the 'Bombslot Right' box, so a bomb laid there reaches it. That
is inferred; the blast-radius routine itself wasn't found.

### GameCube vs Trilogy

**The same on both discs:**
- Every turret object value and every sequence timing.
- All the turret's art: models, collision, both animation sets and the animations they use, the shot and slot effects.
  Checked by hash with `assets.py` / `assets2.py`.
- The turret module's constants.

**Different:**
1. **Slot re-arm (Trilogy script).** When a ball leaves the slot, the slot switches itself off and a new 0.02 s timer
   'Reset Ball Trigger' switches it back on. During those 0.02 s the kick-out is live, so a ball leaving gets a 1-frame
   push. Its only effect is a slightly harder exit.
2. **Controls.** GameCube exits on Jump or the alternate Jump binding, and Trilogy on Jump only. Trilogy's fire button
   has a Wii-only branch. Neither matters to us, because our own control map drives the turret.
3. **Not checked:** an instruction-by-instruction diff of the two modules, and Trilogy's BallTrigger code.
4. **Every MREA was re-cooked in Trilogy** (sizes differ in all six arenas). See section 3.

## 2. How it maps onto our game

### Turret (any hunter can use it)

- **Import** (Core `Arenas/EchoesArena`, beside `Breakables`): a new arena.json section, `turret`, read from the
  user's disc by property ID. Trilogy's added properties such as BoundsSizeMultiplier are harmless. The section holds:
  - positions, limits and speeds, fire rate, damage and health
  - the slot and kick-out boxes
  - the SequenceTimer timings, decoded at import
  - the locators, read from the animation set's skeleton

  The recipe holds no game data. Recipes for the models and animations come later (look phase).
- **Host entity,** on the `EchoesBreakableEntity` pattern: a hidden MPH entity that the host runs and clients mirror
  through `Net/NetWorld.cs`. It owns:
  - the enter box, seat, lift and states (free / entering / active / leaving / destroyed)
  - the timings above
  - the entry telefrag (5.0 m kill, and 4.0 × 5.0 m 1000 damage), scaled to MPH units
  - the 100-health platform. Beam and missile weights follow `EchoesBreakables.Damage`, so Echoes points × 6 gives
    600 MPH breakable health. Bombs and boost don't hurt it.
  - last-damager kill credit
  - the exit push
- **Seat:** the occupant is pinned to the seat in forced first person, with no alt form and no weapons of their own
  (`NoFormSwitch` + a turret flag). The HUD shows the turret view.
- **Controls, 1:1:**
  - The **move stick** aims: X sets the yaw rate, Y sets the pitch target.
  - **SHOOT** fires.
  - **JUMP** exits.
  - Our aim stick, touch and gyro are an open question, D2.
- **Shot:** an MPH projectile spawned at the muzzle.
  - Speed 60 m/s → 35.6 MPH units/s.
  - Range 51 m → 30.2 units.
  - Splash radius 10 m → 5.9 units, with knockback.
  - Owner = the occupant for kill credit.
  - Damage is open question D1.

### Bomb slot

**Arming:** the slot is armed while the turret is occupied. When it isn't armed, the kick-out pushes anyone in its box
out along +Y, with the impulse scaled to our physics.

**Samus** (exactly as in Echoes):
- Her morph ball is snapped to the centre and held there every frame. Rolling does nothing.
- Boost only spins the ball, with no launch.
- She can't unmorph while held (owner correction).
- A bomb laid there whose blast reaches the 'Bombslot Right' box **destroys the turret and kills the occupant**
  (the DamageActor's 1000 to the 100-health platform, then the Destroyed sequence).
- Her bomb is detected the way `EchoesBreakableEntity` already watches exploding `BombEntity`s.
- Dark Samus: open question D3.

**Every other hunter, in alt form:**
- The same grab: the first to enter is snapped to the centre and held, locked in alt form (no unmorphing).
- While held (and only then), their alt-form attack lays **Samus's bomb** in place of their own attack (owner,
  2026-10-16). It has Samus's values and cooldown, appears at the held ball, and its blast **destroys the turret and
  kills the occupant** exactly as Samus's does. Their own attack (Kanden's Stinglarva bomb, Sylux's Lockjaw bomb,
  Noxus's spin, Spire's Dialanche attack, Trace's and Weavel's lunge, the Trooper's drone shot, Zero Suit Samus's once
  her kit lands) doesn't happen in the slot. Code: `PlayerEntity.SlotBombInput` (PlayerEchoesSeat.cs), one hook in
  PlayerInput's alt-form input, `BombEntity.Spawn(..., asType)`.
- Neither the seat nor the slot lets anyone switch forms, bots included (`TrySwitchForms`).

**Getting out of the slot:** the held player can't unmorph, so the slot lets go only when it disarms. After a bomb
(3 s 'Bomb Slot Reset'), or when the turret empties or is destroyed, the kick-out pushes them out along +Y.

**Kill credit:** whoever sets off the slot is the turret's last damager, so they get the occupant's kill (Echoes'
rule).

**Visuals and sound:** the slot's electric effect, ring and arc sound turn on while it's armed.

### Not in Echoes, but needed

- Net sync of the turret state and occupant, the slot holder, and the kill events (host-authoritative).
- A new replay format version (the precedent is `Settings.Breakables`).
- Bots: they should avoid the telefrag zone and the turret's line of fire, and later man it or bomb the slot (phase T6).

## 3. Trilogy as the source: blocker

Today the Echoes arenas don't import from Trilogy at all. The `mp2/trilogy` edition makes only guns + hd, and the
multiplayer group (arenas + music) is GC ONLY by the owner's rule: an edition takes a group only if it makes all of it
byte-identically (docs/IMPORT_PIPELINE.md). Trilogy re-cooked every arena MREA, and its scripts differ in a few
objects per arena (12-46 lines of dump each), including this arena's slot logic and a music volume. So "the turret
works from both discs" needs step T0 first:
- **T0:** run the arena import from Trilogy's `MP2/Metroid6.pak` and diff its output against the GameCube reference
  (`-importcheck arenas`).
  - If geometry, textures and our converted entities come out identical, add the arenas to the edition.
  - If they don't, the owner decides (D5).
  - The music group also needs the same check: Trilogy's `Audio/` streams differ.

**T0 result (2026-10-16).** Run as `-importeditions mp2/trilogy --mp2 brawl_extract/prime/echoes_retail.rvz`, with no
`--write`; the log and `coldiff.py` are in `extract_out/multiplayer/mp2/turret/` (`t0_log.txt`).

**Identical from both discs:**
- **Shooting Gallery,** byte-identical. The script differences (slot re-arm, BoundsSizeMultiplier) don't reach our
  current import.
- Crossfire Chaos, Pipeline, Spider Complex.
- The arena music.
- All guns and hd items.

**Different:** only collision, in two arenas, from small patches Retro made for Trilogy. Found by diffing the MREA
collision meshes with `coldiff.py`:
- **Sidehopper Station:** 3965 → 3981 triangles (16 removed, 32 added), Metal Wall/Floor plus a few Spider Ball wall
  triangles. They sit in a symmetric band at x ±16.6, y -10..-4.7, z 1..18 (Echoes metres): the walls of the left and
  right morph-ball tunnels beside the central ship, around the 'Tunnel Exit Slave' triggers at (±16.7, -7). Our
  arena.json differs only in its CollisionPolygons count.
- **Spires:** the same triangle count. One stone wall at (-47..-43, 155..177, z 9.5..31.7) was re-triangulated: 17
  triangles replaced, vertices moved by at most about 0.15 m.

**So the multiplayer group is 5/7 identical** and stays GC ONLY under the byte-identical rule. To let Trilogy make the
arenas (and so the turret), the owner picks one (D5):
- (a) Accept Trilogy's patched collision for those two arenas. Trilogy players get Retro's tunnel and wall fix, and
  online matches between the two sources play on slightly different collision there.
- (b) Make our import come out identical from both discs, e.g. by keeping the retail collision. Not possible without
  data that only the GameCube disc has, so it isn't practical.
- (c) Let Trilogy make only the arenas that match: Shooting Gallery, Crossfire, Pipeline, Spider Complex, plus the
  music. Sidehopper Station and Spires would still need the GameCube disc.

**Side finding** for the bodies import: `bodies/MP2DarkSamus` now comes out identical from Trilogy but isn't listed in
the edition (the tool's NOTE: run `--write`). Not changed here.

**Owner decision, 2026-10-16:** keep gating the multiplayer imports (arenas + music) to the GameCube Echoes disc.
- D5 is closed: no Trilogy arena source for now. T0 is done; option (a) or (c) can reopen it later at no turret cost,
  because the turret's data is identical on both discs.
- D6 is closed: GameCube retail behaviour (no slot re-arm).
- The turret is built and tested from the GameCube disc only.

## 4. Phases

| Phase | What | Checks |
|---|---|---|
| T0 | Trilogy arena source (section 3) | `-importcheck arenas` from both discs |
| T1 | Import the `turret` section from either disc. Both discs give the same values, apart from the Trilogy re-arm flag if D5/D6 keep it. | A PC test reads both paks and compares |
| T2 | Turret entity: enter, lift, seat, controls, shot, health, destroyed/leave sequences, telefrag, kill credit | A PC Solo arena test with a bot as the target |
| T3 | Bomb slot: arming, grab/hold for all hunters, Samus bombs, alt attacks, 3 s reset, kick-out | A PC test per hunter |
| T4 | Look: turret, lift, hologram and ring models and animations through the Retro importer; shot and effect stand-ins; sounds | Side by side with Echoes in Dolphin on the PC |
| T5 | Net sync + replays | A LAN check on the PC |
| T6 | Bots | |

## 5. Owner decisions

- **D1, damage scale (DECIDED 10-16: (a), the same fraction of a life; DONE: Echoes MP health is 99 like MPH's, so a hit is 75).** Echoes' shot does 75 against Echoes multiplayer health. Echoes multiplayer health is not found
  yet: max health is 99 + 100 per energy tank, and the multiplayer tank count isn't known. Options:
  - (a) The same fraction of a life in MPH. This needs that number, which can be pinned down in the code or in Dolphin
    on the PC.
  - (b) Our breakables' convention: 1 Echoes point = 1 Power Beam shot = 6 MPH. That makes a hit 450 (any hit kills),
    which is likely too strong.

  Recommended: (a) once measured.
- **D2, aim controls (DECIDED 10-16: move stick 1:1 by default, plus right stick, touch and gyro).** 1:1 means the move stick aims (yaw rate, pitch position). Should our right stick, touch and
  gyro also aim the turret? Recommended: 1:1 by default, with the aim inputs also mapped.
- **D3, Dark Samus (DECIDED 10-16: the Samus rule, her ball + bombs).** Use the Samus rule (her ball + bombs), or the alt-attack rule (her Hyper Ball)?
- **D4, Zero Suit Samus (DECIDED 10-16: wait for her kit, then the alt-attack rule).** Her slot rule waits for her alt-form kit.
- **D5, Trilogy (CLOSED 10-16: arenas stay GameCube-only).** If the Trilogy arena import isn't byte-identical, should we accept "the same play data" in place of
  byte-identical output for this group?
- **D6, slot re-arm (CLOSED 10-16: GameCube retail, no re-arm).** One behaviour from both discs:
  - GameCube retail, no re-arm. This is what's listed today.
  - Trilogy's 0.02 s re-arm, which gives a ball leaving the slot a 1-frame push.

  Recommended: retail GameCube, since it's the reference disc.
- **Power bombs:** Echoes' slot also takes power bombs, and there's a Power Bomb pickup beside it. MPH has none, so it's
  left out unless wanted.

Bots (T6, owner 2026-10-16):
- D7: bots sometimes go and man a free turret (when it's closer than their target; more often at higher difficulty).
  They get out the way a player does.
- D8: bots fight an occupied turret by shooting it, and a bot near the slot rolls in and bombs it (its alt-form
  attack for non-Samus hunters).
- D9: the hum stops and the hologram's sound comes back, as in Echoes.

## 6. Open research

- Whether the shot's speed is per frame, and whether range or lifetime ends it. Measure in Dolphin on the PC.
- The sign convention for pitch.
- Camera modes 2 and 3.
- Whether items 0x39-0x3b and 0x5f are the power-up timers that get reset on entry.
- An instruction-by-instruction diff of the GameCube and Trilogy turret modules, and Trilogy's BallTrigger code.

## 7. Built (2026-10-16)

Code: Core `Arenas/EchoesTurret.cs` (import, look import, `EchoesTurretEntity`), MphRead
`Entities/Players/PlayerEchoesSeat.cs` (seat + slot hold on the player) and 3 hooks in `PlayerInput.cs`; App
`Campaign/CampaignArena.cs` draws it. `MatchSettings.EchoesTurret` (null = on, in every kind of match; not on the
wire: null on every device; replay format 37). Importer stamp arenas 4 (3 = before the sounds).

Import (`-echoesarena import`, and the in-app importer; arena.json "Turret", hd/turret.json):
- Values from the retail disc: limits +-90 yaw / +-45 pitch at 120 / 60 deg/s, a shot every 1 s, 75 damage in a 10 m
  (5.93 unit) splash with no falloff, health 600, the platform's 124-triangle collision mesh about the pivot.
- The lift from the animated actor's own clips: the pivot (turret_SDK) rises 5.93 -> 10.67 in 1.77 s
  (turret_ball_up, overshooting to ~11.9) and lowers in 1.57 s (turret_ball_down).
- The bomb slot: the BALT next to DTRG 'Bombslot Right' (open side MPH -Z), the bomb box, the kick-out box and force.
- The look: the turret's skinned model split into its bones' rigid parts (8 parts, all 1314 triangles), the hologram
  (holo_spin) and the slot's ring as placed passes in arena.bin; each part's matrix over ball_up, ball_down,
  turret_shoot and holo_spin (25 poses each); Echoes' lighting at the turret, hologram and ring.

Behaviour (sequence times from the area's timers): entry kill within 5 m, 1000 damage within 4 x 5 m; control at 2 s;
leave: out with Echoes' 30 m/s push, slot off at 0.039 s, turret free at 4 s; destroyed: occupant killed (credited to
the last damager, not a teammate without friendly fire), back at 7 s. Slot: a bomb's blast sets it off: Samus's own, and
the Samus bomb every other hunter lays there with their alt-form attack (owner 2026-10-16); the held player can't
unmorph; it destroys the turret and kills the occupant.

Look on the device: lowered in the floor hole with the hologram spinning while free, rising, raised with the barrel
turned by the occupant's aim, recoil on each shot, lowering on leave/destroy, red flash when hit, the ring while the
slot is armed.

Known approximations: the kick-out impulse (mass 200, momentum per frame); the
turret follows the occupant's aim one sim step late; Zero Suit's 90 health not scaled; an occupant who dies leaves by
the Leave timings; the explosion is MPH effect 77 (Echoes' own not imported); every turning part turns with both yaw
and pitch.

Checks (Tools, from `src/MphRead.Tools/bin/Debug/net9.0`):
- `-echoesarena import disc=../../../../../brawl_extract/prime/echoes_retail.rvz arena=M06_ShootingGallery`
- `-echoesturret test`: Samus and Spire matches through every state (68 checks)
- `-echoesturret look out=<dir>`: PC renders of the poses (free, rising, raised, turned, recoil, lowering)
- `-importcheck arenas all --mp2 ../../../../../brawl_extract/prime/echoes_retail.rvz`: in-app import = PC import
- `-replaygolden`, `-replaytest arena="Shooting Gallery" bots=3 time=120`
- `-echoesturret nettest` (LAN host + joiner); `-lantest`/`-onlinetest`/`-netloop` with `"arena=Shooting Gallery"`

Network play (T5): the host runs the turret; each snapshot carries it (`NetTurret`, 13 bytes, in `Net/NetWorld.cs`
after the crates + breakables; LAN protocol 21, online 19): state, seconds into it, aim, occupant and held seats,
health, hit flash, slot armed, and shot / blow-up counters. Clients run it as a mirror (`EchoesTurret.Mirror`, set by
MatchStart, cleared on a host takeover): no entering, slot, hits or shots of their own. The occupant is seated on every
device (so a guest's own player sits still and its stick + aim turn the barrel there at once, the host following its
view a round trip later), let out with the same push, the held ball held; a new shot is fired there as a look (a
client's beams do no damage) and a blow-up plays effect 77. The kick-out pushes only the device's own player there.
`-echoesturret nettest`: a host + joiner on this PC (LAN); PASS = both saw the same turret states, held seats, shots,
blow-ups, barrel turns and scores.

## 8. Sounds (2026-10-16)

The arenas importer renders the turret's sounds from the player's disc: the Echoes AGSC sound groups (Metroid6.pak's
3b13ca9f / 6e01e6a4 / 00b10d9c, AudioGrp.pak's a2f353cc and the ATBL b7c1b3a7; the recipe's optional `audioPak`)
through Core `Import/Retro/MusyX.cs` (which now reads Echoes' AGSC layout as well as Prime 1's), written as
`hd/turret_<role>.wav` and listed in hd/turret.json "Sounds" (id, volume = the SOND's max volume / 127, range = its max
audible distance x the arena scale, loop, ambient). A looped sample is written as one loop turn, crossfaded at the
wrap. `EchoesTurret.ImportSounds`; the import's note "turret sounds: 11 of 11".

| Role | Id | Where it comes from | When it plays |
|---|---|---|---|
| enter | 0x268f | turret_ball_up's sound event (0.0667 s) | rising, 0.0667 s into Entering |
| exit | 0x268e | turret_ball_down's (0 s) | as Leaving starts |
| rotate | 0x268d | PLRT SFXTurretRotation (loop) | while the barrel turns (4 frames' hold) |
| fire | 0x2572 | PLRT SFXMultiPlayerProjectile | each shot, at the turret |
| hit | 0x058d | PLRT SFXMultiPlayerImpact | the occupant only, each hit on the turret |
| fly | 0x256d | the shot's WPSC flight sound (loop) | following each turret shot while it flies |
| impact | 0x2573 | the shot's CRSC hit sound | where a turret shot hits (MphRead's Power Beam hit not played: `BeamProjectileEntity.HostOwnHitSound`) |
| hud | 0x268c | SOND (loop, ambient, viewport-dependent) | the occupant only, from Entering until Leave / Destroyed |
| holo | 0x2747 | SOND (loop, 150 m) | while the hologram shows (free) |
| explode | 0x2571 | SOND (150 m) | each blow-up |
| arc | 0x23a7 | SOND (loop, 50 m), the slot's BALT ENTR / EXIT | at the held ball while the slot holds it |

App: `Campaign/CampaignRenderer.TurretSfx.cs` (2 hook lines in CampaignRenderer.cs). Placed sounds fade linearly to
the range. Clients play the same from their mirrored turret.

The hum stops on Leave / Destroyed and the hologram's sound comes back with the hologram, as in Echoes (owner,
2026-10-16: that's how the game plays). A reading of Echoes' SequenceTimer (`CScriptSequenceTimer`, rs5mp2_p.dol
0x802076f4) suggested those connections are never sent; something it missed sends them.

Next: T6 (bots use the turret).

## 9. Owner's test, 2026-10-16: fix list (T7)

"The turret is working but there are several things that need fixing":
1. Visual and physical pieces of the turret cannon still missing.
2. Sounds still missing.
3. The cannon needs its own first-person HUD.
4. Locking into the bomb slot makes the camera very shaky (at least Sylux).
5. The bomb slot's little electric arcs are missing while a player is locked in.
6. The destroyed animation looks wrong (bombing does destroy the turret and kill the occupant).
7. The bomb is misaligned with the bomb slot.
8. The range is wrong: it should hit anything it can see.
9. Confirm the rate of fire is 1:1.
10. Carefully audit all other turret logic and bring it over.
11. Every visual and animation aspect.

### 9.1 Audit, 2026-10-16 (three research passes: retail G2ME01 Metroid6.pak MREA d97a600b, the turret RSO, Trilogy's symbol DOL)

Scratch notes: `extract_out/multiplayer/mp2/turret/` (net_ret.txt object network, dol/, ep_all.txt, ard.txt, pw2.txt,
turret_re/rso_dis.txt). Positions are Echoes metres, Z up.

**Logic**
| Item | Echoes | Ours before | Fix |
|---|---|---|---|
| Range (owner item 8) | CEnergyProjectile: 1 m per 1/60 s for 128 ticks (~128 m, 2.13 s) unless it hits. The 51 m is the shot's SOUND range (0x803bc964, used 0x80080a3c) | ShotRange 51 as unchargedDistance: dies at 51 m | no distance limit; life only |
| Splash | ApplyRadiusDamage 0x80243a70: full 75 anywhere inside 10 m | falloff to 0 at 10 m | flat |
| Fire rate (item 9) | fresh press, countdown 1.0 s, press in cooldown lost (0x27c4-0x27f8) | same | matches 1:1 |
| Cooldown on entry | not reset; counts only while active (0x26cc) | 0 on entry | Echoes' |
| Yaw at the limit | a step past ±90 is skipped (0xd54-0xdc8) | clamped | Echoes' |
| Pitch easing | target = stick x 45; 60 deg/s x (1 - PhongBlob(gap/90, 8)), snaps within 0.1 deg (0xe80-0xf58) | the same (PlayerEntity.PitchEase; PhongBlob 0x803855d0 = ((1 + cos(pi x)) / 2)^p) | done 10-16 |
| Knockback | 20 (damage info): ApplyPlayerKnockBackForce 0x8015c210 | the same (EchoesTurret.Knock -> PlayerEntity.HostKnock, section 9.3) | done 10-16 |
| Destroyed | aim snaps to centre (rso 0x1888/0x18b0/0x18cc), looped idle; the lift RSETs to the bottom (no lowering); 'Player Kill' TRGR 0x3a 0.01-0.11 s: 100 damage in a 6.75x6.41x22.5 box at (0,-27,20.6) (anyone on/above the turret) | lowers over 1.57 s keeping the turn | Echoes' |
| Mouse/gyro aim | stick only, rate-limited | direct | our choice (kept) |
| Matches | muzzle start, damage 75/10 m, own platform excluded, yaw 120 deg/s, recentre x4, pitch ±45, hit flash 0.15 s, eject + kill credited to last damager | | |

**Visuals / physical (items 1, 5, 6, 11)**
- Effects (none imported yet): explode PART 934afcf0 at (0,-27,17.555); lift glow EFCT 0x10d PART ef36894f at (0,-27,10)
  Enter t=0 to Leave/Destroyed; lowering EFCT 0x10f PART 529ac6e0 at (0,-27,10) x1.2 from Leave t=0; muzzle PART 795c46cc
  (0 s) + db867cd9 (0.067 s) on muzzleend_LCTR in turret_shoot; ball glow PART a007a9a9 on ball_SDK in ball_up/ball_down.
- Slot arcs: held = EFCT 0x259 ELSC 38390f0b (PART a98ad8e0) at (0,-37.494,7.047), BALT ENTR on / EXIT off; bomb hit =
  EFCT 0x273 ELSC beb6ffdb at (0.25,-39.152,10.053) rot 32.49 deg about X, DTRG DEAD on, TIMR 0x272 off after 0.5 s.
- Hologram glow ACTR 0x109 CMDL d87334fa at (0,-27,10.555) scale (0.6,0.6,0.8). Fades: hologram + glow in 1.0 s / out 0.2 s,
  ring in/out 1.0 s.
- Animation (e749cb49): 0 ball_up, 1 ball_down, 2/3 90_up/down_additive, 4-6 spins, 7 still, 8 rest, 9 shoot. Idle loops
  rest (lowered) / still (raised) with ball_up / ball_down as transitions. Pitch is additive on turretMain_SDK AND ball_SDK
  (our top/ring pieces never pitched).
- Collision: 'Dummy Base' PLAT 0x10e (bounds of CMDL ecf072bc, ±3.6 m, z -7.36..0.56) rides z 3.555 -> 10 over 2.0 s from
  Enter 0.008; 'Dumb Collision Box' ACTR 0x10c solid, shots pass, 5x5x5 x (1,1,2.445) at (0,-27,13.779), Enter t=0 to Leave
  t=0 / Destroyed 0.2 s.
- Slot geometry: BALT (0,-37.5,11.0) ±1.5; ring centre ~(0,-37.82,10.98); DTRG point (0,-37.5,10.809). Our hold = BALT centre.

**Sounds (item 2)**: all 11 heard ids covered. SOND 0x117 Electric FX (0x2690) is placed inactive and never activated
(probably silent in retail). Differences: in multiplayer fire + hit are non-positional to the occupant (vol 127), not at the
turret; the rotation loop is pitch-bent by turn speed (rso 0x1248-0x12f4).

**HUD (item 3)**: FRME_TurretHud2Combat 16e95365 / FRME_TurretHud4Combat 6857838b (NoARAM.pak): ENRG energybart01_hullenergy
(turret hull health, texture fd1a3780), TXPN textpane_title (FONT_Deface13B, STRG b4590ac3 "HullHealth"), MODL model_frame /
model_frame1 / model_reticle (embedded models, textures 394d7ef2, f024e1fa), CAMR fov 30. Shown while the player's turret state
is 3 (0x80224b5c, 0x80232428); the bar follows "Turret Collision"'s health. Fixed reticle, no cooldown indicator, no gun.

**Camera shake (item 4), fixed**: the input step's movement moved a held ball by the stick (and its collision against the
slot walls) before the camera followed it; the ball now goes back to the hold as the camera updates (SlotHoldBall,
PlayerCamera.UpdateCamera). `-echoesturret test` "held, still and rolling" checks all 7 hunters.

### 9.2 Done, 2026-10-16 (needs an Echoes re-import: importer arenas 5)

| Owner item | Status |
|---|---|
| 1 pieces | Hologram glow (ACTR 0x109) drawn while free; 'Dumb Collision Box' column (ACTR 0x10c) is solid to players, shots pass, on Entering/Active and 0.2 s into Destroyed (entity collision slot 1). Whole head pitches (turretMain_SDK + ball_SDK). |
| 2 sounds | Fire + rotation loop non-positional for the occupant; the loop is pitch-bent by turn speed (rso 0x1248). |
| 3 HUD | FRME_TurretHud4Combat: frame, reticle, hull energy bar (CAuiEnergyBarT01 fill/shadow), 'Turret' title in Echoes' Deface font (CampaignRenderer.TurretHud.cs). |
| 4 shake | Fixed (SlotHoldBall) + the slot's own cameras CAMH 0x31/0x2ce, blend in 0.75 s / out 2.0 s. |
| 5 arcs | ELSC 38390f0b while held; ELSC beb6ffdb for 0.5 s on the killing bomb (CampaignRenderer.TurretFx.cs). |
| 6 destroyed | Aim snaps to centre, lift drops (no lowering), explode PART 934afcf0, 'Player Kill' box 0.01-0.11 s. |
| 7 bomb | Laid where Samus's own bomb drops (0.244 below the ball centre), not at the ball centre. |
| 8 range | No distance limit; the shot lives 128 frames. Flat 75 splash inside 10 m. |
| 9 fire rate | 1.0 s from a fresh press, press in cooldown lost: matches 1:1; cooldown counts only while active (not reset on entry). |
| 10 logic | Yaw step skipped past ±90 (not clamped). |
| 11 visuals | Lift glow, lowering, muzzle (795c46cc + db867cd9 at 0.067 s), ball glow, the shot's B1/B2 billboards (WPSC eea4cf05), impact CRSC 9cb622b7. |

Still open after 9.5: PMUS quad particles (a0681474, ef36894f, 8d483388, 0e1ef708); whether the 'Dummy Base' draws its own copy of CMDL ecf072bc. `-echoesturret test` covers held/rolling stillness, the slot
camera and the seated player inside the column, all 7 hunters.

### 9.3 Done, 2026-10-16 13:00 (needs an Echoes re-import: importer arenas 6). On the Odin 13:00:40

- **Pitch easing:** the stick's pitch step is 60 deg/s x (1 - PhongBlob(|gap| / 90, 8)), snapping within 0.1 deg.
- **The lift's base:** Echoes' 'Dummy Base' PLAT 0x10e is a separate solid box, `EchoesTurretBaseEntity`.
  - It is built from arena.json Turret BaseMin/BaseMax: the model bounds of CMDL ecf072bc about the PLAT, which is
    what the loader uses for its zero CollisionBox.
  - It rises BaseRise (3.82 units: z 3.555 to the Top waypoint's z 10) over BaseTime (2 s, its MotionSplineDuration)
    while the turret is Entering or Active, and lowers otherwise.
  - It snaps to the bottom on Destroyed (Echoes' RSET) and carries whoever stands on it.
  - It needs its own entity because an entity has two collision slots, and the turret already uses both.
- **Knockback:** after a turret shot damages a player, `BeamProjectileEntity.HostHitPush` calls `EchoesTurret.Knock`,
  which calls `PlayerEntity.HostKnock`.
  - Power: 20 on a direct hit; (1 - distance / radius) x 20 in the splash (ApplyRadiusDamage 0x80243a70).
  - Direction: flat. Along the shot on a direct hit, from the shooter in the splash.
  - ApplyPlayerKnockBackForce 0x8015c210, in order:
    1. The impulse is P x 500 on mass 200, so 2.5 m/s a point.
    2. It is divided by 3.5 for a body on foot in the air (surface restraint Air). The morph ball is never divided.
    3. The cap is max(40 on foot, 35 in the ball; the speed before).
    4. The speed is min(cap, the new speed), along the new velocity.
    5. On foot only, that speed is scaled by 0.65 + 0.35 |n . axis|. The axis is the facing on the ground and the right
       in the air.
    6. SetMinimalAccelerationTimer(clamp(P / 50, 0.1, 0.6)) is set.
  - While that timer runs, a body's own stick acceleration is x 0.074. Afterwards it ramps 0.296, 0.296, then full a
    frame apart (table at CPlayer+0x338). Echoes reads this on foot only.
  - Not modelled: Echoes' over-speed bleed in the air (speed - 0.074 (speed - max) while the timer runs). MphRead's own
    over-cap rule stays.
- **Fades:** the hologram and its glow fade in over 1.0 s when the turret comes free and out over 0.2 s when it's taken.
  The slot's ring fades in and out over 1.0 s.
  - They are additive, so the fade scales their colour (uExposure per prop in CampaignArena.DrawProps).
  - The first fade logs each pass's blend once ("faded prop pass"), to confirm that they are additive.
- Tests: `-echoesturret test` is all ok for all 7 hunters, including:
  - the base rising (top at 6.26) and dropping on destroy;
  - an air hit's knockback (0.183 units a tick, 0.65 side factor, 0.4 s steering cut).
### 9.4 Done, 2026-10-16 13:14 (needs an Echoes re-import: importer arenas 7). On the Odin 13:14:20

- **Model particles (PMDL):** the lift's spinning rings (6f3a4416 lift, 529ac6e0 lower: CMDL 12771af0) and the smoke's
  model puffs (db867cd9, 42641ad8, 83eac518, aad1d09b: CMDL 7c59f744) now draw.
  - Import: ImportFx runs before the arena HD write and places each PMDL CMDL as its own pass (turret.json ModelPasses).
  - Runtime: `RetroParticleSystem.CollectModels` gives each live model particle's transform, per Metaforce
    CElementGen::RenderModels: scale PMSC, rotate PMRT (degrees, x then y then z), offset PMOP, then the system's own
    turn (none under PMOO) and the particle position. The renderer draws it as a prop: additive (PMAB), no depth
    write, recoloured by PMCL (brightness x colour), faded by the particle's alpha. Unlit; COLR/SIZE are sprite only.
  - `-echoesturret test` steps every effect 40 frames and checks each placed model is drawn by some effect.

### 9.5 Bots, 2026-10-16 13:30 (owner D7 'Sometimes' man a free turret, D8 'Shoot + bomb slot'). On the Odin 13:30:20

Echoes has no bots, so this is ours. Replays before format 41 play without it (Settings.EchoesTurretBots).

- **In the seat** (`EchoesTurretEntity.BotSeat`): the closest enemy the barrel can reach (its turn and pitch limits, the
  shot's range, nothing between the muzzle and them), re-picked every 0.5 / 0.25 / 0.13 s by bot level. She aims where
  they will be when the shot arrives (their speed x the flight time), off by up to 5 / 2.5 / 1 degrees (new each pick),
  at the turret's own turn and pitch speeds, and fires within 6 / 4 / 2.5 degrees. Out after 5 s with nobody in sight,
  or 30 s in all. The AI's own Shoot/Jump presses don't fire or leave for her.
- **On foot** (`EchoesTurretBots`, through `PlayerEntity.HostBotErrand`, PlayerAiData.TurretErrand after the AI's step):
  - a free turret within 30 units: 30% of the time she goes for it (else not for 10 s), along the nav graph's next-hop
    table (MphRead's Func213A1A8 table), jumping its jump links (a second press for double-jump links); straight in once
    close, level and in sight; out of the ball first. One bot at a time; gives up after 20 s, then 20 s off.
  - an enemy in the turret, the bomb slot within 30 units: 50% (else 8 s), the same route, the ball once close, a bomb
    every 20 AI steps once the slot holds her; gives up after 20 s, then 15 s off. Shooting the occupant is her own AI's.
  - the dice and clocks go by the turret's step count and her slot (no RNG drawn).
- PC: `-echoesturret test` puts a bot in the seat (it picks and fires at a player in front in 2.5 s; out 5 s after
  they hide). `-echoesturret botprobe level=2 seconds=240 put=0`: 3 bot entries; `occupy=1` (the idle player kept in
  the seat): destroyed twice in 180 s, a bot held in the slot 3 s.


### 9.6 Round 2, 2026-10-16 15:20-16:00 (owner: no explosion, blast radius, view too far out, rise/exit feel, bots)

- **No explosion:** the impact flash's ATEX flipbook takes its frame count from ILPT (a share of the particle's own
  lifetime); we evaluated it with lifetime 1, so every frame showed tile 0 (a dot). RetroParticles now works the cycle
  per particle (`Cycle()`), as Metaforce's CUVEAnimTexture::GetValueUV does. The PMUS squares (the flash quads) draw
  depth-tested, as Echoes (clipped inside the target recess).
- **Blast radius:** 10 m = 5.93 units, measured to a crate's bounds: crates break from blasts up to 5.5 flat, not at
  6.5 (`-echoesturret beamhit`).
- **Bots never hurt the manned turret:** their sight ray ended on the turret's shell (PlayerAi now counts a ray that
  ends on the occupant's own shell, `HostSeatShell`); shots hit the occupant's body poking out of the front (beams now
  skip a seated occupant); half the DCLN's triangles face in and MPH collision is one-sided (each now faces out from
  the mesh's centre). botprobe: level 0 lost 552-1116 HP and destroyed it 1-2x in 66-114 s; level 1-2 2-4x.
- **Kanden morphing over and over:** both touch buttons switch forms; the AI's own CheckUnmorph pressed Unmorph the
  step after the errand's Morph. The errand clears both, and a slot run stays rolling once close.
- **View / exit:** see Seat camera and Normal exit above.

### 9.7 Round 3, 2026-10-16 16:40-17:31 (owner video: explosions in the sky, flat textures painted on the ground, grates break in MPH's effect). On the Odin 17:08:27 (impact) and 17:33:56 (grate break, importer arenas 8)

- **Impact:** RetroParticles read ASPH's arguments in the wrong order (the squares stood up off the hit and drifted
  into the sky); particles lived past their own lifetime (repainted flat on the ground); PSTS (time scale) wasn't
  applied. All three fixed against Metaforce. The occupant's hit flash is off while seated; the green wash in the
  seat was the bot's MPH beam hits, not the turret.
- **Grates break as Echoes:** the grating's DEAD fires GENR 'Generator 003' (in the generated layer:
  `ReadScripts(includeGenerated: true)`), which places its 8 DebrisExtended (DBR2) at the grating and activates them:
  7 grate chunks (2-3 s, a cone at 5-15 m/s, spin, gravity 25, faded in 10% / out from 80%) and one 0.1 s piece whose
  death particle 741b9b1d is the big white-pink puff. Imported into turret.json as Fx kind debris (`EchoesTurret.
  GrateDebris`), run by `CampaignRenderer.TurretFx.cs` (GrateBit) as CScriptDebris does. The generator's PLAY SOND is
  sfx 3587 in AGSC 'MetalDebrisMed' (not in the room's groups; the importer now searches every AGSC in the paks;
  `-echoesturret sfxfind`). MPH's own break effect still runs (hidden, so replays stay the same) and its sound is
  skipped (`EchoesBreakables.HostOwnGrateBreak`).
- **Checked and already right:** the target panes glow on firing as in the video.
- **Open:** turret state isn't in the replay checksum.
