using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using MphRecomp.Import;
using MphRecomp.Import.Switch;

namespace MphRead
{
    // -switchtest <file.nsp|.xci> --keys <prod.keys> [--title-keys <title.keys>] [--update <update.nsp>] [--list]
    //             [--base-only] [--verify | --verify-all] [--sha1 <romfs path>] [--extract <romfs path> <out file>] [--content control|manual|...] [--service]
    // The clean-room Switch reader (MphRecomp.Import.Switch) against a real game file: the package's NCAs (headers
    // decrypted), the key self-checks, the RomFS listing, and with --verify the IVFC hash tree over every block of the
    // files read (all files when the RomFS is under 1 GiB, else a sample). With --update the update-patched RomFS too
    // ("--update merged": the update inside a card image),
    // and what the update changed. --content: the RomFS of the first NCA of that content type instead of the game's
    // (a game without a RomFS of its own still has its Control NCA's). Prints no key or title key: only whether derived / listed / ticket keys agree.
    internal static class SwitchTest
    {
        public static void Run(string[] args)
        {
            string? Arg(string k) { int i = Array.IndexOf(args, k); return i > 0 && i + 1 < args.Length ? args[i + 1] : null; }
            if (args.Length < 2 || Arg("--keys") is not string keysPath)
            {
                Console.WriteLine("usage: -switchtest <file.nsp|.xci> --keys <prod.keys> [--title-keys <title.keys>] [--update <file>] [--list] [--verify] [--sha1 <path>] [--extract <path> <out>]");
                return;
            }
            string game = args[1];
            string? updatePath = Arg("--update");
            bool verify = args.Contains("--verify") || args.Contains("--verify-all");
            bool verifyAll = args.Contains("--verify-all");   // every file, however large the RomFS
            SwitchKeys keys = SwitchKeys.FromFile(keysPath, Arg("--title-keys"));
            Console.WriteLine($"keys: {keys.Count} named keys, {keys.TitleKeyCount} title keys (values not shown)");

            if (args.Contains("--service"))
            {
                // ImportService's mp4 path with this file: without keys (must ask for prod.keys), then with them
                string outDir = Path.Combine(Path.GetTempPath(), "switchtest_service");
                var service = new ImportService(outDir);
                foreach (SwitchKeys? k in new[] { null, keys })
                {
                    service.SwitchKeys = k;
                    try
                    {
                        using FileStream f = File.OpenRead(game);
                        ImportReport r = service.Import("mp4", f);
                        Console.WriteLine($"service ({(k == null ? "no keys" : "keys")}): imported, {r.GameId}");
                    }
                    catch (Exception e) { Console.WriteLine($"service ({(k == null ? "no keys" : "keys")}): {e.GetType().Name}: {e.Message}"); }
                }
                Console.WriteLine($"service: mp4 accepts {String.Join(" ", service.AcceptedSources("mp4").Extensions)}, folder {service.AcceptedSources("mp4").Folder}");
                return;
            }
            Describe(game, keys);
            if (updatePath != null && updatePath != "merged") Describe(updatePath, keys);

            var clock = Stopwatch.StartNew();
            if (Arg("--content") is string content)
            {
                ContentRomFs(game, keys, Enum.Parse<NcaContentType>(content, ignoreCase: true), verify, args.Contains("--list"));
                return;
            }
            // the game alone (a card image's merged update is applied unless --base-only, or when --update compares the two)
            bool baseOnly = args.Contains("--base-only") || updatePath != null;
            using SwitchRomFs fs = SwitchGame.Open(game, keys, null, verify, baseOnly);
            Console.WriteLine($"\nRomFS of {Path.GetFileName(game)}: title {fs.Title}, {fs.Paths.Count()} files, "
                + $"{fs.Paths.Sum(p => fs.Length(p)) / 1048576.0:0.0} MiB (opened in {clock.ElapsedMilliseconds} ms)");
            PrintIvfc(fs);
            Listing(fs, args.Contains("--list"));
            if (verify) Verify(fs, verifyAll);

            SwitchRomFs? patched = null;
            if (updatePath != null)
            {
                clock.Restart();
                if (updatePath == "merged") updatePath = null;   // the update merged into the card image
                patched = SwitchGame.Open(game, keys, updatePath, verify);
                Console.WriteLine($"\nPatched RomFS (+ {(updatePath != null ? Path.GetFileName(updatePath) : "the merged update")}): title {patched.Title}, {patched.Paths.Count()} files, "
                    + $"{patched.Paths.Sum(p => patched.Length(p)) / 1048576.0:0.0} MiB (opened in {clock.ElapsedMilliseconds} ms)");
                PrintIvfc(patched);
                Listing(patched, args.Contains("--list"));
                if (verify) Verify(patched, verifyAll);
                Diff(fs, patched);
            }

            SwitchRomFs target = patched ?? fs;
            if (Arg("--sha1") is string sp)
            {
                using Stream s = target.OpenRead(sp);
                Console.WriteLine($"\nSHA-1 {sp}: {Convert.ToHexString(SHA1.HashData(s)).ToLowerInvariant()} ({target.Length(sp)} bytes)");
            }
            int ex = Array.IndexOf(args, "--extract");
            if (ex > 0 && ex + 2 < args.Length)
            {
                using Stream s = target.OpenRead(args[ex + 1]);
                using FileStream o = File.Create(args[ex + 2]);
                s.CopyTo(o, 1 << 20);
                Console.WriteLine($"\nextracted {args[ex + 1]} -> {args[ex + 2]} ({o.Length} bytes)");
            }
            patched?.Dispose();
        }

        static void ContentRomFs(string path, SwitchKeys keys, NcaContentType type, bool verify, bool all)
        {
            using FileStream f = File.OpenRead(path);
            using var pkg = new SwitchPackage(f, keys);
            Nca nca = pkg.Ncas.First(n => n.ContentType == type);
            NcaSection sec = nca.Sections.First(s => s != null && s.IsRomFs)!;
            using var fs = new SwitchRomFs(nca.OpenSection(sec), sec.HashData, nca.ProgramId, patched: false, verify);
            Console.WriteLine($"\n{type} RomFS of {nca.Name}: {fs.Paths.Count()} files, {fs.Paths.Sum(p => fs.Length(p))} bytes");
            PrintIvfc(fs);
            Listing(fs, all);
            if (verify) Verify(fs);
        }

        static void Describe(string path, SwitchKeys keys)
        {
            using FileStream f = File.OpenRead(path);
            using var pkg = new SwitchPackage(f, keys);
            Console.WriteLine($"\n{Path.GetFileName(path)}: {pkg.Format}, {f.Length / 1048576.0:0.0} MiB, {pkg.Files.Count} files in the container, "
                + $"{pkg.Ncas.Count} NCAs, {pkg.TicketCount} common tickets{(pkg.PersonalizedTickets > 0 ? $", {pkg.PersonalizedTickets} personalized" : "")}");
            foreach (PartitionFile pf in pkg.Files.Where(x => !x.Name.EndsWith(".nca", StringComparison.OrdinalIgnoreCase)))
            {
                Console.WriteLine($"  {pf.Name} ({pf.Size} bytes)");
            }
            foreach (Nca n in pkg.Ncas)
            {
                Console.WriteLine($"  {n}");
                foreach (NcaSection? s in n.Sections) if (s != null) Console.WriteLine($"    {s}");
                int rev = SwitchKeys.Revision(n.KeyGeneration);
                bool? works = n.CheckKey();
                Console.WriteLine($"    section key decrypts the NCA: {(works == null ? "can't tell" : works.Value ? "yes" : "NO")}");
                if (n.HasRightsId)
                {
                    byte[]? t = pkg.TicketKey(n.RightsId), l = keys.TitleKeyFor(n.RightsId);
                    Console.WriteLine($"    title key: ticket {(t != null ? "yes" : "no")}, title.keys {(l != null ? "yes" : "no")}"
                        + (t != null && l != null ? $", they agree: {(t.AsSpan().SequenceEqual(l) ? "yes" : "NO")}" : ""));
                    byte[]? d = keys.DeriveTitleKek(rev), g = keys.Get($"titlekek_{rev:x2}");
                    Console.WriteLine($"    titlekek_{rev:x2}: listed {(g != null ? "yes" : "no")}, derivable {(d != null ? "yes" : "no")}"
                        + (d != null && g != null ? $", derived == listed: {(d.AsSpan().SequenceEqual(g) ? "yes" : "NO")}" : ""));
                }
                else
                {
                    string kind = n.KeyAreaIndex switch { 0 => "application", 1 => "ocean", _ => "system" };
                    byte[]? d = keys.DeriveKeyAreaKey(kind, rev), g = keys.Get($"key_area_key_{kind}_{rev:x2}");
                    Console.WriteLine($"    key_area_key_{kind}_{rev:x2}: listed {(g != null ? "yes" : "no")}, derivable {(d != null ? "yes" : "no")}"
                        + (d != null && g != null ? $", derived == listed: {(d.AsSpan().SequenceEqual(g) ? "yes" : "NO")}" : ""));
                }
            }
        }

        static void PrintIvfc(SwitchRomFs fs)
        {
            IvfcTree t = fs.Ivfc!;
            for (int i = 0; i < t.Levels; i++)
            {
                Console.WriteLine($"  IVFC level {i}: 0x{t.Offset[i]:X} + 0x{t.Size[i]:X}, block 0x{1 << t.BlockLog[i]:X}{(i == t.Levels - 1 ? " (RomFS)" : "")}");
            }
        }

        static void Listing(SwitchRomFs fs, bool all)
        {
            List<string> paths = fs.Paths.OrderBy(p => p, StringComparer.Ordinal).ToList();
            IEnumerable<string> shown = all ? paths : paths.Take(12);
            foreach (string p in shown) Console.WriteLine($"    {p}  ({fs.Length(p)} bytes)");
            if (!all && paths.Count > 12) Console.WriteLine($"    ... {paths.Count - 12} more (--list for all)");
        }

        // reads files through the verifying RomFS (every block's hash chain checked up to the master hash)
        static void Verify(SwitchRomFs fs, bool all = false)
        {
            var clock = Stopwatch.StartNew();
            List<string> paths = fs.Paths.OrderBy(p => fs.DataOffsetOf(p)).ToList();
            long total = paths.Sum(p => fs.Length(p));
            List<string> pick = total <= 1L << 30 || all ? paths
                : paths.OrderBy(p => fs.Length(p)).Take(4).Concat(paths.OrderByDescending(p => fs.Length(p)).Take(2))
                    .Concat(paths.Where((_, i) => i % Math.Max(1, paths.Count / 6) == 0)).Distinct().ToList();
            long bytes = 0;
            var buf = new byte[1 << 20];
            foreach (string p in pick)
            {
                using Stream s = fs.OpenRead(p);
                int n;
                while ((n = s.Read(buf, 0, buf.Length)) > 0) bytes += n;
            }
            // and a seek-and-read far into the largest file (offsets past 4 GiB when the RomFS is that big)
            string big = paths.OrderByDescending(p => fs.Length(p)).First();
            using (Stream s = fs.OpenRead(big))
            {
                s.Seek(-Math.Min(s.Length, 4096), SeekOrigin.End);
                s.ReadExactly(buf, 0, (int)Math.Min(s.Length, 4096));
            }
            Console.WriteLine($"  verify: {pick.Count} of {paths.Count} files read whole ({bytes / 1048576.0:0.0} MiB), {fs.Ivfc!.CheckedBlocks} hash blocks "
                + $"checked up to the master hash: all match ({clock.Elapsed.TotalSeconds:0.0} s)");
        }

        static void Diff(SwitchRomFs a, SwitchRomFs b)
        {
            var added = b.Paths.Where(p => !a.Contains(p)).OrderBy(p => p).ToList();
            var removed = a.Paths.Where(p => !b.Contains(p)).OrderBy(p => p).ToList();
            var both = a.Paths.Where(b.Contains).ToList();
            var resized = both.Where(p => a.Length(p) != b.Length(p)).OrderBy(p => p).ToList();
            var changed = new List<string>(resized);
            long sameSize = both.Where(p => a.Length(p) == b.Length(p)).Sum(p => a.Length(p));
            if (sameSize <= 8L << 30)
            {
                foreach (string p in both.Where(p => a.Length(p) == b.Length(p)))
                {
                    using Stream x = a.OpenRead(p), y = b.OpenRead(p);
                    if (!SHA1.HashData(x).AsSpan().SequenceEqual(SHA1.HashData(y))) changed.Add(p);
                }
            }
            Console.WriteLine($"\nupdate vs base: {added.Count} files added, {removed.Count} removed, {changed.Count} changed "
                + $"({resized.Count} of them resized{(sameSize > 8L << 30 ? "; same-size files not compared" : "")}), {both.Count - changed.Count} unchanged");
            foreach (string p in added.Take(6)) Console.WriteLine($"  + {p} ({b.Length(p)} bytes)");
            foreach (string p in removed.Take(6)) Console.WriteLine($"  - {p}");
            foreach (string p in changed.OrderBy(p => p).Take(8)) Console.WriteLine($"  * {p} ({a.Length(p)} -> {b.Length(p)} bytes)");
        }
    }
}
