using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using MphRead;
using MphRecomp.Online;
using MphRecomp.Replay;

namespace MphRecomp.Multiplayer.Net
{
    // An online match's pieces, from the lobby to the match screen (as LanSession.Current is for LAN): the server
    // connection, this device's relay slot and the match session over it. The launcher hosts or joins, then opens the
    // match screen, which plays through Current and disposes it when it closes: BYE to the others, out of the game on
    // the server, sockets closed. (docs/ONLINE_PLAY.md O3; the PC harness is MphRead.Tools -onlinetest.)
    // A match from the ONLINE PLAY menus outlives its match screen when it ends normally (EndMatch: the menus' RESULTS, then
    // PLAY AGAIN back to the same lobby, or QUIT disposes it); the screen holds it through TakeScreenHold, whose Dispose
    // (the screen closing: quit from the pause menu, the activity gone) disposes the match unless it was handed back.
    public sealed class OnlineMatch : IDisposable
    {
        public static OnlineMatch? Current { get; set; }

        public OnlineClient Client { get; }
        public RelayTransport Relay { get; }
        // the match's transport (O5, decision 18): Cloudflare TURN once it's up, the relay meanwhile and as the fallback
        public HybridTransport Link { get; }
        // why there's no TURN (the server's reason, "server too old", the allocation's error); null while asking or when up
        public string? TurnNote { get; private set; }
        public NetMatchSession Net { get; }
        public GameCard Game { get; }
        // who's in the game as the server says (its cards: name#tag, friend code); the menus keep it current from MemberEvent
        public List<PlayerCard> Members { get; }
        // the match ended and its screen handed it back (RESULTS, then PLAY AGAIN or QUIT)
        public bool HandedBack { get; private set; }
        public bool Disposed => _disposed;
        private bool _disposed;

        private OnlineMatch(OnlineClient client, RelayTransport relay, HybridTransport link, NetMatchSession net, GameCard game)
        {
            Client = client;
            Relay = relay;
            Link = link;
            Net = net;
            Game = game;
            Members = game.Members.ToList();
            net.LocalPingMs = () => relay.PingMs;
            net.LinkAlive = () => relay.RelayAlive; // (reconnect: our own network vs the host's)
            // O5b: a server that can move a started match to a new host (the host left or dropped) says who hosts next
            net.CanMigrate = client.ServerAtLeast(MigrateSinceServer);
            ReturnClient = ReturnClientsToMenus;
            client.EventReceived += OnServerEvent;
            _ = Task.Run(StartTurnAsync);
            _usageTimer = new Timer(_ => _ = ReportTurnUsageAsync(), null, UsageEveryMs, UsageEveryMs);
        }

        private const int UsageEveryMs = 30_000;
        // host migration (O5b): servers from this version name a new host (MemberEvent.HostSlot)
        public const string MigrateSinceServer = "0.5.0";

        // (the server connection's thread) the server moved this match to a new host, or closed it
        private void OnServerEvent(OnlineMsg msg)
        {
            if (msg is MemberEvent { HostSlot: int slot } moved && moved.GameId == Game.Id)
            {
                MatchLog?.Invoke($"host migration: slot {slot} hosts now (slot {moved.Slot} left)");
                // the game's host as the menus show it (lobby cards, RESULTS, PLAY AGAIN's lobby) is the new one
                if (Game.Members.FirstOrDefault(m => m.Slot == slot) is PlayerCard next)
                {
                    Game.Host = next;
                }
                Net.HostMoved(slot);
            }
            else if (msg is GameClosedEvent closed && closed.GameId == Game.Id)
            {
                Net.CanMigrate = false; // (no one takes over: the match ends as the host's silence says)
            }
        }

        // the relay slot of the game's host (0 unless the host moved: O5b)
        private static int HostSlotOf(GameCard game) =>
            game.Members.FirstOrDefault(m => m.Code == game.Host.Code)?.Slot ?? 0;
        private readonly Timer _usageTimer;
        private long _usageReported;

        // TURN beside the relay: short-lived credentials from our server (it holds the Cloudflare key), then an allocation;
        // the match runs on the relay until (and unless) this works. An older server drops a connection that asks, so ask
        // only from TurnCredsReq.SinceServer.
        private async Task StartTurnAsync()
        {
            (TurnClient? turn, string? note) = await RequestTurnAsync(Client);
            if (turn == null)
            {
                TurnNote = note;
                return;
            }
            if (_disposed || !Link.Attach(turn))
            {
                turn.Dispose();
                TurnNote = "closed";
                return;
            }
            TurnNote = null;
            MatchLog?.Invoke($"TURN allocated on {turn.Server}");
        }

        // an allocated TurnClient from the server's credentials, or null and why (also -onlinetest turn=local's devices)
        public static async Task<(TurnClient? Turn, string? Note)> RequestTurnAsync(OnlineClient client)
        {
            if (!client.ServerAtLeast(TurnCredsReq.SinceServer))
            {
                return (null, "server too old for TURN");
            }
            try
            {
                TurnCredsMsg creds = await client.RequestAsync<TurnCredsMsg>(new TurnCredsReq());
                if (creds.UseRelay)
                {
                    return (null, creds.Reason.Length > 0 ? creds.Reason : "relay only");
                }
                if (TurnClient.Resolve(creds.Urls) is not IPEndPoint server)
                {
                    return (null, "no usable TURN URL");
                }
                var turn = new TurnClient(server, creds.Username, creds.Credential);
                if (!await Task.Run(() => turn.Allocate()))
                {
                    string error = turn.Error ?? "no allocation";
                    turn.Dispose();
                    return (null, error);
                }
                return (turn, null);
            }
            catch (Exception ex) when (ex is OnlineException or System.Net.WebSockets.WebSocketException
                or OperationCanceledException or ObjectDisposedException or InvalidOperationException
                or System.Net.Sockets.SocketException)
            {
                return (null, "TURN: " + ex.Message);
            }
        }

        // what this device received through TURN since the last report (Cloudflare bills it): toward the server's monthly cap
        private async Task ReportTurnUsageAsync()
        {
            TurnClient? turn = Link.Turn;
            if (turn == null || !Client.ServerAtLeast(TurnCredsReq.SinceServer))
            {
                return;
            }
            long total = turn.BytesIn;
            long bytes = total - Interlocked.Exchange(ref _usageReported, total);
            if (bytes <= 0)
            {
                return;
            }
            try
            {
                await Client.RequestAsync<OkMsg>(new TurnUsageReq { Bytes = bytes });
            }
            catch (Exception ex) when (ex is OnlineException or System.Net.WebSockets.WebSocketException
                or OperationCanceledException or ObjectDisposedException or InvalidOperationException)
            {
            }
        }

        // (Android logcat MPHOnline; null = quiet)
        public static Action<string>? MatchLog { get; set; }

        // the match screen's hold on this match: disposing it disposes the match, unless that screen's match ended and was
        // handed back (each screen's own: an old screen closed late can't take a rematch with it)
        public IDisposable TakeScreenHold()
        {
            HandedBack = false;
            return _hold = new ScreenHold(this);
        }

        private ScreenHold? _hold;

        private sealed class ScreenHold : IDisposable
        {
            private readonly OnlineMatch _match;
            public bool Released;
            public ScreenHold(OnlineMatch match) => _match = match;

            public void Dispose()
            {
                if (!Released)
                {
                    Released = true;
                    _match.Dispose();
                }
            }
        }

        // the match screen: the match is over; the session stays for the menus' RESULTS (and the lobby after it)
        public void EndMatch()
        {
            if (_disposed)
            {
                return;
            }
            if (_hold != null)
            {
                _hold.Released = true;
            }
            HandedBack = true;
            Net.EndMatch();
        }

        // the menus start this game's next match: it isn't the one RESULTS kept any more (the next screen takes its own hold)
        public void Rearm() => HandedBack = false;

        // host: PLAY AGAIN: the same game's lobby again, listed on the server again
        public void BackToLobby()
        {
            Net.LobbyChanged();
            _ = Task.Run(async () =>
            {
                try
                {
                    await Client.RequestAsync<OkMsg>(new StartGameReq { Started = false });
                }
                catch (Exception ex) when (ex is OnlineException or System.Net.WebSockets.WebSocketException
                    or OperationCanceledException or ObjectDisposedException or InvalidOperationException)
                {
                }
            });
        }

        // what must match to join (GameInfo.Version): the ROM and the game logic (romKey = LanSession.RomKeyFor)
        public static string VersionKey(string romKey) => $"{romKey}/{ReplayFile.CurrentSimVersion}";

        // open a game on the server and host its match (template seat 0 = this device, then bots); who sees it: PUBLIC and
        // the host's friends / rivals (all three off = invite only; the ONLINE PLAY menus' WHO CAN JOIN)
        public static async Task<OnlineMatch> HostAsync(OnlineClient client, Uri server, MatchSettings template, string romKey,
            int maxPlayers, bool isPublic = true, bool friends = true, bool rivals = true)
        {
            GameMsg game = await client.RequestAsync<GameMsg>(new HostGameReq
            {
                Info = new GameInfo
                {
                    Mode = template.Mode.ToString(), Arena = template.Arena, MaxPlayers = maxPlayers, Public = isPublic,
                    Friends = friends, Rivals = rivals, Version = VersionKey(romKey),
                    Rules = template.TimeLimitSeconds is float t ? $"{t:0} s" : null,
                    // the ADVANCED box's extra rules (the list's icons, ONLINE SETTINGS' filters; server 0.6.0)
                    ExtraPickups = template.PirateBlaster != false,
                    ExtraCharacters = template.PirateBlaster != false && template.ExtraCharacters != false,
                    ExtraArena = ExtraArenas.IsExtra(template.Arena), // (server 0.7.0: Find Game's backfill)
                },
            });
            RelayTransport relay = RelayTransport.For(game.Relay, server);
            var link = new HybridTransport(relay);
            // the match's seats: the people (maxPlayers) and the template's bots (P8: past 4, so a joiner doesn't push a bot out)
            int seatLimit = maxPlayers + template.Players.Count(p => p.Bot);
            return new OnlineMatch(client, relay, link, NetMatchSession.Host(link, template, romKey, seatLimit), game.Game);
        }

        // join a game by id, or QUICK MATCH's pick from the open games list: this version's, a friend's or rival's first, then
        // the fullest (games fill up and start sooner) (OnlineException "no_games" when there's none)
        // (noExtraPickups / noExtraCharacters: ONLINE SETTINGS' filters, games that allow them aren't picked)
        public static async Task<OnlineMatch> JoinAsync(OnlineClient client, Uri server, string romKey, string? gameId,
            bool noExtraPickups = false, bool noExtraCharacters = false)
        {
            string version = VersionKey(romKey);
            if (string.IsNullOrEmpty(gameId))
            {
                GamesMsg list = await client.RequestAsync<GamesMsg>(new ListGamesReq());
                gameId = list.Games.Where(g => g.Info.Version == version && !(noExtraPickups && g.Info.ExtraPickups)
                        && !(noExtraCharacters && g.Info.ExtraCharacters))
                    .OrderBy(g => g.Host.Friend ? 0 : g.Host.Rival ? 1 : 2).ThenByDescending(g => g.Players).FirstOrDefault()?.Id
                    ?? throw new OnlineException("no_games", list.Games.Count == 0 ? "no open games on the server"
                        : $"{list.Games.Count} open game(s), none for this game version ({version})");
            }
            GameMsg game = await client.RequestAsync<GameMsg>(new JoinGameReq { GameId = gameId, Version = version });
            RelayTransport relay = RelayTransport.For(game.Relay, server);
            var link = new HybridTransport(relay);
            return new OnlineMatch(client, relay, link, NetMatchSession.Client(link, romKey, HostSlotOf(game.Game)), game.Game);
        }

        // back into the started match this player still has a seat in (O5 rejoin: the app restarted or the connection
        // dropped): the server gives the same relay slot with a new token (OnlineException "not_found" when there's none);
        // then Net.Join + WaitForStart as for any join: the host re-sends the START and a full snapshot
        public static async Task<OnlineMatch> RejoinAsync(OnlineClient client, Uri server, string romKey)
        {
            GameMsg game = await client.RequestAsync<GameMsg>(new JoinGameReq { GameId = "", Version = VersionKey(romKey) });
            RelayTransport relay = RelayTransport.For(game.Relay, server);
            var link = new HybridTransport(relay);
            return new OnlineMatch(client, relay, link, NetMatchSession.Client(link, romKey, HostSlotOf(game.Game)), game.Game);
        }

        // host: everyone's in; the match settings go out and the server shows the game as started
        public void Start()
        {
            Net.SendStart();
            _ = Client.RequestAsync<OkMsg>(new StartGameReq { Started = true });
        }

        // the ONLINE PLAY menus: when the match is disposed, its server connection goes back to them (TakeReturnedClientAsync)
        // once the server has this player out of the game, instead of closing. A new login of the same player would kick this
        // connection with its LeaveGame still unsent, and the server would keep the seat ("you're in a game already", N1)
        public bool ReturnClient { get; set; }
        // (the ONLINE PLAY menus set it once they're up: every match they start returns its connection)
        public static bool ReturnClientsToMenus { get; set; }
        private static Task<OnlineClient?>? s_closing;
        private static readonly object s_closingLock = new();

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            if (Current == this)
            {
                Current = null;
            }
            Client.EventReceived -= OnServerEvent;
            Net.Dispose();
            _usageTimer.Dispose();
            Task usage = ReportTurnUsageAsync(); // (the last report: its bytes were counted before the sockets close)
            Link.Dispose(); // (TURN, then the relay)
            // the server part waits on the network: never on the caller's thread (often the UI's)
            Task<OnlineClient?> closing = Task.Run(async () =>
            {
                try
                {
                    await usage;
                    await Client.RequestAsync<OkMsg>(new LeaveGameReq());
                }
                catch (Exception ex) when (ex is OnlineException or System.Net.WebSockets.WebSocketException
                    or OperationCanceledException or ObjectDisposedException or InvalidOperationException)
                {
                    MatchLog?.Invoke("leave game: " + ex.Message);
                }
                if (ReturnClient && Client.Connected)
                {
                    // (nobody took it back in time: closed after all)
                    _ = Task.Delay(ReturnedClientKeepMs).ContinueWith(_ => DropReturned(Client));
                    return Client;
                }
                await Client.DisposeAsync();
                return (OnlineClient?)null;
            });
            lock (s_closingLock)
            {
                s_closing = closing;
            }
        }

        private const int ReturnedClientKeepMs = 20_000;
        private static OnlineClient? s_taken;

        private static void DropReturned(OnlineClient client)
        {
            lock (s_closingLock)
            {
                if (ReferenceEquals(s_taken, client) || s_closing is not { IsCompleted: true } c || !ReferenceEquals(c.Result, client))
                {
                    return;
                }
                s_closing = null;
            }
            _ = client.DisposeAsync().AsTask();
        }

        // the menus' connection again after a match (ReturnClient): waits (up to waitMs: a request's own timeout is 15 s) for the match's leave to finish; the
        // connection if it's still up, else null (connect anew: the leave is done or given up on either way)
        public static async Task<OnlineClient?> TakeReturnedClientAsync(int waitMs = 16_000)
        {
            Task<OnlineClient?>? closing;
            lock (s_closingLock)
            {
                closing = s_closing;
            }
            if (closing == null)
            {
                return null;
            }
            await Task.WhenAny(closing, Task.Delay(waitMs));
            lock (s_closingLock)
            {
                if (!closing.IsCompleted || !ReferenceEquals(s_closing, closing))
                {
                    return null;
                }
                s_closing = null;
                OnlineClient? client = closing.Result;
                s_taken = client;
                if (client is { Connected: false })
                {
                    _ = client.DisposeAsync().AsTask();
                    return null;
                }
                return client;
            }
        }
    }
}
