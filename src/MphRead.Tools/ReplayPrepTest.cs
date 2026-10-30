using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using MphRecomp.Campaign;
using MphRecomp.Multiplayer;
using MphRecomp.Replay;

namespace MphRead
{
    // Thread B (instant seek, steps 2-4): a replay prepared by another process (Core ReplayPrep) seeks exactly in a viewer.
    //   -replaypreptest [<file>|<golden name>[,..]|all] [seeks=16]
    //       a child process prepares the sidecar (ReplayPrep.Run, as the Android / desktop shadow does); then this process
    //       starts the replay as the viewer does (draw items on, a 1280x720 view), and seeks to random steps: the nearest
    //       prepared keyframe (whole anchor + its delta), then plays to the target and 120 steps past, checking every step.
    //       Prints the sidecar's size, delta size by distance from its anchor, and load / seek times. Exit 0 = all exact.
    //   -replaypreptest ... live   the same, but the replay is fed to the child as a live match's would be (ReplayFeed:
    //       its first KeySteps, then about 600 steps a second, the end mark last) and the child follows the feed
    //   -replaypreptest <file> prep=<sidecar>   (the child)
    internal static partial class ReplayTest
    {
        public static void PrepTest(string[] args)
        {
            string target = args.Length > 1 && !args[1].Contains('=') ? args[1] : "all";
            string? prep = args.FirstOrDefault(a => a.StartsWith("prep="))?[5..];
            bool live = args.Contains("live");
            int seeks = int.TryParse(args.FirstOrDefault(a => a.StartsWith("seeks="))?[6..], out int n) ? n : 16;
            if (!Setup()) return;
            var files = new List<string>();
            if (target == "all") files.AddRange(Goldens.Select(g => Path.Combine(GoldenDir(), g.Name + ReplayLibrary.Extension)));
            else if (File.Exists(target)) files.Add(target);
            else files.AddRange(target.Split(',').Select(t => Path.Combine(GoldenDir(), t + ReplayLibrary.Extension)));
            if (prep != null)
            {
                // (diagnostics) warm=<replay>: prepare another replay in this process first, as a reused Android prepare process does
                if (args.FirstOrDefault(a => a.StartsWith("warm="))?[5..] is string warm)
                {
                    string warmSidecar = Path.Combine(Path.GetTempPath(), "mph-prep", "warm" + ReplayPrep.Extension);
                    File.Delete(warmSidecar);
                    Console.WriteLine("child: warm-up " + ReplayPrep.Run(ReplayFile.Load(warm), warmSidecar, log: l => { }));
                }
                var sw = Stopwatch.StartNew();
                ReplayFile child = ReplayFile.Load(files[0]);
                ReplayFeed.Follower? feed = ReplayFeed.IsLive(files[0]) ? ReplayFeed.Follow(child, files[0]) : null;
                if (feed != null) Console.WriteLine($"child: following the feed from step {child.Steps.Count}");
                string? r = ReplayPrep.Run(child, prep, more: feed == null ? null : feed.More, log: l => Console.WriteLine("child: " + l));
                feed?.Close(drain: r == null);
                Console.WriteLine(r == null ? $"PREP DONE in {sw.Elapsed.TotalSeconds:0.0} s" : "PREP FAILED: " + r);
                Environment.ExitCode = r == null ? 0 : 1;
                return;
            }
            int pass = 0, fail = 0;
            foreach (string path in files)
            {
                ReplayFile file = ReplayFile.Load(path);
                string name = Path.GetFileNameWithoutExtension(path);
                if (MatchArenas.Find(file.Settings.Arena) == null)
                {
                    Console.WriteLine($"SKIP   {name}: arena not installed");
                    continue;
                }
                if (!ReplayPrep.Worth(file))
                {
                    Console.WriteLine($"SKIP   {name}: {file.Steps.Count} steps (too short to prepare)");
                    continue;
                }
                string? result = PrepOne(path, file, seeks, live);
                Console.WriteLine($"{(result == null ? "PASS" : "FAIL")}   {name}" + (result != null ? ": " + result : ""));
                if (result == null) pass++; else fail++;
            }
            Console.WriteLine($"{(fail == 0 ? "PASS" : "FAIL")}: {pass}/{pass + fail} prepared replays seek exactly");
            Environment.ExitCode = fail == 0 ? 0 : 1;
        }

        private static string? PrepOne(string path, ReplayFile file, int seeks, bool live)
        {
            string dir = Path.Combine(Path.GetTempPath(), "mph-prep");
            Directory.CreateDirectory(dir);
            string sidecar = Path.Combine(dir, ReplayPrep.Key(file) + ReplayPrep.Extension);
            File.Delete(sidecar);
            int code;
            string output;
            if (live)
            {
                // the recording as a match makes it: the steps one by one, the feed told after each
                string data = Path.Combine(dir, "live-data");
                ReplayFile rec = ReplayFile.Load(path);
                List<ReplayStep> all = rec.Steps.ToList();
                rec.Steps.Clear();
                var feed = new ReplayFeed(rec, data);
                System.Threading.Tasks.Task<(int, string)>? child = null;
                var sw = Stopwatch.StartNew();
                for (int i = 0; i < all.Count; i++)
                {
                    rec.Steps.Add(all[i]);
                    if (feed.Step())
                    {
                        if (feed.Sidecar != ReplayPrep.SidecarPath(data, file)) return "the feed's key isn't the saved replay's";
                        File.Delete(feed.Sidecar!);
                        sidecar = feed.Sidecar!;
                        string fed = feed.ReplayPath!;
                        child = System.Threading.Tasks.Task.Run(() => RunChild("-replaypreptest", fed, "prep=" + sidecar));
                    }
                    if (i % 60 == 0) System.Threading.Thread.Sleep(100);
                }
                feed.End();
                if (child == null) return "the feed never began";
                (code, output) = child.Result;
                Console.WriteLine($"       fed {all.Count} steps in {sw.Elapsed.TotalSeconds:0.0} s; the child done with them");
                if (ReplayFeed.IsLive(feed.ReplayPath!)) return "the feed's files weren't cleaned up";
            }
            else
            {
                (code, output) = RunChild("-replaypreptest", path, "prep=" + sidecar);
            }
            foreach (string line in output.Split('\n').Where(l => l.StartsWith("child") || l.Contains("PREP")))
            {
                Console.WriteLine("       " + line.TrimEnd());
            }
            if (code != 0) return "the preparing process failed";
            using (KeyframeSidecar? s = KeyframeSidecar.Open(sidecar))
            {
                if (s == null || !s.Complete) return "no complete sidecar";
                if (!s.Fits(file)) return $"the sidecar doesn't fit the saved replay ({s.EndSteps} steps, the replay {file.Steps.Count})";
                // another match with the same key (the same start, parting later) mustn't take it
                ReplayFile other = ReplayFile.Load(path);
                other.Steps.RemoveAt(other.Steps.Count - 1);
                if (ReplayPrep.Key(other) != ReplayPrep.Key(file) || s.Fits(other)) return "a match with the same key fits the sidecar";
                var recs = s.Records.ToList();
                double minutes = file.Steps.Count / 3600.0;
                Console.WriteLine($"       sidecar {s.Length / 1024} KB ({s.Length / 1024 / minutes:0} KB a minute), {recs.Count} keyframes");
                var byDistance = recs.Where(r => r.Anchor >= 0).GroupBy(r => r.Step - r.Anchor).OrderBy(g => g.Key)
                    .Select(g => $"{g.Key}:{g.Average(r => r.Length) / 1024:0}");
                Console.WriteLine($"       whole KB avg {recs.Where(r => r.Anchor < 0).Average(r => r.Length) / 1024:0}; delta KB by distance "
                    + string.Join(" ", byDistance));
            }

            var player = new ReplayPlayer(file);
            CampaignHost host = player.Start(collectDrawItems: true, viewWidth: 1280, viewHeight: 720);
            try
            {
                if (player.CheckAfterStart() is string after) return "refused: " + after;
                using var kf = new ReplayKeyframes(host, sidecar, file);
                var rng = new Random(7);
                int end = file.Steps.Count;
                var loadMs = new List<double>();
                var seekMs = new List<double>();
                var stepsRun = new List<int>();
                for (int i = 0; i < seeks; i++)
                {
                    int to = i == 0 ? end - 30 : rng.Next(ReplayPrep.Every, end - 1);
                    var sw = Stopwatch.StartNew();
                    int at = kf.Before(to);
                    if (at < 0) return $"no keyframe before {to}";
                    if (!kf.Load(at)) return $"keyframe {at} refused: {kf.LastRefusal}";
                    loadMs.Add(kf.LastLoadMs);
                    player.Rewind(at);
                    while (player.Position < to && !host.Ended && player.TryNext(out CampaignInput input))
                    {
                        host.Step(input);
                        if (!player.Check(host.Scene)) return $"seek to {to} (keyframe {at}) differs at step {player.FailedAt}";
                    }
                    seekMs.Add(sw.Elapsed.TotalMilliseconds);
                    stepsRun.Add(to - at);
                    int until = Math.Min(end, to + 120);
                    while (player.Position < until && !host.Ended && player.TryNext(out CampaignInput input))
                    {
                        host.Step(input);
                        if (!player.Check(host.Scene)) return $"after seek to {to} (keyframe {at}) differs at step {player.FailedAt}";
                    }
                }
                Console.WriteLine($"       {seeks} seeks: load ms first {loadMs[0]:0.0} avg {loadMs.Average():0.0} max {loadMs.Max():0.0}; "
                    + $"seek ms avg {seekMs.Average():0.0} max {seekMs.Max():0.0}; steps played avg {stepsRun.Average():0} max {stepsRun.Max()}");
                return null;
            }
            finally
            {
                host.Dispose();
            }
        }
    }
}
