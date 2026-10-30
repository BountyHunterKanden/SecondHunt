using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace MphRecomp.Import
{
    // Import retention (docs/IMPORT_RETENTION.md, Thread G): the files the importers read from a game's disc or ROM are
    // kept under <root>/source/<game>/ so the player can delete the disc image and every later importer change still
    // rebuilds from them. Only the player's own files, made on their own device; nothing here is ever exported or shipped.
    //   source.json          game, disc id, edition, per file: length, SHA-1 (when hashed), bytes kept
    //   files/<path>         the file as the importers name it (an edition's files by their recipe names); a file kept
    //                        whole is the whole file; a sparse one holds only the byte ranges the importers read, one
    //                        after the other
    //   files/<path>.ranges  a sparse file's ranges: (start, length) pairs of little-endian int64
    // The kept set is the whole files when they total at most the budget (500 MB, owner 2026-10-23), else only what was read.
    // A read of bytes that weren't kept throws SourceMissingException: that item needs the disc again.

    // bytes the kept files don't hold: the item needs its game's disc (or file) again
    public sealed class SourceMissingException : IOException
    {
        public string Path { get; }
        public SourceMissingException(string path) : base($"{path}: not in the copied files") { Path = path; }
    }

    // a source that already knows some files' SHA-1s (a kept set whose sparse files can't be hashed again)
    public interface IKnownHashes
    {
        bool TryKnownSha1(string path, out string sha1);
    }

    // sorted, merged [start, end) byte ranges
    public sealed class RangeSet
    {
        readonly List<(long Start, long End)> _r = new();

        public void Add(long start, long length)
        {
            if (length <= 0) return;
            long end = start + length;
            lock (_r)
            {
                // the usual read continues the last one
                if (_r.Count > 0)
                {
                    (long S, long E) last = _r[^1];
                    if (start >= last.S && start <= last.E)
                    {
                        if (end > last.E) _r[^1] = (last.S, end);
                        return;
                    }
                }
                _r.Add((start, end));
            }
        }

        public void AddAll(IEnumerable<(long Start, long End)> ranges)
        {
            foreach ((long s, long e) in ranges) Add(s, e - s);
        }

        public List<(long Start, long End)> Merged()
        {
            List<(long Start, long End)> copy;
            lock (_r) copy = _r.ToList();
            copy.Sort((a, b) => a.Start.CompareTo(b.Start));
            var merged = new List<(long Start, long End)>();
            foreach ((long s, long e) in copy)
            {
                if (merged.Count > 0 && s <= merged[^1].End)
                {
                    if (e > merged[^1].End) merged[^1] = (merged[^1].Start, e);
                }
                else merged.Add((s, e));
            }
            return merged;
        }

        public static long Total(List<(long Start, long End)> ranges) => ranges.Sum(r => r.End - r.Start);
    }

    // the real source with every read noted (path -> byte ranges). ReadFile notes the whole file.
    public sealed class RecordingSource : IFileSource
    {
        readonly IFileSource _inner;
        readonly ConcurrentDictionary<string, RangeSet> _reads = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, string>? _canon;

        public RecordingSource(IFileSource inner) { _inner = inner; }

        public IFileSource Inner => _inner;
        public string GameId => _inner.GameId;
        public string Title => _inner.Title;
        public IEnumerable<string> Paths => _inner.Paths;
        public bool Contains(string path) => _inner.Contains(path);
        public long Length(string path) => _inner.Length(path);

        // the source's own spelling of a path (the importers' spelling may differ in case)
        public string Canon(string path)
        {
            Dictionary<string, string> map = _canon ??= BuildCanon();
            return map.TryGetValue(path.Replace('\\', '/').TrimStart('/'), out string? real) ? real : path;
        }

        Dictionary<string, string> BuildCanon()
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string p in _inner.Paths) map[p] = p;
            return map;
        }

        // every path read, as the source spells it, with the ranges
        public IReadOnlyDictionary<string, RangeSet> Reads
        {
            get
            {
                var d = new Dictionary<string, RangeSet>(StringComparer.OrdinalIgnoreCase);
                foreach (KeyValuePair<string, RangeSet> kv in _reads) d[Canon(kv.Key)] = kv.Value;
                return d;
            }
        }

        RangeSet Of(string path) => _reads.GetOrAdd(Canon(path), _ => new RangeSet());

        public byte[] ReadFile(string path)
        {
            byte[] b = _inner.ReadFile(path);
            Of(path).Add(0, b.Length);
            return b;
        }

        public Stream OpenRead(string path) => new RecordingStream(_inner.OpenRead(path), Of(path));

        public void Dispose() { }   // the real source belongs to the caller
    }

    sealed class RecordingStream : Stream
    {
        readonly Stream _s;
        readonly RangeSet _set;

        public RecordingStream(Stream s, RangeSet set) { _s = s; _set = set; }

        public override bool CanRead => true;
        public override bool CanSeek => _s.CanSeek;
        public override bool CanWrite => false;
        public override long Length => _s.Length;
        public override long Position { get => _s.Position; set => _s.Position = value; }

        public override int Read(byte[] buffer, int offset, int count)
        {
            long at = _s.Position;
            int n = _s.Read(buffer, offset, count);
            if (n > 0) _set.Add(at, n);
            return n;
        }

        public override int Read(Span<byte> buffer)
        {
            long at = _s.Position;
            int n = _s.Read(buffer);
            if (n > 0) _set.Add(at, n);
            return n;
        }

        public override int ReadByte()
        {
            long at = _s.Position;
            int b = _s.ReadByte();
            if (b >= 0) _set.Add(at, 1);
            return b;
        }

        public override long Seek(long offset, SeekOrigin origin) => _s.Seek(offset, origin);
        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing) _s.Dispose();
            base.Dispose(disposing);
        }
    }

    // the kept files as a file source: reads what was kept, throws SourceMissingException for anything else
    public sealed class KeptSource : IFileSource, IKnownHashes
    {
        sealed class Entry
        {
            public long Length, Kept;
            public string Sha1 = "";
            public string File = "";
            public (long Start, long End, long Stored)[]? Ranges;   // null = the whole file; Stored: where the range starts in the file
        }

        readonly Dictionary<string, Entry> _files = new(StringComparer.OrdinalIgnoreCase);
        readonly string _dir;

        public string GameId { get; }
        public string Title { get; }
        public string EditionId { get; }
        public IEnumerable<string> Paths => _files.Keys;

        internal KeptSource(string dir, JsonElement manifest)
        {
            _dir = dir;
            GameId = manifest.TryGetProperty("discId", out JsonElement di) ? di.GetString() ?? "" : "";
            Title = manifest.TryGetProperty("title", out JsonElement ti) ? ti.GetString() ?? "" : "";
            EditionId = manifest.TryGetProperty("edition", out JsonElement ed) ? ed.GetString() ?? "" : "";
            if (!manifest.TryGetProperty("files", out JsonElement files)) return;
            foreach (JsonProperty p in files.EnumerateObject())
            {
                var e = new Entry
                {
                    Length = p.Value.GetProperty("length").GetInt64(),
                    Kept = p.Value.GetProperty("kept").GetInt64(),
                    Sha1 = p.Value.TryGetProperty("sha1", out JsonElement h) ? h.GetString() ?? "" : "",
                    File = SourceKeep.FilePath(dir, p.Name),
                };
                // a file that isn't there in full (a partial write, a deleted file) isn't kept
                if (!System.IO.File.Exists(e.File) || new FileInfo(e.File).Length != e.Kept) continue;
                if (e.Kept != e.Length)
                {
                    string sidecar = e.File + SourceKeep.RangesSuffix;
                    if (!System.IO.File.Exists(sidecar)) continue;
                    List<(long Start, long End)> r = SourceKeep.ReadRanges(sidecar);
                    if (RangeSet.Total(r) != e.Kept) continue;
                    var ranges = new (long, long, long)[r.Count];
                    long stored = 0;
                    for (int i = 0; i < r.Count; i++)
                    {
                        ranges[i] = (r[i].Start, r[i].End, stored);
                        stored += r[i].End - r[i].Start;
                    }
                    e.Ranges = ranges;
                }
                _files[p.Name] = e;
            }
        }

        public bool Contains(string path) => _files.ContainsKey(Norm(path));

        public long Length(string path) => Find(path).Length;

        public bool TryKnownSha1(string path, out string sha1)
        {
            sha1 = _files.TryGetValue(Norm(path), out Entry? e) ? e.Sha1 : "";
            return sha1.Length > 0;
        }

        public byte[] ReadFile(string path)
        {
            Entry e = Find(path);
            if (e.Ranges == null) return File.ReadAllBytes(e.File);
            if (e.Length > int.MaxValue) throw new SourceMissingException(path);
            byte[] b = new byte[e.Length];
            using Stream s = OpenRead(path);
            s.ReadExactly(b);
            return b;
        }

        public Stream OpenRead(string path)
        {
            Entry e = Find(path);
            var data = new FileStream(e.File, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.RandomAccess);
            if (e.Ranges == null) return data;
            (long Start, long End, long Stored)[] ranges = e.Ranges;
            return new RangeStream(e.Length, (pos, buf) =>
            {
                // the range holding pos (ranges are sorted)
                int lo = 0, hi = ranges.Length - 1, at = -1;
                while (lo <= hi)
                {
                    int mid = (lo + hi) >> 1;
                    if (pos < ranges[mid].Start) hi = mid - 1;
                    else if (pos >= ranges[mid].End) lo = mid + 1;
                    else { at = mid; break; }
                }
                if (at < 0) throw new SourceMissingException(path);
                var r = ranges[at];
                int n = (int)Math.Min(buf.Length, r.End - pos);
                data.Position = r.Stored + (pos - r.Start);
                return data.Read(buf[..n]);
            });
        }

        public void Dispose() { }

        static string Norm(string path) => path.Replace('\\', '/').TrimStart('/');

        Entry Find(string path) => _files.TryGetValue(Norm(path), out Entry? e) ? e : throw new SourceMissingException(path);
    }

    public static class SourceKeep
    {
        public const string RangesSuffix = ".ranges";

        public static string Dir(string root, string gameId) => Path.Combine(root, "source", gameId);

        public static string FilePath(string dir, string path) => Path.Combine(dir, "files", path.Replace('/', Path.DirectorySeparatorChar));

        static string ManifestPath(string dir) => Path.Combine(dir, "source.json");

        public static bool Exists(string root, string gameId) => File.Exists(ManifestPath(Dir(root, gameId)));

        // the bytes the kept files take on disk (what the game page shows)
        public static long Size(string root, string gameId)
        {
            string dir = Dir(root, gameId);
            if (!Directory.Exists(dir)) return 0;
            return Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length);
        }

        public static void Delete(string root, string gameId)
        {
            string dir = Dir(root, gameId);
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }

        public static KeptSource? Open(string root, string gameId)
        {
            string dir = Dir(root, gameId);
            if (!File.Exists(ManifestPath(dir))) return null;
            try
            {
                using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(ManifestPath(dir), Encoding.UTF8));
                return new KeptSource(dir, doc.RootElement);
            }
            catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or IOException)
            {
                return null;   // a damaged manifest reads as nothing kept
            }
        }

        // the items (id -> recipe hash:importer number) whose reads the kept files cover; empty for another edition's set
        public static Dictionary<string, string> ReadCovered(string root, string gameId, string editionId)
        {
            ReadHeld(Dir(root, gameId), out string heldEdition, out _, out _, out Dictionary<string, string> covered);
            // an item counts as covered only when this kept set is complete (its manifest was written)
            return heldEdition == editionId ? covered : new Dictionary<string, string>(StringComparer.Ordinal);
        }

        internal static List<(long Start, long End)> ReadRanges(string path)
        {
            byte[] b = File.ReadAllBytes(path);
            var r = new List<(long, long)>(b.Length / 16);
            for (int i = 0; i + 16 <= b.Length; i += 16)
            {
                long s = BitConverter.ToInt64(b, i), len = BitConverter.ToInt64(b, i + 8);
                r.Add((s, s + len));
            }
            return r;
        }

        static void WriteRanges(string path, List<(long Start, long End)> ranges)
        {
            var b = new byte[ranges.Count * 16];
            for (int i = 0; i < ranges.Count; i++)
            {
                BitConverter.TryWriteBytes(b.AsSpan(i * 16), ranges[i].Start);
                BitConverter.TryWriteBytes(b.AsSpan(i * 16 + 8), ranges[i].End - ranges[i].Start);
            }
            File.WriteAllBytes(path, b);
        }

        // what the kept set already holds for this edition: path -> (length, sha1, ranges; null = whole). Empty when the
        // manifest is from another edition (that one is replaced) or the original disc's is kept and this is an edition
        sealed class Held
        {
            public long Length;
            public string Sha1 = "";
            public List<(long Start, long End)>? Ranges;
        }

        static Dictionary<string, Held> ReadHeld(string dir, out string edition, out string discId, out string title,
            out Dictionary<string, string> covered)
        {
            var held = new Dictionary<string, Held>(StringComparer.OrdinalIgnoreCase);
            covered = new Dictionary<string, string>(StringComparer.Ordinal);
            edition = ""; discId = ""; title = "";
            if (!File.Exists(ManifestPath(dir))) return held;
            try
            {
                using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(ManifestPath(dir), Encoding.UTF8));
                JsonElement m = doc.RootElement;
                if (m.TryGetProperty("covered", out JsonElement cov) && cov.ValueKind == JsonValueKind.Object)
                {
                    foreach (JsonProperty c in cov.EnumerateObject()) covered[c.Name] = c.Value.GetString() ?? "";
                }
                edition = m.TryGetProperty("edition", out JsonElement ed) ? ed.GetString() ?? "" : "";
                discId = m.TryGetProperty("discId", out JsonElement di) ? di.GetString() ?? "" : "";
                title = m.TryGetProperty("title", out JsonElement ti) ? ti.GetString() ?? "" : "";
                foreach (JsonProperty p in m.GetProperty("files").EnumerateObject())
                {
                    long length = p.Value.GetProperty("length").GetInt64(), kept = p.Value.GetProperty("kept").GetInt64();
                    held[p.Name] = new Held
                    {
                        Length = length, Sha1 = p.Value.TryGetProperty("sha1", out JsonElement h) ? h.GetString() ?? "" : "",
                        Ranges = kept == length ? null : ReadRanges(FilePath(dir, p.Name) + RangesSuffix),
                    };
                }
            }
            catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or IOException)
            {
                held.Clear();
                covered.Clear();
                edition = ""; discId = ""; title = "";
            }
            return held;
        }

        // Store: keep the files `reads` names from `inner` (the real source, by its own spelling of the paths).
        //   whole: every path in `reads` (a null range set = the whole file) is kept whole; else only its ranges.
        //   hashes: the SHA-1 of the paths that were hashed.
        // Merges into what the same edition already holds; an original disc replaces an edition's set, and an edition
        // never replaces an original disc's (the original builds everything, an edition only its listed items).
        // Returns false when nothing was changed (the original disc's set was kept).
        public static bool Store(string root, string gameId, string title, string discId, string editionId, IFileSource inner,
            IReadOnlyDictionary<string, RangeSet?> reads, IReadOnlyDictionary<string, string> hashes,
            IReadOnlyDictionary<string, string> newlyCovered, bool whole, CancellationToken ct)
        {
            string dir = Dir(root, gameId);
            Dictionary<string, Held> held = ReadHeld(dir, out string heldEdition, out string heldDisc, out string heldTitle,
                out Dictionary<string, string> covered);
            if (held.Count > 0 && heldEdition != editionId)
            {
                if (editionId.Length > 0 && heldEdition.Length == 0) return false;   // the original disc's set stays
                Delete(root, gameId);                                                  // the original replaces an edition's
                held.Clear();
                covered.Clear();
            }
            foreach (KeyValuePair<string, string> kv in newlyCovered) covered[kv.Key] = kv.Value;
            Directory.CreateDirectory(Path.Combine(dir, "files"));
            // every path the set will hold, with its ranges (null = whole)
            var plan = new Dictionary<string, List<(long Start, long End)>?>(StringComparer.OrdinalIgnoreCase);
            var lengths = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            var sha = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, Held> kv in held)
            {
                plan[kv.Key] = kv.Value.Ranges;
                lengths[kv.Key] = kv.Value.Length;
                sha[kv.Key] = kv.Value.Sha1;
            }
            var rewrite = new HashSet<string>(StringComparer.OrdinalIgnoreCase);   // paths whose kept file is written again
            foreach (KeyValuePair<string, RangeSet?> kv in reads)
            {
                string path = kv.Key;
                long length = inner.Length(path);
                hashes.TryGetValue(path, out string? h);
                if (plan.TryGetValue(path, out List<(long Start, long End)>? have))
                {
                    // another revision of the file replaces it
                    bool same = lengths[path] == length && (String.IsNullOrEmpty(h) || String.IsNullOrEmpty(sha[path]) || String.Equals(h, sha[path], StringComparison.OrdinalIgnoreCase));
                    if (!same) { plan.Remove(path); have = null; }
                    else if (have == null && held.ContainsKey(path)) { continue; }   // kept whole already
                    else if (have != null && !whole)
                    {
                        var set = new RangeSet();
                        set.AddAll(have);
                        if (kv.Value != null) set.AddAll(kv.Value.Merged()); else set.Add(0, length);
                        plan[path] = set.Merged();
                        lengths[path] = length; if (!String.IsNullOrEmpty(h)) sha[path] = h!;
                        rewrite.Add(path);
                        continue;
                    }
                }
                lengths[path] = length;
                sha[path] = h ?? (plan.ContainsKey(path) ? sha[path] : "");
                if (whole || kv.Value == null) plan[path] = null;
                else plan[path] = kv.Value.Merged();
                rewrite.Add(path);
            }
            // a sparse path becomes whole when everything is kept whole
            if (whole)
            {
                foreach (string p in plan.Keys.ToList())
                {
                    if (plan[p] != null && inner.Contains(p)) { plan[p] = null; rewrite.Add(p); }
                }
            }
            foreach (string path in rewrite)
            {
                ct.ThrowIfCancellationRequested();
                if (!inner.Contains(path)) { continue; }
                string file = FilePath(dir, path);
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                string tmp = file + ".new";
                using (Stream src = inner.OpenRead(path))
                using (var dst = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
                {
                    List<(long Start, long End)>? ranges = plan[path];
                    if (ranges == null)
                    {
                        src.Position = 0;
                        src.CopyTo(dst, 1 << 20);
                    }
                    else
                    {
                        byte[] buf = new byte[1 << 20];
                        foreach ((long s, long e) in ranges)
                        {
                            ct.ThrowIfCancellationRequested();
                            src.Position = s;
                            long left = e - s;
                            while (left > 0)
                            {
                                int n = src.Read(buf, 0, (int)Math.Min(buf.Length, left));
                                if (n <= 0) throw new EndOfStreamException(path);
                                dst.Write(buf, 0, n);
                                left -= n;
                            }
                        }
                    }
                }
                File.Move(tmp, file, overwrite: true);
                string sidecar = file + RangesSuffix;
                if (plan[path] == null) { if (File.Exists(sidecar)) File.Delete(sidecar); }
                else WriteRanges(sidecar, plan[path]!);
            }
            // the manifest last: a kept set is complete or it is the old one
            var ms = new MemoryStream();
            using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
            {
                w.WriteStartObject();
                w.WriteNumber("version", 1);
                w.WriteString("game", gameId);
                w.WriteString("title", rewrite.Count > 0 || held.Count == 0 ? title : heldTitle);
                w.WriteString("discId", rewrite.Count > 0 || held.Count == 0 ? discId : heldDisc);
                w.WriteString("edition", editionId);
                w.WriteString("time", DateTime.UtcNow.ToString("o"));
                w.WriteStartObject("files");
                foreach (KeyValuePair<string, List<(long Start, long End)>?> kv in plan.OrderBy(k => k.Key, StringComparer.Ordinal))
                {
                    if (!File.Exists(FilePath(dir, kv.Key))) continue;
                    w.WriteStartObject(kv.Key);
                    w.WriteNumber("length", lengths[kv.Key]);
                    w.WriteNumber("kept", kv.Value == null ? lengths[kv.Key] : RangeSet.Total(kv.Value));
                    if (sha.TryGetValue(kv.Key, out string? sh) && sh.Length > 0) w.WriteString("sha1", sh);
                    w.WriteEndObject();
                }
                w.WriteEndObject();
                w.WriteStartObject("covered");
                foreach (KeyValuePair<string, string> kv in covered.OrderBy(k => k.Key, StringComparer.Ordinal)) w.WriteString(kv.Key, kv.Value);
                w.WriteEndObject();
                w.WriteEndObject();
            }
            string manifest = ManifestPath(dir);
            File.WriteAllBytes(manifest + ".new", ms.ToArray());
            File.Move(manifest + ".new", manifest, overwrite: true);
            return true;
        }
            // after a rebuild from the kept files: the items it made are covered at today's recipe/importer stamps (the kept
        // bytes held everything they read). Rewrites only the manifest's "covered"; a missing manifest is left alone.
        public static void MarkCovered(string root, string gameId, IReadOnlyDictionary<string, string> stamps)
        {
            if (stamps.Count == 0) return;
            string manifest = ManifestPath(Dir(root, gameId));
            if (!File.Exists(manifest)) return;
            if (System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllBytes(manifest)) is not System.Text.Json.Nodes.JsonObject o) return;
            if (o["covered"] is not System.Text.Json.Nodes.JsonObject cov) o["covered"] = cov = new System.Text.Json.Nodes.JsonObject();
            foreach (KeyValuePair<string, string> kv in stamps) cov[kv.Key] = kv.Value;
            File.WriteAllText(manifest + ".new", o.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            File.Move(manifest + ".new", manifest, overwrite: true);
        }
    }
}
