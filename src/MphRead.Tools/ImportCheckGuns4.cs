using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using MphRecomp.Assets;
using MphRecomp.Campaign;
using MphRecomp.Import;
using MphRecomp.Import.Beyond;
using MphRecomp.Import.Guns;

namespace MphRead
{
    // -importcheck guns4 [Suit|all] [--out dir] [--chpr-dir dir] [--gltf-root dir] [--conv-root dir] [--recipes dir|embedded]
    //                    [--repo dir] [--no-emulate] [-v]
    // The golden check of the Beyond first-person guns (game "mp4" recipes in recipes/guns): the C# builder
    // (MphRecomp.Import.Guns.Gun4Builder, the port of gun_export4b.py) fed by the TEST-ONLY adapters over the reference
    // pipeline's inputs (Gun4TestSources.cs: the Model Dumper glTF, the static material conversion) and Samus's CHPR
    // decoded by ChprCharacter, compared with brawl_extract/Converted/guns/MP4<Suit> (made by gun_export4b.py):
    //   gun.bin      MPHGUN2 parsed: bones, names, parents, palettes, counts equal; floats within 1e-4 relative
    //                (1e-6 absolute near zero)
    //   gun.gx.json  parsed: structurally equal, numbers within 1e-5
    //   gun.cfg      equal lines, the root matrix's 16 floats within 1e-5
    //   *.png        pixel-identical (decoded RGBA)
    // and loads the result the way the app does (HdGun.Load, GxJson.Read). The reference read the Python decoder's npz
    // animations, made from constant pool 0 as the dumper's chpr.json printed it (shortest decimals): the C# decoder
    // reads the CHPR's own float32s, so animation values may differ by one float32 step (<= 1.2e-7 relative; see
    // -importcheck beyond-anims). Unless --no-emulate, every suit is then rebuilt with pool 0 fed as the Python saw it
    // ("reference emulation"): its animation floats must be bit-identical to the reference's.
    // The CHPR comes from <chpr-dir>/<recipe chpr>.CHPR.bin (default brawl_extract/mp4/anims/src) and must have the
    // recipe's SHA-1; the glTF from <gltf-root>/<recipe model>/<recipe modelName>.gltf; the conversion from
    // <conv-root>/<Suit>/<recipe character>.{dae,gx.json}.
    internal static partial class ImportCheck
    {
        static void Guns4(string[] args, string repo)
        {
            string Arg(string k, string d) { int i = Array.IndexOf(args, k); return i > 0 && i + 1 < args.Length ? args[i + 1] : d; }
            repo = Path.GetFullPath(Arg("--repo", repo));
            string which = args.Length >= 3 && !args[2].StartsWith("-") ? args[2] : "all";
            bool verbose = args.Contains("-v");
            bool emulate = !args.Contains("--no-emulate");
            string bx = Path.Combine(repo, "brawl_extract");
            string refRoot = Path.Combine(bx, "Converted");
            string chprDir = Arg("--chpr-dir", Path.Combine(bx, "mp4", "anims", "src"));
            string gltfRoot = Arg("--gltf-root", Path.Combine(bx, "mp4", "universeRoom", "Base"));
            string convRoot = Arg("--conv-root", Path.Combine(bx, "rig_work", "gun", "mp4conv"));
            string outRoot = Path.GetFullPath(Arg("--out", Path.Combine(Path.GetTempPath(), "mph-importcheck", "guns4-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"))));
            if (outRoot.StartsWith(Path.GetFullPath(refRoot), StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine($"  -importcheck guns4: --out {outRoot} is inside the reference outputs; pick another folder");
                return;
            }
            string recipeArg = Arg("--recipes", Path.Combine(repo, "recipes"));
            RecipeStore store = recipeArg == "embedded" || !Directory.Exists(recipeArg) ? RecipeStore.Embedded() : RecipeStore.FromDirectory(recipeArg);
            List<ImportRecipe> recipes = store.All.Where(r => r.Kind == "guns" && Gun4Recipe.Applies(r)
                && (which == "all" || r.Name.Equals(which, StringComparison.OrdinalIgnoreCase))).ToList();
            Console.WriteLine($"  importcheck guns4 {which}: {recipes.Count} recipes from {store.Origin}; output {outRoot}");
            if (recipes.Count == 0)
            {
                Console.WriteLine($"  no mp4 guns recipe named '{which}'");
                return;
            }
            var characters = new Dictionary<string, (ChprCharacter Ch, double[] Pool)?>(StringComparer.OrdinalIgnoreCase);
            int pass = 0;
            var failed = new List<string>();
            var times = new List<string>();
            foreach (ImportRecipe ir in recipes)
            {
                Gun4Recipe r = Gun4Recipe.From(ir);
                Console.WriteLine($"  == {r.Name}");
                // the character project (shared by the suits), hash-checked like the importer checks its sources
                if (!characters.TryGetValue(r.Chpr, out var chEntry))
                {
                    chEntry = null;
                    string path = Path.Combine(chprDir, r.Chpr + ".CHPR.bin");
                    if (!File.Exists(path))
                    {
                        Console.WriteLine($"    FAIL no CHPR at {path}");
                    }
                    else
                    {
                        byte[] bytes = File.ReadAllBytes(path);
                        string sha = Convert.ToHexString(SHA1.HashData(bytes)).ToLowerInvariant();
                        if (sha != r.ChprSha1)
                        {
                            Console.WriteLine($"    FAIL {path}: SHA-1 {sha}, the recipe was made from {r.ChprSha1}");
                        }
                        else
                        {
                            var swc = Stopwatch.StartNew();
                            ChprCharacter c = ChprCharacter.Load(bytes);
                            Console.WriteLine($"    CHPR {r.Chpr}: {c.Skeleton.Names.Length} nodes, {c.AnimNames.Length} animations, SHA-1 matches ({swc.Elapsed.TotalMilliseconds:F0} ms)");
                            chEntry = (c, c.Pool0);
                        }
                    }
                    characters[r.Chpr] = chEntry;
                }
                if (chEntry == null)
                {
                    failed.Add(r.Name);
                    continue;
                }
                (ChprCharacter ch, double[] pool) = chEntry.Value;
                string? modelDir = Directory.Exists(gltfRoot) ? Directory.GetDirectories(gltfRoot, r.Model + "*").FirstOrDefault() : null;
                string gltf = modelDir == null ? "" : Path.Combine(modelDir, r.ModelName + ".gltf");
                string conv = Path.Combine(convRoot, r.Name);
                if (!File.Exists(gltf) || !Directory.Exists(conv))
                {
                    Console.WriteLine($"    FAIL inputs missing: glTF '{gltf}', conversion '{conv}'");
                    failed.Add(r.Name);
                    continue;
                }
                string outDir = Path.Combine(outRoot, "guns", r.Name);
                bool ok;
                try
                {
                    var sw = Stopwatch.StartNew();
                    GltfBeyondModel model = GltfBeyondModel.Load(gltf);
                    DaeBeyondLook look = DaeBeyondLook.Load(conv, r.Character);
                    double tRead = sw.Elapsed.TotalSeconds;
                    sw.Restart();
                    ch.Pool0 = pool;
                    GunImporter.Result res = Gun4Builder.Build(model, look, ch, r, outDir,
                        verbose ? (f, m) => Console.WriteLine($"      [{f * 100,5:F1}%] {m}") : null);
                    double tBuild = sw.Elapsed.TotalSeconds;
                    times.Add($"{r.Name}: test inputs read in {tRead:F2} s, built in {tBuild:F2} s");
                    foreach (string line in res.Log) Console.WriteLine("    | " + line);
                    ok = CompareSuit4(Path.Combine(refRoot, "guns", r.Name), outDir, false);
                    if (emulate)
                    {
                        // reference emulation: pool 0 as the dumper's chpr.json printed it, parsed as float64 (the npz inputs)
                        ch.Pool0 = pool.Select(v => Double.IsFinite(v) ? Double.Parse(((float)v).ToString("R", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture) : v).ToArray();
                        string emuDir = Path.Combine(outRoot, "emulated", r.Name);
                        Gun4Builder.Build(GltfBeyondModel.Load(gltf), DaeBeyondLook.Load(conv, r.Character), ch, r, emuDir);
                        ch.Pool0 = pool;
                        Console.WriteLine("    -- reference emulation (pool 0 via the dumper's JSON decimals): animation floats must be bit-identical");
                        ok &= CompareSuit4(Path.Combine(refRoot, "guns", r.Name), emuDir, true);
                    }
                }
                catch (Exception e) when (e is InvalidDataException or IOException or KeyNotFoundException or FormatException)
                {
                    Console.WriteLine($"    FAIL build: {e.GetType().Name}: {e.Message}");
                    ok = false;
                }
                finally
                {
                    ch.Pool0 = pool;
                }
                Console.WriteLine($"    => {(ok ? "PASS" : "FAIL")} {r.Name}");
                if (ok) pass++;
                else failed.Add(r.Name);
            }
            Console.WriteLine();
            foreach (string t in times) Console.WriteLine("  time " + t);
            Console.WriteLine($"  SUMMARY: {pass}/{recipes.Count} MP4 suits PASS" + (failed.Count > 0 ? "; FAIL: " + String.Join(", ", failed) : ""));
        }

        // animFloatsExact: the gun.bin animation floats must be bit-identical (the reference emulation)
        static bool CompareSuit4(string refDir, string outDir, bool animFloatsExact)
        {
            if (!Directory.Exists(refDir))
            {
                Console.WriteLine($"    FAIL no reference folder {refDir}");
                return false;
            }
            bool all = true;
            var refFiles = Directory.GetFiles(refDir).Select(f => Path.GetFileName(f)!).OrderBy(x => x, StringComparer.Ordinal).ToList();
            var outFiles = new HashSet<string>(Directory.GetFiles(outDir).Select(f => Path.GetFileName(f)!));
            int pngs = 0, pngsSame = 0;
            foreach (string f in refFiles)
            {
                string a = Path.Combine(refDir, f), b = Path.Combine(outDir, f);
                if (!outFiles.Remove(f))
                {
                    Console.WriteLine($"    FAIL {f}: not produced");
                    all = false;
                    continue;
                }
                (bool ok, string info) = f switch
                {
                    "gun.bin" => CompareGunBin2(a, b, animFloatsExact),
                    "gun.gx.json" => CompareJson(a, b),
                    "gun.cfg" => CompareCfg4(a, b),
                    _ when f.EndsWith(".png", StringComparison.OrdinalIgnoreCase) => ComparePng(a, b),
                    _ => (File.ReadAllBytes(a).AsSpan().SequenceEqual(File.ReadAllBytes(b)), "bytes"),
                };
                if (f.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                {
                    pngs++;
                    if (ok) pngsSame++;
                    if (!ok) Console.WriteLine($"    FAIL {f}: {info}");
                }
                else
                {
                    Console.WriteLine($"    {(ok ? "PASS" : "FAIL")} {f}: {info}");
                }
                all &= ok;
            }
            Console.WriteLine($"    {(pngs == pngsSame ? "PASS" : "FAIL")} textures: {pngsSame}/{pngs} PNGs pixel-identical");
            foreach (string extra in outFiles.OrderBy(x => x, StringComparer.Ordinal))
            {
                Console.WriteLine($"    FAIL {extra}: produced but not in the reference");
                all = false;
            }
            try
            {
                HdGun g = HdGun.Load(Path.Combine(outDir, "gun.bin"));
                List<GxMaterial> gx = GxJson.Read(Path.Combine(outDir, "gun.gx.json"));
                bool same = gx.Count == g.Materials.Length && g.KeyStride == 10 && g.Muzzle != null && g.GlowUnit >= 0;
                Console.WriteLine($"    {(same ? "PASS" : "FAIL")} app load: HdGun {g.BoneCount} bones, {g.Materials.Length} draws, {g.Anims.Count} animations "
                    + $"(key stride {g.KeyStride}), glow unit {g.GlowUnit}, scale {g.Scale.ToString(CultureInfo.InvariantCulture)}, muzzle {g.Muzzle}; GxJson {gx.Count} materials");
                all &= same;
            }
            catch (Exception e)
            {
                Console.WriteLine($"    FAIL app load: {e.GetType().Name}: {e.Message}");
                all = false;
            }
            return all;
        }

        // ---- gun.bin, MPHGUN2

        sealed class GunBin2
        {
            public int[] Parent = Array.Empty<int>();
            public float[] InvBind = Array.Empty<float>();
            public string[] Names = Array.Empty<string>();
            public readonly List<(int[] Palette, int Count, float[] Verts)> Mats = new();
            public readonly List<(string Name, float Duration, float Tick, int Keys, float[] Frames)> Anims = new();
            public long Trailing;
        }

        static GunBin2 ParseGunBin2(string path)
        {
            using var r = new BinaryReader(File.OpenRead(path));
            string magic = Encoding.ASCII.GetString(r.ReadBytes(8));
            if (magic != "MPHGUN2\0") throw new InvalidDataException($"magic {magic.TrimEnd('\0')}");
            var g = new GunBin2();
            string Str() => Encoding.Latin1.GetString(r.ReadBytes(r.ReadByte()));
            float[] Floats(long n)
            {
                var f = new float[n];
                for (long i = 0; i < n; i++) f[i] = r.ReadSingle();
                return f;
            }
            int nb = r.ReadInt32();
            g.Parent = new int[nb]; g.InvBind = new float[nb * 16]; g.Names = new string[nb];
            for (int i = 0; i < nb; i++)
            {
                g.Parent[i] = r.ReadInt32();
                for (int k = 0; k < 16; k++) g.InvBind[16 * i + k] = r.ReadSingle();
                g.Names[i] = Str();
            }
            int nm = r.ReadInt32();
            for (int m = 0; m < nm; m++)
            {
                var pal = new int[r.ReadInt32()];
                for (int k = 0; k < pal.Length; k++) pal[k] = r.ReadInt32();
                int count = r.ReadInt32();
                g.Mats.Add((pal, count, Floats((long)count * HdGun.FloatsPerVertex)));
            }
            int na = r.ReadInt32();
            for (int a = 0; a < na; a++)
            {
                string name = Str();
                float dur = r.ReadSingle(), tick = r.ReadSingle();
                int keys = r.ReadInt32();
                g.Anims.Add((name, dur, tick, keys, Floats((long)keys * nb * 10)));
            }
            g.Trailing = r.BaseStream.Length - r.BaseStream.Position;
            return g;
        }

        static (bool, string) CompareGunBin2(string refPath, string outPath, bool animFloatsExact)
        {
            GunBin2 a, b;
            try
            {
                a = ParseGunBin2(refPath);
                b = ParseGunBin2(outPath);
            }
            catch (Exception e)
            {
                return (false, $"parse error {e.Message}");
            }
            var errs = new List<string>();
            var bones = new FloatStats();
            var verts = new FloatStats();
            var anims = new FloatStats();
            if (a.Trailing != 0 || b.Trailing != 0) errs.Add($"trailing bytes {a.Trailing} / {b.Trailing}");
            if (a.Parent.Length != b.Parent.Length) errs.Add($"bones {a.Parent.Length} vs {b.Parent.Length}");
            else
            {
                for (int i = 0; i < a.Parent.Length; i++)
                {
                    if (a.Parent[i] != b.Parent[i]) errs.Add($"bone {i} parent {a.Parent[i]} vs {b.Parent[i]}");
                    if (a.Names[i] != b.Names[i]) errs.Add($"bone {i} name {a.Names[i]} vs {b.Names[i]}");
                }
                for (int i = 0; i < a.InvBind.Length; i++) bones.Add(a.InvBind[i], b.InvBind[i], $"bone {i / 16} inverse bind [{i % 16}]");
            }
            if (a.Mats.Count != b.Mats.Count) errs.Add($"draws {a.Mats.Count} vs {b.Mats.Count}");
            else
            {
                for (int m = 0; m < a.Mats.Count; m++)
                {
                    if (!a.Mats[m].Palette.SequenceEqual(b.Mats[m].Palette)) errs.Add($"draw {m} palette [{String.Join(",", a.Mats[m].Palette)}] vs [{String.Join(",", b.Mats[m].Palette)}]");
                    if (a.Mats[m].Count != b.Mats[m].Count) { errs.Add($"draw {m} vertices {a.Mats[m].Count} vs {b.Mats[m].Count}"); continue; }
                    for (int i = 0; i < a.Mats[m].Verts.Length; i++) verts.Add(a.Mats[m].Verts[i], b.Mats[m].Verts[i], $"draw {m} vertex {i / 19} float {i % 19}");
                }
            }
            int nb = a.Parent.Length;
            if (a.Anims.Count != b.Anims.Count) errs.Add($"animations {a.Anims.Count} vs {b.Anims.Count}");
            else
            {
                for (int k = 0; k < a.Anims.Count; k++)
                {
                    var x = a.Anims[k];
                    var y = b.Anims[k];
                    if (x.Name != y.Name) errs.Add($"animation {k} name {x.Name} vs {y.Name}");
                    if (x.Keys != y.Keys) { errs.Add($"animation {x.Name} keys {x.Keys} vs {y.Keys}"); continue; }
                    anims.Add(x.Duration, y.Duration, $"{x.Name} duration");
                    anims.Add(x.Tick, y.Tick, $"{x.Name} tick");
                    for (int i = 0; i < x.Frames.Length; i++) anims.Add(x.Frames[i], y.Frames[i], $"{x.Name} key {i / 10 / nb} bone {i / 10 % nb} float {i % 10}");
                }
            }
            foreach ((string what, FloatStats fs) in new[] { ("inverse binds", bones), ("vertices", verts), ("animations", anims) })
            {
                if (fs.Bad > 0) errs.Add($"{what}: {fs.Bad} floats out of tolerance, first {fs.FirstBad}");
            }
            if (animFloatsExact && anims.Exact != anims.Count) errs.Add($"animations: {anims.Count - anims.Exact} floats not bit-identical");
            bool bytes = File.ReadAllBytes(refPath).AsSpan().SequenceEqual(File.ReadAllBytes(outPath));
            static string S(FloatStats f) => $"{f.Exact}/{f.Count} bit-identical, max |d| {f.MaxAbs:G3} rel {f.MaxRel:G3}";
            string info = $"{nb} bones, {a.Mats.Count} draws ({a.Mats.Sum(m => m.Count)} vertices), {a.Anims.Count} animations "
                + $"({a.Anims.Sum(x => x.Keys)} keys); inverse binds {S(bones)}; vertices {S(verts)}; animations {S(anims)}" + (bytes ? "; file byte-identical" : "");
            if (errs.Count > 0) info += "; " + String.Join("; ", errs.Take(8));
            return (errs.Count == 0, info);
        }

        // ---- gun.cfg: equal lines, "root" within 1e-5

        static (bool, string) CompareCfg4(string refPath, string outPath)
        {
            static List<string> Lines(string p)
            {
                var l = File.ReadAllText(p).Replace("\r\n", "\n").Split('\n').Select(x => x.TrimEnd()).ToList();
                while (l.Count > 0 && l[^1].Length == 0) l.RemoveAt(l.Count - 1);
                return l;
            }
            List<string> a = Lines(refPath), b = Lines(outPath);
            var errs = new List<string>();
            double maxd = 0;
            int sameText = 0;
            if (a.Count != b.Count) errs.Add($"{a.Count} vs {b.Count} lines");
            for (int i = 0; i < Math.Min(a.Count, b.Count); i++)
            {
                if (a[i] == b[i])
                {
                    sameText++;
                    continue;
                }
                string[] ta = a[i].Split(' ', StringSplitOptions.RemoveEmptyEntries), tb = b[i].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (ta.Length == 17 && tb.Length == 17 && ta[0] == "root" && tb[0] == "root")
                {
                    for (int k = 1; k < 17; k++)
                    {
                        double d = Math.Abs(Double.Parse(ta[k], CultureInfo.InvariantCulture) - Double.Parse(tb[k], CultureInfo.InvariantCulture));
                        maxd = Math.Max(maxd, d);
                        if (!(d <= 1e-5)) errs.Add($"root[{k - 1}] {ta[k]} vs {tb[k]}");
                    }
                }
                else
                {
                    errs.Add($"'{a[i]}' vs '{b[i]}'");
                }
            }
            string info = $"{sameText}/{a.Count} lines text-identical" + (sameText < a.Count ? $", root max |d| {maxd:G3}" : "") + " | " + String.Join(" | ", b);
            if (errs.Count > 0) info = String.Join("; ", errs.Take(8)) + " | " + info;
            return (errs.Count == 0, info);
        }
    }
}
