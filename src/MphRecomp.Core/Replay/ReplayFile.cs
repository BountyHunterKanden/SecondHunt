using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
using MphRead;
using MphRead.Entities;
using MphRecomp.Campaign;
using MphRecomp.Multiplayer;

namespace MphRecomp.Replay
{
    // One step of a recorded match: the input that step applied, and the state checksum after it (ReplayState).
    public struct ReplayStep
    {
        public CampaignInput Input;
        public uint Checksum;
    }

    // One seat's result when the match ended (MatchResults: Place 0 = first, by team in team modes)
    public readonly record struct ReplayPlace(int Seat, int Place, int Points);

    // a kill (Death false) or death of one seat at a step: the playback bar's marks (format 18)
    public readonly record struct ReplayMark(int Step, byte Seat, bool Death);

    // One seat's online identity as the match had it (docs/ONLINE_PLAY.md O10: tap a name in REPLAYS -> its license as it
    // was, add friend / rival). FriendCode = the player id (null: no online identity, e.g. a LAN player who never went
    // online); Name the license name, Tag the server's 4 digits (NAME#tag; 0 = not known); LicenseJson that player's own
    // HunterLicense at the match (JSON, HunterLicense's property names). The recording device's seat comes from its own
    // files; in a LAN / online match each other human's device sends its own (ReplayLink) and the host's file carries them all.
    public readonly record struct ReplaySeatIdentity(int Seat, string? FriendCode, string Name, string? LicenseJson, int Tag = 0);

    // What the match host did to a remote seat before a step (format 19; docs/REPLAY_MODE.md R6 / finding 8): applied that
    // seat's NetInput as it came in over the network (Input = NetInput.WriteRecord's bytes: every field, incl. ViewTick for
    // lag compensation), took the seat out (it left or went silent) or let it back in (a rejoin). Playback does the same
    // before the same step, so a LAN / online host's recording re-runs exactly.
    // Format 50 adds Hit: a hit claim the host checked and took (Input = CampaignHost.ClaimRecord's bytes; Seat = the shooter),
    // applied at the start of that step (MphRecomp Net/HitClaims.cs).
    // Format 54 adds Position: a guest's own position the host checked and took (Input = CampaignHost.PositionRecord's bytes),
    // (format 55: and whether its device runs its own knockback, and the speed the host took with it),
    // applied at the start of that step (MphRecomp Net/ClientPosition.cs).
    public enum ReplayNetKind : byte { Input = 0, Drop = 1, Rejoin = 2, Hit = 3, Position = 4 }

    public readonly record struct ReplayNetEvent(int Step, byte Seat, ReplayNetKind Kind, byte[]? Input);

    // The host's shot check of one shot or claimed hit (format 47; MphRecomp Net/ShotCheck.cs, 2026-10-19): what the shooter's
    // device reported against what the host did, at the host step it was settled. Metadata for finding missed-hit bugs: it
    // never changes how a replay plays. Kind: a shot that also went out here (OriginOff / AimOff = how far the two gun
    // positions / directions were apart, in units / degrees), a shot the device fired that never went out here, a hit its
    // screen showed whose victim the host's history had within reach then (VictimOff = how far), one it had elsewhere, or one
    // the history no longer held.
    public enum ReplayShotKind : byte { ShotMatched = 0, ShotLost = 1, ClaimAgrees = 2, ClaimDisagrees = 3, ClaimUnknown = 4 }

    public readonly record struct ReplayShotCheck(int Step, byte Seat, ReplayShotKind Kind, float OriginOff, float AimOff,
        sbyte Victim, float VictimOff);

    // A recorded match (docs/REPLAY_MODE.md). A replay re-runs the match from its inputs, so the file holds what the
    // match started from (settings, both RNG streams, every setting that changes play) and each step's input +
    // checksum. Playback refuses a file it can't play exactly (ReplayPlayer) and stops at the first failed check.
    //   file: "MPHRPLY\0", u16 format, u32 CRC-32 + u32 length of the payload, then the payload deflated.
    //   Steps are stored as given (floats by their exact bits); R2 may pack them tighter.
    //   Format 2 adds Data after AiAspect; format 1 files still load (no Data, so nothing to compare).
    //   Format 3 adds Places after Complete (each seat's result, for the REPLAYS list's win / loss); older files have none.
    //   Format 4 adds Settings.Crates after Places (Echoes arenas' weapon crates); older files read as crates off.
    //   Format 5 adds Settings.Breakables after Crates (Echoes arenas' breakables + Dark Water); older files read as off.
    //   Format 6 adds each seat's Character after Breakables (the Trooper's Shield Wall); older files have none (no Trooper).
    //   Format 7: the same layout; the Trooper wall-clings instead (D17), so a format 6 file with a Trooper is refused.
    //   Format 8: the same layout; the Trooper's alt form is the Scout Drone (D10), so an older file with a Trooper is refused.
    //   Format 9: the same layout; the Trooper splits and merges at once (no Weavel flip), so an older Trooper file is refused.
    //   Format 10: the same layout; the Trooper's drone fires in hold-to-fire bursts, so an older Trooper file is refused.
    //   Format 11: the same layout; the Trooper's drone has its cutting laser (L2), so an older Trooper file is refused.
    //   Format 12: the same layout; the laser steers with his aim, hits 10 a tick and has its own 10 s wait (the burst its
    //   2 s), so an older Trooper file is refused.
    //   Format 13 adds Settings.EchoesMoves after the Characters (Echoes arenas' Echoes movement); older files read as off.
    //   Format 14: the same layout; the Trooper's standing body (drone out) is hit as a whole biped, not Weavel's turret
    //   sphere, so an older Trooper file is refused.
    //   Format 15: the same layout; Pipeline's water moves as Echoes shipped it (no floaty water), so a format 13-14 Pipeline
    //   file with Echoes movement on is refused.
    //   Format 16: the same layout; Pipeline's Gravity Boost starts only on a second press (and stops a fall), so an older
    //   Pipeline file with Echoes movement on is refused.
    //   Format 17 adds Identities after EchoesMoves (O10: each seat's friend code / name / license snapshot, local seat
    //   only so far); metadata only, so it never changes how a match plays -- older files just have none.
    //   Format 18 adds Marks after Identities (each seat's kills and deaths by step, for the playback bar's scrubber);
    //   metadata only too -- an older file's marks are found as it plays.
    //   Format 19 (O10, network matches): each identity carries its Tag; after Marks come Net ("" local, "LAN", "online"),
    //   OwnSeat (the saving device's seat when it isn't the recording's LocalSeat: a guest's copy of the host's file) and
    //   NetEvents (each remote seat's applied input and drop, by step). Older files have none of it (local matches).
    //   Format 20: the same layout; in Echoes arenas a jump just after leaving the floor keeps the double jump
    //   (Settings.EchoesLedgeJump); older files read it as off, so they play exactly.
    //   Format 21 adds each seat's Skin after NetEvents (draw only: a Trooper's skin; older files have none, so their
    //   Troopers show MphRead's Weavel).
    //   Format 22 adds Settings.Abilities after the Skins (a LAN/online match with the Trooper's Wall Cling + Scout Drone
    //   on); older files read it as none, so a network match plays with them off as it did.
    //   Format 23 adds Settings.PirateBlaster after Abilities (the Trooper's affinity weapon, the Pirate Blaster); older
    //   files read it as off, so a Trooper's affinity pickup gives Weavel's Battlehammer as it did.
    //   Format 24: the same layout; Echoes arenas have UA pickups (Settings.EchoesAmmo) and no fall damage on landing from
    //   a jump pad (Settings.EchoesPadNoFall); older files read both as off, so they play exactly.
    //   Format 25: the same layout; the Trooper's Wall Cling needs a jump or a 2-unit fall (Settings.WallClingJumpRule, T5
    //   N10), his Scout Drone flies a jump pad's normal arc (Settings.DronePadArc, T5 N9) and Dark Samus has no walk bob
    //   (Settings.HoverWalk, T5 N13: it moves her muzzle); older files read all three as off, so they play exactly.
    //   Format 26 (T11): each step's input carries the touch controls' stylus (TouchDown + TouchDx/Dy, PlayerTouch.cs), and
    //   a network input record is NetInput's 18 bytes (its touch bytes); older files read the stylus as up (old 15-byte
    //   records are widened), so they play exactly.
    //   Format 27 (T4 N22): the same layout; the Pirate Blaster has its own weapon key (Settings.PirateBlasterKey: D-pad
    //   cycling, a guest's pick on the host); older files read it as off, so they play exactly.
    //   Format 28 (T7 N24): the same layout; bots in Echoes arenas take the graph's double-jump, gap and boost links and
    //   skip items they can't get (Settings.EchoesBotMoves); older files read it as off, so they play exactly.
    //   Format 29 (0.2.0): the same layout; with EXTRA PICKUPS on, arenas carry the owner's baked pickup placements
    //   (Settings.PlacedPickups, Arenas/PlacedPickups.cs); older files read it as off, so they play exactly.
    //   Format 30: the same layout; bots think at vanilla's 30 Hz pace (aim turn step halved per frame, per-tick random
    //   rolls on tick frames, shot leading by the beam's per-tick speed: Settings.BotTickPace); older files read it as off.
    //   Format 31: the same layout; with friendly fire off a teammate's hit is no hit at all (no knockback, invulnerability,
    //   hurt sound or bot reaction: Settings.TeamHitsIgnored); older files read it as off, so they play exactly.
    //   Format 32: the same layout; Dark Samus's morph ball hops on the jump button and holds the bomb button for the Hyper
    //   Ball instead of bombs (Settings.HyperBall, docs/DARK_SAMUS_HYPER.md); older files read it as off, so they play exactly.
    //   Format 33: the same layout; Zero Suit Samus's affinity weapon is the Paralyzer (its charged shot stuns) and she has
    //   90 health (Settings.Paralyzer, docs/ZERO_SUIT_SAMUS.md section 7); older files read it as off, so they play exactly.
    //   Format 34: the same layout; Dark Samus's Hyper Ball plays by Corruption's rules (every enemy in reach zapped in turn,
    //   the ball kicked by its arcs: Settings.HyperBallCorruption); older files read it as off, so they play exactly.
    //   Format 35: the same layout; the Trooper's standing body (drone out) takes headshots in the same zone as any hunter
    //   (Settings.DroneBodyHeadshots); older files read it as off, so they play exactly.
    //   Format 36: the same layout; with EXTRA PICKUPS on, Echoes arenas' weapon crates can also drop the Pirate Blaster
    //   (Settings.CrateBlaster, Arenas/EchoesCrates.cs); older files read it as off, so they play exactly.
    //   Format 37: the same layout; Shooting Gallery's manned turret and bomb slot (Settings.EchoesTurret,
    //   Arenas/EchoesTurret.cs); older files read it as off, so they play exactly.
    //   Format 38: the same layout; continuous shots (Shock Coil) scale their headshot damage like their damage, as the game
    //   does (Settings.ContinuousHeadshots, PlayerEntity.HostContinuousHeadshots); older files read it as off, so they play exactly.
    //   Format 39: the same layout; the zoom as the game does it: zoomed aim speed, zoom step and ease (Settings.VanillaZoom,
    //   PlayerEntity.HostVanillaZoom); older files read it as off, so they play exactly.
    //   Format 40: the same layout; the Shock Coil's ramp kept through one missed tick as the game does (Settings.ShockCoilGrace,
    //   PlayerEntity.HostShockCoilGrace); older files read it as off, so they play exactly.
    //   Format 41: the same layout; bots use Shooting Gallery's turret: they sometimes get in and shoot from it, and roll into
    //   its bomb slot against an enemy in it (Settings.EchoesTurretBots, Arenas/EchoesTurret.cs EchoesTurretBots); older
    //   files read it as off, so they play exactly.
    //   Format 42: the same layout; each step's checksum also covers Shooting Gallery's turret (state, occupant, health,
    //   aim, shots: ReplayState.AddTurret); older files' checksums were taken without it (OldTurretHash), so they verify.
    //   Format 43: the same layout; same-hunter recolours count per character + skin (Settings.RecolorPerSkin,
    //   LookAlikes.SameLookKey); older files read it as off (per look / hunter), so they play exactly.
    //   Format 44: the same layout; Dark Samus's morph ball no longer hops on the jump button (Settings.NoBallJump); older
    //   files read it as off, so their hops play exactly.
    //   Format 45 adds Settings.HyperBall after PirateBlaster, as the match played it: a public build has no Hyper Ball
    //   (owner 2026-10-18), so null no longer says on; formats 32-44 read it as on (every build had it), so they play exactly.
    //   Format 47 also: the Trooper's drone laser pushes nothing (Settings.DroneLaserNoPush); older files read it as off, so
    //   their laser hits play exactly.
    //   Format 47 adds ShotChecks after the Steps (a host's shot check of each shot and claimed hit its guests reported,
    //   ReplayShotCheck); metadata only, and after the steps so the REPLAYS list's header reads never touch it. Older files
    //   have none. (Format 46 was never released: 46 and 47 are both this branch's.)
    //   Format 48: the Trooper's drone ignores his own standing body, with a 20-frame wait between splits and merges
    //   (Settings.DroneSwitchRule); older files (47 too: test builds recorded some) read it as off, so they play exactly.
    //   Format 49: the drone's burst and laser waits count down while the Trooper is on foot too (Settings.DroneWaitsOnFoot);
    //   older files read it as off, so they play exactly.
    //   Format 50: an online host's hit claims (Settings.HitClaims; NetEvents of kind Hit: each claim it took, put back at its
    //   step); older files read the rule as off (no shot is kept for claims), so they play exactly.
    //   Format 51: the Trooper clings once per airtime (Settings.WallClingOnce) and the Pirate Blaster's bolts deal 10 / 14
    //   (Settings.LighterBlaster); older files read both as off, so they play exactly.
    //   Format 52: splash reaches the Trooper's standing body (Settings.DroneBodySplash); older files read it as off, so they
    //   play exactly.
    //   Format 53: one hit per blast on the Trooper (Settings.BodySplashOnce) and one player per claimed shot
    //   (Settings.ClaimOneHit); older files read both as off, so they play exactly.
    //   Format 54: an online host takes its guests' own positions (Settings.ClientPosition; NetEvents of kind Position: each
    //   one it took, put back at its step); older files read the rule as off and hold none, so they play exactly.
    //   Format 55: a Position event may carry a flags byte (13 bytes: the guest runs its own knockback, so the host hands it
    //   the push instead of pushing its copy, PlayerEntity.NetHandPush) and the speed the host took with it (25 bytes); a
    //   12-byte event is as before (the host pushes). Older files hold only those, so they play exactly; a format 54 app
    //   can't open a 55 file.
    //   Format 56: the Trooper clings only with Jump held after 0.3 s in the air (Settings.WallClingHold) and each Pirate
    //   Blaster bolt costs a shot of UA (Settings.BlasterBoltCost), its bolts dealing 9 / 13 (Settings.LighterBlaster2);
    //   older files read all three as off, so they play exactly.
    //   Format 57: bots walk off a ledge toward a drop-down node under it (Settings.BotDropOff, PlayerAiDropOff.cs) and a
    //   bot's camera shake moves only its view, not its aim (Settings.BotSteadyAim) and a bot switches to its preferred
    //   weapon before checking that the one it holds can charge (Settings.BotSwitchFirst) and a bot holds its shot while
    //   its target is behind a wall (Settings.BotFireSight, PlayerAiSight.cs) and a roaming bot's view eases toward its
    //   path's slope (Settings.BotRoamLook, PlayerAiRoamLook.cs); older files read all five as off, so they play exactly.
    //   Format 58: a Trooper bot wall-clings (Settings.BotWallCling, PlayerAiWallCling.cs) and bots break the Echoes
    //   arenas' crates (Settings.BotCrates, PlayerAiCrates.cs); older files read both as off, so they play exactly.
    //   Format 59: a bot's camera shake knocks its aim again, as a player's does (Settings.BotSteadyAim back off, owner
    //   2026-10-23: bots do what players do); format 57-58 files read it as on, so they play exactly.
    //   Format 60 adds Settings.HyperBeam after HyperBall, as the match played it (Characters.HyperBeamOn: Dark Samus's
    //   affinity weapon is the Hyper Beam, dev only, docs/DARK_SAMUS_HYPER.md H2); older files read it as off, so they play
    //   exactly.
    //   Format 61: the same layout; bots run MphRead's bot code set back to the ROM's where it differed, and FireSight holds
    //   a charge 4 ticks, not 2 s (Settings.BotVanillaParity, bots vanilla-parity review); older files read it as off, so
    //   they play exactly.
    //   Format 62 adds Settings.BotStuckFix after HyperBeam, as the match started (bots in MPH's arenas get unstuck,
    //   PlayerAiUnstick.cs); older files read it as off, so they play exactly.
    //   Format 63 adds Settings.BotSwitchOrder after BotStuckFix, as the match started (bots check their AI exits in
    //   vanilla's order on odd frames too, PlayerAi.UpdatePathWeights); older files read it as off, so they play exactly.
    //   Format 64 adds Settings.BotNodeFlat after BotSwitchOrder, as the match started (bots drop a node they stand over by
    //   horizontal distance, PlayerAi.Func2140584); older files read it as off, so they play exactly.
    //   Format 65 adds Settings.BotStuckSight after BotNodeFlat, as the match started (the stuck fix runs on a bot pinned 3 s
    //   in sight of a foe, PlayerAiUnstick.SightPinned); older files read it as off, so they play exactly.
    //   Format 66 adds Settings.BotBreakWalls after BotStuckSight, as the match started (bots shoot down a breakable wall
    //   across their way, PlayerAiWalls.cs); older files read it as off, so they play exactly.
    //   Format 67 adds Settings.BotStuckVertical after BotBreakWalls, as the match started (the stuck fix's clock waits while
    //   a bot falls or rides a lift, PlayerAiUnstick.BotUnstickTick); older files read it as off, so they play exactly.
    //   Format 68 adds Settings.BotEdgeJump after BotStuckVertical, as the match started (bots jump at a ledge or a wall short
    //   of their next node, PlayerAiEdgeJump.cs); older files read it as off, so they play exactly.
    //   Format 69 adds Settings.BotEdgePadJump after BotEdgeJump, as the match started (a bot thrown by a jump pad jumps when
    //   it comes down short of its node, PlayerAiEdgeJump.BotEdgePadJump); older files read it as off, so they play exactly.
    //   Format 70 adds Settings.BotReleaseControls after BotEdgePadJump, as the match started (a bot lets go of the controls
    //   its buttons no longer drive, PlayerAi.ProcessInput); older files read it as off, so they play exactly.
    //   Format 71 adds Settings.BotLiftGuard after BotReleaseControls, as the match started (no stuck-fix climb into a lift
    //   a bot's route doesn't go up, PlayerAiEdgeJump.LiftGuardBlocks); older files read it as off, so they play exactly.
    //   Format 72 adds Settings.BotUpLifts after BotLiftGuard, then Settings.BotArriveTick, as the match started (only a
    //   gravity volume that pushes up is a lift, PlayerAiEdgeJump.IsLift; a bot's node arrival is checked per 30 Hz tick,
    //   PlayerAi.Func2140584); older files read them as off, so they play exactly.
    //   Format 73 adds Settings.BotEdgeHold after BotArriveTick, as the match started (an edge jump's flight keeps its node
    //   until she lands, PlayerAiEdgeJump.EdgeHold); older files read it as off, so they play exactly.
    //   Format 74 adds each seat's MatchPlayer.ColourPick after BotEdgeHold (an sbyte per seat, -1 = auto: the lobby's colour
    //   pick, LookAlikes.CopyIndices); drawn only, so older files read them as auto and look as they did.
    //   Format 46: the same layout; an online host's lag compensation no longer shifts a homing beam (the Shock Coil, homing
    //   missiles) off its own target or Weavel's turret's shots, and shifts bombs, Lockjaw's lines + snare and the drone's laser
    //   (Settings.LagCompFix, PlayerEntity.HostLagFix);
    //   older files read it as off, so their remote seats' hits play exactly. (A 0.3.5-beta app reads up to 45: it can't open
    //   a format 46 file at all.)
    //   Format 45 also holds a served match's recording (docs/DEDICATED_SERVERS.md section 4: the match host records and sends
    //   each player a copy) with no new field: Settings.ServedHost isn't written but read back from the seats (ReadSettings:
    //   a host's recording whose LocalSeat is a remote player). An app from before that reads it as a host's own seat, which
    //   MatchSettings.Validate refuses at the start, so it saves the file and refuses to play it, never plays it wrong.
    public sealed class ReplayFile
    {
        public const int FormatVersion = 74;
        // Bump when a code change alters how a match plays: an old replay then is refused instead of drifting.
        // (R2c: golden replays checked on every build say when.)
        // 2 (R1): matches play with Scene.HostDrawFreeSim (Spire's rocks placed whether or not a frame draws).
        // 3 (R2b): matches play with DetMathF.HostDeterministic (the same trig on every device, MphRead/Utility/DetMath.cs).
        // 4 (2026-10-17): entity collision inverts its transform the same way on every device (DetMatrix.Inverted: OpenTK
        //    used SSE3 on PCs, plain arithmetic on ARM), and Shooting Gallery's turret is in each step's checksum (format 42).
        // 5 (2026-10-26): a beam picked up in alt form is equipped as on foot (Shooting latched by firing while morphing,
        //    and the morph's gun UpDown, no longer block the pickup's equip; owner QoL, PlayerProcess.cs).
        // 6 (2026-10-27): a morph ball under a ceiling keeps vanilla's 1x push-out (the 4x hack pushed Trace/Sylux through
        //    Data Shrine's ramp tunnels, PlayerCollision.cs); docs/ACCURACY.md A10.
        public const int CurrentSimVersion = 6;
        private static ReadOnlySpan<byte> Magic => "MPHRPLY\0"u8;

        public int SimVersion { get; set; } = CurrentSimVersion;
        // read from a file older than format 9 with a Trooper seat (an older Trooper: Shield Wall, or Wall Cling with
        // Weavel's alt form): refused by ReplayPlayer.CheckBeforeStart
        public bool OldTrooper { get; private set; }
        // read from a format 13-15 file of Pipeline with Echoes movement on (older water / boost): refused like OldTrooper
        public bool OldEchoesWater { get; private set; }
        // read from a file older than format 42: its checksums leave out the turret (ReplayState.Capture turretState)
        public bool OldTurretHash { get; private set; }
        public string Build { get; set; } = "";
        public string Platform { get; set; } = DescribePlatform();
        public MatchSettings Settings { get; set; } = new();
        // the RNG streams when the match's setup began (ReplayRecorder.RngAtStart)
        public uint Rng1 { get; set; }
        public uint Rng2 { get; set; }
        public float AiAspect { get; set; }
        // the arena's files that shape play, as the recording device had them (ReplayData; "" = not recorded)
        public string Data { get; set; } = "";
        // MphRead's Bugfixes / Features / Cheats values as the match ran (their Commit() strings)
        public SortedDictionary<string, string> Options { get; } = new(StringComparer.Ordinal);
        public List<ReplayStep> Steps { get; } = new();
        // the match ran to its end; a cut file (crash, battery) is still exact up to where it stops
        public bool Complete { get; set; }
        // how each seat placed (a complete match; empty = not known: an incomplete match, or a file older than format 3)
        public List<ReplayPlace> Places { get; } = new();
        // each known seat's online identity (format 17; O10). Empty on an older file, or when none is known yet.
        public List<ReplaySeatIdentity> Identities { get; } = new();
        public List<ReplayMark> Marks { get; } = new();
        // format 18 or newer: Marks is the whole match's (older: none, found as it plays)
        public bool HasMarks { get; set; } = true;
        // format 19: the match's network ("" = a local match, "LAN", "online"); the saving device's own seat (-1 = the
        // recording's LocalSeat: it recorded the match itself); what the host applied to remote seats, in step order
        public string Net { get; set; } = "";
        public int OwnSeat { get; set; } = -1;
        public List<ReplayNetEvent> NetEvents { get; } = new();
        public List<ReplayShotCheck> ShotChecks { get; } = new();
        // a network input of another size than this build's (NetInput.RecordSize), or an event it doesn't know: refused by ReplayPlayer.CheckBeforeStart
        public bool OldNetInput { get; private set; }

        // the seat this device played (a guest's copy of the host's file: its own, else the recording's)
        public int ViewerSeat => OwnSeat >= 0 ? OwnSeat : Settings.LocalSeat;

        // STREAMING (SHOW NAME off): whether a seat is this device's player: the seat with this device's friend code, else
        // the seat it played. By seat, never by name, so a stranger with the same nickname doesn't show as HUNTER too
        // (which would give the streamer's name away).
        public bool IsOwnSeat(int seat)
        {
            foreach (ReplaySeatIdentity id in Identities)
            {
                if (id.FriendCode != null && MphRecomp.Config.StreamerPrivacy.IsOwnCode(id.FriendCode)) return id.Seat == seat;
            }
            return seat == ViewerSeat && !(Settings.ServedHost && OwnSeat < 0);
        }

        public static string DescribePlatform() =>
            $"{RuntimeInformation.OSDescription.Trim()} {RuntimeInformation.ProcessArchitecture} {RuntimeInformation.FrameworkDescription}";

        public void Write(Stream stream)
        {
            var payload = new MemoryStream();
            using (var w = new BinaryWriter(payload, System.Text.Encoding.UTF8, leaveOpen: true))
            {
                w.Write(SimVersion);
                w.Write(Build);
                w.Write(Platform);
                WriteSettings(w, Settings);
                w.Write(Rng1);
                w.Write(Rng2);
                w.Write(AiAspect);
                w.Write(Data);
                w.Write(Options.Count);
                foreach ((string key, string value) in Options)
                {
                    w.Write(key);
                    w.Write(value);
                }
                w.Write(Complete);
                w.Write(Places.Count);
                foreach (ReplayPlace place in Places)
                {
                    w.Write(place.Seat);
                    w.Write(place.Place);
                    w.Write(place.Points);
                }
                WriteOpt(w, Settings.Crates);
                WriteOpt(w, Settings.Breakables);
                WriteCharacters(w, Settings);
                WriteOpt(w, Settings.EchoesMoves);
                w.Write(Identities.Count);
                foreach (ReplaySeatIdentity id in Identities)
                {
                    w.Write(id.Seat);
                    w.Write(id.FriendCode != null);
                    if (id.FriendCode != null) w.Write(id.FriendCode);
                    w.Write(id.Name);
                    w.Write(id.LicenseJson != null);
                    if (id.LicenseJson != null) w.Write(id.LicenseJson);
                    w.Write(id.Tag);
                }
                w.Write(Marks.Count);
                foreach (ReplayMark mark in Marks)
                {
                    w.Write(mark.Step);
                    w.Write(mark.Seat);
                    w.Write(mark.Death);
                }
                w.Write(Net);
                w.Write(OwnSeat);
                w.Write(NetEvents.Count);
                int lastStep = 0;
                foreach (ReplayNetEvent e in NetEvents)
                {
                    w.Write7BitEncodedInt(e.Step - lastStep); // (in step order: the gap since the last one)
                    lastStep = e.Step;
                    w.Write(e.Seat);
                    w.Write((byte)e.Kind);
                    if (e.Kind is ReplayNetKind.Input or ReplayNetKind.Hit or ReplayNetKind.Position)
                    {
                        byte[] input = e.Input ?? Array.Empty<byte>();
                        w.Write((byte)input.Length);
                        w.Write(input);
                    }
                }
                w.Write(Settings.Players.Count);
                foreach (MatchPlayer p in Settings.Players)
                {
                    w.Write(p.Skin ?? "");
                }
                WriteOpt(w, Settings.Abilities);
                WriteOpt(w, Settings.PirateBlaster); // format 23
                w.Write(Settings.HyperBall ?? !MphRecomp.Config.RecompSettings.PublicBuild); // format 45: as MatchStart played it
                w.Write(Characters.HyperBeamOn(Settings)); // format 60: as MatchStart played it
                w.Write(Settings.BotStuckFix ?? true); // format 62: as MatchStart started it
                w.Write(Settings.BotSwitchOrder ?? true); // format 63: as MatchStart started it
                w.Write(Settings.BotNodeFlat ?? true); // format 64: as MatchStart started it
                w.Write(Settings.BotStuckSight ?? true); // format 65: as MatchStart started it
                w.Write(Settings.BotBreakWalls ?? true); // format 66: as MatchStart started it
                w.Write(Settings.BotStuckVertical ?? true); // format 67: as MatchStart started it
                w.Write(Settings.BotEdgeJump ?? true); // format 68: as MatchStart started it
                w.Write(Settings.BotEdgePadJump ?? true); // format 69: as MatchStart started it
                w.Write(Settings.BotReleaseControls ?? true); // format 70: as MatchStart started it
                w.Write(Settings.BotLiftGuard ?? true); // format 71: as MatchStart started it
                w.Write(Settings.BotUpLifts ?? true); // format 72: as MatchStart started it
                w.Write(Settings.BotArriveTick ?? true); // format 72
                w.Write(Settings.BotEdgeHold ?? true); // format 73: as MatchStart started it
                foreach (MatchPlayer p in Settings.Players) w.Write((sbyte)Math.Clamp(p.ColourPick, -1, LookAlikes.Colours - 1)); // format 74
                w.Write(Steps.Count);
                foreach (ReplayStep step in Steps)
                {
                    WriteInput(w, step.Input);
                    w.Write(step.Checksum);
                }
                w.Write(ShotChecks.Count); // format 47
                foreach (ReplayShotCheck c in ShotChecks)
                {
                    w.Write(c.Step);
                    w.Write(c.Seat);
                    w.Write((byte)c.Kind);
                    w.Write(c.OriginOff);
                    w.Write(c.AimOff);
                    w.Write(c.Victim);
                    w.Write(c.VictimOff);
                }
            }
            byte[] data = payload.ToArray();
            using var head = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true);
            head.Write(Magic);
            head.Write((ushort)FormatVersion);
            head.Write(Crc32(data));
            head.Write(data.Length);
            head.Flush();
            using (var deflate = new DeflateStream(stream, CompressionLevel.Optimal, leaveOpen: true))
            {
                deflate.Write(data);
            }
        }

        // throws InvalidDataException for anything that isn't an intact replay file of this format
        public static ReplayFile Read(Stream stream) => Read(stream, headerOnly: false, out _);

        // everything but the steps (Steps stays empty; `steps` = how many the file holds), for the REPLAYS list (R3):
        // only the start of the payload is inflated and the checksum isn't checked (Read checks it before playing)
        public static ReplayFile ReadHeader(Stream stream, out int steps) => Read(stream, headerOnly: true, out steps);

        public static ReplayFile LoadHeader(string path, out int steps)
        {
            using FileStream fs = File.OpenRead(path);
            return ReadHeader(fs, out steps);
        }

        private static ReplayFile Read(Stream stream, bool headerOnly, out int stepCount)
        {
            using var head = new BinaryReader(stream, System.Text.Encoding.UTF8, leaveOpen: true);
            byte[] magic = head.ReadBytes(Magic.Length);
            if (!magic.AsSpan().SequenceEqual(Magic))
            {
                throw new InvalidDataException("not a replay file");
            }
            int format = head.ReadUInt16();
            if (format < 1 || format > FormatVersion)
            {
                throw new InvalidDataException($"replay format {format} (this build reads up to {FormatVersion})");
            }
            uint crc = head.ReadUInt32();
            int length = head.ReadInt32();
            if (length < 0 || length > 256 << 20)
            {
                throw new InvalidDataException("damaged replay file (length)");
            }
            if (headerOnly)
            {
                using var deflate = new DeflateStream(stream, CompressionMode.Decompress, leaveOpen: true);
                using var hr = new BinaryReader(deflate, System.Text.Encoding.UTF8);
                try
                {
                    return ReadBody(hr, format, headerOnly: true, out stepCount);
                }
                catch (Exception ex) when (ex is EndOfStreamException or ArgumentException or OverflowException or FormatException
                    or IndexOutOfRangeException)
                {
                    throw new InvalidDataException("damaged replay file (cut short)");
                }
            }
            // (grown as it unpacks, not the header's length up front: a shared file's header is the sender's word)
            byte[] data;
            using (var deflate = new DeflateStream(stream, CompressionMode.Decompress, leaveOpen: true))
            using (var unpacked = new MemoryStream())
            {
                byte[] chunk = new byte[64 << 10];
                while (unpacked.Length < length)
                {
                    int n = deflate.Read(chunk, 0, (int)Math.Min(chunk.Length, length - unpacked.Length));
                    if (n <= 0)
                    {
                        throw new InvalidDataException("damaged replay file (cut short)");
                    }
                    unpacked.Write(chunk, 0, n);
                }
                data = unpacked.ToArray();
            }
            if (Crc32(data) != crc)
            {
                throw new InvalidDataException("damaged replay file (checksum)");
            }
            using var r = new BinaryReader(new MemoryStream(data), System.Text.Encoding.UTF8);
            try
            {
                return ReadBody(r, format, headerOnly: false, out stepCount);
            }
            catch (Exception ex) when (ex is EndOfStreamException or ArgumentException or OverflowException or FormatException
                or IndexOutOfRangeException)
            {
                // (a file put together by hand, its checksum made to match)
                throw new InvalidDataException("damaged replay file (contents)");
            }
        }

        private static ReplayFile ReadBody(BinaryReader r, int format, bool headerOnly, out int steps)
        {
            var file = new ReplayFile
            {
                SimVersion = r.ReadInt32(),
                Build = r.ReadString(),
                Platform = r.ReadString(),
                Settings = ReadSettings(r),
                Rng1 = r.ReadUInt32(),
                Rng2 = r.ReadUInt32(),
                AiAspect = r.ReadSingle()
            };
            if (format >= 2)
            {
                file.Data = r.ReadString();
            }
            int options = r.ReadInt32();
            for (int i = 0; i < options; i++)
            {
                string key = r.ReadString();
                file.Options[key] = r.ReadString();
            }
            file.Complete = r.ReadBoolean();
            if (format >= 3)
            {
                int places = r.ReadInt32();
                for (int i = 0; i < places; i++)
                {
                    file.Places.Add(new ReplayPlace(r.ReadInt32(), r.ReadInt32(), r.ReadInt32()));
                }
            }
            file.Settings.Crates = format >= 4 ? (r.ReadBoolean() ? r.ReadBoolean() : null) : false;
            file.Settings.Breakables = format >= 5 ? (r.ReadBoolean() ? r.ReadBoolean() : null) : false;
            if (format >= 6)
            {
                ReadCharacters(r, file.Settings);
                // older Troopers (format 6: Shield Wall; 7: Weavel's alt form; 8: Weavel's flip; 13: his standing body's
                // turret sphere): their matches can't play the same any more
                file.OldTrooper = format < 14 && file.Settings.Players.Any(p => p.Character != null);
            }
            file.Settings.EchoesMoves = format >= 13 ? (r.ReadBoolean() ? r.ReadBoolean() : null) : false;
            file.Settings.EchoesLedgeJump = format >= 20 ? null : false; // not written: on from format 20
            file.Settings.EchoesAmmo = format >= 24 ? null : false; // not written: on from format 24
            file.Settings.EchoesPadNoFall = format >= 24 ? null : false; // not written: on from format 24
            file.Settings.WallClingJumpRule = format >= 25 ? null : false; // not written: on from format 25
            file.Settings.DronePadArc = format >= 25 ? null : false; // not written: on from format 25
            file.Settings.HoverWalk = format >= 25 ? null : false; // not written: on from format 25
            file.Settings.PirateBlasterKey = format >= 27 ? null : false; // not written: on from format 27
            file.Settings.EchoesBotMoves = format >= 28 ? null : false; // not written: on from format 28
            file.Settings.PlacedPickups = format >= 29 ? null : false; // not written: on from format 29
            file.Settings.BotTickPace = format >= 30 ? null : false; // not written: on from format 30
            file.Settings.TeamHitsIgnored = format >= 31 ? null : false; // not written: on from format 31
            file.Settings.HyperBall = format >= 32; // on from format 32 (from format 45 the file says, after PirateBlaster)
            file.Settings.Paralyzer = format >= 33 ? null : false; // not written: on from format 33
            file.Settings.HyperBallCorruption = format >= 34 ? null : false; // not written: on from format 34
            file.Settings.DroneBodyHeadshots = format >= 35 ? null : false; // not written: on from format 35
            file.Settings.CrateBlaster = format >= 36 ? null : false; // not written: on from format 36
            file.Settings.EchoesTurret = format >= 37 ? null : false; // not written: on from format 37
            file.Settings.ContinuousHeadshots = format >= 38 ? null : false; // not written: on from format 38
            file.Settings.VanillaZoom = format >= 39 ? null : false; // not written: on from format 39
            file.Settings.ShockCoilGrace = format >= 40 ? null : false; // not written: on from format 40
            file.Settings.EchoesTurretBots = format >= 41 ? null : false; // not written: on from format 41
            file.Settings.RecolorPerSkin = format >= 43 ? null : false; // not written: on from format 43
            file.Settings.NoBallJump = format >= 44 ? null : false; // not written: on from format 44
            file.Settings.DroneLaserNoPush = format >= 47 ? null : false; // not written: on from format 47
            file.Settings.DroneSwitchRule = format >= 48 ? null : false; // not written: on from format 48
            file.Settings.DroneWaitsOnFoot = format >= 49 ? null : false; // not written: on from format 49
            file.Settings.HitClaims = format >= 50 ? null : false; // not written: on from format 50
            file.Settings.WallClingOnce = format >= 51 ? null : false; // not written: on from format 51
            file.Settings.LighterBlaster = format >= 51 ? null : false; // not written: on from format 51
            file.Settings.DroneBodySplash = format >= 52 ? null : false; // not written: on from format 52
            file.Settings.BodySplashOnce = format >= 53 ? null : false; // not written: on from format 53
            file.Settings.ClaimOneHit = format >= 53 ? null : false; // not written: on from format 53
            file.Settings.ClientPosition = format >= 54 ? null : false; // not written: on from format 54
            file.Settings.WallClingHold = format >= 56 ? null : false; // not written: on from format 56
            file.Settings.BlasterBoltCost = format >= 56 ? null : false; // not written: on from format 56
            file.Settings.LighterBlaster2 = format >= 56 ? null : false; // not written: on from format 56
            file.Settings.BotDropOff = format >= 57 ? null : false; // not written: on from format 57
            file.Settings.BotSteadyAim = format is 57 or 58; // not written: on in formats 57-58 only
            file.Settings.BotSwitchFirst = format >= 57 ? null : false; // not written: on from format 57
            file.Settings.BotFireSight = format >= 57 ? null : false; // not written: on from format 57
            file.Settings.BotRoamLook = format >= 57 ? null : false; // not written: on from format 57
            file.Settings.BotWallCling = format >= 58 ? null : false; // not written: on from format 58
            file.Settings.BotCrates = format >= 58 ? null : false; // not written: on from format 58
            file.Settings.BotVanillaParity = format >= 61 ? null : false; // not written: on from format 61
            file.Settings.LagCompFix = format >= 46 ? null : false; // not written: on from format 46
            file.OldTurretHash = format < 42;
            file.OldEchoesWater = format < 16 && file.Settings.EchoesMoves != false
                && file.Settings.Arena == MphRecomp.Arenas.EchoesArena.Find("M04_Pipeline")?.Room;
            if (format >= 17)
            {
                int identities = r.ReadInt32();
                for (int i = 0; i < identities; i++)
                {
                    int seat = r.ReadInt32();
                    string? friendCode = r.ReadBoolean() ? r.ReadString() : null;
                    string name = r.ReadString();
                    string? licenseJson = r.ReadBoolean() ? r.ReadString() : null;
                    int tag = format >= 19 ? r.ReadInt32() : 0;
                    file.Identities.Add(new ReplaySeatIdentity(seat, friendCode, name, licenseJson, tag));
                }
            }
            file.HasMarks = format >= 18;
            if (format >= 18)
            {
                int marks = r.ReadInt32();
                for (int i = 0; i < marks; i++)
                {
                    file.Marks.Add(new ReplayMark(r.ReadInt32(), r.ReadByte(), r.ReadBoolean()));
                }
            }
            if (format >= 19)
            {
                file.Net = r.ReadString();
                file.OwnSeat = r.ReadInt32();
                int events = r.ReadInt32();
                int step = 0;
                for (int i = 0; i < events; i++)
                {
                    step += r.Read7BitEncodedInt();
                    byte seat = r.ReadByte();
                    var kind = (ReplayNetKind)r.ReadByte();
                    byte[]? input = kind == ReplayNetKind.Input || kind == ReplayNetKind.Hit && format >= 50
                        || kind == ReplayNetKind.Position && format >= 54 ? r.ReadBytes(r.ReadByte()) : null;
                    if (format < 26 && input != null && input.Length == MphRecomp.Multiplayer.Net.NetInput.OldRecordSize)
                    {
                        input = MphRecomp.Multiplayer.Net.NetInput.UpgradeOldRecord(input); // (no touch bytes: the stylus up)
                    }
                    if (kind == ReplayNetKind.Hit && format >= 50)
                    {
                        if (input!.Length != MphRecomp.Campaign.CampaignHost.ClaimRecordSize) file.OldNetInput = true;
                    }
                    else if (kind == ReplayNetKind.Position && format >= 54)
                    {
                        if (!MphRecomp.Campaign.CampaignHost.IsPositionRecord(input!)) file.OldNetInput = true;
                    }
                    else if (input != null && input.Length != MphRecomp.Multiplayer.Net.NetInput.RecordSize || kind > ReplayNetKind.Rejoin)
                    {
                        file.OldNetInput = true; // (a network input or event this build doesn't have)
                    }
                    file.NetEvents.Add(new ReplayNetEvent(step, seat, kind, input));
                }
            }
            if (format >= 21)
            {
                int seats = r.ReadInt32();
                for (int i = 0; i < seats; i++)
                {
                    string skin = r.ReadString();
                    if (i < file.Settings.Players.Count) file.Settings.Players[i].Skin = skin.Length == 0 ? null : skin;
                }
            }
            file.Settings.Abilities = format >= 22 ? (r.ReadBoolean() ? r.ReadBoolean() : null) : null;
            file.Settings.PirateBlaster = format >= 23 ? (r.ReadBoolean() ? r.ReadBoolean() : null) : false;
            if (format >= 45) file.Settings.HyperBall = r.ReadBoolean();
            file.Settings.HyperBeam = format >= 60 ? r.ReadBoolean() : false;
            file.Settings.BotStuckFix = format >= 62 ? r.ReadBoolean() : false;
            file.Settings.BotSwitchOrder = format >= 63 ? r.ReadBoolean() : false;
            file.Settings.BotNodeFlat = format >= 64 ? r.ReadBoolean() : false;
            file.Settings.BotStuckSight = format >= 65 ? r.ReadBoolean() : false;
            file.Settings.BotBreakWalls = format >= 66 ? r.ReadBoolean() : false;
            file.Settings.BotStuckVertical = format >= 67 ? r.ReadBoolean() : false;
            file.Settings.BotEdgeJump = format >= 68 ? r.ReadBoolean() : false;
            file.Settings.BotEdgePadJump = format >= 69 ? r.ReadBoolean() : false;
            file.Settings.BotReleaseControls = format >= 70 ? r.ReadBoolean() : false;
            file.Settings.BotLiftGuard = format >= 71 ? r.ReadBoolean() : false;
            file.Settings.BotUpLifts = format >= 72 ? r.ReadBoolean() : false;
            file.Settings.BotArriveTick = format >= 72 ? r.ReadBoolean() : false;
            file.Settings.BotEdgeHold = format >= 73 ? r.ReadBoolean() : false;
            if (format >= 74)
                foreach (MatchPlayer p in file.Settings.Players)
                {
                    int pick = r.ReadSByte();
                    p.ColourPick = pick >= 0 && pick < LookAlikes.Colours ? pick : -1;
                }
            steps = r.ReadInt32();
            // (each step is at least its 4-byte checksum: a count past what's left is a made-up file)
            if (steps < 0 || r.BaseStream.CanSeek && steps > (r.BaseStream.Length - r.BaseStream.Position) / 4)
            {
                throw new InvalidDataException("damaged replay file (steps)");
            }
            if (headerOnly)
            {
                return file;
            }
            file.Steps.Capacity = steps;
            for (int i = 0; i < steps; i++)
            {
                file.Steps.Add(new ReplayStep { Input = ReadInput(r, format), Checksum = r.ReadUInt32() });
            }
            if (format >= 47)
            {
                int checks = r.ReadInt32();
                for (int i = 0; i < checks; i++)
                {
                    file.ShotChecks.Add(new ReplayShotCheck(r.ReadInt32(), r.ReadByte(), (ReplayShotKind)r.ReadByte(), r.ReadSingle(),
                        r.ReadSingle(), r.ReadSByte(), r.ReadSingle()));
                }
            }
            return file;
        }

        public void Save(string path)
        {
            using FileStream fs = File.Create(path);
            Write(fs);
        }

        public static ReplayFile Load(string path)
        {
            using FileStream fs = File.OpenRead(path);
            return Read(fs);
        }

        // a deep copy, so a caller changing its settings later doesn't change the recording
        public static MatchSettings CopySettings(MatchSettings s)
        {
            var ms = new MemoryStream();
            using (var w = new BinaryWriter(ms, System.Text.Encoding.UTF8, leaveOpen: true))
            {
                WriteSettings(w, s);
            }
            ms.Position = 0;
            using var r = new BinaryReader(ms, System.Text.Encoding.UTF8);
            MatchSettings copy = ReadSettings(r);
            copy.Crates = s.Crates; // stored after Places (format 4), not in the settings block
            copy.Breakables = s.Breakables; // after Crates (format 5)
            copy.EchoesMoves = s.EchoesMoves; // after the Characters (format 13)
            copy.EchoesLedgeJump = s.EchoesLedgeJump; // format 20 (not written)
            copy.EchoesAmmo = s.EchoesAmmo; // format 24 (not written)
            copy.EchoesPadNoFall = s.EchoesPadNoFall; // format 24 (not written)
            copy.WallClingJumpRule = s.WallClingJumpRule; // format 25 (not written)
            copy.DronePadArc = s.DronePadArc; // format 25 (not written)
            copy.HoverWalk = s.HoverWalk; // format 25 (not written)
            copy.PirateBlasterKey = s.PirateBlasterKey; // format 27 (not written)
            copy.EchoesBotMoves = s.EchoesBotMoves; // format 28 (not written)
            copy.PlacedPickups = s.PlacedPickups; // format 29 (not written)
            copy.BotTickPace = s.BotTickPace; // format 30 (not written)
            copy.TeamHitsIgnored = s.TeamHitsIgnored; // format 31 (not written)
            copy.HyperBall = s.HyperBall; // format 32 (written from format 45)
            copy.Paralyzer = s.Paralyzer; // format 33 (not written)
            copy.HyperBeam = s.HyperBeam; // format 60
            copy.BotStuckFix = s.BotStuckFix; // format 62
            copy.BotSwitchOrder = s.BotSwitchOrder; // format 63
            copy.BotNodeFlat = s.BotNodeFlat; // format 64
            copy.BotStuckSight = s.BotStuckSight; // format 65
            copy.BotBreakWalls = s.BotBreakWalls; // format 66
            copy.BotStuckVertical = s.BotStuckVertical; // format 67
            copy.BotEdgeJump = s.BotEdgeJump; // format 68
            copy.BotEdgePadJump = s.BotEdgePadJump; // format 69
            copy.BotReleaseControls = s.BotReleaseControls; // format 70
            copy.BotLiftGuard = s.BotLiftGuard; // format 71
            copy.BotUpLifts = s.BotUpLifts; // format 72
            copy.BotArriveTick = s.BotArriveTick; // format 72
            copy.BotEdgeHold = s.BotEdgeHold; // format 73

            copy.HyperBallCorruption = s.HyperBallCorruption; // format 34 (not written)
            copy.DroneBodyHeadshots = s.DroneBodyHeadshots; // format 35 (not written)
            copy.CrateBlaster = s.CrateBlaster; // format 36 (not written)
            copy.EchoesTurret = s.EchoesTurret; // format 37 (not written)
            copy.ContinuousHeadshots = s.ContinuousHeadshots; // format 38 (not written)
            copy.VanillaZoom = s.VanillaZoom; // format 39 (not written)
            copy.ShockCoilGrace = s.ShockCoilGrace; // format 40 (not written)
            copy.EchoesTurretBots = s.EchoesTurretBots; // format 41 (not written)
            copy.RecolorPerSkin = s.RecolorPerSkin; // format 43 (not written)
            copy.NoBallJump = s.NoBallJump; // format 44 (not written)
            copy.LagCompFix = s.LagCompFix; // format 46 (not written)
            copy.DroneLaserNoPush = s.DroneLaserNoPush; // format 47 (not written)
            copy.DroneSwitchRule = s.DroneSwitchRule; // format 48 (not written)
            copy.DroneWaitsOnFoot = s.DroneWaitsOnFoot; // format 49 (not written)
            copy.HitClaims = s.HitClaims; // format 50 (not written)
            copy.WallClingOnce = s.WallClingOnce; // format 51 (not written)
            copy.LighterBlaster = s.LighterBlaster; // format 51 (not written)
            copy.DroneBodySplash = s.DroneBodySplash; // format 52 (not written)
            copy.BodySplashOnce = s.BodySplashOnce; // format 53 (not written)
            copy.ClaimOneHit = s.ClaimOneHit; // format 53 (not written)
            copy.ClientPosition = s.ClientPosition; // format 54 (not written)
            copy.WallClingHold = s.WallClingHold; // format 56 (not written)
            copy.BlasterBoltCost = s.BlasterBoltCost; // format 56 (not written)
            copy.LighterBlaster2 = s.LighterBlaster2; // format 56 (not written)
            copy.BotDropOff = s.BotDropOff; // format 57 (not written)
            copy.BotSteadyAim = s.BotSteadyAim; // formats 57-58 (not written)
            copy.BotSwitchFirst = s.BotSwitchFirst; // format 57 (not written)
            copy.BotFireSight = s.BotFireSight; // format 57 (not written)
            copy.BotRoamLook = s.BotRoamLook; // format 57 (not written)
            copy.BotWallCling = s.BotWallCling; // format 58 (not written)
            copy.BotCrates = s.BotCrates; // format 58 (not written)
            copy.BotVanillaParity = s.BotVanillaParity; // format 61 (not written)
            copy.Abilities = s.Abilities; // after the Skins (format 22)
            copy.PirateBlaster = s.PirateBlaster; // after Abilities (format 23)
            copy.ExtraCharacters = s.ExtraCharacters; // (not recorded: the seats already have them stripped)
            // (ServedHost: read back from the seats by ReadSettings, as a file's is, so the recording and its playback agree)
            for (int i = 0; i < copy.Players.Count && i < s.Players.Count; i++)
            {
                copy.Players[i].Character = s.Players[i].Character; // after Breakables (format 6)
                copy.Players[i].Skin = s.Players[i].Skin; // after NetEvents (format 21)
                copy.Players[i].ColourPick = s.Players[i].ColourPick; // after BotEdgeHold (format 74)
            }
            return copy;
        }

        // format 6: one string per seat ("" = the hunter's own)
        private static void WriteCharacters(BinaryWriter w, MatchSettings s)
        {
            w.Write(s.Players.Count);
            foreach (MatchPlayer p in s.Players)
            {
                w.Write(p.Character ?? "");
            }
        }

        private static void ReadCharacters(BinaryReader r, MatchSettings s)
        {
            int count = r.ReadInt32();
            for (int i = 0; i < count; i++)
            {
                string character = r.ReadString();
                if (i < s.Players.Count)
                {
                    s.Players[i].Character = character.Length == 0 ? null : character;
                }
            }
        }

        private static void WriteSettings(BinaryWriter w, MatchSettings s)
        {
            w.Write((int)s.Mode);
            w.Write(s.Arena);
            w.Write(s.LocalSeat);
            w.Write(s.IsClient);
            WriteOpt(w, s.PointGoal);
            WriteOpt(w, s.TimeLimitSeconds);
            WriteOpt(w, s.TimeGoalSeconds);
            WriteOpt(w, s.DamageLevel);
            WriteOpt(w, s.FriendlyFire);
            WriteOpt(w, s.OctolithReset);
            WriteOpt(w, s.RadarPlayers);
            WriteOpt(w, s.AffinityWeapons);
            w.Write(s.Players.Count);
            foreach (MatchPlayer p in s.Players)
            {
                w.Write((int)p.Hunter);
                w.Write(p.Recolor);
                w.Write(p.Team);
                w.Write(p.Bot);
                w.Write(p.Remote);
                w.Write(p.BotLevel);
                w.Write(p.Nickname != null);
                if (p.Nickname != null)
                {
                    w.Write(p.Nickname);
                }
            }
        }

        private static MatchSettings ReadSettings(BinaryReader r)
        {
            var s = new MatchSettings
            {
                Mode = (GameMode)r.ReadInt32(),
                Arena = r.ReadString(),
                LocalSeat = r.ReadInt32(),
                IsClient = r.ReadBoolean(),
                PointGoal = r.ReadBoolean() ? r.ReadInt32() : null,
                TimeLimitSeconds = r.ReadBoolean() ? r.ReadSingle() : null,
                TimeGoalSeconds = r.ReadBoolean() ? r.ReadSingle() : null,
                DamageLevel = r.ReadBoolean() ? r.ReadInt32() : null,
                FriendlyFire = r.ReadBoolean() ? r.ReadBoolean() : null,
                OctolithReset = r.ReadBoolean() ? r.ReadBoolean() : null,
                RadarPlayers = r.ReadBoolean() ? r.ReadBoolean() : null,
                AffinityWeapons = r.ReadBoolean() ? r.ReadBoolean() : null
            };
            int players = r.ReadInt32();
            for (int i = 0; i < players; i++)
            {
                s.Players.Add(new MatchPlayer
                {
                    Hunter = (Hunter)r.ReadInt32(),
                    Recolor = r.ReadInt32(),
                    Team = r.ReadInt32(),
                    Bot = r.ReadBoolean(),
                    Remote = r.ReadBoolean(),
                    BotLevel = r.ReadInt32(),
                    Nickname = r.ReadBoolean() ? r.ReadString() : null
                });
            }
            // a served match host's (not written: a host's recording whose own seat is a remote player can only be one, since
            // MatchSettings.Validate refuses that seat otherwise; the served host keeps LocalSeat 0 = the first joiner)
            s.ServedHost = !s.IsClient && s.LocalSeat >= 0 && s.LocalSeat < s.Players.Count && s.Players[s.LocalSeat].Remote;
            return s;
        }

        private static void WriteOpt(BinaryWriter w, int? v) { w.Write(v.HasValue); if (v.HasValue) w.Write(v.Value); }
        private static void WriteOpt(BinaryWriter w, float? v) { w.Write(v.HasValue); if (v.HasValue) w.Write(v.Value); }
        private static void WriteOpt(BinaryWriter w, bool? v) { w.Write(v.HasValue); if (v.HasValue) w.Write(v.Value); }

        private static void WriteInput(BinaryWriter w, in CampaignInput input)
        {
            w.Write(input.Move.X);
            w.Write(input.Move.Y);
            w.Write(input.AimDelta.X);
            w.Write(input.AimDelta.Y);
            w.Write((int)input.Buttons);
            w.Write((sbyte)input.SelectWeapon);
            w.Write(input.WeaponWheel.X);
            w.Write(input.WeaponWheel.Y);
            w.Write(input.DialogButton is HostDialogButton b ? (sbyte)b : (sbyte)-1);
            w.Write(input.TouchDown); // format 26
            w.Write(input.TouchDx);
            w.Write(input.TouchDy);
        }

        private static CampaignInput ReadInput(BinaryReader r, int format)
        {
            var input = new CampaignInput
            {
                Move = new System.Numerics.Vector2(r.ReadSingle(), r.ReadSingle()),
                AimDelta = new System.Numerics.Vector2(r.ReadSingle(), r.ReadSingle()),
                Buttons = (CampaignButtons)r.ReadInt32(),
                SelectWeapon = (BeamType)r.ReadSByte(),
                WeaponWheel = new System.Numerics.Vector2(r.ReadSingle(), r.ReadSingle())
            };
            sbyte dialog = r.ReadSByte();
            input.DialogButton = dialog < 0 ? null : (HostDialogButton)dialog;
            if (format >= 26)
            {
                input.TouchDown = r.ReadBoolean();
                input.TouchDx = r.ReadSByte();
                input.TouchDy = r.ReadSByte();
            }
            return input;
        }

        private static uint[]? _crcTable;

        public static uint Crc32(ReadOnlySpan<byte> data)
        {
            uint[] table = _crcTable ??= Enumerable.Range(0, 256).Select(n =>
            {
                uint c = (uint)n;
                for (int k = 0; k < 8; k++)
                {
                    c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                }
                return c;
            }).ToArray();
            uint crc = 0xFFFFFFFFu;
            foreach (byte b in data)
            {
                crc = table[(crc ^ b) & 0xFF] ^ (crc >> 8);
            }
            return ~crc;
        }
    }
}
