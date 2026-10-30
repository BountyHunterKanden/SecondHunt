using System;
using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;

namespace MphRecomp.Import.Beyond
{
    // Beyond's packed buffers (spec section 3): a u32 packing code, then the packed bytes; the caller knows the unpacked
    // length, and unpacking stops once exactly that many bytes are written. Codes:
    //   0           stored as is
    //   1, 2, 3     flagged copy coding, unit 1, 2 or 4 bytes (a flag byte per 8 items; set bit = 2-byte copy)
    //   12, 13, 14  bit-coded copy coding, unit 1, 2 or 4 bytes (one MSB-first bit stream, three fixed prefix codes)
    //   15          a zlib stream
    // Every copy runs forward one byte at a time, so an overlapping source repeats what was just written. A copy reaching
    // before the output's start, a read past the packed data, or too few bytes written is a damaged buffer (an exception).
    public static class BeyondUnpack
    {
        public static byte[] Unpack(ReadOnlySpan<byte> packed, int unpackedLength, string what)
        {
            var output = new byte[unpackedLength];
            Unpack(packed, output, what);
            return output;
        }

        public static void Unpack(ReadOnlySpan<byte> packed, Span<byte> output, string what)
        {
            if (packed.Length < 4) throw new InvalidDataException($"{what}: packed buffer of {packed.Length} bytes has no packing code");
            uint code = BinaryPrimitives.ReadUInt32LittleEndian(packed);
            ReadOnlySpan<byte> data = packed.Slice(4);
            switch (code)
            {
                case 0:
                    if (data.Length < output.Length) throw Damaged(what, code, "stored data is shorter than the unpacked length");
                    data.Slice(0, output.Length).CopyTo(output);
                    break;
                case 1: case 2: case 3:
                    Flagged(data, output, (int)code - 1, what, code);
                    break;
                case 12: case 13: case 14:
                    BitCoded(data, output, (int)code - 12, what, code);
                    break;
                case 15:
                    Zlib(data, output, what, code);
                    break;
                default:
                    throw new NotSupportedException($"{what}: packing code {code} is not supported");
            }
        }

        static InvalidDataException Damaged(string what, uint code, string why) =>
            new InvalidDataException($"{what}: damaged packed buffer (code {code}): {why}");

        // 3.1: a flag byte before every group of 8 items, bits from 0x80 down; clear = one literal unit, set = a copy of
        // (c0 >> 4) + 3 - g units from ((c0 & 15) * 256 + c1) units back
        static void Flagged(ReadOnlySpan<byte> data, Span<byte> output, int g, string what, uint code)
        {
            int u = 1 << g;
            int ip = 0, op = 0, n = output.Length;
            int flags = 0, left = 0;
            while (op < n)
            {
                if (left == 0)
                {
                    if (ip >= data.Length) throw Damaged(what, code, $"data ends after {op} of {n} bytes");
                    flags = data[ip++];
                    left = 8;
                }
                bool copy = (flags & 0x80) != 0;
                flags <<= 1;
                left--;
                if (!copy)
                {
                    int k = Math.Min(u, n - op);
                    if (ip + k > data.Length) throw Damaged(what, code, $"literal runs past the data at output {op}");
                    data.Slice(ip, k).CopyTo(output.Slice(op));
                    ip += u;
                    op += k;
                }
                else
                {
                    if (ip + 2 > data.Length) throw Damaged(what, code, $"copy runs past the data at output {op}");
                    int c0 = data[ip], c1 = data[ip + 1];
                    ip += 2;
                    int len = ((c0 >> 4) + 3 - g) * u;
                    int dist = ((c0 & 15) * 256 + c1) * u;
                    CopyBack(output, ref op, dist, len, what, code);
                }
            }
        }

        static void CopyBack(Span<byte> output, ref int op, int dist, int len, string what, uint code)
        {
            if (dist == 0) throw Damaged(what, code, $"copy of distance 0 at output {op}");
            int src = op - dist;
            if (src < 0) throw Damaged(what, code, $"copy from {dist} bytes back at output {op} starts before the output");
            int end = Math.Min(op + len, output.Length);
            if (dist >= len && end - op > 16)
            {
                output.Slice(src, end - op).CopyTo(output.Slice(op));
                op = end;
                return;
            }
            while (op < end) output[op++] = output[src++];
        }

        // 3.2 ---------------------------------------------------------------------------------------------------------
        // The three canonical prefix codes (appendix A): per code length, the symbols in code order.
        sealed class PrefixCode
        {
            public readonly int MaxLength;
            public readonly int[] First, Count, Offset;   // per length: the first code, the symbol count, where they start
            public readonly byte[] Symbols;

            public PrefixCode(int[][] byLength)   // byLength[len] = symbols of that length (index 0 unused)
            {
                MaxLength = byLength.Length - 1;
                First = new int[MaxLength + 1];
                Count = new int[MaxLength + 1];
                Offset = new int[MaxLength + 1];
                var syms = new System.Collections.Generic.List<byte>();
                int code = 0;
                long kraft = 0;
                for (int len = 1; len <= MaxLength; len++)
                {
                    int[] s = byLength[len] ?? Array.Empty<int>();
                    First[len] = code;
                    Count[len] = s.Length;
                    Offset[len] = syms.Count;
                    foreach (int v in s) syms.Add((byte)v);
                    kraft += (long)s.Length << (MaxLength - len);
                    code = (code + s.Length) << 1;
                }
                Symbols = syms.ToArray();
                if (Symbols.Length != 256 || kraft != 1L << MaxLength)
                {
                    throw new InvalidOperationException("Beyond unpack: a prefix-code table is not complete");
                }
            }
        }

        static int[][] Lengths(int max, params (int Len, int[] Syms)[] rows)
        {
            var t = new int[max + 1][];
            foreach (var (len, syms) in rows) t[len] = syms;
            return t;
        }

        static readonly PrefixCode LengthCode = new(Lengths(18,
            (1, new[] { 0 }),
            (2, new[] { 1 }),
            (4, new[] { 2 }),
            (5, new[] { 3, 4, 5 }),
            (6, new[] { 6, 7, 9 }),
            (7, new[] { 8, 255 }),
            (8, new[] { 10 }),
            (9, new[] { 11, 12, 13, 14, 15 }),
            (10, new[] { 16, 17, 19 }),
            (11, new[] { 18, 20, 21, 22, 30, 31, 47, 63, 71 }),
            (12, new[] { 23, 24, 25, 27, 32, 33, 35, 38, 39, 51, 62 }),
            (13, new[] { 26, 28, 29, 34, 36, 37, 40, 41, 43, 46, 48, 54, 55, 64, 65, 70, 72, 94, 95, 103, 123, 126, 127, 131 }),
            (14, new[] { 42, 44, 45, 49, 50, 52, 53, 56, 57, 59, 61, 66, 67, 69, 73, 74, 78, 79, 83, 86, 87, 96, 97, 102, 111, 142, 143, 159, 199, 247 }),
            (15, new[] { 58, 60, 68, 75, 76, 77, 80, 81, 82, 88, 91, 93, 98, 99, 104, 105, 106, 107, 110, 118, 119, 125, 128, 129, 130, 134, 135, 144, 147, 163, 165, 175, 176, 179, 183, 185, 189, 190, 191, 239, 243, 246, 249, 251, 252, 253 }),
            (16, new[] { 84, 85, 89, 90, 92, 100, 101, 108, 109, 112, 113, 115, 117, 120, 121, 122, 124, 132, 133, 136, 137, 139, 141, 145, 146, 148, 149, 150, 151, 155, 158, 160, 164, 166, 167, 171, 172, 173, 174, 177, 180, 181, 182, 184, 186, 187, 195, 198, 201, 203, 206, 207, 211, 215, 219, 222, 223, 227, 229, 230, 231, 235, 238, 240, 241, 244, 245, 248, 250, 254 }),
            (17, new[] { 114, 116, 138, 140, 152, 153, 154, 156, 157, 161, 162, 168, 169, 170, 178, 188, 192, 193, 194, 196, 197, 200, 202, 204, 205, 208, 209, 210, 212, 213, 214, 216, 220, 221, 224, 225, 228, 232, 233, 236, 237, 242 }),
            (18, new[] { 217, 218, 226, 234 })));

        static readonly PrefixCode LowDistanceCode = new(Lengths(12,
            (5, new[] { 0, 8, 16, 24, 32 }),
            (6, new[] { 4, 12, 18, 36, 40, 48, 56, 64, 72, 80, 88, 96, 104, 112, 120, 128, 136, 144, 152, 160, 168, 176, 184, 192, 200, 208, 216, 224, 232, 240, 248 }),
            (7, new[] { 1, 20, 28, 44, 52, 60, 68, 76, 84, 92, 100, 108, 116, 124, 132, 140, 156, 164, 172, 180, 188, 196, 204, 212, 228, 244, 252 }),
            (8, new[] { 2, 6, 14, 54, 148, 220, 236 }),
            (9, new[] { 3, 9, 10, 22, 30, 42, 66, 90, 126, 162, 198 }),
            (10, new[] { 5, 7, 11, 15, 23, 26, 34, 38, 46, 50, 58, 62, 70, 74, 78, 82, 86, 94, 98, 102, 106, 110, 114, 118, 122, 130, 134, 138, 142, 146, 150, 154, 158, 166, 170, 174, 178, 182, 186, 190, 194, 202, 206, 210, 214, 218, 222, 226, 230, 234, 238, 242, 246, 250, 254, 255 }),
            (11, new[] { 13, 17, 21, 25, 27, 31, 33, 39, 41, 45, 47, 49, 55, 57, 63, 65, 69, 71, 73, 79, 81, 87, 89, 95, 97, 103, 105, 107, 109, 111, 113, 119, 121, 127, 129, 135, 137, 143, 145, 151, 153, 159, 161, 167, 169, 175, 177, 183, 185, 191, 193, 199, 201, 207, 209, 215, 217, 223, 225, 231, 233, 239, 241, 247, 249 }),
            (12, new[] { 19, 29, 35, 37, 43, 51, 53, 59, 61, 67, 75, 77, 83, 85, 91, 93, 99, 101, 115, 117, 123, 125, 131, 133, 139, 141, 147, 149, 155, 157, 163, 165, 171, 173, 179, 181, 187, 189, 195, 197, 203, 205, 211, 213, 219, 221, 227, 229, 235, 237, 243, 245, 251, 253 })));

        static readonly PrefixCode HighDistanceCode = new(Lengths(10,
            (2, new[] { 0 }),
            (5, new[] { 1, 2, 7 }),
            (6, new[] { 3, 4, 5, 6, 8, 14, 15, 16 }),
            (7, new[] { 9, 10, 11, 12, 13, 17, 18, 19, 20, 23, 24, 28, 30, 31, 32, 48 }),
            (8, new[] { 21, 22, 25, 26, 27, 29, 33, 34, 35, 36, 37, 38, 39, 40, 41, 42, 43, 44, 45, 46, 47, 49, 50, 51, 53, 54, 55, 56, 62, 63, 64, 65, 79, 80, 88, 95, 96, 176 }),
            (9, new[] { 52, 57, 58, 59, 60, 61, 66, 67, 68, 69, 70, 71, 72, 73, 74, 75, 76, 77, 78, 81, 82, 83, 84, 85, 86, 87, 89, 90, 91, 92, 93, 94, 97, 98, 99, 100, 101, 102, 103, 104, 105, 106, 107, 108, 109, 110, 111, 112, 113, 114, 115, 116, 118, 119, 120, 121, 125, 126, 127, 128, 129, 130, 134, 135, 136, 137, 143, 144, 159, 160, 168, 175, 177, 192 }),
            (10, new[] { 117, 122, 123, 124, 131, 132, 133, 138, 139, 140, 141, 142, 145, 146, 147, 148, 149, 150, 151, 152, 153, 154, 155, 156, 157, 158, 161, 162, 163, 164, 165, 166, 167, 169, 170, 171, 172, 173, 174, 178, 179, 180, 181, 182, 183, 184, 185, 186, 187, 188, 189, 190, 191, 193, 194, 195, 196, 197, 198, 199, 200, 201, 202, 203, 204, 205, 206, 207, 208, 209, 210, 211, 212, 213, 214, 215, 216, 217, 218, 219, 220, 221, 222, 223, 224, 225, 226, 227, 228, 229, 230, 231, 232, 233, 234, 235, 236, 237, 238, 239, 240, 241, 242, 243, 244, 245, 246, 247, 248, 249, 250, 251, 252, 253, 254, 255 })));

        // MSB-first bit reader over the packed data; Peek pads past the end with zeros, Consume checks the real end
        ref struct BitReader
        {
            readonly ReadOnlySpan<byte> data;
            ulong buf;      // the next `have` bits, left-aligned
            int have;
            int next;       // next byte to load
            long consumed, total;

            public BitReader(ReadOnlySpan<byte> d)
            {
                data = d; buf = 0; have = 0; next = 0; consumed = 0; total = (long)d.Length * 8;
            }

            void Fill()
            {
                while (have <= 56)
                {
                    ulong v = next < data.Length ? data[next] : 0UL;
                    next++;
                    buf |= v << (56 - have);
                    have += 8;
                }
            }

            public uint Peek(int n)   // n <= 32
            {
                if (have < n) Fill();
                return (uint)(buf >> (64 - n));
            }

            public bool Consume(int n)
            {
                buf <<= n;
                have -= n;
                consumed += n;
                return consumed <= total;
            }

            public bool Bit(out int bit)
            {
                bit = (int)Peek(1);
                return Consume(1);
            }

            public bool Decode(PrefixCode c, out int symbol)
            {
                uint bits = Peek(c.MaxLength);
                for (int len = 1; len <= c.MaxLength; len++)
                {
                    int code = (int)(bits >> (c.MaxLength - len));
                    int k = code - c.First[len];
                    if (k >= 0 && k < c.Count[len])
                    {
                        symbol = c.Symbols[c.Offset[len] + k];
                        return Consume(len);
                    }
                }
                symbol = 0;
                return false;   // not reachable with complete codes
            }
        }

        // 3.2: bit 0 = a literal unit (u bytes of 8 bits each); bit 1 = a copy: length L, low distance, high distance
        static void BitCoded(ReadOnlySpan<byte> data, Span<byte> output, int g, string what, uint code)
        {
            int u = 1 << g;
            int op = 0, n = output.Length;
            var br = new BitReader(data);
            while (op < n)
            {
                if (!br.Bit(out int flag)) throw Damaged(what, code, $"bit stream ends after {op} of {n} bytes");
                if (flag == 0)
                {
                    for (int i = 0; i < u; i++)
                    {
                        int v = (int)br.Peek(8);
                        if (!br.Consume(8)) throw Damaged(what, code, $"literal runs past the data at output {op}");
                        if (op < n) output[op++] = (byte)v;
                    }
                }
                else
                {
                    if (!br.Decode(LengthCode, out int l) || !br.Decode(LowDistanceCode, out int dLow) || !br.Decode(HighDistanceCode, out int dHigh))
                    {
                        throw Damaged(what, code, $"copy runs past the data at output {op}");
                    }
                    int dist = (dHigh * 256 + dLow) * u;
                    int len = (l + 3 - g) * u;
                    CopyBack(output, ref op, dist, len, what, code);
                }
            }
        }

        static void Zlib(ReadOnlySpan<byte> data, Span<byte> output, string what, uint code)
        {
            try
            {
                using var z = new ZLibStream(new MemoryStream(data.ToArray(), writable: false), CompressionMode.Decompress);
                int op = 0;
                while (op < output.Length)
                {
                    int r = z.Read(output.Slice(op));
                    if (r <= 0) throw Damaged(what, code, $"zlib stream ends after {op} of {output.Length} bytes");
                    op += r;
                }
            }
            catch (InvalidDataException e) when (!e.Message.Contains("damaged packed buffer"))
            {
                throw Damaged(what, code, e.Message);
            }
        }
    }
}
