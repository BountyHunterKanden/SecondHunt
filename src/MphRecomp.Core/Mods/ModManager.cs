using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MphRecomp.Save;

namespace MphRecomp.Mods
{
    // A discovered mod before loading: its manifest plus where its code lives (a folder, or inline
    // source for built-in/test mods). ReadSource() returns the entry Lua.
    public sealed class ModEntry
    {
        public ModManifest Manifest { get; set; } = new();
        public string? Folder { get; set; }
        public string? Source { get; set; }
        // extra files of an inline mod, by relative path ("lib/util.lua"), for `require`
        public Dictionary<string, string>? Files { get; set; }
        // why this mod can't run (bad mod.json, missing entry, a missing / too-old required mod, a conflict...), or null
        public string? Problem => LoadProblem ?? ResolveProblem;
        // found when the mod was read (Discover / Add)
        public string? LoadProblem { get; set; }
        // found by ModManager.Resolve (requires, conflicts); cleared by the next Resolve
        public string? ResolveProblem { get; internal set; }

        public string ReadSource()
            => Source ?? File.ReadAllText(Path.Combine(Folder!, Manifest.Entry));

        // `require`: one of the mod's own files by relative path, or null. A folder mod can't read outside its folder.
        public string? ReadFile(string rel)
        {
            if (Folder == null) return Files != null && Files.TryGetValue(rel, out string? s) ? s : null;
            try
            {
                string root = Path.GetFullPath(Folder).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                    + Path.DirectorySeparatorChar;
                string full = Path.GetFullPath(Path.Combine(root, rel));
                if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !File.Exists(full)) return null;
                return File.ReadAllText(full);
            }
            catch (Exception) { return null; }
        }

        // A fingerprint of the mod's files (sha256 over every file's relative path and bytes, in path order); a replay
        // records it so playback can tell the mod changed.
        public string ComputeSha()
        {
            using var sha = SHA256.Create();
            void Add(string rel, byte[] bytes)
            {
                byte[] name = Encoding.UTF8.GetBytes(rel.Replace('\\', '/') + "\n");
                sha.TransformBlock(name, 0, name.Length, null, 0);
                byte[] len = BitConverter.GetBytes((long)bytes.Length);
                sha.TransformBlock(len, 0, len.Length, null, 0);
                sha.TransformBlock(bytes, 0, bytes.Length, null, 0);
            }
            if (Folder != null && Directory.Exists(Folder))
            {
                var files = Directory.GetFiles(Folder, "*", SearchOption.AllDirectories)
                    .Select(f => (Rel: Path.GetRelativePath(Folder, f).Replace('\\', '/'), Full: f))
                    .OrderBy(f => f.Rel, StringComparer.Ordinal);
                foreach (var f in files) Add(f.Rel, File.ReadAllBytes(f.Full));
            }
            else
            {
                Add(Manifest.Entry, Encoding.UTF8.GetBytes(Source ?? ""));
                if (Files != null)
                    foreach (var kv in Files.OrderBy(k => k.Key, StringComparer.Ordinal)) Add(kv.Key, Encoding.UTF8.GetBytes(kv.Value));
                Add("mod.json", JsonSerializer.SerializeToUtf8Bytes(Manifest));
            }
            sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
            return Convert.ToHexString(sha.Hash!).ToLowerInvariant();
        }
    }

    // Persisted mod-manager state: user load order + which mods are turned off. Lives in a reserved
    // save slot so it survives sessions.
    public sealed class ModManagerState
    {
        public List<string> Order { get; set; } = new();
        public List<string> Disabled { get; set; } = new();
    }

    // The in-app mod manager backend: discovers installed mods, tracks enabled state + load order
    // (persisted in the save), and loads the enabled mods into a ModHost in order. The UI is a thin
    // shell over this; all the logic here is headlessly testable.
    public sealed class ModManager
    {
        private const string SaveKey = "__modmanager";
        private readonly List<ModEntry> _entries = new();
        private readonly List<(string Id, string Problem)> _problems = new();
        private ModManagerState _state = new();

        public IReadOnlyList<ModEntry> Available => _entries;
        // mods that can't run and why: folders whose mod.json didn't read (by folder name), duplicates, and every
        // entry with a Problem (after Resolve)
        public IReadOnlyList<(string Id, string Problem)> Problems
            => _problems.Concat(_entries.Where(e => e.Problem != null).Select(e => (e.Manifest.Id, e.Problem!))).ToList();

        // Scan a mods directory: each subfolder with a mod.json becomes an available entry (not yet
        // loaded). A mod.json that doesn't read is listed in Problems; a mod that reads but can't run (bad Kind, newer
        // API, missing entry script) is kept with its Problem set so the MODS page can say why.
        public void Discover(string modsDir)
        {
            if (!Directory.Exists(modsDir)) return;
            foreach (string folder in Directory.GetDirectories(modsDir).OrderBy(f => f, StringComparer.Ordinal))
            {
                string manifestPath = Path.Combine(folder, "mod.json");
                if (!File.Exists(manifestPath)) continue;
                string name = Path.GetFileName(folder);
                ModManifest? manifest;
                try { manifest = JsonSerializer.Deserialize<ModManifest>(File.ReadAllText(manifestPath)); }
                catch (Exception ex)
                {
                    _problems.Add((name, "mod.json doesn't read: " + FirstLine(ex.Message)));
                    continue;
                }
                if (manifest == null) { _problems.Add((name, "mod.json is empty")); continue; }
                if (string.IsNullOrEmpty(manifest.Id)) manifest.Id = name;
                if (_entries.Any(e => e.Manifest.Id == manifest.Id))
                {
                    _problems.Add((name, $"same Id '{manifest.Id}' as another mod; only the first loads"));
                    continue;
                }
                var entry = new ModEntry { Manifest = manifest, Folder = folder };
                entry.LoadProblem = manifest.Validate();
                if (entry.LoadProblem == null && entry.ReadFile(manifest.Entry) == null)
                    entry.LoadProblem = "entry script not found: " + manifest.Entry;
                _entries.Add(entry);
            }
        }

        static string FirstLine(string s) { int i = s.IndexOf('\n'); return (i < 0 ? s : s[..i]).Trim(); }

        // Register an inline mod (built-ins / tests). Returns the entry.
        public ModEntry Add(ModManifest manifest, string luaSource, Dictionary<string, string>? files = null)
        {
            var e = new ModEntry { Manifest = manifest, Source = luaSource, Files = files, LoadProblem = manifest.Validate() };
            _entries.Add(e);
            return e;
        }

        public bool IsEnabled(string id) => !_state.Disabled.Contains(id);

        public void SetEnabled(string id, bool enabled)
        {
            if (enabled) _state.Disabled.Remove(id);
            else if (!_state.Disabled.Contains(id)) _state.Disabled.Add(id);
        }

        // Set an explicit load order (ids). Unlisted-but-discovered mods load after these, in
        // discovery order.
        public void SetOrder(IEnumerable<string> ids) => _state.Order = ids.ToList();

        public void LoadState(SaveData save)
            => _state = SaveManager.GetModData(save, SaveKey, new ModManagerState()) ?? new ModManagerState();

        public void SaveState(SaveData save) => SaveManager.SetModData(save, SaveKey, _state);

        // Enabled entries in effective load order: those named in Order first (that exist + are
        // enabled), then any remaining enabled entries in discovery order.
        public IEnumerable<ModEntry> EnabledInOrder()
        {
            var byId = _entries.ToDictionary(e => e.Manifest.Id);
            var emitted = new HashSet<string>();
            foreach (string id in _state.Order)
                if (byId.TryGetValue(id, out var e) && IsEnabled(id) && emitted.Add(id))
                    yield return e;
            foreach (var e in _entries)
                if (IsEnabled(e.Manifest.Id) && emitted.Add(e.Manifest.Id))
                    yield return e;
        }

        // The enabled mods that can run, in the order they load (docs/MOD_API.md §4.15): the player's order, except
        // that a mod always loads after the mods it Requires. A mod is left out (its Problem says why) when a mod it
        // requires is missing, off, too old or itself left out, when a gameplay mod requires a cosmetic one (a replay
        // only brings gameplay mods back), when it conflicts with a mod earlier in the order, or when requires go in a
        // loop. Earlier Problems from Discover stay.
        public List<ModEntry> Resolve()
        {
            foreach (ModEntry e in _entries) e.ResolveProblem = null;
            List<ModEntry> live = EnabledInOrder().Where(e => e.Problem == null).ToList();
            void Refuse(ModEntry e, string why) { e.ResolveProblem = why; live.Remove(e); }

            bool RequiresMet()
            {
                bool changed = false;
                foreach (ModEntry e in live.ToList())
                {
                    foreach (string r in e.Manifest.Requires)
                    {
                        var parsed = ModVersion.ParseRef(r)!.Value;
                        ModEntry? dep = live.Find(x => x.Manifest.Id == parsed.Id);
                        string? why = null;
                        if (dep == null)
                        {
                            ModEntry? known = _entries.Find(x => x.Manifest.Id == parsed.Id);
                            why = known == null ? $"needs mod '{parsed.Id}'"
                                : !IsEnabled(parsed.Id) ? $"needs mod '{parsed.Id}' turned on"
                                : $"needs mod '{parsed.Id}', which can't run";
                        }
                        else if (!ModVersion.Satisfies(dep.Manifest.Version, parsed.Constraints))
                            why = $"needs '{parsed.Id}' {ModVersion.Describe(parsed.Constraints)} (have {dep.Manifest.Version})";
                        else if (e.Manifest.Gameplay && !dep.Manifest.Gameplay)
                            why = $"a gameplay mod can't need the cosmetic mod '{parsed.Id}'";
                        if (why != null) { Refuse(e, why); changed = true; break; }
                    }
                }
                return changed;
            }
            while (RequiresMet()) { }
            // conflicts: the later mod in the order gives way (either side may name the other)
            for (int i = 0; i < live.Count; i++)
            {
                ModEntry e = live[i];
                for (int j = 0; j < i; j++)
                {
                    ModEntry earlier = live[j];
                    if (Names(e.Manifest.Conflicts, earlier) || Names(earlier.Manifest.Conflicts, e))
                    {
                        Refuse(e, $"conflicts with '{earlier.Manifest.Id}'");
                        i--;
                        break;
                    }
                }
            }
            while (RequiresMet()) { }
            // a stable topological sort: the earliest mod (player's order) whose requires are all placed goes next
            var placed = new List<ModEntry>();
            var left = new List<ModEntry>(live);
            while (left.Count > 0)
            {
                ModEntry? next = left.Find(e => e.Manifest.Requires.All(r =>
                    placed.Exists(p => p.Manifest.Id == ModVersion.ParseRef(r)!.Value.Id)));
                if (next == null)
                {
                    foreach (ModEntry e in left) e.ResolveProblem = "requires loop: " + string.Join(", ", left.Select(x => x.Manifest.Id));
                    break;
                }
                placed.Add(next);
                left.Remove(next);
            }
            return placed;
        }

        static bool Names(List<string> refs, ModEntry other)
        {
            foreach (string r in refs)
            {
                var p = ModVersion.ParseRef(r);
                if (p != null && p.Value.Id == other.Manifest.Id && ModVersion.Satisfies(other.Manifest.Version, p.Value.Constraints))
                    return true;
            }
            return false;
        }

        // Load the enabled mods (resolved order) into an EXISTING host -- use this when the host already
        // has its game context wired (Config/Assets/World), so mods see it during their init.
        public void LoadInto(ModHost host, Action<string, string>? log = null, SaveData? save = null)
        {
            foreach ((string id, string problem) in Problems) log?.Invoke(id, "not loaded: " + problem);
            foreach (ModEntry e in Resolve())
            {
                try { host.LoadFromSource(e.Manifest, e.ReadSource(), save, e.ReadFile); }
                catch (Exception ex) { log?.Invoke(e.Manifest.Id, "could not read source: " + ex.Message); }
            }
        }

        // Convenience: build a fresh ModHost and load the enabled mods into it. A mod that fails to
        // load is disabled by the host (its Error is set) but does not stop the others.
        public ModHost LoadEnabled(Action<string, string>? log = null)
        {
            var host = new ModHost(log);
            LoadInto(host, log);
            return host;
        }
    }
}
