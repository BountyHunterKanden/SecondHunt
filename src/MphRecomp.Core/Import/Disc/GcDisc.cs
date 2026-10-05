using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace MphRecomp.Import.Disc
{
    // A GameCube disc image (plain .iso / .gcm: GC discs are not encrypted) read in place over a seekable stream -- the
    // user's own disc, never copied. The header names the game (ID6 at 0) and the file system table (0x424 offset,
    // 0x428 size); the FST is 12-byte entries (flags + name offset, then file offset + size, or for a directory its
    // parent + the index after its last child) followed by the name strings. Port of prime_disc.py's Disc.
    // As an IFileSource it is one game's file tree for the importer (ImportService).
    public sealed class GcDisc : IFileSource
    {
        public const uint Magic = 0xC2339F3D;   // at 0x1C on every GameCube disc

        readonly Stream _s;
        readonly bool _leaveOpen;
        readonly object _lock = new();
        readonly Dictionary<string, (long Offset, int Size)> _files = new(StringComparer.Ordinal);

        public string GameId { get; }            // e.g. GM8E01 (Metroid Prime, NTSC), G2ME01 (Echoes)
        public byte DiscNumber { get; }
        public byte Revision { get; }
        public string Title { get; }
        public IReadOnlyDictionary<string, (long Offset, int Size)> Files => _files;

        public GcDisc(Stream s, bool leaveOpen = true)
        {
            if (!s.CanSeek) throw new ArgumentException("a disc image needs a seekable stream", nameof(s));
            _s = s; _leaveOpen = leaveOpen;
            byte[] h = Read(0, 0x440);
            if (Be.U32(h, 0x1C) != Magic)
            {
                throw new InvalidDataException("not a GameCube disc image");
            }
            GameId = Encoding.ASCII.GetString(h, 0, 6);
            DiscNumber = h[6]; Revision = h[7];
            int e = Array.IndexOf(h, (byte)0, 0x20, 0x3E0);
            Title = Encoding.ASCII.GetString(h, 0x20, (e < 0 ? 0x400 : e) - 0x20);
            uint fstOff = Be.U32(h, 0x424), fstSize = Be.U32(h, 0x428);
            byte[] fst = Read(fstOff, checked((int)fstSize));
            int n = checked((int)Be.U32(fst, 8));
            int strings = n * 12;
            string Name(int i)
            {
                int o = strings + (int)(Be.U32(fst, i * 12) & 0xFFFFFF);
                return Be.CString(fst, o, out _);
            }
            void Walk(int i, int end, string prefix)
            {
                while (i < end)
                {
                    if (fst[i * 12] != 0)
                    {
                        // directory: its "size" is the index after its last child
                        int next = (int)Be.U32(fst, i * 12 + 8);
                        Walk(i + 1, next, prefix + Name(i) + "/");
                        i = next;
                    }
                    else
                    {
                        _files[prefix + Name(i)] = (Be.U32(fst, i * 12 + 4), (int)Be.U32(fst, i * 12 + 8));
                        i++;
                    }
                }
            }
            Walk(1, n, "");
        }

        // true when the stream starts like a GameCube disc (the position is restored)
        public static bool Detect(Stream s)
        {
            if (!s.CanSeek || s.Length < 0x440) return false;
            long pos = s.Position;
            try
            {
                var b = new byte[4];
                s.Seek(0x1C, SeekOrigin.Begin);
                s.ReadExactly(b);
                return Be.U32(b, 0) == Magic;
            }
            finally { s.Position = pos; }
        }

        public byte[] Read(long offset, int size)
        {
            var b = new byte[size];
            lock (_lock)
            {
                _s.Seek(offset, SeekOrigin.Begin);
                _s.ReadExactly(b);
            }
            return b;
        }

        // a file by its FST path ("SamusGun.pak", "Audio/foo.dsp"): exact first, then ignoring case
        public bool TryFind(string path, out (long Offset, int Size) entry)
        {
            path = path.Replace('\\', '/').TrimStart('/');
            if (_files.TryGetValue(path, out entry)) return true;
            foreach (KeyValuePair<string, (long, int)> kv in _files)
            {
                if (String.Equals(kv.Key, path, StringComparison.OrdinalIgnoreCase))
                {
                    entry = kv.Value;
                    return true;
                }
            }
            return false;
        }

        public bool Contains(string path) => TryFind(path, out _);

        public IEnumerable<string> Paths => _files.Keys;

        public long Length(string path) => TryFind(path, out (long Offset, int Size) e) ? e.Size : throw new FileNotFoundException($"{path}: not on the disc ({GameId})");

        // a file read in place: a window over the disc stream (reads take the disc's lock)
        public Stream OpenRead(string path)
        {
            if (!TryFind(path, out (long Offset, int Size) e))
            {
                throw new FileNotFoundException($"{path}: not on the disc ({GameId})");
            }
            return new RangeStream(e.Size, (at, buf) =>
            {
                lock (_lock)
                {
                    _s.Seek(e.Offset + at, SeekOrigin.Begin);
                    _s.ReadExactly(buf);
                }
                return buf.Length;
            });
        }

        public byte[] ReadFile(string path)
        {
            if (!TryFind(path, out (long Offset, int Size) e))
            {
                throw new FileNotFoundException($"{path}: not on the disc ({GameId})");
            }
            return Read(e.Offset, e.Size);
        }

        public void Dispose()
        {
            if (!_leaveOpen) _s.Dispose();
        }
    }
}
