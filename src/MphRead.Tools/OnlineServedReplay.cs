using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using MphRecomp.Multiplayer.Net;
using MphRecomp.Online;
using MphRecomp.Replay;

namespace MphRead
{
    // served=hub replays=1 (the default with served=hub; docs/DEDICATED_SERVERS.md section 4): each joiner plays the app's guest
    // side of the replay link (MatchReplay.cs StartReplayLink): its identity to the match host as its match starts, asking for
    // the recording; once the match is over it waits for the match host's file, saves it as its own copy (OwnSeat = its seat)
    // and plays it back in a fresh process (-replayplay), which must be exact to the last step. The leaver (leave=) and a
    // rejoin / wifi= joiner aren't checked (nobody owes them the file). ServedVerdict wants one REPLAY ... exact per joiner.
    internal static partial class OnlineMatchTool
    {
        private sealed class JoinerReplay : IDisposable
        {
            private readonly ReplayLink _link;
            private readonly int _index, _seat;
            private readonly ManualResetEventSlim _arrived = new();
            private byte[]? _bytes;
            // live=1 (the default; instant seek always [67c67ed8]): the match host's recording streamed in as it's made, into
            // this joiner's mirror as the app's (MatchReplay.StartReplayLink), so its keyframes could start from the opening
            private readonly ReplayLiveMirror? _mirror;
            private readonly string? _mirrorData;
            private readonly List<string> _mirrorLog = new();
            private string? _began;
            private long _liveBytes;
            private int _liveBlobs;

            private JoinerReplay(ReplayLink link, int index, int seat, bool live)
            {
                (_link, _index, _seat) = (link, index, seat);
                _link.ReplayReceived += bytes =>
                {
                    _bytes = bytes;
                    _arrived.Set();
                };
                if (live)
                {
                    _mirrorData = Path.Combine(Path.GetTempPath(), "secondhunt_served_replays", $"live{index}_{Environment.ProcessId}");
                    _mirror = new ReplayLiveMirror(_mirrorData);
                    _mirror.Log += l => { lock (_mirrorLog) _mirrorLog.Add(l); };
                    _mirror.Begun += (_, sidecar) => _began = sidecar;
                    _link.LiveReceived += blob =>
                    {
                        Interlocked.Add(ref _liveBytes, blob.Length);
                        Interlocked.Increment(ref _liveBlobs);
                    };
                    _link.LiveReceived += _mirror.Receive;
                }
            }

            // right after the joiner's CampaignHost.StartMatch, as the app does it
            public static JoinerReplay Begin(NetMatchSession net, int index, int seat, OnlineIdentity me, string name, bool live)
            {
                var replay = new JoinerReplay(new ReplayLink(net), index, seat, live);
                replay._link.SendIdentity(new ReplaySeatIdentity(seat, me.FriendCode, name, null), wantReplay: true, wantLive: live);
                return replay;
            }

            // the stream against the file that came: whole (or filled from the file), never stopped, under the saved file's key
            private void CheckLive(ReplayFile file)
            {
                if (_mirror == null) return;
                _mirror.Complete(file);
                List<string> log;
                lock (_mirrorLog) log = _mirrorLog.ToList();
                foreach (string l in log) Console.WriteLine($"joiner {_index}: live: {l}");
                string? wrong = _began == null ? "the stream never began"
                    : log.Any(l => l.StartsWith("live keyframes stopped", StringComparison.Ordinal)) ? "the stream stopped"
                    : !log.Any(l => l.Contains("streamed in whole", StringComparison.Ordinal) || l.StartsWith("filled the stream", StringComparison.Ordinal))
                        ? "the stream didn't end whole"
                    : _began != ReplayPrep.SidecarPath(_mirrorData!, file) ? "the stream's key isn't the saved file's"
                    : null;
                double seconds = file.Steps.Count / 60.0;
                Console.WriteLine($"LIVE joiner {_index}: {wrong ?? "OK"}: {_liveBlobs} blobs, {_liveBytes / 1024} KB"
                    + (seconds > 0 ? $", {_liveBytes / seconds:0} bytes a second" : ""));
                _mirror.End();
                try
                {
                    Directory.Delete(_mirrorData!, recursive: true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                }
            }

            // after the match: the match host's recording, saved and played back in a fresh process. It waits as the app's
            // screen hold does (MatchReplay.cs ReplayLinkHold): while pieces keep coming, up to 10 s for the first, 8 s between
            // pieces, 120 s in all (a fixed 15 s cut off 8 seats' recordings: the host paces every seat's together, ~24 s)
            public void Finish()
            {
                var clock = Stopwatch.StartNew();
                long start = Environment.TickCount64;
                while (!_arrived.Wait(50) && clock.ElapsedMilliseconds < 120000)
                {
                    long last = _link.LastPieceAt, now = Environment.TickCount64;
                    if (last >= start ? now - last > 8000 : now - start > 10000) break;
                }
                if (!_arrived.IsSet || _bytes == null)
                {
                    Console.WriteLine($"REPLAY joiner {_index}: NONE: the match host's recording didn't come in "
                        + $"({clock.ElapsedMilliseconds / 1000} s, received {_link.Progress})");
                    return;
                }
                string verdict;
                try
                {
                    ReplayFile file = ReplayFile.Read(new MemoryStream(_bytes));
                    file.OwnSeat = _seat;
                    string dir = Path.Combine(Path.GetTempPath(), "secondhunt_served_replays");
                    Directory.CreateDirectory(dir);
                    string path = Path.Combine(dir, $"served_joiner{_index}_{Environment.ProcessId}.mphreplay");
                    file.Save(path);
                    Console.WriteLine($"joiner {_index}: the match host's recording in {clock.ElapsedMilliseconds} ms: {_bytes.Length / 1024} KB, "
                        + $"{file.Steps.Count} steps, {file.NetEvents.Count} network events, served {file.Settings.ServedHost}, "
                        + $"identities {string.Join(",", file.Identities.Select(i => $"{i.Seat}:{i.Name}"))}, places {file.Places.Count} -> {path}");
                    CheckLive(file);
                    if (!file.Settings.ServedHost)
                    {
                        Console.WriteLine($"REPLAY joiner {_index}: WRONG: the file doesn't read back as a served match's");
                        return;
                    }
                    if (file.Complete == _cutShort)
                    {
                        Console.WriteLine($"REPLAY joiner {_index}: WRONG: {(file.Complete ? "complete" : "cut short")}, "
                            + $"{(_cutShort ? "it left mid-match" : "it played to the end")}");
                        return;
                    }
                    var output = new List<string>();
                    string dll = typeof(OnlineMatchTool).Assembly.Location;
                    using Process p = Start(Environment.ProcessPath!, $"\"{dll}\" -replayplay \"{path}\"", output);
                    p.WaitForExit();
                    p.WaitForExit(); // (the end of its output)
                    lock (output)
                    {
                        verdict = output.LastOrDefault(l => l.Trim().Length > 0)?.Trim() ?? "(no output)";
                        if (p.ExitCode != 0) verdict = $"[exit {p.ExitCode}] {verdict}";
                    }
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
                {
                    verdict = "UNREADABLE: " + ex.Message;
                }
                Console.WriteLine($"REPLAY joiner {_index}: {verdict}");
            }

            // leave= (0.3.6): leaving mid-match, the recording so far (MatchReplay.EndReplay)
            private bool _cutShort;

            public void AskCutShort(long frame)
            {
                _cutShort = true;
                _link.RequestReplay();
                Console.WriteLine($"joiner {_index}: left at frame {frame}, asked for the recording so far");
            }

            public void Dispose()
            {
                _mirror?.End();
                _link.Dispose();
            }
        }
    }
}
