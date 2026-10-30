using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using MphRecomp.Import.Beyond;
using MphRecomp.Import.Retro3;

namespace MphRecomp.Import
{
    // Import retention, family mode (owner 2026-10-24, docs/IMPORT_RETENTION.md): the kept copy holds everything connected
    // to each item, not only the bytes today's importers read, so a later importer change to an existing item still
    // rebuilds without the disc. From the reads of an import:
    //   - an archive read in part (a Corruption .pak, a Beyond .pak) keeps every resource the reads touched, whole, plus
    //     the closure of the resources those link to by asset id (any archive of the same folder), plus its tables;
    //   - any other file read in part is kept whole (a single-file item: a Brawl archive, a music track);
    //   - files the item kinds declare (IImportKind.AlsoKeep: related data no asset id links to) are kept whole.
    // Except Beyond (Prime 4) archives (owner 2026-10-29): their GUID closure reaches nearly all of Preload and Worlds
    // (5.8 GB), so they keep only the reads, what the Sylux import needs (about 60 MB).
    // A link is any 64-bit id (Corruption) or 128-bit GUID (Beyond) of a resource of those archives found at any byte of a
    // resource's unpacked bytes; with ids that wide a chance match is practically impossible.
    // Files the importers read whole (the Prime 1 / Echoes paks, whose resources link only within their own pak) stay whole.
    public static class SourceFamily
    {
        public sealed class Stats
        {
            public long ReadBytes;     // what the importers read
            public long FamilyBytes;   // the family: reads + linked resources + declared files
            public long WholeBytes;    // every file the family touches, whole
            public int Resources;      // archive resources in the family
            public int Linked;         // of those, found only by a link
        }

        // off (owner 2026-10-29): Beyond archives keep only the reads; CloseBeyond stays for a later call
        public static bool CloseBeyondArchives = false;

        // expands `reads` in place (path as `inner` spells it -> ranges; null = the whole file)
        public static Stats Expand(IFileSource inner, Dictionary<string, RangeSet?> reads, IEnumerable<string> alsoKeep, CancellationToken ct)
        {
            var st = new Stats { ReadBytes = Total(inner, reads) };
            foreach (string p in alsoKeep)
            {
                string c = Canon(inner, p);
                if (inner.Contains(c)) reads[c] = null;
            }
            var pak3 = new List<string>();
            var beyond = new List<string>();
            foreach (string p in reads.Keys.ToList())
            {
                if (reads[p] == null) continue;
                switch (Sniff(inner, p))
                {
                    case "pak3": pak3.Add(p); break;
                    case "beyond": beyond.Add(p); break;
                    default: reads[p] = null; break;   // a single-file item: whole
                }
            }
            if (pak3.Count > 0) ClosePak3(inner, reads, pak3, st, ct);
            if (beyond.Count > 0 && CloseBeyondArchives) CloseBeyond(inner, reads, beyond, st, ct);
            st.FamilyBytes = Total(inner, reads);
            st.WholeBytes = reads.Keys.Where(inner.Contains).Sum(inner.Length);
            return st;
        }

        static long Total(IFileSource inner, Dictionary<string, RangeSet?> reads) =>
            reads.Sum(kv => !inner.Contains(kv.Key) ? 0 : kv.Value == null ? inner.Length(kv.Key) : RangeSet.Total(kv.Value.Merged()));

        static string Canon(IFileSource inner, string path)
        {
            path = path.Replace('\\', '/').TrimStart('/');
            if (inner.Contains(path)) return inner.Paths.FirstOrDefault(p => p.Equals(path, StringComparison.OrdinalIgnoreCase)) ?? path;
            return path;
        }

        static string Sniff(IFileSource inner, string path)
        {
            if (!path.EndsWith(".pak", StringComparison.OrdinalIgnoreCase) || inner.Length(path) < 64) return "";
            using Stream s = inner.OpenRead(path);
            var h = new byte[64];
            s.ReadExactly(h);
            if (h[0] == 'R' && h[1] == 'F' && h[2] == 'R' && h[3] == 'M' && h[0x14] == 'P' && h[0x15] == 'A' && h[0x16] == 'C' && h[0x17] == 'K') return "beyond";
            if (BinaryPrimitives.ReadUInt32BigEndian(h) == Pak3.Version) return "pak3";
            return "";
        }

        // the archives a link may land in: the ones read, and every other archive of the same kind in their folders
        static List<string> Siblings(IFileSource inner, IEnumerable<string> read, string kind)
        {
            var dirs = new HashSet<string>(read.Select(p => p.Contains('/') ? p[..p.LastIndexOf('/')] : ""), StringComparer.OrdinalIgnoreCase);
            var all = new List<string>(read);
            foreach (string p in inner.Paths)
            {
                string d = p.Contains('/') ? p[..p.LastIndexOf('/')] : "";
                if (!dirs.Contains(d) || all.Contains(p, StringComparer.OrdinalIgnoreCase)) continue;
                try { if (Sniff(inner, p) == kind) all.Add(p); }
                catch (Exception e) when (e is IOException or InvalidDataException) { }
            }
            all.Sort(StringComparer.Ordinal);
            return all;
        }

        static RangeSet Of(Dictionary<string, RangeSet?> reads, string path)
        {
            if (!reads.TryGetValue(path, out RangeSet? r) || r == null)
            {
                if (reads.ContainsKey(path)) return new RangeSet();   // whole already: nothing to add (a throwaway set)
                r = new RangeSet();
                reads[path] = r;
            }
            return r;
        }

        static bool Overlaps(List<(long Start, long End)> ranges, long start, long end)
        {
            int lo = 0, hi = ranges.Count - 1;
            while (lo <= hi)
            {
                int mid = (lo + hi) / 2;
                if (ranges[mid].End <= start) lo = mid + 1;
                else if (ranges[mid].Start >= end) hi = mid - 1;
                else return true;
            }
            return false;
        }

        // ---- Corruption (Trilogy) paks: 64-bit ids
        static void ClosePak3(IFileSource inner, Dictionary<string, RangeSet?> reads, List<string> read, Stats st, CancellationToken ct)
        {
            var paks = new List<(string Path, Pak3 Pak)>();
            try
            {
                foreach (string p in Siblings(inner, read, "pak3"))
                {
                    try { paks.Add((p, new Pak3(inner.OpenRead(p), p, leaveOpen: false))); }
                    catch (Exception e) when (e is IOException or InvalidDataException) { }
                }
                // id -> the archives holding it, in folder order
                var where = new Dictionary<ulong, List<int>>();
                for (int i = 0; i < paks.Count; i++)
                {
                    foreach (ulong id in paks[i].Pak.Order)
                    {
                        if (!where.TryGetValue(id, out List<int>? l)) where[id] = l = new List<int>(1);
                        l.Add(i);
                    }
                }
                var queue = new Queue<(int Pak, ulong Id)>();
                var seen = new HashSet<(int, ulong)>();
                var seenId = new HashSet<ulong>();
                for (int i = 0; i < paks.Count; i++)
                {
                    if (!reads.TryGetValue(paks[i].Path, out RangeSet? r) || r == null) continue;
                    List<(long Start, long End)> merged = r.Merged();
                    Pak3 pk = paks[i].Pak;
                    foreach (ulong id in pk.Order)
                    {
                        Pak3.Entry e = pk.Resources[id];
                        long s = pk.DataOffset + e.Offset;
                        if (Overlaps(merged, s, s + e.Size) && seen.Add((i, id))) { queue.Enqueue((i, id)); seenId.Add(id); }
                    }
                }
                int seeds = queue.Count;
                var held = new HashSet<int>();
                while (queue.Count > 0)
                {
                    ct.ThrowIfCancellationRequested();
                    (int pi, ulong id) = queue.Dequeue();
                    Pak3 pk = paks[pi].Pak;
                    Pak3.Entry e = pk.Resources[id];
                    Of(reads, paks[pi].Path).Add(pk.DataOffset + e.Offset, e.Size);
                    held.Add(pi);
                    st.Resources++;
                    byte[] b;
                    try { b = pk.Get(id); }
                    catch (Exception x) when (x is InvalidDataException or IOException or IndexOutOfRangeException or ArgumentException) { continue; }
                    for (int o = 0; o + 8 <= b.Length; o++)
                    {
                        ulong link = BinaryPrimitives.ReadUInt64BigEndian(b.AsSpan(o));
                        if (link == 0 || link == ulong.MaxValue || link == id || seenId.Contains(link) || !where.TryGetValue(link, out List<int>? at)) continue;
                        seenId.Add(link);
                        // the linking resource's own archive when it holds the link, else the first in folder order
                        int to = at.Contains(pi) ? pi : at[0];
                        if (seen.Add((to, link))) queue.Enqueue((to, link));
                    }
                }
                st.Linked += st.Resources - seeds;
                // the tables of every archive holding a family member
                foreach (int pi in held) Of(reads, paks[pi].Path).Add(0, paks[pi].Pak.DataOffset);
            }
            finally
            {
                foreach ((_, Pak3 p) in paks) p.Dispose();
            }
        }

        // ---- Beyond (Prime 4) archives: 128-bit GUIDs, plus each asset's side data
        static void CloseBeyond(IFileSource inner, Dictionary<string, RangeSet?> reads, List<string> read, Stats st, CancellationToken ct)
        {
            var arcs = new List<(string Path, BeyondArchive Arc, long TocEnd)>();
            try
            {
                foreach (string p in Siblings(inner, read, "beyond"))
                {
                    try
                    {
                        Stream s = inner.OpenRead(p);
                        var h = new byte[0x2C];
                        s.ReadExactly(h);
                        long toc = 64 + BinaryPrimitives.ReadInt64LittleEndian(h.AsSpan(0x24));
                        s.Position = 0;
                        arcs.Add((p, new BeyondArchive(s, p), toc));
                    }
                    catch (Exception e) when (e is IOException or InvalidDataException) { }
                }
                var where = new Dictionary<Guid, List<int>>();
                var lo8 = new HashSet<ulong>();   // a quick filter on a GUID's first 8 bytes
                Span<byte> g = stackalloc byte[16];
                for (int i = 0; i < arcs.Count; i++)
                {
                    foreach (BeyondArchive.Entry e in arcs[i].Arc.Entries)
                    {
                        if (!where.TryGetValue(e.Id, out List<int>? l)) where[e.Id] = l = new List<int>(1);
                        if (!l.Contains(i)) l.Add(i);
                        e.Id.TryWriteBytes(g);
                        lo8.Add(BinaryPrimitives.ReadUInt64LittleEndian(g));
                    }
                }
                var queue = new Queue<(int Arc, Guid Id)>();
                var seen = new HashSet<(int, Guid)>();
                var seenId = new HashSet<Guid>();
                for (int i = 0; i < arcs.Count; i++)
                {
                    if (!reads.TryGetValue(arcs[i].Path, out RangeSet? r) || r == null) continue;
                    List<(long Start, long End)> merged = r.Merged();
                    foreach (BeyondArchive.Entry e in arcs[i].Arc.Entries)
                    {
                        bool hit = Overlaps(merged, e.Offset, e.Offset + e.StoredLength)
                            || (e.HasSideData && Overlaps(merged, e.SideDataOffset, e.SideDataOffset + e.SideDataLength));
                        if (hit && seen.Add((i, e.Id))) { queue.Enqueue((i, e.Id)); seenId.Add(e.Id); }
                    }
                }
                int seeds = queue.Count;
                var held = new HashSet<int>();
                while (queue.Count > 0)
                {
                    ct.ThrowIfCancellationRequested();
                    (int ai, Guid id) = queue.Dequeue();
                    BeyondArchive arc = arcs[ai].Arc;
                    BeyondArchive.Entry e = arc.GetEntry(id);
                    RangeSet set = Of(reads, arcs[ai].Path);
                    set.Add(e.Offset, e.StoredLength);
                    if (e.HasSideData) set.Add(e.SideDataOffset, e.SideDataLength);
                    held.Add(ai);
                    st.Resources++;
                    byte[] b;
                    try { b = arc.ReadAsset(e); }
                    catch (Exception x) when (x is InvalidDataException or IOException or IndexOutOfRangeException or ArgumentException) { continue; }
                    byte[] side = e.HasSideData ? arc.ReadSideData(e) : Array.Empty<byte>();
                    foreach (byte[] part in new[] { b, side })
                    {
                        for (int o = 0; o + 16 <= part.Length; o++)
                        {
                            if (!lo8.Contains(BinaryPrimitives.ReadUInt64LittleEndian(part.AsSpan(o)))) continue;
                            var link = new Guid(part.AsSpan(o, 16));
                            if (link == id || link == Guid.Empty || seenId.Contains(link) || !where.TryGetValue(link, out List<int>? at)) continue;
                            seenId.Add(link);
                            int to = at.Contains(ai) ? ai : at[0];
                            if (seen.Add((to, link))) queue.Enqueue((to, link));
                        }
                    }
                }
                st.Linked += st.Resources - seeds;
                foreach (int ai in held) Of(reads, arcs[ai].Path).Add(0, arcs[ai].TocEnd);
            }
            finally
            {
                foreach ((_, BeyondArchive a, _) in arcs) a.Dispose();
            }
        }
    }
}
