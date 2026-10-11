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
    // the Echoes affinity crates, and the arena's own spawners and crates the owner deleted taken out. Rule off, or an
    // arena with no file: exactly vanilla.
    // They join the room's entities as it loads (SceneSetup.HostExtraEntities), last after everything else, so their
    // ids are the same on every device; LAN/online guests build the same spawners from the same data and mirror the
    // host's items by those ids (Net/ItemSync). Crates follow EchoesCrates' rule (none where a match has no crates:
    // LAN/online, until crates are synced). Old replays (format < 29: Settings.PlacedPickups false) play without them.
    public static class PlacedPickups
    {
        // this match has them (set by MatchStart before the arena loads); crates too
        public static bool Enabled { get; set; }
        public static bool CratesEnabled { get; set; }

        // on with EXTRA PICKUPS, unless the settings turn them off; never in the dev solo run (its placement editor
        // loads the device's own files: SoloArena, PickupPlacer)
        public static bool For(MatchSettings settings) =>
            (settings.PlacedPickups ?? true) && (settings.PirateBlaster ?? true) && !settings.Solo;

        // MatchStart, before the arena loads (the entity hook lives in EchoesArena's static constructor: run it here, as a
        // vanilla-only session may never have touched an Echoes arena)
        public static void Setup(MatchSettings settings)
        {
            System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(typeof(EchoesArena).TypeHandle);
            Enabled = For(settings);
            CratesEnabled = Enabled && EchoesCrates.For(settings);
        }

        static Dictionary<int, PickupPlacement.FileData>? _files;

        // the shipped files by room id
        public static IReadOnlyDictionary<int, PickupPlacement.FileData> Files
        {
            get
            {
                if (_files != null) return _files;
                var files = new Dictionary<int, PickupPlacement.FileData>();
                Assembly asm = typeof(PlacedPickups).Assembly;
                foreach (string res in asm.GetManifestResourceNames().OrderBy(n => n, StringComparer.Ordinal))
                {
                    string n = res.Replace('\\', '/');
                    if (!n.StartsWith("placements/", StringComparison.Ordinal) || !n.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) continue;
                    using Stream st = asm.GetManifestResourceStream(res)!;
                    PickupPlacement.FileData? data = JsonSerializer.Deserialize<PickupPlacement.FileData>(st);
                    if (data != null) files[data.RoomId] = data;
                }
                return _files = files;
            }
        }

        // the spawners this load made, to place in their room part once the room is in the scene (ResolveNodes)
        static readonly List<ItemSpawnEntity> _placed = new();

        static Vector3 V(float[] v) => v is { Length: 3 } ? new Vector3(v[0], v[1], v[2]) : Vector3.Zero;

        // SceneSetup.HostExtraEntities: an arena's placed spawners and crates after every other entity, ids past theirs;
        // the arena's deleted spawners and crates left out
        public static IReadOnlyList<EntityBase> Extra(Scene scene, int roomId, IReadOnlyList<EntityBase> entities)
        {
            _placed.Clear();
            if (!Enabled || !GameState.Multiplayer || !Files.TryGetValue(roomId, out PickupPlacement.FileData? file)) return entities;
            bool echoes = EchoesArena.All.Any(d => d.Id == roomId);
            var list = new List<EntityBase>(entities);
            foreach (PickupPlacement.RemovedItem r in file.RemovedItems)
            {
                list.RemoveAll(e => e is ItemSpawnEntity s && s.Id == r.Id && s.Data.ItemType.ToString() == r.Item);
            }
            foreach (PickupPlacement.RemovedCrate r in file.RemovedCrates)
            {
                EchoesCrateEntity? c = list.OfType<EchoesCrateEntity>().FirstOrDefault(c => (c.Position - V(r.Pos)).LengthSquared < 0.25f);
                if (c != null) list.Remove(c);
            }
            int id = entities.Count == 0 ? 0 : entities.Max(e => e.Id) + 1;
            // the arena's own spawners decide the base (vanilla arenas: some have one; Echoes: none), as the editor
            bool hasBase = entities.OfType<ItemSpawnEntity>().FirstOrDefault()?.Data.HasBase is byte b && b != 0;
            foreach (PickupPlacement.PickupSpot spot in file.Pickups)
            {
                if (!Enum.TryParse(spot.Item, out ItemType type) || type == ItemType.None) continue;
                int respawn = spot.RespawnSeconds > 0 ? spot.RespawnSeconds : PickupPlacement.RespawnSeconds(type);
                ItemSpawnEntityData data = PickupPlacement.SpawnerData(id++, type, V(spot.Pos), hasBase, (ushort)(respawn * 30));
                // Echoes: the arena's one room node; vanilla: no name, the part is found by position once the room is up
                var spawner = new ItemSpawnEntity(data, echoes ? ArenaModelWriter.RoomNode : null!, scene);
                list.Add(spawner);
                if (!echoes) _placed.Add(spawner);
                if ((int)type < Metadata.Items.Count) scene.LoadModel(Metadata.Items[(int)type]);
            }
            if (CratesEnabled && echoes && file.Crates.Count > 0)
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
                    foreach (ItemType t in pool) scene.LoadModel(Metadata.Items[(int)t]);
                }
                scene.LoadEffect(EchoesCrateEntity.BreakEffect, persistent: false);
            }
            return list;
        }

        // MatchStart, after AddRoom: each vanilla spawner drawn and heard with the room part it stands in (the editor's
        // PickupPlacement.NodeAt), before its first item spawns
        public static void ResolveNodes(Scene scene)
        {
            foreach (ItemSpawnEntity s in _placed) s.NodeRef = PickupPlacement.NodeAt(scene, s.Position);
            _placed.Clear();
        }
    }
}
