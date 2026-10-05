using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MoonSharp.Interpreter;
using MphRecomp.Assets;
using MphRecomp.Config;
using MphRecomp.Save;
using MphRecomp.Sim;

// mph-recomp moddability tier 3: a sandboxed MoonSharp Lua runtime. Each mod runs in its own
// hard-sandboxed Script (no file/OS access from Lua) and talks to the game only through the
// injected `mod` API: log, event hooks (mod.on / host.Fire), and persistent per-mod storage
// (mod.save / mod.load) backed by the versioned save system's isolated ModData slots. A mod that
// throws on load or in a hook is caught and isolated -- it never crashes the host. Pure managed
// logic (no rendering) -> headlessly testable; MoonSharp runs in interpreter mode (AOT-safe).
namespace MphRecomp.Mods
{
    public sealed class ModManifest
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string Version { get; set; } = "0.0.0";
        public string? Description { get; set; }
        public string Entry { get; set; } = "main.lua"; // entry script, for folder mods
        // options the player can change from the MODS screens (before a game, and in the pause menu while playing);
        // the mod reads the current value with mod.setting(id)
        public List<ModSetting> Settings { get; set; } = new();
    }

    // One player-facing option a mod declares in its mod.json, e.g.
    //   { "Id": "damage", "Name": "damage", "Values": ["1x", "2x", "4x"], "Default": "1x", "Description": "..." }
    public sealed class ModSetting
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string? Description { get; set; }
        public List<string> Values { get; set; } = new();
        public string? Default { get; set; }

        public string DefaultValue => Default ?? (Values.Count > 0 ? Values[0] : "");
    }

    public sealed class ModInstance
    {
        public ModManifest Manifest { get; }
        public Script Script { get; }
        public bool Enabled { get; internal set; } = true;
        public string? Error { get; internal set; } // set if load/run failed
        internal readonly Dictionary<string, List<Closure>> Hooks = new();
        internal readonly Dictionary<string, JsonElement> Data = new(); // persistent per-mod KV
        internal ModInstance(ModManifest m, Script s) { Manifest = m; Script = s; }
    }

    public sealed class ModHost
    {
        private readonly List<ModInstance> _mods = new();
        private readonly Dictionary<string, ModInstance> _byId = new();
        private readonly Action<string, string> _log; // (modId, message)

        // Optional game context. When set (the GameSession wires these), the mod API gains
        // mod.config (read tuning), mod.asset (read an override-able asset), and mod.spawn (add a
        // sim entity). Left null in bare/headless use -> those calls no-op / return defaults.
        public GameConfig? Config { get; set; }
        public AssetVfs? Assets { get; set; }
        public SimWorld? World { get; set; }
        // (mod id, setting id) -> the player's current value for a setting the mod declares (null = its default)
        public Func<string, string, string?>? SettingLookup { get; set; }

        public IReadOnlyList<ModInstance> Mods => _mods;
        public ModInstance? Get(string id) => _byId.TryGetValue(id, out var m) ? m : null;

        public ModHost(Action<string, string>? log = null)
            => _log = log ?? ((_, _) => { });

        // Load a mod from Lua source. Sandboxed; on any load/parse/runtime error the mod is
        // disabled with Error set and the host keeps running. Returns the instance either way.
        // If `save` is given, the mod's persisted data is hydrated BEFORE its init runs, so
        // mod.load() during init sees values from previous sessions.
        public ModInstance LoadFromSource(ModManifest manifest, string luaSource, SaveData? save = null)
        {
            if (string.IsNullOrEmpty(manifest.Id)) throw new ArgumentException("mod id required");
            if (_byId.ContainsKey(manifest.Id)) throw new InvalidOperationException($"duplicate mod id '{manifest.Id}'");

            var script = new Script(CoreModules.Preset_HardSandbox); // no io/os/loadfile from Lua
            var inst = new ModInstance(manifest, script);
            InstallApi(inst);
            _mods.Add(inst);
            _byId[manifest.Id] = inst;
            if (save != null)
                foreach (var kv in SaveManager.GetModData(save, manifest.Id, new Dictionary<string, JsonElement>()))
                    inst.Data[kv.Key] = kv.Value;
            try
            {
                script.DoString(luaSource, codeFriendlyName: manifest.Id);
            }
            catch (InterpreterException ex) { Disable(inst, ex.DecoratedMessage ?? ex.Message); }
            catch (Exception ex) { Disable(inst, ex.Message); }
            return inst;
        }

        // Discover folder mods under `dir`: each subfolder with a mod.json (Id/Name/Version/Entry)
        // and its entry Lua is loaded. Missing/invalid mods are skipped with a logged error.
        public void LoadDirectory(string dir)
        {
            if (!Directory.Exists(dir)) return;
            foreach (string folder in Directory.GetDirectories(dir))
            {
                string manifestPath = Path.Combine(folder, "mod.json");
                if (!File.Exists(manifestPath)) continue;
                try
                {
                    var manifest = JsonSerializer.Deserialize<ModManifest>(File.ReadAllText(manifestPath))
                                   ?? throw new Exception("empty manifest");
                    if (string.IsNullOrEmpty(manifest.Id)) manifest.Id = Path.GetFileName(folder);
                    string entry = Path.Combine(folder, manifest.Entry);
                    if (!File.Exists(entry)) { _log(manifest.Id, "entry script not found: " + manifest.Entry); continue; }
                    LoadFromSource(manifest, File.ReadAllText(entry));
                }
                catch (Exception ex) { _log(Path.GetFileName(folder), "failed to load: " + ex.Message); }
            }
        }

        // Fire a named event to every enabled mod's registered hooks, newest-registered last. Each
        // hook call is isolated: a throwing hook is logged and skipped, others still run.
        public void Fire(string eventName, params object[] args)
        {
            for (int i = 0; i < _mods.Count; i++)
            {
                ModInstance inst = _mods[i];
                if (!inst.Enabled || !inst.Hooks.TryGetValue(eventName, out var closures)) continue;
                DynValue[] luaArgs = ToLuaArgs(inst.Script, args);
                for (int j = 0; j < closures.Count; j++)
                {
                    try { inst.Script.Call(closures[j], luaArgs); }
                    catch (InterpreterException ex) { _log(inst.Manifest.Id, $"hook '{eventName}': {ex.DecoratedMessage ?? ex.Message}"); }
                    catch (Exception ex) { _log(inst.Manifest.Id, $"hook '{eventName}': {ex.Message}"); }
                }
            }
        }

        // Pull each mod's persisted data out of the save's isolated ModData slot into memory.
        public void LoadData(SaveData save)
        {
            foreach (ModInstance inst in _mods)
            {
                inst.Data.Clear();
                var slot = SaveManager.GetModData(save, inst.Manifest.Id, new Dictionary<string, JsonElement>());
                foreach (var kv in slot) inst.Data[kv.Key] = kv.Value;
            }
        }

        // Push each mod's in-memory data back into the save's ModData (isolated per mod id).
        public void SaveDataTo(SaveData save)
        {
            foreach (ModInstance inst in _mods)
                SaveManager.SetModData(save, inst.Manifest.Id, inst.Data);
        }

        // ---- internals ----

        private void Disable(ModInstance inst, string error)
        {
            inst.Enabled = false;
            inst.Error = error;
            _log(inst.Manifest.Id, "disabled: " + error);
        }

        // Install the per-mod `mod` global. Every mod gets its own table (own id + own storage +
        // own hooks) so mods are isolated from one another.
        private void InstallApi(ModInstance inst)
        {
            Script s = inst.Script;
            var api = new Table(s)
            {
                ["id"] = inst.Manifest.Id,
                ["name"] = inst.Manifest.Name,
                ["version"] = inst.Manifest.Version
            };
            api["log"] = DynValue.NewCallback((ctx, a) =>
            {
                _log(inst.Manifest.Id, a.Count > 0 ? a[0].ToPrintString() : "");
                return DynValue.Nil;
            });
            api["on"] = DynValue.NewCallback((ctx, a) =>
            {
                if (a.Count < 2 || a[0].Type != DataType.String || a[1].Type != DataType.Function)
                    throw new ScriptRuntimeException("mod.on(event, function) expects a string and a function");
                string ev = a[0].String;
                if (!inst.Hooks.TryGetValue(ev, out var list)) inst.Hooks[ev] = list = new List<Closure>();
                list.Add(a[1].Function);
                return DynValue.Nil;
            });
            api["save"] = DynValue.NewCallback((ctx, a) =>
            {
                if (a.Count < 1 || a[0].Type != DataType.String)
                    throw new ScriptRuntimeException("mod.save(key, value) expects a string key");
                inst.Data[a[0].String] = JsonSerializer.SerializeToElement(LuaToClr(a.Count > 1 ? a[1] : DynValue.Nil));
                return DynValue.Nil;
            });
            api["load"] = DynValue.NewCallback((ctx, a) =>
            {
                if (a.Count < 1 || a[0].Type != DataType.String)
                    throw new ScriptRuntimeException("mod.load(key[, default]) expects a string key");
                if (inst.Data.TryGetValue(a[0].String, out JsonElement el)) return JsonToLua(s, el);
                return a.Count > 1 ? a[1] : DynValue.Nil;
            });
            // read a tuning value by dotted path, e.g. mod.config('Camera.FovDegrees', 60)
            api["config"] = DynValue.NewCallback((ctx, a) =>
            {
                DynValue def = a.Count > 1 ? a[1] : DynValue.Nil;
                if (Config == null || a.Count < 1 || a[0].Type != DataType.String) return def;
                JsonNode? node = JsonSerializer.SerializeToNode(Config);
                foreach (string seg in a[0].String.Split('.'))
                {
                    node = node is JsonObject o && o.TryGetPropertyValue(seg, out var child) ? child : null;
                    if (node == null) return def;
                }
                if (node is JsonValue v)
                {
                    if (v.TryGetValue(out double d)) return DynValue.NewNumber(d);
                    if (v.TryGetValue(out bool b)) return DynValue.NewBoolean(b);
                    if (v.TryGetValue(out string? str)) return DynValue.NewString(str);
                }
                return def;
            });
            // read an asset's text through the VFS (mod overrides apply). Returns nil if absent.
            api["asset"] = DynValue.NewCallback((ctx, a) =>
            {
                if (Assets == null || a.Count < 1 || a[0].Type != DataType.String) return DynValue.Nil;
                byte[]? bytes = Assets.ReadAllBytes(a[0].String);
                return bytes == null ? DynValue.Nil : DynValue.NewString(Encoding.UTF8.GetString(bytes));
            });
            // spawn a sim entity (a model at a position). Returns its id, or nil if no world.
            api["spawn"] = DynValue.NewCallback((ctx, a) =>
            {
                if (World == null || a.Count < 1 || a[0].Type != DataType.String) return DynValue.Nil;
                float Num(int i) => a.Count > i && a[i].Type == DataType.Number ? (float)a[i].Number : 0f;
                var e = World.Spawn(new KinematicEntity { ModelName = a[0].String, Position = new Vector3(Num(1), Num(2), Num(3)) });
                return DynValue.NewNumber(e.Id);
            });
            // the player's value for one of the mod's declared settings, e.g. mod.setting('damage') -> '2x'
            api["setting"] = DynValue.NewCallback((ctx, a) =>
            {
                if (a.Count < 1 || a[0].Type != DataType.String) return DynValue.Nil;
                string key = a[0].String;
                string? value = SettingLookup?.Invoke(inst.Manifest.Id, key);
                if (value == null)
                {
                    foreach (ModSetting setting in inst.Manifest.Settings)
                    {
                        if (setting.Id == key) value = setting.DefaultValue;
                    }
                }
                return value == null ? DynValue.Nil : DynValue.NewString(value);
            });
            s.Globals["mod"] = api;
        }

        private static DynValue[] ToLuaArgs(Script s, object[] args)
        {
            if (args == null || args.Length == 0) return Array.Empty<DynValue>();
            var r = new DynValue[args.Length];
            for (int i = 0; i < args.Length; i++) r[i] = DynValue.FromObject(s, args[i]);
            return r;
        }

        // ---- Lua <-> CLR/JSON conversion (numbers, strings, bools, and tables as object/array) ----

        private static object? LuaToClr(DynValue v)
        {
            switch (v.Type)
            {
                case DataType.Nil:
                case DataType.Void: return null;
                case DataType.Boolean: return v.Boolean;
                case DataType.Number: return v.Number;
                case DataType.String: return v.String;
                case DataType.Table:
                    Table t = v.Table;
                    bool isArray = t.Length > 0;
                    // treat as array only if keys are exactly 1..Length
                    if (isArray)
                        foreach (var pair in t.Pairs)
                            if (pair.Key.Type != DataType.Number) { isArray = false; break; }
                    if (isArray)
                    {
                        var list = new List<object?>();
                        for (int i = 1; i <= t.Length; i++) list.Add(LuaToClr(t.Get(i)));
                        return list;
                    }
                    var dict = new Dictionary<string, object?>();
                    foreach (var pair in t.Pairs) dict[pair.Key.ToPrintString()] = LuaToClr(pair.Value);
                    return dict;
                default: return v.ToPrintString();
            }
        }

        private static DynValue JsonToLua(Script s, JsonElement el)
        {
            switch (el.ValueKind)
            {
                case JsonValueKind.True: return DynValue.True;
                case JsonValueKind.False: return DynValue.False;
                case JsonValueKind.Number: return DynValue.NewNumber(el.GetDouble());
                case JsonValueKind.String: return DynValue.NewString(el.GetString());
                case JsonValueKind.Array:
                {
                    var t = new Table(s); int i = 1;
                    foreach (var item in el.EnumerateArray()) t.Set(i++, JsonToLua(s, item));
                    return DynValue.NewTable(t);
                }
                case JsonValueKind.Object:
                {
                    var t = new Table(s);
                    foreach (var prop in el.EnumerateObject()) t.Set(prop.Name, JsonToLua(s, prop.Value));
                    return DynValue.NewTable(t);
                }
                default: return DynValue.Nil;
            }
        }
    }
}
