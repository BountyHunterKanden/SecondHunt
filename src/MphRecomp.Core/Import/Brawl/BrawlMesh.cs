using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;

namespace MphRecomp.Import.Brawl
{
    // A bone of a mesh. Number = its +0x0C bone number, Node = the node number objects use to name it, Parent = the
    // parent's bone number or -1. Rotation in degrees. Bind / InverseBind: 3 rows x 4 columns, row-major (column 4 is
    // the translation).
    public sealed record BrawlBone(string Name, int Number, int Node, int FacingMode, Vector3 Scale, Vector3 Rotation,
        Vector3 Translation, int Parent, float[] Bind, float[] InverseBind);

    // A decoded vertex array (positions, normals or texture coordinates). Components = how many each entry stores
    // (1-3); unused slots are 0.
    public sealed record BrawlVertexArray(string Name, int Number, int Components, IReadOnlyList<Vector3> Values);

    // One vertex of a triangle: indices into the object's position, normal and texture set 0 arrays (-1 when absent).
    public readonly record struct BrawlMeshVertex(int Position, int Normal, int Tex0);

    // An object: Node = the node number of the bone it hangs on (-1 = weighted to several), array fields = the array
    // numbers it uses (-1 = none). Triangles in the hardware's vertex order (spec 6.4).
    public sealed record BrawlMeshObject(string Name, int Number, int Node, int PositionArray, int NormalArray, int Tex0Array,
        IReadOnlyList<(BrawlMeshVertex A, BrawlMeshVertex B, BrawlMeshVertex C)> Triangles);

    // A draw step: draw object number Object with the named material on bone number Bone (the bone's +0x0C number).
    // SeeThrough = from DrawXlu (the see-through pass), otherwise DrawOpa (opaque).
    public sealed record BrawlDrawStep(string MaterialName, string ObjectName, int Object, int Bone, int Priority, bool SeeThrough);

    // A mesh resource (MDL0, version 9): bones, decoded vertex arrays, objects as triangles and the draw steps.
    // Materials and shaders are not read here (only the material names, for the draw steps).
    // Clean-room from docs/cleanroom/E_brawl_effects_spec.md (section 6).
    public sealed class BrawlMesh
    {
        const int SecDraw = 0, SecBones = 1, SecPositions = 2, SecNormals = 3, SecTexCoords = 5, SecMaterials = 6, SecObjects = 8;

        public string Name { get; }
        public IReadOnlyList<BrawlBone> Bones { get; }
        public IReadOnlyList<BrawlVertexArray> Positions { get; }
        public IReadOnlyList<BrawlVertexArray> Normals { get; }
        public IReadOnlyList<BrawlVertexArray> TexCoords { get; }
        public IReadOnlyList<string> Materials { get; }
        public IReadOnlyList<BrawlMeshObject> Objects { get; }
        public IReadOnlyList<BrawlDrawStep> DrawSteps { get; }

        BrawlMesh(string name, List<BrawlBone> bones, List<BrawlVertexArray> positions, List<BrawlVertexArray> normals,
            List<BrawlVertexArray> texCoords, List<string> materials, List<BrawlMeshObject> objects, List<BrawlDrawStep> steps)
        {
            Name = name;
            Bones = bones;
            Positions = positions;
            Normals = normals;
            TexCoords = texCoords;
            Materials = materials;
            Objects = objects;
            DrawSteps = steps;
        }

        public BrawlBone? BoneByNumber(int number) => Find(Bones, b => b.Number == number);
        public BrawlBone? BoneByNode(int node) => Find(Bones, b => b.Node == node);
        public BrawlBone? BoneByName(string name) => Find(Bones, b => b.Name == name);
        public BrawlMeshObject? ObjectByNumber(int number) => Find(Objects, o => o.Number == number);
        public BrawlVertexArray? PositionArrayByNumber(int number) => Find(Positions, a => a.Number == number);
        public BrawlVertexArray? NormalArrayByNumber(int number) => Find(Normals, a => a.Number == number);
        public BrawlVertexArray? TexCoordArrayByNumber(int number) => Find(TexCoords, a => a.Number == number);

        static T? Find<T>(IReadOnlyList<T> list, Func<T, bool> match) where T : class
        {
            foreach (var x in list)
            {
                if (match(x)) return x;
            }
            return null;
        }

        // every mesh in the file's "3DModels(NW4R)" folder, in record order
        public static List<BrawlMesh> ReadAll(BrawlResFile file)
        {
            var r = new List<BrawlMesh>();
            foreach (var item in file.Folder(BrawlResFile.Models))
            {
                r.Add(Read(file, item.Position));
            }
            return r;
        }

        // the MDL0 resource starting at `position` in the resource file
        public static BrawlMesh Read(BrawlResFile file, int position)
        {
            var d = file.Data.Span;
            string part = $"mesh resource at 0x{position:X}";
            BrawlBytes.Magic(d, position, "MDL0", part);
            BrawlBytes.Need(d, position, 0x80, part + " header");
            uint size = BrawlBytes.U32(d, position + 4L, part);
            BrawlBytes.Need(d, position, size, part);
            uint version = BrawlBytes.U32(d, position + 8L, part);
            int namePos = BrawlBytes.I32(d, position + 0x3CL, part);
            string name = BrawlResFile.Name(d, position + (long)namePos, part);
            part = $"mesh {name}";
            if (version != 9)
            {
                throw new InvalidDataException($"{part}: version {version}, only version 9 is read");
            }

            var sections = new long[11];
            for (int i = 0; i < 11; i++)
            {
                int s = BrawlBytes.I32(d, position + 0x10L + 4 * i, part + " section folders");
                sections[i] = s == 0 ? -1 : position + (long)s;
            }

            var bones = ReadBones(d, sections[SecBones], part);
            var positions = ReadArrays(d, sections[SecPositions], ArrayKind.Position, part);
            var normals = ReadArrays(d, sections[SecNormals], ArrayKind.Normal, part);
            var texCoords = ReadArrays(d, sections[SecTexCoords], ArrayKind.TexCoord, part);
            var materials = new List<string>();
            if (sections[SecMaterials] >= 0)
            {
                foreach (var (mname, _) in Records(d, sections[SecMaterials], part + " materials folder"))
                {
                    materials.Add(mname);
                }
            }
            var objects = ReadObjects(d, sections[SecObjects], positions, normals, texCoords, part);
            var steps = ReadDrawSteps(d, sections[SecDraw], bones, materials, objects, part);
            return new BrawlMesh(name, bones, positions, normals, texCoords, materials, objects, steps);
        }

        // ---- bones (6.2) ----

        static List<BrawlBone> ReadBones(ReadOnlySpan<byte> d, long folder, string mesh)
        {
            var bones = new List<BrawlBone>();
            if (folder < 0) return bones;
            var recs = Records(d, folder, mesh + " bones folder");
            var numberAt = new Dictionary<long, int>();
            var parentAt = new List<long>();
            foreach (var (fname, at) in recs)
            {
                string part = $"{mesh} bone {fname}";
                BrawlBytes.Need(d, at, 0xD0, part);
                int namePos = BrawlBytes.I32(d, at + 0x08, part);
                string name = BrawlResFile.Name(d, at + namePos, part);
                part = $"{mesh} bone {name}";
                int number = (int)BrawlBytes.U32(d, at + 0x0C, part);
                int node = (int)BrawlBytes.U32(d, at + 0x10, part);
                int facing = (int)BrawlBytes.U32(d, at + 0x18, part);
                var scale = Vec3(d, at + 0x20, part);
                var rot = Vec3(d, at + 0x2C, part);
                var trans = Vec3(d, at + 0x38, part);
                int parent = BrawlBytes.I32(d, at + 0x5C, part);
                var bind = Floats(d, at + 0x70, 12, part);
                var inv = Floats(d, at + 0xA0, 12, part);
                if (numberAt.ContainsValue(number))
                {
                    throw new InvalidDataException($"{part}: bone number {number} used twice");
                }
                numberAt[at] = number;
                parentAt.Add(parent == 0 ? -1 : at + parent);
                bones.Add(new BrawlBone(name, number, node, facing, scale, rot, trans, -1, bind, inv));
            }
            for (int i = 0; i < bones.Count; i++)
            {
                if (parentAt[i] < 0) continue;
                if (!numberAt.TryGetValue(parentAt[i], out int p))
                {
                    throw new InvalidDataException($"{mesh} bone {bones[i].Name}: parent position 0x{parentAt[i]:X} is not a bone record");
                }
                bones[i] = bones[i] with { Parent = p };
            }
            return bones;
        }

        // ---- vertex arrays (6.3) ----

        enum ArrayKind { Position, Normal, TexCoord }

        static List<BrawlVertexArray> ReadArrays(ReadOnlySpan<byte> d, long folder, ArrayKind kind, string mesh)
        {
            var arrays = new List<BrawlVertexArray>();
            if (folder < 0) return arrays;
            string what = kind switch { ArrayKind.Position => "position", ArrayKind.Normal => "normal", _ => "texture coordinate" };
            foreach (var (fname, at) in Records(d, folder, $"{mesh} {what} arrays folder"))
            {
                string part = $"{mesh} {what} array {fname}";
                BrawlBytes.Need(d, at, 0x20, part);
                uint valuesPos = BrawlBytes.U32(d, at + 0x08, part);
                int namePos = BrawlBytes.I32(d, at + 0x0C, part);
                string name = BrawlResFile.Name(d, at + namePos, part);
                part = $"{mesh} {what} array {name}";
                int number = (int)BrawlBytes.U32(d, at + 0x10, part);
                uint compKind = BrawlBytes.U32(d, at + 0x14, part);
                uint type = BrawlBytes.U32(d, at + 0x18, part);
                int frac = BrawlBytes.U8(d, at + 0x1C, part);
                int stride = BrawlBytes.U8(d, at + 0x1D, part);
                int count = BrawlBytes.U16(d, at + 0x1E, part);

                int comps = (kind, compKind) switch
                {
                    (ArrayKind.Position, 0) => 2,
                    (ArrayKind.Position, 1) => 3,
                    (ArrayKind.Normal, 0) => 3,
                    (ArrayKind.TexCoord, 0) => 1,
                    (ArrayKind.TexCoord, 1) => 2,
                    _ => throw new InvalidDataException($"{part}: component kind {compKind} not supported"),
                };
                int vsize = type switch
                {
                    0 or 1 => 1,
                    2 or 3 => 2,
                    4 => 4,
                    _ => throw new InvalidDataException($"{part}: value type {type} not supported"),
                };
                if (type != 4 && frac > 30)
                {
                    throw new InvalidDataException($"{part}: {frac} fraction bits not supported");
                }
                float unit = type == 4 ? 1f : 1f / (1 << frac);
                long start = at + valuesPos;
                if (count > 0)
                {
                    BrawlBytes.Need(d, start, (long)stride * (count - 1) + (long)comps * vsize, part + " values");
                }
                var values = new Vector3[count];
                var c = new float[3];
                for (int i = 0; i < count; i++)
                {
                    System.Array.Clear(c);
                    long e = start + (long)stride * i;
                    for (int k = 0; k < comps; k++)
                    {
                        long o = e + (long)k * vsize;
                        c[k] = type switch
                        {
                            0 => BrawlBytes.U8(d, o, part) * unit,
                            1 => (sbyte)BrawlBytes.U8(d, o, part) * unit,
                            2 => BrawlBytes.U16(d, o, part) * unit,
                            3 => BrawlBytes.I16(d, o, part) * unit,
                            _ => BrawlBytes.F32(d, o, part),
                        };
                    }
                    values[i] = new Vector3(c[0], c[1], c[2]);
                }
                foreach (var a in arrays)
                {
                    if (a.Number == number) throw new InvalidDataException($"{part}: array number {number} used twice");
                }
                arrays.Add(new BrawlVertexArray(name, number, comps, values));
            }
            return arrays;
        }

        // ---- objects (6.4) ----

        static List<BrawlMeshObject> ReadObjects(ReadOnlySpan<byte> d, long folder, List<BrawlVertexArray> positions,
            List<BrawlVertexArray> normals, List<BrawlVertexArray> texCoords, string mesh)
        {
            var objects = new List<BrawlMeshObject>();
            if (folder < 0) return objects;
            foreach (var (fname, at) in Records(d, folder, mesh + " objects folder"))
            {
                string part = $"{mesh} object {fname}";
                BrawlBytes.Need(d, at, 0x60, part);
                int node = BrawlBytes.I32(d, at + 0x08, part);
                uint wordA = BrawlBytes.U32(d, at + 0x0C, part);
                uint wordB = BrawlBytes.U32(d, at + 0x10, part);
                uint listSize = BrawlBytes.U32(d, at + 0x24, part);
                uint listPos = BrawlBytes.U32(d, at + 0x2C, part);
                int namePos = BrawlBytes.I32(d, at + 0x38, part);
                string name = BrawlResFile.Name(d, at + namePos, part);
                part = $"{mesh} object {name}";
                int number = (int)BrawlBytes.U32(d, at + 0x3C, part);
                int posArr = BrawlBytes.I16(d, at + 0x48, part);
                int nrmArr = BrawlBytes.I16(d, at + 0x4A, part);
                int texArr = BrawlBytes.I16(d, at + 0x50, part);
                foreach (var o in objects)
                {
                    if (o.Number == number) throw new InvalidDataException($"{part}: object number {number} used twice");
                }

                // attribute byte widths, in vertex order: matrix slot, texture matrix slots 0-7, position, normal,
                // colour 0, colour 1, texture sets 0-7 (0 = absent)
                var widths = new int[1 + 8 + 4 + 8];
                widths[0] = (wordA & 1) != 0 ? 1 : 0;
                for (int k = 0; k < 8; k++) widths[1 + k] = (wordA >> (1 + k) & 1) != 0 ? 1 : 0;
                string[] fieldNames = { "position", "normal", "colour 0", "colour 1" };
                for (int k = 0; k < 4; k++) widths[9 + k] = Width((wordA >> (9 + 2 * k)) & 3, fieldNames[k], part);
                for (int k = 0; k < 8; k++) widths[13 + k] = Width((wordB >> (2 * k)) & 3, $"texture set {k}", part);
                const int PosSlot = 9, NrmSlot = 10, TexSlot = 13;

                var posA = ArrayFor(positions, posArr, widths[PosSlot] != 0, "position", part);
                var nrmA = ArrayFor(normals, nrmArr, widths[NrmSlot] != 0, "normal", part);
                var texA = ArrayFor(texCoords, texArr, widths[TexSlot] != 0, "texture set 0", part);

                long listAt = at + 0x24 + (long)listPos;
                string lp = part + " draw list";
                BrawlBytes.Need(d, listAt, listSize, lp);
                var list = d.Slice((int)listAt, (int)listSize);
                var tris = new List<(BrawlMeshVertex, BrawlMeshVertex, BrawlMeshVertex)>();
                var verts = new List<BrawlMeshVertex>();
                long p = 0;
                while (p < list.Length)
                {
                    byte cmd = list[(int)p];
                    if (cmd == 0) break;
                    int shape = cmd & 0xF8;
                    if (shape is not (0x80 or 0x90 or 0x98 or 0xA0 or 0xA8 or 0xB0 or 0xB8))
                    {
                        throw new InvalidDataException($"{lp}: unknown command byte 0x{cmd:X2} at 0x{p:X}");
                    }
                    int count = BrawlBytes.U16(list, p + 1, lp);
                    p += 3;
                    verts.Clear();
                    for (int v = 0; v < count; v++)
                    {
                        int pi = -1, ni = -1, ti = -1;
                        for (int s = 0; s < widths.Length; s++)
                        {
                            int w = widths[s];
                            if (w == 0) continue;
                            int idx = w == 1 ? BrawlBytes.U8(list, p, lp) : BrawlBytes.U16(list, p, lp);
                            p += w;
                            if (s == PosSlot) pi = Index(idx, posA, "position", lp);
                            else if (s == NrmSlot) ni = Index(idx, nrmA, "normal", lp);
                            else if (s == TexSlot) ti = Index(idx, texA, "texture set 0", lp);
                        }
                        verts.Add(new BrawlMeshVertex(pi, ni, ti));
                    }
                    AddTriangles(shape, verts, tris, lp);
                }
                objects.Add(new BrawlMeshObject(name, number, node, posArr, nrmArr, texArr, tris));
            }
            return objects;
        }

        // byte width of an attribute field: 0 absent, 2 = 1 byte, 3 = 2 bytes; 1 (direct value) is unsupported
        static int Width(uint field, string what, string part) => field switch
        {
            0 => 0,
            2 => 1,
            3 => 2,
            _ => throw new InvalidDataException($"{part}: {what} stored directly in the draw list, not supported"),
        };

        // the array an object names (null when it names none); a present attribute needs an array
        static BrawlVertexArray? ArrayFor(List<BrawlVertexArray> arrays, int number, bool present, string what, string part)
        {
            if (number < 0)
            {
                if (present) throw new InvalidDataException($"{part}: {what} indices present but no {what} array");
                return null;
            }
            foreach (var a in arrays)
            {
                if (a.Number == number) return a;
            }
            throw new InvalidDataException($"{part}: {what} array number {number} matches no array");
        }

        static int Index(int idx, BrawlVertexArray? array, string what, string part)
        {
            if (array == null || idx >= array.Values.Count)
            {
                throw new InvalidDataException($"{part}: {what} index {idx} outside its array ({array?.Values.Count ?? 0} entries)");
            }
            return idx;
        }

        static void AddTriangles(int shape, List<BrawlMeshVertex> v,
            List<(BrawlMeshVertex, BrawlMeshVertex, BrawlMeshVertex)> tris, string part)
        {
            int n = v.Count;
            switch (shape)
            {
                case 0x90:
                    for (int i = 0; i + 2 < n; i += 3) tris.Add((v[i], v[i + 1], v[i + 2]));
                    break;
                case 0x98:
                    for (int i = 0; i + 2 < n; i++)
                    {
                        tris.Add((i & 1) == 0 ? (v[i], v[i + 1], v[i + 2]) : (v[i + 1], v[i], v[i + 2]));
                    }
                    break;
                case 0xA0:
                    for (int i = 0; i + 2 < n; i++) tris.Add((v[0], v[i + 1], v[i + 2]));
                    break;
                case 0x80:
                    for (int i = 0; i + 3 < n; i += 4)
                    {
                        tris.Add((v[i], v[i + 1], v[i + 2]));
                        tris.Add((v[i], v[i + 2], v[i + 3]));
                    }
                    break;
                default:
                    break; // lines, line strip, points: no triangles
            }
        }

        // ---- draw definitions (6.5) ----

        static List<BrawlDrawStep> ReadDrawSteps(ReadOnlySpan<byte> d, long folder, List<BrawlBone> bones, List<string> materials,
            List<BrawlMeshObject> objects, string mesh)
        {
            var steps = new List<BrawlDrawStep>();
            if (folder < 0) return steps;
            foreach (var (iname, at) in Records(d, folder, mesh + " draw definitions folder"))
            {
                bool xlu;
                if (iname == "DrawOpa") xlu = false;
                else if (iname == "DrawXlu") xlu = true;
                else continue;
                string part = $"{mesh} {iname}";
                long p = at;
                while (true)
                {
                    byte step = BrawlBytes.U8(d, p, part);
                    if (step == 0x01) break;
                    switch (step)
                    {
                        case 0x02:
                        case 0x05:
                        case 0x06:
                            BrawlBytes.Need(d, p + 1, 4, part);
                            p += 5;
                            break;
                        case 0x03:
                            int count = BrawlBytes.U8(d, p + 3, part);
                            BrawlBytes.Need(d, p + 4, 6L * count, part);
                            p += 4 + 6L * count;
                            break;
                        case 0x04:
                            int mat = BrawlBytes.U16(d, p + 1, part);
                            int obj = BrawlBytes.U16(d, p + 3, part);
                            int bone = BrawlBytes.U16(d, p + 5, part);
                            int prio = BrawlBytes.U8(d, p + 7, part);
                            p += 8;
                            if (mat >= materials.Count)
                            {
                                throw new InvalidDataException($"{part}: material number {mat} past the materials folder ({materials.Count})");
                            }
                            var o = Find(objects, x => x.Number == obj)
                                ?? throw new InvalidDataException($"{part}: object number {obj} matches no object");
                            if (Find(bones, b => b.Number == bone) == null)
                            {
                                throw new InvalidDataException($"{part}: bone number {bone} matches no bone");
                            }
                            steps.Add(new BrawlDrawStep(materials[mat], o.Name, obj, bone, prio, xlu));
                            break;
                        default:
                            throw new InvalidDataException($"{part}: unknown step 0x{step:X2} at 0x{p:X}");
                    }
                }
            }
            return steps;
        }

        // ---- helpers ----

        static Vector3 Vec3(ReadOnlySpan<byte> d, long at, string part) =>
            new Vector3(BrawlBytes.F32(d, at, part), BrawlBytes.F32(d, at + 4, part), BrawlBytes.F32(d, at + 8, part));

        static float[] Floats(ReadOnlySpan<byte> d, long at, int n, string part)
        {
            var r = new float[n];
            for (int i = 0; i < n; i++) r[i] = BrawlBytes.F32(d, at + 4L * i, part);
            return r;
        }

        // records 1..N of the folder at `at` (section 2.2 layout): (name, data position from the resource file's start)
        internal static List<(string Name, long At)> Records(ReadOnlySpan<byte> d, long at, string part)
        {
            uint n = BrawlBytes.U32(d, at + 4, part);
            BrawlBytes.Need(d, at + 8, 16L * (n + 1L), part + " records");
            var r = new List<(string, long)>((int)n);
            for (long i = 1; i <= n; i++)
            {
                long rec = at + 8 + 16 * i;
                string rp = $"{part} record {i}";
                int namePos = BrawlBytes.I32(d, rec + 8, rp);
                int dataPos = BrawlBytes.I32(d, rec + 12, rp);
                string name = BrawlResFile.Name(d, at + namePos, rp);
                long dataAt = at + dataPos;
                if (dataAt < 0 || dataAt >= d.Length)
                {
                    throw new InvalidDataException($"{rp}: data position 0x{dataAt:X} outside the file (0x{d.Length:X} bytes)");
                }
                r.Add((name, dataAt));
            }
            return r;
        }
    }
}
