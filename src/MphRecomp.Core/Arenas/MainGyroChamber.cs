using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using MphRead;
using MphRecomp.Import;
using MphRecomp.Import.Retro;
using MphRecomp.PrimeMusic;
using OpenTK.Mathematics;

namespace MphRecomp.Arenas
{
    using Anim = MphRecomp.Import.Retro.Anim;

    // Main Gyro Chamber (Echoes' Sanctuary Fortress room, Metroid5.pak MREA 73342a54) as a multiplayer arena (thread 6a6158,
    // owner 2026-11-16). The room is frozen as the campaign leaves it, before the rest of EchoesArena.Import reads it
    // (Freeze rewrites the script objects' transforms, Active and layers):
    //   the two elevators down (their lower waypoints), each with a jump pad to its top stop (ElevatorPads)
    //   the three glass panels gone, whole and broken (owner 2026-11-16: "remove them so that it leaves holes that can be
    //   jumped through"); the centre sphere broken (gone: its bottom pieces stay), the L2 pickup gone
    //   the gyro rings at the pose they stop at (the outer ring on its Lower gyro waypoint; the inner one as authored)
    //   the spinner door open, the Echo Gate in front of the morph ball cannon down (its waypoint), its Echo Locks gone
    //   the cannon's own pieces gone: ArenaCannons.Find turns its SPFN and ball slot into a jump pad to its landing
    //   no spider ball (EchoesArena skips the spider track flags here; the BALWs and the hint go)
    // The five doors are sealed with Echoes' lock look as Mining Plaza's (door closed, Door Lock, shield, own box; the
    // two Missile Locks too). The morph ball tunnels are blocked (owner 2026-11-16, in place of the alt form hop first
    // asked for: "block off the morph ball tunnels ... plug them with crates"): each mouth, found from the collision
    // (TunnelPlugs), gets Sanctuary's Cliff Crate, solid by its box.
    internal static class MainGyroChamber
    {
        public const string Key = "M08_MainGyroChamber";
        public const string MusicKey = TrackCatalog.Kind + ":MP2SanctuaryFortress";

        const uint OuterRing = 0x0024001d, Elevator = 0x00240203, ElevatorI = 0x002401e9, EchoGate = 0x1c240358, SpinnerDoor = 0x00240006;
        static readonly uint[] Doors = { 0x00240003, 0x00240090, 0x00240095, 0x0024009b, 0x002400f4 };
        static readonly uint[] MissileLocks = { 0x0024004e, 0x00240102 };
        const uint CrateModel = 0x120ae46a;   // Sanctuary's Cliff Crate (Metroid5.pak)
        static readonly uint[] Dropped =
        {
            0x00240017,                                         // Center Sphere
            0x08240111, 0x08240088,                             // the L2 pickup's platform and pickup
            0x08240061, 0x08240069, 0x0824006b, 0x0824006c,     // the morph ball cannon's pieces
            0x1c240329, 0x1c24033f, 0x1c24034b, 0x1c240351,     // the Echo Gate's Echo Locks
            0x002402d1,                                         // HINT 'Spiderball'
            0x00240089, 0x0024008d, 0x00240154, 0x00240153,     // the cannon's and the ring's glass, whole and broken
            0x002402bc,                                         // 'Window behind first puzzle'
        };
        // each elevator: its frozen bottom (Echoes z), its top stop (Echoes z) and its model's footprint middle (Echoes x, y)
        static readonly (uint Id, string Name, float Down, float Top, Vector2 Middle)[] Elevators =
        {
            (Elevator, "Elevator", -11.2f, 3.2f, new Vector2(62.46f, 216.43f)),
            (ElevatorI, "Elevator I", -34.8f, -11.2f, new Vector2(74.46f, 216.43f)),
        };
        const float ElevatorHalf = 5;
        static readonly string[] LightFx = { "Glow", "holo" };   // Echoes metres: the platforms are 10 x 10

        const uint LockAncs = 0x9486a701, ShieldModel = 0x6b78fd92;
        const string LockClip = "closed_lock";
        const uint AnimParamsId = 0xe25fb08c, BoxId = 0xf344c0b0, OffsetId = 0x2e686c2a, ModelId = 0xc27ffa8f, DclnId = 0x0fc966dc;
        const uint SolidId = 0x1d8dd846;

        static Mrea.ScriptObject? Obj(List<Mrea.ScriptObject> scripts, uint id) => scripts.FirstOrDefault(o => o.Id == id);

        static Vector3? Vec(Mrea.ScriptObject o, uint prop) =>
            o.Root.Find(prop) is { Data.Length: >= 12 } p ? new Vector3(Be.F32(p.Data, 0), Be.F32(p.Data, 4), Be.F32(p.Data, 8)) : null;

        // ---- the frozen room ----

        static Mrea.Property? Editor(Mrea.ScriptObject o, uint id) => o.Root.Find(Mrea.ScriptObject.EditorPropertiesId)?.Find(id);

        static void SetXf(Mrea.ScriptObject o, int index, float v)
        {
            if (Editor(o, Mrea.ScriptObject.TransformId) is { Data.Length: >= 36 } x) BinaryPrimitives.WriteSingleBigEndian(x.Data.AsSpan(4 * index), v);
        }

        static void Enable(Mrea.ScriptObject o, bool on)
        {
            if (Editor(o, Mrea.ScriptObject.ActiveId) is { Data.Length: >= 1 } a) a.Data[0] = (byte)(on ? 1 : 0);
            if (on) o.Layer = 0;
        }

        public static void Freeze(List<Mrea.ScriptObject> scripts, List<string> notes)
        {
            var done = new List<string>();
            void At(uint id, string what, Action<Mrea.ScriptObject> f)
            {
                if (Obj(scripts, id) is { } o) { f(o); done.Add(what); }
                else notes.Add($"Main Gyro Chamber: {what} ({id:x8}) not in the room");
            }
            // the outer ring on its Lower gyro waypoint (00240014/20 cycle it, 00240270/72 slow it to this)
            At(OuterRing, "outer ring at rest", o => { SetXf(o, 0, 68.46f); SetXf(o, 1, 277.299f); SetXf(o, 2, -12.234f); });
            foreach (var e in Elevators) At(e.Id, $"{e.Name} down", o => SetXf(o, 2, e.Down));
            At(EchoGate, "Echo Gate down", o => { SetXf(o, 0, 73.2f); SetXf(o, 1, 298.9f); SetXf(o, 2, -42.8f); Enable(o, true); });
            At(SpinnerDoor, "spinner door open", o => SetXf(o, 3, 135));
            int dropped = scripts.RemoveAll(o => Array.IndexOf(Dropped, o.Id) >= 0 || o.Type == "BALW");
            notes.Add($"Main Gyro Chamber: frozen ({string.Join(", ", done)}); {dropped} objects dropped (sphere, L2 pickup, cannon pieces, Echo Locks, spider walls, hint, glass)");
        }

        // ---- collision ----

        public static void Seals(Pak pak, List<Mrea.ScriptObject> scripts, Func<float, float, float, Vector3> map,
            IReadOnlyList<float[]> plugs, Action<Vector3, Vector3, Vector3, Mrea.Surface> solid, List<string> notes)
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
            bool OwnBox(uint id, Mrea.Surface s)
            {
                if (Obj(scripts, id) is not { } o || Vec(o, BoxId) is not Vector3 size || size.X * size.Y * size.Z <= 0) return false;
                Vector3 off = Vec(o, OffsetId) ?? Vector3.Zero;
                Box(o.Transform, off - size / 2, off + size / 2, s);
                return true;
            }
            foreach (uint d in Doors.Concat(MissileLocks))
            {
                if (!OwnBox(d, Mrea.Surface.Metal)) notes.Add($"Main Gyro Chamber: {d:x8} has no collision box; left open");
            }
            if (plugs.Count > 0)
            {
                (Vector3 lo, Vector3 hi) = Bounds(Cmdl.Load(pak.Get, CrateModel));
                foreach (float[] xf in plugs) Box(xf, lo, hi, Mrea.Surface.Metal);
            }
            notes.Add($"Main Gyro Chamber: {count} sealing collision triangles (5 doors, 2 Missile Locks, {plugs.Count} tunnel crates)");
            int before = count;
            var solidOnes = new List<string>();
            foreach (Mrea.ScriptObject o in RoomActors(scripts))
            {
                if (o.Root.Find(DclnId) is not { Data.Length: >= 4 } dp || dp.U32 == uint.MaxValue) continue;
                if (o.Type == "ACTR" && o.Root.Find(SolidId) is not { Data: [not 0, ..] }) continue;
                if (MeshTris(pak, o, dp.U32, map) is { } tris)
                {
                    foreach ((Vector3 a, Vector3 b, Vector3 c, Vector3 mid) in tris) Tri(a, b, c, mid, Mrea.Surface.Metal);
                    solidOnes.Add($"{o.Name} {o.Id:x8}");
                }
                else notes.Add($"Main Gyro Chamber: {o.Name} {o.Id:x8} collision {dp.U32:x8} not read; not solid");
            }
            notes.Add($"Main Gyro Chamber: {count - before} room actor collision triangles ({string.Join(", ", solidOnes)})");
        }

        // a collision mesh (DCLN) at an object's transform as MPH triangles with the mesh's middle (as MiningPlaza.MeshTris)
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

        // the room's active default-layer actors and platforms (after Freeze: the frozen ones are among them)
        static IEnumerable<Mrea.ScriptObject> RoomActors(List<Mrea.ScriptObject> scripts) =>
            scripts.Where(o => o.Active && o.Layer == 0 && o.Type is "ACTR" or "PLAT" && Array.IndexOf(MissileLocks, o.Id) < 0);

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

        public static void Looks(Pak pak, List<Mrea.ScriptObject> scripts, IReadOnlyList<float[]> plugs, Func<float, float, float, Vector3> map,
            ArenaRaycaster ray, IReadOnlyList<Mrea.Light> lights, EchoesArena.Info info, List<ArenaHdWriter.Placed> placed)
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
            void Model(Mrea.ScriptObject o, uint model, string name)
            {
                if (!pak.Contains(model)) return;
                (Vector3 lo, Vector3 hi) = Bounds(Cmdl.Load(pak.Get, model));
                Add(name, new ArenaHdWriter.Placed(model, o.Transform, 0), ArenaHdWriter.Place(o.Transform, (lo + hi) / 2));
            }
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
            void Door(Mrea.ScriptObject o)
            {
                if (o.Root.Find(AnimParamsId) is not { Data.Length: >= 12 } ap) return;
                int clip = (int)Be.U32(ap.Data, 8);
                Posed(o, Be.U32(ap.Data, 0), (int)Be.U32(ap.Data, 4), s => clip < s.Anims.Count ? s.Anims[clip].Anim : null, $"{o.Name} door {o.Id:x8}");
            }
            foreach (uint d in Doors)
            {
                if (Obj(scripts, d) is not { } o) continue;
                Door(o);
                Posed(o, LockAncs, 0, s => s.Anims.FirstOrDefault(a => a.Name == LockClip).Anim, $"Door Lock {o.Id:x8}");
                Model(o, ShieldModel, $"Door Shield {o.Id:x8}");
            }
            foreach (uint id in MissileLocks)
            {
                if (Obj(scripts, id) is { } o && o.Root.Find(ModelId) is { Data.Length: >= 4 } mp && mp.U32 != uint.MaxValue) Model(o, mp.U32, $"{o.Name} {o.Id:x8}");
            }
            if (plugs.Count > 0)
            {
                (Vector3 lo, Vector3 hi) = Bounds(Cmdl.Load(pak.Get, CrateModel));
                for (int i = 0; i < plugs.Count; i++)
                    Add($"Tunnel Crate {i + 1}", new ArenaHdWriter.Placed(CrateModel, plugs[i], 0), ArenaHdWriter.Place(plugs[i], (lo + hi) / 2));
            }
            info.Notes.Add($"Main Gyro Chamber: {drawn.Count} sealing models drawn: {string.Join(", ", drawn)}");
            drawn.Clear();
            foreach (Mrea.ScriptObject o in RoomActors(scripts))
            {
                if (LightFx.Any(n => o.Name.Contains(n, StringComparison.OrdinalIgnoreCase))) continue;   // (light effects, as AddStatics)
                string name = $"{o.Name} {o.Id:x8}";
                if (o.Root.Find(AnimParamsId) is { Data.Length: >= 12 } ap && Be.U32(ap.Data, 0) is uint ancs && ancs != uint.MaxValue && pak.Contains(ancs))
                {
                    int clip = (int)Be.U32(ap.Data, 8);
                    Posed(o, ancs, (int)Be.U32(ap.Data, 4), s => clip < s.Anims.Count ? s.Anims[clip].Anim : null, name);
                }
                else if (o.Root.Find(ModelId) is { Data.Length: >= 4 } mp && mp.U32 != uint.MaxValue) Model(o, mp.U32, name);
            }
            info.Notes.Add($"Main Gyro Chamber: {drawn.Count} room actor models drawn: {string.Join(", ", drawn)}");
        }

        // ---- spawns ----

        // eight stand-in spawns (the owner places the real ones with the placer): the room's SPWNs, each door's room side,
        // then rings round the chamber's middle
        public static List<(Vector3 Pos, Vector3 Facing)> StandInSpawns(List<Mrea.ScriptObject> scripts, Func<float, float, float, Vector3> map,
            ArenaRaycaster ray)
        {
            var res = new List<(Vector3, Vector3)>();
            void Try(Vector3 p, Vector3 facing)
            {
                if (res.Count >= 8 || ray.FloorBelow(p + Vector3.UnitY * 0.3f, 6f) is not Vector3 f) return;
                if (res.Any(r => (r.Item1 - f).LengthSquared < 4)) return;
                if (ray.Cast(f + Vector3.UnitY * 0.1f, f + Vector3.UnitY * 1.9f) != null) return;
                res.Add((f, facing));
            }
            foreach (Mrea.ScriptObject o in scripts.Where(o => o.Type == "SPWN" && o.Active))
            {
                float[] x = o.Transform;
                float yaw = MathHelper.DegreesToRadians(x[5]);
                Try(map(x[0], x[1], x[2]), map(-MathF.Sin(yaw), MathF.Cos(yaw), 0).Normalized());
            }
            foreach (float step in new[] { 4f, 9f })
            {
                foreach (uint d in Doors)
                {
                    if (Obj(scripts, d) is not { } o) continue;
                    Vector3 a = ArenaHdWriter.Place(o.Transform, new Vector3(-step, 0, 1)), b = ArenaHdWriter.Place(o.Transform, new Vector3(-step - 1, 0, 1));
                    Vector3 pa = map(a.X, a.Y, a.Z), pb = map(b.X, b.Y, b.Z);
                    Vector3 facing = pb - pa;
                    facing.Y = 0;
                    Try(pa, facing.LengthSquared > 0 ? facing.Normalized() : Vector3.UnitZ);
                }
            }
            Vector3 middle = map(68.5f, 277.3f, -11.2f);
            foreach (float r in new[] { 14f, 20f, 26f })
            {
                for (int k = 0; k < 8; k++)
                {
                    float a = k * MathF.PI / 4;
                    Vector3 p = map(68.5f + r * MathF.Cos(a), 277.3f + r * MathF.Sin(a), -11.2f + 1);
                    Vector3 facing = middle - p;
                    facing.Y = 0;
                    Try(p, facing.LengthSquared > 0 ? facing.Normalized() : Vector3.UnitZ);
                }
            }
            return res;
        }

        // ---- the elevators' jump pads ----

        // a pad on each frozen elevator to where its ride ends: the floor nearest its shaft at its top stop (Tune solves
        // the launch once the arena loads)
        public static List<ArenaCannons.Cannon> ElevatorPads(List<Mrea.ScriptObject> scripts, ArenaRaycaster ray,
            Func<float, float, float, Vector3> map, List<string> notes)
        {
            var res = new List<ArenaCannons.Cannon>();
            static float[] F(Vector3 v) => new[] { v.X, v.Y, v.Z };
            foreach (var e in Elevators)
            {
                if (Obj(scripts, e.Id) == null) continue;
                Vector3 above = map(e.Middle.X, e.Middle.Y, e.Down + 2);
                if (ray.FloorBelow(above, 4) is not Vector3 pad)
                {
                    notes.Add($"Main Gyro Chamber: {e.Name}: no floor on the frozen platform; no pad");
                    continue;
                }
                Vector3 c = map(e.Middle.X, e.Middle.Y, e.Top);
                float half = (map(ElevatorHalf, 0, 0) - map(0, 0, 0)).Length;
                Vector3? best = null;
                for (float r = half + 1.5f; r <= half + 16 && best == null; r += 1)
                {
                    Vector3? fallback = null;
                    for (int k = 0; k < 16; k++)
                    {
                        float a = k * MathF.PI / 8;
                        Vector3 p = c + new Vector3(MathF.Cos(a), 0, MathF.Sin(a)) * r;
                        if (ray.FloorBelow(p + Vector3.UnitY * 1.0f, 1.6f) is not Vector3 f || MathF.Abs(f.Y - c.Y) > 0.6f) continue;
                        if (ray.Cast(f + Vector3.UnitY * 0.1f, f + Vector3.UnitY * 1.9f) != null) continue;
                        // reached from the ride's top stop (Elevator I's shaft is walled up to its ceiling but one side)
                        if (ray.Cast(c + Vector3.UnitY * 1.0f, f + Vector3.UnitY * 1.0f) != null) continue;
                        if (ArenaCannons.Room(ray, f) >= 2) { best = f; break; }
                        fallback ??= f;
                    }
                    best ??= fallback;
                }
                if (best is not Vector3 target)
                {
                    notes.Add($"Main Gyro Chamber: {e.Name}: no floor at its top stop; no pad");
                    continue;
                }
                res.Add(new ArenaCannons.Cannon { Name = e.Name, Rule = "elevator top stop", Slot = F(pad), Pad = F(pad), Marker = F(target), Target = F(target) });
                notes.Add($"Main Gyro Chamber: {e.Name} pad at ({pad.X:0.0}, {pad.Y:0.0}, {pad.Z:0.0}) to ({target.X:0.0}, {target.Y:0.0}, {target.Z:0.0})");
            }
            return res;
        }

        // ---- the window ----

        // the Window behind first puzzle (002402bc, dropped) has a curved shell ~1 m thick in the area's own collision
        // (faces at Echoes y 304.3-307.1, open room north of it): its triangles go too, so the hole can be jumped through
        static readonly Vector3 WindowLo = new(54.5f, 303.3f, -11.2f), WindowHi = new(82.5f, 307.6f, -1.2f);

        public static bool WindowHole(Mrea.Collision col, int t, Mrea.Surface surface, List<string> notes)
        {
            Vector3 mid = Vector3.Zero;
            for (int k = 0; k < 3; k++)
            {
                int i = col.Triangles[3 * t + k];
                mid += new Vector3(col.Vertices[3 * i], col.Vertices[3 * i + 1], col.Vertices[3 * i + 2]) / 3;
            }
            if (mid.X < WindowLo.X || mid.Y < WindowLo.Y || mid.Z < WindowLo.Z
                || mid.X > WindowHi.X || mid.Y > WindowHi.Y || mid.Z > WindowHi.Z) return false;
            string key = $"Main Gyro Chamber: window hole, {surface} triangles dropped: ";
            int j = notes.FindIndex(n => n.StartsWith(key));
            if (j < 0) notes.Add(key + "1");
            else notes[j] = key + (int.Parse(notes[j][key.Length..]) + 1);
            return true;
        }

        // ---- the morph ball tunnels ----

        // a Cliff Crate (Echoes transform) in each tunnel mouth. Tunnel cells (MPH units, a Step grid): a floor of the area's
        // own collision with a ceiling 0.6-1.5 units over it (a biped can't stand there; the room's tunnels are 2 Echoes
        // metres, ~1.19 units). A mouth cell is one whose neighbour, at its floor's height, isn't tunnel and is open (clear
        // sideways for Clear: a side wall is nearer); the mouth cells of a big enough tunnel, grouped, get crates across them,
        // the front face at the open edge, the row as wide as the group and as tall as the tunnel (each end dug into the
        // walls), split so no crate is much wider than it is tall. Both ends of each tunnel (and the side the Window behind
        // first puzzle closed) get crates, so no stub is left to hide in
        public static List<float[]> TunnelPlugs(Pak pak, ArenaRaycaster ray, Func<float, float, float, Vector3> map, List<string> notes)
        {
            var plugs = new List<float[]>();
            if (!pak.Contains(CrateModel))
            {
                notes.Add($"Main Gyro Chamber: crate model {CrateModel:x8} not in the pak; tunnels left open");
                return plugs;
            }
            const float Step = 0.3f, Depth = 1.0f, Side = 0.5f, Clear = 0.9f, Wide = 1.6f;
            const int MinTunnel = 60;
            float scale = map(1, 0, 0).X;
            var cells = new Dictionary<(int X, int Z), List<(float Floor, float Ceil)>>();
            int x0 = (int)MathF.Floor(ray.Min.X / Step), x1 = (int)MathF.Ceiling(ray.Max.X / Step);
            int z0 = (int)MathF.Floor(ray.Min.Z / Step), z1 = (int)MathF.Ceiling(ray.Max.Z / Step);
            float top = ray.Max.Y + 1, bottom = ray.Min.Y - 1;
            for (int ix = x0; ix <= x1; ix++)
            {
                for (int iz = z0; iz <= z1; iz++)
                {
                    float y = top;
                    for (int guard = 0; guard < 64 && y > bottom; guard++)
                    {
                        var from = new Vector3(ix * Step, y, iz * Step);
                        if (ray.Cast(from, new Vector3(from.X, bottom, from.Z)) is not { } h) break;
                        float hy = y + (bottom - y) * h.T;
                        y = hy - 0.05f;
                        if (h.Normal.Y < 0.7f) continue;
                        var f = new Vector3(from.X, hy, from.Z);
                        if (ray.Cast(f + Vector3.UnitY * 0.05f, f + Vector3.UnitY * 1.5f) is not { } up || up.Normal.Y > -0.7f) continue;
                        float gap = 0.05f + 1.45f * up.T;
                        if (gap < 0.6f) continue;
                        if (!cells.TryGetValue((ix, iz), out var list)) cells[(ix, iz)] = list = new();
                        list.Add((hy, hy + gap));
                    }
                }
            }
            static bool Near(float a, float b) => MathF.Abs(a - b) < 0.4f;
            var all = new List<(int X, int Z, float Floor, float Ceil)>();
            var index = new Dictionary<(int, int, int), int>();
            foreach (((int X, int Z) k, List<(float Floor, float Ceil)> l) in cells)
            {
                for (int j = 0; j < l.Count; j++)
                {
                    index[(k.X, k.Z, j)] = all.Count;
                    all.Add((k.X, k.Z, l[j].Floor, l[j].Ceil));
                }
            }
            int Find(int x, int z, float floor) =>
                cells.TryGetValue((x, z), out var l) && l.FindIndex(c => Near(c.Floor, floor)) is int j and >= 0 ? index[(x, z, j)] : -1;
            // the tunnels: cells joined side by side at about the same floor
            int[] comp = Enumerable.Repeat(-1, all.Count).ToArray();
            var sizes = new List<int>();
            (int, int)[] four = { (1, 0), (-1, 0), (0, 1), (0, -1) };
            for (int i = 0; i < all.Count; i++)
            {
                if (comp[i] >= 0) continue;
                int c = sizes.Count, n = 0;
                var todo = new Stack<int>();
                todo.Push(i);
                comp[i] = c;
                while (todo.Count > 0)
                {
                    int k = todo.Pop();
                    n++;
                    foreach ((int dx, int dz) in four)
                    {
                        int j = Find(all[k].X + dx, all[k].Z + dz, all[k].Floor);
                        if (j >= 0 && comp[j] < 0) { comp[j] = c; todo.Push(j); }
                    }
                }
                sizes.Add(n);
            }
            // the mouth cells: a side open at the cell's floor
            var mouths = new List<(int Cell, Vector2 Dir)>();
            for (int i = 0; i < all.Count; i++)
            {
                if (sizes[comp[i]] < MinTunnel) continue;
                var p = new Vector3(all[i].X * Step, all[i].Floor + 0.4f, all[i].Z * Step);
                foreach ((int dx, int dz) in four)
                {
                    if (Find(all[i].X + dx, all[i].Z + dz, all[i].Floor) >= 0) continue;
                    if (ray.Cast(p, p + new Vector3(dx, 0, dz) * Clear) != null) continue;
                    mouths.Add((i, new Vector2(dx, dz)));
                }
            }
            // grouped: mouth cells near each other (two cells or less) at about the same floor, facing about the same way
            int[] group = Enumerable.Repeat(-1, mouths.Count).ToArray();
            var groups = new List<List<int>>();
            for (int i = 0; i < mouths.Count; i++)
            {
                if (group[i] >= 0) continue;
                var g = new List<int> { i };
                group[i] = groups.Count;
                for (int k = 0; k < g.Count; k++)
                {
                    var a = all[mouths[g[k]].Cell];
                    for (int j = 0; j < mouths.Count; j++)
                    {
                        var b = all[mouths[j].Cell];
                        if (group[j] >= 0 || Math.Abs(a.X - b.X) > 2 || Math.Abs(a.Z - b.Z) > 2 || !Near(a.Floor, b.Floor)
                            || Vector2.Dot(mouths[g[k]].Dir, mouths[j].Dir) < 0) continue;
                        group[j] = groups.Count;
                        g.Add(j);
                    }
                }
                groups.Add(g);
            }
            (Vector3 clo, Vector3 chi) = Bounds(Cmdl.Load(pak.Get, CrateModel));
            Vector3 csize = chi - clo, cmid = (clo + chi) / 2;
            var said = new List<string>();
            foreach (List<int> g in groups)
            {
                if (g.Count < 2) continue;   // (a lone open side: a notch, not a mouth)
                Vector2 d = g.Aggregate(Vector2.Zero, (v, m) => v + mouths[m].Dir).Normalized();
                var across = new Vector2(-d.Y, d.X);
                var pts = g.Select(m => new Vector2(all[mouths[m].Cell].X, all[mouths[m].Cell].Z) * Step).ToList();
                float aMin = pts.Min(v => Vector2.Dot(v, across)), aMax = pts.Max(v => Vector2.Dot(v, across));
                float front = pts.Max(v => Vector2.Dot(v, d)) + Step / 2;
                float width = aMax - aMin + Step + 2 * Side, start = aMin - Step / 2 - Side;
                float floor = g.Min(m => all[mouths[m].Cell].Floor) - 0.05f, ceil = g.Max(m => all[mouths[m].Cell].Ceil) + 0.05f;
                int n = Math.Max(1, (int)MathF.Ceiling(width / Wide));
                float w = width / n;
                // Echoes: x = X, y = -Z, z = Y (metres); the crate's own x across the mouth, y along it, z up
                var dirE = new Vector2(across.X, -across.Y);
                float rot = MathHelper.RadiansToDegrees(MathF.Atan2(dirE.Y, dirE.X));
                var size = new Vector3(w, Depth, ceil - floor) / scale;
                for (int k = 0; k < n; k++)
                {
                    Vector2 mid2 = across * (start + w * (k + 0.5f)) + d * (front - Depth / 2);
                    var xf = new float[] { 0, 0, 0, 0, 0, rot, size.X / csize.X, size.Y / csize.Y, size.Z / csize.Z };
                    var centre = new Vector3(mid2.X / scale, -mid2.Y / scale, (floor + ceil) / 2 / scale);
                    Vector3 off = ArenaHdWriter.Place(xf, cmid);
                    xf[0] = centre.X - off.X; xf[1] = centre.Y - off.Y; xf[2] = centre.Z - off.Z;
                    plugs.Add(xf);
                }
                Vector2 c2 = across * (start + width / 2) + d * (front - Depth / 2);
                said.Add($"({c2.X:0.0}, {floor:0.0}, {c2.Y:0.0}) {width:0.0} wide in {n}, {g.Count} cells, facing ({d.X:0.0}, {d.Y:0.0})");
            }
            notes.Add($"Main Gyro Chamber: {all.Count} tunnel cells in {sizes.Count} pieces ({sizes.Count(n => n >= MinTunnel)} tunnels, "
                + $"{string.Join("/", sizes.Where(n => n >= MinTunnel).OrderByDescending(n => n))} cells); {mouths.Count} mouth cells; "
                + $"{plugs.Count} crates: {string.Join("; ", said)}");
            return plugs;
        }
    }
}
