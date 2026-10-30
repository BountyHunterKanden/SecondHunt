using System;
using System.IO;
using System.Linq;
using MphRecomp.Config;

namespace MphRead
{
    // RecompSettings never loses or overwrites the player's settings (owner 10-28): an unreadable file is moved aside, not
    // saved over; .bak and settings_backups/ copies are kept and used when the file can't be read.
    // Run: MphRead.Tools.dll -settingstest   (no ROM/device needed; works in a temp folder)
    internal static class SettingsSafetyTest
    {
        static int _pass, _fail;
        static void Check(string name, bool ok)
        {
            if (ok) _pass++; else _fail++;
            Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {name}");
        }

        static RecompSettings Custom()
        {
            RecompSettings s = new();
            s.FrameRate = 120;
            s.RenderScale = 150;
            s.Music = "hq";
            return s;
        }

        static bool IsCustom(RecompSettings s) => s.FrameRate == 120 && s.RenderScale == 150 && s.Music == "hq";

        public static void Run(string[] args)
        {
            string dir = Path.Combine(Path.GetTempPath(), "mphrecomp_settingstest_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, "recomp_settings.json");
            string log = Path.Combine(dir, "settings_changes.log");
            string Log() => File.Exists(log) ? File.ReadAllText(log) : "";
            string[] Aside() => Directory.GetFiles(dir, "recomp_settings.json.unreadable-*");
            try
            {
                // 1) a good save is mirrored to .bak
                Custom().Save(path);
                Check("save writes .bak", File.Exists(path + ".bak") && File.ReadAllText(path + ".bak") == File.ReadAllText(path));

                // 2) corrupt file + .bak: the .bak's values, the bad file kept aside, never saved over
                File.WriteAllText(path, "{ not json");
                RecompSettings s = Load(path);
                Check("corrupt file loads the .bak values", IsCustom(s));
                Check("corrupt file moved aside", Aside().Length == 1 && File.ReadAllText(Aside()[0]) == "{ not json");
                Check("log names the unreadable file", Log().Contains("unreadable") && Log().Contains("loaded the settings from recomp_settings.json.bak"));
                s.Save(path);
                Check("save after it writes the .bak values", IsCustom(RecompSettings.Load(path)));

                // 3) file missing: restored from .bak
                File.Delete(path);
                Check("missing file restored from .bak", IsCustom(RecompSettings.Load(path)));

                // 4) a file the app can't open (locked here; on the Odin an adb-written file it has no permission to
                // read): nothing is saved over it, and ReloadFrom keeps the in-memory values
                Custom().Save(path);
                string before = File.ReadAllText(path);
                RecompSettings live = Custom();
                live.FrameRate = 90;
                using (FileStream lockIt = new(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                {
                    RecompSettings locked = RecompSettings.Load(path);
                    Check("locked file: loads the backup, not the defaults", IsCustom(locked));
                    locked.RenderScale = 50;
                    locked.Save(path);
                    live.ReloadFrom(path);
                    Check("ReloadFrom on a locked file keeps this instance's values", live.FrameRate == 90);
                    live.Save(path);
                }
                Check("locked file never saved over", File.ReadAllText(path) == before);
                Check("log says saves were skipped", Log().Contains("not saving over it") || Log().Contains("save skipped"));

                // 5) corrupt file and no backups at all: defaults, but the bad file is still kept
                foreach (string f in Directory.GetFiles(dir)) File.Delete(f);
                if (Directory.Exists(Path.Combine(dir, "settings_backups"))) Directory.Delete(Path.Combine(dir, "settings_backups"), true);
                File.WriteAllText(path, "");
                RecompSettings d = Load(path);
                Check("no backup: defaults", d.FrameRate == new RecompSettings().FrameRate);
                Check("no backup: empty file still kept aside", Aside().Length == 1);

                // 6) one save changing many settings keeps the old file; the day's first copy is kept
                Custom().Save(path);
                RecompSettings many = RecompSettings.Load(path);
                many.FrameRate = 30; many.RenderScale = 75; many.Music = "plain"; many.MapPrime = !many.MapPrime;
                many.PrimeHud = !many.PrimeHud; many.GyroDeadband = 5f; many.GyroZoomSensitivity = 300;
                many.TouchControls = "off"; many.MatchMusic = "x";
                many.Save(path);
                string backups = Path.Combine(dir, "settings_backups");
                string[] big = Directory.GetFiles(backups, "before-big-change-*.json");
                Check("big change: old file kept", big.Length == 1 && IsCustom(RecompSettings.Load(CopyTo(big[0], dir))));
                Check("big change: logged", Log().Contains("one save changed"));
                Check("daily copy kept", Directory.GetFiles(backups, "recomp_settings-*.json").Length == 1);

                // 7) a plain round trip is unchanged
                RecompSettings r = RecompSettings.Load(path);
                Check("round trip", r.FrameRate == 30 && r.Music == "plain" && r.TouchControls == "off");
            }
            catch (Exception e)
            {
                Check("no exception: " + e, false);
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch (Exception) { }
            }
            Console.WriteLine($"settingstest: {_pass} passed, {_fail} failed");
            Environment.ExitCode = _fail == 0 ? 0 : 1;
        }

        static RecompSettings Load(string path) => RecompSettings.Load(path);

        static string CopyTo(string file, string dir)
        {
            string sub = Path.Combine(dir, "copy");
            Directory.CreateDirectory(sub);
            string to = Path.Combine(sub, "s.json");
            File.Copy(file, to, true);
            return to;
        }
    }
}
