using System;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;

namespace MphRecomp.Online
{
    // The game's connection to the online server (docs/ONLINE_PLAY.md): log in with the identity's signature, then send
    // requests and collect the server's pushes. Pushes land in Events (the game drains it on its own thread each frame);
    // EventReceived fires on the network thread for tools and tests.
    public sealed class OnlineClient : IAsyncDisposable
    {
        private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);

        private readonly ClientWebSocket _ws = new();
        private readonly ConcurrentDictionary<int, TaskCompletionSource<OnlineMsg>> _pending = new();
        private readonly SemaphoreSlim _sendLock = new(1, 1);
        private readonly CancellationTokenSource _cts = new();
        private Task? _loop;
        private int _nextId;

        public PlayerCard Me { get; private set; } = new();
        public bool Findable { get; private set; }
        // the server's version (its challenge's): what it understands (ReportReason.SinceServer)
        public string ServerVersion { get; private set; } = "";
        // server 0.5.0: online matches to play before typed chat reaches strangers (0 = open), an owner chat mute's end (unix, 0 = none)
        public int ChatLeft { get; private set; }
        public long ChatMutedUntil { get; private set; }
        // server 0.7.0: at login, a started match whose seat waits for this player (the hub's REJOIN); null = none
        public GameCard? Held { get; set; }
        // the chat received in the game this client is in (ChatEvents don't go through Events)
        public ChatLog Chat { get; } = new();

        // the server understands requests new in `version` (an older one drops a connection that sends one it doesn't know)
        public bool ServerAtLeast(string version) => System.Version.TryParse(ServerVersion, out var have)
            && System.Version.TryParse(version, out var want) && have >= want;
        public ConcurrentQueue<OnlineMsg> Events { get; } = new();
        public event Action<OnlineMsg>? EventReceived;
        public bool Connected => _ws.State == WebSocketState.Open && _loop is { IsCompleted: false };
        public string? CloseReason { get; private set; }

        private OnlineClient()
        {
        }

        // device: the scrambled device ID sent with the login (null = this device's DeviceTag; tests pass their own)
        public static async Task<OnlineClient> ConnectAsync(Uri server, OnlineIdentity identity, string name, string app,
            CancellationToken ct = default, string? device = null)
        {
            var client = new OnlineClient();
            try
            {
                client._ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
                await client._ws.ConnectAsync(server, ct);
                if (await OnlineProtocol.ReceiveAsync(client._ws, ct) is not ChallengeMsg challenge)
                {
                    throw new OnlineException("bad_server", "no challenge");
                }
                if (challenge.Proto != OnlineProtocol.Version)
                {
                    throw new OnlineException("old_version", $"server protocol {challenge.Proto}, this game speaks "
                        + $"{OnlineProtocol.Version}: update the game");
                }
                client.ServerVersion = challenge.Server;
                byte[] nonce = Convert.FromBase64String(challenge.Nonce);
                await client.SendAsync(new HelloMsg
                {
                    Key = Convert.ToBase64String(identity.PublicKey),
                    Sig = Convert.ToBase64String(identity.Sign(OnlineProtocol.LoginPayload(nonce))),
                    App = app,
                    Proto = OnlineProtocol.Version,
                    Name = name,
                    Device = device ?? DeviceTag.Current,
                }, ct);
                OnlineMsg? reply = await OnlineProtocol.ReceiveAsync(client._ws, ct);
                if (reply is ErrorMsg error)
                {
                    throw new OnlineException(error.Code, error.Text);
                }
                if (reply is not WelcomeMsg welcome)
                {
                    throw new OnlineException("bad_server", "no welcome");
                }
                client.Me = welcome.Me;
                client.Findable = welcome.Findable;
                client.ChatLeft = welcome.ChatLeft;
                client.ChatMutedUntil = welcome.ChatMutedUntil;
                client.Held = welcome.Held;
                client._loop = Task.Run(client.ReceiveLoop);
                return client;
            }
            catch
            {
                client._ws.Dispose();
                throw;
            }
        }

        public async Task<T> RequestAsync<T>(OnlineMsg request, CancellationToken ct = default) where T : OnlineMsg
        {
            int id = Interlocked.Increment(ref _nextId);
            request.Id = id;
            var reply = new TaskCompletionSource<OnlineMsg>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending[id] = reply;
            try
            {
                if (!Connected)
                {
                    throw new OnlineException("disconnected", CloseReason ?? "not connected");
                }
                await SendAsync(request, ct);
                OnlineMsg msg = await reply.Task.WaitAsync(RequestTimeout, ct);
                if (msg is ErrorMsg error)
                {
                    throw new OnlineException(error.Code, error.Text);
                }
                if (msg is not T typed)
                {
                    throw new OnlineException("bad_reply", $"expected {typeof(T).Name}, got {msg.GetType().Name}");
                }
                if (typed is PlayerMsg own && own.Player.Code == Me.Code)
                {
                    Me = own.Player;
                }
                return typed;
            }
            catch (TimeoutException)
            {
                throw new OnlineException("timeout", "the server didn't answer");
            }
            finally
            {
                _pending.TryRemove(id, out _);
            }
        }

        private async Task SendAsync(OnlineMsg msg, CancellationToken ct)
        {
            await _sendLock.WaitAsync(ct);
            try
            {
                await OnlineProtocol.SendAsync(_ws, msg, ct);
            }
            finally
            {
                _sendLock.Release();
            }
        }

        private async Task ReceiveLoop()
        {
            try
            {
                while (!_cts.IsCancellationRequested)
                {
                    OnlineMsg? msg = await OnlineProtocol.ReceiveAsync(_ws, _cts.Token);
                    if (msg == null)
                    {
                        CloseReason ??= _ws.CloseStatusDescription ?? "closed by the server";
                        break;
                    }
                    if (msg.Id is int id && _pending.TryGetValue(id, out var waiting))
                    {
                        waiting.TrySetResult(msg);
                        continue;
                    }
                    if (msg is KickedEvent kicked)
                    {
                        CloseReason = kicked.Reason;
                    }
                    if (msg is ChatEvent chat)
                    {
                        Chat.Add(chat);
                        EventReceived?.Invoke(msg);
                        continue;
                    }
                    Events.Enqueue(msg);
                    EventReceived?.Invoke(msg);
                }
            }
            catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or OnlineException)
            {
                CloseReason ??= ex.Message;
            }
            foreach (var waiting in _pending.Values)
            {
                waiting.TrySetResult(new ErrorMsg { Code = "disconnected", Text = CloseReason ?? "disconnected" });
            }
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                if (_ws.State == WebSocketState.Open)
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", timeout.Token);
                }
            }
            catch (Exception ex) when (ex is WebSocketException or OperationCanceledException)
            {
            }
            _cts.Cancel();
            if (_loop != null)
            {
                try
                {
                    await _loop;
                }
                catch (Exception)
                {
                }
            }
            _ws.Dispose();
            _cts.Dispose();
        }
    }
}
