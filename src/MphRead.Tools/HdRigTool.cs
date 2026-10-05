using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using MphRead.Entities;
using MphRecomp.Anim;
using MphRecomp.Assets;
using OpenTK.Mathematics;

namespace MphRead
{
    // Rig a Brawl trophy to its DS hunter skeleton (TrophyRigger) and measure the result:
    //  - fit quality: symmetric Chamfer (fraction of body height) before fitting, after the global fit,
    //    after the articulated fit;
    //  - deformation quality: play the game's clips on the rigged trophy (retargeted: clip rotations,
    //    fitted bone lengths) and measure how much every mesh edge stretches/compresses vs the sculpt
    //    (1.0 = rigid). Tearing / candy-wrapping / collapsing joints show up as extreme ratios;
    //  - point dumps for overlay plots (trophy vs fitted DS, and the trophy posed in several clips).
    // Run: MphRead.Tools.dll -hdrig Kanden [more trophies...]
    internal static class HdRigTool
    {
        public static readonly (PlayerAnimation Clip, int Frame)[] ShowPoses =
        {
            (PlayerAnimation.Idle, 0), (PlayerAnimation.WalkForward, 5), (PlayerAnimation.WalkForward, 15),
            (PlayerAnimation.JumpForward, 6), (PlayerAnimation.Flourish, 40), (PlayerAnimation.Shoot, 4),
        };

        public static void Run(string[] args)
        {
            if (!ToolPaths.UseDesktopExtraction()) return;
            string repo = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
            string conv = Path.Combine(repo, "brawl_extract", "Converted");
            // --out <dir> for the point dumps (default: %TEMP%/mph_hdrig)
            int oi = Array.IndexOf(args, "--out");
            string outDir = oi > 0 && oi + 1 < args.Length ? args[oi + 1] : Path.Combine(Path.GetTempPath(), "mph_hdrig");
            int cl = Array.IndexOf(args, "--claim");
            int oni0 = Array.IndexOf(args, "--only");
            var trophies = args.Skip(1).Where(a => !a.StartsWith("--") && !float.TryParse(a, NumberStyles.Float, CultureInfo.InvariantCulture, out _) && (oi < 0 || a != args[oi + 1]) && (oni0 < 0 || a != args[oni0 + 1])).ToList();
            foreach (string trophy in trophies)
            {
                Directory.CreateDirectory(outDir);
                if (!TrophyRigs.TryHunterFor(trophy, out Hunter h)) { Console.WriteLine($"unknown trophy {trophy}"); continue; }
                string? dae = Directory.GetFiles(Path.Combine(conv, trophy), "*.dae").FirstOrDefault();
                if (dae == null) { Console.WriteLine($"{trophy}: no .dae"); continue; }
                Model model = Read.GetModelInstance(Metadata.HunterModels[h][0]).Model;
                DaeModel hd = DaeModel.Load(dae);
                var corners = hd.Meshes.SelectMany(m => m.Positions).Select(p => new Vector3(p.X, p.Y, p.Z)).ToList();
                Console.WriteLine($"=== {trophy} on the {h} rig: {corners.Count / 3} triangles");
                var sw = Stopwatch.StartNew();
                string jointsPath = TrophyLandmarks.PathFor(dae);
                var landmarks = File.Exists(jointsPath) && !args.Contains("--auto") ? TrophyLandmarks.Load(jointsPath) : null;
                Console.WriteLine(landmarks != null ? $"  LANDMARK mode: {landmarks.Joints.Count} joints, {landmarks.Segments.Count} ownership segments{(landmarks.MirrorX ? ", MIRRORED (X flipped)" : "")} from {Path.GetFileName(jointsPath)}" : "  shape-matching mode (no landmark file)");
                string chunksPath = TrophyChunks.PathFor(dae);
                var chunks = landmarks != null && File.Exists(chunksPath) && !args.Contains("--nochunks") ? TrophyChunks.Load(chunksPath, new DsSkeleton(model).Names) : null;
                if (chunks != null) Console.WriteLine($"  CHUNK GROUPING from {Path.GetFileName(chunksPath)}");
                TrophyRig rig = TrophyRigger.Fit(model, corners, new TrophyRigger.Options { Log = Console.WriteLine, RotPriorScale = args.Contains("--noprior") ? 0f : 1f, Landmarks = landmarks, IslandScopedOwnership = !args.Contains("--noislandscope"), Chunks = chunks,
                    PieceClaimFraction = Array.IndexOf(args, "--claim") is int ci && ci > 0 ? float.Parse(args[ci + 1], CultureInfo.InvariantCulture) : 0.3f,
                    PanelDominance = Array.IndexOf(args, "--panels") is int pi && pi > 0 ? float.Parse(args[pi + 1], CultureInfo.InvariantCulture) : 0f,
                    PanelCreaseDeg = Array.IndexOf(args, "--crease") is int cri && cri > 0 ? float.Parse(args[cri + 1], CultureInfo.InvariantCulture) : 35f,
                    BlendScale = Array.IndexOf(args, "--blend") is int bli && bli > 0 ? float.Parse(args[bli + 1], CultureInfo.InvariantCulture) : 1f,
                    CutHandWeldHops = args.Contains("--nocut") ? 0 : 4, CutFarWeldHops = args.Contains("--nocut") || args.Contains("--nofarcut") ? 0 : 5, AlignFacing = !args.Contains("--noface"), AlignHinges = !args.Contains("--nohinge"), FootUp = !args.Contains("--nofoot"), PelvisRoll = !args.Contains("--noroll"), RootFromLegs = !args.Contains("--noroot"), SightOwnership = !args.Contains("--nosight"),
                    CornerMaterial = args.Contains("--nokin") ? null : TrophyRigIO.CornerMaterials(hd) });
                sw.Stop();
                bool variant = args.Contains("--noprior") || args.Contains("--noislandscope") || args.Contains("--nochunks") || args.Contains("--claim") || args.Contains("--unpose") || args.Contains("--panels") || args.Contains("--blend") || args.Contains("--noface") || args.Contains("--nosight") || args.Contains("--nokin") || args.Contains("--nofarcut") || args.Contains("--savestraight") || (args.Contains("--auto") && File.Exists(jointsPath));
                string cachePath = variant ? Path.Combine(outDir, trophy + "_variant.mphrig") : Path.ChangeExtension(dae, ".mphrig");
                TrophyRigIO.Save(cachePath, rig, TrophyRigIO.CacheKey(dae));
                var reloaded = TrophyRigIO.Load(cachePath, TrophyRigIO.CacheKey(dae));
                bool roundTrip = reloaded != null && reloaded.Vertices.Length == rig.Vertices.Length
                    && reloaded.Weights.SequenceEqual(rig.Weights) && reloaded.Bones.SequenceEqual(rig.Bones)
                    && Enumerable.Range(0, rig.BindWorld.Length).All(i => reloaded.BindWorld[i] == rig.BindWorld[i]);
                Console.WriteLine($"  rig cache {Path.GetFileName(cachePath)}: {new FileInfo(cachePath).Length / 1024} KB, save/load round-trip {(roundTrip ? "exact" : "MISMATCH")}");
                Console.WriteLine($"  fit: {sw.Elapsed.TotalSeconds:0.0}s   Chamfer (fraction of body height): start {rig.ChamferStart:0.0000} -> global {rig.ChamferGlobal:0.0000} -> articulated {rig.ChamferFinal:0.0000}   ref {rig.RefPose}");
                var sk = new DsSkeleton(model);
                Console.WriteLine($"  rigid mesh islands: {TrophyRigger.RigidIslands}");
                Console.WriteLine("  rotation away from the reference game pose, swing/twist deg: " + rig.Deviations);
                Console.WriteLine("  bone lengths vs DS: " + string.Join(" ", Enumerable.Range(0, sk.Count).Where(i => MathF.Abs(rig.Lengths[i] - 1) > 0.01f).Select(i => $"{sk.Names[i]}={rig.Lengths[i]:0.00}")));
                var usage = new Dictionary<int, float>();
                for (int i = 0; i < rig.Bones.Length; i++) usage[rig.Bones[i]] = usage.GetValueOrDefault(rig.Bones[i]) + rig.Weights[i];
                Console.WriteLine("  weight per bone (vertex-equivalents): " + string.Join(" ", usage.OrderByDescending(kv => kv.Value).Select(kv => $"{sk.Names[kv.Key]}={kv.Value:0}")));

                // edges of the DRAWN trophy mesh (unique vertex indices; triangles hidden by a replaced part excluded)
                var edges = new HashSet<(int, int)>();
                var hiddenTri = new bool[corners.Count / 3];
                for (int t = 0; t + 2 < corners.Count; t += 3)
                {
                    int a = rig.IndexOf(ToN(corners[t])), b = rig.IndexOf(ToN(corners[t + 1])), c = rig.IndexOf(ToN(corners[t + 2]));
                    if (rig.HidesTriangle(a, b, c)) { hiddenTri[t / 3] = true; continue; }
                    void E(int x, int y) { if (x != y) edges.Add(x < y ? (x, y) : (y, x)); }
                    E(a, b); E(b, c); E(a, c);
                }
                var parts = TrophyRigger.ReplacementParts(model, rig);
                if (rig.Attachments.Length > 0 || rig.Hidden.Length > 0)
                    Console.WriteLine($"  hidden / game parts: {hiddenTri.Count(h => h)} trophy triangles hidden, {parts.Sum(p => p.Corners.Count) / 3} game-model triangles drawn");
                var bind = rig.Vertices.Select(v => Vector3.TransformPosition(v, rig.TrophyToRig)).ToArray();
                var pose = new SkeletonPose(sk.Count);
                var world = new Matrix4[sk.Count];
                var posed = new Vector3[bind.Length];
                float height = bind.Max(p => p.Y) - bind.Min(p => p.Y);
                var lbsStats = new StretchStats(edges.ToArray(), bind, height);
                var trophyStats = new StretchStats(edges.ToArray(), bind, height);
                foreach (bool dqs in new[] { false, true })
                {
                    UseDqs = dqs;
                    ForEachStandingFrame(model, (c, f) =>
                    {
                        DsSkeleton.Sample(model, c, f, pose);
                        sk.Fk(pose, world, 0f, rig.Lengths, rig.Offsets);
                        Deform(rig, bind, world, posed);
                        (dqs ? trophyStats : lbsStats).Add(posed, $"{(PlayerAnimation)c} f{f}");
                    });
                }
                UseDqs = false;
                Console.WriteLine("  TROPHY (linear blend)   " + lbsStats.Report());
                Console.WriteLine("  TROPHY " + FollowReport(model, sk, rig, rig, bind, height));
                {
                    // distortion heat map (linear blend, the runtime path): per corner, trophy coords + heat in % of height
                    var heat = lbsStats.VertexHeat(rig.Vertices.Length);
                    var hn = hd.Meshes.SelectMany(m => m.Normals).ToList();
                    File.WriteAllLines(Path.Combine(outDir, $"{trophy}_heat_surface.txt"), corners.Select((p, i) => (p, i)).Where(c => !hiddenTri[c.i / 3]).Select(c =>
                        string.Create(CultureInfo.InvariantCulture, $"{c.p.X} {c.p.Y} {c.p.Z} {hn[c.i].X} {hn[c.i].Y} {hn[c.i].Z} {heat[rig.IndexOf(ToN(c.p))]:0.###}")));
                }
                // How far each JOINT is bent away from the pose the mesh is bound in, over every game frame.
                // Linear blending collapses a joint roughly as cos(angle/2): this is the root driver of the
                // distortion. Bound in the trophy's sculpted pose vs bound in the game's own Idle pose.
                {
                    var idleW = new Matrix4[sk.Count];
                    DsSkeleton.Sample(model, (int)PlayerAnimation.Idle, 0, pose);
                    sk.Fk(pose, idleW, 0f, rig.Lengths, rig.Offsets);
                    var owns = new bool[sk.Count];
                    foreach (int b in rig.Bones) owns[b] = true;
                    var rows = new List<(string Name, List<float> Sculpt, List<float> Idle)>();
                    var locals = new List<List<Quaternion>>();   // per joint: the child's rotation relative to its partner, every frame
                    int Partner(int c) { for (int x = sk.Parent[c]; x >= 0; x = sk.Parent[x]) if (owns[x]) return x; return -1; }
                    static float RelAngle(Matrix4 a, Matrix4 b)
                    {
                        var q = (Matrix4.Invert(a.ClearTranslation()) * b.ClearTranslation()).ExtractRotation().Normalized();
                        return 2f * MathF.Acos(Math.Clamp(MathF.Abs(q.W), 0f, 1f)) * 180f / MathF.PI;
                    }
                    for (int c = 0; c < sk.Count; c++)
                        if (owns[c] && Partner(c) >= 0) { rows.Add((sk.Names[c] + "|" + sk.Names[Partner(c)], new List<float>(), new List<float>())); locals.Add(new List<Quaternion>()); }
                    ForEachStandingFrame(model, (cl, f) =>
                    {
                        DsSkeleton.Sample(model, cl, f, pose);
                        sk.Fk(pose, world, 0f, rig.Lengths, rig.Offsets);
                        int r = 0;
                        for (int c = 0; c < sk.Count; c++)
                        {
                            if (!(owns[c] && Partner(c) >= 0)) continue;
                            int pp = Partner(c);
                            // skin rotation of each bone = bind^-1 * pose; the joint's blend angle is their difference
                            var sc = rig.InvBind[c] * world[c]; var sp = rig.InvBind[pp] * world[pp];
                            var ic = Matrix4.Invert(idleW[c]) * world[c]; var ip = Matrix4.Invert(idleW[pp]) * world[pp];
                            rows[r].Sculpt.Add(RelAngle(sp, sc)); rows[r].Idle.Add(RelAngle(ip, ic));
                            locals[r].Add((world[c].ClearTranslation() * Matrix4.Invert(world[pp].ClearTranslation())).ExtractRotation().Normalized()); r++;
                        }
                    });
                    static float Pct(List<float> v, double q) { var o = v.OrderBy(x => x).ToList(); return o[(int)Math.Min(o.Count - 1, q * o.Count)]; }
                    // the third option: bind each joint at the CENTRE of its own range of motion in the game's clips
                    // (minimax centre of its rotations, Badoiu-Clarkson on the rotation sphere)
                    static float QAng(Quaternion a, Quaternion b) { float d = MathF.Abs(a.X * b.X + a.Y * b.Y + a.Z * b.Z + a.W * b.W); return 2f * MathF.Acos(Math.Clamp(d, 0f, 1f)) * 180f / MathF.PI; }
                    var centred = new List<List<float>>();
                    foreach (var qs in locals)
                    {
                        var c0 = qs[0];
                        for (int k = 0; k < 400; k++)
                        {
                            var far = qs.OrderByDescending(q => QAng(c0, q)).First();
                            if (c0.X * far.X + c0.Y * far.Y + c0.Z * far.Z + c0.W * far.W < 0) far = new Quaternion(-far.X, -far.Y, -far.Z, -far.W);
                            c0 = Quaternion.Slerp(c0, far, 1f / (k + 2)).Normalized();
                        }
                        centred.Add(qs.Select(q => QAng(c0, q)).ToList());
                    }
                    Console.WriteLine("  JOINT BEND vs bind pose, degrees (median / 95% / max over all frames):  bound as SCULPTED  ->  bound in game IDLE  ->  bound at CENTRE of motion");
                    foreach (var (i, (nm, sc, id)) in rows.Select((r, i) => (i, r)).OrderByDescending(x => Pct(x.r.Sculpt, 0.5)))
                        Console.WriteLine($"    {nm,-28} {Pct(sc, 0.5),5:0} {Pct(sc, 0.95),5:0} {sc.Max(),5:0}   ->  {Pct(id, 0.5),5:0} {Pct(id, 0.95),5:0} {id.Max(),5:0}   ->  {Pct(centred[i], 0.5),5:0} {Pct(centred[i], 0.95),5:0} {centred[i].Max(),5:0}");
                }
                Console.WriteLine("  TROPHY (dual quaternion) " + trophyStats.Report());
                // which bones meet across the worst edges (primary bone of each endpoint)
                var pairs = trophyStats.WorstEdges(0.002).GroupBy(e => (Math.Min(rig.Bones[e.Item1 * 4], rig.Bones[e.Item2 * 4]), Math.Max(rig.Bones[e.Item1 * 4], rig.Bones[e.Item2 * 4])))
                    .OrderByDescending(g => g.Count()).Take(8).Select(g => $"{sk.Names[g.Key.Item1]}|{sk.Names[g.Key.Item2]}={g.Count()}");
                Console.WriteLine("  worst 0.2% edges by endpoint primary bones: " + string.Join(" ", pairs));
                File.WriteAllLines(Path.Combine(outDir, $"{trophy}_worstedges.txt"), trophyStats.WorstEdges(0.002).Select(e =>
                    string.Create(CultureInfo.InvariantCulture, $"{bind[e.Item1].X} {bind[e.Item1].Y} {bind[e.Item1].Z} {bind[e.Item2].X} {bind[e.Item2].Y} {bind[e.Item2].Z} {sk.Names[rig.Bones[e.Item1 * 4]]} {sk.Names[rig.Bones[e.Item2 * 4]]}")));
                DsBaseline(model, sk);
                if (args.Contains("--facing"))
                {
                    // each fitted bone's FACING in the bind (sculpt) pose: the game's forward (-Z in Idle f0) carried by
                    // bind * idle^-1, as a yaw in degrees (rig space) -- to compare with the facing of the mesh it owns
                    var idleW = new Matrix4[sk.Count];
                    DsSkeleton.Sample(model, (int)PlayerAnimation.Idle, 0, pose);
                    sk.Fk(pose, idleW, 0f, rig.Lengths, rig.Offsets);
                    var lines = new List<string>();
                    for (int i = 0; i < sk.Count; i++)
                    {
                        var rot = (Matrix4.Invert(idleW[i].ClearTranslation()) * rig.BindWorld[i].ClearTranslation());
                        var f = Vector3.TransformVector(-Vector3.UnitZ, rot); var u = Vector3.TransformVector(Vector3.UnitY, rot);
                        lines.Add(string.Create(CultureInfo.InvariantCulture, $"{sk.Names[i]} {MathF.Atan2(f.X, -f.Z) * 180 / MathF.PI:0.0} {f.X} {f.Y} {f.Z} {u.X} {u.Y} {u.Z}"));
                    }
                    File.WriteAllLines(Path.Combine(outDir, $"{trophy}_facing.txt"), lines);
                    Console.WriteLine("  bind facing yaw (deg): " + string.Join(" ", lines.Select(l => l.Split(' ')).Where(t => t[0] is "Pelvis" or "Spine_1" or "Spine_2" or "Head_1").Select(t => $"{t[0]}={t[1]}")));
                }
                if (args.Contains("--dumpweights"))
                {
                    // per unique vertex: rig-space bind position (as sculpted), 4 bone names + weights; and the drawn
                    // triangles as vertex indices -- for comparing our weighting with hand-made rigs (ssbu_rig_study.py)
                    File.WriteAllLines(Path.Combine(outDir, $"{trophy}_weights.txt"), Enumerable.Range(0, bind.Length).Select(v =>
                        string.Create(CultureInfo.InvariantCulture, $"{bind[v].X} {bind[v].Y} {bind[v].Z} ") + string.Join(" ", Enumerable.Range(0, 4).Select(k =>
                            string.Create(CultureInfo.InvariantCulture, $"{sk.Names[rig.Bones[v * 4 + k]]} {rig.Weights[v * 4 + k]}")))));
                    var triLines = new List<string>();
                    for (int t = 0; t + 2 < corners.Count; t += 3)
                        if (!hiddenTri[t / 3]) triLines.Add($"{rig.IndexOf(ToN(corners[t]))} {rig.IndexOf(ToN(corners[t + 1]))} {rig.IndexOf(ToN(corners[t + 2]))}");
                    File.WriteAllLines(Path.Combine(outDir, $"{trophy}_tris.txt"), triLines);
                    // per corner: vertex index, UV, mesh index -- UVs survive re-posing, so they match this mesh to a
                    // re-posed copy of the same trophy
                    var cornerLines = new List<string>();
                    for (int mi = 0, cc = 0; mi < hd.Meshes.Count; mi++)
                        for (int k = 0; k < hd.Meshes[mi].Positions.Count; k++, cc++)
                            cornerLines.Add(string.Create(CultureInfo.InvariantCulture, $"{rig.IndexOf(ToN(corners[cc]))} {hd.Meshes[mi].Uvs[k].X} {hd.Meshes[mi].Uvs[k].Y} {mi}"));
                    File.WriteAllLines(Path.Combine(outDir, $"{trophy}_corners.txt"), cornerLines);
                    Console.WriteLine($"  weights + triangles -> {outDir}");
                }
                if (args.Contains("--unpose"))
                    UnposeExperiment(trophy, dae, outDir, model, sk, rig, corners, edges.ToArray(), hiddenTri, bind, height, hd, lbsStats);
                if (args.Contains("--savestraight") || args.Contains("--savevariants"))
                {
                    // (re)build the alternative rigs the app flips between (<dae>.<variant>.mphrig), then measure each
                    // SAVED file exactly as the app will use it: its own bind positions, rebound pose, linear blend
                    // --only <key>: just that one variant
                    string? only = Array.IndexOf(args, "--only") is int oni && oni > 0 && oni + 1 < args.Length ? args[oni + 1] : null;
                    foreach (var v in TrophyRigIO.Variants.Where(v => only != null ? v.Key == only : args.Contains("--savevariants") || v.Key == "straight"))
                    {
                        string sp = TrophyRigIO.VariantPathFor(dae, v);
                        if (File.Exists(sp)) File.Delete(sp);
                        Console.WriteLine($"  --- variant '{v.Key}': {v.Label}");
                        if (TrophyRigIO.LoadOrFitVariant(dae, model, corners, v, fit: true, Console.WriteLine) == null) continue;
                        var st = TrophyRigIO.Load(sp, TrophyRigIO.VariantKey(dae, v))!;
                        var stStats = new StretchStats(edges.ToArray(), bind, height);
                        var stPosed = new Vector3[bind.Length];
                        UseDqs = false;
                        ForEachStandingFrame(model, (c, f) =>
                        {
                            DsSkeleton.Sample(model, c, f, pose);
                            sk.Fk(pose, world, 0f, st.Lengths, st.Offsets);
                            Deform(st, st.BindPos!, world, stPosed);
                            stStats.Add(stPosed, $"{(PlayerAnimation)c} f{f}");
                        });
                        int oneBone = Enumerable.Range(0, bind.Length).Count(q => st.Weights[q * 4] > 0.99f);
                        Console.WriteLine("  " + DiffReport(model, sk, st, TrophyRigIO.Load(TrophyRigIO.StraightPathFor(dae), TrophyRigIO.StraightKey(dae)), bind, height));
                        Console.WriteLine($"  {v.Key.ToUpperInvariant()} RIG {Path.GetFileName(sp)} ({new FileInfo(sp).Length / 1024} KB, as loaded; {100.0 * oneBone / bind.Length:0.0}% of vertices on one bone) " + stStats.Report());
                        var vh = stStats.VertexHeat(bind.Length);
                        File.WriteAllLines(Path.Combine(outDir, $"{trophy}_{v.Key}_heat_surface.txt"), corners.Select((p, i) => (p, i)).Where(c => !hiddenTri[c.i / 3]).Select(c =>
                            string.Create(CultureInfo.InvariantCulture, $"{c.p.X} {c.p.Y} {c.p.Z} 0 1 0 {vh[rig.IndexOf(ToN(c.p))]:0.###}")));
                        File.WriteAllLines(Path.Combine(outDir, $"{trophy}_{v.Key}_bones_surface.txt"), corners.Select((p, i) =>
                            string.Create(CultureInfo.InvariantCulture, $"{p.X} {p.Y} {p.Z} 0 1 0 {(hiddenTri[i / 3] ? 99 : st.Bones[rig.IndexOf(ToN(p)) * 4])}")));
                        // attached game parts (Weavel's gun) must sit exactly where they do on the sculpt-bound rig, every frame
                        var pa = TrophyRigger.ReplacementParts(model, rig); var pb = TrophyRigger.ReplacementParts(model, st);
                        float worst = 0; int nc = 0;
                        if (pa.Count == pb.Count && pa.Zip(pb).All(x => x.First.Corners.Count == x.Second.Corners.Count))
                            ForEachStandingFrame(model, (c, f) =>
                            {
                                DsSkeleton.Sample(model, c, f, pose);
                                sk.Fk(pose, world, 0f, st.Lengths, st.Offsets);
                                for (int k = 0; k < pa.Count; k++)
                                    for (int q = 0; q < pa[k].Corners.Count; q++)
                                    {
                                        var ca = pa[k].Corners[q]; var cb = pb[k].Corners[q];
                                        var d = Vector3.TransformPosition(ca.Pos, rig.InvBind[ca.Node] * world[ca.Node]) - Vector3.TransformPosition(cb.Pos, st.InvBind[cb.Node] * world[cb.Node]);
                                        worst = MathF.Max(worst, d.Length / height); nc++;
                                    }
                            });
                        else worst = float.NaN;
                        Console.WriteLine($"  attached game parts: {pb.Sum(p => p.Corners.Count) / 3} triangles; worst offset vs the sculpt-bound rig over every frame {worst * 100:0.0000}% of height ({nc} corner-frames)");
                    }
                }
                // weld census: triangles whose corners' main bones are >= 4 skeletal hops apart -- parts the
                // sculpt fused together (a forearm against a hip) that any rig must pull apart when they move
                int Hops(int x, int y)
                {
                    var anc = new Dictionary<int, int>();
                    for (int q = x, h0 = 0; q >= 0; q = sk.Parent[q], h0++) anc[q] = h0;
                    for (int q = y, h0 = 0; q >= 0; q = sk.Parent[q], h0++) if (anc.TryGetValue(q, out int hx)) return hx + h0;
                    return 99;
                }
                var weldPairs = new Dictionary<string, int>(); int welds = 0, tris = corners.Count / 3;
                for (int t = 0; t + 2 < corners.Count; t += 3)
                {
                    if (hiddenTri[t / 3]) continue;
                    int[] v = { rig.IndexOf(ToN(corners[t])), rig.IndexOf(ToN(corners[t + 1])), rig.IndexOf(ToN(corners[t + 2])) };
                    int[] bo = v.Select(x => rig.Bones[x * 4]).ToArray();
                    int worstH = 0; (int, int) wp = default;
                    for (int x = 0; x < 3; x++) for (int y = x + 1; y < 3; y++) { int hh = Hops(bo[x], bo[y]); if (hh > worstH) { worstH = hh; wp = (bo[x], bo[y]); } }
                    if (worstH >= 4)
                    {
                        welds++;
                        string key = string.Join("|", new[] { sk.Names[wp.Item1], sk.Names[wp.Item2] }.OrderBy(q => q));
                        weldPairs[key] = weldPairs.GetValueOrDefault(key) + 1;
                    }
                }
                Console.WriteLine($"  welds: {welds} of {tris} triangles ({100.0 * welds / tris:0.00}%) bridge bones >= 4 hops apart: " +
                    string.Join(" ", weldPairs.OrderByDescending(kv => kv.Value).Take(6).Select(kv => $"{kv.Key}={kv.Value}")));

                // segmentation dump: every triangle corner in TROPHY coordinates with its main bone (plotlm.py colours by it)
                {
                    var nrm = hd.Meshes.SelectMany(m => m.Normals).ToList();
                    File.WriteAllLines(Path.Combine(outDir, $"{trophy}_bones_surface.txt"), corners.Select((p, i) =>
                    {
                        int vi = rig.IndexOf(ToN(p));
                        int bone = hiddenTri[i / 3] ? 99 : rig.Bones[vi * 4];   // 99 = hidden (replaced part)
                        return string.Create(CultureInfo.InvariantCulture, $"{p.X} {p.Y} {p.Z} {nrm[i].X} {nrm[i].Y} {nrm[i].Z} {bone}");
                    }));
                    File.WriteAllLines(Path.Combine(outDir, $"{trophy}_bone_names.txt"), Enumerable.Range(0, sk.Count).Select(i => $"{i} {sk.Names[i]}"));
                }
                // dumps: trophy (rig space) + fitted DS points, and posed trophies
                Dump(Path.Combine(outDir, $"{trophy}_bind.txt"), bind, rig, null);
                foreach (var (clip, frame) in ShowPoses)
                {
                    DsSkeleton.Sample(model, (int)clip, frame, pose);
                    sk.Fk(pose, world, 0f, rig.Lengths, rig.Offsets);
                    Deform(rig, bind, world, posed);
                    {
                        // renderable posed triangles (rig space, per corner) coloured by main bone
                        var nrm2 = hd.Meshes.SelectMany(m => m.Normals).ToList();
                        // drawn trophy triangles, then the game model's replacement parts (bone index + 40)
                        var lines = corners.Select((p, i) => (p, i)).Where(c => !hiddenTri[c.i / 3]).Select(c =>
                        {
                            int vi = rig.IndexOf(ToN(c.p)); var q = posed[vi];
                            return string.Create(CultureInfo.InvariantCulture, $"{q.X} {q.Y} {q.Z} 0 1 0 {rig.Bones[vi * 4]}");
                        }).ToList();
                        foreach (var (_, pc) in parts)
                            foreach (var c in pc)
                            {
                                var q = Vector3.TransformPosition(c.Pos, rig.InvBind[c.Node] * world[c.Node]);
                                lines.Add(string.Create(CultureInfo.InvariantCulture, $"{q.X} {q.Y} {q.Z} 0 1 0 {c.Node + 40}"));
                            }
                        File.WriteAllLines(Path.Combine(outDir, $"{trophy}_pose_{clip}_{frame}_surface.txt"), lines);
                    }
                    Dump(Path.Combine(outDir, $"{trophy}_{clip}_{frame}.txt"), posed, rig, null);
                }
                File.WriteAllLines(Path.Combine(outDir, $"{trophy}_fittedds.txt"), rig.FittedDs.Select((p, i) => string.Create(CultureInfo.InvariantCulture, $"{p.X} {p.Y} {p.Z} {rig.FittedDsNode[i]}")));
                // joints (bind + a walk frame) for skeleton overlays
                File.WriteAllLines(Path.Combine(outDir, $"{trophy}_joints.txt"), Enumerable.Range(0, sk.Count).Select(i =>
                {
                    var p = rig.BindWorld[i].ExtractTranslation() * model.Scale.X;
                    return string.Create(CultureInfo.InvariantCulture, $"{sk.Names[i]} {sk.Parent[i]} {p.X} {p.Y} {p.Z}");
                }));
                Console.WriteLine($"  dumps -> {outDir}");
            }
        }

        // -hdjoints T...: write a STARTING GUESS for hand-marked landmarks (<dae>.joints.auto.txt) from the
        // shape-matching rig, plus the trophy surface in trophy coordinates for plotting.
        public static void Joints(string[] args)
        {
            if (!ToolPaths.UseDesktopExtraction()) return;
            string repo = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
            string conv = Path.Combine(repo, "brawl_extract", "Converted");
            int oi = Array.IndexOf(args, "--out");
            string outDir = oi > 0 && oi + 1 < args.Length ? args[oi + 1] : Path.Combine(Path.GetTempPath(), "mph_hdrig");
            Directory.CreateDirectory(outDir);
            foreach (string trophy in args.Skip(1).Where((a, i) => !a.StartsWith("--") && (oi < 0 || i + 1 != oi + 1)))
            {
                if (!TrophyRigs.TryHunterFor(trophy, out Hunter h)) { Console.WriteLine($"unknown trophy {trophy}"); continue; }
                string dae = Directory.GetFiles(Path.Combine(conv, trophy), "*.dae").First();
                Model model = Read.GetModelInstance(Metadata.HunterModels[h][0]).Model;
                DaeModel hd = DaeModel.Load(dae);
                var corners = hd.Meshes.SelectMany(m => m.Positions).Select(p => new Vector3(p.X, p.Y, p.Z)).ToList();
                var normals = hd.Meshes.SelectMany(m => m.Normals).Select(p => new Vector3(p.X, p.Y, p.Z)).ToList();
                // seed from the shape-matching rig (never from a landmark rig -- that's what we're seeding)
                var rig = TrophyRigger.Fit(model, corners, new TrophyRigger.Options());
                var sk = new DsSkeleton(model);
                var toTrophy = Matrix4.Invert(rig.TrophyToRig);
                var owns = new bool[sk.Count]; var cen = new Vector3[sk.Count]; var cnt = new int[sk.Count];
                for (int i = 0; i < rig.FittedDs.Length; i++) { owns[rig.FittedDsNode[i]] = true; cen[rig.FittedDsNode[i]] += rig.FittedDs[i]; cnt[rig.FittedDsNode[i]]++; }
                var list = new List<(string, Vector3, string)>();
                var pos = new Vector3[sk.Count];
                for (int i = 0; i < sk.Count; i++) pos[i] = rig.BindWorld[i].ExtractTranslation() * model.Scale.X;
                for (int i = 0; i < sk.Count; i++)
                {
                    int par = sk.Parent[i];
                    if (par < 0) continue;                                                              // Dummy_Root is not a joint
                    if ((pos[i] - pos[par]).Length < 1e-4f && sk.Names[par] != "Dummy_Root") continue;  // coincides with its parent
                    bool hasChild = Enumerable.Range(0, sk.Count).Any(c => sk.Parent[c] == i);
                    if (!owns[i] && !hasChild) continue;
                    list.Add((sk.Names[i], Vector3.TransformPosition(pos[i], toTrophy), "joint"));
                    bool leaf = !Enumerable.Range(0, sk.Count).Any(c => sk.Parent[c] == i && (pos[c] - pos[i]).Length > 1e-4f);
                    if (leaf && owns[i]) list.Add((sk.Names[i] + ".center", Vector3.TransformPosition(cen[i] / cnt[i], toTrophy), "part centre"));
                }
                string autoPath = Path.ChangeExtension(dae, ".joints.auto.txt");
                TrophyLandmarks.Save(autoPath, list, $"{trophy} on the {h} rig -- AUTO seed from shape matching; correct by eye and save as {Path.GetFileName(TrophyLandmarks.PathFor(dae))}");
                var matOf = hd.Meshes.SelectMany((m, mi) => Enumerable.Repeat(mi, m.Positions.Count)).ToList();
                File.WriteAllLines(Path.Combine(outDir, $"{trophy}_surface.txt"), corners.Select((p, i) => string.Create(CultureInfo.InvariantCulture, $"{p.X} {p.Y} {p.Z} {normals[i].X} {normals[i].Y} {normals[i].Z} {matOf[i]}")));
                File.WriteAllLines(Path.Combine(outDir, $"{trophy}_materials.txt"), hd.Meshes.Select((m, mi) => $"{mi} {m.Material}"));
                Console.WriteLine($"{trophy}: {list.Count} seed landmarks -> {autoPath}; surface -> {outDir}");
            }
        }

        // -dsdump Hunter --out dir: the DS model posed in Idle f0 (model space, faces -Z) as triangles with
        // their bone, plus its joints in landmark-file format -- the anatomical reference for placing a
        // trophy's landmarks the way the game's own rig places its joints.
        public static void DsDump(string[] args)
        {
            if (!ToolPaths.UseDesktopExtraction()) return;
            int oi = Array.IndexOf(args, "--out");
            string outDir = oi > 0 && oi + 1 < args.Length ? args[oi + 1] : Path.Combine(Path.GetTempPath(), "mph_hdrig");
            Directory.CreateDirectory(outDir);
            var h = Enum.Parse<Hunter>(args[1], ignoreCase: true);
            Model model = Read.GetModelInstance(Metadata.HunterModels[h][0]).Model;
            var a = new BipedAnimator(model);
            a.SetBoth(PlayerAnimation.Idle, AnimFlags.None);
            var pal = new float[16 * 32];
            HunterRig.Pose(a, 0f, pal);
            var baked = MphRecomp.Render.GeometryBaker.Bake(model, new float[3], out _, useAnimation: true, useMatrixStack: true, skinned: true);
            var lines = new List<string>();
            foreach (var b in baked)
                for (int i = 0; i + 11 < b.Verts.Count; i += 12)
                {
                    int slot = (int)b.Verts[i + 11];
                    var m = model.Nodes[model.NodeMatrixIds[slot]].Animation;
                    var p = Vector3.TransformPosition(new Vector3(b.Verts[i], b.Verts[i + 1], b.Verts[i + 2]), m) * model.Scale.X;
                    lines.Add(string.Create(CultureInfo.InvariantCulture, $"{p.X} {p.Y} {p.Z} 0 1 0 {model.NodeMatrixIds[slot]}"));
                }
            File.WriteAllLines(Path.Combine(outDir, $"DS_{h}_surface.txt"), lines);
            // per corner: batch (material) index, texture coords, bone -- plus each batch's texture as raw
            // pixels (int32 per texel, W x H header) for inspecting which parts of a bone's mesh are what
            var bl = new List<string>();
            for (int bi = 0; bi < baked.Count; bi++)
            {
                var b = baked[bi];
                for (int i = 0; i + 11 < b.Verts.Count; i += 12)
                {
                    int slot = (int)b.Verts[i + 11];
                    var m = model.Nodes[model.NodeMatrixIds[slot]].Animation;
                    var p = Vector3.TransformPosition(new Vector3(b.Verts[i], b.Verts[i + 1], b.Verts[i + 2]), m) * model.Scale.X;
                    bl.Add(string.Create(CultureInfo.InvariantCulture, $"{p.X} {p.Y} {p.Z} {b.Verts[i + 6]} {b.Verts[i + 7]} {model.NodeMatrixIds[slot]} {bi}"));
                }
                if (b.Pixels != null)
                    using (var w = new BinaryWriter(File.Create(Path.Combine(outDir, $"DS_{h}_tex{bi}.raw"))))
                    { w.Write(b.W); w.Write(b.H); foreach (int px in b.Pixels) w.Write(px); }
            }
            File.WriteAllLines(Path.Combine(outDir, $"DS_{h}_batches.txt"), bl);
            File.WriteAllLines(Path.Combine(outDir, $"DS_{h}_bone_names.txt"), Enumerable.Range(0, model.Nodes.Count).Select(i => $"{i} {model.Nodes[i].Name}"));
            var sk = new DsSkeleton(model); var pose = new SkeletonPose(sk.Count); var world = new Matrix4[sk.Count];
            DsSkeleton.Sample(model, (int)PlayerAnimation.Idle, 0, pose); sk.Fk(pose, world);
            TrophyLandmarks.Save(Path.Combine(outDir, $"DS_{h}_joints.txt"),
                Enumerable.Range(1, sk.Count - 1).Select(i => (sk.Names[i], world[i].ExtractTranslation() * model.Scale.X, "")), $"DS {h} Idle f0 joints (model space, faces -Z)");
            Console.WriteLine($"DS {h}: {lines.Count / 3} triangles -> {outDir}");
        }

        // linear blend skinning in rig space: v' = sum w * (v * InvBind[b] * World[b]) (model scale 1 for hunters)
        public static bool UseDqs;
        public static void Deform(TrophyRig rig, Vector3[] bind, Matrix4[] world, Vector3[] outPts)
        {
            var skin = new Matrix4[world.Length];
            for (int i = 0; i < world.Length; i++) skin[i] = rig.InvBind[i] * world[i];
            if (UseDqs)
            {
                var dq = new DualQuat[world.Length];
                for (int i = 0; i < world.Length; i++) dq[i] = DualQuat.FromMatrix(skin[i]);
                Span<DualQuat> four = stackalloc DualQuat[4]; Span<float> ww = stackalloc float[4];
                for (int v = 0; v < bind.Length; v++)
                {
                    for (int k = 0; k < 4; k++) { four[k] = dq[rig.Bones[v * 4 + k]]; ww[k] = rig.Weights[v * 4 + k]; }
                    outPts[v] = DualQuat.Blend(four, ww).TransformPoint(bind[v]);
                }
                return;
            }
            for (int v = 0; v < bind.Length; v++)
            {
                Vector3 acc = Vector3.Zero;
                for (int k = 0; k < 4; k++)
                {
                    float w = rig.Weights[v * 4 + k];
                    if (w <= 0) continue;
                    acc += Vector3.TransformPosition(bind[v], skin[rig.Bones[v * 4 + k]]) * w;
                }
                outPts[v] = acc;
            }
        }

        // --unpose: straighten the trophy (TrophyUnpose, as-rigid-as-possible) out of its sculpted pose into the
        // game's Idle pose -- shoulders at the centre of their range of motion -- rebind there, and measure
        // every standing frame against the SCULPTED shape, so the straightening's own distortion counts too.
        static void UnposeExperiment(string trophy, string dae, string outDir, Model model, DsSkeleton sk, TrophyRig rig, List<Vector3> corners,
                                     (int, int)[] edges, bool[] hiddenTri, Vector3[] bind, float height, DaeModel hd, StretchStats before)
        {
            int nb = sk.Count, nv = bind.Length;
            var pose = new SkeletonPose(nb); var world = new Matrix4[nb];
            var rig2 = TrophyUnpose.Straighten(model, rig, corners, out bool[] handle, Console.WriteLine);
            var res = new TrophyUnpose.Result { Pos = rig2.BindPos!, Rot = rig2.BindRot! };
            // the straightened mesh's own distortion vs the sculpt, then every frame bound in the new pose
            var straight = new StretchStats(edges, bind, height);
            straight.Add(res.Pos, "straightened");
            Console.WriteLine("  STRAIGHTENED MESH vs sculpt   " + straight.Report());
            var after = new StretchStats(edges, bind, height);
            var posed = new Vector3[nv];
            UseDqs = false;   // the runtime blends linearly (the main report leaves this switched on)
            ForEachStandingFrame(model, (c, f) =>
            {
                DsSkeleton.Sample(model, c, f, pose);
                sk.Fk(pose, world, 0f, rig.Lengths, rig.Offsets);
                Deform(rig2, res.Pos, world, posed);
                after.Add(posed, $"{(PlayerAnimation)c} f{f}");
            });
            Console.WriteLine("  BEFORE (bound as sculpted)    " + before.Report());
            Console.WriteLine("  AFTER  (straightened, rebound) " + after.Report());
            Console.WriteLine("  AFTER  " + FollowReport(model, sk, rig, rig2, bind, height));
            Console.WriteLine("  AFTER  " + DiffReport(model, sk, rig2, TrophyRigIO.Load(TrophyRigIO.StraightPathFor(dae), TrophyRigIO.StraightKey(dae)), bind, height));
            {
                var afterDq = new StretchStats(edges, bind, height);
                UseDqs = true;
                ForEachStandingFrame(model, (c, f) =>
                {
                    DsSkeleton.Sample(model, c, f, pose);
                    sk.Fk(pose, world, 0f, rig.Lengths, rig.Offsets);
                    Deform(rig2, res.Pos, world, posed);
                    afterDq.Add(posed, $"{(PlayerAnimation)c} f{f}");
                });
                UseDqs = false;
                Console.WriteLine("  AFTER, dual quaternion         " + afterDq.Report());
            }
            var hn = hd.Meshes.SelectMany(m => m.Normals).ToList();
            var heat = after.VertexHeat(nv);
            File.WriteAllLines(Path.Combine(outDir, $"{trophy}_heat_after_surface.txt"), corners.Select((p, i) => (p, i)).Where(c => !hiddenTri[c.i / 3]).Select(c =>
                string.Create(CultureInfo.InvariantCulture, $"{c.p.X} {c.p.Y} {c.p.Z} {hn[c.i].X} {hn[c.i].Y} {hn[c.i].Z} {heat[rig.IndexOf(ToN(c.p))]:0.###}")));
            // straightened mesh + a few posed frames, rig space, coloured by main bone (handles marked +40)
            void DumpPose(string name, Vector3[] pts, bool markHandles)
            {
                File.WriteAllLines(Path.Combine(outDir, $"{trophy}_{name}_surface.txt"), corners.Select((p, i) => (p, i)).Where(c => !hiddenTri[c.i / 3]).Select(c =>
                {
                    int vi = rig.IndexOf(ToN(c.p)); var q = pts[vi];
                    return string.Create(CultureInfo.InvariantCulture, $"{q.X} {q.Y} {q.Z} 0 1 0 {rig.Bones[vi * 4] + (markHandles && handle[vi] ? 40 : 0)}");
                }));
            }
            DumpPose("straightened", res.Pos, true);
            foreach (var (clip, frame) in ShowPoses)
            {
                DsSkeleton.Sample(model, (int)clip, frame, pose);
                sk.Fk(pose, world, 0f, rig.Lengths, rig.Offsets);
                Deform(rig2, res.Pos, world, posed);
                DumpPose($"unposed_{clip}_{frame}", posed, false);
            }
        }

        // Edge stretch alone can be fooled: a limb stuck rigidly to the WRONG bone never stretches. This checks the
        // other half -- does the middle of each limb follow that limb? Reference points: vertices whose nearest bone
        // segment (sculpt-bound skeleton) is unambiguous (projection in the middle 20-80% of the segment, the next
        // segment at least 1.5x as far). Each must stay with that bone moved rigidly; report how far it drifts
        // over every standing frame, % of height.
        static string FollowReport(Model model, DsSkeleton sk, TrophyRig baseRig, TrophyRig test, Vector3[] bind, float height)
        {
            int nb = sk.Count;
            var joint = baseRig.BindWorld.Select(m => m.ExtractTranslation()).ToArray();
            var owns = new bool[nb]; foreach (int b in baseRig.Bones) owns[b] = true;
            var segs = new List<(int Bone, Vector3 A, Vector3 B)>();
            for (int i = 0; i < nb; i++)
                if (owns[i]) for (int c = 0; c < nb; c++) if (sk.Parent[c] == i && (joint[c] - joint[i]).Length > 1e-4f) segs.Add((i, joint[i], joint[c]));
            var refBone = new int[bind.Length]; Array.Fill(refBone, -1);
            for (int v = 0; v < bind.Length; v++)
            {
                float d1 = float.MaxValue, d2 = float.MaxValue, t1 = 0; int b1 = -1;
                foreach (var (b, a, e) in segs)
                {
                    var ab = e - a; float t = Math.Clamp(Vector3.Dot(bind[v] - a, ab) / ab.LengthSquared, 0f, 1f);
                    float d = (bind[v] - (a + ab * t)).Length;
                    if (d < d1) { d2 = d1; d1 = d; t1 = t; b1 = b; } else if (d < d2) d2 = d;
                }
                if (b1 >= 0 && t1 >= 0.2f && t1 <= 0.8f && d2 >= 1.5f * d1) refBone[v] = b1;
            }
            var pts = Enumerable.Range(0, bind.Length).Where(v => refBone[v] >= 0).ToArray();
            var testBind = test.BindPos ?? bind;
            var pose = new SkeletonPose(nb); var world = new Matrix4[nb]; var posed = new Vector3[bind.Length];
            var drift = new List<float>(); float worst = 0; string at = "";
            ForEachStandingFrame(model, (c, f) =>
            {
                DsSkeleton.Sample(model, c, f, pose);
                sk.Fk(pose, world, 0f, test.Lengths, test.Offsets);
                Deform(test, testBind, world, posed);
                foreach (int v in pts)
                {
                    var r = Vector3.TransformPosition(bind[v], baseRig.InvBind[refBone[v]] * world[refBone[v]]);
                    float d = (posed[v] - r).Length / height;
                    drift.Add(d); if (d > worst) { worst = d; at = $"{sk.Names[refBone[v]]} {(PlayerAnimation)c} f{f}"; }
                }
            });
            drift.Sort();
            float P(double q) => drift[(int)Math.Clamp(q * (drift.Count - 1), 0, drift.Count - 1)] * 100f;
            return $"LIMB FOLLOW ({pts.Length} mid-limb points): drift %height p50={P(0.5):0.00} p99={P(0.99):0.00} p99.9={P(0.999):0.00} max={worst * 100:0.00} ({at})";
        }

        // How far a rig moves each point away from where a REFERENCE rig (the one the owner has watched) puts it, over
        // every standing frame, % of height: a piece moving with the wrong bone shows up here even when it doesn't stretch.
        static string DiffReport(Model model, DsSkeleton sk, TrophyRig test, TrophyRig? reference, Vector3[] bind, float height)
        {
            if (reference == null) return "DIFF vs reference: (no reference rig)";
            var pose = new SkeletonPose(sk.Count); var world = new Matrix4[sk.Count];
            var a = new Vector3[bind.Length]; var b = new Vector3[bind.Length];
            var diffs = new List<float>(); float worst = 0; string at = "";
            ForEachStandingFrame(model, (c, f) =>
            {
                DsSkeleton.Sample(model, c, f, pose);
                sk.Fk(pose, world, 0f, test.Lengths, test.Offsets);
                Deform(test, test.BindPos ?? bind, world, a);
                Deform(reference, reference.BindPos ?? bind, world, b);
                for (int v = 0; v < bind.Length; v++)
                {
                    float d = (a[v] - b[v]).Length / height; diffs.Add(d);
                    if (d > worst) { worst = d; at = $"{(PlayerAnimation)c} f{f}"; }
                }
            });
            // which parts moved to another bone: among points that ever drift > 5% of height, main bone reference -> test
            var vmax = new float[bind.Length];
            for (int i = 0; i < diffs.Count; i++) { int v = i % bind.Length; if (diffs[i] > vmax[v]) vmax[v] = diffs[i]; }
            var moved = Enumerable.Range(0, bind.Length).Where(v => vmax[v] > 0.05f)
                .GroupBy(v => $"{sk.Names[reference.Bones[v * 4]]}->{sk.Names[test.Bones[v * 4]]}")
                .OrderByDescending(g => g.Count()).Take(8).Select(g => $"{g.Key}={g.Count()}(max {g.Max(v => vmax[v]) * 100:0.0})");
            diffs.Sort();
            float P(double q) => diffs[(int)Math.Clamp(q * (diffs.Count - 1), 0, diffs.Count - 1)] * 100f;
            return $"DIFF vs the watched NEW rig, %height: p50={P(0.5):0.00} p99={P(0.99):0.00} p99.9={P(0.999):0.00} max={worst * 100:0.00} ({at}); " +
                   $"points drifting >5%: {vmax.Count(x => x > 0.05f)} -- by main bone NEW->this: " + string.Join(" ", moved);
        }

        static void ForEachStandingFrame(Model model, Action<int, int> body)
        {
            for (int c = 0; c < model.AnimationGroups.Node.Count; c++)
            {
                if (c is (int)PlayerAnimation.Morph or (int)PlayerAnimation.Unmorph) continue;
                for (int f = 0; f < model.AnimationGroups.Node[c].FrameCount; f++) body(c, f);
            }
        }

        // The same measurement on the game's own DS mesh (rigid one-bone-per-vertex skinning, as the
        // DS draws it) -- the reference for how much edge stretch the vanilla game itself shows.
        static void DsBaseline(Model model, DsSkeleton sk)
        {
            var a = new BipedAnimator(model);
            var pal = new float[16 * 32];
            a.SetBoth(PlayerAnimation.Idle, AnimFlags.None);
            HunterRig.Pose(a, 0f, pal);
            var baked = MphRecomp.Render.GeometryBaker.Bake(model, new float[3], out _, useAnimation: true, useMatrixStack: true, skinned: true);
            var key = new Dictionary<(float, float, float, int), int>();
            var local = new List<(Vector3 P, int S)>();
            var edges = new HashSet<(int, int)>();
            foreach (var b in baked)
            {
                var idx = new List<int>();
                for (int i = 0; i + 11 < b.Verts.Count; i += 12)
                {
                    var k = (b.Verts[i], b.Verts[i + 1], b.Verts[i + 2], (int)b.Verts[i + 11]);
                    if (!key.TryGetValue(k, out int id)) { id = local.Count; key[k] = id; local.Add((new Vector3(k.Item1, k.Item2, k.Item3), k.Item4)); }
                    idx.Add(id);
                }
                for (int t = 0; t + 2 < idx.Count; t += 3)
                {
                    void E(int x, int y) { if (x != y) edges.Add(x < y ? (x, y) : (y, x)); }
                    E(idx[t], idx[t + 1]); E(idx[t + 1], idx[t + 2]); E(idx[t], idx[t + 2]);
                }
            }
            Vector3[] PoseAll(int c, int f)
            {
                a.Legs.SetAnimation(c); a.Legs.AnimInfo.Frame[0] = f; a.Torso.SetAnimation(c); a.Torso.AnimInfo.Frame[0] = f;
                HunterRig.Pose(a, 0f, pal);
                return local.Select(v => Vector3.TransformPosition(v.P, new Matrix4(
                    pal[v.S * 16], pal[v.S * 16 + 1], pal[v.S * 16 + 2], pal[v.S * 16 + 3], pal[v.S * 16 + 4], pal[v.S * 16 + 5], pal[v.S * 16 + 6], pal[v.S * 16 + 7],
                    pal[v.S * 16 + 8], pal[v.S * 16 + 9], pal[v.S * 16 + 10], pal[v.S * 16 + 11], pal[v.S * 16 + 12], pal[v.S * 16 + 13], pal[v.S * 16 + 14], pal[v.S * 16 + 15])) * model.Scale.X).ToArray();
            }
            var bind = PoseAll((int)PlayerAnimation.Idle, 0);
            float height = bind.Max(p => p.Y) - bind.Min(p => p.Y);
            var st = new StretchStats(edges.ToArray(), bind, height);
            ForEachStandingFrame(model, (c, f) => st.Add(PoseAll(c, f), $"{(PlayerAnimation)c} f{f}"));
            Console.WriteLine("  DS MESH BASELINE (idle-relative) " + st.Report());
        }

        // Edge-length change statistics: relative (only edges >= 1% of body height, so sub-millimetre
        // slivers can't dominate) and absolute (length change as a fraction of body height, all edges).
        sealed class StretchStats
        {
            readonly (int, int)[] _edges; readonly Vector3[] _bind; readonly float _height;
            readonly float[] _l0; readonly float[] _worstAbs;
            readonly List<float> _rel = new(), _abs = new();
            float _worstRel = 1f; string _worstRelAt = "";
            public StretchStats((int, int)[] edges, Vector3[] bind, float height)
            {
                _edges = edges; _bind = bind; _height = height;
                _l0 = edges.Select(e => (bind[e.Item1] - bind[e.Item2]).Length).ToArray();
                _worstAbs = new float[edges.Length];
            }
            public void Add(Vector3[] posed, string at)
            {
                for (int i = 0; i < _edges.Length; i++)
                {
                    var (x, y) = _edges[i];
                    float l = (posed[x] - posed[y]).Length;
                    float abs = MathF.Abs(l - _l0[i]) / _height;
                    _abs.Add(abs);
                    if (abs > _worstAbs[i]) _worstAbs[i] = abs;
                    if (_l0[i] >= 0.01f * _height)
                    {
                        float r = l / _l0[i];
                        _rel.Add(r);
                        float dev = MathF.Max(r, 1f / MathF.Max(r, 1e-4f));
                        if (dev > _worstRel) { _worstRel = dev; _worstRelAt = at; }
                    }
                }
            }
            // per vertex: the worst length change (% of height) of any edge touching it, over every frame seen
            public float[] VertexHeat(int nv)
            {
                var h = new float[nv];
                for (int i = 0; i < _edges.Length; i++) { var (x, y) = _edges[i]; float a = _worstAbs[i] * 100f; if (a > h[x]) h[x] = a; if (a > h[y]) h[y] = a; }
                return h;
            }
            public IEnumerable<(int, int)> WorstEdges(double fraction)
            {
                int k = Math.Max(1, (int)(_edges.Length * fraction));
                return Enumerable.Range(0, _edges.Length).OrderByDescending(i => _worstAbs[i]).Take(k).Select(i => _edges[i]);
            }
            public string Report()
            {
                _rel.Sort(); _abs.Sort();
                float R(double q) => _rel[(int)Math.Clamp(q * (_rel.Count - 1), 0, _rel.Count - 1)];
                float A(double q) => _abs[(int)Math.Clamp(q * (_abs.Count - 1), 0, _abs.Count - 1)];
                return $"{_edges.Length} edges: stretch ratio (edges>=1% height) p1={R(0.01):0.00} p50={R(0.5):0.00} p99={R(0.99):0.00} p99.9={R(0.999):0.00} worst x{_worstRel:0.0} ({_worstRelAt}); " +
                       $"abs length change %height p99={A(0.99) * 100:0.00} p99.9={A(0.999) * 100:0.00} max={A(1.0) * 100:0.00}";
            }
        }

        static System.Numerics.Vector3 ToN(Vector3 v) => new(v.X, v.Y, v.Z);

        static void Dump(string path, Vector3[] pts, TrophyRig rig, int[]? _) =>
            File.WriteAllLines(path, pts.Select((p, i) => string.Create(CultureInfo.InvariantCulture, $"{p.X} {p.Y} {p.Z} {rig.Bones[i * 4]}")));
    }
}
