using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace MphRecomp.Import
{
    // a DS cartridge dump (.nds): its header and its file system (NitroFS: the name table walked from the root directory,
    // each file's bytes from the allocation table). Read from the user's own dump (First Hunt: FirstHuntArena).
    public sealed class NdsRom
    {
        readonly byte[] _rom;
        readonly Dictionary<string, (int Start, int End)> _files = new(StringComparer.OrdinalIgnoreCase);

        public string Title { get; }
        public string GameCode { get; }   // e.g. "AMFE" (First Hunt, US)
        public int Version { get; }
        public IReadOnlyCollection<string> Files => _files.Keys;

        public NdsRom(byte[] rom)
        {
            _rom = rom;
            if (rom.Length < 0x200) throw new InvalidDataException("not a DS cartridge image (too small)");
            Title = Encoding.ASCII.GetString(rom, 0, 12).TrimEnd('\0', ' ');
            GameCode = Encoding.ASCII.GetString(rom, 12, 4);
            Version = rom[0x1E];
            int fnt = I32(0x40), fat = I32(0x48), fatSize = I32(0x4C);
            if (fnt <= 0 || fat <= 0 || fnt >= rom.Length || fat + fatSize > rom.Length) throw new InvalidDataException("not a DS cartridge image (no file tables)");
            Walk(fnt, fat, fatSize / 8, 0xF000, "", 0);
        }

        public static NdsRom Read(Stream s)
        {
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            return new NdsRom(ms.ToArray());
        }

        int I32(int at) => BinaryPrimitives.ReadInt32LittleEndian(_rom.AsSpan(at, 4));

        void Walk(int fnt, int fat, int fileCount, int dirId, string path, int depth)
        {
            if (depth > 16) throw new InvalidDataException("DS file table too deep");
            int entry = fnt + (dirId & 0xFFF) * 8;
            int q = fnt + I32(entry);
            int fileId = BinaryPrimitives.ReadUInt16LittleEndian(_rom.AsSpan(entry + 4, 2));
            while (q < _rom.Length)
            {
                int t = _rom[q++];
                if (t == 0) break;
                int len = t & 0x7F;
                string name = Encoding.Latin1.GetString(_rom, q, len);
                q += len;
                if ((t & 0x80) != 0)
                {
                    int sub = BinaryPrimitives.ReadUInt16LittleEndian(_rom.AsSpan(q, 2));
                    q += 2;
                    Walk(fnt, fat, fileCount, sub, path + name + "/", depth + 1);
                }
                else
                {
                    if (fileId < fileCount)
                    {
                        int start = I32(fat + fileId * 8), end = I32(fat + fileId * 8 + 4);
                        if (start >= 0 && end >= start && end <= _rom.Length) _files[path + name] = (start, end);
                    }
                    fileId++;
                }
            }
        }

        // the main processor's code (arm9.bin as the cartridge holds it; First Hunt's is uncompressed) and where it loads
        public byte[] Arm9() => _rom.AsSpan(I32(0x20), I32(0x2C)).ToArray();
        public uint Arm9RamAddress => (uint)I32(0x28);

        // overlay n of the arm9 overlay table: its code and where it loads; null if there's no such overlay
        public (byte[] Code, uint RamAddress)? Arm9Overlay(int n)
        {
            int ovt = I32(0x50), size = I32(0x54);
            if (n < 0 || (n + 1) * 32 > size) return null;
            int entry = ovt + n * 32;
            uint ram = (uint)I32(entry + 4);
            int fileId = I32(entry + 0x18), fat = I32(0x48);
            int start = I32(fat + fileId * 8), end = I32(fat + fileId * 8 + 4);
            return (_rom.AsSpan(start, end - start).ToArray(), ram);
        }

        // a file by its path in the cartridge, e.g. "data/levels/models/mp1_Model.bin" (case ignored); null if absent
        public byte[]? Get(string path)
        {
            path = path.Replace('\\', '/').TrimStart('/');
            return _files.TryGetValue(path, out var f) ? _rom.AsSpan(f.Start, f.End - f.Start).ToArray() : null;
        }
    }
}
