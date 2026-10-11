# Multiplayer: local wireless + online, like vanilla

**Owner GO 2026-09-30.** Thread owner: session "Multiplayer (local + online)" (see `COORDINATION.md`).

Vanilla MPH has both local wireless play (multi-card, single-card download play, bots) and online play (Nintendo Wi-Fi
Connection: friend codes, friends and rivals, worldwide matchmaking). The recomp keeps that structure. Local and online
are two ways of connecting to one shared netcode, not two projects.

## 1. Decisions

- **Netcode model (owner, 2026-09-29):** host-authoritative. The host device runs the real MphRead simulation for
  everyone. Clients send inputs, predict their own movement and get corrected. Lag compensation for hits. MphRead's
  floats stay; there is no determinism rewrite. Only recomp players play together: no real-DS, WFC or Wiimmfi interop.
- **Local multiplayer is wanted too (owner, 2026-09-30),** not only internet play.
- **Single-card play can't be reproduced.** It would send game data to players who don't own the game.
- **Every player imports their own ROM.** USA rev 0 and rev 1 can play each other: everything a match uses (arena
  entities, models, collision, MP mode objects, bot AI, weapon/hunter tables, SFX) is byte-identical between them
  (docs/ROM_VERSIONS.md section 3e). The LAN handshake treats both as "MPH USA" (`MatchLan.LanRomKey`, 2026-10-04) and
  still refuses other regions. Later it should compare app/protocol version + mods + a hash of the match-relevant files
  rather than the ROM revision.
- **The ROM's multiplayer menu pages stay with the UI session.** They get wired to this code later, the way file select
  was wired to the campaign saves.

## 2. Phases

| Phase | What | State |
|---|---|---|
| 1 | Offline match host with bots: 7 modes + team variants, 26 retail arenas, MphRead's own rules and bot AI | **Built, PC-verified** (245/245 playable pairs). Not yet on the Odin |
| 2 | Netcode: host + clients over UDP (section 4) | **N0 done (PC):** remote seats on the host, snapshots, client puppets; two-process `-netloop` match over UDP works (section 4.1). Next: N1 on devices |
| 3 | Local wireless: same Wi-Fi (UDP broadcast / NSD discovery), Wi-Fi Direct, local-only hotspot | **Same Wi-Fi WORKS on devices (2026-09-30):** Odin hosted, Samsung S23 joined, full Battle start to end (`Net/LanSession`, `MatchNetDriver`, launcher Host/Join LAN match). **2026-10-09: every mode + teams + bots alongside joiners, objective state from the host (`Net/NetObjective`, MphRead `OctolithFlagNet`/`NodeDefenseNet`), LAN protocol 8; host takes up to 3 joiners (vanilla menu MULTIPLAYER > LAN; the beta MULTIPLAYER screen stays host + 1 joiner, any mode, the host's bots). PC PASS (`-lantest players=2..4`, `-onlinetest` all 9 objective modes); no device test yet.** Wi-Fi Direct / hotspot not started |
| 4 | Online: our own small server (friend codes, presence, game list, relay) | Needs the owner's hosting decision |
| opt | Split-screen on one device (2-4 Bluetooth controllers, TV out) | Idea only |
| friends | Friend system (section 5) | Researched, not started |

## 3. Phase 1: what exists

- `src/MphRecomp.Core/Multiplayer/`
  - `MatchSettings`: seats (hunter, recolor, team, bot, bot level 0-2, nickname), mode, arena, rule overrides (null =
    the mode's own default from `GameState.Setup`), `Quick()`, `Validate()`, team auto-assignment.
  - `MatchArenas`: the retail arenas are MphRead's room ids 93-118. `Census()` counts PlayerSpawn / OctolithFlag /
    FlagBase / NodeDefense entities on the entity layer each mode loads (`Metadata.GetMultiplayerEntityLayer`), and
    `Supports(mode)` follows from it: Capture needs 2 flags + 2 bases, Bounty 1 + 1, Nodes/Defender a node. This is the
    ROM's own data; 67 mode x arena pairs are not playable.
  - `MatchStart`: `CampaignHost.StartMatch(settings, ...)` (partial of the campaign host).
  - `MatchStatus`: read-only scoreboard over `GameState`'s per-seat arrays.
- Android: `MatchSession.cs` (partial `CampaignRenderer`): `--es match <mode> [arena] [bots] [botlevel] [time]
  [points]`, scoreboard in the status text, results held until A/Start. Dev menu: "Multiplayer (early)".
- Tools (build with `-o MphRead.Tools/bin/Multiplayer/net9.0`): `-matchsim`, `-matchsweep`, `-matcharenas`. Sweep
  results: `extract_out/multiplayer/match_sweep.txt`.
- Measured on the PC: a 4-player match costs about 0.2 ms per simulated frame.
- Upstream MphRead bugs fixed along the way: `mp1/mp6_CTF_node.bi)` path typo (Capture on Data Shrine / Head Shot threw
  at load); `Scene.GetFlagBaseEntities` read the FhBomb list, so the Bounty carrier never saw the base locator.
  Known and harmless in Release: `Debug.Assert(w != 0)` in `PlayerAiData.UpdateAggro` (about 15 hits per full sweep).

## 4. Netcode design (from the 2026-09-30 code survey; line numbers as of that date)

**Architecture: listen-server host, clients run the same arena Scene with puppets.** The client starts the same match
with `PlayerEntity.MainPlayerIndex` = its own seat. Every other seat is a puppet whose state is overwritten from
snapshots, so the whole Android pipeline (draw list, HUD, gun, audio, Main-relative logic) works unchanged.
Rejected: streaming the host's draw lists (built for Main only, advances effects as it goes, megabytes per second per
client) and video streaming (latency). Lockstep conflicts with the owner's decision (static shared Rng, float
differences across ARM64/x64).

**One Scene per process.** MphRead state is static everywhere (`PlayerEntity._players`, `GameState`, `Rng`, `Sfx`,
`CameraSequence.Current`). PC tests need two processes, or two collectible `AssemblyLoadContext`s.

### 4.1 Built so far (N0, PC-verified 2026-09-30)

- **Host:** remote seats (`MatchPlayer.Remote` -> `PlayerEntity.HostRemote`) take their own buttons and an ABSOLUTE
  yaw/pitch target (`NetInput`, 11 bytes) through `CampaignHost.SetRemoteInput`; `ProcessHostInput` turns the target
  into the frame's aim delta. `NetAimEpoch` (bumped by Spawn/Reposition) makes stale views after a respawn/teleport
  harmless. `-matchsim remote`: a scripted remote seat lands within 0.1 deg of its sent view on 1548/1549 frames.
- **Snapshot** (`Net/Snapshot.cs`, ~59 B per seat + 10 B header; 247 B for 4 seats = 14.5 KB/s at 60 Hz): per seat
  flags, position, speed, facing, up, yaw/pitch, aim epoch, health, weapon, weapons, ammo, the buttons it held (bots
  included), points/kills/deaths; match time/state.
- **Client** (`MatchClient.ForSeat`, `CampaignHost.ApplySnapshot`, `PlayerEntity.NetApply`): same match, own seat
  local, every other seat a puppet driven by the relayed buttons + view (so MphRead animates it with its own sounds
  and effects) and pulled onto the host pose each snapshot. `PlayerEntity.NetClient`: no damage starts on a client
  (TakeDamage guard) and no client-side (re)spawns; the host's health drops play through TakeDamage there, its spawns
  through Spawn. The local player keeps its own predicted pose unless it drifts > 0.5 units.
- **Items** (`Net/ItemSync.cs`, in snapshots every 4th frame or on change, ~17 B per item): the host numbers every item
  (spawner items keep their spawner id, kill drops -1) and sends the list; a client never spawns or drops items and its
  puppets never pick up, it creates/removes items to match. Its own player still picks up at once (feel); an item it
  just took isn't re-created for 1.5 s. Snapshot with items ~346 B (20 KB/s at 60 Hz). Protocol 3.
- **Kill messages on clients:** the snapshot carries each seat's last hit (attacker seat, beam, headshot/burn/deathalt)
  and the client's TakeDamage replays it, so "X killed you (weapon)" is right.
- **`-netloop`** (two processes, UDP localhost, lockstep, `delay=` frames): 60 s Battle, the client (Kanden) wins with
  4 kills, identical scores both sides. Drift before correction, own player / puppets: RTT 0: 0.05 / 0.02 mean,
  0.58 / 0.75 max. Own player pulled back per 60 s: RTT 0 ms 7, 33 ms 17, 100 ms 52, 200 ms 126 -> fine for local
  wireless; internet needs N3 (reconciliation).

### Input (host side)
- `ProcessHostInput` (PlayerInput.cs ~2222-2281) feeds only seat 0 (`i != 0` at ~2235; desktop path ~2092 too).
  `Controls` and the aim delta are already per player; only the feeders are static (`HostAimX/Y`, `HostDialogPress`,
  `HostPointerX/Y`).
- Hook: `i != MainPlayerIndex && !player.HostRemote`, per-player aim feed (`HostRemoteAimX/Y`), dialog and movie skip
  for Main only.
- Clients send ABSOLUTE yaw/pitch each tick; the host turns it into the wrapped delta against the player's current gun
  vector. A lost packet then can't leave a permanent aim offset.
- The weapon wheel only works for Main on the host (`UpdateWeaponSelect` runs inside Main's HUD). Clients resolve
  their wheel locally and send a direct weapon key.
- Disconnects: `LoadFlags.Connected/WasConnected` already exist (PlayerProcess.cs ~24-36).
- Main-only gameplay to fix: a blocking camera sequence zeroes aim for every human (PlayerInput.cs ~482). (Locked
  doors open only for Main's shots, but multiplayer arenas have no doors -- owner, 2026-09-30 -- so no fix needed.)

### State vs events
- **Unreliable snapshots, latest wins, 30 Hz LAN / 20 Hz internet:** player pose, flags, timers, animation ids +
  frames, health, ammo, weapons, alpha (~90-130 B per player, mostly private fields, so a new partial
  `Players/PlayerNet.cs` with Capture/Apply); Halfturret; octolith carrier/pose; node progress; moving platforms; match
  time and scores (standings are derived on the client by `GameState.UpdateState`).
- **Reliable ordered events, tagged with the host tick:** shot (end of `BeamProjectileEntity.Spawn`), beam end,
  damage and death (`PlayerEntity.TakeDamage`), spawn, item spawn/pickup/despawn, bomb, octolith, node, prime hunter,
  match start/end. HUD kill messages and most sounds are Main-only on the host, so clients rebuild them from events.
- Projectile motion is predictable from spawn parameters, except homing, ricochet and continuous beams (Shock Coil:
  send start/stop + target).
- Bandwidth: full snapshot ~600 B, delta ~150-250 B, so 5-18 KB/s per client at 30 Hz; input ~13 B per tick (last 4
  repeated) ~4-5 KB/s.

### Client guards (additive, host-guarded)
- A process filter in `UpdateScene` (Renderer.cs ~2945) so item spawners, flag bases, triggers and mode logic run only
  on the host; `NetCosmetic` beams and bombs (no damage); `TakeDamage` and `PickUpItems` guards; `ModeState` host-only.
- `BeamProjectileEntity`, `OctolithFlagEntity`, `NodeDefenseEntity` are not `partial` yet (one-word change each).

### Prediction and lag compensation (later)
- Tier 1: run the real `ProcessPlayer` for Main on the client; blend small errors over ~100 ms, snap on discrete
  changes (alt form, death, teleport, jump pad). Tier 2 (rewind-replay) needs a generated copier of ~200 player fields
  and muted side effects; jump pads/teleporters trigger from their own entities.
- Lag compensation: ~32-tick pose history per player on the host; rewind around the player loop in beam collision,
  splash, ice wave and homing target selection; capped at 200 ms (12 frames, owner 2026-10-09), only for fast/continuous beams.
- `Rng2` is one shared static stream (spread, sway, camera shake for every player). Camera shake feeds alt-form roll
  direction (PlayerCamera.cs ~884 -> PlayerInput.cs ~1184), so a predicting client drifts in alt form. Timers are
  frame-counted: clients step whole 60 Hz ticks only.

### 4.9 8 players (P8, PC-DONE 2026-10-10)

- `PlayerEntity.SlotCapacity` = 8 sizes every per-player table (`PlayerSlots.cs`); `PlayerEntity.MaxPlayers` is 4
  unless the match has 5-8 seats (`MaxPlayersFor`, `MatchStart`). Every 5-8 path is guarded by
  `MaxPlayers > VanillaMaxPlayers`: 1-4 player matches stay byte-identical (`-replaygolden` 9/9).
- Rules for 5-8 follow the 4-player values. FFA Nodes' "no team" = `NodeDefenseEntity.NoTeam` (4, or 8 with 8 slots).
  Team Survival lives scale with team size (`GameState.TeamLives`).
- Net: LAN protocol 11, online match protocol 10 (`SeatLimit`, beacon); `NetObjective` seat count; whole snapshots
  trimmed per joiner when they pass the payload limit (`NetMatchSession` `Joiner.Trimmed`).
- Screens: RESULTS scrolling (`MultiplayerMenu.ResultsRows.cs`), HUD scoreboard 5-8 compact (`PlayerScoreboard8.cs`, owner
  pick 2026-10-10), license seat ranks x8, ONLINE PLAY lobby 8 cards (`OnlineMenus.Seats8.cs`); vanilla LAN lobby 5-8 =
  two rows of smaller seats once a 5th seat is used (owner pick B, `MultiplayerMenu.Seats8.cs`).
- Tests: `-matchsweep bots=7` 275/275, `-replaytest bots=7` (+ `mode=BattleTeams`), `-lantest players=8`,
  `-onlinecreatetest seats=8 bots=6`, `-p8scoreboard`, `MP_SEATS=8 -mpmenu results`.
- Recommended max per arena (`MatchArenas.RecommendedMaxPlayers`: the mode's spawn points clamped 4-8; advice, not a
  limit, not owner-approved):

| Arena (ROM / name) | Max | Lower in |
|---|---|---|
| SANCTORUS / Data Shrine | 8 | - |
| HARVESTER / Harvester | 4 | - |
| PROVING GROUND / Combat Hall | 6 | 5: Nodes, NodesTeams |
| HIGHGROUND - EXPANDED / Elder Passage | 8 | 4: Capture, Bounty, BountyTeams, Defender, DefenderTeams |
| HIGHGROUND / High Ground | 8 | 4: Defender, DefenderTeams |
| FUEL SLUICE / Compression Chamber | 6 | - |
| HEADSHOT / Head Shot | 4 | 4: Battle, BattleTeams, Capture, Nodes, NodesTeams, PrimeHunter |
| PROCESSOR CORE / Processor Core | 5 | - |
| FIRE CONTROL / Weapons Complex | 8 | 4: Capture |
| CRYOCHASM / Ice Hive | 4 | - |
| OVERLOAD / Incubation Vault | 8 | 5: Nodes, NodesTeams |
| BREAKTHROUGH / Sanctorus | 7 | 4: Nodes, NodesTeams, Defender, DefenderTeams |
| SIC TRANSIT / Sic Transit | 8 | 4: Capture |
| ACCELERATOR / Fuel Stack | 5 | 4: Bounty, BountyTeams, Nodes, NodesTeams |
| OUTER REACH / Outer Reach | 4 | - |
| FAULT LINE - EXPANDED / Fault Line | 8 | 6: Bounty, BountyTeams |
| LINE / Subterranean | 8 | 6: Defender, DefenderTeams |
| TRANSFER LOCK BT / Transfer Lock | 4 | 4: Battle, BattleTeams, Survival, SurvivalTeams, Bounty, BountyTeams, Defender, DefenderTeams, PrimeHunter |
| TRANSFER LOCK DM / Transfer Lock | 8 | 4: Nodes, NodesTeams |
| MAGMA VENTS / Council Chamber | 8 | 6: Bounty, BountyTeams |
| ALINOS PERCH / Alinos Perch | 6 | 6: Battle, BattleTeams, Survival, SurvivalTeams, Nodes, NodesTeams, Defender, DefenderTeams, PrimeHunter |
| ALINOS LANDFALL / Alinos Gateway | 8 | 6: Bounty, BountyTeams |
| LANDING BAY / Celestial Gateway | 4 | - |
| 3 VESPER STARPORT / VDO Gateway | 4 | - |
| 4 ARCTERRA BASE / Arcterra Gateway | 6 | 4: Capture |
| Prison / Oubliette | 8 | - |
| SIDEHOPPER STATION / Sidehopper Station | 4 | - |
| SPIRES / Spires | 8 | - |
| CROSSFIRE CHAOS / Crossfire Chaos | 4 | - |
| PIPELINE / Pipeline | 8 | - |
| SPIDER COMPLEX / Spider Complex | 8 | - |
| SHOOTING GALLERY / Shooting Gallery | 4 | - |

### Milestones
- **N0:** Core `Net/` (state structs, serializer, host/client stubs), `PlayerNet.cs` Capture/Apply, a two-process
  `-netloop` harness on the PC.
- **N1:** LAN, host + 1 client, Battle on one arena: both see each other move and shoot, damage and deaths. Odin + PC
  client (or a second Android device).
- **N2:** items, bombs, Halfturret, 4 seats, bots on the host, disconnects, all modes.
- **N3:** prediction. **N4:** lag compensation. **N5:** delta compression, Wi-Fi Direct/hotspot UI, internet.
  **2026-10-09 (Netcode O5, online path, PC-verified):** N3 DONE (`Net/ClientPrediction.cs`, reconcile at the acked
  input), N4 DONE (`Net/LagCompensation.cs`, 32-frame host pose ring, cap 12 frames = 200 ms, owner 2026-10-09), N5's delta compression
  + internet DONE (`Net/SnapshotDelta.cs` 30/20 Hz, input redundancy, seat extras, rejoin, Cloudflare TURN with the
  relay as fallback). The LAN path is unchanged (`-lantest`, `-replaygolden` 9/9). Wi-Fi Direct/hotspot UI not
  started. Details: docs/ONLINE_PLAY.md section 9.
- Transport: UDP with unreliable + reliable-ordered channels (e.g. LiteNetLib, MIT, runs on Android).

### Open questions
- ANSWERED 2026-09-30: the multiplayer intro camera (e.g. 174 in Combat Hall) does NOT set BlockInput; it plays
  until the host's own player spawns (FIRE, or the ~30 s timer). No aim-block fix needed.
- Which weapons are effectively hitscan (Metadata/Weapons.cs speeds)?
- `ProcessPlayer` cost on the Odin (the replay budget).

## 5. Friend system (research 2026-09-30, not started)

Vanilla: 12-digit friend codes entered by both players (or registered through local multi-card play); rivals added
when both tap ADD RIVAL after a match, or by Rival Radar (swapping Hunter Licenses with up to 3 nearby closed DSes);
one 60-slot roster (57 lockable, oldest dropped); Find Game (worldwide, Battle only, arena vote) vs Friends & Rivals
(host picks mode and settings, see who's online); friends-only lobby message and voice (lobby only); Hunter License
with head-to-head records.

Recommended:
1. **No server, alongside local multiplayer:** device key identity, Hunter License, friends and rivals from local
   matches, head-to-head records, the 60-slot roster, a foreground "meetup" Rival Radar over Bluetooth LE.
2. **Own small server for online friends and rivals:** codes, presence, friends' game list, relay fallback (direct
   connections fail for ~30% of pairs). About EUR 0-7/month, no player accounts, changeable server address.
3. **Strangers matchmaking** only if enough people play (moderation and legal duties start here).

Skip: third-party platforms (Epic Online Services, Discord, Google Play Games, Photon, Steam: accounts tied to the
owner and IP policies), free-typed text and voice chat for now.
