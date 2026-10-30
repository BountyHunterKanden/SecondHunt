using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MphRead;
using MphRecomp.Campaign;
using MphRecomp.Frontend;
using MphRecomp.Multiplayer;
using MphRecomp.Multiplayer.Net;
using MphRecomp.Replay;

namespace SecondHunt.MatchHost
{
    // The served match's replay (docs/DEDICATED_SERVERS.md section 4: "the match host records the replay and gives every
    // player a copy"). In a served match every player is a guest, and a guest never records (it doesn't run the whole match):
    // the match host does, as a player host's app does (MatchReplay.cs StartMatchReplayed / EndReplay): ReplayRecorder over
    // the match with every seat's network input tapped (all of them remote here, seat 0 too), and at a match that ended
    // itself the file, built in memory (nothing on the server's disk), goes to each guest that keeps replays over the
    // session's ReplayLink, with every guest's identity in it. A replay that goes wrong is logged and dropped: the match and
    // the lobby carry on.
    // Cut short (0.3.6, 2026-10-19: a partial replay for a guest that quits, and for early match
    // ends): a guest that quits mid-match asks for the recording so far (ReplayLink.RequestReplay) and gets it at once, the
    // match going on; a match that ends early (stopped, or past its limit) sends the cut-short recording to every seat that
    // keeps replays and hasn't had one. One everyone left goes to nobody (each quitter had its copy as it went).
    // A recording that doesn't get to a player (0.3.8, 2026-10-22: a slow link lost a long one): the link hands it back
    // (ReplayLink.SendFailed) and, with --mailbox (the server's drop folder), it's written there as that player's own copy
    // (OwnSeat = their seat) and the server is told (`!replay <friend code> <file>`); the server files it in the player's
    // replay mailbox and the app takes it into REPLAYS when it's next online (HubReplays.cs FromMatchHost).
    internal static partial class Program
    {
        // the last match's link: kept past its match (its recording goes out while the session sits in its lobby), closed when
        // the next match starts or the session stops (CloseReplayLink)
        private static ReplayLink? _replayLink;
        // the seats that asked for the recording so far (the link's receive thread adds; Play's loop answers), and those that
        // had a cut-short copy (not sent again at the end)
        private static readonly System.Collections.Concurrent.ConcurrentQueue<int> _replayAsked = new();
        private static readonly HashSet<int> _replayGiven = new();

        // the recording's Build (information only: playback checks the sim version, settings and arena data, never this)
        internal static string ReplayBuild = "match host";

        // --mailbox: the server's drop folder for recordings that didn't get to their player, and how the server is told
        // (ControlMode's `!` lines); either missing: such a recording is only logged
        internal static string? Mailbox;
        internal static Action<string>? ServerLine;

        // Play, right before CampaignHost.StartMatch: a new link (each guest sends its identity as its own match starts; its
        // pieces are sent again for 20 s, so a link up before the match catches them) and the recorder (the RNG as the match
        // will start from: ReplayRecorder.RngAtStart)
        private static ReplayRecorder? StartReplay(NetMatchSession net, MatchSettings settings)
        {
            CloseReplayLink(0);
            try
            {
                _replayAsked.Clear();
                _replayGiven.Clear();
                var link = new ReplayLink(net);
                _replayLink = link;
                link.ReplayRequested += seat => _replayAsked.Enqueue(seat);
                link.SendFailed += (seat, bytes) => ToMailbox(link, seat, bytes);
                return new ReplayRecorder(settings, ReplayBuild);
            }
            catch (Exception ex)
            {
                Log("replay: not recording: " + ex.Message);
                return null;
            }
        }

        // right after CampaignHost.StartMatch: the remote seats' taps, and room for the whole match up front
        private static void ReplayStarted(ref ReplayRecorder? recorder, CampaignHost host, NetMatchSession net)
        {
            if (recorder == null) return;
            try
            {
                recorder.Started(host, net.Kind);
                // the shot check of each shot and claimed hit, in the file beside the match (format 47: one shared replay
                // carries both sides of every shot)
                ReplayFile file = recorder.File;
                ShotCheck.Settled = check => file.ShotChecks.Add(check);
                float seconds = Math.Min(recorder.File.Settings.TimeLimitSeconds ?? 600, 3600);
                recorder.File.Steps.Capacity = (int)(seconds * 60) + 600;
            }
            catch (Exception ex)
            {
                Log("replay: not recording: " + ex.Message);
                DropReplay(ref recorder, host);
            }
        }

        // after each driver.Step (the remote seats' inputs came through the taps during it)
        private static void RecordStep(ref ReplayRecorder? recorder, in CampaignInput input, CampaignHost host)
        {
            if (recorder == null) return;
            try
            {
                recorder.Step(input, host.Scene);
            }
            catch (Exception ex)
            {
                Log("replay: recording stopped: " + ex.Message);
                DropReplay(ref recorder, host);
            }
        }

        // Play's loop, between steps: each guest that asked (leaving mid-match) gets the recording so far
        private static void AnswerReplayRequests(ReplayRecorder? recorder)
        {
            while (_replayAsked.TryDequeue(out int seat))
            {
                ReplayLink? link = _replayLink;
                if (recorder == null || link == null || recorder.File.Steps.Count < MinCutShortSteps)
                {
                    Log($"replay: seat {seat} left and asked for it: none to send ({recorder?.File.Steps.Count ?? 0} steps)");
                    continue;
                }
                try
                {
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    byte[] bytes = recorder.Partial(link.Identities.OrderBy(i => i.Seat));
                    link.SendReplay(seat, bytes);
                    _replayGiven.Add(seat);
                    Log($"replay: seat {seat} left: {recorder.File.Steps.Count} steps so far, {bytes.Length / 1024} KB to it "
                        + $"(written in {sw.ElapsedMilliseconds} ms)");
                }
                catch (Exception ex)
                {
                    Log($"replay: seat {seat} left: not sent: " + ex.Message);
                }
            }
        }

        // a cut-short recording shorter than this isn't sent (as a player's own: MatchReplay.cs MinIncompleteSteps)
        private const int MinCutShortSteps = 10 * 60;

        private static void DropReplay(ref ReplayRecorder? recorder, CampaignHost host)
        {
            recorder = null;
            ShotCheck.Settled = null;
            host.RemoteInputTap = null;
            host.SeatDropTap = null;
            host.SeatRejoinTap = null;
            host.ClaimedHitTap = null;
            host.ClientPositionTap = null;
        }

        // Play, as the match is over and its seats still the match's (before EndMatch): to every guest that keeps replays, whole
        // for a match that ended itself, cut short for one that ended early (not to a seat that had its cut-short copy as it
        // left; one everyone left goes to nobody)
        private static void EndReplay(ReplayRecorder? recorder, CampaignHost host, NetMatchSession net)
        {
            AnswerReplayRequests(recorder); // (one that asked as the match ended)
            ReplayLink? link = _replayLink;
            ShotCheck.Settled = null;
            if (recorder == null || link == null)
            {
                return;
            }
            bool complete = host.Ended;
            if (!complete && net.Joined == 0)
            {
                Log($"replay: everyone left ({_replayGiven.Count} cut-short copies went out as they did)");
                return;
            }
            if (!complete && recorder.File.Steps.Count < MinCutShortSteps)
            {
                Log($"replay: not sent (the match ended early, after {recorder.File.Steps.Count} steps)");
                return;
            }
            try
            {
                recorder.End(complete);
                ReplayFile file = recorder.File;
                // how each seat placed (the REPLAYS list's win / loss; a match cut short has none, as a player's own); no
                // local seat changes nothing here (Capture's rows are every active seat)
                if (complete)
                {
                    foreach (MatchResults.Player p in MatchResults.Capture(file.Settings.LocalSeat).Rows)
                    {
                        file.Places.Add(new ReplayPlace(p.Seat, p.Place, p.Points));
                    }
                }
                // every human seat's identity as its device sent it (this host has none of its own)
                foreach (ReplaySeatIdentity id in link.Identities.OrderBy(i => i.Seat))
                {
                    file.Identities.Add(id);
                }
                List<int> seats = link.SeatsWantingReplay.Where(s => !_replayGiven.Contains(s)).ToList();
                if (seats.Count == 0)
                {
                    Log($"replay: {file.Steps.Count} steps, nobody asked for it");
                    return;
                }
                var ms = new MemoryStream();
                file.Write(ms);
                byte[] bytes = ms.ToArray();
                foreach (int seat in seats)
                {
                    link.SendReplay(seat, bytes);
                }
                Log($"replay: {file.Steps.Count} steps{(complete ? "" : " (cut short)")}, {file.NetEvents.Count} network events, {file.Identities.Count} identities, "
                    + $"{bytes.Length / 1024} KB to seat(s) {string.Join(", ", seats.OrderBy(s => s))}");
            }
            catch (Exception ex)
            {
                Log("replay: not sent: " + ex.Message);
            }
        }

        // a recording that didn't get to a seat (the link's timer thread, or whoever closed it): to the server's drop folder
        // as that player's own copy
        private static void ToMailbox(ReplayLink link, int seat, byte[] bytes)
        {
            string? code = link.Identities.Where(i => i.Seat == seat).Select(i => i.FriendCode).FirstOrDefault();
            if (Mailbox == null || ServerLine == null || code == null)
            {
                Log($"replay: seat {seat} didn't get its recording ({bytes.Length / 1024} KB): not kept"
                    + (code == null ? " (no friend code)" : " (no mailbox)"));
                return;
            }
            try
            {
                ReplayFile file = ReplayFile.Read(new MemoryStream(bytes));
                file.OwnSeat = seat;
                Directory.CreateDirectory(Mailbox);
                string name = Guid.NewGuid().ToString("N") + ".mphreplay";
                string temp = Path.Combine(Mailbox, name + ".part");
                using (FileStream fs = File.Create(temp))
                {
                    file.Write(fs);
                }
                File.Move(temp, Path.Combine(Mailbox, name));
                // what the mailbox shows before the download: "replay <code> <file> <seconds> <mode> <arena, may hold spaces>"
                string arena = MphRecomp.Multiplayer.MatchArenas.Find(file.Settings.Arena)?.InGameName ?? file.Settings.Arena;
                ServerLine($"replay {code} {name} {file.Steps.Count / 60} {file.Settings.Mode} {arena}");
                Log($"replay: seat {seat} didn't get its recording: {bytes.Length / 1024} KB to its replay mailbox ({name})");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException
                or EndOfStreamException or ArgumentException)
            {
                Log($"replay: seat {seat} didn't get its recording, and it didn't go to its mailbox: " + ex.Message);
            }
        }

        // the last match's link closed: at once, or once its recording has gone out (up to waitMs)
        internal static void CloseReplayLink(int waitMs)
        {
            ReplayLink? link = _replayLink;
            _replayLink = null;
            if (link == null)
            {
                return;
            }
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (link.Sending && sw.ElapsedMilliseconds < waitMs)
            {
                System.Threading.Thread.Sleep(50);
            }
            if (link.Sending)
            {
                Log("replay: the last recording was still going out: stopped");
            }
            link.Dispose();
        }
    }
}
