using System;
using System.Collections.Generic;
using System.Linq;
using MphRecomp.Online;

namespace SecondHunt.Server
{
    // The Gaming Hub's numbers (server 0.10.0; docs/GAMING_HUB.md): one row per player, category and key. Category
    // "total" holds the boards' numbers (HubCategory keys: points, games, win_rate...) and the card's (wins, kills,
    // deaths, seconds, shots, win_run = the current run of wins); "hunter" / "mode" / "arena" count matches
    // per ContentKeys key, "weapon" kills. The "*" row holds the server's own count of matches.
    public sealed partial class Store
    {
        public const string StatTotal = "total";
        private const string AllPlayers = "*";

        private void CreateStatsTables()
        {
            Exec(@"CREATE TABLE IF NOT EXISTS stats(
                     code TEXT NOT NULL, cat TEXT NOT NULL, key TEXT NOT NULL, value INTEGER NOT NULL,
                     PRIMARY KEY(code, cat, key));
                   CREATE INDEX IF NOT EXISTS stats_board ON stats(cat, key, value);");
        }

        // one seat of a finished match, checked by the hub (HubStats.cs)
        public sealed record StatsLine(string Code, int Place, int Team, ResultStats? Stats);

        // a finished match: everyone's totals, favourites, star points (rank_points: vanilla's rank rule against every
        // opponent, from everyone's star points before the match) and Overall Points (HubRules.Overall of the new totals)
        // in one transaction
        public void AddMatchStats(IReadOnlyList<StatsLine> lines, bool teams, string? mode, string? arena, int seconds)
        {
            lock (_lock)
            {
                using var tx = _db.BeginTransaction();
                var before = lines.ToDictionary(l => l.Code, l => (int)Stat(l.Code, StatTotal, HubCategory.RankPoints));
                foreach (StatsLine l in lines)
                {
                    bool won = l.Place == 0;
                    ResultStats s = l.Stats ?? new ResultStats();
                    void Add(string cat, string key, long n)
                    {
                        if (n != 0 && key.Length > 0)
                        {
                            Exec(@"INSERT INTO stats(code, cat, key, value) VALUES($c, $t, $k, $n)
                                   ON CONFLICT(code, cat, key) DO UPDATE SET value = value + $n",
                                ("$c", l.Code), ("$t", cat), ("$k", key), ("$n", n));
                        }
                    }
                    void Set(string key, long n) => Exec(@"INSERT INTO stats(code, cat, key, value) VALUES($c, $t, $k, $n)
                                   ON CONFLICT(code, cat, key) DO UPDATE SET value = $n",
                        ("$c", l.Code), ("$t", StatTotal), ("$k", key), ("$n", n));
                    void Max(string key, long n)
                    {
                        if (n > 0)
                        {
                            Exec(@"INSERT INTO stats(code, cat, key, value) VALUES($c, $t, $k, $n)
                                   ON CONFLICT(code, cat, key) DO UPDATE SET value = MAX(value, $n)",
                                ("$c", l.Code), ("$t", StatTotal), ("$k", key), ("$n", n));
                        }
                    }
                    Add(StatTotal, HubCategory.Games, 1);
                    Add(StatTotal, Wins, won ? 1 : 0);
                    Add(StatTotal, Kills, s.Kills);
                    Add(StatTotal, Deaths, s.Deaths);
                    Add(StatTotal, HubCategory.Headshots, s.Headshots);
                    Add(StatTotal, HubCategory.AltKills, s.AltKills);
                    Add(StatTotal, HubCategory.BipedKills, Math.Max(s.Kills - s.AltKills, 0));
                    Add(StatTotal, Seconds, seconds);
                    Add(StatTotal, Shots, s.Shots);
                    Max(HubCategory.KillStreak, s.BestStreak);
                    long run = won ? Stat(l.Code, StatTotal, WinRun) + 1 : 0;
                    Set(WinRun, run);
                    Max(HubCategory.WinStreak, run);
                    long games = Stat(l.Code, StatTotal, HubCategory.Games), wins = Stat(l.Code, StatTotal, Wins);
                    long rate = games >= HubCategory.WinRateMinGames ? wins * 1000 / games : 0;
                    if (games >= HubCategory.WinRateMinGames)
                    {
                        Set(HubCategory.WinRate, rate);
                    }
                    int stars = HubRules.After(before[l.Code], l.Place, l.Team, teams,
                        lines.Where(o => o.Code != l.Code).Select(o => (before[o.Code], o.Place, o.Team)));
                    Set(HubCategory.RankPoints, stars);
                    Set(HubCategory.Points, HubRules.Overall(games, wins, Stat(l.Code, StatTotal, Kills),
                        Stat(l.Code, StatTotal, HubCategory.Headshots), rate));
                    Add(ContentKeys.Hunter, s.Hunter, 1);
                    Add(ContentKeys.Mode, mode ?? "", 1);
                    Add(ContentKeys.Arena, arena ?? "", 1);
                    foreach ((string weapon, int kills) in s.Weapons)
                    {
                        Add(ContentKeys.Weapon, weapon, kills);
                    }
                }
                Exec(@"INSERT INTO stats(code, cat, key, value) VALUES($c, $t, 'matches', 1)
                       ON CONFLICT(code, cat, key) DO UPDATE SET value = value + 1", ("$c", AllPlayers), ("$t", StatTotal));
                tx.Commit();
            }
        }

        public const string Wins = "wins", Kills = "kills", Deaths = "deaths", Seconds = "seconds", WinRun = "win_run",
            Shots = "shots";

        // one total of a player's (0 without one)
        public long Total(string code, string key)
        {
            lock (_lock)
            {
                return Stat(code, StatTotal, key);
            }
        }

        // (under _lock)
        private long Stat(string code, string cat, string key)
        {
            using var cmd = Command("SELECT value FROM stats WHERE code = $c AND cat = $t AND key = $k",
                new (string, object?)[] { ("$c", code), ("$t", cat), ("$k", key) });
            return cmd.ExecuteScalar() is long v ? v : 0;
        }

        // a board: the top players by a total (among = only these codes; null = everyone), ties by code
        public List<(string Code, long Value)> Board(string key, int top, IReadOnlyCollection<string>? among = null)
        {
            lock (_lock)
            {
                var args = new List<(string, object?)> { ("$k", key), ("$l", top), ("$t", StatTotal) };
                string filter = "";
                if (among != null)
                {
                    if (among.Count == 0)
                    {
                        return new();
                    }
                    filter = " AND code IN (" + string.Join(", ", among.Select((c, i) => "$c" + i)) + ")";
                    args.AddRange(among.Select((c, i) => ("$c" + i, (object?)c)));
                }
                using var cmd = Command($"SELECT code, value FROM stats WHERE cat = $t AND key = $k AND code != '*'{filter} "
                    + "ORDER BY value DESC, code LIMIT $l", args.ToArray());
                using var r = cmd.ExecuteReader();
                var list = new List<(string, long)>();
                while (r.Read())
                {
                    list.Add((r.GetString(0), r.GetInt64(1)));
                }
                return list;
            }
        }

        // a player's place on a board among everyone (1 = top; ties share a place), null without a number
        public (int Place, long Value)? BoardPlace(string code, string key)
        {
            lock (_lock)
            {
                using var cmd = Command("SELECT value FROM stats WHERE code = $c AND cat = $t AND key = $k",
                    new (string, object?)[] { ("$c", code), ("$t", StatTotal), ("$k", key) });
                if (cmd.ExecuteScalar() is not long value)
                {
                    return null;
                }
                using var above = Command("SELECT COUNT(*) FROM stats WHERE cat = $t AND key = $k AND code != '*' AND value > $v",
                    new (string, object?)[] { ("$t", StatTotal), ("$k", key), ("$v", value) });
                return ((int)(long)above.ExecuteScalar()! + 1, value);
            }
        }

        // everyone's sum per key of a category (hunter / weapon / mode / arena)
        public Dictionary<string, long> StatSums(string cat)
        {
            lock (_lock)
            {
                using var cmd = Command("SELECT key, SUM(value) FROM stats WHERE cat = $t AND code != '*' GROUP BY key",
                    new (string, object?)[] { ("$t", cat) });
                using var r = cmd.ExecuteReader();
                var sums = new Dictionary<string, long>();
                while (r.Read())
                {
                    sums[r.GetString(0)] = r.GetInt64(1);
                }
                return sums;
            }
        }

        // one player's rows: cat -> key -> value
        public Dictionary<string, Dictionary<string, long>> PlayerStats(string code)
        {
            lock (_lock)
            {
                using var cmd = Command("SELECT cat, key, value FROM stats WHERE code = $c", new (string, object?)[] { ("$c", code) });
                using var r = cmd.ExecuteReader();
                var all = new Dictionary<string, Dictionary<string, long>>();
                while (r.Read())
                {
                    string cat = r.GetString(0);
                    if (!all.TryGetValue(cat, out var keys))
                    {
                        all[cat] = keys = new Dictionary<string, long>();
                    }
                    keys[r.GetString(1)] = r.GetInt64(2);
                }
                return all;
            }
        }

        public (long Players, long Matches) StatCounts()
        {
            lock (_lock)
            {
                using var players = Command("SELECT COUNT(*) FROM stats WHERE cat = $t AND key = $k AND code != '*'",
                    new (string, object?)[] { ("$t", StatTotal), ("$k", HubCategory.Games) });
                long p = (long)players.ExecuteScalar()!;
                return (p, Stat(AllPlayers, StatTotal, "matches"));
            }
        }

        // findable players who logged in since then (the hub's "online lately")
        public List<string> SeenSince(long since, int limit)
        {
            lock (_lock)
            {
                using var cmd = Command("SELECT code FROM players WHERE seen >= $s AND findable = 1 ORDER BY seen DESC LIMIT $l",
                    new (string, object?)[] { ("$s", since), ("$l", limit) });
                using var r = cmd.ExecuteReader();
                var list = new List<string>();
                while (r.Read())
                {
                    list.Add(r.GetString(0));
                }
                return list;
            }
        }

        public static long UnixNow => Now;
    }
}
