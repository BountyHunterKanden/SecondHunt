using System;
using System.Collections.Generic;
using System.Text;

namespace MphRecomp.Import.Retro3
{
    // A Metroid Prime 3 character (CHAR): the model / skin / skeleton ids it names (prime3_model.py's char_ids) and its
    // animation set (prime3_anim.py's char_animations). The CHAR header (PAS database, particles, event sets) is skipped
    // by locating the animation list itself: u32 count, then count x (name string, meta-anim), every Play naming a
    // known ANIM. Meta-anims (PrimeWorldEditor CMetaAnimFactory numbering, MIT, AxioDL): 0 Play (u64 ANIM, u32 prim
    // id, name, f32, u32), 1 Blend / 2 PhaseBlend (meta, meta, f32, u8), 3 Random (u32 n, n x (meta, u32 weight)),
    // 4 Sequence (u32 n, n x meta).
    public static class Char3
    {
        // a meta-animation tree: "play" leaves (an ANIM id), "blend" / "phaseblend" (two children, a weight), "random"
        // (children with weights), "seq" (children in order)
        public sealed class Meta
        {
            public string Kind = "";
            public ulong AnimId;
            public uint PrimId;
            public string PrimName = "";
            public float BlendWeight;
            public readonly List<Meta> Children = new();
            public readonly List<uint> Weights = new();
        }

        // the character's name and every CMDL / CSKR / CINF id in the 120 bytes after it (typeOf: a resource's type)
        public static (string Name, List<(string Type, ulong Id)> Ids) CharIds(byte[] b, Func<ulong, string?> typeOf)
        {
            string name = Be.CString(b, 2, out int e);
            int o = e + 1;
            var ids = new List<(string, ulong)>();
            for (int k = o; k < Math.Min(b.Length - 8, o + 120); k++)
            {
                ulong v = Pak3.U64(b, k);
                string? t = typeOf(v);
                if (t is "CMDL" or "CSKR" or "CINF") ids.Add((t, v));
            }
            return (name, ids);
        }

        // prime3_anim._cstr: a non-empty printable ASCII name ending in a NUL
        static bool TryName(byte[] b, ref int o, out string s)
        {
            s = "";
            if (o > b.Length) return false;
            int e = Array.IndexOf(b, (byte)0, o);
            if (e < 0 || e == o) return false;
            for (int i = o; i < e; i++) if (b[i] < 0x20 || b[i] > 0x7E) return false;
            s = Encoding.Latin1.GetString(b, o, e - o);
            o = e + 1;
            return true;
        }

        static bool Has(byte[] b, int o, int n) => o >= 0 && (long)o + n <= b.Length;

        static bool TryMeta(byte[] b, ref int o, Func<ulong, bool> isAnim, int depth, out Meta m)
        {
            m = new Meta();
            if (depth > 8 || !Has(b, o, 4)) return false;
            uint t = Be.U32(b, o); o += 4;
            if (t == 0)
            {
                if (!Has(b, o, 8)) return false;
                ulong aid = Pak3.U64(b, o);
                if (!isAnim(aid) || !Has(b, o, 12)) return false;
                uint pid = Be.U32(b, o + 8);
                int p = o + 12;
                if (!TryName(b, ref p, out string nm)) return false;
                o = p + 8;
                m.Kind = "play"; m.AnimId = aid; m.PrimName = nm; m.PrimId = pid;
                return true;
            }
            if (t is 1 or 2)
            {
                if (!TryMeta(b, ref o, isAnim, depth + 1, out Meta a) || !TryMeta(b, ref o, isAnim, depth + 1, out Meta c)) return false;
                if (!Has(b, o, 4)) return false;
                m.BlendWeight = Be.F32(b, o); o += 5;
                m.Kind = t == 1 ? "blend" : "phaseblend";
                m.Children.Add(a); m.Children.Add(c);
                return true;
            }
            if (t is 3 or 4)
            {
                if (!Has(b, o, 4)) return false;
                uint n = Be.U32(b, o); o += 4;
                if (!(n > 0 && n < 64)) return false;
                for (int i = 0; i < n; i++)
                {
                    if (!TryMeta(b, ref o, isAnim, depth + 1, out Meta x)) return false;
                    m.Children.Add(x);
                    if (t == 3)
                    {
                        if (!Has(b, o, 4)) return false;
                        m.Weights.Add(Be.U32(b, o)); o += 4;
                    }
                }
                m.Kind = t == 3 ? "random" : "seq";
                return true;
            }
            return false;
        }

        // [(name, meta tree)] from the CHAR's animation set: the first offset where a whole list parses
        public static List<(string Name, Meta Anim)> Animations(byte[] c, Func<ulong, bool> isAnim)
        {
            for (int o = 0; o < c.Length - 8; o++)
            {
                uint n = Be.U32(c, o);
                if (!(n > 0 && n < 2000)) continue;
                int p = o + 4;
                var list = new List<(string, Meta)>();
                bool ok = true;
                for (int i = 0; i < n && ok; i++)
                {
                    ok = false;
                    if (TryName(c, ref p, out string nm) && TryMeta(c, ref p, isAnim, 0, out Meta m))
                    {
                        list.Add((nm, m));
                        ok = true;
                    }
                }
                if (ok) return list;
            }
            throw new System.IO.InvalidDataException("no animation list found");
        }

        // the Play primitives under a meta-anim, in order
        public static List<Meta> Prims(Meta m)
        {
            var r = new List<Meta>();
            void Walk(Meta x)
            {
                if (x.Kind == "play") r.Add(x);
                else foreach (Meta ch in x.Children) Walk(ch);
            }
            Walk(m);
            return r;
        }

        public static string Describe(Meta m)
        {
            switch (m.Kind)
            {
            case "play": return $"{m.PrimName}[{m.AnimId:x16}]";
            case "blend":
            case "phaseblend": return $"{m.Kind}({Describe(m.Children[0])}, {Describe(m.Children[1])}, {m.BlendWeight:F2})";
            case "random":
                {
                    var parts = new List<string>();
                    for (int i = 0; i < m.Children.Count; i++) parts.Add($"{Describe(m.Children[i])}:{m.Weights[i]}");
                    return "random(" + String.Join(", ", parts) + ")";
                }
            default:
                {
                    var parts = new List<string>();
                    foreach (Meta ch in m.Children) parts.Add(Describe(ch));
                    return "seq(" + String.Join(", ", parts) + ")";
                }
            }
        }
    }
}
