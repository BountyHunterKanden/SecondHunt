using System;
using System.Linq;
using MphRecomp.Arenas;
using MphRead.Entities;
using MphRead.Formats;
using OpenTK.Mathematics;

namespace MphRead
{
    // Prime 1 Main Quarry's crane (MainQuarryCrane.cs):
    //   -quarrycranetest import=<folder holding arenas/P1_MainQuarry>: a Battle from that import; Samus stands in the
    //       Control Tower booth facing its console (scan point 00020251, where the booth's cinematic camera stands) and
    //       fires the Power Beam at it. PASS = the panel's beam hit
    //       starts the crane, its first swing breaks the wall, and a later shot swings it back.
    public static class QuarryCraneTest
    {
        public static void Run(string[] args)
        {
            string? import = args.Skip(1).FirstOrDefault(a => a.StartsWith("import="))?[7..];
            if (import == null)
            {
                Console.WriteLine("usage: -quarrycranetest import=<folder>");
                return;
            }
            if (!ToolPaths.UseDesktopExtraction()) return;
            EchoesArena.ImportRoot = import;
            EchoesArena.EnsureRegistered();
            Console.WriteLine($"registered from the import: {EchoesArena.RegisterImported(import)}");
            EchoesArena.Def def = EchoesArena.Find("P1_MainQuarry")!;
            var settings = MphRecomp.Multiplayer.MatchSettings.Quick(GameMode.Battle, def.Room, Hunter.Samus, 1, 0);
            settings.TimeLimitSeconds = 600;
            using var host = MphRecomp.Campaign.CampaignHost.StartMatch(settings);
            MainQuarryCraneEntity? crane = null;
            MainQuarryPanelEntity? panel = null;
            foreach (EntityBase e in host.Scene.Entities)
            {
                if (e is MainQuarryCraneEntity c) crane = c;
                else if (e is MainQuarryPanelEntity p) panel = p;
            }
            Console.WriteLine($"crane {(crane != null ? "spawned" : "MISSING")}, panel {(panel != null ? "spawned" : "MISSING")}");
            if (crane == null || panel == null)
            {
                Console.WriteLine("FAIL");
                return;
            }
            PlayerEntity me = PlayerEntity.Players[0];
            float[] pc = crane.Spot.PanelCenter;
            var panelAt = new Vector3(pc[0], pc[1], pc[2]);
            // the booth floor 2.5 units back from the console (Prime 113.9, 39.43, 27.7)
            var stand = new Vector3(panelAt.X + 2.5f, panelAt.Y - 1.84f, panelAt.Z);
            Console.WriteLine($"console at {panelAt.X:0.##} {panelAt.Y:0.##} {panelAt.Z:0.##}");
            bool placed = false, wallBroke = false;
            int firstSwings = -1;
            long placedAt = 0, wallAt = -1;
            for (int f = 0; f < 60 * 30; f++)
            {
                var input = new MphRecomp.Campaign.CampaignInput { SelectWeapon = BeamType.None };
                if (!placed && me.LoadFlags.TestFlag(LoadFlags.Spawned) && me.Health > 0 && host.Frame > 60)
                {
                    Vector3 to = panelAt - stand;
                    me.Teleport(stand, to.WithY(0).Normalized(), host.Scene.GetNodeRefContaining(stand));
                    placed = true;
                    placedAt = host.Frame;
                }
                if (placed)
                {
                    long t = host.Frame - placedAt;
                    if (t == 15)
                    {
                        // what a beam from the gun meets on its way to the console
                        Vector3 from = me.Position.AddY(1.2f);
                        var res = new CollisionResult();
                        bool hit = CollisionDetection.CheckBetweenPoints(from, panelAt + (panelAt - from).Normalized() * 2, TestFlags.Beams, host.Scene, ref res);
                        Console.WriteLine($"stood at {me.Position.X:0.##} {me.Position.Y:0.##} {me.Position.Z:0.##}; ray to the console: "
                            + (hit ? $"hits {(res.EntityCollision?.Entity is MainQuarryPanelEntity ? "the console box" : res.EntityCollision?.Entity?.GetType().Name ?? "the room")} at {res.Position.X:0.##} {res.Position.Y:0.##} {res.Position.Z:0.##}" : "nothing"));
                    }
                    // shoot a burst now, then again once the first swing is over
                    if ((t > 20 && t < 60 || t > 900 && t < 940) && t % 8 == 0) input.Buttons = MphRecomp.Campaign.CampaignButtons.Shoot;
                    if (firstSwings < 0 && crane.Moving) { firstSwings = crane.Swings; Console.WriteLine($"frame {host.Frame}: crane moving (swings {crane.Swings})"); }
                    if (!wallBroke && !crane.WallUp) { wallBroke = true; wallAt = host.Frame; Console.WriteLine($"frame {host.Frame}: wall broken"); }
                }
                host.Step(input);
            }
            Console.WriteLine($"end: swings {crane.Swings}, wall up {crane.WallUp}, moving {crane.Moving}");
            bool pass = firstSwings >= 0 && wallBroke && crane.Swings >= 2;
            Console.WriteLine(pass ? "PASS" : "FAIL");
        }
    }
}
