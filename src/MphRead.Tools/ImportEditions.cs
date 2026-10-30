using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using MphRecomp.Import;
using MphRecomp.Import.Disc;

namespace MphRead
{
    // -importeditions [mp1/trilogy|mp2/trilogy|mp3/corruption|all] [--write] [--out <dir>] [--mp1 <disc>] [--mp2 <disc>]
    //                 [--mp3 <disc>] [--trilogy <disc>] [--corruption <disc>] [-v]
    // The editions (MphRecomp.Core Import/Editions.cs: a game's files on another disc) and their golden check: every
    // recipe of the game is imported from its original disc and from the edition's disc, and an item counts for the
    // edition only when both folders come out byte-identical -- and only with its whole group (Group: arm cannons, HD
    // models, multiplayer data): a group with any item that differs is left to the game's own disc. Check (default): the edition files in recipes/editions/
    // must list exactly those items, and an import through them must make the listed ones and refuse the rest.
    // --write: (re)write recipes/editions/<game>/<edition>.json from the comparison (the file hashes the recipes read,
    // on both discs, and the identical items).
    internal static class ImportEditions
    {
        sealed record Spec(string Game, string Edition, string Title, string Platform, string DiscFolder, string RecipeFolder,
            string DiscArg, string DiscDefault, string[] Extensions);

        static readonly string[] WiiExtensions = { ".rvz", ".wia", ".iso", ".wbfs" };

        static readonly Spec[] Specs =
        {
            new("mp1", "trilogy", "Metroid Prime Trilogy", "Wii", "MP1/", "", "--trilogy", "trilogy.rvz", WiiExtensions),
            new("mp2", "trilogy", "Metroid Prime Trilogy", "Wii", "MP2/", "", "--trilogy", "trilogy.rvz", WiiExtensions),
            new("mp3", "corruption", "Metroid Prime 3: Corruption", "Wii", "", "MP3/", "--corruption", "corruption.rvz", WiiExtensions),
        };

        sealed class Quiet : IProgress<(string item, float fraction, string message)>
        {
            public bool Verbose;
            public void Report((string item, float fraction, string message) v)
            {
                if (Verbose && v.fraction >= 0) Console.WriteLine($"      {v.item}: {v.message}");
            }
        }

        public static int Run(string[] args)
        {
            string repo = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
            string Arg(string k, string d) { int i = Array.IndexOf(args, k); return i > 0 && i + 1 < args.Length ? args[i + 1] : d; }
            string which = args.Length >= 2 && !args[1].StartsWith("-") ? args[1] : "all";
            bool write = args.Contains("--write"), verbose = args.Contains("-v");
            if (!ToolPaths.UseDesktopExtraction()) return 1;   // the hd rig fit reads MPH's own hunter models
            string prime = Path.Combine(repo, "brawl_extract", "prime");
            string recipesDir = Path.Combine(repo, "recipes");
            string outRoot = Path.GetFullPath(Arg("--out", Path.Combine(Path.GetTempPath(), "mph-importeditions", DateTime.Now.ToString("yyyyMMdd-HHmmss"))));
            var original = new Dictionary<string, string>
            {
                ["mp1"] = Arg("--mp1", Path.Combine(prime, "mp1.iso")),
                ["mp2"] = Arg("--mp2", Path.Combine(prime, "echoes_widescreen.iso")),
                ["mp3"] = Arg("--mp3", Path.Combine(prime, "trilogy.rvz")),
            };
            RecipeStore store = RecipeStore.FromDirectory(recipesDir);
            int failed = 0, ran = 0;
            foreach (Spec spec in Specs.Where(s => which == "all" || which == s.Game + "/" + s.Edition))
            {
                string disc = Arg(spec.DiscArg, Path.Combine(prime, spec.DiscDefault));
                Console.WriteLine($"  == {spec.Game}/{spec.Edition}: {spec.Title} ({disc}) vs the original ({original[spec.Game]})");
                if (!File.Exists(disc) || !File.Exists(original[spec.Game]))
                {
                    Console.WriteLine("    [SKIP] a disc image is missing");
                    continue;
                }
                ran++;
                failed += Edition(spec, disc, original[spec.Game], store, recipesDir, Path.Combine(outRoot, spec.Game + "-" + spec.Edition), write, verbose) ? 0 : 1;
            }
            Console.WriteLine($"  SUMMARY: {ran - failed}/{ran} editions {(write ? "written" : "PASS")}; output {outRoot}");
            return failed == 0 ? 0 : 1;
        }

        static bool Edition(Spec spec, string disc, string originalDisc, RecipeStore store, string recipesDir, string outDir, bool write, bool verbose)
        {
            List<ImportRecipe> recipes = store.ForGame(spec.Game).ToList();
            string origOut = Path.Combine(outDir, "original"), edOut = Path.Combine(outDir, "edition");
            foreach (string d in new[] { origOut, edOut }) if (Directory.Exists(d)) Directory.Delete(d, true);
            var progress = new Quiet { Verbose = verbose };

            // 1. the original disc, as the app imports it
            var sw = Stopwatch.StartNew();
            ImportReport orig = new ImportService(origOut, store).Import(spec.Game, originalDisc, progress);
            Console.WriteLine($"    original: {orig.Items.Count(i => i.Status == ItemImportStatus.Complete)}/{orig.Items.Count} in {sw.Elapsed.TotalSeconds:0} s");

            // 2. the edition's disc through a candidate edition that vouches for every item (the file hashes read from it)
            var files = new List<(string Name, string Sha1, string EditionSha1)>();
            using (var s = File.OpenRead(disc))
            using (IFileSource raw = DiscImage.Open(s))
            using (IFileSource src = new EditionSource(raw, spec.DiscFolder, spec.RecipeFolder))
            {
                var kinds = new ImportService(edOut, store);
                foreach (ImportRecipe r in recipes)
                {
                    foreach ((string path, string sha1) in kinds.SourcesOf(r))
                    {
                        if (files.Any(f => f.Name.Equals(path, StringComparison.OrdinalIgnoreCase) && f.Sha1 == sha1)) continue;
                        string ed = src.Contains(path) ? Sha1(src.ReadFile(path)) : "";
                        files.Add((path, sha1, ed));
                        if (ed.Length == 0) Console.WriteLine($"    {path}: not on the {spec.Title} disc");
                    }
                }
            }
            var candidate = Build(spec, files.Where(f => f.EditionSha1.Length > 0), recipes.Select(r => r.Id));
            var svc = new ImportService(edOut, store);
            svc.Editions.Clear();
            svc.Editions.Add(ImportEdition.Parse(spec.Game + "/" + spec.Edition, candidate));
            sw.Restart();
            ImportReport ed1 = svc.Import(spec.Game, disc, progress);
            Console.WriteLine($"    {spec.Edition}: {ed1.Items.Count(i => i.Status == ItemImportStatus.Complete)}/{ed1.Items.Count} in {sw.Elapsed.TotalSeconds:0} s");

            // 3. the items both discs make the same
            var same = new List<string>();
            foreach (ImportRecipe r in recipes)
            {
                string a = Path.Combine(origOut, r.Kind, r.Name), b = Path.Combine(edOut, r.Kind, r.Name);
                string? diff = orig.Items.First(i => i.Id == r.Id).Status != ItemImportStatus.Complete ? "the original disc didn't make it"
                    : ed1.Items.First(i => i.Id == r.Id).Status != ItemImportStatus.Complete ? ed1.Items.First(i => i.Id == r.Id).Message
                    : CompareDirs(a, b);
                if (diff == null) same.Add(r.Id);
                Console.WriteLine($"    {(diff == null ? "SAME" : "DIFF")} {r.Id}{(diff == null ? "" : ": " + diff)}");
            }
            Console.WriteLine($"    {same.Count}/{recipes.Count} identical");
            // a disc is taken for a group only when it makes ALL of it (owner 2026-10-07: "only accepts a disc for each
            // grouping of imports for a game that it can grab the full group from"; Echoes' multiplayer arenas + music
            // come from the GameCube disc)
            var whole = new List<string>();
            foreach (IGrouping<string, ImportRecipe> grp in recipes.GroupBy(Group))
            {
                bool all = grp.All(r => same.Contains(r.Id));
                if (all) whole.AddRange(grp.Select(r => r.Id));
                Console.WriteLine($"    group {grp.Key}: {grp.Count(r => same.Contains(r.Id))}/{grp.Count()} identical"
                    + (all ? "" : $" -> needs {spec.Game}'s own disc (the whole group must come out the same)"));
            }

            string path1 = Path.Combine(recipesDir, "editions", spec.Game, spec.Edition + ".json");
            if (write)
            {
                // only the files the identical items read
                var used = new HashSet<(string, string)>();
                var kinds = new ImportService(edOut, store);
                foreach (ImportRecipe r in recipes.Where(r => whole.Contains(r.Id)))
                    foreach ((string p, string h) in kinds.SourcesOf(r)) used.Add((p.ToLowerInvariant(), h));
                Directory.CreateDirectory(Path.GetDirectoryName(path1)!);
                File.WriteAllText(path1, Build(spec, files.Where(f => used.Contains((f.Name.ToLowerInvariant(), f.Sha1))), whole));
                Console.WriteLine($"    wrote {path1}");
                return true;
            }

            // 4. check: the shipped edition lists exactly the identical items, and an import through it makes those and
            // refuses the rest
            if (!File.Exists(path1)) { Console.WriteLine($"    FAIL: no {path1} (run with --write)"); return false; }
            ImportEdition shipped = ImportEdition.Parse(spec.Game + "/" + spec.Edition, File.ReadAllText(path1));
            var extra = shipped.Items.Except(whole).ToList();
            var missing = whole.Except(shipped.Items).ToList();
            var partial = recipes.GroupBy(Group).Where(g => g.Any(r => shipped.Items.Contains(r.Id)) && !g.All(r => shipped.Items.Contains(r.Id)))
                .Select(g => g.Key).ToList();
            if (extra.Count > 0) Console.WriteLine($"    FAIL: the edition lists {String.Join(", ", extra)}, whose group no longer comes out the same");
            if (partial.Count > 0) Console.WriteLine($"    FAIL: the edition lists part of group(s) {String.Join(", ", partial)}");
            if (missing.Count > 0) Console.WriteLine($"    NOTE: now identical but not listed (run --write): {String.Join(", ", missing)}");
            string chkOut = Path.Combine(outDir, "check");
            if (Directory.Exists(chkOut)) Directory.Delete(chkOut, true);
            var chk = new ImportService(chkOut, store);
            chk.Editions.Clear();
            chk.Editions.Add(shipped);
            ImportReport rep = chk.Import(spec.Game, disc, progress);
            int bad = 0;
            foreach (ImportItemInfo i in rep.Items)
            {
                bool listed = shipped.Items.Contains(i.Id);
                bool ok = listed ? i.Status == ItemImportStatus.Complete && CompareDirs(Path.Combine(origOut, i.Kind, i.Name), Path.Combine(chkOut, i.Kind, i.Name)) == null
                    : i.Status == ItemImportStatus.HashMismatch;
                if (!ok) { bad++; Console.WriteLine($"    FAIL {i.Id}: {i.Status} {i.Message}"); }
            }
            bool pass = extra.Count == 0 && partial.Count == 0 && bad == 0;
            Console.WriteLine($"    {(pass ? "PASS" : "FAIL")}: {shipped.Items.Count} items made from {spec.Title}, {rep.Items.Count - shipped.Items.Count} refused (need the original disc)");
            return pass;
        }

        // a game's import groups: its arm cannons, its HD models, its multiplayer data (Echoes' arenas + their music);
        // each Prime song is its own (owner 2026-10-14: songs import and delete one at a time, docs/PRIME_MUSIC.md)
        static string Group(ImportRecipe r) => r.Kind is "arenas" or "music" ? "multiplayer" : r.Kind == "tracks" ? r.Id : r.Kind;

        static string Build(Spec spec, IEnumerable<(string Name, string Sha1, string EditionSha1)> files, IEnumerable<string> items)
        {
            static string Q(string s) => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
            var sb = new StringBuilder();
            sb.Append("{\n");
            sb.Append($"  \"game\": {Q(spec.Game)},\n  \"title\": {Q(spec.Title)},\n  \"platform\": {Q(spec.Platform)},\n");
            sb.Append($"  \"discPrefixes\": [ {(spec.Edition == "trilogy" ? Q("R3M") : Q("RM3"))} ],\n");
            sb.Append($"  \"discFolder\": {Q(spec.DiscFolder)},\n  \"recipeFolder\": {Q(spec.RecipeFolder)},\n");
            sb.Append($"  \"extensions\": [ {String.Join(", ", spec.Extensions.Select(Q))} ],\n");
            sb.Append("  \"files\": [\n");
            sb.Append(String.Join(",\n", files.OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase).ThenBy(f => f.Sha1, StringComparer.Ordinal)
                .Select(f => $"    {{ \"name\": {Q(f.Name)}, \"sha1\": {Q(f.Sha1)}, \"editionSha1\": {Q(f.EditionSha1)} }}")));
            sb.Append("\n  ],\n  \"items\": [\n");
            sb.Append(String.Join(",\n", items.OrderBy(i => i, StringComparer.Ordinal).Select(i => "    " + Q(i))));
            sb.Append("\n  ]\n}\n");
            return sb.ToString();
        }

        // null when both folders hold the same files with the same bytes
        static string? CompareDirs(string a, string b)
        {
            if (!Directory.Exists(a) || !Directory.Exists(b)) return "folder missing";
            var fa = Directory.GetFiles(a).Select(Path.GetFileName).OrderBy(f => f, StringComparer.Ordinal).ToList();
            var fb = Directory.GetFiles(b).Select(Path.GetFileName).OrderBy(f => f, StringComparer.Ordinal).ToList();
            if (!fa.SequenceEqual(fb)) return $"file lists differ ({fa.Count} vs {fb.Count})";
            var diff = fa.Where(f => !File.ReadAllBytes(Path.Combine(a, f!)).AsSpan().SequenceEqual(File.ReadAllBytes(Path.Combine(b, f!)))).ToList();
            return diff.Count == 0 ? null : $"{diff.Count} file(s) differ: {String.Join(", ", diff.Take(3))}";
        }

        static string Sha1(byte[] b) => Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(b)).ToLowerInvariant();
    }
}
