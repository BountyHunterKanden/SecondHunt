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
        // the Trooper's Wall Cling only after a jump or a real fall (PlayerEntity.HostWallClingJumpRule, T5 N10) and his
        // Scout Drone on a jump pad flying the pad's normal arc (HostDronePadArc, T5 N9): null = on; replays recorded
        // before them (format < 25) read as off, so they play exactly
        public bool? WallClingJumpRule { get; set; }
        public bool? DronePadArc { get; set; }
        // Dark Samus without the walk bob (PlayerEntity.HostHoverWalk, T5 N13; the bob moves her muzzle): null = on;
        // replays recorded before it (format < 25) read as off
        public bool? HoverWalk { get; set; }
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
            if (Players.Count < (Solo ? 1 : 2) || Players.Count > (Solo ? 1 : MaxSeats))
            {
                throw new ArgumentException(Solo ? $"a solo run is one player (has {Players.Count})"
                    : $"a match needs 2-{MaxSeats} players (has {Players.Count})");
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

        // The same hunter on more than one seat (bots and seats 5-8 too; owner report N11, 2026-10-10): the first keeps
        // suit palette 0, the next ones 1, 2, 3 (then round again), so they can be told apart. Vanilla's suits are pal_01-04
        // per hunter plus pal_team01/02; in team modes the team's palette replaces this (MphRead GameState: 4 / 5), as the
        // ROM's body loader does. A seat's colour is the lobby's pick in vanilla (arm9 0x20E83FC); the order is ours.
        public void AssignRecolors()
        {
            for (int i = 0; i < Players.Count; i++)
            {
                Hunter hunter = Players[i].Hunter;
                int before = 0;
                for (int j = 0; j < i; j++)
                {
                    if (Players[j].Hunter == hunter) before++;
                }
                Players[i].Recolor = before % 4;
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
