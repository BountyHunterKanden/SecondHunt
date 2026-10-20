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

            private JoinerReplay(ReplayLink link, int index, int seat)
            {
                (_link, _index, _seat) = (link, index, seat);
                _link.ReplayReceived += bytes =>
                {
                    _bytes = bytes;
                    _arrived.Set();
                };
            }

            // right after the joiner's CampaignHost.StartMatch, as the app does it
            public static JoinerReplay Begin(NetMatchSession net, int index, int seat, OnlineIdentity me, string name)
            {
                var replay = new JoinerReplay(new ReplayLink(net), index, seat);
                replay._link.SendIdentity(new ReplaySeatIdentity(seat, me.FriendCode, name, null), wantReplay: true);
                return replay;
            }

            // after the match: the match host's recording (up to 15 s), saved and played back in a fresh process
            public void Finish()
            {
                var clock = Stopwatch.StartNew();
                if (!_arrived.Wait(15000) || _bytes == null)
                {
                    Console.WriteLine($"REPLAY joiner {_index}: NONE: the match host's recording didn't come in 15 s");
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

            public void Dispose() => _link.Dispose();
        }
    }
}
