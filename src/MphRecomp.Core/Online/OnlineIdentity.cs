using System;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace MphRecomp.Online
{
    // The player's online identity (docs/ONLINE_PLAY.md section 4): a random 16-byte seed, made on the first online
    // connect and kept beside the Hunter License. The seed gives an ECDSA P-256 key pair; the public key gives the friend
    // code. No password or email: logging in is signing the server's challenge. The seed doubles as the backup/transfer
    // code, so another device (or Windows <-> Android) can be the same player.
    public sealed class OnlineIdentity
    {
        public const string FileName = "online_identity.json";
        public const int SeedBytes = 16;

        private readonly byte[] _seed;
        private readonly ECParameters _key;

        // 65 bytes: 0x04, X, Y (the uncompressed point)
        public byte[] PublicKey { get; }
        public string FriendCode { get; }
        public string BackupCode => BackupCodes.Encode(_seed);

        private OnlineIdentity(byte[] seed)
        {
            _seed = seed;
            var d = P256.ScalarFromSeed(seed);
            (byte[] x, byte[] y) = P256.MultiplyBase(d);
            _key = new ECParameters
            {
                Curve = ECCurve.NamedCurves.nistP256,
                D = P256.Bytes(d),
                Q = new ECPoint { X = x, Y = y },
            };
            PublicKey = new byte[65];
            PublicKey[0] = 4;
            x.CopyTo(PublicKey, 1);
            y.CopyTo(PublicKey, 33);
            FriendCode = FriendCodes.FromKey(PublicKey);
        }

        public static OnlineIdentity Create() => new OnlineIdentity(RandomNumberGenerator.GetBytes(SeedBytes));

        public static OnlineIdentity? FromBackupCode(string? code)
            => BackupCodes.TryDecode(code, out byte[] seed) ? new OnlineIdentity(seed) : null;

        public byte[] Sign(byte[] data)
        {
            using var key = ECDsa.Create(_key);
            return key.SignData(data, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        }

        public static bool Verify(byte[] publicKey, byte[] data, byte[] signature)
        {
            if (publicKey.Length != 65 || publicKey[0] != 4 || signature.Length != 64)
            {
                return false;
            }
            try
            {
                using var key = ECDsa.Create(new ECParameters
                {
                    Curve = ECCurve.NamedCurves.nistP256,
                    Q = new ECPoint { X = publicKey[1..33], Y = publicKey[33..] },
                });
                return key.VerifyData(data, signature, HashAlgorithmName.SHA256,
                    DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
            }
            catch (CryptographicException)
            {
                return false; // not a point on the curve
            }
        }

        private sealed class Stored
        {
            public string Seed { get; set; } = "";
        }

        public static OnlineIdentity? Load(string? dir)
        {
            string? path = dir == null ? null : Path.Combine(dir, FileName);
            try
            {
                if (path != null && File.Exists(path))
                {
                    var stored = JsonSerializer.Deserialize<Stored>(File.ReadAllText(path));
                    byte[] seed = Convert.FromBase64String(stored?.Seed ?? "");
                    if (seed.Length == SeedBytes)
                    {
                        return new OnlineIdentity(seed);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or JsonException or FormatException or UnauthorizedAccessException)
            {
                Console.Error.WriteLine($"online identity: {path}: {ex.Message}");
            }
            return null;
        }

        // the saved identity, or a new one saved now (the first online connect, as vanilla made the friend code)
        public static OnlineIdentity LoadOrCreate(string? dir)
        {
            OnlineIdentity? id = Load(dir);
            if (id == null)
            {
                id = Create();
                id.Save(dir);
            }
            return id;
        }

        public void Save(string? dir)
        {
            if (dir == null)
            {
                return;
            }
            try
            {
                Directory.CreateDirectory(dir);
                string path = Path.Combine(dir, FileName);
                string tmp = path + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(new Stored { Seed = Convert.ToBase64String(_seed) }));
                File.Move(tmp, path, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Console.Error.WriteLine($"online identity: not saved: {ex.Message}");
            }
        }
    }
}
