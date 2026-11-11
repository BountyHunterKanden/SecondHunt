using System;
using System.Linq;
using MphRead.Entities;
using MphRecomp.Anim;
using OpenTK.Mathematics;

namespace MphRead
{
    // The DEATH look option (docs/IDEAS_SURVEY.md D1): First Hunt's collapse retargeted onto MPH's hunters.
    // -deathanim root=<import root> [probe]: probe dumps the cart's body skeleton and each hunter's, side by side;
    // without it, the check: every hunter gets clip 26, every Idle-animated node is keyed, the last frame lies low.
    internal static class DeathAnimTest
    {
        public static void Run(string[] args)
        {
            if (!ToolPaths.UseDesktopExtraction()) return;
            string? root = args.FirstOrDefault(a => a.StartsWith("root="))?[5..];
            if (!MphRecomp.FirstHunt.FirstHuntGame.Register(root))
            {
                Console.WriteLine($"  [SKIP] no First Hunt import under '{root}'.");
                return;
            }
            Paths.ChooseMphPath();
            Model fh = PlayerEntity.FirstHuntBody().Model;
            if (args.Contains("probe"))
            {
                Dump("FH samus_hi_yellow", fh, PlayerEntity.DeathClip);
                foreach (Hunter h in new[] { Hunter.Samus, Hunter.Kanden, Hunter.Trace, Hunter.Sylux, Hunter.Noxus, Hunter.Spire, Hunter.Weavel, Hunter.Guardian })
                {
                    Dump($"{h} ({Metadata.HunterModels[h][0]})", Read.GetModelInstance(Metadata.HunterModels[h][0]).Model, -1);
                }
                return;
            }
            Check(fh);
        }

        static void Dump(string label, Model m, int deathClip)
        {
            var sk = new DsSkeleton(m);
            var p = new SkeletonPose(sk.Count);
            var w = new Matrix4[sk.Count];
            DsSkeleton.Sample(m, (int)PlayerAnimation.Idle, 0, p);
            sk.Fk(p, w);
            Console.WriteLine($"=== {label} scale={m.Scale.X} nodes={sk.Count} clips={m.AnimationGroups.Node.Count} idle={m.AnimationGroups.Node[8].FrameCount}f");
            for (int i = 0; i < sk.Count; i++)
            {
                var t = w[i].ExtractTranslation();
                Console.WriteLine($"  [{i,2}] {sk.Names[i],-16} parent={(sk.Parent[i] >= 0 ? sk.Names[sk.Parent[i]] : "-"),-16} anim={(p.Animated[i] ? 1 : 0)} " +
                    $"pos=({t.X,6:0.00},{t.Y,6:0.00},{t.Z,6:0.00}) S=({p.S[i].X:0.000},{p.S[i].Y:0.000},{p.S[i].Z:0.000}) R=({p.R[i].X:0.00},{p.R[i].Y:0.00},{p.R[i].Z:0.00}) T=({p.T[i].X:0.00},{p.T[i].Y:0.00},{p.T[i].Z:0.00})");
            }
            if (deathClip >= 0)
            {
                NodeAnimationGroup g = m.AnimationGroups.Node[deathClip];
                Console.WriteLine($"  death clip {deathClip}: {g.FrameCount} frames, {g.Animations.Count} nodes");
                foreach (int f in new[] { 0, g.FrameCount / 2, g.FrameCount - 1 })
                {
                    DsSkeleton.Sample(m, deathClip, f, p);
                    sk.Fk(p, w);
                    Console.WriteLine($"   f{f,3}: " + string.Join(" ", Enumerable.Range(0, sk.Count).Where(i => p.Animated[i])
                        .Select(i => { var t = w[i].ExtractTranslation(); return $"{sk.Names[i]}({t.X:0.00},{t.Y:0.00},{t.Z:0.00})"; })));
                }
            }
        }

        static void Check(Model fh)
        {
            int fails = 0;
            void Fail(string s) { fails++; Console.WriteLine($"  [FAIL] {s}"); }

            // the angle reading gives back the rotation it read, gimbal included
            var rng = new Random(7);
            float worst = 0;
            for (int k = 0; k < 2000; k++)
            {
                var e = new Vector3(Ang(rng), (k % 50 == 0 ? MathF.PI / 2 * (k % 100 == 0 ? 1 : -1) : Ang(rng) / 2), Ang(rng));
                Matrix3 m = Matrix3.CreateRotationX(e.X) * Matrix3.CreateRotationY(e.Y) * Matrix3.CreateRotationZ(e.Z);
                Vector3 back = DeathCollapse.Euler(m);
                Matrix3 m2 = Matrix3.CreateRotationX(back.X) * Matrix3.CreateRotationY(back.Y) * Matrix3.CreateRotationZ(back.Z);
                worst = MathF.Max(worst, Diff(m, m2));
            }
            if (worst > 1e-3f) Fail($"angle reading recomposes {worst:0.0000} off");
            else Console.WriteLine($"  [OK] angle reading recomposes (worst {worst:0.00000})");

            var src = new DsSkeleton(fh);
            int frames = fh.AnimationGroups.Node[PlayerEntity.DeathClip].FrameCount;
            const int idle = (int)PlayerAnimation.Idle;
            var srcIdle = new Matrix4[src.Count]; var srcW = new Matrix4[src.Count]; var sp = new SkeletonPose(src.Count);
            DsSkeleton.Sample(fh, idle, 0, sp); src.Fk(sp, srcIdle);
            foreach (Hunter h in new[] { Hunter.Samus, Hunter.Kanden, Hunter.Trace, Hunter.Sylux, Hunter.Noxus, Hunter.Spire, Hunter.Weavel, Hunter.Guardian })
            {
                for (int lod = 0; lod < 2; lod++)
                {
                    string name = $"{h} lod{lod}";
                    Model m = Read.GetModelInstance(Metadata.HunterModels[h][lod]).Model;
                    if (!DeathCollapse.Add(m, fh) || !DeathCollapse.Add(m, fh)) { Fail($"{name}: not added"); continue; }
                    AnimationGroups g = m.AnimationGroups;
                    int want = PlayerEntity.DeathClip + 1;
                    if (g.Node.Count != want || g.Material.Count < want || g.Texcoord.Count < want || g.Texture.Count < want)
                    {
                        Fail($"{name}: clip lists {g.Node.Count}/{g.Material.Count}/{g.Texcoord.Count}/{g.Texture.Count}, want {want}");
                        continue;
                    }
                    NodeAnimationGroup clip = g.Node[PlayerEntity.DeathClip];
                    if (clip.FrameCount != frames) Fail($"{name}: {clip.FrameCount} frames, want {frames}");
                    var sk = new DsSkeleton(m);
                    var ip = new SkeletonPose(sk.Count); var wIdle = new Matrix4[sk.Count];
                    DsSkeleton.Sample(m, idle, 0, ip); sk.Fk(ip, wIdle);
                    var p = new SkeletonPose(sk.Count); var w = new Matrix4[sk.Count];
                    for (int i = 0; i < sk.Count; i++)
                        if (ip.Animated[i] && !clip.Animations.ContainsKey(sk.Names[i])) Fail($"{name}: {sk.Names[i]} not keyed");
                    int hips = sk.IndexOf("Pelvis") >= 0 ? sk.IndexOf("Pelvis") : sk.IndexOf("Skeleton_Root");
                    float floor = Enumerable.Range(0, sk.Count).Min(i => wIdle[i].ExtractTranslation().Y);
                    float bone = 0, turn = 0, sink = 0;
                    for (int f = 0; f < frames; f++)
                    {
                        DsSkeleton.Sample(m, PlayerEntity.DeathClip, f, p); sk.Fk(p, w);
                        DsSkeleton.Sample(fh, PlayerEntity.DeathClip, f, sp); src.Fk(sp, srcW);
                        for (int i = 0; i < sk.Count; i++)
                        {
                            int pa = sk.Parent[i];
                            if (pa >= 0 && sk.Parent[pa] >= 0) // bone lengths are the hunter's own (the root's shift aside)
                            {
                                float l0 = (wIdle[i].ExtractTranslation() - wIdle[pa].ExtractTranslation()).Length;
                                float l = (w[i].ExtractTranslation() - w[pa].ExtractTranslation()).Length;
                                bone = MathF.Max(bone, MathF.Abs(l - l0));
                            }
                            if (pa >= 0) sink = MathF.Max(sink, floor - w[i].ExtractTranslation().Y);
                            if (Twin(sk.Names[i]) is string t && src.IndexOf(t) is int s && s >= 0) // turns as the cart's does
                            {
                                Matrix3 want3 = Rot(wIdle[i]) * Rot(srcIdle[s]).Inverted() * Rot(srcW[s]);
                                turn = MathF.Max(turn, Diff(Rot(w[i]), want3));
                            }
                        }
                    }
                    Vector3 end = w[hips].ExtractTranslation(), start = wIdle[hips].ExtractTranslation();
                    string at = $"hips ({start.X:0.00},{start.Y:0.00},{start.Z:0.00}) -> ({end.X:0.00},{end.Y:0.00},{end.Z:0.00})";
                    if (bone > 0.01f) Fail($"{name}: a bone stretches by {bone:0.000}");
                    if (turn > 0.01f) Fail($"{name}: a node turns {turn:0.000} off the cart's");
                    if (sink > 0.01f) Fail($"{name}: a joint sinks {sink:0.000} below the feet");
                    if (end.Y > start.Y * 0.45f) Fail($"{name}: doesn't end lying down, {at}");
                    Console.WriteLine($"  [{(fails == 0 ? "OK" : "..")}] {name}: {frames}f, {clip.Animations.Count} nodes, {at}, bone {bone:0.0000} turn {turn:0.0000} sink {sink:0.0000}");
                }
            }
            Console.WriteLine(fails == 0 ? "  ALL OK" : $"  {fails} FAILED");
        }

        static string? Twin(string n) => n switch
        {
            "Skeleton_Root" or "Pelvis" => "Pelvis", "Spine_2" => "Spine_2", "Head_1" => "Geo_Head",
            "L_hip" => "Geo_L_Thigh", "L_knee" => "Geo_L_Calf", "L_ankle" => "Geo_L_Foot",
            "R_hip" => "Geo_R_Thigh", "R_knee" => "Geo_R_Calf", "R_ankle" => "Geo_R_Foot",
            "L_shoulder" or "L_elbow" or "L_wrist" or "R_shoulder" or "R_elbow" => n,
            "L_varias2_SDK" => "Geo_L_Shoulder", "R_varias2_SDK" => "Geo_R_Shoulder",
            _ => null,
        };

        static float Ang(Random r) => (float)(r.NextDouble() * 2 - 1) * MathF.PI;

        static Matrix3 Rot(Matrix4 m) => new Matrix3(m.Row0.Xyz.Normalized(), m.Row1.Xyz.Normalized(), m.Row2.Xyz.Normalized());

        static float Diff(Matrix3 a, Matrix3 b) =>
            MathF.Max(MathF.Max((a.Row0 - b.Row0).Length, (a.Row1 - b.Row1).Length), (a.Row2 - b.Row2).Length);
    }
}
