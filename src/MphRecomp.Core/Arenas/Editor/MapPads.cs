using System;
using System.Collections.Generic;
using OpenTK.Mathematics;

namespace MphRecomp.Arenas.Editor
{
    public static class MapPads
    {
        public const float Gravity = 77 / 4096f;
        public const float PadRadius = 1, PadHeight = 1;
        public const ushort Lock = 1;
        public const float MaxSpeed = 1.6f;   // past the vanilla pads' 1.0, still a sane arc; further = refused
        const int MaxTicks = 600;

        public readonly struct Launch
        {
            public readonly Vector3 Vector;          // speed a tick (the pad's BeamVector direction * Speed)
            public readonly List<Vector3> Arc;       // the flight, a point a tick, ending on the landing spot
            public readonly bool TooFar;
            public readonly bool Blocked;            // no arc under MaxSpeed clears the room: the lowest one is kept
            public Launch(Vector3 v, List<Vector3> arc, bool tooFar, bool blocked = false)
            {
                Vector = v; Arc = arc; TooFar = tooFar; Blocked = blocked;
            }
        }

        // where the biped's Position sits above a pad's spot as it stands there: traced on the Odin's build headless
        // (2026-10-31, Mining Plaza pad 2: launched at pad + 0.755). The pad's volume (pad .. pad + 1) is tested on that
        // Position, so the first launch carries it out of the volume in one tick, and the lock (2 frames) runs out with it:
        // gravity pulls from the next tick. Working the flight from the floor instead added a second no-gravity launch tick and every placed pad
        // landed about 3 units short (the owner's Mining Plaza chain fell off pad 1's ledge)
        public const float Stand = 0.75f;

        // the flight of a launch from the pad at p0: points a tick (floor level, i.e. Position - Stand) until it comes
        // down through landY (or MaxTicks)
        public static List<Vector3> Fly(Vector3 p0, Vector3 v, float landY, out float ticks)
        {
            var arc = new List<Vector3> { p0 };
            var up = new Vector3(0, Stand, 0);
            Vector3 p = p0 + up, vel = Vector3.Zero;
            float land = landY + Stand;
            int lockLeft = 0;
            ticks = MaxTicks;
            for (int t = 1; t <= MaxTicks; t++)
            {
                float dx = p.X - p0.X, dz = p.Z - p0.Z;
                bool inside = p.Y >= p0.Y && p.Y < p0.Y + PadHeight && dx * dx + dz * dz < PadRadius * PadRadius;
                if (inside)
                {
                    vel = v;
                    lockLeft = Lock - 1;   // the launch tick spends the lock's first tick (the trace: Y held 2 frames)
                }
                else if (lockLeft > 0) lockLeft--;
                else vel.Y -= Gravity;
                Vector3 next = p + vel;
                if (vel.Y < 0 && next.Y <= land && p.Y >= land)
                {
                    float f = (p.Y - land) / (p.Y - next.Y);
                    arc.Add(Vector3.Lerp(p, next, f) - up);
                    ticks = t - 1 + f;
                    return arc;
                }
                p = next;
                arc.Add(p - up);
            }
            return arc;
        }

        // the body along an arc: the player stands 0.5 above the floor the arc is worked from, and MPH's biped spans
        // -0.5..+1.1 of that (MinPickupHeight / MaxPickupHeight); three lines through it, the lowest lifted off the floors
        static readonly float[] Body = { 0.3f, 0.9f, 1.5f };
        const float ApexStep = 0.5f;
        const int ApexTries = 80;

        // the launch from pad to target: an arc peaking a bit above the higher end (higher for longer throws), with the
        // sideways speed that covers the distance in the flight's time. Given `blocked` (true when room collision lies
        // between two points), an arc that runs into a pillar or a ledge is raised half a unit at a time until it flies
        // over (2026-10-31), else lowered toward a flat throw in case it fits under something; nothing clear under
        // MaxSpeed (a wall up to the ceiling) keeps the usual arc, marked Blocked
        public static Launch Solve(Vector3 pad, Vector3 target, Func<Vector3, Vector3, bool>? blocked = null)
        {
            float low = MathF.Max(0, target.Y - pad.Y);
            float baseApex = low + 2.5f + 0.15f * new Vector2(target.X - pad.X, target.Z - pad.Z).Length;
            Launch first = Solve(pad, target, baseApex);
            if (blocked == null || !Hits(first.Arc, blocked)) return first;
            for (int n = 1; n <= ApexTries; n++)
            {
                Launch l = Solve(pad, target, baseApex + n * ApexStep);
                if (l.TooFar) break;
                if (!Hits(l.Arc, blocked)) return l;
            }
            for (float apex = baseApex - ApexStep; apex >= low + 0.5f; apex -= ApexStep)
            {
                Launch l = Solve(pad, target, apex);
                if (l.TooFar) break;
                if (!Hits(l.Arc, blocked)) return l;
            }
            return new Launch(first.Vector, first.Arc, first.TooFar, blocked: true);
        }

        static bool Hits(List<Vector3> arc, Func<Vector3, Vector3, bool> blocked)
        {
            for (int i = 1; i < arc.Count; i++)
            {
                foreach (float h in Body)
                {
                    if (blocked(arc[i - 1] + new Vector3(0, h, 0), arc[i] + new Vector3(0, h, 0))) return true;
                }
            }
            return false;
        }

        static Launch Solve(Vector3 pad, Vector3 target, float apex)
        {
            var flat = new Vector2(target.X - pad.X, target.Z - pad.Z);
            float range = flat.Length;
            Vector2 dir = range > 0.001f ? flat / range : Vector2.Zero;
            // the up speed whose flight peaks at the apex (straight up; the pad's volume adds its no-gravity ticks)
            float lo = 0.02f, hi = 3;
            for (int n = 0; n < 40; n++)
            {
                float mid = (lo + hi) / 2;
                if (Peak(pad, mid) < apex) lo = mid;
                else hi = mid;
            }
            float vy = (lo + hi) / 2, vh = 0;
            // the sideways speed: the time to come down to the target depends a little on it (it leaves the pad sooner)
            for (int n = 0; n < 6 && range > 0.001f; n++)
            {
                Fly(pad, new Vector3(dir.X * vh, vy, dir.Y * vh), target.Y, out float ticks);
                vh = range / MathF.Max(1, ticks);
            }
            var v = new Vector3(dir.X * vh, vy, dir.Y * vh);
            List<Vector3> arc = Fly(pad, v, target.Y, out _);
            return new Launch(v, arc, v.Length > MaxSpeed);
        }

        static float Peak(Vector3 pad, float vy)
        {
            float best = 0;
            foreach (Vector3 p in Fly(pad, new Vector3(0, vy, 0), pad.Y - 1000, out _))
            {
                if (p.Y - pad.Y > best) best = p.Y - pad.Y;
                else if (p.Y - pad.Y < best - 0.5f) break;
            }
            return best;
        }
    }
}
