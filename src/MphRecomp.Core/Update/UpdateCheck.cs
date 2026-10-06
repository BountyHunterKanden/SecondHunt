using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

// RECOMP SETTINGS > UPDATES (owner 2026-10-06: a button, no automatic checks; docs/UPDATER.md): asks GitHub for the
// newest published release, and offers it only when its signed update file (UpdateManifest) checks out against
// UpdateKeys and names this platform's file with the size and SHA-256 GitHub reports for it. Offering = the game's
// yes/no box, YES opens the release page in the browser; nothing is downloaded or installed here.
namespace MphRecomp.Update
{
    // what the host tells the check, set once at startup (MphRead.Android's MphApp, the Windows host's Program)
    public static class UpdateHost
    {
        // this build's version name (Android: versionName; Windows: the exe's informational version)
        public static string AppVersion { get; set; } = "0.0.0";
        public static string Platform { get; set; } = UpdatePlatform.Android;
        // opens a web page in the browser (the release page, when this host can't install updates itself)
        public static Action<string>? OpenUrl { get; set; }
        // downloads land and get installed here (UpdateInstall.cs); null = this host only offers the release page
        public static IUpdateInstaller? Installer { get; set; }
        // dev builds only: a text file whose first line replaces the GitHub API base (a local test server, or a file://
        // folder laid out like it: releases/latest + the files it names); a public build ignores it
        public static string? DevFeedFile { get; set; }

        public static string? DevFeed()
        {
            try
            {
                if (DevFeedFile == null || !File.Exists(DevFeedFile)) return null;
                string line = File.ReadAllLines(DevFeedFile)[0].Trim();
                return line.Length > 0 ? line.TrimEnd('/') : null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or IndexOutOfRangeException)
            {
                return null;
            }
        }
    }

    public enum UpdateStatus { UpToDate, Available, Unsigned, BadSignature, Mismatch, NotForThisPlatform, Offline, Busy, Failed }

    public sealed record UpdateResult(UpdateStatus Status, string Current, string? Latest = null, string? PageUrl = null,
        string Detail = "")
    {
        // Available: this platform's file as the update file signed it, and where the release keeps it
        public UpdateFile? File { get; init; }
        public string? FileUrl { get; init; }
    }

    public sealed record ReleaseAsset(string Name, long Size, string? Sha256, string Url);

    public sealed record ReleaseInfo(string Tag, string Version, bool Draft, bool Prerelease, IReadOnlyList<ReleaseAsset> Assets);

    public static class UpdateCheck
    {
        public const string Repo = "BountyHunterKanden/SecondHunt";
        public const string ApiBase = "https://api.github.com/repos/" + Repo;
        // where a release's files download from (browser_download_url); a public build fetches the update file only here
        public const string DownloadPrefix = "https://github.com/" + Repo + "/releases/download/";

        public static string PageUrl(string version) => $"https://github.com/{Repo}/releases/tag/v{version}";

        private static HttpClient? _http;
        internal static HttpClient Http(string current) => _http ??= NewClient(current);

        // `feed` replaces ApiBase (a dev build's test server or folder); null = GitHub. Never throws.
        public static async Task<UpdateResult> RunAsync(string current, string platform, bool publicBuild, string? feed,
            CancellationToken ct = default)
        {
            string api = publicBuild || feed == null ? ApiBase : feed;
            try
            {
                (int status, byte[] body) = await Get(api + "/releases/latest", GitHubJson, 1 << 20, current, ct).ConfigureAwait(false);
                if (status == 404)
                {
                    return new UpdateResult(UpdateStatus.UpToDate, current, Detail: "no published release");
                }
                if (status is 403 or 429)
                {
                    return new UpdateResult(UpdateStatus.Busy, current, Detail: $"HTTP {status}");
                }
                if (status != 200)
                {
                    return new UpdateResult(UpdateStatus.Failed, current, Detail: $"HTTP {status}");
                }
                ReleaseInfo? release = ParseRelease(System.Text.Encoding.UTF8.GetString(body));
                if (release == null)
                {
                    return new UpdateResult(UpdateStatus.Failed, current, Detail: "unreadable release");
                }
                if (release.Draft || release.Prerelease || Compare(release.Version, current) <= 0)
                {
                    return Evaluate(current, platform, UpdateKeys.Trusted(publicBuild), release, null);
                }

                ReleaseAsset? asset = Find(release.Assets, UpdateManifest.AssetName(release.Version));
                byte[]? manifest = null;
                if (asset != null)
                {
                    if (api == ApiBase && !asset.Url.StartsWith(DownloadPrefix, StringComparison.Ordinal))
                    {
                        return new UpdateResult(UpdateStatus.Failed, current, release.Version, Detail: "update file at " + asset.Url);
                    }
                    if (asset.Size > UpdateManifest.MaxBytes)
                    {
                        return new UpdateResult(UpdateStatus.BadSignature, current, release.Version, Detail: "update file too large");
                    }
                    (status, manifest) = await Get(asset.Url, "application/octet-stream", UpdateManifest.MaxBytes + 1, current, ct)
                        .ConfigureAwait(false);
                    if (status != 200)
                    {
                        return new UpdateResult(UpdateStatus.Failed, current, release.Version, Detail: $"update file HTTP {status}");
                    }
                }
                UpdateResult result = Evaluate(current, platform, UpdateKeys.Trusted(publicBuild), release, manifest);
                if (api == ApiBase && result.FileUrl != null && !result.FileUrl.StartsWith(DownloadPrefix, StringComparison.Ordinal))
                {
                    return new UpdateResult(UpdateStatus.Failed, current, release.Version, Detail: "release file at " + result.FileUrl);
                }
                return result;
            }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException)
            {
                return new UpdateResult(UpdateStatus.Offline, current, Detail: ex.GetType().Name + ": " + ex.Message);
            }
            catch (Exception ex)
            {
                return new UpdateResult(UpdateStatus.Failed, current, Detail: ex.GetType().Name + ": " + ex.Message);
            }
        }

        private const string GitHubJson = "application/vnd.github+json";

        // one GET: its HTTP status and up to `cap` bytes of its body. A dev feed may also be a folder on the device
        // (file://...), since Android refuses plain-HTTP test servers; a public build only ever asks GitHub (https).
        private static async Task<(int Status, byte[] Body)> Get(string url, string accept, int cap, string current,
            CancellationToken ct)
        {
            if (url.StartsWith("file://", StringComparison.Ordinal))
            {
                string path = new Uri(url).LocalPath;
                if (!File.Exists(path)) return (404, Array.Empty<byte>());
                byte[] all = await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
                return (200, all.Length > cap ? all[..cap] : all);
            }
            HttpClient http = Http(current);
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("Accept", accept);
            if (accept == GitHubJson) request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
            using HttpResponseMessage response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return ((int)response.StatusCode, Array.Empty<byte>());
            return (200, await ReadCapped(response.Content, cap, ct).ConfigureAwait(false));
        }

        private static HttpClient NewClient(string current)
        {
            var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("SecondHunt/" + (TryParseVersion(current, out _) ? current : "0"));
            return http;
        }

        private static async Task<byte[]> ReadCapped(HttpContent content, int cap, CancellationToken ct)
        {
            await using Stream stream = await content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var buffer = new MemoryStream();
            var chunk = new byte[8192];
            int n;
            while ((n = await stream.ReadAsync(chunk, ct).ConfigureAwait(false)) > 0)
            {
                buffer.Write(chunk, 0, n);
                if (buffer.Length >= cap) break; // too large: Verify refuses it
            }
            return buffer.ToArray();
        }

        // the answer from the release and its update file's bytes (null = it has none); no network, so the release
        // tool's tests run it on fixtures
        public static UpdateResult Evaluate(string current, string platform, IEnumerable<string> trustedKeys, ReleaseInfo release,
            byte[]? manifestBytes)
        {
            string latest = release.Version;
            if (release.Draft || release.Prerelease || Compare(latest, current) <= 0)
            {
                return new UpdateResult(UpdateStatus.UpToDate, current, latest);
            }
            if (manifestBytes == null)
            {
                return new UpdateResult(UpdateStatus.Unsigned, current, latest, Detail: "no " + UpdateManifest.AssetName(latest));
            }
            UpdateManifest? manifest = UpdateManifest.Verify(manifestBytes, trustedKeys, out string error);
            if (manifest == null)
            {
                return new UpdateResult(UpdateStatus.BadSignature, current, latest, Detail: error);
            }
            if (manifest.Version != latest)
            {
                return new UpdateResult(UpdateStatus.BadSignature, current, latest, Detail: "update file is for " + manifest.Version);
            }
            UpdateFile? signed = manifest.FileFor(platform);
            if (signed == null)
            {
                return new UpdateResult(UpdateStatus.NotForThisPlatform, current, latest);
            }
            ReleaseAsset? asset = Find(release.Assets, signed.Name);
            if (signed.Name != UpdateManifest.FileName(platform, latest) || asset == null)
            {
                return new UpdateResult(UpdateStatus.Mismatch, current, latest, Detail: "no release file " + signed.Name);
            }
            if (asset.Size != signed.Size)
            {
                return new UpdateResult(UpdateStatus.Mismatch, current, latest,
                    Detail: $"{signed.Name}: {asset.Size} bytes on the release, {signed.Size} signed");
            }
            if (asset.Sha256 != null && !asset.Sha256.Equals(signed.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                return new UpdateResult(UpdateStatus.Mismatch, current, latest, Detail: $"{signed.Name}: SHA-256 differs from the signed one");
            }
            return new UpdateResult(UpdateStatus.Available, current, latest, PageUrl(latest)) { File = signed, FileUrl = asset.Url };
        }

        // GitHub's release JSON (GET /repos/{repo}/releases/latest); null if it isn't one
        public static ReleaseInfo? ParseRelease(string json)
        {
            try
            {
                using JsonDocument doc = JsonDocument.Parse(json);
                JsonElement root = doc.RootElement;
                string tag = root.GetProperty("tag_name").GetString() ?? "";
                string version = tag.StartsWith('v') ? tag[1..] : tag;
                if (!TryParseVersion(version, out _)) return null;
                var assets = new List<ReleaseAsset>();
                if (root.TryGetProperty("assets", out JsonElement list) && list.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement a in list.EnumerateArray())
                    {
                        string? digest = a.TryGetProperty("digest", out JsonElement d) && d.ValueKind == JsonValueKind.String
                            ? d.GetString() : null;
                        assets.Add(new ReleaseAsset(a.GetProperty("name").GetString() ?? "", a.GetProperty("size").GetInt64(),
                            digest != null && digest.StartsWith("sha256:", StringComparison.Ordinal) ? digest["sha256:".Length..] : null,
                            a.GetProperty("browser_download_url").GetString() ?? ""));
                    }
                }
                return new ReleaseInfo(tag, version, Bool(root, "draft"), Bool(root, "prerelease"), assets);
            }
            catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
            {
                return null;
            }
        }

        private static bool Bool(JsonElement e, string name)
            => e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.True;

        private static ReleaseAsset? Find(IReadOnlyList<ReleaseAsset> assets, string name)
        {
            foreach (ReleaseAsset a in assets)
            {
                if (a.Name == name) return a;
            }
            return null;
        }

        private static readonly Regex VersionPattern = new(@"^(\d+)\.(\d+)\.(\d+)(?:-([0-9A-Za-z.]+))?$");

        public static bool TryParseVersion(string text, out (int Major, int Minor, int Patch, string Suffix) version)
        {
            Match m = VersionPattern.Match(text);
            if (!m.Success)
            {
                version = default;
                return false;
            }
            version = (int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value), m.Groups[4].Value);
            return true;
        }

        // < 0 if a is older than b. A version that doesn't parse (a dev build's "dev") counts as 0.0.0. With equal
        // numbers, a plain version is newer than a suffixed one (0.2.0 after 0.2.0-beta); two suffixes compare as text.
        public static int Compare(string a, string b)
        {
            if (!TryParseVersion(a, out var va)) va = (0, 0, 0, "");
            if (!TryParseVersion(b, out var vb)) vb = (0, 0, 0, "");
            int c = va.Major.CompareTo(vb.Major);
            if (c == 0) c = va.Minor.CompareTo(vb.Minor);
            if (c == 0) c = va.Patch.CompareTo(vb.Patch);
            if (c != 0) return c;
            if (va.Suffix.Length == 0 && vb.Suffix.Length == 0) return 0;
            if (va.Suffix.Length == 0) return 1;
            if (vb.Suffix.Length == 0) return -1;
            return string.CompareOrdinal(va.Suffix, vb.Suffix);
        }

        // the game's message box text for an answer (the box wraps at 140 px, about 7 lines)
        public static string Describe(UpdateResult r, string platform) => r.Status switch
        {
            UpdateStatus.UpToDate => $"you have the newest version, {r.Current}.",
            UpdateStatus.Available => $"second hunt {r.Latest} is out.",
            UpdateStatus.Unsigned => $"{r.Latest} is on github, but it has no signed update file, so it isn't offered here.",
            UpdateStatus.BadSignature => $"{r.Latest} is on github, but its signature didn't check out, so it isn't offered here.",
            UpdateStatus.Mismatch => $"{r.Latest} is on github, but its files don't match its signature, so it isn't offered here.",
            UpdateStatus.NotForThisPlatform =>
                $"{r.Latest} is out, but not for {(platform == UpdatePlatform.Windows ? "windows" : "android")} yet.",
            UpdateStatus.Offline => "couldn't reach github. check your connection and try again.",
            UpdateStatus.Busy => "github is busy right now. try again in a few minutes.",
            _ => "the update check didn't work. try again later."
        };
    }
}
