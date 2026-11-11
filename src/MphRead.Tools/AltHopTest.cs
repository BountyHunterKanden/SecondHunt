using System;
using MphRead.Entities;
using MphRead.Formats;
using MphRecomp.Campaign;
using MphRecomp.Multiplayer;

namespace MphRead
{
    // -althoptest: the campaign's alt-form hop (PlayerAltHop.cs, owner 2026-11-07)
    //   campaign: every hunter's alt form (Samus's ball too) hops once on the jump button, at once, to about the height
    //             Samus's morph ball bomb jump reaches
    //   match: no hop (MatchStart turns it off)
    internal static class AltHopTest
    {
        private static string _room = "UNIT1_C0";

        public static void Run(string[] args)
        {

            if (!ToolPaths.UseDesktopExtraction()) return;
            if (args.Length >= 4 && args[1] == "jump")
            {
                JumpAt(args[2], args[3]);
                return;
            }
            if (args.Length >= 2 && args[1] == "padswitch")
            {
                PadSwitch();
                return;
            }
            if (args.Length >= 2 && args[1] == "ents")
            {
                Ents(args.Length >= 3 ? args[2] : "UNIT1_C4");
                return;
            }
            if (args.Length >= 2 && args[1] == "cammove")
            {
                CamMove();
                return;
            }
            if (args.Length >= 2 && args[1] == "strafe")
            {
                Strafe();
                return;
            }
            if (args.Length >= 2 && args[1] == "bob")
            {
                Bob(args[2], float.Parse(args[3], System.Globalization.CultureInfo.InvariantCulture), float.Parse(args[4], System.Globalization.CultureInfo.InvariantCulture));
                return;
            }
            if (args.Length >= 2 && args[1] == "restscan")
            {
                RestScan();
                return;
            }
            if (args.Length >= 2 && args[1] == "stuck")
            {
                Stuck(args[2], args[3], args[4]);
                return;
            }
            if (args.Length >= 4 && args[1] == "faces")
            {
                Faces(args[2], args[3]);
                return;
            }
            if (args.Length >= 4 && args[1] == "slice")
            {
                Slice(args[2], args[3]);
                return;
            }
            if (args.Length >= 2 && args[1] == "slipmap")
            {
                SlipMap(args.Length >= 3 ? args[2] : "UNIT4_RM1");
                return;
            }
            if (args.Length >= 2 && args[1] == "tap")
            {
                Tap();
                return;
            }
            if (args.Length >= 2 && args[1] == "spot")
            {
                Spot();
                return;
            }
            if (args.Length >= 2 && args[1] == "parity")
            {
                Parity(args[2], args.Length >= 4 ? args[3] : "0,-1");
                return;
            }
            if (args.Length >= 2 && args[1] == "cross")
            {
                Cross();
                return;
            }
            if (args.Length >= 2 && args[1] == "lipscan")
            {
                LipScan();
                return;
            }
            if (args.Length >= 2 && args[1] == "edges")
            {
                Edges(args.Length >= 3 ? args[2] : "11.5,45.5,-20.5", args.Length >= 4 ? float.Parse(args[3], System.Globalization.CultureInfo.InvariantCulture) : 1);
                return;
            }
            if (args.Length >= 2 && args[1] == "tp")
            {
                Tp(args.Length >= 3 ? args[2] : "UNIT1_C4", args.Length >= 4 ? args[3] : "0.59,-27.63,-11.91;0.59,-23.03,-10.61");
                return;
            }
            if (args.Length >= 3 && args[1] == "climb")
            {
                Climb(args[2]);
                return;
            }
            if (args.Length >= 2 && args[1] == "face")
            {
                Face();
                return;
            }
            if (args.Length >= 2) _room = args[1];
            bool pass = true;
            float bombPeak;
            using (CampaignHost host = CampaignHost.Start(_room, arriving: false))
            {
                PlayerEntity me = Settle(host);
                Morph(host, me);
                (bombPeak, int start) = Rise(host, me, CampaignButtons.AltAttack, 150);
                Console.WriteLine($"[bomb] Samus bomb jump: peak {bombPeak:0.000} after {start} frames");
                if (bombPeak < 0.3f)
                {
                    Console.WriteLine("[bomb] FAIL: no bomb jump measured");
                    pass = false;
                }
            }
            foreach (Hunter hunter in new[] { Hunter.Samus, Hunter.Kanden, Hunter.Trace, Hunter.Sylux, Hunter.Noxus, Hunter.Spire, Hunter.Weavel })
            {
                using CampaignHost host = CampaignHost.Start(_room, hunter: hunter, arriving: false);
                PlayerEntity me = Settle(host);
                Morph(host, me);
                (float peak, int start) = Rise(host, me, CampaignButtons.Jump, 150);
                bool ok = me.IsAltForm && me.AltHops == 1 && start >= 0 && start <= 1
                    && Math.Abs(peak - bombPeak) <= bombPeak * 0.15f;
                Console.WriteLine($"[campaign] {hunter}: alt {me.IsAltForm}, hops {me.AltHops}, peak {peak:0.000} after {start} frames {(ok ? "ok" : "FAIL")}");
                pass &= ok;
            }
            {
                var settings = new MatchSettings { Mode = GameMode.Battle, Arena = MatchArenas.Find("Combat Hall")!.Name };
                settings.Players.Add(new MatchPlayer { Hunter = Hunter.Kanden, Nickname = "Player1" });
                settings.Players.Add(new MatchPlayer { Hunter = Hunter.Spire, Bot = true });
                using CampaignHost host = CampaignHost.StartMatch(settings);
                PlayerEntity me = PlayerEntity.Players[0];
                for (int f = 0; f < 900; f++)
                {
                    host.Step(default);
                }
                Morph(host, me);
                (float peak, _) = Rise(host, me, CampaignButtons.Jump, 60);
                bool ok = !me.HostAltHop && me.AltHops == 0 && peak < 0.05f;
                Console.WriteLine($"[match] Kanden: hop flag {me.HostAltHop}, hops {me.AltHops}, rise {peak:0.000} {(ok ? "ok" : "FAIL")}");
                pass &= ok;
            }
            Console.WriteLine(pass ? "PASS althoptest" : "FAIL althoptest");
        }

        // -althoptest cammove: Sylux on Ice Hive's angled platforms under a room camera (PlayerAltCamMove.cs): holding
        // forward / right moves him along the camera's view / to its right; with AltCamMoveOff, relative to his facing
        private static void CamMove()
        {
            bool pass = true;
            string[] names = { "forward", "right", "back", "left" };
            bool[] best = new bool[8];
            foreach (bool off in new[] { false, true })
            {
                PlayerEntity.AltCamMoveOff = off;
                foreach (OpenTK.Mathematics.Vector3 at in new[] { new OpenTK.Mathematics.Vector3(11.5f, 45.5f, -20.5f),
                    new OpenTK.Mathematics.Vector3(15.5f, 45.5f, -22.5f) }) // the ledge and an angled platform, in camera 219's box
                foreach ((string name, System.Numerics.Vector2 move) in new[] { ("forward", new System.Numerics.Vector2(0, 1)),
                    ("right", new System.Numerics.Vector2(1, 0)), ("back", new System.Numerics.Vector2(0, -1)), ("left", new System.Numerics.Vector2(-1, 0)) })
                {
                    using CampaignHost host = CampaignHost.Start("UNIT4_RM1", hunter: Enum.TryParse(Environment.GetEnvironmentVariable("ALTHOP_HUNTER"), out Hunter eh) ? eh : Hunter.Sylux, arriving: false);
                    PlayerEntity me = Settle(host);
                    for (int w = 0; w < 6; w++) { Settle(host); }
                    Morph(host, me);
                    me.Teleport(at, new OpenTK.Mathematics.Vector3(1, 0, 0), host.Scene.GetNodeRefContaining(at));
                    for (int f = 0; f < 60; f++)
                    {
                        host.Step(default);
                    }
                    if (me.MorphCamera == null)
                    {
                        Console.WriteLine($"[cammove] no room camera at {me.Position}");
                        pass = false;
                        continue;
                    }
                    OpenTK.Mathematics.Vector3 start = me.Position;
                    // the camera's view on screen: forward = CameraInfo.Field48/4C, left = Field50/54 (as the rolling alt forms)
                    var fwd = new OpenTK.Mathematics.Vector3(me.CameraInfo.Field48, 0, me.CameraInfo.Field4C).Normalized();
                    var right = -new OpenTK.Mathematics.Vector3(me.CameraInfo.Field50, 0, me.CameraInfo.Field54).Normalized();
                    OpenTK.Mathematics.Vector3 want = move.Y != 0 ? fwd * move.Y : right * move.X;
                    OpenTK.Mathematics.Vector3 face0 = me.FacingVector;
                    int frames = 0;
                    float pushDot = 0; // the first frames' speed: the input's own push, before the ground or a wall turns him
                    for (int f = 0; f < 16 && me.MorphCamera != null; f++)
                    {
                        host.Step(new CampaignInput { Move = move });
                        frames++;
                        OpenTK.Mathematics.Vector3 h = me.Speed.WithY(0);
                        if (f == 2 && h.Length > 0.001f)
                        {
                            pushDot = OpenTK.Mathematics.Vector3.Dot(h.Normalized(), want);
                        }
                    }
                    OpenTK.Mathematics.Vector3 d = (me.Position - start).WithY(0);
                    float dot = d.Length > 0.01f ? OpenTK.Mathematics.Vector3.Dot(d.Normalized(), want) : 0;
                    OpenTK.Mathematics.Vector3 face = me.FacingVector.WithY(0).Normalized();
                    float faceDot = OpenTK.Mathematics.Vector3.Dot(face, want); // turned toward the held direction
                    // (a wall or slope at one spot or the other turns some pushes: each direction must be clean at one)
                    bool ok = pushDot > 0.95f;
                    if (!off && frames == 16 && faceDot <= 0.8f) // the body turns to the held direction (when he stays under the camera)
                    {
                        pass = false;
                    }
                    best[(off ? 4 : 0) + Array.IndexOf(names, name)] |= ok;
                    Console.WriteLine($"[cammove] {(off ? "old" : "new")} {at.X:0.0},{at.Z:0.0} {name}: pushed along camera view {pushDot:0.00}, moved {d.Length:0.00} at {dot:0.00},"
                        + $" facing the held direction {faceDot:0.00} after {frames} frames (facing at start {face0.X:0.00},{face0.Z:0.00}) {(ok ? "clean" : "-")}");
                }
            }
            PlayerEntity.AltCamMoveOff = false;
            for (int i = 0; i < 4; i++)
            {
                pass &= best[i] && !best[4 + i]; // new: along the camera view, facing the move; old: not
            }
            Console.WriteLine(pass ? "PASS althoptest cammove" : "FAIL althoptest cammove");
        }

        // -althoptest strafe: alt-form speed from rest, held forward for 20 ticks, then let go, against vanilla's 30 Hz tick
        // (speed = cap(speed + traction) * factor; position += speed): docs/ACCURACY.md A8 (rolling) and A12 (strafing)
        private static void Strafe()
        {
            bool pass = true;
            foreach (Hunter hunter in new[] { Hunter.Samus, Hunter.Trace, Hunter.Sylux, Hunter.Weavel })
            {
                using CampaignHost host = CampaignHost.Start(_room, hunter: hunter, arriving: false);
                PlayerEntity me = Settle(host);
                Morph(host, me);
                PlayerValues v = me.Values;
                bool strafe = v.AltFormStrafe != 0;
                float t = Fixed.ToFloat(strafe ? v.WalkBipedTraction : v.RollAltTraction);
                float f = Fixed.ToFloat(v.AltGroundSpeedFactor), cap = Fixed.ToFloat(v.AltMinHSpeed);
                float vs = 0, vd = 0, worst = 0, os = 0, prev = 0;
                bool blocked = false; // a wall ahead (the test spot has ~4 u of floor): judged up to there
                OpenTK.Mathematics.Vector3 start = me.Position;
                int vTop = -1, mTop = -1;
                var line = new System.Text.StringBuilder();
                for (int k = 0; k < 40; k++)
                {
                    bool held = k < 20;
                    vs = (held ? MathF.Min(vs + t, cap) : vs) * f;
                    for (int i = 0; i < 2; i++)
                    {
                        // before the fix: the whole traction every 60 Hz frame, friction (1 + factor) / 2 (strafing forms)
                        os = (held ? MathF.Min(os + t, cap) : os) * (1 + f) / 2;
                    }
                    vd += vs;
                    for (int i = 0; i < 2; i++)
                    {
                        host.Step(new CampaignInput { Move = new System.Numerics.Vector2(0, held ? 1 : 0) });
                    }
                    float ms = me.Speed.Xz.Length, md = (me.Position - start).Xz.Length;
                    if (vTop < 0 && vs >= cap * f * 0.98f) { vTop = k; }
                    if (mTop < 0 && ms >= cap * f * 0.98f) { mTop = k; }
                    blocked |= held && ms < prev * 0.5f;
                    prev = ms;
                    if (k < 20 && !blocked)
                    {
                        worst = MathF.Max(worst, MathF.Abs(ms - vs) / MathF.Max(vs, 0.01f));
                    }
                    if (k < 6 || k == 19 || k == 39)
                    {
                        line.Append($" t{k}: {vs:0.000}/{ms:0.000}{(strafe && k < 3 ? $" (old {os:0.000})" : "")}");
                    }
                }
                bool ok = worst < 0.08f && Math.Abs(vTop - mTop) <= 1;
                Console.WriteLine($"[strafe] {hunter}: traction {t:0.0000} factor {f:0.0000} cap {cap:0.0000}; top speed at tick vanilla {vTop} MphRead {mTop}; "
                    + $"worst speed gap while held {worst * 100:0.0}% {(ok ? "ok" : "FAIL")}");
                Console.WriteLine($"[strafe]   speed vanilla/MphRead:{line}");
                pass &= ok;
                {
                    // a part tilt of the move stick (STICK CURVE MODERN, Core Input/StickCurve.cs; PlayerStickPush.cs): half a
                    // tilt tops out at its push's share of the full tilt's top speed, the full tilt at vanilla's (every form)
                    float Top(float tilt) => Run(new System.Numerics.Vector2(0, tilt), true, 90, out _);
                    // the top speed and how far it went, the stick's (MODERN) or the d-pad's
                    float Run(System.Numerics.Vector2 move, bool stick, int steps, out OpenTK.Mathematics.Vector3 went)
                    {
                        using CampaignHost h2 = CampaignHost.Start(_room, hunter: hunter, arriving: false);
                        PlayerEntity me2 = Settle(h2);
                        Morph(h2, me2);
                        OpenTK.Mathematics.Vector3 from = me2.Position;
                        float top = 0;
                        for (int i = 0; i < steps; i++)
                        {
                            var input = new CampaignInput { Move = move };
                            if (stick)
                            {
                                MphRecomp.Input.StickCurve.Apply(ref input);
                            }
                            h2.Step(input);
                            top = MathF.Max(top, me2.Speed.Xz.Length);
                        }
                        went = me2.Position - from;
                        return top;
                    }
                    float full = Top(1), part = Top(0.65f);
                    var probe = new CampaignInput { Move = new System.Numerics.Vector2(0, 0.65f) };
                    MphRecomp.Input.StickCurve.Apply(ref probe);
                    float share = CampaignInput.PushScale(probe.MovePush);
                    bool hok = MathF.Abs(part / full - share) < 0.06f;
                    Console.WriteLine($"[strafe]   stick curve: full tilt top {full:0.000}, 0.65 tilt push {probe.MovePush}/16 top {part:0.000}"
                        + $" = {part / full:0.00} of full, want ~{share:0.00} {(hok ? "ok" : "FAIL")}");
                    pass &= hok;
                    // 360-degree steering (PlayerStickPush.cs): the stick straight ahead and at 45 degrees moves exactly as
                    // the d-pad's up and up-right, and a 25-degree tilt goes between them
                    Run(new System.Numerics.Vector2(0, 1), false, 30, out OpenTK.Mathematics.Vector3 padUp);
                    Run(new System.Numerics.Vector2(1, 1), false, 30, out OpenTK.Mathematics.Vector3 padDiag);
                    Run(new System.Numerics.Vector2(0, 1), true, 30, out OpenTK.Mathematics.Vector3 stickUp);
                    Run(new System.Numerics.Vector2(0.7071f, 0.7071f), true, 30, out OpenTK.Mathematics.Vector3 stickDiag);
                    Run(new System.Numerics.Vector2(MathF.Sin(MathF.PI * 25 / 180), MathF.Cos(MathF.PI * 25 / 180)), true, 30, out OpenTK.Mathematics.Vector3 between);
                    float Heading(OpenTK.Mathematics.Vector3 v) => MathF.Atan2(v.X, v.Z) * 180 / MathF.PI;
                    float hu = Heading(padUp), hd = Heading(padDiag), hb = Heading(between);
                    float Gap(float a, float b) => MathF.Abs(((a - b) % 360 + 540) % 360 - 180);
                    bool same = stickUp == padUp && stickDiag == padDiag;
                    bool mid = Gap(hb, hu) > 3 && Gap(hb, hd) > 3 && MathF.Abs(Gap(hb, hu) + Gap(hb, hd) - Gap(hu, hd)) < 0.5f;
                    Console.WriteLine($"[strafe]   360: stick up/diagonal {(same ? "the same as" : "NOT the same as")} the d-pad's; headings up {hu:0.0}"
                        + $" diagonal {hd:0.0}, 25-degree tilt {hb:0.0} {(same && mid ? "ok" : "FAIL")}");
                    pass &= same && mid;
                }
            }
            Console.WriteLine(pass ? "PASS althoptest strafe" : "FAIL althoptest strafe");
        }

        // Ice Hive (UNIT4_RM1) as Sylux: from a spot, hold forward in 16 headings for 4 s, with and without the edge push
        // (PlayerAltCamMove.cs); how far and how high the ball gets. Edges, ledges and walls are what's judged: with the
        // edge push the ball should get at least as far and as high in every heading (owner 2026-11-08)
        private static void Edges(string at, float push)
        {
            float[] v = Array.ConvertAll(at.Split(','), x => float.Parse(x, System.Globalization.CultureInfo.InvariantCulture));
            var spot = new OpenTK.Mathematics.Vector3(v[0], v[1], v[2]);
            PlayerEntity.AltCamMoveOff = true; // forward = the facing, so the 16 headings differ
            int better = 0, worse = 0;
            for (int h = 0; h < 16; h++)
            {
                float yaw = h * MathF.PI / 8;
                var facing = new OpenTK.Mathematics.Vector3(MathF.Sin(yaw), 0, -MathF.Cos(yaw));
                var res = new (float Dist, float Rise)[2];
                for (int mode = 0; mode < 2; mode++)
                {
                    if (Environment.GetEnvironmentVariable("ALTHOP_ONE") == "1" && (h != 0 || mode != 1)) continue;
                    PlayerEntity.AltEdgePushOff = mode == 0;
                    using CampaignHost host = CampaignHost.Start("UNIT4_RM1", hunter: Enum.TryParse(Environment.GetEnvironmentVariable("ALTHOP_HUNTER"), out Hunter eh) ? eh : Hunter.Sylux, arriving: false);
                    PlayerEntity me = Settle(host);
                    Morph(host, me);
                    me.Teleport(spot, facing, host.Scene.GetNodeRefContaining(spot));
                    me.PrevPosition = spot;
                    for (int f = 0; f < 60; f++)
                    {
                        host.Step(default);
                    }
                    if (!me.IsAltForm)
                    {
                        Morph(host, me); // a spot in a ball-only space, where the teleport left the biped form
                    }
                    OpenTK.Mathematics.Vector3 start = me.Position;
                    float dist = 0, rise = -99;
                    for (int f = 0; f < 240; f++)
                    {
                        int hopAt = int.TryParse(Environment.GetEnvironmentVariable("ALTHOP_HOPAT"), out int ha) ? ha : -1;
                        host.Step(new CampaignInput { Move = new System.Numerics.Vector2(0, push), Buttons = f == hopAt ? CampaignButtons.Jump : CampaignButtons.None });
                        if (Environment.GetEnvironmentVariable("ALTHOP_DEBUG") == "2" && h == 4 && mode == 1)
                        {
                            Console.WriteLine($"[edges-trace] f{f} pos {me.Position} speed {me.Speed}");
                        }
                        dist = MathF.Max(dist, (me.Position - start).Xz.Length);
                        rise = MathF.Max(rise, me.Position.Y - start.Y);
                    }
                    res[mode] = (dist, rise);
                    if (Environment.GetEnvironmentVariable("ALTHOP_DEBUG") == "1")
                    {
                        Console.WriteLine($"[edges-debug] h{h} m{mode}: hops {me.AltHops} rise {rise:0.00} alt {me.IsAltForm} morphing {me.IsMorphing} start {start} end {me.Position} speed {me.Speed}");
                    }
                }
                string tag = "";
                if (res[1].Dist > res[0].Dist + 0.5f || res[1].Rise > res[0].Rise + 0.3f) { better++; tag = " better"; }
                if (res[1].Dist < res[0].Dist - 0.5f || res[1].Rise < res[0].Rise - 0.3f) { worse++; tag += " WORSE"; }
                Console.WriteLine($"[edges] heading {h * 22.5f,5:0.0}: off dist {res[0].Dist:0.00} rise {res[0].Rise:0.00} | on dist {res[1].Dist:0.00} rise {res[1].Rise:0.00}{tag}");
            }
            PlayerEntity.AltEdgePushOff = false;
            PlayerEntity.AltCamMoveOff = false;
            Console.WriteLine($"[edges] better {better}, worse {worse}");
            Console.WriteLine(worse == 0 ? "PASS althoptest edges" : "FAIL althoptest edges");
        }

        // Ice Hive (UNIT4_RM1) as Sylux: the ball's height per frame from a spot, rolling at a heading (degrees) with a
        // stick push (0 = at rest); compared with vanilla's bob logged in BizHawk (owner 2026-11-08)
        private static void Bob(string at, float heading, float push)
        {
            float[] v = Array.ConvertAll(at.Split(','), x => float.Parse(x, System.Globalization.CultureInfo.InvariantCulture));
            var spot = new OpenTK.Mathematics.Vector3(v[0], v[1], v[2]);
            PlayerEntity.AltCamMoveOff = true;
            float yaw = heading * MathF.PI / 180;
            PlayerEntity.AltEdgePushOff = Environment.GetEnvironmentVariable("ALTHOP_EDGEOFF") == "1";
            using CampaignHost host = CampaignHost.Start("UNIT4_RM1", hunter: Hunter.Sylux, arriving: false);
            PlayerEntity me = Settle(host);
            Morph(host, me);
            me.Teleport(spot, new OpenTK.Mathematics.Vector3(MathF.Sin(yaw), 0, -MathF.Cos(yaw)), host.Scene.GetNodeRefContaining(spot));
            me.PrevPosition = spot;
            for (int f = 0; f < 60; f++)
            {
                host.Step(default);
            }
            if (!me.IsAltForm)
            {
                Morph(host, me);
            }
            for (int f = 0; f < 600; f++)
            {
                int stopAt = int.TryParse(Environment.GetEnvironmentVariable("ALTHOP_STOPAT"), out int sa) ? sa : int.MaxValue;
                host.Step(new CampaignInput { Move = new System.Numerics.Vector2(0, f < stopAt ? push : 0) });
                Console.WriteLine($"[bob] {f} {me.Position.X:0.0000} {me.Position.Y:0.0000} {me.Position.Z:0.0000} {me.Speed.Y:0.0000} {me.IsAltForm}");
            }
            PlayerEntity.AltCamMoveOff = false;
            PlayerEntity.AltEdgePushOff = false;
        }

        // Ice Hive (UNIT4_RM1) as Sylux: replays an [altstuck] logcat line (pos, facing x,z, stick x,y) with the camera-relative
        // stick as on the device, printing where the ball goes (owner 2026-11-08: stops on maze edges)
        // -althoptest slipmap [room or arena]: the room's slippery collision (ice) faces, bounds and normal
        // -althoptest slice room x,y0,y1,z0,z1[,cell]: ASCII side view of the room collision cut at plane x (rows = y top
        // down, columns = z), '#' = a face crosses the cell (Magma Drop's side-view maze, owner 2026-11-09)
        private static void Slice(string room, string spec)
        {
            bool cutZ = spec.StartsWith("z:"), cutY = spec.StartsWith("y:"); // y: top view, rows = x, columns = z
            float[] v = Array.ConvertAll((cutZ || cutY ? spec[2..] : spec).Split(','), x => float.Parse(x, System.Globalization.CultureInfo.InvariantCulture));
            float px = v[0], y0 = v[1], y1 = v[2], z0 = v[3], z1 = v[4], cell = v.Length > 5 ? v[5] : 0.5f;
            int rows = (int)Math.Ceiling((y1 - y0) / cell), cols = (int)Math.Ceiling((z1 - z0) / cell);
            char[,] grid = new char[rows, cols];
            for (int r = 0; r < rows; r++) for (int c = 0; c < cols; c++) grid[r, c] = '.';
            using CampaignHost host = CampaignHost.Start(room, hunter: Hunter.Samus, arriving: false);
            void Mark(float y, float z, char ch)
            {
                int r = (int)((y1 - y) / cell), c = (int)((z - z0) / cell);
                if (r >= 0 && r < rows && c >= 0 && c < cols && (grid[r, c] == '.' || ch != '#')) grid[r, c] = ch;
            }
            foreach (MphRead.Formats.Collision.CollisionInstance inst in host.Scene.Room.RoomCollision)
            {
                if (inst.Info is not MphRead.Formats.Collision.MphCollisionInfo info) continue;
                foreach (MphRead.Formats.Collision.CollisionData data in info.Data)
                {
                    var pts = new System.Collections.Generic.List<OpenTK.Mathematics.Vector3>();
                    for (int i = 0; i < data.PointIndexCount; i++)
                    {
                        OpenTK.Mathematics.Vector3 pp = info.Points[info.PointIndices[data.PointStartIndex + i]] + inst.Translation;
                        pts.Add(cutZ ? new OpenTK.Mathematics.Vector3(pp.Z, pp.Y, pp.X) : cutY ? new OpenTK.Mathematics.Vector3(pp.Y, pp.X, pp.Z) : pp);
                    }
                    for (int i = 0; i < pts.Count; i++)
                    {
                        OpenTK.Mathematics.Vector3 a = pts[i], b = pts[(i + 1) % pts.Count];
                        if ((a.X - px) * (b.X - px) > 0 || a.X == b.X) continue;
                        float t = (px - a.X) / (b.X - a.X);
                        OpenTK.Mathematics.Vector3 q = a + (b - a) * t;
                        // walk the face's cut line: collect both crossings, then draw between them
                        for (int j = i + 1; j < pts.Count; j++)
                        {
                            OpenTK.Mathematics.Vector3 c2 = pts[j], d2 = pts[(j + 1) % pts.Count];
                            if ((c2.X - px) * (d2.X - px) > 0 || c2.X == d2.X) continue;
                            OpenTK.Mathematics.Vector3 q2 = c2 + (d2 - c2) * ((px - c2.X) / (d2.X - c2.X));
                            int steps = (int)(Math.Max(Math.Abs(q2.Y - q.Y), Math.Abs(q2.Z - q.Z)) / (cell / 4)) + 1;
                            for (int k = 0; k <= steps; k++) Mark(q.Y + (q2.Y - q.Y) * k / steps, q.Z + (q2.Z - q.Z) * k / steps, '#');
                        }
                    }
                }
            }
            foreach (EntityBase e in host.Scene.Entities)
            {
                e.GetPosition(out OpenTK.Mathematics.Vector3 ep);
                if (cutZ) ep = new OpenTK.Mathematics.Vector3(ep.Z, ep.Y, ep.X);
                else if (cutY) ep = new OpenTK.Mathematics.Vector3(ep.Y, ep.X, ep.Z);
                char ch = e.Type switch { EntityType.Teleporter => 'T', EntityType.ItemSpawn => 'i', EntityType.PlayerSpawn => 'S', EntityType.Platform => 'P', _ => ' ' };
                if (ch != ' ') Mark(ep.Y, ep.Z, ch);
                if (e.Type == EntityType.Teleporter && Environment.GetEnvironmentVariable("ALTHOP_TELE") == "1")
                    Console.WriteLine($"[slice] teleporter {e.Id} at {ep} -> {e.GetType().GetField("_targetPos", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?.GetValue(e)} room {e.GetType().GetField("_targetRoomId", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?.GetValue(e)} node {typeof(EntityBase).GetField("_nodeName", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?.GetValue(e)}");
            }
            Console.WriteLine($"[slice] {room} x {px}: y {y1} (top) .. {y0}, z {z0} (left) .. {z1}, cell {cell}");
            for (int r = 0; r < rows; r++)
            {
                var sb = new System.Text.StringBuilder();
                for (int c = 0; c < cols; c++) sb.Append(grid[r, c]);
                Console.WriteLine($"{y1 - r * cell,7:0.0} {sb}");
            }
        }

        // -althoptest faces room x,y,z,r: the room collision faces with a point within r of x,y,z (normal, points), for
        // reading a snag spot's exact edges
        private static void Faces(string room, string spec)
        {
            float[] v = Array.ConvertAll(spec.Split(','), x => float.Parse(x, System.Globalization.CultureInfo.InvariantCulture));
            var at = new OpenTK.Mathematics.Vector3(v[0], v[1], v[2]);
            using CampaignHost host = CampaignHost.Start(room, hunter: Hunter.Samus, arriving: false);
            foreach (MphRead.Formats.Collision.CollisionInstance inst in host.Scene.Room.RoomCollision)
            {
                if (inst.Info is not MphRead.Formats.Collision.MphCollisionInfo info) continue;
                foreach (MphRead.Formats.Collision.CollisionData data in info.Data)
                {
                    var pts = new System.Collections.Generic.List<string>();
                    bool near = false;
                    for (int i = 0; i < data.PointIndexCount; i++)
                    {
                        OpenTK.Mathematics.Vector3 pp = info.Points[info.PointIndices[data.PointStartIndex + i]] + inst.Translation;
                        near |= (pp - at).Length < v[3];
                        pts.Add($"({pp.X:0.00},{pp.Y:0.00},{pp.Z:0.00})");
                    }
                    if (near) Console.WriteLine($"[faces] n {info.Planes[data.PlaneIndex].Xyz:0.00} {string.Join(" ", pts)}");
                }
            }
        }

        private static void SlipMap(string room)
        {
            using CampaignHost host = room.StartsWith("UNIT") ? CampaignHost.Start(room, hunter: Hunter.Samus, arriving: false) : CombatHall(Hunter.Samus, room);
            int[] counts = new int[4];
            foreach (MphRead.Formats.Collision.CollisionInstance inst in host.Scene.Room.RoomCollision)
            {
                if (inst.Info is not MphRead.Formats.Collision.MphCollisionInfo info)
                {
                    continue;
                }
                foreach (MphRead.Formats.Collision.CollisionData data in info.Data)
                {
                    counts[data.Slipperiness]++;
                    if (data.Slipperiness == 0)
                    {
                        continue;
                    }
                    var lo = new OpenTK.Mathematics.Vector3(float.MaxValue);
                    var hi = new OpenTK.Mathematics.Vector3(float.MinValue);
                    for (int i = 0; i < data.PointIndexCount; i++)
                    {
                        OpenTK.Mathematics.Vector3 pt = info.Points[info.PointIndices[data.PointStartIndex + i]] + inst.Translation;
                        lo = OpenTK.Mathematics.Vector3.ComponentMin(lo, pt);
                        hi = OpenTK.Mathematics.Vector3.ComponentMax(hi, pt);
                    }
                    OpenTK.Mathematics.Vector4 pl = info.Planes[data.PlaneIndex];
                    Console.WriteLine($"[slip] {inst.Name} slip {data.Slipperiness} n {pl.X:0.00},{pl.Y:0.00},{pl.Z:0.00} x {lo.X:0.0}..{hi.X:0.0} y {lo.Y:0.0}..{hi.Y:0.0} z {lo.Z:0.0}..{hi.Z:0.0}");
                }
            }
            Console.WriteLine($"[slip] {room}: faces by slipperiness 0/1/2/3: {counts[0]}/{counts[1]}/{counts[2]}/{counts[3]}");
        }

        // -althoptest tap: Ice Hive (UNIT4_RM1) short and long stick pushes (light / half / full, held 4 or 20 frames) at
        // ALTHOP_SPOTS (x,y,z;...), camera forward and right: peak speed, distance and frames until the alt form stops,
        // and the floor's slipperiness (owner 2026-11-08: "the slightest stick touch sends Sylux flying")
        private static void Tap()
        {
            float[] Parse(string t) => Array.ConvertAll(t.Split(','), x => float.Parse(x, System.Globalization.CultureInfo.InvariantCulture));
            Hunter hunter = Enum.TryParse(Environment.GetEnvironmentVariable("ALTHOP_HUNTER"), out Hunter eh) ? eh : Hunter.Sylux;
            string spots = Environment.GetEnvironmentVariable("ALTHOP_SPOTS") ?? "11.5,45.5,-20.5;15.5,45.5,-22.5;16.8,35.5,-38.6";
            var slip = typeof(PlayerEntity).GetField("_slipperiness", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
            foreach (string sp in spots.Split(';'))
            {
                float[] v = Parse(sp);
                var at = new OpenTK.Mathematics.Vector3(v[0], v[1], v[2]);
                foreach ((string dir, float dx, float dy) in Environment.GetEnvironmentVariable("ALTHOP_DIRS") == "right" ? new[] { ("right", 1f, 0f) } : new[] { ("fwd", 0f, 1f), ("right", 1f, 0f) })
                foreach (float push in Parse(Environment.GetEnvironmentVariable("ALTHOP_PUSHES") ?? "0.4,0.65,1"))
                foreach (float holdF in Parse(Environment.GetEnvironmentVariable("ALTHOP_HOLDS") ?? "4,20"))
                {
                    using CampaignHost host = CampaignHost.Start("UNIT4_RM1", hunter: hunter, arriving: false);
                    PlayerEntity me = Settle(host);
                    Morph(host, me);
                    float[] fc = Parse(Environment.GetEnvironmentVariable("ALTHOP_FACE") ?? "1,0");
                    me.Teleport(at, new OpenTK.Mathematics.Vector3(fc[0], 0, fc[1]).Normalized(), host.Scene.GetNodeRefContaining(at));
                    me.PrevPosition = at;
                    for (int f = 0; f < 90; f++)
                    {
                        host.Step(default);
                    }
                    int hold = (int)holdF;
                    OpenTK.Mathematics.Vector3 start = me.Position;
                    float peak = 0;
                    int stop = -1;
                    int s0 = (int)slip.GetValue(me)!;
                    for (int f = 0; f < 300; f++)
                    {
                        bool held = f < hold;
                        host.Step(new CampaignInput { Move = held ? new System.Numerics.Vector2(dx * push, dy * push) : default });
                        float h = me.Speed.Xz.Length;
                        peak = MathF.Max(peak, h);
                        if (Environment.GetEnvironmentVariable("ALTHOP_DEBUG") == "1" && f % 10 == 0)
                        {
                            Console.WriteLine($"[tapf] {f} pos {me.Position.X:0.000},{me.Position.Y:0.000},{me.Position.Z:0.000} h {h:0.0000} alt {me.IsAltForm}");
                        }
                        if (!held && stop < 0 && h < 0.005f)
                        {
                            stop = f;
                        }
                    }
                    float d = (me.Position - start).Xz.Length;
                    Console.WriteLine($"[tap] {hunter} {at.X:0.0},{at.Y:0.0},{at.Z:0.0} {dir} push {push:0.00} hold {hold}: peak {peak:0.000} moved {d:0.00} stop f{stop}"
                        + $" slip {s0}->{(int)slip.GetValue(me)!} cam {(me.MorphCamera != null ? "room" : "follow")} alt {me.IsAltForm}");
                }
            }
        }

        private static void Stuck(string at, string facing, string stick)
        {
            float[] Parse(string t) => Array.ConvertAll(t.Split(','), x => float.Parse(x, System.Globalization.CultureInfo.InvariantCulture));
            float[] v = Parse(at), fc = Parse(facing), st = Parse(stick);
            var spot = new OpenTK.Mathematics.Vector3(v[0], v[1] + 0.1f, v[2]);
            PlayerEntity.AltEdgePushOff = Environment.GetEnvironmentVariable("ALTHOP_EDGEOFF") == "1";
            Hunter hunter = Enum.TryParse(Environment.GetEnvironmentVariable("ALTHOP_HUNTER"), out Hunter eh) ? eh : Hunter.Sylux;
            using CampaignHost host = CampaignHost.Start("UNIT4_RM1", hunter: hunter, arriving: false);
            PlayerEntity me = Settle(host);
            Morph(host, me);
            me.Teleport(spot, new OpenTK.Mathematics.Vector3(fc[0], 0, fc[1]), host.Scene.GetNodeRefContaining(spot));
            me.PrevPosition = spot;
            for (int f = 0; f < 60; f++)
            {
                host.Step(default);
            }
            if (!me.IsAltForm)
            {
                Morph(host, me);
            }
            for (int f = 0; f < 240; f++)
            {
                OpenTK.Mathematics.Vector3 before = me.Position, spBefore = me.Speed;
                host.Step(new CampaignInput { Move = new System.Numerics.Vector2(st[0], st[1]) });
                if (int.TryParse(Environment.GetEnvironmentVariable("ALTHOP_COLAT"), out int colAt) && Math.Abs(f - colAt) <= 1)
                {
                    PrintContacts(me, hunter, host.Scene, before, spBefore, f);
                }
                if (f % 10 == 0 || Environment.GetEnvironmentVariable("ALTHOP_DEBUG") == "1")
                {
                    Console.WriteLine($"[stuck] {f} pos {me.Position.X:0.000},{me.Position.Y:0.000},{me.Position.Z:0.000} speed {me.Speed.X:0.000},{me.Speed.Y:0.000},{me.Speed.Z:0.000} alt {me.IsAltForm}");
                }
            }
            PlayerEntity.AltEdgePushOff = false;
        }

        // -althoptest lipscan: Sylux (HUNTERS picks another) on the arena in ALTHOP_ARENA (default MP9 CRYOCHASM, the
        // multiplayer Ice Hive): rests the alt form at floor spots next to a step and prints how much it still bobs
        // (peak to peak height, frames 300-600) so the same spots can be logged in vanilla (owner 2026-11-08: Lockjaw
        // bobs on lips)
        private static void LipScan()
        {
            string arena = Environment.GetEnvironmentVariable("ALTHOP_ARENA") ?? "MP9 CRYOCHASM";
            Hunter hunter = (Hunter)((Environment.GetEnvironmentVariable("HUNTERS") ?? "3")[0] - '0');
            using CampaignHost host = CombatHall(hunter, arena);
            Scene scene = host.Scene;
            PlayerEntity me = PlayerEntity.Players[0];
            for (int f = 0; f < 600; f++)
            {
                Park();
                host.Step(default);
            }
            host.Step(default); // the match spawns the player on this step
            float Floor(float x, float z, float top)
            {
                var res = new CollisionResult();
                return CollisionDetection.CheckBetweenPoints(new OpenTK.Mathematics.Vector3(x, top, z), new OpenTK.Mathematics.Vector3(x, top - 6, z),
                    TestFlags.None, scene, ref res) && res.Plane.Y > 0.9f ? res.Position.Y : float.NaN;
            }
            float step = float.TryParse(Environment.GetEnvironmentVariable("ALTHOP_GRID"), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out float g) ? g : 2;
            int tried = 0, max = int.TryParse(Environment.GetEnvironmentVariable("ALTHOP_MAX"), out int mx) ? mx : 150;
            for (float x = -40; x <= 40 && tried < max; x += step)
            {
                for (float z = -40; z <= 40 && tried < max; z += step)
                {
                    for (float top = 40; top > -10 && tried < max; top -= 6)
                    {
                        float y = Floor(x, z, top);
                        if (float.IsNaN(y)) continue;
                        // a step 0.1-0.8 up or down within half a unit
                        bool lip = false;
                        for (int d = 0; d < 8 && !lip; d++)
                        {
                            float a = d * MathF.PI / 4;
                            float ny = Floor(x + MathF.Cos(a) * 0.5f, z + MathF.Sin(a) * 0.5f, y + 1.5f);
                            lip = !float.IsNaN(ny) && MathF.Abs(ny - y) is > 0.1f and < 0.8f;
                        }
                        if (!lip) continue;
                        string at = string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{x:0.00},{z:0.00}");
                        string? only = Environment.GetEnvironmentVariable("ALTHOP_ONLY"); // x,z: that spot alone, every frame printed
                        if (only != null && only != at) continue;
                        tried++;
                        var spot = new OpenTK.Mathematics.Vector3(x, y + 0.6f, z);
                        me.Teleport(spot, new OpenTK.Mathematics.Vector3(0, 0, -1), scene.GetNodeRefContaining(spot));
                        me.PrevPosition = spot;
                        me.Speed = OpenTK.Mathematics.Vector3.Zero;
                        if (!me.IsAltForm)
                        {
                            Morph(host, me);
                            me.Teleport(spot, new OpenTK.Mathematics.Vector3(0, 0, -1), scene.GetNodeRefContaining(spot));
                            me.PrevPosition = spot;
                            me.Speed = OpenTK.Mathematics.Vector3.Zero;
                        }
                        float lo = 99, hi = -99;
                        for (int f = 0; f < 600; f++)
                        {
                            Park();
                            host.Step(default);
                            if (only != null)
                            {
                                Console.WriteLine(string.Create(System.Globalization.CultureInfo.InvariantCulture,
                                    $"[lipframe] {f} {me.Position.Y:0.00000} {me.Speed.Y:0.00000}"));
                            }
                            if (f >= 300)
                            {
                                lo = MathF.Min(lo, me.Position.Y);
                                hi = MathF.Max(hi, me.Position.Y);
                            }
                        }
                        Console.WriteLine(string.Create(System.Globalization.CultureInfo.InvariantCulture,
                            $"[lipscan] {x:0.00},{y + 0.6f:0.000},{z:0.00} -> rest {me.Position.X:0.000},{me.Position.Y:0.000},{me.Position.Z:0.000} pp {hi - lo:0.0000} alt {me.IsAltForm}"));
                    }
                }
            }
        }

        // -althoptest cross: Sylux (HUNTERS picks another) on ALTHOP_ARENA (default MP9 CRYOCHASM) driven at each step in
        // ALTHOP_SPOTS (x,z;x,z) from 2.5 units back, up/down/left/right for 120 frames then released for 60, every frame
        // printed, facing ALTHOP_FACE (vanilla's facing after morphing in the BizHawk state) so vanilla can drive the same runs
        private static void Cross()
        {
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            float[] Parse(string t) => Array.ConvertAll(t.Split(','), x => float.Parse(x, inv));
            string arena = Environment.GetEnvironmentVariable("ALTHOP_ARENA") ?? "MP9 CRYOCHASM";
            Hunter hunter = (Hunter)((Environment.GetEnvironmentVariable("HUNTERS") ?? "3")[0] - '0');
            float[] fc = Parse(Environment.GetEnvironmentVariable("ALTHOP_FACE") ?? "0.65356,-0.75684");
            var face = new OpenTK.Mathematics.Vector3(fc[0], 0, fc[1]).Normalized();
            var side = new OpenTK.Mathematics.Vector3(-face.Z, 0, face.X);
            using CampaignHost host = CombatHall(hunter, arena);
            Scene scene = host.Scene;
            PlayerEntity me = PlayerEntity.Players[0];
            for (int f = 0; f < 600; f++)
            {
                Park();
                host.Step(default);
            }
            host.Step(default); // the match spawns the player on this step
            float Floor(float x, float z, float top)
            {
                var res = new CollisionResult();
                return CollisionDetection.CheckBetweenPoints(new OpenTK.Mathematics.Vector3(x, top, z), new OpenTK.Mathematics.Vector3(x, top - 6, z),
                    TestFlags.None, scene, ref res) && res.Plane.Y > 0.9f ? res.Position.Y : float.NaN;
            }
            void Put(OpenTK.Mathematics.Vector3 at)
            {
                me.Teleport(at, face, scene.GetNodeRefContaining(at));
                me.PrevPosition = at;
                me.Speed = OpenTK.Mathematics.Vector3.Zero;
            }
            var sticks = new (string Name, System.Numerics.Vector2 Stick, OpenTK.Mathematics.Vector3 Way)[]
            {
                ("up", new(0, 1), face), ("down", new(0, -1), -face), ("right", new(1, 0), side), ("left", new(-1, 0), -side),
            };
            int c = 0;
            foreach (string sp in (Environment.GetEnvironmentVariable("ALTHOP_SPOTS") ?? "8,2").Split(';'))
            {
                float[] xz = Parse(sp);
                foreach ((string name, System.Numerics.Vector2 stick, OpenTK.Mathematics.Vector3 way) in sticks)
                {
                    c++;
                    float sx = xz[0] - way.X * 2.5f, sz = xz[1] - way.Z * 2.5f;
                    float top = 40, y = float.NaN;
                    for (; top > -10 && float.IsNaN(y); top -= 3)
                    {
                        y = Floor(sx, sz, top);
                    }
                    if (float.IsNaN(y)) continue;
                    var start = new OpenTK.Mathematics.Vector3(sx, y + 0.6f, sz);
                    Put(start);
                    if (!me.IsAltForm)
                    {
                        Morph(host, me);
                        Put(start);
                    }
                    for (int f = 0; f < 60; f++)
                    {
                        Park();
                        host.Step(default);
                    }
                    Console.WriteLine(string.Create(inv, $"[crossstart] {c} {name} {start.X:0.0000},{start.Y:0.0000},{start.Z:0.0000}"));
                    int.TryParse(Environment.GetEnvironmentVariable("ALTHOP_COLAT"), out int colAt); // frame whose contacts are printed
                    for (int f = 1; f <= 180; f++)
                    {
                        Park();
                        OpenTK.Mathematics.Vector3 before = me.Position;
                        OpenTK.Mathematics.Vector3 spBefore = me.Speed;
                        host.Step(new CampaignInput { Move = f <= 120 ? stick : default });
                        if (colAt != 0 && Math.Abs(f - colAt) <= 1)
                        {
                            PrintContacts(me, hunter, scene, before, spBefore, f);
                        }
                        Console.WriteLine(string.Create(inv,
                            $"[cross] {c},{name},{f},{me.Position.X:0.00000},{me.Position.Y:0.00000},{me.Position.Z:0.00000},{me.Speed.Xz.Length:0.00000},{me.Speed.X:0.00000},{me.Speed.Y:0.00000},{me.Speed.Z:0.00000}"));
                    }
                }
            }
        }

        // every contact of the alt form's sweep this frame, with HandleCollision's face/edge depth at the pre-collision position
        private static void PrintContacts(PlayerEntity me, Hunter hunter, Scene scene, OpenTK.Mathematics.Vector3 before, OpenTK.Mathematics.Vector3 spBefore, int f)
        {
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            CollisionVolume vol = PlayerEntity.PlayerVolumes[(int)hunter, 2];
            float rad = vol.SphereRadius + 0.5f;
            var hits = new CollisionResult[40];
            int n = CollisionDetection.CheckSphereBetweenPoints(before + vol.SpherePosition, me.Position + vol.SpherePosition, rad,
                limit: 40, includeOffset: true, TestFlags.Players, scene, hits);
            for (int i = 0; i < n; i++)
            {
                CollisionResult h = hits[i];
                Console.WriteLine(string.Create(inv, $"[col] f{f} kind {h.Field0} plane {h.Plane.X:0.000},{h.Plane.Y:0.000},{h.Plane.Z:0.000},{h.Plane.W:0.000} "
                    + $"e1 {h.EdgePoint1.X:0.000},{h.EdgePoint1.Y:0.000},{h.EdgePoint1.Z:0.000} e2 {h.EdgePoint2.X:0.000},{h.EdgePoint2.Y:0.000},{h.EdgePoint2.Z:0.000} "
                    + $"centre {me.Position.Y + vol.SpherePosition.Y:0.000} r {vol.SphereRadius:0.000}"));
                if (h.Field0 == 0)
                {
                    float altRad0 = Fixed.ToFloat(me.Values.AltColRadius);
                    OpenTK.Mathematics.Vector3 altPos0 = (before + spBefore).AddY(Fixed.ToFloat(me.Values.AltColYPos));
                    Console.WriteLine(string.Create(inv, $"[face] f{f} n {h.Plane.X:0.000},{h.Plane.Y:0.000},{h.Plane.Z:0.000} v2 {altRad0 + h.Plane.W - OpenTK.Mathematics.Vector3.Dot(altPos0, h.Plane.Xyz):0.0000} "
                        + $"spdot {OpenTK.Mathematics.Vector3.Dot(spBefore, h.Plane.Xyz):0.0000}"));
                }
                if (h.Field0 == 1)
                {
                    // HandleCollision's alt-form edge depth at the frame's start position, and the speed it would take
                    float altRad = Fixed.ToFloat(me.Values.AltColRadius);
                    OpenTK.Mathematics.Vector3 altPos = (before + spBefore).AddY(Fixed.ToFloat(me.Values.AltColYPos));
                    OpenTK.Mathematics.Vector3 edge = h.EdgePoint2 - h.EdgePoint1;
                    float div = Math.Clamp(OpenTK.Mathematics.Vector3.Dot(altPos - h.EdgePoint1, edge) / edge.LengthSquared, 0, 1);
                    OpenTK.Mathematics.Vector3 btw = altPos - (h.EdgePoint1 + edge * div);
                    float mag = btw.Length;
                    Console.WriteLine(string.Create(inv, $"[edgeall] f{f} mag {mag:0.0000} altRad {altRad:0.0000} altPosY {altPos.Y:0.000}"));
                    if (mag < altRad && mag > 0)
                    {
                        float x = OpenTK.Mathematics.Vector3.Dot(btw / mag, h.Plane.Xyz) * (altRad - mag);
                        Console.WriteLine(string.Create(inv, $"[edge] f{f} pen {altRad - mag:0.0000} x {x:0.00000} v2 {x * x:0.0000000} fx {(int)(x * 4096) * (int)(x * 4096) >> 12} "
                            + $"btwY {btw.Y / mag:0.000} spdot {OpenTK.Mathematics.Vector3.Dot(spBefore, h.Plane.Xyz):0.0000}"));
                    }
                }
            }
        }

        private static CampaignHost CombatHall(Hunter hunter, string arena = "Combat Hall")
        {
            var settings = new MatchSettings { Mode = GameMode.Battle, Arena = MatchArenas.Find(arena)!.Name };
            settings.Players.Add(new MatchPlayer { Hunter = hunter, Nickname = "Player1" });
            settings.Players.Add(new MatchPlayer { Hunter = Hunter.Spire, Bot = true });
            return CampaignHost.StartMatch(settings);
        }

        // the bot parked far off, as the BizHawk runs do
        private static void Park()
        {
            PlayerEntity bot = PlayerEntity.Players[1];
            var far = new OpenTK.Mathematics.Vector3(-40, 0.5f, -40);
            bot.Position = far;
            bot.PrevPosition = far;
            bot.Speed = OpenTK.Mathematics.Vector3.Zero;
        }

        // -althoptest spot: Combat Hall's flattest open floor (for the vanilla-vs-MphRead alt-form runs)
        private static void Spot()
        {
            using CampaignHost host = CombatHall(Hunter.Samus);
            Scene scene = host.Scene;
            float Floor(float x, float z)
            {
                var res = new CollisionResult();
                return CollisionDetection.CheckBetweenPoints(new OpenTK.Mathematics.Vector3(x, 30, z), new OpenTK.Mathematics.Vector3(x, -30, z),
                    TestFlags.None, scene, ref res) && res.Plane.Y > 0.99f ? res.Position.Y : float.NaN;
            }
            var spots = new System.Collections.Generic.List<(float Score, float X, float Y, float Z, float Flat, float Clear)>();
            for (float x = -40; x <= 40; x += 1)
            {
                for (float z = -40; z <= 40; z += 1)
                {
                    float y = Floor(x, z);
                    if (float.IsNaN(y)) continue;
                    float flat = 0;
                    for (float r = 1; r <= 20; r += 1)
                    {
                        bool ok = true;
                        for (int d = 0; d < 16 && ok; d++)
                        {
                            float a = d * MathF.PI / 8;
                            float fy = Floor(x + MathF.Cos(a) * r, z + MathF.Sin(a) * r);
                            ok = !float.IsNaN(fy) && MathF.Abs(fy - y) < 0.02f;
                        }
                        if (!ok) break;
                        flat = r;
                    }
                    float clear = 99;
                    for (int d = 0; d < 16; d++)
                    {
                        float a = d * MathF.PI / 8;
                        var p1 = new OpenTK.Mathematics.Vector3(x, y + 0.5f, z);
                        var p2 = p1 + new OpenTK.Mathematics.Vector3(MathF.Cos(a), 0, MathF.Sin(a)) * 25;
                        var res = new CollisionResult();
                        if (CollisionDetection.CheckBetweenPoints(p1, p2, TestFlags.None, scene, ref res))
                        {
                            clear = MathF.Min(clear, (res.Position - p1).Length);
                        }
                    }
                    if (Environment.GetEnvironmentVariable("ALTHOP_LANE") == "1")
                    {
                        // the longest flat, open straight run from here, and which way (16 headings)
                        for (int d = 0; d < 16; d++)
                        {
                            float a = d * MathF.PI / 8;
                            float run = 0;
                            for (float r = 0.5f; r <= 30; r += 0.5f)
                            {
                                bool ok = true;
                                for (float side = -1.5f; side <= 1.5f && ok; side += 1.5f)
                                {
                                    float fy = Floor(x + MathF.Cos(a) * r - MathF.Sin(a) * side, z + MathF.Sin(a) * r + MathF.Cos(a) * side);
                                    ok = !float.IsNaN(fy) && MathF.Abs(fy - y) < 0.02f;
                                }
                                if (!ok) break;
                                run = r;
                            }
                            var p1 = new OpenTK.Mathematics.Vector3(x, y + 0.5f, z);
                            var res = new CollisionResult();
                            if (CollisionDetection.CheckBetweenPoints(p1, p1 + new OpenTK.Mathematics.Vector3(MathF.Cos(a), 0, MathF.Sin(a)) * 30, TestFlags.None, scene, ref res))
                            {
                                run = MathF.Min(run, (res.Position - p1).Length);
                            }
                            spots.Add((run, x, y, z, MathF.Cos(a), MathF.Sin(a)));
                        }
                        continue;
                    }
                    spots.Add((MathF.Min(flat, clear), x, y, z, flat, clear));
                }
            }
            spots.Sort((a, b) => b.Score.CompareTo(a.Score));
            for (int i = 0; i < Math.Min(12, spots.Count); i++)
            {
                Console.WriteLine($"[spot] run {spots[i].Score:0.0} at {spots[i].X},{spots[i].Y:0.000},{spots[i].Z} flat r {spots[i].Flat} clear {spots[i].Clear:0.0}");
            }
        }

        // -althoptest parity <x,y,z> <facing x,z>: the vanilla BizHawk battery (scratch battery.lua) in MphRead, Combat
        // Hall, every hunter: morph on the spot and rest 900 frames, then from there: forward 60 frames and let go, forward
        // taps of 2/4/8 frames, forward 30 then right 60, forward+right, left, back. CSV on stdout as the Lua's.
        // ALTHOP_CAMPAIGN=1 turns on the campaign's stick scaling (HostAltCamMove), ALTHOP_PUSH the stick's length
        private static void Parity(string at, string facing)
        {
            float[] Parse(string t) => Array.ConvertAll(t.Split(','), x => float.Parse(x, System.Globalization.CultureInfo.InvariantCulture));
            float[] v = Parse(at), fc = Parse(facing);
            var spot = new OpenTK.Mathematics.Vector3(v[0], v[1], v[2]);
            var face = new OpenTK.Mathematics.Vector3(fc[0], 0, fc[1]).Normalized();
            bool campaign = Environment.GetEnvironmentVariable("ALTHOP_CAMPAIGN") == "1";
            float push = float.TryParse(Environment.GetEnvironmentVariable("ALTHOP_PUSH"), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out float pu) ? pu : 1;
            string only = Environment.GetEnvironmentVariable("HUNTERS") ?? "0123456";
            var segs = new (string Name, int Frames, Func<int, System.Numerics.Vector2> Move)[]
            {
                ("accel", 150, f => f <= 60 ? new(0, 1) : default),
                ("tap2", 60, f => f <= 2 ? new(0, 1) : default),
                ("tap4", 60, f => f <= 4 ? new(0, 1) : default),
                ("tap8", 60, f => f <= 8 ? new(0, 1) : default),
                ("turnR", 120, f => f <= 30 ? new(0, 1) : f <= 90 ? new(1, 0) : default),
                ("upright", 90, f => f <= 60 ? new(0.7071f, 0.7071f) : default),
                ("left", 90, f => f <= 60 ? new(-1, 0) : default),
                ("back", 90, f => f <= 60 ? new(0, -1) : default),
            };
            Console.WriteLine("h,seg,f,fl,px,py,pz,vx,vy,vz,hsm,f70,f74,f78,f7c");
            foreach (char hc in only)
            {
                int h = hc - '0';
                using CampaignHost host = CombatHall((Hunter)h);
                PlayerEntity me = PlayerEntity.Players[0];
                for (int f = 0; f < 600; f++)
                {
                    Park();
                    host.Step(default);
                }
                me.HostAltCamMove = campaign;
                host.Step(default); // the match spawns the player on this step
                me.Teleport(spot, face, host.Scene.GetNodeRefContaining(spot));
                me.PrevPosition = spot;
                void Row(string seg, int f)
                {
                    Console.WriteLine(string.Create(System.Globalization.CultureInfo.InvariantCulture,
                        $"{h},{seg},{f},{(uint)me.Flags1:X8},{me.Position.X:0.00000},{me.Position.Y:0.00000},{me.Position.Z:0.00000},"
                        + $"{me.Speed.X:0.00000},{me.Speed.Y:0.00000},{me.Speed.Z:0.00000},{me.Speed.Xz.Length:0.00000},"
                        + $"{me.FacingVector.X:0.0000},{me.FacingVector.Z:0.0000},{me.CameraInfo.Field48:0.0000},{me.CameraInfo.Field4C:0.0000}"));
                }
                for (int f = 1; f <= 900; f++)
                {
                    Park();
                    host.Step(new CampaignInput { Buttons = f == 20 ? CampaignButtons.Morph : CampaignButtons.None });
                    Row("rest", f);
                }
                OpenTK.Mathematics.Vector3 rest = me.Position;
                foreach ((string name, int frames, Func<int, System.Numerics.Vector2> move) in segs)
                {
                    me.Teleport(rest, face, host.Scene.GetNodeRefContaining(rest));
                    me.PrevPosition = rest;
                    me.Speed = OpenTK.Mathematics.Vector3.Zero;
                    for (int f = 0; f < 120; f++)
                    {
                        Park();
                        host.Step(default);
                    }
                    for (int f = 1; f <= frames; f++)
                    {
                        Park();
                        host.Step(new CampaignInput { Move = move(f) * push });
                        Row(name, f);
                    }
                }
            }
        }

        // -althoptest tp [room] [x,y,z;...]: the campaign helper teleporters (SceneSetup.GetExtraEntities), default Magma
        // Drop's two: drops the morphed player (ALTHOP_HUNTER, default Trace) on each pad, then prints where it lands and
        // settles, with helpers on and off (owner 2026-11-09: upstream sent these to (-1, 0, 0))
        private static void Tp(string room, string pads)
        {
            float[] Parse(string t) => Array.ConvertAll(t.Split(','), x => float.Parse(x, System.Globalization.CultureInfo.InvariantCulture));
            Hunter hunter = Enum.TryParse(Environment.GetEnvironmentVariable("ALTHOP_HUNTER"), out Hunter eh) ? eh : Hunter.Trace;
            foreach (bool off in new[] { false, true })
            foreach (string pad in pads.Split(';'))
            {
                SceneSetup.AltHuntersHelpersOff = off;
                float[] v = Parse(pad);
                // x,y,z: dropped onto the pad; x,y,z,fx,fy,fz: rolled in from fx,fy,fz (stick held toward the pad)
                var padAt = new OpenTK.Mathematics.Vector3(v[0], v[1], v[2]);
                bool roll = v.Length >= 6;
                var at = roll ? new OpenTK.Mathematics.Vector3(v[3], v[4] + 0.5f, v[5]) : padAt.AddY(0.5f);
                OpenTK.Mathematics.Vector3 facing = roll ? (padAt - at).WithY(0).Normalized() : -OpenTK.Mathematics.Vector3.UnitX;
                using CampaignHost host = CampaignHost.Start(room, hunter: hunter, arriving: false);
                PlayerEntity me = Settle(host);
                Morph(host, me);
                me.Teleport(at, facing, host.Scene.GetNodeRefContaining(at));
                me.PrevPosition = at;
                OpenTK.Mathematics.Vector3 jump = default;
                int jumpF = -1;
                float nearest = float.MaxValue;
                for (int f = 0; f < 180; f++)
                {
                    OpenTK.Mathematics.Vector3 before = me.Position;
                    host.Step(roll && jumpF < 0 ? new CampaignInput { Move = new System.Numerics.Vector2(0, 1) } : default);
                    if (jumpF < 0) nearest = MathF.Min(nearest, (me.Position - padAt).Length);
                    if (jumpF < 0 && (me.Position - before).Length > 2)
                    {
                        jumpF = f;
                        jump = me.Position;
                    }
                }
                OpenTK.Mathematics.Vector3 p = me.Position;
                Console.WriteLine($"[tp] {hunter} {room} helpers {(off ? "off" : "on ")} pad {v[0]:0.00},{v[1]:0.00},{v[2]:0.00}"
                    + (roll ? $" rolled from {v[3]:0.00},{v[4]:0.00},{v[5]:0.00} (nearest {nearest:0.00})" : "") + ": "
                    + (jumpF < 0 ? "no teleport" : $"teleported f{jumpF} to {jump.X:0.00},{jump.Y:0.00},{jump.Z:0.00}")
                    + $", rests at {p.X:0.00},{p.Y:0.00},{p.Z:0.00} speed {me.Speed.Length:0.000} alt {me.IsAltForm} health {me.Health}");
            }
            SceneSetup.AltHuntersHelpersOff = false;
        }

        // -althoptest climb x,y,z: Magma Drop (ALTHOP_ROOM), hop off unless ALTHOP_HOP=1, morphed at x,y,z facing ALTHOP_FACE
        // (fx,fz, default 0,-1), stick held forward (ALTHOP_MOVE mx,my) for ALTHOP_FRAMES; prints the path every 15 frames
        private static void Climb(string spot)
        {
            float[] Parse(string t) => Array.ConvertAll(t.Split(','), x => float.Parse(x, System.Globalization.CultureInfo.InvariantCulture));
            Hunter hunter = Enum.TryParse(Environment.GetEnvironmentVariable("ALTHOP_HUNTER"), out Hunter eh) ? eh : Hunter.Trace;
            string room = Environment.GetEnvironmentVariable("ALTHOP_ROOM") ?? "UNIT1_C4";
            SceneSetup.CampaignAltHop = Environment.GetEnvironmentVariable("ALTHOP_HOP") == "1";
            float[] v = Parse(spot);
            float[] fc = Parse(Environment.GetEnvironmentVariable("ALTHOP_FACE") ?? "0,-1");
            float[] mv = Parse(Environment.GetEnvironmentVariable("ALTHOP_MOVE") ?? "0,1");
            int frames = int.TryParse(Environment.GetEnvironmentVariable("ALTHOP_FRAMES"), out int fr) ? fr : 600;
            using CampaignHost host = CampaignHost.Start(room, hunter: hunter, arriving: false);
            PlayerEntity me = Settle(host);
            Morph(host, me);
            var at = new OpenTK.Mathematics.Vector3(v[0], v[1], v[2]);
            me.Teleport(at, new OpenTK.Mathematics.Vector3(fc[0], 0, fc[1]).Normalized(), host.Scene.GetNodeRefContaining(at));
            me.PrevPosition = at;
            // (a room's intro cutscene, e.g. Ice Hive's puzzle intro, forces biped: let it play, then morph and place again)
            for (int f = 0; f < 30 || MphRead.Formats.CameraSequence.Current != null && f < 1800; f++) host.Step(default);
            if (!me.IsAltForm) Morph(host, me);
            me.Teleport(at, new OpenTK.Mathematics.Vector3(fc[0], 0, fc[1]).Normalized(), host.Scene.GetNodeRefContaining(at));
            me.PrevPosition = at;
            if (Environment.GetEnvironmentVariable("ALTHOP_PRESS") is string press)
            {
                for (int f = 0; f < 30; f++) host.Step(default);
                OpenTK.Mathematics.Vector3 rest = me.Position;
                (float peak, int start) = Rise(host, me, press == "bomb" ? CampaignButtons.AltAttack : CampaignButtons.Jump, 150);
                Console.WriteLine($"[climb] {hunter} {press} at {rest.X:0.00},{rest.Y:0.00},{rest.Z:0.00}: rise {peak:0.00} from f{start}");
                SceneSetup.CampaignAltHop = true;
                return;
            }
            float top = at.Y;
            // ALTHOP_SCRIPT "frame:mx,my;frame:mx,my;...": the stick from each frame on (e.g. let go once a pad has launched you)
            var script = new System.Collections.Generic.List<(int Frame, float[] Move)>();
            foreach (string part in (Environment.GetEnvironmentVariable("ALTHOP_SCRIPT") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                string[] fm = part.Split(':');
                script.Add((int.Parse(fm[0]), Parse(fm[1])));
            }
            float[] m = mv;
            for (int f = 0; f < frames; f++)
            {
                foreach ((int sf, float[] smv) in script)
                {
                    if (sf == f) m = smv;
                }
                // (a pickup's message pauses the room; close it at once, as a player would, keeping the frame count)
                if (host.DialogPaused)
                {
                    Console.WriteLine($"[climb] f{f} dialog {host.Dialog}");
                    for (int k = 0; k < 600 && host.DialogPaused; k++)
                        host.Step(new CampaignInput { DialogButton = k % 10 != 0 ? HostDialogButton.None
                            : host.Dialog == DialogType.YesNo ? HostDialogButton.No : HostDialogButton.Advance });
                }
                host.Step(new CampaignInput { Move = new System.Numerics.Vector2(m[0], m[1]) });
                top = MathF.Max(top, me.Position.Y);
                if (f % 15 == 0 || f == frames - 1)
                {
                    OpenTK.Mathematics.Vector3 p = me.Position;
                    Console.WriteLine($"[climb] {hunter} f{f} {p.X:0.00},{p.Y:0.00},{p.Z:0.00} speed {me.Speed.X:0.00},{me.Speed.Y:0.00},{me.Speed.Z:0.00} alt {me.IsAltForm} hp {me.Health}");
                }
            }
            Console.WriteLine($"[climb] {hunter} hop {me.HostAltHop} top y {top:0.00}");
            SceneSetup.CampaignAltHop = true;
        }

        // -althoptest padswitch: SETTINGS "alt form hop" (owner 2026-11-09). On: the player has the hop, no helper jump pads
        // (ids 30000 up) and Sic Transit's energy tank at vanilla height. Off: no hop, MphRead's pads in every room that had
        // them, and the tank 1.5 lower.
        private static void PadSwitch()
        {
            (int Room, Hunter Hunter, int Pads)[] cases =
            {
                (28, Hunter.Trace, 1), (67, Hunter.Trace, 2), (67, Hunter.Spire, 1), (67, Hunter.Weavel, 1),
                (78, Hunter.Noxus, 3), (78, Hunter.Weavel, 1), (78, Hunter.Trace, 0), (80, Hunter.Noxus, 1),
                (80, Hunter.Spire, 1), (80, Hunter.Trace, 0), (30, Hunter.Trace, 5), (30, Hunter.Spire, 2), (30, Hunter.Sylux, 0),
                (38, Hunter.Trace, 5), (38, Hunter.Weavel, 2), (79, Hunter.Trace, 0), (27, Hunter.Kanden, 0),
            };
            bool pass = true;
            foreach (bool hop in new[] { true, false })
            {
                SceneSetup.CampaignAltHop = hop;
                foreach ((int roomId, Hunter hunter, int padsOff) in cases)
                {
                    string room = System.Linq.Enumerable.First(Metadata.RoomList, r => r.Id == roomId).Name;
                    using CampaignHost host = CampaignHost.Start(room, hunter: hunter, arriving: false);
                    PlayerEntity me = PlayerEntity.Players[0];
                    int pads = 0;
                    float tankY = float.NaN;
                    foreach (EntityBase e in host.Scene.Entities)
                    {
                        if (e.Type == EntityType.JumpPad && e.Id >= 30000) pads++;
                        if (roomId == 79 && e.Type == EntityType.ItemSpawn && e.Id == 29) tankY = e.Position.Y;
                        if (roomId == 80 && e.Type == EntityType.ItemSpawn && e.Id == 31) tankY = e.Position.Y;
                    }
                    int want = hop ? 0 : padsOff;
                    bool ok = pads == want && me.HostAltHop == hop;
                    string tank = "";
                    if (roomId == 80 && hunter == Hunter.Trace)
                    {
                        float wantY = hop ? 4.10f : 0f;
                        ok &= MathF.Abs(tankY - wantY) < 0.01f;
                        tank = $", key y {tankY:0.00} (want {wantY:0.00})";
                    }
                    if (roomId == 79)
                    {
                        float wantY = hop ? 8.24f : 6.74f;
                        ok &= MathF.Abs(tankY - wantY) < 0.01f;
                        tank = $", tank y {tankY:0.00} (want {wantY:0.00})";
                    }
                    pass &= ok;
                    Console.WriteLine($"[padswitch] hop {(hop ? "on " : "off")} {hunter,-6} {room,-10} pads {pads} (want {want}), hop flag {me.HostAltHop}{tank} {(ok ? "ok" : "FAIL")}");
                }
            }
            SceneSetup.CampaignAltHop = true;
            Console.WriteLine(pass ? "[padswitch] ALL OK" : "[padswitch] FAILED");
        }

        // -althoptest ents [room]: the room's spawn, doors, teleporters, jump pads and area volumes, for picking launch spots
        private static void Ents(string room)
        {
            Hunter hunter = Enum.TryParse(Environment.GetEnvironmentVariable("ALTHOP_HUNTER"), out Hunter eh) ? eh : Hunter.Trace;
            using CampaignHost host = CampaignHost.Start(room, hunter: hunter, arriving: false);
            PlayerEntity me = Settle(host);
            Console.WriteLine($"[ents] {hunter} {room} spawn rests at {me.Position.X:0.00},{me.Position.Y:0.00},{me.Position.Z:0.00}");
            foreach (EntityBase e in host.Scene.Entities)
            {
                if (e.Type is EntityType.Door or EntityType.Teleporter or EntityType.JumpPad or EntityType.AreaVolume or EntityType.PlayerSpawn
                    or EntityType.ForceField or EntityType.Platform or EntityType.TriggerVolume or EntityType.Object or EntityType.ItemSpawn)
                {
                    string extra = "";
                    var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
                    if (e is TeleporterEntity)
                    {
                        int target = (int)typeof(TeleporterEntity).GetField("_targetRoomId", flags)!.GetValue(e)!;
                        var tp = (OpenTK.Mathematics.Vector3)typeof(TeleporterEntity).GetField("_targetPos", flags)!.GetValue(e)!;
                        extra = $"-> room {(target == -1 ? "same" : System.Linq.Enumerable.FirstOrDefault(Metadata.RoomList, r => r.Id == target)?.Name)} at {tp.X:0.00},{tp.Y:0.00},{tp.Z:0.00}";
                    }
                    else if (e is AreaVolumeEntity av)
                    {
                        object d = av.Data;
                        var parts = new System.Collections.Generic.List<string>();
                        foreach (System.Reflection.FieldInfo f in d.GetType().GetFields())
                        {
                            if (f.Name != "Header") parts.Add($"{f.Name}={f.GetValue(d)}");
                        }
                        RawCollisionVolume vol = av.Data.Volume;
                        parts.Add(vol.Type == VolumeType.Box
                            ? $"box pos={vol.BoxPosition.ToFloatVector()} v1={vol.BoxVector1.ToFloatVector()}*{vol.BoxDot1.FloatValue} v2={vol.BoxVector2.ToFloatVector()}*{vol.BoxDot2.FloatValue} v3={vol.BoxVector3.ToFloatVector()}*{vol.BoxDot3.FloatValue}"
                            : $"{vol.Type} cylpos={vol.CylinderPosition.ToFloatVector()} cylvec={vol.CylinderVector.ToFloatVector()} r={vol.CylinderRadius.FloatValue}");
                        extra = string.Join(" ", parts) + $" active={av.Active}";
                    }
                    Console.WriteLine($"[ents] {e.Type} {e.Id} at {e.Position.X:0.00},{e.Position.Y:0.00},{e.Position.Z:0.00} face {e.FacingVector.X:0.00},{e.FacingVector.Z:0.00} {extra}");
                }
                else
                {
                    Console.WriteLine($"[ents] {e.Type} {e.Id} at {e.Position.X:0.00},{e.Position.Y:0.00},{e.Position.Z:0.00} (other)");
                }
                if (e is ObjectEntity oe && System.Environment.GetEnvironmentVariable("ALTHOP_OBJ") == "1")
                {
                    ObjectEntityData od = oe.Data;
                    Console.WriteLine($"[ents]   object model {od.ModelId} linked {od.LinkedEntity} effect {od.EffectId} flags {od.EffectFlags} interval {od.EffectInterval} on 0x{od.EffectOnIntervals:X} {od.Flags}");
                }
                if (e is ItemSpawnEntity isp)
                {
                    object d = isp.Data;
                    var parts = new System.Collections.Generic.List<string>();
                    foreach (System.Reflection.FieldInfo f in d.GetType().GetFields())
                    {
                        if (f.Name != "Header") parts.Add($"{f.Name}={f.GetValue(d)}");
                    }
                    Console.WriteLine($"[ents]   item {string.Join(" ", parts)}");
                }
            }
        }

        // -althoptest jump <room> x,y,z[;x,y,z]: biped dropped at each point, then Jump pressed; prints the climb (ALTHOP_HUNTER)
        // -althoptest face: Trace and Sylux (ALTHOP_HUNTER picks one) arrive in Magma Drop at its spawn point, which
        // SceneSetup flips for them (owner 2026-11-09), so they face away from the side view's screen and holding right
        // moves them to the screen's right. ALTHOP_HELPERS=off is vanilla: facing the screen, right goes left.
        private static void Face()
        {
            SceneSetup.AltHuntersHelpersOff = Environment.GetEnvironmentVariable("ALTHOP_HELPERS") == "off";
            var facingField = typeof(PlayerEntity).GetField("_facingVector", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
            bool pass = true;
            Hunter[] hunters = Enum.TryParse(Environment.GetEnvironmentVariable("ALTHOP_HUNTER"), out Hunter eh) ? new[] { eh } : new[] { Hunter.Trace, Hunter.Sylux };
            foreach (Hunter hunter in hunters)
            {
                using CampaignHost host = CampaignHost.Start("UNIT1_C4", hunter: hunter, arriving: true);
                PlayerEntity me = PlayerEntity.Players[0];
                OpenTK.Mathematics.Vector3 spawnFacing = default;
                for (int f = 0; f < 120; f++)
                {
                    for (int k = 0; k < 600 && host.DialogPaused; k++)
                        host.Step(new CampaignInput { DialogButton = k % 10 != 0 ? HostDialogButton.None
                            : host.Dialog == DialogType.YesNo ? HostDialogButton.No : HostDialogButton.Advance });
                    host.Step(default);
                    if (f == 1) spawnFacing = (OpenTK.Mathematics.Vector3)facingField.GetValue(me)!;
                }
                var seq = MphRead.Formats.CameraSequence.Current;
                if (seq?.CamInfoRef == null)
                {
                    Console.WriteLine($"[face] {hunter}: no camera sequence (alt {me.IsAltForm}) FAIL");
                    pass = false;
                    continue;
                }
                OpenTK.Mathematics.Vector3 view = seq.CamInfoRef.Target - seq.CamInfoRef.Position;
                view = new OpenTK.Mathematics.Vector3(view.X, 0, view.Z).Normalized();
                var camRight = OpenTK.Mathematics.Vector3.Cross(view, OpenTK.Mathematics.Vector3.UnitY);
                var facing = (OpenTK.Mathematics.Vector3)facingField.GetValue(me)!;
                facing = new OpenTK.Mathematics.Vector3(facing.X, 0, facing.Z).Normalized();
                OpenTK.Mathematics.Vector3 start = me.Position;
                for (int f = 0; f < 30; f++)
                {
                    host.Step(new CampaignInput { Move = new System.Numerics.Vector2(1, 0) });
                }
                OpenTK.Mathematics.Vector3 moved = me.Position - start;
                float rightward = OpenTK.Mathematics.Vector3.Dot(moved, camRight);
                bool ok = me.IsAltForm && OpenTK.Mathematics.Vector3.Dot(facing, view) > 0.99f && rightward > 0.5f;
                if (SceneSetup.AltHuntersHelpersOff) ok = true; // (comparison only)
                Console.WriteLine($"[face] {hunter}: camseq {seq.SequenceId} alt {me.IsAltForm} spawn facing {spawnFacing.X:0.00},{spawnFacing.Z:0.00}"
                    + $" view {view.X:0.00},{view.Z:0.00} facing {facing.X:0.00},{facing.Z:0.00}; hold right moved {rightward:0.00} screen-right {(ok ? "ok" : "FAIL")}");
                pass &= ok;
            }
            SceneSetup.AltHuntersHelpersOff = false;
            Console.WriteLine(pass ? "PASS althoptest face" : "FAIL althoptest face");
        }

        private static void JumpAt(string room, string points)
        {
            Hunter hunter = Enum.TryParse(Environment.GetEnvironmentVariable("ALTHOP_HUNTER"), out Hunter eh) ? eh : Hunter.Trace;
            foreach (string pt in points.Split(';'))
            {
                float[] v = Array.ConvertAll(pt.Split(','), x => float.Parse(x, System.Globalization.CultureInfo.InvariantCulture));
                var at = new OpenTK.Mathematics.Vector3(v[0], v[1] + 0.5f, v[2]);
                SceneSetup.AltHuntersHelpersOff = Environment.GetEnvironmentVariable("ALTHOP_HELPERS") == "off";
                using CampaignHost host = CampaignHost.Start(room, hunter: hunter, arriving: false);
                PlayerEntity me = Settle(host);
                me.Teleport(at, -OpenTK.Mathematics.Vector3.UnitZ, host.Scene.GetNodeRefContaining(at));
                me.PrevPosition = at;
                void CloseDialogs()
                {
                    for (int k = 0; k < 600 && host.DialogPaused; k++)
                        host.Step(new CampaignInput { DialogButton = k % 10 != 0 ? HostDialogButton.None
                            : host.Dialog == DialogType.YesNo ? HostDialogButton.No : HostDialogButton.Advance });
                }
                string lastState = "";
                void Watch(string tag)
                {
                    var c = MphRead.Formats.CameraSequence.Current;
                    string st = $"camseq {c?.SequenceId} {c?.Flags} pause {host.DialogPaused} dialog {host.Dialog} alt {me.IsAltForm}";
                    if (st != lastState) { Console.WriteLine($"[jump]   {tag}: {st} pos {me.Position}"); lastState = st; }
                }
                for (int f = 0; f < 60; f++) { host.Step(default); Watch($"settle f{f}"); CloseDialogs(); }
                float start = me.Position.Y, top = start;
                int topF = 0;
                for (int f = 0; f < 900; f++)
                {
                    bool press = f < 20 || (f >= 300 && f < 320);
                    if (host.DialogPaused)
                    {
                        var cs = MphRead.Formats.CameraSequence.Current;
                        if (f < 3) Console.WriteLine($"[jump]   f{f} dialog {host.Dialog} up; camseq {cs?.SequenceId} {cs?.Name} {cs?.Flags} menuPause {GameState.MenuPause} pos {me.Position}");
                        CloseDialogs();
                    }
                    host.Step(press ? new CampaignInput { Buttons = CampaignButtons.Jump } : default);
                    Watch($"f{f}");
                    if (Environment.GetEnvironmentVariable("ALTHOP_TRACE") == "1" && (f < 40 || (f >= 295 && f < 340)))
                        Console.WriteLine($"[jump]   f{f} y {me.Position.Y:0.000} vy {me.Speed.Y:0.0000} standing {me.Flags1.TestFlag(PlayerFlags1.Standing)} press {press} flags2 {me.Flags2}");
                    if (Environment.GetEnvironmentVariable("ALTHOP_TRACE") == "2" && f % 5 == 0 && f < 200)
                        Console.WriteLine($"[jump]   f{f} pos {me.Position.X:0.00},{me.Position.Y:0.00},{me.Position.Z:0.00} vy {me.Speed.Y:0.0000} vz {me.Speed.Z:0.000}");
                    if (me.Position.Y > top) { top = me.Position.Y; topF = f; }
                    if (f == 2) Console.WriteLine($"[jump]   f2 speedY {me.Speed.Y:0.0000} gravity {typeof(PlayerEntity).GetField("_gravity", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(me)}");
                }
                OpenTK.Mathematics.Vector3 p = me.Position;
                Console.WriteLine($"[jump] {hunter} {room} at {v[0]:0.00},{v[1]:0.00},{v[2]:0.00}: stood {start:0.00}, top {top:0.00} (+{top - start:0.00}) at f{topF}, ends {p.X:0.00},{p.Y:0.00},{p.Z:0.00} health {me.Health}");
                if (Environment.GetEnvironmentVariable("ALTHOP_AFTER") == "1")
                {
                    // after the exit's room change: is the hop still on, and does it still lift?
                    PlayerEntity now = PlayerEntity.Players[0];
                    Console.WriteLine($"[after] room {GameState.StorySave?.GetType().Name} same seat {ReferenceEquals(now, me)} main {ReferenceEquals(PlayerEntity.Main, now)} hop {now.HostAltHop} cammove {now.HostAltCamMove} ballJump {now.HostBallJump} alt {now.IsAltForm} pos {now.Position}");
                    if (!now.IsAltForm) Morph(host, now);
                    for (int f = 0; f < 120; f++) host.Step(default);
                    int hops = now.AltHops;
                    (float peak, int s) = Rise(host, now, CampaignButtons.Jump, 90);
                    Console.WriteLine($"[after] alt {now.IsAltForm} standing {now.Flags1.TestFlag(PlayerFlags1.Standing)}/{now.Flags1.TestFlag(PlayerFlags1.StandingPrevious)} hops +{now.AltHops - hops} peak {peak:0.000} start f{s} pos {now.Position}");
                }
            }
        }

        // -althoptest restscan: Ice Hive (UNIT4_RM1, or ALTHOP_ROOM) as Sylux (ALTHOP_HUNTER): drops the alt form at every grid
        // spot in ALTHOP_BOX (x0,x1,z0,z1) from each height in ALTHOP_YS, lets go of the stick and prints the spots still
        // bobbing after ALTHOP_REST frames (height peak to peak over the last 300) (owner 2026-11-11: endless bob in the tunnel)
        private static void RestScan()
        {
            float[] Parse(string t) => Array.ConvertAll(t.Split(','), x => float.Parse(x, System.Globalization.CultureInfo.InvariantCulture));
            Hunter hunter = Enum.TryParse(Environment.GetEnvironmentVariable("ALTHOP_HUNTER"), out Hunter eh) ? eh : Hunter.Sylux;
            string room = Environment.GetEnvironmentVariable("ALTHOP_ROOM") ?? "UNIT4_RM1";
            float[] box = Parse(Environment.GetEnvironmentVariable("ALTHOP_BOX") ?? "10,20,-42,-35");
            float[] ys = Parse(Environment.GetEnvironmentVariable("ALTHOP_YS") ?? "36,41,44");
            float step = float.TryParse(Environment.GetEnvironmentVariable("ALTHOP_GRID"), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out float g) ? g : 0.5f;
            int rest = int.TryParse(Environment.GetEnvironmentVariable("ALTHOP_REST"), out int r) ? r : 900;
            float limit = float.TryParse(Environment.GetEnvironmentVariable("ALTHOP_PP"), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out float l) ? l : 0.01f;
            bool all = Environment.GetEnvironmentVariable("ALTHOP_ALL") == "1";
            CampaignHost host = null!;
            PlayerEntity me = null!;
            void Ready()
            {
                host?.Dispose();
                host = CampaignHost.Start(room, hunter: hunter, arriving: false);
                me = Settle(host);
                Morph(host, me);
                var first = new OpenTK.Mathematics.Vector3(14.5f, 41, -38.5f);
                me.Teleport(first, new OpenTK.Mathematics.Vector3(0, 0, -1), host.Scene.GetNodeRefContaining(first));
                me.PrevPosition = first;
                // (Ice Hive's intro cutscene forces biped: let it play, then morph, as Climb does)
                for (int f = 0; f < 30 || MphRead.Formats.CameraSequence.Current != null && f < 1800; f++) host.Step(default);
                if (!me.IsAltForm) Morph(host, me);
            }
            Ready();
            int deaths = 0;
            if (Environment.GetEnvironmentVariable("ALTHOP_TRACE") is string trace)
            {
                // one spot: rest, then print every step's height, speed and spring contacts
                float[] t = Parse(trace);
                var at = new OpenTK.Mathematics.Vector3(t[0], t[1], t[2]);
                me.Teleport(at, new OpenTK.Mathematics.Vector3(0, 0, -1), host.Scene.GetNodeRefContaining(at));
                me.PrevPosition = at;
                for (int f = 0; f < rest; f++) host.Step(default);
                int frame = 0;
                PlayerEntity.AltSpringTrace = line => Console.WriteLine($"[trace] {frame}   {line}");
                for (frame = 0; frame < 120; frame++)
                {
                    host.Step(default);
                    string plat = "";
                    foreach (EntityBase e in host.Scene.Entities)
                    {
                        if (e.Type == EntityType.Platform && (e.Position.Xz - me.Position.Xz).Length < 4)
                        {
                            plat += $" plat {e.Position.X:0.00},{e.Position.Y:0.000},{e.Position.Z:0.00}";
                        }
                    }
                    Console.WriteLine($"[trace] {frame} pos {me.Position.X:0.0000},{me.Position.Y:0.0000},{me.Position.Z:0.0000} speed {me.Speed.X:0.0000},{me.Speed.Y:0.0000},{me.Speed.Z:0.0000}{plat}");
                }
                PlayerEntity.AltSpringTrace = null;
                host.Dispose();
                return;
            }
            int tried = 0, bad = 0;
            for (float x = box[0]; x <= box[1] + 0.001f; x += step)
            {
                for (float z = box[2]; z <= box[3] + 0.001f; z += step)
                {
                    foreach (float y in ys)
                    {
                        var at = new OpenTK.Mathematics.Vector3(x, y, z);
                        if (me.Health == 0)
                        {
                            deaths++; // fell out of the room last spot: start over
                            Ready();
                        }
                        me.Teleport(at, new OpenTK.Mathematics.Vector3(0, 0, -1), host.Scene.GetNodeRefContaining(at));
                        me.PrevPosition = at;
                        for (int f = 0; f < 90; f++) host.Step(default);
                        if (me.Position.Y < y - 8) continue; // nothing under this spot
                        if (!me.IsAltForm) Morph(host, me);
                        float lo = float.MaxValue, hi = float.MinValue;
                        OpenTK.Mathematics.Vector3 mark = default;
                        for (int f = 0; f < rest; f++)
                        {
                            if (host.DialogPaused)
                            {
                                for (int k = 0; k < 600 && host.DialogPaused; k++)
                                    host.Step(new CampaignInput { DialogButton = k % 10 != 0 ? HostDialogButton.None
                                        : host.Dialog == DialogType.YesNo ? HostDialogButton.No : HostDialogButton.Advance });
                            }
                            host.Step(default);
                            if (f == rest - 300) mark = me.Position;
                            if (f >= rest - 300)
                            {
                                lo = MathF.Min(lo, me.Position.Y);
                                hi = MathF.Max(hi, me.Position.Y);
                            }
                        }
                        tried++;
                        float slide = (me.Position.Xz - mark.Xz).Length;
                        // (still rolling, or riding Ice Hive's elevator 47 at 13.81,-38.80: not a rest bob)
                        bool moving = slide > 0.05f || (me.Position.Xz - new OpenTK.Mathematics.Vector2(13.81f, -38.80f)).Length < 1.3f;
                        if (all || hi - lo > limit && !moving)
                        {
                            bad++;
                            Console.WriteLine($"[rest] from {x:0.00},{y:0.00},{z:0.00} -> {me.Position.X:0.000},{me.Position.Y:0.000},{me.Position.Z:0.000}"
                                + $" pp {hi - lo:0.0000} slide {slide:0.000} alt {me.IsAltForm} speedY {me.Speed.Y:0.0000}");
                        }
                    }
                }
            }
            host.Dispose();
            Console.WriteLine($"[rest] {hunter} {room}: {bad} of {tried} spots still bob over {limit} after {rest} frames ({deaths} restarts)");
        }

        private static PlayerEntity Settle(CampaignHost host)
        {
            for (int f = 0; f < 240; f++)
            {
                host.Step(default);
            }
            return PlayerEntity.Players[0];
        }

        private static void Morph(CampaignHost host, PlayerEntity me)
        {
            host.Step(new CampaignInput { Buttons = CampaignButtons.Morph });
            for (int f = 0; f < 300 && !(me.IsAltForm && !me.IsMorphing); f++)
            {
                host.Step(default);
            }
            for (int f = 0; f < 60; f++)
            {
                host.Step(default);
            }
        }

        private static (float Peak, int Start) Rise(CampaignHost host, PlayerEntity me, CampaignButtons press, int frames)
        {
            float y0 = me.Position.Y;
            float peak = 0;
            int start = -1;
            for (int f = 0; f < frames; f++)
            {
                host.Step(new CampaignInput { Buttons = f == 0 ? press : CampaignButtons.None });
                float dy = me.Position.Y - y0;
                if (start < 0 && dy > 0.01f)
                {
                    start = f;
                }
                peak = Math.Max(peak, dy);
            }
            return (peak, start);
        }
    }
}
