using System;
using System.Collections;
using System.IO;
using System.Reflection;
using MphRecomp.Campaign;
using MphRecomp.Config;
using MphRecomp.Frontend;

namespace MphRead
{
    // -trooperrow <data dir with hd/>: SECOND HUNT SETTINGS' CAMPAIGN TROOPER TYPE row (owner 2026-10-28). With the Troopers
    // imported it steps through them (POWER, ICE, WAVE, PLASMA) and saves TrooperSkin; it shows only once the campaign's
    // hunters are unlocked and a save plays the Trooper, and never with no hd/ folder.
    internal static partial class CampaignSim
    {
        public static void TrooperRow(string[] args)
        {
            int pass = 0, fail = 0;
            void Check(string name, bool ok)
            {
                if (ok) pass++; else fail++;
                Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}: {name}");
            }
            object? Row(RecompMenus menus)
            {
                var rows = (IList)typeof(RecompMenus).GetMethod("SettingRows", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(menus, null)!;
                foreach (object r in rows)
                {
                    if ((string?)r.GetType().GetField("Label")!.GetValue(r) == "campaign trooper\ntype") return r;
                }
                return null;
            }
            string Value(object row) => ((Func<string>)row.GetType().GetField("Value")!.GetValue(row)!)();
            void Step(object row, int s) => ((Action<int>)row.GetType().GetField("Step")!.GetValue(row)!)(s);

            if (!ToolPaths.UseDesktopExtraction()) return; // (a campaign save's story reads the ROM's scan strings)
            string settingsPath = Path.Combine(args[1], "trooperrow_test_settings.json");
            string empty = Path.Combine(Path.GetTempPath(), "mph_trooperrow_" + Environment.ProcessId);
            string savesDir = Path.Combine(empty, "saves");
            try
            {
                Directory.CreateDirectory(savesDir);
                var saves = new CampaignSaves(savesDir);
                var settings = new RecompSettings();
                var menus = new RecompMenus(null!, null!, settings, settingsPath, modsDir: null);
                Check("no saves folder: the row is hidden", Row(menus) == null);
                menus.SavesDir = savesDir;
                Check("no saves: the row is hidden", Row(menus) == null);
                saves.CreateNew(1, Hunter.Weavel, MphRecomp.Multiplayer.Characters.Trooper.Id, "MP1PowerTrooper");
                Check("a Trooper file but the hunters not unlocked: the row is hidden", Row(menus) == null);
                saves.Delete(1);
                saves.CreateNew(2);
                File.WriteAllText(saves.UnlockMarkerPath, "");
                Check("unlocked but only a Samus file: the row is hidden", Row(menus) == null);
                saves.CreateNew(3, Hunter.Weavel, MphRecomp.Multiplayer.Characters.Trooper.Id, "MP1PowerTrooper");
                object? row = Row(menus);
                Check("unlocked with a Trooper file: the row is there", row != null);
                if (row != null)
                {
                    var seen = new System.Collections.Generic.List<string>();
                    for (int i = 0; i < 5; i++)
                    {
                        seen.Add($"{Value(row)} ({settings.TrooperSkin})");
                        Step(row, 1);
                    }
                    Console.WriteLine("  steps: " + String.Join(" -> ", seen));
                    Check("POWER first, then ICE, WAVE, PLASMA and back to POWER", seen[0].StartsWith("POWER") && seen[1].StartsWith("ICE")
                        && seen[2].StartsWith("WAVE") && seen[3].StartsWith("PLASMA") && seen[4].StartsWith("POWER"));
                    settings.TrooperSkin = "MP1PowerTrooper";
                    Step(row, -1);
                    Check("left from POWER wraps to PLASMA", settings.TrooperSkin == "MP1PlasmaTrooper");
                    settings.TrooperSkin = "MP1WaveTrooper";
                    settings.Save(settingsPath);
                    Check("TrooperSkin saves and loads", RecompSettings.Load(settingsPath).TrooperSkin == "MP1WaveTrooper");
                    settings.TrooperSkin = "junk";
                    Check("an unknown setting shows the first imported one", Value(row) == "POWER");
                }
                var bare = new RecompMenus(null!, null!, new RecompSettings(), Path.Combine(empty, "s.json"), modsDir: null);
                bare.SavesDir = savesDir;
                Check("no Trooper imported (a Trooper file all the same): the row is hidden", Row(bare) == null);
            }
            finally
            {
                try { File.Delete(settingsPath); File.Delete(settingsPath + ".bak"); } catch { }
                try { Directory.Delete(empty, recursive: true); } catch { }
            }
            Console.WriteLine($"  {pass}/{pass + fail} passed");
        }
    }
}
