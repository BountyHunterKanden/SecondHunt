using System;
using System.Collections.Generic;
using System.Diagnostics;
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
    //   -replayaftermatch online [arena=<name>] [time=30]
    //       (0.5.1) records a bot match, then plays an online match here as its guest (PlayOnline: a local server and an
    //       -onlinetest host in another process), then diffs this process's walk of the recorded replay against a fresh one
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
            else if (args.Contains("online"))
            {
                // (0.5.1) the replay first, offline; then this process plays an online match as a guest, then views it
                path = PlayMatch(args, Arg, dir);
                if (PlayOnline(Arg) is string why)
                {
                    Console.WriteLine("FAIL: the online match: " + why);
                    Environment.ExitCode = 1;
                    return;
                }
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
            (int code, string output) = RunChild("-replayaftermatch", "child=" + (args.Contains("whole") || args.Contains("online") || Arg("replay") != null ? path : PartialPath()), "out=" + childOut);
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
            string arena = Arg("arena") is string name ? MatchArenas.Find(name)?.Name ?? name : MatchArenas.All.First().Name;
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

        // (0.5.1) an online match through the real server and relay, this process its guest (as the app that plays online
        // and then opens a replay): an -onlinetest external=1 host in another process (its own local server, a bot, a
        // player that hunts), joined here by OnlineMatchTool.Join. A saved replay (prior=) never runs the guest's net side.
        private static string? PlayOnline(Func<string, string?> Arg)
        {
            string arena = Arg("arena") ?? MatchArenas.All.First().Name, time = Arg("time") ?? "30";
            string exe = Environment.ProcessPath ?? "dotnet";
            var psi = new ProcessStartInfo(exe) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            if (Path.GetFileNameWithoutExtension(exe).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            {
                psi.ArgumentList.Add(typeof(ReplayTest).Assembly.Location);
            }
            foreach (string arg in new[] { "-onlinetest", "external=1", "players=2", "bots=1", "arena=" + arena, "time=" + time })
            {
                psi.ArgumentList.Add(arg);
            }
            using Process hostProcess = Process.Start(psi)!;
            var output = new List<string>();
            string? server = null, game = null;
            using var ready = new System.Threading.ManualResetEventSlim();
            hostProcess.OutputDataReceived += (_, e) =>
            {
                if (e.Data == null) return;
                lock (output)
                {
                    output.Add(e.Data);
                    if (e.Data.StartsWith("server: ", StringComparison.Ordinal)) server = e.Data[8..].Split(',')[0].Trim();
                    int at = e.Data.IndexOf(" hosts game ", StringComparison.Ordinal);
                    if (at >= 0) game = e.Data[(at + 12)..].Split(',')[0].Trim();
                    if (server != null && game != null) ready.Set();
                }
            };
            hostProcess.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (output) output.Add("stderr: " + e.Data); };
            hostProcess.BeginOutputReadLine();
            hostProcess.BeginErrorReadLine();
            try
            {
                if (!ready.Wait(60000)) return "the host didn't open a game: " + String.Join("\n", output);
                try
                {
                    OnlineMatchTool.Join(new[] { "-onlinejoin", "server=" + server, "game=" + game, "index=1", "hunter=Kanden", "time=" + time });
                }
                catch (MphRecomp.Online.OnlineException ex)
                {
                    // the host's server goes when its match ends, so the guest's last leave request may find it gone
                    Console.WriteLine("online: the guest's cleanup: " + ex.Message);
                }
                if (!hostProcess.WaitForExit(60000)) return "the host didn't end";
                hostProcess.WaitForExit();
                string? end = output.LastOrDefault(l => l.StartsWith("host: ") && l.Contains(" frames, ended "));
                Console.WriteLine("online: " + (end ?? "no end line"));
                return end?.Contains("ended True") == true ? null : "the match didn't end: " + String.Join("\n", output.TakeLast(20));
            }
            finally
            {
                if (!hostProcess.HasExited) hostProcess.Kill(entireProcessTree: true);
            }
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
