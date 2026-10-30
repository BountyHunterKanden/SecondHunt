using System;
using System.Collections.Generic;
using MphRead;

namespace MphRecomp.Multiplayer
{
    // One seat in a match: the player this device controls (MatchSettings.LocalSeat = PlayerEntity.Main), a bot, or
    // a remote human whose input arrives over the network (the host feeds it; PlayerEntity.HostRemote).
    public sealed class MatchPlayer
    {
        public Hunter Hunter { get; set; } = Hunter.Samus;
        public int Recolor { get; set; } // the hunter's palette (team modes override it with the team colour)
        public int Team { get; set; } = -1; // 0 = orange, 1 = green; -1 = assign automatically in team modes
        public bool Bot { get; set; }
        public bool Remote { get; set; } // a human on another device
        public int BotLevel { get; set; } // 0-2, MphRead's PlayerAi skill tiers
        public string? Nickname { get; set; }
        // the player's Hunter License rank level (0-4, the vanilla menu's ranking points), -1 = not known (a bot)
        public int RankLevel { get; set; } = -1;
        // the seat's look (docs/CHARACTERS_AND_SKINS.md, Characters): an HD model folder under hd/ on this hunter's rig
        // (MP1VariaSuit, MP1WaveTrooper, ...), null = the hunter's own DS model. Cosmetic only: the match is the same
        // without it (a device that hasn't imported it draws the DS model)
        public string? Skin { get; set; }
        // the seat's character when it isn't the hunter itself (Characters.cs): "trooper" = the Trooper, Weavel's moves +
        // his Shield Wall. Unlike the skin this changes play, so replays carry it (format 6); null = the hunter's own
        public string? Character { get; set; }
        // the seat's lobby colour pick (owner 2026-10-29: "default to normal seat color and allow someone to pick another
        // color if it's not taken"): a copy number 0-7 (0 = its look's default; LookAlikes.CopyIndices), -1 = automatic.
        // Team modes wear the team colours whatever it is
        public int ColourPick { get; set; } = -1;
    }

    // A match as the vanilla GAME OPTIONS screen describes it: mode, arena, seats and the rule overrides. Any rule
    // left null keeps the mode's own default (GameState.Setup: e.g. battle 7 points / 7 minutes).
    public sealed class MatchSettings
    {
        public GameMode Mode { get; set; } = GameMode.Battle;
        public string Arena { get; set; } = "MP3 PROVING GROUND"; // a RoomMetadata name (MatchArenas)
        public List<MatchPlayer> Players { get; } = new();
        // the seat this device plays (the host is seat 0; a match client plays its own seat)
        public int LocalSeat { get; set; }
        // this device mirrors a host (MatchClient.ForSeat): damage and scores come from the host's snapshots
        public bool IsClient { get; set; }
        // a served match host (docs/DEDICATED_SERVERS.md): this device plays no seat, every human is remote (LocalSeat
        // only keeps PlayerEntity.Main set); never sent to the players
        public bool ServedHost { get; set; }
        // a served match on Fruity Prime's movement sync (docs/FRUITY_PORT.md P1; MatchHost --fruity-p1): sent to the joiners
        // as a trailing byte on MsgWelcome/MsgStart (Net/FruitySync.cs); phone-hosted and LAN matches never set it
        public bool FruitySync { get; set; }

        public int? PointGoal { get; set; } // kills / octoliths / node points; spare lives in survival
        public float? TimeLimitSeconds { get; set; }
        public float? TimeGoalSeconds { get; set; } // defender / prime hunter
        public int? DamageLevel { get; set; } // 0 low, 1 medium, 2 high
        public bool? FriendlyFire { get; set; }
        public bool? OctolithReset { get; set; }
        public bool? RadarPlayers { get; set; }
        public bool? AffinityWeapons { get; set; }
        // Echoes arenas' weapon crates (Core Arenas/EchoesCrates): null = on (forced off in LAN matches until crate sync);
        // replays recorded before crates (format < 4) read as off, so they play exactly
        public bool? Crates { get; set; }
        // Echoes arenas' breakables and Dark Water (Core Arenas/EchoesBreakables): null = on (off in LAN matches); replays
        // recorded before them (format < 5) read as off, so they play exactly
        public bool? Breakables { get; set; }
        // Echoes arenas' Echoes movement (Core Arenas/EchoesMovement: double jump, water, Gravity Boost, spider ball):
        // null = on; replays recorded before it (format < 13) read as off, so they play exactly
        public bool? EchoesMoves { get; set; }
        // with EchoesMoves: a jump just after leaving the floor keeps the double jump (EchoesMovement.LedgeJump): null =
        // on; replays recorded before it read as off, so they play exactly
        public bool? EchoesLedgeJump { get; set; }
        // Echoes arenas' UA pickups (Core Arenas/EchoesAmmo, T6 N5): null = on; replays recorded before them (format < 24)
        // read as off, so they play exactly
        public bool? EchoesAmmo { get; set; }
        // the owner's baked pickup placements (Core Arenas/PlacedPickups, 0.2.0), only with EXTRA PICKUPS (PirateBlaster)
        // on: null = on; replays recorded before them (format < 29) read as off, so they play exactly
        public bool? PlacedPickups { get; set; }
        // with EchoesMoves: no fall damage on landing from a jump pad's flight (EchoesMovement.PadNoFall, T6 N4): null = on;
        // replays recorded before it (format < 24) read as off
        public bool? EchoesPadNoFall { get; set; }
        // bots in Echoes arenas take the graph's double-jump, gap and boost links and skip items they can't get
        // (PlayerEntity.HostBotMoves, T7 N24): null = on; replays recorded before it (format < 28) read as off
        public bool? EchoesBotMoves { get; set; }
        // bots think at vanilla's 30 Hz pace: aim turn speed, per-tick random rolls, shot leading (PlayerEntity
        // HostBotTickPace): null = on; replays recorded before it (format < 30) read as off, so they play exactly
        public bool? BotTickPace { get; set; }
        // with friendly fire off, a teammate's hit is no hit at all (PlayerEntity.HostTeamHitsIgnored: no knockback,
        // invulnerability, hurt sound or bot reaction; vanilla does those with 0 damage): null = on; replays recorded
        // before it (format < 31) read as off, so they play exactly
        public bool? TeamHitsIgnored { get; set; }
        // the Trooper's Wall Cling only after a jump or a real fall (PlayerEntity.HostWallClingJumpRule, T5 N10) and his
        // Scout Drone on a jump pad flying the pad's normal arc (HostDronePadArc, T5 N9): null = on; replays recorded
        // before them (format < 25) read as off, so they play exactly
        public bool? WallClingJumpRule { get; set; }
        public bool? DronePadArc { get; set; }
        // Dark Samus without the walk bob (PlayerEntity.HostHoverWalk, T5 N13; the bob moves her muzzle): null = on;
        // replays recorded before it (format < 25) read as off
        public bool? HoverWalk { get; set; }
        // Dark Samus's morph ball: the jump button hops it as high as a bomb jump and the bomb button held is Corruption's
        // Hyper Ball, paid from UA, instead of bombs (PlayerEntity.HostBallJump / HostHyperBall, docs/DARK_SAMUS_HYPER.md):
        // null = on (off in a public build: MatchStart); replays recorded before it (format < 32) read as off, 32-44 as on,
        // and from format 45 the file says (ReplayFile), so they play exactly
        public bool? HyperBall { get; set; }
        // ...without the ball hop (owner 2026-10-18: "remove morph ball jump entirely for dark Samus"): null = on (no hop);
        // replays recorded before it (format < 44) read as off, so their hops still play exactly
        public bool? NoBallJump { get; set; }
        // an online host's lag compensation as fixed 2026-10-19 (PlayerEntity.HostLagFix: a homing beam isn't shifted off its
        // own target, Weavel's turret's shots aren't shifted, bombs, Lockjaw and the drone laser are): null = on; replays recorded before it (format < 46)
        // read as off, so their remote seats' hits play exactly
        public bool? LagCompFix { get; set; }
        // the Trooper's drone laser pushes nothing (PlayerEntity.HostDroneLaserNoPush, 2026-10-19: it rarely sent a target
        // flying): null = on; replays recorded before it (format < 47) read as off, so their laser hits play exactly
        public bool? DroneLaserNoPush { get; set; }
        // the Trooper's split/merge spam fix (PlayerEntity.HostDroneSwitchRule, 2026-10-19: morphing over and over let him
        // fly at his height): his drone ignores his own standing body and the next switch waits 20 frames; null = on; replays
        // recorded before it (format < 48) read as off
        public bool? DroneSwitchRule { get; set; }
        // the drone's burst and laser waits count down while the Trooper is on foot too (PlayerEntity.HostDroneWaitsOnFoot,
        // 2026-10-19: they froze until he split again): null = on; replays recorded before it (format < 49) read as off
        public bool? DroneWaitsOnFoot { get; set; }
        // an online host takes its guests' hit claims (PlayerEntity.HostHitClaims, MphRecomp Net/HitClaims.cs,
        // 2026-10-19): a hit a guest's screen saw that the host's copy of the shot missed is applied once the host checked it
        // against its own history; null = on; replays recorded before it (format < 50) read as off
        public bool? HitClaims { get; set; }
        // the Trooper clings once per airtime, no chained wall hops (PlayerEntity.HostWallClingOnce, 2026-10-19); null = on;
        // replays recorded before it (format < 51) read as off (3 clings)
        public bool? WallClingOnce { get; set; }
        // the Pirate Blaster's bolts deal 10 (14 to the head) instead of 12 (16) (PirateBlaster.HostBoltDamage,
        // 2026-10-19: a bit too strong); null = on; replays recorded before it (format < 51) read as off
        public bool? LighterBlaster { get; set; }
        // splash damage reaches the Trooper's standing body while his drone is out (PlayerEntity.HostDroneBodySplash,
        // 2026-10-19); null = on; replays recorded before it (format < 52) read as off (it took no splash, as Weavel's turret)
        public bool? DroneBodySplash { get; set; }
        // one hit per blast on the Trooper: a blast that hit him leaves his standing body alone
        // (BeamProjectileEntity.HostBodySplashOnce, 2026-10-19); null = on; replays before format 53 read as off
        public bool? BodySplashOnce { get; set; }
        // the Trooper clings only while Jump is held, and not in his first 0.3 s after a jump or leaving the ground
        // (PlayerEntity.HostWallClingHold, 2026-10-20: clinging by accident was too easy); null = on;
        // replays before format 56 read as off
        public bool? WallClingHold { get; set; }
        // each Pirate Blaster bolt costs a shot of UA (5), not each burst (PirateBlaster.HostBoltCost, 2026-10-20);
        // null = on; replays before format 56 read as off (5 a burst)
        public bool? BlasterBoltCost { get; set; }
        // the Pirate Blaster's bolts a little lighter again, 9 / 13 instead of LighterBlaster's 10 / 14 (2026-10-20);
        // only counts with LighterBlaster on; null = on; replays before format 56 read as off (10 / 14)
        public bool? LighterBlaster2 { get; set; }
        // bots walk off a ledge toward a drop-down node under it instead of shaking over it (PlayerEntity.HostBotDropOff,
        // 2026-10-20, back on 2026-10-23); null = on; replays before format 57 read as off
        public bool? BotDropOff { get; set; }
        // a bot's camera shake (bomb hits) moves only its view, not its aim, so its gun doesn't snap back and forth
        // (PlayerEntity.HostBotSteadyAim, 2026-10-23); null = OFF since format 59 (owner 2026-10-23: a player's aim takes the
        // shake, so a bot's does too); replays of formats 57-58 read as on, all others as off
        public bool? BotSteadyAim { get; set; }
        // a bot switches to its preferred weapon before checking that the one it holds can charge, as the game's own code
        // reads (PlayerEntity.HostBotSwitchFirst, 2026-10-23); null = on; replays before format 57 read as off
        public bool? BotSwitchFirst { get; set; }
        // a bot doesn't fire at a target player its gun can't see (wall, platform, door), holding a charge until it can
        // (PlayerEntity.HostBotFireSight, PlayerAiSight.cs, 2026-10-23); null = on; replays before format 57 read as off
        public bool? BotFireSight { get; set; }
        // a roaming bot (no target it can see) eases its view toward the slope of its path, level when flat, instead of
        // keeping the pitch of its last aim (PlayerEntity.HostBotRoamLook, PlayerAiRoamLook.cs, 2026-10-23); null = on;
        // replays before format 57 read as off
        public bool? BotRoamLook { get; set; }
        // a Trooper bot wall-clings and wall-jumps as a player does (PlayerEntity.HostBotWallCling, PlayerAiWallCling.cs,
        // 2026-10-23); null = on; replays before format 58 read as off
        public bool? BotWallCling { get; set; }
        // bots break the Echoes arenas' crates and go for the weapon each drops (PlayerEntity.HostBotCrates,
        // PlayerAiCrates.cs, 2026-10-23); null = on; replays before format 58 read as off
        public bool? BotCrates { get; set; }
        // bot code MphRead had off from the ROM's set back to vanilla: steering, stuck checks, aim, the Judicator/Imperialist
        // fire and the charge path's weapon switch, and FireSight's hold cut to 4 ticks (PlayerEntity.HostBotVanillaParity,
        // bots vanilla-parity review 2026-10-24); null = on; replays before format 61 read as off
        public bool? BotVanillaParity { get; set; }
        // with BotVanillaParity, bots in MPH's arenas get unstuck as in the Echoes ones: a jump toward a node above, then the
        // closest node they can see (PlayerEntity.HostBotStuckFix, owner 2026-10-25: bots never stuck); null = on; written
        // from format 62 as the match started (a dev switch in the gun-adjust pause flips it live); older replays read off
        public bool? BotStuckFix { get; set; }
        // with BotVanillaParity, a bot checks its AI node's exits in order on an odd frame too, as vanilla's 30 Hz tick does
        // (PlayerEntity.HostBotSwitchOrder, 10-25: Kanden/Spire left their alt form early); null = on; written from format
        // 63 as the match started (L3 in the gun-adjust pause flips it live); older replays read off
        public bool? BotSwitchOrder { get; set; }
        // with BotVanillaParity, a bot drops a node it stands over by horizontal distance only, as vanilla (rev1 0x21408F4),
        // so one that fell below a ledge node doesn't stand pinned until the stuck timeout (PlayerEntity.HostBotNodeFlat,
        // 10-25: the High Ground column pin); null = on; written from format 64 as the match started (L3 + R3 together in the
        // gun-adjust pause flips it live); older replays read off
        public bool? BotNodeFlat { get; set; }
        // with BotVanillaParity, MPH's stuck fix runs on a bot pinned 3 s in one spot following nodes though it sees a foe
        // (PlayerEntity.HostBotStuckSight, 10-26: Oubliette Spire pinned 9.5 s); null = on; written from format 65 as the
        // match started (a tap in the gun-adjust pause flips it live); older replays read off
        public bool? BotStuckSight { get; set; }
        // with BotVanillaParity, a bot shoots down a breakable wall standing across its way to its next node (Echoes arenas;
        // PlayerEntity.HostBotBreakWalls, 10-26: Crossfire Chaos bots walked into a wall forever); null = on; written from
        // format 66 as the match started (a tap in the gun-adjust pause flips it live); older replays read off
        public bool? BotBreakWalls { get; set; }
        // with BotVanillaParity and BotStuckFix, the stuck fix's clock waits while a bot is off the ground moving up or down
        // (a fall, a lift's shaft; PlayerEntity.HostBotStuckVertical, 10-26 link audit: the fix pushed bots out of Fuel
        // Stack's lifts); null = on; written from format 67 as the match started (a tap in the gun-adjust pause flips it
        // live); older replays read off
        public bool? BotStuckVertical { get; set; }
        // with BotVanillaParity and BotStuckFix, a bot jumps at a ledge or a wall short of its next node, and the stuck fix's
        // climbs become a backstop (step aside, the jump again, a run-up, the node marked bad for 5 s;
        // PlayerEntity.HostBotEdgeJump, docs/BOT_MOVE_SOLVER.md P2); null = on; written from format 68 as the match started (a
        // tap in the gun-adjust pause flips it live); older replays read off
        public bool? BotEdgeJump { get; set; }
        // with BotEdgeJump, a bot thrown by a jump pad and coming down short of its next node jumps when that jump reaches
        // it (PlayerEntity.HostBotEdgePadJump, docs/BOT_MOVE_SOLVER.md P2, owner 10-27: the careful rule); null = on; written
        // from format 69 as the match started (a tap in the gun-adjust pause flips it live); older replays read off
        public bool? BotEdgePadJump { get; set; }
        // a bot lets go of the controls its buttons no longer drive after a morph or unmorph (PlayerEntity.HostBotReleaseControls,
        // 2026-10-28: a held roll steered her next morph); null = on; written from format 70 as the match started; older
        // replays read off
        public bool? BotReleaseControls { get; set; }
        // no stuck-fix climb or edge jump into a lift a bot's route doesn't go up (PlayerEntity.HostBotLiftGuard, owner GO
        // 2026-10-28: Fuel Stack's lift into its death volume); null = on; written from format 71 as the match started (a
        // tap in the gun-adjust pause flips it live); older replays read off
        public bool? BotLiftGuard { get; set; }
        // only a gravity volume that pushes up counts as a lift for the edge jump and the lift guard
        // (PlayerEntity.HostBotUpLifts, owner GO 2026-10-29: Weapons Complex's low-gravity pits read as lifts, so bots
        // walked off their ledges into the pits); null = on; written from format 72 as the match started; older replays
        // read off
        public bool? BotUpLifts { get; set; }
        // a bot sees it reached its next node on 30 Hz tick frames only, like vanilla (PlayerEntity.HostBotArriveTick,
        // 2026-10-29: checked every frame, bots jumped Weapons Complex's north ledge a frame early and fell in the pit);
        // null = on; written from format 72 as the match started; older replays read off
        public bool? BotArriveTick { get; set; }
        // an edge jump's flight keeps its node until she lands (PlayerEntity.HostBotEdgeHold, owner GO 2026-10-29: in the
        // air over Harvester's top platform bots were handed an aerial node out over the gap and fell to the floor);
        // null = on; written from format 73 as the match started; older replays read off
        public bool? BotEdgeHold { get; set; }
        // a claimed shot hits one player at most (PlayerEntity.HostClaimOneHit, 2026-10-19); null = on; replays
        // before format 53 read as off
        public bool? ClaimOneHit { get; set; }
        // an online host takes each guest's own position from its inputs when it passes the host's checks (MphRecomp
        // Net/ClientPosition.cs, 0.3.6): null = on; a match host turns it off when the server says so; replays before format 54
        // read as off (they hold no taken positions)
        public bool? ClientPosition { get; set; }
        // Zero Suit Samus's kit (owner 2026-10-12, docs/ZERO_SUIT_SAMUS.md section 7): her affinity weapon is the Paralyzer
        // (its charged shot stuns, MphRead PlayerParalyzer.cs) and she has 90 health (PlayerEntity.HostHealthPercent):
        // null = on; replays recorded before it (format < 33) read as off, so they play exactly
        public bool? Paralyzer { get; set; }
        // Dark Samus's affinity weapon is the Hyper Beam (docs/DARK_SAMUS_HYPER.md H2, MphRead Metadata/HyperBeam.cs): her
        // affinity pickups give it, and its full charge held is Hyper Mode rapid fire. null = on in a dev build, off in a
        // public one (owner 2026-10-24: "dev only", as the Hyper Ball) and in served matches (SecondHunt.MatchHost); replays
        // say how the match played it from format 60 and older ones read as off, so they play exactly
        public bool? HyperBeam { get; set; }

        // Dark Samus's Hyper Ball by Corruption's own rules (owner 2026-10-12 "Match corruption": every enemy in reach
        // zapped in turn, its arcs to the surfaces kick the ball; PlayerEntity.HostHyperBallCorruption): null = on;
        // replays recorded before it (format < 34) read as off, so they play exactly
        public bool? HyperBallCorruption { get; set; }
        // headshots on the Trooper's standing body while his Scout Drone is out (owner 2026-10-15; PlayerEntity
        // HostDroneBodyHeadshots): the same zone as on any hunter, not the turret's none: null = on; replays recorded before
        // it (format < 35) read as off, so they play exactly
        public bool? DroneBodyHeadshots { get; set; }
        // with EXTRA PICKUPS (PirateBlaster) on: every Echoes arena weapon crate's pool also holds the Pirate Blaster
        // (owner 2026-10-15; EchoesCrates.Blaster; the placed affinity crates keep theirs): null = on; replays recorded
        // before it (format < 36) read as off, so they play exactly
        public bool? CrateBlaster { get; set; }
        // Shooting Gallery's manned turret and bomb slot (Echoes' PlayerTurret, owner 2026-10-15; Arenas/EchoesTurret.cs):
        // null = on (LAN/online too: not on the wire, null on every device; clients mirror the host's, Net/NetWorld.cs);
        // replays recorded before it (format < 37) read as off, so they play exactly
        public bool? EchoesTurret { get; set; }
        // bots use that turret (owner D7/D8 2026-10-16: they sometimes get in and shoot from it, and roll into its bomb slot
        // against an enemy in it; EchoesTurretBots): null = on (the host's bots only); replays recorded before it
        // (format < 41) read as off, so they play exactly
        public bool? EchoesTurretBots { get; set; }
        // same-hunter recolours count copies per character + skin (owner 2026-10-17: Echoes and Corruption
        // Dark Samus, an HD suit and the DS-model hunter are different looks, each in its default colour; LookAlikes.SameLookKey):
        // null = on (every device works it out from the same seats); replays recorded before it (format < 43) read as off
        // (copies per look / hunter), so they play exactly
        public bool? RecolorPerSkin { get; set; }
        // continuous shots (Shock Coil) scale their headshot damage like their damage, as the game does (2026-10-16, a
        // user report: Shock Coil head hits did full damage every frame; PlayerEntity.HostContinuousHeadshots): null = on
        // (not on the wire: the host decides damage); replays recorded before it (format < 38) read as off, so they play exactly
        public bool? ContinuousHeadshots { get; set; }
        // the Shock Coil's ramp kept through one missed tick, dropped after 2 without a hit, as the game does (2026-10-16;
        // PlayerEntity.HostShockCoilGrace): null = on (not on the wire: the host decides damage); replays recorded before it
        // (format < 40) read as off, so they play exactly
        public bool? ShockCoilGrace { get; set; }
        // the zoom (Imperialist) as the game does it (2026-10-16, owner: the zoom felt bad; PlayerEntity.HostVanillaZoom):
        // the zoomed aim speed, the zoom's step in and ease out, the facing's catch-up. null = on (not on the wire: each
        // device plays its own seat's aim); replays recorded before it (format < 39) read as off, so they play exactly
        public bool? VanillaZoom { get; set; }
        // the Pirate Blaster's own weapon key (PlayerEntity.HostPirateBlasterKey, T4 N22: D-pad / wheel cycling reaches it,
        // and a LAN/online guest's pick of it works on the host): null = on; replays recorded before it (format < 27) read
        // as off, so they play exactly
        public bool? PirateBlasterKey { get; set; }
        // the characters' abilities (the Trooper's Wall Cling + Scout Drone, Characters.AbilitiesFor) in a LAN/online match:
        // the host sets it (LAN protocol 9, online 8) and snapshots carry the state (Net/SeatAbilities); null = off there
        // (an older host, replays recorded before it: format < 22). Local matches have them whatever this says
        public bool? Abilities { get; set; }
        // the Pirate Blaster (MphRead Metadata/PirateBlaster.cs): the Trooper's affinity weapon (an AffinityWeapon pickup
        // gives a Trooper the blaster, PlayerEntity.HostAffinityBeam). null = on; replays recorded before it (format < 23)
        // read as off, so a Trooper there picks up Weavel's Battlehammer as it did. LAN protocol 10 / online 9.
        // This is also the ADVANCED box's EXTRA PICKUPS rule (owner 2026-10-10: one switch; off = no Pirate Blaster
        // anywhere, the weapon ring and touch select back to the DS six slots: PlayerEntity.HostSevenWeaponSlots)
        public bool? PirateBlaster { get; set; }
        // the ADVANCED box's EXTRA CHARACTERS rule (owner 2026-10-10): TROOPER and DARK SAMUS (Characters.IsExtra) may play;
        // off = a seat picked as one plays its base hunter in that hunter's own look. Characters.ApplyExtraRules strips them
        // before a match is sent or recorded, so the match itself never reads this. Needs EXTRA PICKUPS: the Trooper plays
        // with his own blaster or not at all. null = on (an older host). LAN protocol 12 / online 11
        public bool? ExtraCharacters { get; set; }
        // dev launcher "Solo arena" (App Campaign/SoloArena.cs): the local player alone in the arena, no bots or remote
        // seats (MphRead's Features.AllowInvalidTeams keeps a one-player match going). Never sent or recorded
        public bool Solo { get; set; }

        // First Hunt's three deathmatch arenas (docs/FIRST_HUNT_PORT.md P5, from the imported cart): the match runs First
        // Hunt's rules (Core FirstHunt/FirstHuntStart.cs StartFirstHuntMatch)
        public static bool IsFirstHuntArena(string arena) => arena is "Level MP1" or "Level MP2" or "Level MP3";

        public static bool IsTeamMode(GameMode mode) => mode is GameMode.BattleTeams or GameMode.SurvivalTeams
            or GameMode.Capture or GameMode.BountyTeams or GameMode.NodesTeams or GameMode.DefenderTeams;

        public static readonly IReadOnlyList<GameMode> Modes = new[]
        {
            GameMode.Battle, GameMode.BattleTeams, GameMode.Survival, GameMode.SurvivalTeams, GameMode.Capture,
            GameMode.Bounty, GameMode.BountyTeams, GameMode.Nodes, GameMode.NodesTeams, GameMode.Defender,
            GameMode.DefenderTeams, GameMode.PrimeHunter
        };

        // most seats in a match: the game's 4, or 8 (P8, docs/ONLINE_PLAY.md section 10; MphRead PlayerSlots.cs)
        public const int MaxSeats = MphRead.Entities.PlayerEntity.SlotCapacity;

        // A quick local match: the player plus bots of the given level on distinct hunters (past the seven hunters, 5-8
        // seats: the bot order again).
        public static MatchSettings Quick(GameMode mode, string arena, Hunter hunter, int bots, int botLevel = 1)
        {
            var settings = new MatchSettings { Mode = mode, Arena = arena };
            settings.Players.Add(new MatchPlayer { Hunter = hunter, Nickname = "Player1" });
            var used = new HashSet<Hunter> { hunter };
            int wanted = Math.Clamp(bots, 0, MaxSeats - 1);
            foreach (Hunter h in _botOrder)
            {
                if (settings.Players.Count > wanted)
                {
                    break;
                }
                if (used.Add(h))
                {
                    settings.Players.Add(new MatchPlayer { Hunter = h, Bot = true, BotLevel = botLevel });
                }
            }
            for (int i = 0; settings.Players.Count <= wanted; i++)
            {
                settings.Players.Add(new MatchPlayer { Hunter = _botOrder[i % _botOrder.Length], Bot = true, BotLevel = botLevel });
            }
            return settings;
        }

        private static readonly Hunter[] _botOrder = new[]
        {
            Hunter.Kanden, Hunter.Spire, Hunter.Trace, Hunter.Noxus, Hunter.Sylux, Hunter.Weavel, Hunter.Samus
        };

        // Throws when the match can't be played (the game itself ends a match at once with fewer than two players,
        // or with a team mode that doesn't have both teams).
        public void Validate()
        {
            if (Mode == GameMode.SinglePlayer || Mode == GameMode.None || Mode == GameMode.Unknown15)
            {
                throw new ArgumentException($"{Mode} is not a multiplayer mode");
            }
            // a solo run: one player, and only bots beside them (the dev launcher's practice targets)
            int people = 0;
            foreach (MatchPlayer p in Players) people += p.Bot ? 0 : 1;
            if (Solo ? people != 1 || Players.Count > MaxSeats : Players.Count < 2 || Players.Count > MaxSeats)
            {
                throw new ArgumentException(Solo ? $"a solo run is one player and its bots (has {people} players)"
                    : $"a match needs 2-{MaxSeats} players (has {Players.Count})");
            }
            if (LocalSeat < 0 || LocalSeat >= Players.Count || !ServedHost && (Players[LocalSeat].Bot || Players[LocalSeat].Remote))
            {
                throw new ArgumentException($"seat {LocalSeat} is the local player: it must exist and be neither a bot nor remote");
            }
            for (int i = 0; i < Players.Count; i++)
            {
                if (Players[i].Bot && Players[i].Remote)
                {
                    throw new ArgumentException($"seat {i} can't be both a bot and a remote player");
                }
            }
            if (!MatchArenas.IsArena(Arena) && !IsFirstHuntArena(Arena))
            {
                throw new ArgumentException($"{Arena} is not a multiplayer arena");
            }
            if (IsTeamMode(Mode))
            {
                int[] teams = TeamAssignment();
                if (Array.IndexOf(teams, 0) < 0 || Array.IndexOf(teams, 1) < 0)
                {
                    throw new ArgumentException($"{Mode} needs players on both teams");
                }
            }
        }

        // The same hunter on more than one seat (bots and seats 5-8 too; owner report N11, 2026-10-10): the first keeps
        // suit palette 0, the next ones 1, 2, 3 (then round again), so they can be told apart. Vanilla's suits are pal_01-04
        // per hunter plus pal_team01/02; in team modes the team's palette replaces this (MphRead GameState: 4 / 5), as the
        // ROM's body loader does. A seat's colour is the lobby's pick in vanilla (arm9 0x20E83FC); the order is ours.
        // "The same hunter" for the Trooper and Dark Samus is the same character look (LookAlikes.SameLookKey, 2026-10-11:
        // a Trooper doesn't push a Weavel onto palette 1; their own palettes/tints follow this number, MatchSkins.cs).
        // Since 2026-10-17 (C16, RecolorPerSkin) it's the same character in the same skin: a different skin is never a copy.
        public void AssignRecolors()
        {
            for (int i = 0; i < Players.Count; i++)
            {
                Players[i].Recolor = LookAlikes.CopyIndex(Players, i, RecolorPerSkin != false) % 4;
            }
        }

        // the palette slot a seat's body ends up in once the match starts: its team's (4 orange / 5 green, MphRead
        // GameState) in a team mode, else AssignRecolors' (worked out here without changing the seats)
        public int RecolorSlot(int seat)
        {
            if (seat < 0 || seat >= Players.Count) return 0;
            if (IsTeamMode(Mode)) return TeamAssignment()[seat] == 0 ? 4 : 5;
            return LookAlikes.CopyIndex(Players, seat, RecolorPerSkin != false) % 4;
        }

        // Each seat's team in a team mode: explicit teams are kept, the rest fill whichever team is smaller.
        public int[] TeamAssignment()
        {
            int[] teams = new int[Players.Count];
            int[] counts = new int[2];
            for (int i = 0; i < Players.Count; i++)
            {
                teams[i] = Players[i].Team is 0 or 1 ? Players[i].Team : -1;
                if (teams[i] >= 0)
                {
                    counts[teams[i]]++;
                }
            }
            for (int i = 0; i < Players.Count; i++)
            {
                if (teams[i] < 0)
                {
                    teams[i] = counts[0] <= counts[1] ? 0 : 1;
                    counts[teams[i]]++;
                }
            }
            return teams;
        }
    }
}
