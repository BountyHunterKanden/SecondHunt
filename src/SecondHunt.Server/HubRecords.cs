using System;
using System.Collections.Generic;
using System.Linq;
using MphRecomp.Online;

namespace SecondHunt.Server
{
    // Head to head on the server (T3, 0.7.0; docs/ONLINE_PLAY.md section 5). Each device used to count its own record as it
    // reached RESULTS (by names matched to seats), so two players' licenses disagreed. Now the host reports a finished match's
    // places once (MatchResultReq) and every pair of human opponents in it moves on together; licenses read the record back
    // on the player card (PlayerCard.Record). An online match is checked against who was in its game when it started
    // (RoundStarted, from StartGame); a LAN match the server never saw takes the host's word, a few an hour, each once.
    public sealed partial class Hub
    {
        public const int MaxLanReportsPerHour = 20;
        private static readonly TimeSpan RoundKeep = TimeSpan.FromHours(6);

        // the newest started match of each game (under _gamesLock): who was in it, when, and whether it was reported
        private sealed class Round
        {
            public HashSet<string> Members { get; } = new();
            public DateTime At { get; } = DateTime.UtcNow;
            public bool Reported { get; set; }
            // a served game's match: the match host reports it (HubServed.cs), the players' reports aren't taken
            public bool Served { get; set; }
        }

        private readonly Dictionary<string, Round> _rounds = new();
        // LAN reports already counted (reporter + its MatchKey) and when, for the hourly cap and repeats (under _recordsLock)
        private readonly object _recordsLock = new();
        private readonly Dictionary<string, DateTime> _lanReports = new();

        // StartGame (under _gamesLock): a match of this game starts with these players
        private void RoundStarted(Game g)
        {
            DateTime old = DateTime.UtcNow - RoundKeep;
            foreach (string id in _rounds.Where(r => r.Value.At < old).Select(r => r.Key).ToList())
            {
                _rounds.Remove(id);
            }
            var round = new Round { Served = g.MatchHost != null };
            round.Members.UnionWith(g.Slots.Values);
            _rounds[g.Id] = round;
        }

        private OkMsg MatchResult(string me, MatchResultReq r)
        {
            List<ResultRow> rows = r.Rows ?? new List<ResultRow>();
            if (rows.Count < 2 || rows.Count > MatchResultReq.MaxRows || r.MatchKey.Length is 0 or > 64
                || rows.Select(x => x.Code).Distinct().Count() != rows.Count
                || rows.Any(x => x.Place is < 0 or >= MatchResultReq.MaxRows || x.Team is < -1 or > 1 || _store.Get(x.Code) == null))
            {
                throw new OnlineException("bad_result", "not a match result");
            }
            if (r.Lan)
            {
                if (rows.All(x => x.Code != me))
                {
                    throw new OnlineException("bad_result", "a LAN result names its reporter");
                }
                lock (_recordsLock)
                {
                    DateTime now = DateTime.UtcNow;
                    foreach (string k in _lanReports.Where(e => e.Value < now - TimeSpan.FromDays(1)).Select(e => e.Key).ToList())
                    {
                        _lanReports.Remove(k);
                    }
                    string key = me + "|" + r.MatchKey;
                    if (_lanReports.ContainsKey(key))
                    {
                        return new OkMsg { Done = false }; // (sent again from the device's queue: counted already)
                    }
                    if (_lanReports.Count(e => e.Key.StartsWith(me + "|", StringComparison.Ordinal) && e.Value > now - TimeSpan.FromHours(1))
                        >= MaxLanReportsPerHour)
                    {
                        throw new OnlineException("too_many", "too many LAN results this hour");
                    }
                    _lanReports[key] = now;
                }
            }
            else
            {
                lock (_gamesLock)
                {
                    if (!_rounds.TryGetValue(r.GameId ?? "", out Round? round) || !round.Members.Contains(me))
                    {
                        throw new OnlineException("not_in_game", "no such match of yours");
                    }
                    if (rows.Any(x => !round.Members.Contains(x.Code)))
                    {
                        throw new OnlineException("bad_result", "a player who wasn't in the match");
                    }
                    if (round.Reported || round.Served)
                    {
                        return new OkMsg { Done = false };
                    }
                    round.Reported = true;
                }
            }
            return new OkMsg { Done = CountHeadToHead(rows, r.Teams) > 0 };
        }

        // every pair of opponents in a finished match moves on together (teammates aren't opponents); the pairs counted
        private int CountHeadToHead(List<ResultRow> rows, bool teams)
        {
            int pairs = 0;
            for (int i = 0; i < rows.Count; i++)
            {
                for (int j = i + 1; j < rows.Count; j++)
                {
                    ResultRow a = rows[i], b = rows[j];
                    if (teams && a.Team >= 0 && a.Team == b.Team)
                    {
                        continue; // (teammates aren't opponents)
                    }
                    _store.AddHeadToHead(a.Code, b.Code, a.Place, b.Place);
                    pairs++;
                }
            }
            return pairs;
        }
    }
}
