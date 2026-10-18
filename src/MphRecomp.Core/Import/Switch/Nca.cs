using System;
using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using MphRecomp.Import.Disc;

namespace MphRecomp.Import.Switch
{
    public enum NcaContentType : byte { Program = 0, Meta = 1, Control = 2, Manual = 3, Data = 4, PublicData = 5 }

    public enum NcaEncryption : byte { Auto = 0, None = 1, AesXts = 2, AesCtr = 3, AesCtrEx = 4, AesCtrSkipLayerHash = 5, AesCtrExSkipLayerHash = 6 }

    public enum NcaHashType : byte { Auto = 0, None = 1, HierarchicalSha256 = 2, HierarchicalIntegrity = 3, AutoSha3 = 4, HierarchicalSha3256 = 5, HierarchicalIntegritySha3 = 6 }

    // One section of an NCA: its place in the NCA (FsEntry, 0x200-byte units) and its decrypted FsHeader (0x200 bytes):
    //   0x00 u16 version, 0x02 fs type (0 RomFS, 1 PartitionFS), 0x03 hash type, 0x04 encryption type,
    //   0x08 hash data (0xF8: IVFC for a RomFS, HierarchicalSha256 for a PartitionFS), 0x100 PatchInfo (0x40, updates),
    //   0x140 u32 generation + 0x144 u32 secure value (the AES-CTR upper IV), 0x148 SparseInfo, 0x178 CompressionInfo
    public sealed class NcaSection
    {
        public int Index { get; init; }
        public long Offset { get; init; }     // in the NCA
        public long Size { get; init; }
        public byte[] FsHeader { get; init; } = Array.Empty<byte>();

        public bool IsRomFs => FsHeader[2] == 0;
        public NcaHashType HashType => (NcaHashType)FsHeader[3];
        public NcaEncryption Encryption => (NcaEncryption)FsHeader[4];
        // the counter's upper 8 bytes: generation in the low word, secure value in the high (as the little-endian u64 at 0x140)
        public ulong UpperIv => BinaryPrimitives.ReadUInt64LittleEndian(FsHeader.AsSpan(0x140));
        public uint SecureValue => BinaryPrimitives.ReadUInt32LittleEndian(FsHeader.AsSpan(0x144));
        public ReadOnlySpan<byte> HashData => FsHeader.AsSpan(0x08, 0xF8);
        public ReadOnlySpan<byte> PatchInfo => FsHeader.AsSpan(0x100, 0x40);
        public bool IsPatch => BinaryPrimitives.ReadUInt32LittleEndian(FsHeader.AsSpan(0x110)) == BucketTree.Magic
            && Encryption is NcaEncryption.AesCtrEx or NcaEncryption.AesCtrExSkipLayerHash;
        public long SparseTableSize => (long)BinaryPrimitives.ReadUInt64LittleEndian(FsHeader.AsSpan(0x150));
        public long CompressionTableSize => (long)BinaryPrimitives.ReadUInt64LittleEndian(FsHeader.AsSpan(0x180));

        public override string ToString() => $"section {Index}: {(IsRomFs ? "RomFS" : "PartitionFS")}, {Encryption}, {HashType}, "
            + $"{Size / 1048576.0:0.0} MiB at 0x{Offset:X}{(IsPatch ? ", patch (BKTR)" : "")}";
    }

    // An NCA (switchbrew "NCA"): a 0xC00-byte header AES-128-XTS encrypted with header_key (0x200-byte sectors from 0,
    // Nintendo's big-endian tweak; an NCA2 encrypts each FsHeader as sector 0), then up to four sections, each AES-CTR
    // encrypted (or not) with a key from the header's key area (decrypted with key_area_key_<kind>_<revision>) or, when
    // the header has a rights id, the title key from the title's ticket (decrypted with titlekek_<revision>).
    // Header layout used: 0x200 magic "NCA3"/"NCA2", 0x204 distribution (0 download, 1 game card), 0x205 content type,
    // 0x206 old key generation, 0x207 key area index, 0x208 u64 size, 0x210 u64 program id, 0x218 u32 content index,
    // 0x220 key generation, 0x230 rights id, 0x240 four FsEntries (u32 start, u32 end in 0x200 units), 0x280 four
    // SHA-256 of the FsHeaders, 0x300 the encrypted key area (four 16-byte keys; AES-CTR sections use key 2),
    // 0x400 four FsHeaders.
    public sealed class Nca
    {
        public const int HeaderSize = 0xC00;
        readonly IDiscBlob _blob;
        readonly SwitchKeys _keys;
        readonly Func<byte[], byte[]?>? _ticketKey;
        readonly byte[] _header;
        byte[]? _sectionKey;

        public string Name { get; }
        public int Version { get; }                    // 3 or 2
        public bool IsGameCard => _header[0x204] == 1;
        public NcaContentType ContentType => (NcaContentType)_header[0x205];
        public int KeyAreaIndex => _header[0x207];
        public long ContentSize => (long)BinaryPrimitives.ReadUInt64LittleEndian(_header.AsSpan(0x208));
        public ulong ProgramId => BinaryPrimitives.ReadUInt64LittleEndian(_header.AsSpan(0x210));
        public int ContentIndex => (int)BinaryPrimitives.ReadUInt32LittleEndian(_header.AsSpan(0x218));
        public int KeyGeneration => Math.Max(_header[0x206], _header[0x220]);
        public byte[] RightsId => _header.AsSpan(0x230, 16).ToArray();
        public bool HasRightsId => _header.AsSpan(0x230, 16).IndexOfAnyExcept((byte)0) >= 0;
        public NcaSection?[] Sections { get; } = new NcaSection?[4];
        public IDiscBlob Blob => _blob;

        // ticketKey: the encrypted title key the package's own ticket holds for a rights id (null: none)
        public Nca(IDiscBlob blob, SwitchKeys keys, string name = "", Func<byte[], byte[]?>? ticketKey = null)
        {
            _blob = blob; _keys = keys; _ticketKey = ticketKey;
            Name = name;
            if (blob.Size < HeaderSize) throw new InvalidDataException($"{name}: too small for an NCA");
            var raw = new byte[HeaderSize];
            blob.Read(0, raw);
            _header = (byte[])raw.Clone();
            byte[] hk = keys.HeaderKey;
            SwitchCrypto.XtsDecrypt(hk, _header, 0);
            string magic = Encoding.ASCII.GetString(_header, 0x200, 4);
            if (magic == "NCA2")
            {
                Version = 2;
                for (int i = 0; i < 4; i++)
                {
                    raw.AsSpan(0x400 + i * 0x200, 0x200).CopyTo(_header.AsSpan(0x400 + i * 0x200));
                    SwitchCrypto.XtsDecrypt(hk, _header.AsSpan(0x400 + i * 0x200, 0x200), 0);
                }
            }
            else if (magic == "NCA3") Version = 3;
            else throw new SwitchKeyRequiredException($"{name}: the header doesn't decrypt to an NCA (wrong header_key, or not an NCA3/NCA2 file)");

            for (int i = 0; i < 4; i++)
            {
                long start = BinaryPrimitives.ReadUInt32LittleEndian(_header.AsSpan(0x240 + i * 0x10)) * 0x200L;
                long end = BinaryPrimitives.ReadUInt32LittleEndian(_header.AsSpan(0x244 + i * 0x10)) * 0x200L;
                if (end <= start) continue;
                byte[] fs = _header.AsSpan(0x400 + i * 0x200, 0x200).ToArray();
                if (!SHA256.HashData(fs).AsSpan().SequenceEqual(_header.AsSpan(0x280 + i * 0x20, 0x20)))
                {
                    throw new InvalidDataException($"{name}: FsHeader {i} fails its header hash (damaged file?)");
                }
                Sections[i] = new NcaSection { Index = i, Offset = start, Size = end - start, FsHeader = fs };
            }
        }

        // the 16-byte AES-CTR key of the sections
        public byte[] SectionKey()
        {
            if (_sectionKey != null) return _sectionKey;
            int rev = SwitchKeys.Revision(KeyGeneration);
            if (HasRightsId)
            {
                byte[] rid = RightsId;
                if (_ticketKey?.Invoke(rid) is byte[] ticket)
                {
                    _sectionKey = SwitchCrypto.EcbDecrypt(_keys.TitleKek(rev), ticket);
                }
                else
                {
                    byte[] listed = _keys.TitleKeyFor(rid)
                        ?? throw new SwitchKeyRequiredException($"{Name}: no ticket for this title in the file and no title key for it in title.keys");
                    // title.keys is taken as tickets store the key (encrypted with titlekek); a file holding the
                    // decrypted key instead is accepted too: whichever form decrypts a section is the one
                    byte[] dec = SwitchCrypto.EcbDecrypt(_keys.TitleKek(rev), listed);
                    _sectionKey = KeyWorks(dec) != false ? dec : listed;
                }
            }
            else
            {
                byte[] area = SwitchCrypto.EcbDecrypt(_keys.KeyAreaKey(KeyAreaIndex, rev), _header.AsSpan(0x300, 0x40));
                _sectionKey = area.AsSpan(0x20, 16).ToArray();
            }
            return _sectionKey;
        }

        // whether a section key decrypts this NCA (a known plaintext: an ExeFS/PartitionFS section starts its data with
        // "PFS0", a RomFS's data level with its header size 0x50); null when no section can tell
        public bool? KeyWorks(byte[] key)
        {
            foreach (NcaSection? s in Sections)
            {
                if (s == null || s.Encryption is not (NcaEncryption.AesCtr or NcaEncryption.AesCtrSkipLayerHash)) continue;
                long at;
                if (s.HashType == NcaHashType.HierarchicalSha256) at = (long)BinaryPrimitives.ReadUInt64LittleEndian(s.HashData[0x38..]);   // region 1: the PFS0
                else if (s.HashType == NcaHashType.HierarchicalIntegrity && s.IsRomFs) at = (long)BinaryPrimitives.ReadUInt64LittleEndian(s.HashData[(0x10 + 0x18 * ((int)BinaryPrimitives.ReadUInt32LittleEndian(s.HashData[0x0C..]) - 2))..]);
                else continue;
                if (at < 0 || at + 8 > s.Size) continue;
                using var ctr = new CtrBlob(new SubBlob(_blob, s.Offset, s.Size), key, s.UpperIv, s.Offset);
                var b = new byte[8];
                ctr.Read(at, b);
                return s.IsRomFs ? BinaryPrimitives.ReadUInt64LittleEndian(b) == 0x50 : BinaryPrimitives.ReadUInt32LittleEndian(b) == PartitionFs.Pfs0Magic;
            }
            return null;
        }

        // the section key, checked against a known plaintext (KeyWorks); null when no section can tell
        public bool? CheckKey() => KeyWorks(SectionKey());

        // a section's decrypted bytes, offsets from the section's start (for a patch section: the raw patch data with
        // only its BKTR tables readable; Bktr opens the rest)
        public IDiscBlob OpenSection(NcaSection s)
        {
            if (s.SparseTableSize != 0) throw new NotSupportedException($"{Name}: sparse NCA sections are not supported yet");
            if (s.CompressionTableSize != 0) throw new NotSupportedException($"{Name}: compressed NCA sections are not supported yet");
            if (s.Offset + s.Size > _blob.Size) throw new InvalidDataException($"{Name}: section {s.Index} lies outside the file (truncated dump?)");
            var raw = new SubBlob(_blob, s.Offset, s.Size);
            return s.Encryption switch
            {
                NcaEncryption.None => raw,
                NcaEncryption.AesCtr or NcaEncryption.AesCtrEx or NcaEncryption.AesCtrSkipLayerHash or NcaEncryption.AesCtrExSkipLayerHash
                    => new CtrBlob(raw, SectionKey(), s.UpperIv, s.Offset),
                _ => throw new NotSupportedException($"{Name}: section {s.Index} uses {s.Encryption} encryption (not supported)"),
            };
        }

        public override string ToString() => $"{Name}: NCA{Version} {ContentType}, program {ProgramId:X16}, key generation {KeyGeneration}, "
            + $"{(HasRightsId ? "rights id (ticket)" : "key area")}{(IsGameCard ? ", game card" : "")}";
    }

    // An AES-CTR encrypted region read decrypted. The counter of the region's byte `o` is (upperIv, (counterBase + o) / 16):
    // counterBase is the region's offset in its NCA (the counter counts NCA bytes). Thread-safe.
    public sealed class CtrBlob : IDiscBlob, IDisposable
    {
        readonly IDiscBlob _raw;
        readonly CtrCipher _ctr;
        readonly ulong _iv;
        readonly long _base;
        readonly object _lock = new();

        public CtrBlob(IDiscBlob raw, byte[] key, ulong upperIv, long counterBase)
        {
            _raw = raw; _iv = upperIv; _base = counterBase;
            _ctr = new CtrCipher(key);
        }

        public long Size => _raw.Size;

        public void Read(long offset, Span<byte> dst) => Read(offset, dst, _iv);

        // with another upper IV (an AES-CTR-EX subsection's generation)
        public void Read(long offset, Span<byte> dst, ulong upperIv)
        {
            _raw.Read(offset, dst);
            lock (_lock) _ctr.Transform(upperIv, _base + offset, dst);
        }

        public void Dispose() => _ctr.Dispose();
    }
}
