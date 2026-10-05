using System;
using System.Collections.Generic;

// Joins from geometry: for touching, non-parallel segments the centre lines are intersected at X.
//   X near an end of both (<= 1 + 0.75 * max width) -> CORNER (mitred join, same stroke; candidates sorted by width
//   change, then by -turn); X near an end of one and inside the other -> T (butt end on the other's centre line);
//   otherwise the end is free -> square cap flush with the last pixel edge.
// Strokes = chains of corner joins, each drawn as ONE polygon (per-segment width, mitred joins, square/butt caps).
namespace MphRecomp.Frontend.Redraw
{
    internal sealed class Stroke
    {
        public readonly List<Vec2> Pts = new();
        public readonly List<double> Widths = new();
        public bool SquareStart, SquareEnd;
        public readonly List<int> Ids = new();
    }

    internal static class Strokes
    {
        private const int Lo = 0, Hi = 1;

        private static bool Adjacent(Seg a, Seg b)
        {
            foreach ((int y, int x) in a.Mem)
            {
                for (int dy = -1; dy <= 1; dy++)
                {
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        if (b.MemSet.Contains((long)(y + dy) << 32 | (uint)(x + dx))) return true;
                    }
                }
            }
            return false;
        }

        private readonly struct Cand
        {
            public readonly int Kind, Order;
            public readonly double WDiff, NegTurn;
            public readonly int I, Ei, J, Ej;
            public readonly Vec2 X;
            public Cand(int kind, double wdiff, double negTurn, int i, int ei, int j, int ej, Vec2 x, int order)
            {
                Kind = kind; WDiff = wdiff; NegTurn = negTurn; I = i; Ei = ei; J = j; Ej = ej; X = x; Order = order;
            }
        }

        public static List<Stroke> Build(List<Seg> segs, PixelRedraw.Options o)
        {
            int n = segs.Count;
            var corner = new Dictionary<(int, int), (int J, int Ej, Vec2 X)>();
            var tee = new Dictionary<(int, int), Vec2>();
            var cands = new List<Cand>();
            double parallel = Math.Sin(o.ParallelDegrees * Geom.Deg);
            for (int i = 0; i < n; i++)
            {
                for (int j = i + 1; j < n; j++)
                {
                    Seg A = segs[i], B = segs[j];
                    if (Math.Abs(A.D.X * B.D.Y - A.D.Y * B.D.X) < parallel) continue;
                    if (!Adjacent(A, B)) continue;
                    Vec2 X = Geom.Intersect(A.C, A.D, B.C, B.D);
                    double m = 1.0 + 0.75 * Math.Max(A.W, B.W);
                    (int ea, double _) = Near(A, X, m);
                    (int eb, double _) = Near(B, X, m);
                    double turn = Math.Acos(Math.Min(1, Math.Abs(A.D.Dot(B.D)))) * (180.0 / Math.PI);
                    if (ea <= Hi && eb <= Hi) cands.Add(new Cand(0, Math.Abs(A.W - B.W), -turn, i, ea, j, eb, X, cands.Count));
                    else if (ea <= Hi && eb == In) cands.Add(new Cand(1, 0, 0, i, ea, j, -1, X, cands.Count));
                    else if (eb <= Hi && ea == In) cands.Add(new Cand(1, 0, 0, j, eb, i, -1, X, cands.Count));
                }
            }
            cands.Sort((a, b) =>
            {
                int c = a.Kind.CompareTo(b.Kind);
                if (c == 0) c = a.WDiff.CompareTo(b.WDiff);
                if (c == 0) c = a.NegTurn.CompareTo(b.NegTurn);
                return c != 0 ? c : a.Order.CompareTo(b.Order);
            });
            foreach (Cand c in cands)
            {
                if (c.Kind == 0)
                {
                    if (corner.ContainsKey((c.I, c.Ei)) || corner.ContainsKey((c.J, c.Ej))) continue;
                    corner[(c.I, c.Ei)] = (c.J, c.Ej, c.X);
                    corner[(c.J, c.Ej)] = (c.I, c.Ei, c.X);
                }
                else
                {
                    if (corner.ContainsKey((c.I, c.Ei)) || tee.ContainsKey((c.I, c.Ei))) continue;
                    tee[(c.I, c.Ei)] = c.X;
                }
            }
            // walk the chains: free ends first, then whatever is left (closed loops)
            var chains = new List<List<(int S, int Ent, int Ext)>>();
            var seen = new HashSet<int>();
            var starts = new List<(int, int)>();
            for (int i = 0; i < n; i++)
            {
                for (int e = Lo; e <= Hi; e++)
                {
                    if (!corner.ContainsKey((i, e))) starts.Add((i, e));
                }
            }
            for (int i = 0; i < n; i++) starts.Add((i, Lo));
            foreach ((int i, int e) in starts)
            {
                if (seen.Contains(i)) continue;
                var chain = new List<(int S, int Ent, int Ext)>();
                int cur = i, ent = e;
                while (cur >= 0 && !seen.Contains(cur))
                {
                    seen.Add(cur);
                    int ext = ent == Lo ? Hi : Lo;
                    chain.Add((cur, ent, ext));
                    if (corner.TryGetValue((cur, ext), out var nx)) (cur, ent) = (nx.J, nx.Ej);
                    else cur = -1;
                }
                chains.Add(chain);
            }
            var res = new List<Stroke>();
            foreach (var chain in chains)
            {
                var st = new Stroke();
                for (int idx = 0; idx < chain.Count; idx++)
                {
                    (int si, int ent, int ext) = chain[idx];
                    Seg S = segs[si];
                    if (idx == 0)
                    {
                        if (tee.TryGetValue((si, ent), out Vec2 tx))
                        {
                            st.Pts.Add(tx);
                            st.SquareStart = false;
                        }
                        else
                        {
                            double t = ent == Lo ? S.Tlo + S.W / 2.0 : S.Thi - S.W / 2.0;
                            st.Pts.Add(S.C + S.D * t);
                            st.SquareStart = true;
                        }
                    }
                    if (corner.TryGetValue((si, ext), out var nxt) && idx + 1 < chain.Count)
                    {
                        st.Pts.Add(nxt.X);
                    }
                    else if (tee.TryGetValue((si, ext), out Vec2 tx))
                    {
                        st.Pts.Add(tx);
                        st.SquareEnd = false;
                    }
                    else
                    {
                        double t = ext == Hi ? S.Thi - S.W / 2.0 : S.Tlo + S.W / 2.0;
                        st.Pts.Add(S.C + S.D * t);
                        st.SquareEnd = true;
                    }
                    st.Widths.Add(S.W);
                    st.Ids.Add(si);
                }
                res.Add(st);
            }
            return res;
        }

        private const int In = 2, Out = 3;

        private static (int End, double T) Near(Seg s, Vec2 x, double m)
        {
            double t = (x - s.C).Dot(s.D);
            if (Math.Abs(t - s.Tlo) <= m && Math.Abs(t - s.Tlo) <= Math.Abs(t - s.Thi)) return (Lo, t);
            if (Math.Abs(t - s.Thi) <= m) return (Hi, t);
            return (s.Tlo < t && t < s.Thi ? In : Out, t);
        }

        // the stroke as one polygon (zero-length pieces dropped), or PIL's rectangle for a zero-length stroke
        public static Shape? ToShape(Stroke st, int colour)
        {
            var P = new List<Vec2> { st.Pts[0] };
            var Wd = new List<double>();
            for (int i = 1; i < st.Pts.Count; i++)
            {
                if ((st.Pts[i] - P[^1]).Norm > 1e-6)
                {
                    P.Add(st.Pts[i]);
                    Wd.Add(st.Widths[i - 1]);
                }
            }
            if (P.Count < 2)
            {
                double r = st.Widths[0] / 2;
                return new Shape { IsRect = true, Pts = new[] { P[0].X - r, P[0].Y - r, P[0].X + r, P[0].Y + r }, Colour = colour };
            }
            return Shape.Polygon(StrokePolygon(P, Wd, st.SquareStart, st.SquareEnd), colour);
        }

        // offset lines (own half-width per segment) joined by mitres; square or butt ends
        public static List<Vec2> StrokePolygon(List<Vec2> pts, List<double> widths, bool squareStart, bool squareEnd)
        {
            int n = pts.Count - 1;
            var dirs = new Vec2[n];
            var norms = new Vec2[n];
            for (int i = 0; i < n; i++)
            {
                Vec2 d = pts[i + 1] - pts[i];
                dirs[i] = d / d.Norm;
                norms[i] = new Vec2(-dirs[i].Y, dirs[i].X);
            }
            Vec2 a = squareStart ? pts[0] - dirs[0] * widths[0] / 2 : pts[0];
            Vec2 b = squareEnd ? pts[n] + dirs[n - 1] * widths[n - 1] / 2 : pts[n];
            var sides = new List<Vec2>[2];
            for (int s = 0; s < 2; s++)
            {
                double sgn = s == 0 ? 1 : -1;
                var side = new List<Vec2> { a + sgn * norms[0] * widths[0] / 2 };
                for (int i = 1; i < n; i++)
                {
                    Vec2 p = pts[i - 1] + sgn * norms[i - 1] * widths[i - 1] / 2;
                    Vec2 q = pts[i] + sgn * norms[i] * widths[i] / 2;
                    side.Add(Geom.Intersect(p, dirs[i - 1], q, dirs[i]));
                }
                side.Add(b + sgn * norms[n - 1] * widths[n - 1] / 2);
                sides[s] = side;
            }
            var poly = new List<Vec2>(sides[0]);
            for (int i = sides[1].Count - 1; i >= 0; i--) poly.Add(sides[1][i]);
            return poly;
        }
    }
}
