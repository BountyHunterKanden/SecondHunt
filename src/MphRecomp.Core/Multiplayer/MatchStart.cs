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
            for (int i = 0; i < settings.Players.Count; i++)
            {
                MatchPlayer seat = settings.Players[i];
                scene.AddPlayer(seat.Hunter, seat.Recolor, teams?[i] ?? -1);
                // AddPlayer makes every seat after the first a bot; the seat list decides (bot, remote human or local)
                PlayerEntity player = PlayerEntity.Players[i];
                player.IsBot = seat.Bot;
                player.HostRemote = seat.Remote;
                if (seat.Remote)
                {
                    // the game's own network flags: a seat that WAS connected and no longer is drops out of the match
                    player.LoadFlags |= LoadFlags.Connected | LoadFlags.WasConnected;
                }
                player.BotLevel = seat.Bot ? System.Math.Clamp(seat.BotLevel, 0, 2) : 0;
                GameState.Nicknames[i] = seat.Nickname ?? (seat.Bot ? seat.Hunter.ToString() : $"Player{i + 1}");
            }
            scene.AddRoom(settings.Arena, settings.Mode, settings.Players.Count);
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
