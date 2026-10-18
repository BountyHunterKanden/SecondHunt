using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MphRecomp.Anim;
using MphRecomp.Import.Beyond;
using OpenTK.Mathematics;

namespace MphRecomp.Import.Hd
{
    // A Prime 4 character's own clips for its HD body's idle flourish (owner 2026-10-17: Beyond Sylux alternates his
    // taunt_flex and taunt_armSwipe), baked onto the DS bones the body is skinned to (Anim/TrophyFlourish: the file and how
    // the app plays it). Each DS bone follows the joint nearest the root among the Prime 4 joints folded onto it
    // (BeyondTrophy.DsName: the hip joint for a hip, the forearm for the right elbow, ...); its move is that joint's pose
    // over its bind, carried into trophy units (BeyondTrophy's scale and feet shift): D = T * W(t) * Bind^-1 * T^-1.
    // The clips come from the recipe's "flourish" character (the gameplay one: the cinematic model's skeleton has the
    // same joints), its joints a clip leaves alone at the recipe's idle, else at rest (as Gun4Builder's frames).
    public static class BeyondFlourish
    {
        public static void Write(IReadOnlyList<BeyondArchive> paks, HdRecipe h, RetroTrophy.Result conv, string outDir, Action<float, string> progress)
        {
            if (h.FlourishClips.Length == 0) return;
            Guid id = Guid.Parse(h.FlourishChpr);
            byte[] bytes = BeyondPakModelSource.Holder(paks, id, "flourish character file").ReadAsset(id);
            string sha = Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(bytes)).ToLowerInvariant();
            if (sha != h.FlourishChprSha1) throw new InvalidDataException($"{h.Name}: flourish character {h.FlourishChpr} has SHA-1 {sha}, the recipe was made from {h.FlourishChprSha1}");
            ChprCharacter ch = ChprCharacter.Load(bytes);
            ChprSkeleton sk = ch.Skeleton;
            string[] names = sk.Names;
            var parentName = new Dictionary<string, string?>(StringComparer.Ordinal);
            for (int i = 0; i < names.Length; i++) parentName[names[i]] = sk.Parent[i] >= 0 ? names[sk.Parent[i]] : null;
            int Depth(int j)
            {
                int d = 0;
                for (; j >= 0 && d < 1000; j = sk.Parent[j]) d++;
                return d;
            }
            // each DS bone's joint: the root-most one named for it (a joint folded on only through its parent or the Spine_2
            // default is a holder -- Sylux's 'sylux_body' -- that hardly moves); the Pelvis on the skeleton's own root follows
            // the hip joint Spine_1 does (the root stays put while the hips move)
            string[] ds = BeyondTrophy.Ds;
            var joint = new int[ds.Length];
            Array.Fill(joint, -1);
            for (int j = 0; j < names.Length; j++)
            {
                string? d = BeyondTrophy.OwnDsName(names[j]);
                int b = d != null ? Array.IndexOf(ds, d) : -1;
                if (b >= 0 && (joint[b] < 0 || Depth(j) < Depth(joint[b]))) joint[b] = j;
            }
            int pelvis = Array.IndexOf(ds, "Pelvis"), spine1 = Array.IndexOf(ds, "Spine_1");
            if (pelvis >= 0 && spine1 >= 0 && joint[spine1] >= 0 && (joint[pelvis] < 0 || sk.Parent[joint[pelvis]] < 0)) joint[pelvis] = joint[spine1];
            for (int b = 0; b < ds.Length; b++) progress(0.97f, $"flourish: {ds[b]} follows {(joint[b] >= 0 ? names[joint[b]] : "(none)")}");

            ChprAnimation Anim(string name)
            {
                int ai = ch.FindAnim(name);
                if (ai < 0) throw new InvalidDataException($"{h.Name}: the flourish character has no animation '{name}'");
                ChprAnimResult r = ch.Decode(ai);
                if (!r.Ok || r.Animation == null) throw new InvalidDataException($"{h.Name}: flourish '{name}': {r.Status} {r.Message}");
                return r.Animation;
            }
            ChprAnimation idle = Anim(h.FlourishIdle);
            Dictionary<string, double[]> Pose(ChprAnimation an, int f)
            {
                var locals = new Dictionary<string, double[]>(StringComparer.Ordinal);
                void Take(ChprAnimation a, int fa)
                {
                    int ja = a.Joints.Length;
                    for (int i = 0; i < ja; i++)
                    {
                        if (locals.ContainsKey(a.Joints[i])) continue;
                        int q = (fa * ja + i) * 4, t3 = (fa * ja + i) * 3;
                        locals[a.Joints[i]] = ChprMath.Trs(new double[] { a.RotXyzw[q + 3], a.RotXyzw[q], a.RotXyzw[q + 1], a.RotXyzw[q + 2] },
                            new double[] { a.Trans[t3], a.Trans[t3 + 1], a.Trans[t3 + 2] }, new double[] { a.Scale[t3], a.Scale[t3 + 1], a.Scale[t3 + 2] });
                    }
                }
                Take(an, f);
                Take(idle, 0);
                return sk.Worlds(locals);
            }
            // the bind's feet are where the body stands (the trophy's floor); the game's standing stance holds the foot joints
            // higher over its origin than the bind does (Sylux: about 0.07 of his 4.2): the clips come down by that much
            int[] feetJ = Enumerable.Range(0, names.Length).Where(j => BeyondTrophy.DsName(names[j], parentName) is "L_ankle" or "R_ankle").ToArray();
            double lift = 0;
            if (feetJ.Length > 0)
            {
                Dictionary<string, double[]> stand = Pose(idle, 0);
                lift = feetJ.Min(j => stand[names[j]][7]) - feetJ.Min(j => sk.RestWorld[j][7]);
            }
            double s = conv.Scale, feet = conv.Feet;
            progress(0.97f, $"flourish: the standing stance's feet {lift:0.###} over the bind's (taken off)");
            // into the pose from the bind's trophy units; out of it a stance lower
            double[] T = { s, 0, 0, 0, 0, s, 0, -s * (feet + lift), 0, 0, s, 0, 0, 0, 0, 1 };
            double[] Ti = { 1 / s, 0, 0, 0, 0, 1 / s, 0, feet, 0, 0, 1 / s, 0, 0, 0, 0, 1 };
            var binv = joint.Select(j => j >= 0 ? ChprMath.Invert(sk.RestWorld[j]) : null).ToArray();
            // the idle too: the body stands in it, and the flourishes start and end on it
            var file = new TrophyFlourish { Bones = ds.ToArray(), Idle = h.FlourishIdle };
            foreach (string clipName in h.FlourishClips.Append(h.FlourishIdle))
            {
                ChprAnimation an = Anim(clipName);
                var clip = new TrophyFlourish.Clip { Name = clipName, Fps = (float)an.FpsAssumed, Frames = an.Frames };
                clip.D = new Matrix4[an.Frames * ds.Length];
                for (int f = 0; f < an.Frames; f++)
                {
                    Dictionary<string, double[]> world = Pose(an, f);
                    for (int b = 0; b < ds.Length; b++)
                    {
                        double[] dc = joint[b] >= 0
                            ? ChprMath.MatMul(ChprMath.MatMul(T, ChprMath.MatMul(world[names[joint[b]]], binv[b]!)), Ti)
                            : new double[] { 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1 };
                        // column-vector (row-major array) -> OpenTK's row-vector matrix: its transpose
                        clip.D[f * ds.Length + b] = new Matrix4(
                            (float)dc[0], (float)dc[4], (float)dc[8], 0,
                            (float)dc[1], (float)dc[5], (float)dc[9], 0,
                            (float)dc[2], (float)dc[6], (float)dc[10], 0,
                            (float)dc[3], (float)dc[7], (float)dc[11], 1);
                    }
                }
                file.Clips.Add(clip);
                progress(0.98f, $"flourish: {clipName} {an.Frames} frames at {an.FpsAssumed:0.#} fps");
            }
            file.Write(Path.Combine(outDir, h.Stem + TrophyFlourish.Suffix));
        }
    }
}
