using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using MphRecomp.Online;

namespace SecondHunt.Server
{
    // Find Game (O6, docs/ONLINE_PLAY.md section 6, decision 15: no rank): the queue, the match, VOTE FOR AN ARENA and the host.
    // A search is a player or a whole party (FindTicket). A timer looks at the queue every FindConfig.TickMs: a search that
    // fits an open public game just joins it (the old QUICK MATCH); otherwise FindRules.Form groups searches into a match. The
    // match's players vote for an arena, the server picks the host (the best connection) and tells that device to open the
    // game with HostGameReq.Find; the others are invited to it and told where to join. In memory under the games lock like
    // lobbies and parties (a restart ends searches).
    public sealed partial class Hub : IDisposable
    {
        private FindConfig _find = new();
        private Timer? _findTimer;
        private Random _findRandom = new();
        private readonly List<FindTicket> _queue = new();
        private readonly Dictionary<string, FindTicket> _ticketOf = new();
        private readonly Dictionary<string, FindMatch> _matches = new();
        private readonly Dictionary<string, FindMatch> _matchOf = new();
        private readonly ConcurrentDictionary<string, int> _pings = new();

        // a found match: voting until Host is set, then waiting for the host to open the game
        private sealed class FindMatch
        {
            public string Id { get; init; } = "";
            public List<FindTicket> Tickets { get; init; } = new();
            public string Mode { get; init; } = "";
            public string Version { get; init; } = "";
            public List<string> Options { get; init; } = new();
            public Dictionary<string, string> Votes { get; } = new();
            public DateTime VoteEnds { get; set; }
            public string? Host { get; set; }
            public string Arena { get; set; } = "";
            public DateTime HostEnds { get; set; }
            public List<string> Players => Tickets.SelectMany(t => t.Members).ToList();
            // the extra rules the match bans: any search's ONLINE SETTINGS filter (0.6.0)
            public bool NoExtraPickups => Tickets.Any(t => t.NoExtraPickups);
            public bool NoExtraCharacters => Tickets.Any(t => t.NoExtraPickups || t.NoExtraCharacters);
        }

        // test knobs (ServerApp.Build, --find-fast): the rules' timings and the dice
        public FindConfig Find
        {
            get => _find;
            set
            {
                _find = value;
                _findTimer?.Dispose();
                _findTimer = new Timer(_ => FindTickSafe(), null, value.TickMs, value.TickMs);
            }
        }

        public Random FindRandom
        {
            get => _findRandom;
            set => _findRandom = value;
        }

        public void Dispose()
        {
            _safetyTimer?.Dispose();
            _findTimer?.Dispose();
            _replayTimer?.Dispose(); // (HubReplays.cs)
            StopMatchHosts(); // (served games, HubServed.cs)
        }

        private static DateTime Now => DateTime.UtcNow;

        private int Ping(string code) => _pings.TryGetValue(code, out int ms) && ms > 0 ? ms : _find.DefaultPingMs;

        private OkMsg SetPing(string me, int ms)
        {
            _pings[me] = Math.Clamp(ms, 0, 5000);
            return new OkMsg { Done = true };
        }

        // ---- start, cancel, vote ----

        private OkMsg FindStart(string me, FindStartReq r)
        {
            lock (_gamesLock)
            {
                if (string.IsNullOrEmpty(r.Version))
                {
                    throw new OnlineException("version", "a search needs the game version");
                }
                if (r.Modes.Any(m => !FindMode.Valid(m)))
                {
                    throw new OnlineException("bad_mode", "not a game mode");
                }
                if (_gameOf.ContainsKey(me))
                {
                    throw new OnlineException("in_game", "leave your game first");
                }
                if (_ticketOf.ContainsKey(me) || _matchOf.ContainsKey(me))
                {
                    throw new OnlineException("searching", "you're already searching");
                }
                List<string> members = new() { me };
                if (_partyOf.TryGetValue(me, out Party? party))
                {
                    if (party.Leader != me)
                    {
                        throw new OnlineException("not_leader", "the party leader starts the search");
                    }
                    members = party.Members.ToList();
                    if (members.Any(m => m != me && (!_online.ContainsKey(m) || _gameOf.ContainsKey(m) || _ticketOf.ContainsKey(m)
                        || _matchOf.ContainsKey(m))))
                    {
                        throw new OnlineException("party_busy", "someone in your party is in a game or searching");
                    }
                }
                if (r.PingMs > 0)
                {
                    SetPing(me, r.PingMs);
                }
                var ticket = new FindTicket
                {
                    Id = NewId(id => _matches.ContainsKey(id) || _queue.Any(t => t.Id == id)),
                    Members = members,
                    Modes = (r.Modes.Count == 0 ? FindMode.All : r.Modes.ToArray()).ToHashSet(),
                    Nearby = r.Nearby,
                    Version = r.Version,
                    Since = Now,
                    PingMs = members.Max(Ping),
                    NoExtraPickups = r.NoExtraPickups,
                    NoExtraCharacters = r.NoExtraCharacters,
                };
                foreach ((string mode, List<string> arenas) in r.Arenas)
                {
                    if (FindMode.Valid(mode))
                    {
                        ticket.Arenas[mode] = arenas.Select(a => Cut(a)!).Take(64).ToHashSet();
                    }
                }
                if (TryBackfill(ticket))
                {
                    return new OkMsg { Done = true };
                }
                _queue.Add(ticket);
                foreach (string m in members)
                {
                    _ticketOf[m] = ticket;
                }
                ReportProgress(ticket, Now, force: true);
                FindTick(); // (two searches that already fit don't wait for the timer)
                return new OkMsg { Done = true };
            }
        }

        // false = wasn't searching
        private bool FindCancel(string me, string reason = "you")
        {
            lock (_gamesLock)
            {
                if (_ticketOf.TryGetValue(me, out FindTicket? t))
                {
                    DropTicket(t, reason, me);
                    return true;
                }
                if (_matchOf.TryGetValue(me, out FindMatch? m))
                {
                    LeaveMatch(m, m.Tickets.First(x => x.Members.Contains(me)), reason, me);
                    return true;
                }
                return false;
            }
        }

        private OkMsg FindVote(string me, string arena)
        {
            lock (_gamesLock)
            {
                if (!_matchOf.TryGetValue(me, out FindMatch? m) || m.Host != null)
                {
                    throw new OnlineException("no_vote", "there's no vote going on");
                }
                if (!m.Options.Contains(arena))
                {
                    throw new OnlineException("bad_arena", "that arena isn't on the ballot");
                }
                m.Votes[me] = arena;
                if (m.Votes.Count >= m.Players.Count)
                {
                    ResolveVote(m);
                }
                else
                {
                    foreach (string p in m.Players)
                    {
                        PushFind(p, VoteEvent(m, p));
                    }
                }
                return new OkMsg { Done = true };
            }
        }

        // ---- the queue ----

        private void FindTickSafe()
        {
            try
            {
                FindTick();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("find: tick failed: " + ex);
            }
        }

        // one look at the queue and the matches; the timer calls it, tests call it with their own timings
        public void FindTick()
        {
            lock (_gamesLock)
            {
                DateTime now = Now;
                foreach (FindMatch m in _matches.Values.ToList())
                {
                    if (m.Host == null && now >= m.VoteEnds)
                    {
                        ResolveVote(m);
                    }
                    else if (m.Host != null && now >= m.HostEnds)
                    {
                        HostTimedOut(m);
                    }
                }
                foreach (FindTicket t in _queue.OrderBy(t => t.Since).ToList())
                {
                    if (_queue.Contains(t) && TryBackfill(t))
                    {
                        _queue.Remove(t);
                        foreach (string p in t.Members)
                        {
                            _ticketOf.Remove(p);
                        }
                    }
                }
                List<FindTicket>? group;
                while ((group = FindRules.Form(_find, _queue, now, _store.Blocked)) != null)
                {
                    FormMatch(group);
                }
                foreach (FindTicket t in _queue)
                {
                    ReportProgress(t, now, force: false);
                }
            }
        }

        // a search that fits an open public game joins it: told the game, the ticket never queued
        private bool TryBackfill(FindTicket t)
        {
            DateTime now = Now;
            Game? best = null;
            foreach (Game g in _games.Values)
            {
                if (g.Started || g.Info.Public == false || t.Members.Any(m => g.Has(m) || !Visible(g, m)))
                {
                    continue;
                }
                if (!FindRules.GameFits(_find, t, g.Info.Mode, g.Info.Version, g.Info.Mods, g.Info.MaxPlayers - g.Slots.Count, Ping(g.Host), now)
                    || !FindRules.ExtrasFit(t, g.Info))
                {
                    continue;
                }
                if (best == null || g.Slots.Count > best.Slots.Count)
                {
                    best = g;
                }
            }
            if (best == null)
            {
                return false;
            }
            foreach (string m in t.Members)
            {
                PushFind(m, new FindEvent
                {
                    State = FindState.Game, GameId = best.Id, Mode = FindMode.Base(best.Info.Mode), Arena = best.Info.Arena,
                    HostCard = Card(_store.Get(best.Host)!, m),
                });
            }
            return true;
        }

        private void FormMatch(List<FindTicket> group)
        {
            DateTime now = Now;
            var modes = FindRules.CommonModes(group);
            string mode = modes.Keys.ElementAt(_findRandom.Next(modes.Count));
            List<string> options = new();
            if (modes[mode] is List<string> arenas)
            {
                options = arenas.OrderBy(_ => _findRandom.Next()).Take(_find.VoteOptions).ToList();
            }
            var match = new FindMatch
            {
                Id = NewId(id => _matches.ContainsKey(id)), Tickets = group, Mode = mode, Version = group[0].Version, Options = options,
                VoteEnds = now.AddSeconds(_find.VoteSeconds),
            };
            _matches[match.Id] = match;
            foreach (FindTicket t in group)
            {
                _queue.Remove(t);
                foreach (string p in t.Members)
                {
                    _ticketOf.Remove(p);
                    _matchOf[p] = match;
                }
            }
            if (options.Count == 0)
            {
                ResolveVote(match); // (nothing to vote on: the host picks)
                return;
            }
            foreach (string p in match.Players)
            {
                PushFind(p, VoteEvent(match, p));
            }
        }

        private FindEvent VoteEvent(FindMatch m, string viewer) => new()
        {
            State = FindState.Vote, FindId = m.Id, Mode = m.Mode, Players = Cards(m.Players, viewer), Arenas = m.Options,
            Seconds = Math.Max(1, (int)Math.Ceiling((m.VoteEnds - Now).TotalSeconds)), Votes = m.Votes.Count,
            Arena = m.Votes.TryGetValue(viewer, out string? own) ? own : "",
        };

        // the vote is over: the most votes (ties and no votes by chance), the best connection hosts
        private void ResolveVote(FindMatch m)
        {
            m.Arena = FindRules.Tally(m.Options, m.Votes, _findRandom);
            if (TryServeFind(m))
            {
                return; // (the server runs it: HubServedFind.cs)
            }
            m.Host = FindRules.PickHost(m.Players, Ping);
            m.HostEnds = Now.AddSeconds(_find.HostSeconds);
            PlayerCard HostCard(string viewer) => Card(_store.Get(m.Host)!, viewer);
            foreach (string p in m.Players)
            {
                PushFind(p, new FindEvent
                {
                    State = p == m.Host ? FindState.Host : FindState.Join, FindId = m.Id, Mode = m.Mode, Arena = m.Arena,
                    Players = Cards(m.Players, p), HostCard = HostCard(p), Votes = m.Votes.Count,
                    NoExtraPickups = m.NoExtraPickups, NoExtraCharacters = m.NoExtraCharacters,
                });
            }
        }

        // the chosen host never opened the game: their search ends, the others go back to the queue where they were
        private void HostTimedOut(FindMatch m)
        {
            FindTicket hostTicket = m.Tickets.First(t => t.Members.Contains(m.Host!));
            DisbandMatch(m, hostTicket, "host");
        }

        // a search leaves a match (cancelled, party left, offline): the rest go on while there are enough of them and, once the
        // host is chosen, the host is still there; else they're back in the queue
        private void LeaveMatch(FindMatch m, FindTicket gone, string reason, string? by = null)
        {
            if (m.Host != null && gone.Members.Contains(m.Host) || m.Players.Count - gone.Size < _find.MinPlayers)
            {
                DisbandMatch(m, gone, reason, by);
                return;
            }
            m.Tickets.Remove(gone);
            foreach (string p in gone.Members)
            {
                _matchOf.Remove(p);
                m.Votes.Remove(p);
                PushFind(p, new FindEvent { State = FindState.Cancelled, Reason = p == by || reason != "you" ? reason : "party" });
            }
            if (m.Host == null && m.Votes.Count >= m.Players.Count)
            {
                ResolveVote(m);
            }
        }

        // the match is off: `ended` (whoever caused it) is cancelled, everyone else queues again with the wait they had
        private void DisbandMatch(FindMatch m, FindTicket ended, string reason, string? by = null)
        {
            _matches.Remove(m.Id);
            foreach (FindTicket t in m.Tickets)
            {
                foreach (string p in t.Members)
                {
                    _matchOf.Remove(p);
                }
                if (t == ended)
                {
                    foreach (string p in t.Members)
                    {
                        PushFind(p, new FindEvent { State = FindState.Cancelled, Reason = p == by || reason != "you" ? reason : "party" });
                    }
                    continue;
                }
                _queue.Add(t);
                foreach (string p in t.Members)
                {
                    _ticketOf[p] = t;
                }
                ReportProgress(t, Now, force: true, "match");
            }
        }

        // a search that is in the queue ends (all its members hear it)
        private void DropTicket(FindTicket t, string reason, string? by = null)
        {
            _queue.Remove(t);
            foreach (string p in t.Members)
            {
                _ticketOf.Remove(p);
                // (the others of a party hear that it was the party, not why)
                PushFind(p, new FindEvent { State = FindState.Cancelled, Reason = p == by || reason != "you" ? reason : "party" });
            }
        }

        private void ReportProgress(FindTicket t, DateTime now, bool force, string reason = "")
        {
            if (!force && (now - t.Reported).TotalSeconds < _find.ProgressSeconds)
            {
                return;
            }
            t.Reported = now;
            int limit = FindRules.Limit(_find, t, now);
            foreach (string p in t.Members)
            {
                PushFind(p, new FindEvent
                {
                    State = FindState.Searching, Waited = (int)FindRules.Waited(t, now), Queued = _queue.Sum(q => q.Size),
                    LimitMs = limit == int.MaxValue ? 0 : limit, Nearby = t.Nearby, Reason = reason,
                });
            }
        }

        private void PushFind(string code, FindEvent e) => Push(code, e);

        // ---- the game the chosen host opens ----

        // HostGameReq.Find: the game is the match's: the mode, arena, version and seat count are the match's, only its players
        // may join (invited), and they're told where
        private GameInfo FindHostInfo(string me, string find, GameInfo asked, out FindMatch match)
        {
            if (!_matches.TryGetValue(find, out FindMatch? m) || m.Host != me)
            {
                throw new OnlineException("not_found", "that match isn't yours to host");
            }
            if (FindMode.Base(asked.Mode) != m.Mode)
            {
                throw new OnlineException("bad_mode", "the match is " + m.Mode);
            }
            if (m.Arena != "" && asked.Arena != m.Arena)
            {
                throw new OnlineException("bad_arena", "the match voted for " + m.Arena);
            }
            if (asked.Version != m.Version)
            {
                throw new OnlineException("version", "not the version the players searched with");
            }
            match = m;
            return new GameInfo
            {
                Mode = asked.Mode, Arena = asked.Arena, MaxPlayers = m.Players.Count, Friends = false, Rivals = false, Public = false,
                Version = m.Version, Rules = asked.Rules, Mods = GameInfo.NoMods,
                ExtraPickups = asked.ExtraPickups && !m.NoExtraPickups,
                ExtraCharacters = asked.ExtraPickups && asked.ExtraCharacters && !m.NoExtraPickups && !m.NoExtraCharacters,
            };
        }

        // the host's game exists: the other players of the match may join it and hear which
        private void FindGameOpened(FindMatch m, Game g)
        {
            _matches.Remove(m.Id);
            foreach (string p in m.Players)
            {
                _matchOf.Remove(p);
            }
            foreach (string p in m.Players.Where(p => p != g.Host))
            {
                g.Invited.Add(p);
            }
            foreach (string p in m.Players.Where(p => p != g.Host))
            {
                PushFind(p, new FindEvent
                {
                    State = FindState.Game, GameId = g.Id, FindId = m.Id, Mode = m.Mode, Arena = g.Info.Arena,
                    HostCard = Card(_store.Get(g.Host)!, p),
                });
            }
        }

        // this player does something that ends a search (their connection closed, they host or join another game, the party broke up)
        private void FindLeft(string me, string reason)
        {
            FindCancel(me, reason);
        }

        // a party member left the party: the party's search (it was the whole party's) ends for every member
        private void FindPartyLeft(string me)
        {
            lock (_gamesLock)
            {
                if (_ticketOf.TryGetValue(me, out FindTicket? t))
                {
                    DropTicket(t, "party");
                }
                else if (_matchOf.TryGetValue(me, out FindMatch? m))
                {
                    LeaveMatch(m, m.Tickets.First(x => x.Members.Contains(me)), "party");
                }
            }
        }
    }
}
