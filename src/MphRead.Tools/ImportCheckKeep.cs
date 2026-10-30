using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using MphRecomp.Import;

namespace MphRead
{
    // -importcheck keep <game> <disc|folder> [--out dir] [--only id,id] [--recipes dir|embedded] [-v]
    // The golden check of docs/IMPORT_RETENTION.md: imports the game with source retention on, reports what was kept
    // (size, whole files or ranges), then rebuilds EVERY item in a second scratch folder from the kept copy alone (the
    // disc is not opened) and compares the two item trees file by file, byte for byte. A rebuild that differs, or an
    // item that reads "needs the disc again", means an importer reads something its Sources() declaration or the keep
    // step missed. --only limits both runs to the listed item ids.
    internal static partial class ImportCheck
    {
        static void Keep(string[] args, string repo)
        {
            string Arg(string k, string d) { int i = Array.IndexOf(args, k); return i > 0 && i + 1 < args.Length ? args[i + 1] : d; }
            bool verbose = args.Contains("-v");
            if (args.Length < 4 || args[2].StartsWith("-") || args[3].StartsWith("-"))
            {
                Console.WriteLine("  usage: -importcheck keep <game: mp1|mp2|mp3|...> <disc image or folder> [--out dir] [--only id,id] [--recipes dir|embedded] [--prodkeys file [--title-keys file]] [-v]");
                return;
            }
            string game = args[2], disc = args[3];
            if (!ToolPaths.UseDesktopExtraction()) return;   // the hd items' rig fit reads MPH's own hunter models
            string outRoot = Path.GetFullPath(Arg("--out", Path.Combine(Path.GetTempPath(), "mph-importcheck-keep", game + "-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"))));
            string recipeArg = Arg("--recipes", Path.Combine(repo, "recipes"));
            RecipeStore store = recipeArg == "embedded" || !Directory.Exists(recipeArg) ? RecipeStore.Embedded() : RecipeStore.FromDirectory(recipeArg);
            string[]? only = Arg("--only", "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) is { Length: > 0 } o ? o : null;
            if (!File.Exists(disc) && !Directory.Exists(disc))
            {
                Console.WriteLine($"  [SKIP] no disc (or extracted folder) at '{disc}'");
                return;
            }
            string dirA = Path.Combine(outRoot, "disc"), dirB = Path.Combine(outRoot, "kept");
            Console.WriteLine($"  importcheck keep {game}: {disc}\n  scratch {outRoot}");
            var progress = new SyncProgress { Verbose = verbose };

            // 1. the normal import, retention on
            // a Switch game file (mp4) needs the player's own prod.keys; the kept rebuild reads plain files and needs none
            string prodKeys = Arg("--prodkeys", "");
            var svcA = new ImportService(dirA, store) { KeepSources = true };
            if (prodKeys.Length > 0) svcA.SwitchKeys = MphRecomp.Import.Switch.SwitchKeys.FromFile(prodKeys, Arg("--title-keys", null!));
            var sw = Stopwatch.StartNew();
            ImportReport repA = svcA.Import(game, disc, progress, default, only);
            sw.Stop();
            int doneA = repA.Items.Count(i => i.Status == ItemImportStatus.Complete);
            Console.WriteLine($"  disc import: {doneA}/{repA.Items.Count} items in {sw.Elapsed.TotalSeconds:F1} s");
            foreach (ImportItemInfo it in repA.Items.Where(i => i.Status != ItemImportStatus.Complete))
            {
                Console.WriteLine($"    {it.Id,-30} {it.Status,-12} {it.Message}");
            }
            if (repA.KeepError == null || repA.KeepError.Length > 0)
            {
                Console.WriteLine($"  FAIL keep: {(repA.KeepError ?? "the keep step didn't run")}");
                return;
            }
            string srcDir = SourceKeep.Dir(dirA, game);
            string filesDir = Path.Combine(srcDir, "files");
            int sparse = Directory.Exists(filesDir)
                ? Directory.GetFiles(filesDir, "*" + SourceKeep.RangesSuffix, SearchOption.AllDirectories).Length : 0;
            long fileCount = Directory.Exists(filesDir)
                ? Directory.GetFiles(filesDir, "*", SearchOption.AllDirectories).Count(f => !f.EndsWith(SourceKeep.RangesSuffix, StringComparison.Ordinal)) : 0;
            Console.WriteLine($"  KEPT: {repA.KeptBytes / 1048576.0:F1} MB in {fileCount} files ({(sparse == 0 ? "whole files" : $"{sparse} sparse (byte ranges)")}); "
                + $"limit for whole files {ImportService.KeepWholeLimit / 1048576} MB");
            if (repA.KeepFamily is SourceFamily.Stats fam)
                Console.WriteLine($"  FAMILY: read {fam.ReadBytes / 1048576.0:F1} MB -> family {fam.FamilyBytes / 1048576.0:F1} MB "
                    + $"({fam.Resources} archive resources, {fam.Linked} by link); its files whole {fam.WholeBytes / 1048576.0:F1} MB");
            foreach (IGrouping<string, string> g in Directory.Exists(filesDir)
                ? Directory.GetFiles(filesDir, "*", SearchOption.AllDirectories).Where(f => !f.EndsWith(SourceKeep.RangesSuffix, StringComparison.Ordinal))
                    .GroupBy(f => Path.GetRelativePath(filesDir, f).Replace('\\', '/').Split('/')[0]).OrderBy(x => x.Key)
                : Enumerable.Empty<IGrouping<string, string>>())
            {
                Console.WriteLine($"    {g.Key,-16} {g.Sum(f => new FileInfo(f).Length) / 1048576.0,8:F1} MB  {g.Count()} files");
            }

            // 2. the same game rebuilt from the kept copy alone: copy only source/, never the disc
            CopyDir(srcDir, SourceKeep.Dir(dirB, game));
            var svcB = new ImportService(dirB, store) { KeepSources = true };
            sw.Restart();
            ImportReport repB;
            try
            {
                repB = svcB.RebuildFromKept(game, progress, default, only);
            }
            catch (Exception e)
            {
                Console.WriteLine($"  FAIL rebuild from kept: {e.GetType().Name}: {e.Message}");
                return;
            }
            sw.Stop();
            int doneB = repB.Items.Count(i => i.Status == ItemImportStatus.Complete);
            Console.WriteLine($"  kept rebuild: {doneB}/{repB.Items.Count} items in {sw.Elapsed.TotalSeconds:F1} s");

            // 3. compare every item the disc import made
            int same = 0;
            var bad = new List<string>();
            foreach (ImportItemInfo a in repA.Items.Where(i => i.Status == ItemImportStatus.Complete))
            {
                ImportItemInfo? b = repB.Items.FirstOrDefault(i => i.Id == a.Id);
                ImportRecipe r = store.All.First(x => x.Id == a.Id);
                if (b == null || b.Status != ItemImportStatus.Complete)
                {
                    bad.Add($"{a.Id}: kept rebuild {(b == null ? "didn't run it" : b.Status + " - " + b.Message)}");
                    continue;
                }
                string why = CompareTree(svcA.ItemDir(r), svcB.ItemDir(r));
                if (why.Length == 0) same++;
                else bad.Add($"{a.Id}: {why}");
            }
            foreach (string s in bad) Console.WriteLine("    FAIL " + s);
            Console.WriteLine($"  SUMMARY {game}: kept {repA.KeptBytes / 1048576.0:F1} MB (family {(repA.KeepFamily?.FamilyBytes ?? 0) / 1048576.0:F1}, whole {(repA.KeepFamily?.WholeBytes ?? 0) / 1048576.0:F1}); {same}/{doneA} items identical to the disc import"
                + (bad.Count == 0 ? " - PASS" : $" - FAIL ({bad.Count})"));
        }

        static void CopyDir(string from, string to)
        {
            Directory.CreateDirectory(to);
            foreach (string f in Directory.GetFiles(from)) File.Copy(f, Path.Combine(to, Path.GetFileName(f)), true);
            foreach (string d in Directory.GetDirectories(from)) CopyDir(d, Path.Combine(to, Path.GetFileName(d)));
        }

        // "" = the same file names and bytes; else the first difference
        static string CompareTree(string a, string b)
        {
            if (!Directory.Exists(a)) return "no disc output";
            if (!Directory.Exists(b)) return "no kept-rebuild output";
            var fa = Directory.GetFiles(a, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(a, f)).OrderBy(x => x, StringComparer.Ordinal).ToList();
            var fb = Directory.GetFiles(b, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(b, f)).OrderBy(x => x, StringComparer.Ordinal).ToList();
            string? missing = fa.Except(fb).FirstOrDefault(), extra = fb.Except(fa).FirstOrDefault();
            if (missing != null) return $"{fa.Except(fb).Count()} files missing from the kept rebuild, e.g. {missing}";
            if (extra != null) return $"{fb.Except(fa).Count()} extra files in the kept rebuild, e.g. {extra}";
            foreach (string f in fa)
            {
                using FileStream x = File.OpenRead(Path.Combine(a, f)), y = File.OpenRead(Path.Combine(b, f));
                if (x.Length != y.Length) return $"{f}: {x.Length} vs {y.Length} bytes";
                if (!SHA256.HashData(x).AsSpan().SequenceEqual(SHA256.HashData(y))) return $"{f}: bytes differ";
            }
            return "";
        }
    }
}
