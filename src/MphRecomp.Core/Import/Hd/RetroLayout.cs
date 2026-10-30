using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using MphRecomp.Import.Retro;

namespace MphRecomp.Import.Hd
{
    // Retro's own skin laid onto a rig's vertices: "<Stem>.retro.txt", the layout the "retro" rig variant is built from
    // (TrophyRigIO RigVariant "retro"). Port of prime_to_trophy.py --layout. Per rig vertex (the .dae's unique vertices, in
    // the rig's order): the DS bone that Retro's skin weights most at the same spot of the model (Retro bones the DS
    // skeleton lacks fold into their nearest ancestor it has), SOFT where no bone holds 90% of the weight; a joined part
    // (the cannon) rides its own bone whole. Stray points (an island of at most 4 vertices whose bone none of its
    // neighbours has) take their neighbours' most common bone.
    public static class RetroLayout
    {
        static readonly HashSet<string> DsBones = new(StringComparer.Ordinal)
        {
            "Pelvis", "Skeleton_Root", "Spine_1", "Spine_2", "Head_1", "L_hip", "L_knee", "L_ankle", "R_hip", "R_knee", "R_ankle",
            "L_shoulder", "L_elbow", "L_wrist", "R_shoulder", "R_elbow", "L_varias2_SDK", "R_varias2_SDK",
        };

        // a bone the DS skeleton lacks (Prime 3's jets, knee / ankle armour) follows its nearest ancestor that it has
        static string DsUp(string? j, Dictionary<string, string?> parentOf)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            while (!String.IsNullOrEmpty(j) && seen.Add(j))
            {
                string d = RetroTrophy.Ds(j);
                if (DsBones.Contains(d)) return d;
                j = parentOf.TryGetValue(j, out string? p) ? p : null;
            }
            return "Spine_2";
        }

        // sculpt: the rig's vertices (trophy space, the rig's order); tris: the rig's drawn triangles as vertex indices
        public static string Build(RetroTrophy.Result conv, HdRecipe h, IReadOnlyList<System.Numerics.Vector3> sculpt,
            IReadOnlyList<(int A, int B, int C)> tris, List<string> log)
        {
            Cinf skel = conv.Skeleton ?? throw new InvalidOperationException($"{h.Name}: a static model has no rig layout");
            Cmdl m = conv.Parts[0].Model;
            List<(uint Bone, float Weight)[]> skin = m.Skin ?? throw new InvalidOperationException($"{h.Name}: the model has no skin");

            // the bones by id (prime_model.py's npz order), their parents and DS names
            List<uint> ids = skel.Bones.Keys.OrderBy(x => x).ToList();
            var bix = new Dictionary<uint, int>();
            for (int i = 0; i < ids.Count; i++) bix[ids[i]] = i;
            string[] bn = ids.Select(b => skel.Bones[b].Name ?? "").ToArray();
            var parentOf = new Dictionary<string, string?>(StringComparer.Ordinal);
            for (int i = 0; i < ids.Count; i++)
            {
                parentOf[bn[i]] = bix.TryGetValue(skel.Bones[ids[i]].Parent, out int pi) ? bn[pi] : null;
            }
            var dsb = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (string b in bn) dsb[b] = DsUp(b, parentOf);
            string[] dsn = dsb.Values.Distinct().OrderBy(x => x, StringComparer.Ordinal).ToArray();

            // every part's positions, flattened like the reference's parts.npz
            var pts = new List<(double X, double Y, double Z, int Part, int Index)>();
            for (int pj = 0; pj < conv.Parts.Count; pj++)
            {
                double[] P = conv.Parts[pj].P;
                for (int i = 0; i + 2 < P.Length; i += 3) pts.Add((P[i], P[i + 1], P[i + 2], pj, i / 3));
            }
            double ymin = pts.Min(p => p.Y), ymax = pts.Max(p => p.Y);
            int[] nearest = Nearest(pts, sculpt, out double worst, out List<int>?[] tied);
            if (!(worst < 1e-3 * (ymax - ymin))) throw new InvalidOperationException($"{h.Name}: rig vertex not on the model: {worst}");

            // --rigid-head-band: Retro's height and its Head_1 joint (Retro's own coordinates)
            double rzMin = Double.PositiveInfinity, rzMax = Double.NegativeInfinity;
            for (int i = 2; i < m.Positions.Length; i += 3) { rzMin = Math.Min(rzMin, m.Positions[i]); rzMax = Math.Max(rzMax, m.Positions[i]); }
            Cinf.Bone? headJ = skel.Bones.Values.LastOrDefault(b => b.Name == "Head_1");
            var bandSet = new HashSet<string>(StringComparer.Ordinal) { "Head_1", "Neck_1", "Collar" };
            int banded = 0;
            var own = new string[sculpt.Count];
            var soft = new bool[sculpt.Count];
            // a model point's bone: its part's (a joined part), else the DS bone Retro's skin weights most there
            (string Own, bool Soft, bool Band) OwnerOf(int point)
            {
                var pt = pts[point];
                if (pt.Part > 0) return (conv.Parts[pt.Part].Owner ?? "", false, false);
                var w = new double[dsn.Length];
                if (pt.Index < skin.Count)
                {
                    (uint Bone, float Weight)[] ws = skin[pt.Index];
                    for (int k = 0; k < ws.Length && k < 4; k++)
                    {
                        if (!bix.TryGetValue(ws[k].Bone, out int b)) continue;
                        w[Array.IndexOf(dsn, dsb[bn[b]])] += ws[k].Weight;
                    }
                }
                int best = 0;
                double sum = 0;
                for (int k = 0; k < w.Length; k++)
                {
                    if (w[k] > w[best]) best = k;
                    sum += w[k];
                }
                if (h.RigidHeadBand && pt.Index < skin.Count && headJ != null)
                {
                    var rb = new HashSet<string>(StringComparer.Ordinal);
                    (uint Bone, float Weight)[] ws = skin[pt.Index];
                    for (int k = 0; k < ws.Length && k < 4; k++) if (bix.TryGetValue(ws[k].Bone, out int b) && ws[k].Weight > 0.01) rb.Add(bn[b]);
                    int pi3 = 3 * pt.Index;
                    double dx = m.Positions[pi3] - headJ.X, dy = m.Positions[pi3 + 1] - headJ.Y, dz = m.Positions[pi3 + 2] - headJ.Z;
                    if (rb.Count > 1 && rb.Contains("Head_1") && rb.IsSubsetOf(bandSet) && Math.Sqrt(dx * dx + dy * dy + dz * dz) < 0.12 * (rzMax - rzMin))
                    {
                        return ("Head_1", false, true);
                    }
                }
                return (dsn[best], w[best] / Math.Max(sum, 1e-9) < 0.9, false);
            }
            // a rig vertex on coincident model points of different bones (a seam where Retro split the mesh) takes the
            // lowest-index point; the reference converter's KD-tree took whichever its tree layout reached first, so those
            // vertices are logged for the golden check
            var coincident = new List<string>();
            for (int v = 0; v < sculpt.Count; v++)
            {
                bool band;
                (own[v], soft[v], band) = OwnerOf(nearest[v]);
                if (band) banded++;
                if (tied[v] is List<int> tie)
                {
                    string[] alts = tie.Select(i => OwnerOf(i).Own).Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToArray();
                    if (alts.Length > 1) coincident.Add($"{v} {String.Join("/", alts)}");
                }
            }
            if (coincident.Count > 0)
            {
                log.Add($"{coincident.Count} rig vertices on coincident model points of different bones (lowest index taken): {String.Join(", ", coincident)}");
            }

            // stray points: a tiny island (<= 4 points joined by mesh edges) of a bone none of its neighbours has takes the
            // bone its neighbours have
            var nb = new List<int>[sculpt.Count];
            for (int i = 0; i < nb.Length; i++) nb[i] = new List<int>();
            void Link(int a, int b)
            {
                if (!nb[a].Contains(b)) nb[a].Add(b);
            }
            foreach ((int a, int b, int c) in tris)
            {
                Link(a, b); Link(a, c); Link(b, a); Link(b, c); Link(c, a); Link(c, b);
            }
            var seenV = new bool[sculpt.Count];
            var strays = new Dictionary<(string, string), int>();
            int ties = 0;
            for (int v0 = 0; v0 < sculpt.Count; v0++)
            {
                if (seenV[v0]) continue;
                var isl = new List<int> { v0 };
                var stack = new Stack<int>();
                stack.Push(v0);
                seenV[v0] = true;
                while (stack.Count > 0)
                {
                    int v = stack.Pop();
                    foreach (int u in nb[v])
                    {
                        if (!seenV[u] && own[u] == own[v0])
                        {
                            seenV[u] = true;
                            isl.Add(u);
                            stack.Push(u);
                        }
                    }
                }
                if (isl.Count > 4) continue;
                var rim = new List<string>();
                foreach (int v in isl) foreach (int u in nb[v]) if (own[u] != own[v0]) rim.Add(own[u]);
                if (rim.Count == 0) continue;
                // the most common neighbour bone (a tie, which the reference broke in its set order, goes to the first seen)
                var counts = rim.GroupBy(x => x).Select(g => (Bone: g.Key, N: g.Count())).ToList();
                int top = counts.Max(c => c.N);
                if (counts.Count(c => c.N == top) > 1) ties++;
                string to = counts.First(c => c.N == top).Bone, was = own[v0];
                foreach (int v in isl)
                {
                    own[v] = to;
                    soft[v] = false;
                }
                strays[(was, to)] = strays.GetValueOrDefault((was, to)) + isl.Count;
            }
            if (strays.Count > 0)
            {
                log.Add($"{strays.Values.Sum()} stray points joined their neighbours' bone: "
                    + String.Join(", ", strays.OrderByDescending(x => x.Value).Select(x => $"{x.Key.Item1}->{x.Key.Item2} {x.Value}"))
                    + (ties > 0 ? $" ({ties} ties)" : ""));
            }

            var sb = new StringBuilder();
            sb.Append($"# Retro's own skin of {h.Char} (the owner's Prime disc -- study only), exactly: the mesh IS Retro's\n");
            sb.Append("exact\n").Append($"vertices {sculpt.Count}\n");
            sb.Append("seam Spine_2 blend 0.06\nseam Spine_1 blend 0.06\nseam Pelvis blend 0.04\n");
            void Runs(string tag, IReadOnlyList<string> seq)
            {
                int i = 0;
                while (i < seq.Count)
                {
                    int j = i;
                    while (j < seq.Count && seq[j] == seq[i]) j++;
                    sb.Append(tag).Append(' ').Append(seq[i]).Append(' ').Append(j - i).Append('\n');
                    i = j;
                }
            }
            Runs("run", own);
            Runs("soft", soft.Select(x => x ? "1" : "0").ToList());
            if (banded > 0) log.Add($"{banded} helmet/collar band points made solid on the helmet");
            log.Add($"{h.Stem}.retro.txt: {sculpt.Count} vertices, {soft.Count(x => x)} soft");
            return sb.ToString();
        }

        // the nearest model position to every rig vertex (a uniform grid; exact distances in double), ties -> the lowest
        // index; tied: every point at exactly that distance, where there is more than one
        static int[] Nearest(List<(double X, double Y, double Z, int Part, int Index)> pts, IReadOnlyList<System.Numerics.Vector3> q, out double worst,
            out List<int>?[] tied)
        {
            double minX = pts.Min(p => p.X), minY = pts.Min(p => p.Y), minZ = pts.Min(p => p.Z);
            double span = Math.Max(pts.Max(p => p.X) - minX, Math.Max(pts.Max(p => p.Y) - minY, pts.Max(p => p.Z) - minZ));
            double cell = Math.Max(span / 128, 1e-9);
            (long, long, long) Key(double x, double y, double z) => ((long)Math.Floor((x - minX) / cell), (long)Math.Floor((y - minY) / cell), (long)Math.Floor((z - minZ) / cell));
            var grid = new Dictionary<(long, long, long), List<int>>();
            for (int i = 0; i < pts.Count; i++)
            {
                var k = Key(pts[i].X, pts[i].Y, pts[i].Z);
                if (!grid.TryGetValue(k, out List<int>? l)) grid[k] = l = new List<int>();
                l.Add(i);
            }
            var r = new int[q.Count];
            tied = new List<int>?[q.Count];
            worst = 0;
            var at = new List<int>();
            for (int v = 0; v < q.Count; v++)
            {
                double x = q[v].X, y = q[v].Y, z = q[v].Z;
                var (kx, ky, kz) = Key(x, y, z);
                int best = -1;
                double bd = Double.PositiveInfinity;
                at.Clear();
                for (int ring = 0; ; ring++)
                {
                    for (long a = kx - ring; a <= kx + ring; a++)
                    for (long b = ky - ring; b <= ky + ring; b++)
                    for (long c = kz - ring; c <= kz + ring; c++)
                    {
                        if (Math.Max(Math.Abs(a - kx), Math.Max(Math.Abs(b - ky), Math.Abs(c - kz))) != ring) continue;
                        if (!grid.TryGetValue((a, b, c), out List<int>? l)) continue;
                        foreach (int i in l)
                        {
                            double dx = pts[i].X - x, dy = pts[i].Y - y, dz = pts[i].Z - z;
                            double d = dx * dx + dy * dy + dz * dz;
                            if (d < bd) at.Clear();
                            if (d <= bd) at.Add(i);
                            if (d < bd || (d == bd && i < best))
                            {
                                bd = d;
                                best = i;
                            }
                        }
                    }
                    // every point outside the searched cube is at least `ring * cell` away
                    if (best >= 0 && Math.Sqrt(bd) <= ring * cell) break;
                    if (ring > 256) break;
                }
                r[v] = best;
                if (at.Count > 1) tied[v] = new List<int>(at);
                worst = Math.Max(worst, Math.Sqrt(bd));
            }
            return r;
        }
    }
}
