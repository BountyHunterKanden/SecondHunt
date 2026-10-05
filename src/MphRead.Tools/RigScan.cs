using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MphRead.Entities;
using MphRecomp.Anim;
using MphRecomp.Render;
using OpenTK.Mathematics;

namespace MphRead
{
    // How far each rigged trophy's head and limbs sit from the game model's own, in the game's Idle pose.
    // Run: MphRead.Tools.dll -rigscan [Trophy...] [--rig clean|straight|tpose|hinge|default|before] [--meshes]   (all 14 when none named)
    //
    // Both models are posed by the SAME clip on the same skeleton, so any difference is the rig's doing:
    //   LOOK -- where the head looks: head piece centre -> visor/eye centre, on the rigged trophy, on the trophy as
    //           sculpted, and on the game model (only where both have a visor/eye mesh; a helmet's round shape alone
    //           can't tell pitch -- a shape fit tried first said Barriersuit looked UP when it plainly looks down).
    //   bone -- neck, upper arms, forearms, thighs, shins: angle between the bone (joint to child joint) on the rig and
    //           on the game model: where a limb is placed.
    //   axis -- the limbs' pieces: angle between the long axis of the trophy piece and of the game's piece (a tube's
    //           roll can't be told, its axis can) -- e.g. the arm cannon pointing another way. Not meaningful for
    //           pads / non-tube pieces (Weavel's shoulders).
    // --meshes lists every mesh of both models with the bones carrying it (to find visors and eyes).
    internal static class RigScan
    {
        static readonly string[] All = { "Barriersuit", "Samus", "SamusR1", "Gravitysuit", "Normalsuit", "Darksuit", "Darksamus",
            "SzerosuitR1", "Kanden", "Sylux", "Weavel", "Trace", "Noxus", "Spire" };
        static readonly (string From, string To)[] BoneChains = { ("Spine_2", "Head_1"), ("L_shoulder", "L_elbow"), ("L_elbow", "L_wrist"),
            ("R_shoulder", "R_elbow"), ("R_elbow", "R_wrist"), ("L_hip", "L_knee"), ("L_knee", "L_ankle"), ("R_hip", "R_knee"), ("R_knee", "R_ankle") };
        static readonly string[] Limbs = { "L_shoulder", "L_elbow", "R_shoulder", "R_elbow", "L_hip", "L_knee", "R_hip", "R_knee" };

        public static void Run(string[] args)
        {
            if (!ToolPaths.UseDesktopExtraction()) return;
            string repo = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
            string conv = Path.Combine(repo, "brawl_extract", "Converted");
            int ri = Array.IndexOf(args, "--rig");
            string key = ri > 0 && ri + 1 < args.Length ? args[ri + 1] : "clean";
            var valued = new[] { "--rig", "--frames", "--out" }.Select(f => Array.IndexOf(args, f)).Where(i => i > 0).Select(i => i + 1).ToHashSet();
            var trophies = args.Skip(1).Where((a, i) => !a.StartsWith("--") && !valued.Contains(i + 1)).ToList();
            if (trophies.Count == 0) trophies = All.ToList();
            // --check: every variant cache next to the trophy still matches its inputs (layout files are part of the key);
            // a stale one would silently vanish from the app's list. Exit code 1 when any is stale.
            if (args.Contains("--check"))
            {
                int stale = 0;
                foreach (string t in trophies)
                {
                    string? d = Directory.Exists(Path.Combine(conv, t)) ? Directory.GetFiles(Path.Combine(conv, t), "*.dae").FirstOrDefault() : null;
                    if (d == null) continue;
                    foreach (var v in TrophyRigIO.Variants)
                    {
                        string vp = TrophyRigIO.VariantPathFor(d, v);
                        if (!File.Exists(vp)) continue;
                        bool ok = TrophyRigIO.Load(vp, TrophyRigIO.VariantKey(d, v)) != null;
                        if (!ok) { stale++; Console.WriteLine($"{t}: '{v.Key}' rig cache is STALE (rebuild: -hdrig {t} --claim 0.5 --savevariants --only {v.Key})"); }
                    }
                }
                Console.WriteLine(stale == 0 ? "all rig caches current" : $"{stale} stale rig caches");
                Environment.Exit(stale == 0 ? 0 : 1);
            }
            Console.WriteLine($"rig '{key}', game Idle pose. facing: + = trophy piece turned DOWN / to its LEFT. bone/axis: degrees apart.");
            foreach (string t in trophies)
            {
                if (!TrophyRigs.TryHunterFor(t, out Hunter h)) { Console.WriteLine($"{t}: unknown trophy"); continue; }
                string? dae = Directory.Exists(Path.Combine(conv, t)) ? Directory.GetFiles(Path.Combine(conv, t), "*.dae").FirstOrDefault() : null;
                if (dae == null) { Console.WriteLine($"{t}: no .dae"); continue; }
                TrophyRig? rig = key == "default"
                    ? TrophyRigIO.Load(Path.ChangeExtension(dae, ".mphrig"), TrophyRigIO.CacheKey(dae))
                    : key == "before" ? TrophyRigIO.Load(TrophyRigIO.BeforePathFor(dae), 0, anyKey: true)
                    : key == "posed" ? TrophyRigIO.LoadPosedRig(dae, new DsSkeleton(Read.GetModelInstance(Metadata.HunterModels[h][0]).Model).Names)
                    : TrophyRigIO.Variants.FirstOrDefault(v => v.Key == key) is { } v ? TrophyRigIO.Load(TrophyRigIO.VariantPathFor(dae, v), TrophyRigIO.VariantKey(dae, v)) : null;
                if (rig == null) { Console.WriteLine($"{t}: no current '{key}' rig cache"); continue; }
                Model model = Read.GetModelInstance(Metadata.HunterModels[h][0]).Model;
                if (!args.Contains("--nosettle")) TrophyFeet.Settle(rig, model, new DsSkeleton(model), args.Contains("--frames") ? Console.WriteLine : null);   // as the device plays it
                var daeModel = MphRecomp.Assets.DaeModel.Load(dae);
                if (args.Contains("--meshes")) { Meshes(t, model, rig, daeModel); continue; }
                if (Array.IndexOf(args, "--frames") is int fi && fi > 0 && fi + 1 < args.Length)
                {
                    int oi = Array.IndexOf(args, "--out");
                    Frames(t, model, rig, daeModel.Meshes.SelectMany(m => m.Positions).Select(p => new Vector3(p.X, p.Y, p.Z)).ToList(),
                        args[fi + 1], oi > 0 ? args[oi + 1] : Path.Combine(Path.GetTempPath(), "mph_rigframes"));
                    continue;
                }
                if (args.Contains("--bind"))
                {
                    int oi = Array.IndexOf(args, "--out");
                    DumpBind(t, model, rig, daeModel.Meshes.SelectMany(m => m.Positions).Select(p => new Vector3(p.X, p.Y, p.Z)).ToList(),
                        oi > 0 ? args[oi + 1] : Path.Combine(Path.GetTempPath(), "mph_rigbind"), key);
                    continue;
                }
                if (args.Contains("--stretch"))
                {
                    Stretch(t, model, rig, daeModel.Meshes.SelectMany(m => m.Positions).Select(p => new Vector3(p.X, p.Y, p.Z)).ToList());
                    continue;
                }
                Scan(t, model, rig, daeModel);
            }
        }

        // meshes that mark where a head LOOKS: the trophy's visor / eyes (not the face: it sits deep inside the helmet), and the game model's glowing visor or eyes
        static readonly System.Text.RegularExpressions.Regex TrophyFace = new("visor|eye", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        static readonly System.Text.RegularExpressions.Regex GameFace = new("full_bright|eye", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        static void Scan(string trophy, Model model, TrophyRig rig, MphRecomp.Assets.DaeModel dae)
        {
            var sk = new DsSkeleton(model);
            int nb = sk.Count;
            var pose = new SkeletonPose(nb);
            var trW = new Matrix4[nb]; var dsW = new Matrix4[nb];
            DsSkeleton.Sample(model, (int)PlayerAnimation.Idle, 0, pose);
            sk.Fk(pose, trW, 0f, rig.Lengths, rig.Offsets);
            sk.Fk(pose, dsW);
            var ds = GamePieces(model, dsW);
            var bind = rig.BindPos ?? rig.Vertices.Select(p => Vector3.TransformPosition(p, rig.TrophyToRig)).ToArray();
            List<Vector3> Piece(int b)
            {
                var res = new List<Vector3>();
                for (int v = 0; v < bind.Length; v++)
                    if (rig.Bones[v * 4] == b && rig.Weights[v * 4] >= 0.9f && (rig.Hidden.Length == 0 || !rig.Hidden[v]))
                        res.Add(Vector3.TransformPosition(bind[v], rig.InvBind[b] * trW[b]));
                return res;
            }

            var line = new List<string>();
            // look direction: head piece centre -> face/visor centre, on each model, both posed by the head bone
            int hb = sk.IndexOf("Head_1");
            if (hb >= 0)
            {
                var headSet = new HashSet<int>();
                for (int v = 0; v < bind.Length; v++)
                    if (rig.Bones[v * 4] == hb && rig.Weights[v * 4] >= 0.9f && (rig.Hidden.Length == 0 || !rig.Hidden[v])) headSet.Add(v);
                var faceV = dae.Meshes.Where(m => TrophyFace.IsMatch(m.Material) || TrophyFace.IsMatch(Path.GetFileName(m.TextureFile ?? "")))
                    .SelectMany(m => m.Positions).Select(p => rig.IndexOf(p)).Where(headSet.Contains).Distinct().ToList();
                var gameFace = GameMeshOnBone(model, dsW, hb, name => GameFace.IsMatch(name));
                if (faceV.Count >= 3 && gameFace.Count >= 3 && headSet.Count >= 30)
                {
                    Vector3 Posed(int v) => Vector3.TransformPosition(bind[v], rig.InvBind[hb] * trW[hb]);
                    var lt = Centroid(faceV.Select(Posed).ToList()) - Centroid(headSet.Select(Posed).ToList());
                    var lg = Centroid(gameFace) - Centroid(ds[hb]);
                    float Down(Vector3 d) => -MathF.Asin(d.Normalized().Y) * 180f / MathF.PI;
                    float Left(Vector3 d) => MathF.Atan2(-d.X, -d.Z) * 180f / MathF.PI;
                    // the same measure on the trophy as sculpted (its own pose, turned to face the game's way)
                    Vector3 Sculpt(int v) => Vector3.TransformPosition(rig.Vertices[v], rig.TrophyToRig);
                    var ls = Centroid(faceV.Select(Sculpt).ToList()) - Centroid(headSet.Select(Sculpt).ToList());
                    line.Add($"LOOK rigged {Down(lt),4:+0;-0;0} down {Left(lt),4:+0;-0;0} left | as sculpted {Down(ls),4:+0;-0;0} down {Left(ls),4:+0;-0;0} left" +
                        $" | game {Down(lg),4:+0;-0;0} down {Left(lg),4:+0;-0;0} left  => rig tipped the head {Down(lt) - Down(ls),4:+0;-0;0} down, {Left(lt) - Left(ls),4:+0;-0;0} left" +
                        $"  ({faceV.Count} visor verts, {gameFace.Count} game)");
                }
                else line.Add($"LOOK n/a (face verts {faceV.Count}, game visor/eye verts {gameFace.Count})");
            }
            // how the rig turned each piece away from the sculpt: the same vertices, as sculpted vs posed into Idle by their
            // bone -- known correspondences, so an exact rigid fit (Horn), no shape guessing
            foreach (string name in new[] { "Head_1", "Spine_2" })
            {
                int b = sk.IndexOf(name);
                if (b < 0) continue;
                var pairs = new List<(Vector3, Vector3)>();
                for (int v = 0; v < bind.Length; v++)
                    if (rig.Bones[v * 4] == b && rig.Weights[v * 4] >= 0.9f && (rig.Hidden.Length == 0 || !rig.Hidden[v]))
                        pairs.Add((Vector3.TransformPosition(rig.Vertices[v], rig.TrophyToRig), Vector3.TransformPosition(bind[v], rig.InvBind[b] * trW[b])));
                if (pairs.Count < 30) continue;
                var cs = Centroid(pairs.Select(p => p.Item1).ToList()); var ci = Centroid(pairs.Select(p => p.Item2).ToList());
                var q = TrophyRigger.Horn(pairs.Select(p => (p.Item1 - cs, p.Item2 - ci)).ToList());
                var f = Vector3.Transform(-Vector3.UnitZ, q);   // the sculpt's forward, after the rig's turn
                float down = -MathF.Asin(Math.Clamp(f.Y, -1f, 1f)) * 180f / MathF.PI, left = MathF.Atan2(-f.X, -f.Z) * 180f / MathF.PI;
                line.Add($"{Short(name)} turned from sculpt {down,3:+0;-0;0} down {left,3:+0;-0;0} left");
            }
            // where the head TURNS: its joint inside the head piece, as a fraction of the piece's height above its bottom
            // (0 = at the chin/neck line, 1 = at the crown) and of its depth from its back (0) to its front (1), trophy
            // vs game model. A pivot high in the helmet swings the helmet's rim across the collar instead of rolling it.
            if (hb >= 0)
            {
                var tro = Piece(hb);
                if (tro.Count >= 30 && ds[hb].Count >= 12)
                {
                    string Where(IReadOnlyList<Vector3> p, Vector3 j)
                    {
                        float y0 = p.Min(q => q.Y), y1 = p.Max(q => q.Y), z0 = p.Min(q => q.Z), z1 = p.Max(q => q.Z);
                        // the game faces -Z: front = min z
                        return $"{(j.Y - y0) / (y1 - y0):0.00} up, {(z1 - j.Z) / (z1 - z0):0.00} forward";
                    }
                    line.Add($"HEAD PIVOT trophy {Where(tro, trW[hb].ExtractTranslation())} | game {Where(ds[hb], dsW[hb].ExtractTranslation())}");
                }
            }
            var bones = new List<string>();
            foreach (var (from, to) in BoneChains)
            {
                int a = sk.IndexOf(from), c = sk.IndexOf(to);
                if (a < 0 || c < 0) continue;
                var vt = trW[c].ExtractTranslation() - trW[a].ExtractTranslation();
                var vd = dsW[c].ExtractTranslation() - dsW[a].ExtractTranslation();
                if (vt.Length < 1e-4f || vd.Length < 1e-4f) continue;
                bones.Add($"{Short(from)}>{Short(to)} {Angle(vt, vd),3:0}");
            }
            var axes = new List<string>();
            foreach (string name in Limbs)
            {
                int b = sk.IndexOf(name);
                if (b < 0) continue;
                var tro = Piece(b);
                if (tro.Count < 30 || ds[b].Count < 12) continue;
                float d = Angle(MajorAxis(tro), MajorAxis(ds[b]));
                axes.Add($"{Short(name)} {MathF.Min(d, 180f - d),3:0}");   // an axis has no sign
            }
            // the same limb axes through every frame of Shoot (they should hold constant: both ride the same bones)
            var shoot = new List<string>();
            int clip = (int)PlayerAnimation.Shoot, frames = model.AnimationGroups.Node[clip].FrameCount;
            foreach (string name in new[] { "R_shoulder", "R_elbow" })
            {
                int b = sk.IndexOf(name);
                if (b < 0) continue;
                float lo = 999, hi = -1;
                for (int f = 0; f < frames; f++)
                {
                    var p = new SkeletonPose(nb); var tw = new Matrix4[nb]; var dw = new Matrix4[nb];
                    DsSkeleton.Sample(model, clip, f, p);
                    sk.Fk(p, tw, 0f, rig.Lengths, rig.Offsets); sk.Fk(p, dw);
                    var tro = new List<Vector3>();
                    for (int v = 0; v < bind.Length; v++)
                        if (rig.Bones[v * 4] == b && rig.Weights[v * 4] >= 0.9f && (rig.Hidden.Length == 0 || !rig.Hidden[v]))
                            tro.Add(Vector3.TransformPosition(bind[v], rig.InvBind[b] * tw[b]));
                    var game = ds[b].Select(x => Vector3.TransformPosition(x, Matrix4.Invert(dsW[b]) * dw[b])).ToList();
                    if (tro.Count < 30 || game.Count < 12) break;
                    float d = Angle(MajorAxis(tro), MajorAxis(game)); d = MathF.Min(d, 180f - d);
                    lo = MathF.Min(lo, d); hi = MathF.Max(hi, d);
                }
                if (hi >= 0) shoot.Add($"{Short(name)} {lo:0}-{hi:0}");
            }
            // how the game's shoulder pads move: over every frame of every clip, the pad bone's turn away from the chest and
            // away from the upper arm (degrees, 95th percentile) -- does a pad ride the chest, the arm, or between?
            foreach (string side in new[] { "L", "R" })
            {
                int pad = sk.IndexOf(side + "_varias2_SDK"), arm = sk.IndexOf(side + "_shoulder"), ch = sk.IndexOf("Spine_2");
                if (pad < 0 || arm < 0 || ch < 0) continue;
                var toChest = new List<float>(); var toArm = new List<float>(); var armToChest = new List<float>();
                float Turn(Matrix4 a, Matrix4 b)
                {
                    var q = (a.ClearTranslation() * Matrix4.Invert(b.ClearTranslation())).ExtractRotation().Normalized();
                    return 2f * MathF.Acos(Math.Clamp(MathF.Abs(q.W), 0f, 1f)) * 180f / MathF.PI;
                }
                var p0 = new SkeletonPose(nb); var w0 = new Matrix4[nb];
                DsSkeleton.Sample(model, (int)PlayerAnimation.Idle, 0, p0); sk.Fk(p0, w0);
                for (int c = 0; c < model.AnimationGroups.Node.Count; c++)
                {
                    if (c is (int)PlayerAnimation.Morph or (int)PlayerAnimation.Unmorph || model.AnimationGroups.Node[c].Count == 0) continue;
                    for (int f = 0; f < model.AnimationGroups.Node[c].FrameCount; f++)
                    {
                        var p = new SkeletonPose(nb); var dw = new Matrix4[nb];
                        DsSkeleton.Sample(model, c, f, p); sk.Fk(p, dw);
                        // each bone's turn since Idle, relative to the chest / the arm
                        Matrix4 D(int b) => Matrix4.Invert(w0[b].ClearTranslation()) * dw[b].ClearTranslation();
                        toChest.Add(Turn(D(pad), D(ch))); toArm.Add(Turn(D(pad), D(arm))); armToChest.Add(Turn(D(arm), D(ch)));
                    }
                }
                float P95(List<float> x) { x.Sort(); return x[(int)(0.95 * (x.Count - 1))]; }
                line.Add($"{side} PAD vs chest {P95(toChest):0} deg, vs arm {P95(toArm):0} (arm vs chest {P95(armToChest):0})");
            }
            int offs = rig.Offsets.Count(o => o.HasValue);
            Console.WriteLine($"=== {trophy}   ({offs} bones with the trophy's own joint offsets)");
            Console.WriteLine("  " + string.Join("   ", line));
            Console.WriteLine("  bone: " + string.Join("  ", bones));
            Console.WriteLine("  axis: " + string.Join("  ", axes) + "   | through Shoot: " + string.Join("  ", shoot));
        }

        // --frames Flourish:0,12,24 --out <dir>: both models posed in those frames, triangle by triangle, to draw side by
        // side (scratchpad frames_plot.py). One line per triangle: 9 coordinates, the bone of each corner (the game
        // model: its one bone; the rig: its main bone, -1 when blended), and the triangle's WARP = the largest change of an
        // edge's length relative to that edge in the model's own standing Idle f0 (0 = moved rigidly).
        // Files: <T>_<clip>_<f>_game.txt / _rig.txt, same space (the game's model space, trophy scaled to it).
        static void Frames(string trophy, Model model, TrophyRig rig, IReadOnlyList<Vector3> corners, string spec, string outDir)
        {
            Directory.CreateDirectory(outDir);
            var sk = new DsSkeleton(model);
            int nb = sk.Count;
            string[] cs = spec.Split(':');
            // "Rest": the skeleton with every rotation zero (the clips' bone offsets, nothing turned) -- the pose the
            // model's own parts are built around
            bool rest = cs[0].Equals("Rest", StringComparison.OrdinalIgnoreCase);
            int clip = rest ? (int)PlayerAnimation.Idle : (int)Enum.Parse<PlayerAnimation>(cs[0], ignoreCase: true);
            var frames = cs.Length > 1 ? cs[1].Split(',').Select(int.Parse).ToList() : Enumerable.Range(0, model.AnimationGroups.Node[clip].FrameCount).ToList();
            var pose = new SkeletonPose(nb); var w = new Matrix4[nb];
            var baked = GeometryBaker.Bake(model, new float[3], out _, useAnimation: false, useMatrixStack: true, skinned: true);
            var gp = new List<Vector3>(); var gb = new List<int>();
            foreach (var b in baked)
                for (int k = 0; k + GeometryBaker.StrideSkinned <= b.Verts.Count; k += GeometryBaker.StrideSkinned)
                { gp.Add(new Vector3(b.Verts[k], b.Verts[k + 1], b.Verts[k + 2])); gb.Add(model.NodeMatrixIds[(int)b.Verts[k + 11]]); }
            Vector3[] Game(int c, int f)
            {
                DsSkeleton.Sample(model, c, f, pose);
                if (rest && c == clip) for (int i = 0; i < nb; i++) if (sk.Names[i] is not ("Dummy_Root" or "Skeleton_Root")) pose.R[i] = Vector3.Zero;
                sk.Fk(pose, w);
                return gp.Select((p, i) => Vector3.TransformPosition(p, w[gb[i]])).ToArray();
            }
            var bind = rig.BindPos ?? rig.Vertices.Select(p => Vector3.TransformPosition(p, rig.TrophyToRig)).ToArray();
            var tris = new List<int>();
            for (int i = 0; i + 2 < corners.Count; i += 3)
            {
                int a = rig.IndexOf(new System.Numerics.Vector3(corners[i].X, corners[i].Y, corners[i].Z));
                int b = rig.IndexOf(new System.Numerics.Vector3(corners[i + 1].X, corners[i + 1].Y, corners[i + 1].Z));
                int c = rig.IndexOf(new System.Numerics.Vector3(corners[i + 2].X, corners[i + 2].Y, corners[i + 2].Z));
                if (a < 0 || b < 0 || c < 0 || rig.HidesTriangle(a, b, c)) continue;
                tris.Add(a); tris.Add(b); tris.Add(c);
            }
            Vector3[] Rig(int c, int f)
            {
                DsSkeleton.Sample(model, c, f, pose); sk.Fk(pose, w, 0f, rig.Lengths, rig.Offsets);
                var o = new Vector3[bind.Length]; HdRigTool.Deform(rig, bind, w, o); return o;
            }
            int MainBone(int v) => rig.Weights[v * 4] >= 0.999f ? rig.Bones[v * 4] : -1;
            static float Warp(Vector3 a, Vector3 b, Vector3 c, Vector3 a0, Vector3 b0, Vector3 c0)
            {
                float E(Vector3 p, Vector3 q, Vector3 p0, Vector3 q0) { float l0 = (p0 - q0).Length; return l0 < 1e-6f ? 0f : MathF.Abs((p - q).Length / l0 - 1f); }
                return MathF.Max(E(a, b, a0, b0), MathF.Max(E(b, c, b0, c0), E(c, a, c0, a0)));
            }
            var ci = System.Globalization.CultureInfo.InvariantCulture;
            var g0 = Game((int)PlayerAnimation.Idle, 0); var r0 = Rig((int)PlayerAnimation.Idle, 0);
            foreach (int f in frames)
            {
                var g = Game(clip, f); var r = Rig(clip, f);
                var gl = new List<string>(gp.Count / 3);
                for (int t = 0; t + 2 < gp.Count; t += 3)
                    gl.Add(string.Create(ci, $"{g[t].X} {g[t].Y} {g[t].Z} {g[t + 1].X} {g[t + 1].Y} {g[t + 1].Z} {g[t + 2].X} {g[t + 2].Y} {g[t + 2].Z} {gb[t]} {gb[t + 1]} {gb[t + 2]} {Warp(g[t], g[t + 1], g[t + 2], g0[t], g0[t + 1], g0[t + 2]):0.####}"));
                File.WriteAllLines(Path.Combine(outDir, $"{trophy}_{(rest ? "Rest" : ((PlayerAnimation)clip).ToString())}_{f}_game.txt"), gl);
                var rl = new List<string>(tris.Count / 3);
                for (int t = 0; t + 2 < tris.Count; t += 3)
                {
                    int a = tris[t], b = tris[t + 1], c = tris[t + 2];
                    rl.Add(string.Create(ci, $"{r[a].X} {r[a].Y} {r[a].Z} {r[b].X} {r[b].Y} {r[b].Z} {r[c].X} {r[c].Y} {r[c].Z} {MainBone(a)} {MainBone(b)} {MainBone(c)} {Warp(r[a], r[b], r[c], r0[a], r0[b], r0[c]):0.####}"));
                }
                File.WriteAllLines(Path.Combine(outDir, $"{trophy}_{(rest ? "Rest" : ((PlayerAnimation)clip).ToString())}_{f}_rig.txt"), rl);
                // joints of both skeletons in this frame: "name gx gy gz rx ry rz" (game's own, then the rig's -- its lengths/offsets)
                var gw = new Matrix4[nb]; DsSkeleton.Sample(model, clip, f, pose);
                if (rest) for (int i = 0; i < nb; i++) if (sk.Names[i] is not ("Dummy_Root" or "Skeleton_Root")) pose.R[i] = Vector3.Zero;
                sk.Fk(pose, gw);
                var rw2 = new Matrix4[nb]; DsSkeleton.Sample(model, clip, f, pose); sk.Fk(pose, rw2, 0f, rig.Lengths, rig.Offsets);
                File.WriteAllLines(Path.Combine(outDir, $"{trophy}_{(rest ? "Rest" : ((PlayerAnimation)clip).ToString())}_{f}_joints.txt"),
                    Enumerable.Range(0, nb).Select(i => { var a = gw[i].ExtractTranslation(); var b = rw2[i].ExtractTranslation();
                        return string.Create(ci, $"{sk.Names[i]} {a.X} {a.Y} {a.Z} {b.X} {b.Y} {b.Z}"); }));
            }
            File.WriteAllLines(Path.Combine(outDir, $"{trophy}_bone_names.txt"), sk.Names.Select((n, i) => $"{i} {n}"));
            Console.WriteLine($"{trophy}: {frames.Count} frames of {(PlayerAnimation)clip} -> {outDir}");
        }

        // --bind --out <dir>: the rig's bind pose as data (for transferring a hand-made rig's layout onto the trophy):
        // <T>_<rig>_verts.txt one line per rig vertex "x y z bone weight" (bind position in rig space, main bone + its weight),
        // <T>_<rig>_joints.txt "name x y z" per bone (bind pose), <T>_tris.txt triangles as rig vertex indices (drawn ones).
        static void DumpBind(string trophy, Model model, TrophyRig rig, IReadOnlyList<Vector3> corners, string outDir, string key)
        {
            Directory.CreateDirectory(outDir);
            var sk = new DsSkeleton(model);
            var ci = System.Globalization.CultureInfo.InvariantCulture;
            var bind = rig.BindPos ?? rig.Vertices.Select(p => Vector3.TransformPosition(p, rig.TrophyToRig)).ToArray();
            File.WriteAllLines(Path.Combine(outDir, $"{trophy}_{key}_verts.txt"), bind.Select((p, v) =>
                string.Create(ci, $"{p.X} {p.Y} {p.Z} {rig.Bones[v * 4]} {rig.Weights[v * 4]:0.###}")));
            File.WriteAllLines(Path.Combine(outDir, $"{trophy}_{key}_joints.txt"), Enumerable.Range(0, sk.Count).Select(i =>
            { var t = rig.BindWorld[i].ExtractTranslation(); return string.Create(ci, $"{sk.Names[i]} {t.X} {t.Y} {t.Z}"); }));
            var tris = new List<string>();
            for (int i = 0; i + 2 < corners.Count; i += 3)
            {
                int a = rig.IndexOf(new System.Numerics.Vector3(corners[i].X, corners[i].Y, corners[i].Z));
                int b = rig.IndexOf(new System.Numerics.Vector3(corners[i + 1].X, corners[i + 1].Y, corners[i + 1].Z));
                int c = rig.IndexOf(new System.Numerics.Vector3(corners[i + 2].X, corners[i + 2].Y, corners[i + 2].Z));
                if (a < 0 || b < 0 || c < 0 || rig.HidesTriangle(a, b, c)) continue;
                tris.Add($"{a} {b} {c}");
            }
            File.WriteAllLines(Path.Combine(outDir, $"{trophy}_tris.txt"), tris);
            // the same vertices as sculpted, in the trophy's own coordinates (to match the .dae's own pieces to rig vertices)
            File.WriteAllLines(Path.Combine(outDir, $"{trophy}_sculpt.txt"), rig.Vertices.Select(p => string.Create(ci, $"{p.X} {p.Y} {p.Z}")));
            Console.WriteLine($"{trophy}: '{key}' bind pose, {bind.Length} vertices, {tris.Count} triangles -> {outDir}");
        }

        // --stretch: where each model stretches, over every frame of every clip (not the morph-ball ones), measured
        // against its own standing Idle f0 as how much an edge's length changes, in % of the model's height:
        //   inside a piece -- both ends on ONE bone alone (the game's models: always 0, every vertex follows one bone)
        //   at seams       -- everything else (edges across or near a joint)
        // The game model's own seams are the yardstick for how much a joint may stretch.
        static void Stretch(string trophy, Model model, TrophyRig rig, IReadOnlyList<Vector3> corners)
        {
            var sk = new DsSkeleton(model);
            int nb = sk.Count, nv = rig.Vertices.Length;
            var clips = Enumerable.Range(0, model.AnimationGroups.Node.Count)
                .Where(c => c != (int)PlayerAnimation.Morph && c != (int)PlayerAnimation.Unmorph && model.AnimationGroups.Node[c].Count > 0).ToList();
            var pose = new SkeletonPose(nb); var w = new Matrix4[nb];

            // the game model: triangle corners, bone-local, with their bone
            var baked = GeometryBaker.Bake(model, new float[3], out _, useAnimation: false, useMatrixStack: true, skinned: true);
            var gp = new List<Vector3>(); var gb = new List<int>();
            foreach (var b in baked)
                for (int k = 0; k + GeometryBaker.StrideSkinned <= b.Verts.Count; k += GeometryBaker.StrideSkinned)
                { gp.Add(new Vector3(b.Verts[k], b.Verts[k + 1], b.Verts[k + 2])); gb.Add(model.NodeMatrixIds[(int)b.Verts[k + 11]]); }
            var gEdges = new List<(int, int)>();
            for (int t = 0; t + 2 < gp.Count; t += 3)
                for (int c = 0; c < 3; c++) { int a = t + c, d = t + (c + 1) % 3; if (gb[a] != gb[d]) gEdges.Add((a, d)); }
            Vector3[] GamePosed(int clip, int frame)
            {
                DsSkeleton.Sample(model, clip, frame, pose); sk.Fk(pose, w);
                return gp.Select((p, i) => Vector3.TransformPosition(p, w[gb[i]])).ToArray();
            }
            var gRef = GamePosed((int)PlayerAnimation.Idle, 0);
            float gH = gRef.Max(p => p.Y) - gRef.Min(p => p.Y);
            var gMax = new float[gEdges.Count];
            var gRefLen = gEdges.Select(e => (gRef[e.Item1] - gRef[e.Item2]).Length).ToArray();

            // the rig
            var bind = rig.BindPos ?? rig.Vertices.Select(p => Vector3.TransformPosition(p, rig.TrophyToRig)).ToArray();
            var edges = new HashSet<(int, int)>();
            for (int i = 0; i + 2 < corners.Count; i += 3)
            {
                int[] v = { rig.IndexOf(new System.Numerics.Vector3(corners[i].X, corners[i].Y, corners[i].Z)),
                    rig.IndexOf(new System.Numerics.Vector3(corners[i + 1].X, corners[i + 1].Y, corners[i + 1].Z)),
                    rig.IndexOf(new System.Numerics.Vector3(corners[i + 2].X, corners[i + 2].Y, corners[i + 2].Z)) };
                if (v.Any(x => x < 0) || rig.HidesTriangle(v[0], v[1], v[2])) continue;
                for (int c = 0; c < 3; c++) { int a = v[c], d = v[(c + 1) % 3]; if (a != d) edges.Add(a < d ? (a, d) : (d, a)); }
            }
            var el = edges.ToArray();
            bool Solo(int v) => rig.Weights[v * 4] >= 0.999f;
            var inside = el.Select(e => Solo(e.Item1) && Solo(e.Item2) && rig.Bones[e.Item1 * 4] == rig.Bones[e.Item2 * 4]).ToArray();
            var cur = new Vector3[nv];
            Vector3[] RigPosed(int clip, int frame)
            {
                DsSkeleton.Sample(model, clip, frame, pose); sk.Fk(pose, w, 0f, rig.Lengths, rig.Offsets);
                HdRigTool.Deform(rig, bind, w, cur);
                return cur;
            }
            var rRef = (Vector3[])RigPosed((int)PlayerAnimation.Idle, 0).Clone();
            float rH = rRef.Max(p => p.Y) - rRef.Min(p => p.Y);
            var rRefLen = el.Select(e => (rRef[e.Item1] - rRef[e.Item2]).Length).ToArray();
            var rMax = new float[el.Length]; var rWhere = new string[el.Length];

            foreach (int c in clips)
                for (int f = 0; f < model.AnimationGroups.Node[c].FrameCount; f++)
                {
                    var g = GamePosed(c, f);
                    for (int i = 0; i < gEdges.Count; i++)
                        gMax[i] = MathF.Max(gMax[i], MathF.Abs((g[gEdges[i].Item1] - g[gEdges[i].Item2]).Length - gRefLen[i]) / gH * 100f);
                    var r = RigPosed(c, f);
                    for (int i = 0; i < el.Length; i++)
                    {
                        float d = MathF.Abs((r[el[i].Item1] - r[el[i].Item2]).Length - rRefLen[i]) / rH * 100f;
                        if (d > rMax[i]) { rMax[i] = d; rWhere[i] = $"{(PlayerAnimation)c} f{f}"; }
                    }
                }
            static string Pct(IEnumerable<float> xs)
            {
                var s = xs.OrderBy(x => x).ToArray();
                if (s.Length == 0) return "none";
                float P(double q) => s[Math.Min(s.Length - 1, (int)(q * s.Length))];
                return $"p99 {P(0.99):0.00}  p99.9 {P(0.999):0.00}  max {s[^1]:0.00}";
            }
            var ins = Enumerable.Range(0, el.Length).Where(i => inside[i]).Select(i => rMax[i]).ToList();
            var seam = Enumerable.Range(0, el.Length).Where(i => !inside[i]).ToList();
            Console.WriteLine($"=== {trophy}  (edge length change over every clip vs standing Idle, % of height)");
            Console.WriteLine($"  game model seams:  {gEdges.Count,5} edges  {Pct(gMax)}");
            Console.WriteLine($"  rig inside pieces: {ins.Count,5} edges  {Pct(ins)}   (> 0.5%: {ins.Count(x => x > 0.5f)})");
            Console.WriteLine($"  rig at seams:      {seam.Count,5} edges  {Pct(seam.Select(i => rMax[i]))}   (> game max {gMax.Max():0.0}: {seam.Count(i => rMax[i] > gMax.Max())})");
            var worst = seam.Where(i => rMax[i] > gMax.Max())
                .GroupBy(i => string.Join("|", new[] { sk.Names[rig.Bones[el[i].Item1 * 4]], sk.Names[rig.Bones[el[i].Item2 * 4]] }.OrderBy(x => x)))
                .OrderByDescending(g => g.Count()).Take(6).Select(g => $"{g.Key}={g.Count()} (max {g.Max(i => rMax[i]):0.0}, {rWhere[g.OrderByDescending(i => rMax[i]).First()]})");
            Console.WriteLine("  seams past the game's max, by bones: " + string.Join("  ", worst));
        }

        // --meshes: every mesh of both models with the bones carrying it -- to find the visor / eyes on each
        static void Meshes(string trophy, Model model, TrophyRig rig, MphRecomp.Assets.DaeModel dae)
        {
            var sk = new DsSkeleton(model);
            Console.WriteLine($"=== {trophy}: game model {model.Name}");
            var visible = model.Meshes.Select(m => m.Visible).ToArray();
            for (int i = 0; i < model.Meshes.Count; i++)
            {
                for (int j = 0; j < model.Meshes.Count; j++) model.Meshes[j].Visible = j == i;
                var baked = GeometryBaker.Bake(model, new float[3], out _, useAnimation: false, useMatrixStack: true, skinned: true);
                var byBone = new Dictionary<int, int>();
                foreach (var b in baked)
                    for (int k = 0; k + GeometryBaker.StrideSkinned <= b.Verts.Count; k += GeometryBaker.StrideSkinned)
                    {
                        int node = model.NodeMatrixIds[(int)b.Verts[k + 11]];
                        byBone[node] = byBone.GetValueOrDefault(node) + 1;
                    }
                Console.WriteLine($"  game [{i}] {model.Materials[model.Meshes[i].MaterialId].Name}: " + string.Join(" ", byBone.OrderByDescending(kv => kv.Value).Take(5).Select(kv => $"{sk.Names[kv.Key]}={kv.Value}")));
            }
            for (int j = 0; j < model.Meshes.Count; j++) model.Meshes[j].Visible = visible[j];
            foreach (var m in dae.Meshes)
            {
                var byBone = new Dictionary<int, int>();
                foreach (var p in m.Positions)
                {
                    int v = rig.IndexOf(p);
                    if (v < 0) continue;
                    int b = rig.Bones[v * 4];
                    byBone[b] = byBone.GetValueOrDefault(b) + 1;
                }
                Console.WriteLine($"  trophy {m.Material} [{Path.GetFileName(m.TextureFile ?? "-")}] {m.Positions.Count / 3} tris: " + string.Join(" ", byBone.OrderByDescending(kv => kv.Value).Take(4).Select(kv => $"{sk.Names[kv.Key]}={kv.Value}")));
            }
        }

        static string Short(string n) => n.Replace("_shoulder", "_sh").Replace("_elbow", "_el").Replace("_wrist", "_wr").Replace("Spine_2", "chest").Replace("Head_1", "head");

        // the game model's own mesh per bone, posed into the given world matrices
        static List<Vector3>[] GamePieces(Model model, Matrix4[] world)
        {
            var poser = new BipedAnimator(model);
            poser.SetBoth(PlayerAnimation.Idle, AnimFlags.None);
            HunterRig.Pose(poser, 0f, new float[16 * 32]);
            var baked = GeometryBaker.Bake(model, new float[3], out _, useAnimation: true, useMatrixStack: true, skinned: true);
            const int S = GeometryBaker.StrideSkinned;
            var res = new List<Vector3>[model.Nodes.Count];
            for (int i = 0; i < res.Length; i++) res[i] = new List<Vector3>();
            foreach (var b in baked)
                for (int i = 0; i + S <= b.Verts.Count; i += S)
                {
                    int node = model.NodeMatrixIds[(int)b.Verts[i + 11]];
                    res[node].Add(Vector3.TransformPosition(new Vector3(b.Verts[i], b.Verts[i + 1], b.Verts[i + 2]), world[node]));
                }
            return res;
        }

        // the game model's vertices of the meshes whose material passes `keep`, on one bone, posed into `world`
        static List<Vector3> GameMeshOnBone(Model model, Matrix4[] world, int bone, Func<string, bool> keep)
        {
            var visible = model.Meshes.Select(m => m.Visible).ToArray();
            for (int j = 0; j < model.Meshes.Count; j++) model.Meshes[j].Visible = keep(model.Materials[model.Meshes[j].MaterialId].Name);
            var res = new List<Vector3>();
            if (visible.Where((_, j) => model.Meshes[j].Visible).Any())
                foreach (var b in GeometryBaker.Bake(model, new float[3], out _, useAnimation: false, useMatrixStack: true, skinned: true))
                    for (int k = 0; k + GeometryBaker.StrideSkinned <= b.Verts.Count; k += GeometryBaker.StrideSkinned)
                        if (model.NodeMatrixIds[(int)b.Verts[k + 11]] == bone)
                            res.Add(Vector3.TransformPosition(new Vector3(b.Verts[k], b.Verts[k + 1], b.Verts[k + 2]), world[bone]));
            for (int j = 0; j < model.Meshes.Count; j++) model.Meshes[j].Visible = visible[j];
            return res;
        }

        static float Angle(Vector3 a, Vector3 b) => MathF.Acos(Math.Clamp(Vector3.Dot(a.Normalized(), b.Normalized()), -1f, 1f)) * 180f / MathF.PI;

        static Vector3 Centroid(IReadOnlyList<Vector3> p) => p.Aggregate(Vector3.Zero, (s, x) => s + x) / p.Count;

        // the direction the points spread most along (power iteration on the covariance)
        static Vector3 MajorAxis(IReadOnlyList<Vector3> p)
        {
            var c = Centroid(p);
            var m = new Matrix3();
            foreach (var x in p) { var d = x - c; m.Row0 += d * d.X; m.Row1 += d * d.Y; m.Row2 += d * d.Z; }
            var v = new Vector3(0.577f, 0.577f, 0.577f);
            for (int i = 0; i < 60; i++) v = new Vector3(Vector3.Dot(m.Row0, v), Vector3.Dot(m.Row1, v), Vector3.Dot(m.Row2, v)).Normalized();
            return v;
        }
    }
}
