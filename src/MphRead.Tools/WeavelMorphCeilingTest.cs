using System;
using System.Globalization;
using MphRead.Entities;
using MphRecomp.Campaign;
using MphRecomp.Multiplayer;
using OpenTK.Mathematics;

namespace MphRead
{
    // -weavelmorphtest: Weavel starting to morph beside a low overhang (owner's replay "clip", 2026-11-22: tgirllimoki fell
    // through Cryochasm's floor at 1:56, standing at (9.65, 33.98, -27.65) beside a block whose underside is at y 35.28).
    // His Halfturret is added under him the step the morph starts and lifts his still-standing body 0.9; under the block the
    // biped's 4x ceiling push-out then shoved him below the floor's plane (PlayerCollision.cs HandleCollision,
    // docs/ACCURACY.md A15). The test stands a Weavel on every spot of a grid around that place, morphs with each stick
    // direction held and runs on: FAIL if any run ends far below the floor it started on (out of the arena). Each stick
    // direction is first walked with no morph; where that alone leaves the arena (a ledge) the morph isn't counted. A fall
    // counts only when a step pushed him down more than 1 (the shove); a smooth fall is the turret's lift and sideways
    // push carrying him over a ledge beside the pit, listed but not a failure (not checked against vanilla).
    //   -weavelmorphtest         the fix on: must be 0 falls
    //   -weavelmorphtest off     the 4x push uncapped (the old way): shows the falls the fix removes
    //   -weavelmorphtest trace   each morph's first steps (WEAVELMORPH_TRACEN of them, default 12)
    // WEAVELMORPH_BOX="x0 x1 z0 z1 step" another box; WEAVELMORPH_Y its floor (Position.Y standing); WEAVELMORPH_ROOM.
    internal static class WeavelMorphCeilingTest
    {
        public static void Run(string[] args)
        {
            if (!ToolPaths.UseDesktopExtraction()) return;
            // "public": as a public build (the cap is gated off there until the owner has tested it), must behave like "off"
            bool asPublic = Array.IndexOf(args, "public") >= 0;
            if (asPublic) MphRecomp.Config.RecompSettings.PublicBuild = true;
            bool off = Array.IndexOf(args, "off") >= 0 || asPublic;
            bool trace = Array.IndexOf(args, "trace") >= 0;
            int traceSteps = int.TryParse(Environment.GetEnvironmentVariable("WEAVELMORPH_TRACEN"), out int tn) ? tn : 12;
            string room = Environment.GetEnvironmentVariable("WEAVELMORPH_ROOM") ?? "MP9 CRYOCHASM";
            float[] box = { 8.9f, 10.3f, -29.0f, -26.2f, 0.1f };
            if (Environment.GetEnvironmentVariable("WEAVELMORPH_BOX") is string b)
            {
                string[] parts = b.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                for (int i = 0; i < box.Length && i < parts.Length; i++) box[i] = float.Parse(parts[i], CultureInfo.InvariantCulture);
            }
            float floorY = float.TryParse(Environment.GetEnvironmentVariable("WEAVELMORPH_Y"), NumberStyles.Float,
                CultureInfo.InvariantCulture, out float fy) ? fy : 33.981f;
            MphRecomp.Arenas.EchoesMovement.CeilingPushCap = !off || asPublic; // (applied on each room load; a public build has it off too)
            var moves = new System.Numerics.Vector2[9];
            for (int i = 0; i < 8; i++) moves[i + 1] = new System.Numerics.Vector2(MathF.Round(MathF.Sin(i * MathF.PI / 4)), MathF.Round(MathF.Cos(i * MathF.PI / 4)));
            int spots = 0, runs = 0, falls = 0, lifted = 0, ledges = 0, hops = 0;
            CampaignHost? host = null;
            PlayerEntity me = null!;
            Vector3 home = default;
            void Step(CampaignInput input)
            {
                PlayerEntity bot = PlayerEntity.Players[1];
                bot.Position = bot.PrevPosition = new Vector3(-40, 0.5f, -40);
                bot.Speed = Vector3.Zero;
                host!.Step(input);
            }
            void Start()
            {
                host?.Dispose();
                var settings = new MatchSettings { Mode = GameMode.Battle, Arena = room };
                settings.Players.Add(new MatchPlayer { Hunter = Hunter.Weavel, Nickname = "Player1" });
                settings.Players.Add(new MatchPlayer { Hunter = Hunter.Samus, Bot = true });
                host = CampaignHost.StartMatch(settings);
                me = PlayerEntity.Players[0];
                Step(new CampaignInput { Buttons = CampaignButtons.Shoot });
                for (int f = 0; f < 900; f++) Step(default);
                home = me.Position;
            }
            // back to two legs at the spawn point (under the overhang he can't unmorph), then onto the spot
            bool Place(Vector3 at)
            {
                if (me.Health == 0 || me.Position.Y < floorY - 20) Start();
                me.Position = me.PrevPosition = home;
                me.Speed = Vector3.Zero;
                for (int f = 0; f < 20; f++) Step(default);
                if (me.IsAltForm || me.IsMorphing)
                {
                    Step(new CampaignInput { Buttons = CampaignButtons.Morph });
                    for (int f = 0; f < 300 && (me.IsAltForm || me.IsUnmorphing || me.IsMorphing); f++) Step(default);
                    for (int f = 0; f < 10; f++) Step(default);
                }
                if (me.IsAltForm || me.Health == 0)
                {
                    Start();
                }
                me.Position = me.PrevPosition = at;
                me.Speed = Vector3.Zero;
                for (int f = 0; f < 8; f++) Step(default);
                return me.Flags1.TestFlag(PlayerFlags1.Standing) && MathF.Abs(me.Position.Y - at.Y) < 0.05f
                    && (me.Position - at).Xz.Length < 0.05f;
            }
            // one run from where he stands: the first step's button with the stick held, the stick for 90 steps, then on to 420
            bool Go(System.Numerics.Vector2 move, CampaignButtons first, bool print, out float top, out float low, out float shove)
            {
                top = low = me.Position.Y;
                shove = 0;
                Step(new CampaignInput { Move = move, Buttons = first });
                for (int f = 0; f < 420; f++)
                {
                    top = MathF.Max(top, me.Position.Y);
                    low = MathF.Min(low, me.Position.Y);
                    if (print && f < traceSteps)
                    {
                        Console.WriteLine($"[weavelmorph]   f{f} {me.Position.X:0.000} {me.Position.Y:0.000} {me.Position.Z:0.000} sy {me.Speed.Y:0.000} alt {me.IsAltForm} morphing {me.IsMorphing}");
                    }
                    if (me.Health == 0 || me.Position.Y < floorY - 20) break;
                    float y = me.Position.Y, sy = MathF.Min(me.Speed.Y, 0);
                    Step(new CampaignInput { Move = f < 90 ? move : default });
                    // a drop in one step past what he was falling at: a push down (the switch to alt form re-centres him 0.5)
                    if (!me.IsAltForm) shove = MathF.Max(shove, y + sy - me.Position.Y);
                }
                return me.Health == 0 || me.Position.Y < floorY - 20;
            }
            Start();
            // a Deathalt pickup forces the morph with no button (PlayerProcess.cs): the replay's morph was one of these
            foreach (EntityBase e in host!.Scene.Entities)
            {
                if (e is ItemSpawnEntity sp && sp.Data.ItemType == ItemType.Deathalt)
                {
                    Console.WriteLine($"[weavelmorph] Deathalt pickup spawns at ({e.Position.X:0.00}, {e.Position.Y:0.00}, {e.Position.Z:0.00})");
                }
            }
            for (float x = box[0]; x <= box[1] + 0.001f; x += box[4])
            {
                for (float z = box[2]; z <= box[3] + 0.001f; z += box[4])
                {
                    var at = new Vector3(x, floorY, z);
                    bool counted = false;
                    for (int m = 0; m < moves.Length; m++)
                    {
                        // the control first: the same stick with no morph. Leaving the arena that way is a ledge to walk off
                        if (!Place(at)) break; // not a place to stand
                        if (!counted) { spots++; counted = true; }
                        if (Go(moves[m], CampaignButtons.None, trace, out _, out _, out float walkShove))
                        {
                            ledges++;
                            if (walkShove > 1)
                            {
                                falls++;
                                Console.WriteLine($"[weavelmorph] SHOVED THROUGH THE FLOOR just walking from ({x:0.00}, {floorY:0.00}, {z:0.00}) stick {moves[m].X:0} {moves[m].Y:0}: pushed down {walkShove:0.00} in a step");
                            }
                            continue;
                        }
                        if (!Place(at)) break;
                        runs++;
                        bool fell = Go(moves[m], CampaignButtons.Morph, trace, out float top, out float low, out float shove);
                        if (top > floorY + 0.5f) lifted++;
                        if (fell && shove > 1)
                        {
                            falls++;
                            Console.WriteLine($"[weavelmorph] SHOVED THROUGH THE FLOOR from ({x:0.00}, {floorY:0.00}, {z:0.00}) stick {moves[m].X:0} {moves[m].Y:0}: top {top:0.00}, pushed down {shove:0.00} in a step, lowest {low:0.00}");
                        }
                        else if (fell)
                        {
                            hops++;
                            Console.WriteLine($"[weavelmorph] over a ledge from ({x:0.00}, {floorY:0.00}, {z:0.00}) stick {moves[m].X:0} {moves[m].Y:0}: top {top:0.00}, biggest push down {shove:0.00}, lowest {low:0.00}");
                        }
                    }
                }
            }
            host?.Dispose();
            MphRecomp.Arenas.EchoesMovement.CeilingPushCap = true;
            Console.WriteLine($"[weavelmorph] {room}, ceiling push cap {(off ? "OFF (old)" : "on")}: {spots} standing spots, {runs} morphs, "
                + $"{lifted} lifted over 0.5 by the turret, {falls} shoved through the floor, {hops} carried over a ledge by the turret's lift ({ledges} spot-and-stick pairs skipped: a plain walk leaves the arena there)");
            Console.WriteLine(off ? $"[weavelmorph] A/B run (cap off): {falls} falls" : falls == 0 ? "[weavelmorph] PASS" : "[weavelmorph] FAIL");
            if (!off && falls > 0) Environment.ExitCode = 1;
        }
    }
}
