using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using MphRead;
using MphRead.Entities;
using MphRecomp.Multiplayer;
using MphRecomp.Multiplayer.Net;

namespace MphRecomp.FirstHunt
{
    // First Hunt's deathmatch lobbies (docs/FIRST_HUNT_PORT.md P5; fh_research p5_multiplayer.md 1) over our LAN session in
    // place of the cart's DS wireless: its own ROM key, so only First Hunt players see and join them; Samus only, no bots,
    // 2-4 players, the three arenas, no rule options (the clock is the level's). LanSession.Current, as the vanilla
    // multiplayer menu's: the match screen plays it, and closes it when the match is over (BYE).
    public static class FhLobby
    {
        public const string RomKey = "FIRST HUNT";
        public const int MaxPlayers = 4;

        // the host's arena hexes 1-3 -> levels (0x207a1e4 = {0, 1, 2})
        public static readonly string[] Arenas = { "Level MP1", "Level MP2", "Level MP3" };

        public static int ArenaIndex(string room) => Array.IndexOf(Arenas, room);

        public static string ArenaName(int arena)
        {
            if (arena < 0 || arena >= Arenas.Length) return "";
            (RoomMetadata? room, _) = Metadata.GetRoomByName(Arenas[arena]);
            return room?.InGameName ?? Arenas[arena];
        }

        public static MatchSettings Template(int arena, string name)
        {
            MatchSettings s = MatchSettings.Quick(GameMode.Battle, Arenas[Math.Clamp(arena, 0, Arenas.Length - 1)], Hunter.Samus, bots: 0);
            s.Players[0].Nickname = name;
            return s;
        }

        public static LanSession? Lan => LanSession.Current;

        // the lobby's session ends (BYE to the other devices)
        public static void End()
        {
            LanSession? lan = LanSession.Current;
            if (lan != null)
            {
                lan.Dispose();
                LanSession.Current = null;
            }
        }

        public static LanSession Host(int arena, string name)
        {
            End();
            LanSession lan = LanSession.Host(Template(arena, name), RomKey, name, seatLimit: MaxPlayers);
            LanSession.Current = lan;
            return lan;
        }

        public static LanSession Browse()
        {
            End();
            LanSession lan = LanSession.Browse(RomKey);
            LanSession.Current = lan;
            return lan;
        }

        // the games on this network a player can join (First Hunt's, not started, not full)
        public static List<LanHostInfo> Games(LanSession lan) => lan.Hosts
            .Where(h => h.RomKey == RomKey && !h.Running && h.Seats < Math.Min(h.MaxSeats, MaxPlayers)).ToList();

        // host: the players in the lobby (this one, then the joiners as they came)
        public static List<string> HostSeats(LanSession lan, string name)
        {
            var names = new List<string> { name };
            foreach (LanJoinerInfo j in lan.Joiners)
            {
                if (!j.Left && names.Count < MaxPlayers) names.Add(j.Player.Nickname ?? "");
            }
            return names;
        }
    }

    // A finished deathmatch, from the game screen to the menus' results page (<saves>/firsthunt_last.txt, as a training
    // game's result): per player in seat order, the name, kills (of others), deaths (all), score, rank, and the kill table
    public sealed class FhMatchResult
    {
        public int LocalSlot { get; init; }
        public bool Host { get; init; }
        public string[] Names { get; init; } = Array.Empty<string>();
        public int[] Kills { get; init; } = Array.Empty<int>();
        public int[] Deaths { get; init; } = Array.Empty<int>();
        public int[] Points { get; init; } = Array.Empty<int>();
        public int[] Ranks { get; init; } = Array.Empty<int>();
        public int[,] KillTable { get; init; } = new int[FhLobby.MaxPlayers, FhLobby.MaxPlayers];

        public int Players => Names.Length;
        public int LocalRank => LocalSlot >= 0 && LocalSlot < Ranks.Length ? Ranks[LocalSlot] : 0;

        // MphRead's match as it stands (the match is over): players = the match's seats
        public static FhMatchResult Capture(int players, int localSlot, bool host)
        {
            players = Math.Clamp(players, 1, FhLobby.MaxPlayers);
            var names = new string[players];
            int[] kills = new int[players], deaths = new int[players], points = new int[players], ranks = new int[players];
            var table = new int[FhLobby.MaxPlayers, FhLobby.MaxPlayers];
            for (int i = 0; i < players; i++)
            {
                names[i] = GameState.Nicknames[i] ?? "";
                kills[i] = GameState.Kills[i];
                deaths[i] = GameState.Deaths[i];
                points[i] = GameState.Points[i];
                for (int j = 0; j < players; j++) table[i, j] = FirstHuntMission.KillTable[i, j];
            }
            // rank = how many players score more (0x2030b6c), over this match's players
            for (int i = 0; i < players; i++)
            {
                for (int j = 0; j < players; j++)
                {
                    if (points[j] > points[i]) ranks[i]++;
                }
            }
            return new FhMatchResult
            {
                LocalSlot = localSlot, Host = host, Names = names, Kills = kills, Deaths = deaths, Points = points, Ranks = ranks,
                KillTable = table
            };
        }

        // one line: "mp <local> <host> <n>" then per player "name kills deaths points rank" (name escaped), then the table
        public string ToLine()
        {
            var parts = new List<string> { "mp", LocalSlot.ToString(CultureInfo.InvariantCulture), Host ? "1" : "0", Players.ToString(CultureInfo.InvariantCulture) };
            for (int i = 0; i < Players; i++)
            {
                parts.Add(Uri.EscapeDataString(Names[i]));
                parts.Add(Kills[i].ToString(CultureInfo.InvariantCulture));
                parts.Add(Deaths[i].ToString(CultureInfo.InvariantCulture));
                parts.Add(Points[i].ToString(CultureInfo.InvariantCulture));
                parts.Add(Ranks[i].ToString(CultureInfo.InvariantCulture));
            }
            for (int i = 0; i < Players; i++)
            {
                for (int j = 0; j < Players; j++) parts.Add(KillTable[i, j].ToString(CultureInfo.InvariantCulture));
            }
            return String.Join(' ', parts);
        }

        public static bool IsMatchLine(string line) => line.TrimStart().StartsWith("mp ", StringComparison.Ordinal);

        public static FhMatchResult? Parse(string line)
        {
            string[] p = line.Trim().Split(' ');
            if (p.Length < 4 || p[0] != "mp") return null;
            if (!Int32.TryParse(p[1], out int local) || !Int32.TryParse(p[3], out int n) || n < 1 || n > FhLobby.MaxPlayers
                || p.Length != 4 + n * 5 + n * n)
            {
                return null;
            }
            var names = new string[n];
            int[] kills = new int[n], deaths = new int[n], points = new int[n], ranks = new int[n];
            var table = new int[FhLobby.MaxPlayers, FhLobby.MaxPlayers];
            int at = 4;
            for (int i = 0; i < n; i++)
            {
                names[i] = Uri.UnescapeDataString(p[at++]);
                if (!Int32.TryParse(p[at++], out kills[i]) || !Int32.TryParse(p[at++], out deaths[i])
                    || !Int32.TryParse(p[at++], out points[i]) || !Int32.TryParse(p[at++], out ranks[i]))
                {
                    return null;
                }
            }
            for (int i = 0; i < n; i++)
            {
                for (int j = 0; j < n; j++)
                {
                    if (!Int32.TryParse(p[at++], out table[i, j])) return null;
                }
            }
            return new FhMatchResult
            {
                LocalSlot = Math.Clamp(local, 0, n - 1), Host = p[2] == "1", Names = names, Kills = kills, Deaths = deaths, Points = points,
                Ranks = ranks, KillTable = table
            };
        }
    }
}
