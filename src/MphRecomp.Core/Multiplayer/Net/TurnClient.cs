using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace MphRecomp.Multiplayer.Net
{
    // A minimal TURN client (O5, owner decision 18): RFC 8656 over UDP with the STUN message format of RFC 8489. One
    // allocation on a TURN server (Cloudflare Realtime: short-lived credentials our server mints), a channel to each peer's
    // relayed address (ChannelBind also installs the permission), ChannelData both ways. No ICE, no TCP/TLS, no IPv6
    // allocations: HybridTransport keeps the Chicago relay running beside it and falls back to it on any trouble. Only the
    // relayed address is ever shared with a peer (never this device's own address).
    public sealed class TurnClient : IDisposable
    {
        public delegate void DataHandler(IPEndPoint peer, ReadOnlySpan<byte> data);

        // STUN/TURN wire constants (RFC 8489 section 5/18, RFC 8656 section 18)
        public const uint Cookie = 0x2112A442;
        public const ushort MethodAllocate = 0x003, MethodRefresh = 0x004, SendInd = 0x016, DataInd = 0x017, MethodCreatePermission = 0x008,
            MethodChannelBind = 0x009;
        public const ushort ClassSuccess = 0x100, ClassError = 0x110;
        public const ushort AttrMappedAddress = 0x0001, AttrUsername = 0x0006, AttrIntegrity = 0x0008, AttrErrorCode = 0x0009,
            AttrChannelNumber = 0x000C, AttrLifetime = 0x000D, AttrPeerAddress = 0x0012, AttrData = 0x0013, AttrRealm = 0x0014,
            AttrNonce = 0x0015, AttrRelayedAddress = 0x0016, AttrRequestedTransport = 0x0019, AttrMappedXor = 0x0020,
            AttrSoftware = 0x8022, AttrFingerprint = 0x8028;
        public const uint FingerprintXor = 0x5354554E;
        private const int Lifetime = 600; // s, asked for; refreshed every RefreshMs
        private const int RefreshMs = 240_000; // allocation + channels (a channel lasts 600 s, its permission 300 s)
        private const int RtoMs = 500, MaxTries = 6;

        private sealed class Pending
        {
            public ushort Method;
            public byte[] Packet = Array.Empty<byte>();
            public List<(ushort, byte[])> Attrs = new();
            public long SentAt;
            public int Tries;
            public bool Authed;
            public Action<bool, Dictionary<ushort, byte[]>>? Done;
        }

        private sealed class Channel
        {
            public ushort Number;
            public IPEndPoint Peer = null!;
            public bool Bound;
            public long BoundAt;
            public bool Binding;
        }

        private readonly Socket _socket;
        private readonly string _username;
        private readonly string _password;
        private readonly object _lock = new();
        private readonly Dictionary<string, Pending> _pending = new();
        private readonly Dictionary<IPEndPoint, Channel> _byPeer = new();
        private readonly Dictionary<ushort, Channel> _byNumber = new();
        private readonly Thread _receiver;
        private readonly Timer _timer;
        private readonly ManualResetEventSlim _allocated = new();
        private readonly byte[] _sendBuffer = new byte[4 + 1500];
        private string? _realm;
        private string? _nonce;
        private byte[]? _key;
        private long _refreshedAt;
        private long _bytesIn;
        private long _bytesOut;
        private volatile bool _running = true;

        public IPEndPoint Server { get; }
        // this allocation's relayed address on the TURN server (the one thing peers learn), null until allocated
        public IPEndPoint? Relayed { get; private set; }
        // the allocation broke (refresh refused, server gone): HybridTransport stops using it
        public bool Failed { get; private set; }
        // tests: this device's TURN sends are lost (a network change left the allocation unreachable from here) while
        // packets still arrive, until set back
        public volatile bool SendOffline;
        public string? Error { get; private set; }
        // every byte received from the TURN server (what Cloudflare bills: its egress to this client, TURN overhead included)
        public long BytesIn => Interlocked.Read(ref _bytesIn);
        public long BytesOut => Interlocked.Read(ref _bytesOut);
        // raised on the receive thread
        public event DataHandler? Received;

        public TurnClient(IPEndPoint server, string username, string password)
        {
            Server = server;
            _username = username;
            _password = password;
            _socket = new Socket(server.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
            if (OperatingSystem.IsWindows())
            {
                const int SioUdpConnreset = -1744830452;
                _socket.IOControl(SioUdpConnreset, new byte[] { 0 }, null);
            }
            _socket.Bind(new IPEndPoint(server.AddressFamily == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Any : IPAddress.Any, 0));
            _socket.Connect(server);
            _receiver = new Thread(ReceiveLoop) { IsBackground = true, Name = "MphTurnReceive" };
            _receiver.Start();
            _timer = new Timer(_ => Tick(), null, 100, 100);
        }

        // "turn:host:port?transport=udp" (the server's TurnCredsMsg.Urls) -> host + port (UDP only; null otherwise)
        public static (string Host, int Port)? ParseUrl(string url)
        {
            if (!url.StartsWith("turn:", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }
            string rest = url[5..];
            int q = rest.IndexOf('?');
            string query = q < 0 ? "" : rest[(q + 1)..];
            if (q >= 0)
            {
                rest = rest[..q];
            }
            if (query.Length > 0 && !query.Contains("transport=udp", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }
            int colon = rest.LastIndexOf(':');
            if (colon < 0 || rest.Contains(']') && colon < rest.IndexOf(']'))
            {
                return (rest.Trim('[', ']'), 3478);
            }
            return Int32.TryParse(rest[(colon + 1)..], out int port) ? (rest[..colon].Trim('[', ']'), port) : null;
        }

        // the first of the server's UDP TURN URLs that resolves to IPv4 (3478 first, then 443)
        public static IPEndPoint? Resolve(IEnumerable<string> urls)
        {
            foreach ((string host, int port) in urls.Select(ParseUrl).Where(u => u != null).Select(u => u!.Value)
                .OrderBy(u => u.Port == 3478 ? 0 : 1))
            {
                try
                {
                    IPAddress? a = IPAddress.TryParse(host, out IPAddress? literal) ? literal
                        : Dns.GetHostAddresses(host).FirstOrDefault(x => x.AddressFamily == AddressFamily.InterNetwork);
                    if (a != null)
                    {
                        return new IPEndPoint(a, port);
                    }
                }
                catch (SocketException)
                {
                }
            }
            return null;
        }

        // Allocate (401 with REALM + NONCE, then again with the long-term credentials); blocks up to timeoutMs
        public bool Allocate(int timeoutMs = 4000)
        {
            Request(MethodAllocate, new List<(ushort, byte[])>
            {
                (AttrRequestedTransport, new byte[] { 17, 0, 0, 0 }), // UDP
                (AttrLifetime, U32(Lifetime)),
            }, (ok, attrs) =>
            {
                if (ok && attrs.TryGetValue(AttrRelayedAddress, out byte[]? relayed))
                {
                    Relayed = ReadXorAddress(relayed);
                    Interlocked.Exchange(ref _refreshedAt, Environment.TickCount64);
                }
                else if (ok)
                {
                    Error = "no relayed address";
                }
                _allocated.Set();
            });
            _allocated.Wait(timeoutMs);
            if (Relayed == null)
            {
                Error ??= "no answer";
                Failed = true;
            }
            return Relayed != null;
        }

        // a channel to this peer's relayed address (ChannelBind also installs the permission); Send works once it's bound
        public void Bind(IPEndPoint peer)
        {
            Channel c;
            lock (_lock)
            {
                if (_byPeer.ContainsKey(peer))
                {
                    return;
                }
                if (_byNumber.Count >= 0x1000)
                {
                    return;
                }
                c = new Channel { Number = (ushort)(0x4000 + _byNumber.Count), Peer = peer };
                _byPeer[peer] = c;
                _byNumber[c.Number] = c;
            }
            SendBind(c);
        }

        public bool IsBound(IPEndPoint peer)
        {
            lock (_lock)
            {
                return _byPeer.TryGetValue(peer, out Channel? c) && c.Bound;
            }
        }

        private void SendBind(Channel c)
        {
            lock (_lock)
            {
                if (c.Binding)
                {
                    return;
                }
                c.Binding = true;
            }
            var number = new byte[4];
            BinaryPrimitives.WriteUInt16BigEndian(number, c.Number);
            Request(MethodChannelBind, new List<(ushort, byte[])> { (AttrChannelNumber, number), (AttrPeerAddress, XorAddress(c.Peer)) },
                (ok, _) =>
                {
                    lock (_lock)
                    {
                        c.Binding = false;
                        if (ok)
                        {
                            c.Bound = true;
                            c.BoundAt = Environment.TickCount64;
                        }
                    }
                });
        }

        // ChannelData to a bound peer (dropped otherwise: the relay carries it then)
        public bool Send(IPEndPoint peer, ReadOnlySpan<byte> data)
        {
            if (!_running || Failed || data.Length > _sendBuffer.Length - 4)
            {
                return false;
            }
            Channel? c;
            lock (_lock)
            {
                if (!_byPeer.TryGetValue(peer, out c) || !c.Bound)
                {
                    return false;
                }
            }
            lock (_sendBuffer)
            {
                BinaryPrimitives.WriteUInt16BigEndian(_sendBuffer, c.Number);
                BinaryPrimitives.WriteUInt16BigEndian(_sendBuffer.AsSpan(2), (ushort)data.Length);
                data.CopyTo(_sendBuffer.AsSpan(4));
                return SendRaw(_sendBuffer.AsSpan(0, 4 + data.Length));
            }
        }

        private bool SendRaw(ReadOnlySpan<byte> packet)
        {
            if (SendOffline)
            {
                return true; // (sent, as far as this device can tell)
            }
            try
            {
                _socket.Send(packet, SocketFlags.None);
                Interlocked.Add(ref _bytesOut, packet.Length);
                return true;
            }
            catch (SocketException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
            return false;
        }

        // ---------------------------------------------------------------- transactions

        private void Request(ushort method, List<(ushort, byte[])> attrs, Action<bool, Dictionary<ushort, byte[]>>? done)
        {
            var p = new Pending { Method = method, Attrs = attrs, Done = done };
            Transmit(p);
        }

        private void Transmit(Pending p)
        {
            byte[] txid = RandomNumberGenerator.GetBytes(12);
            byte[]? key;
            string? realm, nonce;
            lock (_lock)
            {
                key = _key;
                realm = _realm;
                nonce = _nonce;
                p.Authed = key != null;
                p.Packet = Build(p.Method, txid, p.Attrs, key == null ? null : (_username, realm!, nonce!, key));
                p.SentAt = Environment.TickCount64;
                p.Tries++;
                _pending[Convert.ToHexString(txid)] = p;
            }
            SendRaw(p.Packet);
        }

        private void Tick()
        {
            if (!_running)
            {
                return;
            }
            long now = Environment.TickCount64;
            var resend = new List<Pending>();
            var lost = new List<Pending>();
            lock (_lock)
            {
                foreach ((string id, Pending p) in _pending.ToList())
                {
                    if (now - p.SentAt < RtoMs << Math.Min(p.Tries - 1, 4))
                    {
                        continue;
                    }
                    if (p.Tries >= MaxTries)
                    {
                        _pending.Remove(id);
                        lost.Add(p);
                    }
                    else
                    {
                        resend.Add(p);
                    }
                }
            }
            foreach (Pending p in resend)
            {
                p.SentAt = now;
                p.Tries++;
                SendRaw(p.Packet); // (same transaction id: a retransmission)
            }
            foreach (Pending p in lost)
            {
                if (p.Method == MethodRefresh || p.Method == MethodAllocate)
                {
                    Error = $"no answer from the TURN server ({MethodName(p.Method)})";
                    Failed = true;
                }
                p.Done?.Invoke(false, new Dictionary<ushort, byte[]>());
            }
            if (Relayed != null && !Failed && now - Interlocked.Read(ref _refreshedAt) > RefreshMs)
            {
                Interlocked.Exchange(ref _refreshedAt, now);
                Request(MethodRefresh, new List<(ushort, byte[])> { (AttrLifetime, U32(Lifetime)) }, (ok, attrs) =>
                {
                    if (!ok)
                    {
                        Error = "the TURN allocation ended";
                        Failed = true;
                    }
                });
                List<Channel> channels;
                lock (_lock)
                {
                    channels = _byPeer.Values.ToList();
                }
                foreach (Channel c in channels)
                {
                    SendBind(c); // (refreshes the channel and its permission)
                }
            }
        }

        private void OnResponse(ReadOnlySpan<byte> packet, ushort type)
        {
            string id = Convert.ToHexString(packet.Slice(8, 12));
            Pending? p;
            lock (_lock)
            {
                if (!_pending.Remove(id, out p))
                {
                    return; // (a late answer to a retransmission)
                }
            }
            Dictionary<ushort, byte[]> attrs = ParseAttributes(packet);
            if ((type & ClassError) == ClassError)
            {
                int code = attrs.TryGetValue(AttrErrorCode, out byte[]? e) && e.Length >= 4 ? e[2] * 100 + e[3] : 0;
                // 401: the first request (realm + nonce to use); 438: the nonce went stale; either way once more, signed
                if ((code == 401 && !p.Authed || code == 438) && attrs.TryGetValue(AttrRealm, out byte[]? realm)
                    && attrs.TryGetValue(AttrNonce, out byte[]? nonce) && p.Tries < MaxTries)
                {
                    lock (_lock)
                    {
                        _realm = Encoding.UTF8.GetString(realm);
                        _nonce = Encoding.UTF8.GetString(nonce);
                        _key = MD5.HashData(Encoding.UTF8.GetBytes($"{_username}:{_realm}:{_password}"));
                    }
                    Transmit(p);
                    return;
                }
                if (p.Method == MethodAllocate)
                {
                    string reason = e != null && e.Length > 4 ? Encoding.UTF8.GetString(e.AsSpan(4)) : "";
                    Error = $"TURN {MethodName(p.Method)} refused: {code} {reason}".TrimEnd();
                }
                p.Done?.Invoke(false, attrs);
                return;
            }
            p.Done?.Invoke(true, attrs);
        }

        private void ReceiveLoop()
        {
            byte[] buffer = new byte[2048];
            while (_running)
            {
                int length;
                try
                {
                    length = _socket.Receive(buffer);
                }
                catch (Exception) when (!_running)
                {
                    return;
                }
                catch (SocketException)
                {
                    continue;
                }
                Interlocked.Add(ref _bytesIn, length);
                ReadOnlySpan<byte> packet = buffer.AsSpan(0, length);
                if (length >= 4 && (packet[0] & 0xC0) == 0x40)
                {
                    // ChannelData: channel number, length, data
                    ushort number = BinaryPrimitives.ReadUInt16BigEndian(packet);
                    int size = BinaryPrimitives.ReadUInt16BigEndian(packet[2..]);
                    Channel? c;
                    lock (_lock)
                    {
                        _byNumber.TryGetValue(number, out c);
                    }
                    if (c != null && size <= length - 4)
                    {
                        Received?.Invoke(c.Peer, packet.Slice(4, size));
                    }
                    continue;
                }
                if (length < 20 || (packet[0] & 0xC0) != 0 || BinaryPrimitives.ReadUInt32BigEndian(packet[4..]) != Cookie)
                {
                    continue;
                }
                ushort type = BinaryPrimitives.ReadUInt16BigEndian(packet);
                if (type == DataInd)
                {
                    // a peer's data before its channel was bound here
                    Dictionary<ushort, byte[]> attrs = ParseAttributes(packet);
                    if (attrs.TryGetValue(AttrPeerAddress, out byte[]? from) && attrs.TryGetValue(AttrData, out byte[]? data)
                        && ReadXorAddress(from) is IPEndPoint peer)
                    {
                        Received?.Invoke(peer, data);
                    }
                    continue;
                }
                if ((type & ClassSuccess) != 0)
                {
                    OnResponse(packet, type);
                }
            }
        }

        public void Dispose()
        {
            if (!_running)
            {
                return;
            }
            if (Relayed != null && !Failed)
            {
                // Refresh with LIFETIME 0 ends the allocation now (no answer awaited)
                byte[]? key;
                string? realm, nonce;
                lock (_lock)
                {
                    key = _key;
                    realm = _realm;
                    nonce = _nonce;
                }
                SendRaw(Build(MethodRefresh, RandomNumberGenerator.GetBytes(12), new List<(ushort, byte[])> { (AttrLifetime, U32(0)) },
                    key == null ? null : (_username, realm!, nonce!, key)));
            }
            _running = false;
            _timer.Dispose();
            _socket.Dispose();
            _allocated.Dispose();
        }

        // ---------------------------------------------------------------- the STUN message format

        private static byte[] U32(int value)
        {
            var b = new byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(b, (uint)value);
            return b;
        }

        private static string MethodName(ushort method) => method switch
        {
            MethodAllocate => "Allocate", MethodRefresh => "Refresh", MethodChannelBind => "ChannelBind", MethodCreatePermission => "CreatePermission",
            _ => $"0x{method:x3}",
        };

        // a request (class 0) or indication: header, attributes, then USERNAME/REALM/NONCE + MESSAGE-INTEGRITY when signed, and
        // FINGERPRINT last
        public static byte[] Build(ushort type, byte[] txid, List<(ushort Type, byte[] Value)> attrs,
            (string User, string Realm, string Nonce, byte[] Key)? auth)
        {
            var all = new List<(ushort Type, byte[] Value)>(attrs);
            if (auth is var (user, realm, nonce, _))
            {
                all.Add((AttrUsername, Encoding.UTF8.GetBytes(user)));
                all.Add((AttrRealm, Encoding.UTF8.GetBytes(realm)));
                all.Add((AttrNonce, Encoding.UTF8.GetBytes(nonce)));
            }
            int size = 20 + all.Sum(a => 4 + Pad4(a.Value.Length)) + (auth != null ? 24 : 0) + 8;
            var msg = new byte[size];
            BinaryPrimitives.WriteUInt16BigEndian(msg, type);
            BinaryPrimitives.WriteUInt32BigEndian(msg.AsSpan(4), Cookie);
            txid.CopyTo(msg, 8);
            int at = 20;
            foreach ((ushort t, byte[] v) in all)
            {
                BinaryPrimitives.WriteUInt16BigEndian(msg.AsSpan(at), t);
                BinaryPrimitives.WriteUInt16BigEndian(msg.AsSpan(at + 2), (ushort)v.Length);
                v.CopyTo(msg, at + 4);
                at += 4 + Pad4(v.Length);
            }
            if (auth != null)
            {
                at = AppendIntegrity(msg, at, auth.Value.Key);
            }
            AppendFingerprint(msg, at);
            return msg;
        }

        private static int Pad4(int n) => (n + 3) & ~3;

        // MESSAGE-INTEGRITY at `at`: HMAC-SHA1 over the message so far with the length field counting up to its end
        public static int AppendIntegrity(byte[] msg, int at, byte[] key)
        {
            BinaryPrimitives.WriteUInt16BigEndian(msg.AsSpan(2), (ushort)(at + 24 - 20));
            byte[] mac = HMACSHA1.HashData(key, msg.AsSpan(0, at));
            BinaryPrimitives.WriteUInt16BigEndian(msg.AsSpan(at), AttrIntegrity);
            BinaryPrimitives.WriteUInt16BigEndian(msg.AsSpan(at + 2), 20);
            mac.CopyTo(msg, at + 4);
            return at + 24;
        }

        // FINGERPRINT at `at`: CRC-32 of the message so far (length counting it) XOR 0x5354554E
        public static int AppendFingerprint(byte[] msg, int at)
        {
            BinaryPrimitives.WriteUInt16BigEndian(msg.AsSpan(2), (ushort)(at + 8 - 20));
            uint crc = Crc32(msg.AsSpan(0, at)) ^ FingerprintXor;
            BinaryPrimitives.WriteUInt16BigEndian(msg.AsSpan(at), AttrFingerprint);
            BinaryPrimitives.WriteUInt16BigEndian(msg.AsSpan(at + 2), 4);
            BinaryPrimitives.WriteUInt32BigEndian(msg.AsSpan(at + 4), crc);
            return at + 8;
        }

        private static readonly uint[] CrcTable = Enumerable.Range(0, 256).Select(n =>
        {
            uint c = (uint)n;
            for (int k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            }
            return c;
        }).ToArray();

        public static uint Crc32(ReadOnlySpan<byte> data)
        {
            uint c = 0xFFFFFFFF;
            foreach (byte b in data)
            {
                c = CrcTable[(c ^ b) & 0xFF] ^ (c >> 8);
            }
            return c ^ 0xFFFFFFFF;
        }

        public static Dictionary<ushort, byte[]> ParseAttributes(ReadOnlySpan<byte> packet)
        {
            var attrs = new Dictionary<ushort, byte[]>();
            int end = Math.Min(packet.Length, 20 + BinaryPrimitives.ReadUInt16BigEndian(packet[2..]));
            int at = 20;
            while (at + 4 <= end)
            {
                ushort t = BinaryPrimitives.ReadUInt16BigEndian(packet[at..]);
                int len = BinaryPrimitives.ReadUInt16BigEndian(packet[(at + 2)..]);
                if (at + 4 + len > end)
                {
                    break;
                }
                attrs.TryAdd(t, packet.Slice(at + 4, len).ToArray());
                at += 4 + Pad4(len);
            }
            return attrs;
        }

        // XOR-*-ADDRESS (IPv4; IPv6 would need the transaction id, which the addresses we use never do)
        public static byte[] XorAddress(IPEndPoint ep)
        {
            byte[] ip = ep.Address.MapToIPv4().GetAddressBytes();
            var v = new byte[8];
            v[1] = 0x01;
            BinaryPrimitives.WriteUInt16BigEndian(v.AsSpan(2), (ushort)(ep.Port ^ (Cookie >> 16)));
            BinaryPrimitives.WriteUInt32BigEndian(v.AsSpan(4), BinaryPrimitives.ReadUInt32BigEndian(ip) ^ Cookie);
            return v;
        }

        public static IPEndPoint? ReadXorAddress(byte[] v)
        {
            if (v.Length < 8 || v[1] != 0x01)
            {
                return null;
            }
            int port = BinaryPrimitives.ReadUInt16BigEndian(v.AsSpan(2)) ^ (int)(Cookie >> 16);
            var ip = new byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(ip, BinaryPrimitives.ReadUInt32BigEndian(v.AsSpan(4)) ^ Cookie);
            return new IPEndPoint(new IPAddress(ip), port);
        }
    }
}
