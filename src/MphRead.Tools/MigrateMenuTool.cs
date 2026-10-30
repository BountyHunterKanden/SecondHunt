using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using MphRecomp.Frontend;
using MphRecomp.Multiplayer;
using MphRecomp.Multiplayer.Net;
using MphRecomp.Online;

namespace MphRead
{
    // O5b host migration through the ONLINE PLAY menus (Netcode thread), live (server 0.5.0+):
    //   -migratemenutest [server=wss://...]
    // A PC host opens a game with a bot; PC ONE joins first (relay slot 1), PC TWO second; START; the host quits (BYE +
    // leave). PASS = PC ONE hosts now (its card is the game's host), PC TWO follows slot 1; the menus, with PC ONE's ended
    // match on RESULTS, PLAY AGAIN -> PC ONE's lobby as its host, the server lists the game again under PC ONE, PC TWO comes
    // back (as Spire), a new PC THREE joins (finds the new host's slot), the menus' START plays the rematch: 4 seats (PC ONE,
    // PC TWO, PC THREE, the bot), no old host.
    internal static class MigrateMenuTool
    {
        private static string RomKey => LanSession.RomKeyFor(Paths.MphKey);
        private static T Wait<T>(System.Threading.Tasks.Task<T> task) => task.GetAwaiter().GetResult();

        private static bool Until(Func<bool> done, int seconds, Action? tick = null)
        {
            var clock = Stopwatch.StartNew();
            while (!done() && clock.Elapsed.TotalSeconds < seconds)
            {
                tick?.Invoke();
                Thread.Sleep(16);
            }
            return done();
        }

        public static void Test(string[] args)
        {
            if (!ToolPaths.UseDesktopExtraction()) return;
            Dictionary<string, string> a = args.Skip(1).Select(x => x.Split('=', 2)).Where(p => p.Length == 2)
                .ToDictionary(p => p[0], p => p[1]);
            var uri = new Uri(a.GetValueOrDefault("server", RecompMenus.OnlineServer));
            Console.WriteLine($"migratemenutest: {uri}");
            var ids = new List<(OnlineIdentity Id, string Name)>
            {
                (OnlineIdentity.Create(), "PCHOST"), (OnlineIdentity.Create(), "PCONE"), (OnlineIdentity.Create(), "PCTWO"),
                (OnlineIdentity.Create(), "PCLOOK"),
                (OnlineIdentity.Create(), "PCTHREE"),
            };
            string? fail = null;
            OnlineMatch? host = null, one = null, two = null, three = null;
            var requests = new List<FrontendRequest>();
            try
            {
                OnlineClient hc = Wait(OnlineClient.ConnectAsync(uri, ids[0].Id, "PCHOST", "migratemenutest"));
                if (!hc.ServerAtLeast(OnlineMatch.MigrateSinceServer))
                {
                    fail = $"the server is older than {OnlineMatch.MigrateSinceServer}";
                    return;
                }
                MatchSettings template = MatchSettings.Quick(GameMode.Battle, "Combat Hall", Hunter.Kanden, 0);
                template.Players[0].Nickname = "PCHOST";
                template.Players.Add(new MatchPlayer { Hunter = Hunter.Noxus, Bot = true, BotLevel = 1 });
                template.TimeLimitSeconds = 120;
                host = Wait(OnlineMatch.HostAsync(hc, uri, template, RomKey, 3));
                Console.WriteLine($"  host {hc.Me} opened game {host.Game.Id}");
                OnlineClient c1 = Wait(OnlineClient.ConnectAsync(uri, ids[1].Id, "PCONE", "migratemenutest"));
                one = Wait(OnlineMatch.JoinAsync(c1, uri, RomKey, host.Game.Id));
                if (!one.Net.Join(Hunter.Sylux, "PCONE", 3, timeoutMs: 15000))
                {
                    fail = "PC ONE got no seat: " + one.Net.Status;
                    return;
                }
                OnlineClient c2 = Wait(OnlineClient.ConnectAsync(uri, ids[2].Id, "PCTWO", "migratemenutest"));
                two = Wait(OnlineMatch.JoinAsync(c2, uri, RomKey, host.Game.Id));
                if (!two.Net.Join(Hunter.Weavel, "PCTWO", 3, timeoutMs: 15000))
                {
                    fail = "PC TWO got no seat: " + two.Net.Status;
                    return;
                }
                OnlineMatch h = host;
                if (!Until(() => h.Net.Joined >= 2, 10))
                {
                    fail = $"the host saw {host.Net.Joined} of 2 joiners";
                    return;
                }
                host.Start();
                if (!one.Net.WaitForStart(5000) || !two.Net.WaitForStart(5000))
                {
                    fail = "a joiner never got START";
                    return;
                }
                Console.WriteLine($"  started: PC ONE relay slot {one.Relay.LocalSlot}, PC TWO relay slot {two.Relay.LocalSlot}");
                Thread.Sleep(1000); // (the server marks the game started)
                OnlineMatch.Current = one; // (PC ONE's match screen: these menus are PC ONE's)
                one.TakeScreenHold();
                host.Dispose(); // the host quits: BYE + leave the game
                host = null;
                OnlineMatch o1 = one, o2 = two;
                if (!Until(() => o1.Net.IsHost && o2.Net.HostSlot == o1.Relay.LocalSlot, 20))
                {
                    fail = $"no takeover: PC ONE host {one.Net.IsHost} ({one.Net.Status}), PC TWO follows slot {two.Net.HostSlot} ({two.Net.Status})";
                    return;
                }
                string oneCode = c1.Me!.Code;
                if (one.Game.Host.Code != oneCode || two.Game.Host.Code != oneCode)
                {
                    fail = $"the game's host card didn't move to PC ONE (PC ONE sees {one.Game.Host.Name}, PC TWO sees {two.Game.Host.Name})";
                    return;
                }
                Console.WriteLine($"  the host quit: PC ONE hosts ({one.Net.Status}), PC TWO follows slot {two.Net.HostSlot}");
                Thread.Sleep(500); // (PC TWO's REHOST)
                // both match screens end: RESULTS
                MatchSettings played = one.Net.Settings!;
                one.EndMatch();
                two.EndMatch();
                RecompMenus.OnlineNoticeSkipped = true;
                string dir = Path.Combine(Path.GetTempPath(), "migrate-menu-test-" + Guid.NewGuid().ToString("N")[..6]);
                Directory.CreateDirectory(dir);
                FrontendSession menus = FrontendSession.Load(Paths.FileSystem, settingsPath: Path.Combine(dir, "recomp_settings.json"));
                menus.Request = r => requests.Add(r);
                // (as the menus' Play does at the match's start: RESULTS knows it was an online match)
                menus.Multiplayer.ArmOnlineMatch(played, RecompMenus.OnlinePages.Hub);
                MatchResults.Last = new MatchResults
                {
                    Mode = played.Mode, LocalSeat = played.LocalSeat,
                    Rows = played.Players.Select((p, i) => new MatchResults.Player(i, p.Hunter, p.Nickname ?? "", p.Bot, 0, i, 10 - i, 0, 10 - i, i,
                        0, 0, 0, 0, 0, 0, 0, 0, 50, 0)).ToArray(),
                };
                void Tick() => menus.Tick();
                menus.Start(MultiplayerMenu.ResultsPage);
                if (!Until(() => menus.Menu.Page?.Index == MultiplayerMenu.ResultsPage && menus.Menu.PendingPage == -1, 10, Tick))
                {
                    fail = $"the menus didn't open on RESULTS (page {menus.Menu.Page?.Index})";
                    return;
                }
                for (int i = 0; i < 30; i++) Tick();
                menus.Multiplayer.OnCall(menus.Menu, new MenuAction(), 196, -1); // PLAY AGAIN
                (_, int lobby) = RecompMenus.OnlineCreatePages;
                if (!Until(() => menus.Menu.Page?.Index == lobby && menus.Recomp.OnlineHosting == o1 && menus.Menu.PendingPage == -1, 10, Tick))
                {
                    fail = $"PLAY AGAIN didn't bring PC ONE (the new host) to its lobby (page {menus.Menu.Page?.Index}, hosting {menus.Recomp.OnlineHosting != null})";
                    return;
                }
                two.Net.SetLook(Hunter.Spire, null, null, choosing: false); // (PC TWO's PLAY AGAIN: back in the lobby)
                if (!Until(() =>
                {
                    o2.Net.LobbyTick(true);
                    return o1.Net.NotBack().Count == 0 && o1.Net.Settings?.Players.Any(p => p.Remote && p.Hunter == Hunter.Spire) == true;
                }, 15, Tick))
                {
                    fail = $"PC TWO never showed up back in PC ONE's lobby ({one.Net.Status}; not back {one.Net.NotBack().Count})";
                    return;
                }
                // (an outsider looks: the list leaves out games the asker is in)
                OnlineClient look = Wait(OnlineClient.ConnectAsync(uri, ids[3].Id, "PCLOOK", "migratemenutest"));
                GamesMsg list = Wait(look.RequestAsync<GamesMsg>(new ListGamesReq()));
                Wait(look.DisposeAsync().AsTask().ContinueWith(_ => 0));
                GameCard? listed = list.Games.FirstOrDefault(g => g.Id == o1.Game.Id);
                if (listed == null || listed.Host.Code != oneCode)
                {
                    fail = listed == null ? "the server doesn't list the game again after PLAY AGAIN" : $"the server lists the game under {listed.Host.Name}, not PC ONE";
                    return;
                }
                Console.WriteLine($"  PLAY AGAIN: PC ONE's lobby, PC TWO back as Spire, the server lists {listed.Id} under {listed.Host.Name}");
                // a new player joins the lobby after the takeover: it must find the new host's slot (not slot 0)
                OnlineClient c3 = Wait(OnlineClient.ConnectAsync(uri, ids[4].Id, "PCTHREE", "migratemenutest"));
                three = Wait(OnlineMatch.JoinAsync(c3, uri, RomKey, o1.Game.Id));
                if (three.Net.HostSlot != o1.Relay.LocalSlot || !three.Net.Join(Hunter.Trace, "PCTHREE", 3, timeoutMs: 15000))
                {
                    fail = $"a new joiner didn't get a seat from the new host (host slot {three.Net.HostSlot}, {three.Net.Status})";
                    return;
                }
                OnlineMatch o3 = three;
                if (!Until(() => o1.Net.Joined >= 2, 10, Tick))
                {
                    fail = $"the new host's lobby never saw PC THREE ({o1.Net.Joined} joined)";
                    return;
                }
                Console.WriteLine($"  PC THREE joined the new host's lobby (host slot {three.Net.HostSlot})");
                int starts = requests.Count(r => r.Kind == FrontendRequestKind.StartMatch);
                menus.Recomp.OnCall(menus.Menu, 2591, inPause: false); // START
                if (!Until(() => requests.Count(r => r.Kind == FrontendRequestKind.StartMatch) > starts, 10, Tick))
                {
                    fail = "START didn't start the rematch";
                    return;
                }
                if (!two.Net.WaitForStart(5000) || !three.Net.WaitForStart(5000))
                {
                    fail = "PC TWO or PC THREE never got the rematch's START";
                    return;
                }
                MatchSettings js = two.Net.Settings!;
                string seats = string.Join(", ", js.Players.Select((p, i) => $"{i}:{p.Hunter}{(p.Bot ? " bot" : " " + p.Nickname)}"));
                Console.WriteLine("  rematch seats (PC TWO): " + seats);
                if (js.Players.Count != 4) fail = $"the rematch has {js.Players.Count} seats, expected 4 (PC ONE, PC TWO, PC THREE, the bot)";
                else if (js.Players.Any(p => !p.Bot && p.Nickname == "PCHOST")) fail = "the old host has a seat in the rematch";
                // (a joiner's settings don't mark bots: the new host's do)
                else if (one.Net.Settings?.Players.Count(p => p.Bot) != 1) fail = "the bot didn't stay in the rematch (the new host's seats)";
                else if (js.Players[js.LocalSeat].Hunter != Hunter.Spire) fail = $"PC TWO plays {js.Players[js.LocalSeat].Hunter}, not Spire";
            }
            catch (Exception ex) when (ex is OnlineException or System.Net.WebSockets.WebSocketException or IOException
                or InvalidOperationException or TimeoutException)
            {
                fail = ex.GetType().Name + ": " + ex.Message;
            }
            finally
            {
                MatchResults.Last = null;
                host?.Dispose();
                three?.Dispose();
                two?.Dispose();
                one?.Dispose();
                OnlineMatch.Current?.Dispose();
                Thread.Sleep(500);
                DeleteTestProfiles(uri, ids);
                Console.WriteLine(fail == null
                    ? "PASS migratemenutest: the host quit mid-match -> PC ONE took over (host card moved) -> RESULTS -> PLAY AGAIN -> PC ONE's lobby (listed under it) -> PC TWO back + new PC THREE joins -> rematch START, 4 seats, no old host"
                    : "FAIL migratemenutest: " + fail);
            }
        }

        private static void DeleteTestProfiles(Uri uri, List<(OnlineIdentity Id, string Name)> ids)
        {
            int deleted = 0;
            foreach ((OnlineIdentity id, string name) in ids)
            {
                try
                {
                    OnlineClient c = Wait(OnlineClient.ConnectAsync(uri, id, name, "onlinetests"));
                    if (c.ServerAtLeast(DeleteProfileReq.SinceServer))
                    {
                        Wait(c.RequestAsync<OkMsg>(new DeleteProfileReq()));
                        deleted++;
                    }
                    Wait(c.DisposeAsync().AsTask().ContinueWith(_ => 0));
                }
                catch (Exception ex) when (ex is OnlineException or System.Net.WebSockets.WebSocketException or IOException
                    or InvalidOperationException)
                {
                    Console.WriteLine($"  cleanup: {name}: {ex.Message}");
                }
            }
            Console.WriteLine($"  cleanup: {deleted} test hunter(s) deleted from the server");
        }
    }
}
