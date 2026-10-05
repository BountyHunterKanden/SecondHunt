using System;
using System.Collections.Generic;
using System.Linq;
using MphRead;
using MphRead.Entities;
using MphRecomp.Render;
using OpenTK.Mathematics;

namespace MphRecomp.Anim
{
    // Puts a rigged trophy's FEET ON THE FLOOR where the game model's are.
    //
    // Why: the game's clips carry the hunter's hip height (Skeleton_Root's translation), which the rig scales by the root's
    // length factor. A landmark fit sets that factor from where the pelvis sits in the SCULPT -- a lunging statue, or a
    // pelvis mark above the hip joints, gives a hip height that doesn't go with the trophy's own legs, and in the game's
    // standing Idle the feet then sank into the floor (SyluxTpose 12% of its height, Weavel and Spire 7%) or floated.
    //
    // How: the feet (vertices mostly on an ankle bone) are posed through the standing Idle and the planted frames of the
    // walk; the lowest foot point moves linearly with the root's factor, so the factor that best puts it where the game
    // model's own lowest foot point is (the standing Idle counting most) is solved in one step. Only that one number changes:
    // the bind, every joint and every piece stay as they are -- the whole body is carried up or down in the clips.
    public static class TrophyFeet
    {
        public const float PlantedReach = 0.03f;
        public const float IdleShare = 0.8f;       // standing is what's seen most: the Idle counts 4x the walk's planted frames   // a walk frame counts when the game's feet are this close (x height) to the Idle floor
        static readonly PlayerAnimation[] Clips = { PlayerAnimation.Idle, PlayerAnimation.WalkForward };

        public static void Settle(TrophyRig rig, Model model, DsSkeleton sk, Action<string>? log = null)
        {
            int root = sk.IndexOf("Skeleton_Root");
            var ankles = Enumerable.Range(0, sk.Count).Where(i => sk.Names[i].Contains("ankle", StringComparison.OrdinalIgnoreCase)).ToHashSet();
            if (root < 0 || ankles.Count == 0 || rig.Lengths.Length != sk.Count) return;
            int nv = rig.Vertices.Length;
            var bind = rig.BindPos ?? rig.Vertices.Select(p => Vector3.TransformPosition(p, rig.TrophyToRig)).ToArray();
            var feet = new List<int>();
            for (int v = 0; v < nv; v++)
            {
                float w = 0;
                for (int k = 0; k < 4; k++) if (ankles.Contains(rig.Bones[v * 4 + k])) w += rig.Weights[v * 4 + k];
                if (w >= 0.5f && !(rig.Hidden.Length > 0 && rig.Hidden[v])) feet.Add(v);
            }
            if (feet.Count < 8) return;
            // the game model's feet
            var baked = GeometryBaker.Bake(model, new float[3], out _, useAnimation: false, useMatrixStack: true, skinned: true);
            var gp = new List<Vector3>(); var gb = new List<int>(); var ap = new List<Vector3>(); var ab = new List<int>();
            foreach (var b in baked)
                for (int k = 0; k + GeometryBaker.StrideSkinned <= b.Verts.Count; k += GeometryBaker.StrideSkinned)
                {
                    int node = model.NodeMatrixIds[(int)b.Verts[k + 11]];
                    var p = new Vector3(b.Verts[k], b.Verts[k + 1], b.Verts[k + 2]);
                    ap.Add(p); ab.Add(node);
                    if (ankles.Contains(node)) { gp.Add(p); gb.Add(node); }
                }
            if (gp.Count == 0) return;
            var pose = new SkeletonPose(sk.Count); var world = new Matrix4[sk.Count]; var skin = new Matrix4[sk.Count];
            float GameFoot(int clip, int f)
            {
                DsSkeleton.Sample(model, clip, f, pose); sk.Fk(pose, world);
                float m = float.MaxValue;
                for (int i = 0; i < gp.Count; i++) m = MathF.Min(m, Vector3.TransformPosition(gp[i], world[gb[i]]).Y);
                return m;
            }
            float RigFoot(int clip, int f, float k)
            {
                float keep = rig.Lengths[root]; rig.Lengths[root] = k;
                DsSkeleton.Sample(model, clip, f, pose); sk.Fk(pose, world, 0f, rig.Lengths, rig.Offsets);
                rig.Lengths[root] = keep;
                for (int i = 0; i < sk.Count; i++) skin[i] = rig.InvBind[i] * world[i];
                float m = float.MaxValue;
                foreach (int v in feet)
                {
                    Vector3 acc = Vector3.Zero;
                    for (int q = 0; q < 4; q++) { float w = rig.Weights[v * 4 + q]; if (w > 0) acc += Vector3.TransformPosition(bind[v], skin[rig.Bones[v * 4 + q]]) * w; }
                    m = MathF.Min(m, acc.Y);
                }
                return m;
            }
            float k0 = rig.Lengths[root], k1 = k0 + 0.1f;
            float floor0 = GameFoot((int)PlayerAnimation.Idle, 0);
            float height = Math.Max(1e-3f, Enumerable.Range(0, ap.Count).Max(i => Vector3.TransformPosition(ap[i], world[ab[i]]).Y) - floor0);   // (world = Idle f0)
            var rows = new List<(int Clip, float W, float Tgt, float F0, float S)>();
            foreach (var clip in Clips)
            {
                int c = (int)clip;
                if (c >= model.AnimationGroups.Node.Count || model.AnimationGroups.Node[c].FrameCount == 0) continue;
                var fr = new List<(float Tgt, float F0, float S)>();
                for (int f = 0; f < model.AnimationGroups.Node[c].FrameCount; f++)
                {
                    float g = GameFoot(c, f);
                    if (clip != PlayerAnimation.Idle && g > floor0 + PlantedReach * height) continue;
                    float a = RigFoot(c, f, k0), b = RigFoot(c, f, k1);
                    fr.Add((g, a, (b - a) / (k1 - k0)));
                }
                foreach (var (t, a, s) in fr) rows.Add((c, (clip == PlayerAnimation.Idle ? IdleShare : 1f - IdleShare) / fr.Count, t, a, s));
            }
            if (rows.Count == 0) return;
            // least squares for dk: error = F0 + dk S - Tgt
            double ss = 0, bs = 0;
            foreach (var r in rows) { ss += r.W * r.S * r.S; bs += r.W * r.S * (r.Tgt - r.F0); }
            if (ss < 1e-12) return;
            float kNew = Math.Clamp(k0 + (float)(bs / ss), 0.5f, 2f);
            string Err(Func<(int Clip, float W, float Tgt, float F0, float S), double> e) => string.Join(", ", rows.GroupBy(r => r.Clip).Select(g =>
                $"{(PlayerAnimation)g.Key} {g.Average(e) / height * 100:+0.0;-0.0;0.0}%"));
            string was = Err(r => r.F0 - r.Tgt), now = Err(r => r.F0 + (kNew - k0) * r.S - r.Tgt);
            rig.Lengths[root] = kNew;
            log?.Invoke($"  feet: hip height x{kNew / k0:0.000}; feet vs the game's floor: {was} -> {now}");
        }
    }
}
