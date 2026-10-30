using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using MphRecomp.Import;

namespace MphRead
{
    // -recipecheck [--recipes dir] [-v]
    // The recipe lint: everything under
    // recipes/ ships inside the app, so it may hold ids, names, parameters and source hashes, never game data. Run it
    // before each commit; the exit code is 1 when any file fails.
    //   every file    strict JSON (the parser the app uses; nothing else may live in recipes/); no numeric array longer
    //                 than 64; no table of more than 64 numeric rows; at most 4096 numbers in the file; no string over
    //                 1024 characters, holding more than 64 numbers, or with a base64/hex run (65+ characters of
    //                 [A-Za-z0-9+/], 129+ counting =_-) or a data: URI
    //   import recipe (recipes/<kind>/<Name>.json, what RecipeStore reads): a known "game"; its kind's importer parses
    //                 it, and every source file that importer reads (IImportKind.Sources) carries a 40-digit SHA-1; a
    //                 kind that can't check its source yet (the mp4 guns) must still name one in a "...Sha1" field;
    //                 every "...sha1" field is 40 hex digits; no item named after a never-ships mesh
    //   other files   (deeper folders, e.g. recipes/frontend/MenuSamus/MenuSamus.json): the data rules only
    // The thresholds sit far above any recipe today (largest numeric array: 3) and far below any mesh, texture or sample
    // data; raise them only for a reviewed recipe that needs it.
    internal static class RecipeCheck
    {
        const int MaxArray = 64, MaxRows = 64, MaxNumbers = 4096, MaxString = 1024, MaxStringNumbers = 64;

        // every IImportKind ImportService registers; a recipe folder with no kind here fails
        static readonly IImportKind[] Kinds = { new MphRecomp.Import.Guns.GunImportKind(), new MphRecomp.Import.Hd.HdImportKind(),
            new MphRecomp.Import.Echoes.EchoesArenaImportKind(), new MphRecomp.Import.Echoes.MusicImportKind(),
            new MphRecomp.Import.Bodies.BodyImportKind() };

        // mp1-mp3 import today (ImportService.KnownGames); mp4's recipes are ready, its game is not registered yet (CR6)
        static readonly string[] Games = { "mp1", "mp2", "mp3", "mp4" };

        // never shipped: community fan-rig meshes and the dropped MP3 suit
        static readonly string[] NeverShips = { "KandenRest", "SyluxRest" };

        static readonly Regex Sha1Rx = new("^[0-9a-fA-F]{40}$", RegexOptions.Compiled);
        static readonly Regex B64Rx = new("[A-Za-z0-9+/]{65,}", RegexOptions.Compiled);
        static readonly Regex B64UrlRx = new("[A-Za-z0-9+/=_-]{129,}", RegexOptions.Compiled);

        public static int Run(string[] args)
        {
            string repo = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
            int a = Array.IndexOf(args, "--recipes");
            string dir = Path.GetFullPath(a > 0 && a + 1 < args.Length ? args[a + 1] : Path.Combine(repo, "recipes"));
            bool verbose = args.Contains("-v");
            if (!Directory.Exists(dir))
            {
                Console.WriteLine($"  -recipecheck: no folder {dir}");
                return 1;
            }
            Console.WriteLine($"  -recipecheck {dir}");
            string[] files = Directory.GetFiles(dir, "*", SearchOption.AllDirectories);
            Array.Sort(files, StringComparer.Ordinal);
            int pass = 0, fail = 0;
            var notes = new List<string>();
            var dirTexts = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (string f in files)
            {
                string rel = Path.GetRelativePath(dir, f).Replace('\\', '/');
                var errors = new List<string>();
                var info = new List<string>();
                Check(f, rel, errors, info, dirTexts);
                if (errors.Count == 0)
                {
                    pass++;
                    string[] shown = info.Where(i => !i.StartsWith("NOTE ", StringComparison.Ordinal)).ToArray();
                    if (verbose) Console.WriteLine($"  PASS {rel}" + (shown.Length > 0 ? $"  ({String.Join("; ", shown)})" : ""));
                }
                else
                {
                    fail++;
                    Console.WriteLine($"  FAIL {rel}");
                    foreach (string e in errors) Console.WriteLine($"       {e}");
                }
                notes.AddRange(info.Where(i => i.StartsWith("NOTE ", StringComparison.Ordinal)).Select(i => $"{rel}: {i[5..]}"));
            }
            // the recipes the app carries are the ones built into MphRecomp.Core: say when this build's copy is stale
            if (a < 0)
            {
                Dictionary<string, string> built = RecipeStore.Embedded().All.ToDictionary(r => r.Kind + "/" + r.Name + ".json", r => r.Text, StringComparer.Ordinal);
                foreach ((string rel, string text) in dirTexts)
                {
                    if (!built.TryGetValue(rel, out string? b)) notes.Add($"{rel}: not in this build's embedded recipes (rebuild MphRecomp.Core)");
                    else if (b != text) notes.Add($"{rel}: differs from this build's embedded copy (rebuild MphRecomp.Core)");
                }
                foreach (string rel in built.Keys.Where(k => !dirTexts.ContainsKey(k))) notes.Add($"{rel}: embedded in this build but not in {dir}");
            }
            foreach (string n in notes) Console.WriteLine($"  note: {n}");
            Console.WriteLine($"  SUMMARY: {pass}/{pass + fail} recipe files pass" + (fail > 0 ? $", {fail} FAIL" : ""));
            return fail > 0 ? 1 : 0;
        }

        static void Check(string path, string rel, List<string> errors, List<string> info, Dictionary<string, string> dirTexts)
        {
            if (!rel.EndsWith(".json", StringComparison.Ordinal))
            {
                errors.Add("not a .json recipe (recipes/ holds JSON recipes only)");
                return;
            }
            string text;
            try { text = File.ReadAllText(path); }
            catch (Exception ex) { errors.Add($"unreadable: {ex.Message}"); return; }
            JsonDocument doc;
            try { doc = JsonDocument.Parse(text); }
            catch (JsonException ex) { errors.Add($"not valid JSON: {ex.Message}"); return; }
            using (doc)
            {
                int numbers = 0;
                Walk(doc.RootElement, "$", errors, ref numbers);
                if (numbers > MaxNumbers) errors.Add($"{numbers} numbers in the file (limit {MaxNumbers}): bulk data?");
                string[] parts = rel.Split('/');
                if (parts.Length != 2)
                {
                    info.Add("not an import recipe: data rules only");
                    return;
                }
                dirTexts[rel] = text;
                Recipe(parts[0], Path.GetFileNameWithoutExtension(parts[1]), text, doc.RootElement, errors, info);
            }
        }

        // the data rules, over every value in the file
        static void Walk(JsonElement e, string at, List<string> errors, ref int numbers)
        {
            switch (e.ValueKind)
            {
            case JsonValueKind.Number:
                numbers++;
                break;
            case JsonValueKind.String:
                StringRule(e.GetString() ?? "", at, errors);
                break;
            case JsonValueKind.Object:
                foreach (JsonProperty p in e.EnumerateObject())
                {
                    StringRule(p.Name, at + " (key)", errors);
                    Walk(p.Value, at + "." + p.Name, errors, ref numbers);
                }
                break;
            case JsonValueKind.Array:
                int len = e.GetArrayLength();
                bool allNumbers = len > 0 && e.EnumerateArray().All(x => x.ValueKind == JsonValueKind.Number);
                bool allRows = len > 0 && e.EnumerateArray().All(x => x.ValueKind == JsonValueKind.Array
                    && x.EnumerateArray().All(y => y.ValueKind == JsonValueKind.Number));
                if (allNumbers && len > MaxArray) errors.Add($"{at}: numeric array of {len} (limit {MaxArray}): bulk data?");
                else if (allRows && len > MaxRows) errors.Add($"{at}: {len} numeric rows (limit {MaxRows}): per-vertex or sample data?");
                int i = 0;
                foreach (JsonElement x in e.EnumerateArray()) Walk(x, $"{at}[{i++}]", errors, ref numbers);
                break;
            }
        }

        static void StringRule(string s, string at, List<string> errors)
        {
            if (s.Length > MaxString) errors.Add($"{at}: string of {s.Length} characters (limit {MaxString})");
            if (s.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) errors.Add($"{at}: data: URI");
            Match m = B64Rx.Match(s);
            if (!m.Success) m = B64UrlRx.Match(s);
            if (m.Success) errors.Add($"{at}: {m.Length}-character base64/hex run (\"{m.Value[..16]}...\")");
            int nums = s.Split(new[] { ' ', '\t', '\r', '\n', ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
                .Count(t => Double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out _));
            if (nums > MaxStringNumbers) errors.Add($"{at}: string holding {nums} numbers (limit {MaxStringNumbers})");
        }

        // the import-recipe rules: game, the kind's own parse, a hash for every source
        static void Recipe(string kindName, string name, string text, JsonElement root, List<string> errors, List<string> info)
        {
            if (NeverShips.Contains(name, StringComparer.OrdinalIgnoreCase)) errors.Add($"'{name}' never ships (docs/IMPORT_PIPELINE.md section 6)");
            var sha1Fields = new List<(string At, JsonElement Value)>();
            Sha1Fields(root, "$", sha1Fields);
            foreach ((string at, JsonElement v) in sha1Fields)
            {
                if (v.ValueKind != JsonValueKind.String || !Sha1Rx.IsMatch(v.GetString() ?? "")) errors.Add($"{at}: not a 40-digit SHA-1");
            }
            ImportRecipe r;
            try { r = ImportRecipe.Parse(kindName, name, text); }
            catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException or InvalidDataException)
            {
                errors.Add("no \"game\" string (every import recipe names its game)");
                return;
            }
            if (!Games.Contains(r.Game)) errors.Add($"game '{r.Game}' is not one of {String.Join(", ", Games)}");
            IImportKind? kind = Kinds.FirstOrDefault(k => k.Kind == kindName);
            if (kind == null)
            {
                errors.Add($"no importer for kind '{kindName}' (ImportService registers {String.Join(", ", Kinds.Select(k => k.Kind))})");
                return;
            }
            List<(string Path, string Sha1)> sources;
            try { sources = kind.Sources(r).ToList(); }
            catch (Exception ex)
            {
                errors.Add($"the '{kindName}' importer can't read it: {ex.GetType().Name}: {ex.Message}");
                return;
            }
            foreach ((string p, string h) in sources)
            {
                if (String.IsNullOrWhiteSpace(p)) errors.Add("a source with no file path");
                else if (!Sha1Rx.IsMatch(h ?? "")) errors.Add($"source {p}: no SHA-1 (\"{h}\")");
            }
            if (sources.Count == 0)
            {
                if (sha1Fields.Count == 0) errors.Add($"no source SHA-1 (the '{kindName}' importer names no source file and the recipe has no ...Sha1 field)");
                else info.Add($"NOTE source hash in {String.Join(", ", sha1Fields.Select(f => f.At))}, not checked by the importer yet (game {r.Game})");
            }
            info.Add($"{kindName}, {r.Game}, {sources.Count} source(s) hashed");
        }

        static void Sha1Fields(JsonElement e, string at, List<(string, JsonElement)> list)
        {
            if (e.ValueKind == JsonValueKind.Object)
            {
                foreach (JsonProperty p in e.EnumerateObject())
                {
                    string here = at + "." + p.Name;
                    if (p.Name.EndsWith("sha1", StringComparison.OrdinalIgnoreCase)) list.Add((here, p.Value));
                    else Sha1Fields(p.Value, here, list);
                }
            }
            else if (e.ValueKind == JsonValueKind.Array)
            {
                int i = 0;
                foreach (JsonElement x in e.EnumerateArray()) Sha1Fields(x, $"{at}[{i++}]", list);
            }
        }
    }
}
