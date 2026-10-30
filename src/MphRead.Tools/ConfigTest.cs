using System;
using System.IO;
using MphRecomp.Config;

namespace MphRead
{
    // Headless regression tests for the mph-recomp config/tuning layer (MphRecomp.Config).
    // Run: MphRead.Tools.dll -configtest   (no ROM/device needed). Exercises defaults, layered
    // partial override (deep merge), round-trip, missing/corrupt recovery, and range clamping.
    internal static class ConfigTest
    {
        static int _pass, _fail;
        static void Check(string name, bool ok)
        {
            if (ok) _pass++; else _fail++;
            Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {name}");
        }

        public static void Run(string[] args)
        {
            string dir = Path.Combine(Path.GetTempPath(), "mphrecomp_configtest_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, "config.json");
            try
            {
                // 1) defaults reproduce the renderer's current hardcoded values
                var d = ConfigManager.Default();
                Check("default FOV = 62", Math.Abs(d.Camera.FovDegrees - 62f) < 1e-4f);
                Check("default eye-height factor = 0.06", Math.Abs(d.Movement.EyeHeightFactor - 0.06f) < 1e-4f);
                Check("default ambient = 0.55", Math.Abs(d.Lighting.AmbientBase - 0.55f) < 1e-4f);

                // 2) missing file -> defaults, no throw
                Check("Load(missing) = defaults", Math.Abs(ConfigManager.Load(path).Camera.FovDegrees - 62f) < 1e-4f);

                // 3) round-trip
                var c = ConfigManager.Default();
                c.Camera.FovDegrees = 75f; c.Movement.EyeHeightFactor = 0.09f;
                ConfigManager.Save(c, path);
                var back = ConfigManager.Load(path);
                Check("round-trip FOV", Math.Abs(back.Camera.FovDegrees - 75f) < 1e-4f);
                Check("round-trip eye height", Math.Abs(back.Movement.EyeHeightFactor - 0.09f) < 1e-4f);

                // 4) PARTIAL override merges over defaults: only the specified key changes
                File.WriteAllText(path, "{ \"Camera\": { \"FovDegrees\": 90 } }");
                var merged = ConfigManager.Load(path);
                Check("partial: overridden FOV applied", Math.Abs(merged.Camera.FovDegrees - 90f) < 1e-4f);
                Check("partial: unspecified Camera.LookSpeed kept default", Math.Abs(merged.Camera.LookSpeed - 2.2f) < 1e-4f);
                Check("partial: unspecified Movement kept default", Math.Abs(merged.Movement.GravityFactor - 2.0f) < 1e-4f);

                // 5) deep merge into a nested section: sibling fields survive
                var overridden = ConfigManager.ApplyOverrides(ConfigManager.Default(),
                    "{ \"Lighting\": { \"AmbientBase\": 0.8 } }");
                Check("deep-merge: Lighting.AmbientBase overridden", Math.Abs(overridden.Lighting.AmbientBase - 0.8f) < 1e-4f);
                Check("deep-merge: Lighting.DiffuseScale kept default", Math.Abs(overridden.Lighting.DiffuseScale - 0.6f) < 1e-4f);
                Check("deep-merge: Lighting.Light1Dir kept default",
                    Math.Abs(overridden.Lighting.Light1Dir.Y - 0.85f) < 1e-4f);

                // 6) corrupt file -> defaults
                File.WriteAllText(path, "not json {{{");
                Check("Load(corrupt) = defaults", Math.Abs(ConfigManager.Load(path).Camera.FovDegrees - 62f) < 1e-4f);

                // 7) validation clamps out-of-range values
                var clamped = ConfigManager.ApplyOverrides(ConfigManager.Default(),
                    "{ \"Camera\": { \"FovDegrees\": 500 }, \"Lighting\": { \"AmbientBase\": 9 } }");
                Check("clamp: FOV capped to 110", Math.Abs(clamped.Camera.FovDegrees - 110f) < 1e-4f);
                Check("clamp: ambient capped to 1", Math.Abs(clamped.Lighting.AmbientBase - 1f) < 1e-4f);

                // 8) NaN/inf rejected to the low bound
                var bad = ConfigManager.Validate(new GameConfig { Camera = new CameraConfig { FovDegrees = float.NaN } });
                Check("validate: NaN -> low bound", bad.Camera.FovDegrees == 30f);
            }
            finally { try { Directory.Delete(dir, recursive: true); } catch { } }
            Console.WriteLine($"\nCONFIG TESTS: {_pass} passed, {_fail} failed  =>  {(_fail == 0 ? "ALL GREEN" : "FAILURES")}");
            Environment.ExitCode = _fail == 0 ? 0 : 1;
        }
    }
}
