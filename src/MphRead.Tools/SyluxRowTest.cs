using System;
using System.Collections;
using System.IO;
using System.Reflection;
using MphRecomp.Campaign;
using MphRecomp.Config;
using MphRecomp.Frontend;

namespace MphRead
{
    // -syluxrow: SECOND HUNT SETTINGS' CAMPAIGN SYLUX TYPE row (owner 2026-11-08: the same as CAMPAIGN TROOPER TYPE). With
    // Beyond's Sylux imported it steps ORIGINAL <-> BEYOND SYLUX and saves SyluxSkin; it shows only once the campaign's
    // hunters are unlocked and a save plays Sylux, and never with no Sylux skin imported. Makes its own data folder (an
    // hd/MP4Sylux with a stand-in .dae), so it needs no import.
    internal static partial class CampaignSim
    {
        public static void SyluxRow()
        {
            int pass = 0, fail = 0;
            void Check(string name, bool ok)
            {
                if (ok) pass++; else fail++;
                Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}: {name}");
            }
            object? Labelled(RecompMenus menus, string label)
            {
                var rows = (IList)typeof(RecompMenus).GetMethod("SettingRows", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(menus, null)!;
                foreach (object r in rows)
                {
                    if ((string?)r.GetType().GetField("Label")!.GetValue(r) == label) return r;
                }
                return null;
            }
            object? Row(RecompMenus menus) => Labelled(menus, "campaign sylux\ntype");
            string Value(object row) => ((Func<string>)row.GetType().GetField("Value")!.GetValue(row)!)();
            void Step(object row, int s) => ((Action<int>)row.GetType().GetField("Step")!.GetValue(row)!)(s);

            if (!ToolPaths.UseDesktopExtraction()) return; // (a campaign save's story reads the ROM's scan strings)
            string root = Path.Combine(Path.GetTempPath(), "mph_syluxrow_" + Environment.ProcessId);
            string data = Path.Combine(root, "data"), empty = Path.Combine(root, "empty");
            string settingsPath = Path.Combine(data, "s.json");
            string savesDir = Path.Combine(root, "saves");
            try
            {
                Directory.CreateDirectory(Path.Combine(data, "hd", "MP4Sylux"));
                File.WriteAllText(Path.Combine(data, "hd", "MP4Sylux", "MP4Sylux.dae"), "");
                Directory.CreateDirectory(Path.Combine(empty, "hd"));
                Directory.CreateDirectory(savesDir);
                var saves = new CampaignSaves(savesDir);
                var settings = new RecompSettings();
                Check("SyluxSkin starts as original", settings.SyluxSkin == "original");
                var menus = new RecompMenus(null!, null!, settings, settingsPath, modsDir: null);
                Check("no saves folder: the row is hidden", Row(menus) == null);
                menus.SavesDir = savesDir;
                Check("no saves: the row is hidden", Row(menus) == null);
                saves.CreateNew(1, Hunter.Sylux, null, null);
                Check("a Sylux file but the hunters not unlocked: the row is hidden", Row(menus) == null);
                saves.Delete(1);
                saves.CreateNew(2);
                File.WriteAllText(saves.UnlockMarkerPath, "");
                Check("unlocked but only a Samus file: the row is hidden", Row(menus) == null);
                saves.CreateNew(3, Hunter.Weavel, MphRecomp.Multiplayer.Characters.Trooper.Id, "MP1PowerTrooper");
                Check("unlocked with a Trooper file (on Weavel): the row is hidden", Row(menus) == null);
                saves.Delete(3);
                saves.CreateNew(3, Hunter.Sylux, null, null);
                object? row = Row(menus);
                Check("unlocked with a Sylux file: the row is there", row != null);
                if (row != null)
                {
                    Check("it shows ORIGINAL by default", Value(row) == "ORIGINAL");
                    Step(row, 1);
                    Check("right: BEYOND SYLUX (MP4Sylux)", Value(row) == "BEYOND SYLUX" && settings.SyluxSkin == "MP4Sylux");
                    Step(row, 1);
                    Check("right again wraps to ORIGINAL", Value(row) == "ORIGINAL" && settings.SyluxSkin == "original");
                    Step(row, -1);
                    Check("left from ORIGINAL wraps to BEYOND SYLUX", settings.SyluxSkin == "MP4Sylux");
                    settings.Save(settingsPath);
                    Check("SyluxSkin saves and loads", RecompSettings.Load(settingsPath).SyluxSkin == "MP4Sylux");
                    settings.SyluxSkin = "junk";
                    Check("an unknown setting shows ORIGINAL", Value(row) == "ORIGINAL");
                    string? hint = SettingHints.For(SettingHints.All, "campaignsyluxtype", "BEYOND SYLUX", publicBuild: true, desktop: false);
                    Check("BEYOND SYLUX has its own hint", hint != null && hint.Contains("beyond"));
                }
                // a campaign's pause: the row follows the file being played, not the other slots (owner 2026-11-09)
                saves.CreateNew(1, Hunter.Weavel, MphRecomp.Multiplayer.Characters.Trooper.Id, "MP1PowerTrooper");
                menus.CampaignPlays = () => null;
                Check("in a Samus campaign with a Sylux file B: the Sylux row is hidden", Row(menus) == null);
                menus.CampaignPlays = () => MphRecomp.Multiplayer.Characters.Trooper.Id;
                Check("in a Trooper campaign: the Sylux row is hidden", Row(menus) == null);
                menus.CampaignPlays = () => MphRecomp.Multiplayer.Characters.Sylux.Id;
                Check("in a Sylux campaign: the Sylux row is there", Row(menus) != null);
                menus.CampaignPlays = null;
                Check("the front end again: any Sylux file shows it", Row(menus) != null);
                var bare = new RecompMenus(null!, null!, new RecompSettings(), Path.Combine(empty, "s.json"), modsDir: null);
                bare.SavesDir = savesDir;
                Check("no Sylux skin imported (a Sylux file all the same): the row is hidden", Row(bare) == null);
            }
            finally
            {
                try { Directory.Delete(root, recursive: true); } catch { }
            }
            Console.WriteLine($"  {pass}/{pass + fail} passed");
        }
    }
}
