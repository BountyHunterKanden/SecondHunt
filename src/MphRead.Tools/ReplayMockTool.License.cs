using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MphRecomp.Frontend;
using MphRecomp.Replay;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace MphRead
{
    internal static partial class ReplayMockTool
    {
        // -replaymock licensecheck [w=1920] [h=1080]: a network match's replay in REPLAYS (O10, Replays track), on a scratch copy of
        // the sample replays with the newest one made an online match's (seat 1 = KANDEN#4412, a friend code and a license
        // snapshot; seat 0 = this device). Renders the list, the details page and the seat's license as it was (mock_art/
        // licensecheck_*.png, to judge by eye beside the V_C mockup) and drives it: tap the player (on the drawn screen) -> the replay's license page,
        // R turns its tab, SEE CURRENT -> ONLINE PLAY's license view, B -> back to the replay's, B -> the details page. The
        // server is pointed at a closed local port (nothing leaves this PC).
        private static void LicenseCheck()
        {
            string src = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "testdata",
                "replays"));
            string dir = Path.Combine(Path.GetTempPath(), $"replay_licensecheck_{Environment.ProcessId}");
            foreach (string f in Directory.GetFiles(src, "*", SearchOption.AllDirectories))
            {
                string to = Path.Combine(dir, Path.GetRelativePath(src, f));
                Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                File.Copy(f, to);
            }
            string newest = Path.Combine(dir, "replays", "20261008-005600 Battle Combat Hall.mphreplay");
            ReplayFile file = ReplayFile.Load(newest);
            file.Net = "online";
            file.Identities.Clear();
            file.Identities.Add(new ReplaySeatIdentity(file.Settings.LocalSeat, "1111-2222-3333", "SAMUS", null, 1203));
            var kanden = new HunterLicense
            {
                RankPoints = 9000, WifiWins = 230, OnlineMatches = 480, BipedKills = 1200, AltFormKills = 300, HeadshotKills = 410,
                WifiBestWinStreak = 7, WifiBestKillStreak = 12, FavoriteHunter = 1, FavoriteWeapon = 3, FavoriteMode = 0, LuckyArena = 1,
            };
            int other = file.Settings.LocalSeat == 1 ? 0 : 1;
            file.Settings.Players[other].Bot = false;
            file.Settings.Players[other].Nickname = "KANDEN";
            file.Identities.Add(new ReplaySeatIdentity(other, "4444-5555-6666", "KANDEN", RecompMenus.OnlineLicenseJson(kanden), 4412));
            using (FileStream fs = File.Create(newest)) file.Write(fs);

            int w = 1920, h = 1080;
            RecompMenus.OnlineServerOverride = "ws://127.0.0.1:9/v1/ws";
            FrontendSession s = FrontendSession.Load(Paths.FileSystem, settingsPath: Path.Combine(dir, "recomp_settings.json"));
            s.Log = _ => { };
            var played = new List<int>();
            s.PlaySound = id => played.Add(id);
            void Run(int frames)
            {
                for (int i = 0; i < frames; i++)
                {
                    s.Build(w, h);
                    s.Tick();
                }
            }
            void Tap(int call)
            {
                MenuPage page = s.Menu.Page!;
                for (int i = 0; i < page.Items.Count; i++)
                {
                    foreach (MenuAction a in page.Items[i].Actions)
                    {
                        if (!a.Calls.Any(c => c.B == call) || !MenuEngine.TryRect(a, page.Items[i], out float x0, out float y0, out float x1, out float y1)) continue;
                        s.TouchDs((x0 + x1) / 2, 192 - (y0 + y1) / 2);
                        Run(6);
                        return;
                    }
                }
                Console.WriteLine($"FAIL no key with call {call} on page {page.Index}");
            }
            void Shot(string name)
            {
                s.Build(w, h);
                using Image<Rgba32> img = FrontendTool.RasterCanvas(s.DrawList, w, h);
                string path = Path.Combine(OutDir(), $"licensecheck_{name}.png");
                img.SaveAsPng(path);
                Console.WriteLine("-> " + path);
            }
            int fails = 0;
            void Check(bool ok, string what)
            {
                Console.WriteLine($"{(ok ? "PASS" : "FAIL")} {what}");
                if (!ok) fails++;
            }
            ReplayInfo? info = ReplayLibrary.List(dir).FirstOrDefault(r => r.Path == newest);
            Check(info != null && info.Net == "online" && info.Identity(other)?.Tag == 4412, $"the replay reads back as an online match's (net '{info?.Net}', seat {other} {info?.Identity(other)?.Name})");

            s.Start(s.Recomp.ReplaysPage);
            Run(60);
            Shot("list");
            int list = s.Menu.Page!.Index;
            s.Press(MenuKeys.A);
            Run(40);
            Check(s.Menu.Page?.Index == RecompMenus.ReplaysDetailPage, $"A on the first row opens its details (page {s.Menu.Page?.Index})");
            Run(20);
            Shot("details");
            // a tap on the screen as drawn (the layout's regions, as on a device): seat 1's icon (1920 x 1080: seat k at x 205 + 430 k)
            s.TouchCanvas((205 + 430 * other) * w / 1920f, 800 * h / 1080f);
            Run(50);
            Check(s.Menu.Page?.Index == RecompMenus.ReplayLicensePage && s.Recomp.ReplayLicenseSeat == other,
                $"tapping KANDEN opens the replay's license (page {s.Menu.Page?.Index}, seat {s.Recomp.ReplayLicenseSeat})");
            Run(30);
            Shot("license");
            played.Clear();
            s.Press(MenuKeys.R);
            Run(20);
            int keySound = played.Count > 0 ? played[0] : 0;
            Shot("license_tab2");
            Check(s.Recomp.ReplayLicenseTab == 1, $"R turns to the second tab (tab {s.Recomp.ReplayLicenseTab})");
            // a tap on a tab picks it (owner 2026-10-24: as the main menu's card), a second and third tap leave it picked
            foreach (int pick in new[] { 4, 0, 3, 3, 3, 2 })
            {
                played.Clear();
                Tap(2163 + pick);
                Check(s.Recomp.ReplayLicenseTab == pick, $"a tap on tab {pick + 1} (tab {s.Recomp.ReplayLicenseTab})");
                if (pick == 4) Check(played.Count > 0 && keySound != 0 && played.Contains(keySound), $"R makes the same sound as a tab tap (R sfx 0x{keySound:X}, tap {string.Join(",", played.Select(x => x.ToString("X")))})");
            }
            Shot("license_tab3");
            // and a tap on the screen as drawn (1920 x 1080: the strip under the card, tab k at x 348 + 69.4 k, y 545..590)
            foreach (int pick in new[] { 4, 0, 2, 1 })
            {
                s.TouchCanvas((348 + 69.4f * pick + 34) * w / 1920f, 567 * h / 1080f);
                Run(6);
                Check(s.Recomp.ReplayLicenseTab == pick, $"a tap on the drawn tab {pick + 1} (tab {s.Recomp.ReplayLicenseTab})");
            }
            s.Press(MenuKeys.L);
            Run(10);
            Check(s.Recomp.ReplayLicenseTab == 0, $"L after the taps: the tab before (tab {s.Recomp.ReplayLicenseTab})");
            Tap(2157); // SEE CURRENT
            Run(40);
            Check(s.Menu.Page?.Index == RecompMenus.OnlineLicensePage && s.Recomp.OnlineLicenseCard?.Code == "4444-5555-6666",
                $"SEE CURRENT opens ONLINE PLAY's license view for that friend code (page {s.Menu.Page?.Index}, {s.Recomp.OnlineLicenseCard?.Code})");
            s.Press(MenuKeys.B);
            Run(40);
            Check(s.Menu.Page?.Index == RecompMenus.ReplayLicensePage, $"B comes back to the replay's license (page {s.Menu.Page?.Index})");
            s.Press(MenuKeys.B);
            Run(40);
            Check(s.Menu.Page?.Index == RecompMenus.ReplaysDetailPage, $"B again: the details page (page {s.Menu.Page?.Index})");
            // this device's own seat: the license opens, without ADD FRIEND / ADD RIVAL
            s.TouchCanvas((205 + 430 * file.Settings.LocalSeat) * w / 1920f, 800 * h / 1080f); // (its icon: in the back arrow's corner's reach)
            Run(50);
            Check(s.Menu.Page?.Index == RecompMenus.ReplayLicensePage, $"its own seat opens too (page {s.Menu.Page?.Index})");
            Shot("license_own");
            s.Press(MenuKeys.B);
            Run(30);
            s.Press(MenuKeys.B);
            Run(40);
            Check(s.Menu.Page?.Index == list, $"B: the list (page {s.Menu.Page?.Index})");
            RecompMenus.OnlineServerOverride = null;
            Directory.Delete(dir, recursive: true);
            Console.WriteLine(fails == 0 ? "PASS licensecheck" : $"FAIL licensecheck ({fails})");
        }
    }
}
