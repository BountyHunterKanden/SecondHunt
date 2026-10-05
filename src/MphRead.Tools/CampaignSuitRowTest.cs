using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using MphRecomp.Config;
using MphRecomp.Frontend;

namespace MphRead
{
    // -suitrow: RECOMP SETTINGS' samus suit row on a fake device folder (no ROM needed). guns/mp1 (the first export of
    // P1 PWR's gun, no HD body) is not offered, and a saved "mp1" setting reads (and saves) as MP1PowerSuit.
    internal static partial class CampaignSim
    {
        public static void SuitRow(string[] args)
        {
            string root = Path.Combine(Path.GetTempPath(), "mph_suitrow_" + Environment.ProcessId);
            int pass = 0, fail = 0;
            void Check(string name, bool ok)
            {
                if (ok) pass++; else fail++;
                Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}: {name}");
            }
            try
            {
                foreach (string gun in new[] { "mp1", "MP1PowerSuit", "MP2DarkSuit" })
                {
                    Directory.CreateDirectory(Path.Combine(root, "guns", gun));
                    File.WriteAllBytes(Path.Combine(root, "guns", gun, "gun.bin"), new byte[] { 0 });
                }
                string settingsPath = Path.Combine(root, "recomp_settings.json");
                File.WriteAllText(settingsPath, "{ \"SamusSuit\": \"mp1\" }");
                RecompSettings settings = RecompSettings.Load(settingsPath);
                Check($"a saved \"mp1\" loads as {settings.SamusSuit}", settings.SamusSuit == "MP1PowerSuit");
                settings.Save(settingsPath);
                Check("and saves back as MP1PowerSuit", File.ReadAllText(settingsPath).Contains("\"MP1PowerSuit\""));
                var menus = new RecompMenus(null!, null!, settings, settingsPath, modsDir: null);
                var suits = (List<string>)typeof(RecompMenus).GetField("_suits", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(menus)!;
                Console.WriteLine($"  suit row: {String.Join(", ", suits)}");
                Check("guns/mp1 isn't offered; P1 PWR, P2 DRK and ORIGINAL are",
                    !suits.Contains("mp1") && suits.Contains("MP1PowerSuit") && suits.Contains("MP2DarkSuit") && suits[^1] == "original");
                settings.SamusSuit = "MP2DarkSuit";
                Check("other suits are kept as set", settings.SamusSuit == "MP2DarkSuit");
            }
            finally
            {
                try { Directory.Delete(root, recursive: true); } catch { }
            }
            Console.WriteLine($"  {pass}/{pass + fail} passed");
        }
    }
}
