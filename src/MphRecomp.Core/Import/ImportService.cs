using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using MphRecomp.Import.Disc;
using MphRecomp.Import.Guns;
using MphRecomp.Import.Retro;

namespace MphRecomp.Import
{
    // The in-app importer (docs/IMPORT_PIPELINE.md section 7): the user gives the app their own game files; every item
    // (a recipe from recipes/<kind>/) is made from them by code in this library and written under the output root in
    // the layout the renderers read (<root>/guns/<Suit>/, ...). The front-end's IMPORT GAMES page drives it:
    //   Games                              every importable game with its status and each item's
    //   AcceptedSources(gameId)            file extensions (or a folder) for the SAF picker
    //   Import(gameId, source, progress)   off the GL thread; the source is a seekable stream read in place (a disc
    //                                      image), a folder path (the game's extracted file tree), or any IFileSource
    // Each import records what it made in <root>/import/<gameId>.json (source hashes, recipe hashes, per item status).
    // Item kinds plug in through IImportKind (Register); "guns" is built in.

    public enum GameImportStatus { NotProvided, HashMismatch, Partial, Complete }

    public enum ItemImportStatus { Missing, Complete, Stale, HashMismatch, Failed }

    public sealed class ImportGameInfo
    {
        public string Id = "", Title = "", Platform = "";
        public GameImportStatus Status;
        public string? DiscId;
        public readonly List<ImportItemInfo> Items = new();
    }

    public sealed class ImportItemInfo
    {
        public string Id = "", Kind = "", Name = "";
        public ItemImportStatus Status;
        public string Message = "";
        public double Seconds;
    }

    public sealed class ImportSources
    {
        public string[] Extensions = Array.Empty<string>();   // ".iso", ".gcm"
        public bool Folder;                                     // a folder instead (Switch RomFS)
    }

    public sealed class ImportReport
    {
        public string GameId = "", DiscId = "";
        public double Seconds;
        public readonly List<ImportItemInfo> Items = new();
        public bool AllComplete => Items.Count > 0 && Items.All(i => i.Status == ItemImportStatus.Complete);
    }

    // one kind of importable item: recipes/<Kind>/<Name>.json -> <root>/<Kind>/<Name>/
    public interface IImportKind
    {
        string Kind { get; }
        // the files each recipe reads from the source, with the SHA-1 each was authored against
        IEnumerable<(string Path, string Sha1)> Sources(ImportRecipe recipe);
        // make the item into outDir (an empty staging folder); progress: (fraction of this item, message)
        void Run(ImportContext ctx, ImportRecipe recipe, string outDir, Action<float, string> progress, CancellationToken ct);
    }

    // what one import has open: the game's file tree (a disc, a folder), and its files / paks / hashes, each read once
    public sealed class ImportContext
    {
        public readonly IFileSource Source;
        readonly Dictionary<string, byte[]> _files = new(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, Pak> _paks = new(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, string> _sha1 = new(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, object> _shared = new(StringComparer.OrdinalIgnoreCase);

        public ImportContext(IFileSource source) { Source = source; }

        // the source when it is a GameCube disc image
        public GcDisc? Disc => Source as GcDisc;

        public byte[] File(string path)
        {
            if (!_files.TryGetValue(path, out byte[]? b))
            {
                b = Source.ReadFile(path);
                _files[path] = b;
            }
            return b;
        }

        // one object per key for the whole import (an item kind's own readers: "pak3:MP3/SamusGun.pak")
        public T Shared<T>(string key, Func<T> make) where T : class
        {
            if (!_shared.TryGetValue(key, out object? o))
            {
                o = make();
                _shared[key] = o;
            }
            return (T)o;
        }

        public Pak Pak(string path)
        {
            if (!_paks.TryGetValue(path, out Pak? p))
            {
                p = new Pak(File(path), path);
                _paks[path] = p;
            }
            return p;
        }

        public string Sha1(string path)
        {
            if (!_sha1.TryGetValue(path, out string? h))
            {
                h = Convert.ToHexString(SHA1.HashData(File(path))).ToLowerInvariant();
                _sha1[path] = h;
            }
            return h;
        }
    }

    public sealed class ImportService
    {
        sealed class GameDef
        {
            public string Id = "", Title = "", Platform = "";
            public string[] DiscPrefixes = Array.Empty<string>();
            public string[] Extensions = Array.Empty<string>();
            // a folder holding the game's extracted file tree is accepted too; FolderMarker is a path every such tree
            // has (the tree's root is found from it: FolderSource.Locate)
            public bool Folder;
            public string FolderMarker = "";
        }

        static readonly GameDef[] KnownGames =
        {
            new() { Id = "mp1", Title = "Metroid Prime", Platform = "GameCube", DiscPrefixes = new[] { "GM8" }, Extensions = new[] { ".iso", ".gcm", ".rvz", ".wia" } },
            new() { Id = "mp2", Title = "Metroid Prime 2: Echoes", Platform = "GameCube", DiscPrefixes = new[] { "G2M" }, Extensions = new[] { ".iso", ".gcm", ".rvz", ".wia" } },
            // Prime 3 from Metroid Prime Trilogy (Wii): its disc image -- RVZ/WIA need no key (Dolphin stores partition
            // data decrypted); a plain .iso/.wbfs needs the user's own key file (WiiKeys) -- or, for development, the
            // disc's extracted file tree (Dolphin: Extract Entire Disc -> DATA/files, holding MP3/*.pak)
            new() { Id = "mp3", Title = "Metroid Prime 3: Corruption (Metroid Prime Trilogy)", Platform = "Wii", DiscPrefixes = new[] { "R3M" },
                Extensions = new[] { ".rvz", ".wia", ".iso", ".wbfs" }, Folder = true, FolderMarker = "MP3" },
            // TODO "mp4" (Metroid Prime 4: Beyond: the user's extracted RomFS folder, Folder = true, no Extensions): its
            // recipes (recipes/guns/MP4*.json, Gun4Recipe) and Guns/Gun4Builder are ready (-importcheck guns4), but the
            // source side is not: the Beyond RomFS pak + SMDL/CMDL + MATI/TXTR readers and the material conversion
            // (implementing Beyond/BeyondSources.cs), and a hash check for resources inside a pak (GunImportKind.Sources)
        };

        readonly Dictionary<string, IImportKind> _kinds = new(StringComparer.Ordinal);

        public string OutputRoot { get; }
        public RecipeStore Recipes { get; }
        // the user's own Wii common key file, loaded by the app (WiiCommonKeys.FromFile) -- only plain Wii .iso/.wbfs
        // images need it; the app never contains a key (docs/IMPORT_PIPELINE.md section 6)
        public WiiCommonKeys? WiiKeys { get; set; }

        public ImportService(string outputRoot, RecipeStore? recipes = null)
        {
            OutputRoot = outputRoot;
            Recipes = recipes ?? RecipeStore.Embedded();
            Register(new GunImportKind());
        }

        public void Register(IImportKind kind) => _kinds[kind.Kind] = kind;

        public string StatePath(string gameId) => Path.Combine(OutputRoot, "import", gameId + ".json");

        public string ItemDir(ImportRecipe r) => Path.Combine(OutputRoot, r.Kind, r.Name);

        static GameDef Def(string gameId) => KnownGames.FirstOrDefault(g => g.Id == gameId)
            ?? throw new ArgumentException($"unknown game '{gameId}'", nameof(gameId));

        public ImportSources AcceptedSources(string gameId) => new() { Extensions = (string[])Def(gameId).Extensions.Clone(), Folder = Def(gameId).Folder };

        // every importable game, with its status from the last import and the recipes it has now
        public IReadOnlyList<ImportGameInfo> Games
        {
            get
            {
                var list = new List<ImportGameInfo>();
                foreach (GameDef def in KnownGames)
                {
                    var g = new ImportGameInfo { Id = def.Id, Title = def.Title, Platform = def.Platform };
                    Dictionary<string, (string Status, string RecipeSha1, string Message)> state = ReadState(def.Id, out string? discId, out bool provided);
                    g.DiscId = discId;
                    foreach (ImportRecipe r in Recipes.ForGame(def.Id))
                    {
                        var it = new ImportItemInfo { Id = r.Id, Kind = r.Kind, Name = r.Name, Status = ItemImportStatus.Missing };
                        if (state.TryGetValue(r.Id, out (string Status, string RecipeSha1, string Message) s))
                        {
                            it.Message = s.Message;
                            it.Status = s.Status switch
                            {
                                "complete" => s.RecipeSha1 != r.Sha1 ? ItemImportStatus.Stale
                                    : Directory.Exists(ItemDir(r)) ? ItemImportStatus.Complete : ItemImportStatus.Missing,
                                "hash-mismatch" => ItemImportStatus.HashMismatch,
                                "failed" => ItemImportStatus.Failed,
                                _ => ItemImportStatus.Missing,
                            };
                        }
                        g.Items.Add(it);
                    }
                    int complete = g.Items.Count(i => i.Status == ItemImportStatus.Complete);
                    g.Status = !provided ? GameImportStatus.NotProvided
                        : g.Items.Count > 0 && complete == g.Items.Count ? GameImportStatus.Complete
                        : complete == 0 && g.Items.Count > 0 && g.Items.All(i => i.Status == ItemImportStatus.HashMismatch) ? GameImportStatus.HashMismatch
                        : GameImportStatus.Partial;
                    list.Add(g);
                }
                return list;
            }
        }

        // sourcePath: a disc image file, or (for a game that accepts one) a folder holding the extracted file tree
        public ImportReport Import(string gameId, string sourcePath, IProgress<(string item, float fraction, string message)>? progress = null,
            CancellationToken ct = default, IReadOnlyCollection<string>? only = null)
        {
            if (Directory.Exists(sourcePath))
            {
                GameDef def = Def(gameId);
                if (!def.Folder) throw new InvalidDataException($"{def.Title} is imported from its disc image ({String.Join(" / ", def.Extensions)}), not a folder");
                string root = FolderSource.Locate(sourcePath, def.FolderMarker)
                    ?? throw new InvalidDataException($"{sourcePath} holds no {def.Title} files (no {def.FolderMarker} in it)");
                using var folder = new FolderSource(root);
                return Import(gameId, folder, progress, ct, only);
            }
            using FileStream f = new(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.RandomAccess);
            return Import(gameId, f, progress, ct, only);
        }

        // only: item ids ("guns/MP1PowerSuit") or names ("MP1PowerSuit") to make; null = every recipe of the game
        public ImportReport Import(string gameId, Stream source, IProgress<(string item, float fraction, string message)>? progress = null,
            CancellationToken ct = default, IReadOnlyCollection<string>? only = null)
        {
            GameDef def = Def(gameId);
            var clock = Stopwatch.StartNew();
            if (def.Extensions.Length == 0)
            {
                throw new InvalidDataException($"{def.Title}: disc images are not supported yet; give the folder holding its extracted files");
            }
            // GameCube .iso/.gcm, Wii .iso/.wbfs, and .wia/.rvz of either platform (Disc/DiscImage)
            if (DiscImage.Detect(source) == DiscImageFormat.Unknown)
            {
                throw new InvalidDataException($"not a disc image ({def.Title} expects {String.Join(" / ", def.Extensions)})");
            }
            DiscPlatform? platform = DiscImage.PlatformOf(source);
            if (platform != null && platform.ToString() != def.Platform)
            {
                throw new InvalidDataException($"this is a {platform} disc; {def.Title} is a {def.Platform} game");
            }
            IFileSource disc = DiscImage.Open(source, WiiKeys, leaveOpen: true); // WiiKeyRequiredException: plain Wii image, no/wrong key
            try
            {
                if (!def.DiscPrefixes.Any(p => disc.GameId.StartsWith(p, StringComparison.Ordinal)))
                {
                    throw new InvalidDataException($"this disc is {disc.GameId} ({disc.Title}), not {def.Title}");
                }
                return ImportFrom(def, disc, progress, ct, only, clock);
            }
            finally
            {
                (disc as IDisposable)?.Dispose();
            }
        }

        // a game's file tree from any source (a disc's file system, an extracted folder). A source naming its game
        // (GameId) must name this one; one that does not (a bare folder) is checked by the recipes' file hashes only.
        public ImportReport Import(string gameId, IFileSource source, IProgress<(string item, float fraction, string message)>? progress = null,
            CancellationToken ct = default, IReadOnlyCollection<string>? only = null)
        {
            GameDef def = Def(gameId);
            var clock = Stopwatch.StartNew();
            if (source.GameId.Length > 0 && !def.DiscPrefixes.Any(p => source.GameId.StartsWith(p, StringComparison.Ordinal)))
            {
                throw new InvalidDataException($"these files are {source.GameId} ({source.Title}), not {def.Title}");
            }
            if (def.FolderMarker.Length > 0 && source is FolderSource fs
                && !fs.Paths.Any(p => p.StartsWith(def.FolderMarker + "/", StringComparison.OrdinalIgnoreCase) || p.Equals(def.FolderMarker, StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidDataException($"{fs.Root} holds no {def.Title} files (no {def.FolderMarker} in it)");
            }
            return ImportFrom(def, source, progress, ct, only, clock);
        }

        ImportReport ImportFrom(GameDef def, IFileSource disc, IProgress<(string item, float fraction, string message)>? progress,
            CancellationToken ct, IReadOnlyCollection<string>? only, Stopwatch clock)
        {
            string gameId = def.Id;
            var report = new ImportReport { GameId = gameId, DiscId = disc.GameId.Length > 0 ? disc.GameId : "folder" };
            var ctx = new ImportContext(disc);
            List<ImportRecipe> recipes = Recipes.ForGame(gameId)
                .Where(r => only == null || only.Contains(r.Id) || only.Contains(r.Name)).ToList();
            var sourceHashes = new Dictionary<string, string>();
            try
            {
                for (int i = 0; i < recipes.Count; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    ImportRecipe r = recipes[i];
                    var item = new ImportItemInfo { Id = r.Id, Kind = r.Kind, Name = r.Name };
                    report.Items.Add(item);
                    int index = i;
                    void Report(float f, string msg) => progress?.Report((r.Id, (index + Math.Clamp(f, 0, 1)) / recipes.Count, msg));
                    var itemClock = Stopwatch.StartNew();
                    if (!_kinds.TryGetValue(r.Kind, out IImportKind? kind))
                    {
                        item.Status = ItemImportStatus.Failed; item.Message = $"no importer for '{r.Kind}'";
                        continue;
                    }
                    Report(0, "checking the source");
                    string? mismatch = null;
                    foreach ((string path, string sha1) in kind.Sources(r))
                    {
                        if (!disc.Contains(path))
                        {
                            mismatch = disc is GcDisc ? $"{path} is not on this disc" : $"{path} is not in these files";
                            break;
                        }
                        string h = ctx.Sha1(path);
                        sourceHashes[path] = h;
                        if (!String.Equals(h, sha1, StringComparison.OrdinalIgnoreCase))
                        {
                            mismatch = $"{path} is a different revision (SHA-1 {h}, the recipe was made from {sha1})";
                            break;
                        }
                    }
                    if (mismatch != null)
                    {
                        item.Status = ItemImportStatus.HashMismatch; item.Message = mismatch;
                        Report(1, mismatch);
                        continue;
                    }
                    string final = ItemDir(r);
                    string staging = Path.Combine(OutputRoot, r.Kind, "." + r.Name + ".importing");
                    if (Directory.Exists(staging)) Directory.Delete(staging, true);   // our own leftover from an interrupted import
                    Directory.CreateDirectory(staging);
                    try
                    {
                        kind.Run(ctx, r, staging, Report, ct);
                        Commit(staging, final);
                        item.Status = ItemImportStatus.Complete; item.Message = "done";
                    }
                    catch (OperationCanceledException)
                    {
                        Directory.Delete(staging, true);
                        item.Status = ItemImportStatus.Missing; item.Message = "cancelled";
                        throw;
                    }
                    catch (Exception e)
                    {
                        // one item failing (a damaged or unexpected resource) leaves the others to import
                        Directory.Delete(staging, true);
                        item.Status = ItemImportStatus.Failed; item.Message = e.GetType().Name + ": " + e.Message;
                    }
                    item.Seconds = itemClock.Elapsed.TotalSeconds;
                    Report(1, item.Message);
                }
            }
            finally
            {
                report.Seconds = clock.Elapsed.TotalSeconds;
                WriteState(report, sourceHashes);
            }
            return report;
        }

        static bool LooksLikeRvz(Stream s)
        {
            if (!s.CanSeek || s.Length < 4) return false;
            long pos = s.Position;
            try
            {
                var b = new byte[4];
                s.Seek(0, SeekOrigin.Begin);
                s.ReadExactly(b);
                return b[0] == (byte)'R' && b[1] == (byte)'V' && b[2] == (byte)'Z' && b[3] == 1;
            }
            finally { s.Position = pos; }
        }

        // move the staged files into the item's folder (replacing the files of the same name; nothing else is touched)
        static void Commit(string staging, string final)
        {
            Directory.CreateDirectory(final);
            foreach (string f in Directory.GetFiles(staging))
            {
                File.Move(f, Path.Combine(final, Path.GetFileName(f)), overwrite: true);
            }
            Directory.Delete(staging, false);
        }

        void WriteState(ImportReport report, Dictionary<string, string> sourceHashes)
        {
            Dictionary<string, (string Status, string RecipeSha1, string Message)> old = ReadState(report.GameId, out _, out _);
            string path = StatePath(report.GameId);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var ms = new MemoryStream();
            using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
            {
                w.WriteStartObject();
                w.WriteString("game", report.GameId);
                w.WriteString("discId", report.DiscId);
                w.WriteString("time", DateTime.UtcNow.ToString("o"));
                w.WriteStartObject("sources");
                foreach (KeyValuePair<string, string> kv in sourceHashes) w.WriteString(kv.Key, kv.Value);
                w.WriteEndObject();
                w.WriteStartObject("items");
                var written = new HashSet<string>();
                foreach (ImportItemInfo it in report.Items)
                {
                    ImportRecipe? r = Recipes.All.FirstOrDefault(x => x.Id == it.Id);
                    string status = it.Status switch
                    {
                        ItemImportStatus.Complete => "complete",
                        ItemImportStatus.HashMismatch => "hash-mismatch",
                        ItemImportStatus.Failed => "failed",
                        _ => "missing",
                    };
                    w.WriteStartObject(it.Id);
                    w.WriteString("status", status);
                    w.WriteString("recipeSha1", r?.Sha1 ?? "");
                    w.WriteString("message", it.Message);
                    w.WriteEndObject();
                    written.Add(it.Id);
                }
                // items this import did not touch keep what the last one recorded
                foreach (KeyValuePair<string, (string Status, string RecipeSha1, string Message)> kv in old)
                {
                    if (written.Contains(kv.Key)) continue;
                    w.WriteStartObject(kv.Key);
                    w.WriteString("status", kv.Value.Status);
                    w.WriteString("recipeSha1", kv.Value.RecipeSha1);
                    w.WriteString("message", kv.Value.Message);
                    w.WriteEndObject();
                }
                w.WriteEndObject();
                w.WriteEndObject();
            }
            File.WriteAllBytes(path, ms.ToArray());
        }

        Dictionary<string, (string Status, string RecipeSha1, string Message)> ReadState(string gameId, out string? discId, out bool provided)
        {
            var d = new Dictionary<string, (string, string, string)>();
            discId = null; provided = false;
            string path = StatePath(gameId);
            if (!File.Exists(path)) return d;
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path, Encoding.UTF8));
                JsonElement root = doc.RootElement;
                provided = true;
                discId = root.TryGetProperty("discId", out JsonElement di) ? di.GetString() : null;
                if (root.TryGetProperty("items", out JsonElement items))
                {
                    foreach (JsonProperty p in items.EnumerateObject())
                    {
                        string S(string k) => p.Value.TryGetProperty(k, out JsonElement e) ? e.GetString() ?? "" : "";
                        d[p.Name] = (S("status"), S("recipeSha1"), S("message"));
                    }
                }
            }
            catch (JsonException)
            {
                // a damaged state file reads as "not imported yet"
                provided = false;
            }
            return d;
        }
    }
}
