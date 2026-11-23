using System;
using System.Linq;
using MphRead.Entities;
using MphRead.Formats;
using MphRecomp.Campaign;
using MphRecomp.Multiplayer;
using OpenTK.Mathematics;

namespace MphRead
{
    // Mid-air deaths on DEATH's RAGDOLL, run by -ragdollair (owner 2026-11-12: "with ragdoll you can fall slowly through
    // the air like a feather instead of falling with gravity"): the main seat's body at death, copied, lifted high over the
    // arena and let go each way a death can go (a plain fall, a jump's, a headshot's tilt, a Judicator freeze, an electric
    // death's shaking hold), then one slid down a wall. Pass: every copy falls as fast as a lone point would under the
    // same gravity and air damping (at least FallShare of it, step for step), none hovers or sleeps in the air, the wall
    // doesn't hold a body up, and the deaths on foot land as they did (printed fingerprints, to compare before and after)
    internal static class RagdollAirTest
    {
        private const float Gravity = 17f / 3600;
        private const float Damping = 0.99f;
        private const float FallShare = 0.85f;
        private const float Lift = 150;

        // -ragdollair [arena=<name>] [freeze] (freeze: the freeze-motion checks alone)
        public static void Run(string[] args)
        {
            if (!ToolPaths.UseDesktopExtraction()) return;
            Paths.ChooseMphPath();
            string[] arenas = args.Where(a => a.StartsWith("arena=")).Select(a => a[6..]).ToArray();
            if (arenas.Length == 0) arenas = new[] { "MP3 PROVING GROUND", "MP1 SANCTORUS", "MP7 PROCESSOR CORE" };
            MphRecomp.Anim.DeathCollapse.Set("ragdoll", Environment.CurrentDirectory);
            bool freezeOnly = args.Contains("freeze");
            int fails = 0;
            foreach (string arena in arenas)
            {
                using CampaignHost host = CampaignHost.StartMatch(MatchSettings.Quick(GameMode.Battle, arena, Hunter.Samus, 7));
                var input = new CampaignInput { SelectWeapon = BeamType.None };
                PlayerEntity.HostRagdollShown = true;
                for (int f = 0; f < 120; f++) host.Step(input);
                for (int f = 0; f < 600 && PlayerEntity.Main.Health <= 0; f++)
                {
                    host.Step(new CampaignInput { SelectWeapon = BeamType.None, Buttons = f % 2 == 0 ? CampaignButtons.Shoot : 0 });
                }
                for (int f = 0; f < 30; f++) host.Step(input);
                PlayerEntity? p = PlayerEntity.Players.Take(PlayerEntity.PlayerCount)
                    .FirstOrDefault(q => q.Health > 0 && !q.IsAltForm && !q.IsMorphing);
                if (p == null)
                {
                    fails++;
                    Console.WriteLine($"  [FAIL] {arena}: no seat alive on foot to kill");
                    continue;
                }
                var facing = new Vector3(p.Field70, 0, p.Field74);
                p.TakeDamage(999u, DamageFlags.IgnoreInvuln, -facing, null);
                if (p.Ragdoll == null)
                {
                    fails++;
                    Console.WriteLine($"  [FAIL] {arena}: no ragdoll at seat {p.SlotIndex}'s death");
                    continue;
                }
                if (!freezeOnly) fails += Run(host, p.Ragdoll.Clone(), facing, arena);
                fails += FreezeMotion(host, p.Ragdoll.Clone(), facing, arena);
                fails += JumpFreeze(host, arena);
            }
            MphRecomp.Anim.DeathCollapse.Set(null, Environment.CurrentDirectory);
            Console.WriteLine(fails == 0 ? "  ALL OK" : $"  {fails} FAILED");
        }

        private static Vector3 Middle(PlayerRagdoll d)
        {
            Vector3 m = Vector3.Zero;
            for (int i = 0; i < PlayerRagdoll.PointCount; i++) m += d.Pos[i];
            return m / PlayerRagdoll.PointCount;
        }

        private static uint Fingerprint(PlayerRagdoll d)
        {
            uint h = 2166136261;
            for (int i = 0; i < PlayerRagdoll.PointCount; i++)
            {
                h = (h ^ (uint)BitConverter.SingleToInt32Bits(d.Pos[i].X)) * 16777619;
                h = (h ^ (uint)BitConverter.SingleToInt32Bits(d.Pos[i].Y)) * 16777619;
                h = (h ^ (uint)BitConverter.SingleToInt32Bits(d.Pos[i].Z)) * 16777619;
            }
            return h;
        }

        private static int Run(CampaignHost host, PlayerRagdoll doll, Vector3 facing, string arena)
        {
            int fails = 0;
            Scene scene = host.Scene;
            Vector3 face = facing.WithY(0);
            face = face.LengthSquared < 1e-8f ? Vector3.UnitZ : face.Normalized();
            Vector3 way = -face, push = way * 0.1f;
            float height = (doll.Start[PlayerRagdoll.HeadTop] - doll.Start[PlayerRagdoll.Pelvis]).Length;

            // (how far the standing body's points are off the floor at death: a hold's support check has to reach this)
            float gap = float.MaxValue;
            for (int i = 0; i < PlayerRagdoll.PointCount; i++)
            {
                var r = new CollisionResult();
                if (CollisionDetection.CheckBetweenPoints(doll.Pos[i], doll.Pos[i] - Vector3.UnitY * 3, TestFlags.None, scene, ref r))
                {
                    gap = MathF.Min(gap, doll.Pos[i].Y - r.Position.Y);
                }
            }
            Console.WriteLine($"  [..] {arena}: body {height:0.000} hips-head, lowest point {gap:0.000} off the floor at death");

            // the deaths on foot, as they land (for before/after)
            PlayerRagdoll Foot(Action<PlayerRagdoll>? how, bool vary)
            {
                PlayerRagdoll c = doll.Clone();
                c.Launch(Vector3.Zero, push);
                if (vary) c.Vary(Hunter.Samus, face, Vector3.Zero, push);
                how?.Invoke(c);
                for (int s = 0; s < 300; s++) c.Step(scene);
                return c;
            }
            var foot = new (string, PlayerRagdoll)[]
            {
                ("plain", Foot(null, false)), ("vary", Foot(null, true)), ("headshot", Foot(d => d.HeadSnap(push), true)),
                ("freeze", Foot(d => d.Freeze(), true)), ("shock", Foot(d => d.Twitch(onFoot: true), true)),
            };
            Console.WriteLine("  [..] on foot: " + string.Join(" ", foot.Select(f =>
                $"{f.Item1}={Fingerprint(f.Item2):x8}{(f.Item2.Asleep ? "" : "(awake)")}")));

            // (high over the arena: clear below for Lift, so nothing is touched in the steps checked)
            var clear = new CollisionResult();
            Vector3 high = Vector3.UnitY * Lift;
            Vector3 top = doll.Pos[PlayerRagdoll.Pelvis] + high;
            if (CollisionDetection.CheckBetweenPoints(top, top - Vector3.UnitY * (Lift * 0.6f), TestFlags.None, scene, ref clear))
            {
                Console.WriteLine($"  [..] {arena}: no clear drop under {Lift} up, skipped");
                return fails;
            }
            // (the floor it comes down on: the fall is judged until a body's height over it, then the landing)
            var under = new CollisionResult();
            float floorY = CollisionDetection.CheckBetweenPoints(top, top - Vector3.UnitY * (Lift * 3), TestFlags.None, scene,
                ref under) ? under.Position.Y : float.MinValue;
            void Fall(string name, Vector3 speed, Action<PlayerRagdoll>? how, int steps = 240)
            {
                PlayerRagdoll c = doll.Clone();
                c.Shift(high);
                c.Launch(speed, push);
                c.Vary(Hunter.Samus, face, speed, push);
                how?.Invoke(c);
                float v = speed.Y;
                float y0 = Middle(c).Y, ideal = y0, lastY = y0;
                float worst = float.MaxValue;
                int worstAt = 0;
                int asleepAt = -1, downAt = -1;
                var line = new System.Text.StringBuilder();
                for (int s = 1; s <= steps; s++)
                {
                    c.Step(scene);
                    v = v * Damping - Gravity;
                    ideal += v;
                    float y = Middle(c).Y;
                    float dropped = y0 - y, idealDrop = y0 - ideal;
                    if (downAt < 0 && (y < floorY + 2 * height || ideal < floorY + 2 * height)) downAt = s;
                    // (from the step a lone point would be falling at all; the launch's own lift goes first)
                    if (idealDrop > 0.5f && downAt < 0)
                    {
                        float share = dropped / idealDrop;
                        if (share < worst)
                        {
                            worst = share;
                            worstAt = s;
                        }
                    }
                    if (s is 30 or 60 or 120 or 180 or 240)
                    {
                        line.Append($" {s}:{y - lastY:0.000}/{v:0.000}");
                    }
                    lastY = y;
                    if (c.Asleep && asleepAt < 0) asleepAt = s;
                }
                // (asleep in the air, or the moment it lands: a long fall's landing still tumbles and settles)
                bool badSleep = asleepAt >= 0 && (downAt < 0 || asleepAt < downAt + 15);
                string sleep = downAt >= 0 ? $", down at {downAt}" : "";
                sleep += asleepAt >= 0 ? $", {(badSleep ? "ASLEEP" : "asleep")} at {asleepAt}" : "";
                bool ok = worst >= FallShare && !badSleep;
                Console.WriteLine($"  [{(ok ? "OK" : "FAIL")}] {arena} air {name}: fell {worst:0.00} of a lone point's drop at worst"
                    + $" (step {worstAt}){sleep}; speed (step:its/lone point's){line}");
                if (!ok) fails++;
            }
            Fall("plain", Vector3.Zero, null);
            Fall("jump falling", new Vector3(0, -0.04f, 0), null);
            Fall("jump rising", new Vector3(0, 0.05f, 0), null);
            Fall("headshot", Vector3.Zero, d => d.HeadSnap(push));
            Fall("headshot falling", new Vector3(0, -0.04f, 0), d => d.HeadSnap(push));
            Fall("freeze", Vector3.Zero, d => d.Freeze());
            Fall("shock (on-foot flag)", Vector3.Zero, d => d.Twitch(onFoot: true));
            Fall("shock", Vector3.Zero, d => d.Twitch(onFoot: false));
            Fall("long fall", Vector3.Zero, null, steps: 600);

            // a body slid down a wall: a wall a ray from just over the chest finds within WallReach, standing straight and
            // clear in front from the floor to WallRise and the body's height over it; the body is moved up against it,
            // its lowest point WallRise over the floor, and pushed into it a little
            const float WallRise = 6, WallReach = 40;
            Vector3 chest = doll.Pos[PlayerRagdoll.Chest] + Vector3.UnitY;
            Vector3 pelvis = doll.Pos[PlayerRagdoll.Pelvis];
            float low = doll.Pos.Min(p => p.Y);
            float best = float.MaxValue;
            Vector3 wallWay = Vector3.Zero, column = Vector3.Zero, wallHit = Vector3.Zero;
            Vector4 wallPlane = Vector4.Zero;
            float wallFloor = 0;
            for (int k = 0; k < 32; k++)
            {
                float a = k * MathF.PI / 16;
                var w = new Vector3(MathF.Cos(a), 0, MathF.Sin(a));
                var r = new CollisionResult();
                if (!CollisionDetection.CheckBetweenPoints(chest, chest + w * WallReach, TestFlags.None, scene, ref r)
                    || MathF.Abs(r.Plane.Y) > 0.2f)
                {
                    continue;
                }
                Vector3 n = r.Plane.Xyz.WithY(0).Normalized();
                if (Vector3.Dot(n, w) > 0) n = -n;
                // (the body's pelvis a body's width off the face, over the floor there)
                Vector3 col = r.Position.WithY(chest.Y) + n * (0.5f * height + 0.2f);
                var down = new CollisionResult();
                if (!CollisionDetection.CheckBetweenPoints(col, col - Vector3.UnitY * 20, TestFlags.None, scene, ref down)
                    || down.Plane.Y < 0.7f)
                {
                    continue;
                }
                float fy = down.Position.Y;
                var up = new CollisionResult();
                Vector3 base0 = col.WithY(fy + 0.1f);
                if (CollisionDetection.CheckBetweenPoints(base0, base0 + Vector3.UnitY * (WallRise + 2 * height + 1), TestFlags.None,
                    scene, ref up))
                {
                    continue;
                }
                float dist = Vector3.Dot(col - r.Position, n);
                bool tall = true;
                for (float h = 0.3f; h <= WallRise + 2 * height && tall; h += 0.5f)
                {
                    var r2 = new CollisionResult();
                    Vector3 from = col.WithY(fy + h);
                    tall = CollisionDetection.CheckBetweenPoints(from, from - n * (dist + 1), TestFlags.None, scene, ref r2)
                        && MathF.Abs(r2.Plane.Y) < 0.2f && MathF.Abs(Vector3.Dot(from - r2.Position, n) - dist) < 0.15f;
                }
                float far = (r.Position - chest).Length;
                if (tall && far < best)
                {
                    best = far;
                    wallWay = -n;
                    wallPlane = r.Plane;
                    column = col;
                    wallHit = r.Position;
                    wallFloor = fy;
                }
            }
            if (best == float.MaxValue)
            {
                Console.WriteLine($"  [..] {arena}: no tall clear wall within {WallReach}, wall slide skipped");
                return fails;
            }
            {
                PlayerRagdoll c = doll.Clone();
                c.Shift(new Vector3(column.X - pelvis.X, wallFloor + WallRise - low, column.Z - pelvis.Z));
                // (the nearest point a radius and a hair off the face: how far each is in front of it, along its normal)
                Vector3 n = -wallWay;
                float near = float.MaxValue;
                for (int i = 0; i < PlayerRagdoll.PointCount; i++)
                {
                    near = MathF.Min(near, Vector3.Dot(c.Pos[i] - wallHit, n));
                }
                c.Shift(wallWay * (near - 0.11f * height));
                c.Launch(Vector3.Zero, wallWay * 0.03f);
                float y0 = Middle(c).Y, ideal = y0, v = 0, lastY = y0;
                int landedAt = -1;
                var line = new System.Text.StringBuilder();
                for (int s = 1; s <= 120; s++)
                {
                    c.Step(scene);
                    v = v * Damping - Gravity;
                    ideal += v;
                    float y = Middle(c).Y;
                    if (s is 10 or 20 or 30 or 45 or 60 or 90 or 120) line.Append($" {s}:{y - lastY:0.000}/{v:0.000}");
                    lastY = y;
                    if (landedAt < 0 && y0 - y > WallRise - 1.5f) landedAt = s;
                }
                // (a lone point drops WallRise - 1.5 in this many steps)
                int idealAt = -1;
                {
                    float iy = 0, iv = 0;
                    for (int s = 1; s <= 600 && idealAt < 0; s++)
                    {
                        iv = iv * Damping - Gravity;
                        iy += iv;
                        if (-iy > WallRise - 1.5f) idealAt = s;
                    }
                }
                // (tight: the old rub against a wall took ~1.15x as long)
                bool ok = landedAt > 0 && landedAt <= idealAt * 1.08f + 2;
                Console.WriteLine($"  [{(ok ? "OK" : "FAIL")}] {arena} wall slide ({best:0.00} off, normal {wallPlane.Xyz.X:0.00},"
                    + $"{wallPlane.Xyz.Y:0.00},{wallPlane.Xyz.Z:0.00}): down {WallRise - 1.5f} at step {landedAt} (a lone point:"
                    + $" {idealAt}); speed (step:its/lone point's){line}");
                if (!ok) fails++;
            }
            return fails;
        }

        // a Judicator freeze's motion (owner 2026-11-12, a player's 0.5.0 video: frozen and killed in the air, "the really
        // fast spinny thing", then shattered on the ground "and then flipped around and tossed through the air wildly"; the
        // freeze "should freeze their pose ... tumble a little if the physics call for it from the fall, but there should be
        // nothing inherent about the freeze death that makes the hunter spin around or flail wildly"). Each body: its spin
        // (the whole body's turn, degrees a second), its flail (the energy in its motion that isn't the body moving or turning
        // as one piece), and from the first touch on any rise of the middle (a toss) and of its energy (motion + height)
        private struct Motion
        {
            public float Spin;
            public float Flail;
            public float Energy;
            public Vector3 Middle;
        }

        private static Motion Measure(PlayerRagdoll d)
        {
            const int n = PlayerRagdoll.PointCount;
            Vector3 c = Vector3.Zero, vc = Vector3.Zero;
            for (int i = 0; i < n; i++)
            {
                c += d.Pos[i];
                vc += d.Pos[i] - d.Prev[i];
            }
            c /= n;
            vc /= n;
            double lx = 0, ly = 0, lz = 0, ixx = 0, iyy = 0, izz = 0, ixy = 0, ixz = 0, iyz = 0, ke = 0, pe = 0;
            for (int i = 0; i < n; i++)
            {
                Vector3 r = d.Pos[i] - c;
                Vector3 v = d.Pos[i] - d.Prev[i];
                Vector3 l = Vector3.Cross(r, v - vc);
                lx += l.X;
                ly += l.Y;
                lz += l.Z;
                ixx += r.Y * r.Y + r.Z * r.Z;
                iyy += r.X * r.X + r.Z * r.Z;
                izz += r.X * r.X + r.Y * r.Y;
                ixy -= r.X * r.Y;
                ixz -= r.X * r.Z;
                iyz -= r.Y * r.Z;
                ke += 0.5 * v.LengthSquared;
                pe += Gravity * d.Pos[i].Y;
            }
            // (the turn w = I^-1 L, by Cramer's rule)
            double det = ixx * (iyy * izz - iyz * iyz) - ixy * (ixy * izz - iyz * ixz) + ixz * (ixy * iyz - iyy * ixz);
            double wx = 0, wy = 0, wz = 0;
            if (Math.Abs(det) > 1e-12)
            {
                wx = (lx * (iyy * izz - iyz * iyz) - ixy * (ly * izz - iyz * lz) + ixz * (ly * iyz - iyy * lz)) / det;
                wy = (ixx * (ly * izz - iyz * lz) - lx * (ixy * izz - iyz * ixz) + ixz * (ixy * lz - ly * ixz)) / det;
                wz = (ixx * (iyy * lz - ly * iyz) - ixy * (ixy * lz - ly * ixz) + lx * (ixy * iyz - iyy * ixz)) / det;
            }
            double rot = 0.5 * (wx * lx + wy * ly + wz * lz);
            double lin = 0.5 * n * vc.LengthSquared;
            return new Motion
            {
                Spin = (float)(Math.Sqrt(wx * wx + wy * wy + wz * wz) * 60 * 180 / Math.PI),
                Flail = (float)Math.Max(0, ke - rot - lin),
                Energy = (float)(ke + pe),
                Middle = c,
            };
        }

        private struct Watched
        {
            public float AirSpin;
            public float AirFlail;
            public float AirGain;
            public float LandSpin;
            public float LandFlail;
            public float Toss;
            public float Gain;
            public int TouchAt;
            public int BrokeAt;
            public int SleptAt;
            public bool Under;
        }

        // (a floor within reach under any point: what it lands on, wherever that is, and at rest what it lies on, or it
        // went through)
        private static bool OnFloor(Scene scene, PlayerRagdoll c, float reach)
        {
            for (int i = 0; i < PlayerRagdoll.PointCount; i++)
            {
                var result = new CollisionResult();
                if (CollisionDetection.CheckBetweenPoints(c.Pos[i], c.Pos[i] - Vector3.UnitY * reach, TestFlags.None, scene,
                    ref result) && result.Plane.Y > 0.5f)
                {
                    return true;
                }
            }
            return false;
        }

        // (one body, stepped until it sleeps: its spin and flail in the air, and from its first touch on)
        private static Watched Watch(Scene scene, PlayerRagdoll c, float floorY, string name, bool print)
        {
            Motion m0 = Measure(c);
            var w = new Watched { TouchAt = -1, BrokeAt = -1, SleptAt = -1 };
            float lowY = float.MaxValue, lowE = float.MaxValue, airLowE = m0.Energy;
            var line = new System.Text.StringBuilder();
            for (int s = 1; s <= 600 && !c.Asleep; s++)
            {
                c.Step(scene);
                Motion m = Measure(c);
                if (c.IceBrokeNow)
                {
                    w.BrokeAt = s;
                }
                if (w.TouchAt < 0 && OnFloor(scene, c, 0.35f))
                {
                    w.TouchAt = s;
                }
                if (w.TouchAt < 0)
                {
                    w.AirSpin = MathF.Max(w.AirSpin, m.Spin);
                    w.AirFlail = MathF.Max(w.AirFlail, m.Flail);
                    airLowE = MathF.Min(airLowE, m.Energy);
                    w.AirGain = MathF.Max(w.AirGain, m.Energy - airLowE);
                }
                else
                {
                    w.LandSpin = MathF.Max(w.LandSpin, m.Spin);
                    w.LandFlail = MathF.Max(w.LandFlail, m.Flail);
                    lowY = MathF.Min(lowY, m.Middle.Y);
                    w.Toss = MathF.Max(w.Toss, m.Middle.Y - lowY);
                    lowE = MathF.Min(lowE, m.Energy);
                    w.Gain = MathF.Max(w.Gain, m.Energy - lowE);
                }
                if (print && (s <= 3 || s % 5 == 0 && s <= 120 || s % 20 == 0 || s == w.TouchAt || s == w.BrokeAt))
                {
                    line.Append($"\n        {s,3}{(s == w.TouchAt ? " touch" : "")}{(s == w.BrokeAt ? " BREAK" : "")}: spin {m.Spin,6:0}"
                        + $" flail {m.Flail * 1e4,7:0.0} E {(m.Energy - m0.Energy) * 1e3,8:0.0} mid.y {m.Middle.Y - floorY,7:0.00}"
                        + $" low.y {c.Pos.Min(q => q.Y) - floorY,7:0.00}{(c.Iced ? " iced" : "")}");
                }
                if (c.Asleep)
                {
                    w.SleptAt = s;
                }
            }
            w.Under = !OnFloor(scene, c, 1);
            Console.WriteLine($"  [..] {name}: spin {m0.Spin:0} at the death, most {w.AirSpin:0} in the air (flail"
                + $" {w.AirFlail * 1e4:0.0}, energy gain {w.AirGain * 1e3:0.0}); from the touch (step {w.TouchAt}, break"
                + $" {w.BrokeAt}, asleep {w.SleptAt}) spin {w.LandSpin:0}, flail {w.LandFlail * 1e4:0.0}, toss {w.Toss:0.00} up,"
                + $" energy gain {w.Gain * 1e3:0.0}{line}");
            return w;
        }

        // (a freeze against the same death without it. Pass: frozen in the air it falls as one piece, turning no faster
        // than ~120 degrees a second and gaining no energy, the ice breaks within a few steps of its touch, and from the
        // touch on it spins, flails, tosses and gains no more than the plain death does, give or take a margin)
        private const float AirTurnMost = 125; // (degrees a second: PlayerRagdoll.IceTurn's 120, and a little)
        private const float SpinMargin = 1.25f;
        private const float SpinSlack = 120;
        private const int BreakWithin = 8;

        private static int Judge(Watched plain, Watched ice, string name, bool inAir)
        {
            int fails = 0;
            void Fail(string what)
            {
                Console.WriteLine($"  [FAIL] {name}: {what}");
                fails++;
            }
            if (ice.SleptAt < 0)
            {
                Fail("never came to rest in 600 steps");
            }
            if (ice.Under || plain.Under)
            {
                Fail($"went through the floor ({(ice.Under ? "frozen" : "")}{(ice.Under && plain.Under ? " and " : "")}"
                    + $"{(plain.Under ? "plain" : "")})");
            }
            if (inAir)
            {
                if (ice.AirSpin > AirTurnMost)
                {
                    Fail($"spun {ice.AirSpin:0} degrees a second in the air (most {AirTurnMost:0})");
                }
                if (ice.AirFlail * 1e4 > 1)
                {
                    Fail($"flailed in the air ({ice.AirFlail * 1e4:0.0})");
                }
                if (ice.AirGain * 1e3 > 1)
                {
                    Fail($"gained energy in the air ({ice.AirGain * 1e3:0.0})");
                }
                if (ice.TouchAt >= 0 && (ice.BrokeAt < 0 || ice.BrokeAt > ice.TouchAt + BreakWithin))
                {
                    Fail($"the ice broke at step {ice.BrokeAt}, its touch at {ice.TouchAt} (within {BreakWithin})");
                }
            }
            float spinMost = MathF.Max(plain.LandSpin * SpinMargin, plain.LandSpin + SpinSlack);
            if (ice.LandSpin > spinMost)
            {
                Fail($"spun {ice.LandSpin:0} from the touch on, the plain death {plain.LandSpin:0} (most {spinMost:0})");
            }
            float flailMost = MathF.Max(plain.LandFlail * SpinMargin, plain.LandFlail + 2e-2f);
            if (ice.LandFlail > flailMost)
            {
                Fail($"flailed {ice.LandFlail * 1e4:0.0} from the touch on, the plain death {plain.LandFlail * 1e4:0.0}");
            }
            if (ice.Toss > MathF.Max(0.15f, plain.Toss + 0.1f))
            {
                Fail($"tossed {ice.Toss:0.00} up from the touch on, the plain death {plain.Toss:0.00}");
            }
            if (ice.Gain > MathF.Max(plain.Gain * SpinMargin, plain.Gain + 5e-3f))
            {
                Fail($"gained {ice.Gain * 1e3:0.0} energy from the touch on, the plain death {plain.Gain * 1e3:0.0}");
            }
            return fails;
        }

        private static int FreezeMotion(CampaignHost host, PlayerRagdoll doll, Vector3 facing, string arena)
        {
            Scene scene = host.Scene;
            Vector3 face = facing.WithY(0);
            face = face.LengthSquared < 1e-8f ? Vector3.UnitZ : face.Normalized();
            Vector3 push = -face * 0.06f;
            var under = new CollisionResult();
            Vector3 pelvis = doll.Pos[PlayerRagdoll.Pelvis];
            if (!CollisionDetection.CheckBetweenPoints(pelvis, pelvis - Vector3.UnitY * 5, TestFlags.None, scene, ref under))
            {
                Console.WriteLine($"  [..] {arena}: no floor under the body, freeze motion skipped");
                return 0;
            }
            float floorY = under.Position.Y;
            // (the room's ceiling over the spot: a start above it is outside the room, where the faces point away)
            var over = new CollisionResult();
            float top = doll.Pos.Max(q => q.Y);
            float headroom = CollisionDetection.CheckBetweenPoints(pelvis, pelvis + Vector3.UnitY * 40, TestFlags.None, scene,
                ref over) ? over.Position.Y - top : 40;
            Console.WriteLine($"  [..] {arena}: headroom over the body {headroom:0.00}");
            int fails = 0;
            foreach (float up in new[] { 0f, 3, 8, 20 })
            {
                if (up > headroom - 0.5f)
                {
                    Console.WriteLine($"  [..] {arena}: {up} up skipped, above the ceiling");
                    continue;
                }
                foreach ((string how, Vector3 speed) in new[] { ("still", Vector3.Zero), ("rising", new Vector3(0, 0.05f, 0)),
                    ("falling", new Vector3(0, -0.04f, 0)), ("running", face * 0.12f) })
                {
                    if (up == 0 && how != "still")
                    {
                        continue;
                    }
                    bool print = false;
                    Watched[] got = new Watched[2];
                    for (int k = 0; k < 2; k++)
                    {
                        PlayerRagdoll c = doll.Clone();
                        c.Shift(Vector3.UnitY * up);
                        c.Launch(speed, push);
                        c.Vary(Hunter.Samus, face, speed, push);
                        if (k == 1)
                        {
                            c.Freeze();
                        }
                        got[k] = Watch(scene, c, floorY, $"{arena} {(k == 1 ? "freeze" : "plain")} {up} up {how}", print && k == 1);
                    }
                    fails += Judge(got[0], got[1], $"{arena} freeze {up} up {how}", inAir: up > 0);
                }
            }
            return fails;
        }

        // the main seat killed in the middle of a jump (its own pose and speed), then frozen as a charged Judicator kill is
        private static int JumpFreeze(CampaignHost host, string arena)
        {
            int fails = 0;
            foreach (int frames in new[] { 6, 14, 24 })
            {
                var input = new CampaignInput { SelectWeapon = BeamType.None };
                for (int f = 0; f < 600 && PlayerEntity.Main.Health <= 0; f++)
                {
                    host.Step(new CampaignInput { SelectWeapon = BeamType.None, Buttons = f % 2 == 0 ? CampaignButtons.Shoot : 0 });
                }
                for (int f = 0; f < 30; f++)
                {
                    host.Step(input);
                }
                PlayerEntity p = PlayerEntity.Main;
                if (p.Health <= 0 || p.IsAltForm)
                {
                    Console.WriteLine($"  [..] {arena}: main seat not up on foot, jump freeze skipped");
                    continue;
                }
                for (int f = 0; f < frames; f++)
                {
                    host.Step(new CampaignInput
                    {
                        SelectWeapon = BeamType.None,
                        Buttons = f < 4 ? CampaignButtons.Jump : 0,
                        Move = new System.Numerics.Vector2(0, f < 2 ? 0 : 1),
                    });
                }
                var facing = new Vector3(p.Field70, 0, p.Field74);
                Vector3 speed = p.Speed;
                p.TakeDamage(999u, DamageFlags.IgnoreInvuln, -facing, null);
                if (p.Ragdoll == null)
                {
                    Console.WriteLine($"  [FAIL] {arena}: no ragdoll at the jump kill");
                    fails++;
                    continue;
                }
                PlayerRagdoll plain = p.Ragdoll.Clone();
                PlayerRagdoll c = p.Ragdoll.Clone();
                c.Freeze();
                var under = new CollisionResult();
                Vector3 pelvis = c.Pos[PlayerRagdoll.Pelvis];
                float floorY = CollisionDetection.CheckBetweenPoints(pelvis, pelvis - Vector3.UnitY * 40, TestFlags.None, host.Scene,
                    ref under) ? under.Position.Y : c.Pos.Min(q => q.Y) - 3;
                string name = $"{arena} jump kill {frames} frames in (speed {speed.X:0.00},{speed.Y:0.00},{speed.Z:0.00},"
                    + $" {c.Pos.Min(q => q.Y) - floorY:0.00} up)";
                bool inAir = !OnFloor(host.Scene, c, 0.35f);
                Watched a = Watch(host.Scene, plain, floorY, $"{name} plain", print: false);
                Watched b = Watch(host.Scene, c, floorY, $"{name} freeze", print: frames == 24 && arena.StartsWith("MP7"));
                fails += Judge(a, b, $"{name} freeze", inAir);
            }
            return fails;
        }
    }
}
