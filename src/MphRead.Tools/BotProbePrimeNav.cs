using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MphRead.Entities;
using MphRecomp.Arenas;
using OpenTK.Mathematics;

namespace MphRead
{
    // Bot move solver step 1 (docs/BOT_MOVE_SOLVER.md, "Status and the rest of the plan"), measure only: the parts
    // (strongly connected components) ArenaNav.Build's graph falls into on an imported arena, every node kept, at the
    // import's grid and a finer one, without the shipped placed pads. Writes the nodes and links as TSV for a look.
    //
    //   -botprobe primenav=<room name, e.g. PRIME MONITOR STATION>[@spacing][!h = the human scan] [out=<dir>]
    internal static partial class BotProbe
    {
        private static void PrimeNav(string arg, string? outDir)
        {
            Paths.UpdatePaths(); Paths.ChooseMphPath();
            int reg = EchoesArena.Register(Paths.FileSystem); // registers the imported rooms
            Console.WriteLine($"files {Paths.FileSystem}, {reg} arenas registered, public {MphRecomp.Config.RecompSettings.PublicBuild}");
            bool human = arg.EndsWith("!h");
            if (human) arg = arg[..^2];
            string room = arg.Split('@')[0];
            float spacing = arg.Contains('@') ? Single.Parse(arg.Split('@')[1], System.Globalization.CultureInfo.InvariantCulture) : 4.5f;
            RoomMetadata meta = Metadata.GetRoomByName(room).Item1 ?? throw new ArgumentException($"no room {room}");
            var col = Formats.Collision.Collision.GetCollision(meta, roomLayerMask: -1);
            var cinfo = (Formats.Collision.MphCollisionInfo)col.Info;
            var tris = new List<(Vector3, Vector3, Vector3)>();
            foreach (Formats.Collision.CollisionData data in cinfo.Data)
            {
                var pts = Enumerable.Range(0, data.PointIndexCount).Select(i => cinfo.Points[cinfo.PointIndices[data.PointStartIndex + i]]).ToList();
                for (int j = 1; j + 1 < pts.Count; j++) tris.Add((pts[0], pts[j], pts[j + 1]));
            }
            var ray = new ArenaRaycaster(tris);
            string archive = Path.GetDirectoryName(Path.Combine(Paths.FileSystem, meta.CollisionPath))!;
            EchoesArena.Info info = System.Text.Json.JsonSerializer.Deserialize<EchoesArena.Info>(File.ReadAllText(Path.Combine(archive, "arena.json")))!;
            List<Vector3> spawns = Read.GetEntities(meta.EntityPath!, -1, firstHunt: false)
                .Where(e => e.Type == EntityType.PlayerSpawn).Select(e => e.Position).ToList();
            var water = (info.Fluids ?? new()).Where(f => f.Kind == "water")
                .Select(f => (new Vector3(f.Min[0], f.Min[1], f.Min[2]), new Vector3(f.Max[0], f.Max[1], f.Max[2]))).ToList();
            // a Prime 1 room's lava floor (Prime1Rooms.NoBots): the note's height, over the whole room
            var lava = info.Notes.Select(t => System.Text.RegularExpressions.Regex.Match(t, @"lava floor \(MPH Lava terrain\) at (-?[0-9.]+)"))
                .Where(m => m.Success).Select(m => Single.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture))
                .Select(y => (new Vector3(ray.Min.X - 1, ray.Min.Y - 1, ray.Min.Z - 1), new Vector3(ray.Max.X + 1, y + 0.1f, ray.Max.Z + 1))).ToList();
            Console.WriteLine($"{room}: {tris.Count} triangles, box {Fmt(ray.Min)}..{Fmt(ray.Max)}, kill {info.KillHeight:0.0}, {spawns.Count} spawns, "
                + $"{water.Count} water, {lava.Count} lava, fluids {String.Join(",", (info.Fluids ?? new()).Select(f => f.Kind))}");
            // PRIMENAV_COL="x,z,top;...": every surface a ray straight down meets, 6 below top
            foreach (string c in (Environment.GetEnvironmentVariable("PRIMENAV_COL") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                float[] v = c.Split(',').Select(t => Single.Parse(t, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
                float y = v[2];
                var hits = new List<string>();
                while (y > v[2] - 6 && ray.Cast(new Vector3(v[0], y, v[1]), new Vector3(v[0], v[2] - 6, v[1])) is { } h)
                {
                    float hy = y - (y - (v[2] - 6)) * h.T;
                    hits.Add($"{hy:0.00} n{Fmt(h.Normal)}");
                    y = hy - 0.01f;
                }
                Console.WriteLine($"  col {c}: {String.Join(", ", hits)}");
            }
            ArenaNav.Graph imp = ArenaNav.Build(ray, spawns, info.KillHeight, null, water, noBots: lava, human: human);
            Console.WriteLine($"  the import's build, no placed pads: {imp.Nodes.Count} nodes kept");
            if (Environment.GetEnvironmentVariable("PRIMENAV_WRITE") is { Length: > 0 } wp) File.WriteAllBytes(wp, ArenaNav.Write(imp)); // the import's nodes file, to match a replay's kept copy
            var sw = System.Diagnostics.Stopwatch.StartNew();
            ArenaNav.Graph g = ArenaNav.Build(ray, spawns, info.KillHeight, null, water, noBots: lava, spacing: spacing, keepAll: true, human: human);
            int n = g.Nodes.Count;
            int[] comp = Tarjan(n, g.Edges);
            var parts = Enumerable.Range(0, n).GroupBy(i => comp[i]).OrderByDescending(p => p.Count()).ToList();
            Console.WriteLine($"  grid {spacing}: {n} floor nodes, {g.Edges.Sum(e => e.Count)} links, {parts.Count} parts ({sw.ElapsedMilliseconds} ms)");
            foreach (var p in parts.Take(25))
            {
                var pts = p.Select(i => g.Nodes[i]).ToList();
                var lo = pts.Aggregate(Vector3.ComponentMin); var hi = pts.Aggregate(Vector3.ComponentMax);
                int sp = spawns.Count(s => comp[Nearest(g.Nodes, s)] == p.Key);
                Console.WriteLine($"    part {p.Key}: {p.Count()} nodes, y {lo.Y:0.0}..{hi.Y:0.0}, x {lo.X:0.0}..{hi.X:0.0}, z {lo.Z:0.0}..{hi.Z:0.0}{(sp > 0 ? $", {sp} spawns" : "")}");
            }
            foreach (Vector3 s in spawns)
            {
                int k = Nearest(g.Nodes, s);
                Console.WriteLine($"  spawn {Fmt(s)}: node {Fmt(g.Nodes[k])} part {comp[k]} ({parts.First(p => p.Key == comp[k]).Count()} nodes)");
            }
            // why the near misses fail: per pair of big parts with no link up between them, the lowest climbs in reach,
            // walked with the double jump's 3.6 and the measured 4.75
            var big = parts.Where(p => p.Count() >= 15).Select(p => p.Key).ToHashSet();
            var linked = new HashSet<(int, int)>();
            for (int i = 0; i < n; i++) foreach ((int j, _) in g.Edges[i]) linked.Add((comp[i], comp[j]));
            foreach (int pa in big)
            {
                foreach (int pb in big)
                {
                    if (pa == pb || linked.Contains((pa, pb))) continue;
                    var tries = (from i in Enumerable.Range(0, n) where comp[i] == pa from j in Enumerable.Range(0, n) where comp[j] == pb
                                 let a = g.Nodes[i] let b = g.Nodes[j] let rise = b.Y - a.Y let h = (a.Xz - b.Xz).Length
                                 where rise > -4 && rise < 6 && h <= 6.75f orderby rise, h select (a, b)).Take(3).ToList();
                    foreach ((Vector3 a, Vector3 b) in tries)
                    {
                        Console.WriteLine($"  why {pa}->{pb}: {Fmt(a)} -> {Fmt(b)} rise {b.Y - a.Y:0.00}");
                        foreach (float rise in new[] { 3.6f, 4.75f })
                        {
                            var why = new List<string>();
                            ArenaNav.Trace = why.Add;
                            bool? ok = ArenaNav.WalkableProbe(ray, a, b, rise, gap: false, human);
                            bool? okGap = ArenaNav.WalkableProbe(ray, a, b, 1.9f, gap: true, human);
                            ArenaNav.Trace = null;
                            Console.WriteLine($"      rise {rise}: {(ok == null ? "no" : ok == true ? "jump" : "walk")}, gap {(okGap == null ? "no" : "yes")}: {String.Join(" | ", why)}");
                        }
                    }
                }
            }
            if (outDir != null)
            {
                Directory.CreateDirectory(outDir);
                File.WriteAllLines(Path.Combine(outDir, $"nodes_{spacing}.tsv"), Enumerable.Range(0, n)
                    .Select(i => $"{i}\t{g.Nodes[i].X:0.00}\t{g.Nodes[i].Y:0.00}\t{g.Nodes[i].Z:0.00}\t{comp[i]}"));
                var kinds = g.Links.ToDictionary(l => (l[0], l[1]), l => l[2]);
                File.WriteAllLines(Path.Combine(outDir, $"links_{spacing}.tsv"), Enumerable.Range(0, n)
                    .SelectMany(i => g.Edges[i].Select(e => $"{i}\t{e.To}\t{e.Cost:0.0}\t{(kinds.TryGetValue((i, e.To), out int kd) ? kd : 0)}")));
                Console.WriteLine($"  wrote {outDir}");
            }
        }

        private static int Nearest(List<Vector3> nodes, Vector3 p)
        {
            int best = 0; float bd = Single.MaxValue;
            for (int i = 0; i < nodes.Count; i++) { float d = (nodes[i] - p).LengthSquared; if (d < bd) { bd = d; best = i; } }
            return best;
        }

        // strongly connected components (iterative Tarjan): the component id of each node
        private static int[] Tarjan(int n, List<List<(int To, float Cost)>> edges)
        {
            var index = new int[n]; var low = new int[n]; var comp = new int[n]; var on = new bool[n];
            Array.Fill(index, -1);
            var stack = new Stack<int>(); int next = 0, comps = 0;
            for (int r = 0; r < n; r++)
            {
                if (index[r] >= 0) continue;
                var work = new Stack<(int V, int E)>();
                work.Push((r, 0)); index[r] = low[r] = next++; stack.Push(r); on[r] = true;
                while (work.Count > 0)
                {
                    (int v, int e) = work.Pop();
                    if (e < edges[v].Count)
                    {
                        work.Push((v, e + 1));
                        int w = edges[v][e].To;
                        if (index[w] < 0) { index[w] = low[w] = next++; stack.Push(w); on[w] = true; work.Push((w, 0)); }
                        else if (on[w]) low[v] = Math.Min(low[v], index[w]);
                        continue;
                    }
                    if (low[v] == index[v])
                    {
                        int w;
                        do { w = stack.Pop(); on[w] = false; comp[w] = comps; } while (w != v);
                        comps++;
                    }
                    if (work.Count > 0) { int u = work.Peek().V; low[u] = Math.Min(low[u], low[v]); }
                }
            }
            return comp;
        }
    }
}
