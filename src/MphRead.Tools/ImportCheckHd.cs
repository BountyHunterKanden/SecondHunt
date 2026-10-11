using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using MphRecomp.Assets;
using MphRecomp.Import;
using MphRecomp.Import.Hd;

namespace MphRead
{
    // -importcheck hd [Name|all] [--out dir] [--mp1 disc.iso] [--mp2 disc.iso] [--mp3 folder|disc] [--recipes dir|embedded] [-v]
    // The golden check (docs/IMPORT_PIPELINE.md rule 4) for the HD model folders (docs/CLEANROOM_QUEUE.md CR1): runs the
    // in-app "hd" importer (MphRecomp.Import.Hd, the same code the device runs) on the owner's own discs, then compares
    // every file with today's reference in brawl_extract/Converted/<Name> (made by prime_to_trophy.py):
    //   *.dae, *.joints.txt  the text with every number masked equal; the numbers within 1e-5 relative (1.5e-4 absolute
    //                        for the 4-decimal joints)
    //   *.gx.json, *.convert.json  structurally equal, numbers within 1e-5
    //   *.png                pixel-identical (decoded RGBA)
    //   *.retro.txt          the rig layout (Retro's skin per rig vertex), as the text compare above
    //   *.rigid.mphrig, *.retro.mphrig  the rig caches the app plays: vertices and bones equal, weights within 1e-5, bind
    //                        matrices within 1e-4, and the cache key valid for the imported .dae / layout
    // Files only the PC tools make (the converter's *.parts.npz, the base rig and the straight / clean / tpose / hinge
    // variants for the dev viewer) are listed as PC-ONLY, not failed. Then loads the result the way the app does.
    internal static partial class ImportCheck
    {
        static readonly string[] HdPcOnly = { ".parts.npz", ".straight.mphrig", ".clean.mphrig", ".tpose.mphrig", ".hinge.mphrig" };

        static void Hd(string[] args, string repo)
        {
            if (!ToolPaths.UseDesktopExtraction()) return;   // the rig fit reads MPH's own hunter models
            string Arg(string k, string d) { int i = Array.IndexOf(args, k); return i > 0 && i + 1 < args.Length ? args[i + 1] : d; }
            string which = args.Length >= 3 && !args[2].StartsWith("-") ? args[2] : "all";
            bool verbose = args.Contains("-v");
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
                ["mp2"] = Arg("--mp2", Path.Combine(repo, "brawl_extract", "prime", "echoes_retail.rvz")),
                // Prime 3: the Trilogy disc's extracted file tree (DATA/files, holding MP3/*.pak), or its image
                ["mp3"] = Arg("--mp3", Path.Combine(repo, "brawl_extract", "prime", "trilogy", "DATA", "files")),
            };
            string recipeArg = Arg("--recipes", Path.Combine(repo, "recipes"));
            RecipeStore store = recipeArg == "embedded" || !Directory.Exists(recipeArg) ? RecipeStore.Embedded() : RecipeStore.FromDirectory(recipeArg);
            List<ImportRecipe> recipes = store.All.Where(r => r.Kind == "hd" && (which == "all" || r.Name.Equals(which, StringComparison.OrdinalIgnoreCase))).ToList();
            Console.WriteLine($"  importcheck hd {which}: {recipes.Count} recipes from {store.Origin}; output {outRoot}");
            if (recipes.Count == 0) return;
            var svc = new ImportService(outRoot, store);
            var progress = new HdProgress { Verbose = verbose };
            foreach (IGrouping<string, ImportRecipe> game in recipes.GroupBy(r => r.Game))
            {
                string disc = discs.TryGetValue(game.Key, out string? d) ? d : "";
                if (!File.Exists(disc) && !Directory.Exists(disc))
                {
                    Console.WriteLine($"  [SKIP] {game.Key}: no disc at '{disc}'");
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
                Console.WriteLine($"  {game.Key} ({rep.DiscId}, {Path.GetFileName(disc)}): {rep.Items.Count(i => i.Status == ItemImportStatus.Complete)}/{rep.Items.Count} items imported in {sw.Elapsed.TotalSeconds:F2} s");
                foreach (ImportItemInfo it in rep.Items) Console.WriteLine($"    {it.Id,-24} {it.Status,-12} {it.Seconds,6:F2} s  {it.Message}");
            }
            Console.WriteLine();
            int pass = 0, later = 0;
            var failed = new List<string>();
            foreach (ImportRecipe r in recipes)
            {
                (bool ok, int lt) = CompareHd(r.Name, Path.Combine(refRoot, r.Name), Path.Combine(outRoot, "hd", r.Name), progress.Log("hd/" + r.Name));
                later += lt;
                if (ok) pass++;
                else failed.Add(r.Name);
            }
            Console.WriteLine($"  SUMMARY: {pass}/{recipes.Count} HD folders PASS ({later} PC-only reference files not imported)"
                + (failed.Count > 0 ? "; FAIL: " + String.Join(", ", failed) : ""));
        }

        sealed class HdProgress : IProgress<(string item, float fraction, string message)>
        {
            public bool Verbose;
            readonly Dictionary<string, List<string>> _log = new();
            public List<string> Log(string item) => _log.TryGetValue(item, out List<string>? l) ? l : new List<string>();
            public void Report((string item, float fraction, string message) v)
            {
                if (!_log.TryGetValue(v.item, out List<string>? l)) _log[v.item] = l = new List<string>();
                l.Add(v.message);
                if (Verbose) Console.WriteLine($"      [{v.fraction * 100,5:F1}%] {v.item}: {v.message}");
            }
        }

        // a .retro.txt that differs from the reference only by the stray points the importer reports joining their
        // neighbours' bone: the references made before prime_to_trophy.py's stray pass (2026-09-27) lack it
        static List<string> RunsOf(string path)
        {
            var own = new List<string>();
            foreach (string l in File.ReadAllLines(path))
            {
                string[] t = l.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (t.Length == 3 && t[0] == "run") own.AddRange(Enumerable.Repeat(t[1], Int32.Parse(t[2], CultureInfo.InvariantCulture)));
            }
            return own;
        }

        // ... or by rig vertices on coincident model points of different bones (a seam where Retro split the mesh): the
        // importer takes the lowest-index point and logs each such vertex with its candidate bones; the reference's KD-tree
        // took whichever point its tree reached first. A vertex counts as a tie only when it is logged and the reference's
        // bone and the import's are both among its candidates; every other difference must be exactly the stray pass.
        static bool StraysExplain(string refPath, string outPath, List<string> log, out string info)
        {
            List<string> a = RunsOf(refPath), b = RunsOf(outPath);
            var ties = new Dictionary<int, string[]>();
            string? tieLine = log.FirstOrDefault(l => l.Contains("coincident model points"));
            if (tieLine != null)
            {
                foreach (Match m in Regex.Matches(tieLine[(tieLine.IndexOf(':') + 1)..], @"(\d+) (\S+?)(?:,|$)"))
                {
                    ties[Int32.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)] = m.Groups[2].Value.Split('/');
                }
            }
            var diff = new Dictionary<string, int>();
            int tied = 0;
            for (int i = 0; i < Math.Min(a.Count, b.Count); i++)
            {
                if (a[i] == b[i]) continue;
                if (ties.TryGetValue(i, out string[]? c) && c.Contains(a[i]) && c.Contains(b[i])) tied++;
                else diff[$"{a[i]}->{b[i]}"] = diff.GetValueOrDefault($"{a[i]}->{b[i]}") + 1;
            }
            var want = new Dictionary<string, int>();
            string? line = log.FirstOrDefault(l => l.Contains("stray points joined"));
            if (line != null)
            {
                foreach (Match m in Regex.Matches(line[(line.IndexOf(':') + 1)..], @"(\S+->\S+) (\d+)")) want[m.Groups[1].Value] = Int32.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
            }
            info = $"{diff.Values.Sum() + tied} vertices differ ({String.Join(", ", diff.Select(x => $"{x.Key} {x.Value}"))}"
                + (tied > 0 ? $"{(diff.Count > 0 ? ", " : "")}{tied} at coincident points" : "") + $"); importer's stray pass: {line ?? "none"}"
                + (tieLine != null ? $"; {tieLine}" : "");
            bool straysOk = diff.Count == 0 || (diff.Count == want.Count && diff.All(x => want.TryGetValue(x.Key, out int n) && n == x.Value));
            return a.Count == b.Count && diff.Values.Sum() + tied > 0 && straysOk;
        }

        static (bool, int) CompareHd(string name, string refDir, string outDir, List<string> log)
        {
            Console.WriteLine($"  == {name}");
            if (!Directory.Exists(refDir))
            {
                Console.WriteLine($"    FAIL no reference folder {refDir}");
                return (false, 0);
            }
            if (!Directory.Exists(outDir))
            {
                Console.WriteLine($"    FAIL nothing imported into {outDir}");
                return (false, 0);
            }
            bool all = true;
            int later = 0;
            // a layout that differs only by the stray pass: rebuild the retro rig from the REFERENCE layout and require it
            // byte-identical to the reference rig (the rig build is then proven; only its input moved)
            string stem = "Dol" + name;
            bool strayOnly = false;
            string rtx = Path.Combine(refDir, stem + ".retro.txt"), otx = Path.Combine(outDir, stem + ".retro.txt");
            if (File.Exists(rtx) && File.Exists(otx) && !File.ReadAllBytes(rtx).AsSpan().SequenceEqual(File.ReadAllBytes(otx)))
            {
                strayOnly = StraysExplain(rtx, otx, log, out string why);
                Console.WriteLine($"    {(strayOnly ? "NOTE" : "FAIL")} {stem}.retro.txt: {why}");
                if (strayOnly)
                {
                    string tmp = Path.Combine(Path.GetTempPath(), "mph-importcheck-refit", name);
                    if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
                    Directory.CreateDirectory(tmp);
                    foreach (string f in Directory.GetFiles(outDir)) if (!f.EndsWith(".retro.mphrig", StringComparison.Ordinal)) File.Copy(f, Path.Combine(tmp, Path.GetFileName(f)));
                    File.Copy(rtx, Path.Combine(tmp, stem + ".retro.txt"), true);
                    HdRigBuild.Refit(Path.Combine(tmp, stem + ".dae"), name, "retro");
                    (bool rok, string rinfo) = CompareRig(Path.Combine(refDir, stem + ".retro.mphrig"), Path.Combine(tmp, stem + ".retro.mphrig"));
                    Console.WriteLine($"    {(rok ? "PASS" : "FAIL")} retro rig rebuilt from the reference layout: {rinfo}");
                    strayOnly = rok;
                }
                all &= strayOnly;
            }
            var outFiles = new HashSet<string>(Directory.GetFiles(outDir).Select(f => Path.GetFileName(f)!));
            foreach (string f in Directory.GetFiles(refDir).Select(f => Path.GetFileName(f)!).OrderBy(x => x, StringComparer.Ordinal))
            {
                string a = Path.Combine(refDir, f), b = Path.Combine(outDir, f);
                if (!outFiles.Remove(f))
                {
                    if (HdPcOnly.Any(x => f.EndsWith(x, StringComparison.OrdinalIgnoreCase)) || f == name.Insert(0, "Dol") + ".mphrig")
                    {
                        later++;
                        continue;
                    }
                    Console.WriteLine($"    FAIL {f}: not produced");
                    all = false;
                    continue;
                }
                (bool ok, string info) = f switch
                {
                    _ when f.EndsWith(".json", StringComparison.OrdinalIgnoreCase) => CompareJson(a, b),
                    _ when f.EndsWith(".png", StringComparison.OrdinalIgnoreCase) => ComparePng(a, b),
                    _ when f.EndsWith(".joints.txt", StringComparison.OrdinalIgnoreCase) => CompareNumbered(a, b, 1e-5, 1.5e-4),
                    _ when f.EndsWith(".retro.txt", StringComparison.OrdinalIgnoreCase) => strayOnly ? (true, "the stray pass / coincident points only (see NOTE)") : CompareNumbered(a, b, 0, 0),
                    _ when strayOnly && f.EndsWith(".retro.mphrig", StringComparison.OrdinalIgnoreCase) => (true, "built from the newer layout (see NOTE); " + CompareRig(a, b).Item2),
                    _ when f.EndsWith(".mphrig", StringComparison.OrdinalIgnoreCase) => CompareRig(a, b),
                    _ when f.EndsWith(".dae", StringComparison.OrdinalIgnoreCase) => CompareNumbered(a, b, 1e-5, 1e-9),
                    _ => (File.ReadAllBytes(a).AsSpan().SequenceEqual(File.ReadAllBytes(b)), "bytes"),
                };
                if (!ok || !f.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) Console.WriteLine($"    {(ok ? "PASS" : "FAIL")} {f}: {info}");
                all &= ok;
            }
            int pngs = Directory.GetFiles(outDir, "*.png").Length;
            Console.WriteLine($"    {pngs} PNGs checked" + (later > 0 ? $"; PC-ONLY {later} (parts.npz, base rig, dev-viewer variants)" : ""));
            foreach (string extra in outFiles.OrderBy(x => x, StringComparer.Ordinal))
            {
                Console.WriteLine($"    FAIL {extra}: produced but not in the reference");
                all = false;
            }
            try
            {
                string dae = Directory.GetFiles(outDir, "*.dae").Single();
                DaeModel model = DaeModel.Load(dae);
                List<GxMaterial> gx = GxJson.Read(Path.ChangeExtension(dae, ".gx.json"));
                Console.WriteLine($"    PASS app load: DaeModel {model.Meshes.Count} meshes, {model.TriangleCount} triangles, size "
                    + $"{model.Size.X.ToString("F2", CultureInfo.InvariantCulture)} x {model.Size.Y.ToString("F2", CultureInfo.InvariantCulture)} x {model.Size.Z.ToString("F2", CultureInfo.InvariantCulture)}; GxJson {gx.Count} materials");
            }
            catch (Exception e)
            {
                Console.WriteLine($"    FAIL app load: {e.GetType().Name}: {e.Message}");
                all = false;
            }
            Console.WriteLine($"    => {(all ? "PASS" : "FAIL")} {name}");
            return (all, later);
        }

        // a rig cache: what the app reads from it, compared with tolerance; and whether the app accepts the imported one
        static (bool, string) CompareRig(string refPath, string outPath)
        {
            MphRecomp.Anim.TrophyRig? ra = MphRecomp.Anim.TrophyRigIO.Load(refPath, 0, anyKey: true), rb = MphRecomp.Anim.TrophyRigIO.Load(outPath, 0, anyKey: true);
            if (ra == null || rb == null) return (false, ra == null ? "reference unreadable" : "import unreadable");
            if (ra.Vertices.Length != rb.Vertices.Length) return (false, $"{ra.Vertices.Length} vs {rb.Vertices.Length} vertices");
            if (ra.BindWorld.Length != rb.BindWorld.Length) return (false, $"{ra.BindWorld.Length} vs {rb.BindWorld.Length} nodes");
            int bones = 0;
            for (int i = 0; i < ra.Bones.Length; i++) if (ra.Bones[i] != rb.Bones[i] && (ra.Weights[i] > 1e-6f || rb.Weights[i] > 1e-6f)) bones++;
            double wd = 0, md = 0, vd = 0;
            for (int i = 0; i < ra.Weights.Length; i++) wd = Math.Max(wd, Math.Abs(ra.Weights[i] - rb.Weights[i]));
            for (int i = 0; i < ra.BindWorld.Length; i++)
            {
                for (int r = 0; r < 4; r++) for (int c = 0; c < 4; c++) md = Math.Max(md, Math.Abs(ra.BindWorld[i][r, c] - rb.BindWorld[i][r, c]));
            }
            for (int i = 0; i < ra.Vertices.Length; i++) vd = Math.Max(vd, (ra.Vertices[i] - rb.Vertices[i]).Length);
            string dir = Path.GetDirectoryName(outPath)!;
            string dae = Directory.GetFiles(dir, "*.dae").Single();
            string key = Path.GetFileName(outPath).Split('.')[^2];
            var v = MphRecomp.Anim.TrophyRigIO.Variants.First(x => x.Key == key);
            bool accepted = MphRecomp.Anim.TrophyRigIO.Load(outPath, MphRecomp.Anim.TrophyRigIO.VariantKey(dae, v)) != null;
            bool same = File.ReadAllBytes(refPath).AsSpan().SequenceEqual(File.ReadAllBytes(outPath));
            bool ok = bones == 0 && wd <= 1e-5 && md <= 1e-4 && vd <= 1e-5 && accepted;
            return (ok, (same ? "byte-identical; " : "") + $"{ra.Vertices.Length} vertices, {bones} bone slots differ, max |d| weight {wd:G3} bind {md:G3} vertex {vd:G3}; "
                + (accepted ? "key valid for the import" : "KEY REJECTED by the app"));
        }

        static readonly Regex Number = new(@"-?(?:\d+\.?\d*|\.\d+)(?:[eE][+-]?\d+)?", RegexOptions.CultureInvariant);

        // the text with every number masked must match exactly; then the numbers pair up within tolerance
        static (bool, string) CompareNumbered(string refPath, string outPath, double rel, double abs)
        {
            string ta = File.ReadAllText(refPath).Replace("\r\n", "\n"), tb = File.ReadAllText(outPath).Replace("\r\n", "\n");
            if (ta == tb) return (true, "identical apart from line endings");
            var na = new List<string>();
            var nb = new List<string>();
            string ma = Number.Replace(ta, m => { na.Add(m.Value); return "#"; });
            string mb = Number.Replace(tb, m => { nb.Add(m.Value); return "#"; });
            if (ma != mb)
            {
                int i = 0;
                while (i < ma.Length && i < mb.Length && ma[i] == mb[i]) i++;
                string Around(string s) => s.Substring(Math.Max(0, i - 40), Math.Min(80, s.Length - Math.Max(0, i - 40))).Replace("\n", "\\n");
                return (false, $"text differs at char {i}: ref \"{Around(ma)}\" vs \"{Around(mb)}\"");
            }
            double maxd = 0;
            int diffs = 0, textDiffs = 0;
            string first = "";
            for (int i = 0; i < na.Count; i++)
            {
                if (na[i] == nb[i]) continue;
                textDiffs++;
                double x = Double.Parse(na[i], CultureInfo.InvariantCulture), y = Double.Parse(nb[i], CultureInfo.InvariantCulture);
                double d = Math.Abs(x - y);
                maxd = Math.Max(maxd, d);
                if (d > abs && d > rel * Math.Max(Math.Abs(x), Math.Abs(y)))
                {
                    if (diffs == 0) first = $"number {i}: {na[i]} vs {nb[i]}";
                    diffs++;
                }
            }
            string info = $"{na.Count} numbers, {textDiffs} written differently, max |d| {maxd:G3}";
            if (diffs > 0) info = $"{diffs} numbers out of tolerance (first {first}); " + info;
            return (diffs == 0, info);
        }
    }
}
