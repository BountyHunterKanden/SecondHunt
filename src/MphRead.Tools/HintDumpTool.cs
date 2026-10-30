using System;
using System.IO;
using MphRecomp.Frontend;

// -hintdump: SECOND HUNT SETTINGS' rows on a new profile in a temp folder, each stepped through its values, with the hint
// each value shows (F2: the hint describes only the value the row is on); "section|label|value|hint" per line, and a
// FAIL for any value with no hint
namespace MphRead
{
    public static class HintDumpTool
    {
        public static void Run(string[] args)
        {
            if (!ToolPaths.UseDesktopExtraction()) return;
            string dir = Path.Combine(Path.GetTempPath(), $"hintdump_{Environment.ProcessId}");
            Directory.CreateDirectory(dir);
            FrontendSession s = FrontendSession.Load(Paths.FileSystem, settingsPath: Path.Combine(dir, "recomp_settings.json"),
                savesDir: Path.Combine(dir, "saves"));
            s.Start(18);
            for (int i = 0; i < 30; i++)
            {
                s.Build(640, 360);
                s.Tick();
            }
            int fails = 0;
            foreach (string line in s.Recomp.HintDump())
            {
                Console.WriteLine(line);
                if (line.EndsWith('|')) fails++;
            }
            Console.WriteLine(fails == 0 ? "PASS hintdump" : $"FAIL hintdump ({fails} values without a hint)");
            if (fails > 0) Environment.ExitCode = 1;
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }
}
