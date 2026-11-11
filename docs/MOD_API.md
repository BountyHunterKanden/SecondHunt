# Mod API: what exists, the full coverage map, and the plan

Started 2026-10-30. This is the map of the Lua mod layer. It covers:

- what's built, and which game moments a mod can hear today (sections 1-3);
- **everything a modder should be able to change**, with where it lives in the code and what each piece needs
  (section 4);
- how a mod uses content from other games without shipping it (section 5);
- the mod package and manifest (section 6);
- the in-game mod browser, the mod index repo and how mods get submitted (sections 7-8);
- the phases, the tests and the owner's decisions (sections 9-11).

The owner's goal (2026-10-30): "a modder can do pretty much anything they can think of with this game", including
swapping the player model for characters from other games, making or changing game modes, and graphics changes or
enhancements, with as much content as possible covered by the API. Mods with other games' content must not ship any
of that content (the gen1recomp approach). The player-facing guide is [MODDING.md](MODDING.md). Earlier plans are in
IDEAS_SURVEY §6 and ONLINE_PLAY §8b.

Paths are under `src/` unless they start with `docs/`. `Core/` is `MphRecomp.Core`, `App/` is `MphRecomp.App`.
Line numbers are from 2026-10-30, so check them before editing.

**Phase 1 built 2026-10-30:** manifest v2, load order, `require`, on-screen errors, crash-safe
data, typed settings, `mod.config`, `mod_api.lua`, PC hot reload, the gameplay kind with DetMath / mod RNG, seven
filters, `HostEnemyDamage`, and replay format 75. Rows marked **built** below are done; section 9 has the limits.

## 1. What exists

| Piece | Where | What it does |
|---|---|---|
| Lua runtime | `Core/Mods/ModHost.cs` | MoonSharp, `CoreModules.Preset_HardSandbox` (no `os`, `io`, `dofile`). One `Script` per mod. A sandboxed `require('a.b')` loads `a/b.lua` from the mod's own folder only. Gameplay mods get a DetMath `math` and a `math.random` / `mod.random` stream seeded from `ModSeed ^ fnv(id)`. |
| Runaway guard | `ModHost.HookInstructionLimit` (200k) / `LoadInstructionLimit` (2M) | Every call runs as a coroutine with an auto-yield counter. A mod that runs past its budget is turned off with an error, and the game carries on. |
| Discovery | `Core/Mods/ModManager.cs` | Finds `<mods>/<id>/mod.json` (a bad one becomes an entry with a `Problem`, not a silent drop), keeps the load order (`RecompSettings.ModOrder`) and the on/off list. `Resolve()` sorts `EnabledInOrder` so a mod loads after what it `Requires`, and leaves out mods with a missing requirement, a conflict or a newer `Api`. |
| Menu | RecompMenus MODS page, pause MOD SETTINGS | Lists mods with OFF / ON / ERROR and a "can't run: ..." line, turns them on and off (`RecompSettings.DisabledMods`), edits each mod's typed `Settings` (list, bool, int, range; `setting_changed` fires on a change). |
| Event bus | `MphRead/GameEvents.cs` | `Any`, `Tick`, `Listener`, `Raise(name, args)`. Call sites do `if (GameEvents.Any) GameEvents.Raise(...)`, so with no mod listening each hook costs one static bool read. |
| Filters | `MphRead/GameFilters.cs` | One static bool per filter (`Damage`, `Kill`, ...), set by the host when a gameplay mod hooks it. Sites do `if (GameFilters.Damage) damage = GameFilters.PlayerDamage(...)`. Section 4.3. |
| Live attach | `Core/Mods/LiveMods.cs` | Two hosts. Cosmetic mods load when a game starts (`CampaignLaunch.Create`). Gameplay mods load per match in `StartMatch(recorded, out seed)` (from `MatchReplay`), which returns the replay header, or for playback loads the recorded mods with their recorded settings and seed. Errors go to `LastErrors`, an on-screen toast (`App` `ModToasts`) and the MODS page. `Poll` flushes `mod_data.json` every 5 s and does PC hot reload (a `FileSystemWatcher`, on in the desktop app). Writes `mods/mod_api.lua` (`ModApiMeta`, `-modmeta`). |
| Prototype session | `Core/Game/GameSession.cs` | The old headless host the example mod test uses. It fires `start`, `room_enter`, `tick(dt)`. |
| Asset layers | `Core/Assets/AssetVfs.cs` | Layered file system, last layer wins. Only the prototype and tests use it; the live game uses Mod content below. |
| Mod content | `Core/Mods/ModContent.cs`, hook `Read.HostReadFile` (`Read.ReadBytes` / `Read.ReadFileBytes`) | A mod's `files/` folder mirrors the game's file tree; while the mod runs, the game reads its copy (case-insensitive). Cosmetic mods: text (`stringTables*`) and HUD pictures (`hud/`) only, other files skipped with a log line. Gameplay mods: any game file, for the matches they run in (the files are in the replay sha). Gameplay beats cosmetic; later in MODS order wins. Never mounted online. Phase 3, built 10-30. |
| Tests | Tools `-modtest`, `-exampletest`, `-modapitest`, `-modmgrtest`, `-vfstest`, `-modcontenttest`, `-replaygolden mods` | Headless, no device needed. See section 10. |

### The `mod` table

| Call | In a live game |
|---|---|
| `mod.id`, `mod.name`, `mod.version` | Yes |
| `mod.log(msg)` | Yes. Goes to the app log (tag `MPHMods`) and `<data>/mods/mod_log.txt`. |
| `mod.on(event, fn)` | Yes. Section 2 lists the events. |
| `mod.save(key, value)` / `mod.load(key, default)` | Yes. Saved to `<data>/mods/mod_data.json` under the mod's id, every 5 s while it changes and when the game ends. 1 MB cap per mod. |
| `mod.setting(id)` | Yes. The player's pick for one of the manifest's `Settings` (boolean / number / string by type), or its default. In playback, the recorded value. |
| `mod.config(path, default)` | Yes. The game's tuning config (`ConfigManager.Default()`); cosmetic mods can also read `Settings.<name>` (a read-only `RecompSettings` view). |
| `mod.filter(name, fn)` | Gameplay mods. Section 4.3. |
| `mod.random(m, n)` | Yes. The mod's own stream; recorded with the replay for gameplay mods. |
| `mod.asset(path)` | A file's text: the game file at that path (a mod's `files/` copy when one is mounted), else the mod's own file, else `nil`. |
| `mod.spawn(model, x, y, z)` | Gameplay mods only; returns `nil` in live games (prototype `SimWorld` only) until phase 2's runtime spawn. |

## 2. Events (the hook map)

Every event only reports something that happened. A player is named by slot (0-7) and any other entity by its id.
-1 means none or unknown. Enum values arrive as their names (strings).

### Game and world

| Event | Args | When | Call site |
|---|---|---|---|
| `start` | none | Once, when the game's mods load, before the room | `LiveMods.Attach` |
| `game_start` | `mode, room, roomId, players` | The first room has loaded (`SinglePlayer` in the campaign) | `Renderer.cs:502` (end of AddRoom) |
| `tick` | `dt, frame` | Every simulation frame (60 a second, `dt` = 1/60). Has its own flag (`GameEvents.Tick`) so a mod that doesn't hook it costs nothing per frame. | `Renderer.cs:1589` |
| `room_enter` | `room, roomId` | A campaign room transition has finished | `RoomEntity.cs:831` (EndTransition) |
| `match_end` | `mode` | The match is over (game over screen) | `GameState.cs:419` |
| `door_unlock` | `roomId, doorId` | A locked door opens | `DoorEntity.cs:475` (Unlock) |
| `artifact` | `model, artifactId` | An artifact is picked up. `model` 8 or more = an octolith. | `ArtifactEntity.cs:168` |
| `enemy_killed` | `enemyType, enemyId, killerSlot` | An enemy dies (also Echoes crates and similar in matches) | `EnemyInstanceEntity.cs:460` (TakeDamage) |

### Players

| Event | Args | When | Call site |
|---|---|---|---|
| `spawn` | `slot, respawn` | A player spawns or respawns | `PlayerEntity.cs:993` |
| `damage` | `slot, attackerSlot, amount, beam, fatal` | A player takes damage. `beam` is the weapon's name or `""` (falls, hazards). | `PlayerEntity.cs:1926` |
| `death` | `slot, attackerSlot, beam` | A player dies | `PlayerEntity.cs:2071` |
| `fire` | `slot, beam, charged` | A shot leaves the gun | `PlayerInput.cs:1245` |
| `bomb` | `slot` | A morph ball bomb is laid | `PlayerInput.cs:1864` |
| `weapon_switch` | `slot, beam, previous` | A player changes weapon | `PlayerEntity.cs:1394` |
| `pickup` | `slot, item` | A player picks up an item (`ItemType` name) | `PlayerProcess.cs:1487` |
| `alt_form` | `slot, altForm` | A player goes into or out of alt form (`altForm` true/false) | `PlayerProcess.cs:1993` |
| `scan` | `slot, scanId, entityType` | A scan finishes | `PlayerScan.cs:354` (AfterScan) |

### Modes

| Event | Args | When | Call site |
|---|---|---|---|
| `octolith_pickup` | `slot, team, bounty` | Capture/Bounty: an octolith is taken | `OctolithFlagEntity.cs:237` |
| `octolith_return` | `slot, team` | Capture: a team's octolith goes home | `OctolithFlagEntity.cs:225` |
| `octolith_capture` | `slot, bounty` | An octolith is scored | `OctolithFlagEntity.cs:346` |

Counts from one pass of the 10 goldens (`-replaygolden mods`), to show what a match produces: a 4-player Battle on
Combat Hall heard tick 4122, fire 195, damage 83, weapon_switch 51, pickup 32, spawn 14, alt_form 12, death 10, bomb 7,
game_start 1, match_end 1. Capture and Bounty add `octolith_pickup`, and the Echoes arenas add `enemy_killed`.

### Adding an event

1. Pick the moment in MphRead. Put the call after the game's own state change, so the mod sees the result.
2. Write it as one line, with primitive args only:
   `if (GameEvents.Any) GameEvents.Raise("name", a, b); // recomp: mod event (GameEvents.cs)`
3. Add a row here and a check if it needs one. Run `-replaygolden` and `-replaygolden mods`: both must stay 10/10 exact.

## 3. Rules

- **Listeners only watch; filters change.** Nothing an event listener does goes back into the simulation, and
  `Raise` swallows any exception. The only way back in is a gameplay mod's filter (`GameFilters`), under the
  determinism rules below, and a replay of a filtered match records the mods so it plays back exact.
- **Offline games only, today.** `CampaignLaunch.Create` skips mods for a `net` launch and whenever
  `OnlineMatch.Current` or `LanSession.Current` is set. This is decision 10 (no mods with strangers). Section 4.14 and
  phase 6 cover online. A replay being played back loads only its recorded gameplay mods (cosmetic ones are skipped
  so their hooks don't fire twice).
- **Two kinds of mod** (owner decision §11.2). Every manifest says `"Kind": "cosmetic"` or `"Kind": "gameplay"`.
  - A **cosmetic** mod can only use the presentation APIs: drawing, sound, music, HUD, menus, its own settings, and
    reading game state. It can't change the simulation, so it never goes in the replay header, and two players with
    different cosmetic mods can still play together.
  - A **gameplay** mod may also use filters, spawns, modes, data patches and messages. It runs under the
    determinism rules (seeded mod RNG, DetMath, no clock), goes into the replay header with its version and hash,
    and must match on every machine in a game.
  - The engine enforces the kind, not the mod. A cosmetic mod that calls `mod.filter` or `mod.spawn` gets an error
    (at load when it's called at the top of the script, as filters usually are).
- **The API carries the online gate.** Some presentation changes would give an edge online (a wider FOV, no fog,
  see-through walls, a tiny or glowing player model). Each API in section 4 has an **Online** column: `ok` (allowed
  in any game), `clamped` (allowed, but the engine holds it to what the game's own settings allow) or `off` (ignored
  in online games). A mod can't opt out of that, so "no advantage, no cheating" holds whatever a mod tries.
- **No IP in mods.** A mod package may not contain ROM, disc or game files, or art taken from them. Other games'
  content comes from the player's own imports through recipes (section 5). The packer and the index CI refuse
  packages that break this (section 6.3). New art made in the game's style is fine (owner rule: in-style additions
  OK, never recreate shipped assets).
- **Where the files are.** Mods go in `<data>/mods/<id>/` (`mod.json` and `main.lua`). On Android `<data>` is
  `/sdcard/Android/data/com.mphrecomp.app/files`; on Windows it's `%LOCALAPPDATA%\Second Hunt` (`DesktopPaths.Root`).
  The log is `mods/mod_log.txt`, rewritten each game. Saved values are in `mods/mod_data.json`.
- **Never writable from a mod**, whatever the kind: `CameraInfo`, `SetHostCamera`, `Renderer.SpawnEffect` (it draws
  from the game RNG and the shared effect pool, so it would break replays), `QueueHudMessage` (sim state), `HostFade`,
  light entities, and anything GL. Lua never calls GL: presentation calls write into a double-buffered state that the
  GL thread reads (section 4.6).

## 4. The coverage map

This is the "anything a modder can think of" list. Each area gives what a modder wants, where it lives, what exists
today, the API it needs, a size (S = a day or so, M = a few days, L = a week or more), the kind it needs
(**C** cosmetic / **G** gameplay), the online gate, and the phase (section 9).

### 4.1 Characters, models and skins (including characters from other games)

What's there today. There are three ways a non-ROM model gets into the game:

1. A DS `Model` built in code by name: `Read.cs:29` `HostBuildModel` chain. `DarkSamusStandin.cs:35-39` uses it.
2. An HD skin: `<data>/hd/<id>/*.dae`, auto-rigged onto a hunter (`CampaignSuit.cs:198` TryLoad, `:192` RigHunter,
   `Core/Anim/TrophyRigger.cs`).
3. A native body: `<data>/bodies/<id>/body.bin` (MPHGUN1, with its own skeleton and clips;
   `CampaignRetroBody.cs:87`, `Core/Campaign/NativeBody.cs`).

Per seat, `MatchSkins.cs:231-257` (StartSkinLoads) and `:356-418` (SetSkinHooks) fill the `PlayerDraw` host fields:
`HostOwnBiped` :44, `HostBodyModel` :103-116 (needs `Spine_1` and `R_elbow` bones), `HostAltModel`/`Recolor`
:121-122, `HostOwnAlt` :47, shoot point and muzzle :45/46/54, damage flash :57, trail :60, recolors :63/:82, name
:65, track nodes :68, gun light and bomb colours :85-91.

What's in the way:

- The registries are hard-coded: `Characters.All` (`Characters.cs:89`, abilities :96-140) and `TrophyRigIO.RigOf`
  (`:14-47`). `BodyClips.Register` (`NativeBody.cs:761`) is the only open one.
- `ImportedContent.Usable` makes public builds refuse hand-dropped folders (it wants a recipe hash).
- About 25 loaders call `File.ReadAllBytes` directly and skip `Read.ReadBytes` (`Read.cs:642`), so nothing can
  layer over them: `Collision.cs:67`, `NodeData.cs:27`, `AiPersonality.cs:105/242`, `Sound.cs:239-419`,
  `FhSound.cs:61`, `HudInfo.cs:423/430/722/1021/1258`, `Strings.cs:38/238`, `Frontend.cs:167`,
  `CameraSequence.cs:535`, `Archive.cs:87/148`.
- There's no runtime glTF loader. Retargeting (`UalRetarget`) exists only in the tools.

| Want | API | Size | Kind | Online | Phase |
|---|---|---|---|---|---|
| Swap my hunter's body for another model (any game, via section 5, or the modder's own art) | `character.register{ id, name, base = "Sylux", body = "<derived or mod path>", rig = "auto" or bone map, clips = {...}, alt = ..., gun = ..., colours = {...} }`. It becomes a picker entry like the built-in characters. The body is retargeted to the base hunter's skeleton at import time and saved as `body.bin`. | L | C | clamped (drawn at the base hunter's height and outline, with team colour and damage flash forced on) | 3 |
| New skin for an existing hunter | `skin.register{ hunter, id, model or textures, recolors }`. Feeds `Characters.Skins`. | M | C | ok | 3 |
| Retexture or recolour any model | Texture pack (section 4.6), plus `HostExtraRecolors` (`Read.cs:25`) for palettes | M | C | clamped (no fullbright or transparent player textures online) | 3 |
| Own animations | `BodyClips.Register` already takes clip sets. Expose it as `character.clips(id, {idle=..., run=...})` over the 26 `PlayerAnimation` slots (`PlayerEntity.cs:76-105`). | M | C | ok | 3 |
| Alt form model | `HostAltModel` per seat | S | C | clamped | 3 |
| Arm cannon / first-person gun | `GunViewmodel.cs`, `MatchSkinGun.cs:31`; the gun importer (`GunImporter`/`GunWriter`) drops `gun.bin` today. Keep it and expose `character.gun`. | M | C | ok | 3 |
| A new character with its own abilities | `Characters.All` becomes a registry. Abilities pick from the existing hunter kits (`base`), or are built from filters and spawns. Hitbox and height stay the base hunter's (bodies are scaled to 1.7). | L | G | off until phase 6 | 3 + 1 |
| Peers see my model | Only the Character and Skin ids travel over the network. A peer without the mod sees the base hunter's look (`LookAlikes`). Keep that: a missing cosmetic never blocks a game. | S | C | ok | 6 |

Engine work this needs: route every raw `File.ReadAllBytes` through `Read.ReadBytes` and mount `AssetVfs` there
(MphRead edits are one line each, host-guarded); let `ImportedContent.Usable` accept a mod's derived cache
(section 5.4); move `UalRetarget` and a glTF reader from Tools into `Core` so the import step can run on the
device.

### 4.2 Game modes and match rules

Today the mode is a fixed enum (`Formats.cs:1285` `GameMode : byte`, values 2-14, about 200 references). There's one
`ModeState` delegate per mode (`GameState.cs:137`, private setter, called at :331; vanilla rules :597-700), an
if-chain for setup (`GameState.cs:191`), kill scoring hard-coded per mode (`PlayerEntity.cs:2317-2430`), ranking
comparers that switch on the mode (`GameState.cs:1270-1420`), and frozen entity layers (`Metadata.cs:419-437`,
which throws on an unknown mode). The precedent for changing rules is `MatchSettings`: nullable per-match flags,
recorded in the replay, applied in `MatchStart.StartMatch` (`MatchStart.cs:16`; global :30-64, per player :87-134,
pickups :153, AddRoom :154, GameState overrides :156-164).

| Want | API | Size | Kind | Phase |
|---|---|---|---|---|
| A new mode | **Built (phase 2, runtime):** declared in mod.json `Modes` (`Id`, `Name`, `Description`, `Base` = one of Battle/Bounty/Capture/Defender/Nodes/PrimeHunter/Survival, starting `PointGoal`/`TimeLimit` minutes/`TimeGoal` seconds), gameplay mods only. The mode plays as its `Base` (no new mode numbers, nothing online changes); the rules come from the mod's `mode.setup/tick`. `MatchSettings.ModMode` = `"<mod id>:<mode id>"` picks it; a mod that declares modes runs only in matches playing one of them (`LiveMods.StartMatch`). The choice is recorded as the reserved setting `$mode` in the mod's replay row (no format bump); `mode.current()` returns `mode_id, mod_id`. `LiveMods.Modes()` lists them for a menu. Menu entry: pending (owner picks the layout). | L | G | 2 |
| Mode setup | **Built (phase 2):** `mode.setup(fn)` runs once from `LiveMods.MatchSetup`, called in `MatchStart` after the match's overrides and before `scene.OnLoad`. `fn` writes the goals and time with `match.set_point_goal/set_time_goal/set_time_left` (reads: `match.point_goal/time_goal`). Teams are not writable yet. | S | G | 2 |
| Per-frame rules and win check | **Built (phase 2):** `mode.tick(fn(dt))` runs in place of `ModeState` through the additive MphRead hook `GameState.HostModeTick` (`LiveGame.ModeTick`); `mode.vanilla()` runs `ModeState` once that frame; if every mode.tick mod has stopped, `ModeState` runs. The time limit still ends the match. Also **built:** `match.finish()` via `ForceEndGame` (named `finish` because `end` is a Lua keyword; the results rank by the scores as they stand, so set them first). Read side, open to every mod: `match.active/mode/teams/time_left()`, `match.setting(name)` (any plain `MatchSettings` rule by property name, case-insensitive; network fields hidden), `match.player(slot)` -> `{hunter, team, bot}`. | M | G | 2 |
| Scores | **Built:** `score.get/set/add(slot)`, `score.team_get/team_set/team_add(team)`, `score.kills/deaths(slot)` over `GameState.cs:109-135` (`Core/Mods/ModGame.cs` `IModGame`/`LiveGame`). Reads are open to cosmetic mods; changes are gameplay only. | S | G | 2 |
| Kill and suicide scoring | **Built:** `mod.filter("kill", t)` runs after the vanilla scoring with `vanilla_points`; `t.points` (default 0) is added on top to `t.scorer` (default the attacker). Replacing the vanilla scoring outright waits for phase 2's `mode.*`. | M | G | 1 |
| Rankings | `mode.rank(a, b)` or a sort key | M | G | 2 |
| Respawn rules | **Built:** `filter("respawn_time")` (`frames`, vanilla 180) and `filter("spawn_point")` (`index` into the room's spawn points). Lives wait for phase 2. | M | G | 1 |
| Change an existing mode | The same hooks with `mode = "Battle"` etc. | S | G | 1-2 |
| Score text and messages | `hud.score_text(slot)`, `hud.message(text, frames)` drawn by the app (not `QueueHudMessage`) | M | C | 4 |
| Bots play the mode | **Built (phase 2):** `bot.set_objective(slot, x, y, z)` / `bot.objective(slot)`. Where a bot would wander to a random nav node it heads for the node closest to the objective instead; fights, pickups and the mode's own targets still come first (hook `PlayerEntity.HostBotObjective`) | M | G | 2 |
| Show up in the match menu | Owner pick 10-30: at the end of the GAME MODE list, after the vanilla modes, offline only. Built: `MatchSetupText` carries `ModMode` (`mm=`, never with a net role). Built 10-30: a row at the end of the GAME MODE list (`MultiplayerMenu.ModModes.cs`): arrows step through `LiveMods.Modes(modsDir, settings)`, A or a tap on the name opens the Base's rules page with the mode's starting goals; the mode's description shows while the row is focused; hidden online and for LAN joiners, LAN hosting refused while one is picked; PC check `-mpmenu modmodes` | L | G | 2 |
| Advanced match settings for the mode | `mode.settings{...}` rows. The EXTRA RULES box (`MultiplayerMenu.ExtraRules.cs`) is full at 5 rows, so it needs a sub-page. | M | G | 2 |

The smallest set for a brand-new playable mode is: register, setup, tick and end, the kill and respawn filters, load-time
entities (4.4), the mod RNG (phase 1) and the replay fingerprint. Score text and bots make it polished.

### 4.3 Gameplay values

| Want | Where | API | Size | Kind | Phase |
|---|---|---|---|---|---|
| Damage | `PlayerEntity.cs:1720` TakeDamage: effectiveness :1776, double damage :1792, team ignore :1806, `DamageLevels` :1833 | **Built:** `mod.filter("damage", function(t) t.damage = t.damage * 2 end)` after the damage level and before shields and death. Fields `target, attacker, damage, beam, headshot`. | S | G | 1 |
| Health, ammo, speed, jump, every movement constant | `struct PlayerValues` (`PlayerEntity.cs:2860`, about 110 fixed-point fields), table `Metadata/Player.cs:206`, picked at `PlayerEntity.cs:585` | **Built** as `mod.filter("player_values", t)`: every int/ushort field by its C# name (`t.JumpSpeed`), patched into a copy at spawn; the shared table is never touched. | M | G | 1 |
| Weapon stats | `WeaponInfo` (`Metadata/Weapons.cs:133`), `Weapons.Current` :373 (settable); tables MP :1878, 1P :375, bot :6367, enemy :3381 | `weapons.patch("Magmaul", {damage = ..., cooldown = ...})` at match start | M | G | 3 |
| Pickups | `PlayerProcess.cs:1274` PickUpItems (switch :1328, health amounts :1591); `PlacedPickups.cs:98` | **Built:** `filter("pickup", t)` (`player, item, allow`; `allow = false` leaves the item). Changing the amount and `pickup.place(type, pos, respawn)` wait for phase 2. | S-M | G | 1 / 2 |
| Enemy stats | Health set in each enemy's Initialize (e.g. `00_WarWasp.cs:47`); `Metadata/Enemies.cs:2947` damage multipliers, :3016 effectiveness | **Built:** `filter("enemy_damage", t)` (`enemy, attacker, damage`) on every hit on an enemy, and `PlayerEntity.HostEnemyDamage` is real (the `#if ENEMY_DAMAGE_HOOK` in `EnemyPlacement.cs` is gone). **Built (phase 2):** `filter("enemy_spawn", t)` (`enemy, room, health`): one site in `EnemyInstanceEntity.Initialize` right after the enemy's own set-up, so every enemy type is covered; health and max health both follow `t.health`, clamped to 1..65535. | M | G | 1 / 2 |
| Rules a match already has | `MatchSettings.cs:76-301` | `match.setting(name)` read-only | S | C | 1 |

### 4.4 Rooms, entities, triggers and the campaign

| Want | Where | API | Size | Kind | Phase |
|---|---|---|---|---|---|
| Add things to a room when it loads | `SceneSetup.cs:212` `HostExtraEntities` (chained, applied :383-386); used by `PlacedPickups.Extra` and `EnemyPlacement.Install` **Built (phase 2):** `room.on_load(function(r) ... end)`, gameplay mods only. `r.id`, `r.name`; `r.add_pickup(item, x, y, z, respawn?)` (an `ItemType` name, respawn seconds, default the item's usual) in any room; `r.add_enemy(id, x, y, z, facing?)` (an `EnemyCatalogue` id: `mph:Zoomer`, `fh:Metroid`, `mph:Hunter:Noxus`; one enemy, spawned once on entry) in campaign rooms only for now. At most 64 adds per mod per room; a handler that errors adds nothing. `LiveMods.HookRooms` chains `HostExtraEntities` once (after `EchoesArena`'s, so last in the chain); the ids start at 30000. `r.add_trigger` is not built yet. | S | G | 2 |
| Spawn and remove at runtime | `Scene.cs:18` AddEntity, :67 TryGetEntity, :72 RemoveEntity; `EnemySpawnEntity.cs:340` | **Built (phase 2):** `entity.enemies()` (handles of the enemies alive now, scene order, numbered from 1 per room) and `entity.get(h)` (`kind`, `type`, `x`, `y`, `z`, `health`, `max_health`, or nil) for any mod; `entity.spawn_enemy(id, x, y, z, facing?)` (an `EnemyCatalogue` id, campaign rooms only for now) and `entity.remove(h)` for gameplay mods. Spawn/remove are queued and carried out at the end of the frame (the `tick` spot, after the entity loop), at most 16 per mod per frame; a turned-off mod's queued ops are dropped. Remove counts as the enemy leaving range for its spawner (no drop) and refuses boss parts, hunters, spawners and locks (`LiveEntities.Removable`). `Core/Mods/ModEntities.cs`; no MphRead edits. | M | G | 2 |
| Doors, triggers, cutscenes (the game's level scripting) | `Messaging.cs:36/46` SendMessage (queue of 40, processed `Renderer.cs:1583`); `Message` enum (`Enums.cs:350`); `EntityBase.cs:760` HandleMessage | **Built (phase 2):** `mod.on_message(entity id or message name, fn)` for any mod, called as `fn(name, from, to, p1, p2)` as each message is delivered (ids -1 = none, a param that isn't a number is nil; hook `GameEvents.Messages`, set only while a mod watches, raised at the top of `DispatchMessage`). `msg.send(name, from?, to, p1?, p2?, delay?)` for gameplay mods: only the 12 messages in `ModHost.Sendable` (SetActive, Trigger, Unlock, Lock, Activate, Complete, Unlock/LockConnectors, Set/ClearTriggerState with the bit 0-31 as p1, PlatformWakeup/Sleep), delay 0-600 frames, at most 16 per mod per frame, queued and sent at the end of the frame after `entity.*` ops; a missing entity id is reported, not sent. | S-M | watch C, send G | 2 |
| Entity hooks | `EntityBase.Process`, `Destroy`, `HandleMessage` | Before and after hooks filtered by entity type | M | G | 2 |
| Per-room scripts and waves | `EnemyPlacement.cs` (per-room JSON, waves, buffs, door gates; `Tick` :453 called from `CampaignHost.cs:199`); `Rooms.cs:4091` RoomMetadata | Expose waves and gates; `room.on_enter` filter | M | G | 2 |
| Campaign progress and save data | `Core/Save/RecompSave.cs` (`SaveManager.GetModData/SetModData`); `StorySave` | **Built (phase 2):** `mod.campaign.get(key, default?)` (any mod) and `mod.campaign.set(key, value)` (gameplay mods; numbers, strings, booleans, tables; 64 KB per mod; returns false with no campaign file loaded). Kept per mod id in `campaign<slot>.json` as a `Mods` object (`CampaignSaveFile.Mods`, older builds skip the field). `CampaignShip.AdoptSave` hands the file's values to `LiveMods.AdoptCampaign`; `SaveShip` writes `LiveMods.CampaignDataForSave()` (values of mods not loaded this run are kept). They reach disk only at the game's own ship saves, so loading an older save brings back its values. Matches have no campaign file (get gives the default). | M | G | 2 |
| Scan text, strings | `Strings.cs:38/238` (`Read.ReadFileBytes`, routed 10-30) | A cosmetic mod's `files/stringTables*/...` (`ModContent`) | S | C | 3 (built) |

### 4.5 Bots

`PlayerAi.cs` (about 12,250 lines) has mode checks at :392/:427/:898/:3238/:5484/:5803-5833/:10909. Personalities
load in `AiPersonality.cs:23` (`LoadAll(mode)`, raw file reads at :105/:242). About 25 bot flags sit in
`MatchStart.cs:34-54`.

| Want | API | Size | Kind | Phase |
|---|---|---|---|---|
| Bots chase a mod's objective | **Built (phase 2):** `bot.set_objective(slot, x, y, z)` (just `slot` clears it), `bot.objective(slot)` -> x, y, z or nil. A position only; follow an entity by setting its position each tick | M | G | 2 |
| Bot personality tweaks | `AiPersonality` files through the VFS, plus `bot.personality(slot, {...})` | S | G | 3 |
| Fully scripted bot | `bot.control(slot, fn)` returning a `PlayerControls` (`PlayerInput.cs:2797`) | L | G | later |

### 4.6 Graphics

How a frame is drawn. MphRead runs headless and collects draw lists (opaque, decal and translucent; `Renderer.cs:87-101`)
plus host textures (:105-125) and HUD quads (`RecordHud` :222-247). The live GLES3 renderer is
`App/Campaign/CampaignRenderer.cs`. `Render()` (:1489) clears, begins the view warp, sets the fog uniforms
(:1516-1523), runs the layered passes, tints underwater, ends the warp (:1591) and draws the HUD (:1593). There's one
scene shader, `Rendering/SceneShaderSource.cs`, kept DS-exact. The HD TEV interpreter lives only in the old Android
`RenderActivity` and isn't in the live path. There's no skybox: rooms are geometry plus the clear colour.

| Want | Where | API | Size | Online | Phase |
|---|---|---|---|---|---|
| Post-processing (bloom, CRT, colour grading, sharpening) | The view warp FBO (`CampaignView.cs:91` BeginViewWarp, :144 AllocWarpTarget, :185 EndViewWarp; non-WIDE only today) | `gfx.post(name, glsl, uniforms)`: a chain of full-screen passes. The shader source is checked at load; GL runs only on the GL thread. | M | clamped (no pass that brightens dark areas past the game's brightness range) | 4 |
| Material and shader tweaks | `CampaignRenderer.cs:675` program link | An opt-in `modFragment` hook in a second program; the DS-exact shader stays the default | M | clamped | 4 |
| HD texture packs | `Renderer.cs:1327` BindTexture, :1353 SetHostTexture, PredecodeTextures :1235-1251 (key: model, texture, palette, recolor); GPU upload `CampaignRenderer.cs:2439` / :1684 | Folder `textures/<model>/<tex>_<pal>.png` in the mod. Keep the original width and height for the UV matrix (see the note at `Collada.cs:322`) and the opaque flag. | M | clamped (no transparency on walls or players) | 3 |
| Texture filtering | Samplers `CampaignRenderer.cs:693-704` (all NEAREST) | `gfx.filter("linear")` | S | ok | 4 |
| Fog and clear colour | `Renderer.cs:250-260` getters; a new `HostFogOverride` | `gfx.fog{color, near, far}`, `gfx.clear(color)` | S | off (fog hides players) | 4 |
| Lighting | `CampaignRenderer.cs:1771` DrawItem, lights :1784-1807 | `gfx.light(...)` app-side override (never the light entities) | S-M | clamped | 4 |
| Skybox | none | A cubemap pass before the room | M | ok | 4 |
| Particles and effects | `Renderer.SpawnEffect` (`Renderer.cs:2396-2425`) uses the game RNG and the shared pool, so mods can't call it | A separate app-side particle system: `fx.emit(def, pos)` | L | ok | 4 |
| Beam looks | `BeamProjectileEntity.cs:1314-1416`; app Fx partials (BlasterFx, HyperBeamFx, ParalyzerFx, TurretFx) | `fx.beam(weapon, def)` | M | clamped | 4 |
| Render scale, framing, projection | `CampaignView.cs:40` ConfigureView, `Core/Framing/ViewFraming.cs`, `Renderer.cs:1607-1618` | `gfx.framing(...)` within the WIDE/DS/PRIME/PANINI set | S | clamped (FOV slider range) | 4 |

The thread bridge: Lua runs on the sim step and the renderer on the GL thread. Every `gfx`, `fx` and `hud` call
writes into a presentation state that is double-buffered and swapped once a frame. Lua never touches GL.

### 4.7 HUD

| Want | Where | API | Size | Online | Phase |
|---|---|---|---|---|---|
| Draw text and shapes | `UiGlDrawer` after `DrawHud` (not `QueueHudMessage`, which is sim state) | `hud.text(x, y, str, style)`, `hud.rect`, `hud.image(asset)` each frame | S-M | ok | 4 |
| Draw in the game's own HUD style | `Renderer.cs:204` `HostHudQuad` (as `FirstHuntHud.cs:218-233` uses it) | A `HostHudExtra` callback for mod quads | S | ok | 4 |
| Hide parts of the HUD | `PlayerHud.cs:21-22` HostHideReticle/HostHideScore, `PlayerServedHost.cs:18` HostHudShown, `PlayerPrimeHud.cs:28-32` | `hud.hide("reticle")` etc. | S | ok | 4 |
| Replace the whole HUD | Prime HUD painters (`PlayerPrimeHud.cs:67-81`), DrawHudLayer :3913, DrawHudObject :3986 | `hud.painter(fn)` with read-only player state (health, ammo, beam, radar dots the HUD already shows) | M | clamped (only what the vanilla HUD shows: no extra radar, no enemy positions) | 4 |
| Read HUD state | Player health, ammo, weapon, score | `player.get(slot)` read-only | S | ok (own slot only online) | 4 |

### 4.8 Camera

| Want | Where | API | Size | Online | Phase |
|---|---|---|---|---|---|
| FOV | `Renderer.cs:1618` HostFovScale (not `CameraInfo.Fov`) | `cam.fov_scale(x)` | S | clamped to the FOV slider | 4 |
| Third person | `SceneHostThirdPerson` HostCameraOffset; `CampaignRenderer.cs:565/575` | `cam.third_person(offset)` | S | off | 4 |
| Free or scripted camera (photo mode, cinematics) | `SceneHostReplay` HostView, HostBeforeCamera, HostRefreshView (render-only). Not `SetHostCamera`. | `cam.view(pos, target, fov)` | S | off | 4 |
| Screen overlays and fades | App-side (not `HostFade`, which the sim drives) | `screen.fade(color, alpha)` | S | ok | 4 |
| Shake, sway | App-side offset on the view matrix | `cam.shake(amount, frames)` | S | ok | 4 |

### 4.9 Audio and music

| Want | Where | API | Size | Online | Phase |
|---|---|---|---|---|---|
| Play a sound | `App/Audio/GameAudio.cs`: PlayUi 1050, PlaySample 1171, PlayPcm 1197, PlayPcmLoop 1232, StopPcm 1260, PlayScript 1334; `MphRead/Sound/Sfx.cs:179` | `sound.play(sfxId or asset, vol, pos)` plus a WAV/OGG decode (RIFF parsing is in `Formats/Sound.cs` and `DesktopAudio.cs`) | S | ok | 4 |
| Replace a sound | `GameSfxPlayer.LoadTables` :965 (SDAT); `Sound.cs:239-419` raw reads | VFS override in the loader (not in phase 3 slice 1: the tables load once at start-up) | M | ok | 3 |
| Room or match music | `MphRead/Sound/Music.cs:56-64` IRoomMusicHost; `Core/Music/MusicLibrary.cs:78-140`; `App/Campaign/CampaignAudio.cs:25-50` chain; `StreamMusicPlayer.cs:64` (dsp, rsf, rs03, fsb3 only) | `music.room(roomId, asset)` as a host in the chain, plus an OGG `IMusicStream` (`Core/Music/MusicStream.cs`) | M | ok | 3 |
| A song in the per-player picker | `TrackCatalog.cs`, `MultiplayerMenu.MatchMusic.cs` | Mod tracks registered as catalog entries (`tracks:<mod>/<name>`) | S | ok | 3 |
| Menu music | `App/Frontend/FrontendRenderer.cs:316-399` | A hook in the front-end host | S | ok | 4 |
| Other games' music | The Prime music import (`docs/PRIME_MUSIC.md`) | Section 5 recipes (`music/...`) | S | ok | 3 |

### 4.10 Menus and settings

| Want | Where | API | Size | Phase |
|---|---|---|---|---|
| A whole mod page | `Core/Frontend/RecompMenus.cs:123-154` RowModel, :157-181 RowsPage, BuildRowsPage :478/:484 (rows are already data) | `menu.page(title, rowsFn)` mapped onto RowModel. Needs a front-end ModHost (cosmetic mods only). | M | 4 |
| Rows on an existing page | SettingRows :545, ModRows :1336, ModSettingRows :1349 | `menu.rows(page, fn)` | S | 4 |
| Pause actions | `RecompMenus.cs:1743` (fixed ROM buttons) | Mod actions go on the MOD SETTINGS page, not new ROM buttons | S | 4 |
| Richer settings | `ModSetting` in `ModHost.cs` | **Built:** bool, int and range types (`Min`/`Max`/`Step`), plus a `setting_changed` event | S | 1 |
| Read game settings | `mod.config` | **Built:** the game's config, and a read-only `Settings.<name>` view for cosmetic mods | S | 1 |

### 4.11 Input

| Want | Where | API | Size | Kind | Phase |
|---|---|---|---|---|---|
| Read buttons | `Core/Input/InputMap.cs:16-40` | `input.down(action)`, `input.pressed(action)`. A gameplay mod reads the recorded sim input so replays stay exact. | S-M | C/G | 1 |
| A bindable mod action | `Core/Config/ControlBinds.cs:36` Function, PadFunctions :66, KeyFunctions :102, groups :41, Allowed :335; UI in `RecompMenus.Controls.cs` | `mod.bind(id, label, desc, defaults)` adds a MODS group and an `action` event; read in `App/Campaign/PadBindings.cs` and `KeyboardMouseInput.cs`. A gameplay action is recorded like any input. | M | C/G | 2 |
| Gyro and touch values | `GyroAim.cs`, `StylusAim.cs`, `CampaignGyroAim.cs` | Read-only | S | C | 4 |

### 4.12 Saves

| Want | Where | API | Size | Phase |
|---|---|---|---|---|
| Mod data that survives a crash | `LiveMods.Poll` / `Flush` | **Built:** flushed every 5 s while dirty (atomic write) and on Detach; 1 MB cap per mod (`ModHost.MaxDataBytes`) | S | 1 |
| Per-save-slot data | `RecompSave.cs` | `mod.campaign.get/set` (4.4) | M | 2 |

### 4.13 Replays

| Want | Where | API | Size | Phase |
|---|---|---|---|---|
| Record gameplay mods | `Core/Replay/ReplayFile.cs` FormatVersion 75 | **Built:** `Mods` (id, version, sha of the mod's files, settings) and `ModSeed`. Older files read as no mods. | S | 1 |
| Refuse playback without the mod | `ReplayPlayer.ModsCheck` | **Built:** "missing mod X" / "mod X changed" before the match starts | S | 1 |
| Mods row in replay info | `RecompMenus.Replays.cs` DrawDetail | **Built:** a 4th "mods" row with the ids when the header has any | S | 1 |
| Run gameplay mods during playback | `LiveMods.StartMatch(recorded)` | **Built:** loads the recorded gameplay mods with their recorded settings and seed; cosmetic mods are skipped. Limit: Lua state isn't in seek checkpoints or keyframes, so a modded replay records none, `ReplayPrep` skips it, and a seek back restarts from the start. | M | 1 |

### 4.14 Online

The server already enforces a fingerprint (`SecondHunt.Server/HubGames.cs:67` 64-char cut, :75-81 public games must
have none, :239 fixed once players join, :305-316 mismatch error `mods`; `FindRules.cs:174-178`; `HubServed.cs:187`).
The client fields exist (`OnlineProtocol.cs:152` `GameInfo.Mods`, :491 `JoinGameReq.Mods`) but `OnlineMatch.cs:259/359/370`
never fill them.

| Want | API / work | Size | Phase |
|---|---|---|---|
| Fingerprint | A hash of the sorted gameplay `id@version@sha` | S | 6 |
| Names for the can't-join popup | A new `GameInfo.ModList` of (id, version, name), count-capped in `Clean()` | M | 6 |
| ALLOW MODS filter | `OnlineSettings.AllowMods` plus a filter step (`OnlineMenus.cs:130/940-947`, `OnlineMenus.Settings.cs:124-128/399-400/448-449`, like the extras toggles) | S | 6 |
| Can't-join icon and popup | `OnlineMenus.cs` JoinBlock :1176-1182, JoinBlockMessage :1188, Popup :1166/:1202/:1689, BanIcons :2682-2695, row draw ~:2210-2224; detail facts :2255-2270 already show "mods" | S-M | 6 |
| Host's MODS setting | A MODS row in the advanced match settings (`MultiplayerMenu` Setup :307 / ExtraRules), which makes the game friends-only. `CampaignLaunch.cs:97-100` then allows cosmetic mods always and matching gameplay mods in friend games. | M | 6 |
| Mode state for clients | Mode scores and state replicated through a generic `NetObjective` blob (`Multiplayer/MatchNet.cs`); clients skip the host-only hooks | L | 6 |
| Cosmetic mods online | Allowed in any game from phase 4, through the Online column above | S | 4 |

### 4.15 Runtime and tooling (what a modder hits first)

| Gap | Fix | Size | Phase |
|---|---|---|---|
| Errors are invisible | **Built:** an on-screen `MOD <id>: ...` toast, ERROR / "can't run" on the MODS page, a bad `mod.json` listed with its problem | S | 1 |
| No hot reload | **Built:** PC (`RecompSettings.DesktopHost` or `LiveMods.HotReload`): a watcher on `mods/`; reloads at the next match, or in `Poll` outside one | S | 1 |
| One file per mod | **Built:** a sandboxed `require` that reads only the mod's own folder, once per module, with loop detection | S | 1 |
| No editor help | **Built:** `mods/mod_api.lua` from `ModApiMeta` (`-modmeta` prints it) | S | 1 |
| Saved load order ignored | **Built:** `EnabledInOrder` plus a dependency sort (`ModManager.Resolve`) | S | 1 |
| No `Kind`, `Requires`, `Api`, `Author`, `Games` | **Built** except `Games` and `GameVersion` (section 6.1) | S | 1 |
| Menus and First Hunt have no host | A front-end ModHost (cosmetic only) and a First Hunt attach | M | 4 |
| Hard to add mods on Android | `.shmod` import from a file picker, and the browser (section 7) | M | 5 |
| C# plugins (MODDING tier 4) | Not planned. Lua plus the hooks above covers the list; compiled code can't be sandboxed or checked for IP. | - | - |

## 5. Content from other games without shipping it (the import system)

The model is gen1recomp's: the mod carries **instructions**, and the player's own copy of the game supplies the
**content**. We already have most of it in IMPORT GAMES.

### 5.1 What exists

- **IMPORT GAMES** (`Core/Import/ImportService.cs:159-178`): `mp1` (GameCube), `mp2` Echoes, `mp3` Trilogy (Wii), `mp4`
  Beyond (Switch), `ssbb` Brawl. First Hunt is separate (`FirstHuntGame.Import`). The kinds are Gun, Hd, Body,
  EchoesArena, Music, BrawlFx and Track (:197-203).
- **Recipes** (`Core/Import/Recipes.cs:14-102`, `recipes/<kind>/<Name>.json`): about 214 today (arenas 6, bodies 12,
  brawl 2, guns 17, hd 43, music 1, standin 2, tracks 131). A recipe holds only ids, parameters and the source file's
  SHA-1, never game data. `Id` = `kind/Name`; its own `Sha1` is the hash of the recipe text. Recipes are embedded,
  and `RecipeStore.FromDirectory` (:67) can already load them from a folder.
- **Output**: `<data>/<kind>/<Name>/`, with state in `<data>/import/<game>.json` (`{items: {"kind/Name": {status,
  recipeSha1, importer}}}`).
- **The gate**: `ImportedContent.Usable(root, kind, name)` (`ImportedContent.cs:26-46`) = complete, recipe hash
  matches, and importer version matches. Public builds use nothing else.
- **Kept sources** (`SourceKeep.cs:11-25`): `<data>/source/<game>/` keeps the files the recipes read, so importers
  can re-run without the disc. A new recipe that needs a file that wasn't kept says "needs the disc again".

### 5.2 What a mod asks for

`mod.json` gets three fields (section 6.1):

```json
"Games": ["mph"],
"Imports": ["mp3:bodies/SpacePirate", "mp2:tracks/SanctuaryFortress"],
"Recipes": "recipes/"
```

- `Imports` names recipes that already ship in the game. The mod can only be turned on when each one is `Usable`.
- `Recipes` points at the mod's own recipe folder. A mod recipe works like a built-in one: it names a game, a
  source file by path and SHA-1, and the importer kind with its parameters. Example: "body from Prime 3's
  `Gandrayda.CMDL` with SHA-1 ..., rig `auto`, clips idle/run/jump from these ANIM ids". The source hash pins it to a
  real disc, so it can't smuggle anything in.
- The MODS page and the browser show what's missing: "Needs Metroid Prime 3: IMPORT GAMES", or "Needs your Echoes
  disc again" when a kept source lacks the file.

### 5.3 Games we don't import (the player's own files)

For a game outside IMPORT GAMES, a mod lists `UserFiles`:

```json
"UserFiles": [{ "Id": "suit", "Name": "your extracted suit model (suit.glb)", "Sha1": "...", "Size": 1234567 }]
```

The player picks the file; the app checks size and SHA-1, then copies it to `mods/<id>/user/`. The mod reads it
only through its transforms (5.4), with a size cap. This is gen1recomp's `required_imports`. Nothing ships, and the
hash means only the exact file works.

### 5.4 Transforms and the derived cache

Some content needs work after import: retarget a body onto a hunter skeleton, recolour a texture, cut a clip,
build a palette. A mod can ship `transforms.lua`, which runs in the same sandbox at a high instruction budget, only
at install time or when an input changes. It sees:

- `imports.read(id, offset, len)` and `imports.list(id)` (its declared imports and user files only);
- built-in readers and writers in C#: `retro.model/anim/texture` (CMDL, ANIM, TXTR via the existing importers),
  `gltf.read/write`, `image.read/write/recolor/blit`, `body.build{mesh, skeleton, clips}`, `retarget(mesh, from, to)`.

Output goes to `<data>/mods-derived/<id>/`, stamped with the input hashes plus the transform's hash, so it re-runs
only when something changes. `ImportedContent.Usable` accepts this folder for that mod's content. A failed
transform turns off only that mod's derived content and says why.

### 5.5 Rules

- No patches (BPS/IPS/xdelta) and no ROM or disc files in a mod; the packer refuses them (6.3).
- Derived content stays on the player's device. It never uploads, and replays and online games send only ids.
- An online peer without the import sees the base hunter's look (`LookAlikes`), so imports never block a game.

## 6. Mod packages

### 6.1 Manifest v2 (`mod.json`)

Existing fields stay (`Id`, `Name`, `Version`, `Description`, `Entry`, `Settings`). New ones (phase 1 built `Kind`,
`Api`, `Author`, `Requires`, `Conflicts` and the setting types; the rest come with phases 3 and 5):

| Field | Meaning |
|---|---|
| `Kind` | `cosmetic` or `gameplay` (section 3) |
| `Api` | The mod API version it was written for. A newer game refuses an older major version and says so. |
| `GameVersion` | Optional range, e.g. `">=0.5.0"` |
| `Author` | Display name; in the index, the GitHub owner |
| `Games` | `mph` (always), and `fh` for First Hunt mods |
| `Requires` / `Conflicts` | Other mods as `id` with a version range |
| `Imports`, `Recipes`, `UserFiles` | Section 5 |
| `Settings[].Type` | `list`, `bool`, `int`, `range` (with `Min`, `Max`, `Step`) |
| `Links` | Homepage, source, issues |

### 6.3 The packer (`modkit`)

A Tools command (`-modkit validate|pack|submit`), later a small standalone tool. It refuses:

- ROM and disc extensions (`.nds`, `.iso`, `.gcm`, `.wbfs`, `.rvz`, `.xci`, `.nsp`, `.sdat`, `.bin` outside the
  allowed folders), and patch files;
- any file whose hash matches a file in the author's own import cache (`<data>/<kind>/...` and `source/`). This is
  gen1recomp's cache-signature check, and it catches copied imports;
- precompiled Lua, bad paths, and a manifest that fails the schema.

`pack` writes `<id>-<version>.shmod` and its SHA-256. `submit` writes the index entry (section 8).

## 7. The in-game mod browser

A MODS page with three tabs, built like REPLAYS > SHARED WITH ME (`RecompMenus.ReplayShare.cs:14-27/172/597`; tabs
by touch or L1/R1, the list fetched when the tab opens): **INSTALLED**, **BROWSE** and **UPDATES**.

- **BROWSE** shows the catalog: thumbnail, name, author, version, kind (COSMETIC / GAMEPLAY), an **ONLINE OK** badge
  when the owner marked it safe (section 8.3), category, and what it needs ("Needs Metroid Prime 3", "Needs mod X").
  Filters: category, kind, online OK, "works with what I've imported".
- **Install** downloads the `.shmod` from the author's release URL in the catalog, checks size and SHA-256 against the
  signed catalog, unpacks it, then runs any imports and transforms with IMPORT GAMES' status pills and progress
  (`Frontend/ImportGames.cs`). Missing games link to IMPORT GAMES. Missing mods it requires install with it.
- **UPDATES** lists installed mods with a newer catalog version, with UPDATE ALL and the red badge pattern
  (`ImportGames.cs:1153/1285`). Old versions stay installable when a replay needs them.
- **INSTALLED** keeps today's on/off and order, adds the error row (4.15), IMPORT .SHMOD (file picker) and REMOVE.
- No installs or updates during a match or a campaign run (the updater's `Blocker` hook).
- REPORT on a mod card uses the existing report alerts (`HubAlerts.cs`).

The download code is the updater's: `UpdateDownload` (`Core/Update/UpdateInstall.cs:32-125`) downloads to `.part`
while hashing, refuses anything over the signed size, times out on a stall and moves into place atomically.
`UpdateCheck.Get` (`UpdateCheck.cs:141-180`) has the byte cap and accepts `file://` feeds for tests.

## 8. The mod index repo and submissions

### 8.1 The repo

A public GitHub repo under the owner's account (proposed name `BountyHunterKanden/SecondHunt-Mods`; creating it needs
the owner's GO). It holds **metadata only**, never mod files:

```
mods/<author>@<id>/meta.json        id, name, author, kind, version, category, api, requires, imports,
                                    download (release URL), sha256, size, links, online_ok (owner only)
mods/<author>@<id>/description.md
mods/<author>@<id>/thumb.png        <= 1 MB
blocklist.json
schema/meta.schema.json
```

Mod files live on each author's own GitHub Releases as `<id>-<version>.shmod`. That keeps hosting free and our server
small (its budget is about $10 a month).

### 8.2 Submitting (owner decision §11.7: a website portal for approval, like gen1recomp)

Modders submit through a **submission website**, a static page on the index repo's GitHub Pages, so it costs nothing
to host:

1. The modder runs `modkit pack`, uploads the `.shmod` to a release on their own GitHub repo, and opens the portal.
2. They sign in with GitHub, paste the release URL (or their repo), and fill in the form: name, description,
   category, kind, thumbnail, links. The page reads `mod.json` from the package to pre-fill it, then shows what the
   checks will look at.
3. Submitting opens a pull request on the index repo for them (through the GitHub sign-in), adding
   `mods/<author>@<id>/`. Modders who'd rather skip the site can open that pull request by hand (CONTRIBUTING.md
   explains it); both lanes go through the same checks.
4. CI (GitHub Actions) checks the schema, downloads the release and checks its hash, runs `modkit validate` on it
   (the IP check, bad files), scans the Lua for anything outside the API, checks the blocklist and the links, and
   rejects extra files. The result is posted on the pull request and shown on the portal's "my submissions" view.
5. The owner approves (merges) or sends it back with a note. Approval is the listing; the portal shows the status
   (waiting, checks failed, changes asked, approved).

Updates go the same way: a new release plus a submission that changes the version, URL and hash. A SUBMIT button in
the PC app can come later and would just open the portal.

### 8.3 The signed catalog

On merge, a workflow builds `catalog.txt` from every `meta.json`, in the updater's format (`UpdateManifest.cs`: plain
text, one line per mod, `key` and `signature` lines at the end, ECDSA P-256). The app trusts it only if the signature
checks out against a **catalog key** in `UpdateKeys` (a new key, not the release key). `catalog.txt` is served from
GitHub Pages or the repo's raw URL. Our server can mirror it at `/v1/mods/catalog` later.

`online_ok` is set only by the owner, after checking the mod uses nothing marked `off` and isn't gameplay. The
browser shows it as the ONLINE OK badge.

A scheduled workflow (like quiver-feedback-watch) checks every download every 6 hours and opens an issue for dead
links or a changed hash. Two strikes drop the entry from the catalog.

## 9. Phases

Each phase is its own piece of work for the owner to schedule. The owner put phase 1 first (§11.3).

1. **Runtime basics, determinism and filters.**
   - Runtime: manifest v2 with `Kind`, load order, `require`, errors on screen, a crash-safe flush, richer settings,
     `mod.config`, LuaLS meta, PC hot reload (4.15).
   - Determinism: gameplay mods get DetMath for `math`, a mod RNG stream seeded from the replay header (never the
     game's Rng1/Rng2), and no clock.
   - Filters at the sites in 4.2-4.3 (damage, kill, respawn_time, spawn_point, pickup, player_values, enemy_damage).
     A filter call site looks like an event: `if (GameFilters.Damage) damage = GameFilters.PlayerDamage(...)`.
   - Replays: format 75 header, refusal, mods row, gameplay mods during playback (4.13).
   - **Status: built 2026-10-30.** Dropped: `enemy_spawn` (moves to phase 2, section 4.3). Limit: a modded
     replay has no seek checkpoints or keyframes (Lua state isn't snapshotted), so seeking back restarts it.
2. **Modes, entities and messages.** `mode.*`, `score.*`, `room.on_load`, runtime spawn and remove, entity hooks, the
   message bus, campaign data, bot objectives, the data-driven mode list (4.2, 4.4, 4.5).
   - **Status: in progress 2026-10-30.** Built: `enemy_spawn` filter, `score.*`, `match.*` (read +
     `finish`), `room.on_load` (pickups anywhere, enemies in campaign rooms), runtime `entity.*` (read, spawn, remove), the message bus (`mod.on_message`, `msg.send`), campaign data (`mod.campaign`), `mode.setup/tick/vanilla`,
     `bot.set_objective/objective`, `mode.register` runtime (mod.json `Modes`, `$mode` in the replay row,
     `mode.current`). `MatchSetupText` `mm=`. GAME MODE list row (end of the list) built 10-30.
3. **Content: assets, data tables and imports.** Route the raw reads through `Read.ReadBytes` and mount the VFS;
   texture packs, sound and music overrides; `weapons.patch`; `character.register` and skins; mod recipes, user files,
   transforms and the derived cache (4.1, 4.3, 4.9, section 5).
   - **Status: slice 1 built 2026-10-30.** A mod's `files/` folder replaces game files read through
     `Read.ReadBytes` / `Read.ReadFileBytes` (models, rooms, entities, collision, text via `Strings.cs`, HUD pictures via
     `HudInfo.cs`): `Core/Mods/ModContent.cs` behind the `Read.HostReadFile` hook. Cosmetic mods only `stringTables*` and
     `hud/` (anything the sim reads, like models with named nodes, needs a gameplay mod); gameplay mods mount per match
     and their files are in the replay sha. `mod.asset` works live. Limits: files load as a room loads (a change shows
     from the next room or match); sound tables (`Sound.cs:239-419`) and other start-up loads don't follow yet; the
     app's GPU caches may keep a replaced model's old textures until restart. Next: sounds and music, texture packs,
     `weapons.patch`, `character.register`, recipes.
4. **Presentation.** `gfx`, `fx`, `hud`, `cam`, `sound`, `menu`, the front-end and First Hunt hosts, and the Online
   column's clamps (4.6-4.10). Cosmetic mods become allowed in online games here.
5. **Distribution.** `.shmod`, `modkit`, the browser, the index repo, the submission portal, CI and the signed catalog (sections 6-8). The
   browser and packer can start alongside phase 3, since they don't depend on the sim.
6. **Gameplay mods online with friends.** Fingerprint, mod list, ALLOW MODS filter, can't-join icon and popup, host
   MODS setting, mode state replication (4.14). Strangers never run gameplay mods.

## 10. Testing

- `-modtest`: the runtime and the live path, 32 checks. It covers sandboxing, the runaway guard, persistence,
  `LiveMods` attaching from a folder, settings, engine args reaching Lua, detach, saving across games and a disabled
  mod not running.
- `-exampletest`: the shipped example mods end to end, including the PARTY MODES sample (14).
- `-modcontenttest` (16): `files/` overrides on a fake game tree: cosmetic allowlist, gameplay per match, case,
  priority, `mod.asset`, online, a disabled mod, the replay sha.
- `-modapitest` (82): the `mod` table plus the phase 1 checks: manifest v2, `require`, typed settings and
  `setting_changed`, DetMath and the mod RNG, a cosmetic mod refused for `mod.filter`, the 1 MB cap, and the replay
  round trip below.
- `-modmgrtest` (12), `-vfstest` (12): discovery, order and the dependency sort; the VFS.
- `-modmeta`: prints `mod_api.lua`.
- `-replaygolden mods`: plays all 10 goldens with a listener on every event and prints what each one heard. Every
  golden must still be exact.
- To add with each phase:
  - Phase 1 (**done**, in `-modapitest`, `ReplayTest.Mods.cs`): a mod doubles damage in a bot match, the replay
    header names it, format 75 round-trips, playback without it is refused ("missing mod double"), playback with it
    is exact, and a changed mod is refused.
  - Phase 2: a test mode (e.g. "first to 3 headshots") played by bots headless to the end.
  - Phase 3: `-modcontenttest` (slice 1, done); a recipe test against a fixture source file, and a `modkit` test that refuses a copied import.
  - Phase 4: every `off` and `clamped` API checked in an online-flagged session.
  - Phase 5: catalog signature tests in the ReleaseTool selftest style, and a `file://` feed for the browser.

## 11. Owner decisions (2026-10-30)

1. **Thread:** Mods is its own line of work. The inventory and hooks came first.
2. **Kind:** yes, split mods into cosmetic and gameplay. Gameplay mods are allowed in replays (recorded in the header,
   needed for playback). Online, the split must make sure the mods allowed there give no advantage and can't be used
   to cheat. Allowed mods are a match filter and an advanced match setting. You can't join a game without the right
   mods, and a listed game you can't join shows an icon; clicking it says which mods you need.
3. **Order:** phase 1 (determinism + value filters) goes first, then the rest in the section 9 order.
4. **Online mod games** (decision 12, phase 6): later, after the offline phases are in and tested.
5. **Replays:** yes, the replay info gets a mods row (ids and versions, and which one is missing if you lack it).
6. **Reach** (later on 10-30): cover as much as possible, including other games' characters, new and changed modes,
   and graphics. Other games' content comes through an import system like gen1recomp's, with no IP in mods. Add an
   in-game mod browser, a mod repo hooked up to it, and a way to submit mods, in our style.

7. **Answered later on 10-30:**
   - Index repo: `BountyHunterKanden/SecondHunt-Mods`, created later (when phase 5 starts; still needs the GO then).
   - Catalog signing: automatic on GitHub, with the catalog key as a repo secret, signed on every merge.
   - A character from another game that keeps the base hunter's abilities is **cosmetic**.
   - Submissions: a website portal for approval like gen1recomp's (section 8.2), with hand-made pull requests as the
     fallback lane.
