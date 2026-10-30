using System;
using System.IO;
using MphRead.Entities;
using MphRecomp.Campaign;
using MphRecomp.Multiplayer;

namespace MphRead
{
    // -p8scoreboard [seats=8] [mode=Battle] [arena=MP3 PROVING GROUND] [tag=] [points=37,0,...] [ping=45,120,999,...] [deaths=...]: the match's GAME OVER scoreboard
    // (PlayerHud DrawScoreboard, MatchState.Ending) with N seats (bots fill), software-rendered like -huddialog; points= sets
    // each seat's points at game over (seat order; teams 2v2: even seats team 1) and prints the result order
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
            int[]? points = null, pings = null, deaths = null;
            foreach (string arg in args[1..])
            {
                if (arg.StartsWith("seats=")) seats = Int32.Parse(arg[6..]);
                else if (arg.StartsWith("mode=")) mode = Enum.Parse<GameMode>(arg[5..], ignoreCase: true);
                else if (arg.StartsWith("arena=")) arena = arg[6..];
                else if (arg.StartsWith("tag=")) tag = "_" + arg[4..];
                else if (arg.StartsWith("points=")) points = Array.ConvertAll(arg[7..].Split(','), Int32.Parse);
                else if (arg.StartsWith("ping=")) pings = Array.ConvertAll(arg[5..].Split(','), Int32.Parse); // per seat, ms (-1 none)
                else if (arg.StartsWith("deaths=")) deaths = Array.ConvertAll(arg[7..].Split(','), Int32.Parse);
            }
            string outDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "extract_out", "p8_results"));
            Directory.CreateDirectory(outDir);
            MatchSettings settings = MatchSettings.Quick(mode, arena, Hunter.Samus, seats - 1, 2);
            settings.TimeLimitSeconds = 20;
            using CampaignHost host = CampaignHost.StartMatch(settings, collectDrawItems: true, viewWidth: 1920, viewHeight: 1080);
            var input = new CampaignInput { SelectWeapon = BeamType.None };
            int f = 0;
            for (; f < 60 * 60 && GameState.MatchState != MatchState.Ending; f++) host.Step(input);
            if (points != null)
            {
                for (int i = 0; i < points.Length && i < seats; i++) GameState.Points[i] = points[i];
                GameState.UpdateState(voices: false);
            }
            for (int i = 0; i < 30; i++) host.Step(input);
            for (int i = 0; deaths != null && i < deaths.Length && i < seats; i++) GameState.Deaths[i] = deaths[i];
            for (int i = 0; pings != null && i < pings.Length && i < seats; i++) PlayerEntity.HostSeatPingMs[i] = pings[i];
            if (pings != null || deaths != null) host.Step(input);
            if (points != null)
            {
                Console.WriteLine("  result order: " + String.Join(" ", Array.ConvertAll(GameState.ResultSlots[..GameState.ActivePlayers],
                    s => $"seat{s}(team{PlayerEntity.Players[s].TeamIndex + 1},{GameState.Points[s]})")));
            }
            string file = Path.Combine(outDir, $"scoreboard_{mode}_{seats}p{tag}.png");
            CampaignSim.RenderHud(host.Scene, 1920, 1080, file);
            Console.WriteLine($"  {mode} {seats} seats, ending at frame {f}, MaxPlayers {PlayerEntity.MaxPlayers}, {host.Scene.HudItemCount} HUD items -> {file}");
        }
    }
}
