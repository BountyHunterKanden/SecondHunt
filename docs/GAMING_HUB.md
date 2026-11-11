# Gaming Hub (in game)

The old nintendowifi.com MPH Gaming Hub (2006-2014), rebuilt inside SECOND HUNT's online menus. Only the MPH pages
come back, with Second Hunt's hunters, weapons, modes and arenas added, and it stays open to more. It's in game, not a
website.

## 1. What the site had, and what we keep

| Site page | In game |
|---|---|
| Hub: each board's leader, favourite hunter / weapon / mode in %, players online lately | Hub page (HubReq/HubMsg): leaders, favourites (hunters, weapons, modes, **arenas**), up to 20 players seen in the last 15 min, shuffled |
| 8 leaderboards, top 10, friends / rivals filter | LeadersReq/LeadersMsg: same 8 boards, top 10, scope all / friends / rivals, plus the asker's own place |
| Hunter License page: stats tab, favourites tab | StatsReq/StatsMsg: totals, places on each board, per-hunter / weapon / mode / arena counts |

The 8 boards, in the site's order (`HubCategory.All`): OVERALL POINTS, GAMES PLAYED, WIN RATE, CONSECUTIVE WINS,
CONSECUTIVE KILLS, ALT KILLS, BIPED KILLS, HEADSHOTS. Shots Fired is counted (the license's STATS tab, not a board).
Not counted: disconnects (owner 2026-11-07, confirmed 2026-11-11: leave connection rate alone for now). Ours, not the site's: the arenas list on the hub (the site only had an
arena on each license), the 10-game minimum for Win Rate, and the Overall Points formula (§3: built like Nintendo's,
not Nintendo's own, which can't be recovered).

## 2. Content keys (`Core/Online/ContentKeys.cs`)

Everything is counted by a stable lower-case string key, never an enum number, so a new hunter, weapon or arena needs no
protocol or server change:

- **hunter:** the picked character's name when it isn't a vanilla hunter (`trooper`, `dark_samus`...), else the hunter.
  Characters.cs ids that differ from the registry key map to it (`darksamus` -> `dark_samus`, `zerosuit` ->
  `zero_suit_samus`); `-hubselftest` checks every picked character lands on a known key.
- **weapon:** by BeamType index (`power_beam`..`omega_cannon`, 11 `pirate_blaster`, 12 `hyper_beam`, 13 `paralyzer`),
  plus `alt_form`. Beam 14+ shows up as `beam_14` until it gets a name.
- **mode:** HunterLicense.ModeIndex's 7 groups (a team mode counts with its solo mode).
- **arena:** the room name through `Key()` (`MP1 SANCTORUS` -> `mp1_sanctorus`).

A key the registry doesn't know still counts and shows in upper case with `_` as spaces. Adding it to the registry only
gives it a nicer name and its place in the lists. `Valid(key)` is what the server accepts.

## 3. Overall Points and the rank (star points)

Two numbers, both computed by the server (a client can't send its own):

- **Star points** (`HubCategory.RankPoints`, total `rank_points`, not a board): vanilla's Wi-Fi rank rule. `HubRules`
  copies HunterLicense's rank table (arm9 0x020CB4DC / 0x020CB560): 5 levels, thresholds 40/140/390/750/851, max 850.
  Against every opponent not on your team, a better place gains and a worse one loses, by the two players' levels before
  the match. They give the star and rank title on leaderboard rows (`LeaderRow.Rank`, 0-4, filled by the server) and on the
  license STATS band. `MphRead.Tools -hubselftest` checks the copy still matches HunterLicense.
- **Overall Points** (`HubCategory.Points`, the OVERALL POINTS board): `HubRules.Overall`, recomputed from the player's
  totals after every match: games x4 (cap 10,000) + wins x4 (cap 6,000) + kills / 10 (cap 4,000) + headshots / 20
  (cap 1,000) + win rate 0-10 (from 10 games; cap 10). At most 21,010.

Why (RE + the site archive, 2026-11-07): the US cart's Wi-Fi upload (23 DWC keys built at arm9 0x2063d4c from the license
copy at 0x20eade8) never sends the star points (+0x1C), so Nintendo's Overall Points were a server-side formula. On the
archived 2006-11-14 board, cheaters with ~1,000,000 in every stat all sat at 21,010 (+0..28): per-part caps. On 22
archived real licenses (rank only, no points) games played ordered the ranks best, then wins; kills and headshots
weakly. The weights above order all 46 same-month pairs of those licenses correctly and hit the same 21,010 ceiling.
Nintendo's exact weights can't be recovered (no archived page shows points for a non-cheater), so this is ours.

## 4. Trust: who reports

- An online match: only the host's `MatchResultReq` (OnlineMenus.Records.cs), the same report the head-to-head
  record uses. The report is counted once per match.
- A served match: only the match host's `!result` (SecondHunt.MatchHost ControlMode.cs, HubServed.cs).
- LAN (`Lan = true`) never counts. Vanilla's pages were Wi-Fi only.
- The server clamps: 4 h per match, 24 weapon keys, invalid keys dropped, headshots / alt kills / best streak never
  above kills (HubStats.Clean). Kills (and so every kill number) go by how long the server saw the match run, from its
  StartGame on the server's clock: 20 + 12 a minute, 999 at most (HubStats.MatchKillCap). An honest stomp tops out near a
  kill every 5 s; a doctored report gains only what that real time could give (an instant one: 20 kills, 3 Overall Points).
- WIN RATE (tenths of a percent) counts from 10 games. Ties share a board place.

What a seat sends (`ResultStats`, from `MatchResults.HubStats`): hunter, kills, deaths, headshots, alt kills, best kill
streak, kills per weapon. GameState counts the match's seconds and the extra beams' kills (ExtraBeamKills), and
PlayerEntity counts the streaks.

## 5. Server (0.10.0)

- `StoreStats.cs`: one `stats` table (code, cat, key, value). Cat `total` holds board numbers and card totals (wins, kills,
  deaths, seconds, win_run). `hunter` / `mode` / `arena` count matches per key, `weapon` counts kills. Row `*` counts
  matches.
- `HubStats.cs`: the three pages. Blocked players don't show to each other. "Online lately" lists only findable players.
- An older server drops a connection that sends these messages, so the client gates on `ServerAtLeast("0.10.0")`.
- Deleting a profile deletes its `stats` rows too (`Store.DeletePlayer`); the `*` match count stays.
- Live since 2026-11-07 (server binary only; the match host stayed the 0.4.4 release build, so served matches count
  games and wins but no kills, hunters, mode or arena until a release ships a match host that sends `Stats`).

## 6. Phases

1. **Done (2026-11-07):** protocol, keys, counting on the client, server tables and pages, tests.
2. **Done (2026-11-07):** the screens in the online menus (`Core/Frontend/OnlineMenus.Hub.cs`), shown only on a 0.10.0
   server. See §8.
3. **Done (2026-11-07):** Shots Fired (`GameState.ShotsFired`, ResultStats.Shots, the server's Shots total) and the
   bespoke layouts with ROM pictures (§8).
4. **Done (2026-11-07):** Overall Points as our capped activity total; star points kept apart for the stars (§3,
   owner: option 2). Rivals stays switched off (`ShowRivals = false`), not removed.
5. **Decided (owner 2026-11-11):** disconnects not counted for now; quitting costs no Overall Points (only the licence's vanilla rank loss); Overall Points option 2 kept (most 21,010). The screens are called HUNTER HUB (was GAMING HUB).

## 7. Tests

- `SecondHunt.Server --selftest` (or `SELFTEST_ONLY=hub`): 21 hub checks (SelfTestStats.cs) covering the rank rule,
  keys, leaders, shares, the card, clamping, win rate from 10 games, ties, friends / rivals, bad board and LAN. Plus the
  served check: the match host's result counts (SelfTestServed.cs).
- `MphRead.Tools -hubselftest`: HubRules equals HunterLicense, and every mode, beam, hunter and picked character has a
  known key.
- `MphRead.Tools -onlinemenu`: the hub screens from sample data (no server): 22 checks plus `online_hub_*` /
  `online_leaders_*` / `online_license_*` renders in `extract_out/online_menus/build`.
- **Dev builds, the hub filled without a server's data:** put an empty `sample_hub.txt` beside recomp_settings.json
  (like `fake_friends.txt`; never in a public build). GAMING HUB, LEADERBOARDS and any made-up hunter's license
  (CARD / STATS / FAVOURITES) then show `HubSample`'s data (`Core/Frontend/HubSample.cs`, the same data the PC preview
  uses), with "SAMUS" in it replaced by you. The made-up hunters' codes start `919191`; their licenses aren't asked of the
  server. Delete the file to go back to the real hub. The sample is consistent: each board's #1 is the hub's leader for
  it, and your places match your STATS (you are #1 on WIN RATE and 9th on HEADSHOTS).
- **MY LICENSE** (GAMING HUB, bottom right, under ONLINE LATELY): opens your own card on CARD, with STATS and FAVOURITES
  beside it (no online matches yet: they say so). Your own card has no HEAD TO HEAD panel or actions. Back returns to
  MY LICENSE.
- `-onlinemenu recolour=1`: the friends and recent players pick TROOPER (each skin) and DARK SAMUS, to see their
  cards' round icons: the look-alike recolour without `pictures=`, the head picture with it.
- `MphRead.Tools -onlinehubtest [server=ws://127.0.0.1:8080/v1/ws]`: live, against a **local** SecondHunt.Server (never
  the live one; its results would land on the real boards). Three PC hunters play ten matches, then the menus open
  GAMING HUB, OVERALL POINTS, WIN RATE, the winner's row -> license STATS and FAVOURITES; `online_live_*` renders. The
  PC hunters delete their profiles at the end. Start a server with
  `dotnet run --project SecondHunt.Server -- --db <scratch>/hub.db --urls http://127.0.0.1:18431`.

## 8. Screens (`Core/Frontend/OnlineMenus.Hub.cs`)

- **Entry:** a GAMING HUB button on the online hub page, only when `ServerAtLeast(HubReq.SinceServer)`.
- **GAMING HUB** (call 2400): the 8 boards' #1 (tap = that board, 2410+b), favourite hunters / weapons / modes / arenas
  with "% of matches" / "% of kills" and the #1's picture, ONLINE LATELY (up to 20; tap = their license, 2420+r; drag
  or D-pad scrolls), "players with stats" (profiles with a counted 0.10.0 match) and "matches played" (all counted).
  "see all >" (2418) opens LEADERBOARDS on ALL BOARDS.
- **LEADERBOARDS:** `<` board `>` (2440/2441, also L/R), ALL / FRIENDS (2446+s; RIVALS hidden while `ShowRivals` is
  off), top 10 with tied places shown as `2=`, your own place under the list when you're not in it (2460), a row opens
  that hunter's license (2450+r). Win rate shows one decimal and needs 10 games. The first entry of the board cycle is
  ALL BOARDS: eight cards with each board's top 5 and your place (tap = that board, 2480+c). Places 1-3 get the
  results medals (`results\medalgold/silver/bronze`); OVERALL POINTS rows show the rank star + title.
- **HUNTER LICENSE:** CARD / STATS / FAVOURITES tabs (2470+v, L/R) on your own card and on anyone's: STATS = totals +
  your place on each board, FAVOURITES = your four share lists. Names come from `ContentKeys.Display`.
  STATS: world rank (your place on OVERALL POINTS), the rank star (`gamerscard\<colour>star`) and title, 8 totals
  (time as `1d 18h`, shots fired), a biped / alt form kills bar, your place on each board. FAVOURITES: four panels,
  the #1 with its picture, % and "N of TOTAL", then #2 and #3.
- **Pictures:** hunters = the HUNTER LICENSE portraits (`gamerscard\<hunter>most`) in the arena pictures' orange
  frame. Pirate Trooper, Dark Samus and the Zero Suit show their rendered head picture instead (`HubCharacterPortrait`: the
  character's first imported skin's CharacterPictures head, the match HUD portrait's crop (HudPortraits.Zoom) at 192 px),
  black inside the frame until the device has drawn it; never a seat icon or a look-alike recolour (owner 2026-11-07).
  On a licence's FAVOURITES the hunter is drawn in that player's most played skin (owner 2026-11-07,
  `HunterLicense.SkinOf`; your own card reads `Multiplayer.License`, others the uploaded licence): Pirate Trooper, Dark Samus
  and the Zero Suit in that skin where this device has it (else the first imported one); Samus's suits and Sylux in
  Prime 4 as that skin's head picture only where this device has it drawn, else the vanilla licence portrait. The
  community HUB page has no per-skin counts, so it keeps the default look.
  Weapons = `hud_weaponicon` frames from Sylux's file (`localSylux`: the bare symbol in the beam's colour, no tile;
  owner's pick), Pirate Blaster its own. Modes = the mode models, arenas = the arena previews (`ArenaPicture`). No dark
  square behind the favourite pictures. The rank stars are redrawn sharp at run time (`HubSharpStar`): the ROM star's
  points, radii and colour are measured, then drawn as a clean star at 16x (the black star keeps its pale plate).
- **PC renders with the head pictures:** `-onlinemenu hd=<data>\hd pictures=<data>\cache\characters` (a device's or a
  capture's data folder; the pictures' stamps must match those `.dae` files). Give **absolute** paths: relative ones
  make `CharacterPictures.For` find nothing (black squares, Sylux in his vanilla portrait). The shots
  `online_license_favourites_skins*.png` show the most-played-skin case.
