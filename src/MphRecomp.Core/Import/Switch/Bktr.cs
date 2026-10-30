using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using MphRecomp.Import.Disc;

namespace MphRecomp.Import.Switch
{
    // A bucket tree (switchbrew "NCA" BucketTree / "RomFs patching"): its 0x10-byte header ("BKTR", u32 version,
    // u32 entry count, u32 reserved) sits in the FsHeader; the table itself is 0x4000-byte blocks in the section:
    // first the node block (u32 index, u32 bucket count, u64 end offset, then each bucket's u64 start offset), then the
    // buckets (u32 index, u32 entry count, u64 end offset, then the entries). Entries start with their u64 offset and are
    // sorted; each runs to the next entry's offset (the last to the tree's end offset).
    public sealed class BucketTree
    {
        public const uint Magic = 0x52544B42;   // "BKTR" little-endian
        const int BlockSize = 0x4000;

        public readonly long[] Offsets;          // each entry's start
        public readonly byte[][] Entries;        // each entry's bytes
        public readonly long EndOffset;

        // table: the decrypted table bytes; header: its 0x10-byte BucketTree header
        public BucketTree(byte[] table, ReadOnlySpan<byte> header, int entrySize)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(header) != Magic) throw new InvalidDataException("not a BKTR bucket tree");
            int count = (int)BinaryPrimitives.ReadUInt32LittleEndian(header[8..]);
            if (table.Length < BlockSize) throw new InvalidDataException("BKTR: table too small");
            int buckets = (int)BinaryPrimitives.ReadUInt32LittleEndian(table.AsSpan(4));
            EndOffset = (long)BinaryPrimitives.ReadUInt64LittleEndian(table.AsSpan(8));
            int perNode = (BlockSize - 0x10) / 8;
            // TODO: a tree of more buckets than one node block lists (> 2046 buckets: hundreds of thousands of entries)
            // has a second node level; not seen yet, so refused rather than guessed
            if (buckets < 1 || buckets > perNode) throw new NotSupportedException($"BKTR: {buckets} buckets (multi-level trees are not supported yet)");
            Offsets = new long[count];
            Entries = new byte[count][];
            int n = 0;
            for (int b = 0; b < buckets; b++)
            {
                int at = BlockSize * (1 + b);
                if (at + BlockSize > table.Length) throw new InvalidDataException("BKTR: bucket outside the table");
                int inBucket = (int)BinaryPrimitives.ReadUInt32LittleEndian(table.AsSpan(at + 4));
                if (inBucket < 0 || 0x10 + inBucket * entrySize > BlockSize || n + inBucket > count) throw new InvalidDataException("BKTR: bad bucket");
                for (int i = 0; i < inBucket; i++, n++)
                {
                    Entries[n] = table.AsSpan(at + 0x10 + i * entrySize, entrySize).ToArray();
                    Offsets[n] = (long)BinaryPrimitives.ReadUInt64LittleEndian(Entries[n]);
                    if (n > 0 && Offsets[n] < Offsets[n - 1]) throw new InvalidDataException("BKTR: entries out of order");
                }
            }
            if (n != count) throw new InvalidDataException($"BKTR: {n} entries in the buckets, header says {count}");
        }

        public int Count => Offsets.Length;

        // the entry holding `offset`
        public int Find(long offset)
        {
            int i = Array.BinarySearch(Offsets, offset);
            if (i < 0) i = ~i - 1;
            if (i < 0 || offset >= EndOffset) throw new InvalidDataException($"BKTR: offset 0x{offset:X} outside the tree");
            return i;
        }

        public long End(int i) => i + 1 < Offsets.Length ? Offsets[i + 1] : EndOffset;
    }

    // An update's patched RomFS section (switchbrew "RomFs patching"): the update's RomFS section holds new data, then
    // two bucket trees named by its PatchInfo (FsHeader 0x100: u64 offset, u64 size, header, for each):
    //   indirect (relocation) table, entries of 0x14: u64 offset in the patched section, u64 offset in the source,
    //       u32 source (0 the base game's RomFS section, 1 the update's own section)
    //   AES-CTR-EX (subsection) table, entries of 0x10: u64 offset in the update's section, u32 unused, u32 the
    //       subsection's counter generation (replacing the FsHeader's generation, the low half of the upper IV)
    // Both tables are encrypted with the section's plain AES-CTR. The patched section is a whole IVFC section of its
    // own, described by the update's FsHeader; its RomFS lists every file of the updated game. Thread-safe.
    public sealed class PatchedSection : IDiscBlob
    {
        readonly IDiscBlob _base;
        readonly CtrBlob _patch;
        readonly BucketTree _indirect, _ctrEx;
        readonly ulong _secureHigh;

        public long Size => _indirect.EndOffset;
        public int RelocationCount => _indirect.Count;
        public int SubsectionCount => _ctrEx.Count;

        // the bytes of the patched section that come from the update (the rest is the base game's)
        public long PatchBytes
        {
            get
            {
                long n = 0;
                for (int i = 0; i < _indirect.Count; i++)
                {
                    if (BinaryPrimitives.ReadInt32LittleEndian(_indirect.Entries[i].AsSpan(0x10)) == 1) n += _indirect.End(i) - _indirect.Offsets[i];
                }
                return n;
            }
        }

        // baseSection: the base game's decrypted RomFS section; patch: the update's RomFS section (its CtrBlob)
        public PatchedSection(IDiscBlob baseSection, CtrBlob patch, NcaSection patchSection)
        {
            _base = baseSection; _patch = patch;
            _secureHigh = (ulong)patchSection.SecureValue << 32;
            ReadOnlySpan<byte> info = patchSection.PatchInfo;
            _indirect = new BucketTree(ReadTable(info), info.Slice(0x10, 0x10), 0x14);
            _ctrEx = new BucketTree(ReadTable(info[0x20..]), info.Slice(0x30, 0x10), 0x10);
        }

        byte[] ReadTable(ReadOnlySpan<byte> info)
        {
            long off = (long)BinaryPrimitives.ReadUInt64LittleEndian(info);
            long size = (long)BinaryPrimitives.ReadUInt64LittleEndian(info[8..]);
            if (size <= 0 || size > 512 << 20 || off + size > _patch.Size) throw new InvalidDataException("BKTR: table outside the update's section");
            var t = new byte[size];
            _patch.Read(off, t);
            return t;
        }

        public void Read(long offset, Span<byte> dst)
        {
            while (dst.Length > 0)
            {
                if (offset >= Size)
                {
                    dst.Clear();
                    return;
                }
                int i = _indirect.Find(offset);
                byte[] e = _indirect.Entries[i];
                long src = (long)BinaryPrimitives.ReadUInt64LittleEndian(e.AsSpan(8)) + (offset - _indirect.Offsets[i]);
                int storage = BinaryPrimitives.ReadInt32LittleEndian(e.AsSpan(0x10));
                int n = (int)Math.Min(dst.Length, _indirect.End(i) - offset);
                if (storage == 0) _base.Read(src, dst[..n]);
                else if (storage == 1) ReadPatch(src, dst[..n]);
                else throw new InvalidDataException($"BKTR: relocation entry {i} has source {storage}");
                dst = dst[n..];
                offset += n;
            }
        }

        // the update's own bytes: each subsection with its own counter generation
        void ReadPatch(long offset, Span<byte> dst)
        {
            while (dst.Length > 0)
            {
                int i = _ctrEx.Find(offset);
                uint generation = BinaryPrimitives.ReadUInt32LittleEndian(_ctrEx.Entries[i].AsSpan(0x0C));
                int n = (int)Math.Min(dst.Length, _ctrEx.End(i) - offset);
                _patch.Read(offset, dst[..n], _secureHigh | generation);
                dst = dst[n..];
                offset += n;
            }
        }
    }
}
