using System.Security.Cryptography;
using System.Text;
using MphRecomp.Update;

namespace SecondHunt.ReleaseTool;

// SecondHuntRelease <command>: see Usage. Exit code 0 = done / checks passed.
internal static class Program
{
    const string Usage = """
        SecondHuntRelease keygen --out <key.pem> [--no-password]
            Makes an update key (ECDSA P-256). The private key goes to <key.pem>, encrypted with a password you type
            (--no-password: test keys only); the public key to <key.pem>.pub, for UpdateKeys.cs.
        SecondHuntRelease sign --key <key.pem> --version <0.n.p-beta> --dir <folder> [--test]
            Signs SecondHunt-<version>-update.txt over the release files in <folder> (SecondHunt-<version>.apk and/or
            SecondHunt-<version>-windows-x64.zip), then checks it against the keys this source tree trusts.
        SecondHuntRelease verify --file <update.txt> [--dir <folder>] [--test]
            Checks an update file's signature (release key; --test also the test key) and the files in <folder>.
        SecondHuntRelease check [--current <version>] [--feed <api base>] [--test]
            What the game's UPDATES button answers right now, on both platforms (default: as a 0.0.0 build).
        SecondHuntRelease selftest
            The update check's offline cases.
        """;

    static int Main(string[] args)
    {
        if (args.Length == 0 || args[0] is "help" or "-h" or "--help")
        {
            Console.WriteLine(Usage);
            return args.Length == 0 ? 1 : 0;
        }
        var opts = Options(args[1..]);
        try
        {
            return args[0] switch
            {
                "keygen" => KeyGen(Need(opts, "out"), opts.ContainsKey("no-password")),
                "sign" => Sign(Need(opts, "key"), Need(opts, "version"), Need(opts, "dir"), opts.ContainsKey("test")),
                "verify" => Verify(Need(opts, "file"), opts.GetValueOrDefault("dir"), opts.ContainsKey("test")),
                "check" => Check(opts.GetValueOrDefault("current") ?? "0.0.0", opts.GetValueOrDefault("feed"), opts.ContainsKey("test")),
                "selftest" => SelfTest.Run(),
                _ => throw new ArgumentException("unknown command " + args[0])
            };
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or CryptographicException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine("error: " + ex.Message);
            return 1;
        }
    }

    static Dictionary<string, string> Options(string[] args)
    {
        var opts = new Dictionary<string, string>();
        for (int i = 0; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--")) throw new ArgumentException("unexpected " + args[i]);
            string name = args[i][2..];
            bool flag = i + 1 >= args.Length || args[i + 1].StartsWith("--");
            opts[name] = flag ? "" : args[++i];
        }
        return opts;
    }

    static string Need(Dictionary<string, string> opts, string name)
        => opts.TryGetValue(name, out string? v) && v.Length > 0 ? v : throw new ArgumentException($"--{name} is required");

    // ---- keygen ----

    static int KeyGen(string path, bool noPassword)
    {
        path = Path.GetFullPath(path);
        if (File.Exists(path)) throw new IOException(path + " exists; a new key would orphan every build that trusts it");
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        string pem;
        if (noPassword)
        {
            pem = key.ExportPkcs8PrivateKeyPem();
        }
        else
        {
            string password = ReadPassword("new update key password: ");
            if (password.Length < 8) throw new ArgumentException("use at least 8 characters");
            if (ReadPassword("again: ") != password) throw new ArgumentException("the two passwords differ");
            pem = key.ExportEncryptedPkcs8PrivateKeyPem(password,
                new PbeParameters(PbeEncryptionAlgorithm.Aes256Cbc, HashAlgorithmName.SHA256, 600_000));
        }
        string publicKey = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, pem);
        File.WriteAllText(path + ".pub", publicKey + "\n");
        Console.WriteLine($"private key: {path}{(noPassword ? " (NO PASSWORD: test use only)" : "")}");
        Console.WriteLine($"public key:  {path}.pub");
        Console.WriteLine($"key id:      {UpdateManifest.KeyIdOf(publicKey)}");
        Console.WriteLine($"public key (for UpdateKeys.cs): {publicKey}");
        return 0;
    }

    static ECDsa LoadKey(string path)
    {
        string pem = File.ReadAllText(path);
        var key = ECDsa.Create();
        if (pem.Contains("ENCRYPTED PRIVATE KEY"))
        {
            key.ImportFromEncryptedPem(pem, ReadPassword("update key password: "));
        }
        else
        {
            key.ImportFromPem(pem);
        }
        return key;
    }

    static string ReadPassword(string prompt)
    {
        if (Console.IsInputRedirected) throw new ArgumentException("the password is typed at the console; run this in a terminal");
        Console.Write(prompt);
        var sb = new StringBuilder();
        while (true)
        {
            ConsoleKeyInfo k = Console.ReadKey(intercept: true);
            if (k.Key == ConsoleKey.Enter) break;
            if (k.Key == ConsoleKey.Backspace)
            {
                if (sb.Length > 0) sb.Length--;
                continue;
            }
            if (!char.IsControl(k.KeyChar)) sb.Append(k.KeyChar);
        }
        Console.WriteLine();
        return sb.ToString();
    }

    // ---- sign / verify ----

    static int Sign(string keyPath, string version, string dir, bool test)
    {
        if (!UpdateCheck.TryParseVersion(version, out _)) throw new ArgumentException("version must look like 0.1.3-beta");
        var files = new List<UpdateFile>();
        foreach (string platform in new[] { UpdatePlatform.Android, UpdatePlatform.Windows })
        {
            string file = Path.Combine(dir, UpdateManifest.FileName(platform, version));
            if (!File.Exists(file)) continue;
            files.Add(Describe(platform, file));
        }
        if (files.Count == 0)
        {
            throw new ArgumentException($"no {UpdateManifest.FileName(UpdatePlatform.Android, version)} or " +
                $"{UpdateManifest.FileName(UpdatePlatform.Windows, version)} in {dir}");
        }
        using ECDsa key = LoadKey(keyPath);
        string publicKey = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
        string text = UpdateManifest.Sign(UpdateManifest.Body(version, files, UpdateManifest.KeyIdOf(publicKey)), key);
        string output = Path.Combine(dir, UpdateManifest.AssetName(version));
        File.WriteAllText(output, text, new UTF8Encoding(false));
        Console.WriteLine($"wrote {output}:\n{text}");
        foreach (UpdateFile f in files) Console.WriteLine($"  {f.Platform}: {f.Name} {f.Size} bytes");
        return Verify(output, dir, test);
    }

    static UpdateFile Describe(string platform, string path)
    {
        using FileStream s = File.OpenRead(path);
        return new UpdateFile(platform, Path.GetFileName(path), s.Length, Convert.ToHexString(SHA256.HashData(s)).ToLowerInvariant());
    }

    static int Verify(string path, string? dir, bool test)
    {
        UpdateManifest? m = UpdateManifest.Verify(File.ReadAllBytes(path), UpdateKeys.Trusted(publicBuild: !test), out string error);
        if (m == null)
        {
            Console.WriteLine($"FAIL {Path.GetFileName(path)}: {error}");
            if (!test && UpdateKeys.Release.Length == 0) Console.WriteLine("  (UpdateKeys.Release is empty: run keygen and put its public key there)");
            return 1;
        }
        Console.WriteLine($"OK signature: {m.Version}, key {m.KeyId}{(m.KeyId == KeyId(UpdateKeys.Release) ? " (release key)" : " (test key)")}");
        int bad = 0;
        foreach (UpdateFile f in m.Files)
        {
            if (f.Name != UpdateManifest.FileName(f.Platform, m.Version))
            {
                Console.WriteLine($"FAIL {f.Platform}: {f.Name} isn't the release name the game looks for");
                bad++;
                continue;
            }
            if (dir == null) continue;
            string file = Path.Combine(dir, f.Name);
            if (!File.Exists(file))
            {
                Console.WriteLine($"  {f.Name}: not in {dir} (not checked)");
                continue;
            }
            UpdateFile local = Describe(f.Platform, file);
            bool ok = local.Size == f.Size && local.Sha256 == f.Sha256;
            Console.WriteLine($"{(ok ? "OK" : "FAIL")} {f.Name}: {local.Size} bytes, sha256 {local.Sha256}");
            if (!ok) bad++;
        }
        return bad == 0 ? 0 : 1;
    }

    static string KeyId(string publicKey) => publicKey.Length == 0 ? "" : UpdateManifest.KeyIdOf(publicKey);

    // ---- check: the button's answer, live ----

    static int Check(string current, string? feed, bool test)
    {
        int bad = 0;
        foreach (string platform in new[] { UpdatePlatform.Android, UpdatePlatform.Windows })
        {
            UpdateResult r = UpdateCheck.RunAsync(current, platform, publicBuild: !test && feed == null, feed).GetAwaiter().GetResult();
            Console.WriteLine($"{platform}: {r.Status} (latest {r.Latest ?? "-"}){(r.Detail.Length > 0 ? " " + r.Detail : "")}");
            Console.WriteLine($"  box: \"{UpdateCheck.Describe(r, platform)}\"{(r.PageUrl != null ? " -> " + r.PageUrl : "")}");
            if (r.Status is not (UpdateStatus.Available or UpdateStatus.UpToDate)) bad++;
        }
        return bad == 0 ? 0 : 1;
    }
}
