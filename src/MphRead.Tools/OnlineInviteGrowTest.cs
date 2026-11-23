using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using MphRecomp.Campaign;
using MphRecomp.Multiplayer;
using MphRecomp.Multiplayer.Net;
using MphRecomp.Online;

namespace MphRead
{
    // -onlineinvitegrow [people=4] [bots=0] (owner 2026-11-22: "make sure a bot is always replaced on invite and never
    // refuses a player"; "players without an invite should not just be able to join a lobby where the player count is already
    // full"): a player-hosted game (the app's OnlineMatch on a local server, no match host) with PLAYERS = people= and bots=
    // bots fills up; a player who isn't invited is turned away ("full"); the host's friend, invited, joins anyway. PASS = the
    // invited player is in, the host's People grew by one (MemberEvent.MaxPlayers -> NetMatchSession.GrowPeople), and the
    // START has people + 1 humans and the bots that still fit in 8. The served path is -onlinetest served=hub invite=1.
    internal static partial class OnlineMatchTool
    {
        public static void InviteGrowTest(string[] args)
        {
            if (!ToolPaths.UseDesktopExtraction()) return;
            Dictionary<string, string> a = args.Skip(1).Select(x => x.Split('=', 2)).Where(p => p.Length == 2).ToDictionary(p => p[0], p => p[1]);
            int people = int.Parse(a.GetValueOrDefault("people", "4"));
            int bots = int.Parse(a.GetValueOrDefault("bots", "0"));
            string dll = ServerDll();
            if (!File.Exists(dll))
            {
                Console.WriteLine($"FAIL onlineinvitegrow: no server build at {dll}");
                return;
            }
            int http = FreeTcpPort();
            string dbDir = Path.Combine(Path.GetTempPath(), "secondhunt-invitegrow-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(dbDir);
            var serverOut = new List<string>();
            Process server = Start(Environment.ProcessPath!, $"\"{dll}\" --urls http://127.0.0.1:{http} --db \"{Path.Combine(dbDir, "test.db")}\" "
                + $"--relay-port {FreeUdpPort()} --words none.txt", serverOut);
            var clients = new List<OnlineClient>();
            var matches = new List<OnlineMatch>();
            var problems = new List<string>();
            try
            {
                if (!WaitHealthy(http))
                {
                    Console.WriteLine("FAIL onlineinvitegrow: the server didn't start");
                    Dump("server", server, serverOut);
                    return;
                }
                var uri = new Uri($"ws://127.0.0.1:{http}/v1/ws");
                OnlineClient Connect(string name)
                {
                    OnlineClient c = Wait(OnlineClient.ConnectAsync(uri, OnlineIdentity.Create(), name, "invitegrow"));
                    clients.Add(c);
                    return c;
                }
                OnlineClient hostClient = Connect("PCHOST");
                MatchSettings template = MatchSettings.Quick(GameMode.Battle, MatchArenas.Find("Combat Hall")!.Name, Hunter.Kanden, bots);
                template.Players[0].Nickname = "PCHOST";
                OnlineMatch host = Wait(OnlineMatch.HostAsync(hostClient, uri, template, RomKey, people, askServed: false));
                matches.Add(host);
                Console.WriteLine($"host opened game {host.Game.Id}: PLAYERS {host.Game.Info.MaxPlayers}, {bots} bot(s), People {host.Net.People}");
                Hunter[] hunters = [Hunter.Samus, Hunter.Spire, Hunter.Trace, Hunter.Noxus, Hunter.Sylux, Hunter.Weavel, Hunter.Guardian];
                bool JoinAs(OnlineClient c, string name, int i)
                {
                    OnlineMatch m = Wait(OnlineMatch.JoinAsync(c, uri, RomKey, host.Game.Id));
                    matches.Add(m);
                    bool ok = m.Net.Join(hunters[i % hunters.Length], name, timeoutMs: 15000);
                    Console.WriteLine($"  {name}: {m.Net.Status}");
                    return ok;
                }
                for (int i = 1; i < people; i++)
                {
                    if (!JoinAs(Connect($"PC{i + 1}"), $"PC{i + 1}", i))
                    {
                        problems.Add($"PC{i + 1} couldn't fill a seat");
                    }
                }
                // not invited: the full game turns them away (browse / QUICK MATCH / the friends list)
                OnlineClient browser = Connect("BROWSER");
                try
                {
                    Wait(OnlineMatch.JoinAsync(browser, uri, RomKey, host.Game.Id));
                    problems.Add("a player without an invite joined the full game");
                }
                catch (OnlineException ex) when (ex.Code == "full")
                {
                    Console.WriteLine($"  BROWSER (no invite): refused: {ex.Message}");
                }
                // invited: the host's friend joins the full game; PLAYERS grows, a bot makes way at 8
                OnlineClient friend = Connect("FRIEND");
                Wait(hostClient.RequestAsync<OkMsg>(new AddReq { Code = friend.Me.Code }));
                Wait(friend.RequestAsync<OkMsg>(new AddReq { Code = hostClient.Me.Code }));
                Wait(hostClient.RequestAsync<OkMsg>(new InviteReq { Code = friend.Me.Code }));
                try
                {
                    if (!JoinAs(friend, "FRIEND", people))
                    {
                        problems.Add("the invited player was refused by the host");
                    }
                }
                catch (OnlineException ex)
                {
                    problems.Add($"the server refused the invited player: {ex.Code} {ex.Message}");
                }
                var clock = Stopwatch.StartNew();
                while (host.Net.People < people + 1 && clock.ElapsedMilliseconds < 5000) Thread.Sleep(50);
                Console.WriteLine($"host: People {host.Net.People}, SeatLimit {host.Net.SeatLimit}, joined {host.Net.Joined}, card PLAYERS {host.Game.Info.MaxPlayers}");
                if (host.Net.People != people + 1) problems.Add($"the host's People is {host.Net.People}, wanted {people + 1}");
                if (host.Game.Info.MaxPlayers != people + 1) problems.Add($"the host's card says PLAYERS {host.Game.Info.MaxPlayers}, wanted {people + 1}");
                host.Start();
                clock.Restart();
                while (!matches.Skip(1).All(m => m.Net.Started) && clock.ElapsedMilliseconds < 10000) Thread.Sleep(50);
                MatchSettings? s = host.Net.Settings;
                int humans = s?.Players.Count(p => !p.Bot) ?? 0, botsIn = s?.Players.Count(p => p.Bot) ?? 0;
                int wantBots = Math.Min(bots, MatchSettings.MaxSeats - people - 1);
                Console.WriteLine($"START: {humans} people + {botsIn} bots: " + string.Join(", ", s?.Players.Select(p => $"{p.Nickname}{(p.Bot ? " bot" : "")}") ?? []));
                if (!matches.Skip(1).All(m => m.Net.Started)) problems.Add("not every joiner got the START");
                if (humans != people + 1 || botsIn != wantBots) problems.Add($"the match has {humans} people + {botsIn} bots, wanted {people + 1} + {wantBots}");
            }
            catch (Exception ex)
            {
                problems.Add($"{ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                foreach (OnlineMatch m in matches) m.Dispose();
                foreach (OnlineClient c in clients) _ = c.DisposeAsync().AsTask();
                try { server.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                server.WaitForExit(5000);
                try { Directory.Delete(dbDir, recursive: true); } catch (IOException) { }
            }
            lock (serverOut)
            {
                foreach (string l in serverOut.Where(l => l.Contains("invited player", StringComparison.Ordinal))) Console.WriteLine("  [server] " + l);
            }
            Console.WriteLine(problems.Count == 0
                ? $"PASS onlineinvitegrow: PLAYERS {people} + {bots} bot(s) full, a player without an invite refused, the invited one in ({people + 1} people)"
                : "FAIL onlineinvitegrow: " + string.Join("; ", problems));
        }
    }
}
