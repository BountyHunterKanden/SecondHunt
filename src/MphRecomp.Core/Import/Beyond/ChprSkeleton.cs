using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace MphRecomp.Import.Beyond
{
    // A Beyond CHPR's skeleton: the abs (skeleton) nodes, their parents, the rest pose ("bind locals") and the skinned
    // bind matrices. Port of mp4pose.Rig / skel.py (brawl_extract/rig_work/gun/mp4anim/py), reading everything from the
    // CHPR bytes (Chpr) instead of the dumper's chpr.json.
    //
    // Rest pose: an array of 28-byte records (f32 qw, qx, qy, qz, tx, ty, tz: rotation + translation local to the
    // parent) stored as typed blocks: u16 (record count << 5 | 4), u16 byte size (= 28 x count), records; an array
    // longer than 146 records runs on in the next block. The first such array of 100+ valid records after the name pool
    // is the rest table (Samus's CHPR: 0x1ed36, 146 + 146 + 37 = 329 records).
    //   Record order: first every JOINT anim node in anim-group order (groups ascending, slot order inside a group;
    //   groups whose nodes are not transforms -- events, floats, visibility bools -- are skipped), then the abs-only
    //   nodes in abs order (nodes present in the anim set under an aliased name "X.suffix" and Skeleton_Root skipped).
    //   (The CHPR also maps anim nodes to record indices explicitly, in the evaluation data before the table; that map
    //   agrees with this order on all 271 anim joints it lists.)
    //   Check: composing these locals from the root reproduces all 234 skinned bind matrices (the inverses of the render
    //   contexts' skinned inverse matrices) to < 1e-6.
    // Matrices here are 4x4 row major (m[4 * row + col]) for column vectors (M = T * R, translation in column 3), as the
    // Python's numpy arrays are.
    public sealed class ChprSkeleton
    {
        // anim node kinds (Chpr.NodeKind) that are transforms: 0x43 joint, 0x44 root motion
        public static readonly int[] TransformKinds = { 0x43, 0x44 };

        public Chpr Chpr = null!;
        public string[] Names = Array.Empty<string>();          // abs nodes
        public int[] Parent = Array.Empty<int>();               // -1 = root; the reference decoder's reading (golden-compatible)
        // the parent's true list position: parent ids are TRANSFORM indices (Chpr.NodeIndex), which equal list positions
        // only up to the first non-transform node (Samus: node 294). Differs from Parent for 1 node of Samus's CHPR
        // (AttractorViola: EyeEffect, not back_jet_cover_vis); none of the skinned or first-person joints.
        public int[] ParentCorrected = Array.Empty<int>();
        public readonly Dictionary<string, int> Index = new();  // name -> abs index (the last of equal names)
        // anim nodes: low 16 bits of the id -> name / full id
        public readonly Dictionary<int, string> AnimNodes = new();
        public readonly Dictionary<int, uint> AnimNodeIds = new();
        public int[] NonJointGroups = Array.Empty<int>();
        public string[] RestOrder = Array.Empty<string>();      // the node each rest record belongs to (joint anim nodes, then abs-only)
        public int RestTableOffset;
        public readonly List<double[]> RestRecords = new();     // 7 values each: qw qx qy qz tx ty tz
        public int AnimJointCount;
        public readonly Dictionary<string, (double[] Q, double[] T)> Rest = new();   // rotation wxyz, translation
        public readonly Dictionary<string, double[]> RestWorldByName = new();
        public double[][] RestWorld = Array.Empty<double[]>();  // per abs node: its bind (rest world) matrix
        public readonly Dictionary<int, double[]> BindWorld = new();   // skinned abs index -> inverse of its skinned inverse matrix
        public (int Good, int Total, double MaxErr) BindCheck;
        // skinned bones whose rebuilt rest world misses the CHPR's own bind matrix (name, largest element error)
        public readonly List<(string Name, double Err)> BindMismatches = new();

        public static ChprSkeleton Build(Chpr c)
        {
            var s = new ChprSkeleton { Chpr = c, Names = c.AbsNodes.Names };
            int n = s.Names.Length;
            s.Parent = new int[n];
            s.ParentCorrected = new int[n];
            for (int i = 0; i < n; i++)
            {
                s.Parent[i] = c.ParentIds[i] == 0xffff ? -1 : Chpr.NodeIndex(c.ParentIds[i]);
                s.ParentCorrected[i] = c.ParentIds[i] == 0xffff ? -1 : c.AbsPositionOfTransform.GetValueOrDefault(Chpr.NodeIndex(c.ParentIds[i]), -1);
                s.Index[s.Names[i]] = i;
            }
            for (int i = 0; i < c.AnimNodes.Ids.Length; i++)
            {
                int v = (int)(c.AnimNodes.Ids[i] & 0xffff);
                s.AnimNodes[v] = c.AnimNodes.Names[i];
                s.AnimNodeIds[v] = c.AnimNodes.Ids[i];
            }
            // groups holding no transform node: events, floats, visibility bools
            var groups = new SortedSet<int>(s.AnimNodes.Keys.Select(v => v & 63));
            s.NonJointGroups = groups.Where(g => !s.AnimNodeIds.Where(kv => (kv.Key & 63) == g).Any(kv => TransformKinds.Contains(Chpr.NodeKind(kv.Value)))).ToArray();

            (s.RestTableOffset, List<double[]> recs) = FindRestRecords(c.Data, c.NamePoolEnd, c.AnimNodes.Offset);
            s.RestRecords.AddRange(recs);
            var order = new List<string>();
            foreach (int g in groups)
            {
                if (Array.IndexOf(s.NonJointGroups, g) < 0) order.AddRange(s.GroupMembers(g));
            }
            var animSet = new HashSet<string>(s.AnimNodes.Values);
            var dotted = new HashSet<string>(animSet.Where(x => x.Contains('.')).Select(x => x.Substring(0, x.IndexOf('.'))));
            var extra = s.Names.Where(x => !animSet.Contains(x) && !dotted.Contains(x) && x != "Skeleton_Root");
            s.RestOrder = order.Concat(extra).ToArray();
            s.AnimJointCount = order.Count;
            for (int k = 0; k < s.RestOrder.Length && k < recs.Count; k++)
            {
                double[] r = recs[k];
                s.Rest[s.RestOrder[k]] = (new[] { r[0], r[1], r[2], r[3] }, new[] { r[4], r[5], r[6] });
            }
            if (s.Rest.TryGetValue("root.move", out var rm)) s.Rest.TryAdd("root", rm);
            if (s.Rest.TryGetValue("root.anchorBlend", out var ra)) s.Rest.TryAdd("Skeleton_Root", ra);

            foreach (Chpr.RenderContext rc in c.RenderContexts)
            {
                for (int i = 0; i < rc.BoneIds.Length && i < rc.InverseMatrices.Length; i++)
                {
                    float[] m = rc.InverseMatrices[i];
                    var full = new double[16];
                    for (int k = 0; k < 12; k++) full[k] = m[k];
                    full[15] = 1.0;
                    s.BindWorld[Chpr.NodeIndex(rc.BoneIds[i])] = ChprMath.Invert(full);
                }
            }
            foreach (var kv in s.BindWorld)
            {
                if (kv.Key >= n) continue;
                string nm = s.Names[kv.Key];
                if (s.Rest.ContainsKey(nm)) continue;
                int p = s.Parent[kv.Key];
                double[] local = kv.Value;
                if (p >= 0)
                {
                    if (!s.BindWorld.TryGetValue(p, out double[]? pb)) continue;   // parent's pose unknown here: leave it
                    local = ChprMath.MatMul(ChprMath.Invert(pb), kv.Value);
                }
                s.BindRest[nm] = (MatToQuat(local), new[] { local[3], local[7], local[11] });
            }
            foreach (var kv in s.Worlds(new Dictionary<string, double[]>())) s.RestWorldByName[kv.Key] = kv.Value;
            s.RestWorld = s.Names.Select(x => s.RestWorldByName[x]).ToArray();
            int good = 0;
            double worst = 0;
            foreach (var kv in s.BindWorld)
            {
                if (kv.Key >= n) throw new InvalidDataException($"CHPR: skinned bone {kv.Key} is not a skeleton node");
                double[] w = s.RestWorldByName[s.Names[kv.Key]];
                double e = 0;
                for (int k = 0; k < 16; k++) e = Math.Max(e, Math.Abs(w[k] - kv.Value[k]));
                if (e < 1e-4) good++;
                else s.BindMismatches.Add((s.Names[kv.Key], e));
                worst = Math.Max(worst, e);
            }
            s.BindCheck = (good, s.BindWorld.Count, worst);
            return s;
        }

        // anim nodes of group g in slot order
        public List<string> GroupMembers(int g) =>
            AnimNodes.Where(kv => (kv.Key & 63) == g).OrderBy(kv => kv.Key >> 6).Select(kv => kv.Value).ToList();

        public (double[] Q, double[] T) RestLocal(string name) =>
            Rest.TryGetValue(name, out var r) ? r
            : BindRest.TryGetValue(name, out var br) ? br
            : (new[] { 1.0, 0, 0, 0 }, new double[3]);

        // rest locals of skinned nodes the rest table doesn't list, taken from their CHPR bind matrix (a node with no rest
        // record is static, so its rest pose is its bind pose -- Sylux's root-level shock_coil_vis / sylux_body, bound
        // 0.8 up). Kept apart from Rest so HasRestRecord still reports only the table's own records.
        public readonly Dictionary<string, (double[] Q, double[] T)> BindRest = new();

        static double[] MatToQuat(double[] m)
        {
            double tr = m[0] + m[5] + m[10];
            if (tr > 0)
            {
                double S = Math.Sqrt(tr + 1) * 2;
                return new[] { 0.25 * S, (m[9] - m[6]) / S, (m[2] - m[8]) / S, (m[4] - m[1]) / S };
            }
            if (m[0] > m[5] && m[0] > m[10])
            {
                double S = Math.Sqrt(1 + m[0] - m[5] - m[10]) * 2;
                return new[] { (m[9] - m[6]) / S, 0.25 * S, (m[1] + m[4]) / S, (m[2] + m[8]) / S };
            }
            if (m[5] > m[10])
            {
                double S = Math.Sqrt(1 + m[5] - m[0] - m[10]) * 2;
                return new[] { (m[2] - m[8]) / S, (m[1] + m[4]) / S, 0.25 * S, (m[6] + m[9]) / S };
            }
            double S2 = Math.Sqrt(1 + m[10] - m[0] - m[5]) * 2;
            return new[] { (m[4] - m[1]) / S2, (m[2] + m[8]) / S2, (m[6] + m[9]) / S2, 0.25 * S2 };
        }

        public bool HasRestRecord(string name) => Rest.ContainsKey(name);

        public bool Skinned(int i) => BindWorld.ContainsKey(i);

        // {name: 4x4 local} (missing -> rest) -> {name: 4x4 world} for every abs node (a name's first node wins)
        public Dictionary<string, double[]> Worlds(Dictionary<string, double[]> locals)
        {
            var w = new Dictionary<string, double[]>();
            var busy = new HashSet<int>();
            double[] At(int i)
            {
                string nm = Names[i];
                if (w.TryGetValue(nm, out double[]? m)) return m;
                if (!busy.Add(i)) throw new InvalidDataException("CHPR: the skeleton's parents form a loop");
                double[] l = locals.TryGetValue(nm, out double[]? lm) ? lm : ChprMath.Trs(RestLocal(nm).Q, RestLocal(nm).T);
                int p = Parent[i];
                m = p < 0 ? l : ChprMath.MatMul(At(p), l);
                w[nm] = m;
                return m;
            }
            for (int i = 0; i < Names.Length; i++) At(i);
            return w;
        }

        static bool Valid(double[] r)
        {
            foreach (double v in r)
            {
                if (!double.IsFinite(v)) return false;
            }
            double nq = Math.Sqrt(r[0] * r[0] + r[1] * r[1] + r[2] * r[2] + r[3] * r[3]);
            return Math.Abs(nq - 1) < 1e-3 && Math.Abs(r[4]) < 50 && Math.Abs(r[5]) < 50 && Math.Abs(r[6]) < 50;
        }

        // the first chain of 28-byte typed blocks in [from, to) holding 100+ valid (quaternion, translation) records:
        // -> (offset of its first record, the records up to the first invalid one)
        public static (int Start, List<double[]> Records) FindRestRecords(byte[] b, int from, int to)
        {
            for (int o = from; o + 4 <= to; o++)
            {
                int hdr = Chpr.U16(b, o);
                if ((hdr & 31) != 4 || (hdr >> 5) == 0 || Chpr.U16(b, o + 2) != 28 * (hdr >> 5)) continue;
                var recs = new List<double[]>();
                int p = o;
                while (p + 4 <= b.Length)
                {
                    int h = Chpr.U16(b, p), cnt = h >> 5;
                    if ((h & 31) != 4 || cnt == 0 || Chpr.U16(b, p + 2) != 28 * cnt || p + 4 + 28L * cnt > b.Length) break;
                    bool stop = false;
                    for (int r = 0; r < cnt; r++)
                    {
                        var rec = new double[7];
                        for (int k = 0; k < 7; k++) rec[k] = Chpr.F32(b, p + 4 + 28 * r + 4 * k);
                        if (!Valid(rec)) { stop = true; break; }
                        recs.Add(rec);
                    }
                    if (stop) break;
                    p += 4 + 28 * cnt;
                }
                if (recs.Count >= 100) return (o + 4, recs);
            }
            throw new InvalidDataException("CHPR: rest-pose table not found");
        }
    }
}
