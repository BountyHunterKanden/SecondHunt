using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MphRead.Entities;
using MphRecomp.Campaign;
using MphRecomp.Multiplayer;
using MphRecomp.Replay;

namespace MphRead
{
    // Thread B: a replay opened straight after its match was played (the app's usual path) must walk the same keyframe
    // base as a fresh process (the live prepare), or the viewer refuses the prepared keyframes.
    //   -replayaftermatch [arena=<name>] [mode=Battle] [bots=3] [time=30]
    //       plays a bot match here with a recorder, opens its replay in this same process, and diffs that walk against a
    //       fresh process's walk of the same file. Exit code 0 = the same walk.
    //       before=<name> plays a match there first (a different arena's match left over from earlier)
    //   -replayaftermatch arena=all [time=20]
    //       the release check (docs/RELEASE.md section 5): every arena, each after the previous arena's match, in its
    //       own process. Exit code 0 = every arena kept its keyframes.
    //   -replayaftermatch prior=<a.mphreplay>[,<b>...] replay=<c.mphreplay>
    //       the same check for saved replays: plays each prior replay through here first (the matches the app ran before
    //       the viewer opened), then diffs this process's walk of the replay against a fresh process's
    internal static partial class ReplayTest
    {
        public static void AfterMatchTest(string[] args)
        {
            string? Arg(string key) => args.FirstOrDefault(a => a.StartsWith(key + "="))?[(key.Length + 1)..];
            if (!Setup())
            {
                return;
            }
            if (Arg("child") is string childPath)
            {
                Console.WriteLine(DescribeReplay(ReplayFile.Load(childPath), Arg("out")!) ?? "CHILD DONE");
                return;
            }
            if (Arg("arena") == "all")
            {
                SweepArenas(Arg("time") ?? "20");
                return;
            }
            string dir = Path.Combine(Path.GetTempPath(), "mph-aftermatch");
            Directory.CreateDirectory(dir);
            string path;
            if (Arg("replay") is string replayPath)
            {
                foreach (string prior in (Arg("prior") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries))
                {
                    if (prior.StartsWith("view:", StringComparison.Ordinal))
                    {
                        Console.WriteLine($"viewed {Path.GetFileName(prior[5..])} first: {ViewWithSeek(ReplayFile.Load(prior[5..]))}");
                        continue;
                    }
                    ReplayVerify.Result r = ReplayVerify.Run(ReplayFile.Load(prior));
                    Console.WriteLine($"played {Path.GetFileName(prior)} first: {r}");
                }
                path = replayPath;
            }
            else
            {
                if (Arg("before") is string before)
                {
                    string[] first = args.Where(a => !a.StartsWith("arena=") && !a.StartsWith("before=")).Append("arena=" + before).ToArray();
                    PlayMatch(first, k => first.FirstOrDefault(a => a.StartsWith(k + "="))?[(k.Length + 1)..], dir);
                }
                path = PlayMatch(args, Arg, dir);
            }
            string parentOut = Path.Combine(dir, "base_parent.txt"), childOut = Path.Combine(dir, "base_child.txt");
            if (DescribeReplay(ReplayFile.Load(path), parentOut, draw: true) is string err)
            {
                Console.WriteLine("FAIL: " + err);
                Environment.ExitCode = 1;
                return;
            }
            (int code, string output) = RunChild("-replayaftermatch", "child=" + (args.Contains("whole") || Arg("replay") != null ? path : PartialPath()), "out=" + childOut);
            if (!File.Exists(childOut))
            {
                Console.WriteLine("FAIL: the other process: " + output);
                Environment.ExitCode = 1;
                return;
            }
            string[] a = File.ReadAllLines(parentOut), b = File.ReadAllLines(childOut);
            Console.WriteLine($"after match: {a[0]}");
            Console.WriteLine($"fresh:       {b[0]}");
            var ca = a.Skip(1).GroupBy(x => x).ToDictionary(g => g.Key, g => g.Count());
            var cb = b.Skip(1).GroupBy(x => x).ToDictionary(g => g.Key, g => g.Count());
            int shown = 0;
            foreach (string k in ca.Keys.Union(cb.Keys).OrderBy(x => x))
            {
                int na = ca.GetValueOrDefault(k), nb = cb.GetValueOrDefault(k);
                if (na != nb && shown++ < 80) Console.WriteLine($"  {na,4} vs {nb,4}  {k}");
            }
            bool same = a.SequenceEqual(b);
            Console.WriteLine(same ? "PASS: the same walk" : $"FAIL: the walks differ ({shown} line kinds)");
            Environment.ExitCode = same ? 0 : 1;
        }

        // (11-06) the spider ball's track cache held the last match's room and a Mining Plaza replay played after a
        // match refused its keyframes; each arena here after the one before it, so a new leftover shows up before release
        private static void SweepArenas(string time)
        {
            var failed = new List<string>();
            IReadOnlyList<RoomMetadata> arenas = MatchArenas.All;
            for (int i = 0; i < arenas.Count; i++)
            {
                string before = arenas[(i + arenas.Count - 1) % arenas.Count].Name;
                (int code, string output) = RunChild("-replayaftermatch", "arena=" + arenas[i].Name, "before=" + before, "time=" + time, "whole");
                string last = output.Split('\n', StringSplitOptions.RemoveEmptyEntries).LastOrDefault(l => l.StartsWith("PASS") || l.StartsWith("FAIL"))?.Trim()
                    ?? "FAIL: no result";
                Console.WriteLine($"{arenas[i].Name} (after {before}): {last}");
                if (code != 0)
                {
                    failed.Add(arenas[i].Name);
                    Console.WriteLine(output);
                }
            }
            Console.WriteLine(failed.Count == 0 ? $"PASS: all {arenas.Count} arenas keep their keyframes after a match"
                : $"FAIL: {failed.Count} of {arenas.Count}: {String.Join(", ", failed)}");
            Environment.ExitCode = failed.Count == 0 ? 0 : 1;
        }

        private static string PlayMatch(string[] args, Func<string, string?> Arg, string dir)
        {
            string arena = Arg("arena") ?? MatchArenas.All.First().Name;
            GameMode mode = Enum.Parse<GameMode>(Arg("mode") ?? "Battle", ignoreCase: true);
            int bots = Int32.Parse(Arg("bots") ?? "3");
            MatchSettings settings = MatchSettings.Quick(mode, arena, Hunter.Samus, bots, 1);
            settings.TimeLimitSeconds = Single.Parse(Arg("time") ?? "30");
            var recorder = new ReplayRecorder(settings, "aftermatch-test");
            CampaignHost host = CampaignHost.StartMatch(settings, collectDrawItems: true, viewWidth: 256, viewHeight: 192);
            recorder.Started(host.Scene);
            var input = new CampaignInput { SelectWeapon = BeamType.None };
            long cap = (long)(settings.TimeLimitSeconds.Value * 60) + 900;
            while (!host.Ended && host.Frame < cap)
            {
                if (args.Contains("move"))
                {
                    input.Move = new System.Numerics.Vector2(0, 1);
                    input.AimDelta = new System.Numerics.Vector2(host.Frame % 240 < 120 ? 2 : -2, 0);
                    input.Buttons = host.Frame % 30 < 10 ? CampaignButtons.Shoot : CampaignButtons.None;
                }
                host.Step(input);
                recorder.Step(input, host.Scene);
                // the live feed's copy: the file as it was a few steps in (ReplayFeed.Step), what the prepare starts from
                if (recorder.File.Steps.Count == ReplayPrep.KeySteps) recorder.File.Save(PartialPath());
            }
            recorder.End(host.Ended);
            host.Dispose();
            string path = Path.Combine(dir, "played" + ReplayLibrary.Extension);
            recorder.File.Save(path);
            Console.WriteLine($"played {recorder.File.Steps.Count} steps in {arena} ({mode}), saved {path}");
            return path;
        }

        // a viewing as the app does one: drawn, its step-0 keyframe base walked, checkpoints taken, then a seek back
        private static string ViewWithSeek(ReplayFile file)
        {
            var player = new ReplayPlayer(file);
            CampaignHost host = player.Start(collectDrawItems: true, viewWidth: 256, viewHeight: 192);
            try
            {
                ReplayCheckpoints.KeyframeBase(host);
                var cps = new ReplayCheckpoints();
                cps.AfterStep(0, host);
                while (player.Position < 600 && !host.Ended && player.TryNext(out CampaignInput input))
                {
                    player.BeforeStep();
                    host.Step(input);
                    cps.AfterStep(player.Position, host);
                }
                if (cps.Before(130) is not SimSnapshot snap) return "no checkpoint";
                snap.Restore();
                player.Rewind(snap.Step);
                return $"seeked back to {snap.Step}";
            }
            finally
            {
                host.Dispose();
            }
        }

        private static string PartialPath() => Path.Combine(Path.GetTempPath(), "mph-aftermatch", "partial" + ReplayLibrary.Extension);

        private static string? DescribeReplay(ReplayFile file, string outPath, bool draw = false)
        {
            if (ReplayPlayer.CheckBeforeStart(file) is string why) return "refused: " + why;
            var player = new ReplayPlayer(file);
            CampaignHost host = player.Start(collectDrawItems: draw, viewWidth: 256, viewHeight: 192);
            try
            {
                if (player.CheckAfterStart() is string after) return "refused: " + after;
                ReplayCheckpoints.SetUpOnce();
                var kbase = ReplayCheckpoints.KeyframeBase(host);
                File.WriteAllLines(outPath, new[] { $"objects {kbase.ObjectCount} leaves {kbase.LeafCount} signature {kbase.Signature}" }
                    .Concat(kbase.Describe()));
                return null;
            }
            finally
            {
                host.Dispose();
            }
        }
    }
}
