using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace MphRecomp.Import.Beyond
{
    // Metroid Prime 4: Beyond character project (CHPR): the container, its animation table and the parts of the
    // character info the animation decoder needs. Clean-room: worked out from the owner's own RomFS dump (Samus's
    // universeRoom.pak CHPR 7238eaad-21a1-4966-83fb-7fb7d1173bf6) with no game code read, and it replaces the Prime 4
    // Model Dumper's CHPR class (chpr.json) that brawl_extract/rig_work/gun/mp4anim/py/mp4anim_lib.py used to read.
    // Input: the CHPR's decompressed bytes (the pak/RomFS layer that pulls them out of a pak is a separate reader).
    //
    // FORMAT (little endian):
    //   0x00 "RFRM" ... 0x14 "CHPR", 0x18 u32 version (0xa1) x2
    //   0x20 partition table: (u32 offset, u32 length, u32 length2) until offset + length = file size
    //        then the animation table: (u32 index, u32 offset, u32 size) x anim count; the blobs run to the end of file
    //   name pool, right after the animation table:
    //        u32 string bytes, NUL-terminated strings; u16 group count (1), u16 hash-list count;
    //        u32 strings per group; u32 entries per hash list; u32 string count;
    //        per hash list: u32 n, u32[n] name hashes, u32[n] string indices
    //   ... (evaluation data, the rest-pose table -- see ChprSkeleton) ...
    //   anim node set: u16 n, u16 hash list, u32[n] node ids
    //        id: bits 0-5 anim group, bits 6-15 slot in the group, bits 16-23 node kind (0x43 transform, 0x44 root
    //        motion, 0x40 visibility bool, 0x45 event, 0x46 float), bits 24-31 flags
    //   abs (skeleton) context, right after it: u8 descriptor count, u16 node count, u16, u16, u16, u16 ...
    //   abs node set: u16 n, u16 hash list, u32[n] node ids. Byte 2 = kind (0x62 transform; 0x60, 0x61, 0x65, 0x66 other
    //        nodes); a transform's low 16 bits give its TRANSFORM index (NodeIndex), counting transforms only, in list
    //        order (Samus: 331 transforms; the first 294 sit at list positions 0..293, the other 37 after 18 other nodes)
    //   parents: n x (u16 parent transform id (0xffff = none), u16), then one byte (0xff)
    //   render contexts: (u8 1, u16 bones, u16 n2, u16 n3, u8 n4, u32 extra bytes, u8 index, u16[bones] bone ids,
    //        u16[n2], (bones + 1) x 3x4 f32 skinned inverse bind matrices (row major, column vectors),
    //        extra bytes) ... u8 0.  Only n3 = n4 = 0 is known.
    //   ... ; u16 anim count, ...; constant pools: u32 pool count, per pool: u16 index, u16 n, f32[n]
    // The sections between the ones parsed here are not understood; each parsed section is anchored on the one before
    // it where the layout is known, and located by its own validated signature where it is not (node sets, constant
    // pools: see ParseNodeSetsAndSkeleton, FindConstPools; the rest table: ChprSkeleton.FindRestRecords).
    public sealed class Chpr
    {
        public sealed class HashList
        {
            public uint[] Hashes = Array.Empty<uint>();
            public int[] StringIds = Array.Empty<int>();
            public int Count => StringIds.Length;
        }

        public sealed class NodeSet
        {
            public int Offset;            // of the u16 count
            public int NameSet;           // hash list holding the node names
            public uint[] Ids = Array.Empty<uint>();
            public string[] Names = Array.Empty<string>();
            public int End => Offset + 4 + 4 * Ids.Length;
        }

        public sealed class RenderContext
        {
            public int Offset;            // of the u16 bone count
            public int Index;             // the context's own index byte
            public ushort[] BoneIds = Array.Empty<ushort>();
            public ushort[] Section2 = Array.Empty<ushort>();
            public float[][] InverseMatrices = Array.Empty<float[]>();   // 12 floats each: 3x4 row major, column vectors
            public byte[] Extra = Array.Empty<byte>();
        }

        public sealed class ConstPool
        {
            public int Offset;            // of the pool's u16 index
            public int Index;
            public float[] Values = Array.Empty<float>();
        }

        public sealed class AbsHeader
        {
            public int Offset, DescriptorCount, NodeCount, Num3, Num4, Num5, Num6;
        }

        public byte[] Data = Array.Empty<byte>();
        public readonly List<(int Offset, int Length, int Length2)> Partitions = new();
        public readonly List<(int Offset, int Size)> Anims = new();
        public int AnimTableEnd;
        public int FirstAnimId; // the animation table's first id (0 for Samus; Sylux's starts at 1)
        public string[] Strings = Array.Empty<string>();
        public HashList[] HashLists = Array.Empty<HashList>();
        public int NamePoolEnd;
        public NodeSet AnimNodes = new();
        public AbsHeader Abs = new();
        public NodeSet AbsNodes = new();
        public ushort[] ParentIds = Array.Empty<ushort>();      // per abs node: the parent's transform id (0xffff = root)
        public readonly Dictionary<int, int> AbsPositionOfTransform = new();   // transform index (NodeIndex) -> abs list position
        public ushort[] ParentAux = Array.Empty<ushort>();      // the second u16 of each parent entry (meaning unknown)
        public int ParentsEnd;
        public readonly List<RenderContext> RenderContexts = new();
        public int RenderContextsEnd;
        public readonly List<ConstPool> ConstPools = new();

        // a transform node id -> its transform index (ids are laid out in pages of 170, 3 steps of 0x80 apart). The
        // reference decoder (and so the golden outputs) takes this as the node's list position, which it is for the
        // first 294 nodes only -- see ChprSkeleton.ParentCorrected.
        public static int NodeIndex(uint id)
        {
            int lo = (int)(id & 0xffff);
            return (lo >> 7) / 3 + 170 * (lo & 0x7f);
        }

        // node kind byte of an anim node id (0x43 transform, 0x44 root motion, 0x40 bool, 0x45 event, 0x46 float)
        public static int NodeKind(uint id) => (int)((id >> 16) & 0xff);

        public static Chpr Read(Stream s)
        {
            using var m = new MemoryStream();
            s.CopyTo(m);
            return Parse(m.ToArray());
        }

        public static Chpr Parse(byte[] b)
        {
            var c = new Chpr { Data = b };
            if (b.Length < 0x2c || Encoding.ASCII.GetString(b, 0, 4) != "RFRM" || Encoding.ASCII.GetString(b, 0x14, 4) != "CHPR")
            {
                throw new InvalidDataException("not a CHPR (RFRM ... CHPR header missing)");
            }
            c.ParseTables();
            c.ParseNamePool();
            c.ParseNodeSetsAndSkeleton();
            c.ParseRenderContexts();
            c.FindConstPools();
            return c;
        }

        // ---------------------------------------------------------------- little-endian reads (throw past the end)
        internal static ushort U16(byte[] b, int o) => BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(o, 2));
        internal static uint U32(byte[] b, int o) => BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(o, 4));
        internal static int I32(byte[] b, int o) => BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(o, 4));
        internal static float F32(byte[] b, int o) => BinaryPrimitives.ReadSingleLittleEndian(b.AsSpan(o, 4));
        internal static ulong U64(byte[] b, int o) => BinaryPrimitives.ReadUInt64LittleEndian(b.AsSpan(o, 8));

        // the end of the region holding the character info: the first animation blob
        public int InfoEnd => Anims.Count > 0 ? Anims[0].Offset : Data.Length;

        public string AnimName(int i)
        {
            var h = ChprAnimCodec.RecordHeader.Parse(Data, Anims[i].Offset);
            return h.HasName != 0 && h.NameId < Strings.Length ? Strings[h.NameId] : $"anim{i}";
        }

        // ---------------------------------------------------------------- partition + animation tables
        void ParseTables()
        {
            byte[] b = Data;
            int o = 0x20;
            while (true)
            {
                if (o + 12 > b.Length) throw new InvalidDataException("CHPR: partition table runs past the end");
                uint off = U32(b, o), ln = U32(b, o + 4), ln2 = U32(b, o + 8);
                Partitions.Add(((int)off, (int)ln, (int)ln2));
                o += 12;
                if ((long)off + ln == b.Length) break;
            }
            // entries (id, offset, size) with consecutive ids; the first id need not be 0 (Sylux's CHPR 91605ed3 lists
            // 1..28: it has no animation 0 -- its first listed animation starts right where the character info ends)
            int first = -1;
            while (o + 12 <= b.Length)
            {
                uint i = U32(b, o), off = U32(b, o + 4), sz = U32(b, o + 8);
                if (first < 0)
                {
                    if (i > 0xffff) break;
                    first = (int)i;
                }
                if (i != first + Anims.Count || (long)off + sz > b.Length || sz == 0) break;
                Anims.Add(((int)off, (int)sz));
                o += 12;
                if ((long)off + sz == b.Length) break;
            }
            FirstAnimId = Math.Max(first, 0);
            AnimTableEnd = o;
        }

        // ---------------------------------------------------------------- name pool
        void ParseNamePool()
        {
            byte[] b = Data;
            int o = AnimTableEnd;
            long end = o + 4 + (long)U32(b, o);
            o += 4;
            if (end > b.Length) throw new InvalidDataException("CHPR: name pool runs past the end");
            var strings = new List<string>();
            while (o < end)
            {
                int z = Array.IndexOf(b, (byte)0, o, (int)(end - o));
                if (z < 0) throw new InvalidDataException("CHPR: unterminated name");
                strings.Add(Encoding.Latin1.GetString(b, o, z - o));
                o = z + 1;
            }
            Strings = strings.ToArray();
            int groups = U16(b, o), lists = U16(b, o + 2);
            o += 4;
            if (groups != 1) throw new NotSupportedException($"CHPR: {groups} name groups (only 1 is known)");
            long inGroups = 0;
            for (int g = 0; g < groups; g++, o += 4) inGroups += U32(b, o);
            var counts = new int[lists];
            for (int k = 0; k < lists; k++, o += 4) counts[k] = checked((int)U32(b, o));
            uint total = U32(b, o);
            o += 4;
            if (inGroups != Strings.Length || total != Strings.Length) throw new InvalidDataException("CHPR: name pool counts disagree with its strings");
            HashLists = new HashList[lists];
            for (int k = 0; k < lists; k++)
            {
                int n = checked((int)U32(b, o));
                o += 4;
                if (n != counts[k]) throw new InvalidDataException($"CHPR: hash list {k} holds {n} entries, its header says {counts[k]}");
                var h = new HashList { Hashes = new uint[n], StringIds = new int[n] };
                for (int i = 0; i < n; i++) h.Hashes[i] = U32(b, o + 4 * i);
                o += 4 * n;
                for (int i = 0; i < n; i++)
                {
                    uint s = U32(b, o + 4 * i);
                    if (s >= Strings.Length) throw new InvalidDataException($"CHPR: hash list {k} names string {s} of {Strings.Length}");
                    h.StringIds[i] = (int)s;
                }
                o += 4 * n;
                HashLists[k] = h;
            }
            NamePoolEnd = o;
        }

        // ---------------------------------------------------------------- node sets, skeleton parents
        // The anim node set is the first node-set header after the name pool (u16 n, u16 list with n = that hash list's
        // size) whose ids are distinct in their low 16 bits and do not number the nodes in order; the abs (skeleton)
        // context header follows it directly, and its node set is the first header after that with the header's node
        // count whose transform nodes (the first node's kind) number 0, 1, 2 ... in list order (NodeIndex). The parents
        // follow the abs node set directly.
        void ParseNodeSetsAndSkeleton()
        {
            byte[] b = Data;
            int limit = InfoEnd;
            NodeSet? anim = FindNodeSet(NamePoolEnd, limit, -1, ids => DistinctLow16(ids) && !TransformsInOrder(ids));
            if (anim == null) throw new InvalidDataException("CHPR: anim node set not found after the name pool");
            AnimNodes = anim;
            int o = anim.End;
            Abs = new AbsHeader
            {
                Offset = o, DescriptorCount = b[o], NodeCount = U16(b, o + 1), Num3 = U16(b, o + 3), Num4 = U16(b, o + 5),
                Num5 = U16(b, o + 7), Num6 = U16(b, o + 9),
            };
            NodeSet? abs = FindNodeSet(o + 11, limit, Abs.NodeCount, TransformsInOrder);
            if (abs == null) throw new InvalidDataException($"CHPR: skeleton node set ({Abs.NodeCount} nodes) not found after the anim node set");
            AbsNodes = abs;
            int n = abs.Ids.Length;
            for (int i = 0; i < n; i++)
            {
                if (NodeKind(abs.Ids[i]) == NodeKind(abs.Ids[0])) AbsPositionOfTransform[NodeIndex(abs.Ids[i])] = i;
            }
            o = abs.End;
            if (o + 4 * n > limit) throw new InvalidDataException("CHPR: skeleton parents run past the character info");
            ParentIds = new ushort[n];
            ParentAux = new ushort[n];
            for (int i = 0; i < n; i++)
            {
                ParentIds[i] = U16(b, o + 4 * i);
                ParentAux[i] = U16(b, o + 4 * i + 2);
                if (ParentIds[i] != 0xffff && NodeIndex(ParentIds[i]) >= n) throw new InvalidDataException($"CHPR: node {i}'s parent id {ParentIds[i]:x4} is out of range");
            }
            ParentsEnd = o + 4 * n;
        }

        NodeSet? FindNodeSet(int from, int to, int wantCount, Func<uint[], bool> accept)
        {
            byte[] b = Data;
            for (int o = from; o + 4 <= to; o++)
            {
                int n = U16(b, o), k = U16(b, o + 2);
                if (n == 0 || k >= HashLists.Length || n != HashLists[k].Count || (wantCount >= 0 && n != wantCount)) continue;
                if (o + 4 + 4L * n > to) continue;
                var ids = new uint[n];
                for (int i = 0; i < n; i++) ids[i] = U32(b, o + 4 + 4 * i);
                if (!accept(ids)) continue;
                var names = new string[n];
                for (int i = 0; i < n; i++) names[i] = Strings[HashLists[k].StringIds[i]];
                return new NodeSet { Offset = o, NameSet = k, Ids = ids, Names = names };
            }
            return null;
        }

        static bool DistinctLow16(uint[] ids)
        {
            var seen = new HashSet<uint>();
            foreach (uint x in ids)
            {
                if (!seen.Add(x & 0xffff)) return false;
            }
            return true;
        }

        // the nodes of the first node's kind carry transform indices 0, 1, 2 ... in list order
        static bool TransformsInOrder(uint[] ids)
        {
            int kind = NodeKind(ids[0]), k = 0;
            foreach (uint id in ids)
            {
                if (NodeKind(id) != kind) continue;
                if (NodeIndex(id) != k) return false;
                k++;
            }
            return k > 0;
        }

        // ---------------------------------------------------------------- render contexts (skinned inverse binds)
        void ParseRenderContexts()
        {
            byte[] b = Data;
            int o = ParentsEnd + 1;           // one byte (0xff) after the parents; meaning unknown
            int limit = InfoEnd;
            if (o >= limit || b[o] != 1) throw new InvalidDataException("CHPR: no render context after the skeleton parents");
            while (b[o] == 1)
            {
                if (RenderContexts.Count >= 64) throw new InvalidDataException("CHPR: render context chain does not end");
                o++;
                var rc = new RenderContext { Offset = o };
                int bones = U16(b, o), n2 = U16(b, o + 2), n3 = U16(b, o + 4), n4 = b[o + 6];
                long extra = U32(b, o + 7);
                rc.Index = b[o + 11];
                o += 12;
                if (n3 != 0 || n4 != 0) throw new NotSupportedException($"CHPR: render context {rc.Index} has sections ({n3}, {n4}) not seen in the reference file");
                if (o + 2L * (bones + n2) + 48L * (bones + 1) + extra + 1 > limit) throw new InvalidDataException("CHPR: render context runs past the character info");
                rc.BoneIds = new ushort[bones];
                for (int i = 0; i < bones; i++) rc.BoneIds[i] = U16(b, o + 2 * i);
                o += 2 * bones;
                rc.Section2 = new ushort[n2];
                for (int i = 0; i < n2; i++) rc.Section2[i] = U16(b, o + 2 * i);
                o += 2 * n2;
                rc.InverseMatrices = new float[bones + 1][];
                for (int m = 0; m <= bones; m++)
                {
                    var f = new float[12];
                    for (int i = 0; i < 12; i++)
                    {
                        f[i] = F32(b, o + 4 * i);
                        if (!float.IsFinite(f[i])) throw new InvalidDataException($"CHPR: render context {rc.Index} matrix {m} is not finite");
                    }
                    rc.InverseMatrices[m] = f;
                    o += 48;
                }
                rc.Extra = b.AsSpan(o, (int)extra).ToArray();
                o += (int)extra;
                RenderContexts.Add(rc);
            }
            if (b[o] != 0) throw new InvalidDataException("CHPR: render context chain ends badly");
            RenderContextsEnd = o + 1;
        }

        // ---------------------------------------------------------------- constant pools
        // Located by signature after the render contexts: the character's u16 anim count (= the animation table's),
        // ten bytes before a u32 pool count whose pools (u16 index 0, 1, ...; u16 n; f32[n]) chain up exactly.
        void FindConstPools()
        {
            byte[] b = Data;
            int limit = InfoEnd;
            // the pool chain sits behind a fixed tag: u16 1 then u32 0x01000000 (bytes 01 00 00 00 00 01). The u16 ten
            // bytes before the chain equals Samus's animation count (1670) but is 786 in Sylux's CHPR (28 animations),
            // so it is only a first-pass hint: search with it, then with the tag alone
            for (int pass = 0; pass < 2; pass++)
            {
                if (TryConstPools(b, limit, q => pass == 0 ? U16(b, q - 10) == Anims.Count : U16(b, q - 6) == 1 && U32(b, q - 4) == 0x01000000))
                {
                    return;
                }
            }
            throw new InvalidDataException("CHPR: constant pools not found after the render contexts");
        }

        bool TryConstPools(byte[] b, int limit, Func<int, bool> signature)
        {
            for (int q = Math.Max(RenderContextsEnd, 10); q + 4 <= limit; q++)
            {
                if (!signature(q)) continue;
                uint np = U32(b, q);
                if (np == 0 || np > 16) continue;
                int p = q + 4;
                bool ok = true;
                for (int k = 0; k < np && ok; k++)
                {
                    if (p + 4 > limit || U16(b, p) != k || U16(b, p + 2) == 0) { ok = false; break; }
                    p += 4 + 4 * U16(b, p + 2);
                    if (p > limit) ok = false;
                }
                if (!ok) continue;
                p = q + 4;
                for (int k = 0; k < np; k++)
                {
                    int n = U16(b, p + 2);
                    var pool = new ConstPool { Offset = p, Index = k, Values = new float[n] };
                    for (int i = 0; i < n; i++) pool.Values[i] = F32(b, p + 4 + 4 * i);
                    ConstPools.Add(pool);
                    p += 4 + 4 * n;
                }
                return true;
            }
            return false;
        }
    }
}
