using System.Security.Cryptography;
using System.Text;
using MphRecomp.Update;

namespace SecondHunt.ReleaseTool;

// The update check's answers on fixtures (no network): a throwaway trusted key, a second untrusted one, and a release
// shaped like GitHub's JSON for each case the button can meet.
internal static class SelfTest
{
    static int _failed, _passed;

    public static int Run()
    {
        Versions();
        Answers();
        Parsing();
        Texts();
        Console.WriteLine($"{_passed} passed, {_failed} failed");
        return _failed == 0 ? 0 : 1;
    }

    static void Expect(bool ok, string what)
    {
        if (ok) _passed++;
        else
        {
            _failed++;
            Console.WriteLine("FAIL " + what);
        }
    }

    static void Versions()
    {
        Expect(UpdateCheck.Compare("0.1.2-beta", "0.1.3-beta") < 0, "0.1.2-beta < 0.1.3-beta");
        Expect(UpdateCheck.Compare("0.1.10-beta", "0.1.9-beta") > 0, "0.1.10-beta > 0.1.9-beta (numbers, not text)");
        Expect(UpdateCheck.Compare("0.2.0-beta", "0.1.9-beta") > 0, "0.2.0-beta > 0.1.9-beta");
        Expect(UpdateCheck.Compare("0.2.0", "0.2.0-beta") > 0, "0.2.0 > 0.2.0-beta");
        Expect(UpdateCheck.Compare("0.1.2-beta", "0.1.2-beta") == 0, "equal");
        Expect(UpdateCheck.Compare("0.1.0", "0.1.2-beta") < 0, "Android dev 0.1.0 < 0.1.2-beta");
        Expect(UpdateCheck.Compare("dev", "0.0.1") < 0, "unparsable = 0.0.0");
        Expect(!UpdateCheck.TryParseVersion("v0.1.2-beta", out _), "the tag's v isn't part of the version");
    }

    const string V = "0.1.3-beta";
    static readonly string Apk = UpdateManifest.FileName(UpdatePlatform.Android, V);
    static readonly string Zip = UpdateManifest.FileName(UpdatePlatform.Windows, V);
    static readonly string ApkHash = new('a', 64), ZipHash = new('b', 64);

    static ReleaseInfo Release(string version = V, bool prerelease = false, long apkSize = 1000, string? apkDigest = null,
        bool withZip = true, bool withApk = true)
    {
        var assets = new List<ReleaseAsset>();
        if (withApk) assets.Add(new ReleaseAsset(UpdateManifest.FileName(UpdatePlatform.Android, version), apkSize, apkDigest ?? ApkHash, "x"));
        if (withZip) assets.Add(new ReleaseAsset(UpdateManifest.FileName(UpdatePlatform.Windows, version), 2000, ZipHash, "x"));
        assets.Add(new ReleaseAsset(UpdateManifest.AssetName(version), 300, null, "x"));
        return new ReleaseInfo("v" + version, version, false, prerelease, assets);
    }

    static byte[] Manifest(ECDsa key, string version = V, bool withZip = true, Func<string, string>? tamper = null)
    {
        var files = new List<UpdateFile> { new(UpdatePlatform.Android, UpdateManifest.FileName(UpdatePlatform.Android, version), 1000, ApkHash) };
        if (withZip) files.Add(new UpdateFile(UpdatePlatform.Windows, UpdateManifest.FileName(UpdatePlatform.Windows, version), 2000, ZipHash));
        string publicKey = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
        string text = UpdateManifest.Sign(UpdateManifest.Body(version, files, UpdateManifest.KeyIdOf(publicKey)), key);
        return Encoding.UTF8.GetBytes(tamper?.Invoke(text) ?? text);
    }

    static void Answers()
    {
        using ECDsa trusted = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using ECDsa other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        string[] keys = { Convert.ToBase64String(trusted.ExportSubjectPublicKeyInfo()) };
        byte[] good = Manifest(trusted);

        UpdateStatus Ask(string current, ReleaseInfo release, byte[]? manifest, string platform = UpdatePlatform.Android)
            => UpdateCheck.Evaluate(current, platform, keys, release, manifest).Status;

        UpdateResult offered = UpdateCheck.Evaluate("0.1.2-beta", UpdatePlatform.Android, keys, Release(), good);
        Expect(offered.Status == UpdateStatus.Available, "newer + signed = offered");
        Expect(offered.PageUrl == "https://github.com/BountyHunterKanden/SecondHunt/releases/tag/v0.1.3-beta", "page URL from the version");
        Expect(Ask("0.1.2-beta", Release(), good, UpdatePlatform.Windows) == UpdateStatus.Available, "Windows offered too");
        Expect(Ask(V, Release(), good) == UpdateStatus.UpToDate, "same version = up to date");
        Expect(Ask("0.1.4-beta", Release(), good) == UpdateStatus.UpToDate, "older release = up to date");
        Expect(Ask("0.1.2-beta", Release(prerelease: true), good) == UpdateStatus.UpToDate, "prerelease never offered");
        Expect(Ask("0.1.2-beta", Release(), null) == UpdateStatus.Unsigned, "no update file = not offered");
        Expect(Ask("0.1.2-beta", Release(), Manifest(other)) == UpdateStatus.BadSignature, "untrusted key");
        Expect(Ask("0.1.2-beta", Release(), Manifest(trusted, tamper: t => t.Replace(" 1000 ", " 1001 "))) == UpdateStatus.BadSignature,
            "edited after signing");
        Expect(Ask("0.1.2-beta", Release(), Manifest(trusted, tamper: t => t.Replace("\n", "\r\n"))) == UpdateStatus.BadSignature,
            "CRLF line ends");
        Expect(Ask("0.1.2-beta", Release(), Manifest(trusted, tamper: t => t[..t.IndexOf("signature ", StringComparison.Ordinal)]))
            == UpdateStatus.BadSignature, "signature line missing");
        Expect(Ask("0.1.2-beta", Release(), Manifest(trusted, version: "0.1.2-beta")) == UpdateStatus.BadSignature,
            "update file for another version");
        Expect(Ask("0.1.2-beta", Release(withZip: false), Manifest(trusted, withZip: false), UpdatePlatform.Windows)
            == UpdateStatus.NotForThisPlatform, "no Windows file in this release");
        Expect(Ask("0.1.2-beta", Release(apkDigest: new string('c', 64)), good) == UpdateStatus.Mismatch,
            "APK on the release isn't the signed one (digest)");
        Expect(Ask("0.1.2-beta", Release(apkSize: 999), good) == UpdateStatus.Mismatch, "APK size differs");
        Expect(Ask("0.1.2-beta", Release(withApk: false), good) == UpdateStatus.Mismatch, "signed APK missing from the release");
        Expect(Ask("0.1.2-beta", Release(), new byte[UpdateManifest.MaxBytes + 1]) == UpdateStatus.BadSignature, "oversized update file");
    }

    static void Parsing()
    {
        // the shape of GET /repos/BountyHunterKanden/SecondHunt/releases/latest (v0.1.2-beta, 2026-10-06), trimmed
        const string json = """
            {"tag_name":"v0.1.2-beta","name":"Second Hunt Beta 1.2 (0.1.2-beta)","draft":false,"prerelease":false,
             "assets":[
              {"name":"SecondHunt-0.1.2-beta-windows-x64.zip","size":63757448,
               "digest":"sha256:83c368660936b8cd865a9d973e89b309f9bdb3f815abecc153617be4f32a98e3",
               "browser_download_url":"https://github.com/BountyHunterKanden/SecondHunt/releases/download/v0.1.2-beta/SecondHunt-0.1.2-beta-windows-x64.zip"},
              {"name":"SecondHunt-0.1.2-beta.apk","size":24144744,"digest":null,
               "browser_download_url":"https://github.com/BountyHunterKanden/SecondHunt/releases/download/v0.1.2-beta/SecondHunt-0.1.2-beta.apk"}]}
            """;
        ReleaseInfo? r = UpdateCheck.ParseRelease(json);
        Expect(r is { Version: "0.1.2-beta", Draft: false, Prerelease: false, Assets.Count: 2 }, "parses GitHub's release JSON");
        Expect(r?.Assets[0].Sha256 == "83c368660936b8cd865a9d973e89b309f9bdb3f815abecc153617be4f32a98e3", "digest without its sha256: prefix");
        Expect(r?.Assets[1].Sha256 == null, "a null digest = no hash to compare");
        Expect(r != null && UpdateCheck.Evaluate("0.1.1-beta", UpdatePlatform.Android, Array.Empty<string>(), r, null).Status
            == UpdateStatus.Unsigned, "today's 0.1.2-beta has no update file");
        Expect(UpdateCheck.ParseRelease("{\"message\":\"Not Found\"}") == null, "an error body isn't a release");
        Expect(UpdateCheck.ParseRelease("not json") == null, "garbage isn't a release");
        Expect(UpdateCheck.ParseRelease("{\"tag_name\":\"nightly\",\"assets\":[]}") == null, "a tag that isn't a version");
    }

    static void Texts()
    {
        foreach (UpdateStatus s in Enum.GetValues<UpdateStatus>())
        {
            string text = UpdateCheck.Describe(new UpdateResult(s, "0.1.12-beta", "0.1.13-beta"), UpdatePlatform.Windows);
            // the game's box: 140 px lines, about 7 of them; ~5.5 px a glyph -> keep under ~130 characters
            Expect(text.Length <= 130, $"{s} text fits the box ({text.Length} chars)");
        }
    }
}
