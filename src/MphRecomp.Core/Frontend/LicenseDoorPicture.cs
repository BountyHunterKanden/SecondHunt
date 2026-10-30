using System;
using System.Collections.Generic;

namespace MphRecomp.Frontend
{
    // The HUNTER LICENSE door's picture (owner 2026-10-08, candidate G3 of the mockups):
    // ours, drawn here, not taken from the ROM. Three
    // stars -- the big one in front, two behind it lower and to the sides, touching it -- each point split into a lit half
    // and a shaded half, inside a ring with the middle see-through. That's the style of page 52's FRIENDS AND RIVALS button
    // (wifi_createjoin/wififriend), whose ring, size and place in a 128x128 texture are also the single-card door's, so that
    // door's own triangles and UVs draw this texture unchanged (MultiplayerMenu.UseLicenseDoorPicture). The edges are
    // smoothed: 8x8 samples per texel.
    internal static class LicenseDoorPicture
    {
        public const string Key = "hunter license door";
        public const int Size = 128;
        private const int Samples = 8;
        private const float CX = 36.5f, CY = 37.5f, RingOuter = 35.5f, RingInner = 31.5f;
        // the FRIENDS AND RIVALS button's orange (its ring and figures) and its highlight, and two darker shades of it
        private static readonly (byte R, byte G, byte B) Orange = (230, 99, 8), Light = (247, 148, 0), Rim = (189, 82, 16),
            Darker = (132, 57, 8);

        private readonly struct Tri
        {
            public readonly float Ax, Ay, Bx, By, Cx, Cy;
            public readonly (byte R, byte G, byte B) Color;

            public Tri((float X, float Y) a, (float X, float Y) b, (float X, float Y) c, (byte, byte, byte) color)
            {
                (Ax, Ay, Bx, By, Cx, Cy) = (a.X, a.Y, b.X, b.Y, c.X, c.Y);
                Color = color;
            }

            // inside or on an edge (the facets share edges: no sample falls between two of them)
            public bool Contains(float x, float y)
            {
                float d1 = (x - Bx) * (Ay - By) - (Ax - Bx) * (y - By);
                float d2 = (x - Cx) * (By - Cy) - (Bx - Cx) * (y - Cy);
                float d3 = (x - Ax) * (Cy - Ay) - (Cx - Ax) * (y - Ay);
                bool neg = d1 < 0 || d2 < 0 || d3 < 0, pos = d1 > 0 || d2 > 0 || d3 > 0;
                return !(neg && pos);
            }
        }

        // a five-pointed star, point up, as ten facets: each point's left half lit, its right half shaded
        private static void Star(List<Tri> tris, float cx, float cy, float r, (byte, byte, byte) lit, (byte, byte, byte) shade)
        {
            var p = new (float X, float Y)[10];
            for (int i = 0; i < 10; i++)
            {
                float a = -MathF.PI / 2 + i * MathF.PI / 5, rr = i % 2 == 0 ? r : r * 0.42f;
                p[i] = (cx + rr * MathF.Cos(a), cy + rr * MathF.Sin(a));
            }
            for (int i = 0; i < 10; i += 2)
            {
                tris.Add(new Tri((cx, cy), p[(i + 9) % 10], p[i], lit));
                tris.Add(new Tri((cx, cy), p[i], p[i + 1], shade));
            }
        }

        public static (int Width, int Height, byte[] Rgba) Draw()
        {
            var tris = new List<Tri>(); // back to front
            Star(tris, CX - 15, CY + 7, 14.5f, Rim, Darker);
            Star(tris, CX + 15, CY + 7, 14.5f, Rim, Darker);
            Star(tris, CX, CY - 2, 21.5f, Light, Orange);
            byte[] rgba = new byte[Size * Size * 4];
            for (int y = 0; y < Size; y++)
            {
                for (int x = 0; x < Size; x++)
                {
                    float dx = x + 0.5f - CX, dy = y + 0.5f - CY;
                    if (dx * dx + dy * dy > (RingOuter + 1) * (RingOuter + 1)) continue;
                    int r = 0, g = 0, b = 0, hits = 0;
                    for (int sy = 0; sy < Samples; sy++)
                    {
                        for (int sx = 0; sx < Samples; sx++)
                        {
                            float px = x + (sx + 0.5f) / Samples, py = y + (sy + 0.5f) / Samples;
                            (byte R, byte G, byte B)? c = null;
                            for (int t = tris.Count - 1; t >= 0; t--)
                            {
                                if (tris[t].Contains(px, py))
                                {
                                    c = tris[t].Color;
                                    break;
                                }
                            }
                            if (c == null)
                            {
                                float d2 = (px - CX) * (px - CX) + (py - CY) * (py - CY);
                                if (d2 <= RingOuter * RingOuter && d2 >= RingInner * RingInner) c = Orange;
                            }
                            if (c is (byte, byte, byte) col)
                            {
                                r += col.R; g += col.G; b += col.B;
                                hits++;
                            }
                        }
                    }
                    if (hits == 0) continue;
                    int o = (y * Size + x) * 4;
                    rgba[o] = (byte)(r / hits);
                    rgba[o + 1] = (byte)(g / hits);
                    rgba[o + 2] = (byte)(b / hits);
                    rgba[o + 3] = (byte)(hits * 255 / (Samples * Samples));
                }
            }
            return (Size, Size, rgba);
        }
    }
}
