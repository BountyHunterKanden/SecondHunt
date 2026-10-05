using System;
using System.Collections.Generic;
using System.IO;

namespace MphRecomp.Import.Retro3
{
    // A Metroid Prime 3 skin ("SKIN", version 2): Prime 1's CSKR groups -- (bone id, weight) lists, each shared by the
    // next run of vertices -- behind a 12-byte header ("SKIN", version, group count). Port of prime3_char.py's skin3.
    // The groups do NOT follow the position array: they cover the model's distinct (position, normal, uv0) vertices in
    // Cmdl3.SkinOrder (sorted by position, ties in the order the display lists first send them). Each position takes
    // its first copy's weights; where a position's copies disagree (a handful, at weight-group hand-overs: Retro keeps
    // split copies a little out of that order) it takes the copy whose main bone most of its mesh neighbours share.
    public static class Skin3
    {
        // the groups in file order, one entry per vertex they cover
        public static List<(uint Bone, float Weight)[]> Groups(byte[] s)
        {
            if (s.Length < 12 || s[0] != (byte)'S' || s[1] != (byte)'K' || s[2] != (byte)'I' || s[3] != (byte)'N')
            {
                throw new InvalidDataException("not a SKIN");
            }
            var per = new List<(uint, float)[]>();
            int o = 12;
            int ng = checked((int)Be.U32(s, 8));
            for (int g = 0; g < ng; g++)
            {
                int nw = checked((int)Be.U32(s, o)); o += 4;
                var ws = new (uint, float)[nw];
                for (int i = 0; i < nw; i++) ws[i] = (Be.U32(s, o + 8 * i), Be.F32(s, o + 8 * i + 4));
                o += 8 * nw;
                int nv = checked((int)Be.U32(s, o)); o += 4;
                for (int v = 0; v < nv; v++) per.Add(ws);
            }
            return per;
        }

        static readonly (uint, float)[] Default = { (0u, 1.0f) };

        static bool Same((uint Bone, float Weight)[] a, (uint Bone, float Weight)[] b)
        {
            if (ReferenceEquals(a, b)) return true;
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++)
            {
                if (a[i].Bone != b[i].Bone || !(a[i].Weight == b[i].Weight)) return false;
            }
            return true;
        }

        // how many different weight lists a position's copies carry (Python's len({tuple(w) for w in cands[p]}))
        static int Distinct(List<(uint Bone, float Weight)[]> c)
        {
            var seen = new List<(uint, float)[]>();
            foreach ((uint, float)[] w in c)
            {
                bool dup = false;
                foreach ((uint, float)[] x in seen) if (Same(w, x)) { dup = true; break; }
                if (!dup) seen.Add(w);
            }
            return seen.Count;
        }

        // the bone of the (first) largest weight
        static uint Top((uint Bone, float Weight)[] ws)
        {
            if (ws.Length == 0) throw new InvalidDataException("an empty weight list");
            int best = 0;
            for (int i = 1; i < ws.Length; i++) if (ws[i].Weight > ws[best].Weight) best = i;
            return ws[best].Bone;
        }

        // per position (n of them): its (bone, weight) list. order: Cmdl3.SkinOrder (null = the groups in position order,
        // as Prime 1 lays them); triPositions: the triangles' position indices (Cmdl3.TrianglePositions) for the vote
        public static List<(uint Bone, float Weight)[]> Load(byte[] s, int n, IReadOnlyList<(int P, int N, int T0)>? order = null, int[]? triPositions = null)
        {
            List<(uint Bone, float Weight)[]> per = Groups(s);
            if (order == null) return per.GetRange(0, Math.Min(n, per.Count));
            if (per.Count != order.Count)
            {
                throw new InvalidDataException($"skin covers {per.Count} vertices, the display lists send {order.Count} distinct ones");
            }
            var cands = new List<(uint, float)[]>[n];
            for (int i = 0; i < n; i++) cands[i] = new List<(uint, float)[]>();
            for (int i = 0; i < order.Count; i++) cands[order[i].P].Add(per[i]);
            var outp = new List<(uint Bone, float Weight)[]>(n);
            for (int i = 0; i < n; i++) outp.Add(cands[i].Count > 0 ? cands[i][0] : Default);
            if (triPositions != null)
            {
                var nb = new HashSet<int>[n];
                for (int i = 0; i < n; i++) nb[i] = new HashSet<int>();
                for (int t = 0; t + 2 < triPositions.Length; t += 3)
                {
                    int a = triPositions[t], b = triPositions[t + 1], c = triPositions[t + 2];
                    nb[a].Add(b); nb[a].Add(c);
                    nb[b].Add(a); nb[b].Add(c);
                    nb[c].Add(a); nb[c].Add(b);
                }
                var distinct = new int[n];
                for (int i = 0; i < n; i++) distinct[i] = Distinct(cands[i]);
                for (int p = 0; p < n; p++)
                {
                    if (distinct[p] <= 1) continue;
                    var votes = new List<uint>();
                    foreach (int q in nb[p]) if (distinct[q] <= 1) votes.Add(Top(outp[q]));
                    (uint, float)[] best = cands[p][0];
                    int bestScore = -1;
                    foreach ((uint, float)[] ws in cands[p])
                    {
                        uint top = Top(ws);
                        int score = 0;
                        foreach (uint v in votes) if (v == top) score++;
                        if (score > bestScore)
                        {
                            best = ws; bestScore = score;
                        }
                    }
                    outp[p] = best;
                }
            }
            return outp;
        }
    }
}
