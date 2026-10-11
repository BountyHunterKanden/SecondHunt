using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using MphRecomp.Assets;
using MphRecomp.Import;

namespace MphRecomp.Arenas
{
    // An imported arena's picture for the multiplayer menu's ARENA box (Vanilla multiplayer menu M5), in the place of the
    // ROM's stage pictures: an elevated, angled view into the arena. Rendered at import from the HD look ArenaHdWriter
    // has just written (arena.bin, arena.gx.json, the textures), on the CPU, so every platform makes the same picture
    // without a GL context: the same mesh, the same GX-TEV programs (GxTev, the CPU reference GxShader mirrors) and the
    // same inputs the device draws them with (CampaignArena.cs: no scene light on the world, the lightmaps light it; the
    // sky around the camera with ambient 1; back faces culled on the sky and the world (device 2026-10-08: Echoes' thin
    // walls are back-to-back faces in one plane, unculled they z-fought); GX blend factors), supersampled.
    // The camera is searched (owner, 2026-10-07: "a sort of cinematic angled view of above the map"): candidates inside
    // the playable area (raised and pulled back over the focus points, the spawns and pickups, with nothing solid between
    // point and lens) plus a ring above the middle, each scored on a small depth render by how many focus points it sees
    // and how far it reaches, less walls in front of the lens or the subject, back faces (a camera inside or behind
    // geometry) and empty frame, looking down 20-45 degrees preferred.
    public static class ArenaThumbnail
    {
        public const string FileName = "thumb.png";
        // the stage picture frame is 116 x 74 menu units; this is it at 3.3x for the HD canvases
        public const int Width = 384, Height = 245;
        const int Super = 2;
        const float FovYDeg = 55;
        const float Near = 0.1f;
        const int SearchW = 64, SearchH = 41;

        public sealed class Result
        {
            public Vector3 Eye, Target;
            public int Seen, Focus;
            public float Score;
            public double Ms;
        }

        // writes <hdDir>/thumb.png (or outPath); null when the bundle can't be read
        // inside: keep the lens within the world's bounds, under its top (a view into the arena, as the ROM's pictures),
        // else views from above it are candidates too
        public static Result? Write(string hdDir, IReadOnlyList<Vector3> focus, string? outPath = null, Action<string>? log = null,
            bool inside = false)
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            Bundle? b = Bundle.Read(hdDir, log);
            if (b == null) return null;
            Debug = Environment.GetEnvironmentVariable("THUMB_DEBUG") == "1" ? new() : null;
            Result pick = Search(b, focus, inside);
            // THUMB_EYE=ex,ey,ez,tx,ty,tz (dev, T6 2026-10-10): this lens instead of the searched one
            if (Environment.GetEnvironmentVariable("THUMB_EYE") is string eyeArg && eyeArg.Split(',') is { Length: 6 } ev)
            {
                float F(int i) => float.Parse(ev[i], System.Globalization.CultureInfo.InvariantCulture);
                pick = new Result { Eye = new Vector3(F(0), F(1), F(2)), Target = new Vector3(F(3), F(4), F(5)), Focus = pick.Focus };
            }
            if (Debug != null)
            {
                log?.Invoke($"thumbnail: {Debug.Count} candidates");
                foreach (var d in Debug.OrderByDescending(d => d.Score).Take(6)) log?.Invoke(FormattableString.Invariant($"  {d.Score:0.00} {d.Line}"));
            }
            byte[] rgba = Render(b, pick.Eye, pick.Target, Width, Height);
            PngOut.Write(outPath ?? Path.Combine(hdDir, FileName), rgba, Width, Height);
            // THUMB_ALTS=n (with THUMB_DEBUG=1): the n best candidates with lenses 3 m apart, as <picture>_alt<k>.png
            if (Debug != null && Int32.TryParse(Environment.GetEnvironmentVariable("THUMB_ALTS"), out int alts))
            {
                var shown = new List<Vector3>();
                foreach (var d in Debug.OrderByDescending(d => d.Score))
                {
                    if (shown.Count >= alts) break;
                    if (shown.Any(e => Vector3.DistanceSquared(e, d.Eye) < 9)) continue;
                    shown.Add(d.Eye);
                    string alt = Path.ChangeExtension(outPath ?? Path.Combine(hdDir, FileName), null) + $"_alt{shown.Count}.png";
                    PngOut.Write(alt, Render(b, d.Eye, d.Target, Width, Height), Width, Height);
                    log?.Invoke(FormattableString.Invariant($"thumbnail: alt {shown.Count} {d.Score:0.00} {d.Line}"));
                }
            }
            pick.Ms = clock.Elapsed.TotalMilliseconds;
            if (Environment.GetEnvironmentVariable("THUMB_DEBUG") == "1" && focus.Count > 0)
            {
                List<Vector3> box = OpaqueCorners(b);
                log?.Invoke($"thumbnail: world {Fmt(box[0])}..{Fmt(box[1])}, focus {Fmt(focus.Aggregate(Vector3.Min))}..{Fmt(focus.Aggregate(Vector3.Max))}");
            }
            log?.Invoke($"thumbnail: eye {Fmt(pick.Eye)} -> {Fmt(pick.Target)}, {pick.Seen}/{pick.Focus} focus points seen, " +
                $"score {pick.Score:0.00}, {pick.Ms:0} ms");
            return pick;
        }

        // THUMB_DEBUG=1: every candidate's score terms (the best few are logged)
        static List<(float Score, Vector3 Eye, Vector3 Target, string Line)>? Debug;

        static string Fmt(Vector3 v) => FormattableString.Invariant($"({v.X:0.0}, {v.Y:0.0}, {v.Z:0.0})");

        // ---- the HD bundle ----

        sealed class Tex
        {
            public int W, H;
            public byte[] Px = Array.Empty<byte>();
        }

        sealed class Mat
        {
            public GxMaterial Gx = null!;
            public readonly Tex?[] Units = new Tex?[8];        // per texmap
            public readonly int[] Tc = new int[8];               // the texcoord each texmap reads
            public readonly int[] Mode = new int[8];             // per texcoord: 0 uv0, 1 the view-space normal, 2 uv1
            public readonly int[] WrapS = new int[8], WrapT = new int[8];
            public readonly float[][] Mtx = new float[8][];
        }

        sealed class Bundle
        {
            public float[] V = Array.Empty<float>();
            public int Floats;
            public readonly List<(int Mat, int Start, int Count, int Pass)> Batches = new();
            public readonly List<Mat> Mats = new();

            public static Bundle? Read(string dir, Action<string>? log)
            {
                string mesh = Path.Combine(dir, ArenaHdWriter.Mesh), json = Path.Combine(dir, ArenaHdWriter.Materials);
                if (!File.Exists(mesh) || !File.Exists(json)) return null;
                var b = new Bundle();
                List<GxMaterial> gx = GxJson.Read(json);
                var uv1 = new List<HashSet<int>>();
                var noMips = new HashSet<string>();
                using (JsonDocument doc = JsonDocument.Parse(File.ReadAllText(json)))
                {
                    foreach (JsonElement jm in doc.RootElement.GetProperty("materials").EnumerateArray())
                    {
                        var set = new HashSet<int>();
                        foreach (JsonElement jl in jm.GetProperty("layers").EnumerateArray())
                        {
                            if (jl.TryGetProperty("uv", out JsonElement u) && u.GetInt32() == 1) set.Add(jl.GetProperty("texcoord").GetInt32());
                        }
                        uv1.Add(set);
                    }
                    if (doc.RootElement.TryGetProperty("mips", out JsonElement mips))
                    {
                        foreach (JsonProperty t in mips.EnumerateObject()) if (t.Value.GetInt32() <= 1) noMips.Add(t.Name);
                    }
                }
                var textures = new Dictionary<string, Tex?>();
                Tex? Load(string? name)
                {
                    if (name == null) return null;
                    if (textures.TryGetValue(name, out Tex? t)) return t;
                    string path = Path.Combine(dir, name + ".png");
                    try
                    {
                        byte[] px = PngOut.Read(path, out int w, out int h);
                        // the picture is small: halve big textures (a mip level), except the lightmap atlases (single
                        // mip on the disc; halving would bleed their charts into each other)
                        while (!noMips.Contains(name) && w > 256 && h > 1 && w % 2 == 0 && h % 2 == 0) px = Half(px, ref w, ref h);
                        t = new Tex { W = w, H = h, Px = px };
                    }
                    catch (Exception ex)
                    {
                        log?.Invoke($"thumbnail: texture {name}: {ex.Message}");
                        t = null;
                    }
                    textures[name] = t;
                    return t;
                }
                for (int i = 0; i < gx.Count; i++)
                {
                    GxMaterial g = gx[i];
                    g.Reg[2] = Vector4.One; // TEV register 1 scales the lightmap: white, as CampaignArena sets it
                    var m = new Mat { Gx = g };
                    for (int tc = 0; tc < 8; tc++)
                    {
                        GxLayer? L = g.Layers[tc];
                        m.Mtx[tc] = L?.UvMtx ?? new float[] { 1, 0, 0, 0, 1, 0, 0, 0, 1 };
                        if (L == null) continue;
                        m.Mode[tc] = L.MapMode is 1 or 3 or 4 || (L.MapMode == 0 && g.TexGenSrc[tc] == 1) ? 1 : uv1[i].Contains(tc) ? 2 : 0;
                        if (L.TexMapId >= 0 && L.TexMapId < 8)
                        {
                            m.Units[L.TexMapId] = Load(L.Name ?? g.TexNames[L.TexMapId]);
                            m.Tc[L.TexMapId] = tc;
                            m.WrapS[L.TexMapId] = L.WrapS;
                            m.WrapT[L.TexMapId] = L.WrapT;
                        }
                    }
                    b.Mats.Add(m);
                }
                using var r = new BinaryReader(File.OpenRead(mesh));
                string magic = System.Text.Encoding.ASCII.GetString(r.ReadBytes(8));
                if (magic != "MPHARN1\0") return null;
                b.Floats = r.ReadInt32();
                int count = r.ReadInt32(), batches = r.ReadInt32();
                for (int i = 0; i < batches; i++) b.Batches.Add((r.ReadInt32(), r.ReadInt32(), r.ReadInt32(), r.ReadInt32()));
                b.V = new float[count * b.Floats];
                for (int i = 0; i < b.V.Length; i++) b.V[i] = r.ReadSingle();
                return b;
            }

            static byte[] Half(byte[] px, ref int w, ref int h)
            {
                int nw = w / 2, nh = h / 2;
                var o = new byte[nw * nh * 4];
                for (int y = 0; y < nh; y++)
                {
                    for (int x = 0; x < nw; x++)
                    {
                        for (int c = 0; c < 4; c++)
                        {
                            int s = px[((2 * y) * w + 2 * x) * 4 + c] + px[((2 * y) * w + 2 * x + 1) * 4 + c]
                                + px[((2 * y + 1) * w + 2 * x) * 4 + c] + px[((2 * y + 1) * w + 2 * x + 1) * 4 + c];
                            o[(y * nw + x) * 4 + c] = (byte)((s + 2) / 4);
                        }
                    }
                }
                w = nw; h = nh;
                return o;
            }

            public Vector3 Pos(int v) => new(V[v * Floats], V[v * Floats + 1], V[v * Floats + 2]);
            public Vector3 Nrm(int v) => new(V[v * Floats + 3], V[v * Floats + 4], V[v * Floats + 5]);
            public Vector2 Uv0(int v) => new(V[v * Floats + 6], V[v * Floats + 7]);
            public Vector2 Uv1(int v) => new(V[v * Floats + 8], V[v * Floats + 9]);
        }

        // ---- the camera ----

        static Result Search(Bundle b, IReadOnlyList<Vector3> focus, bool inside)
        {
            // the playable area: the focus points (spawns, pickups), else the opaque mesh's middle
            var pts = focus.Count > 0 ? focus.ToList() : OpaqueCorners(b);
            Vector3 c = Vector3.Zero;
            foreach (Vector3 p in pts) c += p;
            c /= pts.Count;
            float floor = pts.Min(p => p.Y);
            var target = new Vector3(c.X, floor + 0.35f * (c.Y - floor), c.Z);
            float reach = MathF.Max(5, pts.Max(p => new Vector2(p.X - c.X, p.Z - c.Z).Length()));
            var marks = pts.Select(p => p + new Vector3(0, 0.8f, 0)).ToList(); // chest height: a point on the floor hides in it
            var solid = new Solid(b);
            bool sky = b.Batches.Any(t => t.Pass == ArenaHdWriter.SkyPass);
            // candidates from inside the arena: over a few distinct focus points (the outermost first), raised and pulled
            // back from the middle, kept only when nothing solid lies between the point and the lens (no camera behind a
            // wall or over a roof); plus a ring above the middle for open arenas, kept when some focus point sees it
            var eyes = new List<Vector3>();
            var bases = new List<Vector3>();
            foreach (Vector3 m in marks.OrderByDescending(m => new Vector2(m.X - c.X, m.Z - c.Z).LengthSquared()))
            {
                if (bases.All(q => Vector3.DistanceSquared(q, m) > 9)) bases.Add(m);
            }
            foreach (Vector3 m in bases.Take(24))
            {
                var outward = new Vector3(m.X - c.X, 0, m.Z - c.Z);
                outward = outward.LengthSquared() > 1e-6f ? Vector3.Normalize(outward) : Vector3.UnitX;
                foreach (float up in new[] { 2.5f, 4f, 8f, 13f, 20f, 30f })
                {
                    foreach (float pull in new[] { 0f, 4f, 8f })
                    {
                        Vector3 eye = m + new Vector3(0, up, 0) + outward * pull;
                        if (solid.Clear(m, eye)) eyes.Add(eye);
                    }
                }
            }
            foreach (float radius in new[] { 0.5f, 0.8f, 1.1f })
            {
                foreach (float pitch in new[] { 30f, 42f, 55f })
                {
                    for (int yaw = 0; yaw < 12; yaw++)
                    {
                        float a = yaw * MathF.PI / 6, r = radius * reach;
                        var eye = target + new Vector3(MathF.Cos(a) * r, r * MathF.Tan(pitch * MathF.PI / 180), MathF.Sin(a) * r);
                        if (marks.Any(m => solid.Clear(m, eye))) eyes.Add(eye);
                    }
                }
            }
            // with no sky the void shows around the mesh: keep the lens inside the world's bounds (no cut-away views)
            if (!sky || inside)
            {
                List<Vector3> box = OpaqueCorners(b);
                Vector3 pad = (box[1] - box[0]) * 0.03f;
                eyes = eyes.Where(e => e.X > box[0].X + pad.X && e.Y > box[0].Y + pad.Y && e.Z > box[0].Z + pad.Z
                    && e.X < box[1].X - pad.X && e.Y < box[1].Y - pad.Y && e.Z < box[1].Z - pad.Z).ToList();
            }
            // aimed at the middle, or at the playable spot nearest it (the middle may be inside a pillar)
            Vector3 middle = target;
            var targets = new[] { middle, marks.OrderBy(m => Vector3.DistanceSquared(m, middle)).First() };
            var best = new Result { Eye = middle + new Vector3(0, reach, reach), Target = middle, Score = float.MinValue, Focus = marks.Count };
            var depth = new float[SearchW * SearchH];
            var back = new bool[SearchW * SearchH];
            void Try(Vector3 eye, Vector3 target, bool room = false)
            {
                if (new Vector2(eye.X - target.X, eye.Z - target.Z).LengthSquared() < 1) return; // straight down: no look-at
                Matrix4x4 view = Matrix4x4.CreateLookAt(eye, target, Vector3.UnitY);
                DepthOnly(b, view, SearchW, SearchH, depth, back);
                // near: right at the lens; blocking: something big in the middle of the frame, well in front of the arena's
                // middle (a pillar, a sphere; the floor before the lens is always nearer, so only the frame's centre counts)
                // open: how far the view reaches across the arena (long sightlines over a wall up close)
                int covered = 0, backs = 0, near = 0, blocking = 0;
                float dist = Vector3.Distance(eye, target), open = 0;
                for (int i = 0; i < depth.Length; i++)
                {
                    if (depth[i] <= 0) continue;
                    covered++;
                    open += MathF.Min(1, 1 / depth[i] / reach);
                    if (back[i]) backs++;
                    if (1 / depth[i] < 0.12f * dist) near++;
                    else if (1 / depth[i] < 0.5f * dist)
                    {
                        int x = i % SearchW, y = i / SearchW;
                        if (x > 0.3f * SearchW && x < 0.7f * SearchW && y > 0.25f * SearchH && y < 0.75f * SearchH) blocking++;
                    }
                }
                int seen = 0;
                foreach (Vector3 m in marks)
                {
                    Vector3 v = Vector3.Transform(m, view);
                    if (v.Z > -Near) continue;
                    (float sx, float sy) = Project(v, SearchW, SearchH);
                    if (sx < 0.04f * SearchW || sx > 0.96f * SearchW || sy < 0.04f * SearchH || sy > 0.96f * SearchH) continue;
                    float d = depth[(int)sy * SearchW + (int)sx];
                    if (d <= 0 || 1 / -v.Z >= d * 0.97f) seen++;
                }
                float n = depth.Length;
                // empty frame: the sky where the arena has one (fine, up to half the picture), else the void around the mesh
                float empty = 1 - covered / n;
                float bare = sky ? 0.5f * MathF.Max(0, empty - 0.5f) : 1.5f * MathF.Max(0, empty - 0.15f);
                // looking down into the arena (the stage pictures' angle): 20-45 degrees best, level views lose
                float pitch = MathF.Atan2(eye.Y - target.Y, new Vector2(eye.X - target.X, eye.Z - target.Z).Length()) * 180 / MathF.PI;
                // (framing a room: no bonus, a level view shows the room; owner 2026-10-07 picked that for Pipeline)
                float angle = pitch < 12 ? -0.05f : pitch is >= 20 and <= 45 && !room ? 0.05f : 0;
                float score = seen / (float)marks.Count - 2 * backs / n - 1.5f * near / n - 1.5f * blocking / (0.16f * n) - bare + angle
                    + 0.3f * open / n;
                Debug?.Add((score, eye, target, $"{Fmt(eye)} -> {Fmt(target)} seen {seen} open {open / n:0.00} near {near / n:0.00} block {blocking / (0.16f * n):0.00} back {backs / n:0.00} bare {bare:0.00} pitch {pitch:0}"));
                if (score > best.Score)
                {
                    best = new Result { Eye = eye, Target = target, Seen = seen, Focus = marks.Count, Score = score };
                }
            }
            foreach (Vector3 eye in eyes)
            {
                foreach (Vector3 aim in targets) Try(eye, aim);
            }
            // an arena of separate rooms (Pipeline: under a tenth of the spots seen from anywhere) has no one view of its
            // middle: then also aim each lens at the middle of the spots it sees, framing the room it's in (the other
            // arenas keep the pictures the owner approved, 2026-10-07)
            if (best.Seen < 0.1f * marks.Count)
            {
                foreach (Vector3 eye in eyes)
                {
                    var visible = marks.Where(m => solid.Clear(m, eye)).ToList();
                    if (visible.Count < 2) continue;
                    Vector3 vc = Vector3.Zero;
                    foreach (Vector3 m in visible) vc += m;
                    vc /= visible.Count;
                    float low = visible.Min(m => m.Y);
                    Try(eye, new Vector3(vc.X, low + 0.35f * (vc.Y - low), vc.Z), room: true);
                }
            }
            return best;
        }

        // the opaque world's triangles for line-of-sight tests (Moller-Trumbore, brute force: a few hundred segments)
        sealed class Solid
        {
            readonly Vector3[] _a, _e1, _e2;

            public Solid(Bundle b)
            {
                var a = new List<Vector3>();
                var e1 = new List<Vector3>();
                var e2 = new List<Vector3>();
                foreach (var bt in b.Batches)
                {
                    if (bt.Pass != 0 || b.Mats[bt.Mat].Gx.AlphaTest) continue;
                    for (int v = bt.Start; v + 2 < bt.Start + bt.Count; v += 3)
                    {
                        Vector3 p = b.Pos(v);
                        a.Add(p);
                        e1.Add(b.Pos(v + 1) - p);
                        e2.Add(b.Pos(v + 2) - p);
                    }
                }
                _a = a.ToArray();
                _e1 = e1.ToArray();
                _e2 = e2.ToArray();
            }

            // nothing solid on the segment from -> to
            public bool Clear(Vector3 from, Vector3 to)
            {
                Vector3 d = to - from;
                for (int i = 0; i < _a.Length; i++)
                {
                    Vector3 p = Vector3.Cross(d, _e2[i]);
                    float det = Vector3.Dot(_e1[i], p);
                    if (MathF.Abs(det) < 1e-9f) continue;
                    float inv = 1 / det;
                    Vector3 s = from - _a[i];
                    float u = Vector3.Dot(s, p) * inv;
                    if (u < 0 || u > 1) continue;
                    Vector3 q = Vector3.Cross(s, _e1[i]);
                    float v = Vector3.Dot(d, q) * inv;
                    if (v < 0 || u + v > 1) continue;
                    float t = Vector3.Dot(_e2[i], q) * inv;
                    if (t > 1e-4f && t < 1) return false;
                }
                return true;
            }
        }

        static List<Vector3> OpaqueCorners(Bundle b)
        {
            Vector3 min = new(float.MaxValue), max = new(float.MinValue);
            foreach (var bt in b.Batches.Where(t => t.Pass == 0))
            {
                for (int v = bt.Start; v < bt.Start + bt.Count; v++)
                {
                    min = Vector3.Min(min, b.Pos(v));
                    max = Vector3.Max(max, b.Pos(v));
                }
            }
            return new List<Vector3> { min, max };
        }

        static readonly float Focal = 1 / MathF.Tan(FovYDeg * MathF.PI / 360);

        static (float X, float Y) Project(Vector3 v, int w, int h)
        {
            float aspect = w / (float)h;
            float x = v.X * Focal / aspect / -v.Z, y = v.Y * Focal / -v.Z;
            return ((x * 0.5f + 0.5f) * w, (0.5f - y * 0.5f) * h);
        }

        // ---- rasterising ----

        // one clipped, projected corner: screen position, 1/distance, and its weights over the triangle's three corners
        struct Corner
        {
            public float X, Y, Iz;
            public Vector3 W;
        }

        // the triangle's view-space corners, clipped to the near plane and projected; returns the polygon's corner count
        static int Clip(Vector3 a, Vector3 b2, Vector3 c, int w, int h, Span<Corner> output)
        {
            Span<(Vector3 P, Vector3 W)> poly = stackalloc (Vector3, Vector3)[4];
            Span<(Vector3 P, Vector3 W)> src = stackalloc (Vector3, Vector3)[3] { (a, Vector3.UnitX), (b2, Vector3.UnitY), (c, Vector3.UnitZ) };
            int n = 0;
            for (int i = 0; i < 3; i++)
            {
                var p = src[i];
                var q = src[(i + 1) % 3];
                bool pin = p.P.Z <= -Near, qin = q.P.Z <= -Near;
                if (pin) poly[n++] = p;
                if (pin != qin)
                {
                    float t = (-Near - p.P.Z) / (q.P.Z - p.P.Z);
                    poly[n++] = (p.P + (q.P - p.P) * t, p.W + (q.W - p.W) * t);
                }
            }
            for (int i = 0; i < n; i++)
            {
                (float sx, float sy) = Project(poly[i].P, w, h);
                output[i] = new Corner { X = sx, Y = sy, Iz = 1 / -poly[i].P.Z, W = poly[i].W };
            }
            return n;
        }

        delegate void Pixel(int x, int y, float iz, Vector3 weights, bool backFacing);

        // cull: 0 none, 1 back faces (counter-clockwise on screen with y up = front, as the device's sky)
        static void Raster(Vector3 a, Vector3 b2, Vector3 c, int w, int h, int cull, Pixel pixel)
        {
            Span<Corner> poly = stackalloc Corner[4];
            int n = Clip(a, b2, c, w, h, poly);
            if (n < 3) return;
            // facing from the projected polygon: screen y runs down, so a front (counter-clockwise with y up) face has a
            // negative signed area here
            float area2 = 0;
            for (int i = 0; i < n; i++)
            {
                Corner p = poly[i], q = poly[(i + 1) % n];
                area2 += p.X * q.Y - q.X * p.Y;
            }
            bool backFacing = area2 > 0;
            if (cull == 1 && backFacing) return;
            for (int k = 1; k + 1 < n; k++)
            {
                Corner p0 = poly[0], p1 = poly[k], p2 = poly[k + 1];
                float area = (p1.X - p0.X) * (p2.Y - p0.Y) - (p2.X - p0.X) * (p1.Y - p0.Y);
                if (MathF.Abs(area) < 1e-9f) continue;
                int x0 = Math.Max(0, (int)MathF.Floor(MathF.Min(p0.X, MathF.Min(p1.X, p2.X))));
                int x1 = Math.Min(w - 1, (int)MathF.Ceiling(MathF.Max(p0.X, MathF.Max(p1.X, p2.X))));
                int y0 = Math.Max(0, (int)MathF.Floor(MathF.Min(p0.Y, MathF.Min(p1.Y, p2.Y))));
                int y1 = Math.Min(h - 1, (int)MathF.Ceiling(MathF.Max(p0.Y, MathF.Max(p1.Y, p2.Y))));
                for (int y = y0; y <= y1; y++)
                {
                    float py = y + 0.5f;
                    for (int x = x0; x <= x1; x++)
                    {
                        float px = x + 0.5f;
                        float l0 = ((p1.X - px) * (p2.Y - py) - (p2.X - px) * (p1.Y - py)) / area;
                        float l1 = ((p2.X - px) * (p0.Y - py) - (p0.X - px) * (p2.Y - py)) / area;
                        float l2 = 1 - l0 - l1;
                        if (l0 < 0 || l1 < 0 || l2 < 0) continue;
                        float iz = l0 * p0.Iz + l1 * p1.Iz + l2 * p2.Iz;
                        // perspective-correct weights over the original triangle's corners
                        Vector3 wts = (l0 * p0.Iz * p0.W + l1 * p1.Iz * p1.W + l2 * p2.Iz * p2.W) / iz;
                        pixel(x, y, iz, wts, backFacing);
                    }
                }
            }
        }

        static void DepthOnly(Bundle b, Matrix4x4 view, int w, int h, float[] depth, bool[] back)
        {
            Array.Clear(depth);
            Array.Clear(back);
            foreach (var bt in b.Batches)
            {
                if (bt.Pass != 0 || b.Mats[bt.Mat].Gx.AlphaTest) continue;
                for (int v = bt.Start; v + 2 < bt.Start + bt.Count; v += 3)
                {
                    Vector3 a = Vector3.Transform(b.Pos(v), view), b2 = Vector3.Transform(b.Pos(v + 1), view), c = Vector3.Transform(b.Pos(v + 2), view);
                    // facing from the stored normals (outward from the surface), not the winding
                    Vector3 nrm = b.Nrm(v) + b.Nrm(v + 1) + b.Nrm(v + 2);
                    Vector3 nv = Vector3.TransformNormal(nrm, view);
                    bool backs = Vector3.Dot(nv, (a + b2 + c) / 3) > 0;
                    Raster(a, b2, c, w, h, 0, (x, y, iz, _, _) =>
                    {
                        int i = y * w + x;
                        if (iz <= depth[i]) return;
                        depth[i] = iz;
                        back[i] = backs;
                    });
                }
            }
        }

        // ---- the picture ----

        static byte[] Render(Bundle b, Vector3 eye, Vector3 target, int width, int height)
        {
            int w = width * Super, h = height * Super;
            var color = new Vector4[w * h];
            var depth = new float[w * h];
            Matrix4x4 view = Matrix4x4.CreateLookAt(eye, target, Vector3.UnitY);
            Matrix4x4 skyView = view;
            skyView.Translation = Vector3.Zero; // the sky around the camera
            var rot = new Matrix4x4(view.M11, view.M12, view.M13, 0, view.M21, view.M22, view.M23, 0, view.M31, view.M32, view.M33, 0, 0, 0, 0, 1);
            // the device's order: the sky (behind everything, batch order, back faces culled), the opaque world, then the
            // blended and additive materials over it (depth tested; depth written as the material says)
            // THUMB_PLACED=1 (dev, T6 2026-10-10): the placed models' passes too (breakables, static actors), lit flat
            bool placed = Environment.GetEnvironmentVariable("THUMB_PLACED") == "1";
            IEnumerable<int> passes = new[] { ArenaHdWriter.SkyPass, 0 };
            if (placed) passes = passes.Concat(b.Batches.Select(t => t.Pass).Where(p => p >= ArenaHdWriter.FirstPlacedPass).Distinct().OrderBy(p => p));
            foreach (int pass in passes.Concat(new[] { 1, 2 }))
            {
                bool sky = pass == ArenaHdWriter.SkyPass;
                foreach (var bt in b.Batches)
                {
                    if (bt.Pass != pass) continue;
                    Mat m = b.Mats[bt.Mat];
                    GxMaterial g = m.Gx;
                    Matrix4x4 vm = sky ? skyView : view;
                    // channel 0: lit = the scene ambient (0 on the world, 1 on the sky; no lights), unlit = the material's white
                    var rasc = new Vector3(g.Lit ? (sky ? 1 : pass >= ArenaHdWriter.FirstPlacedPass ? 0.7f : 0) : 1);
                    for (int v = bt.Start; v + 2 < bt.Start + bt.Count; v += 3)
                    {
                        Vector3 a = Vector3.Transform(b.Pos(v), vm), b2 = Vector3.Transform(b.Pos(v + 1), vm), c = Vector3.Transform(b.Pos(v + 2), vm);
                        int v0 = v;
                        Raster(a, b2, c, w, h, 1, (x, y, iz, wt, _) =>
                        {
                            int i = y * w + x;
                            if (!sky && depth[i] > 0 && iz < depth[i] * 0.9999f) return;
                            Vector4 src = Shade(b, m, v0, wt, rot, rasc);
                            if (g.AlphaTest && src.W < 128 / 255f) return;
                            Vector4 dst = color[i];
                            Vector4 o = g.BlendEnable ? Blend(src, dst, g.BlendSrc, g.BlendDst) : src;
                            color[i] = Vector4.Clamp(o, Vector4.Zero, Vector4.One);
                            if (!sky && g.DepthWrite) depth[i] = MathF.Max(depth[i], iz);
                        });
                    }
                }
            }
            // down to the picture's size (a box over each Super x Super block), opaque
            var rgba = new byte[width * height * 4];
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    Vector4 sum = Vector4.Zero;
                    for (int sy = 0; sy < Super; sy++)
                    {
                        for (int sx = 0; sx < Super; sx++) sum += color[(y * Super + sy) * w + x * Super + sx];
                    }
                    sum /= Super * Super;
                    int o = (y * width + x) * 4;
                    rgba[o] = (byte)MathF.Round(sum.X * 255);
                    rgba[o + 1] = (byte)MathF.Round(sum.Y * 255);
                    rgba[o + 2] = (byte)MathF.Round(sum.Z * 255);
                    rgba[o + 3] = 255;
                }
            }
            return rgba;
        }

        static Vector4 Shade(Bundle b, Mat m, int v, Vector3 wt, Matrix4x4 rot, Vector3 rasc)
        {
            Vector2 uv0 = b.Uv0(v) * wt.X + b.Uv0(v + 1) * wt.Y + b.Uv0(v + 2) * wt.Z;
            Vector2 uv1 = b.Uv1(v) * wt.X + b.Uv1(v + 1) * wt.Y + b.Uv1(v + 2) * wt.Z;
            Vector3 n = b.Nrm(v) * wt.X + b.Nrm(v + 1) * wt.Y + b.Nrm(v + 2) * wt.Z;
            Vector3 ne = Vector3.TransformNormal(n, rot);
            if (ne.LengthSquared() > 1e-12f) ne = Vector3.Normalize(ne);
            var inputs = new GxTev.Inputs
            {
                Rasc = rasc,
                Rasa = 1,
                Tex = unit =>
                {
                    Tex? t = unit >= 0 && unit < 8 ? m.Units[unit] : null;
                    if (t == null) return Vector4.One;
                    int tc = m.Tc[unit];
                    Vector2 s = m.Mode[tc] == 1 ? new Vector2(0.5f + 0.5f * ne.X, 0.5f - 0.5f * ne.Y) : m.Mode[tc] == 2 ? uv1 : uv0;
                    float[] mt = m.Mtx[tc];
                    float u = mt[0] * s.X + mt[3] * s.Y + mt[6], vv = mt[1] * s.X + mt[4] * s.Y + mt[7];
                    return Sample(t, u, vv, m.WrapS[unit], m.WrapT[unit]);
                },
            };
            return GxTev.Evaluate(m.Gx, inputs);
        }

        static Vector4 Sample(Tex t, float u, float v, int wrapS, int wrapT)
        {
            float x = u * t.W - 0.5f, y = v * t.H - 0.5f;
            int x0 = (int)MathF.Floor(x), y0 = (int)MathF.Floor(y);
            float fx = x - x0, fy = y - y0;
            Vector4 Texel(int px, int py)
            {
                px = Wrap(px, t.W, wrapS);
                py = Wrap(py, t.H, wrapT);
                int o = (py * t.W + px) * 4;
                return new Vector4(t.Px[o], t.Px[o + 1], t.Px[o + 2], t.Px[o + 3]) / 255f;
            }
            return Vector4.Lerp(Vector4.Lerp(Texel(x0, y0), Texel(x0 + 1, y0), fx), Vector4.Lerp(Texel(x0, y0 + 1), Texel(x0 + 1, y0 + 1), fx), fy);
        }

        // wrap 0 clamp, 1 repeat, 2 mirror
        static int Wrap(int i, int n, int mode)
        {
            if (mode == 0) return Math.Clamp(i, 0, n - 1);
            if (mode == 2)
            {
                int p = ((i % (2 * n)) + 2 * n) % (2 * n);
                return p < n ? p : 2 * n - 1 - p;
            }
            return ((i % n) + n) % n;
        }

        // GX blend factors: source 0 zero, 1 one, 2 dst colour, 3 1 - dst colour, 4 src alpha, 5 1 - src alpha, 6 dst
        // alpha, 7 1 - dst alpha; the destination's 2/3 are the source colour's
        static Vector4 Blend(Vector4 src, Vector4 dst, int sf, int df)
        {
            Vector4 Factor(int f, bool source) => f switch
            {
                0 => Vector4.Zero,
                1 => Vector4.One,
                2 => source ? dst : src,
                3 => Vector4.One - (source ? dst : src),
                4 => new Vector4(src.W),
                5 => new Vector4(1 - src.W),
                6 => new Vector4(dst.W),
                _ => new Vector4(1 - dst.W),
            };
            return src * Factor(sf, true) + dst * Factor(df, false);
        }
    }
}
