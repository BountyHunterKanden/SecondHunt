using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using MphRecomp.Frontend;
using MphRecomp.Update;

// -updatelaunchtest [feed folder]: the launch update check (owner 2026-10-11; FrontendSession.TickLaunchCheck) driven on
// a real session against the test-signed 0.1.9-beta feed (handoffs/updater_2026-10-06-files/feed_file, file:// URLs):
// offered once on the main menu, YES answers through the host, silent when up to date / switched off / a dev build
// without a feed, waits off the main menu, and doesn't come back on a later session in the same run.
namespace MphRead
{
    public static class UpdateLaunchTest
    {
        private static int _pass, _fail;

        public static void Run(string[] args)
        {
            string feedDir = Path.GetFullPath(args.Length > 1 ? args[1] : Path.Combine(AppContext.BaseDirectory,
                "..", "..", "..", "..", "..", "handoffs", "updater_2026-10-06-files", "feed_file"));
            if (!ToolPaths.UseDesktopExtraction()) return;
            string tmp = Path.Combine(Path.GetTempPath(), "updatelaunchtest");
            Directory.CreateDirectory(tmp);
            string feedFile = Path.Combine(tmp, "update_feed.txt");
            File.WriteAllText(feedFile, new Uri(feedDir + Path.DirectorySeparatorChar).AbsoluteUri + "\n");
            string settings = Path.Combine(tmp, "recomp_settings.json");
            UpdateHost.Platform = UpdatePlatform.Windows;
            UpdateHost.Installer = null; // YES = open the release page (the same box an installing host asks first)
            MphRecomp.Config.RecompSettings.PublicBuild = false;
            Console.WriteLine($"feed {feedDir}");

            // 1. older version, feed set: offered once the main menu has settled; YES opens 0.1.9-beta's page
            Case("offer on the main menu", "0.1.0", feedFile, settings, on: true, startPage: 18, (s, log, urls) =>
            {
                bool opened = TickUntil(s, () => s.Popup!.IsOpen, 600);
                Check("offer box opened", opened);
                Check("logged the offer", log.Exists(l => l.Contains("offering 0.1.9-beta")));
                Shot(s, "update_launch_offer");
                s.Press(MenuKeys.A); // focuses YES
                Tick(s, 2);
                s.Press(MenuKeys.A); // presses it
                Tick(s, 60);
                Check("YES opened the release page", urls.Count == 1 && urls[0].EndsWith("/releases/tag/v0.1.9-beta"));
                // a later session in the same run doesn't ask again
                FrontendSession again = Session(settings, 18, log);
                Tick(again, 300);
                Check("not asked again in the same run", !again.Popup!.IsOpen);
            });
            // 2. up to date: nothing shown
            Case("up to date is silent", "0.2.0", feedFile, settings, on: true, startPage: 18, (s, log, urls) =>
            {
                Tick(s, 300);
                Check("no box", !s.Popup!.IsOpen);
                Check("logged UpToDate", log.Exists(l => l.Contains("launch check: UpToDate")));
            });
            // 3. UPDATE ALERTS off: no check at all
            Case("switched off", "0.1.0", feedFile, settings, on: false, startPage: 18, (s, log, urls) =>
            {
                Tick(s, 300);
                Check("no box", !s.Popup!.IsOpen);
                Check("never checked", !log.Exists(l => l.Contains("launch check")));
            });
            // 4. a dev build without a test feed never asks GitHub on its own
            Case("dev build without a feed", "0.1.0", null, settings, on: true, startPage: 18, (s, log, urls) =>
            {
                Tick(s, 300);
                Check("no box", !s.Popup!.IsOpen);
                Check("never checked", !log.Exists(l => l.Contains("launch check")));
            });
            // 5. off the main menu (OPTIONS): the offer waits, then opens once the player is back on the main menu
            Case("waits for the main menu", "0.1.0", feedFile, settings, on: true, startPage: 21, (s, log, urls) =>
            {
                Tick(s, 300);
                Check("no box on another page", !s.Popup!.IsOpen && log.Exists(l => l.Contains("launch check")));
                s.Menu.Enter(18);
                bool opened = TickUntil(s, () => s.Popup.IsOpen, 300);
                Check("box opened on the main menu", opened);
                s.Press(MenuKeys.B); // NO
                Tick(s, 60);
                Check("NO opened nothing", urls.Count == 0 && !s.Popup.IsOpen);
            });
            // 6. a check by hand (UPDATES CHECK) before the launch check answers stands in for it
            Case("CHECK pressed first", "0.1.0", feedFile, settings, on: true, startPage: 21, (s, log, urls) =>
            {
                typeof(FrontendSession).GetMethod("CheckForUpdates", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(s, null);
                TickUntil(s, () => log.Exists(l => l.StartsWith("update check: Available")), 600);
                s.Popup!.Press(MenuKeys.B);
                Tick(s, 60);
                s.Menu.Enter(18);
                Tick(s, 300);
                Check("launch offer not repeated", !s.Popup.IsOpen && !log.Exists(l => l.Contains("offering")));
            });

            // a picture of RECOMP SETTINGS' last page at its UPDATE ALERTS row (L wraps to it, Up from the top to the bottom)
            ResetLaunchCheck();
            UpdateHost.DevFeedFile = null;
            FrontendSession page = Session(settings, 18, new List<string>());
            page.Menu.Enter(page.Recomp.SettingsPage);
            Tick(page, 60);
            page.Press(MenuKeys.L); // the last settings page
            Tick(page, 40);
            for (int i = 0; i < 2; i++)
            {
                page.Press(MenuKeys.Up);
                Tick(page, 20);
            }
            Shot(page, "update_alerts_row");

            Console.WriteLine($"updatelaunchtest: {_pass} passed, {_fail} failed");
            if (_fail > 0) Environment.ExitCode = 1;
        }

        private static void Case(string name, string version, string? feedFile, string settingsPath, bool on, int startPage,
            Action<FrontendSession, List<string>, List<string>> body)
        {
            Console.WriteLine("-- " + name);
            ResetLaunchCheck();
            UpdateHost.AppVersion = version;
            UpdateHost.DevFeedFile = feedFile;
            File.WriteAllText(settingsPath, "{ \"CheckUpdatesAtLaunch\": " + (on ? "true" : "false") + " }");
            var log = new List<string>();
            var urls = new List<string>();
            UpdateHost.OpenUrl = urls.Add;
            body(Session(settingsPath, startPage, log), log, urls);
        }

        private static FrontendSession Session(string settingsPath, int page, List<string> log)
        {
            FrontendSession s = FrontendSession.Load(Paths.FileSystem, settingsPath: settingsPath);
            s.Log = log.Add;
            s.Start(page);
            return s;
        }

        // the launch check is once per process: a fresh run per case
        private static void ResetLaunchCheck()
        {
            const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Static;
            typeof(FrontendSession).GetField("_launchCheck", flags)!.SetValue(null, null);
            typeof(FrontendSession).GetField("_launchDone", flags)!.SetValue(null, false);
        }

        private static void Tick(FrontendSession s, int ticks)
        {
            for (int i = 0; i < ticks; i++)
            {
                s.Build(640, 360);
                s.Tick();
                System.Threading.Thread.Sleep(1); // the check runs on the thread pool
            }
        }

        private static bool TickUntil(FrontendSession s, Func<bool> done, int max)
        {
            for (int i = 0; i < max; i++)
            {
                if (done()) return true;
                Tick(s, 1);
            }
            return done();
        }

        private static void Shot(FrontendSession s, string name)
        {
            s.Build(1280, 720);
            using var img = FrontendTool.RasterCanvas(s.DrawList, 1280, 720);
            string path = Path.Combine(FrontendTool.OutDir(), name + ".png");
            SixLabors.ImageSharp.ImageExtensions.SaveAsPng(img, path);
            Console.WriteLine("   shot " + path);
        }

        private static void Check(string what, bool ok)
        {
            if (ok) _pass++;
            else _fail++;
            Console.WriteLine($"   {(ok ? "PASS" : "FAIL")} {what}");
        }
    }
}
