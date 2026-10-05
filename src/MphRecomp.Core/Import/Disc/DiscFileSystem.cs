using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;

namespace MphRecomp.Import.Disc
{
    public enum DiscPlatform { GameCube, Wii }

    // Random access to the bytes of a disc image, or of a Wii partition's decrypted data. Read is thread-safe.
    public interface IDiscBlob
    {
        long Size { get; }
        // fills dst from offset; bytes past Size read as zero
        void Read(long offset, Span<byte> dst);
    }

    // A seekable Stream as a thread-safe blob (the stream is shared, so reads take a lock).
    public sealed class StreamDiscBlob : IDiscBlob, IDisposable
    {
        readonly Stream _s;
        readonly bool _leaveOpen;
        readonly object _lock = new();

        public StreamDiscBlob(Stream s, bool leaveOpen = true)
        {
            if (!s.CanSeek) throw new ArgumentException("a disc image needs a seekable stream", nameof(s));
            _s = s; _leaveOpen = leaveOpen;
            Size = s.Length;
        }

        public long Size { get; }

        public void Read(long offset, Span<byte> dst)
        {
            int n = 0;
            if (offset < Size)
            {
                n = (int)Math.Min(dst.Length, Size - offset);
                lock (_lock)
                {
                    _s.Seek(offset, SeekOrigin.Begin);
                    _s.ReadExactly(dst.Slice(0, n));
                }
            }
            dst.Slice(n).Clear();
        }

        public void Dispose()
        {
            if (!_leaveOpen) _s.Dispose();
        }
    }

    public static class DiscBlobExtensions
    {
        // a read-only seekable window [start, start + length) over a blob
        public static Stream OpenWindow(this IDiscBlob blob, long start, long length)
            => new RangeStream(length, (at, buf) =>
            {
                blob.Read(start + at, buf);
                return buf.Length;
            });
    }

    // One file of a disc's file system. Offset is in the file system's own address space: the disc for a GameCube
    // disc, the decrypted (hash-free) partition data for a Wii partition; reading in Offset order reads a disc in order.
    public readonly record struct DiscFileInfo(string Path, long Offset, long Size);

    // The GameCube / Wii file system table over a blob, as an IFileSource: a GameCube disc (RVZ/WIA; a plain .iso goes
    // through GcDisc) or a Wii partition's decrypted data. The FST is 12-byte entries (flags + name offset, then file
    // offset + size, or for a directory its parent + the index after its last child) followed by the name strings; on
    // Wii the FST offset/size (0x424/0x428) and file offsets are stored >> 2. Same walk as GcDisc (prime_disc.py).
    public sealed class FstFileSystem : IFileSource
    {
        readonly IDiscBlob _data;
        readonly IDisposable? _owner;
        readonly List<DiscFileInfo> _files = new();
        readonly Dictionary<string, int> _byPath = new(StringComparer.Ordinal);
        Dictionary<string, int>? _byPathIgnoreCase;

        public string GameId { get; }
        public string Title { get; }
        public DiscPlatform Platform { get; }
        public IReadOnlyList<DiscFileInfo> Files => _files;     // FST order, with offsets and sizes
        public IEnumerable<string> Paths
        {
            get
            {
                foreach (DiscFileInfo f in _files) yield return f.Path;
            }
        }
        public IDiscBlob Data => _data;

        // data: the disc (GameCube) or the partition's decrypted data (Wii); owner is disposed with this
        public FstFileSystem(IDiscBlob data, DiscPlatform platform, IDisposable? owner = null)
        {
            _data = data; _owner = owner; Platform = platform;
            var h = new byte[0x440];
            data.Read(0, h);
            GameId = Encoding.ASCII.GetString(h, 0, 6);
            int e = Array.IndexOf(h, (byte)0, 0x20, 0x3E0);
            Title = Encoding.ASCII.GetString(h, 0x20, (e < 0 ? 0x400 : e) - 0x20);
            int shift = platform == DiscPlatform.Wii ? 2 : 0;
            long fstOff = (long)Be.U32(h, 0x424) << shift, fstSize = (long)Be.U32(h, 0x428) << shift;
            if (fstSize < 12 || fstSize > 64 << 20 || fstOff + fstSize > data.Size)
            {
                throw new InvalidDataException($"{GameId}: the file system table is out of range (0x{fstOff:X} + 0x{fstSize:X})");
            }
            var fst = new byte[fstSize];
            data.Read(fstOff, fst);
            int n = checked((int)Be.U32(fst, 8));
            if (n < 1 || (long)n * 12 > fstSize) throw new InvalidDataException($"{GameId}: bad file system table ({n} entries)");
            int strings = n * 12;
            string Name(int i) => Be.CString(fst, strings + (int)(Be.U32(fst, i * 12) & 0xFFFFFF), out _);
            void Walk(int i, int end, string prefix, int depth)
            {
                if (depth > 64) throw new InvalidDataException($"{GameId}: the file system table nests too deep");
                while (i < end)
                {
                    if (fst[i * 12] != 0)
                    {
                        // directory: its "size" is the index after its last child
                        int next = (int)Be.U32(fst, i * 12 + 8);
                        if (next <= i || next > end) throw new InvalidDataException($"{GameId}: bad directory entry {i}");
                        Walk(i + 1, next, prefix + Name(i) + "/", depth + 1);
                        i = next;
                    }
                    else
                    {
                        string path = prefix + Name(i);
                        _byPath[path] = _files.Count;
                        _files.Add(new DiscFileInfo(path, (long)Be.U32(fst, i * 12 + 4) << shift, Be.U32(fst, i * 12 + 8)));
                        i++;
                    }
                }
            }
            Walk(1, n, "", 0);
        }

        // by FST path: exact first, then ignoring case; '\' is accepted for '/', a leading '/' is ignored
        public bool TryGetFile(string path, out DiscFileInfo file)
        {
            path = path.Replace('\\', '/').TrimStart('/');
            if (!_byPath.TryGetValue(path, out int i))
            {
                if (_byPathIgnoreCase == null)
                {
                    var d = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                    for (int k = 0; k < _files.Count; k++) d.TryAdd(_files[k].Path, k);
                    Interlocked.CompareExchange(ref _byPathIgnoreCase, d, null);
                }
                if (!_byPathIgnoreCase.TryGetValue(path, out i))
                {
                    file = default;
                    return false;
                }
            }
            file = _files[i];
            return true;
        }

        DiscFileInfo Get(string path) => TryGetFile(path, out DiscFileInfo f) ? f
            : throw new FileNotFoundException($"{path}: not on the disc ({GameId})");

        public bool Contains(string path) => TryGetFile(path, out _);

        public long Length(string path) => Get(path).Size;

        // a file read in place (several may be open at once, from several threads)
        public Stream OpenRead(string path)
        {
            DiscFileInfo f = Get(path);
            return _data.OpenWindow(f.Offset, f.Size);
        }

        public byte[] ReadFile(string path)
        {
            DiscFileInfo f = Get(path);
            var b = new byte[f.Size];
            _data.Read(f.Offset, b);
            return b;
        }

        public void Dispose() => _owner?.Dispose();
    }
}
