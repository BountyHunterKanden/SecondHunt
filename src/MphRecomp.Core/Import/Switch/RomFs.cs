using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using MphRecomp.Import.Disc;

namespace MphRecomp.Import.Switch
{
    // A RomFS section's hash tree (switchbrew "NCA" HierarchicalIntegrity, "IVFC"): the FsHeader's hash data holds
    // "IVFC", u32 version, u32 master hash size, u32 layer count, then (layer count - 1) levels of 0x18 bytes (u64 offset
    // in the section, u64 size, u32 log2 block size, u32 reserved) and, at 0xC0, the master hash. Each level is cut into
    // blocks of its own block size; the SHA-256 of block j of level i is the j-th 32 bytes of level i - 1, and level 0
    // is hashed by the master hash. The last level is the RomFS itself. Checked against real files: a level's last,
    // short block is hashed zero-padded to the full block size. Level 0 has been one whole block in every file seen
    // (so whether the master hash would cover a shorter level 0 padded or not is untested; taken unpadded).
    public sealed class IvfcTree
    {
        public const uint Magic = 0x43465649;   // "IVFC" little-endian
        public readonly long[] Offset, Size;
        public readonly int[] BlockLog;
        public readonly byte[] MasterHash;
        public int Levels => Offset.Length;
        public long DataOffset => Offset[^1];
        public long DataSize => Size[^1];

        readonly IDiscBlob _section;
        readonly HashSet<(int, long)> _good = new();
        readonly object _lock = new();

        public IvfcTree(IDiscBlob section, ReadOnlySpan<byte> hashData)
        {
            _section = section;
            if (BinaryPrimitives.ReadUInt32LittleEndian(hashData) != Magic) throw new InvalidDataException("RomFS section: no IVFC hash header");
            int layers = (int)BinaryPrimitives.ReadUInt32LittleEndian(hashData[0x0C..]);
            int levels = layers - 1;
            if (levels < 1 || levels > 6) throw new InvalidDataException($"IVFC: {layers} layers");
            Offset = new long[levels]; Size = new long[levels]; BlockLog = new int[levels];
            for (int i = 0; i < levels; i++)
            {
                ReadOnlySpan<byte> l = hashData.Slice(0x10 + i * 0x18, 0x18);
                Offset[i] = (long)BinaryPrimitives.ReadUInt64LittleEndian(l);
                Size[i] = (long)BinaryPrimitives.ReadUInt64LittleEndian(l[8..]);
                BlockLog[i] = (int)BinaryPrimitives.ReadUInt32LittleEndian(l[16..]);
                if (BlockLog[i] < 4 || BlockLog[i] > 24) throw new InvalidDataException($"IVFC: level {i} block size 2^{BlockLog[i]}");
            }
            MasterHash = hashData.Slice(0xC0, 0x20).ToArray();
        }

        // checks every block of the data level that [offset, offset + length) touches, and the hash blocks above them
        // (each block once); throws InvalidDataException on a mismatch
        public void Check(long offset, long length)
        {
            int last = Levels - 1;
            if (length <= 0) return;
            long first = offset >> BlockLog[last], end = (offset + length - 1) >> BlockLog[last];
            lock (_lock)
            {
                for (long b = first; b <= end; b++) CheckBlock(last, b);
            }
        }

        public int CheckedBlocks { get { lock (_lock) return _good.Count; } }

        void CheckBlock(int level, long block)
        {
            if (_good.Contains((level, block))) return;
            int bs = 1 << BlockLog[level];
            long at = (long)block << BlockLog[level];
            if (at >= Size[level]) throw new InvalidDataException($"IVFC: block {block} past level {level}");
            int n = (int)Math.Min(bs, Size[level] - at);
            byte[] buf = new byte[level == 0 ? n : bs];   // short last blocks hash zero-padded (level 0: the master hash, exact)
            _section.Read(Offset[level] + at, buf.AsSpan(0, n));
            byte[] got = SHA256.HashData(buf);
            byte[] want;
            if (level == 0)
            {
                want = MasterHash;
            }
            else
            {
                long h = block * 32;
                CheckBlock(level - 1, h >> BlockLog[level - 1]);
                want = new byte[32];
                _section.Read(Offset[level - 1] + h, want);
            }
            if (!got.AsSpan().SequenceEqual(want))
            {
                throw new InvalidDataException($"IVFC hash mismatch: level {level}, block {block} (0x{Offset[level] + at:X} in the section): wrong key or damaged file");
            }
            _good.Add((level, block));
        }
    }

    // Small reads (RomFS tables, archive headers, scattered pak entries) come through a few cached 64 KiB chunks;
    // larger reads go straight through. Thread-safe.
    public sealed class BlockCache : IDiscBlob
    {
        const int ChunkLog = 16, Slots = 16;
        readonly IDiscBlob _inner;
        readonly byte[][] _data = new byte[Slots][];
        readonly long[] _chunk = new long[Slots];
        readonly long[] _stamp = new long[Slots];
        readonly object _lock = new();
        long _clock;

        public BlockCache(IDiscBlob inner)
        {
            _inner = inner;
            for (int i = 0; i < Slots; i++) { _data[i] = new byte[1 << ChunkLog]; _chunk[i] = -1; }
        }

        public long Size => _inner.Size;

        public void Read(long offset, Span<byte> dst)
        {
            if (dst.Length >= 1 << ChunkLog)
            {
                _inner.Read(offset, dst);
                return;
            }
            lock (_lock)
            {
                while (dst.Length > 0)
                {
                    long c = offset >> ChunkLog;
                    int within = (int)(offset & ((1 << ChunkLog) - 1));
                    int n = Math.Min(dst.Length, (1 << ChunkLog) - within);
                    Load(c).AsSpan(within, n).CopyTo(dst);
                    dst = dst[n..];
                    offset += n;
                }
            }
        }

        byte[] Load(long chunk)
        {
            int slot = 0;
            for (int i = 0; i < Slots; i++)
            {
                if (_chunk[i] == chunk) { _stamp[i] = ++_clock; return _data[i]; }
                if (_stamp[i] < _stamp[slot]) slot = i;
            }
            _chunk[slot] = -1;
            _inner.Read(chunk << ChunkLog, _data[slot]);
            _chunk[slot] = chunk;
            _stamp[slot] = ++_clock;
            return _data[slot];
        }
    }

    // A Switch title's RomFS (switchbrew "RomFS") as the game's file tree. The RomFS starts with a 0x50-byte header of
    // u64s: header size, directory hash table offset/size, directory table offset/size, file hash table offset/size,
    // file table offset/size, file data offset. Directory entries: u32 parent, sibling, first child directory, first
    // file, hash link, name length, name (padded to 4); file entries: u32 parent, sibling, u64 data offset, u64 size,
    // u32 hash link, u32 name length, name. 0xFFFFFFFF ends a list; the root directory is entry 0 (no name). Paths are
    // '/'-separated without the leading '/' ("Preload/DuplicateData.pak"); a lookup tries the exact spelling, then
    // ignores case (as FolderSource does). OpenRead streams decrypt in place, so multi-GB files are never copied.
    public sealed class SwitchRomFs : IFileSource
    {
        readonly IDiscBlob _data;          // the RomFS (the IVFC data level), through the cache
        readonly IvfcTree? _ivfc;
        readonly bool _verify;
        readonly IDisposable? _owner;
        readonly Dictionary<string, (long Offset, long Size)> _files = new(StringComparer.Ordinal);
        readonly Dictionary<string, string> _folded = new(StringComparer.OrdinalIgnoreCase);

        public string GameId => "";        // a Switch title id isn't a disc ID6 (TitleId has it)
        public string Title { get; }
        public ulong TitleId { get; }
        public bool Patched { get; }       // an update's BKTR patch is applied
        public IEnumerable<string> Paths => _files.Keys;
        public IvfcTree? Ivfc => _ivfc;
        public long DataSize => _data.Size;

        // section: the RomFS section's decrypted bytes (a patched one for an update); hashData: its FsHeader's IVFC
        // data. verify: every block read is checked against the hash tree first (slower; a self-test)
        public SwitchRomFs(IDiscBlob section, ReadOnlySpan<byte> hashData, ulong titleId, bool patched, bool verify = false, IDisposable? owner = null)
        {
            _owner = owner;
            var cached = new BlockCache(section);   // one cache for the RomFS and the hash levels above it
            _ivfc = new IvfcTree(cached, hashData);
            _verify = verify;
            TitleId = titleId; Patched = patched;
            Title = $"{titleId:X16}{(patched ? " (updated)" : "")}";
            if (_ivfc.DataOffset + _ivfc.DataSize > section.Size) throw new InvalidDataException("RomFS: the data level lies outside its section");
            _data = new SubBlob(cached, _ivfc.DataOffset, _ivfc.DataSize);

            var h = new byte[0x50];
            ReadData(0, h);
            if (BinaryPrimitives.ReadUInt64LittleEndian(h) != 0x50)
            {
                throw new SwitchKeyRequiredException("the RomFS doesn't decrypt (wrong title key or key area key in your keys?)");
            }
            long U(int o) => (long)BinaryPrimitives.ReadUInt64LittleEndian(h.AsSpan(o));
            long dirOff = U(0x18), dirSize = U(0x20), fileOff = U(0x38), fileSize = U(0x40), dataOff = U(0x48);
            if (dirSize > 256 << 20 || fileSize > 256 << 20 || dirOff + dirSize > _data.Size || fileOff + fileSize > _data.Size)
            {
                throw new InvalidDataException("RomFS: implausible tables");
            }
            var dirs = new byte[dirSize];
            var files = new byte[fileSize];
            ReadData(dirOff, dirs);
            ReadData(fileOff, files);
            Walk(dirs, files, 0, "", dataOff, 0);
        }

        const uint None = 0xFFFFFFFF;

        void Walk(byte[] dirs, byte[] files, uint dir, string prefix, long dataOff, int depth)
        {
            if (depth > 64) throw new InvalidDataException("RomFS: directories nested too deep");
            uint child = BinaryPrimitives.ReadUInt32LittleEndian(dirs.AsSpan((int)dir + 8));
            uint file = BinaryPrimitives.ReadUInt32LittleEndian(dirs.AsSpan((int)dir + 12));
            for (int guard = 0; file != None; guard++)
            {
                if (file + 0x20 > files.Length || guard > 1 << 22) throw new InvalidDataException("RomFS: bad file table");
                ReadOnlySpan<byte> e = files.AsSpan((int)file);
                long off = (long)BinaryPrimitives.ReadUInt64LittleEndian(e[8..]);
                long size = (long)BinaryPrimitives.ReadUInt64LittleEndian(e[16..]);
                int nameLen = (int)BinaryPrimitives.ReadUInt32LittleEndian(e[28..]);
                string path = prefix + Encoding.UTF8.GetString(e.Slice(0x20, nameLen));
                if (dataOff + off + size > _data.Size) throw new InvalidDataException($"RomFS: {path} lies outside the RomFS");
                _files[path] = (dataOff + off, size);
                _folded.TryAdd(path, path);
                file = BinaryPrimitives.ReadUInt32LittleEndian(e[4..]);
            }
            for (int guard = 0; child != None; guard++)
            {
                if (child + 0x18 > dirs.Length || guard > 1 << 20) throw new InvalidDataException("RomFS: bad directory table");
                ReadOnlySpan<byte> e = dirs.AsSpan((int)child);
                int nameLen = (int)BinaryPrimitives.ReadUInt32LittleEndian(e[20..]);
                string name = Encoding.UTF8.GetString(e.Slice(0x18, nameLen));
                Walk(dirs, files, child, prefix + name + "/", dataOff, depth + 1);
                child = BinaryPrimitives.ReadUInt32LittleEndian(e[4..]);
            }
        }

        void ReadData(long offset, Span<byte> dst)
        {
            if (_verify) _ivfc!.Check(offset, dst.Length);
            _data.Read(offset, dst);
        }

        bool TryFind(string path, out (long Offset, long Size) f)
        {
            path = path.Replace('\\', '/').TrimStart('/');
            if (_files.TryGetValue(path, out f)) return true;
            if (_folded.TryGetValue(path, out string? exact)) return _files.TryGetValue(exact, out f);
            return false;
        }

        (long Offset, long Size) Find(string path) => TryFind(path, out var f) ? f : throw new FileNotFoundException($"{path}: not in the game's RomFS");

        public bool Contains(string path) => TryFind(path, out _);

        public long Length(string path) => Find(path).Size;

        // where a file's bytes lie in the RomFS (for the hash self-test)
        public long DataOffsetOf(string path) => Find(path).Offset;

        public byte[] ReadFile(string path)
        {
            var (off, size) = Find(path);
            if (size > Array.MaxLength) throw new IOException($"{path}: {size} bytes is too large to read whole; use OpenRead");
            var b = new byte[size];
            ReadData(off, b);
            return b;
        }

        public Stream OpenRead(string path)
        {
            var (off, size) = Find(path);
            return new RangeStream(size, (at, buf) =>
            {
                ReadData(off + at, buf);
                return buf.Length;
            });
        }

        public void Dispose() => _owner?.Dispose();
    }
}
