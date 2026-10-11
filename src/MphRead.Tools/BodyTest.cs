using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MphRead.Entities;
using MphRecomp.Campaign;
using MphRecomp.Import;
using OpenTK.Mathematics;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace MphRead
{
    // -bodytest [Name|all] [--mp1 disc.iso] [--mp2 disc] [--mp3 folder] [--out dir] [--copyto <data root>] [-v]
    // The native Retro bodies (Import/Bodies/BodyImporter, Core Campaign/NativeBody; docs/CHARACTERS_AND_SKINS.md D22):
    // runs the in-app importer on the owner's own discs for the "bodies" recipes, loads each folder the way the app does
    // (NativeBody.Load), and checks it: every material within GxShader's 32 bones, the character's clip map
    // (BodyClips) all present, and every mapped clip posed at 8 times on the floor (its lowest skinned point within 15%
    // of the body's height of the bind feet at its lowest, 25% at its highest: a run's flight phase) and about as tall as
    // the bind pose (40%..130%) -- the root motion out.
    // --copyto: the bodies/ folders are copied into that data root (e.g. the desktop app's --data folder) after a PASS.
    internal static class BodyTest
    {
        sealed class Quiet : IProgress<(string item, float fraction, string message)>
        {
            public bool Verbose;
            public void Report((string item, float fraction, string message) v)
            {
                if (Verbose) Console.WriteLine($"      {v.item}: {v.message}");
            }
        }

        public static void Run(string[] args)
        {
            string repo = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
            string Arg(string k, string d) { int i = Array.IndexOf(args, k); return i > 0 && i + 1 < args.Length ? args[i + 1] : d; }
            string which = args.Length >= 2 && !args[1].StartsWith("-") ? args[1] : "all";
            string outRoot = Path.GetFullPath(Arg("--out", Path.Combine(Path.GetTempPath(), "mph-bodytest")));
            string copyTo = Arg("--copyto", "");
            if (Arg("--raw", "") is string raw && raw.Length > 0)
            {
                Raw(Arg("--mp1", Path.Combine(repo, "brawl_extract", "prime", "mp1.iso")), raw);
                return;
            }
            if (Arg("--bones", "") is string bonesArg && bonesArg.Length > 0)
            {
                Bones(Path.Combine(outRoot, "bodies", which == "all" ? "MP1WaveTrooper" : which), bonesArg, Arg("--render", ""));
                return;
            }
            var discs = new Dictionary<string, string>
            {
                ["mp1"] = Arg("--mp1", Path.Combine(repo, "brawl_extract", "prime", "mp1.iso")),
                ["mp2"] = Arg("--mp2", Path.Combine(repo, "brawl_extract", "prime", "echoes_retail.rvz")),
                ["mp3"] = Arg("--mp3", Path.Combine(repo, "brawl_extract", "prime", "trilogy", "DATA", "files")),
            };
            RecipeStore store = RecipeStore.FromDirectory(Path.Combine(repo, "recipes"));
            List<ImportRecipe> recipes = store.All.Where(r => r.Kind == "bodies" && (which == "all" || r.Name.Equals(which, StringComparison.OrdinalIgnoreCase))).ToList();
            Console.WriteLine($"  bodytest {which}: {recipes.Count} recipes; output {outRoot}");
            var svc = new ImportService(outRoot, store);
            int pass = 0, total = 0;
            foreach (IGrouping<string, ImportRecipe> game in recipes.GroupBy(r => r.Game))
            {
                string disc = discs.TryGetValue(game.Key, out string? d) ? d : "";
                if (!File.Exists(disc) && !Directory.Exists(disc))
                {
                    Console.WriteLine($"  [SKIP] {game.Key}: no disc at '{disc}'");
                    continue;
                }
                ImportReport rep = svc.Import(game.Key, disc, new Quiet { Verbose = args.Contains("-v") }, default, game.Select(r => r.Id).ToList());
                foreach (ImportItemInfo it in rep.Items)
                {
                    Console.WriteLine($"    {it.Id,-24} {it.Status,-10} {it.Seconds,6:F2} s  {it.Message}");
                }
                foreach (ImportRecipe r in game)
                {
                    total++;
                    if (Check(Path.Combine(outRoot, "bodies", r.Name), r.Name))
                    {
                        pass++;
                        if (copyTo.Length > 0) CopyDir(Path.Combine(outRoot, "bodies", r.Name), Path.Combine(copyTo, "bodies", r.Name));
                    }
                }
            }
            Console.WriteLine($"  SUMMARY: {pass}/{total} bodies PASS" + (copyTo.Length > 0 ? $" (passing ones copied to {Path.Combine(copyTo, "bodies")})" : ""));
        }

        static bool Check(string dir, string name)
        {
            if (!NativeBody.Exists(dir))
            {
                Console.WriteLine($"  [FAIL] {name}: no body.bin");
                return false;
            }
            NativeBody b = NativeBody.Load(dir);
            HdGun g = b.Data;
            bool ok = true;
            Console.WriteLine($"  {name}: {g.BoneCount} bones, {g.Materials.Length} materials (palettes {String.Join(",", g.Materials.Select(m => m.Palette.Length))}), "
                + $"{g.Anims.Count} clips, height {b.Height:F2} feet {b.Feet:F2} (scale {b.Scale:F3}), game {b.Game}");
            if (g.Materials.Any(m => m.Palette.Length > 32))
            {
                Console.WriteLine("    FAIL: a material over 32 bones");
                ok = false;
            }
            BodyClips? map = BodyClips.For(b);
            if (map == null)
            {
                Console.WriteLine("    FAIL: no clip map for this body's clips");
                return false;
            }
            if (DroneBody.Is(b))
            {
                // the Scout Drone floats: every clip DroneBody.Animator plays is there and stays a drone's size (no stray
                // bone flung off), instead of the bipeds' feet-on-the-floor check
                foreach (string c in DroneBody.Clips)
                {
                    HdGun.Anim? an = b.Clip(c);
                    if (an == null)
                    {
                        Console.WriteLine($"    FAIL: clip {c} missing");
                        ok = false;
                        continue;
                    }
                    float span = 0;
                    for (int s = 0; s < 8; s++)
                    {
                        b.Pose(an, an.Duration * s / 7f);
                        (float lo, float hi) = Bounds(b);
                        span = Math.Max(span, hi - lo);
                    }
                    bool sane = span > 0.3f * b.Height && span < 2.5f * b.Height && !Single.IsNaN(span);
                    Console.WriteLine($"    {(sane ? "ok  " : "FAIL")} {c,-26} {an.Duration,5:F2} s  height {span / b.Height:P0}");
                    ok &= sane;
                }
                Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {name}");
                return ok;
            }
            var clips = new SortedSet<string>(StringComparer.Ordinal) { map.Idle, map.WallIn, map.WallLoop, map.WallOut };
            foreach (PlayerAnimation a in Enum.GetValues<PlayerAnimation>())
            {
                if (a is PlayerAnimation.None or PlayerAnimation.Unused17) continue;
                clips.Add(map.ClipFor(a));
                if (map.TorsoFor(a, out (string Clip, bool Loop) te)) clips.Add(te.Clip);
            }
            clips.Remove("");   // a map with no wall clips
            foreach (string c in clips)
            {
                HdGun.Anim? an = b.Clip(c);
                if (an == null)
                {
                    Console.WriteLine($"    FAIL: clip {c} missing");
                    ok = false;
                    continue;
                }
                float lowest = Single.MaxValue, highest = Single.MinValue, top = Single.MinValue, bottom = Single.MaxValue;
                for (int s = 0; s < 8; s++)
                {
                    b.Pose(an, an.Duration * s / 7f);
                    (float lo, float hi) = Bounds(b);
                    lowest = Math.Min(lowest, lo);
                    highest = Math.Max(highest, lo);
                    top = Math.Max(top, hi - lo);
                    bottom = Math.Min(bottom, hi - lo);
                }
                bool floor = Math.Abs(lowest - b.Feet) < 0.15f * b.Height && Math.Abs(highest - b.Feet) < 0.25f * b.Height;
                bool tall = bottom > 0.4f * b.Height && top < 1.3f * b.Height;
                b.Speeds.TryGetValue(c, out float v);
                Console.WriteLine($"    {(floor && tall ? "ok  " : "FAIL")} {c,-26} {an.Duration,5:F2} s  feet {lowest - b.Feet,6:F2}..{highest - b.Feet,5:F2}  "
                    + $"height {bottom / b.Height:P0}..{top / b.Height:P0}" + (v > 0 ? $"  speed {v:F2}/s" : ""));
                ok &= floor && tall;
            }
            Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {name}");
            return ok;
        }

        // the posed mesh's lowest and highest point (skinned on the CPU as GxShader does)
        static (float Lo, float Hi) Bounds(NativeBody b)
        {
            HdGun g = b.Data;
            float lo = Single.MaxValue, hi = Single.MinValue;
            var pal = new float[32 * 16];
            for (int m = 0; m < g.Materials.Length; m++)
            {
                HdGun.Material mat = g.Materials[m];
                b.FillPalette(m, pal);
                for (int v = 0; v < mat.VertexCount; v += 3)
                {
                    int o = v * HdGun.FloatsPerVertex;
                    var p = new Vector4(mat.Vertices[o], mat.Vertices[o + 1], mat.Vertices[o + 2], 1);
                    Vector4 acc = Vector4.Zero;
                    for (int k = 0; k < 4; k++)
                    {
                        float w = mat.Vertices[o + 15 + k];
                        if (w <= 0) continue;
                        int bi = (int)mat.Vertices[o + 11 + k] * 16;
                        var M = new Matrix4(pal[bi], pal[bi + 1], pal[bi + 2], pal[bi + 3], pal[bi + 4], pal[bi + 5], pal[bi + 6], pal[bi + 7],
                            pal[bi + 8], pal[bi + 9], pal[bi + 10], pal[bi + 11], pal[bi + 12], pal[bi + 13], pal[bi + 14], pal[bi + 15]);
                        acc += w * (p * M);
                    }
                    lo = Math.Min(lo, acc.Y);
                    hi = Math.Max(hi, acc.Y);
                }
            }
            return (lo, hi);
        }

        // --bones <clip,...>: an imported body's bone positions in model space (+Z = facing, Y up, feet at body.cfg feet)
        // at the start, middle and end of each clip, plus the posed mesh's extent along Z (where a wall would touch it)
        // --render <png>: each spec at t = 0 / mid / end, front + side, flat-shaded (a look at the layered poses, no GL)
        static void Bones(string dir, string clipsArg, string render = "")
        {
            NativeBody b = NativeBody.Load(dir);
            var tiles = new List<(string Label, Rgba32[] Px)>();
            HdGun g = b.Data;
            BodyClips? map = BodyClips.For(b);
            int torsoRoot = map != null && map.TorsoRoot.Length > 0 ? b.Bone(map.TorsoRoot) : -1;
            foreach (string spec in clipsArg.Split(','))
            {
                // legs+torso: the torso clip layered over the legs clip from the map's torso root, as NativeBodyAnimator does;
                // @yaw:pitch[:wall]: then the aim twist toward that aim (degrees from the upper body's forward; wall: a wall clip,
                // forward = out of the wall)
                string[] at = spec.Split('@');
                string[] parts = at[0].Split('+');
                string clip = parts[0];
                HdGun.Anim? an = b.Clip(clip);
                HdGun.Anim? torso = parts.Length > 1 ? b.Clip(parts[1]) : null;
                if (an == null || parts.Length > 1 && (torso == null || torsoRoot < 0))
                {
                    Console.WriteLine($"  {spec}: missing (or no torso root)");
                    continue;
                }
                foreach (float f in new[] { 0f, 0.5f, 1f })
                {
                    b.Pose(an, an.Duration * f);
                    if (torso != null) b.PoseLayer(torsoRoot, torso, torso.Duration * f, null, 0, 1, 1);
                    if (at.Length > 1 && map != null && map.AimBone.Length > 0)
                    {
                        string[] a = at[1].Split(':');
                        bool wall = a.Length > 2 && a[2] == "wall";
                        float h = (wall ? MathF.PI : 0) + MathHelper.DegreesToRadians(Single.Parse(a[0], System.Globalization.CultureInfo.InvariantCulture));
                        float pt = MathHelper.DegreesToRadians(Single.Parse(a[1], System.Globalization.CultureInfo.InvariantCulture));
                        var d = new Vector3(MathF.Sin(h) * MathF.Cos(pt), MathF.Sin(pt), MathF.Cos(h) * MathF.Cos(pt));
                        NativeBodyAnimator.AimAndPin(b, map, d, wall, 1, b.Bone(map.AimBone), NativeBodyAnimator.PinBones(b, map));
                    }
                    if (render.Length > 0)
                    {
                        tiles.Add(($"{spec} {f:F1}", Splat(b, front: true)));
                        tiles.Add(("side", Splat(b, front: false)));
                    }
                    (float zlo, float zhi, float xlo, float xhi) = ExtentZX(b);
                    Console.WriteLine($"  {spec} t={an.Duration * f:F2}: mesh z {zlo:F2}..{zhi:F2}  x {xlo:F2}..{xhi:F2}  (height {b.Height:F2})");
                    for (int i = 0; i < g.BoneCount; i++)
                    {
                        Vector3 p = b.World[i].ExtractTranslation();
                        Console.WriteLine($"    {g.Names[i],-24} ({p.X,6:F2},{p.Y - b.Feet,6:F2},{p.Z,6:F2})");
                    }
                }
            }
            if (render.Length > 0 && tiles.Count > 0) SaveSheet(tiles, render);
        }

        const int Tile = 280;

        static void SaveSheet(List<(string Label, Rgba32[] Px)> tiles, string path)
        {
            int cols = 6, rows = (tiles.Count + cols - 1) / cols;
            using var img = new Image<Rgba32>(cols * Tile, rows * Tile, new Rgba32(40, 44, 52, 255));
            for (int k = 0; k < tiles.Count; k++)
            {
                int ox = k % cols * Tile, oy = k / cols * Tile;
                for (int y = 0; y < Tile; y++)
                {
                    for (int x = 0; x < Tile; x++) img[ox + x, oy + y] = tiles[k].Px[y * Tile + x];
                }
            }
            img.SaveAsPng(path);
            Console.WriteLine($"  sheet {path}: {tiles.Count / 2} poses, front + side each (labels in order):");
            for (int k = 0; k < tiles.Count; k += 2) Console.WriteLine($"    {k / 2}: {tiles[k].Label}");
        }

        // the posed mesh, orthographic, z-buffered, flat-shaded by face normal: front = looking at his face (his +Z toward
        // the viewer, his left on the right), side = his +Z to the right. Feet at the bottom, 4.4 model units tall
        static Rgba32[] Splat(NativeBody b, bool front)
        {
            HdGun g = b.Data;
            var px = new Rgba32[Tile * Tile];
            var depth = new float[Tile * Tile];
            Array.Fill(px, new Rgba32(40, 44, 52, 255));
            Array.Fill(depth, Single.MaxValue);
            float scale = Tile / 4.4f;
            var pal = new float[32 * 16];
            var light = new Vector3(0.4f, 0.6f, 0.7f).Normalized();
            for (int m = 0; m < g.Materials.Length; m++)
            {
                HdGun.Material mat = g.Materials[m];
                b.FillPalette(m, pal);
                var s = new Vector3[mat.VertexCount];
                for (int v = 0; v < mat.VertexCount; v++)
                {
                    int o = v * HdGun.FloatsPerVertex;
                    var p = new Vector4(mat.Vertices[o], mat.Vertices[o + 1], mat.Vertices[o + 2], 1);
                    Vector4 acc = Vector4.Zero;
                    for (int k = 0; k < 4; k++)
                    {
                        float w = mat.Vertices[o + 15 + k];
                        if (w <= 0) continue;
                        int bi = (int)mat.Vertices[o + 11 + k] * 16;
                        var M = new Matrix4(pal[bi], pal[bi + 1], pal[bi + 2], pal[bi + 3], pal[bi + 4], pal[bi + 5], pal[bi + 6], pal[bi + 7],
                            pal[bi + 8], pal[bi + 9], pal[bi + 10], pal[bi + 11], pal[bi + 12], pal[bi + 13], pal[bi + 14], pal[bi + 15]);
                        acc += w * (p * M);
                    }
                    // view space: x right, y up, z away from the viewer
                    s[v] = front ? new Vector3(-acc.X, acc.Y - b.Feet, -acc.Z) : new Vector3(acc.Z, acc.Y - b.Feet, -acc.X);
                }
                for (int t = 0; t + 2 < s.Length; t += 3)
                {
                    Vector3 a = s[t], c1 = s[t + 1], c2 = s[t + 2];
                    Vector3 n = Vector3.Cross(c1 - a, c2 - a);
                    if (n.LengthSquared < 1e-12f) continue;
                    n = n.Normalized();
                    float shade = 0.25f + 0.75f * Math.Abs(Vector3.Dot(n, light));
                    var col = new Rgba32((byte)(200 * shade), (byte)(170 * shade), (byte)(230 * shade), 255);
                    Vector2 P(Vector3 q) => new(Tile / 2f + q.X * scale, Tile - 6 - q.Y * scale);
                    Vector2 p0 = P(a), p1 = P(c1), p2 = P(c2);
                    int x0 = Math.Max(0, (int)MathF.Floor(MathF.Min(p0.X, MathF.Min(p1.X, p2.X))));
                    int x1 = Math.Min(Tile - 1, (int)MathF.Ceiling(MathF.Max(p0.X, MathF.Max(p1.X, p2.X))));
                    int y0 = Math.Max(0, (int)MathF.Floor(MathF.Min(p0.Y, MathF.Min(p1.Y, p2.Y))));
                    int y1 = Math.Min(Tile - 1, (int)MathF.Ceiling(MathF.Max(p0.Y, MathF.Max(p1.Y, p2.Y))));
                    float area = (p1.X - p0.X) * (p2.Y - p0.Y) - (p2.X - p0.X) * (p1.Y - p0.Y);
                    if (MathF.Abs(area) < 1e-6f) continue;
                    for (int y = y0; y <= y1; y++)
                    {
                        for (int x = x0; x <= x1; x++)
                        {
                            float w0 = ((p1.X - x) * (p2.Y - y) - (p2.X - x) * (p1.Y - y)) / area;
                            float w1 = ((p2.X - x) * (p0.Y - y) - (p0.X - x) * (p2.Y - y)) / area;
                            float w2 = 1 - w0 - w1;
                            if (w0 < 0 || w1 < 0 || w2 < 0) continue;
                            float z = w0 * a.Z + w1 * c1.Z + w2 * c2.Z;
                            int i = y * Tile + x;
                            if (z >= depth[i]) continue;
                            depth[i] = z;
                            px[i] = col;
                        }
                    }
                }
            }
            // the floor line
            for (int x = 0; x < Tile; x++) px[(Tile - 6) * Tile + x] = new Rgba32(120, 120, 120, 255);
            return px;
        }

        static (float, float, float, float) ExtentZX(NativeBody b)
        {
            HdGun g = b.Data;
            float zlo = Single.MaxValue, zhi = Single.MinValue, xlo = Single.MaxValue, xhi = Single.MinValue;
            var pal = new float[32 * 16];
            for (int m = 0; m < g.Materials.Length; m++)
            {
                HdGun.Material mat = g.Materials[m];
                b.FillPalette(m, pal);
                for (int v = 0; v < mat.VertexCount; v++)
                {
                    int o = v * HdGun.FloatsPerVertex;
                    var p = new Vector4(mat.Vertices[o], mat.Vertices[o + 1], mat.Vertices[o + 2], 1);
                    Vector4 acc = Vector4.Zero;
                    for (int k = 0; k < 4; k++)
                    {
                        float w = mat.Vertices[o + 15 + k];
                        if (w <= 0) continue;
                        int bi = (int)mat.Vertices[o + 11 + k] * 16;
                        var M = new Matrix4(pal[bi], pal[bi + 1], pal[bi + 2], pal[bi + 3], pal[bi + 4], pal[bi + 5], pal[bi + 6], pal[bi + 7],
                            pal[bi + 8], pal[bi + 9], pal[bi + 10], pal[bi + 11], pal[bi + 12], pal[bi + 13], pal[bi + 14], pal[bi + 15]);
                        acc += w * (p * M);
                    }
                    zlo = Math.Min(zlo, acc.Z); zhi = Math.Max(zhi, acc.Z);
                    xlo = Math.Min(xlo, acc.X); xhi = Math.Max(xhi, acc.X);
                }
            }
            return (zlo, zhi, xlo, xhi);
        }

        // --raw <clip,...>: the trooper's top bone and pelvis tracks as the disc has them (before the root pin)
        static void Raw(string disc, string clipsArg)
        {
            var src = MphRecomp.Import.Disc.DiscImage.Open(disc);
            var pak = new MphRecomp.Import.Retro.Pak(src.ReadFile("Metroid1.pak"), "Metroid1.pak");
            byte[] ancsB = pak.Get(0x8169653a);
            var set = MphRecomp.Import.Retro.Ancs.Parse(ancsB).Set!;
            (_, _, uint kid) = MphRecomp.Import.Retro.Ancs.FindCharacter(ancsB, "wave_trooper");
            var skel = MphRecomp.Import.Retro.Cinf.Load(pak.Get(kid));
            foreach (string clip in clipsArg.Split(','))
            {
                var meta = set.Anims.First(a => a.Name == clip).Anim;
                var anim = MphRecomp.Import.Retro.Anim.Load(pak.Get(MphRecomp.Import.Retro.Ancs.Prims(meta)[0].AnimId), false);
                Console.WriteLine($"  {clip}: {anim.Keys} keys, tick {anim.Tick:F4}");
                foreach (int k in new[] { 0, anim.Keys / 2, anim.Keys - 1 })
                {
                    anim.Sample(k * anim.Tick, out var rot, out var trans);
                    foreach ((uint id, var bone) in skel.Bones.Where(b => b.Value.Name is "root" or "Skeleton_Root"))
                    {
                        string R = rot.TryGetValue(id, out var q) ? String.Join(",", q.Select(x => x.ToString("F3"))) : "-";
                        string T = trans.TryGetValue(id, out var t) ? String.Join(",", t.Select(x => x.ToString("F3"))) : "-";
                        Console.WriteLine($"    key {k,3} {bone.Name,-14} bind ({bone.X:F2},{bone.Y:F2},{bone.Z:F2}) rot [{R}] trans [{T}]");
                    }
                }
            }
        }

        static void CopyDir(string from, string to)
        {
            if (Directory.Exists(to)) Directory.Delete(to, true);
            Directory.CreateDirectory(to);
            foreach (string f in Directory.GetFiles(from)) File.Copy(f, Path.Combine(to, Path.GetFileName(f)));
        }
    }
}
