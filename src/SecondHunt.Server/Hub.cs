using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using MphRecomp.Online;

namespace SecondHunt.Server
{
    // One WebSocket per player: the signed login, then requests and pushes (docs/ONLINE_PLAY.md sections 4-5, 13).
    public sealed partial class Hub
    {
        public const string ServerVersion = "0.8.0"; // 0.8.0 (docs/DEDICATED_SERVERS.md D3): served games (GameInfo.Served: the match runs in a SecondHuntMatchHost process here on relay slot 0, Host = the lobby's owner on slot 1; JoinGameReq.Served; MemberEvent.Owner; results from the match host); 0.7.2 (release 0.2.0 F1): a seat dropped for its silence gets its slot back when its device sends again (Relay.Returned, Hub.SeatReturned); 0.7.1 (release 0.2.0 F3): Find Game matches up to 8 (FindConfig.MaxPlayers; a full party of 4 plays alone after the fill wait); 0.2.0: report, delete_profile; 0.3.0: ping, Find Game; 0.4.0: rejoin, TURN; 0.5.0: chat + safety (filter, mutes, bans, report evidence), host migration; 0.6.0: games carry the extra rules (GameInfo.ExtraPickups / ExtraCharacters), Find Game filters them; 0.7.0 (release test fixes): held seats (WelcomeMsg.Held = the hub's REJOIN, FIND releases a held seat, a seat silent 90 s is dropped); T3: friend OR rival (a rival upgrades to a friend, rival links between friends removed), head to head on the server (match_result, PlayerCard.Record), typed-chat lock skipped for friends only; T2: GameInfo.ExtraArena (Find Game's backfill only into games in an imported arena the searchers all have)
        private const int MaxPendingRequests = 100;

        private readonly Store _store;
        private readonly ConcurrentDictionary<string, Session> _online = new();

        public Hub(Store store, Relay relay, string relayHost = "")
        {
            _store = store;
            _relay = relay;
            _relayHost = relayHost;
            _relay.Expired += RoomExpired;
            _relay.Returned = SeatReturned;
            StartSafetyTimer();
            StartSeatTimer();
        }

        public int OnlineCount => _online.Count;

        private sealed class Session
        {
            private readonly SemaphoreSlim _sendLock = new(1, 1);
            public WebSocket Ws { get; }
            public string Code { get; }
            public CancellationTokenSource Cts { get; }
            // the scrambled device ID from the login (memory only; written down only if this profile is banned)
            public string Device { get; }

            public Session(WebSocket ws, string code, CancellationTokenSource cts, string device = "")
            {
                Ws = ws;
                Code = code;
                Cts = cts;
                Device = device;
            }

            public async Task Send(OnlineMsg msg)
            {
                await _sendLock.WaitAsync(Cts.Token);
                try
                {
                    await OnlineProtocol.SendAsync(Ws, msg, Cts.Token);
                }
                finally
                {
                    _sendLock.Release();
                }
            }

            public async Task Push(OnlineMsg msg)
            {
                try
                {
                    await Send(msg);
                }
                catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or ObjectDisposedException)
                {
                }
            }

            private int _kicked;
            // set when this session's loop has ended (Run's finally)
            public TaskCompletionSource Ended { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

            public async Task Kick(string reason)
            {
                if (Interlocked.Exchange(ref _kicked, 1) != 0)
                {
                    return; // (a ban check and the player logging in elsewhere can both get here)
                }
                await Push(new KickedEvent { Reason = reason });
                try
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    // (a close frame's text may be 123 bytes at most: the full reason is in the event above)
                    await Ws.CloseOutputAsync(WebSocketCloseStatus.PolicyViolation, reason.Length > 40 ? "kicked" : reason, timeout.Token);
                }
                catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or ObjectDisposedException or ArgumentException)
                {
                }
                // the close frame is out: give the player's side a moment to read the event and answer before the socket is dropped
                // (cancelling at once could reset the connection with the event still in flight)
                await Task.WhenAny(Ended.Task, Task.Delay(TimeSpan.FromSeconds(2)));
                Cts.Cancel();
            }
        }

        public async Task Run(WebSocket ws, CancellationToken aborted)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(aborted);
            CancellationToken ct = cts.Token;
            byte[] nonce = RandomNumberGenerator.GetBytes(32);
            Store.Player me;
            string device = "";
            try
            {
                await OnlineProtocol.SendAsync(ws, new ChallengeMsg
                {
                    Nonce = Convert.ToBase64String(nonce),
                    Server = ServerVersion,
                    Proto = OnlineProtocol.Version,
                }, ct);
                OnlineMsg? first;
                using (var login = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    login.CancelAfter(TimeSpan.FromSeconds(10));
                    first = await OnlineProtocol.ReceiveAsync(ws, login.Token);
                }
                if (first is not HelloMsg hello)
                {
                    throw new OnlineException("bad_login", "expected hello");
                }
                if (hello.Proto != OnlineProtocol.Version)
                {
                    throw new OnlineException("old_version", $"this server speaks protocol {OnlineProtocol.Version}, "
                        + $"the game {hello.Proto}: update the game");
                }
                byte[] key, sig;
                try
                {
                    key = Convert.FromBase64String(hello.Key);
                    sig = Convert.FromBase64String(hello.Sig);
                }
                catch (FormatException)
                {
                    throw new OnlineException("bad_login", "bad key");
                }
                if (!OnlineIdentity.Verify(key, OnlineProtocol.LoginPayload(nonce), sig))
                {
                    throw new OnlineException("bad_signature", "login signature doesn't match the key");
                }
                device = CleanDevice(hello.Device);
                if (_store.ActiveBan(key) is Store.BanRow ban)
                {
                    _store.AddBanDevice(ban.Code, device); // a banned profile's device is banned with it
                    throw new OnlineException("banned", BanText(ban));
                }
                if (_store.ActiveDeviceBan(device) is Store.BanRow deviceBan)
                {
                    throw new OnlineException("banned", BanText(deviceBan, device: true)); // a new profile on a banned device
                }
                me = _store.Login(key, hello.Name);
            }
            catch (OnlineException ex)
            {
                await Refuse(ws, ex, ct);
                return;
            }
            catch (Exception ex) when (ex is WebSocketException or OperationCanceledException)
            {
                return;
            }

            var session = new Session(ws, me.Code, cts, device);
            if (_online.TryGetValue(me.Code, out Session? old))
            {
                await old.Kick("logged in on another device");
            }
            _online[me.Code] = session;
            try
            {
                await session.Send(new WelcomeMsg
                {
                    Me = Card(me, me.Code),
                    Findable = me.Findable,
                    ChatLeft = Math.Max(0, ChatUnlockMatches - _store.Matches(me.Code)),
                    ChatMutedUntil = _store.ChatUntil(me.Code),
                    Held = HeldCard(me.Code), // (0.7.0: a started match whose seat waits for this player: the hub's REJOIN)
                });
                BroadcastPresence(me.Code);
                while (!ct.IsCancellationRequested)
                {
                    OnlineMsg? msg = await OnlineProtocol.ReceiveAsync(ws, ct);
                    if (msg == null)
                    {
                        break;
                    }
                    OnlineMsg reply;
                    try
                    {
                        reply = msg is TurnCredsReq ? await TurnCreds(me.Code) : Handle(me.Code, msg);
                    }
                    catch (OnlineException ex)
                    {
                        reply = new ErrorMsg { Code = ex.Code, Text = ex.Message };
                    }
                    reply.Id = msg.Id;
                    await session.Send(reply);
                    if (msg is DeleteProfileReq && reply is OkMsg)
                    {
                        break; // (nobody to be any more: the connection closes)
                    }
                }
            }
            catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or OnlineException)
            {
            }
            finally
            {
                session.Ended.TrySetResult();
                if (_online.TryRemove(KeyValuePair.Create(me.Code, session)))
                {
                    Disconnected(me.Code);
                    BroadcastPresence(me.Code);
                }
            }
        }

        private static async Task Refuse(WebSocket ws, OnlineException ex, CancellationToken ct)
        {
            try
            {
                await OnlineProtocol.SendAsync(ws, new ErrorMsg { Code = ex.Code, Text = ex.Message }, ct);
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await ws.CloseOutputAsync(WebSocketCloseStatus.PolicyViolation, ex.Code, timeout.Token);
            }
            catch (Exception e) when (e is WebSocketException or OperationCanceledException)
            {
            }
        }

        private OnlineMsg Handle(string me, OnlineMsg msg)
        {
            if (msg is FindStartReq)
            {
                ReleaseHeld(me); // (a seat held in a match this player isn't playing isn't "your game": 0.7.0, N1)
            }
            switch (msg)
            {
            case SetNameReq r:
                return new PlayerMsg { Player = Card(_store.SetName(me, r.Name), me) };
            case SetLicenseReq r:
                _store.SetLicense(me, r.License);
                return new OkMsg { Done = true };
            case SetFindableReq r:
                _store.SetFindable(me, r.Findable);
                return new OkMsg { Done = true };
            case GetPlayerReq r:
            {
                Store.Player? p = _store.Get(r.Code);
                // someone who blocked you is "not found", so a block can't be seen from the other side
                if (p == null || _store.HasLink(p.Code, me, LinkKind.Block))
                {
                    throw new OnlineException("not_found", "no such player");
                }
                return new PlayerMsg { Player = Card(p, me) };
            }
            case SearchReq r:
                return new PlayersMsg
                {
                    Players = _store.Search(r.Query).Where(p => p.Code != me && !_store.HasLink(p.Code, me, LinkKind.Block))
                        .Select(p => Card(p, me)).ToList(),
                };
            case AddReq r:
                return Add(me, r.Code, r.Kind);
            case AnswerReq r:
            {
                Mutual(r.Kind);
                if (!_store.RemoveLink(r.Code, me, Request(r.Kind)))
                {
                    throw new OnlineException("no_request", "no such request");
                }
                if (r.Accept && r.Kind == LinkKind.Rival && _store.HasLink(me, r.Code, LinkKind.Friend))
                {
                    return new OkMsg { Done = false }; // (they became friends meanwhile: a friend is never a rival too)
                }
                if (r.Accept)
                {
                    Link(me, r.Code, r.Kind);
                }
                return new OkMsg { Done = r.Accept };
            }
            case RemoveReq r:
            {
                Mutual(r.Kind);
                bool removed = Unlink(me, r.Code, r.Kind);
                removed |= _store.RemoveLink(me, r.Code, Request(r.Kind)); // take back my request
                removed |= _store.RemoveLink(r.Code, me, Request(r.Kind)); // or turn theirs down
                return new OkMsg { Done = removed };
            }
            case BlockReq r:
                if (r.Code == me || _store.Get(r.Code) == null)
                {
                    throw new OnlineException("not_found", "no such player");
                }
                if (r.Block)
                {
                    _store.AddLink(me, r.Code, LinkKind.Block);
                    foreach (string kind in new[] { LinkKind.Friend, LinkKind.Rival })
                    {
                        Unlink(me, r.Code, kind);
                        _store.RemoveLink(me, r.Code, Request(kind));
                        _store.RemoveLink(r.Code, me, Request(kind));
                    }
                }
                else
                {
                    _store.RemoveLink(me, r.Code, LinkKind.Block);
                }
                return new OkMsg { Done = true };
            case RosterReq:
                return Roster(me);
            case HostGameReq r:
                return HostGame(me, r.Info, r.Find);
            case UpdateGameReq r:
                return UpdateGame(me, r.Info);
            case ListGamesReq:
                return ListGames(me);
            case JoinGameReq r:
                return JoinGame(me, r.GameId, r.Version, r.Mods, r.Served);
            case LeaveGameReq:
                return new OkMsg { Done = LeaveGame(me) };
            case StartGameReq r:
                return StartGame(me, r.Started);
            case InviteReq r:
                return Invite(me, r.Code);
            case RecentReq:
                return new PlayersMsg { Players = Cards(_store.Recent(me).Where(c => !_store.Blocked(me, c)), me) };
            case PartyInviteReq r:
                return PartyInvite(me, r.Code);
            case PartyJoinReq r:
                return PartyJoin(me, r.PartyId);
            case PartyLeaveReq:
                return new OkMsg { Done = PartyLeave(me) };
            case PingReq r:
                return SetPing(me, r.Ms);
            case FindStartReq r:
                return FindStart(me, r);
            case FindCancelReq:
                return new OkMsg { Done = FindCancel(me) };
            case FindVoteReq r:
                return FindVote(me, r.Arena);
            case DeleteProfileReq:
            {
                FindCancel(me, "left");
                LeaveGame(me);
                PartyLeave(me);
                foreach (string kind in new[] { LinkKind.Friend, LinkKind.Rival })
                {
                    foreach (string other in _store.Linked(me, kind))
                    {
                        Unlink(me, other, kind);
                    }
                }
                _store.DeletePlayer(me);
                return new OkMsg { Done = true };
            }
            case TurnUsageReq r:
                return TurnUsage(me, r.Bytes);
            case ChatReq r:
                return Chat(me, r);
            case MatchResultReq r:
                return MatchResult(me, r); // (head to head: HubRecords.cs)
            case ReportReq r:
            {
                if (!ReportReason.Valid(r.Reason))
                {
                    throw new OnlineException("bad_reason", "not a report reason");
                }
                Store.Player? p = r.Code == me ? null : _store.Get(r.Code);
                if (p == null)
                {
                    throw new OnlineException("not_found", "no such player");
                }
                bool added = _store.AddReport(me, p, r.Reason, r.Reason == ReportReason.Chat ? ChatEvidence(p.Code) ?? "(no chat in memory: the server restarted or it was over three hours ago)" : null);
                if (added)
                {
                    AlertReport(r.Reason); // the owner's phone (HubAlerts.cs)
                }
                return new OkMsg { Done = added };
            }
            default:
                throw new OnlineException("unknown", $"not a request: {msg.GetType().Name}");
            }
        }

        private static string Request(string kind) => "req:" + kind;

        private static void Mutual(string kind)
        {
            if (!LinkKind.IsMutual(kind))
            {
                throw new OnlineException("bad_kind", "kind must be friend or rival");
            }
        }

        private OkMsg Add(string me, string code, string kind)
        {
            Mutual(kind);
            if (code == me)
            {
                throw new OnlineException("self", "that's your own code");
            }
            if (_store.Get(code) == null)
            {
                throw new OnlineException("not_found", "no player has this code");
            }
            if (_store.HasLink(me, code, LinkKind.Block))
            {
                throw new OnlineException("blocked", "you blocked this player: unblock them first");
            }
            if (_store.HasLink(code, me, LinkKind.Block))
            {
                return new OkMsg { Done = false }; // looks sent; nothing reaches a player who blocked you
            }
            if (_store.HasLink(me, code, kind))
            {
                return new OkMsg { Done = true };
            }
            // friend OR rival, never both (vanilla, 0.7.0): a friend can't be made a rival; a rival can become a friend (vanilla
            // "you have upgraded your rival to a friend": Link drops the rival link when the friendship completes)
            if (kind == LinkKind.Rival && _store.HasLink(me, code, LinkKind.Friend))
            {
                throw new OnlineException("already_friends", "you're friends already: a friend can't be a rival too");
            }
            if (_store.HasLink(code, me, Request(kind)))
            {
                // they asked first (or both entered each other's codes, as vanilla): done now
                _store.RemoveLink(code, me, Request(kind));
                Link(me, code, kind);
                return new OkMsg { Done = true };
            }
            if (!_store.HasLink(me, code, Request(kind)))
            {
                if (_store.CountLinks(me, Request(kind)) >= MaxPendingRequests)
                {
                    throw new OnlineException("too_many", "too many requests waiting: take some back first");
                }
                _store.AddLink(me, code, Request(kind));
                Push(code, new RequestEvent { From = Card(_store.Get(me)!, code), Kind = kind });
            }
            return new OkMsg { Done = false };
        }

        private void Link(string a, string b, string kind)
        {
            _store.RemoveLink(a, b, Request(kind));
            _store.RemoveLink(b, a, Request(kind));
            if (kind == LinkKind.Friend)
            {
                // a rival upgraded to a friend: the rival link and any rival requests between them go (both told: RemovedEvent)
                if (Unlink(a, b, LinkKind.Rival))
                {
                    Push(a, new RemovedEvent { Code = b, Kind = LinkKind.Rival });
                }
                _store.RemoveLink(a, b, Request(LinkKind.Rival));
                _store.RemoveLink(b, a, Request(LinkKind.Rival));
            }
            _store.AddLink(a, b, kind);
            _store.AddLink(b, a, kind);
            Push(a, new AddedEvent { Player = Card(_store.Get(b)!, a), Kind = kind });
            Push(b, new AddedEvent { Player = Card(_store.Get(a)!, b), Kind = kind });
        }

        private bool Unlink(string me, string other, string kind)
        {
            bool had = _store.RemoveLink(me, other, kind);
            had |= _store.RemoveLink(other, me, kind);
            if (had)
            {
                Push(other, new RemovedEvent { Code = me, Kind = kind });
            }
            return had;
        }

        private RosterMsg Roster(string me)
        {
            var roster = new RosterMsg
            {
                Friends = Cards(_store.Linked(me, LinkKind.Friend), me),
                Rivals = Cards(_store.Linked(me, LinkKind.Rival), me),
                Blocked = Cards(_store.Linked(me, LinkKind.Block), me),
            };
            foreach (string kind in new[] { LinkKind.Friend, LinkKind.Rival })
            {
                foreach (PlayerCard p in Cards(_store.Linked(me, Request(kind)), me))
                {
                    roster.Requests.Add(new RequestInfo { Player = p, Kind = kind, Incoming = false });
                }
                foreach (PlayerCard p in Cards(_store.Linked(me, Request(kind), incoming: true), me))
                {
                    roster.Requests.Add(new RequestInfo { Player = p, Kind = kind, Incoming = true });
                }
            }
            return roster;
        }

        private List<PlayerCard> Cards(IEnumerable<string> codes, string viewer)
            => codes.Select(_store.Get).Where(p => p != null).Select(p => Card(p!, viewer)).ToList();

        // a player as the viewer sees them: presence (and the game they're in, when the viewer may join it) only between
        // friends or rivals (and for yourself)
        private PlayerCard Card(Store.Player p, string viewer)
        {
            bool friend = _store.HasLink(viewer, p.Code, LinkKind.Friend);
            bool rival = !friend && _store.HasLink(viewer, p.Code, LinkKind.Rival); // (friend OR rival, 0.7.0)
            bool seen = p.Code == viewer || friend || rival;
            return new PlayerCard
            {
                Code = p.Code,
                Name = p.Name,
                Tag = p.Tag,
                Presence = seen ? PresenceOf(p.Code) : Presence.Offline,
                Friend = friend,
                Rival = rival,
                License = p.License,
                GameId = seen ? JoinableGameOf(p.Code, viewer) : null,
                Record = p.Code == viewer ? null : _store.HeadToHead(viewer, p.Code), // (0.7.0: the same numbers on both sides)
            };
        }

        private void Push(string code, OnlineMsg msg)
        {
            if (_online.TryGetValue(code, out Session? s))
            {
                _ = s.Push(msg);
            }
        }

        // tell this player's friends and rivals where they are now (menus, lobby, match, offline) and which game they may join
        private void BroadcastPresence(string code)
        {
            string presence = PresenceOf(code);
            foreach (string other in _store.Linked(code, LinkKind.Friend).Union(_store.Linked(code, LinkKind.Rival)))
            {
                if (_online.ContainsKey(other))
                {
                    Push(other, new PresenceEvent { Code = code, Presence = presence, GameId = JoinableGameOf(code, other) });
                }
            }
        }
    }
}
