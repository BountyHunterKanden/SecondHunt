using System;
using System.IO;
using MphRecomp.Import.Retro;

namespace MphRecomp.Import.Brawl
{
    // Wii tiled pixel formats, by the format numbers Brawl's texture headers use. Decodes the base image to 8-bit RGBA,
    // the top row first. I4 / I8 / IA4 / IA8 / RGBA8 are decoded here; RGB565, RGB5A3 and CMPR go through the project's
    // Retro decoder (Txtr, whose numbers differ). The colour-table formats (C4 / C8 / C14X2) and unknown numbers throw.
    // Clean-room from docs/cleanroom/E_brawl_effects_spec.md (section 4).
    public static class BrawlPixels
    {
        public const int I4 = 0, I8 = 1, IA4 = 2, IA8 = 3, RGB565 = 4, RGB5A3 = 5, RGBA8 = 6, C4 = 8, C8 = 9, C14X2 = 10, CMPR = 14;

        public static bool CanDecode(int format) => format is I4 or I8 or IA4 or IA8 or RGB565 or RGB5A3 or RGBA8 or CMPR;

        // an intensity-only texture: the hardware also gives the intensity as its alpha (the decoded alpha stays 255)
        public static bool IntensityOnly(int format) => format is I4 or I8;

        public static string FormatName(int format) => format switch
        {
            I4 => "I4", I8 => "I8", IA4 => "IA4", IA8 => "IA8", RGB565 => "RGB565", RGB5A3 => "RGB5A3", RGBA8 => "RGBA8",
            C4 => "C4", C8 => "C8", C14X2 => "C14X2", CMPR => "CMPR", _ => $"format {format}",
        };

        // tile width, tile height, bits per pixel
        static (int Tw, int Th, int Bits) Tile(int format, string part) => format switch
        {
            I4 => (8, 8, 4),
            I8 => (8, 4, 8),
            IA4 => (8, 4, 8),
            IA8 => (4, 4, 16),
            RGB565 => (4, 4, 16),
            RGB5A3 => (4, 4, 16),
            RGBA8 => (4, 4, 32),
            CMPR => (8, 8, 4),
            C4 or C8 or C14X2 => throw new InvalidDataException($"{part}: {FormatName(format)} uses a colour table, not supported"),
            _ => throw new InvalidDataException($"{part}: unknown pixel format {format}"),
        };

        // bytes the base image takes, padded up to whole tiles
        public static long ByteSize(int format, int width, int height, string part = "texture")
        {
            var (tw, th, bits) = Tile(format, part);
            return BrawlBytes.RoundUp(width, tw) * BrawlBytes.RoundUp(height, th) * bits / 8;
        }

        // the base image as RGBA8 rows, top first; `pixels` must hold at least ByteSize bytes
        public static byte[] Decode(ReadOnlySpan<byte> pixels, int format, int width, int height, string part = "texture")
        {
            if (width <= 0 || height <= 0)
            {
                throw new InvalidDataException($"{part}: size {width} x {height}");
            }
            if ((long)width * height * 4 > Array.MaxLength)
            {
                throw new InvalidDataException($"{part}: size {width} x {height} too large");
            }
            var (tw, th, _) = Tile(format, part);
            long need = ByteSize(format, width, height, part);
            BrawlBytes.Need(pixels, 0, need, part + " pixels");
            var src = pixels.Slice(0, (int)need);
            return format switch
            {
                RGB565 => ViaTxtr(src, Txtr.RGB565, width, height, part),
                RGB5A3 => ViaTxtr(src, Txtr.RGB5A3, width, height, part),
                CMPR => ViaTxtr(src, Txtr.CMPR, width, height, part),
                RGBA8 => Rgba8(src, width, height),
                _ => Small(src, format, tw, th, width, height),
            };
        }

        // I4 / I8 / IA4 / IA8: pixel p of the padded image is the p-th stored, tile by tile
        static byte[] Small(ReadOnlySpan<byte> s, int format, int tw, int th, int w, int h)
        {
            var img = new byte[w * h * 4];
            int pw = (int)BrawlBytes.RoundUp(w, tw), ph = (int)BrawlBytes.RoundUp(h, th);
            int tilesX = pw / tw, perTile = tw * th;
            long total = (long)pw * ph;
            for (long p = 0; p < total; p++)
            {
                long tile = p / perTile;
                int within = (int)(p % perTile);
                int x = (int)(tile % tilesX) * tw + within % tw;
                int y = (int)(tile / tilesX) * th + within / tw;
                if (x >= w || y >= h) continue;
                int i, a;
                switch (format)
                {
                    case I4:
                        {
                            int b = s[(int)(p >> 1)];
                            i = ((p & 1) == 0 ? b >> 4 : b & 15) * 17;
                            a = 255;
                            break;
                        }
                    case I8:
                        i = s[(int)p];
                        a = 255;
                        break;
                    case IA4:
                        {
                            int b = s[(int)p];
                            a = (b >> 4) * 17;
                            i = (b & 15) * 17;
                            break;
                        }
                    default: // IA8
                        a = s[(int)(2 * p)];
                        i = s[(int)(2 * p + 1)];
                        break;
                }
                int o = (y * w + x) * 4;
                img[o] = (byte)i; img[o + 1] = (byte)i; img[o + 2] = (byte)i; img[o + 3] = (byte)a;
            }
            return img;
        }

        // RGBA8: a 4x4 tile is 64 bytes, 16 (alpha, red) pairs then 16 (green, blue) pairs, both in pixel order
        static byte[] Rgba8(ReadOnlySpan<byte> s, int w, int h)
        {
            var img = new byte[w * h * 4];
            int tilesX = (int)BrawlBytes.RoundUp(w, 4) / 4, tilesY = (int)BrawlBytes.RoundUp(h, 4) / 4;
            for (int ty = 0; ty < tilesY; ty++)
            {
                for (int tx = 0; tx < tilesX; tx++)
                {
                    int t = ((ty * tilesX) + tx) * 64;
                    for (int k = 0; k < 16; k++)
                    {
                        int x = tx * 4 + (k & 3), y = ty * 4 + (k >> 2);
                        if (x >= w || y >= h) continue;
                        int o = (y * w + x) * 4;
                        img[o] = s[t + 2 * k + 1];
                        img[o + 1] = s[t + 32 + 2 * k];
                        img[o + 2] = s[t + 32 + 2 * k + 1];
                        img[o + 3] = s[t + 2 * k];
                    }
                }
            }
            return img;
        }

        // the Retro decoder reads a TXTR: u32 format, u16 width, u16 height, u32 image count, then the pixels
        static byte[] ViaTxtr(ReadOnlySpan<byte> s, int txtrFormat, int w, int h, string part)
        {
            var t = new byte[12 + s.Length];
            t[0] = (byte)(txtrFormat >> 24); t[1] = (byte)(txtrFormat >> 16); t[2] = (byte)(txtrFormat >> 8); t[3] = (byte)txtrFormat;
            t[4] = (byte)(w >> 8); t[5] = (byte)w; t[6] = (byte)(h >> 8); t[7] = (byte)h;
            t[11] = 1;
            s.CopyTo(t.AsSpan(12));
            return Txtr.Decode(t, out _, out _, out _) ?? throw new InvalidDataException($"{part}: the Retro decoder left format {txtrFormat} undecoded");
        }
    }
}
