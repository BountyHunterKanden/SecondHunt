using System.Collections.Generic;

namespace MphRecomp.Online
{
    // The Gaming Hub (server 0.10.0; docs/GAMING_HUB.md): the old nintendowifi.com MPH pages, in game. The server counts
    // every online and served match from the host's results (MatchResultReq / the match host's !result, never a player's
    // own license), and answers three pages: the hub, a leaderboard, a player's stats card. Hunters, weapons, modes and
    // arenas are ContentKeys, so new content shows up without a protocol change. An older server drops a connection that
    // sends these (ServerAtLeast(SinceServer)).
    public static class HubCategory
    {
        public const string Points = "points", Games = "games", WinRate = "win_rate", WinStreak = "win_streak",
            KillStreak = "kill_streak", AltKills = "alt_kills", BipedKills = "biped_kills", Headshots = "headshots";

        // license totals that aren't boards (StatsMsg.Totals)
        public const string Wins = "wins", Kills = "kills", Deaths = "deaths", Seconds = "seconds", Shots = "shots";

        // the vanilla star points (HubRules.After, 0-850): the stars and rank titles, not a board (Overall Points are
        // HubRules.Overall; docs/GAMING_HUB.md section 3)
        public const string RankPoints = "rank_points";

        // the site's order
        public static readonly string[] All = { Points, Games, WinRate, WinStreak, KillStreak, AltKills, BipedKills, Headshots };

        public static string Display(string cat) => cat switch
        {
            Points => "OVERALL POINTS",
            Games => "GAMES PLAYED",
            WinRate => "WIN RATE",
            WinStreak => "CONSECUTIVE WINS",
            KillStreak => "CONSECUTIVE KILLS",
            AltKills => "ALT KILLS",
            BipedKills => "BIPED KILLS",
            Headshots => "HEADSHOTS",
            _ => cat.ToUpperInvariant(),
        };

        // win rate counts from this many games (a 1-0 record doesn't top the board)
        public const int WinRateMinGames = 10;
    }

    // a player's place on a board; Value = the category's number (win rate in tenths of a percent), Rank = their star
    // level 0-4 (from rank_points)
    public sealed class LeaderRow
    {
        public int Place { get; set; }
        public string Code { get; set; } = "";
        public string Name { get; set; } = "";
        public int Tag { get; set; }
        public long Value { get; set; }
        public int Rank { get; set; }
    }

    // a favourite across all players: Share in tenths of a percent of the category's total
    public sealed class ShareRow
    {
        public string Key { get; set; } = "";
        public int Share { get; set; }
    }

    // the hub page: each category's leader, everyone's favourites, players seen online lately (at most 20, shuffled)
    public sealed class HubReq : OnlineMsg
    {
        public const string SinceServer = "0.10.0";
    }

    public sealed class HubMsg : OnlineMsg
    {
        // category -> its leader (a category nobody has yet is left out)
        public Dictionary<string, LeaderRow> Leaders { get; set; } = new();
        public List<ShareRow> Hunters { get; set; } = new();
        public List<ShareRow> Weapons { get; set; } = new();
        public List<ShareRow> Modes { get; set; } = new();
        public List<ShareRow> Arenas { get; set; } = new();
        public List<LeaderRow> Recent { get; set; } = new();
        public int Players { get; set; }
        public long Matches { get; set; }
    }

    // a leaderboard: the top 10 of a category among everyone, or the asker and their friends / rivals
    public sealed class LeadersReq : OnlineMsg
    {
        public const string SinceServer = "0.10.0";
        public const string All = "all", Friends = "friends", Rivals = "rivals";
        public const int Top = 10;
        public string Category { get; set; } = HubCategory.Points;
        public string Scope { get; set; } = All;
    }

    public sealed class LeadersMsg : OnlineMsg
    {
        public string Category { get; set; } = "";
        public string Scope { get; set; } = "";
        public List<LeaderRow> Rows { get; set; } = new();
        // the asker's own place on this board (null without a record), shown under the top 10 when it isn't in them
        public LeaderRow? Mine { get; set; }
    }

    // a player's stats card (the Hunter License page): Code empty = the asker
    public sealed class StatsReq : OnlineMsg
    {
        public const string SinceServer = "0.10.0";
        public string Code { get; set; } = "";
    }

    public sealed class StatsMsg : OnlineMsg
    {
        public string Code { get; set; } = "";
        public string Name { get; set; } = "";
        public int Tag { get; set; }
        // HubCategory values (points, games, win_rate...) plus wins, kills, deaths, seconds
        public Dictionary<string, long> Totals { get; set; } = new();
        // ContentKeys key -> matches played (hunters, modes, arenas) or kills (weapons)
        public Dictionary<string, int> Hunters { get; set; } = new();
        public Dictionary<string, int> Weapons { get; set; } = new();
        public Dictionary<string, int> Modes { get; set; } = new();
        public Dictionary<string, int> Arenas { get; set; } = new();
        // the player's place on each board among everyone (category -> place, 1 = top; left out without a record)
        public Dictionary<string, int> Places { get; set; } = new();
    }
}
