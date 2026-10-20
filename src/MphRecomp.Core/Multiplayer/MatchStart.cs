using MphRead;
using MphRead.Entities;
using MphRecomp.Multiplayer;
using OpenTK.Mathematics;

namespace MphRecomp.Campaign
{
    // A multiplayer match runs on the same headless host as the campaign: MphRead's own match logic (GameState's
    // mode states, scoring, match end camera and fade), bot AI (PlayerAi) and HUD, stepped with the same
    // CampaignInput for the local seat. Everything that draws or plays a campaign session draws or plays a match.
    public sealed partial class CampaignHost
    {
        // The match this host is running (null for a campaign session).
        public MatchSettings? Match { get; private set; }

        public static CampaignHost StartMatch(MatchSettings settings, bool collectDrawItems = false,
            int viewWidth = 256, int viewHeight = 192)
        {
            settings.Validate();
            if (MatchSettings.IsFirstHuntArena(settings.Arena))
            {
                return StartFirstHuntMatch(settings, collectDrawItems, viewWidth, viewHeight); // recomp: First Hunt (FirstHuntStart.cs)
            }
            PlayerEntity.HostFirstHunt = false; // (a First Hunt game before: its values, before the players initialize)
            Scene.Headless = true;
            Scene.CollectDrawItems = collectDrawItems;
            MphRead.Music.Init(); // see Start: the arena's music setup needs the tables
            Menu.SaveSlot = 0;
            Cheats.FreeWeaponSelect = false;
            // same float results on every device (replays and LAN play across devices; MphRead/Utility/DetMath.cs)
            DetMathF.HostDeterministic = true;
            PlayerEntity.HostClaimOneHit = settings.ClaimOneHit ?? true;
            BeamProjectileEntity.HostBodySplashOnce = settings.BodySplashOnce ?? true;
            PlayerEntity.HostBotDropOff = settings.BotDropOff ?? false; // (bots over a drop-down node: PlayerAiDropOff.cs; off for now, 2026-10-20: kept out of the release)
            PlayerEntity.HostHitClaims = settings.HitClaims ?? true; // (only a host's remote seats keep shots: Net/HitClaims.cs)
            bool lighterBlaster = settings.LighterBlaster ?? true; // (the blaster's bolt damage: PirateBlaster.cs)
            bool lighterBlaster2 = settings.LighterBlaster2 ?? true; // (9 / 13 instead of 10 / 14, 2026-10-20)
            PirateBlaster.HostBoltDamage = !lighterBlaster ? 0 : lighterBlaster2 ? PirateBlaster.LighterBolt2 : PirateBlaster.LighterBolt;
            PirateBlaster.HostBoltHeadshot = !lighterBlaster ? 0
                : lighterBlaster2 ? PirateBlaster.LighterHeadshot2 : PirateBlaster.LighterHeadshot;
            PirateBlaster.HostBoltCost = settings.BlasterBoltCost ?? true; // (each bolt pays a shot of UA: PirateBlaster.cs)
            PlayerEntity.HostDroneLaserNoPush = settings.DroneLaserNoPush ?? true; // (no push from the drone's laser: MatchSettings)
            PlayerEntity.HostLagFix = settings.LagCompFix ?? true; // (an online host's lag compensation, 2026-10-19: MatchSettings)
            PlayerEntity.ResetSeatPings(); // (a network match's driver fills them: Net/SeatPings.cs; none in a local match or replay)
            // the game's 4 seats, or 8 for a match of 5-8 (P8, MphRead PlayerSlots.cs): set before the scene makes the players
            PlayerEntity.MaxPlayers = PlayerEntity.MaxPlayersFor(settings.Players.Count);
            CampaignHost? host = null;
            var scene = new Scene(new Vector2i(viewWidth, viewHeight), null!, null!, _ => { }, () =>
            {
                if (host != null)
                {
                    host.Ended = true;
                }
            });
            host = new CampaignHost(scene) { Match = settings };
            PlayerEntity.MainPlayerIndex = settings.LocalSeat;
            PlayerEntity.NetClient = settings.IsClient;
            PlayerEntity.HostNoLocalSeat = settings.ServedHost && !settings.IsClient; // (a served match host: docs/DEDICATED_SERVERS.md)
            int[]? teams = MatchSettings.IsTeamMode(settings.Mode) ? settings.TeamAssignment() : null;
            settings.AssignRecolors(); // (the same hunter twice: palettes 0-3; every device and replay works it out the same)
            // the Trooper's Wall Cling + Scout Drone (Characters.cs, MphRead PlayerWallCling.cs / PlayerScoutDrone.cs; the
            // parked Shield Wall, PlayerShield.cs, only in its test): local matches and replays for now
            bool abilities = Characters.AbilitiesFor(settings);
            PlayerEntity.HostAnyShieldWall = false;
            PlayerEntity.HostSevenWeaponSlots = settings.PirateBlaster != false; // the weapon ring's 7th slot (the Pirate Blaster)
            for (int i = 0; i < settings.Players.Count; i++)
            {
                MatchPlayer seat = settings.Players[i];
                scene.AddPlayer(seat.Hunter, seat.Recolor, teams?[i] ?? -1);
                // AddPlayer makes every seat after the first a bot; the seat list decides (bot, remote human or local)
                PlayerEntity player = PlayerEntity.Players[i];
                player.HostShieldWall = abilities && Characters.HasShieldWall(seat);
                player.HostWallCling = abilities && Characters.HasWallCling(seat);
                player.HostWallClingJumpRule = settings.WallClingJumpRule ?? true;
                player.HostWallClingOnce = settings.WallClingOnce ?? true;
                player.HostWallClingHold = settings.WallClingHold ?? true;
                player.HostDronePadArc = settings.DronePadArc ?? true;
                player.HostDroneSwitchRule = settings.DroneSwitchRule ?? true;
                player.HostDroneWaitsOnFoot = settings.DroneWaitsOnFoot ?? true;
                player.ResetHitClaims(); // (its shots numbered from 1: Net/HitClaims.cs)
                player.HostDroneBodyHeadshots = settings.DroneBodyHeadshots ?? true;
                player.HostDroneBodySplash = settings.DroneBodySplash ?? true;
                player.HostContinuousHeadshots = settings.ContinuousHeadshots ?? true;
                player.HostShockCoilGrace = settings.ShockCoilGrace ?? true;
                player.HostVanillaZoom = settings.VanillaZoom ?? true;
                player.HostHoverWalk = Characters.IsDarkSamus(seat) && (settings.HoverWalk ?? true);
                // her ball hop + Hyper Ball; null = on in a dev build, off in a public one (owner 2026-10-18: "I never wanted it
                // public I'm not done with it": she has her bombs there, as in 0.2.1)
                bool hyperBall = Characters.IsDarkSamus(seat) && (settings.HyperBall ?? !MphRecomp.Config.RecompSettings.PublicBuild);
                player.HostBallJump = hyperBall && settings.NoBallJump == false; // hop removed (owner 10-18); old replays keep it
                player.HostHyperBall = hyperBall;
                player.HostHyperBallCorruption = hyperBall && (settings.HyperBallCorruption ?? true);
                player.HostSoloUa = settings.Solo; // the dev solo run: full UA for her Hyper Ball (no pickups to grab first)
                player.HostPirateBlasterKey = settings.PirateBlasterKey ?? true;
                player.HostTeamHitsIgnored = settings.TeamHitsIgnored ?? true; // friendly fire off: no hit at all (old replays: vanilla's)
                player.HostScoutDrone = abilities && Characters.HasScoutDrone(seat);
                player.HostAffinityBeam = Characters.AffinityBeamFor(settings, seat); // recomp: the Trooper's Pirate Blaster
                player.HostHealthPercent = Characters.HealthPercentFor(settings, seat); // Zero Suit: 90 (no armour)
                PlayerEntity.HostAnyShieldWall |= player.HostShieldWall;
                player.IsBot = seat.Bot;
                player.HostRemote = seat.Remote;
                if (seat.Remote)
                {
                    // the game's own network flags: a seat that WAS connected and no longer is drops out of the match
                    player.LoadFlags |= LoadFlags.Connected | LoadFlags.WasConnected;
                }
                player.BotLevel = seat.Bot ? System.Math.Clamp(seat.BotLevel, 0, 2) : 0;
                // (a nameless bot: its character's name for TROOPER / DARK SAMUS, D7, LookAlikes.cs)
                string? character = LookAlikes.On ? LookAlikes.CharacterName(seat) : null;
                GameState.Nicknames[i] = seat.Nickname ?? (seat.Bot ? character ?? seat.Hunter.ToString() : $"Player{i + 1}");
            }
            // Echoes arenas' weapon crates join the room's entities as it loads (off in LAN matches and old replays)
            MphRecomp.Arenas.EchoesCrates.Enabled = MphRecomp.Arenas.EchoesCrates.For(settings);
            MphRecomp.Arenas.EchoesCrates.Blaster = (settings.CrateBlaster ?? true) && (settings.PirateBlaster ?? true);
            MphRecomp.Arenas.EchoesBreakables.Enabled = MphRecomp.Arenas.EchoesBreakables.For(settings);
            MphRecomp.Arenas.EchoesBreakables.Mirror = settings.IsClient; // (a client follows the host's, Net/NetWorld.cs)
            // and Shooting Gallery's turret (old replays: off; a client follows the host's, Net/NetWorld.cs)
            MphRecomp.Arenas.EchoesTurret.Enabled = MphRecomp.Arenas.EchoesTurret.For(settings);
            MphRecomp.Arenas.EchoesTurret.Mirror = settings.IsClient;
            MphRecomp.Arenas.EchoesTurretBots.On = settings.EchoesTurretBots ?? true;   // (old replays: off)
            // and Echoes movement there (double jump, water, Gravity Boost, spider ball; old replays: off)
            MphRecomp.Arenas.EchoesMovement.Enabled = MphRecomp.Arenas.EchoesMovement.For(settings);
            MphRecomp.Arenas.EchoesMovement.LedgeJump = settings.EchoesLedgeJump ?? true;
            MphRecomp.Arenas.EchoesMovement.PadNoFall = settings.EchoesPadNoFall ?? true;
            MphRecomp.Arenas.EchoesMovement.BotMoves = settings.EchoesBotMoves ?? true;   // T7
            PlayerEntity.HostBotTickPace = settings.BotTickPace ?? true;   // bots at vanilla's tick pace (old replays: off)
            // and UA pickups there (old replays: none)
            MphRecomp.Arenas.EchoesAmmo.Enabled = MphRecomp.Arenas.EchoesAmmo.For(settings);
            // and the owner's baked pickup placements in any arena, with EXTRA PICKUPS (old replays: none)
            MphRecomp.Arenas.PlacedPickups.Setup(settings);
            scene.AddRoom(settings.Arena, settings.Mode, settings.Players.Count);
            MphRecomp.Arenas.PlacedPickups.ResolveNodes(scene);
            // AddRoom's GameState.Setup applied the mode's defaults; the match's own rules go on top
            if (settings.PointGoal is int points) GameState.PointGoal = points;
            if (settings.TimeLimitSeconds is float time) GameState.MatchTime = time;
            if (settings.TimeGoalSeconds is float goal) GameState.TimeGoal = goal;
            if (settings.DamageLevel is int damage) GameState.DamageLevel = System.Math.Clamp(damage, 0, 2);
            if (settings.FriendlyFire is bool ff) GameState.FriendlyFire = ff;
            if (settings.OctolithReset is bool reset) GameState.OctolithReset = reset;
            if (settings.RadarPlayers is bool radar) GameState.RadarPlayers = radar;
            if (settings.AffinityWeapons is bool affinity) GameState.AffinityWeapons = affinity;
            scene.OnLoad();
            return host;
        }
    }
}
