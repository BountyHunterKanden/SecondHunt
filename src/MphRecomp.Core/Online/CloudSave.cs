using System;
using System.Security.Cryptography;
using System.Text;

namespace MphRecomp.Online
{
    // CLOUD SAVE (docs/PROFILE_SYNC.md Phase 2, server 0.11.0; SecondHunt.Server HubCloud.cs). One sealed profile pack per
    // player on the hub: the license, the synced settings and the saves (Profile/ProfilePack.cs's zip), sealed with AES-GCM
    // under a key derived from the identity seed (HKDF, CloudSaveRules.KeyLabel), so the hub keeps bytes it can't read.
    // cloud_get answers the copy and its rev (0 = none); cloud_put replaces it only when BaseRev is the rev there now
    // (compare-and-swap: otherwise Done = false with the rev there, and the app pulls, merges and puts again); cloud_delete
    // drops it (turning CLOUD SAVE off). The hub keeps a copy only for a player with a started online match, takes a put at
    // most every PutGapSeconds and PutsPerDay a day, and deletes a copy with the profile (delete_profile) or after KeepDays
    // without a login. Errors: no_matches, too_soon, too_many, too_big, bad_blob.
    // An older server drops a connection that sends any of these: check ServerAtLeast(CloudGetReq.SinceServer).
    public static class CloudSaveRules
    {
        public const int MaxBlob = 40_000; // sealed bytes (a pack is ~10 KB; base64 keeps a put under the 64 KB message)
        public const int PutGapSeconds = 30;
        public const int PutsPerDay = 100;
        public const int KeepDays = 365;
        public const int MinMatches = 1;
        public const string KeyLabel = "SecondHunt profile v1";
        public const byte BlobVersion = 1;
        public const int NonceBytes = 12, TagBytes = 16;
        public const int MinBlob = 1 + NonceBytes + TagBytes;

        public static bool Supported(OnlineClient c) => c.ServerAtLeast(CloudGetReq.SinceServer);

        // the server's only check of a blob: the version byte and room for a nonce and a tag
        public static bool LooksSealed(byte[] blob) => blob.Length >= MinBlob && blob.Length <= MaxBlob && blob[0] == BlobVersion;
    }

    // seal/open: version 1 | 12-byte nonce | ciphertext | 16-byte tag; the friend code is the associated data, so a copy
    // only opens for the identity it was made for
    public static class CloudSeal
    {
        public static bool Available => AesGcm.IsSupported;

        public static byte[] Seal(OnlineIdentity id, byte[] plain)
        {
            byte[] key = id.DeriveKey(CloudSaveRules.KeyLabel);
            byte[] blob = new byte[CloudSaveRules.MinBlob + plain.Length];
            blob[0] = CloudSaveRules.BlobVersion;
            Span<byte> nonce = blob.AsSpan(1, CloudSaveRules.NonceBytes);
            RandomNumberGenerator.Fill(nonce);
            using var gcm = new AesGcm(key, CloudSaveRules.TagBytes);
            gcm.Encrypt(nonce, plain, blob.AsSpan(1 + CloudSaveRules.NonceBytes, plain.Length),
                blob.AsSpan(blob.Length - CloudSaveRules.TagBytes), Encoding.UTF8.GetBytes(id.FriendCode));
            CryptographicOperations.ZeroMemory(key);
            return blob;
        }

        // null when the blob isn't one of this identity's (another identity's, damaged, a newer version)
        public static byte[]? Open(OnlineIdentity id, byte[] blob)
        {
            if (blob.Length < CloudSaveRules.MinBlob || blob[0] != CloudSaveRules.BlobVersion) return null;
            byte[] key = id.DeriveKey(CloudSaveRules.KeyLabel);
            byte[] plain = new byte[blob.Length - CloudSaveRules.MinBlob];
            try
            {
                using var gcm = new AesGcm(key, CloudSaveRules.TagBytes);
                gcm.Decrypt(blob.AsSpan(1, CloudSaveRules.NonceBytes), blob.AsSpan(1 + CloudSaveRules.NonceBytes, plain.Length),
                    blob.AsSpan(blob.Length - CloudSaveRules.TagBytes), plain, Encoding.UTF8.GetBytes(id.FriendCode));
                return plain;
            }
            catch (CryptographicException)
            {
                return null;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(key);
            }
        }
    }

    // this player's cloud copy; the reply is a CloudSaveMsg
    public sealed class CloudGetReq : OnlineMsg
    {
        public const string SinceServer = "0.11.0";
    }

    // Rev 0 = no copy (Blob ""); At = unix seconds of the put
    public sealed class CloudSaveMsg : OnlineMsg
    {
        public long Rev { get; set; }
        public long At { get; set; }
        public string Blob { get; set; } = ""; // base64
    }

    // replace the copy whose rev is BaseRev (0 = there's none yet); the reply is a CloudPutMsg
    public sealed class CloudPutReq : OnlineMsg
    {
        public long BaseRev { get; set; }
        public string Blob { get; set; } = ""; // base64
    }

    // Done: stored as Rev; not Done: the copy there is Rev now (pull it, merge, put again)
    public sealed class CloudPutMsg : OnlineMsg
    {
        public bool Done { get; set; }
        public long Rev { get; set; }
    }

    // drop this player's copy; the reply is an OkMsg (Done: there was one)
    public sealed class CloudDeleteReq : OnlineMsg
    {
    }
}
