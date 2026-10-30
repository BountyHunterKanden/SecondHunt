using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MphRead.Entities;
using MphRead.Formats;
using MphRecomp.Campaign;
using OpenTK.Mathematics;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace MphRead
{
    // First-person arm cannons cut from the native bodies (Core Campaign/BodyArmCannon.cs; owner 2026-10-08):
    //   -armcannontest gunnodes                 MPH's own first-person guns (SamusGun, WeavelGun): their nodes and how
    //                                           each gun state moves them (the motion the cut cannons follow)
    //   -armcannontest cut data=<root> [out=<dir>] [body=<Id>]
    //                                           each body's cut (bodies/<Id> under the data root): its triangles, its
    //                                           frame, and a software-rendered sheet per body (no GL; flat-shaded): the
    //                                           whole body in the cut pose with the cut red, then the cut alone in
    //                                           cannon space from its side, from above, and from behind the muzzle
    internal static class ArmCannonTest
    {
        public static void Run(string[] args)
        {
            string mode = args.Length >= 2 ? args[1] : "cut";
            var opt = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string a in args.Skip(2))
            {
                int eq = a.IndexOf('=');
                if (eq > 0) opt[a[..eq]] = a[(eq + 1)..];
            }
            string Opt(string k, string d) => opt.TryGetValue(k, out string? v) ? v : d;
            if (mode == "gunnodes")
            {
                if (!ToolPaths.UseDesktopExtraction()) return;
                GunNodes(Hunter.Samus);
                GunNodes(Hunter.Weavel);
                return;
            }
            if (mode == "cut")
            {
                string root = Opt("data", "");
                string outDir = Path.GetFullPath(Opt("out", Path.Combine(Path.GetTempPath(), "mph-armcannon")));
                Directory.CreateDirectory(outDir);
                string only = Opt("body", "");
                foreach (string dir in Directory.GetDirectories(Path.Combine(root, "bodies")).OrderBy(d => d))
                {
                    string id = Path.GetFileName(dir);
                    if (only.Length > 0 && !id.Equals(only, StringComparison.OrdinalIgnoreCase)) continue;
                    if (!NativeBody.Exists(dir)) continue;
                    NativeBody body = NativeBody.Load(dir);
                    BodyArmCannon? c = BodyArmCannon.For(body);
                    if (c == null)
                    {
                        Console.WriteLine($"  {id}: no arm cannon");
                        continue;
                    }
                    Console.WriteLine($"  {id}: {c.Describe()}");
                    // the bones under the elbow in cannon space at first-person size (world units: X left, Y up, Z forward)
                    c.Pose();
                    float s = body.Scale * c.Size;
                    for (int i = 0; i < body.Data.BoneCount; i++)
                    {
                        int p = i;
                        while (p >= 0 && p != c.Elbow) p = body.Data.Parent[p];
                        if (p < 0) continue;
                        Vector3 at = Vector3.TransformPosition(body.World[i].ExtractTranslation(), c.ToCannon) * s;
                        Console.WriteLine($"    {body.Data.Names[i],-18} {at.X,7:0.000} {at.Y,7:0.000} {at.Z,7:0.000}");
                    }
                    // the cut's cross-section along the barrel: X and Y extents per 0.04 slice
                    var pal = new float[32 * 16];
                    var bands = new SortedDictionary<int, (float X0, float X1, float Y0, float Y1)>();
                    for (int m = 0; m < body.Data.Materials.Length; m++)
                    {
                        if (c.Cut[m].Length == 0) continue;
                        List<(Vector3 P, Vector3 N)> v = Skin(body, m, pal);
                        foreach (int k in c.Cut[m])
                        {
                            Vector3 q = Vector3.TransformPosition(v[k].P, c.ToCannon) * s;
                            int band = (int)MathF.Floor(q.Z / 0.04f);
                            var e = bands.TryGetValue(band, out var b0) ? b0 : (9f, -9f, 9f, -9f);
                            bands[band] = (MathF.Min(e.Item1, q.X), MathF.Max(e.Item2, q.X), MathF.Min(e.Item3, q.Y), MathF.Max(e.Item4, q.Y));
                        }
                    }
                    foreach (var (band, e) in bands)
                    {
                        Console.WriteLine($"    z {band * 0.04f,6:0.00}..{band * 0.04f + 0.04f,5:0.00}  x {e.X0,6:0.000}..{e.X1,6:0.000}  y {e.Y0,6:0.000}..{e.Y1,6:0.000}");
                    }
                    Sheet(c, Path.Combine(outDir, id + "_cut.png"), Int32.Parse(Opt("size", "300")));
                }
                return;
            }
            if (mode == "reach")
            {
                Reach(Path.Combine(Opt("data", ""), "bodies", Opt("body", "MP1IceTrooper")));
                return;
            }
            Console.WriteLine($"  unknown mode {mode}");
        }

        // -------- the Trooper's upper body vs where MPH's shots start --------

        // -armcannontest reach data=<root> [body=<Id>]: the Trooper's shooting pose (B_ready legs, A_gunloop torso, the
        // arm hold) in the player's frame (left, up, forward of p.Position, MPH units) against the sim's muzzle, and what
        // the body's own aim additives hold
        static void Reach(string dir)
        {
            NativeBody body = NativeBody.Load(dir);
            BodyClips map = BodyClips.For(body) ?? throw new InvalidOperationException("no clip map");
            Matrix4 model = body.ModelMatrix(Hunter.Weavel, Vector3.Zero, Vector3.UnitZ, map.Lift);
            string[] bones = { "Skeleton_Root", "Spine_1", "Spine_2", "Neck_1", "Head_1", "R_collar", "R_shoulder", "R_elbow",
                "R_wrist", "R_gun_LCTR", "L_collar", "L_shoulder" };
            Vector3 At(string bone) => Vector3.TransformPosition(body.World[body.Bone(bone)].ExtractTranslation(), model);
            void Dump(string label)
            {
                Console.WriteLine($"  {label}");
                foreach (string b in bones)
                {
                    if (body.Bone(b) < 0) continue;
                    Vector3 p = At(b);
                    Console.WriteLine(FormattableString.Invariant($"    {b,-14} left {p.X,6:0.000} up {p.Y,6:0.000} fwd {p.Z,6:0.000}"));
                }
                Vector3 tip = At("R_gun_LCTR"), elbow = At("R_elbow"), shoulder = At("R_shoulder");
                Vector3 bar = (tip - elbow).Normalized();
                Console.WriteLine(FormattableString.Invariant(
                    $"    upper arm {(elbow - shoulder).Length:0.000}  forearm->tip {(tip - elbow).Length:0.000}  barrel ({bar.X:0.00} {bar.Y:0.00} {bar.Z:0.00})"));
            }
            HdGun.Anim legs = body.Clip("B_ready_pirate")!, torso = body.Clip("A_gunloop_pirate")!;
            int root = body.Bone("Spine_1");
            DarkSamusBody.Hold? hold = DarkSamusBody.Hold.For(body, "A_gunloop_pirate", 0, "R_gun_LCTR");
            body.Pose(legs, 0);
            body.PoseLayer(root, torso, 0, null, 0, 1, 1);
            Dump("ready + gunloop t0 (no hold)");
            hold?.Apply(body, 0f);
            Dump("+ arm hold, level aim");
            Console.WriteLine("  sim muzzle (measured in game, level aim): left -0.100 up 0.670 fwd 0.390");
            Console.WriteLine("  clips with 'aim' or 'additive':");
            foreach ((string name, HdGun.Anim a) in body.Data.Anims.OrderBy(k => k.Key))
            {
                if (!name.Contains("aim", StringComparison.OrdinalIgnoreCase) && !name.Contains("additive", StringComparison.OrdinalIgnoreCase)) continue;
                var turns = new List<string>();
                foreach (string b in new[] { "Spine_1", "Spine_2", "R_collar", "R_shoulder", "R_elbow" })
                {
                    int i = body.Bone(b);
                    if (i < 0) continue;
                    body.Data.Sample(a, 0, i, out Quaternion r0, out _);
                    body.Data.Sample(a, a.Duration, i, out Quaternion r1, out _);
                    body.Data.Sample(legs, 0, i, out Quaternion rb, out _);
                    float deg0 = MathHelper.RadiansToDegrees(2 * MathF.Acos(Math.Min(1, MathF.Abs(r0.W))));
                    float deg1 = MathHelper.RadiansToDegrees(2 * MathF.Acos(Math.Min(1, MathF.Abs(r1.W))));
                    float vsReady = MathHelper.RadiansToDegrees(2 * MathF.Acos(Math.Min(1, MathF.Abs(r1.X * rb.X + r1.Y * rb.Y + r1.Z * rb.Z + r1.W * rb.W))));
                    turns.Add(FormattableString.Invariant($"{b} {deg0:0}/{deg1:0} ({vsReady:0} vs ready)"));
                }
                Console.WriteLine(FormattableString.Invariant($"    {name,-40} {a.Duration:0.00}s  rot t0/end: {string.Join(", ", turns)}"));
            }
        }

        // -------- MPH's guns --------

        static void GunNodes(Hunter hunter)
        {
            string name = Metadata.HunterModels[hunter][3];
            ModelInstance inst = Read.GetModelInstance(name);
            Model model = inst.Model;
            PlayerValues values = Metadata.PlayerValues[(int)hunter];
            Console.WriteLine($"{name}: {model.Nodes.Count} nodes, scale {model.Scale}; gun pos B0 {Fixed.ToFloat(values.FieldB0):0.000} "
                + $"B4 {Fixed.ToFloat(values.FieldB4):0.000} B8 {Fixed.ToFloat(values.FieldB8):0.000}, muzzle {Fixed.ToFloat(values.MuzzleOffset):0.000}");
            for (int i = 0; i < model.Nodes.Count; i++)
            {
                Node n = model.Nodes[i];
                Console.WriteLine($"  node {i} {n.Name,-16} parent {n.ParentIndex,2} meshes {n.MeshCount} bounds {n.MinBounds} .. {n.MaxBounds}");
            }
            Matrix4[]? rest = null;
            foreach (GunAnimation a in Enum.GetValues<GunAnimation>())
            {
                int id = Metadata.GunAnimationIds[(int)hunter, (int)a, 0];
                if (id < 0)
                {
                    Console.WriteLine($"  {a}: none");
                    continue;
                }
                inst.SetAnimation(id, slot: 0, SetFlags.Node);
                int count = inst.AnimInfo.FrameCount[0];
                var line = new List<string>();
                foreach (int f in new[] { 0, count / 4, count / 2, 3 * count / 4, Math.Max(0, count - 1) }.Distinct())
                {
                    inst.AnimInfo.Frame[0] = f;
                    model.ComputeNodeMatrices(0);
                    model.AnimateNodes(0, false, Matrix4.Identity, model.Scale, inst.AnimInfo);
                    Matrix4[] now = model.Nodes.Select(n => n.Animation).ToArray();
                    rest ??= now;
                    var moved = new List<string>();
                    for (int i = 0; i < now.Length; i++)
                    {
                        Matrix4 d = rest[i].Inverted() * now[i];
                        Vector3 t = d.ExtractTranslation();
                        float ang = MathHelper.RadiansToDegrees(2 * MathF.Acos(Math.Clamp(MathF.Abs(d.ExtractRotation().W), 0, 1)));
                        if (t.Length > 0.002f || ang > 0.3f) moved.Add($"{i}:({t.X:0.000},{t.Y:0.000},{t.Z:0.000}) {ang:0.0}deg");
                    }
                    line.Add($"f{f}: {(moved.Count == 0 ? "rest" : string.Join(" ", moved))}");
                }
                Console.WriteLine($"  {a} (anim {id}, {count} frames)\n      {string.Join("\n      ", line)}");
            }
        }

        // -------- the cut, drawn in software --------

        static void Sheet(BodyArmCannon c, string png, int size)
        {
            NativeBody b = c.Body;
            var pal = new float[32 * 16];
            c.Pose();
            // every vertex of the body in the cut pose, per material, and which ones the cut keeps
            var all = new List<(Vector3 P, Vector3 N, bool Cut)>();
            for (int m = 0; m < b.Data.Materials.Length; m++)
            {
                var keep = new HashSet<int>(c.Cut[m]);
                List<(Vector3 P, Vector3 N)> v = Skin(b, m, pal);
                // triangles in body order (3 vertices each)
                for (int i = 0; i + 2 < v.Count; i += 3)
                {
                    bool cut = keep.Contains(i) && keep.Contains(i + 1) && keep.Contains(i + 2);
                    all.Add((v[i].P, v[i].N, cut));
                    all.Add((v[i + 1].P, v[i + 1].N, cut));
                    all.Add((v[i + 2].P, v[i + 2].N, cut));
                }
            }
            // columns: the body from its front-right, the cut in cannon space from its left side (+X), from above, from
            // behind the muzzle (as first person sees it: a little above and right of the barrel's back end)
            using var img = new Image<Rgba32>(size * 4, size, new Rgba32(40, 44, 52));
            var light = Vector3.Normalize(new Vector3(0.4f, 0.6f, 1f));
            for (int col = 0; col < 4; col++)
            {
                var zbuf = new float[size * size];
                Array.Fill(zbuf, Single.MinValue);
                Func<Vector3, Vector3> view;
                float span, cy;
                if (col == 0)
                {
                    float yaw = MathHelper.DegreesToRadians(-35), c0 = MathF.Cos(yaw), s0 = MathF.Sin(yaw);
                    view = p => new Vector3(p.X * c0 - p.Z * s0, p.Y, p.X * s0 + p.Z * c0);
                    span = b.Height * 1.3f;
                    cy = b.Height / 2;
                }
                else
                {
                    Matrix4 to = c.ToCannon;
                    float len = c.Length;
                    span = len * 1.4f;
                    cy = 0;
                    if (col == 1) view = p => { Vector3 q = Vector3.TransformPosition(p, to); return new Vector3(-q.Z - len / 2, q.Y, q.X); };
                    else if (col == 2) view = p => { Vector3 q = Vector3.TransformPosition(p, to); return new Vector3(-q.Z - len / 2, -q.X, q.Y); };
                    else view = p =>
                    {
                        // from behind (looking down +Z): screen x = -X (gun space +X = the player's left), y = Y; tilted 15 deg down
                        Vector3 q = Vector3.TransformPosition(p, to);
                        float a = MathHelper.DegreesToRadians(15), ca = MathF.Cos(a), sa = MathF.Sin(a);
                        float y = q.Y * ca - (q.Z + len / 2) * sa, z = q.Y * sa + (q.Z + len / 2) * ca;
                        return new Vector3(-q.X, y, -z);
                    };
                }
                (float X, float Y) Screen(Vector3 v) => (size / 2f + v.X / span * size, size / 2f - (v.Y - cy) / span * size);
                for (int i = 0; i + 2 < all.Count; i += 3)
                {
                    if (col > 0 && !all[i].Cut) continue;
                    Vector3 a0 = view(all[i].P), a1 = view(all[i + 1].P), a2 = view(all[i + 2].P);
                    Vector3 n = Vector3.Cross(a1 - a0, a2 - a0);
                    if (n.LengthSquared < 1e-12f) continue;
                    n.Normalize();
                    float shade = 0.3f + 0.7f * MathF.Abs(Vector3.Dot(n, light));
                    Rgba32 color = all[i].Cut ? new Rgba32((byte)(230 * shade), (byte)(80 * shade), (byte)(60 * shade))
                        : new Rgba32((byte)(150 * shade), (byte)(160 * shade), (byte)(185 * shade));
                    Tri(img, zbuf, size, col * size, Screen(a0), a0.Z, Screen(a1), a1.Z, Screen(a2), a2.Z, color);
                }
                if (col > 0)
                {
                    // the muzzle (cannon space 0) as a green dot
                    Vector3 mz = view(Vector3.TransformPosition(Vector3.Zero, c.ToCannon.Inverted()));
                    (float X, float Y) s = Screen(mz);
                    for (int dy = -2; dy <= 2; dy++)
                        for (int dx = -2; dx <= 2; dx++)
                        {
                            int x = (int)s.X + dx, y = (int)s.Y + dy;
                            if (x >= 0 && x < size && y >= 0 && y < size) img[col * size + x, y] = new Rgba32(90, 230, 110);
                        }
                }
            }
            img.SaveAsPng(png);
            Console.WriteLine($"    sheet {png}");
        }

        // the posed mesh's skinned vertices (model space) for one material, in body order
        static List<(Vector3 P, Vector3 N)> Skin(NativeBody b, int m, float[] pal)
        {
            HdGun.Material mat = b.Data.Materials[m];
            b.FillPalette(m, pal);
            var dst = new List<(Vector3, Vector3)>(mat.VertexCount);
            for (int v = 0; v < mat.VertexCount; v++)
            {
                int o = v * HdGun.FloatsPerVertex;
                var p = new Vector4(mat.Vertices[o], mat.Vertices[o + 1], mat.Vertices[o + 2], 1);
                var n = new Vector4(mat.Vertices[o + 3], mat.Vertices[o + 4], mat.Vertices[o + 5], 0);
                Vector4 ap = Vector4.Zero, an = Vector4.Zero;
                for (int k = 0; k < 4; k++)
                {
                    float w = mat.Vertices[o + 15 + k];
                    if (w <= 0) continue;
                    int bi = (int)mat.Vertices[o + 11 + k] * 16;
                    var M = new Matrix4(pal[bi], pal[bi + 1], pal[bi + 2], pal[bi + 3], pal[bi + 4], pal[bi + 5], pal[bi + 6], pal[bi + 7],
                        pal[bi + 8], pal[bi + 9], pal[bi + 10], pal[bi + 11], pal[bi + 12], pal[bi + 13], pal[bi + 14], pal[bi + 15]);
                    ap += w * (p * M);
                    an += w * (n * M);
                }
                dst.Add((ap.Xyz, an.Xyz.LengthSquared > 0 ? an.Xyz.Normalized() : Vector3.UnitY));
            }
            return dst;
        }

        static void Tri(Image<Rgba32> img, float[] zbuf, int size, int ox, (float X, float Y) p0, float z0, (float X, float Y) p1, float z1,
            (float X, float Y) p2, float z2, Rgba32 col)
        {
            int minX = Math.Max(0, (int)MathF.Floor(Math.Min(p0.X, Math.Min(p1.X, p2.X))));
            int maxX = Math.Min(size - 1, (int)MathF.Ceiling(Math.Max(p0.X, Math.Max(p1.X, p2.X))));
            int minY = Math.Max(0, (int)MathF.Floor(Math.Min(p0.Y, Math.Min(p1.Y, p2.Y))));
            int maxY = Math.Min(size - 1, (int)MathF.Ceiling(Math.Max(p0.Y, Math.Max(p1.Y, p2.Y))));
            float area = (p1.X - p0.X) * (p2.Y - p0.Y) - (p2.X - p0.X) * (p1.Y - p0.Y);
            if (MathF.Abs(area) < 1e-6f) return;
            for (int y = minY; y <= maxY; y++)
            {
                for (int x = minX; x <= maxX; x++)
                {
                    float px = x + 0.5f, py = y + 0.5f;
                    float w0 = ((p1.X - px) * (p2.Y - py) - (p2.X - px) * (p1.Y - py)) / area;
                    float w1 = ((p2.X - px) * (p0.Y - py) - (p0.X - px) * (p2.Y - py)) / area;
                    float w2 = 1 - w0 - w1;
                    if (w0 < 0 || w1 < 0 || w2 < 0) continue;
                    float z = w0 * z0 + w1 * z1 + w2 * z2;
                    int i = y * size + x;
                    if (z <= zbuf[i]) continue;
                    zbuf[i] = z;
                    img[ox + x, y] = col;
                }
            }
        }
    }
}
