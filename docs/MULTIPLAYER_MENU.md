# Vanilla multiplayer menu ("P-MP"): scope (2026-10-07)

Status: owner GO 2026-10-07 08:40. **M0 done, M1 built (09:14); decision 1 = A (owner 10:30): doors HUNTER LICENSE /
LOCAL PLAY / ONLINE PLAY; M2 RESULTS and M4 REPLAYS built, all on the Odin 11:10** (not yet run on a device). **M3 LAN
built, on the Odin 12:06** (PC two-process runs pass; needs two devices to try). **Join by address (decision 6, owner
12:40: the ROM's keypad) built 13:00** (PC runs pass), **on Android too since 13:26** (owner); the host lobby shows its
address on every platform. **M5 Echoes arenas built 14:50** (PC: listed, named, sized, played to RESULTS; each arena's
picture rendered at import), **M6 nickname built 15:05** (profile + first-visit notice + the ROM's name keyboard behind
HUNTER LICENSE), both not installed. **M6 unlocks built 16:45** (decision 11: vanilla's rules, RECOMP SETTINGS >
UNLOCKS = ALL after a yes/no; PC `-mpmenu unlocks` 22/22). **M6 Hunter License card built 18:35** (decisions 13-15: the
card on page 60 as vanilla shows your own, vanilla's license counting, Wi-Fi = internet / wireless = LAN + bots, LAN
protocol 6 with rank exchange). **All of it on the Odin 18:42** (not yet run on the device). **Owner tasks 20:10 (GO):
the card's back fixed (rev 1) and the card as one combined 16:9 screen; RESULTS fixed on rev 1, 20:44** (see the card
paragraph). **Owner report 21:20 (board "Reports"), this thread's items, all on the Odin by 22:34:38:** R1 page 26's back
arrow (call 17 with no page, both revs) now leaves for the doors, `-mpmenu back` checks every page's arrow and B (rev 0 +
rev 1); R14 the doors' DS wireless icon draws over LOCAL PLAY as vanilla (MenuEngine: a widget's item depth adds to its z);
R9 the lobby: a bot's star bars are a ring in the data (hiding one shows the next), so the lobbies get them without
those links, and each seat's arrows are its own (they were found by call, which the bot's check also makes) -
`-mpmenu lobby`; R2 a LAN joiner's PLAY AGAIN goes back to the same host (36, then "please wait" until the host's lobby
takes it: `RejoinHost`), as vanilla - `-mpmenu lan Kanden [addr] again`; R3 pages 61 and 62 as one 16:9 screen each
(MenuLayout.Keypad / Keyboard, owner ~22:27); R4 explained (arenas imported before M5 had no picture; the owner's
re-import made them); R5 answered (a fresh profile: 3 hunters, 9 ROM arenas + 6 Echoes).
Old lobby screens (decision 7): retired after the owner has played M3 on two devices (owner 12:40).
Parent plans: `docs/REPLAY_MODE.md` (P-MP, "Menus" decision 5) and
`docs/MULTIPLAYER.md` ("the ROM's multiplayer menu pages stay with the UI session ... wired to this code later").

Sources, all from the ROM and our own tools (no owner screenshots):
- The menu graph `frontend/metroidhunters.bin`: `extract_out/frontend/menus.md` (Tools `-fedump`, USA rev 0) and the page
  renders `extract_out/frontend/page_<n>.png` (`-ferender`). USA rev 1 checked with the UI thread's `marmpages.py`
  (sound census kit, `handoffs/start-menu-ui-rebuild_2026-10-01-files/sound_census/scripts`).
- Overlay 0's per-page handler table (rev 1, `cbtab.py` on the table at 0x02149954), for which pages carry game code.
- Vanilla in BizHawk (USA rev 1, the kit's `mainmenu` state), driven by the UI thread's route player with its page
  log: main menu -> MULTIPLAYER -> multi-card -> create game -> Battle -> rules -> hunter -> lobby with 3 bots -> START
  GAME -> the match. Every step confirmed (section 10).

## Summary

- Vanilla's multiplayer starts on one page with three doors: SINGLE-CARD PLAY, MULTI-CARD PLAY, NINTENDO WI-FI
  CONNECTION, plus a RIVAL RADAR button. 37 of the ROM's 67 menu pages belong to multiplayer.
- **Everything the app plays today fits behind MULTI-CARD, on vanilla's own pages.** Vanilla plays bot matches there
  itself: CREATE GAME, pick the mode, the rules and arena, the hunter, then ADD BOT in the lobby and START GAME, on one
  DS with nobody else around. A LAN host is the same CREATE GAME path (other devices see it), and a LAN joiner picks it
  from the same page's AVAILABLE GAMES list. None of what the app plays today needs an invented screen.
- Every rule our matches support has a vanilla row, one for one (`MatchSettings` already holds them all).
- No counterpart today: SINGLE-CARD (can't be done: it sends the game to people who don't own it), NINTENDO WI-FI
  CONNECTION (our online play doesn't exist yet), RIVAL RADAR (a closed-DS feature). REPLAYS, the Echoes arenas and the
  Windows "join by address" field are ours, with no vanilla page.
- REPLAYS: a button built from the file screen's MOVIES pieces, on the CHOOSE MULTIPLAYER TYPE page. Until R3 (the
  replay list) it opens the WORK IN PROGRESS box.
- It lives in the shared front end (`MphRecomp.Core/Frontend`), so Android and Windows get it at once. The platform
  shells lose their lobby screens and keep two jobs: start the match screen with the chosen settings, and come back
  to the menu after it.

## 1. What vanilla has (ROM pages)

Page numbers are the menu graph's (same in rev 0 and rev 1). "Group" is the page's frame family: 6 = the logo pages
(like the main menu), 7 = multi-card setup (the two-box top screen), 8 = single-card setup, 9 = Hunter License, 10 =
results, 11 = keyboards.

| Page | Group | What it is | Doors / main controls |
|---|---|---|---|
| 25 | 6 | CHOOSE MULTIPLAYER TYPE | single-card (call 90), multi-card (91), Nintendo WFC (92), RIVAL RADAR (93), back. Arrival call 89 = the first-visit nickname notice |
| 26 | 7 | CREATE OR JOIN A GAME | CREATE GAME (94 -> 27), AVAILABLE GAMES list, 4 rows (95-98 -> 36) |
| 27 | 7 | Game mode | BATTLE (big), SURVIVAL, BOUNTY, DEFENDER, PRIME HUNTER, CAPTURE, NODES; focus shows the mode's description in the top-left box (DESCRIPTION) |
| 28-34 | 7 | Rules, one page per mode | ARENA n/N with arrows (113/114) + the arena's preview on the top screen; the mode's rows with arrows; ADVANCED (115) opens a box: show hunters on radar, damage level, available weapons, friendly fire; OK -> 35. Recomp (2026-10-10, `MultiplayerMenu.ExtraRules.cs`): + EXTRA PICKUPS / EXTRA CHARACTERS rows (copies of the FRIENDLY FIRE row, calls 2340/2341; rows at a 20 px pitch, AVAILABLE WEAPONS under them, the box OK moved right; Prime Hunter one row higher), persisted in extra_rules.json, top-screen ADVANCED gets a 5th line "extras: all / pickups / off" |
| 35 | 7 | SELECT HUNTER (host) | 7 hunters; a tap shows the big portrait + alt-form name, a tap on the portrait confirms; locked hunters dark ("...off" widgets) |
| 36 | 7 | SELECT HUNTER (joiner) | same |
| 37 | 7 | "please wait for all players to finish downloading" | |
| 38 | 7 | Host lobby | 4 seats: hunter arrows, team red/blue; ADD BOT (+) fills a seat with a bot named after its hunter (SAMBOT, KANBOT, ...); each bot has level arrows around a check, and the check confirms it (its level then shows as 1-3 stars); START GAME appears once every bot is confirmed; remove bot; "illegal teams" |
| 39 | 7 | Joiner lobby | "please wait for all players to be ready." |
| 40 | 6 | Single-card info | OK -> 42 |
| 41 | 6 | Rival radar info | "tap the check box to activate the rival radar, then close your DS" |
| 42-46 | 8 | Single-card setup | Battle rules, hunter, LOCK PLAYERS (wait for downloads), lobby, downloading |
| 47 | 10 | RESULTS (local) | ranks, names, stats (L/R pages more stats), PLAY AGAIN / QUIT |
| 48 | 10 | RESULTS (Wi-Fi) | + ADD RIVAL |
| 52 | 6 | NINTENDO WI-FI CONNECTION | FIND GAME, FRIENDS AND RIVALS, CONFIGURE WI-FI, EDIT FRIENDS AND RIVALS |
| 53-55 | 6/7 | Find game | SET SEARCH FILTER (region, rank), SEARCHING FOR PLAYERS, VOTE FOR AN ARENA |
| 56 | 7 | Available games (friends and rivals) | host list, CREATE GAME, view a license |
| 57, 60 | 9 | Hunter License + friends/rivals roster | license card (rank, stats, hunter of choice), 60-slot roster, lock/delete |
| 58 | 7 | Wi-Fi lobby | + SEND MESSAGE (friends only), "not ready" |
| 59 | 7 | MAKE THIS GAME AVAILABLE TO: friends / rivals | |
| 61 | 5 | ENTER NEW CODE (friend code keypad) | |
| 62-64 | 11 | Keyboards (US / EURO / JAP) | chat message, a friend's temporary name, "enter the new name you wish to use" (the nickname) |

Not multiplayer: 49-50 (adventure end stats), 51 (RECORDS), 65-66 (empty).

**USA rev 1 vs rev 0:** the same pages, items and callback ids, except that the logo pages (25, 40, 41, 52-54) get the
rev 1 "TM" text, which shifts their later item numbers by two (as on every logo page). Our usual rule holds: never
index ROM items or strings by number; find them by widget name, callback or text position.

**Music:** IDS page table: 43 on every multiplayer page, 6 on results.

## 2. How vanilla flows

```
main menu (18) --MULTIPLAYER--> [first visit: "your nickname is X. you can change it in the edit friends and rivals menu." OK]
  25 CHOOSE MULTIPLAYER TYPE
   |- SINGLE-CARD -> 40 info -> 42 rules (Battle only) -> 43 hunter -> 44 lock players -> 45 lobby -> 46 downloading
   |- MULTI-CARD  -> 26 CREATE OR JOIN
   |                 |- CREATE GAME -> 27 mode -> 28..34 rules + arena (+ ADVANCED box) -> 35 hunter -> 38 lobby
   |                 |    -> confirm each bot -> START GAME -> 15 / 17 (loading) -> match
   |                 '- a game in AVAILABLE GAMES -> 36 hunter -> 39 lobby (wait for the host) -> match
   |- NINTENDO WFC -> 52 -> FIND GAME 53 -> 54 -> 55 vote -> 58 lobby
   |                      FRIENDS AND RIVALS 56 -> CREATE 59 -> 27 ... -> 58 lobby | join a host -> ...
   |                      EDIT FRIENDS AND RIVALS 60 (license, roster) -> 61 code / 62 name
   '- RIVAL RADAR -> 41
match -> 47 RESULTS (48 on Wi-Fi) -> PLAY AGAIN / QUIT
```

The group 7 pages share a frame: a top screen with SETTINGS (DESCRIPTION on the mode page) + ADVANCED on the left and
GAME MODE + ARENA + PLAYERS on the right, filled in as choices are made, with the arena's preview behind; tabs MODE /
ARENA / HUNTER across the touch screen's top that light up as the setup advances and jump back when tapped (calls 0-2);
and wireless signal bars at the top left.

## 3. How ours maps onto it

| Today (our lobby, `MatchLobby.cs`) | Vanilla page |
|---|---|
| "MULTIPLAYER" on the main menu opens our own screen (`FrontendRequestKind.Multiplayer`) | page 25, inside the game's menus |
| Match against bots on this device | MULTI-CARD -> CREATE GAME -> 27 -> 28-34 -> 35 -> 38: ADD BOT, START GAME |
| Mode (7 modes + team variants) | page 27 + the TEAM PLAY row (our `BattleTeams` etc. = the mode with TEAM PLAY on) |
| Arena (retail, filtered by the mode's entity census) | ARENA n/N on pages 28-34, the stage preview widgets (`stage/mp1..mp14`, `ad1`, `ctf1`, ... in the ROM) |
| Play as | page 35 |
| Bots 1-3, one bot level for all | page 38: ADD BOT / remove, a level and a hunter per bot (`MatchPlayer.BotLevel` and `Hunter` are already per seat) |
| Time limit (mode default / 10 / 5 / 3 / 1 min) | the mode's TIME LIMIT row, vanilla's own values |
| Point goal, lives, time goal, octolith reset, team play, damage, friendly fire, radar, weapons | `MatchSettings` has every one (`PointGoal`, `TimeGoalSeconds`, `OctolithReset`, `DamageLevel`, `FriendlyFire`, `RadarPlayers`, `AffinityWeapons`); today's lobby doesn't offer them, vanilla's rows and ADVANCED box do |
| Host a LAN match | the same CREATE GAME path; the host's `LanSession` starts beaconing; a joiner appears in the page 38 grid |
| Join a LAN match | page 26 AVAILABLE GAMES = the `LanSession` beacons -> 36 hunter -> 39 |
| Results panel inside the match screen (`MatchSession`) | page 47 RESULTS, PLAY AGAIN / QUIT |
| Gun = "original" | nothing to show (fixed) |

**Today's LAN limits stay until the netcode grows (N2):** host + one joiner, Battle, no bots alongside a joiner. The
vanilla lobby allows 4 seats and bots, so until N2 the lobby refuses what can't run yet, in the game's own message box.

## 4. What has no vanilla counterpart

| Thing | Proposal |
|---|---|
| SINGLE-CARD PLAY | Can't be done (`MULTIPLAYER.md` section 1). Keep the door (the page looks as vanilla), a press says so in the game's message box |
| NINTENDO WI-FI CONNECTION | Our online play (phase 4, needs the hosting decision). WORK IN PROGRESS box until then. Its pages map well onto the planned friend system later (FIND GAME, friends' games, roster, license, codes) |
| RIVAL RADAR | A closed-DS sleep feature. Nothing planned (`MULTIPLAYER.md` section 5 has a Bluetooth "meetup" idea). See REPLAYS |
| Voice chat ("hold X to talk"), CONFIGURE WI-FI, download pages 37/46 | Hidden or never reached |
| Wireless signal bars | Shown for a LAN game from the link's packet loss, or hidden. Small |
| REPLAYS | Ours (section 5) |
| Echoes arenas | Listed after the retail arenas, before RANDOM, when imported (owner 14:20). Name from our arena data, "map size" from its room entry (large); the picture is rendered from the player's own Echoes files at import (owner 14:35: "have the game render the maps and take screenshots as part of the import process"; Echoes itself has no arena pictures, only map holograms) |
| Windows "join by address" (typed IP, for networks that block broadcasts) | AVAILABLE GAMES' last row, "enter address", typed on the game's own number pad (page 61's keys, plus a "." key); built M3 |
| Nickname default | Vanilla takes the DS owner's name (the run shows "MELONDS"). The phone's or PC's user name would broadcast a real name over the LAN, so the default is a fixed name the player can change |

## 5. REPLAYS

Vanilla's page 25 has RIVAL RADAR in the bottom-right corner, the same spot MOVIES has on the file screen (page 24).
Options:
- **A (recommended):** REPLAYS takes RIVAL RADAR's corner, built like MOVIES: the film-reel icon (`main menu/movies`),
  the small highlight, and "replays" in the game's font. RIVAL RADAR is hidden (it has no counterpart).
- **B:** keep RIVAL RADAR (WORK IN PROGRESS box) and put REPLAYS in a row above it, the way COPY/DELETE sit above MOVIES
  (`MenuLayout.MainFamily(actionsAboveRight)`).

Until R3 a press shows WORK IN PROGRESS. R3's list page then plugs in behind the same button (Replay mode owns
`MphRecomp.Core/Replay/**`; this thread owns the button).

## 6. Screen shape

The logo pages (25, 40, 41) already use the main menu's combined 16:9 composition. The setup pages (group 7) are new to
our layout code: today `MenuLayout.For` would draw them as the two DS screens side by side. Owner decision 1 (2026-09-29)
asks for one combined screen per menu, so the proposal is a combined composition: the touch screen's controls in the
middle, the top screen's left boxes (SETTINGS/ADVANCED) on the left and its right boxes (GAME MODE/ARENA/PLAYERS) on the
right, the arena preview behind. Checked at 16:9, 20:9, 16:10, 4:3 and 21:9 (`-fecanvas`), like every other page.
Mock: `mock_16x9.png` in the kit (section 10).

## 7. Game code to rebuild (overlay 0)

The data gives every page's pieces, positions, animations, links and sounds. The game code fills in the rest, as it
did for file select and COPY/DELETE. By page, with the size of overlay 0's page handler (rev 1) as a rough guide:

| Area | Calls | Handler | Notes |
|---|---|---|---|
| Doors, nickname notice | 89-93 | 168 B | which page each door opens, the first-visit notice |
| Create / join | 94-98 | 368 B | the AVAILABLE GAMES rows |
| Mode | 99-106 | 388 B | sets the mode, the DESCRIPTION text on focus |
| Rules pages 28-34 | 107-131 | 476-580 B each | value lists per row (A/Y and the arrows step them), arena picker + preview, ADVANCED box |
| Hunter select | 132-145 | 484 / 348 B | locks, big portrait, alt-form name |
| Lobbies 38/39 | 146-184 | 936 / 700 B | seats, add/remove bot, bot hunter + level + confirm, teams + "illegal teams", ready states, START GAME |
| Results | 194-205 | 104 / 264 B | ranks, stats pages (L/R), PLAY AGAIN / QUIT (+ ADD RIVAL on 48) |
| Top-screen boxes | every group 7 page | | SETTINGS / ADVANCED / GAME MODE / ARENA / PLAYERS text, filled from the choices |

Method: the copy/delete method (`handoffs/start-menu-ui-rebuild_2026-10-04-files/copy_delete/notes.md`): read the
handler, check it in BizHawk with a route (page log + item states + shots), build, then compare our `-feflow` run with
vanilla's shots on the same route.

## 8. Milestones

Effort: S = about one session, M = 2-4 sessions.

**M0. Vanilla reference (S).** BizHawk routes with shots for: the bot-match path to START; each mode's rules page and
the ADVANCED box; hunter select; the lobby (add/remove bot, levels, teams, illegal teams); a short match to RESULTS
and both PLAY AGAIN and QUIT; back out of every page. Decode the value lists and lock rules from overlay 0.
*Accept:* every page in the bot-match path has a vanilla shot set and its handler's rules written down.
*Done 2026-10-07:* overlay 0 decoded (value tables, defaults, arena list + mode filter, map sizes, lobby calls, ADD BOT,
illegal teams, results' PLAY AGAIN -> 35 / QUIT -> 25) and BizHawk runs of every mode's rules page + ADVANCED box and
the lobby (kit `m0/notes.md`, `routes/m0_*`, `shots/m0_*`). Vanilla's back (B / the back arrow) on pages 27-38 asks
"this will end wireless communications. are you sure?" and YES goes to page 25; with the ADVANCED box up, B closes it.
Still open: bot names' source (we use the ROM's "sambot"... strings), PLAY AGAIN / QUIT not run in BizHawk.

**M1. Bot matches through the vanilla menu (M).** New `MphRecomp.Core/Frontend/MultiplayerMenu.cs` (callback ids read
per ROM, like `FileSelect`): pages 25 (MULTI-CARD works, the other doors answer in the message box), 26 (CREATE; the
list stays empty), 27, 28-34 + ADVANCED, 35, 38 with bots. The combined layout for group 7. START builds a
`MatchSettings` and asks the host to start the match (a new request kind); the match's end comes back to the menu.
Retail arenas only.
*Accept:* `-feflow` matches vanilla's shots on M0's routes; a bot match starts and returns on the Odin and on Windows.
*Built 2026-10-07:* `Core/Frontend/MultiplayerMenu.cs` (pages 25-38), `MatchSetupText.cs` (the setup line the shells pass
as `--es setup`), `MenuLayout.Setup` (the combined screen, checked at 16:9 / 20:9 / 16:10 / 4:3 / 21:9), hooks in
FrontendSession / GameActivity / DesktopApp / MatchSession (board "Cross-session edits"). After a match the menus open
at the lobby (`FrontendSession.ReturnPage()`; RESULTS with M2). PC checks: Tools `-mpmenu check` = every mode (and team
play) set up through the menu starts a headless match (12/12), and all 141 mode x arena pairs the game's grid offers
pass our census; `-mpmenu "<script>"` dumps a page's visible items. Not yet: every hunter and arena open (M6), nickname
"HUNTER" (M6), the doors (decision 1), Windows run by hand.

**M2. Results (S).** Page 47 after every match from `MatchStatus`, replacing the match screen's own results panel.
PLAY AGAIN and QUIT go where vanilla's go (M0).
*Built 2026-10-07 (on the Odin 11:06):* vanilla reference in the kit's `m2/` (BizHawk: a POINT GOAL 1 bot match to RESULTS,
every column scrolled, PLAY AGAIN -> 35, QUIT -> the end-wireless question -> 25; overlay 0's fill decoded: rows, team
colours and the empty row between teams, the medal by the local place, "1st place" / "tied for 2nd place", every stat's
source and format, the scroll and arrows; `m2/notes.md`). New `Core/Frontend/MatchResults.cs` (the match's results from
MphRead's GameState, kept process-wide); `MatchSession` (Multiplayer's) captures them for a menu match and goes straight
back to the menus, which open on RESULTS (`MultiplayerMenu.PendingReturn`; a match left without results returns to the
lobby). RESULTS uses the combined screen (medal left, table centre, mode / name / place right). Vanilla differences fixed
on the way: MphRead's "damage dealt" counted hits (now the damage sum, as the game); the engine ignored an item's link to
itself (RESULTS' win-condition box, the lobby's ADD BOT focus). Not rebuilt: the "saving to the game card" box (license
stats, M6). PC: Tools `-mpmenu results <mode> [teams] [quit]` plays a menu-set-up match to its real end and checks RESULTS
(all 7 modes + team play).
*Checked against vanilla 2026-10-07 11:45 (BizHawk, Battle with ONE bot, route `m2/routes/m2v_1bot.txt`, state
`states/mp_results_1bot.State`):* RESULTS music = seq 6 (RESULTS), the ROM page table's entry for page 47, which ours
already plays; the game-over jingle before it = seq 7 (TIMEOUT), already in MphRead's GameState. Fewer than 4 players:
the empty rows stay drawn, blank (as ours). A 2-player loser reads "2nd place" with the silver medal and no lightning
(G+0x250 places, G+4 player count = 2: the "local place < players - 1" rule holds). Team play: each row's points are that
player's own (the fill reads G+0x230 per player), as ours. Ours with `-mpmenu results battle bots=1` matches.

**M3. LAN in the menu (M).** CREATE GAME hosts (`LanSession` beacons), page 26 lists the games found, a joiner goes
36 -> 39, the host's lobby shows the joiner and START hands the session to the match. Today's LAN limits enforced
(section 3). The Windows address row (decision 6). Then the old lobby screens go (decision 7).
*Accept:* Odin hosts, Samsung joins, and the other way round; PC <-> Android; a full Battle both ways.
*Built 2026-10-07 (on the Odin 12:06):* the host lobby (38) is listed on the
network while it can take a player (today's limits: Battle, no team play, no bots; a host who adds a bot stops being
listed, ADD BOT with a joiner seated says why in the game's message box). CREATE OR JOIN (26) listens on entry; its
AVAILABLE GAMES rows (the red bars, calls 95-98) show up to 4 games by the host's nickname (lower case; two with the same
name get their address's last number). As vanilla's rows, a tap selects one and a second tap (or A) joins: the joiner's
SELECT HUNTER (36, the host page's calls 132-138) sends JOIN and opens the "please wait for all players to be ready." lobby
(39), which shows the host's game (top boxes, preview, seats) from the WELCOME. The host's lobby seats the joiner (ready,
its nickname); START GAME sends LanSession's new START (protocol 4) with the final match, and both go loading page -> fade ->
match (setup line `net=host|client`, launch args `--es net` + `--es menu 1`), then both RESULTS (MatchSession disposes the
session). A joiner's B / the host leaving: BYE -> the host's seat empties and the game is listed again / "the host left."
-> back to the list. Leaving these pages any other way ends the session. A joiner's PLAY AGAIN goes back to the list.
Android holds Wi-Fi's multicast lock while listening (`MphRead.Android/MenuLan.cs`, set in GameActivity).
PC: Tools `-mpmenu lan [hunter]` = host menus here + joiner menus in a second process, real UDP on this PC, a full Battle
(the joiner hunts), both RESULTS (3 runs pass; shots `extract_out/frontend/lan_*.png` with MP_SHOTS=1); `-mpmenu check`
12/12 and `-mpmenu results` unchanged.
*Join by address, built 2026-10-07 13:00 (decision 6; owner 12:40 chose
the ROM's keypad):* on every platform since 13:26 (`MultiplayerMenu.JoinByAddress`, was Windows only; owner 13:26: Android
too, the way in when a network blocks the hosts' beacons)
AVAILABLE GAMES' 4th row always reads "enter address" (the list shows up to 3 games) and opens ENTER NEW CODE (page 61, the
Wi-Fi friend-code keypad). The ROM's 4th row opens on one tap (its action has no second-tap flag, unlike rows 1-3).
Page 61 is ours as a copy at the same index (`MenuFile.ReplacePage`, so its music and button sounds stay the ROM's): back
goes to CREATE OR JOIN instead of the friend roster (60); the texts are new strings (the ROM's are shared with the other
Wi-Fi pages): ENTER ADDRESS, YOUR ADDRESS (the nickname + up to 3 of this device's IPv4 addresses, gateway networks
first), HOST ADDRESS (the typed address; a game heard there puts its name over it), and the prompt "enter the address of
the game you want to join. the host's lobby shows its address."; the Wi-Fi signal icons are hidden. Keys: calls 284-293
are the digits by position (the digit texts sit in the same rows and columns: 7 8 9 / 4 5 6 / 1 2 3 / 0), 283 delete,
282 OK (its check and highlight show once the address is whole); a "." key is added in the keypad's empty bottom-middle
spot (a copy of the "2" key, call 950, sounding like it). The address starts as the last one joined, else this network's
first three numbers ("192.168.1."); a number that can't take a digit (3 digits, over 255, a leading 0) gets its "." first.
OK joins as a row does (SELECT HUNTER, then the "please wait" lobby; the boxes stay empty until the host's WELCOME if its
beacon wasn't heard); a failed join ("no answer from 192.168.1.250:47770") comes back to the keypad. The host's lobby hint
adds "address: 192.168.1.20" while it's listed, on every platform. PC: `-mpmenu lan [hunter] addr` (the joiner types this
PC's address), `addr=127.0.0.1` (a game whose beacon isn't heard there) both play a full Battle to both RESULTS;
`-mpmenu lanjoin Kanden addr=192.168.1.250` = the no-answer path; the list path, `-mpmenu check` 12/12, arena grid 141/141
and `-mpmenu results` unchanged. Shots: kit `m3/addr/`.
*Still open:* retiring the old lobby screens (decision 7), after the owner has played M3 on two devices (owner 12:40).

**M4. REPLAYS button (S).** Decision 3; WORK IN PROGRESS until R3.
*Built 2026-10-07 (on the Odin 11:10):* page 25's RIVAL RADAR corner shows the file screen's MOVIES pieces (film-reel icon,
its small highlight, label "replays") with our description "watch replays of your recent matches."; the radar bar and
label are gone. The highlight takes the radar's item index, so its focus link and press (call 93) carry over; the press
opens WORK IN PROGRESS until Replay mode's R3 list plugs in behind call 93.

**M5. Echoes arenas in the arena list (S).** Name and stand-in preview, only when imported. Set
`EchoesArena.ImportRoot` (the folder holding recomp_settings.json, as IMPORT GAMES uses) before listing: in a fresh
process `MatchArenas.All` only sees in-app imports once it's set (Clean room queue handoff, 2026-10-07).
*Owner 14:20:* before RANDOM (RANDOM can pick them), name + map size as vanilla. *Owner 14:35:* the picture rendered by the
game at import (Echoes has no thumbnails, only collision-like map holograms).
*Built 2026-10-07 14:50 (PC):* `MultiplayerMenu.RefreshArenas` (on CREATE OR JOIN and on picking a mode) puts
`MatchArenas.All`'s imported rooms (ids past 118) before RANDOM with the modes their entity census allows (today battle,
prime hunter, survival: "1000011"), keeping the chosen arena; RANDOM's pick includes them. Name = the room's in-game name in
the ROM font, map size = its room entry (Echoes: large). FrontendSession sets `EchoesArena.ImportRoot ??=` the settings
folder (Start menu UI, at our request). The picture: NEW `Core/Arenas/ArenaThumbnail.cs`, called by the arenas import
(`EchoesArena.Import`, after the HD look) writes `thumb.png` beside the HD look: a CPU render of the HD mesh with the same
GX-TEV programs (GxTev) and inputs the device uses (lightmaps light the world, the sky around the camera, GX blending),
384x245 at 2x2 supersampling, 2-6 s per arena on the PC; the camera is searched (candidates raised over the spawns and
pickups with a clear line to the lens, plus a ring over the middle; scored on a 64x41 depth render by focus points seen,
reach, walls in front, back faces, empty frame, a 20-45 degree look down). Style "inside" (camera within the world's
bounds) by default; "from above" overviews are the other option (sheet: kit `m5_thumbs_compare.png`, owner to pick). The
rules pages draw it on the ROM's RANDOM picture's frame (texture added to the menu's cache: `MenuWidgets.Textures`, Start
menu UI). PC: `-mpmenu results battle arena=sidehopper_station bots=1` steps ARENA to 27/33 and plays it to RESULTS (shots
`arena_<name>.png`, `MP_SHOT_DELAY`); `-mpmenu thumbs [arena] [compare]` renders the pictures on the PC; `-mpmenu check`
12/12 + grid 141/141. Arenas imported before this have no picture (the frame shows RANDOM's) until imported again.
*Pipeline's picture, 2026-10-07 17:00 (cont. 5):* an arena of separate rooms (under a tenth of the spots seen from any
lens) also aims each lens at the middle of the spots it sees, without the downward-angle bonus; Pipeline 1/24 -> 4/24
(the room with the round platform; owner picked it from kit `m5b/pipeline_options.png`); the other five pictures are
byte-identical. `THUMB_DEBUG=1 THUMB_ALTS=n` writes the n best distinct lenses as `_alt<k>.png`. CR2 golden 7/7 after the
PC reference was regenerated (`-mpmenu thumbs`).
*Note:* the ARENA row's text pulses in the ROM's steady state, so a shot can catch it dim; vanilla arenas do the same.

**M6. Nickname, unlocks, license (S-M, per decisions 4-5).** The first-visit notice and the name keyboard (page 62);
locks from the campaign saves (board T12); license stats (T11) and pages 57/60 only if the owner wants them now.
*Built 2026-10-07 15:05 (PC), the nickname part (decision 10):* NEW `Core/Frontend/HunterLicense.cs`, the player profile
`hunter_license.json` beside recomp_settings.json (name, notice seen; room for rank, stats, hunter of choice, roster), set
through `MultiplayerMenu.ProfileDir` by FrontendSession (no folder = memory only, no notice: the PC tools). Arriving on
CHOOSE MULTIPLAYER TYPE (call 89) shows vanilla's box once per profile: ROM strings "your nickname is
" + the name (capitals)
+ "
you can change it in the hunter license." (the ROM's "the edit friends and rivals menu" replaced). HUNTER LICENSE (call
90) opens the ROM's keyboard page 62 in its "new name" use: prompt "enter the new name you wish to use for METROID PRIME
HUNTERS.", the name with a cursor in "newname here"; the chat / temporary-name texts, the EURO / Japanese switches
(keytoggle + calls 295/296) and the Wi-Fi icons hidden. Keys by call (US, rows in page order): 297-308 "1234567890-=",
309-318 "qwertyuiop", 319-327 "asdfghjkl", 328-337 "zxcvbnm,./", 338-341 "; ' [ ]"; SHIFT (345, one key) "!@#$%^&*()_+",
capitals, "<>?", ": ~ { }"; CAPS (342) letters only; 343 backspace, 346 space, 344 ENTER = OK (call 6), B / CANCEL (call 5)
leave it; both back to the doors. Vanilla's rules (rev 1 overlay 0, research subagent 2026-10-07: type-char 0x0211B91C caps
names at 10 characters, ENTER 0x02129784 refuses an empty or all-spaces name with "invalid name. please enter a name that
has characters other than just spaces.", the name kept as typed). PC: `-mpmenu name [text]` (fresh profile: notice once,
keyboard, the empty name refused, "Kanden~7" typed with SHIFT, OK, saved, no second notice; shots name_*.png).
*Not yet:* unlocks from the campaign saves (decision 4, board T12), license card + stats + roster (pages 57/60, T11).
*Join by address, 2026-10-07 (cont. 5):* the address OK'd on the keypad is kept in the profile (`LastAddress` in
hunter_license.json) and the keypad starts with it next time. PC: `-mpmenu lan Kanden addr` prints "profile: last address
... PASS kept" (the joiner on a fresh profile), full Battle to both RESULTS.
*Unlocks, vanilla's rules (US rev 1, read from the code 2026-10-07 by session cont. 5; scripts in the kit `m6/unlocks/`):*
- **Where:** cart-wide, not per file: the options save `cfg = 0x020E8318`, `+4` = 27 arena bits (bit = room id - 93),
  `+8` = 7 hunter bits (bit = hunter: Samus 0, Kanden 1, Trace 2, Sylux 3, Noxus 4, Spire 5, Weavel 6). Erasing a file
  doesn't lock anything again.
- **New cart** (arm9 0x0205C328, cfg reset): hunters `0x23` = **Samus, Kanden, Spire**; arenas `0x161295` = **9 arenas:**
  Data Shrine, Combat Hall, High Ground, Processor Core, Ice Hive, Sic Transit, Transfer Lock (both), Alinos Perch.
  (Matches the BizHawk kit's "ARENA 1/9" and 3 of 7 hunters, section 10.)
- **Hunters, Adventure** (arm9 0x02018EB8): defeating a hunter in Adventure sets its bit (with the story save's defeated
  bits +0x1087 and count +0x1088; a Guardian, hunter 7, counts in cfg+0xA0 instead).
- **Hunters, multiplayer** (arm9 0x02056424, after every match unless GameState+0xE bits 6-7 = 2): the first active
  player who placed below you (GameState+0x250 per player) with a locked hunter unlocks that hunter (one per match).
- **Arenas, Adventure** (arm9 0x02057000, Adventure mode, on entering a room): Alinos Gateway (room 27) -> arena Alinos
  Gateway (114); Celestial Gateway (45) -> 115; VDO Gateway (65) -> 116; Arcterra Gateway (77) -> 117. **Oubliette**
  (118) on the good ending only (ov1 0x021045C8, with the good-ending flag 0x020CBD90+0x74 bit 12).
- **Arenas, matches** (arm9 0x02056810): every match adds 1 to the license's local (+0x2C) or Wi-Fi (+0x24) match count
  (flag 0x020EB5B0 bit 0); when local + Wi-Fi is even, the next locked arena of this order opens: Sanctorus,
  Compression Chamber, Incubation Vault, Subterranean, Outer Reach, Harvester, Weapons Complex, Council Chamber, Elder
  Passage, Fuel Stack, Fault Line (table arm9 0x020CB098). **Head Shot** (99): a 4-player match (GameState+3 = 4).
  **Stasis Bunker** (119): any Wi-Fi match, or 40 local matches.
- **Menu:** overlay 0 0x02136C00 = arena open? (index 27 RANDOM always; else cfg+4 bit from the table at 0x02143D28);
  locked arenas are left out of the ARENA arrows (BizHawk "1/9"). 0x02136C8C = hunter open?; SELECT HUNTER draws a locked
  hunter's dark portrait (page 35 items 87-92, draw 0x02137478), the seat cycling (0x02123070) and the random pick
  (0x02134CDC) skip locked ones.
*Unlocks built 2026-10-07 16:45 (PC; decision 11):* `HunterLicense` holds vanilla's bits (`Arenas`, `Hunters`, a new
cart's defaults), `LocalMatches` / `OnlineMatches` and the rules (`FromStory`, `AfterMatch`, `GoodEnding`).
`MultiplayerMenu` (UNLOCKS section): CHOOSE MULTIPLAYER TYPE reads files A/B/C (`SavesDir`: hunters defeated, the four
gateway rooms); RESULTS counts each match once (`_lastWifi`: decision 15, an internet match); the good ending's
results page (49) opens Oubliette; `ArenaAllowed` drops locked arenas (RANDOM, imported always open); SELECT HUNTER
35/36 hides a locked hunter's icon + highlight and shows its "off" icon (found by widget and call 139-145); the lobby's
arrows and ADD BOT skip locked hunters. No profile (PC tools) or UNLOCKS = ALL: all open (matches still count).
RECOMP SETTINGS > UNLOCKS (`RecompSettings.UnlockEverything`, rule-2 rows in RecompMenus/RecompSettings/FrontendSession):
VANILLA -> ALL asks "open every multiplayer hunter and arena without earning them? ..." (the game's yes/no box), ALL ->
VANILLA at once. Vanilla shows no "unlocked" message (no such string in the menu text), so neither do we. Not done:
vanilla's GameState+0xE bits 6-7 = 2 skip (meaning unknown); hunters/arenas opened in Adventure count only once saved
(vanilla writes them as they happen); Oubliette from good endings before this build isn't recorded in our saves.
PC: `-mpmenu unlocks` 22/22 (new cart + file A: 16 Battle arenas, dark icons Noxus/Sylux/Weavel, bots open, the
after-match rules, NO / YES / back, Oubliette); `check` 12/12 + 141/141, `results battle` (counted, Head Shot), `name`,
`lan Kanden addr` pass; Desktop builds. (Decision 15 later made LAN a wireless match: Stasis Bunker after 40 or online.)

*Hunter License card built 2026-10-07 18:35 (cont. 6; decisions 13-15), on the Odin 18:42:* vanilla shows your own card
on EDIT FRIENDS AND RIVALS (page 60; BizHawk US rev 1: NINTENDO WFC -> EDIT FRIENDS AND RIVALS, references with poked
test licenses A and B in kit `m6/license/vanilla/`, RE in `m6/license/re_report.md` + d1-d3). HUNTER LICENSE opens a
recomp copy of page 60 (`MultiplayerMenu` HUNTER LICENSE CARD section: back -> the doors, filled texts on new strings).
Top screen (ov0 0x02119EC0 / 0x0211A3C8): name; rank level 0-4 = bounty / super / elite / master / legendary hunter with
red / bronze / silver / gold / black stars (legendary: gold header, black name); ranking points and the next level's
threshold ("--" at legendary); five tabs on L / R (the card opens on the first every time; tab 1's outline is the card's,
tabs 2-5 Selected recolour it): wi-fi win record / wireless win record ("wins of matches") / connection history; win
ratio / win streak (both buckets' bests added) / lucky arena; favorite weapon / headshot kills / favorite mode; biped kills
/ alt-form kills / kill streak (bests added); wi-fi / wireless / total play time ("%dh %02dm %02ds", "-- -- --" at 0);
"-" before any match. Hunter of choice portrait; medal by total wins (25 bronze, 100 silver, 200 gold); frame at 500
matches (gold when legendary); the alimbic swirl once the good ending was seen, the octolith at 100%. Touch screen
(decision 14): "you are:" + name; a tap on the name shows EDIT NAME (vanilla), which opens the name keyboard (62,
starting empty as vanilla's) and comes back to the card; the roster (friends / rivals panels, add friend, delete) hidden.
*Page relayout (owner 2026-10-08 ~00:42: "center everything ... center the license, reduce the size of the ranking points
and next rank info area ... put the edit name button in the corner in the right opposite the back button", "completely
eliminating the you are part"):* `MenuLayout.License` centres the card (1.1); back and EDIT NAME sit in the bottom
corners as the main menus' footer (0.9, 10 menu px in); `BindCard` hides "you are:", the name, its panel and the
touch-screen backdrop, shows EDIT NAME from the start, and narrows the ranking box (x 0..256 -> 48..208, words moved with
its right half) and lowers its top (y 38 -> 27). Supersedes the column layout below. `-mpmenu license` checks it; its
shots take MP_SIZE (4:3 fits too). On the Odin 2026-10-08 (install log).
*Floating keyboard + tab taps (owner 2026-10-08 ~01:05: "when you hit edit name theres some screen that is behind the
keyboard can we just get the keyboard floating over the license", "its not just the black pannel theres somtehing else
behind the black pannel", "can the different tabs on the license be tappable to switch straight to it"):* the screen
behind the keys was two ROM pieces of page 62, `keyboard\keyboardmask` (a black panel and a grid of tiles over both DS
screens) and `main menu\blackdrop` (an orange-framed see-through panel). Opened from the card (`KeyboardOverCard`),
neither is drawn (`ReplacePreview`) and `FrontendSession` draws the card as it was when EDIT NAME was pressed, dimmed 50%
as under the game's box, behind the keyboard (`TakeCardSnapshot` / `KeepCard` / `DrawCard`). EDIT NAME now calls 281 itself
(vanilla's press wipes the mask in over the card first). Each of the five tabs takes a tap (call 951, Field17 = the tab;
strips 25 DS px wide from x 38, y 53..72 under the card); they stay out of the D-pad's way (L / R as vanilla). Owner
01:4x: "tab taps are good".
*No blink between the card and its keyboard (owner 2026-10-08 ~01:20: "the license still disappears for a moment like
youre paging away both when you click edit name and then go back"):* no page exit or entry animation either way. EDIT
NAME switches to page 62 on the next tick (`MultiplayerMenu.EnterNext` from `Tick`, not inside the press, whose key would
go on to the new page's own actions) with `MenuEngine.Enter`; the keyboard's own pieces come in over the card, whose dim
fades in over 8 ticks. OK / ENTER, CANCEL and B (`LeaveName`) enter page 60 the same way, then `MenuEngine.Settle()` (new:
every item straight to the end of its way in) shows the card whole at once, on the tab it was left on (`_cardKeepTab`;
opened from the doors it still starts on the first tab, as vanilla). Opened from the doors, the keyboard leaves with the
usual page change. `-mpmenu license` checks: keyboard up at page frame 1; CANCEL, B and OK each back on a settled card at
frame 1 on the same tab; OK shows the typed name.
*The keyboard for other screens (owner 2026-10-08: "go ahead with that hook"; Replay mode's RENAME):*
`MultiplayerMenu.EditText(menu, start, max, returnPage, done)` opens page 62 with `start` typed (cut to `max`; typing stops
there, no cursor when full), the prompt "enter the new name / you wish to use." (the ROM's words without "for METROID
PRIME HUNTERS.", kept on two lines: one line would sit above the layout's band and not show). OK refuses a blank name as
for the nickname, else `done(text.Trim())`; CANCEL and B `done(null)`; both after `GoTo(returnPage)`, the usual page
change. The nickname is untouched, and EDIT NAME and the doors clear a RENAME left some other way. 20 of the widest
letters (m, w) fill the name box edge to edge, so Replay's 20-letter titles fit. `-mpmenu name` runs these checks
("PASS rename"; MP_SHOTS=1: rename_wide.png, rename_title.png).
*Owner tasks 2026-10-07 20:10:* (1) back did nothing on the devices (owner 20:05): on rev 1 page 60's B carries no page
(rev 0's goes to 52, which the copy retargets) and the back arrow never does (both revs): vanilla's code leaves on call
17. `OnCardCall` now takes 17 to the doors when the action has no page of its own. `-mpmenu license` checks B, the arrow
and B after L/R straight in from the doors (and the first-visit notice on the way); it passes on rev 0 and rev 1 (a rev 1
unpack through a scratch Tools copy; the shared Tools run rev 0). (2) The card as ONE 16:9 screen (owner: "I wanted a
16:9 screen"; `MenuLayout.License`, `MultiplayerMenu.IsCardPage`): the top screen at the setup pages' 1.1 on the right,
"you are:" level with the card's name, EDIT NAME under it and the back arrow bottom-left in a column on its left, the
backdrop over the canvas; the empty roster panel isn't drawn; side by side in RECOMP SETTINGS as before. Kit:
`handoffs/vanilla-multiplayer-menu_2026-10-07_owner-tasks-files/license_card_16x9_vs_old.png`. Still side by side: 61
(keypad) and 62 (keyboard) -- owner's call (RESULTS, 47, is already combined: `IsSetupPage`). (3) Found on the way: on
rev 1 RESULTS never bound (its name column moved from x 38 to 42; `BindResults` wanted 38), so every menu match came back
to the lobby and wasn't counted on the license. The name column is now "left of the first value column";
`-mpmenu results` prints FAIL when a match doesn't return to RESULTS. Rev 0 and rev 1 both: RESULTS + "match counted".
*Counting (`HunterLicense.AfterMatch`, vanilla arm9 0x020562A4; offsets in HunterLicense):* a win = first place
(0x020553FC; teams: the team's); win streaks per bucket; wins and matches per arena (lucky arena = the best wins * 4096 /
(matches + 1), starting from the arena just played, ties keep it); mode and hunter counts (favourites: the one just played
wins a tie); the kill code's live counts (arm9 0x02018460 / 0x02017D9C / 0x020183D4: biped vs alt-form kills, headshots,
beam kills, the kill streak which runs on across matches until a death or teamkill) through MphRead host hooks
(`PlayerEntity.HostLicenseKill` / `HostLicenseStreakEnd`, `GameState.HostLicenseTime` for the match clock); favourite
weapon = strictly most kills. Ranking points (0x02056928): against each other human not on your team (bots skipped:
0x84E = IsBot), table 0x020CB560 by both levels (a better place gains, worse loses, equal nothing), stops at 850; levels
at 40 / 140 / 390 / 750. Opponents' levels travel over LAN (protocol 6: JOIN + player list; `MatchPlayer.RankLevel`).
Total game time: the menus' time plus each match's (vanilla counts the whole game; Adventure time isn't counted yet).
PC: `-mpmenu license` (A and B: every tab's lines = vanilla's text, the pictures, EDIT NAME -> keyboard -> back, back ->
doors; the after-match rules incl. rank points), `results battle` with `MP_FIRE=1` (the hooks' tally = GameState's own
kills / headshots / beams / deaths), `lan Kanden` (+`addr`: both sides see the other's level, joiner +2 for a win), `name`,
`unlocks`, `check` 12/12 + 141/141. Looks judged full-size against vanilla (kit `m6/license/cmp_*.png`): the same except
the "hunter license" title, which vanilla draws letter-spaced (text format byte 0x04, also RESULTS' "PLAYER" and the
credits' "CONGRATULATIONS": not done by our menu text renderer), and "hunter of choice" sitting ~2 DS px lower.
*Not done:* Disconnects (connection history) until online play; vanilla's "doesn't count" result (GameState+0xE = 2:
a wireless disconnect) is counted by us; kills in a match quit from the pause menu aren't kept (vanilla writes them live).
**Later:** the Wi-Fi pages with online play (phase 4); RIVAL RADAR if a meetup feature is built.

**Later (owner 2026-10-07 ~23:10: "leave but document as an item to come back to later"): missing Echoes arena
pictures.** An Echoes arena imported before M5's picture step (built 2026-10-07 ~14:30-15:10) has no `thumb.png`, so its
ARENA box shows random's "?" until the player imports it again (the owner's Odin was fixed that way, re-import 22:09-22:13;
nothing tells a player that re-importing is the fix). Proposed: when the menu first needs an imported arena's picture and
there is none, render it in the background from the HD look already on the device (`ArenaThumbnail.Write`, as the import
and `-mpmenu thumbs` do), showing "?" until it is written; time the render on the PC and the Odin first to choose when it
runs (first use, entering multiplayer, or app start). MR1 (forced re-import) won't cover it on its own: existing installs
count as version 1 (decision D4), so a pre-picture import counts as current unless a later version bump forces a
re-import. Who's affected: anyone who imported Echoes arenas with a build from before that afternoon (the Samsung not
checked).

**Order:** M0 -> M1 -> M2 -> M4 -> M3 -> M5 -> M6. M1 to M2 is the first install worth judging. Can run alongside
Replay mode's R0-R2.

## 9. Owner decisions (asked 2026-10-07; answered 08:40, decision 1 at 10:30)

1. **The three doors: A (owner 10:30, after reopening them at 08:40).** HUNTER LICENSE (the single-card spot, page 52's
   friends-and-rivals icon; WORK IN PROGRESS until M6) / LOCAL PLAY (the multi-card spot -> page 26, vanilla's description
   "up to 4 players or bots can hunt each other in 7 unique game modes.") / ONLINE PLAY (the Wi-Fi spot; "hunt anyone in the
   world online."; WORK IN PROGRESS until online play). **Order changed (owner 2026-10-07 ~23:19, board R19): LOCAL PLAY /
   ONLINE PLAY / HUNTER LICENSE, left to right** (MultiplayerMenu.MoveDoors moves each door's pieces onto vanilla's
   single-card / multi-card / Wi-Fi spots). Owner ~23:30: the HUNTER LICENSE door's name and picture (page 52's FRIENDS AND
   RIVALS icon) will clash with ONLINE PLAY's own FRIENDS AND RIVALS once online play copies the Wi-Fi menu. **Picture
   decided (owner 2026-10-08 ~00:30, "g3 is good"): our own, drawn in code (`Core/Frontend/LicenseDoorPicture.cs`):
   three faceted stars, the big one in front and two touching it behind, lower and to the sides, darker, inside the
   FRIENDS AND RIVALS button's ring, the middle see-through, smooth edges; the borrowed button draws it instead of its own
   texture (MultiplayerMenu.UseLicenseDoorPicture), page 52 keeps vanilla's.** Mockups (A-G) in kit
   `handoffs/vanilla-multiplayer-menu_2026-10-08-files/door_candidates/` (Tools `-mpmenu icons`, `-mpmenu doorshot`). The
   door's NAME stays "hunter license" (owner 00:45); the door opens only your card (the roster stays hidden); the vanilla roster (RAM/save layout, BizHawk fake roster) is investigated with online play's FRIENDS AND RIVALS, not before. Labels and descriptions are ours, in the game's font. Section 9a
   keeps the options as proposed.
2. **Setup pages at 16:9: one combined screen (owner 08:40).**
3. **REPLAYS: in RIVAL RADAR's corner (owner 08:40),** option A (radar hidden).
4. **Unlocks: as vanilla, from the campaign saves (owner 08:40).**
5. **Nickname: as proposed (owner 08:40)**, "HUNTER" by default, never the device or OS user name, **plus room for a
   profile system built on, or close to, MPH's own** (the Hunter License: name, rank, stats, hunter of choice; the
   friends and rivals roster). So the nickname lives in a player profile from the start, not a loose setting.
6. **Windows "join by address": an extra row under AVAILABLE GAMES (owner 08:40),** typed on the ROM's ENTER NEW CODE
   keypad (owner 12:40). Built 13:00 (M3). **Owner 13:26: on Android too** (the fallback for the Odin + Samsung test when
   a network blocks the beacons; the list then shows up to 3 games + the row).
7. **Old lobby screens removed when M3 lands (owner 08:40),** once the owner has played M3 on two devices (owner 12:40);
   the developer menu keeps its own multiplayer entries.
8. **Results: vanilla's RESULTS page after every match (owner 08:40).**
9. **Echoes arenas (M5, owner 14:20):** before RANDOM in the list (RANDOM can pick them); the box shows name + map size as
   vanilla. **Pictures (14:35):** rendered by the game at import ("have the game render the maps and take screenshots as
   part of the import process"); **style B, the camera inside the arena (14:55)** (kit `m5_thumbs_compare.png`).
10. **M6 (owner 14:55):** the HUNTER LICENSE door opens the name keyboard for now (the license card and roster later: 13); the
    first-visit notice points at the hunter license; US keys only.
11. **Unlocks (owner ~16:20):** vanilla's rules (section 8 M6), the campaign's files counting on first visit; imported
    Echoes arenas always open; LAN and online matches count as vanilla's Wi-Fi. Then (owner, same minutes): a RECOMP
    SETTINGS toggle, default vanilla, the other setting opens everything, with a confirmation before it does. (LAN as
    Wi-Fi: changed by decision 15.)
12. **Pipeline's picture (owner 17:00):** option 2 (the room fallback of the camera search).
13. **Hunter License (owner 17:15):** the card now, the roster later.
14. **The card (owner ~17:25; 2026-10-08: no "you are:", EDIT NAME always shown, bottom-right):** the touch screen shows only the player's own part ("you are:", EDIT NAME), the roster
    hidden until it's built; ranking points as vanilla, from human opponents only (bots never change the rank; a person
    on LAN does, as a person over local wireless did in vanilla); the tab texts stay vanilla's words.
15. **Wi-Fi vs wireless (owner ~17:30):** Wi-Fi = internet battles, wireless = everything else: LAN battles with people
    and bot matches on one device (vanilla counts bot matches in its non-Wi-Fi bucket too: BizHawk RAM, local matches
    0 -> 1 and play time after a bots-only match; a first place counts as a win). A LAN match is an internet one when the
    other device's address is public. The unlocks follow the same split (Stasis Bunker: an internet match or 40 others).

### 9a. Doors proposal (decision 1)

Our plans (`MULTIPLAYER.md`): bots on this device (built); same Wi-Fi host/join (works, grows with N2); Wi-Fi Direct /
hotspot (N5, still "nearby"); online through our own small server, friend codes, presence, friends' games, relay
(phase 4, hosting decision pending); strangers matchmaking only if enough people play; the friend system's no-server
part (Hunter License, friends and rivals from local matches, the 60-slot roster). Plus REPLAYS (decision 3) and the
profile (decision 5).

Vanilla page 26 (CREATE GAME + AVAILABLE GAMES) already is host + join on one page. Two ways to cut the doors:

- **A (recommended): by where the players are, plus the profile.** Same three door spots, icons and highlight; the
  labels and focus descriptions become ours, in the game's font.
  - **LOCAL PLAY** (multi-card icon) -> page 26 as vanilla: CREATE GAME = host (bots, and anyone on this network can
    join), AVAILABLE GAMES = join (+ the Windows address row). Later Wi-Fi Direct / hotspot games show up in the
    same list.
  - **ONLINE PLAY** (Wi-Fi icon, without the Nintendo name) -> page 52's pages when phase 4 exists (FIND GAME, FRIENDS
    AND RIVALS). WORK IN PROGRESS until then.
  - **HUNTER LICENSE** (the single-card spot) -> the profile: license card (name, rank, stats, hunter of choice),
    friends and rivals roster, the name keyboard (pages 57/60/62). Works offline, before online exists. Until M6:
    WORK IN PROGRESS.
- **B (the owner's idea): HOST / JOIN / ONLINE.** HOST goes straight to the mode page (27), JOIN opens page 26 showing
  only the list, ONLINE opens page 52, and the profile sits inside ONLINE (where vanilla keeps EDIT FRIENDS AND
  RIVALS). One tap fewer to a bot match. Costs: the profile hides behind a door that stays WORK IN PROGRESS until
  there's a server; vanilla's page 26 is cut in two; once online exists, hosting for friends lives in two places (HOST
  and ONLINE > FRIENDS AND RIVALS > CREATE) unless HOST gains vanilla's "make this game available to" page (59).

Either way SINGLE-CARD and RIVAL RADAR go (no counterpart), and REPLAYS takes the radar's corner.

## 10. Evidence

Everything below is git-ignored (`handoffs/`, `extract_out/`): game frames never go in the repo.

- ROM page data: `extract_out/frontend/menus.md` pages 25-64, renders `extract_out/frontend/page_<n>.png`; compact
  summaries in the kit (`rom_pages_summary_rev0.txt`, `rom_pages_rev1_marmpages.txt`).
- Kit `handoffs/vanilla-multiplayer-menu_2026-10-07-files/`: `vanilla_flow.png` (one frame per step), `mock_16x9.png`
  (decision 2, cut from vanilla frames), `routes/` (route files + vanilla page logs), `shots/`. Runner: the UI thread's
  `sound_census/vanilla/run.ps1` on a private BizHawk copy. Savestates made on the way (`mp_type` = page 25 settled,
  `mp_lobby` = host lobby with no bots) live in that private copy; rebuild them with the routes.
- What the run showed (kit save, USA rev 1):
  - First press of MULTIPLAYER: "your nickname is MELONDS. you can change it in the edit friends and rivals menu."
    (the emulator's firmware name), one check at touch (128,122). Not shown again.
  - Page loads: state 0 (loading) ~110-135 frames, then the items come in, as on the other menus.
  - Battle defaults: POINT GOAL 7, TIME LIMIT 7 (minutes: the match clock starts at 7:00), TEAM PLAY OFF; ADVANCED
    RADAR OFF, DAMAGE MED, F.F. OFF, DEFAULT WEAPONS. ARENA 1/9 (Combat Hall first, MAP SIZE: SMALL): this save has 9
    arenas for Battle. 3 of 7 hunters unlocked. So vanilla's locks are live (decision 4).
  - The top screen fills as you go: GAME MODE on picking the mode, SETTINGS/ADVANCED/ARENA on the rules page, PLAYERS
    lists every seat (MELONDS, SAMBOT, KANBOT, SPIBOT). The MODE / ARENA / HUNTER tabs light up step by step.
  - START GAME: pages 15 and 17 (the same loading pages the adventure uses), music stops (fade 20), then the arena
    (room 95) with "LAWS OF BATTLE ... PRESS FIRE TO BEGIN".

## 11. Files and owners

| File | Owner | Change |
|---|---|---|
| NEW `MphRecomp.Core/Frontend/MultiplayerMenu.cs` | this thread | the pages' game code |
| NEW `MphRecomp.Core/Frontend/MatchResults.cs`, `MatchSetupText.cs` | this thread | the match's results for RESULTS; the setup line the shells pass |
| `FrontendSession.cs`, `MenuLayout.cs`, `MenuEngine.cs`, `MenuFile.cs`, `MenuPopup.cs` | Start menu UI | hand the multiplayer pages to MultiplayerMenu, the group 7 layout, a StartMatch request, `MenuFile.ReplacePage` for the keypad (board rule 2) |
| `MatchSession.cs`, `MatchLan.cs`, `MatchLobby.cs`, `MatchLobbyActivity.cs` | Multiplayer | results via the menu, LAN session handover, retire the lobby (rule 2) |
| `LanSession.cs` | Multiplayer | M3: START (protocol 4), PeerLeft, RomKeyFor (rule 2, logged 11:45) |
| NEW `MphRead.Android/MenuLan.cs` | this thread | Android's multicast lock for AVAILABLE GAMES |
| `GameActivity.cs` (Android), `DesktopApp.cs` + `LobbyScreen.cs` (Windows) | UI / Windows release | start a match from the request, come back to the menu (thin) |
| Tools `FrontendTool.cs` (+ own `Program.cs` line) | UI / own line | routes for the new pages |
