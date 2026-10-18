using System;
using System.Collections.Generic;
using System.Linq;

namespace MphRecomp.Frontend
{
    // IMPORT GAMES' card pictograms (owner 2026-10-08: the page has to look right before anything is imported, so its
    // base look can't rest on imported pictures): ours, drawn here, in the multiplayer doors' round-icon style -- a flat
    // orange disc with a darker rim and a black glyph. The glyphs the owner picked: the suits' S (made at run time from
    // the ROM's results-screen gold medal by SuitFromMedal's recipe: its blades poke out of the disc as they poke out of
    // the medal), a morph ball seen at an angle (a seam through it, its round side plate shaded), a skull (characters), a
    // radar (maps), two beamed notes (music); and the cards' emblems: a game disc (its number goes on it in the menu
    // font), three discs (the trilogy), a padlock (not yet); a key (a game page's wii key box); and the import-again badge
    // (owner 2026-10-16: a red disc with a white "!" in a card's upper left corner when an update made some of its imports
    // stale -- not the orange icon style, so it stands out). Shapes are in a 64 x 64
    // grid (y down), painted in order, each over the ones before; `size` texels a side, `samples` x `samples` per texel
    // for smooth edges.
    internal static class ImportCardPictures
    {
        public const string Suit = "suit", Ball = "ball", Creature = "creature", Radar = "radar", Music = "music";
        public const string Disc = "disc", Discs3 = "discs3", Lock = "lock", Key = "key";
        public const string Alert = "alert";

        private static readonly (byte R, byte G, byte B) Orange = (230, 99, 16), Rim = (189, 82, 8), Dark = (206, 90, 8),
            Black = (8, 0, 0), Brown = (74, 33, 0);
        private static readonly (byte R, byte G, byte B) AlertRed = (220, 24, 24), AlertRim = (90, 0, 0), White = (255, 255, 255);

        // ---- the suits' S: made from the player's own ROM (results/medalgold, the results screen's gold medal) ----
        // A recipe, not a picture (owner 2026-10-10, ship-safe audit): ten straight edges of the medal's S, each given
        // only as a coarse search band (ends on a 4-texel grid, +-3.5 texels across). Every edge is found in the
        // texture's own pixels (the strongest brightness step across the band, sampled along its middle 60%), fitted as a
        // line, and the S's corners are where neighbouring edges cross; the two blade tips are the medal's farthest
        // opaque texels up-right and down-left. Without the ROM texture there's no S.
        private static readonly (int X, int Y)[] SuitGuides =
            { (72, 0), (24, 28), (24, 56), (48, 40), (48, 44), (8, 80), (60, 52), (60, 32), (40, 40), (36, 36) };
        private const float SuitBand = 3.5f;

        // medal = the ROM texture's RGBA (rows top down); null when it isn't the medal we know (too small, nothing opaque)
        public static (float X, float Y)[]? SuitFromMedal(int width, int height, byte[] rgba)
        {
            if (width < 80 || height < 82 || rgba.Length < width * height * 4) return null;
            float Lum(int x, int y)
            {
                x = Math.Clamp(x, 0, width - 1); y = Math.Clamp(y, 0, height - 1);
                int o = (y * width + x) * 4;
                return rgba[o + 3] >= 128 ? (rgba[o] + rgba[o + 1] + rgba[o + 2]) / 3f : -60;
            }
            float Sample(float x, float y) // bilinear, texel centres at +0.5
            {
                x -= 0.5f; y -= 0.5f;
                int x0 = (int)MathF.Floor(x), y0 = (int)MathF.Floor(y);
                float fx = x - x0, fy = y - y0;
                return (Lum(x0, y0) * (1 - fx) + Lum(x0 + 1, y0) * fx) * (1 - fy) + (Lum(x0, y0 + 1) * (1 - fx) + Lum(x0 + 1, y0 + 1) * fx) * fy;
            }
            int n = SuitGuides.Length;
            var lines = new (float Px, float Py, float Ux, float Uy)[n];
            for (int i = 0; i < n; i++)
            {
                (int ax, int ay) = SuitGuides[i];
                (int bx, int by) = SuitGuides[(i + 1) % n];
                float dx = bx - ax, dy = by - ay, len = MathF.Sqrt(dx * dx + dy * dy);
                float ux = dx / len, uy = dy / len, nx = -uy, ny = ux;
                int count = Math.Max(3, (int)len);
                float sx = 0, sy = 0;
                var pts = new (float X, float Y)[count];
                for (int k = 0; k < count; k++)
                {
                    float t = 0.2f + 0.6f * k / (count - 1);
                    float cx = ax + dx * t, cy = ay + dy * t;
                    float best = -1, ex = cx, ey = cy;
                    for (float o = -SuitBand; o <= SuitBand + 0.01f; o += 0.25f)
                    {
                        float px = cx + nx * o, py = cy + ny * o;
                        float g = MathF.Abs(Sample(px + nx * 0.5f, py + ny * 0.5f) - Sample(px - nx * 0.5f, py - ny * 0.5f));
                        if (g > best) { best = g; ex = px; ey = py; }
                    }
                    pts[k] = (ex, ey);
                    sx += ex; sy += ey;
                }
                float mx = sx / count, my = sy / count;
                if (len >= 8)
                {
                    // the points' main direction (the larger eigenvector of their spread)
                    float xx = 0, xy = 0, yy = 0;
                    foreach ((float X, float Y) p in pts) { xx += (p.X - mx) * (p.X - mx); xy += (p.X - mx) * (p.Y - my); yy += (p.Y - my) * (p.Y - my); }
                    float angle = 0.5f * MathF.Atan2(2 * xy, xx - yy);
                    ux = MathF.Cos(angle); uy = MathF.Sin(angle);
                }
                lines[i] = (mx, my, ux, uy);
            }
            var s = new (float X, float Y)[n];
            for (int i = 0; i < n; i++)
            {
                var a = lines[(i + n - 1) % n];
                var b = lines[i];
                float det = a.Ux * -b.Uy - -b.Ux * a.Uy;
                if (MathF.Abs(det) < 1e-4f) return null;
                float t = ((b.Px - a.Px) * -b.Uy - -b.Ux * (b.Py - a.Py)) / det;
                s[i] = (a.Px + a.Ux * t, a.Py + a.Uy * t);
            }
            // the blade tips: the farthest opaque texels up-right (x - y) and down-left (y - x), at their outer corners
            (int X, int Y) up = (-1, -1), down = (-1, -1);
            int upBest = int.MinValue, downBest = int.MinValue;
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    if (rgba[(y * width + x) * 4 + 3] < 128) continue;
                    if (x - y > upBest) { upBest = x - y; up = (x, y); }
                    if (y - x > downBest) { downBest = y - x; down = (x, y); }
                }
            }
            if (up.X < 0) return null;
            s[0] = (up.X + 1, up.Y);
            s[5] = (down.X, down.Y + 1);
            return s;
        }

        private delegate bool Shape(float x, float y);

        // off: the game's "not available" look (characterselect's <hunter>off): grey, 42% bright
        // suit: the S from SuitFromMedal (none: the disc alone)
        public static (int Width, int Height, byte[] Rgba) Draw(string name, int size, int samples, bool off = false, (float X, float Y)[]? suit = null)
        {
            List<(Shape In, (byte R, byte G, byte B) Colour)> layers = Layers(name, suit);
            byte[] rgba = new byte[size * size * 4];
            float texel = 64f / size;
            int n = samples * samples;
            for (int ty = 0; ty < size; ty++)
            {
                for (int tx = 0; tx < size; tx++)
                {
                    float r = 0, g = 0, b = 0, a = 0;
                    for (int sy = 0; sy < samples; sy++)
                    {
                        for (int sx = 0; sx < samples; sx++)
                        {
                            float x = (tx + (sx + 0.5f) / samples) * texel, y = (ty + (sy + 0.5f) / samples) * texel;
                            for (int l = layers.Count - 1; l >= 0; l--)
                            {
                                if (!layers[l].In(x, y)) continue;
                                (byte cr, byte cg, byte cb) = layers[l].Colour;
                                r += cr; g += cg; b += cb; a += 1;
                                break;
                            }
                        }
                    }
                    if (a == 0) continue;
                    int o = (ty * size + tx) * 4;
                    // straight alpha: the covered samples' average colour
                    r /= a; g /= a; b /= a;
                    if (off)
                    {
                        float grey = (0.299f * r + 0.587f * g + 0.114f * b) * 0.42f;
                        r = g = b = grey;
                    }
                    rgba[o] = (byte)MathF.Round(r);
                    rgba[o + 1] = (byte)MathF.Round(g);
                    rgba[o + 2] = (byte)MathF.Round(b);
                    rgba[o + 3] = (byte)MathF.Round(a / n * 255);
                }
            }
            return (size, size, rgba);
        }

        private static List<(Shape, (byte, byte, byte))> Layers(string name, (float X, float Y)[]? suit)
        {
            if (name == Alert)
            {
                // the badge: a dark rim so it reads on the orange edges and the renders, a red face, a white "!"
                return new List<(Shape, (byte, byte, byte))>
                {
                    (Ellipse(1, 1, 63, 63), AlertRim),
                    (Ellipse(5, 5, 59, 59), AlertRed),
                    (RoundedRect(27, 11, 37, 40, 4), White),
                    (Ellipse(27, 44, 37, 54), White),
                };
            }
            var l = new List<(Shape, (byte, byte, byte))>
            {
                (Ellipse(1, 1, 63, 63), Rim),
                (Ellipse(4, 4, 60, 60), Orange)
            };
            switch (name)
            {
            case Suit:
            {
                if (suit == null || suit.Length < 3) break;
                // the medal's S fitted into (11, 5)-(53, 59), so its blades poke out of the disc as they poke out of the medal
                float minX = suit.Min(p => p.X), maxX = suit.Max(p => p.X), minY = suit.Min(p => p.Y), maxY = suit.Max(p => p.Y);
                var pts = new (float, float)[suit.Length];
                for (int i = 0; i < pts.Length; i++)
                {
                    pts[i] = (11 + (suit[i].X - minX) * 42 / (maxX - minX), 5 + (suit[i].Y - minY) * 54 / (maxY - minY));
                }
                l.Add((Polygon(pts), Black));
                break;
            }
            case Ball:
                l.Add((Ellipse(12, 12, 52, 52), Black));
                l.Add((Arc(25, 12, 41, 52, 270, 90, 2), Orange)); // the seam, curving over the front
                l.Add((Ellipse(14, 22, 30, 42), Brown)); // the side plate, foreshortened
                l.Add((Ellipse(16, 25, 28, 39), Dark));
                l.Add((Ellipse(18, 28, 26, 36), Rim));
                l.Add((Ellipse(20, 30, 24, 34), Orange));
                l.Add((Arc(14, 22, 30, 42, 120, 240, 1), Black)); // its shadow edge
                break;
            case Creature:
                l.Add((Ellipse(17, 11, 47, 41), Black)); // cranium
                l.Add((RoundedRect(23, 34, 41, 52, 3), Black)); // jaw
                l.Add((Ellipse(21, 23, 30, 33), Orange)); // eye sockets
                l.Add((Ellipse(34, 23, 43, 33), Orange));
                l.Add((Polygon(new (float, float)[] { (32, 34), (35, 39), (29, 39) }), Orange)); // nose
                foreach (float x in new float[] { 27, 32, 37 }) l.Add((Line(x, 44, x, 51, 1), Orange)); // the teeth
                l.Add((Line(23, 44, 41, 44, 1), Orange));
                break;
            case Radar:
                l.Add((Ellipse(10, 10, 54, 54), Black));
                l.Add((Ring(18, 18, 46, 46, 1), Dark));
                l.Add((Line(32, 11, 32, 53, 1), Dark));
                l.Add((Line(11, 32, 53, 32, 1), Dark));
                l.Add((Pie(10, 10, 54, 54, 290, 340), Brown)); // the sweep
                l.Add((Line(32, 32, 47, 18, 2), Orange));
                l.Add((Ellipse(38, 36, 43, 41), Orange)); // blips
                l.Add((Ellipse(21, 22, 25, 26), Orange));
                break;
            case Music:
                l.Add((Polygon(new (float, float)[] { (24, 18), (46, 12), (46, 18), (24, 24) }), Black)); // the beam
                l.Add((Rect(22, 18, 25, 44), Black));
                l.Add((Rect(43, 12, 46, 38), Black));
                l.Add((Ellipse(13, 39, 25, 49), Black));
                l.Add((Ellipse(34, 33, 46, 43), Black));
                break;
            case Disc:
                l.Add((Ellipse(9, 9, 55, 55), Black));
                l.Add((Arc(14, 14, 50, 50, 200, 250, 2), Brown)); // a glint
                break;
            case Discs3:
                foreach (float cx in new float[] { 22, 32, 42 })
                {
                    // each over the last: its orange edge, its black face, its hub
                    l.Add((Ellipse(cx - 13, 19, cx + 13, 45), Orange));
                    l.Add((Ellipse(cx - 12, 20, cx + 12, 44), Black));
                    l.Add((Ellipse(cx - 3, 29, cx + 3, 35), Orange));
                }
                break;
            case Key:
                l.Add((Ellipse(12, 22, 32, 42), Black)); // the bow
                l.Add((Ellipse(18, 28, 26, 36), Orange));
                l.Add((Rect(30, 29, 54, 35), Black)); // the shank and its two teeth
                l.Add((Rect(44, 35, 48, 42), Black));
                l.Add((Rect(50, 35, 54, 40), Black));
                break;
            case Lock:
                l.Add((RoundedRect(19, 30, 45, 52, 3), Black));
                l.Add((Arc(22, 12, 42, 40, 180, 360, 4), Black)); // the shackle
                l.Add((Line(24, 26, 24, 31, 4), Black));
                l.Add((Line(40, 26, 40, 31, 4), Black));
                l.Add((Ellipse(29, 36, 35, 42), Orange)); // the keyhole
                l.Add((Rect(31, 40, 33, 47), Orange));
                break;
            }
            return l;
        }

        // ---- shapes (a box's corners as PIL takes them) ----

        private static Shape Ellipse(float x0, float y0, float x1, float y1)
        {
            float cx = (x0 + x1) / 2, cy = (y0 + y1) / 2, rx = (x1 - x0) / 2, ry = (y1 - y0) / 2;
            return (x, y) => Sq((x - cx) / rx) + Sq((y - cy) / ry) <= 1;
        }

        // an ellipse's outline `w` wide, inside its box
        private static Shape Ring(float x0, float y0, float x1, float y1, float w)
        {
            Shape outer = Ellipse(x0, y0, x1, y1), inner = Ellipse(x0 + w, y0 + w, x1 - w, y1 - w);
            return (x, y) => outer(x, y) && !inner(x, y);
        }

        // degrees clockwise from 3 o'clock (y is down), from `start` round to `end`
        private static bool Within(float x, float y, float cx, float cy, float rx, float ry, float start, float end)
        {
            float a = MathF.Atan2((y - cy) / ry, (x - cx) / rx) * 180 / MathF.PI;
            if (a < 0) a += 360;
            if (end < start) end += 360;
            return a >= start && a <= end || a + 360 >= start && a + 360 <= end;
        }

        private static Shape Arc(float x0, float y0, float x1, float y1, float start, float end, float w)
        {
            Shape ring = Ring(x0, y0, x1, y1, w);
            float cx = (x0 + x1) / 2, cy = (y0 + y1) / 2, rx = (x1 - x0) / 2, ry = (y1 - y0) / 2;
            return (x, y) => ring(x, y) && Within(x, y, cx, cy, rx, ry, start, end);
        }

        private static Shape Pie(float x0, float y0, float x1, float y1, float start, float end)
        {
            Shape e = Ellipse(x0, y0, x1, y1);
            float cx = (x0 + x1) / 2, cy = (y0 + y1) / 2, rx = (x1 - x0) / 2, ry = (y1 - y0) / 2;
            return (x, y) => e(x, y) && Within(x, y, cx, cy, rx, ry, start, end);
        }

        private static Shape Rect(float x0, float y0, float x1, float y1) => (x, y) => x >= x0 && x <= x1 && y >= y0 && y <= y1;

        private static Shape RoundedRect(float x0, float y0, float x1, float y1, float r) => (x, y) =>
        {
            if (x < x0 || x > x1 || y < y0 || y > y1) return false;
            float cx = Math.Clamp(x, x0 + r, x1 - r), cy = Math.Clamp(y, y0 + r, y1 - r);
            return Sq(x - cx) + Sq(y - cy) <= r * r;
        };

        // a straight stroke `w` wide, square ends
        private static Shape Line(float x0, float y0, float x1, float y1, float w)
        {
            float dx = x1 - x0, dy = y1 - y0, len = MathF.Sqrt(dx * dx + dy * dy);
            float ux = dx / len, uy = dy / len;
            return (x, y) =>
            {
                float px = x - x0, py = y - y0, along = px * ux + py * uy, across = MathF.Abs(px * uy - py * ux);
                return along >= 0 && along <= len && across <= w / 2;
            };
        }

        // even-odd fill
        private static Shape Polygon((float X, float Y)[] pts) => (x, y) =>
        {
            bool inside = false;
            for (int i = 0, j = pts.Length - 1; i < pts.Length; j = i++)
            {
                if ((pts[i].Y > y) != (pts[j].Y > y) && x < (pts[j].X - pts[i].X) * (y - pts[i].Y) / (pts[j].Y - pts[i].Y) + pts[i].X) inside = !inside;
            }
            return inside;
        };

        private static float Sq(float v) => v * v;
    }
}
