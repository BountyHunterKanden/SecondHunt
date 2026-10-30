using System;
using System.IO;
using System.Linq;
using MphRecomp.Mods;
using MphRecomp.Save;

namespace MphRead
{
    // Headless regression tests for the mph-recomp mod manager (MphRecomp.Mods.ModManager). Run:
    // MphRead.Tools.dll -modmgrtest  (no ROM/device). Covers folder discovery, enable/disable,
    // load ordering, state persistence in the save, and ordered loading into a ModHost (only
    // enabled mods, in order).
    internal static class ModManagerTest
    {
        static int _pass, _fail;
        static void Check(string name, bool ok) { if (ok) _pass++; else _fail++; Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {name}"); }

        public static void Run(string[] args)
        {
            // --- folder discovery from disk ---
            string dir = Path.Combine(Path.GetTempPath(), "mphrecomp_modmgr_" + Guid.NewGuid().ToString("N"));
            try
            {
                WriteMod(dir, "alpha", "Alpha", "1.0.0");
                WriteMod(dir, "beta", "Beta", "0.9.0");
                Directory.CreateDirectory(Path.Combine(dir, "notamod")); // no mod.json -> ignored

                var mgr = new ModManager();
                mgr.Discover(dir);
                mgr.Add(new ModManifest { Id = "gamma", Name = "Gamma", Entry = "x" }, "mod.log('gamma up')"); // inline
                Check("discover: found folder mods with mod.json (2) + skipped non-mod", mgr.Available.Count == 3);
                Check("discover: manifest fields parsed", mgr.Available.Any(e => e.Manifest.Id == "alpha" && e.Manifest.Name == "Alpha"));

                // --- default state: all enabled, discovery order ---
                Check("default: all enabled", mgr.IsEnabled("alpha") && mgr.IsEnabled("beta") && mgr.IsEnabled("gamma"));
                Check("default order = discovery order",
                    mgr.EnabledInOrder().Select(e => e.Manifest.Id).SequenceEqual(new[] { "alpha", "beta", "gamma" }));

                // --- enable/disable + explicit order ---
                mgr.SetEnabled("beta", false);
                mgr.SetOrder(new[] { "gamma", "alpha" });
                Check("disabled mod excluded from load list", !mgr.EnabledInOrder().Any(e => e.Manifest.Id == "beta"));
                Check("explicit order respected",
                    mgr.EnabledInOrder().Select(e => e.Manifest.Id).SequenceEqual(new[] { "gamma", "alpha" }));

                // --- persistence: state survives a save round-trip into a fresh manager ---
                var save = new SaveData();
                mgr.SaveState(save);
                var save2 = RoundTrip(save);
                var mgr2 = new ModManager();
                mgr2.Discover(dir);
                mgr2.Add(new ModManifest { Id = "gamma", Name = "Gamma", Entry = "x" }, "mod.log('gamma up')");
                mgr2.LoadState(save2);
                Check("persisted: beta still disabled after reload", !mgr2.IsEnabled("beta"));
                Check("persisted: order still gamma, alpha",
                    mgr2.EnabledInOrder().Select(e => e.Manifest.Id).SequenceEqual(new[] { "gamma", "alpha" }));

                // --- LoadEnabled actually loads (in order) into a host, real folder Lua included ---
                var order = new System.Collections.Generic.List<string>();
                ModHost host = mgr2.LoadEnabled((id, msg) => order.Add(id));
                Check("LoadEnabled: only enabled mods loaded", host.Mods.Count == 2 && host.Get("beta") == null);
                Check("LoadEnabled: loaded in order (gamma before alpha)",
                    host.Mods.Select(m => m.Manifest.Id).SequenceEqual(new[] { "gamma", "alpha" }));
                Check("LoadEnabled: folder mod's Lua ran (alpha logged)", order.Contains("alpha"));
            }
            finally { try { Directory.Delete(dir, recursive: true); } catch { } }

            // newly discovered (unordered) mods append after ordered ones
            var m3 = new ModManager();
            m3.Add(new ModManifest { Id = "a" }, "");
            m3.Add(new ModManifest { Id = "b" }, "");
            m3.Add(new ModManifest { Id = "c" }, "");
            m3.SetOrder(new[] { "c" }); // only c ordered; a,b are "new"
            Check("new mods append after explicitly-ordered ones",
                m3.EnabledInOrder().Select(e => e.Manifest.Id).SequenceEqual(new[] { "c", "a", "b" }));

            Console.WriteLine($"\nMOD MANAGER TESTS: {_pass} passed, {_fail} failed  =>  {(_fail == 0 ? "ALL GREEN" : "FAILURES")}");
            Environment.ExitCode = _fail == 0 ? 0 : 1;
        }

        static void WriteMod(string root, string id, string name, string version)
        {
            string folder = Path.Combine(root, id);
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "mod.json"),
                $"{{ \"Id\": \"{id}\", \"Name\": \"{name}\", \"Version\": \"{version}\", \"Entry\": \"main.lua\" }}");
            File.WriteAllText(Path.Combine(folder, "main.lua"), $"mod.log('{id} loaded')");
        }

        static SaveData RoundTrip(SaveData s)
        {
            string p = Path.Combine(Path.GetTempPath(), "mphrecomp_mmstate_" + Guid.NewGuid().ToString("N") + ".json");
            try { SaveManager.Save(s, p); return SaveManager.Load(p); }
            finally { try { File.Delete(p); } catch { } }
        }
    }
}
