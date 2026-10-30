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
   - Echoes, First Hunt and custom maps: `ArenaNav.Build`, ours.
     - Its input is any triangle soup: `FhArenaTool.Tris` reads any `_Collision.bin`, and the map maker can hand over
       its own triangles.
     - To add: breakable walls and solid entities in the raycaster's solid set (on 10-26, Crossfire Chaos links went
       through walls).
     - Ideas from Fruity Prime's MIT `MapNodePacker` (keep the notice if code is copied): thin nodes over the graph
       rather than the room, push nodes 1.0 off walls, and drop links checked by `FloorBetween`.
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

## Ship rule

No map data ships. The solve runs on the player's device from their own import: at ROM or Echoes import, next to
ArenaNav, and for custom maps when the editor saves the map (in the background), cached beside the map.

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
- **P4 map maker.** .shmap to ArenaNav to the solver at save time; coverage report in the editor.
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

**Owner 2026-10-28: PARKED.** Morph Ball and Regulator won't host bot matches for now (they'll be player races later),
and MP5 (the unused prototype blockout, dev menu only) is left out of bot work. So the `ArenaNav.Build` component fix
is parked: nothing bots play needs it, since MP1-3 and the Echoes arenas have sound graphs. Pick it up if bots ever go
to oddly built maps (map maker, randomizer). G3 has no untuned real arena left; it stays open until the owner names one.
Owner, later 10-28: First Hunt maps stay gated out of the public build, so all First Hunt bot work is parked too:
removing MP5 from the game, and Trooper Module's 16% stuck.

## Considered and set aside for now

- **DotRecast** (C#, zlib, so it could be borrowed): navmesh plus `DtJumpLinkBuilder`, which fits jump arcs off
  navmesh edges.
  - Not needed while ArenaNav covers candidates, and vanilla AI wants a node graph, not a navmesh.
  - Revisit if ArenaNav misses jumps on custom maps: its edge-sampling idea could go into ArenaNav without the library.
- **Fruity Prime** has no move or link solver and no unstick logic. Its bots are vanilla AI on generated `_Node.bin`
  files.
  - Its BotTour coverage metric and AltFormProbe's trial pattern (drive a non-main slot; set PrevPosition and Position
    on reset) are worth copying.
  - Fruity-Prime-private has no licence grant: borrow nothing from it.
- **Quake 3 AAS** (GPL, ideas only): typed links checked by predicting the real movement. That is this plan's pattern.
