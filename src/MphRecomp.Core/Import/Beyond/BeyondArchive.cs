using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;

namespace MphRecomp.Import.Beyond
{
    // A Metroid Prime 4: Beyond .pak archive (spec section 2), over a seekable read-only stream: only the table of
    // contents is read up front (a RFRM/PACK form whose first chunk-holder is a TOCC form with ADIR, META and STRG
    // chunks); an asset's bytes and its side data are read when asked for. The 3.85 GB Preload/DuplicateData.pak has
    // 21,276 entries and a 1.3 MB table of contents.
    //   ADIR  u32 n, n x 60: kind, GUID, u32 version A, u32 version B, s64 offset, s64 unpacked U, s64 stored P, u64
    //         (U == P: stored as is; otherwise a packed buffer, BeyondUnpack). The first entry for an id wins.
    //   META  u32 n, n x 20: GUID, u32 R. The side data starts at (META data start) + 4 + R and runs to the next larger
    //         side-data start, or the chunk's end.
    //   STRG  u32 n, per record: kind, GUID, u32 length, the ASCII name (kept for messages).
    // Clean-room: written from docs/cleanroom/G_beyond_spec.md alone.
    public sealed class BeyondArchive : IDisposable
    {
        public sealed class Entry
        {
            public string Kind = "";
            public Guid Id;
            public uint VersionA, VersionB;
            public long Offset;            // of the stored bytes, from the archive's start
            public long UnpackedLength;
            public long StoredLength;
            public long SideDataOffset = -1;   // from the archive's start; -1 = none
            public long SideDataLength;
            public bool Packed => UnpackedLength != StoredLength;
            public bool HasSideData => SideDataOffset >= 0;
        }

        readonly Stream stream;
        readonly bool leaveOpen;
        readonly List<Entry> entries = new();
        readonly Dictionary<Guid, Entry> byId = new();
        readonly Dictionary<Guid, string> names = new();

        public string Name { get; }
        public IReadOnlyList<Entry> Entries => entries;           // in ADIR order, duplicates included
        public IReadOnlyDictionary<Guid, string> Names => names;  // STRG names (first record per id)

        public static BeyondArchive Open(string path) =>
            new(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16), Path.GetFileName(path), false);

        public BeyondArchive(Stream s, string name = "", bool leaveOpen = false)
        {
            if (!s.CanSeek || !s.CanRead) throw new ArgumentException("a Beyond archive needs a readable, seekable stream", nameof(s));
            stream = s;
            this.leaveOpen = leaveOpen;
            Name = name;
            try
            {
                ReadToc();
            }
            catch
            {
                if (!leaveOpen) s.Dispose();
                throw;
            }
        }

        public void Dispose()
        {
            if (!leaveOpen) stream.Dispose();
        }

        void ReadAt(long offset, Span<byte> dst, string what)
        {
            if (offset < 0 || offset + dst.Length > stream.Length)
            {
                throw new InvalidDataException($"{Name}: {what} (0x{offset:x}, {dst.Length} bytes) lies outside the archive");
            }
            stream.Position = offset;
            stream.ReadExactly(dst);
        }

        void ReadToc()
        {
            Span<byte> head = stackalloc byte[64];
            ReadAt(0, head, "header");
            if (BeyondForm.Tag(head, 0) != "RFRM" || BeyondForm.Tag(head, 0x14) != "PACK")
            {
                throw new InvalidDataException($"{Name}: not a Beyond archive (no RFRM/PACK form)");
            }
            if (BeyondForm.Tag(head, 0x20) != "RFRM" || BeyondForm.Tag(head, 0x34) != "TOCC")
            {
                throw new InvalidDataException($"{Name}: no TOCC table of contents at +32");
            }
            long tocLength = BinaryPrimitives.ReadInt64LittleEndian(head.Slice(0x24));
            if (tocLength < 0 || 64 + tocLength > stream.Length || tocLength > int.MaxValue)
            {
                throw new InvalidDataException($"{Name}: table of contents length {tocLength} is out of range");
            }
            var toc = new byte[tocLength];
            ReadAt(64, toc, "table of contents");
            string what = $"{Name} table of contents";
            List<BeyondChunk> chunks = BeyondForm.ChunksIn(toc, 0, toc.Length, what);

            BeyondChunk adir = BeyondForm.Find(chunks, "ADIR", what);
            var c = new BeyondCursor(toc, adir.DataStart, adir.End, what + " ADIR");
            int n = c.Count(60, "asset");
            for (int i = 0; i < n; i++)
            {
                var e = new Entry
                {
                    Kind = c.Tag(), Id = c.Guid(), VersionA = c.U32(), VersionB = c.U32(),
                    Offset = c.S64(), UnpackedLength = c.S64(), StoredLength = c.S64(),
                };
                c.Skip(8);
                entries.Add(e);
                byId.TryAdd(e.Id, e);
            }

            foreach (BeyondChunk ch in chunks)
            {
                if (ch.Tag == "META") ReadMeta(toc, ch, what);
                else if (ch.Tag == "STRG") ReadNames(toc, ch, what);
            }
        }

        void ReadMeta(byte[] toc, BeyondChunk meta, string what)
        {
            var c = new BeyondCursor(toc, meta.DataStart, meta.End, what + " META");
            int m = c.Count(20, "side-data record");
            long baseInFile = 64L + meta.DataStart + 4;
            long chunkEnd = 64L + meta.End;
            var starts = new SortedSet<long>();
            var first = new Dictionary<Guid, long>();
            for (int i = 0; i < m; i++)
            {
                Guid id = c.Guid();
                long at = baseInFile + c.U32();
                if (at > chunkEnd) throw new InvalidDataException($"{what}: side data of {id} starts past the META chunk");
                starts.Add(at);
                first.TryAdd(id, at);
            }
            var sorted = new List<long>(starts);
            foreach (var (id, at) in first)
            {
                int k = sorted.BinarySearch(at);
                long end = k + 1 < sorted.Count ? sorted[k + 1] : chunkEnd;
                if (byId.TryGetValue(id, out Entry? e))
                {
                    e.SideDataOffset = at;
                    e.SideDataLength = end - at;
                }
            }
        }

        void ReadNames(byte[] toc, BeyondChunk strg, string what)
        {
            var c = new BeyondCursor(toc, strg.DataStart, strg.End, what + " STRG");
            int n = c.Count(24, "name");
            for (int i = 0; i < n; i++)
            {
                c.Skip(4);
                Guid id = c.Guid();
                int len = c.Count(1, "name length");
                int at = c.Pos;
                c.Skip(len);
                names.TryAdd(id, System.Text.Encoding.ASCII.GetString(toc, at, len).TrimEnd('\0'));
            }
        }

        public bool Contains(Guid id) => byId.ContainsKey(id);

        public bool TryGetEntry(Guid id, out Entry entry)
        {
            if (byId.TryGetValue(id, out Entry? e))
            {
                entry = e;
                return true;
            }
            entry = null!;
            return false;
        }

        public Entry GetEntry(Guid id) =>
            byId.TryGetValue(id, out Entry? e) ? e : throw new KeyNotFoundException($"asset {id} is not in {Name}");

        // "<kind> <id> (<name>)" for messages
        public string Describe(Guid id)
        {
            string kind = byId.TryGetValue(id, out Entry? e) ? e.Kind + " " : "";
            return names.TryGetValue(id, out string? nm) ? $"{kind}{id} ({nm})" : $"{kind}{id}";
        }

        // the asset's bytes (unpacked when stored packed): a form of the entry's kind
        public byte[] ReadAsset(Guid id) => ReadAsset(GetEntry(id));

        public byte[] ReadAsset(Entry e)
        {
            string what = $"{Name}: {e.Kind} {e.Id}";
            if (e.StoredLength < 0 || e.UnpackedLength < 0 || e.StoredLength > int.MaxValue || e.UnpackedLength > int.MaxValue)
            {
                throw new InvalidDataException($"{what}: lengths ({e.StoredLength} stored, {e.UnpackedLength} unpacked) are out of range");
            }
            var stored = new byte[e.StoredLength];
            ReadAt(e.Offset, stored, $"asset {e.Id}");
            byte[] asset = e.Packed ? BeyondUnpack.Unpack(stored, (int)e.UnpackedLength, what) : stored;
            BeyondForm.Expect(asset, e.Kind, what);
            return asset;
        }

        // the asset's side data (empty when it has none)
        public byte[] ReadSideData(Guid id) => ReadSideData(GetEntry(id));

        public byte[] ReadSideData(Entry e)
        {
            if (!e.HasSideData) return Array.Empty<byte>();
            var b = new byte[e.SideDataLength];
            ReadAt(e.SideDataOffset, b, $"side data of {e.Id}");
            return b;
        }
    }
}
