# Logs

Second Hunt writes one complete log on every platform. There are no switches: everything is always written, and you
filter it when you read it.

## Where it is

| Platform | File | How to get it |
| --- | --- | --- |
| Windows / Linux | `logs\log.txt` in the game's data folder (`%LOCALAPPDATA%\Second Hunt\`, or `data\` beside the exe for a portable install). Older runs are `log.1.txt` (newest) to `log.4.txt` | SECOND HUNT SETTINGS > APP > HELP > SHARE LOG (SAVE) copies it to `Downloads\Second Hunt\second-hunt-log-<time>.txt` |
| Android | the app's own log file | SECOND HUNT SETTINGS > APP > HELP > SHARE LOG (Share, or Save to Downloads) |

A second copy of the game running at the same time on PC writes `logs\log-<pid>.txt` instead. A normal start deletes
those once they are older than a week.

## Line format

```
2026-11-17T21:40:12.345-05:00 I 12 MPHPlay[pickup,weapon,omega] P1 SAMUS (you) picked up Omega Cannon
```

The fields are the time (ISO, with offset), the level (`D` detail, `I` info, `W` warning, `E` error), the thread, the
tag, and the message.

The tag is an **area** (`MPHPlay`, `MPHAudio`, `MPHNet`, ...) followed by its **tags** in brackets. A line's own tags come
first, then its area's. Every area has a fixed set of area tags (`LogFormat.AreaTags`), so every line can be picked
out by topic. Android's logcat shows the area as the tag and puts `[tags]` at the start of the message.

## Gameplay lines (`MPHPlay`)

These are what happened in a match or the campaign, one line per game event (`PlayLog.cs`, from `GameEvents`).

| Event | Level | Tags | Example |
| --- | --- | --- | --- |
| match start | I | match, arena | `match start: Battle in MP3 PROVING GROUND (id 92), 4 players: P1 SAMUS (you), P2 TROOPER (bot), ...` |
| campaign start | I | campaign, room | `campaign start: SinglePlayer in Alinos Gateway (...)` |
| room | I | room | `room: ...` |
| spawn | I | spawn | `P2 KANDEN (bot) respawned` |
| pickup | I | pickup + weapon / health / ammo + the item | `P1 SAMUS (you) picked up Omega Cannon` = `[pickup,weapon,omega,omegacannon]` |
| weapon switch | I | weapon + the beam | `P1 ... switched to Shock Coil (from Power Beam)` |
| kill / death | I | death, kill, weapon + the beam | `P1 ... killed P3 NOXUS (bot) with Imperialist` |
| enemy killed | I | enemy, kill + the type | `P1 ... killed mp2 Grenchler (id 12)` = `[enemy,kill,mp2,grenchler]` |
| scan, door, artifact | I | scan / door / artifact | |
| octolith | I | octolith (+ score on a capture) | |
| match end | I | match | `match end: Battle` |
| damage | D | damage, weapon + the beam | `P3 ... took 30 damage from P1 ... with Battlehammer` |
| fire | D | weapon, fire (+ charged) + the beam | `P1 ... fired Magmaul (charged)` |
| bomb | D | weapon, bomb | |
| alt form | D | altform | |

Beam and item tags are the name in lowercase (`shockcoil`, `judicator`, `pirateblaster`). The Omega Cannon also gets
`omega`. The lines are not written while a replay plays back, because they would repeat the match it recorded.

## Sound lines (`MPHAudio`)

One detail line for every sound effect that starts (`SoundLog.cs`, from `GameAudio`'s sound setup). A held loop or a
quick repeat of a sound already playing is one line, not one per frame.

```
2026-11-17T22:51:50.122-06:00 D 2 MPHAudio[dgn,dgn_mortar_bounce,weapon,magmaul,sound]: started DGN_MORTAR_BOUNCE (dgn 43)
2026-11-17T22:51:56.187-06:00 D 2 MPHAudio[sample,spire_jump,hunter,spire,sound]: started SPIRE_JUMP (sample 434)
```

| Tag | When |
| --- | --- |
| `sample` / `dgn` / `script` | the kind of sound (one sample, a terrain/hit set, a scripted sequence) |
| the sound's name, lowercase | always (`mortar`, `beam_hit`, `player_spawn`) |
| `weapon` + the beam | a sound in the game's own weapon sound table (`powerbeam`, `magmaul`, `shockcoil`, ...); the Omega Cannon's also get `omega` |
| `hunter` + the hunter | a hunter's own sound (`trace`, `spire`). A sound several hunters share, like `player_spawn`, is just `hunter` |
| `enemy` | an enemy hurt or death sound |
| `self` | a sound from your own hunter (the message ends ", yours") |
| `menu` | a menu sound |

The weapon and hunter tags come from the game's sound tables, so every Magmaul sound carries `magmaul` whatever its
file is called. A bot match writes about 15 of these a second. Music, voice chat and movie sound are not in these
lines (they have their own `MPHMusic` / `MPHVoice` / `MPHMovie` lines). Like the gameplay lines, they are not
written while a replay plays back; menu sounds always are.

## Filtering

Lines with a tag (`omega`, `weapon`, ...):

```powershell
Select-String -Path log.txt -Pattern '\[[^\]]*\bomega\b'
```

```bash
grep -E '\[[^]]*\bomega\b' log.txt
```

`sound` or `weapon` but not `input`:

```powershell
Select-String -Path log.txt -Pattern '\[[^\]]*\b(sound|weapon)\b' | Where-Object { $_.Line -notmatch '\[[^\]]*\binput\b' }
```

```bash
grep -E '\[[^]]*\b(sound|weapon)\b' log.txt | grep -vE '\[[^]]*\binput\b'
```

One area only: `grep ' MPHPlay\[' log.txt`. Info and up only, without the detail lines: `grep -v ' D ' log.txt`.

## Area tags

The source of truth is `LogFormat.AreaTags` (`src/MphRecomp.App/Platform/LogFormat.cs`). In short: sound (MPHAudio,
MPHMusic, MPHVoice, MPHMovie), input (MPHInput, MPHPad, MPHTouch, MPHRumble), net (MPHNet, MPHOnline, MPHLobby,
MPHVoice, MPHSmooth, and the sim's network console lines), render, hud, replay, campaign, match, bot, weapon (MPHShot),
perf, menu, app, mods, editor, capture.
