using System;

namespace MphRecomp.Import.Retro
{
    // A Retro texture (TXTR): format, width, height (u16 each), mip count, then the GX-tiled texels of each mip. Only the
    // top mip is decoded, to RGBA8 rows top first, exactly as prime_tex.py decodes it (formats as PrimeWorldEditor's
    // CTextureDecoder reads them, MIT). The palette formats (C4 / C8 / C14X2) and RGBA8 are not decoded, like the
    // reference: Decode returns null for them.
    public static class Txtr
    {
        public const int I4 = 0, I8 = 1, IA4 = 2, IA8 = 3, C4 = 4, C8 = 5, C14X2 = 6, RGB565 = 7, RGB5A3 = 8, RGBA8 = 9, CMPR = 10;
        public static readonly string[] FormatNames = { "I4", "I8", "IA4", "IA8", "C4", "C8", "C14X2", "RGB565", "RGB5A3", "RGBA8", "CMPR" };

        public static bool CanDecode(uint format) => format is CMPR or RGB5A3 or RGB565 or IA8 or IA4 or I8 or I4;

        public static (uint Format, int Width, int Height) Header(byte[] t) => (Be.U32(t, 0), Be.U16(t, 4), Be.U16(t, 6));

        // RGBA8, width x height, the first row the top one -- or null for a format the reference leaves undecoded
        public static byte[]? Decode(byte[] t, out uint format, out int w, out int h)
        {
            (format, w, h) = Header(t);
            const int d = 12;
            return format switch
            {
                CMPR => Cmpr(t, d, w, h),
                RGB5A3 => Rgb5a3(t, d, w, h),
                RGB565 => Rgb565(t, d, w, h),
                IA8 => Ia8(t, d, w, h),
                IA4 => Ia4(t, d, w, h),
                I8 => Intensity8(t, d, w, h),
                I4 => Intensity4(t, d, w, h),
                _ => null,
            };
        }

        // the rows upside down (the HD models' textures are stored this way; their UVs stay Retro's)
        public static byte[] FlipRows(byte[] rgba, int w, int h)
        {
            var r = new byte[rgba.Length];
            int stride = w * 4;
            for (int y = 0; y < h; y++)
            {
                Buffer.BlockCopy(rgba, y * stride, r, (h - 1 - y) * stride, stride);
            }
            return r;
        }

        static int Pad(int v, int min, int step) => (Math.Max(v, min) + step - 1) / step * step;

        // crop a padded (tile-aligned) image to w x h
        static byte[] Crop(byte[] img, int pw, int w, int h)
        {
            if (pw == w && img.Length == w * h * 4) return img;
            var r = new byte[w * h * 4];
            for (int y = 0; y < h; y++)
            {
                Buffer.BlockCopy(img, y * pw * 4, r, y * w * 4, w * 4);
            }
            return r;
        }

        static void Put(byte[] img, int pw, int x, int y, int r, int g, int b, int a)
        {
            int o = (y * pw + x) * 4;
            img[o] = (byte)r; img[o + 1] = (byte)g; img[o + 2] = (byte)b; img[o + 3] = (byte)a;
        }

        // DXT1 in 8x8 tiles of four 4x4 sub-blocks (big-endian colours, index bits MSB first)
        static byte[] Cmpr(byte[] d, int o, int w, int h)
        {
            int pw = Pad(w, 8, 8), ph = Pad(h, 8, 8);
            var img = new byte[pw * ph * 4];
            Span<int> pal = stackalloc int[16];
            for (int ty = 0; ty < ph; ty += 8)
            {
                for (int tx = 0; tx < pw; tx += 8)
                {
                    for (int sy = 0; sy < 8; sy += 4)
                    {
                        for (int sx = 0; sx < 8; sx += 4)
                        {
                            int c0 = Be.U16(d, o), c1 = Be.U16(d, o + 2);
                            uint bits = Be.U32(d, o + 4);
                            o += 8;
                            int ar = ((c0 >> 11) & 31) * 255 / 31, ag = ((c0 >> 5) & 63) * 255 / 63, ab = (c0 & 31) * 255 / 31;
                            int br = ((c1 >> 11) & 31) * 255 / 31, bg = ((c1 >> 5) & 63) * 255 / 63, bb = (c1 & 31) * 255 / 31;
                            pal[0] = ar; pal[1] = ag; pal[2] = ab; pal[3] = 255;
                            pal[4] = br; pal[5] = bg; pal[6] = bb; pal[7] = 255;
                            if (c0 > c1)
                            {
                                pal[8] = (2 * ar + br) / 3; pal[9] = (2 * ag + bg) / 3; pal[10] = (2 * ab + bb) / 3; pal[11] = 255;
                                pal[12] = (ar + 2 * br) / 3; pal[13] = (ag + 2 * bg) / 3; pal[14] = (ab + 2 * bb) / 3; pal[15] = 255;
                            }
                            else
                            {
                                pal[8] = (ar + br) / 2; pal[9] = (ag + bg) / 2; pal[10] = (ab + bb) / 2; pal[11] = 255;
                                pal[12] = 0; pal[13] = 0; pal[14] = 0; pal[15] = 0;
                            }
                            for (int i = 0; i < 16; i++)
                            {
                                int k = (int)((bits >> (30 - 2 * i)) & 3) * 4;
                                Put(img, pw, tx + sx + (i & 3), ty + sy + (i >> 2), pal[k], pal[k + 1], pal[k + 2], pal[k + 3]);
                            }
                        }
                    }
                }
            }
            return Crop(img, pw, w, h);
        }

        // 4x4 tiles of 16-bit texels: RGB555 (top bit set) or ARGB3444
        static byte[] Rgb5a3(byte[] d, int o, int w, int h)
        {
            int pw = Pad(w, 4, 4), ph = Pad(h, 4, 4);
            var img = new byte[pw * ph * 4];
            for (int ty = 0; ty < ph; ty += 4)
            {
                for (int tx = 0; tx < pw; tx += 4)
                {
                    for (int py = 0; py < 4; py++)
                    {
                        for (int px = 0; px < 4; px++)
                        {
                            int v = Be.U16(d, o); o += 2;
                            if ((v & 0x8000) != 0)
                            {
                                Put(img, pw, tx + px, ty + py, ((v >> 10) & 31) * 255 / 31, ((v >> 5) & 31) * 255 / 31, (v & 31) * 255 / 31, 255);
                            }
                            else
                            {
                                Put(img, pw, tx + px, ty + py, ((v >> 8) & 15) * 17, ((v >> 4) & 15) * 17, (v & 15) * 17, ((v >> 12) & 7) * 255 / 7);
                            }
                        }
                    }
                }
            }
            return Crop(img, pw, w, h);
        }

        // 4x4 tiles of big-endian RGB565
        static byte[] Rgb565(byte[] d, int o, int w, int h)
        {
            int pw = Pad(w, 4, 4), ph = Pad(h, 4, 4);
            var img = new byte[pw * ph * 4];
            for (int ty = 0; ty < ph; ty += 4)
            {
                for (int tx = 0; tx < pw; tx += 4)
                {
                    for (int py = 0; py < 4; py++)
                    {
                        for (int px = 0; px < 4; px++)
                        {
                            int v = Be.U16(d, o); o += 2;
                            Put(img, pw, tx + px, ty + py, ((v >> 11) & 31) * 255 / 31, ((v >> 5) & 63) * 255 / 63, (v & 31) * 255 / 31, 255);
                        }
                    }
                }
            }
            return Crop(img, pw, w, h);
        }

        // 8x4 tiles, one byte per texel: alpha (high nibble), intensity (low nibble)
        static byte[] Ia4(byte[] d, int o, int w, int h)
        {
            int pw = Pad(w, 8, 8), ph = Pad(h, 4, 4);
            var img = new byte[pw * ph * 4];
            for (int ty = 0; ty < ph; ty += 4)
            {
                for (int tx = 0; tx < pw; tx += 8)
                {
                    for (int py = 0; py < 4; py++)
                    {
                        for (int px = 0; px < 8; px++)
                        {
                            int v = d[o++];
                            int i = (v & 15) * 17;
                            Put(img, pw, tx + px, ty + py, i, i, i, (v >> 4) * 17);
                        }
                    }
                }
            }
            return Crop(img, pw, w, h);
        }

        // 4x4 tiles, two bytes per texel: alpha, intensity
        static byte[] Ia8(byte[] d, int o, int w, int h)
        {
            int pw = Pad(w, 4, 4), ph = Pad(h, 4, 4);
            var img = new byte[pw * ph * 4];
            for (int ty = 0; ty < ph; ty += 4)
            {
                for (int tx = 0; tx < pw; tx += 4)
                {
                    for (int py = 0; py < 4; py++)
                    {
                        for (int px = 0; px < 4; px++)
                        {
                            int a = d[o], i = d[o + 1]; o += 2;
                            Put(img, pw, tx + px, ty + py, i, i, i, a);
                        }
                    }
                }
            }
            return Crop(img, pw, w, h);
        }

        // 8x4 tiles of 8-bit intensity (GX intensity = the same value in all four channels)
        static byte[] Intensity8(byte[] d, int o, int w, int h)
        {
            int pw = Pad(w, 8, 8), ph = Pad(h, 4, 4);
            var img = new byte[pw * ph * 4];
            for (int ty = 0; ty < ph; ty += 4)
            {
                for (int tx = 0; tx < pw; tx += 8)
                {
                    for (int py = 0; py < 4; py++)
                    {
                        for (int px = 0; px < 8; px++)
                        {
                            int g = d[o++];
                            Put(img, pw, tx + px, ty + py, g, g, g, g);
                        }
                    }
                }
            }
            return Crop(img, pw, w, h);
        }

        // 8x8 tiles of 4-bit intensity, high nibble first
        static byte[] Intensity4(byte[] d, int o, int w, int h)
        {
            int pw = Pad(w, 8, 8), ph = Pad(h, 8, 8);
            var img = new byte[pw * ph * 4];
            for (int ty = 0; ty < ph; ty += 8)
            {
                for (int tx = 0; tx < pw; tx += 8)
                {
                    for (int k = 0; k < 64; k += 2)
                    {
                        int b = d[o++];
                        int g0 = (b >> 4) * 17, g1 = (b & 15) * 17;
                        Put(img, pw, tx + (k & 7), ty + (k >> 3), g0, g0, g0, g0);
                        Put(img, pw, tx + (k & 7) + 1, ty + (k >> 3), g1, g1, g1, g1);
                    }
                }
            }
            return Crop(img, pw, w, h);
        }
    }
}
