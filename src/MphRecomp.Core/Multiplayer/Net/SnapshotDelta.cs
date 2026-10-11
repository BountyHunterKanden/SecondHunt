using System;
using System.Buffers.Binary;
using System.Collections.Generic;

namespace MphRecomp.Multiplayer.Net
{
    // Online snapshots as deltas (O5, docs/ONLINE_PLAY.md section 9): the host sends each joiner only what changed since a
    // snapshot that joiner said it has (its ack, carried in its inputs). Both sides keep the snapshots as Snapshot.Write's
    // exact bytes, so a delta rebuilds the very bytes the host encoded (quantised floats included) and Snapshot.Read reads
    // them as from the LAN wire. Blocks of a snapshot:
    //   head     match time + state + seat count: always sent (6 bytes)
    //   seats    per seat a 13-bit mask of the field groups (SeatGroups) that differ from the base, then those bytes
    //   items    0 = same as the base, 1 = literal (u16 length + bytes), 2 = XOR runs against the base's (u16 length + runs)
    //   mode     the same three kinds (NetObjective; an empty block = none, Battle; the bytes run to the end of
    //            Snapshot.Write's, so they carry the Trooper's cling + drone too, SeatAbilities, online protocol 8)
    //   extras   the same three kinds (SeatExtras: online only, not part of Snapshot.Write's bytes)
    //   handoff  the same three kinds (HostHandoff, protocol 7: online only, for host migration)
    // Packet: tick u32, base tick u32 (0 = no base: everything literal), input ack u16 (0xFFFF = none), held u8, then the
    // blocks. No base the receiver lacks is ever used: a joiner acks only what it rebuilt and keeps the last History.
    public static class SnapshotDelta
    {
        public const int History = 64; // ~2 s at 30 Hz: an ack older than this gets a full snapshot
        public const int PacketHeader = 11;

        // byte ranges of Snapshot.WriteSeat's layout that change together
        private static readonly (int Start, int Length)[] SeatGroups =
        {
            (0, 1), // flags
            (1, 12), // position
            (13, 12), // speed
            (25, 6), // facing
            (31, 6), // up
            (37, 4), // yaw + pitch
            (41, 1), // aim epoch
            (42, 2), // health
            (44, 3), // weapon + weapons
            (47, 4), // ammo
            (51, 2), // controls
            (53, 6), // points, kills, deaths
            (59, 3) // the last hit
        };
        private const ushort AllGroups = (1 << 13) - 1;

        // one snapshot as Snapshot.Write wrote it, cut into its blocks
        public sealed class Encoded
        {
            public uint Tick;
            public byte[] Bytes = Array.Empty<byte>();
            public int SeatCount;
            public int ItemsStart, ObjectiveStart; // block starts in Bytes (items: its present flag; mode: its flag)
            public long SentAt; // host: when it was first sent (the joiners' acks give their round trip)
            public byte[] Extras = Array.Empty<byte>(); // SeatExtras.ToBytes (empty: none)
            public byte[] Handoff = Array.Empty<byte>(); // HostHandoff.ToBytes (empty: none)

            public static Encoded From(byte[] bytes)
            {
                int seats = bytes[9];
                int items = Snapshot.HeaderSize + seats * Snapshot.SeatSize;
                int objective = items + (bytes[items] == 0 ? 1 : 2 + bytes[items + 1] * NetItem.Size);
                return new Encoded
                {
                    Tick = BinaryPrimitives.ReadUInt32LittleEndian(bytes), Bytes = bytes, SeatCount = seats, ItemsStart = items,
                    ObjectiveStart = objective
                };
            }

            public ReadOnlySpan<byte> Seat(int i) => Bytes.AsSpan(Snapshot.HeaderSize + i * Snapshot.SeatSize, Snapshot.SeatSize);
            public ReadOnlySpan<byte> Items => Bytes.AsSpan(ItemsStart, ObjectiveStart - ItemsStart);
            public ReadOnlySpan<byte> Objective => Bytes.AsSpan(ObjectiveStart);
        }

        public static Encoded Encode(Snapshot snap)
        {
            byte[] buffer = new byte[snap.Size + 8];
            int length = snap.Write(buffer);
            Encoded e = Encoded.From(buffer.AsSpan(0, length).ToArray());
            e.Extras = snap.Extras?.ToBytes() ?? Array.Empty<byte>();
            e.Handoff = snap.Handoff?.ToBytes() ?? Array.Empty<byte>();
            return e;
        }

        // host: the packet body for one joiner (after the message type byte); base null = everything literal
        public static int Write(Span<byte> dest, Encoded cur, Encoded? baseSnap, int inputAck, int held)
        {
            if (baseSnap != null && baseSnap.SeatCount != cur.SeatCount)
            {
                baseSnap = null;
            }
            BinaryPrimitives.WriteUInt32LittleEndian(dest, cur.Tick);
            BinaryPrimitives.WriteUInt32LittleEndian(dest[4..], baseSnap?.Tick ?? 0);
            BinaryPrimitives.WriteUInt16LittleEndian(dest[8..], inputAck < 0 ? (ushort)0xFFFF : (ushort)inputAck);
            dest[10] = (byte)Math.Clamp(held, 0, 255);
            int o = PacketHeader;
            cur.Bytes.AsSpan(4, 6).CopyTo(dest[o..]); // match time, state, seat count
            o += 6;
            for (int i = 0; i < cur.SeatCount; i++)
            {
                ReadOnlySpan<byte> seat = cur.Seat(i);
                ushort mask = AllGroups;
                if (baseSnap != null)
                {
                    ReadOnlySpan<byte> b = baseSnap.Seat(i);
                    mask = 0;
                    for (int g = 0; g < SeatGroups.Length; g++)
                    {
                        (int start, int length) = SeatGroups[g];
                        if (!seat.Slice(start, length).SequenceEqual(b.Slice(start, length)))
                        {
                            mask |= (ushort)(1 << g);
                        }
                    }
                }
                BinaryPrimitives.WriteUInt16LittleEndian(dest[o..], mask);
                o += 2;
                for (int g = 0; g < SeatGroups.Length; g++)
                {
                    if ((mask & (1 << g)) != 0)
                    {
                        (int start, int length) = SeatGroups[g];
                        seat.Slice(start, length).CopyTo(dest[o..]);
                        o += length;
                    }
                }
            }
            o += WriteBlock(dest[o..], cur.Items, baseSnap == null ? null : baseSnap.Items.ToArray());
            o += WriteBlock(dest[o..], cur.Objective, baseSnap == null ? null : baseSnap.Objective.ToArray());
            o += WriteBlock(dest[o..], cur.Extras, baseSnap?.Extras);
            o += WriteBlock(dest[o..], cur.Handoff, baseSnap?.Handoff);
            return o;
        }

        // the largest packet body a snapshot of this size can need (all literal)
        public static int MaxSize(Encoded cur) => PacketHeader + 6 + cur.SeatCount * (2 + Snapshot.SeatSize)
            + 3 * 4 + cur.Bytes.Length + cur.Extras.Length + cur.Handoff.Length;

        private static int WriteBlock(Span<byte> dest, ReadOnlySpan<byte> cur, byte[]? baseBlock)
        {
            if (baseBlock != null && cur.SequenceEqual(baseBlock))
            {
                dest[0] = 0;
                return 1;
            }
            if (baseBlock != null && baseBlock.Length == cur.Length && cur.Length > 0)
            {
                // XOR runs: (zeros u8, literal count u8, literal XOR bytes) until the block is covered
                Span<byte> runs = stackalloc byte[cur.Length * 2 + 4];
                int r = 0, i = 0;
                bool fits = true;
                while (i < cur.Length)
                {
                    int zeros = 0;
                    while (i + zeros < cur.Length && zeros < 255 && cur[i + zeros] == baseBlock[i + zeros])
                    {
                        zeros++;
                    }
                    i += zeros;
                    int literal = 0;
                    while (i + literal < cur.Length && literal < 255 && cur[i + literal] != baseBlock[i + literal])
                    {
                        literal++;
                    }
                    if (r + 2 + literal > cur.Length)
                    {
                        fits = false; // no smaller than the literal block
                        break;
                    }
                    runs[r++] = (byte)zeros;
                    runs[r++] = (byte)literal;
                    for (int k = 0; k < literal; k++)
                    {
                        runs[r++] = (byte)(cur[i + k] ^ baseBlock[i + k]);
                    }
                    i += literal;
                }
                if (fits)
                {
                    dest[0] = 2;
                    BinaryPrimitives.WriteUInt16LittleEndian(dest[1..], (ushort)r);
                    runs[..r].CopyTo(dest[3..]);
                    return 3 + r;
                }
            }
            dest[0] = 1;
            BinaryPrimitives.WriteUInt16LittleEndian(dest[1..], (ushort)cur.Length);
            cur.CopyTo(dest[3..]);
            return 3 + cur.Length;
        }

        // client: the snapshots rebuilt so far (the bases a delta may name)
        public sealed class Receiver
        {
            private readonly Dictionary<uint, Encoded> _history = new();
            private readonly Queue<uint> _order = new();
            // deltas whose base was gone (shouldn't happen: the host only names acked ticks this device still keeps)
            public int MissingBase { get; private set; }

            public void Reset()
            {
                _history.Clear();
                _order.Clear();
            }

            // the packet body (after the message type byte) -> the snapshot, or null when its base isn't here
            public Snapshot? Read(ReadOnlySpan<byte> src)
            {
                uint tick = BinaryPrimitives.ReadUInt32LittleEndian(src);
                uint baseTick = BinaryPrimitives.ReadUInt32LittleEndian(src[4..]);
                ushort ack = BinaryPrimitives.ReadUInt16LittleEndian(src[8..]);
                int held = src[10];
                Encoded? baseSnap = null;
                if (baseTick != 0 && !_history.TryGetValue(baseTick, out baseSnap))
                {
                    MissingBase++;
                    return null;
                }
                int o = PacketHeader;
                int seats = src[o + 5];
                var bytes = new List<byte>(Snapshot.HeaderSize + seats * Snapshot.SeatSize + 64);
                Span<byte> head = stackalloc byte[4];
                BinaryPrimitives.WriteUInt32LittleEndian(head, tick);
                bytes.AddRange(head.ToArray());
                bytes.AddRange(src.Slice(o, 6).ToArray());
                o += 6;
                if (baseSnap != null && baseSnap.SeatCount != seats)
                {
                    MissingBase++;
                    return null;
                }
                byte[] seat = new byte[Snapshot.SeatSize];
                for (int i = 0; i < seats; i++)
                {
                    ushort mask = BinaryPrimitives.ReadUInt16LittleEndian(src[o..]);
                    o += 2;
                    if (baseSnap != null)
                    {
                        baseSnap.Seat(i).CopyTo(seat);
                    }
                    for (int g = 0; g < SeatGroups.Length; g++)
                    {
                        if ((mask & (1 << g)) != 0)
                        {
                            (int start, int length) = SeatGroups[g];
                            src.Slice(o, length).CopyTo(seat.AsSpan(start));
                            o += length;
                        }
                    }
                    bytes.AddRange(seat);
                }
                o += ReadBlock(src[o..], baseSnap == null ? null : baseSnap.Items, bytes);
                o += ReadBlock(src[o..], baseSnap == null ? null : baseSnap.Objective, bytes);
                var extras = new List<byte>();
                o += ReadBlock(src[o..], baseSnap == null ? null : baseSnap.Extras, extras);
                var handoff = new List<byte>();
                o += ReadBlock(src[o..], baseSnap == null ? null : baseSnap.Handoff, handoff);
                Encoded cur = Encoded.From(bytes.ToArray());
                cur.Extras = extras.ToArray();
                cur.Handoff = handoff.ToArray();
                _history[tick] = cur;
                _order.Enqueue(tick);
                while (_order.Count > History)
                {
                    _history.Remove(_order.Dequeue());
                }
                Snapshot snap = Snapshot.Read(cur.Bytes);
                snap.InputAck = ack == 0xFFFF ? -1 : ack;
                snap.InputHeld = held;
                snap.Extras = SeatExtras.FromBytes(cur.Extras);
                snap.Handoff = HostHandoff.FromBytes(cur.Handoff);
                return snap;
            }

            private static int ReadBlock(ReadOnlySpan<byte> src, ReadOnlySpan<byte> baseBlock, List<byte> into)
            {
                byte kind = src[0];
                if (kind == 0)
                {
                    into.AddRange(baseBlock.ToArray());
                    return 1;
                }
                int length = BinaryPrimitives.ReadUInt16LittleEndian(src[1..]);
                ReadOnlySpan<byte> body = src.Slice(3, length);
                if (kind == 1)
                {
                    into.AddRange(body.ToArray());
                    return 3 + length;
                }
                byte[] block = baseBlock.ToArray();
                int i = 0, r = 0;
                while (r < body.Length)
                {
                    i += body[r++];
                    int literal = body[r++];
                    for (int k = 0; k < literal; k++)
                    {
                        block[i + k] ^= body[r++];
                    }
                    i += literal;
                }
                into.AddRange(block);
                return 3 + length;
            }
        }
    }
}
