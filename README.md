# Second Hunt

**Second Hunt is _Metroid Prime Hunters_ rebuilt as a native game for Android and Windows.** It plays the Nintendo DS
original campaign and multiplayer, in true widescreen, with dual-stick controller controls (or keyboard and mouse on PC)
and a one-screen HUD. It runs everything from your own copy of the game.

Second Hunt is a fan project and a fork of MphRead. It contains no game data and isn't affiliated with or endorsed by Nintendo.

> **Beta 1.2 (Android and Windows).** Playable start to finish, with some rough edges. See [Known issues](#known-issues) and
> [What's planned](#whats-planned).

> **AI tools were used in the making of this project.** If you don't support AI in video games, then you don't have to play. Or, look
> at the product and judge it for yourself. Future support and maintenance is planned to continue regardless. This is a project born out of
> love for a game that was precious to me in childhood, and the desire to experience it in a new and modern way. So most importantly, please enjoy.

## What Second Hunt is

The game's logic (enemies, weapons, physics, rooms, scripting, bots) comes from
[MphRead](https://github.com/NoneGiven/MphRead), NoneGiven's C# recreation of the game, built from reverse engineering.
Second Hunt adds everything else it takes to play it on Android and Windows: the title screen and menus, the gunship,
star map and saves, the HUD, music and sound, cutscenes, controls, and LAN multiplayer. Both versions run the same game
code; only the window, the controls and the sound output are each platform's own.

All the game's content comes from your ROM. The first time you launch the app you pick your copy of the game, and
Second Hunt unpacks it into the app's own storage on your device or PC. Nothing gets downloaded or uploaded.

Since the game is drawn natively instead of through an emulated DS, the 3D runs in real widescreen at your screen's
resolution, and at 60 fps instead of the DS's 30. The two DS screens become one: the HUD sits in the game view,
and the touch-screen controls (morph ball, weapon select, scan visor, dialog buttons) move to buttons. Controls are made
for a controller, laid out like _Metroid Prime Remastered_, with optional gyro aim and an optional touch overlay on
Android. On Windows the keyboard and mouse use MphRead's own PC controls.

The aim is for it to play like the DS game. When something behaves differently (a speed, a timing, a fire rate, a
sound), it is compared against the original game and fixed. Gameplay values aren't changed.

## What's in Beta 1.2

**Campaign**
- The whole adventure from the opening to Gorea: Celestial Archives, Alinos, Vesper Defense Outpost, Arcterra and the
  Oubliette
- The game's own title screen, attract movie, file select (three files, with copy and delete), options and credits,
  rebuilt from the menus in your ROM
- The gunship: cockpit, ship menus, star map, planet select, and the landing and take-off movies
- Every cutscene, the HUD for every hunter, and the scan visor
- A pause map on Select, showing either the game's own map or a Prime-style map of the rooms you've explored
- The game's music and sound effects from your ROM, plus the title music and voices

**Multiplayer**
- Bot matches on one device in every mode and arena, with adjustable bot level and time limit
- LAN matches between two devices on the same network, Android or Windows in any mix, Battle mode only for now
  (experimental). It's under Main menu > MULTIPLAYER.

**Controls** (OPTIONS > CONTROLS, also RECOMP SETTINGS > CONTROLS and the gunship's OPTIONS)
- Change which button does what: pick a function, then press its new button (X on a function adds a second button).
  For a controller on Android and Windows, and for the keyboard and mouse on Windows.
- Sensitivity and look invert for the controller, and on Windows a separate sensitivity for the mouse

**Audio** (OPTIONS > AUDIO, wired up like the original)
- Sound effect and music volume
- The sound test and music test, with all 425 sounds and 34 songs
- A QUALITY setting in place of the mic option. It changes how the music is played, live, mid-song:
  - ORIG: the DS sound
  - ORIG+FIX: the DS sound without the clicks and clipping
  - HQ+FIX: smoother
  - HQ+TONE: HQ+FIX with the DS's treble added back

**Options** (Options > RECOMP SETTINGS, L / R to change pages)
- First- or third-person camera
- Music quality, the same setting as QUALITY on OPTIONS > AUDIO. In the pause menu the paused music plays while MUSIC
  is selected, so a change can be heard.
- Controls: the same page as OPTIONS > CONTROLS
- Gyro aim (Android): off, on, or only while zoomed or scanning, with its own speed and axis settings
- Touch controls (Android): auto (shown when no controller is connected), on or off
- Menus with both DS screens merged into one or side by side, crisp or smoothed pixel art, and the HUD's line art
  redrawn as clean lines or kept as DS pixels
- Pause map look and mode
- Credits, licenses, and a button to share crash reports (on Windows it opens their folder)
- Lua mods, sandboxed so they can't touch your files or the network
- MODS and RECOMP SETTINGS are on the game's OPTIONS page, where the Nintendo WFC stats option was

## Getting started

**Android.** You'll need:
- An Android 8.0+ device with OpenGL ES 3.0
- A controller (recommended), though touch controls work too
- Your own _Metroid Prime Hunters_ (USA) ROM, version 1.0 or 1.1 (game code AMHE). Other regions aren't supported yet,
  and the app will tell you if your file is one of them.

To install:
1. Download the APK from [Releases](../../releases).
2. Open it on your device. Android will ask you to allow installs from your browser or file manager.
3. On first launch, pick your ROM file. Second Hunt unpacks it once, and after that it goes straight into the game.

**Windows.** You'll need:
- Windows 10 or 11, 64-bit, with a graphics card or chip that has OpenGL 3.3 or newer
- A keyboard and mouse or a controller
- The same ROM as above

To install:
1. Download the Windows zip from [Releases](../../releases) and unzip it anywhere.
2. Open `SecondHunt.exe`. Windows may say it protected your PC, since the app isn't signed with a paid certificate:
   click "More info", then "Run anyway".
3. On first launch, pick your ROM file or drop it on the window. Saves and settings are kept in
   `%LOCALAPPDATA%\Second Hunt`.
4. For LAN matches, Windows Firewall has to let Second Hunt through. Windows usually asks the first time you host or
   join, but the prompt can hide behind a full-screen window, and on a network set to Public nothing gets through until
   it's allowed. If other devices can't see your match or joining says no answer, go to Windows Security > Firewall &
   network protection > Allow an app through firewall, add `SecondHunt.exe`, and tick both Private and Public.

## Controls

These are the defaults. OPTIONS > CONTROLS changes any of the game buttons below; the sticks, Start, Select and the
buttons in menus and dialogs stay where they are.

**Controller** (Xbox-style layout: A bottom, B right, X left, Y top)

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
| D-pad left / right | Previous / next weapon (only weapons you have, with ammo) |
| D-pad up | Scan visor on/off (in a match, where there's no visor: Power Beam) |
| D-pad down | Missiles |
| Select | Pause map (campaign only) |
| Start | Pause menu |
| A / B in dialogs | OK or YES / NO (the press that closes a dialog doesn't also jump or fire) |
| Android Back (button or gesture) | Pause menu. In a menu, the map or the ship it works like B. It never quits the game. |

**Keyboard and mouse** (Windows; MphRead's own PC controls)

| Input | Action |
|---|---|
| W A S D | Move |
| Mouse | Aim (its own sensitivity: OPTIONS > CONTROLS, under KEYBOARD & MOUSE) |
| Left click | Fire |
| Right click | Zoom, or scan with the scan visor on |
| Space | Jump (boost in morph ball) |
| C | Morph ball |
| Q | Lay bombs in morph ball (left click does too), scan with the scan visor on |
| E | Scan visor on/off |
| Mouse wheel | Next / previous weapon (only weapons you have, with ammo) |
| Arrow keys | The controller's D-pad: previous / next weapon, scan visor, missiles |
| Hold middle click | Weapon menu: move the mouse toward a weapon, let go to equip it |
| 1 to 9 | Power Beam, missiles, then each weapon in turn |
| Tab or Esc | Pause menu (Esc also backs out of menus, the map and the ship's screens) |
| M | Pause map (campaign only) |
| F3 | Status line: short, detailed or off |
| F11 or Alt+Enter | Full screen |
| Space, Enter or left click in dialogs | OK or YES (Backspace or right click is NO) |

In the menus, the arrow keys or W A S D move, Enter or Space choose, Esc or Backspace go back, Q / E change pages, and
the mouse clicks anything the DS touch screen would. A controller works on Windows too, with the buttons above.

**Touch:** the overlay has a move stick on the left half of the screen and an aim stick on the right, with buttons for
fire, jump, morph, missile, weapon (hold it and drag toward a weapon, like the DS stylus) and pause, plus visor, scan
and map in the campaign. When missiles are selected, the missile button says POWER BEAM and switches you back. In
dialogs, JUMP is OK/YES and FIRE is NO. The touch buttons keep these jobs whatever the controller's buttons are changed
to.

**Gyro:** tilt the device to fine-tune your aim. The right stick still works as normal.

## Known issues

**Missing for now**

Anything on the menus that isn't in yet shows a WORK IN PROGRESS box when you pick it, closed with its check.

- Beating Gorea ends on a plain "mission complete" screen. The real endings and the credits roll aren't in yet.
- LAN play is two players and Battle mode only, and item pickups may not stay in sync between the two devices.
- Only USA ROMs (1.0 and 1.1) work.
- No rumble yet. The original supported the DS Rumble Pak, but controller and phone vibration aren't hooked up.
- No multiplayer license. Your stats (kills, deaths, wins, play time) aren't recorded, and every hunter and arena is
  open from the start instead of being unlocked through the adventure.
- Some save details aren't kept yet: the game timer, boss records, a few stats and the artifact flags.
- The speaker setting on OPTIONS > AUDIO is saved, but SURROUND and HEADPHONES sound the same as STEREO for now.
- ERASE ALL DATA on OPTIONS isn't in yet.
- The MOVIES gallery doesn't unlock movies yet, so every movie shows "?".

**Controls**
- The DS showed a weapon bar on the bottom screen, which isn't drawn yet, so there's no on-screen list of the weapons
  you're carrying. A new weapon ring is planned.
- The touch overlay has no buttons for stepping through weapons, so use the weapon button (hold and drag) instead. You
  also can't steer the morph ball by touch the way you could with the DS stylus.
- The volume and screenshot buttons may not work on the title and menu screens.

**Looks and performance**
- Pop-ups like item pickups and the enter-ship prompt look rough and still need layout work.
- You may get short frame dips the first time a room or cutscene loads.
- The scan visor's box and icon aren't quite the right shape in widescreen.
- The HUD's energy and ammo bars don't flash and change color exactly like the original (low ammo, pickups).

**Reported, still being checked against the original**
- Looping or odd sounds in Piston Cave (Alinos)
- Flickering lava texture in Processor Core
- The Fault Line shock barrier is invisible and can be walked through
- The Docking Bay floating platforms slide you backward
- Some skybox parts spin at different speeds
- Council Chamber: what the shot switch triggers, and whether the bounce pad should always be there
- The small red sniper targets (hit with the Imperialist) don't stay folded
- Frame drops in Transfer Lock
- Which sounds play when you arrive through a portal (the Sic Transit escape, Elder Passage)
- Feel: the Judicator's fire rate, the Imperialist's auto-zoom, Slench's hit box, and whether the Volt Driver's charged
  shot should distort your visor

**Devices:** so far Second Hunt has only been tested on Snapdragon 8 Gen 2 hardware (AYN Odin 2 Portal, Galaxy S23).
Reports from other phones and handhelds are very welcome.

**If it crashes:** go to RECOMP SETTINGS > crash log > SHARE to send the latest crash report (it doesn't contain any
personal data). On Windows the same row says OPEN and shows the report's folder. Please attach it to a bug report on the [Issues](../../issues) page, with your device and what you were
doing.

## What's planned

**Next**
- Beta 2: HD Samus suits (her body and morph ball), imported in the app from your own copy of _Metroid Prime_
  (GameCube)
- Both real endings and the credits roll
- A new weapon selector: hold L2 for a full weapon ring, point with the right stick and let go to equip, even while
  moving
- Full touch controls, including the DS's touch-screen morph ball controls
- Sound timing like the original: what plays while paused and during escapes, and how music and sounds stop or fade
  when you die, go back to the ship or finish a match
- The rest of the save data: game timer, boss records, stats and artifact flags
- Fixes for the known issues above, each checked against the original game

**Later**
- More multiplayer: more than two LAN players, every mode over LAN, online play, the original's rules for sharing
  damage, weapons and items between devices, the multiplayer license and stats, and unlocking hunters and arenas
  through the adventure
- Rumble on controllers and phones that support it
- The original's SURROUND and HEADPHONES speaker modes, and HQ music that keeps more of the DS's treble
- Unlocking the sound test the original way (after the true ending's results screen). For now it's always open.
- An optional Prime-style HUD with a mini map and weapon indicators
- European, Japanese and Korean ROMs
- Dual-screen devices, with the DS's second screen on a handheld's second display
- More HD content from games you own: each Prime game's arm cannon, the _Metroid Prime 2: Echoes_ multiplayer
  arenas, and the other hunters
- The rest of MphRead's to-do list (below)

## MphRead's to-do list

MphRead marks every unfinished or uncertain spot in its code with a `todo` comment, and there are 1,451 of them as of
October 2026. They're being worked through area by area, and the ones that change how the game plays are fixed after
being compared against the original game.

| Area | TODOs | What they're about |
|---|---|---|
| Frame rate | 822 | MphRead runs the game logic at 60 Hz instead of the DS's 30, so every timer, speed and step was converted by hand and marked. Most are exact. The ones that aren't are being measured and fixed (see below). |
| Naming | 173 | Fields and values whose purpose isn't known yet. No effect on play. |
| Rendering | 36 | Effects, texture animation, how some objects are drawn. |
| Wi-Fi rules | 21 | How the original shares damage, weapons and items between DS units in a wireless match. These will become the LAN rules. |
| Viewer and debug tools | 15 | MphRead's desktop viewer. No effect on play. |
| Bots | 13 | Open questions about how the bots decide things, to be answered from the original's code. |
| HUD and menus | 12 | Bar flashes, pop-up text wrapping and size, the multiplayer HUD. |
| Multiplayer license | 11 | The stats the original records per player. |
| Sound and music | 9 | Stops, fades and sounds during pauses and escapes. |
| Saves | 5 | Story save fields not kept yet. |
| Rumble | 2 | The DS Rumble Pak. |
| Everything else | 332 | Gameplay details not ported yet, open questions, and notes about the code itself. |

**Still open:**
- Movement: sliding along walls and climbing over edges aren't exact yet. Walking gets to top speed a bit sooner and
  stops a bit shorter than the original. The boost ball, jump pads, bomb jumps and Space Jump haven't been measured yet.
- Bots: a few random decisions may happen twice as often as in the original.
- Effects: some particle and beam effects still need their 60 Hz timing checked. The Cretaphid's beam is drawn too
  thin, and Gorea's second form moves jerkily.
- Cutscenes: data from the previous room's cutscenes isn't cleared when you change rooms, and an object can show for one
  frame before some cutscenes start.
- HUD: energy and ammo bar flashes, the scan visor's proportions, and pop-up text wrapping and size.
- Saves: the game timer, boss records, some stats and the artifact flags.
- Sound: sounds during pauses and escape sequences, music and sound stops and fades, and how often Gorea's Shock Coil
  sound restarts.
- Multiplayer: the Wi-Fi rules, the license stats, unlocks from the adventure, and multiplayer saving.
- Endings: the credits after each ending.
- Wide views: whether portals at the screen edges are tested correctly for a wider screen, and where the gun's muzzle
  smoke is drawn.
- Rumble.

Five of the TODOs are bugs in the original game that MphRead copies on purpose (Gorea's first form has two weapon
tracks swapped, for example). Those stay in, since that's how the original plays.

MphRead's own README also plans a room editor, a save editor, and more rendering and gameplay logic.

## What's been fixed in MphRead

All of these were compared against the original game, on the same route with the same inputs.

**Feel and physics** (from the 30 to 60 Hz conversion)
- Jumping: the arc peaked 3% low. It now matches the original tick for tick.
- Walking: top speed was 7-9% too fast. It's exact now.
- Morph ball: it accelerated twice as fast as the original. Acceleration and top speed match now.
- Slopes: walking up anything steeper than about 26° was 10-40% too slow. It's within a few percent now.
- Strafing: the camera leaned half as far as it should and settled twice as fast. Fixed.
- Morph ball camera: it followed the ball too closely. Fixed.

**Bugs**
- A new game marked the wrong logbook entries (off by one).
- Bots could see past the left and top edges of their view. They now use all four edges like the original.
- The portal opening sound was skipped about half the time.
- Your weapon reset to the Power Beam whenever a portal or cutscene reloaded the room. It stays selected now.
- The gunship's thruster flames showed while the ship was hidden during the landing.
- Capture mode in Data Shrine and Head Shot wouldn't load (two misspelled file names).
- In Bounty, the flag base marker never showed because it checked the wrong list.
- Several boss encounter files were spelled differently from the game's own and wouldn't load on case-sensitive
  storage like Android's.
- The game could crash when it released the same effect twice (seen while escaping Arcterra).

## Improvements over the original game

Gameplay values are the original's. What's different is how the game looks, sounds and controls:

- Real widescreen 3D at your screen's resolution instead of 256×192
- 60 fps instead of the original's 30. MphRead runs the game logic at 60 Hz, tuned to behave like the original.
- One screen, with the HUD in the game view and the touch-screen controls moved to buttons
- Controller controls modeled on _Metroid Prime Remastered_, with optional gyro aim and a touch overlay
- Weapons you can step through with the D-pad or the mouse wheel, where the DS only had its touch screen
- Every game button can be moved to another one, where the DS had four fixed control types
- Options save as soon as they change, so the options pages have no SAVE button
- A pause map on Select. Its Prime-style mode only shows rooms you've explored and lets you move around and hop between
  rooms, and its detailed view draws every room from its real shape.
- An optional third-person camera
- The HUD's line art redrawn as clean lines at any resolution, or kept as DS pixels
- Menus with both DS screens merged into one, or side by side
- Bot matches on one device in every mode, and LAN matches over Wi-Fi without needing a second DS
- A music quality setting that takes the clicks and clipping out of the DS music, or smooths it out, and switches
  mid-song
- A fix for a version 1.1 bug: on 1.1 ROMs the file-select sounds (including the start-game sound) never play, because
  that version misaligned a sound table. Second Hunt plays them like version 1.0 does.

## Building from source

**Android.** You need the .NET 9 SDK with the Android workload (`dotnet workload install android`), JDK 17 and the
Android SDK.

```
dotnet build src/MphRead.Android/MphRead.Android.csproj -c Release -p:JavaSdkDirectory=<jdk17> -p:AndroidSdkDirectory=<android-sdk>
```

That gives you a development build (`com.mphrecomp.app`, signed with your machine's debug key), which installs next to
the release app and has the developer tools. Release builds use `-p:MphReleaseSign=true` and the project's signing key,
and come out as `com.secondhunt.app`.

**Windows.** You need the .NET 9 SDK.

```
dotnet build src/SecondHunt.Desktop/SecondHunt.Desktop.csproj -c Release
```

That gives you a development build (it keeps its data in `%LOCALAPPDATA%\MPH Recomp`). The release zip comes from
`src/SecondHunt.Desktop/Publish-Windows.ps1`, which builds a self-contained public build (`-p:MphPublic=true`) and packs
it with its README and notices.

| Folder | What's in it |
|---|---|
| `src/MphRead` | MphRead's game logic, formats and renderer (upstream, with small hooks for Second Hunt) |
| `src/MphRecomp.Core` | Second Hunt's shared code: menus, settings, input, multiplayer, mods |
| `src/MphRecomp.App` | The app itself, shared by both platforms: front end, campaign, ship, map, cutscenes, matches, music and sound, drawn through one GLES 3 binding |
| `src/MphRead.Android` | The Android shell: activities, AudioTrack output, gyro, touch overlay, lobby, ROM setup |
| `src/SecondHunt.Desktop` | The Windows shell: window and GL contexts, keyboard, mouse and controllers, sound device, ROM setup, lobby, credits |
| `src/MphRead.Tools` | MphRead's desktop viewer, plus Second Hunt's PC-side test harnesses |
| `src/NcsfPlay` | The DS music player |

## Credits

Second Hunt is built on **MphRead** by NoneGiven (MIT, see `LICENSE` and `NOTICE-fork.md`). Third-party libraries and
their licenses are listed in `THIRD_PARTY_NOTICES.md`, which the app also shows under RECOMP SETTINGS > credits.

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

(Note from Second Hunt: MphRead's VX movie decoder, and the sequencer in the NCSF-based music player, have since been
replaced with clean-room code that gives the same pictures and sound. See [docs/cleanroom](docs/cleanroom).)

### Special Thanks

This project's reverse engineering effort was developed parallel to **[hackyourlife's mph-viewer](https://github.com/hackyourlife/mph-viewer)**, a model viewer implementation in C. Major features such as the transparency rendering implementation were derived from its source code.

(Note from Second Hunt: that transparency rendering code has since been replaced with a clean-room version that draws
the same result. See [docs/cleanroom](docs/cleanroom).)

## About MphRead (upstream)

MphRead is a reverse engineering and game recreation effort comprising a model viewer, scene renderer, and general
parser for file formats used in Metroid Prime Hunters, with documentation of game features in its
[wiki](https://github.com/NoneGiven/MphRead/wiki). See the upstream repository for its desktop releases and setup
guide.
