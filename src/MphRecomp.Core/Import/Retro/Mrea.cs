using System;
using System.Collections.Generic;
using System.Text;

namespace MphRecomp.Import.Retro
{
    // An Echoes area (MREA v0x19): the section table unpacked from its LZO clusters, the area collision (triangles +
    // 64-bit material flags) and the script layers (objects with their property trees). Ported from the research
    // prototypes in extract_out/multiplayer/mp2 (mp2geo/mp2_mrea.py, mp2/scly.py), as PrimeWorldEditor's CAreaLoader /
    // CCollisionLoader / CScriptLoader read them (MIT). World geometry and lights come later (docs/MP2_MULTIPLAYER_IMPORT.md).
    public sealed class Mrea
    {
        public const uint Magic = 0xDEADBEEF, EchoesVersion = 0x19;

        // collision material bits (Retro Modding Wiki; Echoes keeps Prime's low word)
        public const ulong FlippedTri = 1UL << 24, Ceiling = 1UL << 29, Wall = 1UL << 30, Floor = 1UL << 31,
            JumpNotAllowed = 1UL << 58, SpiderBall = 1UL << 61;
        public enum Surface { Unknown = 0, Stone = 1, Metal = 2, Grass = 3, Ice = 4, Pillar = 5, MetalGrating = 6, Phazon = 7,
            Dirt = 8, SpMetal = 9, Glass = 10, Snow = 11, Fabric = 12, Halfpipe = 13, Sand = 17, ShootThru = 20, CameraThru = 21,
            Wood = 22, Organic = 23 }

        public float[] Transform = new float[12];       // area to world, 3x4 row-major (identity in every MP arena)
        public int ModelCount, LayerCount;
        public byte[][] Sections = Array.Empty<byte[]>();
        // section numbers of each block, -1 when absent
        public int GeometrySection, ScriptSection, GeneratedSection, CollisionSection, OctreeSection, LightSection,
            VisiSection, PathSection, PtlaSection, EgmcSection;

        public static Mrea Read(byte[] raw)
        {
            if (Be.U32(raw, 0) != Magic || Be.U32(raw, 4) != EchoesVersion)
            {
                throw new InvalidOperationException($"not an Echoes MREA ({Be.U32(raw, 0):x8} v{Be.U32(raw, 4):x})");
            }
            var a = new Mrea();
            for (int i = 0; i < 12; i++) a.Transform[i] = Be.F32(raw, 8 + 4 * i);
            a.ModelCount = (int)Be.U32(raw, 0x38);
            a.LayerCount = (int)Be.U32(raw, 0x3C);
            int sectionCount = (int)Be.U32(raw, 0x40);
            int Block(int i) => Be.I32(raw, 0x44 + 4 * i);
            a.GeometrySection = Block(0); a.ScriptSection = Block(1); a.GeneratedSection = Block(2); a.CollisionSection = Block(3);
            a.OctreeSection = Block(4); a.LightSection = Block(5); a.VisiSection = Block(6); a.PathSection = Block(7);
            a.PtlaSection = Block(9); a.EgmcSection = Block(10);
            int clusterCount = (int)Be.U32(raw, 0x70);
            int o = 0x80;
            var sizes = new int[sectionCount];
            for (int i = 0; i < sectionCount; i++) sizes[i] = (int)Be.U32(raw, o + 4 * i);
            o = Align32(o + 4 * sectionCount);
            int table = o;
            o = Align32(o + 16 * clusterCount);
            var data = new List<byte>();
            for (int i = 0; i < clusterCount; i++)
            {
                int decompSize = (int)Be.U32(raw, table + 16 * i + 4), compSize = (int)Be.U32(raw, table + 16 * i + 8);
                if (compSize == 0)
                {
                    data.AddRange(Be.Slice(raw, o, decompSize));
                    o += decompSize;
                }
                else
                {
                    // a compressed cluster is padded at its front to a 32-byte boundary
                    if (compSize % 32 != 0) o += 32 - compSize % 32;
                    data.AddRange(Pak.Segmented(Be.Slice(raw, o, compSize), 0, decompSize));
                    o += compSize;
                }
            }
            byte[] all = data.ToArray();
            a.Sections = new byte[sectionCount][];
            int p = 0;
            for (int i = 0; i < sectionCount; i++)
            {
                a.Sections[i] = Be.Slice(all, p, sizes[i]);
                p += sizes[i];
            }
            return a;
        }

        static int Align32(int o) => (o + 31) & ~31;

        // ---- collision ----

        public sealed class Collision
        {
            public float[] Vertices = Array.Empty<float>();   // xyz per vertex, area space (metres, Z up)
            public int[] Triangles = Array.Empty<int>();       // 3 vertex indices per triangle, wound so the normal follows the right-hand rule
            public ulong[] TriangleFlags = Array.Empty<ulong>();
            public int TriangleCount => Triangles.Length / 3;
            public static Surface SurfaceOf(ulong flags)
            {
                for (int i = 1; i < 24; i++)
                {
                    if ((flags >> i & 1) != 0 && Enum.IsDefined(typeof(Surface), i)) return (Surface)i;
                }
                return Surface.Unknown;
            }
        }

        public Collision ReadCollision()
        {
            byte[] s = Sections[CollisionSection];
            int o = 8;
            if (Be.U32(s, o) != 0xDEAFBABE) throw new InvalidOperationException("area collision: bad magic");
            o += 0x20;
            o += 8 + (int)Be.U32(s, o + 4);                 // octree (kind, size, body)
            int matCount = (int)Be.U32(s, o); o += 4;
            var mats = new ulong[matCount];
            for (int i = 0; i < matCount; i++) mats[i] = (ulong)Be.U32(s, o + 8 * i) << 32 | Be.U32(s, o + 8 * i + 4);
            o += 8 * matCount;
            o = SkipBytes(s, o);                             // vertex material indices
            o = SkipBytes(s, o);                             // edge material indices
            int triMatCount = (int)Be.U32(s, o); int triMats = o + 4; o += 4 + triMatCount;
            int edgeCount = (int)Be.U32(s, o); int edges = o + 4; o += 4 + 4 * edgeCount;
            int triIdxCount = (int)Be.U32(s, o); int tris = o + 4; o += 4 + 2 * triIdxCount;
            int unk = (int)Be.U32(s, o); o += 4 + 2 * unk;  // Echoes-only u16 array, purpose unknown
            int vertCount = (int)Be.U32(s, o); o += 4;
            var c = new Collision { Vertices = new float[vertCount * 3] };
            for (int i = 0; i < vertCount * 3; i++) c.Vertices[i] = Be.F32(s, o + 4 * i);
            int triCount = triIdxCount / 3;
            c.Triangles = new int[triCount * 3];
            c.TriangleFlags = new ulong[triCount];
            for (int t = 0; t < triCount; t++)
            {
                // a triangle names 3 edges: edge 0 gives two corners, edge 1 the third
                int e0 = Be.U16(s, tris + 6 * t), e1 = Be.U16(s, tris + 6 * t + 2);
                int v0 = Be.U16(s, edges + 4 * e0), v1 = Be.U16(s, edges + 4 * e0 + 2);
                int w0 = Be.U16(s, edges + 4 * e1), w1 = Be.U16(s, edges + 4 * e1 + 2);
                int v2 = w0 != v0 && w0 != v1 ? w0 : w1;
                ulong flags = mats[s[triMats + t]];
                if ((flags & FlippedTri) != 0) (v0, v1) = (v1, v0);
                c.Triangles[3 * t] = v0; c.Triangles[3 * t + 1] = v1; c.Triangles[3 * t + 2] = v2;
                c.TriangleFlags[t] = flags;
            }
            return c;
        }

        static int SkipBytes(byte[] s, int o) => o + 4 + (int)Be.U32(s, o);

        // ---- world geometry ----

        // every world model merged into one Cmdl (materials, positions, normals, both UV arrays, triangles). A lightmapped
        // material (flag 0x2000) reads texture slot 0 through the short UV array: those corners' T0 index ShortUvs.
        public Cmdl ReadGeometry()
        {
            byte[] set = Sections[GeometrySection];
            int nt = (int)Be.U32(set, 0);
            var texIds = new uint[nt];
            for (int i = 0; i < nt; i++) texIds[i] = Be.U32(set, 4 + 4 * i);
            int o = 4 + 4 * nt;
            int nm = (int)Be.U32(set, o); o += 4;
            var ends = new int[nm];
            for (int i = 0; i < nm; i++) ends[i] = (int)Be.U32(set, o + 4 * i);
            o += 4 * nm;
            var geo = new Cmdl { Version = 3, Materials = new Cmdl.Material[nm] };
            for (int i = 0; i < nm; i++)
            {
                Cmdl.Material m = Cmdl.ReadMaterial(set, o + (i > 0 ? ends[i - 1] : 0), o + ends[i], echoes: true);
                m.TexIds = new uint[m.Tex.Length];
                for (int t = 0; t < m.Tex.Length; t++) m.TexIds[t] = texIds[m.Tex[t]];
                geo.Materials[i] = m;
            }
            var pos = new List<float>(); var nrm = new List<float>(); var uv = new List<float>(); var suv = new List<float>();
            var tris = new List<Cmdl.Corner>(); var tmat = new List<int>();
            int k = GeometrySection + 1;
            for (int mi = 0; mi < ModelCount; mi++)
            {
                int pOff = pos.Count / 3, nOff = nrm.Count / 3, uOff = uv.Count / 2, sOff = suv.Count / 2;
                // header (visor flags, transform -- identity in the MP arenas -- AABB), positions, normals, colours, UVs, short UVs
                AddFloats(pos, Sections[k + 1], 3);
                AddShorts(nrm, Sections[k + 2], 3);
                AddFloats(uv, Sections[k + 4], 2);
                AddShorts(suv, Sections[k + 5], 2);
                int ns = (int)Be.U32(Sections[k + 6], 0);
                k += 7;
                var verts = new List<Cmdl.Corner>();
                for (int si = 0; si < ns; si++)
                {
                    byte[] s = Sections[k + si];
                    int mat = (int)Be.U32(s, 12);
                    int p = Align32(0x30 + (int)Be.U32(s, 0x1C));
                    uint vd = geo.Materials[mat].VertexAttributes;
                    bool shortUv = (geo.Materials[mat].Flags & 0x2000) != 0;
                    while (p < s.Length)
                    {
                        int op = s[p++];
                        if (op == 0) break;
                        int prim = op & 0xF8, n = Be.U16(s, p); p += 2;
                        verts.Clear();
                        for (int v = 0; v < n; v++)
                        {
                            for (int bit = 0; bit < 8; bit++)
                            {
                                if ((vd & (0x01000000u << bit)) != 0) p++;
                            }
                            var c = new Cmdl.Corner { T0 = -1, T1 = -1, T2 = -1, T3 = -1 };
                            if ((vd & 3) != 0) { c.P = pOff + Be.U16(s, p); p += 2; }
                            if ((vd & 0xC) != 0) { c.N = nOff + Be.U16(s, p); p += 2; }
                            if ((vd & 0x30) != 0) p += 2;
                            if ((vd & 0xC0) != 0) p += 2;
                            for (int t = 0; t < 7; t++)
                            {
                                if ((vd & (0x300u << (2 * t))) == 0) continue;
                                int idx = Be.U16(s, p); p += 2;
                                switch (t)
                                {
                                case 0: c.T0 = idx + (shortUv ? sOff : uOff); break;
                                case 1: c.T1 = idx + uOff; break;
                                case 2: c.T2 = idx + uOff; break;
                                case 3: c.T3 = idx + uOff; break;
                                }
                            }
                            verts.Add(c);
                        }
                        void Face(int a, int b, int c) { tris.Add(verts[a]); tris.Add(verts[b]); tris.Add(verts[c]); tmat.Add(mat); }
                        if (prim == 0x90) { for (int i = 0; i < n - 2; i += 3) Face(i, i + 1, i + 2); }
                        else if (prim == 0x98) { for (int i = 0; i < n - 2; i++) { if (i % 2 == 0) Face(i, i + 1, i + 2); else Face(i + 1, i, i + 2); } }
                        else if (prim == 0xA0) { for (int i = 0; i < n - 2; i++) Face(0, i + 1, i + 2); }
                        else throw new InvalidOperationException($"model {mi} surface {si}: primitive {op:x2}");
                    }
                }
                k += ns + 2;   // the surfaces, then Echoes' surface lookup + one unknown section
            }
            geo.Positions = pos.ToArray(); geo.Normals = nrm.ToArray(); geo.Uv0 = uv.ToArray(); geo.ShortUvs = suv.ToArray();
            geo.Triangles = tris.ToArray(); geo.TriangleMaterial = tmat.ToArray();
            return geo;
        }

        // whole elements only: a section is padded to 32 bytes
        static void AddFloats(List<float> to, byte[] s, int per) { int n = s.Length / (4 * per) * per; for (int i = 0; i < n; i++) to.Add(Be.F32(s, 4 * i)); }
        static void AddShorts(List<float> to, byte[] s, int per) { int n = s.Length / (2 * per) * per; for (int i = 0; i < n; i++) to.Add(Be.I16(s, 2 * i) / 32768f); }

        // ---- script layers ----

        public sealed class Property
        {
            public uint Id;
            public byte[] Data = Array.Empty<byte>();
            public List<Property>? Children;   // set when the body parses as a property struct

            public Property? Find(uint id)
            {
                if (Children == null) return null;
                foreach (Property p in Children)
                {
                    if (p.Id == id) return p;
                }
                return null;
            }
            public float F32 => Be.F32(Data, 0);
            public int I32 => Be.I32(Data, 0);
            public uint U32 => Be.U32(Data, 0);
            public bool Bool => Data.Length > 0 && Data[0] != 0;
            public string Str => Encoding.Latin1.GetString(Data).TrimEnd('\0');
        }

        public sealed class ScriptObject
        {
            public string Type = "";
            public uint Id;
            public int Layer;                  // -1 = the generated-object layer (SGEN)
            public List<(string State, string Message, uint Target)> Connections = new();
            public Property Root = new();

            // EditorProperties: Name, Transform (position, rotation degrees, scale), Active
            public const uint EditorPropertiesId = 0x255A4580, NameId = 0x494E414D, TransformId = 0x5846524D, ActiveId = 0x41435456;
            Property? Editor => Root.Find(EditorPropertiesId);
            public string Name => Editor?.Find(NameId)?.Str ?? "";
            public bool Active => Editor?.Find(ActiveId)?.Bool ?? true;
            public float[] Transform
            {
                get
                {
                    Property? x = Editor?.Find(TransformId);
                    var v = new float[9];
                    if (x != null && x.Data.Length >= 36)
                    {
                        for (int i = 0; i < 9; i++) v[i] = Be.F32(x.Data, 4 * i);
                    }
                    return v;
                }
            }
        }

        public List<ScriptObject> ReadScripts(bool includeGenerated = false)
        {
            var res = new List<ScriptObject>();
            for (int i = 0; i < LayerCount; i++)
            {
                ReadLayer(Sections[ScriptSection + i], res);
            }
            if (includeGenerated && GeneratedSection >= 0)
            {
                ReadLayer(Sections[GeneratedSection], res);
            }
            return res;
        }

        static void ReadLayer(byte[] s, List<ScriptObject> res)
        {
            bool gen = s[0] == 'S' && s[1] == 'G';           // "SGEN" vs "SCLY"
            int layer = gen ? -1 : (int)Be.U32(s, 5);
            int count = (int)Be.U32(s, gen ? 6 : 10);
            int o = gen ? 10 : 14;
            for (int i = 0; i < count; i++)
            {
                var obj = new ScriptObject { Type = Encoding.ASCII.GetString(s, o, 4), Layer = layer };
                int size = Be.U16(s, o + 4);
                int b = o + 6;
                obj.Id = Be.U32(s, b);
                int conns = Be.U16(s, b + 4);
                int p = b + 6;
                for (int c = 0; c < conns; c++)
                {
                    obj.Connections.Add((Encoding.ASCII.GetString(s, p, 4), Encoding.ASCII.GetString(s, p + 4, 4), Be.U32(s, p + 8)));
                    p += 12;
                }
                obj.Root = new Property { Id = Be.U32(s, p), Data = Be.Slice(s, p + 6, Be.U16(s, p + 4)) };
                obj.Root.Children = ParseStruct(obj.Root.Data, 0, obj.Root.Data.Length);
                res.Add(obj);
                o = b + size;
            }
        }

        // a property struct body: u16 count, then (u32 id, u16 size, data) each; null when the bytes aren't one
        static List<Property>? ParseStruct(byte[] b, int o, int end)
        {
            if (o + 2 > end) return null;
            int n = Be.U16(b, o);
            int p = o + 2;
            var res = new List<Property>(n);
            for (int i = 0; i < n; i++)
            {
                if (p + 6 > end) return null;
                uint id = Be.U32(b, p);
                int size = Be.U16(b, p + 4);
                p += 6;
                if (p + size > end) return null;
                var prop = new Property { Id = id, Data = Be.Slice(b, p, size) };
                if (size >= 2) prop.Children = ParseStruct(b, p, p + size);
                res.Add(prop);
                p += size;
            }
            return p == end ? res : null;
        }
    }
}
