# mph-recomp Modding Guide

mph-recomp is moddable in four layers. You can go as light or as deep as you like:

| Tier | What it is | How |
|---|---|---|
| 1. **Assets** | Replace or add any game asset (texture, model, data file) | Put a file at the same path under your mod's `files/` folder (e.g. `files/stringTables/ScanLog.bin`) and the game reads it instead of its own (`MphRecomp.Mods.ModContent`). A cosmetic mod may only replace text (`stringTables*`) and HUD pictures (`hud/`); a gameplay mod may replace any game file. `main.lua` can be empty for a files-only mod |
| 2. **Data / config** | Retune game constants (camera, movement, lighting…) | Ship a partial `config.json`; it deep-merges over the defaults (`MphRecomp.Config`) |
| 3. **Scripts (Lua)** | React to game events, add behavior | A sandboxed Lua mod using the `mod` API below (`MphRecomp.Mods`) |
| 4. **Plugins (C#)** | Compiled extensions | (planned) |

**Status (2026-10-30, mod API phase 1):** tier 3 runs in real games. Cosmetic mods listen to events; gameplay mods
can also change a match through filters (damage, kills, respawns, spawn points, pickups, hunter values, enemy damage, enemy health at spawn) and read or change scores and the match (`score`, `match`).
`mod.config` reads the game's real tuning values. Tier 1 (phase 3, first slice, 10-30): files under a mod's `files/`
folder replace the game's own as rooms load (models, rooms, collision, text, HUD pictures); sounds and other files the
app loads once at start-up don't follow yet, and nothing is replaced in online games. `mod.asset(path)` returns a game
file's text (the mod copy if one is mounted) or the mod's own file. [MOD_API.md](MOD_API.md) section 4 is the full map of what is
built and what is planned.

Mods are **sandboxed** (no file/OS access from Lua), **isolated** from each other, and a mod that throws (on load or in a hook) is caught and disabled without taking down the game. Each mod gets its own persistent storage inside your save, so mods never clobber each other or the base save.

## A mod is a folder

```
my_mod/
  mod.json      # manifest
  main.lua      # entry script (name it whatever Entry says)
  ...            # any assets your mod overrides/adds
```

`mod.json`:

```json
{
  "Id": "my_mod",
  "Name": "My Mod",
  "Version": "1.0.0",
  "Description": "What it does.",
  "Author": "you",
  "Kind": "cosmetic",
  "Api": 1,
  "Requires": ["other_mod>=1.2.0"],
  "Conflicts": ["rival_mod"],
  "Entry": "main.lua",
  "Settings": [
    { "Id": "loud", "Name": "Loud", "Type": "bool", "Default": "true" },
    { "Id": "lives", "Name": "Lives", "Type": "int", "Min": 1, "Max": 9, "Default": "3" },
    { "Id": "scale", "Name": "Scale", "Type": "range", "Min": 0.5, "Max": 2, "Step": 0.25, "Default": "1" },
    { "Id": "mode", "Name": "Mode", "Values": ["a", "b"], "Default": "a" }
  ],
  "Modes": [
    { "Id": "tag", "Name": "TAG", "Description": "Hold the tag to score.", "Base": "Battle",
      "PointGoal": 5, "TimeLimit": 3 }
  ]
}
```

- **Kind**: `cosmetic` (the default) watches the game, logs and keeps data. `gameplay` may also change the match
  (`mod.filter`, `mod.spawn`). A cosmetic mod that calls a gameplay function is stopped with an error.
- **Api**: the mod API version the mod was written for (today 1). A mod asking for a newer one won't load.
- **Requires / Conflicts**: other mod ids, optionally with a version (`id>=1.2.0`). A mod loads after the mods it
  requires; it is left off (with the reason on the MODS page) when one is missing or a conflicting mod is on.
- **Settings**: `bool`, `int` and `range` (with `Min`/`Max`/`Step`), or a list of `Values`. They appear under the mod
  on the MODS page, and `mod.setting(id)` returns a boolean, a number or a string to match. Ids starting with `$`
  are reserved.
- **Modes** (gameplay mods): new match modes. Each plays as its `Base` mode (Battle, Bounty, Capture, Defender,
  Nodes, PrimeHunter or Survival) with your `mode.setup`/`mode.tick` rules on top; `PointGoal`, `TimeLimit` (minutes)
  and `TimeGoal` (seconds) are its starting rules. A mod that declares modes runs only in matches playing one of them.
  Offline only; the choice is saved in replays. Your modes show in a row at the end of the GAME MODE list
  (step with the arrows, press A or tap the name) while the mod is turned on in MODS.

Drop the folder in the game's `mods/` directory. The in-app mod manager discovers it; you can enable/disable and reorder mods, and that choice is saved.

## The `mod` API (Lua)

Every mod runs with a `mod` global scoped to *it*:

| Call | Description |
|---|---|
| `mod.id`, `mod.name`, `mod.version` | Your mod's manifest fields |
| `mod.log(message)` | Write to the game log |
| `mod.on(event, function(...) end)` | Register a handler for a game event |
| `mod.save(key, value)` | Persist a value (numbers, strings, booleans, tables) in your mod's own save slot |
| `mod.load(key, default)` | Read a previously-saved value, or `default` |
| `mod.campaign.get(key, default)` | Read a value kept in the current campaign save file, or `default` (also outside a campaign) |
| `mod.campaign.set(key, value)` | Gameplay mods: keep a value in the current campaign save file (up to 64 KB per mod). It is written when the game saves at the ship, so loading an older save brings back that save's values. Returns false when no campaign file is loaded |
| `mod.config('Section.Key', default)` | Read a tuning value by dotted path, e.g. `mod.config('Camera.FovDegrees', 60)` |
| `mod.setting(id)` | The player's pick for one of your `Settings` (boolean, number or string) |
| `mod.random(m, n)` | A random number from your mod's own stream (same forms as `math.random`) |
| `mod.filter(name, function(t) end)` | Gameplay mods: change a value the game is about to use (see Filters) |
| `mod.asset('logical/path')` | Read an asset's text through the VFS (override-aware); `nil` if missing. Live games: `nil` for now |
| `mod.spawn('model', x, y, z)` | Gameplay mods: spawn a model at a position; returns its id. Live games: `nil` for now |
| `score.get(slot)`, `score.kills(slot)`, `score.deaths(slot)`, `score.team_get(team)` | Read the match scores (any mod; 0 outside a match) |
| `score.set/add(slot, n)`, `score.team_set/team_add(team, n)` | Gameplay mods: change the scores |
| `match.active()`, `match.mode()`, `match.teams()`, `match.time_left()` | The match being played (any mod) |
| `match.setting(name)` | A match rule by name, e.g. `match.setting('PointGoal')`; `nil` if unknown |
| `match.player(slot)` | `{hunter, team, bot}` for a seat, or `nil` |
| `match.finish()` | Gameplay mods: end the match now (results rank by the current scores) |
| `match.point_goal()`, `match.time_goal()` | The match's point goal and time goal in seconds (0 = none) |
| `match.set_point_goal(n)`, `match.set_time_goal(s)`, `match.set_time_left(s)` | Gameplay mods: change the goals or the time left (0 ends the match) |
| `mode.setup(fn)` | Gameplay mods: `fn()` runs once as a match starts, after its own rules are set: the place to change goals and time |
| `mode.tick(fn)` | Gameplay mods: `fn(dt)` runs every frame of a match in place of the mode's own rules (the point/time goal win checks, Survival lives, the Prime Hunter). The time limit still ends the match. If every such mod stops on an error the mode's rules come back |
| `mode.vanilla()` | Inside `mode.tick`: run the mode's own rules for this frame (at most once a frame), e.g. keep them and add a rule of your own |
| `mode.current()` | Any mod: the mod mode this match plays as `mode_id, mod_id`, or nil |
| `bot.set_objective(slot, x, y, z)` | Gameplay mods: the bot in `slot` heads for this spot whenever it would otherwise wander (fights and pickups still come first). `bot.set_objective(slot)` clears it. Cleared at each match start |
| `bot.objective(slot)` | Any mod: the bot's objective as `x, y, z`, or nil |
| `room.on_load(function(r) ... end)` | Gameplay mods: runs as each room loads. `r.id`, `r.name`; `r.add_pickup('MissileBig', x, y, z, respawn_seconds?)` adds a pickup; `r.add_enemy('mph:Zoomer', x, y, z, facing?)` adds one enemy (campaign rooms only for now). Up to 64 per room |
| `entity.enemies()`, `entity.get(h)` | Any mod: the enemies alive now as handles; `get` gives `type`, `x`, `y`, `z`, `health`, `max_health` (nil once it's gone) |
| `entity.spawn_enemy('mph:Zoomer', x, y, z, facing?)`, `entity.remove(h)` | Gameplay mods: spawn an enemy now (campaign rooms only for now) or remove one (not bosses or hunters). Happens at the end of the frame; up to 16 a frame |
| `mod.on_message('Unlock' or entity id, fn)` | Any mod: watch the game's level-scripting messages (doors, triggers, platforms...). `fn(name, from, to, p1, p2)` |
| `msg.send('Unlock', from, to, p1, p2, delay)` | Gameplay mods: send one (SetActive, Trigger, Unlock, Lock, Activate, Complete, UnlockConnectors, LockConnectors, SetTriggerState, ClearTriggerState, PlatformWakeup, PlatformSleep). `from` may be nil; the trigger-state ones take the bit (0-31) as p1 and need no target. End of the frame, up to 16 a frame, delay up to 600 frames |
| `require('lib.util')` | Load `lib/util.lua` from your own mod folder (once; nothing outside the folder) |

### Events

The game fires these to your `mod.on` handlers:

| Event | Args | When |
|---|---|---|
| `start` | none | Once, after mods load |
| `setting_changed` | `modId, settingId, value` | The player changed one of your settings on the MODS page |
| `room_enter` | `roomName, roomId` | You enter a room |
| `tick` | `dt, frame` | Every sim frame (60 a second, dt = 1/60) |
| `damage`, `death`, `fire`, `pickup`, `scan`, … | see the list | Players, enemies, doors, artifacts, octoliths |

The full list with every event's arguments is in [MOD_API.md](MOD_API.md) section 2. Mods run in offline games only
(not online or LAN games). `mod.log` lines also go to `mods/mod_log.txt`.

### Filters (gameplay mods)

`mod.filter(name, fn)` calls `fn(t)` with a table of fields; change the marked ones to change what the game does.

| Filter | Fields (the ones you may change are **bold**) |
|---|---|
| `damage` | `target`, `attacker`, **`damage`**, `beam`, `headshot` (a hit on a player) |
| `kill` | `victim`, `attacker`, `suicide`, `beam`, `headshot`, `vanilla_points`, **`points`** (added on top), **`scorer`** |
| `respawn_time` | `player`, `attacker`, **`frames`** (60 a second; vanilla 180) |
| `spawn_point` | `player`, **`index`**, `count` |
| `pickup` | `player`, `item`, **`allow`** (false leaves the item where it is) |
| `player_values` | `player`, `hunter`, and **every hunter value by name**, e.g. `JumpSpeed` (fixed point, 4096 = 1.0) |
| `enemy_damage` | `enemy`, `attacker`, **`damage`** (a hit on an enemy) |
| `enemy_spawn` | `enemy`, `room`, **`health`** (as the enemy spawns; 1 to 65535, max health follows) |

```lua
mod.filter("damage", function(t)
  if t.headshot then t.damage = t.damage * 2 end
end)
```

A filter nobody hooks costs the game nothing. Gameplay mods get a `math` library that gives the same answers on every
device, `mod.random` for random numbers, and no clock (`os.time`, `os.clock`), so a replay of a modded match plays
back the same.

### Editor support

The game writes `mods/mod_api.lua`, a Lua language server (`---@meta`) file. Open your mod in an editor with the Lua
extension and it autocompletes `mod.*`, the event names and each filter's fields.

### Errors

A mod that fails to load or throws in a hook is turned off for the rest of the game. The error shows as a short
`MOD <id>: ...` line on screen, the mod's row on the MODS page shows ERROR with the reason, and the line goes to
`mods/mod_log.txt`. A mod that can't run at all (a missing requirement, a conflict, a newer API) says why on its row.

### Replays

A replay of a match a gameplay mod played in records the mod (id, version and a hash of its files), its settings and
its random seed. Playing it back loads the same mods again; if one is missing or has changed, the replay won't start
and says which (`missing mod X`). The replay's info shows a `mods` row. Cosmetic mods are never recorded and don't run
during playback. Seeking back in a modded replay restarts it from the beginning (mod state isn't in the seek
snapshots), so seeking is slower than in an unmodded one.

### Hot reload (PC)

On PC the game watches the `mods/` folder: save a file and the mods reload before the next match (or straight away in
a campaign), so you can edit and retry without restarting the game.

## Example

A complete, working example ships in [`docs/mods/example_stat_tracker/`](mods/example_stat_tracker/): a small stat tracker that counts launches, room visits, and playtime and reads config + an asset. Copy that folder to start your own.

For game modes, copy [`docs/mods/example_party_modes/`](mods/example_party_modes/): three Battle-based modes
(INSTAGIB, HEADHUNTER, MOON BOUNCE), each one a `mod.filter` that checks `mode.current()`, plus two mod settings.
Turn it on in MODS and the modes show at the end of the GAME MODE list.

```lua
mod.on("room_enter", function(room)
  local visits = mod.load("visits", {})
  visits[room] = (visits[room] or 0) + 1
  mod.save("visits", visits)
  mod.log("entered " .. room .. " (visit #" .. visits[room] .. ")")
end)
```

## Notes

- **Persistence** is per-mod and per-key, stored in the versioned save under your mod id, so uninstalling a mod never corrupts the base save (mirrors the `save.modData[modId]` pattern from gen1recomp mods).
- **Load order** matters if two mods override the same asset or react to the same event: later-loaded wins for assets; all handlers run for events, and filters run in load order (each sees the previous one's changes). Reorder on the MODS page; a mod always loads after the mods it `Requires`.
- **Data**: `mod.save` keeps up to 1 MB per mod in `mods/mod_data.json`, written every few seconds and when a game ends, so a crash loses at most a few seconds.
- The whole mod stack is covered by headless tests (`-modtest`, `-exampletest`, `-modapitest`, `-modmgrtest`, `-vfstest` in the tools CLI; `-modmeta` prints `mod_api.lua`).
