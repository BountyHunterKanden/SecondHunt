using System;
using System.Collections.Generic;
using System.Linq;
using MphRecomp.Online;

namespace SecondHunt.Server
{
    // The Gaming Hub (server 0.10.0; Core Online/OnlineProtocol.Hub.cs, docs/GAMING_HUB.md): the old nintendowifi.com MPH
    // pages from the server's own counts. Counted from an online match's host report (HubRecords.MatchResult) and a served
    // match's match host (HubServed.ServedResult), once per match; a LAN match the server never saw doesn't count (vanilla's
    // pages were Wi-Fi only).
    public sealed partial class Hub
    {
        public const int HubRecentMinutes = 15, HubRecentMax = 20;
        // a match's numbers past these are a broken or doctored report: clamped
        private const int MaxMatchKills = 999, MaxMatchSeconds = 4 * 3600, MaxWeapons = 24;
        // shots: one a 60 Hz frame at most (the Shock Coil fires every frame) for the longest match
        private const int MaxMatchShots = MaxMatchSeconds * 60;
        // kills (and headshots, alt-form kills, streaks, weapon kills: all within kills) also go by how long the server saw the
        // match run, from its StartGame: a doctored report gains only what that time could honestly give (an 8-player stomp
        // tops out near a kill every 5 s; the slack covers a short match's burst and the loading before play)
        private const int MaxKillsPerMinute = 12, KillSlack = 20;
        internal static int MatchKillCap(double minutes) =>
            (int)Math.Clamp(KillSlack + MaxKillsPerMinute * Math.Max(minutes, 0), KillSlack, MaxMatchKills);
        private readonly Random _hubRandom = new();

        // (rows already checked: codes known, distinct, places in range)
        private void CountHubStats(List<ResultRow> rows, bool teams, string? mode, string? arena, int seconds, DateTime started)
        {
            if (rows.Count < 2)
            {
                return;
            }
            int maxKills = MatchKillCap((DateTime.UtcNow - started).TotalMinutes);
            var lines = rows.Select(x => new Store.StatsLine(x.Code, x.Place, x.Team, Clean(x.Stats, maxKills))).ToList();
            _store.AddMatchStats(lines, teams, ContentKeys.Valid(mode) ? mode : null, ContentKeys.Valid(arena) ? arena : null,
                Math.Clamp(seconds, 0, MaxMatchSeconds));
        }

        private static ResultStats? Clean(ResultStats? s, int maxKills)
        {
            if (s == null)
            {
                return null;
            }
            int Kills(int n) => Math.Clamp(n, 0, maxKills);
            var weapons = new Dictionary<string, int>();
            foreach ((string key, int kills) in s.Weapons ?? new())
            {
                if (weapons.Count < MaxWeapons && ContentKeys.Valid(key) && kills > 0)
                {
                    weapons[key] = Kills(kills);
                }
            }
            int all = Kills(s.Kills);
            return new ResultStats
            {
                Hunter = ContentKeys.Valid(s.Hunter) ? s.Hunter : "",
                Kills = all,
                Deaths = Kills(s.Deaths),
                Headshots = Math.Min(Kills(s.Headshots), all),
                AltKills = Math.Min(Kills(s.AltKills), all),
                BestStreak = Math.Min(Kills(s.BestStreak), all),
                Shots = Math.Clamp(s.Shots, 0, MaxMatchShots),
                Weapons = weapons,
            };
        }

        private LeaderRow Leader(string code, int place, long value)
        {
            Store.Player? p = _store.Get(code);
            return new LeaderRow { Place = place, Code = code, Name = p?.Name ?? "", Tag = p?.Tag ?? 0, Value = value,
                Rank = HubRules.Level((int)_store.Total(code, HubCategory.RankPoints)) };
        }

        private static List<ShareRow> Shares(Dictionary<string, long> sums, string cat)
        {
            long total = sums.Values.Sum();
            if (total <= 0)
            {
                return new();
            }
            return sums.Where(e => e.Value > 0)
                .OrderByDescending(e => e.Value).ThenBy(e => ContentKeys.Order(cat, e.Key)).ThenBy(e => e.Key, StringComparer.Ordinal)
                .Select(e => new ShareRow { Key = e.Key, Share = (int)(e.Value * 1000 / total) }).ToList();
        }

        private HubMsg HubPage(string me)
        {
            var msg = new HubMsg();
            foreach (string cat in HubCategory.All)
            {
                if (_store.Board(cat, 1).FirstOrDefault() is { Code: not null } top)
                {
                    msg.Leaders[cat] = Leader(top.Code, 1, top.Value);
                }
            }
            msg.Hunters = Shares(_store.StatSums(ContentKeys.Hunter), ContentKeys.Hunter);
            msg.Weapons = Shares(_store.StatSums(ContentKeys.Weapon), ContentKeys.Weapon);
            msg.Modes = Shares(_store.StatSums(ContentKeys.Mode), ContentKeys.Mode);
            msg.Arenas = Shares(_store.StatSums(ContentKeys.Arena), ContentKeys.Arena);
            // online lately: logged in in the last 15 minutes or still here; findable players only, shuffled
            long since = Store.UnixNow - HubRecentMinutes * 60;
            var recent = _online.Keys.Concat(_store.SeenSince(since, 200)).Distinct()
                .Where(c => c != me && !_store.Blocked(me, c)).ToList();
            foreach (string c in recent.OrderBy(_ => _hubRandom.Next()))
            {
                Store.Player? p = _store.Get(c);
                if (p == null || !p.Findable)
                {
                    continue;
                }
                long points = _store.BoardPlace(c, HubCategory.Points)?.Value ?? 0;
                msg.Recent.Add(new LeaderRow { Code = c, Name = p.Name, Tag = p.Tag, Value = points,
                    Rank = HubRules.Level((int)_store.Total(c, HubCategory.RankPoints)) });
                if (msg.Recent.Count == HubRecentMax)
                {
                    break;
                }
            }
            (long players, long matches) = _store.StatCounts();
            msg.Players = (int)players;
            msg.Matches = matches;
            return msg;
        }

        private LeadersMsg Leaders(string me, LeadersReq r)
        {
            if (!HubCategory.All.Contains(r.Category) || r.Scope is not (LeadersReq.All or LeadersReq.Friends or LeadersReq.Rivals))
            {
                throw new OnlineException("bad_board", "no such leaderboard");
            }
            List<string>? among = r.Scope == LeadersReq.All ? null
                : _store.Linked(me, r.Scope == LeadersReq.Friends ? LinkKind.Friend : LinkKind.Rival).Append(me).ToList();
            var msg = new LeadersMsg { Category = r.Category, Scope = r.Scope };
            int place = 0;
            long last = long.MinValue;
            List<(string Code, long Value)> board = _store.Board(r.Category, LeadersReq.Top, among);
            for (int i = 0; i < board.Count; i++)
            {
                if (board[i].Value != last)
                {
                    place = i + 1; // (ties share a place)
                    last = board[i].Value;
                }
                msg.Rows.Add(Leader(board[i].Code, place, board[i].Value));
            }
            if (msg.Rows.FirstOrDefault(x => x.Code == me) is LeaderRow mine)
            {
                msg.Mine = mine;
            }
            else if (r.Scope == LeadersReq.All && _store.BoardPlace(me, r.Category) is (int p, long v))
            {
                msg.Mine = Leader(me, p, v);
            }
            else if (among != null && _store.Board(r.Category, among.Count, among) is var all
                && all.FindIndex(x => x.Code == me) is int at and >= 0)
            {
                msg.Mine = Leader(me, all.Count(x => x.Value > all[at].Value) + 1, all[at].Value);
            }
            return msg;
        }

        private StatsMsg Stats(string me, StatsReq r)
        {
            string code = string.IsNullOrEmpty(r.Code) ? me : r.Code;
            Store.Player? p = _store.Get(code);
            if (p == null || code != me && _store.Blocked(me, code))
            {
                throw new OnlineException("not_found", "no such player");
            }
            Dictionary<string, Dictionary<string, long>> all = _store.PlayerStats(code);
            Dictionary<string, int> Ints(string cat) => all.TryGetValue(cat, out var d)
                ? d.ToDictionary(e => e.Key, e => (int)Math.Min(e.Value, int.MaxValue)) : new();
            var msg = new StatsMsg
            {
                Code = code,
                Name = p.Name,
                Tag = p.Tag,
                Totals = all.TryGetValue(Store.StatTotal, out var totals) ? totals : new(),
                Hunters = Ints(ContentKeys.Hunter),
                Weapons = Ints(ContentKeys.Weapon),
                Modes = Ints(ContentKeys.Mode),
                Arenas = Ints(ContentKeys.Arena),
            };
            foreach (string cat in HubCategory.All)
            {
                if (_store.BoardPlace(code, cat) is (int place, _))
                {
                    msg.Places[cat] = place;
                }
            }
            return msg;
        }
    }
}
