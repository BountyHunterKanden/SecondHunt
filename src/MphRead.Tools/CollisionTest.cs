using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using MphRecomp.Game;
using MphRecomp.Sim;
using MphRecomp.World;

namespace MphRead
{
    // Tests MeshCollision (floor raycast + wall test) and the sim PlayerController walking on it,
    // including on REAL SANCTORUS collision via RoomLoader. Run: MphRead.Tools.dll -collisiontest.
    internal static class CollisionTest
    {
        static int _pass, _fail;
        static void Check(string name, bool ok) { if (ok) _pass++; else _fail++; Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {name}"); }
        static bool Near(float a, float b, float e) => Math.Abs(a - b) < e;

        public static void Run(string[] args)
        {
            // --- synthetic mesh: floor at Y=0 over [-5,5], plus a wall at X=3 ---
            var tris = new List<float>();
            Quad(tris, new(-5, 0, -5), new(5, 0, -5), new(5, 0, 5), new(-5, 0, 5));        // floor
            Quad(tris, new(3, 0, -5), new(3, 4, -5), new(3, 4, 5), new(3, 0, 5));          // wall at x=3
            var col = new MeshCollision(tris.ToArray());

            Check("floor: found below a point above it", Near(col.FloorBelow(new Vector3(0, 5, 0)), 0f, 1e-4f));
            Check("floor: none below a point under the floor", float.IsNegativeInfinity(col.FloorBelow(new Vector3(0, -1, 0))));
            Check("floor: none outside the floor's XZ extent", float.IsNegativeInfinity(col.FloorBelow(new Vector3(100, 5, 100))));
            Check("wall: blocked toward the wall within range", col.Blocked(new Vector3(0, 1, 0), new Vector3(1, 0, 0), 5f));
            Check("wall: not blocked away from the wall", !col.Blocked(new Vector3(0, 1, 0), new Vector3(-1, 0, 0), 5f));
            Check("wall: not blocked if the wall is beyond range", !col.Blocked(new Vector3(0, 1, 0), new Vector3(1, 0, 0), 2f));

            // --- player physics on the synthetic mesh: gravity settles onto the floor ---
            var world = new SimWorld();
            var player = world.Spawn(new PlayerController(col) { Position = new Vector3(0, 5, 0), EyeHeight = 1.2f, Gravity = 20f, MoveSpeed = 8f });
            for (int i = 0; i < 90; i++) world.Tick(1f / 60f, default);
            Check("player: gravity lands them on the floor (y = floor + eye)", Near(player.Position.Y, 1.2f, 0.05f));

            // walk into the wall -> blocked before passing through it
            var walk = new InputState { Move = new Vector2(1, 0) }; // strafe +X toward the wall at x=3
            for (int i = 0; i < 120; i++) world.Tick(1f / 60f, walk);
            Check("player: wall stops them (x < 3, and they did advance)", player.Position.X < 3f && player.Position.X > 1f);

            // --- REAL SANCTORUS collision: the sim player lands on actual room geometry ---
            string files = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "MphRead", "bin", "Debug", "net9.0"));
            if (File.Exists(Path.Combine(files, "paths.txt")))
            {
                Directory.SetCurrentDirectory(files);
                Paths.UpdatePaths(); Paths.ChooseMphPath();
                MeshCollision? room = RoomLoader.LoadCollision("MP1 SANCTORUS");
                Check("SANCTORUS collision loaded (has triangles)", room != null && room.TriangleCount > 1000);

                var w2 = new SimWorld();
                var p2 = w2.Spawn(new PlayerController(room) { Position = new Vector3(10.7f, 15f, 0f), EyeHeight = 1.2f, Gravity = 20f });
                for (int i = 0; i < 100; i++) w2.Tick(1f / 60f, default);
                float yAt100 = p2.Position.Y;
                for (int i = 0; i < 20; i++) w2.Tick(1f / 60f, default);
                Check("SANCTORUS: player fell from spawn height", p2.Position.Y < 15f);
                Check("SANCTORUS: player landed (didn't fall through the map)", p2.Position.Y > -5f);
                Check("SANCTORUS: player settled on the floor (y stable)", Near(p2.Position.Y, yAt100, 0.05f));
            }
            else Console.WriteLine("  [SKIP] SANCTORUS collision (extracted files not found)");

            Console.WriteLine($"\nCOLLISION TESTS: {_pass} passed, {_fail} failed  =>  {(_fail == 0 ? "ALL GREEN" : "FAILURES")}");
            Environment.ExitCode = _fail == 0 ? 0 : 1;
        }

        // two triangles (a,b,c) + (a,c,d) into the 9-floats-per-tri buffer
        static void Quad(List<float> t, Vector3 a, Vector3 b, Vector3 c, Vector3 d)
        { Tri(t, a, b, c); Tri(t, a, c, d); }
        static void Tri(List<float> t, Vector3 a, Vector3 b, Vector3 c)
        { t.Add(a.X); t.Add(a.Y); t.Add(a.Z); t.Add(b.X); t.Add(b.Y); t.Add(b.Z); t.Add(c.X); t.Add(c.Y); t.Add(c.Z); }
    }
}
