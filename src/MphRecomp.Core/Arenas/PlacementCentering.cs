using System;
using System.Collections.Generic;
using MphRead;
using MphRead.Formats;
using OpenTK.Mathematics;

namespace MphRecomp.Arenas
{
    // Centring the owner's placements (PickupPlacement files) in their surroundings (owner 2026-10-10: "if my placement
    // is a little more right than left you can fix it"). A spot keeps its distance from the wall or pillar it stands by
    // and moves along it to the middle: of that wall's face (a pillar: the face narrows to its middle), or of the floor /
    // the walls to either side, whichever is nearer. A spot in the open is left as it is. Then, where the arena is a
    // mirror image about x = 0 or z = 0 around the spot, a spot close to that line snaps onto it. Only proposes: the
    // owner OKs each move before it's written.
    public static class PlacementCentering
    {
        public sealed record Proposal(Vector3 From, Vector3 To, string Why)
        {
            public float Shift => (To - From).Xz.Length;
        }

        const float NearWall = 3f, Reach = 8f, MaxShift = 1.5f, Step = 0.05f, SnapAxis = 0.6f;

        static float? Ray(Scene scene, Vector3 from, Vector3 dir, float len, out Vector3 normal)
        {
            normal = Vector3.Zero;
            CollisionResult res = default;
            if (!CollisionDetection.CheckBetweenPoints(from, from + dir * len, TestFlags.None, scene, ref res)) return null;
            normal = res.Plane.Xyz;
            return res.Distance * len;
        }

        // floor within `tol` of y under p
        static bool FloorAt(Scene scene, Vector3 p, float y, float tol = 0.35f)
        {
            CollisionResult res = default;
            Vector3 top = new Vector3(p.X, y + 0.5f, p.Z);
            return CollisionDetection.CheckBetweenPoints(top, top.AddY(-0.5f - tol), TestFlags.None, scene, ref res);
        }

        // floor: the spot on the floor; height: how high its middle is over the floor (crate ~1.1, pickup ~0.65)
        public static Proposal Propose(Scene scene, Vector3 floor, float height)
        {
            Vector3 mid = floor.AddY(height);
            // the nearest wall about the spot (32 directions)
            float best = Single.MaxValue;
            Vector3 toWall = Vector3.Zero;
            for (int i = 0; i < 32; i++)
            {
                float a = MathHelper.TwoPi * i / 32;
                var dir = new Vector3(MathF.Sin(a), 0, MathF.Cos(a));
                if (Ray(scene, mid, dir, Reach, out Vector3 n) is float d && d < best && MathF.Abs(n.Y) < 0.5f)
                {
                    best = d;
                    // the way to the wall: against its face (its normal), not the ray's direction
                    var nf = new Vector3(-n.X, 0, -n.Z);
                    toWall = nf.LengthSquared > 1e-6f ? nf.Normalized() : dir;
                }
            }
            Vector3 to = floor;
            string why;
            if (best > NearWall)
            {
                why = "in the open (no wall within 3): kept";
            }
            else
            {
                float wallDist = Ray(scene, mid, toWall, Reach, out _) ?? best;
                var side = new Vector3(toWall.Z, 0, -toWall.X); // along the face
                // how far each way the spot can go along the face: the face itself (still there, about as far), the floor
                // under the spot, and nothing in the way
                float Extent(int sign)
                {
                    float s = 0;
                    while (s < Reach)
                    {
                        float next = s + Step;
                        Vector3 p = mid + side * (sign * next);
                        float? face = Ray(scene, p, toWall, wallDist + 1f, out _);
                        if (face is not float f || MathF.Abs(f - wallDist) > 0.3f) break;
                        if (!FloorAt(scene, p, floor.Y)) break;
                        if (Ray(scene, mid + side * (sign * s), side * sign, Step + 0.4f, out _) != null) break;
                        s = next;
                    }
                    return s;
                }
                float left = Extent(-1), right = Extent(1);
                float shift = (right - left) / 2;
                if (MathF.Abs(shift) > MaxShift)
                {
                    why = $"by a wall {wallDist:0.00} away; its face runs {left:0.00} one way and {right:0.00} the other: too far off to be a nudge, kept";
                }
                else
                {
                    to = floor + side * shift;
                    why = $"by a wall {wallDist:0.00} away; its face / the floor runs {left:0.00} one way and {right:0.00} the other: moved {MathF.Abs(shift):0.00} to the middle";
                }
            }
            // the arena's mirror lines: x = 0 and z = 0, where the room around the spot is the same on both sides
            foreach ((bool onX, string axis) in new[] { (true, "x"), (false, "z") })
            {
                float off = onX ? to.X : to.Z;
                if (MathF.Abs(off) < 1e-3f || MathF.Abs(off) > SnapAxis) continue;
                if (!Mirrored(scene, to.AddY(height), onX)) continue;
                Vector3 snapped = onX ? new Vector3(0, to.Y, to.Z) : new Vector3(to.X, to.Y, 0);
                why += $"; the arena mirrors about {axis} = 0 here and the spot was {MathF.Abs(off):0.00} off it: snapped onto it";
                to = snapped;
            }
            if (to != floor)
            {
                CollisionResult res = default;
                Vector3 top = to.AddY(1f);
                if (CollisionDetection.CheckBetweenPoints(top, top.AddY(-3f), TestFlags.None, scene, ref res)) to = res.Position;
            }
            return new Proposal(floor, to, why);
        }

        // the room around p mirrored about x = 0 (or z = 0): 16 rays from p and their mirror images from p's mirror
        // image reach about as far
        static bool Mirrored(Scene scene, Vector3 p, bool aboutX)
        {
            Vector3 m = aboutX ? new Vector3(-p.X, p.Y, p.Z) : new Vector3(p.X, p.Y, -p.Z);
            int same = 0, both = 0;
            for (int i = 0; i < 16; i++)
            {
                float a = MathHelper.TwoPi * i / 16;
                var d = new Vector3(MathF.Sin(a), 0, MathF.Cos(a));
                Vector3 dm = aboutX ? new Vector3(-d.X, 0, d.Z) : new Vector3(d.X, 0, -d.Z);
                float r1 = Ray(scene, p, d, Reach, out _) ?? Reach, r2 = Ray(scene, m, dm, Reach, out _) ?? Reach;
                both++;
                if (MathF.Abs(r1 - r2) < 0.25f + 2 * MathF.Abs(aboutX ? p.X : p.Z)) same++;
            }
            return same >= both - 1;
        }
    }
}
