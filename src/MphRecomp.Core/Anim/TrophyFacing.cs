using System;
using System.Collections.Generic;
using System.Linq;
using MphRead;
using MphRead.Entities;
using OpenTK.Mathematics;

namespace MphRecomp.Anim
{
    // Turns the bind of the body's symmetric parts (hips, chest, head) so each FACES the way the game model's does.
    //
    // Why: a landmark fit aims the chest bone at its child joints (head + shoulders). On a sculpt with one arm raised
    // and pulled back (Weavel), that shoulder joint drags the shoulder line round, so the fitted chest is turned ~20
    // degrees from where the chest armour itself faces -- and linear skinning carries that into every clip: the chest
    // keeps the trophy's turn instead of lining up with the head the way the game's Idle does.
    //
    // How: pose each part rigidly by its own bone into the game's Idle f0; find the vertical plane it is mirror-
    // symmetric about (a chest, pelvis or helmet is left/right symmetric even when the whole sculpt is not); do the
    // same for the game model's own mesh of that bone; turn the bind about the vertical through the joint by the
    // difference. Only the bind of that bone changes, so only the vertices it carries turn.
    public static class TrophyFacing
    {
        // chest and head: the hips' own mesh is mostly carried by the thighs, and the few pelvis-only vertices left are
        // too small and lopsided to give a facing (Weavel's measured 86 degrees off -- nonsense)
        public static readonly string[] Parts = { "Spine_2", "Head_1" };

        public static void Align(Model model, TrophyRig rig, Action<string>? log = null)
        {
            var sk = new DsSkeleton(model);
            int nb = sk.Count;
            var pose = new SkeletonPose(nb);
            var idleW = new Matrix4[nb]; var dsW = new Matrix4[nb];
            DsSkeleton.Sample(model, (int)PlayerAnimation.Idle, 0, pose);
            sk.Fk(pose, idleW, 0f, rig.Lengths, rig.Offsets);
            sk.Fk(pose, dsW);
            // the game model's own mesh per bone, bone-local
            var poser = new BipedAnimator(model);
            poser.SetBoth(PlayerAnimation.Idle, AnimFlags.None);
            HunterRig.Pose(poser, 0f, new float[16 * 32]);
            var baked = MphRecomp.Render.GeometryBaker.Bake(model, new float[3], out _, useAnimation: true, useMatrixStack: true, skinned: true);
            const int S = MphRecomp.Render.GeometryBaker.StrideSkinned;
            var dsLocal = new List<Vector3>[model.Nodes.Count];
            for (int i = 0; i < dsLocal.Length; i++) dsLocal[i] = new List<Vector3>();
            foreach (var b in baked)
                for (int i = 0; i + S <= b.Verts.Count; i += S)
                    dsLocal[model.NodeMatrixIds[(int)b.Verts[i + 11]]].Add(new Vector3(b.Verts[i], b.Verts[i + 1], b.Verts[i + 2]));
            var bind = rig.BindPos ?? rig.Vertices.Select(v => Vector3.TransformPosition(v, rig.TrophyToRig)).ToArray();
            var report = new List<string>();
            foreach (string name in Parts)
            {
                int b = sk.IndexOf(name);
                if (b < 0 || dsLocal[b].Count < 20) continue;
                var tro = new List<Vector3>();
                for (int v = 0; v < bind.Length; v++)
                    if (rig.Bones[v * 4] == b && rig.Weights[v * 4] >= 0.9f && (rig.Hidden.Length == 0 || !rig.Hidden[v]))
                        tro.Add(Vector3.TransformPosition(bind[v], rig.InvBind[b] * idleW[b]));
                if (tro.Count < 40) continue;
                var ds = dsLocal[b].Select(p => Vector3.TransformPosition(p, dsW[b])).ToList();
                float yT = MirrorYaw(tro), yD = MirrorYaw(ds);
                float d = Wrap(yT - yD);
                report.Add($"{name} {d:+0;-0} deg");
                if (MathF.Abs(d) < 4f) continue;   // within what the measure can tell apart
                if (MathF.Abs(d) > 35f) { report[^1] += " (implausible, not applied)"; continue; }
                // turn the part (as carried into Idle by its bone) by -d about the vertical through its joint
                var j = idleW[b].ExtractTranslation();
                var turn = Matrix4.CreateTranslation(-j) * Matrix4.CreateRotationY(d * MathF.PI / 180f) * Matrix4.CreateTranslation(j);
                var inv = rig.InvBind[b] * idleW[b] * turn * Matrix4.Invert(idleW[b]);
                rig.InvBind[b] = inv; rig.BindWorld[b] = Matrix4.Invert(inv);
            }
            if (report.Count > 0) log?.Invoke("  facing aligned to the game model (chest/head turned by): " + string.Join(", ", report) + " (under 4 deg left alone)");
        }

        static float Wrap(float a) { a = (a + 90f) % 180f; if (a < 0) a += 180f; return a - 90f; }   // mirror planes repeat every 180

        // yaw (degrees) of the vertical plane the points are most nearly mirror-symmetric about: the plane's normal is
        // (cos yaw, 0, sin yaw) through the centroid (slid sideways a little); scored by mean nearest distance
        public static float MirrorYaw(IReadOnlyList<Vector3> pts)
        {
            var P = pts.Distinct().ToArray();
            var c = P.Aggregate(Vector3.Zero, (a, p) => a + p) / P.Length;
            var probe = P.Length > 400 ? Enumerable.Range(0, 400).Select(i => P[i * P.Length / 400]).ToArray() : P;
            float Score(float yawDeg, float off)
            {
                float a = yawDeg * MathF.PI / 180f; var n = new Vector3(MathF.Cos(a), 0, MathF.Sin(a));
                double s = 0;
                foreach (var p in probe)
                {
                    var r = p - 2f * Vector3.Dot(p - c, n) * n + 2f * off * n;
                    float best = float.MaxValue;
                    foreach (var q in P) { float dd = (q - r).LengthSquared; if (dd < best) best = dd; }
                    s += MathF.Sqrt(best);
                }
                return (float)(s / probe.Length);
            }
            float span = MathF.Sqrt(P.Max(p => (p - c).LengthSquared));
            float bestYaw = 0, bestS = float.MaxValue;
            foreach (float step in new[] { 3f, 0.5f })
            {
                float lo = step == 3f ? -90f : bestYaw - 3f, hi = step == 3f ? 90f : bestYaw + 3f;
                for (float y = lo; y < hi; y += step)
                    foreach (float off in new[] { -0.04f, 0f, 0.04f })
                    {
                        float s = Score(y, off * span);
                        if (s < bestS) { bestS = s; bestYaw = y; }
                    }
            }
            return bestYaw;
        }
    }
}
