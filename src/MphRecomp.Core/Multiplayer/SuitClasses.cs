using System;
using System.Collections.Generic;

namespace MphRecomp.Multiplayer
{
    // Which colour group each texel of a Samus suit texture belongs to, for GxShader's suit recolour (uTint w 11+k). Picking
    // the group per pixel from the texel's own hue speckles (a highlight on an orange plate reads yellow), and from a blurred
    // hue bleeds across seams ("badly spray painted", owner 2026-10-24); so the groups are worked out once per texture: each
    // texel by its hue, then each patch of smoothly-changing paint votes as one, stray specks join what surrounds them, and a
    // paint island that is mostly helmet/chest goes helmet/chest whole. The result is a texture the shader samples beside the
    // suit's own: R the second group, G helmet/chest, B lights, A recoloured at all (the rest of A is the main group); 0 = as is
    // (in a glow texture: the lights); R+G without A = the face, drawn in the visor glass colour.
    public static class SuitClasses
    {
        const byte Bg = 0, Grey = 1, Main = 2, Second = 3, Chest = 4, Lights = 5, Keep = 6, Dots = 7, Glass = 8;

        // per-suit extras (SuitColours.Rules): Dark, the brightness under which a big dark patch (the under-suit between the
        // plates) keeps its own colour; Dots, the hue of small bright marks drawn in the lights colour (MP2 Dark Suit's red
        // dots); Glow, textures drawn whole in the lights colour; Plain, textures that keep their own colour (Phazon's thigh
        // veins and scratches: the suit's own look has them dim red on silver, owner 2026-10-24); Regions, texture rects
        // (0-1) whose biggest paint patch is helmet/chest whole; Dim, the brightness under which paint can't be a light (MP2 Dark
        // Suit's dark brown neck ribbing sits at the orange visor's hue, so it went two colours, owner 2026-10-24)
        // Pieces: the suit is recoloured on the CPU piece by piece instead (SuitPieces; Beta Phazon, owner 2026-10-29)
        public sealed record Rules(float Dark = -1, float Dots = -1, string[]? Glow = null, string[]? Plain = null, bool Split = false,
            float Dim = -1, SuitPieces.Spec? Pieces = null, params Region[] Regions);
        // Poly (optional): x,y pairs in 1/512ths of the texture, an outline the patch must stay inside (the rect is then ignored);
        // Second: the patch goes second colour whole instead (PED's gold knee band went two colours, owner 2026-10-24); Keep:
        // every texel in the rect keeps its own colour (PED's helmet tubes and neck ticks share the body texture); Glass: the
        // patch is visor glass, lights colour whole, its pale highlight too (owner 2026-10-29: the P3 Varia visor's near-white
        // centre strip went the second colour, a line down the middle of the visor)
        public readonly record struct Region(string Texture, float X0, float Y0, float X1, float Y1, int[]? Poly = null, bool Second = false,
            bool Keep = false, bool Glass = false);

        // Samus's face (seen through the visor) is skin, not paint: in a recolour it takes the visor glass colour, flat.
        // Prime 3's face is in pieces: the skin plus eyeballs, eye shine and lashes (MP3 Varia 52c8abbb/56746dcc/183d0321/
        // bead0857/33b41760/32d83ac0,
        // PED 32a21a99/6b3131e4), so its eyes showed through every recoloured visor (owner 2026-10-24)
        static readonly string[] FaceTextures = { "51e5b069", "94cc8e0e", "a4f474a3", "ddb0329f",
            "52c8abbb", "56746dcc", "183d0321", "bead0857", "33b41760", "32d83ac0", "32a21a99", "6b3131e4",
            "f45e67b8" }; // Beta Phazon

        public static bool IsFace(string png)
        {
            string name = System.IO.Path.GetFileName(png.Replace('\\', '/'));
            foreach (string f in FaceTextures) if (name.StartsWith(f, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        // px: RGBA bytes as GL takes them (R the low byte); hues: main, second, third, lights, chest (-1 none). Returns the
        // class texture in the same layout
        public static int[] Build(int[] px, int w, int h, (float Main, float Second, float Third, float Lights, float Chest) hues, bool face,
            Rules? rules = null, string? texture = null)
        {
            int n = w * h;
            var outPx = new int[n];
            if (face) { Array.Fill(outPx, 0x0000FFFF); return outPx; }
            if (n == 0) return outPx;
            var lab = new byte[n];
            var rgb = new float[n * 3];
            var glow = new bool[n];
            for (int i = 0; i < n; i++)
            {
                int p = px[i];
                float r = (p & 0xFF) / 255f, g = ((p >> 8) & 0xFF) / 255f, b = ((p >> 16) & 0xFF) / 255f;
                rgb[i * 3] = r; rgb[i * 3 + 1] = g; rgb[i * 3 + 2] = b;
                float v = MathF.Max(r, MathF.Max(g, b)), mn = MathF.Min(r, MathF.Min(g, b)), d = v - mn;
                float s = v > 0 ? d / v : 0;
                if (v < 0.06f) { lab[i] = Bg; continue; }
                if (s < 0.18f) { lab[i] = Grey; continue; }
                float hue = Hue(r, g, b, v, d);
                float dm = Hd(hue, hues.Main), ds = MathF.Min(Hd(hue, hues.Second), Hd(hue, hues.Third)), dc = Hd(hue, hues.Chest), dl = Hd(hue, hues.Lights);
                if (rules != null && rules.Dots >= 0 && s > 0.65f && v > 0.08f && Hd(hue, rules.Dots) < 15) { lab[i] = Dots; continue; }
                byte k = Main; float best = dm;
                if (ds < best) { k = Second; best = ds; }
                if (dc < best) { k = Chest; best = dc; }
                if (dl < best && (rules == null || v >= rules.Dim)) { k = Lights; best = dl; }
                // paint whose hue sits near the lights' too may glow: the shader lights it where it's bright and deep (MP2 Dark
                // Suit: brown body 15, orange visor 25, owner 2026-10-24: the visor went the body's colour)
                if (dl < 20) glow[i] = true;
                lab[i] = best < 32 ? k : Keep;
            }
            int minsz = Math.Max(16, n / 1500);
            GlowPatches(glow, rgb, w, h);
            Vote(lab, rgb, w, h, minsz, rules?.Split == true);
            Cleanup(lab, w, h, minsz);
            Slivers(lab, w, h, minsz * 20);
            Rims(lab, rgb, w, h, hues);
            Shades(lab, rgb, w, h, hues);
            Creases(lab, rgb, w, h, minsz * 20);
            Islands(lab, rgb, w, h);
            Weathering(lab, w, h, minsz * 20);
            if (rules != null)
            {
                if (texture != null)
                {
                    string name = System.IO.Path.GetFileName(texture.Replace('\\', '/'));
                    foreach (Region r in rules.Regions) if (name.StartsWith(r.Texture, StringComparison.OrdinalIgnoreCase)) FillRegion(lab, rgb, w, h, r, glow);
                    // a glow texture (PED's chest emblem: green plates and a cyan star) is drawn whole in the lights colour, as the
                    // suit's own look lights all of it (owner 2026-10-24: the star went the main colour)
                    if (rules.Glow != null && Array.Exists(rules.Glow, g => name.StartsWith(g, StringComparison.OrdinalIgnoreCase)))
                        for (int i = 0; i < n; i++)
                            if (lab[i] is Grey or Main or Second or Chest) lab[i] = Lights;
                    if (rules.Plain != null && Array.Exists(rules.Plain, g => name.StartsWith(g, StringComparison.OrdinalIgnoreCase)))
                        for (int i = 0; i < n; i++) if (lab[i] != Bg) lab[i] = Keep;
                }
                if (rules.Dark > 0) Under(lab, rgb, w, h, rules.Dark, minsz * 2);
            }
            for (int i = 0; i < n; i++)
            {
                outPx[i] = lab[i] switch
                {
                    Grey or Main => unchecked((int)0xFF000000),
                    Second => unchecked((int)0xFF0000FF),
                    Chest => unchecked((int)0xFF00FF00),
                    Lights => unchecked((int)0xFFFF0000),
                    Dots => 0x00FF0000,
                    Glass => unchecked((int)0xFFFF0001), // lights, R = 1/255: the shader paints it lights colour whole
                    _ => 0,
                };
                if (glow[i] && lab[i] is Main or Second or Chest) outPx[i] |= 0x00FF0000;
            }
            return outPx;
        }

        static float Hue(float r, float g, float b, float v, float d)
        {
            if (d <= 0) return 0;
            float h = v == r ? ((g - b) / d % 6 + 6) % 6 : v == g ? (b - r) / d + 2 : (r - g) / d + 4;
            return h * 60;
        }

        static float Hd(float a, float t)
        {
            if (t < 0) return 999;
            float d = MathF.Abs(a - t) % 360;
            return MathF.Min(d, 360 - d);
        }

        static int Find(int[] par, int x)
        {
            while (par[x] != x) { par[x] = par[par[x]]; x = par[x]; }
            return x;
        }

        static float Diff(float[] rgb, int a, int b) =>
            MathF.Max(MathF.Abs(rgb[a * 3] - rgb[b * 3]), MathF.Max(MathF.Abs(rgb[a * 3 + 1] - rgb[b * 3 + 1]), MathF.Abs(rgb[a * 3 + 2] - rgb[b * 3 + 2])));

        static float Val(float[] rgb, int a) => MathF.Max(rgb[a * 3], MathF.Max(rgb[a * 3 + 1], rgb[a * 3 + 2]));

        // patches of paint whose neighbouring texels differ by under 0.06: a big one takes its majority colour group (if a
        // quarter of it is coloured at all); its greys stay greys
        static void Vote(byte[] lab, float[] rgb, int w, int h, int minsz, bool split)
        {
            int n = w * h;
            var par = new int[n];
            for (int i = 0; i < n; i++) par[i] = i;
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    int i = y * w + x;
                    if (Val(rgb, i) <= 0.06f) continue;
                    // split: a patch only joins texels of its own hue group (MP3 Varia's soft paint runs its yellow plates into
                    // the orange around them, so the orange outvoted them; owner 2026-10-24: zones should match the other Varias)
                    if (x + 1 < w && Val(rgb, i + 1) > 0.06f && Diff(rgb, i, i + 1) < 0.06f && (!split || lab[i] == lab[i + 1])) Union(par, i, i + 1);
                    if (y + 1 < h && Val(rgb, i + w) > 0.06f && Diff(rgb, i, i + w) < 0.06f && (!split || lab[i] == lab[i + w])) Union(par, i, i + w);
                }
            }
            var cnt = new Dictionary<int, int[]>();
            var root = new int[n];
            for (int i = 0; i < n; i++)
            {
                int r = root[i] = Find(par, i);
                if (!cnt.TryGetValue(r, out int[]? c)) cnt[r] = c = new int[9];
                c[lab[i]]++;
                c[8]++;
            }
            var best = new Dictionary<int, byte>();
            foreach (var (r, c) in cnt)
            {
                if (c[8] < minsz) continue;
                int all = c[1] + c[2] + c[3] + c[4] + c[5] + c[6], colour = c[2] + c[3] + c[4] + c[5];
                if (colour <= 0.25f * all) continue;
                byte k = Main;
                for (byte j = Second; j <= Lights; j++) if (c[j] > c[k]) k = j;
                best[r] = k;
            }
            for (int i = 0; i < n; i++)
            {
                if (lab[i] >= Main && lab[i] != Dots && best.TryGetValue(root[i], out byte k)) lab[i] = k;
            }
        }

        static void Union(int[] par, int a, int b)
        {
            int ra = Find(par, a), rb = Find(par, b);
            if (ra != rb) par[rb] = ra;
        }

        // a speck of one group smaller than minsz takes the commonest group within two texels around it
        static void Cleanup(byte[] lab, int w, int h, int minsz)
        {
            int n = w * h;
            var comp = new int[n];
            var queue = new int[n];
            var ring = new List<int>();
            var mark = new int[n];
            int stamp = 0;
            for (byte k = Grey; k <= Keep; k++)
            {
                Array.Fill(comp, 0);
                int id = 0;
                for (int s = 0; s < n; s++)
                {
                    if (lab[s] != k || comp[s] != 0) continue;
                    id++;
                    int head = 0, tail = 0;
                    queue[tail++] = s; comp[s] = id;
                    while (head < tail)
                    {
                        int i = queue[head++], x = i % w, y = i / w;
                        if (x > 0 && lab[i - 1] == k && comp[i - 1] == 0) { comp[i - 1] = id; queue[tail++] = i - 1; }
                        if (x + 1 < w && lab[i + 1] == k && comp[i + 1] == 0) { comp[i + 1] = id; queue[tail++] = i + 1; }
                        if (y > 0 && lab[i - w] == k && comp[i - w] == 0) { comp[i - w] = id; queue[tail++] = i - w; }
                        if (y + 1 < h && lab[i + w] == k && comp[i + w] == 0) { comp[i + w] = id; queue[tail++] = i + w; }
                    }
                    if (tail >= minsz) continue;
                    // the ring: texels within 2 steps (4-neighbour) of the speck, outside it
                    stamp++;
                    for (int q = 0; q < tail; q++) mark[queue[q]] = stamp;
                    ring.Clear();
                    var votes = new int[7];
                    for (int q = 0; q < tail; q++)
                    {
                        int i = queue[q], x = i % w, y = i / w;
                        for (int dy = -2; dy <= 2; dy++)
                        {
                            for (int dx = -2; dx <= 2; dx++)
                            {
                                if (Math.Abs(dx) + Math.Abs(dy) > 2) continue;
                                int xx = x + dx, yy = y + dy;
                                if (xx < 0 || yy < 0 || xx >= w || yy >= h) continue;
                                int j = yy * w + xx;
                                if (mark[j] == stamp) continue;
                                mark[j] = stamp;
                                byte l = lab[j];
                                if (l != Bg && l != k && l != Dots) votes[l]++;
                            }
                        }
                    }
                    int bestK = -1;
                    for (int j = 1; j < 7; j++) if (votes[j] > 0 && (bestK < 0 || votes[j] > votes[bestK])) bestK = j;
                    if (bestK < 0) continue;
                    for (int q = 0; q < tail; q++) lab[queue[q]] = (byte)bestK;
                }
            }
        }

        // a thin line of one paint group (most of its texels on its edge) lying in another paint group's patch: a crease in the
        // texture, not a stripe of its own (owner 2026-10-24: helmet-red slivers on recoloured shoulders); lights, greys and
        // kept colours are never folded, nor anything bigger than maxsz
        static void Slivers(byte[] lab, int w, int h, int maxsz)
        {
            int n = w * h;
            var comp = new bool[n];
            var queue = new int[n];
            var mark = new int[n];
            int stamp = 0;
            for (int s = 0; s < n; s++)
            {
                byte k = lab[s];
                if (comp[s] || (k != Main && k != Second && k != Chest)) continue;
                int head = 0, tail = 0, edge = 0;
                queue[tail++] = s; comp[s] = true;
                while (head < tail)
                {
                    int i = queue[head++], x = i % w, y = i / w;
                    bool onEdge = false;
                    void Step(int j) { if (lab[j] == k) { if (!comp[j]) { comp[j] = true; queue[tail++] = j; } } else onEdge = true; }
                    if (x > 0) Step(i - 1); else onEdge = true;
                    if (x + 1 < w) Step(i + 1); else onEdge = true;
                    if (y > 0) Step(i - w); else onEdge = true;
                    if (y + 1 < h) Step(i + w); else onEdge = true;
                    if (onEdge) edge++;
                }
                if (tail > maxsz || edge < 0.7f * tail) continue;
                stamp++;
                for (int q = 0; q < tail; q++) mark[queue[q]] = stamp;
                var votes = new int[7];
                int all = 0;
                for (int q = 0; q < tail; q++)
                {
                    int i = queue[q], x = i % w, y = i / w;
                    for (int d = 0; d < 4; d++)
                    {
                        int xx = x + (d == 0 ? -1 : d == 1 ? 1 : 0), yy = y + (d == 2 ? -1 : d == 3 ? 1 : 0);
                        if (xx < 0 || yy < 0 || xx >= w || yy >= h) continue;
                        int j = yy * w + xx;
                        if (mark[j] == stamp) continue;
                        mark[j] = stamp;
                        if (lab[j] == Bg) continue;
                        if (lab[j] < votes.Length) votes[lab[j]]++;
                        all++;
                    }
                }
                int bestK = Main;
                foreach (byte j in new[] { Main, Second, Chest }) if (j != k && (bestK == k || votes[j] > votes[bestK])) bestK = j;
                if (bestK == k || votes[bestK] < 0.8f * all) continue;
                for (int q = 0; q < tail; q++) lab[queue[q]] = (byte)bestK;
            }
        }

        // the shaded rim where one plate falls off into a seam or a neighbouring plate: going from each texel to its brightest
        // neighbour, step by step, leads up into the paint it is the shadow of; if that is another group and the texel is dark
        // or its hue nearer that group's, it takes that group (owner 2026-10-24: a helmet-red line along the recoloured
        // shoulder's edge, where the yellow plate darkens to brown beside the red stripe)
        static void Rims(byte[] lab, float[] rgb, int w, int h, (float Main, float Second, float Third, float Lights, float Chest) hues)
        {
            int n = w * h;
            var src = (byte[])lab.Clone();
            for (int s = 0; s < n; s++)
            {
                byte k = src[s];
                if (k != Main && k != Second && k != Chest) continue;
                int i = s;
                for (int step = 0; step < 8; step++)
                {
                    int x = i % w, y = i / w, next = i;
                    float bv = Val(rgb, i);
                    for (int dy = -1; dy <= 1; dy++)
                    {
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            int xx = x + dx, yy = y + dy;
                            if ((dx == 0 && dy == 0) || xx < 0 || yy < 0 || xx >= w || yy >= h) continue;
                            int j = yy * w + xx;
                            if (src[j] == Bg || src[j] == Lights || src[j] == Keep || src[j] == Dots) continue;
                            if (Val(rgb, j) > bv) { bv = Val(rgb, j); next = j; }
                        }
                    }
                    if (next == i) break;
                    i = next;
                }
                byte t = src[i];
                if (t == k || (t != Main && t != Second && t != Chest)) continue;
                float v0 = Val(rgb, s);
                if (Val(rgb, i) < 1.3f * v0) continue;
                float r = rgb[s * 3], g = rgb[s * 3 + 1], b = rgb[s * 3 + 2], mn = MathF.Min(r, MathF.Min(g, b));
                float hue = Hue(r, g, b, v0, v0 - mn);
                if (v0 < 0.35f || Hd(hue, GroupHue(t, hues)) < Hd(hue, GroupHue(k, hues))) lab[s] = t;
            }
        }

        // the plate's own dark falloff: its brown shading drifts in hue toward the helmet/chest red, so dark texels whose hue
        // sits between the main colour's and their group's (and not right on the group's), reached from the main plate through
        // such texels, are main (owner 2026-10-24: Power Suit shoulder seams striped in the helmet colour)
        static void Shades(byte[] lab, float[] rgb, int w, int h, (float Main, float Second, float Third, float Lights, float Chest) hues)
        {
            int n = w * h;
            if (hues.Main < 0) return;
            var ok = new bool[n];
            for (int i = 0; i < n; i++)
            {
                byte k = lab[i];
                if (k != Chest && k != Second) continue;
                float v = Val(rgb, i);
                if (v >= 0.35f) continue;
                float r = rgb[i * 3], g = rgb[i * 3 + 1], b = rgb[i * 3 + 2], mn = MathF.Min(r, MathF.Min(g, b));
                float hue = Hue(r, g, b, v, v - mn), t = GroupHue(k, hues);
                float dm = Hd(hue, hues.Main), dk = Hd(hue, t), arc = Hd(hues.Main, t);
                ok[i] = dk >= 6 && dm + dk <= arc + 1;
            }
            var queue = new int[n];
            int head = 0, tail = 0;
            for (int i = 0; i < n; i++) if (lab[i] == Main) queue[tail++] = i;
            while (head < tail)
            {
                int i = queue[head++], x = i % w, y = i / w;
                void Visit(int j) { if (ok[j]) { ok[j] = false; lab[j] = Main; queue[tail++] = j; } }
                if (x > 0) Visit(i - 1);
                if (x + 1 < w) Visit(i + 1);
                if (y > 0) Visit(i - w);
                if (y + 1 < h) Visit(i + w);
            }
        }

        // a helmet/second patch walled in by the main colour whose own hue matches the paint around it: a groove cut in that
        // paint, not a mark of its own (owner 2026-10-24: the Power Suit shoulder pad's groove went jagged helmet-red)
        static void Creases(byte[] lab, float[] rgb, int w, int h, int maxsz)
        {
            int n = w * h;
            var comp = new bool[n];
            var queue = new int[n];
            var mark = new int[n];
            int stamp = 0;
            for (int s = 0; s < n; s++)
            {
                byte k = lab[s];
                if (comp[s] || (k != Second && k != Chest)) continue;
                int head = 0, tail = 0;
                queue[tail++] = s; comp[s] = true;
                while (head < tail)
                {
                    int i = queue[head++], x = i % w, y = i / w;
                    void Step(int j) { if (lab[j] == k && !comp[j]) { comp[j] = true; queue[tail++] = j; } }
                    if (x > 0) Step(i - 1);
                    if (x + 1 < w) Step(i + 1);
                    if (y > 0) Step(i - w);
                    if (y + 1 < h) Step(i + w);
                }
                if (tail > maxsz) continue;
                stamp++;
                for (int q = 0; q < tail; q++) mark[queue[q]] = stamp;
                int all = 0, main = 0;
                float ix = 0, iy = 0, ox = 0, oy = 0;
                for (int q = 0; q < tail; q++)
                {
                    int i = queue[q], x = i % w, y = i / w;
                    HueSum(rgb, i, ref ix, ref iy);
                    for (int d = 0; d < 4; d++)
                    {
                        int xx = x + (d == 0 ? -1 : d == 1 ? 1 : 0), yy = y + (d == 2 ? -1 : d == 3 ? 1 : 0);
                        if (xx < 0 || yy < 0 || xx >= w || yy >= h) continue;
                        int j = yy * w + xx;
                        if (mark[j] == stamp) continue;
                        mark[j] = stamp;
                        if (lab[j] == Bg) continue;
                        all++;
                        if (lab[j] != Main) continue;
                        main++;
                        HueSum(rgb, j, ref ox, ref oy);
                    }
                }
                if (all == 0 || main < 0.9f * all) continue;
                float hin = MathF.Atan2(iy, ix) * 180 / MathF.PI, hout = MathF.Atan2(oy, ox) * 180 / MathF.PI;
                if (Hd((hin + 360) % 360, (hout + 360) % 360) > 15) continue;
                for (int q = 0; q < tail; q++) lab[queue[q]] = Main;
            }
        }

        // adds a texel's hue as a unit vector weighted by its saturation (for a circular mean)
        static void HueSum(float[] rgb, int i, ref float x, ref float y)
        {
            float r = rgb[i * 3], g = rgb[i * 3 + 1], b = rgb[i * 3 + 2], v = MathF.Max(r, MathF.Max(g, b)), d = v - MathF.Min(r, MathF.Min(g, b));
            if (v <= 0 || d <= 0) return;
            float a = Hue(r, g, b, v, d) * MathF.PI / 180, s = d / v;
            x += s * MathF.Cos(a); y += s * MathF.Sin(a);
        }

        static float GroupHue(byte k, (float Main, float Second, float Third, float Lights, float Chest) hues) =>
            k == Main ? hues.Main : k == Chest ? hues.Chest : hues.Second;

        // a paint island (texels joined by anything not black) that is mostly helmet/chest goes helmet/chest whole
        static void Islands(byte[] lab, float[] rgb, int w, int h)
        {
            int n = w * h;
            var seen = new bool[n];
            var queue = new int[n];
            for (int s = 0; s < n; s++)
            {
                if (seen[s] || Val(rgb, s) <= 0.06f) continue;
                int head = 0, tail = 0, col = 0, chest = 0;
                queue[tail++] = s; seen[s] = true;
                while (head < tail)
                {
                    int i = queue[head++], x = i % w, y = i / w;
                    byte l = lab[i];
                    if (l == Main || l == Second || l == Chest) col++;
                    if (l == Chest) chest++;
                    void Visit(int j) { if (!seen[j] && Val(rgb, j) > 0.06f) { seen[j] = true; queue[tail++] = j; } }
                    if (x > 0) Visit(i - 1);
                    if (x + 1 < w) Visit(i + 1);
                    if (y > 0) Visit(i - w);
                    if (y + 1 < h) Visit(i + w);
                }
                if (col == 0 || chest <= 0.6f * col) continue;
                for (int q = 0; q < tail; q++) if (lab[queue[q]] == Main || lab[queue[q]] == Second) lab[queue[q]] = Chest;
            }
        }

        // a small grey speck (weathering, a scratch) lying in a second or helmet/chest patch takes that patch's group, so it
        // is tinted with it, not with the main colour (owner 2026-10-24: spots "left out" of the Dark Suit body and the
        // Echoes Varia chest)
        static void Weathering(byte[] lab, int w, int h, int maxsz)
        {
            int n = w * h;
            var comp = new bool[n];
            var queue = new int[n];
            var mark = new int[n];
            int stamp = 0;
            for (int s = 0; s < n; s++)
            {
                if (comp[s] || lab[s] != Grey) continue;
                int head = 0, tail = 0;
                queue[tail++] = s; comp[s] = true;
                while (head < tail)
                {
                    int i = queue[head++], x = i % w, y = i / w;
                    void Step(int j) { if (lab[j] == Grey && !comp[j]) { comp[j] = true; queue[tail++] = j; } }
                    if (x > 0) Step(i - 1);
                    if (x + 1 < w) Step(i + 1);
                    if (y > 0) Step(i - w);
                    if (y + 1 < h) Step(i + w);
                }
                if (tail > maxsz) continue;
                stamp++;
                for (int q = 0; q < tail; q++) mark[queue[q]] = stamp;
                int all = 0, sec = 0, chest = 0;
                for (int q = 0; q < tail; q++)
                {
                    int i = queue[q], x = i % w, y = i / w;
                    for (int d = 0; d < 4; d++)
                    {
                        int xx = x + (d == 0 ? -1 : d == 1 ? 1 : 0), yy = y + (d == 2 ? -1 : d == 3 ? 1 : 0);
                        if (xx < 0 || yy < 0 || xx >= w || yy >= h) continue;
                        int j = yy * w + xx;
                        if (mark[j] == stamp) continue;
                        mark[j] = stamp;
                        if (lab[j] == Bg) continue;
                        all++;
                        if (lab[j] == Second) sec++;
                        else if (lab[j] == Chest) chest++;
                    }
                }
                byte t = sec >= 0.85f * all ? Second : chest >= 0.85f * all ? Chest : Grey;
                if (all == 0 || t == Grey) continue;
                for (int q = 0; q < tail; q++) lab[queue[q]] = t;
            }
        }

        // a texture rect whose biggest paint patch (texels joined by anything not black) is one plate: its painted texels go
        // helmet/chest (owner 2026-10-24: the Echoes Varia chest plate is red and orange-shaded paint; only the red went chest).
        // Plates that touch other pieces in the texture take an outline instead (the Echoes Varia chest touches an upper-arm
        // and a foot piece, which went chest with a rect)
        static void FillRegion(byte[] lab, float[] rgb, int w, int h, Region r, bool[]? glow = null)
        {
            int x0 = (int)(r.X0 * w), x1 = Math.Min(w, (int)(r.X1 * w)), y0 = (int)(r.Y0 * h), y1 = Math.Min(h, (int)(r.Y1 * h));
            int n = w * h;
            bool[]? inside = null;
            if (r.Poly != null)
            {
                // even-odd fill of the outline at texel centres
                inside = new bool[n];
                int m = r.Poly.Length / 2;
                x0 = w; x1 = 0; y0 = h; y1 = 0;
                for (int k = 0; k < m; k++)
                {
                    int px = r.Poly[k * 2] * w / 512, py = r.Poly[k * 2 + 1] * h / 512;
                    x0 = Math.Min(x0, px); x1 = Math.Max(x1, px); y0 = Math.Min(y0, py); y1 = Math.Max(y1, py);
                }
                x0 = Math.Max(0, x0); y0 = Math.Max(0, y0); x1 = Math.Min(w, x1 + 1); y1 = Math.Min(h, y1 + 1);
                for (int y = y0; y < y1; y++)
                {
                    float fy = (y + 0.5f) * 512f / h;
                    for (int x = x0; x < x1; x++)
                    {
                        float fx = (x + 0.5f) * 512f / w;
                        bool c = false;
                        for (int k = 0, l = m - 1; k < m; l = k++)
                        {
                            float ax = r.Poly[k * 2], ay = r.Poly[k * 2 + 1], bx = r.Poly[l * 2], by = r.Poly[l * 2 + 1];
                            if ((ay > fy) != (by > fy) && fx < (bx - ax) * (fy - ay) / (by - ay) + ax) c = !c;
                        }
                        inside[y * w + x] = c;
                    }
                }
            }
            if (r.Keep)
            {
                for (int y = y0; y < y1; y++)
                    for (int x = x0; x < x1; x++)
                        if ((inside == null || inside[y * w + x]) && lab[y * w + x] != Bg) lab[y * w + x] = Keep;
                return;
            }
            var seen = new bool[n];
            var queue = new int[n];
            int[] bestQ = Array.Empty<int>();
            for (int sy = y0; sy < y1; sy++)
            {
                for (int sx = x0; sx < x1; sx++)
                {
                    int s = sy * w + sx;
                    if (seen[s] || Val(rgb, s) <= 0.1f || (inside != null && !inside[s])) continue;
                    int head = 0, tail = 0;
                    queue[tail++] = s; seen[s] = true;
                    while (head < tail)
                    {
                        int i = queue[head++], x = i % w, y = i / w;
                        void Visit(int j, int xx, int yy) { if (xx >= x0 && xx < x1 && yy >= y0 && yy < y1 && !seen[j] && Val(rgb, j) > 0.1f && (inside == null || inside[j])) { seen[j] = true; queue[tail++] = j; } }
                        Visit(i - 1, x - 1, y); Visit(i + 1, x + 1, y); Visit(i - w, x, y - 1); Visit(i + w, x, y + 1);
                    }
                    if (tail > bestQ.Length) bestQ = queue[..tail];
                }
            }
            byte to = r.Glass ? Glass : r.Second ? Second : Chest;
            foreach (int i in bestQ)
            {
                if (lab[i] is Main or Second or Chest || r.Glass && lab[i] == Lights) lab[i] = to;
                if (r.Glass && glow != null) glow[i] = false;
            }
        }

        // the dark under-suit between the plates (Light Suit's black ribbing, PED's navy body): big patches darker than dark
        // keep their own colour, as in the suit's own look (owner 2026-10-24); a plate's thin shaded edge is not one (the
        // patch is eroded 2 texels and grown back 3, within the dark texels)
        static void Under(byte[] lab, float[] rgb, int w, int h, float dark, int minsz)
        {
            int n = w * h;
            var m = new bool[n];
            for (int i = 0; i < n; i++) m[i] = lab[i] is Grey or Main or Second or Chest && Val(rgb, i) < dark;
            var core = new int[n];
            Array.Fill(core, -1);
            var queue = new int[n];
            int tail = 0;
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    int i = y * w + x;
                    if (!m[i]) continue;
                    bool all = true;
                    for (int dy = -2; dy <= 2 && all; dy++)
                    {
                        for (int dx = -2; dx <= 2 && all; dx++)
                        {
                            int xx = x + dx, yy = y + dy;
                            if (xx < 0 || yy < 0 || xx >= w || yy >= h || !m[yy * w + xx]) all = false;
                        }
                    }
                    if (all) { core[i] = 0; queue[tail++] = i; }
                }
            }
            int head = 0;
            while (head < tail)
            {
                int i = queue[head++], x = i % w, y = i / w;
                if (core[i] >= 3) continue;
                void Grow(int j) { if (m[j] && core[j] < 0) { core[j] = core[i] + 1; queue[tail++] = j; } }
                if (x > 0) Grow(i - 1);
                if (x + 1 < w) Grow(i + 1);
                if (y > 0) Grow(i - w);
                if (y + 1 < h) Grow(i + w);
            }
            var comp = new bool[n];
            for (int s = 0; s < n; s++)
            {
                if (comp[s] || core[s] < 0) continue;
                int qh = 0, qt = 0;
                queue[qt++] = s; comp[s] = true;
                while (qh < qt)
                {
                    int i = queue[qh++], x = i % w, y = i / w;
                    void Step(int j) { if (core[j] >= 0 && !comp[j]) { comp[j] = true; queue[qt++] = j; } }
                    if (x > 0) Step(i - 1);
                    if (x + 1 < w) Step(i + 1);
                    if (y > 0) Step(i - w);
                    if (y + 1 < h) Step(i + w);
                }
                if (qt < minsz) continue;
                for (int q = 0; q < qt; q++) lab[queue[q]] = Keep;
            }
        }

        // the glow flag stays on a patch that is mostly bright, deep paint (the orange visor), not on paint that only shares
        // its hue (owner 2026-10-24: the Dark Suit's brown neck ribbing went two colours, stripe by stripe)
        static void GlowPatches(bool[] glow, float[] rgb, int w, int h)
        {
            int n = w * h;
            var seen = new bool[n];
            var queue = new int[n];
            for (int s = 0; s < n; s++)
            {
                if (seen[s] || !glow[s]) continue;
                int head = 0, tail = 0, deep = 0;
                queue[tail++] = s; seen[s] = true;
                while (head < tail)
                {
                    int i = queue[head++], x = i % w, y = i / w;
                    float v = Val(rgb, i), mn = MathF.Min(rgb[i * 3], MathF.Min(rgb[i * 3 + 1], rgb[i * 3 + 2]));
                    if (v > 0.6f && (v - mn) / v > 0.7f) deep++;
                    void Step(int j) { if (glow[j] && !seen[j]) { seen[j] = true; queue[tail++] = j; } }
                    if (x > 0) Step(i - 1);
                    if (x + 1 < w) Step(i + 1);
                    if (y > 0) Step(i - w);
                    if (y + 1 < h) Step(i + w);
                }
                if (deep >= tail / 4) continue;
                for (int q = 0; q < tail; q++) glow[queue[q]] = false;
            }
        }
    }
}
