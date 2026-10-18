# Pickups: Pirate Blaster + affinity pickups (Second Hunt)

Pickups track (board row "Pickups: Pirate Blaster + affinity pickups"), from 2026-10-09. Owner steps: (1) census,
(2) affinity pickups in Echoes arenas, (3) Pirate Blaster pickup art + recoloured Power Beam fallback, (4) placement
behind a match option (pure vanilla + replay goldens stay exact).

## Census summary (2026-10-09)

**The affinity pickup** (`ItemType.AffinityWeapon`, 21) gives each hunter their own weapon
(`Weapons.AffinityWeapons`: Kanden Volt Driver, Spire Magmaul, Trace Imperialist, Sylux Shock Coil, Noxus Judicator,
Weavel Battlehammer); Samus gets +50 missiles instead (`PlayerProcess.PickUpWeapon`). Separate from the vanilla
ADVANCED option "affinity weapons", which turns every weapon pickup into an affinity pickup.

**Not every vanilla arena has one.** 21 of 26 arenas carry 34 affinity spawners in some mode; 20 have one in Battle.

| Arena | Affinity pickups | Notes |
|---|---|---|
| Data Shrine, Harvester, Head Shot, Weapons Complex, Council Chamber | 0 | all five place all six weapons |
| Oubliette | 1 | Defender only |
| Combat Hall, High Ground, Compression Chamber, Processor Core, Ice Hive, Sanctorus, Sic Transit, Fault Line, Subterranean, Transfer Lock, Alinos Perch, Alinos Gateway, Celestial Gateway, Arcterra Gateway, Stasis Bunker | 1 in Battle | some swap spots by mode (Compression, Sanctorus, Alinos Gateway, Celestial: one spot for Battle, another for Nodes) |
| Elder Passage, Incubation Vault, Outer Reach, VDO Gateway | 2 in Battle | mirrored pairs (Outer Reach +-z, VDO diagonal) |
| Fuel Stack | 3 in Battle | one top, two in the lower level |

Pattern: an arena that places all six weapons usually skips the affinity pickup (5 of 7; Fault Line and Subterranean
have both); every arena that places fewer than six weapons has one. Spots are central or high contested points
(Processor Core's centre top, Arcterra's summit, Ice Hive's upper ledge) or a mirrored pair. Respawn 5 s (like the
weapons) except Subterranean 20 s; Celestial Gateway's first appears 5 s in.

Respawn times seen (30 Hz frames): weapons 5 s (a few 10 s), health small 15 s, UA big 10 s, missile big 10 s, double
damage 40 s-3 min 20 s, cloak ~2-3 min, Deathalt 4-5 min, Omega Cannon 20 s.

**Echoes arenas** (our import): no affinity pickup, no weapon spawner. Weapons come from the crates (each arena's pool of
four, `EchoesArena.CratePools`); fixed items are health/missile (+ Spider Complex's double damage):

| Arena | Fixed items | Crate pool |
|---|---|---|
| Sidehopper Station (m01) | 25: missile small x10, health small x10, health medium x5 | Imperialist, Judicator, Battlehammer, Volt Driver |
| Spires (m02) | 11: health small x6, missile small x4, health medium x1 | Imperialist, Judicator, Volt Driver, Magmaul |
| Crossfire Chaos (m03) | 18: missile small x8, health small x8, health medium x2 | Battlehammer, Magmaul, Shock Coil, Imperialist |
| Pipeline (m04) | 16: health small x10, missile small x5, health medium x1 | Shock Coil, Magmaul, Battlehammer, Judicator |
| Spider Complex (m05) | 17: missile small x8, health small x6, health medium x2, double damage x1 (60 s) | Shock Coil, Magmaul, Judicator, Volt Driver |
| Shooting Gallery (m06) | 17: missile small x7, health small x7, health medium x3 | Imperialist, Shock Coil, Battlehammer, Volt Driver |

Every Echoes pool has four of the six weapons, so by the vanilla pattern every Echoes arena qualifies for an affinity
pickup.

## Census: every item spawner (ROM entity data, rev 0 = rev 1 MP data)

Generated 2026-10-09 by the Pickups track's scratch census (MphRead `Read.GetEntities` over each MP room's entity file + the six imported Echoes arenas). Respawn = SpawnInterval at 30 Hz. Modes = the entity's layers (MphRead `Metadata._modeLayers`: Battle 0-2 by 2/3/4 players, Battle teams 3, Nodes 4-6, Nodes teams 7, Bounty 8-10, Bounty teams 11, Capture 12, Defender 14, Survival 15; Prime Hunter uses Battle's).

### 93 MP1 SANCTORUS | Data Shrine

| # | Item | Position (x, y, z) | Respawn | Modes |
|---|---|---|---|---|
| 7 | Health small | 0.00, 1.60, -29.37 | 10 s | Battle(2P/3P), Nodes(2P/3P), Survival |
| 11 | Health small | 0.00, 1.60, 29.43 | 10 s | Battle(2P/3P), Nodes(2P/3P), Survival |
| 46 | Health small | 10.73, 0.00, -0.01 | 20 s | Battle(3P/4P), BattleTeams, Nodes(3P/4P), NodesTeams, Capture |
| 47 | Health small | -10.73, 0.00, -0.01 | 20 s | Battle(3P/4P), BattleTeams, Nodes(3P/4P), NodesTeams, Capture |
| 48 | ShockCoil | -0.01, 3.53, -21.87 | 5 s | all |
| 49 | Imperialist | -0.01, 3.53, 21.92 | 5 s | all |
| 4 | Health big | 0.00, 1.60, 29.43 | 10 s | Battle(4P), BattleTeams, Nodes(4P), NodesTeams, Capture, Defender |
| 5 | Health big | 0.00, 1.60, -29.37 | 10 s | Battle(4P), BattleTeams, Nodes(4P), NodesTeams, Capture, Defender |
| 10 | Judicator | -16.89, 3.15, -16.91 | 10 s | all |
| 12 | VoltDriver | 16.91, 3.15, -16.89 | 10 s | all |
| 13 | Magmaul | 16.89, 3.15, 16.91 | 10 s | all |
| 14 | Battlehammer | -16.91, 3.15, 16.89 | 10 s | all |
| 15 | UA big | -14.71, 3.53, 25.47 | 10 s | all |
| 16 | UA big | 14.59, 3.53, 25.47 | 10 s | all |
| 17 | UA big | 14.71, 3.53, -25.40 | 10 s | all |
| 18 | UA big | -14.73, 3.53, -25.46 | 10 s | all |
| 6 | Missile big | 0.00, 3.74, 0.00 | 10 s | Battle, BattleTeams, Nodes(2P/3P), Bounty, BountyTeams, Capture, Survival |
| 19 | Missile small | 0.00, 3.74, -17.50 | 10 s | Nodes(4P), NodesTeams, Defender |
| 20 | Missile small | 0.00, 3.74, 17.59 | 10 s | Nodes(4P), NodesTeams, Defender |

### 94 MP2 HARVESTER | Harvester

| # | Item | Position (x, y, z) | Respawn | Modes |
|---|---|---|---|---|
| 23 | Imperialist | 0.00, 13.28, -65.51 | 15 s | Battle, BattleTeams, Capture, Defender, Survival |
| 22 | UA big | 4.26, 13.28, -59.80 | 10 s | Battle, BattleTeams, Capture, Defender, Survival |
| 24 | UA big | -4.26, 13.28, -59.80 | 10 s | Battle, BattleTeams, Capture, Defender, Survival |
| 27 | Health big | 0.00, 16.23, -22.00 | 20 s | Nodes(4P), NodesTeams, Capture |
| 29 | Health small | 3.98, 6.02, 0.00 | 15 s | Battle(4P), BattleTeams, Capture |
| 30 | Health small | -3.98, 6.02, 0.00 | 15 s | Battle(4P), BattleTeams, Capture |
| 33 | VoltDriver | 0.00, 16.23, -15.37 | 5 s | Battle, BattleTeams, Nodes, NodesTeams, Capture, Defender, Survival |
| 36 | VoltDriver | 0.00, 16.23, 15.37 | 5 s | Battle, BattleTeams, Nodes, NodesTeams, Capture, Defender, Survival |
| 38 | Health big | 0.00, 16.23, 22.00 | 20 s | Nodes(4P), NodesTeams, Capture |
| 44 | UA big | 4.26, 13.28, 59.80 | 10 s | Battle, BattleTeams, Capture, Defender, Survival |
| 45 | UA big | -4.26, 13.28, 59.80 | 10 s | Battle, BattleTeams, Capture, Defender, Survival |
| 48 | UA big | -11.02, 16.23, 20.32 | 10 s | Battle, BattleTeams, Nodes, NodesTeams, Capture, Defender, Survival |
| 49 | UA big | 11.02, 16.23, 20.32 | 10 s | Battle, BattleTeams, Nodes, NodesTeams, Capture, Defender, Survival |
| 50 | UA big | 11.02, 16.23, -20.32 | 10 s | Battle, BattleTeams, Nodes, NodesTeams, Capture, Defender, Survival |
| 51 | UA big | -11.02, 16.23, -20.32 | 10 s | Battle, BattleTeams, Nodes, NodesTeams, Capture, Defender, Survival |
| 52 | Cloak | 0.00, 31.03, -22.63 | 116.667 s | Battle, BattleTeams, Nodes, NodesTeams, Defender, Survival |
| 53 | Cloak | 0.00, 31.03, 22.63 | 116.667 s | Battle, BattleTeams, Nodes, NodesTeams, Defender, Survival |
| 34 | Missile big | 16.00, 16.23, 0.00 | 10 s | Battle, BattleTeams, Nodes, NodesTeams, Capture, Defender, Survival |
| 35 | Missile big | -16.00, 16.23, 0.00 | 10 s | Battle, BattleTeams, Nodes, NodesTeams, Capture, Defender, Survival |
| 57 | Health big | 0.00, 13.42, -61.26 | 20 s | Battle(3P/4P), BattleTeams, Defender, Survival |
| 58 | Health big | -0.00, 13.42, 61.21 | 20 s | Battle(3P/4P), BattleTeams, Defender, Survival |
| 63 | Health small | -0.00, 13.42, 61.21 | 15 s | Battle(2P), Nodes(2P) |
| 64 | Health small | 0.00, 13.42, -61.26 | 15 s | Battle(2P), Nodes(2P) |
| 60 | Health small | 0.00, 16.23, 22.00 | 15 s | Nodes(3P) |
| 59 | Health small | 0.00, 16.23, -22.00 | 15 s | Nodes(3P) |
| 46 | Imperialist | 0.00, 13.28, 65.51 | 15 s | Battle, BattleTeams, Capture, Defender, Survival |
| 14 | UA big | 15.20, 6.02, 0.00 | 10 s | Battle, BattleTeams, Nodes, NodesTeams, Capture, Defender, Survival |
| 2 | Health small | 0.00, 6.02, 13.50 | 15 s | Nodes(3P/4P), NodesTeams |
| 28 | UA big | -15.20, 6.02, 0.00 | 10 s | Battle, BattleTeams, Nodes, NodesTeams, Capture, Defender, Survival |
| 6 | Health small | 0.00, 6.02, -13.50 | 15 s | Nodes(3P/4P), NodesTeams |
| 12 | Judicator | 8.12, 6.02, -11.19 | 5 s | all |
| 13 | ShockCoil | -8.10, 6.02, 11.21 | 5 s | all |
| 17 | Battlehammer | 8.13, 6.02, 11.26 | 5 s | all |
| 19 | Magmaul | -8.11, 6.02, -11.23 | 5 s | all |
| 20 | Imperialist | 0.00, 6.88, -17.16 | 5 s | Nodes, NodesTeams |
| 21 | Imperialist | 0.00, 6.88, 17.16 | 5 s | Nodes, NodesTeams |

### 95 MP3 PROVING GROUND | Combat Hall

| # | Item | Position (x, y, z) | Respawn | Modes |
|---|---|---|---|---|
| 8 | **AFFINITY** | -2.06, 0.00, 12.64 | 5 s | Battle, BattleTeams, Nodes, NodesTeams, Defender, Survival |
| 10 | Health small | -9.29, 6.60, -10.23 | 10 s | Battle, BattleTeams, Nodes, NodesTeams, Defender, Survival |
| 11 | Health big | 7.58, -0.26, -8.38 | 20 s | Battle(3P/4P), BattleTeams, Nodes(3P/4P), NodesTeams, Defender |
| 12 | Health small | 5.09, 6.60, 1.73 | 10 s | Battle, BattleTeams, Nodes, NodesTeams, Defender, Survival |
| 13 | Missile big | -8.06, 6.60, -2.66 | 10 s | Battle, BattleTeams, Nodes, NodesTeams, Defender, Survival |
| 17 | UA big | -3.84, -0.25, -19.04 | 10 s | Battle, BattleTeams, Nodes, NodesTeams, Defender, Survival |
| 22 | UA big | -3.52, 0.00, 13.02 | 10.1 s | Battle, BattleTeams, Nodes, NodesTeams, Defender, Survival |
| 23 | UA big | -7.97, 6.59, 23.26 | 5 s | Battle, BattleTeams, Nodes, NodesTeams, Defender, Survival |
| 24 | UA big | 3.01, 6.59, -25.77 | 10 s | Battle, BattleTeams, Nodes, NodesTeams, Defender, Survival |
| 27 | Magmaul | -7.05, 0.00, -11.20 | 5 s | Battle, BattleTeams, Nodes, NodesTeams, Defender, Survival |
| 26 | Health small | 7.58, -0.26, -8.38 | 20 s | Battle(2P), Nodes(2P), Survival |
| 0 | Health small | -2.07, 0.00, 18.27 | 15 s | Battle, BattleTeams, Nodes, NodesTeams, Bounty, BountyTeams, Capture, Defender |

### 96 MP4 HIGHGROUND - EXPANDED | Elder Passage

| # | Item | Position (x, y, z) | Respawn | Modes |
|---|---|---|---|---|
| 1 | Health big | -0.05, 1.50, -0.21 | 20 s | Battle(3P/4P), BattleTeams, Nodes(3P/4P), NodesTeams, Bounty, BountyTeams, Capture, Defender |
| 2 | Health small | 8.87, 0.04, -17.00 | 30 s | Battle(3P), BattleTeams, Nodes(4P), NodesTeams, Bounty, BountyTeams, Capture, Defender |
| 4 | Missile big | 0.02, 11.62, 10.67 | 20 s | all |
| 6 | UA big | -0.80, 16.92, -12.42 | 10 s | all |
| 7 | Magmaul | 0.91, 16.92, -12.42 | 5 s | all |
| 8 | Missile big | 10.73, 16.92, -2.41 | 10 s | all |
| 16 | Health small | -9.65, 0.04, 14.85 | 30 s | all |
| 25 | UA big | 2.63, 19.55, 15.18 | 10 s | all |
| 27 | UA big | 23.83, 17.34, 8.20 | 15 s | Battle, BattleTeams, Survival |
| 28 | Health small | 8.55, 16.89, -20.28 | 30 s | all |
| 31 | **AFFINITY** | -25.61, 17.35, 1.99 | 5 s | Battle, BattleTeams, Nodes, NodesTeams, Capture, Defender, Survival |
| 32 | Health small | -25.61, 12.06, 9.04 | 15 s | Battle, BattleTeams, Nodes, NodesTeams, Bounty, BountyTeams, Capture, Survival |
| 34 | UA small | 23.83, 12.02, -7.55 | 30 s | all |
| 14 | UA big | 8.81, 4.53, -16.85 | 20 s | all |
| 35 | UA big | -9.64, 4.53, 14.79 | 20 s | all |
| 3 | **AFFINITY** | 21.61, 17.34, -0.04 | 5 s | all |
| 38 | ShockCoil | -2.39, 4.53, 8.28 | 5 s | all |
| 39 | UA big | -1.13, 4.53, 7.89 | 10 s | all |
| 40 | UA big | 2.12, 4.53, -9.06 | 10 s | all |
| 23 | UA big | -24.79, 17.34, 1.09 | 10 s | Battle, BattleTeams, Nodes, NodesTeams, Capture, Defender, Survival |
| 43 | Judicator | -9.69, 0.12, -16.94 | 5 s | all |
| 21 | UA big | 0.04, 11.59, 20.17 | 15 s | all |
| 54 | Health small | 12.86, 11.59, -16.83 | 20 s | Defender |
| 73 | Health small | -13.13, 11.59, 17.10 | 20 s | Defender |
| 74 | Health small | 28.95, 12.04, -0.02 | 15 s | Battle, BattleTeams, Survival |
| 75 | Health small | 23.83, 17.34, 8.20 | 15 s | Nodes, NodesTeams, Bounty, BountyTeams, Capture, Survival |
| 17 | Imperialist | -0.99, 22.11, 10.72 | 10 s | all |

### 97 MP4 HIGHGROUND | High Ground

| # | Item | Position (x, y, z) | Respawn | Modes |
|---|---|---|---|---|
| 1 | Health big | -0.05, 1.50, -0.21 | 20 s | Battle(3P/4P), BattleTeams |
| 2 | Health small | 8.87, 0.04, -17.00 | 30 s | Battle(2P/4P), BattleTeams, Nodes, NodesTeams, Defender, Survival |
| 4 | Missile big | 0.02, 11.62, 10.67 | 20 s | Battle, BattleTeams, Nodes, NodesTeams, Defender, Survival |
| 6 | UA big | -0.80, 16.92, -12.42 | 10 s | Battle, BattleTeams, Nodes, NodesTeams, Defender, Survival |
| 7 | Magmaul | 0.91, 16.92, -12.42 | 5 s | Battle, BattleTeams, Nodes, NodesTeams, Defender, Survival |
| 8 | Missile big | 10.73, 16.92, -2.41 | 10 s | Battle, BattleTeams, Nodes, NodesTeams, Defender, Survival |
| 16 | Health small | -9.65, 0.04, 14.85 | 30 s | Battle(2P/4P), BattleTeams, Nodes, NodesTeams, Defender, Survival |
| 25 | UA big | 2.81, 19.55, 14.96 | 10 s | Battle, BattleTeams, Nodes, NodesTeams, Defender, Survival |
| 14 | UA big | 8.81, 4.53, -16.85 | 20 s | Battle, BattleTeams, Nodes, NodesTeams, Defender, Survival |
| 38 | **AFFINITY** | -2.39, 4.53, 8.28 | 5 s | Battle, BattleTeams, Nodes, NodesTeams, Defender, Survival |
| 39 | UA big | -1.13, 4.53, 7.89 | 10 s | Battle, BattleTeams, Nodes, NodesTeams, Defender, Survival |
| 40 | UA big | 2.39, 4.53, -9.09 | 10 s | Battle, BattleTeams, Nodes, NodesTeams, Defender, Survival |
| 5 | Judicator | -9.70, 0.12, -17.00 | 5 s | Battle, BattleTeams, Nodes, NodesTeams, Defender, Survival |
| 23 | UA big | 8.37, 16.89, -20.55 | 10 s | Battle, BattleTeams, Survival |
| 24 | UA big | -0.38, 11.59, 20.17 | 15 s | Battle, BattleTeams, Defender, Survival |
| 27 | Health small | 12.53, 11.59, -16.61 | 15 s | Battle(3P/4P), BattleTeams, Nodes, NodesTeams, Defender |
| 28 | Health small | -13.16, 11.59, 17.34 | 20 s | Battle, BattleTeams, Nodes, NodesTeams, Defender, Survival |
| 37 | UA big | 12.31, 11.59, 18.86 | 15 s | Nodes, NodesTeams |
| 46 | Health small | -4.42, 6.30, 7.48 | 15 s | Nodes(3P/4P) |
| 9 | Double damage | 0.00, 4.09, 0.00 | 200 s | all |
| 12 | Imperialist | -1.00, 22.11, 10.71 | 10 s | all |

### 98 MP5 FUEL SLUICE | Compression Chamber

| # | Item | Position (x, y, z) | Respawn | Modes |
|---|---|---|---|---|
| 7 | UA big | 5.02, 5.00, 7.49 | 10 s | Battle, BattleTeams, Nodes, NodesTeams, Survival |
| 14 | Double damage | 11.50, 0.61, -0.06 | 40 s | Battle, BattleTeams, Nodes, NodesTeams, Survival |
| 15 | **AFFINITY** | 1.81, 5.00, 0.07 | 5 s | Nodes, NodesTeams, Survival |
| 16 | Health small | 17.02, 0.00, 8.85 | 10 s | Battle, BattleTeams, Nodes, NodesTeams, Survival |
| 17 | Health small | 17.02, 0.00, -8.83 | 10 s | Battle, BattleTeams, Nodes, NodesTeams, Survival |
| 18 | Missile big | -1.82, 5.00, 0.01 | 15 s | Battle, BattleTeams, Nodes, NodesTeams, Survival |
| 37 | Judicator | -19.08, 0.00, 0.02 | 5 s | Battle, BattleTeams, Nodes, NodesTeams, Survival |
| 2 | **AFFINITY** | 11.52, 5.00, 0.00 | 5 s | Battle, BattleTeams, Survival |
| 13 | Missile big | 25.27, 9.14, 0.00 | 10 s | Battle, BattleTeams, Nodes, NodesTeams, Survival |
| 8 | UA big | 5.02, 5.00, -7.49 | 10 s | Battle, BattleTeams, Nodes, NodesTeams, Survival |
| 5 | UA big | -19.08, 0.00, -2.95 | 10 s | Battle, BattleTeams, Nodes, NodesTeams, Survival |
| 12 | UA big | -19.08, 0.00, 2.95 | 10 s | Battle, BattleTeams, Nodes, NodesTeams, Survival |
| 22 | Health small | -16.66, 5.00, -7.38 | 15 s | Nodes, NodesTeams |
| 27 | Health small | -16.72, 5.00, 7.30 | 15 s | Nodes, NodesTeams |
| 31 | Health small | -7.24, 10.00, 0.00 | 15 s | Battle, Nodes, Survival |
| 3 | Health big | -7.24, 10.00, 0.00 | 20 s | BattleTeams, NodesTeams |
| 4 | Health small | -11.01, 0.00, -0.00 | 15 s | Battle(4P), BattleTeams, Nodes(4P), NodesTeams |

### 99 MP6 HEADSHOT | Head Shot

| # | Item | Position (x, y, z) | Respawn | Modes |
|---|---|---|---|---|
| 19 | Imperialist | -0.00, 29.32, -0.01 | 5 s | Battle, BattleTeams, Nodes, NodesTeams, Capture, Survival |
| 28 | Health big | 41.93, 23.81, 42.01 | 20 s | Battle(3P/4P), BattleTeams |
| 30 | Health big | -42.07, 23.81, 41.98 | 20 s | Battle(3P/4P), BattleTeams |
| 32 | Health big | -42.00, 23.81, -42.07 | 20 s | Battle(3P/4P), BattleTeams |
| 34 | Health big | 42.08, 23.81, -41.99 | 20 s | Battle(3P/4P), BattleTeams |
| 40 | Health small | -27.12, 41.59, 0.00 | 15 s | Nodes(2P/3P) |
| 49 | Health small | 0.00, 41.59, 27.12 | 15 s | Nodes(2P/3P) |
| 54 | Health small | 27.12, 41.59, 0.00 | 15 s | Nodes(2P/3P) |
| 55 | Health small | 0.00, 41.59, -27.12 | 15 s | Nodes(2P/3P) |
| 69 | Health small | 42.08, 23.81, -41.99 | 15 s | Battle(2P), Survival |
| 70 | Health small | 41.93, 23.81, 42.01 | 15 s | Battle(2P), Survival |
| 71 | Health small | -42.07, 23.81, 41.98 | 15 s | Battle(2P), Survival |
| 72 | Health small | -42.00, 23.81, -42.07 | 15 s | Battle(2P), Survival |
| 6 | UA big | 7.59, 36.87, -7.47 | 10 s | Battle, BattleTeams, Nodes, NodesTeams, Bounty(2P), Capture, Defender, Survival |
| 12 | Health small | -7.56, 36.88, 7.51 | 20 s | Battle, BattleTeams, Nodes, NodesTeams, Bounty(2P), Capture, Defender, Survival |
| 15 | UA big | 7.50, 36.87, 7.54 | 10 s | Battle, BattleTeams, Nodes, NodesTeams, Bounty(2P), Capture, Defender, Survival |
| 16 | UA big | -7.05, 36.87, -8.01 | 10 s | Battle, BattleTeams, Nodes, NodesTeams, Bounty(2P), Capture, Defender, Survival |
| 4 | Health big | 20.19, 28.50, -20.30 | 20 s | Capture |
| 5 | Health big | -20.17, 28.50, 20.35 | 20 s | Capture |
| 17 | Deathalt | 0.01, 21.22, 0.00 | 233.333 s | all |
| 13 | Health small | 3.56, 32.28, -3.65 | 15 s | all |
| 23 | Magmaul | -19.46, 29.06, -0.00 | 5 s | all |
| 24 | Judicator | 19.44, 29.06, -0.01 | 5 s | all |
| 25 | Battlehammer | -0.04, 29.06, 19.46 | 5 s | all |
| 26 | VoltDriver | 0.01, 29.06, -19.43 | 5 s | all |
| 10 | UA big | 0.02, 31.28, 7.16 | 10 s | all |
| 2 | ShockCoil | -8.27, 22.22, -2.23 | 5 s | all |
| 11 | ShockCoil | 8.26, 22.21, 2.21 | 5 s | all |

### 100 MP7 PROCESSOR CORE | Processor Core

| # | Item | Position (x, y, z) | Respawn | Modes |
|---|---|---|---|---|
| 6 | Health big | 0.00, 4.41, 2.27 | 20 s | Battle, BattleTeams, Defender, Survival |
| 7 | Magmaul | 8.23, 2.21, 0.00 | 5 s | Battle, BattleTeams, Nodes, NodesTeams, Defender, Survival |
| 8 | Judicator | -8.20, 2.21, 0.00 | 5 s | Battle, BattleTeams, Nodes, NodesTeams, Defender, Survival |
| 11 | UA big | -8.20, 2.21, -2.00 | 10 s | Battle, BattleTeams, Nodes, NodesTeams, Defender, Survival |
| 12 | UA big | 8.29, 2.21, 2.00 | 10 s | Battle, BattleTeams, Nodes, NodesTeams, Defender, Survival |
| 22 | Deathalt | 0.04, 13.25, 22.69 | 300 s | Battle(4P), BattleTeams, Nodes(4P), NodesTeams, Defender |
| 9 | Health small | -0.00, 13.49, 13.23 | 15 s | Battle(2P/3P), Survival |
| 16 | Double damage | -0.01, 11.84, -20.04 | 120 s | Battle, BattleTeams, Nodes, NodesTeams, Defender, Survival |
| 18 | **AFFINITY** | -0.00, 13.25, 0.00 | 5 s | Battle, BattleTeams, Nodes, NodesTeams, Defender, Survival |
| 10 | UA big | 8.29, 2.21, -2.00 | 10 s | Battle, BattleTeams, Nodes, NodesTeams, Defender, Survival |
| 17 | UA big | -8.20, 2.21, 2.00 | 10 s | Battle, BattleTeams, Nodes, NodesTeams, Defender, Survival |
| 14 | Health big | -0.00, 13.49, 13.23 | 20 s | Battle(4P), BattleTeams |
| 15 | Health big | 0.00, 13.25, -17.07 | 20 s | Nodes(2P/4P), NodesTeams |
| 19 | Health small | 8.36, 0.00, 16.10 | 15 s | Nodes(3P/4P), NodesTeams |
| 20 | Health small | -8.36, 0.00, 16.10 | 15 s | Nodes(3P/4P), NodesTeams |
| 21 | Health small | 0.00, 13.25, -17.07 | 15 s | Battle(3P/4P), BattleTeams, Nodes(3P) |

### 101 MP8 FIRE CONTROL | Weapons Complex

| # | Item | Position (x, y, z) | Respawn | Modes |
|---|---|---|---|---|
| 8 | VoltDriver | -8.86, 4.31, 0.00 | 5 s | Battle, BattleTeams, Nodes, NodesTeams, Capture, Defender, Survival |
| 12 | VoltDriver | 8.84, 4.31, 0.00 | 5 s | Battle, BattleTeams, Nodes, NodesTeams, Capture, Defender, Survival |
| 14 | ShockCoil | -0.06, 11.74, 19.71 | 10 s | Battle, BattleTeams, Nodes, NodesTeams, Capture, Defender, Survival |
| 16 | UA big | -1.43, 11.74, 19.71 | 10 s | Battle, BattleTeams, Nodes, NodesTeams, Capture, Defender, Survival |
| 26 | Health big | 0.00, 21.57, -9.29 | 20 s | Battle(4P), BattleTeams, Nodes(4P), NodesTeams |
| 27 | Magmaul | -37.95, 13.41, 0.00 | 10 s | Battle, BattleTeams, Nodes, NodesTeams, Capture, Defender, Survival |
| 29 | UA big | -38.07, 13.41, -2.14 | 10 s | Battle, BattleTeams, Nodes, NodesTeams, Capture, Defender, Survival |
| 31 | Battlehammer | 37.95, 13.41, 0.00 | 10 s | Battle, BattleTeams, Nodes, NodesTeams, Capture, Defender, Survival |
| 32 | UA big | 37.95, 13.41, 1.33 | 10 s | Battle, BattleTeams, Nodes, NodesTeams, Capture, Defender, Survival |
| 20 | Missile big | -15.38, 11.75, -0.01 | 20 s | Battle, BattleTeams, Nodes, NodesTeams, Capture, Defender, Survival |
| 21 | Missile big | 15.39, 11.75, 0.03 | 20 s | Battle, BattleTeams, Nodes, Capture, Defender, Survival |
| 49 | Imperialist | 0.02, 21.55, 9.27 | 5 s | Battle, BattleTeams, Nodes, NodesTeams, Capture, Defender, Survival |
| 50 | Cloak | -0.00, 15.72, -27.65 | 180 s | Battle, BattleTeams, Nodes, NodesTeams, Capture, Defender, Survival |
| 59 | Cloak | 0.02, 7.77, 41.56 | 180 s | Battle, BattleTeams, Nodes(2P), Capture, Survival |
| 9 | Judicator | -6.59, 12.24, -27.39 | 5 s | Battle, BattleTeams, Nodes, NodesTeams, Capture, Defender, Survival |
| 11 | UA big | -8.01, 12.06, -26.34 | 10 s | Battle, BattleTeams, Nodes, NodesTeams, Capture, Defender, Survival |
| 63 | Health small | 0.00, 21.57, -9.29 | 15 s | Battle(2P/3P), Nodes(2P/3P), Capture, Defender, Survival |
| 7 | Health small | 0.00, 4.02, 20.04 | 15 s | Battle(4P), BattleTeams, Nodes, NodesTeams, Capture, Defender |
| 17 | Health small | -33.68, 4.83, -0.01 | 15 s | Battle, BattleTeams, Defender, Survival |
| 18 | Health small | 33.61, 4.83, 0.03 | 15 s | Battle, BattleTeams, Defender, Survival |
| 35 | Health small | 10.10, 12.11, -27.20 | 15 s | Nodes, NodesTeams |
| 36 | UA big | 0.00, 4.31, -8.27 | 10 s | Battle, BattleTeams, Nodes, NodesTeams, Capture, Defender, Survival |
| 47 | UA big | 0.00, 4.31, 8.27 | 10 s | Battle, BattleTeams, Nodes, NodesTeams, Capture, Defender, Survival |
| 0 | UA big | -29.26, 4.21, 14.46 | 10 s | all |
| 10 | UA big | 31.68, 4.31, 15.91 | 10 s | all |

### 102 MP9 CRYOCHASM | Ice Hive

| # | Item | Position (x, y, z) | Respawn | Modes |
|---|---|---|---|---|
| 0 | Health big | 0.00, 18.85, -0.61 | 20 s | Battle(3P/4P), BattleTeams, Nodes(3P/4P), NodesTeams, Capture, Defender |
| 3 | Health small | -5.24, 31.42, -30.90 | 20 s | Battle, BattleTeams, Nodes, NodesTeams, Defender, Survival |
| 8 | **AFFINITY** | -13.81, 19.85, -0.04 | 5 s | Battle, BattleTeams, Nodes, NodesTeams, Capture, Defender, Survival |
| 9 | Missile big | -13.76, 19.87, 1.52 | 10 s | Battle, BattleTeams, Nodes, NodesTeams, Capture, Defender, Survival |
| 11 | Imperialist | -15.01, 30.44, -21.71 | 5 s | Battle, BattleTeams, Survival |
| 12 | Judicator | 0.01, 25.53, -28.03 | 5 s | Battle, BattleTeams, Nodes, NodesTeams, Capture, Defender, Survival |
| 14 | UA big | -13.82, 19.85, -1.51 | 10 s | Battle, BattleTeams, Nodes, NodesTeams, Capture, Defender, Survival |
| 23 | Health small | 18.45, 33.47, -35.93 | 30 s | Battle(3P/4P), BattleTeams, Nodes(3P/4P), NodesTeams, Defender |
| 24 | UA big | 14.38, 31.41, -28.00 | 20 s | Battle, BattleTeams, Nodes, NodesTeams, Capture, Defender, Survival |
| 31 | UA big | 11.49, 26.86, -11.00 | 20 s | Battle, BattleTeams, Nodes, NodesTeams, Capture, Defender, Survival |
| 37 | Health small | 7.91, 26.86, 7.99 | 20 s | Battle, BattleTeams, Nodes, NodesTeams, Capture, Defender, Survival |
| 35 | Missile big | -0.00, 26.86, 15.79 | 20 s | Battle, BattleTeams, Capture, Defender, Survival |
| 36 | Health small | -14.98, 26.86, 5.38 | 20 s | Battle(4P), BattleTeams, Nodes(4P), NodesTeams, Capture, Defender |
| 13 | Double damage | -16.90, 28.94, -31.68 | 80 s | Battle, BattleTeams, Nodes, NodesTeams, Defender, Survival |
| 28 | Imperialist | 8.36, 30.96, -21.48 | 5 s | Nodes, NodesTeams, Defender |
| 22 | Deathalt | 9.67, 33.48, -26.75 | 300 s | Battle, BattleTeams, Nodes, NodesTeams, Defender, Survival |
| 15 | Health small | 9.67, 33.48, -26.79 | 15 s | Capture |
| 26 | Health small | 0.00, 18.85, -0.61 | 20 s | Battle(2P), Nodes(2P), Survival |
| 1 | UA big | -20.37, 31.41, -28.99 | 10 s | all |
| 2 | VoltDriver | 14.94, 26.86, 0.53 | 10 s | all |

### 103 MP10 OVERLOAD | Incubation Vault

| # | Item | Position (x, y, z) | Respawn | Modes |
|---|---|---|---|---|
| 1 | Health small | 6.00, 0.00, 7.61 | 10 s | Battle(3P/4P), BattleTeams, Nodes(3P/4P), NodesTeams, Defender |
| 3 | Battlehammer | 9.51, 6.04, 9.59 | 5 s | Battle, BattleTeams, Nodes, NodesTeams, Defender, Survival |
| 4 | Health small | -6.00, 0.00, 7.61 | 10 s | Battle(3P/4P), BattleTeams, Nodes(3P/4P), NodesTeams, Defender |
| 5 | **AFFINITY** | -20.49, 3.82, -5.11 | 5 s | all |
| 7 | Missile big | -6.64, 6.04, -0.18 | 10 s | Battle, BattleTeams, Nodes, NodesTeams, Survival |
| 8 | UA big | -1.33, 0.00, -3.05 | 10 s | Battle, BattleTeams, Nodes, NodesTeams, Defender, Survival |
| 9 | UA big | 0.64, 0.00, -3.05 | 10 s | Battle, BattleTeams, Nodes, NodesTeams, Defender, Survival |
| 18 | ShockCoil | -0.35, 0.00, -3.05 | 5 s | Battle, BattleTeams, Nodes, NodesTeams, Defender, Survival |
| 10 | Health small | 14.48, 6.06, -4.62 | 15 s | Battle, BattleTeams, Nodes, NodesTeams, Defender, Survival |
| 2 | **AFFINITY** | -7.35, 6.07, -13.18 | 5 s | Battle, BattleTeams, Nodes, NodesTeams, Defender, Survival |
| 22 | UA big | 8.35, 6.07, 9.70 | 10 s | Battle, BattleTeams, Nodes, NodesTeams, Defender, Survival |
| 6 | Health small | -5.65, 0.00, -11.69 | 15 s | Battle, BattleTeams, Bounty, BountyTeams, Capture, Defender, Survival |
| 19 | Health small | -12.90, 6.07, -7.37 | 15 s | Nodes, NodesTeams |

### 104 MP11 BREAKTHROUGH | Sanctorus

| # | Item | Position (x, y, z) | Respawn | Modes |
|---|---|---|---|---|
| 0 | VoltDriver | -7.22, 9.57, -0.11 | 5 s | Battle, BattleTeams, Defender, Survival |
| 2 | Health small | 0.09, 3.02, 5.89 | 10 s | Battle, BattleTeams, Nodes, NodesTeams, Defender, Survival |
| 3 | Health small | 0.02, 3.19, -5.86 | 20 s | Battle(4P), BattleTeams, Nodes(4P), NodesTeams, Defender |
| 4 | Missile big | 0.73, 7.36, 0.87 | 10 s | Battle, BattleTeams, Nodes, NodesTeams, Defender, Survival |
| 5 | Double damage | -5.73, 5.86, 0.68 | 180 s | Battle, BattleTeams, Nodes, NodesTeams, Defender, Survival |
| 6 | **AFFINITY** | 11.88, 1.47, -0.03 | 5 s | Battle, BattleTeams, Survival |
| 8 | Health small | 20.38, 2.94, -9.74 | 10 s | Battle, BattleTeams, Nodes, NodesTeams, Defender, Survival |
| 10 | UA big | 3.92, 0.00, 11.56 | 10 s | Battle, BattleTeams, Nodes, NodesTeams, Defender, Survival |
| 11 | UA big | 4.81, 1.47, -10.91 | 10 s | Battle, BattleTeams, Nodes, NodesTeams, Defender, Survival |
| 12 | UA big | 26.48, 2.94, 0.88 | 10 s | Battle, BattleTeams, Nodes, NodesTeams, Defender, Survival |
| 19 | Health small | 14.72, 6.68, 10.51 | 10 s | Battle(3P/4P), BattleTeams, Nodes(3P/4P), NodesTeams, Defender, Survival |
| 1 | UA big | -0.96, 9.62, -6.33 | 10 s | Battle, BattleTeams, Nodes, NodesTeams, Defender, Survival |
| 28 | **AFFINITY** | 24.58, 2.94, -2.98 | 5 s | Nodes, NodesTeams, Defender |
| 7 | VoltDriver | 0.25, 9.62, 6.28 | 5 s | Nodes, NodesTeams |

### 105 MP12 SIC TRANSIT | Sic Transit

| # | Item | Position (x, y, z) | Respawn | Modes |
|---|---|---|---|---|
| 8 | Health big | 0.89, -0.09, 0.14 | 30 s | Battle(3P/4P), BattleTeams, Nodes(3P/4P), NodesTeams, Defender |
| 13 | Imperialist | 0.07, 12.64, -0.01 | 5 s | Battle, BattleTeams, Nodes, NodesTeams, Defender, Survival |
| 14 | Battlehammer | 21.99, 11.00, 0.00 | 5 s | Battle, BattleTeams, Capture, Survival |
| 15 | Health small | 21.40, 7.70, -26.20 | 30 s | Battle, BattleTeams, Nodes, NodesTeams, Capture, Defender, Survival |
| 24 | Missile big | -9.64, 2.65, -27.56 | 10 s | Battle, BattleTeams, Nodes, NodesTeams, Capture, Defender, Survival |
| 37 | Cloak | -12.43, 5.36, -25.34 | 200 s | Battle, BattleTeams, Nodes, NodesTeams, Defender, Survival |
| 38 | UA big | 21.46, 11.00, 1.05 | 10 s | Battle, BattleTeams, Capture, Survival |
| 39 | UA big | -10.57, 4.39, -22.03 | 15 s | Battle, BattleTeams, Nodes, NodesTeams, Defender, Survival |
| 40 | Double damage | -12.07, 7.12, 17.74 | 120 s | Battle, BattleTeams, Nodes, NodesTeams, Defender, Survival |
| 12 | UA big | 5.89, 4.42, -0.34 | 10 s | Battle, BattleTeams, Nodes, NodesTeams, Capture, Defender, Survival |
| 17 | UA big | -1.56, -0.14, 2.66 | 10 s | Battle, BattleTeams, Nodes, NodesTeams, Capture, Defender, Survival |
| 20 | Missile big | 8.16, -0.14, 2.98 | 5 s | Battle, BattleTeams, Nodes, NodesTeams, Capture, Defender, Survival |
| 22 | Battlehammer | 16.43, 10.47, -5.57 | 5 s | Nodes, NodesTeams, Defender |
| 23 | UA big | 15.48, 10.59, -4.83 | 10 s | Nodes, NodesTeams, Defender |
| 18 | **AFFINITY** | 8.72, -0.14, -1.90 | 5 s | Battle, BattleTeams, Nodes, NodesTeams, Capture, Defender, Survival |
| 43 | Health small | -6.89, 4.76, 13.66 | 20 s | Battle, BattleTeams, Nodes, NodesTeams, Capture, Defender, Survival |
| 59 | Judicator | 10.31, 0.01, -6.97 | 5 s | none (layer mask 0: never placed) |
| 10 | Health small | 0.89, -0.09, 0.14 | 20 s | Capture |
| 11 | ShockCoil | -7.05, -0.22, -9.37 | 5 s | all |
| 21 | **AFFINITY** | -15.85, 0.01, 0.00 | 5 s | Capture |
| 44 | UA big | -15.27, -0.00, 0.92 | 10 s | Capture |
| 45 | UA big | 11.05, 4.42, -3.09 | 10 s | Battle, BattleTeams, Nodes, NodesTeams, Defender, Survival |
| 9 | UA big | -11.60, -0.21, 9.85 | 10 s | all |

### 106 MP13 ACCELERATOR | Fuel Stack

| # | Item | Position (x, y, z) | Respawn | Modes |
|---|---|---|---|---|
| 5 | VoltDriver | 29.38, 13.00, 29.56 | 5 s | Battle, BattleTeams, Survival |
| 6 | Health small | 38.33, 13.00, 35.23 | 10 s | Battle, BattleTeams, Survival |
| 7 | Health small | 34.91, 13.00, 38.70 | 10 s | Battle, BattleTeams, Survival |
| 8 | Health big | 21.48, -13.00, 12.52 | 20 s | Battle, BattleTeams, Survival |
| 13 | Missile big | -5.70, 25.99, 5.55 | 15 s | Battle, BattleTeams, Nodes, NodesTeams, Bounty, BountyTeams, Survival |
| 15 | Health small | -0.10, 0.04, -4.34 | 10 s | Battle, BattleTeams, Nodes, NodesTeams, Bounty, BountyTeams, Survival |
| 16 | Health small | 0.11, 0.02, 4.42 | 10 s | Battle, BattleTeams, Nodes, NodesTeams, Bounty, BountyTeams, Survival |
| 17 | Health small | -8.53, 19.23, -8.50 | 10 s | Battle, BattleTeams, Nodes, NodesTeams, Bounty, BountyTeams, Survival |
| 18 | UA big | 9.86, 20.48, 6.21 | 15 s | Battle, BattleTeams, Nodes, NodesTeams, Survival |
| 19 | Cloak | 22.20, -2.50, 12.47 | 180 s | Battle, BattleTeams, Nodes, NodesTeams, Bounty, BountyTeams, Survival |
| 20 | Battlehammer | -9.16, 13.00, -2.38 | 5 s | Battle, BattleTeams, Nodes, NodesTeams, Bounty, BountyTeams, Survival |
| 23 | Health small | 7.12, 39.00, -9.99 | 13.3333 s | Battle, BattleTeams, Survival |
| 26 | ShockCoil | -16.65, 5.48, 6.21 | 5 s | Battle, BattleTeams, Nodes, NodesTeams, Bounty, BountyTeams, Survival |
| 28 | **AFFINITY** | -2.85, 5.00, 24.08 | 5 s | Battle, BattleTeams, Nodes, NodesTeams, Bounty, BountyTeams, Survival |
| 34 | UA big | -3.80, 5.00, 22.76 | 10 s | Battle, BattleTeams, Nodes, NodesTeams, Bounty, BountyTeams, Survival |
| 11 | UA big | 36.82, 13.00, 36.82 | 10 s | Battle, BattleTeams, Survival |
| 35 | UA big | -15.19, 5.48, 5.27 | 10 s | Battle, BattleTeams, Nodes, NodesTeams, Bounty, BountyTeams, Survival |
| 36 | Missile big | -18.76, -1.50, -2.73 | 10 s | Battle, BattleTeams, Bounty, BountyTeams, Survival |
| 27 | UA big | -8.50, 13.00, -3.82 | 10 s | Battle, BattleTeams, Nodes, NodesTeams, Bounty, BountyTeams, Survival |
| 14 | **AFFINITY** | -0.75, -12.59, -19.18 | 5 s | Battle, BattleTeams, Nodes, NodesTeams, Bounty, BountyTeams, Survival |
| 38 | **AFFINITY** | 6.20, -12.82, 19.69 | 5 s | Battle, BattleTeams, Nodes, NodesTeams, Bounty, BountyTeams, Survival |
| 39 | Double damage | -6.94, -21.30, -0.00 | 120 s | Battle, BattleTeams, Nodes, NodesTeams, Bounty, BountyTeams, Survival |
| 12 | Judicator | 9.09, 13.00, 4.01 | 5 s | Battle, BattleTeams, Nodes, NodesTeams, Bounty, BountyTeams, Survival |
| 37 | UA big | -1.17, -12.65, -18.26 | 10 s | Battle, BattleTeams, Nodes, NodesTeams, Bounty, BountyTeams, Survival |
| 43 | UA big | 7.30, -12.86, 18.83 | 10 s | Battle, BattleTeams, Nodes, NodesTeams, Bounty, BountyTeams, Survival |
| 40 | UA big | 38.33, 13.00, 35.23 | 10 s | Nodes, NodesTeams, Bounty, BountyTeams |
| 44 | VoltDriver | 36.82, 13.00, 36.82 | 5 s | Nodes, NodesTeams, Bounty, BountyTeams |
| 45 | Health big | 18.80, 5.28, 19.10 | 20 s | Nodes, NodesTeams |
| 53 | UA big | 10.23, 21.00, -17.32 | 10 s | Battle, BattleTeams, Nodes, NodesTeams, Bounty, BountyTeams, Survival |
| 51 | Missile big | 18.80, 5.28, 19.10 | 15 s | Battle, BattleTeams, Bounty, BountyTeams, Survival |
| 41 | Missile big | 34.91, 13.00, 38.70 | 10 s | Nodes, NodesTeams, Bounty, BountyTeams |
| 22 | Imperialist | 10.04, 39.15, -7.40 | 5 s | all |

### 107 MP14 OUTER REACH | Outer Reach

| # | Item | Position (x, y, z) | Respawn | Modes |
|---|---|---|---|---|
| 10 | ShockCoil | 0.01, 12.82, -14.60 | 5 s | Battle, BattleTeams, Nodes, NodesTeams, Capture, Defender, Survival |
| 11 | Judicator | -0.02, 12.81, 14.68 | 5 s | Battle, BattleTeams, Nodes, NodesTeams, Capture, Defender, Survival |
| 15 | Imperialist | -0.10, 16.86, 0.00 | 5 s (+10 s first) | all |
| 16 | **AFFINITY** | 0.14, 4.75, 16.79 | 5 s | Battle, BattleTeams, Nodes, NodesTeams, Capture, Defender, Survival |
| 17 | **AFFINITY** | 0.01, 4.74, -16.33 | 5 s | Battle, BattleTeams, Nodes, NodesTeams, Capture, Defender, Survival |
| 21 | UA big | 18.80, 10.00, 0.03 | 10 s | Battle, BattleTeams, Nodes, NodesTeams, Capture, Defender, Survival |
| 23 | UA big | -19.25, 9.51, 0.04 | 10 s | Battle, BattleTeams, Nodes, NodesTeams, Capture, Defender, Survival |
| 24 | UA big | 7.13, 4.73, -16.10 | 10 s | Battle, BattleTeams, Nodes, NodesTeams, Capture, Defender, Survival |
| 25 | UA big | -7.25, 4.74, -16.10 | 10 s | Battle, BattleTeams, Nodes, NodesTeams, Capture, Defender, Survival |
| 26 | UA big | -7.25, 4.75, 16.02 | 10 s | Battle, BattleTeams, Nodes, NodesTeams, Capture, Defender, Survival |
| 27 | UA big | 7.13, 4.75, 16.02 | 10 s | Battle, BattleTeams, Nodes, NodesTeams, Capture, Defender, Survival |
| 28 | Missile big | 0.00, 19.92, -23.64 | 15 s | Battle, BattleTeams, Survival |
| 29 | Missile big | 0.00, 19.92, 23.64 | 15 s | Battle, BattleTeams, Survival |
| 35 | Health small | 20.17, 4.73, 0.07 | 15 s | Battle, BattleTeams, Nodes, NodesTeams, Capture, Defender, Survival |
| 36 | Health small | -20.45, 4.73, 0.07 | 15 s | Battle, BattleTeams, Nodes, NodesTeams, Capture, Defender, Survival |
| 39 | UA big | 0.07, -2.83, 7.88 | 10 s | Battle, BattleTeams, Nodes, NodesTeams, Capture, Survival |
| 40 | UA big | 0.07, -2.83, -7.86 | 10 s | Battle, BattleTeams, Nodes, NodesTeams, Capture, Survival |
| 50 | Missile big | 0.00, 19.91, -17.50 | 20 s | Nodes, NodesTeams, Capture |
| 51 | Missile big | 0.00, 19.91, 17.50 | 20 s | Nodes, NodesTeams, Capture |
| 19 | Health big | 0.00, 4.91, 0.00 | 20 s | Battle(4P), BattleTeams, Nodes(4P), NodesTeams, Capture |
| 18 | Health small | 0.00, 4.91, 0.00 | 15 s | Battle(3P), Nodes(3P) |

### 108 CTF1 FAULT LINE - EXPANDED | Fault Line

| # | Item | Position (x, y, z) | Respawn | Modes |
|---|---|---|---|---|
| 9 | Health big | -59.24, 10.00, -5.50 | 20 s | Bounty(4P), BountyTeams |
| 12 | UA big | -43.56, 10.00, -11.07 | 10 s | Battle, BattleTeams, Bounty, BountyTeams, Survival |
| 14 | UA big | -40.73, 10.00, -8.85 | 10 s | Bounty, BountyTeams, Capture |
| 16 | Missile big | -34.13, 14.50, -0.37 | 10 s | Battle, BattleTeams, Bounty, BountyTeams, Capture, Survival |
| 17 | Battlehammer | -42.45, 10.00, -9.66 | 5 s | Battle, BattleTeams, Bounty, BountyTeams, Capture, Survival |
| 21 | UA big | -61.02, 14.50, -5.10 | 10 s | Battle, BattleTeams, Bounty, BountyTeams, Capture, Survival |
| 26 | UA big | -39.58, 0.00, -0.81 | 20 s | Battle, BattleTeams, Bounty, BountyTeams, Capture, Survival |
| 27 | Health small | 4.02, 0.00, 8.16 | 15 s | Battle, BattleTeams, Bounty, BountyTeams, Capture, Survival |
| 29 | UA big | 30.49, 20.66, 4.83 | 10 s | Battle, BattleTeams, Bounty, BountyTeams, Capture, Survival |
| 31 | UA big | 31.77, 0.00, 3.87 | 20 s | Battle, BattleTeams, Bounty, BountyTeams, Capture, Survival |
| 35 | Battlehammer | -19.91, 17.66, -9.94 | 5 s | Battle, BattleTeams, Bounty, BountyTeams, Capture, Survival |
| 34 | Health big | -3.09, 12.47, -0.38 | 20 s | Battle(4P), BattleTeams, Bounty(4P), BountyTeams |
| 40 | Double damage | 31.95, 0.00, -6.10 | 80 s | Battle, BattleTeams, Bounty, BountyTeams, Survival |
| 41 | ShockCoil | -40.03, 0.00, -2.34 | 5 s | Battle, BattleTeams, Bounty, BountyTeams, Capture, Survival |
| 18 | Health small | -65.31, 0.01, 2.90 | 20 s | Battle(4P), BattleTeams, BountyTeams, Capture, Survival |
| 25 | Cloak | -16.39, 17.98, 11.59 | 180 s | Battle, BattleTeams, Bounty, BountyTeams, Survival |
| 28 | Health small | 22.74, 0.00, -12.64 | 20 s | Bounty(3P/4P), BountyTeams |
| 32 | Health big | 54.93, 17.66, 3.83 | 20 s | Battle, BattleTeams, Survival |
| 51 | VoltDriver | 35.72, 20.70, 2.46 | 5 s | Battle, BattleTeams, Bounty, BountyTeams, Capture, Survival |
| 53 | Missile big | 24.27, 0.00, -12.43 | 10 s | Battle, BattleTeams, Capture, Survival |
| 54 | Health small | 41.70, 0.00, -1.53 | 15 s | Battle(4P), BattleTeams, Capture |
| 56 | Health big | -54.75, 20.00, 6.13 | 20 s | Battle(3P/4P), BattleTeams |
| 57 | UA big | -48.89, 15.85, 12.67 | 15 s | Battle, BattleTeams, Survival |
| 60 | Imperialist | 13.47, 11.47, -8.99 | 5 s | Battle, BattleTeams, Bounty, BountyTeams, Capture, Survival |
| 61 | UA big | 16.55, 11.40, -4.22 | 10 s | Battle, BattleTeams, Bounty, BountyTeams, Capture, Survival |
| 83 | Magmaul | 31.37, 0.00, -6.17 | 5 s | Capture |
| 90 | Magmaul | -61.63, 14.50, -3.67 | 5 s | Battle, BattleTeams, Bounty, BountyTeams, Capture, Survival |
| 75 | Health small | -59.24, 10.00, -5.50 | 15 s | Battle(3P/4P), BattleTeams, Bounty(2P/3P) |
| 76 | Health small | -54.75, 20.00, 6.13 | 15 s | Battle(2P), Survival |
| 15 | Judicator | -50.39, -0.41, 5.07 | 5 s | all |
| 19 | **AFFINITY** | -24.09, 20.66, 0.00 | 5 s | all |

### 109 CTF1_FAULT LINE | Subterranean

| # | Item | Position (x, y, z) | Respawn | Modes |
|---|---|---|---|---|
| 12 | UA big | -43.56, 10.00, -11.07 | 10 s | Battle, BattleTeams, Nodes, NodesTeams, Defender, Survival |
| 13 | Magmaul | -62.26, 14.50, -3.58 | 5 s | Battle, BattleTeams, Nodes, NodesTeams, Defender, Survival |
| 16 | Missile big | -34.13, 14.50, -0.37 | 10 s | Battle, BattleTeams, Nodes, NodesTeams, Defender, Survival |
| 17 | Battlehammer | -42.45, 10.00, -9.66 | 5 s | Battle, BattleTeams, Nodes, NodesTeams, Defender, Survival |
| 20 | UA big | -52.31, 16.49, 10.95 | 15 s | Battle, BattleTeams, Nodes, NodesTeams, Defender, Survival |
| 0 | Missile big | -67.26, 10.00, 8.09 | 10 s | Battle, BattleTeams, Nodes, NodesTeams, Defender, Survival |
| 9 | Health small | -53.25, 20.00, 6.45 | 15 s | Battle, BattleTeams, Survival |
| 38 | Double damage | -50.46, 19.49, -3.56 | 180 s | Battle, BattleTeams, Nodes, NodesTeams, Defender, Survival |
| 43 | Health small | -39.76, 10.00, 0.79 | 20 s | Battle(3P/4P), BattleTeams, Nodes(3P/4P), NodesTeams, Defender |
| 44 | Health small | -57.86, 10.00, -3.85 | 15 s | Battle(3P/4P), BattleTeams, Nodes(3P/4P), NodesTeams, Defender |
| 4 | ShockCoil | -38.65, 0.00, 2.25 | 5 s | Battle, BattleTeams, Nodes, NodesTeams, Defender, Survival |
| 11 | UA big | -38.56, 0.00, 1.22 | 10 s | Battle, BattleTeams, Nodes, NodesTeams, Defender, Survival |
| 25 | Health big | 3.95, 0.00, 8.10 | 20 s | Battle(3P/4P), BattleTeams, Nodes(3P/4P), NodesTeams |
| 28 | Imperialist | 14.22, 11.48, -8.33 | 5 s | Battle, BattleTeams, Nodes, NodesTeams, Defender, Survival |
| 49 | Health small | -19.67, 17.66, -9.72 | 15 s | Battle(3P/4P), BattleTeams, Nodes(3P/4P), NodesTeams, Defender |
| 47 | Missile big | 13.11, 0.00, 8.13 | 15 s | Battle, BattleTeams, Nodes, NodesTeams, Defender, Survival |
| 51 | Cloak | 12.96, 0.00, -2.32 | 120 s | Battle, BattleTeams, Nodes, NodesTeams, Defender, Survival |
| 52 | Double damage | 24.36, 20.66, 3.86 | 120 s | Battle, BattleTeams, Nodes, NodesTeams, Defender, Survival |
| 54 | UA big | -60.05, 10.00, -11.91 | 15 s | Battle, BattleTeams, Nodes, NodesTeams, Defender, Survival |
| 55 | UA big | -58.29, -0.41, -1.23 | 15 s | Battle, BattleTeams, Nodes, NodesTeams, Defender, Survival |
| 56 | UA big | -15.89, 11.65, 7.56 | 15 s | Battle, BattleTeams, Nodes, NodesTeams, Defender, Survival |
| 1 | **AFFINITY** | -24.17, 20.66, 0.07 | 20 s | all |
| 30 | VoltDriver | 9.88, 1.11, 2.70 | 5 s | Battle, BattleTeams, Nodes, NodesTeams, Defender, Survival |
| 40 | Health small | 3.95, 0.00, 8.10 | 15 s | Battle(2P), Nodes(2P), Defender, Survival |
| 15 | Judicator | -50.34, -0.41, 5.15 | 5 s | Battle(4P) |
| 19 | UA big | -42.80, 10.00, -5.71 | 10 s | all |

### 110 AD1 TRANSFER LOCK BT | Transfer Lock

| # | Item | Position (x, y, z) | Respawn | Modes |
|---|---|---|---|---|
| 5 | Health small | 36.86, 12.44, -0.01 | 20 s | Bounty, BountyTeams, Defender |
| 16 | Imperialist | 12.97, 9.48, -0.01 | 10 s | Bounty, BountyTeams, Capture, Defender |
| 18 | UA big | -40.49, 9.48, 12.95 | 10 s | Bounty, BountyTeams, Capture, Defender |
| 19 | UA big | -40.49, 9.48, -13.07 | 10 s | Bounty, BountyTeams, Capture, Defender |
| 28 | UA big | -6.95, 5.05, 15.66 | 10 s | Bounty, BountyTeams, Defender |
| 32 | UA big | -6.97, 5.05, -15.58 | 10 s | Bounty, BountyTeams, Defender |
| 35 | UA big | 14.40, 9.48, -0.01 | 10 s | Bounty, BountyTeams, Capture, Defender |
| 11 | Health small | 13.15, 5.06, -14.38 | 10 s | Bounty(3P/4P), BountyTeams, Defender |
| 12 | Health small | 13.20, 5.06, 14.40 | 10 s | Bounty(3P/4P), BountyTeams, Defender |
| 25 | Missile big | -24.45, 9.48, -0.00 | 10 s | Bounty, BountyTeams, Defender |
| 26 | Health small | -52.15, 13.93, -20.05 | 20 s | Bounty, BountyTeams, Defender |
| 37 | Health small | -52.15, 13.93, 19.83 | 20 s | Bounty, BountyTeams, Defender |
| 17 | **AFFINITY** | -9.66, 5.05, 0.00 | 5 s | Bounty, BountyTeams, Capture, Defender |
| 22 | Double damage | 57.02, 7.19, 0.00 | 120 s | Defender |
| 51 | Health small | -12.80, 5.06, 11.08 | 15 s | Capture |
| 52 | Health small | -12.80, 5.06, -11.08 | 15 s | Capture |
| 53 | Health small | 13.26, 13.91, 0.00 | 15 s | Capture |
| 57 | Health small | -23.34, 9.47, 12.95 | 15 s | Bounty(4P), BountyTeams |
| 58 | Health small | -23.34, 9.47, -12.95 | 15 s | Bounty(4P), BountyTeams |
| 6 | Missile big | -24.37, 9.47, 0.00 | 10 s | Capture |
| 8 | UA big | 30.14, 5.06, 5.57 | 10 s | Capture |
| 9 | UA big | 30.14, 5.06, -5.46 | 10 s | Capture |
| 14 | Battlehammer | -68.37, 13.91, 0.05 | 5 s | all |
| 15 | VoltDriver | 13.15, 9.47, -10.51 | 5 s | all |
| 20 | ShockCoil | 13.17, 9.47, 10.34 | 5 s | all |

### 111 AD1 TRANSFER LOCK DM | Transfer Lock

| # | Item | Position (x, y, z) | Respawn | Modes |
|---|---|---|---|---|
| 9 | Health big | -46.42, 13.47, -0.02 | 10 s | Battle(3P/4P), BattleTeams, Nodes(3P) |
| 20 | UA big | -39.09, 9.48, 13.18 | 10 s | Battle, BattleTeams, Nodes, NodesTeams, Survival |
| 21 | UA big | -39.09, 9.48, -13.18 | 10 s | Battle, BattleTeams, Nodes, NodesTeams, Survival |
| 33 | Health small | -22.66, 9.48, -13.18 | 20 s | Battle, BattleTeams, Nodes, NodesTeams, Survival |
| 34 | Health small | -22.66, 9.48, 13.18 | 20 s | Battle, BattleTeams, Nodes, NodesTeams, Survival |
| 25 | Missile big | -24.45, 9.48, -0.00 | 10 s | Battle, BattleTeams, Nodes, NodesTeams, Survival |
| 41 | Health small | -51.83, 13.91, -13.18 | 20 s | Nodes(4P), NodesTeams |
| 44 | Health small | -51.83, 13.91, 13.18 | 20 s | Nodes(4P), NodesTeams |
| 10 | Imperialist | 6.90, 13.91, 0.00 | 5 s | Battle, BattleTeams, Nodes, NodesTeams, Survival |
| 17 | UA big | -6.96, 5.05, 15.67 | 10 s | Battle, BattleTeams, Nodes, NodesTeams, Survival |
| 30 | UA big | -7.02, 5.05, -15.67 | 10 s | Battle, BattleTeams, Nodes, NodesTeams, Survival |
| 31 | **AFFINITY** | -8.51, 5.05, 0.00 | 5 s | Battle, BattleTeams, Nodes, NodesTeams, Survival |
| 45 | Health small | -46.31, 13.47, 0.00 | 20 s | Battle(2P), Nodes(2P), Survival |
| 2 | VoltDriver | -56.07, 13.91, 6.65 | 5 s | all |
| 4 | Battlehammer | -56.04, 13.91, -6.60 | 5 s | all |

### 112 AD2 MAGMA VENTS | Council Chamber

| # | Item | Position (x, y, z) | Respawn | Modes |
|---|---|---|---|---|
| 10 | Magmaul | 16.56, 18.22, -7.04 | 5 s | all |
| 19 | Judicator | 24.57, 1.25, 0.01 | 5 s | all |
| 20 | ShockCoil | -23.25, 0.04, 0.00 | 5 s | all |
| 21 | UA big | 16.57, 18.22, -5.47 | 20 s | all |
| 25 | Missile big | 27.23, 1.13, -5.63 | 30 s | all |
| 26 | UA big | 23.35, 1.07, 0.72 | 10 s | all |
| 31 | Health big | 24.73, 5.83, -11.12 | 20 s | Bounty(3P/4P), BountyTeams |
| 32 | Health small | 35.33, 29.80, 7.66 | 20 s | Nodes(3P/4P), NodesTeams, Bounty, BountyTeams, Capture, Defender |
| 33 | Health small | 2.52, 0.00, 7.71 | 20 s | Battle(3P/4P), BattleTeams, Nodes(3P/4P), NodesTeams, Bounty(4P), BountyTeams, Capture |
| 35 | Health small | -54.93, 4.41, -20.15 | 20 s | all |
| 22 | Imperialist | 36.29, 25.39, 7.66 | 5 s | all |
| 15 | Missile big | 45.51, 25.39, 7.92 | 10 s | all |
| 34 | Health small | -8.77, 11.59, -0.11 | 20 s | Battle, BattleTeams, Nodes, NodesTeams, Bounty(4P), BountyTeams, Defender, Survival |
| 12 | UA big | -21.86, 0.04, 0.67 | 10 s | all |
| 13 | UA big | -1.71, 0.01, 1.41 | 10 s | all |
| 17 | Health small | 7.90, -0.09, 2.43 | 20 s | all |
| 28 | Battlehammer | 27.07, 13.80, -4.39 | 5 s | all |
| 38 | UA big | 49.94, 13.80, -19.97 | 10 s | all |
| 41 | Missile big | -53.28, 9.75, -3.25 | 15 s | all |
| 58 | Health small | 1.98, 12.70, 0.00 | 15 s | Capture |
| 68 | UA big | 30.14, 20.98, -7.30 | 15 s | all |
| 70 | Health big | 46.92, 10.48, -1.19 | 20 s | Battle, BattleTeams, Survival |
| 71 | Health small | 24.73, 5.83, -11.12 | 15 s | Battle, BattleTeams, Nodes, NodesTeams, Bounty(2P), Capture, Defender, Survival |
| 42 | UA big | 33.80, 20.98, 7.66 | 15 s | Battle(3P/4P), BattleTeams, Nodes(3P/4P), NodesTeams, Bounty(3P/4P), BountyTeams, Capture, Defender |
| 18 | VoltDriver | 49.94, 13.80, -18.86 | 5 s | all |

### 113 AD2 ALINOS PERCH | Alinos Perch

| # | Item | Position (x, y, z) | Respawn | Modes |
|---|---|---|---|---|
| 10 | Magmaul | 16.56, 18.22, -7.04 | 5 s | Battle, BattleTeams, Nodes, NodesTeams, Capture, Defender, Survival |
| 20 | **AFFINITY** | -23.25, 0.04, 0.00 | 5 s | Battle, BattleTeams, Nodes, NodesTeams, Capture, Defender, Survival |
| 21 | UA big | 16.57, 18.22, -5.47 | 20 s | Battle, BattleTeams, Nodes, NodesTeams, Capture, Defender, Survival |
| 11 | UA big | 35.33, 29.80, 7.66 | 20 s | Capture |
| 23 | Imperialist | 36.29, 25.39, 7.66 | 5 s | Battle, BattleTeams, Nodes, NodesTeams, Capture, Defender, Survival |
| 19 | Missile big | 45.51, 25.39, 7.92 | 10 s | Battle, BattleTeams, Nodes, NodesTeams, Capture, Defender, Survival |
| 22 | Health small | -8.77, 11.59, -0.11 | 20 s | Battle, BattleTeams, Nodes, NodesTeams, Survival |
| 12 | UA big | -21.86, 0.04, 0.67 | 10 s | Battle, BattleTeams, Nodes, NodesTeams, Capture, Defender, Survival |
| 13 | UA big | -1.71, 0.01, 1.41 | 10 s | Battle, BattleTeams, Nodes, NodesTeams, Capture, Defender, Survival |
| 25 | Health big | 46.90, 28.15, -1.22 | 20 s | Battle(4P), BattleTeams |
| 27 | Health big | -39.88, 8.83, 6.08 | 20 s | Battle(3P/4P), BattleTeams, Defender |
| 17 | Missile big | -53.24, 9.75, -3.24 | 15 s | Battle, BattleTeams, Nodes, NodesTeams, Capture, Defender, Survival |
| 42 | Health small | 2.40, 12.70, 0.00 | 15 s | Capture, Defender |
| 48 | Health small | 46.90, 28.15, -1.22 | 15 s | Battle(2P/3P), Survival |
| 49 | Health small | 35.33, 29.80, 7.66 | 20 s | Nodes, NodesTeams |
| 50 | UA big | 33.54, 20.98, 7.63 | 15 s | Battle, BattleTeams, Nodes, NodesTeams, Defender, Survival |
| 18 | Health small | 27.97, 20.98, 15.66 | 15 s | Defender |
| 14 | Health small | -54.85, 4.41, -20.46 | 20 s | Nodes, NodesTeams, Capture, Defender, Survival |
| 2 | Health small | -39.88, 8.83, 6.08 | 15 s | Battle(2P), Survival |
| 9 | ShockCoil | 37.49, 28.70, -6.83 | 10 s | all |

### 114 UNIT1 ALINOS LANDFALL | Alinos Gateway

| # | Item | Position (x, y, z) | Respawn | Modes |
|---|---|---|---|---|
| 16 | Imperialist | 3.87, 21.04, -50.34 | 5 s | all |
| 18 | Missile big | 5.87, 7.16, -50.33 | 10 s | Battle, BattleTeams, Nodes, NodesTeams, Bounty, BountyTeams, Defender, Survival |
| 20 | **AFFINITY** | 14.47, 12.84, 0.00 | 5 s | Battle, BattleTeams, Survival |
| 23 | Missile big | -55.74, 1.33, -1.20 | 10 s | Battle, BattleTeams, Nodes, NodesTeams, Bounty, BountyTeams, Defender, Survival |
| 25 | UA big | -44.38, 1.31, 20.02 | 10 s | Battle, BattleTeams, Nodes, NodesTeams, Bounty, BountyTeams, Defender, Survival |
| 26 | Health small | -53.73, 2.06, 36.99 | 15 s | Battle, BattleTeams, Nodes, NodesTeams, Bounty, BountyTeams, Defender, Survival |
| 29 | Health small | 14.42, 14.47, -11.16 | 20 s | Battle, BattleTeams, Nodes, NodesTeams, Bounty(3P/4P), BountyTeams, Defender, Survival |
| 30 | Health small | 14.42, 14.47, 11.11 | 20 s | Battle, BattleTeams, Nodes, NodesTeams, Bounty(4P), BountyTeams, Defender, Survival |
| 19 | Health small | -30.49, 18.46, -43.60 | 20 s | all |
| 34 | Health small | -52.60, 2.06, -37.39 | 15 s | Battle, BattleTeams, Nodes, NodesTeams, Bounty, BountyTeams, Defender, Survival |
| 37 | Health big | -37.18, 1.31, 6.28 | 20 s | Battle, BattleTeams, Nodes, NodesTeams, Bounty, BountyTeams, Defender, Survival |
| 41 | Missile big | 25.30, 1.84, 7.87 | 10 s | Battle, BattleTeams, Nodes, NodesTeams, Bounty, BountyTeams, Defender, Survival |
| 17 | UA big | -33.42, 1.59, -40.86 | 10 s | Battle, BattleTeams, Nodes, NodesTeams, Bounty, BountyTeams, Defender, Survival |
| 58 | UA big | 8.33, 1.87, -12.29 | 10 s | Battle, BattleTeams, Nodes, NodesTeams, Bounty, BountyTeams, Defender, Survival |
| 1 | **AFFINITY** | -20.46, 1.87, 0.00 | 5 s | Nodes, NodesTeams, Bounty, BountyTeams, Defender |
| 6 | Magmaul | -45.87, 1.31, 19.29 | 5 s | all |
| 8 | Battlehammer | -35.32, 1.64, -39.97 | 5 s | all |

### 115 UNIT2 LANDING BAY | Celestial Gateway

| # | Item | Position (x, y, z) | Respawn | Modes |
|---|---|---|---|---|
| 11 | Battlehammer | 19.71, 11.00, 0.00 | 5 s | Battle, BattleTeams, Nodes, NodesTeams, Bounty, Capture, Defender, Survival |
| 14 | **AFFINITY** | 0.02, 3.00, 0.03 | 5 s (+5 s first) | Battle, BattleTeams, Bounty, Capture, Survival |
| 17 | UA big | 12.17, 0.00, 12.11 | 10 s | Battle, BattleTeams, Nodes, NodesTeams, Bounty, Capture, Defender, Survival |
| 20 | Missile big | -14.36, 11.00, 0.01 | 10 s | Battle, BattleTeams, Nodes, NodesTeams, Capture, Defender, Survival |
| 5 | UA big | -12.11, 0.00, 12.17 | 10 s | Battle, BattleTeams, Nodes, NodesTeams, Bounty, Capture, Defender, Survival |
| 18 | UA big | -12.17, 0.00, -12.11 | 10 s | Battle, BattleTeams, Nodes, NodesTeams, Bounty, Capture, Defender, Survival |
| 19 | UA big | 12.11, 0.00, -12.17 | 10 s | Battle, BattleTeams, Nodes, NodesTeams, Bounty, Capture, Defender, Survival |
| 12 | Health small | -0.01, 13.00, 24.38 | 15 s | Battle, BattleTeams, Defender, Survival |
| 13 | Health small | 0.01, 13.00, -24.32 | 15 s | Battle, BattleTeams, Defender, Survival |
| 16 | Health big | -24.05, 0.00, -0.11 | 20 s | BattleTeams, Nodes(3P/4P), NodesTeams |
| 15 | Health small | -24.05, 0.00, -0.09 | 15 s | Battle(3P/4P), Nodes(2P), Defender, Survival |
| 21 | Health small | 24.31, 13.00, -0.00 | 15 s | Nodes(2P) |
| 22 | Health small | 19.95, 3.00, 0.00 | 15 s | Nodes(3P) |
| 24 | Health big | 19.95, 3.00, 0.00 | 20 s | Nodes(4P), NodesTeams |
| 25 | Health small | 24.38, 13.00, 0.02 | 15 s | Capture |
| 26 | Health big | -24.02, 0.00, -0.10 | 20 s | Capture |
| 29 | Health small | 7.18, -10.50, 0.00 | 15 s | all |
| 30 | ShockCoil | -7.25, -10.50, 0.00 | 10 s | all |
| 31 | Magmaul | 0.00, 3.00, 19.95 | 5 s | all |
| 39 | Judicator | 0.00, 3.00, -19.88 | 5 s | all |
| 40 | **AFFINITY** | 0.02, 3.00, 10.19 | 5 s (+5 s first) | Nodes, NodesTeams, Defender |

### 116 UNIT 3 VESPER STARPORT | VDO Gateway

| # | Item | Position (x, y, z) | Respawn | Modes |
|---|---|---|---|---|
| 2 | Health big | 0.00, 10.42, 0.00 | 20 s | Battle(4P), BattleTeams, Capture |
| 12 | Imperialist | -9.37, 18.50, 9.34 | 6 s | Battle, BattleTeams, Nodes, NodesTeams, Capture, Defender, Survival |
| 13 | Magmaul | 9.24, 18.50, -9.29 | 5 s | Battle, BattleTeams, Nodes, NodesTeams, Capture, Defender, Survival |
| 16 | Double damage | -0.01, -5.19, -0.02 | 60 s | Battle, BattleTeams, Capture, Survival |
| 18 | **AFFINITY** | -19.14, 0.00, 19.12 | 5 s | Battle, BattleTeams, Nodes, NodesTeams, Capture, Defender, Survival |
| 19 | Health small | -11.00, 18.50, -11.01 | 15 s | Battle(4P), BattleTeams, Capture, Defender |
| 23 | Health small | 11.00, 18.50, 11.01 | 15 s | Battle(4P), BattleTeams, Capture, Defender |
| 0 | UA big | 20.14, 0.00, -18.18 | 10 s | Battle, BattleTeams, Nodes, NodesTeams, Capture, Defender, Survival |
| 14 | UA big | 18.14, 0.00, -20.18 | 10 s | Battle, BattleTeams, Nodes, NodesTeams, Capture, Defender, Survival |
| 32 | UA big | -20.14, 0.00, 18.18 | 10 s | Battle, BattleTeams, Nodes, NodesTeams, Capture, Defender, Survival |
| 33 | UA big | -18.14, 0.00, 20.18 | 10 s | Battle, BattleTeams, Nodes, NodesTeams, Capture, Defender, Survival |
| 37 | Missile big | 22.99, 7.00, -22.31 | 20 s | Battle, BattleTeams, Nodes, NodesTeams, Capture, Defender, Survival |
| 39 | Missile big | -22.99, 7.00, 22.31 | 20 s | Battle, BattleTeams, Nodes, NodesTeams, Capture, Defender, Survival |
| 40 | Health big | -0.01, -5.19, -0.02 | 10 s | Nodes(4P), NodesTeams |
| 41 | Health small | 31.06, 7.00, -7.82 | 15 s | Nodes, NodesTeams |
| 42 | Health small | 7.82, 7.00, -31.06 | 15 s | Nodes, NodesTeams |
| 43 | Health small | -31.06, 7.00, 7.82 | 15 s | Nodes, NodesTeams |
| 44 | Health small | -7.82, 7.00, 31.06 | 15 s | Nodes, NodesTeams |
| 60 | Health small | 0.00, 10.42, 0.00 | 20 s | Battle(2P/3P), Survival |
| 61 | Health small | -0.01, -5.19, -0.02 | 10 s | Nodes(2P/3P), Defender |
| 62 | Health small | 11.00, 18.50, 11.01 | 15 s | Battle(2P/3P), Survival |
| 63 | Health small | -11.00, 18.50, -11.01 | 15 s | Battle(2P/3P), Survival |
| 17 | **AFFINITY** | 19.14, 0.00, -19.18 | 5 s | Battle, BattleTeams, Nodes, NodesTeams, Capture, Defender, Survival |

### 117 UNIT 4 ARCTERRA BASE | Arcterra Gateway

| # | Item | Position (x, y, z) | Respawn | Modes |
|---|---|---|---|---|
| 25 | UA big | 2.34, 0.90, -12.03 | 10 s | Battle, BattleTeams, Nodes, NodesTeams, Capture, Defender, Survival |
| 27 | UA big | 13.00, 18.85, -3.69 | 10 s | Battle, BattleTeams, Capture, Defender, Survival |
| 29 | UA big | -14.88, 23.55, 3.39 | 10 s | Battle, BattleTeams, Nodes, NodesTeams, Capture, Defender, Survival |
| 30 | **AFFINITY** | 17.06, 18.85, -16.46 | 5 s | Nodes, NodesTeams |
| 31 | UA big | 15.93, 18.85, -17.80 | 10 s | Nodes, NodesTeams |
| 41 | Missile big | -4.67, 5.85, 3.29 | 10 s | Battle, BattleTeams, Nodes, NodesTeams, Capture, Defender, Survival |
| 53 | Health big | -5.11, 0.10, -0.04 | 15 s | Battle, BattleTeams, Nodes, NodesTeams, Capture, Defender, Survival |
| 54 | UA big | -11.95, 0.41, 16.78 | 15 s | Battle, BattleTeams, Nodes, NodesTeams, Capture, Defender, Survival |
| 55 | Health small | -1.62, 20.12, -18.16 | 15 s | Battle(3P/4P), BattleTeams, Nodes(3P/4P), NodesTeams, Defender |
| 56 | Health small | 1.35, 24.80, 21.63 | 15 s | Battle(3P/4P), BattleTeams, Nodes(3P/4P), NodesTeams, Defender |
| 58 | Health small | -4.81, 31.87, 6.82 | 15 s | Battle, BattleTeams, Nodes, NodesTeams, Capture, Survival |
| 0 | Missile big | 6.61, 31.87, -5.77 | 15 s | Battle, BattleTeams, Nodes, NodesTeams, Capture, Defender, Survival |
| 2 | **AFFINITY** | 0.00, 32.10, 1.01 | 5 s | all |

### 118 Gorea Prison | Oubliette

| # | Item | Position (x, y, z) | Respawn | Modes |
|---|---|---|---|---|
| 0 | Omega Cannon | -1.19, 37.94, 0.58 | 20 s | Battle, BattleTeams, Survival |
| 11 | Missile big | 11.78, 17.70, 0.51 | 10 s | Battle, BattleTeams, Nodes, NodesTeams, Defender, Survival |
| 13 | Health big | 3.15, 22.55, -0.08 | 20 s | Battle, BattleTeams, Nodes, NodesTeams, Defender, Survival |
| 19 | Missile big | -10.99, 27.03, 8.37 | 10 s | Battle, BattleTeams, Nodes, NodesTeams, Defender, Survival |
| 20 | Health small | 3.71, 8.80, 2.93 | 15 s | Battle, BattleTeams, Nodes, NodesTeams, Defender, Survival |
| 21 | Health small | -8.49, 31.90, 10.66 | 15 s | Battle, BattleTeams, Nodes, NodesTeams, Defender, Survival |
| 9 | **AFFINITY** | 0.01, 8.76, -0.04 | 5 s | Defender |

### 119 E3 FIRST HUNT | Stasis Bunker

| # | Item | Position (x, y, z) | Respawn | Modes |
|---|---|---|---|---|
| 1 | VoltDriver | -9.22, 0.30, -1.17 | 5 s | Battle, BattleTeams, Nodes, NodesTeams, Capture, Defender, Survival |
| 2 | Battlehammer | 9.08, 0.30, 1.26 | 5 s | Battle, BattleTeams, Nodes, NodesTeams, Capture, Defender, Survival |
| 4 | UA big | 26.11, 2.50, 2.49 | 15 s | Battle, BattleTeams, Nodes(2P/3P), Capture, Defender, Survival |
| 5 | UA big | 26.12, 2.50, -12.66 | 15 s | Battle, BattleTeams, Nodes, NodesTeams, Capture, Defender, Survival |
| 6 | Imperialist | -12.07, 11.69, -15.22 | 5 s | Battle, BattleTeams, Nodes, NodesTeams, Capture, Defender, Survival |
| 9 | **AFFINITY** | 14.94, 8.12, -4.99 | 5 s | Battle, BattleTeams, Nodes, NodesTeams, Capture, Defender, Survival |
| 15 | Health big | 11.33, 8.75, -14.34 | 30 s | Battle(3P/4P), BattleTeams, Nodes(3P/4P), NodesTeams, Capture, Defender |
| 18 | UA big | -21.55, 2.50, -25.63 | 20 s | Battle, BattleTeams, Nodes, NodesTeams, Capture, Defender, Survival |
| 16 | Health small | 1.07, 8.00, -13.75 | 15 s | Battle, BattleTeams, Nodes, NodesTeams, Capture, Defender, Survival |
| 19 | UA big | 23.21, 8.00, 6.40 | 15 s | Battle, BattleTeams, Nodes, NodesTeams, Capture, Defender, Survival |
| 10 | Health small | 9.99, 0.00, -12.01 | 10 s | Battle, BattleTeams, Nodes, NodesTeams, Capture, Defender, Survival |
| 11 | UA big | 2.57, 0.00, 13.98 | 20 s | Battle, BattleTeams, Nodes, NodesTeams, Capture, Defender, Survival |
| 22 | Missile big | -3.91, 13.88, 8.94 | 15 s | Battle, BattleTeams, Nodes, NodesTeams, Capture, Defender, Survival |
| 7 | Missile big | 3.94, 13.86, -8.90 | 15 s | Battle, BattleTeams, Nodes, NodesTeams, Capture, Defender, Survival |
| 20 | Health small | -15.36, 3.20, -13.33 | 15 s | Battle(3P/4P), BattleTeams, Nodes(3P/4P), NodesTeams |
| 50 | Health small | 11.33, 8.75, -14.34 | 15 s | Battle(2P), Nodes(2P), Survival |

### ECHOES echoes_m01

| # | Item | Position (x, y, z) | Respawn | Modes |
|---|---|---|---|---|
| 4 | Missile small | 32.86, 6.81, -6.63 | 15 s | all |
| 5 | Missile small | -0.15, 0.09, -5.72 | 15 s | all |
| 6 | Missile small | -29.18, 6.81, -8.56 | 15 s | all |
| 7 | Missile small | -0.10, 0.09, -24.98 | 15 s | all |
| 8 | Missile small | -51.56, 0.12, 54.67 | 15 s | all |
| 9 | Missile small | -11.85, 13.04, -20.74 | 15 s | all |
| 10 | Missile small | 11.85, 12.90, -20.74 | 15 s | all |
| 11 | Missile small | 51.16, 0.12, 54.57 | 15 s | all |
| 12 | Health small | -23.55, 9.55, -11.56 | 15 s | all |
| 13 | Health small | -41.24, 0.12, 54.54 | 15 s | all |
| 14 | Health medium | 5.23, 0.07, -34.86 | 15 s | all |
| 15 | Health medium | 46.52, 0.12, 23.70 | 15 s | all |
| 16 | Health small | -56.91, 0.10, 36.40 | 15 s | all |
| 17 | Health small | 41.04, 0.12, 54.96 | 15 s | all |
| 18 | Health small | 56.74, 0.10, 35.70 | 15 s | all |
| 19 | Missile small | -3.51, 8.00, 4.94 | 15 s | all |
| 20 | Health medium | -46.32, 0.10, 23.70 | 15 s | all |
| 21 | Missile small | 3.12, 8.00, 4.93 | 15 s | all |
| 22 | Health small | -37.48, 0.09, -1.33 | 15 s | all |
| 23 | Health small | 36.89, 0.09, -1.78 | 15 s | all |
| 24 | Health small | -18.37, 0.06, -16.44 | 15 s | all |
| 25 | Health medium | 0.00, 13.01, -38.52 | 15 s | all |
| 26 | Health small | 18.07, 0.05, -16.74 | 15 s | all |
| 27 | Health small | 23.52, 9.55, -11.49 | 15 s | all |
| 28 | Health medium | -5.66, 0.06, -34.75 | 15 s | all |

### ECHOES echoes_m02

| # | Item | Position (x, y, z) | Respawn | Modes |
|---|---|---|---|---|
| 10 | Missile small | -7.88, 2.07, -105.93 | 15 s | all |
| 11 | Missile small | -6.08, 4.44, 52.27 | 15 s | all |
| 12 | Missile small | 8.62, 4.44, 73.74 | 15 s | all |
| 13 | Missile small | 12.66, 2.08, -87.85 | 15 s | all |
| 14 | Health small | -21.70, 4.44, 65.18 | 15 s | all |
| 15 | Health small | 5.84, 4.44, 52.80 | 15 s | all |
| 16 | Health small | -8.51, 4.44, 73.60 | 15 s | all |
| 17 | Health small | 7.16, 2.07, -105.59 | 15 s | all |
| 18 | Health medium | -0.05, 2.08, -105.55 | 15 s | all |
| 19 | Health small | 21.47, 4.44, 65.29 | 15 s | all |
| 20 | Health small | -12.79, 2.07, -87.79 | 15 s | all |

### ECHOES echoes_m03

| # | Item | Position (x, y, z) | Respawn | Modes |
|---|---|---|---|---|
| 4 | Missile small | -4.61, 0.32, 36.83 | 15 s | all |
| 5 | Missile small | 4.63, 0.33, 34.01 | 15 s | all |
| 6 | Missile small | 4.43, 0.24, -57.32 | 15 s | all |
| 7 | Missile small | 7.42, 0.99, -40.04 | 15 s | all |
| 8 | Missile small | -7.37, 0.32, 16.15 | 15 s | all |
| 9 | Missile small | -7.86, -1.49, -15.73 | 15 s | all |
| 10 | Missile small | 7.88, -1.49, -7.78 | 15 s | all |
| 11 | Missile small | -4.12, 0.23, -60.68 | 15 s | all |
| 12 | Health small | -14.92, 0.17, 18.67 | 15 s | all |
| 13 | Health medium | 4.49, 0.56, 28.06 | 15 s | all |
| 14 | Health small | 14.70, 0.16, -42.68 | 15 s | all |
| 15 | Health small | -14.88, 0.25, 22.41 | 15 s | all |
| 16 | Health small | 20.24, 0.10, 8.07 | 15 s | all |
| 17 | Health small | 7.97, 0.04, 6.11 | 15 s | all |
| 18 | Health medium | -4.32, 0.58, -51.67 | 15 s | all |
| 19 | Health small | 14.66, 0.24, -46.42 | 15 s | all |
| 20 | Health small | -9.40, 0.15, -29.36 | 15 s | all |
| 21 | Health small | -18.90, -0.13, -29.39 | 15 s | all |

### ECHOES echoes_m04

| # | Item | Position (x, y, z) | Respawn | Modes |
|---|---|---|---|---|
| 8 | Missile small | -0.76, 5.83, 0.38 | 15 s | all |
| 9 | Missile small | 34.82, 2.39, -29.12 | 15 s | all |
| 10 | Missile small | 34.82, 2.39, 29.14 | 15 s | all |
| 11 | Missile small | 9.20, 6.32, 9.43 | 15 s | all |
| 12 | Missile small | -15.99, 7.00, 0.05 | 15 s | all |
| 13 | Health small | -21.46, -1.59, -12.97 | 15 s | all |
| 14 | Health small | -21.46, -1.79, 13.44 | 15 s | all |
| 15 | Health small | -8.23, -0.53, -33.00 | 15 s | all |
| 16 | Health small | -19.72, -1.75, 21.21 | 15 s | all |
| 17 | Health small | 41.94, 3.60, -29.23 | 15 s | all |
| 18 | Health small | -19.72, -1.67, -21.44 | 15 s | all |
| 19 | Health small | -8.23, -0.39, 32.58 | 15 s | all |
| 20 | Health small | 41.85, 3.58, 29.27 | 15 s | all |
| 21 | Health small | 18.71, 0.09, -27.90 | 15 s | all |
| 22 | Health small | 18.71, 0.09, 28.06 | 15 s | all |
| 23 | Health medium | 34.83, 3.90, 0.06 | 15 s | all |

### ECHOES echoes_m05

| # | Item | Position (x, y, z) | Respawn | Modes |
|---|---|---|---|---|
| 8 | Missile small | 30.73, 5.10, 14.49 | 15 s | all |
| 9 | Missile small | -22.10, 5.09, -6.70 | 15 s | all |
| 10 | Missile small | 9.95, 17.14, 0.31 | 15 s | all |
| 11 | Missile small | 6.66, 14.11, -6.15 | 15 s | all |
| 12 | Missile small | -5.76, 20.29, -5.73 | 15 s | all |
| 13 | Double damage | 0.41, 14.96, 9.35 | 60 s | all |
| 14 | Missile small | -9.12, 16.99, 0.48 | 15 s | all |
| 15 | Missile small | -23.72, 6.80, 22.11 | 15 s | all |
| 16 | Missile small | 9.23, 8.06, 30.14 | 15 s | all |
| 17 | Health small | 0.26, 16.88, 9.88 | 15 s | all |
| 18 | Health small | -7.83, 8.06, 29.40 | 15 s | all |
| 19 | Health small | -5.79, 14.02, -5.30 | 15 s | all |
| 20 | Health small | 31.02, 5.10, 1.65 | 15 s | all |
| 21 | Health small | -23.80, 5.40, 16.49 | 15 s | all |
| 22 | Health medium | 18.90, 16.93, -0.15 | 15 s | all |
| 23 | Health medium | -17.49, 16.93, -0.00 | 15 s | all |
| 24 | Health small | 6.55, 20.13, -5.75 | 15 s | all |

### ECHOES echoes_m06

| # | Item | Position (x, y, z) | Respawn | Modes |
|---|---|---|---|---|
| 4 | Missile small | 20.79, 8.30, -11.80 | 15 s | all |
| 5 | Missile small | 0.05, 1.19, -17.72 | 15 s | all |
| 6 | Missile small | -20.69, 8.30, -11.80 | 15 s | all |
| 7 | Missile small | 14.87, 2.95, -47.35 | 15 s | all |
| 8 | Missile small | 14.87, 2.37, -26.61 | 15 s | all |
| 9 | Missile small | 0.05, 8.30, -26.61 | 15 s | all |
| 10 | Missile small | 26.72, 2.37, -35.50 | 15 s | all |
| 11 | Health small | -14.81, 2.96, -47.41 | 15 s | all |
| 12 | Health small | -5.93, 1.52, -17.78 | 15 s | all |
| 13 | Health small | 38.52, 2.37, -23.70 | 15 s | all |
| 14 | Health small | -38.52, 2.37, -23.70 | 15 s | all |
| 15 | Health small | -14.81, 2.40, -26.67 | 15 s | all |
| 16 | Health medium | 50.37, 8.33, -11.85 | 15 s | all |
| 17 | Health small | 5.93, 1.52, -17.78 | 15 s | all |
| 18 | Health small | -26.67, 2.37, -35.56 | 15 s | all |
| 19 | Health medium | 0.00, 8.30, -38.52 | 15 s | all |
| 20 | Health medium | -50.37, 8.30, -11.85 | 15 s | all |


## Echoes affinity crate: owner picks (2026-10-09)

One affinity crate per Echoes arena, always present (not under the Extra pickups rule). All Echoes crates are up at
match start, then use the normal Echoes 30 s respawn. The owner marked these on `extract_out/pickups/sheet_echoes.png`
and confirmed them on `extract_out/pickups/picks_echoes.png`. Crates go exactly at the marks; the owner judges them in-game.

| Arena | Crate floor position (x, y, z) | Notes |
|---|---|---|
| Crossfire Chaos (m03) | -8.0, -1.5, -7.6 | candidate C, against the middle section's west wall |
| Pipeline (m04) | 7.2, -5.7, 1.4 | lower basin beside the round pillar, open floor |
| Shooting Gallery (m06) | -0.1, 2.4, -12.9 | centre corridor between the two existing crates |
| Sidehopper Station (m01) | -1.4, 0.1, 1.9 | candidate A: main hall front wall, centre (the owner's mark had no floor under it) |
| Spider Complex (m05) | 0.6, 23.5, 0.1 | top of the central tower (both launchers land there) |
| Spires (m02) | -0.5, 2.1, -124.4 | north end of the lower platform |

## Extra pickups rule (built 2026-10-10)

EXTRA PICKUPS is ONE field with the blaster switch: `MatchSettings.PirateBlaster` (off = no Pirate Blaster, 6-slot ring and
touch select). An ADVANCED box row for local, LAN and online, default on, persisted in `extra_rules.json` beside the license
(`Core/Multiplayer/ExtraRules.cs`); off also turns EXTRA CHARACTERS off. The Extra pickups placement data (step 4) will hang
off the same field. Checks: `-extrarules check` (22/22).

## Baked placements (release 0.2.0, 2026-10-11)

The owner's 23 placement files (pulled from the Odin 2026-10-11 10:49) ship inside the app:
`MphRecomp.Core/Arenas/Placements/*.json`, embedded as `placements/<file>`, read by `Arenas/PlacedPickups.cs`. With EXTRA
PICKUPS on, a match in one of those arenas gets all of its file: the placed spawners (respawn as the file says), the Echoes
affinity crates, and the arena's own spawners/crates the owner deleted taken out (none in this set). Arenas without a file,
and every match with EXTRA PICKUPS off, are exactly vanilla. The device's own `<data>/placements` folder is only the dev
editor's: it never changes a real match, and the dev solo run doesn't get the baked set (its editor loads the device files).
Crates follow the Echoes crate rule (local matches and replays; not LAN/online until crates are synced). Replay format 29
(older = none), LAN protocol 15 / online 14. Check: `-placetest baked` (all 23: off = vanilla, on = placed + deleted,
old replay = vanilla).

## Placement editor (dev, 2026-10-10)

The owner places pickups in-game instead of picking from map pictures. Dev launcher > Solo arena > "Place pickups
(D-pad + Select): ON" > Start solo (`--es solo 1 --es place 1`). In the run: D-pad left/right picks what to place
(Pirate Blaster, Affinity weapon, the weapons, health/missile/UA, double damage, cloak, Deathalt; in an Echoes arena
also "Affinity crate"), D-pad up places it at your feet, Select places it where your aim meets the floor, D-pad down
deletes the nearest pickup or crate (yours or the arena's own). The status line names the choice and what D-pad down
would delete. Each placement is live at once (a real item spawner, or an Echoes crate holding the affinity weapon).

Every change is saved to `<data>/placements/<room name>.json` on the device
(`/sdcard/Android/data/com.mphrecomp.app/files/placements/`, e.g. `ECHOES_SPIRES.json`, `MP3_PROVING_GROUND.json`):
`Pickups` (item, floor position, respawn s), `Crates` (pool, centre, floor), `RemovedItems` (the arena's own spawners
deleted: entity id + item + position), `RemovedCrates` (the arena's own Echoes crates deleted, by position). An Echoes
arena with no file yet starts with the owner's picked affinity crate from the table above. These files are the
owner's picks for step (4): they are baked into the "Extra pickups" placement data.
Code: `MphRecomp.Core/Arenas/PickupPlacement.cs` (logic + file), `MphRecomp.App/Campaign/PickupPlacer.cs` (controls +
status); test `-placetest` (Tools).
