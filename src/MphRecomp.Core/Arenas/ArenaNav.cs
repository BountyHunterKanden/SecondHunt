using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using OpenTK.Mathematics;

namespace MphRecomp.Arenas
{
    // Ray queries against an arena's collision triangles (MPH units, Y up), bucketed on an XZ grid.
    public sealed class ArenaRaycaster
    {
        const float Cell = 4;
        readonly Vector3[] _a, _b, _c, _n;
        readonly Dictionary<(int, int), List<int>> _grid = new();

        public int TriangleCount => _a.Length;
        public Vector3 Min { get; }
        public Vector3 Max { get; }

        public ArenaRaycaster(IReadOnlyList<(Vector3 A, Vector3 B, Vector3 C)> tris)
        {
            int n = tris.Count;
            _a = new Vector3[n]; _b = new Vector3[n]; _c = new Vector3[n]; _n = new Vector3[n];
            var min = new Vector3(float.MaxValue); var max = new Vector3(float.MinValue);
            for (int i = 0; i < n; i++)
            {
                (Vector3 a, Vector3 b, Vector3 c) = tris[i];
                _a[i] = a; _b[i] = b; _c[i] = c;
                _n[i] = Vector3.Cross(b - a, c - a).Normalized();
                Vector3 lo = Vector3.ComponentMin(a, Vector3.ComponentMin(b, c)), hi = Vector3.ComponentMax(a, Vector3.ComponentMax(b, c));
                min = Vector3.ComponentMin(min, lo); max = Vector3.ComponentMax(max, hi);
                for (int x = Key(lo.X); x <= Key(hi.X); x++)
                {
                    for (int z = Key(lo.Z); z <= Key(hi.Z); z++)
                    {
                        if (!_grid.TryGetValue((x, z), out List<int>? list)) _grid[(x, z)] = list = new List<int>();
                        list.Add(i);
                    }
                }
            }
            Min = min; Max = max;
        }

        static int Key(float v) => (int)MathF.Floor(v / Cell);

        // first hit along from -> to: (t in 0..1, triangle normal), or null
        public (float T, Vector3 Normal)? Cast(Vector3 from, Vector3 to)
        {
            Vector3 d = to - from;
            Vector3 lo = Vector3.ComponentMin(from, to), hi = Vector3.ComponentMax(from, to);
            float best = float.MaxValue; Vector3 bestN = default;
            HashSet<int>? seen = null;
            int x0 = Key(lo.X), x1 = Key(hi.X), z0 = Key(lo.Z), z1 = Key(hi.Z);
            bool many = (x1 - x0 + 1) * (z1 - z0 + 1) > 1;
            if (many) seen = new HashSet<int>();
            for (int x = x0; x <= x1; x++)
            {
                for (int z = z0; z <= z1; z++)
                {
                    if (!_grid.TryGetValue((x, z), out List<int>? list)) continue;
                    foreach (int i in list)
                    {
                        if (many && !seen!.Add(i)) continue;
                        if (Intersect(from, d, i, out float t) && t < best) { best = t; bestN = _n[i]; }
                    }
                }
            }
            return best <= 1 ? (best, bestN) : null;
        }

        // Moller-Trumbore, both faces
        bool Intersect(Vector3 o, Vector3 d, int i, out float t)
        {
            t = 0;
            Vector3 e1 = _b[i] - _a[i], e2 = _c[i] - _a[i];
            Vector3 p = Vector3.Cross(d, e2);
            float det = Vector3.Dot(e1, p);
            if (MathF.Abs(det) < 1e-9f) return false;
            float inv = 1 / det;
            Vector3 s = o - _a[i];
            float u = Vector3.Dot(s, p) * inv;
            if (u < 0 || u > 1) return false;
            Vector3 q = Vector3.Cross(s, e1);
            float v = Vector3.Dot(d, q) * inv;
            if (v < 0 || u + v > 1) return false;
            t = Vector3.Dot(e2, q) * inv;
            return t >= 0;
        }

        // the floor under p within maxDrop (walkable: normal up), or null
        public Vector3? FloorBelow(Vector3 p, float maxDrop, float minUp = 0.7f)
        {
            (float T, Vector3 Normal)? hit = Cast(p, p - new Vector3(0, maxDrop, 0));
            if (hit is not { } h || h.Normal.Y < minUp) return null;
            return p - new Vector3(0, maxDrop * h.T, 0);
        }
    }

    // A bot navigation graph for an arena, generated from its collision, written as MphRead NodeData version 6:
    // one set of Navigation nodes (id = index) and, per node, a run-length next-hop table over destination ids
    // ((run length, next node) pairs, as PlayerAi.Func213A1A8 reads it). The neighbour list (Offset2) is left empty.
    public static class ArenaNav
    {
        public const float Spacing = 4.5f;     // MPH graphs: edges 3-16 units, median 4-6
        const float Height = 1.6f;             // a hunter, -0.5..1.1 around its position
        const float StepUp = 0.6f, MaxDrop = 4f, Sample = 0.5f;

        public sealed class Graph
        {
            public List<Vector3> Nodes = new();
            public List<List<(int To, float Cost)>> Edges = new();
            public int[,] Next = new int[0, 0];
        }

        // jumps: one-way flights (jump pads: pad -> landing); the pad's node leaves only by its flight
        public static Graph Build(ArenaRaycaster ray, IReadOnlyList<Vector3> anchors, float killHeight,
            IReadOnlyList<(Vector3 From, Vector3 To)>? jumps = null)
        {
            var g = new Graph();
            // 1. floor samples on an XZ grid, every walkable level of each column, with headroom
            for (float x = ray.Min.X + Spacing / 2; x < ray.Max.X; x += Spacing)
            {
                for (float z = ray.Min.Z + Spacing / 2; z < ray.Max.Z; z += Spacing)
                {
                    float top = ray.Max.Y + 1;
                    while (top > ray.Min.Y)
                    {
                        var from = new Vector3(x, top, z);
                        (float T, Vector3 Normal)? hit = ray.Cast(from, new Vector3(x, ray.Min.Y - 1, z));
                        if (hit is not { } h) break;
                        float y = top - (top - (ray.Min.Y - 1)) * h.T;
                        var floor = new Vector3(x, y, z);
                        if (h.Normal.Y >= 0.7f && y > killHeight + 0.5f && ray.Cast(floor + Vector3.UnitY * 0.05f, floor + Vector3.UnitY * (Height + 0.3f)) == null)
                        {
                            g.Nodes.Add(floor);
                        }
                        top = y - 0.05f;
                    }
                }
            }
            var pads = new List<(int From, int To)>();
            foreach ((Vector3 from, Vector3 to) in jumps ?? Array.Empty<(Vector3, Vector3)>())
            {
                g.Nodes.Add(from);
                g.Nodes.Add(to);
                pads.Add((g.Nodes.Count - 2, g.Nodes.Count - 1));
            }
            // 2. walk links to nearby nodes (one-way where it's a drop)
            int n = g.Nodes.Count;
            for (int i = 0; i < n; i++) g.Edges.Add(new List<(int, float)>());
            float reach = Spacing * 1.5f;
            for (int i = 0; i < n; i++)
            {
                for (int j = 0; j < n; j++)
                {
                    if (i == j) continue;
                    Vector3 a = g.Nodes[i], b = g.Nodes[j];
                    if ((a.Xz - b.Xz).LengthSquared > reach * reach || b.Y - a.Y > reach || a.Y - b.Y > MaxDrop) continue;
                    if (Walkable(ray, a, b)) g.Edges[i].Add((j, (b - a).Length));
                }
            }
            foreach ((int from, int to) in pads)
            {
                g.Edges[from].Clear();   // stepping on a pad launches you
                g.Edges[from].Add((to, (g.Nodes[to] - g.Nodes[from]).Length * 0.5f));
            }
            // 3. keep what the spawns' component can reach and come back from
            var keep = StrongComponentOf(g, anchors.Select(p => Nearest(g.Nodes, p)).Where(k => k >= 0).Distinct().ToList());
            Compact(g, keep);
            // 4. next hops: shortest paths to each destination over the reversed graph
            n = g.Nodes.Count;
            g.Next = new int[n, n];
            var rev = new List<List<(int, float)>>();
            for (int i = 0; i < n; i++) rev.Add(new List<(int, float)>());
            for (int i = 0; i < n; i++) foreach ((int j, float c) in g.Edges[i]) rev[j].Add((i, c));
            var dist = new float[n];
            for (int dst = 0; dst < n; dst++)
            {
                Array.Fill(dist, float.MaxValue);
                dist[dst] = 0;
                var pq = new PriorityQueue<int, float>();
                pq.Enqueue(dst, 0);
                while (pq.TryDequeue(out int u, out float du))
                {
                    if (du > dist[u]) continue;
                    foreach ((int v, float c) in rev[u])
                    {
                        if (du + c < dist[v]) { dist[v] = du + c; pq.Enqueue(v, dist[v]); }
                    }
                }
                for (int src = 0; src < n; src++)
                {
                    int hop = src;
                    if (src != dst && dist[src] < float.MaxValue)
                    {
                        float best = float.MaxValue;
                        foreach ((int v, float c) in g.Edges[src])
                        {
                            if (dist[v] < float.MaxValue && c + dist[v] < best) { best = c + dist[v]; hop = v; }
                        }
                    }
                    g.Next[src, dst] = hop;
                }
            }
            return g;
        }

        // feet stay on walkable floor along the way, rising at most a step per sample, with room for the body
        static bool Walkable(ArenaRaycaster ray, Vector3 a, Vector3 b)
        {
            int steps = Math.Max(1, (int)MathF.Ceiling((b.Xz - a.Xz).Length / Sample));
            Vector3 prev = a;
            for (int s = 1; s <= steps; s++)
            {
                Vector3 p = Vector3.Lerp(a, b, s / (float)steps);
                Vector3? floor = ray.FloorBelow(new Vector3(p.X, prev.Y + StepUp + 0.05f, p.Z), StepUp + MaxDrop + 0.1f);
                if (floor is not { } f || f.Y - prev.Y > StepUp) return false;
                Vector3 chest = Vector3.UnitY * 0.8f;
                if (ray.Cast(prev + chest, f + chest) != null) return false;
                prev = f;
            }
            return MathF.Abs(prev.Y - b.Y) < 0.3f;
        }

        static int Nearest(List<Vector3> nodes, Vector3 p)
        {
            int best = -1; float bd = float.MaxValue;
            for (int i = 0; i < nodes.Count; i++)
            {
                float d = (nodes[i] - p).LengthSquared;
                if (d < bd) { bd = d; best = i; }
            }
            return best;
        }

        // nodes reachable from the anchors AND able to reach an anchor
        static bool[] StrongComponentOf(Graph g, List<int> anchors)
        {
            int n = g.Nodes.Count;
            bool[] fwd = Flood(n, anchors, i => g.Edges[i].Select(e => e.To));
            var rev = new List<int>[n];
            for (int i = 0; i < n; i++) rev[i] = new List<int>();
            for (int i = 0; i < n; i++) foreach ((int j, _) in g.Edges[i]) rev[j].Add(i);
            bool[] back = Flood(n, anchors, i => rev[i]);
            var keep = new bool[n];
            for (int i = 0; i < n; i++) keep[i] = fwd[i] && back[i];
            return keep;
        }

        static bool[] Flood(int n, List<int> start, Func<int, IEnumerable<int>> next)
        {
            var seen = new bool[n];
            var stack = new Stack<int>(start);
            foreach (int s in start) seen[s] = true;
            while (stack.Count > 0)
            {
                foreach (int v in next(stack.Pop()))
                {
                    if (!seen[v]) { seen[v] = true; stack.Push(v); }
                }
            }
            return seen;
        }

        static void Compact(Graph g, bool[] keep)
        {
            var map = new int[g.Nodes.Count];
            var nodes = new List<Vector3>();
            for (int i = 0; i < g.Nodes.Count; i++)
            {
                map[i] = keep[i] ? nodes.Count : -1;
                if (keep[i]) nodes.Add(g.Nodes[i]);
            }
            var edges = new List<List<(int, float)>>();
            for (int i = 0; i < g.Nodes.Count; i++)
            {
                if (!keep[i]) continue;
                edges.Add(g.Edges[i].Where(e => keep[e.To]).Select(e => (map[e.To], e.Cost)).ToList());
            }
            g.Nodes = nodes;
            g.Edges = edges;
        }

        public static byte[] Write(Graph g, float maxDistance = 1.2f)
        {
            int n = g.Nodes.Count;
            using var ms = new MemoryStream();
            using var w = new BinaryWriter(ms);
            const int header = 14, indexOffset = 14, dataOffset = 16, set2 = 24, nodes = 32;
            w.Write((ushort)6); w.Write((ushort)1); w.Write((uint)indexOffset); w.Write((uint)dataOffset); w.Write((ushort)0);
            w.Write((ushort)0);                                    // the (empty) set index list, 2 bytes before the data
            w.Write((uint)set2); w.Write((ushort)1); w.Write((ushort)0x5C);
            w.Write((uint)nodes); w.Write((ushort)n); w.Write((ushort)0x5C);
            System.Diagnostics.Debug.Assert(ms.Position == nodes && header == indexOffset);
            // the route runs, after the node table
            var runs = new List<List<(ushort Run, ushort Hop)>>();
            for (int src = 0; src < n; src++)
            {
                var list = new List<(ushort, ushort)>();
                int dst = 0;
                while (dst < n)
                {
                    int hop = g.Next[src, dst], run = 1;
                    while (dst + run < n && g.Next[src, dst + run] == hop) run++;
                    list.Add(((ushort)run, (ushort)hop));
                    dst += run;
                }
                runs.Add(list);
            }
            int values = nodes + 36 * n;
            int offset = values;
            for (int i = 0; i < n; i++)
            {
                Vector3 p = g.Nodes[i];
                w.Write((ushort)0); w.Write((ushort)i); w.Write((ushort)0); w.Write((ushort)0);   // navigation, id, field4, no neighbours
                w.Write(Fx(p.X)); w.Write(Fx(p.Y)); w.Write(Fx(p.Z));
                w.Write(Fx(maxDistance));
                w.Write((uint)offset); w.Write((uint)offset); w.Write(0u);
                offset += 4 * runs[i].Count;
            }
            foreach (List<(ushort Run, ushort Hop)> list in runs)
            {
                foreach ((ushort run, ushort hop) in list) { w.Write(run); w.Write(hop); }
            }
            return ms.ToArray();
        }

        static int Fx(float v) => (int)MathF.Round(v * 4096);
    }
}
