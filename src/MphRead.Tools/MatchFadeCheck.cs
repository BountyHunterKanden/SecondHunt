using System;
using MphRead.Entities;
using MphRead.Formats;
using MphRecomp.Campaign;
using MphRecomp.Multiplayer;

namespace MphRead
{
    // -matchfade [arena=...] [frames=40]: the screen fade at the start of a Battle, frame by frame (C1 2026-10-24: vanilla
    // fades the arena in from black over ~28 frames as the intro views start; MphRead sets a FadeInBlack there, which
    // HostFadeAmount must actually report so the host draws it). FAIL when the first frame isn't dark or nothing fades.
    internal static class MatchFadeCheck
    {
        public static void Run(string[] args)
        {
            if (!ToolPaths.UseDesktopExtraction())
            {
                return;
            }
            string room = "MP3 PROVING GROUND";
            int frames = 40;
            foreach (string arg in args[1..])
            {
                if (arg.StartsWith("arena=")) room = arg[6..];
                else if (arg.StartsWith("frames=")) frames = Int32.Parse(arg[7..]);
            }
            MatchSettings settings = MatchSettings.Quick(GameMode.Battle, room, Hunter.Samus, 3, 1);
            using CampaignHost host = CampaignHost.StartMatch(settings);
            var input = new CampaignInput { SelectWeapon = BeamType.None };
            float first = -1, last = -1;
            int fadeFrames = 0;
            for (int f = 0; f < frames; f++)
            {
                host.Step(input);
                Scene scene = host.Scene;
                float amount = scene.HostFadeAmount;
                if (f == 0) first = amount;
                if (amount > 0) fadeFrames = f + 1;
                last = amount;
                Console.WriteLine($"  f{f,3} fade {scene.FadeType,-12} drawn {amount:0.00} camera {scene.CameraMode,-8} "
                    + $"intro {CameraSequence.Current?.IsIntro == true} active {PlayerEntity.Main.LoadFlags.TestFlag(LoadFlags.Active)} "
                    + $"spawned {PlayerEntity.Main.LoadFlags.TestFlag(LoadFlags.Spawned)}");
            }
            bool ok = first >= 0.9f && last == 0 && fadeFrames >= 10;
            Console.WriteLine($"{(ok ? "PASS" : "FAIL")}: {room}: first frame {first:0.00} dark, drawn fade lasts {fadeFrames} frames");
        }
    }
}
