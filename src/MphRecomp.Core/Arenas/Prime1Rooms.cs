using System;
using System.Collections.Generic;
using System.Linq;
using MphRecomp.Import;
using MphRecomp.Import.Retro;
using MphRecomp.PrimeMusic;
using OpenTK.Mathematics;

namespace MphRecomp.Arenas
{
    using Anim = MphRecomp.Import.Retro.Anim;

    // More Prime 1 rooms as multiplayer arenas, built the way the Main Quarry is (MainQuarry.cs; thread fbb8db, owner
    // 2026-11-16; dev-only until the owner signs them off), each one a table of its own script object ids:
    //   Monitor Station (Magmoor Caverns, Metroid6.pak MREA 0c57a641): the three bridges always extended (the "Final"
    //     platforms, solid by their DCLNs; the Upper Bridge Final is inactive in the room, forced on), the round platform
    //     static, the four doors sealed, the crates breakable and respawning, the lava "exactly like MPH lava" (owner): a
    //     solid floor at the lava's surface whose terrain is MPH's Lava (PlayerCollision OnLava: MPH's burn and its Spire
    //     exemption), laid only over cells with room floor under the surface, drawn as a fluid from the room's own textures
    //   Phendrana Shorelines (Phendrana Drifts, Metroid3.pak MREA f7285979): the whole room, the six doors sealed, its
    //     solid platforms (ice block, grating, dais, blockage, the two ledges), swimmable ice water (Echoes movement, as
    //     Pipeline's), the tower's spider track left as scenery (Prime 1 collision never sets the Echoes spider bit:
    //     Mrea1.Prime1Flags; owner: "Ledge unreachable")
    // No enemies, no room pickups; the owner places spawns and pickups with the dev placer (stand-ins until then).
    // Property offsets as MainQuarry.cs lists them; a Water object is name, position, extent (no rotation), then its look
    internal sealed class Prime1Room
    {
        public const string MonitorStationKey = "P1_MonitorStation", ShorelinesKey = "P1_PhendranaShorelines";

        // the rooms' own music: the Prime music import's Magmoor Caverns and Phendrana Drifts themes
        public const string MonitorStationMusic = TrackCatalog.Kind + ":MP1MagmoorCaverns";
        public const string ShorelinesMusic = TrackCatalog.Kind + ":MP1PhendranaDrifts";

        const byte ActorType = 0, DoorType = 3, PlatformType = 8, GeneratorType = 0x0A, DebrisType = 0x45, SpawnPointType = 0x0F, WaterType = 0x20, AreaAttributesType = 0x4E;

        public string Key { get; private init; } = "";
        public string Name { get; private init; } = "";
        // each door, with the actors drawn at it (its shield)
        (uint Door, uint[] With)[] _doors = Array.Empty<(uint, uint[])>();
        // solid: platforms by their DCLN, actors by their own solid flag (their model's bounds); drawn: model only
        uint[] _solid = Array.Empty<uint>(), _drawn = Array.Empty<uint>(), _crates = Array.Empty<uint>();
        // actors made solid by their model's own triangles though their solid flag is off (owner 11-16: the Shorelines'
        // ice mounds, "drawn but not solid"; their collision box is empty, so their shape is the only one there is)
        uint[] _meshSolid = Array.Empty<uint>();
        // the room's Water object: lava (a Lava floor at its surface) or swimmable water, and how it's drawn
        uint _fluid = uint.MaxValue;
        bool _lava;
        ArenaHdWriter.FluidLook? _look;

        // Monitor Station's lava (Water 'lava': ColorMap d6754bb8, PatternMap1 06fb5520, UV layers 30 s x10 / 40 s x20,
        // alpha 0.65 in Prime with its glow on top): drawn near opaque, warm (judged by eye)
        static readonly ArenaHdWriter.FluidLook Lava = new(0xd6754bb8, 10, 30, 0x06fb5520, 20, 40,
            new[] { 1.0f, 0.62f, 0.32f }, new[] { 1.0f, 0.55f, 0.18f }, 0.94f);
        // the Shorelines' ice water (Water 'ice water': ColorMap b2e97927, PatternMap1 a919cf17, alpha 0.65, inside fog
        // (0.443, 0.569, 0.624))
        static readonly ArenaHdWriter.FluidLook IceWater = new(0xb2e97927, 5, 12, 0xa919cf17, 16, 30,
            new[] { 0.443f, 0.569f, 0.624f }, new[] { 0.72f, 0.88f, 0.95f }, 0.65f);

        public static readonly IReadOnlyList<Prime1Room> All = new[]
        {
            new Prime1Room
            {
                Key = MonitorStationKey, Name = "Monitor Station",
                _doors = new[]
                {
                    (0x0009003du, new[] { 0x00090056u }), (0x00090059u, new[] { 0x0009005cu }),
                    (0x0009005fu, new[] { 0x00090062u }), (0x0009009bu, new[] { 0x0009009eu }),
                },
                // the three bridges' Final platforms, the round platform, the four destructible panels (kept whole)
                _solid = new uint[] { 0x00090015, 0x00090006, 0x00090025, 0x00090115, 0x0009006d, 0x0009009a, 0x000900ab, 0x000900b2 },
                // the spinner and the three dippers (not solid in Prime)
                _drawn = new uint[] { 0x00090004, 0x00090009, 0x0009002f, 0x0009000a },
                _crates = new uint[]
                {
                    0x00090169, 0x0009016c, 0x0009016f, 0x00090172, 0x00090192, 0x00090195, 0x00090198, 0x0009019b,
                    0x0009026b, 0x0009026d, 0x0009026f, 0x00090271, 0x000901f5, 0x000901f8,
                },
                _fluid = 0x0009001e, _lava = true, _look = Lava,
            },
            new Prime1Room
            {
                Key = ShorelinesKey, Name = "Phendrana Shorelines",
                _doors = new[]
                {
                    (0x00020037u, new[] { 0x0002003au }), (0x0002003du, new[] { 0x00020040u }), (0x00020043u, new[] { 0x00020048u }),
                    (0x00020019u, new[] { 0x00020026u }), (0x00020239u, new[] { 0x0002023cu }), (0x0002030cu, new[] { 0x0002030fu }),
                },
                // the two ledge platforms, the grating, the dais, the blockage, the ice block; the first-visit ice mounds
                // (layer 1 "Before"; their solid flag is off, so solid by their model's triangles: _meshSolid)
                _solid = new uint[] { 0x000200f1, 0x000200f2, 0x000200f5, 0x00020110, 0x00020113, 0x00020336, 0x040200f7, 0x04020101 },
                _meshSolid = new uint[] { 0x040200f7, 0x04020101 },
                _fluid = 0x00020047, _look = IceWater,
            },
        };

        public static Prime1Room? Find(string key) => All.FirstOrDefault(r => r.Key == key);

        // ---- object reading (as MainQuarry's) ----

        static Scly1.Obj? Obj(List<Scly1.Obj> objs, uint id) => objs.FirstOrDefault(o => o.Id == id);

        static int Start(Scly1.Obj o)
        {
            Be.CString(o.Props, 0, out int end);
            return end + 1;
        }

        static uint U32At(Scly1.Obj o, int off) => Start(o) + off + 4 <= o.Props.Length ? Be.U32(o.Props, Start(o) + off) : uint.MaxValue;

        static Vector3 Vec(Scly1.Obj o, int at) => new(Be.F32(o.Props, at), Be.F32(o.Props, at + 4), Be.F32(o.Props, at + 8));

        static float[] Xf(Scly1.Obj o)
        {
            int r = Start(o);
            var x = new float[9];
            for (int i = 0; i < 9; i++) x[i] = Be.F32(o.Props, r + 4 * i);
            return x;
        }

        static uint ModelOf(Scly1.Obj o) => o.Type switch { ActorType => U32At(o, 196), PlatformType => U32At(o, 60), _ => uint.MaxValue };

        static uint DclnOf(Scly1.Obj o) => o.Type == PlatformType ? U32At(o, 206) : uint.MaxValue;

        static (Vector3 Lo, Vector3 Hi) Bounds(Cmdl g)
        {
            var lo = new Vector3(float.MaxValue);
            var hi = new Vector3(float.MinValue);
            for (int i = 0; i + 2 < g.Positions.Length; i += 3)
            {
                var p = new Vector3(g.Positions[i], g.Positions[i + 1], g.Positions[i + 2]);
                lo = Vector3.ComponentMin(lo, p);
                hi = Vector3.ComponentMax(hi, p);
            }
            return (lo, hi);
        }

        static void Box(float[] xf, Vector3 lo, Vector3 hi, Func<float, float, float, Vector3> map, Action<Vector3, Vector3, Vector3, Vector3> tri)
        {
            Vector3 M(Vector3 e) => map(e.X, e.Y, e.Z);
            Vector3 C(int k) => M(ArenaHdWriter.Place(xf, new Vector3((k & 1) != 0 ? hi.X : lo.X, (k & 2) != 0 ? hi.Y : lo.Y, (k & 4) != 0 ? hi.Z : lo.Z)));
            Vector3 mid = M(ArenaHdWriter.Place(xf, (lo + hi) / 2));
            int[][] faces = { new[] { 0, 2, 6, 4 }, new[] { 1, 5, 7, 3 }, new[] { 0, 4, 5, 1 }, new[] { 2, 3, 7, 6 }, new[] { 0, 1, 3, 2 }, new[] { 4, 6, 7, 5 } };
            foreach (int[] f in faces)
            {
                tri(C(f[0]), C(f[1]), C(f[2]), mid);
                tri(C(f[0]), C(f[2]), C(f[3]), mid);
            }
        }

        // an actor's model's own triangles (placed), each facing away from the model's middle
        static bool MeshShape(Pak pak, Scly1.Obj o, Func<float, float, float, Vector3> map, Action<Vector3, Vector3, Vector3, Vector3> tri)
        {
            uint model = ModelOf(o);
            if (!pak.Contains(model)) return false;
            Cmdl g = Cmdl.Load(pak.Get, model);
            if (g.Triangles.Length < 3) return false;
            float[] xf = Xf(o);
            Vector3 P(int i) { Vector3 e = ArenaHdWriter.Place(xf, new Vector3(g.Positions[3 * i], g.Positions[3 * i + 1], g.Positions[3 * i + 2])); return map(e.X, e.Y, e.Z); }
            (Vector3 lo, Vector3 hi) = Bounds(g);
            // the middle a little under the model's base, so a low hump's faces all point up and out
            Vector3 c = (lo + hi) / 2;
            c.Z = lo.Z - (hi.Z - lo.Z);
            Vector3 e0 = ArenaHdWriter.Place(xf, c), mid = map(e0.X, e0.Y, e0.Z);
            for (int k = 0; k + 2 < g.Triangles.Length; k += 3) tri(P(g.Triangles[k].P), P(g.Triangles[k + 1].P), P(g.Triangles[k + 2].P), mid);
            return true;
        }

        static bool Shape(Pak pak, Scly1.Obj o, Func<float, float, float, Vector3> map, Action<Vector3, Vector3, Vector3, Vector3> tri)
        {
            uint mesh = DclnOf(o);
            if (mesh != uint.MaxValue)
            {
                if (!pak.Contains(mesh) || EchoesTurret.ReadDcln(pak.Get(mesh), prime1: true) is not (float[] v, int[] t)) return false;
                float[] xf = Xf(o);
                xf[6] = xf[7] = xf[8] = 1;
                Vector3 P(int i) { Vector3 e = ArenaHdWriter.Place(xf, new Vector3(v[3 * i], v[3 * i + 1], v[3 * i + 2])); return map(e.X, e.Y, e.Z); }
                Vector3 mid = Vector3.Zero;
                for (int i = 0; i < v.Length / 3; i++) mid += P(i);
                mid /= Math.Max(1, v.Length / 3);
                for (int k = 0; k + 2 < t.Length; k += 3) tri(P(t[k]), P(t[k + 1]), P(t[k + 2]), mid);
                return true;
            }
            uint model = ModelOf(o);
            if (!pak.Contains(model)) return false;
            (Vector3 lo, Vector3 hi) = Bounds(Cmdl.Load(pak.Get, model));
            Box(Xf(o), lo, hi, map, tri);
            return true;
        }

        // ---- the sky (as MainQuarry.Sky) ----

        public uint Sky(Pak pak, List<Scly1.Obj> objs, List<string> notes)
        {
            Scly1.Obj? attrs = objs.FirstOrDefault(o => o.Type == AreaAttributesType);
            if (attrs == null || attrs.Props.Length < 29) { notes.Add($"{Name}: no AreaAttributes; no sky"); return uint.MaxValue; }
            bool show = attrs.Props[4] != 0;
            uint own = Be.U32(attrs.Props, 25);
            if (!show) { notes.Add($"{Name}: AreaAttributes shows no sky"); return uint.MaxValue; }
            if (pak.Contains(own)) { notes.Add($"{Name}: sky {own:x8} (the room's AreaAttributes)"); return own; }
            foreach (uint id in pak.Order)
            {
                if (pak.TypeOf(id) != "MLVL") continue;
                byte[] w = pak.Get(id);
                uint world = w.Length >= 20 ? Be.U32(w, 16) : uint.MaxValue;
                notes.Add($"{Name}: sky {world:x8} (the world's default, MLVL {id:x8}){(pak.Contains(world) ? "" : " NOT in the pak")}");
                return pak.Contains(world) ? world : uint.MaxValue;
            }
            notes.Add($"{Name}: no MLVL in the pak; no sky");
            return uint.MaxValue;
        }

        // ---- collision ----

        // the room's fluid box (Prime metres), or null
        (Vector3 Lo, Vector3 Hi)? FluidBox(List<Scly1.Obj> objs)
        {
            if (Obj(objs, _fluid) is not { Type: WaterType } w || w.Props.Length < Start(w) + 24) return null;
            Vector3 p = Vec(w, Start(w)), ext = Vec(w, Start(w) + 12);
            return (p - ext / 2, p + ext / 2);
        }

        // the lava's floor (MPH units: its box and surface) once Seals has laid it, for the stand-in spawns to keep off
        (Vector3 Min, Vector3 Max)? _lavaBox;

        // and for the bots' nav: no floor samples on the lava (they walked onto it and burned there, never fighting)
        public IReadOnlyList<(Vector3 Min, Vector3 Max)>? NoBots => _lavaBox is { } b ? new[] { (b.Min, b.Max + Vector3.UnitY * 0.1f) } : null;

        // the doors' seals, the solid platforms and actors, and the lava floor: each as triangles facing out. area: the
        // room's own collision (where the lava has floor under it). lava: a triangle of Lava terrain
        public void Seals(Pak pak, List<Scly1.Obj> objs, Func<float, float, float, Vector3> map, ArenaRaycaster area,
            Action<Vector3, Vector3, Vector3, Mrea.Surface> solid, Action<Vector3, Vector3, Vector3> lava, List<string> notes)
        {
            int count = 0;
            Action<Vector3, Vector3, Vector3, Vector3> Tri(Mrea.Surface s) => (a, b, c, inside) =>
            {
                Vector3 n = Vector3.Cross(b - a, c - a);
                if (n.LengthSquared < 1e-10f) return;
                if (Vector3.Dot(n, (a + b + c) / 3 - inside) < 0) (b, c) = (c, b);
                solid(a, b, c, s);
                count++;
            };
            foreach ((uint d, _) in _doors)
            {
                if (Obj(objs, d) is not { Type: DoorType } o || o.Props.Length < Start(o) + 64)
                {
                    notes.Add($"{Name}: door {d:x8} not found; left open");
                    continue;
                }
                float[] x = Xf(o);
                int b = o.Props.Length - 32;
                Vector3 size = Vec(o, b), off = Vec(o, b + 12);
                Box(new[] { x[0], x[1], x[2], 0f, 0f, 0f, 1f, 1f, 1f }, off - size / 2, off + size / 2, map, Tri(Mrea.Surface.Metal));
            }
            int doors = count;
            var solidOnes = new List<string>();
            foreach (uint id in _solid)
            {
                if (Obj(objs, id) is not { } o) { notes.Add($"{Name}: {id:x8} not found"); continue; }
                if (o.Type == ActorType && _meshSolid.Contains(id))
                {
                    if (MeshShape(pak, o, map, Tri(Mrea.Surface.Snow))) solidOnes.Add($"{o.Name} {id:x8} (its model)");
                    else notes.Add($"{Name}: {o.Name} {id:x8} model not read; not solid");
                    continue;
                }
                if (o.Type == ActorType && o.Props[^15] == 0) continue;
                if (o.Type is not (ActorType or PlatformType)) continue;
                if (Shape(pak, o, map, Tri(o.Type == PlatformType && o.Name.Contains("Ice") ? Mrea.Surface.Ice : Mrea.Surface.Metal))) solidOnes.Add($"{o.Name} {id:x8}");
                else notes.Add($"{Name}: {o.Name} {id:x8} collision not read; not solid");
            }
            notes.Add($"{Name}: {doors} door seal triangles, {count - doors} platform/scenery triangles ({string.Join(", ", solidOnes)})");
            if (_lava && FluidBox(objs) is ({ } lo, { } hi)) LavaFloor(lo, hi, map, area, lava, notes);
        }

        // MPH lava is a floor with Lava terrain: the lava's surface, in 2 m cells over the room's own floor under it (a cell
        // whose middle has walkable floor within the lava's depth plus 4 m below the surface), each row's runs one strip
        void LavaFloor(Vector3 lo, Vector3 hi, Func<float, float, float, Vector3> map, ArenaRaycaster area,
            Action<Vector3, Vector3, Vector3> lava, List<string> notes)
        {
            const float Step = 2;
            int nx = (int)MathF.Ceiling((hi.X - lo.X) / Step), ny = (int)MathF.Ceiling((hi.Y - lo.Y) / Step);
            Vector3 down = map(0, 0, 1).Normalized();
            float drop = (map(0, 0, hi.Z - lo.Z + 4) - map(0, 0, 0)).Length;
            int cells = 0, tris = 0;
            void Quad(float x0, float y0, float x1, float y1)
            {
                Vector3 a = map(x0, y0, hi.Z), b = map(x1, y0, hi.Z), c = map(x1, y1, hi.Z), d = map(x0, y1, hi.Z);
                foreach ((Vector3 p, Vector3 q, Vector3 r) in new[] { (a, b, c), (a, c, d) })
                {
                    // facing up (MPH +Y)
                    if (Vector3.Cross(q - p, r - p).Y < 0) lava(p, r, q);
                    else lava(p, q, r);
                    tris++;
                }
            }
            for (int j = 0; j < ny; j++)
            {
                float y0 = lo.Y + j * Step, y1 = MathF.Min(hi.Y, y0 + Step);
                int run = -1;
                for (int i = 0; i <= nx; i++)
                {
                    bool on = false;
                    if (i < nx)
                    {
                        float x0 = lo.X + i * Step, x1 = MathF.Min(hi.X, x0 + Step);
                        Vector3 p = map((x0 + x1) / 2, (y0 + y1) / 2, hi.Z) + down * 0.02f;
                        on = area.FloorBelow(p, drop) != null;
                        if (on) cells++;
                    }
                    if (on && run < 0) run = i;
                    else if (!on && run >= 0)
                    {
                        Quad(lo.X + run * Step, y0, MathF.Min(hi.X, lo.X + i * Step), y1);
                        run = -1;
                    }
                }
            }
            Vector3 m0 = map(lo.X, lo.Y, hi.Z), m1 = map(hi.X, hi.Y, hi.Z);
            _lavaBox = (Vector3.ComponentMin(m0, m1), Vector3.ComponentMax(m0, m1));
            notes.Add($"{Name}: lava floor (MPH Lava terrain) at {m0.Y:0.00}: {cells} of {nx * ny} 2 m cells, {tris} triangles");
        }

        // ---- fluids ----

        // the room's fluid drawn (its whole box's surface, as Prime draws it); swimmable water is also a "water" FluidSpot
        // (Echoes movement underwater, the bots' water). Lava burns through its floor's terrain (Seals), not a FluidSpot
        public void Fluids(List<Scly1.Obj> objs, Func<float, float, float, Vector3> map, EchoesArena.Info info, List<ArenaHdWriter.Fluid> fluids)
        {
            if (FluidBox(objs) is not ({ } lo, { } hi) || _look == null) return;
            fluids.Add(new ArenaHdWriter.Fluid(new[] { lo.X, lo.Y, lo.Z }, new[] { hi.X, hi.Y, hi.Z }, hi.Z, _look));
            if (_lava) { info.Notes.Add($"{Name}: lava drawn over {hi.X - lo.X:0}x{hi.Y - lo.Y:0} m"); return; }
            Vector3 a = map(lo.X, lo.Y, lo.Z), b = map(hi.X, hi.Y, hi.Z);
            Vector3 min = Vector3.ComponentMin(a, b), max = Vector3.ComponentMax(a, b);
            static float R(float v) => MathF.Round(v, 4);
            info.Fluids ??= new();
            info.Fluids.Add(new EchoesArena.FluidSpot { Kind = "water", Min = new[] { R(min.X), R(min.Y), R(min.Z) }, Max = new[] { R(max.X), R(max.Y), R(max.Z) }, Surface = R(max.Y) });
        }

        // ---- the crates (as MainQuarry.Crates) ----

        public void Crates(Pak pak, List<Scly1.Obj> objs, Func<float, float, float, Vector3> map, ArenaRaycaster ray,
            IReadOnlyList<Mrea.Light> lights, EchoesArena.Info info, List<ArenaHdWriter.Placed> placed)
        {
            if (_crates.Length == 0) return;
            info.SandCrates = new();
            int NextPass() => placed.Count == 0 ? ArenaHdWriter.FirstPlacedPass : Math.Max(ArenaHdWriter.FirstPlacedPass, placed.Max(p => p.Pass) + 1);
            float[] identity = MiningPlazaCrates.F(Matrix4.Identity);
            var debrisFor = new Dictionary<uint, (List<TurretFx> Pieces, int Count, int Slots)>();
            var debrisPasses = new Dictionary<uint, int>();
            // the crate's Dead -> its Generator ("Chunk Generator": SpawnCount picks of its DebrisExtended followers)
            (List<TurretFx> Pieces, int Count, int Slots) Debris(Scly1.Obj crate)
            {
                Scly1.Obj? genr = crate.Connections.Select(c => Obj(objs, c.Target)).FirstOrDefault(g => g?.Type == GeneratorType);
                if (genr == null) return (new List<TurretFx>(), 0, 0);
                if (debrisFor.TryGetValue(genr.Id, out var had)) return had;
                var pieces = new List<TurretFx>();
                List<Scly1.Obj> followers = genr.Connections.Select(c => Obj(objs, c.Target)).OfType<Scly1.Obj>().Where(f => f.Type == DebrisType).ToList();
                foreach (Scly1.Obj f in followers)
                {
                    if (PieceOf(f) is not { } d || !pak.Contains(d.Id) || pak.TypeOf(d.Id) != "CMDL") continue;
                    pieces.Add(d);
                    if (!debrisPasses.ContainsKey(d.Id))
                    {
                        debrisPasses[d.Id] = NextPass();
                        placed.Add(new ArenaHdWriter.Placed(d.Id, new float[] { 0, 0, 0, 0, 0, 0, 1, 1, 1 }, debrisPasses[d.Id]));
                    }
                }
                int count = (int)U32At(genr, 0);   // SpawnCount
                return debrisFor[genr.Id] = (pieces, count is > 0 and < 256 ? count : pieces.Count, followers.Count);
            }
            foreach (uint id in _crates)
            {
                if (Obj(objs, id) is not { } o || !pak.Contains(ModelOf(o))) { info.Notes.Add($"{Name}: crate {id:x8} not found"); continue; }
                float[] xf = Xf(o);
                uint model = ModelOf(o);
                (Vector3 mlo, Vector3 mhi) = Bounds(Cmdl.Load(pak.Get, model));
                var lo = new Vector3(float.MaxValue);
                var hi = new Vector3(float.MinValue);
                for (int c = 0; c < 8; c++)
                {
                    Vector3 w = ArenaHdWriter.Place(xf, new Vector3((c & 1) != 0 ? mhi.X : mlo.X, (c & 2) != 0 ? mhi.Y : mlo.Y, (c & 4) != 0 ? mhi.Z : mlo.Z));
                    Vector3 m = map(w.X, w.Y, w.Z);
                    lo = Vector3.ComponentMin(lo, m);
                    hi = Vector3.ComponentMax(hi, m);
                }
                Vector3 center = (lo + hi) / 2, origin = map(xf[0], xf[1], xf[2]);
                int pass = NextPass();
                placed.Add(new ArenaHdWriter.Placed(model, xf, pass));
                var spot = new MiningPlazaCrates.CrateSpot
                {
                    Id = id, Ready = new[] { pass }, Fps = 1, Delta = identity,
                    Origin = new[] { origin.X, origin.Y, origin.Z }, Center = new[] { center.X, center.Y, center.Z },
                    Radius = MathF.Max(hi.X - lo.X, hi.Z - lo.Z) / 2, Top = hi.Y,
                };
                (spot.Ambient, spot.Lights) = EchoesCrates.Lighting(lights, ArenaHdWriter.Place(xf, (mlo + mhi) / 2), map, ray);
                var f = new List<float>();
                if (Shape(pak, o, map, (a, b, c, inside) =>
                    {
                        Vector3 n = Vector3.Cross(b - a, c - a);
                        if (n.LengthSquared < 1e-10f) return;
                        if (Vector3.Dot(n, (a + b + c) / 3 - inside) < 0) (b, c) = (c, b);
                        f.AddRange(new[] { a.X, a.Y, a.Z, b.X, b.Y, b.Z, c.X, c.Y, c.Z });
                    })) spot.Tris = f.ToArray();
                else info.Notes.Add($"{Name}: crate {o.Name} {id:x8} collision not read; not solid");
                var deb = Debris(o);
                spot.Debris = deb.Pieces;
                spot.DebrisCount = deb.Count;
                spot.DebrisSlots = deb.Slots;
                spot.DebrisPasses = deb.Pieces.Select(d => d.Id).Distinct().ToDictionary(m => m, m => debrisPasses[m]);
                info.SandCrates.Add(spot);
            }
            info.Notes.Add($"{Name}: {info.SandCrates.Count} breakable crates (respawn {MiningPlazaCrates.RegrowFrames / 60} s), collision "
                + string.Join("/", info.SandCrates.Select(c => c.Tris.Length / 9)) + " triangles, debris "
                + string.Join("/", info.SandCrates.Select(c => $"{c.DebrisCount} of {c.Debris.Count}")) + " pieces");
            info.Notes.Add(MainQuarry.Stacks(objs, info.SandCrates, Name));
        }

        // a Prime 1 DebrisExtended as a "debris" effect (as EchoesTurret.GrateDebris for Echoes'): after the name its
        // transform (9 floats), LinConeAngle, LinMin/MaxMag, AngMin/MaxMag, Min/MaxDuration, ColorInT, ColorOutT (36),
        // Color (72), EndsColor (88), ScaleOutT (104), EndScale (108), Restitution (120), DownwardSpeed (124),
        // LocalOffset (128), Model (140). Its cone is about its up axis (CScriptDebris's extended mode).
        internal static TurretFx? PieceOf(Scly1.Obj o)
        {
            int r = Start(o);
            if (r + 144 > o.Props.Length) return null;
            float F(int off) => Be.F32(o.Props, r + off);
            static float R(float v) => MathF.Round(v, 5);
            uint model = U32At(o, 140);
            if (model is 0 or UInt32.MaxValue) return null;
            float[] x = Xf(o);
            Vector3 Ax(Vector3 e)
            {
                Vector3 w = ArenaHdWriter.Place(x, e) - ArenaHdWriter.Place(x, Vector3.Zero);
                return new Vector3(w.X, w.Z, -w.Y);
            }
            Vector3 ex = Ax(Vector3.UnitX), ey = Ax(Vector3.UnitY), ez = Ax(Vector3.UnitZ);
            float[] d = new float[EchoesTurret.DebrisLength];
            d[EchoesTurret.DebrisCone] = F(36);
            d[EchoesTurret.DebrisDir + 2] = 1;
            d[EchoesTurret.DebrisSpeed] = F(40); d[EchoesTurret.DebrisSpeed + 1] = F(44);
            d[EchoesTurret.DebrisSpin] = F(48); d[EchoesTurret.DebrisSpin + 1] = F(52);
            d[EchoesTurret.DebrisLife] = F(56); d[EchoesTurret.DebrisLife + 1] = F(60);
            d[EchoesTurret.DebrisFadeIn] = F(64) / 100; d[EchoesTurret.DebrisFadeOut] = F(68) / 100;
            for (int i = 0; i < 4; i++) d[EchoesTurret.DebrisEndColor + i] = F(88 + 4 * i);
            d[EchoesTurret.DebrisScaleOut] = F(104) / 100;
            for (int i = 0; i < 3; i++) d[EchoesTurret.DebrisEndScale + i] = F(108 + 4 * i);
            d[EchoesTurret.DebrisGravity] = F(124);
            return new TurretFx
            {
                Role = "crate", Kind = "debris", Id = model,
                At = new[] { R(ex.X), R(ex.Y), R(ex.Z), R(ey.X), R(ey.Y), R(ey.Z), R(ez.X), R(ez.Y), R(ez.Z), 0, 0, 0 },
                Debris = d.Select(R).ToArray(),
            };
        }

        // ---- the look (as MainQuarry.Looks) ----

        public void Looks(Pak pak, List<Scly1.Obj> objs, Func<float, float, float, Vector3> map, ArenaRaycaster ray,
            IReadOnlyList<Mrea.Light> lights, EchoesArena.Info info, List<ArenaHdWriter.Placed> placed)
        {
            var drawn = new List<string>();
            void Add(string name, ArenaHdWriter.Placed pl, Vector3 middle)
            {
                info.Statics ??= new();
                if (info.Statics.Any(st => st.Name == name)) return;
                int pass = ArenaHdWriter.FirstPlacedPass + (info.Breakables?.Count ?? 0) + info.Statics.Count;
                (float[] amb, List<float[]> lit) = EchoesCrates.Lighting(lights, middle, map, ray);
                info.Statics.Add(new EchoesArena.StaticSpot { Name = name, Pass = pass, Ambient = amb, Lights = lit });
                placed.Add(pl with { Pass = pass });
                drawn.Add(name);
            }
            void Model(float[] xf, uint model, string name)
            {
                if (!pak.Contains(model)) return;
                (Vector3 lo, Vector3 hi) = Bounds(Cmdl.Load(pak.Get, model));
                Add(name, new ArenaHdWriter.Placed(model, xf, 0), ArenaHdWriter.Place(xf, (lo + hi) / 2));
            }
            void Door(Scly1.Obj o)
            {
                float[] xf = Xf(o);
                string name = $"{o.Name} {o.Id:x8}";
                uint ancsId = U32At(o, 36);
                int character = (int)U32At(o, 40), clip = (int)U32At(o, 44);
                if (!pak.Contains(ancsId)) { info.Notes.Add($"{Name}: {name} animation set {ancsId:x8} not in the pak"); return; }
                Ancs ancs = Ancs.Parse(pak.Get(ancsId));
                if (character < 0 || character >= ancs.Characters.Count) return;
                Ancs.Character ch = ancs.Characters[character];
                if (!pak.Contains(ch.Cmdl)) return;
                (Vector3 lo, Vector3 hi) = Bounds(Cmdl.Load(pak.Get, ch.Cmdl));
                Vector3 middle = ArenaHdWriter.Place(xf, (lo + hi) / 2);
                try
                {
                    if (ancs.Set != null && clip >= 0 && clip < ancs.Set.Anims.Count && pak.Contains(ch.Cinf))
                    {
                        (_, Ancs.MetaAnim meta) = ancs.Set.Anims[clip];
                        uint animId = meta.Kind == "Play" || meta.Children.Count == 0 ? meta.AnimId : Ancs.Prims(meta).FirstOrDefault()?.AnimId ?? uint.MaxValue;
                        if (pak.Contains(animId))
                        {
                            Cinf sk = Cinf.Load(pak.Get(ch.Cinf));
                            Anim anim = Anim.Load(pak.Get(animId), echoes: false);
                            var pose = new Dictionary<uint, Matrix4>();
                            foreach ((uint bone, (double[] q, double[] p)) in EchoesTurret.Pose(sk, anim, 0))
                            {
                                Cinf.Bone bn = sk.Bones[bone];
                                pose[bone] = Matrix4.CreateTranslation(-(float)bn.X, -(float)bn.Y, -(float)bn.Z)
                                    * Matrix4.CreateFromQuaternion(new Quaternion((float)q[0], (float)q[1], (float)q[2], (float)q[3]))
                                    * Matrix4.CreateTranslation((float)p[0], (float)p[1], (float)p[2]);
                            }
                            Add(name, new ArenaHdWriter.Placed(ch.Cmdl, xf, 0) { Skin = ch.Cskr, Pose = pose }, middle);
                            return;
                        }
                    }
                }
                catch (Exception e) when (e is InvalidOperationException or System.IO.InvalidDataException or IndexOutOfRangeException
                    or ArgumentException or KeyNotFoundException)
                {
                    info.Notes.Add($"{Name}: {name} clip {clip} not posed ({e.Message}); drawn in its bind pose");
                }
                Add(name, new ArenaHdWriter.Placed(ch.Cmdl, xf, 0), middle);
            }
            foreach ((uint d, uint[] with) in _doors)
            {
                if (Obj(objs, d) is not { Type: DoorType } door) continue;
                Door(door);
                foreach (uint a in with)
                {
                    if (Obj(objs, a) is { Type: ActorType } o) Model(Xf(o), ModelOf(o), $"{o.Name} {o.Id:x8}");
                }
            }
            foreach (uint id in _solid.Concat(_drawn))
            {
                if (Obj(objs, id) is { } o) Model(Xf(o), ModelOf(o), $"{o.Name} {o.Id:x8}");
            }
            info.Notes.Add($"{Name}: {drawn.Count} models drawn: {string.Join(", ", drawn)}");
        }

        // ---- spawns (as MainQuarry.StandInSpawns, kept off the lava) ----

        public List<(Vector3 Pos, Vector3 Facing)> StandInSpawns(List<Scly1.Obj> objs, Func<float, float, float, Vector3> map, ArenaRaycaster ray)
        {
            var res = new List<(Vector3, Vector3)>();
            void Try(Vector3 p, Vector3 facing)
            {
                if (res.Count >= 8 || ray.FloorBelow(p + Vector3.UnitY * 0.3f, 6f) is not Vector3 f) return;
                if (_lavaBox is ({ } lmin, { } lmax) && f.X >= lmin.X && f.X <= lmax.X && f.Z >= lmin.Z && f.Z <= lmax.Z
                    && MathF.Abs(f.Y - lmin.Y) < 0.05f) return;
                if (res.Any(r => (r.Item1 - f).LengthSquared < 4)) return;
                facing.Y = 0;
                res.Add((f, facing.LengthSquared > 0 ? facing.Normalized() : Vector3.UnitZ));
            }
            Vector3 At(float[] xf, Vector3 local) { Vector3 e = ArenaHdWriter.Place(xf, local); return map(e.X, e.Y, e.Z); }
            foreach (Scly1.Obj o in objs.Where(o => o.Type == SpawnPointType))
            {
                float[] x = Xf(o);
                float yaw = MathHelper.DegreesToRadians(x[5]);
                Try(map(x[0], x[1], x[2]), map(-MathF.Sin(yaw), MathF.Cos(yaw), 0));
            }
            foreach (float step in new[] { 4f, 9f })
            {
                foreach ((uint d, _) in _doors)
                {
                    if (Obj(objs, d) is not { } o) continue;
                    float[] x = Xf(o);
                    x[6] = x[7] = x[8] = 1;
                    foreach (float side in new[] { -1f, 1f })
                    {
                        Vector3 a = At(x, new Vector3(side * step, 0, 1)), b = At(x, new Vector3(side * (step + 1), 0, 1));
                        Try(a, b - a);
                    }
                }
            }
            foreach (uint id in _crates.Concat(_solid))
            {
                if (Obj(objs, id) is not { } o) continue;
                float[] x = Xf(o);
                foreach (Vector3 off in new[] { new Vector3(4, 0, 1), new Vector3(-4, 0, 1), new Vector3(0, 4, 1), new Vector3(0, -4, 1) })
                {
                    Try(map(x[0] + off.X, x[1] + off.Y, x[2] + off.Z), map(-off.X, -off.Y, 0));
                }
            }
            return res;
        }
    }
}
