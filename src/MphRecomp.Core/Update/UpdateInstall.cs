using System;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

// Installing an offered update in the game (owner 2026-10-06: "i want it to actually handle the update itself"): Core
// downloads this platform's file and keeps it only if its size and SHA-256 are the ones the signed update file names
// (UpdateCheck already checked that signature); the host installs it. Android: PackageInstaller, after Android's own
// "update this app?" screen. Windows: the game's files swapped in place, then a restart.
namespace MphRecomp.Update
{
    public interface IUpdateInstaller
    {
        // where downloads go (a private folder; one file per version)
        string DownloadDir { get; }

        // why this copy can't update itself right now, or null if it can. Checked before the download. `fix` (may be
        // null) runs when the player closes the message, e.g. Android's "install unknown apps" switch for this app.
        string? Blocker(UpdateResult offer, out Action? fix);

        // installs the verified file at `path`. Calls `done` once from any thread with a message for the game's box,
        // or never, when the install ends this process (Android replaces the app; Windows restarts into the new one).
        void Install(string path, UpdateResult offer, Action<string> done);
    }

    public static class UpdateDownload
    {
        // this platform's file of `offer` into `dir`; null + `error` (box text) if it didn't arrive whole and signed.
        // A file already there with the signed size and SHA-256 is reused. `progress` gets 0..1. Never throws.
        public static async Task<(string? Path, string Error, string Detail)> RunAsync(UpdateResult offer, string dir,
            Action<float> progress, CancellationToken ct = default)
        {
            UpdateFile? file = offer.File;
            if (file == null || offer.FileUrl == null) return (null, Failed, "no file in the offer");
            string path = Path.Combine(dir, file.Name);
            string part = path + ".part";
            try
            {
                Directory.CreateDirectory(dir);
                foreach (string old in Directory.GetFiles(dir))
                {
                    if (old != path) TryDelete(old); // earlier versions' downloads
                }
                if (File.Exists(path) && Matches(path, file)) return (path, "", "already downloaded");
                TryDelete(path);

                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                long received = 0;
                if (offer.FileUrl.StartsWith("file://", StringComparison.Ordinal))
                {
                    // a dev build's test feed folder
                    await using FileStream source = File.OpenRead(new Uri(offer.FileUrl).LocalPath);
                    received = await Copy(source, part, hash, file.Size, progress, ct).ConfigureAwait(false);
                }
                else
                {
                    using var request = new HttpRequestMessage(HttpMethod.Get, offer.FileUrl);
                    request.Headers.Add("Accept", "application/octet-stream");
                    using HttpResponseMessage response = await UpdateCheck.Http(offer.Current)
                        .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                    if (!response.IsSuccessStatusCode) return (null, Failed, $"HTTP {(int)response.StatusCode}");
                    await using Stream source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                    received = await Copy(source, part, hash, file.Size, progress, ct).ConfigureAwait(false);
                }
                string sha = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
                if (received != file.Size || sha != file.Sha256.ToLowerInvariant())
                {
                    TryDelete(part);
                    return (null, NotSigned, $"{received} bytes, sha256 {sha}; signed {file.Size}, {file.Sha256}");
                }
                File.Move(part, path, overwrite: true);
                return (path, "", $"{received} bytes, sha256 ok");
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException or UnauthorizedAccessException)
            {
                TryDelete(part);
                return (null, Failed, ex.GetType().Name + ": " + ex.Message);
            }
        }

        public const string Failed = "the download didn't finish. check your connection and try again.";
        public const string NotSigned = "the download doesn't match its signed update file, so it wasn't installed.";

        // a stalled connection gives up after this long without a byte
        private static readonly TimeSpan Stall = TimeSpan.FromSeconds(30);

        private static async Task<long> Copy(Stream source, string part, IncrementalHash hash, long expected, Action<float> progress,
            CancellationToken ct)
        {
            await using FileStream output = File.Create(part);
            var buffer = new byte[81920];
            long received = 0;
            while (true)
            {
                using var stall = CancellationTokenSource.CreateLinkedTokenSource(ct);
                stall.CancelAfter(Stall);
                int n = await source.ReadAsync(buffer, stall.Token).ConfigureAwait(false);
                if (n == 0) break;
                received += n;
                if (received > expected) break; // more than was signed: refused below
                hash.AppendData(buffer, 0, n);
                await output.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
                progress(Math.Min(1f, received / (float)expected));
            }
            return received;
        }

        private static bool Matches(string path, UpdateFile file)
        {
            using FileStream s = File.OpenRead(path);
            return s.Length == file.Size
                && Convert.ToHexString(SHA256.HashData(s)).Equals(file.Sha256, StringComparison.OrdinalIgnoreCase);
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path)) File.Delete(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }
}
