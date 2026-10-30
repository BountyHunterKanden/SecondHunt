using System;
using System.Collections.Generic;
using System.IO;

namespace MphRecomp.Import.Retro
{
    // A Retro model (CMDL, Metroid Prime v2 / Echoes v3+) in full: the chosen material set (textures, konst colours,
    // blend, TEV stages, texgens, UV animations), the vertex arrays and every surface's GX display list turned into
    // per-corner triangles. Port of prime_model.py's load_model / read_material (as PrimeWorldEditor's CModelLoader /
    // CMaterialLoader read them, MIT; Metaforce documents the bit fields).
    public sealed class Cmdl
    {
        // material flag bits (Metaforce MaterialSet)
        public const uint Konst = 0x8, DepthSort = 0x10, AlphaTest = 0x20, Reflect = 0x40, DepthWrite = 0x80, ReflectEye = 0x100,
            Occluder = 0x200, IndStage = 0x400, Lightmap = 0x800, ShortUv = 0x2000;

        public struct TevOp
        {
            public int Clamp, Op, Bias, Scale, Out;
            public static TevOp From(uint v) => new() { Clamp = (int)(v >> 8) & 1, Op = (int)v & 0xF, Bias = (int)(v >> 4) & 3, Scale = (int)(v >> 6) & 3, Out = (int)(v >> 9) & 3 };
        }

        public sealed class TevStage
        {
            public int[] ColorIn = new int[4], AlphaIn = new int[4];
            // every character stage reads 0x100 / 0x700: bit 8 = clamp, bits 9-10 = output register, the rest 0
            public TevOp ColorOp, AlphaOp;
            public int KAlpha, KColor, Ras;
            public uint RawColorOp, RawAlphaOp;
            public int Tex, TexCoord;   // 255 = no texture
        }

        public struct TexGen
        {
            public int Type, Src, Mtx, Post;
            public bool Normalize;
        }

        public sealed class UvAnim
        {
            public uint Mode;
            public double[] Params = Array.Empty<double>();
        }

        public sealed class Material
        {
            public uint Flags;
            public int[] Tex = Array.Empty<int>();          // indices into the set's texture list
            public uint[] TexIds = Array.Empty<uint>();     // ... resolved to TXTR ids
            public uint VertexAttributes;
            public uint[]? EchoesUnknown;
            public uint Group;
            public byte[][] Konst = Array.Empty<byte[]>();  // rgba
            public int BlendDst, BlendSrc;
            public uint? IndTex;
            public uint[] Channels = Array.Empty<uint>();
            public TevStage[] Stages = Array.Empty<TevStage>();
            public TexGen[] TexGens = Array.Empty<TexGen>();
            public UvAnim[] UvAnims = Array.Empty<UvAnim>();
        }

        // a triangle corner: position, normal and uv0..uv3 indices (-1 = none)
        public struct Corner
        {
            public int P, N, T0, T1, T2, T3;
        }

        public uint Version, Flags;
        public int MaterialSets;
        public bool Echoes => Version >= 3;
        public Material[] Materials = Array.Empty<Material>();
        public float[] Positions = Array.Empty<float>();   // xyz
        public float[] Normals = Array.Empty<float>();     // xyz (shorts / 32768 when flags & 2)
        public uint[] Colors = Array.Empty<uint>();
        public float[] Uv0 = Array.Empty<float>();         // st, the float UV array
        public float[]? ShortUvs;                          // st, the short UV array (flags & 4), shorts / 32768
        public Corner[] Triangles = Array.Empty<Corner>(); // 3 corners per triangle
        public int[] TriangleMaterial = Array.Empty<int>();
        public List<(uint Bone, float Weight)[]>? Skin;    // per position (CSKR)

        public int PositionCount => Positions.Length / 3;
        public int TriangleCount => TriangleMaterial.Length;

        static TevStage[] ReadStages(byte[] b, ref int o)
        {
            int n = checked((int)Be.U32(b, o)); o += 4;
            var st = new TevStage[n];
            for (int i = 0; i < n; i++)
            {
                uint ci = Be.U32(b, o), ai = Be.U32(b, o + 4), co = Be.U32(b, o + 8), ao = Be.U32(b, o + 12);
                var s = new TevStage
                {
                    ColorOp = TevOp.From(co), AlphaOp = TevOp.From(ao), RawColorOp = co, RawAlphaOp = ao,
                    KAlpha = b[o + 17], KColor = b[o + 18], Ras = b[o + 19],
                };
                for (int k = 0; k < 4; k++)
                {
                    s.ColorIn[k] = (int)(ci >> (5 * k)) & 0x1F;
                    s.AlphaIn[k] = (int)(ai >> (5 * k)) & 0x1F;
                }
                st[i] = s;
                o += 20;
            }
            for (int i = 0; i < n; i++)
            {
                st[i].Tex = b[o + 2]; st[i].TexCoord = b[o + 3]; o += 4;
            }
            return st;
        }

        public static Material ReadMaterial(byte[] b, int o, int end, bool echoes)
        {
            var m = new Material();
            m.Flags = Be.U32(b, o); o += 4;
            int n = checked((int)Be.U32(b, o)); o += 4;
            m.Tex = new int[n];
            for (int i = 0; i < n; i++) m.Tex[i] = checked((int)Be.U32(b, o + 4 * i));
            o += 4 * n;
            m.VertexAttributes = Be.U32(b, o); o += 4;
            if (echoes)
            {
                m.EchoesUnknown = new[] { Be.U32(b, o), Be.U32(b, o + 4) };
                o += 8;
            }
            m.Group = Be.U32(b, o); o += 4;
            if ((m.Flags & Konst) != 0)
            {
                n = checked((int)Be.U32(b, o)); o += 4;
                m.Konst = new byte[n][];
                for (int i = 0; i < n; i++) m.Konst[i] = Be.Slice(b, o + 4 * i, 4);
                o += 4 * n;
            }
            m.BlendDst = Be.U16(b, o); m.BlendSrc = Be.U16(b, o + 2); o += 4;
            if ((m.Flags & IndStage) != 0)
            {
                m.IndTex = Be.U32(b, o); o += 4;
            }
            n = checked((int)Be.U32(b, o)); o += 4;
            m.Channels = new uint[n];
            for (int i = 0; i < n; i++) m.Channels[i] = Be.U32(b, o + 4 * i);
            o += 4 * n;
            m.Stages = ReadStages(b, ref o);
            n = checked((int)Be.U32(b, o)); o += 4;
            m.TexGens = new TexGen[n];
            for (int i = 0; i < n; i++)
            {
                uint g = Be.U32(b, o + 4 * i);
                m.TexGens[i] = new TexGen
                {
                    Type = (int)g & 0xF, Src = (int)(g >> 4) & 0x1F, Mtx = (int)(g >> 9) & 0x1F, Normalize = (g & 0x4000) != 0, Post = (int)(g >> 15) & 0x3F,
                };
            }
            o += 4 * n;
            int size = checked((int)Be.U32(b, o)); o += 4;
            int aEnd = o + size;
            n = checked((int)Be.U32(b, o)); o += 4;
            m.UvAnims = new UvAnim[n];
            for (int i = 0; i < n; i++)
            {
                uint mode = Be.U32(b, o); o += 4;
                int k = mode switch { 2 => 4, 3 => 2, 4 => 4, 5 => 4, 7 => 2, 8 => 9, _ => 0 };
                var p = new double[k];
                for (int j = 0; j < k; j++) p[j] = Be.F32(b, o + 4 * j);
                o += 4 * k;
                m.UvAnims[i] = new UvAnim { Mode = mode, Params = p };
            }
            if (o != aEnd) throw new InvalidDataException($"uv anim block ends at {o:x}, expected {aEnd:x}");
            if (o > end) throw new InvalidDataException($"material overran by {o - end}");
            return m;
        }

        // res: resource bytes by id; matSet 0 = the model's own (Prime 1's morph ball keeps Power / Varia / Gravity looks
        // as sets 0 / 1 / 2; Echoes' VariaArm keeps one set per suit)
        public static Cmdl Load(Func<uint, byte[]> res, uint modelId, uint? skinId = null, int matSet = 0)
        {
            byte[] M = res(modelId);
            if (Be.U32(M, 0) != 0xDEADBABE) throw new InvalidDataException($"{modelId:x8}: not a CMDL");
            var model = new Cmdl { Version = Be.U32(M, 4), Flags = Be.U32(M, 8) };
            bool echoes = model.Echoes;
            int blocks = checked((int)Be.U32(M, 0x24));
            int matsets = checked((int)Be.U32(M, 0x28));
            model.MaterialSets = matsets;
            var sec = new (int Start, int End)[blocks];
            int o = (0x2C + 4 * blocks + 31) / 32 * 32;
            for (int i = 0; i < blocks; i++)
            {
                int s = checked((int)Be.U32(M, 0x2C + 4 * i));
                sec[i] = (o, o + s);
                o += s;
            }
            // Python's sec[min(matset, matsets - 1)]: a negative index counts from the end
            int setIndex = Math.Min(matSet, matsets - 1);
            if (setIndex < 0) setIndex += blocks;
            o = sec[setIndex].Start;
            int nt = checked((int)Be.U32(M, o));
            var texIds = new uint[nt];
            for (int i = 0; i < nt; i++) texIds[i] = Be.U32(M, o + 4 + 4 * i);
            o += 4 + 4 * nt;
            int nm = checked((int)Be.U32(M, o)); o += 4;
            var ends = new int[nm];
            for (int i = 0; i < nm; i++) ends[i] = checked((int)Be.U32(M, o + 4 * i));
            o += 4 * nm;
            int bse = o;
            model.Materials = new Material[nm];
            for (int i = 0; i < nm; i++)
            {
                int mo = bse + (i > 0 ? ends[i - 1] : 0);
                Material m = ReadMaterial(M, mo, bse + ends[i], echoes);
                m.TexIds = new uint[m.Tex.Length];
                for (int t = 0; t < m.Tex.Length; t++) m.TexIds[t] = texIds[m.Tex[t]];
                model.Materials[i] = m;
            }
            int si = matsets;
            model.Positions = Floats(M, sec[si], 3);
            model.Normals = (model.Flags & 2) != 0 ? Shorts(M, sec[si + 1], 3) : Floats(M, sec[si + 1], 3);
            {
                (int s, int e) = sec[si + 2];
                model.Colors = new uint[(e - s) / 4];
                for (int i = 0; i < model.Colors.Length; i++) model.Colors[i] = Be.U32(M, s + 4 * i);
            }
            model.Uv0 = Floats(M, sec[si + 3], 2);
            int k = si + 4;
            if ((model.Flags & 4) != 0)
            {
                model.ShortUvs = Shorts(M, sec[k], 2);
                k++;
            }
            int nsurf = checked((int)Be.U32(M, sec[k].Start));
            k++;
            var tris = new List<Corner>();
            var tmat = new List<int>();
            var verts = new List<Corner>();
            for (int s = 0; s < nsurf; s++)
            {
                (int so, int e) = sec[k + s];
                // centre, material, mantissa + display list size + 2 pointers, extra size, reflection normal, [Echoes: 4], extra
                int mat = checked((int)Be.U32(M, so + 12));
                int extra = checked((int)Be.U32(M, so + 0x1C));
                o = so + 0x2C + (echoes ? 4 : 0) + extra;
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
                        var c = new Corner { T0 = -1, T1 = -1, T2 = -1, T3 = -1 };
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
            if (skinId is uint sk)
            {
                model.Skin = Cskr.Load(res(sk));
            }
            return model;
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
