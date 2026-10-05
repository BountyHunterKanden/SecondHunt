using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using MphRecomp.Import.Retro;

namespace MphRecomp.Import.Retro3
{
    // A Metroid Prime 3 .pak archive (version 2): a small header naming the section table's offset; the table lists
    // STRG (names: string, type, 64-bit id), RSHD (the resources: compressed flag, type, 64-bit id, size, offset into
    // DATA) and DATA, each section starting 64-byte aligned after the table. A compressed resource is "CMPD": a block
    // count, then per block (flags << 24 | stored size, decompressed size), then the blocks -- each stored raw (the two
    // sizes equal), one zlib stream, or LZO1X in size-prefixed segments like Echoes' (a negative size = stored).
    // Port of prime3_pak.py (the layout as PrimeWorldEditor's CPackage / CompressionUtil read it, MIT). Read in place over
    // a seekable stream: only the tables are read up front, each resource when asked for.
    public sealed class Pak3 : IDisposable
    {
        public const uint Version = 2;

        public readonly struct Entry
        {
            public readonly string Type;
            public readonly bool Compressed;
            public readonly long Offset;   // in DATA
            public readonly int Size;      // as stored
            public Entry(string type, bool compressed, long offset, int size) { Type = type; Compressed = compressed; Offset = offset; Size = size; }
        }

        public readonly string Name;
        readonly Stream _s;
        readonly bool _leaveOpen;
        readonly object _lock = new();
        public readonly Dictionary<string, (long Offset, long Size)> Sections = new(StringComparer.Ordinal);
        public readonly Dictionary<ulong, (string Type, string Name)> Names = new();
        // the first entry wins when an id is listed twice (Python's setdefault)
        public readonly Dictionary<ulong, Entry> Resources = new();
        public readonly List<ulong> Order = new();   // resource ids in table order
        public readonly long DataOffset;

        public Pak3(byte[] data, string name = "") : this(new MemoryStream(data, writable: false), name, leaveOpen: false) { }

        public Pak3(Stream s, string name = "", bool leaveOpen = true)
        {
            if (!s.CanSeek) throw new ArgumentException("a pak needs a seekable stream", nameof(s));
            _s = s; _leaveOpen = leaveOpen; Name = name;
            byte[] h = Read(0, 0x40);
            if (Be.U32(h, 0) != Version) throw new InvalidDataException($"{name}: pak version {Be.U32(h, 0)}");
            long hdr = Be.U32(h, 4);
            byte[] t = Read(hdr, 4 + 12 * 8);
            int n = checked((int)Be.U32(t, 0));
            var secs = new List<(string, long)>();
            int o = 4;
            for (int i = 0; i < n; i++)
            {
                secs.Add((Encoding.ASCII.GetString(t, o, 4), Be.U32(t, o + 4)));
                o += 8;
            }
            long pos = (hdr + 4 + 8 * n + 63) / 64 * 64;
            foreach ((string sname, long size) in secs)
            {
                Sections[sname] = (pos, size);
                pos += size;
            }
            if (Sections.TryGetValue("STRG", out (long Offset, long Size) st))
            {
                byte[] b = Read(st.Offset, checked((int)st.Size));
                int cnt = checked((int)Be.U32(b, 0));
                o = 4;
                for (int i = 0; i < cnt; i++)
                {
                    string nm = Be.CString(b, o, out int e);
                    o = e + 1;
                    string typ = Encoding.ASCII.GetString(b, o, 4);
                    ulong rid = U64(b, o + 4);
                    o += 12;
                    Names[rid] = (typ, nm);
                }
            }
            if (!Sections.TryGetValue("RSHD", out (long Offset, long Size) rs)) throw new InvalidDataException($"{name}: no RSHD section");
            {
                byte[] b = Read(rs.Offset, checked((int)rs.Size));
                int cnt = checked((int)Be.U32(b, 0));
                o = 4;
                for (int i = 0; i < cnt; i++)
                {
                    uint comp = Be.U32(b, o);
                    string typ = Encoding.ASCII.GetString(b, o + 4, 4);
                    ulong rid = U64(b, o + 8);
                    int size = checked((int)Be.U32(b, o + 16));
                    long off = Be.U32(b, o + 20);
                    o += 24;
                    if (Resources.TryAdd(rid, new Entry(typ, comp == 1, off, size))) Order.Add(rid);
                }
            }
            if (!Sections.TryGetValue("DATA", out (long Offset, long Size) da)) throw new InvalidDataException($"{name}: no DATA section");
            DataOffset = da.Offset;
        }

        public static ulong U64(byte[] b, int o) => System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(b.AsSpan(o, 8));

        // up to n bytes at offset (fewer at the end of the stream, like a Python file read)
        byte[] Read(long offset, int n)
        {
            lock (_lock)
            {
                long avail = Math.Max(0, Math.Min(n, _s.Length - offset));
                var b = new byte[avail];
                _s.Seek(offset, SeekOrigin.Begin);
                _s.ReadExactly(b);
                return b;
            }
        }

        public bool Contains(ulong id) => Resources.ContainsKey(id);

        public string TypeOf(ulong id) => Resources[id].Type;

        // the resource as stored (a compressed one still "CMPD")
        public byte[] Raw(ulong id)
        {
            if (!Resources.TryGetValue(id, out Entry e)) throw new KeyNotFoundException($"{id:x16} is not in {Name}");
            return Read(DataOffset + e.Offset, e.Size);
        }

        // the resource's bytes, decompressed
        public byte[] Get(ulong id)
        {
            Entry e = Resources.TryGetValue(id, out Entry x) ? x : throw new KeyNotFoundException($"{id:x16} is not in {Name}");
            byte[] b = Raw(id);
            if (!e.Compressed) return b;
            if (b.Length < 4 || b[0] != (byte)'C' || b[1] != (byte)'M' || b[2] != (byte)'P' || b[3] != (byte)'D')
            {
                throw new InvalidDataException($"{id:x16}: compressed without CMPD");
            }
            return Cmpd(b);
        }

        // the stated decompressed size of a CMPD resource (the sum of its blocks')
        public static long CmpdSize(byte[] b)
        {
            int nb = checked((int)Be.U32(b, 4));
            long total = 0;
            for (int i = 0; i < nb; i++) total += Be.U32(b, 12 + 8 * i);
            return total;
        }

        public static byte[] Cmpd(byte[] b)
        {
            int nb = checked((int)Be.U32(b, 4));
            int o = 8;
            var blocks = new (int C, int D)[nb];
            for (int i = 0; i < nb; i++)
            {
                blocks[i] = ((int)(Be.U32(b, o) & 0x00FFFFFF), checked((int)Be.U32(b, o + 4)));
                o += 8;
            }
            var output = new MemoryStream();
            foreach ((int csz, int dsz) in blocks)
            {
                byte[] seg = Be.Slice(b, o, csz);
                o += csz;
                if (csz == dsz)
                {
                    output.Write(seg);
                }
                else if (seg.Length >= 2 && seg[0] == 0x78 && (seg[1] == 0xDA || seg[1] == 0x9C || seg[1] == 0x01))
                {
                    output.Write(Pak.Inflate(seg, 0, seg.Length));
                }
                else
                {
                    // LZO, split into size-prefixed segments like Echoes' (a negative size = stored)
                    var part = new MemoryStream(dsz);
                    int ip = 0;
                    while (ip < seg.Length && part.Length < dsz)
                    {
                        int sz = Be.I16(seg, ip); ip += 2;
                        if (sz < 0)
                        {
                            part.Write(Be.Slice(seg, ip, -sz));
                            ip += -sz;
                        }
                        else
                        {
                            part.Write(Lzo1x.Decompress(Be.Slice(seg, ip, sz), dsz - (int)part.Length));
                            ip += sz;
                        }
                    }
                    output.Write(part.GetBuffer(), 0, (int)Math.Min(part.Length, dsz));
                }
            }
            return output.ToArray();
        }

        public void Dispose()
        {
            if (!_leaveOpen) _s.Dispose();
        }
    }

    // Every resource of several MP3 paks by id, the first pak in the list that holds it winning -- prime3_model.py's
    // res() / rtype() (which read every .pak of the disc's MP3 folder, sorted by name).
    public sealed class Pak3Set : IDisposable
    {
        public readonly List<Pak3> Paks;
        readonly Dictionary<ulong, (string Type, Pak3 Pak)> _where = new();

        public Pak3Set(IEnumerable<Pak3> paks)
        {
            Paks = paks.ToList();
            foreach (Pak3 pk in Paks)
            {
                foreach (ulong id in pk.Order) _where.TryAdd(id, (pk.Resources[id].Type, pk));
            }
        }

        // every .pak under `dir` of a game's file tree, in the reference's order (ordinal by name), each read in place
        public static Pak3Set FromSource(IFileSource src, string dir = "MP3")
        {
            string prefix = dir.Length == 0 ? "" : dir.TrimEnd('/') + "/";
            List<string> paths = src.Paths
                .Where(p => p.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && p.IndexOf('/', prefix.Length) < 0
                    && p.EndsWith(".pak", StringComparison.OrdinalIgnoreCase))
                .OrderBy(p => p, StringComparer.Ordinal).ToList();
            var paks = new List<Pak3>();
            foreach (string p in paths) paks.Add(new Pak3(src.OpenRead(p), p, leaveOpen: false));
            return new Pak3Set(paks);
        }

        public bool Contains(ulong id) => _where.ContainsKey(id);

        public string? TypeOf(ulong id) => _where.TryGetValue(id, out (string Type, Pak3 Pak) w) ? w.Type : null;

        public Pak3? PakOf(ulong id) => _where.TryGetValue(id, out (string Type, Pak3 Pak) w) ? w.Pak : null;

        public byte[] Get(ulong id) => _where.TryGetValue(id, out (string Type, Pak3 Pak) w) ? w.Pak.Get(id)
            : throw new KeyNotFoundException($"{id:x16} is in none of {Paks.Count} paks");

        public void Dispose()
        {
            foreach (Pak3 p in Paks) p.Dispose();
        }
    }
}
