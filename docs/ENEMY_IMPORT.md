# Enemy import: First Hunt + Prime 1/2/3/4 (scope and plan)

Owner, 2026-10-26: import enemies from First Hunt and Prime 1, 2, 3 and 4, ported 1:1 with all their logic, moves and
effects, to the standard of the Shooting Gallery turret. They serve three purposes:

1. **Hard Mode (priority).** A new campaign, written by the owner.
2. **Later: a campaign randomizer.** It puts enemies in random rooms from pools the player chooses, adds new gates,
   brings back mini bosses, and adds new enemies to rooms.

This is the plan. Nothing is built yet. Research done for it (2026-10-26): a survey of the code we already have and a
survey of where each game's enemy logic can be read from. The method being copied is in
[SHOOTING_GALLERY_TURRET.md](SHOOTING_GALLERY_TURRET.md).

## 1. What "1:1" means here (the turret standard)

An enemy counts as ported when all of the following hold:

- **Logic comes from the game's own code.** Every state, timer, speed, range, damage number and decision is read from
  the executable: disassembly, plus names where a build has them. Addresses go in the enemy's notes. Nothing is
  guessed from videos.
- **Numbers come from the user's disc at import time.** That covers the script object's per-instance values (health,
  speeds, vulnerabilities, scale) and the tweak values. Nothing from a disc ships with the app (ship-safe rule).
- **Its look is the game's.** The skinned model, every animation clip, the anim-event timeline (sounds, particles and
  locators at the right frames), its particles, swooshes, electric effects, projectiles, decals and sounds.
- **It has a PC test** that drives it through every state, like `-echoesturret test`.
- **It has a side-by-side look check** against the original in Dolphin (or melonDS for FH), judged by eye at full size.
- **Units are converted once.** Retro metres become MPH units at the turret's ratio (10 m = 5.93 units). Times are
  converted to 60 Hz frames, or kept as seconds where MphRead uses seconds.

## 2. Where each game stands

| Source | Enemies (rough) | Where the logic is | Names | What we can read today | Verdict |
|---|---|---|---|---|---|
| **MPH** | 42 classes, incl. bosses | MphRead (already C#) | n/a | everything | **Usable in Hard Mode now** |
| **First Hunt** | 7 (Zoomer, War Wasp, Metroid, Mochtroid 1-4), no bosses | cart ARM9 0x20222a4-0x202806c | none, but they are MPH's ancestors | **Already ported** (FH P2, 10-14, `src/MphRead/FirstHunt/Enemies/`) | **Usable after a small adapter** |
| **Echoes** (GC retail + Trilogy) | ~50 + ~10 bosses | GC: `RelProd/*.rel` modules + DOL. Trilogy: `RSO_*` modules (named imports/exports; `RSO_Grenchler`, `RSO_Splinter`, `RSO_ElitePirate`, `RSO_DarkSamus`, `RSO_rhbm` seen) | partial: every DOL call a module makes is named | models, skeletons, animations, ANCS, EVNT, PART/ELSC/SWHC/WPSC, MREA with script layers, sound | **Best source.** Same method as the turret |
| **Prime 1** (GC) | ~45 + 9 bosses | all in the DOL | none locally | models, skeletons, animations, ANCS, EVNT, particles, sound. **No MREA/SCLY reader** | Doable. Needs names: see decision D3 |
| **Corruption** (Wii + Trilogy) | ~45 + 8 bosses | DOL + RSOs (to verify) | RSO imports/exports, if they are like MP2's | CMDL/CHAR/SKIN/ANIM (v1 only), sound. **No MREA, no PART v3, no CHAR events** | Doable after reader work |
| **Prime 4** (Switch) | unknown | `main` NSO (AArch64), new engine | none (dynamic-link names only) | models, skeletons, animations, textures. **No effects, sound, script or AI** | **Not realistic 1:1 yet.** Research spike per enemy, last |

Rosters for Prime 1-4 above were from general knowledge. R1 (done 2026-10-26) replaced them with real lists from the
discs: see section 10.

## 3. What we already have, and the gaps

**Already in the tree:**
- **An enemy base class.** `EnemyInstanceEntity` handles health, per-beam effectiveness, death sound and effect, and
  drops.
- **A way for non-MPH entities to plug in.** The turret (`EchoesTurretEntity`) and the Echoes breakables already
  subclass the enemy base class and enter rooms through `SceneSetup.HostExtraEntities`. The campaign builds an
  ordinary MphRead Scene, so that hook runs there too.
- **A skinned Retro body runtime.** `NativeBody` + `NativeBodyAnimator` (Pirate Trooper, Dark Samus) do GPU skinning, clip
  cross-fades, a layered torso and GX TEV materials.
- **Retro effect runtimes, ported from Metaforce (MIT):**
  - `RetroParticles` (PART);
  - `RetroSwoosh` (SWHC, the tube kind only);
  - `RetroElectric` (ELSC).
- **Import readers.**
  - Prime 1/2: CMDL, CINF, CSKR, ANCS, ANIM, PART, ELSC, WPSC, EVNT, MusyX sound, and Echoes MREA (collision + script
    layers).
  - Prime 3: CMDL3, CHAR3, SKIN3, ANIM3 v1, CAUD/CSMP.
  - Prime 4: SMDL, CHPR, skeletons and animations.
- **Bot navigation for any room** (`ArenaNav`). Prime enemies path along PATH assets that MPH rooms don't have, so
  this is their stand-in.

**Gaps, all shared work that every enemy needs:**

| Gap | Needed for |
|---|---|
| Enemy clip driver. NativeBody is driven by the biped player state today; an enemy needs its own (play, blend, loop, events). | all Retro enemies |
| EVNT dispatch at runtime: sounds, particles and locators fired on frames | all Retro enemies |
| Particle gaps: lights, line particles, motion blur, collision-spawned children; the other SWHC kinds; DPSC decals; CRSC impact responses (only the DEFS entry is read today) | most effects |
| Retro physics actor on MPH collision: gravity, velocity, move-and-slide, collision primitives (box/sphere/joint spheres), knockback | anything that walks or flies |
| Damage table: Prime vulnerabilities (Power/Wave/Ice/Plasma/missile/charge/bomb/...) mapped onto MPH's weapons | all Prime enemies (D2, section 8) |
| Script-instance import: Prime 1 MREA/SCLY reader; Corruption MREA reader | Prime 1 and Corruption values |
| AFSM/FSM2 state-machine reader (Prime 1/2 use these for some AI) | some MP1/MP2 enemies |
| Prime 3: ANIM3 bitstream v2, CHAR3 events, PART/ELSC/SWHC v3 | Corruption |

## 4. Architecture: one system for all three uses

```
          \                    |                        /
           +------ Campaign placement data (per room) --+
                               |
          Enemy catalogue (ids like mph:Zoomer, fh:Metroid, mp2:Grenchler, mp1:SpacePirate)
                               |
   MPH/FH enemies (MphRead)        RetroEnemyEntity : EnemyInstanceEntity
                                   + per-enemy logic class (ported from the game's code)
                                   + Retro actor kit (body, clip driver, EVNT, particles,
                                     physics, damage table, nav, projectiles, sounds)
                                   + imported data (<data>/enemies/<game>/<Enemy>/)
```

- **Catalogue.** One entry per enemy and variant: its id, its source game, the import it needs, default instance
  values (taken from a real placement in its own game, e.g. "Grenchler as in Torvus Bog"), size class (fits a small
  room / needs a big room), abilities it needs (see D4), and a status (ported / look only / not started).
- **Campaign placement data.** Per campaign and per room. It holds:
  - enemies: catalogue id, position, facing, overrides for health, damage, speed and drops, and a spawn trigger
    (on entry, on a volume, wave N);
  - gates: doors locked until a group is cleared, the "mini boss" kind;
  - removals of the room's own enemies.

- **One spawn path.** The placement hook adds entities through `SceneSetup.HostExtraEntities`, as the turret does.
  MPH enemies go through MphRead's own `EnemySpawnEntity.SpawnEnemy`, so they behave exactly as vanilla.
- **Network-ready state.** Campaign is single player, so no netcode now. But each Retro enemy keeps its state small
  and in one struct, like `NetTurret`. Co-op, replays or a shared randomizer seed can then sync it later without a
  rewrite.

## 5. Hard Mode first: what the owner needs and when

The owner can **start writing Hard Mode before any Prime enemy is ported**, because MPH's 42 enemies (bosses and
guardians included) and FH's 7 work through the same file. Imported enemies then join the catalogue one by one, and
the owner drops them into rooms as they land.

Hard Mode needs, in order:
1. **The placement file + hook in the campaign**, plus a HARD MODE campaign slot. That means its own saves and its own
   start, and normal campaign saves are untouched.
2. **An in-game enemy placer** on the dev menu, the same idea as the pickup placer (`PickupPlacer` /
   `PickupPlacement`):
   - D-pad picks the enemy;
   - Select places it at the aim point;
   - another button deletes the nearest;
   - a small panel edits the overrides and the trigger.

   It saves to the Hard Mode file on every change. The owner judges placement by playing, so this matters more than a
   text format, though the file stays hand-editable.
3. **Gates.** Lock a room's doors until a group is dead, using MPH's own door-lock messages. A "mini boss" gate is one
   of these with a boss-class enemy and its health bar.
4. **Room-own enemy edits.** Remove, replace or buff the room's existing spawners.

## 6. Phases

| Phase | What | Done when |
|---|---|---|
| **R1** | Census from the discs. A scratch tool lists every RSO/REL module (trilogy.rvz, echoes_retail.rvz, corruption.rvz) and every enemy script-object type per game (Echoes: 185 object types already dumped in `slot_re/fac_ret.txt`). **Done 2026-10-26, section 10.**. Output: the catalogue table with a source tier per enemy (named module / DOL unnamed / needs AFSM / needs abilities). | Owner has the full enemy list to pick from |
| **H1** | Hard Mode foundation with MPH + FH enemies: placement format, campaign hook (lifts the multiplayer-only gate for campaign files), HARD MODE slot, enemy placer, gates, room-own edits, FH enemy adapter for MPH rooms. PC test `-enemyplacetest`. | Owner can author Hard Mode on the Odin |
| **K1** | Retro actor kit, pass 1: `RetroEnemyEntity`, enemy clip driver over NativeBody, EVNT dispatch, physics actor, damage table, ArenaNav pathing, WPSC projectiles, sounds. | A pilot enemy stands, walks, attacks and dies 1:1 |
| **E1** | **Echoes wave.** Pilot = one simple enemy with its own named RSO (Grenchler or Splinter, picked from R1). Then the owner's Hard Mode picks, then the rest. Kit pass 2 as effects need it (particle gaps, CRSC, DPSC, SWHC kinds). | Each enemy passes its PC test + look check |
| **E2** | **Prime 1 wave.** Prime 1 MREA/SCLY reader, AFSM reader if needed, names per D3. Same order: pilot, owner's picks, rest. | same |
| **E3** | **Corruption wave.** MREA3, ANIM3 v2, CHAR3 events, particle v3. | same |
| **B1** | **Bosses as mini bosses**, per owner pick. Each is big and room-bound, so each gets its own scope. Big ones need a big enough room, or a built one later. | per boss |
| **P4** | **Prime 4 spike.** Read the NSO for one chosen enemy and see what 1:1 costs. Effects, sound and script readers come first. | Owner decides go/no-go per enemy |
| **Later: RZ** | Randomizer: pools UI, a seeded generator writing the placement file, a beatability check (the campaign's item and door graph, so new gates never softlock), mini-boss gate placement. | |

**Effort, honestly.** The turret took one long session (owner, 10-26). A simple enemy should be about that size.
A Space Pirate-class enemy with several attacks is likely two or three such sessions, and a boss more than that. MP1-3 hold about 150
regular enemies and 27 bosses, so "all of them" is a long track. The plan therefore ports in the order Hard Mode needs
them, with the shared kit paid for once.

## 7. Owner decisions

**Decided 2026-10-26: the owner took every recommended option below** (D1 Echoes → Prime 1 → Corruption →
Prime 4; D2 one owner-set table, overridable per placement; D3 (a) Metaforce as the credited reference, checked
against the retail DOL; D4 leave out by default; D5 (b) fallback enemy per placement; D6 true size; D7 GameCube
retail is ground truth).

**Hunter decisions (owner 2026-10-30):**
- **Grapple:** adapt the grapple hunters now, and build a real grapple beam as its own project (the owner wants grapple
  for everyone anyway, starting with the Shooting Gallery grapple point). Plan: docs/GRAPPLE.md.
- **Hypermode / Phazon:** hunter-only. The hunter enters Hypermode and uses its Hypermode attacks; the player gets no
  Hypermode.
- **Echoes Dark Samus' Freeze Beam:** adapt Echoes' own freeze (its freeze effect and timing, read from the disc) onto
  the MPH player, marked as an adaptation.
- **First hunter port:** Echoes Dark Samus.

- **D1. Order of games after H1.** Recommended: Echoes, then Prime 1, then Corruption, then Prime 4. Echoes has the
  best source (the turret's method), and Prime 4 has no readable logic yet.
- **D2. Damage mapping.** How Prime's beam weaknesses map onto MPH weapons. One table for everything, set by the owner
  for Hard Mode, overridable per placement. Worked out in section 8 (families, rule, damage, tint, softlock check;
  owner picks V1-V3).
- **D3. Prime 1 names.** The retail DOL has no symbols. Options:
  - (a) Use Metaforce (MIT, already credited for our particle runtimes) as the credited reference for class layout
    and names, with every number and behaviour checked against the retail DOL.
  - (b) Raw RE of the unnamed DOL only. Slower.

  PrimeDecomp-style code is never copied (the NcsfPlay precedent); at most a spec.
- **D4. Enemies that need abilities MPH doesn't have.** For example, Chozo Ghosts need the X-ray visor, Ing need
  Light Beam and dark-world rules, and some Corruption enemies need grapple or Hypermode. Per enemy, the owner can
  leave it out, adapt it (a stated, owner-approved change, marked as such in the catalogue), or add the ability as its
  own project. Recommended: leave these out by default. The R1 census flags them ("needs X-ray", "needs Light Beam"),
  and they wait until the owner wants one for Hard Mode. Add an ability as its own project only when several wanted
  enemies need it. Adapt only where the change doesn't alter how the enemy is fought (e.g. a grapple point it never
  uses against the player).
- **D5. Missing discs.** If a Hard Mode room uses an enemy from a game the player hasn't imported:
  - (a) Hard Mode needs those discs;
  - (b) each placement names a fallback enemy;
  - (c) the enemy is skipped.

  Recommended: (b). It keeps Hard Mode playable with only the MPH cart.
- **D6. Scale.** Prime enemies are sized for Prime rooms. Options: keep true size at the turret ratio (recommended,
  the 1:1 choice; big ones then only go in big rooms), or allow per-placement scale as a Hard Mode override.
- **D7. Trilogy vs GameCube.** Same rule as the turret: the GameCube retail disc is ground truth, and Trilogy is
  accepted where it imports identically.

## 8. Vulnerability translation (D2 in detail)

Prime enemies are weak or immune to weapons the hunters don't have: Wave, Ice, Plasma, Dark, Light, Annihilator,
Nova, Power Bombs, Boost Ball, Screw Attack, Phazon/Hyper. This section is how each of those becomes an MPH weapon,
without losing the enemy's own reactions.

### 8.1 What each side has

- **MPH:** each enemy has 9 weapon rows (Power Beam, Volt Driver, Missile, Battlehammer, Imperialist, Judicator,
  Magmaul, Shock Coil, Omega Cannon), each set to one of 4 levels: `Zero`, `Half`, `Normal`, `Double`
  (`EnemyInstanceEntity.BeamEffectiveness`, `Effectiveness` enum). Bombs skip the table and do a fixed amount
  (`CheckHitByBomb`). Our added weapons (Pirate Blaster, Hyper Beam, Paralyzer) borrow a vanilla row
  (`GetEffectiveness` → `PirateBlaster.BaseIndex`).
- **Prime 1/2:** each enemy has a vulnerability record per damage type. It has separate entries for uncharged shots,
  charged shots and charge combos, and levels richer than MPH's: weak, normal, immune, **deflect** (the shot bounces
  off with its own effect and sound), pass-through, and **direct-only** variants (splash doesn't count). Some enemies
  also change their table by state, e.g. armour that comes off. The exact fields and multipliers are read from the
  code in K1. Per-enemy values come from the disc.
- **Prime 3:** per beam (Power, Plasma, Nova), missiles (plus Ice Missile and Seeker), Hyper Beam/Missile/Ball, Grapple
  Lasso/Voltage, Screw Attack, Boost Ball, bombs. Confirmed in E3.
- **Prime 4:** unknown until its P4 spike.

### 8.2 The rule: translate the hit, keep the enemy's table

An imported enemy **keeps its game's own vulnerability table, untouched**. When an MPH weapon hits it, the hit is
translated into that game's damage type (plus charged/uncharged), and the enemy's own ported code decides what
happens: damage, a deflect spark, ignoring splash, a stagger, armour breaking. Squashing Prime's levels down to MPH's
four would lose deflects and direct-only hits, so the translation happens at the hit instead. That keeps the
behaviour 1:1.

Each MPH weapon maps to **exactly one** damage type per game. Several MPH weapons may share one type. Any MPH weapon
the game has no counterpart for counts as that game's **Power Beam**, so an enemy immune to Power is immune to it
too.

### 8.3 The families (owner's table: decided 10-26, edit any row any time)

**This table is the owner's master copy.** Change any row here whenever you like. Whoever builds E1 turns it into
`enemies/damage_map.json` (8.7) and keeps the two in step, and after that the same rows can be changed in the JSON file
directly with no rebuild. MPH weapons are grouped by what they do, and each family stands in for the Prime weapons
that do the same job:

| Family | MPH weapon / attack | Prime 1 | Echoes | Corruption | Why |
|---|---|---|---|---|---|
| Basic | Power Beam | Power | Power | Power | same weapon |
| Electric | Volt Driver, Shock Coil | Wave | Annihilator | (Power) | electric, arcs/homes like Wave; Annihilator is Echoes' Wave-like homing beam |
| Cold / stopping | Judicator | Ice | Dark | Ice Missile (charged Judicator) | freezes/holds: Ice freezes, Dark Beam entangles |
| Heat | Magmaul | Plasma | Light | Plasma | burns: Plasma and Light both set targets alight |
| Precision | Imperialist | (Power) | (Power) | Nova | piercing long-range shot, like Nova |
| Missile | Missile (charged = homing) | Missile (charged → Super Missile) | Missile (charged → Super Missile) | Missile (charged → Seeker) | same weapon |
| Heavy | Battlehammer | (Power); counts as **Bomb** for bomb-only targets | same | same | explosive splash |
| Ultimate | Omega Cannon | best entry in the table | best entry | best entry | top weapon; never the *only* answer (it's rare in the campaign) |
| Phazon | Hyper Beam (our Dark Samus weapon) | Phazon | Phazon | Hyper Beam | same thing |
| Other added | Pirate Blaster, Paralyzer | Power | Power | Power | as today (Power row). The Paralyzer's stun only applies if the enemy has its own stun/freeze state |
| Bomb | every alt form's bomb or shot: Morph Ball bombs, Lockjaw bombs, Stinglarva, Halfturret | Bomb | Bomb | Bomb | so bomb-only enemies stay killable by every hunter |
| Contact | ramming alt forms: Boost Ball, Dialanche, Triclaw, Vhoscythe | Boost Ball | Boost Ball / Screw Attack | Boost Ball / Screw Attack | body attacks |
| Power Bomb | charged Battlehammer and Omega Cannon | Power Bomb | Power Bomb | (none) | biggest blast we have |

The charge rule: an MPH weapon's charged shot uses the charged entry of its mapped type. Prime's charge combos
(Wavebuster, Ice Spreader, Flamethrower, Sonic Boom, Darkburst, Sunburst) are only reachable where an MPH charged shot
is the natural match, as in the Missile row. Any other combo-only weakness falls under D4.

**Not translatable (D4, leave out by default):** X-ray-only and Echo-visor-only targets, Grapple Lasso/Voltage-only
weaknesses, light/dark-world rules (Ing in dark water, safe zones).

### 8.4 How much damage

Vulnerability is "which weapons hurt"; damage is "how much". The two are kept separate:
- **Anchor the health on the Power Beam.** An enemy keeps the same number of uncharged Power Beam shots to kill as in
  its own game: its MPH health = Prime health × (MPH Power Beam damage ÷ that game's Power Beam damage). Both values
  are read from the disc and the ROM, not typed in. Every other weapon then does its own MPH damage × the translated
  multiplier, which keeps MPH's weapon balance.
- **Enemy damage to the player stays raw.** Both series count energy in 99 plus 100 per tank. Hard Mode can multiply
  it per placement.
- **No MPH headshots on imported enemies**, matching MPH's own enemies. Their weak points (a mouth, a core, armour
  gaps) come with their own ported logic.

### 8.5 Readability: coloured shields

Some enemies show their weakness by colour: Prime 1's Power/Ice/Wave/Plasma-shielded troopers, Echoes' Dark- and
Light-only enemies, beam barriers. An MPH player can't know "purple = Volt Driver". Options:
- (a) keep the original colours (purest 1:1);
- (b) **tint the coloured part to the mapped MPH weapon's colour**, as MPH's own beam-locked doors are, behind a setting.

Recommended (b), on by default. It changes nothing but the tint on the parts the game itself colour-codes.

### 8.6 Softlock safety

A weakness is only fair if the player can have that weapon when they meet the enemy. That goes for every hunter,
since the campaign allows any hunter after the first clear.
- The enemy placer shows, for each placed enemy, which weapons and alt forms can hurt it. It **warns** when none of
  them is owned on reaching that room on the normal route. The Bomb and Contact families make sure every hunter's alt
  form counts.
- The randomizer (RZ) uses the same check as a hard rule.
- Omega Cannon never counts as an answer, because it's only in the final fight.

### 8.7 Where it lives

- `enemies/damage_map.json`: our own text file, safe to ship. It holds the table above per game, and the owner can
  edit it.
- A per-placement override in the campaign placement file (e.g. "this Hard Mode trooper: Ice → Imperialist").
- PC test `-vulntest`: for every catalogue enemy, prints the matrix MPH weapon → translated type → result
  (damage / deflect / immune) and fails if an enemy can't be hurt by anything outside Omega.

### 8.8 Owner picks for this section

**Decided 2026-10-26: the owner took every recommendation** (V1 the table as proposed, V2 tint on by default, V3
Pirate Blaster keeps the Power row), with the table in 8.3 kept editable for later changes.

- **V1.** The family table in 8.3 as proposed. In particular: Dark → Judicator and Light → Magmaul (Echoes);
  Annihilator → Volt Driver rather than Omega; Shock Coil sharing Volt Driver's family.
- **V2.** Shield tint (8.5): recommended (b), on by default.
- **V3.** Pirate Blaster: keep the Power row (recommended), or count it as Prime's enemy-weapon type. That's more
  1:1, but most Prime enemies ignore their own side's fire, so the Pirate Trooper couldn't hurt pirates.

## 9. Next step

1. ~~Owner answers D1-D7.~~ Done 10-26: all recommended options. D2's actual table is still the owner's to fill in
   before the first Prime enemy (E1); it isn't needed for R1 or H1.
2. ~~Run R1 (the census) and H1 (the Hard Mode foundation) as two threads.~~ R1 done 2026-10-26 (section 10); H1 is
   its own thread. The owner now picks enemies from section 10 for E1 (Echoes first, per D1).
3. 2026-10-30: the goal is every enemy and hunter of every Prime game (section 11). Work that needs no owner is
   queued in 11.2, items 1-8.

## 10. Enemy catalogue (R1 census, 2026-10-26)

Produced by scratch tools only (no app code, nothing installed). Sources: `trilogy.rvz`, `echoes_retail.rvz` and
`corruption.rvz` unpacked to a scratch folder; the Echoes pak files (every room's MREA, 72 enemy types with
instances); the PWE templates (`MP1`/`MP2`/`MP3`, research reference only, the licence is unchecked, nothing here
ships). Echoes is the only game whose damage data was read; Prime 1 and Corruption damage tables need the readers
from E2 and E3.

### 10.1 How to read the tables

- **Tier** (how much is already in hand for an enemy):
  - **named module**: the logic is in its own REL/RSO with symbol names (Echoes, Corruption).
  - **named module + needs AFSM / FSM2**: as above, and the patterned AI points at one AFSM or FSM2 state-machine
    asset, so the state-machine reader (section 3 gap) comes first. Swarms and non-patterned types are "code only".
  - **DOL unnamed**: logic only in the DOL, no names (all of Prime 1). Names come from Metaforce per D3.
  - **needs abilities**: the enemy's fight relies on a Samus ability or visor that D4 leaves out by default. These
    flags are from game knowledge, marked "(knowledge)", not from the data.
- **Type** is the FourCC script-object type; **SP** is the number of placed instances found in the single-player
  paks (a type not in the SP data at all is marked).
- **Health** is the value stored on instances, with `x-counts` when instances differ. "none set" means the instance
  does not store one (the archetype default applies).
- **Damage notation** (10.4): percent of normal damage per Echoes weapon slot, 100 = normal, 200 = weak, 50 =
  resists. Only slots that differ from the default are listed; "Hurt by" is the set of MPH families from 8.3 that
  still do damage after immunity, reflect and pass-through are removed.
- **Flags** are the weaknesses section 8 cannot translate: combo-only weapon slots (D4), world rules (light/dark
  world), visor and grapple conditions, and enemies nothing in the 8.3 table can hurt.
- Unnamed type slots in the games' type lists (scripting helpers, triggers, cameras) are not shown.

### 10.2 Module inventory per disc

| Disc | DOL | Modules | Notes |
|---|---|---|---|
| `echoes_retail.rvz` (GC retail, D7 ground truth) | `default.dol` | 86 `RelProd/*.rel` | Two more than Trilogy MP2: `IngSwarm`, `WallCrawlerSwarm`. |
| `trilogy.rvz` | `rs5fe_p`, `rs5mp1_p`, `rs5mp2_p`, `rs5mp3_p` | `fe`: 4 RSO (`ScriptGUIScreenFrontEndUI`, `ScriptWorldTeleporter`, `ScriptWorldTeleporterAttributes`, `Tweaks`) + `rhbm` + `selfile.sel`. `MP1`: `rhbm` + `selfile.sel` only (no per-enemy modules). `MP2`: 84 modules + `rhbm` + `selfile.sel`. `MP3`: 73 modules + `rhbm` + `selfile.sel` | The MP3 set is the same file list as the Corruption folder below. |
| `corruption.rvz` | not extracted | `RSO/Rev2 Production`: 73 modules + `rhbm` + `selfile.sel` (75 files) | The DOL could not be pulled out of the Wii image yet. RSO names are matched by file name because the MP3 templates carry no module list. No RSO exists for Korakk or SamusForm. |

Echoes retail REL list (86): AIMannedTurret, AtomicAlpha, AtomicBeta, BacteriaSwarm, Blogg, ChozoGhost, CommandoPirate,
DarkCommando, DarkSamus, DarkSamusBattleStage, DarkTrooper, DestructibleBarrier, DigitalGuardian, ElitePirate,
EmperorIngStage1, EmperorIngStage2Tentacle, EmperorIngStage3, EyeBall, FishCloud, FlyerSwarm, FlyingPirate, FogOverlay,
ForgottenObject, GeomBlobV2, Glowbug, Grenchler, GunTurret, Ing, IngBlobSwarm, IngBoostBallGuardian, IngPuddle,
IngSnatchingSwarm, IngSpaceJumpGuardian, IngSpiderballGuardian, IngSwarm, Kralee, Krocuss, Lumite, MediumIng, Metaree,
MetareeSwarm, Metroid, MinorIng, MysteryFlyer, OctapedeSegment, Parasite, PillBug, PirateRagDoll, PlantScarabSwarm,
PuddleSpore, Puffer, Rezbit, Ripper, RubiksPuzzle, SandBoss, Sandworm, ScriptCannonBall, ScriptCoin,
ScriptFrontEndDataNetwork, ScriptGui, ScriptPlayerActor, ScriptPlayerProxy, ScriptPlayerTurret, ScriptRiftPortal,
ScriptRsfAudio, ScriptSafeZone, ScriptStreamedMovie, Shredder, Shrieker, SkyRipple, SnakeWeedSwarm, SpacePirate,
SpankWeed, Splinter, Splitter, Sporb, StoneToad, SwampBossStage1, SwampBossStage2, SwarmBasics, Tryclops, Tweaks,
WallCrawler, WallCrawlerSwarm, WallWalker, WispTentacle.

Corruption RSO list (75): AtomicAlpha, AuroraUnit1, AuroraUnit2, BeastRider, Berserker, BlinkWolf, CombatTrooper,
DarkSamus, DarkSamusEcho, DefenseMechanoid, EyePod, FargullHatcher, FargullHatcherSwarm, FlyingPirate, Friendly,
Gandrayda, GellSac, Ghor, GragnolFlyer, GunTurret, KorbaMaw, MapScreenWebInterface, Metaree, MetroidHatcher,
MetroidHopper, MetroidPhazeoid, MinorIng, MysteryFlyer, NoseTurret, Parasite, Phaazoid, PhazonHarvester, PhazonLeech,
PhazonPuffer, PillBug, PirateDrone, Puffer, ReptilicusHunter, rhbm, Ridley1, Rundas, ScriptAchievement,
ScriptActorMorph, ScriptAIFuse, ScriptCable, ScriptContextActionButtonPressing, ScriptContextActionWelding,
ScriptContextSensitiveAction, ScriptContextSensitiveActivator, ScriptGrapplePoint, ScriptGUIScreenFrontEndUI,
ScriptLODController, ScriptMultiModelActor, ScriptPhysicsDebris, ScriptSnagVineHelper, ScriptTeamAiMgr, ScriptWater,
ScriptWorldTeleporter, ScriptWorldTeleporterAttributes, SeedBoss1, SeedBoss2, SeedBoss2Bot, SeedBoss3, ShellBug, Ship,
SpacePirate, Sprite, SteamBot, SteamDrone, SteamLord, SwarmBot, Tweaks, VenomWeed, selfile.sel.

Prime 1 has no per-enemy modules at all: every enemy lives in the DOL (10.6). First Hunt (7) and MPH (42) enemies are
usable now and need no census.


### 10.3 Echoes (GC retail, ground truth per D7)

Rows are every enemy-class script object in the single-player paks (`Metroid1-6.pak`, 8753 objects counted), plus the five enemy types the SP paks never place. "SP" is the object count. Health is the HealthInfo value of the instances; `x N` is how many instances use that value (top four). **AI** is the asset the instances point at.

| Type | Name | Module(s) | SP | Health | AI | Tier | Flags (D4, visor, grapple) |
|---|---|---|---|---|---|---|---|
| **Pirates and Ing** | | | | | | | |
| PIRT | SpacePirate | PirateRagDoll, SpacePirate | 65 | 60 x50, 150 x13, 500 x2 | AFSM 5652a12f/7b49526b | named module + needs AFSM |  |
| CMDO | CommandoPirate | PirateRagDoll, CommandoPirate | 9 | 225 | AFSM 68a7f573 | named module + needs AFSM |  |
| EPRT | ElitePirate | ElitePirate | 10 | 600 | FSM2 dc2d4f47 | named module + needs FSM2 |  |
| FPRT | FlyingPirate | FlyingPirate | 18 | 90 | AFSM 7a71739a/7d9a41a2 | named module + needs AFSM |  |
| DKTR | DarkTrooper | PirateRagDoll, DarkTrooper | 35 | 25 x34, 600 x1 | FSM2 736d9efc | named module + needs FSM2 |  |
| DRKC | DarkCommando | PirateRagDoll, DarkCommando | 39 | 650 | FSM2 5824c524 | named module + needs FSM2 |  |
| DRKS | DarkSamus | DarkSamus | 4 | 1380 x1, 600 x1, 1800 x1, 1500 x1 | FSM2 9f23ac7b | named module + needs FSM2 |  |
| INGS | Ing | GeomBlobV2, Ing | 40 | 300 x38, 300000 x2 | FSM2 5938482e | named module + needs FSM2 | Ing: dark-world / Light-beam rules (knowledge) |
| MNNG | MinorIng | GeomBlobV2, MinorIng | 62 | 20 x51, 1 x11 | FSM2 c71b295f | named module + needs FSM2 | Ing: dark-world / Light-beam rules (knowledge) |
| MING | MediumIng | GeomBlobV2, MediumIng | 18 | 150 | FSM2 4b42811c | named module + needs FSM2 | Ing: dark-world / Light-beam rules (knowledge) |
| IPUD | IngPuddle | GeomBlobV2, Ing, IngPuddle | 7 | 10000 x5, 5 x2 | AFSM 5369bc20 | named module + needs AFSM | Ing: dark-world / Light-beam rules (knowledge) |
| ISSW | IngSnatchingSwarm | IngSnatchingSwarm | 7 | none set | AFSM 2c940bb3/30c88d1a | named module + needs AFSM | Ing: dark-world / Light-beam rules (knowledge) |
| IBSM | IngBlobSwarm | SwarmBasics, IngBlobSwarm | 4 | 2 | code only | named module | Ing: dark-world / Light-beam rules (knowledge) |
| IBBG | IngBoostBallGuardian | GeomBlobV2, Ing, IngBoostBallGuardian | 1 | 1000 | FSM2 b5d55287 | named module + needs FSM2 | Ing guardian: arena fight tied to a morph-ball / Space Jump / Spider Ball puzzle (knowledge) |
| ISJG | IngSpaceJumpGuardian | GeomBlobV2, Ing, IngSpaceJumpGuardian | 1 | 1100 | FSM2 5afab3c7 | named module + needs FSM2 | Ing guardian: arena fight tied to a morph-ball / Space Jump / Spider Ball puzzle (knowledge) |
| ISBG | IngSpiderballGuardian | IngSpiderballGuardian | 1 | 600 | FSM2 c00d11de | named module + needs FSM2 | Ing guardian: arena fight tied to a morph-ball / Space Jump / Spider Ball puzzle (knowledge) |
| **Creatures** | | | | | | | |
| SPTR | Splinter | Splinter | 61 | 6 x29, 60 x20, 2 x9, 1 x2 | FSM2 6f7f09cd | named module + needs FSM2 |  |
| BLOG | Blogg | Blogg | 22 | 12 x9, 60 x8, 100 x3, 1 x1 | FSM2 2d57a259 | named module + needs FSM2 |  |
| GRCH | Grenchler | Grenchler | 16 | 200 x12, 300 x2, 1000 x1, 700 x1 | FSM2 0e17fe4b | named module + needs FSM2 |  |
| MREE | Metaree | Metaree | 28 | 2 | FSM2 c2671c10 | named module + needs FSM2 |  |
| MYSF | MysteryFlyer | MysteryFlyer | 47 | 6 x41, 12 x6 | FSM2 e87e87b6 | named module + needs FSM2 |  |
| REZB | Rezbit | Rezbit | 21 | 250 | FSM2 7778f4c3 | named module + needs FSM2 |  |
| SHRD | Shredder | Shredder | 14 | 500 x10, 80 x4 | FSM2 18779174 | named module + needs FSM2 |  |
| SHRK | Shrieker | Shrieker | 10 | 60 | AFSM caeac3ed | named module + needs AFSM |  |
| PILB | PillBug | WallCrawler, PillBug | 15 | 25 | FSM2 1768c1f5 | named module + needs FSM2 |  |
| CRLT | Crystallite | WallCrawler, Parasite | 2 | 1 | FSM2 3fba4dc5 | named module + needs FSM2 |  |
| OCTS | OctapedeSegment | WallCrawler, OctapedeSegment | 32 | 8 | FSM2 0f6c9fcd | named module + needs FSM2 |  |
| BRZG | Brizgee | WallCrawler, Parasite | 9 | 12 | AFSM 259d8598 | named module + needs AFSM |  |
| KRAL | Kralee | WallCrawler, Kralee | 10 | 5 | FSM2 a00ca32f | named module + needs FSM2 |  |
| KROC | Krocuss | WallCrawler, Krocuss | 3 | 60 | FSM2 5f6d2d7d | named module + needs FSM2 |  |
| GBUG | Glowbug | Glowbug | 8 | 30 | AFSM a5800138 | named module + needs AFSM |  |
| EYEB | EyeBall | EyeBall | 7 | 5 | FSM2 1b51446c | named module + needs FSM2 |  |
| LUMI | Lumite | Lumite | 27 | 12 x14, 6 x13 | FSM2 aa3201a9 | named module + needs FSM2 |  |
| PUFR | Puffer | Puffer | 37 | 5 | FSM2 8411ce27 | named module + needs FSM2 |  |
| WISP | WispTentacle | WispTentacle | 32 | 5 | AFSM 6c7e429e | named module + needs AFSM |  |
| WLWK | WallWalker | WallCrawler, WallWalker | 6 | 1 | FSM2 be85503e | named module + needs FSM2 |  |
| WORM | Sandworm | Sandworm | 5 | 120 x3, 950 x1, 1000 x1 | FSM2 728f751c | named module + needs FSM2 |  |
| SNDB | SandBoss | SandBoss | 3 | 150 | FSM2 c6533129 | named module + needs FSM2 |  |
| STOD | StoneToad | StoneToad | 2 | 5 | FSM2 e45a1e23 | named module + needs FSM2 |  |
| TRYC | Tryclops | Tryclops | 2 | 5 | AFSM 214e18eb | named module + needs AFSM |  |
| SPLL | SplitterMainChassis | Splitter | 18 | 10 | FSM2 0e78ec74/350dfc00 | named module + needs FSM2 |  |
| SPLU | SplitterCommandModule | Splitter | 17 | 150 | FSM2 aaaff18a/d18b14ac | named module + needs FSM2 |  |
| SPBB | SporbBase | Sporb | 6 | 5 x5, 500 x1 | AFSM 8c2f40ca/a1bfeb4f | named module + needs AFSM |  |
| SPBP | SporbProjectile | Sporb | 5 | 5 | AFSM 70008d4b | named module + needs AFSM |  |
| SPBT | SporbTop | Sporb | 6 | 55 x5, 5 x1 | AFSM 26a6ec52 | named module + needs AFSM |  |
| SPOR | PuddleSpore | PuddleSpore | 2 | 1000000 | AFSM 935dae5d | named module + needs AFSM |  |
| GNTB | GunTurretBase | GunTurret | 12 | 80 | AFSM 18c3be20 | named module + needs AFSM | base + top are two objects |
| GNTT | GunTurretTop | GunTurret | 12 | 20 x6, 90 x6 | AFSM 3c278a24 | named module + needs AFSM | base + top are two objects |
| AIMT | AIMannedTurret | AIMannedTurret | 2 | 5 | AFSM ddd2c218 | named module + needs AFSM |  |
| MTDA | MetroidAlpha | Metroid | 28 | 1 x27, 30 x1 | FSM2 1bbdb38f/cf340923 | named module + needs FSM2 |  |
| ATMA | AtomicAlpha | AtomicAlpha | 5 | 50 | FSM2 a276d808 | named module + needs FSM2 |  |
| ATMB | AtomicBeta | AtomicBeta | 8 | 5 | FSM2 a34b02df | named module + needs FSM2 |  |
| **Swarms** | | | | | | | |
| FSWM | FlyerSwarm | SwarmBasics, FlyerSwarm | 34 | 2 x20, 1 x13, 4 x1 | code only | named module |  |
| MSWM | MetareeSwarm | SwarmBasics, MetareeSwarm | 1 | 2 | code only | named module |  |
| PSSM | PlantScarabSwarm | SwarmBasics, PlantScarabSwarm | 3 | 2 | code only | named module |  |
| BSWM | BacteriaSwarm | BacteriaSwarm | 7 | 2 | code only | named module |  |
| FISH | FishCloud | FishCloud | 69 | - | code only | named module |  |
| SNAK | SnakeWeedSwarm | SnakeWeedSwarm | 5 | - | code only | named module |  |
| **Bosses and guardians** | | | | | | | |
| EMS1 | EmperorIngStage1 | EmperorIngStage1 | 1 | 700 | FSM2 8b81a2d1 | named module + needs FSM2 | Emperor Ing: scripted arena, Light/Dark beam phases (knowledge) |
| EM2T | EmperorIngStage2Tentacle | EmperorIngStage2Tentacle | 11 | 30 | FSM2 7bf0e942 | named module + needs FSM2 | Emperor Ing: scripted arena, Light/Dark beam phases (knowledge) |
| EMS3 | EmperorIngStage3 | EmperorIngStage3 | 1 | 5 | FSM2 2bdfd033 | named module + needs FSM2 | Emperor Ing: scripted arena, Light/Dark beam phases (knowledge) |
| SBS1 | SwampBossStage1 | SwampBossStage1 | 1 | 1200 | FSM2 d6859416 | named module + needs FSM2 | Swamp boss: scripted arena (knowledge) |
| SBS2 | SwampBossStage2 | SwampBossStage2 | 1 | 500 | FSM2 69694bf8 | named module + needs FSM2 | Swamp boss: scripted arena (knowledge) |
| DGRD | DigitalGuardian | DigitalGuardian | 1 | 150 | FSM2 23d93868 | named module + needs FSM2 |  |
| DGHD | DigitalGuardianHead | DigitalGuardian | 1 | 150 | FSM2 78ad02da | named module + needs FSM2 |  |
| DSBS | DarkSamusBattleStage | DarkSamusBattleStage | 12 | - | code only | named module | Dark Samus battle stage: arena controller, not an enemy body |
| **Other AI objects** | | | | | | | |
| AMIA | AmbientAI | DOL | 37 | 5 | code only | DOL unnamed | ambient AI actor: scenery, not a fight |
| CHOG | ChozoGhost | ChozoGhost | 0 | not in SP data | - | named module | not placed in SP paks (MP/other areas only); data unread; X-ray-like visor ghost (knowledge) |
| PARA | Parasite | WallCrawler, Parasite | 0 | not in SP data | - | named module | not placed in SP paks (MP/other areas only); data unread |
| RIPR | Ripper | Ripper | 0 | not in SP data | - | named module | not placed in SP paks (MP/other areas only); data unread |
| SPNK | SpankWeed | SpankWeed | 0 | not in SP data | - | named module | not placed in SP paks (MP/other areas only); data unread |
| SPBN | SporbNeedle (projectile) | Sporb | 0 | not in SP data | - | named module | not placed in SP paks (MP/other areas only); data unread |

### 10.4 Echoes damage (what the SP data says, dominant variant)

Percent of normal damage per weapon slot (100 = normal, 200 = weak). Only slots that differ from normal are listed. "Hurt by" is the set of MPH families (section 8.3) that still do damage after immunity, reflect and pass-through are removed; **any** means all nine. A few types have several table variants in the paks (`variants`); the columns show the most common one.

| Type | Weak (%) | Resists (%) | Immune | Reflects / passes | Hurt by (MPH families) | Flags |
|---|---|---|---|---|---|---|
| PIRT | Dark 200, Entangler 200, BlackHole 200 | Light 50, LightBlast 50, Sunburst 50 | CannonBall | - | any | Dark-weak, light-world type; 7 table variants in the paks |
| CMDO | Dark 200, Entangler 200, BlackHole 200 | Light 50, LightBlast 50, Sunburst 50 | CannonBall | - | any | Dark-weak, light-world type |
| EPRT | Dark 200, BlackHole 200 | Light 50, Entangler 93, LightBlast 50, Sunburst 50 | CannonBall | - | any | Dark-weak, light-world type |
| FPRT | Dark 200, Entangler 200, SonicBoom 150, BlackHole 200, Imploder 150 | Light 50, LightBlast 50, Sunburst 50 | CannonBall | - | any | combo-only weakness (SonicBoom, Imploder): D4; Dark-weak, light-world type |
| DKTR | Light 200, LightBlast 200, Sunburst 200 | Dark 50, Entangler 50, BlackHole 50 | CannonBall | - | any | Light-weak, dark-world type: world rule not translated |
| DRKC | Light 200, LightBlast 200, Sunburst 200 | Dark 50, Entangler 50, BlackHole 50 | CannonBall | - | any | Light-weak, dark-world type: world rule not translated |
| DRKS | Dark 200, Entangler 200, BlackHole 200 | Light 50, PowerBomb 1, LightBlast 50, Sunburst 50 | CannonBall | - | any | Dark-weak, light-world type; 4 table variants in the paks |
| INGS | Light 200, LightBlast 200, Sunburst 200 | Dark 50, Entangler 50, BlackHole 50 | CannonBall | - | any | Light-weak, dark-world type: world rule not translated |
| MNNG | Light 200, LightBlast 200, Sunburst 200 | Dark 50, Entangler 50, BlackHole 50 | CannonBall | - | any | Light-weak, dark-world type: world rule not translated; 4 table variants in the paks |
| MING | Light 200, LightBlast 200, Sunburst 200 | Dark 50, Entangler 50, BlackHole 50 | CannonBall | - | any | Light-weak, dark-world type: world rule not translated |
| IPUD | - | - | ALL | - | none | NOTHING in the 8.3 table hurts it (invulnerable / scripted kill) |
| ISSW | Light 200, LightBlast 200, Sunburst 200 | Dark 50, Entangler 50, BlackHole 50 | CannonBall | - | any | Light-weak, dark-world type: world rule not translated; 4 table variants in the paks |
| IBSM | - | - | Annihilator, CannonBall | - | Basic, Cold, Heat, Contact, Bomb, PBomb, Missile, Hyper |  |
| IBBG | Light 200, LightBlast 200, Sunburst 200 | Dark 50, Annihilator 66, Entangler 50, SonicBoom 66, BlackHole 66, Imploder 66 | CannonBall | - | any | Light-weak, dark-world type: world rule not translated |
| ISJG | Light 200 | Dark 50 | CannonBall | - | any | Light-weak, dark-world type: world rule not translated |
| ISBG | - | - | all but Power, ScrewAttack | - | Basic, Contact |  |
| SPTR | Dark 200, BoostBall 500, Entangler 200, BlackHole 200 | Light 50, LightBlast 50, Sunburst 50 | CannonBall | - | any | Dark-weak, light-world type; 5 table variants in the paks |
| BLOG | Dark 200, Missile 200, Entangler 200, SuperMissle 200, BlackHole 200 | Light 50, LightBlast 50, Sunburst 50 | - | - | any | Dark-weak, light-world type; 5 table variants in the paks |
| GRCH | Dark 200, Entangler 200, BlackHole 200 | Light 50, LightBlast 50, Sunburst 50 | BoostBall, CannonBall, Bomb | - | Basic, Cold, Heat, Electric, Contact, PBomb, Missile, Hyper | Dark-weak, light-world type; 4 table variants in the paks |
| MREE | Dark 200, Entangler 200, BlackHole 200 | Light 50, LightBlast 50, Sunburst 50 | CannonBall, Phazon | - | Basic, Cold, Heat, Electric, Contact, Bomb, PBomb, Missile | Dark-weak, light-world type |
| MYSF | Dark 200, CannonBall 500, Entangler 200, BlackHole 200 | Light 50, LightBlast 50, Sunburst 50 | - | - | any | Dark-weak, light-world type |
| REZB | Dark 200, CannonBall 400, ScrewAttack 400, Entangler 200, BlackHole 200 | Light 50, LightBlast 50, Sunburst 50 | - | - | any | Dark-weak, light-world type |
| SHRD | SuperMissle 500 | - | all but PowerBomb, Missile, SuperMissle | - | PBomb, Missile |  |
| SHRK | Dark 200, Entangler 200, BlackHole 200 | Light 50, LightBlast 50, Sunburst 50 | CannonBall | - | any | Dark-weak, light-world type |
| PILB | PowerBomb 200 | - | all but PowerBomb | - | PBomb |  |
| CRLT | ScrewAttack 500 | - | Dark, Light, CannonBall, Bomb | reflects Power, Missile, PowerCharge, SuperMissle | Electric, Contact, PBomb, Hyper |  |
| OCTS | Dark 200, Entangler 200, BlackHole 200 | Light 50, LightBlast 50, Sunburst 50 | BoostBall | - | any | Dark-weak, light-world type |
| BRZG | Dark 200, Entangler 200, BlackHole 200 | Light 50, LightBlast 50, Sunburst 50 | CannonBall, Phazon | - | Basic, Cold, Heat, Electric, Contact, Bomb, PBomb, Missile | Dark-weak, light-world type |
| KRAL | Dark 200, Entangler 200, BlackHole 200 | Light 50, LightBlast 50, Sunburst 50 | CannonBall | - | any | Dark-weak, light-world type |
| KROC | Dark 200, Entangler 200, BlackHole 200 | Light 50, LightBlast 50, Sunburst 50 | - | - | any | Dark-weak, light-world type |
| GBUG | Dark 200, Entangler 200, BlackHole 200 | Light 50, LightBlast 50, Sunburst 50 | CannonBall | - | any | Dark-weak, light-world type |
| EYEB | - | - | all but ScrewAttack, PowerBomb, PowerCharge, SuperMissle | - | Contact, PBomb, Basic, Missile |  |
| LUMI | Dark 200, Light 200, Entangler 200, LightBlast 200, SonicBoom 200, BlackHole 200, Sunburst 200 | - | CannonBall | - | any | combo-only weakness (SonicBoom): D4; Light-weak, dark-world type: world rule not translated |
| PUFR | Light 200, LightBlast 200, Sunburst 200 | Dark 50, Entangler 50, BlackHole 50 | - | - | any | Light-weak, dark-world type: world rule not translated |
| WISP | - | - | ALL | - | none | NOTHING in the 8.3 table hurts it (invulnerable / scripted kill) |
| WLWK | - | - | CannonBall | reflects all but CannonBall, ScrewAttack | Contact |  |
| WORM | Dark 200, Entangler 200, BlackHole 200 | Light 50, LightBlast 50, Sunburst 50 | CannonBall | - | any | Dark-weak, light-world type |
| SNDB | Light 200, LightBlast 200, Sunburst 200 | - | Dark, CannonBall | - | Basic, Heat, Electric, Contact, Bomb, PBomb, Missile, Hyper | Light-weak, dark-world type: world rule not translated |
| STOD | - | - | BoostBall, CannonBall, Bomb, PowerBomb, Phazon | reflects Power, Dark, Light, Annihilator, Missile, PowerCharge, SuperMissle | Contact |  |
| TRYC | - | - | all but Bomb, PowerBomb, Missile, SuperMissle | reflects Missile, SuperMissle | Bomb, PBomb |  |
| SPLL | - | - | ScrewAttack | reflects all but BoostBall, CannonBall, ScrewAttack, Bomb, PowerBomb | Contact, Bomb, PBomb |  |
| SPLU | Dark 200, CannonBall 200, Entangler 200, BlackHole 200 | Light 50, LightBlast 50, Sunburst 50 | BoostBall | - | any | Dark-weak, light-world type |
| SPBB | - | - | Dark, Light, Annihilator, BoostBall, CannonBall, Bomb, PowerBomb | - | Basic, Contact, Missile, Hyper |  |
| SPBP | - | - | Dark, Light, Annihilator, BoostBall, CannonBall, Bomb, PowerBomb | - | Basic, Contact, Missile, Hyper |  |
| SPBT | Dark 200, Entangler 200, BlackHole 200 | Light 50, LightBlast 50, Sunburst 50 | CannonBall | - | any | Dark-weak, light-world type |
| SPOR | - | - | CannonBall | - | any |  |
| GNTB | - | - | Dark, Light, Annihilator, CannonBall | - | Basic, Contact, Bomb, PBomb, Missile, Hyper |  |
| GNTT | Dark 200, BoostBall 150, Entangler 200, BlackHole 200 | Light 50, LightBlast 50, Sunburst 50 | CannonBall | - | any | Dark-weak, light-world type |
| AIMT | Dark 200, Entangler 200, BlackHole 200 | Light 50, LightBlast 50, Sunburst 50 | CannonBall | - | any | Dark-weak, light-world type |
| MTDA | - | - | all but ScrewAttack, PowerBomb, Phazon | - | Contact, PBomb, Hyper |  |
| ATMA | Dark 200, Entangler 200, BlackHole 200 | Light 50, LightBlast 50, Sunburst 50 | CannonBall | - | any | Dark-weak, light-world type |
| ATMB | BlackHole 200, Sunburst 200 | - | all but Dark, Annihilator | - | Cold, Electric | combo-only weakness (Sunburst, BlackHole): D4 |
| FSWM | Dark 200, Entangler 200, BlackHole 200 | Light 50, LightBlast 50, Sunburst 50 | - | - | any | Dark-weak, light-world type; 5 table variants in the paks |
| MSWM | - | - | CannonBall | - | any |  |
| PSSM | Dark 200, Entangler 200, BlackHole 200 | Light 50, LightBlast 50, Sunburst 50 | CannonBall | - | any | Dark-weak, light-world type |
| BSWM | - | - | Dark, Light, Annihilator, CannonBall | - | Basic, Contact, Bomb, PBomb, Missile, Hyper |  |
| FISH | - | - | - | - | - | no vulnerability table (arena object) |
| SNAK | - | - | - | - | - | no vulnerability table (arena object) |
| EMS1 | - | Light 50 | BoostBall, CannonBall, Bomb, PowerBomb, Phazon | - | Basic, Cold, Heat, Electric, Contact, Missile |  |
| EM2T | - | - | all but Bomb, PowerBomb | - | Bomb, PBomb |  |
| EMS3 | - | - | - | passes through all but ScrewAttack | Contact |  |
| SBS1 | - | Power 50, Dark 50, Light 50, Annihilator 50, PowerCharge 50, Entangler 50, SonicBoom 50, BlackHole 50, Sunburst 50, Imploder 50 | BoostBall, CannonBall, ScrewAttack, Bomb, PowerBomb, Phazon | reflects Missile, SuperMissle | Basic, Cold, Heat, Electric |  |
| SBS2 | Dark 200, Entangler 200, BlackHole 200 | Light 50, LightBlast 50, Sunburst 50 | PowerBomb | - | Basic, Cold, Heat, Electric, Contact, Bomb, Missile, Hyper | Dark-weak, light-world type |
| DGRD | Light 200, LightBlast 200, Sunburst 200 | Dark 50, Entangler 50, BlackHole 50 | Annihilator, CannonBall | - | Basic, Cold, Heat, Contact, Bomb, PBomb, Missile, Hyper | Light-weak, dark-world type: world rule not translated |
| DGHD | - | - | ALL | - | none | NOTHING in the 8.3 table hurts it (invulnerable / scripted kill) |
| DSBS | - | - | - | - | - | no vulnerability table (arena object) |
| AMIA | - | - | all but ScrewAttack | - | Contact |  |

### 10.5 Corruption (Wii disc and Trilogy `MP3/RSO/Rev2 Production`)

Same 75 RSO files on both. Types are matched to RSOs **by name** (the PWE MP3 templates carry no module list). Damage tables are **not read yet**: that needs the MREA3 script reader (E3). Every enemy needs the MREA3 + ANIM3 v2 + PART v3 readers first, so the tier is "named RSO; reader work first".

| Type | Name | RSO | Tier | Notes |
|---|---|---|---|---|
| ATMA | AtomicAlpha | RSO_AtomicAlpha | data read; ANIM3/PART next | 80 / 15; immune to every listed weapon (killed another way) |
| AUR1 | AuroraUnit1 | RSO_AuroraUnit1 | data read; ANIM3/PART next | 4000 / 20; immune to every listed weapon (boss scripted) |
| AUR2 | AuroraUnit2 | RSO_AuroraUnit2 | data read; ANIM3/PART next | 4000 / 25; immune to every listed weapon (boss scripted) |
| BLWF | BlinkWolf | RSO_BlinkWolf | data read; ANIM3/PART next | 400 / 5; Plasma, Nova 50; IceMissile 300 |
| BOS3 | SeedBoss3 | RSO_SeedBoss3 | data read; ANIM3/PART next | 500 / 0.1; immune to every listed weapon (boss scripted) |
| BSKR | Berserker | RSO_Berserker | data read; ANIM3/PART next | 250 or 150 / 10; Phazon 100 or 25; PhazonBall 20-25 |
| BSTR | BeastRider | RSO_BeastRider | data read; ANIM3/PART next | 125 / 0; immune to ball, bomb, screw |
| CMBT | CombatTrooper | RSO_CombatTrooper | data read; ANIM3/PART next | 20 / 0; immune to every listed weapon |
| DEFM | DefenseMechanoid | RSO_DefenseMechanoid | data read; ANIM3/PART next | 700 / 10; immune to every listed weapon |
| DKSE | DarkSamusEcho | RSO_DarkSamusEcho | no placed instances | **no placed instances** (spawned by DarkSamus at run time). Dark Samus; Hypermode / Phazon rules (D4) |
| DRKS | DarkSamus | RSO_DarkSamus | data read; ANIM3/PART next | 6500 / 30; PhazonCharge 75, PhazonMissile 50, PhazonBall 50. Dark Samus; Hypermode / Phazon rules (D4) |
| EYEP | EyePod | RSO_EyePod | data read; ANIM3/PART next | 25 / 15 |
| FGHT | FargullHatcher | RSO_FargullHatcher (+FargullHatcherSwarm) | data read; ANIM3/PART next | 500 / 0 |
| FLYP | FlyingPirate | RSO_FlyingPirate | data read; ANIM3/PART next | 100 / 5 (2 immune scripted variants) |
| FRND | Friendly | RSO_Friendly | data read; ANIM3/PART next | 50 / 0; immune to player weapons (friendly troopers) |
| GAND | Gandrayda | RSO_Gandrayda | data read; ANIM3/PART next | 2000 / 10; Phazon 25 |
| GHOR | GhorLowerBody | RSO_Ghor | data read; ANIM3/PART next | 10000 or 5 / 10; immune (damage goes through parts). Lower/upper body, one RSO |
| GHRU | GhorUpperBody | RSO_Ghor | data read; ANIM3/PART next | 5 / 10; immune. Lower/upper body, one RSO |
| GNTB | GunTurretBase | RSO_GunTurret | data read; ANIM3/PART next | 25 / 5. Base + top, one RSO |
| GNTT | GunTurretTop | RSO_GunTurret | data read; ANIM3/PART next | 50-150 (99999 on invulnerable ones) / 1.5-5. Base + top, one RSO |
| GRFL | GragnolFlyer | RSO_GragnolFlyer | data read; ANIM3/PART next | 15 or 50 / 0 |
| GSAC | GellSac | RSO_GellSac | data read; ANIM3/PART next | 150 / 2.5 |
| KRAK | Korakk | RSO_BeastRider (class CKorakk; BossKorakk) | data read; ANIM3/PART next | 400 / 2; Phazon 25 |
| KRBM | KorbaMaw | RSO_KorbaMaw | data read; ANIM3/PART next | 200 / 0; immune (scripted) |
| MHAT | MetroidHatcher | RSO_MetroidHatcher | data read; ANIM3/PART next | 500 / 10 |
| MHOP | MetroidHopper | RSO_MetroidHopper | data read; ANIM3/PART next | 50 / 70 / 300 / 15 health by variant; contact 5 or 25 |
| MINI | MinorIng | RSO_MinorIng | data read; ANIM3/PART next | 125 or 200 / 0 |
| MPHZ | MetroidPhazeoid | RSO_MetroidPhazeoid | data read; ANIM3/PART next | 100 / 5 |
| MREE | Metaree | RSO_Metaree | data read; ANIM3/PART next | 5 or 10 / 0 |
| MYSF | MysteryFlyer | RSO_MysteryFlyer | no placed instances | **no placed instances** |
| NOTU | NoseTurret | RSO_NoseTurret | data read; ANIM3/PART next | 100 (99999 invulnerable) / 0 |
| PARA | Parasite | RSO_Parasite | no placed instances | **no placed instances** (class CParasite, a CWallCrawler, still in the RSO) |
| PDRN | PirateDrone | RSO_PirateDrone | data read; ANIM3/PART next | 15 / 2 |
| PHHR | PhazonHarvester | RSO_PhazonHarvester | data read; ANIM3/PART next | 10000; immune (scripted) |
| PHPF | PhazonPuffer | RSO_PhazonPuffer | data read; ANIM3/PART next | 225 / 0 |
| PHZD | Phaazoid | RSO_Phaazoid | data read; ANIM3/PART next | 300 / 20; only Phazon weapons hurt it |
| PILL | PillBug | RSO_PillBug | data read; ANIM3/PART next | 5 / 2.5; immune |
| PIRT | SpacePirate | RSO_SpacePirate | data read; ANIM3/PART next | 100 / 50 / 150 health by variant; contact 10; Phazon 50 on most, Electrical 5000 on 8 |
| PLCH | PhazonLeech | RSO_PhazonLeech | data read; ANIM3/PART next | 8 / 5 |
| PTNT | PhazonTentacle | RSO_PhazonTentacle | no placed instances | **no placed instances** |
| PUFR | Puffer | RSO_Puffer | data read; ANIM3/PART next | 15 / 20 |
| REPH | ReptilicusHunter | RSO_ReptilicusHunter | data read; ANIM3/PART next | 150 or 300 / 2.5; Plasma, Nova 50; IceMissile 200-300 |
| RID1 | Ridley1 | RSO_Ridley1 | data read; ANIM3/PART next | 700 / 0 |
| RUND | Rundas | RSO_Rundas | data read; ANIM3/PART next | 2500 (150 for Gandrayda posing as Rundas) / 10 |
| SAMF | SamusForm | RSO_Gandrayda (Gandrayda's Samus form, "MorphToSamusForm") | data read; ANIM3/PART next | 120 / 0; Phazon 25 |
| SB2G | SeedBoss2GiantForm | RSO_SeedBoss2 | data read; ANIM3/PART next | 1,000,000 (scripted) |
| SB2P | SeedBoss2PrimeBot | RSO_SeedBoss2 | data read; ANIM3/PART next | 3500; immune to PhazonBall, Screw |
| SB2X | SeedBoss2BotSwarm | RSO_SeedBoss2Bot | data read; ANIM3/PART next | 300; immune |
| SBO1 | SeedBoss1Orb | RSO_SeedBoss1 | data read; ANIM3/PART next | 180 or 120; every weapon 40 |
| SDB1 | SeedBoss1 | RSO_SeedBoss1 | data read; ANIM3/PART next | 1500 / 10 |
| SDRN | SteamDrone | RSO_SteamDrone | data read; ANIM3/PART next | 100 / 5; immune to missiles |
| SHBG | ShellBug | RSO_ShellBug | data read; ANIM3/PART next | 5 or 1 / 1 |
| SHIP | Ship | RSO_Ship | data read; ANIM3/PART next | 5000 |
| SPRT | Sprite | RSO_Sprite | no placed instances | **no placed instances** |
| STMB | SteamBot | RSO_SteamBot | data read; ANIM3/PART next | 25 or 100 / 5; Phazon 500, PhazonCharge 5000 |
| STML | SteamLord | RSO_SteamLord | data read; ANIM3/PART next | 250 / 20; Phazon 25; immune to grapple |
| SWBT | SwarmBot | RSO_SwarmBot | data read; ANIM3/PART next | 30 / 0 |
| WEED | VenomWeed | RSO_VenomWeed | data read; ANIM3/PART next | 10 placements in 1 area, no PatternedAI (a plant actor, no health table) |

### 10.6 Prime 1 (GC DOL, `rs5mp1_p.dol` and `default.dol`)

Prime 1 has no per-enemy modules, so every enemy is **DOL unnamed**. The IDs and names below are the PWE MP1 templates (a research reference; the license is unchecked, so nothing here ships). Per D3 the names are checked against Metaforce (MIT) and then against the DOL before E2 uses them. "Patterned" means the template shares the PatternedAI struct. Correction 2026-10-30: nearly every patterned enemy names an AFSM in its script object (map in 11.3), not only Thardus (0x58) and its rock (0x5F). Health, contact damage and vulnerabilities are **read from the disc** (11.2 item 2, 2026-10-30): `Import/Retro/Scly1.cs` reads every room's script layers (276 areas on mp1.iso, 54,670 objects, 0 failures) and finds each object's PatternedInfo block; `MphRead.Tools -mp1scly census` prints this table, `-mp1scly types` every object type.

How to read the new columns: **Instances** = script objects of that type on the disc (layers included). **Health** and **contact damage** are the values per instance, "×n" = how many instances carry that value (the top four, "(+n)" more values). **Vulnerable to** is one letter per weapon in Prime 1's order: Power, Ice, Wave, Plasma, Bomb, Power Bomb, Missile, Boost Ball, Phazon. W weak (double), N normal, D deflect, I immune, P pass-through; a small "d" in front = direct hits only (no charge or splash), as Prime 1 marks it. A "*" means instances differ and the letter is the most common one (Space Pirate variants, Parasite vs Platform Zoomer, Metroid kinds). Boss values are the script's **base** block only: Thardus (5), Flaahgra tentacles (10000), Metroid Prime's exo (5) and PuddleSpore (50000/1000000) keep their real health and weak points elsewhere (parts, their own structs, or scripted states), so they need their own read in the port.

| Object ID | Name | Instances | Health | Contact damage | Vulnerable to (P I W Pl B PB M BB Ph) | Tier |
|---|---|---|---|---|---|---|
| 0xE | NewIntroBoss | 1 | 480 | 0 | W W W W W W W W W | DOL unnamed |
| 0x16 | Beetle | 49 | 10×42, 30×4, 40×3 | 10×46, 20×3 | N N N N* N N N N N | DOL unnamed |
| 0x21 | WarWasp | 15 | 4×14, 2×1 | 10×11, 5×4 | N N N N* N N N N N | DOL unnamed |
| 0x24 | SpacePirate | 168 | 60×67, 70×35, 100×17, 150×14 (+5) | 10×120, 5×45, 2×3 | dN* dN* dN* N* N* N* N* D* N | DOL unnamed |
| 0x25 | FlyingPirate | 40 | 90×35, 120×5 | 10 | N N N N N N N N N | DOL unnamed |
| 0x26 | ElitePirate | 7 | 300×6, 700×1 | 25 | dN dN dN dN D D dN D dN | DOL unnamed |
| 0x27 | MetroidBeta | 10 | 1 | 32 | I I I I D N I D D* | DOL unnamed |
| 0x28 | ChozoGhost | 23 | 200 | 5 | N P P P P* P* P P P | DOL unnamed |
| 0x2D | BloodFlower | 4 | 100 | 0.2 | I I I W I I I I N | DOL unnamed |
| 0x2E | FlickerBat | 17 | 4 | 10 | N N N N N N N N N | DOL unnamed |
| 0x31 | PuddleSpore | 5 | 50000×3, 1000000×2 | 10×3, 2×2 | D D D D D D D D D | DOL unnamed |
| 0x34 | PuddleToadGamma | 6 | 1×5, 10×1 | 0 | D* D* D* D* N N D* D* D* | DOL unnamed |
| 0x36 | FireFlea | 8 | 4 | 10 | N N N N N N N N N | DOL unnamed |
| 0x37 | MetareeAlpha | 92 | 1 | 5 | N N N N N N N N N | DOL unnamed |
| 0x3B | SpankWeed | 47 | 2×40, 1000000×7 | 10×44, 20×3 | N N N N N N N N N | DOL unnamed |
| 0x3D | Parasite | 98 | 5×63, 6×25, 2×7, 10×3 | 10×63, 2×30, 0×5 | D* D* D* N* N* N* N* D* D* | DOL unnamed |
| 0x3F | Ripper | 5 | 5 | 0 | D I D D D D D D D | DOL unnamed |
| 0x43 | Drone | 16 | 40×15, 120×1 | 10 | N N N N N N N N N | DOL unnamed |
| 0x44 | MetroidAlpha | 61 | 1×56, 5×5 | 5×59, 0×2 | I I I I D N* I* D D* | DOL unnamed |
| 0x4B | IceSheegoth | 5 | 400×2, 250×2, 500×1 | 20×4, 10×1 | D D D N W N D D D | DOL unnamed |
| 0x4D | Flaahgra | 1 | 2000 | 16 | N N N N D D N D N | DOL unnamed |
| 0x54 | JellyZap | 22 | 30 | 5 | N N N N N N N N N | DOL unnamed |
| 0x58 | Thardus | 1 | 5 | 10 | D D D D D D D D D | DOL unnamed + needs AFSM (64a97140, c44a40c9) |
| 0x5A | WallCrawlerSwarm | — | — | — | no PatternedInfo (own struct) | DOL unnamed |
| 0x5C | FlaahgraTentacle | 4 | 10000 | 20 | D D D D D D D D D | DOL unnamed |
| 0x5F | ThardusRockProjectile | 1 | 6 | 7 | N N N N N N N N N | DOL unnamed + needs AFSM (ff3c0ddc) |
| 0x64 | GunTurret | — | — | — | no PatternedInfo (own struct) | DOL unnamed |
| 0x66 | Babygoth | 14 | 6 | 10 | N D N W N N N D N | DOL unnamed |
| 0x67 | Eyeball | 14 | 10 | 5×13, 10×1 | I I I I I W I D D | DOL unnamed |
| 0x6B | Magdolite | 9 | 100 | 5 | N N N N N N N D N | DOL unnamed |
| 0x6D | SnakeWeedSwarm | — | — | — | no PatternedInfo (own struct) | DOL unnamed |
| 0x6F | Oculus | 14 | 5 | 10 | dI dI dI dI dI dI dI* dI dI | DOL unnamed |
| 0x70 | Geemer | 5 | 1000000 | 10 | I I I I I N I I I | DOL unnamed |
| 0x72 | AtomicAlpha | 28 | 18×26, 10×2 | 30 | D D N D D D D D D* | DOL unnamed |
| 0x77 | AtomicBeta | 19 | 18 | 16×11, 18×8 | D D N D D D D D D | DOL unnamed |
| 0x78 | IceZoomer | — | — | — | not placed on the disc | DOL unnamed |
| 0x79 | Puffer | 71 | 2 | 0 | N* N* N* N* N* N* N N* N* | DOL unnamed |
| 0x7A | Tryclops | 12 | 5 | 5 | D N D D N N D D D | DOL unnamed |
| 0x7B | Ridley | 1 | 2000 | 20 | N N N N N N N N N | DOL unnamed |
| 0x7C | Seedling | 13 | 5 | 10 | N N N N N N N N N | DOL unnamed |
| 0x7F | Burrower | 23 | 2 | 10 | N N N N* N N N N N | DOL unnamed |
| 0x83 | MetroidPrimeStage2 | 1 | 36667 | 30 | D D D D D D D D N | DOL unnamed |
| 0x84 | MetroidPrimeStage1 | 4 | 5 | 25 | D D D D D D D D D | DOL unnamed |
| 0x86 | OmegaPirate | 1 | 600 | 50 | dN dN dN dN D D dN D dN | DOL unnamed |
| 0x88 | PhazonHealingNodule | 9 | 75 | 20 | N N N N N N N N N | DOL unnamed |
| 0x8B | EnergyBall | 12 | 30 | 0 | N N N N N N N N N | DOL unnamed |

### 10.7 What the census found (flags for the owner)

- **Visor-restricted enemies: none in Echoes.** The visor condition lives on Actors, DamageableTriggers and a few
  Platforms, never on an enemy type. Corruption and Prime 1 are unread.
- **Grapple-only: none in Echoes.** Its vulnerability table has no grapple slot (grapple is a separate mechanism on
  the player side). Corruption's grapple-driven fights (Ghor, Gandrayda, Rundas, Berserker, SteamLord and similar)
  will need the **needs abilities** flag; that is knowledge, not read from the data.
- **Combo-only weaknesses (D4, not translatable):** FPRT (SonicBoom, Imploder), LUMI (SonicBoom) and ATMB (Sunburst,
  BlackHole). FPRT and LUMI are also weak to a base weapon, so they stay killable. ATMB is hurt only by Cold and
  Electric in 8.3 terms, which MPH has.
- **Nothing in the 8.3 table hurts:** IPUD (IngPuddle), WISP (WispTentacle), DGHD (DigitalGuardian). They are
  invulnerable or killed by script, so they need a fallback rule (placed as scripted, or swapped for the fallback
  enemy per D3).
- **Narrow kill sets (softlock risk, section 8.6):** ISBG hurt only by Power/ScrewAttack; SHRD only Missile and
  PowerBomb (SuperMissile 500%); PILB only PowerBomb; CRLT only ScrewAttack (Power and Missile reflected); WLWK
  reflects all but ScrewAttack; EMS3 passes through all but ScrewAttack.
- **Light and dark world:** the Echoes pattern is consistent. Light-world creatures take Dark and resist Light; Ing
  and dark-world creatures are the reverse. MPH has no world split, so only the weak/resist pair carries over and the
  "which world" condition is dropped (flagged per row).
- **Not in the SP data:** CHOG, PARA, RIPR, SPNK and SPBN have a module but no placed instance in the pak scan. They
  are listed in 10.3 with that note and have no damage data.
- **Corruption:** 58 patterned types from the template. Korakk and SamusForm have no RSO under that name. Damage
  tables, health and exported symbol names are not read.
- **Prime 1:** 46 object types, no names in the DOL itself. Health, contact damage and vulnerabilities are now read
  (10.6, 2026-10-30); IceZoomer (0x78) has no instance on the disc. Immune-to-almost-everything rows (softlock risk,
  8.6): MetroidBeta and MetroidAlpha (Power Bomb only, plus freezing in the real game), Eyeball (Power Bomb), Geemer
  0x70 (Power Bomb), BloodFlower (Plasma, Phazon), Oculus (direct hits immune: scripted). The DOL object-ID table is
  not read.

### 10.8 Open items R1 did not cover

1. Corruption's DOL (not extractable yet), RSO export names, and MREA3 vulnerability data (E3).
2. Prime 1's DOL enumeration (E2). ~~The MP1 SCLY health and damage tables~~ read 2026-10-30 (10.6).
3. The "needs abilities" flags (Ing guardians, Emperor Ing, swamp boss, Chozo Ghost, Corruption grapple enemies) are
   from game knowledge; confirm in E1 and E3.
4. ~~The AFSM/FSM2 reader is the first blocker~~ Resolved 2026-10-30: `Import/Retro/Fsm.cs` reads all of them (11.3).

## 11. The full roster, hunters included (owner 2026-10-30)

Owner, 2026-10-30: "I want to be able to import all enemies from all prime games, hunters included. I know prime 4
may not be possible yet. That's ok."

So the goal is now explicit: **every enemy, boss and hunter** of First Hunt, MPH, Prime 1, Echoes and Corruption gets
a catalogue entry and a 1:1 port (section 1). Prime 4 follows when its readers exist (P4); until then it's a research
item, not a promise. The order doesn't change: Hard Mode's picks first, then the rest of each game's wave (D1). In
section 6, "the rest" now means every row of section 10 plus every row of 11.1, not a subset.

Abilities rules (D4) still apply: an enemy whose fight needs a visor, grapple, Hypermode or the light/dark world is
still in the roster. It waits behind its flag until the owner says leave out, adapt or build the ability.

### 11.1 Hunters per game

"Hunter" means a bounty hunter or a hunter-class rival fought like one (Dark Samus). Bosses that aren't hunters stay
in B1.

| Game | Hunter fights | Catalogue ids | Where the logic is | What's in hand | Blockers |
|---|---|---|---|---|---|
| MPH | The 6 rival hunters as campaign encounters: Kanden, Spire, Trace, Noxus, Sylux, Weavel (`EnemyType.Hunter` = 48, spawned with a `HunterId`, driven by the bot brain `PlayerAi*` and the ROM's personality data). Also Gorea's Guardians (`Hunter.Guardian`) | `mph:Hunter.<name>`, `mph:Guardian` | MphRead (C#), already 1:1 (bot parity checked 10-24) | everything | none. H1 left hunters and bosses out of the placer (`EnemyCatalogue.PlaceableMph`), so they need H2 |
| MPH extras (our characters) | Pirate Trooper, Dark Samus stand-in, Zero Suit Samus as AI rivals, on the same bot brain | `sh:Hunter.<name>` | our code (bot support exists for Pirate Trooper/Dark Samus, thread A) | everything | Not 1:1 with any game: an **adaptation** (D4 label), owner's call per character |
| First Hunt | none | | | | |
| Prime 1 | none (no bounty hunters in Prime 1) | | | | |
| Echoes | Dark Samus (DRKS, 4 SP instances, health 600-1800) + her arena controller DarkSamusBattleStage (DSBS) | `mp2:DarkSamus` | `DarkSamus.rel` / `RSO_DarkSamus` (named) | model, anims, effects readers; the Dark Samus body already runs as a playable character | FSM2 9f23ac7b parses (11.3; 59 states, 92 transitions); next: the K1 kit, then a port |
| Corruption | Rundas (RUND), Ghor (GHOR lower + GHRU upper body), Gandrayda (GAND), Dark Samus (DRKS) + her echoes (DKSE) | `mp3:Rundas`, `mp3:Ghor`, `mp3:Gandrayda`, `mp3:DarkSamus` | `RSO_Rundas`, `RSO_Ghor`, `RSO_Gandrayda`, `RSO_DarkSamus`, `RSO_DarkSamusEcho` (named) | CMDL3/CHAR3/SKIN3/ANIM3 v1, sound | E3 readers (MREA3, ANIM3 v2, CHAR3 events, PART v3), the DOL out of the Wii image; D4 flags: grapple (Rundas, Ghor, Gandrayda), Hypermode / Phazon (Dark Samus, Gandrayda) |
| Prime 4 | Sylux | `mp4:Sylux` | `main` NSO (AArch64), no names | Sylux's model + skeleton + animations (CR6 clean-room importer) | no script, AI, effects or sound readers; 1:1 needs NSO RE per move. P4 spike; owner: OK if it isn't possible yet |

Dark Samus appears in two games with two different fights, so she gets two ids. Hunters are boss-class: each is
room-bound (a big enough room, or a built arena later) and gets the mini-boss gate and health bar.

### 11.2 Work that needs no owner

Everything below is PC-only research or code with a PC test. None of it needs an owner pick, an Odin check or a disc
the owner hasn't already given us. Items 1-4 are mechanical reader work, a good fit for a cheaper-model session. The
owner judges the results when back (look checks by eye, Odin installs as usual).

| # | Work | Unblocks | Output | Done when |
|---|---|---|---|---|
| 1 | **DONE 10-30** (11.3). **AFSM/FSM2 reader** (Prime 1 AFSM, Echoes FSM2): states, triggers, conditions, parameter names | Echoes Dark Samus and every "needs AFSM/FSM2" row; Thardus | `Import/Retro/Fsm.cs` + a `-fsmdump` Tools switch | every AFSM/FSM2 asset on the Prime 1 and Echoes discs parses and dumps readably |
| 2 | **DONE 10-30** (10.6; reader in `Import/Retro/Scly1.cs`, not `Mrea.cs`, which belongs to the Echoes arenas). **Prime 1 script layers (SCLY)** | E2; section 10.6 health and damage columns | catalogue rows filled from the disc | every MP1 enemy type in 10.6 has health + damage values |
| 3 | **DONE 2026-10-30.** **Corruption DOL + MREA3 script data**: pull the DOL out of the Wii image with our disc readers, read MREA3 script instances | E3; 10.5 health/damage; resolves Korakk and SamusForm | catalogue rows filled | every 10.5 row has values and a module |
| 4 | **RSO/REL part DONE 10-30** (11.4, docs/ENEMY_MODULES.md); Prime 1 DOL entry points open. **Module symbol index**: export/import names of every REL/RSO (Echoes, Trilogy MP2/MP3), the DOL entry point per Prime 1 enemy (Metaforce names checked against the DOL, D3) | every port's "where is the logic" step | appendix per enemy and hunter | every catalogue row points at its code |
| 5 | **K1 kit, pilot-free parts**: enemy clip driver on NativeBody, EVNT runtime dispatch (sounds, particles, locators on frames), Retro physics actor on MPH collision, damage table from `enemies/damage_map.json` (8.3 rows) | every Retro enemy | Core + MphRead host hooks, each with a PC test | tests pass on an Echoes body dropped into an MPH room |
| 6 | **K1 pilot**: one simple Echoes enemy end to end (recommended Grenchler, named first in E1; Splinter as backup) | proves the kit | `mp2:Grenchler` in the catalogue + `-enemytest grenchler` | PC test drives every state; Dolphin side-by-side ready for the owner's eye |
| 7 | **Hunters + Guardians DONE 10-30** (`mph:Hunter:<name>` x8; a live-placed hunter gets a bot slot via `EnemyPlacement.AttachBot`, delete frees it; a defeated story hunter keeps threshold+1 health and flees, as vanilla; bosses still to do). **H2: MPH hunters + bosses in the placer**: hunter encounters (any of the 6, personality from the ROM), Guardians, and room-bound bosses only in their own room | Hard Mode can place hunters now | `EnemyCatalogue` + `EnemyPlacement` | `-enemyplacetest` places and kills a hunter encounter; installed for the owner |
| 8 | **DONE 10-30** (11.5). **Hunter census detail** for 11.1: per hunter, module, AI asset, clip count, attacks, projectiles, D4 flags (read, not from knowledge) | B1 hunter scopes | table rows here | every 11.1 row has data from the disc |

Owner-only items, for when they're back: the H1 check on the Odin (still open from 10-26), the 8.3 damage numbers
before the first Prime enemy, Hard Mode enemy picks, look checks by eye, and the D4 calls on grapple and Hypermode
hunters.

**Effort.** About 150 regular enemies, 27 bosses and 6 non-MPH hunters across MP1-3. The kit is paid once; then a
simple enemy is about one session, a pirate-class one two or three, a boss or hunter more. This is a long track,
worked in the order above.

### 11.3 State machine map (item 1, done 2026-10-30)

Reader: `src/MphRecomp.Core/Import/Retro/Fsm.cs` (format notes in its header). Tool: `MphRead.Tools -fsmdump`
(`extract out=<dir>`, `dump [disc=] [id=<hex>]` prints states, transitions and conditions, `refs [disc=]` finds the
script objects that name each machine). Every state machine on both discs parses to its end: **Prime 1 53 AFSM**
(65 distinct conditions), **Echoes 110** (89 FSM2 v1 + 21 AFSM; 612 distinct conditions/actions). FSM2 version 1 is
the only version on the disc. The conditions and actions are the list a port must implement per enemy (`dump id=`).

**Correction to 10.6:** almost every Prime 1 patterned enemy names an AFSM in its script object, not only Thardus.
So E2 is "DOL unnamed + needs AFSM" for most rows. Prime 1 map (object -> AFSM):

| Enemy | AFSM | Enemy | AFSM | Enemy | AFSM |
|---|---|---|---|---|---|
| Metroid Prime (exo) | 09689750 | Metroid Prime (essence) | 52f9b74d | Ridley | 0f5e66f8 |
| Thardus | 64a97140, c44a40c9 | Thardus rock | ff3c0ddc | Flaahgra | 5774382b |
| Flaahgra tentacle | cf5c25a3 | Omega Pirate | ac15b86b | Elite Pirate | c163b9e9 |
| Sheegoth | 713eb1f2 | Baby Sheegoth | 7ed253fb | NewIntroBoss (Parasite Queen) | 2f9e3665 |
| Space Pirate | 5652a12f (main), 293020fb (melee), c47e1c06, db7f4702, f3084465 | Flying Pirate | 7a71739a, 7bfec237 | Chozo Ghost | 0da425c6 |
| Metroid | 6655c185 | Metroid (beta) | ccc63521 | Seedling | 0b44f92c |
| Ice Beetle | 173239ed | Beetle | e23dfb53 | Tryclops | 214e18eb |
| Phazon healing nodule | 28a73fae | Parasite | 29735bba | Flickerbat | 2fd93b36 |
| War Wasp | 394913ad, ddd32d4a | Atomic Alpha / Beta | bb79601e / 44b2a0af | Puffer | 4cb31e59 |
| Puddle toad | 610a94c7, d2d84757 | Puddle spore | 935dae5d | Metaree | 62c39ad6 |
| Zoomer | 6f59fdf1 | Geemer | 88376e6a | Eyeball | 71614ee4 |
| Magdolite | 804dcf11 | Water tentacle (SpankWeed) | 84490a92 | Jelly Zap | acb45853 |
| Blood flower | bbb29f1d | Ripper | bfa1a122 | Burrower | c2109366 |
| Fire flea | c6f9280f | Energy ball | d5dccd02 | Drone (Aqua/Pirate) | ea5c85b9, ff23b218 |
| Oculus | f302f2ec | | | | |

Echoes: the 10.3 table already carries each enemy's machine id (those came from the SP script data and all parse).
Not in 10.3: Dark Samus 9f23ac7b (59 states, 92 transitions: missile deflect, scatter shot, dive, super missile, sweep beam, boost ball, phazon enrage; the hunter port's spine), Emperor Ing stages 8b81a2d1 / 2bdfd033 + tentacle
7bf0e942, Chykka (SwampBoss) d6859416 / 69694bf8, Amorbis (SandBoss) c6533129, Bomb Guardian 728f751c.

About 40 Echoes FSM2 are named only in MLVL dependency lists, not by a script object directly: boss phases and
sub-machines the boss module loads itself. Identified by their state names (read from the data, confirm when that boss
is ported):

| Likely owner | Machines (state / action names that say so) |
|---|---|
| Quadraxis | 0f8c588b (ProtectKnees), fcbf5deb (DetachFromBody, DeploySplitters), 3d31822b (BombedReaction, FollowRetreatPath), cdb2a199 (SpinAttack, BounceOffPlayer) |
| Amorbis | dfd06bb7 (SuckBall, DestroySphere, UnderGround), 59054130 (Stampede), b7e90122 (SnapJaws, LockSphere), fb018737 (AttachToSphere) |
| Chykka | 6bea7bd4 (SpitWater, Swoop, SpitBarrage), 7b757d6d (DarkLurk, DropFlyer) |
| Caretaker / water boss | 905adf4b (Beach, TongueLoop, ExposeBelly, ShreddersOff), c5b1710e (SplashJump) |
| Bomb Guardian | d1cc2808 (BombFountainAttack, BombTossAttack) |
| Rezbit variants | 268df298 (BecomeDeRezzed, VirusAttack), 4cde1caf (BecomeRezzed, CuttingLaserAttack) |
| Ing variants (Warrior Ing, Darkling, hive) | 10e929e4, 227ec252, 3734ed3f, 4077ee81, bd9dea3f, d48297bc (BecomeIngSpot, IngSpotPathFind), 79e9eaa2 (ExitPortal, SnatchTarget) |
| Dark Pirate class | 3d81d9da, f99b115f (Alert, Taunt, StartRangedAttack), 43aebe16 (ShadowDash, FireEMP) |
| Emperor Ing / Dark Samus phases (to check) | 5d1fc5f5 (MiniPortalAttack), 6d34ea6f (MistIn, MistAttack), 8915aab2 (VortexAttack), 492b912b (AngryAttack, Shockwave), c5cdd27d (EyeShut, DarkBeamAttack), ecbc50ad (PulseAttack, MissileAttack, ChargeAttack) |
| Small flyers / turrets | 0edeee9d (RaiseShields), 1164f360 (Patrol, TookDamage), 655d3bea, 6fc98f3c (DeathHeadAttack), 95872429 (LaserSweep), ad112d87, c331cd18 (LaserPulse), f548e6e4 (LegStabAttack) |
| Player / animation, not enemies | AFSM 118a0532, 94d64095, e7a1018c, ff492ea2 (Holstered, MorphToBall, PlayerJump) |

Nothing runs a state machine yet; running one is part of the K1 kit (item 5) and each port.


### 11.4 Module symbol index (item 4, RSO/REL part done 2026-10-30)

`-moduleindex out=docs/ENEMY_MODULES.md` (Tools `ModuleIndexTool.cs`) reads every code module on the three discs and
writes [ENEMY_MODULES.md](ENEMY_MODULES.md). That file is generated, so rerun the tool instead of editing it. It covers 327 modules:
Trilogy 167 RSO (MP2 + MP3), Echoes GC 86 REL, and Corruption Wii 74 RSO, of which 69 match their Trilogy copy and are listed once.

What a module tells us:
- **Base classes (RSO only).** RSOs export only `_prolog`/`_epilog`, so a module's own functions have no names. The DOL
  functions it imports do have mangled names, and the classes whose `AcceptScriptMsg`/`Think` it imports are its
  base classes. Counts: CPatterned alone 51, CActor 18, CEntity 11, CRenderActor 10, CWallCrawler 8, CPatterned +
  CBoneTracking 8 (Grenchler is one), CPatterned + CPathMeshConstraint 6, CSwarmBasics 4, and others. CPatterned
  appears in 85 modules. The 17 with no base are support modules (rhbm, Tweaks, PirateRagDoll, SwarmBasics,
  WallCrawler, map screen, teleporter attributes), not enemies.
- **AI vocabulary (RSO and REL).** The data sections hold the state and trigger function names that the FSM assets
  (11.3) refer to by name, plus locator and effect names. This is how a state in a dumped FSM is matched to the module
  that runs it (for example, RSO_SteamLord lists StateOver, ShotAt and IsCombatReady). RELs import by address, so for
  Echoes GC only the strings are listed. The Trilogy RSO of the same enemy gives the names.

**Still open: Prime 1 DOL entry points.** Prime 1 has no enemy modules (its `rhbm` and `selfile.sel` only), and its DOL
carries no symbols, so the Metaforce class names cannot be checked against it by name. Doing that needs signature
matching against the Trilogy MP1 code (which shares the engine) or vtable walking from the SCLY loader table. That is
left for each Prime 1 port's "where is the logic" step.

### 11.5 Hunter census (item 8, done 2026-10-30)

Read from the discs with `-mp3scly hunters` (Corruption, default disc) and
`-mp3scly hunters disc=<prime>/echoes_retail.rvz [types=DRKS,...]` (Echoes). Per hunter type: health and contact damage
for each PatternedAITypedef block (Corruption has three: normal, Easy, Elite), every named DamageInfo outside those
blocks (the attacks, with base damage), the assets the object references by type (WPSC = projectile definitions,
PART = particle effects, CAUD = sound), and the animation character with its animation and clip counts. The state and
attack vocabulary comes from the module strings (docs/ENEMY_MODULES.md). Nothing here is from memory of the games.

MPH's own hunters and Guardians are not in this table: they already run 1:1 in MphRead (bot brain, parity checked
10-24) and are in the placer since H2.

| Hunter | Module | AI asset | Character | Health (contact) | Attacks (base damage) | Assets | D4 flags |
|---|---|---|---|---|---|---|---|
| Echoes Dark Samus (DRKS, 4 objects in 4 areas: "DarkSamus 001" x2, "DarkSamus Battle 2" x2) | `DarkSamus.rel` | FSM2 9f23ac7b (59 states, 92 transitions, 11.3) | ANCS 39900a11, 3 characters (Dark_Samus, Battle_2, Battle_3): 76 animations, 66 clips | 600 / 1380 / 1500 / 1800 by fight (10) | Melee 20, DiveAttack 40, ScatterShot 2, NormalMissile 20, SuperMissile 40, FreezeBeam 40, SweepBeam 20, BoostBall 30, PhazonSuperMissile 10, PhazonProjectile 10, PhazonEnrageSphere contact 0 | PART 11, WPSC 7, TXTR 2, SWHC 2, CRSC 1, CMDL 1, CSKR 1 | Phazon enrage; freeze beam (MPH has no freeze: translate to a slow, owner's call) |
| Rundas (RUND, 2 objects: Rundas, and Gandrayda posing as him) | `RSO_Rundas` | in the module (the object references no FSM asset) | CHAR "Rundas": 81 animations, 76 clips (the second CHAR, 18659a2ab4827a79, is not in Metroid3.pak) | 2500 on all 3 blocks; 150 for the Gandrayda copy (10) | DamageInfo 50, IceGrenade 15 (10 on Easy), IceSword 15, DamageInfo 10, MorningStar 50, SurfingProjectile 5 | PART 14, CMDL 12, WPSC 6, CAUD 2, TXTR 1, SCAN 1 | grapple; Hypermode (EnterHypermode in the module); ice surf (Surf state) |
| Ghor lower body (GHOR, 6 objects in 2 areas: Easy, Normal, Hard and Hyper are separate objects) | `RSO_Ghor` | in the module | CHAR "lower_body_ghor": 51 animations, 51 clips | 10000 (x4) or 5 (x2); damage goes through parts (10) | Damage 20, 30 | CMDL 11, PART 2 | grapple (armour pulled off); Hypermode |
| Ghor upper body (GHRU, 6 objects) | `RSO_Ghor` | in the module | CHAR "Ghor_upper_body": 101 animations, 100 clips | 5 (10) | CollisionDamage 100, BeamDamage 10/15, ChargeAttack 10/15, Slam 15/30, Throw 10/30, Melee 10, Damage 5/7/7.5/15, ContactDamage 10/15 | CMDL 20, CAUD 14, WPSC 6, PART 6, TXTR 4, SWHC 2 | as GHOR (one fight, two objects) |
| Gandrayda (GAND, 1 object "Base Form") | `RSO_Gandrayda` | in the module | CHAR "Gandrayda_Corrupted": 94 animations, 94 clips | 2000 (10) | Melee 10/15, RadialMelee 10/15, ThrownProjectile 5, EnergyWaveProjectile 10/15, GrapplePull 10, Grapple 0.17 (per tick), Damage 20 | WPSC 9, PART 9, CAUD 9, ELSC 9, CMDL 3 | grapple (hers and yours); morphs into other hunters (needs their bodies: Rundas, Samus); cloak; Hypermode |
| Corruption Dark Samus (DRKS, 1 object) | `RSO_DarkSamus` | in the module | CHAR "Dark_Samus": 118 animations, 112 clips | 6500 on all 3 blocks (30) | Melee 30, MegaBlaster 15, MegaBoostTrail 10, MegaBoostThruPlayer 27.5, PhazonBarrageBeam 100, Damage 25, HomingMissile 25, EnergyWave 50, EchoBlast 50, SuperLoop 30, ShellGameContact 10 | PART 12, WPSC 7, CAUD 6, TXTR 5, SCAN 2 | Hypermode / Phazon; splits into echoes (DKSE) |
| Dark Samus echo (DKSE) | `RSO_DarkSamusEcho` | in the module | uses Dark Samus' character | spawned at run time: 0 placed objects | PhazonBallAttack, MorphballAttack (module strings; numbers come from the parent's properties) | | as DRKS |
| Prime 4 Sylux | `main` NSO | none readable | CR6 importer: model, skeleton, animations | not readable yet (no script reader) | not readable yet | | P4 spike |

What this changes in the plan:
- Every hunter's numbers and clip lists are now known, so a K1 kit (item 5) can be sized per hunter: Corruption
  Dark Samus (112 clips) and Ghor's upper body (100) are the biggest, Ghor's lower body (51) the smallest.
- None of the Corruption hunter objects references an FSM asset (their asset lists above), so their AI is in the RSO
  code and porting them
  is code reading per state (module strings give the state names), like MPH's bosses. Echoes Dark Samus is the one
  hunter whose behaviour is data (FSM2): she is the best first hunter port.
- Corruption Easy/Elite blocks change little for hunters (only Rundas' IceGrenade, 15 -> 10 on Easy); Hard Mode can
  use the normal block and scale like MPH's own enemies.
- D4 per hunter is above; grapple (3 of 4 Corruption hunters) is the main ability gap, then Hypermode.

