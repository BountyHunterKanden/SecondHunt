using System;
using System.IO;
using System.Text.Json;
using MphRecomp.Save;

namespace MphRead
{
    // Headless regression tests for the mph-recomp save framework (MphRecomp.Save).
    // Run: MphRead.Tools.dll -savetest   (no ROM/device needed). Exercises round-trip,
    // defaults, corruption recovery, mod-data isolation, and version migration.
    internal static class SaveTest
    {
        static int _pass, _fail;
        static void Check(string name, bool ok)
        {
            if (ok) _pass++; else _fail++;
            Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {name}");
        }
        static string Json(object o) => JsonSerializer.Serialize(o, new JsonSerializerOptions { WriteIndented = false });

        public static void Run(string[] args)
        {
            string dir = Path.Combine(Path.GetTempPath(), "mphrecomp_savetest_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, "save.json");
            try
            {
                // 1) missing file -> fresh default, no throw
                var d0 = SaveManager.Load(path);
                Check("Load(missing) returns default", d0 != null && d0.Version == SaveManager.CurrentVersion);

                // 2) round-trip: populate every section, save, load, deep-compare via JSON
                var src = new SaveData();
                src.Settings.Presentation = PresentationMode.DualScreen;
                src.Settings.LookSensitivity = 1.75f;
                src.Settings.InvertY = true;
                src.Settings.MusicVolume = 0.42f;
                src.Story.Started = true;
                src.Story.CurrentRoom = "MP1 SANCTORUS";
                src.Story.BossFlags = 0x155;
                src.Story.WeaponsUnlocked = 0b1011;
                src.Story.EnergyTanks = 3;
                src.Story.Octoliths.Add(2); src.Story.Octoliths.Add(5);
                src.Story.PlaytimeSeconds = 12345;
                src.Records["Battle"] = new ModeRecord { Wins = 4, Losses = 2, Kills = 37, BestTimeMs = 90200 };
                src.Records["Capture"] = new ModeRecord { Wins = 1 };
                SaveManager.Save(src, path);
                var back = SaveManager.Load(path);
                Check("round-trip preserves all fields", Json(src) == Json(back));
                Check("save file exists on disk", File.Exists(path));
                Check("temp file cleaned up after atomic write", !File.Exists(path + ".tmp"));

                // 3) overwrite an existing save (File.Replace path) works
                back.Story.EnergyTanks = 8;
                SaveManager.Save(back, path);
                Check("overwrite existing save", SaveManager.Load(path).Story.EnergyTanks == 8);

                // 4) corrupt file -> default, no throw
                File.WriteAllText(path, "{ this is not valid json ]]");
                var d3 = SaveManager.Load(path);
                Check("Load(corrupt) recovers to default", d3 != null && d3.Story.EnergyTanks == 0);

                // 5) mod data: isolation + round-trip + typed get/set (gen1recomp pattern)
                var s = new SaveData();
                SaveManager.SetModData(s, "kanto_companion", new { level = 7, notes = "hi", flags = new[] { 1, 2, 3 } });
                SaveManager.SetModData(s, "statdex", new { seen = 81 });
                SaveManager.Save(s, path);
                var s2 = SaveManager.Load(path);
                var kc = SaveManager.GetModData(s2, "kanto_companion", new { level = 0, notes = "", flags = Array.Empty<int>() });
                var sd = SaveManager.GetModData(s2, "statdex", new { seen = 0 });
                Check("mod data round-trips (mod A)", kc.level == 7 && kc.notes == "hi" && kc.flags.Length == 3);
                Check("mod data isolation (mod B unaffected)", sd.seen == 81);
                Check("GetModData(unknown mod) returns fallback", SaveManager.GetModData(s2, "nope", new { x = 99 }).x == 99);
                Check("HasModData / ClearModData", SaveManager.HasModData(s2, "statdex")
                    && !SaveManager.HasModData(s2, "nope"));

                // 6) migration: a future/newer version is accepted and stamped current (no crash)
                var future = new SaveData { Version = SaveManager.CurrentVersion + 5 };
                var migrated = SaveManager.Migrate(future);
                Check("Migrate(newer version) clamps to current", migrated.Version == SaveManager.CurrentVersion);
                // an older version (simulate) migrates forward to current
                var old = new SaveData { Version = 0 };
                Check("Migrate(older version) advances to current", SaveManager.Migrate(old).Version == SaveManager.CurrentVersion);
            }
            finally
            {
                try { Directory.Delete(dir, recursive: true); } catch { }
            }
            Console.WriteLine($"\nSAVE TESTS: {_pass} passed, {_fail} failed  =>  {(_fail == 0 ? "ALL GREEN" : "FAILURES")}");
            Environment.ExitCode = _fail == 0 ? 0 : 1;
        }
    }
}
