using System;
using System.Collections.Generic;
using System.IO;

// The 2D screens outside the menu file -- the gunship's (_archives/shipGrnd, shipTop, shipSpace) -- are plain DS
// graphics: tiled background layers and OAM sprite sheets, each with its own palette. Decoded straight from the files:
//
// BG layer:  u32 0, u32 tile bytes, u32 palette bytes (512) | 4bpp tiles | 256 RGB555 colours |
//            u16 width, u16 height (in tiles), u16 map bytes, u16 0 | map entries (tile | hflip 0x400 | vflip 0x800 |
//            palette << 12). A 64-tile-wide map is stored as two 32x32 screen blocks, as the DS keeps it.
// Sprite:    u16 frames, u16 images, u16 width, u16 height, i32 param bytes, i32 attr bytes, i32 tile bytes,
//            i32 palette bytes | params | OAM attributes (8 bytes per frame; shape/size give the object size) |
//            4bpp tiles, one object-sized block per image | 256 colours = 16 banks of 16. Colour 0 is transparent.
namespace MphRecomp.Frontend
{
    public sealed class DsImage
    {
        public int Width { get; init; }
        public int Height { get; init; }
        public byte[] Rgba { get; init; } = Array.Empty<byte>();
    }

    public sealed class DsSprite
    {
        public int Width { get; init; }
        public int Height { get; init; }
        public int ImageCount { get; init; }
        public byte[] Tiles { get; init; } = Array.Empty<byte>();
        public ushort[] Palette { get; init; } = Array.Empty<ushort>();
        // the animation: per frame its image and how many menu ticks (30 Hz) it shows (params: byte 0 image, byte 1
        // ticks -- the front end's popup buttons and spinner run at exactly this rate on BizHawk USA rev 1)
        public (int Image, int Ticks)[] Frames { get; init; } = Array.Empty<(int, int)>();

        // one image in one of the 16-colour palette banks
        public DsImage Image(int image, int bank)
        {
            int tw = Width / 8, th = Height / 8, size = Width * Height / 2;
            var rgba = new byte[Width * Height * 4];
            for (int ty = 0; ty < th; ty++)
            {
                for (int tx = 0; tx < tw; tx++)
                {
                    int tile = image * size + (ty * tw + tx) * 32;
                    for (int y = 0; y < 8; y++)
                    {
                        for (int x = 0; x < 8; x++)
                        {
                            byte b = Tiles[tile + y * 4 + x / 2];
                            int ci = (x & 1) != 0 ? b >> 4 : b & 0xF;
                            if (ci != 0)
                            {
                                DsGraphics.Put(rgba, ((ty * 8 + y) * Width + tx * 8 + x) * 4, Palette[bank * 16 + ci]);
                            }
                        }
                    }
                }
            }
            return new DsImage { Width = Width, Height = Height, Rgba = rgba };
        }

        public (byte R, byte G, byte B) Colour(int bank, int index) => DsGraphics.Rgb(Palette[bank * 16 + index]);
    }

    public static class DsGraphics
    {
        public static (byte R, byte G, byte B) Rgb(ushort c) =>
            ((byte)((c & 31) * 255 / 31), (byte)((c >> 5 & 31) * 255 / 31), (byte)((c >> 10 & 31) * 255 / 31));

        internal static void Put(byte[] rgba, int at, ushort colour)
        {
            (byte r, byte g, byte b) = Rgb(colour);
            rgba[at] = r;
            rgba[at + 1] = g;
            rgba[at + 2] = b;
            rgba[at + 3] = 255;
        }

        public static DsImage LoadBg(string path)
        {
            byte[] f = File.ReadAllBytes(path);
            int tileBytes = BitConverter.ToInt32(f, 4), palBytes = BitConverter.ToInt32(f, 8);
            int tiles = 12, pal = tiles + tileBytes, map = pal + palBytes;
            int w = BitConverter.ToUInt16(f, map), h = BitConverter.ToUInt16(f, map + 2);
            map += 8;
            var rgba = new byte[w * 8 * h * 8 * 4];
            for (int ty = 0; ty < h; ty++)
            {
                for (int tx = 0; tx < w; tx++)
                {
                    int index = w == 64 ? tx / 32 * 1024 + ty * 32 + tx % 32 : ty * w + tx;
                    int e = BitConverter.ToUInt16(f, map + index * 2);
                    int tile = tiles + (e & 0x3FF) * 32, bank = e >> 12;
                    bool hf = (e & 0x400) != 0, vf = (e & 0x800) != 0;
                    for (int y = 0; y < 8; y++)
                    {
                        for (int x = 0; x < 8; x++)
                        {
                            int sx = hf ? 7 - x : x, sy = vf ? 7 - y : y;
                            byte b = f[tile + sy * 4 + sx / 2];
                            int ci = (sx & 1) != 0 ? b >> 4 : b & 0xF;
                            if (ci != 0)
                            {
                                Put(rgba, ((ty * 8 + y) * w * 8 + tx * 8 + x) * 4, BitConverter.ToUInt16(f, pal + (bank * 16 + ci) * 2));
                            }
                        }
                    }
                }
            }
            return new DsImage { Width = w * 8, Height = h * 8, Rgba = rgba };
        }

        private static readonly (int W, int H)[,] _objSize =
        {
            { (1, 1), (2, 2), (4, 4), (8, 8) }, // square
            { (2, 1), (4, 1), (4, 2), (8, 4) }, // wide
            { (1, 2), (1, 4), (2, 4), (4, 8) }  // tall
        };

        public static DsSprite LoadSprite(string path)
        {
            byte[] f = File.ReadAllBytes(path);
            int paramBytes = BitConverter.ToInt32(f, 8), attrBytes = BitConverter.ToInt32(f, 12);
            int tileBytes = BitConverter.ToInt32(f, 16), palBytes = BitConverter.ToInt32(f, 20);
            int attrs = 24 + paramBytes, tiles = attrs + attrBytes, pal = tiles + tileBytes;
            int attr0 = BitConverter.ToUInt16(f, attrs), attr1 = BitConverter.ToUInt16(f, attrs + 2);
            (int tw, int th) = _objSize[attr0 >> 14, attr1 >> 14];
            var palette = new ushort[palBytes / 2];
            for (int i = 0; i < palette.Length; i++)
            {
                palette[i] = BitConverter.ToUInt16(f, pal + i * 2);
            }
            int frameCount = BitConverter.ToUInt16(f, 0);
            var frames = new (int Image, int Ticks)[frameCount];
            for (int i = 0; i < frameCount && paramBytes > 0; i++)
            {
                int at = 24 + i * (paramBytes / frameCount);
                frames[i] = (f[at], f[at + 1]);
            }
            return new DsSprite
            {
                Width = tw * 8,
                Height = th * 8,
                ImageCount = tileBytes / (tw * th * 32),
                Tiles = f.AsSpan(tiles, tileBytes).ToArray(),
                Palette = palette,
                Frames = frames
            };
        }
    }
}
