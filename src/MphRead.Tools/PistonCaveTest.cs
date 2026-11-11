using System;
using System.Linq;
using MphRead.Entities;
using MphRecomp.Campaign;

namespace MphRead
{
    // -pistoncave: Piston Cave (UNIT1_C5, room 38) morph-ball snag on the double piston and its looping sounds.
    internal static class PistonCaveTest
    {
        public static void Run(string[] args)
        {
            if (!ToolPaths.UseDesktopExtraction())
            {
                return;
            }
            using CampaignHost host = CampaignHost.Start("UNIT1_C5", arriving: false);
            Scene scene = host.Scene;
            for (int i = 0; i < 60; i++)
            {
                host.Step(default);
            }
            Console.WriteLine($"player {host.Player.Position}");
            foreach (PlatformEntity p in scene.GetPlatformEntities())
            {
                PlatformEntityData d = p.Data;
                Console.WriteLine($"plat #{p.Id} model {d.ModelId} {Metadata.GetPlatformById((int)d.ModelId)?.Name} pos {p.Position} "
                    + $"positions {d.PositionCount} fwd {d.ForwardSpeed.FloatValue} back {d.BackwardSpeed.FloatValue} delay {d.Delay} "
                    + $"flags {p.Flags} sfx {String.Join(",", Enumerable.Range(0, 4).Select(c => $"0x{Metadata.PlatformSfx[d.ModelId, c]:X}"))}");
            }
        }
    }
}
