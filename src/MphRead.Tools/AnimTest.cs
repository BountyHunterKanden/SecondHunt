using System;
using System.Collections.Generic;
using System.Linq;
using MphRead.Entities;
using MphRecomp.Anim;
using MphRecomp.Render;
using OpenTK.Mathematics;

namespace MphRead
{
    // Headless validation of the per-frame hunter animation port against REAL ROM data:
    //  1. SKINNING EQUIVALENCE -- for every hunter and a spread of poses (legs/torso layers, aim
    //     pitch), the skinned bake + the shader's math (world = (bone[slot] * local) * scale) must
    //     reproduce the proven CPU world-baked vertices, vertex for vertex (positions + normals).
    //     This pins down the bone-slot tagging, local-space extraction and palette layout before
    //     any GPU is involved.
    //  2. STATE MACHINE -- scripted intents must produce the game's clip sequence (spawn->idle,
    //     walk/strafe precedence, jump direction + hold, landing, flourish timing, turn, torso-only
    //     shooting with frame-synced hand-back) at the game's 30 fps frame cadence.
    // Run: MphRead.Tools.dll -animtest
    internal static class AnimTest
    {
        static int _pass, _fail;
        static void Check(string name, bool ok, string detail = "")
        {
            if (ok) _pass++; else _fail++;
            Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {name}{(detail.Length > 0 ? "  (" + detail + ")" : "")}");
        }

        static readonly Hunter[] Hunters = { Hunter.Samus, Hunter.Kanden, Hunter.Trace, Hunter.Sylux, Hunter.Noxus, Hunter.Spire, Hunter.Weavel };

        public static void Run(string[] args)
        {
            if (!ToolPaths.UseDesktopExtraction()) return;
            Console.WriteLine("-- skinning equivalence (skinned bake + palette == CPU world bake) --");
            foreach (Hunter h in Hunters) SkinningEquivalence(h);
            Console.WriteLine("-- biped state machine (Kanden) --");
            StateMachine();
            Console.WriteLine("-- landmark-solver maths --");
            {
                var rnd = new Random(7);
                Vector3 Rv() => new Vector3((float)rnd.NextDouble() * 2 - 1, (float)rnd.NextDouble() * 2 - 1, (float)rnd.NextDouble() * 2 - 1).Normalized();
                float worstFt = 0, worstRow = 0, worstHorn = 0, worstTw = 0, worstTwAxis = 0;
                for (int k = 0; k < 500; k++)
                {
                    var a = Rv(); var b = Rv();
                    var q = TrophyRigger.FromTo(a, b);
                    worstFt = MathF.Max(worstFt, (Vector3.Transform(a, q) - b).Length);
                    // Twist(axis, u, v): spins about the axis only, and brings u's across-axis part onto v's
                    var ax = Rv(); var tw = TrophyRigger.Twist(ax, a, b);
                    Vector3 Across(Vector3 v) => (v - ax * Vector3.Dot(ax, v)).Normalized();
                    worstTw = MathF.Max(worstTw, (Across(Vector3.Transform(a, tw)) - Across(b)).Length);
                    worstTwAxis = MathF.Max(worstTwAxis, (Vector3.Transform(ax, tw) - ax).Length);
                    // the row-vector matrix built from a quaternion must rotate exactly like the quaternion
                    worstRow = MathF.Max(worstRow, (Vector3.TransformVector(a, Matrix4.CreateFromQuaternion(q)) - Vector3.Transform(a, q)).Length);
                    var truth = Quaternion.FromAxisAngle(Rv(), (float)rnd.NextDouble() * 6f);
                    var ps = new[] { Rv(), Rv(), Rv() };
                    var h = TrophyRigger.Horn(ps.Select(p => (p, Vector3.Transform(p, truth))).ToList());
                    foreach (var p in ps) worstHorn = MathF.Max(worstHorn, (Vector3.Transform(p, h) - Vector3.Transform(p, truth)).Length);
                }
                Check("FromTo(a,b) maps a onto b", worstFt < 1e-4f, $"max err {worstFt:E1}");
                Check("CreateFromQuaternion row-vector matrix == quaternion rotation", worstRow < 1e-5f, $"max err {worstRow:E1}");
                Check("Horn recovers a random rotation from 3 vector pairs", worstHorn < 1e-3f, $"max err {worstHorn:E1}");
                Check("Twist(axis,a,b) aligns a's across-axis part with b's", worstTw < 1e-3f, $"max err {worstTw:E1}");
                Check("Twist leaves its axis fixed", worstTwAxis < 1e-5f, $"max err {worstTwAxis:E1}");
            }
            Console.WriteLine("-- DsSkeleton FK (retarget solver) == the game's AnimateNodes posing --");
            foreach (Hunter h in Hunters) FkParity(h);
            Console.WriteLine("-- every clip x every frame x every hunter (finite, sane extent, no frame pops) --");
            foreach (Hunter h in Hunters) ClipSweep(h);
            Console.WriteLine("-- hunter dummy on a flat floor (game movement constants) --");
            foreach (Hunter h in new[] { Hunter.Samus, Hunter.Kanden, Hunter.Spire }) Dummy(h);
            Console.WriteLine("-- charge / charged shot: upper body only, legs keep standing or walking --");
            foreach (Hunter h in new[] { Hunter.Samus, Hunter.Weavel }) Combat(h);
            Console.WriteLine($"\nANIM TESTS: {_pass} passed, {_fail} failed  =>  {(_fail == 0 ? "ALL GREEN" : "FAILURES")}");
            Environment.ExitCode = _fail == 0 ? 0 : 1;
        }

        static void SkinningEquivalence(Hunter h)
        {
            string name = Metadata.HunterModels[h][0];
            Model model = Read.GetModelInstance(name).Model;
            var anim = new BipedAnimator(model);
            var palette = new float[16 * 32];
            // (legs clip, legs frame, torso clip, torso frame, aim pitch Y)
            var poses = new (PlayerAnimation, int, PlayerAnimation, int, float)[]
            {
                (PlayerAnimation.Idle, 0, PlayerAnimation.Idle, 0, 0f),
                (PlayerAnimation.Idle, 20, PlayerAnimation.Idle, 20, 0f),
                (PlayerAnimation.WalkForward, 7, PlayerAnimation.WalkForward, 7, 0f),
                (PlayerAnimation.WalkLeft, 5, PlayerAnimation.Shoot, 4, 0.5f),
                (PlayerAnimation.JumpForward, 6, PlayerAnimation.JumpForward, 6, -0.9f),
            };
            double worstPos = 0, worstNrm = 0; int verts = 0, fallbacks = 0, mismatches = 0; bool layoutOk = true, slotsOk = true;
            float moved = 0;
            float[]? idle0 = null;
            foreach (var (l, lf, t, tf, pitch) in poses)
            {
                anim.Legs.SetAnimation((int)l); anim.Legs.AnimInfo.Frame[0] = lf;
                anim.Torso.SetAnimation((int)t); anim.Torso.AnimInfo.Frame[0] = tf;
                HunterRig.Pose(anim, pitch, palette);
                var c = new float[3];
                var world = GeometryBaker.Bake(model, c, out _, useAnimation: true, useMatrixStack: true);
                var skin = GeometryBaker.Bake(model, c, out _, useAnimation: true, useMatrixStack: true, skinned: true);
                fallbacks = Math.Max(fallbacks, GeometryBaker.FaceNormalFallbacks);
                mismatches = Math.Max(mismatches, GeometryBaker.NormalSlotMismatches);
                if (world.Count != skin.Count) { layoutOk = false; continue; }
                float scale = model.Scale.X;
                var flat = new List<float>();
                for (int b = 0; b < world.Count; b++)
                {
                    List<float> wv = world[b].Verts, sv = skin[b].Verts;
                    int n = wv.Count / GeometryBaker.StrideWorld;
                    if (sv.Count / GeometryBaker.StrideSkinned != n) { layoutOk = false; continue; }
                    for (int i = 0; i < n; i++)
                    {
                        int wi = i * GeometryBaker.StrideWorld, si = i * GeometryBaker.StrideSkinned;
                        int slot = (int)sv[si + 11];
                        if (slot < 0 || slot >= model.NodeMatrixIds.Count) { slotsOk = false; continue; }
                        Matrix4 bone = FromPalette(palette, slot);
                        // exactly what the vertex shader does (row-vector form of uModel * uBones[s] * p)
                        Vector3 p = Vector3.TransformPosition(new Vector3(sv[si], sv[si + 1], sv[si + 2]), bone) * scale;
                        Vector3 nrm = Vector3.TransformNormal(new Vector3(sv[si + 3], sv[si + 4], sv[si + 5]), bone);
                        if (nrm.LengthSquared > 1e-12f) nrm.Normalize();
                        worstPos = Math.Max(worstPos, (p - new Vector3(wv[wi], wv[wi + 1], wv[wi + 2])).Length);
                        var wn = new Vector3(wv[wi + 3], wv[wi + 4], wv[wi + 5]);
                        if (wn.LengthSquared > 1e-8f) worstNrm = Math.Max(worstNrm, (nrm - wn).Length);
                        flat.Add(p.X); flat.Add(p.Y); flat.Add(p.Z);
                        verts++;
                    }
                }
                if (idle0 == null) idle0 = flat.ToArray();
                else if (flat.Count == idle0.Length)
                    for (int i = 0; i < flat.Count; i++) moved = Math.Max(moved, Math.Abs(flat[i] - idle0[i]));
            }
            Check($"{h}: batch/vertex layout identical, bone slots in range [0,{model.NodeMatrixIds.Count})", layoutOk && slotsOk, $"{verts} verts over {poses.Length} poses");
            Check($"{h}: skinned positions == world bake", worstPos < 1e-4, $"max err {worstPos:E2}");
            Check($"{h}: skinned normals == world bake", worstNrm < 2e-3, $"max err {worstNrm:E2}; face-normal fallback tris={fallbacks}, normal/vertex slot mismatches={mismatches}");
            Check($"{h}: the poses actually differ (animation moves vertices)", moved > 0.05f, $"max displacement vs idle f0 = {moved:0.000}");
        }

        // Skin every frame of every clip on the CPU (the shader's exact math) and check: all finite; the
        // body's vertical extent stays plausible; and no vertex jumps implausibly far between consecutive
        // frames (a rotation-wrap or LUT-indexing bug would show up as a one-frame pop). Clip loops are
        // checked across the wrap too (last -> first frame) for looping clips.
        static void ClipSweep(Hunter h)
        {
            Model model = Read.GetModelInstance(Metadata.HunterModels[h][0]).Model;
            var a = new BipedAnimator(model);
            var pal = new float[16 * 32];
            a.SetBoth(PlayerAnimation.Idle, AnimFlags.None);
            HunterRig.Pose(a, 0f, pal);
            var skin = GeometryBaker.Bake(model, new float[3], out _, useAnimation: true, useMatrixStack: true, skinned: true);
            var local = new List<(Vector3 P, int S)>();
            foreach (var b in skin)
                for (int i = 0; i + 11 < b.Verts.Count; i += 12)
                    local.Add((new Vector3(b.Verts[i], b.Verts[i + 1], b.Verts[i + 2]), (int)b.Verts[i + 11]));
            int clips = model.AnimationGroups.Node.Count, frames = 0;
            bool finite = true; float minH = 99, maxH = 0, worstPop = 0, worstMorph = 0; string worstAt = "", worstMorphAt = "";
            var bigSteps = new List<(int Clip, int From, int To, int Slot)>();
            var prev = new Vector3[local.Count]; var cur = new Vector3[local.Count]; var first = new Vector3[local.Count];
            for (int c = 0; c < clips; c++)
            {
                int n = model.AnimationGroups.Node[c].FrameCount;
                for (int f = 0; f < n; f++)
                {
                    a.Legs.SetAnimation(c); a.Legs.AnimInfo.Frame[0] = f;
                    a.Torso.SetAnimation(c); a.Torso.AnimInfo.Frame[0] = f;
                    HunterRig.Pose(a, 0f, pal);
                    float lo = 99, hi = -99;
                    for (int v = 0; v < local.Count; v++)
                    {
                        Vector3 p = Vector3.TransformPosition(local[v].P, FromPalette(pal, local[v].S)) * model.Scale.X;
                        if (!float.IsFinite(p.X) || !float.IsFinite(p.Y) || !float.IsFinite(p.Z)) finite = false;
                        lo = MathF.Min(lo, p.Y); hi = MathF.Max(hi, p.Y);
                        cur[v] = p;
                    }
                    minH = MathF.Min(minH, hi - lo); maxH = MathF.Max(maxH, hi - lo);
                    bool morphClip = c is (int)PlayerAnimation.Morph or (int)PlayerAnimation.Unmorph;
                    if (f > 0)
                        for (int v = 0; v < local.Count; v++)
                        {
                            float d = (cur[v] - prev[v]).Length;
                            if (d > BigStep) bigSteps.Add((c, f - 1, f, local[v].S));
                            if (morphClip)
                            {
                                if (d > worstMorph) { worstMorph = d; worstMorphAt = $"{(PlayerAnimation)c} f{f - 1}->{f} bone slot {local[v].S} ({model.Nodes[model.NodeMatrixIds[local[v].S]].Name})"; }
                            }
                            else if (d > worstPop) { worstPop = d; worstAt = $"{(PlayerAnimation)c} f{f - 1}->{f} bone slot {local[v].S} ({model.Nodes[model.NodeMatrixIds[local[v].S]].Name})"; }
                        }
                    else Array.Copy(cur, first, cur.Length);
                    (prev, cur) = (cur, prev);
                    frames++;
                }
                // looping clips: last frame -> first frame must also be continuous
                bool loops = c is (int)PlayerAnimation.Idle or (int)PlayerAnimation.WalkForward or (int)PlayerAnimation.WalkBackward
                    or (int)PlayerAnimation.WalkLeft or (int)PlayerAnimation.WalkRight or (int)PlayerAnimation.Charge;
                if (loops)
                    for (int v = 0; v < local.Count; v++)
                    {
                        float d = (first[v] - prev[v]).Length;
                        if (d > BigStep) bigSteps.Add((c, n - 1, 0, local[v].S));
                        if (d > worstPop) { worstPop = d; worstAt = $"{(PlayerAnimation)c} loop wrap"; }
                    }
            }
            Check($"{h}: {clips} clips / {frames} frames all finite", finite);
            Check($"{h}: body height stays plausible in every frame", minH > 0.3f && maxH < 3.5f, $"height {minH:0.00}..{maxH:0.00}");
            // Fast authored motion is real (morph tucks, flourish swings, landing impacts): a big step is
            // only a defect if an INTERPOLATED channel (blend > 1) is involved -- that's where a rotation
            // wrap or LUT-indexing bug in our playback would show. Raw per-frame keys are the ROM's data.
            int chTotal = 0, chInterp = 0;
            foreach (var g in model.AnimationGroups.Node)
                foreach (NodeAnimation na in g.Animations.Values)
                    foreach (var (bl, ln) in new[] { (na.ScaleBlendX, na.ScaleLutLengthX), (na.ScaleBlendY, na.ScaleLutLengthY), (na.ScaleBlendZ, na.ScaleLutLengthZ),
                        (na.RotateBlendX, na.RotateLutLengthX), (na.RotateBlendY, na.RotateLutLengthY), (na.RotateBlendZ, na.RotateLutLengthZ),
                        (na.TranslateBlendX, na.TranslateLutLengthX), (na.TranslateBlendY, na.TranslateLutLengthY), (na.TranslateBlendZ, na.TranslateLutLengthZ) })
                    { chTotal++; if (bl > 1 && ln > 1) chInterp++; }
            // Measured property of the ROM data (not assumed): hunter body clips store every S/R/T channel
            // as one sample per frame or as a constant -- nothing is keyframe-interpolated. So playback is
            // an exact lookup of authored poses, and any fast frame-to-frame motion (morph tucks, flourish
            // swings, landing impacts) is the game's own data, not a playback artefact. If a future model
            // (e.g. a mod) ships interpolated channels, this fails and the big-step list below needs review.
            var distinct = bigSteps.Distinct().ToList();
            var interpolated = distinct.Where(s => !ChainRaw(model, model.AnimationGroups.Node[s.Clip], model.NodeMatrixIds[s.Slot])).ToList();
            Check($"{h}: all {chTotal} animation channels are per-frame samples or constants (exact authored poses, no interpolation)",
                chInterp == 0 && interpolated.Count == 0,
                $"{chInterp} interpolated; {distinct.Count} frame steps > {BigStep} units (all authored), worst non-morph {worstPop:0.00} ({worstAt}), worst morph {worstMorph:0.00}");
        }

        const float BigStep = 0.6f;

        // Every clip x frame, split layers (legs clip != torso clip) and a non-zero aim: our FK must give
        // the same node matrices as MphRead's AnimateNodes path (HunterRig.Pose) to float precision.
        static void FkParity(Hunter h)
        {
            Model model = Read.GetModelInstance(Metadata.HunterModels[h][0]).Model;
            var sk = new DsSkeleton(model);
            var a = new BipedAnimator(model);
            var pal = new float[16 * 32];
            var pose = new SkeletonPose(sk.Count);
            var world = new Matrix4[sk.Count];
            float worst = 0; int checks = 0; string at = "";
            int clips = model.AnimationGroups.Node.Count;
            for (int c = 0; c < clips; c++)
            {
                int n = model.AnimationGroups.Node[c].FrameCount;
                int other = (c + 7) % clips, on = model.AnimationGroups.Node[other].FrameCount;
                for (int f = 0; f < n; f++)
                {
                    float aim = ((c * 31 + f) % 11 - 5) / 6f; // spread of aims incl. beyond the 45 deg clamp
                    a.Legs.SetAnimation(c); a.Legs.AnimInfo.Frame[0] = f;
                    a.Torso.SetAnimation(other); a.Torso.AnimInfo.Frame[0] = f % on;
                    HunterRig.Pose(a, aim, pal);           // writes node.Animation via AnimateNodes
                    sk.SampleBiped(model, a, pose);
                    sk.Fk(pose, world, aim);
                    for (int i = 0; i < sk.Count; i++)
                    {
                        Matrix4 d = world[i] - model.Nodes[i].Animation;
                        float e = MathF.Max(MathF.Max(d.Row0.Length, d.Row1.Length), MathF.Max(d.Row2.Length, d.Row3.Length));
                        if (e > worst) { worst = e; at = $"{(PlayerAnimation)c}/{(PlayerAnimation)other} f{f} node {sk.Names[i]}"; }
                        checks++;
                    }
                }
            }
            Check($"{h}: FK == AnimateNodes for every node, clip, frame (split layers + aim)", worst < 1e-4f, $"{checks} node matrices, max err {worst:E2}{(worst > 0 ? " at " + at : "")}");
        }

        // Every animated node from `nodeIdx` up to the root stores all 9 S/R/T channels either as one
        // value per frame (blend 1) or as a constant (LUT length 1) -- i.e. nothing is interpolated.
        static bool ChainRaw(Model m, NodeAnimationGroup g, int nodeIdx)
        {
            static bool Raw(int blend, int len) => blend == 1 || len == 1;
            for (int k = nodeIdx; k >= 0; k = m.Nodes[k].ParentIndex)
            {
                if (!g.Animations.TryGetValue(m.Nodes[k].Name, out NodeAnimation a)) continue;
                if (!(Raw(a.ScaleBlendX, a.ScaleLutLengthX) && Raw(a.ScaleBlendY, a.ScaleLutLengthY) && Raw(a.ScaleBlendZ, a.ScaleLutLengthZ)
                    && Raw(a.RotateBlendX, a.RotateLutLengthX) && Raw(a.RotateBlendY, a.RotateLutLengthY) && Raw(a.RotateBlendZ, a.RotateLutLengthZ)
                    && Raw(a.TranslateBlendX, a.TranslateLutLengthX) && Raw(a.TranslateBlendY, a.TranslateLutLengthY) && Raw(a.TranslateBlendZ, a.TranslateLutLengthZ)))
                    return false;
            }
            return true;
        }

        sealed class FlatFloor : MphRecomp.Game.ICollision
        {
            public float FloorBelow(System.Numerics.Vector3 pos) => pos.Y >= 0 ? 0f : float.NegativeInfinity;
            public bool Blocked(System.Numerics.Vector3 pos, System.Numerics.Vector3 dir, float dist) => false;
        }

        static void Dummy(Hunter h)
        {
            Model model = Read.GetModelInstance(Metadata.HunterModels[h][0]).Model;
            var d = new HunterDummy(h, model, new Vector3(0, 3, 0), 0f) { Collision = new FlatFloor() };
            d.SnapToFloor();
            Check($"{h}: snaps feet onto the floor", MathF.Abs(d.Feet.Y) < 1e-4f, $"feet y={d.Feet.Y:0.0000}, position y={d.Position.Y:0.000}");
            for (int i = 0; i < 30; i++) d.Tick(default);
            Check($"{h}: standing still stays grounded in Idle", d.Grounded && d.Anim.LegsAnim == PlayerAnimation.Idle && MathF.Abs(d.Feet.Y) < 1e-4f);

            var p0 = d.Position;
            for (int i = 0; i < 60; i++) d.Tick(new DummyInput { MoveY = 1 });
            var moved = d.Position - p0;
            float hs = MathF.Sqrt(d.Speed.X * d.Speed.X + d.Speed.Z * d.Speed.Z);
            Check($"{h}: forward walks along facing (-Z at yaw 0), WalkForward", moved.Z < -1f && MathF.Abs(moved.X) < 1e-3f && d.Anim.LegsAnim == PlayerAnimation.WalkForward,
                $"{-moved.Z:0.00} units/s, steady speed {hs:0.000}/frame");
            for (int i = 0; i < 60; i++) d.Tick(default);
            Check($"{h}: releasing the stick coasts to a stop and Idles", MathF.Sqrt(d.Speed.X * d.Speed.X + d.Speed.Z * d.Speed.Z) < 0.01f && d.Anim.LegsAnim == PlayerAnimation.Idle);

            d.Tick(new DummyInput { Jump = true });
            Check($"{h}: jump -> JumpNeutral, leaves the ground", d.Anim.LegsAnim == PlayerAnimation.JumpNeutral && d.Feet.Y > 0);
            float apex = 0; int air = 1;
            while (!d.Standing && air < 600) { d.Tick(default); apex = MathF.Max(apex, d.Feet.Y); air++; }
            Check($"{h}: lands -> LandNeutral", d.Standing && d.Anim.LegsAnim == PlayerAnimation.LandNeutral, $"apex {apex:0.00} units, airtime {air / 60f:0.00}s");
            d.Tick(new DummyInput { Jump = true });
            Check($"{h}: can jump again after landing", !d.Standing);
            while (!d.Standing) d.Tick(default);

            for (int i = 0; i < 90; i++) d.Tick(default);
            d.Tick(new DummyInput { TurnX = 1 });
            Check($"{h}: hard right-stick flick plays Turn", d.Anim.LegsAnim == PlayerAnimation.Turn);
            float yaw0 = d.Yaw;
            for (int i = 0; i < 20; i++) d.Tick(new DummyInput { TurnX = 1 });
            Check($"{h}: stick right turns clockwise (yaw grows)", d.Yaw > yaw0);
        }

        // Holding fire the way the viewer's R2 and auto combat demo do: the torso must go Shoot -> Charge ->
        // ChargeShoot while the legs never leave the movement clip; a tap or an early release never charges.
        static void Combat(Hunter h)
        {
            Model model = Read.GetModelInstance(Metadata.HunterModels[h][0]).Model;
            var d = new HunterDummy(h, model, new Vector3(0, 3, 0), 0f) { Collision = new FlatFloor() };
            d.SnapToFloor();
            for (int i = 0; i < 30; i++) d.Tick(default);

            var torso = new List<PlayerAnimation>();
            bool legsStayed = true;
            void Run(int ticks, DummyInput input, PlayerAnimation legs, bool firstPress)
            {
                for (int i = 0; i < ticks; i++)
                {
                    d.Tick(input with { Fire = firstPress && i == 0 });
                    legsStayed &= d.Anim.LegsAnim == legs;
                    if (torso.Count == 0 || torso[^1] != d.Anim.TorsoAnim) torso.Add(d.Anim.TorsoAnim);
                }
            }
            string Seq() => String.Join(" > ", torso);

            // standing: hold 1.5 s, let go, watch 1.5 s
            Run(90, new DummyInput { FireHeld = true }, PlayerAnimation.Idle, firstPress: true);
            Run(90, default, PlayerAnimation.Idle, firstPress: false);
            Check($"{h}: standing charge -> torso Shoot > Charge > ChargeShoot > Idle", Seq() == "Shoot > Charge > ChargeShoot > Idle", Seq());
            Check($"{h}: standing charge -> legs stayed Idle throughout", legsStayed);
            Check($"{h}: torso re-synced to the legs after the charged shot", d.Anim.TorsoFrame == d.Anim.LegsFrame);

            // walking forward: the same, legs on WalkForward the whole time
            torso.Clear(); legsStayed = true;
            Run(10, new DummyInput { MoveY = 1 }, PlayerAnimation.WalkForward, firstPress: false);
            torso.Clear();
            Run(90, new DummyInput { MoveY = 1, FireHeld = true }, PlayerAnimation.WalkForward, firstPress: true);
            Run(80, new DummyInput { MoveY = 1 }, PlayerAnimation.WalkForward, firstPress: false);
            Check($"{h}: walking charge -> torso Shoot > Charge > ChargeShoot > WalkForward", Seq() == "Shoot > Charge > ChargeShoot > WalkForward", Seq());
            Check($"{h}: walking charge -> legs stayed WalkForward throughout", legsStayed);

            // a tap (0.1 s) and an early release (0.5 s, under the 0.6 s minimum) never play ChargeShoot
            for (int i = 0; i < 60; i++) d.Tick(default);
            torso.Clear();
            Run(6, new DummyInput { FireHeld = true }, PlayerAnimation.Idle, firstPress: true);
            Run(60, default, PlayerAnimation.Idle, firstPress: false);
            Run(30, new DummyInput { FireHeld = true }, PlayerAnimation.Idle, firstPress: true);
            Run(60, default, PlayerAnimation.Idle, firstPress: false);
            Check($"{h}: tap and early release never play ChargeShoot", !torso.Contains(PlayerAnimation.ChargeShoot), Seq());
        }

        static Matrix4 FromPalette(float[] p, int slot)
        {
            int o = slot * 16;
            return new Matrix4(p[o], p[o + 1], p[o + 2], p[o + 3], p[o + 4], p[o + 5], p[o + 6], p[o + 7],
                p[o + 8], p[o + 9], p[o + 10], p[o + 11], p[o + 12], p[o + 13], p[o + 14], p[o + 15]);
        }

        static void StateMachine()
        {
            Model model = Read.GetModelInstance(Metadata.HunterModels[Hunter.Kanden][0]).Model;
            var a = new BipedAnimator(model);
            var ground = new BipedIntent { Grounded = true, TimeSinceJumpPad = 1000 };
            void Run(BipedIntent it, int ticks) { for (int i = 0; i < ticks; i++) a.Tick(it); }

            Check("spawns playing Spawn on both layers", a.LegsAnim == PlayerAnimation.Spawn && a.TorsoAnim == PlayerAnimation.Spawn);
            Run(ground, 1);
            Run(ground, 1);
            Check("first grounded ticks hand over to Idle (legs, then torso follows)", a.LegsAnim == PlayerAnimation.Idle && a.TorsoAnim == PlayerAnimation.Idle);

            // cadence: one clip frame per two 60 Hz ticks
            int f0 = a.LegsFrame; Run(ground, 20);
            Check("frames advance at 30 fps (10 frames per 20 ticks)", a.LegsFrame == (f0 + 10) % a.LegsFrameCount, $"frame {f0} -> {a.LegsFrame}");
            Check("torso frame-synced to legs while mirroring", a.TorsoFrame == a.LegsFrame);

            var fwd = ground; fwd.Forward = true; fwd.HasInput = true;
            Run(fwd, 2);
            Check("forward -> WalkForward", a.LegsAnim == PlayerAnimation.WalkForward && a.TorsoAnim == PlayerAnimation.WalkForward);
            var fwdRight = fwd; fwdRight.Right = true;
            Run(fwdRight, 2);
            Check("forward+right -> still WalkForward (forward/back wins over strafe)", a.LegsAnim == PlayerAnimation.WalkForward);
            var right = ground; right.Right = true; right.HasInput = true;
            Run(right, 2);
            Check("right only -> WalkRight", a.LegsAnim == PlayerAnimation.WalkRight);
            var back = ground; back.Back = true; back.HasInput = true;
            Run(back, 2);
            Check("back -> WalkBackward", a.LegsAnim == PlayerAnimation.WalkBackward);
            Run(ground, 2);
            Check("release -> Idle (walk clips loop, so they hand back at once)", a.LegsAnim == PlayerAnimation.Idle);

            // jump forward: NoLoop, holds the last frame while airborne
            var jump = fwd; jump.JumpStarted = true; jump.Grounded = false;
            Run(jump, 1);
            Check("jump + forward -> JumpForward NoLoop", a.LegsAnim == PlayerAnimation.JumpForward && a.LegsAnimFlags.HasFlag(AnimFlags.NoLoop));
            var air = fwd; air.Grounded = false;
            Run(air, 60);
            Check("airborne: jump clip ends and holds its last frame", a.LegsAnim == PlayerAnimation.JumpForward && a.LegsAnimFlags.HasFlag(AnimFlags.Ended) && a.LegsFrame == a.LegsFrameCount - 1);
            Check("airborne: torso followed the jump once free", a.TorsoAnim == PlayerAnimation.JumpForward);
            var land = ground; land.Landed = true;
            Run(land, 1);
            Check("landing after a forward jump -> LandNeutral", a.LegsAnim == PlayerAnimation.LandNeutral);
            Run(ground, a.LegsFrameCount * 2 + 4);
            Check("LandNeutral plays out, then Idle", a.LegsAnim == PlayerAnimation.Idle);

            var jl = ground; jl.Left = true; jl.JumpStarted = true; jl.Grounded = false; jl.HasInput = true;
            Run(jl, 1);
            Check("jump + left -> JumpLeft", a.LegsAnim == PlayerAnimation.JumpLeft);
            Run(land, 1);
            Check("landing from JumpLeft -> LandLeft", a.LegsAnim == PlayerAnimation.LandLeft);
            var landMoving = fwd; landMoving.Landed = false;
            Run(landMoving, 1);
            Check("moving on the ground right after landing cuts to WalkForward", a.LegsAnim == PlayerAnimation.WalkForward);

            // shoot while walking: torso-only override, then frame-synced hand-back to the legs
            var shoot = fwd; shoot.Fired = true;
            Run(shoot, 1);
            Check("fire while walking: torso Shoot, legs keep walking", a.TorsoAnim == PlayerAnimation.Shoot && a.LegsAnim == PlayerAnimation.WalkForward);
            Run(fwd, a.TorsoFrameCount * 2 + 4);
            Check("after Shoot ends the torso re-syncs to the legs' clip + frame", a.TorsoAnim == PlayerAnimation.WalkForward && a.TorsoFrame == a.LegsFrame);

            // turn: faster than 3 deg/tick while standing
            var turnL = ground; turnL.AimYawDeltaDeg = 4f; turnL.HasInput = true;
            Run(turnL, 1);
            Check("turning left > 3 deg/tick -> Turn, reversed", a.LegsAnim == PlayerAnimation.Turn && a.LegsAnimFlags.HasFlag(AnimFlags.Reverse));
            var turnR = ground; turnR.AimYawDeltaDeg = -4f; turnR.HasInput = true;
            Run(turnR, 1);
            Check("turning right -> Turn, forward", a.LegsAnim == PlayerAnimation.Turn && !a.LegsAnimFlags.HasFlag(AnimFlags.Reverse));
            var slow = ground; slow.AimYawDeltaDeg = 2f; slow.HasInput = true;
            Run(slow, 1);
            Check("slow turn (< 3 deg/tick) does not start/renew Turn; it settles NoLoop", a.LegsAnim == PlayerAnimation.Turn && a.LegsAnimFlags.HasFlag(AnimFlags.NoLoop));
            Run(ground, 60);
            Check("Turn settles back to Idle", a.LegsAnim == PlayerAnimation.Idle);

            // flourish: needs BOTH >600 ticks idling AND >600 ticks since the last input. Touch the
            // input once (no movement), then count ticks until it fires.
            var touch = ground; touch.HasInput = true;
            Run(touch, 1);
            int waited = 0;
            while (a.LegsAnim != PlayerAnimation.Flourish && waited < 2000) { a.Tick(ground); waited++; }
            Check("Flourish fires exactly when the no-input timer passes 600 ticks", waited == BipedAnimator.FlourishDelayTicks + 1, $"after {waited} ticks");
            Run(ground, 2);
            Check("torso joins the Flourish", a.TorsoAnim == PlayerAnimation.Flourish);
            Run(ground, a.LegsFrameCount * 2 + 4);
            Check("Flourish ends back in Idle", a.LegsAnim == PlayerAnimation.Idle);

            // manual (viewer) mode loops a fixed clip on both layers
            a.PlayManual(PlayerAnimation.WalkLeft);
            Run(fwd, 40);
            Check("manual mode ignores intents and loops the pinned clip", a.LegsAnim == PlayerAnimation.WalkLeft && a.TorsoAnim == PlayerAnimation.WalkLeft);
        }
    }
}
