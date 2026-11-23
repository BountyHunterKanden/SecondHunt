using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MphRead;
using MphRead.Formats;
using MphRead.Formats.Collision;
using MphRecomp.Render;
using OpenTK.Mathematics;

namespace MphRead
{
    // Holiday mode ground snow research (owner 2026-11-20): which room materials cover the up-facing floors of the rooms
    // with snow collision (terrain 5), by area, so the ground snow can borrow a vanilla snow texture.
    //   -holidayground [room name ...]   (no names: every room with snow collision)
    public static class HolidayGroundTool
    {
        // texture repeats per world unit along the snow's surface (UV span over position span, first triangles)
        static float Density(Model model, int mat)
        {
            float sum = 0; int n = 0;
            foreach (Mesh mesh in model.Meshes.Where(m => m.MaterialId == mat))
            {
                float[] v = DsDisplayList.Decode(model, mesh, isRoom: true);
                int s = DsDisplayList.Stride;
                for (int i = 0; i + 3 * s <= v.Length && n < 200; i += 3 * s)
                {
                    var a = new Vector3(v[i], v[i + 1], v[i + 2]); var b = new Vector3(v[i + s], v[i + s + 1], v[i + s + 2]);
                    float d = (b - a).Length * model.Scale.X;
                    float uv = new Vector2(v[i + s + 10] - v[i + 10], v[i + s + 11] - v[i + 11]).Length;
                    if (d > 0.5f) { sum += uv / d; n++; }
                }
            }
            return n > 0 ? sum / n * 128 : 0;
        }

        // -holidaylayers: per multiplayer room, the nodes its Battle layer mask switches off (never drawn) that hold roof
        // triangles (not upright), by name and count, and the holo/door-looking materials (ground snow v3, owner point 6)
        // -holidayprebuild [cacheRoot]: holiday mode's background ground snow build, waited for (timing + cache size);
        // cacheRoot defaults to the PC app's cache, so a PC match then reads these copies
        public static void Prebuild(string[] args)
        {
            if (!ToolPaths.UseDesktopExtraction())
            {
                return;
            }
            MphRecomp.Holiday.HolidaySnow.Apply("on");
            MphRecomp.Holiday.HolidayGround.CacheRoot = args.Length > 1 ? args[1]
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MPH Recomp", "cache");
            var sw = System.Diagnostics.Stopwatch.StartNew();
            MphRecomp.Holiday.HolidayGround.Prebuild();
            while (MphRecomp.Holiday.HolidayGround.Prebuilding)
            {
                System.Threading.Thread.Sleep(200);
            }
            string dir = Path.Combine(MphRecomp.Holiday.HolidayGround.CacheRoot, "holiday_snow");
            long bytes = Directory.Exists(dir) ? Directory.GetFiles(dir).Sum(f => new FileInfo(f).Length) : 0;
            Console.WriteLine($"prebuild: {sw.ElapsedMilliseconds} ms, {(Directory.Exists(dir) ? Directory.GetFiles(dir).Length : 0)} files, {bytes / 1024} KB in {dir}");
        }

        // -holidaybackdroptest [arena]: the menus' arena backdrop with holiday mode on and an empty ground snow cache, as
        // after an update (Odin 2026-11-21: Main Gyro Chamber's snow built in the menus' first frame back from a game,
        // 1.4 s). PASS: the worker's load (CampaignHost.StartMenuBackdrop) builds the sky map and ground snow, and the
        // first step (on the GL thread in the app) builds nothing
        public static void BackdropTest(string[] args)
        {
            if (!ToolPaths.UseDesktopExtraction())
            {
                return;
            }
            string arena = args.Length > 1 ? args[1] : "ECHOES MAIN GYRO CHAMBER";
            MphRecomp.Arenas.EchoesArena.Register(Paths.FileSystem);
            MphRecomp.Holiday.HolidaySnow.Apply("on");
            string cache = Path.Combine(Path.GetTempPath(), "SecondHunt-backdroptest-" + Environment.ProcessId);
            MphRecomp.Holiday.HolidayGround.CacheRoot = cache; // empty: nothing built ahead
            try
            {
                bool pass = true;
                ulong? builtHash = null;
                // twice: built (empty cache, as after an update), then read back from the cache (every later quit)
                // then read ahead as a game's pause menu opening does (MenuArenaBackdrop.ReadAhead), the load taking it
                foreach (string pass2 in new[] { "built", "cached", "ahead" })
                {
                    if (pass2 == "ahead" && Metadata.GetRoomByName(arena).Item1 is RoomMetadata aheadMeta)
                    {
                        MphRecomp.Holiday.HolidayGround.ReadAhead(aheadMeta);
                        System.Threading.Thread.Sleep(1500); // (the pause menu is up a second or so before YES)
                    }
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    // its snow made beside the room load, as CampaignHost.BeginMenuBackdrop does (MPH_HOLIDAY_EARLY=0: the old way)
                    MphRecomp.Holiday.HolidaySnow.PrepareEarly(Metadata.GetRoomByName(arena).Item1);
                    var host = MphRecomp.Campaign.CampaignHost.StartMenuBackdrop(arena, 1920, 1080);
                    long load = sw.ElapsedMilliseconds;
                    if (pass2 == "ahead" && !MphRecomp.Holiday.HolidayGround.LastReadAhead)
                    {
                        Console.WriteLine("the snow read ahead was not taken");
                        pass = false;
                    }
                    bool map = MphRecomp.Holiday.HolidaySnow.Map is { } m && m.RoomId == host.RoomId;
                    int files = Directory.Exists(Path.Combine(cache, "holiday_snow")) ? Directory.GetFiles(Path.Combine(cache, "holiday_snow")).Length : 0;
                    Console.WriteLine($"backdrop {arena} ({pass2}): worker load {load} ms ({MphRecomp.Campaign.CampaignHost.LoadTimes}), sky map {(map ? "ready" : "MISSING")}, ground snow cache files {files}");
                    double first = 0, worst = 0;
                    for (int i = 0; i < 4; i++)
                    {
                        var step = System.Diagnostics.Stopwatch.StartNew();
                        host.StepBackdrop();
                        double ms = step.Elapsed.TotalMilliseconds;
                        if (i == 0) first = ms;
                        worst = Math.Max(worst, ms);
                    }
                    Console.WriteLine($"first step {first:0.0} ms, worst of 4 {worst:0.0} ms");
                    pass &= map && first < 100;
                    // the snow read back from the cache is the snow that was built: every instruction's code and arguments
                    ulong h = 14695981039346656037UL;
                    int instructions = 0;
                    if (host.Scene.Room is MphRead.Entities.RoomEntity roomEnt
                        && MphRead.Entities.RoomEntity.HostOverlay?.Invoke(roomEnt) is ModelInstance cover)
                    {
                        foreach (RenderInstruction ins in cover.Model.RenderInstructionLists[0])
                        {
                            instructions++;
                            h = (h ^ (uint)ins.Code) * 1099511628211UL;
                            foreach (uint a in ins.Arguments) h = (h ^ a) * 1099511628211UL;
                        }
                    }
                    Console.WriteLine($"snow: {instructions} instructions, hash {h:x16}");
                    // the cover's vertices decoded on a worker (HolidayGround.DecodeCoverAhead) are the GL thread's own decode
                    if (host.Scene.Room is MphRead.Entities.RoomEntity roomEnt2
                        && MphRead.Entities.RoomEntity.HostOverlay?.Invoke(roomEnt2) is ModelInstance cover2)
                    {
                        Mesh mesh = cover2.Model.Meshes[0];
                        var dsw = System.Diagnostics.Stopwatch.StartNew();
                        float[]? ahead = MphRecomp.Holiday.HolidayGround.TakeCoverVerts(cover2.Model, mesh);
                        double waited = dsw.Elapsed.TotalMilliseconds;
                        dsw.Restart();
                        float[] own = DsDisplayList.Decode(cover2.Model, mesh, isRoom: true);
                        bool same = ahead != null && ahead.AsSpan().SequenceEqual(own);
                        Console.WriteLine($"cover decoded ahead: {(ahead == null ? "MISSING" : same ? "same" : "DIFFERS")} ({own.Length / DsDisplayList.Stride} vertices; waited {waited:0} ms, own decode {dsw.ElapsedMilliseconds} ms)");
                        pass &= same;
                    }
                    if (builtHash == null) builtHash = h;
                    else if (builtHash != h) { Console.WriteLine("cached snow differs from the built snow"); pass = false; }
                    host.Dispose();
                }
                Console.WriteLine(pass ? "holidaybackdroptest: PASS" : "holidaybackdroptest: FAIL");
            }
            finally
            {
                try { Directory.Delete(cache, recursive: true); } catch (IOException) { }
            }
        }

        public static void Layers(string[] args)
        {
            if (!ToolPaths.UseDesktopExtraction())
            {
                return;
            }
            for (int id = 0; id < 160; id++)
            {
                RoomMetadata? meta;
                try { meta = Metadata.GetRoomById(id, noThrow: true); } catch (ArgumentOutOfRangeException) { break; }
                if (meta == null || !meta.Multiplayer)
                {
                    continue;
                }
                Model model;
                try { model = Read.GetRoomModelInstance(meta.Name).Model; }
                catch (Exception ex) { Console.WriteLine($"{meta.Name}: {ex.GetType().Name}"); continue; }
                model.FilterNodes(SceneSetup.GetNodeLayer(GameMode.Battle, meta.NodeLayer, 2));
                var off = new List<string>();
                var holo = new HashSet<string>();
                foreach (Node node in model.Nodes)
                {
                    bool drawn = node.Enabled && model.NodeParentsEnabled(node);
                    int roofs = 0;
                    for (int k = 0; k < node.MeshCount; k++)
                    {
                        Mesh mesh = model.Meshes[node.MeshId / 2 + k];
                        string mat = model.Materials[mesh.MaterialId].Name;
                        if (mat.Contains("holo", StringComparison.OrdinalIgnoreCase) || mat.Contains("door", StringComparison.OrdinalIgnoreCase)
                            || mat.Contains("force", StringComparison.OrdinalIgnoreCase))
                        {
                            holo.Add($"{mat}@{node.Name}{(drawn ? "" : " OFF")}{(mesh.Visible ? "" : " INVIS")}");
                        }
                        float[] v = DsDisplayList.Decode(model, mesh, isRoom: true);
                        int s = DsDisplayList.Stride;
                        for (int i = 0; i + 3 * s <= v.Length; i += 3 * s)
                        {
                            var a = new Vector3(v[i], v[i + 1], v[i + 2]);
                            Vector3 n = Vector3.Cross(new Vector3(v[i + s], v[i + s + 1], v[i + s + 2]) - a, new Vector3(v[i + 2 * s], v[i + 2 * s + 1], v[i + 2 * s + 2]) - a);
                            if (n.LengthSquared > 1e-8f && MathF.Abs(n.Normalized().Y) >= 0.2f) roofs++;
                        }
                    }
                    if (!drawn && roofs > 0) off.Add($"{node.Name}:{roofs}");
                }
                Console.WriteLine($"{id} {meta.Name} ({meta.InGameName}) scale {model.Scale.X} off: {string.Join(" ", off)}");
                if (holo.Count > 0) Console.WriteLine($"    holo/door mats: {string.Join(", ", holo)}");
            }
        }

        // -holidaysky [all | room ...]: the snow's sky map per room (Battle layer, or the campaign's for a campaign room) as
        // the game builds it: the build line, then the map shrunk to at most 100 columns ('#' all closed, 'o' some cell
        // open to the sky, '.' nothing drawn). "all": every multiplayer room, a summary line each (universal sky map,
        // owner 2026-11-21)
        public static void Sky(string[] args)
        {
            if (!ToolPaths.UseDesktopExtraction())
            {
                return;
            }
            MphRecomp.Arenas.EchoesArena.EnsureRegistered(); // the imported arenas (Shooting Gallery etc.) too
            var names = args.Skip(1).ToList();
            bool campaign = names.Contains("campaign"); // every campaign room's summary line instead
            bool all = names.Count == 0 || names.Contains("all") || campaign;
            for (int id = 0; id < 3000; id++)
            {
                RoomMetadata? meta;
                try { meta = Metadata.GetRoomById(id, noThrow: true); } catch (ArgumentOutOfRangeException) { continue; } // past MPH's rooms: the imported arenas' ids (1000+)
                if (meta == null || (all ? meta.Multiplayer == campaign :!names.Contains(meta.Name) && !names.Contains(meta.InGameName)))
                {
                    continue;
                }
                int mask = meta.Multiplayer ? SceneSetup.GetNodeLayer(GameMode.Battle, meta.NodeLayer, 2)
                    : SceneSetup.GetNodeLayer(GameMode.SinglePlayer, meta.NodeLayer, 1);
                MphRecomp.Holiday.HolidaySnow.RoofMap map;
                Console.Write($"{id} {meta.Name} ({meta.InGameName}): ");
                Model skyRoom;
                List<(Vector3 A, Vector3 B, Vector3 C)> fillTris;
                try { map = MphRecomp.Holiday.HolidayGround.SkyMap(meta, mask, out skyRoom, out _, out fillTris); }
                catch (Exception ex) { Console.WriteLine($"{ex.GetType().Name} {ex.Message}"); continue; }
                Console.WriteLine("    sky nodes: " + string.Join(" ", skyRoom.Nodes.Where(nd => nd.Name.Contains("sky", StringComparison.OrdinalIgnoreCase))
                    .Select(nd => $"{nd.Name}{(nd.Enabled && skyRoom.NodeParentsEnabled(nd) ? "" : "(off)")}:{nd.MeshCount}")));
                if (all || map.W == 0)
                {
                    continue;
                }
                // the ceiling cells FillCeilings closed: their heights and extent (2 triangles a cell)
                if (fillTris.Count > 0)
                {
                    var byY = fillTris.GroupBy(t => (int)MathF.Round(t.A.Y)).OrderBy(g => g.Key)
                        .Select(g => $"y{g.Key}:{g.Count() / 2}");
                    float fx0 = fillTris.Min(t => t.A.X), fx1 = fillTris.Max(t => t.C.X), fz0 = fillTris.Min(t => t.A.Z), fz1 = fillTris.Max(t => t.C.Z);
                    Console.WriteLine($"    closed by the ceiling: x {fx0:0}..{fx1:0} z {fz0:0}..{fz1:0}, cells by height {string.Join(" ", byY)}");
                    // each closed patch (cells touching), biggest first: where to stand and look up
                    var cellY = new Dictionary<(int, int), float>();
                    for (int t = 0; t < fillTris.Count; t += 2)
                    {
                        cellY[((int)MathF.Round((fillTris[t].A.X - map.X0) / map.Cell), (int)MathF.Round((fillTris[t].A.Z - map.Z0) / map.Cell))] = fillTris[t].A.Y;
                    }
                    var done = new HashSet<(int, int)>();
                    var found = new List<(int N, float X, float Z, float Lo, float Hi)>();
                    foreach (var c0 in cellY.Keys)
                    {
                        if (!done.Add(c0)) continue;
                        var st = new Stack<(int, int)>();
                        st.Push(c0);
                        int n = 0; float sx = 0, sz = 0, lo = float.MaxValue, hi = float.MinValue;
                        while (st.Count > 0)
                        {
                            var c = st.Pop();
                            n++; sx += c.Item1; sz += c.Item2; lo = MathF.Min(lo, cellY[c]); hi = MathF.Max(hi, cellY[c]);
                            foreach (var d in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
                            {
                                var nb = (c.Item1 + d.Item1, c.Item2 + d.Item2);
                                if (cellY.ContainsKey(nb) && done.Add(nb)) st.Push(nb);
                            }
                        }
                        found.Add((n, map.X0 + (sx / n + 0.5f) * map.Cell, map.Z0 + (sz / n + 0.5f) * map.Cell, lo, hi));
                    }
                    foreach (var f in found.Where(f => f.N * map.Cell * map.Cell >= 2).OrderByDescending(f => f.N).Take(8))
                    {
                        Console.WriteLine($"      closed patch {f.N * map.Cell * map.Cell,6:0.0} sq at ({f.X:0.0}, {f.Z:0.0}) y {f.Lo:0.0}..{f.Hi:0.0}");
                    }
                }
                // what tops the open cells: node, material, its culling and which way the triangle faces
                Model room = Read.ReadRoomModelUncached(meta);
                room.FilterNodes(mask);
                var what = new Dictionary<string, int>();
                for (int ni = 0; ni < room.Nodes.Count; ni++)
                {
                    Node node = room.Nodes[ni];
                    if (!MphRecomp.Holiday.HolidaySnow.Drawn(room, node)) continue;
                    for (int k = 0; k < node.MeshCount; k++)
                    {
                        Mesh mesh = room.Meshes[node.MeshId / 2 + k];
                        if (!mesh.Visible) continue;
                        Material mat = room.Materials[mesh.MaterialId];
                        float[] v = DsDisplayList.Decode(room, mesh, isRoom: true);
                        int st = DsDisplayList.Stride;
                        for (int o = 0; o + 3 * st <= v.Length; o += 3 * st)
                        {
                            var a = new Vector3(v[o], v[o + 1], v[o + 2]) * room.Scale.X;
                            var b = new Vector3(v[o + st], v[o + st + 1], v[o + st + 2]) * room.Scale.X;
                            var c = new Vector3(v[o + 2 * st], v[o + 2 * st + 1], v[o + 2 * st + 2]) * room.Scale.X;
                            Vector3 n = Vector3.Cross(b - a, c - a);
                            if (n.LengthSquared < 1e-8f || MathF.Abs(n.Normalized().Y) < 0.2f) continue;
                            float y = MathF.Max(a.Y, MathF.Max(b.Y, c.Y));
                            var mid = (a + b + c) / 3;
                            int i = map.CellAt(mid);
                            if (i < 0 || !map.Open[i] || MathF.Abs(map.Top[i] / 8f - y) > 0.5f) continue;
                            string key = $"{node.Name}/{mat.Name} {mat.Culling} n.y {(n.Y > 0 ? "+" : "-")} y~{y:0}";
                            what[key] = what.GetValueOrDefault(key) + 1;
                        }
                    }
                }
                foreach (var kv in what.OrderByDescending(kv => kv.Value).Take(15)) Console.WriteLine($"    {kv.Value,4} {kv.Key}");
                // the open patches, biggest first: where to stand and look up (PC shots of the sky)
                var seenP = new bool[map.W * map.H];
                var patches = new List<(int Cells, float X, float Z, float Lo, float Hi)>();
                for (int s0 = 0; s0 < map.W * map.H; s0++)
                {
                    if (!map.Open[s0] || seenP[s0]) continue;
                    var st2 = new Stack<int>();
                    st2.Push(s0);
                    seenP[s0] = true;
                    int cnt = 0;
                    float sx = 0, sz = 0, lo = float.MaxValue, hi = float.MinValue;
                    while (st2.Count > 0)
                    {
                        int i = st2.Pop();
                        int x = i % map.W, z = i / map.W;
                        cnt++;
                        sx += x;
                        sz += z;
                        lo = MathF.Min(lo, map.Top[i] / 8f);
                        hi = MathF.Max(hi, map.Top[i] / 8f);
                        foreach ((int dx, int dz) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
                        {
                            int nx = x + dx, nz = z + dz, n = nz * map.W + nx;
                            if (nx < 0 || nz < 0 || nx >= map.W || nz >= map.H || !map.Open[n] || seenP[n]) continue;
                            seenP[n] = true;
                            st2.Push(n);
                        }
                    }
                    patches.Add((cnt, map.X0 + (sx / cnt + 0.5f) * map.Cell, map.Z0 + (sz / cnt + 0.5f) * map.Cell, lo, hi));
                }
                foreach (var pt in patches.OrderByDescending(pt => pt.Cells).Take(8))
                {
                    Console.WriteLine($"    patch {pt.Cells * map.Cell * map.Cell,7:0.0} sq at ({pt.X:0.0}, {pt.Z:0.0}) top {pt.Lo:0.0}..{pt.Hi:0.0}");
                }
                if (!args.Contains("grid")) continue;
                int step = Math.Max(1, (int)MathF.Ceiling(map.W / 100f));
                var line = new System.Text.StringBuilder();
                for (int z0 = (map.H - 1) / step * step; z0 >= 0; z0 -= step)
                {
                    line.Clear();
                    for (int x0 = 0; x0 < map.W; x0 += step)
                    {
                        bool open = false, any = false;
                        for (int z = z0; z < Math.Min(map.H, z0 + step); z++)
                        {
                            for (int x = x0; x < Math.Min(map.W, x0 + step); x++)
                            {
                                int i = z * map.W + x;
                                open |= map.Open[i];
                                any |= map.Top[i] != MphRecomp.Holiday.HolidaySnow.NoRoof;
                            }
                        }
                        line.Append(open ? 'o' : any ? '#' : '.');
                    }
                    Console.WriteLine(line.ToString());
                }
            }
        }

        // -holidayroof <room ...>: the falling snow's roof map for a room, cell by cell: the first surface down with every
        // drawn triangle counted vs with the steep and wide ones left out (as the roof map did), and which node tops each
        // cell as a letter grid ('+'/'*': the old map's roof well under the first surface) (falling snow inside, 2026-11-20)
        public static void Roofs(string[] args)
        {
            if (!ToolPaths.UseDesktopExtraction())
            {
                return;
            }
            var names = args.Skip(1).ToList();
            for (int id = 0; id < 160; id++)
            {
                RoomMetadata? meta;
                try { meta = Metadata.GetRoomById(id, noThrow: true); } catch (ArgumentOutOfRangeException) { break; }
                if (meta == null || !names.Contains(meta.Name) && !names.Contains(meta.InGameName))
                {
                    continue;
                }
                Model model;
                try { model = Read.GetRoomModelInstance(meta.Name).Model; }
                catch (Exception ex) { Console.WriteLine($"{meta.Name}: {ex.GetType().Name}"); continue; }
                model.FilterNodes(SceneSetup.GetNodeLayer(meta.Multiplayer ? GameMode.Battle : GameMode.SinglePlayer, meta.NodeLayer, 2));
                var tris = new List<(Vector3 A, Vector3 B, Vector3 C, int Node, int Mat, bool Steep, bool Wide)>();
                float sc = model.Scale.X;
                for (int ni = 0; ni < model.Nodes.Count; ni++)
                {
                    Node node = model.Nodes[ni];
                    if (!MphRecomp.Holiday.HolidaySnow.Drawn(model, node)) continue;
                    for (int k = 0; k < node.MeshCount; k++)
                    {
                        Mesh mesh = model.Meshes[node.MeshId / 2 + k];
                        if (!mesh.Visible) continue;
                        float[] v = DsDisplayList.Decode(model, mesh, isRoom: true);
                        int s = DsDisplayList.Stride;
                        for (int i = 0; i + 3 * s <= v.Length; i += 3 * s)
                        {
                            var a = new Vector3(v[i], v[i + 1], v[i + 2]) * sc;
                            var b = new Vector3(v[i + s], v[i + s + 1], v[i + s + 2]) * sc;
                            var c = new Vector3(v[i + 2 * s], v[i + 2 * s + 1], v[i + 2 * s + 2]) * sc;
                            Vector3 n = Vector3.Cross(b - a, c - a);
                            if (n.LengthSquared < 1e-8f) continue;
                            Vector3 mn = Vector3.ComponentMin(a, Vector3.ComponentMin(b, c)), mx = Vector3.ComponentMax(a, Vector3.ComponentMax(b, c));
                            tris.Add((a, b, c, ni, mesh.MaterialId, MathF.Abs(n.Normalized().Y) < 0.2f, mx.X - mn.X > 60 || mx.Z - mn.Z > 60));
                        }
                    }
                }
                Vector3 lo = new Vector3(float.MaxValue), hi = new Vector3(float.MinValue);
                foreach (var t in tris)
                {
                    lo = Vector3.ComponentMin(lo, Vector3.ComponentMin(t.A, Vector3.ComponentMin(t.B, t.C)));
                    hi = Vector3.ComponentMax(hi, Vector3.ComponentMax(t.A, Vector3.ComponentMax(t.B, t.C)));
                }
                const float cell = 1.5f;
                int w = (int)MathF.Ceiling((hi.X - lo.X) / cell), h = (int)MathF.Ceiling((hi.Z - lo.Z) / cell);
                Console.WriteLine($"{id} {meta.Name} ({meta.InGameName}) scale {sc} {tris.Count} tris, {tris.Count(t => t.Steep)} steep, {tris.Count(t => t.Wide)} wide, {w}x{h} cells, y {lo.Y:F1}..{hi.Y:F1}");
                static float? YAt(Vector3 a, Vector3 b, Vector3 c, float x, float z)
                {
                    float d = (b.Z - c.Z) * (a.X - c.X) + (c.X - b.X) * (a.Z - c.Z);
                    if (MathF.Abs(d) < 1e-9f) return null;
                    float u = ((b.Z - c.Z) * (x - c.X) + (c.X - b.X) * (z - c.Z)) / d, ww = ((c.Z - a.Z) * (x - c.X) + (a.X - c.X) * (z - c.Z)) / d;
                    if (u < 0 || ww < 0 || u + ww > 1) return null;
                    return u * a.Y + ww * b.Y + (1 - u - ww) * c.Y;
                }
                var letters = new Dictionary<int, char>();
                var topCount = new Dictionary<int, int>();
                var grid = new System.Text.StringBuilder();
                int leaks = 0, single = 0;
                var leakNodes = new Dictionary<string, int>();
                for (int z = h - 1; z >= 0; z--)
                {
                    for (int x = 0; x < w; x++)
                    {
                        float px = lo.X + (x + 0.5f) * cell, pz = lo.Z + (z + 0.5f) * cell;
                        float all = float.MinValue, kept = float.MinValue;
                        int topNode = -1, topMat = -1, hits = 0;
                        foreach (var t in tris)
                        {
                            if (YAt(t.A, t.B, t.C, px, pz) is not float y) continue;
                            hits++;
                            if (y > all) { all = y; topNode = t.Node; topMat = t.Mat; }
                            if (!t.Steep && !t.Wide && y > kept) kept = y;
                        }
                        char ch = '.';
                        if (topNode >= 0)
                        {
                            if (!letters.TryGetValue(topNode, out ch))
                            {
                                ch = letters.Count < 26 ? (char)('A' + letters.Count) : letters.Count < 52 ? (char)('a' + letters.Count - 26) : '#';
                                letters[topNode] = ch;
                            }
                            topCount[topNode] = topCount.GetValueOrDefault(topNode) + 1;
                            if (all - kept > 1.5f && hits >= 2)
                            {
                                leaks++;
                                string key = model.Nodes[topNode].Name + "/" + model.Materials[topMat].Name + (kept == float.MinValue ? " (none kept)" : "");
                                leakNodes[key] = leakNodes.GetValueOrDefault(key) + 1;
                                ch = '+';
                            }
                            if (hits == 1) single++;
                        }
                        grid.Append(ch);
                    }
                    grid.Append('\n');
                }
                Console.WriteLine($"  cells whose old-map roof is >1.5 under the first surface: {leaks}; cells with one surface only: {single}");
                foreach (var p in leakNodes.OrderByDescending(p => p.Value).Take(12)) Console.WriteLine($"    leak under {p.Key}: {p.Value}");
                foreach (var p in topCount.OrderByDescending(p => p.Value))
                {
                    var mats = tris.Where(t => t.Node == p.Key).Select(t => model.Materials[t.Mat].Name).Distinct().Take(4);
                    Console.WriteLine($"  {letters[p.Key]} {model.Nodes[p.Key].Name} tops {p.Value} cells, mats {string.Join(",", mats)}");
                }
                Console.Write(grid.ToString());
                // the drawn top vs the collision top per cell, as height digits (0-9 over the room's y range; '.' none)
                var col = new List<(Vector3 A, Vector3 B, Vector3 C)>();
                try
                {
                    if (Collision.GetCollision(meta).Info is MphCollisionInfo ci)
                    {
                        foreach (CollisionData d in ci.Data)
                        {
                            Vector3 p0 = ci.Points[ci.PointIndices[d.PointStartIndex]];
                            for (int q = 1; q + 1 < d.PointIndexCount; q++)
                            {
                                col.Add((p0, ci.Points[ci.PointIndices[d.PointStartIndex + q]], ci.Points[ci.PointIndices[d.PointStartIndex + q + 1]]));
                            }
                        }
                    }
                }
                catch (Exception ex) { Console.WriteLine($"  collision: {ex.Message}"); }
                char Digit(float y) => y == float.MinValue ? '.' : (char)('0' + Math.Clamp((int)((y - lo.Y) / (hi.Y - lo.Y + 1e-3f) * 10), 0, 9));
                var g2 = new System.Text.StringBuilder();
                for (int z = h - 1; z >= 0; z--)
                {
                    var l1 = new System.Text.StringBuilder();
                    var l2 = new System.Text.StringBuilder();
                    for (int x = 0; x < w; x++)
                    {
                        float px = lo.X + (x + 0.5f) * cell, pz = lo.Z + (z + 0.5f) * cell;
                        float kept = float.MinValue, ct = float.MinValue;
                        foreach (var t in tris) if (!t.Steep && !t.Wide && YAt(t.A, t.B, t.C, px, pz) is float y && y > kept) kept = y;
                        foreach (var t in col) if (YAt(t.A, t.B, t.C, px, pz) is float y && y > ct) ct = y;
                        l1.Append(Digit(kept));
                        l2.Append(Digit(ct));
                    }
                    g2.Append(l1).Append("   ").Append(l2).AppendLine();
                }
                foreach (string probe in Environment.GetEnvironmentVariable("ROOF_PROBE")?.Split(';', StringSplitOptions.RemoveEmptyEntries) ?? Array.Empty<string>())
                {
                    // column,row as printed (row 0 = top line)
                    string[] pr = probe.Split(',');
                    int x = int.Parse(pr[0]), z = h - 1 - int.Parse(pr[1]);
                    float px = lo.X + (x + 0.5f) * cell, pz = lo.Z + (z + 0.5f) * cell;
                    Console.WriteLine($"  probe {probe} at {px:F1},{pz:F1}:");
                    foreach (var t in tris)
                    {
                        if (YAt(t.A, t.B, t.C, px, pz) is not float y) continue;
                        Vector3 n = Vector3.Cross(t.B - t.A, t.C - t.A).Normalized();
                        Console.WriteLine($"    drawn y {y:F1} n.y {n.Y:F2} {model.Nodes[t.Node].Name}/{model.Materials[t.Mat].Name}{(t.Steep ? " steep" : "")}");
                    }
                    foreach (var t in col)
                    {
                        if (YAt(t.A, t.B, t.C, px, pz) is float y) Console.WriteLine($"    collision y {y:F1}");
                    }
                    // every node over the probe, drawn or not, with why the snow skips it
                    foreach (Node node in model.Nodes)
                    {
                        for (int k = 0; k < node.MeshCount; k++)
                        {
                            Mesh mesh = model.Meshes[node.MeshId / 2 + k];
                            float[] v = DsDisplayList.Decode(model, mesh, isRoom: true);
                            int s = DsDisplayList.Stride;
                            for (int i = 0; i + 3 * s <= v.Length; i += 3 * s)
                            {
                                var a = new Vector3(v[i], v[i + 1], v[i + 2]) * sc;
                                var b = new Vector3(v[i + s], v[i + s + 1], v[i + s + 2]) * sc;
                                var c = new Vector3(v[i + 2 * s], v[i + 2 * s + 1], v[i + 2 * s + 2]) * sc;
                                if (YAt(a, b, c, px, pz) is float y && y > 5)
                                {
                                    Console.WriteLine($"    any y {y:F1} {node.Name}/{model.Materials[mesh.MaterialId].Name} enabled {node.Enabled} parents {model.NodeParentsEnabled(node)} visible {mesh.Visible} alpha {model.Materials[mesh.MaterialId].Alpha} poly {model.Materials[mesh.MaterialId].PolygonMode}");
                                }
                            }
                        }
                    }
                }
                Console.WriteLine($"  drawn top (left) vs collision top (right), {col.Count} collision triangles:");
                Console.Write(g2.ToString());
            }
        }

        public static void Run(string[] args)
        {
            if (!ToolPaths.UseDesktopExtraction())
            {
                return;
            }
            var names = args.Skip(1).ToList();
            for (int id = 0; id < 160; id++)
            {
                RoomMetadata? meta;
                try { meta = Metadata.GetRoomById(id, noThrow: true); } catch (ArgumentOutOfRangeException) { break; }
                if (meta == null)
                {
                    continue;
                }
                int snow = 0;
                try
                {
                    if (Collision.GetCollision(meta).Info is MphCollisionInfo col)
                    {
                        snow = col.Data.Count(d => d.Terrain == Terrain.Snow);
                    }
                }
                catch (Exception) { }
                if (names.Count > 0 ? !names.Contains(meta.Name) && !names.Contains(meta.InGameName) : snow == 0)
                {
                    continue;
                }
                Model model;
                try { model = Read.GetRoomModelInstance(meta.Name).Model; }
                catch (Exception ex) { Console.WriteLine($"{meta.Name}: {ex.GetType().Name}"); continue; }
                var area = new Dictionary<int, (float Up, float All)>();
                foreach (Node node in model.Nodes)
                {
                    if (node.Name.Contains("sky", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                    for (int k = 0; k < node.MeshCount; k++)
                    {
                        Mesh mesh = model.Meshes[node.MeshId / 2 + k];
                        float[] v = DsDisplayList.Decode(model, mesh, isRoom: true);
                        int s = DsDisplayList.Stride;
                        for (int i = 0; i + 3 * s <= v.Length; i += 3 * s)
                        {
                            var a = new Vector3(v[i], v[i + 1], v[i + 2]);
                            var b = new Vector3(v[i + s], v[i + s + 1], v[i + s + 2]);
                            var c = new Vector3(v[i + 2 * s], v[i + 2 * s + 1], v[i + 2 * s + 2]);
                            Vector3 n = Vector3.Cross(b - a, c - a);
                            float ar = n.Length / 2 * model.Scale.X * model.Scale.X;
                            if (ar <= 0)
                            {
                                continue;
                            }
                            (float up, float all) = area.GetValueOrDefault(mesh.MaterialId);
                            area[mesh.MaterialId] = (up + (n.Y / n.Length > 0.6f ? ar : 0), all + ar);
                        }
                    }
                }
                Console.WriteLine($"{id} {meta.Name} ({meta.InGameName}) snow col {snow} scale {model.Scale.X}");
                foreach ((int mat, (float up, float all)) in area.OrderByDescending(p => p.Value.Up).Take(12))
                {
                    Material m = model.Materials[mat];
                    string tex = m.TextureId == -1 ? "-" : $"tex{m.TextureId} {model.Recolors[0].Textures[m.TextureId].Width}x{model.Recolors[0].Textures[m.TextureId].Height}";
                    Console.WriteLine($"    mat{mat} {m.Name} {tex} {m.RenderMode} up {up:F0} all {all:F0}");
                    if (m.Name.Contains("snow", StringComparison.OrdinalIgnoreCase))
                    {
                        Console.WriteLine($"      light {m.Lighting} alpha {m.Alpha} dif {m.Diffuse.Red},{m.Diffuse.Green},{m.Diffuse.Blue} amb {m.Ambient.Red},{m.Ambient.Green},{m.Ambient.Blue}"
                            + $" texgen {m.TexgenMode} st {m.ScaleS},{m.ScaleT} rep {m.XRepeat},{m.YRepeat} poly {m.PolygonMode} cull {m.Culling} anim {m.AnimationFlags} texel/unit {Density(model, mat):F1}");
                    }
                }
            }
        }
    }
}
