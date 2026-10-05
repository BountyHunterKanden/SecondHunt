using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace MphRecomp.Import.Retro
{
    // A Retro animation character set (ANCS: Metroid Prime v6 characters, Echoes v10): its characters (model, skin,
    // skeleton, animation names, PAS states, particles, effects) and its animation set (named meta-animation trees whose
    // leaves play ANIM resources). Port of ancs_dump.py's parse (layout per PrimeWorldEditor's CAnimSetLoader, MIT, and
    // the Retro Modding Wiki ANCS page) and of prime_model.py's character() lookup.
    public sealed class Ancs
    {
        public sealed class Character
        {
            public uint Id;
            public int Version;
            public string Name = "";
            public uint Cmdl, Cskr, Cinf;
            public readonly List<(uint Index, string Name)> AnimNames = new();
            public readonly List<(uint Id, uint Parms, uint Anims)> PasStates = new();
            public readonly Dictionary<string, List<uint>> Particles = new();
            public readonly List<string> Aabbs = new();
            public readonly List<(string Name, List<(string Name, string Type, uint Id, string Bone)> Components)> Effects = new();
            public (uint, uint) Frozen;
            public List<uint>? UsedAnims;
            public int EchoesTailAt = -1;
        }

        // a meta-animation tree: "play" leaves (an ANIM id), "blend" / "phaseblend" (two children), "random" (children
        // with weights), "seq" (children in order)
        public sealed class MetaAnim
        {
            public string Kind = "";
            public uint AnimId, PrimId;
            public string PrimName = "";
            public readonly List<MetaAnim> Children = new();
            public readonly List<uint> Weights = new();
        }

        public sealed class AnimSet
        {
            public int Version;
            public readonly List<(string Name, MetaAnim Anim)> Anims = new();
            public readonly List<(uint Anim, uint Evnt)> Resources = new();
        }

        public readonly List<Character> Characters = new();
        public AnimSet? Set;

        sealed class Reader
        {
            public readonly byte[] B;
            public int O;
            public Reader(byte[] b, int o = 0) { B = b; O = o; }
            public byte U8() { byte v = B[O]; O++; return v; }
            public ushort U16() { ushort v = Be.U16(B, O); O += 2; return v; }
            public uint U32() { uint v = Be.U32(B, O); O += 4; return v; }
            public float F32() { float v = Be.F32(B, O); O += 4; return v; }
            public string S() { string v = Be.CString(B, O, out int e); O = e + 1; return v; }
            public void Skip(int n) => O += n;
            public string Tag() { string v = Encoding.Latin1.GetString(B, O, 4); O += 4; return v; }
        }

        // prime_model.py's character(): (CMDL, CSKR, CINF) of the named character, walking the char nodes by their
        // headers only (each next node is found by its index + version)
        public static (uint Cmdl, uint Cskr, uint Cinf) FindCharacter(byte[] a, string want)
        {
            int o = 4;
            int count = checked((int)Be.U32(a, o)); o += 4;
            Span<byte> next = stackalloc byte[6];
            for (int i = 0; i < count; i++)
            {
                ushort ver = Be.U16(a, o + 4);
                string name = Be.CString(a, o + 6, out int e);
                int o2 = e + 1;
                if (name == want)
                {
                    return (Be.U32(a, o2), Be.U32(a, o2 + 4), Be.U32(a, o2 + 8));
                }
                next[0] = (byte)((i + 1) >> 24); next[1] = (byte)((i + 1) >> 16); next[2] = (byte)((i + 1) >> 8); next[3] = (byte)(i + 1);
                next[4] = (byte)(ver >> 8); next[5] = (byte)ver;
                o = Be.Index(a, next, o2 + 12);
            }
            throw new KeyNotFoundException($"character {want}");
        }

        static List<(uint, uint, uint)> Pas(Reader r)
        {
            if (r.B.AsSpan(r.O, 4).SequenceEqual("PAS4"u8) == false)
            {
                throw new InvalidDataException($"expected PAS4 at {r.O:x}");
            }
            r.Skip(4);
            int n = checked((int)r.U32()); r.U32();
            var states = new List<(uint, uint, uint)>();
            for (int i = 0; i < n; i++)
            {
                uint sid = r.U32(), np = r.U32(), na = r.U32();
                int skip = 0;
                for (uint k = 0; k < np; k++)
                {
                    uint t = r.U32(); r.Skip(8);
                    if (t is 0 or 1 or 2 or 4) { r.Skip(8); skip += 4; }
                    else if (t == 3) { r.Skip(2); skip += 1; }
                }
                r.Skip(checked((int)((4 + skip) * na)));
                states.Add((sid, np, na));
            }
            return states;
        }

        static List<uint> U32List(Reader r)
        {
            uint n = r.U32();
            var l = new List<uint>();
            for (uint i = 0; i < n; i++) l.Add(r.U32());
            return l;
        }

        static Character Char(Reader r)
        {
            var c = new Character { Id = r.U32() };
            int ver = r.U16();
            c.Version = ver;
            c.Name = r.S();
            c.Cmdl = r.U32(); c.Cskr = r.U32(); c.Cinf = r.U32();
            uint n = r.U32();
            for (uint i = 0; i < n; i++)
            {
                uint idx = r.U32();
                if (ver < 10) r.S();
                c.AnimNames.Add((idx, r.S()));
            }
            c.PasStates.AddRange(Pas(r));
            c.Particles["PART"] = U32List(r);
            c.Particles["SWHC"] = U32List(r);
            if (ver >= 6) c.Particles["unk"] = U32List(r);
            c.Particles["ELSC"] = U32List(r);
            if (ver >= 10) c.Particles["SPSC"] = U32List(r);
            r.U32();
            if (ver >= 10) r.U32();
            n = r.U32();
            for (uint i = 0; i < n; i++)
            {
                c.Aabbs.Add(r.S());
                r.Skip(24);
            }
            n = r.U32();
            for (uint i = 0; i < n; i++)
            {
                string nm = r.S();
                uint nc = r.U32();
                var comps = new List<(string, string, uint, string)>();
                for (uint k = 0; k < nc; k++)
                {
                    string cn = r.S();
                    string typ = r.Tag();
                    uint pid = r.U32();
                    string bone = ver >= 10 ? r.U32().ToString() : r.S();
                    r.Skip(12);
                    comps.Add((cn, typ, pid, bone));
                }
                c.Effects.Add((nm, comps));
            }
            c.Frozen = (r.U32(), r.U32());
            if (ver >= 5) c.UsedAnims = U32List(r);
            if (ver >= 10)
            {
                // spatial primitives (Echoes): not decoded (PWE: an "Unknown" block)
                c.EchoesTailAt = r.O;
            }
            return c;
        }

        // Python's default recursion limit, less the frames below the tree (a garbage candidate in the Echoes scan fails
        // there with a RecursionError, caught like any other parse error)
        const int MaxDepth = 996;

        static MetaAnim Meta(Reader r, int depth = 0)
        {
            if (depth > MaxDepth) throw new InvalidDataException("meta anim tree too deep");
            uint t = r.U32();
            if (t == 0)
            {
                var m = new MetaAnim { Kind = "play", AnimId = r.U32(), PrimId = r.U32() };
                m.PrimName = r.S();
                r.F32(); r.U32();
                return m;
            }
            if (t is 1 or 2)
            {
                var m = new MetaAnim { Kind = t == 1 ? "blend" : "phaseblend" };
                m.Children.Add(Meta(r, depth + 1));
                m.Children.Add(Meta(r, depth + 1));
                r.F32(); r.U8();
                return m;
            }
            if (t == 3)
            {
                var m = new MetaAnim { Kind = "random" };
                uint n = r.U32();
                for (uint i = 0; i < n; i++)
                {
                    m.Children.Add(Meta(r, depth + 1));
                    m.Weights.Add(r.U32());
                }
                return m;
            }
            if (t == 4)
            {
                var m = new MetaAnim { Kind = "seq" };
                uint n = r.U32();
                for (uint i = 0; i < n; i++) m.Children.Add(Meta(r, depth + 1));
                return m;
            }
            throw new InvalidDataException($"meta anim type {t} at {r.O - 4:x}");
        }

        static void Trans(Reader r)
        {
            uint t = r.U32();
            if (t == 0) { Meta(r); return; }
            if (t is 1 or 2) { r.Skip(14); return; }
            if (t == 3) return;
            throw new InvalidDataException($"meta trans {t} at {r.O - 4:x}");
        }

        // the leaves of a meta-animation, in order
        public static List<MetaAnim> Prims(MetaAnim m)
        {
            var l = new List<MetaAnim>();
            void Walk(MetaAnim x)
            {
                if (x.Kind == "play") { l.Add(x); return; }
                foreach (MetaAnim c in x.Children) Walk(c);
            }
            Walk(m);
            return l;
        }

        static AnimSet ReadAnimSet(Reader r, bool echoes)
        {
            var set = new AnimSet { Version = r.U16() };
            int ver = set.Version;
            uint n = r.U32();
            for (uint i = 0; i < n; i++)
            {
                string nm = r.S();
                set.Anims.Add((nm, Meta(r)));
            }
            if (ver >= 2)
            {
                uint nt = r.U32();
                for (uint i = 0; i < nt; i++) { r.U32(); r.U32(); r.U32(); Trans(r); }
            }
            Trans(r);
            if (ver >= 3)
            {
                uint nb = r.U32();
                for (uint i = 0; i < nb; i++) { r.U32(); r.F32(); r.F32(); }
                r.F32(); r.F32();
            }
            if (ver >= 4)
            {
                uint nh = r.U32();
                for (uint i = 0; i < nh; i++) { r.U32(); Trans(r); }
            }
            if (!echoes)
            {
                uint nr = r.U32();
                for (uint i = 0; i < nr; i++) set.Resources.Add((r.U32(), r.U32()));
            }
            return set;
        }

        static bool IsAnimName(string s)
        {
            if (s.Length == 0) return false;
            foreach (char ch in s)
            {
                if (!(ch is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '_' or ' ')) return false;
            }
            return true;
        }

        public static Ancs Parse(byte[] b)
        {
            var ancs = new Ancs();
            var r = new Reader(b);
            if (r.U32() != 0x00010001) throw new InvalidDataException("not an ANCS");
            uint n = r.U32();
            bool echoes = false;
            Span<byte> next = stackalloc byte[6];
            for (uint i = 0; i < n; i++)
            {
                Character c = Char(r);
                echoes = c.Version >= 10;
                ancs.Characters.Add(c);
                if (echoes && i + 1 < n)
                {
                    // Echoes char tail (anim AABBs + spatial prims) not decoded: jump to the next char node
                    next[0] = (byte)((i + 1) >> 24); next[1] = (byte)((i + 1) >> 16); next[2] = (byte)((i + 1) >> 8); next[3] = (byte)(i + 1);
                    next[4] = (byte)(c.Version >> 8); next[5] = (byte)c.Version;
                    r.O = Be.Index(b, next, r.O);
                }
            }
            if (echoes)
            {
                // find the animation set: the first offset where it parses cleanly with plausible names
                for (int o = r.O; o < b.Length - 8; o++)
                {
                    if (b[o] != 0 || b[o + 1] is not (2 or 3 or 4 or 5)) continue;
                    AnimSet set;
                    try
                    {
                        set = ReadAnimSet(new Reader(b, o), true);
                    }
                    catch (Exception e) when (e is InvalidDataException or ArgumentOutOfRangeException or IndexOutOfRangeException or OverflowException)
                    {
                        continue;
                    }
                    if (set.Anims.Count > 0 && set.Anims.TrueForAll(a => IsAnimName(a.Name)))
                    {
                        ancs.Set = set;
                        return ancs;
                    }
                }
                return ancs;
            }
            ancs.Set = ReadAnimSet(r, false);
            return ancs;
        }
    }
}
