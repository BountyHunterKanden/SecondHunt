using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using MphRead.Entities;
using MphRecomp.Anim;
using MphRecomp.Assets;
using OpenTK.Mathematics;

namespace MphRead
{
    // Dump what a trophy's saved rigs actually do, for checking by eye (scripts/rig_look.py draws it):
    //   sculpt  -- the trophy as sculpted with the skeleton the landmark fit put in it (the default rig's bind)
    //   tpose   -- the "clean + T-pose" rig's bind: the trophy straightened into a T-pose, with its skeleton
    //   idle    -- the default rig playing the game's Idle, first frame
    // Everything in rig space (the game model's units). Writes <out>/<T>_{sculpt,tpose,idle}_verts.txt (x y z per
    // trophy vertex), <T>_{...}_joints.txt (name parent x y z), <T>_tris.txt (drawn triangles as vertex indices).
    // Run: MphRead.Tools.dll -riglook Kanden [more] --out dir
    internal static class RigLook
    {
        public static void Run(string[] args)
        {
            if (!ToolPaths.UseDesktopExtraction()) return;
            string repo = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
            string conv = Path.Combine(repo, "brawl_extract", "Converted");
            int oi = Array.IndexOf(args, "--out");
            string outDir = oi > 0 ? args[oi + 1] : Path.Combine(Path.GetTempPath(), "mph_riglook");
            Directory.CreateDirectory(outDir);
            var ci = CultureInfo.InvariantCulture;
            foreach (string trophy in args.Skip(1).Where(a => !a.StartsWith("--") && (oi < 0 || a != args[oi + 1])))
            {
                if (!TrophyRigs.TryHunterFor(trophy, out Hunter h)) { Console.WriteLine($"unknown trophy {trophy}"); continue; }
                string? dae = Directory.GetFiles(Path.Combine(conv, trophy), "*.dae").FirstOrDefault();
                if (dae == null) { Console.WriteLine($"{trophy}: no .dae"); continue; }
                Model model = Read.GetModelInstance(Metadata.HunterModels[h][0]).Model;
                var hd = DaeModel.Load(dae);
                var corners = hd.Meshes.SelectMany(m => m.Positions).ToList();   // System.Numerics, as TrophyRig.IndexOf takes
                var sk = new DsSkeleton(model);
                var rig = TrophyRigIO.Load(Path.ChangeExtension(dae, ".mphrig"), TrophyRigIO.CacheKey(dae));
                var tv = TrophyRigIO.Variants.First(v => v.Key == "tpose");
                var tp = TrophyRigIO.Load(TrophyRigIO.VariantPathFor(dae, tv), TrophyRigIO.VariantKey(dae, tv));
                if (rig == null) { Console.WriteLine($"{trophy}: no saved rig"); continue; }
                int n = sk.Count;
                string Nm(int i) => sk.Names[i];
                void Joints(string file, Func<int, Vector3> at) => File.WriteAllLines(file, Enumerable.Range(0, n).Select(i =>
                    string.Create(ci, $"{Nm(i)} {(sk.Parent[i] >= 0 ? Nm(sk.Parent[i]) : "-")} {at(i).X} {at(i).Y} {at(i).Z}")));
                void Verts(string file, Vector3[] v) => File.WriteAllLines(file, v.Select(p => string.Create(ci, $"{p.X} {p.Y} {p.Z}")));

                // sculpt: the trophy in rig space + the fitted bind skeleton
                var sculpt = rig.Vertices.Select(p => Vector3.TransformPosition(p, rig.TrophyToRig)).ToArray();
                Verts(Path.Combine(outDir, $"{trophy}_sculpt_verts.txt"), rig.BindPos ?? sculpt);
                Joints(Path.Combine(outDir, $"{trophy}_sculpt_joints.txt"), i => rig.BindWorld[i].ExtractTranslation());
                // tpose: the straightened bind of the T-pose rig
                if (tp != null && tp.BindPos != null && tp.BindPos.Length == rig.Vertices.Length)
                {
                    Verts(Path.Combine(outDir, $"{trophy}_tpose_verts.txt"), tp.BindPos);
                    Joints(Path.Combine(outDir, $"{trophy}_tpose_joints.txt"), i => tp.BindWorld[i].ExtractTranslation());
                }
                // idle: the default rig in the game's Idle, frame 0 (linear blend skinning, as the app does)
                var pose = new SkeletonPose(n); var world = new Matrix4[n];
                DsSkeleton.Sample(model, (int)PlayerAnimation.Idle, 0, pose);
                sk.Fk(pose, world, 0f, rig.Lengths, rig.Offsets);
                var bind = rig.BindPos ?? sculpt;
                var idle = new Vector3[bind.Length];
                for (int v = 0; v < bind.Length; v++)
                {
                    var acc = Vector3.Zero;
                    for (int k = 0; k < 4; k++)
                    {
                        float w = rig.Weights[v * 4 + k]; if (w <= 0) continue;
                        int b = rig.Bones[v * 4 + k];
                        acc += w * Vector3.TransformPosition(bind[v], rig.InvBind[b] * world[b]);
                    }
                    idle[v] = acc;
                }
                Verts(Path.Combine(outDir, $"{trophy}_idle_verts.txt"), idle);
                Joints(Path.Combine(outDir, $"{trophy}_idle_joints.txt"), i => world[i].ExtractTranslation());
                // drawn triangles
                var tris = new List<string>();
                for (int t = 0; t + 2 < corners.Count; t += 3)
                {
                    int a = rig.IndexOf(corners[t]), b = rig.IndexOf(corners[t + 1]), c = rig.IndexOf(corners[t + 2]);
                    if (a < 0 || b < 0 || c < 0 || rig.HidesTriangle(a, b, c)) continue;
                    tris.Add($"{a} {b} {c}");
                }
                File.WriteAllLines(Path.Combine(outDir, $"{trophy}_tris.txt"), tris);
                // the T-pose rig's own drawn triangles (its cuts can differ from the default rig's)
                if (tp != null)
                {
                    var tt = new List<string>();
                    for (int t = 0; t + 2 < corners.Count; t += 3)
                    {
                        int a = tp.IndexOf(corners[t]), b = tp.IndexOf(corners[t + 1]), c = tp.IndexOf(corners[t + 2]);
                        if (a < 0 || b < 0 || c < 0 || tp.HidesTriangle(a, b, c)) continue;
                        tt.Add($"{a} {b} {c}");
                    }
                    File.WriteAllLines(Path.Combine(outDir, $"{trophy}_tpose_tris.txt"), tt);
                    File.WriteAllLines(Path.Combine(outDir, $"{trophy}_tpose_bones.txt"), Enumerable.Range(0, tp.Vertices.Length).Select(v =>
                        string.Join(" ", Enumerable.Range(0, 4).Where(k => tp.Weights[v * 4 + k] > 0.01f).Select(k => string.Create(ci, $"{Nm(tp.Bones[v * 4 + k])}:{tp.Weights[v * 4 + k]:0.00}")))));
                }
                // each vertex's main bone (default rig) and the skeleton hop count between bones, for weld checks
                File.WriteAllLines(Path.Combine(outDir, $"{trophy}_bones.txt"), Enumerable.Range(0, rig.Vertices.Length).Select(v => Nm(rig.Bones[v * 4])));
                Console.WriteLine($"{trophy}: {rig.Vertices.Length} vertices, {tris.Count} triangles, tpose {(tp?.BindPos != null ? "yes" : "no")}");
            }
        }
    }
}
