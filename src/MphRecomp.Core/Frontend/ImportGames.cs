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

// IMPORT GAMES: the player picks their own Prime ROMs, each game's page lists what can be made from it (ImportService's recipes: the arm cannons, Prime 1's
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
    public enum ImportPickKind { Disc, WiiKey, Folder, SwitchKeys, DsSave }

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
                        // the import files (owner 2026-10-23/24: all an item is made from, so a change rebuilds it with no disc)
                        // are kept for the games the player said yes for (Run sets KeepSources a game at a time); only what this
                        // page offers is covered
                        _service = new ImportService(_root)
                        {
                            KeepFilter = r => Offered(new ImportItemInfo { Id = r.Id, Kind = r.Kind, Name = r.Name })
                        };
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

        // A public build also leaves out what no player can use in it (owner 2026-10-26: "gate anything from the importer in
        // the public build that is not currently available to a player"; beta and dev builds keep them): the Space Pirate
        // models (only the dev skin override draws them), the Brawl Samus ball and the Brawl Paralyzer.
        public static readonly string[] PublicUnused = { "hd/MP1SpacePirate", "hd/MP2PirateTrooper", "hd/MP2DarkPirateTrooper",
            "hd/MP2PirateCommando", "hd/MP2DarkCommando", "hd/SamusBall" };

        public static bool Offered(ImportItemInfo item) => (item.Kind != "guns" || OfferedGuns.Contains(item.Name, StringComparer.OrdinalIgnoreCase))
            && !(MphRecomp.Config.RecompSettings.PublicBuild && (item.Kind == "brawl"
                || item.Name.Contains("ZeroSuit", StringComparison.OrdinalIgnoreCase) || PublicUnused.Contains(item.Id)));

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

        // ---- cards that stand for more than one import: Beyond Sylux's suit (hd/, his pictures in the menus), his own
        //      Prime 4 bodies (bodies/, how he and his Lockjaw move in a match) and his arm cannon are one SYLUX card
        //      (owner 2026-10-26: "merge all the sylux items into one"); every character with both an hd/ model and a
        //      bodies/ body (the troopers, Dark Samus...) is one card too (owner 2026-10-26: no duplicate tiles). The card
        //      is the first id; the rest are imported, updated and deleted with it, and the card shows the least finished ----

        private static readonly Dictionary<string, string[]> Joined = new() {
            ["hd/MP4Sylux"] = new[] { "bodies/MP4Sylux", "bodies/MP4SyluxLockjaw", "guns/MP4Sylux" },
            ["hd/MP1FusionSuit"] = new[] { "hd/MP1FusionSuitVaria", "hd/MP1FusionSuitGravity", "hd/MP1FusionSuitPhazon" } };

        // the joins as offered (Fold fills it from the page's items: only parts this build offers, hd/X + bodies/X found)
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string[]> OfferedJoins = new();

        // an id and the ids that go with it
        public static IEnumerable<string> WithJoined(string id) => OfferedJoins.TryGetValue(id, out string[]? more) || Joined.TryGetValue(id, out more)
            ? more.Prepend(id) : new[] { id };

        // a card's parts among these items: its listed ones, and for hd/X the bodies/X of the same name
        private static string[] PartsOf(ImportItemInfo lead, List<ImportItemInfo> items)
        {
            IEnumerable<string> want = Joined.TryGetValue(lead.Id, out string[]? listed) ? listed : Enumerable.Empty<string>();
            if (lead.Kind == "hd") want = want.Append("bodies/" + lead.Name);
            return want.Distinct().Where(id => items.Any(i => i.Id == id)).ToArray();
        }

        // each card's joined items taken out of the list and folded into its status
        private static List<ImportItemInfo> Fold(List<ImportItemInfo> items)
        {
            foreach (ImportItemInfo lead in items.Where(i => i.Kind == "hd").ToList())
            {
                if (!items.Contains(lead)) continue;
                string[] ids = PartsOf(lead, items);
                OfferedJoins[lead.Id] = ids;
                if (ids.Length == 0) continue;
                List<ImportItemInfo> parts = items.Where(i => ids.Contains(i.Id)).ToList();
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
            Confirm($"import all {songs.Count} songs from {Lower(src.Name)}?", new[] { gameId }, src, songs.Select(i => i.Id).ToArray(), gameId);
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
                (int d, int t) = TileCount(g);
                done += d;
                total += t;
            }
            return (done, total);
        }

        // a game's (done, total) as its page's tiles: the songs are one MUSIC tile, done once all are (owner 2026-10-26:
        // the counts on the main import screen match the tiles)
        public static (int Done, int Total) TileCount(ImportGameInfo g)
        {
            List<ImportItemInfo> songs = g.Items.Where(IsSong).ToList();
            List<ImportItemInfo> rest = g.Items.Where(i => !IsSong(i)).ToList();
            int done = rest.Count(i => i.Status == ItemImportStatus.Complete);
            if (songs.Count > 0 && songs.All(i => i.Status == ItemImportStatus.Complete)) done++;
            return (done, rest.Count + (songs.Count > 0 ? 1 : 0));
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
        // the GameCube games refuse Trilogy's Echoes arenas + music; Prime 3 refuses the standalone Corruption disc's characters: TRILOGY)
        public static string ItemValue(string gameId, ImportItemInfo item) => item.Kind == SongsTileKind ? item.Message : item.Status switch
        {
            ItemImportStatus.Complete => "DONE",
            ItemImportStatus.Stale => "UPDATE",
            ItemImportStatus.Missing when item.New => "NEW",
            ItemImportStatus.HashMismatch when OnlyFromOriginal(item) && gameId is "mp1" or "mp2" => "GC ONLY",
            ItemImportStatus.HashMismatch when OnlyFromOriginal(item) && gameId == "mp3" => "TRILOGY",
            ItemImportStatus.HashMismatch => "WRONG",
            ItemImportStatus.Failed when NeedsDisc(item) => "DISC",
            ItemImportStatus.Failed => "FAILED",
            _ => "IMPORT",
        };

        // a rebuild from the kept import files found something missing: this item needs the game's disc again (ImportService)
        public static bool NeedsDisc(ImportItemInfo item)
            => item.Status == ItemImportStatus.Failed && item.Message.StartsWith(ImportService.NeedsDiscPrefix, StringComparison.Ordinal);

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
                ItemImportStatus.Missing when item.New => $"{ItemName(item)}: {what}. new in this version of the game. a: import it.",
                ItemImportStatus.HashMismatch when OnlyFromOriginal(item) => $"{ItemName(item)}: {OnlyFromOriginalText(gameId)}",
                ItemImportStatus.HashMismatch => $"{ItemName(item)}: your disc isn't the version this was made from ({ExpectedDisc(gameId, item)}). "
                    + "other versions aren't supported yet.",
                ItemImportStatus.Failed when NeedsDisc(item) => $"{ItemName(item)}: this version of the game needs your {Row(gameId).Title.ToLowerInvariant()} disc again "
                    + "for something the copy lacks. a: pick the disc.",
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
            // straight to the picker (owner 2026-10-26): no "file or folder?" question first; a game that takes a dumped
            // folder too (Prime 3 / 4) is picked as a game file here
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
            if (FirstImportOf(TrilogyId))
            {
                FirstPick(TrilogyId);
                return;
            }
            PickDisc(TrilogyId, () =>
            {
                ImportPicked? src = Source(TrilogyId);
                (int _, int total) = TrilogyCount();
                if (src == null || total == 0) return;
                Confirm($"import everything from {Lower(src.Name)}? prime 1, echoes and corruption: {total} in all. the suits take a few minutes.",
                    TrilogyGames, src, null, TrilogyId);
            });
        }

        // "import all": every item of the game (the disc picker first if there's no disc yet)
        public void ImportAll(string gameId)
        {
            if (Busy || !Ready()) return;
            ImportGameInfo? g = Game(gameId);
            if (g == null || g.Items.Count == 0) return;
            ImportPicked? src = Source(gameId);
            string time = g.Items.Any(i => i.Kind == "hd") ? " the suits take a few minutes." : "";
            if ((src == null || DiscGone(src)) && KeptFor(gameId))
            {
                // the disc is gone (or never picked) but its files are kept: rebuild from them, no disc
                Popup!.Ask($"import all {TileCount(g).Total} from the import files copied from your disc?{time}", yes =>
                {
                    if (yes) Run(new[] { gameId }, null, g.Items.Select(i => i.Id).ToArray(), gameId);
                });
                return;
            }
            if (src == null && FirstImportOf(gameId))
            {
                FirstPick(gameId);
                return;
            }
            if (src == null)
            {
                PickDisc(gameId, () => ImportAll(gameId));
                return;
            }
            Confirm($"import all {TileCount(g).Total} from {Lower(src.Name)}?{time}", new[] { gameId }, src, null, gameId);
        }

        // ---- the import files (owner 2026-10-24, docs/IMPORT_RETENTION.md): a copy of everything each imported item is made
        //      from, so a change to it rebuilds with no disc. Asked on a game's first import; the disc is only needed again for
        //      something new. We never delete the player's ROM ourselves ----

        // the warning every copy notice carries (owner 2026-10-26)
        public const string NeedRomNotice = "notice: you will need your rom again if additional content is added to the game that is not already in these files.";

        // a game's import files are kept (ImportService.KeepSources): its items can be rebuilt without the disc
        public bool KeptFor(string gameId) => Service?.HasKept(gameId) == true;

        public long KeptBytes(string gameId) => Service?.KeptBytes(gameId) ?? 0;

        // IMPORT GAMES' DELETE IMPORT FILES: any game's
        public bool AnyKept() => DeletableGames.Any(KeptFor);

        public static string Megabytes(long bytes) => bytes >= 1024L * 1024 * 1024 ? $"{bytes / (1024.0 * 1024 * 1024):0.0} gb" : $"{Math.Max(1, bytes / (1024 * 1024))} mb";

        private static string[] GamesOf(string gameId) => gameId == TrilogyId ? TrilogyGames : new[] { gameId };

        // about how much a game's import files and its imported items take (measured on PC 2026-10-24 with -importcheck keep;
        // the Prime songs are most of a Prime game's share, and every build imports them since owner 2026-10-29)
        private static (long Keep, long Import) Estimate(string gameId)
        {
            bool songs = true;
            (long keep, long import) = gameId switch
            {
                "mp1" => songs ? (525, 235) : (365, 55),
                "mp2" => songs ? (680, 310) : (500, 135),
                "mp3" => songs ? (230, 225) : (45, 40),
                "mp4" => (60, 40),   // only the Sylux import's reads (owner 2026-10-29, docs/IMPORT_RETENTION.md §5)
                _ => (100, 50),
            };
            return (keep * 1024 * 1024, import * 1024 * 1024);
        }

        // nothing imported from the game yet (or since DELETE ALL) and no import files: its import asks about them first
        private bool FirstImport(string gameId) => !KeptFor(gameId) && Game(gameId)?.Items.Any(Clearable) != true;

        // something of the game needs its disc again (a thing the import files lack)
        public bool NeedsDiscIn(string gameId) => GamesOf(gameId).Any(id => Game(id)?.Items.Any(NeedsDisc) == true);

        // KEEP IMPORT FILES' size line: what's kept, or about what keeping takes
        public string KeepSize(string gameId)
        {
            long kept = GamesOf(gameId).Sum(KeptBytes);
            return kept > 0 ? Megabytes(kept) : "about " + Megabytes(GamesOf(gameId).Sum(id => Estimate(id).Keep));
        }

        // the game strip's KEEP IMPORT FILES reads IMPORT FILES KEPT, greyed
        public bool KeepDone(string gameId) => GamesOf(gameId).All(KeptFor) && !NeedsDiscIn(gameId);

        // free space where the imports go; null = unknown (then nothing is checked)
        private long? FreeBytes()
        {
            try
            {
                string full = Path.GetFullPath(_root!);
                DriveInfo? best = null;
                int bestLength = -1;
                foreach (DriveInfo d in DriveInfo.GetDrives())
                {
                    string r;
                    try
                    {
                        if (!d.IsReady) continue;
                        r = d.RootDirectory.FullName;
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        continue;
                    }
                    if (r.Length > bestLength && full.StartsWith(r, StringComparison.OrdinalIgnoreCase))
                    {
                        best = d;
                        bestLength = r.Length;
                    }
                }
                return best?.AvailableFreeSpace;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                Log?.Invoke("import: free space unknown (" + ex.Message + ")");
                return null;
            }
        }

        // ---- a game's first import (owner 2026-10-26): its page shows only the disc button. The disc picked, one ask about
        //      copying the import files (check: import and copy, X: import only), then the whole game is imported. Its
        //      KEEP FILES button shows from then on ----

        // nothing of the game (each of the Trilogy's three) imported and no import files
        public bool FirstImportOf(string gameId) => GamesOf(gameId).All(id => Game(id) != null && FirstImport(id));

        // the game page's disc button while FirstImportOf: the picker, the ask, the import
        public void FirstPick(string gameId)
        {
            if (Busy || !Ready()) return;
            PickDisc(gameId, () =>
            {
                if (Source(gameId) is not { } src || DiscGone(src)) return;
                string[] games = GamesOf(gameId);
                if (games.Any(id => Game(id) is not { Items.Count: > 0 })) return;
                long import = games.Sum(id => Estimate(id).Import);
                string[] first = games.Where(FirstImport).ToArray();
                long keep = first.Sum(id => Estimate(id).Keep);
                Popup!.Ask("you can also make a copy of the imported files so you can delete your rom and keep only necessary files. "
                    + NeedRomNotice + " "
                    + "do this now?", yes =>
                {
                    if (yes) RunWithRoom(games, src, null, gameId, first, import, keep, firstRun: true);
                    else RunWithRoom(games, src, null, gameId, Array.Empty<string>(), import, 0, firstRun: true);
                });
            });
        }

        // an import's question, then on a game's first import the import files' (owner 2026-10-24: before the import, with
        // their size; the ROM can be deleted after)
        private void Confirm(string question, string[] games, ImportPicked src, string[]? only, string key)
        {
            Popup!.Ask(question, yes =>
            {
                if (!yes) return;
                long import = only == null ? games.Sum(id => Estimate(id).Import) : 0;
                // IMPORT ALL (owner 2026-10-26): asked whenever a game's import files aren't kept, not only on its first import;
                // the whole game goes in, so it ends with the first import's short notice
                bool all = only == null;
                string[] first = games.Where(id => all ? !KeptFor(id) : FirstImport(id)).ToArray();
                if (first.Length == 0)
                {
                    RunWithRoom(games, src, only, key, first, import, 0);
                    return;
                }
                long keep = first.Sum(id => Estimate(id).Keep);
                Popup!.Ask("you can also make a copy of the imported files so you can delete your rom and keep only necessary files. "
                    + NeedRomNotice + " "
                    + $"do this now? about {Megabytes(keep)} more.", k =>
                {
                    if (k) RunWithRoom(games, src, only, key, first, import, keep, firstRun: all);
                    else RunWithRoom(games, src, only, key, Array.Empty<string>(), import, 0, firstRun: all);
                });
            });
        }

        // the run, once there's room for it: short of room for the import files, how much is missing and the import without
        // them; short even for the import, the same ask (the estimates are rough, the player may know better)
        private void RunWithRoom(string[] games, ImportPicked src, string[]? only, string key, string[] keep, long import, long keepBytes,
            bool firstRun = false)
        {
            long? free = FreeBytes();
            IEnumerable<string> keeps = keep.Concat(games.Where(KeptFor));
            if (free == null || free.Value >= import + keepBytes)
            {
                Run(games, src, only, key, keeps, firstRun);
                return;
            }
            Log?.Invoke($"import: {Megabytes(free.Value)} free, about {Megabytes(import)} + {Megabytes(keepBytes)} wanted");
            string question = keepBytes > 0 && free.Value >= import
                ? $"there isn't room to copy the import files: about {Megabytes(import + keepBytes - free.Value)} more is needed. import without them? your ROM is then needed for updates."
                : $"there may not be room for this import: about {Megabytes(import + keepBytes - free.Value)} more is needed. import anyway{(keepBytes > 0 ? ", without the import files" : "")}?";
            Popup!.Ask(question, yes =>
            {
                if (yes) Run(games, src, only, key, games.Where(KeptFor), firstRun);
            });
        }

        // the game strip's KEEP IMPORT FILES (owner 2026-10-24): make them, or add what an item needs the disc for; once
        // they're whole the button is greyed and says what they mean
        public void KeepButton(string gameId)
        {
            if (Busy || !Ready()) return;
            if (KeepDone(gameId))
            {
                Popup!.Inform($"the import files are copied ({Megabytes(GamesOf(gameId).Sum(KeptBytes))}). updates remake your imports from them, so the "
                    + "ROM can be deleted. " + NeedRomNotice);
                return;
            }
            KeepCopy(gameId);
        }

        // the import files of a game imported without them (or what an item needs the disc for): the disc is read once more
        private void KeepCopy(string gameId)
        {
            ImportPicked? src = Source(gameId);
            if (src == null || DiscGone(src))
            {
                PickDisc(gameId, () =>
                {
                    if (Source(gameId) is { } picked && !DiscGone(picked)) KeepCopy(gameId);
                });
                return;
            }
            string[] games = GamesOf(gameId);
            string[] needs = games.SelectMany(id => Game(id)?.Items.Where(NeedsDisc).Select(i => i.Id) ?? Enumerable.Empty<string>()).ToArray();
            string[] fresh = games.Where(id => !KeptFor(id)).ToArray();
            long keep = fresh.Sum(id => Estimate(id).Keep);
            string question = fresh.Length == 0
                ? $"add what {(needs.Length == 1 ? "1 item needs" : $"{needs.Length} items need")} from {Lower(src.Name)} to the import files?"
                : $"copy the import files from {Lower(src.Name)}? about {Megabytes(keep)}.";
            Popup!.Ask(question + " it takes a while. then the ROM can be deleted. " + NeedRomNotice, yes =>
            {
                if (yes) RunWithRoom(games, src, needs, gameId, games, 0, keep);
            });
        }

        // DELETE IMPORT FILES: a game's (null: every game's) import files only. What was imported stays, but updating it needs
        // the disc again (owner 2026-10-24: said more strongly when the ROM is gone too)
        public void DeleteImportFiles(string? gameId)
        {
            if (Busy || !Ready()) return;
            string[] games = (gameId == null ? DeletableGames : GamesOf(gameId)).Where(KeptFor).ToArray();
            if (games.Length == 0) return;
            bool gone = games.Any(id => Source(id) is not { } s || DiscGone(s));
            string whose = gameId == null ? "the copies of every game's files" : "the copies of this game's files";
            string size = Megabytes(games.Sum(KeptBytes));
            string question = gone
                ? $"delete {whose} ({size})? {(gameId == null ? "a ROM is" : "the ROM is")} gone too: your imports still play, "
                    + "but nothing can be updated until you pick the disc again."
                : $"delete {whose} ({size})? what you imported stays, but updating it will need the disc again.";
            Popup!.Ask(question, yes =>
            {
                if (!yes) return;
                try
                {
                    foreach (string id in games) Service!.DeleteKept(id);
                    Log?.Invoke($"import: deleted the import files of {String.Join(", ", games)}");
                    Changed?.Invoke();
                    Popup?.Inform("deleted the copies.");
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Log?.Invoke("import: delete import files failed: " + ex);
                    Changed?.Invoke();
                    Popup?.Inform("the import files couldn't all be deleted: " + Lower(ex.Message));
                }
            });
        }

        // a picked ROM that can't be opened any more (deleted, moved, access withdrawn)
        private static bool DiscGone(ImportPicked src)
        {
            if (src.Folder) return false;   // a folder is opened (and checked) by the run
            try
            {
                using Stream? s = ImportHost.Open(src.Token);
                return s == null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return true;
            }
        }

        // one item's row: import it (again)
        public void ImportOne(string gameId, ImportItemInfo item)
        {
            if (Busy || !Ready()) return;
            ImportPicked? src = Source(gameId);
            if (KeptFor(gameId) && !NeedsDisc(item))
            {
                // the kept import files make it with no disc (a thing they lack answers "needs the disc again" on its row)
                Popup!.Ask(item.Status == ItemImportStatus.Complete
                    ? $"import {ItemName(item)} again from the import files copied from your disc?"
                    : $"import {ItemName(item)} from the import files copied from your disc?", yes =>
                {
                    if (yes) Run(new[] { gameId }, null, new[] { item.Id }, gameId);
                });
                return;
            }
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
            Confirm(question, new[] { gameId }, src, new[] { item.Id }, gameId);
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

        // an entry's X (owner 2026-10-07): the ask box, then what those items' imports wrote is deleted
        // (ImportService.Remove) and their rows read IMPORT again. The import files stay
        public void Clear(string gameId, IReadOnlyList<ImportItemInfo> items, string what)
            => Clear(items.Select(i => (gameId, i)).ToList(), what, "your disc", Array.Empty<string>());

        // a game page's DELETE ALL: every entry and the game's import files (owner 2026-10-24: "deletes everything")
        public void ClearGame(string gameId, IReadOnlyList<ImportItemInfo> items, string what)
            => Clear(items.Select(i => (gameId, i)).ToList(), what, "your disc", new[] { gameId });

        // IMPORT GAMES' DELETE ALL: everything from every game, import files too
        public void ClearEverything()
            => Clear(DeletableGames.SelectMany(id => (Game(id)?.Items ?? new()).Select(i => (id, i))).ToList(), "everything you imported", "your discs",
                DeletableGames.ToArray());

        private void Clear(List<(string Game, ImportItemInfo Item)> items, string what, string source, string[] withFiles)
        {
            if (Busy || !Ready()) return;
            List<(string Game, ImportItemInfo Item)> clear = items.Where(x => Clearable(x.Item)).ToList();
            string[] files = withFiles.Where(KeptFor).ToArray();
            if (clear.Count == 0 && files.Length == 0) return;
            int made = clear.Count(x => Imported(x.Item));
            // how it comes back (owner 2026-10-26: the ask says so now there are import files): an entry's X keeps
            // them, so what they can make needs no disc; DELETE ALL deletes them, so the disc is needed again
            bool fromFiles = withFiles.Length == 0 && clear.Count > 0 && clear.All(x => KeptFor(x.Game) && !NeedsDisc(x.Item));
            string Again(string them) => fromFiles ? $"your import files are copied, so you can import {them} again without {source}."
                : $"you will need {source} to import {them} again.";
            string question = clear.Count == 0 ? $"delete {what}? nothing is imported, only the import files are left."
                : made == 0 ? $"delete {what}? none of them is imported; their rows go back to import."
                : clear.Count == 1 ? $"delete {what}? {Again("it")}"
                : $"delete {what}? {(made == 1 ? "the one" : $"the {made}")} imported {(made == 1 ? "is" : "are")} deleted. {Again(made == 1 ? "it" : "them")}";
            if (files.Length > 0)
            {
                // owner 2026-10-24: DELETE ALL's ask says the import files go too
                string kept = Megabytes(files.Sum(id => Service!.KeptBytes(id)));
                question = clear.Count == 0 || made == 0 ? $"delete {what}? its import files ({kept}) are deleted too. {Again("it")}"
                    : $"delete {what}? {(made == 1 ? "the one" : $"the {made}")} imported and the import files ({kept}) are deleted. {Again(made == 1 ? "it" : "them")}";
            }
            Popup!.Ask(question, yes =>
            {
                if (!yes) return;
                try
                {
                    foreach (IGrouping<string, (string Game, ImportItemInfo Item)> game in clear.GroupBy(x => x.Game))
                    {
                        Service!.Remove(game.Key, game.SelectMany(x => WithJoined(x.Item.Id)).ToList());
                    }
                    foreach (string id in files) Service!.DeleteKept(id);
                    Log?.Invoke($"import: deleted {String.Join(", ", clear.Select(x => x.Item.Id).Concat(files.Select(id => id + " import files")))}");
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

        // items an update added that the game's copied import files can't make (New), or that need the disc again, in a game
        // whose disc isn't picked and present: the main menu says the rom is needed (FrontendSession.TickNewContent)
        public int NewContentNeedingRom()
        {
            int n = 0;
            foreach (ImportGameInfo g in Service?.Games ?? Array.Empty<ImportGameInfo>())
            {
                ImportPicked? src = Source(g.Id);
                if (src != null && !DiscGone(src)) continue;
                foreach (ImportItemInfo i in g.Items) if (Offered(i) && (i.New || NeedsDisc(i))) n++;
            }
            return n;
        }

        // an item a stopped import removed (the Scout Drones, first in line, were lost this way on the Odin and never came
        // back: nothing marked them) counts too: the red badge, the main-menu box and UPDATE ALL bring it back
        public static bool WasStopped(ImportItemInfo item)
            => item.Status == ItemImportStatus.Missing && item.Message == ImportService.StoppedMessage;

        public static bool NeedsUpdate(ImportItemInfo item) => item.Status == ItemImportStatus.Stale || item.New || NeedsDisc(item) || WasStopped(item);

        // the games an UPDATE ALL reaches: a game page's own (the Trilogy page: its three), or (null) every game
        private IEnumerable<string> UpdateGames(string? gameId)
            => gameId == null ? DeletableGames : gameId == TrilogyId ? TrilogyGames : new[] { gameId };

        public int UpdateCount(string? gameId = null) => UpdateGames(gameId).Sum(id => Game(id)?.Items.Count(NeedsUpdate) ?? 0);

        private readonly HashSet<string> _keptTried = new();  // the games whose kept copy this UPDATE ALL already ran
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
                _keptTried.Clear();
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
            // the kept import files first (owner 2026-10-23: an update rebuilds from them, no disc): a game with a kept copy runs
            // its items from it once; what the copy lacks answers "needs the disc again" and is asked of the disc below
            string? keptGame = _updates.FirstOrDefault(id => !_keptTried.Contains(id) && KeptFor(id)
                && Game(id)?.Items.Any(i => NeedsUpdate(i) && !NeedsDisc(i)) == true);
            if (keptGame != null)
            {
                _keptTried.Add(keptGame);
                string[] ids = Game(keptGame)!.Items.Where(i => NeedsUpdate(i) && !NeedsDisc(i)).Select(i => i.Id).ToArray();
                Run(new[] { keptGame }, null, ids, keptGame);
                if (_task == null) EndUpdates("");
                return;
            }
            string first = _updates[0];
            ImportPicked? src = Source(first);
            if (src == null)
            {
                PickDisc(first, NextUpdate);
                return;
            }
            if (DiscGone(src))
            {
                // the remembered disc was moved or deleted (owner 2026-10-26: the run used to stop on it, the other games
                // never reached): forget it for every game that read from it and ask for it here, then carry on
                List<string> readers = _updates.Where(id => Source(id)?.Token == src.Token).ToList();
                foreach (string id in readers) WriteSource(id, null);
                Log?.Invoke($"import: update all: {src.Name} can't be opened any more");
                string title = Lower(Row(first).Title);
                Popup!.Ask($"{Lower(src.Name)} can't be opened any more (moved or deleted). pick your {title} disc again?", yes =>
                {
                    if (yes)
                    {
                        PickDisc(first, NextUpdate);
                        return;
                    }
                    // no: skip that disc's games, the rest still update
                    _updates.RemoveAll(readers.Contains);
                    _updateAnswers.Add($"{Lower(src.Name)} can't be opened any more: {title}'s items weren't updated.");
                    NextUpdate();
                });
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
        private bool _runKept;   // the run reads the kept import files, not a disc
        private bool _firstRun;  // the run is a game's first import (FirstPick)
        private volatile string _note = "";   // the service's "copying the import files" line, once it's at that step
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
                _owner._note = p.message.StartsWith("copying the import files", StringComparison.Ordinal) ? p.message : "";
            }
        }

        // games: one game, or the Trilogy's three from one disc; key: whose remembered pick this is (forgotten when the
        // disc turns out unreadable or wrong)
        // src == null: no disc, the games' kept import files are the source (ImportService.RebuildFromKept); `only` is then named
        // keep: the games whose import files this run keeps (owner 2026-10-24: the player's yes on the first import); null =
        // the games that already have them, so the copy grows with what's new
        // firstRun: a game's first import (FirstPick): a whole success answers "game successfully imported"
        private void Run(string[] games, ImportPicked? src, string[]? only, string key, IEnumerable<string>? keep = null, bool firstRun = false)
        {
            ImportService service = Service!;
            bool fromKept = src == null;
            string srcName = src?.Name ?? "the copied import files";
            var keeps = new HashSet<string>(keep ?? games.Where(KeptFor));
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
            _runName = srcName;
            _runKept = fromKept;
            _firstRun = firstRun;
            _note = "";
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
            Log?.Invoke($"import: {String.Join("+", games)} from {srcName}, {(all ? "all " + items.Count : String.Join(", ", only!))}");
            Popup!.BusyCancel($"importing from {Lower(srcName)}.", () =>
            {
                _cancelled = true;
                _cts?.Cancel();
                Popup?.SetText("stopping. nothing from this import is kept.");
                Log?.Invoke("import: stop pressed");
            });
            if (src == null)
            {
                string[] ids = only!;
                _task = Task.Run(() =>
                {
                    var reports = new List<ImportReport>();
                    for (int k = 0; k < games.Length; k++)
                    {
                        ct.ThrowIfCancellationRequested();
                        _gameIndex = k;
                        string[] mine = service.Recipes.ForGame(games[k]).Select(r => r.Id).Where(ids.Contains).ToArray();
                        reports.Add(service.RebuildFromKept(games[k], sink, ct, mine));
                    }
                    return reports;
                }, ct);
                return;
            }
            if (FolderOnly(games[0]) || src.Folder)
            {
                string marker = service.AcceptedSources(games[0]).Marker;
                _task = Task.Run(() =>
                {
                    // a folder: its tree read in place (a picked Android folder through its own source, else a path)
                    service.KeepSources = keeps.Contains(games[0]);
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
                        service.KeepSources = keeps.Contains(games[k]);
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
            string note = _note;
            if (note.Length > 0) return $"importing from {Lower(_runName)}.\n{note}\nthis lets you delete the ROM afterwards.";
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

        // the run's answer, then what became of the import files (owner 2026-10-24: say again that the disc can go)
        private string Summary(List<ImportReport> reports)
        {
            bool keptOnly = reports.All(r => r.Items.Count == 0) && reports.Any(r => r.KeepError != null);
            // a first import that all went in (owner 2026-10-26): the plain notice, the copy and the rom named when it was made
            List<ImportItemInfo> all = reports.SelectMany(r => r.Items).ToList();
            if (_firstRun && !_runKept && all.Count > 0 && all.All(i => i.Status == ItemImportStatus.Complete)
                && reports.All(r => String.IsNullOrEmpty(r.KeepError)))
            {
                return reports.Any(r => r.KeepError == "") ? "game successfully imported and files copied. you may now delete your rom."
                    : "game successfully imported.";
            }
            string s = keptOnly ? "" : reports.All(r => r.Items.Count == 0) ? "nothing needed importing." : ItemsSummary(reports);
            string? failedKeep = reports.Select(r => r.KeepError).FirstOrDefault(e => !String.IsNullOrEmpty(e));
            if (failedKeep != null)
            {
                s += $" the import files couldn't be copied ({Lower(failedKeep)}): keep your ROM, updates will need it.";
            }
            else if (!_runKept && reports.Any(r => r.KeepError == ""))
            {
                s += " import files copied. rom can now be deleted. new content updates may require it again.";
            }
            return s.TrimStart();
        }

        private string ItemsSummary(List<ImportReport> reports)
        {
            var items = Fold(reports.SelectMany(r => r.Items).ToList());
            int n = items.Count;
            int done = items.Count(i => i.Status == ItemImportStatus.Complete);
            int wrong = items.Count(i => i.Status == ItemImportStatus.HashMismatch);
            int original = items.Count(i => i.Status == ItemImportStatus.HashMismatch && OnlyFromOriginal(i));
            int needDisc = items.Count(NeedsDisc);
            int failed = items.Count(i => i.Status == ItemImportStatus.Failed) - needDisc;
            if (n == 1)
            {
                ImportItemInfo it = items[0];
                return it.Status switch
                {
                    ItemImportStatus.Complete => $"{ItemName(it)} imported.",
                    ItemImportStatus.HashMismatch when OnlyFromOriginal(it) => $"{ItemName(it)} wasn't imported: {OnlyFromOriginalText(_runGames[0])}",
                    ItemImportStatus.HashMismatch => $"{ItemName(it)} wasn't imported: your disc isn't the version it was made from. other versions aren't supported yet.",
                    _ when NeedsDisc(it) => $"{ItemName(it)} wasn't imported: this version needs your disc again for something the copy lacks. pick the disc and update it.",
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
            if (needDisc > 0) s += $" {needDisc} need{(needDisc == 1 ? "s" : "")} your disc again for something the copy lacks: see {(needDisc == 1 ? "its row" : "their rows")}.";
            List<ImportItemInfo> broken = items.Where(i => i.Status == ItemImportStatus.Failed && !NeedsDisc(i)).ToList();
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
            if (_runKept && ex is not OperationCanceledException)
            {
                Log?.Invoke("import: " + ex);
                return "the copied import files couldn't be used: " + Lower(ex.Message) + ". pick your disc to import again.";
            }
            switch (ex)
            {
            case OperationCanceledException:
                return "import stopped. nothing from it was kept.";
            case WiiKeyRequiredException:
                return "this ROM is encrypted and needs your wii's key file: set it on the wii key file row. .rvz and .wia ROMs need none.";
            case SwitchKeyRequiredException { Firmware: string fw }:
                return "your switch keys are older than this game"
                    + (fw.Length > 0 ? $": it needs keys from switch system version {fw} or newer" : "")
                    + ". update your switch, dump its prod.keys again and set them on the switch keys box.";
            case SwitchKeyRequiredException:
                return "this game file is encrypted and needs your switch's prod.keys, dumped from your own switch: set it on the switch keys box.";
            case FileNotFoundException or UnauthorizedAccessException or DirectoryNotFoundException:
                if (Source(_runKey) is { Folder: false } picked && !DiscGone(picked))
                {
                    // the disc still opens: a file the import wanted wasn't on it (owner 2026-10-26, Samsung: echoes_retail.rvz
                    // was blamed and forgotten while it sat in Download). Keep the disc and say what was missing
                    Log?.Invoke("import: " + ex);
                    return "the import failed: " + Lower(ex.Message) + ".";
                }
                WriteSource(_runKey, null); // forget it: the next press asks for the disc again
                return $"{Lower(_runName)} can't be opened any more. pick your disc again.";
            case InvalidDataException:
                WriteSource(_runKey, null); // not this game's disc (or no ROM at all): ask for the disc next time
                return Lower(ex.Message) + ".";
            default:
                Log?.Invoke("import: " + ex);
                return "the import failed: " + Lower(ex.Message);
            }
        }
    }
}
