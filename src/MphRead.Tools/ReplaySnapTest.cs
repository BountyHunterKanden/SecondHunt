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
    // T9 (instant seek back): the replay checkpoints (Core Replay/ReplaySnapshot.cs, ReplayCheckpoints.cs) must put a match
    // back EXACTLY, or seeking back would stop a replay part-way.
    //   -replaysnaptest [<file>|<golden name>|all] [every=300] [census] [view=nodraw|draw]
    //       plays the replay straight to its end, taking a checkpoint every `every` steps (time + size reported); then
    //       restores every checkpoint in a shuffled order and plays on from each, checking every step against the
    //       recording: `every` x 2 steps from each, and from the first one to the very end. Exit code 0 = all exact.
    //       census: the biggest types a checkpoint holds (for tuning what's left out).
    internal static partial class ReplayTest
    {
        public static void SnapTest(string[] args)
        {
            string target = args.Length > 1 && !args[1].Contains('=') && args[1] != "census" ? args[1] : "all";
            int every = int.TryParse(args.FirstOrDefault(a => a.StartsWith("every="))?[6..], out int e) ? e : ReplayCheckpoints.DefaultEvery;
            bool census = args.Contains("census");
            bool draw = args.Contains("view=draw");
            SimSnapshot.TrackOwners = census;
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
                files.Add(Path.Combine(GoldenDir(), target + ReplayLibrary.Extension));
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
                string? result = SnapOne(file, every, census, draw);
                Console.WriteLine($"{(result == null ? "PASS" : "FAIL")}   {Path.GetFileNameWithoutExtension(path)}"
                    + (result != null ? ": " + result : ""));
                if (result == null) pass++; else fail++;
            }
            Console.WriteLine($"{(fail == 0 ? "PASS" : "FAIL")}: {pass}/{pass + fail} replays restore exactly from every checkpoint");
            Environment.ExitCode = fail == 0 ? 0 : 1;
        }

        private static string? SnapOne(ReplayFile file, int every, bool census, bool draw)
        {
            string? why = ReplayPlayer.CheckBeforeStart(file);
            if (why != null) return "refused: " + why;
            var player = new ReplayPlayer(file);
            CampaignHost host = player.Start(collectDrawItems: draw, viewWidth: 256, viewHeight: 192);
            try
            {
                if (player.CheckAfterStart() is string after) return "refused: " + after;
                var cps = new ReplayCheckpoints(every);
                var takeMs = new List<double>();
                cps.AfterStep(0, host);
                takeMs.Add(cps.LastTakeMs);
                var straight = Stopwatch.StartNew();
                int steps = 0;
                while (!host.Ended && player.TryNext(out CampaignInput input))
                {
                    host.Step(input);
                    if (!player.Check(host.Scene)) return $"straight run differs at step {player.FailedAt}";
                    int before = cps.Count;
                    cps.AfterStep(player.Position, host);
                    if (cps.Count > before) takeMs.Add(cps.LastTakeMs);
                    steps++;
                }
                double runMs = straight.Elapsed.TotalMilliseconds - takeMs.Skip(1).Sum();
                int end = player.Position;
                SimSnapshot first = cps.Before(0)!;
                Console.WriteLine($"       {file.Settings.Mode} in {file.Settings.Arena}: {end} steps ({runMs / Math.Max(1, steps):0.00} ms/step); "
                    + $"{cps.Count} checkpoints, {first.ObjectCount} objects, first {first.Bytes / 1024} KB, all {cps.Bytes / 1024} KB; "
                    + $"take {takeMs.Average():0.0} ms avg, {takeMs.Max():0.0} max");
                if (census)
                {
                    foreach ((string type, int count, long bytes) in first.Census().Take(40))
                    {
                        Console.WriteLine($"         {bytes / 1024,8} KB {count,7}  {type}");
                    }
                }
                // every checkpoint, shuffled: restore, play on, every step checked
                var order = Enumerable.Range(0, cps.Count).Select(i => i * every).Where(s => s <= end).ToList();
                var rng = new Random(1);
                order = order.OrderBy(_ => rng.Next()).ToList();
                var restoreMs = new List<double>();
                foreach (int at in order.Append(0))
                {
                    SimSnapshot snap = cps.Before(at)!;
                    var sw = Stopwatch.StartNew();
                    snap.Restore();
                    restoreMs.Add(sw.Elapsed.TotalMilliseconds);
                    player.Rewind(snap.Step);
                    int until = restoreMs.Count == order.Count + 1 ? end : Math.Min(end, snap.Step + every * 2);
                    while (player.Position < until && player.TryNext(out CampaignInput input))
                    {
                        host.Step(input);
                        if (!player.Check(host.Scene)) return $"restored at step {snap.Step}, differs at step {player.FailedAt}";
                    }
                }
                Console.WriteLine($"       restored {restoreMs.Count} times ({restoreMs.Average():0.0} ms avg, {restoreMs.Max():0.0} max), "
                    + "every step after exact; the first to the end too");
                return null;
            }
            finally
            {
                host.Dispose();
            }
        }
    }
}
