using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using MphRead;
using MphRead.Entities;
using MphRead.Formats;
using MphRecomp.Arenas;
using OpenTK.Mathematics;

namespace MphRecomp.Campaign
{
    // Hard Mode's enemies (docs/ENEMY_IMPORT.md sections 4-6, phase H1): one hand-editable file per room,
    // <dir>/<ROOM>.json, holding the enemies placed there (catalogue id, spot, facing, counts, buffs, trigger, group),
    // the door gates (a group's doors stay locked from its first enemy until all of its enemies are dead) and the edits
    // to the room's own spawners (remove, replace with a catalogue enemy, buff, put in a group).
    // Install hooks SceneSetup.HostExtraEntities (campaign only: the single-player mode) and the player's damage taken;
    // Tick (CampaignHost.Step, once per frame after the scene) applies health buffs to new enemies, releases waves and
    // runs the gates. The editing calls are the in-game placer's (App Campaign/EnemyPlacer.cs): each saves the file.
    public static class EnemyPlacement
    {
        public sealed class PlacedEnemy
        {
            public string Id { get; set; } = "";
            public float[] Pos { get; set; } = new float[3];
            public float Facing { get; set; }          // degrees around Y, 0 = +Z
            public int Count { get; set; } = 1;        // spawned at once
            public int Limit { get; set; } = 1;        // alive at once
            public int Total { get; set; } = 1;        // overall (0 = no end)
            public float HealthMul { get; set; } = 1f;
            public float DamageMul { get; set; } = 1f;
            public int ItemChance { get; set; } = -1;  // -1 = the template's
            public string Trigger { get; set; } = "near"; // near (within Radius) / entry (on entering the room) / wave
            public float Radius { get; set; } = 12f;
            public int Wave { get; set; }              // wave: after the group's lower waves are dead
            public int Group { get; set; }
        }

        public sealed class Gate
        {
            public int Group { get; set; }
            public List<int> Doors { get; set; } = new List<int>(); // door entity ids; empty = every door in the room
            public bool MiniBoss { get; set; }         // (a later phase: health bar)
        }

        public sealed class RoomEdit
        {
            public int Id { get; set; }                // the room's own spawner's entity id
            public string Type { get; set; } = "";     // its enemy (for reading the file)
            public bool Remove { get; set; }
            public string? ReplaceWith { get; set; }   // catalogue id
            public float HealthMul { get; set; } = 1f;
            public float DamageMul { get; set; } = 1f;
            public int Group { get; set; } = -1;       // -1 = no group
        }

        public sealed class FileData
        {
            public string Room { get; set; } = "";
            public int RoomId { get; set; }
            public string Saved { get; set; } = "";
            public List<PlacedEnemy> Enemies { get; set; } = new List<PlacedEnemy>();
            public List<Gate> Gates { get; set; } = new List<Gate>();
            public List<RoomEdit> RoomEdits { get; set; } = new List<RoomEdit>();
        }

        // ---- the spawners ----

        // a placed MPH enemy's spawner: no room node in its data, so it takes the node under its spot
        public sealed class PlacedEnemySpawner : EnemySpawnEntity
        {
            public PlacedEnemySpawner(EnemySpawnEntityData data, Scene scene) : base(data, "", scene)
            {
            }

            public override void Initialize()
            {
                base.Initialize();
                NodeRef = PickupPlacement.NodeAt(_scene, Position);
            }
        }

        // a placed First Hunt enemy's spawner, running in an MPH room (HostInMph); Held = a wave not released yet
        public sealed class PlacedFhEnemySpawner : FhEnemySpawnEntity
        {
            public bool Held { get; set; }

            public PlacedFhEnemySpawner(FhEnemySpawnEntityData data, Scene scene) : base(data, scene)
            {
                HostInMph = true;
            }

            public override void Initialize()
            {
                base.Initialize();
                NodeRef = PickupPlacement.NodeAt(_scene, Position);
            }

            public override bool Process()
            {
                return Held || base.Process();
            }
        }

        sealed class Tracked
        {
            public EntityBase Spawner = null!;
            public PlacedEnemy? Placed;
            public RoomEdit? Edit;
            public EnemySpawnEntityData Original;      // a room-own spawner's own data
            public bool Held;
            public float HealthMul => Placed?.HealthMul ?? Edit?.HealthMul ?? 1f;
            public float DamageMul => Placed?.DamageMul ?? Edit?.DamageMul ?? 1f;
            public int Group => Placed?.Group ?? Edit?.Group ?? -1;
            public int Wave => Placed?.Wave ?? 0;
        }

        sealed class GateState
        {
            public Gate Gate = null!;
            public int Phase;                           // 0 waiting, 1 locked, 2 cleared
            public readonly List<DoorEntity> Locked = new List<DoorEntity>();
        }

        public static string? Dir { get; private set; }
        public static string? LastError { get; private set; }
        public static FileData? Current { get; private set; }

        static bool _installed;
        static Scene? _scene;
        static string? _path;
        static readonly List<Tracked> _tracked = new List<Tracked>();
        static readonly Dictionary<EntityBase, Tracked> _bySpawner = new Dictionary<EntityBase, Tracked>();
        static readonly List<GateState> _gates = new List<GateState>();
        static HashSet<EnemyInstanceEntity> _seen = new HashSet<EnemyInstanceEntity>();
        static HashSet<EnemyInstanceEntity> _seenNext = new HashSet<EnemyInstanceEntity>();

        static readonly JsonSerializerOptions _json = new JsonSerializerOptions { WriteIndented = true };

        // dir = the Hard Mode campaign's placement folder (<data>/campaigns/hardmode); null turns it off
        public static void Install(string? dir)
        {
            Dir = dir;
            if (_installed || dir == null)
            {
                return;
            }
            _installed = true;
            Func<Scene, int, IReadOnlyList<EntityBase>, IReadOnlyList<EntityBase>>? next = SceneSetup.HostExtraEntities;
            SceneSetup.HostExtraEntities = (scene, room, entities) =>
            {
                if (next != null)
                {
                    entities = next(scene, room, entities);
                }
                return AddEntities(scene, room, entities);
            };
#if ENEMY_DAMAGE_HOOK
            Func<uint, EntityBase, uint>? nextDamage = PlayerEntity.HostEnemyDamage;
            PlayerEntity.HostEnemyDamage = (damage, source) =>
            {
                if (nextDamage != null)
                {
                    damage = nextDamage(damage, source);
                }
                return EnemyDamage(damage, source);
            };
#endif
        }

        static bool On => Dir != null && GameState.Mode == GameMode.SinglePlayer;

        public static FileData Load(string dir, int roomId, out string path)
        {
            RoomMetadata? meta = Metadata.GetRoomById(roomId, noThrow: true);
            string name = meta?.Name ?? roomId.ToString();
            path = Path.Combine(dir, PickupPlacement.FileName(name));
            FileData? data = null;
            if (File.Exists(path))
            {
                try
                {
                    data = JsonSerializer.Deserialize<FileData>(File.ReadAllText(path));
                }
                catch (Exception ex)
                {
                    LastError = "couldn't read " + path + ": " + ex.Message;
                }
            }
            data ??= new FileData();
            data.Room = name;
            data.RoomId = roomId;
            data.Enemies ??= new List<PlacedEnemy>();
            data.Gates ??= new List<Gate>();
            data.RoomEdits ??= new List<RoomEdit>();
            return data;
        }

        public static bool Save()
        {
            if (Current == null || _path == null)
            {
                return false;
            }
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                Current.Saved = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                string tmp = _path + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(Current, _json));
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

        // the hook: the room's entities with the file's edits made and its placed enemies added
        static IReadOnlyList<EntityBase> AddEntities(Scene scene, int roomId, IReadOnlyList<EntityBase> entities)
        {
            if (!On)
            {
                return entities;
            }
            _scene = scene;
            _tracked.Clear();
            _bySpawner.Clear();
            _gates.Clear();
            _seen.Clear();
            Current = Load(Dir!, roomId, out _path);
            FileData data = Current;
            var list = new List<EntityBase>(entities.Count + data.Enemies.Count);
            foreach (EntityBase entity in entities)
            {
                RoomEdit? edit = entity is EnemySpawnEntity ? data.RoomEdits.FirstOrDefault(r => r.Id == entity.Id) : null;
                if (edit == null)
                {
                    list.Add(entity);
                    continue;
                }
                if (edit.Remove)
                {
                    continue;
                }
                var own = (EnemySpawnEntity)entity;
                var t = new Tracked { Edit = edit, Original = own.Data };
                t.Spawner = BuildRoomOwn(scene, t) ?? entity;
                list.Add(t.Spawner);
                Track(t);
            }
            var taken = new HashSet<int>(list.Select(e => e.Id));
            int id = 20000;
            foreach (PlacedEnemy placed in data.Enemies)
            {
                while (taken.Contains(id))
                {
                    id++;
                }
                EntityBase? spawner = BuildPlaced(scene, placed, id, out bool held);
                if (spawner == null)
                {
                    continue;
                }
                taken.Add(id);
                list.Add(spawner);
                Track(new Tracked { Spawner = spawner, Placed = placed, Held = held });
            }
            SetUpGates();
            return list;
        }

        static void Track(Tracked t)
        {
            _tracked.Add(t);
            _bySpawner[t.Spawner] = t;
        }

        static void SetUpGates()
        {
            foreach (GateState g in _gates)
            {
                UnlockAll(g);
            }
            _gates.Clear();
            if (Current == null)
            {
                return;
            }
            foreach (Gate gate in Current.Gates)
            {
                _gates.Add(new GateState { Gate = gate });
            }
        }

        // ---- building spawners ----

        static Vector3 V(float[] v) => v is { Length: 3 } ? new Vector3(v[0], v[1], v[2]) : Vector3.Zero;
        static float[] A(Vector3 v) => new[] { MathF.Round(v.X, 3), MathF.Round(v.Y, 3), MathF.Round(v.Z, 3) };

        public static Vector3 FacingVector(float degrees)
        {
            float r = MathHelper.DegreesToRadians(degrees);
            return new Vector3(MathF.Sin(r), 0, MathF.Cos(r));
        }

        static byte[] Bytes<T>(T value) where T : struct
        {
            int size = Marshal.SizeOf<T>();
            var bytes = new byte[size];
            IntPtr ptr = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(value, ptr, fDeleteOld: false);
                Marshal.Copy(ptr, bytes, 0, size);
            }
            finally
            {
                Marshal.FreeHGlobal(ptr);
            }
            return bytes;
        }

        static int Off<T>(string field) => (int)Marshal.OffsetOf<T>(field);

        static void W16(byte[] b, int o, int v) => BinaryPrimitives.WriteInt16LittleEndian(b.AsSpan(o), (short)v);
        static void W32(byte[] b, int o, int v) => BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(o), v);

        static void WriteHeader<T>(byte[] b, int id, Vector3 pos, Vector3 facing)
        {
            int h = Off<T>("Header");
            W16(b, h + Off<EntityDataHeader>("EntityId"), id);
            WriteVec(b, h + Off<EntityDataHeader>("Position"), pos);
            WriteVec(b, h + Off<EntityDataHeader>("UpVector"), Vector3.UnitY);
            WriteVec(b, h + Off<EntityDataHeader>("FacingVector"), facing);
        }

        static void WriteVec(byte[] b, int o, Vector3 v)
        {
            W32(b, o, Fixed.ToInt(v.X));
            W32(b, o + 4, Fixed.ToInt(v.Y));
            W32(b, o + 8, Fixed.ToInt(v.Z));
        }

        static byte Clamp8(int v, int min, int max) => (byte)Math.Clamp(v, min, max);

        // a placed MPH enemy's data: the template's per-type settings (relative to the spawner) at the new spot, with
        // the entry's counts and trigger; no links, messages or room node
        public static EnemySpawnEntityData PlacedData(EnemySpawnEntityData template, PlacedEnemy p, int id)
        {
            byte[] b = Bytes(template);
            WriteHeader<EnemySpawnEntityData>(b, id, V(p.Pos), FacingVector(p.Facing));
            bool wave = p.Trigger == "wave";
            W16(b, Off<EnemySpawnEntityData>("LinkedEntityId"), -1);
            b[Off<EnemySpawnEntityData>("SpawnTotal")] = Clamp8(p.Total, 0, 255);
            b[Off<EnemySpawnEntityData>("SpawnLimit")] = Clamp8(p.Limit, 1, 255);
            b[Off<EnemySpawnEntityData>("SpawnCount")] = Clamp8(p.Count, 1, 255);
            b[Off<EnemySpawnEntityData>("Active")] = (byte)(wave ? 0 : 1);
            b[Off<EnemySpawnEntityData>("AlwaysActive")] = 1;
            if (p.ItemChance >= 0)
            {
                b[Off<EnemySpawnEntityData>("ItemChance")] = Clamp8(p.ItemChance, 0, 100);
            }
            W16(b, Off<EnemySpawnEntityData>("SpawnerHealth"), 0);
            float reach = p.Trigger == "near" ? Math.Clamp(p.Radius, 1f, 200f) : 200f;
            W32(b, Off<EnemySpawnEntityData>("ActiveDistance"), Fixed.ToInt(reach));
            Array.Clear(b, Off<EnemySpawnEntityData>("NodeName"), 16);
            foreach (string f in new[] { "EntityId1", "EntityId2", "EntityId3" })
            {
                W16(b, Off<EnemySpawnEntityData>(f), -1);
            }
            foreach (string f in new[] { "Message1", "Message2", "Message3" })
            {
                W32(b, Off<EnemySpawnEntityData>(f), 0);
            }
            return Read.ReadStruct<EnemySpawnEntityData>(b);
        }

        // a room's own spawner replaced by a catalogue MPH enemy: everything of its own (id, spot, counts, messages)
        // except the enemy type and its per-type settings
        public static EnemySpawnEntityData ReplacedData(EnemySpawnEntityData own, EnemySpawnEntityData template)
        {
            byte[] b = Bytes(own);
            byte[] t = Bytes(template);
            int type = Off<EnemySpawnEntityData>("EnemyType");
            int fields = Off<EnemySpawnEntityData>("Fields");
            int end = Off<EnemySpawnEntityData>("LinkedEntityId");
            b[type] = t[type];
            Array.Copy(t, fields, b, fields, end - fields);
            int dist = Off<EnemySpawnEntityData>("EnemyActiveDistance");
            Array.Copy(t, dist, b, dist, 4);
            return Read.ReadStruct<EnemySpawnEntityData>(b);
        }

        public static FhEnemySpawnEntityData PlacedFhData(FhEnemySpawnEntityData template, int id, Vector3 pos, Vector3 facing,
            int total, int limit, int count)
        {
            byte[] b = Bytes(template);
            WriteHeader<FhEnemySpawnEntityData>(b, id, pos, facing);
            b[Off<FhEnemySpawnEntityData>("SpawnTotal")] = Clamp8(total, 0, 255);
            b[Off<FhEnemySpawnEntityData>("SpawnLimit")] = Clamp8(limit, 1, 255);
            b[Off<FhEnemySpawnEntityData>("SpawnCount")] = Clamp8(count, 1, 255);
            W16(b, Off<FhEnemySpawnEntityData>("StartFrame"), 0);
            Array.Clear(b, Off<FhEnemySpawnEntityData>("NodeName"), 16);
            W16(b, Off<FhEnemySpawnEntityData>("ParentId"), -1);
            int msg = Off<FhEnemySpawnEntityData>("EmptyMessage");
            Array.Clear(b, msg, b.Length - msg);
            return Read.ReadStruct<FhEnemySpawnEntityData>(b);
        }

        static EntityBase? BuildPlaced(Scene scene, PlacedEnemy p, int id, out bool held)
        {
            held = p.Trigger == "wave";
            EnemyCatalogue.Entry? entry = EnemyCatalogue.Find(p.Id);
            if (entry == null)
            {
                LastError = "unknown enemy " + p.Id;
                return null;
            }
            if (entry.FirstHunt)
            {
                FhEnemySpawnEntityData data = PlacedFhData(entry.Fh, id, V(p.Pos), FacingVector(p.Facing), p.Total, p.Limit, p.Count);
                return new PlacedFhEnemySpawner(data, scene) { Held = held };
            }
            return new PlacedEnemySpawner(PlacedData(entry.Mph, p, id), scene);
        }

        static EntityBase? BuildRoomOwn(Scene scene, Tracked t)
        {
            EnemyCatalogue.Entry? entry = EnemyCatalogue.Find(t.Edit?.ReplaceWith);
            if (entry == null)
            {
                return new EnemySpawnEntity(t.Original, "", scene) { };
            }
            EnemySpawnEntityData own = t.Original;
            if (entry.FirstHunt)
            {
                FhEnemySpawnEntityData data = PlacedFhData(entry.Fh, own.Header.EntityId, own.Header.Position.ToFloatVector(),
                    own.Header.FacingVector.ToFloatVector(), own.SpawnTotal, Math.Max((int)own.SpawnLimit, 1), Math.Max((int)own.SpawnCount, 1));
                return new PlacedFhEnemySpawner(data, scene);
            }
            return new PlacedEnemySpawner(ReplacedData(own, entry.Mph), scene);
        }

        // ---- per frame ----

        public static void Tick(Scene scene)
        {
            if (!On || _scene != scene || _tracked.Count == 0)
            {
                return;
            }
            // health buffs, once per enemy as it appears
            _seenNext.Clear();
            foreach (EnemyInstanceEntity enemy in scene.GetEnemyInstanceEntities())
            {
                _seenNext.Add(enemy);
                if (_seen.Contains(enemy) || enemy.Owner == null || !_bySpawner.TryGetValue(enemy.Owner, out Tracked? t) || t.HealthMul == 1f)
                {
                    continue;
                }
                int health = (int)MathF.Round(enemy.Health * t.HealthMul);
                enemy.SetHealth((ushort)Math.Clamp(health, 1, UInt16.MaxValue));
            }
            (_seen, _seenNext) = (_seenNext, _seen);
            // waves
            foreach (Tracked t in _tracked)
            {
                if (t.Held && LowerWavesDone(t))
                {
                    t.Held = false;
                    if (t.Spawner is PlacedFhEnemySpawner fh)
                    {
                        fh.Held = false;
                    }
                    else
                    {
                        t.Spawner.SetActive(true);
                    }
                }
            }
            // gates
            foreach (GateState g in _gates)
            {
                if (g.Phase == 0 && GroupAlive(g.Gate.Group))
                {
                    g.Phase = 1;
                    foreach (DoorEntity door in scene.GetDoorEntities())
                    {
                        if ((g.Gate.Doors.Count == 0 || g.Gate.Doors.Contains(door.Id)) && !door.Flags.TestFlag(DoorFlags.Locked))
                        {
                            door.Lock(updateState: false);
                            g.Locked.Add(door);
                        }
                    }
                }
                else if (g.Phase == 1 && GroupDone(g.Gate.Group))
                {
                    g.Phase = 2;
                    UnlockAll(g);
                }
            }
        }

        static void UnlockAll(GateState g)
        {
            foreach (DoorEntity door in g.Locked)
            {
                door.Unlock(updateState: false, noLockAnimSfx: false);
            }
            g.Locked.Clear();
        }

        static bool InScene(EntityBase e) => _scene != null && _scene.TryGetEntity(e.Id, out EntityBase? found) && found == e;

        // all of this spawner's enemies are spawned and dead (an inactive room spawner that never spawned counts: it
        // was cleared before, or waits on the room's own script); endless spawners count when none are alive
        static bool Done(Tracked t)
        {
            if (t.Held)
            {
                return false;
            }
            if (!InScene(t.Spawner))
            {
                return true;
            }
            if (t.Spawner is FhEnemySpawnEntity fh)
            {
                return fh.Finished || fh.Data.SpawnTotal == 0 && fh.SpawnedCount > 0 && fh.AliveCount == 0;
            }
            var s = (EnemySpawnEntity)t.Spawner;
            if (s.ActiveCount > 0)
            {
                return false;
            }
            bool active = s.Flags.TestFlag(SpawnerFlags.Active);
            return s.Data.SpawnTotal > 0 ? s.SpawnedCount >= s.Data.SpawnTotal || !active : s.SpawnedCount > 0 || !active;
        }

        static bool Alive(Tracked t)
        {
            if (!InScene(t.Spawner))
            {
                return false;
            }
            return t.Spawner switch
            {
                FhEnemySpawnEntity fh => fh.AliveCount > 0,
                EnemySpawnEntity s => s.ActiveCount > 0,
                _ => false,
            };
        }

        static bool LowerWavesDone(Tracked t)
        {
            foreach (Tracked o in _tracked)
            {
                if (o != t && o.Group == t.Group && o.Wave < t.Wave && !Done(o))
                {
                    return false;
                }
            }
            return true;
        }

        static bool GroupAlive(int group) => _tracked.Any(t => t.Group == group && Alive(t));

        static bool GroupDone(int group) => _tracked.All(t => t.Group != group || Done(t));

        // the player's damage from a tracked spawner's enemy (or its shot) times its buff
        static uint EnemyDamage(uint damage, EntityBase source)
        {
            if (!On || _bySpawner.Count == 0)
            {
                return damage;
            }
            EntityBase? from = source is BeamProjectileEntity beam ? beam.Owner : source;
            for (int i = 0; i < 4 && from is EnemyInstanceEntity enemy; i++)
            {
                if (enemy.Owner != null && _bySpawner.TryGetValue(enemy.Owner, out Tracked? t))
                {
                    return t.DamageMul == 1f ? damage : (uint)Math.Max(0, MathF.Round(damage * t.DamageMul));
                }
                from = enemy.Owner;
            }
            return damage;
        }

        // ---- the placer's calls ----

        public readonly record struct Target(EntityBase Spawner, bool Placed, string Label, float Distance);

        public static IEnumerable<Target> Targets(Vector3 from)
        {
            if (_scene == null)
            {
                yield break;
            }
            foreach (EnemySpawnEntity s in _scene.GetEnemySpawnEntities())
            {
                yield return MakeTarget(s, from);
            }
            foreach (FhEnemySpawnEntity s in _scene.GetFhEnemySpawnEntities())
            {
                if (_bySpawner.ContainsKey(s))
                {
                    yield return MakeTarget(s, from);
                }
            }
        }

        static Target MakeTarget(EntityBase s, Vector3 from)
        {
            _bySpawner.TryGetValue(s, out Tracked? t);
            return new Target(s, t?.Placed != null, LabelOf(s, t), Vector3.Distance(from, s.Position));
        }

        static string LabelOf(EntityBase s, Tracked? t)
        {
            if (t?.Placed != null)
            {
                return EnemyCatalogue.Find(t.Placed.Id)?.Label ?? t.Placed.Id;
            }
            string own = s is EnemySpawnEntity es ? EnemyCatalogue.SplitWords((t?.Original ?? es.Data).EnemyType.ToString()) : "?";
            if (t?.Edit?.ReplaceWith is string with)
            {
                return own + " -> " + (EnemyCatalogue.Find(with)?.Label ?? with);
            }
            return own + " (ROOM)";
        }

        public static Target? Nearest(Vector3 from, float reach)
        {
            Target? best = null;
            foreach (Target t in Targets(from))
            {
                if (t.Distance <= reach && (best == null || t.Distance < best.Value.Distance))
                {
                    best = t;
                }
            }
            return best;
        }

        // the catalogue entry describing what's at a target (a room's own boss-type enemies get an ad hoc entry)
        public static EnemyCatalogue.Entry EntryOf(EntityBase spawner)
        {
            _bySpawner.TryGetValue(spawner, out Tracked? t);
            if (t?.Placed != null && EnemyCatalogue.Find(t.Placed.Id) is EnemyCatalogue.Entry placed)
            {
                return placed;
            }
            if (t?.Edit?.ReplaceWith != null && EnemyCatalogue.Find(t.Edit.ReplaceWith) is EnemyCatalogue.Entry with)
            {
                return with;
            }
            if (spawner is FhEnemySpawnEntity fh)
            {
                return EnemyCatalogue.Find("fh:" + fh.Data.EnemyType) ?? new EnemyCatalogue.Entry { FirstHunt = true };
            }
            EnemyType type = spawner is EnemySpawnEntity es ? es.Data.EnemyType : EnemyType.Zoomer;
            return EnemyCatalogue.Find("mph:" + type) ?? new EnemyCatalogue.Entry
            {
                Id = "mph:" + type, Label = EnemyCatalogue.SplitWords(type.ToString()), Type = type,
            };
        }

        public static EnemyInstanceEntity? LiveEnemyOf(EntityBase spawner)
        {
            if (_scene == null)
            {
                return null;
            }
            foreach (EnemyInstanceEntity e in _scene.GetEnemyInstanceEntities())
            {
                if (e.Owner == spawner)
                {
                    return e;
                }
            }
            return null;
        }

        static int NextId()
        {
            int id = 20000;
            while (_scene != null && _scene.TryGetEntity(id, out _))
            {
                id++;
            }
            return id;
        }

        // place a catalogue enemy at a floor spot facing a direction; live at once, and saved
        public static EntityBase? Place(EnemyCatalogue.Entry entry, Vector3 floor, float facing)
        {
            if (_scene == null || Current == null)
            {
                return null;
            }
            var p = new PlacedEnemy { Id = entry.Id, Pos = A(floor), Facing = MathF.Round(facing) };
            Current.Enemies.Add(p);
            EntityBase? spawner = AddLive(p);
            Save();
            return spawner;
        }

        static EntityBase? AddLive(PlacedEnemy p)
        {
            EntityBase? spawner = BuildPlaced(_scene!, p, NextId(), out bool held);
            if (spawner == null)
            {
                return null;
            }
            AddToScene(spawner);
            Track(new Tracked { Spawner = spawner, Placed = p, Held = held });
            return spawner;
        }

        static void AddToScene(EntityBase spawner)
        {
            if (spawner is EnemySpawnEntity s)
            {
                SceneSetup.LoadEnemyResources(s, _scene!);
            }
            _scene!.AddEntity(spawner);
        }

        // take a spawner and its enemies out of the scene
        static void RemoveLive(EntityBase spawner)
        {
            if (_scene == null)
            {
                return;
            }
            var doomed = new List<EnemyInstanceEntity>();
            foreach (EnemyInstanceEntity e in _scene.GetEnemyInstanceEntities())
            {
                if (e.Owner == spawner)
                {
                    doomed.Add(e);
                }
            }
            foreach (EnemyInstanceEntity e in doomed)
            {
                e.Destroy();
                _scene.RemoveEntity(e);
            }
            if (InScene(spawner))
            {
                spawner.Destroy();
                _scene.RemoveEntity(spawner);
            }
            if (_bySpawner.Remove(spawner, out Tracked? t))
            {
                _tracked.Remove(t);
            }
        }

        // delete a placed enemy, or remove a room's own spawner (kept in the file as a removal)
        public static bool Delete(EntityBase spawner)
        {
            if (Current == null)
            {
                return false;
            }
            _bySpawner.TryGetValue(spawner, out Tracked? t);
            if (t?.Placed != null)
            {
                Current.Enemies.Remove(t.Placed);
            }
            else if (spawner is EnemySpawnEntity own)
            {
                RoomEdit edit = EditFor(own, t);
                edit.Remove = true;
            }
            else
            {
                return false;
            }
            RemoveLive(spawner);
            Save();
            return true;
        }

        static RoomEdit EditFor(EnemySpawnEntity own, Tracked? t)
        {
            if (t?.Edit != null)
            {
                return t.Edit;
            }
            RoomEdit? edit = Current!.RoomEdits.FirstOrDefault(r => r.Id == own.Id);
            if (edit == null)
            {
                edit = new RoomEdit { Id = own.Id, Type = own.Data.EnemyType.ToString() };
                Current.RoomEdits.Add(edit);
            }
            return edit;
        }

        public static string[] PlacedFields { get; } =
            { "HEALTH", "DAMAGE", "COUNT", "LIMIT", "TOTAL", "TRIGGER", "RADIUS", "WAVE", "GROUP", "ITEM", "FACING", "GATE" };

        public static string[] RoomFields { get; } = { "REPLACE", "HEALTH", "DAMAGE", "GROUP", "GATE" };

        public static string[] FieldsOf(EntityBase spawner) =>
            _bySpawner.TryGetValue(spawner, out Tracked? t) && t.Placed != null ? PlacedFields : RoomFields;

        static readonly string[] _triggers = { "near", "entry", "wave" };

        public static string FieldValue(EntityBase spawner, int field)
        {
            string[] names = FieldsOf(spawner);
            if (field < 0 || field >= names.Length)
            {
                return "";
            }
            _bySpawner.TryGetValue(spawner, out Tracked? t);
            PlacedEnemy? p = t?.Placed;
            RoomEdit? e = t?.Edit;
            int group = p?.Group ?? e?.Group ?? -1;
            return names[field] switch
            {
                "HEALTH" => "x" + (p?.HealthMul ?? e?.HealthMul ?? 1f).ToString("0.00"),
                "DAMAGE" => "x" + (p?.DamageMul ?? e?.DamageMul ?? 1f).ToString("0.00"),
                "COUNT" => p!.Count.ToString(),
                "LIMIT" => p!.Limit.ToString(),
                "TOTAL" => p!.Total == 0 ? "ENDLESS" : p.Total.ToString(),
                "TRIGGER" => p!.Trigger.ToUpperInvariant(),
                "RADIUS" => p!.Radius.ToString("0"),
                "WAVE" => p!.Wave.ToString(),
                "GROUP" => group < 0 ? "NONE" : group.ToString(),
                "ITEM" => p!.ItemChance < 0 ? "DEFAULT" : p.ItemChance + "%",
                "FACING" => p!.Facing.ToString("0"),
                "GATE" => group >= 0 && Current?.Gates.Any(g => g.Group == group) == true ? "ON" : "OFF",
                "REPLACE" => e?.ReplaceWith == null ? "NONE" : EnemyCatalogue.Find(e.ReplaceWith)?.Label ?? e.ReplaceWith,
                _ => "",
            };
        }

        // step a field by dir (-1 / +1); the spawner is rebuilt where it changes what spawns. Returns the spawner now
        // standing for it (rebuilt ones are new entities).
        public static EntityBase Adjust(EntityBase spawner, int field, int dir)
        {
            string[] names = FieldsOf(spawner);
            if (Current == null || field < 0 || field >= names.Length || dir == 0)
            {
                return spawner;
            }
            _bySpawner.TryGetValue(spawner, out Tracked? t);
            PlacedEnemy? p = t?.Placed;
            RoomEdit? e = null;
            if (p == null)
            {
                if (spawner is not EnemySpawnEntity own)
                {
                    return spawner;
                }
                e = EditFor(own, t);
                if (t == null)
                {
                    t = new Tracked { Spawner = spawner, Edit = e, Original = own.Data };
                    Track(t);
                }
                t.Edit = e;
            }
            bool rebuild = false;
            switch (names[field])
            {
            case "HEALTH":
                if (p != null) p.HealthMul = Step(p.HealthMul, dir, 0.25f, 0.25f, 10f);
                else e!.HealthMul = Step(e.HealthMul, dir, 0.25f, 0.25f, 10f);
                break;
            case "DAMAGE":
                if (p != null) p.DamageMul = Step(p.DamageMul, dir, 0.25f, 0f, 10f);
                else e!.DamageMul = Step(e.DamageMul, dir, 0.25f, 0f, 10f);
                break;
            case "COUNT":
                p!.Count = Math.Clamp(p.Count + dir, 1, 8);
                rebuild = true;
                break;
            case "LIMIT":
                p!.Limit = Math.Clamp(p.Limit + dir, 1, 8);
                rebuild = true;
                break;
            case "TOTAL":
                p!.Total = Math.Clamp(p.Total + dir, 0, 20);
                rebuild = true;
                break;
            case "TRIGGER":
                int i = Array.IndexOf(_triggers, p!.Trigger);
                p.Trigger = _triggers[((i < 0 ? 0 : i) + dir + _triggers.Length) % _triggers.Length];
                if (p.Trigger == "wave" && p.Wave == 0)
                {
                    p.Wave = 1;
                }
                rebuild = true;
                break;
            case "RADIUS":
                p!.Radius = Math.Clamp(p.Radius + dir * 2f, 4f, 60f);
                rebuild = true;
                break;
            case "WAVE":
                p!.Wave = Math.Clamp(p.Wave + dir, 0, 9);
                rebuild = true;
                break;
            case "GROUP":
                int oldGroup = p?.Group ?? e!.Group;
                int newGroup = Math.Clamp(oldGroup + dir, p != null ? 0 : -1, 9);
                if (p != null) p.Group = newGroup;
                else e!.Group = newGroup;
                SetUpGates();
                break;
            case "ITEM":
                p!.ItemChance = p.ItemChance < 0 && dir > 0 ? 0 : Math.Clamp(p.ItemChance + dir * 10, -1, 100);
                rebuild = true;
                break;
            case "FACING":
                p!.Facing = ((p.Facing + dir * 45f) % 360f + 360f) % 360f;
                rebuild = true;
                break;
            case "GATE":
                int g = p?.Group ?? e!.Group;
                if (g < 0)
                {
                    // a gate needs a group: the room's own spawner joins group 0
                    e!.Group = g = 0;
                }
                Gate? gate = Current.Gates.FirstOrDefault(x => x.Group == g);
                if (gate != null) Current.Gates.Remove(gate);
                else Current.Gates.Add(new Gate { Group = g });
                SetUpGates();
                break;
            case "REPLACE":
                IReadOnlyList<EnemyCatalogue.Entry> all = EnemyCatalogue.Entries;
                int at = e!.ReplaceWith == null ? -1 : all.ToList().FindIndex(x => x.Id == e.ReplaceWith);
                at += dir;
                if (at < -1) at = all.Count - 1;
                if (at >= all.Count) at = -1;
                e.ReplaceWith = at < 0 ? null : all[at].Id;
                rebuild = true;
                break;
            }
            if (rebuild)
            {
                spawner = Rebuild(t!);
            }
            Save();
            return spawner;
        }

        static float Step(float v, int dir, float step, float min, float max) =>
            MathF.Round(Math.Clamp(v + dir * step, min, max) * 100f) / 100f;

        static EntityBase Rebuild(Tracked t)
        {
            int id = t.Spawner.Id;
            RemoveLive(t.Spawner);
            EntityBase? fresh;
            bool held = false;
            if (t.Placed != null)
            {
                fresh = BuildPlaced(_scene!, t.Placed, id, out held);
            }
            else
            {
                fresh = BuildRoomOwn(_scene!, t);
            }
            if (fresh == null)
            {
                return t.Spawner;
            }
            AddToScene(fresh);
            Track(new Tracked { Spawner = fresh, Placed = t.Placed, Edit = t.Edit, Original = t.Original, Held = held });
            return fresh;
        }

        // test hook: the tracked spawners and gates now (count, held waves, gate phases)
        public static (int Tracked, int Held, int[] GatePhases) DebugState() =>
            (_tracked.Count, _tracked.Count(t => t.Held), _gates.Select(g => g.Phase).ToArray());
    }
}
