using System;
using System.IO;
using System.Linq;
using System.Reflection;
using MphRead.Entities;
using MphRead.Formats;
using MphRecomp.Campaign;
using MphRecomp.Multiplayer;

namespace MphRead
{
    // -wave1hud out=DIR [arena=MP3 PROVING GROUND] [w=1920] [h=1080] [frames=240] [hunters=Samus,Spire] [morph]: every hunter's match
    // HUD recorded headless and software-rendered at a widescreen size (wave 1 fixes, 2026-11-07: the locked target's name
    // above the portrait, Spire's weapon box, stretched text) -> DIR/wave1hud_<hunter>.png, with a bot locked as the target.
    internal static class Wave1HudTool
    {
        public static void Run(string[] args)
        {
            if (!ToolPaths.UseDesktopExtraction())
            {
                return;
            }
            string arena = "MP3 PROVING GROUND", outDir = ".";
            int w = 1920, h = 1080, frames = 240;
            GameMode mode = GameMode.Battle;
            BeamType weapon = BeamType.None;
            bool morph = false;
            Hunter[] hunters = { Hunter.Samus, Hunter.Kanden, Hunter.Trace, Hunter.Sylux, Hunter.Noxus, Hunter.Spire, Hunter.Weavel };
            foreach (string arg in args)
            {
                if (arg.StartsWith("out=")) outDir = arg[4..];
                else if (arg.StartsWith("arena=")) arena = arg[6..];
                else if (arg.StartsWith("w=")) w = Int32.Parse(arg[2..]);
                else if (arg.StartsWith("h=")) h = Int32.Parse(arg[2..]);
                else if (arg.StartsWith("frames=")) frames = Int32.Parse(arg[7..]);
                else if (arg.StartsWith("mode=")) mode = Enum.Parse<GameMode>(arg[5..], true);
                else if (arg.StartsWith("weapon=")) weapon = Enum.Parse<BeamType>(arg[7..], true);
                else if (arg == "morph") morph = true;
                else if (arg.StartsWith("hunters=")) hunters = arg[8..].Split(',').Select(s => Enum.Parse<Hunter>(s, true)).ToArray();
            }
            Directory.CreateDirectory(outDir);
            FieldInfo index = typeof(PlayerEntity).GetField("_opponentIndex", BindingFlags.NonPublic | BindingFlags.Instance)!;
            FieldInfo timer = typeof(PlayerEntity).GetField("_opponentHealthbarTimer", BindingFlags.NonPublic | BindingFlags.Instance)!;
            foreach (Hunter hunter in hunters)
            {
                MatchSettings settings = MatchSettings.Quick(mode, arena, hunter, 3, 1);
                using CampaignHost host = CampaignHost.StartMatch(settings, collectDrawItems: true, viewWidth: w, viewHeight: h);
                var input = new CampaignInput { SelectWeapon = BeamType.None };
                int f = 0;
                for (; f < 1200 && (f < 60 || CameraSequence.Current?.IsIntro == true); f++)
                {
                    input.Buttons = f % 20 < 2 ? CampaignButtons.Shoot : CampaignButtons.None;
                    host.Step(input);
                }
                input.Buttons = CampaignButtons.None;
                for (f = 0; f < frames; f++)
                {
                    input.SelectWeapon = f == 10 ? weapon : BeamType.None;
                    input.Buttons = morph && f == 20 ? CampaignButtons.Morph : CampaignButtons.None;
                    host.Step(input);
                }
                PlayerEntity me = host.Player;
                PlayerEntity? bot = null;
                foreach (PlayerEntity p in host.Scene.GetPlayerEntities())
                {
                    if (p != me && bot == null) bot = p;
                }
                if (bot != null)
                {
                    index.SetValue(me, bot.SlotIndex);
                    timer.SetValue(me, 2f);
                    host.Step(input);
                }
                string file = Path.Combine(outDir, $"wave1hud_{hunter}_{mode}{(morph ? "_alt" : "")}.png");
                CampaignSim.RenderHud(host.Scene, w, h, file);
                Console.WriteLine($"  {hunter}: {host.Scene.HudItemCount} HUD items -> {file}");
            }
        }
    }
}
