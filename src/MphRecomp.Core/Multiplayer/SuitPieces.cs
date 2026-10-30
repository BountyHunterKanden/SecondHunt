using System;
using System.Collections.Generic;

namespace MphRecomp.Multiplayer
{
    // A suit recoloured piece by piece on the CPU (Beta Phazon): its seams found from the glow layer drawn over them, its
    // plates split into connected pieces that each take one colour group, and every seam texel unmixed into the plate
    // under it plus the seam's own cyan, each recoloured and added back. The seams keep vanilla's exact shape because
    // nothing is re-drawn: the texel's own mix of plate and line is kept, only the two colours change.
    // (the hue test the class maps use can't do it here: Beta's teal-tinted ribs share the seams' hue, and the seams'
    // soft edges pass through the second colour's, owner 2026-10-29 "isolate each piece, use a colorizer")
    public static class SuitPieces
    {
        // Pairs: a paint texture and the glow layer whose lines sit on its seams (same size); Whole: paint textures with
        // no seams whose blue-to-teal plates are all the helmet/chest group; Chest: small textures whose coloured texels
        // are all that group; Glow: the line layers, drawn in the lights colour at their own brightness
        public sealed record Spec((string Paint, string Glow)[] Pairs, string[] Whole, string[] Chest, string[] Glow);

        // per texel: K 0 kept as is, 1 a plate, 2 a seam, 3 a glow layer texel, 4 a chest-group texel; Z its plate's group
        // (0 main, 1 second, 2 lights, 3 chest); P the plate colour under it (a seam: its nearest plate texel); A, B how
        // much of the plate is left and how much seam cyan is added
        public sealed class Split
        {
            public int W, H;
            public int[] Src = Array.Empty<int>();
            public byte[] K = Array.Empty<byte>();
            public sbyte[] Z = Array.Empty<sbyte>();
            public int[] P = Array.Empty<int>();
            public float[] A = Array.Empty<float>(), B = Array.Empty<float>();
        }

        // vanilla's seam core (24,190,180)
        const float S0r = 24 / 255f, S0g = 190 / 255f, S0b = 180 / 255f;

        static string Name(string png) => System.IO.Path.GetFileName(png.Replace('\\', '/'));
        static bool Is(string name, string prefix) => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);

        // the glow layer a paint texture pairs with (null: none, or not in the spec)
        public static string? GlowOf(Spec spec, string png)
        {
            string n = Name(png);
            foreach (var p in spec.Pairs) if (Is(n, p.Paint)) return p.Glow;
            return null;
        }

        public static bool Covers(Spec spec, string png)
        {
            string n = Name(png);
            foreach (var p in spec.Pairs) if (Is(n, p.Paint)) return true;
            return Array.Exists(spec.Whole, g => Is(n, g)) || Array.Exists(spec.Chest, g => Is(n, g)) || Array.Exists(spec.Glow, g => Is(n, g));
        }

        static void Rgb(int p, out float r, out float g, out float b) { r = (p & 0xFF) / 255f; g = ((p >> 8) & 0xFF) / 255f; b = ((p >> 16) & 0xFF) / 255f; }

        static float Hue(float r, float g, float b, out float s, out float v)
        {
            v = MathF.Max(r, MathF.Max(g, b));
            float d = v - MathF.Min(r, MathF.Min(g, b));
            s = v > 0 ? d / v : 0;
            if (d <= 0) return 0;
            float h = v == r ? ((g - b) / d) % 6 : v == g ? (b - r) / d + 2 : (r - g) / d + 4;
            h *= 60;
            return h < 0 ? h + 360 : h;
        }

        static float Hd(float a, float b) { float d = MathF.Abs(a - b) % 360; return MathF.Min(d, 360 - d); }

        // px: RGBA as GL takes it (R the low byte); glow: the paired glow layer's pixels (same size) or null
        public static Split? Make(Spec spec, string png, int[] px, int w, int h, int[]? glow, int gw, int gh)
        {
            string n = Name(png);
            int len = w * h;
            var s = new Split { W = w, H = h, Src = px, K = new byte[len], Z = new sbyte[len], P = new int[len], A = new float[len], B = new float[len] };
            if (Array.Exists(spec.Glow, g => Is(n, g))) { Array.Fill(s.K, (byte)3); return s; }
            if (Array.Exists(spec.Chest, g => Is(n, g)))
            {
                for (int i = 0; i < len; i++)
                {
                    Rgb(px[i], out float r, out float g, out float b);
                    Hue(r, g, b, out float sat, out _);
                    if (sat > 0.2f) { s.K[i] = 4; s.Z[i] = 3; s.P[i] = px[i]; }
                }
                return s;
            }
            bool whole = Array.Exists(spec.Whole, g => Is(n, g));
            if (!whole && GlowOf(spec, png) == null) return null;
            if (glow != null && (gw != w || gh != h)) glow = null;

            // the seam zone: texels within 3.5 of a glow line, not black and not grey
            var dark = new bool[len];
            var z = new sbyte[len];
            var seam = new bool[len];
            for (int i = 0; i < len; i++)
            {
                Rgb(px[i], out float r, out float g, out float b);
                float hue = Hue(r, g, b, out float sat, out float v);
                bool dk = v < 0.04f, grey = sat < 0.22f && !dk;
                sbyte k = -1;
                if (hue > 212 && hue < 250) k = 0; else if (hue > 185 && hue <= 212) k = 1; else if (hue > 140 && hue <= 185) k = 3;
                if (whole && hue > 140 && hue < 250) k = 3;
                if (Hd(hue, 25) < 30 && sat > 0.3f) k = 2;
                if (grey || dk) k = -1;
                z[i] = k;
                dark[i] = dk || grey;
            }
            if (glow != null)
            {
                for (int y = 0; y < h; y++)
                    for (int x = 0; x < w; x++)
                    {
                        Rgb(glow[y * w + x], out float r, out float g, out float b);
                        if (MathF.Max(r, MathF.Max(g, b)) <= 0.12f) continue;
                        for (int dy = -3; dy <= 3; dy++)
                            for (int dx = -3; dx <= 3; dx++)
                            {
                                int xx = x + dx, yy = y + dy;
                                if (dx * dx + dy * dy > 12.25f || xx < 0 || yy < 0 || xx >= w || yy >= h) continue;
                                int j = yy * w + xx;
                                if (!dark[j]) seam[j] = true;
                            }
                    }
            }

            // the plates: connected pieces (4-neighbour), each one group by majority
            var plate = new bool[len];
            for (int i = 0; i < len; i++) plate[i] = z[i] >= 0 && !seam[i];
            var lab = new int[len];
            var stack = new Stack<int>();
            var piece = new List<int>();
            Span<int> votes = stackalloc int[4];
            int next = 0;
            for (int i0 = 0; i0 < len; i0++)
            {
                if (!plate[i0] || lab[i0] != 0) continue;
                lab[i0] = ++next;
                stack.Push(i0);
                piece.Clear();
                votes.Clear();
                while (stack.Count > 0)
                {
                    int i = stack.Pop();
                    piece.Add(i);
                    votes[z[i]]++;
                    int x = i % w, y = i / w;
                    if (x > 0 && plate[i - 1] && lab[i - 1] == 0) { lab[i - 1] = next; stack.Push(i - 1); }
                    if (x < w - 1 && plate[i + 1] && lab[i + 1] == 0) { lab[i + 1] = next; stack.Push(i + 1); }
                    if (y > 0 && plate[i - w] && lab[i - w] == 0) { lab[i - w] = next; stack.Push(i - w); }
                    if (y < h - 1 && plate[i + w] && lab[i + w] == 0) { lab[i + w] = next; stack.Push(i + w); }
                }
                sbyte best = 0;
                for (sbyte k = 1; k < 4; k++) if (votes[k] > votes[best]) best = k;
                foreach (int i in piece) { s.K[i] = 1; s.Z[i] = best; s.P[i] = px[i]; }
            }

            // the seams: the nearest plate texel's colour and group, and the texel unmixed as (1-a) plate + b cyan
            for (int i = 0; i < len; i++)
            {
                if (!seam[i] || plate[i]) continue;
                int x = i % w, y = i / w, bestJ = -1, bestD = int.MaxValue;
                for (int rad = 1; rad <= 64 && bestJ < 0; rad++)
                {
                    // the ring at Chebyshev radius rad, then one more ring: a nearer Euclidean texel can sit one ring out
                    for (int rr = rad; rr <= Math.Min(rad * 2, 64); rr++)
                    {
                        for (int dy = -rr; dy <= rr; dy++)
                            for (int dx = -rr; dx <= rr; dx++)
                            {
                                if (Math.Max(Math.Abs(dx), Math.Abs(dy)) != rr) continue;
                                int xx = x + dx, yy = y + dy;
                                if (xx < 0 || yy < 0 || xx >= w || yy >= h || !plate[yy * w + xx]) continue;
                                int d = dx * dx + dy * dy;
                                if (d < bestD) { bestD = d; bestJ = yy * w + xx; }
                            }
                        if (bestJ >= 0 && rr * rr >= bestD) break;
                    }
                }
                if (bestJ < 0) continue;
                Rgb(px[i], out float cr, out float cg, out float cb);
                Rgb(px[bestJ], out float pr, out float pg, out float pb);
                // least squares for c - p = a(-p) + b S0
                float m00 = pr * pr + pg * pg + pb * pb + 1e-6f, m01 = -(pr * S0r + pg * S0g + pb * S0b), m11 = S0r * S0r + S0g * S0g + S0b * S0b + 1e-6f;
                float rr0 = cr - pr, rr1 = cg - pg, rr2 = cb - pb;
                float t0 = -(pr * rr0 + pg * rr1 + pb * rr2), t1 = S0r * rr0 + S0g * rr1 + S0b * rr2;
                float det = m00 * m11 - m01 * m01;
                float a = (t0 * m11 - m01 * t1) / det, b = (m00 * t1 - m01 * t0) / det;
                s.K[i] = 2; s.Z[i] = s.Z[bestJ]; s.P[i] = px[bestJ];
                s.A[i] = Math.Clamp(a, 0, 1); s.B[i] = MathF.Max(b, 0);
            }
            return s;
        }

        static float Lum(float r, float g, float b) => 0.299f * r + 0.587f * g + 0.114f * b;

        // the lights colour at luminance L, pushed toward white past full so bright lines stay bright
        static void GlowCol(float L, Vector3f lig, out float r, out float g, out float b)
        {
            float ll = MathF.Max(Lum(lig.X, lig.Y, lig.Z), 1e-3f);
            float qr = lig.X / ll, qg = lig.Y / ll, qb = lig.Z / ll, qm = MathF.Max(qr, MathF.Max(qg, qb));
            r = L * qr; g = L * qg; b = L * qb;
            float ex = MathF.Max(MathF.Max(r, MathF.Max(g, b)) - 1, 0), t = Math.Clamp(ex / qm, 0, 1) * 0.5f;
            r = Math.Clamp(r, 0, 1); g = Math.Clamp(g, 0, 1); b = Math.Clamp(b, 0, 1);
            r += (1 - r) * t; g += (1 - g) * t; b += (1 - b) * t;
        }

        public readonly record struct Vector3f(float X, float Y, float Z);

        // the plate colour p in group z: a colour group at the plate's brightness against the group's reference (the
        // shader's lit paint, 35% floor), lights as a glow
        static void Paint(int p, int z, Vector3f[] col, float[] refs, out float r, out float g, out float b)
        {
            Rgb(p, out float pr, out float pg, out float pb);
            if (z == 2) { GlowCol(Lum(pr, pg, pb), col[2], out r, out g, out b); return; }
            float v = MathF.Max(pr, MathF.Max(pg, pb)), f = 0.35f + 0.65f * v / MathF.Max(refs[z], 0.01f);
            r = Math.Clamp(col[z].X * f, 0, 1); g = Math.Clamp(col[z].Y * f, 0, 1); b = Math.Clamp(col[z].Z * f, 0, 1);
        }

        // the texture in a seat's colours: col main, second, lights, chest; refs those groups' reference brightness (lights
        // unused); strength 0..1 mixes from the vanilla texel
        public static int[] Compose(Split s, Vector3f[] col, float[] refs, float strength)
        {
            int len = s.W * s.H;
            var o = new int[len];
            for (int i = 0; i < len; i++)
            {
                int src = s.Src[i];
                float r, g, b;
                switch (s.K[i])
                {
                    case 1: case 4: Paint(s.P[i], s.Z[i], col, refs, out r, out g, out b); break;
                    case 2:
                    {
                        Paint(s.P[i], s.Z[i], col, refs, out float pr, out float pg, out float pb);
                        GlowCol(Lum(S0r, S0g, S0b), col[2], out float sr, out float sg, out float sb);
                        float a = s.A[i], bb = s.B[i];
                        r = Math.Clamp((1 - a) * pr + bb * sr, 0, 1); g = Math.Clamp((1 - a) * pg + bb * sg, 0, 1); b = Math.Clamp((1 - a) * pb + bb * sb, 0, 1);
                        break;
                    }
                    case 3:
                    {
                        Rgb(src, out float cr, out float cg, out float cb);
                        GlowCol(Lum(cr, cg, cb), col[2], out r, out g, out b);
                        break;
                    }
                    default: o[i] = src; continue;
                }
                if (strength < 1)
                {
                    Rgb(src, out float cr, out float cg, out float cb);
                    r = cr + (r - cr) * strength; g = cg + (g - cg) * strength; b = cb + (b - cb) * strength;
                }
                o[i] = (int)(src & 0xFF000000) | (int)(b * 255 + 0.5f) << 16 | (int)(g * 255 + 0.5f) << 8 | (int)(r * 255 + 0.5f);
            }
            return o;
        }
    }
}
