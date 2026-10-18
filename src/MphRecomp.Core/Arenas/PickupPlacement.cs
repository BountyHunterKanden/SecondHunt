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
        int _nextId = 20000;

        public bool IsEchoes { get; }
        public string? Path => _path;
        public int PlacedCount => _items.Count + _crates.Count;
        public int DeletedCount => _data.RemovedItems.Count + _data.RemovedCrates.Count;
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
