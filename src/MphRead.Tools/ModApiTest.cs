using System;
using System.IO;
using System.Linq;
using MphRecomp.Assets;
using MphRecomp.Config;
using MphRecomp.Mods;
using MphRecomp.Sim;

namespace MphRead
{
    // Headless tests for the extended mod API that connects the pillars: mod.config (read tuning),
    // mod.asset (read override-able assets via the VFS), and mod.spawn (add sim entities). Run:
    // MphRead.Tools.dll -modapitest  (no ROM/device).
    internal static class ModApiTest
    {
        static int _pass, _fail;
        static void Check(string name, bool ok) { if (ok) _pass++; else _fail++; Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {name}"); }

        const string Lua = @"
mod.save('fov', mod.config('Camera.FovDegrees', 0))
mod.save('ambient', mod.config('Lighting.AmbientBase', 0))
mod.save('missing', mod.config('Nope.Nope', -1))
mod.save('greeting', mod.asset('data/hello.txt'))
mod.save('noAsset', mod.asset('data/none.txt') == nil)
mod.save('spawnId', mod.spawn('pick_health_A', 1, 2, 3))
";

        public static void Run(string[] args)
        {
            string dir = Path.Combine(Path.GetTempPath(), "mphrecomp_modapi_" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(Path.Combine(dir, "base", "data"));
                File.WriteAllText(Path.Combine(dir, "base", "data", "hello.txt"), "hi mod");
                var vfs = new AssetVfs();
                vfs.AddLayer("base", Path.Combine(dir, "base"));

                var world = new SimWorld();
                var host = new ModHost { Config = ConfigManager.Default(), Assets = vfs, World = world };
                var m = host.LoadFromSource(new ModManifest { Id = "apimod" }, Lua);
                Check("mod loaded (no error)", m.Enabled && m.Error == null);

                // config reads
                Check("mod.config reads a number (FOV 62)", m.Data["fov"].GetDouble() == 62);
                Check("mod.config reads nested (ambient 0.55)", Math.Abs(m.Data["ambient"].GetDouble() - 0.55) < 1e-6);
                Check("mod.config unknown path -> default", m.Data["missing"].GetDouble() == -1);

                // asset reads through the VFS
                Check("mod.asset reads a base asset", m.Data["greeting"].GetString() == "hi mod");
                Check("mod.asset missing -> nil", m.Data["noAsset"].GetBoolean());

                // spawn into the sim world
                Check("mod.spawn returned an id", m.Data["spawnId"].GetDouble() >= 1);
                world.Tick(1f / 60f, default); // integrate the queued spawn
                var spawned = world.Entities.FirstOrDefault(e => e.ModelName == "pick_health_A");
                Check("mod.spawn added the entity to the world", spawned != null);
                Check("mod.spawn placed it at the given position",
                    spawned != null && spawned.Position == new System.Numerics.Vector3(1, 2, 3));

                // asset override: a mod layer replaces the greeting -> mod.asset sees the override
                Directory.CreateDirectory(Path.Combine(dir, "mod", "data"));
                File.WriteAllText(Path.Combine(dir, "mod", "data", "hello.txt"), "modded");
                vfs.AddLayer("mymod", Path.Combine(dir, "mod"));
                var host2 = new ModHost { Assets = vfs };
                var m2 = host2.LoadFromSource(new ModManifest { Id = "reader" }, "mod.save('g', mod.asset('data/hello.txt'))");
                Check("mod.asset honors override layer", m2.Data["g"].GetString() == "modded");

                // no context -> calls degrade gracefully (return defaults / nil, no crash)
                var bare = new ModHost().LoadFromSource(new ModManifest { Id = "bare" },
                    "mod.save('c', mod.config('Camera.FovDegrees', 99)) mod.save('a', mod.asset('x') == nil) mod.save('s', mod.spawn('m') == nil)");
                Check("no context: config -> default", bare.Data["c"].GetDouble() == 99);
                Check("no context: asset -> nil, spawn -> nil", bare.Data["a"].GetBoolean() && bare.Data["s"].GetBoolean());
            }
            finally { try { Directory.Delete(dir, recursive: true); } catch { } }

            Console.WriteLine($"\nMOD API TESTS: {_pass} passed, {_fail} failed  =>  {(_fail == 0 ? "ALL GREEN" : "FAILURES")}");
            Environment.ExitCode = _fail == 0 ? 0 : 1;
        }
    }
}
