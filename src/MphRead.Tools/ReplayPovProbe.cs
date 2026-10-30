using System;
using System.Collections.Generic;
using MphRead.Entities;
using MphRecomp.Campaign;
using OpenTK.Mathematics;

namespace MphRead
{
    // -replayplay <file> pov=1 (replays batch 10-23, B3): how each seat's first-person gun sits in its own view, step by
    // step, from the sim's camera (CameraInfo: what that player's screen shows) and MPH's gun transform (GunDrawTransform).
    // Per seat, over the steps it's in first person and alive: the gun's aim off the view's facing (degrees), the gun's
    // place in view space (right / up / forward from the eye) and how far it moves there per step, and the view's own pitch.
    // A human's gun barely moves in its view (bob only); a gun that "swings free" moves a lot there.
    internal static class ReplayPovProbe
    {
        sealed class Seat
        {
            public int Steps;
            public double AimOff, AimOffMax, Move, MoveMax, Pitch, PitchUp;
            public Vector3 Sum, Prev, PrevFacing;
            public bool HasPrev;
            public double LagOff, LagOffMax; public int LagSteps; // the gun's aim off the PREVIOUS step's facing (a view a step late)
            public readonly int[] AimHist = new int[6]; // <2, <5, <10, <20, <40, >=40 degrees
        }

        static readonly Dictionary<int, Seat> _seats = new();

        public static void Install()
        {
            _seats.Clear();
            ReplayTest.StepProbe = Probe;
        }

        static void Probe(int step, CampaignHost host)
        {
            for (int i = 0; i < PlayerEntity.MaxPlayers; i++)
            {
                PlayerEntity p = PlayerEntity.Players[i];
                if (p == null || p.Health == 0 || p.IsAltForm || p.CameraType != CameraType.First || p.LoadFlags == 0)
                {
                    if (_seats.TryGetValue(i, out Seat? gone)) gone.HasPrev = false;
                    continue;
                }
                if (!_seats.TryGetValue(i, out Seat? s)) _seats[i] = s = new Seat();
                CameraInfo c = p.CameraInfo;
                Vector3 f = c.Facing.LengthSquared > 1e-6f ? c.Facing.Normalized() : Vector3.UnitZ;
                Vector3 up = c.TrueUp.LengthSquared > 1e-6f ? c.TrueUp.Normalized() : Vector3.UnitY;
                Vector3 r = Vector3.Cross(f, up).Normalized();
                up = Vector3.Cross(r, f);
                Matrix4 g = p.GunDrawTransform;
                Vector3 aim = g.Row2.Xyz.LengthSquared > 1e-6f ? g.Row2.Xyz.Normalized() : f;
                double off = MathHelper.RadiansToDegrees(MathF.Acos(Math.Clamp(Vector3.Dot(aim, f), -1, 1)));
                Vector3 d = g.Row3.Xyz - c.Position;
                var local = new Vector3(Vector3.Dot(d, r), Vector3.Dot(d, up), Vector3.Dot(d, f));
                s.Steps++;
                s.AimOff += off;
                s.AimOffMax = Math.Max(s.AimOffMax, off);
                s.AimHist[off < 2 ? 0 : off < 5 ? 1 : off < 10 ? 2 : off < 20 ? 3 : off < 40 ? 4 : 5]++;
                s.Sum += local;
                if (s.HasPrev)
                {
                    double lag = MathHelper.RadiansToDegrees(MathF.Acos(Math.Clamp(Vector3.Dot(aim, s.PrevFacing), -1, 1)));
                    s.LagOff += lag;
                    s.LagOffMax = Math.Max(s.LagOffMax, lag);
                    s.LagSteps++;
                    double move = (local - s.Prev).Length;
                    s.Move += move;
                    s.MoveMax = Math.Max(s.MoveMax, move);
                }
                s.Prev = local;
                s.PrevFacing = f;
                s.HasPrev = true;
                double pitch = MathHelper.RadiansToDegrees(MathF.Asin(Math.Clamp(f.Y, -1, 1)));
                s.Pitch += pitch;
                if (pitch > 20) s.PitchUp++;
            }
        }

        public static void Report()
        {
            Console.WriteLine("seat  kind   steps  gun aim off view: avg / max deg  [<2 <5 <10 <20 <40 >=40 %]   gun in view (r,u,f) avg   moves/step avg / max   view pitch avg, >20 up %   gun off a step-late view avg / max");
            foreach ((int i, Seat s) in new SortedDictionary<int, Seat>(_seats))
            {
                if (s.Steps == 0) continue;
                PlayerEntity p = PlayerEntity.Players[i];
                string hist = String.Join(" ", Array.ConvertAll(s.AimHist, h => $"{100.0 * h / s.Steps,3:0}"));
                Vector3 avg = s.Sum / s.Steps;
                Console.WriteLine($"{i,4}  {(p.IsBot ? "bot" : "human"),-5} {s.Steps,6}  {s.AimOff / s.Steps,6:0.0} / {s.AimOffMax,5:0.0}  [{hist}]"
                    + $"   ({avg.X:0.00},{avg.Y:0.00},{avg.Z:0.00})   {s.Move / Math.Max(1, s.Steps - 1):0.000} / {s.MoveMax:0.00}"
                    + $"   {s.Pitch / s.Steps,5:0.0}, {100 * s.PitchUp / s.Steps,3:0}"
                    + $"   {s.LagOff / Math.Max(1, s.LagSteps),6:0.0} / {s.LagOffMax,5:0.0}");
            }
        }
    }
}
