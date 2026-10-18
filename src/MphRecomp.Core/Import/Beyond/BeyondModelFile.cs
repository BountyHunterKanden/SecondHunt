using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

namespace MphRecomp.Import.Beyond
{
    // A Metroid Prime 4: Beyond skinned model (SMDL, spec section 4) with its side data: the material slots (MATI ids),
    // the draw records and their levels of detail, the vertex layouts and index formats, and the unpacked GPU buffers.
    // BuildMeshes turns draw records into merged meshes exactly as the reference export writes them (4.5).
    //   side data  u32, u32, u32 NR, NR x (u32 length, u32 offset in the asset), u32 NV, NV x 20-byte buffer records,
    //              u32 NI, NI x buffer records; a record = u32 region, u32 offset in it, u32 stored, u32 unpacked, u32
    //   MTRL  "MSHP", u32 slots, u32, u32 k, k x u32, slots x 38: MATI GUID, base material GUID, 6 bytes
    //   MESH  u32 NE, NE x 16-byte draw records, ceil(NE/4) + ceil(NE/8) bytes, u32 NS, NS x u16 record list, u8 NL,
    //         NL x 5 ranges (u32 start, u32 count into the record list), u32 flag (1: NL x f32 follow)
    //   VBUF  u32 layouts; per layout u32 NA, NA x (stream, offset, stride, value format, meaning), u32, u8;
    //         stream s of layout j is vertex buffer 2j + s
    //   IBUF  u32 n, n x u32 index format (1 = u16, 2 = u32)
    // Clean-room: written from docs/cleanroom/G_beyond_spec.md alone (Sylux: SMDL 2c3f75c5-19e0-430e-bf24-7d8d0a19651f).
    public sealed class BeyondModelFile
    {
        public sealed class DrawRecord
        {
            public int Number;
            public int MaterialSlot;
            public int Layout;          // into Layouts
            public int IndexBuffer;     // into IndexBuffers
            public long FirstIndex;
            public long IndexCount;     // a triangle list
            public int[] Levels = Array.Empty<int>();   // the levels listing this record, ascending
        }

        public sealed class VertexAttribute
        {
            public int Stream, Offset, Stride, Format, Meaning;
        }

        public enum Selection
        {
            TopLevel,      // the records of level 0, in that order (the importer's)
            EveryRecord,   // every draw record in record order (Gun4Builder names other levels' meshes from it)
        }

        // value formats (4.3)
        public const int FormatHalf2 = 20, FormatUnorm8x4 = 21, FormatUint8x4 = 22, FormatUnorm16x4 = 30, FormatHalf4 = 34,
            FormatFloat3 = 37, FormatFloat4 = 40;
        // meanings (4.3)
        public const int MeaningPosition = 0, MeaningNormal = 1, MeaningTangent = 2, MeaningUv01 = 5, MeaningUv2 = 6,
            MeaningColor = 9, MeaningSkinSlots = 10, MeaningSkinWeights = 11;

        public string What = "";                       // for messages
        public readonly List<Guid> MaterialSlots = new();    // MATI ids
        public readonly List<Guid> BaseMaterials = new();
        public readonly List<DrawRecord> Records = new();
        public int[] RecordList = Array.Empty<int>();
        public readonly List<int[]> LevelRecords = new();    // per level: its draw-record numbers, first appearance only
        public readonly List<VertexAttribute[]> Layouts = new();
        public int[] IndexFormats = Array.Empty<int>();
        public byte[][] VertexBuffers = Array.Empty<byte[]>();
        public byte[][] IndexBuffers = Array.Empty<byte[]>();

        public static BeyondModelFile Read(BeyondArchive archive, Guid id) =>
            Parse(archive.ReadAsset(id), archive.ReadSideData(id), $"SMDL {id}");

        public static BeyondModelFile Parse(byte[] asset, byte[] sideData, string what)
        {
            BeyondForm.Expect(asset, "SMDL", what);
            var m = new BeyondModelFile { What = what };
            m.ReadBuffers(asset, sideData);
            List<BeyondChunk> chunks = BeyondForm.Chunks(asset, what);
            m.ReadMaterials(asset, BeyondForm.Find(chunks, "MTRL", what));
            m.ReadMeshes(asset, BeyondForm.Find(chunks, "MESH", what));
            m.ReadLayouts(asset, BeyondForm.Find(chunks, "VBUF", what));
            m.ReadIndexFormats(asset, BeyondForm.Find(chunks, "IBUF", what));
            m.Check();
            return m;
        }

        // ---------------------------------------------------------------- side data: the GPU buffers
        void ReadBuffers(byte[] asset, byte[] side)
        {
            if (side.Length == 0) throw new InvalidDataException($"{What}: no side data (the GPU buffers cannot be found)");
            var c = new BeyondCursor(side, 0, side.Length, What + " side data");
            c.Skip(8);
            int nr = c.Count(8, "read-region");
            var regions = new (long Length, long Offset)[nr];
            for (int i = 0; i < nr; i++)
            {
                long len = c.U32();
                long off = c.U32();
                if (off + len > asset.Length) throw new InvalidDataException($"{What}: read region {i} (0x{off:x} + {len}) lies past the asset's end");
                regions[i] = (len, off);
            }
            VertexBuffers = ReadBufferList(asset, c, regions, "vertex");
            IndexBuffers = ReadBufferList(asset, c, regions, "index");
        }

        byte[][] ReadBufferList(byte[] asset, BeyondCursor c, (long Length, long Offset)[] regions, string kind)
        {
            int n = c.Count(20, kind + " buffer");
            var list = new byte[n][];
            for (int i = 0; i < n; i++)
            {
                uint region = c.U32();
                long inRegion = c.U32(), stored = c.U32(), unpacked = c.U32();
                c.Skip(4);
                if (region >= regions.Length) throw new InvalidDataException($"{What}: {kind} buffer {i} names read region {region} of {regions.Length}");
                (long rLen, long rOff) = regions[region];
                if (inRegion + stored > rLen) throw new InvalidDataException($"{What}: {kind} buffer {i} runs past its read region");
                long at = rOff + inRegion;
                list[i] = BeyondUnpack.Unpack(asset.AsSpan((int)at, (int)stored), checked((int)unpacked), $"{What} {kind} buffer {i}");
            }
            return list;
        }

        // ---------------------------------------------------------------- chunks
        void ReadMaterials(byte[] a, BeyondChunk ch)
        {
            var c = new BeyondCursor(a, ch.DataStart, ch.End, What + " MTRL");
            string tag = c.Tag();
            if (tag != "MSHP") throw new InvalidDataException($"{What}: MTRL starts with {tag}, not MSHP");
            int n = c.Count(0, "material slot");
            c.Skip(4);
            int k = c.Count(4, "MTRL list");
            c.Skip(4L * k);
            if ((long)n * 38 > c.Left) throw new InvalidDataException($"{What}: {n} material slots do not fit in MTRL");
            for (int i = 0; i < n; i++)
            {
                MaterialSlots.Add(c.Guid());
                BaseMaterials.Add(c.Guid());
                c.Skip(6);
            }
        }

        void ReadMeshes(byte[] a, BeyondChunk ch)
        {
            var c = new BeyondCursor(a, ch.DataStart, ch.End, What + " MESH");
            int ne = c.Count(16, "draw record");
            for (int i = 0; i < ne; i++)
            {
                var r = new DrawRecord { Number = i, MaterialSlot = c.U16(), Layout = c.U8(), IndexBuffer = c.U8() };
                r.FirstIndex = c.U32();
                r.IndexCount = c.U32();
                c.Skip(4);
                Records.Add(r);
            }
            c.Skip((ne + 3) / 4 + (ne + 7) / 8);
            int ns = c.Count(2, "record list");
            RecordList = new int[ns];
            for (int i = 0; i < ns; i++) RecordList[i] = c.U16();
            int nl = c.U8();
            var levelsOf = new List<int>[ne];
            for (int l = 0; l < nl; l++)
            {
                var recs = new List<int>();
                var seen = new HashSet<int>();
                for (int k = 0; k < 5; k++)
                {
                    long start = c.U32(), count = c.U32();
                    if (start + count > ns) throw new InvalidDataException($"{What}: level {l} range {k} ({start}, {count}) runs past the {ns}-entry record list");
                    for (long j = start; j < start + count; j++)
                    {
                        int rec = RecordList[j];
                        if (rec >= ne) throw new InvalidDataException($"{What}: level {l} lists draw record {rec} of {ne}");
                        if (seen.Add(rec)) recs.Add(rec);
                    }
                }
                LevelRecords.Add(recs.ToArray());
                foreach (int rec in recs) (levelsOf[rec] ??= new List<int>()).Add(l);
            }
            // the trailing flag (and the switch distances after it) is not needed
            for (int i = 0; i < ne; i++) Records[i].Levels = levelsOf[i]?.ToArray() ?? Array.Empty<int>();
        }

        void ReadLayouts(byte[] a, BeyondChunk ch)
        {
            var c = new BeyondCursor(a, ch.DataStart, ch.End, What + " VBUF");
            int n = c.Count(9, "vertex layout");
            for (int j = 0; j < n; j++)
            {
                int na = c.Count(20, $"layout {j} attribute");
                var attrs = new VertexAttribute[na];
                for (int i = 0; i < na; i++)
                {
                    attrs[i] = new VertexAttribute
                    {
                        Stream = checked((int)c.U32()), Offset = checked((int)c.U32()), Stride = checked((int)c.U32()),
                        Format = checked((int)c.U32()), Meaning = checked((int)c.U32()),
                    };
                }
                c.Skip(5);
                Layouts.Add(attrs);
            }
        }

        void ReadIndexFormats(byte[] a, BeyondChunk ch)
        {
            var c = new BeyondCursor(a, ch.DataStart, ch.End, What + " IBUF");
            int n = c.Count(4, "index format");
            IndexFormats = new int[n];
            for (int i = 0; i < n; i++)
            {
                uint f = c.U32();
                if (f != 1 && f != 2) throw new InvalidDataException($"{What}: index buffer {i} has index format {f} (1 = u16, 2 = u32 known)");
                IndexFormats[i] = (int)f;
            }
        }

        void Check()
        {
            foreach (DrawRecord r in Records)
            {
                string at = $"{What}: draw record {r.Number}";
                if (r.MaterialSlot >= MaterialSlots.Count) throw new InvalidDataException($"{at} uses material slot {r.MaterialSlot} of {MaterialSlots.Count}");
                if (r.Layout >= Layouts.Count) throw new InvalidDataException($"{at} uses vertex layout {r.Layout} of {Layouts.Count}");
                if (r.IndexBuffer >= IndexBuffers.Length || r.IndexBuffer >= IndexFormats.Length)
                {
                    throw new InvalidDataException($"{at} uses index buffer {r.IndexBuffer} of {Math.Min(IndexBuffers.Length, IndexFormats.Length)}");
                }
                if (r.IndexCount % 3 != 0) throw new InvalidDataException($"{at} has {r.IndexCount} indices, not a triangle list");
                int size = IndexFormats[r.IndexBuffer] == 1 ? 2 : 4;
                if ((r.FirstIndex + r.IndexCount) * size > IndexBuffers[r.IndexBuffer].Length)
                {
                    throw new InvalidDataException($"{at}: indices {r.FirstIndex}..+{r.IndexCount} run past index buffer {r.IndexBuffer}");
                }
                foreach (VertexAttribute v in Layouts[r.Layout])
                {
                    if (2 * r.Layout + v.Stream >= VertexBuffers.Length)
                    {
                        throw new InvalidDataException($"{at}: layout {r.Layout} stream {v.Stream} needs vertex buffer {2 * r.Layout + v.Stream} of {VertexBuffers.Length}");
                    }
                    ValueSize(v.Format, at);
                }
            }
        }

        // ---------------------------------------------------------------- building meshes (4.4, 4.5)
        // "Mesh<k>_LOD<levels>_MatID<slot>", the levels joined by "_" ("None" when no level lists the record)
        public static string MeshName(int number, int[] levels, int slot) =>
            $"Mesh{number}_LOD{(levels.Length > 0 ? string.Join("_", levels) : "None")}_MatID{slot}";

        public List<DrawRecord> Select(Selection selection)
        {
            if (selection == Selection.EveryRecord) return new List<DrawRecord>(Records);
            var list = new List<DrawRecord>();
            if (LevelRecords.Count > 0)
            {
                foreach (int r in LevelRecords[0]) list.Add(Records[r]);
            }
            return list;
        }

        // the meshes of a selection, numbered by position in it; skin may be null for a model without skin attributes
        public List<BeyondModelMesh> BuildMeshes(Selection selection, BeyondSkinTable? skin)
        {
            var meshes = new List<BeyondModelMesh>();
            List<DrawRecord> recs = Select(selection);
            for (int k = 0; k < recs.Count; k++) meshes.Add(BuildMesh(recs[k], k, skin));
            return meshes;
        }

        static int ValueSize(int format, string what) => format switch
        {
            FormatHalf2 => 4,
            FormatUnorm8x4 or FormatUint8x4 => 4,
            FormatUnorm16x4 or FormatHalf4 => 8,
            FormatFloat3 => 12,
            FormatFloat4 => 16,
            _ => throw new NotSupportedException($"{what}: vertex value format {format} is not known"),
        };

        // whether a value format gives 4 values (so meaning 5's z w are UV set 1)
        static bool HasFourValues(int format) => format is FormatUnorm8x4 or FormatUint8x4 or FormatUnorm16x4 or FormatHalf4 or FormatFloat4;

        // one attribute value as (x, y, z, w), every division in single precision
        static void DecodeValue(ReadOnlySpan<byte> b, int format, Span<float> v)
        {
            switch (format)
            {
                case FormatHalf2:
                    v[0] = (float)BinaryPrimitives.ReadHalfLittleEndian(b);
                    v[1] = (float)BinaryPrimitives.ReadHalfLittleEndian(b.Slice(2));
                    v[2] = 0; v[3] = 0;
                    break;
                case FormatUnorm8x4:
                    for (int i = 0; i < 4; i++) v[i] = b[i] / 255f;
                    break;
                case FormatUint8x4:
                    for (int i = 0; i < 4; i++) v[i] = b[i];
                    break;
                case FormatUnorm16x4:
                    for (int i = 0; i < 4; i++) v[i] = BinaryPrimitives.ReadUInt16LittleEndian(b.Slice(2 * i)) / 65535f;
                    break;
                case FormatHalf4:
                    for (int i = 0; i < 4; i++) v[i] = (float)BinaryPrimitives.ReadHalfLittleEndian(b.Slice(2 * i));
                    break;
                case FormatFloat3:
                    for (int i = 0; i < 3; i++) v[i] = BinaryPrimitives.ReadSingleLittleEndian(b.Slice(4 * i));
                    v[3] = 0;
                    break;
                case FormatFloat4:
                    for (int i = 0; i < 4; i++) v[i] = BinaryPrimitives.ReadSingleLittleEndian(b.Slice(4 * i));
                    break;
            }
        }

        // raw vertices compared as the bit patterns of all their decoded values
        sealed class RawVertexComparer : IEqualityComparer<int>
        {
            readonly uint[] bits;
            readonly int width;
            public RawVertexComparer(uint[] bits, int width) { this.bits = bits; this.width = width; }

            public bool Equals(int a, int b) =>
                bits.AsSpan(a * width, width).SequenceEqual(bits.AsSpan(b * width, width));

            public int GetHashCode(int a)
            {
                var h = new HashCode();
                foreach (uint x in bits.AsSpan(a * width, width)) h.Add(x);
                return h.ToHashCode();
            }
        }

        public BeyondModelMesh BuildMesh(DrawRecord r, int number, BeyondSkinTable? skin)
        {
            string what = $"{What}: draw record {r.Number}";
            var mesh = new BeyondModelMesh
            {
                Number = number, Name = MeshName(number, r.Levels, r.MaterialSlot), Record = r.Number, Levels = r.Levels,
                MaterialSlot = r.MaterialSlot, JointNames = skin?.Names ?? Array.Empty<string>(),
            };

            // 1. the record's indices
            byte[] ib = IndexBuffers[r.IndexBuffer];
            bool wide = IndexFormats[r.IndexBuffer] == 2;
            int count = checked((int)r.IndexCount);
            var stored = new long[count];
            long lo = long.MaxValue, hi = long.MinValue;
            for (int i = 0; i < count; i++)
            {
                long at = r.FirstIndex + i;
                long x = wide ? BinaryPrimitives.ReadUInt32LittleEndian(ib.AsSpan((int)(at * 4))) : BinaryPrimitives.ReadUInt16LittleEndian(ib.AsSpan((int)(at * 2)));
                stored[i] = x;
                if (x < lo) lo = x;
                if (x > hi) hi = x;
            }
            if (count == 0) return EmptyGeometry(mesh);

            // 2-3. the raw vertices lo..hi, every attribute decoded
            VertexAttribute[] attrs = Layouts[r.Layout];
            int na = attrs.Length;
            int raw = checked((int)(hi - lo + 1));
            int width = na * 4;
            var values = new float[(long)raw * width];
            for (int a = 0; a < na; a++)
            {
                VertexAttribute at = attrs[a];
                byte[] vb = VertexBuffers[2 * r.Layout + at.Stream];
                int size = ValueSize(at.Format, what);
                long last = at.Offset + (hi * at.Stride) + size;
                if (at.Offset < 0 || last > vb.Length)
                {
                    throw new InvalidDataException($"{what}: vertex {hi} attribute {a} (meaning {at.Meaning}) runs past vertex buffer {2 * r.Layout + at.Stream}");
                }
                for (int v = 0; v < raw; v++)
                {
                    long o = at.Offset + (lo + v) * at.Stride;
                    DecodeValue(vb.AsSpan((int)o, size), at.Format, values.AsSpan(v * width + a * 4, 4));
                }
            }

            // 4.5.1 merging: a raw vertex equal (bit for bit) to an earlier one becomes it
            uint[] bits = MemoryMarshal.Cast<float, uint>(values.AsSpan()).ToArray();
            var first = new Dictionary<int, int>(raw, new RawVertexComparer(bits, width));
            var outOf = new int[raw];
            var order = new List<int>(raw);
            for (int v = 0; v < raw; v++)
            {
                if (first.TryGetValue(v, out int o)) outOf[v] = o;
                else
                {
                    outOf[v] = order.Count;
                    first.Add(v, order.Count);
                    order.Add(v);
                }
            }
            mesh.Indices = new int[count];
            for (int i = 0; i < count; i++) mesh.Indices[i] = outOf[stored[i] - lo];

            // 4.5.2-4 the output attributes
            int n = order.Count;
            int Find(int meaning)
            {
                for (int a = 0; a < na; a++) if (attrs[a].Meaning == meaning) return a;
                return -1;
            }
            int aPos = Find(MeaningPosition), aNrm = Find(MeaningNormal), aUv = Find(MeaningUv01), aUv2 = Find(MeaningUv2);
            int aCol = Find(MeaningColor), aSlot = Find(MeaningSkinSlots), aWgt = Find(MeaningSkinWeights);
            mesh.Positions = new float[n * 3];
            mesh.Normals = new float[n * 3];
            mesh.Colors = new float[n * 4];
            if (aUv >= 0) mesh.Uv0 = new float[n * 2];
            if (aUv >= 0 && HasFourValues(attrs[aUv].Format)) mesh.Uv1 = new float[n * 2];
            if (aUv2 >= 0) mesh.Uv2 = new float[n * 2];
            mesh.Joints = new int[n * BeyondModelMesh.SkinSlots];
            mesh.Weights = new float[n * BeyondModelMesh.SkinSlots];
            Span<int> keptSlot = stackalloc int[4];
            Span<float> keptWeight = stackalloc float[4];
            for (int k = 0; k < n; k++)
            {
                int vo = order[k] * width;
                if (aPos >= 0)
                {
                    for (int i = 0; i < 3; i++) mesh.Positions[k * 3 + i] = values[vo + aPos * 4 + i];
                }
                if (aNrm >= 0)
                {
                    float x = values[vo + aNrm * 4], y = values[vo + aNrm * 4 + 1], z = values[vo + aNrm * 4 + 2];
                    float sum = x * x + y * y + z * z;
                    float len = MathF.Sqrt(sum);
                    if (len != 0)
                    {
                        mesh.Normals[k * 3] = x / len;
                        mesh.Normals[k * 3 + 1] = y / len;
                        mesh.Normals[k * 3 + 2] = z / len;
                    }
                }
                if (aUv >= 0)
                {
                    mesh.Uv0![k * 2] = values[vo + aUv * 4];
                    mesh.Uv0[k * 2 + 1] = values[vo + aUv * 4 + 1];
                    if (mesh.Uv1 != null)
                    {
                        mesh.Uv1[k * 2] = values[vo + aUv * 4 + 2];
                        mesh.Uv1[k * 2 + 1] = values[vo + aUv * 4 + 3];
                    }
                }
                if (aUv2 >= 0)
                {
                    mesh.Uv2![k * 2] = values[vo + aUv2 * 4];
                    mesh.Uv2[k * 2 + 1] = values[vo + aUv2 * 4 + 1];
                }
                for (int i = 0; i < 4; i++) mesh.Colors[k * 4 + i] = aCol >= 0 ? values[vo + aCol * 4 + i] : 1f;

                if (aSlot >= 0 && aWgt >= 0)
                {
                    int kept = 0;
                    for (int s = 0; s < 4; s++)
                    {
                        float w = values[vo + aWgt * 4 + s];
                        if (w == 0) continue;
                        float slot = values[vo + aSlot * 4 + s];
                        int bone = (int)slot;
                        if (bone != slot || bone < 0 || (skin != null && bone >= skin.Count))
                        {
                            throw new InvalidDataException($"{what}: vertex {lo + order[k]} skin slot {s} names bone {slot} of the {skin?.Count ?? 0}-entry skinned-bone table");
                        }
                        keptSlot[kept] = bone;
                        keptWeight[kept] = w;
                        kept++;
                    }
                    SkinWeights(keptWeight.Slice(0, kept));
                    for (int s = 0; s < kept; s++)
                    {
                        mesh.Joints[k * 4 + s] = keptSlot[s];
                        mesh.Weights[k * 4 + s] = keptWeight[s];
                    }
                }
            }
            return mesh;
        }

        // 4.5.4: weights to hundredths -- every kept slot but the last rounds w x 100 (single precision product, ties to
        // even); the last takes what is left of 100; each output is (f32) q x 0.01f
        static void SkinWeights(Span<float> w)
        {
            int k = w.Length;
            if (k == 0) return;
            int sum = 0;
            Span<int> q = stackalloc int[4];
            for (int i = 0; i < k - 1; i++)
            {
                float p = w[i] * 100f;
                q[i] = (int)MathF.Round(p, MidpointRounding.ToEven);
                sum += q[i];
            }
            q[k - 1] = 100 - sum;
            for (int i = 0; i < k; i++) w[i] = (float)q[i] * 0.01f;
        }

        static BeyondModelMesh EmptyGeometry(BeyondModelMesh m)
        {
            m.Positions = Array.Empty<float>();
            m.Normals = Array.Empty<float>();
            m.Colors = Array.Empty<float>();
            m.Indices = Array.Empty<int>();
            return m;
        }
    }
}
