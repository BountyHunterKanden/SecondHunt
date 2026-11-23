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
    //       its first KeySteps, then about 600 steps a second, the end mark last) and the child follows the feed; a
    //       network match's events go in as its host records them (a step's events just before the step)
    //   -replaypreptest ... guest[=cut|late|leave|rejoin]   (instant seek always [67c67ed8]) a network guest's live prepare: the
    //       recording as a host makes it goes through ReplayLiveSender into a guest's ReplayLiveMirror (blobs out of order,
    //       the first record blob before the head), and the child follows the guest's feed; cut: the stream stops at 70%
    //       (the link lost) and the host's whole file fills the rest (ReplayLiveMirror.Complete); late: this guest joins at
    //       95% (another from the start), so it gets the head written at the first send and is caught up from there in
    //       blobs of ReplayLive.MaxBlobSteps. Prints the stream's KB a minute and the sender's game-thread ms
    //       leave: the link lost at 60%, the host's file ending before what streamed in (the keyframes must cover it, no
    //       second prepare); rejoin: the app gone at 50% with its prepare still running, back as a new generation of the seat:
    //       the second prepare waits for the first (StartLive) and goes on from its keyframes (sidecar v4, owner 11-22)
    //   -replaypreptest ... appwire=off   the viewer without the app's menus wired first (by default they are: see PrepTest)
    //   -replaypreptest ... walkdiff=viewer|prepared|both|preparedleaf   (instant seek always) the two copies' walks
    //       made to differ (ReplayWalkProbe): extra objects and a leaf in the viewer only / extra objects in the prepare only
    //       / a list of another length in each: every seek must still be exact, the keyframes matched by name;
    //       preparedleaf: the prepare holds a leaf the viewer lacks, so the first keyframe must be refused naming the field
    //   -replaypreptest <file> prep=<sidecar>   (the child)
    internal static partial class ReplayTest
    {
        public static void PrepTest(string[] args)
        {
            string target = args.Length > 1 && !args[1].Contains('=') ? args[1] : "all";
            string? prep = args.FirstOrDefault(a => a.StartsWith("prep="))?[5..];
            bool live = args.Contains("live");
            string? guest = args.Contains("guest") ? "whole" : args.FirstOrDefault(a => a.StartsWith("guest="))?[6..];
            int seeks = int.TryParse(args.FirstOrDefault(a => a.StartsWith("seeks="))?[6..], out int n) ? n : 16;
            _walkDiff = args.FirstOrDefault(a => a.StartsWith("walkdiff="))?[9..];
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
                SetWalkProbe(viewer: false);
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
            // the viewer as the apps have it: their menus wired first (a settings file and a saves folder, as they start), so a
            // static only the app sets shows here as a refused keyframe ((11-21) CLOUD SAVE's CampaignSaves.Saved hook refused
            // every keyframe on the Odin since 11-17 while this test, with no menus, passed). appwire=off: without them
            if (args.FirstOrDefault(a => a.StartsWith("appwire="))?[8..] != "off")
            {
                string appDir = Path.Combine(Path.GetTempPath(), "mph-prep", "app");
                Directory.CreateDirectory(Path.Combine(appDir, "saves"));
                MphRecomp.Frontend.FrontendSession.Load(Paths.FileSystem, settingsPath: Path.Combine(appDir, "recomp_settings.json"),
                    savesDir: Path.Combine(appDir, "saves"));
                Console.WriteLine("       viewer: the app's menus wired first (appwire=off: without)");
            }
            // (diagnostics) viewwarm=<replay,...>: play those through in this process first, as the Android app plays its
            // matches before one is watched
            if (args.FirstOrDefault(a => a.StartsWith("viewwarm="))?[9..] is string viewWarm)
            {
                foreach (string w in viewWarm.Split(','))
                {
                    var wp = new ReplayPlayer(ReplayFile.Load(w));
                    CampaignHost wh = wp.Start(collectDrawItems: true, viewWidth: 1280, viewHeight: 720);
                    while (!wh.Ended && wp.TryNext(out CampaignInput wi)) wh.Step(wi);
                    wh.Dispose();
                    Console.WriteLine("       played " + Path.GetFileName(w) + " first");
                }
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
                string? result = PrepOne(path, file, seeks, live, args.FirstOrDefault(a => a.StartsWith("warm=")), guest);
                Console.WriteLine($"{(result == null ? "PASS" : "FAIL")}   {name}" + (result != null ? ": " + result : ""));
                if (result == null) pass++; else fail++;
            }
            Console.WriteLine($"{(fail == 0 ? "PASS" : "FAIL")}: {pass}/{pass + fail} prepared replays seek exactly");
            Environment.ExitCode = fail == 0 ? 0 : 1;
        }

        static string? _walkDiff;

        // the probe's value in this copy (walkdiff=): null where the kind gives this side nothing
        static void SetWalkProbe(bool viewer)
        {
            MphRecomp.Campaign.ReplayWalkProbe.Value = (_walkDiff, viewer) switch
            {
                ("viewer", true) => new List<object> { new int[4], new object(), new float[2] },
                ("prepared", false) => new List<object> { new int[4], new List<int> { 1, 2, 3 } },
                ("both", true) => new List<object> { new int[2] },
                ("both", false) => new List<object> { new int[2], new int[3], new int[5], new int[7], new int[9] },
                ("preparedleaf", false) => new List<object> { new int[4], new object() },
                _ => null
            };
        }

        static string[] ChildArgs(params string[] args) => _walkDiff == null ? args : args.Append("walkdiff=" + _walkDiff).ToArray();

        // the host's live sends, handed straight to the test: the watched guest's (seat 1; late: seat 2, once Joined; rejoin:
        // seat 1's next generation once it's back)
        sealed class LoopLink : IReplayLiveLink
        {
            public readonly List<byte[]> Sent = new();
            public bool Late, Joined;
            public int Generation = 1;
            public IReadOnlyList<(int Seat, int Generation)> SeatsWantingLive
                => !Late ? new[] { (1, Generation) } : Joined ? new[] { (1, 1), (2, 1) } : new[] { (1, 1) };
            public int LiveInFlight(int seat) => 0;
            public void SendLive(int seat, byte[] blob)
            {
                if (seat == (Late ? 2 : 1)) Sent.Add(blob);
            }
        }

        // -replaypreptest guest: the host's recording step by step through the sender, the guest's mirror fed what it sends
        // (out of order), the child following the guest's feed. leave: the link lost at 60%, and the host's file (what the
        // guest saves) ends 45 steps before what streamed in; rejoin: the guest's app gone at 50% -- its prepare left running
        // on a feed nobody ends -- and back 10 s later as a new generation of the seat, caught up from step 0 (owner 11-22
        // "everyone in every case": both had prepared from step 0 after the match)
        private static string? GuestStream(string path, ReplayFile file, string mode, string? warm, ref string sidecar,
            out int code, out string output, out ReplayFile? saved)
        {
            code = 1;
            output = "";
            saved = null;
            string dir = Path.Combine(Path.GetTempPath(), "mph-prep");
            string data = Path.Combine(dir, "guest-data");
            File.Delete(ReplayPrep.SidecarPath(data, file)); // (from step 0: a v4 sidecar is kept and gone on from)
            ReplayFile rec = ReplayFile.Load(path);
            List<ReplayStep> all = rec.Steps.ToList();
            List<ReplayNetEvent> events = rec.NetEvents.ToList();
            rec.Steps.Clear();
            rec.NetEvents.Clear();
            var link = new LoopLink { Late = mode == "late" };
            int late = all.Count * 95 / 100;
            int cut = mode switch { "cut" => all.Count * 7 / 10, "leave" => all.Count * 6 / 10, "rejoin" => all.Count / 2, _ => int.MaxValue };
            int back = mode == "rejoin" ? cut + 600 : int.MaxValue;
            var sender = new ReplayLiveSender(rec, link);
            var mirror = new ReplayLiveMirror(data);
            string? began = null, fed = null, keyWrong = null;
            var log = new List<string>();
            var children = new List<System.Threading.Tasks.Task<(int, string)>>();
            void Wire(ReplayLiveMirror m, string tag)
            {
                m.Log += l => { lock (log) log.Add(tag + l); };
                m.Begun += (p, s) =>
                {
                    if (s != ReplayPrep.SidecarPath(data, file)) keyWrong = "the guest's feed key isn't the saved replay's";
                    fed = p;
                    began = s;
                    // the app's own call (MatchReplay's mirror.Begun)
                    ReplayPrep.StartLive(s, () =>
                    {
                        lock (children)
                        {
                            children.Add(System.Threading.Tasks.Task.Run(() => warm == null ? RunChild(ChildArgs("-replaypreptest", p, "prep=" + s))
                                : RunChild(ChildArgs("-replaypreptest", p, "prep=" + s, warm))));
                        }
                    }, l => { lock (log) log.Add(tag + l); });
                };
            }
            Wire(mirror, "");
            int nextEvent = 0, delivered = 0;
            bool lost = false;
            var held = new List<byte[]>();
            double sendMs = 0, sendMax = 0;
            var sw = Stopwatch.StartNew();
            var one = new Stopwatch();
            void Deliver(bool rest)
            {
                // two at a time, the later first (so the first record blob comes before the head: it waits for it)
                if (!lost) while (delivered < link.Sent.Count) held.Add(link.Sent[delivered++]);
                while (held.Count >= 2 || (rest && held.Count > 0))
                {
                    if (held.Count >= 2) { mirror.Receive(held[1]); mirror.Receive(held[0]); held.RemoveRange(0, 2); }
                    else { mirror.Receive(held[0]); held.RemoveAt(0); }
                }
            }
            for (int i = 0; i < all.Count; i++)
            {
                while (nextEvent < events.Count && events[nextEvent].Step <= i) rec.NetEvents.Add(events[nextEvent++]);
                rec.Steps.Add(all[i]);
                if (i >= late) link.Joined = true;
                if (i == back)
                {
                    // the app back: a new mirror (the old one gone with its process, its feed never ended)
                    mirror = new ReplayLiveMirror(data);
                    Wire(mirror, "rejoined: ");
                    link.Generation = 2;
                    delivered = link.Sent.Count;
                    held.Clear();
                    lost = false;
                }
                one.Restart();
                sender.Step();
                double ms = one.Elapsed.TotalMilliseconds;
                sendMs += ms;
                sendMax = Math.Max(sendMax, ms);
                if (i >= cut && i < back) lost = true; // (the link lost: nothing more gets there)
                Deliver(rest: false);
                if (keyWrong != null) return keyWrong;
                if (began != null) sidecar = began;
                if (i % 60 == 0) System.Threading.Thread.Sleep(100);
            }
            while (nextEvent < events.Count) rec.NetEvents.Add(events[nextEvent++]);
            sender.End();
            Deliver(rest: true);
            // the host's whole file arrives (MatchReplay's ReplayReceived); leave: the match to the guest's leave
            ReplayFile whole = ReplayFile.Load(path);
            if (mode == "leave")
            {
                int at = mirror.Steps - 45;
                whole.Steps.RemoveRange(at, whole.Steps.Count - at);
                whole.NetEvents.RemoveAll(ev => ev.Step >= at);
                saved = whole;
            }
            mirror.Complete(whole);
            if (mode == "rejoin")
            {
                // the second prepare can only start once the first lets go of the sidecar
                var wait = Stopwatch.StartNew();
                while (true)
                {
                    lock (children) if (children.Count >= 2) break;
                    if (wait.ElapsedMilliseconds > ReplayFeed.StallMs + 20000) break;
                    System.Threading.Thread.Sleep(250);
                }
            }
            lock (log) foreach (string l in log) Console.WriteLine("       guest: " + l);
            if (children.Count == 0) return "the guest's feed never began";
            if (log.Any(l => l.Contains("live keyframes stopped"))) return "the guest's stream stopped";
            string want = mode switch
            {
                "cut" => "filled the stream",
                "leave" => "its keyframes fit it",
                _ => "streamed in whole"
            };
            if (!log.Any(l => l.Contains(want))) return $"the guest's log has no \"{want}\"";
            if (mode == "late" && !log.Any(l => l.Contains($"streams in from step {ReplayPrep.KeySteps / ReplayLive.SendEvery * ReplayLive.SendEvery + ReplayLive.SendEvery}")))
                return "the late guest didn't get the head written at the first send";
            if (mode == "rejoin" && children.Count < 2) return "the rejoined guest's prepare never started";
            var outputs = new List<string>();
            foreach (var c in children)
            {
                (int cc, string co) = c.Result;
                outputs.Add(co);
                code = cc;
                if (cc != 0) { output = string.Join("\n", outputs); return "a preparing process failed"; }
            }
            output = string.Join("\n", outputs);
            if (mode == "rejoin" && !outputs[1].Contains("going on from the keyframe at step"))
                return "the rejoined guest's prepare didn't go on from the first one's keyframes";
            double minutes = all.Count / 3600.0;
            Console.WriteLine($"       streamed {all.Count} steps ({mode}): {sender.Blobs} blobs, {sender.Bytes / 1024} KB "
                + $"({sender.Bytes / 1024 / minutes:0} KB a minute, {sender.Bytes / (all.Count / 60.0):0} bytes a second) in {sw.Elapsed.TotalSeconds:0.0} s");
            Console.WriteLine($"       sender game-thread ms: {sendMs:0.0} total, {sendMs / all.Count * 1000:0.0} us a step, max {sendMax:0.00} per step");
            if (ReplayFeed.IsLive(fed!)) return "the guest feed's files weren't cleaned up";
            return null;
        }

        private static string? PrepOne(string path, ReplayFile file, int seeks, bool live, string? warm = null, string? guest = null)
        {
            string dir = Path.Combine(Path.GetTempPath(), "mph-prep");
            Directory.CreateDirectory(dir);
            string sidecar = Path.Combine(dir, ReplayPrep.Key(file) + ReplayPrep.Extension);
            File.Delete(sidecar);
            int code;
            string output;
            if (guest != null)
            {
                string? why = GuestStream(path, file, guest, warm, ref sidecar, out code, out output, out ReplayFile? saved);
                if (why != null) return why;
                if (saved != null) file = saved; // (leave: the guest's replay is the host's file to its leave)
            }
            else if (live)
            {
                // the recording as a match makes it: the steps one by one, the feed told after each
                string data = Path.Combine(dir, "live-data");
                File.Delete(ReplayPrep.SidecarPath(data, file)); // (from step 0: a v4 sidecar is kept and gone on from)
                ReplayFile rec = ReplayFile.Load(path);
                List<ReplayStep> all = rec.Steps.ToList();
                List<ReplayNetEvent> events = rec.NetEvents.ToList();
                rec.Steps.Clear();
                rec.NetEvents.Clear();
                int nextEvent = 0;
                var feed = new ReplayFeed(rec, data);
                System.Threading.Tasks.Task<(int, string)>? child = null;
                var sw = Stopwatch.StartNew();
                for (int i = 0; i < all.Count; i++)
                {
                    while (nextEvent < events.Count && events[nextEvent].Step <= i) rec.NetEvents.Add(events[nextEvent++]);
                    rec.Steps.Add(all[i]);
                    if (feed.Step())
                    {
                        if (feed.Sidecar != ReplayPrep.SidecarPath(data, file)) return "the feed's key isn't the saved replay's";
                        // (MatchReplay.FeedStep: ReplayPrep.StartLive, whose lock is free here)
                        if (ReplayPrep.Busy(feed.Sidecar!)) return "the live feed's sidecar is held";
                        sidecar = feed.Sidecar!;
                        string fed = feed.ReplayPath!;
                        child = System.Threading.Tasks.Task.Run(() => warm == null ? RunChild(ChildArgs("-replaypreptest", fed, "prep=" + sidecar)) : RunChild(ChildArgs("-replaypreptest", fed, "prep=" + sidecar, warm)));
                    }
                    if (i % 60 == 0) System.Threading.Thread.Sleep(100);
                }
                while (nextEvent < events.Count) rec.NetEvents.Add(events[nextEvent++]); // (after the last step: none applied)
                feed.End();
                if (child == null) return "the feed never began";
                (code, output) = child.Result;
                Console.WriteLine($"       fed {all.Count} steps in {sw.Elapsed.TotalSeconds:0.0} s; the child done with them");
                if (ReplayFeed.IsLive(feed.ReplayPath!)) return "the feed's files weren't cleaned up";
            }
            else
            {
                (code, output) = warm == null ? RunChild(ChildArgs("-replaypreptest", path, "prep=" + sidecar)) : RunChild(ChildArgs("-replaypreptest", path, "prep=" + sidecar, warm));
            }
            foreach (string line in output.Split('\n').Where(l => l.StartsWith("child") || l.Contains("PREP")))
            {
                Console.WriteLine("       " + line.TrimEnd());
            }
            if (code != 0) return "the preparing process failed";
            using (KeyframeSidecar? s = KeyframeSidecar.Open(sidecar))
            {
                if (s == null || !s.Complete) return "no complete sidecar";
                if (guest == "leave" ? !s.Covers(file) : !s.Fits(file))
                    return $"the sidecar doesn't {(guest == "leave" ? "cover" : "fit")} the saved replay ({s.EndSteps} steps, the replay {file.Steps.Count})";
                if (!s.Covers(file)) return "the sidecar fits the saved replay but doesn't cover it";
                // another match with the same key (the same start, parting later) mustn't take it
                ReplayFile other = ReplayFile.Load(path);
                other.Steps.RemoveAt(other.Steps.Count - 1);
                if (ReplayPrep.Key(other) != ReplayPrep.Key(file) || s.Fits(other)) return "a match with the same key fits the sidecar";
                // (v4) one that parts at 60%: only the keyframes to there are its own (the prefix hashes)
                ReplayFile parted = ReplayFile.Load(path);
                if (guest == "leave") parted.Steps.RemoveRange(file.Steps.Count, parted.Steps.Count - file.Steps.Count);
                int part = parted.Steps.Count * 6 / 10;
                ReplayStep ps = parted.Steps[part];
                ps.Checksum ^= 1;
                parted.Steps[part] = ps;
                int partedBefore = s.Before(parted.Steps.Count, ReplayPrep.PrefixHashes(parted));
                if (ReplayPrep.Key(parted) != ReplayPrep.Key(file) || s.Covers(parted) || partedBefore > part)
                    return $"a match that parts at step {part} takes keyframes past it (to {partedBefore})";
                Console.WriteLine($"       a match parting at step {part} gets the keyframes to {partedBefore} only");
                var recs = s.Records.ToList();
                double minutes = file.Steps.Count / 3600.0;
                Console.WriteLine($"       sidecar {s.Length / 1024} KB ({s.Length / 1024 / minutes:0} KB a minute), {recs.Count} keyframes");
                var byDistance = recs.Where(r => r.Anchor >= 0).GroupBy(r => r.Step - r.Anchor).OrderBy(g => g.Key)
                    .Select(g => $"{g.Key}:{g.Average(r => r.Length) / 1024:0}");
                Console.WriteLine($"       whole KB avg {recs.Where(r => r.Anchor < 0).Average(r => r.Length) / 1024:0}; delta KB by distance "
                    + string.Join(" ", byDistance));
            }
            if (live || guest != null)
            {
                // a new match with this one's start (bot matches often share it) gets its live prepare: the complete
                // sidecar left by this one mustn't stop it, though it's kept for viewing (11-21: it was kept,
                // and the Odin's second self-test match got no keyframes until it ended)
                string again = Path.Combine(dir, "again" + ReplayPrep.Extension);
                File.Copy(sidecar, again, true);
                if (ReplayPrep.NeedsPrep(again)) return "a complete sidecar wasn't kept for viewing";
                if (!ReplayPrep.NeedsPrep(again, live: true) || !File.Exists(again)) return "a new live match wouldn't prepare into an earlier match's sidecar (v4: kept)";
                File.Delete(again);
                Console.WriteLine("       a new match with the same start still prepares live (into the same sidecar)");
            }

            SetWalkProbe(viewer: true);
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
                    if (!kf.Load(at))
                    {
                        if (_walkDiff == "preparedleaf" && kf.LastRefusal?.Contains("this copy doesn't have") == true)
                        {
                            Console.WriteLine("       refused as it should be: " + kf.LastRefusal);
                            return null;
                        }
                        return $"keyframe {at} refused: {kf.LastRefusal}";
                    }
                    if (i == 0 && _walkDiff != null)
                    {
                        if (kf.MatchedByName == null) return "walkdiff: the walks matched (the probe made no difference)";
                        if (_walkDiff == "preparedleaf") return "walkdiff=preparedleaf: the keyframe loaded though the viewer lacks a leaf";
                        Console.WriteLine("       " + kf.MatchedByName.Split('\n')[0]);
                    }
                    loadMs.Add(kf.LastLoadMs);
                    // (as MatchReplay.AfterRestore: the reticles' pixel data with their restored size; without it a seek
                    // into an Imperialist zoom indexed the normal reticle's data and crashed)
                    foreach (MphRead.Entities.PlayerEntity p in MphRead.Entities.PlayerEntity.Players) p.HostAfterRestore();
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
                MphRecomp.Campaign.ReplayWalkProbe.Value = null;
            }
        }
    }
}
