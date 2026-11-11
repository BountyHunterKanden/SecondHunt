using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using MphRead;
using MphRead.Entities;
using MphRecomp.Arenas;
using MphRecomp.Campaign;
using MphRecomp.Config;
using MphRecomp.Replay;
using MphRecomp.Save;

namespace MphRecomp.Mods
{
    // Runs the player's enabled mods during a game: loads them from <data>/mods (MODS menu on/off, load order and the mod
    // settings from recomp_settings.json), points the engine's event bus (MphRead GameEvents.cs) and filter bus (MphRead
    // GameFilters.cs) at them, and takes them down again when the game ends (docs/MOD_API.md).
    //
    // Two hosts. Cosmetic mods only watch and run for the whole game. Gameplay mods may change the game (mod.filter,
    // mod.spawn) and are rebuilt at each match's start (StartMatch) so a replay can bring back exactly the same mods: same
    // files (sha), settings, mod.save data and random seed, all in the replay's header (ReplayFile format 75).
    //
    // Offline games only for now: online and LAN games skip all mods (docs/ONLINE_PLAY.md §8b, decisions 10 and 12). A
    // replay being played back runs no cosmetic mods (they'd fire twice) but does run the gameplay mods it was recorded
    // with. Mod lines and errors go to <data>/mods/mod_log.txt (rewritten each game) as well as the app log; errors are
    // also shown on screen (TakeToasts) and on the MODS page (LastErrors).
    public static class LiveMods
    {
        public const string LogFileName = "mod_log.txt";
        public const string DataFileName = "mod_data.json";
        public const string MetaFileName = "mod_api.lua";
        // mod_data.json is written at most this often while mods have changed it (and at the game's end)
        public const long FlushMs = 5000;

        static ModManager? _manager;
        static RecompSettings? _settings;
        static string? _modsDir, _dataPath, _settingsPath;
        static ModHost? _cosmetic, _play;
        static SaveData? _save;
        static StreamWriter? _logFile;
        static Action<string>? _appLog;
        static bool _playback, _playPending;
        static long _lastFlush;
        static readonly List<string> _toasts = new();
        static FileSystemWatcher? _watcher;
        static volatile bool _reloadWanted;
        // the campaign file's mod.campaign values (AdoptCampaign), shared by both hosts; null = no campaign file
        static Dictionary<string, Dictionary<string, JsonElement>>? _campaign;

        // the cosmetic host (null = no cosmetic mod running)
        public static ModHost? Host => _cosmetic;
        // the gameplay host (null = no gameplay mod running)
        public static ModHost? PlayHost => _play;
        // every mod problem of the last Attach / StartMatch, "<id>: <what>" (the MODS page lists them)
        public static List<string> LastErrors { get; } = new();
        // PC: reload the mods when a file under mods/ changes (between matches: the next StartMatch, or Poll in a campaign).
        // On by itself in the PC app (RecompSettings.DesktopHost); a test can turn it on.
        public static bool HotReload { get; set; }

        // dataDir: the player's files (recomp_settings.json, mods/). skip: why this game runs without mods (null = run
        // them; "replay" = a replay played back: StartMatch loads the gameplay mods it recorded). match: a multiplayer match,
        // whose gameplay mods load at StartMatch rather than here. Returns how many mods were loaded and running.
        public static int Attach(string dataDir, string? skip, Action<string>? appLog = null, bool match = false)
        {
            DetachHosts();
            LastErrors.Clear();
            string modsDir = Path.Combine(dataDir, "mods");
            // public builds: no MODS button for now (owner 2026-11-07), so none run in play either (a replay still brings back its own)
            if ((skip != null && skip != "replay") || (RecompSettings.PublicBuild && skip == null) || !Directory.Exists(modsDir))
            {
                return 0;
            }
            _appLog = appLog;
            _modsDir = modsDir;
            _playback = skip == "replay";
            _settingsPath = Path.Combine(dataDir, "recomp_settings.json");
            _settings = RecompSettings.Load(_settingsPath);
            _manager = Discover(modsDir, _settings);
            if (_manager.Available.Count == 0)
            {
                _manager = null;
                return 0;
            }
            try { File.WriteAllText(Path.Combine(modsDir, MetaFileName), ModApiMeta.Generate()); } catch (Exception) { }
            try { _logFile = new StreamWriter(new FileStream(Path.Combine(modsDir, LogFileName), FileMode.Create, FileAccess.Write,
                FileShare.ReadWrite)) { AutoFlush = true }; }
            catch (Exception) { _logFile = null; }
            _dataPath = Path.Combine(modsDir, DataFileName);
            _save = ReadData();
            _lastFlush = Environment.TickCount64;
            ReplayPlayer.ModsCheck = CheckRecorded;
            if (HotReload || RecompSettings.DesktopHost) StartWatcher(modsDir);

            List<ModEntry> resolved = _manager.Resolve();
            foreach ((string id, string problem) in _manager.Problems) Problem(id, "not loaded: " + problem);
            if (!_playback)
            {
                _cosmetic = Build(resolved.Where(e => !e.Manifest.Gameplay), _save, seed: 0, settings: null, live: true);
                if (!match) _play = Build(resolved.Where(e => e.Manifest.Gameplay && e.Manifest.Modes.Count == 0),_save, NewSeed(), settings: null, live: true);
                else _playPending = true;
                ModContent.MountCosmetic(Ran(_cosmetic, resolved), Problem);
                if (!match) ModContent.MountGameplay(Ran(_play, resolved), Problem);
            }
            Wire();
            int running = Running(_cosmetic) + Running(_play);
            Write("mods", $"{running} mod(s) running{(_playback ? " (replay: its gameplay mods start with the match)" : "")}");
            _cosmetic?.Fire("start"); // once, before the room loads (game_start follows when it has)
            _play?.Fire("start");
            return running;
        }

        // A match is starting (MatchReplay.StartMatchReplayed, before CampaignHost.StartMatch): the gameplay mods start
        // over. recorded = the replay being played back (its mods come back as recorded), null = a live match (the
        // player's mods, a new seed). Returns the gameplay mods to write in a recorded match's header (empty = none).
        public static List<ReplayMod> StartMatch(ReplayFile? recorded, out ulong seed, Multiplayer.MatchSettings? match = null)
        {
            seed = 0;
            LiveGame.Settings = recorded?.Settings ?? match; // match.setting / match.player read these
            LiveGame.CurrentModMode = null;
            var header = new List<ReplayMod>();
            if (_manager == null || _modsDir == null)
            {
                SetFilters();
                return header;
            }
            if (_reloadWanted) Reload();
            Flush(force: true);
            _play?.SaveDataTo(_save ??= new SaveData());
            _play = null;
            _playPending = false;
            ModContent.MountGameplay(null);
            List<ModEntry> resolved = _manager.Resolve();
            if (recorded != null)
            {
                seed = recorded.ModSeed;
                if (recorded.Mods.Count > 0)
                {
                    var byId = _manager.Available.ToDictionary(e => e.Manifest.Id, StringComparer.Ordinal);
                    var save = new SaveData();
                    foreach (ReplayMod m in recorded.Mods)
                    {
                        try { save.ModData[m.Id] = JsonDocument.Parse(m.Data).RootElement.Clone(); } catch (Exception) { }
                    }
                    var settings = recorded.Mods.ToDictionary(m => m.Id, m => (IReadOnlyDictionary<string, string>)m.Settings, StringComparer.Ordinal);
                    ReplayMod? owner = recorded.Mods.FirstOrDefault(m => m.Settings.ContainsKey(ModeSettingKey));
                    if (owner != null) LiveGame.CurrentModMode = owner.Id + ":" + owner.Settings[ModeSettingKey];
                    List<ModEntry> ran = recorded.Mods.Where(m => byId.ContainsKey(m.Id)).Select(m => byId[m.Id]).ToList();
                    _play = Build(ran, save, seed, settings, live: false);
                    ModContent.MountGameplay(Ran(_play, ran), Problem);
                }
            }
            else
            {
                seed = NewSeed();
                (ModEntry Mod, ModModeInfo Mode)? chosen = FindMode(resolved, match?.ModMode);
                if (match?.ModMode != null && chosen == null) Problem(match.ModMode, "this mode's mod isn't running; the match plays " + match.Mode);
                // mods that add modes run only in a match playing one of them
                List<ModEntry> play = resolved.Where(e => e.Manifest.Gameplay && (e.Manifest.Modes.Count == 0 || e == chosen?.Mod)).ToList();
                var save = _save ?? new SaveData();
                _play = Build(play, save, seed, settings: null, live: true);
                ModContent.MountGameplay(Ran(_play, play), Problem);
                if (_play != null)
                {
                    foreach (ModInstance inst in _play.Mods.Where(i => i.Enabled))
                    {
                        ModEntry e = play.First(p => p.Manifest.Id == inst.Manifest.Id);
                        var rm = new ReplayMod { Id = inst.Manifest.Id, Version = inst.Manifest.Version, Sha = e.ComputeSha(),
                            Data = save.ModData.TryGetValue(inst.Manifest.Id, out JsonElement d) ? d.GetRawText() : "{}" };
                        foreach (ModSetting s in inst.Manifest.Settings.Where(s => s.Usable))
                            rm.Settings[s.Id] = s.Normalize(_settings?.GetModSetting(inst.Manifest.Id, s.Id));
                        if (e == chosen?.Mod)
                        {
                            rm.Settings[ModeSettingKey] = chosen.Value.Mode.Id;
                            LiveGame.CurrentModMode = e.Manifest.Id + ":" + chosen.Value.Mode.Id;
                        }
                        header.Add(rm);
                    }
                }
            }
            Wire();
            _play?.Fire("start");
            return header;
        }

        // the replay's mods row records the mod mode played as this reserved setting on its mod (mod settings can't start with $)
        public const string ModeSettingKey = "$mode";

        // a mod mode the match menu can offer: "<mod id>:<mode id>", its name, the mode it plays as and its starting values
        public sealed record ModeChoice(string Key, string ModId, string Name, string? Description, GameMode Base,
            int? PointGoal, int? TimeLimitMinutes, int? TimeGoalSeconds);

        // The modes the player's gameplay mods add (mod.json Modes), in MODS order; empty = none (or mods not attached)
        public static List<ModeChoice> Modes() => _manager == null ? new List<ModeChoice>() : Modes(_manager);

        // the same for the front end, where no mods are attached between games: read from the mods folder with the
        // player's MODS order and switches (attached: the running list, as above)
        public static List<ModeChoice> Modes(string? modsDir, RecompSettings? settings)
        {
            if (_manager != null) return Modes(_manager);
            if (modsDir == null || !Directory.Exists(modsDir)) return new List<ModeChoice>();
            return Modes(Discover(modsDir, settings ?? new RecompSettings()));
        }

        static List<ModeChoice> Modes(ModManager manager)
        {
            var list = new List<ModeChoice>();
            foreach (ModEntry e in manager.Resolve().Where(e => e.Manifest.Gameplay))
                foreach (ModModeInfo m in e.Manifest.Modes)
                    if (m.BaseMode is GameMode b)
                        list.Add(new ModeChoice(e.Manifest.Id + ":" + m.Id, e.Manifest.Id, string.IsNullOrWhiteSpace(m.Name) ? m.Id : m.Name,
                            m.Description, b, m.PointGoal, m.TimeLimit, m.TimeGoal));
            return list;
        }

        static (ModEntry Mod, ModModeInfo Mode)? FindMode(List<ModEntry> resolved, string? key)
        {
            int colon = key?.LastIndexOf(':') ?? -1;
            if (key == null || colon <= 0) return null;
            ModEntry? e = resolved.FirstOrDefault(x => x.Manifest.Gameplay && x.Manifest.Id == key[..colon]);
            ModModeInfo? m = e?.Manifest.Modes.FirstOrDefault(x => x.Id == key[(colon + 1)..] && x.BaseMode != null);
            return e != null && m != null ? (e, m) : null;
        }

        // MatchStart, once the match's own rules are on GameState (AddRoom's mode defaults + the match settings): the gameplay
        // mods' mode.setup hooks may change them before the room's first frame
        public static void MatchSetup()
        {
            LiveGame.ClearObjectives();
            if (GameState.Multiplayer) _play?.Fire(ModHost.ModeSetupEvent);
        }

        // Once a frame (the campaign renderer): mod.save changes reach mod_data.json within FlushMs, so a crash loses at
        // most that much; on PC a changed mod file reloads the mods outside a match.
        public static void Poll()
        {
            if (_reloadWanted && !_playback) Reload(); // cosmetic mods now; gameplay mods at the next match / game
            if (Environment.TickCount64 - _lastFlush >= FlushMs) Flush(force: false);
        }

        // Errors since the last call, for the screen ("MOD <id>: <what>")
        public static List<string> TakeToasts()
        {
            lock (_toasts)
            {
                var r = new List<string>(_toasts);
                _toasts.Clear();
                return r;
            }
        }

        // The player changed a mod setting (MODS menu or pause menu): cosmetic mods hear setting_changed(mod, setting, value)
        // at once. Gameplay mods keep the values the match started with (they're in its replay) until the next match.
        public static void SettingChanged(string modId, string settingId, string value)
        {
            _settings?.SetModSetting(modId, settingId, value);
            if (_cosmetic?.Get(modId) is not ModInstance inst) return;
            ModSetting? s = inst.Manifest.Settings.FirstOrDefault(x => x.Id == settingId);
            object typed = s != null ? s.Typed(value) : value;
            _cosmetic.FireWhere("setting_changed", m => m == inst, modId, settingId, typed);
        }

        // A campaign file was loaded or started (CampaignShip.AdoptSave): its mod.campaign values, null = none yet.
        // Any time, before or after Attach.
        public static void AdoptCampaign(Dictionary<string, Dictionary<string, JsonElement>>? mods)
        {
            _campaign = new();
            if (mods != null)
                foreach (var mod in mods) _campaign[mod.Key] = new Dictionary<string, JsonElement>(mod.Value);
            if (_cosmetic != null) _cosmetic.Campaign = _campaign;
            if (_play != null) _play.Campaign = _campaign;
        }

        // CampaignShip.SaveShip: a copy of the mod.campaign values to write in the save (null = none). Values of mods
        // not loaded this run are kept as the file had them.
        public static Dictionary<string, Dictionary<string, JsonElement>>? CampaignDataForSave()
        {
            if (_campaign == null) return null;
            var copy = new Dictionary<string, Dictionary<string, JsonElement>>();
            foreach (var mod in _campaign)
                if (mod.Value.Count > 0) copy[mod.Key] = new Dictionary<string, JsonElement>(mod.Value);
            return copy.Count > 0 ? copy : null;
        }

        // The game is over: stop the events and filters and keep each mod's mod.save values for next time.
        public static void Detach()
        {
            DetachHosts();
            _campaign = null;
        }

        static void DetachHosts()
        {
            GameEvents.Any = false;
            LiveEntities.Instance.SetScene(null);
            GameEvents.Tick = false;
            GameEvents.Listener = null;
            GameEvents.Messages = null;
            GameState.HostModeTick = null;
            LiveGame.ClearObjectives();
            GameFilters.Clear();
            LiveGame.Settings = null;
            ReplayPlayer.ModsCheck = null;
            Flush(force: true);
            _watcher?.Dispose();
            _watcher = null;
            _reloadWanted = false;
            _cosmetic = _play = null;
            ModContent.Clear();
            _manager = null;
            _settings = null;
            _save = null;
            _modsDir = _dataPath = _settingsPath = null;
            _playback = _playPending = false;
            _logFile?.Dispose();
            _logFile = null;
        }

        // ReplayPlayer.CheckBeforeStart: null = this device has every gameplay mod the replay ran with, unchanged
        public static string? CheckRecorded(IReadOnlyList<ReplayMod> mods)
        {
            if (mods.Count == 0) return null;
            if (_manager == null) return $"it needs the mod {mods[0].Id}, and no mods are installed";
            foreach (ReplayMod m in mods)
            {
                ModEntry? e = _manager.Available.FirstOrDefault(x => x.Manifest.Id == m.Id);
                if (e == null) return $"missing mod {m.Id} {m.Version}";
                if (e.LoadProblem != null) return $"the mod {m.Id} can't load: {e.LoadProblem}";
                if (!e.Manifest.Gameplay) return $"the mod {m.Id} is no longer a gameplay mod";
                if (!string.Equals(e.ComputeSha(), m.Sha, StringComparison.OrdinalIgnoreCase))
                    return $"the mod {m.Id} changed since the match (recorded with {m.Version}, installed {e.Manifest.Version})";
            }
            return null;
        }

        // ---- internals ----

        internal static ModManager Discover(string modsDir, RecompSettings settings)
        {
            var manager = new ModManager();
            manager.Discover(modsDir);
            manager.SetOrder(settings.ModOrder);
            foreach (string id in settings.DisabledMods) manager.SetEnabled(id, false);
            return manager;
        }

        // a host with these mods (null = none of them ran). settings: recorded values by mod id (a replay), else the player's.
        static ModHost? Build(IEnumerable<ModEntry> entries, SaveData save, ulong seed,
            Dictionary<string, IReadOnlyDictionary<string, string>>? settings, bool live)
        {
            List<ModEntry> list = entries.ToList();
            if (list.Count == 0) return null;
            var host = new ModHost(Write) { ModSeed = seed, Config = ConfigManager.Default(), OnError = (id, msg) => Problem(id, msg),
                Game = LiveGame.Instance, Entities = LiveEntities.Instance };
            host.EntityOpsQueued = () => GameEvents.Tick = true;
            host.ReadAsset = ModContent.Read;
            host.Campaign = _campaign;
            if (settings != null)
                host.SettingLookup = (mod, id) => settings.TryGetValue(mod, out var v) && v.TryGetValue(id, out string? s) ? s : null;
            else
                host.SettingLookup = (mod, id) => _settings?.GetModSetting(mod, id);
            if (live && list.All(e => !e.Manifest.Gameplay)) host.SettingsView = _settings;
            foreach (ModEntry e in list)
            {
                try { host.LoadFromSource(e.Manifest, e.ReadSource(), save, e.ReadFile); }
                catch (Exception ex) { Problem(e.Manifest.Id, "could not read: " + ex.Message); }
            }
            return Running(host) > 0 ? host : null;
        }

        static int Running(ModHost? host) => host?.Mods.Count(m => m.Enabled) ?? 0;

        // the entries whose mods are running in this host (their files/ folders get mounted: ModContent)
        static List<ModEntry> Ran(ModHost? host, IEnumerable<ModEntry> from)
            => host == null ? new List<ModEntry>()
                : from.Where(e => host.Mods.Any(m => m.Enabled && m.Manifest.Id == e.Manifest.Id)).ToList();

        // ---- room.on_load: gameplay mods add pickups and enemies as a room loads ----

        // mod-added entities' ids start here (EnemyPlacement uses 20000 up), or past the room's own if those run higher
        const int FirstRoomAddId = 30000;
        static bool _roomHooked;

        // wraps SceneSetup.HostExtraEntities once, after EchoesArena sets it (its static constructor runs first, so it can't
        // replace this); does nothing unless the gameplay host has a room.on_load handler
        static void HookRooms()
        {
            if (_roomHooked) return;
            _roomHooked = true;
            System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(typeof(EchoesArena).TypeHandle);
            var next = SceneSetup.HostExtraEntities;
            SceneSetup.HostExtraEntities = (scene, room, entities) =>
            {
                LiveEntities.Instance.SetScene(scene);
                if (next != null) entities = next(scene, room, entities);
                return RoomExtra(scene, room, entities);
            };
        }

        // a mod's pickup spawner: no room node in its data, so it takes the room part under its spot (as PlacedPickups)
        sealed class ModItemSpawner : ItemSpawnEntity
        {
            readonly bool _named;
            public ModItemSpawner(ItemSpawnEntityData data, string? node, Scene scene) : base(data, node!, scene) { _named = node != null; }

            public override void Initialize()
            {
                base.Initialize();
                if (!_named) NodeRef = PickupPlacement.NodeAt(_scene, Position);
            }
        }

        static IReadOnlyList<EntityBase> RoomExtra(Scene scene, int roomId, IReadOnlyList<EntityBase> entities)
        {
            ModHost? p = _play;
            if (p == null || !p.WantsRoomLoad()) return entities;
            string name = Metadata.GetRoomById(roomId, noThrow: true)?.Name ?? roomId.ToString();
            List<RoomAdd> adds = p.RoomLoad(roomId, name);
            if (adds.Count == 0) return entities;
            var list = new List<EntityBase>(entities);
            int id = Math.Max(FirstRoomAddId, entities.Count == 0 ? 0 : entities.Max(e => e.Id) + 1);
            bool echoes = EchoesArena.All.Any(d => d.Id == roomId);
            bool hasBase = entities.OfType<ItemSpawnEntity>().FirstOrDefault()?.Data.HasBase is byte b && b != 0;
            foreach (RoomAdd add in adds)
            {
                var pos = new OpenTK.Mathematics.Vector3(add.X, add.Y, add.Z);
                try
                {
                    if (add.Kind == "pickup")
                    {
                        if (!Enum.TryParse(add.Type, true, out ItemType type) || !Enum.IsDefined(type) || type == ItemType.None
                            || (int)type < 0 || (int)type >= Metadata.Items.Count)
                        {
                            Problem(add.Mod, $"room.on_load: no pickup named '{add.Type}'");
                            continue;
                        }
                        int respawn = add.Value > 0 ? (int)MathF.Round(add.Value) : PickupPlacement.RespawnSeconds(type);
                        var data = PickupPlacement.SpawnerData(id++, type, pos, hasBase, (ushort)Math.Clamp(respawn * 30, 0, ushort.MaxValue));
                        list.Add(new ModItemSpawner(data, echoes ? ArenaModelWriter.RoomNode : null, scene));
                        scene.LoadModel(Metadata.Items[(int)type]);
                    }
                    else
                    {
                        // campaign rooms only for now: enemies in a match arena are untested (bots, scoring, netcode-free
                        // but not designed for)
                        if (GameState.Mode != GameMode.SinglePlayer)
                        {
                            Problem(add.Mod, "room.on_load: add_enemy works in campaign rooms only for now");
                            continue;
                        }
                        EnemyCatalogue.Entry? entry = EnemyCatalogue.Find(add.Type);
                        if (entry == null)
                        {
                            Problem(add.Mod, $"room.on_load: no enemy '{add.Type}' (ids like mph:Zoomer, fh:Metroid, mph:Hunter:Noxus)");
                            continue;
                        }
                        if (entry.FirstHunt)
                        {
                            var fh = EnemyPlacement.PlacedFhData(entry.Fh, id++, pos, EnemyPlacement.FacingVector(add.Value), 1, 1, 1);
                            list.Add(new EnemyPlacement.PlacedFhEnemySpawner(fh, scene));
                        }
                        else
                        {
                            var placed = new EnemyPlacement.PlacedEnemy { Id = add.Type, Pos = new[] { add.X, add.Y, add.Z }, Facing = add.Value, Trigger = "entry" };
                            list.Add(new EnemyPlacement.PlacedEnemySpawner(EnemyPlacement.PlacedData(entry.Mph, placed, id++, entry.Hunter), scene));
                        }
                    }
                }
                catch (Exception ex) { Problem(add.Mod, $"room.on_load: couldn't add {add.Kind} '{add.Type}': {ex.Message}"); }
            }
            return list;
        }

        // ---- entity.*: the runtime spawn / remove queue, carried out at the end of a frame (the "tick" event's spot, after
        // the entity loop) so nothing changes under the loop; same frame live and in a replay

        static bool _wantTick;

        static void FlushEntities()
        {
            GameEvents.Tick = _wantTick;
            ModHost? p = _play;
            if (p == null) return;
            List<EntityOp> ops = p.TakeEntityOps();
            List<MessageOp> msgs = p.TakeMessageOps();
            if (ops.Count == 0 && msgs.Count == 0) return;
            Scene? scene = LiveEntities.Instance.Scene;
            if (scene == null) return;
            foreach (EntityOp op in ops)
            {
                try
                {
                    if (op.Op == "remove")
                    {
                        string? why = LiveEntities.Instance.Remove(scene, op.Handle);
                        if (why != null) Problem(op.Mod, $"entity.remove({op.Handle}): {why}");
                    }
                    else if (GameState.Mode != GameMode.SinglePlayer)
                    {
                        Problem(op.Mod, "entity.spawn_enemy works in campaign rooms only for now");
                    }
                    else
                    {
                        EnemyCatalogue.Entry? entry = EnemyCatalogue.Find(op.Type);
                        if (entry == null)
                        {
                            Problem(op.Mod, $"entity.spawn_enemy: no enemy '{op.Type}' (ids like mph:Zoomer, fh:Metroid, mph:Hunter:Noxus)");
                            continue;
                        }
                        int id = LiveEntities.Instance.FreeId(scene);
                        var pos = new OpenTK.Mathematics.Vector3(op.X, op.Y, op.Z);
                        EntityBase spawner = entry.FirstHunt
                            ? new EnemyPlacement.PlacedFhEnemySpawner(EnemyPlacement.PlacedFhData(entry.Fh, id, pos,
                                EnemyPlacement.FacingVector(op.Facing), 1, 1, 1), scene)
                            : new EnemyPlacement.PlacedEnemySpawner(EnemyPlacement.PlacedData(entry.Mph, new EnemyPlacement.PlacedEnemy
                                { Id = op.Type, Pos = new[] { op.X, op.Y, op.Z }, Facing = op.Facing, Trigger = "entry" }, id, entry.Hunter), scene);
                        scene.AddEntity(spawner);
                    }
                }
                catch (Exception ex) { Problem(op.Mod, $"entity.{op.Op}: {ex.Message}"); }
            }
            // msg.send: after the entity ops, in the order they were asked for
            foreach (MessageOp m in msgs)
            {
                EntityBase? from = null, to = null;
                if (m.To >= 0 && !scene.TryGetEntity(m.To, out to)) { Problem(m.Mod, $"msg.send({m.Type}): no entity {m.To} in this room"); continue; }
                if (m.From >= 0 && !scene.TryGetEntity(m.From, out from)) { Problem(m.Mod, $"msg.send({m.Type}): no entity {m.From} in this room"); continue; }
                try { scene.SendMessage(m.Type, from ?? to!, to, m.Param1, m.Param2, m.Delay); }
                catch (Exception ex) { Problem(m.Mod, $"msg.send({m.Type}): {ex.Message}"); }
            }
        }

        static void Wire()
        {
            if (_play?.WantsRoomLoad() == true || _play?.Mods.Any(m => m.Enabled && m.Gameplay) == true) HookRooms();
            ModHost? c = _cosmetic, p = _play;
            bool any = (c?.Mods.Any(m => m.Enabled && m.Hooks.Count > 0) ?? false) || (p?.Mods.Any(m => m.Enabled && m.Hooks.Count > 0) ?? false);
            // with a gameplay host the listener is always set: an entity op turns Tick on until the frame-end flush
            GameEvents.Listener = any || p != null ? (name, args) =>
            {
                if (name == "tick") FlushEntities();
                if (any) { c?.Fire(name, args); p?.Fire(name, args); }
            } : null;
            bool watch = (c?.WantsMessages() ?? false) || (p?.WantsMessages() ?? false);
            GameEvents.Messages = watch ? (m, f, t, p1, p2) => { c?.FireMessage(m, f, t, p1, p2); p?.FireMessage(m, f, t, p1, p2); } : null;
            _wantTick = (c?.Wants("tick") ?? false) || (p?.Wants("tick") ?? false);
            GameEvents.Tick = _wantTick;
            GameEvents.Any = any;
            GameState.HostModeTick = p?.Wants(ModHost.ModeTickEvent) == true ? scene => LiveGame.ModeTick(scene, p) : null;
            SetFilters();
        }

        static void SetFilters()
        {
            ModHost? p = _play;
            if (p == null) GameFilters.Clear();
            else GameFilters.Set(p.FilterNames(), p.ApplyFilter);
        }

        static ulong NewSeed()
        {
            // a fresh stream per match; it goes in the replay, which is all a replay needs
            ulong x = (ulong)Environment.TickCount64 * 0x9E3779B97F4A7C15UL ^ (ulong)DateTime.UtcNow.Ticks;
            return x == 0 ? 1 : x;
        }

        static SaveData ReadData()
        {
            try { return _dataPath != null && File.Exists(_dataPath) ? SaveManager.Load(_dataPath) : new SaveData(); }
            catch (Exception) { return new SaveData(); }
        }

        // write mod.save changes to mod_data.json (force: even if nothing changed). Never during a replay: its gameplay mods
        // run from the recorded data and mustn't overwrite the player's.
        static void Flush(bool force)
        {
            _lastFlush = Environment.TickCount64;
            if (_dataPath == null || _save == null) return;
            bool dirty = (_cosmetic?.Mods.Any(m => m.Dirty) ?? false) || (!_playback && (_play?.Mods.Any(m => m.Dirty) ?? false));
            if (!dirty && !force) return;
            if (!dirty && _cosmetic == null && (_playback || _play == null)) return;
            try
            {
                _cosmetic?.SaveDataTo(_save);
                if (!_playback) _play?.SaveDataTo(_save);
                SaveManager.Save(_save, _dataPath);
            }
            catch (Exception ex) { Write("mods", "couldn't write " + DataFileName + ": " + ex.Message); }
        }

        static void Reload()
        {
            _reloadWanted = false;
            if (_modsDir == null || _settingsPath == null) return;
            Flush(force: true);
            Write("mods", "a mod file changed: reloading the mods");
            _settings = RecompSettings.Load(_settingsPath);
            _manager = Discover(_modsDir, _settings);
            _save = ReadData();
            LastErrors.Clear();
            List<ModEntry> resolved = _manager.Resolve();
            foreach ((string id, string problem) in _manager.Problems) Problem(id, "not loaded: " + problem);
            _cosmetic = Build(resolved.Where(e => !e.Manifest.Gameplay), _save, 0, null, live: true);
            ModContent.MountCosmetic(Ran(_cosmetic, resolved), Problem);
            Wire();
            _cosmetic?.Fire("start");
        }

        static void StartWatcher(string modsDir)
        {
            try
            {
                _watcher = new FileSystemWatcher(modsDir) { IncludeSubdirectories = true, EnableRaisingEvents = false };
                FileSystemEventHandler changed = (_, e) =>
                {
                    string name = Path.GetFileName(e.FullPath);
                    if (name is LogFileName or DataFileName or MetaFileName) return;
                    _reloadWanted = true;
                };
                _watcher.Changed += changed;
                _watcher.Created += changed;
                _watcher.Deleted += changed;
                _watcher.Renamed += (_, e) => _reloadWanted = true;
                _watcher.EnableRaisingEvents = true;
            }
            catch (Exception) { _watcher = null; }
        }

        static void Write(string id, string msg)
        {
            string line = $"[{id}] {msg}";
            _appLog?.Invoke(line);
            try { _logFile?.WriteLine(line); } catch (Exception) { }
        }

        static void Problem(string id, string msg)
        {
            Write(id, msg);
            string line = $"{id}: {msg}";
            if (!LastErrors.Contains(line)) LastErrors.Add(line);
            lock (_toasts) { if (_toasts.Count < 20) _toasts.Add("MOD " + line); }
        }
    }
}
