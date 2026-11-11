using System;
using System.Collections.Generic;
using MphRead;
using MphRead.Formats;
using MphRead.Hud;

namespace MphRecomp.Frontend
{
    // a custom character's HUD portrait (the target health bar's and scoreboard's picture: Dark Samus, the Zero Suit, the
    // Trooper, owner 2026-11-05), so they don't show their base hunter's face: the hunter's ROM portrait (enemy_<hunter>,
    // 32 x 32) with its frame (palette index 1) kept and its inside (index 2 up) filled with the character's head picture
    // (CharacterPictures, the top square the menus' icons use) over black, Scale x the ROM size so the face stays sharp
    public static class HudPortraits
    {
        public const int Scale = 4;
        public const float Zoom = 0.78f;

        // the 8x8-tiled character data of frame 0 as one palette index per pixel, row by row
        public static byte[] Linear(IReadOnlyList<byte> data, int width, int height)
        {
            var result = new byte[width * height];
            int tilesX = width / 8;
            for (int ty = 0; ty < height / 8; ty++)
            {
                for (int tx = 0; tx < tilesX; tx++)
                {
                    for (int py = 0; py < 8; py++)
                    {
                        for (int px = 0; px < 8; px++)
                        {
                            result[(ty * 8 + py) * width + tx * 8 + px] = data[ty * tilesX * 64 + tx * 64 + py * 8 + px];
                        }
                    }
                }
            }
            return result;
        }

        // head: the head picture's RGBA (CharacterPictures.HeadW x HeadH, any size works)
        public static Scene.HostHudSprite Compose(Hunter hunter, byte[] head, int headW, int headH)
        {
            HudObject obj = HudInfo.GetHudObject(HudElements.Hunters[(int)hunter]);
            int w = obj.Width, h = obj.Height;
            byte[] idx = Linear(obj.CharacterData, w, h);
            // the inside's bounds (the face square is fitted to them)
            int x0 = w, y0 = h, x1 = -1, y1 = -1;
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    if (idx[y * w + x] >= 2)
                    {
                        x0 = Math.Min(x0, x); y0 = Math.Min(y0, y); x1 = Math.Max(x1, x); y1 = Math.Max(y1, y);
                    }
                }
            }
            int bw = (x1 - x0 + 1) * Scale, bh = (y1 - y0 + 1) * Scale;
            // the square's middle Zoom of it (the head picture's square is the head and shoulders; MPH's portraits are the face)
            float full = Math.Min(headW, headH), side = full * Zoom;
            float top = headH * CharacterPictures.HeadCropTop + (full - side) / 2, left = (headW - side) / 2;
            float step = side / Math.Max(bw, bh); // source pixels per portrait pixel
            int ow = w * Scale, oh = h * Scale;
            var pixels = new ColorRgba[ow * oh];
            for (int y = 0; y < oh; y++)
            {
                for (int x = 0; x < ow; x++)
                {
                    byte i = idx[y / Scale * w + x / Scale];
                    if (i == 0)
                    {
                        continue;
                    }
                    if (i == 1)
                    {
                        pixels[y * ow + x] = obj.PaletteData[1];
                        continue;
                    }
                    // the source box this pixel covers, averaged (alpha-weighted), over black
                    float sx = left + (x - x0 * Scale) * step, sy = top + (y - y0 * Scale) * step;
                    float r = 0, g = 0, b = 0, n = 0;
                    int ax = (int)sx, ay = (int)sy, bx = Math.Max(ax + 1, (int)(sx + step)), by = Math.Max(ay + 1, (int)(sy + step));
                    for (int py = ay; py < by; py++)
                    {
                        for (int px = ax; px < bx; px++)
                        {
                            int cx = Math.Clamp(px, 0, headW - 1), cy = Math.Clamp(py, 0, headH - 1);
                            int o = (cy * headW + cx) * 4;
                            float a = head[o + 3] / 255f;
                            r += head[o] * a; g += head[o + 1] * a; b += head[o + 2] * a; n++;
                        }
                    }
                    pixels[y * ow + x] = new ColorRgba((byte)(r / n), (byte)(g / n), (byte)(b / n), 255);
                }
            }
            return new Scene.HostHudSprite { Width = ow, Height = oh, Pixels = pixels };
        }
    }
}
