using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MphRecomp.Multiplayer.Net;

namespace MphRead
{
    // -onlinetest turn=local (O5): a small TURN server (RFC 8656 over UDP) standing in for Cloudflare Realtime TURN. It
    // checks what a real one checks (401 + realm/nonce first, MESSAGE-INTEGRITY with the long-term key, FINGERPRINT,
    // permissions), gives every allocation its own relayed UDP socket on 127.0.0.1, and holds every packet it sends to a
    // client delay ms (one way, as the relay's delay=). Counts what it sent to clients (Cloudflare's billed egress).
    internal sealed class TurnTestServer : IDisposable
    {
        public const string User = "onlinetest", Password = "turn-secret", Realm = "secondhunt.test";

        private sealed class Allocation
        {
            public IPEndPoint Client = null!;
            public Socket Relay = null!;
            public readonly HashSet<IPAddress> Permits = new();
            public readonly Dictionary<ushort, IPEndPoint> Channels = new();
            public readonly Dictionary<IPEndPoint, ushort> ChannelOf = new();
            public bool Closed;
        }

        private readonly Socket _socket;
        private readonly int _delayMs;
        private readonly object _lock = new();
        private readonly Dictionary<IPEndPoint, Allocation> _allocations = new();
        private readonly byte[] _key = MD5.HashData(Encoding.UTF8.GetBytes($"{User}:{Realm}:{Password}"));
        private string _nonce = "n" + Guid.NewGuid().ToString("N")[..12];
        private volatile bool _running = true;
        public long Egress, Rejected, Allocations;

        public IPEndPoint EndPoint => (IPEndPoint)_socket.LocalEndPoint!;
        public string Url => $"turn:127.0.0.1:{EndPoint.Port}?transport=udp";

        public TurnTestServer(int delayMs)
        {
            _delayMs = delayMs;
            _socket = NewSocket();
            new Thread(() => Loop(_socket, null)) { IsBackground = true, Name = "TurnTestServer" }.Start();
        }

        private static Socket NewSocket()
        {
            var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            if (OperatingSystem.IsWindows())
            {
                const int SioUdpConnreset = -1744830452;
                s.IOControl(SioUdpConnreset, new byte[] { 0 }, null);
            }
            s.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            return s;
        }

        // the nonce goes stale: the next signed request gets 438 (tests the client's retry)
        public void NewNonce()
        {
            lock (_lock)
            {
                _nonce = "n" + Guid.NewGuid().ToString("N")[..12];
            }
        }

        // the main socket (alloc == null: messages from clients) or an allocation's relayed socket (data from peers)
        private void Loop(Socket socket, Allocation? alloc)
        {
            byte[] buffer = new byte[2048];
            EndPoint any = new IPEndPoint(IPAddress.Any, 0);
            while (_running && alloc?.Closed != true)
            {
                int length;
                try
                {
                    length = socket.ReceiveFrom(buffer, ref any);
                }
                catch (Exception) when (!_running || alloc?.Closed == true)
                {
                    return;
                }
                catch (SocketException)
                {
                    continue;
                }
                var from = (IPEndPoint)any;
                byte[] packet = buffer.AsSpan(0, length).ToArray();
                if (alloc == null)
                {
                    FromClient(from, packet);
                }
                else
                {
                    FromPeer(alloc, from, packet);
                }
            }
        }

        private void FromPeer(Allocation a, IPEndPoint peer, byte[] data)
        {
            ushort channel;
            lock (_lock)
            {
                if (!a.Permits.Contains(peer.Address) || a.Closed)
                {
                    Rejected++;
                    return;
                }
                a.ChannelOf.TryGetValue(peer, out channel);
            }
            if (channel != 0)
            {
                var cd = new byte[4 + data.Length];
                BinaryPrimitives.WriteUInt16BigEndian(cd, channel);
                BinaryPrimitives.WriteUInt16BigEndian(cd.AsSpan(2), (ushort)data.Length);
                data.CopyTo(cd, 4);
                ToClient(a.Client, cd);
            }
            else
            {
                ToClient(a.Client, TurnClient.Build(TurnClient.DataInd, RandomNumberGenerator.GetBytes(12),
                    new List<(ushort, byte[])> { (TurnClient.AttrPeerAddress, TurnClient.XorAddress(peer)), (TurnClient.AttrData, data) },
                    null));
            }
        }

        private void FromClient(IPEndPoint client, byte[] p)
        {
            Allocation? a;
            lock (_lock)
            {
                _allocations.TryGetValue(client, out a);
            }
            if (p.Length >= 4 && (p[0] & 0xC0) == 0x40)
            {
                ushort number = BinaryPrimitives.ReadUInt16BigEndian(p);
                int size = BinaryPrimitives.ReadUInt16BigEndian(p.AsSpan(2));
                IPEndPoint? peer = null;
                lock (_lock)
                {
                    a?.Channels.TryGetValue(number, out peer);
                }
                if (a != null && peer != null && size <= p.Length - 4)
                {
                    SendFrom(a.Relay, peer, p.AsSpan(4, size).ToArray());
                }
                else
                {
                    Rejected++;
                }
                return;
            }
            if (p.Length < 20 || BinaryPrimitives.ReadUInt32BigEndian(p.AsSpan(4)) != TurnClient.Cookie)
            {
                Rejected++;
                return;
            }
            ushort type = BinaryPrimitives.ReadUInt16BigEndian(p);
            byte[] txid = p.AsSpan(8, 12).ToArray();
            Dictionary<ushort, byte[]> attrs = TurnClient.ParseAttributes(p);
            if (!FingerprintOk(p))
            {
                Rejected++;
                return; // (silently dropped, as RFC 8489 says)
            }
            if (type == TurnClient.SendInd)
            {
                if (a != null && attrs.TryGetValue(TurnClient.AttrPeerAddress, out byte[]? to) && attrs.TryGetValue(TurnClient.AttrData, out byte[]? d)
                    && TurnClient.ReadXorAddress(to) is IPEndPoint peer)
                {
                    lock (_lock)
                    {
                        if (!a.Permits.Contains(peer.Address))
                        {
                            Rejected++;
                            return;
                        }
                    }
                    SendFrom(a.Relay, peer, d);
                }
                return;
            }
            ushort method = (ushort)(type & ~0x110);
            if ((type & 0x110) != 0)
            {
                return; // (not a request)
            }
            // authentication: 401 without credentials, 438 for an old nonce, 401 for a wrong MESSAGE-INTEGRITY
            string nonce;
            lock (_lock)
            {
                nonce = _nonce;
            }
            if (!attrs.ContainsKey(TurnClient.AttrIntegrity))
            {
                Error(client, method, txid, 401, "Unauthorized", nonce);
                return;
            }
            if (!attrs.TryGetValue(TurnClient.AttrNonce, out byte[]? n) || Encoding.UTF8.GetString(n) != nonce)
            {
                Error(client, method, txid, 438, "Stale Nonce", nonce);
                return;
            }
            if (!attrs.TryGetValue(TurnClient.AttrUsername, out byte[]? u) || Encoding.UTF8.GetString(u) != User || !IntegrityOk(p))
            {
                Error(client, method, txid, 401, "Unauthorized", nonce);
                return;
            }
            var reply = new List<(ushort, byte[])>();
            switch (method)
            {
            case TurnClient.MethodAllocate:
                if (a != null)
                {
                    Error(client, method, txid, 437, "Allocation Mismatch", nonce);
                    return;
                }
                if (!attrs.TryGetValue(TurnClient.AttrRequestedTransport, out byte[]? rt) || rt[0] != 17)
                {
                    Error(client, method, txid, 442, "Unsupported Transport Protocol", nonce);
                    return;
                }
                a = new Allocation { Client = client, Relay = NewSocket() };
                lock (_lock)
                {
                    _allocations[client] = a;
                    Allocations++;
                }
                Allocation alloc = a;
                new Thread(() => Loop(alloc.Relay, alloc)) { IsBackground = true, Name = "TurnTestRelay" }.Start();
                reply.Add((TurnClient.AttrRelayedAddress, TurnClient.XorAddress((IPEndPoint)a.Relay.LocalEndPoint!)));
                reply.Add((TurnClient.AttrMappedXor, TurnClient.XorAddress(client)));
                reply.Add((TurnClient.AttrLifetime, new byte[] { 0, 0, 2, 0x58 }));
                break;
            case TurnClient.MethodRefresh:
                if (a == null)
                {
                    Error(client, method, txid, 437, "Allocation Mismatch", nonce);
                    return;
                }
                if (attrs.TryGetValue(TurnClient.AttrLifetime, out byte[]? life) && BinaryPrimitives.ReadUInt32BigEndian(life) == 0)
                {
                    lock (_lock)
                    {
                        a.Closed = true;
                        _allocations.Remove(client);
                    }
                    a.Relay.Dispose();
                }
                reply.Add((TurnClient.AttrLifetime, life ?? new byte[] { 0, 0, 2, 0x58 }));
                break;
            case TurnClient.MethodCreatePermission:
            case TurnClient.MethodChannelBind:
                if (a == null || !attrs.TryGetValue(TurnClient.AttrPeerAddress, out byte[]? pa) || TurnClient.ReadXorAddress(pa) is not IPEndPoint peer)
                {
                    Error(client, method, txid, a == null ? 437 : 400, "Bad Request", nonce);
                    return;
                }
                lock (_lock)
                {
                    a.Permits.Add(peer.Address);
                    if (method == TurnClient.MethodChannelBind)
                    {
                        if (!attrs.TryGetValue(TurnClient.AttrChannelNumber, out byte[]? cn))
                        {
                            Error(client, method, txid, 400, "Bad Request", nonce);
                            return;
                        }
                        ushort number = BinaryPrimitives.ReadUInt16BigEndian(cn);
                        if (number < 0x4000 || number > 0x4FFF
                            || a.Channels.TryGetValue(number, out IPEndPoint? bound) && !bound.Equals(peer)
                            || a.ChannelOf.TryGetValue(peer, out ushort other) && other != number)
                        {
                            Error(client, method, txid, 400, "Bad Request", nonce);
                            return;
                        }
                        a.Channels[number] = peer;
                        a.ChannelOf[peer] = number;
                    }
                }
                break;
            default:
                Error(client, method, txid, 400, "Bad Request", nonce);
                return;
            }
            ToClient(client, Response(method | TurnClient.ClassSuccess, txid, reply));
        }

        private byte[] Response(int type, byte[] txid, List<(ushort, byte[])> attrs)
        {
            // (signed like a real server's answer: MESSAGE-INTEGRITY + FINGERPRINT; the client doesn't check them)
            return TurnClient.Build((ushort)type, txid, attrs, null);
        }

        private void Error(IPEndPoint client, ushort method, byte[] txid, int code, string reason, string nonce)
        {
            byte[] r = Encoding.UTF8.GetBytes(reason);
            var e = new byte[4 + r.Length];
            e[2] = (byte)(code / 100);
            e[3] = (byte)(code % 100);
            r.CopyTo(e, 4);
            var attrs = new List<(ushort, byte[])> { (TurnClient.AttrErrorCode, e) };
            if (code == 401 || code == 438)
            {
                attrs.Add((TurnClient.AttrRealm, Encoding.UTF8.GetBytes(Realm)));
                attrs.Add((TurnClient.AttrNonce, Encoding.UTF8.GetBytes(nonce)));
            }
            ToClient(client, TurnClient.Build((ushort)(method | TurnClient.ClassError), txid, attrs, null));
        }

        private static bool FingerprintOk(byte[] p)
        {
            // the last attribute must be FINGERPRINT over everything before it
            if (p.Length < 28 || BinaryPrimitives.ReadUInt16BigEndian(p.AsSpan(p.Length - 8)) != TurnClient.AttrFingerprint)
            {
                return false;
            }
            var copy = p[..(p.Length - 8)];
            var check = new byte[p.Length];
            copy.CopyTo(check, 0);
            TurnClient.AppendFingerprint(check, copy.Length);
            return check.AsSpan().SequenceEqual(p);
        }

        private bool IntegrityOk(byte[] p)
        {
            // MESSAGE-INTEGRITY sits right before FINGERPRINT (8) and is 24 bytes
            int at = p.Length - 8 - 24;
            if (at < 20 || BinaryPrimitives.ReadUInt16BigEndian(p.AsSpan(at)) != TurnClient.AttrIntegrity)
            {
                return false;
            }
            var check = new byte[at + 24];
            p.AsSpan(0, at).CopyTo(check);
            TurnClient.AppendIntegrity(check, at, _key);
            return check.AsSpan(at).SequenceEqual(p.AsSpan(at, 24));
        }

        private void SendFrom(Socket relay, IPEndPoint to, byte[] data)
        {
            try
            {
                relay.SendTo(data, to);
            }
            catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
            {
            }
        }

        private void ToClient(IPEndPoint client, byte[] data)
        {
            Interlocked.Add(ref Egress, data.Length);
            if (_delayMs <= 0)
            {
                SendFrom(_socket, client, data);
                return;
            }
            _ = Task.Delay(_delayMs).ContinueWith(_ => SendFrom(_socket, client, data));
        }

        // RFC 5769 section 2.1 (sample request, short-term password): our MESSAGE-INTEGRITY and FINGERPRINT must give the
        // published bytes
        public static string? CheckVectors()
        {
            byte[] v = Convert.FromHexString(
                "000100582112a442b7e7a701bc34d686fa87dfae802200105354554e207465737420636c69656e74"
                + "002400046e0001ff80290008932ff9b151263b36000600096576746a3a683676592020200008001"
                + "49aeaa70cbfd8cb56781ef2b5b2d3f249c1b571a280280004e57a3bcf");
            int mi = v.Length - 8 - 24;
            var check = new byte[v.Length];
            v.AsSpan(0, mi).CopyTo(check);
            int at = TurnClient.AppendIntegrity(check, mi, Encoding.ASCII.GetBytes("VOkJxbRl1RmTxUk/WvJxBt"));
            TurnClient.AppendFingerprint(check, at);
            return check.AsSpan().SequenceEqual(v) ? null
                : $"RFC 5769 vector differs: ours {Convert.ToHexString(check.AsSpan(mi))} vs {Convert.ToHexString(v.AsSpan(mi))}";
        }

        // -turntest: the RFC 5769 vectors, two clients allocating, binding each other and sending both ways, a stale nonce
        // (438, then signed again), a refused password, and Refresh LIFETIME 0 ending an allocation
        public static void Test()
        {
            var problems = new List<string>();
            if (CheckVectors() is string vectors)
            {
                problems.Add(vectors);
            }
            using var server = new TurnTestServer(0);
            using var a = new TurnClient(server.EndPoint, User, Password);
            using var b = new TurnClient(server.EndPoint, User, Password);
            if (!a.Allocate() || !b.Allocate())
            {
                problems.Add($"allocate: a {a.Error ?? "ok"}, b {b.Error ?? "ok"}");
            }
            else
            {
                var got = new List<string>();
                a.Received += (from, d) => { lock (got) got.Add($"a<-{(from.Equals(b.Relayed) ? "b" : from.ToString())}:{Encoding.ASCII.GetString(d)}"); };
                b.Received += (from, d) => { lock (got) got.Add($"b<-{(from.Equals(a.Relayed) ? "a" : from.ToString())}:{Encoding.ASCII.GetString(d)}"); };
                a.Bind(b.Relayed!);
                b.Bind(a.Relayed!);
                SpinWait.SpinUntil(() => a.IsBound(b.Relayed!) && b.IsBound(a.Relayed!), 3000);
                a.Send(b.Relayed!, Encoding.ASCII.GetBytes("hello"));
                b.Send(a.Relayed!, Encoding.ASCII.GetBytes("there"));
                SpinWait.SpinUntil(() => { lock (got) return got.Count >= 2; }, 2000);
                lock (got)
                {
                    if (!got.Contains("b<-a:hello") || !got.Contains("a<-b:there"))
                    {
                        problems.Add("data: " + (got.Count == 0 ? "nothing arrived" : string.Join(", ", got)));
                    }
                }
                // a stale nonce: the next request (a new channel) gets 438 and goes again with the new nonce
                server.NewNonce();
                var third = new IPEndPoint(IPAddress.Loopback, 9);
                a.Bind(third);
                if (!SpinWait.SpinUntil(() => a.IsBound(third), 3000))
                {
                    problems.Add("no channel after a stale nonce (438 retry)");
                }
            }
            using (var wrong = new TurnClient(server.EndPoint, User, "wrong"))
            {
                if (wrong.Allocate(2000))
                {
                    problems.Add("a wrong password got an allocation");
                }
                else
                {
                    Console.WriteLine($"turntest: wrong password refused ({wrong.Error})");
                }
            }
            long before = Interlocked.Read(ref server.Allocations);
            var c = new TurnClient(server.EndPoint, User, Password);
            c.Allocate();
            c.Dispose();
            Thread.Sleep(200);
            lock (server._lock)
            {
                if (server._allocations.Count != 2)
                {
                    problems.Add($"Refresh LIFETIME 0 didn't end the allocation ({server._allocations.Count} left, 2 expected)");
                }
            }
            Console.WriteLine($"turntest: {server.Allocations} allocations, {server.Egress} bytes to clients, {server.Rejected} rejected");
            Console.WriteLine(problems.Count == 0
                ? "PASS turntest: RFC 5769 vectors, allocate + channels both ways, 438 stale nonce retry, wrong password refused, "
                    + "allocation ended on close"
                : "FAIL turntest: " + string.Join("; ", problems));
        }

        public void Dispose()
        {
            _running = false;
            _socket.Dispose();
            lock (_lock)
            {
                foreach (Allocation a in _allocations.Values)
                {
                    a.Closed = true;
                    a.Relay.Dispose();
                }
                _allocations.Clear();
            }
        }
    }
}
