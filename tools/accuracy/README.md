# Vanilla accuracy: emulator side

The real game in an emulator, driven by script, as the reference for `docs/ACCURACY.md`. PC only. Nothing here ships
in the app, and no game data goes in the repo: ROMs, savestates, RAM dumps and screenshots stay in the emulator work
dir (default `%USERPROFILE%\Downloads\mph-emu`).

## Setup (done 2026-09-30)

- BizHawk 2.11.1 (`BizHawk-2.11.1-win-x64.zip` from github.com/TASEmulators/BizHawk releases, sha256
  `dd7cbd5e...5717`) unzipped to `mph-emu\BizHawk-2.11.1`. DS core = melonDS (built in). No BIOS files needed.
- **Unbind the mouse from the DS touch screen** in `BizHawk-*\config.ini`: `AllTrollersAnalog` -> `NDS Controller` ->
  `Touch X`/`Touch Y` `"Value": ""`, and `AllTrollers` -> `NDS Controller` -> `Touch` `""`. Otherwise
  `joypad.setanalog` touch positions are silently dropped (BizHawk issue #4535: sticky axes XOR the mouse axes). The
  original is kept as `config.ini.orig`.
- ROM: `mph-emu\roms\AMHE1.nds` = USA rev 1, SHA-1 `a8679d4f8dcb445cbe946a424241ef44901b0caf` (MphRead follows 1.1).

## Running

```
tools\accuracy\run.ps1 -Route tools\accuracy\routes\to_landing.lua
python tools\accuracy\sheet.py %USERPROFILE%\Downloads\mph-emu\out\shots out.png
```

`driver.lua` plays a route file (taps, keys, savestate load/save, screenshots, Main RAM dumps); see its header.
Output goes to `mph-emu\out\`. The game's save file persists between runs in BizHawk's SaveRAM folder; routes that
start from a savestate don't depend on it.

## Routes and states (`mph-emu\states\`)

| State | Where |
|---|---|
| `title.State` / `mainmenu.State` | title screen / main menu |
| `briefing.State` | new file, mission briefing (SKIP at 234,183) |
| `starmap.State` | star map, Celestial Archives (LAND SHIP at 30,165) |
| `landing.State` | Samus on the Celestial Archives landing site, gameplay running |

Menu facts: a tap on a main-menu icon only focuses it, A activates. Touch points are DS bottom-screen pixels.

## Tests

| Emulator route | MphRead side | What |
|---|---|---|
| `routes/probe_buttons.lua` (writes `out/trace.csv`) | `MphRead.Tools -accuracy jump <trace.csv>` (build with `-o MphRead.Tools/bin/Accuracy/net9.0`) | Standing jump on the landing site, per vanilla tick |
| same `trace.csv` | `-accuracy walk <trace.csv>` | Walking off the gunship: speed per tick, distance, fall, landing |
| `routes/probe_strafe.lua` (`out/strafe.csv`) | `-accuracy strafe <strafe.csv>` | Strafe / walk camera tilt (`_field684` / `_field688`) |
| `routes/probe_morph.lua` (`out/morph.csv`) | `-accuracy morph <out.csv>` then compare | Morph ball camera follow and roll speed |
| `routes/probe_roll.lua` (`out/roll.csv`) | `-accuracy roll <roll.csv>` | Morph ball roll on flat ground: acceleration from rest, top speed, coasting (A8) |
| (none; vanilla unmeasured) | `-accuracy ramp [ball]` | Stronghold Void A corridor crease, walking or in the morph ball |
| `routes/probe_slope.lua` (`out/slope.csv`) | `-accuracy slope <slope.csv>` | Walking (backward) from rest across the flat floor and up the landing site's 19.3 deg slope (A9) |
| `routes/probe_ramp26.lua` (`out/ramp26.csv`) | `-accuracy ramp26 <ramp26.csv>` | Walking up the 26.6 deg ramp (placed there by a position poke) (A9) |
| `routes/probe_ramp45.lua` (`out/ramp45.csv`) | `-accuracy ramp45 <ramp45.csv>` | Walking up the 45 deg ramp off the y 13 ledge (position poke) (A9) |
| (MphRead only) | `-accuracy steep <ROOM>\|all [verts \| box x0 x1 z0 z1]` | Walkable steep floor faces (normal Y 0.5-0.9) per room, to find test inclines |
| `routes/probe_idle.lua` (`out/idle.csv`) | `-accuracy idle` | Standing on the bobbing gunship |
| (MphRead only) | `-accuracy ramp`, `-accuracy logbook`, `-accuracy scanlog [n]` | Stronghold Void crease, new-game logbook, ScanLog table |

MphRead side notes: tests start the landing room with `collectDrawItems: true` (the draw pass advances animations, which
platform collision follows) and settle 1000 frames (input is held until ~frame 952). Touch-screen buttons seen so far:
morph ball (220, 163), briefing SKIP (234, 183), star map LAND SHIP (30, 165).

Vanilla runs its logic every 2nd DS frame (30 Hz); compare vanilla ticks with every 2nd MphRead frame.

## Memory (USA rev 1)

| What | Address | Note |
|---|---|---|
| Player 0 (`CPlayer`, stride 0xF30) | `0x020DAF94` | EU rev 1 (MphRead `Memory.cs` "amhp1") has `0x020DB034`: US = EU - 0xA0 here. Type 25 at +0, position fx32 at +0x1C, hunter at +0x400, slot at +0x84D |

Player fields used so far: position +0x1C, speed +0x34, energy +0xDA, strafe/walk tilt +0x684/+0x688, camera position
+0x55C (CameraInfo). Field layouts: MphRead `MemoryClasses.cs` (offsets are the DS struct offsets).
