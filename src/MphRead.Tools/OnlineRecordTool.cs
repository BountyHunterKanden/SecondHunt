using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using MphRead;
using MphRecomp.Frontend;
using MphRecomp.Multiplayer;
using MphRecomp.Multiplayer.Net;
using MphRecomp.Online;
using MphRecomp.Replay;

namespace MphRead
{
    // -onlinerecordtest [server=]   T3 (owner note N30): head to head counted once on the server, the same on both sides.
    //                               A local 0.7.0 server (spawned unless server= names one), the menus' profile hosts an
    //                               online match a PC joiner joins (real relay, real replay link: the joiner sends its seat
    //                               identity); the match "ends" with the host first; the menus open RESULTS as the host;
    //                               PASS = the server's record reads 1-0 from the host's side and 0-1 from the joiner's,
    //                               nothing left queued; then a LAN result (no online game) waits in the queue until the
    //                               menus connect, and the record moves to 2 rounds on both sides.
    internal static class OnlineRecordTool
    {
        private static T Wait<T>(System.Threading.Tasks.Task<T> task) => task.GetAwaiter().GetResult();
        private static string RomKey => LanSession.RomKeyFor(Paths.MphKey);

        public static void Test(string[] args)
        {
            if (!ToolPaths.UseDesktopExtraction()) return;
            Dictionary<string, string> a = args.Skip(1).Select(x => x.Split('=', 2)).Where(p => p.Length == 2).ToDictionary(p => p[0], p => p[1]);
            string dir = Path.Combine(Path.GetTempPath(), "online-record-test-" + Guid.NewGuid().ToString("N")[..6]);
            Directory.CreateDirectory(dir);
            Process? server = null;
            var serverOut = new List<string>();
            string? fail = null;
            OnlineMatch? host = null, joiner = null;
            ReplayLink? hostLink = null, joinLink = null;
            try
            {
                Uri uri;
                if (a.TryGetValue("server", out string? given)) uri = new Uri(given);
                else
                {
                    int port = FreeTcp(), relay = FreeUdp();
                    string dll = Path.GetFullPath(Path.Combine(typeof(OnlineRecordTool).Assembly.Location, "..", "..", "..", "..", "..",
                        "SecondHunt.Server", "bin", "Debug", "net9.0", "SecondHuntServer.dll"));
                    var psi = new ProcessStartInfo(Environment.ProcessPath ?? "dotnet",
                        $"\"{dll}\" --urls http://127.0.0.1:{port} --db \"{Path.Combine(dir, "server.db")}\" --relay-port {relay} --relay-host 127.0.0.1")
                    {
                        UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = dir,
                    };
                    server = Process.Start(psi)!;
                    server.OutputDataReceived += (_, e) => { if (e.Data != null) lock (serverOut) serverOut.Add(e.Data); };
                    server.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (serverOut) serverOut.Add("stderr: " + e.Data); };
                    server.BeginOutputReadLine();
                    server.BeginErrorReadLine();
                    if (!WaitHealthy(port))
                    {
                        fail = "the local server didn't come up";
                        return;
                    }
                    uri = new Uri($"ws://127.0.0.1:{port}/v1/ws");
                }
                Console.WriteLine($"onlinerecordtest: {uri}");
                RecompMenus.OnlineServerOverride = uri.ToString();
                RecompMenus.OnlineNoticeSkipped = true;
                FrontendSession s = FrontendSession.Load(Paths.FileSystem, settingsPath: Path.Combine(dir, "recomp_settings.json"));
                OnlineIdentity hostId = OnlineIdentity.LoadOrCreate(dir); // (the menus' own profile hosts)
                OnlineIdentity joinId = OnlineIdentity.Create();
                OnlineClient hc = Wait(OnlineClient.ConnectAsync(uri, hostId, "RECHOST", "onlinerecordtest"));
                OnlineClient jc = Wait(OnlineClient.ConnectAsync(uri, joinId, "RECJOIN", "onlinerecordtest"));
                if (!hc.ServerAtLeast(MatchResultReq.SinceServer))
                {
                    fail = $"the server is {hc.ServerVersion}, head to head needs {MatchResultReq.SinceServer}";
                    return;
                }
                MatchSettings template = MatchSettings.Quick(GameMode.Battle, "MP3 PROVING GROUND", Hunter.Samus, bots: 0);
                host = Wait(OnlineMatch.HostAsync(hc, uri, template, RomKey, maxPlayers: 2));
                joiner = Wait(OnlineMatch.JoinAsync(jc, uri, RomKey, host.Game.Id));
                if (!joiner.Net.Join(Hunter.Kanden, "RECJOIN", 3, timeoutMs: 15000))
                {
                    fail = "the joiner got no seat: " + joiner.Net.Status;
                    return;
                }
                if (!Until(() => { host.Net.LobbyTick(true); return host.Net.Joined >= 1; }, 10))
                {
                    fail = "the host never saw the joiner";
                    return;
                }
                host.Start();
                if (!joiner.Net.WaitForStart(5000))
                {
                    fail = "the joiner never got START";
                    return;
                }
                // the replay link of each side (as the match screen makes them; the host's last, so it's ReplayLink.Latest)
                joinLink = new ReplayLink(joiner.Net);
                hostLink = new ReplayLink(host.Net);
                int joinSeat = joiner.Net.Settings!.LocalSeat;
                joinLink.SendIdentity(new ReplaySeatIdentity(joinSeat, joinId.FriendCode, "RECJOIN", null, 0), wantReplay: false);
                if (!Until(() => hostLink.Identities.Any(i => i.FriendCode == joinId.FriendCode), 10))
                {
                    fail = "the joiner's seat identity never reached the host";
                    return;
                }
                Thread.Sleep(1000); // (the server marks the game started: StartGameReq is sent without waiting)

                // the match ends: the host won; the menus open RESULTS as the host
                host.EndMatch();
                joiner.EndMatch();
                OnlineMatch.Current = host;
                MatchSettings played = host.Net.Settings!;
                s.Multiplayer.ArmOnlineMatch(played, RecompMenus.OnlinePages.Item1);
                MatchResults.Last = Results(played, 0, joinSeat, hostPlace: 0);
                s.Start(MultiplayerMenu.ResultsPage);
                if (!Until(() => { s.Tick(); return s.Menu.Page?.Index == MultiplayerMenu.ResultsPage && s.Menu.PendingPage == -1; }, 10))
                {
                    fail = $"the menus didn't open on RESULTS (page {s.Menu.Page?.Index})";
                    return;
                }
                HeadToHeadInfo? jh = null, hj = null;
                bool counted = Until(() =>
                {
                    s.Tick();
                    jh = Wait(jc.RequestAsync<PlayerMsg>(new GetPlayerReq { Code = hostId.FriendCode })).Player.Record;
                    return jh != null;
                }, 10);
                hj = Wait(hc.RequestAsync<PlayerMsg>(new GetPlayerReq { Code = joinId.FriendCode })).Player.Record;
                Console.WriteLine($"  online: joiner sees {Show(jh)}, host sees {Show(hj)}, queued {RecompMenus.PendingResultCount(dir)}");
                if (!counted) fail = "the host's report never reached the server";
                else if (jh is not { Rounds: 1, Victories: 0, Last: HeadToHeadInfo.Defeat } || hj is not { Rounds: 1, Victories: 1, Last: HeadToHeadInfo.Victory })
                    fail = "the two sides' records don't read 1-0 / 0-1";
                else if (RecompMenus.PendingResultCount(dir) != 0) fail = "a sent report is still queued";
                if (fail != null) return;

                // a LAN match (no online game): queued while the menus have no connection, sent when they connect
                OnlineMatch.Current = null;
                MatchResults.Last = Results(played, 0, joinSeat, hostPlace: 1); // (the joiner won this one)
                s.Recomp.OnlineReportMatchResult();
                int queued = RecompMenus.PendingResultCount(dir);
                s.Recomp.OpenOnline(s.Menu);
                bool lan = Until(() =>
                {
                    s.Tick();
                    jh = Wait(jc.RequestAsync<PlayerMsg>(new GetPlayerReq { Code = hostId.FriendCode })).Player.Record;
                    return jh is { Rounds: 2 };
                }, 20);
                // (the menus' connection is the host's identity: it logged the PC host client out; a fresh one reads its side)
                OnlineClient hc2 = Wait(OnlineClient.ConnectAsync(uri, hostId, "RECHOST", "onlinerecordtest"));
                hj = Wait(hc2.RequestAsync<PlayerMsg>(new GetPlayerReq { Code = joinId.FriendCode })).Player.Record;
                Console.WriteLine($"  LAN: queued {queued} before connecting; joiner sees {Show(jh)}, host sees {Show(hj)}, queued {RecompMenus.PendingResultCount(dir)}");
                if (queued != 1) fail = $"the LAN report wasn't queued ({queued})";
                else if (!lan) fail = "the queued LAN report never reached the server";
                else if (jh is not { Rounds: 2, Victories: 1, Last: HeadToHeadInfo.Victory } || hj is not { Rounds: 2, Victories: 1, Last: HeadToHeadInfo.Defeat })
                    fail = "after the LAN match the two sides' records don't read 1-1";
                else if (!Until(() => { s.Tick(); return RecompMenus.PendingResultCount(dir) == 0; }, 5)) fail = "the LAN report stayed queued";
            }
            catch (Exception ex)
            {
                fail = $"{ex.GetType().Name}: {ex.Message}";
            }
            finally
            {
                hostLink?.Dispose();
                joinLink?.Dispose();
                joiner?.Dispose();
                host?.Dispose();
                OnlineMatch.Current = null;
                MatchResults.Last = null;
                RecompMenus.OnlineServerOverride = null;
                Thread.Sleep(300);
                if (server != null)
                {
                    if (fail != null)
                    {
                        lock (serverOut) foreach (string line in serverOut.TakeLast(30)) Console.WriteLine("  [server] " + line);
                    }
                    try { server.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                }
                Console.WriteLine(fail == null
                    ? "PASS onlinerecordtest: the host's RESULTS counts the match once on the server, both sides read the same record; a LAN result waits in the queue and goes up on the next connection"
                    : "FAIL onlinerecordtest: " + fail);
                Environment.ExitCode = fail == null ? 0 : 1;
            }
        }

        private static string Show(HeadToHeadInfo? h) => h == null ? "none" : $"{h.Rounds} rounds, {h.Victories} won, last {h.Last}";

        private static MatchResults Results(MatchSettings played, int hostSeat, int joinSeat, int hostPlace) => new()
        {
            Mode = played.Mode, LocalSeat = hostSeat,
            Rows = new[] { (hostSeat, hostPlace), (joinSeat, 1 - hostPlace) }.OrderBy(r => r.Item2)
                .Select(r => new MatchResults.Player(r.Item1, played.Players[r.Item1].Hunter, played.Players[r.Item1].Nickname ?? "", false, 0,
                    r.Item2, 10 - r.Item2, 0, 10 - r.Item2, r.Item2, 0, 0, 0, 0, 0, 0, 0, 0, 50, 0)).ToArray(),
        };

        private static bool Until(Func<bool> done, int seconds)
        {
            var clock = Stopwatch.StartNew();
            while (!done())
            {
                if (clock.Elapsed.TotalSeconds > seconds) return false;
                Thread.Sleep(50);
            }
            return true;
        }

        private static int FreeTcp()
        {
            var l = new TcpListener(IPAddress.Loopback, 0);
            l.Start();
            int port = ((IPEndPoint)l.LocalEndpoint).Port;
            l.Stop();
            return port;
        }

        private static int FreeUdp()
        {
            using var u = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            return ((IPEndPoint)u.Client.LocalEndPoint!).Port;
        }

        private static bool WaitHealthy(int port)
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(1) };
            return Until(() =>
            {
                try
                {
                    return Wait(http.GetStringAsync($"http://127.0.0.1:{port}/v1/health")).StartsWith("ok");
                }
                catch (Exception ex) when (ex is HttpRequestException or System.Threading.Tasks.TaskCanceledException)
                {
                    return false;
                }
            }, 15);
        }
    }
}
