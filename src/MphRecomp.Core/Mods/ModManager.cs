using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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

        public string ReadSource()
            => Source ?? File.ReadAllText(Path.Combine(Folder!, Manifest.Entry));
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
        private ModManagerState _state = new();

        public IReadOnlyList<ModEntry> Available => _entries;

        // Scan a mods directory: each subfolder with a mod.json becomes an available entry (not yet
        // loaded). Invalid/missing manifests are skipped.
        public void Discover(string modsDir)
        {
            if (!Directory.Exists(modsDir)) return;
            foreach (string folder in Directory.GetDirectories(modsDir))
            {
                string manifestPath = Path.Combine(folder, "mod.json");
                if (!File.Exists(manifestPath)) continue;
                try
                {
                    var manifest = JsonSerializer.Deserialize<ModManifest>(File.ReadAllText(manifestPath));
                    if (manifest == null) continue;
                    if (string.IsNullOrEmpty(manifest.Id)) manifest.Id = Path.GetFileName(folder);
                    if (_entries.Any(e => e.Manifest.Id == manifest.Id)) continue; // first wins
                    _entries.Add(new ModEntry { Manifest = manifest, Folder = folder });
                }
                catch (Exception) { /* skip malformed */ }
            }
        }

        // Register an inline mod (built-ins / tests). Returns the entry.
        public ModEntry Add(ModManifest manifest, string luaSource)
        {
            var e = new ModEntry { Manifest = manifest, Source = luaSource };
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

        // Load the enabled mods (in order) into an EXISTING host -- use this when the host already
        // has its game context wired (Config/Assets/World), so mods see it during their init.
        public void LoadInto(ModHost host, Action<string, string>? log = null)
        {
            foreach (ModEntry e in EnabledInOrder())
            {
                try { host.LoadFromSource(e.Manifest, e.ReadSource()); }
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
