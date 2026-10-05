# Second Hunt

**Second Hunt is _Metroid Prime Hunters_ rebuilt as a native Android game.** It plays the Nintendo DS original, campaign
and multiplayer, in true widescreen at up to 120 fps, with dual-stick controller controls and a one-screen HUD. It runs
everything from your own copy of the game.

Second Hunt is a fan project. It contains no game data and isn't affiliated with or endorsed by Nintendo.

> **Status: Beta 1 (Android).** Playable start to finish, with rough edges. See [Known issues](#known-issues),
> [What's planned](#whats-planned), [MphRead's to-do list](#mphreads-to-do-list),
> [What we've fixed in MphRead](#what-weve-fixed-in-mphread) and
> [Improvements over the original game](#improvements-over-the-original-game).

## What Second Hunt is

**It isn't an emulator.** An emulator pretends to be a DS and runs the game's original program. Second Hunt is a
reimplementation instead:

- **The game's logic is C# code.** Enemies, weapons, physics, rooms, scripting and bots were rebuilt by hand from
  reverse engineering in [MphRead](https://github.com/NoneGiven/MphRead), NoneGiven's recreation of the game. Second Hunt
  is a fork of it.
- **Second Hunt builds the rest of the game around that logic,** as an Android app: the title screen and menus, the
  gunship, star map and save files, the HUD, music and sound, cutscenes, controls, and LAN multiplayer.
- **The game's content comes from your ROM.** On first launch you pick your copy of the game. Second Hunt unpacks it into
  the app's private storage, on your device, and reads the models, rooms, menus, music and movies from it. Nothing is
  downloaded or uploaded.

Because the game is drawn natively rather than by an emulated DS:

- the 3D is **real widescreen** at your screen's resolution, not a stretched 256×192 image;
- it runs at **up to 120 fps** on displays that support it;
- the DS's two screens become **one screen**: the HUD sits in the game view, and what the touch screen did (morph
  ball, weapon select, scan visor, dialog buttons) moves to buttons;
- **controls are built for a controller**, modelled on _Metroid Prime Remastered_'s dual-stick layout, with optional
  gyro aim and an optional on-screen touch overlay.

**The goal is to play like the original.** Where Second Hunt behaves differently from the DS game (speeds, timings, fire
rates, sounds), we measure the difference against the original and fix it. We don't change gameplay values for
convenience.

## What's in Beta 1

**Campaign**
- The full adventure, from the opening to Gorea: Celestial Archives, Alinos, Vesper Defense Outpost, Arcterra and the
  Oubliette.
- The game's own title screen, attract movie, file select (three save files, with copy and delete), options and credits,
  rebuilt from the menus in your ROM.
- The gunship: cockpit, ship menus, star map, planet select, and the landing and take-off movies.
- All cutscenes, the HUD for every hunter, and the scan visor with its dialogs.
- A **pause map** on Select. It can show the game's own map, or a Prime-style map of just the rooms you've explored.
- The game's own music and sound effects, played from your ROM's sound data, plus the title music and voices.

**Multiplayer**
- **Bot matches** on one device, in every mode, on the original arenas, with adjustable bot level and time limit.
- **LAN matches**: two devices on the same Wi-Fi network, Battle mode (experimental). Main menu > MULTIPLAYER.

**Audio** (the game's own **OPTIONS > AUDIO** page, working as in the original)
- Sound effect and music volume, heard as you change them.
- The **sound test** and **music test**: play any of the game's 425 sounds or 34 songs.
- **QUALITY** (in place of the original's mic setting): how the music is played. ORIG is the DS sound; ORIG+FIX is the
  DS sound without its clicks and clipping; HQ+FIX is smoother; HQ+TONE is HQ+FIX with the DS's treble added back. It
  changes live, mid-song.

**Options** (in the game's Options menu, under **RECOMP SETTINGS**; L / R turn its pages)
- First-person or third-person camera.
- Gyro aim: off, on, or only while zoomed or scanning, with its own speed and axis settings.
- On-screen touch controls: auto (shown when no controller is connected), on or off.
- Menu presentation: both DS screens merged onto one, or side by side; crisp or smoothed pixel art; the HUD's line art
  redrawn as clean lines or kept as DS pixels.
- Pause map look and mode.
- Credits and licences, and a crash-report share button.
- **Lua mods**, run in a sandbox that can't touch your files or the network.

## Getting started

**You need:**
- an Android 8.0+ device with OpenGL ES 3.0;
- a game controller (recommended); touch controls are available too;
- your own **_Metroid Prime Hunters_ (USA)** ROM, version 1.0 or 1.1 (game code AMHE). Other regions aren't supported
  yet; the app tells you if your file is a different one.

**To install:**
1. Download the APK from [Releases](../../releases).
2. Open it on your device. Android will ask you to allow installs from your browser or file manager.
3. On first launch, pick your ROM file. Second Hunt unpacks it once; after that it starts straight into the game.

## Controls

**Controller** (positions as on an Xbox-style pad: A bottom, B right, X left, Y top):

| Input | Action |
|---|---|
| Left stick | Move |
| Right stick | Aim |
| R2 or B | Fire (lay bombs in morph ball) |
| A or L1 | Jump (boost in morph ball) |
| X | Morph ball |
| L2 | Scan (with the scan visor on), otherwise zoom |
| R3 | Zoom |
| Hold Y + right stick | Weapon wheel: point at a weapon, let go of Y to equip it |
| R1 | Switch between missiles and the Power Beam |
| D-pad up / down | Power Beam / missiles |
| D-pad left | Your third weapon slot (picked in the ship's WEAPON SELECT) |
| D-pad right | Scan visor on/off |
| Select | Pause map (campaign only) |
| Start | Pause menu |
| A / B in dialogs | OK or YES / NO |
| Android Back (button or gesture) | Pause menu; in a menu, the map or the ship, it works like B. It never quits the game. |

**Touch:** the overlay puts a move stick on the left half of the screen and an aim stick on the right, with buttons for
fire, jump, morph, missile, weapon (hold it and drag toward a weapon, like the DS stylus) and pause, plus visor, scan
and map in the campaign. While missiles are selected, the missile button reads POWER BEAM: it switches back. In
dialogs, JUMP becomes OK/YES and FIRE becomes NO.

**Gyro:** turn the device to fine-tune your aim. The right stick still works as usual.

## Known issues

**Missing for now**
- **Beating Gorea ends on a plain "mission complete" screen.** Neither of the real ending sequences nor the credits roll
  is in yet.
- **LAN play is limited to two players and Battle mode.** Item pickups may not stay in sync between the two devices.
- **Only USA ROMs** (1.0 and 1.1) are supported.
- **No rumble.** The original supported the DS Rumble Pak; controller and phone vibration aren't wired up yet.
- **No multiplayer license.** Your multiplayer stats (kills, deaths, wins, play time) aren't recorded, and every hunter
  and arena is open from the start instead of being unlocked through the adventure.
- **Some save details aren't kept yet:** the game timer, boss records, a few stats and the artifact flags.
- **Speaker type** (OPTIONS > AUDIO) is saved, but SURROUND and HEADPHONES sound the same as STEREO for now.

**Controls**
- Changing weapons mid-fight is awkward. The DS showed a weapon bar on its bottom screen, which isn't drawn yet, so the
  third weapon slot (D-pad left) has no on-screen label. A new weapon ring is planned.
- The touch overlay has no buttons for D-pad left, up and down; use the weapon button (hold and drag) instead. The morph
  ball can't be steered by touch the way the DS stylus could.
- The device's volume and screenshot buttons may do nothing on the title and menu screens.

**Looks and performance**
- Pop-ups such as item pickups and the enter-ship prompt look rough. Their layout still needs cleaning up.
- There can be short frame dips the first time a room or cutscene loads.
- The scan visor's box and icon aren't quite the right shape in widescreen.
- The HUD's energy and ammo bars don't flash and change colour exactly as in the original (low ammo, pickups).

**Reported, being checked against the original**
- Looping or odd sounds in Piston Cave (Alinos).
- Flickering lava texture in Processor Core.
- The Fault Line shock barrier is invisible and can be crossed.
- The Docking Bay floating platforms slide you backward.
- Some skybox parts spin at different speeds.
- Council Chamber: what the shot switch triggers, and whether the bounce pad should always be there.
- The small red sniper targets (hit with the Imperialist) don't stay folded.
- Frame drops in Transfer Lock.
- Which sounds play when you arrive through a portal (the Sic Transit escape, Elder Passage).
- Feel: the Judicator's fire rate, the Imperialist's auto-zoom, Slench's hit box, and whether the Volt Driver's charged
  shot should distort your visor.

**Devices:** Second Hunt has only been tested on Snapdragon 8 Gen 2 hardware so far (AYN Odin 2 Portal, Galaxy S23).
Reports from other phones and handhelds are very welcome.

**If it crashes:** go to RECOMP SETTINGS > crash log > SHARE to send the newest crash report. It holds no personal
data. Please include it in a bug report on the [Issues](../../issues) page, along with your device and what you were
doing.

## What's planned

**Next**
- **Beta 1.1: HD Samus suits** (her body and morph ball) from your own copy of _Metroid Prime_ (GameCube), imported in
  the app.
- **A Windows version.**
- **The real endings,** both of them, and the credits roll.
- **A new weapon selector:** hold L2 to open a full-circle weapon ring, point with the right stick, then let go to
  equip. It will work while you move.
- **Full touch controls**, including the DS's touch-screen morph ball controls.
- **Sound timing like the original:** what plays while paused and during escape sequences, and how music and sounds stop
  or fade when you die, return to the ship or finish a match.
- **The rest of the save data:** game timer, boss records, stats and artifact flags.
- **Fixes for the known issues above**, each measured against the original game.

**Later**
- **More multiplayer:** more than two LAN players, every mode over LAN, and online play; the original's rules for how
  damage, weapons and items are shared between devices; the multiplayer license and your stats; hunters and arenas
  unlocked through the adventure, as in the original.
- **Rumble** on controllers and phones that support it.
- **The original's SURROUND and HEADPHONES speaker types,** and HQ music that keeps more of the DS's treble.
- **The sound test unlocked the original's way:** OPTIONS > AUDIO's SFX TEST and MUSIC TEST open after the true ending's
  results screen. For now they are always open.
- **A Prime-style HUD** option: a mini map and weapon indicators added to the current HUD.
- **Other regions' ROMs** (Europe, Japan, Korea).
- **Dual-screen devices:** the DS's second screen on a handheld's second display.
- **More HD content from games you own:** each Prime game's arm cannon, the _Metroid Prime 2: Echoes_ multiplayer
  arenas, and the other hunters.
- **The rest of MphRead's to-do list** (next section).

## MphRead's to-do list

MphRead marks every unfinished or uncertain spot in its code with a `todo` comment: 1,451 of them as of October 2026.
Second Hunt treats that list as its own. We work through it area by area, check each one against the original game, and
fix whatever changes how the game plays.

| Area | TODOs | What they're about |
|---|---|---|
| Frame rate | 822 | MphRead runs the game logic at 60 Hz; the DS runs it at 30 Hz. Every timer, speed and step was converted by hand and marked. Most conversions are exact; the ones that weren't are being measured and fixed (see below). |
| Naming | 173 | Fields and values whose purpose isn't known yet. No effect on play. |
| Rendering | 36 | Effects, texture animation, how some objects are drawn. |
| Wi-Fi rules | 21 | How the original shares damage, weapons and items between DS units in a wireless match. To be ported as LAN rules. |
| Viewer and debug tools | 15 | MphRead's desktop viewer. No effect on play. |
| Bots | 13 | Open questions about the bots' choices, to be answered from the original's code. |
| HUD and menus | 12 | Bar flashes, pop-up text wrapping and size, the multiplayer HUD. |
| Multiplayer license | 11 | The stats the original records per player. |
| Sound and music | 9 | Stops, fades and sounds during pauses and escapes. |
| Saves | 5 | Story save fields not kept yet. |
| Rumble | 2 | The DS Rumble Pak. |
| Everything else | 332 | Gameplay details not ported yet, open questions, and notes about the code itself. |

**Still open, in plain words:**
- **Movement:** sliding along walls and climbing over edges aren't exact yet. Walking reaches top speed a little sooner
  and stops a little shorter than the original. The boost ball, jump pads, bomb jumps and Space Jump haven't been
  measured yet.
- **Bots:** a few random decisions may happen twice as often as in the original.
- **Effects:** some particle and beam effects still need their 60 Hz timing checked. The Cretaphid's beam is drawn too
  thin, and Gorea's second form moves jerkily.
- **Cutscenes:** data from a previous room's cutscenes isn't cleared on a room change, and an object can show for one
  frame before some cutscenes start.
- **HUD:** energy and ammo bar flashes, the scan visor's proportions, and pop-up text wrapping and size.
- **Saves:** the game timer, boss records, some stats and the artifact flags.
- **Sound:** sounds during pauses and escape sequences, music and sound stops and fades, and how often Gorea's Shock
  Coil sound restarts.
- **Multiplayer:** the Wi-Fi rules, the license stats, unlocks from the adventure and multiplayer saving.
- **Endings:** the credits after each ending.
- **Wide views:** whether portals at the screen edges are tested the right way for a wider screen, and where the gun's
  muzzle smoke is drawn.
- **Rumble.**

Five spots are original-game bugs that MphRead reproduces on purpose (for example, Gorea's first form has two weapon
tracks swapped). They stay, because that's how the original plays.

MphRead's own README also plans a room editor, a save editor, and more rendering and gameplay logic.

## What we've fixed in MphRead

Each of these was measured against the original game, which we run in an emulator (BizHawk with the melonDS core) on
the same route with the same inputs.

**Feel and physics** (the 30 to 60 Hz conversion)
- **Jumping:** the jump arc peaked 3% low. It now matches the original tick for tick over the whole arc.
- **Walking:** top speed was 7-9% too fast. It's now exact.
- **Morph ball:** it accelerated twice as fast as the original. Acceleration and top speed now match tick for tick.
- **Slopes:** walking up slopes steeper than about 26° was 10-40% too slow. It's now within a few percent.
- **Strafing:** the camera leaned half as far as the original and settled twice as fast. It now matches.
- **Morph ball camera:** it followed the ball too closely. It now matches.

**Bugs**
- **Logbook:** a new game marked the wrong logbook entries (off by one).
- **Bots** could see past the left and top edges of their view. They now use all four edges, as the original's code does.
- **Portal sound:** the sound of a portal opening was skipped about half the time.
- **Your weapon** reset to the Power Beam whenever a portal or a cutscene reloaded the room. It's now kept.
- **The gunship's thruster flames** showed while the ship itself was hidden during the landing.
- **Capture** in Data Shrine and Head Shot failed to load (two misspelled file names).
- **Bounty:** the marker for the flag base never showed, because it looked up the wrong list.
- **Boss encounters:** several encounter files were spelled differently from the game's own and failed to load on
  case-sensitive storage such as Android's.
- **A crash** when the game released the same effect twice (seen while escaping Arcterra).

## Improvements over the original game

Gameplay values stay the original's. These are changes to how you see, hear and control the game:

- **Real widescreen 3D** at your screen's resolution, instead of 256×192.
- **Up to 120 fps** on displays that support it. MphRead runs the game logic at 60 Hz instead of the original's 30, and
  we tune it to behave like the original.
- **One screen:** the HUD sits in the game view, and the touch-screen controls move to buttons.
- **Controller controls** modelled on _Metroid Prime Remastered_, with optional gyro aim and an optional touch overlay.
- **A pause map** on Select. It has a Prime-style mode that shows only the rooms you've explored, lets you move freely
  and hop between rooms, plus a detailed look that draws every room from its real shape.
- **An optional third-person camera.**
- **The HUD's line art redrawn** as clean lines at any resolution, or kept as DS pixels.
- **Menus** with both DS screens merged into one, or side by side.
- **Bot matches on one device** in every mode, and **LAN matches over Wi-Fi** without a second DS.
- **A music quality setting** that plays the DS music without its clicks and clipping, or smoother, switchable mid-song.
- **A fix for a version 1.1 bug:** on 1.1 ROMs the file-select sounds (including the start-game sound) never play,
  because that version misaligned a sound table. Second Hunt plays them as version 1.0 does.
- **Quality of life:** Android Back never quits the game, crash reports can be shared from the settings, and Lua mods
  run in a sandbox.

## Building from source

You need the .NET 9 SDK with the Android workload (`dotnet workload install android`), JDK 17 and the Android SDK.

```
dotnet build src/MphRead.Android/MphRead.Android.csproj -c Release -p:JavaSdkDirectory=<jdk17> -p:AndroidSdkDirectory=<android-sdk>
```

That makes a development build (`com.mphrecomp.app`, signed with your machine's debug key). It installs alongside the
release app and keeps the developer tools. Release builds are made with `-p:MphReleaseSign=true` and the project's own
signing key, as `com.secondhunt.app`.

| Folder | What's in it |
|---|---|
| `src/MphRead` | MphRead's game logic, formats and renderer (upstream, with small hooks for Second Hunt) |
| `src/MphRecomp.Core` | Second Hunt's shared code: menus, settings, input, multiplayer, mods |
| `src/MphRead.Android` | The Android app: campaign, front end, matches, audio, touch overlay |
| `src/MphRead.Tools` | MphRead's desktop viewer, plus Second Hunt's PC-side test harnesses |
| `src/NcsfPlay` | The DS music player |

## Credits

Second Hunt is built on **MphRead** by NoneGiven (MIT; see `LICENSE` and `NOTICE-fork.md`). Third-party libraries and their
licences are listed in `THIRD_PARTY_NOTICES.md`, which the app also shows under RECOMP SETTINGS > credits.

From MphRead's README:

### Acknowledgements

A significant portion of this project's code was based on the file format information or source code from several other projects.

- **dsgraph** - The original MPH model viewer, on which all other projects are built.
- **[Chemical's model format](https://gitlab.com/ch-mcl/metroid-prime-hunters-file-document/-/blob/master/Model/BinModel.md)** - Documentation of the model format.
- **[McKay42's mph-model-viewer](https://github.com/McKay42/mph-model-viewer)** - COLLADA export method.
- **[McKay42's mph-arc-extractor](https://github.com/McKay42/mph-arc-extractor)** - ARC file format information.
- **[Barubary's dsdecmp](https://github.com/Barubary/dsdecmp)** - LZ10 compression routines.
- **[loveemu's swav2wav](https://github.com/loveemu/loveemu-lab)** - SWAV conversion function.
- **[Gericom's ffmpeg patch](https://lists.ffmpeg.org/pipermail/ffmpeg-devel/2021-March/277774.html)** - ActImagine VX movie file format information.
- **[CharlesVanEeckhout's actimagine decoder](https://github.com/CharlesVanEeckhout/actimagine)** - Further understanding of VX video decoding, based on the above ffmpeg patch.
- **[CyberBotX's NCSF](https://github.com/CyberBotX/NCSF)** - Source code for the NCSF converter and player for Nintendo DS sequenced music.

(Second Hunt note: Second Hunt has since replaced MphRead's VX movie decoder, and the sequencer in the NCSF-based music
player, with its own clean-room implementations, which give the same pictures and sound. See
[docs/cleanroom](docs/cleanroom).)

### Special Thanks

This project's reverse engineering effort was developed parallel to **[hackyourlife's mph-viewer](https://github.com/hackyourlife/mph-viewer)**, a model viewer implementation in C. Major features such as the transparency rendering implementation were derived from its source code.

(Second Hunt note: Second Hunt has since replaced that transparency rendering code with its own clean-room implementation,
which draws the same result. See [docs/cleanroom](docs/cleanroom).)

## About MphRead (upstream)

MphRead is a reverse engineering and game recreation effort comprising a model viewer, scene renderer, and general
parser for file formats used in Metroid Prime Hunters, with documentation of game features in its
[wiki](https://github.com/NoneGiven/MphRead/wiki). See the upstream repository for its desktop releases and setup
guide.
