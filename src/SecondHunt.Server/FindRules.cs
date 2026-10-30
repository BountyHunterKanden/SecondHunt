using System;
using System.Collections.Generic;
using System.Linq;
using MphRecomp.Online;

namespace SecondHunt.Server
{
    // Find Game's rules as plain functions (docs/ONLINE_PLAY.md section 6, decision 15: no rank): what a search accepts, how
    // the REGION limit widens with the wait, who can be in one match, who hosts and how the arena vote counts. The hub
    // (HubFind.cs) only feeds these; the self-test calls them with made-up clocks and tickets.
    public sealed class FindConfig
    {
        // a match holds up to this many players (8: P8, owner 2026-10-11 "8 players in all modes"; was vanilla's 4. Parties
        // stay at Hub.MaxPartySize)
        public int MaxPlayers { get; set; } = 8;
        public int MinPlayers { get; set; } = 2;
        // a party this big (Hub.MaxPartySize) may play by itself once its search has waited FillSeconds (at 4 players a match
        // it was full at once; P8 made a match 8)
        public int FullParty { get; set; } = 4;
        // a group short of MaxPlayers forms once its oldest search has waited this long
        public double FillSeconds { get; set; } = 15;
        // NEARBY: two players may match while the sum of their round trips to the server (their relayed latency) is at most
        // NearbyBaseMs; it grows by NearbyStepMs every NearbyStepSeconds of the longer wait, and after NearbyOpenSeconds
        // there's no limit (the search has become WORLDWIDE)
        public int NearbyBaseMs { get; set; } = 120;
        public int NearbyStepMs { get; set; } = 40;
        public double NearbyStepSeconds { get; set; } = 10;
        public double NearbyOpenSeconds { get; set; } = 60;
        // a player whose device hasn't reported its ping
        public int DefaultPingMs { get; set; } = 80;
        // the arenas offered on the vote page, the vote's length, how long the chosen host has to open the game
        public int VoteOptions { get; set; } = 4;
        public double VoteSeconds { get; set; } = 15;
        public double HostSeconds { get; set; } = 20;
        // how often the queue is looked at and the searchers are told how it's going
        public int TickMs { get; set; } = 500;
        public double ProgressSeconds { get; set; } = 2;

        // the rules at test speed (self-test, MphRead.Tools -findgametest): a wait of a second or two instead of tens
        public static FindConfig Fast => new()
        {
            FillSeconds = 2, NearbyStepSeconds = 1, NearbyOpenSeconds = 6, VoteSeconds = 4, HostSeconds = 4, TickMs = 100,
            ProgressSeconds = 0.5,
        };
    }

    // one search: a player, or a whole party that queued together
    public sealed class FindTicket
    {
        public string Id { get; init; } = "";
        // the party leader first; a lone player is the only member
        public List<string> Members { get; init; } = new();
        public HashSet<string> Modes { get; init; } = new();
        public bool Nearby { get; init; } = true;
        public string Version { get; init; } = "";
        // mode -> the arenas this search's players all have (a mode with no entry: any arena)
        public Dictionary<string, HashSet<string>> Arenas { get; init; } = new();
        public DateTime Since { get; set; }
        // when the searchers last heard how it's going
        public DateTime Reported { get; set; }
        // the worst round trip of its members
        public int PingMs { get; set; }
        // ONLINE SETTINGS' filters (FindStartReq.NoExtraPickups / NoExtraCharacters, 0.6.0)
        public bool NoExtraPickups { get; init; }
        public bool NoExtraCharacters { get; init; }
        public int Size => Members.Count;
    }

    public static class FindRules
    {
        public static double Waited(FindTicket t, DateTime now) => Math.Max(0, (now - t.Since).TotalSeconds);

        // the route limit in ms (sum of two round trips) a search accepts after waiting `seconds`; int.MaxValue = none
        public static int Limit(FindConfig c, bool nearby, double seconds)
        {
            if (!nearby || seconds >= c.NearbyOpenSeconds)
            {
                return int.MaxValue;
            }
            return c.NearbyBaseMs + c.NearbyStepMs * (int)Math.Floor(seconds / c.NearbyStepSeconds);
        }

        public static int Limit(FindConfig c, FindTicket t, DateTime now) => Limit(c, t.Nearby, Waited(t, now));

        // two searches fit the REGION rule: the sum of their pings is within both limits (each by its own wait)
        public static bool PingOk(FindConfig c, FindTicket a, FindTicket b, DateTime now)
            => (long)a.PingMs + b.PingMs <= Math.Min(Limit(c, a, now), Limit(c, b, now));

        // the same for a hosted game's host (their ping) and a search
        public static bool PingOk(FindConfig c, FindTicket t, int hostPingMs, DateTime now)
            => (long)t.PingMs + hostPingMs <= Limit(c, t, now);

        // the modes (with their common arenas) a group of searches could play: every search accepts the mode and the
        // arenas all of them have are not none. Arenas: mode -> the common names (null = nobody listed any, any arena).
        public static Dictionary<string, List<string>?> CommonModes(IEnumerable<FindTicket> tickets)
        {
            var result = new Dictionary<string, List<string>?>();
            List<FindTicket> list = tickets.ToList();
            foreach (string mode in FindMode.All)
            {
                if (!list.All(t => t.Modes.Contains(mode)))
                {
                    continue;
                }
                HashSet<string>? common = null;
                foreach (FindTicket t in list)
                {
                    if (t.Arenas.TryGetValue(mode, out HashSet<string>? own))
                    {
                        common = common == null ? new HashSet<string>(own) : new HashSet<string>(common.Intersect(own));
                    }
                }
                if (common is { Count: 0 })
                {
                    continue; // nothing they could all play
                }
                result[mode] = common?.OrderBy(a => a, StringComparer.Ordinal).ToList();
            }
            return result;
        }

        // one group out of the searches, oldest first: the oldest one that can form a match now. A match forms at MaxPlayers
        // at once (a full party included: it needs nobody else), or, with at least MinPlayers from at least two searches (Find
        // Game finds other players), once its oldest search has waited FillSeconds. `blocked(a, b)` = a block either
        // way between two players. Returns the group's tickets (oldest first) or null.
        public static List<FindTicket>? Form(FindConfig c, IReadOnlyList<FindTicket> queue, DateTime now, Func<string, string, bool> blocked)
        {
            List<FindTicket> ordered = queue.OrderBy(t => t.Since).ToList();
            foreach (FindTicket anchor in ordered)
            {
                var group = new List<FindTicket> { anchor };
                int size = anchor.Size;
                foreach (FindTicket t in ordered)
                {
                    if (t == anchor || size + t.Size > c.MaxPlayers)
                    {
                        continue;
                    }
                    if (Fits(c, group, t, now, blocked))
                    {
                        group.Add(t);
                        size += t.Size;
                    }
                }
                bool full = size >= c.MaxPlayers;
                bool waited = Waited(group.OrderBy(g => g.Since).First(), now) >= c.FillSeconds;
                if (full || (size >= c.MinPlayers && (group.Count >= 2 || size >= c.FullParty) && waited))
                {
                    return group.OrderBy(g => g.Since).ToList();
                }
            }
            return null;
        }

        // may this search join the group: same game version, a mode and arena they could all play, nobody blocked either way,
        // and the REGION rule against each search in it
        public static bool Fits(FindConfig c, List<FindTicket> group, FindTicket t, DateTime now, Func<string, string, bool> blocked)
        {
            if (group.Any(g => g.Version != t.Version))
            {
                return false;
            }
            if (CommonModes(group.Append(t)).Count == 0)
            {
                return false;
            }
            if (group.Any(g => g.Members.Any(a => t.Members.Any(b => blocked(a, b)))))
            {
                return false;
            }
            return group.All(g => PingOk(c, g, t, now));
        }

        // an open public game a search could simply join instead of waiting (the old QUICK MATCH): same version, no mods, a
        // mode the search accepts, room for the whole search, nobody blocked, route within the search's limit now
        public static bool GameFits(FindConfig c, FindTicket t, string mode, string version, string mods, int seatsFree, int hostPingMs,
            DateTime now)
            => mods == GameInfo.NoMods && version == t.Version && t.Modes.Contains(FindMode.Base(mode)) && seatsFree >= t.Size
                && PingOk(c, t, hostPingMs, now);

        // ... and allows nothing the search filters out: a game with EXTRA PICKUPS (or EXTRA CHARACTERS) on isn't joined by a
        // search that doesn't want them (owner 2026-10-10); a game in an imported arena (GameInfo.ExtraArena, 0.7.0) only by a
        // search whose players all have that arena in the game's mode (their lists; none sent: they don't have it, N27/N29)
        public static bool ExtrasFit(FindTicket t, GameInfo game)
            => !(t.NoExtraPickups && game.ExtraPickups) && !(t.NoExtraCharacters && game.ExtraCharacters)
                && (!game.ExtraArena || t.Arenas.TryGetValue(FindMode.Base(game.Mode), out HashSet<string>? own) && own.Contains(game.Arena));

        // who hosts: the player with the best connection, meaning the lowest round trip (every other player reaches them
        // through the relay, so their latency is theirs plus the host's); a tie goes to the longer-waiting search, then the
        // earlier member. `players` are in search order.
        public static string PickHost(IReadOnlyList<string> players, Func<string, int> pingMs)
            => players.Select((p, i) => (P: p, I: i)).OrderBy(x => pingMs(x.P)).ThenBy(x => x.I).First().P;

        // VOTE FOR AN ARENA: the most votes wins, a tie is drawn at random, nobody voting is a random option; "" with no options
        public static string Tally(IReadOnlyList<string> options, IReadOnlyDictionary<string, string> votes, Random random)
        {
            if (options.Count == 0)
            {
                return "";
            }
            var counts = options.ToDictionary(o => o, o => votes.Values.Count(v => v == o));
            int best = counts.Values.Max();
            List<string> top = options.Where(o => counts[o] == best).ToList(); // (best 0 = everyone: nobody voted)
            return top[random.Next(top.Count)];
        }
    }
}
