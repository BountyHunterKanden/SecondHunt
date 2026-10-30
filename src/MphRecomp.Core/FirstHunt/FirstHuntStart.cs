using MphRead;
using MphRead.Entities;
using MphRecomp.Multiplayer;
using OpenTK.Mathematics;

namespace MphRecomp.Campaign
{
    // A First Hunt level (docs/FIRST_HUNT_PORT.md) on the same headless host as the campaign and matches: MphRead loads
    // the room from the imported game (FirstHunt/FirstHuntGame.cs, Paths.FhFileSystem) with its own First Hunt entities.
    // P0: Samus alone in the level, under MphRead's Battle rules; First Hunt's own mode rules (training missions,
    // deathmatch) come on top in later phases.
    public sealed partial class CampaignHost
    {
        // the First Hunt level this host is running (null otherwise)
        public string? FirstHuntRoom { get; private set; }

        public static CampaignHost StartFirstHunt(string roomName, bool collectDrawItems = false, int viewWidth = 256, int viewHeight = 192)
        {
            (RoomMetadata? meta, _) = Metadata.GetRoomByName(roomName);
            if (meta == null || !meta.FirstHunt) throw new System.ArgumentException($"{roomName} is not a First Hunt level");
            Scene.Headless = true;
            Scene.CollectDrawItems = collectDrawItems;
            MphRead.Music.Init(); // (see Start)
            Menu.SaveSlot = 0;
            Cheats.FreeWeaponSelect = false;
            DetMathF.HostDeterministic = false;
            PlayerEntity.MaxPlayers = PlayerEntity.VanillaMaxPlayers;
            PlayerEntity.MainPlayerIndex = 0;
            PlayerEntity.NetClient = false;
            PlayerEntity.HostAnyShieldWall = false;
            PlayerEntity.HostSevenWeaponSlots = false;
            PlayerEntity.HostBotTickPace = true;
            PlayerEntity.HostBotDropOff = false;
            PlayerEntity.HostBotSteadyAim = false;
            PlayerEntity.HostBotSwitchFirst = false;
            PlayerEntity.HostBotFireSight = false;
            PlayerEntity.HostBotRoamLook = false;
            PlayerEntity.HostBotWallCling = false;
            PlayerEntity.HostBotCrates = false;
            PlayerEntity.HostBotVanillaParity = false;
            PlayerEntity.HostBotStuckFix = false;
            PlayerEntity.HostBotSwitchOrder = false;
            PlayerEntity.HostBotNodeFlat = false;
            PlayerEntity.HostBotStuckSight = false;
            PlayerEntity.HostBotBreakWalls = false;
            PlayerEntity.HostBotStuckVertical = false;
            PlayerEntity.HostBotEdgeJump = false;
            PlayerEntity.HostBotEdgePadJump = false;
            PlayerEntity.HostBotReleaseControls = false;
            PlayerEntity.HostBotLiftGuard = false;
            PlayerEntity.HostBotUpLifts = false;
            PlayerEntity.HostBotArriveTick = false;
            PlayerEntity.HostBotEdgeHold = false;
            // nothing of the extra arenas' (a match before may have left them on)
            MphRecomp.Arenas.EchoesCrates.Enabled = false;
            MphRecomp.Arenas.EchoesBreakables.Enabled = false;
            MphRecomp.Arenas.EchoesMovement.Enabled = false;
            MphRecomp.Arenas.EchoesAmmo.Enabled = false;
            CampaignHost? host = null;
            var scene = new Scene(new Vector2i(viewWidth, viewHeight), null!, null!, _ => { }, () =>
            {
                if (host != null)
                {
                    host.Ended = true;
                }
            });
            host = new CampaignHost(scene) { FirstHuntRoom = meta.Name };
            scene.AddPlayer(Hunter.Samus);
            PlayerEntity me = PlayerEntity.Players[0];
            me.IsBot = false;
            me.HostRemote = false;
            me.HostShieldWall = me.HostWallCling = me.HostHoverWalk = me.HostBallJump = me.HostHyperBall = me.HostScoutDrone = false;
            me.HostSoloUa = false;
            GameState.Nicknames[0] = "Player1";
            // the Regulator is the cart's mode 1, two players: slot 1 an AI Samus (the +0x42c player: 3 tanks, PlayerFirstHunt)
            // who spawns only at the last room's bots-only point once the player reaches it, and whose death is the win
            // (FirstHuntMission); MphRead's bot brain drives her on the level's own node data (regulator_Node.bin)
            int players = 1;
            if (meta.Name == "Level SP Regulator")
            {
                scene.AddPlayer(Hunter.Samus, recolor: 1);
                PlayerEntity cpu = PlayerEntity.Players[1];
                cpu.HostShieldWall = cpu.HostWallCling = cpu.HostHoverWalk = cpu.HostBallJump = cpu.HostHyperBall = cpu.HostScoutDrone = false;
                cpu.HostHyperBallCorruption = cpu.HostSoloUa = false;
                cpu.HostAffinityBeam = null;
                cpu.HostHealthPercent = 100;
                cpu.IsBot = true;
                cpu.BotLevel = 1;
                cpu.HostRemote = false;
                GameState.Nicknames[1] = "Samus";
                players = 2;
            }
            PlayerEntity.HostFirstHunt = true; // (before the player initializes: First Hunt's values; FirstHuntRoom sets it per room)
            MphRecomp.FirstHunt.FirstHuntRoom.Loading(meta); // (its pads, morph cameras and ball rules)
            scene.AddRoom(meta.Name, GameMode.Battle, players);
            GameState.MatchTime = -1; // (MphRead: never runs out; nobody wins on points)
            GameState.PointGoal = 0;
            FirstHuntMission.HostOver = () => host.Ended = true; // (P2: the training game's end; results with P4)
            scene.OnLoad();
            return host;
        }

        // A First Hunt deathmatch (docs/FIRST_HUNT_PORT.md P5; fh_research p5_multiplayer.md): MatchStart's match on a First
        // Hunt arena (Level MP1-MP3, MatchSettings.IsFirstHuntArena). As the cart: 2-4 players, all Samus in their slot's
        // colour, no bots, deathmatch with the level's own time limit (FirstHuntMission.TimeLimit: MP1 5:00, MP2 8:00, MP3
        // 5:00) and no kill limit; a kill scores, a fall or a self-kill only counts a death (PlayerEntity.HostFirstHunt).
        // GameState keeps the clock and the scores, so a LAN client gets them in the host's snapshots like any match's.
        private static CampaignHost StartFirstHuntMatch(MatchSettings settings, bool collectDrawItems, int viewWidth, int viewHeight)
        {
            (RoomMetadata? meta, _) = Metadata.GetRoomByName(settings.Arena);
            if (meta == null || !meta.FirstHunt) throw new System.ArgumentException($"{settings.Arena} is not a First Hunt level");
            if (settings.Players.Count > PlayerEntity.VanillaMaxPlayers)
            {
                throw new System.ArgumentException($"a First Hunt match is 2-{PlayerEntity.VanillaMaxPlayers} players");
            }
            if (settings.Players.Exists(p => p.Bot))
            {
                throw new System.ArgumentException("a First Hunt match has no bots");
            }
            Scene.Headless = true;
            Scene.CollectDrawItems = collectDrawItems;
            MphRead.Music.Init(); // (see Start)
            Menu.SaveSlot = 0;
            Cheats.FreeWeaponSelect = false;
            DetMathF.HostDeterministic = true; // (as StartMatch: LAN play across devices)
            PlayerEntity.MaxPlayers = PlayerEntity.VanillaMaxPlayers;
            PlayerEntity.HostAnyShieldWall = false;
            PlayerEntity.HostSevenWeaponSlots = false;
            PlayerEntity.HostBotTickPace = true;
            PlayerEntity.HostBotDropOff = false;
            PlayerEntity.HostBotSteadyAim = false;
            PlayerEntity.HostBotSwitchFirst = false;
            PlayerEntity.HostBotFireSight = false;
            PlayerEntity.HostBotRoamLook = false;
            PlayerEntity.HostBotWallCling = false;
            PlayerEntity.HostBotCrates = false;
            PlayerEntity.HostBotVanillaParity = false;
            PlayerEntity.HostBotStuckFix = false;
            PlayerEntity.HostBotSwitchOrder = false;
            PlayerEntity.HostBotNodeFlat = false;
            PlayerEntity.HostBotStuckSight = false;
            PlayerEntity.HostBotBreakWalls = false;
            PlayerEntity.HostBotStuckVertical = false;
            PlayerEntity.HostBotEdgeJump = false;
            PlayerEntity.HostBotEdgePadJump = false;
            PlayerEntity.HostBotReleaseControls = false;
            PlayerEntity.HostBotLiftGuard = false;
            PlayerEntity.HostBotUpLifts = false;
            PlayerEntity.HostBotArriveTick = false;
            PlayerEntity.HostBotEdgeHold = false;
            MphRecomp.Arenas.EchoesCrates.Enabled = false;
            MphRecomp.Arenas.EchoesBreakables.Enabled = false;
            MphRecomp.Arenas.EchoesMovement.Enabled = false;
            MphRecomp.Arenas.EchoesAmmo.Enabled = false;
            CampaignHost? host = null;
            var scene = new Scene(new Vector2i(viewWidth, viewHeight), null!, null!, _ => { }, () =>
            {
                if (host != null)
                {
                    host.Ended = true;
                }
            });
            host = new CampaignHost(scene) { Match = settings, FirstHuntRoom = meta.Name };
            PlayerEntity.MainPlayerIndex = settings.LocalSeat;
            PlayerEntity.NetClient = settings.IsClient;
            PlayerEntity.HostNoLocalSeat = false; // (only a served match host has none: MatchStart)
            PlayerEntity.HostFirstHunt = true; // (before the players initialize: First Hunt's values)
            System.Array.Clear(GameState.HiddenNames);
            for (int i = 0; i < settings.Players.Count; i++)
            {
                MatchPlayer seat = settings.Players[i];
                // the slot's colour (the cart's yellow, green, white, blue: todo, its own models; MPH Samus' palettes for now)
                scene.AddPlayer(Hunter.Samus, recolor: i);
                PlayerEntity player = PlayerEntity.Players[i];
                // (the seats are reused from match to match: none of Second Hunt's extras)
                player.HostShieldWall = player.HostWallCling = player.HostHoverWalk = player.HostBallJump = false;
                player.HostHyperBall = player.HostHyperBallCorruption = player.HostScoutDrone = player.HostSoloUa = false;
                player.HostWallClingJumpRule = player.HostDronePadArc = player.HostPirateBlasterKey = true;
                player.HostTeamHitsIgnored = true;
                player.HostAffinityBeam = null;
                player.HostHealthPercent = 100;
                player.IsBot = false;
                player.BotLevel = 0;
                player.HostRemote = seat.Remote;
                if (seat.Remote)
                {
                    player.LoadFlags |= LoadFlags.Connected | LoadFlags.WasConnected;
                }
                GameState.Nicknames[i] = seat.Nickname ?? $"Player{i + 1}";
                GameState.HiddenNames[i] = !seat.Remote; // (STREAMING: SHOW NAME off; this device's seat, never a name match)
            }
            MphRecomp.FirstHunt.FirstHuntRoom.Loading(meta);
            scene.AddRoom(meta.Name, GameMode.Battle, settings.Players.Count);
            GameState.PointGoal = 0; // (no kill limit)
            GameState.MatchTime = settings.TimeLimitSeconds ?? FirstHuntMission.TimeLimitSeconds; // (the lobby sets none: tests do)
            FirstHuntMission.HostOver = null; // (the match ends through GameState, as any match)
            scene.OnLoad();
            return host;
        }
    }
}
