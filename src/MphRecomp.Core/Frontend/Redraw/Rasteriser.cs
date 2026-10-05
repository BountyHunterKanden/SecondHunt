using System;
using System.Collections.Generic;

// The Python filled every shape with PIL's ImageDraw.polygon on an RGBA canvas at scale * SS (each shape REPLACES the
// pixels it covers; holes are filled with transparent), mirrored the canvas's left half onto the right, and
// downsampled with Image.resize(LANCZOS). Reproduced here to the pixel:
//  - PIL truncates float vertices to ints, then fills scanline y between ROUND_UP(x_left) and ROUND_DOWN(x_right)
//    (both inclusive) from pairs of edge crossings, plus horizontal edges as lines (so boundaries are inclusive);
//  - Pillow's resize premultiplies RGBA, runs a horizontal then a vertical 8-bit fixed-point pass (22 fraction bits)
//    with normalised filter taps, then un-premultiplies.
// Only a rolling band of supersampled rows exists at a time: canvas rows are painted on demand, run through the
// horizontal pass and dropped once no output row needs them.
namespace MphRecomp.Frontend.Redraw
{
    internal static class Rasteriser
    {
        private const int PrecisionBits = 32 - 8 - 2;

        private sealed class PEdge
        {
            public int X0, Y0, YMin, YMax;
            public float Dx;
        }

        private sealed class PShape
        {
            public int Colour, YTop, YBottom; // rows it can touch (inclusive)
            public int LoopY0, LoopY1;        // PIL's clipped scanline range
            public PEdge[] Edges = Array.Empty<PEdge>();
            public (int Y, int X0, int X1)[] Flat = Array.Empty<(int, int, int)>();
            public bool IsRect;
            public int RX0, RY0, RX1, RY1;
        }

        public static DsImage Render(Drawing d, float scale, PixelRedraw.Options o)
        {
            double sc = scale;
            double S = sc * o.SuperSample;
            int wc = (int)Np.Round(d.Width * S), hc = (int)Np.Round(d.Height * S);
            int wo = (int)Np.Round(d.Width * sc), ho = (int)Np.Round(d.Height * sc);
            var output = new byte[Math.Max(wo, 0) * Math.Max(ho, 0) * 4];
            if (wo <= 0 || ho <= 0 || wc <= 0 || hc <= 0) return new DsImage { Width = Math.Max(wo, 0), Height = Math.Max(ho, 0), Rgba = output };
            var shapes = new List<PShape>(d.Shapes.Count);
            foreach (Shape s in d.Shapes)
            {
                PShape? p = Prepare(s, S, hc);
                if (p != null) shapes.Add(p);
            }
            // palette, premultiplied (all inks are opaque)
            var pal = new int[d.Palette.Count * 4];
            for (int i = 1; i < d.Palette.Count; i++)
            {
                uint c = d.Palette[i];
                pal[i * 4] = (int)(c >> 16 & 255);
                pal[i * 4 + 1] = (int)(c >> 8 & 255);
                pal[i * 4 + 2] = (int)(c & 255);
                pal[i * 4 + 3] = 255;
            }
            Filter f = o.Filter == RedrawFilter.Box ? Filter.Box : Filter.Lanczos;
            bool needH = wo != wc, needV = ho != hc;
            (int[] bh, int[] kh, int ksh) = Coeffs(wc, wo, f);
            (int[] bv, int[] kv, int ksv) = Coeffs(hc, ho, f);
            var painter = new Painter(shapes, wc, hc, d.Mirrored);
            const int half = 1 << (PrecisionBits - 1);
            // horizontal taps as prefix sums (a run of one ink adds ink * the sum of its taps: the same integers as tap
            // by tap), and the output columns each canvas column feeds
            int kps = ksh + 1;
            var kph = new int[wo * kps];
            var firstOut = new int[wc];
            var lastOut = new int[wc];
            Array.Fill(firstOut, int.MaxValue);
            Array.Fill(lastOut, -1);
            for (int xx = 0; xx < wo; xx++)
            {
                int xmin = bh[xx * 2], n = bh[xx * 2 + 1];
                for (int i = 0; i < n; i++)
                {
                    kph[xx * kps + i + 1] = kph[xx * kps + i] + kh[xx * ksh + i];
                    int x = xmin + i;
                    if (xx < firstOut[x]) firstOut[x] = xx;
                    if (xx > lastOut[x]) lastOut[x] = xx;
                }
            }
            // ring of horizontally-resampled rows (premultiplied RGBA at output width) + their non-transparent spans
            int cap = (needV ? ksv : 1) + 2;
            var ring = new byte[cap][];
            var ringRow = new int[cap];
            var spans = new List<int>[cap];
            for (int i = 0; i < cap; i++)
            {
                ring[i] = new byte[wo * 4];
                ringRow[i] = -1;
                spans[i] = new List<int>();
            }
            int[] hacc = new int[wo * 4];
            int[] acc = new int[wo * 4];

            void Ensure(int r)
            {
                int slot = r % cap;
                if (ringRow[slot] == r) return;
                ringRow[slot] = r;
                byte[] dst = ring[slot];
                List<int> sp = spans[slot];
                for (int i = 0; i < sp.Count; i += 2) Array.Clear(dst, sp[i] * 4, (sp[i + 1] - sp[i] + 1) * 4);
                sp.Clear();
                (int lo, int hi) = painter.Paint(r);
                if (lo > hi) return;
                int[] row = painter.Row;
                if (!needH)
                {
                    for (int x = lo; x <= hi; x++)
                    {
                        int p = row[x];
                        if (p == 0) continue;
                        dst[x * 4] = (byte)pal[p * 4];
                        dst[x * 4 + 1] = (byte)pal[p * 4 + 1];
                        dst[x * 4 + 2] = (byte)pal[p * 4 + 2];
                        dst[x * 4 + 3] = 255;
                        if (sp.Count > 0 && sp[^1] == x - 1) sp[^1] = x;
                        else
                        {
                            sp.Add(x);
                            sp.Add(x);
                        }
                    }
                    return;
                }
                int olo = int.MaxValue, ohi = -1;
                for (int x = lo; x <= hi; x++)
                {
                    int p = row[x];
                    if (p == 0) continue;
                    int rs = x;
                    while (x < hi && row[x + 1] == p) x++;
                    int a = firstOut[rs], b = lastOut[x];
                    if (a == int.MaxValue || b < 0)
                    {
                        // an end of the run feeds no output column (never with Lanczos/box at these ratios)
                        for (int q = rs; q <= x; q++)
                        {
                            a = Math.Min(a, firstOut[q]);
                            b = Math.Max(b, lastOut[q]);
                        }
                    }
                    if (a > b) continue;
                    if (olo == int.MaxValue)
                    {
                        olo = a;
                        ohi = a - 1;
                    }
                    if (b > ohi)
                    {
                        Array.Clear(hacc, (ohi + 1) * 4, (b - ohi) * 4);
                        ohi = b;
                    }
                    int pr = pal[p * 4], pg = pal[p * 4 + 1], pb = pal[p * 4 + 2];
                    for (int xx = a; xx <= b; xx++)
                    {
                        int xmin = bh[xx * 2], s0 = Math.Max(rs, xmin), s1 = Math.Min(x, xmin + bh[xx * 2 + 1] - 1);
                        if (s0 > s1) continue;
                        int kb = xx * kps - xmin;
                        int K = kph[kb + s1 + 1] - kph[kb + s0];
                        int i = xx * 4;
                        hacc[i] += pr * K;
                        hacc[i + 1] += pg * K;
                        hacc[i + 2] += pb * K;
                        hacc[i + 3] += 255 * K;
                    }
                }
                for (int xx = olo; xx <= ohi; xx++)
                {
                    int i = xx * 4;
                    byte c0 = Clip8(half + hacc[i]), c1 = Clip8(half + hacc[i + 1]), c2 = Clip8(half + hacc[i + 2]), c3 = Clip8(half + hacc[i + 3]);
                    if ((c0 | c1 | c2 | c3) == 0) continue;
                    dst[i] = c0;
                    dst[i + 1] = c1;
                    dst[i + 2] = c2;
                    dst[i + 3] = c3;
                    if (sp.Count > 0 && sp[^1] == xx - 1) sp[^1] = xx;
                    else
                    {
                        sp.Add(xx);
                        sp.Add(xx);
                    }
                }
            }

            for (int yy = 0; yy < ho; yy++)
            {
                int ob = yy * wo * 4;
                int lo = int.MaxValue, hi = -1;
                if (!needV)
                {
                    Ensure(yy);
                    int slot = yy % cap;
                    List<int> sp = spans[slot];
                    for (int i = 0; i < sp.Count; i += 2)
                    {
                        Buffer.BlockCopy(ring[slot], sp[i] * 4, output, ob + sp[i] * 4, (sp[i + 1] - sp[i] + 1) * 4);
                        lo = Math.Min(lo, sp[i]);
                        hi = Math.Max(hi, sp[i + 1]);
                    }
                }
                else
                {
                    int ymin = bv[yy * 2], n = bv[yy * 2 + 1];
                    for (int y = ymin; y < ymin + n; y++)
                    {
                        Ensure(y);
                        List<int> sp = spans[y % cap];
                        if (sp.Count == 0) continue;
                        lo = Math.Min(lo, sp[0]);
                        hi = Math.Max(hi, sp[^1]);
                    }
                    if (hi < lo) continue;
                    Array.Fill(acc, half, lo * 4, (hi - lo + 1) * 4);
                    for (int y = ymin; y < ymin + n; y++)
                    {
                        int slot = y % cap;
                        List<int> sp = spans[slot];
                        if (sp.Count == 0) continue;
                        int k = kv[yy * ksv + y - ymin];
                        byte[] src = ring[slot];
                        for (int s2 = 0; s2 < sp.Count; s2 += 2)
                        {
                            int e = (sp[s2 + 1] + 1) * 4;
                            for (int i = sp[s2] * 4; i < e; i++) acc[i] += src[i] * k;
                        }
                    }
                    for (int x = lo; x <= hi; x++)
                    {
                        int i = x * 4;
                        output[ob + i] = Clip8(acc[i]);
                        output[ob + i + 1] = Clip8(acc[i + 1]);
                        output[ob + i + 2] = Clip8(acc[i + 2]);
                        output[ob + i + 3] = Clip8(acc[i + 3]);
                    }
                }
                // RGBa -> RGBA as Pillow does
                for (int x = lo; x <= hi; x++)
                {
                    int i = ob + x * 4;
                    int a = output[i + 3];
                    if (a == 0 || a == 255) continue;
                    output[i] = (byte)Math.Min(255, 255 * output[i] / a);
                    output[i + 1] = (byte)Math.Min(255, 255 * output[i + 1] / a);
                    output[i + 2] = (byte)Math.Min(255, 255 * output[i + 2] / a);
                }
            }
            return new DsImage { Width = wo, Height = ho, Rgba = output };
        }

        // the whole supersampled canvas as palette indices (inspection/tests only: wc * hc ints)
        internal static int[] PaintCanvas(Drawing d, double S, out int wc, out int hc)
        {
            wc = (int)Np.Round(d.Width * S);
            hc = (int)Np.Round(d.Height * S);
            var shapes = new List<PShape>();
            foreach (Shape s in d.Shapes)
            {
                PShape? p = Prepare(s, S, hc);
                if (p != null) shapes.Add(p);
            }
            var painter = new Painter(shapes, wc, hc, d.Mirrored);
            var canvas = new int[wc * hc];
            for (int y = 0; y < hc; y++)
            {
                painter.Paint(y);
                Array.Copy(painter.Row, 0, canvas, y * wc, wc);
            }
            return canvas;
        }

        private static byte Clip8(int v)
        {
            int s = v >> PrecisionBits;
            return s < 0 ? (byte)0 : s > 255 ? (byte)255 : (byte)s;
        }

        // ------------------------------------------------------------------------------------- PIL polygon fill
        private static PShape? Prepare(Shape s, double S, int hc)
        {
            if (s.IsRect)
            {
                int x0 = (int)(s.Pts[0] * S), y0 = (int)(s.Pts[1] * S), x1 = (int)(s.Pts[2] * S), y1 = (int)(s.Pts[3] * S);
                if (y0 > y1) (y0, y1) = (y1, y0);
                return new PShape { IsRect = true, Colour = s.Colour, RX0 = x0, RY0 = y0, RX1 = x1, RY1 = y1, YTop = y0, YBottom = y1 };
            }
            int count = s.Pts.Length / 2;
            if (count <= 0) return null;
            var xy = new int[count * 2];
            for (int i = 0; i < count * 2; i++) xy[i] = (int)(s.Pts[i] * S);
            var edges = new List<PEdge>();
            var flat = new List<(int, int, int)>();
            int ymin = hc - 1, ymax = 0;
            void AddEdge(int x0, int y0, int x1, int y1)
            {
                int ey0 = Math.Min(y0, y1), ey1 = Math.Max(y0, y1);
                if (ymin > ey0) ymin = ey0;
                if (ymax < ey1) ymax = ey1;
                if (y0 == y1)
                {
                    flat.Add((y0, Math.Min(x0, x1), Math.Max(x0, x1)));
                    return;
                }
                edges.Add(new PEdge { X0 = x0, Y0 = y0, YMin = ey0, YMax = ey1, Dx = (float)(x1 - x0) / (y1 - y0) });
            }
            int last = count - 1;
            for (int i = 0; i < last; i++) AddEdge(xy[i * 2], xy[i * 2 + 1], xy[i * 2 + 2], xy[i * 2 + 3]);
            if (xy[last * 2] != xy[0] || xy[last * 2 + 1] != xy[1]) AddEdge(xy[last * 2], xy[last * 2 + 1], xy[0], xy[1]);
            int ly0 = Math.Max(ymin, 0), ly1 = Math.Min(ymax, hc);
            int top = int.MaxValue, bottom = int.MinValue;
            foreach (PEdge e in edges)
            {
                top = Math.Min(top, e.YMin);
                bottom = Math.Max(bottom, e.YMax);
            }
            foreach ((int y, int _, int _) in flat)
            {
                top = Math.Min(top, y);
                bottom = Math.Max(bottom, y);
            }
            return new PShape
            {
                Colour = s.Colour, Edges = edges.ToArray(), Flat = flat.ToArray(), LoopY0 = ly0, LoopY1 = ly1,
                YTop = top, YBottom = bottom
            };
        }

        private sealed class Painter
        {
            private readonly List<PShape> _shapes;
            private readonly int _wc, _hc;
            private readonly bool _mirror;
            public readonly int[] Row;
            private float[] _xx = new float[16];

            public Painter(List<PShape> shapes, int wc, int hc, bool mirror)
            {
                _shapes = shapes;
                _wc = wc;
                _hc = hc;
                _mirror = mirror;
                Row = new int[wc];
            }

            private int _lo, _hi;

            private void HLine(int x0, int x1, int colour)
            {
                if (x0 < 0) x0 = 0;
                else if (x0 >= _wc) return;
                if (x1 < 0) return;
                if (x1 >= _wc) x1 = _wc - 1;
                if (x0 > x1) return;
                for (int x = x0; x <= x1; x++) Row[x] = colour;
                if (x0 < _lo) _lo = x0;
                if (x1 > _hi) _hi = x1;
            }

            private static float RoundF(float f) => MathF.Round(f, MidpointRounding.AwayFromZero); // C roundf

            private static int RoundUp(float f) => f >= 0.0f ? (int)Math.Floor(f + 0.5f) : -(int)Math.Floor(Math.Abs(f) + 0.5f);
            private static int RoundDown(float f) => f >= 0.0f ? (int)Math.Ceiling(f - 0.5f) : -(int)Math.Ceiling(Math.Abs(f) - 0.5f);

            private int _dirtyLo = 0, _dirtyHi = -1;

            // paints canvas row y into Row (everything else in Row is transparent); returns the column range that may
            // be non-transparent
            public (int Lo, int Hi) Paint(int y)
            {
                if (_dirtyHi >= _dirtyLo) Array.Clear(Row, _dirtyLo, _dirtyHi - _dirtyLo + 1);
                _lo = int.MaxValue;
                _hi = -1;
                _dirtyLo = 0;
                _dirtyHi = -1;
                if ((uint)y >= (uint)_hc) return (1, 0);
                foreach (PShape s in _shapes)
                {
                    if (y < s.YTop || y > s.YBottom) continue;
                    if (s.IsRect)
                    {
                        int ry0 = s.RY0, ry1 = s.RY1;
                        if (ry0 < 0) ry0 = 0;
                        else if (ry0 >= _hc) continue;
                        if (ry1 < 0) continue;
                        if (ry1 > _hc) ry1 = _hc;
                        if (y >= ry0 && y <= ry1) HLine(s.RX0, s.RX1, s.Colour);
                        continue;
                    }
                    foreach ((int fy, int fx0, int fx1) in s.Flat)
                    {
                        if (fy == y) HLine(fx0, fx1, s.Colour);
                    }
                    if (y < s.LoopY0 || y > s.LoopY1) continue;
                    int j = 0;
                    if (_xx.Length < s.Edges.Length * 2) _xx = new float[s.Edges.Length * 2];
                    float[] xx = _xx;
                    PEdge[] edges = s.Edges;
                    for (int i = 0; i < edges.Length; i++)
                    {
                        PEdge e = edges[i];
                        if (y < e.YMin || y > e.YMax) continue;
                        xx[j++] = (float)(y - e.Y0) * e.Dx + e.X0;
                        if (y == e.YMax && y < s.LoopY1)
                        {
                            // needed to draw consistent polygons
                            xx[j] = xx[j - 1];
                            j++;
                        }
                        else if ((y == e.YMin || y == e.YMax) && e.Dx != 0)
                        {
                            // connect discontiguous corners: where two sloped edges meet on this row, the span
                            // reaches over to the neighbouring row's crossings
                            for (int k = 0; k < i; k++)
                            {
                                PEdge o = edges[k];
                                if ((y != o.YMin && y != o.YMax) || o.Dx == 0) continue;
                                if (RoundF(xx[j - 1]) != RoundF((float)(y - o.Y0) * o.Dx + o.X0)) continue;
                                int offset = y == e.YMax ? -1 : 1;
                                float adj = (float)(y + offset - e.Y0) * e.Dx + e.X0;
                                if (y + offset >= o.YMin && y + offset <= o.YMax)
                                {
                                    float adjO = (float)(y + offset - o.Y0) * o.Dx + o.X0;
                                    if (xx[j - 1] > adj + 1 && xx[j - 1] > adjO + 1) xx[j - 1] = RoundF(Math.Max(adj, adjO)) + 1;
                                    else if (xx[j - 1] < adj - 1 && xx[j - 1] < adjO - 1) xx[j - 1] = RoundF(Math.Min(adj, adjO)) - 1;
                                }
                                break;
                            }
                        }
                    }
                    if (j > 1) Array.Sort(xx, 0, j);
                    for (int i = 1; i < j; i += 2) HLine(RoundUp(xx[i - 1]), RoundDown(xx[i]), s.Colour);
                }
                if (_lo > _hi) return (1, 0);
                if (_mirror)
                {
                    // the left half's mirror image replaces the right part [wc - wm, wc): only where the source may be
                    // painted or the target was painted (everywhere else both are transparent)
                    int wm = _wc / 2, off = _wc - wm;
                    int iLo = int.MaxValue, iHi = -1;
                    if (_lo <= wm - 1)
                    {
                        iLo = wm - 1 - Math.Min(_hi, wm - 1);
                        iHi = wm - 1 - _lo;
                    }
                    if (_hi >= off)
                    {
                        iLo = Math.Min(iLo, Math.Max(_lo, off) - off);
                        iHi = Math.Max(iHi, _hi - off);
                    }
                    for (int i = iLo; i <= iHi; i++) Row[off + i] = Row[wm - 1 - i];
                    if (iHi >= 0) _hi = Math.Max(_hi, off + iHi);
                }
                _dirtyLo = _lo;
                _dirtyHi = _hi;
                return (_lo, _hi);
            }
        }

        // ------------------------------------------------------------------------------------ Pillow resample
        private enum Filter { Box, Lanczos }

        private static double FilterAt(Filter f, double x)
        {
            if (f == Filter.Box) return x > -0.5 && x <= 0.5 ? 1.0 : 0.0;
            if (-3.0 <= x && x < 3.0) return Sinc(x) * Sinc(x / 3);
            return 0.0;
        }

        private static double Sinc(double x)
        {
            if (x == 0.0) return 1.0;
            x *= Math.PI;
            return Math.Sin(x) / x;
        }

        // precompute_coeffs + normalize_coeffs_8bpc: per output index (first input, count) and fixed-point taps
        private static (int[] Bounds, int[] K, int KSize) Coeffs(int inSize, int outSize, Filter f)
        {
            double support0 = f == Filter.Box ? 0.5 : 3.0;
            double scale = (double)inSize / outSize, filterscale = scale < 1.0 ? 1.0 : scale;
            double support = support0 * filterscale;
            int ksize = (int)Math.Ceiling(support) * 2 + 1;
            var bounds = new int[outSize * 2];
            var kk = new int[outSize * ksize];
            var pre = new double[ksize];
            for (int xx = 0; xx < outSize; xx++)
            {
                double center = (xx + 0.5) * scale;
                double ww = 0.0, ss = 1.0 / filterscale;
                int xmin = (int)(center - support + 0.5);
                if (xmin < 0) xmin = 0;
                int xmax = (int)(center + support + 0.5);
                if (xmax > inSize) xmax = inSize;
                xmax -= xmin;
                for (int x = 0; x < xmax; x++)
                {
                    double w = FilterAt(f, (x + xmin - center + 0.5) * ss);
                    pre[x] = w;
                    ww += w;
                }
                for (int x = 0; x < xmax; x++)
                {
                    if (ww != 0.0) pre[x] /= ww;
                    double v = pre[x] * (1 << PrecisionBits);
                    kk[xx * ksize + x] = pre[x] < 0 ? (int)(-0.5 + v) : (int)(0.5 + v);
                }
                bounds[xx * 2] = xmin;
                bounds[xx * 2 + 1] = xmax;
            }
            return (bounds, kk, ksize);
        }
    }
}
