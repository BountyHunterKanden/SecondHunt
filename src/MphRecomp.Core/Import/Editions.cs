using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;

namespace MphRecomp.Import
{
    // A game's files on another disc than the one its recipes were made from: Metroid Prime Trilogy (Wii) carries Prime 1
    // and Echoes under MP1/ and MP2/, and the original Metroid Prime 3: Corruption disc carries Prime 3 at its root where
    // Trilogy has MP3/. An edition names that disc (ID prefixes, platform), where the game's files sit on it, and for each
    // file the recipes read the SHA-1 it has there. Only the items listed under "items" are made from it: each one was
    // imported from that disc and found byte-identical to what the original disc makes (Tools -importeditions); the
    // others answer "import it from the original disc".
    //   recipes/editions/<game>/<edition>.json (one folder deeper than recipes/<kind>/<Name>.json, so RecipeStore skips it):
    //   { "game": "mp1", "title": "Metroid Prime Trilogy", "platform": "Wii", "discPrefixes": [ "R3M" ],
    //     "discFolder": "MP1/", "recipeFolder": "", "extensions": [ ".rvz", ".wia", ".iso", ".wbfs" ],
    //     "files": [ { "name": "Metroid1.pak", "sha1": "<the recipe's>", "editionSha1": "<this disc's>" } ],
    //   A file entry may add "editionName": the recipe-style path this disc keeps the file under when it isn't "name" (the
    //   original Prime 3 disc has the revised Trilogy mixes as other files); the recipe then reads that file instead.
    //     "items": [ "guns/MP1PowerSuit", "hd/MP1PowerSuit" ] }
    // Only ids, paths and hashes: never game data (docs/IMPORT_PIPELINE.md section 3).
    public sealed class ImportEdition
    {
        public string Game = "", Id = "", Title = "", Platform = "";
        public string DiscFolder = "", RecipeFolder = "";   // "MP1/" on the disc is "" in the recipes; "" on the disc is "MP3/"
        public string[] DiscPrefixes = Array.Empty<string>();
        public string[] Extensions = Array.Empty<string>();
        public readonly HashSet<string> Items = new(StringComparer.Ordinal);
        // (recipe path, recipe SHA-1) -> the SHA-1 that file has on this disc
        readonly Dictionary<(string, string), string> _hashes = new();
        // recipe path (lower case) -> the recipe-style path this disc has that file under
        readonly Dictionary<string, string> _names = new();

        public bool Matches(string discId) => DiscPrefixes.Any(p => discId.StartsWith(p, StringComparison.Ordinal));

        public bool Accepts(string path, string recipeSha1, string sha1) =>
            _hashes.TryGetValue((path.ToLowerInvariant(), recipeSha1.ToLowerInvariant()), out string? want)
            && String.Equals(want, sha1, StringComparison.OrdinalIgnoreCase);

        // the disc's file tree as the recipes name it ("MP1/Metroid1.pak" is "Metroid1.pak")
        public IFileSource Map(IFileSource disc) => new EditionSource(disc, DiscFolder, RecipeFolder, _names);

        public static ImportEdition Parse(string id, string text)
        {
            using JsonDocument doc = JsonDocument.Parse(text);
            JsonElement r = doc.RootElement;
            string Str(string k) => r.TryGetProperty(k, out JsonElement e) && e.ValueKind == JsonValueKind.String ? e.GetString() ?? "" : "";
            string[] Strs(string k) => r.TryGetProperty(k, out JsonElement e) && e.ValueKind == JsonValueKind.Array
                ? e.EnumerateArray().Select(x => x.GetString() ?? "").ToArray() : Array.Empty<string>();
            var ed = new ImportEdition
            {
                Game = Str("game"), Id = id, Title = Str("title"), Platform = Str("platform"),
                DiscFolder = Str("discFolder"), RecipeFolder = Str("recipeFolder"), DiscPrefixes = Strs("discPrefixes"), Extensions = Strs("extensions"),
            };
            if (ed.Game.Length == 0 || ed.DiscPrefixes.Length == 0) throw new InvalidDataException($"edition {id}: game and discPrefixes are required");
            if (r.TryGetProperty("files", out JsonElement files))
            {
                foreach (JsonElement f in files.EnumerateArray())
                {
                    string name = f.GetProperty("name").GetString() ?? "", sha1 = f.GetProperty("sha1").GetString() ?? "";
                    string edSha1 = f.GetProperty("editionSha1").GetString() ?? "";
                    if (sha1.Length != 40 || edSha1.Length != 40) throw new InvalidDataException($"edition {id}: {name} needs two SHA-1s");
                    ed._hashes[(name.ToLowerInvariant(), sha1.ToLowerInvariant())] = edSha1.ToLowerInvariant();
                    if (f.TryGetProperty("editionName", out JsonElement en) && en.ValueKind == JsonValueKind.String && en.GetString() is { Length: > 0 } alias)
                    {
                        ed._names[name.ToLowerInvariant()] = alias;
                    }
                }
            }
            foreach (string item in Strs("items")) ed.Items.Add(item);
            return ed;
        }

        // recipes/editions/<game>/<edition>.json under the repo's recipes folder (PC tools)
        public static List<ImportEdition> FromDirectory(string recipesDir)
        {
            var list = new List<ImportEdition>();
            string dir = Path.Combine(recipesDir, "editions");
            if (!Directory.Exists(dir)) return list;
            foreach (string gameDir in Directory.GetDirectories(dir).OrderBy(d => d, StringComparer.Ordinal))
            {
                foreach (string f in Directory.GetFiles(gameDir, "*.json").OrderBy(f => f, StringComparer.Ordinal))
                {
                    list.Add(Parse(Path.GetFileName(gameDir) + "/" + Path.GetFileNameWithoutExtension(f), File.ReadAllText(f)));
                }
            }
            return list;
        }

        // the editions built into this library (EmbeddedResource "recipes/editions/<game>/<edition>.json")
        public static List<ImportEdition> Embedded()
        {
            var list = new List<ImportEdition>();
            Assembly asm = typeof(ImportEdition).Assembly;
            foreach (string res in asm.GetManifestResourceNames().OrderBy(n => n, StringComparer.Ordinal))
            {
                string n = res.Replace('\\', '/');
                if (!n.StartsWith("recipes/editions/", StringComparison.Ordinal) || !n.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) continue;
                string[] parts = n["recipes/editions/".Length..].Split('/');
                if (parts.Length != 2) continue;
                using Stream st = asm.GetManifestResourceStream(res)!;
                using var rd = new StreamReader(st, Encoding.UTF8);
                list.Add(Parse(parts[0] + "/" + Path.GetFileNameWithoutExtension(parts[1]), rd.ReadToEnd()));
            }
            return list;
        }
    }

    // a disc's files under one folder, named as the recipes name them; GameId stays the disc's own
    public sealed class EditionSource : IFileSource
    {
        readonly IFileSource _disc;
        readonly Dictionary<string, string> _map = new(StringComparer.OrdinalIgnoreCase);

        public EditionSource(IFileSource disc, string discFolder, string recipeFolder, IReadOnlyDictionary<string, string>? names = null)
        {
            _disc = disc;
            foreach (string p in disc.Paths)
            {
                if (p.StartsWith(discFolder, StringComparison.OrdinalIgnoreCase)) _map[recipeFolder + p.Substring(discFolder.Length)] = p;
            }
            // a file this disc keeps under another name: the recipe's path reads that one
            if (names != null)
            {
                foreach ((string recipePath, string alias) in names)
                {
                    if (_map.TryGetValue(alias, out string? real)) _map[recipePath] = real;
                }
            }
        }

        public string GameId => _disc.GameId;
        public string Title => _disc.Title;
        public IEnumerable<string> Paths => _map.Keys;
        public bool Contains(string path) => _map.ContainsKey(path);
        public long Length(string path) => _disc.Length(Real(path));
        public byte[] ReadFile(string path) => _disc.ReadFile(Real(path));
        public Stream OpenRead(string path) => _disc.OpenRead(Real(path));
        public void Dispose() { }   // the disc belongs to the caller

        string Real(string path) => _map.TryGetValue(path, out string? p) ? p : throw new FileNotFoundException(path);
    }
}
