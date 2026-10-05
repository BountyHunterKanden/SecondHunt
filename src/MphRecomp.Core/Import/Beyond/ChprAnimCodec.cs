using System;
using System.Collections.Generic;
using System.IO;

namespace MphRecomp.Import.Beyond
{
    // A decode failure with the reference decoder's own message (mp4anim_codec.py / mp4anim_lib.py), so a run can be
    // compared anim by anim with its index.json.
    public sealed class ChprAnimException : Exception
    {
        public ChprAnimException(string message) : base(message) { }
    }

    // Metroid Prime 4: Beyond CHPR animation codec: the "CompStream" bitstream records of a CHPR's animation table.
    // Clean-room, worked out from the owner's own RomFS dump (no game code read). Port of
    // brawl_extract/rig_work/gun/mp4anim/py/mp4anim_codec.py (all arithmetic double precision, as the Python's is).
    //
    // Anim record: u8 type (0 = CompStream, 1 = Sequence, 2 = Grid), u8 set, u16 index, u8 x4, u8 has name, u32 name
    //   (string index), i32 ref.  CompStream (type 0), at +17 (all offsets below relative to +17):
    //   u32 total (= record size - 17), u16 o1 (0x20), u16 o2, u32 o3, f32 rate factor (@12), u16 frame count (@16),
    //   u64 group mask (@18) ...; [o1] low 4 bits = stream count, [o2, o3) track descriptors,
    //   [o3, total) per stream: bitstream, then an 8-aligned table entry.
    // Table entry: 01, u16 length, blocks: 02 joints (cnt, u16 group, cnt x (u16 rot, u16 scale, u16 trans)),
    //   03 root (cnt, u16 group, cnt x (u16 rot, u16 trans)), 04 bools (cnt, u8 value, cnt x u16 node),
    //   07 floats (cnt, u16 group, cnt x u16).
    //   u16 slot value: bit15 set -> animated track (class = (v >> 10) & 31, index = v & 0x3ff); else a constant-pool
    //   float index (a scale slot with bit14 set: ONE pool value = uniform scale delta, at index v & 0x3fff).
    // Track descriptors (bits LSB first) after 2 bytes (00, first type): per class: n x [w:5][f:3][x:6], then an 8-bit
    //   separator; w = bits per component, x[3..5] = component mask (x, y, z); type = class + first type; type 2 is the
    //   quaternion type. Fraction bits = f | (x[0] + 2 x[1]) << 3.
    // Bitstream (per stream, LSB first): W0:4, per track a W0-bit code (frame of its 2nd key; 0 = none), key 0 of every
    //   track, then the 2nd key of every track with a code; then for t = 1 .. frames-2: N:kb (kb = bit length of the
    //   track count) = number of tracks whose last key is at t; if N: W:4, N x W-bit codes (frames to that track's next
    //   key, in track order; 0 = no more keys), then those keys.
    //   key = [2 flag bits if quaternion] + one w-bit signed value per masked component.  Quaternion flags: bit1 = w is
    //   -sqrt(1 - x2 - y2 - z2); bit0 = rare, meaning unknown, ignored.
    public static class ChprAnimCodec
    {
        public sealed class RecordHeader
        {
            public int Type, Set, Index, B4, B5, B6, B7, HasName, NameId, Ref;

            public static RecordHeader Parse(byte[] b, int off) => new()
            {
                Type = b[off], Set = b[off + 1], Index = Chpr.U16(b, off + 2), B4 = b[off + 4], B5 = b[off + 5], B6 = b[off + 6],
                B7 = b[off + 7], HasName = b[off + 8], NameId = (int)Math.Min(Chpr.U32(b, off + 9), int.MaxValue), Ref = Chpr.I32(b, off + 13),
            };
        }

        public enum BlockKind { Joints = 2, Root = 3, Bools = 4, Floats = 7 }

        // one block of a stream's table entry
        public sealed class Block
        {
            public BlockKind Kind;
            public int Group;                              // anim group (Joints / Root / Floats)
            public int Value;                              // Bools: the value every listed node takes
            public ushort[][] Records = Array.Empty<ushort[]>();   // Joints: (rot, scale, trans); Root: (rot, trans)
            public ushort[] Values = Array.Empty<ushort>();        // Floats: slots; Bools: nodes
        }

        public sealed class Stream
        {
            public int BitStart, TableStart;               // offsets into CompStream.D
            public List<Block> Blocks = new();
        }

        public sealed class Track
        {
            public int Class, Width, Frac, Type, FlagBits;
            public byte[] X = Array.Empty<byte>();          // the 6 descriptor bits x[0..5]
            public int[] Comps = Array.Empty<int>();       // masked components (0 x, 1 y, 2 z)
            public bool IsQuaternion => Type == 2;
            public int Exponent => Frac | ((X[0] + 2 * X[1]) << 3);
        }

        public readonly struct Key
        {
            public readonly int Frame, Flags;
            public readonly long V0, V1, V2;
            public readonly byte Mask;                     // bit c = component c present

            public Key(int frame, int flags, long[] v, byte mask)
            {
                Frame = frame; Flags = flags; V0 = v[0]; V1 = v[1]; V2 = v[2]; Mask = mask;
            }

            public bool Has(int c) => (Mask & (1 << c)) != 0;
            public long this[int c] => c == 0 ? V0 : c == 1 ? V1 : V2;
        }

        public sealed class StreamStatus
        {
            public int Stream;
            public bool Ok = true, RestZero;
            public string Msg = "";
            public long End, Bits;
        }

        public sealed class CompStream
        {
            public int Total, O1, O2, Frames, StreamCount;
            public long O3;
            public float RateFactor;                       // f32 @12: 1.0 or 0.5 (frame rate = 30 x this, assumed)
            public ulong GroupMask;
            public byte[] D = Array.Empty<byte>();         // the record from +17 on
            public byte[] S2 = Array.Empty<byte>();        // [o2, o3): track descriptors
            public List<Stream> Streams = new();

            public static CompStream Parse(byte[] b, int off, int size)
            {
                int s = off + 17;
                var c = new CompStream { Total = checked((int)Chpr.U32(b, s)), O1 = Chpr.U16(b, s + 4), O2 = Chpr.U16(b, s + 6), O3 = Chpr.U32(b, s + 8) };
                if (c.Total != size - 17) throw new ChprAnimException($"CompStream size {c.Total} disagrees with the record size {size}");
                c.D = b.AsSpan(s, Math.Min(c.Total, b.Length - s)).ToArray();
                c.RateFactor = Chpr.F32(c.D, 12);
                c.Frames = Chpr.U16(c.D, 16);
                c.GroupMask = Chpr.U64(c.D, 18);
                if (c.O1 >= c.D.Length) throw new ChprAnimException("index out of range");
                c.StreamCount = c.D[c.O1] & 15;
                int lo = Math.Min(c.O2, c.D.Length), hi = (int)Math.Min(Math.Max(c.O3, lo), c.D.Length);
                c.S2 = c.D.AsSpan(lo, hi - lo).ToArray();
                List<Stream>? st = c.O3 <= int.MaxValue ? SolveStreams(c.D, (int)c.O3, c.Total, c.StreamCount) : null;
                c.Streams = st ?? throw new ChprAnimException("stream/table layout not found");
                return c;
            }
        }

        // ---------------------------------------------------------------- tables
        static int A8(int x) => (x + 7) & ~7;

        public static List<Block>? ParsePayload(byte[] d, int start, int len)
        {
            var o = new List<Block>();
            int q = 0;
            int U16(int at) => Chpr.U16(d, start + at);
            while (q < len)
            {
                if (q + 3 > len) return null;
                int tag = d[start + q], cnt = d[start + q + 1];
                if (cnt == 0) return null;
                switch (tag)
                {
                case 2:
                    {
                        if (q + 4 + 6 * cnt > len) return null;
                        var blk = new Block { Kind = BlockKind.Joints, Group = U16(q + 2), Records = new ushort[cnt][] };
                        q += 4;
                        for (int i = 0; i < cnt; i++) blk.Records[i] = new[] { (ushort)U16(q + 6 * i), (ushort)U16(q + 6 * i + 2), (ushort)U16(q + 6 * i + 4) };
                        q += 6 * cnt;
                        o.Add(blk);
                        break;
                    }
                case 3:
                    {
                        if (q + 4 + 4 * cnt > len) return null;
                        var blk = new Block { Kind = BlockKind.Root, Group = U16(q + 2), Records = new ushort[cnt][] };
                        q += 4;
                        for (int i = 0; i < cnt; i++) blk.Records[i] = new[] { (ushort)U16(q + 4 * i), (ushort)U16(q + 4 * i + 2) };
                        q += 4 * cnt;
                        o.Add(blk);
                        break;
                    }
                case 7:
                    {
                        if (q + 4 + 2 * cnt > len) return null;
                        var blk = new Block { Kind = BlockKind.Floats, Group = U16(q + 2), Values = new ushort[cnt] };
                        for (int i = 0; i < cnt; i++) blk.Values[i] = (ushort)U16(q + 4 + 2 * i);
                        q += 4 + 2 * cnt;
                        o.Add(blk);
                        break;
                    }
                case 4:
                    {
                        if (q + 3 + 2 * cnt > len) return null;
                        var blk = new Block { Kind = BlockKind.Bools, Value = d[start + q + 2], Values = new ushort[cnt] };
                        for (int i = 0; i < cnt; i++) blk.Values[i] = (ushort)U16(q + 3 + 2 * i);
                        q += 3 + 2 * cnt;
                        o.Add(blk);
                        break;
                    }
                default:
                    return null;
                }
            }
            return o;
        }

        static (List<Block> Blocks, int Next)? TryEntry(byte[] d, int q, int ts)
        {
            if (q + 3 > ts || d[q] != 1) return null;
            int len = Chpr.U16(d, q + 1);
            if (len == 0 || q + 3 + len > ts) return null;
            List<Block>? pp = ParsePayload(d, q + 3, len);
            if (pp == null) return null;
            return (pp, A8(q + 3 + len));
        }

        // n streams from p on, each = a bitstream then an 8-aligned table entry; the first layout found (depth first)
        public static List<Stream>? SolveStreams(byte[] d, int p, int ts, int n)
        {
            if (n == 0) return ts - p >= 0 && ts - p < 16 ? new List<Stream>() : null;
            for (int q = p; q < ts; q++)
            {
                var r = TryEntry(d, q, ts);
                if (r == null) continue;
                List<Stream>? rest = SolveStreams(d, r.Value.Next, ts, n - 1);
                if (rest == null) continue;
                rest.Insert(0, new Stream { BitStart = p, TableStart = q, Blocks = r.Value.Blocks });
                return rest;
            }
            return null;
        }

        // ---------------------------------------------------------------- descriptors
        // {class: indices} of the animated track references in a stream's table
        public static SortedDictionary<int, HashSet<int>> TrackClasses(List<Block> payload)
        {
            var cls = new SortedDictionary<int, HashSet<int>>();
            void Add(ushort v)
            {
                if ((v & 0x8000) == 0) return;
                int c = (v >> 10) & 31;
                if (!cls.TryGetValue(c, out var set)) cls[c] = set = new HashSet<int>();
                set.Add(v & 0x3ff);
            }
            foreach (Block blk in payload)
            {
                if (blk.Kind is BlockKind.Joints or BlockKind.Root)
                {
                    foreach (ushort[] rec in blk.Records)
                    {
                        foreach (ushort v in rec) Add(v);
                    }
                }
                else if (blk.Kind == BlockKind.Floats)
                {
                    foreach (ushort v in blk.Values) Add(v);
                }
            }
            return cls;
        }

        // per stream: its tracks in class / index order (the descriptor lists are stored stream after stream)
        public static List<List<Track>> ParseDescriptors(byte[] s2, List<Stream> streams)
        {
            if (s2.Length < 2) throw new ChprAnimException("index out of range");
            int first = s2[1];
            var br = new BitReader(s2, 2, s2.Length - 2);
            var o = new List<List<Track>>();
            foreach (Stream st in streams)
            {
                var lay = new List<Track>();
                foreach (var kv in TrackClasses(st.Blocks))
                {
                    int c = kv.Key, n = 0;
                    foreach (int i in kv.Value) n = Math.Max(n, i + 1);
                    for (int k = 0; k < n; k++)
                    {
                        int w = (int)br.U(5), f = (int)br.U(3);
                        var x = new byte[6];
                        for (int i = 0; i < 6; i++) x[i] = (byte)br.U(1);
                        int typ = c + first;
                        var comps = new List<int>(3);
                        for (int i = 0; i < 3; i++)
                        {
                            if (x[3 + i] != 0) comps.Add(i);
                        }
                        lay.Add(new Track { Class = c, Width = w, Frac = f, X = x, Type = typ, FlagBits = typ == 2 ? 2 : 0, Comps = comps.ToArray() });
                    }
                    br.P += 8;                    // the separator (skipped unread, even at the very end)
                }
                o.Add(lay);
            }
            return o;
        }

        // ---------------------------------------------------------------- bitstream
        public sealed class BitReader
        {
            readonly byte[] _d;
            readonly int _start;
            public readonly long N;
            public long P;

            public BitReader(byte[] d, int start, int length)
            {
                _d = d; _start = start; N = 8L * Math.Max(0, length);
            }

            public ulong U(int n)
            {
                if (n == 0) return 0;
                if (P + n > N) throw new ChprAnimException("bit read past the end of the data");
                ulong v = 0;
                int shift = 0;
                long p = P;
                while (shift < n)
                {
                    int bo = (int)(p & 7), take = Math.Min(8 - bo, n - shift);
                    ulong bits = (ulong)((_d[_start + (int)(p >> 3)] >> bo) & ((1 << take) - 1));
                    v |= bits << shift;
                    shift += take; p += take;
                }
                P = p;
                return v;
            }

            // two's complement; a zero-width value is the Python's "negative shift count" error
            public long S(int n)
            {
                ulong v = U(n);
                if (n == 0) throw new ChprAnimException("negative shift count");
                return ((v >> (n - 1)) & 1) != 0 ? (long)v - (1L << n) : (long)v;
            }

            public bool RestZero()
            {
                for (long p = Math.Max(P, 0); p < N; p++)
                {
                    if (((_d[_start + (int)(p >> 3)] >> (int)(p & 7)) & 1) != 0) return false;
                }
                return true;
            }
        }

        static Key ReadKey(BitReader br, Track t, int frame)
        {
            int flags = t.FlagBits > 0 ? (int)br.U(t.FlagBits) : 0;
            var v = new long[3];
            byte mask = 0;
            foreach (int c in t.Comps)
            {
                v[c] = br.S(t.Width);
                mask |= (byte)(1 << c);
            }
            return new Key(frame, flags, v, mask);
        }

        // -> per track its keys (frame ascending), plus the stream's status
        public static (List<Key>[] Keys, StreamStatus Status) DecodeStream(byte[] d, int start, int length, List<Track> lay, int frames)
        {
            var br = new BitReader(d, start, length);
            int nt = lay.Count;
            int kb = 1;
            while ((nt >> kb) != 0) kb++;         // max(1, bit length of nt)
            int w0 = (int)br.U(4);
            var codes0 = new int[nt];
            for (int i = 0; i < nt; i++) codes0[i] = (int)br.U(w0);
            var keys = new List<Key>[nt];
            for (int i = 0; i < nt; i++) keys[i] = new List<Key> { ReadKey(br, lay[i], 0) };
            const int Never = 1_000_000_000;
            var nxt = new int[nt];
            for (int i = 0; i < nt; i++)
            {
                nxt[i] = Never;
                if (codes0[i] > 0)
                {
                    keys[i].Add(ReadKey(br, lay[i], codes0[i]));
                    nxt[i] = codes0[i];
                }
            }
            var st = new StreamStatus();
            var due = new List<int>();
            for (int t = 1; t < frames - 1; t++)
            {
                due.Clear();
                bool allDone = true;
                for (int i = 0; i < nt; i++)
                {
                    if (nxt[i] == t) due.Add(i);
                    if (nxt[i] < frames) allDone = false;
                }
                if (due.Count == 0 && allDone) break;
                if (br.P + kb > br.N)
                {
                    st.Ok = false; st.Msg = "eof";
                    break;
                }
                int n = (int)br.U(kb);
                if (n != due.Count)
                {
                    st.Ok = false; st.Msg = $"N mismatch at frame {t} (N={n}, due={due.Count})";
                    break;
                }
                if (n > 0)
                {
                    int w = (int)br.U(4);
                    var codes = new int[n];
                    for (int k = 0; k < n; k++) codes[k] = (int)br.U(w);
                    for (int k = 0; k < n; k++)
                    {
                        int i = due[k], c = codes[k];
                        if (c == 0)
                        {
                            nxt[i] = Never;
                            continue;
                        }
                        keys[i].Add(ReadKey(br, lay[i], t + c));
                        nxt[i] = t + c;
                    }
                }
            }
            st.End = br.P; st.Bits = br.N; st.RestZero = br.RestZero();
            return (keys, st);
        }

        // ---------------------------------------------------------------- values
        // quaternion tracks -> (w, x, y, z): the stored x, y, z (masked out = 0), w = +-sqrt(1 - |xyz|^2), negative when
        // flag bit1 is set, normalised; other tracks -> x, y, z (masked out = 0).  value = int x 2^-exponent
        public static double[] Dequant(Track t, in Key k)
        {
            double sc = Math.Pow(2.0, -t.Exponent);
            if (t.IsQuaternion)
            {
                double x = k.Has(0) ? k.V0 * sc : 0.0, y = k.Has(1) ? k.V1 * sc : 0.0, z = k.Has(2) ? k.V2 * sc : 0.0;
                double s = x * x + y * y + z * z, dd = Math.Sqrt(Math.Max(0.0, 1.0 - s));
                double w = (k.Flags & 2) != 0 ? -dd : dd;
                double nrm = Math.Sqrt(w * w + x * x + y * y + z * z);
                return new[] { w / nrm, x / nrm, y / nrm, z / nrm };
            }
            return new[] { k.Has(0) ? k.V0 * sc : 0.0, k.Has(1) ? k.V1 * sc : 0.0, k.Has(2) ? k.V2 * sc : 0.0 };
        }

        // per-frame values: normalised lerp between quaternion keys (shortest way), linear between vector keys, the end
        // keys held outside them
        public static double[][] SampleTrack(Track t, List<Key> keys, int frames)
        {
            int nk = keys.Count;
            var fr = new int[nk];
            var vals = new double[nk][];
            for (int i = 0; i < nk; i++)
            {
                fr[i] = keys[i].Frame;
                vals[i] = Dequant(t, keys[i]);
            }
            var o = new double[frames][];
            for (int f = 0; f < frames; f++)
            {
                if (f <= fr[0]) { o[f] = (double[])vals[0].Clone(); continue; }
                if (f >= fr[nk - 1]) { o[f] = (double[])vals[nk - 1].Clone(); continue; }
                int j = SearchRight(fr, f) - 1;
                double[] a = vals[j], b = vals[j + 1];
                double u = (double)(f - fr[j]) / (fr[j + 1] - fr[j]);
                int m = a.Length;
                var q = new double[m];
                if (t.IsQuaternion)
                {
                    double dot = 0;
                    for (int i = 0; i < m; i++) dot += a[i] * b[i];
                    double sb = dot < 0 ? -1.0 : 1.0;
                    for (int i = 0; i < m; i++) q[i] = (1 - u) * a[i] + u * (sb * b[i]);
                    double nrm = 0;
                    for (int i = 0; i < m; i++) nrm += q[i] * q[i];
                    nrm = Math.Sqrt(nrm);
                    for (int i = 0; i < m; i++) q[i] /= nrm;
                }
                else
                {
                    for (int i = 0; i < m; i++) q[i] = (1 - u) * a[i] + u * b[i];
                }
                o[f] = q;
            }
            return o;
        }

        // numpy.searchsorted(a, v, side="right"): the first index whose value is > v
        static int SearchRight(int[] a, int v)
        {
            int lo = 0, hi = a.Length;
            while (lo < hi)
            {
                int mid = (lo + hi) >> 1;
                if (v < a[mid]) hi = mid;
                else lo = mid + 1;
            }
            return lo;
        }
    }
}
