using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using MphRead;
using MphRead.Editor;
using MphRead.Formats.Collision;
using MphRead.Utility;
using MphRecomp.Import;
using OpenTK.Mathematics;

namespace MphRecomp.Arenas
{
    // First Hunt (the 2004 demo cart that came with the DS, AMFE) arenas as MPH multiplayer rooms (owner 2026-10-13: "no
    // beams, no hunters. Treat them like they're mph native maps. We create weapon pools based off mph weapon pools and
    // replace the first hunt pickups with mph pickups"). Trooper Module, Assault Cradle and Ancient Vestige are extra
    // arenas; MP5 (an unused one-mesh prototype), Regulator and the Morph Ball room are for the dev menu only.
    // Import reads the user's own cart dump (.nds) and writes one folder per arena, <import root>/fharenas/<Key>/:
    //   fh_<key>_Model.bin, _Anim.bin    the cart's room model as it is (First Hunt and MPH share the model format)
    //   fh_<key>_Collision.bin           the cart's collision rewritten as MPH "wc01" polygons, room portals kept (MphRead
    //                                    reads FH collision but its physics skips it)
    //   fh_<key>_Ent.bin                 spawns, pickups, jump pads and morph cameras, converted by MphRead
    //                                    (Repack.FhEntitiesAsMph), the pickups swapped for MPH ones (Pickup, Weapons)
    //   fh_<key>_Node.bin                a generated bot navigation graph (ArenaNav)
    //   arena.json                       what registering needs (bounds, kill height, notes)
    // Register / EnsureRegistered add them to MphRead's room table (Metadata.AddHostRoom), ids 1101-1106.
    public static class FirstHuntArena
    {
        // Model: the cart's room model/collision name; Entities: its entity file; Source: MphRead's own First Hunt room
        // entry (fog, lights); CentreGrid: the bots' nav grid through 0,0 (Assault Cradle's 2.2-wide doorways sit on the axes;
        // not Trooper Module, whose 1.2-wide ball tunnels sit on them too and drew standing bots in)
        public sealed record Def(string Key, string Model, string Entities, string Source, string Room, string InGameName, int Id, bool Dev, bool CentreGrid = false);

        public static readonly IReadOnlyList<Def> All = new[]
        {
            // MP1-MP3 out of the arena lists in every build for now (owner 2026-10-17: "can we take them out of the
            // list for now?", and not in the next release): Dev like the rest, so still startable from the dev menu. Set them
            // back to false when the owner asks for them in the lists
            new Def("MP1", "mp1", "mp1", "Level MP1", "FH TROOPER MODULE", "Trooper Module", 1101, true),
            new Def("MP2", "mp2", "mp2", "Level MP2", "FH ASSAULT CRADLE", "Assault Cradle", 1102, true, CentreGrid: true),
            new Def("MP3", "mp3", "mp3", "Level MP3", "FH ANCIENT VESTIGE", "Ancient Vestige", 1103, true),
            new Def("MP5", "mp5", "mp5", "Level MP5", "FH MP5", "MP5 (First Hunt prototype)", 1104, true),
            new Def("Regulator", "blueRoom", "regulator", "Level SP Regulator", "FH REGULATOR", "Regulator (First Hunt)", 1105, true),
            new Def("MorphBall", "e3Level", "morphBall", "Level SP Morphball", "FH MORPH BALL", "Morph Ball (First Hunt)", 1106, true),
        };

        public static Def? Find(string keyOrName) => All.FirstOrDefault(d =>
            string.Equals(d.Key, keyOrName, StringComparison.OrdinalIgnoreCase) || string.Equals(d.Room, keyOrName, StringComparison.OrdinalIgnoreCase)
            || string.Equals(d.InGameName, keyOrName, StringComparison.OrdinalIgnoreCase));

        static bool _rulesHooked;

        // First Hunt's ball rules (PlayerEntity.HostFhBallRules) on for an FH room, off for any other, on every room load:
        // wraps SceneSetup.HostExtraEntities after EchoesArena sets it (its static constructor runs first, so it can't
        // replace this)
        // (also for the whole game's levels, FirstHuntGame.Register)
        static void HookRoomRules()
        {
            if (_rulesHooked) return;
            _rulesHooked = true;
            System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(typeof(EchoesArena).TypeHandle);
            var next = SceneSetup.HostExtraEntities;
            SceneSetup.HostExtraEntities = (scene, room, entities) =>
            {
                MphRead.Entities.PlayerEntity.HostFhBallRules = All.Any(d => d.Id == room);
                return next != null ? next(scene, room, entities) : entities;
            };
        }

        public static bool IsFirstHuntRoom(string? roomName) => roomName != null && All.Any(d => d.Room == roomName);

        // a dev-menu room: in MatchArenas.All (so a match can start there) but not offered in the arena lists
        public static bool IsDevRoom(string? roomName) => roomName != null && All.Any(d => d.Dev && d.Room == roomName);

        // the cartridges this reads (First Hunt US; the European demo would be AMFP, not seen yet)
        public static bool IsFirstHunt(NdsRom rom) => rom.GameCode is "AMFE" or "AMFP";

        static string Archive(Def d) => "fh_" + d.Key.ToLowerInvariant();

        public sealed class Info
        {
            public string Key { get; set; } = "";
            public string GameCode { get; set; } = "";
            public float KillHeight { get; set; }
            public float[] Min { get; set; } = new float[3];
            public float[] Max { get; set; } = new float[3];
            public int CollisionPolygons { get; set; }
            public int Portals { get; set; }
            public int Spawns { get; set; }
            public int Items { get; set; }
            public int JumpPads { get; set; }
            public int NavNodes { get; set; }
            public int NavVersion { get; set; } = ArenaNav.Version;
            [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
            public List<int[]>? NavLinks { get; set; }
            // each pad: where it is and where its launch comes down (nav; measured again by the Tools test)
            public List<float[]> PadLandings { get; set; } = new();
            public List<string> Notes { get; set; } = new();
        }

        // ---- pickups ----

        // owner 10-13: FH's pickups become MPH's. Health, Double Damage and missiles have MPH twins; FH's ammo is
        // Universal Ammo; FH's only weapon, the Electro Lob, is the Volt Driver's ancestor. Each arena then gets an MPH
        // arena's weapon set (Weapons) at its Electro Lob spots and, where those are too few for that set, at ammo spots.
        static ItemType Pickup(FhItemType t) => t switch
        {
            FhItemType.HealthSmall => ItemType.HealthSmall,
            FhItemType.HealthBig => ItemType.HealthBig,
            FhItemType.DoubleDamage => ItemType.DoubleDamage,
            FhItemType.Missile => ItemType.MissileBig,
            FhItemType.AmmoBig => ItemType.UABig,
            FhItemType.AmmoSmall => ItemType.UASmall,
            FhItemType.ElectroLob => ItemType.VoltDriver,
            _ => ItemType.None,
        };

        // FH item spawn id -> the MPH weapon there. Modelled on MPH arenas of the same build (census of the retail arenas,
        // 10-13: one to six weapons each, at 150-300 ticks):
        static readonly Dictionary<string, Dictionary<short, ItemType>> Weapons = new()
        {
            // Trooper Module: a hub with linked side rooms, like Data Shrine, so Data Shrine's six: the Electro Lob spots
            // at the north and south ends, then the four outer ammo spots (west/east rooms), each diagonal pair alike
            ["MP1"] = new()
            {
                [11] = ItemType.VoltDriver, [12] = ItemType.Battlehammer,
                [13] = ItemType.Imperialist, [16] = ItemType.Judicator, [14] = ItemType.Magmaul, [15] = ItemType.ShockCoil,
            },
            // Assault Cradle: the biggest, on three levels, the same six: the Electro Lob spots (lower and upper deck),
            // then the four ammo spots round the upper ring, diagonal pairs alike
            ["MP2"] = new()
            {
                [11] = ItemType.VoltDriver, [23] = ItemType.Judicator,
                [38] = ItemType.Battlehammer, [41] = ItemType.Magmaul, [39] = ItemType.ShockCoil, [40] = ItemType.Imperialist,
            },
            // Ancient Vestige: one small room and no weapon of its own, like Combat Hall: one weapon and the affinity
            // weapon, at two of its four big ammo spots (the north and south ends)
            ["MP3"] = new() { [1] = ItemType.Magmaul, [4] = ItemType.AffinityWeapon },
        };

        static bool IsWeapon(ItemType t) => t is ItemType.VoltDriver or ItemType.Battlehammer or ItemType.Imperialist or ItemType.Judicator
            or ItemType.Magmaul or ItemType.ShockCoil or ItemType.OmegaCannon or ItemType.AffinityWeapon;

        // respawn ticks kept in the range MPH's arenas use for that pickup (census 10-13); FH's own where it fits
        static ushort Respawn(ItemType t, ushort fh) => t switch
        {
            _ when IsWeapon(t) => 300,
            ItemType.DoubleDamage => (ushort)Math.Clamp((int)fh, 1200, 6000),
            ItemType.HealthSmall or ItemType.HealthBig => (ushort)Math.Clamp((int)fh, 300, 900),
            ItemType.MissileBig => (ushort)Math.Clamp((int)fh, 150, 900),
            _ => (ushort)Math.Clamp((int)fh, 150, 900),
        };

        // ---- import ----

        // write one arena from the cart into dir (its item folder)
        public static Info Import(NdsRom rom, Def def, string dir, Action<string>? log = null)
        {
            byte[] Need(string path) => rom.Get(path) ?? throw new InvalidDataException($"{path} is not on this cartridge");
            var info = new Info { Key = def.Key, GameCode = rom.GameCode };
            string arc = Archive(def);
            Directory.CreateDirectory(dir);

            // collision: polygons, portals and triangles for the bots' navigation
            (List<CollisionDataEditor> editors, List<Portal> portals, List<(Vector3, Vector3, Vector3)> tris) =
                ReadCollision(Need($"data/levels/collision/{def.Model}_Collision.bin"), info);
            info.CollisionPolygons = editors.Count;
            info.Portals = portals.Count;
            var ray = new ArenaRaycaster(tris);
            info.Min = new[] { ray.Min.X, ray.Min.Y, ray.Min.Z };
            info.Max = new[] { ray.Max.X, ray.Max.Y, ray.Max.Z };
            // FH's rooms have no kill height (0 in its level table); a little under the lowest collision, as the Echoes arenas
            info.KillHeight = ray.Min.Y - 5;

            // entities: MphRead's FH -> MPH conversion, then MPH pickups
            string fhEnt = Path.Combine(dir, "source_Ent.bin");
            File.WriteAllBytes(fhEnt, Need($"data/levels/entities/{def.Entities}_Ent.bin"));
            List<EntityEditorBase> converted;
            var fhItems = new Dictionary<short, FhItemType>();
            try
            {
                _ = Paths.AllPaths;   // MphRead's path table (an absolute path is read where it is)
                converted = Repack.FhEntitiesAsMph(fhEnt);
                foreach (Entity e in Read.GetEntitiesFromPath(fhEnt, -1, firstHunt: true))
                {
                    if (e is Entity<FhItemSpawnEntityData> it) fhItems[e.EntityId] = it.Data.ItemType;
                }
            }
            finally
            {
                File.Delete(fhEnt);
            }
            var ents = new List<EntityEditorBase>();
            var dropped = new Dictionary<string, int>();
            Weapons.TryGetValue(def.Key, out Dictionary<short, ItemType>? weapons);
            foreach (EntityEditorBase e in converted)
            {
                switch (e)
                {
                case ItemSpawnEntityEditor item:
                    {
                        FhItemType fh = fhItems.TryGetValue(item.Id, out FhItemType t) ? t : FhItemType.AmmoSmall;
                        ItemType mph = weapons != null && weapons.TryGetValue(item.Id, out ItemType w) ? w : Pickup(fh);
                        if (mph == ItemType.None) { dropped[$"FH {fh}"] = dropped.GetValueOrDefault($"FH {fh}") + 1; continue; }
                        item.ItemType = mph;
                        item.SpawnInterval = Respawn(mph, item.SpawnInterval);
                        ents.Add(item);
                        break;
                    }
                case PlayerSpawnEntityEditor or JumpPadEntityEditor or MorphCameraEntityEditor:
                    ents.Add(e);
                    break;
                default:
                    // doors, platforms, enemies, triggers and the area volume: First Hunt's own (models, rules) or for its
                    // single-player levels; an MPH-style arena has none of them
                    dropped[e.Type.ToString()] = dropped.GetValueOrDefault(e.Type.ToString()) + 1;
                    break;
                }
            }
            if (dropped.Count > 0) info.Notes.Add("left out: " + string.Join(", ", dropped.Select(kv => $"{kv.Key} x{kv.Value}")));
            info.Spawns = ents.Count(e => e is PlayerSpawnEntityEditor);
            info.Items = ents.Count(e => e is ItemSpawnEntityEditor);
            info.Notes.Add("pickups: " + string.Join(", ", ents.OfType<ItemSpawnEntityEditor>().GroupBy(i => i.ItemType).OrderBy(g => g.Key)
                .Select(g => $"{g.Key} x{g.Count()}")));
            List<JumpPadEntityEditor> pads = ents.OfType<JumpPadEntityEditor>().ToList();
            foreach (JumpPadEntityEditor pad in pads) pad.Unused28 |= KeepPushBit;
            info.JumpPads = pads.Count;

            // bot navigation over the converted collision, anchored at the spawns, the pads' flights as one-way links
            var jumps = new List<(Vector3 From, Vector3 To)>();
            foreach (JumpPadEntityEditor pad in pads)
            {
                // a morph ball pad: bots (on foot) walk over it, so it's floor to them, not a flight
                if (!pad.TriggerFlags.HasFlag(MphRead.Entities.TriggerFlags.PlayerBiped)) continue;
                Vector3? land = PadLanding(ray, pad, info.KillHeight, pads);
                // a lift (straight up, back down onto itself): the player steers off at the top, so the flight is to the
                // ledge it rises past
                if (land is Vector3 back && (back.Xz - pad.Position.Xz).Length < 1.5f) land = LiftLedge(ray, pad);
                info.PadLandings.Add(land is Vector3 l ? new[] { pad.Position.X, pad.Position.Y, pad.Position.Z, l.X, l.Y, l.Z }
                    : new[] { pad.Position.X, pad.Position.Y, pad.Position.Z });
                if (land is Vector3 to) jumps.Add((pad.Position, to));
                else info.Notes.Add($"pad {pad.Id} at ({pad.Position.X:0.0}, {pad.Position.Y:0.0}, {pad.Position.Z:0.0}): no landing found for the bots' map");
            }
            List<Vector3> anchors = ents.OfType<PlayerSpawnEntityEditor>().Select(e => e.Position).ToList();
            ArenaNav.Graph nav = ArenaNav.Build(ray, anchors, info.KillHeight, jumps, gridOrigin: def.CentreGrid ? Vector2.Zero : null, echoesMoves: false, padReach: PadReach);
            info.NavNodes = nav.Nodes.Count;
            info.NavLinks = nav.Links.Count > 0 ? nav.Links : null;
            info.NavVersion = ArenaNav.Version;
            // a single-player room (the Morph Ball room has 1 spawn): a second spawn on the walkable floor furthest from the
            // first, since a match (and the dev launcher's list, MatchArenas.Supports) needs 2
            if (ents.OfType<PlayerSpawnEntityEditor>().ToList() is [PlayerSpawnEntityEditor only] && nav.Nodes.Count > 0)
            {
                Vector3 far = nav.Nodes.MaxBy(n => (n - only.Position).LengthSquared);
                if ((far - only.Position).Length > 2)
                {
                    Vector3 look = new(only.Position.X - far.X, 0, only.Position.Z - far.Z);
                    ents.Add(new PlayerSpawnEntityEditor
                    {
                        Id = (short)(ents.Max(e => e.Id) + 1), LayerMask = only.LayerMask, NodeName = only.NodeName, Position = far,
                        Up = Vector3.UnitY, Facing = look.LengthSquared > 0.01f ? look.Normalized() : only.Facing,
                        Availability = only.Availability, Active = only.Active, TeamIndex = only.TeamIndex,
                    });
                    info.Spawns++;
                    info.Notes.Add($"added a spawn at ({far.X:0.0}, {far.Y:0.0}, {far.Z:0.0}) (the room has one)");
                }
            }

            File.WriteAllBytes(Path.Combine(dir, $"{arc}_Model.bin"), Need($"data/levels/models/{def.Model}_Model.bin"));
            File.WriteAllBytes(Path.Combine(dir, $"{arc}_Anim.bin"), Need($"data/levels/models/{def.Model}_Anim.bin"));
            File.WriteAllBytes(Path.Combine(dir, $"{arc}_Collision.bin"), RepackCollision.PackMphCollision(editors, portals));
            File.WriteAllBytes(Path.Combine(dir, $"{arc}_Ent.bin"), Repack.PackEntities(ents));
            File.WriteAllBytes(Path.Combine(dir, $"{arc}_Node.bin"), ArenaNav.Write(nav));
            File.WriteAllText(Path.Combine(dir, "arena.json"), JsonSerializer.Serialize(info, new JsonSerializerOptions { WriteIndented = true }));
            log?.Invoke($"{def.InGameName}: {info.CollisionPolygons} collision polygons, {info.Portals} portals, {info.Spawns} spawns, "
                + $"{info.Items} items, {info.JumpPads} pads, {info.NavNodes} nav nodes, kill height {info.KillHeight:0.0}"
                + string.Concat(info.Notes.Select(n => "\n  note: " + n)));
            return info;
        }

        // First Hunt collision: points, planes, edge vectors and polygons ("data"), the first PortalCount data being the
        // portals'. Two header layouts: 72 bytes (every room the game uses) and an older 64-byte one with one table pair
        // fewer (MP5 only: its points start at 64), which MphRead's reader doesn't know. Polygons become MPH editors as
        // MphRead's own FH -> MPH repack builds them (RepackCollision.GetEditors(FhCollisionInfo)); portals keep their
        // 4 corners, side planes and plane, always on (layer mask 4) as MphRead reads FH portals.
        // a First Hunt collision file rewritten as MPH collision ("wc01", what MphRead's physics runs on), as the arenas'
        // import does; the full game's import (FirstHuntGame) writes one for every level. notes: anything left out
        public static byte[] ConvertCollision(byte[] fhCollision, List<string>? notes = null)
        {
            var info = new Info();
            (List<CollisionDataEditor> editors, List<Portal> portals, _) = ReadCollision(fhCollision, info);
            notes?.AddRange(info.Notes);
            return RepackCollision.PackMphCollision(editors, portals);
        }

        static (List<CollisionDataEditor>, List<Portal>, List<(Vector3, Vector3, Vector3)>) ReadCollision(byte[] b, Info info)
        {
            int I(int at) => BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(at, 4));
            int H(int at) => BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(at, 2));
            float Fx(int at) => I(at) / 4096f;
            int pointCount = I(0), pointOff = I(4), planeCount = I(8), planeOff = I(12), vecCount = I(16), vecOff = I(20);
            int dataCount = H(24), dataOff = I(28);
            bool old = pointOff == 64;
            int portalCount = I(old ? 56 : 64), portalOff = I(old ? 60 : 68);
            var points = new Vector3[pointCount];
            for (int i = 0; i < pointCount; i++) points[i] = new Vector3(Fx(pointOff + i * 12), Fx(pointOff + i * 12 + 4), Fx(pointOff + i * 12 + 8));
            var planes = new Vector4[planeCount];
            for (int i = 0; i < planeCount; i++)
            {
                int o = planeOff + i * 16;
                planes[i] = new Vector4(Fx(o), Fx(o + 4), Fx(o + 8), Fx(o + 12));
            }
            (int P1, int P2, int Plane) Vec(int i) => (H(vecOff + i * 6), H(vecOff + i * 6 + 2), H(vecOff + i * 6 + 4));
            if (vecOff + vecCount * 6 > b.Length || dataOff + dataCount * 6 > b.Length) throw new InvalidDataException("collision tables run past the file");

            var editors = new List<CollisionDataEditor>();
            var tris = new List<(Vector3, Vector3, Vector3)>();
            int offPlane = 0;
            for (int i = portalCount; i < dataCount; i++)
            {
                int o = dataOff + i * 6;
                int planeIndex = H(o), count = H(o + 2), start = H(o + 4);
                if (count < 3) continue;
                Vector4 plane = planes[planeIndex];
                Vector3 normal = plane.Xyz;
                var ed = new CollisionDataEditor { LayerMask = (ushort)(4 | PrimaryAxis(normal)), Plane = new Vector4(normal, plane.W) };
                for (int j = 0; j < count; j++) ed.Points.Add(points[Vec(start + j).P2]);
                if (MathF.Abs(Vector3.Dot(normal, ed.Points[0]) - plane.W) > 0.05f) offPlane++;
                editors.Add(ed);
                for (int j = 1; j + 1 < count; j++) tris.Add((ed.Points[0], ed.Points[j], ed.Points[j + 1]));
            }
            if (offPlane > 0) info.Notes.Add($"{offPlane} polygons off their plane by more than 0.05");
            var portals = new List<Portal>();
            for (int i = 0; i < portalCount; i++)
            {
                int o = portalOff + i * 96;
                string S(int at, int len) => Encoding.ASCII.GetString(b, at, len).Split('\0')[0];
                string node1 = S(o + 40, 16), node2 = S(o + 56, 16);
                var plane = new Vector4(Fx(o + 72), Fx(o + 76), Fx(o + 80), Fx(o + 84));
                int count = H(o + 88), start = H(o + 90);
                var pts = new List<Vector3>();
                var sides = new List<Vector4>();
                for (int j = 0; j < count; j++)
                {
                    (_, int p2, int pl) = Vec(start + j);
                    pts.Add(points[p2]);   // Point2Index: MPH's corner order (MphRead Portal)
                    sides.Add(planes[pl]);
                }
                if (count != 4)
                {
                    info.Notes.Add($"portal {node1}/{node2} has {count} corners (MPH portals have 4): left out");
                    continue;
                }
                portals.Add(new Portal(node1, node2, pts, sides, plane));
            }
            return (editors, portals, tris);
        }

        static int PrimaryAxis(Vector3 n)
        {
            float x = MathF.Abs(n.X), y = MathF.Abs(n.Y), z = MathF.Abs(n.Z);
            if (y > x && y >= z) return 1;
            if (z > x && z > y) return 2;
            return 0;
        }

        // a pad's flight, for the bots' map: the launch (BeamVector to the pad's speed, per frame) straight on while the
        // control lock holds gravity off (MPH: ControlLockTime 30 Hz ticks), then falling; where it first comes down on
        // a floor. PadGravity per 60 Hz frame; both checked against MphRead's own player physics by the Tools test
        public static float PadGravity { get; set; } = 0.0185f;

        // the highest floor a lift's rise reaches within a few steps of its shaft, with room to stand and a clear way over
        // to it at the top (nearest first on a tie); null = none
        public static Vector3? LiftLedge(ArenaRaycaster ray, JumpPadEntityEditor pad)
        {
            Matrix3 m = new(MphRead.Entities.EntityBase.GetTransformMatrix(pad.Facing, pad.Up));
            float vy = (pad.BeamVector * m).Normalized().Y * pad.Speed;
            if (vy <= 0) return null;
            // the rise, plus what a cooldown-0 pad adds while the player is still in its trigger (measured: 7.3-8.8 for 0.5)
            float top = pad.Position.Y + vy * vy / (2 * PadGravity) + vy * pad.ControlLockTime + 1.5f;
            Vector3? best = null;
            for (float r = 1.5f; r <= 6f; r += 0.5f)
            {
                for (int a = 0; a < 24; a++)
                {
                    float t = a * MathF.PI / 12;
                    var col = new Vector3(pad.Position.X + r * MathF.Cos(t), 0, pad.Position.Z + r * MathF.Sin(t));
                    var from = new Vector3(col.X, top - 0.3f, col.Z);
                    if (ray.Cast(from, new Vector3(col.X, pad.Position.Y + 1f, col.Z)) is not { } h || h.Normal.Y < 0.7f) continue;
                    var at = new Vector3(col.X, from.Y - (from.Y - (pad.Position.Y + 1f)) * h.T, col.Z);
                    if (ray.Cast(at + Vector3.UnitY * 0.05f, at + Vector3.UnitY * 2f) != null) continue;
                    var over = new Vector3(pad.Position.X, at.Y + 1f, pad.Position.Z);
                    if (ray.Cast(over, at + Vector3.UnitY * 1f) != null) continue;
                    if (best is not Vector3 b || at.Y > b.Y + 0.25f) best = at;
                }
            }
            return best;
        }

        // First Hunt's pads keep their push against a wall (JumpPadEntity.HostKeepPushBit; in the pad's Unused28, which MPH
        // leaves 0 or 2 and the Echoes cannons mark with ArenaCannons.SameArcBit)
        public const uint KeepPushBit = 0x40000000;

        // a pad this high over the floor around it is reached by a jump into its trigger (-fharena liftjump: Assault Cradle's
        // centre lifts, discs 2.9 up, from 1.2-2.5 away; a jump lifts her 2.54 and her body reaches 1.1 over her position)
        const float PadReach = 3.2f;

        // where a pad's flight comes down, on MphRead's MPH physics with the First Hunt push (KeepPushBit): no gravity for
        // the lock, then PadGravity a frame; a wall stops only the way into it (the push stays, so the player slides up and
        // over a lip); flying into another pad (biped-triggered) takes that pad's flight. Null = out of the arena / none.
        public static Action<int, Vector3>? Trace { get; set; } // test hook: each frame of PadLanding's flight

        public static Vector3? PadLanding(ArenaRaycaster ray, JumpPadEntityEditor pad, float killHeight, IReadOnlyList<JumpPadEntityEditor>? chain = null, int depth = 0)
        {
            if (pad.BeamVector.LengthSquared < 1e-6f) return null;
            Matrix3 m = new(MphRead.Entities.EntityBase.GetTransformMatrix(pad.Facing, pad.Up));
            Vector3 v = (pad.BeamVector * m).Normalized() * pad.Speed;
            Vector3 up = pad.Up.LengthSquared > 1e-6f ? pad.Up.Normalized() : Vector3.UnitY;
            Vector3 p = pad.Position + up * 0.5f;
            int lockFrames = pad.ControlLockTime; // 30 Hz frames, as the speeds and PadGravity
            for (int f = 0; f < 1200; f++)
            {
                if (f >= lockFrames) v.Y -= PadGravity;
                Vector3 next = p + v;
                if (ray.Cast(p, next) is { } hit)
                {
                    Vector3 at = p + (next - p) * hit.T;
                    if (hit.Normal.Y >= 0.7f && v.Y <= 0) return at;
                    if (hit.Normal.Y >= 0.7f) { p = at + Vector3.UnitY * 0.05f; continue; }   // a floor met on the way up
                    if (hit.Normal.Y <= -0.7f) { v.Y = 0; p = at - Vector3.UnitY * 0.05f; continue; } // a ceiling: the rise ends
                    // a wall: slide along it, the push kept
                    Vector3 n = hit.Normal;
                    Vector3 rest = (next - at) - n * Vector3.Dot(next - at, n);
                    Vector3 from = at + n * 0.02f;
                    p = ray.Cast(from, from + rest) == null ? from + rest : from;
                }
                else
                {
                    p = next;
                }
                Trace?.Invoke(f, p);
                if (p.Y < killHeight) return null;
                if (chain != null && depth < 4 && f > 4)
                {
                    foreach (JumpPadEntityEditor q in chain)
                    {
                        if (q != pad && q.TriggerFlags.HasFlag(MphRead.Entities.TriggerFlags.PlayerBiped) && (q.Position - p).Length < 2f) // (the player's body reaches a trigger before this point does)
                        {
                            return PadLanding(ray, q, killHeight, chain, depth + 1);
                        }
                    }
                }
            }
            return null;
        }

        // ---- register ----

        public const string ImportKind = "fharenas";

        public static string ItemDir(string importRoot, Def d) => Path.Combine(importRoot, ImportKind, d.Key);

        static bool HasArena(string dir, Def d) => File.Exists(Path.Combine(dir, $"{Archive(d)}_Model.bin")) && File.Exists(Path.Combine(dir, "arena.json"));

        // add the arenas imported under importRoot (EchoesArena.ImportRoot) that are usable (ImportedContent: imported,
        // up to date; a dev build also takes a folder copied in by hand) to MphRead's room table; returns how many
        public static int Register(string importRoot)
        {
            int count = 0;
            // First Hunt clears the screen to the fog colour where the room says so (MP1's white haze); MphRead does that only
            // for rooms it loads as First Hunt's
            MphRead.Scene.HostClearFogRoom ??= m => IsFirstHuntRoom(m.Name);
            MphRead.Entities.JumpPadEntity.HostKeepPushBit = KeepPushBit;
            HookRoomRules();
            foreach (Def d in All)
            {
                string dir = ItemDir(importRoot, d);
                if (!HasArena(dir, d) || !ImportedContent.Usable(importRoot, ImportKind, d.Key)) continue;
                // a folder the app can't read or parse (e.g. copied in by hand without read access) is skipped, never fatal:
                // this runs at startup from the arena lists
                try
                {
                    Info? info = JsonSerializer.Deserialize<Info>(File.ReadAllText(Path.Combine(dir, "arena.json")));
                    if (info == null) continue;
                    Metadata.AddHostRoom(Room(d, info, dir));
                    ArenaNav.SetRoom(d.Id, new EchoesArena.Info { NavVersion = info.NavVersion, NavLinks = info.NavLinks });
                    count++;
                }
                catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or KeyNotFoundException)
                {
                    continue;
                }
            }
            return count;
        }

        static string? _registeredStamp;

        // register once the import root is known (MatchArenas.All calls this), and again when an import there changed
        static long WriteTicks(string path)
        {
            try
            {
                return File.GetLastWriteTimeUtc(path).Ticks;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return -1;
            }
        }

        public static void EnsureRegistered()
        {
            string? imports = EchoesArena.ImportRoot;
            string stamp = imports ?? "";
            if (imports != null)
            {
                foreach (Def d in All) stamp += "|" + WriteTicks(Path.Combine(ItemDir(imports, d), "arena.json"));
                stamp += "|" + WriteTicks(Path.Combine(imports, "import", "fh.json"));
            }
            if (stamp == _registeredStamp) return;
            foreach (Def d in All)
            {
                if (imports != null && HasArena(ItemDir(imports, d), d) && ImportedContent.Usable(imports, ImportKind, d.Key)) continue;
                Metadata.RemoveHostRoom(d.Room);
                ArenaNav.SetRoom(d.Id, null);
            }
            if (imports != null) Register(imports);
            _registeredStamp = stamp;
        }

        static RoomMetadata Room(Def d, Info i, string folder)
        {
            static int Fx(float v) => (int)MathF.Round(v * 4096);
            RoomMetadata fh = Metadata.RoomMetadata[d.Source];
            var min = new Vector3(i.Min[0], i.Min[1], i.Min[2]);
            var max = new Vector3(i.Max[0], i.Max[1], i.Max[2]);
            float far = MathF.Max(fh.FarClip, (max - min).Length + 50);
            string arc = Archive(d);
            return new RoomMetadata(
                id: d.Id,
                name: d.Room,
                inGameName: d.InGameName,
                archive: arc,
                modelPath: $"{arc}_Model.bin",
                animationPath: $"{arc}_Anim.bin",
                collisionPath: $"{arc}_Collision.bin",
                texturePath: null,
                entityPath: $"{arc}_Ent.bin",
                nodePath: $"{arc}_Node.bin",
                roomNodeName: null,
                battleTimeLimit: 10 * 1800,
                timeLimit: 4 * 1800,
                pointLimit: 0,
                nodeLayer: 0,
                fogEnabled: fh.FogEnabled,
                clearFog: fh.ClearFog,
                fogColor: fh.FogColor,
                fogSlope: fh.FogSlope,
                fogOffset: (ushort)fh.FogOffset,
                light1Color: fh.Light1Color,
                light1Vector: fh.Light1Vector,
                light2Color: fh.Light2Color,
                light2Vector: fh.Light2Vector,
                farClip: Fx(far),
                killHeight: Fx(i.KillHeight),
                size: RoomSize.Large,
                cameraMin: min - new Vector3(10),
                cameraMax: max + new Vector3(10),
                playerMin: new Vector3(-300),
                playerMax: new Vector3(300),
                multiplayer: true,
                hostFolder: folder);
        }
    }
}
