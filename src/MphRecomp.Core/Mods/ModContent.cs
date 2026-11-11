using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MphRead;

namespace MphRecomp.Mods
{
    // Mod content (docs/MOD_API.md phase 3): a mod's files/ folder mirrors the game's own file tree (Paths.FileSystem,
    // e.g. files/stringTables/ScanLog.bin), and while the mod runs the game reads that copy instead of its own. The hook
    // is MphRead's Read.HostReadFile (Read.ReadBytes and Read.ReadFileBytes): set only while some mod has files mounted.
    //
    // Two groups, mounted by LiveMods with their hosts. Cosmetic mods may only replace files nothing in the match's rules
    // or replays reads (CosmeticFolders: text and HUD pictures); their other files are skipped with a note in the mod
    // log. Gameplay mods may replace any game file, for the matches they run in (their files are part of the sha a
    // replay records). A gameplay mod's file wins over a cosmetic one, and within a group the later mod in MODS order
    // wins. MPH files only (not First Hunt's). Nothing is mounted in online or LAN games (LiveMods skips all mods there).
    //
    // A file is read when the game loads it: most loads happen as a room loads (the Scene clears the model and string
    // caches), so a change shows from the next room or match. Files the app loads once at start-up don't follow yet.
    public static class ModContent
    {
        public const string FolderName = "files";

        // the game folders a cosmetic mod's files may replace (lowercase, '/'-separated prefixes of the relative path)
        public static readonly string[] CosmeticFolders = { "stringtables", "hud/" };

        // relative path (lowercase, '/') -> the mod's file and its mod id
        static Dictionary<string, (string Full, string ModId)> _cosmetic = new(), _play = new();
        static string? _root; // Paths.FileSystem, full, with a trailing separator

        // how many files each group has mounted
        public static int CosmeticCount => _cosmetic.Count;
        public static int GameplayCount => _play.Count;

        public static bool CosmeticAllowed(string rel)
        {
            string key = Key(rel);
            return CosmeticFolders.Any(f => key.StartsWith(f, StringComparison.Ordinal));
        }

        // the cosmetic mods' files (null or empty = none); note(modId, what) hears about skipped files
        public static void MountCosmetic(IEnumerable<ModEntry>? mods, Action<string, string>? note = null)
        {
            _cosmetic = Index(mods, cosmetic: true, note);
            Install();
        }

        // the gameplay mods' files for the match starting (null or empty = none)
        public static void MountGameplay(IEnumerable<ModEntry>? mods, Action<string, string>? note = null)
        {
            _play = Index(mods, cosmetic: false, note);
            Install();
        }

        public static void Clear()
        {
            _cosmetic = new();
            _play = new();
            Install();
        }

        // the bytes the game reads for this relative game path: a mounted mod's copy, else the game's own (null = neither)
        public static byte[]? Read(string rel)
        {
            if (Find(Key(rel)) is string full) return File.ReadAllBytes(full);
            if (string.IsNullOrEmpty(Paths.FileSystem)) return null;
            try
            {
                string path = Paths.Combine(Paths.FileSystem, rel);
                return IsUnder(Path.GetFullPath(path), BaseRoot()) && File.Exists(path) ? File.ReadAllBytes(path) : null;
            }
            catch (Exception) { return null; }
        }

        // which mod's copy the game reads for this relative path (null = the game's own)
        public static string? SourceMod(string rel)
        {
            string key = Key(rel);
            return _play.TryGetValue(key, out var p) ? p.ModId : _cosmetic.TryGetValue(key, out var c) ? c.ModId : null;
        }

        // ---- internals ----

        static void Install()
        {
            _root = _cosmetic.Count + _play.Count > 0 ? BaseRoot() : null;
            MphRead.Read.HostReadFile = _root != null ? HostRead : null;
        }

        static string BaseRoot()
        {
            string root = Path.GetFullPath(Paths.FileSystem);
            return root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        }

        // Read.HostReadFile: the full path the game was about to read
        static byte[]? HostRead(string fullPath)
        {
            if (_root == null) return null;
            string full;
            try { full = Path.GetFullPath(fullPath); } catch (Exception) { return null; }
            if (!full.StartsWith(_root, StringComparison.OrdinalIgnoreCase)) return null;
            string? mod = Find(Key(full[_root.Length..]));
            return mod == null ? null : File.ReadAllBytes(mod);
        }

        static string? Find(string key)
            => _play.TryGetValue(key, out var p) ? p.Full : _cosmetic.TryGetValue(key, out var c) ? c.Full : null;

        static string Key(string rel) => rel.Replace('\\', '/').TrimStart('/').ToLowerInvariant();

        static bool IsUnder(string full, string root) => full.StartsWith(root, StringComparison.OrdinalIgnoreCase);

        static Dictionary<string, (string Full, string ModId)> Index(IEnumerable<ModEntry>? mods, bool cosmetic,
            Action<string, string>? note)
        {
            var index = new Dictionary<string, (string, string)>(StringComparer.Ordinal);
            if (mods == null) return index;
            foreach (ModEntry e in mods) // in load order: a later mod's file replaces an earlier one's
            {
                if (e.Folder == null) continue;
                string dir = Path.Combine(e.Folder, FolderName);
                if (!Directory.Exists(dir)) continue;
                string root = Path.GetFullPath(dir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                    + Path.DirectorySeparatorChar;
                int skipped = 0;
                string? firstSkipped = null;
                foreach (string f in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                {
                    string full = Path.GetFullPath(f);
                    if (!IsUnder(full, root)) continue;
                    string key = Key(full[root.Length..]);
                    if (cosmetic && !CosmeticFolders.Any(x => key.StartsWith(x, StringComparison.Ordinal)))
                    {
                        skipped++;
                        firstSkipped ??= key;
                        continue;
                    }
                    index[key] = (full, e.Manifest.Id);
                }
                if (skipped > 0)
                    note?.Invoke(e.Manifest.Id, $"{skipped} file(s) under files/ skipped (e.g. {firstSkipped}): a cosmetic mod may only " +
                        "replace text (stringTables*) and HUD pictures (hud/); make it a gameplay mod to replace other game files");
            }
            return index;
        }
    }
}
