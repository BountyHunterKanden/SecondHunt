using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using MphRead.Entities;
using MphRecomp.Campaign;
using MphRecomp.Frontend;
using MphRecomp.Multiplayer;
using MphRecomp.Multiplayer.Net;
using MphRecomp.Online;

namespace MphRead
{
    // Release 0.2.0 F1 item 2: the host leaves a 2-player online match (no bots); the one left plays it out, then RESULTS'
    // PLAY AGAIN must work (owner: "it errored out"; with a bot it worked).
    //   -onlinehostleaveagain [server=] [time=20] [leave=4] [takeover=0]
    // A PC host opens a 2-player game (no bots); the menus (a FrontendSession) join it from BROWSE as in -onlinemenutest and
    // play the match here as the match screen does (CampaignHost + MatchNetDriver on OnlineMatch.Current); the host STARTs
    // and quits leave= s later (BYE + leave the game: the server names the menus' device the new host, O5b). The match runs
    // to its end alone, is handed back (MatchResults + EndMatch, as MatchSession), the menus open on RESULTS and press PLAY
    // AGAIN. PASS = no popup, the menus host the game's lobby (OnlineHosting), the server lists it as theirs and open again.
    internal static class OnlineHostLeaveTool
    {
        private static string RomKey => LanSession.RomKeyFor(Paths.MphKey);
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

        private static string? PopupText(FrontendSession s)
        {
            if (s.Popup is not MenuPopup p || !p.IsOpen) return null;
            // (a message or a question; the "creating your game..." spinner isn't one)
            object? mode = typeof(MenuPopup).GetField("_mode", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(p);
            if (mode?.ToString() == "Busy") return null;
            return typeof(MenuPopup).GetField("_text", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(p) as string ?? "(popup)";
        }

        public static void Test(string[] args)
        {
            if (!ToolPaths.UseDesktopExtraction()) return;
            Dictionary<string, string> a = args.Skip(1).Select(x => x.Split('=', 2)).Where(p => p.Length == 2).ToDictionary(p => p[0], p => p[1]);
            var uri = new Uri(a.GetValueOrDefault("server", RecompMenus.OnlineServer));
            float time = float.Parse(a.GetValueOrDefault("time", "20"), System.Globalization.CultureInfo.InvariantCulture);
            float leave = float.Parse(a.GetValueOrDefault("leave", "4"), System.Globalization.CultureInfo.InvariantCulture);
            // takeover=0: the one left doesn't take the match over (no host migration: it ends on the host's silence, its game
            // gone), as the owner's device did; PLAY AGAIN must still open a lobby (a new game, hosted by the one left)
            bool takeover = a.GetValueOrDefault("takeover", "1") != "0";
            Console.WriteLine($"onlinehostleaveagain: {uri}, version {OnlineMatch.VersionKey(RomKey)}");
            OnlineIdentity hostId = OnlineIdentity.Create();
            OnlineClient hostClient = Wait(OnlineClient.ConnectAsync(uri, hostId, "PCHOST", "onlinehostleave"));
            MatchSettings template = MatchSettings.Quick(GameMode.Battle, MatchArenas.Find("Combat Hall")!.Name, Hunter.Kanden, 0);
            template.Players[0].Nickname = "PCHOST";
            template.TimeLimitSeconds = time;
            OnlineMatch host = Wait(OnlineMatch.HostAsync(hostClient, uri, template, RomKey, 2));
            Console.WriteLine($"  host {hostClient.Me} opened game {host.Game.Id}");
            string? fail = null;
            var requests = new List<FrontendRequest>();
            string dir = Path.Combine(Path.GetTempPath(), "online-hostleave-test-" + Guid.NewGuid().ToString("N")[..6]);
            RecompMenus.OnlineNoticeSkipped = true;
            Directory.CreateDirectory(dir);
            FrontendSession s = FrontendSession.Load(Paths.FileSystem, settingsPath: Path.Combine(dir, "recomp_settings.json"));
            s.Request = r => requests.Add(r);
            bool hostGone = false;
            try
            {
                (int hub, int browse, int detail) = RecompMenus.OnlinePages;
                s.Recomp.OpenOnline(s.Menu);
                if (!RunUntil(s, () => s.Recomp.OnlineConnected && s.Recomp.OnlineGames.Any(g => g.Id == host.Game.Id), 20))
                {
                    fail = "the menus didn't connect or didn't list the host's game";
                    return;
                }
                s.Menu.GoTo(browse);
                if (!RunUntil(s, () => s.Menu.Page?.Index == browse && s.Recomp.OnlineShownIndex(host.Game.Id) >= 0, 10))
                {
                    fail = "BROWSE didn't show the host's game";
                    return;
                }
                RunUntil(s, () => false, 1);
                int at = s.Recomp.OnlineShownIndex(host.Game.Id);
                for (int i = 0; i < at % 3; i++)
                {
                    s.Navigate(0, -1);
                    for (int t = 0; t < 6; t++) { s.Tick(); Thread.Sleep(16); }
                }
                for (int i = 0; i < at / 3; i++)
                {
                    s.Press(MenuKeys.R);
                    for (int t = 0; t < 6; t++) { s.Tick(); Thread.Sleep(16); }
                }
                s.Press(MenuKeys.A);
                if (!RunUntil(s, () => s.Menu.Page?.Index == detail, 5))
                {
                    fail = "the row didn't open the game's details";
                    return;
                }
                RunUntil(s, () => false, 1);
                s.Press(MenuKeys.A); // JOIN -> SELECT HUNTER
                if (!RunUntil(s, () => s.Menu.Page?.Index == MultiplayerMenu.HunterPage && s.Menu.PendingPage == -1, 5))
                {
                    fail = $"JOIN didn't ask for a hunter (page {s.Menu.Page?.Index})";
                    return;
                }
                s.Multiplayer.OnCall(s.Menu, new MenuAction(), 133, -1); // Kanden
                if (!RunUntil(s, () => host.Net.Joined >= 1, 20))
                {
                    fail = $"the host never saw the joiner ({host.Net.Status})";
                    return;
                }
                RunUntil(s, () => false, 1);
                host.Start();
                if (!RunUntil(s, () => requests.Any(r => r.Kind == FrontendRequestKind.StartMatch), 15))
                {
                    fail = "the menus didn't start the match after the host's START";
                    return;
                }
                OnlineMatch joined = OnlineMatch.Current ?? throw new InvalidOperationException("no OnlineMatch.Current");
                MatchSettings js = joined.Net.Settings!;
                string gameId = joined.Game.Id;
                Console.WriteLine($"  the menus play seat {js.LocalSeat} of {js.Players.Count}; the host quits in {leave:0.#} s");
                // the match screen: hold, step at 60 Hz (the host quits mid-match), hand back at its end
                IDisposable hold = joined.TakeScreenHold();
                if (!takeover)
                {
                    // no migration at all: the match ends on the host's BYE, and the server's word that this device hosts now
                    // (MemberEvent.HostSlot) never reaches the session (OnlineMatch.OnServerEvent unhooked)
                    joined.Net.CanMigrate = false;
                    var heard = (Action<OnlineMsg>)Delegate.CreateDelegate(typeof(Action<OnlineMsg>), joined, "OnServerEvent");
                    joined.Client.EventReceived -= heard;
                }
                CampaignHost match = CampaignHost.StartMatch(js);
                var driver = new MatchNetDriver(joined.Net, match);
                var clock = Stopwatch.StartNew();
                long frames = 0;
                while (!match.Ended && clock.Elapsed.TotalSeconds < time + leave + 40)
                {
                    if (!hostGone && clock.Elapsed.TotalSeconds >= leave)
                    {
                        hostGone = true;
                        host.Dispose(); // BYE + LeaveGame: the server moves the game to the menus' device
                        Console.WriteLine($"  host quit at frame {match.Frame}");
                    }
                    long due = (long)(clock.Elapsed.TotalSeconds * 60);
                    while (frames < due && !match.Ended)
                    {
                        driver.Step(new CampaignInput { SelectWeapon = BeamType.None });
                        frames++;
                    }
                    Thread.Sleep(1);
                }
                Console.WriteLine($"  match over after {match.Frame} frames, ended {match.Ended}: {driver.StatusLine()}");
                if (!match.Ended)
                {
                    fail = "the match never ended";
                    return;
                }
                if (!driver.TookOver)
                {
                    Console.WriteLine("  (the menus' device did not take over: the match ended on the host's silence)");
                }
                MatchResults.Last = MatchResults.Capture(js.LocalSeat);
                joined.EndMatch();
                hold.Dispose();
                match.Dispose();
                int page = FrontendSession.ReturnPage();
                if (page != MultiplayerMenu.ResultsPage)
                {
                    fail = $"after the match the menus wouldn't open on RESULTS (ReturnPage {page})";
                    return;
                }
                FrontendSession menus = FrontendSession.Load(Paths.FileSystem, settingsPath: Path.Combine(dir, "recomp_settings.json"));
                menus.Request = r => requests.Add(r);
                menus.Start(page);
                if (!RunUntil(menus, () => menus.Menu.Page?.Index == MultiplayerMenu.ResultsPage && menus.Menu.PendingPage == -1, 10))
                {
                    fail = $"the menus didn't open on RESULTS (page {menus.Menu.Page?.Index})";
                    return;
                }
                RunUntil(menus, () => false, 1);
                menus.Multiplayer.OnCall(menus.Menu, new MenuAction(), 196, -1); // PLAY AGAIN
                (_, int lobby) = RecompMenus.OnlineCreatePages;
                RunUntil(menus, () => PopupText(menus) != null || menus.Menu.Page?.Index == lobby && menus.Menu.PendingPage == -1, takeover ? 10 : 30);
                RunUntil(menus, () => PopupText(menus) != null, 3); // (a popup that comes a moment later)
                string? popup = PopupText(menus);
                Console.WriteLine($"  PLAY AGAIN -> page {menus.Menu.Page?.Index} (lobby {lobby}, hub {hub}), hosting {menus.Recomp.OnlineHosting != null}, "
                    + $"in lobby {menus.Recomp.OnlineInLobby}, popup {(popup == null ? "none" : "\"" + popup.Replace("\n", " / ") + "\"")}");
                if (popup != null)
                    fail = $"PLAY AGAIN showed a popup: \"{popup.Replace("\n", " / ")}\" (page {menus.Menu.Page?.Index})";
                else if (menus.Menu.Page?.Index != lobby)
                    fail = $"PLAY AGAIN didn't open a lobby (page {menus.Menu.Page?.Index})";
                else if (!takeover && menus.Recomp.OnlineHosting?.Game.Id == gameId)
                    fail = "PLAY AGAIN went back to the old game (this device never took it over)";
                else if (menus.Recomp.OnlineHosting is not OnlineMatch m || !m.Net.IsHost)
                    fail = "PLAY AGAIN's lobby isn't hosted by the one left";
                else
                {
                    // the server shows the game again (open, the menus' player its host) for others to join
                    OnlineClient look = Wait(OnlineClient.ConnectAsync(uri, OnlineIdentity.Create(), "PCLOOK", "onlinehostleave"));
                    GameCard? card = null;
                    var wait = Stopwatch.StartNew();
                    while (card == null && wait.ElapsedMilliseconds < 8000)
                    {
                        menus.Tick();
                        card = Wait(look.RequestAsync<GamesMsg>(new ListGamesReq())).Games.FirstOrDefault(g => g.Id == m.Game.Id);
                        Thread.Sleep(200);
                    }
                    Console.WriteLine($"  the server lists {(card == null ? "no game" : $"{card.Id} by {card.Host.Name} ({card.Players}/{card.Info.MaxPlayers})")}");
                    if (card == null) fail = "the server doesn't list the game again after PLAY AGAIN";
                    Wait(look.DisposeAsync().AsTask().ContinueWith(_ => 0));
                }
            }
            catch (Exception ex)
            {
                fail = "exception: " + ex;
            }
            finally
            {
                OnlineMatch.Current?.Dispose();
                if (!hostGone) host.Dispose();
                MatchResults.Last = null;
                Console.WriteLine(fail == null
                    ? "PASS onlinehostleaveagain: the host quit a 2-player match, the one left finished it, RESULTS -> PLAY AGAIN -> its own lobby, open on the server"
                    : "FAIL onlinehostleaveagain: " + fail);
            }
        }
    }
}
