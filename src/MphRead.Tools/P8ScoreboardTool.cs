using System;
using System.IO;
using MphRead.Entities;
using MphRecomp.Campaign;
using MphRecomp.Multiplayer;

namespace MphRead
{
    // -p8scoreboard [seats=8] [mode=Battle] [arena=MP3 PROVING GROUND] [tag=]: the match's GAME OVER scoreboard (PlayerHud
    // DrawScoreboard, MatchState.Ending) with N seats (bots fill), software-rendered like -huddialog
    // -> extract_out/p8_results/scoreboard_<mode>_<seats>p[_tag].png (milestone P8: 5-8 rows)
    internal static class P8ScoreboardTool
    {
        public static void Run(string[] args)
        {
            if (!ToolPaths.UseDesktopExtraction())
            {
                return;
            }
            int seats = 8;
            GameMode mode = GameMode.Battle;
            string arena = "MP3 PROVING GROUND", tag = "";
            foreach (string arg in args[1..])
            {
                if (arg.StartsWith("seats=")) seats = Int32.Parse(arg[6..]);
                else if (arg.StartsWith("mode=")) mode = Enum.Parse<GameMode>(arg[5..], ignoreCase: true);
                else if (arg.StartsWith("arena=")) arena = arg[6..];
                else if (arg.StartsWith("tag=")) tag = "_" + arg[4..];
            }
            string outDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "extract_out", "p8_results"));
            Directory.CreateDirectory(outDir);
            MatchSettings settings = MatchSettings.Quick(mode, arena, Hunter.Samus, seats - 1, 2);
            settings.TimeLimitSeconds = 20;
            using CampaignHost host = CampaignHost.StartMatch(settings, collectDrawItems: true, viewWidth: 1920, viewHeight: 1080);
            var input = new CampaignInput { SelectWeapon = BeamType.None };
            int f = 0;
            for (; f < 60 * 60 && GameState.MatchState != MatchState.Ending; f++) host.Step(input);
            for (int i = 0; i < 30; i++) host.Step(input);
            string file = Path.Combine(outDir, $"scoreboard_{mode}_{seats}p{tag}.png");
            CampaignSim.RenderHud(host.Scene, 1920, 1080, file);
            Console.WriteLine($"  {mode} {seats} seats, ending at frame {f}, MaxPlayers {PlayerEntity.MaxPlayers}, {host.Scene.HudItemCount} HUD items -> {file}");
        }
    }
}
