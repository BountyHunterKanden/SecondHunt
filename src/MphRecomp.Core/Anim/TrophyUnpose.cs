using System;
using System.Collections.Generic;
using System.Linq;
using MphRead;
using MphRead.Entities;
using OpenTK.Mathematics;

namespace MphRecomp.Anim
{
    // Straightens a rigged trophy out of its sculpted pose into a target pose of its skeleton -- once, ahead
    // of time -- so the game's clips only have to bend it as far as they bend the game's own model.
    //
    // Why: linear-blend skinning collapses a joint roughly as cos(bend/2), where "bend" is how far the joint
    // sits from the pose the mesh is bound in. Bound as sculpted (Weavel's sword arm raised overhead, Trace's
    // folded legs) typical bends are 50-120 degrees; bound in the game's own idle pose they are 10-25.
    // Every published rig of these trophies (and standard practice) T-poses first for the same reason.
    //
    // How: as-rigid-as-possible deformation (Sorkine & Alexa 2007). The interior of each limb -- vertices
    // well away from any joint, wholly on one bone -- is a HANDLE carried rigidly to the target pose; the
    // regions around joints are solved so every vertex's neighbourhood keeps its sculpted shape as closely
    // as possible (alternating a per-vertex best rotation with a sparse Laplacian solve). Each vertex also
    // gets its rotation, so the model's authored normals (hard edges included) turn with it.
    public static class TrophyUnpose
    {
        public sealed class Result
        {
            public Vector3[] Pos = Array.Empty<Vector3>();       // straightened positions (target pose, rig space)
            public Quaternion[] Rot = Array.Empty<Quaternion>(); // per vertex: rotation from sculpt to straightened
            public int Handles, Free, FixedIslands;
        }

        // pos: sculpted positions (rig space); tris: triangle vertex indices (3 per triangle);
        // bones/weights: 4 per vertex; bindWorld/targetWorld: per bone, the sculpted and target pose;
        // handle: per vertex, carried rigidly by its main bone.
        public static Result Solve(Vector3[] pos, int[] tris, int[] bones, float[] weights, Matrix4[] bindWorld, Matrix4[] targetWorld,
                                   bool[] handle, int iterations = 12, Action<string>? log = null)
        {
            int n = pos.Length;
            var skin = new Matrix4[bindWorld.Length];
            for (int b = 0; b < skin.Length; b++) skin[b] = Matrix4.Invert(bindWorld[b]) * targetWorld[b];
            // linear blend of the bones' transforms -- except where the vertex's bones turn more than 60 degrees apart
            // (Samus's cannon forearm straightened from a sharp bend into the T): a linear blend of two such rotations
            // collapses and flings the vertex across the body, so those blend the ROTATION (dual quaternions) instead
            var skinRot = skin.Select(m => m.ExtractRotation().Normalized()).ToArray();
            static float QDot(Quaternion a, Quaternion b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z + a.W * b.W;
            Vector3 Lbs(int v)
            {
                float cosMin = 1f;
                for (int a = 0; a < 4; a++)
                    for (int c = a + 1; c < 4; c++)
                        if (weights[v * 4 + a] > 0 && weights[v * 4 + c] > 0)
                            cosMin = MathF.Min(cosMin, MathF.Abs(QDot(skinRot[bones[v * 4 + a]], skinRot[bones[v * 4 + c]])));
                if (cosMin < 0.866f) return Dqs(pos[v], v);   // |q1.q2| = cos(half angle): 0.866 = 60 degrees apart
                var acc = Vector3.Zero;
                for (int k = 0; k < 4; k++) { float w = weights[v * 4 + k]; if (w > 0) acc += Vector3.TransformPosition(pos[v], skin[bones[v * 4 + k]]) * w; }
                return acc;
            }
            Vector3 Dqs(Vector3 p, int v)
            {
                // blend rotation r and translation t of each bone as a dual quaternion (r, 0.5 t r), hemisphere-aligned
                Quaternion r0 = default; var qr = new Vector4(); var qd = new Vector4(); float scale = 0;
                bool first = true;
                for (int k = 0; k < 4; k++)
                {
                    float w = weights[v * 4 + k]; if (w <= 0) continue;
                    var m = skin[bones[v * 4 + k]];
                    var r = skinRot[bones[v * 4 + k]]; var t = m.ExtractTranslation(); float sc = m.ExtractScale().X;
                    if (first) { r0 = r; first = false; }
                    if (QDot(r0, r) < 0) { r = new Quaternion(-r.X, -r.Y, -r.Z, -r.W); }
                    var d = new Quaternion(t, 0f) * r; d = new Quaternion(d.X * 0.5f, d.Y * 0.5f, d.Z * 0.5f, d.W * 0.5f);
                    qr += w * new Vector4(r.X, r.Y, r.Z, r.W); qd += w * new Vector4(d.X, d.Y, d.Z, d.W); scale += w * sc;
                }
                float len = qr.Length; if (len < 1e-8f) return p;
                var R = new Quaternion(qr.X / len, qr.Y / len, qr.Z / len, qr.W / len);
                var D = new Quaternion(qd.X / len, qd.Y / len, qd.Z / len, qd.W / len);
                var tq = D * Quaternion.Conjugate(R);
                var trans = new Vector3(tq.X, tq.Y, tq.Z) * 2f;
                return Vector3.Transform(p * scale, R) + trans;
            }
            Quaternion BoneRot(int v) => skin[bones[v * 4]].ExtractRotation().Normalized();

            // cotangent weights (clamped positive: game meshes have slivers and obtuse triangles)
            var w2 = new Dictionary<long, double>();
            long Key(int a, int b) => a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;
            for (int t = 0; t + 2 < tris.Length; t += 3)
                for (int c = 0; c < 3; c++)
                {
                    int i = tris[t + c], j = tris[t + (c + 1) % 3], k = tris[t + (c + 2) % 3];
                    if (i == j || j == k || i == k) continue;
                    var e1 = pos[i] - pos[k]; var e2 = pos[j] - pos[k];
                    double cr = Vector3.Cross(e1, e2).Length;
                    if (cr < 1e-12) continue;
                    double cot = Vector3.Dot(e1, e2) / cr;
                    long key = Key(i, j);
                    w2[key] = w2.GetValueOrDefault(key) + 0.5 * cot;
                }
            var nbr = new List<(int J, double W)>[n];
            for (int v = 0; v < n; v++) nbr[v] = new List<(int, double)>();
            foreach (var (key, w) in w2)
            {
                int a = (int)(key >> 32), b = (int)(key & 0xffffffff);
                double ww = Math.Clamp(w, 0.01, 20.0);
                nbr[a].Add((b, ww)); nbr[b].Add((a, ww));
            }

            // pieces with no handle would float free in the solve: carry those whole by their skinning
            var comp = new int[n]; Array.Fill(comp, -1);
            var fixedV = (bool[])handle.Clone();
            int fixedIslands = 0;
            for (int s = 0, id = 0; s < n; s++)
            {
                if (comp[s] >= 0) continue;
                var list = new List<int> { s }; comp[s] = id;
                for (int q = 0; q < list.Count; q++) foreach (var (j, _) in nbr[list[q]]) if (comp[j] < 0) { comp[j] = id; list.Add(j); }
                if (!list.Any(v => handle[v])) { foreach (int v in list) fixedV[v] = true; fixedIslands++; }
                id++;
            }

            var cur = new Vector3[n];
            for (int v = 0; v < n; v++) cur[v] = fixedV[v] && handle[v] ? Vector3.TransformPosition(pos[v], skin[bones[v * 4]]) : Lbs(v);
            var rot = new Quaternion[n];
            for (int v = 0; v < n; v++) rot[v] = BoneRot(v);

            // free-vertex numbering for the solve
            var idx = new int[n]; int nf = 0;
            for (int v = 0; v < n; v++) idx[v] = fixedV[v] ? -1 : nf++;
            var diag = new double[nf];
            for (int v = 0; v < n; v++) if (idx[v] >= 0) foreach (var (_, w) in nbr[v]) diag[idx[v]] += w;
            var bx = new double[nf]; var by = new double[nf]; var bz = new double[nf];
            var x = new double[nf]; var y = new double[nf]; var z = new double[nf];

            for (int it = 0; it < iterations; it++)
            {
                // local step: best rotation per vertex (fixed vertices keep their bone's rotation)
                for (int v = 0; v < n; v++)
                {
                    if (fixedV[v]) continue;
                    double sxx = 0, sxy = 0, sxz = 0, syx = 0, syy = 0, syz = 0, szx = 0, szy = 0, szz = 0;
                    foreach (var (j, w) in nbr[v])
                    {
                        var p = pos[v] - pos[j]; var q = cur[v] - cur[j];
                        sxx += w * p.X * q.X; sxy += w * p.X * q.Y; sxz += w * p.X * q.Z;
                        syx += w * p.Y * q.X; syy += w * p.Y * q.Y; syz += w * p.Y * q.Z;
                        szx += w * p.Z * q.X; szy += w * p.Z * q.Y; szz += w * p.Z * q.Z;
                    }
                    rot[v] = TrophyRigger.HornFromCovariance(sxx, sxy, sxz, syx, syy, syz, szx, szy, szz);
                }
                // global step: L p' = sum_j w_ij (R_i + R_j)/2 (p_i - p_j), fixed vertices on the right
                for (int v = 0; v < n; v++)
                {
                    int iv = idx[v]; if (iv < 0) continue;
                    double rx = 0, ry = 0, rz = 0;
                    foreach (var (j, w) in nbr[v])
                    {
                        var e = pos[v] - pos[j];
                        var a = Vector3.Transform(e, rot[v]); var c = Vector3.Transform(e, rot[j]);
                        rx += w * 0.5 * (a.X + c.X); ry += w * 0.5 * (a.Y + c.Y); rz += w * 0.5 * (a.Z + c.Z);
                        if (idx[j] < 0) { rx += w * cur[j].X; ry += w * cur[j].Y; rz += w * cur[j].Z; }
                    }
                    bx[iv] = rx; by[iv] = ry; bz[iv] = rz;
                    x[iv] = cur[v].X; y[iv] = cur[v].Y; z[iv] = cur[v].Z;
                }
                Cg(bx, x); Cg(by, y); Cg(bz, z);
                for (int v = 0; v < n; v++) { int iv = idx[v]; if (iv >= 0) cur[v] = new Vector3((float)x[iv], (float)y[iv], (float)z[iv]); }
            }
            log?.Invoke($"  unpose: {n} vertices, {handle.Count(h => h)} handles, {nf} solved, {fixedIslands} handle-less pieces carried whole, {iterations} ARAP iterations");
            return new Result { Pos = cur, Rot = rot, Handles = handle.Count(h => h), Free = nf, FixedIslands = fixedIslands };

            // conjugate gradient on the free block of the Laplacian (Jacobi preconditioner), warm-started
            void Cg(double[] b, double[] sol)
            {
                var r = new double[nf]; var zz = new double[nf]; var p = new double[nf]; var ap = new double[nf];
                void Mul(double[] src, double[] dst)
                {
                    for (int v = 0; v < n; v++)
                    {
                        int iv = idx[v]; if (iv < 0) continue;
                        double s = diag[iv] * src[iv];
                        foreach (var (j, w) in nbr[v]) { int ij = idx[j]; if (ij >= 0) s -= w * src[ij]; }
                        dst[iv] = s;
                    }
                }
                Mul(sol, ap);
                double bn = 0;
                for (int i = 0; i < nf; i++) { r[i] = b[i] - ap[i]; zz[i] = r[i] / diag[i]; p[i] = zz[i]; bn += b[i] * b[i]; }
                double rz = 0; for (int i = 0; i < nf; i++) rz += r[i] * zz[i];
                double tol = 1e-12 * Math.Max(bn, 1e-30);
                for (int k = 0; k < 800; k++)
                {
                    Mul(p, ap);
                    double pap = 0; for (int i = 0; i < nf; i++) pap += p[i] * ap[i];
                    if (pap <= 0) break;
                    double alpha = rz / pap, rr = 0;
                    for (int i = 0; i < nf; i++) { sol[i] += alpha * p[i]; r[i] -= alpha * ap[i]; rr += r[i] * r[i]; }
                    if (rr < tol) break;
                    double rzNew = 0; for (int i = 0; i < nf; i++) { zz[i] = r[i] / diag[i]; rzNew += r[i] * zz[i]; }
                    double beta = rzNew / rz; rz = rzNew;
                    for (int i = 0; i < nf; i++) p[i] = zz[i] + beta * p[i];
                }
            }
        }

        // The straightened version of a fitted rig: bound in the game's Idle f0 on the trophy's own proportions,
        // each shoulder at the minimax centre of its motion over every standing frame. Same bones, weights and
        // lengths; new bind pose, per-vertex bind positions + rotations, and attachment anchors carried along
        // (same place on their bone). `corners` = the trophy's triangle corners (trophy units).
        // tpose: instead of the game's stance, a literal T-pose -- arms straight out to the sides, level -- the pose
        // the hand-made rigs of these trophies are bound in.
        public static TrophyRig Straighten(Model model, TrophyRig rig, IReadOnlyList<Vector3> corners, Action<string>? log = null, bool tpose = false) =>
            Straighten(model, rig, corners, out _, log, tpose);

        // rigidHandles (the RIGID rig): EVERY vertex on one bone is a handle carried whole -- solid pieces must not be bent
        // near their joint on the way into the target pose; only the blend bands at joints are reshaped
        public static TrophyRig Straighten(Model model, TrophyRig rig, IReadOnlyList<Vector3> corners, out bool[] handle, Action<string>? log = null, bool tpose = false, bool rigidHandles = false)
        {
            var sk = new DsSkeleton(model);
            int nb = sk.Count, nv = rig.Vertices.Length;
            var bind = rig.Vertices.Select(v => Vector3.TransformPosition(v, rig.TrophyToRig)).ToArray();
            float height = bind.Max(p => p.Y) - bind.Min(p => p.Y);
            var pose = new SkeletonPose(nb); var world = new Matrix4[nb];
            // target skeleton: Idle f0 on the trophy's own proportions...
            var idleW = new Matrix4[nb];
            DsSkeleton.Sample(model, (int)PlayerAnimation.Idle, 0, pose);
            sk.Fk(pose, idleW, 0f, rig.Lengths, rig.Offsets);
            var order = new List<int>(); { var done = new bool[nb]; while (order.Count < nb) for (int i = 0; i < nb; i++) if (!done[i] && (sk.Parent[i] < 0 || done[sk.Parent[i]])) { done[i] = true; order.Add(i); } }
            Matrix4 Local(Matrix4[] w, int i) => sk.Parent[i] >= 0 ? w[i] * Matrix4.Invert(w[sk.Parent[i]]) : w[i];
            // ...with each shoulder at the minimax centre of its local rotation over every standing frame
            var centreBones = Enumerable.Range(0, nb).Where(i => sk.Names[i].Contains("shoulder") && sk.Parent[i] >= 0).ToList();
            var samples = centreBones.ToDictionary(i => i, _ => new List<Quaternion>());
            for (int c = 0; c < model.AnimationGroups.Node.Count; c++)
            {
                if (c is (int)PlayerAnimation.Morph or (int)PlayerAnimation.Unmorph) continue;
                for (int f = 0; f < model.AnimationGroups.Node[c].FrameCount; f++)
                {
                    DsSkeleton.Sample(model, c, f, pose);
                    sk.Fk(pose, world, 0f, rig.Lengths, rig.Offsets);
                    foreach (int i in centreBones) samples[i].Add(Local(world, i).ClearTranslation().ExtractRotation().Normalized());
                }
            }
            var targetW = (Matrix4[])idleW.Clone();
            var locals = Enumerable.Range(0, nb).Select(i => Local(idleW, i)).ToArray();
            foreach (int i in centreBones)
            {
                var cq = MinimaxCentre(samples[i]);
                var t = locals[i].ExtractTranslation();
                locals[i] = Matrix4.CreateFromQuaternion(cq) * Matrix4.CreateTranslation(t);
                var iq = Local(idleW, i).ClearTranslation().ExtractRotation().Normalized();
                float moved = 2f * MathF.Acos(Math.Clamp(MathF.Abs(cq.X * iq.X + cq.Y * iq.Y + cq.Z * iq.Z + cq.W * iq.W), 0f, 1f)) * 180f / MathF.PI;
                if (!tpose) log?.Invoke($"  unpose target: {sk.Names[i]} at the centre of its motion ({moved:0} deg from idle)");
            }
            var touched = new bool[nb];
            foreach (int i in centreBones) touched[i] = true;
            foreach (int i in order)
            {
                int par = sk.Parent[i];
                if (par >= 0 && touched[par]) touched[i] = true;
                if (touched[i] && par >= 0) targetW[i] = locals[i] * targetW[par];
            }
            if (tpose) targetW = TPose(model, sk, idleW, log);
            // handles: wholly on one bone and well away from its joint and its children's joints
            var joint = rig.BindWorld.Select(m => m.ExtractTranslation()).ToArray();
            var kids = Enumerable.Range(0, nb).Select(i => Enumerable.Range(0, nb).Where(c => sk.Parent[c] == i && (joint[c] - joint[i]).Length > 1e-4f).ToList()).ToArray();
            float R(int b)
            {
                float seg = kids[b].Count > 0 ? kids[b].Max(c => (joint[c] - joint[b]).Length) : 0.15f * height;
                return Math.Clamp(0.3f * seg, 0.05f * height, 0.15f * height);
            }
            handle = new bool[nv];
            for (int v = 0; v < nv; v++)
            {
                if (rig.Weights[v * 4] < 0.99f) continue;
                if (rigidHandles) { handle[v] = true; continue; }
                int b = rig.Bones[v * 4]; float r = R(b);
                if ((bind[v] - joint[b]).Length < r) continue;
                if (kids[b].Any(c => (bind[v] - joint[c]).Length < r)) continue;
                handle[v] = true;
            }
            // the surface the solve holds together: every drawn triangle -- NOT the bridges the fit cut (a hand or forearm
            // fused to another part) or left-out pieces: kept, they tied Samus's cannon to her body and the T-pose dragged
            // the joining vertex across her
            var triList = new List<int>(corners.Count);
            for (int i = 0; i + 2 < corners.Count; i += 3)
            {
                int a = rig.IndexOf(new System.Numerics.Vector3(corners[i].X, corners[i].Y, corners[i].Z));
                int b = rig.IndexOf(new System.Numerics.Vector3(corners[i + 1].X, corners[i + 1].Y, corners[i + 1].Z));
                int c = rig.IndexOf(new System.Numerics.Vector3(corners[i + 2].X, corners[i + 2].Y, corners[i + 2].Z));
                if (a < 0 || b < 0 || c < 0 || rig.HidesTriangle(a, b, c)) continue;
                triList.Add(a); triList.Add(b); triList.Add(c);
            }
            var tris = triList.ToArray();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var res = Solve(bind, tris, rig.Bones, rig.Weights, rig.BindWorld, targetW, handle, 12, log);
            log?.Invoke($"  unpose: solved in {sw.Elapsed.TotalSeconds:0.0}s");
            var inv = targetW.Select(m => Matrix4.Invert(m)).ToArray();
            var atts = rig.Attachments.Select(a => new TrophyRig.Attachment
            {
                Node = a.Node, Scale = a.Scale, Cut = a.Cut, HidesOwnMesh = a.HidesOwnMesh,
                Anchor = a.Anchor is Vector3 an ? Vector3.TransformPosition(an, rig.InvBind[a.Node] * targetW[a.Node]) : null,
            }).ToArray();
            var outRig = new TrophyRig
            {
                Rig = rig.Rig, TrophyToRig = rig.TrophyToRig, Lengths = rig.Lengths, Offsets = rig.Offsets,
                BindWorld = targetW, InvBind = inv, Vertices = rig.Vertices, Bones = rig.Bones, Weights = rig.Weights,
                ChamferStart = rig.ChamferStart, ChamferGlobal = rig.ChamferGlobal, ChamferFinal = rig.ChamferFinal,
                RefPose = rig.RefPose, Deviations = rig.Deviations, FittedDs = rig.FittedDs, FittedDsNode = rig.FittedDsNode,
                Attachments = atts, Hidden = rig.Hidden, BindPos = res.Pos, BindRot = res.Rot,
            };
            outRig.CutTris.UnionWith(rig.CutTris);
            outRig.BuildIndex();
            return outRig;
        }

        // Idle on the trophy's proportions with each arm turned straight out to its side, level: upper arm, forearm
        // and hand in turn (a leaf points along its own game mesh). DS rigs face -Z with L_ bones at -X.
        static Matrix4[] TPose(Model model, DsSkeleton sk, Matrix4[] idleW, Action<string>? log)
        {
            int nb = sk.Count;
            var w = (Matrix4[])idleW.Clone();
            // each node's game mesh centroid, bone-local (a leaf's direction)
            var poser = new BipedAnimator(model);
            poser.SetBoth(PlayerAnimation.Idle, AnimFlags.None);
            HunterRig.Pose(poser, 0f, new float[16 * 32]);
            var baked = MphRecomp.Render.GeometryBaker.Bake(model, new float[3], out _, useAnimation: true, useMatrixStack: true, skinned: true);
            const int S = MphRecomp.Render.GeometryBaker.StrideSkinned;
            var cen = new Vector3[model.Nodes.Count]; var cnt = new int[model.Nodes.Count];
            foreach (var b in baked)
                for (int i = 0; i + S <= b.Verts.Count; i += S)
                {
                    int node = model.NodeMatrixIds[(int)b.Verts[i + 11]];
                    cen[node] += new Vector3(b.Verts[i], b.Verts[i + 1], b.Verts[i + 2]); cnt[node]++;
                }
            bool Desc(int d, int a) { for (int x = d; x >= 0; x = sk.Parent[x]) if (x == a) return true; return false; }
            foreach (var (side, lateral) in new[] { ("L_", -Vector3.UnitX), ("R_", Vector3.UnitX) })
                foreach (string part in new[] { "shoulder", "elbow", "wrist" })
                {
                    int b = sk.IndexOf(side + part);
                    if (b < 0) continue;
                    var jb = w[b].ExtractTranslation();
                    int child = Enumerable.Range(0, nb).FirstOrDefault(c => sk.Parent[c] == b && (w[c].ExtractTranslation() - jb).Length > 1e-4f, -1);
                    Vector3 dir;
                    if (child >= 0) dir = w[child].ExtractTranslation() - jb;
                    else if (cnt[b] > 0) dir = Vector3.TransformVector(cen[b] / cnt[b], w[b]);
                    else continue;
                    if (dir.LengthSquared < 1e-10f) continue;
                    var turn = Matrix4.CreateTranslation(-jb) * Matrix4.CreateFromQuaternion(TrophyRigger.FromTo(dir, lateral)) * Matrix4.CreateTranslation(jb);
                    float deg = MathF.Acos(Math.Clamp(Vector3.Dot(dir.Normalized(), lateral), -1f, 1f)) * 180f / MathF.PI;
                    for (int d = 0; d < nb; d++) if (Desc(d, b)) w[d] = w[d] * turn;
                    log?.Invoke($"  unpose target (T-pose): {side}{part} turned {deg:0} deg to point straight out");
                }
            return w;
        }

        // Rotation-only minimax centre of a set of rotations (Badoiu-Clarkson on the rotation sphere): the
        // bind rotation that minimises the WORST bend to any of them -- the data's own "A-pose" for a joint.
        public static Quaternion MinimaxCentre(IReadOnlyList<Quaternion> qs, int iterations = 400)
        {
            static float Ang(Quaternion a, Quaternion b) => 2f * MathF.Acos(Math.Clamp(MathF.Abs(a.X * b.X + a.Y * b.Y + a.Z * b.Z + a.W * b.W), 0f, 1f));
            var c = qs[0];
            for (int k = 0; k < iterations; k++)
            {
                var far = qs[0]; float fa = -1;
                foreach (var q in qs) { float a = Ang(c, q); if (a > fa) { fa = a; far = q; } }
                if (c.X * far.X + c.Y * far.Y + c.Z * far.Z + c.W * far.W < 0) far = new Quaternion(-far.X, -far.Y, -far.Z, -far.W);
                c = Quaternion.Slerp(c, far, 1f / (k + 2)).Normalized();
            }
            return c;
        }
    }
}
