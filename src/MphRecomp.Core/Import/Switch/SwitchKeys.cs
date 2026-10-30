using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;

namespace MphRecomp.Import.Switch
{
    // The user's own Switch keys, loaded from the files they give the app (the app ships no key, like WiiCommonKeys):
    //   prod.keys   "name = hex" lines (header_key, key_area_key_application_XX, titlekek_XX, master_key_XX, ...)
    //   title.keys  "rights id = title key" lines: the title keys of download titles whose own ticket is missing,
    //               stored as tickets carry them (encrypted with titlekek_XX; checked against real tickets)
    // Names are matched without case; '#' and ';' start comments. Values never leave this class except to the readers
    // that need them, and nothing here prints or logs one.
    public sealed class SwitchKeys
    {
        readonly Dictionary<string, byte[]> _keys = new(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, byte[]> _titleKeys = new(StringComparer.OrdinalIgnoreCase);   // rights id hex -> encrypted title key

        public int Count => _keys.Count;
        public int TitleKeyCount => _titleKeys.Count;

        // prod.keys, and title.keys from beside it (or `titleKeysPath`) when there is one
        public static SwitchKeys FromFile(string prodKeysPath, string? titleKeysPath = null)
        {
            var k = new SwitchKeys();
            k.Load(File.ReadAllText(prodKeysPath), title: false);
            titleKeysPath ??= Path.Combine(Path.GetDirectoryName(Path.GetFullPath(prodKeysPath)) ?? ".", "title.keys");
            if (File.Exists(titleKeysPath)) k.Load(File.ReadAllText(titleKeysPath), title: true);
            if (k.Get("header_key") == null) throw new InvalidDataException("not a Switch prod.keys file (no header_key line)");
            return k;
        }

        public static SwitchKeys FromText(string prodKeys, string? titleKeys = null)
        {
            var k = new SwitchKeys();
            k.Load(prodKeys, title: false);
            if (titleKeys != null) k.Load(titleKeys, title: true);
            return k;
        }

        void Load(string text, bool title)
        {
            foreach (string raw in text.Split('\n'))
            {
                string line = raw.Trim();
                int c = line.IndexOfAny(new[] { '#', ';' });
                if (c >= 0) line = line[..c].Trim();
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                string name = line[..eq].Trim(), value = line[(eq + 1)..].Trim();
                if (value.Length == 0 || value.Length % 2 != 0) continue;
                byte[] bytes;
                try { bytes = Convert.FromHexString(value); }
                catch (FormatException) { continue; }
                (title ? _titleKeys : _keys)[name] = bytes;
            }
        }

        public byte[]? Get(string name) => _keys.TryGetValue(name, out byte[]? v) ? v : null;

        // the 32-byte NCA header key (two AES-128 keys: data, tweak)
        public byte[] HeaderKey => Get("header_key") is { Length: 32 } k ? k
            : throw new SwitchKeyRequiredException("prod.keys has no header_key: give your own complete prod.keys");

        // master key revision of an NCA's key generation: generations 0 and 1 are both revision 0
        public static int Revision(int keyGeneration) => keyGeneration == 0 ? 0 : keyGeneration - 1;

        // the system version that brought in an NCA key generation (switchbrew "NCA", header KeyGeneration; 1 unused), so
        // "keys too old" can say which firmware to dump them from (owner 2026-10-17)
        static readonly string[] GenerationFirmware =
        {
            "1.0.0", "1.0.0", "3.0.0", "3.0.1", "4.0.0", "5.0.0", "6.0.0", "6.2.0", "7.0.0", "8.1.0", "9.0.0", "9.1.0",
            "12.1.0", "13.0.0", "14.0.0", "15.0.0", "16.0.0", "17.0.0", "18.0.0", "19.0.0", "20.0.0", "21.0.0", "22.0.0", "23.0.0",
        };

        // the firmware a master key revision first came with; null past the table (a newer one than this app knows)
        public static string? FirmwareFor(int revision)
        {
            int gen = revision == 0 ? 0 : revision + 1;
            return gen < GenerationFirmware.Length ? GenerationFirmware[gen] : null;
        }

        static SwitchKeyRequiredException TooOld(string key, int revision) =>
            new($"prod.keys has no {key} (keys older than switch firmware {FirmwareFor(revision) ?? "this game needs"}? dump your keys again from an up-to-date console)")
            { Firmware = FirmwareFor(revision) ?? "" };

        // the key-area key of an NCA: index 0 application, 1 ocean, 2 system; revision from Revision(). Derived from
        // master_key_XX when prod.keys lists only the sources (switchbrew "Cryptosystem": kek = master_key decrypts
        // aes_kek_generation_source, that decrypts key_area_key_<kind>_source, that decrypts aes_key_generation_source)
        public byte[] KeyAreaKey(int index, int revision)
        {
            string kind = index switch { 0 => "application", 1 => "ocean", 2 => "system", _ => throw new InvalidDataException($"NCA: bad key area index {index}") };
            byte[]? k = Get($"key_area_key_{kind}_{revision:x2}") ?? DeriveKeyAreaKey(kind, revision);
            return k ?? throw TooOld($"key_area_key_{kind}_{revision:x2}", revision);
        }

        // the title key encryption key of a revision (derived as master_key_XX decrypting titlekek_source when absent)
        public byte[] TitleKek(int revision)
        {
            byte[]? k = Get($"titlekek_{revision:x2}") ?? DeriveTitleKek(revision);
            return k ?? throw TooOld($"titlekek_{revision:x2}", revision);
        }

        // the encrypted title key title.keys lists for a rights id, or null
        public byte[]? TitleKeyFor(ReadOnlySpan<byte> rightsId) =>
            _titleKeys.TryGetValue(Convert.ToHexString(rightsId), out byte[]? v) && v.Length == 16 ? v : null;

        public byte[]? DeriveTitleKek(int revision)
        {
            byte[]? master = Get($"master_key_{revision:x2}"), src = Get("titlekek_source");
            return master is { Length: 16 } && src is { Length: 16 } ? SwitchCrypto.EcbDecrypt(master, src) : null;
        }

        public byte[]? DeriveKeyAreaKey(string kind, int revision)
        {
            byte[]? master = Get($"master_key_{revision:x2}"), kekSrc = Get("aes_kek_generation_source"),
                keySrc = Get("aes_key_generation_source"), areaSrc = Get($"key_area_key_{kind}_source");
            if (master is not { Length: 16 } || kekSrc is not { Length: 16 } || keySrc is not { Length: 16 } || areaSrc is not { Length: 16 }) return null;
            byte[] kek = SwitchCrypto.EcbDecrypt(master, kekSrc);
            byte[] srcKek = SwitchCrypto.EcbDecrypt(kek, areaSrc);
            return SwitchCrypto.EcbDecrypt(srcKek, keySrc);
        }
    }

    // no keys, or keys that don't decrypt this file: the UI asks for the player's own prod.keys
    public sealed class SwitchKeyRequiredException : IOException
    {
        public SwitchKeyRequiredException(string message) : base(message) { }

        // set when the keys are there but too old for this game: the firmware its key generation came with ("" = newer
        // than this app's table); null for no keys / wrong keys
        public string? Firmware { get; init; }
    }

    public static class SwitchCrypto
    {
        public static byte[] EcbDecrypt(byte[] key, ReadOnlySpan<byte> data)
        {
            using Aes aes = Aes.Create();
            aes.Key = key;
            return aes.DecryptEcb(data, PaddingMode.None);
        }

        // AES-128-XTS decryption of whole 0x200-byte sectors, the first being `sector`, with Nintendo's tweak: the
        // sector number as a BIG-endian 128-bit value (standard XTS uses little-endian); the tweak's per-block
        // multiplication by x in GF(2^128) is the standard one. key: data key (16 bytes) then tweak key (16 bytes).
        public static void XtsDecrypt(ReadOnlySpan<byte> key, Span<byte> data, long sector, int sectorSize = 0x200)
        {
            using Aes k1 = Aes.Create(), k2 = Aes.Create();
            k1.Key = key[..16].ToArray();
            k2.Key = key.Slice(16, 16).ToArray();
            Span<byte> tweak = stackalloc byte[16];
            var mask = new byte[sectorSize];
            for (int at = 0; at + sectorSize <= data.Length; at += sectorSize, sector++)
            {
                tweak.Clear();
                System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(tweak[8..], (ulong)sector);
                k2.EncryptEcb(tweak, tweak, PaddingMode.None);
                for (int b = 0; b < sectorSize; b += 16)
                {
                    tweak.CopyTo(mask.AsSpan(b, 16));
                    // multiply by x: shift the 128-bit little-endian value left, folding the carry in as 0x87
                    int carry = tweak[15] >> 7;
                    for (int i = 15; i > 0; i--) tweak[i] = (byte)(tweak[i] << 1 | tweak[i - 1] >> 7);
                    tweak[0] = (byte)(tweak[0] << 1 ^ (carry * 0x87));
                }
                Span<byte> s = data.Slice(at, sectorSize);
                Xor(s, mask);
                k1.DecryptEcb(s, s, PaddingMode.None);
                Xor(s, mask);
            }
        }

        public static void Xor(Span<byte> dst, ReadOnlySpan<byte> with)
        {
            int i = 0;
            if (System.Numerics.Vector.IsHardwareAccelerated)
            {
                int w = System.Numerics.Vector<byte>.Count;
                for (; i + w <= dst.Length; i += w)
                {
                    var v = new System.Numerics.Vector<byte>(dst[i..]) ^ new System.Numerics.Vector<byte>(with[i..]);
                    v.CopyTo(dst[i..]);
                }
            }
            for (; i < dst.Length; i++) dst[i] ^= with[i];
        }
    }

    // AES-128-CTR as NCA sections use it: the 16-byte counter is the section's 8-byte upper IV (big-endian) followed by
    // the byte offset / 16 (big-endian). Transform(offset, data) XORs the key stream for bytes at `offset` (any
    // alignment) into data. Not thread-safe (callers lock).
    public sealed class CtrCipher : IDisposable
    {
        const int Batch = 0x1000;   // counter blocks encrypted per AES call (64 KiB of key stream)
        readonly Aes _aes = Aes.Create();
        readonly byte[] _ctr = new byte[Batch * 16];
        readonly byte[] _stream = new byte[Batch * 16];

        public CtrCipher(byte[] key) { _aes.Key = key; }

        public void Transform(ulong upperIv, long offset, Span<byte> data)
        {
            long block = offset >> 4;
            int skip = (int)(offset & 15);
            while (data.Length > 0)
            {
                int blocks = (int)Math.Min(Batch, (skip + data.Length + 15) / 16);
                for (int i = 0; i < blocks; i++)
                {
                    Span<byte> c = _ctr.AsSpan(i * 16, 16);
                    System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(c, upperIv);
                    System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(c[8..], (ulong)(block + i));
                }
                _aes.EncryptEcb(_ctr.AsSpan(0, blocks * 16), _stream.AsSpan(0, blocks * 16), PaddingMode.None);
                int n = Math.Min(data.Length, blocks * 16 - skip);
                Xor(data[..n], _stream.AsSpan(skip, n));
                data = data[n..];
                block += blocks;
                skip = 0;
            }
        }

        static void Xor(Span<byte> dst, ReadOnlySpan<byte> with) => SwitchCrypto.Xor(dst, with);

        public void Dispose() => _aes.Dispose();
    }
}
