using System;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using MphRecomp.Online;

namespace MphRecomp.Multiplayer.Net
{
    // This device's slot in a game's relay room (RelayWire): one UDP socket to the relay, nothing to any other player
    // (no one sees anyone's address, and it works behind any home router: the relay answers the address it heard).
    // A keepalive goes out every second; its answer keeps the router's mapping open and measures the relay ping.
    // When the relay goes quiet (Wi-Fi off and on, Wi-Fi to mobile data) the socket is made again every few seconds: a
    // connected UDP socket keeps the address it started on, and the relay learns the new one from the next packet (the
    // token stays the same, so the match goes on: release test T1, rejoin).
    public sealed class RelayTransport : IDatagramTransport
    {
        private volatile Socket _socket;
        private readonly ulong _token;
        private readonly byte[] _sendBuffer = new byte[RelayWire.SendHeader + RelayWire.MaxPayload];
        private readonly Thread _receiver;
        private readonly Timer _keepAlive;
        private volatile bool _running = true;
        private long _pingSentAt;
        private int _pingMs = -1;
        private long _lastHeard = Environment.TickCount64;
        // the relay's last answer to our keepalive: our sends reach it (hearing the match alone doesn't say so: on Android a
        // socket made on a network that went away still receives on the new one while its sends go nowhere, release 0.2.0
        // o-wifi retest 10:04)
        private long _lastAnswered = Environment.TickCount64;
        // tests: sends from this socket are lost until the next new socket (an Android socket left on a network that went away)
        private volatile Socket? _deadSocket;
        private long _lastRebuild = Environment.TickCount64;
        private const long RebuildAfterMs = 3000;

        public int LocalSlot { get; }
        public int MaxPayload => RelayWire.MaxPayload;
        public event DatagramHandler? Received;
        // round trip to the relay (keepalive), -1 = no answer yet
        public int PingMs => _pingMs;
        // the relay answered in the last few seconds
        public bool RelayAlive => Environment.TickCount64 - Interlocked.Read(ref _lastHeard) < 3000;
        public IPEndPoint Relay { get; }
        // sockets made again after the relay went quiet
        public int Rebuilds { get; private set; }
        // tests: a device "off the network" (nothing sent, everything received dropped) until set back
        public volatile bool Offline;
        // tests: only this device's sends are lost (its uplink broke; it still hears the relay), until set back
        public volatile bool SendOffline;

        public RelayTransport(IPEndPoint relay, ulong token, int slot)
        {
            Relay = relay;
            _token = token;
            LocalSlot = slot;
            _socket = NewSocket(relay);
            _receiver = new Thread(ReceiveLoop) { IsBackground = true, Name = "MphRelayReceive" };
            _receiver.Start();
            _keepAlive = new Timer(_ => SendKeepAlive(), null, 0, 1000);
        }

        private static Socket NewSocket(IPEndPoint relay)
        {
            var socket = new Socket(relay.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
            try
            {
                if (OperatingSystem.IsWindows())
                {
                    // don't let an ICMP "port unreachable" end the receive loop
                    const int SioUdpConnreset = -1744830452;
                    socket.IOControl(SioUdpConnreset, new byte[] { 0 }, null);
                }
                socket.Bind(new IPEndPoint(relay.AddressFamily == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Any : IPAddress.Any, 0));
                socket.Connect(relay);
                return socket;
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }

        // the relay quiet for a while: a new socket (no network yet: the old one stays, tried again later)
        private void RebuildIfQuiet()
        {
            long now = Environment.TickCount64;
            long heard = now - Interlocked.Read(ref _lastHeard), answered = now - Interlocked.Read(ref _lastAnswered);
            if (!_running || (heard < RebuildAfterMs && answered < RebuildAfterMs) || now - _lastRebuild < RebuildAfterMs)
            {
                return;
            }
            _lastRebuild = now;
            if (Offline)
            {
                return; // (tests: no network, so no new socket either, as on a phone: the old one stays)
            }
            Socket fresh;
            try
            {
                fresh = NewSocket(Relay);
            }
            catch (SocketException)
            {
                return;
            }
            Socket old = _socket;
            lock (_sendBuffer)
            {
                _socket = fresh;
            }
            Rebuilds++;
            Console.WriteLine($"relay: new socket #{Rebuilds} (heard {heard} ms ago, keepalive answered {answered} ms ago)");
            old.Dispose(); // (its receive ends; the loop goes on with the new one)
        }

        // the relay the server named for this game; an empty host = the server's own host (serverUri)
        public static RelayTransport For(RelayInfo info, Uri serverUri)
        {
            string host = String.IsNullOrEmpty(info.Host) ? serverUri.Host : info.Host;
            IPAddress[] addresses = IPAddress.TryParse(host, out IPAddress? literal) ? new[] { literal }
                : Dns.GetHostAddresses(host);
            IPAddress address = addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork) ?? addresses[0];
            return new RelayTransport(new IPEndPoint(address, info.Port), RelayWire.ParseToken(info.Token), info.Slot);
        }

        public void Send(int slot, ReadOnlySpan<byte> payload) => SendTo((byte)slot, payload);

        public void SendAll(ReadOnlySpan<byte> payload) => SendTo(RelayWire.ToAll, payload);

        private void SendTo(byte to, ReadOnlySpan<byte> payload)
        {
            if (!_running || payload.Length > RelayWire.MaxPayload)
            {
                return;
            }
            lock (_sendBuffer)
            {
                RelayWire.WriteHeader(_sendBuffer, _token, to);
                payload.CopyTo(_sendBuffer.AsSpan(RelayWire.SendHeader));
                if (Offline || SendOffline || ReferenceEquals(_socket, _deadSocket))
                {
                    return;
                }
                try
                {
                    _socket.Send(_sendBuffer, 0, RelayWire.SendHeader + payload.Length, SocketFlags.None);
                }
                catch (SocketException)
                {
                }
                catch (ObjectDisposedException)
                {
                }
            }
        }

        // tests: this socket's sends stop reaching the relay (its receives go on) until a new socket is made
        public void KillSocketSends() => _deadSocket = _socket;

        private void SendKeepAlive()
        {
            RebuildIfQuiet();
            Interlocked.Exchange(ref _pingSentAt, Environment.TickCount64);
            SendTo(RelayWire.KeepAlive, ReadOnlySpan<byte>.Empty);
        }

        private void ReceiveLoop()
        {
            byte[] buffer = new byte[RelayWire.ReceiveHeader + RelayWire.MaxPayload + 64];
            while (_running)
            {
                int length;
                Socket socket = _socket;
                try
                {
                    length = socket.Receive(buffer);
                }
                catch (Exception) when (!_running)
                {
                    return;
                }
                catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
                {
                    if (ReferenceEquals(socket, _socket))
                    {
                        Thread.Sleep(50); // (no network: don't spin on the error)
                    }
                    continue; // (or a new socket: RebuildIfQuiet)
                }
                if (Offline || length < RelayWire.ReceiveHeader || buffer[0] != RelayWire.Magic)
                {
                    continue;
                }
                Interlocked.Exchange(ref _lastHeard, Environment.TickCount64);
                if (buffer[1] == RelayWire.KeepAlive)
                {
                    _pingMs = (int)(Environment.TickCount64 - Interlocked.Read(ref _pingSentAt));
                    Interlocked.Exchange(ref _lastAnswered, Environment.TickCount64);
                    continue;
                }
                Received?.Invoke(buffer[1], buffer.AsSpan(RelayWire.ReceiveHeader, length - RelayWire.ReceiveHeader));
            }
        }

        public void Dispose()
        {
            if (!_running)
            {
                return;
            }
            _keepAlive.Dispose();
            _running = false;
            _socket.Dispose();
        }
    }
}
