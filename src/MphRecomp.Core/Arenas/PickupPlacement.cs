using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using MphRead;
using MphRead.Entities;
using MphRead.Formats;
using MphRead.Formats.Culling;
using MphRecomp.Arenas.Editor;
using OpenTK.Mathematics;

namespace MphRecomp.Arenas
{
    // Dev pickup placement editor (owner 2026-10-10): in a solo arena run the owner
    // walks about and places pickups -- the Pirate Blaster, the affinity weapon, any item -- and, in an Echoes arena, the
    // affinity weapon's crates, where they want them; and deletes what they don't like, the arena's own pickups and crates
    // included. Each one is a live entity at once (an item spawner MphRead runs as any other, or an Echoes crate holding
    // the affinity weapon), so they can run over it, break it, take it. Every change is saved straight away to
    // <data>/placements/<room>.json, which are read to bake the picks into the match data (docs/PICKUPS.md).
    // Nothing here runs in a real match: the solo run (SoloArena.cs) is dev only and never recorded or networked.
    public sealed class PickupPlacement
    {
        // what can be placed, in the D-pad's order: the new pickups first
        public static readonly ItemType[] Placeable =
        {
            ItemType.PirateBlaster, ItemType.AffinityWeapon,
            ItemType.VoltDriver, ItemType.Battlehammer, ItemType.Imperialist, ItemType.Judicator, ItemType.Magmaul,
            ItemType.ShockCoil, ItemType.OmegaCannon,
            ItemType.HealthSmall, ItemType.HealthMedium, ItemType.HealthBig,
            ItemType.MissileSmall, ItemType.MissileBig, ItemType.UASmall, ItemType.UABig,
            ItemType.DoubleDamage, ItemType.Cloak, ItemType.Deathalt,
        };

        // the respawn a placed pickup gets, as vanilla's arenas mostly have them (docs/PICKUPS.md census)
        public static int RespawnSeconds(ItemType t) => t switch
        {
            ItemType.HealthSmall or ItemType.HealthMedium or ItemType.MissileSmall or ItemType.UASmall => 15,
            ItemType.HealthBig or ItemType.MissileBig or ItemType.UABig => 10,
            ItemType.OmegaCannon => 20,
            ItemType.DoubleDamage => 60,
            ItemType.Cloak => 150,
            ItemType.Deathalt => 270,
            _ => 5, // the weapons, the affinity weapon and the Pirate Blaster (owner 2026-10-09: blaster 5 s)
        };

        public static string Label(ItemType t) => t switch
        {
            ItemType.PirateBlaster => "Pirate Blaster",
            ItemType.AffinityWeapon => "Affinity weapon",
            ItemType.VoltDriver => "Volt Driver",
            ItemType.ShockCoil => "Shock Coil",
            ItemType.OmegaCannon => "Omega Cannon",
            ItemType.HealthSmall => "Health small",
            ItemType.HealthMedium => "Health medium",
            ItemType.HealthBig => "Health big",
            ItemType.MissileSmall => "Missile small",
            ItemType.MissileBig => "Missile big",
            ItemType.UASmall => "UA small",
            ItemType.UABig => "UA big",
            ItemType.DoubleDamage => "Double damage",
            _ => t.ToString(),
        };

        // the affinity crates the owner picked on the map pictures (docs/PICKUPS.md "Echoes affinity crate: owner picks",
        // 2026-10-09): where an Echoes arena starts when it has no saved placements yet
        static readonly Dictionary<int, Vector3> PickedAffinityCrates = new()
        {
            [1001] = new Vector3(-1.4f, 0.1f, 1.9f),     // Sidehopper Station: main hall front wall, centre
            [1002] = new Vector3(-0.5f, 2.1f, -124.4f),  // Spires: north end of the lower platform
            [1003] = new Vector3(-8.0f, -1.5f, -7.6f),   // Crossfire Chaos: middle section's west wall
            [1004] = new Vector3(7.2f, -5.7f, 1.4f),     // Pipeline: lower basin beside the round pillar
            [1005] = new Vector3(0.6f, 23.5f, 0.1f),     // Spider Complex: top of the central tower
            [1006] = new Vector3(-0.1f, 2.4f, -12.9f),   // Shooting Gallery: centre corridor
        };

        // an Echoes crate's centre over its floor (arena.json's crates: 1.07-1.25 units)
        const float CrateLift = 1.1f;
        const float DeleteReach = 6f, AimReach = 80f;

        public sealed class FileData
        {
            public string Room { get; set; } = "";
            public int RoomId { get; set; }
            public string Arena { get; set; } = "";
            public string Saved { get; set; } = "";
            public List<PickupSpot> Pickups { get; set; } = new();
            public List<CrateSpotData> Crates { get; set; } = new();
            // the arena's own item spawners deleted (entity id in the room's file, and what it held)
            public List<RemovedItem> RemovedItems { get; set; } = new();
            // the arena's own Echoes weapon crates deleted (by where they stand)
            public List<RemovedCrate> RemovedCrates { get; set; } = new();
            // the player spawns (the editor's spawn mode, Mining Plaza 2026-10-31): when there are any, a match uses these
            // and none of the arena's own (PlacedPickups.Extra)
            public List<SpawnSpotData> Spawns { get; set; } = new();
            // jump pads (the editor's jump pad mode, owner 2026-10-31): MPH's own pad, its launch solved to land on Target
            // (MapPads, as the map maker's); in every multiplayer match (PlacedPickups.Extra)
            public List<JumpPadSpotData> JumpPads { get; set; } = new();
            // the modes' octoliths, bases and nodes (the editor's mode choices, owner 2026-11-01): when the file has any for
            // a match's mode, they replace the room's own (PlacedPickups.Extra; MatchArenas.Census counts them)
            public List<ModeSpotData> Modes { get; set; } = new();
        }

        public sealed class ModeSpotData
        {
            public string Kind { get; set; } = "";              // ModeKinds
            public int Team { get; set; }                       // Capture: 0 / 1
            public float[] Pos { get; set; } = new float[3];    // on the floor
        }

        public sealed class JumpPadSpotData
        {
            public float[] Pos { get; set; } = new float[3];    // the pad, on the floor
            public float[] Target { get; set; } = new float[3]; // where it lands you, on the floor
        }

        public sealed class SpawnSpotData
        {
            public float[] Pos { get; set; } = new float[3];    // on the floor
            public float[] Facing { get; set; } = new float[3]; // level, unit length
        }

        public sealed class PickupSpot
        {
            public string Item { get; set; } = "";
            public float[] Pos { get; set; } = new float[3]; // the spawner's base (on the floor); the item floats 0.65 over it
            public int RespawnSeconds { get; set; }
        }

        public sealed class CrateSpotData
        {
            public List<string> Pool { get; set; } = new();
            public float[] Pos { get; set; } = new float[3];   // the crate's centre
            public float[] Floor { get; set; } = new float[3]; // the floor under it (its weapon appears 0.65 over it)
        }

        public sealed class RemovedItem
        {
            public int Id { get; set; }
            public string Item { get; set; } = "";
            public float[] Pos { get; set; } = new float[3];
        }

        public sealed class RemovedCrate
        {
            public float[] Pos { get; set; } = new float[3];
        }

        // one thing that can be deleted: what the status line names
        public readonly record struct Target(EntityBase Entity, string Label, float Distance);

        readonly Scene _scene;
        readonly string? _path;
        readonly FileData _data;
        readonly List<(ItemSpawnEntity Entity, PickupSpot Spot)> _items = new();
        readonly List<(EchoesCrateEntity Entity, CrateSpotData Spot)> _crates = new();
        readonly List<(PlayerSpawnEntity Entity, SpawnSpotData Spot)> _spawns = new();
        readonly List<(JumpPadEntity Entity, JumpPadSpotData Spot)> _pads = new();
        readonly List<(PlacedModeMarker Entity, ModeSpotData Spot)> _modes = new();
        int _nextId = 20000;

        public bool IsEchoes { get; }
        public string? Path => _path;
        public int PlacedCount => _items.Count + _crates.Count;
        public int DeletedCount => _data.RemovedItems.Count + _data.RemovedCrates.Count;
        public int SpawnCount => _spawns.Count;
        public int PadCount => _pads.Count;
        public int ModeCount(string kind, int team = -1) => _modes.Count(m => m.Spot.Kind == kind && (team < 0 || m.Spot.Team == team));
        // a match's seats
        public const int SpawnsWanted = 8;
        public string? LastError { get; private set; }

        // dir null: nothing saved (tests)
        public PickupPlacement(Scene scene, int roomId, string? dir)
        {
            _scene = scene;
            RoomMetadata? meta = Metadata.GetRoomById(roomId);
            IsEchoes = MphRecomp.Arenas.EchoesArena.All.Any(d => d.Id == roomId);
            string name = meta?.Name ?? roomId.ToString();
            _path = dir == null ? null : System.IO.Path.Combine(dir, FileName(name));
            _data = new FileData { Room = name, RoomId = roomId, Arena = meta?.InGameName ?? name };
            FileData? saved = null;
            if (_path != null && File.Exists(_path))
            {
                try
                {
                    saved = JsonSerializer.Deserialize<FileData>(File.ReadAllText(_path));
                }
                catch (Exception ex)
                {
                    LastError = "couldn't read " + _path + ": " + ex.Message;
                }
            }
            // the arena's own crates up at once, so all of them can be seen and judged
            foreach (EchoesCrateEntity c in ArenaCrates()) c.UpNow();
            // the arena's own spawns (the room's, or the baked file's) marked like the ones placed here (owner 2026-10-31)
            foreach (PlayerSpawnEntity sp in ArenaSpawns())
            {
                foreach (ModelInstance inst in sp.GetModels()) inst.IsPlaceholder = false;
            }
            if (saved != null)
            {
                foreach (PickupSpot s in saved.Pickups)
                {
                    if (Enum.TryParse(s.Item, out ItemType t)) AddItem(t, V(s.Pos), s.RespawnSeconds);
                }
                foreach (CrateSpotData c in saved.Crates)
                {
                    var pool = c.Pool.Select(n => Enum.TryParse(n, out ItemType t) ? t : ItemType.None).Where(t => t != ItemType.None).ToArray();
                    if (pool.Length > 0) AddCrate(V(c.Floor), V(c.Pos), pool);
                }
                // a file with spawns replaces all the room's own, as in a match (PlacedPickups.Extra), so they go here too
                if (saved.Spawns.Count > 0)
                {
                    foreach (PlayerSpawnEntity sp in ArenaSpawns()) Destroy(sp);
                }
                foreach (SpawnSpotData sp in saved.Spawns) AddSpawn(V(sp.Pos), V(sp.Facing));
                foreach (JumpPadSpotData jp in saved.JumpPads) AddJumpPad(V(jp.Pos), V(jp.Target), out _);
                foreach (ModeSpotData m in saved.Modes) AddMode(m.Kind, m.Team, V(m.Pos));
                foreach (RemovedItem r in saved.RemovedItems)
                {
                    if (_scene.TryGetEntity(r.Id, out EntityBase? e) && e is ItemSpawnEntity s && s.Data.ItemType.ToString() == r.Item)
                    {
                        Destroy(s);
                    }
                    _data.RemovedItems.Add(r);
                }
                foreach (RemovedCrate r in saved.RemovedCrates)
                {
                    EchoesCrateEntity? c = ArenaCrates().FirstOrDefault(c => (c.Position - V(r.Pos)).LengthSquared < 0.25f);
                    if (c != null) Destroy(c);
                    _data.RemovedCrates.Add(r);
                }
            }
            else if (IsEchoes && PickedAffinityCrates.TryGetValue(roomId, out Vector3 picked))
            {
                // first visit: the owner's picks from the map pictures, ready to keep or delete
                Vector3 floor = FloorBelow(picked.AddY(1f), 3f) ?? picked;
                AddCrate(floor, floor.AddY(CrateLift), new[] { ItemType.AffinityWeapon });
                Save();
            }
        }

        public static string FileName(string room)
        {
            var chars = room.Select(ch => Char.IsLetterOrDigit(ch) ? ch : '_').ToArray();
            return new string(chars) + ".json";
        }

        static Vector3 V(float[] v) => v is { Length: 3 } ? new Vector3(v[0], v[1], v[2]) : Vector3.Zero;
        static float[] A(Vector3 v) => new[] { MathF.Round(v.X, 3), MathF.Round(v.Y, 3), MathF.Round(v.Z, 3) };

        IEnumerable<EchoesCrateEntity> ArenaCrates()
        {
            var list = new List<EchoesCrateEntity>();
            foreach (EnemyInstanceEntity e in _scene.GetEnemyInstanceEntities())
            {
                if (e is EchoesCrateEntity c && !_crates.Any(p => p.Entity == c)) list.Add(c);
            }
            return list;
        }

        // the room's player spawns not placed here
        List<PlayerSpawnEntity> ArenaSpawns()
        {
            var list = new List<PlayerSpawnEntity>();
            foreach (PlayerSpawnEntity e in _scene.GetPlayerSpawnEntities())
            {
                if (!_spawns.Any(p => p.Entity == e)) list.Add(e);
            }
            return list;
        }

        IEnumerable<ItemSpawnEntity> ArenaItems()
        {
            var list = new List<ItemSpawnEntity>();
            foreach (ItemSpawnEntity e in _scene.GetItemSpawnEntities())
            {
                if (!_items.Any(p => p.Entity == e)) list.Add(e);
            }
            return list;
        }

        // the floor under p (down to `depth` units), or null
        public Vector3? FloorBelow(Vector3 p, float depth)
        {
            CollisionResult res = default;
            if (CollisionDetection.CheckBetweenPoints(p, p.AddY(-depth), TestFlags.None, _scene, ref res))
            {
                return res.Position;
            }
            return null;
        }

        // where a pickup goes for a player: at their feet, or where their aim meets the room (backed off a wall, dropped
        // to the floor). Null: the aim reaches nothing (or nothing under it)
        public Vector3? SpotAtFeet(PlayerEntity p) => FloorBelow(p.Position.AddY(0.5f), 4f) ?? p.Position;

        public Vector3? SpotAtAim(Vector3 eye, Vector3 dir, out float distance)
        {
            distance = 0;
            if (dir.LengthSquared < 1e-6f) return null;
            dir.Normalize();
            CollisionResult res = default;
            if (!CollisionDetection.CheckBetweenPoints(eye, eye + dir * AimReach, TestFlags.None, _scene, ref res)) return null;
            distance = res.Distance * AimReach;
            // a floor hit stays where it is; a wall or a ceiling: a step back towards the player, then down to the floor
            Vector3 at = res.Plane.Y > 0.7f ? res.Position.AddY(0.3f) : res.Position - dir * 0.6f;
            return FloorBelow(at, 30f);
        }

        public bool CanPlaceCrate => IsEchoes;

        // the room part a spot on the floor belongs to (drawn and heard only while that part is): the part whose own
        // geometry is there, the smallest node box around the spot. Scene.GetNodeRefByPosition tests each part's
        // portal planes, which run on forever, so a spot far from a part can pass its test (Council Chamber: a spot by
        // the west wall came out in the east part, and the item was hidden from the room it's in)
        public NodeRef NodeAt(Vector3 floor) => NodeAt(_scene, floor);

        // (also PlacedPickups: the baked spawners in a real match, once the room is in the scene)
        public static NodeRef NodeAt(Scene scene, Vector3 floor)
        {
            NodeRef best = NodeRef.None;
            float bestSize = float.MaxValue;
            Model? model = scene.Room?.GetModels().FirstOrDefault()?.Model;
            NodeRef planes = scene.GetNodeRefByPosition(floor.AddY(0.65f));
            if (model == null) return planes;
            var stack = new Stack<int>();
            foreach (Node part in model.Nodes)
            {
                if (part.RoomPartId < 0 || part.ChildIndex == -1) continue;
                stack.Push(part.ChildIndex);
                while (stack.Count > 0)
                {
                    for (int i = stack.Pop(); i != -1; i = model.Nodes[i].NextIndex)
                    {
                        Node n = model.Nodes[i];
                        if (n.ChildIndex != -1) stack.Push(n.ChildIndex);
                        if (n.MeshCount == 0) continue;
                        Vector3 min = n.MinBounds, max = n.MaxBounds;
                        if (floor.X < min.X - 0.5f || floor.X > max.X + 0.5f || floor.Z < min.Z - 0.5f || floor.Z > max.Z + 0.5f
                            || floor.Y < min.Y - 1f || floor.Y > max.Y + 1f) continue;
                        float size = (max.X - min.X + 1) * (max.Y - min.Y + 1) * (max.Z - min.Z + 1);
                        if (part.RoomPartId != planes.PartIndex) size *= 1000; // the plane test's part first, when it has geometry here
                        if (size < bestSize)
                        {
                            bestSize = size;
                            best = new NodeRef(part.RoomPartId, part.ChildIndex, modelIndex: 0);
                        }
                    }
                }
            }
            return best.PartIndex != -1 ? best : planes;
        }

        public ItemSpawnEntity AddItem(ItemType type, Vector3 floor, int respawnSeconds = -1)
        {
            if (respawnSeconds < 0) respawnSeconds = RespawnSeconds(type);
            // the arena's own spawners decide the base (vanilla arenas: some have one; Echoes: none)
            bool hasBase = ArenaItems().FirstOrDefault()?.Data.HasBase is byte b && b != 0;
            ItemSpawnEntity e = new ItemSpawnEntity(SpawnerData(NextId(), type, floor, hasBase, (ushort)(respawnSeconds * 30)), "", _scene);
            _scene.AddEntity(e);
            e.NodeRef = NodeAt(floor);
            var spot = new PickupSpot { Item = type.ToString(), Pos = A(floor), RespawnSeconds = respawnSeconds };
            _items.Add((e, spot));
            _data.Pickups.Add(spot);
            return e;
        }

        public EchoesCrateEntity AddCrate(Vector3 floor, Vector3 center, ItemType[] pool)
        {
            EchoesCrateEntity? near = ArenaCrates().Concat(_crates.Select(c => c.Entity))
                .OrderBy(c => (c.Position - center).LengthSquared).FirstOrDefault();
            float scale = near?.Scale ?? MphRecomp.Arenas.EchoesArena.DefaultScale;
            foreach (ItemType t in pool) _scene.LoadModel(Metadata.Items[(int)t]);
            _scene.LoadEffect(EchoesCrateEntity.BreakEffect, persistent: false);
            // lit as the nearest crate Echoes lit (its light at the import); none: white
            var crate = new EchoesCrateEntity(NextId(), center, floor, near?.Light, pool, scale, _scene);
            _scene.AddEntity(crate);
            crate.UpNow();
            var spot = new CrateSpotData { Pool = pool.Select(t => t.ToString()).ToList(), Pos = A(center), Floor = A(floor) };
            _crates.Add((crate, spot));
            _data.Crates.Add(spot);
            return crate;
        }

        // a player spawn on the floor, facing `facing` (made level). Its marker is a live spawn too, so the solo run
        // respawns there: the red placeholder MphRead's viewer draws for spawns, shown
        public PlayerSpawnEntity AddSpawn(Vector3 floor, Vector3 facing)
        {
            facing = Level(facing);
            var e = new PlayerSpawnEntity(SpawnData(NextId(), floor, facing), "", _scene);
            foreach (ModelInstance inst in e.GetModels()) inst.IsPlaceholder = false;
            _scene.AddEntity(e);
            e.NodeRef = NodeAt(floor);
            var spot = new SpawnSpotData { Pos = A(floor), Facing = A(facing) };
            _spawns.Add((e, spot));
            _data.Spawns.Add(spot);
            return e;
        }

        internal static Vector3 Level(Vector3 facing)
        {
            facing.Y = 0;
            return facing.LengthSquared > 1e-6f ? facing.Normalized() : Vector3.UnitZ;
        }

        // a player spawn's data as a room file holds it (Entity.cs PlayerSpawnEntityData): any time, active, no team
        internal static PlayerSpawnEntityData SpawnData(int id, Vector3 pos, Vector3 facing)
        {
            var bytes = new byte[Marshal.SizeOf<PlayerSpawnEntityData>()];
            using (var w = new BinaryWriter(new MemoryStream(bytes)))
            {
                w.Write((ushort)EntityType.PlayerSpawn);
                w.Write((short)id);
                foreach (Vector3 v in new[] { pos, Vector3.UnitY, facing })
                {
                    w.Write(Fixed.ToInt(v.X));
                    w.Write(Fixed.ToInt(v.Y));
                    w.Write(Fixed.ToInt(v.Z));
                }
                w.Write((byte)0);  // Availability: any time
                w.Write((byte)1);  // Active
                w.Write((sbyte)-1); // TeamIndex: none
            }
            return MemoryMarshal.Read<PlayerSpawnEntityData>(bytes);
        }

        // the spawn nearest to p (placed or the arena's own), within reach
        public Target? NearestSpawn(Vector3 p)
        {
            Target? best = null;
            void Consider(EntityBase e, string label)
            {
                float d = (e.Position - p).Length;
                if (d <= DeleteReach && (best == null || d < best.Value.Distance)) best = new Target(e, label, d);
            }
            for (int i = 0; i < _spawns.Count; i++) Consider(_spawns[i].Entity, $"spawn {i + 1}");
            List<PlayerSpawnEntity> arena = ArenaSpawns();
            for (int i = 0; i < arena.Count; i++) Consider(arena[i], $"arena's spawn {i + 1}");
            return best;
        }

        // deleting one of the arena's spawns: a file with spawns replaces all the room's in a match, so the rest of
        // them move into the file first (kept where they are), then that one goes
        void AdoptArenaSpawns(PlayerSpawnEntity except)
        {
            foreach (PlayerSpawnEntity e in ArenaSpawns())
            {
                if (e == except) continue;
                var spot = new SpawnSpotData { Pos = A(e.Position), Facing = A(Level(e.FacingVector)) };
                _spawns.Add((e, spot));
                _data.Spawns.Add(spot);
            }
        }

        // a jump pad on the floor at `pad` that lands you at `target`; null (and why) when the throw is past MapPads.MaxSpeed
        public JumpPadEntity? AddJumpPad(Vector3 pad, Vector3 target, out MapPads.Launch launch)
        {
            JumpPadEntity? e = MakeJumpPad(NextId(), pad, target, IsEchoes, "", _scene, out launch);
            if (e == null) return null;
            _scene.AddEntity(e);
            e.NodeRef = NodeAt(pad);
            var spot = new JumpPadSpotData { Pos = A(pad), Target = A(target) };
            _pads.Add((e, spot));
            _data.JumpPads.Add(spot);
            return e;
        }

        // a placed pad as a room file would hold it (the map maker's compile, MapCompiler: vanilla MP pad, radius 1 height 1,
        // no cooldown, biped + alt form). Echoes arenas: every form on the biped's arc, as their cannons (SameArcBit)
        internal static JumpPadEntity? MakeJumpPad(int id, Vector3 pad, Vector3 target, bool echoes, string node, Scene scene,
            out MapPads.Launch launch)
        {
            launch = MapPads.Solve(pad, target, (a, b) =>
            {
                var res = new CollisionResult();
                return CollisionDetection.CheckBetweenPoints(a, b, TestFlags.Players, scene, ref res);
            });
            if (launch.TooFar || launch.Vector.LengthSquared < 1e-8f) return null;
            var header = new EntityDataHeader((ushort)EntityType.JumpPad, (short)id, pad, Vector3.UnitY, Vector3.UnitZ);
            var volume = new RawCollisionVolume(Vector3.UnitY.ToVector3Fx(), new Vector3Fx(0, 0, 0),
                new Fixed(Fixed.ToInt(MapPads.PadRadius)), new Fixed(Fixed.ToInt(MapPads.PadHeight)));
            var data = new JumpPadEntityData(header, parentId: -1, volume, (launch.Vector.Normalized() * 4).ToVector3Fx(),
                new Fixed(Fixed.ToInt(launch.Vector.Length)), MapPads.Lock, cooldownTime: 0, active: 1, modelId: 0, beamType: 0,
                TriggerFlags.PlayerBiped | TriggerFlags.PlayerAlt);
            if (echoes)
            {
                Span<byte> raw = MemoryMarshal.AsBytes(MemoryMarshal.CreateSpan(ref data, 1));
                int at = (int)Marshal.OffsetOf<JumpPadEntityData>(nameof(JumpPadEntityData.Unused28));
                MemoryMarshal.Write(raw.Slice(at), ArenaCannons.SameArcBit);
            }
            scene.LoadModel(Metadata.JumpPads[0]);
            scene.LoadModel("JumpPad_Beam");
            return new JumpPadEntity(data, node, scene);
        }

        // the placed pad nearest to p (by the pad or its landing spot), within reach
        public Target? NearestPad(Vector3 p)
        {
            Target? best = null;
            for (int i = 0; i < _pads.Count; i++)
            {
                float dPad = (_pads[i].Entity.Position - p).Length, dLand = (V(_pads[i].Spot.Target) - p).Length;
                float d = MathF.Min(dPad, dLand);
                string label = dPad <= dLand ? $"jump pad {i + 1}" : $"jump pad {i + 1} (its landing)";
                if (d <= DeleteReach && (best == null || d < best.Value.Distance)) best = new Target(_pads[i].Entity, label, d);
            }
            return best;
        }

        // a mode's octolith, base or node on the floor (its stand-in shown); a kind there's one of moves instead
        public PlacedModeMarker AddMode(string kind, int team, Vector3 floor)
        {
            if (ModeKinds.Single(kind))
            {
                foreach (var old in _modes.Where(m => m.Spot.Kind == kind && m.Spot.Team == team).ToList()) Delete(old.Entity);
            }
            var e = new PlacedModeMarker(kind, team, floor, _scene);
            _scene.AddEntity(e);
            e.NodeRef = NodeAt(floor);
            var spot = new ModeSpotData { Kind = kind, Team = team, Pos = A(floor) };
            _modes.Add((e, spot));
            _data.Modes.Add(spot);
            return e;
        }

        // the editor shows only the mode being placed (owner 2026-11-01): null = every mode's elements
        public void ShowMode(string? mode)
        {
            foreach ((PlacedModeMarker e, ModeSpotData spot) in _modes) e.Hidden = mode != null && ModeKinds.Mode(spot.Kind) != mode;
        }

        // the placed mode element of that kind (and team) nearest to p, within reach
        public Target? NearestMode(Vector3 p, string kind, int team)
        {
            Target? best = null;
            int n = 0;
            foreach ((PlacedModeMarker e, ModeSpotData spot) in _modes)
            {
                if (spot.Kind != kind || spot.Team != team) continue;
                n++;
                float d = (e.Position - p).Length;
                string label = ModeKinds.Single(kind) ? ModeKinds.Label(kind, team) : $"node {n}";
                if (d <= DeleteReach && (best == null || d < best.Value.Distance)) best = new Target(e, label, d);
            }
            return best;
        }

        // the kinds a mode uses (none: the mode has no octoliths or nodes)
        static bool ForMode(string kind, GameMode mode) => kind switch
        {
            ModeKinds.Capture => mode == GameMode.Capture,
            ModeKinds.Octolith or ModeKinds.Drop => mode is GameMode.Bounty or GameMode.BountyTeams,
            ModeKinds.Node => mode is GameMode.Nodes or GameMode.NodesTeams,
            ModeKinds.Defend => mode is GameMode.Defender or GameMode.DefenderTeams,
            _ => false,
        };

        // the file has mode elements for this mode: then they replace the room's own
        public static bool HasModeElements(FileData file, GameMode mode) => file.Modes.Any(m => ForMode(m.Kind, mode));

        // (octoliths, bases, nodes) the file's elements make in a match of this mode
        public static (int Flags, int Bases, int Nodes) ModeCounts(FileData file, GameMode mode)
        {
            int flags = 0, bases = 0, nodes = 0;
            foreach (ModeSpotData m in file.Modes.Where(m => ForMode(m.Kind, mode)))
            {
                if (m.Kind is ModeKinds.Capture or ModeKinds.Octolith) flags++;
                if (m.Kind is ModeKinds.Capture or ModeKinds.Drop) bases++;
                if (m.Kind is ModeKinds.Node or ModeKinds.Defend) nodes++;
            }
            return (flags, bases, nodes);
        }

        // MphRead's own entities for a placed mode element in a match of `mode` (none when it isn't that mode's), as the
        // map maker compiles them (MapCompiler): a Capture base = the team's octolith + its base at one spot (r1 h1); Bounty
        // = an octolith, and a team-1 base as the drop-off; a node r2.5 h4; Defender's ring r4 h4
        internal static List<EntityBase> ModeEntities(ModeSpotData spot, GameMode mode, ref int id, Scene scene)
        {
            var list = new List<EntityBase>();
            if (!ForMode(spot.Kind, mode)) return list;
            Vector3 pos = V(spot.Pos);
            switch (spot.Kind)
            {
            case ModeKinds.Capture:
                list.Add(new OctolithFlagEntity(OctolithData(id++, pos, (byte)spot.Team), scene));
                list.Add(new FlagBaseEntity(BaseData(id++, pos, (uint)spot.Team), scene));
                break;
            case ModeKinds.Octolith:
                list.Add(new OctolithFlagEntity(OctolithData(id++, pos, 0), scene));
                break;
            case ModeKinds.Drop:
                list.Add(new FlagBaseEntity(BaseData(id++, pos, 1), scene));
                break;
            default:
                list.Add(new NodeDefenseEntity(NodeData(id++, pos, ModeKinds.Radius(spot.Kind), ModeKinds.Height(spot.Kind)), scene));
                break;
            }
            return list;
        }

        static RawCollisionVolume Cylinder(float r, float h) => new RawCollisionVolume(Vector3.UnitY.ToVector3Fx(),
            new Vector3Fx(0, 0, 0), new Fixed(Fixed.ToInt(r)), new Fixed(Fixed.ToInt(h)));

        delegate void Fields(Span<byte> bytes);

        static T Build<T>(EntityType type, int id, Vector3 pos, Fields fields) where T : struct
        {
            var bytes = new byte[Marshal.SizeOf<T>()];
            var header = new EntityDataHeader((ushort)type, (short)id, pos, Vector3.UnitY, Vector3.UnitZ);
            MemoryMarshal.Write(bytes, in header);
            fields(bytes);
            return MemoryMarshal.Read<T>(bytes);
        }

        static int Offset<T>(string field) => (int)Marshal.OffsetOf<T>(field);

        internal static OctolithFlagEntityData OctolithData(int id, Vector3 pos, byte team) =>
            Build<OctolithFlagEntityData>(EntityType.OctolithFlag, id, pos,
                b => b[Offset<OctolithFlagEntityData>(nameof(OctolithFlagEntityData.TeamId))] = team);

        internal static FlagBaseEntityData BaseData(int id, Vector3 pos, uint team) =>
            Build<FlagBaseEntityData>(EntityType.FlagBase, id, pos, b =>
            {
                MemoryMarshal.Write(b.Slice(Offset<FlagBaseEntityData>(nameof(FlagBaseEntityData.TeamId))), in team);
                RawCollisionVolume vol = Cylinder(1, 1);
                MemoryMarshal.Write(b.Slice(Offset<FlagBaseEntityData>(nameof(FlagBaseEntityData.Volume))), in vol);
            });

        internal static NodeDefenseEntityData NodeData(int id, Vector3 pos, float r, float h) =>
            Build<NodeDefenseEntityData>(EntityType.NodeDefense, id, pos, b =>
            {
                RawCollisionVolume vol = Cylinder(r, h);
                MemoryMarshal.Write(b.Slice(Offset<NodeDefenseEntityData>(nameof(NodeDefenseEntityData.Volume))), in vol);
            });

        public EchoesCrateEntity AddAffinityCrate(Vector3 floor) => AddCrate(floor, floor.AddY(CrateLift), new[] { ItemType.AffinityWeapon });

        int NextId()
        {
            while (_scene.TryGetEntity(_nextId, out _)) _nextId++;
            return _nextId++;
        }

        // an item spawner's data as a room file holds it (72 bytes: Entity.cs ItemSpawnEntityData), always on, endless
        internal static ItemSpawnEntityData SpawnerData(int id, ItemType type, Vector3 pos, bool hasBase, ushort interval)
        {
            var bytes = new byte[Marshal.SizeOf<ItemSpawnEntityData>()];
            using (var w = new BinaryWriter(new MemoryStream(bytes)))
            {
                w.Write((ushort)EntityType.ItemSpawn);
                w.Write((short)id);
                foreach (Vector3 v in new[] { pos, Vector3.UnitY, Vector3.UnitZ })
                {
                    w.Write(Fixed.ToInt(v.X));
                    w.Write(Fixed.ToInt(v.Y));
                    w.Write(Fixed.ToInt(v.Z));
                }
                w.Write(-1); // ParentId: none
                w.Write((int)type);
                w.Write((byte)1); // Enabled
                w.Write((byte)(hasBase ? 1 : 0));
                w.Write((byte)0); // AlwaysActive
                w.Write((byte)0);
                w.Write((ushort)0); // MaxSpawnCount: no limit
                w.Write(interval); // SpawnInterval (30 Hz)
                w.Write((ushort)0); // SpawnDelay: at once
                w.Write((short)-1); // NotifyEntityId
                w.Write((uint)Message.None);
                w.Write(0);
                w.Write(0);
            }
            return MemoryMarshal.Read<ItemSpawnEntityData>(bytes);
        }

        // the pickup or crate nearest to p (placed or the arena's own), within reach
        public Target? Nearest(Vector3 p)
        {
            Target? best = null;
            void Consider(EntityBase e, string label)
            {
                float d = (e.Position - p).Length;
                if (d <= DeleteReach && (best == null || d < best.Value.Distance)) best = new Target(e, label, d);
            }
            foreach ((ItemSpawnEntity e, _) in _items) Consider(e, "your " + Label(e.Data.ItemType));
            foreach ((EchoesCrateEntity e, _) in _crates) Consider(e, "your " + CrateLabel(e));
            foreach (ItemSpawnEntity e in ArenaItems()) Consider(e, "arena's " + Label(e.Data.ItemType));
            foreach (EchoesCrateEntity e in ArenaCrates()) Consider(e, "arena's " + CrateLabel(e));
            return best;
        }

        static string CrateLabel(EchoesCrateEntity c) =>
            c.Pool.Count == 1 && c.Pool[0] == ItemType.AffinityWeapon ? "affinity crate" : "weapon crate";

        public void Delete(EntityBase e)
        {
            int i = _items.FindIndex(p => p.Entity == e);
            if (i >= 0)
            {
                _data.Pickups.Remove(_items[i].Spot);
                _items.RemoveAt(i);
            }
            else if ((i = _crates.FindIndex(p => p.Entity == e)) >= 0)
            {
                _data.Crates.Remove(_crates[i].Spot);
                _crates.RemoveAt(i);
            }
            else if ((i = _spawns.FindIndex(p => p.Entity == e)) >= 0)
            {
                _data.Spawns.Remove(_spawns[i].Spot);
                _spawns.RemoveAt(i);
            }
            else if ((i = _pads.FindIndex(p => p.Entity == e)) >= 0)
            {
                _data.JumpPads.Remove(_pads[i].Spot);
                _pads.RemoveAt(i);
            }
            else if ((i = _modes.FindIndex(p => p.Entity == e)) >= 0)
            {
                _data.Modes.Remove(_modes[i].Spot);
                _modes.RemoveAt(i);
            }
            else if (e is PlayerSpawnEntity ps)
            {
                AdoptArenaSpawns(ps);
            }
            else if (e is ItemSpawnEntity s)
            {
                _data.RemovedItems.Add(new RemovedItem { Id = s.Id, Item = s.Data.ItemType.ToString(), Pos = A(s.Position) });
            }
            else if (e is EchoesCrateEntity c)
            {
                _data.RemovedCrates.Add(new RemovedCrate { Pos = A(c.Position) });
            }
            Destroy(e);
        }

        // out of the room now, as MphRead removes an entity whose Process says it's done (Scene.ProcessFrame); a
        // spawner's item still out goes with it
        void Destroy(EntityBase e)
        {
            if (e is ItemSpawnEntity s && s.Item is ItemInstanceEntity item)
            {
                item.Destroy();
                _scene.RemoveEntity(item);
                s.Item = null;
            }
            e.Destroy();
            _scene.RemoveEntity(e);
        }

        public bool Save()
        {
            if (_path == null) return true;
            try
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_path)!);
                _data.Saved = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                string tmp = _path + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(_data, new JsonSerializerOptions { WriteIndented = true }));
                File.Move(tmp, _path, overwrite: true);
                LastError = null;
                return true;
            }
            catch (Exception ex)
            {
                LastError = "couldn't save " + _path + ": " + ex.Message;
                return false;
            }
        }
    }
}
