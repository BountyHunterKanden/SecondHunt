using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using MphRecomp.Campaign;

namespace MphRecomp.Replay
{
    // Thread B (instant seek, step 2a): what portable keyframe work costs on a device (the Odin), with no drawing. Plays a
    // replay headless, writing a whole keyframe and a delta from the previous one every `every` steps (timed), keeps a few
    // whole ones and a chain of deltas, then loads them back in this same process (timed: the first load, then warm) and
    // plays on checking every step, so a timing run is also an exactness run. The Android side is ReplayKeyframeTimeActivity
    // (dev only); the PC's cross-process exactness test is Tools -replaykeyframetest.
    public static class ReplayKeyframeTiming
    {
        static double Avg(List<double> v) => v.Count == 0 ? 0 : v.Average();
        static double Max(List<double> v) => v.Count == 0 ? 0 : v.Max();
        static double P99(List<double> v)
        {
            if (v.Count == 0) return 0;
            var s = v.OrderBy(x => x).ToList();
            return s[Math.Min(s.Count - 1, (int)(s.Count * 0.99))];
        }

        static byte[] Brotli(byte[] data, CompressionLevel level)
        {
            var o = new MemoryStream();
            using (var b = new BrotliStream(o, level, leaveOpen: true)) b.Write(data);
            return o.ToArray();
        }

        // keep: how many whole keyframes to hold for the load timings (memory; they are spread over the replay)
        public static string? Run(ReplayFile file, int every, int keep, string label, Action<string> log)
        {
            string? why = ReplayPlayer.CheckBeforeStart(file);
            if (why != null) return "refused: " + why;
            var player = new ReplayPlayer(file);
            CampaignHost host = player.Start(collectDrawItems: false, viewWidth: 256, viewHeight: 192);
            try
            {
                if (player.CheckAfterStart() is string after) return "refused: " + after;
                ReplayCheckpoints.SetUpOnce();
                var sw = Stopwatch.StartNew();
                var kbase = ReplayCheckpoints.KeyframeBase(host);
                double baseMs = sw.Elapsed.TotalMilliseconds;
                var writer = new SimSnapshot.KeyframeWriter(kbase);
                int total = file.Steps.Count;
                int saveEvery = Math.Max(every, total / Math.Max(1, keep) / every * every);
                var stepMs = new List<double>();
                var takeMs = new List<double>();
                var wholeMs = new List<double>();
                var deltaMs = new List<double>();
                var fastMs = new List<double>();
                var wholeRaw = new List<double>();
                var wholeFast = new List<double>();
                var deltaRaw = new List<double>();
                var deltaFast = new List<double>();
                var wholes = new Dictionary<int, byte[]>();
                var chain = new List<(int From, int To, byte[] Data)>();
                int firstSaved = -1, chainEnd = -1;
                SimSnapshot prev = kbase.Snap;
                while (!host.Ended && player.TryNext(out CampaignInput input))
                {
                    sw.Restart();
                    host.Step(input);
                    stepMs.Add(sw.Elapsed.TotalMilliseconds);
                    if (!player.Check(host.Scene)) return $"straight run differs at step {player.FailedAt}";
                    int pos = player.Position;
                    if (pos % every != 0) continue;
                    sw.Restart();
                    SimSnapshot snap = SimSnapshot.Take(pos, ReplayCheckpoints.Roots(host), prev);
                    takeMs.Add(sw.Elapsed.TotalMilliseconds);
                    sw.Restart();
                    byte[] f = writer.Write(snap);
                    wholeMs.Add(sw.Elapsed.TotalMilliseconds);
                    sw.Restart();
                    byte[] d = writer.Write(snap, prev);
                    deltaMs.Add(sw.Elapsed.TotalMilliseconds);
                    sw.Restart();
                    byte[] ff = Brotli(f, CompressionLevel.Fastest);
                    fastMs.Add(sw.Elapsed.TotalMilliseconds);
                    byte[] df = Brotli(d, CompressionLevel.Fastest);
                    wholeRaw.Add(f.Length);
                    wholeFast.Add(ff.Length);
                    deltaRaw.Add(d.Length);
                    deltaFast.Add(df.Length);
                    if (pos == every || (pos % saveEvery == 0 && wholes.Count < keep))
                    {
                        wholes[pos] = f;
                        if (firstSaved < 0)
                        {
                            firstSaved = pos;
                            chainEnd = pos + 10 * every;
                        }
                    }
                    if (firstSaved >= 0 && pos > firstSaved && pos <= chainEnd) chain.Add((prev.Step, pos, d));
                    prev = snap;
                }
                if (writer.Refused.Count > 0) return "refused types: " + string.Join(", ", writer.Refused);
                int played = player.Position;
                double simTotal = stepMs.Sum();
                log($"[{label}] {file.Settings.Mode} in {file.Settings.Arena}, {file.Settings.Players.Count} players, {played} steps, every {every}");
                log($"[{label}] base {baseMs:0} ms ({kbase.ObjectCount} objects, {kbase.LeafCount} leaves)");
                log($"[{label}] sim ms/step avg {Avg(stepMs):0.000} p99 {P99(stepMs):0.00} max {Max(stepMs):0.0}");
                log($"[{label}] snapshot take ms avg {Avg(takeMs):0.0} max {Max(takeMs):0.0}");
                log($"[{label}] whole write ms avg {Avg(wholeMs):0.0} max {Max(wholeMs):0.0}; brotli fast ms avg {Avg(fastMs):0.0}; "
                    + $"KB raw {Avg(wholeRaw) / 1024:0} fast {Avg(wholeFast) / 1024:0}");
                log($"[{label}] delta({every}) write ms avg {Avg(deltaMs):0.0} max {Max(deltaMs):0.0}; KB raw {Avg(deltaRaw) / 1024:0} fast {Avg(deltaFast) / 1024:0}");
                // per match second: sim + a delta every `every` steps + (whole every 600 steps, fast-compressed)
                double perSecond = 60 * Avg(stepMs) + 60.0 / every * (Avg(takeMs) + Avg(deltaMs))
                    + 60.0 / 600 * (Avg(wholeMs) + Avg(fastMs));
                log($"[{label}] shadow cost per match second: {perSecond:0} ms (= {perSecond / 10:0}% of one core at 1x; sim {60 * Avg(stepMs):0})");

                // loads, same process: whole ones in shuffled order (the first = cold), each followed by `every` checked steps
                var rng = new Random(1);
                var loadMs = new List<double>();
                var warmChecked = 0;
                foreach (int at in wholes.Keys.OrderBy(_ => rng.Next()))
                {
                    sw.Restart();
                    string? r = new SimSnapshot.KeyframeLoader(kbase).Load(wholes[at]);
                    loadMs.Add(sw.Elapsed.TotalMilliseconds);
                    if (r != null) return $"whole {at} refused: {r}";
                    player.Rewind(at);
                    int until = Math.Min(total, at + every);
                    while (player.Position < until && !host.Ended && player.TryNext(out CampaignInput input))
                    {
                        host.Step(input);
                        if (!player.Check(host.Scene)) return $"loaded whole {at}, differs at step {player.FailedAt}";
                        warmChecked++;
                    }
                }
                log($"[{label}] whole load x{loadMs.Count} ms first {loadMs[0]:0.0}, warm avg {Avg(loadMs.Skip(1).ToList()):0.0} max {Max(loadMs):0.0} "
                    + $"(a fresh loader each; {warmChecked} steps after exact)");
                // the same loader reused (warm), whole loads again
                var loader = new SimSnapshot.KeyframeLoader(kbase);
                var reuse = new List<double>();
                foreach (int at in wholes.Keys.OrderBy(_ => rng.Next()))
                {
                    sw.Restart();
                    string? r = loader.Load(wholes[at]);
                    reuse.Add(sw.Elapsed.TotalMilliseconds);
                    if (r != null) return $"whole {at} (reused loader) refused: {r}";
                }
                log($"[{label}] whole load, one loader reused: avg {Avg(reuse):0.0} ms max {Max(reuse):0.0}");
                // whole + its deltas, then on
                if (chain.Count > 0)
                {
                    int start = wholes.Keys.Min();
                    string? r = loader.Load(wholes[start]);
                    if (r != null) return "chain start refused: " + r;
                    var dms = new List<double>();
                    foreach ((int from, int to, byte[] data) in chain)
                    {
                        sw.Restart();
                        r = loader.Load(data);
                        dms.Add(sw.Elapsed.TotalMilliseconds);
                        if (r != null) return $"delta {from}->{to} refused: {r}";
                    }
                    int at = chain[^1].To;
                    player.Rewind(at);
                    int until = Math.Min(total, at + every);
                    int n = 0;
                    while (player.Position < until && !host.Ended && player.TryNext(out CampaignInput input))
                    {
                        host.Step(input);
                        if (!player.Check(host.Scene)) return $"after deltas to step {at}, differs at step {player.FailedAt}";
                        n++;
                    }
                    log($"[{label}] delta load x{dms.Count} ms avg {Avg(dms):0.0} max {Max(dms):0.0}; whole {start} + {chain.Count} deltas exact for {n} steps after");
                }
                log($"[{label}] done, sim total {simTotal / 1000:0.0} s");
                return null;
            }
            finally
            {
                host.Dispose();
            }
        }
    }
}
