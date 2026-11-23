using System;
using System.Collections.Generic;
using System.Linq;
using MphRead;
using MphRead.Entities;
using MphRead.Formats.Collision;
using MphRead.Formats.Culling;
using MphRecomp.Import.Retro;
using OpenTK.Mathematics;

namespace MphRecomp.Arenas
{
    // Main Quarry's crane, the wall it breaks and the scan panel that swings it (owner 2026-11-16: "the crane moves ...
    // first move breaks the wall ... then it swings back and forth"). Prime's chain: a shot on the upper booth's panel
    // (Actor 0002052f) -> Relay Rotate Crane 00020244 -> Switch Rotate Left 00020241 (ANCS f5504443 clip 0
    // '01Crane_rotate_left') -> Timer Wall Hit Delay 0002024e (6 s) -> Relay Destroy Wall 00020246 (Unbroken 00020017 off,
    // Broken 00020018 on, not solid). The clip turns the whole crane about its origin by its CraneShaft bone: 0 -> 102.5
    // degrees in 7.2 s (the keys below, 1.2 s apart), and the right clip back; the wire leans ~14 degrees as the arm turns,
    // kicks back when it stops and settles with the magnet by the clip's end (~9.6 s). Here the animated crane (Actor
    // 00020020, Prime's script swaps it in for the still Crane Right) is drawn as its three bones' rigid parts posed from
    // both clips (MainQuarry.CraneParts), and the still crane's DCLN turns by the shaft's yaw: the first shot swings it (the
    // wall breaks when the magnet first touches its box, ~7.2 s, not at Prime's 6 s timer, which fires with it ~5.8 short)
    // left through the wall, each later one to the other side (shots before the clip ends do nothing). The host's sim runs it (replays repeat it); LAN/online guests mirror the host (Net/NetWorld.cs: up =
    // the wall standing, breaks = swings started). Its sounds and effects are the room's own (Prime1Fx.Import's moreFx /
    // moreSfx: Fx, Sfx below; CampaignRenderer.QuarryFx.cs plays them): Sound Crane Turn 000202bc (looped while it turns),
    // Crane Stop 000202b9, Destroy Wall 000202e8 (0.5 s after the hit), the two Dust Effects 0002004c / 00020051 and the
    // five Wall Pieces (DebrisExtended 00020029-2c, 4b, flown as the crates' pieces). The camera shake (CameraShaker
    // 00020210: 0.2 for 0.4 s) is the sim's (it draws on the sim's random numbers, as CrashPillar's). The bots' graph keeps
    // the crane at rest and the wall standing.
    internal static partial class MainQuarry
    {
        // the crane, the wall and the panel as entity collision and their passes; in the bots' graph as still solids (navOnly)
        static void CraneSeals(Pak pak, List<Scly1.Obj> objs, Func<float, float, float, Vector3> map, Action<Vector3, Vector3, Vector3> navOnly,
            EchoesArena.Info info)
        {
            if (Obj(objs, CraneRight) is not { } crane || Obj(objs, Wall) is not { } wall || Obj(objs, BoothScan) is null)
            {
                info.Notes.Add("Main Quarry: crane, wall or booth console not found; no moving crane");
                return;
            }
            float[] F(Action<Action<Vector3, Vector3, Vector3, Vector3>> shape, bool nav = true)
            {
                var f = new List<float>();
                shape((a, b, c, inside) =>
                {
                    Vector3 n = Vector3.Cross(b - a, c - a);
                    if (n.LengthSquared < 1e-10f) return;
                    if (Vector3.Dot(n, (a + b + c) / 3 - inside) < 0) (b, c) = (c, b);
                    f.AddRange(new[] { a.X, a.Y, a.Z, b.X, b.Y, b.Z, c.X, c.Y, c.Z });
                    if (nav) navOnly(a, b, c);
                });
                return f.ToArray();
            }
            float[] cx = Xf(crane);
            Vector3 pivot = map(cx[0], cx[1], cx[2]);
            var spot = new MainQuarryCrane.CraneSpot
            {
                Pivot = new[] { pivot.X, pivot.Y, pivot.Z },
                Tris = F(t => Shape(pak, crane, map, t)),
                WallTris = F(t => Shape(pak, wall, map, t)),
            };
            BoothConsole(spot, map);
            // the swing's way: the crane meets the wall 6 s into its first swing (89.4 degrees), so the turn that brings the
            // crane nearest the wall's box then is the left one
            (Vector3 wlo, Vector3 whi) = MainQuarryCrane.TriBox(spot.WallTris);
            float Nearest(float sign)
            {
                Matrix4 m = MainQuarryCrane.Model(pivot, sign * MainQuarryCrane.Curve(MainQuarryCrane.Left, MainQuarryCrane.Hit));
                float best = float.MaxValue;
                for (int k = 0; k + 2 < spot.Tris.Length; k += 3)
                {
                    Vector3 p = Vector3.TransformPosition(new Vector3(spot.Tris[k], spot.Tris[k + 1], spot.Tris[k + 2]), m);
                    best = MathF.Min(best, (Vector3.Clamp(p, wlo, whi) - p).Length);
                }
                return best;
            }
            float plus = Nearest(1), minus = Nearest(-1);
            spot.Sign = minus < plus ? -1 : 1;
            // the sounds' and the dust's places (MPH), the dust's sizes (Prime's scale)
            float[]? At(uint id)
            {
                if (Obj(objs, id) is not { } o) return null;
                float[] x = Xf(o);
                Vector3 m = map(x[0], x[1], x[2]);
                return new[] { m.X, m.Y, m.Z };
            }
            spot.TurnSound = At(TurnSound);
            spot.StopSound = At(StopSound);
            spot.WallSound = At(WallSound);
            spot.Dust = new();
            foreach (uint id in Dust)
            {
                if (Obj(objs, id) is not { } o) continue;
                float[] x = Xf(o);
                Vector3 m = map(x[0], x[1], x[2]);
                spot.Dust.Add(new[] { m.X, m.Y, m.Z, x[6], x[7], x[8] });
            }
            info.Crane = spot;
            info.Notes.Add($"Main Quarry: moving crane (pivot {pivot.X:0.##} {pivot.Y:0.##} {pivot.Z:0.##}), collision {spot.Tris.Length / 9}/"
                + $"{spot.WallTris.Length / 9}/{spot.PanelTris.Length / 9} triangles (crane/wall/panel); nearest the wall (box "
                + $"{wlo.X:0.#} {wlo.Y:0.#} {wlo.Z:0.#} .. {whi.X:0.#} {whi.Y:0.#} {whi.Z:0.#}) at the hit: {plus:0.##} turned +, {minus:0.##} turned - -> sign {spot.Sign}");
        }

        // the console about its scan point (POI 00020251 "Crane Active", Prime metres, world-aligned): its screens and its
        // body down to the booth floor (Prime z 27.7) as a beam-only box; the console's own collision stays the room's
        // (MainQuarryPanelEntity lets players through). Built from these numbers again when the arena loads (SetRoom), so
        // imports from before the console (vc 724 and older: the Scan Panel by the hex door) get it without a re-import
        static readonly Vector3 BoothConsoleAt = new(107.882f, 39.544f, 30.812f);

        internal static void BoothConsole(MainQuarryCrane.CraneSpot spot, Func<float, float, float, Vector3> map)
        {
            float[] px = { BoothConsoleAt.X, BoothConsoleAt.Y, BoothConsoleAt.Z, 0, 0, 0, 1, 1, 1 };
            var f = new List<float>();
            Box(px, new Vector3(-0.7f, -0.7f, 27.7f - px[2]), new Vector3(0.7f, 0.7f, 0.6f), map, (a, b, c, inside) =>
            {
                if (Vector3.Dot(Vector3.Cross(b - a, c - a), (a + b + c) / 3 - inside) < 0) (b, c) = (c, b);
                f.AddRange(new[] { a.X, a.Y, a.Z, b.X, b.Y, b.Z, c.X, c.Y, c.Z });
            });
            spot.PanelTris = f.ToArray();
            Vector3 m = map(px[0], px[1], px[2]);
            spot.PanelCenter = new[] { m.X, m.Y, m.Z };
        }

        // the crane, the standing wall and the broken one as the entity's own placed passes, lit at their middles
        static void CraneLooks(Pak pak, List<Scly1.Obj> objs, Func<float, float, float, Vector3> map, ArenaRaycaster ray,
            IReadOnlyList<Mrea.Light> lights, EchoesArena.Info info, List<ArenaHdWriter.Placed> placed)
        {
            if (info.Crane is not { } spot) return;
            int NextPass() => placed.Count == 0 ? ArenaHdWriter.FirstPlacedPass : Math.Max(ArenaHdWriter.FirstPlacedPass, placed.Max(p => p.Pass) + 1);
            (int Pass, float[]? Amb, List<float[]>? Lights) Add(uint id)
            {
                if (Obj(objs, id) is not { } o || !pak.Contains(ModelOf(o)))
                {
                    info.Notes.Add($"Main Quarry: {id:x8} model not found; not drawn");
                    return (-1, null, null);
                }
                float[] xf = Xf(o);
                (Vector3 lo, Vector3 hi) = Bounds(Cmdl.Load(pak.Get, ModelOf(o)));
                int pass = NextPass();
                placed.Add(new ArenaHdWriter.Placed(ModelOf(o), xf, pass));
                (float[] amb, List<float[]> lit) = EchoesCrates.Lighting(lights, ArenaHdWriter.Place(xf, (lo + hi) / 2), map, ray);
                return (pass, amb, lit);
            }
            if (CraneParts(pak, objs, map, info, placed, NextPass) is { } parts)
            {
                // the animated crane's parts, lit as the still crane would be (its model's middle)
                spot.Parts = parts;
                if (Obj(objs, CraneRight) is { } still && pak.Contains(ModelOf(still)))
                {
                    (Vector3 lo, Vector3 hi) = Bounds(Cmdl.Load(pak.Get, ModelOf(still)));
                    (spot.Ambient, spot.Lights) = EchoesCrates.Lighting(lights, ArenaHdWriter.Place(Xf(still), (lo + hi) / 2), map, ray);
                }
            }
            else (spot.Pass, spot.Ambient, spot.Lights) = Add(CraneRight);
            (spot.WallPass, spot.WallAmbient, spot.WallLights) = Add(Wall);
            (spot.BrokenPass, _, _) = Add(BrokenWall);
            // the wall's pieces: placed passes at no transform (the app flies them, as the crates' pieces)
            spot.WallDebris = new();
            spot.WallDebrisAt = new();
            spot.WallDebrisPasses = new();
            foreach (uint id in WallPieces)
            {
                if (Obj(objs, id) is not { } o || Prime1Room.PieceOf(o) is not { } d || !pak.Contains(d.Id) || pak.TypeOf(d.Id) != "CMDL") continue;
                float[] x = Xf(o);
                Vector3 m = map(x[0], x[1], x[2]);
                spot.WallDebris.Add(d);
                spot.WallDebrisAt.Add(new[] { m.X, m.Y, m.Z });
                if (!spot.WallDebrisPasses.ContainsKey(d.Id))
                {
                    spot.WallDebrisPasses[d.Id] = NextPass();
                    placed.Add(new ArenaHdWriter.Placed(d.Id, new float[] { 0, 0, 0, 0, 0, 0, 1, 1, 1 }, spot.WallDebrisPasses[d.Id]));
                }
            }
            info.Notes.Add($"Main Quarry: crane pass {spot.Pass} / parts {string.Join(" ", spot.Parts?.Select(p => $"{p.Bone}:{p.Pass}") ?? Array.Empty<string>())}, wall {spot.WallPass}, broken wall {spot.BrokenPass}; {spot.WallDebris.Count} wall "
                + $"pieces, {spot.Dust?.Count ?? 0} dust spots, sounds {(spot.TurnSound != null ? "turn " : "")}{(spot.StopSound != null ? "stop " : "")}{(spot.WallSound != null ? "wall" : "")}");
        }

        // the animated crane (Actor 00020020, at the still one's transform: Prime's script swaps the two): its character
        // split into its bones' rigid parts (arm, wire, magnet), each its own placed pass about the bone's bind position,
        // and each part's model matrix (MPH, row vectors) over both clips, every CraneStep s. Null when it doesn't read
        // (the still crane is drawn turned instead)
        const float CraneStep = 0.1f;

        static List<MainQuarryCrane.CranePart>? CraneParts(Pak pak, List<Scly1.Obj> objs, Func<float, float, float, Vector3> map,
            EchoesArena.Info info, List<ArenaHdWriter.Placed> placed, Func<int> nextPass)
        {
            MainQuarryCrane.CraneSpot spot = info.Crane!;
            if (Obj(objs, CraneAnimated) is not { Type: ActorType } actor)
            {
                info.Notes.Add("Main Quarry: animated crane 00020020 not found; the still crane turns");
                return null;
            }
            // the actor's AnimationParameters (after its static model)
            uint ancsId = U32At(actor, 200);
            int character = (int)U32At(actor, 204);
            if (!pak.Contains(ancsId)) { info.Notes.Add($"Main Quarry: crane animation set {ancsId:x8} not in the pak; the still crane turns"); return null; }
            try
            {
                Ancs ancs = Ancs.Parse(pak.Get(ancsId));
                if (character < 0 || character >= ancs.Characters.Count || ancs.Set == null) return null;
                Ancs.Character ch = ancs.Characters[character];
                if (!pak.Contains(ch.Cmdl) || !pak.Contains(ch.Cskr) || !pak.Contains(ch.Cinf)) return null;
                Import.Retro.Anim? Clip(string name)
                {
                    Ancs.MetaAnim? m = ancs.Set.Anims.FirstOrDefault(x => x.Name == name).Anim;
                    if (m == null) return null;
                    uint id = m.Kind == "Play" || m.Children.Count == 0 ? m.AnimId : Ancs.Prims(m).FirstOrDefault()?.AnimId ?? uint.MaxValue;
                    return pak.Contains(id) ? Import.Retro.Anim.Load(pak.Get(id), echoes: false) : null;
                }
                if (Clip("01Crane_rotate_left") is not { } left || Clip("01Crane_rotate_right") is not { } right)
                {
                    info.Notes.Add("Main Quarry: the crane's rotate clips not found; the still crane turns");
                    return null;
                }
                Cinf sk = Cinf.Load(pak.Get(ch.Cinf));
                Cmdl g = Cmdl.Load(pak.Get, ch.Cmdl, ch.Cskr);
                float[] ax = Xf(actor);
                float s = map(1, 0, 0).X - map(0, 0, 0).X;
                Vector3 Unmap(Vector3 v) => new(v.X / s, -v.Z / s, v.Y / s);
                static float R(float v) => MathF.Round(v, 5);
                float[] Matrix(double[] q, double[] p)
                {
                    Vector3 F(Vector3 local)
                    {
                        Vector3 e = Unmap(local);
                        double[] r = EchoesTurret.Rotate(q, new double[] { e.X, e.Y, e.Z });
                        Vector3 w = ArenaHdWriter.Place(ax, new Vector3((float)(r[0] + p[0]), (float)(r[1] + p[1]), (float)(r[2] + p[2])));
                        return map(w.X, w.Y, w.Z);
                    }
                    Vector3 o = F(Vector3.Zero), x = F(Vector3.UnitX) - o, y = F(Vector3.UnitY) - o, z = F(Vector3.UnitZ) - o;
                    return new[] { R(x.X), R(x.Y), R(x.Z), R(y.X), R(y.Y), R(y.Z), R(z.X), R(z.Y), R(z.Z), R(o.X), R(o.Y), R(o.Z) };
                }
                var bones = Enumerable.Range(0, g.TriangleCount).Select(t => ArenaHdWriter.TriangleBone(g, t))
                    .Where(b => sk.Bones.ContainsKey(b)).Distinct().OrderBy(b => b).ToList();
                if (bones.Count == 0) return null;
                float[] Sampled(Import.Retro.Anim an, uint bone)
                {
                    int n = Math.Max(1, (int)MathF.Ceiling((float)an.Duration / CraneStep));
                    var f = new List<float>(12 * (n + 1));
                    for (int i = 0; i <= n; i++)
                    {
                        (double[] q, double[] p) = EchoesTurret.Pose(sk, an, an.Duration * i / n)[bone];
                        f.AddRange(Matrix(q, p));
                    }
                    return f.ToArray();
                }
                var parts = new List<MainQuarryCrane.CranePart>();
                foreach (uint b in bones)
                {
                    Cinf.Bone bone = sk.Bones[b];
                    int pass = nextPass();
                    placed.Add(new ArenaHdWriter.Placed(ch.Cmdl, ax, pass) { Skin = ch.Cskr, Bone = b, Origin = new Vector3((float)bone.X, (float)bone.Y, (float)bone.Z) });
                    parts.Add(new MainQuarryCrane.CranePart { Bone = bone.Name ?? b.ToString(), Pass = pass, Left = Sampled(left, b), Right = Sampled(right, b) });
                }
                spot.LeftTime = (float)left.Duration;
                spot.RightTime = (float)right.Duration;
                // when the first swing meets the wall: Prime's timer (6 s) fires with the trailing magnet still short of it,
                // so the hit is the first moment a part's triangles reach the wall's box (else the nearest moment)
                (Vector3 wlo, Vector3 whi) = MainQuarryCrane.TriBox(spot.WallTris);
                var local = new List<(MainQuarryCrane.CranePart Part, Vector3 V)>();
                for (int k = 0; k < parts.Count; k++)
                {
                    Vector3 org = new((float)sk.Bones[bones[k]].X, (float)sk.Bones[bones[k]].Y, (float)sk.Bones[bones[k]].Z);
                    var seen = new HashSet<int>();
                    for (int t = 0; t < g.TriangleCount; t++)
                    {
                        if (ArenaHdWriter.TriangleBone(g, t) != bones[k]) continue;
                        for (int c = 0; c < 3; c++)
                        {
                            int pi = g.Triangles[3 * t + c].P;
                            if (!seen.Add(pi)) continue;
                            Vector3 d = new Vector3(g.Positions[3 * pi], g.Positions[3 * pi + 1], g.Positions[3 * pi + 2]) - org;
                            local.Add((parts[k], map(d.X, d.Y, d.Z)));
                        }
                    }
                }
                float Gap(float time)
                {
                    float best = float.MaxValue;
                    MainQuarryCrane.CranePart? last = null;
                    Matrix4 m = Matrix4.Identity;
                    foreach ((MainQuarryCrane.CranePart part, Vector3 v) in local)
                    {
                        if (part != last) { m = EchoesTurret.PieceMatrix(part.Left, time / spot.LeftTime); last = part; }
                        Vector3 w = Vector3.TransformPosition(v, m);
                        best = MathF.Min(best, (Vector3.Clamp(w, wlo, whi) - w).Length);
                    }
                    return best;
                }
                float hit = -1, nearAt = 0, near = float.MaxValue;
                for (float time = 0; time <= spot.LeftTime; time += 1 / 60f)
                {
                    float gap = Gap(time);
                    if (gap < near) (near, nearAt) = (gap, time);
                    if (gap <= 0.05f) { hit = time; break; }
                }
                spot.HitTime = hit >= 0 ? hit : nearAt;
                info.Notes.Add($"Main Quarry: the swinging crane meets the wall at {spot.HitTime:0.##} s ({(hit >= 0 ? "touch" : $"nearest, {near:0.##} away")}; gap at Prime's 6 s: {Gap(MainQuarryCrane.Hit):0.##})");
                // the clips' own events (sounds, particles)
                foreach ((string name, Ancs.MetaAnim? m) in ancs.Set.Anims.Where(x => x.Name.StartsWith("01Crane")).Select(x => (x.Name, (Ancs.MetaAnim?)x.Anim)))
                {
                    uint id = m == null ? uint.MaxValue : m.Kind == "Play" || m.Children.Count == 0 ? m.AnimId : Ancs.Prims(m).FirstOrDefault()?.AnimId ?? uint.MaxValue;
                    uint evnt = ancs.Set.Resources.FirstOrDefault(r => r.Anim == id).Evnt;
                    if (evnt == 0 || !pak.Contains(evnt)) { info.Notes.Add($"Main Quarry: crane clip {name}: no events"); continue; }
                    var ev = new List<Import.Effects.BodyFx.Event>();
                    var snd = new List<Import.Effects.BodyFx.Sound>();
                    Import.Effects.BodyFx.ReadEvnt(pak.Get(evnt), name, ev, snd);
                    info.Notes.Add($"Main Quarry: crane clip {name} events {evnt:x8}: sounds [{string.Join(", ", snd.Select(x => $"{x.Id}@{x.Time:0.##}"))}], particles [{string.Join(", ", ev.Select(x => $"{x.Part:x8}@{x.Time:0.##}"))}]");
                }
                // the arm's turn at the wall hit against the collision's (the still crane's DCLN turned by Sign * the yaw keys)
                Matrix4 a0 = EchoesTurret.PieceMatrix(parts[0].Left, 0), ah = EchoesTurret.PieceMatrix(parts[0].Left, MainQuarryCrane.Hit / spot.LeftTime);
                float Heading(Matrix4 m) => MathHelper.RadiansToDegrees(MathF.Atan2(-m.Row0.Z, m.Row0.X));
                float turned = Heading(ah) - Heading(a0);
                turned = (turned + 540) % 360 - 180;
                info.Notes.Add($"Main Quarry: animated crane {ancsId:x8} char {character}: {parts.Count} parts ({string.Join(", ", parts.Select(p => p.Bone))}), clips "
                    + $"{spot.LeftTime:0.##}/{spot.RightTime:0.##} s; arm turned {turned:0.#} deg at the hit, collision {spot.Sign * MainQuarryCrane.Curve(MainQuarryCrane.Left, MainQuarryCrane.Hit):0.#}");
                return parts;
            }
            catch (Exception e) when (e is InvalidOperationException or System.IO.InvalidDataException or IndexOutOfRangeException
                or ArgumentException or KeyNotFoundException)
            {
                info.Notes.Add($"Main Quarry: animated crane not read ({e.Message}); the still crane turns");
                return null;
            }
        }
    }

    public static class MainQuarryCrane
    {
        public sealed class CraneSpot
        {
            // MPH world: the crane's turning point (its origin), collision triangles facing out (9 floats each)
            public float[] Pivot { get; set; } = Array.Empty<float>();
            public float[] Tris { get; set; } = Array.Empty<float>();
            public float[] WallTris { get; set; } = Array.Empty<float>();
            public float[] PanelTris { get; set; } = Array.Empty<float>();
            public float[] PanelCenter { get; set; } = Array.Empty<float>();
            // the left swing's way about MPH's Y (+1 or -1: CraneSeals' check)
            public int Sign { get; set; } = 1;
            public int Pass { get; set; } = -1;
            public int WallPass { get; set; } = -1;
            public int BrokenPass { get; set; } = -1;
            public float[]? Ambient { get; set; }
            public List<float[]>? Lights { get; set; }
            public float[]? WallAmbient { get; set; }
            public List<float[]>? WallLights { get; set; }
            // the sounds' places (MPH), the dust effects (MPH place, Prime's scale x y z), the wall's pieces (as the crates':
            // their placed passes) and where each starts (MPH)
            public float[]? TurnSound { get; set; }
            public float[]? StopSound { get; set; }
            public float[]? WallSound { get; set; }
            public List<float[]>? Dust { get; set; }
            public List<TurretFx>? WallDebris { get; set; }
            public List<float[]>? WallDebrisAt { get; set; }
            public Dictionary<uint, int>? WallDebrisPasses { get; set; }
            // the animated crane's parts (MainQuarry.CraneParts; null: the still crane, Pass, turned) and its clips' lengths (s)
            public List<CranePart>? Parts { get; set; }
            public float LeftTime { get; set; }
            public float RightTime { get; set; }
            // the drawn crane's wall hit (s into the first swing; the parts' clip)
            public float HitTime { get; set; } = MainQuarryCrane.Hit;
        }

        // one rigid part of the animated crane: its placed pass and its model matrix (12 floats, MPH row vectors) every
        // MainQuarry.CraneStep s through each clip
        public sealed class CranePart
        {
            public string Bone { get; set; } = "";
            public int Pass { get; set; } = -1;
            public float[] Left { get; set; } = Array.Empty<float>();
            public float[] Right { get; set; } = Array.Empty<float>();
        }

        // the room's own effects and sounds for Prime1Fx.Import (role, PART id / game sfx id: Dust Effect's PART, the Sound
        // objects' ids; crane_clank is the rotate clips' own sound event, 6 s into each swing), and the Destroy Wall sound's
        // delay (s; after Prime's early timer, so none when the hit is the drawn crane's touch)
        public static readonly (string, uint)[] Fx = { ("wall_dust", 0xdd12b8a3u) };
        public static readonly (string, uint)[] Sfx = { ("crane_turn", 3548u), ("crane_stop", 4230u), ("wall_break", 3615u), ("crane_clank", 3539u) };
        public const float WallSoundDelay = 0.5f, ClankAt = 6f;
        // the wall's camera shake (CameraShaker 00020210: 0.2 for 0.4 s), full within ShakeNear m
        internal const float Shake = 0.2f, ShakeNear = 12f;
        internal const int ShakeFrames = 24;

        // the CraneShaft bone's yaw (seconds, degrees) in Prime's two clips (the collision turns by these; the clips run on
        // to ~9.6 s while the wire and magnet settle)
        internal static readonly float[][] Left =
        {
            new[] { 0f, 0f }, new[] { 1.2f, 2.8f }, new[] { 2.4f, 18.9f }, new[] { 3.6f, 39.9f }, new[] { 4.8f, 64.1f },
            new[] { 6.0f, 89.4f }, new[] { 7.2f, 102.5f },
        };
        internal static readonly float[][] Right =
        {
            new[] { 0f, 102.5f }, new[] { 1.19f, 102.4f }, new[] { 2.39f, 85.8f }, new[] { 3.58f, 61f }, new[] { 4.77f, 33.8f },
            new[] { 5.96f, 9.8f }, new[] { 7.16f, 0f },
        };
        // the swung-out yaw, and when the first swing breaks the wall (Timer Wall Hit Delay)
        internal const float Out = 102.5f, Hit = 6f;

        internal static float Curve(float[][] keys, float t)
        {
            if (t <= keys[0][0]) return keys[0][1];
            for (int i = 1; i < keys.Length; i++)
            {
                if (t <= keys[i][0])
                {
                    float[] a = keys[i - 1], b = keys[i];
                    return a[1] + (b[1] - a[1]) * (t - a[0]) / (b[0] - a[0]);
                }
            }
            return keys[^1][1];
        }

        // the crane's rest place to the turned one (row vectors: about the pivot)
        internal static Matrix4 Model(Vector3 pivot, float degrees)
        {
            if (degrees == 0) return Matrix4.Identity;
            return Matrix4.CreateTranslation(-pivot) * Matrix4.CreateRotationY(MathHelper.DegreesToRadians(degrees)) * Matrix4.CreateTranslation(pivot);
        }

        internal static (Vector3 Lo, Vector3 Hi) TriBox(float[] t)
        {
            Vector3 lo = new(float.MaxValue), hi = new(float.MinValue);
            for (int k = 0; k + 2 < t.Length; k += 3)
            {
                var v = new Vector3(t[k], t[k + 1], t[k + 2]);
                lo = Vector3.ComponentMin(lo, v);
                hi = Vector3.ComponentMax(hi, v);
            }
            return (lo, hi);
        }

        static readonly Dictionary<int, CraneSpot> _rooms = new();

        internal static void SetRoom(int roomId, EchoesArena.Info? info)
        {
            lock (_rooms)
            {
                if (info?.Crane is { } c)
                {
                    float scale = info.Scale > 0 ? info.Scale : EchoesArena.DefaultScale;
                    MainQuarry.BoothConsole(c, (x, y, z) => new Vector3(x, z, -y) * scale);
                    _rooms[roomId] = c;
                }
                else _rooms.Remove(roomId);
            }
        }

        // SceneSetup.HostExtraEntities: the crane and its panel after every other entity (any multiplayer match in the room)
        public static IReadOnlyList<EntityBase> Extra(Scene scene, int roomId, IReadOnlyList<EntityBase> entities)
        {
            CraneSpot? spot;
            lock (_rooms)
            {
                if (!GameState.Multiplayer || !_rooms.TryGetValue(roomId, out spot)) return entities;
            }
            var list = new List<EntityBase>(entities);
            int id = entities.Count == 0 ? 0 : entities.Max(e => e.Id) + 1;
            var crane = new MainQuarryCraneEntity(id, spot, MiningPlazaGate.Light(spot.Ambient, spot.Lights),
                MiningPlazaGate.Light(spot.WallAmbient, spot.WallLights), scene);
            list.Add(crane);
            if (spot.PanelTris.Length > 0) list.Add(new MainQuarryPanelEntity(id + 1, spot, crane, scene));
            return list;
        }
    }

    public sealed class MainQuarryCraneEntity : EnemyInstanceEntity
    {
        public MainQuarryCrane.CraneSpot Spot { get; }
        readonly CrateLight? _light, _wallLight;
        readonly Vector3 _pivot;
        // swings started (odd: out at rest), steps (60 Hz) into the current one (-1 at rest)
        int _swings, _t = -1;

        public bool WallUp { get; private set; } = true;
        public int Swings => _swings;
        // turning (the yaw keys); the wire and magnet swing on until the clip ends, and a shot waits for that
        public bool Moving => _t >= 0 && _t < Keys[^1][0] * 60;
        // seconds into the swing playing (its clip's events: crane_clank), -1 at rest
        public float SwingTime => _t < 0 ? -1 : _t / 60f;
        float[][] Keys => (_swings - 1 & 1) == 0 ? MainQuarryCrane.Left : MainQuarryCrane.Right;
        float ClipTime => Spot.Parts is { Count: > 0 } ? MathF.Max(Keys[^1][0], (_swings - 1 & 1) == 0 ? Spot.LeftTime : Spot.RightTime) : Keys[^1][0];
        public CrateLight? WallLight => _wallLight;

        public MainQuarryCraneEntity(int id, MainQuarryCrane.CraneSpot spot, CrateLight? light, CrateLight? wallLight, Scene scene)
            : base(new EnemyInstanceEntityData(EnemyType.HitZone, null!), NodeRef.None, scene)
        {
            Id = id;
            Spot = spot;
            _light = light;
            _wallLight = wallLight ?? light;
            _pivot = spot.Pivot.Length >= 3 ? new Vector3(spot.Pivot[0], spot.Pivot[1], spot.Pivot[2]) : Vector3.Zero;
            Position = _pivot;
        }

        protected override void EnemyInitialize()
        {
            NodeRef = _scene.GetNodeRefByName(ArenaModelWriter.RoomNode);
            Flags = EnemyFlags.NoMaxDistance | EnemyFlags.Static | EnemyFlags.Invincible;
            _hurtVolumeInit = _hurtVolume = new CollisionVolume(Position.AddY(-10000), 0);
            Transform = Matrix4.Identity;
            if (Spot.Tris.Length > 0)
            {
                SetCollision(MiningPlazaPillars.Shape(Spot.Tris, "quarry_crane", Terrain.Metal), 0);
                UpdateCollisionTransform(0, Matrix4.Identity);
                UpdateLinkedInverse(0);
            }
            if (Spot.WallTris.Length > 0)
            {
                SetCollision(MiningPlazaPillars.Shape(Spot.WallTris, "quarry_wall", Terrain.Rock), 1);
                UpdateCollisionTransform(1, Matrix4.Identity);
                UpdateLinkedInverse(1);
            }
        }

        public override bool GetTargetable() => false;

        protected override bool EnemyGetDrawInfo() => true;

        // the crane's yaw now (degrees, the left swing's way)
        public float Yaw
        {
            get
            {
                if (_t < 0) return (_swings & 1) != 0 ? MainQuarryCrane.Out : 0;
                return MainQuarryCrane.Curve(Keys, _t / 60f);
            }
        }

        Matrix4 CraneModel => MainQuarryCrane.Model(_pivot, Spot.Sign * Yaw);

        // the panel shot (host): a swing to the other side, unless one is under way
        public void Trigger()
        {
            if (_t >= 0) return;
            _swings++;
            _t = 0;
        }

        public override bool Process()
        {
            if (_t >= 0)
            {
                _t++;
                bool left = (_swings - 1 & 1) == 0;
                if (left && WallUp && _t >= (Spot.Parts is { Count: > 0 } ? Spot.HitTime : MainQuarryCrane.Hit) * 60 && !EchoesBreakables.Mirror) BreakWall(live: true);
                if (_t >= ClipTime * 60) _t = -1;
                Step();
            }
            if (_shake > 0)
            {
                _shake--;
                if (PlayerEntity.Main is { } me && Spot.WallSound is { Length: 3 } w)
                {
                    float d2 = (me.Position - new Vector3(w[0], w[1], w[2])).LengthSquared;
                    float near = MainQuarryCrane.ShakeNear * MainQuarryCrane.ShakeNear;
                    me.CameraInfo.SetShake(MainQuarryCrane.Shake * MathF.Min(1, near / MathF.Max(d2, 1)));
                }
            }
            return base.Process();
        }

        void Step()
        {
            if (EntityCollision[0] == null) return;
            UpdateLinkedInverse(0);
            UpdateCollisionTransform(0, CraneModel);
        }

        // live: seen breaking (the shake); a snap to a broken wall is still
        int _shake;
        void BreakWall(bool live)
        {
            WallUp = false;
            if (live) _shake = MainQuarryCrane.ShakeFrames;
            if (EntityCollision[1]?.Collision is { } c) c.Active = false;
        }

        // a network client's crane: the host's state (Net/NetWorld.cs). The first snapshot snaps to it; after that one swing
        // more starts it moving, anything else snaps
        bool _netSeen;
        public void NetApply(bool up, bool hit, int breaks)
        {
            int ahead = (breaks - (_swings & 63)) & 63;
            if (!_netSeen || ahead > 1)
            {
                _swings = breaks;
                _t = -1;
                Step();
            }
            else if (ahead == 1)
            {
                _swings = breaks;
                _t = 0;
            }
            if (!up && WallUp) BreakWall(live: _netSeen);
            _netSeen = true;
        }

        // what to draw now: the crane turned, the wall standing or broken (added to the caller's list, fade 1)
        public void AddPieces(List<(Matrix4 Model, int Pass, CrateLight? Light, float Fade)> into)
        {
            if (Spot.Parts is { Count: > 0 } parts)
            {
                // the clip under way at its time; at rest the end of the last one (the start of the left one before any)
                bool left = _swings == 0 || (_swings - 1 & 1) == 0;
                float f = _t >= 0 ? _t / 60f / MathF.Max(left ? Spot.LeftTime : Spot.RightTime, 0.01f) : _swings == 0 ? 0 : 1;
                foreach (MainQuarryCrane.CranePart p in parts)
                {
                    if (p.Pass >= 0) into.Add((EchoesTurret.PieceMatrix(left ? p.Left : p.Right, f), p.Pass, _light, 1));
                }
            }
            else if (Spot.Pass >= 0) into.Add((CraneModel, Spot.Pass, _light, 1));
            int wall = WallUp ? Spot.WallPass : Spot.BrokenPass;
            if (wall >= 0) into.Add((Matrix4.Identity, wall, _wallLight, 1));
        }
    }

    // the Control Tower booth's console: beams only (players pass; the console itself is the room's), and a beam on it swings the crane (the host's; guests see the swing mirrored)
    public sealed class MainQuarryPanelEntity : EnemyInstanceEntity
    {
        readonly MainQuarryCrane.CraneSpot _spot;
        readonly MainQuarryCraneEntity _crane;

        public MainQuarryPanelEntity(int id, MainQuarryCrane.CraneSpot spot, MainQuarryCraneEntity crane, Scene scene)
            : base(new EnemyInstanceEntityData(EnemyType.HitZone, null!), NodeRef.None, scene)
        {
            Id = id;
            _spot = spot;
            _crane = crane;
            float[] c = spot.PanelCenter;
            Position = c.Length >= 3 ? new Vector3(c[0], c[1], c[2]) : Vector3.Zero;
        }

        protected override void EnemyInitialize()
        {
            NodeRef = _scene.GetNodeRefByName(ArenaModelWriter.RoomNode);
            Flags = EnemyFlags.NoMaxDistance | EnemyFlags.Static | EnemyFlags.Invincible;
            _hurtVolumeInit = _hurtVolume = new CollisionVolume(Position.AddY(-10000), 0);
            Transform = Matrix4.Identity;
            SetCollision(MiningPlazaPillars.Shape(_spot.PanelTris, "quarry_panel", Terrain.Metal, players: false), 0);
            UpdateCollisionTransform(0, Matrix4.Identity);
            UpdateLinkedInverse(0);
        }

        public override bool GetTargetable() => false;

        protected override bool EnemyGetDrawInfo() => true;

        public override void HandleMessage(MessageInfo info)
        {
            if (info.Message == Message.BeamCollideWith && !EchoesBreakables.Mirror && info.Sender is BeamProjectileEntity beam
                && beam.Owner?.Type != EntityType.EnemyInstance)
            {
                _crane.Trigger();
            }
        }
    }
}
