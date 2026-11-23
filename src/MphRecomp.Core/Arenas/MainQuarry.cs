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

    // Main Quarry (Prime 1's Phazon Mines room, metroid5.pak MREA 643d038f) as a multiplayer arena, stage 1 (thread
    // 57ec2f, owner 2026-11-15; dev-only until the owner signs it off). A story room like Mining Plaza (MiningPlaza.cs),
    // read from Prime 1's MREA v0xF (Mrea1.cs, scripts by position: Scly1) and kept as the owner picked it:
    //   the four doors sealed: the door shell in its own AnimParams clip, its key and shield actors at the door's
    //     transform, solid by the door's collision box (world-aligned, size + offset as the door's last properties)
    //   the crane moving (owner 2026-11-16): a shot on the upper booth's scan panel swings it, the first swing breaking
    //     the wall (MainQuarryCrane.cs: drawn as the animated crane's three parts in Prime's clips; no spider track: the CraneStop/
    //     waypoints are left out)
    //   the crates breakable (stage 2, owner 2026-11-16): Mining Plaza's crate entities (Crates), solid by their DCLNs
    //     (the two without one by their model's bounds), dropping health / missiles / UA and back after 30 s
    //   the room's other default-layer scenery (grating, scan panels) and the first-visit turret platforms (layer 2,
    //     "1st Pass Enemies": the platforms only, no turrets), solid by their model's bounds as Prime's solid actors are
    // No enemies, turrets, force field, thermal wire puzzle or room pickups. The room's one SpawnPoint is Samus's story
    // spot: the owner places the eight spawns and the pickups with the dev placer, so the import leaves stand-ins.
    // Prime 1 property offsets (from the object's name on; Metaforce's loaders' order, checked on the disc):
    //   every object: name, transform (position, rotation degrees, scale) as the 9 floats after it
    //   Actor: model (CMDL) 196, solid flag len-15, active len-13
    //   Platform: model 60, active 205, collision (DCLN) 206
    //   Door: animation set (ANCS, character, clip) 36, collision box size + offset at len-32
    internal static partial class MainQuarry
    {
        public const string Key = "P1_MainQuarry";

        // the room's own music (Mines Main, the room's StreamedAudio): the Prime music import's Phazon Mines track
        public const string MusicKey = TrackCatalog.Kind + ":MP1PhazonMines";

        const byte ActorType = 0, DoorType = 3, PlatformType = 8, GeneratorType = 0x0A, SpawnPointType = 0x0F, DebrisType = 0x45;

        // each door with its key and shield actors (at the door's transform)
        static readonly (uint Door, uint Key, uint Shield)[] Doors =
        {
            (0x00020181, 0x0002017e, 0x00020184), (0x0002026b, 0x00020268, 0x0002026e),
            (0x00020275, 0x00020272, 0x00020278), (0x0002027f, 0x0002027c, 0x00020282),
        };
        // the crane (Platform - Crane Right, at rest), the wall whole and broken, and the Control Tower booth's console scan
        // (POI "Crane Active.scan", Booth Action -> Rotate Crane) that swings it: the console is the room's own geometry
        // (owner 2026-11-20: not the Scan Panel actor 0002052f by the hex door)
        // the crane's sounds, the wall's dust and pieces (MainQuarryCrane.cs)
        const uint TurnSound = 0x000202bc, StopSound = 0x000202b9, WallSound = 0x000202e8;
        static readonly uint[] Dust = { 0x0002004c, 0x00020051 };
        static readonly uint[] WallPieces = { 0x00020029, 0x0002002a, 0x0002002b, 0x0002002c, 0x0002004b };
        const uint CraneRight = 0x00020013, CraneAnimated = 0x00020020, Wall = 0x00020017, BrokenWall = 0x00020018, BoothScan = 0x00020251, ExplodingCrate = 0x000202c9;
        static readonly uint[] CrateIds =
        {
            0x00020103, 0x00020106, 0x00020109, 0x0002010c, 0x0002010f, 0x000201ad, 0x000201b0, 0x000201b3, 0x000201b6,
            0x000201b9, 0x000201bc, 0x00020215, ExplodingCrate,
        };
        // Actor_Grating, the three scan panels, the two turret platforms
        static readonly uint[] Scenery = { 0x000202b6, 0x0002025e, 0x00020266, 0x0002052f, 0x080200e8, 0x080200e9 };

        static Scly1.Obj? Obj(List<Scly1.Obj> objs, uint id) => objs.FirstOrDefault(o => o.Id == id);

        // Prime 1's stacked crates: a lower crate's "on Dead (0xE) send Deactivate (0x4)" to the crate on it, so the upper one
        // breaks with it instead of floating (owner 2026-11-20). Fills CrateSpot.Above; returns a note.
        internal static string Stacks(List<Scly1.Obj> objs, List<MiningPlazaCrates.CrateSpot> spots, string name)
        {
            var ids = spots.Select(s => s.Id).ToHashSet();
            var pairs = new List<string>();
            foreach (MiningPlazaCrates.CrateSpot s in spots)
            {
                if (Obj(objs, s.Id) is not { } o) continue;
                uint[] above = o.Connections.Where(c => c.State == 0xE && c.Message == 0x4 && c.Target != s.Id && ids.Contains(c.Target))
                    .Select(c => c.Target).Distinct().ToArray();
                if (above.Length == 0) continue;
                s.Above = above;
                pairs.AddRange(above.Select(a => $"{s.Id:x8}>{a:x8}"));
            }
            return $"{name}: {pairs.Count} stacked crates (break with the one below): {(pairs.Count == 0 ? "none" : string.Join(", ", pairs))}";
        }

        // the first property byte after the name
        static int Start(Scly1.Obj o)
        {
            Be.CString(o.Props, 0, out int end);
            return end + 1;
        }

        static uint U32At(Scly1.Obj o, int off) => Start(o) + off + 4 <= o.Props.Length ? Be.U32(o.Props, Start(o) + off) : uint.MaxValue;

        static Vector3 Vec(Scly1.Obj o, int at) => new(Be.F32(o.Props, at), Be.F32(o.Props, at + 4), Be.F32(o.Props, at + 8));

        // position, rotation (degrees), scale: as Mrea.ScriptObject.Transform
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

        // ---- the sky ----

        const byte AreaAttributesType = 0x4E;

        // the room's sky model (owner 2026-11-16: "add the missing skybox"): its AreaAttributes' own sky when it shows one
        // (no name; load u32, show sky bool, effect u32, four floats, then the sky CMDL), else the world's default sky (the
        // pak's MLVL: magic, version, name STRG, save SAVW, then the sky CMDL)
        public static uint Sky(Pak pak, List<Scly1.Obj> objs, List<string> notes)
        {
            Scly1.Obj? attrs = objs.FirstOrDefault(o => o.Type == AreaAttributesType);
            if (attrs == null || attrs.Props.Length < 29) { notes.Add("Main Quarry: no AreaAttributes; no sky"); return uint.MaxValue; }
            bool show = attrs.Props[4] != 0;
            uint own = Be.U32(attrs.Props, 25);
            if (!show) { notes.Add("Main Quarry: AreaAttributes shows no sky"); return uint.MaxValue; }
            if (pak.Contains(own)) { notes.Add($"Main Quarry: sky {own:x8} (the room's AreaAttributes)"); return own; }
            foreach (uint id in pak.Order)
            {
                if (pak.TypeOf(id) != "MLVL") continue;
                byte[] w = pak.Get(id);
                uint world = w.Length >= 20 ? Be.U32(w, 16) : uint.MaxValue;
                notes.Add($"Main Quarry: sky {world:x8} (the world's default, MLVL {id:x8}){(pak.Contains(world) ? "" : " NOT in the pak")}");
                return pak.Contains(world) ? world : uint.MaxValue;
            }
            notes.Add("Main Quarry: no MLVL in the pak; no sky");
            return uint.MaxValue;
        }

        // ---- collision ----

        // a box in an object's own space (Prime metres), placed by its transform: each triangle with a point inside (MPH)
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

        // an object's solid shape: a platform's Prime 1 collision mesh (its vertices carry the object's scale already, so
        // placed turned and moved only), else its model's bounds at its transform
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

        // every sealing and scenery solid, as triangles facing out (solid: one triangle, MPH units, and the surface it is).
        // The crates aren't here: they break (Crates). Nor the crane, the wall and the scan panel that swings it: entity
        // collision (CraneSeals), only in the bots' graph (navOnly) here
        public static void Seals(Pak pak, List<Scly1.Obj> objs, Func<float, float, float, Vector3> map,
            Action<Vector3, Vector3, Vector3, Mrea.Surface> solid, Action<Vector3, Vector3, Vector3> navOnly, EchoesArena.Info info)
        {
            List<string> notes = info.Notes;
            int count = 0;
            Action<Vector3, Vector3, Vector3, Vector3> Tri(Mrea.Surface s) => (a, b, c, inside) =>
            {
                Vector3 n = Vector3.Cross(b - a, c - a);
                if (n.LengthSquared < 1e-10f) return;
                if (Vector3.Dot(n, (a + b + c) / 3 - inside) < 0) (b, c) = (c, b);
                solid(a, b, c, s);
                count++;
            };
            // the doors' own boxes: world-aligned about the door's position + offset
            foreach ((uint d, _, _) in Doors)
            {
                if (Obj(objs, d) is not { Type: DoorType } o || o.Props.Length < Start(o) + 64)
                {
                    notes.Add($"Main Quarry: door {d:x8} not found; left open");
                    continue;
                }
                float[] x = Xf(o);
                int b = o.Props.Length - 32;
                Vector3 size = Vec(o, b), off = Vec(o, b + 12);
                Box(new[] { x[0], x[1], x[2], 0f, 0f, 0f, 1f, 1f, 1f }, off - size / 2, off + size / 2, map, Tri(Mrea.Surface.Metal));
            }
            int doors = count;
            var solidOnes = new List<string>();
            // the solid scenery actors (each one's own solid flag)
            foreach (uint id in Scenery)
            {
                if (Obj(objs, id) is not { Type: ActorType } o) { notes.Add($"Main Quarry: {id:x8} not found"); continue; }
                if (o.Props[^15] == 0) continue;
                if (Shape(pak, o, map, Tri(Mrea.Surface.Metal))) solidOnes.Add(o.Name);
                else notes.Add($"Main Quarry: {o.Name} {id:x8} model not read; not solid");
            }
            notes.Add($"Main Quarry: {doors} door seal triangles, {count - doors} scenery triangles ({string.Join(", ", solidOnes)})");
            CraneSeals(pak, objs, map, navOnly, info);
        }

        // ---- the crates ----

        // Info.SandCrates (owner 2026-11-16: "crates real + destructible + respawning, dropping health/missiles/UA"): each crate
        // a MiningPlazaCrateEntity (its health, drops on the attacker's needs, 30 s regrow, network mirror and the bots' crate
        // list), drawn from its own placed pass at its own transform (no clip: one still frame), solid by its DCLN or its
        // model's bounds as entity collision, lit as the area's lights light its middle
        public static void Crates(Pak pak, List<Scly1.Obj> objs, Func<float, float, float, Vector3> map, ArenaRaycaster ray,
            IReadOnlyList<Mrea.Light> lights, EchoesArena.Info info, List<ArenaHdWriter.Placed> placed)
        {
            info.SandCrates = new();
            int NextPass() => placed.Count == 0 ? ArenaHdWriter.FirstPlacedPass : Math.Max(ArenaHdWriter.FirstPlacedPass, placed.Max(p => p.Pass) + 1);
            float[] identity = MiningPlazaCrates.F(Matrix4.Identity);
            var debrisFor = new Dictionary<uint, (List<TurretFx> Pieces, int Count, int Slots)>();
            var debrisPasses = new Dictionary<uint, int>();
            // the crate's Dead -> its Generator ("Chunk Generator": SpawnCount picks of its DebrisExtended followers), straight
            // or through one relay; as Prime1Room.Crates
            (List<TurretFx> Pieces, int Count, int Slots) Debris(Scly1.Obj crate)
            {
                IEnumerable<Scly1.Obj> next = crate.Connections.Select(c => Obj(objs, c.Target)).OfType<Scly1.Obj>();
                Scly1.Obj? genr = next.FirstOrDefault(g => g.Type == GeneratorType)
                    ?? next.SelectMany(r => r.Connections.Select(c => Obj(objs, c.Target)).OfType<Scly1.Obj>()).FirstOrDefault(g => g.Type == GeneratorType);
                if (genr == null) return (new List<TurretFx>(), 0, 0);
                if (debrisFor.TryGetValue(genr.Id, out var had)) return had;
                var pieces = new List<TurretFx>();
                List<Scly1.Obj> followers = genr.Connections.Select(c => Obj(objs, c.Target)).OfType<Scly1.Obj>().Where(f => f.Type == DebrisType).ToList();
                foreach (Scly1.Obj f in followers)
                {
                    if (Prime1Room.PieceOf(f) is not { } d || !pak.Contains(d.Id) || pak.TypeOf(d.Id) != "CMDL") continue;
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
            foreach (uint id in CrateIds)
            {
                if (Obj(objs, id) is not { } o || !pak.Contains(ModelOf(o))) { info.Notes.Add($"Main Quarry: crate {id:x8} not found"); continue; }
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
                    Radius = MathF.Max(hi.X - lo.X, hi.Z - lo.Z) / 2, Top = hi.Y, Metal = true,
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
                else info.Notes.Add($"Main Quarry: crate {o.Name} {id:x8} collision not read; not solid");
                var deb = Debris(o);
                spot.Debris = deb.Pieces;
                spot.DebrisCount = deb.Count;
                spot.DebrisSlots = deb.Slots;
                spot.DebrisPasses = deb.Pieces.Select(d => d.Id).Distinct().ToDictionary(m => m, m => debrisPasses[m]);
                info.SandCrates.Add(spot);
            }
            info.Notes.Add($"Main Quarry: {info.SandCrates.Count} breakable crates (respawn {MiningPlazaCrates.RegrowFrames / 60} s), collision "
                + string.Join("/", info.SandCrates.Select(c => c.Tris.Length / 9)) + " triangles, debris "
                + string.Join("/", info.SandCrates.Select(c => $"{c.DebrisCount} of {c.Debris.Count}")) + " pieces");
            info.Notes.Add(Stacks(objs, info.SandCrates, "Main Quarry"));
        }

        // ---- the look ----

        // the doors, the crane, the wall and the scenery as placed passes (the crates: Crates) after the statics', lit as the area's
        // lights light them at their middle (as MiningPlaza.Looks)
        public static void Looks(Pak pak, List<Scly1.Obj> objs, Func<float, float, float, Vector3> map, ArenaRaycaster ray,
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
            // a static model at a transform
            void Model(float[] xf, uint model, string name)
            {
                if (!pak.Contains(model)) return;
                (Vector3 lo, Vector3 hi) = Bounds(Cmdl.Load(pak.Get, model));
                Add(name, new ArenaHdWriter.Placed(model, xf, 0), ArenaHdWriter.Place(xf, (lo + hi) / 2));
            }
            // a door's animation set character in its clip's first pose (its bind pose when the clip doesn't read)
            void Door(Scly1.Obj o)
            {
                float[] xf = Xf(o);
                string name = $"{o.Name} {o.Id:x8}";
                uint ancsId = U32At(o, 36);
                int character = (int)U32At(o, 40), clip = (int)U32At(o, 44);
                if (!pak.Contains(ancsId)) { info.Notes.Add($"Main Quarry: {name} animation set {ancsId:x8} not in the pak"); return; }
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
                        (string clipName, Ancs.MetaAnim meta) = ancs.Set.Anims[clip];
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
                            info.Notes.Add($"Main Quarry: {name} posed in clip {clip} '{clipName}'");
                            return;
                        }
                    }
                }
                catch (Exception e) when (e is InvalidOperationException or System.IO.InvalidDataException or IndexOutOfRangeException
                    or ArgumentException or KeyNotFoundException)
                {
                    info.Notes.Add($"Main Quarry: {name} clip {clip} not posed ({e.Message}); drawn in its bind pose");
                }
                Add(name, new ArenaHdWriter.Placed(ch.Cmdl, xf, 0), middle);
            }
            foreach ((uint d, uint key, uint shield) in Doors)
            {
                if (Obj(objs, d) is not { Type: DoorType } door) continue;
                Door(door);
                foreach (uint a in new[] { key, shield })
                {
                    if (Obj(objs, a) is { Type: ActorType } o) Model(Xf(o), ModelOf(o), $"{o.Name} {o.Id:x8}");
                }
            }
            // (the crane and the wall: the crane entity's own passes, CraneLooks; the scan panel stays a still one)
            foreach (uint id in Scenery)
            {
                if (Obj(objs, id) is { } o) Model(Xf(o), ModelOf(o), $"{o.Name} {o.Id:x8}");
            }
            info.Notes.Add($"Main Quarry: {drawn.Count} models drawn: {string.Join(", ", drawn)}");
            CraneLooks(pak, objs, map, ray, lights, info, placed);
        }

        // ---- spawns ----

        // eight stand-in spawns until the owner places his own (PickupPlacement's spawn mode; PlacedPickups swaps them in):
        // the room's story spawn, then a spot in front of each door (whichever side has floor: the room's), then beside
        // the crates
        public static List<(Vector3 Pos, Vector3 Facing)> StandInSpawns(List<Scly1.Obj> objs, Func<float, float, float, Vector3> map,
            ArenaRaycaster ray)
        {
            var res = new List<(Vector3, Vector3)>();
            void Try(Vector3 p, Vector3 facing)
            {
                if (res.Count >= 8 || ray.FloorBelow(p + Vector3.UnitY * 0.3f, 6f) is not Vector3 f) return;
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
                foreach ((uint d, _, _) in Doors)
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
            foreach (uint id in CrateIds)
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
