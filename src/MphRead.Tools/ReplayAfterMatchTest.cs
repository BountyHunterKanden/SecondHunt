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
            string arena = Arg("arena") ?? MatchArenas.All.First().Name;
            GameMode mode = Enum.Parse<GameMode>(Arg("mode") ?? "Battle", ignoreCase: true);
            int bots = Int32.Parse(Arg("bots") ?? "3");
            MatchSettings settings = MatchSettings.Quick(mode, arena, Hunter.Samus, bots, 1);
            settings.TimeLimitSeconds = Single.Parse(Arg("time") ?? "30");
            Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "mph-aftermatch"));
            string dir = Path.Combine(Path.GetTempPath(), "mph-aftermatch");
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
            string parentOut = Path.Combine(dir, "base_parent.txt"), childOut = Path.Combine(dir, "base_child.txt");
            if (DescribeReplay(ReplayFile.Load(path), parentOut, draw: true) is string err)
            {
                Console.WriteLine("FAIL: " + err);
                Environment.ExitCode = 1;
                return;
            }
            (int code, string output) = RunChild("-replayaftermatch", "child=" + (args.Contains("whole") ? path : PartialPath()), "out=" + childOut);
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
