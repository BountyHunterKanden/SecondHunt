using System.Collections.Generic;
using System.Linq;
using MphRecomp.Online;

namespace SecondHunt.Server
{
    // Parties (docs/ONLINE_PLAY.md section 6): friends group up before a game. The leader invites friends and rivals;
    // when the leader hosts or joins a game the others are told (ev_party_game) and may join it even when it isn't
    // listed for them. A leader who leaves hands the party to the next member; the last one out ends it. In memory,
    // under the games lock (joining a game looks at parties).
    public sealed partial class Hub
    {
        // 8 once P8 lands (docs/ONLINE_PLAY.md section 10)
        public const int MaxPartySize = 4;

        private readonly Dictionary<string, Party> _parties = new();
        private readonly Dictionary<string, Party> _partyOf = new();

        private sealed class Party
        {
            public string Id { get; }
            // Members[0] leads
            public List<string> Members { get; } = new();
            public HashSet<string> Invited { get; } = new();

            public Party(string id, string leader)
            {
                Id = id;
                Members.Add(leader);
            }

            public string Leader => Members[0];
        }

        private PartyInfo PartyInfo(Party p, string viewer) => new()
        {
            Id = p.Id,
            Leader = p.Leader,
            Members = Cards(p.Members, viewer),
        };

        private void PushParty(Party p, string? except = null)
        {
            foreach (string m in p.Members.Where(m => m != except))
            {
                Push(m, new PartyEvent { Party = PartyInfo(p, m) });
            }
        }

        // the viewer's party leader is in this game (a party member may follow them in)
        private bool LeaderIn(Game g, string viewer)
            => _partyOf.TryGetValue(viewer, out Party? p) && p.Leader != viewer && g.Has(p.Leader);

        // the leader hosted or joined a game: tell the others
        private void PartyFollow(string me, Game g)
        {
            if (_partyOf.TryGetValue(me, out Party? p) && p.Leader == me)
            {
                foreach (string m in p.Members.Where(m => m != me && !g.Has(m)))
                {
                    Push(m, new PartyGameEvent { GameId = g.Id });
                }
            }
        }

        private OkMsg PartyInvite(string me, string code)
        {
            lock (_gamesLock)
            {
                if (code == me)
                {
                    throw new OnlineException("self", "that's your own code");
                }
                bool linked = _store.HasLink(me, code, LinkKind.Friend) || _store.HasLink(me, code, LinkKind.Rival);
                if (!linked || _store.Blocked(me, code))
                {
                    throw new OnlineException("not_friend", "you can invite your friends and rivals");
                }
                if (!_partyOf.TryGetValue(me, out Party? p))
                {
                    p = new Party(NewId(_parties.ContainsKey), me);
                    _parties[p.Id] = p;
                    _partyOf[me] = p;
                    PushParty(p);
                }
                if (p.Leader != me)
                {
                    throw new OnlineException("not_leader", "only the party leader can invite");
                }
                if (p.Members.Contains(code))
                {
                    return new OkMsg { Done = true };
                }
                if (p.Members.Count >= MaxPartySize)
                {
                    throw new OnlineException("full", "the party is full");
                }
                p.Invited.Add(code);
                Push(code, new PartyInviteEvent { From = Card(_store.Get(me)!, code), PartyId = p.Id });
                return new OkMsg { Done = _online.ContainsKey(code) };
            }
        }

        private PartyMsg PartyJoin(string me, string id)
        {
            lock (_gamesLock)
            {
                if (!_parties.TryGetValue(id, out Party? p) || (!p.Invited.Contains(me) && !p.Members.Contains(me)))
                {
                    throw new OnlineException("not_found", "that party isn't open to you");
                }
                if (p.Members.Contains(me))
                {
                    return new PartyMsg { Party = PartyInfo(p, me) };
                }
                if (p.Members.Count >= MaxPartySize)
                {
                    throw new OnlineException("full", "the party is full");
                }
                PartyLeave(me);
                p.Invited.Remove(me);
                p.Members.Add(me);
                _partyOf[me] = p;
                PushParty(p, except: me);
                // the leader is already in a lobby: follow them
                if (_gameOf.TryGetValue(p.Leader, out Game? g) && !g.Has(me))
                {
                    Push(me, new PartyGameEvent { GameId = g.Id });
                }
                return new PartyMsg { Party = PartyInfo(p, me) };
            }
        }

        // false = wasn't in a party
        private bool PartyLeave(string me)
        {
            lock (_gamesLock)
            {
                if (!_partyOf.Remove(me, out Party? p))
                {
                    return false;
                }
                FindPartyLeft(me); // (a search was the whole party's)
                p.Members.Remove(me);
                Push(me, new PartyEvent { Party = null });
                if (p.Members.Count == 0)
                {
                    _parties.Remove(p.Id);
                }
                else
                {
                    PushParty(p); // a new leader when the old one left
                }
                return true;
            }
        }
    }
}
