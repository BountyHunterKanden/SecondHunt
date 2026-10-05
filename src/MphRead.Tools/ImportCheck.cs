using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using MphRecomp.Assets;
using MphRecomp.Campaign;
using MphRecomp.Import;

namespace MphRead
{
    // -importcheck guns [Suit|all] [--out dir] [--mp1 disc.iso] [--mp2 disc.iso] [--mp3 folder] [--recipes dir|embedded] [-v]
    // The golden check of docs/IMPORT_PIPELINE.md (rule 4): runs the in-app importer (MphRecomp.Import, the same code
    // the device runs) on the owner's own discs into a scratch folder, then compares every file with today's reference
    // outputs in brawl_extract/Converted/guns/<Suit> (made by gun_export.py; the MP3 suits by gun_export3.py). --mp3 is
    // the Trilogy disc's extracted file tree (a folder holding MP3/*.pak; the Wii disc readers are not wired in yet):
    //   gun.bin      parsed: bones, palettes and structure equal; floats within 1e-4 relative (1e-6 absolute near zero)
    //   gun.gx.json  parsed: structurally equal, numbers within 1e-5
    //   gun.cfg      equal lines
    //   *.png        pixel-identical (decoded RGBA)
    // and loads the result the way the app does (HdGun.Load, GxJson.Read). Nothing is written outside --out (default:
    // a fresh folder under %TEMP%\mph-importcheck). "--recipes embedded" uses the recipes built into MphRecomp.Core.
    // -importcheck readers <disc.iso> [--echoes] ...: the Retro reader sweep (ImportCheckReaders.cs).
    // -importcheck readers3 [folder] ...: the same for Corruption's formats (ImportCheckReaders3.cs).
    // -importcheck guns4 [Suit|all] ...: the Beyond (game "mp4") guns, built by Gun4Builder (ImportCheckGuns4.cs); "guns"
    // leaves them out.
    internal static partial class ImportCheck
    {
        sealed class SyncProgress : IProgress<(string item, float fraction, string message)>
        {
            public bool Verbose;
            public void Report((string item, float fraction, string message) v)
            {
                if (Verbose) Console.WriteLine($"      [{v.fraction * 100,5:F1}%] {v.item}: {v.message}");
            }
        }

        public static void Run(string[] args)
        {
            string repo = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
            string Arg(string k, string d) { int i = Array.IndexOf(args, k); return i > 0 && i + 1 < args.Length ? args[i + 1] : d; }
            string kind = args.Length >= 2 && !args[1].StartsWith("-") ? args[1] : "guns";
            string which = args.Length >= 3 && !args[2].StartsWith("-") ? args[2] : "all";
            bool verbose = args.Contains("-v");
            if (kind == "readers")
            {
                Readers(args, repo);
                return;
            }
            if (kind == "readers3")
            {
                Readers3(args, repo);
                return;
            }
            if (kind == "beyond-anims") { BeyondAnims(args, repo); return; }   // ImportCheckBeyond.cs
            if (kind == "guns4") { Guns4(args, repo); return; }                // ImportCheckGuns4.cs
            if (kind != "guns")
            {
                Console.WriteLine($"  -importcheck: unknown item kind '{kind}' (only 'guns', 'guns4', or 'readers' for the reader sweep)");
                return;
            }
            string refRoot = Path.Combine(repo, "brawl_extract", "Converted");
            string outRoot = Path.GetFullPath(Arg("--out", Path.Combine(Path.GetTempPath(), "mph-importcheck", DateTime.Now.ToString("yyyyMMdd-HHmmss"))));
            if (outRoot.StartsWith(Path.GetFullPath(refRoot), StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine($"  -importcheck: --out {outRoot} is inside the reference outputs; pick another folder");
                return;
            }
            var discs = new Dictionary<string, string>
            {
                ["mp1"] = Arg("--mp1", Path.Combine(repo, "brawl_extract", "prime", "mp1.iso")),
                ["mp2"] = Arg("--mp2", Path.Combine(repo, "brawl_extract", "prime", "echoes_widescreen.iso")),
                ["mp3"] = Arg("--mp3", Path.Combine(repo, "brawl_extract", "prime", "trilogy", "DATA", "files")),
            };
            string recipeArg = Arg("--recipes", Path.Combine(repo, "recipes"));
            RecipeStore store = recipeArg == "embedded" || !Directory.Exists(recipeArg) ? RecipeStore.Embedded() : RecipeStore.FromDirectory(recipeArg);
            // the Beyond suits (game "mp4") have no disc import yet: -importcheck guns4 builds and checks them
            List<ImportRecipe> recipes = store.All.Where(r => r.Kind == kind && r.Game != "mp4" && (which == "all" || r.Name.Equals(which, StringComparison.OrdinalIgnoreCase))).ToList();
            Console.WriteLine($"  importcheck {kind} {which}: {recipes.Count} recipes from {store.Origin}; output {outRoot}");
            if (recipes.Count == 0)
            {
                Console.WriteLine($"  no {kind} recipe named '{which}'" + (store.All.Any(r => r.Game == "mp4" && r.Name.Equals(which, StringComparison.OrdinalIgnoreCase)) ? " (an MP4 suit: -importcheck guns4)" : ""));
                return;
            }
            var svc = new ImportService(outRoot, store);
            var progress = new SyncProgress { Verbose = verbose };
            var timings = new List<string>();
            foreach (IGrouping<string, ImportRecipe> game in recipes.GroupBy(r => r.Game))
            {
                string disc = discs.TryGetValue(game.Key, out string? d) ? d : "";
                if (!File.Exists(disc) && !Directory.Exists(disc))
                {
                    Console.WriteLine($"  [SKIP] {game.Key}: no disc (or extracted folder) at '{disc}'");
                    continue;
                }
                var sw = Stopwatch.StartNew();
                ImportReport rep;
                try
                {
                    rep = svc.Import(game.Key, disc, progress, default, game.Select(r => r.Id).ToList());
                }
                catch (Exception e) when (e is InvalidDataException or IOException or NotSupportedException)
                {
                    Console.WriteLine($"  [REFUSED] {game.Key} from {disc}: {e.Message}");
                    continue;
                }
                sw.Stop();
                string t = $"{game.Key} ({rep.DiscId}, {Path.GetFileName(disc)}): {rep.Items.Count(i => i.Status == ItemImportStatus.Complete)}/{rep.Items.Count} items imported in {sw.Elapsed.TotalSeconds:F2} s";
                timings.Add(t);
                Console.WriteLine("  " + t);
                foreach (ImportItemInfo it in rep.Items)
                {
                    Console.WriteLine($"    {it.Id,-22} {it.Status,-12} {it.Seconds,6:F2} s  {it.Message}");
                }
            }
            Console.WriteLine();
            int pass = 0;
            var failed = new List<string>();
            foreach (ImportRecipe r in recipes)
            {
                bool ok = CompareSuit(r.Name, Path.Combine(refRoot, kind, r.Name), Path.Combine(outRoot, kind, r.Name));
                if (ok) pass++;
                else failed.Add(r.Name);
            }
            Console.WriteLine();
            foreach (ImportGameInfo g in svc.Games)
            {
                Console.WriteLine($"  status {g.Id} ({g.Title}): {g.Status}; " + String.Join(", ", g.Items.Select(i => $"{i.Name} {i.Status}")));
            }
            foreach (string t in timings) Console.WriteLine("  time " + t);
            Console.WriteLine($"  SUMMARY: {pass}/{recipes.Count} suits PASS" + (failed.Count > 0 ? "; FAIL: " + String.Join(", ", failed) : ""));
        }

        static bool CompareSuit(string suit, string refDir, string outDir)
        {
            Console.WriteLine($"  == {suit}");
            if (!Directory.Exists(refDir))
            {
                Console.WriteLine($"    FAIL no reference folder {refDir}");
                return false;
            }
            if (!Directory.Exists(outDir))
            {
                Console.WriteLine($"    FAIL nothing imported into {outDir}");
                return false;
            }
            bool all = true;
            var refFiles = Directory.GetFiles(refDir).Select(Path.GetFileName).OrderBy(x => x, StringComparer.Ordinal).ToList();
            var outFiles = new HashSet<string>(Directory.GetFiles(outDir).Select(f => Path.GetFileName(f)!));
            foreach (string? f in refFiles)
            {
                if (f == null) continue;
                string a = Path.Combine(refDir, f), b = Path.Combine(outDir, f);
                if (!outFiles.Remove(f))
                {
                    Console.WriteLine($"    FAIL {f}: not produced");
                    all = false;
                    continue;
                }
                (bool ok, string info) = f switch
                {
                    "gun.bin" => CompareGunBin(a, b),
                    "gun.gx.json" => CompareJson(a, b),
                    "gun.cfg" => CompareLines(a, b),
                    _ when f.EndsWith(".png", StringComparison.OrdinalIgnoreCase) => ComparePng(a, b),
                    _ => (File.ReadAllBytes(a).AsSpan().SequenceEqual(File.ReadAllBytes(b)), "bytes"),
                };
                Console.WriteLine($"    {(ok ? "PASS" : "FAIL")} {f}: {info}");
                all &= ok;
            }
            foreach (string extra in outFiles.OrderBy(x => x, StringComparer.Ordinal))
            {
                Console.WriteLine($"    FAIL {extra}: produced but not in the reference");
                all = false;
            }
            // the app's own loaders on the result
            try
            {
                HdGun g = HdGun.Load(Path.Combine(outDir, "gun.bin"));
                List<GxMaterial> gx = GxJson.Read(Path.Combine(outDir, "gun.gx.json"));
                bool same = gx.Count == g.Materials.Length;
                Console.WriteLine($"    {(same ? "PASS" : "FAIL")} app load: HdGun {g.BoneCount} bones, {g.Materials.Length} materials, {g.Anims.Count} animations, "
                    + $"lights [{String.Join(" ", g.LightMaterials)}] scale {g.Scale.ToString(CultureInfo.InvariantCulture)}; GxJson {gx.Count} materials");
                all &= same;
            }
            catch (Exception e)
            {
                Console.WriteLine($"    FAIL app load: {e.GetType().Name}: {e.Message}");
                all = false;
            }
            Console.WriteLine($"    => {(all ? "PASS" : "FAIL")} {suit}");
            return all;
        }

        // ---- gun.bin

        sealed class GunBin
        {
            public string Magic = "";
            public int[] Parent = Array.Empty<int>();
            public float[] Bind = Array.Empty<float>();
            public string[] Names = Array.Empty<string>();
            public readonly List<(int[] Palette, int Count, float[] Verts)> Mats = new();
            public readonly List<(string Name, float Duration, float Tick, int Keys, float[] Frames)> Anims = new();
            public long Trailing;
        }

        static GunBin ParseGunBin(string path)
        {
            using var r = new BinaryReader(File.OpenRead(path));
            var g = new GunBin { Magic = Encoding.ASCII.GetString(r.ReadBytes(8)) };
            if (g.Magic != "MPHGUN1\0") throw new InvalidDataException($"magic {g.Magic.TrimEnd('\0')}");
            string Str() => Encoding.Latin1.GetString(r.ReadBytes(r.ReadByte()));
            float[] Floats(long n)
            {
                var f = new float[n];
                for (long i = 0; i < n; i++) f[i] = r.ReadSingle();
                return f;
            }
            int nb = r.ReadInt32();
            g.Parent = new int[nb]; g.Bind = new float[nb * 3]; g.Names = new string[nb];
            for (int i = 0; i < nb; i++)
            {
                g.Parent[i] = r.ReadInt32();
                g.Bind[3 * i] = r.ReadSingle(); g.Bind[3 * i + 1] = r.ReadSingle(); g.Bind[3 * i + 2] = r.ReadSingle();
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
                g.Anims.Add((name, dur, tick, keys, Floats((long)keys * nb * 7)));
            }
            g.Trailing = r.BaseStream.Length - r.BaseStream.Position;
            return g;
        }

        sealed class FloatStats
        {
            public long Count, Exact, Bad;
            public double MaxAbs, MaxRel;
            public string FirstBad = "";

            public void Add(float a, float b, string where)
            {
                Count++;
                if (a.Equals(b)) { Exact++; return; }
                double d = Math.Abs((double)a - b), m = Math.Max(Math.Abs((double)a), Math.Abs((double)b));
                MaxAbs = Math.Max(MaxAbs, d);
                if (m > 0) MaxRel = Math.Max(MaxRel, d / m);
                if (!(d <= Math.Max(1e-4 * m, 1e-6)))
                {
                    if (Bad == 0) FirstBad = $"{where}: {a.ToString("R", CultureInfo.InvariantCulture)} vs {b.ToString("R", CultureInfo.InvariantCulture)}";
                    Bad++;
                }
            }
        }

        static (bool, string) CompareGunBin(string refPath, string outPath)
        {
            GunBin a, b;
            try
            {
                a = ParseGunBin(refPath);
                b = ParseGunBin(outPath);
            }
            catch (Exception e)
            {
                return (false, $"parse error {e.Message}");
            }
            var errs = new List<string>();
            var fs = new FloatStats();
            if (a.Trailing != 0 || b.Trailing != 0) errs.Add($"trailing bytes {a.Trailing} / {b.Trailing}");
            if (a.Parent.Length != b.Parent.Length) errs.Add($"bones {a.Parent.Length} vs {b.Parent.Length}");
            else
            {
                for (int i = 0; i < a.Parent.Length; i++)
                {
                    if (a.Parent[i] != b.Parent[i]) errs.Add($"bone {i} parent {a.Parent[i]} vs {b.Parent[i]}");
                    if (a.Names[i] != b.Names[i]) errs.Add($"bone {i} name {a.Names[i]} vs {b.Names[i]}");
                }
                for (int i = 0; i < a.Bind.Length; i++) fs.Add(a.Bind[i], b.Bind[i], $"bind {i / 3}");
            }
            if (a.Mats.Count != b.Mats.Count) errs.Add($"materials {a.Mats.Count} vs {b.Mats.Count}");
            else
            {
                for (int m = 0; m < a.Mats.Count; m++)
                {
                    if (!a.Mats[m].Palette.SequenceEqual(b.Mats[m].Palette)) errs.Add($"material {m} palette [{String.Join(",", a.Mats[m].Palette)}] vs [{String.Join(",", b.Mats[m].Palette)}]");
                    if (a.Mats[m].Count != b.Mats[m].Count) { errs.Add($"material {m} vertices {a.Mats[m].Count} vs {b.Mats[m].Count}"); continue; }
                    for (int i = 0; i < a.Mats[m].Verts.Length; i++) fs.Add(a.Mats[m].Verts[i], b.Mats[m].Verts[i], $"material {m} vertex {i / 19} float {i % 19}");
                }
            }
            if (a.Anims.Count != b.Anims.Count) errs.Add($"animations {a.Anims.Count} vs {b.Anims.Count}");
            else
            {
                for (int k = 0; k < a.Anims.Count; k++)
                {
                    var x = a.Anims[k];
                    var y = b.Anims[k];
                    if (x.Name != y.Name) errs.Add($"animation {k} name {x.Name} vs {y.Name}");
                    if (x.Keys != y.Keys) { errs.Add($"animation {x.Name} keys {x.Keys} vs {y.Keys}"); continue; }
                    fs.Add(x.Duration, y.Duration, $"{x.Name} duration");
                    fs.Add(x.Tick, y.Tick, $"{x.Name} tick");
                    for (int i = 0; i < x.Frames.Length; i++) fs.Add(x.Frames[i], y.Frames[i], $"{x.Name} key {i / 7 / a.Parent.Length} bone {i / 7 % a.Parent.Length} float {i % 7}");
                }
            }
            if (fs.Bad > 0) errs.Add($"{fs.Bad} floats out of tolerance, first {fs.FirstBad}");
            bool bytes = File.ReadAllBytes(refPath).AsSpan().SequenceEqual(File.ReadAllBytes(outPath));
            string info = $"{a.Parent.Length} bones, {a.Mats.Count} materials ({a.Mats.Sum(m => m.Count)} vertices), {a.Anims.Count} animations; "
                + $"{fs.Count} floats, {fs.Exact} bit-identical, max |d| {fs.MaxAbs:G3} (rel {fs.MaxRel:G3})" + (bytes ? "; file byte-identical" : "");
            if (errs.Count > 0) info += "; " + String.Join("; ", errs.Take(8));
            return (errs.Count == 0, info);
        }

        // ---- gun.gx.json

        static (bool, string) CompareJson(string refPath, string outPath)
        {
            string ta = File.ReadAllText(refPath), tb = File.ReadAllText(outPath);
            var errs = new List<string>();
            double maxd = 0;
            try
            {
                using JsonDocument a = JsonDocument.Parse(ta), b = JsonDocument.Parse(tb);
                Walk(a.RootElement, b.RootElement, "$", errs, ref maxd);
            }
            catch (JsonException e)
            {
                return (false, "parse error " + e.Message);
            }
            bool bytes = ta.Replace("\r\n", "\n") == tb.Replace("\r\n", "\n");
            string info = $"structurally equal, max |d| {maxd:G3}" + (bytes ? "; byte-identical apart from line endings" : "; text differs");
            if (errs.Count > 0) info = $"{errs.Count} differences: " + String.Join("; ", errs.Take(8));
            return (errs.Count == 0, info);
        }

        static void Walk(JsonElement a, JsonElement b, string path, List<string> errs, ref double maxd)
        {
            if (a.ValueKind != b.ValueKind && !(IsBool(a) && IsBool(b)))
            {
                errs.Add($"{path}: {a.ValueKind} vs {b.ValueKind}");
                return;
            }
            switch (a.ValueKind)
            {
            case JsonValueKind.Object:
                {
                    var ka = a.EnumerateObject().Select(p => p.Name).ToList();
                    var kb = b.EnumerateObject().Select(p => p.Name).ToList();
                    if (!ka.OrderBy(x => x, StringComparer.Ordinal).SequenceEqual(kb.OrderBy(x => x, StringComparer.Ordinal)))
                    {
                        errs.Add($"{path}: keys [{String.Join(",", ka)}] vs [{String.Join(",", kb)}]");
                        return;
                    }
                    foreach (string k in ka) Walk(a.GetProperty(k), b.GetProperty(k), path + "." + k, errs, ref maxd);
                    break;
                }
            case JsonValueKind.Array:
                {
                    int na = a.GetArrayLength(), nb = b.GetArrayLength();
                    if (na != nb)
                    {
                        errs.Add($"{path}: {na} vs {nb} elements");
                        return;
                    }
                    for (int i = 0; i < na; i++) Walk(a[i], b[i], $"{path}[{i}]", errs, ref maxd);
                    break;
                }
            case JsonValueKind.Number:
                {
                    double x = a.GetDouble(), y = b.GetDouble(), d = Math.Abs(x - y);
                    maxd = Math.Max(maxd, d);
                    if (!(d <= 1e-5)) errs.Add($"{path}: {a.GetRawText()} vs {b.GetRawText()}");
                    break;
                }
            case JsonValueKind.String:
                if (a.GetString() != b.GetString()) errs.Add($"{path}: \"{a.GetString()}\" vs \"{b.GetString()}\"");
                break;
            case JsonValueKind.True:
            case JsonValueKind.False:
                if (a.GetBoolean() != b.GetBoolean()) errs.Add($"{path}: {a.GetBoolean()} vs {b.GetBoolean()}");
                break;
            }
        }

        static bool IsBool(JsonElement e) => e.ValueKind is JsonValueKind.True or JsonValueKind.False;

        // ---- gun.cfg

        static (bool, string) CompareLines(string refPath, string outPath)
        {
            static List<string> Lines(string p)
            {
                var l = File.ReadAllText(p).Replace("\r\n", "\n").Split('\n').Select(x => x.TrimEnd()).ToList();
                while (l.Count > 0 && l[^1].Length == 0) l.RemoveAt(l.Count - 1);
                return l;
            }
            List<string> a = Lines(refPath), b = Lines(outPath);
            bool ok = a.SequenceEqual(b);
            return (ok, ok ? String.Join(" | ", a) : $"[{String.Join(" | ", a)}] vs [{String.Join(" | ", b)}]");
        }

        // ---- textures

        static (bool, string) ComparePng(string refPath, string outPath)
        {
            try
            {
                byte[] a = PngOut.Read(refPath, out int wa, out int ha), b = PngOut.Read(outPath, out int wb, out int hb);
                if (wa != wb || ha != hb) return (false, $"{wa}x{ha} vs {wb}x{hb}");
                int diff = 0;
                for (int i = 0; i < a.Length; i += 4)
                {
                    if (a[i] != b[i] || a[i + 1] != b[i + 1] || a[i + 2] != b[i + 2] || a[i + 3] != b[i + 3]) diff++;
                }
                return (diff == 0, diff == 0 ? $"{wa}x{ha} pixel-identical" : $"{wa}x{ha}, {diff} pixels differ");
            }
            catch (Exception e)
            {
                return (false, "decode error " + e.Message);
            }
        }
    }
}
