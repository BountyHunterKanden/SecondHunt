using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using MphRead;
using MphRead.Editor;
using MphRead.Formats.Collision;
using MphRead.Utility;
using MphRecomp.Import.Retro;
using OpenTK.Mathematics;

namespace MphRecomp.Arenas
{
    // Metroid Prime 2: Echoes multiplayer arenas as MPH multiplayer rooms (docs/MP2_MULTIPLAYER_IMPORT.md). Import reads
    // one arena from the user's own disc (Metroid6.pak) and writes MphRead's own room files next to the ROM's, under
    // the MPH file system folder:
    //   _archives/<archive>/<archive>_Model.bin, _Anim.bin    a DS stand-in model (flat colours from Echoes' lightmaps)
    //   _archives/<archive>/<archive>_Collision.bin           the area collision as MPH "wc01" polygons
    //   _archives/<archive>/arena.json                        what Register needs (scale, kill height, bounds)
    //   levels/entities/<archive>_Ent.bin                     spawns + pickups converted from Echoes' script objects
    //   levels/nodeData/<archive>_Node.bin                    a generated bot navigation graph
    // Register adds every imported arena to MphRead's room table (Metadata.AddHostRoom).
    public static class EchoesArena
    {
        public sealed record Def(string Key, uint Mrea, string Room, string InGameName, string Archive, int Id);

        public static readonly IReadOnlyList<Def> All = new[]
        {
            new Def("M01_SidehopperStation", 0x02690de1, "ECHOES SIDEHOPPER STATION", "Sidehopper Station", "echoes_m01", 1001),
            new Def("M02_Spires", 0x09749b2f, "ECHOES SPIRES", "Spires", "echoes_m02", 1002),
            new Def("M03_CrossfireChaos", 0x44d34a2e, "ECHOES CROSSFIRE CHAOS", "Crossfire Chaos", "echoes_m03", 1003),
            new Def("M04_Pipeline", 0x845dcf93, "ECHOES PIPELINE", "Pipeline", "echoes_m04", 1004),
            new Def("M05_SpiderComplex", 0xe7095052, "ECHOES SPIDER COMPLEX", "Spider Complex", "echoes_m05", 1005),
            new Def("M06_ShootingGallery", 0xd97a600b, "ECHOES SHOOTING GALLERY", "Shooting Gallery", "echoes_m06", 1006),
        };

        public static Def? Find(string keyOrName) => All.FirstOrDefault(d =>
            string.Equals(d.Key, keyOrName, StringComparison.OrdinalIgnoreCase) || string.Equals(d.Room, keyOrName, StringComparison.OrdinalIgnoreCase)
            || string.Equals(d.InGameName, keyOrName, StringComparison.OrdinalIgnoreCase) || d.Key.EndsWith(keyOrName, StringComparison.OrdinalIgnoreCase));

        // hunter height (1.6 units, -0.5..1.1) over Echoes Samus's (2.7 m, TweakPlayer2): the owner's "match the hunters"
        public const float DefaultScale = 1.6f / 2.7f;

        public sealed class Info
        {
            public string Key { get; set; } = "";
            public float Scale { get; set; }
            public float KillHeight { get; set; }
            public float[] Min { get; set; } = new float[3];
            public float[] Max { get; set; } = new float[3];
            public int CollisionPolygons { get; set; }
            public int ModelTriangles { get; set; }
            public int Spawns { get; set; }
            public int Items { get; set; }
            public int NavNodes { get; set; }
            public int HdTriangles { get; set; }
            public List<ArenaCannons.Cannon> Cannons { get; set; } = new();
            public List<string> Notes { get; set; } = new();
        }

        static string ArchiveDir(string root, Def d) => Path.Combine(root, "_archives", d.Archive);
        static string ModelFile(string root, Def d) => Path.Combine(ArchiveDir(root, d), $"{d.Archive}_Model.bin");

        // ---- import ----

        // Echoes player items (PlayerItem enum) the arenas hand out
        const uint HealthRefill = 0x29, PowerBomb = 0x2B, Missile = 0x2C, Invisibility = 0x39, AmpDamage = 0x3A, Invincibility = 0x3B;
        const uint PickupItemId = 0xA02EF0C4, PickupAmountId = 0x94AF1445;
        const uint NeedSkyId = 0x95D4BEE7, OverrideSkyId = 0xD208C9FA;   // AreaAttributes
        // MPH weapons the weapon crates hand out in turn (Echoes' crates give beams; tier A of the plan keeps MPH weapons)
        static readonly ItemType[] CrateWeapons = { ItemType.Battlehammer, ItemType.Imperialist, ItemType.Judicator,
            ItemType.Magmaul, ItemType.ShockCoil, ItemType.VoltDriver };

        public static Info Import(Pak metroid6, Def def, string root, float scale = DefaultScale, Action<string>? log = null)
        {
            var info = new Info { Key = def.Key, Scale = scale };
            Mrea area = Mrea.Read(metroid6.Get(def.Mrea));
            List<Mrea.ScriptObject> scripts = area.ReadScripts();
            Vector3 Map(float x, float y, float z) => new Vector3(x, z, -y) * scale;   // Echoes Z up -> MPH Y up, metres -> units

            // collision
            Mrea.Collision col = area.ReadCollision();
            var tris = new List<(Vector3 A, Vector3 B, Vector3 C)>();
            var editors = new List<CollisionDataEditor>();
            for (int t = 0; t < col.TriangleCount; t++)
            {
                Vector3 V(int k) { int i = col.Triangles[3 * t + k]; return Map(col.Vertices[3 * i], col.Vertices[3 * i + 1], col.Vertices[3 * i + 2]); }
                Vector3 a = V(0), b = V(1), c = V(2);
                Vector3 n = Vector3.Cross(b - a, c - a);
                if (n.LengthSquared < 1e-10f) continue;
                n.Normalize();
                ulong flags = col.TriangleFlags[t];
                var ed = new CollisionDataEditor { Plane = new Vector4(n, Vector3.Dot(n, a)), LayerMask = (ushort)(4 | PrimaryAxis(n)) };
                ed.Points.Add(a); ed.Points.Add(b); ed.Points.Add(c);
                ed.Terrain = TerrainOf(Mrea.Collision.SurfaceOf(flags));
                if ((flags & (1UL << (int)Mrea.Surface.ShootThru)) != 0) ed.Beams = false;
                editors.Add(ed);
                tris.Add((a, b, c));
            }
            info.CollisionPolygons = editors.Count;
            var ray = new ArenaRaycaster(tris);
            info.Min = new[] { ray.Min.X, ray.Min.Y, ray.Min.Z };
            info.Max = new[] { ray.Max.X, ray.Max.Y, ray.Max.Z };

            // kill height: the top of an arena-wide "Death Trigger" box, else a little under the lowest collision
            info.KillHeight = ray.Min.Y - 5;
            foreach (Mrea.ScriptObject o in scripts.Where(o => o.Type == "TRGR" && o.Active))
            {
                float[] x = o.Transform;
                string nm = o.Name.ToLowerInvariant();
                if ((nm.Contains("death") || nm.Contains("kill")) && x[6] > 100)
                {
                    info.KillHeight = (x[2] + x[8] / 2) * scale;
                }
            }

            // entities
            var ents = new List<EntityEditorBase>();
            var used = new List<Vector3>();
            bool Fresh(Vector3 p) { if (used.Any(u => (u - p).LengthSquared < 0.25f)) return false; used.Add(p); return true; }
            Vector3 OnFloor(Vector3 p) => ray.FloorBelow(p + Vector3.UnitY * 0.3f, 3f) ?? p;
            short id = 0;
            foreach (Mrea.ScriptObject o in scripts.Where(o => o.Type == "SPWN" && o.Active))
            {
                float[] x = o.Transform;
                Vector3 p = OnFloor(Map(x[0], x[1], x[2]));
                if (!Fresh(p)) continue;
                float yaw = MathHelper.DegreesToRadians(x[5]);
                Vector3 facing = Map(-MathF.Sin(yaw), MathF.Cos(yaw), 0).Normalized();   // Echoes faces +Y, turned by Z
                ents.Add(new PlayerSpawnEntityEditor { Id = id++, LayerMask = 0xFFFF, NodeName = ArenaModelWriter.RoomNode,
                    Position = p, Up = Vector3.UnitY, Facing = facing, Availability = 0, Active = true, TeamIndex = -1 });
                info.Spawns++;
            }
            int crate = 0;
            foreach (Mrea.ScriptObject o in scripts.Where(o => o.Active && (o.Type == "PCKP" || o.Type == "ACTR" && o.Name.Contains("Weapon Crate"))))
            {
                ItemType item;
                ushort interval = 450;                               // Echoes pickups respawn in 15 s
                if (o.Type == "ACTR")
                {
                    item = CrateWeapons[crate % CrateWeapons.Length];
                    interval = 300;
                }
                else
                {
                    uint what = o.Root.Find(PickupItemId)?.U32 ?? uint.MaxValue;
                    int amount = o.Root.Find(PickupAmountId)?.I32 ?? 0;
                    item = what switch
                    {
                        HealthRefill => amount >= 30 ? ItemType.HealthMedium : ItemType.HealthSmall,
                        Missile => ItemType.MissileSmall,
                        AmpDamage => ItemType.DoubleDamage,
                        Invisibility => ItemType.Cloak,
                        _ => ItemType.None
                    };
                    if (what == AmpDamage) interval = 1800;          // Spider Complex's Quad Damage: 60 s
                    if (item == ItemType.None)
                    {
                        if (what is PowerBomb or Invincibility) info.Notes.Add($"pickup '{o.Name}' (item {what:x}) has no MPH equivalent yet");
                        continue;
                    }
                }
                float[] x = o.Transform;
                Vector3 p = OnFloor(Map(x[0], x[1], x[2]));
                if (!Fresh(p)) continue;
                if (o.Type == "ACTR") crate++;
                ents.Add(new ItemSpawnEntityEditor { Id = id++, LayerMask = 0xFFFF, NodeName = ArenaModelWriter.RoomNode,
                    Position = p, Up = Vector3.UnitY, Facing = Vector3.UnitZ, ParentId = 0xFFFF, ItemType = item, Enabled = true,
                    HasBase = false, AlwaysActive = false, MaxSpawnCount = 0, SpawnInterval = interval, SpawnDelay = 0, NotifyEntityId = -1 });
                info.Items++;
            }
            // orb cannons -> jump pads (launch solved below, once the arena loads)
            List<ArenaCannons.Cannon> cannons = ArenaCannons.Find(scripts, ray, Map, scale, info.KillHeight, info.Notes);
            foreach (ArenaCannons.Cannon c in cannons) c.EntityId = id++;
            info.Cannons = cannons;

            // bot navigation over the converted collision, anchored at the spawns
            List<Vector3> anchors = ents.OfType<PlayerSpawnEntityEditor>().Select(e => e.Position).ToList();
            ArenaNav.Graph Nav() => ArenaNav.Build(ray, anchors, info.KillHeight, cannons.Where(c => !c.Dropped).Select(c => (c.PadV, c.TargetV)).ToList());
            ArenaNav.Graph nav = Nav();
            info.NavNodes = nav.Nodes.Count;

            // stand-in model: the render mesh, each corner coloured by its lightmap texel times its texture's mean colour
            // the arena's own look for the device (CampaignArena.cs draws it; the stand-in stays as the fallback)
            try
            {
                // the sky: AreaAttributes' NeedSky + OverrideSky (Pipeline has none; none of the six uses the world's default)
                uint sky = uint.MaxValue;
                if (scripts.FirstOrDefault(o => o.Type == "REAA") is { } reaa && reaa.Root.Find(NeedSkyId)?.Bool == true
                    && reaa.Root.Find(OverrideSkyId) is { Data.Length: >= 4 } os)
                {
                    sky = os.U32;
                }
                ArenaHdWriter.Result hd = ArenaHdWriter.Write(area, metroid6, Map, Path.Combine(ArchiveDir(root, def), ArenaHdWriter.Dir), sky);
                info.HdTriangles = hd.Triangles;
                info.Notes.Add($"HD look: {hd.Triangles} triangles in {hd.Batches} batches, {hd.Materials} materials, {hd.Textures} textures"
                    + (hd.Undecoded > 0 ? $" ({hd.Undecoded} not decoded)" : "") + $", mesh {hd.Bytes / 1024} KB"
                    + (hd.SkyTriangles > 0 ? $", sky {sky:x8} ({hd.SkyTriangles} triangles)" : sky != uint.MaxValue ? $", sky {sky:x8} NOT found" : ", no sky"));
            }
            catch (Exception ex)
            {
                info.Notes.Add($"HD look not written: {ex.Message}");
            }
            List<ArenaModelWriter.Corner> corners = ModelCorners(area, metroid6, Map, info);
            info.ModelTriangles = corners.Count / 3;
            (byte[] model, byte[] anim) = ArenaModelWriter.Write(def.Archive, corners);

            string dir = ArchiveDir(root, def);
            Directory.CreateDirectory(dir);
            Directory.CreateDirectory(Path.Combine(root, "levels", "entities"));
            Directory.CreateDirectory(Path.Combine(root, "levels", "nodeData"));
            File.WriteAllBytes(ModelFile(root, def), model);
            File.WriteAllBytes(Path.Combine(dir, $"{def.Archive}_Anim.bin"), anim);
            File.WriteAllBytes(Path.Combine(dir, $"{def.Archive}_Collision.bin"), RepackCollision.PackMphCollision(editors));
            string entFile = Path.Combine(root, "levels", "entities", $"{def.Archive}_Ent.bin");
            string infoFile = Path.Combine(dir, "arena.json");
            void WriteEntities() => File.WriteAllBytes(entFile, Repack.PackEntities(ents.Concat(cannons.Where(c => !c.Dropped).Select(c => ArenaCannons.Editor(c, c.EntityId))).ToList()));
            WriteEntities();
            string nodeFile = Path.Combine(root, "levels", "nodeData", $"{def.Archive}_Node.bin");
            File.WriteAllBytes(nodeFile, ArenaNav.Write(nav));
            File.WriteAllText(infoFile, JsonSerializer.Serialize(info, new JsonSerializerOptions { WriteIndented = true }));
            if (cannons.Count > 0)
            {
                // solve the pads' launches with MphRead's own player physics in the arena just written
                if (string.Equals(Path.GetFullPath(root).TrimEnd('\\', '/'), Path.GetFullPath(Paths.FileSystem).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase))
                {
                    Register(root);
                    foreach (string line in ArenaCannons.Tune(def.Room, cannons, info.KillHeight, ray)) log?.Invoke(line);
                    WriteEntities();
                    nav = Nav();   // pads may have moved out of their cannon, or been dropped
                    info.NavNodes = nav.Nodes.Count;
                    File.WriteAllBytes(nodeFile, ArenaNav.Write(nav));
                    foreach (ArenaCannons.Cannon c in cannons.Where(c => c.Dropped)) info.Notes.Add($"cannon '{c.Name}': no jump pad (no launch lands on its target)");
                    File.WriteAllText(infoFile, JsonSerializer.Serialize(info, new JsonSerializerOptions { WriteIndented = true }));
                }
                else
                {
                    info.Notes.Add("jump pads not tuned (the arena root isn't the game's file system)");
                }
            }
            log?.Invoke($"{def.InGameName}: {info.CollisionPolygons} collision polygons, {info.ModelTriangles} model triangles, " +
                $"{info.Spawns} spawns, {info.Items} items, {info.NavNodes} nav nodes, kill height {info.KillHeight:0.0}" +
                string.Concat(info.Notes.Select(n => "\n  note: " + n)));
            return info;
        }

        static int PrimaryAxis(Vector3 n)
        {
            float x = MathF.Abs(n.X), y = MathF.Abs(n.Y), z = MathF.Abs(n.Z);
            if (y > x && y >= z) return 1;
            if (z > x && z > y) return 2;
            return 0;
        }

        static Terrain TerrainOf(Mrea.Surface s) => s switch
        {
            Mrea.Surface.Stone or Mrea.Surface.Dirt or Mrea.Surface.Grass or Mrea.Surface.Organic or Mrea.Surface.Wood
                or Mrea.Surface.Phazon => Terrain.Rock,
            Mrea.Surface.Sand => Terrain.Sand,
            Mrea.Surface.Snow => Terrain.Snow,
            Mrea.Surface.Ice => Terrain.Ice,
            _ => Terrain.Metal
        };

        static List<ArenaModelWriter.Corner> ModelCorners(Mrea area, Pak pak, Func<float, float, float, Vector3> map, Info info)
        {
            var res = new List<ArenaModelWriter.Corner>();
            Cmdl geo;
            try
            {
                geo = area.ReadGeometry();
            }
            catch (Exception ex)
            {
                info.Notes.Add($"render mesh unreadable ({ex.Message}); no stand-in model triangles");
                return res;
            }
            var images = new Dictionary<uint, (byte[] Rgba, int W, int H)?>();
            (byte[] Rgba, int W, int H)? Image(uint id)
            {
                if (images.TryGetValue(id, out var img)) return img;
                img = null;
                if (pak.Contains(id))
                {
                    byte[]? rgba = Txtr.Decode(pak.Get(id), out _, out int w, out int h);
                    if (rgba != null) img = (rgba, w, h);
                }
                images[id] = img;
                return img;
            }
            var means = new Dictionary<uint, Vector3>();
            Vector3 Mean(uint id)
            {
                if (means.TryGetValue(id, out Vector3 m)) return m;
                m = new Vector3(0.6f);
                if (Image(id) is { } img)
                {
                    double r = 0, g = 0, b = 0; int n = img.W * img.H;
                    for (int i = 0; i < n; i++) { r += img.Rgba[4 * i]; g += img.Rgba[4 * i + 1]; b += img.Rgba[4 * i + 2]; }
                    m = new Vector3((float)(r / n / 255), (float)(g / n / 255), (float)(b / n / 255));
                }
                return means[id] = m;
            }
            Vector3 Sample(uint id, float s, float t)
            {
                if (Image(id) is not { } img) return Vector3.One;
                // the decoded rows run top first, as GX samples them: t = 0 is the first row (ArenaHdWriter)
                int x = (int)((s - MathF.Floor(s)) * img.W) % img.W, y = (int)((t - MathF.Floor(t)) * img.H) % img.H;
                int o = 4 * (y * img.W + x);
                return new Vector3(img.Rgba[o], img.Rgba[o + 1], img.Rgba[o + 2]) / 255f;
            }
            var light = new Vector3(0.3f, 1, 0.2f).Normalized();
            int skipped = 0;
            for (int t = 0; t < geo.TriangleCount; t++)
            {
                Cmdl.Material m = geo.Materials[geo.TriangleMaterial[t]];
                if (!(m.BlendSrc == 1 && m.BlendDst == 0)) { skipped++; continue; }   // blended surfaces (glass, glows) left out
                bool lightmapped = (m.Flags & 0x800) != 0 && m.TexIds.Length > 0;
                uint? diffuse = m.TexIds.Length > (lightmapped ? 1 : 0) ? m.TexIds[lightmapped ? 1 : 0] : null;
                Vector3 baseColor = diffuse is uint d ? Mean(d) : new Vector3(0.6f);
                for (int k = 0; k < 3; k++)
                {
                    Cmdl.Corner c = geo.Triangles[3 * t + k];
                    Vector3 p = map(geo.Positions[3 * c.P], geo.Positions[3 * c.P + 1], geo.Positions[3 * c.P + 2]);
                    Vector3 lit;
                    if (lightmapped && c.T0 >= 0 && geo.ShortUvs != null && 2 * c.T0 + 1 < geo.ShortUvs.Length)
                    {
                        lit = Sample(m.TexIds[0], geo.ShortUvs[2 * c.T0], geo.ShortUvs[2 * c.T0 + 1]) + new Vector3(0.08f);
                    }
                    else
                    {
                        Vector3 n = 3 * c.N + 2 < geo.Normals.Length
                            ? map(geo.Normals[3 * c.N], geo.Normals[3 * c.N + 1], geo.Normals[3 * c.N + 2]).Normalized() : Vector3.UnitY;
                        lit = new Vector3(0.4f + 0.6f * MathF.Max(0, Vector3.Dot(n, light)));
                    }
                    res.Add(new ArenaModelWriter.Corner(p, Vector3.Clamp(baseColor * lit, Vector3.Zero, Vector3.One)));
                }
            }
            if (skipped > 0) info.Notes.Add($"{skipped} blended render triangles left out of the stand-in model");
            return res;
        }

        // ---- register ----

        // add every arena imported under root to MphRead's room table; returns how many
        public static int Register(string root)
        {
            int count = 0;
            foreach (Def d in All)
            {
                string json = Path.Combine(ArchiveDir(root, d), "arena.json");
                if (!File.Exists(ModelFile(root, d)) || !File.Exists(json)) continue;
                Info? info = JsonSerializer.Deserialize<Info>(File.ReadAllText(json));
                if (info == null) continue;
                Metadata.AddHostRoom(Room(d, info));
                count++;
            }
            return count;
        }

        public static bool IsEchoesRoom(string roomName) => All.Any(d => d.Room == roomName);

        static string? _registeredRoot;

        // copy arena files staged in a folder laid out like the file system (_archives/echoes_*, levels/entities/echoes_*,
        // levels/nodeData/echoes_*) into the MPH file system when they differ, then register. Lets arenas imported on the
        // PC reach a device whose extracted ROM lives in private storage; the in-app import replaces this (phase 6).
        public static int InstallStaged(string stagingRoot)
        {
            string root;
            try
            {
                root = Paths.FileSystem;
            }
            catch
            {
                return 0;
            }
            if (!Directory.Exists(stagingRoot)) return 0;
            int copied = 0;
            foreach (string src in Directory.EnumerateFiles(stagingRoot, "*", SearchOption.AllDirectories))
            {
                string rel = Path.GetRelativePath(stagingRoot, src);
                if (!Path.GetFileName(rel).StartsWith("echoes_") && !rel.Replace('\\', '/').Contains("/echoes_")) continue;
                string dst = Path.Combine(root, rel);
                var si = new FileInfo(src);
                var di = new FileInfo(dst);
                if (di.Exists && di.Length == si.Length && di.LastWriteTimeUtc >= si.LastWriteTimeUtc) continue;
                Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                File.Copy(src, dst, overwrite: true);
                copied++;
            }
            _registeredRoot = null;
            EnsureRegistered();
            return copied;
        }

        // register the imported arenas once the MPH file system is known (MatchArenas.All calls this)
        public static void EnsureRegistered()
        {
            string root;
            try
            {
                root = Paths.FileSystem;
            }
            catch
            {
                return;   // paths not set up yet
            }
            if (root == _registeredRoot || !Directory.Exists(root)) return;
            Register(root);
            _registeredRoot = root;
        }

        static RoomMetadata Room(Def d, Info i)
        {
            static int Fx(float v) => (int)MathF.Round(v * 4096);
            var min = new Vector3(i.Min[0], i.Min[1], i.Min[2]);
            var max = new Vector3(i.Max[0], i.Max[1], i.Max[2]);
            float far = MathF.Max(350, (max - min).Length + 50);
            return new RoomMetadata(
                id: d.Id,
                name: d.Room,
                inGameName: d.InGameName,
                archive: d.Archive,
                modelPath: $"{d.Archive}_Model.bin",
                animationPath: $"{d.Archive}_Anim.bin",
                collisionPath: $"{d.Archive}_Collision.bin",
                texturePath: null,
                entityPath: $"{d.Archive}_Ent.bin",
                nodePath: $"{d.Archive}_Node.bin",
                roomNodeName: null,
                battleTimeLimit: 10 * 1800,
                timeLimit: 4 * 1800,
                pointLimit: 0,
                nodeLayer: 1,
                fogEnabled: false,
                clearFog: false,
                fogColor: new ColorRgb(0, 0, 0),
                fogSlope: 0,
                fogOffset: 0,
                light1Color: new ColorRgb(31, 31, 29),
                light1Vector: new Vector3(0.3f, -1, 0.2f),
                light2Color: new ColorRgb(12, 13, 15),
                light2Vector: new Vector3(-0.3f, -1, -0.2f),
                farClip: Fx(far),
                killHeight: Fx(i.KillHeight),
                size: RoomSize.Large,
                cameraMin: min - new Vector3(10),
                cameraMax: max + new Vector3(10),
                playerMin: new Vector3(-300),
                playerMax: new Vector3(300),
                multiplayer: true);
        }
    }
}
