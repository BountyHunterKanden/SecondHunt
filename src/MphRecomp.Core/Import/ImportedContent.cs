using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using MphRecomp.Config;

namespace MphRecomp.Import
{
    // Whether an item folder under the import root (<root>/<kind>/<name>/: hd, guns, bodies, arenas, music) may be used.
    // Every reader of those folders asks here instead of only checking that the folder exists (ship-safe audit
    // 2026-10-10, docs/SHIP_SAFE_AUDIT_2026-10-10.md; MR1 V1, docs/MULTIPLAYER_RELIABILITY.md section 2):
    //   - a recipe item: usable when <root>/import/<game>.json records it complete, made from today's recipe by today's
    //     importer (ImporterVersions); a stale one is not used until it's imported again (owner decision D3);
    //   - public builds (RecompSettings.PublicBuild) use nothing else: no folder the in-app importer didn't make (owner
    //     2026-10-10: no Brawl trophies, rig work folders, hand-made rigs or other hand-dropped models in a public build);
    //   - dev builds also use folders with no import record (models pushed to a test device by hand, the Brawl trophies).
    public static class ImportedContent
    {
        static readonly Lazy<RecipeStore> Store = new(RecipeStore.Embedded);
        static readonly Dictionary<string, (DateTime Time, Dictionary<string, (string Status, string Sha1, int Importer)> Items)> States = new();
        static readonly object Gate = new();

        // kindRoot = <root>/<kind> (the folder the readers already hold: hdRoot, bodiesRoot, the guns folder, ...)
        public static bool Usable(string? kindRoot, string name)
        {
            if (String.IsNullOrEmpty(kindRoot) || String.IsNullOrEmpty(name)) return false;
            string kind = Path.GetFileName(Path.TrimEndingDirectorySeparator(kindRoot));
            string root = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(kindRoot)) ?? "";
            return Usable(root, kind, name);
        }

        public static bool Usable(string root, string kind, string name)
        {
            string dir = Path.Combine(root, kind, name);
            if (!Directory.Exists(dir)) return false;
            ImportRecipe? r = Store.Value.All.FirstOrDefault(x => x.Kind == kind && x.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (r == null) return !RecompSettings.PublicBuild;
            if (!State(Path.Combine(root, "import", r.Game + ".json")).TryGetValue(r.Id, out var s)) return !RecompSettings.PublicBuild;
            return s.Status == "complete" && s.Sha1 == r.Sha1 && s.Importer == ImporterVersions.For(kind);
        }

        // a public build lists only imported items; a dev build everything there
        public static IEnumerable<string> UsableNames(string? kindRoot, IEnumerable<string> names) =>
            names.Where(n => Usable(kindRoot, n));

        static Dictionary<string, (string Status, string Sha1, int Importer)> State(string path)
        {
            DateTime time = File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue;
            lock (Gate)
            {
                if (States.TryGetValue(path, out var c) && c.Time == time) return c.Items;
                var items = new Dictionary<string, (string, string, int)>(StringComparer.Ordinal);
                if (time != DateTime.MinValue)
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(File.ReadAllText(path, Encoding.UTF8));
                        if (doc.RootElement.TryGetProperty("items", out JsonElement all))
                        {
                            foreach (JsonProperty p in all.EnumerateObject())
                            {
                                string S(string k) => p.Value.TryGetProperty(k, out JsonElement e) && e.ValueKind == JsonValueKind.String ? e.GetString() ?? "" : "";
                                int importer = p.Value.TryGetProperty("importer", out JsonElement iv) && iv.ValueKind == JsonValueKind.Number ? iv.GetInt32() : 1;
                                items[p.Name] = (S("status"), S("recipeSha1"), importer);
                            }
                        }
                    }
                    catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
                    {
                        // a damaged or unreadable state file reads as "nothing imported"
                    }
                }
                States[path] = (time, items);
                return items;
            }
        }
    }
}
