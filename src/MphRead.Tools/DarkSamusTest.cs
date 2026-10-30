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
    // -darksamustest [MP2DarkSamus|MP3DarkSamus|all] [--mp2 disc] [--mp3 folder] [--out dir] [--noimport] [--copyto <data root>]
    //                [--sheet <png> [--clips a,b,..] [--nohold] [--size 220]]
    // Dark Samus's native bodies (Core Campaign/DarkSamusBody.cs; docs/CHARACTERS_AND_SKINS.md D2/D8/D14/D18): imports the
    // "bodies" recipes from the owner's discs (as -bodytest), loads each folder the way the app does and checks her map:
    // every mapped clip there, the hover lift set, no pose below her own feet, and the D14 cannon hold -- after the hold the
    // cannon (R_elbow to its tip locator) points along MPH's aim within 3 deg in every mapped clip at 8 times, aimed level,
    // 45 deg up and 45 deg down. --sheet: a software-rendered contact sheet (no GL; flat-shaded, the cannon arm red, the
    // aim line green, the floor line where MphRead's floor is under her hover): a row per clip, columns t=0 / middle / end
    // from her front-left, then the middle from her left side level / aimed up / aimed down.
    internal static class DarkSamusTest
    {
        static readonly string[] Names = { "MP2DarkSamus", "MP3DarkSamus" };

        public static void Run(string[] args)
        {
            string repo = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
            string Arg(string k, string d) { int i = Array.IndexOf(args, k); return i > 0 && i + 1 < args.Length ? args[i + 1] : d; }
            string which = args.Length >= 2 && !args[1].StartsWith("-") ? args[1] : "all";
            string outRoot = Path.GetFullPath(Arg("--out", Path.Combine(Path.GetTempPath(), "mph-darksamustest")));
            string copyTo = Arg("--copyto", "");
            // --effect <id> --out <dir>: an MPH effect's particle textures as PNGs + their average colour (her morph's samusFurl)
            if (Arg("--effect", "") is string fx && fx.Length > 0)
            {
                DumpEffect(Int32.Parse(fx), outRoot, Arg("--fs", ""));
                return;
            }
            List<string> names = Names.Where(n => which == "all" || n.Equals(which, StringComparison.OrdinalIgnoreCase)).ToList();
            if (!args.Contains("--noimport"))
            {
                var discs = new Dictionary<string, string>
                {
                    ["mp2"] = Arg("--mp2", Path.Combine(repo, "brawl_extract", "prime", "echoes_retail.rvz")),
                    ["mp3"] = Arg("--mp3", Path.Combine(repo, "brawl_extract", "prime", "trilogy", "DATA", "files")),
                };
                RecipeStore store = RecipeStore.FromDirectory(Path.Combine(repo, "recipes"));
                var svc = new ImportService(outRoot, store);
                foreach (IGrouping<string, ImportRecipe> game in store.All.Where(r => r.Kind == "bodies" && names.Contains(r.Name)).GroupBy(r => r.Game))
                {
                    string disc = discs.TryGetValue(game.Key, out string? d) ? d : "";
                    if (!File.Exists(disc) && !Directory.Exists(disc))
                    {
                        Console.WriteLine($"  [SKIP] {game.Key}: no disc at '{disc}'");
                        continue;
                    }
                    ImportReport rep = svc.Import(game.Key, disc, null, default, game.Select(r => r.Id).ToList());
                    foreach (ImportItemInfo it in rep.Items) Console.WriteLine($"    {it.Id,-24} {it.Status,-10} {it.Seconds,6:F2} s  {it.Message}");
                }
            }
            int pass = 0;
            foreach (string name in names)
            {
                string dir = Path.Combine(outRoot, "bodies", name);
                if (!Check(dir, name)) continue;
                pass++;
                if (copyTo.Length > 0) CopyDir(dir, Path.Combine(copyTo, "bodies", name));
            }
            Console.WriteLine($"  SUMMARY: {pass}/{names.Count} Dark Samus bodies PASS");
            string sheet = Arg("--sheet", "");
            if (sheet.Length > 0 && names.Count == 1)
            {
                Sheet(Path.Combine(outRoot, "bodies", names[0]), sheet, Arg("--clips", ""), !args.Contains("--nohold"), Int32.Parse(Arg("--size", "220")));
            }
        }

        // fs: an unpacked US 1.1 ROM (the folder with archives/ and effects/), as the desktop app's game/files/AMHE1
        static void DumpEffect(int id, string outDir, string fs)
        {
            Directory.CreateDirectory(outDir);
            Paths.SetPath(Ver.AMHE1, fs);
            Paths.MphKey = Ver.AMHE1;
            MphRead.Effect e = MphRead.Read.LoadEffect(id, persistent: false);
            Console.WriteLine($"  effect {id} {e.Name}: {e.Elements.Count} elements");
            foreach (MphRead.EffectElement el in e.Elements)
            {
                foreach (MphRead.Particle pd in el.Particles)
                {
                    MphRead.Material m = pd.Model.Materials[pd.MaterialId];
                    if (m.TextureId < 0) { Console.WriteLine($"    {el.Name} / {pd.Name}: no texture"); continue; }
                    MphRead.Texture t = pd.Model.Recolors[0].Textures[m.TextureId];
                    IReadOnlyList<MphRead.ColorRgba> px = pd.Model.Recolors[0].GetPixels(m.TextureId, m.PaletteId);
                    using var img = new Image<Rgba32>(t.Width, t.Height);
                    double r = 0, g = 0, b = 0, w = 0;
                    for (int i = 0; i < px.Count; i++)
                    {
                        MphRead.ColorRgba c = px[i];
                        img[i % t.Width, i / t.Width] = new Rgba32(c.Red, c.Green, c.Blue, c.Alpha);
                        r += c.Red * c.Alpha; g += c.Green * c.Alpha; b += c.Blue * c.Alpha; w += c.Alpha;
                    }
                    string png = Path.Combine(outDir, $"fx{id}_{el.Name}_{pd.Name}.png");
                    img.SaveAsPng(png);
                    Console.WriteLine($"    {el.Name} / {pd.Name}: {t.Width}x{t.Height}, avg rgb {(w > 0 ? r / w : 0):0} {(w > 0 ? g / w : 0):0} {(w > 0 ? b / w : 0):0}, funcs {string.Join(",", el.Funcs.Keys)}");
                }
            }
        }

        // the map's clips: the legs layer's, then the torso layer's (posed over her idle legs, as MphRead's biped2 plays them)
        static List<(string Clip, bool Torso)> MappedClips(BodyClips map)
        {
            var legs = new SortedSet<string>(StringComparer.Ordinal) { map.Idle };
            var torso = new SortedSet<string>(StringComparer.Ordinal);
            foreach (PlayerAnimation a in Enum.GetValues<PlayerAnimation>())
            {
                if (a is PlayerAnimation.None or PlayerAnimation.Unused17) continue;
                legs.Add(map.ClipFor(a));
                if (map.TorsoFor(a, out (string Clip, bool Loop) e)) torso.Add(e.Clip);
            }
            legs.Remove("");
            torso.Remove("");
            return legs.Select(c => (c, false)).Concat(torso.Select(c => (c, true))).ToList();
        }

        // clip c at fraction f of its length (a torso clip over her idle legs at the same fraction)
        static void PoseAt(NativeBody b, BodyClips map, (string Clip, bool Torso) c, float f)
        {
            HdGun.Anim an = b.Clip(c.Clip)!;
            if (c.Torso && b.Clip(map.Idle) is HdGun.Anim idle && b.Bone(map.TorsoRoot) >= 0)
            {
                b.Pose(idle, idle.Duration * f);
                b.PoseLayer(b.Bone(map.TorsoRoot), an, an.Duration * f, null, 0, 1, 1);
            }
            else b.Pose(an, an.Duration * f);
        }

        static bool Check(string dir, string name)
        {
            if (!NativeBody.Exists(dir))
            {
                Console.WriteLine($"  [FAIL] {name}: no body.bin");
                return false;
            }
            NativeBody b = NativeBody.Load(dir);
            bool ok = true;
            Console.WriteLine($"  {name}: {b.Data.BoneCount} bones, {b.Data.Anims.Count} clips, height {b.Height:F2} (scale {b.Scale:F3})");
            BodyClips? map = BodyClips.For(b);
            DarkSamusBody.Hold? hold = DarkSamusBody.HoldOf(b);
            if (map == null || hold == null || map.AfterPose == null)
            {
                Console.WriteLine($"    FAIL: map {map != null}, hold {hold != null}, AfterPose {map?.AfterPose != null}");
                return false;
            }
            Console.WriteLine($"    lift {map.Lift:F2} units ({map.Lift / b.Scale:F2} of her own), hold frame {hold.OffDegrees:F1} deg off ahead before truing");
            if (map.Lift >= 0.01f || map.Lift < -0.06f) ok = false; // toe tips just into the floor (owner 20:30), not hovering
            string tip = DarkSamusBody.TipOf(b);
            float[] aims = { 0, MathF.Sin(MathF.PI / 4), -MathF.Sin(MathF.PI / 4) };
            foreach ((string c, bool torso) in MappedClips(map))
            {
                HdGun.Anim? an = b.Clip(c);
                if (an == null)
                {
                    Console.WriteLine($"    FAIL: clip {c} missing");
                    ok = false;
                    continue;
                }
                float lowest = Single.MaxValue, worst = 0;
                for (int s = 0; s < 8; s++)
                {
                    foreach (float y in aims)
                    {
                        PoseAt(b, map, (c, torso), s / 7f);
                        hold.Apply(b, y);
                        Matrix4 pitch = DarkSamusBody.Hold.Pitch(y);
                        Vector3 want = Vector3.TransformVector(Vector3.UnitZ, pitch);
                        float off = MathHelper.RadiansToDegrees(MathF.Acos(Math.Clamp(Vector3.Dot(DarkSamusBody.Hold.Aim(b, tip), want), -1f, 1f)));
                        worst = Math.Max(worst, off);
                        if (y == 0) lowest = Math.Min(lowest, Lowest(b));
                    }
                }
                bool good = worst < 3 && lowest - b.Feet > -0.05f * b.Height;
                Console.WriteLine($"    {(good ? "ok  " : "FAIL")} {(torso ? "torso " : "legs  ")}{c,-36} {an.Duration,5:F2} s  feet {lowest - b.Feet,6:F2}  cannon off aim <= {worst:F1} deg");
                ok &= good;
            }
            Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {name}");
            return ok;
        }

        // the posed mesh's skinned vertices (model space) and normals, per material
        static void Skin(NativeBody b, int m, float[] pal, List<(Vector3 P, Vector3 N, int Bone)> dst)
        {
            HdGun.Material mat = b.Data.Materials[m];
            b.FillPalette(m, pal);
            for (int v = 0; v < mat.VertexCount; v++)
            {
                int o = v * HdGun.FloatsPerVertex;
                var p = new Vector4(mat.Vertices[o], mat.Vertices[o + 1], mat.Vertices[o + 2], 1);
                var n = new Vector4(mat.Vertices[o + 3], mat.Vertices[o + 4], mat.Vertices[o + 5], 0);
                Vector4 ap = Vector4.Zero, an = Vector4.Zero;
                int bone = -1;
                float best = 0;
                for (int k = 0; k < 4; k++)
                {
                    float w = mat.Vertices[o + 15 + k];
                    if (w <= 0) continue;
                    int slot = (int)mat.Vertices[o + 11 + k];
                    int bi = slot * 16;
                    var M = new Matrix4(pal[bi], pal[bi + 1], pal[bi + 2], pal[bi + 3], pal[bi + 4], pal[bi + 5], pal[bi + 6], pal[bi + 7],
                        pal[bi + 8], pal[bi + 9], pal[bi + 10], pal[bi + 11], pal[bi + 12], pal[bi + 13], pal[bi + 14], pal[bi + 15]);
                    ap += w * (p * M);
                    an += w * (n * M);
                    if (w > best)
                    {
                        best = w;
                        bone = mat.Palette[slot];
                    }
                }
                dst.Add((ap.Xyz, an.Xyz.LengthSquared > 0 ? an.Xyz.Normalized() : Vector3.UnitY, bone));
            }
        }

        static float Lowest(NativeBody b)
        {
            var pal = new float[32 * 16];
            var verts = new List<(Vector3 P, Vector3 N, int Bone)>();
            float lo = Single.MaxValue;
            for (int m = 0; m < b.Data.Materials.Length; m++)
            {
                verts.Clear();
                Skin(b, m, pal, verts);
                foreach ((Vector3 p, _, _) in verts) lo = Math.Min(lo, p.Y);
            }
            return lo;
        }

        static void Sheet(string dir, string png, string clipsArg, bool held, int size)
        {
            NativeBody b = NativeBody.Load(dir);
            BodyClips map = BodyClips.For(b)!;
            DarkSamusBody.Hold? hold = held ? DarkSamusBody.HoldOf(b) : null;
            List<(string Clip, bool Torso)> mapped = MappedClips(map);
            // --clips: by name (a torso clip of the map stays over her idle legs; a name with a leading + is posed that way too)
            List<(string Clip, bool Torso)> clips = clipsArg.Length > 0
                ? clipsArg.Split(',').Select(c => c.StartsWith('+') ? (c[1..], true) : (c, mapped.Any(m => m.Clip == c && m.Torso))).ToList()
                : mapped;
            clips = clips.Where(c => b.Clip(c.Clip) != null).ToList();
            int elbow = b.Bone("R_elbow");
            var arm = new bool[b.Data.BoneCount];
            foreach (int i in b.Data.Order) arm[i] = i == elbow || b.Data.Parent[i] >= 0 && arm[b.Data.Parent[i]];
            string tip = DarkSamusBody.TipOf(b);
            // her height plus the hover and some room, in her own units, fills a cell
            float span = (b.Height + map.Lift / b.Scale) * 1.35f;
            float floor = b.Feet - map.Lift / b.Scale;
            var cols = new (float T, float Yaw, float Aim)[] { (0, 35, 0), (0.5f, 35, 0), (1, 35, 0), (0.5f, 90, 0), (0.5f, 90, 0.6f), (0.5f, 90, -0.6f) };
            using var img = new Image<Rgba32>(size * cols.Length, size * clips.Count, new Rgba32(40, 44, 52));
            var pal = new float[32 * 16];
            var verts = new List<(Vector3 P, Vector3 N, int Bone)>();
            for (int r = 0; r < clips.Count; r++)
            {
                for (int c = 0; c < cols.Length; c++)
                {
                    PoseAt(b, map, clips[r], cols[c].T);
                    hold?.Apply(b, cols[c].Aim);
                    float yaw = MathHelper.DegreesToRadians(cols[c].Yaw), cy = MathF.Cos(yaw), sy = MathF.Sin(yaw);
                    // camera in front of her turned by yaw toward her left: her left (+X) on our right at yaw 0
                    Vector3 View(Vector3 p) => new(p.X * cy - p.Z * sy, p.Y, p.X * sy + p.Z * cy);
                    var zbuf = new float[size * size];
                    Array.Fill(zbuf, Single.MinValue);
                    int ox = c * size, oy = r * size;
                    (float X, float Y) Screen(Vector3 v) => (size / 2f + v.X / span * size, size * 0.92f - (v.Y - floor) / span * size);
                    var light = Vector3.Normalize(new Vector3(0.4f, 0.6f, 1f));
                    for (int m = 0; m < b.Data.Materials.Length; m++)
                    {
                        verts.Clear();
                        Skin(b, m, pal, verts);
                        for (int v = 0; v + 2 < verts.Count; v += 3)
                        {
                            Vector3 a0 = View(verts[v].P), a1 = View(verts[v + 1].P), a2 = View(verts[v + 2].P);
                            Vector3 n = View(Vector3.Normalize(verts[v].N + verts[v + 1].N + verts[v + 2].N));
                            float shade = 0.3f + 0.7f * MathF.Abs(Vector3.Dot(n, light));
                            bool red = verts[v].Bone >= 0 && arm[verts[v].Bone];
                            var col = red ? new Rgba32((byte)(230 * shade), (byte)(70 * shade), (byte)(60 * shade))
                                : new Rgba32((byte)(150 * shade), (byte)(160 * shade), (byte)(185 * shade));
                            Tri(img, zbuf, size, ox, oy, Screen(a0), a0.Z, Screen(a1), a1.Z, Screen(a2), a2.Z, col);
                        }
                    }
                    // the floor MphRead stands her on, and the aim line from the elbow through the cannon tip
                    for (int x = 0; x < size; x++) img[ox + x, Math.Clamp(oy + (int)(size * 0.92f), 0, img.Height - 1)] = new Rgba32(200, 200, 90);
                    Vector3 e = View(b.World[elbow].Row3.Xyz), k = View(b.World[b.Bone(tip)].Row3.Xyz);
                    Vector3 far = e + (k - e).Normalized() * span * 0.5f;
                    (float X, float Y) s0 = Screen(k), s1 = Screen(far);
                    for (int i = 0; i <= 60; i++)
                    {
                        int x = (int)(s0.X + (s1.X - s0.X) * i / 60f), y = (int)(s0.Y + (s1.Y - s0.Y) * i / 60f);
                        if (x >= 0 && x < size && y >= 0 && y < size) img[ox + x, oy + y] = new Rgba32(90, 230, 110);
                    }
                }
            }
            img.SaveAsPng(png);
            File.WriteAllLines(Path.ChangeExtension(png, ".txt"), clips.Select((c, i) => $"row {i + 1}: {c.Clip}{(c.Torso ? " (torso, over idle legs)" : "")}")
                .Append($"columns: t=0 / mid / end from her front-left (35 deg); mid from her left side aimed level / up / down; hold {(hold != null ? "ON" : "OFF")}"));
            Console.WriteLine($"  sheet: {png} ({clips.Count} rows)");
        }

        static void Tri(Image<Rgba32> img, float[] zbuf, int size, int ox, int oy, (float X, float Y) p0, float z0, (float X, float Y) p1, float z1,
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
                    img[ox + x, oy + y] = col;
                }
            }
        }

        static void CopyDir(string from, string to)
        {
            Directory.CreateDirectory(to);
            foreach (string f in Directory.GetFiles(from)) File.Copy(f, Path.Combine(to, Path.GetFileName(f)), true);
        }
    }
}
