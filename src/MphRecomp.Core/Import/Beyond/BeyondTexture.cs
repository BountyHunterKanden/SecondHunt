using System;
using System.IO;

namespace MphRecomp.Import.Beyond
{
    // A Metroid Prime 4: Beyond texture (TXTR, spec section 6) with its side data: the header (shape, pixel format, size,
    // mip count), the SURFACE the side data assembles (every mip level, block-linear), and the top level decoded to RGBA8.
    //   HEAD       u32 shape (1 = flat 2D), u32 format, u32 width, u32 height, u32 depth, 4 bytes, u32 mip count
    //   side data  5 x u32, u32 surface length Z, u32 NR, NR x (u8, u32 offset in the asset, u32 length), then the two
    //              placements f0..f11 (DecodeSurface)
    // Formats: 12/13 RGBA8, 20-29 BC1-BC5, 53-66 / 67-80 ASTC (linear / sRGB, 14 block sizes), 83/84 BC7; an sRGB format
    // decodes to the same bytes as its linear twin (no colour-space conversion). BC6H (81, 82) is recognised but not
    // decoded. Clean-room: written from docs/cleanroom/G_beyond_spec.md and Microsoft's Direct3D block-compression
    // documentation alone.
    public sealed class BeyondTexture
    {
        public enum Kind { Rgba8, Astc, Bc7, Bc1To5, Bc6h }

        public Guid Id;
        public string What = "";
        public int Shape, Format, Width, Height, Depth, MipCount;
        public byte[] Surface = Array.Empty<byte>();

        public static BeyondTexture Read(BeyondArchive archive, Guid id)
        {
            var t = Parse(archive.ReadAsset(id), archive.ReadSideData(id), $"TXTR {id}");
            t.Id = id;
            return t;
        }

        public static BeyondTexture Parse(byte[] asset, byte[] sideData, string what)
        {
            BeyondForm.Expect(asset, "TXTR", what);
            var t = new BeyondTexture { What = what };
            BeyondChunk head = BeyondForm.Find(BeyondForm.Chunks(asset, what), "HEAD", what);
            var c = new BeyondCursor(asset, head.DataStart, head.End, what + " HEAD");
            t.Shape = checked((int)c.U32());
            t.Format = checked((int)c.U32());
            t.Width = checked((int)c.U32());
            t.Height = checked((int)c.U32());
            t.Depth = checked((int)c.U32());
            c.Skip(4);
            t.MipCount = checked((int)c.U32());
            if (t.Width <= 0 || t.Height <= 0) throw new InvalidDataException($"{what}: size {t.Width}x{t.Height}");
            t.Surface = DecodeSurface(asset, sideData, what);
            return t;
        }

        // (block width, block height, bytes per block, kind) of a pixel format
        public static (int BlockWidth, int BlockHeight, int BlockBytes, Kind Kind) FormatInfo(int format, string what = "TXTR")
        {
            if (format is 12 or 13) return (1, 1, 4, Kind.Rgba8);
            if (format is >= 53 and <= 80)
            {
                int[] s = AstcBlockDecoder.BlockSizes[format >= 67 ? format - 67 : format - 53];
                return (s[0], s[1], 16, Kind.Astc);
            }
            if (format is 83 or 84) return (4, 4, 16, Kind.Bc7);
            if (format is >= 20 and <= 29) return (4, 4, format is 20 or 21 or 26 or 27 ? 8 : 16, Kind.Bc1To5);
            if (format is 81 or 82) return (4, 4, 16, Kind.Bc6h);
            throw new NotSupportedException($"{what}: pixel format {format} is not supported");
        }

        // ---------------------------------------------------------------- 6.2: the surface
        static byte[] DecodeSurface(byte[] asset, byte[] side, string what)
        {
            if (side.Length == 0) throw new InvalidDataException($"{what}: no side data (the surface cannot be assembled)");
            var c = new BeyondCursor(side, 0, side.Length, what + " side data");
            c.Skip(20);
            long z = c.U32();
            int nr = c.Count(9, "read-region");
            var regions = new (long Offset, long Length)[nr];
            for (int i = 0; i < nr; i++)
            {
                c.Skip(1);
                regions[i] = (c.U32(), c.U32());
            }
            var f = new long[12];
            for (int i = 0; i < 12; i++) f[i] = c.U32();
            var surface = new byte[z];

            long Region(long r)
            {
                if (r >= regions.Length) throw new InvalidDataException($"{what}: a placement names read region {r} of {regions.Length}");
                return regions[r].Offset;
            }
            ReadOnlySpan<byte> In(long at, long n)
            {
                if (at < 0 || n < 0 || at + n > asset.Length) throw new InvalidDataException($"{what}: placement read (0x{at:x}, {n} bytes) lies past the asset's end");
                return asset.AsSpan((int)at, (int)n);
            }
            Span<byte> Out(long at, long n)
            {
                if (at < 0 || n < 0 || at + n > surface.Length) throw new InvalidDataException($"{what}: placement write (0x{at:x}, {n} bytes) lies past the {surface.Length}-byte surface");
                return surface.AsSpan((int)at, (int)n);
            }

            // first placement: f1 bytes at region f0, plain when f1 = f3, else packed to f3 bytes; written at f2
            if (f[1] > 0)
            {
                ReadOnlySpan<byte> src = In(Region(f[0]), f[1]);
                if (f[1] == f[3]) src.CopyTo(Out(f[2], f[3]));
                else BeyondUnpack.Unpack(src, Out(f[2], f[3]), $"{what} first placement");
            }
            // second placement: region f5 holds n6 = f6 bytes, for surface offset d = f7; a packed part of c = f10 bytes at
            // p = f11 unpacks to m = f9 bytes at d; the p bytes before it go to d + m, the bytes after it to d + p + m
            if (f[6] > 0)
            {
                long b = Region(f[5]), n6 = f[6], d = f[7], m = f[9], cl = f[10], p = f[11];
                if (p < n6)
                {
                    ReadOnlySpan<byte> src = In(b + p, cl);
                    if (cl == m) src.CopyTo(Out(d, m));
                    else BeyondUnpack.Unpack(src, Out(d, m), $"{what} second placement");
                }
                if (p > 0) In(b, p).CopyTo(Out(d + m, p));
                if (p + cl < n6) In(b + p + cl, n6 - (p + cl)).CopyTo(Out(d + p + m, n6 - (p + cl)));
            }
            return surface;
        }

        // ---------------------------------------------------------------- 6.3-6.5: the top level
        // the top mip level as linear rows of format blocks
        public byte[] TopLevelBlocks()
        {
            if (Shape != 1) throw new NotSupportedException($"{What}: texture shape {Shape} (only 1, flat 2D, is supported)");
            var (bw, bh, bb, _) = FormatInfo(Format, What);
            int w = (Width + bw - 1) / bw, h = (Height + bh - 1) / bh;
            return TegraBlockLinear.Linearize(Surface, 0, w, h, bb, What);
        }

        // the top level as width x height RGBA8, row 0 first, no flip, no colour-space conversion
        public byte[] DecodeRgba()
        {
            var (bw, bh, _, kind) = FormatInfo(Format, What);
            if (kind == Kind.Bc6h) throw new NotSupportedException($"{What}: pixel format {Format} (BC6H) is not decoded");
            byte[] blocks = TopLevelBlocks();
            return kind switch
            {
                Kind.Rgba8 => blocks,
                Kind.Astc => AstcBlockDecoder.DecodeImage(blocks, Width, Height, bw, bh),
                Kind.Bc1To5 => DecodeBcImage(blocks, Format, Width, Height),
                _ => Bc7BlockDecoder.DecodeImage(blocks, Width, Height),
            };
        }

        // ---------------------------------------------------------------- 6.5: BC1-BC5
        // width x height RGBA8 (rows top first) from linear rows of BC1-BC5 blocks (8 bytes for BC1/BC4, else 16); edge
        // blocks are cropped
        static byte[] DecodeBcImage(ReadOnlySpan<byte> blocks, int format, int width, int height)
        {
            int bb = format is 20 or 21 or 26 or 27 ? 8 : 16;
            int bx = (width + 3) / 4, by = (height + 3) / 4;
            if (blocks.Length < (long)bx * by * bb) throw new InvalidDataException($"BC format {format} image {width}x{height}: {blocks.Length} bytes for {bx * by} blocks");
            var rgba = new byte[(long)width * height * 4];
            Span<byte> tile = stackalloc byte[64];
            for (int j = 0; j < by; j++)
            {
                for (int i = 0; i < bx; i++)
                {
                    DecodeBcBlock(blocks.Slice((j * bx + i) * bb, bb), format, tile);
                    int x0 = i * 4, y0 = j * 4;
                    int w = Math.Min(4, width - x0), h = Math.Min(4, height - y0);
                    for (int y = 0; y < h; y++) tile.Slice(y * 16, w * 4).CopyTo(rgba.AsSpan(((y0 + y) * width + x0) * 4, w * 4));
                }
            }
            return rgba;
        }

        // one block's 4 x 4 texels as RGBA8, row by row (64 bytes). BC1 = colour (with 1-bit alpha), BC2 = 4-bit alpha
        // (x 17) + colour, BC3 = BC4-style alpha + colour, BC4 = one channel as grey, BC5 = two channels as R and G
        static void DecodeBcBlock(ReadOnlySpan<byte> b, int format, Span<byte> rgba)
        {
            Span<int> ch = stackalloc int[16];
            switch (format)
            {
                case 20 or 21:
                    BcColour(b, rgba, true);
                    break;
                case 22 or 23:
                    BcColour(b.Slice(8), rgba, false);
                    for (int t = 0; t < 16; t++) rgba[t * 4 + 3] = (byte)(((b[t >> 1] >> ((t & 1) * 4)) & 15) * 17);
                    break;
                case 24 or 25:
                    BcColour(b.Slice(8), rgba, false);
                    BcChannel(b, false, ch);
                    for (int t = 0; t < 16; t++) rgba[t * 4 + 3] = (byte)ch[t];
                    break;
                case 26 or 27:
                    BcChannel(b, format == 27, ch);
                    for (int t = 0; t < 16; t++)
                    {
                        rgba[t * 4] = rgba[t * 4 + 1] = rgba[t * 4 + 2] = (byte)ch[t];
                        rgba[t * 4 + 3] = 255;
                    }
                    break;
                default:
                    BcChannel(b, format == 29, ch);
                    for (int t = 0; t < 16; t++) rgba[t * 4] = (byte)ch[t];
                    BcChannel(b.Slice(8), format == 29, ch);
                    for (int t = 0; t < 16; t++)
                    {
                        rgba[t * 4 + 1] = (byte)ch[t];
                        rgba[t * 4 + 2] = 0;
                        rgba[t * 4 + 3] = 255;
                    }
                    break;
            }
        }

        // a BC1 colour block: two RGB565 endpoints (expanded to 8 bits by repeating the top bits) and 2-bit indices. c0 > c1
        // (as 16-bit numbers) or not allowThree (BC2, BC3): 4 colours (c0, c1, the two thirds); otherwise c0, c1, the half
        // and transparent black. The thirds and the half are taken on the expanded 8-bit values, rounded down (spec 6.5
        // [checked] for the thirds against the BC3 reference images)
        static void BcColour(ReadOnlySpan<byte> b, Span<byte> rgba, bool allowThree)
        {
            int e0 = b[0] | b[1] << 8, e1 = b[2] | b[3] << 8;
            Span<int> pal = stackalloc int[16];
            Expand565(e0, pal.Slice(0, 4));
            Expand565(e1, pal.Slice(4, 4));
            bool four = e0 > e1 || !allowThree;
            for (int k = 0; k < 3; k++)
            {
                int c0 = pal[k], c1 = pal[4 + k];
                if (four)
                {
                    pal[8 + k] = (2 * c0 + c1) / 3;
                    pal[12 + k] = (c0 + 2 * c1) / 3;
                }
                else
                {
                    pal[8 + k] = (c0 + c1) / 2;
                    pal[12 + k] = 0;
                }
            }
            pal[11] = 255;
            pal[15] = four ? 255 : 0;
            uint idx = (uint)(b[4] | b[5] << 8 | b[6] << 16 | b[7] << 24);
            for (int t = 0; t < 16; t++)
            {
                int p = (int)((idx >> (t * 2)) & 3) * 4;
                for (int k = 0; k < 4; k++) rgba[t * 4 + k] = (byte)pal[p + k];
            }
        }

        static void Expand565(int c, Span<int> rgba)
        {
            int r = (c >> 11) & 31, g = (c >> 5) & 63, bl = c & 31;
            rgba[0] = r << 3 | r >> 2;
            rgba[1] = g << 2 | g >> 4;
            rgba[2] = bl << 3 | bl >> 2;
            rgba[3] = 255;
        }

        // a BC4 channel block (also BC3's alpha and each half of BC5): two 8-bit endpoints and 3-bit indices. a0 > a1: 8
        // values (a0, a1, six sevenths); otherwise a0, a1, four fifths, then the minimum and the maximum; the sevenths and
        // fifths are rounded to nearest (spec 6.5 [checked] for BC3's alpha). A signed block holds -127..127 (-128 is read
        // as -127) and outputs round((v + 127) x 255 / 254) [unchecked: no reference texture is signed]
        static void BcChannel(ReadOnlySpan<byte> b, bool signed, Span<int> values)
        {
            int a0 = signed ? Math.Max((int)(sbyte)b[0], -127) : b[0];
            int a1 = signed ? Math.Max((int)(sbyte)b[1], -127) : b[1];
            Span<int> pal = stackalloc int[8];
            pal[0] = a0;
            pal[1] = a1;
            if (a0 > a1)
            {
                for (int i = 1; i <= 6; i++) pal[1 + i] = Lerp(a0, a1, i, 7);
            }
            else
            {
                for (int i = 1; i <= 4; i++) pal[1 + i] = Lerp(a0, a1, i, 5);
                pal[6] = signed ? -127 : 0;
                pal[7] = signed ? 127 : 255;
            }
            ulong idx = 0;
            for (int i = 0; i < 6; i++) idx |= (ulong)b[2 + i] << (i * 8);
            for (int t = 0; t < 16; t++)
            {
                int v = pal[(int)((idx >> (t * 3)) & 7)];
                values[t] = signed ? ((v + 127) * 255 + 127) / 254 : v;
            }
        }

        // ((n - i) x a + i x b) / n rounded to nearest (a negative sum rounds its half away from zero)
        static int Lerp(int a, int b, int i, int n)
        {
            int s = (n - i) * a + i * b;
            return s >= 0 ? (s + n / 2) / n : -((-s + n / 2) / n);
        }
    }
}
