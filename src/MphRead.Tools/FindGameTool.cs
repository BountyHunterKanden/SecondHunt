using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using MphRead.Entities;
using MphRecomp.Campaign;
using MphRecomp.Frontend;
using MphRecomp.Multiplayer;
using MphRecomp.Multiplayer.Net;
using MphRecomp.Online;
using OpenTK.Mathematics;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace MphRead
{
    // FIND GAME (Social track O6; Core Frontend/OnlineMenus.Find.cs, server SecondHunt.Server/HubFind.cs), on the PC:
    //   -findgamemenu            FIND GAME (idle, searching with a party) and the vote (voting, decided) with sample data (no
    //                            server) -> extract_out/online_menus/build/find_<page>.png
    //   -findgametest [time=20] [server=]
    //                            four devices on a local server (spawned with --find-fast unless server= names one): devices 1
    //                            and 2 are friends and a party (1 leads), 3 and 4 alone; all search BATTLE, NEARBY; three vote
    //                            the first arena offered, one the second. PASS = one match of four, the first arena, device 3
    //                            (the lowest ping, not the party's leader) hosts, the others join through the server, everyone
    //                            plays it and ends with the same scoreboard
    //   -findgamemenutest [server=]
    //                            the menus (a FrontendSession) and a PC searcher on a local --find-fast server: QUICK MATCH opens
    //                            FIND GAME, SEARCH, the vote page, a tap votes; the menus host (the PC's ping is worse), the PC
    //                            joins, the lobby starts by itself (StartMatch, net=online); then again with the PC's ping
    //                            better: the PC hosts and the menus join its game and play it when it starts
    //   -findgamejoin ...        one device of -findgametest (a separate process: one match a process)
    internal static class FindGameTool
    {
        private static string RomKey => LanSession.RomKeyFor(Paths.MphKey);
        private static string VersionKey => OnlineMatch.VersionKey(RomKey);
        private static T Wait<T>(System.Threading.Tasks.Task<T> task) => task.GetAwaiter().GetResult();

        private static Dictionary<string, string> Args(string[] args) => args.Skip(1).Select(a => a.Split('=', 2))
            .Where(p => p.Length == 2).ToDictionary(p => p[0], p => p[1]);

        // ---------------------------------------------------------------- sample renders

        public static void Preview(string[] args)
        {
            if (!ToolPaths.UseDesktopExtraction()) return;
            FrontendSession s = Session(Path.Combine(Path.GetTempPath(), "find-menu-preview"));
            PlayerCard me = Card("SAMUS", 815), kanden = Card("KANDEN", 4412, friend: true), noxus = Card("NOXUS", 193, rival: true),
                raven = Card("RAVEN", 7781);
            s.Recomp.OnlinePreview(me, new List<GameCard>(), new List<PlayerCard> { kanden }, 214, 41);
            (int find, int vote) = RecompMenus.OnlineFindPages;
            if (find < 0)
            {
                Console.WriteLine("FAIL findgamemenu: the pages aren't installed (no multiplayer menu on this ROM?)");
                return;
            }
            s.Recomp.OpenOnline(s.Menu);
            Run(s, 60);
            Shot(s, "find_hub");
            s.Menu.GoTo(find);
            Run(s, 90);
            Shot(s, "find_idle");
            var party = new PartyInfo { Id = "p1", Leader = me.Code, Members = new List<PlayerCard> { me, kanden } };
            s.Recomp.OnlinePreviewFind(new FindEvent { State = FindState.Searching, Waited = 14, Queued = 3, LimitMs = 160, Nearby = true }, party);
            Run(s, 30);
            Shot(s, "find_searching");
            var players = new List<PlayerCard> { me, kanden, noxus, raven };
            var arenas = new List<string> { "Combat Hall", "Harvester", "Sanctorus", "Elder Passage" };
            arenas = arenas.Select(a => MatchArenas.Find(a)?.Name ?? a).ToList();
            s.Recomp.OnlinePreviewFind(new FindEvent
            {
                State = FindState.Vote, FindId = "f1", Mode = "Battle", Players = players, Arenas = arenas, Seconds = 12, Votes = 2, Arena = arenas[1],
            }, party);
            s.Menu.GoTo(vote);
            Run(s, 90);
            Shot(s, "find_vote");
            s.Recomp.OnlinePreviewFind(new FindEvent
            {
                State = FindState.Join, FindId = "f1", Mode = "Battle", Players = players, Arena = arenas[1], HostCard = raven,
            }, party);
            Run(s, 30);
            Shot(s, "find_vote_decided");
            // P8 (release 0.2.0 F3): a match of eight found: the players in two rows
            var eight = players.Concat(new[] { Card("SYLUX", 5120), Card("TRACE", 6631), Card("SPIRE", 2290), Card("WEAVEL", 3307) }).ToList();
            s.Recomp.OnlinePreviewFind(new FindEvent
            {
                State = FindState.Vote, FindId = "f2", Mode = "Capture", Players = eight, Arenas = arenas, Seconds = 12, Votes = 5, Arena = arenas[0],
            }, party);
            Run(s, 30);
            Shot(s, "find_vote8");
            s.Recomp.OnlinePreviewFind(new FindEvent
            {
                State = FindState.Join, FindId = "f2", Mode = "Capture", Players = eight, Arena = arenas[0], HostCard = raven,
            }, party);
            Run(s, 30);
            Shot(s, "find_vote8_decided");
            Console.WriteLine("findgamemenu: done");
        }

        // ---------------------------------------------------------------- four devices

        public static void Test(string[] args)
        {
            if (!ToolPaths.UseDesktopExtraction()) return;
            Dictionary<string, string> a = Args(args);
            float time = Single.Parse(a.GetValueOrDefault("time", "20"), CultureInfo.InvariantCulture);
            var server = new TestServer();
            string? fail = null;
            var devices = new List<(Process P, List<string> Out)>();
            try
            {
                Uri uri = a.TryGetValue("server", out string? given) ? new Uri(given) : server.Start();
                Console.WriteLine($"findgametest: {uri}");
                OnlineIdentity[] ids = Enumerable.Range(0, 4).Select(_ => OnlineIdentity.Create()).ToArray();
                string[] names = { "FINDA1", "FINDB2", "FINDC3", "FINDD4" };
                // devices 1 and 2: friends (both send ADD FRIEND), so 1 may invite 2 into a party
                OnlineClient c1 = Wait(OnlineClient.ConnectAsync(uri, ids[0], names[0], "findgametest"));
                OnlineClient c2 = Wait(OnlineClient.ConnectAsync(uri, ids[1], names[1], "findgametest"));
                Wait(c1.RequestAsync<OkMsg>(new AddReq { Code = c2.Me.Code, Kind = LinkKind.Friend }));
                Wait(c2.RequestAsync<OkMsg>(new AddReq { Code = c1.Me.Code, Kind = LinkKind.Friend }));
                Wait(c1.DisposeAsync().AsTask().ContinueWith(_ => 0));
                Wait(c2.DisposeAsync().AsTask().ContinueWith(_ => 0));
                int[] pings = { 40, 30, 10, 60 }; // device 3 has the best connection
                string dll = typeof(FindGameTool).Assembly.Location;
                for (int i = 0; i < 4; i++)
                {
                    string party = i == 0 ? $" mate={ids[1].FriendCode} lead=1" : i == 1 ? $" mate={ids[0].FriendCode} lead=0" : "";
                    var output = new List<string>();
                    string line = $"\"{dll}\" -findgamejoin server={uri} key={ids[i].BackupCode} name={names[i]} index={i + 1} ping={pings[i]} "
                        + $"vote={(i == 3 ? 1 : 0)} time={time.ToString(CultureInfo.InvariantCulture)}{party}";
                    devices.Add((Spawn(line, output, stdin: true), output));
                }
                // everyone connected (and the party made) before anyone searches: one match of four
                if (!Until(() => devices.All(d => Has(d.Out, "READY")), 30))
                {
                    fail = "not every device got ready: " + string.Join(", ", devices.Select((d, i) => $"{i + 1}:{Has(d.Out, "READY")}"));
                    return;
                }
                foreach ((Process p, _) in devices) p.StandardInput.WriteLine("go");
                foreach ((Process p, _) in devices)
                {
                    if (!p.WaitForExit((int)(time * 1000) + 90000)) p.Kill();
                    p.WaitForExit();
                }
                fail = Verdict(devices.Select(d => Lines(d.Out)).ToList(), ids[2].FriendCode);
            }
            catch (Exception ex) when (ex is OnlineException or IOException or HttpRequestException or InvalidOperationException
                or System.Net.WebSockets.WebSocketException)
            {
                fail = ex.ToString();
            }
            finally
            {
                for (int i = 0; i < devices.Count; i++) Dump($"device {i + 1}", devices[i].Out);
                server.Stop(fail != null);
                Console.WriteLine(fail == null
                    ? "PASS findgametest: a party of 2 and 2 singles searched, one match of 4 formed, the vote picked the first arena, the best "
                        + "connection (device 3) hosted, everyone joined through the server and all four scoreboards agree"
                    : "FAIL findgametest: " + fail);
            }
        }

        private static string? Verdict(List<List<string>> devices, string hostCode)
        {
            var problems = new List<string>();
            for (int i = 0; i < devices.Count; i++)
            {
                foreach (string f in devices[i].Where(l => l.StartsWith("FAIL"))) problems.Add($"device {i + 1}: {f}");
            }
            List<string?> found = devices.Select(d => d.FirstOrDefault(l => l.StartsWith("FOUND "))).ToList();
            if (found.Any(f => f == null)) problems.Add("not every device was in a match: " + string.Join(", ", found.Select((f, i) => $"{i + 1}:{f != null}")));
            else if (found.Distinct().Count() != 1) problems.Add("the devices saw different matches: " + string.Join(" / ", found));
            else
            {
                string[] parts = found[0]!.Split(' ');
                // FOUND <find id> <players> <host code> <arena...>
                if (parts[2] != "4") problems.Add($"a match of {parts[2]}, expected 4");
                if (parts[3] != hostCode) problems.Add("the host isn't device 3 (the best connection)");
                string? first = devices[0].FirstOrDefault(l => l.StartsWith("OPTIONS "))?.Split('|')[0][8..];
                string arena = string.Join(' ', parts.Skip(4));
                if (first == null) problems.Add("device 1 saw no vote");
                else if (arena != first) problems.Add($"the arena is {arena}, the vote was for {first} (3 to 1)");
            }
            int hosts = devices.Count(d => d.Any(l => l.StartsWith("HOSTING ")));
            if (hosts != 1 || !devices[2].Any(l => l.StartsWith("HOSTING "))) problems.Add($"{hosts} device(s) hosted, expected only device 3");
            List<string?> scores = devices.Select(d => d.FirstOrDefault(l => l.StartsWith("SCORES "))).ToList();
            if (scores.Any(s => s == null)) problems.Add("not every device played: " + string.Join(", ", scores.Select((s, i) => $"{i + 1}:{s != null}")));
            else if (scores.Distinct().Count() != 1) problems.Add("the scoreboards differ: " + string.Join(" / ", scores));
            return problems.Count == 0 ? null : string.Join("; ", problems);
        }

        // ---------------------------------------------------------------- one device (a separate process)

        public static void Join(string[] args)
        {
            if (!ToolPaths.UseDesktopExtraction()) return;
            Dictionary<string, string> a = Args(args);
            var uri = new Uri(a["server"]);
            string name = a["name"];
            int index = Int32.Parse(a["index"]), ping = Int32.Parse(a["ping"]), voteFor = Int32.Parse(a.GetValueOrDefault("vote", "0"));
            float time = Single.Parse(a.GetValueOrDefault("time", "20"), CultureInfo.InvariantCulture);
            string? mate = a.GetValueOrDefault("mate");
            bool lead = a.GetValueOrDefault("lead") == "1";
            Hunter hunter = new[] { Hunter.Samus, Hunter.Kanden, Hunter.Spire, Hunter.Trace }[(index - 1) % 4];
            OnlineIdentity id = OnlineIdentity.FromBackupCode(a["key"]) ?? throw new ArgumentException("bad key");
            OnlineClient client = Wait(OnlineClient.ConnectAsync(uri, id, name, "findgametest"));
            OnlineMatch? match = null;
            try
            {
                Wait(client.RequestAsync<OkMsg>(new PingReq { Ms = ping }));
                if (mate != null && !MakeParty(client, mate, lead))
                {
                    Console.WriteLine("FAIL no party");
                    return;
                }
                Console.WriteLine($"READY {client.Me} ping {ping}" + (mate != null ? (lead ? " party leader" : " party member") : ""));
                Console.ReadLine(); // (the parent's "go")
                if (mate == null || lead)
                {
                    Wait(client.RequestAsync<OkMsg>(new FindStartReq
                    {
                        Modes = new List<string> { "Battle" }, Nearby = true, Version = VersionKey,
                        Arenas = new Dictionary<string, List<string>> { ["Battle"] = MultiplayerMenu.FindArenas()["Battle"] }, PingMs = ping,
                    }));
                    Console.WriteLine("searching");
                }
                var clock = Stopwatch.StartNew();
                bool voted = false;
                while (match == null && clock.Elapsed.TotalSeconds < 60)
                {
                    if (!client.Events.TryDequeue(out OnlineMsg? ev))
                    {
                        Thread.Sleep(10);
                        continue;
                    }
                    if (ev is not FindEvent f) continue;
                    Console.WriteLine($"find {f.State} {f.Mode} {f.Arena} {f.Reason} waited {f.Waited} queued {f.Queued} limit {f.LimitMs}");
                    switch (f.State)
                    {
                    case FindState.Vote:
                        if (voted) break;
                        voted = true;
                        Console.WriteLine("OPTIONS " + string.Join("|", f.Arenas));
                        Wait(client.RequestAsync<OkMsg>(new FindVoteReq { Arena = f.Arenas[Math.Min(voteFor, f.Arenas.Count - 1)] }));
                        break;
                    case FindState.Host:
                    {
                        Console.WriteLine($"FOUND {f.FindId} {f.Players.Count} {f.HostCard?.Code} {f.Arena}");
                        var took = Stopwatch.StartNew();
                        MatchSettings template = MultiplayerMenu.BuildFindMatch(f.Mode, f.Arena);
                        Console.WriteLine($"built the match in {took.ElapsedMilliseconds} ms");
                        template.Players[0].Hunter = hunter;
                        template.Players[0].Nickname = name;
                        template.TimeLimitSeconds = time;
                        match = Wait(OnlineMatch.HostAsync(client, uri, template, RomKey, f.Players.Count, isPublic: false, friends: false, rivals: false));
                        Console.WriteLine($"HOSTING {match.Game.Id} public {match.Game.Info.Public} max {match.Game.Info.MaxPlayers} after {took.ElapsedMilliseconds} ms");
                        var wait = Stopwatch.StartNew();
                        while (match.Net.Joined < f.Players.Count - 1 && wait.ElapsedMilliseconds < 30000) Thread.Sleep(20);
                        Console.WriteLine($"{match.Net.Joined}/{f.Players.Count - 1} joined after {wait.ElapsedMilliseconds} ms");
                        if (match.Net.Joined < f.Players.Count - 1)
                        {
                            Console.WriteLine("FAIL not everyone joined");
                            return;
                        }
                        match.Start();
                        break;
                    }
                    case FindState.Join:
                        Console.WriteLine($"FOUND {f.FindId} {f.Players.Count} {f.HostCard?.Code} {f.Arena}");
                        break;
                    case FindState.Game:
                        match = Wait(OnlineMatch.JoinAsync(client, uri, RomKey, f.GameId));
                        if (!match.Net.Join(hunter, name, 3, timeoutMs: 15000))
                        {
                            Console.WriteLine("FAIL no seat: " + match.Net.Status);
                            return;
                        }
                        Console.WriteLine($"joined {f.GameId} as seat {match.Net.Settings?.LocalSeat}");
                        if (!match.Net.WaitForStart(30000))
                        {
                            Console.WriteLine("FAIL no START: " + match.Net.Status);
                            return;
                        }
                        break;
                    case FindState.Cancelled:
                        Console.WriteLine("FAIL the search was cancelled: " + f.Reason);
                        return;
                    }
                }
                if (match == null)
                {
                    Console.WriteLine("FAIL no match in 60 s");
                    return;
                }
                Play(match, time);
            }
            finally
            {
                if (match != null) match.Dispose();
                else Wait(client.DisposeAsync().AsTask().ContinueWith(_ => 0));
            }
        }

        // leader: invite the mate until they're in; member: take the invite
        private static bool MakeParty(OnlineClient client, string mate, bool lead)
        {
            var clock = Stopwatch.StartNew();
            long nextInvite = 0;
            while (clock.Elapsed.TotalSeconds < 20)
            {
                if (lead && clock.ElapsedMilliseconds >= nextInvite)
                {
                    nextInvite = clock.ElapsedMilliseconds + 2000;
                    try
                    {
                        Wait(client.RequestAsync<OkMsg>(new PartyInviteReq { Code = mate }));
                    }
                    catch (OnlineException ex)
                    {
                        Console.WriteLine("invite: " + ex.Code);
                    }
                }
                while (client.Events.TryDequeue(out OnlineMsg? ev))
                {
                    if (lead && ev is PartyEvent { Party.Members.Count: 2 }) return true;
                    if (!lead && ev is PartyInviteEvent invite)
                    {
                        PartyMsg party = Wait(client.RequestAsync<PartyMsg>(new PartyJoinReq { PartyId = invite.PartyId }));
                        return party.Party.Members.Count == 2;
                    }
                }
                Thread.Sleep(20);
            }
            return false;
        }

        private static void Play(OnlineMatch match, float time)
        {
            MatchSettings settings = match.Net.Settings!;
            Console.WriteLine($"play {settings.Mode} in {settings.Arena} as seat {settings.LocalSeat} of {settings.Players.Count}");
            CampaignHost host = CampaignHost.StartMatch(settings);
            var driver = new MatchNetDriver(match.Net, host);
            bool isHost = match.Net.IsHost;
            RealTime(host, () => driver.Step(isHost ? new CampaignInput { SelectWeapon = BeamType.None } : Brain(host)), time + (isHost ? 15 : 25));
            Console.WriteLine($"{host.Frame} frames, ended {host.Ended}, {driver.StatusLine()}");
            Console.WriteLine(OnlineMatchTool.Scores(settings.Players.Count));
            host.Dispose();
        }

        // ---------------------------------------------------------------- the menus

        public static void MenuTest(string[] args)
        {
            if (!ToolPaths.UseDesktopExtraction()) return;
            Dictionary<string, string> a = Args(args);
            var server = new TestServer();
            string? fail = null;
            var requests = new List<FrontendRequest>();
            OnlineMatch? pc = null;
            try
            {
                Uri uri = a.TryGetValue("server", out string? given) ? new Uri(given) : server.Start();
                RecompMenus.OnlineServerOverride = uri.ToString();
                Console.WriteLine($"findgamemenutest: {uri}");
                FrontendSession s = Session(Path.Combine(Path.GetTempPath(), "find-menu-test-" + Guid.NewGuid().ToString("N")[..6]));
                s.Request = r => requests.Add(r);
                (int find, int vote) = RecompMenus.OnlineFindPages;
                (int hub, _, _) = RecompMenus.OnlinePages;
                (_, int lobby) = RecompMenus.OnlineCreatePages;

                // 1: the menus host (the PC's route is worse)
                s.Recomp.OpenOnline(s.Menu);
                fail = Round(s, uri, hub, find, vote, lobby, requests, pcPing: 300, menusHost: true, out pc);
                pc?.Dispose();
                pc = null;
                OnlineMatch.Current?.Dispose();
                if (fail != null) return;
                // 2: the PC hosts (its route is better; it also searched first), the menus join
                s.Recomp.OpenOnline(s.Menu);
                fail = Round(s, uri, hub, find, vote, lobby, requests, pcPing: 1, menusHost: false, out pc);
            }
            finally
            {
                pc?.Dispose();
                OnlineMatch.Current?.Dispose();
                RecompMenus.OnlineServerOverride = null;
                server.Stop(fail != null);
                Console.WriteLine(fail == null
                    ? "PASS findgamemenutest: QUICK MATCH -> FIND GAME -> SEARCH -> the vote (a tap votes) -> the menus host, the PC joins, the "
                        + "lobby starts by itself; then the PC hosts and the menus join its game and play it"
                    : "FAIL findgamemenutest: " + fail);
            }
        }

        private static string? Round(FrontendSession s, Uri uri, int hub, int find, int vote, int lobby, List<FrontendRequest> requests,
            int pcPing, bool menusHost, out OnlineMatch? pcMatch)
        {
            pcMatch = null;
            string round = menusHost ? "round 1 (menus host)" : "round 2 (menus join)";
            Console.WriteLine(round);
            if (!RunUntil(s, () => s.Recomp.OnlineConnected && s.Menu.Page?.Index == hub && s.Menu.PendingPage == -1, 20)) return $"{round}: the menus didn't connect";
            Run(s, 30);
            s.Recomp.OnCall(s.Menu, 2500, inPause: false); // the big tile
            if (!RunUntil(s, () => s.Menu.Page?.Index == find && s.Menu.PendingPage == -1, 5)) return $"{round}: the tile didn't open FIND GAME (page {s.Menu.Page?.Index})";
            Run(s, 20);
            if (menusHost) Shot(s, "find_live_idle");
            OnlineClient pc = Wait(OnlineClient.ConnectAsync(uri, OnlineIdentity.Create(), menusHost ? "FINDPC1" : "FINDPC2", "findgamemenutest"));
            Wait(pc.RequestAsync<OkMsg>(new PingReq { Ms = pcPing }));
            var startReq = new FindStartReq
            {
                Modes = new List<string> { "Battle" }, Nearby = true, Version = VersionKey, PingMs = pcPing,
                Arenas = new Dictionary<string, List<string>> { ["Battle"] = MultiplayerMenu.FindArenas()["Battle"] },
            };
            if (!menusHost) Wait(pc.RequestAsync<OkMsg>(startReq));
            int starts = requests.Count(r => r.Kind == FrontendRequestKind.StartMatch);
            s.Recomp.OnCall(s.Menu, 2727, inPause: false); // SEARCH
            if (menusHost) Wait(pc.RequestAsync<OkMsg>(startReq));
            Run(s, 30);
            if (menusHost) Shot(s, "find_live_searching");
            if (!RunUntil(s, () => s.Menu.Page?.Index == vote && s.Menu.PendingPage == -1, 20)) return $"{round}: no vote page (page {s.Menu.Page?.Index})";
            Run(s, 20);
            if (menusHost) Shot(s, "find_live_vote");
            s.Recomp.OnCall(s.Menu, 2731, inPause: false); // the second arena
            // the PC: votes, then hosts or joins
            var clock = Stopwatch.StartNew();
            bool voted = false;
            while (pcMatch == null && clock.Elapsed.TotalSeconds < 40)
            {
                s.Tick();
                Thread.Sleep(16);
                if (!pc.Events.TryDequeue(out OnlineMsg? ev) || ev is not FindEvent f) continue;
                Console.WriteLine($"  pc: {f.State} {f.Arena} {f.Reason}");
                if (f.State == FindState.Vote && !voted)
                {
                    voted = true;
                    Wait(pc.RequestAsync<OkMsg>(new FindVoteReq { Arena = f.Arenas[0] }));
                }
                else if (f.State == FindState.Host)
                {
                    if (menusHost) return $"{round}: the PC was picked to host";
                    MatchSettings t = MatchSettings.Quick(GameMode.Battle, f.Arena, Hunter.Kanden, 0, 0);
                    t.Players[0].Nickname = "FINDPC2";
                    t.TimeLimitSeconds = 30;
                    var task = OnlineMatch.HostAsync(pc, uri, t, RomKey, f.Players.Count, isPublic: false, friends: false, rivals: false);
                    while (!task.IsCompleted)
                    {
                        s.Tick();
                        Thread.Sleep(16);
                    }
                    pcMatch = task.GetAwaiter().GetResult();
                }
                else if (f.State == FindState.Game)
                {
                    if (!menusHost) return $"{round}: the menus were picked to host";
                    var task = OnlineMatch.JoinAsync(pc, uri, RomKey, f.GameId);
                    while (!task.IsCompleted)
                    {
                        s.Tick();
                        Thread.Sleep(16);
                    }
                    pcMatch = task.GetAwaiter().GetResult();
                    OnlineMatch joined = pcMatch;
                    var seat = System.Threading.Tasks.Task.Run(() => joined.Net.Join(Hunter.Kanden, "FINDPC1", 3, timeoutMs: 15000));
                    while (!seat.IsCompleted)
                    {
                        s.Tick();
                        Thread.Sleep(16);
                    }
                    if (!seat.Result) return $"{round}: the PC got no seat ({joined.Net.Status})";
                }
                else if (f.State == FindState.Cancelled) return $"{round}: the PC's search was cancelled ({f.Reason})";
            }
            if (pcMatch == null) return $"{round}: the PC never got into the match";
            if (menusHost)
            {
                if (!RunUntil(s, () => s.Menu.Page?.Index == lobby && s.Recomp.OnlineHosting != null, 10)) return $"{round}: the menus aren't hosting (page {s.Menu.Page?.Index})";
                OnlineMatch host = s.Recomp.OnlineHosting!;
                Console.WriteLine($"  menus host {host.Game.Id}: {host.Game.Info.Mode} in {host.Game.Info.Arena}, public {host.Game.Info.Public}, max {host.Game.Info.MaxPlayers}");
                Shot(s, "find_live_lobby");
                // the lobby starts by itself 3 s after everyone's in
                if (!RunUntil(s, () => requests.Count(r => r.Kind == FrontendRequestKind.StartMatch) > starts, 15)) return $"{round}: the lobby didn't start by itself";
                if (!pcMatch.Net.WaitForStart(5000)) return $"{round}: the PC never got START";
                if (OnlineMatch.Current != host || host.Game.Info.Public) return $"{round}: the started match isn't the menus' private hosted one";
                Console.WriteLine("  " + requests.Last(r => r.Kind == FrontendRequestKind.StartMatch).Arg);
                return null;
            }
            // the PC hosts: the menus join it (a JOIN), then the PC starts
            if (!RunUntil(s, () => s.Menu.Page?.Index == lobby && s.Recomp.OnlineInLobby, 20)) return $"{round}: the menus didn't get into the PC's lobby (page {s.Menu.Page?.Index})";
            OnlineMatch pm = pcMatch;
            if (!RunUntil(s, () => pm.Net.Joined >= 1, 10)) return $"{round}: the PC's match never saw the menus join";
            Run(s, 30);
            Shot(s, "find_live_lobby_joined");
            pcMatch.Start();
            if (!RunUntil(s, () => requests.Count(r => r.Kind == FrontendRequestKind.StartMatch) > starts, 10)) return $"{round}: the menus didn't play the PC's match";
            MatchSettings? ms = OnlineMatch.Current?.Net.Settings;
            if (ms == null || ms.LocalSeat != 1) return $"{round}: the menus' match isn't seat 1 of the PC's ({ms?.LocalSeat})";
            Console.WriteLine("  " + requests.Last(r => r.Kind == FrontendRequestKind.StartMatch).Arg);
            return null;
        }

        // ---------------------------------------------------------------- shared

        private static FrontendSession Session(string dir)
        {
            RecompMenus.OnlineNoticeSkipped = true;
            Directory.CreateDirectory(dir);
            return FrontendSession.Load(Paths.FileSystem, settingsPath: Path.Combine(dir, "recomp_settings.json"));
        }

        private static void Run(FrontendSession s, int frames)
        {
            for (int f = 0; f < frames; f++)
            {
                s.Tick();
                Thread.Sleep(16);
            }
        }

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

        private static void Shot(FrontendSession s, string name)
        {
            s.Build(1920, 1080);
            using Image<Rgba32> img = FrontendTool.RasterCanvas(s.DrawList, 1920, 1080);
            string dir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "extract_out", "online_menus", "build"));
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, name + ".png");
            img.SaveAsPng(path);
            Console.WriteLine($"  page {s.Menu.Page?.Index} -> {path}");
        }

        private static PlayerCard Card(string name, int tag, bool friend = false, bool rival = false) => new()
        {
            Code = (name.GetHashCode() & 0x7fffffff).ToString("D12")[..12], Name = name, Tag = tag, Presence = Presence.Online, Friend = friend,
            Rival = rival,
        };

        private static bool Until(Func<bool> done, int seconds)
        {
            var clock = Stopwatch.StartNew();
            while (!done() && clock.Elapsed.TotalSeconds < seconds) Thread.Sleep(50);
            return done();
        }

        private static List<string> Lines(List<string> output)
        {
            lock (output) return output.ToList();
        }

        private static bool Has(List<string> output, string prefix)
        {
            lock (output) return output.Any(l => l.StartsWith(prefix));
        }

        private static void Dump(string who, List<string> output)
        {
            foreach (string line in Lines(output)) Console.WriteLine($"  [{who}] {line}");
        }

        private static Process Spawn(string arguments, List<string> output, bool stdin = false)
        {
            var psi = new ProcessStartInfo(Environment.ProcessPath!, arguments)
            {
                UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = stdin,
                WorkingDirectory = Path.GetDirectoryName(typeof(FindGameTool).Assembly.Location)!
            };
            Process p = Process.Start(psi)!;
            p.OutputDataReceived += (_, e) => { if (e.Data != null) lock (output) output.Add(e.Data); };
            p.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (output) output.Add("stderr: " + e.Data); };
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            return p;
        }

        // a SecondHunt server of this tree's build on free ports, Find Game at test speed (--find-fast)
        private sealed class TestServer
        {
            private Process? _p;
            private readonly List<string> _out = new();
            private string? _db;

            public Uri Start()
            {
                string dll = Path.GetFullPath(Path.Combine(typeof(FindGameTool).Assembly.Location, "..", "..", "..", "..", "..",
                    "SecondHunt.Server", "bin", "Debug", "net9.0", "SecondHuntServer.dll"));
                if (!File.Exists(dll)) throw new InvalidOperationException($"no server build at {dll} (build src/SecondHunt.Server first)");
                var l = new TcpListener(IPAddress.Loopback, 0);
                l.Start();
                int http = ((IPEndPoint)l.LocalEndpoint).Port;
                l.Stop();
                int relay;
                using (var u = new UdpClient(new IPEndPoint(IPAddress.Any, 0))) relay = ((IPEndPoint)u.Client.LocalEndPoint!).Port;
                _db = Path.Combine(Path.GetTempPath(), "secondhunt-findtest-" + Guid.NewGuid().ToString("N")[..8]);
                Directory.CreateDirectory(_db);
                _p = Spawn($"\"{dll}\" --urls http://127.0.0.1:{http} --db \"{Path.Combine(_db, "test.db")}\" --relay-port {relay} --words none.txt --find-fast", _out);
                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(1) };
                var clock = Stopwatch.StartNew();
                while (clock.ElapsedMilliseconds < 15000)
                {
                    try
                    {
                        if (Wait(client.GetStringAsync($"http://127.0.0.1:{http}/v1/health")).StartsWith("ok")) return new Uri($"ws://127.0.0.1:{http}/v1/ws");
                    }
                    catch (Exception ex) when (ex is HttpRequestException or System.Threading.Tasks.TaskCanceledException)
                    {
                    }
                    Thread.Sleep(100);
                }
                throw new InvalidOperationException("the server didn't start");
            }

            public void Stop(bool dump)
            {
                if (_p != null)
                {
                    _p.Kill();
                    _p.WaitForExit();
                    if (dump) Dump("server", _out);
                }
                if (_db == null) return;
                try
                {
                    Directory.Delete(_db, recursive: true);
                }
                catch (IOException)
                {
                }
            }
        }

        // steps at 60 Hz of wall-clock time until the match ends or the time runs out (OnlineMatchTool's)
        private static void RealTime(CampaignHost host, Action step, float seconds)
        {
            var clock = Stopwatch.StartNew();
            long frames = 0;
            while (!host.Ended && clock.Elapsed.TotalSeconds < seconds)
            {
                long due = (long)(clock.Elapsed.TotalSeconds * 60);
                while (frames < due && !host.Ended)
                {
                    step();
                    frames++;
                }
                Thread.Sleep(1);
            }
        }

        // hunt the nearest opponent in sight (OnlineMatchTool's joiner brain)
        private static CampaignInput Brain(CampaignHost host)
        {
            var input = new CampaignInput { SelectWeapon = BeamType.None };
            PlayerEntity me = host.Player;
            if (me.Health == 0)
            {
                input.Buttons = CampaignButtons.Shoot;
                return input;
            }
            PlayerEntity? target = null;
            float best = Single.MaxValue;
            for (int i = 0; i < PlayerEntity.MaxPlayers; i++)
            {
                PlayerEntity p = PlayerEntity.Players[i];
                if (p == me || !p.LoadFlags.TestFlag(LoadFlags.Active) || p.Health == 0) continue;
                float d = (p.Position - me.Position).LengthSquared;
                if (d < best)
                {
                    best = d;
                    target = p;
                }
            }
            if (target != null)
            {
                Vector3 to = target.Position - me.Position;
                float yaw = MathHelper.RadiansToDegrees(MathF.Atan2(to.X, to.Z));
                float pitch = Math.Clamp(MathHelper.RadiansToDegrees(MathF.Atan2(to.Y, MathF.Sqrt(to.X * to.X + to.Z * to.Z))), -80, 80);
                float dyaw = yaw - me.NetYaw;
                dyaw -= 360 * MathF.Floor((dyaw + 180) / 360);
                float dpitch = pitch - me.NetPitch;
                input.AimDelta = new System.Numerics.Vector2(-4 * Math.Clamp(dyaw, -12, 12), -4 * Math.Clamp(dpitch, -6, 6));
                input.Move = new System.Numerics.Vector2(0, 1);
                if (MathF.Abs(dyaw) < 10 && host.Frame % 20 < 12) input.Buttons = CampaignButtons.Shoot;
            }
            return input;
        }
    }
}
