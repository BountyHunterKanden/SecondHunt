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
            Scene.Headless = true;
            Scene.CollectDrawItems = collectDrawItems;
            MphRead.Music.Init(); // see Start: the arena's music setup needs the tables
            Menu.SaveSlot = 0;
            Cheats.FreeWeaponSelect = false;
            // same float results on every device (replays and LAN play across devices; MphRead/Utility/DetMath.cs)
            DetMathF.HostDeterministic = true;
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
                player.HostDronePadArc = settings.DronePadArc ?? true;
                player.HostHoverWalk = Characters.IsDarkSamus(seat) && (settings.HoverWalk ?? true);
                player.HostPirateBlasterKey = settings.PirateBlasterKey ?? true;
                player.HostScoutDrone = abilities && Characters.HasScoutDrone(seat);
                player.HostAffinityBeam = Characters.AffinityBeamFor(settings, seat); // recomp: the Trooper's Pirate Blaster
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
            MphRecomp.Arenas.EchoesBreakables.Enabled = MphRecomp.Arenas.EchoesBreakables.For(settings);
            MphRecomp.Arenas.EchoesBreakables.Mirror = settings.IsClient; // (a client follows the host's, Net/NetWorld.cs)
            // and Echoes movement there (double jump, water, Gravity Boost, spider ball; old replays: off)
            MphRecomp.Arenas.EchoesMovement.Enabled = MphRecomp.Arenas.EchoesMovement.For(settings);
            MphRecomp.Arenas.EchoesMovement.LedgeJump = settings.EchoesLedgeJump ?? true;
            MphRecomp.Arenas.EchoesMovement.PadNoFall = settings.EchoesPadNoFall ?? true;
            MphRecomp.Arenas.EchoesMovement.BotMoves = settings.EchoesBotMoves ?? true;   // T7
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
