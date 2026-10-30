using System;
using System.Collections.Generic;

namespace MphRecomp.Frontend.Redraw
{
    internal readonly struct Vec2
    {
        public readonly double X, Y;
        public Vec2(double x, double y) { X = x; Y = y; }
        public static Vec2 operator +(Vec2 a, Vec2 b) => new(a.X + b.X, a.Y + b.Y);
        public static Vec2 operator -(Vec2 a, Vec2 b) => new(a.X - b.X, a.Y - b.Y);
        public static Vec2 operator -(Vec2 a) => new(-a.X, -a.Y);
        public static Vec2 operator *(Vec2 a, double s) => new(a.X * s, a.Y * s);
        public static Vec2 operator *(double s, Vec2 a) => new(s * a.X, s * a.Y);
        public static Vec2 operator /(Vec2 a, double s) => new(a.X / s, a.Y / s);
        public double Dot(Vec2 b) => X * b.X + Y * b.Y;
        public double Norm => Math.Sqrt(X * X + Y * Y);
        public override string ToString() => $"({X:0.###}, {Y:0.###})";
    }

    internal static class Geom
    {
        public const double Deg = Math.PI / 180.0;

        // where two lines (point + direction) cross; parallel lines -> the midpoint of their points
        public static Vec2 Intersect(Vec2 p, Vec2 d, Vec2 q, Vec2 e)
        {
            // [d, -e] t = q - p, solved like LAPACK getrf/getrs: partial pivoting, the column scaled by the
            // reciprocal pivot, then forward and back substitution
            double a11 = d.X, a12 = -e.X, a21 = d.Y, a22 = -e.Y;
            double r1 = q.X - p.X, r2 = q.Y - p.Y;
            bool swap = Math.Abs(a21) > Math.Abs(a11);
            if (swap)
            {
                (a11, a21) = (a21, a11);
                (a12, a22) = (a22, a12);
                (r1, r2) = (r2, r1);
            }
            double l = a11 != 0 ? a21 * (1.0 / a11) : 0;
            double u22 = a22 - l * a12;
            double det = a11 * u22 * (swap ? -1 : 1);
            if (Math.Abs(det) < 1e-6) return (p + q) / 2;
            double t1 = (r2 - r1 * l) / u22;
            double t0 = (r1 - t1 * a12) / a11;
            return p + d * t0;
        }

        // Douglas-Peucker on pts[lo..hi]; returns the kept indices
        public static void DouglasPeucker(IReadOnlyList<(int X, int Y)> pts, int lo, int hi, double tol, List<int> outIdx)
        {
            if (hi - lo + 1 < 3)
            {
                for (int i = lo; i <= hi; i++) outIdx.Add(i);
                return;
            }
            double ax = pts[lo].X, ay = pts[lo].Y;
            double abx = pts[hi].X - ax, aby = pts[hi].Y - ay;
            double n = Hypot(abx, aby);
            double best = -1;
            int idx = lo;
            for (int i = lo + 1; i < hi; i++)
            {
                double px = pts[i].X, py = pts[i].Y;
                double d = n > 1e-9 ? Math.Abs(abx * (ay - py) - aby * (ax - px)) / n : Hypot(px - ax, py - ay);
                if (d > best)
                {
                    best = d;
                    idx = i;
                }
            }
            if (best <= tol)
            {
                outIdx.Add(lo);
                outIdx.Add(hi);
                return;
            }
            var left = new List<int>();
            DouglasPeucker(pts, lo, idx, tol, left);
            left.RemoveAt(left.Count - 1);
            outIdx.AddRange(left);
            DouglasPeucker(pts, idx, hi, tol, outIdx);
        }

        public static double Hypot(double x, double y)
        {
            // numpy/C hypot is correctly scaled; plain sqrt is identical for these (small, exact) inputs
            return Math.Sqrt(x * x + y * y);
        }

        // areas: DP corners -> least-squares line per edge -> corners = intersections; jogs dropped
        public static List<Vec2> TidyPolygon(List<(int X, int Y)> pts, double tol, double minEdge)
        {
            var closed = new List<(int X, int Y)>(pts) { pts[0] };
            var simple = new List<int>();
            DouglasPeucker(closed, 0, closed.Count - 1, tol, simple);
            simple.RemoveAt(simple.Count - 1);
            for (int it = 0; it < 6; it++)
            {
                if (simple.Count <= 3) break;
                int k = 0;
                double kl = double.PositiveInfinity;
                for (int i = 0; i < simple.Count; i++)
                {
                    (int X, int Y) a = pts[simple[i]], b = pts[simple[(i + 1) % simple.Count]];
                    double l = Hypot(b.X - a.X, b.Y - a.Y);
                    if (l < kl)
                    {
                        kl = l;
                        k = i;
                    }
                }
                if (kl < minEdge)
                {
                    simple.RemoveAt((k + 1) % simple.Count);
                    continue;
                }
                break;
            }
            int m = simple.Count;
            var lines = new (Vec2 C, Vec2 D)[m];
            var seg = new List<(int X, int Y)>();
            for (int i = 0; i < m; i++)
            {
                int a = simple[i], b = simple[(i + 1) % m];
                seg.Clear();
                if (a <= b)
                {
                    for (int j = a; j <= b; j++) seg.Add(pts[j]);
                }
                else
                {
                    for (int j = a; j < pts.Count; j++) seg.Add(pts[j]);
                    for (int j = 0; j <= b; j++) seg.Add(pts[j]);
                }
                lines[i] = FitLine(seg);
            }
            var res = new List<Vec2>(m);
            for (int i = 0; i < m; i++)
            {
                (Vec2 C, Vec2 D) l1 = lines[(i - 1 + m) % m], l2 = lines[i];
                res.Add(Intersect(l1.C, l1.D, l2.C, l2.D));
            }
            return res;
        }

        // centroid + principal direction (the SVD's first right singular vector)
        public static (Vec2 C, Vec2 D) FitLine(List<(int X, int Y)> pts)
        {
            double sx = 0, sy = 0;
            foreach ((int x, int y) in pts)
            {
                sx += x;
                sy += y;
            }
            double cx = sx / pts.Count, cy = sy / pts.Count;
            double xx = 0, xy = 0, yy = 0;
            foreach ((int x, int y) in pts)
            {
                double dx = x - cx, dy = y - cy;
                xx += dx * dx;
                xy += dx * dy;
                yy += dy * dy;
            }
            // largest-eigenvalue eigenvector of [[xx, xy], [xy, yy]]; exact for axis-aligned edges (as the SVD is),
            // so corners on the pixel lattice stay exactly on it
            if (xy == 0) return (new Vec2(cx, cy), xx >= yy ? new Vec2(1, 0) : new Vec2(0, 1));
            double half = (xx - yy) / 2, l1 = (xx + yy) / 2 + Math.Sqrt(half * half + xy * xy);
            double ux = xy, uy = l1 - xx, vx = l1 - yy, vy = xy;
            (double dx2, double dy2) = ux * ux + uy * uy >= vx * vx + vy * vy ? (ux, uy) : (vx, vy);
            double n = Math.Sqrt(dx2 * dx2 + dy2 * dy2);
            return (new Vec2(cx, cy), new Vec2(dx2 / n, dy2 / n));
        }
    }

    // numpy's exact arithmetic where the Python's results depend on it
    internal static class Np
    {
        // np.arange(start, stop, step): length ceil((stop - start) / step); a[i] = start + i * ((start + step) - start)
        public static double[] Arange(double start, double stop, double step)
        {
            int n = (int)Math.Ceiling((stop - start) / step);
            if (n <= 0) return Array.Empty<double>();
            var a = new double[n];
            a[0] = start;
            if (n > 1) a[1] = start + step;
            double delta = (n > 1 ? a[1] : start + step) - start;
            for (int i = 2; i < n; i++) a[i] = start + i * delta;
            return a;
        }

        // np.add.reduce's pairwise summation (float64)
        public static double PairwiseSum(List<double> a, int start, int n)
        {
            if (n < 8)
            {
                double res = 0.0;
                for (int i = 0; i < n; i++) res += a[start + i];
                return res;
            }
            if (n <= 128)
            {
                double r0 = a[start], r1 = a[start + 1], r2 = a[start + 2], r3 = a[start + 3];
                double r4 = a[start + 4], r5 = a[start + 5], r6 = a[start + 6], r7 = a[start + 7];
                int i;
                for (i = 8; i < n - n % 8; i += 8)
                {
                    r0 += a[start + i]; r1 += a[start + i + 1]; r2 += a[start + i + 2]; r3 += a[start + i + 3];
                    r4 += a[start + i + 4]; r5 += a[start + i + 5]; r6 += a[start + i + 6]; r7 += a[start + i + 7];
                }
                double res = ((r0 + r1) + (r2 + r3)) + ((r4 + r5) + (r6 + r7));
                for (; i < n; i++) res += a[start + i];
                return res;
            }
            int n2 = n / 2;
            n2 -= n2 % 8;
            return PairwiseSum(a, start, n2) + PairwiseSum(a, start + n2, n - n2);
        }

        public static double Mean(List<double> a) => (0.0 + PairwiseSum(a, 0, a.Count)) / a.Count;

        // int(np.median(ints))
        public static int MedianInt(List<int> a)
        {
            var s = new List<int>(a);
            s.Sort();
            int n = s.Count;
            double m = n % 2 == 1 ? s[n / 2] : (s[n / 2 - 1] + (double)s[n / 2]) / 2.0;
            return (int)m;
        }

        // Python's round(x) (half to even), as a double
        public static double Round(double x) => Math.Round(x, MidpointRounding.ToEven);
    }
}
