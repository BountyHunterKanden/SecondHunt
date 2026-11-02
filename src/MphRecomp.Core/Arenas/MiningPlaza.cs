using System;
using System.Collections.Generic;
using System.Linq;
using MphRead;
using MphRead.Editor;
using MphRecomp.Import;
using MphRecomp.Import.Retro;
using OpenTK.Mathematics;

namespace MphRecomp.Arenas
{
    using Anim = MphRecomp.Import.Retro.Anim;

    // Mining Plaza (Echoes' Agon Wastes room, Metroid3.pak MREA 427fad9a) as a multiplayer arena (owner

    // 2026-10-31). A story room, so what EchoesArena.Import does for the six multiplayer arenas isn't enough: its exits
    // are sealed here, each with the disc's own pieces as Echoes draws them locked.
    //   the four doors: the door shell in its closed pose, the Door Lock actor (Metroid_Lock, closed_lock) at the door's
    //     transform (where Echoes' 36 locked story doors put it), the blue shield, and the door's own collision box
    //     (DOOR f344c0b0 size at offset 2e686c2a); the South door's Missile Lock blast shield too, with its box
    //   the morph tunnel: its door drawn closed and the bomb rock (Rock Blocker) unbreakable, solid by its DCLN
    //   the translator gate (Gate Closed: no box, no DCLN, so its model's bounds as Echoes' loaders use them) stays shut
    //   the Luminoth gate (PLAT, DCLN, its holograms) opens mid-match as Echoes opens it (Gate, MiningPlazaGate)
    // None of these objects is in the area's collision (story actors carry their own) or its render mesh, so each is solid
    // here and drawn as a placed pass (ArenaHdWriter.Placed: the animated ones baked in their one pose, Placed.Pose).
    // The room's three SPWNs are Samus's story spots and its one pickup the Energy Tank: the owner places the eight
    // spawns and the pickups with the dev placer (PickupPlacement), so the import only leaves eight stand-in spawns.
    internal static class MiningPlaza
    {
        public const string Key = "M07_MiningPlaza";

        const uint North = 0x00020064, West = 0x00020037, South = 0x0002024d, East = 0x00020082, Morph = 0x000200de;
        static readonly uint[] LockedDoors = { North, West, South, East };
        const uint MissileLock = 0x00020021, GateClosed = 0x0002021b, LuminothGate = 0x000202ea, RockBlocker = 0x000201f8;
        // Door Lock: the actor Echoes' locked story doors carry (ANCS Metroid_Lock); the door's shield model
        const uint LockAncs = 0x9486a701, ShieldModel = 0x6b78fd92;
        const string LockClip = "closed_lock";
        const uint AnimParamsId = 0xe25fb08c, BoxId = 0xf344c0b0, OffsetId = 0x2e686c2a, ModelId = 0xc27ffa8f, DclnId = 0x0fc966dc;
        // the bomb rock's and the Luminoth gate's collision meshes (their PLATs' DCLN)
        const uint RockDcln = 0x27db7333, GateDcln = 0xcbf844b1;
        static readonly uint[] SealIds = { MissileLock, GateClosed, LuminothGate, RockBlocker };
        // an actor's "solid" flag (1 on the sand crates, the Echo Emitters and the lore statue)
        const uint SolidId = 0x1d8dd846;
        // the two collapsing pillars' pieces (MiningPlazaPillars, drawn and solid there, not as room actors): each one's
        // column, top, middle and bottom chunks, stone cover and rubble, and the fallen state's (layer 1) rubble and pillar
        static readonly uint[] PillarIds =
        {
            0x00020140, 0x00020139, 0x00020141, 0x0002013f, 0x00020143, 0x00020144,
            0x00020156, 0x0002014c, 0x00020148, 0x00020150, 0x00020157, 0x00020152,
            0x00020109, 0x0002011a, 0x00020119, 0x0002011b,
        };
        // the Luminoth gate's pieces (MiningPlazaGate): the gate, its two holograms and their two glows
        const uint Holo1 = 0x000202ed, Holo2 = 0x000202e6, Glow1 = 0x000202c5, Glow2 = 0x000202c6;
        static readonly uint[] GateIds = { LuminothGate, Holo1, Holo2, Glow1, Glow2 };
        // the seven SAND CRATEs (MiningPlazaCrates: drawn, solid and broken there)
        static readonly uint[] CrateIds = { 0x0002039a, 0x00020085, 0x0002016a, 0x0002039d, 0x0002016c, 0x0002017f, 0x000202ef };
        const uint CrateGenerator = 0x0002017d;
        public static bool OwnsObject(uint id) => Array.IndexOf(PillarIds, id) >= 0 || Array.IndexOf(GateIds, id) >= 0 || Array.IndexOf(CrateIds, id) >= 0;

        static Mrea.ScriptObject? Obj(List<Mrea.ScriptObject> scripts, uint id) => scripts.FirstOrDefault(o => o.Id == id);

        static Vector3? Vec(Mrea.ScriptObject o, uint prop) =>
            o.Root.Find(prop) is { Data.Length: >= 12 } p ? new Vector3(Be.F32(p.Data, 0), Be.F32(p.Data, 4), Be.F32(p.Data, 8)) : null;

        // ---- collision ----

        // every seal's solid shape, as triangles facing out (solid: one triangle, MPH units, and the surface it is)
        public static void Seals(Pak pak, List<Mrea.ScriptObject> scripts, Func<float, float, float, Vector3> map,
            Action<Vector3, Vector3, Vector3, Mrea.Surface> solid, List<string> notes)
        {
            int count = 0;
            void Tri(Vector3 a, Vector3 b, Vector3 c, Vector3 inside, Mrea.Surface s)
            {
                Vector3 n = Vector3.Cross(b - a, c - a);
                if (n.LengthSquared < 1e-10f) return;
                if (Vector3.Dot(n, (a + b + c) / 3 - inside) < 0) (b, c) = (c, b);
                solid(a, b, c, s);
                count++;
            }
            Vector3 M(Vector3 e) => map(e.X, e.Y, e.Z);
            // a box in the object's own space (Echoes metres), placed by its transform
            void Box(float[] xf, Vector3 lo, Vector3 hi, Mrea.Surface s)
            {
                Vector3 C(int k) => M(ArenaHdWriter.Place(xf, new Vector3((k & 1) != 0 ? hi.X : lo.X, (k & 2) != 0 ? hi.Y : lo.Y, (k & 4) != 0 ? hi.Z : lo.Z)));
                Vector3 mid = M(ArenaHdWriter.Place(xf, (lo + hi) / 2));
                int[][] faces = { new[] { 0, 2, 6, 4 }, new[] { 1, 5, 7, 3 }, new[] { 0, 4, 5, 1 }, new[] { 2, 3, 7, 6 }, new[] { 0, 1, 3, 2 }, new[] { 4, 6, 7, 5 } };
                foreach (int[] f in faces)
                {
                    Tri(C(f[0]), C(f[1]), C(f[2]), mid, s);
                    Tri(C(f[0]), C(f[2]), C(f[3]), mid, s);
                }
            }
            // the actor's collision box (size, offset: Echoes' CActor box about its origin, scaled by the transform)
            bool OwnBox(uint id, Mrea.Surface s)
            {
                if (Obj(scripts, id) is not { } o || Vec(o, BoxId) is not Vector3 size || size.X * size.Y * size.Z <= 0) return false;
                Vector3 off = Vec(o, OffsetId) ?? Vector3.Zero;
                Box(o.Transform, off - size / 2, off + size / 2, s);
                return true;
            }
            foreach (uint d in LockedDoors)
            {
                if (!OwnBox(d, Mrea.Surface.Metal)) notes.Add($"Mining Plaza: door {d:x8} has no collision box; left open");
            }
            OwnBox(MissileLock, Mrea.Surface.Metal);
            // the translator gate: its model's bounds
            if (Obj(scripts, GateClosed) is { } gate && gate.Root.Find(ModelId) is { Data.Length: >= 4 } gm && pak.Contains(gm.U32))
            {
                (Vector3 lo, Vector3 hi) = Bounds(Cmdl.Load(pak.Get, gm.U32));
                Box(gate.Transform, lo, hi, Mrea.Surface.Metal);
            }
            else notes.Add("Mining Plaza: Gate Closed not found; the translator gate is open");
            // a collision mesh (DCLN) at an object's transform (a DCLN's triangles come either way round: each turned to face
            // away from the mesh's middle). A scaled actor's mesh is mostly baked at its scale already (the Echo Emitter legs'
            // each have their own): placed unscaled when its size matches the model's scaled, not the model's own
            bool Mesh(Mrea.ScriptObject o, uint mesh, Mrea.Surface s)
            {
                if (MeshTris(pak, o, mesh, map) is not { } tris) return false;
                foreach ((Vector3 a, Vector3 b, Vector3 c, Vector3 mid) in tris) Tri(a, b, c, mid, s);
                return true;
            }
            // the bomb rock: its mesh (the Luminoth gate's is its entity's: Gate)
            foreach ((uint id, uint dcln, Mrea.Surface s) in new[] { (RockBlocker, RockDcln, Mrea.Surface.Stone) })
            {
                Mrea.ScriptObject? o = Obj(scripts, id);
                uint mesh = o?.Root.Find(DclnId) is { Data.Length: >= 4 } dp && dp.U32 != uint.MaxValue ? dp.U32 : dcln;
                if (o == null || !Mesh(o, mesh, s)) notes.Add($"Mining Plaza: {o?.Name ?? id.ToString("x8")} collision {mesh:x8} not read; left open");
            }
            notes.Add($"Mining Plaza: {count} sealing collision triangles (4 doors, Missile Lock, translator gate, bomb rock)");
            // the room's other actors, solid by their own mesh as Echoes has them: every platform, and each actor marked solid
            // (the sand crates, the Echo Emitters, the lore statue; not the holograms, glows, rings or stone covers)
            int before = count;
            var solidOnes = new List<string>();
            foreach (Mrea.ScriptObject o in RoomActors(scripts))
            {
                if (o.Root.Find(DclnId) is not { Data.Length: >= 4 } dp || dp.U32 == uint.MaxValue) continue;
                if (o.Type == "ACTR" && o.Root.Find(SolidId) is not { Data: [not 0, ..] }) continue;
                if (Mesh(o, dp.U32, SurfaceOf(o.Name))) solidOnes.Add(o.Name);
                else notes.Add($"Mining Plaza: {o.Name} {o.Id:x8} collision {dp.U32:x8} not read; not solid");
            }
            notes.Add($"Mining Plaza: {count - before} room actor collision triangles ({string.Join(", ", solidOnes)})");
        }

        // a collision mesh (DCLN) at an object's transform as MPH triangles with the mesh's middle (a DCLN's triangles come
        // either way round: each to be turned to face away from it). A scaled actor's mesh is mostly baked at its scale
        // already (the Echo Emitter legs' each have their own): placed unscaled when its size matches the model's scaled,
        // not the model's own
        static List<(Vector3 A, Vector3 B, Vector3 C, Vector3 Mid)>? MeshTris(Pak pak, Mrea.ScriptObject o, uint mesh, Func<float, float, float, Vector3> map)
        {
            if (!pak.Contains(mesh) || EchoesTurret.ReadDcln(pak.Get(mesh)) is not (float[] v, int[] t)) return null;
            float[] xf = o.Transform;
            if ((MathF.Abs(xf[6] - 1) > 0.01f || MathF.Abs(xf[7] - 1) > 0.01f || MathF.Abs(xf[8] - 1) > 0.01f)
                && o.Root.Find(ModelId) is { Data.Length: >= 4 } mp && pak.Contains(mp.U32))
            {
                (Vector3 mlo, Vector3 mhi) = Bounds(Cmdl.Load(pak.Get, mp.U32));
                var lo = new Vector3(float.MaxValue);
                var hi = new Vector3(float.MinValue);
                for (int i = 0; i + 2 < v.Length; i += 3)
                {
                    lo = Vector3.ComponentMin(lo, new Vector3(v[i], v[i + 1], v[i + 2]));
                    hi = Vector3.ComponentMax(hi, new Vector3(v[i], v[i + 1], v[i + 2]));
                }
                var sc = new Vector3(xf[6], xf[7], xf[8]);
                if (((hi - lo) - (mhi - mlo) * sc).Length < ((hi - lo) - (mhi - mlo)).Length)
                {
                    xf = (float[])xf.Clone();
                    xf[6] = xf[7] = xf[8] = 1;
                }
            }
            Vector3 P(int i)
            {
                Vector3 e = ArenaHdWriter.Place(xf, new Vector3(v[3 * i], v[3 * i + 1], v[3 * i + 2]));
                return map(e.X, e.Y, e.Z);
            }
            Vector3 mid = Vector3.Zero;
            for (int i = 0; i < v.Length / 3; i++) mid += P(i);
            mid /= Math.Max(1, v.Length / 3);
            var res = new List<(Vector3, Vector3, Vector3, Vector3)>();
            for (int k = 0; k + 2 < t.Length; k += 3) res.Add((P(t[k]), P(t[k + 1]), P(t[k + 2]), mid));
            return res;
        }

        static Mrea.Surface SurfaceOf(string name) =>
            name.Contains("SAND CRATE") ? Mrea.Surface.Organic
            : name.Contains("Pillar") || name.Contains("Chunk") || name.Contains("Stone") || name.Contains("Rubble") || name.Contains("Lore") ? Mrea.Surface.Stone
            : Mrea.Surface.Metal;

        // the room's own actors and platforms past the seals: the active default-layer ones (layers 1+ are story states:
        // the clouds, the lightning and echo platforms, the opened gates, the fallen pillars)
        static IEnumerable<Mrea.ScriptObject> RoomActors(List<Mrea.ScriptObject> scripts) =>
            scripts.Where(o => o.Active && o.Layer == 0 && o.Type is "ACTR" or "PLAT" && !SealIds.Contains(o.Id) && !OwnsObject(o.Id));

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

        // ---- the look ----

        // each seal's models as placed passes after the statics', lit as Echoes lights them at their middle (as AddStatics)
        public static void Looks(Pak pak, List<Mrea.ScriptObject> scripts, Func<float, float, float, Vector3> map, ArenaRaycaster ray,
            IReadOnlyList<Mrea.Light> lights, EchoesArena.Info info, List<ArenaHdWriter.Placed> placed)
        {
            var drawn = new List<string>();
            void Add(string name, ArenaHdWriter.Placed pl, Vector3 middle)
            {
                info.Statics ??= new();
                // AddStatics already draws a seal whose model its collision now follows (same name)
                if (info.Statics.Any(st => st.Name == name)) return;
                int pass = ArenaHdWriter.FirstPlacedPass + (info.Breakables?.Count ?? 0) + info.Statics.Count;
                (float[] amb, List<float[]> lit) = EchoesCrates.Lighting(lights, middle, map, ray);
                info.Statics.Add(new EchoesArena.StaticSpot { Name = name, Pass = pass, Ambient = amb, Lights = lit });
                placed.Add(pl with { Pass = pass });
                drawn.Add(name);
            }
            // a static model at an object's transform
            void Model(Mrea.ScriptObject o, uint model, string name)
            {
                if (!pak.Contains(model)) return;
                (Vector3 lo, Vector3 hi) = Bounds(Cmdl.Load(pak.Get, model));
                Add(name, new ArenaHdWriter.Placed(model, o.Transform, 0), ArenaHdWriter.Place(o.Transform, (lo + hi) / 2));
            }
            // an animation set's character in one clip's first pose, at an object's transform
            void Posed(Mrea.ScriptObject o, uint ancsId, int character, Func<Ancs.AnimSet, Ancs.MetaAnim?> clip, string name)
            {
                if (!pak.Contains(ancsId)) return;
                Ancs ancs = Ancs.Parse(pak.Get(ancsId));
                if (character >= ancs.Characters.Count || ancs.Set == null || clip(ancs.Set) is not { } meta) return;
                Ancs.Character ch = ancs.Characters[character];
                uint animId = meta.Kind == "Play" || meta.Children.Count == 0 ? meta.AnimId : Ancs.Prims(meta).FirstOrDefault()?.AnimId ?? uint.MaxValue;
                if (!pak.Contains(animId) || !pak.Contains(ch.Cinf) || !pak.Contains(ch.Cmdl)) return;
                Cinf sk = Cinf.Load(pak.Get(ch.Cinf));
                Anim anim = Anim.Load(pak.Get(animId), echoes: true);
                var pose = new Dictionary<uint, Matrix4>();
                foreach ((uint bone, (double[] q, double[] p)) in EchoesTurret.Pose(sk, anim, 0))
                {
                    Cinf.Bone b = sk.Bones[bone];
                    pose[bone] = Matrix4.CreateTranslation(-(float)b.X, -(float)b.Y, -(float)b.Z)
                        * Matrix4.CreateFromQuaternion(new Quaternion((float)q[0], (float)q[1], (float)q[2], (float)q[3]))
                        * Matrix4.CreateTranslation((float)p[0], (float)p[1], (float)p[2]);
                }
                (Vector3 lo, Vector3 hi) = Bounds(Cmdl.Load(pak.Get, ch.Cmdl));
                Add(name, new ArenaHdWriter.Placed(ch.Cmdl, o.Transform, 0) { Skin = ch.Cskr, Pose = pose }, ArenaHdWriter.Place(o.Transform, (lo + hi) / 2));
            }
            // a door's own animation set at its AnimParams clip (the closed pose: closed_door, the tunnel's closed)
            void Door(Mrea.ScriptObject o)
            {
                if (o.Root.Find(AnimParamsId) is not { Data.Length: >= 12 } ap) return;
                int clip = (int)Be.U32(ap.Data, 8);
                Posed(o, Be.U32(ap.Data, 0), (int)Be.U32(ap.Data, 4), s => clip < s.Anims.Count ? s.Anims[clip].Anim : null, $"{o.Name} door {o.Id:x8}");
            }
            foreach (uint d in LockedDoors)
            {
                if (Obj(scripts, d) is not { } o) continue;
                Door(o);
                Posed(o, LockAncs, 0, s => s.Anims.FirstOrDefault(a => a.Name == LockClip).Anim, $"Door Lock {o.Id:x8}");
                Model(o, ShieldModel, $"Door Shield {o.Id:x8}");
            }
            if (Obj(scripts, Morph) is { } morph) Door(morph);
            foreach (uint id in SealIds)
            {
                if (id == LuminothGate) continue;
                if (Obj(scripts, id) is { } o && o.Root.Find(ModelId) is { Data.Length: >= 4 } mp && mp.U32 != uint.MaxValue) Model(o, mp.U32, $"{o.Name} {o.Id:x8}");
            }
            info.Notes.Add($"Mining Plaza: {drawn.Count} sealing models drawn: {string.Join(", ", drawn)}");
            // every other actor and platform of the room (AddStatics draws those its collision test finds: not again), an
            // animated one in its AnimParams clip's first pose
            drawn.Clear();
            foreach (Mrea.ScriptObject o in RoomActors(scripts))
            {
                string name = $"{o.Name} {o.Id:x8}";
                if (o.Root.Find(AnimParamsId) is { Data.Length: >= 12 } ap && Be.U32(ap.Data, 0) is uint ancs && ancs != uint.MaxValue && pak.Contains(ancs))
                {
                    int clip = (int)Be.U32(ap.Data, 8);
                    Posed(o, ancs, (int)Be.U32(ap.Data, 4), s => clip < s.Anims.Count ? s.Anims[clip].Anim : null, name);
                }
                else if (o.Root.Find(ModelId) is { Data.Length: >= 4 } mp && mp.U32 != uint.MaxValue) Model(o, mp.U32, name);
            }
            info.Notes.Add($"Mining Plaza: {drawn.Count} room actor models drawn: {string.Join(", ", drawn)}");
            // the ones Echoes turns with an ActorRotate (memory: 11-01 AROT decode) spin in the same loops
            foreach ((uint id, int axis, float degrees, float period) in Spinners)
            {
                if (Obj(scripts, id) is not { } o || info.Statics?.FirstOrDefault(st => st.Name == $"{o.Name} {o.Id:x8}") is not { } st) continue;
                st.SpinXf = (float[])o.Transform.Clone();
                st.SpinAxis = axis;
                st.SpinDegrees = degrees;
                st.SpinPeriod = period;
                st.Scale = info.Scale;
                info.Notes.Add($"Mining Plaza: {st.Name} spins {degrees} deg about its axis {axis - 3} every {period} s (rotation {o.Transform[3]:0.#}, {o.Transform[4]:0.#}, {o.Transform[5]:0.#})");
            }
        }

        // the room's spinning actors and their ActorRotates' loops (own axis 3 X / 5 Z, degrees, seconds): the Lore Hologram
        // over the upper middle platform (AROT 00020281), the translator gate's Out_Rings (AROTs 00020111, 0002010e, 0002010d)
        static readonly (uint Id, int Axis, float Degrees, float Period)[] Spinners =
        {
            (0x0002027d, 5, 360, 4), (0x0002010f, 3, 360, 10), (0x00020110, 3, -360, 12), (0x0002010c, 3, 360, 15),
        };

        // ---- the collapsing pillars ----

        // each pillar's pieces (Echoes ids), its waypoint (pillar_end) and its effects' spots (Echoes), read from the room's
        // pillar timers (memory: stage 2 decode): column, top, middle, bottom, cover, rubble
        static readonly (uint Column, uint Top, uint Middle, uint Bottom, uint Cover, uint Rubble, Vector3 End, Vector3 Explosion,
            Vector3 Impact, float ImpactRz, Vector3 Dust1, Vector3 Dust2)[] PillarSet =
        {
            (0x00020140, 0x00020139, 0x00020141, 0x0002013f, 0x00020143, 0x00020144, new(-328.309f, -42.301f, -21.116f),
                new(-331.6f, -39.9f, -19.0f), new(-315.707f, -32.868f, -19.781f), -112.232f, new(-311.6f, -36.0f, -17.9f), new(-312.7f, -31.9f, -18.0f)),
            (0x00020156, 0x0002014c, 0x00020148, 0x00020150, 0x00020157, 0x00020152, new(-352.919f, -97.183f, -21.964f),
                new(-355.7f, -99.6f, -20.6f), new(-354.621f, -81.533f, -20.765f), -52.845f, new(-353.9f, -78.4f, -19.0f), new(-349.8f, -79.6f, -18.9f)),
        };
        // the pieces' collision meshes when an object names none (the columns', the bottom chunk's, the rubble's)
        static readonly uint[] ColumnDcln = { 0x4a9c7ca4, 0xcc080e0a };
        const uint BottomDcln = 0x95f8ceee, RubbleDcln = 0xb9574fd4;

        // Info.Pillars: each pillar's pieces as placed passes of their own (drawn by its entity, not as statics), its
        // collision and its effects' spots
        public static void Pillars(Pak pak, List<Mrea.ScriptObject> scripts, Func<float, float, float, Vector3> map, ArenaRaycaster ray,
            IReadOnlyList<Mrea.Light> lights, EchoesArena.Info info, List<ArenaHdWriter.Placed> placed)
        {
            info.Pillars = new();
            float[] F(Vector3 e)
            {
                Vector3 m = map(e.X, e.Y, e.Z);
                return new[] { m.X, m.Y, m.Z };
            }
            for (int k = 0; k < PillarSet.Length; k++)
            {
                var d = PillarSet[k];
                if (Obj(scripts, d.Column) is not { } col)
                {
                    info.Notes.Add($"Mining Plaza: pillar {d.Column:x8} not in the room; left out");
                    continue;
                }
                var spot = new MiningPlazaPillars.PillarSpot
                {
                    Id = col.Id, Xf = (float[])col.Transform.Clone(), End = new[] { d.End.X, d.End.Y, d.End.Z }, Scale = info.Scale,
                    Explosion = F(d.Explosion), Impact = F(d.Impact), Dust1 = F(d.Dust1), Dust2 = F(d.Dust2),
                };
                float[] ixf = { d.Impact.X, d.Impact.Y, d.Impact.Z, 0, 0, d.ImpactRz, 1, 1, 1 };
                Vector3 io = map(d.Impact.X, d.Impact.Y, d.Impact.Z);
                var axes = new List<float>();
                foreach (Vector3 e in new[] { Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ })
                {
                    Vector3 w = ArenaHdWriter.Place(ixf, e);
                    Vector3 ax = (map(w.X, w.Y, w.Z) - io).Normalized();
                    axes.AddRange(new[] { ax.X, ax.Y, ax.Z });
                }
                spot.ImpactAxes = axes.ToArray();
                // the pieces: each its model at its own transform, a pass of its own
                Vector3 colMid = Vector3.Zero;
                int Piece(uint id, string role)
                {
                    if (Obj(scripts, id) is not { } o || o.Root.Find(ModelId) is not { Data.Length: >= 4 } mp || mp.U32 == uint.MaxValue || !pak.Contains(mp.U32))
                    {
                        info.Notes.Add($"Mining Plaza: pillar {col.Id:x8} {role} {id:x8} has no model; not drawn");
                        return -1;
                    }
                    if (role == "column")
                    {
                        (Vector3 lo, Vector3 hi) = Bounds(Cmdl.Load(pak.Get, mp.U32));
                        colMid = (lo + hi) / 2;
                    }
                    int pass = placed.Count == 0 ? ArenaHdWriter.FirstPlacedPass : Math.Max(ArenaHdWriter.FirstPlacedPass, placed.Max(p => p.Pass) + 1);
                    placed.Add(new ArenaHdWriter.Placed(mp.U32, o.Transform, pass));
                    return pass;
                }
                spot.Column = Piece(d.Column, "column");
                spot.Top = Piece(d.Top, "top");
                spot.Middle = Piece(d.Middle, "middle");
                spot.Bottom = Piece(d.Bottom, "bottom");
                spot.Cover = Piece(d.Cover, "cover");
                spot.Rubble = Piece(d.Rubble, "rubble");
                // lit as Echoes lights the column's middle standing, and fallen (the rubble too)
                (spot.Ambient, spot.Lights) = EchoesCrates.Lighting(lights, ArenaHdWriter.Place(col.Transform, colMid), map, ray);
                float[] down = MiningPlazaPillars.FallXf(col.Transform, spot.End, MiningPlazaPillars.Landed);
                (spot.DownAmbient, spot.DownLights) = EchoesCrates.Lighting(lights, ArenaHdWriter.Place(down, colMid), map, ray);
                // collision: the column's and the bottom chunk's meshes, the rubble's (triangles facing out)
                float[] Solid(uint id, uint fallback, string role)
                {
                    Mrea.ScriptObject? o = Obj(scripts, id);
                    uint mesh = o?.Root.Find(DclnId) is { Data.Length: >= 4 } dp && dp.U32 != uint.MaxValue ? dp.U32 : fallback;
                    if (o == null || MeshTris(pak, o, mesh, map) is not { } tris)
                    {
                        info.Notes.Add($"Mining Plaza: pillar {col.Id:x8} {role} collision {mesh:x8} not read; not solid");
                        return Array.Empty<float>();
                    }
                    var f = new List<float>(tris.Count * 9);
                    foreach ((Vector3 a, Vector3 b0, Vector3 c0, Vector3 mid) in tris)
                    {
                        Vector3 b = b0, c = c0;
                        Vector3 n = Vector3.Cross(b - a, c - a);
                        if (n.LengthSquared < 1e-10f) continue;
                        if (Vector3.Dot(n, (a + b + c) / 3 - mid) < 0) (b, c) = (c, b);
                        f.AddRange(new[] { a.X, a.Y, a.Z, b.X, b.Y, b.Z, c.X, c.Y, c.Z });
                    }
                    return f.ToArray();
                }
                spot.ColumnTris = Solid(d.Column, ColumnDcln[k], "column");
                spot.BottomTris = Solid(d.Bottom, BottomDcln, "bottom");
                spot.RubbleTris = Solid(d.Rubble, RubbleDcln, "rubble");
                info.Pillars.Add(spot);
                info.Notes.Add($"Mining Plaza: pillar {col.Id:x8}: passes column {spot.Column}, top {spot.Top}, middle {spot.Middle}, bottom {spot.Bottom},"
                    + $" cover {spot.Cover}, rubble {spot.Rubble}; collision {spot.ColumnTris.Length / 9}/{spot.BottomTris.Length / 9}/{spot.RubbleTris.Length / 9} triangles");
            }
        }

        // ---- the sand crates ----

        public const float CrateFps = 30;   // (11-01: was 12, which read as a handful of flinch frames)

        // Info.SandCrates: the crates' animation set (ready loop, flinch) baked a frame a pass at crate 0's transform, each
        // crate's place relative to it, its collision (its DCLN), light and its generator's debris pieces. `withGenerated`:
        // the room's scripts with the generated layer (the debris pieces are only there)
        public static void Crates(Pak pak, List<Mrea.ScriptObject> scripts, List<Mrea.ScriptObject> withGenerated, Func<float, float, float, Vector3> map,
            ArenaRaycaster ray, IReadOnlyList<Mrea.Light> lights, EchoesArena.Info info, List<ArenaHdWriter.Placed> placed)
        {
            info.SandCrates = null;
            var crates = CrateIds.Select(id => Obj(scripts, id)).OfType<Mrea.ScriptObject>().ToList();
            if (crates.Count == 0) return;
            int NextPass() => placed.Count == 0 ? ArenaHdWriter.FirstPlacedPass : Math.Max(ArenaHdWriter.FirstPlacedPass, placed.Max(p => p.Pass) + 1);
            Mrea.ScriptObject first = crates[0];
            if (first.Root.Find(AnimParamsId) is not { Data.Length: >= 12 } ap || !pak.Contains(Be.U32(ap.Data, 0)))
            {
                info.Notes.Add("Mining Plaza: sand crate animation set not found; crates left out");
                return;
            }
            Ancs ancs = Ancs.Parse(pak.Get(Be.U32(ap.Data, 0)));
            int character = (int)Be.U32(ap.Data, 4);
            if (character >= ancs.Characters.Count || ancs.Set == null || ancs.Set.Anims.Count < 2)
            {
                info.Notes.Add("Mining Plaza: sand crate animation set has no ready/flinch clips; crates left out");
                return;
            }
            Ancs.Character ch = ancs.Characters[character];
            Ancs.AnimSet set = ancs.Set;
            Cinf sk = Cinf.Load(pak.Get(ch.Cinf));
            (Vector3 mlo, Vector3 mhi) = Bounds(Cmdl.Load(pak.Get, ch.Cmdl));
            int[] Clip(int index)
            {
                Ancs.MetaAnim meta = set.Anims[index].Anim;
                uint animId = meta.Kind == "Play" || meta.Children.Count == 0 ? meta.AnimId : Ancs.Prims(meta).FirstOrDefault()?.AnimId ?? uint.MaxValue;
                if (!pak.Contains(animId)) return Array.Empty<int>();
                Anim anim = Anim.Load(pak.Get(animId), echoes: true);
                int frames = Math.Max(1, (int)Math.Round(anim.Duration * CrateFps));
                var passes = new int[frames];
                for (int f = 0; f < frames; f++)
                {
                    var pose = new Dictionary<uint, Matrix4>();
                    foreach ((uint bone, (double[] q, double[] p)) in EchoesTurret.Pose(sk, anim, f / CrateFps))
                    {
                        Cinf.Bone b = sk.Bones[bone];
                        pose[bone] = Matrix4.CreateTranslation(-(float)b.X, -(float)b.Y, -(float)b.Z)
                            * Matrix4.CreateFromQuaternion(new Quaternion((float)q[0], (float)q[1], (float)q[2], (float)q[3]))
                            * Matrix4.CreateTranslation((float)p[0], (float)p[1], (float)p[2]);
                    }
                    passes[f] = NextPass();
                    placed.Add(new ArenaHdWriter.Placed(ch.Cmdl, first.Transform, passes[f]) { Skin = ch.Cskr, Pose = pose });
                }
                return passes;
            }
            int[] ready = Clip(0), flinch = Clip(1);
            // the debris: each crate's generator (its connection to a GENR; else the room's crate generator) and its pieces
            Mrea.ScriptObject? Genr(Mrea.ScriptObject crate) =>
                crate.Connections.Select(c => withGenerated.FirstOrDefault(o => o.Id == c.Target && o.Type == "GENR")).OfType<Mrea.ScriptObject>().FirstOrDefault()
                ?? withGenerated.FirstOrDefault(o => o.Id == CrateGenerator && o.Type == "GENR");
            var debrisFor = new Dictionary<uint, (List<TurretFx> Pieces, int Count, int Slots)>();
            var debrisPasses = new Dictionary<uint, int>();
            (List<TurretFx> Pieces, int Count, int Slots) Debris(Mrea.ScriptObject? genr)
            {
                if (genr == null) return (new List<TurretFx>(), 0, 0);
                if (debrisFor.TryGetValue(genr.Id, out var had)) return had;
                var pieces = new List<TurretFx>();
                foreach (Mrea.ScriptObject o in genr.Connections.Where(c => c.Message == "ACTV")
                    .Select(c => withGenerated.FirstOrDefault(o => o.Id == c.Target && o.Type == "DBR2")).OfType<Mrea.ScriptObject>())
                {
                    if (EchoesTurret.GrateDebris(o) is not { } d || !pak.Contains(d.Id) || pak.TypeOf(d.Id) != "CMDL") continue;
                    pieces.Add(d);
                    if (!debrisPasses.ContainsKey(d.Id))
                    {
                        debrisPasses[d.Id] = NextPass();
                        placed.Add(new ArenaHdWriter.Placed(d.Id, new float[] { 0, 0, 0, 0, 0, 0, 1, 1, 1 }, debrisPasses[d.Id]));
                    }
                }
                int count = genr.Root.Find(0x3fa67145) is { Data.Length: >= 4 } rc ? (int)rc.U32 : pieces.Count;   // RandomCount
                int slots = genr.Connections.Count(c => c.Message == "ACTV");
                return debrisFor[genr.Id] = (pieces, count, slots);
            }
            Matrix4 baseFrame = MiningPlazaPillars.Frame(first.Transform, info.Scale);
            info.SandCrates = new();
            foreach (Mrea.ScriptObject o in crates)
            {
                Matrix4 frame = MiningPlazaPillars.Frame(o.Transform, info.Scale);
                Vector3 origin = map(o.Transform[0], o.Transform[1], o.Transform[2]);
                // the model box in MPH (its eight corners through the crate transform)
                var lo = new Vector3(float.MaxValue);
                var hi = new Vector3(float.MinValue);
                for (int c = 0; c < 8; c++)
                {
                    var e = new Vector3((c & 1) != 0 ? mhi.X : mlo.X, (c & 2) != 0 ? mhi.Y : mlo.Y, (c & 4) != 0 ? mhi.Z : mlo.Z);
                    Vector3 w = ArenaHdWriter.Place(o.Transform, e);
                    Vector3 m = map(w.X, w.Y, w.Z);
                    lo = Vector3.ComponentMin(lo, m);
                    hi = Vector3.ComponentMax(hi, m);
                }
                Vector3 center = (lo + hi) / 2;
                var spot = new MiningPlazaCrates.CrateSpot
                {
                    Id = o.Id, Ready = ready, Flinch = flinch, Fps = CrateFps,
                    Delta = MiningPlazaCrates.F(baseFrame.Inverted() * frame),
                    Origin = new[] { origin.X, origin.Y, origin.Z }, Center = new[] { center.X, center.Y, center.Z },
                    Radius = MathF.Max(hi.X - lo.X, hi.Z - lo.Z) / 2, Top = hi.Y,
                };
                (spot.Ambient, spot.Lights) = EchoesCrates.Lighting(lights, ArenaHdWriter.Place(o.Transform, (mlo + mhi) / 2), map, ray);
                uint mesh = o.Root.Find(DclnId) is { Data.Length: >= 4 } dp && dp.U32 != uint.MaxValue ? dp.U32 : 0xd34bfdeb;
                if (MeshTris(pak, o, mesh, map) is { } tris)
                {
                    var f = new List<float>(tris.Count * 9);
                    foreach ((Vector3 a, Vector3 b0, Vector3 c0, Vector3 mid) in tris)
                    {
                        Vector3 b = b0, c = c0;
                        Vector3 n = Vector3.Cross(b - a, c - a);
                        if (n.LengthSquared < 1e-10f) continue;
                        if (Vector3.Dot(n, (a + b + c) / 3 - mid) < 0) (b, c) = (c, b);
                        f.AddRange(new[] { a.X, a.Y, a.Z, b.X, b.Y, b.Z, c.X, c.Y, c.Z });
                    }
                    spot.Tris = f.ToArray();
                }
                else info.Notes.Add($"Mining Plaza: sand crate {o.Id:x8} collision {mesh:x8} not read; not solid");
                var deb = Debris(Genr(o));
                spot.Debris = deb.Pieces;
                spot.DebrisCount = deb.Count;
                spot.DebrisSlots = deb.Slots;
                spot.DebrisPasses = deb.Pieces.Select(d => d.Id).Distinct().ToDictionary(id => id, id => debrisPasses[id]);
                info.SandCrates.Add(spot);
            }
            info.Notes.Add($"Mining Plaza: {info.SandCrates.Count} sand crates, ready {ready.Length} + flinch {flinch.Length} frames at {CrateFps} fps,"
                + $" debris {string.Join("/", info.SandCrates.Select(c => c.Debris.Count))} pieces ({info.SandCrates[0].DebrisCount} of {info.SandCrates[0].DebrisSlots}),"
                + $" collision {string.Join("/", info.SandCrates.Select(c => c.Tris.Length / 9))} triangles");
        }

        // the crates' debris death effects (for the pillar look's effects)
        public static IEnumerable<(string Role, uint Id)> CrateFx(EchoesArena.Info info) =>
            info.SandCrates?.SelectMany(c => c.Debris).Select(d => d.Death).Where(d => d != 0).Distinct().Select(d => ($"crate_death_{d:x8}", d))
            ?? Enumerable.Empty<(string, uint)>();

        // ---- the Luminoth gate ----

        // Echoes' 'Gate Control' sequence (SQTR 000202e8, decoded from the disc; seconds from its start): the holograms and
        // glows off with the squeak and the stop sound at 0, the dust at 0.49, the gate PLAT starts at 1.0 (its 5 s path down
        // to WAYP 'Gate BOTTOM' 000202c4 on its progress spline: 10% by 0.5 s, held to 1.0 s, the rest by 5.0 s), the lower
        // loop at 1.299, the idle hum and the lower loop stop at 4.9, the dust stops at 5.0, the stop sound again at 5.5;
        // at 6.0 'Keep Gate Down' swaps in the Opened PLAT (same model and DCLN, at the bottom). The holograms spin by AROT
        // 000202dd: -360 degrees about world Z every 4 s. The camera shaker (CAMS 000202e9) at 0 is left out.
        static readonly Vector3 GateBottom = new(-336.804f, -14.604f, -40.584f);
        static readonly Vector3 DustFront = new(-329.75f, -15.369f, -30.191f), DustBack = new(-329.833f, -11.824f, -30.191f);

        public static void Gate(Pak pak, List<Mrea.ScriptObject> scripts, Func<float, float, float, Vector3> map, ArenaRaycaster ray,
            IReadOnlyList<Mrea.Light> lights, EchoesArena.Info info, List<ArenaHdWriter.Placed> placed)
        {
            if (Obj(scripts, LuminothGate) is not { } g)
            {
                info.Notes.Add("Mining Plaza: Luminoth gate not in the room; left out");
                return;
            }
            float[] F(Vector3 e)
            {
                Vector3 m = map(e.X, e.Y, e.Z);
                return new[] { m.X, m.Y, m.Z };
            }
            Vector3 bottom = Obj(scripts, 0x000202c4) is { } wp ? new Vector3(wp.Transform[0], wp.Transform[1], wp.Transform[2]) : GateBottom;
            var spot = new MiningPlazaGate.GateSpot
            {
                Id = g.Id, Xf = (float[])g.Transform.Clone(), End = new[] { bottom.X, bottom.Y, bottom.Z }, Scale = info.Scale,
                HolosOff = 0, MoveStart = 1, MoveEnd = 6,
                Ease = new() { new[] { 0f, 0f }, new[] { 0.1f, 0.1f }, new[] { 0.2f, 0.1f }, new[] { 1f, 1f } },
                SpinPeriod = 4, Spin = new() { new[] { 0f, 0f }, new[] { 1f, -360f } },
                DustFront = F(DustFront), DustBack = F(DustBack),
                Cues = new()
                {
                    new() { Role = "squeak", At = 0 }, new() { Role = "stop", At = 0 }, new() { Role = "dust", At = 0.49f },
                    new() { Role = "lower", At = 1.299f }, new() { Role = "idleoff", At = 4.9f }, new() { Role = "loweroff", At = 4.9f },
                    new() { Role = "dustoff", At = 5.0f }, new() { Role = "stop", At = 5.5f },
                },
            };
            Vector3 mid = Vector3.Zero;
            int Piece(Mrea.ScriptObject o, bool middle)
            {
                if (o.Root.Find(ModelId) is not { Data.Length: >= 4 } mp || mp.U32 == uint.MaxValue || !pak.Contains(mp.U32))
                {
                    info.Notes.Add($"Mining Plaza: gate piece {o.Name} {o.Id:x8} has no model; not drawn");
                    return -1;
                }
                if (middle)
                {
                    (Vector3 lo, Vector3 hi) = Bounds(Cmdl.Load(pak.Get, mp.U32));
                    mid = ArenaHdWriter.Place(o.Transform, (lo + hi) / 2);
                }
                int pass = placed.Count == 0 ? ArenaHdWriter.FirstPlacedPass : Math.Max(ArenaHdWriter.FirstPlacedPass, placed.Max(p => p.Pass) + 1);
                placed.Add(new ArenaHdWriter.Placed(mp.U32, o.Transform, pass));
                return pass;
            }
            spot.Pass = Piece(g, true);
            (spot.Ambient, spot.Lights) = EchoesCrates.Lighting(lights, mid, map, ray);
            spot.Center = F(mid);
            Vector3 holoMid = Vector3.Zero;
            int holos = 0;
            foreach (uint id in new[] { Holo1, Holo2 })
            {
                if (Obj(scripts, id) is not { } h) continue;
                spot.HoloPasses.Add(Piece(h, false));
                spot.HoloXf.Add((float[])h.Transform.Clone());
                holoMid += new Vector3(h.Transform[0], h.Transform[1], h.Transform[2]);
                holos++;
            }
            foreach (uint id in new[] { Glow1, Glow2 })
            {
                if (Obj(scripts, id) is { } gl) spot.GlowPasses.Add(Piece(gl, false));
            }
            if (holos > 0) (spot.HoloAmbient, spot.HoloLights) = EchoesCrates.Lighting(lights, holoMid / holos, map, ray);
            // collision: the gate's DCLN (the Opened PLAT's is the same), triangles facing out
            uint mesh = g.Root.Find(DclnId) is { Data.Length: >= 4 } dp && dp.U32 != uint.MaxValue ? dp.U32 : GateDcln;
            if (MeshTris(pak, g, mesh, map) is { } tris)
            {
                var f = new List<float>(tris.Count * 9);
                foreach ((Vector3 a, Vector3 b0, Vector3 c0, Vector3 m) in tris)
                {
                    Vector3 b = b0, c = c0;
                    Vector3 n = Vector3.Cross(b - a, c - a);
                    if (n.LengthSquared < 1e-10f) continue;
                    if (Vector3.Dot(n, (a + b + c) / 3 - m) < 0) (b, c) = (c, b);
                    f.AddRange(new[] { a.X, a.Y, a.Z, b.X, b.Y, b.Z, c.X, c.Y, c.Z });
                }
                spot.Tris = f.ToArray();
            }
            else info.Notes.Add($"Mining Plaza: Luminoth gate collision {mesh:x8} not read; not solid");
            info.Gate = spot;
            info.Notes.Add($"Mining Plaza: Luminoth gate {g.Id:x8}: pass {spot.Pass}, holograms {string.Join("/", spot.HoloPasses)}, glows {string.Join("/", spot.GlowPasses)},"
                + $" collision {spot.Tris.Length / 9} triangles, down {(spot.Xf[2] - bottom.Z):0.000} m");
        }

        // ---- spawns ----

        // eight stand-in spawns until the owner places his own (PickupPlacement's spawn mode; PlacedPickups swaps them in):
        // the room's story spawns, then a spot in front of each locked door, then the room's middle
        public static List<(Vector3 Pos, Vector3 Facing)> StandInSpawns(List<Mrea.ScriptObject> scripts, Func<float, float, float, Vector3> map,
            ArenaRaycaster ray)
        {
            var res = new List<(Vector3, Vector3)>();
            void Try(Vector3 p, Vector3 facing)
            {
                if (res.Count >= 8 || ray.FloorBelow(p + Vector3.UnitY * 0.3f, 6f) is not Vector3 f) return;
                if (res.Any(r => (r.Item1 - f).LengthSquared < 4)) return;
                res.Add((f, facing));
            }
            foreach (Mrea.ScriptObject o in scripts.Where(o => o.Type == "SPWN" && o.Active))
            {
                float[] x = o.Transform;
                float yaw = MathHelper.DegreesToRadians(x[5]);
                Try(map(x[0], x[1], x[2]), map(-MathF.Sin(yaw), MathF.Cos(yaw), 0).Normalized());
            }
            // the door's room side is its -X (the lock and shield face that way)
            foreach (float step in new[] { 4f, 9f })
            {
                foreach (uint d in LockedDoors)
                {
                    if (Obj(scripts, d) is not { } o) continue;
                    Vector3 a = ArenaHdWriter.Place(o.Transform, new Vector3(-step, 0, 1)), b = ArenaHdWriter.Place(o.Transform, new Vector3(-step - 1, 0, 1));
                    Vector3 pa = map(a.X, a.Y, a.Z), pb = map(b.X, b.Y, b.Z);
                    Vector3 facing = pb - pa;
                    facing.Y = 0;
                    Try(pa, facing.LengthSquared > 0 ? facing.Normalized() : Vector3.UnitZ);
                }
            }
            return res;
        }
    }
}
