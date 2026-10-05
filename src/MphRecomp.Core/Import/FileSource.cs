using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace MphRecomp.Import
{
    // A game's file tree, whatever holds it: a disc image's file system (GcDisc now; the Wii disc readers later) or a
    // folder the user extracted their disc to (FolderSource). Paths are '/'-separated and relative to the tree's root,
    // as the disc's FST names them ("SamusGun.pak", "MP3/SamusGun.pak"); a lookup tries the exact spelling first, then
    // ignores case. Files are read in place: OpenRead gives a seekable window, so a large archive is never copied.
    public interface IFileSource : IDisposable
    {
        string GameId { get; }          // ID6 when the source names it (a disc header, an extraction's sys/boot.bin), else ""
        string Title { get; }
        IEnumerable<string> Paths { get; }
        bool Contains(string path);
        long Length(string path);
        byte[] ReadFile(string path);
        Stream OpenRead(string path);   // seekable, read-only; the caller disposes it
    }

    // A read-only, seekable window [0, length) over something read at absolute offsets (a file inside a disc image).
    public sealed class RangeStream : Stream
    {
        readonly Func<long, Span<byte>, int> _readAt;   // (offset in the window, buffer) -> bytes read
        readonly long _length;
        long _pos;

        public RangeStream(long length, Func<long, Span<byte>, int> readAt)
        {
            _length = length; _readAt = readAt;
        }

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => _length;

        public override long Position
        {
            get => _pos;
            set => _pos = value < 0 ? throw new ArgumentOutOfRangeException(nameof(value)) : value;
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            long left = _length - _pos;
            if (left <= 0 || buffer.Length == 0) return 0;
            int n = (int)Math.Min(buffer.Length, left);
            int got = _readAt(_pos, buffer[..n]);
            _pos += got;
            return got;
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            Position = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => _pos + offset,
                _ => _length + offset,
            };
            return _pos;
        }

        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    // A game's file tree the user extracted to a folder (Dolphin's "Extract Entire Disc": <out>/DATA/files/...,
    // <out>/DATA/sys/boot.bin). The root is the folder the FST paths are relative to; its ID6 comes from sys/boot.bin
    // beside it when there is one.
    public sealed class FolderSource : IFileSource
    {
        readonly Dictionary<string, string> _files = new(StringComparer.Ordinal);   // relative path -> full path

        public string Root { get; }
        public string GameId { get; } = "";
        public string Title { get; } = "";
        public IEnumerable<string> Paths => _files.Keys;

        public FolderSource(string root)
        {
            Root = Path.GetFullPath(root);
            if (!Directory.Exists(Root)) throw new DirectoryNotFoundException(Root);
            foreach (string f in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories))
            {
                _files[Path.GetRelativePath(Root, f).Replace('\\', '/')] = f;
            }
            // <DATA>/files is the root and <DATA>/sys/boot.bin names the game (or <root>/sys when the root is the DATA folder)
            string? parent = Path.GetDirectoryName(Root);
            foreach (string boot in new[] { Path.Combine(Root, "sys", "boot.bin"), parent != null ? Path.Combine(parent, "sys", "boot.bin") : "" })
            {
                if (boot.Length == 0 || !File.Exists(boot)) continue;
                byte[] h = new byte[0x60];
                using (FileStream s = File.OpenRead(boot))
                {
                    int n = s.Read(h, 0, h.Length);
                    if (n < 0x40) continue;
                }
                bool printable = true;
                for (int i = 0; i < 6; i++) printable &= h[i] is >= 0x20 and < 0x7F;
                if (!printable) continue;
                GameId = Encoding.ASCII.GetString(h, 0, 6);
                int e = Array.IndexOf(h, (byte)0, 0x20);
                Title = Encoding.ASCII.GetString(h, 0x20, (e < 0 ? h.Length : e) - 0x20);
                break;
            }
        }

        // the root of the tree under (or just above) `folder` holding `marker` (a relative file or directory: "MP3"):
        // the folder itself, its files/ or DATA/files/ (a whole Dolphin extraction), or its parent (the user picked the
        // marker directory itself); null when none has it
        public static string? Locate(string folder, string marker)
        {
            folder = Path.GetFullPath(folder);
            var candidates = new List<string> { folder, Path.Combine(folder, "files"), Path.Combine(folder, "DATA", "files") };
            string? up = Path.GetDirectoryName(folder);
            if (up != null) candidates.Add(up);
            foreach (string c in candidates)
            {
                string m = Path.Combine(c, marker.Replace('/', Path.DirectorySeparatorChar));
                if (Directory.Exists(m) || File.Exists(m)) return c;
            }
            return null;
        }

        bool TryFind(string path, out string full)
        {
            path = path.Replace('\\', '/').TrimStart('/');
            if (_files.TryGetValue(path, out full!)) return true;
            foreach (KeyValuePair<string, string> kv in _files)
            {
                if (String.Equals(kv.Key, path, StringComparison.OrdinalIgnoreCase))
                {
                    full = kv.Value;
                    return true;
                }
            }
            full = "";
            return false;
        }

        string Find(string path) => TryFind(path, out string full) ? full : throw new FileNotFoundException($"{path}: not in {Root}");

        public bool Contains(string path) => TryFind(path, out _);

        public long Length(string path) => new FileInfo(Find(path)).Length;

        public byte[] ReadFile(string path) => File.ReadAllBytes(Find(path));

        public Stream OpenRead(string path) =>
            new FileStream(Find(path), FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.RandomAccess);

        public void Dispose() { }
    }
}
