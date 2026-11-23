using System;
using System.Collections.Generic;
using MphRead;
using MphRead.Effects;
using MphRead.Entities;
using MphRead.Formats;
using MphRead.Formats.Collision;
using MphRecomp.Arenas;
using MphRecomp.Render;
using OpenTK.Mathematics;

namespace MphRecomp.Holiday
{
    // HOLIDAY MODE's snow (owner 2026-11-20), built the way Phendrana Shorelines' snow works (docs/HOLIDAY_SNOW.md; our
    // own code and flake, nothing of Prime's). This side works out, once per room, where snow may fall; the app draws
    // the flakes (MphRecomp.App CampaignRenderer.HolidaySnow.cs: a field of stateless flakes round the camera, one draw).
    //
    // The sky map, one rule for all the snow (owner 2026-11-21: "maps that are fully inside and dont have sky visible SHOULD
    // NEVER have snow. maps that are outside but have several inside areas should never have snow falling through
    // ceiling", one universal rule, no per-map patches). The room's floor plan in SkyCell x SkyCell cells; each cell gets
    // the highest point of the room's drawn geometry over ANY part of it (every triangle clipped to the cell), and whether
    // that top surface is seen from above. Snow is only ever where it sees the sky: over a cell whose top is seen from
    // above (a floor, a roof's top), and above that top. So:
    //   - under a ceiling (drawn facing down, seen only from under it) no snow at any height, flat or sloped. The old map
    //     took one height at each 1.5-unit cell's middle, and under a sloped or curved ceiling flakes showed in the band
    //     below the ceiling's high side, then vanished on falling past the middle's height (Data Shrine, owner
    //     2026-11-21: "falling through a ceiling, realizing it shouldnt be there, then disappears")
    //   - over nothing (out in space past Head Shot's arms) or past the room's walls, none
    //   - a room with no cell open to the sky (all indoors) gets no snow at all, falling or on the ground, and its bells
    //     play everywhere (HolidayBells)
    // Built whole when the room loads. The ground snow (HolidayGround) and the bells (HolidayBells) ask this same map.
    //
    // The ground snow and breath also use the drawn roofs below, the ground snow reaching DriftReach in under ledges
    // (where real snow would pile up) and deeper in corners.
    public static class HolidaySnow
    {
        public const float SkyCell = 0.5f; // MPH units
        public const short NoRoof = short.MinValue;
        const int MaxCells = 4_000_000; // a room bigger than this many cells gets coarser ones
        const float Tie = 0.05f; // two tops this close are one height: the cell is open only if both are seen from above
        const float MinSky = 4; // sq units: a smaller patch of open cells is a seam or a frame's top, not sky

        // the room's sky map, row-major in X then Z
        public sealed class RoofMap
        {
            public int RoomId;
            public float X0, Z0, Cell = SkyCell;
            public int W, H;
            // per cell: the highest drawn point over any part of it (x 8; NoRoof: nothing there at all), and whether that
            // top surface is seen from above
            public short[] Top = Array.Empty<short>();
            public bool[] Open = Array.Empty<bool>();
            public int OpenCells;

            // some of the room is open to the sky (none: an indoor room, no snow anywhere)
            public bool AnyOpen => OpenCells > 0;

            public int CellAt(Vector3 at)
            {
                int x = (int)MathF.Floor((at.X - X0) / Cell), z = (int)MathF.Floor((at.Z - Z0) / Cell);
                return x < 0 || z < 0 || x >= W || z >= H ? -1 : z * W + x;
            }

            // snow may be here: over a cell open to the sky, and above everything in that cell
            public bool SeesSky(Vector3 at)
            {
                int i = CellAt(at);
                return i >= 0 && Open[i] && at.Y * 8 >= Top[i];
            }

            // the column here is open to the sky (its top seen from above), at whatever height
            public bool OpenAt(Vector3 at)
            {
                int i = CellAt(at);
                return i >= 0 && Open[i];
            }
        }

        public static bool Enabled { get; private set; }
        // the current room's map (null: none yet, or holiday mode off); swapped whole, read by the app's draw
        public static RoofMap? Map { get; private set; }

        const float CellXZ = 2f; // the drawn roofs' buckets (ground snow, breath)
        const float SkyRay = 300f; // how far up a ray looks for a roof
        static Scene? _scene;
        static int _roomId = -1;
        // the room's drawn roof triangles (anything not upright), by 2x2 column, and each once
        static readonly Dictionary<(int, int), List<(Vector3 A, Vector3 B, Vector3 C)>> _roofs = new();
        static readonly List<(Vector3, Vector3, Vector3)> _drawn = new();
        static readonly List<sbyte> _drawnFacing = new(); // _drawn's: 1 seen from above, -1 only from below, 0 both
        static readonly List<Vector3> _drawnSky = new(); // the corners of the room's sky shapes (DrawnTris)
        static bool _roofsBuilt;
        const float SkyTriangle = 60f; // a triangle wider than this is the sky dome
        const float SkyReach = 30f; // how far past the room's collision a sky shape reaches

        // HOLIDAY MODE: "on", "off" or "auto" (December 1 to January 6)
        public static void Apply(string? mode)
        {
            DateTime now = DateTime.Now;
            bool season = now.Month == 12 || (now.Month == 1 && now.Day <= 6);
            bool on = mode == "on" || (mode == "auto" && season);
            Enabled = on;
            Scene.HostCosmeticFrame = Frame;
        }


        static void Frame(Scene scene)
        {
            if (!Ready(scene))
            {
                return;
            }
            HolidayGround.Frame(scene);
            HolidayBreath.Frame(scene, RoofAbove);
        }

        // a room's snow made ready before its first frame, off the GL thread (the menus' arena backdrop, built on a worker:
        // CampaignHost.StartMenuBackdrop): the sky map and the ground snow, so the first frame finds them done (Frame
        // skips a room already built). An arena's ground snow not built ahead yet (HolidayGround.Prebuild, every room
        // again after an update) froze the menus' first frame back from a game 1.4 s (Main Gyro Chamber, Odin 2026-11-21)
        public static void Prepare(Scene scene)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var task = _early;
            _early = null;
            Early? early = null;
            if (task != null)
            {
                try { early = task.Result; } // (still going: it's further along than starting over)
                catch (AggregateException) { }
            }
            long waited = sw.ElapsedMilliseconds;
            if (early != null && (early.RoomId != scene.RoomId || !Enabled))
            {
                early = null;
            }
            if (early != null)
            {
                Adopt(scene, early);
                Console.WriteLine($"Holiday snow: room {early.RoomId}'s sky map and ground snow made beside its load (map {early.MapMs} ms,"
                    + $" ground {early.GroundMs} ms), waited {waited} ms for them");
            }
            if (Ready(scene))
            {
                HolidayGround.Frame(scene, early?.Ground);
            }
            if (early != null && Environment.GetEnvironmentVariable("MPH_HOLIDAY_EARLY_CHECK") == "1")
            {
                CheckEarly(scene, early);
            }
        }

        // ---- the menus' backdrop: its snow made beside its room load (owner 2026-11-22: "can we get back to instant loading
        // on initial game load and match quit while keeping snowfall") ----
        // Prepare built the sky map from the loaded room (113 ms on the Odin's Fault Line) and then the ground snow, after the
        // room load, on the backdrop worker's own time: the menus' cold start and the way back from a game waited for it.
        // Started with the backdrop's load (CampaignHost.BeginMenuBackdrop), this makes both from the room's own copies
        // (HolidayGround.SkyMap, as the background build does) on another worker, so Prepare only takes them.

        sealed record Early(int RoomId, RoofMap Map, List<(Vector3 A, Vector3 B, Vector3 C)> Tris, List<sbyte> Facing,
            List<Vector3> Sky, List<(Vector3 A, Vector3 B, Vector3 C)> Ceiling, HolidayGround.Early? Ground, long MapMs, long GroundMs);
        static System.Threading.Tasks.Task<Early?>? _early;

        // before the backdrop's worker loads meta's room (Battle, 4 players: StartMenuBackdrop's layers)
        public static void PrepareEarly(RoomMetadata? meta)
        {
            _early = null;
            if (!Enabled || meta == null || Environment.GetEnvironmentVariable("MPH_HOLIDAY_EARLY") == "0") // (0: the old way, timing tests)
            {
                return;
            }
            int mask = SceneSetup.GetNodeLayer(GameMode.Battle, meta.NodeLayer, 4);
            // its ground snow's cache file read meanwhile, by the background build's index (MakeEarly takes it when it's
            // the file its own key names)
            HolidayGround.ReadAheadOnce(meta);
            _early = System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    RoofMap map = HolidayGround.SkyMap(meta, mask, out Model room, out var glass, out var ceiling,
                        out var tris, out var facing, out var sky);
                    long mapMs = sw.ElapsedMilliseconds;
                    HolidayGround.Early? ground = HolidayGround.MakeEarly(room, meta, glass, ceiling, map);
                    return new Early(meta.Id, map, tris, facing, sky, ceiling, ground, mapMs, sw.ElapsedMilliseconds - mapMs);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Holiday snow: {meta.Name}'s snow not made beside its load ({ex.Message})");
                    return null;
                }
            });
        }

        // Ready's roofs and sky map from what PrepareEarly made
        static void Adopt(Scene scene, Early early)
        {
            _scene = scene;
            _roomId = scene.RoomId;
            _roofs.Clear();
            _drawn.Clear();
            _drawnFacing.Clear();
            _drawnSky.Clear();
            _drawn.AddRange(early.Tris);
            _drawnFacing.AddRange(early.Facing);
            _drawnSky.AddRange(early.Sky);
            AddRoofs();
            CeilingFill.Clear();
            CeilingFill.AddRange(early.Ceiling);
            Map = early.Map;
            _roofsBuilt = true;
        }

        // MPH_HOLIDAY_EARLY_CHECK=1 (PC tests): the sky map made the old way from the loaded room, compared with the early one
        static void CheckEarly(Scene scene, Early early)
        {
            RoofMap mine = early.Map;
            var keep = (new List<(Vector3, Vector3, Vector3)>(_drawn), new List<(Vector3, Vector3, Vector3)>(CeilingFill));
            _roofs.Clear();
            _drawn.Clear();
            _drawnFacing.Clear();
            _drawnSky.Clear();
            BuildRoofs(scene);
            RoofMap theirs = BuildMap(scene);
            bool same = mine.X0 == theirs.X0 && mine.Z0 == theirs.Z0 && mine.W == theirs.W && mine.H == theirs.H
                && mine.OpenCells == theirs.OpenCells && mine.Top.AsSpan().SequenceEqual(theirs.Top) && mine.Open.AsSpan().SequenceEqual(theirs.Open)
                && _drawn.Count == keep.Item1.Count && CeilingFill.Count == keep.Item2.Count;
            Console.WriteLine($"Holiday snow early check: room {scene.RoomId} sky map {(same ? "MATCHES" : "DIFFERS FROM")} the loaded room's"
                + $" ({_drawn.Count} / {keep.Item1.Count} drawn triangles, {CeilingFill.Count} / {keep.Item2.Count} ceiling)");
            Map = mine; // (the early one stays in use)
        }

        // the room's roofs and sky map, once per room; false while holiday mode is off
        static bool Ready(Scene scene)
        {
            if (scene != _scene || scene.RoomId != _roomId)
            {
                _scene = scene;
                _roomId = scene.RoomId;
                _roofs.Clear();
                _drawn.Clear();
                _drawnFacing.Clear();
                _drawnSky.Clear();
                _roofsBuilt = false;
                Map = null;
            }
            if (!Enabled)
            {
                Map = null;
                return false;
            }
            if (!_roofsBuilt)
            {
                _roofsBuilt = true;
                BuildRoofs(scene);
                Map = BuildMap(scene);
            }
            return true;
        }

        // a slow wind the whole snowfall sways with (units a second; a few seconds a gust)
        public static Vector2 Wind(float t)
        {
            return new Vector2(0.55f * MathF.Sin(t * MathF.Tau / 8.5f) + 0.2f * MathF.Sin(t * MathF.Tau / 3.3f + 1.1f),
                0.4f * MathF.Sin(t * MathF.Tau / 11.7f + 2.3f) + 0.15f * MathF.Sin(t * MathF.Tau / 4.1f));
        }

        // the sky map from the room's drawn geometry (DrawnTris). Not its collision: MPH arenas have invisible collision
        // caps over open sky (Fault Line's whole play area), which would close the sky everywhere; the collision only
        // fills in ceilings the drawn room leaves out (FillCeilings)
        static RoofMap BuildMap(Scene scene)
        {
            MphCollisionInfo? info = null;
            Vector3 shift = Vector3.Zero;
            if (scene.Room is RoomEntity room && room.RoomCollision.Count > 0 && room.RoomCollision[0].Info is MphCollisionInfo i)
            {
                info = i;
                shift = room.RoomCollision[0].Translation;
            }
            CeilingFill.Clear();
            RoomMetadata? meta = (scene.Room as RoomEntity)?.Meta;
            return BuildMap(scene.RoomId, _drawn, _drawnFacing, DrawsSky(meta, _drawnSky, info, shift), CeilingSource(meta, info), shift,
                CeilingFill);
        }

        // the collision FillCeilings may close undrawn ceilings from: an MPH room's (its glass panes aren't drawn, Incubation
        // Vault), none for an imported arena. Its stand-in is its whole render mesh but the blended triangles, and those
        // come back as its glass (ReadGlass), so everything it draws is already in the map: a collision ceiling over its
        // drawn top is an invisible lid over open air. Shooting Gallery's lid at its rim (y ~20) closed its whole outdoor
        // bowl, and no snow fell or lay there (owner 2026-11-21: "it looks like now it thinks there is a ceiling over the big
        // outdoor area")
        internal static MphCollisionInfo? CeilingSource(RoomMetadata? meta, MphCollisionInfo? info)
        {
            return meta != null && EchoesArena.IsEchoesRoom(meta.Name) ? null : info;
        }

        internal static bool DrawsSky(RoomMetadata? meta, List<Vector3> sky, MphCollisionInfo? info, Vector3 shift)
        {
            if (meta != null && EchoesArena.IsEchoesRoom(meta.Name))
            {
                return HdDrawsSky(meta.Name);
            }
            bool borrowedSky = false;
            if (info == null || info.Points.Count == 0 || borrowedSky)
            {
                return true;
            }
            Vector3 lo = new Vector3(float.MaxValue), hi = new Vector3(float.MinValue);
            foreach (Vector3 p in info.Points)
            {
                lo = Vector3.ComponentMin(lo, p + shift);
                hi = Vector3.ComponentMax(hi, p + shift);
            }
            foreach (Vector3 p in sky)
            {
                if (p.Y > hi.Y + SkyReach || p.X < lo.X - SkyReach || p.X > hi.X + SkyReach
                    || p.Z < lo.Z - SkyReach || p.Z > hi.Z + SkyReach)
                {
                    return true;
                }
            }
            return false;
        }

        // the same from plain inputs, for HolidayGround.Prebuild's background build (no scene): the drawn triangles and
        // which way each is seen (DrawnTris), whether the room draws a sky (DrawsSky) and its collision; the ceiling cells
        // it closes go in fill
        internal static RoofMap BuildMap(int roomId, List<(Vector3 A, Vector3 B, Vector3 C)> tris, List<sbyte> facing,
            bool drawsSky, MphCollisionInfo? info, Vector3 shift, List<(Vector3 A, Vector3 B, Vector3 C)> fill)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var map = new RoofMap { RoomId = roomId };
            if (tris.Count == 0)
            {
                return map;
            }
            Vector3 lo = new Vector3(float.MaxValue), hi = new Vector3(float.MinValue);
            foreach ((Vector3 a, Vector3 b, Vector3 c) in tris)
            {
                lo = Vector3.ComponentMin(lo, Vector3.ComponentMin(a, Vector3.ComponentMin(b, c)));
                hi = Vector3.ComponentMax(hi, Vector3.ComponentMax(a, Vector3.ComponentMax(b, c)));
            }
            float cell = SkyCell;
            while ((hi.X - lo.X) / cell * ((hi.Z - lo.Z) / cell) > MaxCells)
            {
                cell *= 1.25f;
            }
            int w = Math.Max(1, (int)MathF.Ceiling((hi.X - lo.X) / cell)), h = Math.Max(1, (int)MathF.Ceiling((hi.Z - lo.Z) / cell));
            map.X0 = lo.X;
            map.Z0 = lo.Z;
            map.Cell = cell;
            map.W = w;
            map.H = h;
            var top = new float[w * h];
            Array.Fill(top, float.NegativeInfinity);
            map.Open = new bool[w * h];
            Raster(tris, facing, map, top, map.Open);
            int stray = DropStrays(map);
            map.Top = new short[w * h];
            int covered = 0;
            for (int i = 0; i < w * h; i++)
            {
                if (float.IsNegativeInfinity(top[i]))
                {
                    map.Top[i] = NoRoof;
                    continue;
                }
                map.Top[i] = (short)Math.Clamp(MathF.Ceiling(top[i] * 8), short.MinValue + 1, short.MaxValue);
                covered++;
            }
            int filled = 0;
            if (drawsSky)
            {
                filled = info == null ? 0 : FillCeilings(info, shift, map, fill);
                stray += DropStrays(map); // the panes' own slivers
            }
            else
            {
                Array.Clear(map.Open); // no sky to see: every hole in its roof is void
            }
            for (int i = 0; i < w * h; i++)
            {
                if (map.Open[i]) map.OpenCells++;
            }
            Console.WriteLine($"Holiday snow sky map: room {roomId}, {w}x{h} cells of {cell:0.00} from ({lo.X:0.0}, {lo.Z:0.0}), "
                + $"{covered} with a surface, {map.OpenCells} open to the sky ({map.OpenCells * cell * cell:0} sq units)"
                + $"{(drawsSky ? "" : ", draws no sky")}{(map.AnyOpen ? "" : ", none: indoors, no snow")}, {stray} stray, {filled} closed by the ceiling, {tris.Count} triangles, "
                + $"{sw.ElapsedMilliseconds} ms");
            return map;
        }

        // every triangle onto every cell it covers any part of: its highest point there (the triangle clipped to the cell; a
        // flat piece's highest point is a corner) and whether it is seen from above (facing: 1 from above, -1 only from
        // below, 0 both). Where two tops tie (a roof's edge meeting a ceiling's), the cell is open only if both are
        static void Raster(List<(Vector3 A, Vector3 B, Vector3 C)> tris, List<sbyte>? facing, RoofMap map, float[] top, bool[] open)
        {
            float cell = map.Cell;
            int w = map.W, h = map.H;
            var p = new Vector3[8];
            var q = new Vector3[8];
            for (int t = 0; t < tris.Count; t++)
            {
                (Vector3 a, Vector3 b, Vector3 c) = tris[t];
                bool up = facing == null || facing[t] >= 0;
                int i0 = Math.Max(0, (int)MathF.Floor((MathF.Min(a.X, MathF.Min(b.X, c.X)) - map.X0) / cell));
                int i1 = Math.Min(w - 1, (int)MathF.Floor((MathF.Max(a.X, MathF.Max(b.X, c.X)) - map.X0) / cell));
                int j0 = Math.Max(0, (int)MathF.Floor((MathF.Min(a.Z, MathF.Min(b.Z, c.Z)) - map.Z0) / cell));
                int j1 = Math.Min(h - 1, (int)MathF.Floor((MathF.Max(a.Z, MathF.Max(b.Z, c.Z)) - map.Z0) / cell));
                for (int j = j0; j <= j1; j++)
                {
                    float cz = map.Z0 + j * cell;
                    for (int i = i0; i <= i1; i++)
                    {
                        float cx = map.X0 + i * cell;
                        p[0] = a;
                        p[1] = b;
                        p[2] = c;
                        int n = Clip(p, 3, q, true, cx, 1);
                        n = Clip(q, n, p, true, cx + cell, -1);
                        n = Clip(p, n, q, false, cz, 1);
                        n = Clip(q, n, p, false, cz + cell, -1);
                        if (n == 0)
                        {
                            continue;
                        }
                        float y = p[0].Y;
                        for (int k = 1; k < n; k++)
                        {
                            y = MathF.Max(y, p[k].Y);
                        }
                        int at = j * w + i;
                        if (y > top[at] + Tie)
                        {
                            top[at] = y;
                            open[at] = up;
                        }
                        else if (y >= top[at] - Tie)
                        {
                            top[at] = MathF.Max(top[at], y);
                            open[at] &= up;
                        }
                    }
                }
            }
        }

        // open cells that are not sky: a thin seam between two ceiling meshes, a door frame's top inside the wall, a ledge
        // poking up between a ceiling's panels (Data Shrine, Processor Core, Ice Hive, Stasis Bunker each showed a few). An
        // open cell stays only if it sits in a patch at least two cells wide every way (an opening: cells whose 3x3 block
        // is all open, grown back by one) and that patch holds MinSky; the cells dropped are returned
        static int DropStrays(RoofMap map)
        {
            int w = map.W, h = map.H;
            bool[] open = map.Open;
            var core = new bool[w * h];
            for (int z = 1; z + 1 < h; z++)
            {
                for (int x = 1; x + 1 < w; x++)
                {
                    bool all = true;
                    for (int dz = -1; dz <= 1 && all; dz++)
                    {
                        for (int dx = -1; dx <= 1 && all; dx++)
                        {
                            all = open[(z + dz) * w + x + dx];
                        }
                    }
                    core[z * w + x] = all;
                }
            }
            var keep = new bool[w * h];
            for (int z = 0; z < h; z++)
            {
                for (int x = 0; x < w; x++)
                {
                    if (!open[z * w + x])
                    {
                        continue;
                    }
                    for (int dz = -1; dz <= 1 && !keep[z * w + x]; dz++)
                    {
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            int nx = x + dx, nz = z + dz;
                            if (nx >= 0 && nz >= 0 && nx < w && nz < h && core[nz * w + nx])
                            {
                                keep[z * w + x] = true;
                                break;
                            }
                        }
                    }
                }
            }
            int minCells = (int)MathF.Ceiling(MinSky / (map.Cell * map.Cell));
            var seen = new bool[w * h];
            var patch = new List<int>();
            var stack = new Stack<int>();
            for (int start = 0; start < w * h; start++)
            {
                if (!keep[start] || seen[start])
                {
                    continue;
                }
                patch.Clear();
                seen[start] = true;
                stack.Push(start);
                while (stack.Count > 0)
                {
                    int i = stack.Pop();
                    patch.Add(i);
                    int x = i % w, z = i / w;
                    for (int d = 0; d < 4; d++)
                    {
                        int nx = x + (d == 0 ? 1 : d == 1 ? -1 : 0), nz = z + (d == 2 ? 1 : d == 3 ? -1 : 0);
                        int n = nz * w + nx;
                        if (nx >= 0 && nz >= 0 && nx < w && nz < h && keep[n] && !seen[n])
                        {
                            seen[n] = true;
                            stack.Push(n);
                        }
                    }
                }
                if (patch.Count < minCells)
                {
                    foreach (int i in patch) keep[i] = false;
                }
            }
            int dropped = 0;
            for (int i = 0; i < w * h; i++)
            {
                if (open[i] && !keep[i])
                {
                    open[i] = false;
                    dropped++;
                }
            }
            return dropped;
        }

        // the polygon's part on one side of x (or z) = bound (side 1: above it, -1: below), into dst; its corner count
        static int Clip(Vector3[] src, int n, Vector3[] dst, bool alongX, float bound, int side)
        {
            int m = 0;
            for (int k = 0; k < n; k++)
            {
                Vector3 a = src[k], b = src[(k + 1) % n];
                float da = side * ((alongX ? a.X : a.Z) - bound), db = side * ((alongX ? b.X : b.Z) - bound);
                if (da >= 0)
                {
                    dst[m++] = a;
                }
                if (da >= 0 != db >= 0)
                {
                    dst[m++] = a + (b - a) * (da / (da - db));
                }
            }
            return m;
        }

        const float CeilingMatch = 1.5f; // how near a hole's collision ceiling must sit to the drawn roof round it

        // the room's ceiling where it isn't drawn: cells whose collision tops the drawn room by more than CeilingMatch, in a
        // patch the drawn roof closes round at that same height, get the collision's height as their top. Incubation Vault's
        // glass ceiling is drawn only as its frame and strips (the panes are the sky seen through), but its collision is
        // whole, so snow fell through the panes into the room (owner 2026-11-20). The patch must be closed in: Fault
        // Line's invisible cap over its open play area meets the room's edge, not a drawn roof, and stays open. The panes
        // are glass to the sky: snow falls onto them, not past them. Their triangles also shelter the ground snow
        // (CeilingFill)
        static int FillCeilings(MphCollisionInfo info, Vector3 shift, RoofMap map, List<(Vector3 A, Vector3 B, Vector3 C)> fill)
        {
            var col = new List<(Vector3 A, Vector3 B, Vector3 C)>();
            foreach (CollisionData data in info.Data)
            {
                if (MathF.Abs(info.Planes[data.PlaneIndex].Y) < 0.2f)
                {
                    continue; // a wall
                }
                Vector3 p0 = info.Points[info.PointIndices[data.PointStartIndex]] + shift;
                for (int q = 1; q + 1 < data.PointIndexCount; q++)
                {
                    col.Add((p0, info.Points[info.PointIndices[data.PointStartIndex + q]] + shift,
                        info.Points[info.PointIndices[data.PointStartIndex + q + 1]] + shift));
                }
            }
            if (col.Count == 0)
            {
                return 0;
            }
            int w = map.W, h = map.H;
            float cell = map.Cell;
            var ceiling = new float[w * h];
            Array.Fill(ceiling, float.NegativeInfinity);
            Raster(col, null, map, ceiling, new bool[w * h]);
            var hole = new bool[w * h];
            for (int i = 0; i < w * h; i++)
            {
                short r = map.Top[i];
                hole[i] = !float.IsNegativeInfinity(ceiling[i]) && (r == NoRoof || ceiling[i] - r / 8f > CeilingMatch);
            }
            // each patch of holes: closed when it doesn't reach the map's edge and at least half its edge is drawn roof at the
            // ceiling's height (the rest may step down to a lower roof, Incubation Vault's ends, or up to a strut)
            var seen = new bool[w * h];
            var filledAt = new bool[w * h];
            var patch = new List<int>();
            var stack = new Stack<int>();
            int filled = 0;
            for (int start = 0; start < w * h; start++)
            {
                if (!hole[start] || seen[start])
                {
                    continue;
                }
                patch.Clear();
                seen[start] = true;
                stack.Push(start);
                int match = 0, edge = 0, matchOpen = 0;
                bool open = false;
                while (stack.Count > 0)
                {
                    int i = stack.Pop();
                    patch.Add(i);
                    int x = i % w, z = i / w;
                    for (int d = 0; d < 4; d++)
                    {
                        int nx = x + (d == 0 ? 1 : d == 1 ? -1 : 0), nz = z + (d == 2 ? 1 : d == 3 ? -1 : 0);
                        if (nx < 0 || nz < 0 || nx >= w || nz >= h)
                        {
                            open = true;
                            continue;
                        }
                        int n = nz * w + nx;
                        if (hole[n])
                        {
                            if (!seen[n])
                            {
                                seen[n] = true;
                                stack.Push(n);
                            }
                            continue;
                        }
                        edge++;
                        short r = map.Top[n];
                        if (r != NoRoof && MathF.Abs(r / 8f - ceiling[i]) <= CeilingMatch)
                        {
                            match++;
                            if (map.Open[n]) matchOpen++;
                        }
                    }
                }
                // and the roof round it at that height is a ceiling (seen from below): a pane's frame. Where it is mostly the
                // top of a wall or a floor (seen from above), the patch is open air under an invisible lid at the rim's
                // height: the campaign Weapons Complex courtyard (lid at y 31, rim 154 of 154 seen from above) and every
                // imported arena's lid. The panes this is for (Incubation Vault, Celestial Gateway, Weapons Complex's
                // shafts, Elder Passage) have none of their rim seen from above (owner 2026-11-21, Shooting Gallery)
                if (open || match * 2 < edge || matchOpen * 4 >= match)
                {
                    continue;
                }
                foreach (int i in patch)
                {
                    map.Top[i] = (short)Math.Clamp(MathF.Ceiling(ceiling[i] * 8), short.MinValue + 1, short.MaxValue);
                    map.Open[i] = true;
                    filledAt[i] = true;
                    Shelter(i);
                    filled++;
                }
            }
            // the drawn strips, struts and frame between the filled panes (at the ceiling or over it): the ground snow's own
            // look-up may pass beside a strip, so they shelter at the ceiling too
            for (int i = 0; i < w * h; i++)
            {
                if (filledAt[i] || hole[i] || float.IsNegativeInfinity(ceiling[i]) || map.Top[i] == NoRoof
                    || map.Top[i] / 8f < ceiling[i] - CeilingMatch)
                {
                    continue;
                }
                int x = i % w, z = i / w;
                bool near = false;
                for (int dz = -1; dz <= 1 && !near; dz++)
                {
                    for (int dx = -1; dx <= 1 && !near; dx++)
                    {
                        int nx = x + dx, nz = z + dz;
                        near = nx >= 0 && nz >= 0 && nx < w && nz < h && filledAt[nz * w + nx];
                    }
                }
                if (near) Shelter(i);
            }
            return filled;

            void Shelter(int i)
            {
                float x0 = map.X0 + i % w * cell, z0 = map.Z0 + i / w * cell, y = ceiling[i];
                var a = new Vector3(x0, y, z0);
                var b = new Vector3(x0 + cell, y, z0);
                var c = new Vector3(x0 + cell, y, z0 + cell);
                var e = new Vector3(x0, y, z0 + cell);
                fill.Add((a, b, c));
                fill.Add((a, c, e));
            }
        }

        // the current room's ceiling cells FillCeilings closed, as triangles (two a cell): cover for the ground snow
        internal static readonly List<(Vector3 A, Vector3 B, Vector3 C)> CeilingFill = new();

        static void BuildRoofs(Scene scene)
        {
            if (scene.Room == null || scene.Room.GetModels().Count == 0)
            {
                return;
            }
            _drawn.AddRange(DrawnTris(scene.Room.GetModels()[0].Model, HdGlass(scene), _drawnFacing, _drawnSky));
            AddRoofs();
        }

        // _drawn into _roofs, by 2x2 column
        static void AddRoofs()
        {
            foreach ((Vector3 a, Vector3 b, Vector3 c) in _drawn)
            {
                Vector3 min = Vector3.ComponentMin(a, Vector3.ComponentMin(b, c));
                Vector3 max = Vector3.ComponentMax(a, Vector3.ComponentMax(b, c));
                for (int cx = (int)MathF.Floor(min.X / CellXZ); cx <= (int)MathF.Floor(max.X / CellXZ); cx++)
                {
                    for (int cz = (int)MathF.Floor(min.Z / CellXZ); cz <= (int)MathF.Floor(max.Z / CellXZ); cz++)
                    {
                        if (!_roofs.TryGetValue((cx, cz), out var list))
                        {
                            _roofs[(cx, cz)] = list = new List<(Vector3, Vector3, Vector3)>();
                        }
                        list.Add((a, b, c));
                    }
                }
            }
        }

        // the room's drawn triangles that can roof something (no walls, slivers or sky), with its glass; any thread. With
        // facing, which way each is seen: 1 from above, -1 only from below (a ceiling: the material culls the other face;
        // the display list's triangles wind counter-clockwise to the front), 0 from both (glass, open to the sky above it).
        // A double-sided surface goes by its winding: Data Shrine's tube and room ceilings are double-sided but wound to
        // face down into the room, and counting them open let snow fall inside (owner 2026-11-21). With sky, the corners of
        // every node on (its sky nodes too) with a triangle wider than SkyTriangle on some axis: the room's sky shapes
        // (DrawsSky)
        internal static List<(Vector3 A, Vector3 B, Vector3 C)> DrawnTris(Model model, List<(Vector3 A, Vector3 B, Vector3 C)> glass,
            List<sbyte>? facing = null, List<Vector3>? sky = null)
        {
            var drawn = new List<(Vector3 A, Vector3 B, Vector3 C)>();
            // room space times the model's scale (what AnimateNodes puts in node.Animation, room nodes untransformed). Not
            // node.Animation itself: that is only set when the room draws, and an imported arena hides its stand-in room
            // behind the HD mesh, so it stayed unscaled (Main Quarry's roof map came out 2x2 cells, owner 2026-11-20)
            Matrix4 toWorld = Matrix4.CreateScale(model.Scale);
            void Add(Vector3 a, Vector3 b, Vector3 c, CullingMode cull, bool isGlass = false)
            {
                Vector3 min = Vector3.ComponentMin(a, Vector3.ComponentMin(b, c));
                Vector3 max = Vector3.ComponentMax(a, Vector3.ComponentMax(b, c));
                Vector3 n = Vector3.Cross(b - a, c - a);
                if (n.LengthSquared < 1e-8f || MathF.Abs(n.Normalized().Y) < 0.2f
                    || max.X - min.X > SkyTriangle || max.Z - min.Z > SkyTriangle)
                {
                    return; // a wall, a sliver or the sky
                }
                drawn.Add((a, b, c));
                // the front face is seen when the back is culled (Back), the back when the front is (Front)
                facing?.Add(isGlass ? (sbyte)0
                    : (n.Y > 0) == (cull != CullingMode.Front) ? (sbyte)1 : (sbyte)-1);
            }
            foreach (Node node in model.Nodes)
            {
                bool isDrawn = Drawn(model, node);
                if (!isDrawn && (sky == null || node.MeshCount == 0 || !node.Enabled || !model.NodeParentsEnabled(node)))
                {
                    continue;
                }
                Vector3 lo = new Vector3(float.MaxValue), hi = new Vector3(float.MinValue);
                bool skySized = false;
                int start = node.MeshId / 2;
                for (int k = 0; k < node.MeshCount; k++)
                {
                    Mesh mesh = model.Meshes[start + k];
                    if (!mesh.Visible)
                    {
                        continue;
                    }
                    CullingMode cull = model.Materials[mesh.MaterialId].Culling;
                    float[] v = DsDisplayList.Decode(model, mesh, isRoom: true);
                    int stride = DsDisplayList.Stride;
                    for (int o = 0; o + 3 * stride <= v.Length; o += 3 * stride)
                    {
                        Vector3 a = Vector3.TransformPosition(new Vector3(v[o], v[o + 1], v[o + 2]), toWorld);
                        Vector3 b = Vector3.TransformPosition(new Vector3(v[o + stride], v[o + stride + 1], v[o + stride + 2]), toWorld);
                        Vector3 c = Vector3.TransformPosition(new Vector3(v[o + 2 * stride], v[o + 2 * stride + 1], v[o + 2 * stride + 2]),
                            toWorld);
                        if (sky != null)
                        {
                            Vector3 min = Vector3.ComponentMin(a, Vector3.ComponentMin(b, c));
                            Vector3 max = Vector3.ComponentMax(a, Vector3.ComponentMax(b, c));
                            Vector3 size = max - min;
                            skySized |= MathF.Max(size.X, MathF.Max(size.Y, size.Z)) > SkyTriangle;
                            lo = Vector3.ComponentMin(lo, min);
                            hi = Vector3.ComponentMax(hi, max);
                        }
                        if (isDrawn)
                        {
                            Add(a, b, c, cull);
                        }
                    }
                }
                if (skySized)
                {
                    sky!.Add(lo);
                    sky.Add(hi);
                }
            }
            // an imported arena's glass: its stand-in leaves blended surfaces out, so the booth's glass roof let the snow
            // fall through it (owner 2026-11-20 point 5); the HD mesh's own glass instead
            foreach ((Vector3 a, Vector3 b, Vector3 c) in glass)
            {
                Add(a, b, c, CullingMode.Neither, isGlass: true);
            }
            return drawn;
        }

        // a room node the game draws: on in the room's layer (FilterNodes) with its parents. An MPH room's single-player
        // door blockers and frames are switched off in multiplayer but still hold triangles, and they roofed the holo
        // doorways once the builders scaled every node (owner 2026-11-20 point 6: no snow through the holo doors)
        internal static bool Drawn(Model model, Node node)
        {
            return node.MeshCount > 0 && node.Enabled && model.NodeParentsEnabled(node)
                && !node.Name.Contains("sky", StringComparison.OrdinalIgnoreCase);
        }

        static string? _glassRoom;
        static List<(Vector3 A, Vector3 B, Vector3 C)> _glass = new();

        // an imported arena's blended (glass) triangles in world units, from its HD mesh (ArenaHdWriter: pass 1); none for
        // an MPH room. Read once per room, shared by the roofs and the ground snow
        internal static List<(Vector3 A, Vector3 B, Vector3 C)> HdGlass(Scene scene)
        {
            string? name = scene.Room?.Meta?.Name;
            if (name == _glassRoom)
            {
                return _glass;
            }
            _glassRoom = name;
            _glass = ReadGlass(name);
            return _glass;
        }

        // the same, read fresh (any thread)
        internal static List<(Vector3 A, Vector3 B, Vector3 C)> ReadGlass(string? name)
        {
            var glass = new List<(Vector3 A, Vector3 B, Vector3 C)>();
            try
            {
                if (name == null || !EchoesArena.IsEchoesRoom(name) || EchoesArena.HdDir(name) is not string dir
                    || !System.IO.File.Exists(System.IO.Path.Combine(dir, "arena.bin")))
                {
                    return glass;
                }
                using var r = new System.IO.BinaryReader(System.IO.File.OpenRead(System.IO.Path.Combine(dir, "arena.bin")));
                if (System.Text.Encoding.ASCII.GetString(r.ReadBytes(8)) != "MPHARN1\0")
                {
                    return glass;
                }
                int floats = r.ReadInt32(), count = r.ReadInt32(), batches = r.ReadInt32();
                var runs = new List<(int Start, int Count)>();
                for (int i = 0; i < batches; i++)
                {
                    int mat = r.ReadInt32(), start = r.ReadInt32(), n = r.ReadInt32(), pass = r.ReadInt32();
                    if (pass == 1) runs.Add((start, n));
                }
                var v = new float[(long)count * floats];
                byte[] bytes = r.ReadBytes(v.Length * 4);
                Buffer.BlockCopy(bytes, 0, v, 0, bytes.Length);
                Vector3 P(int i) => new Vector3(v[i * floats], v[i * floats + 1], v[i * floats + 2]);
                foreach ((int start, int n) in runs)
                {
                    for (int i = start; i + 2 < start + n && i + 2 < count; i += 3)
                    {
                        glass.Add((P(i), P(i + 1), P(i + 2)));
                    }
                }
                Console.WriteLine($"Holiday snow: {name} glass, {glass.Count} triangles from its HD mesh");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Holiday snow: {name} glass unread ({ex.Message})");
            }
            return glass;
        }


        static readonly Dictionary<string, bool> _hdSky = new();

        // whether an imported arena's HD mesh draws a sky (any ArenaHdWriter.SkyPass batch; only the batch table is read),
        // once per arena (any thread). An unreadable mesh counts as drawing one, so the sky map alone decides as before
        internal static bool HdDrawsSky(string name)
        {
            string? path = EchoesArena.HdDir(name) is string dir ? System.IO.Path.Combine(dir, "arena.bin") : null;
            string key = path == null || !System.IO.File.Exists(path) ? name
                : $"{path}|{System.IO.File.GetLastWriteTimeUtc(path).Ticks}";   // a re-import is read again
            lock (_hdSky)
            {
                if (_hdSky.TryGetValue(key, out bool known))
                {
                    return known;
                }
            }
            bool draws = true;
            try
            {
                if (path != null && System.IO.File.Exists(path))
                {
                    using var r = new System.IO.BinaryReader(System.IO.File.OpenRead(path));
                    if (System.Text.Encoding.ASCII.GetString(r.ReadBytes(8)) == "MPHARN1\0")
                    {
                        r.ReadInt32();
                        r.ReadInt32();
                        int batches = r.ReadInt32();
                        draws = false;
                        for (int i = 0; i < batches && !draws; i++)
                        {
                            r.ReadInt32();
                            r.ReadInt32();
                            int n = r.ReadInt32(), pass = r.ReadInt32();
                            draws = pass == ArenaHdWriter.SkyPass && n > 0;
                        }
                        Console.WriteLine($"Holiday snow: {name} {(draws ? "draws a sky" : "draws no sky, indoors")} (its HD mesh)");
                    }
                }
            }
            catch (Exception ex)
            {
                draws = true;
                Console.WriteLine($"Holiday snow: {name} sky unread ({ex.Message})");
            }
            lock (_hdSky)
            {
                _hdSky[key] = draws;
            }
            return draws;
        }

        // a drawn roof anywhere above p (the falling snow's test, for the ground snow and breath)
        static bool RoofAbove(Vector3 p)
        {
            if (_roofs.TryGetValue(((int)MathF.Floor(p.X / CellXZ), (int)MathF.Floor(p.Z / CellXZ)), out var roofs))
            {
                foreach (var (a, b, c) in roofs)
                {
                    if (HitsUp(p, a, b, c))
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        // a ray straight up from p meets triangle abc (either side) within SkyRay
        static bool HitsUp(Vector3 p, Vector3 a, Vector3 b, Vector3 c)
        {
            // barycentric test in XZ, then the height there
            float d = (b.Z - c.Z) * (a.X - c.X) + (c.X - b.X) * (a.Z - c.Z);
            if (MathF.Abs(d) < 1e-6f)
            {
                return false;
            }
            float u = ((b.Z - c.Z) * (p.X - c.X) + (c.X - b.X) * (p.Z - c.Z)) / d;
            float w = ((c.Z - a.Z) * (p.X - c.X) + (a.X - c.X) * (p.Z - c.Z)) / d;
            if (u < 0 || w < 0 || u + w > 1)
            {
                return false;
            }
            float y = u * a.Y + w * b.Y + (1 - u - w) * c.Y;
            return y > p.Y && y < p.Y + SkyRay;
        }
    }
}
