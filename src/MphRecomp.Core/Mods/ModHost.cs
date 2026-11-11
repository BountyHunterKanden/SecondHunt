using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using MoonSharp.Interpreter;
using MphRead;
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
//
// Phase 1 (docs/MOD_API.md §4.10-4.15): manifest v2 (Kind, Api, Requires, Conflicts...), typed settings, a `require`
// that only reads the mod's own folder, and the cosmetic / gameplay split. A gameplay mod may change the match
// (mod.filter, mod.spawn) and so gets a deterministic `math` (DetMath) and its own random stream seeded from the
// replay header (ModSeed), never the game's Rng1/Rng2; a cosmetic mod that calls a gameplay API is refused at load.
namespace MphRecomp.Mods
{
    public sealed class ModManifest
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string Version { get; set; } = "0.0.0";
        public string? Description { get; set; }
        public string Entry { get; set; } = "main.lua"; // entry script, for folder mods
        // "cosmetic" (the default: watches the game, draws, plays sounds) or "gameplay" (may change the match: filters,
        // spawns; recorded in replays). docs/MOD_API.md §3.
        public string Kind { get; set; } = "cosmetic";
        // the mod API version the mod was written for (ModHost.ApiVersion); a newer one than this build's is refused
        public int Api { get; set; } = 1;
        public string? Author { get; set; }
        public string? GameVersion { get; set; }
        // which games the mod is for: "mph" (default when left out), "fh" (First Hunt: not supported yet)
        public List<string>? Games { get; set; }
        // other mods this one needs / can't run with: "id", or "id>=1.2.0" (also >, <=, <, =; several separated by spaces)
        public List<string> Requires { get; set; } = new();
        public List<string> Conflicts { get; set; } = new();
        public Dictionary<string, string>? Links { get; set; }
        // options the player can change from the MODS screens (before a game, and in the pause menu while playing);
        // the mod reads the current value with mod.setting(id)
        public List<ModSetting> Settings { get; set; } = new();
        // match modes the mod adds (gameplay mods; docs/MOD_API.md 4.2): each plays as its Base mode with the mod's rules.
        // A mod that declares any runs only in matches playing one of its modes
        public List<ModModeInfo> Modes { get; set; } = new();

        [JsonIgnore] public bool Gameplay => string.Equals(Kind, "gameplay", StringComparison.OrdinalIgnoreCase);

        // null = fine to load; else why this build can't run it
        public string? Validate()
        {
            if (string.IsNullOrWhiteSpace(Id)) return "mod.json has no Id";
            if (Id.IndexOfAny(new[] { '/', '\\', ':', '"' }) >= 0 || Id.Contains("..")) return $"bad Id '{Id}'";
            if (!string.Equals(Kind, "cosmetic", StringComparison.OrdinalIgnoreCase) && !Gameplay)
                return $"unknown Kind '{Kind}' (cosmetic or gameplay)";
            if (Api > ModHost.ApiVersion) return $"needs mod API {Api}; this game has {ModHost.ApiVersion} (update the game)";
            if (Games != null && Games.Count > 0 && !Games.Exists(g => string.Equals(g, "mph", StringComparison.OrdinalIgnoreCase)))
                return "First Hunt mods aren't supported yet";
            foreach (string r in Requires) if (ModVersion.ParseRef(r) == null) return $"bad Requires entry '{r}'";
            foreach (string c in Conflicts) if (ModVersion.ParseRef(c) == null) return $"bad Conflicts entry '{c}'";
            foreach (ModSetting s in Settings)
            {
                if (string.IsNullOrEmpty(s.Id)) return "a setting has no Id";
                if (s.Id.StartsWith('$')) return $"bad setting Id '{s.Id}'";
            }
            if (Modes.Count > 0 && !Gameplay) return "only gameplay mods can add modes (Kind \"gameplay\")";
            var modeIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (ModModeInfo m in Modes)
            {
                if (string.IsNullOrWhiteSpace(m.Id) || m.Id.IndexOfAny(new[] { ':', '"', '/', '\\' }) >= 0) return $"bad mode Id '{m.Id}'";
                if (!modeIds.Add(m.Id)) return $"two modes with the Id '{m.Id}'";
                if (m.BaseMode == null) return $"mode '{m.Id}': unknown Base '{m.Base}' ({string.Join(", ", ModModeInfo.Bases)})";
            }
            return null;
        }
    }

    // A match mode a gameplay mod adds (mod.json "Modes"), e.g.
    //   { "Id": "tag", "Name": "tag", "Base": "Battle", "Description": "...", "PointGoal": 10, "TimeLimit": 7 }
    // It plays as its Base mode (every vanilla rule, layer and bot behaviour of that mode) and the mod changes it with
    // mode.setup / mode.tick and the filters. PointGoal / TimeLimit (minutes) / TimeGoal (seconds): the menu's starting values.
    public sealed class ModModeInfo
    {
        public static readonly string[] Bases = { "Battle", "Bounty", "Capture", "Defender", "Nodes", "PrimeHunter", "Survival" };

        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string? Description { get; set; }
        public string Base { get; set; } = "Battle";
        public int? PointGoal { get; set; }
        public int? TimeLimit { get; set; }
        public int? TimeGoal { get; set; }

        // the solo GameMode it plays as, null = not one of Bases
        [JsonIgnore] public GameMode? BaseMode
        {
            get
            {
                string? b = Array.Find(Bases, x => string.Equals(x, Base?.Replace(" ", ""), StringComparison.OrdinalIgnoreCase));
                return b != null && Enum.TryParse(b, out GameMode g) ? g : null;
            }
        }
    }

    // Version strings and "id>=1.2.0" references (Requires / Conflicts). Up to three numeric parts compare as numbers;
    // anything after a '-' or '+' (1.2.0-beta) is ignored.
    public static class ModVersion
    {
        public static int Compare(string a, string b)
        {
            int[] x = Parts(a), y = Parts(b);
            for (int i = 0; i < 3; i++) if (x[i] != y[i]) return x[i].CompareTo(y[i]);
            return 0;
        }

        static int[] Parts(string v)
        {
            var r = new int[3];
            string core = v.Split('-', '+')[0].Trim().TrimStart('v', 'V');
            string[] p = core.Split('.');
            for (int i = 0; i < 3 && i < p.Length; i++) int.TryParse(p[i], NumberStyles.None, CultureInfo.InvariantCulture, out r[i]);
            return r;
        }

        // ("id", [(op, version)...]) or null when it doesn't parse
        public static (string Id, List<(string Op, string Version)> Constraints)? ParseRef(string text)
        {
            string t = text.Trim();
            int cut = t.IndexOfAny(new[] { '<', '>', '=', ' ', ',' });
            string id = cut < 0 ? t : t[..cut];
            if (id.Length == 0) return null;
            var list = new List<(string, string)>();
            if (cut >= 0)
            {
                foreach (string tok in t[cut..].Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    string op = tok.StartsWith(">=") || tok.StartsWith("<=") || tok.StartsWith("==") ? tok[..2]
                        : tok.StartsWith(">") || tok.StartsWith("<") || tok.StartsWith("=") ? tok[..1] : "";
                    string ver = tok[op.Length..];
                    if (op.Length == 0 || ver.Length == 0) return null;
                    list.Add((op == "==" ? "=" : op, ver));
                }
            }
            return (id, list);
        }

        public static bool Satisfies(string version, List<(string Op, string Version)> constraints)
        {
            foreach ((string op, string ver) in constraints)
            {
                int c = Compare(version, ver);
                bool ok = op switch { ">=" => c >= 0, ">" => c > 0, "<=" => c <= 0, "<" => c < 0, _ => c == 0 };
                if (!ok) return false;
            }
            return true;
        }

        public static string Describe(List<(string Op, string Version)> constraints)
            => string.Join(" ", constraints.ConvertAll(c => c.Op + c.Version));
    }

    // One player-facing option a mod declares in its mod.json, e.g.
    //   { "Id": "damage", "Name": "damage", "Values": ["1x", "2x", "4x"], "Default": "1x", "Description": "..." }
    //   { "Id": "loud", "Type": "bool", "Default": "true" }
    //   { "Id": "lives", "Type": "int", "Min": 1, "Max": 9, "Default": "3" }
    //   { "Id": "scale", "Type": "range", "Min": 0.5, "Max": 2, "Step": 0.25, "Default": "1" }
    // Values are kept as strings (recomp_settings.json ModSettings); mod.setting gives a bool / number / string.
    public sealed class ModSetting
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string? Description { get; set; }
        public string Type { get; set; } = "list"; // list, bool, int, range
        public List<string> Values { get; set; } = new();
        public string? Default { get; set; }
        public double? Min { get; set; }
        public double? Max { get; set; }
        public double? Step { get; set; }

        [JsonIgnore] public string Kind => (Type ?? "").ToLowerInvariant() switch { "bool" => "bool", "int" => "int", "range" => "range", _ => "list" };
        [JsonIgnore] public bool Usable => Kind != "list" || Values.Count > 0;
        double Lo => Min ?? 0;
        double Hi => Math.Max(Lo, Max ?? 100);
        double Inc => Step is double s && s > 0 ? s : 1;

        public string DefaultValue => Raw(Default) ?? Kind switch { "bool" => "false", "int" or "range" => Format(Lo), _ => Values.Count > 0 ? Values[0] : "" };

        // a stored value made valid for this setting (unknown / out of range -> the default, clamped, on its step)
        public string Normalize(string? value) => Raw(value) ?? DefaultValue;

        string? Raw(string? v)
        {
            if (v == null) return null;
            switch (Kind)
            {
                case "bool":
                    string b = v.Trim().ToLowerInvariant();
                    return b is "true" or "on" or "yes" or "1" ? "true" : b is "false" or "off" or "no" or "0" ? "false" : null;
                case "int":
                case "range":
                    if (!double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out double d) || !double.IsFinite(d)) return null;
                    return Format(Snap(d));
                default:
                    return Values.Contains(v) ? v : null;
            }
        }

        double Snap(double d)
        {
            d = Math.Clamp(d, Lo, Hi);
            if (Kind == "int") return Math.Clamp(Math.Round(Lo + Math.Round((d - Lo) / Math.Max(1, Math.Round(Inc))) * Math.Max(1, Math.Round(Inc))), Math.Ceiling(Lo), Math.Floor(Hi));
            return Math.Clamp(Lo + Math.Round((d - Lo) / Inc) * Inc, Lo, Hi);
        }

        static string Format(double d) => d.ToString("G12", CultureInfo.InvariantCulture);

        // the value one press left (dir < 0) or right from current: bool flips, numbers step and stop at the ends, lists wrap
        public string Next(string? current, int dir)
        {
            string cur = Normalize(current);
            switch (Kind)
            {
                case "bool": return cur == "true" ? "false" : "true";
                case "int":
                case "range":
                    double d = double.Parse(cur, CultureInfo.InvariantCulture);
                    double inc = Kind == "int" ? Math.Max(1, Math.Round(Inc)) : Inc;
                    return Format(Snap(d + Math.Sign(dir) * inc));
                default:
                    int i = Math.Max(0, Values.IndexOf(cur)), n = Values.Count;
                    return n == 0 ? "" : Values[((i + dir) % n + n) % n];
            }
        }

        // what the menu shows
        public string Display(string? value) => Kind == "bool" ? (Normalize(value) == "true" ? "ON" : "OFF") : Normalize(value);

        // what Lua gets: a boolean, a number, or the string
        public object Typed(string? value)
        {
            string v = Normalize(value);
            return Kind switch
            {
                "bool" => v == "true",
                "int" or "range" => double.Parse(v, CultureInfo.InvariantCulture),
                _ => v
            };
        }
    }

    public sealed class ModInstance
    {
        public ModManifest Manifest { get; }
        public Script Script { get; }
        public bool Enabled { get; internal set; } = true;
        public string? Error { get; internal set; } // set if load/run failed
        public bool Gameplay => Manifest.Gameplay;
        // mod.save changed something since the last flush (LiveMods.Poll writes mod_data.json)
        public bool Dirty { get; internal set; }
        internal string? Refused; // a cosmetic mod called a gameplay API: turned off after the call returns
        internal Func<string, string?>? ReadFile; // `require`: a file of the mod's own, by relative path (null = none)
        internal ulong Rng; // the mod's own random stream (splitmix64), seeded from ModHost.ModSeed and its id
        internal readonly Dictionary<string, List<Closure>> Hooks = new();
        internal readonly Dictionary<string, List<Closure>> Filters = new();
        internal readonly List<Closure> RoomLoads = new(); // room.on_load handlers (gameplay mods)
        internal readonly List<(int? Id, Message? Type, Closure Fn)> MessageWatches = new(); // mod.on_message
        internal readonly Dictionary<string, JsonElement> Data = new(); // persistent per-mod KV
        internal ModInstance(ModManifest m, Script s) { Manifest = m; Script = s; }
    }

    // something a room.on_load handler asked for (ModHost.RoomLoad); LiveMods turns it into the entity as the room loads.
    // Kind "pickup": Type = an ItemType name, Value = respawn seconds (0 = the item's usual); "enemy": Type = an
    // EnemyCatalogue id ("mph:Zoomer", "fh:Metroid", "mph:Hunter:Noxus"), Value = facing in degrees
    public readonly record struct RoomAdd(string Mod, string Kind, string Type, float X, float Y, float Z, float Value);

    // something entity.spawn_enemy / entity.remove asked for this frame (ModHost.TakeEntityOps); LiveMods carries it out
    // at the end of the frame, outside the entity loop. Op "spawn": Type = an EnemyCatalogue id, Facing in degrees;
    // "remove": Handle = an entity.enemies() handle
    public readonly record struct EntityOp(string Mod, string Op, string Type, int Handle, float X, float Y, float Z, float Facing);

    // a message msg.send asked for this frame (ModHost.TakeMessageOps); LiveMods sends it at the end of the frame. From/To
    // are entity ids (-1 = none: From defaults to the target), Delay in frames
    public readonly record struct MessageOp(string Mod, Message Type, int From, int To, int Param1, int Param2, int Delay);

    // what entity.get(handle) reads (Kind "enemy" for now)
    public readonly record struct ModEntityInfo(string Kind, string Type, float X, float Y, float Z, int Health, int MaxHealth);

    // the running scene's entities for the entity table (LiveMods); null = none (enemies() is empty, get gives nil)
    public interface IModEntities
    {
        // handles of the enemies alive now, in the scene's own order (a handle stays the same for an enemy's life)
        IReadOnlyList<int> Enemies();
        ModEntityInfo? Get(int handle);
    }

    public sealed class ModHost
    {
        // the mod API version this build provides (mod.json "Api")
        public const int ApiVersion = 1;
        // the most a mod may keep with mod.save (its whole slot, as JSON text)
        public const int MaxDataBytes = 1 << 20;
        // the most a mod may keep with mod.campaign.set in one campaign file (it is written at every ship save)
        public const int MaxCampaignBytes = 64 << 10;
        // mode.setup / mode.tick hooks (gameplay mods; LiveMods.MatchSetup, LiveGame.ModeTick); mod.on can't clash with these names
        public const string ModeSetupEvent = "@mode_setup", ModeTickEvent = "@mode_tick";

        private readonly List<ModInstance> _mods = new();
        private readonly Dictionary<string, ModInstance> _byId = new();
        private readonly Action<string, string> _log; // (modId, message)

        // Optional game context. When set (the GameSession wires these), the mod API gains
        // mod.config (read tuning), mod.asset (read an override-able asset), and mod.spawn (add a
        // sim entity). Left null in bare/headless use -> those calls no-op / return defaults.
        public GameConfig? Config { get => _config; set { _config = value; _configNode = null; } }
        // the player's own settings for cosmetic mods' mod.config('Settings.X') (LiveMods: RecompSettings, read-only);
        // gameplay mods never see it (it isn't in the replay)
        public object? SettingsView { get => _settingsView; set { _settingsView = value; _settingsNode = null; } }
        public AssetVfs? Assets { get; set; }
        // mod.asset's reader when Assets has no such file (LiveMods: ModContent.Read = the mounted mods' copies, then the
        // game's own file); null = none
        public Func<string, byte[]?>? ReadAsset { get; set; }
        public SimWorld? World { get; set; }
        // the running game for the score and match tables (LiveMods: LiveGame.Instance); null = none (they read 0 / nil)
        public IModGame? Game { get; set; }
        // the running scene's entities (LiveMods); null = none
        public IModEntities? Entities { get; set; }
        // called when a mod queues an entity op, so the host makes sure the end-of-frame flush runs
        public Action? EntityOpsQueued { get; set; }
        // (mod id, setting id) -> the player's current value for a setting the mod declares (null = its default)
        public Func<string, string, string?>? SettingLookup { get; set; }
        // (mod id, message) for a mod's errors only (load errors, hook errors, turned off): LiveMods shows them on screen
        public Action<string, string>? OnError { get; set; }
        // the seed of gameplay mods' random streams (math.random / mod.random); a replay records it. Set before loading.
        public ulong ModSeed { get; set; }
        // the loaded campaign file's mod.campaign values, mod id -> key -> value (LiveMods shares one dictionary between
        // its hosts and CampaignSaveFile.Mods); null = no campaign file (mod.campaign.get gives the default, set does nothing)
        public Dictionary<string, Dictionary<string, JsonElement>>? Campaign { get; set; }

        private GameConfig? _config;
        private object? _settingsView;
        private JsonNode? _configNode, _settingsNode;

        public IReadOnlyList<ModInstance> Mods => _mods;
        public ModInstance? Get(string id) => _byId.TryGetValue(id, out var m) ? m : null;

        public ModHost(Action<string, string>? log = null)
            => _log = log ?? ((_, _) => { });

        // Load a mod from Lua source. Sandboxed; on any load/parse/runtime error the mod is
        // disabled with Error set and the host keeps running. Returns the instance either way.
        // If `save` is given, the mod's persisted data is hydrated BEFORE its init runs, so
        // mod.load() during init sees values from previous sessions. `readFile` serves the mod's
        // `require` (a path relative to its folder -> the file's text, or null).
        public ModInstance LoadFromSource(ModManifest manifest, string luaSource, SaveData? save = null,
            Func<string, string?>? readFile = null)
        {
            if (string.IsNullOrEmpty(manifest.Id)) throw new ArgumentException("mod id required");
            if (_byId.ContainsKey(manifest.Id)) throw new InvalidOperationException($"duplicate mod id '{manifest.Id}'");

            var script = new Script(CoreModules.Preset_HardSandbox); // no io/os/loadfile from Lua
            var inst = new ModInstance(manifest, script) { ReadFile = readFile, Rng = ModSeed ^ Fnv(manifest.Id) };
            _mods.Add(inst);
            _byId[manifest.Id] = inst;
            if (manifest.Validate() is string invalid)
            {
                Disable(inst, invalid);
                return inst;
            }
            InstallApi(inst);
            if (save != null)
                foreach (var kv in SaveManager.GetModData(save, manifest.Id, new Dictionary<string, JsonElement>()))
                    inst.Data[kv.Key] = kv.Value;
            try
            {
                DynValue chunk = script.LoadString(luaSource, codeFriendlyName: manifest.Id);
                if (!RunGuarded(inst, chunk.Function, Array.Empty<DynValue>(), LoadInstructionLimit))
                    Disable(inst, $"script ran too long while loading (over {LoadInstructionLimit} steps)");
            }
            catch (InterpreterException ex) { Disable(inst, ex.DecoratedMessage ?? ex.Message); }
            catch (Exception ex) { Disable(inst, ex.Message); }
            if (inst.Refused != null && inst.Enabled) Disable(inst, inst.Refused);
            return inst;
        }

        // Runaway guard: every mod call runs as a coroutine that the interpreter forcibly yields after this many VM
        // steps. A mod that hits the limit (an endless loop, or far too much work in one hook) is turned off with an
        // error instead of freezing the game. Loading gets a bigger allowance than a hook call.
        public int HookInstructionLimit { get; set; } = 200_000;
        public int LoadInstructionLimit { get; set; } = 2_000_000;

        // false = the call was cut off by the limit
        private static bool RunGuarded(ModInstance inst, Closure fn, DynValue[] args, int limit)
            => RunGuarded(inst, fn, args, limit, out _);

        private static bool RunGuarded(ModInstance inst, Closure fn, DynValue[] args, int limit, out DynValue result)
        {
            DynValue co = inst.Script.CreateCoroutine(fn);
            co.Coroutine.AutoYieldCounter = limit;
            result = co.Coroutine.Resume(args);
            return result.Type != DataType.YieldRequest || !result.YieldRequest.Forced;
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
                    var e = new ModEntry { Manifest = manifest, Folder = folder };
                    LoadFromSource(manifest, File.ReadAllText(entry), readFile: e.ReadFile);
                }
                catch (Exception ex) { _log(Path.GetFileName(folder), "failed to load: " + ex.Message); }
            }
        }

        // Fire a named event to every enabled mod's registered hooks, newest-registered last. Each
        // hook call is isolated: a throwing hook is logged and skipped, others still run.
        public void Fire(string eventName, params object[] args) => FireWhere(eventName, null, args);

        // Fire to the mods `only` picks (e.g. setting_changed: cosmetic mods only, LiveMods.SettingChanged)
        public void FireWhere(string eventName, Func<ModInstance, bool>? only, params object[] args)
        {
            for (int i = 0; i < _mods.Count; i++)
            {
                ModInstance inst = _mods[i];
                if (!inst.Enabled || !inst.Hooks.TryGetValue(eventName, out var closures)) continue;
                if (only != null && !only(inst)) continue;
                DynValue[] luaArgs = ToLuaArgs(inst.Script, args);
                for (int j = 0; j < closures.Count && inst.Enabled; j++)
                {
                    try
                    {
                        if (!RunGuarded(inst, closures[j], luaArgs, HookInstructionLimit))
                            Disable(inst, $"hook '{eventName}' ran too long (over {HookInstructionLimit} steps)");
                    }
                    catch (InterpreterException ex) { HookError(inst, $"hook '{eventName}': {ex.DecoratedMessage ?? ex.Message}"); }
                    catch (Exception ex) { HookError(inst, $"hook '{eventName}': {ex.Message}"); }
                    if (inst.Refused != null && inst.Enabled) Disable(inst, inst.Refused);
                }
            }
        }

        // true when at least one enabled mod listens for this event, so a host can skip building the event's arguments
        public bool Wants(string eventName)
        {
            for (int i = 0; i < _mods.Count; i++)
            {
                if (_mods[i].Enabled && _mods[i].Hooks.ContainsKey(eventName)) return true;
            }
            return false;
        }

        // true when an enabled gameplay mod has a mod.filter for this name
        public bool WantsFilter(string name)
        {
            for (int i = 0; i < _mods.Count; i++)
            {
                if (_mods[i].Enabled && _mods[i].Gameplay && _mods[i].Filters.ContainsKey(name)) return true;
            }
            return false;
        }

        // every filter name some enabled gameplay mod hooks
        public HashSet<string> FilterNames()
        {
            var r = new HashSet<string>(StringComparer.Ordinal);
            foreach (ModInstance m in _mods) if (m.Enabled && m.Gameplay) r.UnionWith(m.Filters.Keys);
            return r;
        }

        // Run a value filter (docs/MOD_API.md §4.2): each enabled gameplay mod's filters for `name`, in load order, get a
        // table of `fields` and may change them; the changed values come back into `fields` (same CLR type: int, double,
        // bool or string; numbers for an int are rounded, nil / a wrong type keeps the old value). Keys the caller didn't
        // give are ignored. A filter that throws is logged and changes nothing. Returns whether any filter ran.
        public bool ApplyFilter(string name, Dictionary<string, object?> fields)
        {
            bool ran = false;
            for (int i = 0; i < _mods.Count; i++)
            {
                ModInstance inst = _mods[i];
                if (!inst.Enabled || !inst.Gameplay || !inst.Filters.TryGetValue(name, out var fns)) continue;
                for (int j = 0; j < fns.Count && inst.Enabled; j++)
                {
                    var t = new Table(inst.Script);
                    foreach (var kv in fields) t.Set(kv.Key, DynValue.FromObject(inst.Script, kv.Value));
                    try
                    {
                        if (!RunGuarded(inst, fns[j], new[] { DynValue.NewTable(t) }, HookInstructionLimit))
                        {
                            Disable(inst, $"filter '{name}' ran too long (over {HookInstructionLimit} steps)");
                            continue;
                        }
                    }
                    catch (InterpreterException ex) { HookError(inst, $"filter '{name}': {ex.DecoratedMessage ?? ex.Message}"); continue; }
                    catch (Exception ex) { HookError(inst, $"filter '{name}': {ex.Message}"); continue; }
                    ran = true;
                    foreach (string key in new List<string>(fields.Keys))
                    {
                        DynValue v = t.Get(key);
                        switch (fields[key])
                        {
                            case int old when v.Type == DataType.Number && double.IsFinite(v.Number):
                                fields[key] = (int)Math.Clamp(Math.Round(v.Number, MidpointRounding.AwayFromZero), int.MinValue, int.MaxValue);
                                break;
                            case double when v.Type == DataType.Number:
                                fields[key] = v.Number;
                                break;
                            case float when v.Type == DataType.Number:
                                fields[key] = (float)v.Number;
                                break;
                            case bool when v.Type != DataType.Nil:
                                fields[key] = v.CastToBool();
                                break;
                            case string when v.Type == DataType.String:
                                fields[key] = v.String;
                                break;
                        }
                    }
                }
            }
            return ran;
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
            {
                SaveManager.SetModData(save, inst.Manifest.Id, inst.Data);
                inst.Dirty = false;
            }
        }

        // ---- internals ----

        private void Disable(ModInstance inst, string error)
        {
            inst.Enabled = false;
            inst.Error = error;
            _log(inst.Manifest.Id, "disabled: " + error);
            OnError?.Invoke(inst.Manifest.Id, "turned off: " + error);
        }

        private void HookError(ModInstance inst, string error)
        {
            _log(inst.Manifest.Id, error);
            OnError?.Invoke(inst.Manifest.Id, error);
        }

        // a gameplay-only API: a cosmetic mod's call fails, and the mod is turned off once it returns (even if it
        // caught the error with pcall)
        private static void RequireGameplay(ModInstance inst, string api)
        {
            if (inst.Gameplay) return;
            inst.Refused ??= $"{api} needs \"Kind\": \"gameplay\" in mod.json";
            throw new ScriptRuntimeException(inst.Refused);
        }

        static ulong Fnv(string s)
        {
            ulong h = 14695981039346656037UL;
            foreach (char c in s) { h ^= c; h *= 1099511628211UL; }
            return h;
        }

        // splitmix64: the mod's next random number in [0, 1)
        static double NextRandom(ModInstance inst)
        {
            ulong z = inst.Rng += 0x9E3779B97F4A7C15UL;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            z ^= z >> 31;
            return (z >> 11) * (1.0 / (1UL << 53));
        }

        // math.random's three forms on the mod's own stream: () -> [0,1), (m) -> 1..m, (m, n) -> m..n
        static DynValue Random(ModInstance inst, CallbackArguments a, string fname)
        {
            double r = NextRandom(inst);
            if (a.Count == 0 || a[0].IsNil()) return DynValue.NewNumber(r);
            double lo = 1, hi = Num(a, 0, fname);
            if (a.Count > 1 && !a[1].IsNil()) { lo = hi; hi = Num(a, 1, fname); }
            lo = Math.Floor(lo); hi = Math.Floor(hi);
            if (lo > hi) throw new ScriptRuntimeException($"bad argument to '{fname}' (interval is empty)");
            return DynValue.NewNumber(lo + Math.Floor(r * (hi - lo + 1)));
        }

        static double Num(CallbackArguments a, int i, string fname)
        {
            double? d = a.Count > i ? a[i].CastToNumber() : null;
            if (d == null) throw new ScriptRuntimeException($"bad argument #{i + 1} to '{fname}' (number expected)");
            return d.Value;
        }

        // A gameplay mod's `math`: every function whose result could differ between devices goes through DetMath (the
        // simulation's own bit-exact trig), and random is the mod's seeded stream. (sqrt, floor, abs... are exact
        // already. Lua's `^` operator still uses the platform's pow: gameplay mods use math.pow.)
        private static void InstallGameplayMath(ModInstance inst)
        {
            Script s = inst.Script;
            if (s.Globals.Get("math").Table is not Table math) return;
            void F(string name, Func<double, double> f)
                => math[name] = DynValue.NewCallback((ctx, a) => DynValue.NewNumber(f(Num(a, 0, name))));
            F("sin", DetMath.Sin);
            F("cos", DetMath.Cos);
            F("tan", DetMath.Tan);
            F("asin", DetMath.Asin);
            F("acos", DetMath.Acos);
            F("exp", DetMath.Exp);
            F("sinh", x => (DetMath.Exp(x) - DetMath.Exp(-x)) / 2);
            F("cosh", x => (DetMath.Exp(x) + DetMath.Exp(-x)) / 2);
            F("tanh", x => { double e = DetMath.Exp(2 * x); return double.IsInfinity(e) ? 1 : (e - 1) / (e + 1); });
            F("log10", x => DetMath.Log(x) / DetMath.Log(10));
            math["atan"] = DynValue.NewCallback((ctx, a) => DynValue.NewNumber(a.Count > 1 && !a[1].IsNil()
                ? DetMath.Atan2(Num(a, 0, "atan"), Num(a, 1, "atan")) : DetMath.Atan(Num(a, 0, "atan"))));
            math["atan2"] = DynValue.NewCallback((ctx, a) => DynValue.NewNumber(DetMath.Atan2(Num(a, 0, "atan2"), Num(a, 1, "atan2"))));
            math["log"] = DynValue.NewCallback((ctx, a) => DynValue.NewNumber(a.Count > 1 && !a[1].IsNil()
                ? DetMath.Log(Num(a, 0, "log")) / DetMath.Log(Num(a, 1, "log")) : DetMath.Log(Num(a, 0, "log"))));
            math["pow"] = DynValue.NewCallback((ctx, a) => DynValue.NewNumber(DetMath.Pow(Num(a, 0, "pow"), Num(a, 1, "pow"))));
            math["random"] = DynValue.NewCallback((ctx, a) => Random(inst, a, "random"));
            math["randomseed"] = DynValue.NewCallback((ctx, a) =>
            {
                double seed = Num(a, 0, "randomseed");
                inst.Rng = BitConverter.DoubleToUInt64Bits(seed) ^ Fnv(inst.Manifest.Id);
                return DynValue.Nil;
            });
        }

        // `require("a.b")` loads a.b.lua, then a/b.lua... from the mod's own folder only, once (later calls get the
        // same value back). The Lua side keeps the cache; the host only reads and compiles the file.
        private const string RequireLua = @"
return function(load_module)
  local loaded, loading = {}, {}
  function require(name)
    if type(name) ~= 'string' then error('require(name) expects a string', 2) end
    local v = loaded[name]
    if v ~= nil then return v end
    if loading[name] then error(""require loop at '"" .. name .. ""'"", 2) end
    loading[name] = true
    local fn = load_module(name)
    local r = fn(name)
    loading[name] = nil
    if r == nil then r = true end
    loaded[name] = r
    return r
  end
end";

        private static void InstallRequire(ModInstance inst)
        {
            Script s = inst.Script;
            DynValue loader = DynValue.NewCallback((ctx, a) =>
            {
                string name = a.Count > 0 && a[0].Type == DataType.String ? a[0].String : "";
                if (name.Length == 0 || name.Contains("..") || name.IndexOfAny(new[] { '\\', ':', '/' }) >= 0 || name.StartsWith('.') || name.EndsWith('.'))
                    throw new ScriptRuntimeException($"require: bad module name '{name}' (use dots: require('lib.util') loads lib/util.lua)");
                string rel = name.Replace('.', '/') + ".lua";
                string? text = inst.ReadFile?.Invoke(rel);
                if (text == null) throw new ScriptRuntimeException($"require: module '{name}' not found ({rel} in the mod's folder)");
                return s.LoadString(text, null, inst.Manifest.Id + "/" + rel);
            });
            s.Call(s.DoString(RequireLua), loader);
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
                ["version"] = inst.Manifest.Version,
                ["kind"] = inst.Gameplay ? "gameplay" : "cosmetic",
                ["api"] = ApiVersion
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
                if (ev.StartsWith('@')) throw new ScriptRuntimeException($"mod.on: no event '{ev}' (mode.setup / mode.tick register those)");
                if (!inst.Hooks.TryGetValue(ev, out var list)) inst.Hooks[ev] = list = new List<Closure>();
                list.Add(a[1].Function);
                return DynValue.Nil;
            });
            // a value filter (gameplay mods): fn(t) changes t's fields, e.g. mod.filter('damage', function(t) t.damage = t.damage * 2 end)
            api["filter"] = DynValue.NewCallback((ctx, a) =>
            {
                RequireGameplay(inst, "mod.filter");
                if (a.Count < 2 || a[0].Type != DataType.String || a[1].Type != DataType.Function)
                    throw new ScriptRuntimeException("mod.filter(name, function) expects a string and a function");
                string name = a[0].String;
                if (Array.IndexOf(MphRead.GameFilters.Names, name) < 0)
                    throw new ScriptRuntimeException($"mod.filter: no filter named '{name}' (there are: {string.Join(", ", MphRead.GameFilters.Names)})");
                if (!inst.Filters.TryGetValue(name, out var list)) inst.Filters[name] = list = new List<Closure>();
                list.Add(a[1].Function);
                return DynValue.Nil;
            });
            api["save"] = DynValue.NewCallback((ctx, a) =>
            {
                if (a.Count < 1 || a[0].Type != DataType.String)
                    throw new ScriptRuntimeException("mod.save(key, value) expects a string key");
                string key = a[0].String;
                JsonElement el = JsonSerializer.SerializeToElement(LuaToClr(a.Count > 1 ? a[1] : DynValue.Nil));
                bool had = inst.Data.TryGetValue(key, out JsonElement old);
                inst.Data[key] = el;
                long size = 0;
                foreach (var kv in inst.Data) size += kv.Key.Length + kv.Value.GetRawText().Length + 4;
                if (size > MaxDataBytes)
                {
                    if (had) inst.Data[key] = old; else inst.Data.Remove(key);
                    throw new ScriptRuntimeException($"mod.save: over the {MaxDataBytes / 1024} KB a mod may keep");
                }
                inst.Dirty = true;
                return DynValue.Nil;
            });
            api["load"] = DynValue.NewCallback((ctx, a) =>
            {
                if (a.Count < 1 || a[0].Type != DataType.String)
                    throw new ScriptRuntimeException("mod.load(key[, default]) expects a string key");
                if (inst.Data.TryGetValue(a[0].String, out JsonElement el)) return JsonToLua(s, el);
                return a.Count > 1 ? a[1] : DynValue.Nil;
            });
            // per-campaign-file values: kept in campaign<slot>.json when the game saves at the ship, so loading an older
            // save brings back that save's values. get: any mod; set: gameplay mods (it changes what a save holds)
            var campaign = new Table(s);
            campaign["get"] = DynValue.NewCallback((ctx, a) =>
            {
                if (a.Count < 1 || a[0].Type != DataType.String)
                    throw new ScriptRuntimeException("mod.campaign.get(key[, default]) expects a string key");
                if (Campaign != null && Campaign.TryGetValue(inst.Manifest.Id, out var values)
                    && values.TryGetValue(a[0].String, out JsonElement el)) return JsonToLua(s, el);
                return a.Count > 1 ? a[1] : DynValue.Nil;
            });
            campaign["set"] = DynValue.NewCallback((ctx, a) =>
            {
                RequireGameplay(inst, "mod.campaign.set");
                if (a.Count < 1 || a[0].Type != DataType.String)
                    throw new ScriptRuntimeException("mod.campaign.set(key, value) expects a string key");
                if (Campaign == null) return DynValue.False;
                string key = a[0].String;
                JsonElement el = JsonSerializer.SerializeToElement(LuaToClr(a.Count > 1 ? a[1] : DynValue.Nil));
                if (!Campaign.TryGetValue(inst.Manifest.Id, out var values)) Campaign[inst.Manifest.Id] = values = new();
                bool had = values.TryGetValue(key, out JsonElement old);
                values[key] = el;
                long size = 0;
                foreach (var kv in values) size += kv.Key.Length + kv.Value.GetRawText().Length + 4;
                if (size > MaxCampaignBytes)
                {
                    if (had) values[key] = old; else values.Remove(key);
                    throw new ScriptRuntimeException($"mod.campaign.set: over the {MaxCampaignBytes / 1024} KB a mod may keep in a save");
                }
                return DynValue.True;
            });
            api["campaign"] = DynValue.NewTable(campaign);
            // read a tuning value by dotted path, e.g. mod.config('Camera.FovDegrees', 60); cosmetic mods also read the
            // player's settings, mod.config('Settings.MusicVolume')
            api["config"] = DynValue.NewCallback((ctx, a) =>
            {
                DynValue def = a.Count > 1 ? a[1] : DynValue.Nil;
                if (a.Count < 1 || a[0].Type != DataType.String) return def;
                string[] path = a[0].String.Split('.');
                JsonNode? node;
                int from = 0;
                if (path[0] == "Settings" && !inst.Gameplay && SettingsView != null)
                {
                    node = _settingsNode ??= JsonSerializer.SerializeToNode(SettingsView, SettingsView.GetType());
                    from = 1;
                }
                else
                {
                    if (Config == null) return def;
                    node = _configNode ??= JsonSerializer.SerializeToNode(Config);
                }
                for (int i = from; i < path.Length; i++)
                {
                    node = node is JsonObject o && o.TryGetPropertyValue(path[i], out var child) ? child : null;
                    if (node == null) return def;
                }
                if (node is JsonValue v)
                {
                    if (v.TryGetValue(out double d)) return DynValue.NewNumber(d);
                    if (v.TryGetValue(out bool b)) return DynValue.NewBoolean(b);
                    if (v.TryGetValue(out string? str)) return DynValue.NewString(str);
                    if (v.GetValueKind() == JsonValueKind.Number) return DynValue.NewNumber(v.GetValue<JsonElement>().GetDouble());
                }
                return def;
            });
            // read an asset's text through the VFS (mod overrides apply), else ReadAsset (a game file, with mods' files/
            // copies), else a file of the mod's own. Returns nil if absent.
            api["asset"] = DynValue.NewCallback((ctx, a) =>
            {
                if (a.Count < 1 || a[0].Type != DataType.String) return DynValue.Nil;
                string rel = a[0].String;
                byte[]? bytes = Assets?.ReadAllBytes(rel);
                if (bytes == null && ReadAsset != null)
                {
                    try { bytes = ReadAsset(rel); } catch (Exception) { bytes = null; }
                }
                if (bytes != null) return DynValue.NewString(Encoding.UTF8.GetString(bytes));
                string? own = inst.ReadFile?.Invoke(rel);
                return own == null ? DynValue.Nil : DynValue.NewString(own);
            });
            // spawn a sim entity (a model at a position; gameplay mods). Returns its id, or nil if no world.
            api["spawn"] = DynValue.NewCallback((ctx, a) =>
            {
                RequireGameplay(inst, "mod.spawn");
                if (World == null || a.Count < 1 || a[0].Type != DataType.String) return DynValue.Nil;
                float Num(int i) => a.Count > i && a[i].Type == DataType.Number ? (float)a[i].Number : 0f;
                var e = World.Spawn(new KinematicEntity { ModelName = a[0].String, Position = new Vector3(Num(1), Num(2), Num(3)) });
                return DynValue.NewNumber(e.Id);
            });
            // the player's value for one of the mod's declared settings: a boolean (bool), number (int / range) or
            // string (list), e.g. mod.setting('damage') -> '2x'
            api["setting"] = DynValue.NewCallback((ctx, a) =>
            {
                if (a.Count < 1 || a[0].Type != DataType.String) return DynValue.Nil;
                string key = a[0].String;
                string? value = SettingLookup?.Invoke(inst.Manifest.Id, key);
                ModSetting? decl = inst.Manifest.Settings.Find(x => x.Id == key);
                if (decl != null) return DynValue.FromObject(s, decl.Typed(value));
                return value == null ? DynValue.Nil : DynValue.NewString(value);
            });
            // a random number from the mod's own stream (the same forms as math.random); a gameplay mod's is recorded
            // with the replay, so it plays back the same
            api["random"] = DynValue.NewCallback((ctx, a) => Random(inst, a, "random"));
            s.Globals["mod"] = api;
            InstallGame(inst);
            InstallRoom(inst);
            InstallEntity(inst);
            InstallMessages(inst, api);
            InstallRequire(inst);
            if (inst.Gameplay) InstallGameplayMath(inst);
        }

        // the room table (docs/MOD_API.md §4.4): room.on_load(fn) is gameplay mods only; fn(r) runs as each room loads
        // (RoomLoad), r = {id, name, add_pickup(item, x, y, z, respawn?), add_enemy(id, x, y, z, facing?)}
        private void InstallRoom(ModInstance inst)
        {
            var room = new Table(inst.Script);
            room["on_load"] = DynValue.NewCallback((ctx, a) =>
            {
                RequireGameplay(inst, "room.on_load");
                if (a.Count < 1 || a[0].Type != DataType.Function)
                    throw new ScriptRuntimeException("room.on_load(function) expects a function");
                inst.RoomLoads.Add(a[0].Function);
                return DynValue.Nil;
            });
            inst.Script.Globals["room"] = room;
        }

        // the entity table (docs/MOD_API.md §4.4): any mod reads (enemies, get); spawn_enemy and remove are gameplay mods
        // only and are queued, carried out at the end of the frame (TakeEntityOps)
        public const int MaxEntityOpsPerFrame = 16;
        private readonly List<(ModInstance Inst, EntityOp Op)> _entityOps = new();

        private void InstallEntity(ModInstance inst)
        {
            Script s = inst.Script;
            float Num(CallbackArguments a, int at, string fn, float or)
            {
                if (a.Count <= at || a[at].Type == DataType.Nil) return or;
                if (a[at].Type != DataType.Number || !double.IsFinite(a[at].Number))
                    throw new ScriptRuntimeException($"{fn}: argument {at + 1} should be a number");
                return (float)a[at].Number;
            }
            void Queue(EntityOp op, string fn)
            {
                int mine = 0;
                foreach (var q in _entityOps) if (q.Inst == inst) mine++;
                if (mine >= MaxEntityOpsPerFrame)
                    throw new ScriptRuntimeException($"{fn}: a mod can ask for at most {MaxEntityOpsPerFrame} spawns/removes a frame");
                _entityOps.Add((inst, op));
                EntityOpsQueued?.Invoke();
            }
            var entity = new Table(s);
            entity["spawn_enemy"] = DynValue.NewCallback((ctx, a) =>
            {
                RequireGameplay(inst, "entity.spawn_enemy");
                if (a.Count < 4 || a[0].Type != DataType.String)
                    throw new ScriptRuntimeException("entity.spawn_enemy(id, x, y, z, facing?) expects an enemy id and a position");
                Queue(new EntityOp(inst.Manifest.Id, "spawn", a[0].String, -1, Num(a, 1, "entity.spawn_enemy", 0),
                    Num(a, 2, "entity.spawn_enemy", 0), Num(a, 3, "entity.spawn_enemy", 0), Num(a, 4, "entity.spawn_enemy", 0)),
                    "entity.spawn_enemy");
                return DynValue.Nil;
            });
            entity["remove"] = DynValue.NewCallback((ctx, a) =>
            {
                RequireGameplay(inst, "entity.remove");
                if (a.Count < 1 || a[0].Type != DataType.Number)
                    throw new ScriptRuntimeException("entity.remove(handle) expects a handle from entity.enemies()");
                Queue(new EntityOp(inst.Manifest.Id, "remove", "", (int)a[0].Number, 0, 0, 0, 0), "entity.remove");
                return DynValue.Nil;
            });
            entity["enemies"] = DynValue.NewCallback((ctx, a) =>
            {
                var t = new Table(s);
                IReadOnlyList<int>? list = Entities?.Enemies();
                if (list != null) for (int i = 0; i < list.Count; i++) t[i + 1] = list[i];
                return DynValue.NewTable(t);
            });
            entity["get"] = DynValue.NewCallback((ctx, a) =>
            {
                if (a.Count < 1 || a[0].Type != DataType.Number || Entities?.Get((int)a[0].Number) is not ModEntityInfo e)
                    return DynValue.Nil;
                return DynValue.NewTable(new Table(s)
                {
                    ["kind"] = e.Kind, ["type"] = e.Type, ["x"] = e.X, ["y"] = e.Y, ["z"] = e.Z,
                    ["health"] = e.Health, ["max_health"] = e.MaxHealth,
                });
            });
            inst.Script.Globals["entity"] = entity;
        }

        // the entity ops queued since the last call, in order, from mods still turned on; clears the queue
        public List<EntityOp> TakeEntityOps()
        {
            var ops = new List<EntityOp>();
            foreach (var q in _entityOps) if (q.Inst.Enabled) ops.Add(q.Op);
            _entityOps.Clear();
            return ops;
        }

        // the level-scripting messages entities pass each other (doors, triggers, platforms, spawners...): any mod watches
        // (mod.on_message, FireMessage), gameplay mods send (msg.send), queued like entity ops and sent at the end of the frame
        public const int MaxMessagesPerFrame = 16;
        public const int MaxMessageDelay = 600; // frames (10 s)
        // what msg.send may send: the scripting messages whose params are plain numbers. The rest carry engine objects
        // (collisions, damage) or drive bosses, cutscenes and the escape sequence
        public static readonly IReadOnlySet<Message> Sendable = new HashSet<Message>
        {
            Message.SetActive, Message.Trigger, Message.Unlock, Message.Lock, Message.Activate, Message.Complete,
            Message.UnlockConnectors, Message.LockConnectors, Message.SetTriggerState, Message.ClearTriggerState,
            Message.PlatformWakeup, Message.PlatformSleep,
        };
        const int TriggerStateBits = 32; // StorySave.TriggerState is 4 bytes
        private readonly List<(ModInstance Inst, MessageOp Op)> _messageOps = new();

        static bool TryMessage(DynValue v, out Message m)
        {
            m = Message.None;
            return v.Type == DataType.String && v.String.Length > 0 && char.IsLetter(v.String[0])
                && Enum.TryParse(v.String, false, out m) && Enum.IsDefined(m) && m != Message.None;
        }

        private void InstallMessages(ModInstance inst, Table api)
        {
            api["on_message"] = DynValue.NewCallback((ctx, a) =>
            {
                if (a.Count < 2 || a[1].Type != DataType.Function)
                    throw new ScriptRuntimeException("mod.on_message(entity id or message name, function) expects an id or name and a function");
                if (a[0].Type == DataType.Number && a[0].Number >= 0 && a[0].Number <= int.MaxValue && a[0].Number == Math.Floor(a[0].Number))
                    inst.MessageWatches.Add(((int)a[0].Number, null, a[1].Function));
                else if (TryMessage(a[0], out Message m))
                    inst.MessageWatches.Add((null, m, a[1].Function));
                else
                    throw new ScriptRuntimeException($"mod.on_message: '{a[0].ToPrintString()}' is not an entity id or a message name (like 'Unlock')");
                return DynValue.Nil;
            });
            var msg = new Table(inst.Script);
            msg["send"] = DynValue.NewCallback((ctx, a) =>
            {
                RequireGameplay(inst, "msg.send");
                if (a.Count < 1 || !TryMessage(a[0], out Message m))
                    throw new ScriptRuntimeException("msg.send(message, from, to, p1?, p2?, delay?) expects a message name (like 'Unlock')");
                if (!Sendable.Contains(m))
                    throw new ScriptRuntimeException($"msg.send: a mod can't send '{m}'");
                int Int(int at, string what, int or, int min, int max)
                {
                    if (a.Count <= at || a[at].Type == DataType.Nil) return or;
                    double d = a[at].Type == DataType.Number ? a[at].Number : double.NaN;
                    if (!(d >= min && d <= max && d == Math.Floor(d)))
                        throw new ScriptRuntimeException($"msg.send: {what} should be a whole number from {min} to {max}");
                    return (int)d;
                }
                int from = Int(1, "from (an entity id or nil)", -1, -1, int.MaxValue);
                int to = Int(2, "to (an entity id)", -1, -1, int.MaxValue);
                bool state = m is Message.SetTriggerState or Message.ClearTriggerState;
                int p1 = state ? Int(3, "the trigger state bit", -1, 0, TriggerStateBits - 1) : Int(3, "p1", 0, int.MinValue, int.MaxValue);
                if (state && p1 < 0) throw new ScriptRuntimeException($"msg.send: {m} needs the bit (0-{TriggerStateBits - 1}) as p1");
                if (!state && to < 0) throw new ScriptRuntimeException($"msg.send: {m} needs a target entity id");
                int p2 = Int(4, "p2", 0, int.MinValue, int.MaxValue);
                int delay = Int(5, "delay (frames)", 0, 0, MaxMessageDelay);
                int mine = 0;
                foreach (var q in _messageOps) if (q.Inst == inst) mine++;
                if (mine >= MaxMessagesPerFrame)
                    throw new ScriptRuntimeException($"msg.send: a mod can send at most {MaxMessagesPerFrame} messages a frame");
                _messageOps.Add((inst, new MessageOp(inst.Manifest.Id, m, from, to, p1, p2, delay)));
                EntityOpsQueued?.Invoke();
                return DynValue.Nil;
            });
            inst.Script.Globals["msg"] = msg;
        }

        // true when an enabled mod watches messages (LiveMods sets GameEvents.Messages only then)
        public bool WantsMessages()
        {
            for (int i = 0; i < _mods.Count; i++)
            {
                if (_mods[i].Enabled && _mods[i].MessageWatches.Count > 0) return true;
            }
            return false;
        }

        // a message is being delivered: run the mod.on_message handlers watching its type, its sender or its target, as
        // fn(name, from, to, p1, p2) (a param that isn't a number reads nil). Guarded like any hook
        public void FireMessage(Message m, int from, int to, object param1, object param2)
        {
            for (int i = 0; i < _mods.Count; i++)
            {
                ModInstance inst = _mods[i];
                if (!inst.Enabled || inst.MessageWatches.Count == 0) continue;
                DynValue[]? args = null;
                for (int j = 0; j < inst.MessageWatches.Count && inst.Enabled; j++)
                {
                    var w = inst.MessageWatches[j];
                    if (w.Type is Message t ? t != m : w.Id != from && w.Id != to) continue;
                    args ??= new[]
                    {
                        DynValue.NewString(m.ToString()), DynValue.NewNumber(from), DynValue.NewNumber(to),
                        param1 is int p1 ? DynValue.NewNumber(p1) : DynValue.Nil, param2 is int p2 ? DynValue.NewNumber(p2) : DynValue.Nil,
                    };
                    try
                    {
                        if (!RunGuarded(inst, w.Fn, args, HookInstructionLimit))
                            Disable(inst, $"mod.on_message ran too long (over {HookInstructionLimit} steps)");
                    }
                    catch (InterpreterException ex) { HookError(inst, $"mod.on_message: {ex.DecoratedMessage ?? ex.Message}"); }
                    catch (Exception ex) { HookError(inst, $"mod.on_message: {ex.Message}"); }
                    if (inst.Refused != null && inst.Enabled) Disable(inst, inst.Refused);
                }
            }
        }

        // the messages msg.send queued since the last call, in order, from mods still turned on; clears the queue
        public List<MessageOp> TakeMessageOps()
        {
            var ops = new List<MessageOp>();
            foreach (var q in _messageOps) if (q.Inst.Enabled) ops.Add(q.Op);
            _messageOps.Clear();
            return ops;
        }

        // the most one mod may add to one room (each pickup or enemy is an entity the room keeps)
        public const int MaxRoomAdds = 64;

        // true when an enabled gameplay mod has a room.on_load handler
        public bool WantsRoomLoad()
        {
            for (int i = 0; i < _mods.Count; i++)
            {
                if (_mods[i].Enabled && _mods[i].Gameplay && _mods[i].RoomLoads.Count > 0) return true;
            }
            return false;
        }

        // Run every room.on_load handler for a room that's loading, in load order; returns what they asked to add.
        // A handler that throws or runs too long adds nothing (its mod is logged / turned off); a bad argument throws
        // inside the handler (so the mod sees the error). The host checks the item / enemy names (LiveMods).
        public List<RoomAdd> RoomLoad(int roomId, string roomName)
        {
            var adds = new List<RoomAdd>();
            for (int i = 0; i < _mods.Count; i++)
            {
                ModInstance inst = _mods[i];
                if (!inst.Enabled || !inst.Gameplay || inst.RoomLoads.Count == 0) continue;
                Script s = inst.Script;
                var mine = new List<RoomAdd>();
                float Num(CallbackArguments a, int at, string fn, float or)
                {
                    if (a.Count <= at || a[at].Type == DataType.Nil) return or;
                    if (a[at].Type != DataType.Number || !double.IsFinite(a[at].Number))
                        throw new ScriptRuntimeException($"{fn}: argument {at + 1} should be a number");
                    return (float)a[at].Number;
                }
                void Add(CallbackArguments a, string fn, string kind, float valueOr)
                {
                    if (a.Count < 4 || a[0].Type != DataType.String)
                        throw new ScriptRuntimeException($"{fn}(name, x, y, z, ...) expects a name and a position");
                    if (mine.Count >= MaxRoomAdds)
                        throw new ScriptRuntimeException($"{fn}: a mod can add at most {MaxRoomAdds} things to a room");
                    mine.Add(new RoomAdd(inst.Manifest.Id, kind, a[0].String, Num(a, 1, fn, 0), Num(a, 2, fn, 0), Num(a, 3, fn, 0),
                        Num(a, 4, fn, valueOr)));
                }
                var r = new Table(s)
                {
                    ["id"] = roomId,
                    ["name"] = roomName,
                    ["add_pickup"] = DynValue.NewCallback((ctx, a) => { Add(a, "r.add_pickup", "pickup", 0); return DynValue.Nil; }),
                    ["add_enemy"] = DynValue.NewCallback((ctx, a) => { Add(a, "r.add_enemy", "enemy", 0); return DynValue.Nil; }),
                };
                var args = new[] { DynValue.NewTable(r) };
                for (int j = 0; j < inst.RoomLoads.Count && inst.Enabled; j++)
                {
                    int before = mine.Count;
                    try
                    {
                        if (!RunGuarded(inst, inst.RoomLoads[j], args, HookInstructionLimit))
                        {
                            Disable(inst, $"room.on_load ran too long (over {HookInstructionLimit} steps)");
                            mine.RemoveRange(before, mine.Count - before);
                        }
                    }
                    catch (InterpreterException ex) { HookError(inst, $"room.on_load: {ex.DecoratedMessage ?? ex.Message}"); mine.RemoveRange(before, mine.Count - before); }
                    catch (Exception ex) { HookError(inst, $"room.on_load: {ex.Message}"); mine.RemoveRange(before, mine.Count - before); }
                }
                if (inst.Enabled) adds.AddRange(mine);
            }
            return adds;
        }

        // the score and match tables (docs/MOD_API.md §4.4): any mod reads, gameplay mods change
        private void InstallGame(ModInstance inst)
        {
            Script s = inst.Script;
            int Slot(CallbackArguments a, string fn)
            {
                if (a.Count < 1 || a[0].Type != DataType.Number)
                    throw new ScriptRuntimeException($"{fn}(slot, ...) expects a slot number (0-{(Game?.Slots ?? 8) - 1})");
                return (int)a[0].Number;
            }
            int Value(CallbackArguments a, string fn)
            {
                if (a.Count < 2 || a[1].Type != DataType.Number)
                    throw new ScriptRuntimeException($"{fn}(slot, n) expects a number");
                return (int)Math.Round(a[1].Number);
            }
            var score = new Table(s);
            score["get"] = DynValue.NewCallback((ctx, a) => DynValue.NewNumber(Game?.Points(Slot(a, "score.get")) ?? 0));
            score["set"] = DynValue.NewCallback((ctx, a) =>
            {
                RequireGameplay(inst, "score.set");
                Game?.SetPoints(Slot(a, "score.set"), Value(a, "score.set"));
                return DynValue.Nil;
            });
            score["add"] = DynValue.NewCallback((ctx, a) =>
            {
                RequireGameplay(inst, "score.add");
                int slot = Slot(a, "score.add");
                if (Game != null) Game.SetPoints(slot, Game.Points(slot) + Value(a, "score.add"));
                return DynValue.Nil;
            });
            score["team_get"] = DynValue.NewCallback((ctx, a) => DynValue.NewNumber(Game?.TeamPoints(Slot(a, "score.team_get")) ?? 0));
            score["team_set"] = DynValue.NewCallback((ctx, a) =>
            {
                RequireGameplay(inst, "score.team_set");
                Game?.SetTeamPoints(Slot(a, "score.team_set"), Value(a, "score.team_set"));
                return DynValue.Nil;
            });
            score["team_add"] = DynValue.NewCallback((ctx, a) =>
            {
                RequireGameplay(inst, "score.team_add");
                int team = Slot(a, "score.team_add");
                if (Game != null) Game.SetTeamPoints(team, Game.TeamPoints(team) + Value(a, "score.team_add"));
                return DynValue.Nil;
            });
            score["kills"] = DynValue.NewCallback((ctx, a) => DynValue.NewNumber(Game?.Kills(Slot(a, "score.kills")) ?? 0));
            score["deaths"] = DynValue.NewCallback((ctx, a) => DynValue.NewNumber(Game?.Deaths(Slot(a, "score.deaths")) ?? 0));
            s.Globals["score"] = score;

            var match = new Table(s);
            match["active"] = DynValue.NewCallback((ctx, a) => DynValue.NewBoolean(Game?.InMatch ?? false));
            match["mode"] = DynValue.NewCallback((ctx, a) => Game == null ? DynValue.Nil : DynValue.NewString(Game.Mode));
            match["teams"] = DynValue.NewCallback((ctx, a) => DynValue.NewBoolean(Game?.Teams ?? false));
            match["time_left"] = DynValue.NewCallback((ctx, a) => DynValue.NewNumber(Game?.TimeLeft ?? -1));
            match["setting"] = DynValue.NewCallback((ctx, a) =>
            {
                if (Game == null || a.Count < 1 || a[0].Type != DataType.String) return DynValue.Nil;
                object? v = Game.Setting(a[0].String);
                return v == null ? DynValue.Nil : DynValue.FromObject(s, v);
            });
            match["player"] = DynValue.NewCallback((ctx, a) =>
            {
                if (Game?.Player(Slot(a, "match.player")) is not { } p) return DynValue.Nil;
                var t = new Table(s) { ["hunter"] = p.Hunter, ["team"] = p.Team, ["bot"] = p.Bot };
                return DynValue.NewTable(t);
            });
            // ends the match now; the results rank by the scores as they stand (set them first). "end" is a Lua keyword.
            match["finish"] = DynValue.NewCallback((ctx, a) =>
            {
                RequireGameplay(inst, "match.finish");
                Game?.Finish();
                return DynValue.Nil;
            });
            match["point_goal"] = DynValue.NewCallback((ctx, a) => DynValue.NewNumber(Game?.PointGoal ?? 0));
            match["time_goal"] = DynValue.NewCallback((ctx, a) => DynValue.NewNumber(Game?.TimeGoal ?? 0));
            double Number(CallbackArguments a, string fn)
            {
                if (a.Count < 1 || a[0].Type != DataType.Number) throw new ScriptRuntimeException($"{fn}(n) expects a number");
                return a[0].Number;
            }
            match["set_point_goal"] = DynValue.NewCallback((ctx, a) =>
            {
                RequireGameplay(inst, "match.set_point_goal");
                double n = Number(a, "match.set_point_goal");
                if (Game != null) Game.PointGoal = (int)Math.Round(n);
                return DynValue.Nil;
            });
            match["set_time_goal"] = DynValue.NewCallback((ctx, a) =>
            {
                RequireGameplay(inst, "match.set_time_goal");
                double n = Number(a, "match.set_time_goal");
                if (Game != null) Game.TimeGoal = (float)n;
                return DynValue.Nil;
            });
            match["set_time_left"] = DynValue.NewCallback((ctx, a) =>
            {
                RequireGameplay(inst, "match.set_time_left");
                double n = Number(a, "match.set_time_left");
                Game?.SetTimeLeft((float)n);
                return DynValue.Nil;
            });
            s.Globals["match"] = match;

            // the mode's rules (gameplay mods): mode.setup(fn) once as the match starts, after its own rules are set;
            // mode.tick(fn(dt)) every frame in place of the mode's rules (the score / time goal win checks, Survival's lives,
            // the Prime Hunter...), which mode.vanilla() runs for this frame. The time limit still ends the match.
            var mode = new Table(s);
            DynValue Register(CallbackArguments a, string fn, string ev)
            {
                RequireGameplay(inst, fn);
                if (a.Count < 1 || a[0].Type != DataType.Function) throw new ScriptRuntimeException($"{fn}(function) expects a function");
                if (!inst.Hooks.TryGetValue(ev, out var list)) inst.Hooks[ev] = list = new List<Closure>();
                list.Add(a[0].Function);
                return DynValue.Nil;
            }
            mode["setup"] = DynValue.NewCallback((ctx, a) => Register(a, "mode.setup", ModeSetupEvent));
            mode["tick"] = DynValue.NewCallback((ctx, a) => Register(a, "mode.tick", ModeTickEvent));
            mode["vanilla"] = DynValue.NewCallback((ctx, a) =>
            {
                RequireGameplay(inst, "mode.vanilla");
                Game?.VanillaRules();
                return DynValue.Nil;
            });
            // the mod mode this match plays (mod.json "Modes"): its Id and the mod's id, nil = a plain vanilla mode
            mode["current"] = DynValue.NewCallback((ctx, a) =>
            {
                string? key = Game?.ModMode;
                int colon = key?.LastIndexOf(':') ?? -1;
                if (key == null || colon <= 0) return DynValue.Nil;
                return DynValue.NewTuple(DynValue.NewString(key[(colon + 1)..]), DynValue.NewString(key[..colon]));
            });
            s.Globals["mode"] = mode;

            // where bots go (gameplay mods): a bot heads for the arena node closest to its objective whenever it would
            // wander to a random one; fights, pickups and the mode's own targets still come first
            var bot = new Table(s);
            bot["objective"] = DynValue.NewCallback((ctx, a) =>
            {
                if (Game?.BotObjective(Slot(a, "bot.objective")) is not { } o) return DynValue.Nil;
                return DynValue.NewTuple(DynValue.NewNumber(o.X), DynValue.NewNumber(o.Y), DynValue.NewNumber(o.Z));
            });
            bot["set_objective"] = DynValue.NewCallback((ctx, a) =>
            {
                RequireGameplay(inst, "bot.set_objective");
                int slot = Slot(a, "bot.set_objective");
                if (a.Count < 2 || a[1].IsNil())
                {
                    Game?.SetBotObjective(slot, null);
                    return DynValue.Nil;
                }
                if (a.Count < 4 || a[1].Type != DataType.Number || a[2].Type != DataType.Number || a[3].Type != DataType.Number)
                    throw new ScriptRuntimeException("bot.set_objective(slot, x, y, z) expects a position (or just slot to clear it)");
                Game?.SetBotObjective(slot, ((float)a[1].Number, (float)a[2].Number, (float)a[3].Number));
                return DynValue.Nil;
            });
            s.Globals["bot"] = bot;
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
