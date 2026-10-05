using System;
using System.Collections.Generic;
using System.Linq;
using MphRead;
using MphRead.Entities;
using MphRecomp.Render;
using OpenTK.Mathematics;

namespace MphRecomp.Anim
{
    // The result of rigging one Brawl trophy mesh to its DS hunter skeleton. Everything is DERIVED at
    // runtime from the user's own files (their ROM + their trophy export); nothing here ships.
    public sealed class TrophyRig
    {
        public string Rig = "";                 // DS hunter model name the skeleton comes from
        public Matrix4 TrophyToRig;             // trophy units -> DS model space (scale, facing, offset)
        public float[] Lengths = Array.Empty<float>();      // per node: bone-length factor vs the DS rig
        public Vector3?[] Offsets = Array.Empty<Vector3?>(); // per node: exact rest offset (landmark rigs), or null
        public Matrix4[] BindWorld = Array.Empty<Matrix4>(); // per node: fitted pose (rig space)
        public Matrix4[] InvBind = Array.Empty<Matrix4>();
        public Vector3[] Vertices = Array.Empty<Vector3>();  // unique trophy vertices, trophy units
        public int[] Bones = Array.Empty<int>();             // 4 node indices per vertex
        public float[] Weights = Array.Empty<float>();       // 4 weights per vertex (sum 1)
        public float ChamferStart, ChamferGlobal, ChamferFinal; // fit quality, fraction of body height
        public string RefPose = "";
        public string Deviations = "";   // per bone: swing/twist (degrees) away from the reference game pose
        // diagnostics: the fitted DS template (rig space) and each point's bone
        public Vector3[] FittedDs = Array.Empty<Vector3>();
        public int[] FittedDsNode = Array.Empty<int>();
        // game-model parts drawn on the trophy (landmarks "replace" / "attach"): the DS mesh of bone Node --
        // whole, or only beyond Cut along its axis -- scaled, and seated at Anchor (rig space) when given
        public sealed class Attachment { public int Node; public float Scale = 1f, Cut; public Vector3? Anchor; public bool HidesOwnMesh; }
        public Attachment[] Attachments = Array.Empty<Attachment>();
        public bool[] Hidden = Array.Empty<bool>();   // per vertex: on a left-out mesh piece (landmark "hide")
        // straightened rig (TrophyUnpose.Straighten): per vertex, the bind position (rig space) in the straightened
        // pose and the rotation that took it there (turns its authored normal); BindWorld is then that pose.
        // Null = bound as sculpted: bind position = TrophyToRig * Vertices.
        public Vector3[]? BindPos;
        public Quaternion[]? BindRot;
        public TrophyRig Copy() => (TrophyRig)MemberwiseClone();   // shallow: callers replace the arrays they change
        // triangles left out because the sculpt WELDED a hand onto another part (Samus's left hand gripping her cannon,
        // a hand on a hip): they join bones far apart in the skeleton, and the game's clips pull those apart, so the
        // bridge would stretch into a sheet. Keyed by the triangle's sorted vertex indices.
        public readonly HashSet<(int, int, int)> CutTris = new();
        public static (int, int, int) TriKey(int a, int b, int c)
        {
            if (a > b) (a, b) = (b, a);
            if (b > c) (b, c) = (c, b);
            if (a > b) (a, b) = (b, a);
            return (a, b, c);
        }

        // weight a vertex puts on bones whose trophy part is replaced; a triangle mostly on them is not drawn
        public float ReplacedWeight(int v)
        {
            float w = 0;
            for (int k = 0; k < 4; k++) foreach (var a in Attachments) if (a.HidesOwnMesh && Bones[v * 4 + k] == a.Node) w += Weights[v * 4 + k];
            return w;
        }
        public bool HidesTriangle(int a, int b, int c) =>
            (Hidden.Length > 0 && (Hidden[a] || Hidden[b] || Hidden[c])) ||
            (CutTris.Count > 0 && CutTris.Contains(TriKey(a, b, c))) ||
            (Attachments.Length > 0 && ReplacedWeight(a) + ReplacedWeight(b) + ReplacedWeight(c) >= 1.5f);

        readonly Dictionary<(float, float, float), int> _index = new();
        public int IndexOf(System.Numerics.Vector3 p) =>
            _index.TryGetValue((p.X, p.Y, p.Z), out int i) ? i : -1;
        internal void BuildIndex() { _index.Clear(); for (int i = 0; i < Vertices.Length; i++) _index[(Vertices[i].X, Vertices[i].Y, Vertices[i].Z)] = i; }
    }

    // Fits the DS skeleton to a trophy's sculpted pose, then skins the trophy to it.
    //
    //  1. The DS mesh (the ROM's own hunter model, posed by our exact FK) is the fitting template: each
    //     DS vertex belongs to one bone. Energy = symmetric Chamfer distance between the posed DS mesh
    //     and the trophy's vertices, each nearest-distance truncated (robust to trophy-only details like
    //     Trace's spikes), measured in DS units.
    //  2. Global fit: scale, facing (yaw) and offset, from several candidate reference poses (Idle and
    //     the best-matching clip frames); keep the best.
    //  3. Articulated fit: per node, a local rotation (coordinate descent on small rotations about the
    //     node's axes, coarse to fine, root -> leaves) and a bone-length factor. Limb roots additionally
    //     get a randomized global search so a raised arm can swing up from a lowered start.
    //  4. Skinning: each trophy vertex takes weights from its K nearest fitted DS vertices' bones
    //     (inverse-square distance), then weights are smoothed over the trophy's own mesh edges so
    //     joints bend instead of tearing; top 4 kept.
    // Retargeted playback then uses the clip's rotations with the fitted bone lengths (DsSkeleton.Fk
    // with Lengths), and skin matrix = InvBind * world.
    public static class TrophyRigger
    {
        public const float Truncate = 0.25f;   // DS units (~12% of a hunter's height)
        public const float MinLength = 0.75f, MaxLength = 1.33f, LengthPenalty = 0.01f;
        public const float SmoothRadius = 0.03f; // DS units (~2% of a hunter's height)
        public const float BlendBand = 0.04f;    // joint blend half-width, fraction of body height
        public const float RigidIslandFraction = 0.15f; // mesh islands smaller than this share of vertices move rigidly...
        public const float RigidDominance = 0.8f;        // ...when at least this share of the piece belongs to one bone
        [ThreadStatic] public static int RigidIslands;
        public const float OwnerRadius = 0.03f;
        public const float CoincidentJoint = 0.02f; // an unmarked child this close (fraction of height) is looked through
        // Rotation prior (per bone, energy units per radian^2): the surface distance barely changes when a
        // round-ish part (head, chest, hips) spins about itself, so without a prior the fit parks bones
        // at arbitrary twists that the game's clips then faithfully reproduce -- a sideways head. Twist
        // about the bone's own axis is almost never justified by shape, so it costs far more than swing.
        public const float SwingPrior = 0.0015f, TwistPrior = 0.02f, LeafPrior = 0.01f;

        public sealed class Options
        {
            public int Seed = 1234;
            public int TrophySample = 2500;
            public int LimbSearch = 400;
            public int Sweeps = 4;
            public int SmoothIterations = 1;
            public float RotPriorScale = 1f;   // 0 = no rotation prior (diagnostic A/B)
            public bool IslandScopedOwnership = true; // false = any segment may claim any mesh piece (diagnostic A/B)
            // share of a bone segment that must run through (or next to) a mesh piece for the bone to claim it:
            // an arm bone that merely STARTS inside the chest armour must not drag the chest's shoulder corners
            public float PieceClaimFraction = 0.3f;
            // CLEAN BOUNDARIES (how hand-made rigs of these trophies are weighted): split the surface into panels
            // at its own creases (adjacent faces meeting at more than PanelCreaseDeg); a panel that one bone owns
            // at least PanelDominance of moves wholly with that bone, and where two such panels of different
            // bones meet the crease line is shared between them. Panels no bone dominates (a continuous sleeve
            // over an elbow) keep the joint-plane blending. 0 = off.
            public float PanelDominance = 0f;
            public float PanelCreaseDeg = 35f;
            // width of the blend band at joints, as a multiple of BlendBand: 1 = a soft bend (skin), small = a narrow
            // hinge line (chitin / armour plates that pivot at the joint instead of bending like flesh)
            public float BlendScale = 1f;
            // leave out triangles that weld a hand to a part at least this many skeleton hops away (see TrophyRig.CutTris);
            // 0 = off. Only hands: a cloak from shoulders to waist, or legs growing from the chest, are real surface.
            public int CutHandWeldHops = 4;
            // ...and triangles joining ANY two bones at least this far apart (a forearm fused to a hip, an arm resting on a
            // thigh): never real surface on a biped -- a cloak from shoulders to waist is 4 hops. 0 = off.
            public int CutFarWeldHops = 5;
            // landmark ownership by line of sight (see the owner step): a bone takes only surface it sees from inside the
            // body; competitors up to SightRatio x the nearest distance (+ SightSlack x height) are considered
            public bool SightOwnership = true;
            // per trophy corner: which material (mesh) it belongs to (same numbering for the same material name); lets a
            // small separate piece go with the bone its material belongs to (see the material-kinship step). null = off.
            public int[]? CornerMaterial;
            public float SightRatio = 2.5f, SightSlack = 0.03f;
            // turn the hips/chest/head binds so each part faces the way the game model's does (see TrophyFacing)
            public bool AlignFacing = true;
            // landmark fit: knee/elbow hinge twist, default foot up, pelvis-between-hips roll, clips' root height from leg
            // length (switches for A/B)
            public bool AlignHinges = true, FootUp = true, PelvisRoll = true, RootFromLegs = true;
            // The user's chunk grouping (Rig Studio): owner bone per trophy vertex (-1 = automatic) and seam
            // behaviour per joint. Where given it decides ownership outright; chunks move rigidly and only
            // seams set to blend get a narrow blend band.
            public TrophyChunks? Chunks;
            // Joint landmarks in TROPHY coordinates (node name -> position; "<leaf>.center" = centre of a
            // leaf's body part; optional X mirror and extra ownership segments). When given, the skeleton
            // is solved onto them directly instead of by shape matching -- robust for dramatic trophy
            // poses. See TrophyLandmarks.
            public LandmarkSet? Landmarks;
            public Action<string>? Log;
        }

        public static TrophyRig Fit(Model model, IReadOnlyList<Vector3> trophyCorners, Options? opt = null)
        {
            opt ??= new Options();
            var log = opt.Log ?? (_ => { });
            var sk = new DsSkeleton(model);
            int n = sk.Count;

            // --- trophy: unique vertices + triangle adjacency (corners come as triangle triples) ---
            var rig = new TrophyRig { Rig = model.Name };
            var uniq = new List<Vector3>(); var map = new Dictionary<(float, float, float), int>();
            var tri = new int[trophyCorners.Count];
            for (int i = 0; i < trophyCorners.Count; i++)
            {
                var p = trophyCorners[i]; var key = (p.X, p.Y, p.Z);
                if (!map.TryGetValue(key, out int idx)) { idx = uniq.Count; map[key] = idx; uniq.Add(p); }
                tri[i] = idx;
            }
            rig.Vertices = uniq.ToArray(); rig.BuildIndex();
            var adj = new HashSet<int>[uniq.Count];
            for (int i = 0; i < uniq.Count; i++) adj[i] = new HashSet<int>();
            for (int t = 0; t + 2 < tri.Length; t += 3)
            {
                int a = tri[t], b = tri[t + 1], c = tri[t + 2];
                adj[a].Add(b); adj[a].Add(c); adj[b].Add(a); adj[b].Add(c); adj[c].Add(a); adj[c].Add(b);
            }

            // --- DS template: skinned local vertices + their node (via the matrix-stack slot) ---
            var poser = new BipedAnimator(model);
            poser.SetBoth(PlayerAnimation.Idle, AnimFlags.None);
            HunterRig.Pose(poser, 0f, new float[16 * 32]);
            var baked = GeometryBaker.Bake(model, new float[3], out _, useAnimation: true, useMatrixStack: true, skinned: true);
            var dsLocal = new List<Vector3>(); var dsNode = new List<int>();
            var seen = new HashSet<(float, float, float, int)>();
            foreach (var b in baked)
                for (int i = 0; i + 11 < b.Verts.Count; i += 12)
                {
                    int node = model.NodeMatrixIds[(int)b.Verts[i + 11]];
                    if (!seen.Add((b.Verts[i], b.Verts[i + 1], b.Verts[i + 2], node))) continue;
                    dsLocal.Add(new Vector3(b.Verts[i], b.Verts[i + 1], b.Verts[i + 2])); dsNode.Add(node);
                }
            float mscale = model.Scale.X;

            // fit state: per-node local rotation (matrix), scale + translation from the reference pose,
            // and the length factor
            var S = new Vector3[n]; var T = new Vector3[n]; var Q = new Matrix4[n]; var K = new float[n]; var animated = new bool[n];
            var Off = new Vector3?[n];   // exact rest offsets (landmark mode only)
            var world = new Matrix4[n];
            var order = new List<int>();
            { var placed = new bool[n]; while (order.Count < n) for (int i = 0; i < n; i++) if (!placed[i] && (sk.Parent[i] < 0 || placed[sk.Parent[i]])) { placed[i] = true; order.Add(i); } }
            void FitFk()
            {
                // same composition as DsSkeleton.Fk (Scale * Rot * Translate(T * length), times parent)
                foreach (int i in order)
                {
                    if (!animated[i]) { world[i] = Matrix4.Identity; continue; }
                    var t = (Off[i] ?? T[i] * K[i]) / mscale;
                    Matrix4 w = Matrix4.CreateScale(S[i]) * Q[i] * Matrix4.CreateTranslation(t);
                    if (sk.Parent[i] >= 0) w *= world[sk.Parent[i]];
                    world[i] = w;
                }
            }
            var dsPosed = new Vector3[dsLocal.Count];
            void PoseDs() { FitFk(); for (int v = 0; v < dsLocal.Count; v++) dsPosed[v] = Vector3.TransformPosition(dsLocal[v], world[dsNode[v]]) * mscale; }

            void LoadRef(int clip, int frame)
            {
                var pose = new SkeletonPose(n);
                DsSkeleton.Sample(model, clip, frame, pose);
                for (int i = 0; i < n; i++)
                {
                    S[i] = pose.S[i]; T[i] = pose.T[i]; animated[i] = pose.Animated[i]; K[i] = 1f;
                    Q[i] = Matrix4.CreateRotationX(pose.R[i].X) * Matrix4.CreateRotationY(pose.R[i].Y) * Matrix4.CreateRotationZ(pose.R[i].Z);
                }
            }

            // --- trophy sample in rig space under the current global transform G ---
            var rng = new Random(opt.Seed);
            var sampleIdx = Enumerable.Range(0, uniq.Count).OrderBy(_ => rng.Next()).Take(Math.Min(opt.TrophySample, uniq.Count)).ToArray();
            float gScale = 1, gYaw = MathF.PI; Vector3 gOff = Vector3.Zero;
            // gMirror (landmark "mirror"): the trophy's X is flipped first, so a sculpt that is the game
            // model's mirror image (weapon on the other arm) drives the same limbs as the game's clips.
            // A reflection keeps normals outward (they transform by the inverse transpose) and only flips
            // triangle winding, which nothing downstream culls on.
            bool gMirror = false;
            Matrix4 G() => Matrix4.CreateScale(gMirror ? -gScale : gScale, gScale, gScale) * Matrix4.CreateRotationY(gYaw) * Matrix4.CreateTranslation(gOff);
            var trophyRig = new Vector3[sampleIdx.Length];
            var trophyGrid = new Grid(0.125f);
            void PlaceTrophy()
            {
                var g = G();
                for (int i = 0; i < sampleIdx.Length; i++) trophyRig[i] = Vector3.TransformPosition(uniq[sampleIdx[i]], g);
                trophyGrid.Build(trophyRig);
            }
            var dsGrid = new Grid(0.125f);
            float Energy()
            {
                dsGrid.Build(dsPosed);
                double a = 0, b = 0;
                foreach (var p in dsPosed) a += MathF.Min(Truncate, trophyGrid.Nearest(p, trophyRig, Truncate));
                foreach (var p in trophyRig) b += MathF.Min(Truncate, dsGrid.Nearest(p, dsPosed, Truncate));
                return (float)(a / dsPosed.Length + b / trophyRig.Length) / 2f;
            }

            // body height of the DS reference (for reporting as a fraction) and trophy extents
            float dsHeight;
            {
                LoadRef((int)PlayerAnimation.Idle, 0); PoseDs();
                dsHeight = dsPosed.Max(p => p.Y) - dsPosed.Min(p => p.Y);
            }
            float tMinY = uniq.Min(p => p.Y), tMaxY = uniq.Max(p => p.Y);
            float tCx = uniq.Average(p => p.X), tCz = uniq.Average(p => p.Z);

            // initial global placement for the current DS pose: same height, feet on the same floor, XZ
            // centroids together, facing = yaw0
            void AlignGlobal(float yaw0)
            {
                float h = dsPosed.Max(p => p.Y) - dsPosed.Min(p => p.Y);
                var dsC = new Vector3(dsPosed.Average(p => p.X), dsPosed.Min(p => p.Y), dsPosed.Average(p => p.Z));
                gScale = h / (tMaxY - tMinY); gYaw = yaw0;
                var tc = Vector3.TransformPosition(new Vector3(tCx, tMinY, tCz), Matrix4.CreateScale(gScale) * Matrix4.CreateRotationY(gYaw));
                gOff = dsC - tc;
            }

            // shared by both solve modes (and reported afterwards)
            Matrix4[] qRef;
            Vector3?[] leafEnd = new Vector3?[n];   // landmark mode: where each leaf bone's part points (rig space)
            bool landmarkMode = opt.Landmarks != null;
            int rootRunIdx = -1; float rootRunK = 1f;   // landmark mode: the root height the game's clips play at (see below)
            var boneAxis = new Vector3?[n];
            var mirror = new int[n];
            for (int i = 0; i < n; i++)
            {
                string nm = sk.Names[i];
                mirror[i] = nm.StartsWith("L_") ? sk.IndexOf("R_" + nm[2..]) : nm.StartsWith("R_") ? sk.IndexOf("L_" + nm[2..]) : -1;
            }
            void SetBoneAxes()
            {
                for (int i = 0; i < n; i++)
                    for (int c = 0; c < n; c++)
                        if (sk.Parent[c] == i && T[c].LengthSquared > 1e-8f) { boneAxis[i] = T[c].Normalized(); break; }
            }

            if (opt.Landmarks != null)
            {
                // ===== LANDMARK MODE: solve the skeleton onto marked joints =====
                var L = opt.Landmarks.Joints;
                gMirror = opt.Landmarks.MirrorX;
                bool Has(string nm) => L.ContainsKey(nm);
                LoadRef((int)PlayerAnimation.Idle, 0);
                qRef = (Matrix4[])Q.Clone();
                SetBoneAxes();
                PoseDs();
                var dsJ = new Vector3[n];
                for (int i = 0; i < n; i++) dsJ[i] = world[i].ExtractTranslation() * mscale;
                float dsFloor = dsPosed.Min(p => p.Y);
                // each bone's "up" in its own frame: the direction that points straight up while the game
                // model stands in Idle f0 (what a "<bone>.up" landmark is matched to)
                var upLocal = new Vector3[n];
                // ...and its "forward": the way the game model FACES (-Z) in Idle f0 (what a "<bone>.fwd" landmark is matched to)
                var fwdLocal = new Vector3[n];
                for (int i = 0; i < n; i++)
                {
                    var r3 = world[i].ClearTranslation();
                    bool ok = MathF.Abs(r3.Determinant) > 1e-12f;
                    upLocal[i] = ok ? Vector3.TransformVector(Vector3.UnitY, Matrix4.Invert(r3)) : Vector3.UnitY;
                    fwdLocal[i] = ok ? Vector3.TransformVector(-Vector3.UnitZ, Matrix4.Invert(r3)) : -Vector3.UnitZ;
                }

                // facing: the trophy's "left" axis from its left-minus-right joint pairs; the DS rig's left
                // is -X while it faces -Z, so facing = left x up
                var left = Vector3.Zero;
                foreach (var (l, r) in new[] { ("L_hip", "R_hip"), ("L_shoulder", "R_shoulder"), ("L_collar", "R_collar"), ("L_knee", "R_knee") })
                    if (Has(l) && Has(r)) left += L[l] - L[r];
                if (gMirror) left.X = -left.X;
                left.Y = 0;
                if (left.LengthSquared < 1e-12f) throw new ArgumentException("landmarks need at least one L_/R_ pair to find the facing");
                left.Normalize();
                var facingT = Vector3.Cross(left, Vector3.UnitY);
                float bestYaw = 0, bestDot = -2;
                for (int k = 0; k < 7200; k++)
                {
                    float yaw = k * MathF.PI * 2f / 7200f;
                    float d = Vector3.Dot(Vector3.TransformNormal(facingT, Matrix4.CreateRotationY(yaw)), -Vector3.UnitZ);
                    if (d > bestDot) { bestDot = d; bestYaw = yaw; }
                }
                // scale: summed length of every landmarked bone (to its nearest landmarked ancestor), DS/trophy
                int Anc(int c) { for (int a = sk.Parent[c]; a >= 0; a = sk.Parent[a]) if (Has(sk.Names[a])) return a; return -1; }
                double dsLen = 0, tLen = 0;
                for (int c = 0; c < n; c++)
                {
                    if (!Has(sk.Names[c])) continue;
                    int a = Anc(c);
                    if (a < 0) continue;
                    dsLen += (dsJ[c] - dsJ[a]).Length; tLen += (L[sk.Names[c]] - L[sk.Names[a]]).Length;
                }
                gScale = (float)(dsLen / Math.Max(1e-9, tLen)); gYaw = bestYaw; gOff = Vector3.Zero;
                // feet on the DS floor; the pelvis landmark over the DS root in XZ
                var rootName = Has("Skeleton_Root") ? "Skeleton_Root" : Has("Pelvis") ? "Pelvis" : throw new ArgumentException("landmarks need Skeleton_Root (pelvis centre)");
                var rootR = Vector3.TransformPosition(L[rootName], G());
                float minY = uniq.Min(q => Vector3.TransformPosition(q, G()).Y);
                int rootIdx = sk.IndexOf("Skeleton_Root");
                gOff = new Vector3(dsJ[rootIdx].X - rootR.X, dsFloor - minY, dsJ[rootIdx].Z - rootR.Z);
                PlaceTrophy();
                rig.ChamferStart = rig.ChamferGlobal = Energy() / dsHeight;
                rig.RefPose = "landmarks (on Idle f0)";
                Vector3 Lr(string nm) => Vector3.TransformPosition(L[nm], G());
                log($"  landmark global: scale {gScale:0.0000}, yaw {gYaw * 180 / MathF.PI:0}{(gMirror ? ", X mirrored" : "")}, {L.Count} landmarks, E={rig.ChamferGlobal:0.0000}");

                // hip height: the root's translation length factor puts Skeleton_Root at the pelvis landmark
                // height (its offset measured through the parent node's transform -- Trace's Dummy_Root is
                // offset and scaled, Weavel's is not); then the trophy slides in XZ so the root sits exactly
                // on its landmark
                {
                    FitFk();
                    int par = sk.Parent[rootIdx];
                    var baseT = par >= 0 ? world[par].ExtractTranslation() * mscale : Vector3.Zero;
                    var offW = par >= 0 ? Vector3.TransformVector(T[rootIdx] / mscale, world[par]) * mscale : T[rootIdx];
                    if (MathF.Abs(offW.Y) > 1e-4f) K[rootIdx] = Math.Clamp((Lr(rootName).Y - baseT.Y) / offW.Y, 0.5f, 2f);
                    FitFk();
                    var rootW = world[rootIdx].ExtractTranslation() * mscale;
                    var lr = Lr(rootName);
                    gOff += new Vector3(rootW.X - lr.X, 0f, rootW.Z - lr.Z);
                    PlaceTrophy();
                }
                // local centroid of each node's own DS vertices (a leaf's part direction)
                var localCentroid = new Vector3?[n];
                {
                    var lsum = new Vector3[n]; var lcnt = new int[n];
                    for (int v = 0; v < dsLocal.Count; v++) { lsum[dsNode[v]] += dsLocal[v]; lcnt[dsNode[v]]++; }
                    for (int i = 0; i < n; i++) if (lcnt[i] > 0) localCentroid[i] = lsum[i] / lcnt[i];
                }
                // hinges (see SolveTopDown): for a bone aimed at ONE marked child c that has a marked far end (its own
                // marked child joint, or its part centre), the axis the game's c joint bends about -- in the bone's own
                // frame, from the frame of the standing clips where it bends most (hinges barely bend in Idle)
                int EndChild(int c) { for (int g = 0; g < n; g++) if (sk.Parent[g] == c && Has(sk.Names[g]) && T[g].LengthSquared >= 1e-8f) return g; return -1; }
                Vector3? HingeEnd(int c) => EndChild(c) is int g && g >= 0 ? Lr(sk.Names[g]) : Has(sk.Names[c] + ".center") ? Lr(sk.Names[c] + ".center") : null;
                var hingeLocal = new Vector3?[n];
                {
                    var cand = new List<(int I, int C, int G)>();
                    for (int i = 0; i < n; i++)
                    {
                        if (!animated[i] || sk.Parent[i] < 0) continue;
                        if (!(sk.Names[i].EndsWith("_hip") || sk.Names[i].EndsWith("_shoulder"))) continue;   // knees and elbows only
                        var hk = Enumerable.Range(0, n).Where(c => sk.Parent[c] == i && Has(sk.Names[c]) && T[c].LengthSquared >= 1e-8f).ToList();
                        if (hk.Count != 1 || HingeEnd(hk[0]) == null) continue;
                        int gi = EndChild(hk[0]);
                        if (gi < 0 && localCentroid[hk[0]] == null) continue;
                        cand.Add((i, hk[0], gi));
                    }
                    var hp = new SkeletonPose(n); var hw = new Matrix4[n]; var best = new float[n];
                    for (int clip = 0; clip < model.AnimationGroups.Node.Count; clip++)
                    {
                        if (clip is (int)PlayerAnimation.Morph or (int)PlayerAnimation.Unmorph) continue;
                        for (int f = 0; f < model.AnimationGroups.Node[clip].FrameCount; f++)
                        {
                            DsSkeleton.Sample(model, clip, f, hp); sk.Fk(hp, hw);
                            foreach (var (i, c, g) in cand)
                            {
                                var jc = hw[c].ExtractTranslation();
                                var gp = g >= 0 ? hw[g].ExtractTranslation() : jc + Vector3.TransformVector(localCentroid[c]!.Value, hw[c]);
                                var a = jc - hw[i].ExtractTranslation(); var b = gp - jc;
                                if (a.LengthSquared < 1e-12f || b.LengthSquared < 1e-12f) continue;
                                float bend = MathF.Acos(Math.Clamp(Vector3.Dot(a.Normalized(), b.Normalized()), -1f, 1f)) * 180f / MathF.PI;
                                if (bend <= best[i]) continue;
                                best[i] = bend;
                                hingeLocal[i] = Vector3.TransformVector(Vector3.Cross(a, b).Normalized(), Matrix4.Invert(hw[i].ClearTranslation())).Normalized();
                            }
                        }
                    }
                    foreach (var (i, _, _) in cand) if (best[i] < 20f || !opt.AlignHinges) hingeLocal[i] = null;   // never really bends: no hinge to match
                    log("  hinges (game's biggest bend): " + string.Join(" ", cand.Where(q => hingeLocal[q.I] != null).Select(q => $"{sk.Names[q.C]} {best[q.I]:0}deg")));
                }
                // exact joint offset of landmarked child c, measured in its parent's (current) frame
                void SetExactOffset(int c)
                {
                    int par = sk.Parent[c];
                    if (par < 0 || c == rootIdx || !Has(sk.Names[c]) || T[c].LengthSquared < 1e-8f) return;
                    FitFk();
                    var worldOffset = Lr(sk.Names[c]) - world[par].ExtractTranslation() * mscale;
                    Off[c] = Vector3.TransformVector(worldOffset, Matrix4.Invert(world[par].ClearTranslation())) * mscale;
                }
                // exact: each bone's children are pinned to their landmarks as soon as the bone is aimed, so
                // every bone below is aimed FROM its true joint (with L/R-averaged lengths an asymmetric
                // sculpt would otherwise aim a limb from a point that isn't the joint)
                void SolveTopDown(bool setLengths, bool exact = false)
                {
                    foreach (int i in order)
                    {
                        if (!animated[i] || sk.Parent[i] < 0) continue;
                        FitFk();
                        var Ji = world[i].ExtractTranslation() * mscale;
                        var pairs = new List<(Vector3 Cur, Vector3 Tgt, int Child)>();
                        // "<bone>.fwd" AND "<bone>.up" on a bone WITH children (a chest, the hips): the piece's own facing
                        // decides its whole turn, and its children are pinned to their marks by the exact offsets below.
                        // Aiming a chest at its neck and shoulder joints instead tilts it by however far the statue's neck
                        // juts ahead of its chest armour (Sylux's chest leant 20-35 deg forward in the standing Idle).
                        bool ownFacing = Has(sk.Names[i] + ".fwd") && Has(sk.Names[i] + ".up") && Enumerable.Range(0, n).Any(c => sk.Parent[c] == i && Has(sk.Names[c]));
                        if (ownFacing)
                        {
                            pairs.Add((Vector3.TransformVector(fwdLocal[i], world[i]), Lr(sk.Names[i] + ".fwd") - Ji, -1));
                            pairs.Add((Vector3.TransformVector(upLocal[i], world[i]), Lr(sk.Names[i] + ".up") - Ji, -1));
                        }
                        for (int c = 0; c < n && !ownFacing; c++)
                        {
                            if (sk.Parent[c] != i || T[c].LengthSquared < 1e-8f) continue;
                            // a child MARKED on top of this joint (seeded landmarks put Pelvis and Spine_1 exactly on the
                            // root) gives no direction at all -- aiming at it turned the root at random and handed a
                            // backwards spine to everything above; look through it like an unmarked one
                            bool markedOnTop = Has(sk.Names[c]) && (Lr(sk.Names[c]) - Ji).Length < CoincidentJoint * dsHeight;
                            if (Has(sk.Names[c]) && !markedOnTop)
                            {
                                var cur = Vector3.TransformVector(T[c] * K[c] / mscale, world[i]) * mscale;
                                pairs.Add((cur, Lr(sk.Names[c]) - Ji, c));
                            }
                            else if (markedOnTop || (T[c] * K[c]).Length < CoincidentJoint * dsHeight)
                                // look through an unmarked joint sitting on this one (Spine_1 on the root):
                                // its marked children still say how this bone is turned. Without it the
                                // root sees only the two hips -- nearly opposite directions, so its tilt
                                // about the hip line would be left to chance.
                                for (int g = 0; g < n; g++)
                                    if (sk.Parent[g] == c && Has(sk.Names[g]) && T[g].LengthSquared >= 1e-8f)
                                        pairs.Add((world[g].ExtractTranslation() * mscale - Ji, Lr(sk.Names[g]) - Ji, -1));
                        }
                        // "<bone>.fwd" (a point straight ahead of the part, the way it looks as sculpted): the part faces there when
                        // the game model faces forward in Idle f0 -- with "<bone>.up" this is its whole turn. Aiming by the part's
                        // CENTRE instead is unstable when the joint sits near that centre (Retro's head pivot is almost in the
                        // middle of the helmet: the short joint-to-centre line tipped every copied helmet 75-80 deg down)
                        if (pairs.Count == 0 && Has(sk.Names[i] + ".fwd"))
                            pairs.Add((Vector3.TransformVector(fwdLocal[i], world[i]), Lr(sk.Names[i] + ".fwd") - Ji, -1));
                        if (pairs.Count == 0 && Has(sk.Names[i] + ".center") && localCentroid[i] != null)
                            pairs.Add((Vector3.TransformVector(localCentroid[i]!.Value, world[i]) * mscale, Lr(sk.Names[i] + ".center") - Ji, -1));
                        if (pairs.Count == 0) continue;
                        if (opt.PelvisRoll && pairs.Count == 2 && Vector3.Dot(pairs[0].Tgt.Normalized(), pairs[1].Tgt.Normalized()) < -0.8f && Has("Spine_2") && sk.IndexOf("Spine_2") is int sp && sp >= 0 && sp != i)
                        {
                            // two nearly opposite directions fix a line, not a rotation: the pelvis between its hips could roll
                            // any way about the hip line (a kneeling sculpt flipped it upside down) -- the spine settles it
                            pairs.Add((world[sp].ExtractTranslation() * mscale - Ji, Lr("Spine_2") - Ji, -1));
                        }
                        Quaternion d = pairs.Count == 1 ? FromTo(pairs[0].Cur, pairs[0].Tgt)
                                                        : Horn(pairs.Select(q => (q.Cur.Normalized(), q.Tgt.Normalized())).ToList());
                        // "<bone>.up": a single direction leaves the spin about it free (FromTo takes the
                        // least rotation). The marked up side fixes that spin: the part turns about its
                        // aimed axis until its game-idle "up" faces the mark (a foot's sole flat, a gun's top up).
                        if (pairs.Count == 1 && Has(sk.Names[i] + ".up"))
                        {
                            var ax = pairs[0].Tgt.Normalized();
                            var u1 = Vector3.Transform(Vector3.TransformVector(upLocal[i], world[i]), d);
                            var ut = Lr(sk.Names[i] + ".up") - Ji;
                            d = Twist(ax, u1, ut) * d;
                        }
                        // no up mark on a FOOT: its top faces the shin (true for a planted foot and a foot on its toes
                        // alike) -- otherwise the least rotation can leave a kneeling trophy's foot rolled onto its side
                        else if (opt.FootUp && pairs.Count == 1 && sk.Names[i].Contains("ankle") && sk.Parent[i] >= 0 && Has(sk.Names[sk.Parent[i]]))
                        {
                            var ax = pairs[0].Tgt.Normalized();
                            var shin = Lr(sk.Names[sk.Parent[i]]) - Ji;
                            var ut = shin - ax * Vector3.Dot(shin, ax);
                            // only when foot and shin are clearly at an angle: a part centre marked straight below the
                            // ankle (Weavel's) runs along the shin, and "towards the shin" then says nothing
                            float footShin = MathF.Acos(Math.Clamp(Vector3.Dot(ax, shin.Normalized()), -1f, 1f)) * 180f / MathF.PI;
                            if (footShin > 35f && footShin < 145f)
                                d = Twist(ax, Vector3.Transform(Vector3.TransformVector(upLocal[i], world[i]), d), ut) * d;
                        }
                        // a KNEE or ELBOW is a hinge: turn the upper bone about its aimed axis so the joint below bends
                        // about the same axis as the game's does (taken from the frame of the clips where it bends
                        // most). The least rotation alone leaves that spin to chance on a limb sculpted bent (a
                        // kneeling leg, a raised arm), and the armour on it then faces the wrong way in every clip.
                        else if (pairs.Count == 1 && hingeLocal[i] is Vector3 hl && pairs[0].Child >= 0 && HingeEnd(pairs[0].Child) is Vector3 gT)
                        {
                            var ax = pairs[0].Tgt.Normalized();
                            var toC = pairs[0].Tgt; var toG = gT - Lr(sk.Names[pairs[0].Child]);
                            float bend = MathF.Acos(Math.Clamp(Vector3.Dot(toC.Normalized(), toG.Normalized()), -1f, 1f)) * 180f / MathF.PI;
                            if (bend > 12f)
                            {
                                var hT = Vector3.Cross(toC, toG);
                                var h1 = Vector3.Transform(Vector3.TransformVector(hl, world[i]), d);
                                d = Twist(ax, h1, hT) * d;
                            }
                        }
                        Matrix4 D = Matrix4.CreateFromQuaternion(d);
                        Matrix4 Rp = Matrix4.CreateFromQuaternion(world[sk.Parent[i]].ExtractRotation());
                        Q[i] = Q[i] * Rp * D * Matrix4.Invert(Rp);
                        if (setLengths)
                            foreach (var q in pairs)
                                if (q.Child >= 0) K[q.Child] = Math.Clamp(q.Tgt.Length / (T[q.Child].Length / mscale * mscale), 0.5f, 2f);
                        if (exact)
                            for (int c = 0; c < n; c++) if (sk.Parent[c] == i) SetExactOffset(c);
                    }
                }
                SolveTopDown(setLengths: true);
                // (the hinge and foot rules inside SolveTopDown need the landmarks and the game's clips; see there)
                // proportions are symmetric even where the pose isn't: average each L/R pair, then re-aim
                for (int i = 0; i < n; i++)
                    if (mirror[i] > i) { float kk = (K[i] + K[mirror[i]]) / 2f; K[i] = K[mirror[i]] = kk; }
                SolveTopDown(setLengths: false, exact: true);
                // exact joints everywhere (the solve pinned them as it went; this also covers any child of a
                // bone that had nothing to aim at): each landmarked joint except the root -- whose translation
                // is the clip's root motion -- takes its measured offset from its parent, in the parent's frame
                foreach (int i in order) SetExactOffset(i);
                PoseDs();
                // the root height the game's clips play at: the game model's own Idle pelvis height scaled by THIS
                // trophy's leg length -- not the sculpt's pelvis height, which on a kneeling or crouched trophy is far
                // lower and would sink every standing clip into the floor (knees on the ground). The bind keeps the
                // sculpt's height; only the clips use this.
                {
                    float Leg(Func<string, Vector3> P, string s) => (P(s + "_hip") - P(s + "_knee")).Length + (P(s + "_knee") - P(s + "_ankle")).Length;
                    Vector3 DsP(string nm) => dsJ[sk.IndexOf(nm)];
                    var sides = new[] { "L", "R" }.Where(s => new[] { "_hip", "_knee", "_ankle" }.All(j => Has(s + j) && sk.IndexOf(s + j) >= 0)).ToList();
                    if (sides.Count > 0)
                    {
                        float ratio = sides.Average(s => Leg(Lr, s) / Math.Max(1e-6f, Leg(DsP, s)));
                        FitFk();
                        int par = sk.Parent[rootIdx];
                        var baseT = par >= 0 ? world[par].ExtractTranslation() * mscale : Vector3.Zero;
                        var offW = par >= 0 ? Vector3.TransformVector(T[rootIdx] / mscale, world[par]) * mscale : T[rootIdx];
                        float targetY = dsFloor + ratio * (dsJ[rootIdx].Y - dsFloor);
                        if (MathF.Abs(offW.Y) > 1e-4f && opt.RootFromLegs)
                        {
                            rootRunIdx = rootIdx; rootRunK = Math.Clamp((targetY - baseT.Y) / offW.Y, 0.5f, 2f);
                            log($"  root height for the clips: legs {ratio:0.00}x the game model's -> root factor {rootRunK:0.00} (the sculpt's pelvis alone gave {K[rootIdx]:0.00})");
                        }
                        PoseDs();
                    }
                }
                foreach (var kv in L)
                    if (kv.Key.EndsWith(".center") && sk.IndexOf(kv.Key[..^7]) is int li && li >= 0)
                        leafEnd[li] = Lr(kv.Key);
                // part markers: how far the solved bind leaves each part's game-idle centre / up from its marks
                {
                    var rep = new List<string>();
                    static float Ang(Vector3 a, Vector3 b) => MathF.Acos(Math.Clamp(Vector3.Dot(a.Normalized(), b.Normalized()), -1f, 1f)) * 180f / MathF.PI;
                    for (int i = 0; i < n; i++)
                    {
                        var Ji = world[i].ExtractTranslation() * mscale;
                        if (Has(sk.Names[i] + ".center") && localCentroid[i] != null)
                            rep.Add($"{sk.Names[i]}.center={Ang(Vector3.TransformVector(localCentroid[i]!.Value, world[i]), Lr(sk.Names[i] + ".center") - Ji):0}deg");
                        if (Has(sk.Names[i] + ".up"))
                            rep.Add($"{sk.Names[i]}.up={Ang(Vector3.TransformVector(upLocal[i], world[i]), Lr(sk.Names[i] + ".up") - Ji):0}deg");
                    }
                    if (rep.Count > 0) log("  part markers (angle left after solve): " + string.Join(" ", rep));
                }
                float err = 0; int ne = 0;
                var misses = new List<string>();
                foreach (var kv in L)
                {
                    int idx = sk.IndexOf(kv.Key);
                    if (idx < 0) continue;
                    float miss = (world[idx].ExtractTranslation() * mscale - Lr(kv.Key)).Length;
                    err = MathF.Max(err, miss); ne++;
                    if (miss > 0.01f * dsHeight) misses.Add($"{kv.Key}={miss / dsHeight * 100:0.0}%");
                }
                var atts = new List<TrophyRig.Attachment>();
                foreach (var (bone, scale) in opt.Landmarks.Replace)
                {
                    int bi = sk.IndexOf(bone);
                    if (bi < 0) { log($"  replace '{bone}' ignored: no such node"); continue; }
                    atts.Add(new TrophyRig.Attachment { Node = bi, Scale = scale, HidesOwnMesh = true });
                }
                foreach (var (bone, cut, anchor, scale) in opt.Landmarks.Attach)
                {
                    int bi = sk.IndexOf(bone);
                    if (bi < 0) { log($"  attach '{bone}' ignored: no such node"); continue; }
                    atts.Add(new TrophyRig.Attachment { Node = bi, Scale = scale, Cut = cut, Anchor = Vector3.TransformPosition(anchor, G()) });
                }
                rig.Attachments = atts.ToArray();
                if (atts.Count > 0) log("  game-model parts: " + string.Join(" ", atts.Select(a =>
                    $"{sk.Names[a.Node]}{(a.HidesOwnMesh ? "(replaces)" : "")}{(a.Cut > 0 ? $" beyond {a.Cut:0.###}" : "")}{(a.Anchor != null ? " anchored" : "")} x{a.Scale:0.##}")));
                log($"  landmark solve: {ne} joints, worst joint miss {err / dsHeight * 100:0.0}% of height, E={Energy() / dsHeight:0.0000}; misses >1%: {string.Join(" ", misses)}");
            }
            else
            {
            // --- 2. global fit per candidate reference pose ---
            var candidates = new List<(int clip, int frame)> { ((int)PlayerAnimation.Idle, 0) };
            {
                // add the best few whole-body matches over all standing clips (coarse pre-score)
                var scored = new List<(float, int, int)>();
                int clips = model.AnimationGroups.Node.Count;
                for (int c = 0; c < clips; c++)
                {
                    if (c is (int)PlayerAnimation.Morph or (int)PlayerAnimation.Unmorph) continue;
                    int fc = model.AnimationGroups.Node[c].FrameCount;
                    for (int f = 0; f < fc; f += 2)
                    {
                        LoadRef(c, f); PoseDs();
                        foreach (float yaw0 in new[] { MathF.PI, 0f })
                        {
                            AlignGlobal(yaw0); PlaceTrophy();
                            scored.Add((Energy(), c, f));
                        }
                    }
                }
                foreach (var s in scored.OrderBy(s => s.Item1).Take(3)) if (!candidates.Contains((s.Item2, s.Item3))) candidates.Add((s.Item2, s.Item3));
            }
            float bestE = float.MaxValue; (int clip, int frame) bestRef = candidates[0];
            float bScale = 1, bYaw = 0; Vector3 bOff = default;
            foreach (var (c, f) in candidates)
                foreach (float yaw0 in new[] { MathF.PI, 0f })
                {
                    LoadRef(c, f); PoseDs();
                    AlignGlobal(yaw0);
                    PlaceTrophy();
                    // "before fitting": the Idle pose, just height/feet/centroid aligned, trophy facing the DS way
                    if (c == (int)PlayerAnimation.Idle && f == 0 && yaw0 == MathF.PI) rig.ChamferStart = Energy() / dsHeight;
                    float e = GlobalRefine(Energy, PlaceTrophy, ref gScale, ref gYaw, ref gOff);
                    log($"  ref {(PlayerAnimation)c} f{f} yaw0={(yaw0 == 0 ? "0" : "180")}: global E={e / dsHeight:0.0000}");
                    if (e < bestE) { bestE = e; bestRef = (c, f); bScale = gScale; bYaw = gYaw; bOff = gOff; }
                }
            LoadRef(bestRef.clip, bestRef.frame); gScale = bScale; gYaw = bYaw; gOff = bOff;
            PoseDs(); PlaceTrophy();
            rig.ChamferGlobal = Energy() / dsHeight;
            rig.RefPose = $"{(PlayerAnimation)bestRef.clip} f{bestRef.frame}";
            log($"  global fit: ref {rig.RefPose}, scale {gScale:0.0000}, yaw {gYaw * 180 / MathF.PI:0}, E={rig.ChamferGlobal:0.0000}");

            // --- 3. articulated fit ---
            // Whole-sphere search only where a trophy can legitimately be far from any game pose (an arm
            // raised overhead, a leg kicked out); heads/elbows/knees only refine locally.
            string[] limbRoots = { "L_shoulder", "R_shoulder", "L_collar", "R_collar", "L_hip", "R_hip" };
            // Bone lengths: left/right share one factor (a trophy is symmetric in proportion even when
            // posed asymmetrically), clamped to +-33%, with a small penalty for straying from the DS
            // proportions -- without these the fit "explains" shape differences with absurd bone lengths.
            // reference local rotations + each bone's axis (toward its child, in its own local frame)
            qRef = (Matrix4[])Q.Clone();
            SetBoneAxes();
            float RotPrior()
            {
                double pen = 0;
                for (int i = 0; i < n; i++)
                {
                    if (!animated[i] || sk.Parent[i] < 0) continue;
                    var (swing, twist) = SwingTwist(Q[i] * Matrix4.Invert(qRef[i]), boneAxis[i]);
                    pen += boneAxis[i] == null ? LeafPrior * (swing + twist) * (swing + twist) : SwingPrior * swing * swing + TwistPrior * twist * twist;
                }
                return (float)pen * opt.RotPriorScale;
            }
            float Obj()
            {
                double pen = 0;
                for (int i = 0; i < n; i++) { float l = MathF.Log(K[i]); pen += l * l; }
                return Energy() + LengthPenalty * (float)pen + RotPrior();
            }
            float cur = Obj();
            for (int sweep = 0; sweep < opt.Sweeps; sweep++)
            {
                float step = 0.35f / (1 << sweep);
                foreach (int i in order)
                {
                    if (!animated[i] || sk.Names[i] is "Dummy_Root") continue;
                    if (sweep == 0 && Array.IndexOf(limbRoots, sk.Names[i]) >= 0)
                    {
                        // randomized search over the whole rotation sphere for limb roots
                        var keep = Q[i];
                        for (int s = 0; s < opt.LimbSearch; s++)
                        {
                            var qr = RandomRotation(rng);
                            Q[i] = qr; PoseDs(); float e = Obj();
                            if (e < cur) { cur = e; keep = qr; }
                        }
                        Q[i] = keep; PoseDs();
                    }
                    for (int it = 0; it < 3; it++)
                    {
                        bool improved = false;
                        for (int axis = 0; axis < 3; axis++)
                            foreach (float sgn in new[] { 1f, -1f })
                            {
                                var keep = Q[i];
                                Matrix4 d = axis == 0 ? Matrix4.CreateRotationX(step * sgn) : axis == 1 ? Matrix4.CreateRotationY(step * sgn) : Matrix4.CreateRotationZ(step * sgn);
                                Q[i] = d * keep; PoseDs(); float e = Obj();
                                if (e < cur - 1e-7f) { cur = e; improved = true; } else Q[i] = keep;
                            }
                        // bone length (not for the root: global scale covers it)
                        if (sk.Parent[i] >= 0 && T[i].LengthSquared > 1e-8f)
                            foreach (float f in new[] { 1f + step * 0.5f, 1f / (1f + step * 0.5f) })
                            {
                                float keep = K[i];
                                K[i] = Math.Clamp(keep * f, MinLength, MaxLength);
                                if (mirror[i] >= 0) K[mirror[i]] = K[i];
                                PoseDs(); float e = Obj();
                                if (e < cur - 1e-7f) { cur = e; improved = true; }
                                else { K[i] = keep; if (mirror[i] >= 0) K[mirror[i]] = keep; }
                            }
                        if (!improved) break;
                    }
                }
                GlobalRefine(Energy, PlaceTrophy, ref gScale, ref gYaw, ref gOff, fine: true);
                PoseDs(); cur = Obj();
                log($"  articulated sweep {sweep + 1}: step {step:0.000} rad, E={Energy() / dsHeight:0.0000}");
            }
            } // end shape-matching mode
            PoseDs();
            rig.ChamferFinal = Energy() / dsHeight;
            rig.TrophyToRig = G();
            rig.Lengths = (float[])K.Clone();
            if (rootRunIdx >= 0) rig.Lengths[rootRunIdx] = rootRunK;   // the clips' root height (the bind below keeps K's)
            rig.Offsets = (Vector3?[])Off.Clone();
            PoseDs();
            rig.BindWorld = (Matrix4[])world.Clone();
            rig.FittedDs = (Vector3[])dsPosed.Clone(); rig.FittedDsNode = dsNode.ToArray();
            rig.Deviations = string.Join(" ", Enumerable.Range(0, n).Where(i => animated[i] && sk.Parent[i] >= 0).Select(i =>
            {
                var (sw, tw) = SwingTwist(Q[i] * Matrix4.Invert(qRef[i]), boneAxis[i]);
                return $"{sk.Names[i]}={sw * 180 / MathF.PI:0}/{tw * 180 / MathF.PI:0}";
            }));
            rig.InvBind = world.Select(w => { var m = w; return Matrix4.Invert(m); }).ToArray();

            // --- 4. skinning weights ---
            // The DS mesh decides WHICH bone a trophy region belongs to (the nearest DS surface's owner);
            // blending happens only between that bone and its skeletal neighbours (parent + children),
            // by distance to their actual bone segments in the fitted pose -- so a hand hanging next to
            // a thigh can't pick up leg weight. Then weights are smoothed in space (a fixed radius, so
            // the blend width doesn't depend on how finely the trophy is tessellated).
            var gAll = rig.TrophyToRig;
            var all = uniq.Select(p => Vector3.TransformPosition(p, gAll)).ToArray();
            // bone segments: node -> each child joint; a leaf that carries DS mesh gets a segment toward
            // (twice) the centroid of its own DS vertices. Only nodes that own DS vertices take weight
            // (the rest never carried mesh in the game).
            var joint = new Vector3[n];
            for (int i = 0; i < n; i++) joint[i] = world[i].ExtractTranslation() * mscale;
            var owns = new bool[n]; var centroid = new Vector3[n]; var cnt = new int[n];
            for (int v = 0; v < dsPosed.Length; v++) { owns[dsNode[v]] = true; centroid[dsNode[v]] += dsPosed[v]; cnt[dsNode[v]]++; }
            var segs = new List<(Vector3 A, Vector3 B)>[n];
            for (int i = 0; i < n; i++)
            {
                segs[i] = new List<(Vector3, Vector3)>();
                if (!owns[i]) continue;
                for (int c = 0; c < n; c++) if (sk.Parent[c] == i) segs[i].Add((joint[i], joint[c]));
                if (segs[i].Count == 0) segs[i].Add((joint[i], joint[i] + 2f * (centroid[i] / cnt[i] - joint[i])));
            }
            static float SegDist(Vector3 p, Vector3 a, Vector3 b)
            {
                var ab = b - a; float l2 = ab.LengthSquared;
                float t = l2 > 1e-12f ? Math.Clamp(Vector3.Dot(p - a, ab) / l2, 0f, 1f) : 0f;
                return (p - (a + ab * t)).Length;
            }
            // Surface neighbourhoods: vertices reachable along the trophy's own mesh edges without leaving
            // a radius -- "near on the surface", so weights never leak across a gap between separate parts
            // (a hand resting by a hip, a blade beside a leg).
            List<int> SurfaceNear(int v, float radius, int cap = 400)
            {
                var res = new List<int> { v }; var seenV = new HashSet<int> { v }; var q = new Queue<int>(); q.Enqueue(v);
                float r2 = radius * radius;
                while (q.Count > 0 && res.Count < cap)
                {
                    int u = q.Dequeue();
                    foreach (int w in adj[u])
                        if (seenV.Add(w) && (all[w] - all[v]).LengthSquared <= r2) { res.Add(w); q.Enqueue(w); }
                }
                return res;
            }
            // skeletal distance (hops) between mesh-carrying bones
            var hops = new int[n, n];
            for (int i = 0; i < n; i++)
                for (int j = 0; j < n; j++)
                {
                    var anc = new Dictionary<int, int>();
                    for (int x = i, h = 0; x >= 0; x = sk.Parent[x], h++) anc[x] = h;
                    int hh = int.MaxValue;
                    for (int x = j, h = 0; x >= 0; x = sk.Parent[x], h++) if (anc.TryGetValue(x, out int hi)) { hh = hi + h; break; }
                    hops[i, j] = hh;
                }

            // mesh islands: connected pieces of the trophy (a gun, a blade, a head modelled separately)
            var island = new int[all.Length];
            Array.Fill(island, -1);
            var islandSize = new List<int>();
            for (int v0 = 0; v0 < all.Length; v0++)
            {
                if (island[v0] >= 0) continue;
                int id = islandSize.Count, size = 0;
                var st = new Stack<int>(); st.Push(v0); island[v0] = id;
                while (st.Count > 0) { int u = st.Pop(); size++; foreach (int w2 in adj[u]) if (island[w2] < 0) { island[w2] = id; st.Push(w2); } }
                islandSize.Add(size);
            }

            // left-out pieces (landmark "hide"): each point takes the piece its nearest trophy vertex is on
            if (landmarkMode && opt.Landmarks!.Hide.Count > 0)
            {
                rig.Hidden = new bool[all.Length];
                foreach (var p in opt.Landmarks.Hide)
                {
                    int nv = 0; float bd = float.MaxValue;
                    for (int v = 0; v < uniq.Count; v++) { float dd = (uniq[v] - p).LengthSquared; if (dd < bd) { bd = dd; nv = v; } }
                    int cntH = 0;
                    for (int v = 0; v < all.Length; v++) if (island[v] == island[nv]) { rig.Hidden[v] = true; cntH++; }
                    log($"  hide {p}: piece of {cntH} vertices (nearest vertex {MathF.Sqrt(bd):0.###} away)");
                }
            }

            // (a) raw owner per vertex.
            //     Landmark mode: the joints are exact, so the SKELETON is the best evidence -- the nearest
            //     bone segment (joint -> child joint, or joint -> part centre for leaves) owns the vertex.
            //     Shape-matching mode: the bone owning most of the nearby fitted DS surface.
            const int Knn = 8;
            var owner0 = new int[all.Length];
            if (landmarkMode)
            {
                var lmSegs = new List<(Vector3 A, Vector3 B, bool Hand)>[n];
                var handDrawn = opt.Landmarks!.Segments.Select(sg => sg.Bone).ToHashSet();
                for (int i = 0; i < n; i++)
                {
                    lmSegs[i] = new List<(Vector3, Vector3, bool)>();
                    if (!owns[i]) continue;
                    for (int c = 0; c < n; c++)
                        if (sk.Parent[c] == i && (joint[c] - joint[i]).LengthSquared > 1e-8f) lmSegs[i].Add((joint[i], joint[c], false));
                    // a leaf points at its marked part centre; failing that, at the game part's direction --
                    // unless the part was drawn by hand (.seg), which beats that guess
                    if (lmSegs[i].Count == 0 && leafEnd[i] is Vector3 e) lmSegs[i].Add((joint[i], joint[i] + 1.5f * (e - joint[i]), false));
                    else if (lmSegs[i].Count == 0 && !handDrawn.Contains(sk.Names[i])) lmSegs[i].Add((joint[i], joint[i] + 2f * (centroid[i] / cnt[i] - joint[i]), false));
                }
                // hand-drawn extra segments (horns, crests...): only for bones that carry mesh in the game
                foreach (var (bone, a, b) in opt.Landmarks!.Segments)
                {
                    int bi = sk.IndexOf(bone);
                    if (bi < 0 || !owns[bi]) { log($"  ownership segment for '{bone}' ignored: {(bi < 0 ? "no such node" : "that bone carries no mesh in the game")}"); continue; }
                    lmSegs[bi].Add((Vector3.TransformPosition(a, gAll), Vector3.TransformPosition(b, gAll), true));
                }
                // A segment claims mesh only on the pieces whose VOLUME it runs through: the gun arm's
                // shoulder segment lies inside the gun piece and must not take the chest plating around its
                // socket, however close; an upper-arm segment inside a sleeve keeps the sleeve even where an
                // armour plate is nearer. Inside = odd ray-crossing parity (majority of 3 rays, so small
                // holes in a piece don't flip it). Pieces no segment claims (a loose spike) stay open to
                // every segment.
                var islandTris = new List<int>[islandSize.Count];
                var islandMin = new Vector3[islandSize.Count]; var islandMax = new Vector3[islandSize.Count];
                for (int k = 0; k < islandSize.Count; k++) { islandTris[k] = new List<int>(); islandMin[k] = new Vector3(float.MaxValue); islandMax[k] = new Vector3(float.MinValue); }
                for (int t = 0; t + 2 < tri.Length; t += 3)
                {
                    int k = island[tri[t]]; islandTris[k].Add(t);
                    for (int c = 0; c < 3; c++) { islandMin[k] = Vector3.ComponentMin(islandMin[k], all[tri[t + c]]); islandMax[k] = Vector3.ComponentMax(islandMax[k], all[tri[t + c]]); }
                }
                var rays = new[] { new Vector3(0.5773f, 0.5774f, 0.5774f), new Vector3(-0.2672f, 0.8018f, -0.5345f), new Vector3(0.6247f, -0.3123f, -0.7157f) };
                bool Inside(int k, Vector3 q)
                {
                    if (q.X < islandMin[k].X || q.Y < islandMin[k].Y || q.Z < islandMin[k].Z || q.X > islandMax[k].X || q.Y > islandMax[k].Y || q.Z > islandMax[k].Z) return false;
                    int odd = 0;
                    foreach (var dir in rays)
                    {
                        int hits = 0;
                        foreach (int t in islandTris[k]) if (RayHitsTri(q, dir, all[tri[t]], all[tri[t + 1]], all[tri[t + 2]])) hits++;
                        if ((hits & 1) == 1) odd++;
                    }
                    return odd >= 2;
                }
                var segIslands = new List<HashSet<int>>[n];
                var claimed = new HashSet<int>();
                for (int i = 0; i < n; i++)
                {
                    segIslands[i] = new List<HashSet<int>>();
                    foreach (var sg in lmSegs[i])
                    {
                        var inside = new Dictionary<int, int>(); var nearest = new Dictionary<int, int>();
                        const int Samples = 9;
                        for (int k = 0; k < Samples; k++)
                        {
                            var q = Vector3.Lerp(sg.A, sg.B, (k + 0.5f) / Samples);
                            for (int isl = 0; isl < islandSize.Count; isl++) if (Inside(isl, q)) inside[isl] = inside.GetValueOrDefault(isl) + 1;
                            int nv = 0; float bd = float.MaxValue;
                            for (int v = 0; v < all.Length; v++) { float dd = (all[v] - q).LengthSquared; if (dd < bd) { bd = dd; nv = v; } }
                            nearest[island[nv]] = nearest.GetValueOrDefault(island[nv]) + 1;
                        }
                        // skeleton segments: the pieces they run inside or mostly next to (a forearm segment
                        // runs through the forearm and on into the blade it holds). Hand-drawn segments mark
                        // ONE decoration: the pieces they run inside plus the single nearest piece -- a fin
                        // drawn beside an arm must not claim the arm.
                        float claim = opt.PieceClaimFraction * Samples;
                        var set = inside.Where(kv => kv.Value >= claim).Select(kv => kv.Key).ToHashSet();
                        if (sg.Hand) set.Add(nearest.OrderByDescending(kv => kv.Value).First().Key);
                        else set.UnionWith(nearest.Where(kv => kv.Value >= claim).Select(kv => kv.Key));
                        if (set.Count == 0) set.Add(nearest.OrderByDescending(kv => kv.Value).First().Key);
                        segIslands[i].Add(set); claimed.UnionWith(set);
                    }
                }
                log("  segment pieces (piece#:vertices): " + string.Join(" ", Enumerable.Range(0, n).Where(i => lmSegs[i].Count > 0).Select(i =>
                    sk.Names[i] + "=" + string.Join("/", segIslands[i].Select(st => st.Count == 0 ? "-" : string.Join("+", st.Select(k => $"{k}:{islandSize[k]}")))))));
                // Line of sight: the nearest bone takes the vertex only if it can SEE it from inside the body -- the
                // straight line from the vertex to the nearest point of the bone crosses no other surface. Where the
                // sculpt rests an arm on the chest, the arm is nearest to the breast but the line has to leave the
                // arm and enter the torso, so the chest (which sees it) takes it; same for a hand resting on a gun.
                // Only bones not much farther than the nearest compete, and a vertex no bone sees (armour layered over
                // a body, where every bone is behind the body surface) keeps the nearest.
                TrophySight? sight = opt.SightOwnership
                    ? new TrophySight(all, tri, rig.Hidden.Length > 0 ? t => rig.Hidden[tri[t]] || rig.Hidden[tri[t + 1]] || rig.Hidden[tri[t + 2]] : null)
                    : null;
                var insideCache = new Dictionary<(int, int, int), bool>();
                bool InsideQ(Vector3 q)
                {
                    float cs = 0.004f * dsHeight;
                    var key = ((int)MathF.Floor(q.X / cs), (int)MathF.Floor(q.Y / cs), (int)MathF.Floor(q.Z / cs));
                    if (!insideCache.TryGetValue(key, out bool r)) insideCache[key] = r = sight!.Inside(q);
                    return r;
                }
                var moved = new Dictionary<(int, int), int>(); int unseen = 0, unjoined = 0;
                var cand = new List<(float D, int Bone, Vector3 Q)>();
                var sightTo = new int[all.Length]; Array.Fill(sightTo, -1);
                for (int v = 0; v < all.Length; v++)
                {
                    float best = float.MaxValue;
                    bool open = !opt.IslandScopedOwnership || !claimed.Contains(island[v]);
                    cand.Clear();
                    for (int i = 0; i < n; i++)
                    {
                        float bi = float.MaxValue; Vector3 bq = default;
                        for (int k = 0; k < lmSegs[i].Count; k++)
                        {
                            if (!open && !segIslands[i][k].Contains(island[v])) continue;
                            var (a, b) = (lmSegs[i][k].A, lmSegs[i][k].B);
                            var ab = b - a; float l2 = ab.LengthSquared;
                            float tt = l2 > 1e-12f ? Math.Clamp(Vector3.Dot(all[v] - a, ab) / l2, 0f, 1f) : 0f;
                            var q = a + ab * tt; float dd = (all[v] - q).Length;
                            if (dd < bi) { bi = dd; bq = q; }
                        }
                        if (bi == float.MaxValue) continue;
                        cand.Add((bi, i, bq));
                        if (bi < best) { best = bi; owner0[v] = i; }
                    }
                    // small separate pieces move whole with one bone (the rigid-piece and material steps below decide
                    // them); splitting one by sight tore Weavel's sword between his arm and a back fin
                    if (sight == null || cand.Count < 2 || islandSize[island[v]] <= RigidIslandFraction * all.Length) continue;
                    cand.Sort((x, y) => x.D.CompareTo(y.D));
                    // only judge when the nearest bone really is inside the body and really is behind another surface;
                    // a bone point outside the body (a limb segment poking out of a thin arm, a shoulder-pad bone on a
                    // model without pads) can't be judged by sight, so the nearest keeps it
                    int nearest = cand[0].Bone, sees = -1;
                    if (!InsideQ(cand[0].Q) || !sight.Blocked(v, cand[0].Q)) continue;
                    for (int ci = 1; ci < cand.Count; ci++)
                    {
                        var (d, bone, q) = cand[ci];
                        if (d > best * opt.SightRatio + opt.SightSlack * dsHeight) break;
                        if (InsideQ(q) && !sight.Blocked(v, q)) { sees = bone; break; }
                    }
                    if (sees < 0) { unseen++; continue; }
                    sightTo[v] = sees;
                }
                // a switch only stands where it JOINS surface the seeing bone already has: grown over the mesh from the
                // bone's own territory through vertices that switch to it (the breast joins the chest round it); a patch
                // in the middle of a forearm that some other bone happens to see is left alone
                if (sight != null)
                {
                    var acc = new bool[all.Length]; var grow = new Queue<int>();
                    for (int v = 0; v < all.Length; v++)
                        if (sightTo[v] >= 0 && adj[v].Any(u => sightTo[u] < 0 && owner0[u] == sightTo[v])) { acc[v] = true; grow.Enqueue(v); }
                    while (grow.Count > 0) { int u = grow.Dequeue(); foreach (int w in adj[u]) if (!acc[w] && sightTo[w] == sightTo[u]) { acc[w] = true; grow.Enqueue(w); } }
                    for (int v = 0; v < all.Length; v++)
                    {
                        if (sightTo[v] < 0) continue;
                        if (!acc[v]) { unjoined++; continue; }
                        moved[(owner0[v], sightTo[v])] = moved.GetValueOrDefault((owner0[v], sightTo[v])) + 1;
                        owner0[v] = sightTo[v];
                    }
                }
                if (sight != null)
                    log($"  line of sight: {moved.Values.Sum()} vertices go to a bone that sees them instead of the nearest ({unseen} seen by none keep the nearest, {unjoined} not joined to the seeing bone's part stay): "
                        + string.Join(" ", moved.OrderByDescending(kv => kv.Value).Take(12).Select(kv => $"{sk.Names[kv.Key.Item1]}->{sk.Names[kv.Key.Item2]}={kv.Value}")));
            }
            else
                for (int v = 0; v < all.Length; v++)
                {
                    var vote = new Dictionary<int, float>();
                    foreach (var (idx, dist) in KNearest(all[v], dsPosed, Knn)) vote[dsNode[idx]] = vote.GetValueOrDefault(dsNode[idx]) + 1f / (dist + 1e-3f);
                    owner0[v] = vote.OrderByDescending(kv => kv.Value).First().Key;
                }
            // the user's grouping overrides the automatic owner vertex by vertex
            bool chunkMode = opt.Chunks != null && opt.Chunks.Owner.Length == all.Length;
            if (opt.Chunks != null && !chunkMode) log($"  chunk grouping IGNORED: it covers {opt.Chunks.Owner.Length} vertices, the trophy has {all.Length}");
            var userOwned = new bool[all.Length];
            var islandHasUser = new bool[islandSize.Count];
            if (chunkMode)
            {
                int nUser = 0;
                for (int v = 0; v < all.Length; v++)
                    if (opt.Chunks!.Owner[v] >= 0 && opt.Chunks.Owner[v] < n) { owner0[v] = opt.Chunks.Owner[v]; userOwned[v] = true; islandHasUser[island[v]] = true; nUser++; }
                log($"  chunk grouping: {nUser} of {all.Length} vertices assigned by hand, {opt.Chunks!.Seams.Count} seam settings");
            }
            // Rigid islands: a small separate piece of mesh (a blade, a hair tentacle, a gun) moves as ONE
            // part -- all of it goes to its majority owner and it is never blended or split. (Pieces the
            // user grouped by hand are left as grouped.)
            var rigid = new bool[all.Length];
            {
                var votes = new Dictionary<int, Dictionary<int, int>>();
                for (int v = 0; v < all.Length; v++)
                {
                    int id = island[v];
                    if (islandSize[id] > RigidIslandFraction * all.Length || islandHasUser[id]) continue;
                    if (!votes.TryGetValue(id, out var vv)) votes[id] = vv = new Dictionary<int, int>();
                    vv[owner0[v]] = vv.GetValueOrDefault(owner0[v]) + 1;
                }
                // rigid only when one bone clearly dominates the piece; a piece spanning a joint (a shin
                // modelled together with its boot) must still bend there
                var islandOwner = votes.Where(kv => kv.Value.Values.Max() >= RigidDominance * kv.Value.Values.Sum())
                                       .ToDictionary(kv => kv.Key, kv => kv.Value.OrderByDescending(x => x.Value).First().Key);
                for (int v = 0; v < all.Length; v++)
                    if (islandOwner.TryGetValue(island[v], out int io)) { owner0[v] = io; rigid[v] = true; }
                RigidIslands = islandOwner.Count;
            }
            // Material kinship: a small separate piece made of a material that otherwise belongs (almost) wholly to ONE
            // bone, and resting on that bone's part, is part of it -- Dark Suit's cannon rails are separate pieces lying on
            // the cannon right under her left hand, so the hand's bone was nearest, but they're the cannon's material and
            // they sit on the cannon. Body materials run over many bones, so they never decide anything here.
            if (opt.CornerMaterial != null && opt.CornerMaterial.Length == tri.Length)
            {
                var vmat = new int[all.Length]; Array.Fill(vmat, -1);
                for (int i = 0; i < tri.Length; i++) vmat[tri[i]] = opt.CornerMaterial[i];
                var matOwn = new Dictionary<int, Dictionary<int, int>>();
                for (int v = 0; v < all.Length; v++)
                {
                    if (vmat[v] < 0) continue;
                    if (!matOwn.TryGetValue(vmat[v], out var mo)) matOwn[vmat[v]] = mo = new Dictionary<int, int>();
                    mo[owner0[v]] = mo.GetValueOrDefault(owner0[v]) + 1;
                }
                var pieceVerts = new Dictionary<int, List<int>>();
                for (int v = 0; v < all.Length; v++)
                    if (islandSize[island[v]] <= RigidIslandFraction * all.Length && !islandHasUser[island[v]])
                        (pieceVerts.TryGetValue(island[v], out var pl) ? pl : pieceVerts[island[v]] = new List<int>()).Add(v);
                var grid = new Grid(0.01f * dsHeight); grid.Build(all);
                float touch = 0.02f * dsHeight;
                var kin = new List<string>();
                foreach (var (id, pv) in pieceVerts)
                {
                    var mats = pv.Where(v => vmat[v] >= 0).GroupBy(v => vmat[v]).OrderByDescending(g => g.Count()).FirstOrDefault();
                    if (mats == null || mats.Count() < 0.8f * pv.Count) continue;
                    int mat = mats.Key;
                    // the material's owners elsewhere -- leaving out every small piece of it that goes with this piece's bone
                    // (Dark Suit's three rails all lie under the hand; they must not vouch for each other)
                    var cur = pv.GroupBy(v => owner0[v]).OrderByDescending(g => g.Count()).First().Key;
                    var outside = new Dictionary<int, int>(matOwn[mat]);
                    var siblings = new HashSet<int>();   // this piece and its like: small pieces of this material going with cur
                    foreach (var (id2, pv2) in pieceVerts)
                        if (pv2.GroupBy(v => owner0[v]).OrderByDescending(g => g.Count()).First().Key == cur
                            && pv2.Count(v => vmat[v] == mat) >= 0.8f * pv2.Count)
                            foreach (int v in pv2) { siblings.Add(v); if (vmat[v] == mat) outside[owner0[v]]--; }
                    int tot = outside.Values.Sum();
                    if (Environment.GetEnvironmentVariable("MPH_KIN_DEBUG") == "1")
                        log($"    kin? piece {id} {pv.Count}v mat {mat} owner {sk.Names[cur]} elsewhere " + string.Join(" ", outside.Where(kv => kv.Value > 0).Select(kv => $"{sk.Names[kv.Key]}={kv.Value}")));
                    if (tot < 20) continue;
                    var top = outside.OrderByDescending(kv => kv.Value).First();
                    if (top.Value < 0.9f * tot) continue;
                    if (cur == top.Key) continue;
                    // it must rest on that bone's part, and nearer to it than to the part it would otherwise go with
                    float dTop = float.MaxValue, dCur = float.MaxValue;
                    foreach (int v in pv)
                        foreach (int u in grid.Within(all[v], all, touch))
                        {
                            if (siblings.Contains(u)) continue;
                            float dd = (all[u] - all[v]).Length;
                            if (owner0[u] == top.Key && dd < dTop) dTop = dd;
                            if (owner0[u] == cur && dd < dCur) dCur = dd;
                        }
                    if (dTop > touch || dTop >= dCur) continue;
                    foreach (int v in pv) { owner0[v] = top.Key; rigid[v] = true; }
                    kin.Add($"{pv.Count}v {sk.Names[cur]}->{sk.Names[top.Key]}");
                }
                if (kin.Count > 0) log($"  material kinship: {kin.Count} pieces go with the bone their material belongs to: " + string.Join(", ", kin));

                // Material patches: a connected stretch of ONE material that one bone mostly owns is one part; vertices on
                // it taken by a bone far away in the skeleton (a hand, from the other arm's cannon) go back to that bone.
                // Dark Suit's cannon rails are welded to the cannon in its material, but the left hand's bone runs into
                // the cannon there and took them. A material spread over the body (the suit) has no main bone: untouched.
                var patchOf = new Dictionary<(int, int), int>();   // (material, vertex) -> union-find node
                var up = new List<int>();
                int Node((int, int) k) { if (!patchOf.TryGetValue(k, out int id)) { patchOf[k] = id = up.Count; up.Add(id); } return id; }
                int Root(int x) { while (up[x] != x) { up[x] = up[up[x]]; x = up[x]; } return x; }
                for (int t = 0; t + 2 < tri.Length; t += 3)
                {
                    int m = opt.CornerMaterial[t];
                    if (opt.CornerMaterial[t + 1] != m || opt.CornerMaterial[t + 2] != m) continue;
                    int a = Root(Node((m, tri[t]))), b = Root(Node((m, tri[t + 1]))), c = Root(Node((m, tri[t + 2])));
                    up[b] = a; up[Root(c)] = a;
                }
                var patches = new Dictionary<int, List<int>>();
                foreach (var ((m, v), id) in patchOf) { int r = Root(id); (patches.TryGetValue(r, out var l) ? l : patches[r] = new List<int>()).Add(v); }
                var back = new Dictionary<string, int>();
                bool IsHand(int b) => sk.Names[b].Contains("wrist") || (sk.Names[b].Contains("elbow") && !Enumerable.Range(0, n).Any(c => sk.Parent[c] == b && owns[c]));
                foreach (var pv in patches.Values)
                {
                    if (pv.Count < 20) continue;
                    var byOwner = pv.Where(v => !userOwned[v]).GroupBy(v => owner0[v]).ToDictionary(g => g.Key, g => g.ToList());
                    if (byOwner.Count < 2) continue;
                    var main = byOwner.OrderByDescending(kv => kv.Value.Count).First();
                    if (main.Value.Count < 0.6f * pv.Count) continue;
                    foreach (var (bone, vs) in byOwner)
                    {
                        // hands only (a hand grips what it holds; hips and chest share their suit's panels legitimately)
                        if (bone == main.Key || hops[bone, main.Key] < Math.Max(4, opt.CutHandWeldHops) || !(IsHand(bone) || IsHand(main.Key))) continue;
                        foreach (int v in vs) owner0[v] = main.Key;
                        string key = $"{sk.Names[bone]}->{sk.Names[main.Key]}";
                        back[key] = back.GetValueOrDefault(key) + vs.Count;
                    }
                }
                if (back.Count > 0) log("  material patches: vertices a far bone took back to the patch's own bone: " + string.Join(" ", back.Select(kv => $"{kv.Key}={kv.Value}")));
            }
            // (b) denoise: majority owner over the surface neighbourhood
            var owner = new int[all.Length];
            for (int v = 0; v < all.Length; v++)
            {
                var vote = new Dictionary<int, int>();
                if (rigid[v] || userOwned[v]) { owner[v] = owner0[v]; continue; }
                foreach (int u in SurfaceNear(v, OwnerRadius)) vote[owner0[u]] = vote.GetValueOrDefault(owner0[u]) + 1;
                owner[v] = vote.OrderByDescending(kv => kv.Value).First().Key;
            }
            var panelW = new Dictionary<int, float>?[all.Length];
            // (b3) hand welds: a triangle whose corners belong to a hand (a wrist, or an arm ending at the elbow in a weapon)
            // and to a bone at least CutHandWeldHops away is a bridge the sculpt fused, not surface
            if (opt.CutHandWeldHops > 0 || opt.CutFarWeldHops > 0)
            {
                bool Hand(int b) => sk.Names[b].Contains("wrist") || (sk.Names[b].Contains("elbow") && !Enumerable.Range(0, n).Any(c => sk.Parent[c] == b && owns[c]));
                var pairs = new Dictionary<string, int>();
                for (int t = 0; t + 2 < tri.Length; t += 3)
                {
                    int[] bo = { owner[tri[t]], owner[tri[t + 1]], owner[tri[t + 2]] };
                    for (int x = 0; x < 3; x++)
                        for (int y = x + 1; y < 3; y++)
                            if ((opt.CutHandWeldHops > 0 && (Hand(bo[x]) || Hand(bo[y])) && hops[bo[x], bo[y]] >= opt.CutHandWeldHops)
                                || (opt.CutFarWeldHops > 0 && hops[bo[x], bo[y]] >= opt.CutFarWeldHops))
                            {
                                if (rig.CutTris.Add(TrophyRig.TriKey(tri[t], tri[t + 1], tri[t + 2])))
                                {
                                    string key = string.Join("|", new[] { sk.Names[bo[x]], sk.Names[bo[y]] }.OrderBy(q => q));
                                    pairs[key] = pairs.GetValueOrDefault(key) + 1;
                                }
                                x = 3; break;
                            }
                }
                // blending and smoothing work over surface neighbourhoods: they must not reach across a cut bridge
                if (rig.CutTris.Count > 0)
                {
                    foreach (var a in adj) a.Clear();
                    for (int t = 0; t + 2 < tri.Length; t += 3)
                    {
                        if (rig.CutTris.Contains(TrophyRig.TriKey(tri[t], tri[t + 1], tri[t + 2]))) continue;
                        int a0 = tri[t], b0 = tri[t + 1], c0 = tri[t + 2];
                        adj[a0].Add(b0); adj[a0].Add(c0); adj[b0].Add(a0); adj[b0].Add(c0); adj[c0].Add(a0); adj[c0].Add(b0);
                    }
                }
                if (rig.CutTris.Count > 0) log($"  hand welds cut: {rig.CutTris.Count} triangles: " + string.Join(" ", pairs.Select(kv => $"{kv.Key}={kv.Value}")));
            }
            // (c) JOINT-CENTRED blending: a vertex belongs wholly to its owning bone except inside a smooth
            //     band around a joint plane, where it blends 50/50 at the plane into the neighbouring bone.
            //     Long rigid parts (a blade, a cannon, a helmet) stay rigid; elbows/knees/hips bend smoothly.
            //     partner(b) = nearest mesh-carrying ancestor, or -- when there is none -- a mesh-carrying
            //     sibling sharing b's joint (Pelvis/Spine_1 both hang off the root at one point).
            var axisDir = new Vector3[n]; var segLen = new float[n];
            for (int i = 0; i < n; i++)
            {
                var sum = Vector3.Zero; float len = 0; int k = 0;
                for (int c = 0; c < n; c++)
                    if (sk.Parent[c] == i && (joint[c] - joint[i]).LengthSquared > 1e-10f) { sum += (joint[c] - joint[i]).Normalized(); len += (joint[c] - joint[i]).Length; k++; }
                if (k == 0 && owns[i]) { var dc = centroid[i] / cnt[i] - joint[i]; if (dc.LengthSquared > 1e-10f) { sum = dc.Normalized(); len = 2f * dc.Length; k = 1; } }
                axisDir[i] = k > 0 && sum.LengthSquared > 1e-10f ? sum.Normalized() : Vector3.UnitY;
                segLen[i] = k > 0 ? len / k : 0.1f * dsHeight;
            }
            var partner = new int[n];
            for (int i = 0; i < n; i++)
            {
                partner[i] = -1;
                if (!owns[i]) continue;
                for (int x = sk.Parent[i]; x >= 0; x = sk.Parent[x]) if (owns[x]) { partner[i] = x; break; }
                if (partner[i] < 0)
                {
                    float best = -2;
                    for (int j = 0; j < n; j++)
                        if (j != i && owns[j] && (joint[j] - joint[i]).LengthSquared < 1e-8f)
                        {
                            float opp = -Vector3.Dot(axisDir[i], axisDir[j]);
                            if (opp > best) { best = opp; partner[i] = j; }
                        }
                }
            }
            var kids = new List<int>[n];
            for (int i = 0; i < n; i++) kids[i] = new List<int>();
            for (int c = 0; c < n; c++)
                if (owns[c] && partner[c] >= 0) kids[partner[c]].Add(c);
            // The blend plane at a joint BISECTS the bend: its normal is the mean of the incoming bone's
            // direction and the outgoing one. A plane square to the outgoing bone alone is only right for a
            // straight limb -- on a folded knee (a sculpt's crouch) the shin points back along the thigh
            // and that plane would hand the thigh's lower half to the shin.
            var planeN = new Vector3[n];
            for (int i = 0; i < n; i++)
            {
                planeN[i] = axisDir[i];
                int pp = partner[i];
                if (pp < 0 || (joint[i] - joint[pp]).LengthSquared < 1e-8f) continue;
                var bis = (joint[i] - joint[pp]).Normalized() + axisDir[i];
                if (bis.Length > 0.2f) planeN[i] = bis.Normalized();
            }
            float Band(int i) => Math.Max(1e-4f, Math.Min(BlendBand * opt.BlendScale * dsHeight, 0.25f * segLen[i]));
            float Reach(int i) => Math.Max(3f * Band(i), 0.45f * segLen[i]);
            static float Smooth01(float x) { x = Math.Clamp((x + 1f) * 0.5f, 0f, 1f); return x * x * (3f - 2f * x); }
            var wts = new Dictionary<int, float>[all.Length];
            if (chunkMode)
            {
                // CHUNK SEAMS (the user's grouping): every chunk moves rigidly with its bone, as the game's
                // own models do. Across a seam between skeleton neighbours set to blend, a vertex within the
                // band takes up to half its weight from the chunk on the other side (0.5 exactly at the seam,
                // fading to 0 at the band's edge), measured along the surface -- so a continuous skin bends
                // instead of cracking, and nothing blends across a seam the user made rigid. Parts marked
                // STIFF never blend: next to one, the soft side takes the whole bend (up to 1.0 at the seam).
                var stiff = opt.Chunks!.Stiff.Length == all.Length ? opt.Chunks.Stiff : new bool[all.Length];
                bool Neighbours(int a, int b) => sk.Parent[a] == b || sk.Parent[b] == a || partner[a] == b || partner[b] == a;
                (bool Rigid, float Width) Seam(int a, int b)
                {
                    int child = sk.Parent[a] == b || partner[a] == b ? a : b;
                    return opt.Chunks!.Seams.TryGetValue(sk.Names[child], out var s) ? s : (false, TrophyChunks.DefaultSeamWidth);
                }
                float maxW = opt.Chunks!.Seams.Values.Where(s => !s.Rigid).Select(s => s.Width).DefaultIfEmpty(0f).Max();
                maxW = MathF.Max(maxW, TrophyChunks.DefaultSeamWidth) * dsHeight;
                for (int v = 0; v < all.Length; v++)
                {
                    int a = owner[v];
                    var d = new Dictionary<int, float> { [a] = 1f };
                    if (!rigid[v] && !stiff[v])
                    {
                        var nearest = new Dictionary<int, (float Dist, bool Stiff)>();
                        foreach (int u in SurfaceNear(v, maxW, 3000))
                        {
                            int b = owner[u];
                            if (b == a || !Neighbours(a, b)) continue;
                            float dist = (all[u] - all[v]).Length;
                            if (!nearest.TryGetValue(b, out var cur) || dist < cur.Dist) nearest[b] = (dist, stiff[u]);
                        }
                        foreach (var (b, (dist, otherStiff)) in nearest)
                        {
                            var (rigidSeam, width) = Seam(a, b);
                            float w = width * dsHeight;
                            if (rigidSeam || w <= 0 || dist >= w) continue;
                            float x = dist / w, s = 1f - x * x * (3f - 2f * x);
                            float wb = (otherStiff ? 1f : 0.5f) * s;
                            d[b] = d.GetValueOrDefault(b) + wb; d[a] -= wb;
                        }
                        if (d[a] < 0) d[a] = 0;
                    }
                    wts[v] = Normalize(d, 4);
                }
            }
            else
            for (int v = 0; v < all.Length; v++)
            {
                int bOwn = owner[v];
                var d = new Dictionary<int, float>();
                if (rigid[v]) { d[bOwn] = 1f; wts[v] = d; continue; }
                int pp = partner[bOwn];
                float alpha = 0f;
                // only near the joint itself: the plane test alone reaches arbitrarily far sideways
                if (pp >= 0 && (all[v] - joint[bOwn]).Length < Reach(bOwn))
                    alpha = 1f - Smooth01(Vector3.Dot(all[v] - joint[bOwn], planeN[bOwn]) / Band(bOwn));
                float keep = 1f - alpha;
                d[bOwn] = keep;
                if (alpha > 0) d[pp] = alpha;
                foreach (int c in kids[bOwn])
                {
                    if (c == pp || (all[v] - joint[c]).Length >= Reach(c)) continue;
                    float beta = Smooth01(Vector3.Dot(all[v] - joint[c], planeN[c]) / Band(c));
                    if (beta <= 0) continue;
                    d[c] = d.GetValueOrDefault(c) + keep * beta;
                    d[bOwn] -= keep * beta * 0.999f;
                }
                if (d[bOwn] < 0) d[bOwn] = 0;
                wts[v] = Normalize(d, 4);
            }
            // (c2) clean boundaries: panels between the surface's own creases; a panel whose blended weight one bone
            //      mostly carries moves wholly with that bone
            if (opt.PanelDominance > 0f && !chunkMode)
            {
                int nt = tri.Length / 3;
                var fn = new Vector3[nt]; var area = new float[nt];
                for (int t = 0; t < nt; t++)
                {
                    var c = Vector3.Cross(all[tri[3 * t + 1]] - all[tri[3 * t]], all[tri[3 * t + 2]] - all[tri[3 * t]]);
                    area[t] = 0.5f * c.Length; fn[t] = c.LengthSquared > 1e-20f ? c.Normalized() : Vector3.Zero;
                }
                var par = new int[nt]; for (int t = 0; t < nt; t++) par[t] = t;
                int Find(int x) { while (par[x] != x) { par[x] = par[par[x]]; x = par[x]; } return x; }
                var edgeTris = new Dictionary<(int, int), List<int>>();
                for (int t = 0; t < nt; t++)
                    for (int c = 0; c < 3; c++)
                    {
                        if (rig.CutTris.Count > 0 && rig.CutTris.Contains(TrophyRig.TriKey(tri[3 * t], tri[3 * t + 1], tri[3 * t + 2]))) break;
                        int a = tri[3 * t + c], b = tri[3 * t + (c + 1) % 3];
                        if (a == b) continue;
                        var key = a < b ? (a, b) : (b, a);
                        if (!edgeTris.TryGetValue(key, out var l)) edgeTris[key] = l = new List<int>();
                        l.Add(t);
                    }
                float cosCrease = MathF.Cos(opt.PanelCreaseDeg * MathF.PI / 180f);
                foreach (var l in edgeTris.Values)
                    for (int x = 0; x < l.Count; x++)
                        for (int y = x + 1; y < l.Count; y++)
                            if (fn[l[x]] != Vector3.Zero && fn[l[y]] != Vector3.Zero && Vector3.Dot(fn[l[x]], fn[l[y]]) >= cosCrease)
                                par[Find(l[x])] = Find(l[y]);
                int Main(int v) => wts[v].OrderByDescending(kv => kv.Value).First().Key;
                // a panel reaching past a joint (a lower shin running on into the foot, a knee cap onto the thigh) must
                // not move whole: some of it is clearly on the far side of the joint plane, beyond the blend band
                var panelVerts = new Dictionary<int, HashSet<int>>();
                for (int t = 0; t < nt; t++) { int p = Find(t); if (!panelVerts.TryGetValue(p, out var hs)) panelVerts[p] = hs = new HashSet<int>(); for (int c = 0; c < 3; c++) hs.Add(tri[3 * t + c]); }
                bool Spans(int p, int top)
                {
                    foreach (int v in panelVerts[p])
                    {
                        int b = Main(v);
                        if (b == top) continue;
                        if (partner[b] == top) { if (Vector3.Dot(all[v] - joint[b], planeN[b]) > Band(b)) return true; }
                        else if (partner[top] == b) { if (Vector3.Dot(all[v] - joint[top], planeN[top]) < -Band(top)) return true; }
                        else if (partner[b] != partner[top] || partner[b] < 0) return true;   // bridges bones that are not neighbours
                    }
                    return false;
                }
                // each panel's owner by area-weighted vote of its corners' owners
                var votes = new Dictionary<int, Dictionary<int, float>>();
                for (int t = 0; t < nt; t++)
                {
                    int p = Find(t);
                    if (!votes.TryGetValue(p, out var vv)) votes[p] = vv = new Dictionary<int, float>();
                    for (int c = 0; c < 3; c++) foreach (var kv in wts[tri[3 * t + c]]) vv[kv.Key] = vv.GetValueOrDefault(kv.Key) + area[t] * kv.Value;
                }
                var panelOwner = new Dictionary<int, int>();
                foreach (var (p, vv) in votes)
                {
                    float sum = vv.Values.Sum(); var top = vv.OrderByDescending(kv => kv.Value).First();
                    if (sum > 0 && top.Value >= opt.PanelDominance * sum && !Spans(p, top.Key)) panelOwner[p] = top.Key;
                }
                // per vertex: the owners of the dominated panels it lies on (and whether it also lies on an open one)
                var vOwners = new HashSet<int>?[all.Length]; var vOpen = new bool[all.Length];
                for (int t = 0; t < nt; t++)
                {
                    bool dominated = panelOwner.TryGetValue(Find(t), out int po);
                    for (int c = 0; c < 3; c++)
                    {
                        int v = tri[3 * t + c];
                        if (!dominated) { vOpen[v] = true; continue; }
                        (vOwners[v] ??= new HashSet<int>()).Add(po);
                    }
                }
                int whole = 0, seam = 0;
                for (int v = 0; v < all.Length; v++)
                {
                    if (vOwners[v] == null || vOpen[v] || rigid[v] || userOwned[v]) continue;
                    // a crease shared by panels of different bones is split evenly between those that are skeletal
                    // neighbours of the vertex's own owner (never across the body)
                    var os = vOwners[v]!.Where(b => b == Main(v) || hops[b, Main(v)] <= 2).ToList();
                    if (os.Count == 0) os.Add(vOwners[v]!.First());
                    panelW[v] = os.ToDictionary(b => b, _ => 1f / os.Count);
                    if (os.Count == 1) whole++; else seam++;
                }
                for (int v = 0; v < all.Length; v++) if (panelW[v] != null) wts[v] = panelW[v]!;
                log($"  clean boundaries: {votes.Count} panels (crease {opt.PanelCreaseDeg:0} deg), {panelOwner.Count} dominated (>= {opt.PanelDominance:0.##}); " +
                    $"{whole} vertices whole on one bone, {seam} on seams between bones");
            }
            // (d) light smoothing over surface neighbourhoods (cleans ownership speckle at part borders) --
            //     not for a hand grouping, whose chunk borders are deliberate
            for (int it = 0; it < (chunkMode ? 0 : opt.SmoothIterations); it++)
            {
                var next = new Dictionary<int, float>[all.Length];
                for (int v = 0; v < all.Length; v++)
                {
                    if (rigid[v] || panelW[v] != null) { next[v] = wts[v]; continue; }
                    var acc = new Dictionary<int, float>();
                    foreach (int u in SurfaceNear(v, SmoothRadius))
                        foreach (var kv in wts[u]) acc[kv.Key] = acc.GetValueOrDefault(kv.Key) + kv.Value;
                    next[v] = Normalize(acc, 4);
                }
                wts = next;
            }
            rig.Bones = new int[all.Length * 4]; rig.Weights = new float[all.Length * 4];
            for (int v = 0; v < all.Length; v++)
            {
                int k = 0;
                foreach (var kv in wts[v].OrderByDescending(kv => kv.Value)) { rig.Bones[v * 4 + k] = kv.Key; rig.Weights[v * 4 + k] = kv.Value; k++; }
                for (; k < 4; k++) { rig.Bones[v * 4 + k] = rig.Bones[v * 4]; rig.Weights[v * 4 + k] = 0; }
            }
            if (landmarkMode && opt.AlignFacing) TrophyFacing.Align(model, rig, log);
            return rig;
        }

        // The game model's own geometry for the rig's attached parts, placed in the trophy's BIND (rig) space so
        // it skins like trophy mesh: one bone per corner (weight 1). Only triangles lying wholly on the bone are
        // taken, and with a Cut only those wholly beyond it along the part's long axis (principal axis of its
        // bone-local vertices, pointing away from the joint). With an Anchor the kept piece is turned so that
        // axis follows the trophy limb (joint -> anchor) and its cut face is seated on the anchor; otherwise it
        // sits where the game model has it. Source batch = material/texture.
        public sealed class ReplacementCorner { public Vector3 Pos, Normal; public float U, V, R, G, B; public int Node; }
        public static List<(GeometryBaker.Batch Source, List<ReplacementCorner> Corners)> ReplacementParts(Model model, TrophyRig rig)
        {
            var res = new List<(GeometryBaker.Batch, List<ReplacementCorner>)>();
            if (rig.Attachments.Length == 0) return res;
            var poser = new BipedAnimator(model);
            poser.SetBoth(PlayerAnimation.Idle, AnimFlags.None);
            HunterRig.Pose(poser, 0f, new float[16 * 32]);
            var baked = GeometryBaker.Bake(model, new float[3], out _, useAnimation: true, useMatrixStack: true, skinned: true);
            const int S = GeometryBaker.StrideSkinned;
            Vector3 Local(GeometryBaker.Batch b, int i) => new(b.Verts[i], b.Verts[i + 1], b.Verts[i + 2]);
            bool Whole(GeometryBaker.Batch b, int t, int node)
            {
                for (int c = 0; c < 3; c++) if (model.NodeMatrixIds[(int)b.Verts[t + c * S + 11]] != node) return false;
                return true;
            }
            var perBatch = new Dictionary<GeometryBaker.Batch, List<ReplacementCorner>>();
            foreach (var at in rig.Attachments)
            {
                // the part's long axis and the centre of its cut (bone-local; the joint is the origin)
                var verts = new List<Vector3>();
                foreach (var b in baked)
                    for (int t = 0; t + 3 * S <= b.Verts.Count; t += 3 * S)
                        if (Whole(b, t, at.Node)) for (int c = 0; c < 3; c++) verts.Add(Local(b, t + c * S));
                if (verts.Count == 0) continue;
                var cen = verts.Aggregate(Vector3.Zero, (a, v) => a + v) / verts.Count;
                var axis = PrincipalAxis(verts, cen);
                if (Vector3.Dot(axis, cen) < 0) axis = -axis;
                var cutCentre = cen + axis * (at.Cut - Vector3.Dot(cen, axis));
                Matrix4 place;
                if (at.Anchor is Vector3 anchor)
                {
                    var a = Vector3.TransformPosition(anchor, rig.InvBind[at.Node]);
                    var turn = a.LengthSquared > 1e-12f ? Matrix4.CreateFromQuaternion(FromTo(axis, a.Normalized())) : Matrix4.Identity;
                    place = Matrix4.CreateTranslation(-cutCentre) * turn * Matrix4.CreateScale(at.Scale) * Matrix4.CreateTranslation(a);
                }
                else place = Matrix4.CreateScale(at.Scale);
                var m = place * rig.BindWorld[at.Node];
                foreach (var b in baked)
                    for (int t = 0; t + 3 * S <= b.Verts.Count; t += 3 * S)
                    {
                        if (!Whole(b, t, at.Node)) continue;
                        if (at.Cut > 0 && Enumerable.Range(0, 3).Any(c => Vector3.Dot(Local(b, t + c * S), axis) < at.Cut)) continue;
                        if (!perBatch.TryGetValue(b, out var corners)) perBatch[b] = corners = new List<ReplacementCorner>();
                        for (int c = 0; c < 3; c++)
                        {
                            int i = t + c * S;
                            var n = Vector3.TransformNormal(new Vector3(b.Verts[i + 3], b.Verts[i + 4], b.Verts[i + 5]), m);
                            corners.Add(new ReplacementCorner
                            {
                                Pos = Vector3.TransformPosition(Local(b, i), m),
                                Normal = n.LengthSquared > 1e-12f ? n.Normalized() : n,
                                U = b.Verts[i + 6], V = b.Verts[i + 7], R = b.Verts[i + 8], G = b.Verts[i + 9], B = b.Verts[i + 10], Node = at.Node,
                            });
                        }
                    }
            }
            foreach (var b in baked) if (perBatch.TryGetValue(b, out var cs)) res.Add((b, cs));
            return res;
        }

        // dominant eigenvector of the points' covariance (power iteration)
        static Vector3 PrincipalAxis(List<Vector3> pts, Vector3 cen)
        {
            double xx = 0, xy = 0, xz = 0, yy = 0, yz = 0, zz = 0;
            foreach (var p in pts) { var d = p - cen; xx += d.X * d.X; xy += d.X * d.Y; xz += d.X * d.Z; yy += d.Y * d.Y; yz += d.Y * d.Z; zz += d.Z * d.Z; }
            var v = cen.LengthSquared > 1e-12f ? cen.Normalized() : Vector3.UnitX;
            for (int it = 0; it < 100; it++)
            {
                var w = new Vector3((float)(xx * v.X + xy * v.Y + xz * v.Z), (float)(xy * v.X + yy * v.Y + yz * v.Z), (float)(xz * v.X + yz * v.Y + zz * v.Z));
                if (w.LengthSquared < 1e-20f) break;
                v = w.Normalized();
            }
            return v;
        }

        // Moller-Trumbore: does the ray q + t*dir (t > 0) cross triangle abc?
        static bool RayHitsTri(Vector3 q, Vector3 dir, Vector3 a, Vector3 b, Vector3 c)
        {
            var e1 = b - a; var e2 = c - a;
            var pv = Vector3.Cross(dir, e2);
            float det = Vector3.Dot(e1, pv);
            if (MathF.Abs(det) < 1e-12f) return false;
            float inv = 1f / det;
            var tv = q - a;
            float u = Vector3.Dot(tv, pv) * inv;
            if (u < 0f || u > 1f) return false;
            var qv = Vector3.Cross(tv, e1);
            float w = Vector3.Dot(dir, qv) * inv;
            if (w < 0f || u + w > 1f) return false;
            return Vector3.Dot(e2, qv) * inv > 1e-7f;
        }

        // Shortest rotation taking direction a onto direction b (OpenTK convention: Vector3.Transform(a, q)).
        public static Quaternion FromTo(Vector3 a, Vector3 b)
        {
            a.Normalize(); b.Normalize();
            float d = Vector3.Dot(a, b);
            if (d < -0.999999f)
            {
                var axis = Vector3.Cross(Vector3.UnitX, a);
                if (axis.LengthSquared < 1e-6f) axis = Vector3.Cross(Vector3.UnitY, a);
                return Quaternion.FromAxisAngle(axis.Normalized(), MathF.PI);
            }
            var c = Vector3.Cross(a, b);
            return new Quaternion(c, 1f + d).Normalized();
        }

        // Rotation about `axis` taking vector a's component across the axis onto b's (identity when either
        // is parallel to the axis). Applied after a swing, it sets the remaining spin.
        public static Quaternion Twist(Vector3 axis, Vector3 a, Vector3 b)
        {
            axis.Normalize();
            var pa = a - axis * Vector3.Dot(axis, a); var pb = b - axis * Vector3.Dot(axis, b);
            if (pa.LengthSquared < 1e-10f || pb.LengthSquared < 1e-10f) return Quaternion.Identity;
            float ang = MathF.Atan2(Vector3.Dot(axis, Vector3.Cross(pa, pb)), Vector3.Dot(pa, pb));
            return Quaternion.FromAxisAngle(axis, ang);
        }

        // Horn's closed-form best rotation taking unit vectors p_i onto q_i (least squares): the
        // eigenvector of the largest eigenvalue of his 4x4 matrix N (Jacobi eigen-decomposition).
        public static Quaternion Horn(List<(Vector3 P, Vector3 Q)> pairs)
        {
            double sxx = 0, sxy = 0, sxz = 0, syx = 0, syy = 0, syz = 0, szx = 0, szy = 0, szz = 0;
            foreach (var (p, q) in pairs)
            {
                sxx += p.X * q.X; sxy += p.X * q.Y; sxz += p.X * q.Z;
                syx += p.Y * q.X; syy += p.Y * q.Y; syz += p.Y * q.Z;
                szx += p.Z * q.X; szy += p.Z * q.Y; szz += p.Z * q.Z;
            }
            return HornFromCovariance(sxx, sxy, sxz, syx, syy, syz, szx, szy, szz);
        }

        // Horn's method from the cross-covariance S = sum p q^T (p = source, q = target): the rotation taking
        // the p's onto the q's best (maximises sum q . R p).
        public static Quaternion HornFromCovariance(double sxx, double sxy, double sxz, double syx, double syy, double syz, double szx, double szy, double szz)
        {
            var N = new double[4, 4]
            {
                { sxx + syy + szz, syz - szy,        szx - sxz,        sxy - syx },
                { syz - szy,       sxx - syy - szz,  sxy + syx,        szx + sxz },
                { szx - sxz,       sxy + syx,       -sxx + syy - szz,  syz + szy },
                { sxy - syx,       szx + sxz,        syz + szy,       -sxx - syy + szz },
            };
            var V = new double[4, 4];
            for (int i = 0; i < 4; i++) V[i, i] = 1;
            for (int sweep = 0; sweep < 64; sweep++)
            {
                double off = 0;
                for (int i = 0; i < 4; i++) for (int j = i + 1; j < 4; j++) off += N[i, j] * N[i, j];
                if (off < 1e-24) break;
                for (int pI = 0; pI < 3; pI++)
                    for (int qI = pI + 1; qI < 4; qI++)
                    {
                        if (Math.Abs(N[pI, qI]) < 1e-30) continue;
                        double theta = (N[qI, qI] - N[pI, pI]) / (2 * N[pI, qI]);
                        double t = Math.Sign(theta == 0 ? 1 : theta) / (Math.Abs(theta) + Math.Sqrt(theta * theta + 1));
                        double c = 1 / Math.Sqrt(t * t + 1), s = t * c;
                        for (int k = 0; k < 4; k++)
                        {
                            double nkp = N[k, pI], nkq = N[k, qI];
                            N[k, pI] = c * nkp - s * nkq; N[k, qI] = s * nkp + c * nkq;
                        }
                        for (int k = 0; k < 4; k++)
                        {
                            double npk = N[pI, k], nqk = N[qI, k];
                            N[pI, k] = c * npk - s * nqk; N[qI, k] = s * npk + c * nqk;
                        }
                        for (int k = 0; k < 4; k++)
                        {
                            double vkp = V[k, pI], vkq = V[k, qI];
                            V[k, pI] = c * vkp - s * vkq; V[k, qI] = s * vkp + c * vkq;
                        }
                    }
            }
            int best = 0;
            for (int i = 1; i < 4; i++) if (N[i, i] > N[best, best]) best = i;
            // eigenvector = (w, x, y, z)
            return new Quaternion((float)V[1, best], (float)V[2, best], (float)V[3, best], (float)V[0, best]).Normalized();
        }

        // Split a rotation into swing (moves the bone axis) and twist (spin about it); angles in radians.
        // No axis (a leaf bone): everything is reported as swing.
        public static (float swing, float twist) SwingTwist(Matrix4 delta, Vector3? axis)
        {
            var q = delta.ExtractRotation().Normalized();
            if (axis == null) return (2f * MathF.Acos(Math.Clamp(MathF.Abs(q.W), 0f, 1f)), 0f);
            var a = axis.Value;
            var proj = Vector3.Dot(q.Xyz, a) * a;
            var tw = new Quaternion(proj, q.W);
            float twLen = MathF.Sqrt(tw.X * tw.X + tw.Y * tw.Y + tw.Z * tw.Z + tw.W * tw.W);
            float twist = twLen < 1e-6f ? 0f : 2f * MathF.Acos(Math.Clamp(MathF.Abs(tw.W / twLen), 0f, 1f));
            var sw = twLen < 1e-6f ? q : q * Quaternion.Invert(new Quaternion(tw.Xyz / twLen, tw.W / twLen));
            float swing = 2f * MathF.Acos(Math.Clamp(MathF.Abs(sw.W), 0f, 1f));
            return (swing, twist);
        }

        static List<(int idx, float dist)> KNearest(Vector3 p, Vector3[] pts, int k)
        {
            var best = new List<(int idx, float dist)>(k + 1);
            for (int i = 0; i < pts.Length; i++)
            {
                float d = (pts[i] - p).Length;
                if (best.Count < k || d < best[^1].dist)
                {
                    int at = best.Count;
                    while (at > 0 && best[at - 1].dist > d) at--;
                    best.Insert(at, (i, d));
                    if (best.Count > k) best.RemoveAt(k);
                }
            }
            return best;
        }

        static Dictionary<int, float> Normalize(Dictionary<int, float> d, int keep)
        {
            var top = d.OrderByDescending(kv => kv.Value).Take(keep).ToList();
            float sum = top.Sum(kv => kv.Value);
            var r = new Dictionary<int, float>();
            foreach (var kv in top) r[kv.Key] = sum > 0 ? kv.Value / sum : 1f / top.Count;
            return r;
        }

        // coordinate search over the global transform (scale, yaw, offset)
        static float GlobalRefine(Func<float> energy, Action place, ref float scale, ref float yaw, ref Vector3 off, bool fine = false)
        {
            place(); float cur = energy();
            float[] steps = fine ? new[] { 0.02f, 0.01f } : new[] { 0.16f, 0.08f, 0.04f, 0.02f, 0.01f };
            foreach (float st in steps)
            {
                bool improved = true;
                for (int guard = 0; improved && guard < 20; guard++)
                {
                    improved = false;
                    for (int p = 0; p < 5; p++)
                        foreach (float sg in new[] { 1f, -1f })
                        {
                            float s0 = scale, y0 = yaw; var o0 = off;
                            if (p == 0) scale *= 1f + st * 0.5f * sg;
                            else if (p == 1) yaw += st * 2f * sg;
                            else if (p == 2) off.X += st * sg;
                            else if (p == 3) off.Y += st * sg;
                            else off.Z += st * sg;
                            place(); float e = energy();
                            if (e < cur - 1e-7f) { cur = e; improved = true; }
                            else { scale = s0; yaw = y0; off = o0; }
                        }
                }
            }
            place();
            return cur;
        }

        static Matrix4 RandomRotation(Random r)
        {
            // uniform random quaternion (Shoemake)
            double u1 = r.NextDouble(), u2 = r.NextDouble(), u3 = r.NextDouble();
            var q = new Quaternion((float)(Math.Sqrt(1 - u1) * Math.Sin(2 * Math.PI * u2)), (float)(Math.Sqrt(1 - u1) * Math.Cos(2 * Math.PI * u2)),
                                   (float)(Math.Sqrt(u1) * Math.Sin(2 * Math.PI * u3)), (float)(Math.Sqrt(u1) * Math.Cos(2 * Math.PI * u3)));
            return Matrix4.CreateFromQuaternion(q);
        }

        // Uniform hash grid for nearest-neighbour queries within a bounded radius.
        // Uniform grid for bounded-radius neighbour queries, stored densely over the points' bounds
        // (counting sort into one index array). Visits cells and points in exactly the order the
        // earlier hashed version did (dx, dy, dz loops; ascending point index within a cell), so results
        // are bit-identical -- it is only faster (no hashing in the hot loop).
        sealed class Grid
        {
            readonly float _cell;
            int _nx, _ny, _nz, _ox, _oy, _oz;
            int[] _start = Array.Empty<int>(), _items = Array.Empty<int>();
            public Grid(float cell) => _cell = cell;
            int Cx(float v) => (int)MathF.Floor(v / _cell);
            public void Build(Vector3[] pts)
            {
                if (pts.Length == 0) { _nx = _ny = _nz = 0; return; }
                int x0 = int.MaxValue, y0 = int.MaxValue, z0 = int.MaxValue, x1 = int.MinValue, y1 = int.MinValue, z1 = int.MinValue;
                foreach (var p in pts)
                {
                    int x = Cx(p.X), y = Cx(p.Y), z = Cx(p.Z);
                    if (x < x0) x0 = x; if (y < y0) y0 = y; if (z < z0) z0 = z;
                    if (x > x1) x1 = x; if (y > y1) y1 = y; if (z > z1) z1 = z;
                }
                _ox = x0; _oy = y0; _oz = z0; _nx = x1 - x0 + 1; _ny = y1 - y0 + 1; _nz = z1 - z0 + 1;
                int cells = _nx * _ny * _nz;
                if (_start.Length < cells + 1) _start = new int[cells + 1]; else Array.Clear(_start, 0, cells + 1);
                if (_items.Length < pts.Length) _items = new int[pts.Length];
                for (int i = 0; i < pts.Length; i++) _start[Index(pts[i]) + 1]++;
                for (int c = 0; c < cells; c++) _start[c + 1] += _start[c];
                var fill = new int[cells];
                for (int i = 0; i < pts.Length; i++) { int c = Index(pts[i]); _items[_start[c] + fill[c]++] = i; }
            }
            int Index(Vector3 p) => ((Cx(p.X) - _ox) * _ny + (Cx(p.Y) - _oy)) * _nz + (Cx(p.Z) - _oz);
            // indices of all points within radius of p
            public IEnumerable<int> Within(Vector3 p, Vector3[] pts, float radius)
            {
                int cx = Cx(p.X) - _ox, cy = Cx(p.Y) - _oy, cz = Cx(p.Z) - _oz;
                int r = (int)MathF.Ceiling(radius / _cell);
                float r2 = radius * radius;
                for (int x = Math.Max(0, cx - r); x <= Math.Min(_nx - 1, cx + r); x++)
                    for (int y = Math.Max(0, cy - r); y <= Math.Min(_ny - 1, cy + r); y++)
                        for (int z = Math.Max(0, cz - r); z <= Math.Min(_nz - 1, cz + r); z++)
                        {
                            int c = (x * _ny + y) * _nz + z;
                            for (int k = _start[c]; k < _start[c + 1]; k++) { int i = _items[k]; if ((pts[i] - p).LengthSquared <= r2) yield return i; }
                        }
            }
            // distance to the nearest point within maxDist (returns maxDist if none)
            public float Nearest(Vector3 p, Vector3[] pts, float maxDist)
            {
                int cx = Cx(p.X) - _ox, cy = Cx(p.Y) - _oy, cz = Cx(p.Z) - _oz;
                int r = (int)MathF.Ceiling(maxDist / _cell);
                float best = maxDist * maxDist;
                for (int x = Math.Max(0, cx - r); x <= Math.Min(_nx - 1, cx + r); x++)
                    for (int y = Math.Max(0, cy - r); y <= Math.Min(_ny - 1, cy + r); y++)
                        for (int z = Math.Max(0, cz - r); z <= Math.Min(_nz - 1, cz + r); z++)
                        {
                            int c = (x * _ny + y) * _nz + z;
                            for (int k = _start[c]; k < _start[c + 1]; k++)
                            {
                                var d3 = pts[_items[k]] - p;
                                float d = d3.X * d3.X + d3.Y * d3.Y + d3.Z * d3.Z;
                                if (d < best) best = d;
                            }
                        }
                return MathF.Sqrt(best);
            }
        }
    }
}
