using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MphRecomp.Import.Beyond;

namespace MphRead
{
    // -importcheck beyond-anims [--chpr <CHPR.bin>] [--golden <dir>] [--json <chpr.json>|none] [--anim <name>] [--no-bitexact] [-v]
    // The golden check of the Beyond animation decoder (MphRecomp.Import.Beyond, the C# port of
    // brawl_extract/rig_work/gun/mp4anim/py): decodes Samus's CHPR from its bytes ALONE and compares with the Python's
    // exports in brawl_extract/mp4/anims/fps (made by mp4anim_export.py from the CHPR + the Model Dumper's chpr.json):
    //   index.json      every fp* anim: status (ok / fail / error) and message; for ok ones every meta field
    //   <anim>.npz      every ok anim: names, parents, flags exact; per-frame rot / trans / scale (+ deltas) within 1e-5
    //   skeleton.npz    names, parents, flags exact; rest locals + rest world (bind) matrices equal as float32
    // then again with constant pool 0 fed as the Python saw it (the dumper JSON's shortest decimals, parsed as float64):
    // every npz value must then be bit-identical, i.e. the decoder logic is exact and the only differences of the first
    // pass (<= 1 float32 ulp) come from the reference's JSON input, not from the port.
    // --json (default: the chpr.json beside the CHPR, when present): cross-checks every field the Python used to take
    // from the dumper's chpr.json against the C# parse of the bytes. Test-only: the importer never reads it.
    internal static partial class ImportCheck
    {
        static void BeyondAnims(string[] args, string repo)
        {
            string Arg(string k, string d) { int i = Array.IndexOf(args, k); return i > 0 && i + 1 < args.Length ? args[i + 1] : d; }
            string src = Path.Combine(repo, "brawl_extract", "mp4", "anims", "src");
            string chprPath = Arg("--chpr", Path.Combine(src, "7238eaad-21a1-4966-83fb-7fb7d1173bf6.CHPR.bin"));
            string golden = Arg("--golden", Path.Combine(repo, "brawl_extract", "mp4", "anims", "fps"));
            string jsonDefault = Path.Combine(Path.GetDirectoryName(chprPath) ?? ".", Path.GetFileName(chprPath).Replace(".CHPR.bin", ".chpr.json"));
            string jsonPath = Arg("--json", jsonDefault);
            string? only = Arg("--anim", "") is { Length: > 0 } s ? s : null;
            bool verbose = args.Contains("-v");
            if (!File.Exists(chprPath))
            {
                Console.WriteLine($"  beyond-anims: no CHPR at {chprPath}");
                return;
            }
            byte[] bytes = File.ReadAllBytes(chprPath);
            Console.WriteLine($"  importcheck beyond-anims: {chprPath} ({bytes.Length} bytes, SHA-1 {Convert.ToHexString(SHA1.HashData(bytes)).ToLowerInvariant()})");

            // ---- parse (bytes only)
            var sw = Stopwatch.StartNew();
            Chpr chpr = Chpr.Parse(bytes);
            double tParse = sw.Elapsed.TotalMilliseconds;
            sw.Restart();
            ChprCharacter ch = ChprCharacter.Load(chpr);
            double tSkel = sw.Elapsed.TotalMilliseconds;
            ChprSkeleton sk = ch.Skeleton;
            Console.WriteLine($"  parse {tParse:F1} ms, skeleton + names {tSkel:F1} ms");
            Console.WriteLine($"    {chpr.Partitions.Count} partitions; {chpr.Anims.Count} anims (table ends 0x{chpr.AnimTableEnd:x}); name pool: {chpr.Strings.Length} strings, "
                + $"hash lists [{String.Join(", ", chpr.HashLists.Select(h => h.Count))}] (ends 0x{chpr.NamePoolEnd:x})");
            Console.WriteLine($"    anim node set @0x{chpr.AnimNodes.Offset:x}: {chpr.AnimNodes.Ids.Length} nodes, names = hash list {chpr.AnimNodes.NameSet}; "
                + $"abs header @0x{chpr.Abs.Offset:x} ({chpr.Abs.DescriptorCount}, {chpr.Abs.NodeCount}, {chpr.Abs.Num3}, {chpr.Abs.Num4}, {chpr.Abs.Num5}, {chpr.Abs.Num6})");
            Console.WriteLine($"    abs node set @0x{chpr.AbsNodes.Offset:x}: {chpr.AbsNodes.Ids.Length} nodes, names = hash list {chpr.AbsNodes.NameSet}; parents end 0x{chpr.ParentsEnd:x}");
            Console.WriteLine($"    render contexts: {String.Join(", ", chpr.RenderContexts.Select(r => $"#{r.Index} @0x{r.Offset:x} {r.BoneIds.Length} bones"))}; end 0x{chpr.RenderContextsEnd:x}");
            Console.WriteLine($"    constant pools: {String.Join(", ", chpr.ConstPools.Select(p => $"#{p.Index} @0x{p.Offset:x} {p.Values.Length} floats"))}");
            Console.WriteLine($"    rest table @0x{sk.RestTableOffset:x} ({sk.RestTableOffset}): {sk.RestRecords.Count} records = {sk.AnimJointCount} anim joints + "
                + $"{sk.RestRecords.Count - sk.AnimJointCount} abs-only nodes; bind check {sk.BindCheck.Good}/{sk.BindCheck.Total} max {sk.BindCheck.MaxErr:G3}");
                foreach (var (bn, be) in sk.BindMismatches)
                {
                    Console.WriteLine($"      bind mismatch: {bn} (max element error {be:0.####})");
                    int bi = Array.IndexOf(sk.Names, bn);
                    if (Array.IndexOf(args, "-v") >= 0 && bi >= 0)
                    {
                        Console.WriteLine($"        parent {(sk.Parent[bi] >= 0 ? sk.Names[sk.Parent[bi]] : "-")} (corrected {(sk.ParentCorrected[bi] >= 0 ? sk.Names[sk.ParentCorrected[bi]] : "-")}), rest record {sk.Rest.ContainsKey(bn)}");
                        Console.WriteLine($"        rebuilt {String.Join(" ", sk.RestWorldByName[bn].Select(x => x.ToString("0.###")))}");
                        Console.WriteLine($"        stored  {String.Join(" ", sk.BindWorld[bi].Select(x => x.ToString("0.###")))}");
                    }
                }
            var fixedParents = Enumerable.Range(0, sk.Names.Length).Where(i => sk.Parent[i] != sk.ParentCorrected[i])
                .Select(i => $"{sk.Names[i]}: {(sk.Parent[i] < 0 ? "-" : sk.Names[sk.Parent[i]])} -> {(sk.ParentCorrected[i] < 0 ? "-" : sk.Names[sk.ParentCorrected[i]])}").ToList();
            Console.WriteLine($"    NOTE parents read as transform indices (ParentCorrected) differ from the reference decoder's reading for {fixedParents.Count} node(s): {String.Join(", ", fixedParents)}");
            int[] pyNonJoint = { 0, 2, 10, 11, 12, 13 };      // mp4pose.NONJOINT_GROUPS, hard-coded there for this CHPR
            bool njOk = sk.NonJointGroups.SequenceEqual(pyNonJoint);
            Console.WriteLine($"    {(njOk ? "PASS" : "FAIL")} non-joint anim groups derived from the node kinds [{String.Join(",", sk.NonJointGroups)}] vs the Python's constant [{String.Join(",", pyNonJoint)}]");
            int passes = 0, checks = 0;
            void Tally(bool ok) { checks++; if (ok) passes++; }
            Tally(njOk);

            if (jsonPath != "none" && File.Exists(jsonPath))
            {
                foreach (bool ok in CrossCheckJson(chpr, sk, jsonPath)) Tally(ok);
            }
            else
            {
                Console.WriteLine($"    (no chpr.json cross-check: {jsonPath})");
            }

            // ---- index.json
            string indexPath = Path.Combine(golden, "index.json");
            if (!File.Exists(indexPath))
            {
                Console.WriteLine($"  FAIL no golden index at {indexPath}");
                return;
            }
            using JsonDocument index = JsonDocument.Parse(File.ReadAllText(indexPath));
            JsonElement root = index.RootElement;
            bool offOk = root.GetProperty("rest_table_offset").GetInt32() == sk.RestTableOffset;
            JsonElement bc = root.GetProperty("bind_check");
            bool bindOk = bc[0].GetInt32() == sk.BindCheck.Good && bc[1].GetInt32() == sk.BindCheck.Total && sk.BindCheck.MaxErr < 1e-6;
            Console.WriteLine($"  {(offOk ? "PASS" : "FAIL")} rest_table_offset {root.GetProperty("rest_table_offset").GetInt32()} vs {sk.RestTableOffset}");
            Console.WriteLine($"  {(bindOk ? "PASS" : "FAIL")} bind_check [{bc[0]}, {bc[1]}, {bc[2]}] vs [{sk.BindCheck.Good}, {sk.BindCheck.Total}, {sk.BindCheck.MaxErr:R}]");
            Tally(offOk);
            Tally(bindOk);

            // ---- skeleton.npz
            Tally(CompareSkeleton(sk, Path.Combine(golden, "skeleton.npz")));

            // ---- every fp* anim
            var golds = root.GetProperty("anims").EnumerateArray().ToDictionary(e => e.GetProperty("anim_index").GetInt32());
            var mine = Enumerable.Range(0, ch.AnimNames.Length).Where(i => ch.AnimNames[i].StartsWith("fp", StringComparison.Ordinal)).ToList();
            bool setOk = mine.SequenceEqual(golds.Keys.OrderBy(x => x));
            Console.WriteLine($"  {(setOk ? "PASS" : "FAIL")} fp* anims: {mine.Count} in the CHPR, {golds.Count} in index.json");
            Tally(setOk);
            // pass 1: the importer as it ships. pass 2 (reference emulation): constant pool 0 as the Python saw it -- the
            // dumper's chpr.json wrote each float32 as its shortest decimal, which the Python parsed as float64 -- so the
            // decoder's logic must then reproduce every npz value bit for bit.
            double[] exactPool = ch.Pool0;
            Tally(RunAnims("importer", false));
            if (only == null && !args.Contains("--no-bitexact"))
            {
                ch.Pool0 = exactPool.Select(v => Double.IsFinite(v) ? Double.Parse(((float)v).ToString("R", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture) : v).ToArray();
                Tally(RunAnims("reference emulation (pool 0 via the dumper's JSON decimals)", true));
                ch.Pool0 = exactPool;
            }

            bool RunAnims(string label, bool bitExact)
            {
                var counts = new SortedDictionary<string, int>();
                var goldCounts = new SortedDictionary<string, int>();
                int statusSame = 0, npzPass = 0, npzChecked = 0;
                long values = 0, exact = 0;
                double maxd = 0;
                string maxWhere = "";
                var bad = new List<string>();
                double tDecode = 0, tCompare = 0;
                foreach (int i in mine)
                {
                    if (only != null && ch.AnimNames[i] != only) continue;
                    sw.Restart();
                    ChprAnimResult r = ch.Decode(i);
                    tDecode += sw.Elapsed.TotalMilliseconds;
                    JsonElement g = golds.TryGetValue(i, out JsonElement ge) ? ge : default;
                    string gs = g.ValueKind == JsonValueKind.Object ? g.GetProperty("status").GetString() ?? "" : "missing";
                    string gm = g.ValueKind == JsonValueKind.Object && g.TryGetProperty("msg", out JsonElement gmsg) ? gmsg.GetString() ?? "" : "";
                    counts[r.Status] = counts.GetValueOrDefault(r.Status) + 1;
                    goldCounts[gs] = goldCounts.GetValueOrDefault(gs) + 1;
                    bool same = r.Status == gs && (r.Ok || r.Message == gm);
                    var errs = new List<string>();
                    if (!same) errs.Add($"status {r.Status} '{r.Message}' vs {gs} '{gm}'");
                    if (same && r.Ok && r.Animation != null)
                    {
                        ChprAnimation a = r.Animation;
                        if (g.GetProperty("name").GetString() != a.Name) errs.Add("name");
                        if (g.GetProperty("anim_set").GetInt32() != a.Set) errs.Add("anim_set");
                        if (g.GetProperty("nframes").GetInt32() != a.Frames) errs.Add("nframes");
                        if (g.GetProperty("rate_factor").GetDouble() != a.RateFactor) errs.Add("rate_factor");
                        if (g.GetProperty("fps_assumed").GetDouble() != a.FpsAssumed) errs.Add("fps_assumed");
                        if (Math.Abs(g.GetProperty("duration_s_assumed").GetDouble() - a.DurationAssumed) > 1e-12) errs.Add("duration_s_assumed");
                        if (g.GetProperty("joints").GetInt32() != a.Joints.Length) errs.Add("joints");
                        if (g.GetProperty("animated_channels").GetInt32() != a.AnimatedChannels) errs.Add("animated_channels");
                        sw.Restart();
                        npzChecked++;
                        var st = new NpzStats();
                        CompareAnimNpz(a, Path.Combine(golden, a.Name + ".npz"), errs, st);
                        tCompare += sw.Elapsed.TotalMilliseconds;
                        values += st.Values;
                        exact += st.Exact;
                        if (st.MaxD > maxd) { maxd = st.MaxD; maxWhere = $" at {a.Name} {st.MaxWhere}"; }
                        if (bitExact && st.Exact != st.Values) errs.Add($"{st.Values - st.Exact} values not bit-identical");
                        if (errs.Count == 0) npzPass++;
                    }
                    if (errs.Count == 0) statusSame++;
                    else bad.Add($"{ch.AnimNames[i]} (#{i}): {String.Join("; ", errs.Take(6))}");
                    if (verbose || only != null) Console.WriteLine($"    {(errs.Count == 0 ? "PASS" : "FAIL")} #{i,-4} {ch.AnimNames[i],-56} {r.Status,-5} {r.Message}");
                }
                int total = only != null ? statusSame + bad.Count : mine.Count;
                Console.WriteLine($"  [{label}] decode: {String.Join(", ", counts.Select(kv => $"{kv.Value} {kv.Key}"))} (golden {String.Join(", ", goldCounts.Select(kv => $"{kv.Value} {kv.Key}"))}); "
                    + $"{tDecode:F0} ms for {total} anims ({tDecode / Math.Max(1, total):F2} ms each)");
                Console.WriteLine($"  {(bad.Count == 0 ? "PASS" : "FAIL")} [{label}] {statusSame}/{total} anims agree with index.json (status, message, meta); "
                    + $"{npzPass}/{npzChecked} ok anims match their npz {(bitExact ? "bit for bit" : "within 1e-5")} ({values} values, {exact} float32-identical, max |d| {maxd:G3}{maxWhere}); compare {tCompare:F0} ms");
                foreach (string b in bad.Take(20)) Console.WriteLine($"    FAIL {b}");
                if (bad.Count > 20) Console.WriteLine($"    ... {bad.Count - 20} more");
                return bad.Count == 0;
            }
            Console.WriteLine($"  SUMMARY: {passes}/{checks} checks PASS" + (passes == checks ? "" : " -- FAIL"));
        }

        // ---- chpr.json cross-check (every field mp4anim_lib.Character / skel.Skel took from it)

        static IEnumerable<bool> CrossCheckJson(Chpr c, ChprSkeleton sk, string jsonPath)
        {
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(jsonPath));
            JsonElement ci = doc.RootElement.GetProperty("CharacterInfos")[0];
            var res = new List<bool>();
            void Report(bool ok, string what)
            {
                Console.WriteLine($"    {(ok ? "PASS" : "FAIL")} chpr.json {what}");
                res.Add(ok);
            }
            static long[] Longs(JsonElement a) => a.EnumerateArray().Select(x => x.GetInt64()).ToArray();
            JsonElement np = ci.GetProperty("NamePool");
            string[] strs = np.GetProperty("Strings").EnumerateArray().Select(x => x.GetString() ?? "").ToArray();
            Report(strs.SequenceEqual(c.Strings), $"NamePool.Strings ({strs.Length}) = the pool after the animation table");
            JsonElement hl = np.GetProperty("HashLists");
            bool hlOk = hl.GetArrayLength() == c.HashLists.Length;
            for (int k = 0; hlOk && k < c.HashLists.Length; k++)
            {
                hlOk = Longs(hl[k].GetProperty("Hashes")).SequenceEqual(c.HashLists[k].Hashes.Select(x => (long)x))
                    && Longs(hl[k].GetProperty("StringIDs")).SequenceEqual(c.HashLists[k].StringIds.Select(x => (long)x));
            }
            Report(hlOk, $"NamePool.HashLists ({hl.GetArrayLength()}: hashes + string ids)");
            JsonElement an = ci.GetProperty("AnimContext").GetProperty("NodeSet");
            Report(Longs(an.GetProperty("NodeIDs")).SequenceEqual(c.AnimNodes.Ids.Select(x => (long)x)) && an.GetProperty("NameSet").GetInt32() == c.AnimNodes.NameSet,
                $"AnimContext.NodeSet ({c.AnimNodes.Ids.Length} NodeIDs, NameSet {c.AnimNodes.NameSet}) -> anim node names / groups");
            JsonElement ab = ci.GetProperty("AbsContext");
            Report(Longs(ab.GetProperty("NodeSet").GetProperty("NodeIDs")).SequenceEqual(c.AbsNodes.Ids.Select(x => (long)x))
                && ab.GetProperty("NodeSet").GetProperty("NameSet").GetInt32() == c.AbsNodes.NameSet,
                $"AbsContext.NodeSet ({c.AbsNodes.Ids.Length} NodeIDs, NameSet {c.AbsNodes.NameSet}) -> skeleton names");
            var s5 = ab.GetProperty("Section5").EnumerateArray().Select(p => (p[0].GetInt32(), p[1].GetInt32())).ToList();
            Report(s5.SequenceEqual(c.ParentIds.Select((p, i) => ((int)p, (int)c.ParentAux[i]))), $"AbsContext.Section5 ({s5.Count}) -> parents");
            bool numsOk = ab.GetProperty("num1").GetInt32() == c.Abs.DescriptorCount && ab.GetProperty("num2").GetInt32() == c.Abs.NodeCount
                && ab.GetProperty("num3").GetInt32() == c.Abs.Num3 && ab.GetProperty("num4").GetInt32() == c.Abs.Num4
                && ab.GetProperty("num5").GetInt32() == c.Abs.Num5 && ab.GetProperty("num6").GetInt32() == c.Abs.Num6;
            Report(numsOk, "AbsContext num1..num6 = the abs header after the anim node set (not used by the decoder)");
            JsonElement rcs = ci.GetProperty("RenderContexts");
            bool rcOk = rcs.GetArrayLength() == c.RenderContexts.Count;
            for (int k = 0; rcOk && k < c.RenderContexts.Count; k++)
            {
                JsonElement j = rcs[k];
                Chpr.RenderContext rc = c.RenderContexts[k];
                rcOk = Longs(j.GetProperty("SkinnedMatrixBoneIDs")).SequenceEqual(rc.BoneIds.Select(x => (long)x))
                    && Longs(j.GetProperty("section2")).SequenceEqual(rc.Section2.Select(x => (long)x))
                    && j.GetProperty("num6").GetInt32() == rc.Index
                    && j.GetProperty("SkinnedInverseMatrices").GetArrayLength() == rc.InverseMatrices.Length;
                for (int m = 0; rcOk && m < rc.InverseMatrices.Length; m++)
                {
                    float[] jm = j.GetProperty("SkinnedInverseMatrices")[m].EnumerateArray().Select(x => x.GetSingle()).ToArray();
                    rcOk = jm.Length == 16 && jm.Take(12).SequenceEqual(rc.InverseMatrices[m]) && jm[12] == 0 && jm[13] == 0 && jm[14] == 0 && jm[15] == 1;
                }
            }
            Report(rcOk, $"RenderContexts ({rcs.GetArrayLength()}: SkinnedMatrixBoneIDs + SkinnedInverseMatrices, bit-exact) -> skinned flags + bind check");
            JsonElement cps = ci.GetProperty("ConstPools");
            bool cpOk = cps.GetArrayLength() == c.ConstPools.Count;
            for (int k = 0; cpOk && k < c.ConstPools.Count; k++)
            {
                JsonElement p = cps[k];
                float[] jv = p.GetProperty("Unknowns2").EnumerateArray()
                    .Select(x => x.ValueKind == JsonValueKind.String ? Single.Parse(x.GetString()!, CultureInfo.InvariantCulture) : x.GetSingle()).ToArray();
                cpOk = p.GetProperty("Unknown1").GetInt32() == c.ConstPools[k].Index && jv.Length == c.ConstPools[k].Values.Length
                    && jv.Zip(c.ConstPools[k].Values).All(t => BitConverter.SingleToInt32Bits(t.First) == BitConverter.SingleToInt32Bits(t.Second) || (Single.IsNaN(t.First) && Single.IsNaN(t.Second)));
            }
            Report(cpOk, $"ConstPools ({cps.GetArrayLength()}: {String.Join(" + ", c.ConstPools.Select(p => p.Values.Length))} floats, bit-exact) -> constant channels");
            Report(ci.GetProperty("NumAnims").GetInt32() == c.Anims.Count, $"NumAnims {ci.GetProperty("NumAnims").GetInt32()} = the animation table's {c.Anims.Count}");
            // the python's derived tables, rebuilt from the JSON the way mp4anim_lib does, vs the C# skeleton
            var skBones = ci.GetProperty("SkinnedBones").EnumerateArray().Select(x => x.GetProperty("NodeIDX").GetInt32()).OrderBy(x => x).ToList();
            Report(skBones.SequenceEqual(sk.BindWorld.Keys.OrderBy(x => x)), $"SkinnedBones ({skBones.Count} node indices) = the render contexts' bone ids");
            return res;
        }

        // ---- npy / npz (test code: numpy's format, just the dtypes the Python exporter writes)

        sealed class Npy
        {
            public string Descr = "";
            public int[] Shape = Array.Empty<int>();
            public double[] Num = Array.Empty<double>();     // f4 / f8 / i4 / i8 / b1 as doubles
            public string[] Str = Array.Empty<string>();
            public int Count => Shape.Aggregate(1, (a, b) => a * b);
        }

        static Npy ReadNpy(Stream s)
        {
            using var m = new MemoryStream();
            s.CopyTo(m);
            byte[] d = m.ToArray();
            if (d.Length < 10 || d[0] != 0x93 || Encoding.ASCII.GetString(d, 1, 5) != "NUMPY") throw new InvalidDataException("not an .npy");
            int major = d[6];
            int hlen = major == 1 ? BitConverter.ToUInt16(d, 8) : BitConverter.ToInt32(d, 8);
            int ho = major == 1 ? 10 : 12;
            string header = Encoding.UTF8.GetString(d, ho, hlen);
            int o = ho + hlen;
            var npy = new Npy
            {
                Descr = Regex.Match(header, @"'descr':\s*'([^']*)'").Groups[1].Value,
                Shape = Regex.Match(header, @"'shape':\s*\(([^)]*)\)").Groups[1].Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(Int32.Parse).ToArray(),
            };
            if (Regex.IsMatch(header, @"'fortran_order':\s*True")) throw new InvalidDataException("fortran order .npy");
            int n = npy.Count;
            switch (npy.Descr)
            {
            case "<f4": npy.Num = Enumerable.Range(0, n).Select(i => (double)BitConverter.ToSingle(d, o + 4 * i)).ToArray(); break;
            case "<f8": npy.Num = Enumerable.Range(0, n).Select(i => BitConverter.ToDouble(d, o + 8 * i)).ToArray(); break;
            case "<i4": npy.Num = Enumerable.Range(0, n).Select(i => (double)BitConverter.ToInt32(d, o + 4 * i)).ToArray(); break;
            case "<i8": npy.Num = Enumerable.Range(0, n).Select(i => (double)BitConverter.ToInt64(d, o + 8 * i)).ToArray(); break;
            case "|b1": npy.Num = Enumerable.Range(0, n).Select(i => d[o + i] != 0 ? 1.0 : 0.0).ToArray(); break;
            default:
                if (!npy.Descr.StartsWith("<U")) throw new InvalidDataException($"dtype {npy.Descr}");
                int w = Int32.Parse(npy.Descr.Substring(2));
                npy.Str = Enumerable.Range(0, n).Select(i => Encoding.UTF32.GetString(d, o + 4 * w * i, 4 * w).TrimEnd('\0')).ToArray();
                break;
            }
            return npy;
        }

        static Dictionary<string, Npy> ReadNpz(string path)
        {
            using ZipArchive z = ZipFile.OpenRead(path);
            var r = new Dictionary<string, Npy>();
            foreach (ZipArchiveEntry e in z.Entries)
            {
                using Stream s = e.Open();
                r[Path.GetFileNameWithoutExtension(e.FullName)] = ReadNpy(s);
            }
            return r;
        }

        sealed class NpzStats
        {
            public long Values, Exact;
            public double MaxD;
            public string MaxWhere = "";
        }

        static void CompareAnimNpz(ChprAnimation a, string path, List<string> errs, NpzStats st)
        {
            if (!File.Exists(path)) { errs.Add("no npz"); return; }
            Dictionary<string, Npy> z = ReadNpz(path);
            Npy Get(string k) => z.TryGetValue(k, out Npy? v) ? v : throw new InvalidDataException($"npz lacks {k}");
            void Scalar(string k, double v) { if (Get(k).Num[0] != v) errs.Add($"{k} {Get(k).Num[0]} vs {v}"); }
            void Strs(string k, string[] v) { if (!Get(k).Str.SequenceEqual(v)) errs.Add($"{k} differ"); }
            if (Get("name").Str[0] != a.Name) errs.Add("npz name");
            Scalar("anim_index", a.Index);
            Scalar("anim_set", a.Set);
            Scalar("nframes", a.Frames);
            Scalar("rate_factor", a.RateFactor);
            Scalar("fps_assumed", a.FpsAssumed);
            Strs("joints", a.Joints);
            Strs("parents", a.Parents);
            Strs("vis_names", a.VisNames);
            if (!Get("vis_values").Num.SequenceEqual(a.VisValues.Select(x => (double)x))) errs.Add("vis_values differ");
            if (!Get("animated").Num.SequenceEqual(a.Animated.Select(x => x ? 1.0 : 0.0))) errs.Add("animated differ");
            void Floats(string k, float[] mine, int[] shape)
            {
                Npy g = Get(k);
                if (!g.Shape.SequenceEqual(shape)) { errs.Add($"{k} shape ({String.Join(",", g.Shape)}) vs ({String.Join(",", shape)})"); return; }
                int bad = 0;
                for (int i = 0; i < mine.Length; i++)
                {
                    float gv = (float)g.Num[i];
                    st.Values++;
                    if (BitConverter.SingleToInt32Bits(gv) == BitConverter.SingleToInt32Bits(mine[i])) { st.Exact++; continue; }
                    double dd = Math.Abs((double)gv - mine[i]);
                    if (Double.IsNaN(dd)) dd = Double.PositiveInfinity;
                    if (dd > st.MaxD) { st.MaxD = dd; st.MaxWhere = $"{k}[{i}]"; }
                    if (!(dd <= 1e-5)) bad++;
                }
                if (bad > 0) errs.Add($"{k}: {bad} values off by > 1e-5");
            }
            int f = a.Frames, j = a.Joints.Length;
            Floats("rest_rot_xyzw", a.RestRotXyzw, new[] { j, 4 });
            Floats("rest_trans", a.RestTrans, new[] { j, 3 });
            Floats("rot_xyzw", a.RotXyzw, new[] { f, j, 4 });
            Floats("trans", a.Trans, new[] { f, j, 3 });
            Floats("scale", a.Scale, new[] { f, j, 3 });
            Floats("delta_rot_xyzw", a.DeltaRotXyzw, new[] { f, j, 4 });
            Floats("delta_trans", a.DeltaTrans, new[] { f, j, 3 });
            Floats("delta_scale", a.DeltaScale, new[] { f, j, 3 });
        }

        static bool CompareSkeleton(ChprSkeleton sk, string path)
        {
            if (!File.Exists(path))
            {
                Console.WriteLine($"  FAIL no {path}");
                return false;
            }
            Dictionary<string, Npy> z = ReadNpz(path);
            var errs = new List<string>();
            int n = sk.Names.Length;
            if (!z["names"].Str.SequenceEqual(sk.Names)) errs.Add("names");
            if (!z["parent"].Num.SequenceEqual(sk.Parent.Select(x => (double)x))) errs.Add("parent");
            if (!z["has_rest_record"].Num.SequenceEqual(sk.Names.Select(x => sk.HasRestRecord(x) ? 1.0 : 0.0))) errs.Add("has_rest_record");
            if (!z["skinned"].Num.SequenceEqual(Enumerable.Range(0, n).Select(i => sk.Skinned(i) ? 1.0 : 0.0))) errs.Add("skinned");
            long vals = 0, f32same = 0, bitsame = 0;
            double maxd = 0;
            void Cmp(string k, Func<int, double[]> mine)
            {
                Npy g = z[k];
                int w = g.Count / n;
                for (int i = 0; i < n; i++)
                {
                    double[] m = mine(i);
                    if (m.Length != w) { errs.Add($"{k} width"); return; }
                    for (int c = 0; c < w; c++)
                    {
                        double gv = g.Num[i * w + c];
                        vals++;
                        if (gv == m[c]) bitsame++;
                        if ((float)gv == (float)m[c]) f32same++;
                        maxd = Math.Max(maxd, Math.Abs(gv - m[c]));
                    }
                }
            }
            Cmp("rest_rot_xyzw", i => { var q = sk.RestLocal(sk.Names[i]).Q; return new[] { q[1], q[2], q[3], q[0] }; });
            Cmp("rest_trans", i => sk.RestLocal(sk.Names[i]).T);
            Cmp("rest_world", i => sk.RestWorld[i]);
            if (f32same != vals) errs.Add($"{vals - f32same} values differ as float32");
            bool ok = errs.Count == 0;
            Console.WriteLine($"  {(ok ? "PASS" : "FAIL")} skeleton.npz: {n} nodes, names/parents/has_rest_record/skinned equal; {vals} rest values, {f32same} equal as float32, "
                + $"{bitsame} bit-identical as float64, max |d| {maxd:G3}" + (ok ? "" : "; " + String.Join("; ", errs)));
            return ok;
        }
    }
}
