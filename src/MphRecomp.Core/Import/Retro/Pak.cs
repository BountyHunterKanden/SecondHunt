using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace MphRecomp.Import.Retro
{
    // A Retro .pak archive, version 3.5 (the Metroid Prime / Echoes layout): a name table (type, id, name) then the
    // resource table (compressed flag, type, id, size, offset). A compressed resource is its full size, then one zlib
    // stream (Prime 1) or size-prefixed segments (Echoes: LZO1X or zlib, a negative size = stored). Port of
    // prime_disc.py's Pak (formats as PrimeWorldEditor reads them, MIT).
    public sealed class Pak
    {
        public const uint Version = 0x00030005;

        public readonly struct Entry
        {
            public readonly string Type;
            public readonly bool Compressed;
            public readonly int Offset, Size;
            public Entry(string type, bool compressed, int offset, int size) { Type = type; Compressed = compressed; Offset = offset; Size = size; }
        }

        public readonly string Name;
        public readonly byte[] Data;
        public readonly Dictionary<uint, (string Type, string Name)> Names = new();
        // the first entry wins when an id is listed twice (Python's setdefault)
        public readonly Dictionary<uint, Entry> Resources = new();
        public readonly List<uint> Order = new();   // resource ids in table order

        public Pak(byte[] data, string name = "")
        {
            Name = name; Data = data;
            byte[] d = data;
            if (Be.U32(d, 0) != Version)
            {
                throw new InvalidDataException($"{name}: pak version {Be.U32(d, 0):x8}");
            }
            int o = 8;
            int nn = checked((int)Be.U32(d, o)); o += 4;
            for (int i = 0; i < nn; i++)
            {
                string typ = Encoding.ASCII.GetString(d, o, 4);
                uint rid = Be.U32(d, o + 4);
                int ln = checked((int)Be.U32(d, o + 8));
                string nm = Encoding.Latin1.GetString(d, o + 12, ln);
                o += 12 + ln;
                Names[rid] = (typ, nm);
            }
            int nr = checked((int)Be.U32(d, o)); o += 4;
            for (int i = 0; i < nr; i++)
            {
                uint comp = Be.U32(d, o);
                string typ = Encoding.ASCII.GetString(d, o + 4, 4);
                uint rid = Be.U32(d, o + 8);
                int size = checked((int)Be.U32(d, o + 12)), off = checked((int)Be.U32(d, o + 16));
                o += 20;
                if (Resources.TryAdd(rid, new Entry(typ, comp == 1, off, size)))
                {
                    Order.Add(rid);
                }
            }
        }

        public bool Contains(uint id) => Resources.ContainsKey(id);

        public string TypeOf(uint id) => Resources[id].Type;

        // the resource's bytes, decompressed
        public byte[] Get(uint id)
        {
            if (!Resources.TryGetValue(id, out Entry e))
            {
                throw new KeyNotFoundException($"{id:x8} is not in {Name}");
            }
            byte[] raw = Be.Slice(Data, e.Offset, e.Size);
            if (!e.Compressed) return raw;
            int full = checked((int)Be.U32(raw, 0));
            // Prime 1: one zlib stream; Echoes: size-prefixed segments (LZO / zlib) -- a segment size is never 0x78xx
            if (IsZlib(raw, 4))
            {
                return Inflate(raw, 4, raw.Length - 4);
            }
            return Segmented(raw, 4, full);
        }

        static bool IsZlib(byte[] b, int o) => b.Length >= o + 2 && b[o] == 0x78 && (b[o + 1] == 0xDA || b[o + 1] == 0x9C || b[o + 1] == 0x01);

        // one zlib stream; anything after its end (the pak's padding) is ignored
        public static byte[] Inflate(byte[] b, int o, int n)
        {
            using var z = new ZLibStream(new MemoryStream(b, o, n, writable: false), CompressionMode.Decompress);
            using var ms = new MemoryStream();
            z.CopyTo(ms);
            return ms.ToArray();
        }

        internal static byte[] Segmented(byte[] data, int start, int dstLen)
        {
            var output = new MemoryStream(dstLen);
            int ip = start;
            while (ip < data.Length && output.Length < dstLen)
            {
                int size = Be.I16(data, ip); ip += 2;
                if (size < 0)
                {
                    byte[] stored = Be.Slice(data, ip, -size);
                    output.Write(stored);
                    ip += -size;
                }
                else
                {
                    byte[] seg = Be.Slice(data, ip, size);
                    ip += size;
                    byte[] dec = IsZlib(seg, 0) ? Inflate(seg, 0, seg.Length) : Lzo1x.Decompress(seg, dstLen - (int)output.Length);
                    output.Write(dec);
                }
            }
            byte[] res = output.ToArray();
            return res.Length > dstLen ? res.AsSpan(0, dstLen).ToArray() : res;
        }
    }
}
