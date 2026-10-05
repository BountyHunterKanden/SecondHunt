using System;
using System.Collections.Generic;
using System.Linq;
using MphRead.Entities;
using MphRead.Effects;
using MphRead.Formats;
using MphRecomp.Campaign;

namespace MphRead
{
    // -fecockpitjets [room]: the cockpit view hides the gunship (CampaignShip.StartCockpitView: the SamusShip platform's
    // Hidden), and its nozzle jets must go with it (owner queue #16: thruster flames with no ship during a revisit's
    // landing). Loads a landing room as the cockpit view does and counts the frame's draw items by type with the ship
    // shown, hidden, and shown again.
    internal static class CockpitJetsTest
    {
        public static void Run(string[] args)
        {
            if (!ToolPaths.UseDesktopExtraction()) return;
            string room = args.Length >= 2 ? args[1] : "UNIT2_LAND";
            var story = new StorySave();
            story.SetVisitedRoom(Metadata.GetRoomByName(room).Item1!.Id);
            using CampaignHost host = CampaignHost.Start(room, save: story, collectDrawItems: true, arriving: false, holdSpawn: true);
            PlatformEntity? ship = null;
            foreach (PlatformEntity p in host.Scene.GetPlatformEntities())
            {
                if (p.Flags.TestFlag(PlatformFlags.SamusShip)) ship = p;
            }
            if (ship == null)
            {
                Console.WriteLine($"  FAIL no gunship platform in {room}");
                return;
            }
            Dictionary<RenderItemType, int> Count(int frames)
            {
                for (int i = 0; i < frames; i++) host.Step(default);
                return host.Scene.OpaqueItems.Concat(host.Scene.DecalItems).Concat(host.Scene.TranslucentItems)
                    .GroupBy(r => r.Type).ToDictionary(g => g.Key, g => g.Count());
            }
            string Fmt(Dictionary<RenderItemType, int> c) => string.Join(", ", c.OrderBy(k => k.Key).Select(k => $"{k.Key} {k.Value}"));
            int Fx(Dictionary<RenderItemType, int> c) => c.Where(k => k.Key != RenderItemType.Mesh).Sum(k => k.Value);
            // the ship's own nozzle jets: elements with DrawEnabled (room effects such as Alinos' don't count)
            var effects = (List<EffectEntry?>)typeof(PlatformEntity).GetField("_effects",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(ship)!;
            int Jets() => effects.Where(e => e != null).Sum(e => e!.Elements.Count(el => el.Flags.TestFlag(EffElemFlags.DrawEnabled)));
            Dictionary<RenderItemType, int> shown = Count(60);
            int jetsShown = Jets();
            ship.Hidden = true;
            Dictionary<RenderItemType, int> hidden = Count(30);
            int jetsHidden = Jets();
            ship.Hidden = false;
            Dictionary<RenderItemType, int> again = Count(30);
            int jetsAgain = Jets();
            Console.WriteLine($"  {room}: ship shown  {Fmt(shown)}");
            Console.WriteLine($"  {room}: ship hidden {Fmt(hidden)}");
            Console.WriteLine($"  {room}: shown again {Fmt(again)}");
            bool ok = jetsShown > 0 && jetsHidden == 0 && jetsAgain == jetsShown;
            Console.WriteLine($"  {(ok ? "PASS" : "FAIL")} jet elements drawing: shown {jetsShown}, hidden {jetsHidden}, shown again {jetsAgain} "
                + $"(all effect draws: {Fx(shown)} / {Fx(hidden)} / {Fx(again)})");
        }
    }
}
