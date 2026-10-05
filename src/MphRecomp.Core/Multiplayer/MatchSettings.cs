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

        public int? PointGoal { get; set; } // kills / octoliths / node points; spare lives in survival
        public float? TimeLimitSeconds { get; set; }
        public float? TimeGoalSeconds { get; set; } // defender / prime hunter
        public int? DamageLevel { get; set; } // 0 low, 1 medium, 2 high
        public bool? FriendlyFire { get; set; }
        public bool? OctolithReset { get; set; }
        public bool? RadarPlayers { get; set; }
        public bool? AffinityWeapons { get; set; }

        public static bool IsTeamMode(GameMode mode) => mode is GameMode.BattleTeams or GameMode.SurvivalTeams
            or GameMode.Capture or GameMode.BountyTeams or GameMode.NodesTeams or GameMode.DefenderTeams;

        public static readonly IReadOnlyList<GameMode> Modes = new[]
        {
            GameMode.Battle, GameMode.BattleTeams, GameMode.Survival, GameMode.SurvivalTeams, GameMode.Capture,
            GameMode.Bounty, GameMode.BountyTeams, GameMode.Nodes, GameMode.NodesTeams, GameMode.Defender,
            GameMode.DefenderTeams, GameMode.PrimeHunter
        };

        // A quick local match: the player plus bots of the given level on distinct hunters.
        public static MatchSettings Quick(GameMode mode, string arena, Hunter hunter, int bots, int botLevel = 1)
        {
            var settings = new MatchSettings { Mode = mode, Arena = arena };
            settings.Players.Add(new MatchPlayer { Hunter = hunter, Nickname = "Player1" });
            var used = new HashSet<Hunter> { hunter };
            foreach (Hunter h in _botOrder)
            {
                if (settings.Players.Count > Math.Clamp(bots, 0, 3))
                {
                    break;
                }
                if (used.Add(h))
                {
                    settings.Players.Add(new MatchPlayer { Hunter = h, Bot = true, BotLevel = botLevel });
                }
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
            if (Players.Count < 2 || Players.Count > 4)
            {
                throw new ArgumentException($"a match needs 2-4 players (has {Players.Count})");
            }
            if (LocalSeat < 0 || LocalSeat >= Players.Count || Players[LocalSeat].Bot || Players[LocalSeat].Remote)
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
            if (!MatchArenas.IsArena(Arena))
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
