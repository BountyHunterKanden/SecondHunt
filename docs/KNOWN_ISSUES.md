<p><img src="images/hy-h2-known-issues-and-plans.png" width="100%" alt="Known issues and plans"></p>

- [Missing for now](#missing-for-now)
- [Multiplayer](#multiplayer)
- [Controls](#controls)
- [Looks and performance](#looks-and-performance)
- [Still being checked against the original](#still-being-checked-against-the-original)
- [Devices](#devices)
- [If it crashes](#if-it-crashes)
- [What's planned](#whats-planned)

Anything on the menus that isn't in yet shows a WORK IN PROGRESS box when you pick it.

## Missing for now

- Only USA ROMs (1.0 and 1.1) work.
- No rumble yet. The original supported the DS Rumble Pak, but controller and phone vibration aren't hooked up.
- Some save details aren't kept yet: the best-time records (the top three Gorea and total times), part of the game
  timer, a few stats and the artifact flags.
- The speaker setting on OPTIONS > AUDIO is saved, but SURROUND and HEADPHONES sound the same as STEREO for now.
- ERASE ALL DATA on OPTIONS isn't in yet.
- Samus's arm cannons from the Prime games aren't part of the import yet.

## Multiplayer

- Players on different versions can't play together, online or on LAN (the game says "other version"). Update when
  the main menu asks.
- After you quit a LAN match from the pause menu you can't rejoin it. Closing and reopening the app does.
- A replay of an online match that ended early may not seek instantly.
- The original's typed rules intro, with its sweeping camera, doesn't play at the start of a match yet.
- Bots still get stuck for a moment on some _Echoes_ arenas.
- The Trooper's wall cling doesn't hold on curved walls.
- The Pirate Blaster doesn't leave a mark on walls yet.

## Controls

- The DS showed a weapon bar on the bottom screen. It isn't drawn yet, so the only on-screen list of your weapons is
  the weapon ring (hold L2).
- The touch overlay has no buttons for stepping through weapons. Use the weapon button (hold and drag) instead.
- The volume and screenshot buttons may not work on the title and menu screens.

## Looks and performance

- Pop-ups like item pickups and the enter-ship prompt look rough and still need layout work.
- You may get short frame dips the first time a room or cutscene loads.
- The scan visor's box and icon aren't quite the right shape in widescreen.
- The HUD's energy and ammo bars don't flash and change color exactly like the original (low ammo, pickups).

## Still being checked against the original

These were reported and are being compared with the DS game.

- Looping or odd sounds in Piston Cave (Alinos). Fixed in the next update.
- Flickering lava texture in Processor Core
- The Fault Line shock barrier is invisible and can be walked through
- The Docking Bay floating platforms slide you backward
- Some skybox parts spin at different speeds
- Council Chamber: what the shot switch triggers, and whether the bounce pad should always be there
- The small red sniper targets (hit with the Imperialist) don't stay folded
- Frame drops in Transfer Lock
- Which sounds play when you arrive through a portal (the Sic Transit escape, Elder Passage)
- Feel: the Judicator's fire rate, the Imperialist's auto-zoom, Slench's hit box, and whether the Volt Driver's
  charged shot should distort your visor

## Devices

So far Second Hunt has mostly been tested on Snapdragon 8 Gen 2 hardware (AYN Odin 2 Portal, Galaxy S23) and one
Windows PC. Reports from other phones, handhelds and PCs are very welcome.

## If it crashes

Go to SECOND HUNT SETTINGS > APP > HELP and pick SHARE CRASH LOG (on Windows, OPEN CRASH LOG shows the folder). Attach
the report to a bug report on the [Issues](https://github.com/BountyHunterKanden/SecondHunt/issues) page, with your
device and what you were doing. It has no personal data in it. For a bad online match or anything else that isn't a
crash, SHARE LOG (SAVE LOG on Windows) on the same page sends the game's log.

## What's planned

### Next

- The multiplayer issues above: rejoining after quitting, the rules intro, bot pathing, the wall cling and the
  blaster's wall mark
- Step-through weapon buttons for touch
- A proper outline on hunters marked by Squad Mark
- Sound timing like the original: what plays while paused and during escapes, and how music and sounds stop or fade
  when you die, go back to the ship or finish a match
- The rest of the save data: the best-time records, the game timer, stats and artifact flags
- ERASE ALL DATA on OPTIONS
- Fixes for the issues above, each checked against the original

### Later

- Rumble on controllers and phones that have it
- The original's SURROUND and HEADPHONES speaker modes, and HQ music that keeps more of the DS's treble
- Unlocking the sound test the original way (after the true ending's results screen). For now it's always open.
- European, Japanese and Korean ROMs
- Dual-screen devices, with the DS's second screen on a handheld's second display
- More HD content from games you own: each Prime game's arm cannon and the other hunters
- The rest of [MphRead's to-do list](MPHREAD_TODO.md)
