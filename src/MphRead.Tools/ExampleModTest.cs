using System;
using System.Collections.Generic;
using System.IO;
using MphRecomp.Assets;
using MphRecomp.Config;
using MphRecomp.Game;
using MphRecomp.Input;
using MphRecomp.Mods;
using MphRecomp.Save;

namespace MphRead
{
    // End-to-end test that loads the REAL shipped example mod (docs/mods/example_stat_tracker) via
    // the mod manager into a full GameSession and drives it -- so the example, the docs, and the
    // whole mod+game stack are proven to work together. Run: MphRead.Tools.dll -exampletest.
    internal static class ExampleModTest
    {
        static int _pass, _fail;
        static void Check(string name, bool ok) { if (ok) _pass++; else _fail++; Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {name}"); }

        public static void Run(string[] args)
        {
            string? docs = FindDocsDir();
            if (docs == null) { Console.WriteLine("  [FAIL] could not locate docs/ (shipped example mod)"); Environment.ExitCode = 1; return; }
            string modsDir = Path.Combine(docs, "mods");

            var logs = new List<string>();
            void Log(string id, string msg) => logs.Add($"[{id}] {msg}");

            // discover the shipped mod; mount docs/ so its asset path resolves
            var mgr = new ModManager();
            mgr.Discover(modsDir);
            Check("discovered the shipped example mod", mgr.Available.Count >= 1
                && mgr.Available[0].Manifest.Id == "example_stat_tracker");

            var vfs = new AssetVfs();
            vfs.AddLayer("base", docs);

            var save = new SaveData();
            // session first (wires config/assets/world into its ModHost) THEN load mods into it
            var session = new GameSession(config: ConfigManager.Default(), save: save, mods: new ModHost(Log), assets: vfs);
            mgr.LoadInto(session.Mods, Log);

            var mod = session.Mods.Get("example_stat_tracker");
            Check("example mod loaded without error", mod != null && mod.Enabled && mod.Error == null);

            session.Start();                              // fires 'start' -> launch #, config + asset reads
            session.EnterRoom("MP1 SANCTORUS");           // fires 'room_enter' -> visit count
            var idle = new RawInput();
            for (int i = 0; i < 30; i++) session.Update(1.0 / 60.0, idle); // 30 ticks -> playtime

            Check("mod tracked a launch", mod!.Data["launches"].GetDouble() == 1);
            Check("mod counted the room visit", mod.Data["visits"].GetProperty("MP1 SANCTORUS").GetDouble() == 1);
            Check("mod accumulated playtime over ticks", mod.Data["playSeconds"].GetDouble() > 0.4);
            Check("mod read config (FOV 62 logged)", logs.Exists(l => l.Contains("field of view is 62")));
            Check("mod read its asset (welcome text logged)", logs.Exists(l => l.Contains("Thanks for trying")));

            // persistence: save, then a fresh session sees the launch count carry forward (1 -> 2)
            string tmp = Path.Combine(Path.GetTempPath(), "mphrecomp_example_" + Guid.NewGuid().ToString("N") + ".json");
            try
            {
                session.SaveTo(tmp);
                var save2 = SaveManager.Load(tmp);
                var session2 = new GameSession(save: save2, mods: new ModHost(), assets: vfs);
                mgr.LoadInto(session2.Mods);
                session2.Start();
                var mod2 = session2.Mods.Get("example_stat_tracker");
                Check("launch count persisted across sessions (1 -> 2)", mod2!.Data["launches"].GetDouble() == 2);
            }
            finally { try { File.Delete(tmp); } catch { } }

            Console.WriteLine($"\nEXAMPLE-MOD (end-to-end) TESTS: {_pass} passed, {_fail} failed  =>  {(_fail == 0 ? "ALL GREEN" : "FAILURES")}");
            Environment.ExitCode = _fail == 0 ? 0 : 1;
        }

        // walk up from the tools binary until we find docs/mods/example_stat_tracker/mod.json
        static string? FindDocsDir()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            for (int i = 0; i < 8 && dir != null; i++, dir = dir.Parent)
            {
                string candidate = Path.Combine(dir.FullName, "docs", "mods", "example_stat_tracker", "mod.json");
                if (File.Exists(candidate)) return Path.Combine(dir.FullName, "docs");
            }
            return null;
        }
    }
}
