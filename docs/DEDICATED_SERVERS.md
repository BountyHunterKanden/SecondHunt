# Dedicated match servers (O11)

Owner, 2026-10-16: friends said online "felt like subtle lag" and "the host may have an advantage". The owner asked
how Fruity Prime (another MphRead fork) does server-side matches, then: **"set ours up to do the same thing
essentially, or work more like a modern online server"**, **"accomplish the same thing without directly copying"**.
So this is our own design, written from our netcode. Fruity Prime only shows that a headless MphRead host on a small
Linux box is practical. None of its code or documents are used.

## 1. What changes for players

- **Online matches run on our server.** No player is the host, so nobody plays at zero ping and nobody's connection
  or phone decides the match. A player leaving, the lobby creator included, never ends or migrates the match.
- **The lobby creator still runs the lobby.** They are the lobby's *owner*: they pick the mode, arena and rules, and
  press START. Owning the lobby gives no gameplay privilege.
- **Shots feel instant (section 5).** Your own hits show the moment they land on your screen, and the server
  confirms them within its checks. A wider rewind window covers internet pings.
- **Results come from the server,** so they can be trusted (later: ranked, O-2).
- **LAN stays player-hosted** (no server in the room). Online falls back to a player host only when the server
  can't take a match (section 6).

## 2. Pieces

```
 players (Android / Windows)            play.secondhuntonline.com (Linode)
 +--------------------------+   wss     +--------------------------------------------+
 | menus, OnlineMatch       | <-------> | SecondHunt.Server (hub): identity, lobbies, |
 | NetMatchSession (joiner) |           |  Find Game, chat, records, ALLOCATOR        |
 | prediction, hit reports  |   UDP     |  UDP relay (rooms of slots)                 |
 +--------------------------+ <-------> |       ^ loopback, slot 0                    |
                                        | SecondHunt.MatchHost (one process / match): |
                                        |  headless MphRead sim, NetMatchSession host |
                                        |  with no local seat, game data (read-only)  |
                                        +--------------------------------------------+
```

- **Hub (existing `SecondHunt.Server`).** Gains an **allocator**. When a served game is created, it starts a
  `SecondHunt.MatchHost` child process and hands it relay slot 0 plus a token. It caps concurrent matches, watches
  each child's heartbeat, closes the game if a child dies, and drains (no new matches) before an update.
- **Match host (new `src/SecondHunt.MatchHost`, console, `net9.0`).** References `MphRead` + `MphRecomp.Core`
  only. Runs the existing host loop (what `-onlinetest external=1` runs today) with **zero local seats**. Talks to
  the hub over a local control channel (ready, lobby state, results, heartbeat) and to players over the relay.
- **One process per match.** MphRead's game state is static (`PlayerEntity._players`, `GameState`, `Read`
  caches), so matches can't share a process. Each is isolated, and a crash ends only that match.
- **Transport stays the relay.** The match host joins its relay room over loopback (a near-zero hop), so tokens,
  held seats, rejoin and rate limits all keep working. Served games skip Cloudflare TURN: it would only add a hop
  in front of the same box. A direct socket can come later.
- **Ready for more regions.** Later, a match host machine elsewhere (EU, JP) can register with the hub as a
  region. The protocol carries the host address, so clients don't care where it runs.

## 3. Game data on the server

Owner OK, 2026-10-16: the server keeps the owner's own extracted game data. Rules:
- Only what a headless match reads: room collision, entity and node files, and the models and animations
  gameplay uses. No audio, movies or menus. Textures only if the loader can't skip them yet.
- It lives in `/var/lib/secondhunt/gamedata/`, owned by the service user, mode 0700. It is never under Caddy,
  never served, never in the repo or any package.
- It comes from the owner's own dump and imports (MPH, plus the Echoes arena import for EXTRA ARENAS), pushed
  once by a deploy script.
- Matches need the same ROM key as clients (`LanSession.RomKeyFor`). Rev 0 and rev 1 share "MPH USA".
- **What's there (D6, 2026-10-16):** the 652 files in `src/SecondHunt.Server/deploy/gamedata.txt` (names only),
  copied from the owner's rev 1 extract by `gamedata.ps1`: 16 MB. They came from a traced `-matchsweep mode=all`
  (MphRead `Paths.ResolveCase` logs every file it opens when `SECONDHUNT_FILE_TRACE=<file>` is set). Room models,
  collision, entities and nodes; hunter, weapon, pickup and effect models (their textures are in the same files);
  particle files; intro cameras; the bot personality table; HUD string tables and 2 music tables (MphRead's room load
  reads these, and a copy without them crashes). **No cart code:** the match host sets `Extract.NoCodeFiles`, so the
  sound tables and font usually read from `arm9.bin`/overlays become empty tables and a blank font (it never plays or
  draws). No sound, movies or menu screens. No Echoes arenas (served games refuse extra arenas).

## 4. Protocol and lifecycle

- **Game kinds.** `GameInfo.Served` (new). The server answers `host_game` with a served game when it has
  capacity and the client is new enough. Old clients never see served games: the version key carries a marker,
  so `JoinGame`'s exact version check filters them.
- **Owner vs host.** On the server, `Game.Host` splits into `Owner` (a player: card, visibility, settings,
  START; passes to the next player if they leave the lobby) and the match host (slot 0, the process).
- **Lobby.** The owner's MODE/RULES edits go to the match host as a new joiner-to-host `SETTINGS` message,
  honoured only from the owner's seat. START works the same way. The match host keeps sending LOBBY as hosts
  do now.
- **Find Game.** No host pick: every searcher joins a served game.
- **Rejoin, replays, records.**
  - Rejoin works as now.
  - The match host records the replay and gives every player a copy.
  - The match host reports results directly to the hub (trusted).
  - Host migration is off for served games and stays for player-hosted ones.
- **Versions.** Server 0.8.0 (`SinceServer`), a new match protocol number, and a hub-to-match-host control
  protocol (its own version, checked at child start).

## 5. Shot feel ("subtle lag")

Today a guest's beam hits on their own screen, but damage only appears when the host's snapshot comes back, and
the host rewinds targets at most 12 frames (200 ms, `LagCompensation.MaxRewind`). Past about 150 ms ping, shots
that hit on screen can miss. For served games:
- **Local hit feedback.** The shooter's device shows the hit when its own beam connects: hit spark, sound, a
  damage flash on the target. It doesn't wait for the snapshot.
- **Hit reports.** The shooter's device tells the server "my shot X hit seat Y on server frame F". The server
  checks it against its own rewind history: was Y within reach of the shot at F, was the shooter alive, is the
  report not a duplicate, is the weapon's damage right. If so, it applies the hit even when its own replay of the
  shot just missed. The server's own beam test still runs, so a report only rescues near-misses its checks allow.
- **Rewind window** raised for served games (history ring and cap to be measured; the target is about 400-500 ms).
  There is no host to protect, so the usual cost of a wider window ("shot behind cover") falls on everyone equally.
- These parts don't depend on a server host, so player-hosted and LAN games can use them too.

## 6. Capacity and cost

- Simulation runs about 0.2-0.6 ms per frame for 4-8 players on the PC. Memory per match on Windows: 130-180 MB
  (D2). **On the Linode (D6, 2026-10-16): 67-75 MB resident and 5-6% of the one CPU per match** (3 seats, live).
- The Nanode has 1 GB. At about 150 MB per match, it fits about 4 at once beside the hub. The allocator caps it
  (`--max-matches`).
- When the cap is reached, online games fall back to a player host (today's path) and the lobby says so.
  Upgrading to the 2 GB plan (+$7/mo) is the owner's call once real numbers are in.
- Bandwidth: match traffic already flows through this box's relay, so a server host adds no traffic.

## 7. Milestones

| # | Step | Check |
|---|---|---|
| D1 | **Seatless host.** `PlayerEntity.HostNoLocalSeat` (input, Main, cosmetic guards), `StartMatch` with no humans in the template, `NetMatchSession.Host` with zero local seats | PC: `-onlinetest served=1`: every player is a joiner, same scores, `-replaygolden` 9/9 exact. **DONE 2026-10-16:** PASS with 2/3/4 humans, `delay=75 loss=3 jitter=30`, Capture, `leave=15`; goldens 9/9 exact; normal `-onlinetest` PASS. (Fix found: the HUD update must be skipped, since HUD parts exist only for a main player.) |
| D2 | **`SecondHunt.MatchHost` process.** Console host loop + control channel; Linux publish; measure memory/CPU per match on the Linode | Linux smoke match against the live relay; MB + ms per match recorded here. **PC part DONE 2026-10-16:** `SecondHuntMatchHost` (standalone: hosts a public served game, starts at `--min` players after `--countdown`, plays it, opens the next). Local hub + Tools joiners: 2 humans + 1 bot and 4 humans + 4 bots, same scores on every device; everyone leaving ends the match. Step 0.46-0.6 ms mean; 130-180 MB working set on Windows (managed heap about 60 MB). linux-x64 self-contained single file: 89 MB. Linode: 67-75 MB resident, 5-6% CPU per match (D6). The control channel moves to D3 with the allocator. |
| D3 | **Hub allocator + served games.** `GameInfo.Served`, Owner vs host, SETTINGS/START from the owner, Find Game without a host pick, trusted results, drain on update, server 0.8.0 | Server selftest additions; `-onlinetest` served lobby -> match -> PLAY AGAIN -> owner leaves | **DONE except Find Game, 2026-10-16 (PC, nothing deployed):** server 0.8.0 `--matchhost <exe> --matchhost-data <dir> [--max-matches 4]` (HubServed.cs): a served ask gets a SecondHuntMatchHost process on relay slot 0, the asker owns the lobby on slot 1; past the cap / draining / other version / extra arena / mods -> player-hosted as before. Owner messages (Core NetMatchOwner.cs, match msg 13: SETTINGS + START); match host `--control` (stdin owner/stop, stdout !ready/!started/!lobby/!result/!bye); results only from the match host; owner leaves -> lowest slot owns it (MemberEvent.Owner); everyone gone -> closed; match host dies -> closed. `admin drain on|off` (a `drain` file next to the db) + `/v1/health` tail `served N/M [draining]`. Tests: `-onlinetest served=hub` PASS 3+1 bot; 4+2 bots with leave=20; matches=2 ownerleave=1 (the next player starts match 2); matches=4 (match host memory 157 MB after match 1, then flat 219-226 MB, managed 55-60 MB: no leak); player-hosted `-onlinetest` still PASS; server `--selftest` 219/0 (SelfTestServed.cs: 11 served checks, this binary as `--fake-matchhost`). **Left:** Find Game served (needs a lobby with no owner: auto-start when the matched players are in), HostGame answers before the match host is on the relay (joiners' Join retries cover it; D4 may wait for !ready), the owner isn't told when START is refused for a missing arena (D4) |
| D4 | **Clients.** Lobby UI for owner-not-host, served/fallback wording, protocol bump | `-onlinecreatetest` served; Odin + Samsung live match | **DONE 2026-10-16:** CREATE asks the server to serve the game (server 0.8.0+; Find Game doesn't yet). A served reply makes this device join as the lobby's owner (`Net.IsOwner`) and push the creator's rules and bots. The menus' host controls (bots, rules, invite, START, voice) follow `Runner` = the hosted game or the owned served one. Owner START = OWNER START, then a 6 s "the server didn't start the match" popup if nothing happens. Wording: "the server closed the game". No TURN for served games (the relay is on the server). Tests: `-onlinecreatetest served=1 lobbyedit=1 rulesedit=1` PASS locally and **live**; player-hosted create test + `-onlinetest` still PASS. Devices: next APK |
| D5 | **Shot feel.** Local hit feedback, hit reports, wider rewind | `-onlinetest` at 150/250 ms: share of on-screen hits that count, before vs after |
| D6 | **Deploy.** Game data push script, systemd, limits | Live match on the server; owner + friends test | **DONE 2026-10-16 19:16 UTC:** server 0.8.0 + `SecondHuntMatchHost` + the game files live (`deploy.ps1 -GameData`, `gamedata.ps1`, DEPLOY.md section 6); health `ok 0.8.0 ... served 0/3`. Before deploying, the pruned files passed `-onlinetest served=hub` (`SECONDHUNT_MATCHHOST_DATA`) in Battle (2 matches), Capture, Bounty, Nodes, Defender, Prime Hunter, Survival and Battle Teams. Fixed on the way: Prime Hunter crashed a seatless host (`ProcessModeHud` now skipped when `HostNoLocalSeat`). Live `-onlinecreatetest served=1` PASS (owner edits reach the server's match host, START runs there). Left: owner + friends test on the devices |

## 8. Owner decisions (defaults used until told otherwise)

1. **All online games served** (Find Game, public, friends), with player-host fallback when full. LAN unchanged.
2. **Mods off** in served games, as in stranger games today.
3. **Server plan:** stay on the 1 GB Nanode until D2's numbers are in.
4. **Shot feel for player-hosted/LAN too** (section 5): yes, once it's proven on served games.
