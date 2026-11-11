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
        // 2 (2026-10-09, owner: bots "stuck rapidly moving back and forth"): one strongly connected graph (every pair has a
        // route), lips crossed, ledges up to JumpRise linked; arena.json NavVersion, which also turns on PlayerAiUnstick
        // (2026-10-10, still 2: long drops, slides down steep slopes, no way across a pad, pockets retried with longer
        // links; the files differ but say the same thing to the game)
        // 3 (2026-10-10, T7, owner: "Do bots know they have double jump and gravity boost in echoes maps"): links a bot
        // takes with Echoes' double jump (a ledge up to DoubleRise, a gap up to GapMax across) or the Gravity Boost (up
        // out of water), listed by kind in arena.json NavLinks for the bots' link following (PlayerAiUnstick)
        // (2026-10-31, still 3: a jump up a ledge may leave from a step back when the ledge overhangs its foot
        // (TakeOff), pockets retried with long walks along walkways narrower than the grid; owner "bots are never going
        // above the first floor" in Mining Plaza)
        public const int Version = 3;
        public const float Spacing = 4.5f;     // MPH graphs: edges 3-16 units, median 4-6
        const float Height = 1.6f;             // a hunter, -0.5..1.1 around its position
        const float StepUp = 0.6f, MaxDrop = 4f, Sample = 0.5f, LongDrop = 40f;
        // a hunter's jump rises ~2.4 (Samus: speed 1228 / gravity 77, fx32 per DS frame); links may climb one ledge this
        // high, walked into: MPH's bot jumps when it is held against a wall on the ground (PlayerAi _horizColTimer)
        const float JumpRise = 1.9f, JumpCost = 3f;
        // measured (Tools -echoesarena jumpreach, every hunter alike): a jump rises 2.54, Echoes' double jump (second press
        // at the top) 4.75; running (0.24 a step) a double jump carries ~10 across before it is back down to its take-off
        // height (9.7 landing 2 higher); the Gravity Boost held 60 steps lifts 5.6 out of Pipeline's cave (9.5 held long).
        // DoubleRise 3.9 -> 3.6 (2026-10-31, owner: a Noxus bot "stuck trying to jump up a ledge" in Mining Plaza):
        // a bot jumps at the ledge's foot, against the wall, with no run-up; -botprobe linkforce there: every double link up
        // to 3.6 made it, all of 3.7-3.9 bounced off the lip
        const float DoubleRise = 3.6f, GapMax = 4.5f, GapRise = 1.5f, GapDrop = 3f, BoostRise = 7f, MoveCost = 6f;
        // a pad's trigger (ArenaCannons.PadRadius 1.5) plus a hunter's body
        const float PadKeepOut = 2f;
        // how far back from the foot of a ledge a jump up it may leave (TakeOff)
        const float TakeOffBack = 1.2f;

        // how a link is taken (arena.json NavLinks; walking links aren't listed)
        public const int LinkJump = 1, LinkDouble = 2, LinkGap = 3, LinkBoost = 4;

        static readonly Dictionary<int, int> _versions = new();

        // an arena registered (info) or gone (null)
        internal static void SetRoom(int roomId, EchoesArena.Info? info)
        {
            lock (_versions)
            {
                if (info == null) _versions.Remove(roomId);
                else _versions[roomId] = info.NavVersion;
                if (info?.NavLinks is { Count: > 0 } list) _links[roomId] = list.Where(l => l.Length >= 3).ToDictionary(l => (l[0], l[1]), l => l[2]);
                else _links.Remove(roomId);
            }
        }

        // this room's bots get MphRead's unstick rule: an Echoes arena whose nav file this version (or later) made
        public static bool Unstick(int roomId)
        {
            lock (_versions) return _versions.TryGetValue(roomId, out int v) && v >= 2;
        }

        public sealed class Graph
        {
            public List<Vector3> Nodes = new();
            public List<List<(int To, float Cost)>> Edges = new();
            public int[,] Next = new int[0, 0];
            // the links taken with a jump, double jump, gap jump or boost: [from, to, kind] (node ids of the written file)
            public List<int[]> Links = new();
            internal Dictionary<(int, int), int> Kinds = new();
            // per room state, over the same Nodes (BuildSets): the next hops of each, written as MPH's node data sets
            // (null: one set, Next)
            public List<int[,]>? Sets;
            // the top set bit is "carrying an octolith" (the second half of Sets avoids the placed pads), not a room state:
            // written flagged in the set index list (0x8000), PlayerAi sets it per bot
            public bool Carrier;
        }

        // the bots' link kinds per room (arena.json NavLinks): (from, to) -> kind; null = none listed
        static readonly Dictionary<int, Dictionary<(int, int), int>> _links = new();

        public static IReadOnlyDictionary<(int, int), int>? Links(int roomId)
        {
            lock (_versions) return _links.TryGetValue(roomId, out Dictionary<(int, int), int>? l) ? l : null;
        }

        public static Graph Build(ArenaRaycaster ray, IReadOnlyList<Vector3> anchors, float killHeight,
            IReadOnlyList<(Vector3 From, Vector3 To)>? jumps = null, IReadOnlyList<(Vector3 Min, Vector3 Max)>? water = null,
            Vector2? gridOrigin = null, bool echoesMoves = true, float padReach = 0, int heavyFrom = int.MaxValue, bool carrier = false,
            IReadOnlyList<(Vector3 Min, Vector3 Max)>? noBots = null)
        {
            var g = new Graph();
            bool Wet(Vector3 p) => water?.Any(w => p.X >= w.Min.X && p.X <= w.Max.X && p.Z >= w.Min.Z && p.Z <= w.Max.Z
                && p.Y >= w.Min.Y - 0.5f && p.Y + 0.5f < w.Max.Y) == true;
            // 1. floor samples on an XZ grid, every walkable level of each column, with headroom
            // echoesMoves false: no double jump or Gravity Boost links (First Hunt's arenas: MPH's own moves only). padReach: a
            // pad floating up to this high over the floor beside it is reached by a jump into its trigger (First Hunt's
            // Assault Cradle: the centre lifts are discs 2.9 up, which Samus jumps into from the floor around: -fharena liftjump)
            // gridOrigin: a point the grid passes through (First Hunt's arenas: their centre, where their narrow doorways sit
            // on the axes); default half a step in from the arena's corner
            float x0 = gridOrigin is { } go ? go.X + MathF.Ceiling((ray.Min.X - go.X) / Spacing) * Spacing : ray.Min.X + Spacing / 2;
            float z0 = gridOrigin is { } gz ? gz.Y + MathF.Ceiling((ray.Min.Z - gz.Y) / Spacing) * Spacing : ray.Min.Z + Spacing / 2;
            for (float x = x0; x < ray.Max.X; x += Spacing)
            {
                for (float z = z0; z < ray.Max.Z; z += Spacing)
                {
                    float top = ray.Max.Y + 1;
                    while (top > ray.Min.Y)
                    {
                        var from = new Vector3(x, top, z);
                        (float T, Vector3 Normal)? hit = ray.Cast(from, new Vector3(x, ray.Min.Y - 1, z));
                        if (hit is not { } h) break;
                        float y = top - (top - (ray.Min.Y - 1)) * h.T;
                        var floor = new Vector3(x, y, z);
                        if (h.Normal.Y >= 0.7f && y > killHeight + 0.5f && ray.Cast(floor + Vector3.UnitY * 0.05f, floor + Vector3.UnitY * (Height + 0.3f)) == null
                            && noBots?.Any(b => x >= b.Min.X && x <= b.Max.X && z >= b.Min.Z && z <= b.Max.Z && y >= b.Min.Y - 0.5f && y <= b.Max.Y) != true)
                        {
                            g.Nodes.Add(floor);
                        }
                        top = y - 0.05f;
                    }
                }
            }
            // pads: the ones ridden; blocked: the carrier's placed pads, kept as nodes (same ids either way) but never walked onto
            var pads = new List<(int From, int To)>();
            var blocked = new List<(int From, int To)>();
            int jumpIndex = 0;
            foreach ((Vector3 from, Vector3 to) in jumps ?? Array.Empty<(Vector3, Vector3)>())
            {
                g.Nodes.Add(from);
                g.Nodes.Add(to);
                (carrier && jumpIndex++ >= heavyFrom ? blocked : pads).Add((g.Nodes.Count - 2, g.Nodes.Count - 1));
            }
            // 2. walk links to nearby nodes (one-way where it's a drop), and jumps up a ledge
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
                    if (Walkable(ray, a, b) is bool jump)
                    {
                        g.Edges[i].Add((j, (b - a).Length + (jump ? JumpCost : 0)));
                        if (jump) g.Kinds[(i, j)] = LinkJump;
                    }
                    // Echoes' moves (version 3): a ledge too high for one jump, out of water with the boost, a gap
                    else if (echoesMoves && Walkable(ray, a, b, rise: DoubleRise) == true)
                    {
                        g.Edges[i].Add((j, (b - a).Length + MoveCost));
                        g.Kinds[(i, j)] = LinkDouble;
                    }
                    else if (echoesMoves && Wet(a) && Walkable(ray, a, b, rise: BoostRise) == true)
                    {
                        g.Edges[i].Add((j, (b - a).Length + MoveCost));
                        g.Kinds[(i, j)] = LinkBoost;
                    }
                    else if (Walkable(ray, a, b, gap: true) == true)
                    {
                        g.Edges[i].Add((j, (b - a).Length + MoveCost));
                        g.Kinds[(i, j)] = LinkGap;
                    }
                }
            }
            // long drops: stepping off a ledge to any floor further below (MPH has no fall damage). Without these, a ledge or
            // pad landing left only by a drop over MaxDrop was a pocket the component step threw away (Spider Complex: both
            // pads and their y 23.5 top, the y 5.1 east ledge: 33 of 144 nodes, and bots up there gave up)
            for (int i = 0; i < n; i++)
            {
                for (int j = 0; j < n; j++)
                {
                    if (i == j) continue;
                    Vector3 a = g.Nodes[i], b = g.Nodes[j];
                    if ((a.Xz - b.Xz).LengthSquared > reach * reach || a.Y - b.Y <= MaxDrop) continue;
                    if (Walkable(ray, a, b, LongDrop) == false) g.Edges[i].Add((j, (b - a).Length));
                }
            }
            foreach ((int from, int to) in pads)
            {
                g.Edges[from].Clear();   // stepping on a pad launches you
                g.Edges[from].Add((to, (g.Nodes[to] - g.Nodes[from]).Length * 0.5f));
            }
            // nor does any other way cross a pad: a floor node 0.6 from Spider Complex's centre pad had bots launched on the
            // way to it, steering back to it in the air and landing on the pad again, over and over
            bool OnPad(Vector3 p, int pad) => (p.Xz - g.Nodes[pad].Xz).Length < PadKeepOut && p.Y > g.Nodes[pad].Y - 0.5f && p.Y < g.Nodes[pad].Y + 1.5f;
            bool Crosses(Vector3 a, Vector3 b, int pad)
            {
                Vector2 d = b.Xz - a.Xz;
                float t = d.LengthSquared > 0 ? Math.Clamp(Vector2.Dot(g.Nodes[pad].Xz - a.Xz, d) / d.LengthSquared, 0, 1) : 0;
                return OnPad(Vector3.Lerp(a, b, t), pad);
            }
            // i -> j would walk over a pad that isn't j (pads themselves leave only by their flight)
            // (a carrier's blocked pad isn't a way even to itself; off one it was standing on, it walks)
            bool PadInWay(int i, int j) => pads.Any(q => q.From != j && (q.From == i || OnPad(g.Nodes[i], q.From) || Crosses(g.Nodes[i], g.Nodes[j], q.From)))
                || blocked.Any(q => q.From == j || q.From != i && (OnPad(g.Nodes[i], q.From) || Crosses(g.Nodes[i], g.Nodes[j], q.From)));
            for (int i = 0; i < n; i++)
            {
                if (pads.Any(q => q.From == i)) continue;
                g.Edges[i].RemoveAll(e => PadInWay(i, e.To));
            }
            if (padReach > 0)
            {
                foreach ((int pad, _) in pads)
                {
                    Vector3 p = g.Nodes[pad];
                    for (int i = 0; i < n; i++)
                    {
                        Vector3 a = g.Nodes[i];
                        float up = p.Y - a.Y;
                        if (up <= JumpRise || up > padReach || (a.Xz - p.Xz).Length > Spacing || pads.Any(q => q.From == i)
                            || g.Edges[i].Any(e => e.To == pad) || ray.Cast(a + Vector3.UnitY * 0.8f, new Vector3(p.X, a.Y + 0.8f, p.Z)) != null)
                        {
                            continue;
                        }
                        g.Edges[i].Add((pad, (p - a).Length + JumpCost));
                        g.Kinds[(i, pad)] = LinkJump;
                    }
                }
            }
            // 3. keep what the spawns' component can reach and come back from
            var starts = anchors.Select(p => Nearest(g.Nodes, p)).Where(k => k >= 0).Distinct().ToList();
            var keep = StrongComponentOf(g, starts);
            // pockets left out: try longer ways down out of them and into them (Spider Complex's east ledge is left only down
            // a narrow ramp, with no floor node in reach on a straight line past the void beside it), then keep again
            if (keep.Contains(false))
            {
                float far = reach * 2.5f;
                bool added = false;
                for (int i = 0; i < n; i++)
                {
                    for (int j = 0; j < n; j++)
                    {
                        if (i == j || keep[i] && keep[j] || pads.Any(q => q.From == i) || PadInWay(i, j)) continue;
                        Vector3 a = g.Nodes[i], b = g.Nodes[j];
                        if ((a.Xz - b.Xz).LengthSquared <= reach * reach || (a.Xz - b.Xz).LengthSquared > far * far) continue;
                        if (a.Y - b.Y >= StepUp && Walkable(ray, a, b, LongDrop) == false) { g.Edges[i].Add((j, (b - a).Length)); added = true; }
                        // a walkway narrower than the grid, which no row of nodes lands on (Mining Plaza's east bridge,
                        // ~3 wide on a slant between rows 4.5 apart): walked end to end
                        else if (MathF.Abs(a.Y - b.Y) < StepUp && Walkable(ray, a, b) == false) { g.Edges[i].Add((j, (b - a).Length)); added = true; }
                    }
                }
                if (added) keep = StrongComponentOf(g, starts);
            }
            Compact(g, keep);
            // 4. next hops: shortest paths to each destination over the reversed graph
            g.Next = Hops(g.Nodes.Count, g.Edges);
            return g;
        }

        static int[,] Hops(int n, List<List<(int To, float Cost)>> edges)
        {
            var next = new int[n, n];
            var rev = new List<List<(int, float)>>();
            for (int i = 0; i < n; i++) rev.Add(new List<(int, float)>());
            for (int i = 0; i < n; i++) foreach ((int j, float c) in edges[i]) rev[j].Add((i, c));
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
                        foreach ((int v, float c) in edges[src])
                        {
                            if (dist[v] < float.MaxValue && c + dist[v] < best) { best = c + dist[v]; hop = v; }
                        }
                    }
                    next[src, dst] = hop;
                }
            }
            return next;
        }

        // a room whose collision changes during a match (Mining Plaza's pillars: 2^k states, bit i = pillar i down): one
        // graph per state, on one list of nodes (each state's kept nodes, merged), so MPH's node data sets switch the bots'
        // routes when the room changes (PlayerAi.UpdateNodeDataSetSelection, Room.NodeData.SetSelector). A node a state
        // lacks (a floor the rubble covers, the top of the fallen column while it stands) routes to the nearest one it has.
        public static Graph BuildSets(IReadOnlyList<ArenaRaycaster> rays, IReadOnlyList<Vector3> anchors, float killHeight,
            IReadOnlyList<(Vector3 From, Vector3 To)>? jumps = null, IReadOnlyList<(Vector3 Min, Vector3 Max)>? water = null,
            int heavyFrom = int.MaxValue)
        {
            // placed pads (heavyFrom): the states again as an octolith carrier walks them, one more set bit on top (Carrier)
            bool carrier = jumps != null && heavyFrom < jumps.Count;
            Graph[] states = rays.Select(r => Build(r, anchors, killHeight, jumps, water))
                .Concat(carrier ? rays.Select(r => Build(r, anchors, killHeight, jumps, water, heavyFrom: heavyFrom, carrier: true)) : Enumerable.Empty<Graph>())
                .ToArray();
            var g = new Graph { Carrier = carrier };
            var maps = new List<int[]>();
            foreach (Graph st in states)
            {
                var map = new int[st.Nodes.Count];
                for (int k = 0; k < st.Nodes.Count; k++)
                {
                    int u = g.Nodes.FindIndex(p => (p - st.Nodes[k]).LengthSquared < 0.01f);
                    if (u < 0) { u = g.Nodes.Count; g.Nodes.Add(st.Nodes[k]); }
                    map[k] = u;
                }
                maps.Add(map);
            }
            int n = g.Nodes.Count;
            g.Sets = new List<int[,]>();
            for (int s = 0; s < states.Length; s++)
            {
                Graph st = states[s];
                int[] map = maps[s];
                var has = new bool[n];
                foreach (int u in map) has[u] = true;
                var edges = new List<List<(int To, float Cost)>>();
                for (int u = 0; u < n; u++) edges.Add(new List<(int, float)>());
                for (int k = 0; k < st.Nodes.Count; k++)
                {
                    foreach ((int to, float c) in st.Edges[k]) edges[map[k]].Add((map[to], c));
                }
                // (Links are in the compacted ids; Kinds still has the grid's)
                foreach (int[] l in st.Links) g.Kinds.TryAdd((map[l[0]], map[l[1]]), l[2]);
                int[,] next = Hops(n, edges);
                // the nodes this state lacks: leave toward the nearest node it has; routes to them go to that node
                var sub = new int[n];
                for (int u = 0; u < n; u++)
                {
                    sub[u] = u;
                    if (has[u]) continue;
                    float best = float.MaxValue;
                    for (int v = 0; v < n; v++)
                    {
                        float d = (g.Nodes[v] - g.Nodes[u]).LengthSquared;
                        if (has[v] && d < best) { best = d; sub[u] = v; }
                    }
                }
                for (int u = 0; u < n; u++)
                {
                    for (int dst = 0; dst < n; dst++)
                    {
                        if (u == dst) next[u, dst] = u;
                        else if (!has[u]) next[u, dst] = sub[u];
                        else if (!has[dst]) next[u, dst] = sub[dst] == u ? u : next[u, sub[dst]];
                    }
                }
                g.Sets.Add(next);
            }
            g.Next = g.Sets[0];
            g.Edges = Enumerable.Range(0, n).Select(_ => new List<(int, float)>()).ToList();
            g.Links = g.Kinds.Select(kv => new[] { kv.Key.Item1, kv.Key.Item2, kv.Value }).ToList();
            return g;
        }

        // feet stay on walkable floor along the way, rising at most a step per sample (or once, up to JumpRise: a ledge
        // MPH's bot jumps onto when it walks into it), with room for the body. Null = no way; else whether it jumps.
        // rise: the one ledge's limit (JumpRise; DoubleRise / BoostRise for Echoes' moves). gap: one stretch with no floor
        // under it (GapMax across, landing at most GapRise up or GapDrop down, room for the arc), jumped from its edge
        static bool? Walkable(ArenaRaycaster ray, Vector3 a, Vector3 b, float maxDrop = MaxDrop, float rise = JumpRise, bool gap = false)
        {
            int steps = Math.Max(1, (int)MathF.Ceiling((b.Xz - a.Xz).Length / Sample));
            Vector3 prev = a;
            var trail = new List<Vector3> { a };
            bool jumped = false, lip = false;
            Vector3 chest = Vector3.UnitY * 0.8f;
            for (int s = 1; s <= steps; s++)
            {
                Vector3 p = Vector3.Lerp(a, b, s / (float)steps);
                if (gap && !jumped && FloorAt(ray, p, prev.Y + StepUp, maxDrop) == null && SlideAt(ray, p, prev.Y, maxDrop) == null)
                {
                    // no floor here: the first floor further along, within GapMax of the edge and the arc's heights
                    int land = -1;
                    Vector3 lf = default;
                    for (int t = s + 1; t <= steps; t++)
                    {
                        Vector3 q = Vector3.Lerp(a, b, t / (float)steps);
                        if ((q.Xz - prev.Xz).Length > GapMax + Sample) break;
                        if (FloorAt(ray, q, prev.Y + GapRise, GapDrop) is { Lip: false } gf && gf.F.Y - prev.Y <= GapRise)
                        {
                            land = t;
                            lf = gf.F;
                            break;
                        }
                    }
                    Vector3 head = Vector3.UnitY * 2.6f;
                    if (land < 0 || ray.Cast(prev + chest, lf + chest) != null || ray.Cast(prev + head, lf + head) != null
                        || ray.Cast(prev + chest, prev + head) != null)
                    {
                        return null;
                    }
                    jumped = true;
                    prev = lf;
                    s = land;
                    continue;
                }
                (Vector3 F, bool Lip)? floor = FloorAt(ray, p, prev.Y + StepUp, maxDrop);
                Vector3 f;
                if (floor is { } fl && fl.F.Y - prev.Y <= StepUp && !(fl.Lip && (lip || s == steps)))
                {
                    f = fl.F;
                    lip = fl.Lip;
                    if (prev.Y - f.Y > MaxDrop)
                    {
                        // a long fall: off the edge level, then straight down (a slanted ray would clip the ledge's corner)
                        var over = new Vector3(f.X, prev.Y, f.Z);
                        if (ray.Cast(prev + chest, over + chest) != null || ray.Cast(over + chest, f + chest) != null) return null;
                    }
                    else if (ray.Cast(prev + chest, f + chest) != null) return null;
                }
                else if (!jumped && SlideAt(ray, p, prev.Y, maxDrop) is { } down)
                {
                    // down a slope too steep to walk: she slides (Spider Complex's east ledge is left only down a ramp of
                    // normal 0.3, and entered down one from the deck; the centre top's rim is a 0.64 bevel)
                    f = down;
                    lip = false;
                    if (ray.Cast(prev + chest, f + chest) != null) return null;
                }
                else if (!jumped && !lip && !gap && FloorAt(ray, p, prev.Y + rise) is { Lip: false } up && up.F.Y - prev.Y > StepUp
                    && TakeOff(ray, trail, p, up.F, rise))
                {
                    jumped = true;
                    f = up.F;
                }
                else
                {
                    return null;
                }
                prev = f;
                trail.Add(f);
            }
            return MathF.Abs(prev.Y - b.Y) < 0.3f ? jumped : null;
        }

        // where a jump up onto `top` (first floor found at p) leaves from: the last floor walked, or one a little further
        // back when that is tucked under the ledge (Mining Plaza's slabs lean out and overhang their foot, so no jump from
        // the wall's foot had room and the whole upper half of the room was cut off, owner 10-31 "bots are never going
        // above the first floor"); within the reach of the bot's wall check (PlayerAiUnstick.BotWallAhead, 1.2)
        static bool TakeOff(ArenaRaycaster ray, List<Vector3> trail, Vector3 p, Vector3 top, float rise)
        {
            Vector3 chest = Vector3.UnitY * 0.8f;
            for (int k = trail.Count - 1; k >= 0; k--)
            {
                Vector3 t = trail[k];
                if ((t.Xz - p.Xz).Length > TakeOffBack + Sample) break;
                if (top.Y - t.Y > rise) continue;
                if (ray.Cast(t + chest, new Vector3(t.X, top.Y + Height + 0.3f, t.Z)) == null     // room to jump
                    && ray.Cast(new Vector3(t.X, top.Y, t.Z) + chest, top + chest) == null)      // and over
                {
                    return true;
                }
            }
            return false;
        }

        // the walkable floor under p from at most `top` down (a step + MaxDrop); Lip = no floor there but a steep strip
        // with nothing under it, crossed as one sample between floors (a step's bevelled edge: Spires' return pad stands
        // behind a 0.25 step whose bevel reads 0.44 up and cut the north island off). Steep slopes give lips in a row.
        static (Vector3 F, bool Lip)? FloorAt(ArenaRaycaster ray, Vector3 p, float top, float maxDrop = MaxDrop)
        {
            float from = top + 0.05f, bottom = top - StepUp - maxDrop - 0.1f;
            (float T, Vector3 Normal)? hit = ray.Cast(new Vector3(p.X, from, p.Z), new Vector3(p.X, bottom, p.Z));
            if (hit is not { } h) return null;
            float y = from - (from - bottom) * h.T;
            if (h.Normal.Y >= 0.7f) return (new Vector3(p.X, y, p.Z), false);
            if (h.Normal.Y < 0.25f || ray.Cast(new Vector3(p.X, y - 0.02f, p.Z), new Vector3(p.X, y - StepUp, p.Z)) != null) return null;
            return (new Vector3(p.X, y, p.Z), true);
        }

        // a steep slope (not a wall) under p, below the feet at y: slid down, never climbed
        static Vector3? SlideAt(ArenaRaycaster ray, Vector3 p, float y, float maxDrop)
        {
            float from = y + StepUp + 0.05f, bottom = y - StepUp - maxDrop - 0.1f;
            (float T, Vector3 Normal)? hit = ray.Cast(new Vector3(p.X, from, p.Z), new Vector3(p.X, bottom, p.Z));
            if (hit is not { } h || h.Normal.Y < 0.25f || h.Normal.Y >= 0.7f) return null;
            float fy = from - (from - bottom) * h.T;
            return fy < y - 0.1f ? new Vector3(p.X, fy, p.Z) : null;
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

        // one strongly connected component: every kept node can reach every other, so every route-table entry has a
        // real next hop. (Was: reachable from ANY anchor and reaching ANY anchor, which kept one-way pockets, e.g. a ledge
        // you can only drop off or an island you only leave by pad: up to 36% of pairs had no route, and MPH's bot then
        // gets its own node as the next hop and dithers on it forever, owner 10-09 "rapidly moving back and forth".)
        // The component with the most anchors wins (ties: the bigger one); bots spawned outside it walk or drop into it.
        static bool[] StrongComponentOf(Graph g, List<int> anchors)
        {
            int n = g.Nodes.Count;
            var keep = new bool[n];
            var rev = new List<int>[n];
            for (int i = 0; i < n; i++) rev[i] = new List<int>();
            for (int i = 0; i < n; i++) foreach ((int j, _) in g.Edges[i]) rev[j].Add(i);
            int bestAnchors = 0, bestSize = 0;
            var done = new bool[n];
            foreach (int a in anchors)
            {
                if (done[a]) continue;
                bool[] fwd = Flood(n, new List<int> { a }, i => g.Edges[i].Select(e => e.To));
                bool[] back = Flood(n, new List<int> { a }, i => rev[i]);
                var comp = new bool[n];
                int size = 0;
                for (int i = 0; i < n; i++) if (comp[i] = fwd[i] && back[i]) { size++; done[i] = true; }
                int count = anchors.Count(k => comp[k]);
                if (count > bestAnchors || count == bestAnchors && size > bestSize)
                {
                    bestAnchors = count; bestSize = size; keep = comp;
                }
            }
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
            var links = new List<int[]>();
            for (int i = 0; i < g.Nodes.Count; i++)
            {
                if (!keep[i]) continue;
                edges.Add(g.Edges[i].Where(e => keep[e.To]).Select(e => (map[e.To], e.Cost)).ToList());
                foreach ((int to, _) in g.Edges[i])
                {
                    if (keep[to] && g.Kinds.TryGetValue((i, to), out int kind)) links.Add(new[] { map[i], map[to], kind });
                }
            }
            g.Nodes = nodes;
            g.Edges = edges;
            g.Links = links;
        }

        public static byte[] Write(Graph g, float maxDistance = 1.2f)
        {
            int n = g.Nodes.Count;
            // one set (every arena but a changing room) keeps the original layout byte for byte; S = 2^bits sets add
            // the set index list (the bits PlayerAi reads from Room.NodeData.SetSelector) and a node table per set
            List<int[,]> sets = g.Sets ?? new List<int[,]> { g.Next };
            int count = sets.Count, bits = 0;
            while ((1 << bits) < count) bits++;
            System.Diagnostics.Debug.Assert(1 << bits == count);
            using var ms = new MemoryStream();
            using var w = new BinaryWriter(ms);
            const int indexOffset = 14;
            int dataOffset = indexOffset + 2 * Math.Max(1, bits);
            int set2 = dataOffset + 8 * count, nodes = set2 + 8 * count;
            w.Write((ushort)6); w.Write((ushort)count); w.Write((uint)indexOffset); w.Write((uint)dataOffset); w.Write((ushort)bits);
            if (bits == 0) w.Write((ushort)0);                     // the (empty) set index list, 2 bytes before the data
            for (int b = 0; b < bits; b++) w.Write((ushort)(g.Carrier && b == bits - 1 ? 0x8000 | b : b));
            for (int s = 0; s < count; s++) { w.Write((uint)(set2 + 8 * s)); w.Write((ushort)1); w.Write((ushort)0x5C); }
            for (int s = 0; s < count; s++) { w.Write((uint)(nodes + 36 * n * s)); w.Write((ushort)n); w.Write((ushort)0x5C); }
            System.Diagnostics.Debug.Assert(ms.Position == nodes);
            // the route runs, after the node tables
            var runs = new List<List<(ushort Run, ushort Hop)>>();
            foreach (int[,] next in sets)
            {
                for (int src = 0; src < n; src++)
                {
                    var list = new List<(ushort, ushort)>();
                    int dst = 0;
                    while (dst < n)
                    {
                        int hop = next[src, dst], run = 1;
                        while (dst + run < n && next[src, dst + run] == hop) run++;
                        list.Add(((ushort)run, (ushort)hop));
                        dst += run;
                    }
                    runs.Add(list);
                }
            }
            int offset = nodes + 36 * n * count;
            for (int s = 0; s < count; s++)
            {
                for (int i = 0; i < n; i++)
                {
                    Vector3 p = g.Nodes[i];
                    w.Write((ushort)0); w.Write((ushort)i); w.Write((ushort)0); w.Write((ushort)0);   // navigation, id, field4, no neighbours
                    w.Write(Fx(p.X)); w.Write(Fx(p.Y)); w.Write(Fx(p.Z));
                    w.Write(Fx(maxDistance));
                    w.Write((uint)offset); w.Write((uint)offset); w.Write(0u);
                    offset += 4 * runs[s * n + i].Count;
                }
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
