using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;

// The signed update file each release carries (SecondHunt-<version>-update.txt, docs/UPDATER.md): which version it is
// and the name, size and SHA-256 of every file the release ships, signed with the owner's update key (ECDSA P-256,
// SHA-256, the signature as r || s). Plain text, one item per line, "\n" only:
//
//   second hunt update
//   version 0.1.3-beta
//   file android SecondHunt-0.1.3-beta.apk 24144744 <sha256 hex>
//   file windows-x64 SecondHunt-0.1.3-beta-windows-x64.zip 63757448 <sha256 hex>
//   key <key id>
//   signature <base64>
//
// The signature covers every byte before the "signature " line, so nothing has to be re-serialised to check it. The
// game trusts only the public keys in UpdateKeys; SecondHunt.ReleaseTool writes and signs the file.
namespace MphRecomp.Update
{
    public sealed record UpdateFile(string Platform, string Name, long Size, string Sha256);

    public sealed class UpdateManifest
    {
        public const string Header = "second hunt update";
        public const int MaxBytes = 64 * 1024;

        public string Version { get; }
        public IReadOnlyList<UpdateFile> Files { get; }
        public string KeyId { get; }

        private UpdateManifest(string version, IReadOnlyList<UpdateFile> files, string keyId)
        {
            Version = version;
            Files = files;
            KeyId = keyId;
        }

        // the release asset that holds a version's update file
        public static string AssetName(string version) => $"SecondHunt-{version}-update.txt";

        // the release asset each platform installs from (docs/RELEASE.md section 3)
        public static string FileName(string platform, string version) => platform switch
        {
            UpdatePlatform.Android => $"SecondHunt-{version}.apk",
            UpdatePlatform.Windows => $"SecondHunt-{version}-windows-x64.zip",
            _ => throw new ArgumentException("unknown platform " + platform)
        };

        public UpdateFile? FileFor(string platform)
        {
            foreach (UpdateFile f in Files)
            {
                if (f.Platform == platform) return f;
            }
            return null;
        }

        // the text before the signature line, ready to sign
        public static string Body(string version, IEnumerable<UpdateFile> files, string keyId)
        {
            var sb = new StringBuilder();
            sb.Append(Header).Append('\n');
            sb.Append("version ").Append(version).Append('\n');
            foreach (UpdateFile f in files)
            {
                sb.Append("file ").Append(f.Platform).Append(' ').Append(f.Name).Append(' ').Append(f.Size).Append(' ')
                    .Append(f.Sha256.ToLowerInvariant()).Append('\n');
            }
            sb.Append("key ").Append(keyId).Append('\n');
            return sb.ToString();
        }

        public static string Sign(string body, ECDsa key)
        {
            byte[] signature = key.SignData(Encoding.UTF8.GetBytes(body), HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
            return body + "signature " + Convert.ToBase64String(signature) + "\n";
        }

        // the manifest if it parses AND its signature checks out against one of `trustedKeys` (SubjectPublicKeyInfo DER,
        // base64); otherwise null with the reason
        public static UpdateManifest? Verify(byte[] bytes, IEnumerable<string> trustedKeys, out string error)
        {
            error = "";
            if (bytes.Length > MaxBytes)
            {
                error = "update file too large";
                return null;
            }
            string text;
            try
            {
                text = new UTF8Encoding(false, true).GetString(bytes);
            }
            catch (DecoderFallbackException)
            {
                error = "update file is not UTF-8";
                return null;
            }
            if (text.Contains('\r'))
            {
                error = "update file has CR line ends";
                return null;
            }
            int sigLine = text.LastIndexOf("\nsignature ", StringComparison.Ordinal);
            if (sigLine < 0)
            {
                error = "update file has no signature";
                return null;
            }
            string body = text[..(sigLine + 1)];
            byte[] signature;
            try
            {
                signature = Convert.FromBase64String(text[(sigLine + "\nsignature ".Length)..].TrimEnd('\n'));
            }
            catch (FormatException)
            {
                error = "signature is not base64";
                return null;
            }

            string? version = null, keyId = null;
            var files = new List<UpdateFile>();
            string[] lines = body.TrimEnd('\n').Split('\n');
            if (lines.Length == 0 || lines[0] != Header)
            {
                error = "not an update file";
                return null;
            }
            for (int i = 1; i < lines.Length; i++)
            {
                string[] parts = lines[i].Split(' ');
                switch (parts[0])
                {
                case "version" when parts.Length == 2:
                    version = parts[1];
                    break;
                case "file" when parts.Length == 5 && long.TryParse(parts[3], out long size) && size > 0
                    && parts[4].Length == 64:
                    files.Add(new UpdateFile(parts[1], parts[2], size, parts[4]));
                    break;
                case "key" when parts.Length == 2:
                    keyId = parts[1];
                    break;
                default:
                    break; // a later format's line: signed, so harmless to skip
                }
            }
            if (version == null || keyId == null)
            {
                error = "update file is missing its version or key";
                return null;
            }

            byte[] signed = Encoding.UTF8.GetBytes(body);
            foreach (string trusted in trustedKeys)
            {
                if (trusted.Length == 0 || KeyIdOf(trusted) != keyId) continue;
                using ECDsa key = ECDsa.Create();
                key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(trusted), out _);
                if (key.VerifyData(signed, signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
                {
                    return new UpdateManifest(version, files, keyId);
                }
                error = "signature doesn't match";
                return null;
            }
            error = $"signed with key {keyId}, which this build doesn't trust";
            return null;
        }

        // a public key's short name: the first 16 hex digits of the SHA-256 of its SubjectPublicKeyInfo
        public static string KeyIdOf(string publicKeyBase64)
            => Convert.ToHexString(SHA256.HashData(Convert.FromBase64String(publicKeyBase64)))[..16].ToLowerInvariant();
    }

    public static class UpdatePlatform
    {
        public const string Android = "android";
        public const string Windows = "windows-x64";
    }
}
