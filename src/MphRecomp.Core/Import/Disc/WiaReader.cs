using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace MphRecomp.Import.Disc
{
    public enum WiaCompression { None = 0, Purge = 1, Bzip2 = 2, Lzma = 3, Lzma2 = 4, Zstd = 5 }

    // A Wii hash that differs from the one recomputed from the data (a WIA/RVZ "exception"): Sector counts from the
    // partition's first data sector, Offset is inside that sector's 0x400-byte hash block.
    public readonly record struct WiaHashException(long Sector, int Offset, byte[] Hash);

    // A WIA or RVZ disc image (WIA is wit's format by Dirk Clemens; RVZ is Dolphin's extension of it), read in place
    // over a seekable stream. Clean-room: written only from the format description docs/WiaAndRvz.md in the
    // dolphin-emu repository (github.com/dolphin-emu/dolphin; the description was read, no code was taken).
    //
    // The disc is split into raw data regions (everything outside Wii partition data; all of a GameCube disc) and Wii
    // partitions, whose data is stored DECRYPTED and without the 0x400-byte hash block of each 0x8000-byte sector (so
    // no key is needed). Both are cut into groups of chunk_size disc bytes, each compressed on its own (NONE, PURGE,
    // BZIP2, LZMA, LZMA2; RVZ adds Zstandard); RVZ can also replace junk padding with a generator seed (RvzPacking).
    // As an IDiscBlob this is the plain disc image, except that Wii partition data is read through Partitions[i]
    // (decrypted) instead: re-encrypting it into the original image is not implemented (nothing needs it).
    // Reads are thread-safe. A read spanning several groups decodes them in parallel (MaxParallelism); decoded groups
    // are kept in a small cache for the small, scattered reads of archives opened in place.
    public sealed class WiaReader : IDiscBlob, IDisposable
    {
        public const uint WiaMagic = 0x57494101;   // "WIA\x01"
        public const uint RvzMagic = 0x52565A01;   // "RVZ\x01"
        const uint WiaVersion = 0x01000000, WiaReadCompatible = 0x00080000;
        const uint RvzVersion = 0x01000000, RvzReadCompatible = 0x00030000;
        internal const int SectorSize = 0x8000, SectorData = 0x7C00, HashBlock = 0x400;
        const int SectorsPerExceptionList = 64;   // one list per 2 MiB of Wii sectors
        const int CacheSlots = 16;

        struct RawRegion
        {
            public long Offset, End, AlignedStart;
            public int GroupIndex, GroupCount;
        }

        internal struct PartRange
        {
            public long FirstSector, SectorCount;   // FirstSector relative to the partition's first data sector
            public int GroupIndex, GroupCount;
        }

        struct GroupEntry
        {
            public long Offset;
            public int Size;
            public bool Compressed;
            public int PackedSize;
        }

        sealed class CacheEntry
        {
            public int Group = -1;
            public byte[] Data = Array.Empty<byte>();
            public int Length;
            public WiaHashException[] Exceptions = Array.Empty<WiaHashException>();
            public long Stamp;
        }

        readonly Stream _s;
        readonly bool _leaveOpen;
        readonly object _fileLock = new();
        readonly object _cacheLock = new();
        readonly CacheEntry[] _cache = new CacheEntry[CacheSlots];
        long _clock;
        readonly byte[] _dhead;
        readonly byte[] _comprData;
        readonly RawRegion[] _raw;
        readonly GroupEntry[] _groups;
        readonly WiaPartition[] _parts;

        [ThreadStatic] static ZstdDecoder? t_zstd;
        [ThreadStatic] static LzmaDecoder? t_lzma;
        [ThreadStatic] static Bzip2Decoder? t_bzip2;
        [ThreadStatic] static JunkGenerator? t_junk;

        public bool IsRvz { get; }
        public uint Version { get; }
        public uint VersionCompatible { get; }
        public DiscPlatform Platform { get; }
        public WiaCompression Compression { get; }
        public int CompressionLevel { get; }
        public int ChunkSize { get; }
        public long Size { get; }                       // the original disc image's size
        public long FileSize { get; }
        public string GameId => Encoding.ASCII.GetString(_dhead, 0, 6);
        public IReadOnlyList<WiaPartition> Partitions => _parts;
        public int GroupCount => _groups.Length;

        // true when the stream starts like a WIA or RVZ file (the position is restored)
        public static bool Detect(Stream s)
        {
            if (!s.CanSeek || s.Length < 0x48) return false;
            long pos = s.Position;
            try
            {
                Span<byte> b = stackalloc byte[4];
                s.Seek(0, SeekOrigin.Begin);
                s.ReadExactly(b);
                uint m = BinaryPrimitives.ReadUInt32BigEndian(b);
                return m == WiaMagic || m == RvzMagic;
            }
            finally { s.Position = pos; }
        }

        public WiaReader(Stream s, bool leaveOpen = true)
        {
            if (!s.CanSeek) throw new ArgumentException("a disc image needs a seekable stream", nameof(s));
            _s = s; _leaveOpen = leaveOpen;
            for (int i = 0; i < CacheSlots; i++) _cache[i] = new CacheEntry();

            // ---- wia_file_head_t (0x48 bytes)
            byte[] head = ReadFile(0, 0x48);
            uint magic = Be.U32(head, 0);
            if (magic != WiaMagic && magic != RvzMagic) throw new InvalidDataException("not a WIA or RVZ file");
            IsRvz = magic == RvzMagic;
            Version = Be.U32(head, 4);
            VersionCompatible = Be.U32(head, 8);
            uint version = IsRvz ? RvzVersion : WiaVersion, readCompatible = IsRvz ? RvzReadCompatible : WiaReadCompatible;
            if (VersionCompatible > version || Version < readCompatible)
            {
                throw new NotSupportedException($"{(IsRvz ? "RVZ" : "WIA")} version 0x{Version:X8} (compatible 0x{VersionCompatible:X8}) is not supported");
            }
            if (!SHA1.HashData(head.AsSpan(0, 0x34)).AsSpan().SequenceEqual(head.AsSpan(0x34, 20)))
            {
                throw new InvalidDataException("WIA/RVZ header hash mismatch (damaged file)");
            }
            int discSize = (int)Be.U32(head, 0x0C);
            if (discSize < 0x90 || discSize > 0x1000) throw new InvalidDataException("bad wia_disc_t size");
            Size = (long)BinaryPrimitives.ReadUInt64BigEndian(head.AsSpan(0x24));
            FileSize = (long)BinaryPrimitives.ReadUInt64BigEndian(head.AsSpan(0x2C));

            // ---- wia_disc_t (right after; zero-padded when an older writer stored less)
            byte[] discRaw = ReadFile(0x48, discSize);
            if (!SHA1.HashData(discRaw).AsSpan().SequenceEqual(head.AsSpan(0x10, 20)))
            {
                throw new InvalidDataException("WIA/RVZ disc header hash mismatch (damaged file)");
            }
            var disc = new byte[Math.Max(discSize, 0xDC)];
            discRaw.CopyTo(disc, 0);
            uint discType = Be.U32(disc, 0x00);
            Platform = discType switch
            {
                1 => DiscPlatform.GameCube,
                2 => DiscPlatform.Wii,
                _ => throw new InvalidDataException($"WIA/RVZ: unknown disc type {discType}"),
            };
            uint comp = Be.U32(disc, 0x04);
            if (comp > 5 || (comp == 5 && !IsRvz) || (comp == 1 && IsRvz)) throw new InvalidDataException($"WIA/RVZ: bad compression {comp}");
            Compression = (WiaCompression)comp;
            CompressionLevel = Be.I32(disc, 0x08);
            ChunkSize = Be.I32(disc, 0x0C);
            bool chunkOk = IsRvz
                ? ChunkSize >= 0x8000 && (ChunkSize < 0x200000 ? (ChunkSize & (ChunkSize - 1)) == 0 : ChunkSize % 0x200000 == 0)
                : ChunkSize >= 0x200000 && ChunkSize % 0x200000 == 0;
            if (!chunkOk) throw new InvalidDataException($"WIA/RVZ: bad chunk size 0x{ChunkSize:X}");
            _dhead = disc.AsSpan(0x10, 0x80).ToArray();
            int nPart = (int)Be.U32(disc, 0x90);
            int partTSize = (int)Be.U32(disc, 0x94);
            long partOff = (long)BinaryPrimitives.ReadUInt64BigEndian(disc.AsSpan(0x98));
            int nRaw = (int)Be.U32(disc, 0xB4);
            long rawOff = (long)BinaryPrimitives.ReadUInt64BigEndian(disc.AsSpan(0xB8));
            int rawSize = (int)Be.U32(disc, 0xC0);
            int nGroups = (int)Be.U32(disc, 0xC4);
            long groupOff = (long)BinaryPrimitives.ReadUInt64BigEndian(disc.AsSpan(0xC8));
            int groupSize = (int)Be.U32(disc, 0xD0);
            int comprLen = Math.Min((int)disc[0xD4], 7);
            _comprData = disc.AsSpan(0xD5, comprLen).ToArray();
            if (Compression == WiaCompression.Lzma && _comprData.Length < 5) throw new InvalidDataException("WIA: missing LZMA properties");
            if (nPart < 0 || nPart > 64 || nRaw < 0 || nRaw > 1 << 16 || nGroups < 0 || nGroups > 1 << 24)
            {
                throw new InvalidDataException("WIA/RVZ: implausible table sizes");
            }

            // ---- wia_part_t[] (stored uncompressed, hashed)
            _parts = new WiaPartition[nPart];
            if (nPart > 0)
            {
                if (partTSize < 16 || partTSize > 0x1000) throw new InvalidDataException("WIA/RVZ: bad partition entry size");
                byte[] pt = ReadFile(partOff, nPart * partTSize);
                if (!SHA1.HashData(pt).AsSpan().SequenceEqual(disc.AsSpan(0xA0, 20)))
                {
                    throw new InvalidDataException("WIA/RVZ partition table hash mismatch (damaged file)");
                }
                for (int i = 0; i < nPart; i++)
                {
                    var e = new byte[Math.Max(partTSize, 0x30)];
                    Buffer.BlockCopy(pt, i * partTSize, e, 0, partTSize);
                    long first0 = Be.U32(e, 0x10);
                    var ranges = new PartRange[2];
                    for (int k = 0; k < 2; k++)
                    {
                        int o = 0x10 + k * 16;
                        ranges[k] = new PartRange
                        {
                            FirstSector = Be.U32(e, o) - first0,
                            SectorCount = Be.U32(e, o + 4),
                            GroupIndex = (int)Be.U32(e, o + 8),
                            GroupCount = (int)Be.U32(e, o + 12),
                        };
                    }
                    _parts[i] = new WiaPartition(this, i, e.AsSpan(0, 16).ToArray(), first0, ranges);
                }
            }

            // ---- wia_raw_data_t[] (compressed)
            byte[] rawTable = ReadTable(rawOff, rawSize, nRaw * 24);
            _raw = new RawRegion[nRaw];
            for (int i = 0; i < nRaw; i++)
            {
                int o = i * 24;
                long off = (long)BinaryPrimitives.ReadUInt64BigEndian(rawTable.AsSpan(o));
                long size = (long)BinaryPrimitives.ReadUInt64BigEndian(rawTable.AsSpan(o + 8));
                // groups start at the sector boundary at or before the region (the first region starts at 0x80)
                _raw[i] = new RawRegion
                {
                    Offset = off,
                    End = off + size,
                    AlignedStart = off - off % SectorSize,
                    GroupIndex = (int)Be.U32(rawTable, o + 16),
                    GroupCount = (int)Be.U32(rawTable, o + 20),
                };
            }
            Array.Sort(_raw, (a, b) => a.Offset.CompareTo(b.Offset));

            // ---- wia_group_t[] / rvz_group_t[] (compressed)
            int entrySize = IsRvz ? 12 : 8;
            byte[] groupTable = ReadTable(groupOff, groupSize, nGroups * entrySize);
            _groups = new GroupEntry[nGroups];
            for (int i = 0; i < nGroups; i++)
            {
                int o = i * entrySize;
                uint size = Be.U32(groupTable, o + 4);
                _groups[i] = new GroupEntry
                {
                    Offset = (long)Be.U32(groupTable, o) << 2,
                    Size = (int)(size & 0x7FFFFFFF),
                    Compressed = IsRvz ? (size & 0x80000000) != 0 : Compression > WiaCompression.Purge,
                    PackedSize = IsRvz ? (int)Be.U32(groupTable, o + 8) : 0,
                };
            }
            foreach (RawRegion r in _raw) CheckGroups(r.GroupIndex, r.GroupCount);
            foreach (WiaPartition p in _parts)
            {
                foreach (PartRange r in p.Ranges) CheckGroups(r.GroupIndex, r.GroupCount);
            }
        }

        void CheckGroups(int index, int count)
        {
            if (index < 0 || count < 0 || (long)index + count > _groups.Length) throw new InvalidDataException("WIA/RVZ: group index out of range");
        }

        byte[] ReadFile(long offset, int size)
        {
            var b = new byte[size];
            ReadFile(offset, b);
            return b;
        }

        void ReadFile(long offset, Span<byte> dst)
        {
            lock (_fileLock)
            {
                _s.Seek(offset, SeekOrigin.Begin);
                _s.ReadExactly(dst);
            }
        }

        // the raw-data and group tables: compressed with the file's method (PURGE: its segment format)
        byte[] ReadTable(long offset, int storedSize, int size)
        {
            byte[] stored = ReadFile(offset, storedSize);
            if (Compression == WiaCompression.None) return stored.Length >= size ? stored : throw new InvalidDataException("WIA/RVZ: table truncated");
            var result = new byte[size];
            if (Compression == WiaCompression.Purge)
            {
                Unpurge(stored, result);
                return result;
            }
            var tmp = new byte[size + 64];
            int n = Decompress(stored, tmp);
            if (n < size) throw new InvalidDataException("WIA/RVZ: table decompressed short");
            Buffer.BlockCopy(tmp, 0, result, 0, size);
            return result;
        }

        int Decompress(ReadOnlySpan<byte> src, Span<byte> dst)
        {
            switch (Compression)
            {
                case WiaCompression.Zstd: return (t_zstd ??= new ZstdDecoder()).Decompress(src, dst);
                case WiaCompression.Bzip2: return (t_bzip2 ??= new Bzip2Decoder()).Decompress(src, dst);
                case WiaCompression.Lzma: return (t_lzma ??= new LzmaDecoder()).DecompressLzma(src, _comprData, dst);
                case WiaCompression.Lzma2: return (t_lzma ??= new LzmaDecoder()).DecompressLzma2(src, dst);
                default: throw new InvalidOperationException();
            }
        }

        // PURGE: segments (u32 offset, u32 size, data) over a zeroed output, then a SHA-1 (not checked here)
        static void Unpurge(ReadOnlySpan<byte> src, Span<byte> dst)
        {
            dst.Clear();
            int p = 0, end = src.Length - 20;
            if (end < 0) throw new InvalidDataException("WIA purge data too short");
            while (p < end)
            {
                if (p + 8 > end) throw new InvalidDataException("WIA purge segment truncated");
                uint off = BinaryPrimitives.ReadUInt32BigEndian(src.Slice(p));
                uint size = BinaryPrimitives.ReadUInt32BigEndian(src.Slice(p + 4));
                p += 8;
                if (p + (long)size > end || off + (long)size > dst.Length) throw new InvalidDataException("WIA purge segment out of range");
                src.Slice(p, (int)size).CopyTo(dst.Slice((int)off));
                p += (int)size;
            }
        }

        // ---- reads: a request is cut into pieces, one per group it touches; several uncached groups decode in parallel

        // one group's share of a read
        readonly struct Piece
        {
            public readonly int Group, DataSize, Lists, Within, Length, DstOffset;
            public readonly long DataOffset, FirstSector;

            public Piece(int group, long dataOffset, int dataSize, int lists, long firstSector, int within, int length, int dstOffset)
            {
                Group = group; DataOffset = dataOffset; DataSize = dataSize; Lists = lists; FirstSector = firstSector;
                Within = within; Length = length; DstOffset = dstOffset;
            }
        }

        // how many groups one read may decode at once (1 = never in parallel)
        public int MaxParallelism { get; set; } = Environment.ProcessorCount;

        // ---- the disc view (IDiscBlob): header, raw regions; Wii partition data is not readable here
        public void Read(long offset, Span<byte> dst)
        {
            var pieces = new List<Piece>();
            int pos = 0;
            while (pos < dst.Length)
            {
                long at = offset + pos;
                int n;
                if (at >= Size)
                {
                    dst.Slice(pos).Clear();
                    break;
                }
                if (at < 0x80)
                {
                    n = (int)Math.Min(dst.Length - pos, 0x80 - at);
                    _dhead.AsSpan((int)at, n).CopyTo(dst.Slice(pos));
                }
                else if (FindRaw(at, out RawRegion r))
                {
                    long rel = at - r.AlignedStart;
                    int g = (int)(rel / ChunkSize);
                    int within = (int)(rel % ChunkSize);
                    if (g >= r.GroupCount) throw new InvalidDataException("WIA/RVZ: raw data offset past its groups");
                    long groupStart = r.AlignedStart + (long)g * ChunkSize;
                    int groupLen = (int)Math.Min(ChunkSize, r.End - groupStart);
                    n = (int)Math.Min(dst.Length - pos, Math.Min(groupLen - within, r.End - at));
                    pieces.Add(new Piece(r.GroupIndex + g, groupStart, groupLen, 0, 0, within, n, pos));
                }
                else
                {
                    long next = Size;
                    foreach (WiaPartition p in _parts)
                    {
                        if (at >= p.DiscOffset && at < p.DiscOffset + p.SectorCount * SectorSize)
                        {
                            throw new NotSupportedException("WIA/RVZ: Wii partition data is stored decrypted; read it through Partitions[i]");
                        }
                        if (p.DiscOffset > at) next = Math.Min(next, p.DiscOffset);
                    }
                    foreach (RawRegion q in _raw)
                    {
                        if (q.Offset > at) next = Math.Min(next, q.Offset);
                    }
                    n = (int)Math.Min(dst.Length - pos, next - at);   // a gap no region covers reads as zeros
                    dst.Slice(pos, n).Clear();
                }
                pos += n;
            }
            Fetch(pieces, dst);
        }

        bool FindRaw(long offset, out RawRegion region)
        {
            int lo = 0, hi = _raw.Length - 1;
            while (lo <= hi)
            {
                int mid = (lo + hi) >> 1;
                if (_raw[mid].End <= offset) lo = mid + 1;
                else if (_raw[mid].Offset > offset) hi = mid - 1;
                else
                {
                    region = _raw[mid];
                    return true;
                }
            }
            region = default;
            return false;
        }

        // ---- Wii partition data (decrypted, 0x7C00 bytes per disc sector)
        internal void ReadPartition(WiaPartition p, long offset, Span<byte> dst)
        {
            int spc = Math.Max(1, ChunkSize / SectorSize);
            long chunkData = (long)spc * SectorData;
            int lists = Math.Max(1, ChunkSize / 0x200000);
            var pieces = new List<Piece>();
            int pos = 0;
            while (pos < dst.Length)
            {
                long at = offset + pos;
                if (at >= p.Size)
                {
                    dst.Slice(pos).Clear();
                    break;
                }
                long sector = at / SectorData;
                int n = -1;
                long nextStart = p.SectorCount;
                foreach (PartRange r in p.Ranges)
                {
                    if (r.SectorCount == 0) continue;
                    if (sector >= r.FirstSector && sector < r.FirstSector + r.SectorCount)
                    {
                        long rangeOff = r.FirstSector * SectorData;
                        long rel = at - rangeOff;
                        int g = (int)(rel / chunkData);
                        int within = (int)(rel % chunkData);
                        if (g >= r.GroupCount) throw new InvalidDataException("WIA/RVZ: partition offset past its groups");
                        long groupSectors = Math.Min(spc, r.SectorCount - (long)g * spc);
                        int groupLen = (int)(groupSectors * SectorData);
                        n = Math.Min(dst.Length - pos, groupLen - within);
                        pieces.Add(new Piece(r.GroupIndex + g, rangeOff + g * chunkData, groupLen, lists, r.FirstSector + (long)g * spc, within, n, pos));
                        break;
                    }
                    if (r.FirstSector > sector) nextStart = Math.Min(nextStart, r.FirstSector);
                }
                if (n < 0)
                {
                    n = (int)Math.Min(dst.Length - pos, nextStart * SectorData - at);
                    dst.Slice(pos, n).Clear();
                }
                pos += n;
            }
            Fetch(pieces, dst);
        }

        void Fetch(List<Piece> pieces, Span<byte> dst)
        {
            int par = Math.Max(1, MaxParallelism);
            if (pieces.Count < 3 || par == 1)
            {
                foreach (Piece pc in pieces)
                {
                    CopyFromGroup(pc.Group, pc.DataOffset, pc.DataSize, pc.Lists, pc.FirstSector, pc.Within, dst.Slice(pc.DstOffset, pc.Length));
                }
                return;
            }
            // batches of uncached groups: their stored bytes read in a few large reads, decoded in parallel, copied in
            // order; the last one stays cached
            int batch = par * 4;
            for (int b = 0; b < pieces.Count; b += batch)
            {
                int m = Math.Min(batch, pieces.Count - b);
                var need = new List<int>(m);
                lock (_cacheLock)
                {
                    for (int k = 0; k < m; k++)
                    {
                        Piece pc = pieces[b + k];
                        CacheEntry? hit = FindCached(pc.Group);
                        if (hit != null) hit.Data.AsSpan(pc.Within, pc.Length).CopyTo(dst.Slice(pc.DstOffset));
                        else need.Add(b + k);
                    }
                }
                if (need.Count == 0) continue;
                var bufs = new byte[need.Count][];
                var excs = new WiaHashException[need.Count][];
                var stored = new (byte[]? Buf, int Off)[need.Count];
                var runs = new List<byte[]>();
                try
                {
                    ReadStoredRuns(pieces, need, stored, runs);
                    Parallel.For(0, need.Count, new ParallelOptions { MaxDegreeOfParallelism = par }, k =>
                    {
                        Piece pc = pieces[need[k]];
                        bufs[k] = DecodeToBuffer(pc.Group, pc.DataOffset, pc.DataSize, pc.Lists, pc.FirstSector, out excs[k], stored[k].Buf, stored[k].Off);
                    });
                }
                catch (AggregateException e)
                {
                    foreach (byte[] x in bufs)
                    {
                        if (x != null) ArrayPool<byte>.Shared.Return(x);
                    }
                    ExceptionDispatchInfo.Capture(e.InnerExceptions[0]).Throw();
                    throw;
                }
                finally
                {
                    foreach (byte[] r in runs) ArrayPool<byte>.Shared.Return(r);
                }
                for (int k = 0; k < need.Count; k++)
                {
                    Piece pc = pieces[need[k]];
                    bufs[k].AsSpan(pc.Within, pc.Length).CopyTo(dst.Slice(pc.DstOffset));
                    if (k == need.Count - 1)
                    {
                        lock (_cacheLock) AddCached(pc.Group, bufs[k], pc.DataSize, excs[k]);
                    }
                    else
                    {
                        ArrayPool<byte>.Shared.Return(bufs[k]);
                    }
                }
            }
        }

        // the stored bytes of pieces[need[k]], read as runs of neighbouring groups (at most 16 MiB, gaps under 64 KiB)
        void ReadStoredRuns(List<Piece> pieces, List<int> need, (byte[]? Buf, int Off)[] stored, List<byte[]> runs)
        {
            var order = new int[need.Count];
            for (int k = 0; k < order.Length; k++) order[k] = k;
            Array.Sort(order, (a, b) => _groups[pieces[need[a]].Group].Offset.CompareTo(_groups[pieces[need[b]].Group].Offset));
            int i = 0;
            while (i < order.Length)
            {
                GroupEntry first = _groups[pieces[need[order[i]]].Group];
                if (first.Size == 0)
                {
                    i++;
                    continue;
                }
                long start = first.Offset, end = first.Offset + first.Size;
                int j = i + 1;
                while (j < order.Length)
                {
                    GroupEntry g = _groups[pieces[need[order[j]]].Group];
                    if (g.Size == 0) { j++; continue; }
                    long gEnd = Math.Max(end, g.Offset + g.Size);
                    if (g.Offset < start || g.Offset > end + 0x10000 || gEnd - start > 16 << 20) break;
                    end = gEnd;
                    j++;
                }
                int len = (int)(end - start);
                byte[] run = ArrayPool<byte>.Shared.Rent(len);
                runs.Add(run);
                ReadFile(start, run.AsSpan(0, len));
                for (int k = i; k < j; k++)
                {
                    GroupEntry g = _groups[pieces[need[order[k]]].Group];
                    if (g.Size > 0) stored[order[k]] = (run, (int)(g.Offset - start));
                }
                i = j;
            }
        }

        // the hash exceptions of the partition's sectors [first, first + count)
        internal List<WiaHashException> PartitionExceptions(WiaPartition p, long first, long count)
        {
            int spc = Math.Max(1, ChunkSize / SectorSize);
            long chunkData = (long)spc * SectorData;
            int lists = Math.Max(1, ChunkSize / 0x200000);
            var result = new List<WiaHashException>();
            foreach (PartRange r in p.Ranges)
            {
                if (r.SectorCount == 0 || first >= r.FirstSector + r.SectorCount || first + count <= r.FirstSector) continue;
                int gFrom = (int)(Math.Max(0, first - r.FirstSector) / spc);
                int gTo = (int)Math.Min(r.GroupCount - 1, (Math.Min(first + count, r.FirstSector + r.SectorCount) - 1 - r.FirstSector) / spc);
                for (int g = gFrom; g <= gTo; g++)
                {
                    long gFirst = r.FirstSector + (long)g * spc;
                    long gCount = Math.Min(spc, r.SectorCount - (long)g * spc);
                    if (gFirst + gCount <= first || gFirst >= first + count) continue;
                    int groupLen = (int)(gCount * SectorData);
                    WiaHashException[] exc = GroupExceptions(r.GroupIndex + g, r.FirstSector * SectorData + g * chunkData, groupLen, lists, gFirst);
                    foreach (WiaHashException e in exc)
                    {
                        if (e.Sector >= first && e.Sector < first + count) result.Add(e);
                    }
                }
            }
            return result;
        }

        // Cached groups are only read or replaced under _cacheLock, so a buffer is never recycled mid-copy.
        void CopyFromGroup(int group, long dataOffset, int dataSize, int lists, long firstSector, int within, Span<byte> dst)
        {
            lock (_cacheLock)
            {
                CacheEntry? hit = FindCached(group);
                if (hit != null)
                {
                    hit.Data.AsSpan(within, dst.Length).CopyTo(dst);
                    return;
                }
            }
            byte[] data = DecodeToBuffer(group, dataOffset, dataSize, lists, firstSector, out WiaHashException[] exc);
            lock (_cacheLock)
            {
                CacheEntry e = AddCached(group, data, dataSize, exc);
                e.Data.AsSpan(within, dst.Length).CopyTo(dst);
            }
        }

        WiaHashException[] GroupExceptions(int group, long dataOffset, int dataSize, int lists, long firstSector)
        {
            lock (_cacheLock)
            {
                CacheEntry? hit = FindCached(group);
                if (hit != null) return hit.Exceptions;
            }
            byte[] data = DecodeToBuffer(group, dataOffset, dataSize, lists, firstSector, out WiaHashException[] exc);
            lock (_cacheLock)
            {
                return AddCached(group, data, dataSize, exc).Exceptions;
            }
        }

        CacheEntry? FindCached(int group)
        {
            foreach (CacheEntry e in _cache)
            {
                if (e.Group == group)
                {
                    e.Stamp = ++_clock;
                    return e;
                }
            }
            return null;
        }

        CacheEntry AddCached(int group, byte[] data, int length, WiaHashException[] exc)
        {
            CacheEntry victim = _cache[0];
            foreach (CacheEntry e in _cache)
            {
                if (e.Group == group)
                {
                    // another thread decoded it meanwhile
                    ArrayPool<byte>.Shared.Return(data);
                    e.Stamp = ++_clock;
                    return e;
                }
                if (e.Stamp < victim.Stamp) victim = e;
            }
            if (victim.Data.Length > 0) ArrayPool<byte>.Shared.Return(victim.Data);
            victim.Group = group;
            victim.Data = data;
            victim.Length = length;
            victim.Exceptions = exc;
            victim.Stamp = ++_clock;
            return victim;
        }

        // storedBuf/storedOff: the group's stored bytes when already read (else they are read here)
        byte[] DecodeToBuffer(int group, long dataOffset, int dataSize, int lists, long firstSector, out WiaHashException[] exc,
            byte[]? storedBuf = null, int storedOff = 0)
        {
            byte[] data = ArrayPool<byte>.Shared.Rent(dataSize);
            try
            {
                exc = DecodeGroup(group, dataOffset, dataSize, lists, firstSector, data, storedBuf, storedOff);
                return data;
            }
            catch (InvalidDataException e)
            {
                ArrayPool<byte>.Shared.Return(data);
                GroupEntry g = _groups[group];
                throw new InvalidDataException($"WIA/RVZ group {group} (file offset 0x{g.Offset:X}, {g.Size} bytes): {e.Message}", e);
            }
            catch
            {
                ArrayPool<byte>.Shared.Return(data);
                throw;
            }
        }

        WiaHashException[] DecodeGroup(int group, long dataOffset, int dataSize, int lists, long firstSector, byte[] outData,
            byte[]? storedBuf, int storedOff)
        {
            GroupEntry g = _groups[group];
            Span<byte> output = outData.AsSpan(0, dataSize);
            if (g.Size == 0)
            {
                output.Clear();
                return Array.Empty<WiaHashException>();
            }
            byte[]? stored = null;
            byte[]? inflated = null;
            try
            {
                ReadOnlySpan<byte> payload;
                if (storedBuf != null)
                {
                    payload = storedBuf.AsSpan(storedOff, g.Size);
                }
                else
                {
                    stored = ArrayPool<byte>.Shared.Rent(g.Size);
                    ReadFile(g.Offset, stored.AsSpan(0, g.Size));
                    payload = stored.AsSpan(0, g.Size);
                }
                bool padded = true;
                if (g.Compressed)
                {
                    // exception lists (at most 51 hashes a sector) + the data, packed or not
                    int spc = Math.Max(1, ChunkSize / SectorSize);
                    int excBound = lists == 0 ? 0 : lists * 2 + spc * 52 * 22;
                    int cap = excBound + Math.Max(dataSize, g.PackedSize) + 64;
                    inflated = ArrayPool<byte>.Shared.Rent(cap);
                    int n = Decompress(payload, inflated.AsSpan(0, cap));
                    payload = inflated.AsSpan(0, n);
                    padded = false;
                }
                int p = 0;
                WiaHashException[] exc = Array.Empty<WiaHashException>();
                if (lists > 0)
                {
                    // wia_except_list_t[lists]: u16 count, then (u16 offset among the hash blocks, SHA-1) each
                    List<WiaHashException>? list = null;
                    for (int j = 0; j < lists; j++)
                    {
                        if (p + 2 > payload.Length) throw new InvalidDataException("WIA/RVZ: exception list truncated");
                        int count = BinaryPrimitives.ReadUInt16BigEndian(payload.Slice(p));
                        p += 2;
                        for (int k = 0; k < count; k++)
                        {
                            if (p + 22 > payload.Length) throw new InvalidDataException("WIA/RVZ: exception list truncated");
                            int off = BinaryPrimitives.ReadUInt16BigEndian(payload.Slice(p));
                            (list ??= new List<WiaHashException>()).Add(new WiaHashException(
                                firstSector + (long)j * SectorsPerExceptionList + off / HashBlock, off % HashBlock, payload.Slice(p + 2, 20).ToArray()));
                            p += 22;
                        }
                    }
                    if (list != null) exc = list.ToArray();
                    if (padded) p = (p + 3) & ~3;   // NONE / PURGE / uncompressed RVZ groups align the data to 4
                }
                ReadOnlySpan<byte> data = payload.Slice(Math.Min(p, payload.Length));
                if (!IsRvz && Compression == WiaCompression.Purge)
                {
                    Unpurge(data, output);
                }
                else if (g.PackedSize != 0)
                {
                    RvzPacking.Unpack(data, output, dataOffset, t_junk ??= new JunkGenerator());
                }
                else
                {
                    if (data.Length < dataSize) throw new InvalidDataException($"WIA/RVZ: group {group} holds {data.Length} bytes, expected {dataSize}");
                    data.Slice(0, dataSize).CopyTo(output);
                }
                return exc;
            }
            finally
            {
                if (stored != null) ArrayPool<byte>.Shared.Return(stored);
                if (inflated != null) ArrayPool<byte>.Shared.Return(inflated);
            }
        }

        public Stream OpenDisc() => this.OpenWindow(0, Size);

        // diagnostics: where group i is stored
        internal (long Offset, int Size, bool Compressed, int PackedSize) StoredGroup(int i)
            => (_groups[i].Offset, _groups[i].Size, _groups[i].Compressed, _groups[i].PackedSize);

        public void Dispose()
        {
            if (!_leaveOpen) _s.Dispose();
        }
    }

    // One Wii partition of a WIA/RVZ file: its data, decrypted and without hashes (0x7C00 bytes per disc sector),
    // offset 0 = the partition's first data sector.
    public sealed class WiaPartition : IDiscBlob
    {
        readonly WiaReader _r;
        readonly byte[] _key;
        internal readonly WiaReader.PartRange[] Ranges;

        internal WiaPartition(WiaReader r, int index, byte[] key, long firstSector, WiaReader.PartRange[] ranges)
        {
            _r = r; Index = index; _key = key; FirstSector = firstSector; Ranges = ranges;
            long end = 0;
            foreach (WiaReader.PartRange x in ranges)
            {
                if (x.SectorCount > 0) end = Math.Max(end, x.FirstSector + x.SectorCount);
            }
            SectorCount = end;
        }

        public int Index { get; }
        public long FirstSector { get; }                                 // disc sector of the first data sector
        public long DiscOffset => FirstSector * WiaReader.SectorSize;    // = partition offset + its data offset
        public long SectorCount { get; }
        public long Size => SectorCount * WiaReader.SectorData;

        // the partition's title key, decrypted (WIA/RVZ store it so a reader can re-encrypt)
        public byte[] TitleKey => (byte[])_key.Clone();

        public void Read(long offset, Span<byte> dst) => _r.ReadPartition(this, offset, dst);

        // the stored hash exceptions for sectors [first, first + count)
        public IReadOnlyList<WiaHashException> HashExceptions(long first, long count) => _r.PartitionExceptions(this, first, count);
    }
}
