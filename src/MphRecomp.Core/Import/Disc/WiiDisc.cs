using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace MphRecomp.Import.Disc
{
    // One partition of a Wii disc, from the partition tables at 0x40000 and the partition header (ticket, TMD, H3 table
    // and data offsets; all unencrypted, readable without a key). Layout as documented on WiiBrew ("Wii disc").
    public sealed class WiiPartition
    {
        public int Index { get; init; }          // position in WiiDisc.Partitions
        public int Table { get; init; }          // partition table 0..3
        public uint Type { get; init; }          // 0 data, 1 update, 2 channel; others are a 4-character title id
        public long Offset { get; init; }        // disc offset of the partition (its ticket)
        public byte[] Ticket { get; init; } = Array.Empty<byte>();   // 0x2A4 bytes
        public byte[] Tmd { get; init; } = Array.Empty<byte>();
        public long DataOffset { get; init; }    // disc offset of the data area (0x8000-byte sectors)
        public long DataSize { get; init; }      // bytes of that area on disc
        public long H3Offset { get; init; }      // disc offset of the 0x18000-byte H3 table

        public ulong TitleId => Ticket.Length >= 0x1E4 ? (ulong)Be.U32(Ticket, 0x1DC) << 32 | Be.U32(Ticket, 0x1E0) : 0;
        public int CommonKeyIndex => Ticket.Length > 0x1F1 ? Ticket[0x1F1] : 0;   // 0 standard, 1 Korean, 2 vWii
        public long SectorCount => DataSize / WiiDisc.SectorSize;

        // the TMD's first content hash: the SHA-1 of the H3 table
        public byte[]? ContentHash => Tmd.Length >= 0x1F4 + 20 && Be.U16(Tmd, 0x1DE) > 0 ? Tmd.AsSpan(0x1F4, 20).ToArray() : null;

        public string TypeName => Type switch
        {
            0 => "DATA",
            1 => "UPDATE",
            2 => "CHANNEL",
            _ => Encoding.ASCII.GetString(new[] { (byte)(Type >> 24), (byte)(Type >> 16), (byte)(Type >> 8), (byte)Type }),
        };

        public override string ToString() => $"{TypeName} partition at 0x{Offset:X} (data 0x{DataOffset:X}, {DataSize / 1048576.0:0.0} MiB)";
    }

    // A Wii disc: plain .iso (encrypted, decrypted with the user's own common key), .wbfs (the same through
    // WbfsReader), or WIA/RVZ (partition data stored decrypted: no key). The app never contains a key.
    //
    // On disc, each partition's data is 0x8000-byte sectors: a 0x400-byte block of SHA-1 hashes (H0 over each 0x400
    // bytes of data, H1 over 8 sectors' H0, H2 over 8 H1 groups), AES-128-CBC encrypted with IV 0, then 0x7C00 bytes
    // of data, AES-128-CBC encrypted with the title key and the IV at 0x3D0 of the (encrypted) hash block. The title key
    // is in the ticket, AES-128-CBC encrypted with the common key and the IV (title id, 8 zero bytes).
    public sealed class WiiDisc : IDisposable
    {
        public const uint Magic = 0x5D1C9EA3;   // at 0x18 on every Wii disc
        public const int SectorSize = 0x8000, SectorData = 0x7C00, HashBlock = 0x400;

        readonly IDiscBlob _disc;
        readonly WiaReader? _wia;
        readonly WiiCommonKeys? _keys;
        readonly IDisposable? _owner;
        readonly List<WiiPartition> _parts = new();

        public string GameId { get; }
        public string Title { get; }
        public bool HasHashes { get; }         // header 0x60 == 0 (all retail discs)
        public bool IsEncrypted { get; }       // header 0x61 == 0 (all retail discs; WIA/RVZ store the data decrypted anyway)
        public bool IsWia => _wia != null;
        public IReadOnlyList<WiiPartition> Partitions => _parts;
        public WiiPartition? DataPartition => _parts.Find(p => p.Type == 0);

        // true when the stream starts like a plain Wii disc image (the position is restored)
        public static bool Detect(Stream s)
        {
            if (!s.CanSeek || s.Length < 0x50000) return false;
            long pos = s.Position;
            try
            {
                var b = new byte[4];
                s.Seek(0x18, SeekOrigin.Begin);
                s.ReadExactly(b);
                return Be.U32(b, 0) == Magic;
            }
            finally { s.Position = pos; }
        }

        // a plain .iso; keys: the user's own common key(s), needed only to read partition data
        public WiiDisc(Stream s, WiiCommonKeys? keys = null, bool leaveOpen = true)
            : this(new StreamDiscBlob(s, leaveOpen), keys, owner: null, ownsBlob: true) { }

        // a disc as a blob (e.g. WbfsReader); owner (if any) is disposed with this
        public WiiDisc(IDiscBlob disc, WiiCommonKeys? keys = null, IDisposable? owner = null)
            : this(disc, keys, owner, ownsBlob: false) { }

        // a WIA/RVZ image of a Wii disc: no key needed
        public WiiDisc(WiaReader wia, bool ownsReader = false)
            : this(CheckWii(wia), null, ownsReader ? wia : null, ownsBlob: false)
        {
            _wia = wia;
        }

        static WiaReader CheckWii(WiaReader wia) => wia.Platform == DiscPlatform.Wii ? wia
            : throw new InvalidDataException($"{wia.GameId} is a GameCube disc, not a Wii disc");

        WiiDisc(IDiscBlob disc, WiiCommonKeys? keys, IDisposable? owner, bool ownsBlob)
        {
            _disc = disc; _keys = keys;
            _owner = ownsBlob ? disc as IDisposable : owner;
            byte[] h = ReadRaw(0, 0x80);
            if (Be.U32(h, 0x18) != Magic) throw new InvalidDataException("not a Wii disc image");
            GameId = Encoding.ASCII.GetString(h, 0, 6);
            int e = Array.IndexOf(h, (byte)0, 0x20, 0x40);
            Title = Encoding.ASCII.GetString(h, 0x20, (e < 0 ? 0x60 : e) - 0x20);
            HasHashes = h[0x60] == 0;
            IsEncrypted = h[0x61] == 0;

            byte[] tables = ReadRaw(0x40000, 0x20);
            for (int t = 0; t < 4; t++)
            {
                int count = (int)Be.U32(tables, t * 8);
                long tableOff = (long)Be.U32(tables, t * 8 + 4) << 2;
                if (count == 0) continue;
                if (count > 64) throw new InvalidDataException($"{GameId}: implausible partition table ({count} entries)");
                byte[] entries = ReadRaw(tableOff, count * 8);
                for (int i = 0; i < count; i++)
                {
                    long off = (long)Be.U32(entries, i * 8) << 2;
                    uint type = Be.U32(entries, i * 8 + 4);
                    byte[] ph = ReadRaw(off, 0x2C0);
                    int tmdSize = (int)Be.U32(ph, 0x2A4);
                    long tmdOff = (long)Be.U32(ph, 0x2A8) << 2;
                    byte[] tmd = tmdSize > 0 && tmdSize <= 0x10000 ? ReadRaw(off + tmdOff, tmdSize) : Array.Empty<byte>();
                    _parts.Add(new WiiPartition
                    {
                        Index = _parts.Count,
                        Table = t,
                        Type = type,
                        Offset = off,
                        Ticket = ph.AsSpan(0, 0x2A4).ToArray(),
                        Tmd = tmd,
                        H3Offset = off + ((long)Be.U32(ph, 0x2B4) << 2),
                        DataOffset = off + ((long)Be.U32(ph, 0x2B8) << 2),
                        DataSize = (long)Be.U32(ph, 0x2BC) << 2,
                    });
                }
            }
        }

        // unencrypted disc bytes (header, partition tables, tickets, TMDs, H3 tables)
        public byte[] ReadRaw(long offset, int size)
        {
            var b = new byte[size];
            _disc.Read(offset, b);
            return b;
        }

        public byte[] ReadH3(WiiPartition p) => ReadRaw(p.H3Offset, 0x18000);

        // the TMD's content hash against the H3 table: a key-free check that the partition header is intact
        public bool CheckH3(WiiPartition p)
        {
            byte[]? want = p.ContentHash;
            return want != null && SHA1.HashData(ReadH3(p)).AsSpan().SequenceEqual(want);
        }

        // the partition's data, decrypted and without hashes (0x7C00 bytes per sector)
        public IDiscBlob OpenPartitionData(WiiPartition p)
        {
            if (_wia != null)
            {
                foreach (WiaPartition wp in _wia.Partitions)
                {
                    if (wp.DiscOffset == p.DataOffset) return wp;
                }
                throw new InvalidDataException($"{GameId}: the WIA/RVZ file has no data for the {p.TypeName} partition");
            }
            if (!HasHashes && IsEncrypted) throw new NotSupportedException($"{GameId}: encrypted partitions without hashes are not supported");
            byte[]? titleKey = null;
            if (IsEncrypted)
            {
                byte[] common = _keys?.Get(p.CommonKeyIndex) ?? throw new WiiKeyRequiredException(p.CommonKeyIndex == 1
                    ? "This disc image is encrypted with the Korean common key: give your own Korean key file, or an RVZ image"
                    : "This Wii disc image is encrypted: give your own common key file, or an RVZ image made by Dolphin");
                titleKey = WiiCrypto.DecryptTitleKey(p.Ticket, common);
            }
            var data = new WiiPartitionReader(_disc, p.DataOffset, p.SectorCount, titleKey, HasHashes);
            // a wrong key decrypts to noise: the partition's own header must carry the Wii magic
            var h = new byte[0x20];
            data.Read(0, h);
            if (Be.U32(h, 0x18) != Magic)
            {
                throw new WiiKeyRequiredException($"{GameId}: the key does not decrypt the {p.TypeName} partition (wrong key file?)");
            }
            return data;
        }

        // the file system of a partition (default: the DATA partition); disposing it leaves this disc open
        public FstFileSystem OpenFileSystem(WiiPartition? p = null, bool disposeDisc = false)
        {
            p ??= DataPartition ?? throw new InvalidDataException($"{GameId}: no DATA partition");
            return new FstFileSystem(OpenPartitionData(p), DiscPlatform.Wii, disposeDisc ? this : null);
        }

        public void Dispose() => _owner?.Dispose();
    }

    // a plain Wii image and no (or the wrong) common key: the UI asks for the user's key file, or suggests an RVZ
    public sealed class WiiKeyRequiredException : IOException
    {
        public WiiKeyRequiredException(string message) : base(message) { }
    }

    public static class WiiCrypto
    {
        // the ticket's title key (0x1BF), AES-128-CBC with the common key and IV = title id (0x1DC) + 8 zero bytes
        public static byte[] DecryptTitleKey(ReadOnlySpan<byte> ticket, ReadOnlySpan<byte> commonKey)
        {
            if (ticket.Length < 0x1E4) throw new InvalidDataException("ticket too short");
            Span<byte> iv = stackalloc byte[16];
            iv.Clear();
            ticket.Slice(0x1DC, 8).CopyTo(iv);
            using Aes aes = Aes.Create();
            aes.Key = commonKey.ToArray();
            return aes.DecryptCbc(ticket.Slice(0x1BF, 16), iv, PaddingMode.None);
        }

        // one 0x8000-byte disc sector to its 0x7C00 bytes of data
        public static void DecryptSector(Aes titleKey, ReadOnlySpan<byte> sector, Span<byte> data)
        {
            titleKey.DecryptCbc(sector.Slice(WiiDisc.HashBlock, WiiDisc.SectorData), sector.Slice(0x3D0, 16), data, PaddingMode.None);
        }
    }

    // A partition's data from a plain disc image: sectors decrypted (or, for unencrypted discs, stripped of their hash
    // block) 16 at a time (512 KiB of disc), the last few such clusters cached for the scattered reads of archives
    // opened in place. Thread-safe.
    public sealed class WiiPartitionReader : IDiscBlob, IDisposable
    {
        const int ClusterSectors = 16, Slots = 8;
        readonly IDiscBlob _disc;
        readonly long _start, _sectors;
        readonly Aes? _aes;
        readonly bool _hashes;
        readonly int _dataPerSector;
        readonly object _lock = new();
        readonly byte[] _raw = new byte[ClusterSectors * WiiDisc.SectorSize];
        readonly byte[][] _plain = new byte[Slots][];
        readonly long[] _cluster = new long[Slots];
        readonly long[] _stamp = new long[Slots];
        long _clock;

        public WiiPartitionReader(IDiscBlob disc, long dataOffset, long sectors, byte[]? titleKey, bool hasHashes)
        {
            _disc = disc; _start = dataOffset; _sectors = sectors; _hashes = hasHashes;
            _dataPerSector = hasHashes ? WiiDisc.SectorData : WiiDisc.SectorSize;
            for (int i = 0; i < Slots; i++)
            {
                _plain[i] = new byte[ClusterSectors * _dataPerSector];
                _cluster[i] = -1;
            }
            if (titleKey != null)
            {
                _aes = Aes.Create();
                _aes.Key = titleKey;
            }
        }

        public long Size => _sectors * _dataPerSector;

        public void Read(long offset, Span<byte> dst)
        {
            long clusterBytes = (long)ClusterSectors * _dataPerSector;
            lock (_lock)
            {
                while (dst.Length > 0)
                {
                    if (offset >= Size)
                    {
                        dst.Clear();
                        return;
                    }
                    long cluster = offset / clusterBytes;
                    int within = (int)(offset % clusterBytes);
                    byte[] plain = Load(cluster);
                    int n = (int)Math.Min(dst.Length, Math.Min(clusterBytes - within, Size - offset));
                    plain.AsSpan(within, n).CopyTo(dst);
                    dst = dst.Slice(n);
                    offset += n;
                }
            }
        }

        byte[] Load(long cluster)
        {
            int slot = 0;
            for (int i = 0; i < Slots; i++)
            {
                if (_cluster[i] == cluster)
                {
                    _stamp[i] = ++_clock;
                    return _plain[i];
                }
                if (_stamp[i] < _stamp[slot]) slot = i;
            }
            _cluster[slot] = -1;
            long first = cluster * ClusterSectors;
            int count = (int)Math.Min(ClusterSectors, _sectors - first);
            _disc.Read(_start + first * WiiDisc.SectorSize, _raw.AsSpan(0, count * WiiDisc.SectorSize));
            byte[] plain = _plain[slot];
            for (int i = 0; i < count; i++)
            {
                ReadOnlySpan<byte> sector = _raw.AsSpan(i * WiiDisc.SectorSize, WiiDisc.SectorSize);
                Span<byte> data = plain.AsSpan(i * _dataPerSector, _dataPerSector);
                if (!_hashes) sector.CopyTo(data);
                else if (_aes == null) sector.Slice(WiiDisc.HashBlock).CopyTo(data);
                else WiiCrypto.DecryptSector(_aes, sector, data);
            }
            _cluster[slot] = cluster;
            _stamp[slot] = ++_clock;
            return plain;
        }

        public void Dispose() => _aes?.Dispose();
    }

    // The user's own Wii common key(s), loaded from the file they give the app. The app ships no key. Accepted files:
    // the 16 key bytes; a BootMii keys.bin (0x400 bytes, common key at 0x114); or text holding one 32-hex-digit key.
    public sealed class WiiCommonKeys
    {
        public const int Standard = 0, Korean = 1, VWii = 2;
        readonly byte[]?[] _keys = new byte[]?[3];

        public void Set(int index, ReadOnlySpan<byte> key)
        {
            if ((uint)index >= _keys.Length) throw new ArgumentOutOfRangeException(nameof(index));
            if (key.Length != 16) throw new ArgumentException("a Wii common key is 16 bytes", nameof(key));
            _keys[index] = key.ToArray();
        }

        public byte[]? Get(int index) => (uint)index < _keys.Length ? _keys[index] : null;

        public static WiiCommonKeys FromFile(string path, int index = Standard) => FromBytes(File.ReadAllBytes(path), index);

        public static WiiCommonKeys FromBytes(byte[] file, int index = Standard)
        {
            var k = new WiiCommonKeys();
            k.Set(index, ParseKeyFile(file));
            return k;
        }

        public static byte[] ParseKeyFile(byte[] file)
        {
            if (file.Length == 16) return (byte[])file.Clone();
            if (file.Length == 0x400) return file.AsSpan(0x114, 16).ToArray();   // BootMii keys.bin: OTP at 0x100, common key at OTP + 0x14
            if (file.Length <= 0x10000)
            {
                string text = Encoding.ASCII.GetString(file);
                string? found = null;
                int run = 0;
                for (int i = 0; i <= text.Length; i++)
                {
                    bool hex = i < text.Length && Uri.IsHexDigit(text[i]);
                    if (hex) { run++; continue; }
                    if (run == 32)
                    {
                        // "0x" prefixes need nothing: the 'x' ends the "0" run before the key digits
                        string token = text.Substring(i - 32, 32);
                        if (found != null && !found.Equals(token, StringComparison.OrdinalIgnoreCase))
                        {
                            throw new InvalidDataException("the key file holds more than one 32-digit key");
                        }
                        found = token;
                    }
                    run = 0;
                }
                if (found != null) return Convert.FromHexString(found);
            }
            throw new InvalidDataException("not a key file: expected 16 bytes, a BootMii keys.bin, or a 32-digit hex key");
        }
    }

    // A WBFS container (libwbfs layout: "WBFS", hd sector count, log2 hd sector size, log2 WBFS sector size; a disc
    // table of one byte per slot; per disc a 0x100-byte header copy and a big-endian u16 table mapping each WBFS sector of
    // the disc to its place in the file, 0 = not stored). Presents the plain disc image; missing sectors read as
    // zeros. Only single-file WBFS (not .wbf1.. splits). Thread-safe.
    public sealed class WbfsReader : IDiscBlob, IDisposable
    {
        public const uint Magic = 0x57424653;   // "WBFS"
        const long WiiSectorsPerDisc = 143432 * 2;   // dual-layer

        readonly Stream _s;
        readonly bool _leaveOpen;
        readonly object _lock = new();
        readonly ushort[] _wlba;
        readonly int _secShift;

        public long Size { get; }
        public int SectorSize => 1 << _secShift;
        public byte[] DiscHeaderCopy { get; }

        public static bool Detect(Stream s)
        {
            if (!s.CanSeek || s.Length < 0x200) return false;
            long pos = s.Position;
            try
            {
                var b = new byte[4];
                s.Seek(0, SeekOrigin.Begin);
                s.ReadExactly(b);
                return Be.U32(b, 0) == Magic;
            }
            finally { s.Position = pos; }
        }

        public WbfsReader(Stream s, bool leaveOpen = true, int slot = 0)
        {
            if (!s.CanSeek) throw new ArgumentException("a disc image needs a seekable stream", nameof(s));
            _s = s; _leaveOpen = leaveOpen;
            byte[] head = ReadAt(0, 12);
            if (Be.U32(head, 0) != Magic) throw new InvalidDataException("not a WBFS file");
            int hdShift = head[8], secShift = head[9];
            if (hdShift < 9 || hdShift > 20 || secShift < 15 || secShift > 30) throw new InvalidDataException("WBFS: bad sector sizes");
            long hdSec = 1L << hdShift;
            _secShift = secShift;
            byte[] table = ReadAt(12, (int)Math.Min(hdSec - 12, 0x1000));
            if (slot < 0 || slot >= table.Length || table[slot] == 0) throw new InvalidDataException($"WBFS: no disc in slot {slot}");
            long perDisc = WiiSectorsPerDisc >> (secShift - 15);
            long infoSize = (0x100 + perDisc * 2 + hdSec - 1) / hdSec * hdSec;
            long infoOff = hdSec + slot * infoSize;
            byte[] info = ReadAt(infoOff, (int)(0x100 + perDisc * 2));
            DiscHeaderCopy = info.AsSpan(0, 0x100).ToArray();
            _wlba = new ushort[perDisc];
            long last = -1;
            for (int i = 0; i < perDisc; i++)
            {
                _wlba[i] = Be.U16(info, 0x100 + i * 2);
                if (_wlba[i] != 0) last = i;
            }
            if (last < 0) throw new InvalidDataException("WBFS: empty disc");
            Size = perDisc << secShift;
        }

        byte[] ReadAt(long offset, int size)
        {
            var b = new byte[size];
            lock (_lock)
            {
                _s.Seek(offset, SeekOrigin.Begin);
                _s.ReadExactly(b);
            }
            return b;
        }

        public void Read(long offset, Span<byte> dst)
        {
            while (dst.Length > 0)
            {
                long block = offset >> _secShift;
                int within = (int)(offset & ((1L << _secShift) - 1));
                int n = (int)Math.Min(dst.Length, (1L << _secShift) - within);
                ushort w = block < _wlba.Length ? _wlba[block] : (ushort)0;
                if (w == 0)
                {
                    dst.Slice(0, n).Clear();
                }
                else
                {
                    lock (_lock)
                    {
                        _s.Seek(((long)w << _secShift) + within, SeekOrigin.Begin);
                        _s.ReadExactly(dst.Slice(0, n));
                    }
                }
                dst = dst.Slice(n);
                offset += n;
            }
        }

        public void Dispose()
        {
            if (!_leaveOpen) _s.Dispose();
        }
    }
}
