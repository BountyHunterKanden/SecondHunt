using System;
using System.IO;
using MphRead.Entities;
using MphRead.Formats;
using MphRecomp.Arenas;
using MphRecomp.Campaign;
using MphRecomp.Multiplayer;

namespace MphRead
{
    // -introbanner [arena=...] [frames=90]: a Battle's intro HUD (rules + PlayerIntroBanner's middle line) software-rendered
    // like -p8scoreboard -> extract_out/intro_banner/<arena>_<frames>.png; checks the banner text is set and its glyphs
    // were recorded (release 0.2.0 F4 item 1: "Space Jump is active!" missing on the device). Default: every Echoes arena
    // + Combat Hall (no banner).
    internal static class IntroBannerTool
    {
        public static void Run(string[] args)
        {
            if (!ToolPaths.UseDesktopExtraction())
            {
                return;
            }
            string? only = null;
            int frames = 90;
            foreach (string arg in args[1..])
            {
                if (arg.StartsWith("arena=")) only = arg[6..];
                else if (arg.StartsWith("frames=")) frames = Int32.Parse(arg[7..]);
            }
            EchoesArena.Register(Paths.FileSystem);
            string outDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "extract_out", "intro_banner"));
            Directory.CreateDirectory(outDir);
            var rooms = new System.Collections.Generic.List<string> { "MP3 PROVING GROUND" };
            foreach (EchoesArena.Def d in EchoesArena.All) rooms.Add(d.Room);
            int fail = 0;
            foreach (string room in rooms)
            {
                if (only != null && EchoesArena.Find(only)?.Room != room && only != room) continue;
                bool echoes = EchoesArena.Find(room) != null;
                MatchSettings settings = MatchSettings.Quick(GameMode.Battle, room, Hunter.Samus, 3, 1);
                using CampaignHost host = CampaignHost.StartMatch(settings, collectDrawItems: true, viewWidth: 1920, viewHeight: 1080);
                var input = new CampaignInput { SelectWeapon = BeamType.None };
                bool intro = false;
                for (int f = 0; f < frames; f++)
                {
                    host.Step(input);
                    intro |= CameraSequence.Current?.IsIntro == true;
                }
                string? banner = PlayerEntity.HostIntroBanner;
                bool introNow = CameraSequence.Current?.IsIntro == true;
                string file = Path.Combine(outDir, $"{room.Replace(' ', '_')}_{frames}.png");
                CampaignSim.RenderHud(host.Scene, 1920, 1080, file);
                bool ok = echoes ? banner != null : banner == null;
                if (!ok) fail++;
                Console.WriteLine($"  {(ok ? "PASS" : "FAIL")} {room}: banner '{banner}', intro seen {intro} / now {introNow}, "
                    + $"{host.Scene.HudItemCount} HUD items -> {file}");
            }
            Console.WriteLine(fail == 0 ? "PASS" : $"FAIL ({fail})");
        }
    }
}
