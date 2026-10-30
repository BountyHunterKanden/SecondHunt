using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
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
    // (plus the HD look in _archives/<archive>/hd/). The in-app import (ImportService kind "arenas", recipes/arenas/
    // <Key>.json: scale + the tuned jump pads) writes the same files into one folder, <import root>/arenas/<Key>/.
    // Register / RegisterImported add every imported arena to MphRead's room table (Metadata.AddHostRoom).
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

        // the arenas' jump pads fly every form on the biped's arc (ArenaCannons.SameArcBit); set before any arena is
        // registered or loaded, on every platform
        // the weapon crates, then the breakables and the Dark Water, join an arena's entities when a match loads it (off
        // unless the match turns them on); the breakables' boxes join MphRead's platform collision
        static EchoesArena()
        {
            MphRead.Entities.JumpPadEntity.HostSameArcBit = ArenaCannons.SameArcBit;
            // (then the owner's baked pickup placements, any arena: PlacedPickups, so the ids before them stay; then
            // Shooting Gallery's turret, after them so their ids stay too: EchoesTurret)
            SceneSetup.HostExtraEntities = (scene, room, entities) =>
                EchoesTurret.Extra(scene, room, PlacedPickups.Extra(scene, room, EchoesMovement.Extra(scene, room, EchoesBreakables.Extra(scene, room,
                    EchoesCrates.Extra(scene, room, EchoesAmmo.Extra(scene, room, entities))))));
            MphRead.Formats.CollisionDetection.HostEntityCollision = e => e is EchoesBreakableEntity || e is EchoesTurretEntity || e is EchoesTurretBaseEntity;
            MphRead.Entities.BeamProjectileEntity.HostHitPush = EchoesTurret.Knock;   // (only the turret's shots)
            // the view before a match's first spawn (these rooms have no intro sequence): the arena picture's lens
            MphRead.Entities.PlayerEntity.HostPreSpawnView = PreSpawnView;
        }

        // room name -> the arena picture's lens (ArenaThumbnail.ReadView); none yet (a picture still being drawn again
        // in the background: RefreshStale) is looked for again a second later
        static readonly Dictionary<string, ((Vector3 Eye, Vector3 Target)? View, long At)> _views = new();

        static (Vector3 Eye, Vector3 Target)? PreSpawnView(Scene scene)
        {
            if (scene.Room?.Meta is not { } meta || HdDir(meta.Name) is not string hd) return null;
            long now = Environment.TickCount64;
            lock (_views)
            {
                if (_views.TryGetValue(hd, out var known) && (known.View != null || now - known.At < 1000)) return known.View;
            }
            (Vector3, Vector3)? view = null;
            if (ArenaThumbnail.ReadView(hd) is { } v)
            {
                view = (new Vector3(v.Eye.X, v.Eye.Y, v.Eye.Z), new Vector3(v.Target.X, v.Target.Y, v.Target.Z));
            }
            lock (_views) _views[hd] = (view, now);
            return view;
        }

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
            // ArenaNav.Version that made the nav file (absent = 1: before every pair had a route); >= 2 turns on the bots'
            // unstick rule (MphRead PlayerAiUnstick.cs), so older imports and their replays play as before
            public int NavVersion { get; set; } = 1;
            // version 3: the links bots take with a jump, Echoes' double jump, a gap jump or the Gravity Boost: [from, to,
            // kind] (ArenaNav.Link*; Echoes bots T7)
            [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
            public List<int[]>? NavLinks { get; set; }
            public int HdTriangles { get; set; }
            public List<ArenaCannons.Cannon> Cannons { get; set; } = new();
            // the weapon crates (EchoesCrates): where each floats, as Echoes places it, and the floor under it; the
            // weapons they give (ItemType names, equal chance: the recipe's "crates", else CratePools)
            public List<CrateSpot> Crates { get; set; } = new();
            public List<string> CrateWeapons { get; set; } = new();
            // the breakables (EchoesBreakables) and the Dark Water pools; absent in arenas without them
            [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
            public List<BreakSpot>? Breakables { get; set; }
            [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
            public List<FluidSpot>? Fluids { get; set; }
            // the actors whose solid shape is in the area's collision but whose model isn't in its render mesh (Spires'
            // pillars; T6 N3 2026-10-10): drawn in their own passes (EchoesStatics); absent in arenas without them
            [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
            public List<StaticSpot>? Statics { get; set; }
            // Shooting Gallery's manned turret and bomb slot (EchoesTurret); absent in arenas without one
            [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
            public TurretSpot? Turret { get; set; }
            public List<string> Notes { get; set; } = new();
        }

        public sealed class StaticSpot
        {
            public string Name { get; set; } = "";
            // its draw pass in arena.bin (after the breakables'), and Echoes' lighting at its centre (as the crates')
            public int Pass { get; set; }
            public float[]? Ambient { get; set; }
            public List<float[]>? Lights { get; set; }
        }

        public sealed class BreakSpot
        {
            // "grate" (breaks to any bomb, comes back) or "wall" (stays broken)
            public string Kind { get; set; } = "";
            public string Name { get; set; } = "";
            // its box (MPH units, axis-aligned: the model's bounds as placed), health in MPH points (EchoesBreakables),
            // steps until it's back (0 = never), its draw pass in arena.bin
            public float[] Min { get; set; } = new float[3];
            public float[] Max { get; set; } = new float[3];
            // a model turned about the vertical (Sidehopper Station's grates, 30 degrees): its box's size along its own axes
            // and its turn (degrees about MPH Y), centred in Min/Max; absent for an unturned one (the box is Min/Max)
            [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
            public float[]? Size { get; set; }
            [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
            public float? Yaw { get; set; }
            public int Health { get; set; }
            public int RespawnFrames { get; set; }
            public int Pass { get; set; }
            // Echoes' lighting at its centre (as the crates')
            public float[]? Ambient { get; set; }
            public List<float[]>? Lights { get; set; }
        }

        public sealed class FluidSpot
        {
            public string Kind { get; set; } = "";
            // the pool's box (MPH units) and its surface height
            public float[] Min { get; set; } = new float[3];
            public float[] Max { get; set; } = new float[3];
            public float Surface { get; set; }
        }

        // the breakables Echoes' multiplayer arenas place: Shooting Gallery's grates over its Dark Water and Sidehopper
        // Station's 6 turned grates (ACTR 'Grating', health 5, RESET GRILLE after 10 s; the default layer's only: its 2
        // tiny "Coins" layer ones are decoration) and Crossfire Chaos's walls (PLAT 'wall', health 20, no reset)
        sealed record BreakDef(string Type, string Name, uint Model, string Kind, int Health, int Respawn);

        static readonly BreakDef[] BreakDefs =
        {
            new("ACTR", "Grating", 0xf856c70d, "grate", EchoesBreakables.GrateHealth, EchoesBreakables.GrateRespawn),
            new("PLAT", "wall", 0xc220e7e0, "wall", EchoesBreakables.WallHealth, 0),
        };

        // Shooting Gallery's Dark Water (WATR 'Dark Water': ColorMap, FlowColor 12 s / 5 m, GlossMap, FlowGloss1 30 s / 20 m,
        // BaseColor), drawn with ArenaHdWriter's two-stage fluid look. Its colour map is nearly black (mean 0.016, 0,
        // 0.005), so the gloss term carries the colour: the gloss map's mean (0.40) times Gloss lands on Echoes' BaseColor
        // (0.204, 0.004, 0.220), lighter where the gloss map is
        static readonly ArenaHdWriter.FluidLook DarkWater = new(0xb1b6e80d, 5, 12, 0x32fdbbc6, 20, 30,
            new[] { 0.62f, 0.10f, 0.70f }, new[] { 0.48f, 0.01f, 0.54f }, 0.82f);

        // Pipeline's water (its 4 WATR 'Fluid 1/2/3', 'Master Fluid' share one look: ColorMap 1af6276a, FlowColor 12 s / 5 m,
        // GlossMap 16c37701, FlowGloss1 30 s / 16 m, BaseColor (0.486, 0.631, 0.553), Alpha 0.7); swimmable water for Echoes
        // movement (EchoesMovement), harmless. The western two thirds of the arena is one flooded cave whose boxes reach
        // up into the rock, so only the side channels' surfaces show (-echoesarena waterprobe)
        static readonly ArenaHdWriter.FluidLook Water = new(0x1af6276a, 5, 12, 0x16c37701, 16, 30,
            new[] { 0.486f, 0.631f, 0.553f }, new[] { 0.77f, 1.0f, 0.876f }, 0.7f);
        public static readonly float[] WaterFog = { 0.486f, 0.631f, 0.553f };

        // a breakable's box as 12 outward-facing triangles: turned about MPH Y by its yaw as EchoesBreakableEntity turns it
        static IEnumerable<(Vector3, Vector3, Vector3)> GrateTris(BreakSpot b)
        {
            static Vector3 V(float[] v) => new(v[0], v[1], v[2]);
            Vector3 p = (V(b.Min) + V(b.Max)) / 2;
            Vector3 h = b.Size is { Length: 3 } s ? V(s) / 2 : (V(b.Max) - V(b.Min)) / 2;
            float turn = MathHelper.DegreesToRadians(b.Size is { Length: 3 } ? b.Yaw ?? 0 : 0);
            Vector3 r = new Vector3(MathF.Cos(turn), 0, -MathF.Sin(turn)) * h.X, u = Vector3.UnitY * h.Y, f = new Vector3(MathF.Sin(turn), 0, MathF.Cos(turn)) * h.Z;
            // each face: normal axis n, tangents a x b along n
            foreach ((Vector3 n, Vector3 a, Vector3 c) in new[] { (r, u, f), (-r, f, u), (u, f, r), (-u, r, f), (f, r, u), (-f, u, r) })
            {
                Vector3 c0 = p + n - a - c, c1 = p + n + a - c, c2 = p + n + a + c, c3 = p + n - a + c;
                yield return (c0, c1, c2);
                yield return (c0, c2, c3);
            }
        }

        // a water box's top, less the parts inside another box that reaches higher (a surface there would hang inside the
        // other's water): the rectangles that are left
        static List<(float X0, float Y0, float X1, float Y1)> SurfacePieces(float[] lo, float[] hi, IEnumerable<(float[] Lo, float[] Hi)> others)
        {
            var pieces = new List<(float, float, float, float)> { (lo[0], lo[1], hi[0], hi[1]) };
            foreach ((float[] olo, float[] ohi) in others)
            {
                if (ohi[2] <= hi[2] || olo[2] > hi[2]) continue;
                var next = new List<(float, float, float, float)>();
                foreach ((float x0, float y0, float x1, float y1) in pieces)
                {
                    float cx0 = MathF.Max(x0, olo[0]), cx1 = MathF.Min(x1, ohi[0]), cy0 = MathF.Max(y0, olo[1]), cy1 = MathF.Min(y1, ohi[1]);
                    if (cx0 >= cx1 || cy0 >= cy1) { next.Add((x0, y0, x1, y1)); continue; }
                    if (x0 < cx0) next.Add((x0, y0, cx0, y1));
                    if (cx1 < x1) next.Add((cx1, y0, x1, y1));
                    if (y0 < cy0) next.Add((cx0, y0, cx1, cy0));
                    if (cy1 < y1) next.Add((cx0, cy1, cx1, y1));
                }
                pieces = next;
            }
            return pieces;
        }

        public sealed class CrateSpot
        {
            public float[] Pos { get; set; } = new float[3];
            public float[] Floor { get; set; } = new float[3];
            // Echoes' lighting of the crate there (EchoesCrates.Lighting): the area's ambient (rgb), and per light the way
            // to it (MPH axes, unit) + its colour at the crate; null in an import before it (the crate draws lit white)
            public float[]? Ambient { get; set; }
            public List<float[]>? Lights { get; set; }
        }

        // each arena's crate weapons (owner 2026-10-08: a fixed pool per arena, equal chance; each weapon in 4 arenas).
        // What -echoesarena import writes; the in-app import takes the recipe's "crates" (written from arena.json)
        public static readonly IReadOnlyDictionary<string, ItemType[]> CratePools = new Dictionary<string, ItemType[]>
        {
            ["M01_SidehopperStation"] = new[] { ItemType.Imperialist, ItemType.Judicator, ItemType.Battlehammer, ItemType.VoltDriver },
            ["M02_Spires"] = new[] { ItemType.Imperialist, ItemType.Judicator, ItemType.VoltDriver, ItemType.Magmaul },
            ["M03_CrossfireChaos"] = new[] { ItemType.Battlehammer, ItemType.Magmaul, ItemType.ShockCoil, ItemType.Imperialist },
            ["M04_Pipeline"] = new[] { ItemType.ShockCoil, ItemType.Magmaul, ItemType.Battlehammer, ItemType.Judicator },
            ["M05_SpiderComplex"] = new[] { ItemType.ShockCoil, ItemType.Magmaul, ItemType.Judicator, ItemType.VoltDriver },
            ["M06_ShootingGallery"] = new[] { ItemType.Imperialist, ItemType.ShockCoil, ItemType.Battlehammer, ItemType.VoltDriver },
        };

        // Echoes' weapon crate model (every arena's '[OUT] Weapon Crate' actor) and its actor scale
        public const uint CrateModel = 0xccc5bca5;
        public const float CrateActorScale = 1.5f;

        static string ArchiveDir(string root, Def d) => Path.Combine(root, "_archives", d.Archive);

        // where an import's files go: the MPH file system's own layout (Tools -echoesarena import; the dev staging), or
        // one folder per arena, the in-app import's (ImportService's item folder <import root>/arenas/<Key>/, the HD look
        // in it too: ImportService moves an item's files, not folders)
        public sealed record Layout(string ArchiveDir, string HdDir, string EntityDir, string NodeDir)
        {
            public static Layout FileSystem(string root, Def d) => new(EchoesArena.ArchiveDir(root, d), Path.Combine(EchoesArena.ArchiveDir(root, d), ArenaHdWriter.Dir),
                Path.Combine(root, "levels", "entities"), Path.Combine(root, "levels", "nodeData"));
            public static Layout Folder(string dir) => new(dir, dir, dir, dir);
        }

        // ---- import ----

        // Echoes player items (PlayerItem enum) the arenas hand out
        const uint HealthRefill = 0x29, PowerBomb = 0x2B, Missile = 0x2C, Invisibility = 0x39, AmpDamage = 0x3A, Invincibility = 0x3B;
        const uint PickupItemId = 0xA02EF0C4, PickupAmountId = 0x94AF1445;
        const uint NeedSkyId = 0x95D4BEE7, OverrideSkyId = 0xD208C9FA;   // AreaAttributes

        // into the MPH file system under root; the jump pads' launches are solved here when root is the game's own
        public static Info Import(Pak metroid6, Def def, string root, float scale = DefaultScale, Action<string>? log = null, Pak? audio = null, Pak? hud = null)
            => Import(metroid6, def, Layout.FileSystem(root, def), scale, null, log, fsRoot: root, audio: audio, hud: hud);

        // tuned: the jump pads as a recipe holds them, one per cannon in ArenaCannons.Find's order (what a tuning run
        // left in arena.json's Cannons); null = solve them with MphRead's own player physics, which needs the arena
        // written into the game's file system (fsRoot == Paths.FileSystem). crates: the crates' weapons (null:
        // CratePools'; empty: no crates). progress: (fraction, message). audio: the disc's AudioGrp.pak (its sound lookup:
        // the turret's sounds, EchoesTurret.ImportSounds; null: none). hud: the disc's NoARAM.pak (the turret HUD's textures,
        // EchoesTurret.ImportHud; null: none)
        public static Info Import(Pak metroid6, Def def, Layout files, float scale, IReadOnlyList<ArenaCannons.Cannon>? tuned,
            Action<string>? log = null, Action<float, string>? progress = null, string? fsRoot = null, CancellationToken ct = default,
            IReadOnlyList<ItemType>? crates = null, Pak? audio = null, Pak? hud = null)
        {
            var info = new Info { Key = def.Key, Scale = scale };
            progress?.Invoke(0, $"{def.InGameName}: collision and objects");
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
            foreach (Mrea.ScriptObject o in scripts.Where(o => o.Active && o.Type == "PCKP"))
            {
                ushort interval = 450;                               // Echoes pickups respawn in 15 s
                uint what = o.Root.Find(PickupItemId)?.U32 ?? uint.MaxValue;
                int amount = o.Root.Find(PickupAmountId)?.I32 ?? 0;
                ItemType item = what switch
                {
                    HealthRefill => amount >= 30 ? ItemType.HealthMedium : ItemType.HealthSmall,
                    Missile => ItemType.MissileSmall,
                    AmpDamage => ItemType.DoubleDamage,
                    Invisibility => ItemType.Cloak,
                    _ => ItemType.None
                };
                if (what == AmpDamage) interval = 1800;              // Spider Complex's Quad Damage: 60 s
                if (item == ItemType.None)
                {
                    if (what is PowerBomb or Invincibility) info.Notes.Add($"pickup '{o.Name}' (item {what:x}) has no MPH equivalent yet");
                    continue;
                }
                float[] x = o.Transform;
                Vector3 p = OnFloor(Map(x[0], x[1], x[2]));
                if (!Fresh(p)) continue;
                ents.Add(new ItemSpawnEntityEditor { Id = id++, LayerMask = 0xFFFF, NodeName = ArenaModelWriter.RoomNode,
                    Position = p, Up = Vector3.UnitY, Facing = Vector3.UnitZ, ParentId = 0xFFFF, ItemType = item, Enabled = true,
                    HasBase = false, AlwaysActive = false, MaxSpawnCount = 0, SpawnInterval = interval, SpawnDelay = 0, NotifyEntityId = -1 });
                info.Items++;
            }
            // the weapon crates: Echoes' '[OUT] Weapon Crate' actors, inactive until the 'Delay to Spawn Crates' timer
            // (15 s) turns them on; the match's own entities (EchoesCrates), not the room's file
            IReadOnlyList<ItemType> pool = crates ?? (CratePools.TryGetValue(def.Key, out ItemType[]? defaults) ? defaults : Array.Empty<ItemType>());
            if (pool.Count > 0)
            {
                // lit as Echoes lights them: from the area's light layer 0 (the crates ask for light set 1, but every
                // arena's lights are in layer 0 and layer 1 is empty)
                List<Mrea.Light> lights = area.ReadLights()[0];
                foreach (Mrea.ScriptObject o in scripts.Where(o => o.Type == "ACTR" && o.Name.Contains("Weapon Crate")))
                {
                    float[] x = o.Transform;
                    Vector3 p = Map(x[0], x[1], x[2]);
                    if (info.Crates.Any(c => (new Vector3(c.Pos[0], c.Pos[1], c.Pos[2]) - p).LengthSquared < 0.25f)) continue;
                    Vector3 f = ray.FloorBelow(p + Vector3.UnitY * 0.3f, 6f) ?? p;
                    (float[] amb, List<float[]> lit) = EchoesCrates.Lighting(lights, new Vector3(x[0], x[1], x[2]), Map, ray);
                    info.Crates.Add(new CrateSpot { Pos = new[] { p.X, p.Y, p.Z }, Floor = new[] { f.X, f.Y, f.Z }, Ambient = amb, Lights = lit });
                }
                if (info.Crates.Count > 0)
                {
                    info.CrateWeapons = pool.Select(w => w.ToString()).ToList();
                    float[] a = info.Crates[0].Ambient!;
                    info.Notes.Add($"crate lighting (Echoes' actor lights): ambient {a[0]:0.000} {a[1]:0.000} {a[2]:0.000}, lights per crate "
                        + string.Join(" ", info.Crates.Select(c => $"{c.Lights!.Count} (strongest {(c.Lights.Count > 0 ? c.Lights.Max(l => MathF.Max(l[3], MathF.Max(l[4], l[5]))) : 0):0.00})")));
                }
            }
            // breakables and Dark Water (EchoesBreakables): each breakable's box from its model's bounds as placed, lit as
            // Echoes lights it at its centre; each pool's box and surface
            var placed = new List<ArenaHdWriter.Placed>();
            var fluids = new List<ArenaHdWriter.Fluid>();
            TurretLook? turretLook = null;
            {
                List<Mrea.Light>? breakLights = null;
                foreach (Mrea.ScriptObject o in scripts.Where(o => o.Active && o.Layer == 0))
                {
                    BreakDef? bd = BreakDefs.FirstOrDefault(b => b.Type == o.Type && b.Name == o.Name);
                    if (bd == null || !metroid6.Contains(bd.Model)) continue;
                    Cmdl bmodel = Cmdl.Load(metroid6.Get, bd.Model);
                    var lo = new Vector3(float.MaxValue);
                    var hi = new Vector3(float.MinValue);
                    for (int i = 0; i + 2 < bmodel.Positions.Length; i += 3)
                    {
                        var v = new Vector3(bmodel.Positions[i], bmodel.Positions[i + 1], bmodel.Positions[i + 2]);
                        lo = Vector3.ComponentMin(lo, v);
                        hi = Vector3.ComponentMax(hi, v);
                    }
                    float[] x = o.Transform;
                    var min = new Vector3(float.MaxValue);
                    var max = new Vector3(float.MinValue);
                    for (int k = 0; k < 8; k++)
                    {
                        Vector3 c = ArenaHdWriter.Place(x, new Vector3((k & 1) != 0 ? hi.X : lo.X, (k & 2) != 0 ? hi.Y : lo.Y, (k & 4) != 0 ? hi.Z : lo.Z));
                        Vector3 m = Map(c.X, c.Y, c.Z);
                        min = Vector3.ComponentMin(min, m);
                        max = Vector3.ComponentMax(max, m);
                    }
                    breakLights ??= area.ReadLights()[0];
                    Vector3 centre = ArenaHdWriter.Place(x, (lo + hi) / 2);
                    (float[] amb, List<float[]> lit) = EchoesCrates.Lighting(breakLights, centre, Map, ray);
                    info.Breakables ??= new();
                    int pass = ArenaHdWriter.FirstPlacedPass + info.Breakables.Count;
                    static float R(float v) => MathF.Round(v, 4);
                    // turned only about the vertical (its own up stays MPH up): the box along its own axes, so a turned
                    // grate's box isn't the bigger axis-aligned one around it
                    float[]? size = null;
                    float? yaw = null;
                    {
                        Vector3 mc = (lo + hi) / 2;
                        Vector3 Axis(Vector3 d)
                        {
                            Vector3 a = ArenaHdWriter.Place(x, mc + d), b = ArenaHdWriter.Place(x, mc);
                            return Map(a.X, a.Y, a.Z) - Map(b.X, b.Y, b.Z);
                        }
                        Vector3 ex = Axis(Vector3.UnitX), ey = Axis(Vector3.UnitY), ez = Axis(Vector3.UnitZ);
                        float turn = MathF.Atan2(-ex.Z, ex.X);
                        bool upright = MathF.Abs(ex.Y) < 1e-4f * ex.Length && MathF.Abs(ey.Y) < 1e-4f * ey.Length;
                        // a half turn leaves the box where it was
                        if (upright && MathF.Abs(MathF.Sin(turn)) > 0.001f)
                        {
                            yaw = R(MathHelper.RadiansToDegrees(turn));
                            size = new[] { R(ex.Length * (hi.X - lo.X)), R(ez.Length * (hi.Z - lo.Z)), R(ey.Length * (hi.Y - lo.Y)) };
                        }
                    }
                    info.Breakables.Add(new BreakSpot { Kind = bd.Kind, Name = $"{o.Name} {o.Id:x8}", Min = new[] { R(min.X), R(min.Y), R(min.Z) },
                        Max = new[] { R(max.X), R(max.Y), R(max.Z) }, Size = size, Yaw = yaw, Health = bd.Health, RespawnFrames = bd.Respawn,
                        Pass = pass, Ambient = amb, Lights = lit });
                    placed.Add(new ArenaHdWriter.Placed(bd.Model, x, pass));
                }
                foreach (Mrea.ScriptObject o in scripts.Where(o => o.Active && o.Type == "WATR" && o.Name == "Dark Water"))
                {
                    float[] x = o.Transform;
                    float[] lo = { x[0] - x[6] / 2, x[1] - x[7] / 2, x[2] - x[8] / 2 }, hi = { x[0] + x[6] / 2, x[1] + x[7] / 2, x[2] + x[8] / 2 };
                    Vector3 a = Map(lo[0], lo[1], lo[2]), b = Map(hi[0], hi[1], hi[2]);
                    Vector3 min = Vector3.ComponentMin(a, b), max = Vector3.ComponentMax(a, b);
                    static float R(float v) => MathF.Round(v, 4);
                    info.Fluids ??= new();
                    info.Fluids.Add(new FluidSpot { Kind = "darkwater", Min = new[] { R(min.X), R(min.Y), R(min.Z) }, Max = new[] { R(max.X), R(max.Y), R(max.Z) }, Surface = R(max.Y) });
                    fluids.Add(new ArenaHdWriter.Fluid(lo, hi, hi[2], DarkWater));
                }
                // swimmable water (Pipeline): each box, and its top where no higher box covers it
                List<Mrea.ScriptObject> waters = scripts.Where(o => o.Active && o.Layer == 0 && o.Type == "WATR" && o.Name != "Dark Water").ToList();
                static (float[] Lo, float[] Hi) Box(float[] x) => (new[] { x[0] - x[6] / 2, x[1] - x[7] / 2, x[2] - x[8] / 2 }, new[] { x[0] + x[6] / 2, x[1] + x[7] / 2, x[2] + x[8] / 2 });
                foreach (Mrea.ScriptObject o in waters)
                {
                    (float[] lo, float[] hi) = Box(o.Transform);
                    Vector3 a = Map(lo[0], lo[1], lo[2]), b = Map(hi[0], hi[1], hi[2]);
                    Vector3 min = Vector3.ComponentMin(a, b), max = Vector3.ComponentMax(a, b);
                    static float R(float v) => MathF.Round(v, 4);
                    info.Fluids ??= new();
                    info.Fluids.Add(new FluidSpot { Kind = "water", Min = new[] { R(min.X), R(min.Y), R(min.Z) }, Max = new[] { R(max.X), R(max.Y), R(max.Z) }, Surface = R(max.Y) });
                    foreach ((float x0, float y0, float x1, float y1) in SurfacePieces(lo, hi, waters.Where(w => w != o).Select(w => Box(w.Transform))))
                    {
                        fluids.Add(new ArenaHdWriter.Fluid(new[] { x0, y0, lo[2] }, new[] { x1, y1, hi[2] }, hi[2], Water));
                    }
                }
                if (info.Breakables != null)
                {
                    info.Notes.Add($"{info.Breakables.Count} breakables: " + string.Join(", ", info.Breakables.GroupBy(b => b.Kind)
                        .Select(g => $"{g.Count()} {g.Key}s ({g.First().Health} MPH health, " + (g.First().RespawnFrames > 0 ? $"back after {g.First().RespawnFrames} steps)" : "stay broken)")))
                        + "; boxes " + string.Join(" ", info.Breakables.Select(b => $"{b.Max[0] - b.Min[0]:0.0}x{b.Max[1] - b.Min[1]:0.0}x{b.Max[2] - b.Min[2]:0.0}")));
                }
                if (info.Fluids?.Any(f => f.Kind == "darkwater") == true)
                {
                    info.Notes.Add($"{info.Fluids.Count(f => f.Kind == "darkwater")} Dark Water pools (MPH acid: 1 energy every 8 steps standing in one), surfaces "
                        + string.Join(" ", info.Fluids.Where(f => f.Kind == "darkwater").Select(f => $"{f.Surface:0.00}")) + "; Echoes' Light Suit 'False Floor' slabs left out");
                }
                if (info.Fluids?.Any(f => f.Kind == "water") == true)
                {
                    info.Notes.Add($"{info.Fluids.Count(f => f.Kind == "water")} water volumes (Echoes movement underwater), surfaces "
                        + string.Join(" ", info.Fluids.Where(f => f.Kind == "water").Select(f => $"{f.Surface:0.00}"))
                        + $"; {fluids.Count(f => f.Look == Water)} surface pieces drawn");
                }
                breakLights ??= area.ReadLights()[0];
                AddStatics(area, scripts, metroid6, Map, tris, ray, breakLights, info, placed);
                // the turret, its hologram and the slot's ring (EchoesTurret.ImportLook): placed passes after the statics'
                turretLook = EchoesTurret.ImportLook(metroid6, scripts, Map, scale, placed, p => EchoesCrates.Lighting(breakLights, p, Map, ray), info.Notes);
            }
            // Shooting Gallery's turret and bomb slot (EchoesTurret): the disc's values, its collision mesh and its lift
            info.Turret = EchoesTurret.Import(metroid6, scripts, Map, scale, info.Notes);
            // orb cannons -> jump pads (launch solved below, once the arena loads)
            List<ArenaCannons.Cannon> cannons = ArenaCannons.Find(scripts, ray, Map, scale, info.KillHeight, info.Notes);
            foreach (ArenaCannons.Cannon c in cannons) c.EntityId = id++;
            if (tuned != null) ApplyTuning(def, cannons, tuned);
            info.Cannons = cannons;
            ct.ThrowIfCancellationRequested();

            // bot navigation over the converted collision, anchored at the spawns
            progress?.Invoke(0.15f, $"{def.InGameName}: bot navigation");
            List<Vector3> anchors = ents.OfType<PlayerSpawnEntityEditor>().Select(e => e.Position).ToList();
            // the grates are solid (CollisionDetection.HostEntityCollision): bots walk on them, so the graph has no nodes on
            // the pool floors under them that a bot on top can't reach
            ArenaRaycaster navRay = info.Breakables?.Any(b => b.Kind == "grate") == true
                ? new ArenaRaycaster(tris.Concat(info.Breakables.Where(b => b.Kind == "grate").SelectMany(GrateTris)).ToList()) : ray;
            // the water's floors are left with the Gravity Boost (ArenaNav version 3)
            List<(Vector3, Vector3)> navWater = (info.Fluids ?? new()).Where(f => f.Kind == "water")
                .Select(f => (new Vector3(f.Min[0], f.Min[1], f.Min[2]), new Vector3(f.Max[0], f.Max[1], f.Max[2]))).ToList();
            ArenaNav.Graph Nav() => ArenaNav.Build(navRay, anchors, info.KillHeight, cannons.Where(c => !c.Dropped).Select(c => (c.PadV, c.TargetV)).ToList(), navWater);
            ArenaNav.Graph nav = Nav();
            info.NavNodes = nav.Nodes.Count;
            info.NavLinks = nav.Links.Count > 0 ? nav.Links : null;
            info.NavVersion = ArenaNav.Version;
            ct.ThrowIfCancellationRequested();

            // stand-in model: the render mesh, each corner coloured by its lightmap texel times its texture's mean colour
            // the arena's own look for the device (CampaignArena.cs draws it; the stand-in stays as the fallback)
            progress?.Invoke(0.35f, $"{def.InGameName}: textures and the hd look");
            try
            {
                // the sky: AreaAttributes' NeedSky + OverrideSky (Pipeline has none; none of the six uses the world's default)
                uint sky = uint.MaxValue;
                if (scripts.FirstOrDefault(o => o.Type == "REAA") is { } reaa && reaa.Root.Find(NeedSkyId)?.Bool == true
                    && reaa.Root.Find(OverrideSkyId) is { Data.Length: >= 4 } os)
                {
                    sky = os.U32;
                }
                // the turret's effects first: the models their particles draw join the placed passes (with the generated
                // layer: the grates' break pieces are only there). An arena with grates and no turret (Sidehopper Station)
                // gets the grates' break alone, in a turret.json of no pieces (no turret: that comes from the arena's spot)
                List<Mrea.ScriptObject> withGenerated = area.ReadScripts(includeGenerated: true);
                TurretLook? fxLook = turretLook ?? (EchoesTurret.GrateGenerator(withGenerated) != null ? new TurretLook() : null);
                List<TurretFx>? turretFx = fxLook != null ? EchoesTurret.ImportFx(metroid6, withGenerated, Map, files.HdDir, info.Notes, placed, fxLook, gratesOnly: turretLook == null) : null;
                ArenaHdWriter.Result hd = ArenaHdWriter.Write(area, metroid6, Map, files.HdDir, sky,
                    info.Crates.Count > 0 ? CrateModel : uint.MaxValue, CrateActorScale, placed, fluids);
                info.HdTriangles = hd.Triangles;
                info.Notes.Add($"HD look: {hd.Triangles} triangles in {hd.Batches} batches, {hd.Materials} materials, {hd.Textures} textures"
                    + (hd.Undecoded > 0 ? $" ({hd.Undecoded} not decoded)" : "") + $", mesh {hd.Bytes / 1024} KB"
                    + (hd.SkyTriangles > 0 ? $", sky {sky:x8} ({hd.SkyTriangles} triangles)" : sky != uint.MaxValue ? $", sky {sky:x8} NOT found" : ", no sky"));
                if (info.Crates.Count > 0)
                {
                    info.Notes.Add($"{info.Crates.Count} weapon crates ({string.Join(", ", info.CrateWeapons)}), crate model {CrateModel:x8} " + (hd.PropTriangles > 0
                        ? $"({hd.PropTriangles} triangles, {hd.PropMin.Y:0.00}..{hd.PropMax.Y:0.00} units about its origin)" : "NOT found"));
                }
                if (placed.Count > 0) info.Notes.Add($"breakable models: {hd.PlacedTriangles} triangles in {placed.Count} placements");
                string turretFile = Path.Combine(files.HdDir, EchoesTurret.LookFile);
                foreach (string old in Directory.GetFiles(files.HdDir, EchoesTurret.SoundFile("*"))) File.Delete(old);
                if (turretLook != null) turretLook.Sounds = EchoesTurret.ImportSounds(metroid6, audio, scripts, scale, files.HdDir, info.Notes);
                else if (fxLook != null) fxLook.Sounds = EchoesTurret.ImportSounds(metroid6, audio, withGenerated, scale, files.HdDir, info.Notes, gratesOnly: true);
                if (fxLook != null) fxLook.Fx = turretFx;
                if (turretLook != null) turretLook.Hud = EchoesTurret.ImportHud(hud, files.HdDir, info.Notes);
                if (fxLook != null) File.WriteAllText(turretFile, JsonSerializer.Serialize(fxLook));
                else if (File.Exists(turretFile)) File.Delete(turretFile);
                if (fxLook == null && Directory.Exists(Path.Combine(files.HdDir, EchoesTurret.FxDir))) Directory.Delete(Path.Combine(files.HdDir, EchoesTurret.FxDir), true);
                // the multiplayer menu's ARENA picture (Vanilla multiplayer menu M5): rendered from the HD look just
                // written, aimed at the spawns and pickups (thumb.png beside it)
                progress?.Invoke(0.75f, $"{def.InGameName}: the arena's picture");
                try
                {
                    // (as the entity file stores them, so the PC tool's -mpmenu thumbs reading it back aims the same)
                    static float Fx(float v) => Fixed.ToFloat(Fixed.ToInt(v));
                    var focus = ents.Select(e => new System.Numerics.Vector3(Fx(e.Position.X), Fx(e.Position.Y), Fx(e.Position.Z))).ToList();
                    // (with the breakables, static actors and crates as this import places them: arena.json comes after)
                    if (ArenaThumbnail.Write(files.HdDir, focus, log: log, inside: true, extras: ArenaThumbnail.Extras(info)) == null) info.Notes.Add("thumbnail not written");
                }
                catch (Exception ex)
                {
                    info.Notes.Add($"thumbnail not written: {ex.Message}");
                }
            }
            catch (Exception ex)
            {
                info.Notes.Add($"HD look not written: {ex.Message}");
            }
            ct.ThrowIfCancellationRequested();
            progress?.Invoke(0.85f, $"{def.InGameName}: the room files");
            List<ArenaModelWriter.Corner> corners = ModelCorners(area, metroid6, Map, info);
            info.ModelTriangles = corners.Count / 3;
            (byte[] model, byte[] anim) = ArenaModelWriter.Write(def.Archive, corners);
            // the recipe's dropped pads, noted where a tuning run notes them (arena.json comes out the same)
            if (tuned != null)
            {
                foreach (ArenaCannons.Cannon c in cannons.Where(c => c.Dropped)) info.Notes.Add($"cannon '{c.Name}': no jump pad (no launch lands on its target)");
            }

            string dir = files.ArchiveDir;
            Directory.CreateDirectory(dir);
            Directory.CreateDirectory(files.EntityDir);
            Directory.CreateDirectory(files.NodeDir);
            File.WriteAllBytes(Path.Combine(dir, $"{def.Archive}_Model.bin"), model);
            File.WriteAllBytes(Path.Combine(dir, $"{def.Archive}_Anim.bin"), anim);
            File.WriteAllBytes(Path.Combine(dir, $"{def.Archive}_Collision.bin"), RepackCollision.PackMphCollision(editors));
            string entFile = Path.Combine(files.EntityDir, $"{def.Archive}_Ent.bin");
            string infoFile = Path.Combine(dir, "arena.json");
            void WriteEntities() => File.WriteAllBytes(entFile, Repack.PackEntities(ents.Concat(cannons.Where(c => !c.Dropped).Select(c => ArenaCannons.Editor(c, c.EntityId))).ToList()));
            WriteEntities();
            string nodeFile = Path.Combine(files.NodeDir, $"{def.Archive}_Node.bin");
            File.WriteAllBytes(nodeFile, ArenaNav.Write(nav));
            File.WriteAllText(infoFile, JsonSerializer.Serialize(info, new JsonSerializerOptions { WriteIndented = true }));
            if (cannons.Count > 0 && tuned == null)
            {
                // solve the pads' launches with MphRead's own player physics in the arena just written
                string? root = fsRoot;
                if (root != null && string.Equals(Path.GetFullPath(root).TrimEnd('\\', '/'), Path.GetFullPath(Paths.FileSystem).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase))
                {
                    Register(root);
                    foreach (string line in ArenaCannons.Tune(def.Room, cannons, info.KillHeight, ray)) log?.Invoke(line);
                    WriteEntities();
                    nav = Nav();   // pads may have moved out of their cannon, or been dropped
                    info.NavNodes = nav.Nodes.Count;
                    info.NavLinks = nav.Links.Count > 0 ? nav.Links : null;
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
                $"{info.Spawns} spawns, {info.Items} items, {info.Crates.Count} crates, {info.NavNodes} nav nodes, kill height {info.KillHeight:0.0}" +
                string.Concat(info.Notes.Select(n => "\n  note: " + n)));
            progress?.Invoke(1, $"{def.InGameName}: done");
            return info;
        }

        // an actor's or platform's model (CMDL), and the actors that are not static scenery: the crates, breakables, the
        // pickups' and coin chests' parts (moved, hidden or not solid in a match) and the cannons' glows and holograms
        // (light effects, never solid: one lying over bare collision isn't what that collision is)
        const uint ActorModelId = 0xC27FFA8F;
        static readonly string[] NotStatic = { "Weapon Crate", "Coin Chest", "Randomizer Shield", "Special Pickup", "Glow", "holo", "Hologram" };
        // how near the area's bare collision (no render triangle within BareGap) a model's triangle must lie to count as
        // solid-but-undrawn there (MPH units), and the share of its triangles that must (T6 N3: Spires' pillars are baked
        // into the collision, not the render mesh, 0.99-1.00; a glow lying on a drawn floor isn't. The cannons' parts that
        // half-match (0.35-0.64 in Sidehopper Station, Spider Complex, Shooting Gallery) stay undrawn: MPH jump pads
        // stand in for the cannons)
        const float StaticGap = 0.5f, StaticShare = 0.9f, BareGap = 0.35f;

        // the active, default-layer actors and platforms whose model the area's collision follows (its solid shape is
        // there) but the render mesh doesn't draw: each placed in its own pass after the breakables', lit as Echoes lights it
        static void AddStatics(Mrea area, List<Mrea.ScriptObject> scripts, Pak pak, Func<float, float, float, Vector3> map,
            List<(Vector3 A, Vector3 B, Vector3 C)> tris, ArenaRaycaster ray, IReadOnlyList<Mrea.Light> lights, Info info, List<ArenaHdWriter.Placed> placed)
        {
            const float Cell = 2f;
            Dictionary<(int, int, int), List<int>>? grid = null;
            (int, int, int) Key(Vector3 v) => ((int)MathF.Floor(v.X / Cell), (int)MathF.Floor(v.Y / Cell), (int)MathF.Floor(v.Z / Cell));
            var models = new Dictionary<uint, Cmdl?>();
            var checkedNotes = new List<string>();
            foreach (Mrea.ScriptObject o in scripts.Where(o => o.Active && o.Layer == 0 && o.Type is "ACTR" or "PLAT"))
            {
                if (NotStatic.Any(n => o.Name.Contains(n)) || BreakDefs.Any(b => b.Type == o.Type && b.Name == o.Name)) continue;
                if (o.Root.Find(ActorModelId) is not { Data.Length: >= 4 } mp) continue;
                uint id = mp.U32;
                if (id == uint.MaxValue || !pak.Contains(id)) continue;
                if (!models.TryGetValue(id, out Cmdl? model))
                {
                    try { model = Cmdl.Load(pak.Get, id); } catch { model = null; }
                    models[id] = model;
                }
                if (model == null || model.TriangleCount == 0) continue;
                if (grid == null)
                {
                    grid = new();
                    bool[] bare = BareCollision(area, map, tris);
                    for (int i = 0; i < tris.Count; i++)
                    {
                        if (!bare[i]) continue;
                        var (a, b, c) = tris[i];
                        Vector3 lo = Vector3.ComponentMin(a, Vector3.ComponentMin(b, c)) - new Vector3(StaticGap);
                        Vector3 hi = Vector3.ComponentMax(a, Vector3.ComponentMax(b, c)) + new Vector3(StaticGap);
                        var k0 = Key(lo); var k1 = Key(hi);
                        if ((long)(k1.Item1 - k0.Item1 + 1) * (k1.Item2 - k0.Item2 + 1) * (k1.Item3 - k0.Item3 + 1) > 4000) continue;   // the bounds box
                        for (int x = k0.Item1; x <= k1.Item1; x++)
                            for (int y = k0.Item2; y <= k1.Item2; y++)
                                for (int z = k0.Item3; z <= k1.Item3; z++)
                                {
                                    if (!grid.TryGetValue((x, y, z), out List<int>? l)) grid[(x, y, z)] = l = new List<int>();
                                    l.Add(i);
                                }
                    }
                }
                float[] xf = o.Transform;
                int near = 0;
                var lo3 = new Vector3(float.MaxValue);
                var hi3 = new Vector3(float.MinValue);
                for (int t = 0; t < model.TriangleCount; t++)
                {
                    Vector3 P(int k) { Cmdl.Corner c = model.Triangles[3 * t + k]; return new Vector3(model.Positions[3 * c.P], model.Positions[3 * c.P + 1], model.Positions[3 * c.P + 2]); }
                    Vector3 e = ArenaHdWriter.Place(xf, (P(0) + P(1) + P(2)) / 3);
                    lo3 = Vector3.ComponentMin(lo3, e);
                    hi3 = Vector3.ComponentMax(hi3, e);
                    Vector3 m = map(e.X, e.Y, e.Z);
                    if (grid.TryGetValue(Key(m), out List<int>? cand) && cand.Any(i => (ClosestOnTriangle(m, tris[i].A, tris[i].B, tris[i].C) - m).LengthSquared <= StaticGap * StaticGap)) near++;
                }
                float share = near / (float)model.TriangleCount;
                checkedNotes.Add($"{o.Name} {share:0.00}");
                if (share < StaticShare) continue;
                info.Statics ??= new();
                int pass = ArenaHdWriter.FirstPlacedPass + (info.Breakables?.Count ?? 0) + info.Statics.Count;
                (float[] amb, List<float[]> lit) = EchoesCrates.Lighting(lights, (lo3 + hi3) / 2, map, ray);
                info.Statics.Add(new StaticSpot { Name = $"{o.Name} {o.Id:x8}", Pass = pass, Ambient = amb, Lights = lit });
                placed.Add(new ArenaHdWriter.Placed(id, xf, pass));
            }
            if (checkedNotes.Count > 0)
            {
                info.Notes.Add($"{info.Statics?.Count ?? 0} static actor models drawn (solid in the collision, not in the render mesh); "
                    + $"share of each model's triangles on the collision: {string.Join(", ", checkedNotes)}");
            }
        }

        // which collision triangles have no render triangle within BareGap of their centre (nothing drawn there)
        static bool[] BareCollision(Mrea area, Func<float, float, float, Vector3> map, List<(Vector3 A, Vector3 B, Vector3 C)> tris)
        {
            Cmdl geo = area.ReadGeometry();
            var render = new List<(Vector3 A, Vector3 B, Vector3 C)>();
            for (int t = 0; t < geo.TriangleCount; t++)
            {
                Vector3 P(int k) { Cmdl.Corner c = geo.Triangles[3 * t + k]; return map(geo.Positions[3 * c.P], geo.Positions[3 * c.P + 1], geo.Positions[3 * c.P + 2]); }
                render.Add((P(0), P(1), P(2)));
            }
            const float Cell = 2f;
            var grid = new Dictionary<(int, int, int), List<int>>();
            (int, int, int) Key(Vector3 v) => ((int)MathF.Floor(v.X / Cell), (int)MathF.Floor(v.Y / Cell), (int)MathF.Floor(v.Z / Cell));
            for (int i = 0; i < render.Count; i++)
            {
                var (a, b, c) = render[i];
                var k0 = Key(Vector3.ComponentMin(a, Vector3.ComponentMin(b, c)) - new Vector3(BareGap));
                var k1 = Key(Vector3.ComponentMax(a, Vector3.ComponentMax(b, c)) + new Vector3(BareGap));
                if ((long)(k1.Item1 - k0.Item1 + 1) * (k1.Item2 - k0.Item2 + 1) * (k1.Item3 - k0.Item3 + 1) > 4000) continue;   // a sky dome
                for (int x = k0.Item1; x <= k1.Item1; x++)
                    for (int y = k0.Item2; y <= k1.Item2; y++)
                        for (int z = k0.Item3; z <= k1.Item3; z++)
                        {
                            if (!grid.TryGetValue((x, y, z), out List<int>? l)) grid[(x, y, z)] = l = new List<int>();
                            l.Add(i);
                        }
            }
            var bare = new bool[tris.Count];
            for (int t = 0; t < tris.Count; t++)
            {
                Vector3 centre = (tris[t].A + tris[t].B + tris[t].C) / 3;
                bare[t] = !(grid.TryGetValue(Key(centre), out List<int>? cand)
                    && cand.Any(i => (ClosestOnTriangle(centre, render[i].A, render[i].B, render[i].C) - centre).LengthSquared <= BareGap * BareGap));
            }
            return bare;
        }

        // the point of triangle abc nearest p (Ericson, Real-Time Collision Detection 5.1.5)
        static Vector3 ClosestOnTriangle(Vector3 p, Vector3 a, Vector3 b, Vector3 c)
        {
            Vector3 ab = b - a, ac = c - a, ap = p - a;
            float d1 = Vector3.Dot(ab, ap), d2 = Vector3.Dot(ac, ap);
            if (d1 <= 0 && d2 <= 0) return a;
            Vector3 bp = p - b;
            float d3 = Vector3.Dot(ab, bp), d4 = Vector3.Dot(ac, bp);
            if (d3 >= 0 && d4 <= d3) return b;
            float vc = d1 * d4 - d3 * d2;
            if (vc <= 0 && d1 >= 0 && d3 <= 0) return a + ab * (d1 / (d1 - d3));
            Vector3 cp = p - c;
            float d5 = Vector3.Dot(ab, cp), d6 = Vector3.Dot(ac, cp);
            if (d6 >= 0 && d5 <= d6) return c;
            float vb = d5 * d2 - d1 * d6;
            if (vb <= 0 && d2 >= 0 && d6 <= 0) return a + ac * (d2 / (d2 - d6));
            float va = d3 * d6 - d5 * d4;
            if (va <= 0 && d4 - d3 >= 0 && d5 - d6 >= 0) return b + (c - b) * ((d4 - d3) / (d4 - d3 + (d5 - d6)));
            float denom = 1 / (va + vb + vc);
            return a + ab * (vb * denom) + ac * (vc * denom);
        }

        // a recipe's tuned pads onto the cannons this disc gives: one each, in order, from the same ball slot (else the
        // recipe was tuned for other cannons: refused rather than shipping pads that miss)
        static void ApplyTuning(Def def, List<ArenaCannons.Cannon> found, IReadOnlyList<ArenaCannons.Cannon> tuned)
        {
            if (found.Count != tuned.Count)
            {
                throw new InvalidDataException($"{def.InGameName}: the recipe tunes {tuned.Count} jump pads, the arena has {found.Count} cannons");
            }
            for (int i = 0; i < found.Count; i++)
            {
                ArenaCannons.Cannon c = found[i], t = tuned[i];
                if ((c.SlotV - t.SlotV).Length > 0.01f)
                {
                    throw new InvalidDataException($"{def.InGameName}: jump pad {i + 1} of the recipe is for a cannon at {t.SlotV}, the arena's is at {c.SlotV}");
                }
                // the target too: Tune can fall back from the one Find picked (R7b: a far side no launch reaches)
                if (t.Target.Length == 3) c.Target = t.Target;
                c.Pad = t.Pad; c.Launch = t.Launch; c.Plan = t.Plan; c.LockFrames = t.LockFrames;
                c.Landed = t.Landed; c.Miss = t.Miss; c.AltMiss = t.AltMiss; c.AltLanded = t.AltLanded; c.AltOk = t.AltOk; c.Dropped = t.Dropped;
            }
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

        // the player's own files folder (recomp_settings.json's; Android: the external files dir), where the in-app import
        // (ImportService, kind "arenas": Core Import/Echoes) writes <ImportRoot>/arenas/<Key>/. Set by the app before it
        // lists or starts matches. An arena imported there takes the place of one staged into the game's file system.
        public static string? ImportRoot { get; set; }

        public const string ImportKind = "arenas";

        public static string ItemDir(string importRoot, Def d) => Path.Combine(importRoot, ImportKind, d.Key);

        // room name -> the folder of its HD look (CampaignArena.cs), for the arenas registered
        static readonly Dictionary<string, string> _hdDirs = new();

        // add every arena imported under root (the MPH file system's layout) to MphRead's room table; returns how many
        // (a public build uses only the in-app import's folders: a copy staged by a PC tool isn't something it makes)
        public static int Register(string root) => MphRecomp.Config.RecompSettings.PublicBuild ? 0 : RegisterFrom(d => ArchiveDir(root, d), folder: false);

        // the same for the in-app import's item folders under importRoot (room files read from there: RoomMetadata's
        // hostFolder)
        public static int RegisterImported(string importRoot) => RegisterFrom(d => ItemDir(importRoot, d), folder: true);

        // an arena's room files are there (what RegisterFrom registers)
        static bool HasArena(string dir, Def d) => File.Exists(Path.Combine(dir, $"{d.Archive}_Model.bin")) && File.Exists(Path.Combine(dir, "arena.json"));

        // an import item folder is used only when it's imported and up to date (MR1: a stale arena leaves the list until
        // it's imported again; ImportedContent)
        static bool HasImported(string importRoot, Def d) =>
            HasArena(ItemDir(importRoot, d), d) && MphRecomp.Import.ImportedContent.Usable(importRoot, ImportKind, d.Key);

        static int RegisterFrom(Func<Def, string> dirOf, bool folder)
        {
            int count = 0;
            foreach (Def d in All)
            {
                string dir = dirOf(d);
                string json = Path.Combine(dir, "arena.json");
                if (!HasArena(dir, d)) continue;
                if (folder && !MphRecomp.Import.ImportedContent.Usable(Path.GetDirectoryName(dir), d.Key)) continue;
                Info? info = JsonSerializer.Deserialize<Info>(File.ReadAllText(json));
                if (info == null) continue;
                RoomMetadata meta = Room(d, info, folder ? dir : null);
                Metadata.AddHostRoom(meta);
                string hd = folder ? dir : Path.Combine(dir, ArenaHdWriter.Dir);
                lock (_hdDirs) _hdDirs[d.Room] = hd;
                // a picture from before the thumbnail's current look: drawn again in the background, aimed at the spawns and
                // pickups as the import aims it
                if (ArenaThumbnail.Stale(hd) && meta.EntityPath is string entities)
                {
                    ArenaThumbnail.RefreshStale(hd, () => Read.GetEntities(entities, 0, firstHunt: false)
                        .Where(e => e.Type is EntityType.PlayerSpawn or EntityType.ItemSpawn)
                        .Select(e => new System.Numerics.Vector3(e.Position.X, e.Position.Y, e.Position.Z)).ToList());
                }
                EchoesCrates.SetRoom(d.Id, info);
                EchoesBreakables.SetRoom(d.Id, info);
                EchoesTurret.SetRoom(d.Id, info);
                EchoesStatics.SetRoom(d.Id, info);
                EchoesMovement.SetRoom(d.Id, info);
                ArenaNav.SetRoom(d.Id, info);
                count++;
            }
            return count;
        }

        // where a registered Echoes room's HD look is (null: not one)
        public static string? HdDir(string roomName)
        {
            lock (_hdDirs) return _hdDirs.TryGetValue(roomName, out string? dir) ? dir : null;
        }

        public static bool IsEchoesRoom(string roomName) => All.Any(d => d.Room == roomName);

        static string? _registeredStamp;

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
                string top = rel.Replace('\\', '/').Split('/')[0];
                // only the file system's own folders: the staging folder (Android: <external files>/arenas) is also where
                // the in-app import keeps its item folders (arenas/<Key>/), which are read where they are
                if (top is not ("_archives" or "levels")) continue;
                if (!Path.GetFileName(rel).StartsWith("echoes_") && !rel.Replace('\\', '/').Contains("/echoes_")) continue;
                string dst = Path.Combine(root, rel);
                var si = new FileInfo(src);
                var di = new FileInfo(dst);
                if (di.Exists && di.Length == si.Length && di.LastWriteTimeUtc >= si.LastWriteTimeUtc) continue;
                Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                File.Copy(src, dst, overwrite: true);
                copied++;
            }
            _registeredStamp = null;
            EnsureRegistered();
            return copied;
        }

        // register the imported arenas once the MPH file system is known (MatchArenas.All calls this), and again when the
        // import root changes or an import there rewrote an arena
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
            if (!Directory.Exists(root)) return;
            string? imports = ImportRoot;
            string stamp = root + "|" + imports;
            if (imports != null)
            {
                foreach (Def d in All) stamp += "|" + File.GetLastWriteTimeUtc(Path.Combine(ItemDir(imports, d), "arena.json")).Ticks;
                stamp += "|" + File.GetLastWriteTimeUtc(Path.Combine(imports, "import", "mp2.json")).Ticks;   // the import record (stale or not)
            }
            if (stamp == _registeredStamp) return;
            // an arena cleared on IMPORT GAMES with no staged copy behind it leaves the room table (registering only adds)
            foreach (Def d in All)
            {
                if ((!MphRecomp.Config.RecompSettings.PublicBuild && HasArena(ArchiveDir(root, d), d)) || (imports != null && HasImported(imports, d))) continue;
                Metadata.RemoveHostRoom(d.Room);
                lock (_hdDirs) _hdDirs.Remove(d.Room);
                EchoesCrates.SetRoom(d.Id, null);
                EchoesBreakables.SetRoom(d.Id, null);
                EchoesTurret.SetRoom(d.Id, null);
                EchoesStatics.SetRoom(d.Id, null);
                EchoesMovement.SetRoom(d.Id, null);
                ArenaNav.SetRoom(d.Id, null);
            }
            Register(root);
            if (imports != null) RegisterImported(imports);   // after: an imported arena replaces a staged one
            _registeredStamp = stamp;
        }

        static RoomMetadata Room(Def d, Info i, string? folder)
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
                multiplayer: true,
                hostFolder: folder);
        }
    }
}
