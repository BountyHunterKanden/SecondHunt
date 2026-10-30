using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using MphRecomp.Campaign;
using MphRecomp.Multiplayer;
using MphRecomp.Replay;

namespace MphRead
{
    // Thread B (instant seek, step 1): portable keyframes (Core Replay/ReplayKeyframe.cs) must load EXACTLY in another
    // process that started the same replay, or a shadow process couldn't prepare seeks for the viewer.
    //   -replaykeyframetest [<file>|<golden name>|all] [every=60] [save=1800]
    //       plays the replay to its end, writing a keyframe (whole + a delta from the previous one) every `every` steps
    //       (sizes raw + Brotli, write time); saves whole ones at step `every` and every `save` steps, plus a chain of
    //       deltas after the first; then a fresh process starts the same replay, loads each saved keyframe (shuffled) and
    //       plays on to the next one (the last to the end) checking every step, and loads the first + its deltas and plays
    //       on. Exit code 0 = every load exact.
    internal static partial class ReplayTest
    {
        public static void KeyframeTest(string[] args)
        {
            string? child = args.FirstOrDefault(a => a.StartsWith("child="))?[6..];
            string target = args.Length > 1 && !args[1].Contains('=') ? args[1] : "all";
            int every = int.TryParse(args.FirstOrDefault(a => a.StartsWith("every="))?[6..], out int e) ? e : 60;
            int save = int.TryParse(args.FirstOrDefault(a => a.StartsWith("save="))?[5..], out int s) ? s : 1800;
            if (!Setup())
            {
                return;
            }
            var files = new List<string>();
            if (target == "all")
            {
                files.AddRange(Goldens.Select(g => Path.Combine(GoldenDir(), g.Name + ReplayLibrary.Extension)));
            }
            else if (File.Exists(target))
            {
                files.Add(target);
            }
            else
            {
                // (a,b,...: several goldens in one process, in that order)
                files.AddRange(target.Split(',').Select(t => Path.Combine(GoldenDir(), t + ReplayLibrary.Extension)));
            }
            if (args.Contains("time=1"))
            {
                // the Odin's timing test (Core ReplayKeyframeTiming), here for the PC's numbers and a check of the code
                foreach (string path in files)
                {
                    string? r = ReplayKeyframeTiming.Run(ReplayFile.Load(path), every, 6, Path.GetFileNameWithoutExtension(path), Console.WriteLine);
                    Console.WriteLine(r == null ? "TIMING OK" : "TIMING FAILED: " + r);
                }
                return;
            }
            if (child != null)
            {
                string? r = KeyframeChild(ReplayFile.Load(files[0]), child);
                Console.WriteLine(r == null ? "CHILD PASS" : "CHILD FAIL: " + r);
                Environment.ExitCode = r == null ? 0 : 1;
                return;
            }
            int pass = 0, fail = 0;
            foreach (string path in files)
            {
                ReplayFile file = ReplayFile.Load(path);
                if (MatchArenas.Find(file.Settings.Arena) == null)
                {
                    Console.WriteLine($"SKIP   {Path.GetFileNameWithoutExtension(path)}: arena not installed");
                    continue;
                }
                string? result = KeyframeOne(path, file, every, save);
                Console.WriteLine($"{(result == null ? "PASS" : "FAIL")}   {Path.GetFileNameWithoutExtension(path)}"
                    + (result != null ? ": " + result : ""));
                if (result == null) pass++; else fail++;
            }
            Console.WriteLine($"{(fail == 0 ? "PASS" : "FAIL")}: {pass}/{pass + fail} replays load exactly in another process");
            Environment.ExitCode = fail == 0 ? 0 : 1;
        }

        private static byte[] Brotli(byte[] data, CompressionLevel level)
        {
            var o = new MemoryStream();
            using (var b = new BrotliStream(o, level, leaveOpen: true)) b.Write(data);
            return o.ToArray();
        }

        private static string? KeyframeOne(string path, ReplayFile file, int every, int save)
        {
            string? why = ReplayPlayer.CheckBeforeStart(file);
            if (why != null) return "refused: " + why;
            string dir = Path.Combine(Path.GetTempPath(), "mph-keyframes", Path.GetFileNameWithoutExtension(path));
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
            Directory.CreateDirectory(dir);
            var player = new ReplayPlayer(file);
            CampaignHost host = player.Start(collectDrawItems: false, viewWidth: 256, viewHeight: 192);
            try
            {
                if (player.CheckAfterStart() is string after) return "refused: " + after;
                ReplayCheckpoints.SetUpOnce();
                var kbase = ReplayCheckpoints.KeyframeBase(host);
                if (Environment.GetEnvironmentVariable("KF_DUMP") != null) File.WriteAllLines(Path.Combine(dir, "base_parent.txt"), kbase.Describe());
                var writer = new SimSnapshot.KeyframeWriter(kbase);
                var full = new List<(int Raw, int Fast, int Opt)>();
                var delta = new List<(int Raw, int Fast, int Opt)>();
                var writeMs = new List<double>();
                var deltaMs = new List<double>();
                var fastMs = new List<double>();
                int maxDeclared = 0, maxExtras = 0, maxDelegates = 0;
                SimSnapshot prev = kbase.Snap;
                int firstSaved = -1, chainEnd = -1;
                while (!host.Ended && player.TryNext(out CampaignInput input))
                {
                    host.Step(input);
                    if (!player.Check(host.Scene)) return $"straight run differs at step {player.FailedAt}";
                    int pos = player.Position;
                    if (pos % every != 0) continue;
                    SimSnapshot snap = SimSnapshot.Take(pos, ReplayCheckpoints.Roots(host), prev);
                    var sw = Stopwatch.StartNew();
                    byte[] f = writer.Write(snap);
                    writeMs.Add(sw.Elapsed.TotalMilliseconds);
                    maxDeclared = Math.Max(maxDeclared, writer.LastDeclared);
                    maxExtras = Math.Max(maxExtras, writer.LastExtras);
                    maxDelegates = Math.Max(maxDelegates, writer.LastNewDelegates);
                    sw.Restart();
                    byte[] d = writer.Write(snap, prev);
                    deltaMs.Add(sw.Elapsed.TotalMilliseconds);
                    sw.Restart();
                    byte[] ff = Brotli(f, CompressionLevel.Fastest);
                    fastMs.Add(sw.Elapsed.TotalMilliseconds);
                    full.Add((f.Length, ff.Length, Brotli(f, CompressionLevel.Optimal).Length));
                    delta.Add((d.Length, Brotli(d, CompressionLevel.Fastest).Length, Brotli(d, CompressionLevel.Optimal).Length));
                    if (pos == every || pos % save == 0)
                    {
                        File.WriteAllBytes(Path.Combine(dir, $"full_{pos}.kf"), f);
                        if (firstSaved < 0)
                        {
                            firstSaved = pos;
                            chainEnd = pos + save / 3;
                        }
                    }
                    if (firstSaved >= 0 && pos > firstSaved && pos <= chainEnd)
                    {
                        File.WriteAllBytes(Path.Combine(dir, $"delta_{prev.Step}_{pos}.kf"), d);
                    }
                    prev = snap;
                }
                if (writer.Refused.Count > 0) return "refused types: " + string.Join(", ", writer.Refused);
                string K(double b) => $"{b / 1024:0}";
                Console.WriteLine($"       {file.Settings.Mode} in {file.Settings.Arena}: {player.Position} steps; base {kbase.ObjectCount} objects, "
                    + $"{kbase.LeafCount} leaves; new objects up to {maxDeclared}, extras {maxExtras}, new delegates {maxDelegates}");
                Console.WriteLine($"       whole KB raw/fast/opt avg {K(full.Average(x => x.Raw))}/{K(full.Average(x => x.Fast))}/{K(full.Average(x => x.Opt))}"
                    + $" max {K(full.Max(x => x.Raw))}/{K(full.Max(x => x.Fast))}/{K(full.Max(x => x.Opt))}; write {writeMs.Average():0.0} ms avg "
                    + $"{writeMs.Max():0.0} max, brotli fast {fastMs.Average():0.0} ms");
                Console.WriteLine($"       delta({every}) KB raw/fast/opt avg {K(delta.Average(x => x.Raw))}/{K(delta.Average(x => x.Fast))}/{K(delta.Average(x => x.Opt))}"
                    + $" max {K(delta.Max(x => x.Raw))}/{K(delta.Max(x => x.Fast))}/{K(delta.Max(x => x.Opt))}; write {deltaMs.Average():0.0} ms avg; "
                    + $"per match: whole/s fast {K(full.Sum(x => x.Fast) / 1024.0)} MB, deltas fast {K(delta.Sum(x => x.Fast) / 1024.0)} MB");
            }
            finally
            {
                host.Dispose();
            }
            (int code, string output) = RunChild("-replaykeyframetest", path, "child=" + dir);
            foreach (string line in output.Split('\n').Where(l => l.TrimStart().StartsWith("child", StringComparison.OrdinalIgnoreCase)
                || l.Contains("CHILD")))
            {
                Console.WriteLine("       " + line.TrimEnd());
            }
            return code == 0 ? null : "the other process: " + output.Split('\n').LastOrDefault(l => l.Contains("CHILD"))?.Trim();
        }

        private static string? KeyframeChild(ReplayFile file, string dir)
        {
            string? why = ReplayPlayer.CheckBeforeStart(file);
            if (why != null) return "refused: " + why;
            var player = new ReplayPlayer(file);
            CampaignHost host = player.Start(collectDrawItems: false, viewWidth: 256, viewHeight: 192);
            try
            {
                if (player.CheckAfterStart() is string after) return "refused: " + after;
                ReplayCheckpoints.SetUpOnce();
                var kbase = ReplayCheckpoints.KeyframeBase(host);
                if (Environment.GetEnvironmentVariable("KF_DUMP") != null) File.WriteAllLines(Path.Combine(dir, "base_child.txt"), kbase.Describe());
                var loader = new SimSnapshot.KeyframeLoader(kbase);
                List<int> fulls = Directory.GetFiles(dir, "full_*.kf").Select(p => int.Parse(Path.GetFileNameWithoutExtension(p)[5..]))
                    .OrderBy(x => x).ToList();
                if (fulls.Count == 0) return "no keyframes";
                int end = file.Steps.Count;
                var rng = new Random(1);
                var loadMs = new List<double>();
                foreach (int at in fulls.OrderBy(_ => rng.Next()))
                {
                    byte[] data = File.ReadAllBytes(Path.Combine(dir, $"full_{at}.kf"));
                    var sw = Stopwatch.StartNew();
                    string? r = loader.Load(data);
                    loadMs.Add(sw.Elapsed.TotalMilliseconds);
                    if (r != null) return $"keyframe {at} refused: {r}";
                    player.Rewind(at);
                    int next = fulls.FirstOrDefault(x => x > at);
                    int until = next > 0 ? next : int.MaxValue;
                    while (player.Position < until && !host.Ended && player.TryNext(out CampaignInput input))
                    {
                        host.Step(input);
                        if (!player.Check(host.Scene)) return $"loaded step {at}, differs at step {player.FailedAt}";
                    }
                }
                Console.WriteLine($"child: {fulls.Count} whole keyframes loaded ({loadMs.Average():0.0} ms avg, {loadMs.Max():0.0} max, "
                    + $"first {loadMs[0]:0.0}), every step after exact to the end ({player.Position} of {end})");
                // the first + its chain of deltas, then on
                var chain = Directory.GetFiles(dir, "delta_*.kf").Select(p => Path.GetFileNameWithoutExtension(p).Split('_'))
                    .Select(x => (From: int.Parse(x[1]), To: int.Parse(x[2]))).OrderBy(x => x.From).ToList();
                if (chain.Count > 0)
                {
                    string? r = loader.Load(File.ReadAllBytes(Path.Combine(dir, $"full_{fulls[0]}.kf")));
                    if (r != null) return "chain start refused: " + r;
                    var dms = new List<double>();
                    foreach ((int from, int to) in chain)
                    {
                        byte[] data = File.ReadAllBytes(Path.Combine(dir, $"delta_{from}_{to}.kf"));
                        var sw = Stopwatch.StartNew();
                        r = loader.Load(data);
                        dms.Add(sw.Elapsed.TotalMilliseconds);
                        if (r != null) return $"delta {from}->{to} refused: {r}";
                    }
                    int at = chain[^1].To;
                    player.Rewind(at);
                    int until = Math.Min(end, at + 1800);
                    while (player.Position < until && !host.Ended && player.TryNext(out CampaignInput input))
                    {
                        host.Step(input);
                        if (!player.Check(host.Scene)) return $"after deltas to step {at}, differs at step {player.FailedAt}";
                    }
                    Console.WriteLine($"child: whole {fulls[0]} + {chain.Count} deltas to {at} ({dms.Average():0.0} ms avg per delta), "
                        + $"exact for {player.Position - at} steps after");
                }
                return null;
            }
            finally
            {
                host.Dispose();
            }
        }
    }
}
