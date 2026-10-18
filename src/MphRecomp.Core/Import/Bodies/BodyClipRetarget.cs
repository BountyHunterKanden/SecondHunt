using System;
using System.Collections.Generic;
using System.Linq;
using MphRecomp.Import.Retro;

namespace MphRecomp.Import.Bodies
{
    // A clip made for one Retro skeleton put on another of the same family (Retro's human rig: Zero Suit Samus, the
    // Federation troopers, the civilians -- Prime 3 names their bones alike), by bone name (recipe "clips", HdRecipe.Borrow).
    // Retro binds carry no rotation, so a clip's rotations are the bones' own turns from the bind: each mapped bone takes
    // its source bone's model-space turn, corrected by the fixed turn taking its own bind direction (bone -> its aim child)
    // onto the source's, so an arm hanging 33 degrees lower in one bind still swings the same way in the other:
    //   W_dst(b) = W_src(b) * C(b),   C(b): dst bind direction -> src bind direction
    // A bone the source hasn't (her extra spine and collar, her hair and face) keeps its bind turn from its parent. Only the
    // pelvis and the top bone keep a translation (the stance height and the travel), scaled by the pelvis heights; every
    // other bone stays on its own bind offset, so her proportions stay hers.
    public static class BodyClipRetarget
    {
        // the bind direction each bone is trued by: bone -> this child (names on both skeletons); a bone not listed takes its
        // parent's correction. The spine and neck aren't trued: their segments differ in count and length (she has a third
        // spine bone, the civilians don't), so copying the civilian's spine shape bent her waist into a swayback kink (owner
        // 2026-10-12, "like she got hit with a bat"); they keep her own bind curve and take only the clip's turns from it.
        static readonly Dictionary<string, string> AimChild = Build();

        static Dictionary<string, string> Build()
        {
            var d = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (string s in new[] { "L_", "R_" })
            {
                d[s + "collar"] = s + "shoulder"; d[s + "shoulder"] = s + "elbow"; d[s + "elbow"] = s + "wrist"; d[s + "wrist"] = s + "middle_1";
                d[s + "hip"] = s + "knee"; d[s + "knee"] = s + "ankle"; d[s + "ankle"] = s + "ball";
                foreach (string f in new[] { "thumb", "index", "middle", "ring", "pinky" })
                {
                    d[s + f + "_1"] = s + f + "_2"; d[s + f + "_2"] = s + f + "_3";
                }
            }
            return d;
        }

        public static Retro.Anim Retarget(Cinf src, Cinf dst, Retro.Anim a, string pelvis)
        {
            var srcByName = new Dictionary<string, uint>(StringComparer.Ordinal);
            foreach (Cinf.Bone b in src.Bones.Values)
            {
                if (b.Name != null) srcByName.TryAdd(b.Name, b.Id);
            }
            List<uint> dstOrder = TopDown(dst);
            List<uint> srcOrder = TopDown(src);
            // the correction per mapped dst bone
            var map = new Dictionary<uint, uint>();
            var corr = new Dictionary<uint, double[]>();
            foreach (uint id in dstOrder)
            {
                Cinf.Bone b = dst.Bones[id];
                double[] parentCorr = dst.Bones.ContainsKey(b.Parent) && b.Parent != id && corr.TryGetValue(b.Parent, out double[]? pc) ? pc : Q.Identity;
                if (b.Name == null || !srcByName.TryGetValue(b.Name, out uint s))
                {
                    corr[id] = parentCorr;
                    continue;
                }
                map[id] = s;
                double[] c = parentCorr;
                if (AimChild.TryGetValue(b.Name, out string? child) && Find(dst, child) is Cinf.Bone dc && srcByName.TryGetValue(child, out uint sc))
                {
                    double[] dd = Dir(b, dc), sd = Dir(src.Bones[s], src.Bones[sc]);
                    if (dd.Length == 3 && sd.Length == 3) c = Q.Arc(dd, sd);
                }
                corr[id] = c;
            }
            uint? sp = srcByName.TryGetValue(pelvis, out uint spv) ? spv : null;
            uint? dp = Find(dst, pelvis)?.Id;
            double scale = sp is uint s0 && dp is uint d0 && src.Bones[s0].Z > 1e-6 ? dst.Bones[d0].Z / src.Bones[s0].Z : 1;
            var srcTops = src.Bones.Values.Where(b => b.Parent == b.Id || !src.Bones.ContainsKey(b.Parent)).Select(b => b.Id).ToList();
            var dstTops = dst.Bones.Values.Where(b => b.Parent == b.Id || !dst.Bones.ContainsKey(b.Parent)).Select(b => b.Id).ToList();

            var o = new Retro.Anim { Duration = a.Duration, Tick = a.Tick, Keys = a.Keys };
            foreach (uint id in dstOrder) o.Rot[id] = new List<double[]>(a.Keys);
            if (dp is uint dpi) o.Trans[dpi] = new List<double[]>(a.Keys);
            foreach (uint t in dstTops) o.Trans[t] = new List<double[]>(a.Keys);
            var ws = new Dictionary<uint, double[]>();
            var wd = new Dictionary<uint, double[]>();
            for (int k = 0; k < a.Keys; k++)
            {
                ws.Clear(); wd.Clear();
                foreach (uint id in srcOrder)
                {
                    Cinf.Bone b = src.Bones[id];
                    double[] local = a.Rot.TryGetValue(id, out List<double[]>? r) ? r[Math.Min(k, r.Count - 1)] : Q.Identity;
                    ws[id] = b.Parent != id && ws.TryGetValue(b.Parent, out double[]? pw) ? Q.Mul(pw, local) : local;
                }
                foreach (uint id in dstOrder)
                {
                    Cinf.Bone b = dst.Bones[id];
                    double[] pw = b.Parent != id && wd.TryGetValue(b.Parent, out double[]? p) ? p : Q.Identity;
                    double[] w = map.TryGetValue(id, out uint s) ? Q.Mul(ws[s], corr[id]) : pw;
                    wd[id] = w;
                    o.Rot[id].Add(Q.Normalize(Q.Mul(Q.Conj(pw), w)));
                }
                if (dp is uint dpk)
                {
                    o.Trans[dpk].Add(sp is uint spk && a.Trans.TryGetValue(spk, out List<double[]>? tr)
                        ? Scaled(tr[Math.Min(k, tr.Count - 1)], scale) : Offset(dst, dst.Bones[dpk]));
                }
                // the travel: the source's top bones' translation summed onto the destination's first top bone
                for (int i = 0; i < dstTops.Count; i++)
                {
                    double[] v = Offset(dst, dst.Bones[dstTops[i]]);
                    if (i == 0)
                    {
                        foreach (uint st in srcTops)
                        {
                            if (!a.Trans.TryGetValue(st, out List<double[]>? tr)) continue;
                            double[] t = tr[Math.Min(k, tr.Count - 1)];
                            Cinf.Bone sb = src.Bones[st];
                            v = new[] { v[0] + (t[0] - sb.X) * scale, v[1] + (t[1] - sb.Y) * scale, v[2] + (t[2] - sb.Z) * scale };
                        }
                    }
                    o.Trans[dstTops[i]].Add(v);
                }
            }
            return o;
        }

        public const string BindClip = "bind";

        // the skeleton's bind pose as a two-key clip (no turns; every bone on its bind offset)
        public static Retro.Anim Bind(Cinf sk)
        {
            var o = new Retro.Anim { Duration = 1 / 30.0, Tick = 1 / 30.0, Keys = 2 };
            foreach (Cinf.Bone b in sk.Bones.Values)
            {
                o.Rot[b.Id] = new List<double[]> { Q.Identity, Q.Identity };
                double[] t = Offset(sk, b);
                o.Trans[b.Id] = new List<double[]> { t, t };
            }
            return o;
        }

        static double[] Scaled(double[] v, double s) => new[] { v[0] * s, v[1] * s, v[2] * s };

        // a bone's bind offset from its parent
        static double[] Offset(Cinf sk, Cinf.Bone b) =>
            b.Parent != b.Id && sk.Bones.TryGetValue(b.Parent, out Cinf.Bone? p) ? new[] { b.X - p.X, b.Y - p.Y, b.Z - p.Z } : new[] { b.X, b.Y, b.Z };

        static Cinf.Bone? Find(Cinf sk, string name) => sk.Bones.Values.FirstOrDefault(b => b.Name == name);

        static double[] Dir(Cinf.Bone a, Cinf.Bone b)
        {
            double x = b.X - a.X, y = b.Y - a.Y, z = b.Z - a.Z, n = Math.Sqrt(x * x + y * y + z * z);
            return n < 1e-6 ? Array.Empty<double>() : new[] { x / n, y / n, z / n };
        }

        // parents before children
        static List<uint> TopDown(Cinf sk)
        {
            var order = new List<uint>();
            var done = new HashSet<uint>();
            void Visit(uint id)
            {
                if (done.Contains(id)) return;
                Cinf.Bone b = sk.Bones[id];
                if (b.Parent != id && sk.Bones.ContainsKey(b.Parent)) Visit(b.Parent);
                if (done.Add(id)) order.Add(id);
            }
            foreach (uint id in sk.SortedIds()) Visit(id);
            return order;
        }

        // quaternions (x, y, z, w), Hamilton: Mul(a, b) turns by b, then a
        static class Q
        {
            public static readonly double[] Identity = { 0, 0, 0, 1 };

            public static double[] Mul(double[] a, double[] b) => new[]
            {
                a[3] * b[0] + a[0] * b[3] + a[1] * b[2] - a[2] * b[1],
                a[3] * b[1] - a[0] * b[2] + a[1] * b[3] + a[2] * b[0],
                a[3] * b[2] + a[0] * b[1] - a[1] * b[0] + a[2] * b[3],
                a[3] * b[3] - a[0] * b[0] - a[1] * b[1] - a[2] * b[2],
            };

            public static double[] Conj(double[] q) => new[] { -q[0], -q[1], -q[2], q[3] };

            public static double[] Normalize(double[] q)
            {
                double n = Math.Sqrt(q[0] * q[0] + q[1] * q[1] + q[2] * q[2] + q[3] * q[3]);
                return n < 1e-12 ? Identity : new[] { q[0] / n, q[1] / n, q[2] / n, q[3] / n };
            }

            // the shortest turn taking unit direction u onto v
            public static double[] Arc(double[] u, double[] v)
            {
                double d = u[0] * v[0] + u[1] * v[1] + u[2] * v[2];
                if (d < -0.999999) return new double[] { 0, 0, 1, 0 }; // opposite: half a turn (never in these binds)
                double[] c = { u[1] * v[2] - u[2] * v[1], u[2] * v[0] - u[0] * v[2], u[0] * v[1] - u[1] * v[0] };
                return Normalize(new[] { c[0], c[1], c[2], 1 + d });
            }
        }
    }
}
