using System;
using System.Buffers.Binary;
using System.IO;

namespace MphRecomp.Import.Beyond
{
    // A BC7 decoder (spec 6.5) to 8-bit RGBA, after Microsoft's Direct3D 11 BC7 format documentation: 8 modes (the mode is
    // the count of low zero bits before the first 1), partitions with their fix-up (anchor) indices, P-bits, endpoint
    // expansion by repeating the top bits, the 2/3/4-bit weight tables ((64 - w) e0 + w e1 + 32) >> 6, and the channel
    // rotation of modes 4 and 5. A block with no valid mode (low byte 0) decodes to all zero.
    public static class Bc7BlockDecoder
    {
        // per mode: subsets, partition bits, rotation bits, index-selector bits, colour bits, alpha bits, endpoint P-bits,
        // shared P-bits, index bits, second index bits
        static readonly int[,] Modes =
        {
            { 3, 4, 0, 0, 4, 0, 1, 0, 3, 0 },
            { 2, 6, 0, 0, 6, 0, 0, 1, 3, 0 },
            { 3, 6, 0, 0, 5, 0, 0, 0, 2, 0 },
            { 2, 6, 0, 0, 7, 0, 1, 0, 2, 0 },
            { 1, 0, 2, 1, 5, 6, 0, 0, 2, 3 },
            { 1, 0, 2, 0, 7, 8, 0, 0, 2, 2 },
            { 1, 0, 0, 0, 7, 7, 1, 0, 4, 0 },
            { 2, 6, 0, 0, 5, 5, 1, 0, 2, 0 },
        };

        static readonly int[] Weights2 = { 0, 21, 43, 64 };
        static readonly int[] Weights3 = { 0, 9, 18, 27, 37, 46, 55, 64 };
        static readonly int[] Weights4 = { 0, 4, 9, 13, 17, 21, 26, 30, 34, 38, 43, 47, 51, 55, 60, 64 };

        // two-subset partitions: bit i = the subset of texel i (texels row by row)
        static readonly ushort[] Partitions2 =
        {
            0xcccc, 0x8888, 0xeeee, 0xecc8, 0xc880, 0xfeec, 0xfec8, 0xec80, 0xc800, 0xffec, 0xfe80, 0xe800, 0xffe8, 0xff00, 0xfff0, 0xf000,
            0xf710, 0x008e, 0x7100, 0x08ce, 0x008c, 0x7310, 0x3100, 0x8cce, 0x088c, 0x3110, 0x6666, 0x366c, 0x17e8, 0x0ff0, 0x718e, 0x399c,
            0xaaaa, 0xf0f0, 0x5a5a, 0x33cc, 0x3c3c, 0x55aa, 0x9696, 0xa55a, 0x73ce, 0x13c8, 0x324c, 0x3bdc, 0x6996, 0xc33c, 0x9966, 0x0660,
            0x0272, 0x04e4, 0x4e40, 0x2720, 0xc936, 0x936c, 0x39c6, 0x639c, 0x9336, 0x9cc6, 0x817e, 0xe718, 0xccf0, 0x0fcc, 0x7744, 0xee22,
        };

        // three-subset partitions: the subset of each texel, row by row
        static readonly string[] Partitions3 =
        {
            "0011001102212222", "0001001122112221", "0000200122112211", "0222002200110111",
            "0000000011221122", "0011001100220022", "0022002211111111", "0011001122112211",
            "0000000011112222", "0000111111112222", "0000111122222222", "0012001200120012",
            "0112011201120112", "0122012201220122", "0011011211221222", "0011200122002220",
            "0001001101121122", "0111001120012200", "0000112211221122", "0022002200221111",
            "0111011102220222", "0001000122212221", "0000001101220122", "0000110022102210",
            "0122012200110000", "0012001211222222", "0110122112210110", "0000011012211221",
            "0022110211020022", "0110011020022222", "0011012201220011", "0000200022112221",
            "0000000211221222", "0222002200120011", "0011001200220222", "0120012001200120",
            "0000111122220000", "0120120120120120", "0120201212010120", "0011220011220011",
            "0011112222000011", "0101010122222222", "0000000021212121", "0022112200221122",
            "0022001100220011", "0220122102201221", "0101222222220101", "0000212121212121",
            "0101010101012222", "0222011102220111", "0002111200021112", "0000211221122112",
            "0222011101110222", "0002111211120002", "0110011001102222", "0000000021122112",
            "0110011022222222", "0022001100110022", "0022112211220022", "0000000000002112",
            "0002000100020001", "0222122202221222", "0101222222222222", "0111201122012220",
        };

        // the fix-up texel of subset 1 (two subsets), of subsets 1 and 2 (three subsets); subset 0's is texel 0
        static readonly byte[] Anchor2 =
        {
            15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15,
            15, 2, 8, 2, 2, 8, 8, 15, 2, 8, 2, 2, 8, 8, 2, 2,
            15, 15, 6, 8, 2, 8, 15, 15, 2, 8, 2, 2, 2, 15, 15, 6,
            6, 2, 6, 8, 15, 15, 2, 2, 15, 15, 15, 15, 15, 2, 2, 15,
        };

        static readonly byte[] Anchor3a =
        {
            3, 3, 15, 15, 8, 3, 15, 15, 8, 8, 6, 6, 6, 5, 3, 3,
            3, 3, 8, 15, 3, 3, 6, 10, 5, 8, 8, 6, 8, 5, 15, 15,
            8, 15, 3, 5, 6, 10, 8, 15, 15, 3, 15, 5, 15, 15, 15, 15,
            3, 15, 5, 5, 5, 8, 5, 10, 5, 10, 8, 13, 15, 12, 3, 3,
        };

        static readonly byte[] Anchor3b =
        {
            15, 8, 8, 3, 15, 15, 3, 8, 15, 15, 15, 15, 15, 15, 15, 8,
            15, 8, 15, 3, 15, 8, 15, 8, 3, 15, 6, 10, 15, 15, 10, 8,
            15, 3, 15, 10, 10, 8, 9, 10, 6, 15, 8, 15, 3, 6, 6, 8,
            15, 3, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 3, 15, 15, 8,
        };

        // width x height RGBA8 (rows top first) from linear rows of 16-byte blocks; edge blocks are cropped
        public static byte[] DecodeImage(ReadOnlySpan<byte> blocks, int width, int height)
        {
            int bx = (width + 3) / 4, by = (height + 3) / 4;
            if (blocks.Length < (long)bx * by * 16) throw new InvalidDataException($"BC7 image {width}x{height}: {blocks.Length} bytes for {bx * by} blocks");
            var rgba = new byte[(long)width * height * 4];
            Span<byte> tile = stackalloc byte[64];
            for (int j = 0; j < by; j++)
            {
                for (int i = 0; i < bx; i++)
                {
                    DecodeBlock(blocks.Slice((j * bx + i) * 16, 16), tile);
                    int x0 = i * 4, y0 = j * 4;
                    int w = Math.Min(4, width - x0), h = Math.Min(4, height - y0);
                    for (int y = 0; y < h; y++) tile.Slice(y * 16, w * 4).CopyTo(rgba.AsSpan(((y0 + y) * width + x0) * 4, w * 4));
                }
            }
            return rgba;
        }

        ref struct BitReader
        {
            ulong lo, hi;
            int pos;
            public BitReader(ReadOnlySpan<byte> b)
            {
                lo = BinaryPrimitives.ReadUInt64LittleEndian(b);
                hi = BinaryPrimitives.ReadUInt64LittleEndian(b.Slice(8));
                pos = 0;
            }

            public int Read(int n)
            {
                if (n == 0) return 0;
                ulong v = pos >= 64 ? hi >> (pos - 64) : pos == 0 ? lo : (lo >> pos) | (hi << (64 - pos));
                pos += n;
                return (int)(v & ((1UL << n) - 1));
            }
        }

        // the block's 4 x 4 texels as RGBA8, row by row (64 bytes)
        public static void DecodeBlock(ReadOnlySpan<byte> block, Span<byte> rgba)
        {
            int mode = 0;
            while (mode < 8 && (block[0] & (1 << mode)) == 0) mode++;
            if (mode == 8)
            {
                rgba.Slice(0, 64).Clear();
                return;
            }
            var br = new BitReader(block);
            br.Read(mode + 1);
            int subsets = Modes[mode, 0], cb = Modes[mode, 4], ab = Modes[mode, 5];
            int partition = br.Read(Modes[mode, 1]);
            int rotation = br.Read(Modes[mode, 2]);
            int indexSelect = br.Read(Modes[mode, 3]);

            // endpoints: all R, then all G, then all B, then all A
            Span<int> ep = stackalloc int[6 * 4];   // endpoint k (subset k/2), channel c at ep[k * 4 + c]
            int ends = subsets * 2;
            for (int c = 0; c < 3; c++)
            {
                for (int k = 0; k < ends; k++) ep[k * 4 + c] = br.Read(cb);
            }
            for (int k = 0; k < ends; k++) ep[k * 4 + 3] = ab > 0 ? br.Read(ab) : 255;
            int cbits = cb, abits = ab;
            if (Modes[mode, 6] != 0)
            {
                for (int k = 0; k < ends; k++)
                {
                    int p = br.Read(1);
                    for (int c = 0; c < 4; c++) ep[k * 4 + c] = (ep[k * 4 + c] << 1) | p;
                }
                cbits++;
                if (ab > 0) abits++;
            }
            else if (Modes[mode, 7] != 0)
            {
                for (int s = 0; s < subsets; s++)
                {
                    int p = br.Read(1);
                    for (int k = 2 * s; k < 2 * s + 2; k++)
                    {
                        for (int c = 0; c < 3; c++) ep[k * 4 + c] = (ep[k * 4 + c] << 1) | p;
                    }
                }
                cbits++;
            }
            for (int k = 0; k < ends; k++)
            {
                for (int c = 0; c < 3; c++) ep[k * 4 + c] = Expand(ep[k * 4 + c], cbits);
                ep[k * 4 + 3] = ab > 0 ? Expand(ep[k * 4 + 3], abits) : 255;
            }

            // subsets and fix-up texels
            Span<int> subsetOf = stackalloc int[16];
            int anchor1 = -1, anchor2 = -1;
            for (int t = 0; t < 16; t++)
            {
                subsetOf[t] = subsets == 2 ? (Partitions2[partition] >> t) & 1 : subsets == 3 ? Partitions3[partition][t] - '0' : 0;
            }
            if (subsets == 2) anchor1 = Anchor2[partition];
            else if (subsets == 3) { anchor1 = Anchor3a[partition]; anchor2 = Anchor3b[partition]; }

            // indices: the primary set, then (modes 4, 5) the secondary set
            int ib = Modes[mode, 8], ib2 = Modes[mode, 9];
            Span<int> idx = stackalloc int[16];
            Span<int> idx2 = stackalloc int[16];
            for (int t = 0; t < 16; t++)
            {
                bool fix = t == 0 || t == anchor1 || t == anchor2;
                idx[t] = br.Read(fix ? ib - 1 : ib);
            }
            if (ib2 > 0)
            {
                for (int t = 0; t < 16; t++) idx2[t] = br.Read(t == 0 ? ib2 - 1 : ib2);
            }

            for (int t = 0; t < 16; t++)
            {
                int s = subsetOf[t];
                Span<int> e0 = ep.Slice(s * 8, 4), e1 = ep.Slice(s * 8 + 4, 4);
                int ci, cn, ai, an;
                if (ib2 == 0) { ci = ai = idx[t]; cn = an = ib; }
                else if (indexSelect == 0) { ci = idx[t]; cn = ib; ai = idx2[t]; an = ib2; }
                else { ci = idx2[t]; cn = ib2; ai = idx[t]; an = ib; }
                Span<byte> o = rgba.Slice(t * 4, 4);
                for (int c = 0; c < 3; c++) o[c] = Interpolate(e0[c], e1[c], ci, cn);
                o[3] = Interpolate(e0[3], e1[3], ai, an);
                switch (rotation)
                {
                    case 1: (o[0], o[3]) = (o[3], o[0]); break;
                    case 2: (o[1], o[3]) = (o[3], o[1]); break;
                    case 3: (o[2], o[3]) = (o[3], o[2]); break;
                }
            }
        }

        // an n-bit endpoint to 8 bits: shifted up to bit 7, its top bits repeated below
        static int Expand(int v, int n)
        {
            v <<= 8 - n;
            return v | (v >> n);
        }

        static byte Interpolate(int e0, int e1, int index, int bits)
        {
            int w = bits == 2 ? Weights2[index] : bits == 3 ? Weights3[index] : Weights4[index];
            return (byte)(((64 - w) * e0 + w * e1 + 32) >> 6);
        }
    }
}
