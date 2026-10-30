using System;
using System.Buffers.Binary;
using System.IO;
using System.Numerics;

namespace MphRecomp.Import.Beyond
{
    // An ASTC decoder for 2D blocks, LDR profile, to 8-bit RGBA (spec 6.5), written from the Khronos Data Format
    // Specification's ASTC chapter: block modes and weight grids (dual plane included), integer sequence encoding (bits,
    // trits, quints), colour endpoint modes and their unquantisation, partitions (the partition hash), weight
    // unquantisation and infill, and void-extent blocks. HDR endpoint modes, HDR void extents and every illegal encoding
    // give the error colour, opaque magenta. One decoder serves one block footprint; it caches what depends only on the
    // block mode (grid, ranges, infill) and on the partition seed.
    public sealed class AstcBlockDecoder
    {
        public readonly int BlockWidth, BlockHeight;
        readonly int texels;
        readonly ModeInfo?[] modes = new ModeInfo?[2048];
        readonly byte[]?[] partitionMaps = new byte[]?[3 * 1024];

        public static readonly int[][] BlockSizes =
        {
            new[] { 4, 4 }, new[] { 5, 4 }, new[] { 5, 5 }, new[] { 6, 5 }, new[] { 6, 6 }, new[] { 8, 5 }, new[] { 8, 6 },
            new[] { 8, 8 }, new[] { 10, 5 }, new[] { 10, 6 }, new[] { 10, 8 }, new[] { 10, 10 }, new[] { 12, 10 }, new[] { 12, 12 },
        };

        public AstcBlockDecoder(int blockWidth, int blockHeight)
        {
            bool known = false;
            foreach (int[] s in BlockSizes) known |= s[0] == blockWidth && s[1] == blockHeight;
            if (!known) throw new NotSupportedException($"ASTC block size {blockWidth}x{blockHeight} is not a 2D footprint");
            BlockWidth = blockWidth;
            BlockHeight = blockHeight;
            texels = blockWidth * blockHeight;
        }

        // ---------------------------------------------------------------- the 8-bit result (spec 6.5)
        // Kept in these two small functions on purpose: the comparison harness may need the step adjusted.
        // An endpoint pair's channel at weight w (0..64): each 8-bit endpoint widened to 16 bits as e x 257 (linear and
        // sRGB formats alike), interpolated as floor((C0 (64 - w) + C1 w + 32) / 64), then rounded to 8 bits: 65535 gives
        // 255, anything else floor(255 x (C / 65536) + 0.5) in doubles, divided first, then scaled, then the half added.
        static byte Result8(int e0, int e1, int w)
        {
            int c0 = e0 * 257, c1 = e1 * 257;
            int c = (c0 * (64 - w) + c1 * w + 32) >> 6;
            if (c == 65535) return 255;
            return (byte)Math.Floor(255.0 * ((double)c / 65536.0) + 0.5);
        }

        // A void-extent block's channel: the high byte of its stored 16-bit colour.
        static byte VoidExtentResult8(int c16) => (byte)(c16 >> 8);

        // ---------------------------------------------------------------- whole images
        // width x height RGBA8 (rows top first) from linear rows of 16-byte blocks; edge blocks are cropped
        public static byte[] DecodeImage(ReadOnlySpan<byte> blocks, int width, int height, int blockWidth, int blockHeight)
        {
            var dec = new AstcBlockDecoder(blockWidth, blockHeight);
            int bx = (width + blockWidth - 1) / blockWidth, by = (height + blockHeight - 1) / blockHeight;
            if (blocks.Length < (long)bx * by * 16) throw new InvalidDataException($"ASTC image {width}x{height}: {blocks.Length} bytes for {bx * by} blocks");
            var rgba = new byte[(long)width * height * 4];
            var tile = new byte[blockWidth * blockHeight * 4];
            for (int j = 0; j < by; j++)
            {
                for (int i = 0; i < bx; i++)
                {
                    dec.DecodeBlock(blocks.Slice((j * bx + i) * 16, 16), tile);
                    int x0 = i * blockWidth, y0 = j * blockHeight;
                    int w = Math.Min(blockWidth, width - x0), h = Math.Min(blockHeight, height - y0);
                    for (int y = 0; y < h; y++)
                    {
                        tile.AsSpan(y * blockWidth * 4, w * 4).CopyTo(rgba.AsSpan(((y0 + y) * width + x0) * 4, w * 4));
                    }
                }
            }
            return rgba;
        }

        // ---------------------------------------------------------------- one block
        // the block's BlockWidth x BlockHeight texels as RGBA8, row by row
        public void DecodeBlock(ReadOnlySpan<byte> block, Span<byte> rgba)
        {
            ulong lo = BinaryPrimitives.ReadUInt64LittleEndian(block);
            ulong hi = BinaryPrimitives.ReadUInt64LittleEndian(block.Slice(8));
            if (!TryDecode(lo, hi, rgba)) Fill(rgba, 255, 0, 255, 255);
        }

        void Fill(Span<byte> rgba, byte r, byte g, byte b, byte a)
        {
            for (int t = 0; t < texels; t++)
            {
                rgba[t * 4] = r; rgba[t * 4 + 1] = g; rgba[t * 4 + 2] = b; rgba[t * 4 + 3] = a;
            }
        }

        // bits [pos, pos + n) of the 128-bit block (n <= 32); bits at or past `limit` read as 0
        static uint Bits(ulong lo, ulong hi, int pos, int n, int limit = 128)
        {
            if (n == 0 || pos >= limit) return 0;
            if (pos + n > limit) n = limit - pos;
            ulong v = pos >= 64 ? hi >> (pos - 64) : pos == 0 ? lo : (lo >> pos) | (hi << (64 - pos));
            return (uint)(v & ((1UL << n) - 1));
        }

        bool TryDecode(ulong lo, ulong hi, Span<byte> rgba)
        {
            int mode = (int)(lo & 0x7ff);
            if ((mode & 0x1ff) == 0x1fc) return VoidExtent(lo, hi, rgba);
            ModeInfo m = modes[mode] ??= ModeInfo.Make(mode, BlockWidth, BlockHeight);
            if (!m.Valid) return false;

            int parts = (int)Bits(lo, hi, 11, 2) + 1;
            if (m.Dual && parts == 4) return false;

            // colour endpoint modes
            Span<int> cem = stackalloc int[4];
            int extraBits = 0, colorStart;
            int seed = 0;
            if (parts == 1)
            {
                cem[0] = (int)Bits(lo, hi, 13, 4);
                colorStart = 17;
            }
            else
            {
                seed = (int)Bits(lo, hi, 13, 10);
                int field = (int)Bits(lo, hi, 23, 6);
                colorStart = 29;
                if ((field & 3) == 0)
                {
                    for (int p = 0; p < parts; p++) cem[p] = field >> 2;
                }
                else
                {
                    extraBits = 3 * parts - 4;
                    int combined = field | (int)Bits(lo, hi, 128 - m.WeightBits - extraBits, extraBits) << 6;
                    int baseClass = (combined & 3) - 1;
                    for (int p = 0; p < parts; p++)
                    {
                        int c = (combined >> (2 + p)) & 1;
                        int low = (combined >> (2 + parts + 2 * p)) & 3;
                        cem[p] = ((baseClass + c) << 2) | low;
                    }
                }
            }
            int ccs = m.Dual ? (int)Bits(lo, hi, 128 - m.WeightBits - extraBits - 2, 2) : -1;

            // colour values: count, range from the bits left
            int values = 0;
            for (int p = 0; p < parts; p++) values += 2 * ((cem[p] >> 2) + 1);
            if (values > 18) return false;
            int colorEnd = 128 - m.WeightBits - extraBits - (m.Dual ? 2 : 0);
            int available = colorEnd - colorStart;
            if (available < (13 * values + 4) / 5) return false;
            int range = -1;
            for (int q = ColorRanges.Length - 1; q >= 0; q--)
            {
                if (IseBits(values, ColorRanges[q]) <= available)
                {
                    range = q;
                    break;
                }
            }
            if (range < FirstColorRange) return false;
            Span<int> raw = stackalloc int[18];
            DecodeIse(lo, hi, colorStart, colorEnd, values, ColorRanges[range], raw);
            byte[] unq = ColorUnquant[range];
            Span<int> ends = stackalloc int[4 * 8];   // per partition: e0 rgba, e1 rgba
            Span<bool> hdr = stackalloc bool[4];
            Span<int> v = stackalloc int[8];
            int vi = 0;
            for (int p = 0; p < parts; p++)
            {
                int n = 2 * ((cem[p] >> 2) + 1);
                for (int i = 0; i < n; i++) v[i] = unq[raw[vi + i]];
                vi += n;
                hdr[p] = !Endpoints(cem[p], v, ends.Slice(p * 8, 8));
            }

            // weights: a bit-reversed stream from the top of the block
            ulong rlo = ReverseBits(hi), rhi = ReverseBits(lo);
            Span<int> w = stackalloc int[64];
            DecodeIse(rlo, rhi, 0, m.WeightBits, m.WeightCount, WeightRanges[m.WeightRange], w);
            byte[] wunq = WeightUnquant[m.WeightRange];
            for (int i = 0; i < m.WeightCount; i++) w[i] = wunq[w[i]];

            byte[]? map = parts > 1 ? PartitionMap(parts, seed) : null;
            int planes = m.Dual ? 2 : 1;
            for (int t = 0; t < texels; t++)
            {
                int p = map?[t] ?? 0;
                Span<byte> o = rgba.Slice(t * 4, 4);
                if (hdr[p])
                {
                    o[0] = 255; o[1] = 0; o[2] = 255; o[3] = 255;
                    continue;
                }
                int w0 = Infill(m, w, t, planes, 0);
                int w1 = m.Dual ? Infill(m, w, t, planes, 1) : w0;
                Span<int> e = ends.Slice(p * 8, 8);
                for (int c = 0; c < 4; c++)
                {
                    o[c] = Result8(e[c], e[4 + c], c == ccs ? w1 : w0);
                }
            }
            return true;
        }

        static int Infill(ModeInfo m, Span<int> w, int t, int planes, int plane)
        {
            int o = t * 4;
            int[] idx = m.InfillIndex, f = m.InfillFactor;
            int sum = w[idx[o] * planes + plane] * f[o] + w[idx[o + 1] * planes + plane] * f[o + 1]
                + w[idx[o + 2] * planes + plane] * f[o + 2] + w[idx[o + 3] * planes + plane] * f[o + 3];
            return (sum + 8) >> 4;
        }

        static ulong ReverseBits(ulong v)
        {
            v = ((v >> 1) & 0x5555555555555555UL) | ((v & 0x5555555555555555UL) << 1);
            v = ((v >> 2) & 0x3333333333333333UL) | ((v & 0x3333333333333333UL) << 2);
            v = ((v >> 4) & 0x0F0F0F0F0F0F0F0FUL) | ((v & 0x0F0F0F0F0F0F0F0FUL) << 4);
            return BinaryPrimitives.ReverseEndianness(v);
        }

        // ---------------------------------------------------------------- void extent (23.23)
        bool VoidExtent(ulong lo, ulong hi, Span<byte> rgba)
        {
            if (Bits(lo, hi, 9, 1) != 0) return false;        // HDR: the error colour in LDR
            if (Bits(lo, hi, 10, 2) != 3) return false;       // reserved bits must be 1
            uint s0 = Bits(lo, hi, 12, 13), s1 = Bits(lo, hi, 25, 13), t0 = Bits(lo, hi, 38, 13), t1 = Bits(lo, hi, 51, 13);
            bool allOnes = s0 == 0x1fff && s1 == 0x1fff && t0 == 0x1fff && t1 == 0x1fff;
            if (!allOnes && (s0 >= s1 || t0 >= t1)) return false;
            Fill(rgba, VoidExtentResult8((int)Bits(lo, hi, 64, 16)), VoidExtentResult8((int)Bits(lo, hi, 80, 16)),
                VoidExtentResult8((int)Bits(lo, hi, 96, 16)), VoidExtentResult8((int)Bits(lo, hi, 112, 16)));
            return true;
        }

        // ---------------------------------------------------------------- block modes (23.10)
        sealed class ModeInfo
        {
            public bool Valid;
            public int GridWidth, GridHeight;
            public bool Dual;
            public int WeightRange;     // into WeightRanges
            public int WeightCount;     // grid points x planes
            public int WeightBits;
            public int[] InfillIndex = Array.Empty<int>();    // per texel, 4 grid points
            public int[] InfillFactor = Array.Empty<int>();   // per texel, their 4 factors (sum 16)

            static readonly ModeInfo Invalid = new();

            public static ModeInfo Make(int mode, int bw, int bh)
            {
                int b = mode;
                int Bit(int i) => (b >> i) & 1;
                int r, gw, gh;
                bool dual, high;
                if ((b & 3) != 0)
                {
                    r = (Bit(1) << 2) | (Bit(0) << 1) | Bit(4);
                    int A = (b >> 5) & 3, B = (b >> 7) & 3;
                    switch ((b >> 2) & 3)
                    {
                        case 0: gw = B + 4; gh = A + 2; break;
                        case 1: gw = B + 8; gh = A + 2; break;
                        case 2: gw = A + 2; gh = B + 8; break;
                        default:
                            if (Bit(8) == 0) { gw = A + 2; gh = Bit(7) + 6; }
                            else { gw = Bit(7) + 2; gh = A + 2; }
                            break;
                    }
                    dual = Bit(10) == 1;
                    high = Bit(9) == 1;
                }
                else
                {
                    if ((b & 0xf) == 0) return Invalid;                       // x0000: reserved
                    r = (Bit(3) << 2) | (Bit(2) << 1) | Bit(4);
                    int A = (b >> 5) & 3;
                    dual = Bit(10) == 1;
                    high = Bit(9) == 1;
                    switch ((b >> 7) & 3)
                    {
                        case 0: gw = 12; gh = A + 2; break;
                        case 1: gw = A + 2; gh = 12; break;
                        case 2:
                            gw = A + 6; gh = ((b >> 9) & 3) + 6;
                            dual = false; high = false;
                            break;
                        default:
                            if (Bit(6) == 1) return Invalid;                  // 11 1x: reserved (void extent handled before)
                            if (Bit(5) == 0) { gw = 6; gh = 10; } else { gw = 10; gh = 6; }
                            break;
                    }
                }
                if (r < 2) return Invalid;
                int range = (high ? 6 : 0) + r - 2;
                int count = gw * gh * (dual ? 2 : 1);
                if (gw > bw || gh > bh || count > 64) return Invalid;
                int bits = IseBits(count, WeightRanges[range]);
                if (bits < 24 || bits > 96) return Invalid;
                var m = new ModeInfo
                {
                    Valid = true, GridWidth = gw, GridHeight = gh, Dual = dual, WeightRange = range, WeightCount = count, WeightBits = bits,
                };
                m.MakeInfill(bw, bh);
                return m;
            }

            // 23.18, 2D: bilinear from the grid, in 1/16ths
            void MakeInfill(int bw, int bh)
            {
                int n = bw * bh, last = GridWidth * GridHeight - 1;
                InfillIndex = new int[n * 4];
                InfillFactor = new int[n * 4];
                int ds = (1024 + bw / 2) / (bw - 1), dt = (1024 + bh / 2) / (bh - 1);
                for (int t = 0; t < bh; t++)
                {
                    for (int s = 0; s < bw; s++)
                    {
                        int gs = (ds * s * (GridWidth - 1) + 32) >> 6, gt = (dt * t * (GridHeight - 1) + 32) >> 6;
                        int js = gs >> 4, fs = gs & 15, jt = gt >> 4, ft = gt & 15;
                        int v0 = js + jt * GridWidth;
                        int w11 = (fs * ft + 8) >> 4, w10 = ft - w11, w01 = fs - w11, w00 = 16 - fs - ft + w11;
                        int o = (t * bw + s) * 4;
                        // a grid point past the edge only ever gets factor 0; clamp its index so it stays in range
                        InfillIndex[o] = Math.Min(v0, last);
                        InfillIndex[o + 1] = Math.Min(v0 + 1, last);
                        InfillIndex[o + 2] = Math.Min(v0 + GridWidth, last);
                        InfillIndex[o + 3] = Math.Min(v0 + GridWidth + 1, last);
                        InfillFactor[o] = w00;
                        InfillFactor[o + 1] = w01;
                        InfillFactor[o + 2] = w10;
                        InfillFactor[o + 3] = w11;
                    }
                }
            }
        }

        // ---------------------------------------------------------------- integer sequence encoding (23.12)
        readonly struct Range
        {
            public readonly int Levels, Trits, Quints, Bits;
            public Range(int levels, int trits, int quints, int bits) { Levels = levels; Trits = trits; Quints = quints; Bits = bits; }
        }

        // the weight ranges, index = (P ? 6 : 0) + R - 2 (Table 23.8)
        static readonly Range[] WeightRanges =
        {
            new(2, 0, 0, 1), new(3, 1, 0, 0), new(4, 0, 0, 2), new(5, 0, 1, 0), new(6, 1, 0, 1), new(8, 0, 0, 3),
            new(10, 0, 1, 1), new(12, 1, 0, 2), new(16, 0, 0, 4), new(20, 0, 1, 2), new(24, 1, 0, 3), new(32, 0, 0, 5),
        };

        // every range, ascending; colour endpoints use the largest that fits, and never fewer than 6 levels
        static readonly Range[] ColorRanges =
        {
            new(2, 0, 0, 1), new(3, 1, 0, 0), new(4, 0, 0, 2), new(5, 0, 1, 0), new(6, 1, 0, 1), new(8, 0, 0, 3),
            new(10, 0, 1, 1), new(12, 1, 0, 2), new(16, 0, 0, 4), new(20, 0, 1, 2), new(24, 1, 0, 3), new(32, 0, 0, 5),
            new(40, 0, 1, 3), new(48, 1, 0, 4), new(64, 0, 0, 6), new(80, 0, 1, 4), new(96, 1, 0, 5), new(128, 0, 0, 7),
            new(160, 0, 1, 5), new(192, 1, 0, 6), new(256, 0, 0, 8),
        };
        const int FirstColorRange = 4;   // 0..5

        static int IseBits(int count, Range r) =>
            count * r.Bits + (r.Trits != 0 ? (8 * count + 4) / 5 : 0) + (r.Quints != 0 ? (7 * count + 2) / 3 : 0);

        // five trits from 8 bits, three quints from 7 bits (the chapter's bit manipulations, tabulated)
        static readonly byte[] TritTable = MakeTrits();
        static readonly byte[] QuintTable = MakeQuints();

        static byte[] MakeTrits()
        {
            var t = new byte[256 * 5];
            for (int T = 0; T < 256; T++)
            {
                int Bit(int i) => (T >> i) & 1;
                int c, t0, t1, t2, t3, t4;
                if (((T >> 2) & 7) == 7)
                {
                    c = (((T >> 5) & 7) << 2) | (T & 3);
                    t4 = 2; t3 = 2;
                }
                else
                {
                    c = T & 0x1f;
                    if (((T >> 5) & 3) == 3) { t4 = 2; t3 = Bit(7); }
                    else { t4 = Bit(7); t3 = (T >> 5) & 3; }
                }
                int C(int i) => (c >> i) & 1;
                if ((c & 3) == 3) { t2 = 2; t1 = C(4); t0 = (C(3) << 1) | (C(2) & ~C(3) & 1); }
                else if (((c >> 2) & 3) == 3) { t2 = 2; t1 = 2; t0 = c & 3; }
                else { t2 = C(4); t1 = (c >> 2) & 3; t0 = (C(1) << 1) | (C(0) & ~C(1) & 1); }
                t[T * 5] = (byte)t0; t[T * 5 + 1] = (byte)t1; t[T * 5 + 2] = (byte)t2; t[T * 5 + 3] = (byte)t3; t[T * 5 + 4] = (byte)t4;
            }
            return t;
        }

        static byte[] MakeQuints()
        {
            var t = new byte[128 * 3];
            for (int Q = 0; Q < 128; Q++)
            {
                int Bit(int i) => (Q >> i) & 1;
                int q0, q1, q2;
                if (((Q >> 1) & 3) == 3 && ((Q >> 5) & 3) == 0)
                {
                    q2 = (Bit(0) << 2) | ((Bit(4) & ~Bit(0) & 1) << 1) | (Bit(3) & ~Bit(0) & 1);
                    q1 = 4; q0 = 4;
                }
                else
                {
                    int c;
                    if (((Q >> 1) & 3) == 3)
                    {
                        q2 = 4;
                        c = (((Q >> 3) & 3) << 3) | ((~(Q >> 5) & 3) << 1) | Bit(0);
                    }
                    else
                    {
                        q2 = (Q >> 5) & 3;
                        c = Q & 0x1f;
                    }
                    if ((c & 7) == 5) { q1 = 4; q0 = (c >> 3) & 3; }
                    else { q1 = (c >> 3) & 3; q0 = c & 7; }
                }
                t[Q * 3] = (byte)q0; t[Q * 3 + 1] = (byte)q1; t[Q * 3 + 2] = (byte)q2;
            }
            return t;
        }

        // `count` values of range r from the bit stream starting at `start`; bits at or past `limit` read as 0
        static void DecodeIse(ulong lo, ulong hi, int start, int limit, int count, Range r, Span<int> output)
        {
            int n = r.Bits;
            Span<uint> m = stackalloc uint[5];
            if (r.Trits != 0)
            {
                int blockBits = 8 + 5 * n;
                for (int k = 0; 5 * k < count; k++)
                {
                    int p = start + k * blockBits;
                    // m0 T1:0 m1 T3:2 m2 T4 m3 T6:5 m4 T7
                    m[0] = Bits(lo, hi, p, n, limit);
                    uint t = Bits(lo, hi, p + n, 2, limit);
                    m[1] = Bits(lo, hi, p + n + 2, n, limit);
                    t |= Bits(lo, hi, p + 2 * n + 2, 2, limit) << 2;
                    m[2] = Bits(lo, hi, p + 2 * n + 4, n, limit);
                    t |= Bits(lo, hi, p + 3 * n + 4, 1, limit) << 4;
                    m[3] = Bits(lo, hi, p + 3 * n + 5, n, limit);
                    t |= Bits(lo, hi, p + 4 * n + 5, 2, limit) << 5;
                    m[4] = Bits(lo, hi, p + 4 * n + 7, n, limit);
                    t |= Bits(lo, hi, p + 5 * n + 7, 1, limit) << 7;
                    int T = (int)t;
                    for (int i = 0; i < 5 && 5 * k + i < count; i++) output[5 * k + i] = (TritTable[T * 5 + i] << n) | (int)m[i];
                }
            }
            else if (r.Quints != 0)
            {
                int blockBits = 7 + 3 * n;
                for (int k = 0; 3 * k < count; k++)
                {
                    int p = start + k * blockBits;
                    // m0 Q2:0 m1 Q4:3 m2 Q6:5
                    m[0] = Bits(lo, hi, p, n, limit);
                    uint q = Bits(lo, hi, p + n, 3, limit);
                    m[1] = Bits(lo, hi, p + n + 3, n, limit);
                    q |= Bits(lo, hi, p + 2 * n + 3, 2, limit) << 3;
                    m[2] = Bits(lo, hi, p + 2 * n + 5, n, limit);
                    q |= Bits(lo, hi, p + 3 * n + 5, 2, limit) << 5;
                    int Q = (int)q;
                    for (int i = 0; i < 3 && 3 * k + i < count; i++) output[3 * k + i] = (QuintTable[Q * 3 + i] << n) | (int)m[i];
                }
            }
            else
            {
                for (int i = 0; i < count; i++) output[i] = (int)Bits(lo, hi, start + i * n, n, limit);
            }
        }

        // ---------------------------------------------------------------- unquantisation (23.13, 23.17)
        static int Replicate(int v, int from, int to)
        {
            int r = 0, have = 0;
            while (have < to)
            {
                r = (r << from) | v;
                have += from;
            }
            return r >> (have - to);
        }

        // colour values to 0..255, per colour range
        static readonly byte[][] ColorUnquant = MakeColorUnquant();

        static byte[][] MakeColorUnquant()
        {
            var t = new byte[ColorRanges.Length][];
            for (int q = 0; q < ColorRanges.Length; q++)
            {
                Range r = ColorRanges[q];
                t[q] = new byte[r.Levels];
                for (int v = 0; v < r.Levels; v++)
                {
                    int n = r.Bits;
                    if (r.Trits == 0 && r.Quints == 0)
                    {
                        t[q][v] = (byte)Replicate(v, n, 8);
                        continue;
                    }
                    if (n == 0) continue;   // 0..2 and 0..4 are never colour ranges
                    int low = v & ((1 << n) - 1), d = v >> n;
                    int a = (low & 1) != 0 ? 0x1ff : 0;
                    int b = 0, c;
                    if (r.Trits != 0)
                    {
                        switch (n)
                        {
                            case 1: c = 204; break;
                            case 2: { int x = (low >> 1) & 1; b = x << 8 | x << 4 | x << 2 | x << 1; c = 93; break; }
                            case 3: { int x = (low >> 1) & 3; b = x << 7 | x << 2 | x; c = 44; break; }
                            case 4: { int x = (low >> 1) & 7; b = x << 6 | x; c = 22; break; }
                            case 5: { int x = (low >> 1) & 15; b = x << 5 | x >> 2; c = 11; break; }
                            default: { int x = (low >> 1) & 31; b = x << 4 | x >> 4; c = 5; break; }
                        }
                    }
                    else
                    {
                        switch (n)
                        {
                            case 1: c = 113; break;
                            case 2: { int x = (low >> 1) & 1; b = x << 8 | x << 3 | x << 2; c = 54; break; }
                            case 3: { int x = (low >> 1) & 3; b = x << 7 | x << 1 | x >> 1; c = 26; break; }
                            case 4: { int x = (low >> 1) & 7; b = x << 6 | x >> 1; c = 13; break; }
                            default: { int x = (low >> 1) & 15; b = x << 5 | x >> 3; c = 6; break; }
                        }
                    }
                    int u = d * c + b;
                    u ^= a;
                    t[q][v] = (byte)((a & 0x80) | (u >> 2));
                }
            }
            return t;
        }

        // weight values to 0..64, per weight range
        static readonly byte[][] WeightUnquant = MakeWeightUnquant();

        static byte[][] MakeWeightUnquant()
        {
            var t = new byte[WeightRanges.Length][];
            for (int q = 0; q < WeightRanges.Length; q++)
            {
                Range r = WeightRanges[q];
                t[q] = new byte[r.Levels];
                for (int v = 0; v < r.Levels; v++)
                {
                    int n = r.Bits, u;
                    if (r.Trits == 0 && r.Quints == 0) u = Replicate(v, n, 6);
                    else if (n == 0) u = r.Trits != 0 ? new[] { 0, 32, 63 }[v] : new[] { 0, 16, 32, 47, 63 }[v];
                    else
                    {
                        int low = v & ((1 << n) - 1), d = v >> n;
                        int a = (low & 1) != 0 ? 0x7f : 0;
                        int b = 0, c;
                        if (r.Trits != 0)
                        {
                            switch (n)
                            {
                                case 1: c = 50; break;
                                case 2: { int x = (low >> 1) & 1; b = x << 6 | x << 2 | x; c = 23; break; }
                                default: { int x = (low >> 1) & 3; b = x << 5 | x; c = 11; break; }
                            }
                        }
                        else
                        {
                            switch (n)
                            {
                                case 1: c = 28; break;
                                default: { int x = (low >> 1) & 1; b = x << 6 | x << 1; c = 13; break; }
                            }
                        }
                        u = d * c + b;
                        u ^= a;
                        u = (a & 0x20) | (u >> 2);
                    }
                    if (u > 32) u++;
                    t[q][v] = (byte)u;
                }
            }
            return t;
        }

        // ---------------------------------------------------------------- colour endpoint modes (23.14)
        // e = e0 rgba, e1 rgba (0..255); false for an HDR mode (the error colour in LDR)
        static bool Endpoints(int cem, Span<int> v, Span<int> e)
        {
            switch (cem)
            {
                case 0:
                    Set(e, v[0], v[0], v[0], 255, v[1], v[1], v[1], 255);
                    return true;
                case 1:
                {
                    int l0 = (v[0] >> 2) | (v[1] & 0xc0);
                    int l1 = Math.Min(l0 + (v[1] & 0x3f), 255);
                    Set(e, l0, l0, l0, 255, l1, l1, l1, 255);
                    return true;
                }
                case 4:
                    Set(e, v[0], v[0], v[0], v[2], v[1], v[1], v[1], v[3]);
                    return true;
                case 5:
                {
                    int v0 = v[0], v1 = v[1], v2 = v[2], v3 = v[3];
                    BitTransferSigned(ref v1, ref v0);
                    BitTransferSigned(ref v3, ref v2);
                    Set(e, v0, v0, v0, v2, v0 + v1, v0 + v1, v0 + v1, v2 + v3);
                    Clamp(e);
                    return true;
                }
                case 6:
                    Set(e, v[0] * v[3] >> 8, v[1] * v[3] >> 8, v[2] * v[3] >> 8, 255, v[0], v[1], v[2], 255);
                    return true;
                case 8:
                case 12:
                {
                    int a0 = cem == 12 ? v[6] : 255, a1 = cem == 12 ? v[7] : 255;
                    int s0 = v[0] + v[2] + v[4], s1 = v[1] + v[3] + v[5];
                    if (s1 >= s0) Set(e, v[0], v[2], v[4], a0, v[1], v[3], v[5], a1);
                    else
                    {
                        BlueContract(e.Slice(0, 4), v[1], v[3], v[5], a1);
                        BlueContract(e.Slice(4, 4), v[0], v[2], v[4], a0);
                    }
                    return true;
                }
                case 9:
                case 13:
                {
                    int v0 = v[0], v1 = v[1], v2 = v[2], v3 = v[3], v4 = v[4], v5 = v[5];
                    int v6 = cem == 13 ? v[6] : 255, v7 = cem == 13 ? v[7] : 0;
                    BitTransferSigned(ref v1, ref v0);
                    BitTransferSigned(ref v3, ref v2);
                    BitTransferSigned(ref v5, ref v4);
                    if (cem == 13) BitTransferSigned(ref v7, ref v6);
                    int a0 = v6, a1 = cem == 13 ? v6 + v7 : 255;
                    if (v1 + v3 + v5 >= 0) Set(e, v0, v2, v4, a0, v0 + v1, v2 + v3, v4 + v5, a1);
                    else
                    {
                        BlueContract(e.Slice(0, 4), v0 + v1, v2 + v3, v4 + v5, a1);
                        BlueContract(e.Slice(4, 4), v0, v2, v4, a0);
                    }
                    Clamp(e);
                    return true;
                }
                case 10:
                    Set(e, v[0] * v[3] >> 8, v[1] * v[3] >> 8, v[2] * v[3] >> 8, v[4], v[0], v[1], v[2], v[5]);
                    return true;
                default:
                    return false;   // 2, 3, 7, 11, 14, 15: HDR
            }
        }

        static void Set(Span<int> e, int r0, int g0, int b0, int a0, int r1, int g1, int b1, int a1)
        {
            e[0] = r0; e[1] = g0; e[2] = b0; e[3] = a0; e[4] = r1; e[5] = g1; e[6] = b1; e[7] = a1;
        }

        static void BitTransferSigned(ref int a, ref int b)
        {
            b >>= 1;
            b |= a & 0x80;
            a >>= 1;
            a &= 0x3f;
            if ((a & 0x20) != 0) a -= 0x40;
        }

        static void BlueContract(Span<int> e, int r, int g, int b, int a)
        {
            e[0] = (r + b) >> 1; e[1] = (g + b) >> 1; e[2] = b; e[3] = a;
        }

        static void Clamp(Span<int> e)
        {
            for (int i = 0; i < 8; i++) e[i] = Math.Clamp(e[i], 0, 255);
        }

        // ---------------------------------------------------------------- partitions (23.21)
        byte[] PartitionMap(int parts, int seed)
        {
            int key = (parts - 2) * 1024 + seed;
            byte[]? map = partitionMaps[key];
            if (map != null) return map;
            map = new byte[texels];
            bool small = texels < 31;
            for (int y = 0; y < BlockHeight; y++)
            {
                for (int x = 0; x < BlockWidth; x++) map[y * BlockWidth + x] = (byte)SelectPartition(seed, x, y, 0, parts, small);
            }
            return partitionMaps[key] = map;
        }

        static uint Hash52(uint p)
        {
            p ^= p >> 15; p -= p << 17; p += p << 7; p += p << 4;
            p ^= p >> 5; p += p << 16; p ^= p >> 7; p ^= p >> 3;
            p ^= p << 6; p ^= p >> 17;
            return p;
        }

        static int SelectPartition(int seed, int x, int y, int z, int count, bool small)
        {
            if (small) { x <<= 1; y <<= 1; z <<= 1; }
            seed += (count - 1) * 1024;
            uint rnum = Hash52((uint)seed);
            int s1 = (int)(rnum & 0xf), s2 = (int)((rnum >> 4) & 0xf), s3 = (int)((rnum >> 8) & 0xf), s4 = (int)((rnum >> 12) & 0xf);
            int s5 = (int)((rnum >> 16) & 0xf), s6 = (int)((rnum >> 20) & 0xf), s7 = (int)((rnum >> 24) & 0xf), s8 = (int)((rnum >> 28) & 0xf);
            int s9 = (int)((rnum >> 18) & 0xf), s10 = (int)((rnum >> 22) & 0xf), s11 = (int)((rnum >> 26) & 0xf);
            int s12 = (int)(((rnum >> 30) | (rnum << 2)) & 0xf);
            s1 *= s1; s2 *= s2; s3 *= s3; s4 *= s4; s5 *= s5; s6 *= s6;
            s7 *= s7; s8 *= s8; s9 *= s9; s10 *= s10; s11 *= s11; s12 *= s12;
            int sh1, sh2;
            if ((seed & 1) != 0)
            {
                sh1 = (seed & 2) != 0 ? 4 : 5;
                sh2 = count == 3 ? 6 : 5;
            }
            else
            {
                sh1 = count == 3 ? 6 : 5;
                sh2 = (seed & 2) != 0 ? 4 : 5;
            }
            int sh3 = (seed & 0x10) != 0 ? sh1 : sh2;
            s1 >>= sh1; s2 >>= sh2; s3 >>= sh1; s4 >>= sh2; s5 >>= sh1; s6 >>= sh2; s7 >>= sh1; s8 >>= sh2;
            s9 >>= sh3; s10 >>= sh3; s11 >>= sh3; s12 >>= sh3;
            int a = (int)((s1 * x + s2 * y + s11 * z + (rnum >> 14)) & 0x3f);
            int b = (int)((s3 * x + s4 * y + s12 * z + (rnum >> 10)) & 0x3f);
            int c = (int)((s5 * x + s6 * y + s9 * z + (rnum >> 6)) & 0x3f);
            int d = (int)((s7 * x + s8 * y + s10 * z + (rnum >> 2)) & 0x3f);
            if (count < 4) d = 0;
            if (count < 3) c = 0;
            if (a >= b && a >= c && a >= d) return 0;
            if (b >= c && b >= d) return 1;
            if (c >= d) return 2;
            return 3;
        }
    }
}
