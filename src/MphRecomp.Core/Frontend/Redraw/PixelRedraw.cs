using System;
using System.Collections.Generic;

// DS pixel-art line art (RGBA, transparent background) redrawn as clean vector geometry and rasterised at any scale.
// A C# port of the approved tracer (trace_v11.py); the rules, in order:
//  1. mirror symmetry: if the ink is (almost) mirror-symmetric about the centre column, the left half is the master;
//     the finished drawing's left half is flipped onto the right. A stroke ON the axis (the mirror doubled it to 2 px)
//     keeps its own 1-px width and sits exactly on the axis.
//  2. all shades of one ink are one shape (alpha mask); components are 8-connected; colour = the majority shade.
//  3. line art vs area by thickness along a Zhang-Suen skeleton (used ONLY for this decision, > 2.4 px = area):
//     areas -> tidy polygons (DP corners, least-squares edge lines, corners = intersections, jogs < 2.5 px dropped).
//  4. line art -> digital straight segments fitted directly to the ink (SegmentFinder: greedy, widths 1/2,
//     0/45/90 snapping, end-pixel anchoring).
//  5. joins from geometry (Strokes): corners (mitred, same stroke), T joins (butt end on the other's centre line) and
//     free ends (square cap flush with the last pixel edge).
//  6. strokes = chains of corner joins, each ONE polygon; everything is filled on a supersampled canvas with PIL's
//     polygon rules and Lanczos-downsampled (Rasteriser), as the Python did with PIL.
// Deterministic and thread-safe: no mutable statics, every call owns its buffers.
namespace MphRecomp.Frontend.Redraw
{
    public enum RedrawFilter
    {
        Lanczos, // PIL's LANCZOS (what the approved Python used)
        Box      // plain area average: a little softer, a little cheaper
    }

    public static class PixelRedraw
    {
        // The Python's tunables, same defaults.
        public sealed class Options
        {
            public int SuperSample { get; set; } = 4;               // SS: canvas = scale * SS, then downsampled
            public RedrawFilter Filter { get; set; } = RedrawFilter.Lanczos;
            public bool DetectMirror { get; set; } = true;
            public double MirrorMatch { get; set; } = 0.97;          // fraction of pixels that must match their mirror
            public double AreaThickness { get; set; } = 2.4;         // ink px per skeleton px above which a shape is an area
            public double PolygonTolerance { get; set; } = 1.2;      // tidy_polygon DP tolerance
            public double PolygonMinEdge { get; set; } = 2.5;        // tidy_polygon: shorter edges (jogs) are dropped
            public double SnapDegrees { get; set; } = 8.0;           // SNAP_DEG
            public int Overlap { get; set; } = 3;                    // OVERLAP: columns a run may cross explained ink
            public int MinNewPixels { get; set; } = 2;               // a segment must explain this many new pixels
            public int MaxSegmentsPerComponent { get; set; } = 200;
            public double AnchorMaxDeviation { get; set; } = 0.75;   // anchor_free_segments max_dev
            public int AnchorMinColumns { get; set; } = 6;           // anchor_free_segments min_cols
            public double ParallelDegrees { get; set; } = 10.0;      // build_strokes: segments closer than this never join
            internal bool BruteForceSearch { get; set; }             // tests: score every coarse row (the Python way)
        }

        // Redraws src at (Width * scale, Height * scale): straight-alpha RGBA, transparent background, colours = the
        // source inks.
        public static DsImage Redraw(DsImage src, float scale, Options? options = null)
        {
            ArgumentNullException.ThrowIfNull(src);
            if (!(scale > 0) || float.IsInfinity(scale)) throw new ArgumentOutOfRangeException(nameof(scale));
            Options o = options ?? new Options();
            if (o.SuperSample < 1) throw new ArgumentOutOfRangeException(nameof(options), "SuperSample must be >= 1");
            Drawing drawing = Analyse(src, o);
            return Rasteriser.Render(drawing, scale, o);
        }

        // ------------------------------------------------------------------------------------------------ analysis
        internal static Drawing Analyse(DsImage src, Options o)
        {
            int w = src.Width, h = src.Height;
            if (src.Rgba.Length < w * h * 4) throw new ArgumentException("Rgba is smaller than Width * Height * 4", nameof(src));
            byte[] rgba = src.Rgba;
            bool mirrored = false;
            if (o.DetectMirror && w > 0 && h > 0)
            {
                long same = 0;
                for (int y = 0; y < h; y++)
                {
                    int row = y * w;
                    for (int x = 0; x < w; x++)
                    {
                        if ((rgba[(row + x) * 4 + 3] > 0) == (rgba[(row + w - 1 - x) * 4 + 3] > 0)) same++;
                    }
                }
                if (!((double)same / ((double)w * h) < o.MirrorMatch))
                {
                    // the left half is the master: the right half becomes its mirror image
                    mirrored = true;
                    rgba = (byte[])rgba.Clone();
                    for (int y = 0; y < h; y++)
                    {
                        for (int x = w - w / 2; x < w; x++)
                        {
                            Buffer.BlockCopy(rgba, (y * w + w - 1 - x) * 4, rgba, (y * w + x) * 4, 4);
                        }
                    }
                }
            }
            var drawing = new Drawing { Width = w, Height = h, Mirrored = mirrored };
            int[] lab = Label(rgba, w, h, out int count);
            // pixel lists per component, in raster order
            var pixels = new List<int>[count + 1];
            for (int i = 1; i <= count; i++) pixels[i] = new List<int>();
            for (int i = 0; i < w * h; i++)
            {
                if (lab[i] != 0) pixels[lab[i]].Add(i);
            }
            var polys = new List<Shape>();
            var strokes = new List<Shape>();
            var scratch = new SegmentFinder.Scratch();
            for (int k = 1; k <= count; k++)
            {
                List<int> px = pixels[k];
                var comp = new Component(lab, k, w, h, px);
                int colour = drawing.ColourIndex(Majority(rgba, px));
                int skeleton = Thinning.Count(comp);
                if ((double)px.Count / Math.Max(skeleton, 1) > o.AreaThickness)
                {
                    List<List<(int X, int Y)>> loops = Loops(comp);
                    // longest first (stable): the outline is filled, the rest are holes
                    var order = new List<int>();
                    for (int i = 0; i < loops.Count; i++) order.Add(i);
                    order.Sort((a, b) => loops[a].Count != loops[b].Count ? loops[b].Count.CompareTo(loops[a].Count) : a.CompareTo(b));
                    for (int i = 0; i < order.Count; i++)
                    {
                        List<Vec2> poly = Geom.TidyPolygon(loops[order[i]], o.PolygonTolerance, o.PolygonMinEdge);
                        if (poly.Count >= 3) polys.Add(Shape.Polygon(poly, i == 0 ? colour : 0));
                    }
                    continue;
                }
                var finder = new SegmentFinder(o, comp, scratch);
                List<Seg> segs = finder.Find(out List<(int Y, int X)> left);
                if (mirrored)
                {
                    foreach (Seg s in segs)
                    {
                        // a stroke ON the mirror axis (the mirror doubled it) keeps its own width and sits on the axis
                        if (Math.Abs(s.D.X) < 1e-6 && Math.Abs(s.C.X - w / 2.0) < 0.3 && s.W == 2)
                        {
                            s.W = 1;
                            s.C = new Vec2(w / 2.0, s.C.Y);
                        }
                    }
                }
                foreach (Stroke st in Strokes.Build(segs, o))
                {
                    Shape? shape = Strokes.ToShape(st, colour);
                    if (shape != null) strokes.Add(shape);
                }
                foreach ((int y, int x) in left)
                {
                    polys.Add(Shape.Polygon(new List<Vec2> { new(x, y), new(x + 1, y), new(x + 1, y + 1), new(x, y + 1) }, colour));
                }
                drawing.Segments.AddRange(segs);
            }
            // PIL order: every polygon item first, then every stroke
            drawing.Shapes.AddRange(polys);
            drawing.Shapes.AddRange(strokes);
            return drawing;
        }

        private static int[] Label(byte[] rgba, int w, int h, out int count)
        {
            var lab = new int[w * h];
            var stack = new Stack<int>();
            count = 0;
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    int i = y * w + x;
                    if (rgba[i * 4 + 3] == 0 || lab[i] != 0) continue;
                    count++;
                    lab[i] = count;
                    stack.Push(i);
                    while (stack.Count > 0)
                    {
                        int c = stack.Pop();
                        int cy = c / w, cx = c % w;
                        for (int dy = -1; dy <= 1; dy++)
                        {
                            for (int dx = -1; dx <= 1; dx++)
                            {
                                int ny = cy + dy, nx = cx + dx;
                                if (ny < 0 || ny >= h || nx < 0 || nx >= w) continue;
                                int n = ny * w + nx;
                                if (rgba[n * 4 + 3] != 0 && lab[n] == 0)
                                {
                                    lab[n] = count;
                                    stack.Push(n);
                                }
                            }
                        }
                    }
                }
            }
            return lab;
        }

        // the majority shade; ties go to the smallest (r, g, b), as np.unique sorts them
        private static uint Majority(byte[] rgba, List<int> px)
        {
            var counts = new Dictionary<int, int>();
            foreach (int i in px)
            {
                int c = rgba[i * 4] << 16 | rgba[i * 4 + 1] << 8 | rgba[i * 4 + 2];
                counts[c] = counts.TryGetValue(c, out int n) ? n + 1 : 1;
            }
            int best = -1, bestN = -1;
            foreach (KeyValuePair<int, int> kv in counts)
            {
                if (kv.Value > bestN || (kv.Value == bestN && kv.Key < best))
                {
                    best = kv.Key;
                    bestN = kv.Value;
                }
            }
            return (uint)best;
        }

        // boundary loops of a component (pixel-corner vertices), traced exactly as the Python's dict did: a vertex
        // where two boundary edges start keeps the LAST one written (at its first insertion position), and loops are
        // popped from the most recently inserted edge.
        private static List<List<(int X, int Y)>> Loops(Component c)
        {
            var map = new OrderedEdgeMap();
            for (int y = c.Y0; y <= c.Y1; y++)
            {
                for (int x = c.X0; x <= c.X1; x++)
                {
                    if (!c.At(y, x)) continue;
                    if (!c.At(y - 1, x)) map.Set(x, y, x + 1, y);
                    if (!c.At(y, x + 1)) map.Set(x + 1, y, x + 1, y + 1);
                    if (!c.At(y + 1, x)) map.Set(x + 1, y + 1, x, y + 1);
                    if (!c.At(y, x - 1)) map.Set(x, y + 1, x, y);
                }
            }
            var res = new List<List<(int X, int Y)>>();
            while (map.Count > 0)
            {
                (long start, long next) = map.PopLast();
                var loop = new List<(int X, int Y)> { OrderedEdgeMap.Unpack(start) };
                long cur = next;
                while (cur != start && map.Contains(cur))
                {
                    loop.Add(OrderedEdgeMap.Unpack(cur));
                    cur = map.Pop(cur);
                }
                res.Add(loop);
            }
            return res;
        }

        private sealed class OrderedEdgeMap
        {
            private readonly Dictionary<long, int> _index = new();
            private readonly List<(long Key, long Value)> _entries = new();
            private readonly List<bool> _alive = new();
            private int _last = -1;
            public int Count { get; private set; }

            public static long Pack(int x, int y) => (long)x << 32 | (uint)y;
            public static (int X, int Y) Unpack(long k) => ((int)(k >> 32), (int)(k & 0xFFFFFFFF));

            public void Set(int x0, int y0, int x1, int y1)
            {
                long k = Pack(x0, y0), v = Pack(x1, y1);
                if (_index.TryGetValue(k, out int i))
                {
                    _entries[i] = (k, v);
                    return;
                }
                _index[k] = _entries.Count;
                _entries.Add((k, v));
                _alive.Add(true);
                _last = _entries.Count - 1;
                Count++;
            }

            public bool Contains(long k) => _index.ContainsKey(k);

            public (long, long) PopLast()
            {
                while (!_alive[_last]) _last--;
                (long k, long v) = _entries[_last];
                _alive[_last] = false;
                _index.Remove(k);
                Count--;
                return (k, v);
            }

            public long Pop(long k)
            {
                int i = _index[k];
                _index.Remove(k);
                _alive[i] = false;
                Count--;
                return _entries[i].Value;
            }
        }
    }

    // one 8-connected component: the label grid plus its bounding box
    internal sealed class Component
    {
        public readonly int[] Labels;
        public readonly int Id, ImageW, ImageH;
        public readonly List<int> Pixels;
        public readonly int X0, Y0, X1, Y1; // inclusive bbox

        public Component(int[] labels, int id, int w, int h, List<int> pixels)
        {
            Labels = labels;
            Id = id;
            ImageW = w;
            ImageH = h;
            Pixels = pixels;
            int x0 = int.MaxValue, y0 = int.MaxValue, x1 = -1, y1 = -1;
            foreach (int i in pixels)
            {
                int y = i / w, x = i % w;
                if (x < x0) x0 = x;
                if (x > x1) x1 = x;
                if (y < y0) y0 = y;
                if (y > y1) y1 = y;
            }
            X0 = x0; Y0 = y0; X1 = x1; Y1 = y1;
        }

        public bool At(int y, int x) => (uint)y < (uint)ImageH && (uint)x < (uint)ImageW && Labels[y * ImageW + x] == Id;
    }

    internal static class Thinning
    {
        // Zhang-Suen skeleton pixel count (both sub-steps remove simultaneously, repeat until nothing changes)
        public static int Count(Component c)
        {
            int bw = c.X1 - c.X0 + 3, bh = c.Y1 - c.Y0 + 3;
            var m = new byte[bw * bh];
            foreach (int i in c.Pixels)
            {
                int y = i / c.ImageW - c.Y0 + 1, x = i % c.ImageW - c.X0 + 1;
                m[y * bw + x] = 1;
            }
            var rem = new List<int>();
            bool changed = true;
            while (changed)
            {
                changed = false;
                for (int step = 0; step < 2; step++)
                {
                    rem.Clear();
                    for (int y = 1; y < bh - 1; y++)
                    {
                        for (int x = 1; x < bw - 1; x++)
                        {
                            int i = y * bw + x;
                            if (m[i] != 1) continue;
                            int p2 = m[i - bw], p3 = m[i - bw + 1], p4 = m[i + 1], p5 = m[i + bw + 1];
                            int p6 = m[i + bw], p7 = m[i + bw - 1], p8 = m[i - 1], p9 = m[i - bw - 1];
                            int b = p2 + p3 + p4 + p5 + p6 + p7 + p8 + p9;
                            if (b < 2 || b > 6) continue;
                            int a = (p2 == 0 && p3 == 1 ? 1 : 0) + (p3 == 0 && p4 == 1 ? 1 : 0) + (p4 == 0 && p5 == 1 ? 1 : 0)
                                + (p5 == 0 && p6 == 1 ? 1 : 0) + (p6 == 0 && p7 == 1 ? 1 : 0) + (p7 == 0 && p8 == 1 ? 1 : 0)
                                + (p8 == 0 && p9 == 1 ? 1 : 0) + (p9 == 0 && p2 == 1 ? 1 : 0);
                            if (a != 1) continue;
                            bool c1 = step == 0 ? p2 * p4 * p6 == 0 : p2 * p4 * p8 == 0;
                            bool c2 = step == 0 ? p4 * p6 * p8 == 0 : p2 * p6 * p8 == 0;
                            if (c1 && c2) rem.Add(i);
                        }
                    }
                    if (rem.Count > 0)
                    {
                        foreach (int i in rem) m[i] = 0;
                        changed = true;
                    }
                }
            }
            int n = 0;
            foreach (byte v in m) n += v;
            return n;
        }
    }

    // ------------------------------------------------------------------------------------------- drawing (output)
    internal sealed class Shape
    {
        public double[] Pts = Array.Empty<double>(); // x0, y0, x1, y1, ... in source pixels (polygon), or a rect's corners
        public bool IsRect;                          // a zero-length stroke: PIL rectangle (x0, y0, x1, y1)
        public int Colour;                           // palette index; 0 = transparent (cuts a hole)

        public static Shape Polygon(List<Vec2> pts, int colour)
        {
            var a = new double[pts.Count * 2];
            for (int i = 0; i < pts.Count; i++)
            {
                a[i * 2] = pts[i].X;
                a[i * 2 + 1] = pts[i].Y;
            }
            return new Shape { Pts = a, Colour = colour };
        }
    }

    internal sealed class Drawing
    {
        public int Width, Height;
        public bool Mirrored;
        public readonly List<uint> Palette = new() { 0 }; // 0xRRGGBB of each ink; index 0 = transparent
        public readonly List<Shape> Shapes = new();      // paint order (each replaces what is under it)
        public readonly List<Seg> Segments = new();      // for inspection

        public int ColourIndex(uint rgb)
        {
            int i = Palette.IndexOf(rgb, 1);
            if (i >= 1) return i;
            Palette.Add(rgb);
            return Palette.Count - 1;
        }
    }
}
