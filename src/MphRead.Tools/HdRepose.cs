using System;
using System.Globalization;
using System.IO;
using System.Linq;
using MphRecomp.Anim;
using OpenTK.Mathematics;

namespace MphRead
{
    // Bake a RE-POSED rest shape (brawl_extract/rig_work/session17/scripts/repose.py: the whole statue moved into Pose Studio's
    // rest pose, pieces solid, joint regions re-solved as-rigid-as-possible) into one of a trophy's rig caches: the rig's
    // bind pose becomes that rest pose (BindWorld = the rest pose's bone matrices) and every vertex gets its rest position
    // and the turn that took it there (BindPos / BindRot, the format the app already reads for straightened rigs). The
    // cache keeps its key, so the app loads it as that variant; the original is kept as <cache>.prerepose.
    // Optional "w <i> b0 b1 b2 b3 w0 w1 w2 w3" lines (all vertices) replace the skin weights too (session 21: Blender's
    // automatic weights from the Rigify test, mapped to the DS bones). Optional "off <i> x y z" (or "off <i> none") lines set
    // a bone's exact rest offset from its parent joint (parent-local, as TrophyRig.Offsets), so the joints can move onto a
    // re-posed mesh's own (session 25: nemonic's T-pose of Kanden).
    //   MphRead.Tools.dll -hdrepose <Trophy> --from <repose.txt> [--rig pieces]
    internal static class HdRepose
    {
        public static void Run(string[] args)
        {
            var ci = CultureInfo.InvariantCulture;
            string trophy = args[1];
            string Arg(string k, string d) { int i = Array.IndexOf(args, k); return i > 0 && i + 1 < args.Length ? args[i + 1] : d; }
            string from = Arg("--from", ""), key = Arg("--rig", "pieces");
            string repo = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
            string dae = Directory.GetFiles(Path.Combine(repo, "brawl_extract", "Converted", trophy), "*.dae").First();
            var v = TrophyRigIO.Variants.First(x => x.Key == key);
            string path = TrophyRigIO.VariantPathFor(dae, v); ulong vkey = TrophyRigIO.VariantKey(dae, v);
            string keep = path + ".prerepose";
            if (!File.Exists(keep)) File.Copy(path, keep);
            var rig = TrophyRigIO.Load(keep, vkey) ?? throw new InvalidOperationException($"{trophy}: no current '{key}' rig cache");
            int nb = rig.InvBind.Length, nv = rig.Vertices.Length;
            var bindWorld = (Matrix4[])rig.BindWorld.Clone(); var inv = (Matrix4[])rig.InvBind.Clone();
            var pos = new Vector3[nv]; var rot = new Quaternion[nv]; int nbs = 0, nvs = 0, nws = 0;
            var bones = (int[])rig.Bones.Clone(); var weights = (float[])rig.Weights.Clone();
            var offsets = new Vector3?[nb]; Array.Copy(rig.Offsets, offsets, Math.Min(nb, rig.Offsets.Length)); int nos = 0;
            foreach (string raw in File.ReadLines(from))
            {
                string line = raw.Split('#')[0].Trim(); if (line.Length == 0) continue;
                var w = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (w[0] == "off")
                {
                    int i = int.Parse(w[1], ci);
                    offsets[i] = w[2] == "none" ? null : new Vector3(float.Parse(w[2], ci), float.Parse(w[3], ci), float.Parse(w[4], ci)); nos++;
                    continue;
                }
                var f = w.Skip(2).Select(x => float.Parse(x, ci)).ToArray();
                if (w[0] == "bone")
                {
                    int i = int.Parse(w[1], ci);
                    bindWorld[i] = new Matrix4(f[0], f[1], f[2], f[3], f[4], f[5], f[6], f[7], f[8], f[9], f[10], f[11], f[12], f[13], f[14], f[15]);
                    inv[i] = Matrix4.Invert(bindWorld[i]); nbs++;
                }
                else if (w[0] == "v") { int i = int.Parse(w[1], ci); pos[i] = new Vector3(f[0], f[1], f[2]); rot[i] = new Quaternion(f[3], f[4], f[5], f[6]); nvs++; }
                else if (w[0] == "w")
                {
                    int i = int.Parse(w[1], ci);
                    for (int k = 0; k < 4; k++) { bones[i * 4 + k] = (int)f[k]; weights[i * 4 + k] = f[4 + k]; }
                    nws++;
                }
            }
            if (nws != 0 && nws != nv) throw new InvalidDataException($"{Path.GetFileName(from)}: {nws} weight lines, the rig has {nv} vertices");
            if (nbs != nb || nvs != nv) throw new InvalidDataException($"{Path.GetFileName(from)}: {nbs} bones / {nvs} vertices, the rig has {nb} / {nv}");
            var r = rig.Copy(); r.BindWorld = bindWorld; r.InvBind = inv; r.BindPos = pos; r.BindRot = rot;
            if (nws == nv) { r.Bones = bones; r.Weights = weights; }
            if (nos > 0) r.Offsets = offsets;
            TrophyRigIO.Save(path, r, vkey);
            Console.WriteLine($"{trophy}: '{key}' rig re-posed from {Path.GetFileName(from)} ({nv} vertices, {nb} bones{(nws == nv ? ", new weights" : "")}{(nos > 0 ? $", {nos} offsets" : "")}); original kept as {Path.GetFileName(keep)}");
        }
    }
}
