# mph-recomp Modding Guide

mph-recomp is moddable in four layers. You can go as light or as deep as you like:

| Tier | What it is | How |
|---|---|---|
| 1. **Assets** | Replace or add any game asset (texture, model, data file) | Drop a file at the same path in your mod folder — it overrides the base game (`MphRecomp.Assets.AssetVfs`) |
| 2. **Data / config** | Retune game constants (camera, movement, lighting…) | Ship a partial `config.json`; it deep-merges over the defaults (`MphRecomp.Config`) |
| 3. **Scripts (Lua)** | React to game events, add behavior | A sandboxed Lua mod using the `mod` API below (`MphRecomp.Mods`) |
| 4. **Plugins (C#)** | Compiled extensions | (planned) |

Mods are **sandboxed** (no file/OS access from Lua), **isolated** from each other, and a mod that throws — on load or in a hook — is caught and disabled without taking down the game. Each mod gets its own persistent storage inside your save, so mods never clobber each other or the base save.

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
  "Entry": "main.lua"
}
```

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
| `mod.config('Section.Key', default)` | Read a tuning value by dotted path, e.g. `mod.config('Camera.FovDegrees', 60)` |
| `mod.asset('logical/path')` | Read an asset's text through the VFS (override-aware); `nil` if missing |
| `mod.spawn('model', x, y, z)` | Spawn a sim entity (a model at a position); returns its id |

### Events

The game fires these to your `mod.on` handlers:

| Event | Args | When |
|---|---|---|
| `start` | — | Once, after mods load |
| `room_enter` | `roomName` | You enter a room |
| `tick` | `dt` | Every fixed sim step (dt seconds) |

More events (damage, pickup, death, …) arrive as the gameplay layer lands.

## Example

A complete, working example ships in [`docs/mods/example_stat_tracker/`](mods/example_stat_tracker/) — a small stat tracker that counts launches, room visits, and playtime and reads config + an asset. Copy that folder to start your own.

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
- **Load order** matters if two mods override the same asset or react to the same event — later-loaded wins for assets; all handlers run for events. Reorder in the mod manager.
- The whole mod stack is covered by headless tests (`-modtest`, `-modapitest`, `-modmgrtest`, `-vfstest` in the tools CLI).
