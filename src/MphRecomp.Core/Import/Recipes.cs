using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MphRecomp.Import
{
    // A ship-safe import recipe: recipes/<kind>/<Name>.json in the repo (embedded in this library, so the app carries
    // them). Only ids, names, parameters and the hash of the source file it was authored against -- never game data
    // (docs/IMPORT_PIPELINE.md section 3). Every recipe names its game ("game": "mp1"); the rest is the kind's own.
    public sealed class ImportRecipe
    {
        public string Kind = "";   // "guns" -- also the output folder under the import root
        public string Name = "";   // "MP1PowerSuit" -- the item's own folder
        public string Game = "";   // "mp1", "mp2"
        public string Text = "";
        public string Sha1 = "";   // of the recipe text: an edited recipe makes its earlier output stale
        public JsonElement Root;

        public string Id => Kind + "/" + Name;

        public string Str(string key) => Root.GetProperty(key).GetString() ?? throw new InvalidDataException($"{Id}: {key} is null");
        public string? OptStr(string key) => Root.TryGetProperty(key, out JsonElement e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null;
        public int Int(string key, int def = 0) => Root.TryGetProperty(key, out JsonElement e) ? e.GetInt32() : def;
        public bool Bool(string key, bool def = false) => Root.TryGetProperty(key, out JsonElement e) ? e.GetBoolean() : def;
        public uint Hex(string key) => UInt32.Parse(Str(key), System.Globalization.NumberStyles.HexNumber);

        public int[] Ints(string key)
        {
            if (!Root.TryGetProperty(key, out JsonElement e)) return Array.Empty<int>();
            var l = new List<int>();
            foreach (JsonElement x in e.EnumerateArray()) l.Add(x.GetInt32());
            return l.ToArray();
        }

        public static ImportRecipe Parse(string kind, string name, string text)
        {
            using var doc = JsonDocument.Parse(text);
            var r = new ImportRecipe
            {
                Kind = kind, Name = name, Text = text, Root = doc.RootElement.Clone(),
                Sha1 = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant(),
            };
            r.Game = r.Str("game");
            return r;
        }
    }

    public sealed class RecipeStore
    {
        public readonly List<ImportRecipe> All = new();
        public string Origin = "";

        public IEnumerable<ImportRecipe> ForGame(string game)
        {
            foreach (ImportRecipe r in All)
            {
                if (r.Game == game) yield return r;
            }
        }

        // recipes/<kind>/<Name>.json under a directory (the repo's, on the PC)
        public static RecipeStore FromDirectory(string dir)
        {
            var s = new RecipeStore { Origin = dir };
            foreach (string kindDir in Directory.GetDirectories(dir))
            {
                string kind = Path.GetFileName(kindDir);
                string[] files = Directory.GetFiles(kindDir, "*.json");
                Array.Sort(files, StringComparer.Ordinal);
                foreach (string f in files)
                {
                    s.All.Add(ImportRecipe.Parse(kind, Path.GetFileNameWithoutExtension(f), File.ReadAllText(f)));
                }
            }
            return s;
        }

        // the recipes built into this library (EmbeddedResource "recipes/<kind>/<Name>.json")
        public static RecipeStore Embedded()
        {
            var s = new RecipeStore { Origin = "embedded" };
            Assembly asm = typeof(RecipeStore).Assembly;
            string[] names = asm.GetManifestResourceNames();
            Array.Sort(names, StringComparer.Ordinal);
            foreach (string res in names)
            {
                string n = res.Replace('\\', '/');
                if (!n.StartsWith("recipes/", StringComparison.Ordinal) || !n.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) continue;
                string[] parts = n["recipes/".Length..].Split('/');
                if (parts.Length != 2) continue;
                using Stream st = asm.GetManifestResourceStream(res)!;
                using var rd = new StreamReader(st, Encoding.UTF8);
                s.All.Add(ImportRecipe.Parse(parts[0], Path.GetFileNameWithoutExtension(parts[1]), rd.ReadToEnd()));
            }
            return s;
        }
    }
}
