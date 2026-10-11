# Online play: friends, rivals, matchmaking, server

**Owner GO 2026-10-08.** Thread: "Online play (friends, rivals, server)" in `COORDINATION.md`. This doc is the plan and
the contract. It builds on `docs/MULTIPLAYER.md` (netcode, LAN), `docs/MULTIPLAYER_MENU.md` (the vanilla menu pages),
`docs/REPLAY_MODE.md` (replays) and `docs/MULTIPLAYER_RELIABILITY.md` (version and data checks). The 2026-09-30
friend-system research (summarised in `docs/MULTIPLAYER.md` section 5) is the background for the choices below.

Rule of thumb, the same as everywhere else: **vanilla first, modern extras on top.** Where vanilla had something
(friend codes, rivals, Find Game, Friends and Rivals games, the Hunter License, ADD RIVAL on the results screen),
we copy it. The extras sit beside it, never instead of it.

## 1. Owner decisions (2026-10-08)

| # | Question | Decision |
|---|---|---|
| 1 | Who runs the match | **A player hosts now, dedicated servers later.** The host's device runs the match (our LAN netcode); our server does friends, lobbies and relaying. The protocol stays ready to move matches onto server processes later. |
| 2 | Server region | **US.** Players anywhere can play: match traffic goes through relays near each player (section 6), and Find Game prefers nearby players first. |
| 3 | Identity | **No passwords, no email.** A key made on the device, a vanilla 12-digit **friend code**, and a searchable **name with a tag** (`KANDEN#4821`). A backup/transfer code moves the identity to another device. |
| 4 | Budget | **Up to about $10 a month.** |
| 5 | Names shown to strangers | **Real names**, through a word filter, with **block** and **report**. |
| 6 | Chat | **Everything, in the lobby AND in matches:** preset quick messages, typed chat for everyone (filtered), a per-player **mute**, and **voice chat for everyone**: off for strangers by default, on for friends by default. |
| 7 | Matchmaking | **Vanilla Find Game AND a list of open games people host.** **All game modes online** (vanilla's Find Game was Battle only). |
| 8 | Extras | **Invites + join friends, parties, a recent players list.** (Not live spectating, for now.) Plus the owner's opener: friend requests, rival requests, tappable names everywhere, licenses from REPLAYS. |
| 9 | Player count | **Make 8 players possible** (owner, 2026-10-08). Vanilla is 4. Section 10. |
| 10 | Mods online | **Every mod is off in an online match with strangers** (owner, 2026-10-08, for when mods ship: today `Core/Mods` is a framework the app doesn't load into matches yet). Section 8b. |
| 11 | Host quits mid-match | Owner asked to **scope host migration** (2026-10-08). Section 9b; until it's built the match ends with results for everyone, as vanilla. |
| 12 | Mods with friends (was O-5) | **Allowed when every device has the identical set of mods** (owner, 2026-10-08 13:10): a friends/rivals-only game may run mods if everyone's fingerprint matches; strangers still never (decision 10). Section 8b; the server rules are built. |
| 13 | Server OS | **Linux** (owner, 2026-10-08 13:10): Ubuntu 24.04 LTS on the rented VPS (Windows VPS plans cost ~$10-20/mo more for the license). The deploy kit (`src/SecondHunt.Server/deploy/`) targets it. |
| 14 | Word filter (was O-3) | **An open-source list (LDNOOBW, CC BY 4.0, credited in THIRD_PARTY_NOTICES) + the owner's additions** in a plain text file on the server (owner, 2026-10-09). |
| 15 | Rank in Find Game (was O-2) | **No rank matching for now** (owner, 2026-10-09: only worth it with a larger player base): Find Game matches by mode + region; ranking points still count and show on licenses. |
| 16 | Host migration (O5b) | **Build it before the next release** (owner, 2026-10-09), right after O5 (section 9b). |
| 17 | Next release scope | **O5, O5b, O6, O7, O8, O10 and P8 before the next release** (owner, 2026-10-09). |
| 18 | Relays (was O-6) | **Cloudflare Realtime TURN first, our Chicago relay as the fallback** (owner, 2026-10-09, the O5 task). The server mints short-lived credentials from a TURN key kept only on the Linode and stops issuing them past a monthly cap (default 900 GB; Cloudflare's free tier is 1,000 GB), after which players stay on Chicago. **Built 2026-10-09:** client `Net/TurnClient.cs` + `Net/HybridTransport.cs` (Netcode O5), server 0.4.0 `turn_creds` / `turn_usage` (Social O6, live). Section 7. Live use waits for the owner's key. |

## 2. Vanilla's online, in one table (from the ROM text + the manual; research 2026-09-30)

| Vanilla | What it did | Ours |
|---|---|---|
| Friend code | 12 digits, made on the first WFC connect; both players enter each other's code | Same 12 digits, made on the first online connect. Entering a code = a **friend request**; if both entered each other's codes it completes at once, as vanilla |
| Rivals | Both tap ADD RIVAL on the results screen (mutual). (Rival Radar is NOT online: it's the DS-wireless sleep-mode trade, see 2b) | Same ADD RIVAL (mutual); also a **rival request** from a name, the recent players list or a replay. A pair is a friend OR a rival, never both (server 0.7.0) |
| Roster | One roster: 60 slots for friends + rivals, 57 lockable, unlocked slots reused when full (ROM; which one is reused: unsure) | Vanilla pages show the roster as vanilla; open decision O-1 on the 60 cap |
| Find Game | Worldwide, Battle only, REGION + MATCH MY RANK filters, arena vote | Same screens and vote, every mode, parties can queue together |
| Friends and Rivals | Host picks mode/rules, FRIENDS yes/no + RIVALS yes/no; others see AVAILABLE GAMES + who's online | Same, plus **PUBLIC** (open games list for everyone) and invites |
| Hunter License | Name, rank stars, stats, head-to-head (last result, rounds against, victories against) | Same card (already built for LAN: `Core/Frontend/HunterLicense.cs`), uploaded so anyone can open yours by tapping your name |
| Message / voice | Friends only, lobby only, hold X to talk | Preset + typed + voice, lobby and match, strangers off by default (decision 6) |

### 2b. Friends vs rivals: vanilla vs ours (release test N25/N26, 2026-10-10)

Sources: the ROM's English strings (dump in `handoffs/friends-rivals-head-to-head-t3_2026-10-10_1933-files/rom_en.txt`,
menu pages from `menus.md` 44/48/56-64), Nintendo World Report preview 11216 + review 11241, GamesFirst review.
"Certain" = the ROM text says it; "fairly sure" = a review/preview says it; "unsure" = not found.

| Thing | Vanilla (how sure) | Ours | Changed (2026-10-10) |
|---|---|---|---|
| How you become one | Friend: both enter each other's 12-digit code (certain, ROM). Rival: both tick ADD RIVAL on RESULTS, mutual ("these players have agreed to become your rival") (certain); offered after random and friend matches (fairly sure, NWR) | Friend: code / name / lists, a request the other accepts; codes both ways complete at once. Rival: mutual ADD RIVAL + a rival request | - |
| Friend AND rival | Not possible: one roster entry per person (certain: one roster, "delete some friends or rivals") | Was possible (separate link rows, each request checked only its own kind) | **Fixed:** a pair is a friend OR a rival. Server 0.7.0 removes rival links/requests where a friend link exists (once at start) |
| Rival -> friend | Swap friend codes: "you have upgraded your rival to a friend" (certain) | Same: a friend link replaces the rival link and drops waiting rival requests both ways; both devices are told | **New** |
| ADD RIVAL on a friend | Not found in the ROM (unsure) | Refused (`already_friends`); the license shows "friend (not a rival)" in grey, FRIENDS / FIND A HUNTER hide ADD RIVAL for friends | **New** |
| Remove | Same DELETE PLAYER for both, one-sided (certain) | REMOVE works the same for both kinds | - |
| Roster size | 60 shared, 57 lockable (certain); which unlocked slot is reused when full (unsure) | Friends uncapped, rivals 60 (owner pick 10-09) | - |
| Text messages | Lobby SEND MESSAGE "to be seen by all your friends in this game": friends only (certain) | Quick messages for everyone in the lobby; typed chat needs the chat lock opened, except between friends | **Fixed:** the typed-chat bypass was friends + rivals, now friends only |
| Voice | Friends only, hold X, lobby before/after the match, not in the match (certain) | Friends on, strangers off; rivals count as strangers (section 8); lobby + match | - (already vanilla-like for rivals) |
| Friends and Rivals games | Host offers the game to FRIENDS and/or RIVALS; rivals can join (certain) | Same, plus PUBLIC and invites | - |
| Online status + license | Both kinds see each other online and can view the license (fairly sure) | Same | - |
| Head to head | License page 57: last encounter result, total rounds against, victories against (certain); roster-only storage (unsure) | Counted on the server once per finished match from the host's RESULTS; both players read the same record (see section 5) | **New** (was counted per device) |
| Rival Radar | DS-wireless SLEEP-MODE trade of licenses with nearby DSes ("close your nintendo ds ... add each other to your rivals roster"); not an online feature (certain; a per-session limit: unsure) | Not built (no sleep-mode radio on our devices) | Doc fix: was listed under online rivals |
| Ours only (not vanilla-forbidden, kept) | - | Rivals get presence, Friends+Rivals games, invites, parties, mods-with-friends-or-rivals (decision 12) | - |

**Do rivals still make sense?** Yes. They're vanilla's link for people you met in a match and want to see again
without trusting them as friends: they can find you and join your Friends+Rivals games, but get no typed-chat or
voice privileges. That's the same split vanilla made, so they stay.

## 3. The parts

```
 device (Android / Windows)                         our server (one small US VPS)
 +-------------------------------+   WebSocket     +--------------------------------------+
 | Core/Online: identity, client | <-- TLS 443 --> | SecondHunt.Server (.NET 9, one exe)  |
 |  friends, rivals, presence,   |                 |  players, codes, names, relations,   |
 |  lobbies, chat, matchmaking   |                 |  presence, lobbies, matchmaking,     |
 +-------------------------------+                 |  chat relay + filter, reports, SQLite|
 | match netcode (LanSession,    |                 +--------------------------------------+
 |  MatchNetDriver, snapshots)   |   UDP            relay: Cloudflare TURN edge near each
 |  over a relay transport       | <------------->  player (free 1 TB/mo), our own UDP relay
 +-------------------------------+                  on the VPS as the fallback
```

- **One server program**, `src/SecondHunt.Server/`, C# like the game. It links the protocol files from
  `src/MphRecomp.Core/Online/` (the way `SecondHunt.ReleaseTool` links the updater's files), so the game and the server
  can never disagree about a message. It holds **no game data** (no ROM, no models, no arenas): names, codes,
  relations, license summaries, lobbies.
- **The match itself** stays the host-authoritative netcode we already play on LAN (`docs/MULTIPLAYER.md` section 4).
  Online adds a relay transport under it, client prediction for internet ping (N3), and lag compensation (N4).
- **The server address is configurable** and the protocol is documented here, so the community can run its own
  server if ours ever goes away. Rosters live on the device too, so a server wipe loses presence, not friends.

## 4. Identity, friend codes, names

- On the first online connect the device makes an **ECDSA P-256 key pair** (the same .NET crypto the in-game updater
  already uses on Android and Windows). The private key never leaves the device. The server knows the public key.
- **Login = a signature.** The server sends a random challenge, the device signs it. No password to leak or reset.
- **Friend code:** 12 digits shown `1234-5678-9012` like vanilla. 11 digits come from a hash of the public key, the
  12th is a check digit, so a typo is caught on the keypad before anything is sent. The server binds a code to the
  first key that claims it. If a brand-new key ever collides, the device just makes another key.
- **Name:** the Hunter License name (vanilla rules: 10 characters, as typed) plus a 4-digit **tag** the server picks
  so two KANDENs can both exist: `KANDEN#4821`. Changing your license name keeps your tag if it's free under the new
  name. Names pass the word filter (section 8) before anyone else sees them.
- **Search by name:** type a name (tag optional), see the matching players' license cards, send a friend request.
  Setting **"who can find me": everyone / friend code only.**
- **Backup / transfer code:** the private key shown as a long code (and a QR) in RECOMP SETTINGS. Enter it on another
  device (or Windows <-> Android) to be the same player there. It's the one secret: whoever has it *is* you. Losing the
  device without a backup means a new identity (friends re-add you), the same as losing a DS.
- **Never shown to anyone:** IP addresses, the key, device names. Players see name#tag, license card, presence.

## 5. Friends, rivals, requests, recent players, block

- **Friend request:** from a friend code, a name search, a name in a lobby/match/results/replay/recent list. The
  other player sees it in a REQUESTS list and accepts or declines. Two players entering each other's codes complete
  at once (vanilla). Requests wait on the server while the other player is offline (vanilla needed both online).
- **Rival:** vanilla's mutual ADD RIVAL on the results screen, plus a **rival request** like a friend request.
  **Friend OR rival (server 0.7.0, vanilla):** never both. Becoming friends upgrades a rival (rival link + rival
  requests dropped both ways, both devices told); ADD RIVAL on a friend is refused (`already_friends`, license shows
  "friend (not a rival)"). The server removed existing doubles once at start. Full comparison: section 2b.
- **Recent players:** everyone you've played online (last 100, on the device + the server), with when, the mode, the
  result. Tap one: license, add friend, add rival, block, report. **Server part built (2026-10-08):** `get_recent` ->
  the newest 50 players you were in a *started* match with (a lobby doesn't count), newest first, anyone blocked either
  way left out. The device's own list (mode, result, 100 entries) comes with the menus (O9).
- **Block:** never matched together again (Find Game skips them), hidden from each other's lists, no chat/voice, no
  requests. **Mute** is lighter: just silence their chat and voice for you.
- **Head-to-head records** (last result, rounds against, victories against): kept per player id, exactly the vanilla
  license fields; already counted for LAN by `HunterLicense`.
  **On the server from 0.7.0 (release test N30):** each device used to count its own online record when it reached
  RESULTS, matching seats by nickname, so a quit, a name mismatch, LAN or an early leaver made two devices disagree.
  Now the HOST of any network match (online or LAN) sends `match_result` {GameId or Lan + MatchKey, Teams, rows of
  friend code + place + team} from RESULTS. Seats map to friend codes through the replay link's identities; bots and
  unknown seats are skipped; teammates aren't counted against each other. The server counts an online report once per
  started round, only if the reporter and every row were in that round (`HubRecords.cs`); a LAN report must include
  the reporter, once per MatchKey, max 20/hour. Records live in the `records` table (both directions) and come back as
  `PlayerCard.Record`, so both players read the same numbers. Reports queue in `online_pending_results.json` until
  the server answers (offline LAN matches go up on the next connect). The license's local count is only used against
  servers older than 0.7.0. **Records restart at 0.7.0:** the server's record replaces a device's old count the next
  time the license opens.

## 6. Presence, parties, invites, games, Find Game

- **Presence:** offline / in the menus / in a lobby / in a match (+ mode and arena). Friends see it; rivals see it if
  the owner's vanilla FRIENDS/RIVALS flags allow. Presence only while the game is open (no background service).
- **Hosting a game:** the vanilla Friends and Rivals setup (mode, arena, rules, teams, bots) plus who may join:
  FRIENDS yes/no, RIVALS yes/no (vanilla) and PUBLIC yes/no (new: listed in the open games list for everyone).
  Seats up to 4 (vanilla) or 8 (section 10).
- **Open games list:** every PUBLIC game: host name, mode, arena, players 3/4, ping, region. Tap to join.
- **Invites:** invite a friend from your lobby; they get a notice anywhere in the menus; accept = join.
- **Join a friend:** a friend in an open lobby shows JOIN in the friends list.
- **Parties:** up to 4 (or 8) friends group up first, then host a game or Find Game together; the party stays
  together after the match. **Server part built (2026-10-08), with these defaults (owner may change any):** in memory
  (a server restart ends parties, like lobbies); up to 4 (8 once P8); the **leader** invites, friends and rivals only
  (`not_friend` otherwise); joining needs an invite; when the leader hosts or joins a game every other member gets
  `ev_party_game` and may join it even if it isn't listed for them (it's listed for them too), size permitting, but a
  block by anyone in the game still keeps them out; a member joining while the leader is already in a lobby gets
  `ev_party_game` at once; the leader leaving hands the party to the next member (join order); the last one out ends
  it; going offline leaves the party. Find Game with a party is O6.
- **Find Game:** pick the mode (Battle first, as vanilla; every mode available), REGION (nearby / worldwide) and
  MATCH MY RANK (vanilla's filters). The server fills a match from nearby players first and widens the search over
  time. Players vote on the arena (vanilla's VOTE FOR AN ARENA, ties random). The server picks the host: the player
  with the best connection to the others. ADD RIVAL on the results screen.
  **Built 2026-10-09 (O6, server 0.3.0):** MATCH MY RANK left out for now (decision 15). Defaults (owner may change any):
  matches of 2-4, a search fills for 15 s; NEARBY = the two players' pings add up to 120 ms or less, +40 ms every 10 s,
  anyone after 60 s; WORLDWIDE = anyone at once; a fitting search first drops into an open PUBLIC game; the vote lasts
  15 s (plurality, ties random, no votes = random of the options); the host is the lowest ping (ties: who searched first)
  and has 20 s to open the game or the match goes back in the queue. A party searches as one (the leader starts it).
  **QUICK MATCH became FIND GAME's fast path:** its defaults (any mode, nearby) are the old quick match, and find_start
  already drops a fitting search into an open public game, so there is one matchmaker instead of two (simpler). ADD
  RIVAL from RESULTS: tap a name after an online match -> their Hunter License -> ADD RIVAL (no separate button on the
  vanilla RESULTS page yet). Known limit: a party lasts one match (the match takes over the connection; on its end the
  party is gone).
- **Ping across the world:** a match between players near each other has local ping wherever our server is: each
  player talks to the nearest relay, and the relays carry it between them. A US-Europe match is about 100-150 ms and
  US-Japan about 120-180 ms, like any cross-ocean game; that's what client prediction (section 9, N3) is for.

## 7. The relay, the server, and what it costs

**Relay.** Match traffic always goes through a relay, never straight between players: it hides every player's IP
from the others (the host would otherwise see everyone's), and it works through every home router (direct
connections fail for about 30% of pairs). Two relays:
- **Cloudflare Realtime TURN** (standard TURN, RFC 8656): each player connects to the nearest Cloudflare city, so
  European and Japanese players get European and Japanese relays. **1,000 GB a month free**, then $0.05/GB. Our server
  hands out short-lived TURN credentials.
- **Our own UDP relay** inside the server program on the VPS: the fallback, and the one we build and test first.

**How the two work together (decision 18, built 2026-10-09, Netcode O5).** Every device keeps its relay slot for the
whole match (lobby, SendAll, and the fallback). Once in a game it asks our server for TURN credentials (`turn_creds`)
and allocates on Cloudflare (`TurnClient`: RFC 8656 over UDP, Allocate with the long-term credentials, a channel per
peer, ChannelData; no ICE, no TCP/TLS). The match doesn't wait for it: `HybridTransport` starts on the relay, tells each
peer it hears from its *relayed* address over the relay (OFFER: never its own address), both sides bind a channel and
probe over TURN, and a peer's packets move to TURN once a probe is answered over TURN (both ways work). TURN silent
1.5 s or the allocation failing (refresh refused, credentials expired after 2 h) -> back to the relay, probing again.
Packets are accepted from either path at any time, so a switch loses nothing. Usage: each device reports what it
received from TURN (Cloudflare bills only its egress to the client) every 30 s and at close; the server clamps it to
200 KB/s per player and counts per UTC month. Tests: `-turntest` (RFC 5769 vectors, allocate, channels both ways, 438
stale nonce, wrong password, close) and `-onlinetest turn=local` (a local TURN server, our server's `--turn-static`
credentials; `turnkill=<s>` kills it mid-match). `turn=hub server=wss://...` asks a real server, i.e. Cloudflare once
the key is in.

**Bandwidth.** Today's snapshot is ~59 bytes per seat plus items. At 30 snapshots a second a 4-player match moves
about 0.2 GB an hour through the relay, an 8-player match about 0.9 GB an hour. Voice adds about 2 KB/s per person
talking. 1 TB a month is about 1,000 eight-player match-hours or 5,000 four-player match-hours, free.

**The server box (US).** One small Linux VPS is plenty for friends, lobbies, chat and matchmaking for thousands of
players (the server never runs a match; that's the host's device).

| Option | Price | Included traffic | Notes |
|---|---|---|---|
| Akamai (Linode) Nanode, Chicago or Dallas | **$5/mo** | 1 TB | **Recommended**: central US, simple billing |
| Vultr Cloud Compute, Chicago or Dallas | $5-6/mo | 1-2 TB | Same tier |
| DigitalOcean Basic Droplet | $4-6/mo | 0.5-2 TB, then $0.01/GB | Good tooling |
| OVHcloud VPS-1 (Virginia/Oregon) | ~$4.50-6/mo | unmetered | Cheapest, usually a 12-month prepay |
| Hetzner US (CPX11) | ~$20/mo | 1 TB | Too expensive since the June 2026 price change; Hetzner EU (CX23, EUR 5.49) only for a later EU box |

Plus a **domain** (about $10-12 a year, e.g. at Cloudflare Registrar) for the HTTPS address the game connects to.
**Total: about $6 a month.** Prices checked 2026-10-08 from the providers' pages and 2026 comparison sites; confirm at
checkout. Renting the box and the domain is the owner's step (accounts, payment); everything after that (setup,
deploy, updates) is ours.

**Software on the box:** the server program (self-contained .NET 9 for Linux, run as a service), SQLite for storage
(one file, backed up nightly), Caddy in front for HTTPS certificates. Updates: copy the new build and restart.

## 8. Chat, voice, safety

- **Preset messages** (lobby + match): "GG", "Rematch?", "Nice shot", "One more", "Ready", "Pick an arena", ... From a
  quick-message wheel in matches.
- **Typed chat** (lobby + match): for everyone, through the word filter (also used for names). In matches it shows as
  a small overlay; typing opens the keyboard (page 62's keyboard in menus).
- **Voice** (lobby + match): Opus codec (Concentus, a pure C# Opus port, so the same code on Android and Windows), push
  to talk or open mic, sent through the same relay. Defaults: **strangers off, friends on**, each changeable per
  player and globally. Android asks for the microphone the first time.
- **Mute** (per player, chat and/or voice), **block** (section 5), **report** (name, chat, voice, cheating) from any
  name. Reports go to a queue the owner reads with an admin tool; bans are by player key. New identities are free, so
  a fresh player starts with limits (e.g. a few matches before typed chat to strangers) to keep ban-dodging slow.
- **Never kept:** voice (not recorded), IP addresses (memory only, never logged). Typed chat is kept only when it's
  in a report.
- **Legal pieces this needs** (because strangers can talk to each other): a 13+ notice on the first online visit, a
  short privacy notice and code of conduct, an in-app "delete my online profile", and a written risk assessment if
  the game has UK players (UK Online Safety Act: Ofcom's 2025 gaming guidance names stranger matchmaking, chat and
  profiles). The owner is the moderator of last resort. These are written and shipped with O7, before typed chat or
  voice reach strangers.

**Built (O7, 2026-10-09).** Chat goes over the server's WebSocket, not the match relay, so the filter, the limits, blocks
and report evidence all live on the server. One chat per game: its lobby and its match share it.
- Quick messages are indexes into `ChatPresets.All` (18, append-only: each build shows its own words); the match wheel
  holds 8 (`ChatPresets.Wheel`).
- Typed lines: 80 characters (60 from the app's keyboard), masked by `WordFilter` (LDNOOBW English + the owner's
  `words_owner.txt`, re-read live). Rate: 4 typed per 10 s, 0.7 s apart, no same line twice in 10 s; quick messages 6 per
  10 s. **New identities:** typed chat reaches strangers after 3 online matches with other people; before that only the
  game's friends and rivals see it (`chat_locked` tells the sender).
- **Mute** is this device's (`PlayerMutes`, `online_mutes.json`), one switch for chat + voice. **Report** reasons: name,
  chat, voice, cheating, other; a chat report attaches the reported player's last 10 lines and the game's last 20 from
  the server's memory (3 h, never on disk otherwise). Reports kept 30 days handled / 90 open.
- **Admin tool:** `SecondHuntServer admin ...` on the VPS, `src/SecondHunt.Server/deploy/admin.ps1 <command>` from the PC
  (deploy/DEPLOY.md section 5): report queue, dismiss/resolve, ban/unban by key, chat mute, filter test, stats.
- **App:** lobby strip (last 2 lines) + CHAT page (log with MUTE / REPORT per line, TYPE A MESSAGE, 18 quick messages),
  REPORT A HUNTER (reason page), license MUTE (`OnlineMenus.Chat.cs`). In a match (`App/Campaign/MatchChat.cs`): new
  lines appear as MphRead's own HUD messages bottom left; **hold Select** (touch: drag from CHAT) and point the right stick
  at one of 8 quick messages, let go to send. Typing happens in the lobby (no keyboard mid-match).
- Legal drafts: `docs/LEGAL_AGE_NOTICE_13_PLUS.md`, `docs/PRIVACY_NOTICE.md`, `docs/CODE_OF_CONDUCT.md`,
  `docs/UK_ONLINE_SAFETY_ACT_RISK_ASSESSMENT.md` (all DRAFT, for the owner).

**Built (O8 voice, 2026-10-09).** Voice rides the match link itself (`OnlineMatch.Link`: Cloudflare TURN per peer when up,
else our relay), never the server's WebSocket, and nothing records it (replays and the server never see it).
- **Codec:** Opus via Concentus 2.2.2 (BSD-3, `THIRD_PARTY_NOTICES.md`): 16 kHz mono, 20 ms frames, 2 per packet (25
  packets/s while talking), VOIP 16 kbps VBR, in-band FEC, DTX off. About 1.2 KB/s up per talker, per listener.
- **Wire:** first byte `0x41` (`MatchNetVoice.MsgVoice`; `NetMatchSession` skips it before the match's own messages):
  audio `[seq][count|end][len + opus]*`, and a hello (friend code) every 2 s for servers before 0.5.0's `PlayerCard.Slot`.
  Sent per slot only to players voice is on with, so the relay never carries it to anyone else.
- **Who hears whom:** both ways only: mode not off, the per-player choice else friends on / strangers off (rivals count as
  strangers), and not muted (`PlayerMutes`, shared with chat). Re-checked every 100 ms.
- **Jitter buffer:** adaptive target 3-12 frames (RFC 3550 jitter, reset at each talk spurt), FEC for a lost frame, then
  concealment; 138 ms mouth-to-ear on a clean link in `-voicetest`, 157 ms through the live server.
- **Threads:** capture, network receive, a voice playback thread with its own audio track, and a control thread; the game
  thread only passes the talk button on. Nothing waits on the 60 Hz sim or the game's audio mixer.
- **Devices:** Android `AudioRecord` (voice communication source, echo canceller + noise suppressor when the device has
  them; RECORD_AUDIO asked the first time voice needs the mic); Windows SoundFlow capture (built, not yet run with a mic).
- **Controls:** push to talk = **R3** on a pad (owner 2026-10-09; R3 lost its zoom default: L2's tap and Y zoom, and
  CONTROLS can bind zoom), **V** on a PC keyboard (CONTROLS > KEYBOARD & MOUSE), touch TALK in matches, TAP TO TALK under
  START in the lobby (a tap on, a tap off, a minute at most). Open mic: a level gate with a 300 ms hangover.
- **Menus** (`OnlineMenus.Voice.cs`): lobby seat cards say TALKING / VOICE OFF; CHAT page bars PUSH TO TALK / OPEN MIC /
  VOICE OFF, STRANGERS ON / OFF, VOLUME 50-200%; the license's VOICE ON / OFF (per player, beside BLOCK); REPORT A
  HUNTER's "their voice". Settings in `online_voice.json`.
- **Tests:** `-voicetest` (codec, jitter at 0/5/15% loss, 3 devices: friend/stranger/mute/PTT) and `-voicetest live`
  (two identities through the live server).

## 8b. Mods in online matches (decision 10)

All four mod tiers (`docs/MODDING.md`: asset overrides, `config.json` tuning, Lua scripts, later C# plugins) can
change how a match plays or what a player sees (a retuned jump, a see-through wall texture), so **an online match
with strangers runs with every mod off**, on every device in it. How it will work when mods ship:
- **Which matches:** anything a stranger can be in: Find Game, a PUBLIC hosted game, and any game where someone in
  it isn't a friend or rival of everyone else (an invited friend-of-a-friend counts as a stranger).
- **On the device:** joining or hosting such a match turns mods off for that match only (the asset overlay, config
  overrides and scripts unload; the player's enabled list isn't touched) and turns them back on after RESULTS. The
  lobby says "MODS ARE OFF IN ONLINE MATCHES WITH OTHER PLAYERS" once.
- **On the wire:** the game's `GameInfo.Version` gets a mods field: `none` for these matches. The server refuses a
  stranger match whose version isn't `none`, and the host refuses a JOIN that isn't. (A cheater can still edit their
  own copy of the game; the host-authoritative match limits what that does to others: their damage, score and
  movement come from the host's simulation. Visual cheats on their own screen can't be stopped by any rule.)
- **Replays** of these matches are marked mods-off, so they play back the same anywhere.
- **Built now (2026-10-08, server side):** `GameInfo.Mods` (default `none`; a fingerprint string otherwise) and
  `JoinGameReq.Mods`. The server refuses (error `mods`) a PUBLIC game with mods, a join whose mods differ from the
  game's, a join into a modded game by someone who isn't a friend or rival of everyone already in it, and a mods change
  once anyone has joined. Nothing sets a fingerprint yet (no mods load in matches); the device-side unload is for when
  mods ship.
- **Friends-only games with mods (decision 12, was O-5):** allowed when every device has the identical set of mods
  (a fingerprint in `GameInfo.Mods`, like the arena-data fingerprint), so friends can play modded together.

## 9. Netcode work for the internet (on top of the LAN netcode)

From `docs/MULTIPLAYER.md` section 4: LAN works today (protocol 6, host-authoritative, snapshots, items, kill
messages). The internet needs:
- **Relay transport** (O3, built): `Core/Multiplayer/Net/NetMatchSession.cs` is LanSession's flow for 1 host + up
  to 7 joiners (magic "MPHO", own protocol 1, LAN's message codes and settings format; joiners seated after the
  humans, before the bots; START per joiner, re-sent on a repeated JOIN), over `RelayTransport.cs` (one UDP socket to
  the relay, RelayWire header, 1 s keepalive = relay ping). `MatchNetDriver` takes an `IMatchNet` (LanSession or
  NetMatchSession) and applies the silence rule per remote seat. Test: `MphRead.Tools -onlinetest [players=3]
  [bots=1] [mode=] [arena=] [time=30] [delay=<ms one way>] [leave=<s, last joiner quits>] [server=ws://...]` spawns the
  server, hosts, spawns the joiners as separate processes (each its own 60 Hz clock) and passes when every device ends
  with the same scoreboard. Fixed on the way: a client whose time ran out a frame before the host's no longer takes
  the host's pre-time-up clock (it ended ~5 s early: `CampaignHost.ApplySnapshot`).
- **N3 prediction + reconciliation:** DONE 2026-10-09 (`Net/ClientPrediction.cs`): the client reconciles at the
  host's acked input, blended; adaptive jitter buffer. At 150 ms ping + 5% loss + 40 ms jitter, own pull-backs went
  from ~100 to 2-3 a match.
- **N4 lag compensation:** DONE 2026-10-09 (`Net/LagCompensation.cs`): the match host keeps a 32-frame pose ring per
  seat and tests a remote shooter's beams against the targets where that shooter saw them (rewind = host frame - the
  input's ViewTick, capped by `LagCompensation.MaxRewind`, 12 frames = 200 ms): beam segments, splash, ice
  wave; not Weavel's turret (static) or homing target picks. The hit lands on the target as it is now. Measured
  (`-onlinetest players=2 bots=0 delay=75 strafe=1`, a strafing target at 150 ms ping): joiner hits the host counted,
  off 48%, cap 9 frames 82%, cap 14 (uncapped) 100%. Owner 2026-10-09: cap 12 frames (200 ms, covers 150 ms ping
  fully). Deterministic: replays record ViewTick, already capped by the host, so a replay rewinds the same under any
  cap (`-replaygolden` 9/9).
- **Rates and size:** DONE 2026-10-09 (`Net/SnapshotDelta.cs`, online protocol 5): per-joiner delta snapshots at 30 Hz
  (20 Hz on a weak link) against the joiner's acked snapshot, input redundancy (last 4) + jitter buffer.
- **The rest of each player's state** (online protocol 6, `Net/SeatExtras.cs`): afflictions (frozen, disrupted,
  burning), power-ups (double damage, cloak), Weavel's turret health and the detail stats ride every online snapshot;
  clients start/end each effect the game's own way. Item drops come only from the host. No MP arena (vanilla or
  Echoes) has a moving platform, so none need syncing.
- **All 7 modes on clients:** DONE 2026-10-09 on LAN and online: octolith, node, Prime Hunter and mode stats come
  from the host in every snapshot (`Net/NetObjective.cs`; online protocol 3, LAN protocol 8). `-onlinetest` PASS in
  all 9 objective modes on PC; no device test yet.
- **Leaving:** a client leaving keeps the match going; the host leaving ends it with results for everyone (vanilla
  had no host migration). **Rejoin DONE 2026-10-09 (PC):** a player whose app restarted or whose connection dropped
  joins again (`OnlineMatch.RejoinAsync`, `join_game {gameId: ""}`: same relay slot, new token); the host revives its
  seat (same seat; a seat already dropped after 10 s of silence comes back through `CampaignHost.ReconnectSeat`, which
  replays record via `SeatRejoinTap`), re-sends START and a full snapshot; other clients follow the host's seat
  (out/back) while the match runs. `-onlinetest rejoin=<s> rejoinwait=<s>` PASS (2 s and 12 s gaps, also at 150 ms
  ping). The menus don't offer it yet (a REJOIN prompt after an app restart is the Online menus' call).
- **Same game everywhere:** app protocol + sim version + arena data fingerprint (MR3/MR6), so mismatched devices are
  refused up front with a reason, never quietly wrong.

## 9b. Host migration (scoped 2026-10-08, owner asked; O5b BUILT 2026-10-09, see the end of this section)

Today a host quitting ends the match for everyone with results (vanilla had no migration). Promoting a client is a
**medium-to-large change, not a rewrite**: every client already runs the whole match (puppets on the host's poses),
so the new host continues from its own world. What it's missing (code survey, file:line in the thread memo):
1. **Authority switch:** `PlayerEntity.NetClient` (static, set in `MatchStart.cs`) gates damage, respawns, pickups,
   kill drops and spawner items: flip it off at promotion (hits in flight the old host never confirmed are lost).
2. **Bots:** clients see the host's bots as puppets (`MatchClient.ForSeat` drops Bot + BotLevel): START must carry
   each bot's level, then the new host turns them into real `PlayerAi` mid-life (fresh targets/paths).
3. **Item spawners + kill drops:** cooldowns/counts and drop despawn timers aren't synced: add them to the snapshot
   (or a handoff message), and reset `ItemSync` ids on every device.
4. **Objective state (biggest risk):** octolith carrier, node capture, Prime Hunter, `Time[]`/`TeamTime[]` run
   locally on clients from puppet poses and aren't in the snapshot: if the promoted copy differs, scores/objectives
   jump mid-match. Fix: put objective state in the snapshot (also helps O5 "all modes on clients").
5. **Network:** clients only talk to slot 0 today. Needs a fixed successor order (lowest slot still alive), a
   "host is gone" decision (silence + server confirms the host's websocket dropped), and the server re-pointing the
   relay room's host slot; every device re-sends to the new host. Remote humans already run as `HostRemote` seats, so
   their inputs plug straight in; the old host's seat is dropped (`DisconnectSeat`).
6. Fine as is: match clock/state (snapshot), Echoes crates/breakables (off in network matches), replays (network
   matches aren't recorded yet), RNG (clients follow snapshots; the old host's dice can't be continued exactly).

Suggested order: after O5 (objective state in snapshots is O5 work anyway), then items 1-3 + 5 as "O5b", tested with
`-onlinetest hostleave=<s>` (host quits, the others finish with matching scores). **Baseline built (2026-10-08):**
`-onlinetest hostleave=<s>` today checks the vanilla-like behaviour (no migration): the host quits (BYE + leave the
game), every joiner's match ends within 2.5 s with "(the host left)" and the server tells them the game closed. PASS
`players=3 bots=1 time=30 hostleave=12` (also with `delay=40`): both joiners ended in the same millisecond. O5b turns
its pass rule into "the others finish with matching scores". Dedicated servers (O11) remove the
problem for public matches.

**O5b, built 2026-10-09 (Netcode thread), items 1-3 + 5 (4 was O5's `NetObjective`):**
- **Server (0.5.0):** in a started game, the host leaving (`leave_game`) or its websocket dropping calls
  `HubGames.MigrateHost`: the successor is the lowest remaining relay slot whose player is online; the old host's seat
  and relay slot are removed (nothing is forwarded to it), `Game.Host` moves, and every member gets
  `member {slot: <old host>, joined: false, hostSlot: <successor>}`. Nobody online left: the game closes as before. A
  lobby (not started) still closes when its host leaves. Relay slots aren't renumbered. WebSocket keepalive 5 s with a
  10 s timeout, so a dead host's socket is noticed in about 15 s. Selftest: 8 `migrate:` checks.
- **Every device** (`NetMatchSession`): sends to `HostSlot` (was always slot 0). On the server's `hostSlot` the
  successor promotes itself (`Promote`: the START settings it kept become its host template; the other humans become
  its joiners, waiting for their REHOST), the rest point at the new slot, drop the old snapshots and send `REHOST`
  (their seat) every 200 ms until the new host's first snapshot. A BYE from the host (or silence) waits up to 15 s for
  the server's word before the match ends with "(the host left)", only when the server is 0.5.0 or newer.
- **The new host's world** (`CampaignHost.PromoteToHost`): `NetClient` off (it now decides damage, respawns, pickups,
  drops, spawners, objectives), the host-handoff block of the last snapshot applied (spawner active/cooldown/count +
  drop despawn timers, `HostHandoff.cs`), `ItemSync.TakeOver` (its client item ids become the host ids, so nobody
  resets), the old host's bots become real `PlayerAi` (`PlayerEntity.NetBecomeBot`, personality reloaded), the old
  host's own seat is dropped (`DisconnectSeat`; the others follow from the snapshots).
- **Tests:** `-onlinetest hostleave=<s>` (BYE + leave) and `hostcrash=<s>` (no BYE, socket dies): PASS = the
  successor (lowest relay slot) took over, every other joiner followed, all finished with the same scores, the old
  host's seat is out, the bots are bots. The tool takes `SECONDHUNT_SERVER_DLL` to run a server build other than
  the shared Debug one. PASS 2026-10-09 (PC, server 0.5.0 build): quit and crash
  with 3 devices + a bot, both again at 75 ms + 5% loss + 40 ms jitter, 4 devices + 2 bots, 4 devices at 75 ms + loss,
  Capture on Data Shrine, `turn=local`; `-replaygolden` 9/9 unchanged. Fixed on the way: the driver could see the
  move before the promotion (the count is now bumped last), a host move after GameOver is ignored (`MatchOver`: the
  first host to leave its results would otherwise hand a finished match on), the old host's in-flight snapshots
  reaching the new host are dropped quietly.
- **After the match:** the game's host card follows the new host (`OnlineMatch.Game.Host`), so the menus' RESULTS ->
  PLAY AGAIN opens the new host's lobby, the server lists the game again under it, the others come back, new players
  join it (they get the new host's slot from the game card), and its START plays the rematch without the old host.
  Each match's driver counts host moves from its own start (a rematch doesn't follow the last match's move again), so
  the rematch can migrate again. `-migratemenutest` (live): host quits mid-match -> PC ONE takes over -> RESULTS ->
  PLAY AGAIN -> PC ONE's lobby, listed under it -> PC TWO back + a new PC THREE joins -> rematch, 4 seats, no old host.
- **Limits (by design):** hits in flight the old host never confirmed are lost; the RNG can't continue the old host's
  dice (item 6); a migrated match isn't saved as a replay (a replay is the host's recording from the first frame and
  that host is gone; the guests don't wait for one either).

## 10. 8 players (owner, 2026-10-08)

Possible, as a recomp extra; vanilla matches stay exactly vanilla. What it touches (survey 2026-10-08):
- **MphRead:** `PlayerEntity.MaxPlayers = 4` and about 50 fixed 4-slot arrays (`GameState` scores and standings,
  `PlayerEntity._players`, node/teleporter/area-volume slots, bot AI slot hits, HUD team counts), per-player bit masks,
  and rules scaled by `PlayerCount > 2 / > 3` (respawn reveal times, effects). Sized by `MaxPlayers` = 8; rules for
  5-8 players follow the 4-player values.
- **Must hold:** every 1-4 player match stays byte-identical (the replay goldens in `tests/replays/` prove it).
- **Arenas:** spawn points per arena (census like `MatchArenas`); small arenas may get a recommended max.
- **Screens:** lobby seats, HUD scoreboard, RESULTS (vanilla shows 4 rows: scroll or two columns), license counts.
- **Netcode + relay:** snapshots for 8 seats (~0.5 KB), host upload ~1 Mbps at 30 Hz: fine on home internet.
- **Owners:** MphRead upstream edits are host-guarded and additive (rule 2 with Vanilla accuracy); the screens are
  Vanilla multiplayer menu's; the netcode is shared with Multiplayer. Milestone P8 below.

**BUILT 2026-10-09/10 (Netcode P8), PC-verified, not installed yet:**
- **Engine:** MphRead's per-player tables sized `PlayerEntity.SlotCapacity` = 8 (`PlayerSlots.cs`); `PlayerEntity.MaxPlayers`
  stays 4 unless the match has 5-8 seats (`MatchStart`), and every 5-8 change is guarded by `MaxPlayers > 4`, so 1-4
  player matches are byte-identical (`-replaygolden` 9/9 exact). FFA Nodes' "no team" value is `NodeDefenseEntity.NoTeam`
  (4, or 8 with 8 slots); team Survival lives scale with team size (`GameState.TeamLives`).
- **Netcode:** LAN protocol 11 / online match protocol 10 (`SeatLimit`, the LAN beacon carries it); `NetObjective`
  counts seats; a whole 8-seat snapshot that passes the transport's payload limit is sent without the online extras and
  the handoff block, and that trimmed base is what the joiner's next deltas build on (`NetMatchSession`, biggest 1206 B).
  `-lantest players=8`, `-onlinetest` 8 seats (Battle, Capture, Nodes, teams, bot mixes), `-onlinecreatetest seats=8`
  PASS; hostleave with 8: 2/3 (one end-of-match straggler, open).
- **Sweep:** `-matchsweep bots=7` 275/275 (`extract_out/multiplayer/match_sweep_8p.txt`); one debug assert seen in 8p
  only (`w != 0` in `PlayerAiData.UpdateAggro`, handled like the ROM), not root-caused.
- **Arenas:** `MatchArenas.RecommendedMaxPlayers(arena, mode)` = the mode's spawn points, clamped 4-8 (`-matcharenas`
  prints it). Advice for the lobby, not a limit; not owner-approved yet. Table in docs/MULTIPLAYER.md section 4.9.
- **Screens:** RESULTS scrolls for places 5-8 (owner pick, `MultiplayerMenu.ResultsRows.cs`); the HUD scoreboard (Pause
  hold and GAME OVER) for 5-8 seats is the compact one-screen layout (owner pick 2026-10-10, `PlayerScoreboard8.cs`);
  vanilla LAN lobby: seats 5-8 as two rows of smaller seats, only once a 5th seat is used (owner pick B 2026-10-10,
  `MultiplayerMenu.Seats8.cs`; back to the vanilla look at 4 or fewer; teams 1-4 each past 4); license: seat ranks for 8 seats, Head Shot opens at 4+ players;
  ONLINE PLAY lobby: 5-8 seats as half-height cards (`OnlineMenus.Seats8.cs`); dev launcher + beta MULTIPLAYER bots up
  to 7; replays check seats 5-8 (`ReplayStateParts.Seats4To7`). Left: the online details page shows 4 seats + "+N more seats".

## 11. Milestones

| # | What | Done when |
|---|---|---|
| O0 | This plan | Owner decisions in (2026-10-08) |
| **O1** | Identity + server skeleton: key pair, friend code, name#tag, signed login over a WebSocket, SQLite, the license summary upload | **BUILT 2026-10-08 (PC):** `SecondHuntServer --selftest` 35/35 PASS, incl. most of O2 (friend + rival requests, mutual code entry, search, findable switch, presence pushes, block, licenses, second-device kick, restart). Nothing in the app calls it yet |
| O2 | Friends, rivals, requests (code / name search / from a name), recent players, block, privacy setting, open anyone's license | Selftest covers each; Tools harness with real devices later. **Server side DONE 2026-10-08:** recent players added (selftest 67/67). Left: the device's own recent list. **Device license upload built 2026-10-09** (Online play menus, `OnlineMenus.License.cs`): `set_license` as the menus' connection comes in, read back by the HUNTER LICENSE view (section 13) |
| **O3** | Presence, hosted games (FRIENDS / RIVALS / PUBLIC), open games list, invites, parties, join -> a match through our UDP relay | First relayed match PC <-> PC, then Odin <-> Samsung on two networks (phone hotspot). **PC PART PASSED 2026-10-08:** server games + relay wired (selftest 54/54: visibility friends/rivals/invited/public/blocked, version + full refusals, member events, host leave closes, lobby drop, presence lobby/match, raw UDP relay fan-out/unicast/keepalive/unknown token); `NetMatchSession` + `RelayTransport` + `MatchNetDriver` over `IMatchNet`; `-onlinetest` PASS: 3 devices + bot, 4 humans (Survival), 120 ms RTT, a joiner leaving mid-match (seat dropped, others play on), 4 humans + leaver + 60 ms RTT (Bounty), 2 + bots Capture. **Later 2026-10-08:** parties (section 6 defaults) + recent players + the mods field on the server (selftest **67/67**); `-onlinetest hostleave=` PASS (baseline for O5b); **app path built + on the Odin 12:58** (`OnlineMatch` in Core, `MatchOnline.cs` + dev-menu ONLINE TEST: HOST / JOIN, match screen `--es net online`). **FIRST DEVICE MATCH PASSED 2026-10-08 13:15 (Odin <-> PC):** server on the PC's LAN IP, the Odin hosted (ONLINE TEST: HOST, Battle, Data Shrine, 1 min, 2 bots), the PC joined with `-onlinejoin`; the Odin's key login, `ws://` and the UDP relay all worked on Android (relay ping 8-24 ms), 120 fps on the Odin while hosting, and both devices ended with the identical scoreboard (Samus 0/0/1, Sylux 1/1/3, Kanden 1/3/5, Spire 4/5/3). Left: Odin <-> Samsung on two networks (needs O4's internet server), parties/invites UI (O9) |
| O4 | Live server: owner rents the VPS + domain, we deploy | Odin and Samsung play through the US server |
| O5 | Internet netcode: N3 prediction, 30 Hz + delta, all modes on clients, leaving/rejoin, N4 lag comp, Cloudflare TURN relays | A match at 150 ms simulated ping feels right on the Odin. **BUILT 2026-10-09 (Netcode O5), PC-verified:** all six parts (section 9) + TURN (decision 18, section 7); online match protocol 6; server 0.4.0 live (rejoin + TURN creds, key not installed yet). Sweep PASS: `-replaygolden` 9/9, `-replaynettest`, `-lantest` 2/3/4, `-netlobbytest`, menu tests, `-onlinetest` all 12 modes, hostleave, leave, looks, rejoin, `turn=local` (+ turnkill fallback), 150 ms + 5% loss + 40 ms jitter with 3 and 4 devices. Left: the device test, the TURN key, the lag-comp cap (9 or 12 frames). **O5b host migration BUILT 2026-10-09** (section 9b: the lowest live relay slot takes over a started match; online match protocol 7, server 0.5.0 live); left: its device test |
| **O6** | Find Game: queue, every mode, region + rank filters, arena vote, parties, ADD RIVAL | 4 test clients matched and played. **BUILT 2026-10-09 (Social track: Find Game):** server 0.3.0 (`FindRules.cs`, `HubFind*.cs`, selftest **126/126**), menus FIND GAME + VOTE FOR AN ARENA + party panel (`OnlineMenus.Find.cs`, built in the existing ONLINE PLAY style, no mockups), `-findgametest` **PASS** (a party of 2 + 2 singles, one match of 4, vote, lowest ping hosted, all four scoreboards equal) and `-findgamemenutest` **PASS** (menus host, then menus join). Rank filter left out (decision 15). Left: device test |
| O7 | Chat: presets, typed + filter, mute, report, admin tool, the legal notices | Before strangers can type to each other. **BUILT 2026-10-09 (Social O7):** server 0.5.0 LIVE 19:40 (chat, word filter on chat + names, new-identity limit, rate limits, report evidence, bans, admin tool; selftest 180/180); lobby CHAT page + strip, REPORT A HUNTER, license MUTE (`OnlineMenus.Chat.cs`, `-chattest` PASS live); in-match chat lines + Select quick-message wheel + touch CHAT (`App/Campaign/MatchChat.cs`); legal DRAFTS in docs/ (`LEGAL_AGE_NOTICE_13_PLUS.md`, `PRIVACY_NOTICE.md`, `CODE_OF_CONDUCT.md`, `UK_ONLINE_SAFETY_ACT_RISK_ASSESSMENT.md`) waiting for the owner. Left: owner approval of the drafts (and wiring the approved 13+ text into `ShowNotice`), device test |
| O8 | Voice: Opus, push-to-talk/open mic, defaults (strangers off, friends on), per-player mute | Lobby + match on the Odin + Samsung. **BUILT 2026-10-09 (Voice O8, section 8):** Concentus Opus over the match link (0x41), Android + Windows capture, push to talk (pad R3, keyboard V, touch TALK / TAP TO TALK) + open mic, per-player voice, volume; `-voicetest` + `-voicetest live` PASS; on the Odin + Samsung. Left: the Odin <-> Samsung device test, one Windows run with a real microphone |
| O9 | **Menus built 2026-10-09 as the owner's modern design (Online play menus thread: hub, browse, create, lobby with teams / hunter changes / ping / ready, rematch, friends and rivals, find a hunter, Hunter License view, online settings; RESULTS counts online matches; REPORT)** instead of: ONLINE PLAY door -> vanilla pages 52-61 (FIND GAME, FRIENDS AND RIVALS, EDIT FRIENDS AND RIVALS, keypad 61) + new pages (requests, search, recent, party, settings); roster RE first | With Vanilla multiplayer menu |
| O10 | Replays carry each seat's player id + name + license snapshot: tap a name in REPLAYS -> license, add friend/rival. **Replay format change DONE 2026-10-09 (Replay mode thread, format 17, `ReplayFile.Identities`): the local seat's friend code + name + a full `HunterLicense` JSON snapshot, saved at EndReplay. Other seats carry none yet (the license isn't networked).** Left to this thread: read `Identities` and wire the REPLAYS details page's name tap -> license / add friend / add rival **BUILT 2026-10-09 17:50 (Replays track): LAN + online matches recorded by the host exactly (format 19 NetEvents), guests get the host's copy at match end (ReplayLink side channel), every seat on this build carries friend code + NAME#tag + license numbers; REPLAYS rows show the names, a seat on the details page opens the license as it was (V_C) with HEAD TO HEAD, SEE CURRENT, ADD FRIEND / ADD RIVAL. PC checks pass; device test with the owner pending (docs/REPLAY_MODE.md R6).** | REPLAYS details page (name-tap-everywhere) |
| P8 | 8-player matches (section 10), LAN and online | 1-4 player goldens unchanged; 8-player sweep passes. **PC-DONE 2026-10-10** (section 10): goldens 9/9 exact, sweep 275/275, LAN 11 / online 10; owner picks built (compact HUD scoreboard, vanilla lobby two rows past 4); left: install + device test |
| O11 | Later: dedicated match servers (the host role as a headless server process) | Owner call; needs game data on the server |

## 12. Open decisions (not blocking O1-O3)

- **O-1 roster cap:** vanilla's 60 slots (57 locks, oldest dropped) for friends + rivals, or no cap for friends
  (modern) while the vanilla pages show them 6 rows at a time. Recommendation: no silent drops of friends; keep 60
  for rivals as vanilla.
- ~~O-2 ranking~~: decided (decision 15): no rank matching for now. Was: vanilla ranking points online (from matches against people). With player hosts a cheater could
  fake results; a ranked queue waits for dedicated servers.
- ~~O-3 name filter list~~: decided (decision 14). Was: which word list (an open-source list + owner additions).
- **O-4 domain name** for the server (e.g. a `secondhunt` domain).
- ~~O-5 mods in friends-only games~~: decided (decision 12): allowed with an identical set.
- ~~O-6 relays (O5)~~: decided (decision 18): Cloudflare Realtime TURN first, Chicago relay as the fallback, 900 GB monthly cap. Was: Cloudflare TURN vs our Chicago relay only.

## 13. Protocol (O1, the first piece; grows with each milestone)

WebSocket at `wss://<server>/v1/ws`, one JSON object per message:
- server -> `{"t":"challenge","nonce":"<base64 32 bytes>","server":"<version>"}`
- client -> `{"t":"hello","key":"<base64 SubjectPublicKeyInfo>","sig":"<base64 sig of 'secondhunt-login'+nonce>","app":"<version>","proto":1,"name":"KANDEN"}`
- server -> `{"t":"welcome","code":"123456789012","name":"KANDEN","tag":"4821"}` or `{"t":"error","code":"...","text":"..."}`
- then requests `{"t":"<kind>","id":<n>,...}` answered by `{"t":"reply","id":<n>,...}`, and server pushes
  (`{"t":"event",...}`) for requests, presence and invites.

(The code is the reference: `OnlineProtocol.cs` lists every kind. The key is the raw 65-byte P-256 point, the
signature IEEE P1363, replies are typed (`ok`, `player`, `game`, ...) and pushes are `ev_*`.)

Games (O3): `host_game {info}` -> `game {game, relay:{host,port,token,slot:0}}`; `list_games` -> `games` (what this
player may join: not started, not full, public / invited / host's friend or rival as the host allows, nobody in it
blocked either way); `join_game {gameId, version}` -> `game` with the next free relay slot, errors `not_found`,
`started`, `full`, `version`, `already_in`; `leave_game` (the host leaving closes the game: `ev_game_closed`);
`start_game {started}` (host; presence `match` / back to `lobby`); `invite {code}` (friends and rivals:
`ev_invite`); `update_game {info}` (host); `info.mods` / `join_game {mods}` (section 8b, error `mods`). Pushes: `ev_member {gameId, player, slot, joined}` to the others in the
game; `ev_presence` carries `gameId` when the viewer may join that game. A lobby member whose connection drops leaves;
in a started match the seat stays until the host goes back to the lobby or the relay room is quiet for 60 s. Relay:
UDP, `--relay-port` (default 47780), RelayWire format.

Recent players + parties (O2/O3): `get_recent` -> `players`. `party_invite {code}` (leader; friends/rivals; makes the
party if needed) -> `ok {done: online}` + `ev_party_invite {from, partyId}` to them; `party_join {partyId}` -> `party
{party: {id, leader, members}}` (errors `not_found`, `full`); `party_leave` -> `ok`. Pushes: `ev_party {party}` to
every member on each change (`party: null` = you're out), `ev_party_game {gameId}` when the leader hosts or joins.

License (O2, 2026-10-09): `set_license {license}` carries a JSON object of the Hunter License card's numbers under
`HunterLicense`'s own property names (`V`:1, `RankPoints`, `RankLevel`, `WifiWins`, `WirelessWins`, `OnlineMatches`, `LocalMatches`,
the kills, streaks, play seconds in whole minutes, `FavoriteHunter`/`Weapon`/`Mode`, `LuckyArena`, `BeatGame`, `Completed`), so the
viewer reads it back as a `HunterLicense` (clamped to what the card shows). Never in it: the name (the server's word-filtered one is
shown) or the LAN address the license file keeps. Uploaded when it changed since the last upload of that run.

Report (server 0.2.0, 2026-10-09): `report {code, reason}` (`name` / `cheating` / `other`) -> `ok {done}` (false = the same
reporter, player and reason again within a day); errors `bad_reason`, `not_found` (nobody, or yourself). Kept in the `reports`
table with the player's name#tag at the time, for the owner: `sqlite3 /var/lib/secondhunt/secondhunt.db "SELECT * FROM reports
ORDER BY at DESC"`. A server before 0.2.0 drops a connection that sends a kind it doesn't know, so clients check the
challenge's `server` version first (`OnlineClient.ServerAtLeast`). The menus' REPORT sends `name` only on a server older than 0.5.0; on 0.5.0 it opens REPORT A HUNTER (reasons below).
`delete_profile` (server 0.2.0, the "delete my online profile" of section 8): out of any game and party, friends and rivals get
`ev_removed`, then the player, all its links (friends, rivals, requests, blocks both ways) and recent players are deleted and
the server closes the connection after the `ok`. Reports stay. The same key logging in again starts a new profile; the menus
also delete the device's identity (ONLINE SETTINGS > DELETE ONLINE PROFILE) and leave ONLINE PLAY.

Find Game (O6, server 0.3.0, 2026-10-09; clients check `ServerAtLeast("0.3.0")` first): `ping {ms}` (the client's measured
route, 0 = unknown -> 80) -> `ok`; `find_start {modes[], nearby, version, arenas{mode: [arena names]}, pingMs}` -> `ok` (the
leader searches for the whole party; errors `in_game`, `searching`, `not_leader`, `party_busy`, `bad_mode`, `version`);
`find_cancel` -> `ok {done: was searching}`; `find_vote {arena}` -> `ok` (errors `bad_arena`, `no_vote`). Push `ev_find {state
searching|vote|host|join|game|cancelled, waited, queued, limitMs, nearby, findId, mode, players, arenas, seconds, votes, arena,
hostCard, gameId, reason}`. The chosen host just sends the normal `host_game` (`find` optional): the server makes it the match's
game (max = the match's size, not listed, the others invited) and pushes the others `game {gameId}`.

Server 0.4.0 (2026-10-09, live): **rejoin** (Netcode O5): `join_game` for a started game this player still has a seat in (a socket drop keeps it) returns the same relay slot with a new token; `join_game {gameId: ""}` = "my current started game" (after an app restart; `not_found` if none). **Cloudflare TURN** (decision 18): `turn_creds` -> `turn {useRelay, reason, urls, username, credential, ttlSeconds}` for a player in a game (lobby or started), only `turn:...?transport=udp` URLs from Cloudflare's generate-ice-servers (ttl 7200); useRelay with reason `no TURN key` / `monthly TURN cap reached` / `not in a game` / `TURN unavailable`. `turn_usage {bytes}` -> `ok`: counted per UTC month in the database, at most 200 KB/s since that player's credentials or last report; past `--turn-cap-gb` (900) new credentials answer useRelay. The key: `/etc/secondhunt/turn.env` on the VPS only (deploy/DEPLOY.md section 4). Health: `ok 0.4.0 online N turn X.XX GB`. Clients check `ServerAtLeast("0.4.0")` before `turn_*`.

Server 0.5.0 (2026-10-09, live; Social O7): `chat {preset, text}` (`ChatReq`; preset -1 = typed) -> `ok`; errors `no_game`, `bad_text`, `chat_rate`, `chat_locked` (text says how many matches to go), `chat_muted`. Push `ev_chat {gameId, code, name: "NAME#0421", preset, text}` (`ChatEvent`, text already masked) to everyone in the sender's game who may see it, the sender included; never on `OnlineClient.Events` (it goes to `OnlineClient.Chat`, a `ChatLog`). `welcome` gains `chatLeft` (matches until typed chat reaches strangers) and `chatMutedUntil` (unix seconds; max = permanent). `report` reasons add `chat` and `voice` (`ReportReason.SinceServer` gating: an older server drops the connection on an unknown reason); errors `too_many_reports` (10 a day). `PlayerCard.slot` = the relay slot in game rosters (Voice O8). A banned key gets `error banned` before login, and an online one is kicked within 15 s. Clients check `ServerAtLeast("0.5.0")` (`ChatReq.SinceServer`) before `chat`.

Server 0.6.0 + match protocol 11 / LAN 12 (Extra pickups + Extra characters rules, 2026-10-10, deployed 15:08): `GameInfo` gains
`extraPickups` / `extraCharacters` (default true = an older host; HubGames.Clean keeps characters only with pickups);
`find_start` gains `noExtraPickups` / `noExtraCharacters` (ONLINE SETTINGS filters); the server skips open games a searcher
filters (`FindRules.ExtrasFit`) and a found match bans what any ticket filters (`FindEvent.noExtraPickups/...` on the host
event, forced into its GameInfo). Match settings carry `ExtraCharacters` (LAN 12 / online 11); the LAN beacon carries an extras
byte (bit0 pickups, bit1 characters; absent = both on). Clients filter BROWSE and quick JOIN themselves too.

Match protocol 10 (P8, 2026-10-10; LAN protocol 11): up to 8 seats. The host passes `SeatLimit` (people + the
template's bots) and refuses joiners past it; seat arrays and snapshots carry 8 seats; a whole snapshot too big for
the relay (8 seats in Capture/Nodes, ~1.5 KB) goes without its extras and handoff blocks, and the joiner's deltas build
on that trimmed base. (8 and 9 are the Trooper's abilities and the Pirate Blaster, see their threads.)

Match protocol 7 (O5b host migration, 2026-10-09; 6 and older are refused): every full snapshot carries a 5th
delta block, the host handoff (item spawners: id, active, cooldown, count; dropped items: id, despawn timer), kept by
clients and applied only if they become the host; new message `REHOST` (12, client -> new host: header + seat byte)
after the server's `member {hostSlot}` (`MemberEvent.HostSlot`, null = no change; older servers never send it).

Match protocol 6 (Netcode O5, 2026-10-09; 5 and older are refused with "update the game"): protocol 5 made snapshots
per-joiner deltas (type 6: tick, base tick, input ack, then per-seat group masks + changed groups and blocks for items,
objective and, from 6, the seat extras, each "same", literal or XOR runs; a joiner's acked tick is the base, a literal
when there's none) at 30 Hz (20 on a weak link), and inputs carry the newest snapshot tick seen (ViewTick: lag
compensation) + the last 4 inputs. A JOIN from a player of the running match (its slot, or its name after it left) is a
rejoin: its input sequence restarts, its next snapshot is literal, START is re-sent. A 0xE7 first byte is
HybridTransport's (OFFER relayed address / PROBE / ACK nonce), never the session's.

Match protocol 4 (the relay's packets, `NetMatchSession`, 2026-10-09; older apps are refused with "update the game"): the lobby
and the rematch. Host -> each joiner `LOBBY` (its seat, the match so far, then per seat: relay ping ms, choosing a hunter, back
from the results, bot) on each change and once a second from the session's own timer while there's no match (also while the
host is on RESULTS, so joiners know it's there; a joiner gives up after 30 s without it); joiner -> host `LOOK` (hunter, character + skin, choosing, its relay ping) on each change and
once a second while it's in the lobby; host -> joiner `KICK` (reason) for one not back from the results when the host starts
without it. After a match the session stays (`OnlineMatch.EndMatch`, the match screen's hold lets it be): RESULTS' PLAY AGAIN
takes the host back to the lobby (`start_game {started: false}`: listed again) and joiners into it; each joiner counts as back
on its first `LOOK`. A joiner whose match ended drops snapshots and START until the host's next `LOBBY` (the old match's last
packets aren't the next match's); START resets its snapshot tick and count, the host's START each joiner's input sequence.

Files: `src/MphRecomp.Core/Online/` (namespace `MphRecomp.Online`: identity, friend codes, names, messages, client;
no MphRead types, so the server can link them) and `src/SecondHunt.Server/` (the server, its storage, `--selftest`).
