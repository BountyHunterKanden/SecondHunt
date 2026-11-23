using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using MphRead;
using MphRead.Entities;
using MphRead.Formats;
using MphRead.Formats.Collision;
using MphRecomp.Arenas;
using MphRecomp.Render;
using OpenTK.Mathematics;

namespace MphRecomp.Holiday
{
    // HOLIDAY MODE's snow on the ground (owner 2026-11-20: "vanilla snow on ground if possible for rooms that are outside"):
    // a layer of the game's snow (Fault Line's lambert_Snow texture, tiled as Fault Line tiles it) over the room's own
    // up-facing floors. Draw only: a model of its own drawn with the room (RoomEntity.HostOverlay), never in the room's
    // collision, so play is the same.
    //
    // One smooth surface, not the floor's own triangles (owner 2026-11-20: "it should just be snow. thats it", "smooth like
    // snow has really been falling on it all day"): a grid of columns Cell apart over the room, each column's floor heights
    // (one per level), neighbouring columns at about the same height joined into one sheet. The depth at each point comes
    // from the sky above it, deeper against walls and in corners, thinner on slopes; under cover it drifts in from the open
    // edge and thins the further under it is (a trench under a walkway). Every edge of the sheet is closed: rounded down to
    // the floor where the floor goes on, a skirt against a wall, and at a platform's edge a lip that bulges out and hangs
    // over, as Phendrana Shorelines' snow does (its numbers: docs/HOLIDAY_SNOW.md), no longer than the platform is thick.
    //
    // Built once per room and kept in a cache file (the room's geometry hashed into its name), so a room's second visit
    // reads it back; the first build holds the room's load until it's done (owner 2026-11-20: "i also dont want the map
    // loading before the snow does"). A room whose own floors are already snow (Fault Line, Phendrana Shorelines) gets none.
    public static class HolidayGround
    {
        const int Version = 10; // bump when the shape changes: the old cache files are then left unread (9: DrawsSky; 10: no
                                // collision ceilings in imported arenas, HolidaySnow.CeilingSource)
        const string SnowRoom = "UNIT4_RM5"; // Fault Line
        const string SnowMaterial = "lambert_Snow";
        const string ShorelinesRoom = "PRIME PHENDRANA SHORELINES"; // all snow already (its mesh is the snow)
        const float Flat = 0.6f; // the floor's normal at least this upright
        const float Lift = 0.05f; // above the floor
        const float Tile = 4f; // world units per texture repeat (Fault Line's own: ~33 texels a unit on 128)
        const float MaxTriangle = 60f; // wider: the sky dome or a backdrop (as HolidaySnow's roofs)
        const float SnowFloorArea = 50f; // more of the room's own snow floor than this: snow on the ground already
        const float Cell = 0.5f; // the grid's spacing (coarser in a huge room, MaxColumns at most)
        const int MaxColumns = 250_000;
        const float MaxStep = 0.7f; // neighbouring columns this close in height are one sheet
        const int CrevasseColumns = 2; // a crack or groove at most this many columns wide between floors at one height: bridged
        const float Depth = 0.22f; // in the open
        const float Drift = 1.6f, WallReach = 1.8f; // up to (1 + Drift) times deeper against walls and in corners
        // nothing drawn above at all: open sky. Any height counts as cover, as for the falling snow (HolidaySnow.Map): a cave
        // roof 50 units up left all of Monitor Station open at the old 40 (owner 2026-11-21)
        const float Open = 1e6f;
        const float Thin = 0.03f; // thinner than this: no snow (its edge cut along where the depth reaches it)
        static readonly Vector3 Shorelines = new Vector3(194, 205, 205); // Phendrana Shorelines' snow, its texture's average

        static Scene? _scene;
        static int _roomId = -1;
        static ModelInstance? _cover;
        static bool _built;

        // what the build reads: the floors the snow may lie on, everything drawn (what shelters it), and the snow's material
        internal sealed class Source
        {
            public Model Room = null!, Snow = null!;
            public Material Mat = null!;
            public Texture Tex;
            public float Scale, Shade;
            public Vector3 Tint = Vector3.One;
            public readonly List<(Vector3 A, Vector3 B, Vector3 C)> Floors = new(), Solids = new();
            public int RoomId;
            public HolidaySnow.RoofMap Map = null!; // where the room sees the sky (HolidaySnow.BuildMap)
        }

        // RoomEntity.HostOverlay: the cover for the room being drawn
        static ModelInstance? Overlay(RoomEntity room)
        {
            return HolidaySnow.Enabled && _cover != null && _scene != null && _scene.RoomId == _roomId ? _cover : null;
        }

        // from HolidaySnow.Frame (early: the snow HolidaySnow.PrepareEarly made alongside the room's load)
        public static void Frame(Scene scene) => Frame(scene, null);

        internal static void Frame(Scene scene, Early? early)
        {
            RoomEntity.HostOverlay = Overlay;
            if (scene != _scene || scene.RoomId != _roomId)
            {
                _scene = scene;
                _roomId = scene.RoomId;
                _cover = null;
                _built = false;
            }
            if (_built)
            {
                return;
            }
            _built = true;
            try
            {
                if (early == null || !Adopt(scene, early)) Build(scene);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Holiday ground snow: {ex.Message}");
            }
        }

        // on the game thread, before the room's first frame is drawn: the snow is there from the start
        static void Build(Scene scene)
        {
            var sw = Stopwatch.StartNew();
            if (Gather(scene) is not Source src)
            {
                return;
            }
            long read = sw.ElapsedMilliseconds;
            string path = CachePath(src);
            long hashed = sw.ElapsedMilliseconds;
            // the background build on this very room: wait for it rather than build it twice
            lock (_shapeLock)
            {
                while (_shaping == path)
                {
                    System.Threading.Monitor.Wait(_shapeLock);
                }
            }
            long waited = sw.ElapsedMilliseconds;
            bool ahead = TakeAhead(src.RoomId, path, out List<RenderInstruction>? list, out int triangles);
            LastReadAhead = ahead;
            if (!ahead) list = Load(path, out triangles);
            bool cached = list != null;
            if (list == null)
            {
                list = Shape(src, out triangles);
                Save(path, src.RoomId, list, triangles);
            }
            long got = sw.ElapsedMilliseconds, made = got;
            if (triangles > 0)
            {
                Model model = ToModel(src, list);
                made = sw.ElapsedMilliseconds;
                scene.LoadModel(model, isRoom: true);
                _cover = new ModelInstance(model);
            }
            Console.WriteLine($"Holiday ground snow: room {src.RoomId}, {triangles} triangles, "
                + $"{(ahead ? "read ahead" : cached ? "read from the cache" : "built")} in {sw.ElapsedMilliseconds} ms (reading the room {read} ms, key {hashed - read},"
                + $" wait {waited - hashed}, {(cached ? "cache" : "build")} {got - waited}, model {made - got}, scene {sw.ElapsedMilliseconds - made})");
        }

        static Source? Gather(Scene scene)
        {
            if (scene.Room == null || scene.Room.GetModels().Count == 0)
            {
                return null;
            }
            if (HolidaySnow.Map is not HolidaySnow.RoofMap map || map.RoomId != scene.RoomId)
            {
                return null;
            }
            RoomMetadata? meta = Metadata.GetRoomById(scene.RoomId, noThrow: true);
            return Gather(scene.Room.GetModels()[0].Model, meta, scene.RoomId, Read.GetRoomModelInstance(SnowRoom).Model,
                HolidaySnow.HdGlass(scene), HolidaySnow.CeilingFill, map);
        }

        // the same from plain inputs (Prebuild's background build reads its own copies): the room as its layer draws it,
        // Fault Line's model, the arena's glass, and HolidaySnow.BuildMap's sky map and the ceiling it filled in
        static Source? Gather(Model room, RoomMetadata? meta, int roomId, Model snow, List<(Vector3 A, Vector3 B, Vector3 C)> glass,
            List<(Vector3 A, Vector3 B, Vector3 C)> ceiling, HolidaySnow.RoofMap map)
        {
            // the falling snow's rule: a room that sees the sky nowhere is indoors and gets none (Main Gyro Chamber, Data
            // Shrine, owner 2026-11-21: "maps that are fully inside and dont have sky visible SHOULD NEVER have snow")
            if (!map.AnyOpen)
            {
                Console.WriteLine($"Holiday ground snow: room {roomId} sees no sky, indoors, no snow");
                return null;
            }
            if (meta?.Name == ShorelinesRoom)
            {
                Console.WriteLine("Holiday ground snow: Phendrana Shorelines is snow already, none added");
                return null;
            }
            int snowMat = -1;
            for (int i = 0; i < snow.Materials.Count; i++)
            {
                if (snow.Materials[i].Name == SnowMaterial) snowMat = i;
            }
            if (snowMat == -1)
            {
                return null;
            }
            var src = new Source { Room = room, Snow = snow, Mat = snow.Materials[snowMat], Scale = room.Scale.X, RoomId = roomId, Map = map };
            src.Tex = snow.Recolors[0].Textures[src.Mat.TextureId];
            src.Tint = Tint(snow, src.Mat);
            var animated = new HashSet<string>();
            foreach (MaterialAnimationGroup g in room.AnimationGroups.Material) animated.UnionWith(g.Animations.Keys);
            foreach (TexcoordAnimationGroup g in room.AnimationGroups.Texcoord) animated.UnionWith(g.Animations.Keys);
            foreach (TextureAnimationGroup g in room.AnimationGroups.Texture) animated.UnionWith(g.Animations.Keys);

            float snowFloor = 0; // the room's own snow floors, square units
            float lit = 0, litArea = 0; // the floors' light (their vertex colour, as grey), by area
            // room space times the scale, not node.Animation: that is only set once the room draws, and an imported arena's
            // stand-in room is hidden behind its HD mesh (HolidaySnow.BuildRoofs)
            Matrix4 toWorld = Matrix4.CreateScale(room.Scale);
            // an imported arena's stand-in (ArenaModelWriter) is all one untextured material, solid surfaces only (its
            // blended ones left out): every floor of it counts, else Main Quarry got no piles at all (owner 2026-11-20)
            bool standIn = meta != null && EchoesArena.IsEchoesRoom(meta.Name);
            foreach (Node node in room.Nodes)
            {
                if (!HolidaySnow.Drawn(room, node))
                {
                    continue; // the sky, or a node the room's layer switches off (a single-player door blocker)
                }
                int start = node.MeshId / 2;
                for (int k = 0; k < node.MeshCount; k++)
                {
                    Mesh mesh = room.Meshes[start + k];
                    if (!mesh.Visible)
                    {
                        continue;
                    }
                    Material m = room.Materials[mesh.MaterialId];
                    // a floor the snow can lie on: solid, still, not lava or water
                    bool floor = !(m.RenderMode != RenderMode.Normal || (m.TextureId == -1 && !standIn) || animated.Contains(m.Name)
                        || Has(m.Name, "lava") || Has(m.Name, "water") || Has(m.Name, "acid")
                        || Has(m.Name, "ice") || Has(m.Name, "glass") || Has(m.Name, "light") || Has(m.Name, "glow"));
                    bool snowy = Has(m.Name, "snow");
                    float[] v = DsDisplayList.Decode(room, mesh, isRoom: true);
                    int s = DsDisplayList.Stride;
                    for (int o = 0; o + 3 * s <= v.Length; o += 3 * s)
                    {
                        Vector3 a = Vector3.TransformPosition(new Vector3(v[o], v[o + 1], v[o + 2]), toWorld);
                        Vector3 b = Vector3.TransformPosition(new Vector3(v[o + s], v[o + s + 1], v[o + s + 2]), toWorld);
                        Vector3 c = Vector3.TransformPosition(new Vector3(v[o + 2 * s], v[o + 2 * s + 1], v[o + 2 * s + 2]), toWorld);
                        Vector3 n = Vector3.Cross(b - a, c - a);
                        Vector3 min = Vector3.ComponentMin(a, Vector3.ComponentMin(b, c));
                        Vector3 max = Vector3.ComponentMax(a, Vector3.ComponentMax(b, c));
                        if (n.LengthSquared < 1e-8f || max.X - min.X > MaxTriangle || max.Z - min.Z > MaxTriangle)
                        {
                            continue;
                        }
                        src.Solids.Add((a, b, c));
                        float area = n.Length / 2;
                        n.Normalize();
                        if (!floor || n.Y < Flat)
                        {
                            continue;
                        }
                        if (snowy)
                        {
                            snowFloor += area;
                            continue;
                        }
                        src.Floors.Add((a, b, c));
                        for (int q = 0; q < 3; q++)
                        {
                            int i = o + q * s;
                            lit += area / 3 * (v[i + 9] == 1 ? 0.3f * v[i + 6] + 0.59f * v[i + 7] + 0.11f * v[i + 8] : 1);
                        }
                        litArea += area;
                    }
                }
            }
            // an imported arena's glass (left out of its stand-in): it shelters what's under it, and snow lies on its top
            // (owner 2026-11-20 point 4: snow on the booth's glass roof)
            foreach ((Vector3 a, Vector3 b, Vector3 c) in glass)
            {
                Vector3 n = Vector3.Cross(b - a, c - a);
                if (n.LengthSquared < 1e-8f)
                {
                    continue;
                }
                src.Solids.Add((a, b, c));
                if (MathF.Abs(n.Normalized().Y) >= Flat) // either winding: the glass may face in
                {
                    src.Floors.Add((a, b, c));
                }
            }
            // the ceiling the drawn room leaves out (HolidaySnow.FillCeilings: Incubation Vault's glass panes) shelters the
            // ground under it; no snow lies on it (it isn't drawn)
            src.Solids.AddRange(ceiling);
            // a room that already has snow on its ground gets no piles anywhere (owner 2026-11-20: "levels that already
            // have snow on the ground do not need snow piles anywhere")
            if (src.Floors.Count == 0 || snowFloor > SnowFloorArea)
            {
                Console.WriteLine($"Holiday ground snow: room {roomId}, none ({(src.Floors.Count == 0 ? "no floors" : $"its own snow on the ground, {snowFloor:0} sq units")})");
                return null;
            }
            src.Shade = MathF.Min(1, 0.3f + 0.75f * lit / litArea);
            return src;
        }

        // the vertex colour's hue that turns the snow texture's average into Phendrana Shorelines' off-white (its brightness
        // left to the light)
        static Vector3 Tint(Model snow, Material mat)
        {
            try
            {
                var sum = Vector3.Zero;
                int count = 0;
                foreach (ColorRgba c in snow.GetPixels(mat.TextureId, mat.PaletteId, 0))
                {
                    if (c.Alpha > 0)
                    {
                        sum += new Vector3(c.Red, c.Green, c.Blue);
                        count++;
                    }
                }
                if (count == 0 || sum.X <= 0 || sum.Y <= 0 || sum.Z <= 0)
                {
                    return Vector3.One;
                }
                Vector3 t = Shorelines / (sum / count);
                return t / MathF.Max(t.X, MathF.Max(t.Y, t.Z));
            }
            catch (Exception)
            {
                return Vector3.One;
            }
        }

        // ---- the cache: one file per room, named by its geometry ----

        const uint Magic = 0x574E5348; // "HSNW"

        // the app's cache folder (AppPlatform.CacheDir, set before a match), else the temp folder: the snow lasts there
        public static string? CacheRoot;

        static string CacheDir => Path.Combine(CacheRoot ?? Path.Combine(Path.GetTempPath(), "SecondHunt"), "holiday_snow");

        static string CachePath(Source src)
        {
            ulong h = 14695981039346656037UL;
            void Add(float f)
            {
                uint bits = (uint)BitConverter.SingleToInt32Bits(f);
                for (int i = 0; i < 4; i++)
                {
                    h = (h ^ (bits >> (8 * i) & 0xFF)) * 1099511628211UL;
                }
            }
            void AddAll(List<(Vector3 A, Vector3 B, Vector3 C)> tris)
            {
                Add(tris.Count);
                foreach ((Vector3 a, Vector3 b, Vector3 c) in tris)
                {
                    Add(a.X); Add(a.Y); Add(a.Z); Add(b.X); Add(b.Y); Add(b.Z); Add(c.X); Add(c.Y); Add(c.Z);
                }
            }
            Add(Version);
            Add(src.Scale);
            Add(src.Shade);
            Add(src.Tint.X); Add(src.Tint.Y); Add(src.Tint.Z);
            Add(src.Tex.Width); Add(src.Tex.Height);
            AddAll(src.Floors);
            AddAll(src.Solids);
            return Path.Combine(CacheDir, $"{src.RoomId}_v{Version}_{h:x16}.bin");
        }

        // ---- made alongside the menus' backdrop's room load (HolidaySnow.PrepareEarly) ----
        // The backdrop's worker built the snow after its room (Gather, the cache read, the model): 159 ms of the Odin's cold
        // start on Fault Line, 438 on Mining Plaza (2026-11-22, owner: "can we get back to instant loading on initial game
        // load and match quit while keeping snowfall"). From the room's own copies (SkyMap, as the background build reads
        // them) it all runs beside the room load, and the worker only makes the model.

        // the room's snow: none (Src null: indoors, snow already, no snow material) or its instructions
        internal sealed record Early(int RoomId, Source? Src, List<RenderInstruction>? List, int Triangles, string How, long GatherMs, long GotMs);

        static Model? _earlySnow; // Fault Line's model, its own copy (Read.ReadRoomModelUncached), kept for the next backdrop

        // any thread: the room's snow from its own copy, the sky map and ceiling SkyMap made (null: not made, Build does it)
        internal static Early? MakeEarly(Model room, RoomMetadata meta, List<(Vector3 A, Vector3 B, Vector3 C)> glass,
            List<(Vector3 A, Vector3 B, Vector3 C)> ceiling, HolidaySnow.RoofMap map)
        {
            var sw = Stopwatch.StartNew();
            Model? snow = _earlySnow;
            if (snow == null && Metadata.GetRoomByName(SnowRoom).Item1 is RoomMetadata snowMeta)
            {
                _earlySnow = snow = Read.ReadRoomModelUncached(snowMeta);
            }
            if (snow == null)
            {
                return null;
            }
            Source? src = Gather(room, meta, meta.Id, snow, glass, ceiling, map);
            long gathered = sw.ElapsedMilliseconds;
            if (src == null)
            {
                return new Early(meta.Id, null, null, 0, "none", gathered, gathered);
            }
            string path = CachePath(src);
            lock (_shapeLock)
            {
                while (_shaping == path)
                {
                    System.Threading.Monitor.Wait(_shapeLock);
                }
            }
            bool ahead = TakeAhead(src.RoomId, path, out List<RenderInstruction>? list, out int triangles);
            if (!ahead) list = Load(path, out triangles);
            string how = ahead ? "read ahead" : list != null ? "read from the cache" : "built";
            if (list == null)
            {
                list = Shape(src, out triangles);
                Save(path, src.RoomId, list, triangles);
            }
            return new Early(meta.Id, src, list, triangles, how, gathered, sw.ElapsedMilliseconds);
        }

        // Build with what MakeEarly made: only the model, into the scene (false: not this room's, Build does it)
        static bool Adopt(Scene scene, Early early)
        {
            if (early.RoomId != scene.RoomId)
            {
                return false;
            }
            var sw = Stopwatch.StartNew();
            LastReadAhead = early.How == "read ahead";
            if (early.Src is Source src && early.List != null && early.Triangles > 0)
            {
                Model model = ToModel(src, early.List);
                scene.LoadModel(model, isRoom: true);
                _cover = new ModelInstance(model);
            }
            Console.WriteLine($"Holiday ground snow: room {early.RoomId}, {early.Triangles} triangles, {early.How} beside the room load"
                + $" in {early.GotMs} ms (reading the room {early.GatherMs}, {(early.How == "built" ? "build" : "cache")} {early.GotMs - early.GatherMs}), model + scene {sw.ElapsedMilliseconds} ms after it");
            return true;
        }

        // ---- read ahead: the menus' next backdrop's snow, read from its cache while a game's pause menu is up ----
        // Back from a game the menus wait for their arena backdrop's load (CampaignHost.StartMenuBackdrop), which only starts
        // once QUIT's YES is pressed; launch has seconds to hide it. Big snow rooms' cache reads are 160+ ms of that on the
        // Odin (Crossfire Chaos 162, Main Quarry more; owner 11-22: "the menu maps on inital game load are instant even
        // with snow why cant we get this to act the same way"). So the pause menu opening (MenuArenaBackdrop.ReadAhead)
        // picks the next backdrop and reads its snow here on a worker, by the background build's index (prebuilt.txt: the
        // room as the backdrop loads it, Battle with 4 players); Build takes it when its own key is the same file.

        sealed record Ahead(int RoomId, string Path, List<RenderInstruction>? List, int Triangles);
        static System.Threading.Tasks.Task<Ahead?>? _ahead;
        static int _aheadRoom = -1; // the room _ahead reads

        // ReadAhead unless meta's snow is being read ahead already (the pause menu did, before a quit to the menus)
        internal static void ReadAheadOnce(RoomMetadata meta)
        {
            if (_ahead == null || _aheadRoom != meta.Id) ReadAhead(meta);
        }

        // any thread (the game's, its pause menu just opened): meta's snow in the backdrop's layers read on a worker
        public static void ReadAhead(RoomMetadata meta)
        {
            if (!HolidaySnow.Enabled || CacheRoot == null)
            {
                return;
            }
            int mask = SceneSetup.GetNodeLayer(GameMode.Battle, meta.NodeLayer, 4); // (CampaignHost.StartMenuBackdrop's layers)
            _aheadRoom = meta.Id;
            _ahead = System.Threading.Tasks.Task.Run(() =>
            {
                var sw = Stopwatch.StartNew();
                try
                {
                    if (!Directory.Exists(CacheDir)) return null;
                    string indexPath = Path.Combine(CacheDir, "prebuilt.txt");
                    string key = $"{Version}|{meta.Name}|{mask}|{Stamp(meta)}\t";
                    string? line = File.Exists(indexPath)
                        ? File.ReadLines(indexPath).LastOrDefault(l => l.StartsWith(key, StringComparison.Ordinal)) : null;
                    if (line != null && line.Length == key.Length) return null; // no snow there
                    // not built ahead (a match reached it first): the room's one file of this version, if it has only one
                    string[] files = line == null ? Directory.GetFiles(CacheDir, $"{meta.Id}_v{Version}_*.bin") : Array.Empty<string>();
                    if (line == null && files.Length != 1) return null;
                    string path = line != null ? Path.Combine(CacheDir, line[key.Length..]) : files[0];
                    List<RenderInstruction>? list = Load(path, out int triangles);
                    Console.WriteLine($"Holiday ground snow: {meta.Name} read ahead for the menus, {triangles} triangles in {sw.ElapsedMilliseconds} ms");
                    return list == null ? null : new Ahead(meta.Id, path, list, triangles);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Holiday ground snow: {meta.Name} not read ahead ({ex.Message})");
                    return null;
                }
            });
        }

        // the pause menu closed again, or the menus' backdrop is live: what was read ahead goes
        public static void DropAhead() => _ahead = null;

        public static bool LastReadAhead { get; private set; } // (the last Build took its snow from ReadAhead: -holidaybackdroptest)

        // Build: the snow read ahead, when it's this very file (a read still going is waited for: it's further along)
        static bool TakeAhead(int roomId, string path, out List<RenderInstruction>? list, out int triangles)
        {
            list = null;
            triangles = 0;
            var task = _ahead;
            if (task == null) return false;
            Ahead? ahead;
            try { ahead = task.Result; }
            catch (AggregateException) { ahead = null; }
            if (ahead == null || ahead.RoomId != roomId || ahead.Path != path) return false;
            _ahead = null;
            list = ahead.List;
            triangles = ahead.Triangles;
            return true;
        }

        // ---- the cover's vertices decoded on a worker (the menus' backdrop): its first draw only uploads them ----
        // (DsDisplayList.Decode of Crossfire Chaos's 113k triangles was 83 ms of the menus' first frame on the Odin)
        static System.Threading.Tasks.Task<(Model Model, int DlistId, float[] Verts)>? _coverDecode;

        // the backdrop's worker, after its room's snow is made (CampaignHost.StartMenuBackdrop): runs on while it returns
        public static void DecodeCoverAhead(Scene scene)
        {
            if (!HolidaySnow.Enabled || scene != _scene || scene.RoomId != _roomId || _cover?.Model is not Model model
                || model.Meshes.Count != 1)
            {
                _coverDecode = null;
                return;
            }
            Mesh mesh = model.Meshes[0];
            _coverDecode = System.Threading.Tasks.Task.Run(() => (model, mesh.DlistId, MphRecomp.Render.DsDisplayList.Decode(model, mesh, isRoom: true)));
        }

        // the GL thread's first upload of a room mesh (CampaignRenderer.TakePredecoded): the cover's vertices, once
        public static float[]? TakeCoverVerts(Model model, Mesh mesh)
        {
            var task = _coverDecode;
            if (task == null || !ReferenceEquals(model, _cover?.Model)) return null;
            _coverDecode = null;
            try
            {
                var (m, dlist, verts) = task.Result;
                return ReferenceEquals(m, model) && dlist == mesh.DlistId ? verts : null;
            }
            catch (AggregateException)
            {
                return null; // decoded on the GL thread as before
            }
        }

        static List<RenderInstruction>? Load(string path, out int triangles)
        {
            triangles = 0;
            try
            {
                if (!File.Exists(path))
                {
                    return null;
                }
                // decompressed whole, then parsed from memory: a BinaryReader on the GZipStream took 4 bytes a call (Main
                // Quarry's 135k triangles: 1.27 s on the Odin, and the menus wait for their backdrop's load, owner 11-21).
                // An instruction with the same code and arguments as an earlier one is that one (RenderInstruction never
                // changes): normals, texture coordinates, colours and shared vertices repeat across the sheet
                byte[] data;
                using (var gz = new GZipStream(File.OpenRead(path), CompressionMode.Decompress))
                using (var ms = new MemoryStream())
                {
                    gz.CopyTo(ms);
                    data = ms.GetBuffer();
                    Array.Resize(ref data, (int)ms.Length);
                }
                ReadOnlySpan<byte> d = data;
                int at = 0;
                uint U32()
                {
                    uint v = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(at, 4));
                    at += 4;
                    return v;
                }
                if (U32() != Magic || (int)U32() != Version)
                {
                    return null;
                }
                triangles = (int)U32();
                int count = (int)U32();
                var list = new List<RenderInstruction>(count);
                var shared = new Dictionary<(uint Code, uint A, uint B), RenderInstruction>();
                for (int i = 0; i < count; i++)
                {
                    uint code = U32();
                    int n = d[at++];
                    if (n <= 2)
                    {
                        uint a0 = n > 0 ? U32() : 0, a1 = n > 1 ? U32() : 0;
                        var key = (code | (uint)n << 28, a0, a1);
                        if (!shared.TryGetValue(key, out RenderInstruction? ins))
                        {
                            ins = RenderInstruction.HostUnchecked((InstructionCode)code,
                                n == 0 ? Array.Empty<uint>() : n == 1 ? new[] { a0 } : new[] { a0, a1 });
                            shared[key] = ins;
                        }
                        list.Add(ins);
                        continue;
                    }
                    var args = new uint[n];
                    for (int k = 0; k < n; k++)
                    {
                        args[k] = U32();
                    }
                    list.Add(RenderInstruction.HostUnchecked((InstructionCode)code, args));
                }
                return list;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Holiday ground snow: cache unreadable ({ex.Message}), building again");
                triangles = 0;
                return null;
            }
        }

        static void Save(string path, int roomId, List<RenderInstruction> list, int triangles)
        {
            try
            {
                Directory.CreateDirectory(CacheDir);
                // an older version's files for the room (this version's stay: the room's layers differ by player count and mode)
                foreach (string old in Directory.GetFiles(CacheDir, $"{roomId}_*.bin"))
                {
                    if (!Path.GetFileName(old).StartsWith($"{roomId}_v{Version}_", StringComparison.Ordinal)) File.Delete(old);
                }
                string temp = path + "." + Environment.CurrentManagedThreadId + ".tmp";
                using (var w = new BinaryWriter(new GZipStream(File.Create(temp), CompressionLevel.Fastest)))
                {
                    w.Write(Magic);
                    w.Write(Version);
                    w.Write(triangles);
                    w.Write(list.Count);
                    foreach (RenderInstruction ins in list)
                    {
                        w.Write((uint)ins.Code);
                        w.Write((byte)ins.Arguments.Count);
                        foreach (uint arg in ins.Arguments)
                        {
                            w.Write(arg);
                        }
                    }
                }
                File.Move(temp, path, overwrite: true);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Holiday ground snow: cache not written ({ex.Message})");
            }
        }

        // ---- built ahead: every arena's snow in the background, so a match only reads it (owner 2026-11-21: "Can we get
        // this to load at import and save load time on matches?") ----
        //
        // From the menus (CampaignRenderer.ReloadAimFromSettings) and when an import finishes (ImportGames.Tick): each
        // multiplayer arena as Battle and Capture with 2 and with 4 players (its layers), then the campaign's rooms. Its
        // own copies of each room (Read.ReadRoomModelUncached, Collision.GetCollision with the room's layer), a quarter of
        // the cores, one room at a time, and it waits while a match is played (MatchPlaying). A room it already did (its
        // files' sizes and times unchanged, prebuilt.txt) is skipped without reading. A room a match reaches first is
        // built there as before.

        static readonly object _shapeLock = new();
        static string? _shaping; // the cache file the background build is shaping now
        static System.Threading.Thread? _worker;
        static List<(RoomMetadata Meta, int Mask)>? _again; // the rooms for one more round (an import finished during a build)
        static long _playingAt; // Stopwatch ticks of the last frame of a match being played
        static readonly ParallelOptions AllCores = new();

        // CampaignRenderer's tick in a match (not the menus' backdrop): the background build waits
        public static bool Prebuilding { get { lock (_shapeLock) return _worker != null; } }

        public static void MatchPlaying() => System.Threading.Interlocked.Exchange(ref _playingAt, Stopwatch.GetTimestamp());

        static bool Playing => Stopwatch.GetElapsedTime(System.Threading.Interlocked.Read(ref _playingAt)).TotalSeconds < 3;

        // again: rooms were just imported, so a build already running goes round once more
        public static void Prebuild(bool again = false)
        {
            if (!HolidaySnow.Enabled || CacheRoot == null)
            {
                return;
            }
            try
            {
                if (!Directory.Exists(Paths.FileSystem))
                {
                    return; // nothing imported yet
                }
            }
            catch (Exception)
            {
                return;
            }
            List<(RoomMetadata Meta, int Mask)> rooms;
            try
            {
                rooms = PrebuildRooms();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Holiday ground snow: no rooms to build ahead ({ex.Message})");
                return;
            }
            lock (_shapeLock)
            {
                if (_worker != null)
                {
                    if (again) _again = rooms;
                    return;
                }
                _worker = new System.Threading.Thread(() => PrebuildAll(rooms))
                {
                    IsBackground = true,
                    Priority = System.Threading.ThreadPriority.BelowNormal,
                    Name = "holiday snow",
                };
                _worker.Start();
            }
        }

        // the rooms and their layers, as SceneSetup.SetUpRoom picks them (taken on the caller's thread: the room tables
        // change as arenas register)
        static List<(RoomMetadata Meta, int Mask)> PrebuildRooms()
        {
            var rooms = new List<(RoomMetadata, int)>();
            foreach (RoomMetadata meta in MphRecomp.Multiplayer.MatchArenas.All)
            {
                foreach (int mask in new[]
                {
                    SceneSetup.GetNodeLayer(GameMode.Battle, meta.NodeLayer, 4), SceneSetup.GetNodeLayer(GameMode.Battle, meta.NodeLayer, 2),
                    SceneSetup.GetNodeLayer(GameMode.Capture, meta.NodeLayer, 4), SceneSetup.GetNodeLayer(GameMode.Capture, meta.NodeLayer, 2),
                }.Distinct())
                {
                    rooms.Add((meta, mask));
                }
            }
            foreach (RoomMetadata meta in Metadata.RoomList)
            {
                if (!meta.Multiplayer && !meta.FirstHunt && !meta.Hybrid)
                {
                    rooms.Add((meta, SceneSetup.GetNodeLayer(GameMode.SinglePlayer, meta.NodeLayer, 1)));
                }
            }
            return rooms;
        }

        static void PrebuildAll(List<(RoomMetadata Meta, int Mask)> rooms)
        {
            while (true)
            {
                var sw = Stopwatch.StartNew();
                int built = 0, skipped = 0, failed = 0;
                try
                {
                    string indexPath = Path.Combine(CacheDir, "prebuilt.txt");
                    var index = new Dictionary<string, string>();
                    if (File.Exists(indexPath))
                    {
                        foreach (string line in File.ReadAllLines(indexPath))
                        {
                            int tab = line.IndexOf('\t');
                            if (tab > 0) index[line[..tab]] = line[(tab + 1)..];
                        }
                    }
                    var cores = new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount / 4) };
                    Model? snow = null;
                    foreach ((RoomMetadata meta, int mask) in rooms)
                    {
                        if (!HolidaySnow.Enabled)
                        {
                            break;
                        }
                        while (Playing)
                        {
                            System.Threading.Thread.Sleep(1000);
                        }
                        string key = $"{Version}|{meta.Name}|{mask}|{Stamp(meta)}";
                        if (index.TryGetValue(key, out string? file) && (file.Length == 0 || File.Exists(Path.Combine(CacheDir, file))))
                        {
                            skipped++;
                            continue;
                        }
                        try
                        {
                            if (snow == null && Metadata.GetRoomByName(SnowRoom).Item1 is RoomMetadata snowMeta)
                            {
                                snow = Read.ReadRoomModelUncached(snowMeta);
                            }
                            index[key] = PrebuildRoom(meta, mask, snow!, cores, ref built);
                            Directory.CreateDirectory(CacheDir);
                            File.WriteAllLines(indexPath, index.Select(p => p.Key + "\t" + p.Value));
                        }
                        catch (Exception ex)
                        {
                            failed++;
                            Console.WriteLine($"Holiday ground snow: {meta.Name} not built ahead ({ex.Message})");
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Holiday ground snow: building ahead stopped ({ex.Message})");
                }
                Console.WriteLine($"Holiday ground snow: built ahead, {built} rooms built, {skipped} already done, {failed} failed, {sw.ElapsedMilliseconds} ms");
                lock (_shapeLock)
                {
                    if (_again == null)
                    {
                        _worker = null;
                        return;
                    }
                    rooms = _again;
                    _again = null;
                }
            }
        }

        // a room's sky map in one layer, read fresh (any thread): the room as that layer draws it, its glass and the ceiling
        // the map filled in come back too
        internal static HolidaySnow.RoofMap SkyMap(RoomMetadata meta, int mask, out Model room,
            out List<(Vector3 A, Vector3 B, Vector3 C)> glass, out List<(Vector3 A, Vector3 B, Vector3 C)> ceiling)
        {
            return SkyMap(meta, mask, out room, out glass, out ceiling, out _, out _, out _);
        }

        // the same, with the drawn triangles, which way each is seen and the sky shapes' corners (HolidaySnow.DrawnTris):
        // what HolidaySnow.PrepareEarly hands the room's roofs
        internal static HolidaySnow.RoofMap SkyMap(RoomMetadata meta, int mask, out Model room,
            out List<(Vector3 A, Vector3 B, Vector3 C)> glass, out List<(Vector3 A, Vector3 B, Vector3 C)> ceiling,
            out List<(Vector3 A, Vector3 B, Vector3 C)> tris, out List<sbyte> facing, out List<Vector3> sky)
        {
            room = Read.ReadRoomModelUncached(meta);
            room.FilterNodes(mask);
            if (meta.Name == "UNIT2_C6") // Tetra Vista's off-surface decal (RoomEntity.Setup)
            {
                room.Nodes[46].Enabled = false;
            }
            var info = Collision.GetCollision(meta, mask).Info as MphCollisionInfo;
            glass = HolidaySnow.ReadGlass(meta.Name);
            ceiling = new List<(Vector3 A, Vector3 B, Vector3 C)>();
            facing = new List<sbyte>();
            sky = new List<Vector3>();
            tris = HolidaySnow.DrawnTris(room, glass, facing, sky);
            return HolidaySnow.BuildMap(meta.Id, tris, facing, HolidaySnow.DrawsSky(meta, sky, info, Vector3.Zero),
                HolidaySnow.CeilingSource(meta, info), Vector3.Zero, ceiling);
        }

        // one room in one layer: its cache file's name ("" when it gets no snow)
        static string PrebuildRoom(RoomMetadata meta, int mask, Model snow, ParallelOptions cores, ref int built)
        {
            HolidaySnow.RoofMap map = SkyMap(meta, mask, out Model room, out var glass, out var ceiling);
            if (Gather(room, meta, meta.Id, snow, glass, ceiling, map) is not Source src)
            {
                return "";
            }
            string path = CachePath(src);
            if (File.Exists(path))
            {
                return Path.GetFileName(path);
            }
            lock (_shapeLock)
            {
                _shaping = path;
            }
            try
            {
                var sw = Stopwatch.StartNew();
                List<RenderInstruction> list = Shape(src, out int triangles, cores);
                Save(path, src.RoomId, list, triangles);
                built++;
                Console.WriteLine($"Holiday ground snow: built ahead {meta.Name} (layers {mask:x}), {triangles} triangles in {sw.ElapsedMilliseconds} ms");
            }
            finally
            {
                lock (_shapeLock)
                {
                    _shaping = null;
                    System.Threading.Monitor.PulseAll(_shapeLock);
                }
            }
            return Path.GetFileName(path);
        }

        // the room's files as they are now (an arena imported again changes them)
        static string Stamp(RoomMetadata meta)
        {
            var sb = new StringBuilder();
            foreach (string? rel in new[] { meta.ModelPath, meta.CollisionPath, meta.TexturePath })
            {
                if (rel == null) continue;
                string root = meta.FirstHunt || meta.Hybrid ? Paths.FhFileSystem : Paths.FileSystem;
                var f = new FileInfo(Paths.Combine(root, rel));
                sb.Append(f.Exists ? $"{f.Length}:{f.LastWriteTimeUtc.Ticks}," : "-,");
            }
            if (EchoesArena.IsEchoesRoom(meta.Name) && EchoesArena.HdDir(meta.Name) is string dir)
            {
                var f = new FileInfo(Path.Combine(dir, "arena.bin"));
                sb.Append(f.Exists ? $"{f.Length}:{f.LastWriteTimeUtc.Ticks}" : "-");
            }
            return sb.ToString();
        }

        // ---- the snow's shape ----

        static readonly int[] Di = { 1, -1, 0, 0 }, Dj = { 0, 0, 1, -1 }; // +x, -x, +z, -z
        const float SkyRayLength = 30f;
        // up and out from a covered node: 8 ways round at 55 and at 30 degrees up (what the falling snow's slant reaches)
        static readonly Vector3[] SkyRays = Enumerable.Range(0, 16).Select(k =>
        {
            float az = (k % 8 + (k < 8 ? 0 : 0.5f)) * MathF.PI / 4, el = (k < 8 ? 55 : 30) * MathF.PI / 180;
            return new Vector3(MathF.Cos(el) * MathF.Cos(az), MathF.Sin(el), MathF.Cos(el) * MathF.Sin(az));
        }).ToArray();
        const int Linked = 0, WallSide = 1, DropSide = 2;

        // the sky rays only ask whether anything is in the way: walk just the cells under the ray and stop at the first hit
        // (ArenaRaycaster.Cast tests every cell of the ray's box for the nearest hit: most of Monitor Station's build, 2026-11-21)
        sealed class Blocker
        {
            const float Cell = 4;
            readonly Vector3[] _a, _e1, _e2;
            readonly Dictionary<(int, int), int[]> _grid;

            public Blocker(IReadOnlyList<(Vector3 A, Vector3 B, Vector3 C)> tris)
            {
                int n = tris.Count;
                _a = new Vector3[n]; _e1 = new Vector3[n]; _e2 = new Vector3[n];
                var lists = new Dictionary<(int, int), List<int>>();
                for (int i = 0; i < n; i++)
                {
                    (Vector3 a, Vector3 b, Vector3 c) = tris[i];
                    _a[i] = a; _e1[i] = b - a; _e2[i] = c - a;
                    Vector3 lo = Vector3.ComponentMin(a, Vector3.ComponentMin(b, c)), hi = Vector3.ComponentMax(a, Vector3.ComponentMax(b, c));
                    for (int x = Key(lo.X); x <= Key(hi.X); x++)
                    {
                        for (int z = Key(lo.Z); z <= Key(hi.Z); z++)
                        {
                            if (!lists.TryGetValue((x, z), out List<int>? list)) lists[(x, z)] = list = new List<int>();
                            list.Add(i);
                        }
                    }
                }
                _grid = lists.ToDictionary(kv => kv.Key, kv => kv.Value.ToArray());
            }

            static int Key(float v) => (int)MathF.Floor(v / Cell);

            public bool Blocked(Vector3 from, Vector3 to)
            {
                Vector3 d = to - from;
                int x = Key(from.X), z = Key(from.Z);
                int sx = MathF.Sign(d.X), sz = MathF.Sign(d.Z);
                // the ray's t at the next cell edge each way, and across one cell
                float tx = sx == 0 ? float.MaxValue : ((x + (sx > 0 ? 1 : 0)) * Cell - from.X) / d.X;
                float tz = sz == 0 ? float.MaxValue : ((z + (sz > 0 ? 1 : 0)) * Cell - from.Z) / d.Z;
                float dtx = sx == 0 ? float.MaxValue : Cell / MathF.Abs(d.X), dtz = sz == 0 ? float.MaxValue : Cell / MathF.Abs(d.Z);
                while (true)
                {
                    if (_grid.TryGetValue((x, z), out int[]? list))
                    {
                        foreach (int i in list)
                        {
                            if (Hits(from, d, i)) return true;
                        }
                    }
                    if (MathF.Min(tx, tz) > 1) return false;
                    if (tx < tz) { x += sx; tx += dtx; } else { z += sz; tz += dtz; }
                }
            }

            // Moller-Trumbore, both faces, within the ray (as ArenaRaycaster's)
            bool Hits(Vector3 o, Vector3 d, int i)
            {
                Vector3 e1 = _e1[i], e2 = _e2[i];
                Vector3 p = Vector3.Cross(d, e2);
                float det = Vector3.Dot(e1, p);
                if (MathF.Abs(det) < 1e-9f) return false;
                float inv = 1 / det;
                Vector3 s = o - _a[i];
                float u = Vector3.Dot(s, p) * inv;
                if (u < 0 || u > 1) return false;
                Vector3 q = Vector3.Cross(s, e1);
                float v = Vector3.Dot(d, q) * inv;
                if (v < 0 || u + v > 1) return false;
                float t = Vector3.Dot(e2, q) * inv;
                return t >= 0 && t <= 1;
            }
        }

        static List<RenderInstruction> Shape(Source src, out int triangles, ParallelOptions? cores = null)
        {
            var sw = Stopwatch.StartNew();
            var floors = src.Floors;
            var lo = new Vector3(float.MaxValue);
            var hi = new Vector3(float.MinValue);
            foreach ((Vector3 a, Vector3 b, Vector3 c) in floors)
            {
                lo = Vector3.ComponentMin(lo, Vector3.ComponentMin(a, Vector3.ComponentMin(b, c)));
                hi = Vector3.ComponentMax(hi, Vector3.ComponentMax(a, Vector3.ComponentMax(b, c)));
            }
            float cell = Cell;
            while ((hi.X - lo.X) / cell * ((hi.Z - lo.Z) / cell) > MaxColumns)
            {
                cell *= 1.25f;
            }
            float x0 = lo.X, z0 = lo.Z;
            int w = (int)MathF.Floor((hi.X - x0) / cell) + 1, h = (int)MathF.Floor((hi.Z - z0) / cell) + 1;

            // triangles by column: column (i, j) covers x0 + i * cell .. + cell, its sample in the middle
            List<int>?[] ByColumn(List<(Vector3 A, Vector3 B, Vector3 C)> tris, bool skipUpright)
            {
                var byCol = new List<int>?[w * h];
                for (int f = 0; f < tris.Count; f++)
                {
                    (Vector3 a, Vector3 b, Vector3 c) = tris[f];
                    if (skipUpright && MathF.Abs(Vector3.Cross(b - a, c - a).Normalized().Y) < 0.05f)
                    {
                        continue; // a wall: nothing above or below anything
                    }
                    int i0 = Math.Max(0, (int)MathF.Floor((MathF.Min(a.X, MathF.Min(b.X, c.X)) - x0) / cell));
                    int i1 = Math.Min(w - 1, (int)MathF.Floor((MathF.Max(a.X, MathF.Max(b.X, c.X)) - x0) / cell));
                    int j0 = Math.Max(0, (int)MathF.Floor((MathF.Min(a.Z, MathF.Min(b.Z, c.Z)) - z0) / cell));
                    int j1 = Math.Min(h - 1, (int)MathF.Floor((MathF.Max(a.Z, MathF.Max(b.Z, c.Z)) - z0) / cell));
                    for (int j = j0; j <= j1; j++)
                    {
                        for (int i = i0; i <= i1; i++)
                        {
                            (byCol[j * w + i] ??= new List<int>()).Add(f);
                        }
                    }
                }
                return byCol;
            }
            List<int>?[] floorCol = ByColumn(floors, false);
            List<int>?[] solidCol = ByColumn(src.Solids, true);

            List<int>? Column(List<int>?[] byCol, float x, float z)
            {
                int i = (int)MathF.Floor((x - x0) / cell), j = (int)MathF.Floor((z - z0) / cell);
                return i < 0 || j < 0 || i >= w || j >= h ? null : byCol[j * w + i];
            }

            // the floor near height y at (x, z), if there is one
            bool FloorNear(float x, float z, float y, float within)
            {
                if (Column(floorCol, x, z) is { } here)
                {
                    foreach (int f in here)
                    {
                        if (YAt(floors[f], x, z, out float fy) && MathF.Abs(fy - y) < within)
                        {
                            return true;
                        }
                    }
                }
                return false;
            }

            // straight up or down without a ray: the nearest drawn surface over or under (x, z) above y + gap, or below y
            float Above(float x, float z, float y, float gap)
            {
                float best = float.MaxValue;
                if (Column(solidCol, x, z) is { } here)
                {
                    foreach (int f in here)
                    {
                        if (YAt(src.Solids[f], x, z, out float sy) && sy > y + gap && sy < best) best = sy;
                    }
                }
                return best - y;
            }
            float Below(float x, float z, float y)
            {
                float best = float.MinValue;
                if (Column(solidCol, x, z) is { } here)
                {
                    foreach (int f in here)
                    {
                        if (YAt(src.Solids[f], x, z, out float sy) && sy < y && sy > best) best = sy;
                    }
                }
                return y - best;
            }

            // the nodes: each column's floor heights, lowest first (one per level), by column
            var nodeX = new List<float>();
            var nodeZ = new List<float>();
            var nodeY = new List<float>();
            var nodeUp = new List<float>(); // the floor's normal, how upright
            var colStart = new int[w * h + 1];
            var levels = new List<(float Y, float Up)>?[w * h];
            for (int j = 0; j < h; j++)
            {
                for (int i = 0; i < w; i++)
                {
                    if (floorCol[j * w + i] is not { } here)
                    {
                        continue;
                    }
                    float x = x0 + (i + 0.5f) * cell, z = z0 + (j + 0.5f) * cell;
                    var heights = new List<(float Y, float Up)>();
                    foreach (int f in here)
                    {
                        if (YAt(floors[f], x, z, out float y))
                        {
                            (Vector3 a, Vector3 b, Vector3 c) = floors[f];
                            heights.Add((y, MathF.Abs(Vector3.Cross(b - a, c - a).Normalized().Y)));
                        }
                    }
                    heights.Sort((p, q) => p.Y.CompareTo(q.Y));
                    for (int k = heights.Count - 2; k >= 0; k--)
                    {
                        if (heights[k + 1].Y - heights[k].Y < 0.12f)
                        {
                            heights.RemoveAt(k); // the same floor twice (a shared edge, a double face): the upper one
                        }
                    }
                    levels[j * w + i] = heights.Count > 0 ? heights : null;
                }
            }
            // a small crevasse (a crack, a groove a column or two wide between floors at about the same height) bridged:
            // snow fills it level instead of the sheet parting round it in two lips (owner 2026-11-20 point 1)
            int bridged = 0;
            var bridges = new List<(int Col, float Y)>();
            bool Near(int col, float y, float within)
            {
                if (levels[col] is { } l)
                {
                    foreach ((float ly, _) in l)
                    {
                        if (MathF.Abs(ly - y) < within) return true;
                    }
                }
                return false;
            }
            for (int j = 0; j < h; j++)
            {
                for (int i = 0; i < w; i++)
                {
                    if (levels[j * w + i] is not { } here)
                    {
                        continue;
                    }
                    foreach ((float y, float up) in here)
                    {
                        if (up < Flat)
                        {
                            continue;
                        }
                        for (int d = 0; d < 4; d += 2) // +x, +z
                        {
                            for (int g = 1; g <= CrevasseColumns; g++)
                            {
                                int ei = i + Di[d] * (g + 1), ej = j + Dj[d] * (g + 1);
                                if (ei >= w || ej >= h || levels[ej * w + ei] is not { } far)
                                {
                                    continue;
                                }
                                float y2 = float.NaN;
                                foreach ((float fy, float fu) in far)
                                {
                                    if (fu >= Flat && MathF.Abs(fy - y) < 0.25f) y2 = fy;
                                }
                                if (float.IsNaN(y2))
                                {
                                    continue;
                                }
                                bool crevasse = true;
                                for (int q = 1; q <= g && crevasse; q++)
                                {
                                    int col = (j + Dj[d] * q) * w + i + Di[d] * q;
                                    float t = Lerp(y, y2, q / (g + 1f));
                                    // nothing at that height there (else they'd join anyway), nothing above it (a wall, a
                                    // rock), and a floor down in it (a crack, not a pit or a gap between platforms)
                                    bool below = false;
                                    if (levels[col] is { } l)
                                    {
                                        foreach ((float ly, _) in l)
                                        {
                                            if (ly > t - MaxStep) crevasse = false;
                                            else if (ly > t - 3) below = true;
                                        }
                                    }
                                    crevasse &= below;
                                }
                                if (!crevasse)
                                {
                                    continue;
                                }
                                for (int q = 1; q <= g; q++)
                                {
                                    bridges.Add(((j + Dj[d] * q) * w + i + Di[d] * q, Lerp(y, y2, q / (g + 1f))));
                                }
                                break;
                            }
                        }
                    }
                }
            }
            foreach ((int col, float y) in bridges)
            {
                if (!Near(col, y, 0.12f))
                {
                    var l = levels[col]!;
                    l.Add((y, 1));
                    l.Sort((p, q) => p.Y.CompareTo(q.Y));
                    bridged++;
                }
            }
            for (int j = 0; j < h; j++)
            {
                for (int i = 0; i < w; i++)
                {
                    colStart[j * w + i] = nodeY.Count;
                    if (levels[j * w + i] is not { } here)
                    {
                        continue;
                    }
                    float x = x0 + (i + 0.5f) * cell, z = z0 + (j + 0.5f) * cell;
                    foreach ((float y, float up) in here)
                    {
                        nodeX.Add(x);
                        nodeZ.Add(z);
                        nodeY.Add(y);
                        nodeUp.Add(up);
                    }
                }
            }
            colStart[w * h] = nodeY.Count;
            int count = nodeY.Count;
            triangles = 0;
            if (count == 0)
            {
                return new List<RenderInstruction>();
            }

            // joined to the nearest-height node of each neighbouring column, both ways
            var best = new int[count * 4];
            for (int j = 0; j < h; j++)
            {
                for (int i = 0; i < w; i++)
                {
                    for (int n = colStart[j * w + i]; n < colStart[j * w + i + 1]; n++)
                    {
                        for (int d = 0; d < 4; d++)
                        {
                            best[n * 4 + d] = -1;
                            int ni = i + Di[d], nj = j + Dj[d];
                            if (ni < 0 || nj < 0 || ni >= w || nj >= h)
                            {
                                continue;
                            }
                            float closest = MaxStep;
                            for (int m = colStart[nj * w + ni]; m < colStart[nj * w + ni + 1]; m++)
                            {
                                float dy = MathF.Abs(nodeY[m] - nodeY[n]);
                                if (dy < closest)
                                {
                                    closest = dy;
                                    best[n * 4 + d] = m;
                                }
                            }
                        }
                    }
                }
            }
            var link = new int[count * 4];
            for (int n = 0; n < count; n++)
            {
                for (int d = 0; d < 4; d++)
                {
                    int m = best[n * 4 + d];
                    link[n * 4 + d] = m >= 0 && best[m * 4 + (d ^ 1)] == n ? m : -1;
                }
            }

            // what each node sees: how high the cover over it is (none: open sky), and on each side a wall within a cell,
            // or on an open side of the sheet how far the floor still goes
            // open: nothing over it, and its sky map cell sees the sky (the falling snow's own rule, HolidaySnow.SeesSky)
            var cover = new float[count];
            var open = new bool[count];
            Parallel.For(0, count, cores ?? AllCores, n =>
            {
                cover[n] = Above(nodeX[n], nodeZ[n], nodeY[n], 0.3f);
                open[n] = cover[n] > Open && src.Map.OpenAt(new Vector3(nodeX[n], nodeY[n], nodeZ[n]));
            });
            // no floor anywhere open to the sky: no snow, and none of the sky rays below (they cost Monitor Station's ~22 s
            // on the Odin)
            if (!open.Any(o => o))
            {
                Console.WriteLine($"Holiday ground snow: room {src.RoomId} has no floor open to the sky, no snow ({sw.ElapsedMilliseconds} ms)");
                return new List<RenderInstruction>();
            }
            long grid = sw.ElapsedMilliseconds;
            var ray = new ArenaRaycaster(src.Solids);
            var blocker = new Blocker(src.Solids);
            var wallAt = new float[count * 4]; // an upright wall that way within the cell: how far; else -1
            var side = new byte[count * 4];
            var reach = new float[count * 4];
            var exposure = new float[count]; // under cover: the share of the sky it sees past what's over it
            Parallel.For(0, count, cores ?? AllCores, n =>
            {
                var p = new Vector3(nodeX[n], nodeY[n], nodeZ[n]);
                if (open[n])
                {
                    exposure[n] = 1;
                }
                else
                {
                    // slanting snow drifts in under a ledge's edge, less the further under it (owner 2026-11-20 point 3)
                    int seen = 0;
                    Vector3 from = p + new Vector3(0, 0.3f, 0);
                    foreach (Vector3 dir in SkyRays)
                    {
                        // past the ray's end it must be open overhead too: under a high ceiling (Incubation Vault's glass,
                        // HolidaySnow.CeilingFill) a slanted ray runs out before it reaches the roof. That look-up first: it is
                        // cheap, and deep under a roof it rules the ray out without casting it
                        Vector3 end = from + dir * SkyRayLength;
                        bool sky = src.Map.CellAt(end) >= 0 ? src.Map.SeesSky(end) : Above(end.X, end.Z, end.Y, 0) > Open;
                        if (sky && !blocker.Blocked(from, end)) seen++;
                    }
                    exposure[n] = seen / (float)SkyRays.Length;
                }
                Vector3 mid = p + new Vector3(0, 0.25f, 0);
                for (int d = 0; d < 4; d++)
                {
                    var dir = new Vector3(Di[d], 0, Dj[d]);
                    var hit = ray.Cast(mid, mid + dir * cell);
                    wallAt[n * 4 + d] = hit is { } wh && MathF.Abs(wh.Normal.Y) < 0.5f ? wh.T * cell : -1;
                    if (link[n * 4 + d] >= 0)
                    {
                        continue;
                    }
                    if (hit is { } any)
                    {
                        side[n * 4 + d] = WallSide;
                        reach[n * 4 + d] = any.T * cell;
                        continue;
                    }
                    // a drop: how far the floor still goes that way (halved down to a few hundredths)
                    float inside = 0, outside = cell;
                    for (int it = 0; it < 4; it++)
                    {
                        float t = (inside + outside) / 2;
                        // the same floor, sloping at most 45 degrees (not one a step below it)
                        if (FloorNear(p.X + dir.X * t, p.Z + dir.Z * t, p.Y, 0.05f + t)) inside = t; else outside = t;
                    }
                    side[n * 4 + d] = DropSide;
                    // floor all the way: the next column's is there, just not joined (a slope's kink): meet it halfway
                    reach[n * 4 + d] = inside > 0.85f * cell ? cell / 2 : inside;
                }
            });
            long rays = sw.ElapsedMilliseconds - grid;
            // a wall between two joined nodes (a thin wall, a fence) parts the sheet there
            for (int n = 0; n < count; n++)
            {
                for (int d = 0; d < 4; d++)
                {
                    int m = link[n * 4 + d];
                    if (m >= 0 && (wallAt[n * 4 + d] >= 0 || wallAt[m * 4 + (d ^ 1)] >= 0))
                    {
                        link[n * 4 + d] = -1;
                        link[m * 4 + (d ^ 1)] = -1;
                        side[n * 4 + d] = WallSide;
                        side[m * 4 + (d ^ 1)] = WallSide;
                        reach[n * 4 + d] = wallAt[n * 4 + d] >= 0 ? wallAt[n * 4 + d] : cell / 2;
                        reach[m * 4 + (d ^ 1)] = wallAt[m * 4 + (d ^ 1)] >= 0 ? wallAt[m * 4 + (d ^ 1)] : cell / 2;
                    }
                }
            }

            // distances along the sheet, in cells, from the nodes that start true
            int[] Steps(Func<int, bool> start)
            {
                var steps = new int[count];
                var queue = new Queue<int>();
                for (int n = 0; n < count; n++)
                {
                    steps[n] = int.MaxValue;
                    if (start(n))
                    {
                        steps[n] = 0;
                        queue.Enqueue(n);
                    }
                }
                while (queue.Count > 0)
                {
                    int n = queue.Dequeue();
                    for (int d = 0; d < 4; d++)
                    {
                        int m = link[n * 4 + d];
                        if (m >= 0 && steps[m] > steps[n] + 1)
                        {
                            steps[m] = steps[n] + 1;
                            queue.Enqueue(m);
                        }
                    }
                }
                return steps;
            }
            int Walls(int n)
            {
                int k = 0;
                for (int d = 0; d < 4; d++)
                {
                    if (wallAt[n * 4 + d] >= 0 || side[n * 4 + d] == WallSide) k++;
                }
                return k;
            }
            int[] fromSky = Steps(n => open[n]);
            int[] fromWall = Steps(n => Walls(n) > 0);
            int[] fromDrop = Steps(n =>
            {
                for (int d = 0; d < 4; d++)
                {
                    if (side[n * 4 + d] == DropSide) return true;
                }
                return false;
            });

            // the depth: the sky (under cover, drifted in from the open edge, reaching further under higher cover),
            // drifted against walls and into corners, thinner on slopes, lumpy, blurred over the sheet
            var depth = new float[count];
            for (int n = 0; n < count; n++)
            {
                float sky = 1;
                if (!open[n])
                {
                    // what slants in past the cover's edge, and a little more blown in along the floor from the open
                    float under = Math.Clamp(0.3f * cover[n], 0.3f, 1.5f);
                    float blown = fromSky[n] == int.MaxValue ? 0 : 1 - SmoothStep(0, under, (fromSky[n] - 0.5f) * cell);
                    sky = MathF.Max(SmoothStep(0.04f, 0.5f, exposure[n]), blown);
                }
                float drift = 1 + Drift * 0.8f * (1 - SmoothStep(0, WallReach, fromWall[n] * cell));
                if (Walls(n) >= 2)
                {
                    drift += 0.2f * Drift;
                }
                float slope = SmoothStep(0.6f, 0.85f, nodeUp[n]);
                depth[n] = Depth * sky * drift * slope * (0.7f + 0.6f * Noise(nodeX[n] / 2.2f, nodeZ[n] / 2.2f));
            }
            Blur(depth, link, 3);
            var top = new float[count];
            for (int n = 0; n < count; n++)
            {
                // full depth out to a drop, eased off over its last half unit (the lip takes it over the edge)
                if (fromDrop[n] != int.MaxValue)
                {
                    depth[n] *= 0.8f + 0.2f * SmoothStep(0, 0.5f, (fromDrop[n] + 0.5f) * cell);
                }
                top[n] = nodeY[n] + Lift + depth[n];
            }
            // the top itself smoothed too (fills a tile's grooves, rounds a step), never under the floor
            Blur(top, link, 2);
            for (int n = 0; n < count; n++)
            {
                top[n] = MathF.Max(top[n], nodeY[n] + Lift);
            }

            // each node's corner: out to the wall or the floor's edge on an open side, lit by the sheet's slope
            var pos = new Vector3[count];
            var light = new float[count];
            var sun = Vector3.Normalize(new Vector3(0.45f, 1, 0.3f));
            for (int n = 0; n < count; n++)
            {
                var v = new Vector3(nodeX[n], top[n], nodeZ[n]);
                var at = new Vector3(nodeX[n], 0, nodeZ[n]);
                for (int d = 0; d < 4; d++)
                {
                    var dir = new Vector3(Di[d], 0, Dj[d]);
                    if (side[n * 4 + d] == WallSide)
                    {
                        v += dir * Math.Clamp(reach[n * 4 + d] - 0.03f, 0, 0.9f * cell);
                    }
                    else if (side[n * 4 + d] == DropSide)
                    {
                        v += dir * MathF.Max(0, reach[n * 4 + d] - 0.02f);
                        v.Y = nodeY[n] + Lift + (top[n] - nodeY[n] - Lift) * 0.85f;
                    }
                }
                // out to a corner at most (two sides open), never further: a long reach made teeth
                Vector2 off = new Vector2(v.X - at.X, v.Z - at.Z);
                if (off.Length > 0.75f * cell)
                {
                    off *= 0.75f * cell / off.Length;
                }
                v.X = at.X + off.X;
                v.Z = at.Z + off.Y;
                pos[n] = v;
                float T(int d) => link[n * 4 + d] is int m and >= 0 ? top[m] : top[n];
                float gx = (T(0) - T(1)) / (2 * cell), gz = (T(2) - T(3)) / (2 * cell);
                var normal = Vector3.Normalize(new Vector3(-gx, 1, -gz));
                light[n] = Light(normal);
            }
            // the sheet's outline rounded: each edge corner averaged with the edge corners beside it, so a slanted edge
            // runs straight instead of in grid steps (Elder Passage's pit rim came out a saw)
            var edge = new bool[count];
            for (int n = 0; n < count; n++)
            {
                for (int d = 0; d < 4; d++)
                {
                    edge[n] |= link[n * 4 + d] < 0;
                }
            }
            var next = new Vector3[count];
            for (int pass = 0; pass < 3; pass++)
            {
                for (int n = 0; n < count; n++)
                {
                    next[n] = pos[n];
                    if (!edge[n])
                    {
                        continue;
                    }
                    float sx = 2 * pos[n].X, sz = 2 * pos[n].Z, weight = 2;
                    for (int d = 0; d < 4; d++)
                    {
                        int m = link[n * 4 + d];
                        if (m >= 0 && edge[m])
                        {
                            sx += pos[m].X;
                            sz += pos[m].Z;
                            weight++;
                        }
                    }
                    next[n].X = sx / weight;
                    next[n].Z = sz / weight;
                }
                Array.Copy(next, pos, count);
            }

            // the top: two triangles a quad of the sheet
            var tops = new List<(Corner A, Corner B, Corner C)>(count * 2);
            bool Level(int a, int b, int c, int e)
            {
                float lo = MathF.Min(MathF.Min(nodeY[a], nodeY[b]), MathF.Min(nodeY[c], nodeY[e]));
                float hi = MathF.Max(MathF.Max(nodeY[a], nodeY[b]), MathF.Max(nodeY[c], nodeY[e]));
                return hi - lo < 0.35f;
            }
            for (int n = 0; n < count; n++)
            {
                int r = link[n * 4 + 0], d = link[n * 4 + 2];
                int rd = r >= 0 ? link[r * 4 + 2] : -1;
                if (rd < 0 && d >= 0)
                {
                    rd = link[d * 4 + 0];
                }
                if (r >= 0 && d >= 0 && rd >= 0)
                {
                    Triangle(n, d, rd);
                    Triangle(n, rd, r);
                }
                else if (r >= 0 && d >= 0)
                {
                    Triangle(n, d, r);
                }
                else if (r >= 0 && rd >= 0)
                {
                    Triangle(n, rd, r);
                    // the quad's fourth side cut (a lip's or a bump's edge read as a wall) though its corner is there and
                    // level with the rest: closed, not a triangle hole in the sheet (owner 2026-11-20 point 2)
                    if (link[rd * 4 + 1] is int d2 and >= 0 && Level(n, r, rd, d2))
                    {
                        Triangle(n, d2, rd);
                    }
                }
                else if (d >= 0 && rd >= 0)
                {
                    Triangle(n, d, rd);
                    if (link[rd * 4 + 3] is int r2 and >= 0 && Level(n, d, rd, r2))
                    {
                        Triangle(n, rd, r2);
                    }
                }
                // the quad's other corner missing: its three others from the far corner
                int left = link[n * 4 + 1], above = link[n * 4 + 3];
                if (left >= 0 && above >= 0 && link[left * 4 + 3] < 0 && link[above * 4 + 1] < 0)
                {
                    Triangle(left, n, above);
                }
            }

            // the sheet's edges closed: its outline (the edges only one top triangle has), and round each outline corner a
            // profile down from the top: a lip over a drop, a rounding down to the floor where it goes on, a skirt at a wall
            var index = new Dictionary<(int, int, int), int>();
            var verts = new List<Corner>();
            int Id(Corner c)
            {
                var key = ((int)MathF.Round(c.P.X * 1000), (int)MathF.Round(c.P.Y * 1000), (int)MathF.Round(c.P.Z * 1000));
                if (!index.TryGetValue(key, out int i))
                {
                    i = verts.Count;
                    index[key] = i;
                    verts.Add(c);
                }
                return i;
            }
            var edges = new Dictionary<(int, int), (int Count, int A, int B, int Third)>();
            void Edge(int a, int b, int third)
            {
                var key = (Math.Min(a, b), Math.Max(a, b));
                edges[key] = edges.TryGetValue(key, out var e) ? (e.Count + 1, e.A, e.B, e.Third) : (1, a, b, third);
            }
            foreach ((Corner a, Corner b, Corner c) in tops)
            {
                int ia = Id(a), ib = Id(b), ic = Id(c);
                if (ia == ib || ib == ic || ia == ic)
                {
                    continue;
                }
                Edge(ia, ib, ic);
                Edge(ib, ic, ia);
                Edge(ic, ia, ib);
            }
            var outward = new Dictionary<int, Vector3>();
            foreach (var e in edges.Values)
            {
                if (e.Count != 1)
                {
                    continue;
                }
                Vector3 pa = verts[e.A].P, pb = verts[e.B].P, pc = verts[e.Third].P;
                var perp = new Vector3(pb.Z - pa.Z, 0, pa.X - pb.X);
                if (perp.LengthSquared < 1e-10f)
                {
                    continue;
                }
                perp.Normalize();
                if (Vector3.Dot(perp, pc - pa) > 0)
                {
                    perp = -perp;
                }
                outward[e.A] = outward.GetValueOrDefault(e.A) + perp;
                outward[e.B] = outward.GetValueOrDefault(e.B) + perp;
            }
            var rings = new Dictionary<int, Corner[]>();
            int drops = 0, rounds = 0, skirts = 0;
            foreach ((int v, Vector3 sum) in outward)
            {
                if (sum.LengthSquared < 1e-8f)
                {
                    continue;
                }
                rings[v] = Ring(verts[v], sum.Normalized());
            }
            var lips = new List<(Corner A, Corner B, Corner C)>();
            foreach (var e in edges.Values)
            {
                if (e.Count != 1 || !rings.TryGetValue(e.A, out Corner[]? ra) || !rings.TryGetValue(e.B, out Corner[]? rb))
                {
                    continue;
                }
                Vector3 pa = verts[e.A].P, pb = verts[e.B].P, pc = verts[e.Third].P;
                var perp = new Vector3(pb.Z - pa.Z, 0, pa.X - pb.X).Normalized();
                if (Vector3.Dot(perp, pc - pa) > 0)
                {
                    perp = -perp;
                }
                // turned to face out (and up): the first quad's normal decides for the strip
                Vector3 n0 = Vector3.Cross(rb[0].P - ra[0].P, ra[1].P - ra[0].P) + Vector3.Cross(rb[1].P - ra[1].P, ra[2].P - ra[1].P);
                bool flip = Vector3.Dot(n0, perp + new Vector3(0, 0.3f, 0)) < 0;
                for (int k = 0; k + 1 < ra.Length; k++)
                {
                    Lip(ra[k], rb[k], rb[k + 1], flip);
                    Lip(ra[k], rb[k + 1], ra[k + 1], flip);
                }
            }

            // the display list: room space over the room's scale, as the room's own (4.12 fits: the floors came from it)
            var list = new List<RenderInstruction>((tops.Count + lips.Count) * 7)
            {
                new RenderInstruction(InstructionCode.BEGIN_VTXS, 0), // triangles
                new RenderInstruction(InstructionCode.NORMAL, Ten(0) | Ten(1) << 10 | Ten(0) << 20)
            };
            uint color = uint.MaxValue;
            foreach ((Corner a, Corner b, Corner c) in tops)
            {
                Emit(a, b, c);
            }
            foreach ((Corner a, Corner b, Corner c) in lips)
            {
                Emit(a, b, c);
            }
            list.Add(new RenderInstruction(InstructionCode.END_VTXS));
            triangles = tops.Count + lips.Count;
            Console.WriteLine($"Holiday ground snow: room {src.RoomId}, {w}x{h} columns of {cell:0.##}, {count} points, "
                + $"{tops.Count} top + {lips.Count} edge triangles ({drops} lip, {rounds} rounded, {skirts} wall corners), "
                + $"{src.Solids.Count} drawn triangles, shaped in {sw.ElapsedMilliseconds} ms (grid {grid}, rays {rays})");
            return triangles > 0 ? list : new List<RenderInstruction>();

            float Light(Vector3 normal) => src.Shade * MathF.Min(1.1f, 0.35f + 0.7f * MathF.Max(0, Vector3.Dot(normal, sun)));

            // the snow's part of triangle abc: cut where its depth runs out (between corners, where the depth crosses
            // Thin), so the snow ends along a smooth line, not along the grid (owner 2026-11-20: "harshly stops like it
            // was cut with a blade"); that line is then rounded down to the floor
            void Triangle(int a, int b, int c)
            {
                int[] k = { a, b, c };
                int inCount = 0;
                foreach (int q in k)
                {
                    if (depth[q] >= Thin) inCount++;
                }
                if (inCount == 0)
                {
                    return; // no snow here
                }
                if (inCount == 3)
                {
                    Up(At(a), At(b), At(c));
                    return;
                }
                // turned so the odd corner out is first
                while ((depth[k[0]] >= Thin) != (inCount == 1))
                {
                    k = new[] { k[1], k[2], k[0] };
                }
                Corner o = At(k[0]), ab = Cut(k[0], k[1]), ac = Cut(k[0], k[2]);
                if (inCount == 1)
                {
                    Up(o, ab, ac);
                }
                else
                {
                    Corner p1 = At(k[1]), p2 = At(k[2]);
                    Up(ab, p1, p2);
                    Up(ab, p2, ac);
                }
            }

            void Up(Corner a, Corner b, Corner c)
            {
                if (Vector3.Cross(b.P - a.P, c.P - a.P).Y < 0)
                {
                    (b, c) = (c, b); // face up (a corner pulled out to an edge can turn it)
                }
                tops.Add((a, b, c));
            }

            Corner At(int n) => new Corner(pos[n], light[n], nodeY[n], pos[n].X / Tile, pos[n].Z / Tile);

            // the point on edge mn where the depth is Thin, just on the floor there (the same point from either end)
            Corner Cut(int m, int n)
            {
                if (m > n)
                {
                    (m, n) = (n, m);
                }
                float t = Math.Clamp((Thin - depth[m]) / (depth[n] - depth[m]), 0, 1);
                Vector3 p = Vector3.Lerp(pos[m], pos[n], t);
                float floor = Lerp(nodeY[m], nodeY[n], t);
                p.Y = floor + Lift + Thin;
                return new Corner(p, Lerp(light[m], light[n], t), floor, p.X / Tile, p.Z / Tile);
            }

            // the profile down from outline corner c, going out along o
            Corner[] Ring(Corner c, Vector3 o)
            {
                Vector3 p = c.P;
                float floor = c.Floor, dep = MathF.Max(0, p.Y - floor - Lift);
                var prof = new Vector2[6]; // (out, up) from the top corner
                var foot = new Vector3(p.X, floor + 0.15f, p.Z);
                if (ray.Cast(foot - o * 0.05f, foot + o * 0.3f) != null)
                {
                    // against a wall: straight down into the floor
                    skirts++;
                    for (int k = 0; k < 6; k++) prof[k] = new Vector2(0, -(p.Y - floor + 0.03f) * k / 5);
                }
                else if (FloorNear(p.X + o.X * 0.15f, p.Z + o.Z * 0.15f, floor, 0.25f))
                {
                    // the floor goes on: a quarter round down onto it
                    rounds++;
                    float r = Math.Clamp(dep, 0.02f, 0.25f), down = p.Y - floor + 0.03f;
                    for (int k = 0; k < 6; k++)
                    {
                        float a = k / 5f * MathF.PI / 2;
                        prof[k] = new Vector2(r * MathF.Sin(a), -down * (1 - MathF.Cos(a)));
                    }
                }
                else
                {
                    // a drop: bulging out over the edge, curled under and hanging, closed back onto the platform's side
                    // (Shorelines: out 0.09..0.27, down 0.6..1.2 below the top), no further down than the platform is thick
                    drops++;
                    float bulge = Math.Clamp(0.35f * dep + 0.08f, 0.08f, 0.27f);
                    float hang = Math.Clamp(2.2f * dep + 0.25f, 0.3f, 1.1f) * (0.5f + Noise(p.X / 1.3f, p.Z / 1.3f));
                    float thick = MathF.Min(3, Below(p.X - o.X * 0.1f, p.Z - o.Z * 0.1f, floor - 0.02f));
                    float down = MathF.Max(p.Y - floor + 0.03f, MathF.Min(hang, p.Y - floor + 0.8f * thick));
                    prof[0] = Vector2.Zero;
                    prof[1] = new Vector2(0.75f * bulge, -0.12f * down);
                    prof[2] = new Vector2(bulge, -0.4f * down);
                    prof[3] = new Vector2(0.7f * bulge, -0.75f * down);
                    prof[4] = new Vector2(0.15f * bulge, -down);
                    prof[5] = new Vector2(-0.04f, -0.97f * down);
                }
                bool alongX = MathF.Abs(o.X) > MathF.Abs(o.Z); // the side's texture: across it and down it
                var ring = new Corner[6];
                for (int k = 0; k < 6; k++)
                {
                    Vector3 q = p + o * prof[k].X + new Vector3(0, prof[k].Y, 0);
                    Vector2 tan = prof[Math.Min(5, k + 1)] - prof[Math.Max(0, k - 1)];
                    float lit = c.Light;
                    if (k > 0 && tan.LengthSquared > 1e-10f)
                    {
                        Vector3 normal = o * -tan.Y + new Vector3(0, tan.X, 0);
                        lit = Light(normal.Normalized());
                    }
                    ring[k] = new Corner(q, lit, floor, (alongX ? q.Z : q.X) / Tile, -q.Y / Tile);
                }
                return ring;
            }

            void Lip(Corner a, Corner b, Corner c, bool flip)
            {
                if (Vector3.Cross(b.P - a.P, c.P - a.P).LengthSquared < 1e-12f)
                {
                    return;
                }
                lips.Add(flip ? (a, c, b) : (a, b, c));
            }

            void Emit(Corner a, Corner b, Corner c)
            {
                // texture coordinates in texels x16 (signed 16 bits): the repeats nearest the triangle taken off
                float s0 = MathF.Floor(MathF.Min(a.U, MathF.Min(b.U, c.U)));
                float t0 = MathF.Floor(MathF.Min(a.V, MathF.Min(b.V, c.V)));
                foreach (Corner k in new[] { a, b, c })
                {
                    uint Channel(float tint) => (uint)Math.Clamp((int)MathF.Round(k.Light * tint * 31), 0, 31);
                    uint rgb = Channel(src.Tint.X) | Channel(src.Tint.Y) << 5 | Channel(src.Tint.Z) << 10;
                    if (rgb != color)
                    {
                        list.Add(new RenderInstruction(InstructionCode.COLOR, rgb));
                        color = rgb;
                    }
                    list.Add(new RenderInstruction(InstructionCode.TEXCOORD,
                        Tex((k.U - s0) * src.Tex.Width) | Tex((k.V - t0) * src.Tex.Height) << 16));
                    Vector3 m = k.P / src.Scale;
                    list.Add(new RenderInstruction(InstructionCode.VTX_16, Fx(m.X) | Fx(m.Y) << 16, Fx(m.Z)));
                }
            }
        }

        // a corner of the snow: where, its light, the floor's height under it, its texture place in repeats
        readonly record struct Corner(Vector3 P, float Light, float Floor, float U, float V);

        // the snow model round its display list (on the game thread)
        static Model ToModel(Source src, List<RenderInstruction> list)
        {
            Model room = src.Room, snow = src.Snow;
            Header header = Patch(room.Header, (28, (ushort)0), (72, (ushort)1), (74, (ushort)1), (96, (ushort)1),
                (40, (ushort)snow.Recolors[0].Textures.Count), (48, (ushort)snow.Recolors[0].Palettes.Count),
                (98, (ushort)snow.TextureMatrices.Count));
            var lists = new List<IReadOnlyList<RenderInstruction>> { list };
            var dlists = new List<DisplayList> { room.DisplayLists[room.Meshes[0].DlistId] };
            var rawMeshes = new List<RawMesh> { MeshRaw(0, 0) };
            var rawNodes = new List<RawNode> { NodeRaw("holiday_snowcover") };
            var rawMaterials = new List<RawMaterial> { MaterialRaw(src.Mat) };
            return new Model("holiday_snowcover", false, header, rawNodes, rawMeshes, rawMaterials, dlists, lists,
                new AnimationResults(), snow.TextureMatrices, snow.Recolors, Array.Empty<int>(), Array.Empty<Vector3Fx>(),
                Array.Empty<Vector3Fx>(), Array.Empty<int>(), Array.Empty<Fixed>());
        }

        // each value averaged with its sheet neighbours' (twice its own weight), passes times
        static void Blur(float[] values, int[] link, int passes)
        {
            var next = new float[values.Length];
            for (int pass = 0; pass < passes; pass++)
            {
                for (int n = 0; n < values.Length; n++)
                {
                    float sum = 2 * values[n], weight = 2;
                    for (int d = 0; d < 4; d++)
                    {
                        int m = link[n * 4 + d];
                        if (m >= 0)
                        {
                            sum += values[m];
                            weight++;
                        }
                    }
                    next[n] = sum / weight;
                }
                Array.Copy(next, values, values.Length);
            }
        }

        // the floor's height at (x, z) if (x, z) is over it
        static bool YAt((Vector3 A, Vector3 B, Vector3 C) f, float x, float z, out float y)
        {
            (Vector3 a, Vector3 b, Vector3 c) = f;
            y = 0;
            float det = (b.Z - c.Z) * (a.X - c.X) + (c.X - b.X) * (a.Z - c.Z);
            if (MathF.Abs(det) < 1e-9f)
            {
                return false;
            }
            float u = ((b.Z - c.Z) * (x - c.X) + (c.X - b.X) * (z - c.Z)) / det;
            float v = ((c.Z - a.Z) * (x - c.X) + (a.X - c.X) * (z - c.Z)) / det;
            if (u < -1e-4f || v < -1e-4f || u + v > 1 + 1e-4f)
            {
                return false;
            }
            y = u * a.Y + v * b.Y + (1 - u - v) * c.Y;
            return true;
        }

        static float SmoothStep(float e0, float e1, float x)
        {
            float t = Math.Clamp((x - e0) / (e1 - e0), 0, 1);
            return t * t * (3 - 2 * t);
        }

        static float Lerp(float a, float b, float t) => a + (b - a) * t;

        // smooth value noise in 0..1 (the snow's lumps; the same everywhere for a given spot)
        static float Noise(float x, float z)
        {
            int x0 = (int)MathF.Floor(x), z0 = (int)MathF.Floor(z);
            float fx = x - x0, fz = z - z0;
            fx = fx * fx * (3 - 2 * fx);
            fz = fz * fz * (3 - 2 * fz);
            return Lerp(Lerp(Hash(x0, z0), Hash(x0 + 1, z0), fx), Lerp(Hash(x0, z0 + 1), Hash(x0 + 1, z0 + 1), fx), fz);
        }

        static float Hash(int x, int z)
        {
            uint h = (uint)x * 0x8DA6B343U ^ (uint)z * 0xD8163841U;
            h ^= h >> 13;
            h *= 0x5BD1E995U;
            h ^= h >> 15;
            return (h & 0xFFFF) / 65535f;
        }

        static bool Has(string name, string part) => name.Contains(part, StringComparison.OrdinalIgnoreCase);

        static uint Tex(float texels) => (uint)(ushort)(short)Math.Clamp((int)MathF.Round(texels * 16), short.MinValue, short.MaxValue);

        static uint Ten(float v) => (uint)Math.Clamp((int)MathF.Round(v * 512), -512, 511) & 0x3FF;

        static uint Fx(float v) => (uint)(ushort)(short)Math.Clamp((int)MathF.Round(v * 4096), short.MinValue, short.MaxValue);

        // one root node, no transform of its own, the one mesh (RawNode's layout)
        static RawNode NodeRaw(string name)
        {
            var bytes = new byte[Marshal.SizeOf<RawNode>()];
            Encoding.ASCII.GetBytes(name, 0, Math.Min(name.Length, 63), bytes, 0);
            BitConverter.TryWriteBytes(bytes.AsSpan(64), (short)-1); // parent
            BitConverter.TryWriteBytes(bytes.AsSpan(66), (short)-1); // child
            BitConverter.TryWriteBytes(bytes.AsSpan(68), (short)-1); // next
            BitConverter.TryWriteBytes(bytes.AsSpan(72), 1u); // enabled
            BitConverter.TryWriteBytes(bytes.AsSpan(76), (ushort)1); // mesh count
            BitConverter.TryWriteBytes(bytes.AsSpan(78), (ushort)0); // mesh id
            for (int i = 0; i < 3; i++)
            {
                BitConverter.TryWriteBytes(bytes.AsSpan(80 + 4 * i), 4096); // scale 1
            }
            BitConverter.TryWriteBytes(bytes.AsSpan(112), int.MaxValue); // bounding radius
            return Read.ReadStruct<RawNode>(bytes);
        }

        // the vanilla snow material as it is (RawMaterial's layout, as FittedIce.MaterialRaw writes it)
        static RawMaterial MaterialRaw(Material m)
        {
            using var ms = new MemoryStream();
            using var w = new BinaryWriter(ms);
            var nameBytes = new byte[64];
            Encoding.ASCII.GetBytes(m.Name, 0, Math.Min(m.Name.Length, 63), nameBytes, 0);
            w.Write(nameBytes);
            w.Write(m.InitLighting);
            // both faces: a lip of snow hanging over an edge, seen from below, was see-through but for its rim (owner 2026-11-20)
            w.Write((byte)CullingMode.Neither);
            w.Write(m.Alpha);
            w.Write(m.Wireframe);
            w.Write((short)m.PaletteId); w.Write((short)m.TextureId);
            w.Write((byte)m.XRepeat); w.Write((byte)m.YRepeat);
            foreach (ColorRgb c in new[] { m.Diffuse, m.Ambient, m.Specular })
            {
                w.Write(c.Red); w.Write(c.Green); w.Write(c.Blue);
            }
            w.Write((byte)0);
            w.Write((uint)m.PolygonMode);
            w.Write((byte)m.RenderMode); w.Write((byte)m.AnimationFlags); w.Write((ushort)0);
            w.Write((uint)m.TexgenMode);
            w.Write((ushort)0); w.Write((ushort)0);
            w.Write((uint)m.MatrixId);
            w.Write((int)MathF.Round(m.ScaleS * 4096)); w.Write((int)MathF.Round(m.ScaleT * 4096));
            w.Write((ushort)MathF.Round(m.RotateZ / (2f * MathF.PI) * 65536f)); w.Write((ushort)0);
            w.Write((int)MathF.Round(m.TranslateS * 4096)); w.Write((int)MathF.Round(m.TranslateT * 4096));
            w.Write((ushort)0); w.Write((ushort)0);
            w.Write((byte)0); w.Write((byte)0); w.Write((ushort)0);
            return Read.ReadStruct<RawMaterial>(ms.ToArray());
        }

        static RawMesh MeshRaw(int material, int dlist)
        {
            var b = new byte[4];
            BitConverter.TryWriteBytes(b.AsSpan(0), (ushort)material);
            BitConverter.TryWriteBytes(b.AsSpan(2), (ushort)dlist);
            return Read.ReadStruct<RawMesh>(b);
        }

        // a copy of a raw struct with ushort fields replaced at byte offsets (RawFormats' sequential layout)
        static T Patch<T>(T value, params (int Offset, ushort Value)[] fields) where T : struct
        {
            int size = Marshal.SizeOf<T>();
            var bytes = new byte[size];
            IntPtr p = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(value, p, false);
                Marshal.Copy(p, bytes, 0, size);
            }
            finally
            {
                Marshal.FreeHGlobal(p);
            }
            foreach ((int offset, ushort v) in fields)
            {
                BitConverter.TryWriteBytes(bytes.AsSpan(offset), v);
            }
            return Read.ReadStruct<T>(bytes);
        }
    }
}
