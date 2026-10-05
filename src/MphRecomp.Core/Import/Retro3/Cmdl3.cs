using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using MphRecomp.Import.Retro;

namespace MphRecomp.Import.Retro3
{
    // A Metroid Prime 3 model (CMDL version 5). Echoes' section layout -- material set(s), positions, normals, colours,
    // float UVs, [short UVs], the surface table, one section per surface with its GX display list -- but Prime 3's own
    // materials: a list of texture PASSES (DIFF/"CLR " diffuse, INCA glow, RFLV/RFLD reflection mask + map, TRAN, XRAY,
    // BLO* bloom, ...) plus colour and value chunks, instead of a stored TEV program (PrimeWorldEditor
    // CMaterialLoader::ReadCorruptionMaterial, MIT). Port of prime3_model.py's read_material and of prime3_gun.py's
    // FIXED load_model: a model with several material sets keeps them either one section each, or -- small models with
    // several looks (the gun's 5 beams, varia_ball) -- all packed one after another in the first section, the geometry
    // right after it; packed is decided from the section count (1 materials + 4 arrays [+ short UVs] + 1 surface table
    // + the surfaces == the block count).
    // SkinOrder: every distinct (position, normal, uv0) vertex the display lists send, sorted by position index (ties:
    // the order they are first sent) -- the order Prime 3's SKIN weighs them in (Skin3).
    public sealed class Cmdl3
    {
        public sealed class Pass
        {
            public string Type = "";      // "CLR ", "INCA", "RFLV", "RFLD", "TRAN", "XRAY", "BLOI", ...
            public uint Settings;
            public ulong Tex;               // TXTR id (ffffffffffffffff = none)
            public uint UvSource;
            public readonly List<Cmdl.UvAnim> Anims = new();   // zero or one
            public ushort AnimUnknown;
        }

        public sealed class Material
        {
            public uint Flags;
            public uint VertexAttributes;
            public readonly List<Pass> Passes = new();
            public readonly List<(string Type, byte[] Rgba)> Colors = new();   // "CLR " chunks in order (a repeated type: the last one counts)
            public readonly List<(string Type, uint Value)> Ints = new();     // "INT " chunks in order

            public uint? Int(string type)
            {
                uint? v = null;
                foreach ((string t, uint x) in Ints) if (t == type) v = x;
                return v;
            }
        }

        public uint Version, Flags;
        public int MaterialSets;
        public bool Packed;
        public Material[] Materials = Array.Empty<Material>();
        public float[] Positions = Array.Empty<float>();   // xyz
        public float[] Normals = Array.Empty<float>();     // xyz (shorts / 32768 when flags & 2)
        public float[] Uv0 = Array.Empty<float>();         // st
        public Cmdl.Corner[] Triangles = Array.Empty<Cmdl.Corner>();   // 3 corners per triangle
        public int[] TriangleMaterial = Array.Empty<int>();
        public List<(int P, int N, int T0)> SkinOrder = new();

        public int PositionCount => Positions.Length / 3;
        public int TriangleCount => TriangleMaterial.Length;

        static string Tag(byte[] b, int o) => Encoding.Latin1.GetString(b, o, 4);

        public static Material ReadMaterial(byte[] b, int o, int end)
        {
            var m = new Material();
            m.Flags = Be.U32(b, o); o += 4 + 8;
            m.VertexAttributes = Be.U32(b, o); o += 4 + 12;
            while (o < end)
            {
                string t = Tag(b, o); o += 4;
                if (t == "END ") break;
                if (t == "PASS")
                {
                    int size = checked((int)Be.U32(b, o));
                    int next = o + 4 + size;
                    var p = new Pass
                    {
                        Type = Tag(b, o + 4), Settings = Be.U32(b, o + 8), Tex = Pak3.U64(b, o + 12), UvSource = Be.U32(b, o + 20),
                    };
                    uint animSize = Be.U32(b, o + 24);
                    if (animSize != 0)
                    {
                        // (u16 unknown, u16 pad?) then the mode and its floats
                        int ao = o + 28;
                        p.AnimUnknown = Be.U16(b, ao);
                        uint mode = Be.U32(b, ao + 4);
                        int nf = mode switch { 2 => 4, 3 => 2, 4 => 4, 5 => 4, 7 => 2, 8 => 9, 10 => 4, _ => 0 };
                        var prm = new double[nf];
                        for (int k = 0; k < nf; k++) prm[k] = Be.F32(b, ao + 8 + 4 * k);
                        p.Anims.Add(new Cmdl.UvAnim { Mode = mode, Params = prm });
                    }
                    m.Passes.Add(p);
                    o = next;
                }
                else if (t == "CLR ")
                {
                    m.Colors.Add((Tag(b, o), Be.Slice(b, o + 4, 4)));
                    o += 8;
                }
                else if (t == "INT ")
                {
                    m.Ints.Add((Tag(b, o), Be.U32(b, o + 4)));
                    o += 8;
                }
                else
                {
                    throw new InvalidDataException($"material chunk '{t}' at {o - 4:x}");
                }
            }
            return m;
        }

        // M: the CMDL's bytes; matSet 0 = the model's own look
        public static Cmdl3 Load(byte[] M, int matSet = 0)
        {
            if (Be.U32(M, 0) != 0xDEADBABE || Be.U32(M, 4) != 5) throw new InvalidDataException($"not a version 5 CMDL ({Be.U32(M, 4)})");
            var model = new Cmdl3 { Version = Be.U32(M, 4), Flags = Be.U32(M, 8) };
            int blocks = checked((int)Be.U32(M, 0x24)), matsets = checked((int)Be.U32(M, 0x28));
            model.MaterialSets = matsets;
            var sec = new (int Start, int End)[blocks];
            int o = (0x2C + 4 * blocks + 31) / 32 * 32;
            for (int i = 0; i < blocks; i++)
            {
                int s = checked((int)Be.U32(M, 0x2C + 4 * i));
                sec[i] = (o, o + s);
                o += s;
            }
            int table = 5 + ((model.Flags & 4) != 0 ? 1 : 0);
            bool packed = matsets > 1 && table < blocks && Be.U32(M, sec[table].Start) + table + 1 == blocks;
            model.Packed = packed;
            int want = Math.Min(matSet, matsets - 1);
            int setIndex = packed ? 0 : want;
            if (setIndex < 0) setIndex += blocks;   // Python's negative index
            o = sec[setIndex].Start;
            for (int k = 0; k < (packed ? want : 0); k++)
            {
                // skip the sets before the one wanted
                int n_ = checked((int)Be.U32(M, o)); o += 4;
                for (int i = 0; i < n_; i++) o += 4 + checked((int)Be.U32(M, o));
            }
            int nm = checked((int)Be.U32(M, o)); o += 4;
            model.Materials = new Material[nm];
            for (int i = 0; i < nm; i++)
            {
                int size = checked((int)Be.U32(M, o));
                model.Materials[i] = ReadMaterial(M, o + 4, o + 4 + size);
                o += 4 + size;
            }
            int si = packed ? 1 : matsets;
            model.Positions = Floats(M, sec[si], 3);
            model.Normals = (model.Flags & 2) != 0 ? Shorts(M, sec[si + 1], 3) : Floats(M, sec[si + 1], 3);
            model.Uv0 = Floats(M, sec[si + 3], 2);
            int kk = si + 4;
            if ((model.Flags & 4) != 0) kk++;
            int nsurf = checked((int)Be.U32(M, sec[kk].Start));
            kk++;
            var tris = new List<Cmdl.Corner>();
            var tmat = new List<int>();
            var verts = new List<Cmdl.Corner>();
            var first = new Dictionary<(int, int, int), int>();
            var firstOrder = new List<(int P, int N, int T0)>();
            for (int s = 0; s < nsurf; s++)
            {
                (int so, int e) = sec[kk + s];
                int mat = checked((int)Be.U32(M, so + 12));
                int extra = checked((int)Be.U32(M, so + 0x1C));
                o = so + 0x2C + 4 + extra;
                o = so + (o - so + 31) / 32 * 32;
                uint vd = model.Materials[mat].VertexAttributes;
                while (o < e)
                {
                    int op = M[o]; o++;
                    if (op == 0) break;
                    int prim = op & 0xF8;
                    int n = Be.U16(M, o); o += 2;
                    verts.Clear();
                    for (int v = 0; v < n; v++)
                    {
                        for (int bit = 0; bit < 8; bit++)
                        {
                            if ((vd & (0x01000000u << bit)) != 0) o++;
                        }
                        var c = new Cmdl.Corner { T0 = -1, T1 = -1, T2 = -1, T3 = -1 };
                        if ((vd & 3) != 0) { c.P = Be.U16(M, o); o += 2; }
                        if ((vd & 0xC) != 0) { c.N = Be.U16(M, o); o += 2; }
                        if ((vd & 0x30) != 0) o += 2;
                        if ((vd & 0xC0) != 0) o += 2;
                        for (int t = 0; t < 7; t++)
                        {
                            if ((vd & (0x300u << (2 * t))) != 0)
                            {
                                int idx = Be.U16(M, o); o += 2;
                                switch (t)
                                {
                                case 0: c.T0 = idx; break;
                                case 1: c.T1 = idx; break;
                                case 2: c.T2 = idx; break;
                                case 3: c.T3 = idx; break;
                                }
                            }
                        }
                        verts.Add(c);
                        var key = (c.P, c.N, c.T0);
                        if (first.TryAdd(key, first.Count)) firstOrder.Add(key);
                    }
                    void Face(int a, int b, int c)
                    {
                        tris.Add(verts[a]); tris.Add(verts[b]); tris.Add(verts[c]);
                        tmat.Add(mat);
                    }
                    if (prim == 0x90)
                    {
                        for (int i = 0; i < n - 2; i += 3) Face(i, i + 1, i + 2);
                    }
                    else if (prim == 0x98)
                    {
                        for (int i = 0; i < n - 2; i++)
                        {
                            if (i % 2 == 0) Face(i, i + 1, i + 2);
                            else Face(i + 1, i, i + 2);
                        }
                    }
                    else if (prim == 0xA0)
                    {
                        for (int i = 0; i < n - 2; i++) Face(0, i + 1, i + 2);
                    }
                    else
                    {
                        throw new InvalidDataException($"surface {s}: primitive {op:x2}");
                    }
                }
            }
            model.Triangles = tris.ToArray();
            model.TriangleMaterial = tmat.ToArray();
            // sorted by (position, first sent)
            var order = new List<(int P, int N, int T0)>(firstOrder);
            order.Sort((a, b) => a.P != b.P ? a.P.CompareTo(b.P) : first[a].CompareTo(first[b]));
            model.SkinOrder = order;
            return model;
        }

        // the triangles' position indices, 3 per triangle (the skin's mesh neighbours)
        public int[] TrianglePositions()
        {
            var r = new int[Triangles.Length];
            for (int i = 0; i < r.Length; i++) r[i] = Triangles[i].P;
            return r;
        }

        static float[] Floats(byte[] b, (int Start, int End) s, int per)
        {
            int count = (s.End - s.Start) / (4 * per) * per;
            var r = new float[count];
            for (int i = 0; i < count; i++) r[i] = Be.F32(b, s.Start + 4 * i);
            return r;
        }

        static float[] Shorts(byte[] b, (int Start, int End) s, int per)
        {
            int count = (s.End - s.Start) / (2 * per) * per;
            var r = new float[count];
            for (int i = 0; i < count; i++) r[i] = (float)(Be.I16(b, s.Start + 2 * i) / 32768.0);
            return r;
        }
    }
}
