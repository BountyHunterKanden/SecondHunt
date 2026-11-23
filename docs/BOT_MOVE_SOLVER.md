# Bot move solver: one way for bots to learn any map

Part of the bots vanilla parity work, step 3 onward (2026-10-26). The goal: bots keep vanilla difficulty but "naturally
know a map either vanilla or echoes or new custom player map", and never get stuck. This replaces step 3's rule hunting
(one failure, one rule) with a proof per link: before a match, each link is tried with the real physics, and the
move that gets across is stored. At runtime the bot plays that move back.

## What stays vanilla (difficulty)

Combat, aim, target and item choice, route choice (next-hop tables) and the ROM node graphs of vanilla arenas are all
unchanged. Only the way a bot crosses a link it has already chosen is new. Head-on runs at every phase (L0 within
±15%, L1-L2 within ±8%).

## Pipeline (the same for every map)

1. **Graph.**
   - Vanilla: the ROM graph as it is. The solver only labels its links, and adds and removes none.
2. **Candidate filter.** `ArenaNav.Walkable`'s thresholds stay, as a cheap pre-filter only. The final decision is the
   simulation's.
3. **Move solver (new).** For each link and each physics group of hunters, it tries a fixed menu, cheapest first:
   - walk
   - walk, then drift onto the node
   - walk off the edge
   - edge jump
   - run-up jump
   - climb jump (jump, then a second press at the top)
   - double jump or Gravity Boost (Echoes moves only)
   - alt-form roll
   - wait for the lift, then walk on
   - ride the jump pad

   Each move runs from a few start offsets with the real player code: `CampaignHost.StartMatch`, `PlacePlayer`, `Step`,
   or BotProbe's `AuditPut` and linkforce grading. The first move that arrives from most offsets wins. The solver
   stores the move id, its parameters (run-up length, press frame) and its margin.
   - Cost, measured 10-26: 764 Fuel Stack links × 1 hunter × 1 try ≈ 3.5 s on PC.
   - Most links pass on a plain walk, so the menu only runs on the rest.
4. **Map faults.** A link no move crosses (for example Oubliette 603, which fails in vanilla too) goes on the owner's
   list (G4). It is never patched per link, and it is never deleted.
5. **Runtime player.** `BotLinkTick` is widened from kind triggers to "play the stored move", for all arenas. It sits
   behind a new switch under HostBotVanillaParity, with its own touch line in the L3+R3 pause.
   - Before a move: a capsule sweep. Only rays exist today.
   - Backstop, in order:
     1. step aside
     2. replay the move
     3. the next-best stored move
     4. mark the link bad for a few seconds and replan
   - The link is never dropped: it is used again next time. This replaces "give up after 2 climbs".
   - Group 1 v6 (stuck fix vertical) stays, as part of the backstop.

## Measures (every phase)

- Linkall sweep (`out/link_audit/tools/sweep.sh`, `cmp.awk`): links working and links used.
- Area reach (cmp.awk "reached"), plus a BotTour-style coverage run: % nodes visited; stuck = under 1 unit moved in 5 s.
- Head-on (`headon_v.sh` + `pairboot.py`).
- G3: rules and moves are judged on an arena nobody tuned for.

## Phases

- **P1 solver tool (PC only).** `-botprobe linksolve` writes a per-link move table for all vanilla arenas, and counts
  how many of the 357 failing links (step 3 v6 baseline) have a move. No change to the game.
- **P2 runtime playback.** Switch, capsule sweep, backstop; linkall compare; head-on (more seeds for L1, which was
  +9.8% ttk_h on v6, CI -2..+25).
- **P3 Echoes and First Hunt.** ArenaNav graph plus the solver, with the G3 transfer test on an untouched arena.
- **P5 on-device bake** at import, then an Odin install (owner's OK).

## P1 results (10-26)

Tool: `-botprobe linksolve` (`src/MphRead.Tools/BotProbeLinkSolve.cs`). It drives the human seat with `CampaignInput`
and grades arrival with a copy of vanilla `IsNodeInRange`. Output is in `out/link_audit/solve_p1b/` (one file per
hunter, plus `summary.txt`).

- All links: 19,268 links over 10 vanilla arenas. Each hunter has a working move for 19,025 to 19,146 of them.
- **The 357 failing links: 307 have a move for at least one hunter, 306 for all 7.** All seven hunters give nearly the
  same result.
- Samus's move on the failing links:

  | Move | Links |
  |---|---|
  | Edge jump (`ledgejump`) | 269 |
  | Plain walk | 24 |
  | Jump | 5 |
  | Walk through a way-in chain | 3 |
  | Roll | 2 |
  | Wait | 2 |
  | Run-up jump | 1 |
  | Coast | 1 |
  | None | 50 |

  On the 24 plain-walk links, the bot's own steering fails where a straight walk works.
- Per arena (failing / solved by any hunter / solved by all hunters):

  | Arena | Failing | Any | All |
  |---|---|---|---|
  | Alinos Gateway | 5 | 5 | 5 |
  | Alinos Perch | 20 | 19 | 19 |
  | Arcterra Gateway | 34 | 27 | 27 |
  | Celestial Gateway | 26 | 8 | 8 |
  | Compression Chamber | 29 | 27 | 27 |
  | Fuel Stack | 45 | 42 | 42 |
  | Harvester | 38 | 31 | 31 |
  | Oubliette | 27 | 19 | 18 |
  | Subterranean | 33 | 32 | 32 |
  | Weapons Complex | 100 | 97 | 97 |

**Owner list (G4).** The 50 links with no move, by what the walk run ended on (Samus, `solve_p1b_unsolved_samus32.txt`):

- **Teleporter at the node.** Celestial Gateway ring nodes 83-94: links 353, 354, 355, 359, 361, 363, 370, 372, 374,
  378, 380, 384, 385. Every third ring node (83, 86, 89, 92) throws the player 23 units up within one frame. A link out
  of one can't start, and a link into one ends 22.5 above the node. This could be a rule (G1): when the node sits in a
  teleporter's volume, count stepping into the volume as arriving.
- **Target below a floor.** The straight line stops on a floor 8-14 units above the node:
  - Harvester 222, 223, 276
  - Weapons Complex 431, 435, 886
  - Celestial Gateway 68, 527, 534
  - Oubliette 116, 649

  The way down is a detour the link doesn't contain, so a vanilla bot steering straight stops there too.
- **Hazard node.** Harvester 174, 178, 182, 186 (Navigation to Hazard, 8-9 up): the player dies on the way.
- **Narrow edge or fall.** The player falls off on the way:
  - Arcterra Gateway 231, 232, 235, 740, all around nodes 49, 50 and 164 (MaxDistance 0.5)
  - Arcterra Gateway 720
  - Oubliette 17, 19, 24, 637 (nodes 4 and 157, a fall of about 25)
  - Alinos Perch 770
  - Compression Chamber 479, 484
  - Oubliette 598 (and 599 for Weavel only; the other six roll it)
- **Close but outside the range.** The player stops near the node but not within its MaxDistance:
  - Arcterra Gateway 41 (1.8 away), 181 (1.1 away)
  - Oubliette 175 (7.2 away, 4.9 up)
  - Subterranean 951
  - Fuel Stack 620, 682 (jump-pad flights from an Aerial start)
- **Death or bad start.**
  - Fuel Stack 424: the player dies 0.8 short.
  - Celestial Gateway 700, 724: the alt-form start isn't valid.

**Proposed first P2 rule (G1/G2).** Edge jump: when the next node is a step up or across a gap, and the bot is at the
edge or against a wall, jump. It uses only link and geometry properties. It covers 269 links across all 10 arenas,
none of them tuned for.

## P3 (Echoes and First Hunt, from 10-28)

**G3 arena: First Hunt MP5** (`FH MP5`, the prototype arena in the dev menu). It was picked before any P3 tuning: all six
Echoes arenas were tuned in the jitter and wall work, and First Hunt MP1-MP3 in the First Hunt thread. MP5 never had a
bot run. Nothing is tuned on it; it is only measured.

**Tool changes (PC only, no game change).**
- BotProbe `fhroot=<First Hunt import root>` and `arena=fh` run the First Hunt rooms.
- The solver menu has double jump (run-up 2, press at 0.6) and Gravity Boost (straight, at a ledge). These are tried
  only where the host has Echoes moves.
- A try that ends in the Shooting Gallery's manned turret counts as failed, and the player is unseated before the next
  start. Without this, every start after link 439 snapped back to the seat (503 false "moved at start" failures).
- `-botprobe fhroot=<root> navscc=<key>` (`BotProbeNavScc.cs`) prints the nodes ArenaNav keeps from the spawn anchors,
  against a build anchored on every floor sample.

**Echoes (`out/link_audit/solve_p3_echoes2/`, 7 hunters, all moves).** All seven hunters agree (Weavel differs on one
Crossfire Chaos link). 5,346 of 5,413 links solved (98.8%).

| Arena | Links | Solved | Not walk |
|---|---|---|---|
| Sidehopper Station | 1053 | 1041 | jump 4, coast 1 |
| Spires | 911 | 905 | jump 16, roll 2, coast 1, edge jump 1 |
| Crossfire Chaos | 864 | 848 | edge jump 6, jump 5, roll 3, climb 1 |
| Pipeline | 932 | 930 | edge jump 14, Gravity Boost 4, jump 4, roll 1 |
| Spider Complex | 696 | 685 | jump 2, coast 1, run-up 1 |
| Shooting Gallery | 957 | 937 | edge jump 16, jump 12, climb 11 |

The new Echoes moves solve 3 more links (Pipeline, Gravity Boost); the turret fix takes Shooting Gallery from 442 to 937.
Of the 67 unsolved links (`unsolved_samus.tsv`), 19 in Shooting Gallery go into nodes 189, 191 and 193 and end 5-9
above them in the air: something at those nodes throws the player up (not yet checked what).

**First Hunt (`out/link_audit/solve_p3_fh/`, 7 hunters).** The hunters differ on 2 links only.

| Arena | Links | Solved (Samus) | Not walk |
|---|---|---|---|
| Trooper Module | 918 | 918 | none |
| Assault Cradle | 1562 | 1540 | edge jump 24, roll 22, jump 8, coast 6, wait 2 |
| Ancient Vestige | 191 | 185 | jump 5, edge jump 3, roll 1 |
| Regulator | 196 | 196 | edge jump 1 |
| Morph Ball | 12 | 12 | none |
| MP5 (G3) | 12 | 12 | none (a 4-node graph, below) |

The 28 unsolved links (`unsolved_samus.tsv`) are graph faults, not missing moves:
- Drops of 5-7.5 that end about 6 above the node: the target is under a floor (Ancient Vestige 34, 104, 109, 167, 185;
  Assault Cradle 582, 601, 906, 913, 1102, 1269, 1524, 1525).
- Step-ups of 2.9 into Assault Cradle nodes 279, 281, 293 and 295 (12 links): too high for a First Hunt jump.
- Assault Cradle 1552 and 1557 (7.9 up), Ancient Vestige 166 (level, ends 4.8 short).

Trooper Module is all walk, yet its bots were stuck 16% of a 120 s run at (-4, 4, -36). That is steering or behaviour,
not a missing move.

**G3 result: MP5 fails at the graph, before the solver.** ArenaNav keeps the strongly connected part that holds the
most anchors, and the import's anchors are the 4 spawns. On MP5 that part is 4 nodes on one corner perch at y 4.4. The
main floor is a 45-node part at y 5.2 (found by anchoring on all 629 floor samples), and each spawn is 10.5 from it, but
it holds no spawn, so it is never chosen. The baseline bot run matches: 11% stuck, 31 deaths, 25 falls out of the
arena in 120 s. The moves transfer; the graph choice doesn't.

Proposed fix (owner decision; it changes `ArenaNav.Build`, game code): rank the parts by how many anchors can reach
them, then by the anchors inside, then by size. Check first that every tuned Echoes and First Hunt MP1-3 graph comes out
identical. Since MP5 motivated the fix, a fresh G3 would then be Regulator or Morph Ball (never bot-tuned).

The same fault shows on both of those (`navscc`): Morph Ball keeps 4 nodes by its spawns against a 122-node part from
floor anchors, and Regulator keeps 37 nodes around one spawn (x 155-178) while its 6 spawns run from x 0 to 249. Their
12 and 196 solved links are small graphs, not good ones. So either is a fair G3 only after the fix, measured on its
new graph.

## Considered and set aside for now

- **DotRecast** (C#, zlib, so it could be borrowed): navmesh plus `DtJumpLinkBuilder`, which fits jump arcs off
  navmesh edges.
  - Not needed while ArenaNav covers candidates, and vanilla AI wants a node graph, not a navmesh.
- **Fruity Prime** has no move or link solver and no unstick logic. Its bots are vanilla AI on generated `_Node.bin`
  files.
  - Its BotTour coverage metric and AltFormProbe's trial pattern (drive a non-main slot; set PrevPosition and Position
    on reset) are worth copying.
  - Fruity-Prime-private has no licence grant: borrow nothing from it.
- **Quake 3 AAS** (GPL, ideas only): typed links checked by predicting the real movement. That is this plan's pattern.

## Status and the rest of the plan (2026-11-16)

Owner, 2026-11-16: "i just want bots that intuitively know any new area like a human would but without changing their
vanilla difficulty or behavior", and a GO to finish this plan.

**Where it stands.**
- P1 (solver tool) and P3 (Echoes and First Hunt solve) are done, PC only: `BotProbeLinkSolve.cs`, `BotProbeNavScc.cs`,
  `BotProbePadSolve.cs`.
- P2 shipped as general rules rather than stored-move playback: edge jump (`PlayerAiEdgeJump.cs`, BOT EDGE JUMP), pad
  jump, edge hold, lift guard, up-lifts and arrive tick, each behind its own MatchSettings switch, with difficulty
  checked in band (Parity48, 10-29). The stored-move player (`BotLinkTick` playing the solver's move) is not built.
- The pad-throw guard (session df3e13, 11-16) sits unported in worktree `vigilant-kilby-787b58` (19/923 misses vs 37/924;
  the zero bar is unmet). Its own next idea is a per-pad stick schedule solved offline, which is this plan's pattern.

**The gap the Prime 1 rooms show: the graph, before the solver.** The solver only tries links that `ArenaNav.Build`
offers, and ArenaNav samples floors 4.5 apart and caps a climb at DoubleRise 3.6. Monitor Station (id 1009) came out
with no strong component (2 nodes kept, bots idle), so two jump pads were placed by hand
(`Placements/PRIME_MONITOR_STATION.json`). The owner rejects that: "i didnt want you deciding where pads were". A 1-unit
probe over the real collision found the pad ledges reachable only with hops up to ~6 per step (4.5/3.5/6.0/3.5 and
5.8/4.7/5.3), above the measured double jump (4.75). The owner says both ledges can be reached on foot, so the probe
misses something (narrow platforms, steep slopes, a move). The owner's on-foot replay of the climb is the test case.

**Steps (in order; each is PC first, then the owner's look on the Odin).**
1. **Scan finds every human route.** Change ArenaNav (or a pass before it) so the candidates cover what a human can do:
   floor sampled finer where it is narrow (small platforms, walkways; thin the nodes back to MPH spacing after), climbs
   offered up to what the solver can prove (the measured double jump and run-up arcs, not the 3.6 cap), and the
   component choice fixed (rank parts by the anchors that can reach them, the parked 10-28 fix). The solver decides
   which candidates are real. Test: the owner's Monitor Station replay route appears as solved links; then remove the
   placed pads and the room must still have one strong component. Every tuned Echoes / First Hunt graph is checked
   unchanged or better (linkall, navscc).
2. **Bots play the stored moves.** `BotLinkTick` widened to "play the solver's move" (move id, run-up, press frame),
   behind a new MatchSettings switch (off in campaign; replay format bump), with the backstop from the Pipeline section
   (step aside, replay, next-best move, mark bad and replan; never drop the link). Vanilla ROM graphs and every combat,
   aim, target and route choice stay vanilla. Difficulty: head-on and Parity48-style runs on vs off, L0-L2, inside the
   bands. Stuck and coverage: BotTour-style % nodes visited, stuck = under 1 unit moved in 5 s; the bar is zero stuck
   on the test arenas (owner rule: eliminate, not reduce). The G3 transfer test runs on an arena nobody tuned.
4. **On-device solve at import (P5).** The solve runs on the player's device next to ArenaNav at ROM / Echoes / Prime
   import; no map data ships. Then an Odin install.

**Step 1 results (2026-11-21, PC, pads deleted for good).** The owner's word: Monitor Station has an on-foot route
(their replay: climb from 1:11, top platform at 1:37, at 1:42 across a walkway and a jump to the far pad ledge), so the
pads stay out of `PRIME_MONITOR_STATION.json` and the scan must find the route.
- ArenaNav human mode (Prime 1 rooms only, `EchoesArena.cs` passes `human: p1 != null`): fine scan at 1.5, `Footing`
  (3 of 4 floor hits around a node), `Thin` back to MPH spacing, then long on-foot jump bridges (`Bridges`: up to 15
  across and 1.5 up, offered only where walking round costs over 2.5x the straight line).
- Solver moves `long/ledge` and `long/runup2` (the second press waits until the bot falls back below its take-off).
- Monitor Station (617 nodes, 5779 links, full solve): the big strong part is 410 nodes (was 322) and holds both old pad
  bottoms, both pad landings and the spawn floor. The replay route is solved leg by leg, including the climb
  (105.7,26 -> 110.2,30) and the walkway jump 125 -> 155 (`long/ledge`, 3 of 3). Left out: the y60-66 tops (28/24/11
  nodes); the scan offers no link to or from them, likely roof.
- Phendrana Shorelines (397 nodes, 4497 links): 389 nodes in one strong part, every pool-floor node in it. Only 38 of the
  ~400 pool exits are plain walks; the rest need a solved jump, ledge jump or climb. Today's live AI doesn't play those
  moves, which is where bots park on the pool floor (WETLOG: ~10 s stands at -40,88 and -49,98; Kanden 17.2 s, Spire
  20.6 s of 120 under water). That is step 2's job.
- Echoes and First Hunt graphs: unchanged by construction (the human-scan code only runs for Prime 1 rooms).
- Live bots on Monitor Station still don't climb to the deck: they follow the graph with vanilla link handling, so the
  solved climbs wait for step 2's stored-move player.
