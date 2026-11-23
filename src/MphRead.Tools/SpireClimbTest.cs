using System;
using MphRead.Entities;
using MphRecomp.Campaign;
using MphRecomp.Multiplayer;
using OpenTK.Mathematics;

namespace MphRead
{
    // -spireclimbtest: Spire's Dialanche holding into a wall climbs it as vanilla does (Discord report 2026-11-13, "Spire's alt
    // climb struggles to go straight up"). Combat Hall's -X wall (plane x -8.7) in a battle match, the ball pushed at the wall
    // along a world direction, the stick re-aimed each step to keep that direction as the camera turns, as a player steering at
    // the wall does (a stick held fixed on screen slides along the wall in vanilla too, as the camera swings after the ball);
    // SPIRECLIMB_STEER=0 holds the first step's stick instead. Each case is "spot x,y,z dir x,z climb|stall", from scripted
    // vanilla runs (BizHawk, US rev 1, 2026-11-13, the same spot, start facing and steering, 8-way d-pad): by (-7, -17.9)
    // straight in and 30 degrees to -Z reach y 5 (vanilla frame 114 and 112), 30 degrees to +Z stalls under the wall's
    // protrusion at z -12.3 (top 4.54, ours rolled through it before the wall-touch fix); by (-7, 1.5) all three reach y 5
    // (vanilla 108, 112, 194). A climb case passes reaching y 5 within SPIRECLIMB_FRAMES steps (default 300), a stall case
    // staying under it. SPIRECLIMB_CASES replaces the list; SPIRECLIMB_SPOT/SPIRECLIMB_DIRS (x,z;x,z) make climb cases
    // instead; SPIRECLIMB_FACE x,z is the facing before morphing (default vanilla's, -0.2012,-0.9795); SPIRECLIMB_TRACE=1
    // prints every step.
    internal static class SpireClimbTest
    {
        public static void Run(string[] args)
        {
            if (!ToolPaths.UseDesktopExtraction()) return;
            static float[] Parse(string t) => Array.ConvertAll(t.Split(','), x => float.Parse(x, System.Globalization.CultureInfo.InvariantCulture));
            string room = Environment.GetEnvironmentVariable("SPIRECLIMB_ROOM") ?? "MP3 PROVING GROUND";
            string? spotEnv = Environment.GetEnvironmentVariable("SPIRECLIMB_SPOT");
            string? dirsEnv = Environment.GetEnvironmentVariable("SPIRECLIMB_DIRS");
            string cases = Environment.GetEnvironmentVariable("SPIRECLIMB_CASES") ?? "-7.0,0.7,-17.9 -1,0 climb;"
                + "-7.0,0.7,-17.9 -0.866,-0.5 climb;-7.0,0.7,-17.9 -0.866,0.5 stall;-7.0,0.7,1.5 -1,0 climb;"
                + "-7.0,0.7,1.5 -0.866,0.5 climb;-7.0,0.7,1.5 -0.866,-0.5 climb";
            if (spotEnv != null || dirsEnv != null)
            {
                cases = String.Join(';', Array.ConvertAll((dirsEnv ?? "-1,0;-0.866,0.5;-0.866,-0.5").Split(';', StringSplitOptions.RemoveEmptyEntries),
                    d => $"{spotEnv ?? "-7.0,0.7,-17.9"} {d} climb"));
            }
            int frames = int.TryParse(Environment.GetEnvironmentVariable("SPIRECLIMB_FRAMES"), out int fr) ? fr : 300;
            float goal = 5;
            bool trace = Environment.GetEnvironmentVariable("SPIRECLIMB_TRACE") == "1";
            var hSpeedField = typeof(PlayerEntity).GetField("_hSpeedMag", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
            bool pass = true;
            foreach (string c in cases.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                string[] parts = c.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                float[] spot = Parse(parts[0]);
                float[] dv = Parse(parts[1]);
                bool expectClimb = parts.Length < 3 || parts[2] != "stall";
                var dir = new Vector3(dv[0], 0, dv[1]).Normalized();
                var at = new Vector3(spot[0], spot[1], spot[2]);
                // a battle match, as vanilla's: a single-player load of an arena takes other node/entity layers (a
                // MorphCamera over this wall's top, extra collision), and the bot parked far off, as the BizHawk runs do
                var settings = new MatchSettings { Mode = GameMode.Battle, Arena = room };
                settings.Players.Add(new MatchPlayer { Hunter = Hunter.Spire, Nickname = "Player1" });
                settings.Players.Add(new MatchPlayer { Hunter = Hunter.Samus, Bot = true });
                using CampaignHost host = CampaignHost.StartMatch(settings);
                PlayerEntity me = PlayerEntity.Players[0];
                void Step(CampaignInput input)
                {
                    PlayerEntity bot = PlayerEntity.Players[1];
                    bot.Position = bot.PrevPosition = new Vector3(-40, 0.5f, -40);
                    bot.Speed = Vector3.Zero;
                    host.Step(input);
                }
                for (int f = 0; f < 900; f++) Step(default);
                // face the wall before morphing so the ball's camera does too, as a player lines it up
                float[] fv = Parse(Environment.GetEnvironmentVariable("SPIRECLIMB_FACE") ?? "-0.2012,-0.9795");
                Vector3 face = new Vector3(fv[0], 0, fv[1]).Normalized();
                me.Teleport(at, face, host.Scene.GetNodeRefContaining(at));
                me.PrevPosition = at;
                for (int f = 0; f < 30; f++) Step(default);
                Step(new CampaignInput { Buttons = CampaignButtons.Morph });
                for (int f = 0; f < 300 && !(me.IsAltForm && !me.IsMorphing); f++) Step(default);
                for (int f = 0; f < 60; f++) Step(default);
                float startY = me.Position.Y;
                float top = startY;
                int reached = -1;
                int climbSteps = 0;
                // the stick from the camera's axes on screen (forward = CameraInfo.Field48/4C, left = Field50/54)
                var fwd = new Vector3(me.CameraInfo.Field48, 0, me.CameraInfo.Field4C);
                var right = -new Vector3(me.CameraInfo.Field50, 0, me.CameraInfo.Field54);
                var move = new System.Numerics.Vector2(Vector3.Dot(dir, right), Vector3.Dot(dir, fwd));
                if (move.Length() > 1) move /= move.Length();
                bool steer = Environment.GetEnvironmentVariable("SPIRECLIMB_STEER") != "0";
                for (int f = 0; f < frames; f++)
                {
                    if (steer)
                    {
                        fwd = new Vector3(me.CameraInfo.Field48, 0, me.CameraInfo.Field4C);
                        right = -new Vector3(me.CameraInfo.Field50, 0, me.CameraInfo.Field54);
                        move = new System.Numerics.Vector2(Vector3.Dot(dir, right), Vector3.Dot(dir, fwd));
                        if (move.Length() > 1) move /= move.Length();
                    }
                    Step(new CampaignInput { Move = move });
                    bool climbing = me.Flags2.TestFlag(PlayerFlags2.SpireClimbing);
                    if (climbing) climbSteps++;
                    top = MathF.Max(top, me.Position.Y);
                    if (reached < 0 && me.Position.Y >= goal) reached = f;
                    if (trace)
                    {
                        Vector3 p = me.Position;
                        Vector3 s = me.Speed;
                        Console.WriteLine($"[spireclimb] f{f} pos {p.X:0.000},{p.Y:0.000},{p.Z:0.000} speed {s.X:0.0000},{s.Y:0.0000},{s.Z:0.0000}"
                            + $" hmag {(float)hSpeedField.GetValue(me)!:0.0000} lat {(me.Flags1.TestFlag(PlayerFlags1.CollidingLateral) ? 1 : 0)}"
                            + $" climb {(climbing ? 1 : 0)} cam {me.CameraInfo.Field48:0.00},{me.CameraInfo.Field4C:0.00} stick {move.X:0.00},{move.Y:0.00}"
                            + $" campos {me.CameraInfo.Position.X:0.000},{me.CameraInfo.Position.Y:0.000},{me.CameraInfo.Position.Z:0.000} camtgt {me.CameraInfo.Target.Y:0.000}");
                    }
                }
                bool ok = (reached >= 0) == expectClimb;
                pass &= ok;
                Console.WriteLine($"[spireclimb] {(expectClimb ? "climb" : "stall")} at {spot[0]:0.0},{spot[2]:0.0} dir {dir.X:0.00},{dir.Z:0.00}: y {startY:0.00} -> top {top:0.00}, y {goal} at step "
                    + $"{(reached < 0 ? "never" : reached.ToString())}, climbing {climbSteps}/{frames} steps, end {me.Position.X:0.00},{me.Position.Y:0.00},{me.Position.Z:0.00}"
                    + $" {(ok ? "PASS" : "FAIL")}");
            }
            Console.WriteLine($"[spireclimb] {(pass ? "PASS" : "FAIL")}");
        }
    }
}
