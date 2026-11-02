using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using MphRead;
using MphRead.Entities;
using MphRecomp.Multiplayer;
using OpenTK.Mathematics;

namespace MphRecomp.Arenas
{
    // The owner's pickup placements baked into real matches (release 0.2.0, owner 2026-10-11: "i have now placed all
    // arenas where i want and excluded where i dont want"). Each arena the owner placed has a file in Arenas/Placements
    // (PickupPlacement.FileData, made with the dev placement editor and shipped inside this library: never read from
    // <data>/placements, the editor's own folder). Under the EXTRA PICKUPS rule (MatchSettings.PirateBlaster) a match
    // gets all of its arena's file: the placed pickups (MphRead's own item spawners, at the respawn the file gives),
    // the Echoes affinity crates, and the arena's own spawners and crates the owner deleted taken out. Rule off: only a
    // file's plain health/missile/UA spots (IsExtra: every vanilla arena's file holds none, so they stay exactly vanilla).
    // They join the room's entities as it loads (SceneSetup.HostExtraEntities), last after everything else, so their
    // ids are the same on every device; LAN/online guests build the same spawners from the same data and mirror the
    // host's items by those ids (Net/ItemSync). Crates follow EchoesCrates' rule (none where a match has no crates:
    // LAN/online, until crates are synced). Old replays (format < 29: Settings.PlacedPickups false) play without them.
    public static class PlacedPickups
    {
        // this match has them (set by MatchStart before the arena loads); crates too
        public static bool Enabled { get; set; }
        public static bool CratesEnabled { get; set; }
        // the files' player spawns: every match but the dev solo run (its editor shows the device's own file) unless it
        // asks for the placements
        public static bool SpawnsEnabled { get; set; } = true;
        // a file's plain health/missile/UA spots (IsExtra false) in every match, whatever EXTRA PICKUPS says (owner
        // 2026-10-31: the rule gates only the new beams and the ammo placed next to them; Mining Plaza has no pickups of
        // its own). Same solo rule as the spawns' (the editor shows the device's own file there)
        public static bool BaseEnabled { get; set; }

        // on with EXTRA PICKUPS, unless the settings turn them off; never in the dev solo run (its placement editor
        // loads the device's own files: SoloArena, PickupPlacer) unless the run asks for them (TRY CONTROLS sets true)
        public static bool For(MatchSettings settings) =>
            (settings.PirateBlaster ?? true) && (settings.Solo ? settings.PlacedPickups == true : settings.PlacedPickups ?? true);

        // MatchStart, before the arena loads (the entity hook lives in EchoesArena's static constructor: run it here, as a
        // vanilla-only session may never have touched an Echoes arena)
        public static void Setup(MatchSettings settings)
        {
            System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(typeof(EchoesArena).TypeHandle);
            Enabled = For(settings);
            CratesEnabled = Enabled && EchoesCrates.For(settings);
            SpawnsEnabled = !settings.Solo || settings.PlacedPickups == true;
            BaseEnabled = settings.Solo ? settings.PlacedPickups == true : settings.PlacedPickups ?? true;
            // the dev solo run walks through Mining Plaza's Luminoth gate (owner 2026-10-31: to place pickups behind it)
            MiningPlazaGate.Solo = settings.Solo;
        }

        // the beams EXTRA PICKUPS adds
        static bool NewBeam(string item) =>
            item is nameof(ItemType.PirateBlaster) or nameof(ItemType.HyperBeam) or nameof(ItemType.Paralyzer) or nameof(ItemType.AffinityWeapon);

        // how near a new beam its UA counts as that beam's ammo (the shipped files: 1.3-7.5)
        const float BeamAmmoRadius = 10;

        // under EXTRA PICKUPS only: a new beam, or UA placed by one in the same file; everything else is the arena's own
        public static bool IsExtra(PickupPlacement.PickupSpot spot, PickupPlacement.FileData file)
        {
            if (NewBeam(spot.Item)) return true;
            if (spot.Item is not (nameof(ItemType.UASmall) or nameof(ItemType.UABig))) return false;
            Vector3 pos = V(spot.Pos);
            return file.Pickups.Any(b => NewBeam(b.Item) && (V(b.Pos) - pos).Length <= BeamAmmoRadius);
        }

        static Dictionary<int, PickupPlacement.FileData>? _files;

        // the shipped files by room id
        public static IReadOnlyDictionary<int, PickupPlacement.FileData> Files
        {
            get
            {
                if (_files != null) return _files;
                var files = new Dictionary<int, PickupPlacement.FileData>();
                var prints = new Dictionary<int, string>();
                Assembly asm = typeof(PlacedPickups).Assembly;
                foreach (string res in asm.GetManifestResourceNames().OrderBy(n => n, StringComparer.Ordinal))
                {
                    string n = res.Replace('\\', '/');
                    if (!n.StartsWith("placements/", StringComparison.Ordinal) || !n.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) continue;
                    using Stream st = asm.GetManifestResourceStream(res)!;
                    using var mem = new MemoryStream();
                    st.CopyTo(mem);
                    byte[] bytes = mem.ToArray();
                    PickupPlacement.FileData? data = JsonSerializer.Deserialize<PickupPlacement.FileData>(bytes);
                    if (data != null)
                    {
                        files[data.RoomId] = data;
                        prints[data.RoomId] = $"{MphRecomp.Replay.ReplayFile.Crc32(bytes):x8}/{bytes.Length}";
                    }
                }
                _prints = prints;
                return _files = files;
            }
        }

        static Dictionary<int, string>? _prints;

        // a room's shipped placement file as "<crc32>/<length>" (null: none); a replay's arena fingerprint holds it
        // (ReplayData), as the placed spawns, pads and pickups are part of the match
        public static string? FileFingerprint(int roomId)
        {
            _ = Files;
            return _prints != null && _prints.TryGetValue(roomId, out string? print) ? print : null;
        }

        // the spawners this load made, to place in their room part once the room is in the scene (ResolveNodes)
        static readonly List<EntityBase> _placed = new();

        static Vector3 V(float[] v) => v is { Length: 3 } ? new Vector3(v[0], v[1], v[2]) : Vector3.Zero;

        // SceneSetup.HostExtraEntities: an arena's placed spawners and crates after every other entity, ids past theirs;
        // the arena's deleted spawners and crates left out. A file's player spawns (Mining Plaza 2026-10-31) are the
        // arena's spawns in every multiplayer match, whatever EXTRA PICKUPS says: they replace all of the room's own. Its
        // jump pads (the editor's pad mode, 2026-10-31) go in under the same rule, added to the room's own
        public static IReadOnlyList<EntityBase> Extra(Scene scene, int roomId, IReadOnlyList<EntityBase> entities)
        {
            _placed.Clear();
            if (!GameState.Multiplayer || !Files.TryGetValue(roomId, out PickupPlacement.FileData? file)) return entities;
            bool spawns = SpawnsEnabled && file.Spawns.Count > 0;
            bool pads = SpawnsEnabled && file.JumpPads.Count > 0;
            bool baseSpots = !Enabled && BaseEnabled && file.Pickups.Any(p => !IsExtra(p, file));
            bool modes = SpawnsEnabled && PickupPlacement.HasModeElements(file, GameState.Mode);
            if (!Enabled && !spawns && !pads && !baseSpots && !modes) return entities;
            bool echoes = EchoesArena.All.Any(d => d.Id == roomId);
            var list = new List<EntityBase>(entities);
            int id = entities.Count == 0 ? 0 : entities.Max(e => e.Id) + 1;
            if (Enabled || baseSpots) id = AddPickups(scene, file, echoes, entities, list, id);
            if (spawns)
            {
                list.RemoveAll(e => e is PlayerSpawnEntity);
                foreach (PickupPlacement.SpawnSpotData sp in file.Spawns)
                {
                    PlayerSpawnEntityData data = PickupPlacement.SpawnData(id++, V(sp.Pos), PickupPlacement.Level(V(sp.Facing)));
                    list.Add(new PlayerSpawnEntity(data, echoes ? ArenaModelWriter.RoomNode : "", scene));
                }
            }
            if (pads)
            {
                foreach (PickupPlacement.JumpPadSpotData jp in file.JumpPads)
                {
                    JumpPadEntity? pad = PickupPlacement.MakeJumpPad(id++, V(jp.Pos), V(jp.Target), echoes,
                        echoes ? ArenaModelWriter.RoomNode : "", scene, out _);
                    if (pad == null) continue;
                    pad.HostHeavyCarry = true; // an octolith carrier only gets bunted off it (owner 2026-11-01)
                    list.Add(pad);
                    if (!echoes) _placed.Add(pad);
                }
            }
            // the editor's mode elements (owner 2026-11-01): the mode's octoliths, bases and nodes replace the room's own
            if (modes)
            {
                list.RemoveAll(e => e is OctolithFlagEntity or FlagBaseEntity or NodeDefenseEntity);
                foreach (PickupPlacement.ModeSpotData m in file.Modes)
                {
                    foreach (EntityBase e in PickupPlacement.ModeEntities(m, GameState.Mode, ref id, scene))
                    {
                        list.Add(e);
                        _placed.Add(e);
                    }
                }
            }
            return list;
        }

        // all of a file under EXTRA PICKUPS; rule off, only its plain spots (BaseEnabled)
        static int AddPickups(Scene scene, PickupPlacement.FileData file, bool echoes, IReadOnlyList<EntityBase> entities, List<EntityBase> list, int id)
        {
            if (Enabled)
            foreach (PickupPlacement.RemovedItem r in file.RemovedItems)
            {
                list.RemoveAll(e => e is ItemSpawnEntity s && s.Id == r.Id && s.Data.ItemType.ToString() == r.Item);
            }
            if (Enabled)
            foreach (PickupPlacement.RemovedCrate r in file.RemovedCrates)
            {
                EchoesCrateEntity? c = list.OfType<EchoesCrateEntity>().FirstOrDefault(c => (c.Position - V(r.Pos)).LengthSquared < 0.25f);
                if (c != null) list.Remove(c);
            }
            // the arena's own spawners decide the base (vanilla arenas: some have one; Echoes: none), as the editor
            bool hasBase = entities.OfType<ItemSpawnEntity>().FirstOrDefault()?.Data.HasBase is byte b && b != 0;
            foreach (PickupPlacement.PickupSpot spot in file.Pickups)
            {
                if (!Enabled && IsExtra(spot, file)) continue;
                if (!Enum.TryParse(spot.Item, out ItemType type) || type == ItemType.None) continue;
                int respawn = spot.RespawnSeconds > 0 ? spot.RespawnSeconds : PickupPlacement.RespawnSeconds(type);
                ItemSpawnEntityData data = PickupPlacement.SpawnerData(id++, type, V(spot.Pos), hasBase, (ushort)(respawn * 30));
                // Echoes: the arena's one room node; vanilla: no name, the part is found by position once the room is up
                var spawner = new ItemSpawnEntity(data, echoes ? ArenaModelWriter.RoomNode : null!, scene);
                list.Add(spawner);
                if (!echoes) _placed.Add(spawner);
                if ((int)type < Metadata.Items.Count) SceneSetup.LoadItem(type, scene); // model + effects (Death Alt halo, double damage glow)
            }
            if (Enabled && CratesEnabled && echoes && file.Crates.Count > 0)
            {
                List<EchoesCrateEntity> arenaCrates = list.OfType<EchoesCrateEntity>().ToList();
                foreach (PickupPlacement.CrateSpotData c in file.Crates)
                {
                    ItemType[] pool = c.Pool.Select(n => Enum.TryParse(n, out ItemType t) ? t : ItemType.None).Where(t => t != ItemType.None).ToArray();
                    if (pool.Length == 0) continue;
                    Vector3 pos = V(c.Pos);
                    // lit as the nearest crate Echoes lit, at its scale (as the editor places them)
                    EchoesCrateEntity? near = arenaCrates.OrderBy(a => (a.Position - pos).LengthSquared).FirstOrDefault();
                    list.Add(new EchoesCrateEntity(id++, pos, V(c.Floor), near?.Light, pool, near?.Scale ?? EchoesArena.DefaultScale, scene));
                    foreach (ItemType t in pool) SceneSetup.LoadItem(t, scene);
                }
                scene.LoadEffect(EchoesCrateEntity.BreakEffect, persistent: false);
            }
            return id;
        }

        // MatchStart, after AddRoom: each vanilla spawner drawn and heard with the room part it stands in (the editor's
        // PickupPlacement.NodeAt), before its first item spawns
        public static void ResolveNodes(Scene scene)
        {
            foreach (EntityBase s in _placed) s.NodeRef = PickupPlacement.NodeAt(scene, s.Position);
            _placed.Clear();
        }
    }
}
