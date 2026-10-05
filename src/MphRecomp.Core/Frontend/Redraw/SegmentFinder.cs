using System;
using System.Collections.Generic;

// Line art -> DIGITAL STRAIGHT SEGMENTS fitted directly to the ink (greedy, Hough-like):
//  a. a segment = centre line + width w in {1,2}; in every column along its major axis it covers the
//     k = round(w / cos(angle)) pixels centred on the line (how pixel art draws a w-px line at that angle).
//     A thick (k >= 2) hand-drawn line may miss one pixel at an ISOLATED step column; two partial columns in a
//     row end the run; a k >= 2 band must span >= 2 columns.
//  b. coarse search (angle every 2 deg + exact 0/45, offset 0.25 px, w 1/2, x- and y-major) picks the run with the
//     most still-unexplained pixels, then the longest run, then the direction nearest 0/45/90.
//  c. a run may cross already-explained ink only as an OVERLAP (<= 3 columns from its own new pixels).
//  d. refine: the centre of the digital preimage (mean of all angle/offset pairs that explain the same pixels),
//     searched only near the segment.
//  e. snap to 0/45/90 when the snapped line explains every NEW pixel the free line does, or when within 8 deg and
//     it still explains >= 75 % of the ink. Snapped lines sit on the pixel lattice (.5 for odd k, .0 for even k).
//  f. free-angle lines follow the pixel-art (Bresenham) convention: through the centres of the first and last PURE
//     columns, if that stays within 0.75 px of every pure column's run centre.
//  g. mark the segment's pixels (+ a 1-px step thickening of thick lines) explained; repeat while >= 2 new pixels.
// Work happens in a frame where the line is x-major (|angle| <= 45): for a y-major line the mask is transposed.
// Line: minor = b + (major - xm) * tan(theta), evaluated at column centres (j + 0.5).
//
// Speed: the Python scored every (angle, offset) row of the coarse grid on every pass. Here each row keeps an upper
// bound of its new pixels (the unexplained pixels its band touches in ANY column, updated as pixels get explained),
// and a pass scores only rows whose bound can still reach the best count found: the same winner, exactly.
namespace MphRecomp.Frontend.Redraw
{
    internal sealed class Seg
    {
        public int Orient; // 0 = x-major, 1 = y-major
        public double Theta, B, Xm;
        public int W, K, J0, J1, Jg0, Jg1;
        public Vec2 C, D;
        public double Tlo, Thi;
        public List<(int Y, int X)> Mem = new(); // pixels it explains (image coords)
        public HashSet<long> MemSet = new();
    }

    internal sealed class SegmentFinder
    {
        private sealed class Frame
        {
            public int H, W;
            public byte[] Ink = Array.Empty<byte>(), Resid = Array.Empty<byte>();
        }

        // per-call scratch shared by the components of one Redraw call (never by two calls: thread-safe)
        internal sealed class Scratch
        {
            public int[] Hits = Array.Empty<int>(), Newc = Array.Empty<int>(), Far = Array.Empty<int>();
            public readonly List<double> Thetas = new(), Bs = new(), TieTh = new(), TieB = new();
            public readonly List<int> TieJ0 = new(), TieJ1 = new();
            public readonly List<(double Th, double B, RowResult R, double Sc)> Rows = new();
        }

        internal struct RowResult
        {
            public double Score;
            public int J0, J1, New, Length;
        }

        // one coarse grid: frame x/y and width 1/2
        private sealed class Combo
        {
            public Frame F = null!;
            public int Orient, W, Nb;
            public double Xm, B0;
            public double[] Theta = Array.Empty<double>(), Tan = Array.Empty<double>();
            public int[] K = Array.Empty<int>();
            public int[] Bound = Array.Empty<int>(); // [ti * Nb + bi]: unexplained pixels the band touches
            public double B(int bi) => bi == 0 ? B0 : B0 + bi * 0.25;
        }

        private readonly PixelRedraw.Options _o;
        private readonly Component _c;
        private readonly int _x0, _y0, _sw, _sh;
        private readonly Frame _fx, _fy;
        private readonly int[] _hits, _newc, _far;
        private readonly Scratch _s;
        private static readonly double[] CoarseThetas = MakeCoarse();

        private static double[] MakeCoarse()
        {
            var set = new SortedSet<double>(Np.Arange(-44.0, 45.0, 2.0)) { -45.0, 0.0, 45.0 };
            return new List<double>(set).ToArray();
        }

        public SegmentFinder(PixelRedraw.Options o, Component c, Scratch scratch)
        {
            _s = scratch;
            _o = o;
            _c = c;
            _y0 = Math.Max(c.Y0 - 2, 0);
            _x0 = Math.Max(c.X0 - 2, 0);
            int y1 = Math.Min(c.Y1 + 3, c.ImageH), x1 = Math.Min(c.X1 + 3, c.ImageW);
            _sh = y1 - _y0;
            _sw = x1 - _x0;
            _fx = new Frame { H = _sh, W = _sw, Ink = new byte[_sh * _sw], Resid = new byte[_sh * _sw] };
            _fy = new Frame { H = _sw, W = _sh, Ink = new byte[_sh * _sw], Resid = new byte[_sh * _sw] };
            for (int y = 0; y < _sh; y++)
            {
                for (int x = 0; x < _sw; x++)
                {
                    if (!c.At(y + _y0, x + _x0)) continue;
                    _fx.Ink[y * _sw + x] = _fx.Resid[y * _sw + x] = 1;
                    _fy.Ink[x * _sh + y] = _fy.Resid[x * _sh + y] = 1;
                }
            }
            int n = Math.Max(_sw, _sh);
            if (scratch.Hits.Length < n)
            {
                scratch.Hits = new int[n];
                scratch.Newc = new int[n];
                scratch.Far = new int[n];
            }
            _hits = scratch.Hits;
            _newc = scratch.Newc;
            _far = scratch.Far;
        }

        private static int BandBase(double mu, int k) =>
            (k & 1) != 0 ? (int)Math.Floor(mu) - (k - 1) / 2 : (int)Math.Floor(mu + 0.5) - k / 2;

        private static int KFor(int w, double theta) => Math.Max(1, (int)Np.Round(w / Math.Cos(theta * Geom.Deg)));

        // ----------------------------------------------------------------------------------------- one row score
        // best run (Kadane) of columns whose k band pixels are ink, inside columns lo..hi (the window)
        private void EvalRow(Frame f, double t, int k, double b, double xm, int lo, int hi, out RowResult res)
        {
            int W = f.W, H = f.H;
            res.J0 = res.J1 = 0;
            if (lo > hi)
            {
                res.Score = double.NegativeInfinity;
                res.New = 0;
                res.Length = 1;
                return;
            }
            int ov = _o.Overlap, m = Math.Max(ov, 1);
            int cLo = Math.Max(0, lo - m), cHi = Math.Min(W - 1, hi + m);
            byte[] ink = f.Ink, resid = f.Resid;
            int[] hits = _hits, newc = _newc, far = _far;
            for (int c = cLo; c <= cHi; c++)
            {
                double mu = b + ((c + 0.5) - xm) * t;
                int bse = BandBase(mu, k);
                int h = 0, nw = 0;
                for (int q = 0; q < k; q++)
                {
                    int r = bse + q;
                    if ((uint)r < (uint)H)
                    {
                        int i = r * W + c;
                        h += ink[i];
                        nw += resid[i];
                    }
                }
                hits[c] = h;
                newc[c] = nw;
            }
            // distance to the nearest column with new pixels
            int run = 1_000_000;
            for (int c = cLo; c <= cHi; c++)
            {
                run = newc[c] > 0 ? 0 : run + 1;
                far[c] = run;
            }
            run = 1_000_000;
            for (int c = cHi; c >= cLo; c--)
            {
                run = newc[c] > 0 ? 0 : run + 1;
                if (run < far[c]) far[c] = run;
            }
            double S = double.NegativeInfinity, best = double.NegativeInfinity;
            int st = 0, bs = 0, be = 0;
            for (int c = lo; c <= hi; c++)
            {
                int h = hits[c];
                bool ok = k >= 2 ? h >= k - 1 : h >= k;
                if (ok && h < k)
                {
                    // two partial columns in a row = not a line
                    if ((c - 1 >= 0 && hits[c - 1] < k) || (c + 1 < W && hits[c + 1] < k)) ok = false;
                }
                if (ok && far[c] > ov) ok = false;
                double v = h - 1.5 * (k - h);
                bool cont = S > 0;
                if (!cont) st = c;
                S = (cont ? S : 0) + v;
                if (!ok) S = double.NegativeInfinity;
                if (S > best)
                {
                    best = S;
                    bs = st;
                    be = c;
                }
            }
            if (k >= 2 && be - bs < 1) best = double.NegativeInfinity;
            int nsum = 0;
            if (!double.IsNegativeInfinity(best))
            {
                for (int c = bs; c <= be; c++) nsum += newc[c];
            }
            res.Score = best;
            res.J0 = bs;
            res.J1 = be;
            res.New = nsum;
            res.Length = be - bs + 1;
        }

        // ------------------------------------------------------------------------------------------ coarse grid
        private Combo[] MakeCombos()
        {
            var combos = new Combo[4];
            int ci = 0;
            for (int orient = 0; orient < 2; orient++)
            {
                Frame f = orient == 0 ? _fx : _fy;
                for (int w = 1; w <= 2; w++)
                {
                    double start = -f.W / 2.0 - 2, stop = f.H + f.W / 2.0 + 2;
                    int nb = (int)Math.Ceiling((stop - start) / 0.25);
                    var cb = new Combo
                    {
                        F = f, Orient = orient, W = w, Nb = nb, Xm = f.W / 2.0, B0 = start,
                        Theta = CoarseThetas, Tan = new double[CoarseThetas.Length], K = new int[CoarseThetas.Length],
                        Bound = new int[CoarseThetas.Length * nb]
                    };
                    for (int ti = 0; ti < CoarseThetas.Length; ti++)
                    {
                        cb.Tan[ti] = Math.Tan(CoarseThetas[ti] * Geom.Deg);
                        cb.K[ti] = KFor(w, CoarseThetas[ti]);
                    }
                    combos[ci++] = cb;
                }
            }
            return combos;
        }

        // add delta to the bound of every row whose band covers frame pixel (r, j)
        private static void Touch(Combo cb, int r, int j, int delta)
        {
            double xs = j + 0.5;
            for (int ti = 0; ti < cb.Theta.Length; ti++)
            {
                double o = (xs - cb.Xm) * cb.Tan[ti];
                int k = cb.K[ti];
                // base(bi) is non-decreasing in bi: rows with r - k + 1 <= base <= r
                int lo = FirstAtLeast(cb, o, k, r - k + 1);
                int hi = FirstAtLeast(cb, o, k, r + 1) - 1;
                int[] bound = cb.Bound;
                int row = ti * cb.Nb;
                for (int bi = lo; bi <= hi; bi++) bound[row + bi] += delta;
            }
        }

        private static int FirstAtLeast(Combo cb, double o, int k, int target)
        {
            int lo = 0, hi = cb.Nb; // answer in [0, Nb]
            while (lo < hi)
            {
                int mid = (lo + hi) >> 1;
                if (BandBase(cb.B(mid) + o, k) >= target) hi = mid;
                else lo = mid + 1;
            }
            return lo;
        }

        private struct Pick
        {
            public bool Found;
            public double Key;
            public int Combo, Ti, Bi;
            public RowResult Row;
        }

        private Pick Coarse(Combo[] combos, bool bruteForce)
        {
            var pick = new Pick();
            int maxBound = 0;
            foreach (Combo cb in combos)
            {
                foreach (int v in cb.Bound)
                {
                    if (v > maxBound) maxBound = v;
                }
            }
            int minNew = Math.Max(_o.MinNewPixels, 1);
            if (!bruteForce && maxBound < minNew) return pick;
            // new pixels outrank everything else in the key only while length * 1e3 + score * 10 stays below 1e6
            bool prune = !bruteForce && Math.Max(_fx.W, _fy.W) < 900;
            int bestNew = -1;
            int upper = int.MaxValue, lower = prune ? maxBound : int.MinValue;
            while (true)
            {
                for (int c = 0; c < combos.Length; c++)
                {
                    Combo cb = combos[c];
                    for (int ti = 0; ti < cb.Theta.Length; ti++)
                    {
                        double theta = cb.Theta[ti];
                        double off = Math.Abs(theta - Np.Round(theta / 45.0) * 45.0);
                        int row = ti * cb.Nb;
                        for (int bi = 0; bi < cb.Nb; bi++)
                        {
                            int bound = cb.Bound[row + bi];
                            if (bound < lower || bound >= upper) continue;
                            EvalRow(cb.F, cb.Tan[ti], cb.K[ti], cb.B(bi), cb.Xm, 0, cb.F.W - 1, out RowResult rr);
                            double score = double.IsNegativeInfinity(rr.Score) ? -1e2 : rr.Score;
                            double key = rr.New * 1e6 + rr.Length * 1e3 + score * 10 - off * 0.01;
                            if (rr.New > bestNew) bestNew = rr.New;
                            if (!pick.Found || key > pick.Key || (key == pick.Key && (c < pick.Combo || (c == pick.Combo && (ti < pick.Ti || (ti == pick.Ti && bi < pick.Bi))))))
                            {
                                pick = new Pick { Found = true, Key = key, Combo = c, Ti = ti, Bi = bi, Row = rr };
                            }
                        }
                    }
                }
                if (!prune || bestNew >= lower || lower <= minNew) break;
                upper = lower;
                lower = Math.Min(lower - 1, Math.Max(Math.Max(bestNew, minNew), lower * 3 / 4));
            }
            if (pick.Found && pick.Row.New < minNew) pick.Found = false;
            return pick;
        }

        // ---------------------------------------------------------------------------------------------- refine
        private struct Refined
        {
            public double Th, B, Xm;
            public int J0, J1;
        }

        private Refined Refine(Frame f, int w, double th, double b, double xm, int j0, int j1)
        {
            int jm = (j0 + j1) / 2;
            double bestNew = 0, bestFit = 0;
            int winLo = 0, winHi = 0;
            List<double> thetas = _s.Thetas, bsList = _s.Bs, tieTh = _s.TieTh, tieB = _s.TieB;
            List<int> tieJ0 = _s.TieJ0, tieJ1 = _s.TieJ1;
            List<(double Th, double B, RowResult R, double Sc)> rows = _s.Rows;
            for (int it = 0; it < 3; it++)
            {
                double t = Math.Tan(th * Geom.Deg);
                double xm2 = jm + 0.5;
                b = b + (xm2 - xm) * t;
                xm = xm2;
                double span = it == 0 ? 15.0 : 1.0;
                thetas.Clear();
                foreach (double v in Np.Arange(-span, span + 1e-9, it != 0 ? 0.05 : 0.25))
                {
                    double tv = th + v;
                    if (Math.Abs(tv) <= 45.0001) thetas.Add(tv);
                }
                bsList.Clear();
                foreach (double v in Np.Arange(-1.0, 1.0001, 0.05)) bsList.Add(b + v);
                int L = j1 - j0 + 1;
                winLo = j0 - L - 4;
                winHi = j1 + L + 4;
                bool skipped = EvalGrid(f, w, thetas, bsList, xm, winLo, winHi, jm, rows, full: false);
                double m = double.NegativeInfinity;
                foreach (var r in rows)
                {
                    if (r.Sc > m) m = r.Sc;
                }
                if (double.IsNegativeInfinity(m) && skipped) EvalGrid(f, w, thetas, bsList, xm, winLo, winHi, jm, rows, full: true);
                tieTh.Clear(); tieB.Clear(); tieJ0.Clear(); tieJ1.Clear();
                bestNew = double.NegativeInfinity;
                bestFit = double.NegativeInfinity;
                foreach (var r in rows)
                {
                    if (!(r.Sc >= m - 1e-6)) continue;
                    tieTh.Add(r.Th);
                    tieB.Add(r.B);
                    tieJ0.Add(r.R.J0);
                    tieJ1.Add(r.R.J1);
                    if (r.R.New > bestNew) bestNew = r.R.New;
                    if (r.R.Score > bestFit) bestFit = r.R.Score;
                }
                th = Np.Mean(tieTh);
                b = Np.Mean(tieB);
                j0 = Np.MedianInt(tieJ0);
                j1 = Np.MedianInt(tieJ1);
                jm = (j0 + j1) / 2;
            }
            double target = Np.Round(th / 45.0) * 45.0 + 0.0;
            if (th != target)
            {
                // snap to 0/45/90 when the snapped line explains every NEW pixel the free line does, or when it is
                // within SnapDegrees and still explains most of the ink
                bsList.Clear();
                foreach (double v in Np.Arange(-1.5, 1.5001, 0.05)) bsList.Add(b + v);
                thetas.Clear();
                thetas.Add(target);
                EvalGrid(f, w, thetas, bsList, xm, winLo, winHi, jm, rows, full: false);
                int bestI = -1;
                double bestSc = double.NegativeInfinity;
                for (int i = 0; i < rows.Count; i++)
                {
                    var r = rows[i];
                    bool valid = r.R.J0 <= jm && r.R.J1 >= jm && double.IsFinite(r.R.Score);
                    if (!valid) continue;
                    if (bestI < 0 || r.Sc > bestSc)
                    {
                        bestI = i;
                        bestSc = r.Sc;
                    }
                }
                if (bestI >= 0)
                {
                    var r = rows[bestI];
                    bool sameNew = r.R.New >= bestNew;
                    bool close = Math.Abs(th - target) <= _o.SnapDegrees && r.R.Score >= 0.75 * bestFit;
                    if (sameNew || close)
                    {
                        tieB.Clear(); tieJ0.Clear(); tieJ1.Clear();
                        foreach (var q in rows)
                        {
                            bool valid = q.R.J0 <= jm && q.R.J1 >= jm && double.IsFinite(q.R.Score);
                            double sc = valid ? q.Sc : double.NegativeInfinity;
                            if (!(sc >= bestSc - 1e-6)) continue;
                            tieB.Add(q.B);
                            tieJ0.Add(q.R.J0);
                            tieJ1.Add(q.R.J1);
                        }
                        th = target;
                        b = Np.Mean(tieB);
                        j0 = Np.MedianInt(tieJ0);
                        j1 = Np.MedianInt(tieJ1);
                    }
                }
            }
            if (th == target)
            {
                // exact pixel-art lines sit on the half-pixel lattice: a k-pixel band is centred on .5 (odd k) or .0
                int k = KFor(w, th);
                b = k % 2 != 0 ? Math.Floor(b) + 0.5 : Np.Round(b);
            }
            return new Refined { Th = th, B = b, Xm = xm, J0 = j0, J1 = j1 };
        }

        // every (theta, b) row (theta-major), window-limited; Sc = score + 1000 * new for runs containing jm
        // Rows whose band is not a valid run column at jm cannot have a best run containing jm (every column of a run
        // is valid), so they score -inf without a full evaluation; Skipped marks them. With full = true every row is
        // evaluated (needed only when no row reaches jm at all: then the Python averaged over every row).
        private bool EvalGrid(Frame f, int w, List<double> thetas, List<double> bs, double xm, int winLo, int winHi, int jm,
            List<(double Th, double B, RowResult R, double Sc)> rows, bool full)
        {
            rows.Clear();
            bool skipped = false;
            int lo = Math.Max(winLo, 0), hi = Math.Min(winHi, f.W - 1);
            foreach (double th in thetas)
            {
                double t = Math.Tan(th * Geom.Deg);
                int k = KFor(w, th);
                foreach (double b in bs)
                {
                    if (!full && !ValidAt(f, t, k, b, xm, lo, hi, jm))
                    {
                        skipped = true;
                        rows.Add((th, b, new RowResult { Score = double.NegativeInfinity, Length = 1 }, double.NegativeInfinity));
                        continue;
                    }
                    EvalRow(f, t, k, b, xm, lo, hi, out RowResult rr);
                    double sc = rr.J0 <= jm && rr.J1 >= jm ? rr.Score + 1000 * (double)rr.New : double.NegativeInfinity;
                    rows.Add((th, b, rr, sc));
                }
            }
            return skipped;
        }

        private (int Hits, int New) Band(Frame f, double t, int k, double b, double xm, int c)
        {
            double mu = b + ((c + 0.5) - xm) * t;
            int bse = BandBase(mu, k), h = 0, nw = 0;
            for (int q = 0; q < k; q++)
            {
                int r = bse + q;
                if ((uint)r < (uint)f.H)
                {
                    h += f.Ink[r * f.W + c];
                    nw += f.Resid[r * f.W + c];
                }
            }
            return (h, nw);
        }

        // EvalRow's ok test for column jm alone (same band, pair and overlap rules)
        private bool ValidAt(Frame f, double t, int k, double b, double xm, int lo, int hi, int jm)
        {
            if (jm < lo || jm > hi) return false;
            int W = f.W, ov = _o.Overlap;
            int h = Band(f, t, k, b, xm, jm).Hits;
            if (!(k >= 2 ? h >= k - 1 : h >= k)) return false;
            if (h < k && ((jm - 1 >= 0 && Band(f, t, k, b, xm, jm - 1).Hits < k) || (jm + 1 < W && Band(f, t, k, b, xm, jm + 1).Hits < k))) return false;
            for (int c = Math.Max(0, jm - ov); c <= Math.Min(W - 1, jm + ov); c++)
            {
                if (Band(f, t, k, b, xm, c).New > 0) return true;
            }
            return false;
        }

        // ------------------------------------------------------------------------------------------- segments
        private Seg MakeSeg(int orient, double theta, double b, double xm, int w, int j0, int j1)
        {
            Frame f = orient == 0 ? _fx : _fy;
            int H = f.H;
            var s = new Seg { Orient = orient, Theta = theta, B = b, Xm = xm, W = w, J0 = j0, J1 = j1 };
            s.K = KFor(w, theta);
            double t = Math.Tan(theta * Geom.Deg);
            byte[] ink = f.Ink;
            bool Ink(int r, int j) => (uint)r < (uint)H && (uint)j < (uint)f.W && ink[r * f.W + j] != 0;
            for (int j = j0; j <= j1; j++)
            {
                double mu = b + (j + 0.5 - xm) * t;
                int r0 = BandBase(mu, s.K);
                int lo = r0, hi = r0 + s.K - 1;
                if (s.K >= 2)
                {
                    // a one-pixel step thickening of thick lines
                    if (lo - 1 >= 0 && Ink(lo - 1, j) && !(lo - 2 >= 0 && Ink(lo - 2, j))) lo--;
                    if (hi + 1 < H && Ink(hi + 1, j) && !(hi + 2 < H && Ink(hi + 2, j))) hi++;
                }
                for (int r = lo; r <= hi; r++)
                {
                    if (r >= 0 && r < H && Ink(r, j))
                    {
                        (int y, int x) = orient == 0 ? (r, j) : (j, r);
                        if (s.MemSet.Add((long)y << 32 | (uint)x)) s.Mem.Add((y, x));
                    }
                }
            }
            Vec2 P(double maj)
            {
                double mn = b + (maj - xm) * t;
                return orient == 0 ? new Vec2(maj, mn) : new Vec2(mn, maj);
            }
            Vec2 p0 = P(j0), p1 = P(j1 + 1.0);
            Vec2 d = p1 - p0;
            double n = d.Norm;
            s.C = p0;
            s.D = d / n;
            s.Tlo = 0;
            s.Thi = n;
            return s;
        }

        private bool AnyResid(Seg s)
        {
            foreach ((int y, int x) in s.Mem)
            {
                if (_fx.Resid[y * _sw + x] != 0) return true;
            }
            return false;
        }

        // greedy digital straight segments on this component; left = pixels nothing explains (image coords)
        public List<Seg> Find(out List<(int Y, int X)> left)
        {
            var segs = new List<Seg>();
            Combo[] combos = MakeCombos();
            int residCount = 0;
            for (int y = 0; y < _sh; y++)
            {
                for (int x = 0; x < _sw; x++)
                {
                    if (_fx.Resid[y * _sw + x] == 0) continue;
                    residCount++;
                    TouchPixel(combos, y, x, +1);
                }
            }
            for (int iter = 0; iter < _o.MaxSegmentsPerComponent; iter++)
            {
                if (residCount == 0) break;
                Pick p = Coarse(combos, _o.BruteForceSearch);
                if (!p.Found) break;
                Combo cb = combos[p.Combo];
                double th0 = cb.Theta[p.Ti], b0 = cb.B(p.Bi), xm0 = cb.Xm;
                Refined rf = Refine(cb.F, cb.W, th0, b0, xm0, p.Row.J0, p.Row.J1);
                Seg s = MakeSeg(cb.Orient, rf.Th, rf.B, rf.Xm, cb.W, rf.J0, rf.J1);
                if (!AnyResid(s))
                {
                    s = MakeSeg(cb.Orient, th0, b0, xm0, cb.W, p.Row.J0, p.Row.J1);
                    if (!AnyResid(s)) break; // stuck
                }
                foreach ((int y, int x) in s.Mem)
                {
                    if (_fx.Resid[y * _sw + x] == 0) continue;
                    _fx.Resid[y * _sw + x] = 0;
                    _fy.Resid[x * _sh + y] = 0;
                    residCount--;
                    TouchPixel(combos, y, x, -1);
                }
                // back to image coords
                int off = cb.Orient == 0 ? _x0 : _y0;
                s.Jg0 = rf.J0 + off;
                s.Jg1 = rf.J1 + off;
                s.C = s.C + new Vec2(_x0, _y0);
                var mem = new List<(int Y, int X)>(s.Mem.Count);
                var set = new HashSet<long>();
                foreach ((int y, int x) in s.Mem)
                {
                    mem.Add((y + _y0, x + _x0));
                    set.Add((long)(y + _y0) << 32 | (uint)(x + _x0));
                }
                s.Mem = mem;
                s.MemSet = set;
                segs.Add(s);
            }
            AnchorFreeSegments(segs);
            left = new List<(int Y, int X)>();
            for (int y = 0; y < _sh; y++)
            {
                for (int x = 0; x < _sw; x++)
                {
                    if (_fx.Resid[y * _sw + x] != 0) left.Add((y + _y0, x + _x0));
                }
            }
            return segs;
        }

        private static void TouchPixel(Combo[] combos, int y, int x, int delta)
        {
            foreach (Combo cb in combos)
            {
                if (cb.Orient == 0) Touch(cb, y, x, delta);
                else Touch(cb, x, y, delta);
            }
        }

        // Pixel-art (Bresenham) convention for free-angle lines: a line is drawn FROM end pixel TO end pixel, so its
        // centre line runs through the centres of its first and last PURE columns (the column's ink run is exactly the
        // line's own k pixels). Kept only if it stays within max_dev of every pure column's run centre.
        private void AnchorFreeSegments(List<Seg> segs)
        {
            Component c = _c;
            foreach (Seg s in segs)
            {
                if (Math.Abs(s.Theta - Np.Round(s.Theta / 45.0) * 45.0) < 1e-9 || s.Jg1 - s.Jg0 + 1 < _o.AnchorMinColumns) continue;
                bool M(int r, int j) => s.Orient == 0 ? c.At(r, j) : c.At(j, r);
                int mh = s.Orient == 0 ? c.ImageH : c.ImageW;
                var lo = new Dictionary<int, int>();
                var hi = new Dictionary<int, int>();
                foreach ((int y, int x) in s.Mem)
                {
                    (int j, int r) = s.Orient == 0 ? (x, y) : (y, x);
                    if (!lo.TryGetValue(j, out int l) || r < l) lo[j] = r;
                    if (!hi.TryGetValue(j, out int h) || r > h) hi[j] = r;
                }
                var pure = new List<(double M, double N)>();
                for (int j = s.Jg0; j <= s.Jg1; j++)
                {
                    if (!lo.TryGetValue(j, out int l)) continue;
                    int h = hi[j];
                    while (l - 1 >= 0 && M(l - 1, j)) l--;
                    while (h + 1 < mh && M(h + 1, j)) h++;
                    if (h - l + 1 == s.K) pure.Add((j + 0.5, (l + h + 1) / 2.0));
                }
                if (pure.Count < 2) continue;
                (double ma, double na) = pure[0];
                (double mb, double nb) = pure[^1];
                double slope = (nb - na) / (mb - ma);
                double dev = 0;
                foreach ((double m, double n) in pure) dev = Math.Max(dev, Math.Abs(n - (na + (m - ma) * slope)));
                if (dev > _o.AnchorMaxDeviation) continue;
                Vec2 P(double m)
                {
                    double n = na + (m - ma) * slope;
                    return s.Orient == 0 ? new Vec2(m, n) : new Vec2(n, m);
                }
                Vec2 p0 = P(s.Jg0), p1 = P(s.Jg1 + 1);
                Vec2 d = p1 - p0;
                double len = d.Norm;
                s.C = p0;
                s.D = d / len;
                s.Tlo = 0;
                s.Thi = len;
                s.Theta = Math.Atan(slope) * (180.0 / Math.PI);
            }
        }
    }
}
