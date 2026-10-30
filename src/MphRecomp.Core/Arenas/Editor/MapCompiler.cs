using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MphRead;
using MphRead.Editor;
using MphRead.Formats.Collision;
using MphRead.Utility;
using OpenTK.Mathematics;

namespace MphRecomp.Arenas.Editor
{
    // MAP MAKER E0: the block world -> a host room the match code plays like any arena (the Echoes arenas' road:
    // ArenaModelWriter model, packed collision, a spawn file, Metadata.AddHostRoom). One fixed test room, rebuilt on each
    // PLAY; new file names every time so no reader's by-path cache hands back the last build.
    public static class MapCompiler
    {
        public const int RoomId = 1201;
        public const string RoomName = "MAP_MAKER_TEST";
        public const string InGameName = "MAP MAKER TEST";

        static int _build;

        // Budgets (docs/MAP_EDITOR.md section 5b). One collision polygon per rendered triangle. The biggest vanilla multiplayer
        // room (MP2 HARVESTER) has 2922 collision polygons, but that is the DS's limit, not ours: the imported Echoes arena M01
        // already plays with 14,377 triangles. So this is only a heads-up mark, never a cap (owner 10-25); where the Odin
        // starts to struggle was measured 10-25: the Odin runs 60k+ smooth, a GTX 1650 holds 60 fps at 68k, so 30,000 is a
        // generous mark for weaker devices. The model's 4.12 fixed-point range needs no limit (the writer scales the model).
        public const int TriangleSoftLimit = 30000;
        // the collision format's face (and point, plane) indices are 16 bits; the point-index list and the grid go 32-bit
        // ("wc32") when a map needs it, so this is the format's real limit (the packer also refuses 65,535 distinct points)
        public const int TriangleHardLimit = 65535;
        public static string? LastBudgetWarning { get; private set; }

        // returns the room name for the match's arena= argument
        public static string Compile(BlockWorld world, string folder, Vector3 spawn, float yaw, TextureLibrary? textures = null, bool perfRenderOnly = false)
        {
            List<BlockMesher.Tri> tris = BlockMesher.Build(world, textures, merge: true);
            if (tris.Count == 0) throw new InvalidOperationException("The map has no blocks.");
            Directory.CreateDirectory(folder);
            foreach (string old in Directory.GetFiles(folder, "mm*_*.bin"))
            {
                try { File.Delete(old); } catch (IOException) { }
            }
            string archive = $"mm{++_build}";

            var corners = new List<ArenaModelWriter.Corner>(tris.Count * 3);
            var editors = new List<CollisionDataEditor>(tris.Count);
            Vector3 min = new Vector3(float.MaxValue), max = new Vector3(float.MinValue);
            // perfRenderOnly (the Odin perf test): a map past the collision format's cap keeps every triangle to draw but
            // collides with only every k-th small one (big floor triangles always), so the floor stays solid
            static bool Big(BlockMesher.Tri t) =>
                Vector3.Distance(t.A, t.B) > 2 || Vector3.Distance(t.B, t.C) > 2 || Vector3.Distance(t.A, t.C) > 2;
            int stride = 1;
            if (perfRenderOnly && tris.Count > TriangleHardLimit)
            {
                int bigCount = tris.Count(Big);
                stride = Math.Max(1, (tris.Count - bigCount + 14999 - bigCount) / Math.Max(1, 15000 - bigCount));
            }
            int triIndex = 0;
            foreach (BlockMesher.Tri t in tris)
            {
                int m = t.Tex - 1;   // library id -> material index; -1 = flat colour
                corners.Add(new ArenaModelWriter.Corner(t.A, t.Color, m, t.UvA));
                corners.Add(new ArenaModelWriter.Corner(t.B, t.Color, m, t.UvB));
                corners.Add(new ArenaModelWriter.Corner(t.C, t.Color, m, t.UvC));
                Vector3 n = t.Normal;
                if (triIndex++ % stride != 0 && !Big(t)) goto drawnOnly;
                var ed = new CollisionDataEditor { Plane = new Vector4(n, Vector3.Dot(n, t.A)), LayerMask = (ushort)(4 | PrimaryAxis(n)) };
                ed.Points.Add(t.A); ed.Points.Add(t.B); ed.Points.Add(t.C);
                editors.Add(ed);
            drawnOnly:
                min = Vector3.ComponentMin(min, Vector3.ComponentMin(t.A, Vector3.ComponentMin(t.B, t.C)));
                max = Vector3.ComponentMax(max, Vector3.ComponentMax(t.A, Vector3.ComponentMax(t.B, t.C)));
            }
            if (tris.Count > TriangleHardLimit && !perfRenderOnly)
                throw new InvalidOperationException($"{tris.Count} triangles: the collision format holds at most {TriangleHardLimit}. Take some blocks away");
            List<ArenaModelWriter.TexMaterial>? materials = textures?.Entries.Select(e => new ArenaModelWriter.TexMaterial
            {
                Name = $"mmtex{e.Id}", Texture = e.Texture, Data = e.Data, Palette = e.Palette, ScrollS = e.ScrollS, ScrollT = e.ScrollT
            }).ToList();
            (byte[] model, byte[] anim) = ArenaModelWriter.Write(RoomName, corners, materials);
            LastBudgetWarning = tris.Count > TriangleSoftLimit
                ? $"{tris.Count} triangles: a very big map; weak devices (old phones, integrated graphics) may stutter. Not a limit, just a heads-up" : null;
            Console.WriteLine($"Map maker: compiled {tris.Count} triangles ({editors.Count} collision polygons), {model.Length / 1024} KB model" + (LastBudgetWarning != null ? $" (warning: {LastBudgetWarning})" : ""));
            File.WriteAllBytes(Path.Combine(folder, $"{archive}_Model.bin"), model);
            File.WriteAllBytes(Path.Combine(folder, $"{archive}_Anim.bin"), anim);
            File.WriteAllBytes(Path.Combine(folder, $"{archive}_Collision.bin"), RepackCollision.PackMphCollision(editors));

            // two spawns side by side at the cursor (a match wants at least two), facing the way the camera looks
            var facing = new Vector3(MathF.Sin(yaw), 0, -MathF.Cos(yaw));
            var side = new Vector3(-facing.Z, 0, facing.X);
            var ents = new List<EntityEditorBase>();
            for (int i = 0; i < 2; i++)
            {
                ents.Add(new PlayerSpawnEntityEditor { Id = (short)(i + 1), LayerMask = 0xFFFF, NodeName = ArenaModelWriter.RoomNode,
                    Position = spawn + side * (i * 1.5f), Up = Vector3.UnitY, Facing = facing, Availability = 0, Active = true, TeamIndex = -1 });
            }
            File.WriteAllBytes(Path.Combine(folder, $"{archive}_Ent.bin"), Repack.PackEntities(ents));

            Metadata.AddHostRoom(Room(archive, folder, min, max));
            Read.ClearCache();   // the room model is cached by room name
            return RoomName;
        }

        // E3 perf test: the starter floor with a field of small pillars grown until the flat-colour (unmerged) triangle count
        // reaches the target, for measuring where a device starts to struggle. Returns the world and the real count.
        public static (BlockWorld World, int Tris) PerfWorld(int target)
        {
            static BlockWorld Field(int pillars)
            {
                BlockWorld w = BlockWorld.Starter();
                int cols = Math.Max(1, (int)Math.Ceiling(Math.Sqrt(pillars)));
                int spacing = Math.Max(2, 240 / cols);   // steps (0.25 units); the floor is 256 steps wide
                int size = Math.Max(1, Math.Min(4, spacing - 1));
                int placed = 0;
                for (int a = 0; a < cols && placed < pillars; a++)
                {
                    for (int b = 0; b < cols && placed < pillars; b++, placed++)
                        w.SetSolid(new Vector3i(130 + a * spacing, 128, 130 + b * spacing), size, true, merge: false);
                }
                w.MergeAll();
                return w;
            }
            // the count is not linear in the pillars (they split the floor cells), so bisect on it
            int lo = 0, hi = 16000;
            BlockWorld best = Field(0);
            int bestCount = BlockMesher.Build(best, null).Count;
            for (int i = 0; i < 12 && Math.Abs(bestCount - target) > target / 100; i++)
            {
                int mid = (lo + hi) / 2;
                BlockWorld w = Field(mid);
                int count = BlockMesher.Build(w, null).Count;
                if (Math.Abs(count - target) < Math.Abs(bestCount - target)) { best = w; bestCount = count; }
                if (count < target) lo = mid; else hi = mid;
            }
            return (best, bestCount);
        }

        static RoomMetadata Room(string archive, string folder, Vector3 min, Vector3 max)
        {
            static int Fx(float v) => (int)MathF.Round(v * 4096);
            float far = MathF.Max(350, (max - min).Length + 50);
            return new RoomMetadata(
                id: RoomId,
                name: RoomName,
                inGameName: InGameName,
                archive: archive,
                modelPath: $"{archive}_Model.bin",
                animationPath: $"{archive}_Anim.bin",
                collisionPath: $"{archive}_Collision.bin",
                texturePath: null,
                entityPath: $"{archive}_Ent.bin",
                nodePath: null,
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
                killHeight: Fx(min.Y - 5),
                size: RoomSize.Large,
                cameraMin: min - new Vector3(10),
                cameraMax: max + new Vector3(10),
                playerMin: new Vector3(-300),
                playerMax: new Vector3(300),
                multiplayer: true,
                hostFolder: folder);
        }

        static int PrimaryAxis(Vector3 n)
        {
            float x = MathF.Abs(n.X), y = MathF.Abs(n.Y), z = MathF.Abs(n.Z);
            if (y > x && y >= z) return 1;
            if (z > x && z > y) return 2;
            return 0;
        }
    }
}
