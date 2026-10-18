using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MphRecomp.Import;
using MphRecomp.Import.Disc;
using MphRecomp.Import.Switch;

// IMPORT GAMES: the player picks their own Prime disc images, each game's page lists what can be made from it (ImportService's recipes: the arm cannons, Prime 1's
// suits and morph balls) with each one's status, and imports them -- all, or one again -- in the game's own message box
// with a progress line and a NO button to stop. The pages are RecompMenus' (cloned from the game's own pieces like
// RECOMP SETTINGS); this is their game code.
//   - The disc is read where it is (Android: the document picker's content:// stream; Windows: the file), never copied.
//     The pick is remembered (<root>/import/sources.json), so importing again doesn't ask for it again.
//   - Outputs land under <root> = the folder holding the settings file, as hd/<Name>/ and guns/<Name>/: where RECOMP
//     SETTINGS' samus suit row and the campaign read them. An import replaces its own items' files only (ImportService:
//     staging folder, then a move per file); nothing else in hd/ or guns/ is touched.
//   - It runs on a worker; Tick (the menu thread) shows its progress and its answer.
namespace MphRecomp.Frontend
{
    public enum ImportPickKind { Disc, WiiKey, Folder, SwitchKeys }

    // what the platform's file picker is asked for
    public sealed record ImportPickRequest(ImportPickKind Kind, string GameId, string Title, string[] Extensions);

    // a picked file: Token is what ImportHost.Open reads (Android: a content:// uri the app keeps read access to; Windows
    // and the PC tools: a path), Name its file name for the screen; Folder when it's a picked folder (ImportHost.OpenFolder
    // reads it), not a file
    public sealed record ImportPicked(string Token, string Name, bool Folder = false);

    // the platform's side, set once at startup by the Android and Windows shells
    public static class ImportHost
    {
        // shows the platform's file picker, then calls `picked` once (from any thread) with the file, or null when the
        // player backed out. null = this host has no picker (the page says so)
        public static Action<ImportPickRequest, Action<ImportPicked?>>? Pick { get; set; }

        // opens a picked file for reading (seekable); null when it's gone (moved, deleted, access withdrawn)
        public static Func<string, Stream?> Open { get; set; } = OpenPath;

        // opens a picked folder (a game imported from its files, Prime 4's RomFS) as its file tree, rooted where `marker`
        // is (the folder, its romfs/, ...: FolderSource.Locate); null when it's gone. Unset = the token is a path
        public static Func<string, string, IFileSource?>? OpenFolder { get; set; }

        public static Stream? OpenPath(string path) => File.Exists(path)
            ? new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.RandomAccess)
            : null;
    }

    public sealed class ImportGames
    {
        // the games on the IMPORT GAMES page, in its order. Prime 4 has no readers yet (docs/CLEANROOM_QUEUE.md CR6): its
        // row answers with the WORK IN PROGRESS box. Discs: the discs a game's page takes (ImportService's own disc and
        // its editions, Import/Editions.cs: Trilogy's MP1/ and MP2/, the original Prime 3 disc).
        public sealed record GameRow(string Id, string Label, string Title, string Platform, bool Available, string Discs = "");

        // the Metroid Prime Trilogy row (owner 2026-10-07: "an option for a user to get everything ... just from the
        // trilogy rom"): one disc, then Prime 1, Echoes and Corruption imported one after the other
        public const string TrilogyId = "trilogy";
        public static readonly string[] TrilogyGames = { "mp1", "mp2", "mp3" };
        private static readonly string[] TrilogyExtensions = { ".rvz", ".wia", ".iso", ".wbfs" };

        public static readonly GameRow[] GameRows =
        {
            new("mp1", "metroid\nprime", "METROID PRIME", "gamecube or trilogy", true, "metroid prime or metroid prime trilogy"),
            new("mp2", "prime 2\nechoes", "PRIME 2: ECHOES", "gamecube or trilogy", true, "prime 2: echoes or metroid prime trilogy"),
            new("mp3", "prime 3\ncorruption", "PRIME 3: CORRUPTION", "trilogy or corruption", true, "metroid prime trilogy or prime 3: corruption"),
            new(TrilogyId, "prime\ntrilogy", "METROID PRIME TRILOGY", "wii", true, "metroid prime trilogy"),
            new("mp4", "prime 4\nbeyond", "PRIME 4: BEYOND", "switch", true, "prime 4: beyond's game file"),
        };

        private readonly string? _root;
        private ImportService? _service;
        private readonly ConcurrentQueue<Action> _posted = new();

        // the game's message box; null without the frontend2d art (then nothing can run: there's nowhere to show it)
        public MenuPopup? Popup { get; set; }
        public Action<string>? Log { get; set; }
        // an import finished (or the remembered disc changed): the pages and the suit list read things again
        public Action? Changed { get; set; }

        // root: the folder the outputs go to (the settings file's folder); null = nowhere to import to
        public ImportGames(string? root)
        {
            _root = root;
        }

        public bool CanImport => _root != null && Popup != null;

        public ImportService? Service
        {
            get
            {
                if (_service == null && _root != null)
                {
                    try
                    {
                        _service = new ImportService(_root);
                    }
                    catch (Exception ex)
                    {
                        Log?.Invoke("import: no recipes (" + ex.Message + ")");
                    }
                }
                return _service;
            }
        }

        public static GameRow Row(string gameId) => GameRows.First(g => g.Id == gameId);

        // IMPORT GAMES offers the suits, morph balls, characters and Echoes arenas, not the first-person arm cannons, in
        // every build (owner 2026-10-07 ~12:45: public builds; ~21:20, Reports R8: "exclude arm cannons in first person
        // from the importers for now because they need more work", dev builds too): their rows, counts and imports are
        // left out. The guns recipes and ImportService's "guns" kind stay (the PC's -importcheck guns still makes them).
        // A public build also leaves out what isn't in the next release (owner 2026-10-17): the
        // Prime songs (kind "tracks") and Zero Suit Samus's body and model; dev builds offer them.
        // The one exception: Beyond Sylux's own arm cannon (owner 2026-10-17: his Prime 4 Shock Coil, in the public build,
        // imported clean-room from the player's own game file) -- the gun his BEYOND SYLUX skin wears in first person.
        public static readonly string[] OfferedGuns = { "MP4Sylux" };

        public static bool Offered(ImportItemInfo item) => (item.Kind != "guns" || OfferedGuns.Contains(item.Name, StringComparer.OrdinalIgnoreCase))
            && !(MphRecomp.Config.RecompSettings.PublicBuild && (item.Kind == Songs || item.Name.Contains("ZeroSuit", StringComparison.OrdinalIgnoreCase)));

        // the game's status and its items in the page's order (each suit's body, its ball, its arm cannon); null for a
        // game ImportService doesn't know
        public ImportGameInfo? Game(string gameId)
        {
            ImportGameInfo? g = Service?.Games.FirstOrDefault(x => x.Id == gameId);
            if (g == null) return null;
            // the suits and characters first, then the arenas, then their music, then the songs (in the catalog's order)
            var sorted = Fold(g.Items.Where(Offered).ToList()).OrderBy(i => i.Kind switch { "arenas" => 1, "music" => 2, Songs => 3, _ => 0 })
                .ThenBy(i => IsSong(i) ? SongOrder(i.Name) : 0)
                .ThenBy(i => SuitOrder(GroupOf(i.Name))).ThenBy(i => GroupOf(i.Name), StringComparer.Ordinal)
                .ThenBy(i => i.Kind == "hd" ? (i.Name.EndsWith("Ball", StringComparison.Ordinal) ? 1 : 0) : 2).ToList();
            g.Items.Clear();
            g.Items.AddRange(sorted);
            return g;
        }

        // ---- cards that stand for more than one import: Beyond Sylux's suit (hd/, his pictures in the menus) and his own
        //      Prime 4 body (bodies/, how he moves in a match) are one SYLUX card. The card is the first id; the rest are
        //      imported, updated and deleted with it, and the card shows the least finished of them ----

        private static readonly Dictionary<string, string[]> Joined = new() { ["hd/MP4Sylux"] = new[] { "bodies/MP4Sylux" } };

        // an id and the ids that go with it
        public static IEnumerable<string> WithJoined(string id) => Joined.TryGetValue(id, out string[]? more) ? more.Prepend(id) : new[] { id };

        // each card's joined items taken out of the list and folded into its status
        private static List<ImportItemInfo> Fold(List<ImportItemInfo> items)
        {
            foreach (ImportItemInfo lead in items.Where(i => Joined.ContainsKey(i.Id)).ToList())
            {
                List<ImportItemInfo> parts = items.Where(i => Joined[lead.Id].Contains(i.Id)).ToList();
                if (parts.Count == 0) continue;
                List<ImportItemInfo> all = parts.Prepend(lead).ToList();
                ImportItemInfo worst = all.FirstOrDefault(i => i.Status == ItemImportStatus.Failed)
                    ?? all.FirstOrDefault(i => i.Status == ItemImportStatus.HashMismatch)
                    ?? all.FirstOrDefault(i => i.Status != ItemImportStatus.Complete) ?? lead;
                ItemImportStatus status = worst.Status;
                // half of it on disk (the other half missing or made by an older version) needs doing again: an update
                if (status == ItemImportStatus.Missing && all.Any(Imported)) status = ItemImportStatus.Stale;
                items.RemoveAll(parts.Contains);
                items[items.IndexOf(lead)] = new ImportItemInfo
                {
                    Id = lead.Id, Kind = lead.Kind, Name = lead.Name, Status = status, Message = worst.Message, Seconds = all.Sum(i => i.Seconds),
                };
            }
            return items;
        }

        // ---- the Prime songs (docs/PRIME_MUSIC.md; owner 2026-10-14: one MUSIC tile on the game's page opens their list) ----

        private const string Songs = MphRecomp.PrimeMusic.TrackCatalog.Kind;
        // the game page's MUSIC tile: not an import item, it stands for the game's songs
        public const string SongsTileKind = "songs";

        public static bool IsSong(ImportItemInfo item) => item.Kind == Songs;

        private static int SongOrder(string name)
        {
            IReadOnlyList<MphRecomp.PrimeMusic.Track> all = MphRecomp.PrimeMusic.TrackCatalog.All;
            for (int i = 0; i < all.Count; i++) if (all[i].Name == name) return i;
            return all.Count;
        }

        // the MUSIC tile for a game's songs: DONE once all are imported, an X once any is, "n/N" in its box
        public static ImportItemInfo SongsTile(IReadOnlyCollection<ImportItemInfo> songs)
        {
            int done = songs.Count(i => i.Status == ItemImportStatus.Complete), made = songs.Count(Imported);
            return new ImportItemInfo
            {
                Id = SongsTileKind, Kind = SongsTileKind, Name = "music", Message = $"{done}/{songs.Count}",
                Status = done == songs.Count ? ItemImportStatus.Complete : made > 0 ? ItemImportStatus.Stale : ItemImportStatus.Missing,
            };
        }

        // the song list's IMPORT ALL: every song of the game (the disc picker first if there's no disc yet)
        public void ImportSongs(string gameId)
        {
            if (Busy || !Ready()) return;
            List<ImportItemInfo> songs = Game(gameId)?.Items.Where(IsSong).ToList() ?? new();
            if (songs.Count == 0) return;
            ImportPicked? src = Source(gameId);
            if (src == null)
            {
                PickDisc(gameId, () => ImportSongs(gameId));
                return;
            }
            Popup!.Ask($"import all {songs.Count} songs from {Lower(src.Name)}?", yes =>
            {
                if (yes) Run(new[] { gameId }, src, songs.Select(i => i.Id).ToArray(), gameId);
            });
        }

        public string Extensions(string gameId) => String.Join(" ", AcceptedExtensions(gameId));

        private string[] AcceptedExtensions(string gameId) => gameId == TrilogyId ? TrilogyExtensions
            : Service?.AcceptedSources(gameId).Extensions ?? Array.Empty<string>();

        // a game imported from its files, not a disc (Prime 4: the RomFS folder its owner dumped from their own copy)
        public bool FolderOnly(string gameId) => gameId != TrilogyId && Service?.AcceptedSources(gameId) is { Extensions.Length: 0, Folder: true };

        // a folder of the game's files can be picked for it (Prime 4: a RomFS), and a Switch game file (then the
        // player's own prod.keys is needed)
        public bool TakesFolder(string gameId) => gameId != TrilogyId && Service?.AcceptedSources(gameId) is { Folder: true };

        public bool TakesSwitchFiles(string gameId) => AcceptedExtensions(gameId).Contains(".xci", StringComparer.OrdinalIgnoreCase);

        // a Wii disc can be picked for it (then a plain .iso / .wbfs needs the player's Wii key file)
        public bool TakesWiiDiscs(string gameId) => AcceptedExtensions(gameId).Contains(".wbfs", StringComparer.OrdinalIgnoreCase);

        // the Trilogy row: Prime 1, Echoes and Corruption together (done, total)
        public (int Done, int Total) TrilogyCount()
        {
            int done = 0, total = 0;
            foreach (string id in TrilogyGames)
            {
                ImportGameInfo? g = Game(id);
                if (g == null) continue;
                done += g.Items.Count(i => i.Status == ItemImportStatus.Complete);
                total += g.Items.Count;
            }
            return (done, total);
        }

        // ---- names on the page ----

        private static readonly string[] Suits =
        {
            "PowerSuit", "VariaSuit", "GravitySuit", "Phazonsuit", "BetaPhazonSuit", "FusionSuit", "Samus", "DarkSuit", "LightSuit", "DarkSamus",
            "PEDSuit",
        };

        // MP1PowerSuitBall -> PowerSuit; SamusBall -> Samus; Phazonsuit -> Phazonsuit
        private static string GroupOf(string name)
        {
            string s = System.Text.RegularExpressions.Regex.Replace(name, @"^MP\d", "");
            return s.EndsWith("Ball", StringComparison.Ordinal) ? s[..^4] : s;
        }

        private static int SuitOrder(string group)
        {
            int i = Array.IndexOf(Suits, group);
            return i < 0 ? Suits.Length : i;
        }

        private static string SuitName(string group) => group switch
        {
            "Phazonsuit" => "phazon suit",
            "PEDSuit" => "p.e.d. suit",
            "Samus" => "morph",
            _ => System.Text.RegularExpressions.Regex.Replace(group, "(?<=[a-z])(?=[A-Z])", " ").ToLowerInvariant(),
        };

        // a row's two lines: "power\nsuit" / "power suit\nmorph ball" / "power suit\narm cannon" / "dark pirate\ntrooper";
        // SamusBall = "morph\nball"
        public static string ItemLabel(ImportItemInfo item)
        {
            // Echoes' multiplayer arenas ("sidehopper\nstation") and their music (Core Import/Echoes, CR2)
            if (item.Kind == "music") return "arena\nmusic";
            if (item.Kind == SongsTileKind) return "music";
            if (IsSong(item))
            {
                // the song's title, its words split over two lines ("chozo / ruins", "phendrana / drifts")
                string title = (MphRecomp.PrimeMusic.TrackCatalog.Find(item.Name)?.Title ?? item.Name).ToLowerInvariant();
                string[] w = title.Split(' ');
                int half = (w.Length + 1) / 2;
                return w.Length < 2 ? title : String.Join(" ", w.Take(half)) + "\n" + String.Join(" ", w.Skip(half));
            }
            string group = item.Kind == "arenas" ? MphRecomp.Arenas.EchoesArena.Find(item.Name)?.InGameName.ToLowerInvariant() ?? item.Name : GroupOf(item.Name);
            string suit = item.Kind == "arenas" ? group : SuitName(group);
            if (item.Kind == "guns") return suit + "\narm cannon";
            if (group == "Samus") return "morph\nball";
            if (IsBall(item)) return suit + "\nmorph ball";
            string[] words = suit.Split(' ');
            int first = (words.Length + 1) / 2;
            return String.Join(" ", words.Take(first)) + (words.Length > 1 ? "\n" + String.Join(" ", words.Skip(first)) : "");
        }

        public static bool IsBall(ImportItemInfo item) => item.Kind == "hd" && item.Name.EndsWith("Ball", StringComparison.Ordinal);

        // an HD body on Samus's own rig (a suit for RECOMP SETTINGS' samus suit row), else another character
        public static bool IsSuit(ImportItemInfo item) => item.Kind == "hd" && !IsBall(item)
            && MphRecomp.Anim.TrophyRigs.TryHunterFor(item.Name, out MphRead.Hunter h) && h == MphRead.Hunter.Samus;

        // the same in one line, for the progress box and the answers
        public static string ItemName(ImportItemInfo item) => ItemLabel(item).Replace('\n', ' ');

        // a row refused for the group rule isn't an error: it names the disc it needs (owner 2026-10-07: "GC ONLY", not WRONG;
        // only the GameCube games refuse today -- Trilogy's Echoes arenas + music)
        public static string ItemValue(string gameId, ImportItemInfo item) => item.Kind == SongsTileKind ? item.Message : item.Status switch
        {
            ItemImportStatus.Complete => "DONE",
            ItemImportStatus.Stale => "UPDATE",
            ItemImportStatus.HashMismatch when OnlyFromOriginal(item) && gameId is "mp1" or "mp2" => "GC ONLY",
            ItemImportStatus.HashMismatch => "WRONG",
            ItemImportStatus.Failed => "FAILED",
            _ => "IMPORT",
        };

        public string ItemDescription(string gameId, ImportItemInfo item)
        {
            string what = item.Kind == "arenas" ? "an echoes multiplayer arena for your matches"
                : item.Kind == "music" ? "the echoes arenas' music"
                : IsSong(item) ? "a song for your matches and the main menu (recomp settings: match music, menu music)"
                : item.Kind == "guns" ? "the first-person arm cannon" : IsBall(item) ? "the morph ball"
                : IsSuit(item) ? "samus in this suit (third person, the campaign's samus suit row)" : "an hd character model";
            return item.Status switch
            {
                ItemImportStatus.Complete => $"{ItemName(item)}: {what}. imported. a: import it again.",
                ItemImportStatus.Stale => $"{ItemName(item)}: {what}. this version of the game makes it a newer way. a: import it again.",
                ItemImportStatus.HashMismatch when OnlyFromOriginal(item) => $"{ItemName(item)}: {OnlyFromOriginalText(gameId)}",
                ItemImportStatus.HashMismatch => $"{ItemName(item)}: your disc isn't the version this was made from ({ExpectedDisc(gameId, item)}). "
                    + "other versions aren't supported yet.",
                ItemImportStatus.Failed => $"{ItemName(item)} failed: {Lower(item.Message)}",
                _ => $"{ItemName(item)}: {what}. a: import just this one.",
            };
        }

        // the disc id a recipe was made from ("gm8e01"), as its recipe names it
        private string ExpectedDisc(string gameId, ImportItemInfo item)
        {
            ImportRecipe? r = Service?.Recipes.All.FirstOrDefault(x => x.Id == item.Id);
            return (r?.OptStr("discId") ?? (gameId == "mp1" ? "GM8E01" : "the usa disc")).ToLowerInvariant();
        }

        private static string Lower(string s) => s.ToLowerInvariant();

        // refused because the disc is another edition (Trilogy, the original Prime 3) that doesn't make this item's whole
        // group (owner 2026-10-07: a disc is taken for a group only when it makes all of it; Echoes' multiplayer arenas
        // and music need the GameCube disc) -- ImportService: "... isn't made from ...; import it from that disc"
        private static bool OnlyFromOriginal(ImportItemInfo item) => item.Message.Contains("isn't made from", StringComparison.Ordinal);

        private static string OnlyFromOriginalText(string gameId) =>
            $"it comes from your {OriginalDisc(gameId)} disc only. import it from that disc.";

        // the disc each game's recipes were made from
        private static string OriginalDisc(string gameId) => gameId switch
        {
            "mp1" => "gamecube metroid prime",
            "mp2" => "gamecube prime 2: echoes",
            "mp3" => "metroid prime trilogy",
            _ => Row(gameId).Title.ToLowerInvariant(),
        };

        // ---- the remembered picks: <root>/import/sources.json ({ "mp1": { "token": ..., "name": ... } }) and the
        //      player's Wii key, <root>/import/wii_common_key.bin (16 bytes, parsed from their own file), and their Switch
        //      keys, <root>/import/switch_prod.keys (a copy of their own prod.keys, in the app's own storage only) ----

        private string? SourcesPath => _root == null ? null : Path.Combine(_root, "import", "sources.json");
        private string? WiiKeyPath => _root == null ? null : Path.Combine(_root, "import", "wii_common_key.bin");
        private string? SwitchKeysPath => _root == null ? null : Path.Combine(_root, "import", "switch_prod.keys");

        private Dictionary<string, ImportPicked> ReadSources()
        {
            var d = new Dictionary<string, ImportPicked>(StringComparer.Ordinal);
            try
            {
                if (SourcesPath == null || !File.Exists(SourcesPath)) return d;
                using var doc = JsonDocument.Parse(File.ReadAllText(SourcesPath));
                foreach (JsonProperty p in doc.RootElement.EnumerateObject())
                {
                    string? token = p.Value.TryGetProperty("token", out JsonElement t) ? t.GetString() : null;
                    string? name = p.Value.TryGetProperty("name", out JsonElement n) ? n.GetString() : null;
                    bool folder = p.Value.TryGetProperty("folder", out JsonElement f) && f.ValueKind == JsonValueKind.True;
                    if (token != null) d[p.Name] = new ImportPicked(token, name ?? "", folder);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                Log?.Invoke("import: sources.json unreadable (" + ex.Message + ")");
            }
            return d;
        }

        private void WriteSource(string gameId, ImportPicked? picked)
        {
            if (SourcesPath == null) return;
            Dictionary<string, ImportPicked> d = ReadSources();
            if (picked == null) d.Remove(gameId);
            else d[gameId] = picked;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(SourcesPath)!);
                var ms = new MemoryStream();
                using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
                {
                    w.WriteStartObject();
                    foreach (KeyValuePair<string, ImportPicked> kv in d)
                    {
                        w.WriteStartObject(kv.Key);
                        w.WriteString("token", kv.Value.Token);
                        w.WriteString("name", kv.Value.Name);
                        if (kv.Value.Folder) w.WriteBoolean("folder", true);
                        w.WriteEndObject();
                    }
                    w.WriteEndObject();
                }
                File.WriteAllBytes(SourcesPath, ms.ToArray());
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log?.Invoke("import: sources.json not written (" + ex.Message + ")");
            }
        }

        // the disc picked for this game, if any
        public ImportPicked? Source(string gameId) => ReadSources().TryGetValue(gameId, out ImportPicked? p) ? p : null;

        public bool HasWiiKey => WiiKeyPath != null && File.Exists(WiiKeyPath);

        // the wii key file row matters only for a plain Wii .iso / .wbfs (owner 2026-10-07: greyed when a GameCube disc is
        // picked): false once the game's picked disc is known to need none (a GameCube disc, any .rvz / .wia); no disc yet,
        // or one that can't be read, = true. Each pick is read once (its first bytes)
        public bool WiiKeyNeeded(string gameId)
        {
            ImportPicked? src = Source(gameId);
            if (src == null) return true;
            if (!_keyNeeded.TryGetValue(src.Token, out bool need))
            {
                need = true;
                try
                {
                    using Stream? s = ImportHost.Open(src.Token);
                    if (s != null) need = DiscImage.Detect(s) is DiscImageFormat.WiiIso or DiscImageFormat.Wbfs or DiscImageFormat.Unknown;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
                {
                    Log?.Invoke("import: " + src.Name + " unreadable for the wii key check (" + ex.Message + ")");
                }
                _keyNeeded[src.Token] = need;
            }
            return need;
        }

        private readonly Dictionary<string, bool> _keyNeeded = new();

        public bool HasSwitchKeys => SwitchKeysPath != null && File.Exists(SwitchKeysPath);

        // the switch keys box matters for a Switch game file (.xci / .nsp: their contents are encrypted), not for a
        // dumped folder
        public bool SwitchKeysNeeded(string gameId) => TakesSwitchFiles(gameId) && Source(gameId)?.Folder != true;

        private SwitchKeys? LoadSwitchKeys()
        {
            try
            {
                return HasSwitchKeys ? SwitchKeys.FromFile(SwitchKeysPath!) : null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                Log?.Invoke("import: switch keys unreadable (" + ex.Message + ")");
                return null;
            }
        }

        private WiiCommonKeys? LoadWiiKey()
        {
            try
            {
                return HasWiiKey ? WiiCommonKeys.FromBytes(File.ReadAllBytes(WiiKeyPath!)) : null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                Log?.Invoke("import: wii key unreadable (" + ex.Message + ")");
                return null;
            }
        }

        // ---- the player's presses (menu thread) ----

        public bool Busy => _task != null || _picking;

        // the worker is still going (tools: wait for it); the progress box's text meanwhile
        public bool Running => _task != null && !_task.IsCompleted;
        public string ProgressLine => _task != null ? ProgressText().Replace('\n', ' ') : "";

        private bool _picking;

        // "your disc": the picker, then the pick is remembered
        public void PickDisc(string gameId, Action? then = null)
        {
            if (Busy || !Ready()) return;
            GameRow row = Row(gameId);
            if (ImportHost.Pick == null)
            {
                Popup!.Inform("this version of the game can't open a file picker.");
                return;
            }
            if (TakesFolder(gameId) && !FolderOnly(gameId))
            {
                // a game file or a folder of its files (Prime 4: the .xci / .nsp, or a RomFS its owner dumped)
                Popup!.Ask($"pick your {Lower(row.Title)} game file ({String.Join(" / ", AcceptedExtensions(gameId))})? no picks a folder you dumped from it instead.",
                    file => PickWith(gameId, row, file ? ImportPickKind.Disc : ImportPickKind.Folder, then));
                return;
            }
            PickWith(gameId, row, FolderOnly(gameId) ? ImportPickKind.Folder : ImportPickKind.Disc, then);
        }

        private void PickWith(string gameId, GameRow row, ImportPickKind kind, Action? then)
        {
            _picking = true;
            string[] ext = kind == ImportPickKind.Folder ? Array.Empty<string>() : AcceptedExtensions(gameId);
            Log?.Invoke($"import: picking a {(kind == ImportPickKind.Folder ? "folder" : "disc")} for {gameId}");
            ImportHost.Pick!(new ImportPickRequest(kind, gameId, row.Title, ext), got => _posted.Enqueue(() =>
            {
                _picking = false;
                if (got == null)
                {
                    Log?.Invoke("import: no disc picked");
                    EndUpdates($"no {Lower(row.Title)} disc picked: its items weren't updated.");
                    return;
                }
                ImportPicked picked = got with { Folder = kind == ImportPickKind.Folder };
                Log?.Invoke($"import: {gameId} disc = {picked.Name}");
                WriteSource(gameId, picked);
                // the Trilogy disc is each of its games' disc too (their pages import from it)
                if (gameId == TrilogyId) foreach (string id in TrilogyGames) WriteSource(id, picked);
                Changed?.Invoke();
                then?.Invoke();
            }));
        }

        // "wii key file": the player's own key, kept as its 16 bytes
        public void PickWiiKey()
        {
            if (Busy || !Ready()) return;
            if (ImportHost.Pick == null)
            {
                Popup!.Inform("this version of the game can't open a file picker.");
                return;
            }
            _picking = true;
            ImportHost.Pick(new ImportPickRequest(ImportPickKind.WiiKey, "mp3", "WII KEY FILE", new[] { ".bin", ".txt" }), picked => _posted.Enqueue(() =>
            {
                _picking = false;
                if (picked == null) return;
                try
                {
                    using Stream? s = ImportHost.Open(picked.Token);
                    if (s == null) throw new FileNotFoundException(picked.Name);
                    if (s.Length > 0x10000) throw new InvalidDataException("too big for a key file");
                    var bytes = new byte[s.Length];
                    s.ReadExactly(bytes);
                    byte[] key = WiiCommonKeys.ParseKeyFile(bytes);
                    Directory.CreateDirectory(Path.GetDirectoryName(WiiKeyPath!)!);
                    File.WriteAllBytes(WiiKeyPath!, key);
                    Log?.Invoke($"import: wii key set from {picked.Name}");
                    Changed?.Invoke();
                    Popup!.Inform("wii key file set. plain .iso and .wbfs trilogy discs can be imported now.");
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
                {
                    Log?.Invoke($"import: wii key from {picked.Name} refused ({ex.Message})");
                    Popup!.Inform($"{Lower(picked.Name)} isn't a key file: {Lower(ex.Message)}.");
                }
            }));
        }

        // "switch keys": the player's own prod.keys (dumped from their own Switch), kept as a copy in the app's storage.
        // It's checked for the NCA header key; its values are never shown or logged
        public void PickSwitchKeys()
        {
            if (Busy || !Ready()) return;
            if (ImportHost.Pick == null)
            {
                Popup!.Inform("this version of the game can't open a file picker.");
                return;
            }
            _picking = true;
            ImportHost.Pick(new ImportPickRequest(ImportPickKind.SwitchKeys, "mp4", "SWITCH PROD.KEYS", new[] { ".keys", ".txt" }), picked => _posted.Enqueue(() =>
            {
                _picking = false;
                if (picked == null) return;
                try
                {
                    using Stream? s = ImportHost.Open(picked.Token);
                    if (s == null) throw new FileNotFoundException(picked.Name);
                    if (s.Length > 0x100000) throw new InvalidDataException("too big for a keys file");
                    var bytes = new byte[s.Length];
                    s.ReadExactly(bytes);
                    if (SwitchKeys.FromText(System.Text.Encoding.UTF8.GetString(bytes)).Get("header_key") is not { Length: 32 })
                    {
                        throw new InvalidDataException("it has no header_key line");
                    }
                    Directory.CreateDirectory(Path.GetDirectoryName(SwitchKeysPath!)!);
                    File.WriteAllBytes(SwitchKeysPath!, bytes);
                    Log?.Invoke($"import: switch keys set from {picked.Name}");
                    Changed?.Invoke();
                    Popup!.Inform("switch keys set. .xci and .nsp game files can be imported now.");
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
                {
                    Log?.Invoke($"import: switch keys from {picked.Name} refused ({ex.Message})");
                    Popup!.Inform($"{Lower(picked.Name)} isn't a prod.keys file: {Lower(ex.Message)}.");
                }
            }));
        }

        // the Trilogy row: the picker (it opens where the last disc was), then everything from that one disc
        public void ImportTrilogy()
        {
            if (Busy || !Ready()) return;
            PickDisc(TrilogyId, () =>
            {
                ImportPicked? src = Source(TrilogyId);
                (int _, int total) = TrilogyCount();
                if (src == null || total == 0) return;
                Popup!.Ask($"import everything from {Lower(src.Name)}? prime 1, echoes and corruption: {total} in all. the suits take a few minutes.", yes =>
                {
                    if (yes) Run(TrilogyGames, src, null, TrilogyId);
                });
            });
        }

        // "import all": every item of the game (the disc picker first if there's no disc yet)
        public void ImportAll(string gameId)
        {
            if (Busy || !Ready()) return;
            ImportGameInfo? g = Game(gameId);
            if (g == null || g.Items.Count == 0) return;
            ImportPicked? src = Source(gameId);
            if (src == null)
            {
                PickDisc(gameId, () => ImportAll(gameId));
                return;
            }
            string time = g.Items.Any(i => i.Kind == "hd") ? " the suits take a few minutes." : "";
            Popup!.Ask($"import all {g.Items.Count} from {Lower(src.Name)}?{time}", yes =>
            {
                if (yes) Run(new[] { gameId }, src, null, gameId);
            });
        }

        // one item's row: import it (again)
        public void ImportOne(string gameId, ImportItemInfo item)
        {
            if (Busy || !Ready()) return;
            ImportPicked? src = Source(gameId);
            if (src == null)
            {
                PickDisc(gameId, () =>
                {
                    ImportItemInfo? again = Game(gameId)?.Items.FirstOrDefault(i => i.Id == item.Id);
                    if (again != null) ImportOne(gameId, again);
                });
                return;
            }
            string question = item.Status == ItemImportStatus.Complete
                ? $"import {ItemName(item)} again from {Lower(src.Name)}?"
                : $"import {ItemName(item)} from {Lower(src.Name)}?";
            Popup!.Ask(question, yes =>
            {
                if (yes) Run(new[] { gameId }, src, new[] { item.Id }, gameId);
            });
        }

        // imported (files on disk) or answered by an import (WRONG, FAILED): a delete takes it back to IMPORT
        public static bool Clearable(ImportItemInfo item) => item.Status != ItemImportStatus.Missing;

        public static bool Imported(ImportItemInfo item) => item.Status is ItemImportStatus.Complete or ItemImportStatus.Stale;

        // the games DELETE ALL on IMPORT GAMES reaches: every game ImportService knows except the Trilogy row (its items are
        // Prime 1's, Echoes' and Corruption's)
        private IEnumerable<string> DeletableGames => GameRows.Where(r => r.Available && r.Id != TrilogyId).Select(r => r.Id);

        // something to delete: in one game, or (null) in any
        public bool AnyImported(string? gameId = null)
            => (gameId != null ? new[] { gameId } : DeletableGames).Any(id => Game(id)?.Items.Any(Imported) == true);

        // an entry's X, or a game page's DELETE ALL for every entry (owner 2026-10-07): the ask box, then what those items'
        // imports wrote is deleted (ImportService.Remove) and their rows read IMPORT again
        public void Clear(string gameId, IReadOnlyList<ImportItemInfo> items, string what)
            => Clear(items.Select(i => (gameId, i)).ToList(), what, "your disc");

        // IMPORT GAMES' DELETE ALL: everything from every game
        public void ClearEverything()
            => Clear(DeletableGames.SelectMany(id => (Game(id)?.Items ?? new()).Select(i => (id, i))).ToList(), "everything you imported", "your discs");

        private void Clear(List<(string Game, ImportItemInfo Item)> items, string what, string source)
        {
            if (Busy || !Ready()) return;
            List<(string Game, ImportItemInfo Item)> clear = items.Where(x => Clearable(x.Item)).ToList();
            if (clear.Count == 0) return;
            int made = clear.Count(x => Imported(x.Item));
            string question = made == 0 ? $"delete {what}? none of them is imported; their rows go back to import."
                : clear.Count == 1 ? $"delete {what}? {source} can import it again."
                : $"delete {what}? {(made == 1 ? "the one" : $"the {made}")} imported {(made == 1 ? "is" : "are")} deleted. {source} can import {(made == 1 ? "it" : "them")} again.";
            Popup!.Ask(question, yes =>
            {
                if (!yes) return;
                try
                {
                    foreach (IGrouping<string, (string Game, ImportItemInfo Item)> game in clear.GroupBy(x => x.Game))
                    {
                        Service!.Remove(game.Key, game.SelectMany(x => WithJoined(x.Item.Id)).ToList());
                    }
                    Log?.Invoke($"import: deleted {String.Join(", ", clear.Select(x => x.Item.Id))}");
                    Changed?.Invoke();
                    Popup?.Inform($"deleted {what}.");
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Log?.Invoke("import: delete failed: " + ex);
                    Changed?.Invoke();
                    Popup?.Inform($"{what} couldn't all be deleted: {Lower(ex.Message)}");
                }
            });
        }

        // ---- UPDATE ALL (owner 2026-10-17: "an update all button by import all and a notice when you go to the screen"): the
        //      items an update made stale (the red badge), imported again from the discs already picked, a disc at a time ----

        public static bool NeedsUpdate(ImportItemInfo item) => item.Status == ItemImportStatus.Stale;

        // the games an UPDATE ALL reaches: a game page's own (the Trilogy page: its three), or (null) every game
        private IEnumerable<string> UpdateGames(string? gameId)
            => gameId == null ? DeletableGames : gameId == TrilogyId ? TrilogyGames : new[] { gameId };

        public int UpdateCount(string? gameId = null) => UpdateGames(gameId).Sum(id => Game(id)?.Items.Count(NeedsUpdate) ?? 0);

        private List<string> _updates = new();             // the games UPDATE ALL still has to reach
        private readonly List<string> _updateAnswers = new(); // each disc's summary
        private string? _updateScope;
        private int _updateTotal;
        private bool _updating;

        // a game page's UPDATE ALL, or (null, notice) the box on coming to IMPORT GAMES: how many, the check or the X, then
        // each disc's share in turn (a game with no disc picked yet asks for it first). The notice says nothing when
        // nothing needs an update
        public void UpdateAll(string? gameId, bool notice = false)
        {
            if (Busy || !Ready()) return;
            int n = UpdateCount(gameId);
            if (n == 0)
            {
                if (!notice) Popup!.Inform("nothing needs an update: everything imported is up to date.");
                return;
            }
            bool suits = UpdateGames(gameId).Any(id => Game(id)?.Items.Any(i => NeedsUpdate(i) && i.Kind == "hd") == true);
            string question = n == 1 ? "1 item needs an update. update it now?" : $"{n} items need updates. update them all now?";
            Popup!.Ask(question + (suits ? " the suits take a few minutes." : ""), yes =>
            {
                if (!yes) return;
                _updating = true;
                _updateScope = gameId;
                _updateTotal = n;
                _updates = UpdateGames(gameId).ToList();
                _updateAnswers.Clear();
                Log?.Invoke($"import: update all ({n}) in {String.Join("+", _updates)}");
                NextUpdate();
            });
        }

        // the next disc: every game left that reads from it, their stale items only (a Trilogy disc picked on its card is
        // Prime 1's, Echoes' and Corruption's disc: one run)
        private void NextUpdate()
        {
            _updates.RemoveAll(id => Game(id)?.Items.Any(NeedsUpdate) != true);
            if (_updates.Count == 0)
            {
                EndUpdates(null);
                return;
            }
            string first = _updates[0];
            ImportPicked? src = Source(first);
            if (src == null)
            {
                PickDisc(first, NextUpdate);
                return;
            }
            string[] games = _updates.Where(id => Source(id)?.Token == src.Token).ToArray();
            _updates.RemoveAll(games.Contains);
            string[] only = games.SelectMany(id => Game(id)?.Items.Where(NeedsUpdate).Select(i => i.Id) ?? Enumerable.Empty<string>()).Distinct().ToArray();
            Run(games, src, only, first);
            if (_task == null) EndUpdates("");   // Run answered by itself (its box says why)
        }

        // the run's end: everything updated, or each disc's answer (`last`: the run that stopped it, "" = its box is up)
        private void EndUpdates(string? last)
        {
            if (!_updating) return;
            _updating = false;
            _updates.Clear();
            if (last == "") return;
            int left;
            try
            {
                left = UpdateCount(_updateScope);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                left = -1;
            }
            List<string> answers = _updateAnswers.ToList();
            if (last != null) answers.Add(last);
            _updateAnswers.Clear();
            string answer = left == 0 && last == null ? (_updateTotal == 1 ? "updated." : $"all {_updateTotal} updated.")
                : answers.Count > 0 ? String.Join(" ", answers) : "nothing was updated.";
            Log?.Invoke("import: update all: " + answer);
            Changed?.Invoke();
            Popup?.Inform(answer);
        }

        private bool Ready()
        {
            if (Popup == null) return false;
            if (_root == null || Service == null)
            {
                Popup.Inform("there's nowhere to import to: this version of the game has no data folder.");
                return false;
            }
            return true;
        }

        // ---- running (a worker; Tick answers) ----

        private Task<List<ImportReport>>? _task;
        private CancellationTokenSource? _cts;
        private string _runKey = "", _runName = "";   // the game (or TrilogyId) whose pick the run used
        private string[] _runGames = Array.Empty<string>();
        private int _runCount;
        private int[] _gameOffsets = Array.Empty<int>(), _gameCounts = Array.Empty<int>();
        private Dictionary<string, string> _names = new();
        private Dictionary<string, int> _order = new(); // "game:item" -> its place in the worker's order (ImportService's)
        private bool _cancelled;
        private volatile string _item = "";
        private volatile float _fraction;
        private volatile int _gameIndex;
        private int _ticks;

        private sealed class Sink : IProgress<(string item, float fraction, string message)>
        {
            private readonly ImportGames _owner;
            public Sink(ImportGames owner) { _owner = owner; }

            public void Report((string item, float fraction, string message) p)
            {
                _owner._item = p.item;
                _owner._fraction = p.fraction;
            }
        }

        // games: one game, or the Trilogy's three from one disc; key: whose remembered pick this is (forgotten when the
        // disc turns out unreadable or wrong)
        private void Run(string[] games, ImportPicked src, string[]? only, string key)
        {
            ImportService service = Service!;
            var items = new List<(string Game, ImportItemInfo Item)>();
            foreach (string id in games)
            {
                ImportGameInfo? g = Game(id);
                if (g == null) return;
                items.AddRange(g.Items.Where(i => only == null || only.Contains(i.Id)).Select(i => (id, i)));
            }
            // "all" in a public build is all that's offered (Game leaves out the arm cannons): name them, or the service
            // would make every recipe
            bool all = only == null;
            if (all && items.Count < games.Sum(id => service.Recipes.ForGame(id).Count())) only = items.Select(i => i.Item.Id).ToArray();
            if (only != null) only = only.SelectMany(WithJoined).Distinct().ToArray();
            // the suits' rigs are fitted on MPH's own hunters (HdRigBuild: Samus; Sylux for Prime 4's): read them here, on the
            // menu thread, so the worker only takes MphRead's cached models and never fills its caches beside this thread
            if (items.Any(i => i.Item.Kind == "hd"))
            {
                try
                {
                    var hunters = new HashSet<MphRead.Hunter> { MphRead.Hunter.Samus };
                    foreach (var i in items.Where(i => i.Item.Kind == "hd"))
                    {
                        if (MphRecomp.Anim.TrophyRigs.TryHunterFor(i.Item.Name, out MphRead.Hunter h)) hunters.Add(h);
                    }
                    foreach (MphRead.Hunter h in hunters) _ = MphRead.Read.GetModelInstance(MphRead.Metadata.HunterModels[h][0]);
                }
                catch (Exception ex)
                {
                    Log?.Invoke("import: mph's samus model unreadable: " + ex);
                    Popup!.Inform("the suits are fitted on this game's own samus, and her model couldn't be read: " + Lower(ex.Message));
                    return;
                }
            }
            _runKey = key;
            _runName = src.Name;
            _runGames = games;
            _names = items.SelectMany(i => WithJoined(i.Item.Id).Select(id => (Key: i.Game + ":" + id, Name: ItemName(i.Item))))
                .ToDictionary(x => x.Key, x => x.Name);
            _order = new Dictionary<string, int>();
            _gameOffsets = new int[games.Length];
            _gameCounts = new int[games.Length];
            int n = 0;
            for (int k = 0; k < games.Length; k++)
            {
                _gameOffsets[k] = n;
                foreach (string id in service.Recipes.ForGame(games[k]).Select(r => r.Id).Where(id => only == null || only.Contains(id)))
                {
                    _order[games[k] + ":" + id] = ++n;
                }
                _gameCounts[k] = n - _gameOffsets[k];
            }
            _runCount = n;
            _cancelled = false;
            _item = "";
            _fraction = 0;
            _gameIndex = 0;
            _ticks = 0;
            _cts = new CancellationTokenSource();
            CancellationToken ct = _cts.Token;
            WiiCommonKeys? keys = LoadWiiKey();
            SwitchKeys? switchKeys = LoadSwitchKeys();
            var sink = new Sink(this);
            bool trilogy = key == TrilogyId;
            Log?.Invoke($"import: {String.Join("+", games)} from {src.Name}, {(all ? "all " + items.Count : String.Join(", ", only!))}");
            Popup!.BusyCancel($"importing from {Lower(src.Name)}.", () =>
            {
                _cancelled = true;
                _cts?.Cancel();
                Popup?.SetText("stopping. nothing from this import is kept.");
                Log?.Invoke("import: stop pressed");
            });
            if (FolderOnly(games[0]) || src.Folder)
            {
                string marker = service.AcceptedSources(games[0]).Marker;
                _task = Task.Run(() =>
                {
                    // a folder: its tree read in place (a picked Android folder through its own source, else a path)
                    if (ImportHost.OpenFolder == null) return new List<ImportReport> { service.Import(games[0], src.Token, sink, ct, only) };
                    using IFileSource folder = ImportHost.OpenFolder(src.Token, marker) ?? throw new FileNotFoundException("can't open " + src.Name);
                    return new List<ImportReport> { service.Import(games[0], folder, sink, ct, only) };
                }, ct);
                return;
            }
            _task = Task.Run(() =>
            {
                using Stream s = ImportHost.Open(src.Token) ?? throw new FileNotFoundException("can't open " + src.Name);
                service.WiiKeys = keys;
                service.SwitchKeys = switchKeys;
                if (trilogy) CheckTrilogy(s, src.Name, keys);
                var reports = new List<ImportReport>();
                try
                {
                    for (int k = 0; k < games.Length; k++)
                    {
                        ct.ThrowIfCancellationRequested();
                        _gameIndex = k;
                        s.Position = 0;
                        reports.Add(service.Import(games[k], s, sink, ct, only));
                    }
                }
                catch (OperationCanceledException)
                {
                    // a stopped import keeps nothing of itself (owner 2026-10-07, Reports R10): ImportService took back the
                    // stopped game's items; the games a Trilogy run finished before it go too
                    foreach (ImportReport r in reports)
                    {
                        service.Remove(r.GameId, r.Items.Where(i => i.Status == ItemImportStatus.Complete && i.Message == "done").Select(i => i.Id).ToList(),
                            ImportService.StoppedMessage);
                    }
                    throw;
                }
                return reports;
            }, ct);
        }

        // the Trilogy row takes a Trilogy disc only (a game's own disc belongs on that game's page)
        private static void CheckTrilogy(Stream s, string name, WiiCommonKeys? keys)
        {
            using IFileSource disc = DiscImage.Open(s, keys, leaveOpen: true);
            if (!disc.GameId.StartsWith("R3M", StringComparison.Ordinal))
            {
                throw new InvalidDataException($"{name} is {disc.GameId} ({disc.Title}), not metroid prime trilogy");
            }
        }

        // every menu tick
        public void Tick()
        {
            while (_posted.TryDequeue(out Action? a)) a();
            if (_task == null || Popup == null) return;
            _ticks++;
            if (!_task.IsCompleted)
            {
                if (!_cancelled && _ticks % 6 == 0) Popup.SetText(ProgressText());
                return;
            }
            Task<List<ImportReport>> task = _task;
            _task = null;
            _cts?.Dispose();
            _cts = null;
            string answer;
            if (task.IsCompletedSuccessfully)
            {
                answer = Summary(task.Result);
            }
            else
            {
                Exception ex = task.Exception?.GetBaseException() ?? new OperationCanceledException();
                answer = Failure(ex);
            }
            Log?.Invoke("import: " + answer);
            if (_updating)
            {
                // UPDATE ALL: the next disc (its busy box takes this one's place), or the end
                if (task.IsCompletedSuccessfully)
                {
                    _updateAnswers.Add(answer);
                    Changed?.Invoke();
                    NextUpdate();
                }
                else EndUpdates(answer);
                return;
            }
            Changed?.Invoke();
            Popup.Inform(answer);
        }

        private string ProgressText()
        {
            int k = Math.Clamp(_gameIndex, 0, Math.Max(0, _runGames.Length - 1));
            string game = _runGames.Length > 0 ? _runGames[k] : "";
            string key = game + ":" + _item;
            // ImportService's fraction runs over one game's items; the box counts the whole run
            float overall = _runCount == 0 || _gameCounts.Length == 0 ? 0
                : (_gameOffsets[k] + Math.Clamp(_fraction, 0, 1) * _gameCounts[k]) / _runCount;
            int pct = (int)(Math.Clamp(overall, 0, 1) * 100);
            int index = _order.TryGetValue(key, out int i) ? i : _gameOffsets.Length > 0 ? _gameOffsets[k] + 1 : 1;
            string what = _names.TryGetValue(key, out string? name) ? name : "reading the disc";
            return $"importing from {Lower(_runName)}.\n{what}\n{index} of {_runCount}. {pct}%";
        }

        private string Summary(List<ImportReport> reports)
        {
            var items = Fold(reports.SelectMany(r => r.Items).ToList());
            int n = items.Count;
            int done = items.Count(i => i.Status == ItemImportStatus.Complete);
            int wrong = items.Count(i => i.Status == ItemImportStatus.HashMismatch);
            int original = items.Count(i => i.Status == ItemImportStatus.HashMismatch && OnlyFromOriginal(i));
            int failed = items.Count(i => i.Status == ItemImportStatus.Failed);
            if (n == 1)
            {
                ImportItemInfo it = items[0];
                return it.Status switch
                {
                    ItemImportStatus.Complete => $"{ItemName(it)} imported.",
                    ItemImportStatus.HashMismatch when OnlyFromOriginal(it) => $"{ItemName(it)} wasn't imported: {OnlyFromOriginalText(_runGames[0])}",
                    ItemImportStatus.HashMismatch => $"{ItemName(it)} wasn't imported: your disc isn't the version it was made from. other versions aren't supported yet.",
                    _ => $"{ItemName(it)} failed: {Lower(it.Message)}",
                };
            }
            if (wrong == n && original == 0)
            {
                return $"nothing imported: {Lower(_runName)} isn't the version these were made from ({reports[0].DiscId.ToLowerInvariant()}). other versions aren't supported yet.";
            }
            string s = done == n ? $"all {n} imported." : $"{done} of {n} imported.";
            if (original > 0) s += $" {original} come{(original == 1 ? "s" : "")} only from the original disc: see {(original == 1 ? "its row" : "their rows")}.";
            // a few left out are named (owner 2026-10-07, R13: "1 need another version" didn't say which), more are counted
            List<ImportItemInfo> other = items.Where(i => i.Status == ItemImportStatus.HashMismatch && !OnlyFromOriginal(i)).ToList();
            if (other.Count > 0) s += other.Count <= 3
                ? $" {Names(other)} need{(other.Count == 1 ? "s" : "")} another version of the disc: see {(other.Count == 1 ? "its row" : "their rows")}."
                : $" {other.Count} need another version of the disc.";
            List<ImportItemInfo> broken = items.Where(i => i.Status == ItemImportStatus.Failed).ToList();
            if (failed > 0) s += failed <= 3 ? $" {Names(broken)} failed: see {(failed == 1 ? "its row" : "their rows")}." : $" {failed} failed: see their rows.";
            return s;
        }

        // "a", "a and b", "a, b and c"
        private static string Names(List<ImportItemInfo> items)
        {
            var names = items.Select(ItemName).ToList();
            return names.Count == 1 ? names[0] : String.Join(", ", names.Take(names.Count - 1)) + " and " + names[^1];
        }

        private string Failure(Exception ex)
        {
            switch (ex)
            {
            case OperationCanceledException:
                return "import stopped. nothing from it was kept.";
            case WiiKeyRequiredException:
                return "this disc image is encrypted and needs your wii's key file: set it on the wii key file row. .rvz and .wia images need none.";
            case SwitchKeyRequiredException { Firmware: string fw }:
                return "your switch keys are older than this game"
                    + (fw.Length > 0 ? $": it needs keys from switch system version {fw} or newer" : "")
                    + ". update your switch, dump its prod.keys again and set them on the switch keys box.";
            case SwitchKeyRequiredException:
                return "this game file is encrypted and needs your switch's prod.keys, dumped from your own switch: set it on the switch keys box.";
            case FileNotFoundException or UnauthorizedAccessException or DirectoryNotFoundException:
                WriteSource(_runKey, null); // forget it: the next press asks for the disc again
                return $"{Lower(_runName)} can't be opened any more. pick your disc again.";
            case InvalidDataException:
                WriteSource(_runKey, null); // not this game's disc (or no disc image at all): ask for the disc next time
                return Lower(ex.Message) + ".";
            default:
                Log?.Invoke("import: " + ex);
                return "the import failed: " + Lower(ex.Message);
            }
        }
    }
}
