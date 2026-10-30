using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MphRecomp.Frontend;

// -bootskip: SETTINGS > APP > STARTUP (owner 2026-10-29) on throwaway profiles. For each SKIP BOOT LOGOS / SKIP INTRO
// pair, boots from page 0 and checks the first pages and the intro movie request (off: page 0 after the white; logos
// only: page 2, no 0 or 1; both: page 13, no movie; intro alone does nothing), then that the SKIP INTRO row (and its
// hints) is listed only while SKIP BOOT LOGOS is on
namespace MphRead
{
    public static class BootSkipTest
    {
        public static void Run(string[] args)
        {
            if (!ToolPaths.UseDesktopExtraction()) return;
            int fails = 0;
            void Check(bool ok, string what)
            {
                Console.WriteLine($"{(ok ? "ok  " : "FAIL")} {what}");
                if (!ok) fails++;
            }
            foreach ((bool logos, bool intro) in new[] { (false, false), (true, false), (true, true), (false, true) })
            {
                string dir = Path.Combine(Path.GetTempPath(), $"bootskip_{Environment.ProcessId}_{logos}_{intro}");
                Directory.CreateDirectory(dir);
                string settings = Path.Combine(dir, "recomp_settings.json");
                File.WriteAllText(settings, $"{{ \"SkipBootLogos\": {(logos ? "true" : "false")}, \"SkipIntro\": {(intro ? "true" : "false")} }}");
                FrontendSession s = FrontendSession.Load(Paths.FileSystem, settingsPath: settings, savesDir: Path.Combine(dir, "saves"));
                var pages = new List<int>();
                bool movie = false;
                s.Log = line => { if (line.StartsWith("page ") && int.TryParse(line[5..], out int p)) pages.Add(p); };
                s.Request = r => { if (r.Kind == FrontendRequestKind.PlayMovie) movie = true; };
                s.Start(0);
                for (int i = 0; i < 60; i++)
                {
                    s.Build(640, 360);
                    s.Tick();
                }
                string name = $"logos {(logos ? "ON" : "OFF")} intro {(intro ? "ON" : "OFF")}";
                Console.WriteLine($"{name}: pages {string.Join(",", pages)}{(movie ? ", movie" : "")}");
                int first = logos ? (intro ? 13 : 2) : 0;
                Check(pages.Count > 0 && pages[0] == first, $"{name}: first page {first}");
                if (logos) Check(!pages.Contains(0) && !pages.Contains(1), $"{name}: no NINTENDO/ESRB pages");
                Check(!movie, $"{name}: no movie in the first 2 s");
                List<string> hints = s.Recomp.HintDump();
                List<string> introRows = hints.Where(h => h.Contains("|skip intro|")).ToList();
                foreach (string h in hints.Where(h => h.Contains("|skip boot logos|") || h.Contains("|skip intro|"))) Console.WriteLine("  " + h);
                Check(hints.Count(h => h.Contains("|skip boot logos|") && !h.EndsWith('|')) == 2, $"{name}: SKIP BOOT LOGOS row, 2 values with hints");
                Check(logos ? introRows.Count == 2 && introRows.All(h => !h.EndsWith('|')) : introRows.Count == 0,
                    $"{name}: SKIP INTRO row {(logos ? "shown, 2 values with hints" : "hidden")}");
                try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
            }
            Console.WriteLine(fails == 0 ? "PASS bootskip" : $"FAIL bootskip ({fails})");
            if (fails > 0) Environment.ExitCode = 1;
        }
    }
}
