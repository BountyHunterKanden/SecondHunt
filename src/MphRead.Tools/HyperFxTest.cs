using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using MphRecomp.Import;
using MphRecomp.Import.Disc;
using MphRecomp.Import.Retro3;

namespace MphRead
{
    // -hyperfxtest: Dark Samus's Corruption effects (docs/MP3_HYPER_EFFECTS_FORMATS.md, H3).
    //   where <disc or extracted folder>...: which pak holds each Hyper asset on each disc, and whether its bytes match
    internal static class HyperFxTest
    {
        // the Hyper roots: beam shot, charged blast, ball lightning (blue around it, red SpikesAI to targets), ball glow, sounds
        static readonly ulong[] Roots =
        {
            0x06fe1d75af37114a, 0x019855c26606c969, 0x3cf9229136ce2a88, 0x774aaa456a4c5ac9, 0xe694dbadf484b47b, 0x192f13da8ff3ff56,
            0x7fa2a74e3923db6c, 0xaab065969602e73f, 0x90bc1f5a1997eded, 0x80efe360c0a5855d, 0x0c581c8ba10d4429,
            0x04f4f7c2c217a6e0, 0x2b91ce7603b3abd1, 0x1c1a8eba57ecf57f, 0x5cec3a793be7b616, 0xd6558a90ff59e86a,
        };

        // the body recipe's roles (recipes/bodies/MP3DarkSamus.json "hyperfx")
        public static readonly (string Role, ulong Id)[] Roles =
        {
            ("beam", 0x06fe1d75af37114a), ("burst", 0x019855c26606c969), ("muzzle", 0xe694dbadf484b47b),
            ("charge", 0x192f13da8ff3ff56), ("burstmuzzle", 0x7fa2a74e3923db6c), ("ballspikes", 0xaab065969602e73f),
            ("ballglow", 0x90bc1f5a1997eded), ("wake", 0x80efe360c0a5855d), ("contacts", 0x0c581c8ba10d4429),
            ("fire", 0x04f4f7c2c217a6e0), ("chargefire", 0x5cec3a793be7b616), ("ballloop", 0x2b91ce7603b3abd1),
            ("ballimpact", 0x1c1a8eba57ecf57f), ("ballbolts", 0xd6558a90ff59e86a), ("ballparams", 0x248eaae62c74481e),
        };

        public static readonly string[] Paks = { "SamGunFx.pak", "InGameNAND.pak", "InGameAudio.pak" };

        public static void Run(string[] args)
        {
            if (args.Length >= 3 && args[1] == "where")
            {
                Where(args.Skip(2).ToArray());
                return;
            }
            if (args.Length >= 4 && args[1] == "export")
            {
                Console.WriteLine(Export(args[2], args[3]) ? "PASS hyperfxtest export" : "FAIL hyperfxtest export");
                return;
            }
            if (args.Length >= 3 && args[1] == "keys")
            {
                // an exported ELSC's line colours, every key
                var e = MphRecomp.Import.Retro.Elsc.Load(File.ReadAllBytes(args[2]), mp3: true);
                foreach (string t in new[] { "LCL1", "LCL2", "LCL3" })
                {
                    var k = e[t];
                    Console.WriteLine($"{t} {k?.Kind} loop {k?.Loop} {k?.LoopStart}-{k?.LoopEnd}: "
                        + (k?.Keys is float[] f ? string.Join(" ", Enumerable.Range(0, f.Length / 4).Select(i => $"({f[i * 4]:0.##},{f[i * 4 + 1]:0.##},{f[i * 4 + 2]:0.##},{f[i * 4 + 3]:0.###})")) : ""));
                }
                return;
            }
            if (args.Length >= 5 && args[1] == "file")
            {
                // research: loose files on a disc whose path matches the filter (regex), saved to a folder (e.g. Standard.ntwk)
                using IFileSource src = Open(args[2]);
                var rx = new System.Text.RegularExpressions.Regex(args[3], System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                Directory.CreateDirectory(args[4]);
                foreach (string path in src.Paths.Where(p => rx.IsMatch(p)))
                {
                    string to = Path.Combine(args[4], path.Replace('/', '_').Replace((char)92, (char)95));
                    File.WriteAllBytes(to, src.ReadFile(path));
                    Console.WriteLine($"{src.GameId} {path} -> {to}");
                }
                return;
            }
            if (args.Length >= 3 && args[1] == "parseall")
            {
                Console.WriteLine(ParseAll(args[2]) ? "PASS hyperfxtest parseall" : "FAIL hyperfxtest parseall");
                return;
            }
            if (args.Length >= 4 && args[1] == "names")
            {
                // research: named resources across every pak whose name or type matches the filter (regex, case-insensitive)
                using IFileSource src = Open(args[2]);
                using Pak3Set set = OpenPaks(src);
                var rx = new System.Text.RegularExpressions.Regex(args[3], System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                var seen = new HashSet<ulong>();
                foreach (Pak3 pk in set.Paks)
                    foreach ((ulong id, (string type, string name)) in pk.Names)
                        if (rx.IsMatch(type + " " + name) && seen.Add(id))
                            Console.WriteLine($"{type} {id:x16} {name} [{pk.Name}]");
                return;
            }
            if (args.Length >= 4 && args[1] == "list")
            {
                // research: every resource of one type across every pak (named or not)
                using IFileSource src = Open(args[2]);
                using Pak3Set set = OpenPaks(src);
                foreach (Pak3 pk in set.Paks)
                    foreach (ulong id in pk.Order)
                        if (pk.TypeOf(id) == args[3])
                            Console.WriteLine($"{args[3]} {id:x16} {(pk.Names.TryGetValue(id, out var n) ? n.Name : "?")} [{pk.Name}]");
                return;
            }
            if (args.Length >= 4 && args[1] == "refs")
            {
                // research: which resources (in which paks, plus loose disc files) contain the given 64-bit id's bytes
                using IFileSource src = Open(args[2]);
                ulong want = Convert.ToUInt64(args[3], 16);
                byte[] pat = new byte[8];
                System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(pat, want);
                foreach (string path in src.Paths.OrderBy(p => p, StringComparer.Ordinal))
                {
                    if (!path.EndsWith(".pak", StringComparison.OrdinalIgnoreCase))
                    {
                        using Stream fs = src.OpenRead(path);
                        if (fs.Length > 64 << 20) continue;
                        var ms = new MemoryStream(); fs.CopyTo(ms);
                        if (ms.ToArray().AsSpan().IndexOf(pat) >= 0) Console.WriteLine($"loose {path}");
                        continue;
                    }
                    if (args.Length > 4 && !System.Text.RegularExpressions.Regex.IsMatch(path, args[4])) continue;
                    using var pk = new Pak3(src.OpenRead(path), path, leaveOpen: false);
                    foreach (ulong id in pk.Order)
                    {
                        if (id == want) continue;
                        byte[] b;
                        try { b = pk.Get(id); } catch { continue; }
                        if (b.AsSpan().IndexOf(pat) >= 0)
                            Console.WriteLine($"{pk.TypeOf(id)} {id:x16} {(pk.Names.TryGetValue(id, out var n) ? n.Name : "?")} [{path}]");
                    }
                }
                return;
            }
            if (args.Length >= 5 && args[1] == "strings")
            {
                // research: printable strings (with file offsets) in loose disc files matching a path regex and a text regex
                using IFileSource src = Open(args[2]);
                var rx = new System.Text.RegularExpressions.Regex(args[4], System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                foreach (string path in src.Paths.Where(p => System.Text.RegularExpressions.Regex.IsMatch(p, args[3])))
                {
                    using Stream fs = src.OpenRead(path);
                    var ms = new MemoryStream(); fs.CopyTo(ms);
                    byte[] b = ms.ToArray();
                    for (int i = 0; i < b.Length; i++)
                    {
                        int j = i;
                        while (j < b.Length && b[j] >= 0x20 && b[j] < 0x7f) j++;
                        if (j - i >= 4)
                        {
                            string t = System.Text.Encoding.ASCII.GetString(b, i, j - i);
                            if (rx.IsMatch(t)) Console.WriteLine($"{path} {i:x}: {t}");
                        }
                        i = j;
                    }
                }
                Console.WriteLine("paths: " + string.Join(" ", src.Paths.Where(p => !p.EndsWith(".pak", StringComparison.OrdinalIgnoreCase) && !p.Contains('/')).Take(20)));
                return;
            }
            if (args.Length >= 4 && args[1] == "dol")
            {
                // research: save the disc's main.dol (header 0x420) to a file
                using IFileSource src = Open(args[2]);
                var blob = (IDiscBlob)src.GetType().GetField("_data", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(src)!;
                var h = new byte[0x440];
                blob.Read(0, h);
                long off = (long)System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(h.AsSpan(0x420)) << (src.GetType().Name == "FstFileSystem" && h[0x18] == 0x5D ? 2 : 0);
                var dh = new byte[0x100];
                blob.Read(off, dh);
                long size = 0x100;
                for (int i = 0; i < 18; i++)
                    size = Math.Max(size, System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(dh.AsSpan(i * 4)) + (long)System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(dh.AsSpan(0x90 + i * 4)));
                var dol = new byte[size];
                blob.Read(off, dol);
                File.WriteAllBytes(args[3], dol);
                Console.WriteLine($"main.dol at {off:x}: {size} bytes");
                return;
            }
            if (args.Length >= 5 && args[1] == "raw")
            {
                // research: save one resource's (decompressed) bytes
                using IFileSource src = Open(args[2]);
                using Pak3Set set = OpenPaks(src);
                File.WriteAllBytes(args[4], set.Get(Convert.ToUInt64(args[3], 16)));
                return;
            }
            if (args.Length >= 4 && args[1] == "dump")
            {
                // research: one PART / SWHC / ELSC / DGRP's properties, element trees, and named dependencies
                using IFileSource src = Open(args[2]);
                using Pak3Set set = OpenPaks(src);
                var fold = new Dictionary<uint, ulong>();
                var names = new Dictionary<ulong, string>();
                foreach (Pak3 pk in set.Paks)
                {
                    foreach (ulong id in pk.Order) fold[MphRecomp.Import.Retro.Part.Fold(id)] = id;
                    foreach ((ulong id, (string type, string name)) in pk.Names) names.TryAdd(id, name);
                }
                foreach (string a in args.Skip(3))
                {
                    try { Dump(set, Convert.ToUInt64(a, 16), fold, names); }
                    catch (Exception ex) { Console.WriteLine($"  FAIL {ex.Message}"); }
                }
                return;
            }
            Console.WriteLine("usage: -hyperfxtest where <disc|folder>... | export <disc|folder> <out> | parseall <disc|folder>");
        }

        static Pak3Set OpenPaks(IFileSource src, string[]? only = null)
        {
            List<string> paks = src.Paths.Where(p => p.EndsWith(".pak", StringComparison.OrdinalIgnoreCase)
                && (only == null || only.Any(o => Path.GetFileName(p).Equals(o, StringComparison.OrdinalIgnoreCase))))
                .OrderBy(p => p, StringComparer.Ordinal).ToList();
            return new Pak3Set(paks.Select(p => new Pak3(src.OpenRead(p), p, leaveOpen: false)));
        }

        // export the Hyper effects, then read every file back
        static bool Export(string source, string outDir)
        {
            using IFileSource src = Open(source);
            using Pak3Set set = OpenPaks(src, Paks);
            Directory.CreateDirectory(outDir);
            int files = MphRecomp.Import.Effects.HyperFx.Export(set, Roles, outDir);
            string dir = Path.Combine(outDir, MphRecomp.Import.Effects.HyperFx.Folder);
            bool ok = true;
            var kinds = new Dictionary<string, int>();
            foreach (string f in Directory.GetFiles(dir))
            {
                string ext = Path.GetExtension(f).TrimStart('.');
                kinds[ext] = kinds.GetValueOrDefault(ext) + 1;
                try
                {
                    byte[] b = File.ReadAllBytes(f);
                    switch (ext)
                    {
                        case "part": MphRecomp.Import.Retro.Part.Load(b, mp3: true); break;
                        case "swhc": MphRecomp.Import.Retro.Part.Swoosh.Load(b, mp3: true); break;
                        case "elsc": MphRecomp.Import.Retro.Elsc.Load(b, mp3: true); break;
                        case "wpsc": MphRecomp.Import.Retro.Wpsc.Load(b, mp3: true); break;
                        case "crsc": MphRecomp.Import.Retro.Crsc.Load(b, mp3: true); break;
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"FAIL {Path.GetFileName(f)}: {ex.Message}");
                    ok = false;
                }
            }
            var cfg = MphRecomp.Import.Effects.HyperFx.Config.Load(outDir);
            Console.WriteLine($"{files} files: {string.Join(", ", kinds.OrderBy(k => k.Key).Select(k => $"{k.Value} .{k.Key}"))}");
            Console.WriteLine($"roles: {string.Join(' ', cfg!.Roles.Select(r => $"{r.Key}={r.Value.Type}:{r.Value.Id:x8}"))}");
            Console.WriteLine($"sounds: {string.Join(' ', cfg.Sounds.Select(s => $"{s.Key} x{s.Value.Count} vol {s.Value[0].Volume} loop {s.Value[0].Loop}"))}");
            foreach (string wav in Directory.GetFiles(dir, "*.wav"))
            {
                short[]? pcm = MphRecomp.Import.Retro.MusyX.ReadWav(wav, out int rate);
                int peak = pcm == null ? 0 : pcm.Max(x => Math.Abs((int)x));
                Console.WriteLine($"  {Path.GetFileName(wav)}: {pcm?.Length / (float)rate:0.00} s at {rate} Hz, peak {peak}");
                if (pcm == null || pcm.Length == 0 || peak < 100) ok = false;
            }
            return ok && cfg.Roles.Count + cfg.Sounds.Count == Roles.Length;   // every role: an effect or a sound
        }

        // every PART / SWHC / ELSC / WPSC in the effect paks parses
        static bool ParseAll(string source)
        {
            using IFileSource src = Open(source);
            using Pak3Set set = OpenPaks(src, new[] { "SamGunFx.pak", "InGameNAND.pak" });
            var count = new Dictionary<string, (int Ok, int Bad)>();
            foreach (Pak3 pk in set.Paks)
            {
                foreach (ulong id in pk.Order)
                {
                    string type = pk.TypeOf(id);
                    if (type is not ("PART" or "SWHC" or "ELSC" or "WPSC" or "CRSC")) continue;
                    (int ok, int bad) = count.GetValueOrDefault(type);
                    try
                    {
                        byte[] b = pk.Get(id);
                        switch (type)
                        {
                            case "PART": MphRecomp.Import.Retro.Part.Load(b, mp3: true); break;
                            case "SWHC": MphRecomp.Import.Retro.Part.Swoosh.Load(b, mp3: true); break;
                            case "ELSC": MphRecomp.Import.Retro.Elsc.Load(b, mp3: true); break;
                            case "WPSC": MphRecomp.Import.Retro.Wpsc.Load(b, mp3: true); break;
                            case "CRSC": MphRecomp.Import.Retro.Crsc.Load(b, mp3: true); break;
                        }
                        ok++;
                    }
                    catch (Exception ex)
                    {
                        if (bad < 5) Console.WriteLine($"  {pk.Name} {id:x16} {type}: {ex.Message}");
                        bad++;
                    }
                    count[type] = (ok, bad);
                }
            }
            foreach ((string t, (int ok, int bad)) in count) Console.WriteLine($"{t}: {ok} parse, {bad} fail");
            return count.Values.All(v => v.Bad == 0);
        }

        static string Nm(ulong id, Dictionary<ulong, string> names) => names.TryGetValue(id, out string? n) ? n : "?";

        static void Dump(Pak3Set set, ulong id, Dictionary<uint, ulong> fold, Dictionary<ulong, string> names)
        {
            string? type = set.TypeOf(id);
            Console.WriteLine($"== {type} {id:x16} {Nm(id, names)} [{set.PakOf(id)?.Name}]");
            if (type == null) return;
            byte[] b = set.Get(id);
            string Ref(uint f) => fold.TryGetValue(f, out ulong full) ? $"{set.TypeOf(full)} {full:x16} {Nm(full, names)}" : $"?{f:x8}";
            string Tree(MphRecomp.Import.Retro.Part.Elem? e)
            {
                if (e == null) return "-";
                string v = e.Kind;
                if (e.Kind == "CNST" && e.Kids.Length == 0) v += e.Cat switch
                {
                    MphRecomp.Import.Retro.Part.Cat.Real => $"({e.F:0.####})",
                    MphRecomp.Import.Retro.Part.Cat.Int => $"({e.I})",
                    _ => "",
                };
                if (e.Cat == MphRecomp.Import.Retro.Part.Cat.Bool) v = e.B ? "true" : "false";
                if (e.Id != 0) v += "[" + Ref(e.Id) + "]";
                if (e.Keys != null) v += $"{{{(e.Percent ? "%" : "")}{(e.Loop ? "loop " : "")}{string.Join(",", e.Keys.Take(24).Select(k => k.ToString("0.###")))}{(e.Keys.Length > 24 ? ",..." : "")}}}";
                if (e.Kids.Length > 0 && !(e.Kind == "CNST" && e.Cat is MphRecomp.Import.Retro.Part.Cat.Real or MphRecomp.Import.Retro.Part.Cat.Int))
                    v += "(" + string.Join(", ", e.Kids.Select(Tree)) + ")";
                else if (e.Kids.Length > 0) v += "(" + string.Join(", ", e.Kids.Select(Tree)) + ")";
                return v;
            }
            Dictionary<string, MphRecomp.Import.Retro.Part.Elem?>? props = null;
            switch (type)
            {
                case "PART":
                    var p = MphRecomp.Import.Retro.Part.Load(b, mp3: true);
                    props = p.Props;
                    foreach (var sp in p.Spawns) Console.WriteLine($"  KSSM frame {sp.Frame}: {Ref(sp.Part)}");
                    break;
                case "SWHC": props = MphRecomp.Import.Retro.Part.Swoosh.Load(b, mp3: true).Props; break;
                case "ELSC": props = MphRecomp.Import.Retro.Elsc.Load(b, mp3: true).Props; break;
                case "DGRP":
                    int n = (int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(b);
                    for (int i = 0; i < n; i++)
                    {
                        string t = System.Text.Encoding.ASCII.GetString(b, 4 + i * 12, 4);
                        ulong d = Pak3.U64(b, 8 + i * 12);
                        if (t is "PART" or "ELSC" or "SWHC" or "WPSC" or "CRSC" or "SPSC" or "DPSC" or "CAUD")
                            Console.WriteLine($"  {t} {d:x16} {Nm(d, names)}");
                    }
                    Console.WriteLine($"  ({n} entries)");
                    return;
            }
            if (props != null) foreach ((string tag, var e) in props) Console.WriteLine($"  {tag} {Tree(e)}");
        }

        static IFileSource Open(string path) => Directory.Exists(path) ? new FolderSource(path) : DiscImage.Open(path);

        static void Where(string[] sources)
        {
            var table = new Dictionary<ulong, List<string>>();
            foreach (string path in sources)
            {
                using IFileSource src = Open(path);
                List<string> paks = src.Paths.Where(p => p.EndsWith(".pak", StringComparison.OrdinalIgnoreCase)).OrderBy(p => p, StringComparer.Ordinal).ToList();
                Console.WriteLine($"{path}: {src.GameId} {paks.Count} paks: {string.Join(' ', paks)}");
                using var set = new Pak3Set(paks.Select(p => new Pak3(src.OpenRead(p), p, leaveOpen: false)));
                foreach (ulong id in Roots)
                {
                    string cell;
                    if (!set.Contains(id)) cell = "-";
                    else
                    {
                        string hash = Convert.ToHexString(SHA1.HashData(set.Get(id)))[..10].ToLowerInvariant();
                        cell = $"{set.PakOf(id)!.Name}:{set.TypeOf(id)}:{hash}";
                    }
                    if (!table.TryGetValue(id, out List<string>? row)) table[id] = row = new List<string>();
                    row.Add(cell);
                }
            }
            foreach ((ulong id, List<string> row) in table)
            {
                string same = row.Select(c => c.Split(':').Last()).Distinct().Count() == 1 ? "SAME" : "DIFF";
                Console.WriteLine($"{id:x16} {same} {string.Join("  ", row)}");
            }
        }
    }
}
