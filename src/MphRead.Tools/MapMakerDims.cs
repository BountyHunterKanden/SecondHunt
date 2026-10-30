using System;
using System.Collections.Generic;
using System.Linq;
using MphRead.Entities;
using MphRead.Formats;
using MphRead.Formats.Collision;
using OpenTK.Mathematics;

namespace MphRead
{
    // MAP MAKER E1 survey: the Hunters dimensions the editor's default grid has to fit (docs/MAP_EDITOR.md section 5).
    //   -mmsurvey dims    doors, the player (jump reach), and what the multiplayer rooms' collision measures
    internal static class MapMakerDims
    {
        public static void Run()
        {
            Console.WriteLine("== doors (opening = 2 x half width x height; lock offset / radius from DoorMetadata)");
            string[] doorKinds = { "Standard", "MorphBall", "Boss", "Thin" };
            float[] portHeights = { 3.4f, 3.4f, 6.4f, 3.4f };
            for (int d = 0; d < doorKinds.Length; d++)
            {
                DoorMetadata door = Metadata.Doors[d];
                Console.WriteLine($"  {doorKinds[d],-10} {door.Name}: opening {2 * 2.1f:0.0} wide x {portHeights[d]:0.0} high; lock offset {door.LockOffset:0.0} radius {door.Radius:0.0}");
            }
            Console.WriteLine("== hunters (units; speeds per game frame at 60 Hz)");
            for (int h = 0; h < 8; h++)
            {
                PlayerValues v = Metadata.PlayerValues[h];
                float g = Fixed.ToFloat(v.BipedGravity), j = Fixed.ToFloat(v.JumpSpeed);
                float y = 0, vy = j, apex = 0;
                for (int f = 0; f < 600 && vy > 0; f++) { y += vy; vy += g; apex = MathF.Max(apex, y); }
                Console.WriteLine($"  {(Hunter)h,-8} radius {Fixed.ToFloat(v.BipedColRadius):0.00} pickup {Fixed.ToFloat(v.MinPickupHeight):0.00}..{Fixed.ToFloat(v.MaxPickupHeight):0.00}" +
                    $" alt radius {Fixed.ToFloat(v.AltColRadius):0.00} jump speed {j:0.000} gravity {g:0.000} apex {apex:0.00} walk cap {Fixed.ToFloat(v.WalkSpeedCap):0.000}");
            }

            var clearance = new SortedDictionary<float, int>();     // ceiling clearance above a floor (0.5 steps) -> samples
            var widths = new SortedDictionary<float, int>();        // narrowest wall to wall gap at 1.0 above a floor (0.5 steps) -> samples
            var stepGaps = new SortedDictionary<float, int>();      // gap between consecutive floor levels within a room (0.25 steps)
            var extents = new List<(string Name, Vector3 Size, int Polys)>();
            int rooms = 0;
            long polys = 0, points = 0;
            foreach (RoomMetadata meta in Metadata.RoomList.Where(r => r.Multiplayer && !r.FirstHunt && r.Name.StartsWith("MP")))
            {
                CollisionInfo info;
                try { info = Collision.GetCollision(meta).Info; }
                catch (Exception e) { Console.WriteLine($"{meta.Name}: {e.Message}"); continue; }
                if (info is not MphCollisionInfo mph) continue;
                var floors = new List<(Vector3 C, float Area)>();
                var ceilings = new List<(Vector3[] P, Vector3 N)>();
                var walls = new List<(Vector3[] P, Vector3 N)>();
                Vector3 min = new(float.MaxValue), max = new(float.MinValue);
                foreach (CollisionData data in mph.Data)
                {
                    if (data.PointIndexCount < 3) continue;
                    var p = new Vector3[data.PointIndexCount];
                    for (int i = 0; i < p.Length; i++) p[i] = mph.Points[mph.PointIndices[data.PointStartIndex + i]];
                    foreach (Vector3 q in p) { min = Vector3.ComponentMin(min, q); max = Vector3.ComponentMax(max, q); }
                    Vector3 n = Vector3.Zero;
                    for (int i = 1; i + 1 < p.Length; i++) n += Vector3.Cross(p[i] - p[0], p[i + 1] - p[0]);
                    float area = n.Length / 2;
                    if (area < 0.01f) continue;
                    n = Vector3.Normalize(n);
                    polys++; points += p.Length;
                    // the stored plane gives the true facing (winding differs between rooms)
                    Vector4 pl = mph.Planes[data.PlaneIndex];
                    if (Vector3.Dot(n, new Vector3(pl.X, pl.Y, pl.Z)) < 0) n = -n;
                    Vector3 c = p.Aggregate(Vector3.Zero, (a, b) => a + b) / p.Length;
                    if (n.Y > 0.95f && area >= 4) floors.Add((c, area));
                    else if (n.Y < -0.95f) ceilings.Add((p, n));
                    else if (MathF.Abs(n.Y) < 0.2f) walls.Add((p, n));
                }
                rooms++;
                extents.Add((meta.Name, max - min, mph.Data.Count));
                var levels = floors.Select(f => MathF.Round(f.C.Y * 4) / 4).Distinct().OrderBy(y => y).ToList();
                for (int i = 1; i < levels.Count; i++)
                {
                    float gap = levels[i] - levels[i - 1];
                    if (gap > 0 && gap <= 8) Bump(stepGaps, gap);
                }
                // the larger floors: ceiling clearance straight up, wall to wall gaps along X and Z
                foreach ((Vector3 c, _) in floors.OrderByDescending(f => f.Area).Take(40))
                {
                    float up = Cast(ceilings, c + new Vector3(0, 0.05f, 0), Vector3.UnitY);
                    if (up < 60) Bump(clearance, MathF.Round((up + 0.05f) * 2) / 2);
                    Vector3 eye = c + new Vector3(0, 1f, 0);
                    float best = float.MaxValue;
                    foreach (Vector3 dir in new[] { Vector3.UnitX, Vector3.UnitZ })
                    {
                        float a = Cast(walls, eye, dir), b = Cast(walls, eye, -dir);
                        if (a < 60 && b < 60) best = MathF.Min(best, a + b);
                    }
                    if (best < 60) Bump(widths, MathF.Round(best * 2) / 2);
                }
            }
            Console.WriteLine($"== {rooms} multiplayer rooms, {polys} collision polygons ({points} corner indices)");
            Console.WriteLine("== room extents (x, y, z):");
            foreach ((string name, Vector3 size, int n) in extents) Console.WriteLine($"  {name,-24} {size.X,6:0.0} {size.Y,6:0.0} {size.Z,6:0.0}   collision polygons {n}");
            Console.WriteLine("== gaps between consecutive floor levels in a room, <= 8 (gap:count): " + string.Join("  ", stepGaps.Select(k => $"{k.Key:0.00}:{k.Value}")));
            Console.WriteLine("== ceiling clearance over the larger floors (units:count): " + string.Join("  ", clearance.Select(k => $"{k.Key:0.0}:{k.Value}")));
            Console.WriteLine("== wall to wall gap at 1.0 above the larger floors, narrower of X / Z (units:count): " + string.Join("  ", widths.Select(k => $"{k.Key:0.0}:{k.Value}")));
        }

        static void Bump(SortedDictionary<float, int> d, float key) => d[key] = d.GetValueOrDefault(key) + 1;

        // distance along a ray to the nearest of the polygons, 1000 when none
        static float Cast(List<(Vector3[] P, Vector3 N)> polys, Vector3 origin, Vector3 dir)
        {
            float best = 1000;
            foreach ((Vector3[] p, Vector3 n) in polys)
            {
                float denom = Vector3.Dot(n, dir);
                if (MathF.Abs(denom) < 1e-4f) continue;
                float t = Vector3.Dot(n, p[0] - origin) / denom;
                if (t < 0 || t >= best) continue;
                Vector3 hit = origin + dir * t;
                bool inside = true;
                for (int i = 0; i < p.Length && inside; i++)
                {
                    Vector3 edge = p[(i + 1) % p.Length] - p[i];
                    if (Vector3.Dot(Vector3.Cross(edge, hit - p[i]), n) < -1e-3f) inside = false;
                }
                if (inside) best = t;
            }
            return best;
        }
    }
}
