using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using MphRecomp.Frontend;
using MphRecomp.Multiplayer.Net;
using MphRecomp.Online;

namespace MphRecomp.Replay
{
    // The replay link of a LAN / online match (docs/ONLINE_PLAY.md O10, docs/REPLAY_MODE.md R6), over the match session's side
    // channel (IMatchNet.SendSide: its MatchNetSide.MsgSide packets, which the match protocols pass by untouched):
    //   each guest, as the match starts: its own identity (friend code, NAME#tag, license card) to the host, and whether it
    //   keeps replays; the host puts every seat's identity in its recording.
    //   the host, as the match ends: its recording -- the only exact one: it ran the whole match -- to each guest that keeps
    //   replays, which saves it as its own copy (ReplayFile.OwnSeat = its seat).
    //   a guest leaving a match that's still going (0.3.6, 2026-10-19: they get the partial replay):
    //   a request for the recording so far; the host sends it cut short (Complete = false) while the match goes on, and the
    //   guest's screen hold waits for it as for a whole one. An older host ignores the request (the guest gives up waiting).
    // Everything goes in pieces of up to ChunkBytes, each acked and sent again until it is (the relay or a LAN loses some),
    // pumped by its own timer, so a transfer goes on while the screens change. Sending gives up after GiveUpMs.
    public sealed class ReplayLink : IDisposable
    {
        public const int ChunkBytes = 1100;
        private const byte TypeChunk = 1, TypeAck = 2;
        private const byte KindIdentity = 1, KindReplay = 2, KindRequest = 3;
        private const int Window = 48, ResendMs = 250, PumpMs = 40, GiveUpMs = 20000, MaxChunks = 8192;

        private readonly IMatchNet _net;
        private readonly object _lock = new();
        private readonly List<Outgoing> _out = new();
        private readonly Dictionary<(int Seat, uint Id), Incoming> _in = new();
        private readonly Dictionary<int, ReplaySeatIdentity> _identities = new();
        private readonly HashSet<int> _wantReplay = new();
        private readonly Timer _timer;
        private uint _nextId = (uint)Environment.TickCount;
        private bool _disposed;

        // host: a guest's identity came in (its seat = the side channel's, whatever it says)
        public event Action<ReplaySeatIdentity>? IdentityReceived;
        // host: a guest leaving mid-match asked for the recording so far (the receive thread: the recording is the game
        // thread's, so the host notes it and answers from there)
        public event Action<int>? ReplayRequested;
        // guest: the host's recording came in (the file's bytes, not yet checked)
        public event Action<byte[]>? ReplayReceived;

        public bool IsHost => _net.IsHost;

        // the newest network match's link (T3: the host's RESULTS reads its seats' friend codes for the head-to-head report)
        public static ReplayLink? Latest { get; private set; }

        public ReplayLink(IMatchNet net)
        {
            Latest = this;
            _net = net;
            _net.SideReceived += OnReceived;
            _timer = new Timer(_ => Pump(), null, PumpMs, PumpMs);
        }

        // host: the identities the guests sent so far, by seat
        public IReadOnlyList<ReplaySeatIdentity> Identities
        {
            get { lock (_lock) return _identities.Values.ToList(); }
        }

        // host: the guests that keep replays (asked for the recording)
        public IReadOnlyList<int> SeatsWantingReplay
        {
            get { lock (_lock) return _wantReplay.ToList(); }
        }

        // something is still on its way out (not every piece acked, and not given up)
        public bool Sending
        {
            get { lock (_lock) return _out.Count > 0; }
        }

        // pieces of incoming recordings received so far ("have/count" each), for the log when a wait gives up
        public string Progress
        {
            get { lock (_lock) return _in.Count == 0 ? "nothing" : string.Join(", ", _in.Values.Select(i => $"{i.Have}/{i.Parts.Length}{(i.Done ? " done" : "")}")); }
        }

        // guest: this player's identity (and whether to send the recording back) to the host
        public void SendIdentity(ReplaySeatIdentity id, bool wantReplay)
        {
            var ms = new MemoryStream();
            using (var w = new BinaryWriter(ms))
            {
                w.Write((byte)1); // (version)
                w.Write(wantReplay);
                WriteIdentity(w, id);
            }
            Queue(0, KindIdentity, ms.ToArray());
        }

        // guest, leaving a match that's still going: the recording so far, please
        public void RequestReplay() => Queue(0, KindRequest, new byte[] { 1 });

        // host: the recording to a guest's seat
        public void SendReplay(int seat, byte[] file) => Queue(seat, KindReplay, file);

        private void Queue(int seat, byte kind, byte[] blob)
        {
            int count = Math.Max(1, (blob.Length + ChunkBytes - 1) / ChunkBytes);
            if (count > MaxChunks) throw new InvalidOperationException($"replay link: {blob.Length} bytes is too big to send");
            var o = new Outgoing(seat, _nextId++, kind, blob, count);
            lock (_lock)
            {
                if (_disposed) return;
                _out.Add(o);
            }
            Pump();
        }

        private void Pump()
        {
            var packets = new List<(int Seat, byte[] Packet)>();
            lock (_lock)
            {
                if (_disposed) return;
                long now = Environment.TickCount64;
                for (int k = _out.Count - 1; k >= 0; k--)
                {
                    Outgoing o = _out[k];
                    if (o.Left == 0 || now - o.Started > GiveUpMs)
                    {
                        if (o.Left > 0) Console.Error.WriteLine($"replay link: gave up sending to seat {o.Seat} ({o.Left} of {o.Count} pieces unacked)");
                        _out.RemoveAt(k);
                        continue;
                    }
                    int inFlight = 0;
                    for (int i = 0; i < o.Count && inFlight < Window; i++)
                    {
                        if (o.Acked[i]) continue;
                        inFlight++;
                        if (now - o.SentAt[i] < ResendMs) continue;
                        o.SentAt[i] = now;
                        packets.Add((o.Seat, ChunkPacket(o, i)));
                    }
                }
            }
            foreach ((int seat, byte[] packet) in packets)
            {
                try
                {
                    _net.SendSide(seat, packet);
                }
                catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException or IOException
                    or System.Net.Sockets.SocketException)
                {
                    return; // the session closed under us: nothing more goes out
                }
            }
        }

        // [TypeChunk][u32 id][u8 kind][u16 index][u16 count][data]
        private static byte[] ChunkPacket(Outgoing o, int index)
        {
            int start = index * ChunkBytes;
            int length = Math.Min(ChunkBytes, o.Blob.Length - start);
            byte[] p = new byte[10 + length];
            p[0] = TypeChunk;
            BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(1), o.Id);
            p[5] = o.Kind;
            BinaryPrimitives.WriteUInt16LittleEndian(p.AsSpan(6), (ushort)index);
            BinaryPrimitives.WriteUInt16LittleEndian(p.AsSpan(8), (ushort)o.Count);
            o.Blob.AsSpan(start, length).CopyTo(p.AsSpan(10));
            return p;
        }

        // (the session's receive thread)
        private void OnReceived(int fromSeat, byte[] data)
        {
            try
            {
                if (data.Length >= 7 && data[0] == TypeAck)
                {
                    uint id = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(1));
                    int index = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(5));
                    lock (_lock)
                    {
                        // (a guest's acks come from its seat; the host's to a guest from seat 0)
                        Outgoing? o = _out.FirstOrDefault(x => x.Id == id && (!_net.IsHost || x.Seat == fromSeat));
                        if (o != null && index < o.Count && !o.Acked[index])
                        {
                            o.Acked[index] = true;
                            o.Left--;
                        }
                    }
                    return;
                }
                if (data.Length < 10 || data[0] != TypeChunk) return;
                uint blobId = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(1));
                byte kind = data[5];
                int at = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(6));
                int count = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(8));
                // what each side may receive: the host identities, a guest the host's recording
                bool expected = _net.IsHost ? kind is KindIdentity or KindRequest : kind == KindReplay && fromSeat == 0;
                if (!expected || count == 0 || count > MaxChunks || at >= count || data.Length - 10 > ChunkBytes) return;
                byte[] ack = new byte[7];
                ack[0] = TypeAck;
                BinaryPrimitives.WriteUInt32LittleEndian(ack.AsSpan(1), blobId);
                BinaryPrimitives.WriteUInt16LittleEndian(ack.AsSpan(5), (ushort)at);
                byte[]? whole = null;
                lock (_lock)
                {
                    if (_disposed) return;
                    if (!_in.TryGetValue((fromSeat, blobId), out Incoming? inc))
                    {
                        inc = new Incoming(kind, count);
                        _in[(fromSeat, blobId)] = inc;
                    }
                    if (!inc.Done && inc.Kind == kind && inc.Parts.Length == count && inc.Parts[at] == null)
                    {
                        inc.Parts[at] = data[10..];
                        if (++inc.Have == count)
                        {
                            inc.Done = true;
                            whole = inc.Parts.SelectMany(p => p!).ToArray();
                            Array.Clear(inc.Parts); // (kept as Done: a late repeat is only acked)
                        }
                    }
                }
                _net.SendSide(_net.IsHost ? fromSeat : 0, ack);
                if (whole == null) return;
                if (kind == KindIdentity)
                {
                    using var r = new BinaryReader(new MemoryStream(whole));
                    r.ReadByte();
                    bool want = r.ReadBoolean();
                    ReplaySeatIdentity id = ReadIdentity(r) with { Seat = fromSeat };
                    lock (_lock)
                    {
                        _identities[fromSeat] = id;
                        if (want) _wantReplay.Add(fromSeat);
                    }
                    IdentityReceived?.Invoke(id);
                }
                else if (kind == KindRequest)
                {
                    lock (_lock)
                    {
                        _wantReplay.Add(fromSeat);
                    }
                    ReplayRequested?.Invoke(fromSeat);
                }
                else
                {
                    ReplayReceived?.Invoke(whole);
                }
            }
            catch (Exception ex) when (ex is EndOfStreamException or IOException or ArgumentException or ObjectDisposedException
                or InvalidOperationException or System.Net.Sockets.SocketException)
            {
                Console.Error.WriteLine("replay link: bad piece: " + ex.Message);
            }
        }

        public static void WriteIdentity(BinaryWriter w, ReplaySeatIdentity id)
        {
            w.Write(id.Seat);
            w.Write(id.FriendCode ?? "");
            w.Write(id.Name);
            w.Write(id.Tag);
            w.Write(id.LicenseJson ?? "");
        }

        public static ReplaySeatIdentity ReadIdentity(BinaryReader r)
        {
            int seat = r.ReadInt32();
            string code = r.ReadString();
            string name = r.ReadString();
            int tag = r.ReadInt32();
            string license = r.ReadString();
            // (what another device says: kept to what a name and a card can be)
            name = OnlineNames.Clean(name);
            if (name.Length > 16) name = name[..16];
            if (code.Length > 20 || !code.All(c => char.IsDigit(c) || c == '-')) code = "";
            if (license.Length > 8192) license = "";
            return new ReplaySeatIdentity(seat, code.Length > 0 ? code : null, name, license.Length > 0 ? license : null,
                Math.Clamp(tag, 0, 9999));
        }

        // This device's own identity for a seat: its friend code (OnlineIdentity; none = never went online), the Hunter
        // License's name and card (the card's numbers only, as the license goes to the server: never its LAN address), and its
        // tag when known: the server's word (`me`, the online connection's own card) or the last one it gave (kept beside the
        // identity, so a LAN match shows NAME#tag too).
        public static ReplaySeatIdentity OwnIdentity(string? dataDir, int seat, PlayerCard? me = null)
        {
            string? code = OnlineIdentity.Load(dataDir)?.FriendCode;
            HunterLicense license = HunterLicense.Load(dataDir);
            int tag = 0;
            string name = license.Name;
            string? tagPath = dataDir != null ? Path.Combine(dataDir, TagFile) : null;
            if (me != null && code != null && me.Code == code && me.Tag > 0)
            {
                tag = me.Tag;
                if (me.Name.Length > 0) name = me.Name; // (the server's word-filtered name is the one others see)
                try
                {
                    if (tagPath != null) File.WriteAllText(tagPath, $"{code}\n{tag}\n{name}");
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                }
            }
            else if (tagPath != null && code != null)
            {
                try
                {
                    string[] kept = File.Exists(tagPath) ? File.ReadAllLines(tagPath) : Array.Empty<string>();
                    if (kept.Length >= 2 && kept[0] == code && int.TryParse(kept[1], out int t)
                        && (kept.Length < 3 || string.Equals(kept[2], license.Name, StringComparison.OrdinalIgnoreCase)))
                    {
                        tag = t;
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                }
            }
            return new ReplaySeatIdentity(seat, code, name, RecompMenus.OnlineLicenseJson(license), tag);
        }

        public const string TagFile = "online_tag.txt";

        public void Dispose()
        {
            lock (_lock)
            {
                if (_disposed) return;
                _disposed = true;
                _out.Clear();
                _in.Clear();
            }
            _net.SideReceived -= OnReceived;
            _timer.Dispose();
        }

        private sealed class Outgoing
        {
            public readonly int Seat;
            public readonly uint Id;
            public readonly byte Kind;
            public readonly byte[] Blob;
            public readonly int Count;
            public readonly bool[] Acked;
            public readonly long[] SentAt;
            public readonly long Started = Environment.TickCount64;
            public int Left;

            public Outgoing(int seat, uint id, byte kind, byte[] blob, int count)
            {
                (Seat, Id, Kind, Blob, Count) = (seat, id, kind, blob, count);
                Acked = new bool[count];
                SentAt = new long[count];
                Array.Fill(SentAt, long.MinValue / 2);
                Left = count;
            }
        }

        private sealed class Incoming
        {
            public readonly byte Kind;
            public readonly byte[]?[] Parts;
            public int Have;
            public bool Done;

            public Incoming(byte kind, int count)
            {
                Kind = kind;
                Parts = new byte[]?[count];
            }
        }
    }
}
