using System;
using System.IO;
using System.Linq;
using System.Numerics;
using MphRecomp.Sim;
using MphRecomp.World;

namespace MphRead
{
    // Tests the RoomLoader against REAL extracted MPH room data: populates a SimWorld from MP1
    // SANCTORUS's entity list and checks the pickups match the known authored layout. Needs the
    // game files (uses the desktop bin's paths.txt). Run: MphRead.Tools.dll -roomloadtest.
    internal static class RoomLoadTest
    {
        static int _pass, _fail;
        static void Check(string name, bool ok) { if (ok) _pass++; else _fail++; Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {name}"); }

        public static void Run(string[] args)
        {
            string files = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "MphRead", "bin", "Debug", "net9.0"));
            if (!File.Exists(Path.Combine(files, "paths.txt")))
            {
                Console.WriteLine("  [SKIP] extracted game files not found; run desktop extraction first.");
                return;
            }
            Directory.SetCurrentDirectory(files);
            Paths.UpdatePaths();
            Paths.ChooseMphPath();

            var world = new SimWorld();
            int placed = RoomLoader.PopulatePickups(world, "MP1 SANCTORUS");
            Check("SANCTORUS: 19 item pickups placed", placed == 19);

            world.Tick(1f / 60f, default); // integrate the queued spawns
            var pickups = world.Entities.OfType<PickupEntity>().ToList();
            Check("pickups are live sim entities after a tick", pickups.Count == 19);
            Check("all pickups have a pick_* model name", pickups.All(p => p.ModelName.StartsWith("pick_") || p.ModelName.StartsWith("Pick") || p.ModelName.StartsWith("Artifact")));

            // known SANCTORUS layout (verified earlier): weapons at the four corners (|x|=|z|=16.9),
            // a health_A pair at z = +/-29.4, ammo_orange at the exact centre.
            bool corner = pickups.Any(p => Math.Abs(Math.Abs(p.Position.X) - 16.9f) < 0.3f && Math.Abs(Math.Abs(p.Position.Z) - 16.9f) < 0.3f);
            Check("a weapon pickup sits at an arena corner (~16.9, y, ~16.9)", corner);
            bool centre = pickups.Any(p => Math.Abs(p.Position.X) < 0.2f && Math.Abs(p.Position.Z) < 0.2f);
            Check("a pickup sits at the arena centre", centre);
            bool inBounds = pickups.All(p => Math.Abs(p.Position.X) < 40f && Math.Abs(p.Position.Z) < 40f && p.Position.Y is > -5f and < 15f);
            Check("all pickups within the SANCTORUS collision bounds", inBounds);

            // spin behaviour: facing rotates over ticks
            var one = pickups[0];
            Vector3 f0 = one.Facing;
            for (int i = 0; i < 10; i++) world.Tick(1f / 60f, default);
            Check("pickups spin (facing changes over time)", Vector3.Distance(one.Facing, f0) > 0.01f);

            // snapshot exposes them for the renderer to draw (ModelName-tagged render items)
            var snap = new RenderSnapshot();
            world.Snapshot(0f, snap);
            Check("snapshot lists all pickups as render items", snap.Items.Count == 19 && snap.Items.All(i => !string.IsNullOrEmpty(i.Model)));

            Console.WriteLine($"\nROOM LOADER TESTS: {_pass} passed, {_fail} failed  =>  {(_fail == 0 ? "ALL GREEN" : "FAILURES")}");
            Environment.ExitCode = _fail == 0 ? 0 : 1;
        }
    }
}
