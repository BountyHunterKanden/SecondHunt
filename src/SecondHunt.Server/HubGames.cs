using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using MphRecomp.Online;

namespace SecondHunt.Server
{
    // Hosted games (docs/ONLINE_PLAY.md section 6): a player opens a lobby, others see it (public, or the host's friends /
    // rivals as vanilla's MAKE THIS GAME AVAILABLE TO, or because they were invited), join it and get a relay slot. The
    // match itself never touches the hub: it runs between the devices through the relay room that belongs to the game.
    // All of it is in memory (a server restart ends every lobby) under one lock.
    public sealed partial class Hub
    {
        private const int MaxText = 32;

        private readonly Relay _relay;
        private readonly string _relayHost;
        private readonly object _gamesLock = new();
        private readonly Dictionary<string, Game> _games = new();
        private readonly Dictionary<string, Game> _gameOf = new();

        private sealed class Game
        {
            public string Id { get; }
            public string Host { get; set; } // (O5b: the successor after a host migration)
            public Relay.Room Room { get; }
            public GameInfo Info { get; set; }
            public bool Started { get; set; }
            // relay slot -> player code; slot 0 = the host
            public SortedDictionary<int, string> Slots { get; } = new();
            public HashSet<string> Invited { get; } = new();
            // relay slot -> player code of a seat dropped for its silence (DropQuietSeats) while the match runs: it gets the
            // seat back if its device sends again (SeatReturned)
            public Dictionary<int, string> Dropped { get; } = new();

            public Game(string id, string host, GameInfo info, Relay.Room room)
            {
                Id = id;
                Host = host;
                Info = info;
                Room = room;
            }

            public bool Has(string code) => Slots.ContainsValue(code);
            public int SlotOf(string code) => Slots.First(s => s.Value == code).Key;
            public bool Full => Slots.Count >= Info.MaxPlayers;
        }

        private static GameInfo Clean(GameInfo info, int players = 1) => new()
        {
            Mode = Cut(info.Mode) ?? "",
            Arena = Cut(info.Arena) ?? "",
            MaxPlayers = Math.Clamp(info.MaxPlayers, Math.Max(2, players), RelayWire.MaxSlots),
            Friends = info.Friends,
            Rivals = info.Rivals,
            Public = info.Public,
            Version = Cut(info.Version, 64) ?? "",
            Rules = Cut(info.Rules, 64),
            Mods = string.IsNullOrEmpty(info.Mods) ? GameInfo.NoMods : Cut(info.Mods, 64)!,
            ExtraPickups = info.ExtraPickups,
            ExtraCharacters = info.ExtraPickups && info.ExtraCharacters, // (characters need the pickups)
            ExtraArena = info.ExtraArena, // (0.7.0, T2: Find Game's backfill checks the searchers have it)
        };

        // decision 10 (doc section 8b): a game strangers can find runs without mods
        private static void CheckMods(GameInfo info)
        {
            if (info.Public && info.Mods != GameInfo.NoMods)
            {
                throw new OnlineException("mods", "public games run without mods");
            }
        }

        private static string? Cut(string? text, int max = MaxText)
            => text == null ? null : text.Length > max ? text[..max] : text;

        // 6 characters of Crockford base32, not yet taken
        private static string NewId(Func<string, bool> taken)
        {
            const string alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";
            string id;
            do
            {
                id = new string(RandomNumberGenerator.GetBytes(6).Select(b => alphabet[b & 31]).ToArray());
            }
            while (taken(id));
            return id;
        }

        private string PresenceOf(string code)
        {
            if (!_online.ContainsKey(code))
            {
                return Presence.Offline;
            }
            lock (_gamesLock)
            {
                // (a held seat, its device not playing: just online)
                return _gameOf.TryGetValue(code, out Game? g) && !IsHeld(g, code) ? (g.Started ? Presence.Match : Presence.Lobby)
                    : Presence.Online;
            }
        }

        // may the viewer see this game at all: members always; others when nobody in it blocked them (or the other way),
        // and it's public, or they were invited, or their party leader is in it, or they're the host's friend / rival and
        // the host allows those
        private bool Visible(Game g, string viewer)
        {
            if (g.Has(viewer))
            {
                return true;
            }
            if (g.Slots.Values.Any(m => _store.Blocked(m, viewer)))
            {
                return false;
            }
            return g.Info.Public || g.Invited.Contains(viewer) || LeaderIn(g, viewer)
                || (g.Info.Friends && _store.HasLink(viewer, g.Host, LinkKind.Friend))
                || (g.Info.Rivals && _store.HasLink(viewer, g.Host, LinkKind.Rival));
        }

        private bool Joinable(Game g, string viewer) => !g.Has(viewer) && !g.Started && !g.Full && Visible(g, viewer);

        private string? JoinableGameOf(string code, string viewer)
        {
            lock (_gamesLock)
            {
                if (!_gameOf.TryGetValue(code, out Game? g))
                {
                    return null;
                }
                return code == viewer || Joinable(g, viewer) ? g.Id : null;
            }
        }

        private GameCard GameCard(Game g, string viewer) => new()
        {
            Id = g.Id,
            Host = Card(_store.Get(g.Host)!, viewer),
            Info = g.Info,
            Players = g.Slots.Count,
            Started = g.Started,
            Members = g.Slots.Select(s => (Slot: s.Key, Player: _store.Get(s.Value))).Where(m => m.Player != null)
                .Select(m =>
                {
                    PlayerCard card = Card(m.Player!, viewer);
                    card.Slot = m.Slot; // the relay slot (Voice O8 matches audio to players by it)
                    return card;
                }).ToList(),
        };

        private RelayInfo RelayFor(Relay.Member m) => new()
        {
            Host = _relayHost,
            Port = _relay.Port,
            Token = RelayWire.TokenText(m.Token),
            Slot = m.Slot,
        };

        private Game HostedBy(string me)
        {
            if (!_gameOf.TryGetValue(me, out Game? g))
            {
                throw new OnlineException("no_game", "you aren't in a game");
            }
            if (g.Host != me)
            {
                throw new OnlineException("not_host", "only the host can do that");
            }
            return g;
        }

        private GameMsg HostGame(string me, GameInfo info, string? find = null)
        {
            lock (_gamesLock)
            {
                FindMatch? found = null;
                if (string.IsNullOrEmpty(find) && _matchOf.TryGetValue(me, out FindMatch? mine) && mine.Host == me)
                {
                    find = mine.Id; // (the chosen host's next host_game is the match's game: clients needn't name it)
                }
                if (!string.IsNullOrEmpty(find))
                {
                    info = FindHostInfo(me, find, info, out found); // (Find Game: the match's mode, arena, seats, who may join)
                }
                info = Clean(info);
                CheckMods(info);
                if (found == null)
                {
                    FindCancel(me, "hosting"); // (hosting another game ends a search)
                }
                LeaveGame(me);
                string id = NewId(_games.ContainsKey);
                Relay.Room room = _relay.OpenRoom(id);
                _relay.Keep(room, true);
                var g = new Game(id, me, info, room);
                Relay.Member member = _relay.AddMember(room, 0);
                g.Slots[0] = me;
                _games[id] = g;
                _gameOf[me] = g;
                BroadcastPresence(me);
                if (found != null)
                {
                    FindGameOpened(found, g);
                }
                else
                {
                    PartyFollow(me, g);
                }
                return new GameMsg { Game = GameCard(g, me), Relay = RelayFor(member) };
            }
        }

        private GameMsg UpdateGame(string me, GameInfo info)
        {
            lock (_gamesLock)
            {
                Game g = HostedBy(me);
                info = Clean(info, g.Slots.Count);
                CheckMods(info);
                if (info.Mods != g.Info.Mods && g.Slots.Count > 1)
                {
                    throw new OnlineException("mods", "the mods can't change once players have joined");
                }
                g.Info = info;
                foreach (string m in g.Slots.Values)
                {
                    BroadcastPresence(m); // who may join changed
                }
                return new GameMsg { Game = GameCard(g, me) };
            }
        }

        private GamesMsg ListGames(string me)
        {
            lock (_gamesLock)
            {
                return new GamesMsg
                {
                    Games = _games.Values.Where(g => Joinable(g, me)).Select(g => GameCard(g, me)).ToList(),
                };
            }
        }

        private GameMsg JoinGame(string me, string id, string version, string mods)
        {
            lock (_gamesLock)
            {
                // a rejoin (Netcode O5): back into the started game this player still has a seat in (a socket drop keeps it),
                // same relay slot with a new token; an empty id = "my current started game" (after an app restart)
                if (string.IsNullOrEmpty(id))
                {
                    if (!_gameOf.TryGetValue(me, out Game? mine) || !mine.Started || !mine.Has(me))
                    {
                        throw new OnlineException("not_found", "you're not in a started game");
                    }
                    id = mine.Id;
                }
                if (_games.TryGetValue(id, out Game? back) && back.Started && back.Has(me))
                {
                    Relay.Member again = _relay.AddMember(back.Room, back.SlotOf(me));
                    _gameOf[me] = back;
                    return new GameMsg { Game = GameCard(back, me), Relay = RelayFor(again) };
                }
                if (!_games.TryGetValue(id, out Game? g) || !Visible(g, me))
                {
                    throw new OnlineException("not_found", "that game isn't open");
                }
                if (g.Has(me))
                {
                    throw new OnlineException("already_in", "you're already in this game");
                }
                if (g.Started)
                {
                    throw new OnlineException("started", "that game already started");
                }
                if (g.Full)
                {
                    throw new OnlineException("full", "that game is full");
                }
                if (g.Info.Version != version)
                {
                    throw new OnlineException("version", "that game runs a different version: both players need the same game "
                        + "update and ROM");
                }
                if (string.IsNullOrEmpty(mods))
                {
                    mods = GameInfo.NoMods;
                }
                if (g.Info.Mods != mods)
                {
                    throw new OnlineException("mods", g.Info.Mods == GameInfo.NoMods ? "that game runs without mods"
                        : "that game runs with a different set of mods");
                }
                // with mods on, everyone in it must be a friend or rival of everyone else (doc section 8b)
                if (mods != GameInfo.NoMods && g.Slots.Values.Any(m => !_store.HasLink(me, m, LinkKind.Friend)
                    && !_store.HasLink(me, m, LinkKind.Rival)))
                {
                    throw new OnlineException("mods", "modded games are for players who are all friends or rivals");
                }
                FindCancel(me, "joining");
                LeaveGame(me);
                int slot = Enumerable.Range(1, RelayWire.MaxSlots - 1).First(s => !g.Slots.ContainsKey(s));
                Relay.Member member = _relay.AddMember(g.Room, slot);
                g.Slots[slot] = me;
                g.Invited.Remove(me);
                _gameOf[me] = g;
                Store.Player joiner = _store.Get(me)!;
                foreach (string m in g.Slots.Values.Where(m => m != me))
                {
                    Push(m, new MemberEvent { GameId = g.Id, Player = Card(joiner, m), Slot = slot, Joined = true });
                }
                foreach (string m in g.Slots.Values)
                {
                    BroadcastPresence(m); // the joiner is in a lobby now; the game may be full for the others' friends
                }
                PartyFollow(me, g);
                return new GameMsg { Game = GameCard(g, me), Relay = RelayFor(member) };
            }
        }

        // leave the game this player is in (the host leaving closes it); false = wasn't in one
        private bool LeaveGame(string me)
        {
            lock (_gamesLock)
            {
                if (!_gameOf.TryGetValue(me, out Game? g))
                {
                    return false;
                }
                if (g.Host == me)
                {
                    // O5b: a started match goes on with a new host (the others are mid-match); a lobby closes
                    if (!g.Started || !MigrateHost(g))
                    {
                        CloseGame(g, "the host left");
                    }
                    return true;
                }
                int slot = g.SlotOf(me);
                g.Slots.Remove(slot);
                _gameOf.Remove(me);
                _relay.RemoveMember(g.Room, slot);
                Store.Player leaver = _store.Get(me)!;
                foreach (string m in g.Slots.Values)
                {
                    Push(m, new MemberEvent { GameId = g.Id, Player = Card(leaver, m), Slot = slot, Joined = false });
                }
                BroadcastPresence(me);
                foreach (string m in g.Slots.Values)
                {
                    BroadcastPresence(m);
                }
                return true;
            }
        }

        private void CloseGame(Game g, string reason)
        {
            _games.Remove(g.Id);
            foreach (string m in g.Slots.Values)
            {
                _gameOf.Remove(m);
            }
            _relay.CloseRoom(g.Room);
            foreach (string m in g.Slots.Values)
            {
                if (m != g.Host || reason != "the host left")
                {
                    Push(m, new GameClosedEvent { GameId = g.Id, Reason = reason });
                }
                BroadcastPresence(m);
            }
        }

        private OkMsg StartGame(string me, bool started)
        {
            lock (_gamesLock)
            {
                Game g = HostedBy(me);
                if (started && !g.Started)
                {
                    _store.AddRecent(g.Slots.Values.ToList());
                    RoundStarted(g); // (who's in this match: the host's head-to-head report is checked against it, HubRecords.cs)
                    if (g.Slots.Count >= 2)
                    {
                        _store.AddMatches(g.Slots.Values.ToList()); // (a match with people counts toward the typed-chat unlock)
                    }
                }
                g.Started = started;
                // a lobby is kept while its host is online; a match lives on its traffic
                _relay.Keep(g.Room, !started);
                if (!started)
                {
                    // back in the lobby: players who dropped during the match are gone now
                    foreach (string m in g.Slots.Values.Where(m => !_online.ContainsKey(m)).ToList())
                    {
                        LeaveGame(m);
                    }
                }
                foreach (string m in g.Slots.Values)
                {
                    BroadcastPresence(m);
                }
                return new OkMsg { Done = true };
            }
        }

        private OkMsg Invite(string me, string code)
        {
            lock (_gamesLock)
            {
                if (!_gameOf.TryGetValue(me, out Game? g))
                {
                    throw new OnlineException("no_game", "you aren't in a game");
                }
                bool linked = _store.HasLink(me, code, LinkKind.Friend) || _store.HasLink(me, code, LinkKind.Rival);
                if (!linked || _store.Blocked(me, code))
                {
                    throw new OnlineException("not_friend", "you can invite your friends and rivals");
                }
                g.Invited.Add(code);
                Push(code, new InviteEvent { From = Card(_store.Get(me)!, code), Game = GameCard(g, code) });
                BroadcastPresence(me); // the invited player may join from their friends list now
                return new OkMsg { Done = _online.ContainsKey(code) };
            }
        }

        // the websocket closed: a lobby is left; a started match keeps the player (the relay carries it), and the game ends
        // when its relay room goes quiet or the host takes everyone back to the lobby; the party is left either way
        private void Disconnected(string me)
        {
            lock (_gamesLock)
            {
                FindCancel(me, "left");
                if (_gameOf.TryGetValue(me, out Game? g) && !g.Started)
                {
                    LeaveGame(me);
                }
                else if (g != null && g.Host == me)
                {
                    // O5b: the host of a started match dropped: a player still online takes over (none: as before, the
                    // game ends when its relay room goes quiet)
                    MigrateHost(g);
                }
                PartyLeave(me);
                _pings.TryRemove(me, out _);
            }
        }

        // O5b host migration (docs/ONLINE_PLAY.md section 9b): the host of a started match left or its connection dropped.
        // The lowest remaining slot whose player is online hosts the match now: the old host is out of the game and its
        // relay slot, and everyone left hears it (MemberEvent: the old host's slot left, HostSlot = the new host's). The
        // devices re-point themselves (the relay room's slots stay as they are). false = nobody online to take over.
        private bool MigrateHost(Game g)
        {
            string old = g.Host;
            int oldSlot = g.SlotOf(old);
            int next = -1;
            foreach ((int slot, string m) in g.Slots)
            {
                if (m != old && _online.ContainsKey(m))
                {
                    // (0.7.0: a seat still playing first: one whose device is quiet on the relay only when there's no other)
                    if (next < 0 || _relay.QuietMs(g.Room, next) > HeldQuietMs && _relay.QuietMs(g.Room, slot) <= HeldQuietMs)
                    {
                        next = slot;
                    }
                }
            }
            if (next < 0)
            {
                return false;
            }
            g.Slots.Remove(oldSlot);
            _gameOf.Remove(old);
            _relay.RemoveMember(g.Room, oldSlot);
            g.Host = g.Slots[next];
            Store.Player leaver = _store.Get(old)!;
            foreach (string m in g.Slots.Values)
            {
                Push(m, new MemberEvent { GameId = g.Id, Player = Card(leaver, m), Slot = oldSlot, Joined = false, HostSlot = next });
            }
            BroadcastPresence(old);
            foreach (string m in g.Slots.Values)
            {
                BroadcastPresence(m);
            }
            return true;
        }

        // ---- held seats (release test T1, 2026-10-10) ----
        // A started match keeps a player's seat when their connection drops (O5 rejoin), but a device that stopped sending to
        // the relay isn't playing: its seat is HELD. A fresh login hears of it (WelcomeMsg.Held: the hub offers REJOIN), FIND
        // GAME releases it instead of "leave your game first" (a LeaveGame lost when the app quit, N1), and one quiet for
        // SeatHoldMs is dropped (the host's match dropped that seat long before). One whose device sends again gets it back
        // (SeatReturned: only its sends were lost).
        // (the selftest shortens them)
        internal static long HeldQuietMs = 5_000, SeatHoldMs = 90_000;
        private Timer? _seatTimer;

        private void StartSeatTimer() => _seatTimer = new Timer(_ => DropQuietSeats(), null, 1_000, 1_000);

        // (under _gamesLock) a started match's non-host seat whose device has been silent on the relay for a while
        private bool IsHeld(Game g, string code) => g.Started && g.Host != code && g.Has(code)
            && _relay.QuietMs(g.Room, g.SlotOf(code)) > HeldQuietMs;

        private Game? HeldGame(string me)
        {
            lock (_gamesLock)
            {
                return _gameOf.TryGetValue(me, out Game? g) && IsHeld(g, me) ? g : null;
            }
        }

        private GameCard? HeldCard(string me)
        {
            lock (_gamesLock)
            {
                return HeldGame(me) is Game g ? GameCard(g, me) : null;
            }
        }

        // out of a held seat (FIND GAME, and anything else that needs this player free); false = none held
        private bool ReleaseHeld(string me)
        {
            lock (_gamesLock)
            {
                return HeldGame(me) != null && LeaveGame(me);
            }
        }

        private void DropQuietSeats()
        {
            lock (_gamesLock)
            {
                var quiet = new List<(Game, int, string)>();
                foreach (Game g in _games.Values.Where(g => g.Started))
                {
                    foreach ((int slot, string m) in g.Slots)
                    {
                        if (m != g.Host && _relay.QuietMs(g.Room, slot) > SeatHoldMs)
                        {
                            quiet.Add((g, slot, m));
                        }
                    }
                }
                foreach ((Game g, int slot, string m) in quiet)
                {
                    if (LeaveGame(m))
                    {
                        g.Dropped[slot] = m;
                    }
                }
            }
        }

        // the relay: a seat dropped for its silence sends again (its sends were lost, not the device: it heard the match all
        // along, so it never left it). It's back in the game if the match still runs, nobody took the slot and the player
        // isn't in another game; the others hear it joined again (release 0.2.0 F1)
        private bool SeatReturned(Relay.Member member)
        {
            lock (_gamesLock)
            {
                Game? g = _games.Values.FirstOrDefault(g => g.Room == member.Room);
                if (g == null || !g.Started || !g.Dropped.TryGetValue(member.Slot, out string? me) || g.Slots.ContainsKey(member.Slot)
                    || _gameOf.ContainsKey(me) || !_relay.RestoreMember(member))
                {
                    return false;
                }
                g.Dropped.Remove(member.Slot);
                g.Slots[member.Slot] = me;
                _gameOf[me] = g;
                Store.Player back = _store.Get(me)!;
                foreach (string m in g.Slots.Values.Where(m => m != me))
                {
                    Push(m, new MemberEvent { GameId = g.Id, Player = Card(back, m), Slot = member.Slot, Joined = true });
                }
                foreach (string m in g.Slots.Values)
                {
                    BroadcastPresence(m);
                }
                return true;
            }
        }

        private void RoomExpired(Relay.Room room)
        {
            lock (_gamesLock)
            {
                if (_games.TryGetValue(room.Id, out Game? g) && g.Room == room)
                {
                    CloseGame(g, "the match ended");
                }
            }
        }
    }
}
