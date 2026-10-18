# Multiplayer characters and skins

Owner request, 2026-10-08: the Power, Ice, Wave and Plasma troopers as ONE selectable multiplayer character with four
skins, and a skin choice for Samus. Dark Samus becomes her own character later.

## Owner decisions (2026-10-08)

| # | Question | Answer |
|---|---|---|
| D1 | Trooper's moves | Weavel's rig and moves (the trooper rigs already run on his clips; Weavel is a cyborg Space Pirate). **Plus:** a first-person arm cannon authored from the trooper model's own cannon, and an **original alt form** with its own look and a unique use (brainstorm below, owner to pick). |
| D2 | Dark Samus | **Her own character** with two skins (Echoes, Corruption), moving with **her own animations from the games**, and a first-person arm cannon cut from her model. Not a Samus skin. |
| D3 | Where skins are picked | **Both:** your own on SELECT HUNTER; the host sets bots' skins in the lobby. |
| D4 | Bots' skins | **Set in the lobby** by the host. |
| D5 | Import mismatches online (later 2026-10-08) | **Fallbacks:** a player whose character or skin you haven't imported shows as a look-alike built on your own device from MPH's own models; no assets travel (see "Mixed imports"). |
| D6 | Trooper's extras (later 2026-10-08) | The four concepts (jetpack, wall crawl, shield wall, cloak) become the **trooper's unique hunter ability**, like Trace's cloak or Spire's lava immunity. He **still needs a compact alt form** like the other hunters' (brainstorm below). |
| D7 | Name shown to a player without the import (later 2026-10-08) | **The picked character's name** (TROOPER, DARK SAMUS), drawn as the recoloured look-alike (D5). |
| D8 | Dark Samus's moves (later 2026-10-08) | **B: Samus's gameplay + her hover look.** Same weapons, morph ball, speed, hitbox and controls as Samus; only her body is drawn hovering a little off the floor, with her own clips. No sim change (LAN and replays stay exact). |
| D9 | Menu mockups (later 2026-10-08) | SELECT HUNTER: **Option 2, icons**, but **keep vanilla's full info box** (name, ALT-FORM, etc.): highlighting a hunter shows all the info like vanilla, and **selecting it opens the skin chooser** (round skin icons, tap one). LOBBY: **Option 2, badge + chooser** (a skin badge on each seat picture; tapping it opens that character's skins). Next: an updated mockup of the SELECT HUNTER flow (info box untouched + chooser on select), then build. A2 + B1 picked; built, on the Odin 19:56 (SELECT HUNTER 9 icons + skin chooser, lobby badges, D7 bot names TROOPER / DARK SAMUS). |
| D10 | Trooper alt form = **Scout Drone**, owner's design (later 2026-10-08) | Pressing morph: the Prime 1 pirate drone **spawns in with MPH's spawn flourish** (MphRead `spawnEffect` 31 / `spawnEffectMP` 33, as at respawn); **Weavel's health split** (`HalfturretEntity`: the left-behind part takes half the health, `Owner.Health -= half`); the **pirate body stays behind** while the drone goes out; the drone keeps **at least one of its own Prime 1 attacks with its own real beam fire**; it **flies up and down, never higher than the player's head height above whatever surface is under it**. Open: what the left-behind pirate does (stands as a target, or fires on its own like Weavel's turret), which drone attack, how it merges back. |
| D10a | Scout Drone details (owner, later 2026-10-08) | The pirate left behind **stands still and vulnerable** with its half of the health (no auto-fire). The drone fires **twin blasters only** for now (more moves later maybe). **Merging back works exactly like Weavel's** Halfturret. |
| D11 | Trooper ability (owner, later 2026-10-08) | **Shield Wall**, with the owner's limits: his line of fire must be limited, and he is **locked facing one direction** while the shield is up. Proposal below **OK'd as the starting point (~13:30)**, plus a **brief stagger when the barrier breaks** (an existing animation if one fits). GO to build. |
| D12 | Shield vs the Imperialist (owner, 2026-10-08 ~14:20) | **No shield while the Imperialist is equipped**: the zoom button zooms as usual; switching to it drops a planted barrier. (Built 14:30.) |
| D13 | Shield look (owner, 2026-10-08 ~14:20) | Not the flat square panel: use an **existing shield asset**, and **change the pirate's rig so he holds it like the game's own shield pirate** (may need the real Prime rig, as for Dark Samus). Candidates found 14:30: Echoes `shield` (ANCS b722bc61 / CMDL db123f16, gold faceted disc with open/ready/close clips; tie to the Pirate Commando unconfirmed), the Echoes commando's own shield clips (`B_sheild_ready_commando`, ANCS 8169653a), MPH's Slench dome `BigEyeShield`; pictures in `extract_out/characters_mockups/shield_assets/`. **15:07 research:** the Echoes `shield` disc is the Emperor Ing fight's 15 m shield (only user: ACTR "Shield", Metroid2.pak room d7c3b839), NOT a pirate's. The Echoes commando's real shield = particles on his **L_wrist** (ELSC 6b9a4ace, PART 27f8b9bc, PART ab558818/ada12585 whose particle model is CMDL `33e8713f`, a ~2 m glowing dish). Corruption has a real arm shield: CHAR `a492d721a2cd20b0` "Pirate_Shield" (own 9-bone rig, closed/open clips; its open_shield clip puts it at the pirate's left wrist in the pirate's own space), held by CHAR `b82daba63a625397` "Standard" (`b_shield_c_pirate`, `a_shield_shoot_pirate`, shield turns/bash). Prime 1 troopers and the Echoes commando share 27 bone names (arms included), so the commando's clips play on our trooper's own skeleton. PICK sheets sent: `shield_assets/PICK_shield_asset.png` (A Echoes dish / B Corruption shield / C Slench dome / D panel), `PICK_shield_hold.png` (A copy the stance on the Weavel rig / B pirate skeleton + real clips). Tools `-retropose` gained `--clipsfrom`, `--prop`, `--propadd`, a Corruption path (`--mp3 --char3`). Owner to pick. |
| D14 | Dark Samus's arm cannon (owner, 2026-10-08 ~14:25) | **Always extended forward, like every vanilla hunter** (her rig is changed so the cannon arm aims forward in every pose). |
| D15 | HUD weapon icon (owner, 2026-10-08 ~14:20) | The beam icon **changes instantly** with the weapon change (no spin delay), so the player sees at once which beam is on. Built 14:35 (MphRead `HostInstantWeaponIcon`, on in the app). |
| D16 | Shield goes to the shield pirates (owner, 2026-10-08 ~15:15) | "Save the shield for the actual pirates who hold it and think of a different specialty for troopers": the **Shield Wall is PARKED** for a later shield-carrying pirate character (Echoes Pirate Commando / Corruption shield pirate; D13 research kept). Code kept (`PlayerShield.cs`); no character has it; `-shieldtest` turns it on for the Trooper (`Characters.ShieldWallTest`). |
| D17 | Trooper specialty = WALL CLING (owner, 2026-10-08 ~15:20) | "You can jump at a wall to cling to it for a moment and shoot from the wall before you need to jump again or fall a few seconds later." Built 15:35-15:50, see *Wall Cling: as built*. Bots don't cling yet (not asked; off at first). |
| D18 | Build order + Dark Samus per-game rigs (owner, 2026-10-08 ~16:00) | GO for **Dark Samus (D2/D8/D14)** and the **Scout Drone (D10/D10a)**. "The Prime 3 Dark Samus uses her own rig as well so that Dark Samus still works if a user only imports Prime 3": **each Dark Samus skin carries its own game's skeleton and clips** (Echoes skin: Echoes CINF `4abc8a50` + her Echoes clips; Corruption skin: Corruption CINF `adba16a78d7d49f7` + her 118 Corruption clips), so either import alone gives a whole Dark Samus. |
| D19 | Wall Cling rules (owner, 2026-10-08 ~16:50, after testing) | **At most 3 clings per airtime** ("One initial jump and cling, and two more clings can be chained but not more until the trooper touches the ground"). **Stricter walls:** "Only straight and slightly obtuse angles with the ground", no acute (overhanging) ones, no big grace -> only real collision faces (never edges/corners), -1 deg overhang .. +5 deg leaning back. **Height requirement:** no clinging to "really small short walls" -> the face must cover him feet-to-head where he hits it (3 rays). BUILT + on the Odin 16:59:55. |
| D20 | Real wall-hang animation (owner, 2026-10-08 ~17:05) | "See if we can get the actual wall cling animation or pose": the Prime 1 trooper ANCS 8169653a has `B_intowallhang_pirate`, `B_wallhang_pirate`, `B_outofwallhang_pirate` + `A_aimleft/right/up_wallhang_additive_pirate` (sheet `extract_out/characters_mockups/wallhang/`). Owner picked **the native pirate body** (his own Prime skeleton + the disc's clips while clinging), built on the same native Retro body system as Dark Samus (Phase 5), not a pose copied onto the Weavel rig. |
| D21 | Picker order (owner, 2026-10-08 ~17:05) | The D9 pickers (SELECT HUNTER chooser + lobby badge) come **after the Scout Drone and Dark Samus** (and D20). |
| D22 | Trooper on his own Prime rig + drone just spawns (owner, 2026-10-08 ~17:30, testing the drone on the Odin) | "The trooper is still on Weavel's skeleton and flipping into the drone instead of it just spawning; are we going to give the trooper the proper Prime rig": (a) **Morph = the drone pops out at once** (spawn flash, his body stays standing; merge at once too), no Weavel flip: BUILT 17:30, on the Odin 17:37:01 (replay format 9). (b) **The whole Trooper draws on his own Prime 1 pirate skeleton and clips** (walk, run, strafe, jump, fire, wall hang), not only while clinging (widens D20): the native Retro body system (Phase 5). (c) the HUD line reads **TROOPER ENERGY** for him (owner ~17:20), on the Odin 17:37:01. (b) BUILT 18:05 (section "Native bodies: as built"); owner 18:1x: the wall hang wasn't planted on the wall -> the wall clips' frame turned (+Z into the wall), 18:20. |
| D23 | Dark Samus's spawn (owner, 2026-10-08 ~21:30) | **Her own spawn: the Corruption "echo merge"** (her copies merging back into her): Corruption clips `b_echo_merge_into_dark_samus` + `b_echo_merge_dark_samus` (bodies/MP3DarkSamus) for MPH's Spawn state, in place of her ready loop. Not built yet. To do: look at both clips frame by frame (`-darksamustest MP3DarkSamus --noimport --sheet <png> --clips ...`), check their root travel (her energy-wave landing slid her along the floor after a spawn), map Spawn with `BodyClips.Fit` (stretched to MPH's spawn) + a `FadeOut` into ready; whether the merging copies themselves (other bodies / effects) are needed; the Echoes skin's spawn (no merge clip found yet). Also as built 21:24: jumps for both skins = MPH's on her HD suit (owner: Corruption's own energy-wave leap looked bad); Echoes unmorph = her own uncurl clip. |
| D24 | Scout Drone attacks (owner, 2026-10-08 21:00-22:10) | **Two attacks, one shared wait.** (1) Hold-to-fire blaster burst (fire button): unfold 32 frames (no shots), fire while held up to 2.5 s (both guns every 8 frames, 2/3 damage by turns, 94 when all 38 land), fold 24 frames, then a 2 s wait. (2) Prime 1's **cutting laser** (L2 + touch LASER): the whole A_cuttinglaser_drone clip (134 frames), beam on at 0.95 s, Prime's own sweep (CDrone::UpdateLaser: one of four 10-Prime-unit diagonals through the target, the first player along the aim else the wall, over 1 s), 50 damage over 15 ticks to whatever the beam stops on, then a 5 s wait shared with the burst. Double Damage and the Prime Hunter's 150% apply as to his other shots. Measured: a sweep crossing a body standing still deals 13-20 of the 50 (any distance: the sweep is angular); owner to judge (options: flat hit per crossing, tighter sweep, more per tick). Assumed: 0.5 MPH units per Prime unit, target 4-40 units out. Bots alternate (laser when their target is within 12 units). Code PlayerScoutDrone.cs / PlayerDroneLaser.cs, DroneBody.cs; -dronetest; replay format 10 / 11. Drone reticle (MPH's, where the blasters aim) + touch LASER/BURST buttons. |

## How it fits together

A **character** = a base hunter (the moves, weapons, stats, bots' AI, everything MphRead simulates) + a look (the HD
body on the base hunter's rig, later its own first-person cannon, alt form look, animations). A **skin** is one of the
character's looks. Skins never change the simulation, so LAN matches and replays stay exact; only the seat's skin id
travels with the match setup.

| Character | Base hunter | Skins (folders under `hd/`, from IMPORT GAMES) |
|---|---|---|
| Samus | Samus | ORIGINAL (MPH's own) + every imported Samus suit on Samus's rig: MP1 Power / Varia / Gravity / Fusion / Phazon, MP2 Varia / Dark / Light, MP3 Varia / PED (MP4 suits show where present; Brawl trophies and Dark Samus left out) |
| Trooper | Weavel | MP1PowerTrooper, MP1IceTrooper, MP1WaveTrooper, MP1PlasmaTrooper (Prime 1 disc) |
| Dark Samus (later) | open (see Phase 5) | MP2DarkSamus, MP3DarkSamus |

A skin that isn't imported on a device falls back to the base hunter's own DS model there (it's cosmetic: the match
itself is unchanged). The Trooper needs at least one trooper imported to be pickable.

## Phases

1. **Any seat wears an HD body (engine).** Today only your own Samus does (CampaignSuit, campaign + matches). Generalise
   `CampaignSuit` to any rig hunter (Samus, Weavel) and any player: per-player MphRead hooks in `PlayerDraw.cs`
   (additive), one loaded skin shared by every seat wearing it, per-player morph-ball roll, the pirates' one-handed
   firing pose (`TrophyRigs.OffHandStaysDown`), and MphRead's red damage flash on the HD body. Seat skins come from
   `MatchPlayer.Skin` (+ `MatchSetupText`). Dev launch arg `--es skins` to test before the menus exist.
2. **Menus (mockups first).** SELECT HUNTER: a TROOPER icon beside the seven hunters + a skin chooser when the picked
   character has skins. Lobby: per-seat skin arrows for bots (D4). LAN: the seat's skin in the join/lobby messages;
   replays: each seat's skin (cosmetic field).
3. **Trooper first-person arm cannon (D1).** Cut the right forearm + cannon out of the trooper's own model (its
   R_elbow / R_wrist bones) and draw it as the first-person viewmodel in place of Weavel's, posed like MPH's gun
   (idle sway, recoil, charge). Note: public builds hold the Prime arm cannons (owner 2026-10-07); whether these new
   cannons are public is the owner's call.
4. **Trooper ability + compact alt form (D1, D6).** The owner picks one ability and one alt form (below); both are new
   gameplay in MphRead (movement, attack,
   bots, LAN, replay sim version).
5. **Dark Samus (D2).** Her own clips: Echoes' through our Retro ANIM reader (`Import/Retro/Anim.cs`, MP1 + Echoes) and
   Corruption's through `Import/Retro3/Anim3.cs`, mapped onto MPH's animation states (idle, walk, strafe, jump, land,
   shoot, charge, morph, death; she floats in both games). First-person cannon cut from her model. Her morph balls are
   already imported (`MP2DarkSamusBall`, `MP3DarkSamusBall`). Base hunter (moves/weapons) still to decide.

### Phase 5 groundwork (2026-10-08 ~13:05, PC only, no gameplay or device changes)

**Her clips are on the discs, and they are a full fighter's set.** (`-importcheck readers <echoes.iso> --echoes`,
`-importcheck readers3 --dump MP3/Metroid6.pak,MP3/Metroid8.pak`)
- *Echoes:* ANCS `39900a11` in Metroid1.pak (the ANCS her recipe already uses), characters `Dark_Samus` (model
  `d2c3805b`), `Battle_2`, `Battle_3` (model `faa12c05`: her later, Phazon-cracked look, a second Echoes skin); skeleton
  CINF `4abc8a50`, 39 bones (Samus's names plus `GUN_LCTR`, `GUN_Particle_LCTR`, `Charge_LCTR` on the right forearm:
  the muzzle and charge points). 76 animations: `B_ready` (idle), `B_glideForward/Right/Left/Backwards` (2-key lean
  poses the game blends by speed: walk/strafe), `B_dash_*` in 8 directions (boost dash), `B_180_turn_left`,
  `A_ScatShot` (+ left/right), `A_intoSuperMissile` / `A_SuperMissile` / `A_outofSuperMissile` (charge enter/loop/
  exit), `A_MissileBarrage`, `A_SweepBeam`, `A_Meleepulse`, `A_MeleeDash`, `A_BoostBall*`, `A_DiveAttack*`,
  `R_heavyHit F/B/L/R`, `R_flinch_ADDITIVE`, `R_death_FIRST`, `B_taunt_1/2`, `B_psychoLaugh`, `B_phazeIn/Out`,
  `B_invisible_into/outof`. Her morph ball is rigged too: ANCS `654a3f97` (`B_RollForward`, `B_ready`).
  Smaller ANCSs in Metroid3/4/5 + LogBook hold cutscene and scan copies (same model and skeleton).
- *Corruption:* CHAR `7f305ff98ff689fb` in Metroid6.pak (the recipe's), CINF `adba16a78d7d49f7`, **118 clips**,
  including a real run (`b_ready2run`, `b_run_c`, `b_run2ready`), 4-way float with into/out-of transitions, 8 dashes,
  `b_dodge_left/right`, `b_hop_back`, **`b_hover_2_morphball` / `b_morphball_2_hover`** (she morphs on screen),
  `r_hit_left/right`, `r_heavy_hit`, `add_flinch`, `r_air_death`, turns (45/90/180), melee, homing missile, mega
  blaster charge. Her ball: CHAR `4f6dca8d066f5cfa` (`360_rotation_ds_ball`, `b_ready_morphball`).

**Her own rig works on the PC (path (a) below).** New Tools command `-retropose` (`MphRead.Tools/RetroBodyPose.cs`):
her Echoes mesh skinned by Retro's own CSKR to her own CINF, posed by her own clips with the Prime guns' math
(`GunViewmodel.Pose`), software-rendered. Idle, strafe lean, dash, scatter shot, super-missile charge, heavy hit,
death and taunt all pose cleanly, no broken limbs (pictures: `extract_out/characters_mockups/darksamus/`, sent to the
owner ~13:05). Root motion is pinned for the pictures; the clips move her by metres (the game moves the actor by it).

**What is already there.** Her bodies come off the user's own discs through the troopers' importer
(`Import/Hd/RetroTrophy.cs`, `recipes/hd/MP2DarkSamus.json`, `MP3DarkSamus.json`) with Retro's own skin; her bone
names are Samus's, so today she can also be worn on Samus's DS moves (`TrophyRigIO` maps her to Hunter.Samus;
`Characters.Samus` leaves her out on purpose, D2). The readers for her clips exist (`Retro/Ancs.cs` + `Anim.cs`,
`Retro3/Char3.cs` + `Anim3.cs`). The Prime first-person guns already ship a "Retro skeleton + its own clips on the
GPU" path (`GunImporter` -> `GunWriter` `gun.bin` -> `GunViewmodel`), which a body version can reuse.

**What she still needs (not built).**
1. *Rig, two ways:*
   - **(a) Native (recommended; the owner asked to "fully import her rig").** A body version of the gun file: her mesh
     on her CINF + her clips per key (`GunWriter` minus the gun scaling and LBEAM; its 32-bones-per-material palette
     limit fits her 39 bones only if a material uses <= 32, to check), drawn by the GPU-skin path the Prime guns use,
     posed per player in `CampaignSuit`. Her motion is then exactly the game's.
   - (b) Retarget her clips onto MPH's DS Samus skeleton and pose her through `.retro.mphrig` like the troopers. Less
     rendering code, but every bone's bind difference must be right or arms and legs bend wrong. Not recommended.
2. *Clip map* from MPH's player states (`PlayerAnimation` ids shared by all hunters) to hers: idle -> ready, walk/
   strafe -> glide (Echoes) or float/run (Corruption), turn -> 180/90/45, shoot -> scatter shot (one-shot on the
   torso), charge -> super missile enter/loop/exit, damage -> heavy hit by direction + additive flinch, death -> death,
   morph -> her rigged ball (Corruption has the transition clips), jump/fall -> dive-attack clips or float.
3. *First-person cannon:* her forearm + cannon are the vertices on `R_elbow` (+ hand bones mapped to it), one block;
   cut them out and hang them on the Echoes gun's `elbow` bone the way `GunImporter` hangs an arm model (`a.Arm`), so
   it uses Echoes' own first-person gun clips (sway, recoil, charge); `GUN_LCTR` gives the muzzle.
4. *Skins:* Echoes (`Dark_Samus`), Echoes late form (`Battle_3` / `faa12c05`, new find), Corruption.

**Her base moves: options for the owner.**
- **A. Samus's moves** (morph ball, bombs, beams + pickups). Only her look and clips change.
- **B. A + her float as a look:** feet off the ground, her float/glide clips; no sim change, so LAN and replays stay
  exact.
- **C. Her own kit** (boost dash, Phazon beam / scatter shot, shield): new gameplay in MphRead (bots, LAN, replay sim
  version), like the trooper alt form.

## Trooper ability: concepts (D6: one becomes his hunter ability, owner to pick)

Every MPH hunter has a trait of their own (Trace's cloak, Spire's lava immunity). These were first pitched as alt
forms; the owner moved them here. One trait per hunter keeps him in line with the others; Shadow cloak overlaps
Trace, and Wall Crawler overlaps Spire's wall-climbing alt form, so 1 or 3 fit best.

1. **Jet Trooper (recommended).** Prime's Aerotroopers: a jetpack that hovers and flies in short bursts on a fuel
   meter that refills on the ground, with a rocket salvo as its attack. Nobody else in MPH flies, so it owns the high
   ground and crosses gaps, at the price of being an easy target in the air. The jetpack comes from Prime 1's own
   flying pirate model on the player's disc, so it looks native.
2. **Wall Crawler.** Prime's pirates leap onto and cling to walls: clamp to walls and ceilings, scuttle along them and
   shoot from there. Ambush angles nobody expects. Needs new wall physics (shares work with the Echoes spider-ball idea).
3. **Shield Wall.** Echoes' commandos' energy barriers: plant a frontal shield and fire over it, slow but immune from
   the front. Built for holding nodes, octoliths and defender rings.
4. **Shadow cloak.** Prime's Shadow Pirates: near-invisible and fast, no shooting, decloak to strike. Overlaps Trace's
   cloak, so not recommended.

### Shield Wall: proposal (D11, OK'd ~13:30 as the starting point)

- **Plant:** hold one button (proposal: the zoom button, rebindable; the trooper has no zooming weapon of his own) to plant an energy barrier in front of him. Release to drop it.
- **Locked:** while it's up he can't turn, walk or jump; his body faces the way it was planted.
- **Limited line of fire:** he shoots only through a firing slit: aim moves inside a narrow cone around the planted direction (proposal: 20 degrees left/right, 15 up/down). Anything outside the cone he can't hit, so flanking him works.
- **Blocks the front only:** shots hitting him from inside the front arc (proposal: 120 degrees) are absorbed by the barrier; sides and back take full damage.
- **Limited barrier:** the barrier has its own health (proposal: 60) and breaks when spent, with a **brief stagger**
  (owner; ~0.5-0.8 s, no firing/moving, an existing hit/flinch animation if one fits); after dropping or breaking it there's a cooldown (proposal: 6 s, 10 s if broken). Optional extra limit: a slower fire rate while planted.
- **Implementation:** a new player state in MphRead (input, the facing/aim lock, a front-arc check where damage is applied, the barrier's health and cooldown), a draw for the barrier (first a simple energy plane in MPH's style; Echoes' commando barrier is Retro particle data, which needs the same particle reader as the drone's beam), bots that flank a planted trooper, LAN + replay sim version bump.

### Shield Wall: as built (2026-10-08 13:33-14:15, PC-verified)

All in MphRead `Entities/Players/PlayerShield.cs` (partial PlayerEntity, host-guarded: `HostShieldWall` per seat, off = MphRead's own player) plus one-line hooks in `PlayerEntity.cs`, `PlayerInput.cs`, `PlayerDraw.cs`, `PlayerAi.cs`.

- **Who has it:** a seat with `MatchPlayer.Character = "trooper"` on Weavel (`Characters.HasShieldWall`); `MatchStart` sets it before the match loads. Replays carry the character (replay file format 6); the sim version stays 3 (with no trooper seat nothing changes, golden replays 9/9 exact).
- **Plant:** the zoom button (held; a fresh press to plant). Needs him standing, no cooldown, no stagger, not in or going into his alt form. Release, death, alt form, freeze or leaving the ground drops it. **Not while the Imperialist is equipped (D12):** the button zooms; switching to it drops the barrier.
- **Numbers (sim):** barrier health 60; front arc 120 degrees; firing slit 20 degrees left/right, 15 up/down around the planted aim; cooldown 6 s after letting go, 10 s after a break; stagger 40 frames (~0.67 s) with his own DamageFront clip, no moving, firing or planting; a bot holds it at most 2.5 s.
- **Damage:** after the hunter's damage scaling, a hit from inside the front arc goes into the barrier (beams by their travel direction, other sources by where they are); sides, back, burn ticks and instant deaths hit him. A blocked hit doesn't flinch him or restart his hit-invulnerability.
- **Bots:** a trooper bot plants when the player it's fighting shot it from the front in the last 20 frames, within 30 units, and keeps it up while that player stays ahead. Any bot whose target is a planted trooper facing it strafes to the side away from the barrier's middle.
- **Look (drawing only):** MPH's own `ForceField` model (the door lock fields), 1.7 wide x 1.9 high, 0.75 in front of him from his feet up, palette by skin (Power yellow, Ice blue, Wave green, Plasma red, else the Power Beam's); fades in/out over 8 frames, dims with the barrier's health, flashes on hits, half as strong in his own first person. Seen on the PC in a third-person replay (14:10): a see-through blue plane with the field's moving bands.
- **Sounds:** `FORCEFIELD_APPEAR` on plant, `GEN_OFF` on letting go, `SHOTGUN_BREAK_FREEZE` on a break (not heard yet).
- **Not yet:** LAN/online (off until seat messages carry the character: that seat then plays plain Weavel); the look (D13: a real shield asset held like the game's shield pirate, replacing the square ForceField plane).
- **Tests:** `-shieldtest [arena=..] [time=..] [shooter] [me=trooper]` (Combat Hall 120 s: plants, blocks, flanking, replay exact; Data Shrine 420 s: a break; `me=trooper`: seat 0 a scripted trooper, his barrier's steps listed for desktop replay pictures; `me=trooper imperialist [iconspin]`: seat 0 holds the Imperialist mid-match: zooms, never plants, and the HUD icon shows it at once (or still spins without the hook)).

### Wall Cling: as built (2026-10-08 15:35-15:50, PC-verified; D17)

All in MphRead `Entities/Players/PlayerWallCling.cs` (partial PlayerEntity, host-guarded: `HostWallCling` per seat, off = MphRead's own player) plus one-line hooks: `PlayerCollision.HandleCollision` (where MphRead sets `CollidingLateral`: the wall face's normal), `PlayerInput.ProcessBiped` (wall jump before walking/jumping; walking/jumping gated while clinging; the kick-off steering before the walk speed is added), `PlayerInput.ProcessMovement` (hold after a knockback's speed; no gravity while clinging; start/keep/end after the room collision and grounded update), `PlayerEntity` (reset on init and every spawn).

- **Who has it:** a seat with `MatchPlayer.Character = "trooper"` on Weavel (`Characters.HasWallCling`), local matches and replays (`Characters.AbilitiesFor`; LAN/online off until seat messages carry the character). Replay file **format 7** (same layout as 6): a format 6 file with a Trooper seat (the Shield Wall era) is refused with "recorded with the old Trooper (Shield Wall)". Sim version stays 3 (no trooper seat: nothing changes, golden replays 9/9 exact in all views).
- **Start:** in the air (not standing, not within MphRead's 16-frame grounded window, not on a jump pad's flight, a biped, not frozen, alive) and moving into a face MphRead calls a wall (|normal.Y| < 0.1) faster than 0.02 units/frame horizontally. Sound: `LAND_METAL`.
- **On the wall:** no gravity, no walking, nothing moves him (knockback included; other players' bodies can still push him); aim, fire and weapon switches as usual. Pose: whatever his jump clip ended on (Weavel's own DS clips, no new animation yet).
- **Leave:** Jump = **wall jump** (jump speed up, 0.3 off the wall, jump sound, the jump clip; for 14 frames steering back into that wall is ignored, so holding forward doesn't pull him straight back); Morph drops off (he morphs); after **150 frames (2.5 s)** he lets go and falls; death, landing or a freeze also end it.
- **Limits:** after letting go, 20 frames before another cling; the next cling needs a different wall (normals less than ~45 degrees apart count as the same wall) until he lands, so he can't climb one wall, but can go wall to wall.
- **Bots:** never cling (`!IsBot`).
- **Shield Wall parked (D16):** `Characters.HasShieldWall` is true only while `Characters.ShieldWallTest` is set (by `-shieldtest`), so the code and its test stay alive (`-shieldtest` and `me=trooper` PASS, format 7, replay exact).
- **Tests:** `-wallclingtest [arena=..] [time=150] [bots=1] [out=..] [trace=<cling #>]`: seat 0 a scripted local Trooper (walk to a wall, jump into it; alternate clings hang until timeout or wall-jump after ~0.6 s, firing while on the wall), seat 1 a Trooper bot. Checks: clings, a timeout and a wall jump happen; a timeout lasts 150 frames and he falls after; a wall jump goes up and at least 0.2 off the wall; never the same wall again before landing; at most 3 cm drift on a wall (MphRead's collision eases him ~1 cm out of a sloped face over the first frames); bots never cling; replay through a file exact. PASS in Combat Hall, Data Shrine, Sic Transit, Harvester, Alinos Perch (15:4x).
- **Seen on the PC:** third-person desktop replay of the test (Wave Trooper skin, Combat Hall): hanging face-on against the wall, firing into it (`extract_out/characters_mockups/wallclingtest/`).
- **D19 rules (16:50-16:59, on the Odin 16:59:55, replay format 8):** only real collision **faces** (`PlayerCollision` passes `face: result.Field0 == 0`; edge/corner contacts get a synthesised horizontal normal from MphRead and are ignored); face normal.Y in **[-0.0175, +0.0872]** (1 degree overhang .. 5 degrees leaning back); **at most 3 clings per airtime** (`WallClingChain`, reset on Standing); **body height**: 3 rays from his position (feet+0.12, middle, head-0.12) into the wall (BipedColRadius + 0.35 long, `TestFlags.Players`) must each hit a face in that angle range whose horizontal normal is within ~18 degrees (dot >= 0.95) of the touched one. Test counters `WallClingNormalY`, `WallClingChainCount`, `WallClingTooShort` (frames, not walls). `-wallclingtest` also checks chain <= 3 and the normal range and prints refused-short counts; since 17:34 it skips the wall-jump check when another Trooper's standing body (Weavel's turret sphere) is within 1.5 of the cling spot (it blocks the jump: MphRead collision, not the cling).
- **Not yet / owner to judge:** the feel (cling time, wall-jump push, whether pushing away should drop him), a cling pose (Weavel has no wall clip; Prime 1's `B_wallhang` is on the trooper's own Retro skeleton), turning the body to face away from the wall, bots clinging, LAN/online.

### Scout Drone: as built (2026-10-08 16:40-17:37, PC-verified; D10/D10a/D22)

All in MphRead `Entities/Players/PlayerScoutDrone.cs` (partial PlayerEntity, host-guarded `HostScoutDrone` per seat, set by `MatchStart` when `Characters.HasScoutDrone(seat)` = `ScoutDroneOn` (true) and a Trooper seat) plus hook lines in PlayerInput (fire, hover), PlayerProcess (split/merge, animate), PlayerEntity (init), PlayerDraw (look), HalfturretEntity (standing body, HUD line).

- **Split (Morph):** Weavel's split (half his health stays behind as `HalfturretEntity`, he plays the other half), but **at once** (D22, 17:30): `ScoutDroneSplitAtOnce` ends the morph the frame it starts (no Weavel flip); spawn effect 31 (33 with many players) + `HunterSfx.Spawn`. **Merge (Morph again):** whole at once (`ScoutDroneMergeAtOnce`, no flip back, same flash), at the drone's spot, as Weavel's.
- **Standing body:** the halfturret with `Owner.HostScoutDrone`: never fires; drawn as Weavel's biped in idle where he split (DS: `DrawStandingBody`; HD: `MatchSkins` sets `HostBodyByApp`, `CampaignSuit.DrawStanding`). Hitbox (owner 2026-10-09, replay format 14): the **whole biped** standing there, a player's own standing cylinder (floor to 1.6, BipedColRadius 0.5 + the shot's radius; `HalfturretEntity.HostBodyHit`) for beams and the drone laser; still a Halfturret hit, so **no headshots** on it (was Weavel's 0.45 turret sphere: floor to ~0.9, chest and head shots passed through). Bombs, alt-form contact attacks, Judicator's ice wave and body pushing still use the turret sphere (all near the floor). `-standinghittest` checks it. HUD: **TROOPER ENERGY: n** (ROM line 233 with the word swapped).
- **Hover:** on the floor without Jump it walks as Weavel's legs; Jump held = rise (target Speed.Y 0.1, eased 0.012/frame), let go = sink (-0.06); never above **his head height over the floor under it** (`DroneCap` = MaxPickupHeight - MinPickupHeight = 1.60 for Weavel; floor probe straight down); above the cap (flown off a ledge) it drops back fast (0.03/frame, to -0.3) and never rises.
- **Blasters:** the alt attack (R2 in alt form on the pad) fires twin Power Beam shots from two muzzles in turn (0.22 to the side, 0.25 forward), where he aims (bots: their target), infinite ammo, cooldown max(ShotCooldown, 4) x 2 frames. Replaces Weavel's lunge.
- **Look:** the Prime 1 pirate drone's own body when imported (19:30, "Native bodies: as built" -> Scout Drone's real body); otherwise MPH's Psycho Bit (enemy model 23, its hover clip) at scale 0.45, facing the aim, as the stand-in. Its shots stay MPH's Power Beam (the drone's real beam needs the Retro particle reader).
- **Replays:** format 8 (drone), **9** (instant split/merge); older files with a Trooper seat are refused.
- **Tests:** `-dronetest [arena=..] [time=120] [bots=2] [out=..] [trace=<frame>] [lookback=<frames>]`: seat 0 scripted (walk, split, fly forward firing 150 frames, sink, walk, merge; `lookback` backs away instead, for pictures), seat 1 a Trooper bot, seat 2 Kanden. Checks: body = half at the split, merge = drone + body, never rising over the cap, never over it 60 frames, comes down after letting go, standing bodies never fire, shots fired, rises to >= 0.6 of the cap, replay exact. PASS Combat Hall, Data Shrine, Alinos Perch, Harvester (17:30, format 9).
- **Seen on the PC (17:2x, Harvester + Combat Hall, Wave Trooper skin, third person):** the Psycho Bit drone flying and firing twin shots, merging back into the Wave Trooper; the standing body not framed in a picture yet (the third-person camera looks away from it).
- **Not yet / owner to judge:** drone size (0.45) and look on the device; the standing body in HD on the device; bots' drone use (they split as Weavel's AI does); LAN/online.

### Native bodies: as built (2026-10-08 17:45-18:20, PC-verified; D20/D22b)

The whole Trooper draws on his own Prime 1 pirate skeleton with his own clips (not Weavel's DS rig, not an HD statue rig).

- **Import (`bodies` kind):** Core `Import/Bodies/BodyImporter.cs` (`BodyImportKind`, registered in `ImportService`), recipes `recipes/bodies/MP1{Wave,Power,Ice,Plasma}Trooper.json` (Metroid1.pak, ANCS 8169653a, the four trooper chars), also in `recipes/editions/mp1/trilogy.json` (byte-identical from the Trilogy's MP1/). Written in the guns' MPHGUN1 format by `GunWriter` with `Options` (BaseName `body`, own cfg, a key filter): `bodies/<Name>/body.bin`, `body.gx.json`, `body.cfg`, `<texid>.png`. 35 bones, 3 materials (palettes 6/5/17), all 154 clips, 4.36 MB each.
- **Root motion:** the disc's `root` bone carries the travel (horizontal), `Skeleton_Root` (pelvis) the stance height. RootPin keeps the top bones at their bind spot and keeps the pelvis's own height and sway (minus the linear drift); each clip's travel speed goes to body.cfg `speed <clip> <units/s>` (run 11.65, strafe 4.65, backward 3.42).
- **Runtime:** Core `Campaign/NativeBody.cs`: `NativeBody` (pose = R*T per bone, slerp/lerp blend, skin palettes, `ModelMatrix`: bind feet on MphRead's player feet, scaled to 1.7 tall = 0.469 for the trooper), `NativeBodyAnimator` per seat (clip from MphRead's legs `PlayerAnimation`, walk clips at the ground speed over the clip's own clamped 0.5..2, 0.12 s cross-fade), `BodyClips` (the pirate map: ready loop for idle/turn/shoot/charge, B_runloop / B_backward / B_straf*, B_jumpairloop, B_outofjump landing, R_light* hits; wall: B_intowallhang -> B_wallhang, let-go B_outofwallhang). App `Campaign/CampaignRetroBody.cs` draws it like CampaignSuit (same passes, per-material TEV programs, red hit flash); `MatchSkins` uses it for any seat skin with `bodies/<id>/body.bin` (in place of the suit's body and of the drone's standing body; the HD suit keeps the ball).
- **Wall hang facing (owner 18:1x: "he faces whichever direction instead of having his feet/limbs planted on the wall"):** the pirate's wall clips have the wall on his **+Z** side: in B_wallhang his feet are at model z ~+1.0, the blade hooked in above at ~+0.4, head and gun toward -Z (`-bodytest --bones`). So while a wall clip plays the body's +Z points INTO the wall (`NativeBodyAnimator.WallFacing` = minus the cling normal): he hangs with his back to the wall, blade and claws planted on it, facing out. At his 0.469 scale the feet land 0.47 from his centre, his collision radius is 0.5: on the wall. Let go: the let-go clip (turns him back toward the wall) plays only when he's still looking at the wall (a fall); a jump away cuts straight to the air clip with no cross-fade (the hang already faces out).
- **Tests:** `-bodytest [Name|all] [--copyto <data root>]` (palettes <= 32, every mapped clip present, feet within 15% of the height at the lowest / 25% at the highest pose, height 40..130%): 4/4 PASS 18:05. `-bodytest <Name> --bones <clip,...>` prints bone positions + mesh extent; `--raw <clip,...>` the disc's root/pelvis tracks. `-importcheck guns` 10/10 byte-identical after the GunWriter change. `-importeditions mp1/trilogy` 25/25 (bodies 4/4).
- **Seen on the PC (18:05-18:20, desktop replays, third person):** loads in 57-86 ms, GL ready, drawn in place of the suit; ready/run/jump/hang poses, claws on the floor standing; wall hang before/after pictures `extract_out/characters_mockups/nativebody/`.
- **Torso layer (18:55, on the Odin):** MphRead plays Shoot / ChargeShoot / Charge / Damage* on biped2 only, so the body has an upper-body track: `BodyClips.TorsoRoot` (pirate: `Spine_1`) + `SetTorso(state, clip, loop)`; pirate Shoot/ChargeShoot -> `A_standshot_pirate`, Charge -> `A_gunloop_pirate`, Damage F/L -> `R_flinch1_pirate`, B/R -> `R_flinch2_pirate`. `NativeBody.PoseLayer`: the root bone takes the clip's MODEL-space turn at the legs' position (the run's forward lean would otherwise tip the shot torso over), bones below their own local pose, mixed by a weight that fades 0.12 s in/out; a one-shot restarts when biped2's frame drops (back-to-back hits); cut while a wall clip plays. Order: legs -> torso -> `AfterPose`. Check: `-bodytest <Name> --bones "legs+torso,..." --render <png>` (software front/side); dev clip log `--es bodylog 1`.
- **Aim twist (19:12, on the Odin 19:20:05):** MPH's own spine tilt (MphRead `PlayerDraw`: the biped's `Spine_1` pitched toward the aim, |sin| <= 2896/4096 = 45 deg) after the torso layer: `BodyClips.AimBone` (pirate `Spine_1`), `NativeBodyAnimator.AimTurn`. On the wall the upper body also turns left/right toward the aim (`WallYawLimit` 75 deg, faded in with the hang loop) and the hooked blade arm is put back on its hook by two-bone IK (`BodyClips.WallPin` L_shoulder/L_elbow/L_wrist, `NativeBody.PinLimb`). Checked with `-bodytest MP1WaveTrooper --bones "B_wallhang_pirate@60:0:wall" --render`: the blade wrist stays put in every wall aim, nothing goes through the wall.
- **Dev menu (19:12):** the dev launcher's Multiplayer section has **Your skin** (cycles Wave / Power / Ice / Plasma Trooper, Dark Samus Echoes / Corruption) and **Bots as troopers** (bots 1-3 wear Power / Ice / Plasma); Start match passes them as `skins`. Dev builds only, until the D9 pickers.
- **Scout Drone's real body (19:30, D10/D22):** the Prime 1 Space Pirate drone from the user's Prime disc (`Metroid3.pak` ANCS `5e058399`; `recipes/bodies/MP1GammaDrone` purple, worn with the Wave Trooper; `MP1AlphaDrone` grey/orange, with the others; also in the Trilogy edition) replaces MphRead's Psycho Bit stand-in where the drone flies (`PlayerEntity.HostOwnAlt`). Core `Campaign/DroneBody.cs` (its clip map + `DroneBody.Animator`: hit `R_hitf_drone` > firing `A_intoshootloop` -> `A_shootloop` (blaster arms out) -> `A_outofshootloop` > floating by its own motion `B_floatloopf/b`, `B_floatleft/right`, `B_floatup/down` (0.12 s hold) > `B_ready_drone`), App `MatchDrones.cs` (which seat wears which drone, loads with the native bodies) + `CampaignRetroBody.TickDrone/DrawDrone`. Drawn at `DroneDrawCenter` facing `DroneDrawFacing` (PlayerScoutDrone, draw-only getters), pitched toward the aim up to 45 deg, `DroneBody.Scale` 0.3 world units per model unit (0.5 filled the screen: the alt form's camera sits close and the spike points back at it); the local player's own drone fades out 1.1 -> 0.55 units from the camera (backing up). Draw only: no sim change, no replay format change. Not imported -> the stand-in. Tests: `-bodytest MP1AlphaDrone|MP1GammaDrone` (every clip the animator plays, sane size), desktop replay of `-dronetest`.
- **Not yet:** torso aim (the A_aim*_additive clips); shots fired while on the wall show no torso (the wall clips own the body); one-shot land clips don't restart on a second identical state back to back; Echoes/Corruption paths (Dark Samus) are the Dark Samus session's.

## Trooper compact alt form: brainstorm (owner to pick)

The seven MPH alt forms, so nothing repeats: Morph Ball (Samus: bombs, boost), Stinglarva (Kanden: tail bomb),
Dialanche (Spire: climbs walls), Triskelion (Trace: lunge), Vhoscythe (Noxus: spinning blades), Lockjaw (Sylux:
tripwire trap), Halfturret (Weavel: splits into a turret). All seven are about morph-ball size, so arenas' tunnels and
gaps work for every form: the trooper's must be that size too. Each one is new gameplay in MphRead (movement,
attack, bots, LAN, replay sim version) and needs a DS-model fallback every device has (D5).

1. **Scout Drone (recommended).** He folds into a small hovering pirate probe that floats just off the floor (it
   crosses acid and lava puddles, but can't fly). Attack: a short-range **stun pulse** ring. Utility nobody else has:
   **scan ping**, which marks nearby enemies for him through walls for a few seconds, then cools down. A recon form for a tactical soldier.
   **Look: Prime 1's own Space Pirate drone** (Prime 1 disc, Metroid3.pak, ANCS `5e058399`: characters `AlphaDrone`
   grey/orange, `Gamma_Drone` purple, `UnderWater_Drone`; also ANCS `14794a01` `drone`; models `ef920604` /
   `0b6586cb`, 1390 triangles, its own 33-bone skeleton, 91 clips incl. float loops forward/back, float
   up/down/left/right, hits). A small round armoured body with a red eye and a long spike; scaled down to morph-ball
   size and posed natively like Dark Samus (`-retropose`). Picture: `extract_out/characters_mockups/altform_refs/
   Probe_candidates.png`. Other options: the trooper's own helmet and shoulder armour folded together, or the top
   shell of Echoes' pirate mine (below). Fallback: MPH's own **Psycho Bit** (a small
   hovering campaign drone, `Metadata.EnemyModelNames[23]`), recoloured.
   **Owner's design (D10)** builds on this: deploy with the spawn flourish, Weavel's health split, the pirate left standing, the drone's own attack + beam, flight capped at head height over the surface below. The drone's attacks on the disc (ANCS `5e058399`): `A_blasters1/2` + `A_firingblasters` (twin blasters), `A_shootloop` (+ L/R, into/out of: held fire), `A_cuttinglaser` (sweeping laser), plus `B_dash*` / `R_dodge*` in 6 directions, strafes, 45-degree turns, hits, deaths. **Its real beam:** the shots' look is Retro particle data (PART / ELSC / WPSC, named by the drone's script in its room, not in the ANCS); nothing in the repo decodes Retro particles yet (only their ids), so the drone's actual beam needs a new Retro particle reader + renderer (also useful for Dark Samus's Phazon effects).
2. **Mine Layer.** A compact hovering pirate mine. Attack: drop up to three proximity mines that arm after a second
   and burst when an enemy comes near (cracked open by shots). Strong for holding nodes and choke points. Partly
   overlaps Sylux's Lockjaw trap and Samus's bombs. Look: **Echoes' own pirate mine** (Echoes ANCS `971e251f` in
   Metroid5.pak, 7 clips: ready / open / close / open cycle / death; a white shell that opens on a red core; rendered
   in `extract_out/characters_mockups/altform_refs/Mine_sheet.png`); its long tether would need trimming.
3. **Spring Pod.** A compact armoured pod that charges **high vertical bounces** (Trace's Triskelion lunges sideways;
   this goes up) and lands with a **shockwave** that knocks back and damages nearby players. Reaches ledges and
   high ground no other alt form can. Look: armour plates around a spring core, from the trooper's own model.
4. **Shield Roller.** He curls into an armoured ball behind a frontal energy shield (Echoes commandos' barriers): fast
   roll, blocks shots from the front, rams for damage. Close to Morph Ball's boost; only worth it if Shield Wall
   isn't his ability.
5. **Scarab Swarm.** He breaks into a tight pack of small mechanical scarabs that pours along floors, splits around
   corners and chews whoever it touches (damage over time). The most original look, but a swarm is the most new
   drawing and collision code, and the hardest for bots.

## Mixed imports (design rule, 2026-10-08)

Players in one match can have different imports. The rule:
- **A character's look is cosmetic; its base hunter is what plays.** The match always runs on the base hunter's code and
  moves, which every device has (MPH's ROM is required). A device without someone's import draws that player as the
  base hunter: a TROOPER shows as Weavel (his DS model, same moves, same match); a Samus suit you haven't imported
  shows as MPH's own Samus; Dark Samus (if her moves are A/B) shows as Samus. If she gets her own kit (C), that kit is
  code in the app, so it still plays the same everywhere; only her model falls back.
- **Hitboxes never come from a model (owner, later 2026-10-08).** Every hitbox, collision size and hurt volume is part of the simulation (the character's code + values, the same numbers on every device), never measured from the HD model, the look-alike fallback or the DS model drawn. So a fallback is hit exactly where the real look would be. New forms (Scout Drone, the left-behind pirate, the Shield Wall barrier) get their sizes defined in code. A check for it: a test that runs one match with and without the imports and compares every hit (replays already prove the sim ignores skins).
- **You can only pick what you have imported** on SELECT HUNTER (TROOPER appears once a trooper skin is imported).
- **No game assets ever travel between players or through a server.** Sending another player's imported model to a
  device that hasn't imported it (peer to peer in the lobby, or from a server) is distributing Nintendo/Retro's
  assets to someone who hasn't extracted them from their own disc. That breaks the code-only rule the public build
  stands on (ship readiness: everything is rebuilt from the user's own imports). Skins send only their id.
- **Recipes may live on a server**: they are our own small data (disc id, pak names, hashes, resource ids, a height;
  under 1 KB, fetched from a menu, never in a match). They let new characters/skins arrive without an app update, but
  they only say how to build a model from the player's own disc: no disc, no model.
- **Owner decision D5: fallbacks.** A player without the import sees a **look-alike built from MPH's own models** on
  their own device (e.g. MPH's Samus recoloured dark blue with a cyan glow for Dark Samus; Weavel in trooper colours
  for the Trooper; a recoloured Psycho Bit for a drone alt form). Optional extra: an **import hint** ("X is playing
  DARK SAMUS: import Metroid Prime 2 to see her").
- LAN and online seat messages carry each seat's character + skin ids (2026-10-08, section "Network matches" below);
  a skin the device hasn't imported still draws MphRead's own model until the D5 look-alikes exist.
- **Owner decision D7:** the roster and scoreboard show the picked character's name (TROOPER, DARK SAMUS), drawn as the
  recoloured look-alike.

## Network matches: seat character + skin (2026-10-08, PC-verified)

- **What travels:** each seat's `MatchPlayer.Character` + `Skin` (ids only, "" = none) in LAN protocol **7** and online match protocol **2**: the JOIN (the joiner's own pick: `LanSession.Join` / `NetMatchSession.Join` take `character:` / `skin:`) and every seat in WELCOME + START (`LanSession.WriteSettings`, shared by both). `MatchClient.ForSeat` copies both, so every device's match names the same character + skin per seat. The vanilla menu's lobby keeps a LAN joiner's pick into START (`MultiplayerMenu` Seat.Skin/Character). An id from another device is kept only if it's letters/digits/_/- (max 64): a skin names a folder under hd/. `MatchSetupText` (local matches) gained seat field 9 = character.
- **Drawing:** unchanged. `MatchSkins` draws each seat from its Skin; a skin this device hasn't imported draws MphRead's own model (the D5 look-alike isn't built yet).
- **Old peers:** a JOIN from an older app (LAN protocol 6 or lower, online 1) gets a REFUSE in that app's own protocol, so it shows "refused: this host has a newer version of the game ...: update the game" instead of timing out. A newer app can't join an older host (it can't read the JOIN; older LAN hosts don't list on newer devices).
- ~~Abilities stay OFF in network matches~~ **ON since 2026-10-09 (owner "let's get those abilities online"), see "Trooper abilities in network matches (as built)" below.**
- **Tests:** `-lantest looks=1` and `-onlinetest looks=1` (Tools `SeatLooksTest.cs`): a Varia Samus host, a Wave Trooper joiner, a Plasma Trooper bot. Every device must show the same LOOKS, ABILITIES 0 and the same final scores, and a previous-protocol JOIN must be refused (LAN also round-trips the setup line). PASS 18:0x: LAN (2 devices + 2 bots), online (3 devices + 1 bot through the relay). Baselines `-lantest` and `-onlinetest players=4 bots=0` unchanged.
- **Not yet:** no menu sets a seat's skin or character in a network match (the D9 pickers come after D21; the dev `--es skins` arg is off in network matches), so a device test needs the picker or a dev hook on the LAN / ONLINE TEST entries. Network replays don't record skins (the replay file has no skin field; cosmetic).

- **Extra characters rule (2026-10-10, owner):** TROOPER + DARK SAMUS (characters with `HasOriginal == false`) only when the
  host's ADVANCED box allows EXTRA CHARACTERS, which needs EXTRA PICKUPS ("either trooper plays as we designed him or he
  doesn't play"). Banned: the picker greys them; whoever builds the match (menus, LanSession.Build, NetMatchSession.Build)
  runs `Characters.ApplyExtraRules`, so their seats play the base hunter in its own look. Replays are never touched.

### Trooper abilities in network matches (as built, 2026-10-09, PC-verified)
- **Switch:** `MatchSettings.Abilities` (bool?). The LAN and online hosts set it true in `Build`; it travels at the end of
  `LanSession.WriteSettings` (WELCOME/START/lobby, shared by both) = **LAN protocol 9, online protocol 8**; `MatchClient.ForSeat`
  and the host-migration template copy it. `Characters.AbilitiesFor` = network match ? `Abilities == true` : true, so the
  host's sim and every client's prediction run the same Trooper code; an older peer is refused by the protocol.
- **State:** `Net/SeatAbilities` = per seat the cling (MphRead `PlayerNetCling`: clinging, timer, chain, wall normal x127)
  + the drone (`PlayerNetDrone`: fire state/frame, burst wait, laser frame/pattern/wait/distance), 13 bytes, in EVERY
  snapshot (`CaptureSnapshot`), absent when no seat has an ability. It is the last block of `Snapshot.Write`'s bytes, so LAN
  carries it directly and online's `SnapshotDelta` carries it inside the mode block (whose bytes run to the end; XOR-delta
  like the rest). Clients apply it after the poses (`ApplySnapshot`): puppets take it at once, the local Trooper corrects a
  cling mismatch only after 10 frames (`NetApplyCling`), the drone takes only the laser's sweep + distance.
- **Replays:** format **22** records `Settings.Abilities` after the Skins; older files read it as none, so their network
  matches replay with abilities off as they were played. `ReplayFile.CurrentSimVersion` stays 3 (local-match logic
  unchanged; the protocol bumps keep different Trooper builds apart in network matches).
- **Tests (PASS 21:4x):** `-seatabilitiestest` (40 snapshots: LAN bytes + online deltas, with/without abilities and mode
  block), `-lantest looks=1` (2 devices + 2 bots, ABILITIES 2 = both Trooper seats, same scores), `-onlinetest looks=1`
  (3 devices + 1 bot through the relay, ABILITIES 2, same scores), `-replaygolden` 9/9, `-replaynettest`. The looks tests
  now require ABILITIES = the Trooper seats in LOOKS (was 0). Bots use the drone (burst + laser); no bot clings, so the cling
  sync is PC-checked only by the round trip: device test = owner.

### Turning the Trooper's abilities on in network matches (the plan it was built from)
1. **One switch every device agrees on.** Today each device infers it (`AbilitiesFor` = no remote seats and not a client). Instead the host decides and sends it: a match flag in WELCOME/START (protocol bump), and `AbilitiesFor` reads the flag, so the host's sim and every client's prediction run the same Trooper code.
2. **Sim version gate.** Wall Cling and the Scout Drone are simulation. Every change to their rules must then bump `ReplayFile.CurrentSimVersion` (JOIN refuses a different one; the online game list filters on it), not only the replay format as formats 7-9 did, or two builds with different Trooper rules could play together and drift.
3. **Snapshots.** Clients pull every remote seat onto the host's pose each frame (`PlayerNetState`: position, speed, facing, health, Active/Spawned/AltForm, controls). Ability state isn't in it: the cling (clinging, wall, clings left, timer) and the drone split (the standing body = Weavel's halfturret entity: its position and health, which half is the player) must be added, or derived the same way on clients, or puppets and the local Trooper get corrected wrongly. First check how Weavel's own halfturret syncs today (only a halfturret DamageFlag rides in LastFlags).
4. **Input:** hover (Jump held), blasters (AltAttack) and the cling (jump at a wall) already travel in `NetControls`; the client's prediction runs the same rules once the switch is on.
5. **Replays:** a network match's replay records the seats (Character, format 6+) and Remote flags; with abilities on online it must also record the switch (replay format bump) so older network replays keep playing without abilities.
6. **Test:** `-lantest looks=1` / `-onlinetest looks=1` with the switch on: the same scores on every device, puppets within tolerance while a seat clings and flies the drone, and the host's replay exact.

## Open (owner)

- Team modes recolour hunters orange/green; an HD skin has no team colour. Proposal: tint the skin toward its team
  colour (the shader's recolour already exists).
- ~~Dark Samus's base moves (Phase 5)~~: decided, D8 = B.
- Public builds and the new first-person cannons (Phase 3).
