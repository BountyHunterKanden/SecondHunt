using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using MphRecomp.Frontend;
using MphRecomp.Online;

namespace MphRead
{
    // Server hardening 0.10.3: the hub's auto-reconnect (OnlineMenus.Reconnect.cs) and the restart drain notice (HubDrain.cs),
    // against a LOCAL server this tool's caller runs (never the live one).
    //   -onlinereconnecttest server=ws://127.0.0.1:18080/v1/ws drain=<the server's db dir>/drain kill=<a marker file>
    // 1 a PC leader befriends the menus and invites them to a party: the menus join it (a yes);
    // 2 the menus' connection drops (closed here, the server stays up): "reconnecting...", back online, in the same party again;
    // 3 a restart line in the drain file: the menus hear the restart notice (a message; no match running, so it drains at once and
    //   the message says "restarting now");
    // 4 the tool writes the kill= marker and the caller kills the server and starts it again a few seconds later: the menus
    //   show "reconnecting...", come back by themselves and stay on the hub (the restarted server lost the party: no error).
    internal static class OnlineReconnectTool
    {
        private static T Wait<T>(System.Threading.Tasks.Task<T> task) => task.GetAwaiter().GetResult();

        private static bool RunUntil(FrontendSession s, Func<bool> done, int seconds)
        {
            var clock = Stopwatch.StartNew();
            while (!done() && clock.Elapsed.TotalSeconds < seconds)
            {
                s.Tick();
                Thread.Sleep(16);
            }
            return done();
        }

        // yes in an Ask box (A focuses YES, A again presses it)
        private static void Yes(FrontendSession s)
        {
            s.Press(MenuKeys.A);
            Run(s, 3);
            s.Press(MenuKeys.A);
            Run(s, 40);
        }

        public static void Test(string[] args)
        {
            if (!ToolPaths.UseDesktopExtraction()) return;
            Dictionary<string, string> a = args.Skip(1).Select(x => x.Split('=', 2)).Where(p => p.Length == 2).ToDictionary(p => p[0], p => p[1]);
            if (!a.TryGetValue("server", out string? server) || !TestServer(server) || !a.TryGetValue("drain", out string? drain)
                || !a.TryGetValue("kill", out string? kill))
            {
                Console.WriteLine("FAIL onlinereconnecttest: needs server=ws://127.0.0.1:<port>/v1/ws (or the test hub's :8790) drain=<path> kill=<path>");
                return;
            }
            var uri = new Uri(server);
            RecompMenus.OnlineServerOverride = uri.ToString();
            RecompMenus.OnlineNoticeSkipped = true;
            Console.WriteLine($"onlinereconnecttest: {uri}");
            string dir = Path.Combine(Path.GetTempPath(), "online-reconnect-test-" + Guid.NewGuid().ToString("N")[..6]);
            Directory.CreateDirectory(dir);
            FrontendSession s = FrontendSession.Load(Paths.FileSystem, settingsPath: Path.Combine(dir, "recomp_settings.json"));
            OnlineClient lead = Wait(OnlineClient.ConnectAsync(uri, OnlineIdentity.Create(), "RECLEAD", "onlinereconnecttest"));
            string? fail = null;
            try
            {
                (int hub, _, _) = RecompMenus.OnlinePages;
                s.Recomp.OpenOnline(s.Menu);
                if (!RunUntil(s, () => s.Recomp.OnlineClientForTools != null && s.Recomp.OnlineMeCard != null, 15))
                {
                    fail = "the menus didn't connect";
                    return;
                }
                string me = s.Recomp.OnlineMeCard!.Code;
                // 1: friends both ways, then the party
                Wait(lead.RequestAsync<OkMsg>(new AddReq { Code = me, Kind = LinkKind.Friend }));
                Wait(s.Recomp.OnlineClientForTools!.RequestAsync<OkMsg>(new AddReq { Code = lead.Me.Code, Kind = LinkKind.Friend }));
                Wait(lead.RequestAsync<OkMsg>(new PartyInviteReq { Code = me }));
                RunUntil(s, () => s.Popup?.IsOpen == true, 5);
                Yes(s);
                if (!RunUntil(s, () => s.Recomp.OnlineParty?.Members.Count == 2, 10))
                {
                    fail = "the menus didn't join the leader's party";
                    return;
                }
                string party = s.Recomp.OnlineParty!.Id;
                Console.WriteLine($"  1 ok: the menus ({me}) are in {lead.Me.Name}'s party {party}");

                // 2: the menus' connection drops, the server stays up
                var clock = Stopwatch.StartNew();
                Wait(s.Recomp.OnlineClientForTools!.DisposeAsync().AsTask().ContinueWith(_ => 0));
                bool saw = RunUntil(s, () => s.Recomp.OnlineReconnecting, 5);
                bool back = RunUntil(s, () => s.Recomp.OnlineClientForTools?.Connected == true && !s.Recomp.OnlineReconnecting, 20);
                bool again = RunUntil(s, () => s.Recomp.OnlineParty is PartyInfo p && p.Id == party && p.Members.Count == 2, 10);
                if (!saw || !back || !again)
                {
                    fail = $"a dropped connection: reconnecting {saw}, back {back}, in the party again {again} ({s.Recomp.OnlineParty?.Id})";
                    return;
                }
                Console.WriteLine($"  2 ok: dropped -> reconnecting... -> back in {clock.Elapsed.TotalSeconds:0.0} s, in party {party} again");

                // 3: the restart notice
                File.WriteAllText(drain, $"restart {DateTimeOffset.UtcNow.ToUnixTimeSeconds()} 30\n");
                if (!RunUntil(s, () => s.Recomp.OnlineClientForTools?.Notice?.Kind is NoticeKind.Restart or NoticeKind.Drained && s.Popup?.IsOpen == true, 8))
                {
                    fail = $"the restart notice didn't show (notice {s.Recomp.OnlineClientForTools?.Notice?.Kind}, popup {s.Popup?.IsOpen})";
                    return;
                }
                Console.WriteLine("  3 ok: the restart notice shows: " + s.Recomp.OnlineClientForTools!.Notice!.Text);
                s.Popup?.Close();

                // 4: the server is killed and started again (the caller, on the marker)
                File.WriteAllText(kill, "kill\n");
                clock.Restart();
                saw = RunUntil(s, () => s.Recomp.OnlineReconnecting, 20);
                Console.WriteLine($"  the server went: reconnecting {saw} after {clock.Elapsed.TotalSeconds:0.0} s");
                back = RunUntil(s, () => s.Recomp.OnlineClientForTools?.Connected == true && !s.Recomp.OnlineReconnecting, 60);
                Run(s, 120);
                bool onHub = s.Menu.Page?.Index == hub;
                if (!saw || !back || !onHub || s.Popup?.IsOpen == true)
                {
                    fail = $"the server restart: reconnecting {saw}, back {back}, on the hub {onHub} (page {s.Menu.Page?.Index}), popup {s.Popup?.IsOpen}";
                    return;
                }
                Console.WriteLine($"  4 ok: server killed + restarted -> reconnecting... -> back by itself in {clock.Elapsed.TotalSeconds:0.0} s, "
                    + $"on the hub, party {(s.Recomp.OnlineParty == null ? "gone (the server forgot it)" : s.Recomp.OnlineParty.Id)}");
            }
            catch (Exception ex)
            {
                fail ??= ex.GetType().Name + ": " + ex.Message;
            }
            finally
            {
                try
                {
                    Wait(lead.DisposeAsync().AsTask().ContinueWith(_ => 0));
                    Directory.Delete(dir, recursive: true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                }
                Console.WriteLine(fail == null ? "PASS onlinereconnecttest: party drop + rejoin, restart notice, server kill + restart"
                    : "FAIL onlinereconnecttest: " + fail);
            }
        }

        // a local server, or the test hub (secondhunt-test, :8790; never the live one, which these tests drain and kill)
        private static bool TestServer(string server) => Uri.TryCreate(server, UriKind.Absolute, out Uri? u)
            && (u.Host == "127.0.0.1" || u.Port == 8790);

        // -onlinefloodtest server=<a local server or the test hub>: the request flood limit (server 0.10.3, HubFlood.cs) from
        // outside: a calm client (a login's burst of 30, then one a second) is never refused; one firing hundreds at once
        // hears "slow_down" and is dropped, and the calm one stays on.
        public static void FloodTest(string[] args)
        {
            Dictionary<string, string> a = args.Skip(1).Select(x => x.Split('=', 2)).Where(p => p.Length == 2).ToDictionary(p => p[0], p => p[1]);
            if (!a.TryGetValue("server", out string? server) || !TestServer(server))
            {
                Console.WriteLine("FAIL onlinefloodtest: needs server=ws://127.0.0.1:<port>/v1/ws or the test hub's :8790");
                return;
            }
            var uri = new Uri(server);
            Console.WriteLine($"onlinefloodtest: {uri}");
            OnlineClient calm = Wait(OnlineClient.ConnectAsync(uri, OnlineIdentity.Create(), "FLOODCALM", "onlinefloodtest"));
            OnlineClient flood = Wait(OnlineClient.ConnectAsync(uri, OnlineIdentity.Create(), "FLOODBAD", "onlinefloodtest"));
            static async System.Threading.Tasks.Task<string?> CodeAsync(OnlineClient c)
            {
                try
                {
                    await c.RequestAsync<OkMsg>(new PingReq { Ms = 40 });
                    return null;
                }
                catch (OnlineException ex)
                {
                    return ex.Code;
                }
                catch (Exception ex) when (ex is System.Net.WebSockets.WebSocketException or OperationCanceledException
                    or ObjectDisposedException or InvalidOperationException)
                {
                    return "closed";
                }
            }
            string? Code(OnlineClient c) => Wait(CodeAsync(c));
            int calmRefused = Enumerable.Range(0, 30).Count(_ => Code(calm) == "slow_down");
            int slowDown = 0, sent = 0;
            var clock = Stopwatch.StartNew();
            for (int round = 0; round < 10 && flood.Connected; round++)
            {
                string?[] codes = Wait(System.Threading.Tasks.Task.WhenAll(Enumerable.Range(0, 200).Select(_ => CodeAsync(flood))));
                sent += codes.Length;
                slowDown += codes.Count(c => c == "slow_down");
                calmRefused += Code(calm) == "slow_down" ? 1 : 0;
            }
            Thread.Sleep(500);
            double secs = clock.Elapsed.TotalSeconds;
            bool dropped = !flood.Connected && flood.CloseReason?.Contains("too many requests") == true;
            string? after = Code(calm);
            Console.WriteLine($"  flood: {sent} sent in {secs:0.0} s, {slowDown} told slow_down, dropped {dropped} ({flood.CloseReason})");
            Console.WriteLine($"  calm: refused {calmRefused}, still on {calm.Connected}, a ping after: {after ?? "ok"}");
            bool pass = slowDown > 0 && dropped && calmRefused == 0 && calm.Connected && after == null;
            Wait(calm.DisposeAsync().AsTask().ContinueWith(_ => 0));
            Wait(flood.DisposeAsync().AsTask().ContinueWith(_ => 0));
            Console.WriteLine(pass ? "PASS onlinefloodtest: the flood is refused and dropped, the calm client never is" : "FAIL onlinefloodtest");
        }

        private static void Run(FrontendSession s, int frames)
        {
            for (int f = 0; f < frames; f++)
            {
                s.Tick();
                Thread.Sleep(16);
            }
        }
    }
}
