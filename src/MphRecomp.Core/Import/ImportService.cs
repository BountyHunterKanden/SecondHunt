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
using MphRecomp.Import.Switch;

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
        public bool New;   // not imported and added since the game's last import: UPDATE ALL takes it, a red badge marks it
        public string Message = "";
        public double Seconds;
    }

    public sealed class ImportSources
    {
        public string[] Extensions = Array.Empty<string>();   // ".iso", ".gcm"
        public bool Folder;                                     // a folder instead (Switch RomFS)
        public string Marker = "";                              // a path every such folder holds ("Preload")
    }

    public sealed class ImportReport
    {
        public string GameId = "", DiscId = "";
        public double Seconds;
        public readonly List<ImportItemInfo> Items = new();
        public bool AllComplete => Items.Count > 0 && Items.All(i => i.Status == ItemImportStatus.Complete);
        // import retention: what the import kept of the source files (null = it didn't try); "" = kept, else why not
        public string? KeepError;
        public long KeptBytes;
        // the family the keep worked out (SourceFamily.Stats; null = no keep): what was read, the family, its files whole
        public SourceFamily.Stats? KeepFamily;
    }

    // one kind of importable item: recipes/<Kind>/<Name>.json -> <root>/<Kind>/<Name>/
    public interface IImportKind
    {
        string Kind { get; }
        // the files each recipe reads from the source, with the SHA-1 each was authored against
        IEnumerable<(string Path, string Sha1)> Sources(ImportRecipe recipe);
        // make the item into outDir (an empty staging folder); progress: (fraction of this item, message)
        void Run(ImportContext ctx, ImportRecipe recipe, string outDir, Action<float, string> progress, CancellationToken ct);
        // import retention: files of the source related to the item that no asset id links to, kept whole with its family
        IEnumerable<string> AlsoKeep(ImportRecipe recipe) => Array.Empty<string>();
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

        // the source when it is a GameCube ROM
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
                // read through, not held: a file not already loaded may be far bigger than a phone can hold (the standalone
                // Corruption disc's InGameAudio.pak is ~740 MB; Dark Samus Hyper Mode, 2026-10-12)
                if (_files.TryGetValue(path, out byte[]? loaded)) h = Convert.ToHexString(SHA1.HashData(loaded)).ToLowerInvariant();
                else if (Source is IKnownHashes known && known.TryKnownSha1(path, out string? kept)) h = kept;   // a kept set's sparse files
                else
                {
                    // a hash check isn't a read the importers need: it doesn't count towards the kept set
                    IFileSource real = Source is RecordingSource rec ? rec.Inner : Source;
                    using Stream s = real.OpenRead(path);
                    h = Convert.ToHexString(SHA1.HashData(s)).ToLowerInvariant();
                }
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
            // Prime 3 from Metroid Prime Trilogy (Wii): its ROM -- RVZ/WIA need no key (Dolphin stores partition
            // data decrypted); a plain .iso/.wbfs needs the user's own key file (WiiKeys) -- or, for development, the
            // disc's extracted file tree (Dolphin: Extract Entire Disc -> DATA/files, holding MP3/*.pak)
            new() { Id = "mp3", Title = "Metroid Prime 3: Corruption (Metroid Prime Trilogy)", Platform = "Wii", DiscPrefixes = new[] { "R3M" },
                Extensions = new[] { ".rvz", ".wia", ".iso", ".wbfs" }, Folder = true, FolderMarker = "MP3" },
            // Super Smash Bros. Brawl (Wii): Zero Suit's Paralyzer effects (docs/BRAWL_PARALYZER.md); RVZ/WIA need no key, a
            // plain .iso/.wbfs needs the user's key file; or the extracted tree (DATA/files, holding effect/)
            new() { Id = "ssbb", Title = "Super Smash Bros. Brawl", Platform = "Wii", DiscPrefixes = new[] { "RSB" },
                Extensions = new[] { ".rvz", ".wia", ".iso", ".wbfs" }, Folder = true, FolderMarker = "effect" },
            // Metroid Prime 4: Beyond (Switch): the user's own game file (.xci / .nsp, read with their own prod.keys: SwitchKeys)
            // or extracted RomFS folder; its root holds Preload/.
            // Sylux (recipes/hd/MP4Sylux.json) imports through the clean-room Beyond readers (Import/Beyond, CR6). TODO: its
            // arm cannons (recipes/guns/MP4*.json, Gun4Builder, -importcheck guns4) still need IBeyondModelSource /
            // IBeyondLookSource on those readers (GunImportKind refuses them for now)
            new() { Id = "mp4", Title = "Metroid Prime 4: Beyond", Platform = "Switch", Extensions = SwitchGame.Extensions, Folder = true, FolderMarker = "Preload" },
        };

        readonly Dictionary<string, IImportKind> _kinds = new(StringComparer.Ordinal);

        public string OutputRoot { get; }
        public RecipeStore Recipes { get; }
        // other discs a game's files are on (Trilogy's MP1/ and MP2/, the original Prime 3 disc): Import/Editions.cs
        public List<ImportEdition> Editions { get; }
        // the user's own Wii common key file, loaded by the app (WiiCommonKeys.FromFile) -- only plain Wii .iso/.wbfs
        // images need it; the app never contains a key (docs/IMPORT_PIPELINE.md section 6)
        public WiiCommonKeys? WiiKeys { get; set; }
        // the user's own Switch prod.keys (+ title.keys), loaded by the app (SwitchKeys.FromFile): .xci / .nsp files need them
        public SwitchKeys? SwitchKeys { get; set; }

        public ImportService(string outputRoot, RecipeStore? recipes = null)
        {
            OutputRoot = outputRoot;
            Recipes = recipes ?? RecipeStore.Embedded();
            Editions = Directory.Exists(Recipes.Origin) ? ImportEdition.FromDirectory(Recipes.Origin) : ImportEdition.Embedded();
            Register(new GunImportKind());
            Register(new Hd.HdImportKind());   // HD model folders (docs/CLEANROOM_QUEUE.md CR1)
            Register(new Bodies.BodyImportKind());   // native Retro bodies for multiplayer characters (docs/CHARACTERS_AND_SKINS.md)
            Register(new Echoes.EchoesArenaImportKind());   // Echoes arenas + their music (CR2)
            Register(new Echoes.MusicImportKind());
            Register(new BrawlFx.BrawlFxImportKind());   // Brawl's Paralyzer effects (docs/BRAWL_PARALYZER.md)
            Register(new PrimeMusic.TrackImportKind());   // the Prime games' songs, one a recipe (docs/PRIME_MUSIC.md)
        }

        public void Register(IImportKind kind) => _kinds[kind.Kind] = kind;

        public string StatePath(string gameId) => Path.Combine(OutputRoot, "import", gameId + ".json");

        public string ItemDir(ImportRecipe r) => Path.Combine(OutputRoot, r.Kind, r.Name);

        static GameDef Def(string gameId) => KnownGames.FirstOrDefault(g => g.Id == gameId)
            ?? throw new ArgumentException($"unknown game '{gameId}'", nameof(gameId));

        public ImportSources AcceptedSources(string gameId) => new()
        {
            Extensions = Def(gameId).Extensions.Concat(Editions.Where(e => e.Game == gameId).SelectMany(e => e.Extensions))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            Folder = Def(gameId).Folder, Marker = Def(gameId).FolderMarker,
        };

        // the files a recipe reads from the source, with the SHA-1 it was authored against (its kind's Sources)
        public IEnumerable<(string Path, string Sha1)> SourcesOf(ImportRecipe r) =>
            _kinds.TryGetValue(r.Kind, out IImportKind? kind) ? kind.Sources(r) : Enumerable.Empty<(string, string)>();

        // the edition a disc that isn't the game's own disc carries the game as (Trilogy for Prime 1 / Echoes, ...), or null
        ImportEdition? EditionFor(GameDef def, string discId) =>
            discId.Length == 0 ? null : Editions.FirstOrDefault(e => e.Game == def.Id && e.Matches(discId));

        // every importable game, with its status from the last import and the recipes it has now
        public IReadOnlyList<ImportGameInfo> Games
        {
            get
            {
                var list = new List<ImportGameInfo>();
                foreach (GameDef def in KnownGames)
                {
                    var g = new ImportGameInfo { Id = def.Id, Title = def.Title, Platform = def.Platform };
                    Dictionary<string, (string Status, string RecipeSha1, string Message, int Importer)> state = ReadState(def.Id, out string? discId, out bool provided, out HashSet<string>? known);
                    g.DiscId = discId;
                    foreach (ImportRecipe r in Recipes.ForGame(def.Id))
                    {
                        var it = new ImportItemInfo { Id = r.Id, Kind = r.Kind, Name = r.Name, Status = ItemImportStatus.Missing };
                        if (state.TryGetValue(r.Id, out (string Status, string RecipeSha1, string Message, int Importer) s))
                        {
                            it.Message = s.Message;
                            it.Status = s.Status switch
                            {
                                "complete" => s.RecipeSha1 != r.Sha1 || s.Importer != ImporterVersions.For(r.Kind) ? ItemImportStatus.Stale
                                    : Directory.Exists(ItemDir(r)) ? ItemImportStatus.Complete : ItemImportStatus.Missing,
                                "hash-mismatch" => ItemImportStatus.HashMismatch,
                                "failed" => ItemImportStatus.Failed,
                                _ => ItemImportStatus.Missing,
                            };
                        }
                        // new since the last import: the app didn't have it then (a state file from before the list: the
                        // items Beta 3 added to a game a player may have imported already)
                        it.New = provided && it.Status == ItemImportStatus.Missing && !state.ContainsKey(r.Id)
                            && (known != null ? !known.Contains(r.Id) : AddedBeforeKnownList.Contains(r.Id));
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

        // sourcePath: a ROM file, or (for a game that accepts one) a folder holding the extracted file tree
        public ImportReport Import(string gameId, string sourcePath, IProgress<(string item, float fraction, string message)>? progress = null,
            CancellationToken ct = default, IReadOnlyCollection<string>? only = null)
        {
            if (Directory.Exists(sourcePath))
            {
                GameDef def = Def(gameId);
                if (!def.Folder) throw new InvalidDataException($"{def.Title} is imported from its ROM ({String.Join(" / ", def.Extensions)}), not a folder");
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
            // a Switch game file (.xci / .nsp): its RomFS, decrypted in place with the user's own keys (Import/Switch)
            if (SwitchGame.Detect(source) != SwitchFileFormat.Unknown)
            {
                if (def.Platform != "Switch") throw new InvalidDataException($"this is a Switch game file; {def.Title} is a {def.Platform} game");
                using SwitchRomFs romfs = SwitchGame.Open(source, SwitchKeys
                    ?? throw new SwitchKeyRequiredException("Switch game files are encrypted: give your own prod.keys, dumped from your own Switch"));
                if (def.FolderMarker.Length > 0 && !romfs.Paths.Any(p => p.StartsWith(def.FolderMarker + "/", StringComparison.OrdinalIgnoreCase)))
                {
                    throw new InvalidDataException($"this Switch game ({romfs.Title}) is not {def.Title} (no {def.FolderMarker} in it)");
                }
                return Import(gameId, romfs, progress, ct, only);
            }
            if (def.Extensions.Length == 0)
            {
                throw new InvalidDataException($"{def.Title}: ROMs are not supported yet; give the folder holding its extracted files");
            }
            // GameCube .iso/.gcm, Wii .iso/.wbfs, and .wia/.rvz of either platform (Disc/DiscImage)
            if (DiscImage.Detect(source) == DiscImageFormat.Unknown)
            {
                throw new InvalidDataException($"not a ROM ({def.Title} expects {String.Join(" / ", def.Extensions)})");
            }
            DiscPlatform? platform = DiscImage.PlatformOf(source);
            if (platform != null && platform.ToString() != def.Platform && !Editions.Any(e => e.Game == def.Id && e.Platform == platform.ToString()))
            {
                throw new InvalidDataException($"this is a {platform} disc; {def.Title} is a {def.Platform} game");
            }
            IFileSource disc = DiscImage.Open(source, WiiKeys, leaveOpen: true); // WiiKeyRequiredException: plain Wii image, no/wrong key
            try
            {
                if (!def.DiscPrefixes.Any(p => disc.GameId.StartsWith(p, StringComparison.Ordinal)))
                {
                    ImportEdition edition = EditionFor(def, disc.GameId)
                        ?? throw new InvalidDataException($"this disc is {disc.GameId} ({disc.Title}), not {def.Title}");
                    using IFileSource mapped = edition.Map(disc);
                    return ImportFrom(def, mapped, progress, ct, only, clock, edition);
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
                ImportEdition edition = EditionFor(def, source.GameId)
                    ?? throw new InvalidDataException($"these files are {source.GameId} ({source.Title}), not {def.Title}");
                using IFileSource mapped = edition.Map(source);
                return ImportFrom(def, mapped, progress, ct, only, clock, edition);
            }
            if (def.FolderMarker.Length > 0 && source is FolderSource fs
                && !fs.Paths.Any(p => p.StartsWith(def.FolderMarker + "/", StringComparison.OrdinalIgnoreCase) || p.Equals(def.FolderMarker, StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidDataException($"{fs.Root} holds no {def.Title} files (no {def.FolderMarker} in it)");
            }
            return ImportFrom(def, source, progress, ct, only, clock);
        }

        ImportReport ImportFrom(GameDef def, IFileSource disc, IProgress<(string item, float fraction, string message)>? progress,
            CancellationToken ct, IReadOnlyCollection<string>? only, Stopwatch clock, ImportEdition? edition = null)
        {
            string gameId = def.Id;
            var report = new ImportReport { GameId = gameId, DiscId = disc.GameId.Length > 0 ? disc.GameId : "folder" };
            // import retention: every read of the source is noted, and what the importers read is kept afterwards
            RecordingSource? recorder = KeepSources && disc is not KeptSource ? new RecordingSource(disc) : null;
            var ctx = new ImportContext(recorder ?? disc);
            List<ImportRecipe> recipes = Recipes.ForGame(gameId)
                .Where(r => only == null || only.Contains(r.Id) || only.Contains(r.Name)).ToList();
            var sourceHashes = new Dictionary<string, string>();
            Dictionary<string, (string Status, string RecipeSha1, string Message, int Importer)>? before = edition != null ? ReadState(gameId, out _, out _, out _) : null;
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
                    // an item the edition can't make, already made from the game's own disc: kept as it is
                    if (edition != null && !edition.Items.Contains(r.Id) && before != null && before.TryGetValue(r.Id, out var was)
                        && was.Status == "complete" && was.RecipeSha1 == r.Sha1 && was.Importer == ImporterVersions.For(r.Kind)
                        && Directory.Exists(ItemDir(r)))
                    {
                        item.Status = ItemImportStatus.Complete; item.Message = $"kept: made from {def.Title} itself before";
                        Report(1, item.Message);
                        continue;
                    }
                    Report(0, "checking the source");
                    string? mismatch = CheckSources(ctx, disc, def, edition, r, kind, sourceHashes);
                    if (mismatch != null)
                    {
                        // a kept set that lacks a file an updated importer wants isn't a wrong disc: it needs the disc again
                        item.Status = mismatch.StartsWith(NeedsDiscPrefix, StringComparison.Ordinal) ? ItemImportStatus.Failed : ItemImportStatus.HashMismatch;
                        item.Message = mismatch;
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
                        Commit(r, staging, final);
                        item.Status = ItemImportStatus.Complete; item.Message = "done";
                    }
                    catch (OperationCanceledException)
                    {
                        Directory.Delete(staging, true);
                        item.Status = ItemImportStatus.Missing; item.Message = "cancelled";
                        throw;
                    }
                    catch (SourceMissingException sm)
                    {
                        if (Directory.Exists(staging)) Directory.Delete(staging, true);
                        item.Status = ItemImportStatus.Failed; item.Message = $"{NeedsDiscPrefix}: {sm.Path} isn't in the copied files";
                    }
                    catch (Exception e)
                    {
                        // one item failing (a damaged or unexpected resource) leaves the others to import. Commit may have
                        // moved the staging folder already: a delete of a gone folder would throw past this catch, fail
                        // the whole run and hide e (owner 2026-10-26, Samsung: the disc was blamed for a file error)
                        if (Directory.Exists(staging)) Directory.Delete(staging, true);
                        item.Status = ItemImportStatus.Failed; item.Message = e.GetType().Name + ": " + e.Message;
                    }
                    item.Seconds = itemClock.Elapsed.TotalSeconds;
                    Report(1, item.Message);
                }
                if (recorder != null)
                {
                    ct.ThrowIfCancellationRequested();
                    KeepSourceFiles(def, recorder, ctx, report, sourceHashes, edition, progress, ct);
                }
                else if (disc is KeptSource)
                {
                    // a rebuild from the kept files: what it made is covered at today's stamps
                    var made = new Dictionary<string, string>(StringComparer.Ordinal);
                    foreach (ImportItemInfo it in report.Items.Where(i => i.Status == ItemImportStatus.Complete && i.Message == "done"))
                    {
                        ImportRecipe? r = Recipes.All.FirstOrDefault(x => x.Id == it.Id);
                        if (r != null) made[r.Id] = Stamp(r);
                    }
                    try { SourceKeep.MarkCovered(OutputRoot, def.Id, made); }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException) { }
                }
            }
            catch (OperationCanceledException)
            {
                // a stopped import keeps nothing of itself (owner 2026-10-07, Reports R10): the items it finished are removed
                // again, and the ones it only checked keep what the last import recorded
                foreach (ImportItemInfo it in report.Items.Where(i => i.Status == ItemImportStatus.Complete && i.Message == "done"))
                {
                    DeleteMade(recipes.First(x => x.Id == it.Id));
                    it.Status = ItemImportStatus.Missing; it.Message = StoppedMessage;
                }
                report.Items.RemoveAll(i => i.Status is ItemImportStatus.HashMismatch or ItemImportStatus.Failed);
                throw;
            }
            finally
            {
                report.Seconds = clock.Elapsed.TotalSeconds;
                WriteState(report, sourceHashes);
            }
            return report;
        }

        // an item that can't be made from the source: why (null = it can). Hashes the files its kind declares.
        string? CheckSources(ImportContext ctx, IFileSource disc, GameDef def, ImportEdition? edition, ImportRecipe r, IImportKind kind,
            Dictionary<string, string> sourceHashes)
        {
            if (edition != null && !edition.Items.Contains(r.Id))
            {
                return $"{r.Name} isn't made from the {edition.Title} disc: it needs the {(def.Id == "mp3" ? "Metroid Prime Trilogy" : def.Title)} disc ({def.Platform}); import it from that one";
            }
            foreach ((string path, string sha1) in kind.Sources(r))
            {
                if (!disc.Contains(path))
                {
                    return disc is KeptSource ? $"{NeedsDiscPrefix}: {path} isn't in the copied files"
                        : disc is GcDisc ? $"{path} is not on this disc" : $"{path} is not in these files";
                }
                string h = ctx.Sha1(path);
                sourceHashes[path] = h;
                if (!String.Equals(h, sha1, StringComparison.OrdinalIgnoreCase) && !(edition?.Accepts(path, sha1, h) ?? false))
                {
                    return $"{path} is a different revision (SHA-1 {h}, the recipe was made from {sha1})";
                }
            }
            return null;
        }

        // ---- import retention (docs/IMPORT_RETENTION.md): keep the source files the importers read ----

        // the apps turn this on: each import then keeps what the importers read of the source under <root>/source/<game>/
        // (SourceKeep), so the ROM can be deleted and updates rebuild from the kept files
        public bool KeepSources { get; set; }

        // the recipes the app offers a game's player (ImportGames.Offered); the kept files cover those. null = every recipe
        public Func<ImportRecipe, bool>? KeepFilter { get; set; }

        // the kept set is whole files when they total at most this (owner 2026-10-23), else the byte ranges the importers read
        public const long KeepWholeLimit = 500L * 1024 * 1024;

        // an item message starting so means the kept files lack something: the game's disc is needed again
        public const string NeedsDiscPrefix = "needs the disc again";

        public bool HasKept(string gameId) => SourceKeep.Exists(OutputRoot, gameId);

        public long KeptBytes(string gameId) => SourceKeep.Size(OutputRoot, gameId);

        public void DeleteKept(string gameId) => SourceKeep.Delete(OutputRoot, gameId);

        // the recipes of a game the kept files are meant to cover: the ones the app offers (and an edition can make)
        List<ImportRecipe> KeptRecipes(string gameId, ImportEdition? edition) => Recipes.ForGame(gameId)
            .Where(r => (KeepFilter?.Invoke(r) ?? true) && (edition == null || edition.Items.Contains(r.Id))).ToList();

        static string Stamp(ImportRecipe r) => r.Sha1 + ":" + ImporterVersions.For(r.Kind);

        // after an import: keep the files the importers read, for every offered item and not only the ones this run made.
        // Items this run didn't make are read in a record-only run (output discarded), unless the files they declare
        // (IImportKind.Sources) fit the whole-file budget, in which case those are kept whole without running anything.
        void KeepSourceFiles(GameDef def, RecordingSource rec, ImportContext ctx, ImportReport report, Dictionary<string, string> sourceHashes,
            ImportEdition? edition, IProgress<(string item, float fraction, string message)>? progress, CancellationToken ct)
        {
            try
            {
                IFileSource inner = rec.Inner;
                string editionId = edition?.Id ?? "";
                Dictionary<string, string> covered = SourceKeep.ReadCovered(OutputRoot, def.Id, editionId);
                var attempted = new HashSet<string>(report.Items.Select(i => i.Id), StringComparer.Ordinal);
                var newlyCovered = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (ImportItemInfo it in report.Items.Where(i => i.Status == ItemImportStatus.Complete && i.Message == "done"))
                {
                    ImportRecipe? r = Recipes.All.FirstOrDefault(x => x.Id == it.Id);
                    if (r != null) newlyCovered[r.Id] = Stamp(r);
                }
                // the items still to cover, with their kinds, once their source files check out
                var todo = new List<(ImportRecipe R, IImportKind Kind)>();
                foreach (ImportRecipe r in KeptRecipes(def.Id, edition))
                {
                    if (attempted.Contains(r.Id) || (covered.TryGetValue(r.Id, out string? s) && s == Stamp(r))) continue;
                    if (!_kinds.TryGetValue(r.Kind, out IImportKind? kind)) continue;
                    if (CheckSources(ctx, inner, def, edition, r, kind, sourceHashes) != null) continue;   // not this disc's item
                    todo.Add((r, kind));
                }
                var reads = new Dictionary<string, RangeSet?>(StringComparer.OrdinalIgnoreCase);
                foreach (KeyValuePair<string, RangeSet> kv in rec.Reads) reads[kv.Key] = kv.Value;
                // the declared files, when every item to cover declares some and they fit the budget
                bool shortcut = todo.All(t => t.Kind.Sources(t.R).Any());
                if (shortcut)
                {
                    var touched = new HashSet<string>(reads.Keys, StringComparer.OrdinalIgnoreCase);
                    foreach (var t in todo) foreach ((string p, _) in t.Kind.Sources(t.R)) touched.Add(rec.Canon(p));
                    shortcut = TouchedBytes(inner, touched) <= KeepWholeLimit;
                    if (shortcut)
                    {
                        foreach (string p in touched) reads[p] = null;
                        foreach (var t in todo) newlyCovered[t.R.Id] = Stamp(t.R);
                    }
                }
                if (!shortcut && todo.Count > 0)
                {
                    string tmp = Path.Combine(OutputRoot, ".keeping");
                    try
                    {
                        for (int i = 0; i < todo.Count; i++)
                        {
                            ct.ThrowIfCancellationRequested();
                            (ImportRecipe r, IImportKind kind) = todo[i];
                            int index = i;
                            void Note(float f, string msg) => progress?.Report((r.Id, 1f, $"copying the import files ({index + 1}/{todo.Count}): {r.Name}"));
                            Note(0, "");
                            if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
                            Directory.CreateDirectory(tmp);
                            try
                            {
                                kind.Run(ctx, r, tmp, (_, _) => { }, ct);
                                newlyCovered[r.Id] = Stamp(r);
                            }
                            catch (OperationCanceledException) { throw; }
                            catch (Exception) { /* an item that can't be made leaves its files out; it isn't covered */ }
                        }
                    }
                    finally
                    {
                        if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
                    }
                    reads.Clear();
                    foreach (KeyValuePair<string, RangeSet> kv in rec.Reads) reads[kv.Key] = kv.Value;
                }
                // the family: the reads, the resources they link to, single-file items whole, the files the kinds declare
                var alsoKeep = new List<string>();
                foreach (ImportRecipe r in KeptRecipes(def.Id, edition))
                    if (_kinds.TryGetValue(r.Kind, out IImportKind? kind)) alsoKeep.AddRange(kind.AlsoKeep(r).Select(rec.Canon));
                report.KeepFamily = SourceFamily.Expand(inner, reads, alsoKeep, ct);
                // whole files when they fit, else the family's ranges
                bool whole = def.Id != "mp4" && TouchedBytes(inner, reads.Keys) <= KeepWholeLimit;   // Prime 4: only the reads (owner 10-29)
                if (whole) foreach (string p in reads.Keys.ToList()) reads[p] = null;
                progress?.Report((report.Items.Count > 0 ? report.Items[^1].Id : "", 1f, "copying the import files"));
                var hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (KeyValuePair<string, string> kv in sourceHashes) hashes[rec.Canon(kv.Key)] = kv.Value;
                SourceKeep.Store(OutputRoot, def.Id, def.Title, rec.GameId, editionId, inner, reads, hashes, newlyCovered, whole, ct);
                report.KeptBytes = SourceKeep.Size(OutputRoot, def.Id);
                report.KeepError = "";
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                report.KeepError = e.Message;   // out of space: the import itself stands, the disc just stays needed
            }
        }

        static long TouchedBytes(IFileSource source, IEnumerable<string> paths)
        {
            long total = 0;
            foreach (string p in paths) if (source.Contains(p)) total += source.Length(p);
            return total;
        }

        // rebuild items from the kept files, no disc: an update that changed an importer. ids as Import's `only`; null = every
        // offered item. An item whose importer wants something the kept files lack reads "needs the disc again".
        public ImportReport RebuildFromKept(string gameId, IProgress<(string item, float fraction, string message)>? progress = null,
            CancellationToken ct = default, IReadOnlyCollection<string>? only = null)
        {
            GameDef def = Def(gameId);
            KeptSource kept = SourceKeep.Open(OutputRoot, gameId) ?? throw new FileNotFoundException($"no kept files for {def.Title}");
            ImportEdition? edition = kept.EditionId.Length > 0 ? Editions.FirstOrDefault(e => e.Id == kept.EditionId) : null;
            if (kept.EditionId.Length > 0 && edition == null) throw new InvalidDataException($"the kept files are from an edition ({kept.EditionId}) this version doesn't know");
            return ImportFrom(def, kept, progress, ct, only, Stopwatch.StartNew(), edition);
        }

        // ---- the copies' check (owner 2026-10-30: "why cant it only light up when there's truly something to add to the copies"):
        //      an offered item whose recipe or importer changed since the kept files last covered it is made once from them into
        //      a scratch folder, the output thrown away. Reading all it needs makes it covered; wanting a byte they don't hold
        //      marks it as lacking (KeptLacks: the game's COPY FILES button turns UPDATE COPIES and asks for the disc) ----

        const string LacksMark = "|lacks", FailedMark = "|failed";

        KeptSource? OpenKept(string gameId, out ImportEdition? edition)
        {
            edition = null;
            KeptSource? kept = SourceKeep.Open(OutputRoot, gameId);
            if (kept == null || kept.EditionId.Length == 0) return kept;
            edition = Editions.FirstOrDefault(e => e.Id == kept.EditionId);
            return edition != null ? kept : null;
        }

        // the items the check still has to try: not covered at today's stamp, and not tried at it before
        public List<ImportRecipe> KeptChecksDue(string gameId)
        {
            if (OpenKept(gameId, out ImportEdition? edition) is not { } kept) return new List<ImportRecipe>();
            Dictionary<string, string> covered = SourceKeep.ReadCovered(OutputRoot, gameId, kept.EditionId);
            Dictionary<string, string> tried = SourceKeep.ReadChecked(OutputRoot, gameId);
            return KeptRecipes(gameId, edition).Where(r =>
            {
                string stamp = Stamp(r);
                return _kinds.ContainsKey(r.Kind) && !(covered.TryGetValue(r.Id, out string? c) && c == stamp)
                    && !(tried.TryGetValue(r.Id, out string? t) && (t == stamp + LacksMark || t == stamp + FailedMark));
            }).ToList();
        }

        // runs the check for one game (a worker thread: it touches no import state, only the kept files' manifest and a
        // scratch folder); what it learnt is written as it goes, so a stop loses only the item it was on. Returns KeptLacks.
        public IReadOnlyList<string> CheckKept(string gameId, CancellationToken ct = default)
        {
            List<ImportRecipe> due = KeptChecksDue(gameId);
            if (due.Count == 0) return KeptLacks(gameId);
            KeptSource? kept = OpenKept(gameId, out ImportEdition? edition);
            if (kept == null) return Array.Empty<string>();
            GameDef def = Def(gameId);
            var ctx = new ImportContext(kept);
            var hashes = new Dictionary<string, string>();
            string scratch = "";
            try
            {
                foreach (ImportRecipe r in due)
                {
                    ct.ThrowIfCancellationRequested();
                    scratch = Path.Combine(OutputRoot, r.Kind, "." + r.Name + ".checking");   // beside an import's staging
                    string stamp = Stamp(r);
                    IImportKind kind = _kinds[r.Kind];
                    var covered = new Dictionary<string, string>();
                    var tried = new Dictionary<string, string>();
                    string? why = CheckSources(ctx, kept, def, edition, r, kind, hashes);
                    if (why != null)
                    {
                        tried[r.Id] = stamp + (why.StartsWith(NeedsDiscPrefix, StringComparison.Ordinal) ? LacksMark : FailedMark);
                    }
                    else
                    {
                        if (Directory.Exists(scratch)) Directory.Delete(scratch, true);
                        Directory.CreateDirectory(scratch);
                        try
                        {
                            kind.Run(ctx, r, scratch, (_, _) => { }, ct);
                            covered[r.Id] = stamp;
                        }
                        catch (OperationCanceledException) { throw; }
                        catch (SourceMissingException) { tried[r.Id] = stamp + LacksMark; }
                        catch (Exception) { tried[r.Id] = stamp + FailedMark; }   // not the copies' doing: an import says why
                        if (Directory.Exists(scratch)) Directory.Delete(scratch, true);
                    }
                    try { SourceKeep.MarkChecked(OutputRoot, gameId, covered, tried); }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException) { }
                }
            }
            finally
            {
                try { if (scratch.Length > 0 && Directory.Exists(scratch)) Directory.Delete(scratch, true); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            }
            return KeptLacks(gameId);
        }

        // the offered items the check found the kept files lack bytes for, at today's stamps (a top-up from the disc rewrites
        // the manifest and clears them)
        public IReadOnlyList<string> KeptLacks(string gameId)
        {
            if (!HasKept(gameId)) return Array.Empty<string>();
            Dictionary<string, string> tried = SourceKeep.ReadChecked(OutputRoot, gameId);
            if (tried.Count == 0) return Array.Empty<string>();
            return Recipes.ForGame(gameId).Where(r => (KeepFilter?.Invoke(r) ?? true)
                && tried.TryGetValue(r.Id, out string? t) && t == Stamp(r) + LacksMark).Select(r => r.Id).ToList();
        }

        // what a row removed by a stopped import records (Remove's message for the games a stopped Trilogy run finished)
        public const string StoppedMessage = "removed: the import was stopped";

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

        // move the staged files into the item's folder (replacing the files of the same name; nothing else is touched), and
        // add their names to the item's list of what its imports made (what Remove deletes)
        void Commit(ImportRecipe r, string staging, string final)
        {
            Directory.CreateDirectory(final);
            var made = new SortedSet<string>(ReadMade(r), StringComparer.Ordinal);
            foreach (string f in Directory.GetFiles(staging))
            {
                File.Move(f, Path.Combine(final, Path.GetFileName(f)), overwrite: true);
                made.Add(Path.GetFileName(f));
            }
            // a subfolder (a body's fx/) replaces the one of the same name whole, and is listed by its name
            foreach (string d in Directory.GetDirectories(staging))
            {
                string to = Path.Combine(final, Path.GetFileName(d));
                if (Directory.Exists(to)) Directory.Delete(to, true);
                Directory.Move(d, to);
                made.Add(Path.GetFileName(d));
            }
            Directory.Delete(staging, false);
            Directory.CreateDirectory(Path.GetDirectoryName(MadePath(r))!);
            File.WriteAllLines(MadePath(r), made);
        }

        // <root>/import/files/<kind>/<Name>.txt: the files an item's imports wrote into its folder, one name a line (kept
        // out of the item folder, which holds the item's files only; hand-pushed files beside them aren't listed)
        string MadePath(ImportRecipe r) => Path.Combine(OutputRoot, "import", "files", r.Kind, r.Name + ".txt");

        IEnumerable<string> ReadMade(ImportRecipe r) => File.Exists(MadePath(r))
            ? File.ReadAllLines(MadePath(r)).Select(Path.GetFileName).OfType<string>().Where(n => n.Length > 0) : Enumerable.Empty<string>();

        // delete what the item's imports wrote; its folder too once nothing else is in it. An item imported before the lists
        // (2026-10-07) loses every file but hand-pushed trophy rig variants (TrophyRigIO's straight/clean/tpose/hinge .mphrig,
        // the dev rig files): an import writes only the .rigid and .retro ones
        void DeleteMade(ImportRecipe r)
        {
            string dir = ItemDir(r);
            if (Directory.Exists(dir))
            {
                IEnumerable<string> files = File.Exists(MadePath(r))
                    ? ReadMade(r).Select(n => Path.Combine(dir, n)).ToList()
                    : Directory.GetFiles(dir).Where(f => !f.EndsWith(".mphrig", StringComparison.OrdinalIgnoreCase)
                        || f.EndsWith(".rigid.mphrig", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".retro.mphrig", StringComparison.OrdinalIgnoreCase)).ToList();
                foreach (string f in files)
                {
                    if (File.Exists(f)) File.Delete(f);
                    else if (Directory.Exists(f)) Directory.Delete(f, true);
                }
                if (!Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir);
            }
            if (File.Exists(MadePath(r))) File.Delete(MadePath(r));
        }

        // clear items (IMPORT GAMES' clear rows, owner 2026-10-07; a stopped import): the files their imports wrote are
        // deleted and each one reads "not imported" again. ids: "arenas/M02_Spires"; unknown ids are skipped
        public const string ClearedMessage = "cleared";   // a deleted item's (Remove's default)

        public void Remove(string gameId, IReadOnlyCollection<string> ids, string message = ClearedMessage)
        {
            var cleared = new List<string>();
            foreach (ImportRecipe r in Recipes.ForGame(gameId).Where(r => ids.Contains(r.Id)))
            {
                DeleteMade(r);
                cleared.Add(r.Id);
            }
            if (cleared.Count == 0 || !File.Exists(StatePath(gameId))) return;
            System.Text.Json.Nodes.JsonNode? root;
            try
            {
                root = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(StatePath(gameId), Encoding.UTF8));
            }
            catch (JsonException)
            {
                return;   // a damaged state file already reads as "not imported"
            }
            if (root?["items"] is not System.Text.Json.Nodes.JsonObject items) return;
            foreach (string id in cleared)
            {
                items[id] = new System.Text.Json.Nodes.JsonObject { ["status"] = "missing", ["recipeSha1"] = "", ["message"] = message };
            }
            File.WriteAllText(StatePath(gameId), root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }

        void WriteState(ImportReport report, Dictionary<string, string> sourceHashes)
        {
            Dictionary<string, (string Status, string RecipeSha1, string Message, int Importer)> old = ReadState(report.GameId, out _, out _, out _);
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
                    // a cancelled item kept its old folder (only the staging copy is deleted): it keeps what the last import
                    // recorded too, below, instead of reading as missing
                    if (it.Status == ItemImportStatus.Missing && it.Message == "cancelled" && old.ContainsKey(it.Id)) continue;
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
                    // a kept item (an edition's "kept: made from ..." row) keeps the number it was made by
                    w.WriteNumber("importer", it.Message.StartsWith("kept: ", StringComparison.Ordinal) && old.TryGetValue(it.Id, out var kept)
                        ? kept.Importer : ImporterVersions.For(it.Kind));
                    w.WriteEndObject();
                    written.Add(it.Id);
                }
                // items this import did not touch keep what the last one recorded
                foreach (KeyValuePair<string, (string Status, string RecipeSha1, string Message, int Importer)> kv in old)
                {
                    if (written.Contains(kv.Key)) continue;
                    w.WriteStartObject(kv.Key);
                    w.WriteString("status", kv.Value.Status);
                    w.WriteString("recipeSha1", kv.Value.RecipeSha1);
                    w.WriteString("message", kv.Value.Message);
                    w.WriteNumber("importer", kv.Value.Importer);
                    w.WriteEndObject();
                }
                w.WriteEndObject();
                // every item the app has for this game now: one added later reads as new (ImportItemInfo.New)
                w.WriteStartArray("recipes");
                foreach (ImportRecipe r in Recipes.ForGame(report.GameId)) w.WriteStringValue(r.Id);
                w.WriteEndArray();
                // the ones this build offered the player (KeepFilter): an item a later build ungates reads as new too
                w.WriteStartArray("offered");
                foreach (ImportRecipe r in Recipes.ForGame(report.GameId)) if (KeepFilter?.Invoke(r) ?? true) w.WriteStringValue(r.Id);
                w.WriteEndArray();
                w.WriteEndObject();
            }
            File.WriteAllBytes(path, ms.ToArray());
        }

        // the items Beta 3 added to games that had imports before it; a state file without "recipes" (Beta 3.3 and older)
        // reads only these as new
        static readonly HashSet<string> AddedBeforeKnownList = new(StringComparer.Ordinal) { "hd/MP1BetaPhazonSuit", "hd/MP1BetaPhazonSuitBall" };

        Dictionary<string, (string Status, string RecipeSha1, string Message, int Importer)> ReadState(string gameId, out string? discId, out bool provided, out HashSet<string>? known)
        {
            var d = new Dictionary<string, (string, string, string, int)>();
            discId = null; provided = false; known = null;
            string path = StatePath(gameId);
            if (!File.Exists(path)) return d;
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path, Encoding.UTF8));
                JsonElement root = doc.RootElement;
                provided = true;
                discId = root.TryGetProperty("discId", out JsonElement di) ? di.GetString() : null;
                if (root.TryGetProperty("offered", out JsonElement os) && os.ValueKind == JsonValueKind.Array)
                {
                    known = new HashSet<string>(os.EnumerateArray().Select(e => e.GetString() ?? ""), StringComparer.Ordinal);
                }
                else if (root.TryGetProperty("recipes", out JsonElement rs) && rs.ValueKind == JsonValueKind.Array)
                {
                    // Beta 3.4 to 4.0 listed every recipe, the Prime songs too, which public builds hid until 0.4.0 (owner
                    // 2026-10-30: they never showed up in the update); those read as not known so they come up as new
                    known = new HashSet<string>(rs.EnumerateArray().Select(e => e.GetString() ?? "")
                        .Where(id => !id.StartsWith("tracks/", StringComparison.Ordinal)), StringComparer.Ordinal);
                }
                if (root.TryGetProperty("items", out JsonElement items))
                {
                    foreach (JsonProperty p in items.EnumerateObject())
                    {
                        string S(string k) => p.Value.TryGetProperty(k, out JsonElement e) ? e.GetString() ?? "" : "";
                        // "importer": the kind's ImporterVersions number it was made by; missing = 1 (MR1 D4)
                        int importer = p.Value.TryGetProperty("importer", out JsonElement iv) && iv.ValueKind == JsonValueKind.Number ? iv.GetInt32() : 1;
                        d[p.Name] = (S("status"), S("recipeSha1"), S("message"), importer);
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
