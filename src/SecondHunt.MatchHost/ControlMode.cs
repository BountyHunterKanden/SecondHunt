using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using MphRecomp.Frontend;
using MphRecomp.Multiplayer;
using MphRecomp.Multiplayer.Net;
using MphRecomp.Online;

namespace SecondHunt.MatchHost
{
    // --control (docs/DEDICATED_SERVERS.md D3): the server started this process for one served game. It joins the game's
    // relay room on the slot the server gave it (0, over loopback), hosts the lobby with no seat of its own and plays each
    // match the lobby's owner starts (NetMatchOwner.cs), until the server says stop or goes away.
    //   server -> here (stdin, one per line):  owner <slot>   the lobby's owner is now this relay slot (-1 = none)
    //                                          stop           the game is closed: leave
    //   here -> server (stdout, lines starting with '!'; any other line is a log line):
    //                                          !ready <version>   on the relay, hosting (the game version it plays)
    //                                          !started           a match started (the game is started on the server)
    //                                          !lobby             the match is over: the lobby again
    //                                          !bye <reason>      leaving
    // A closed stdin counts as stop: this process never outlives the server that started it.
    // --find (a Find Game match the server opened, HubServedFind.cs): no owner until the first match; this host starts it
    // itself like a player host's device did (OnlineMenus.Find.cs TickFind): all --expect players in, none choosing or on the
    // results -> 3 s -> START; after 25 s it starts with whoever is in (2 or more), or leaves when that's fewer.
    internal static class ControlMode
    {
        private const long FindStartDelayMs = 3000, FindStartLateMs = 25000;

        public static int Run(Program.Options o, string romKey)
        {
            var relayInfo = new RelayInfo { Host = o.RelayHost, Port = o.RelayPort, Token = o.Token, Slot = 0 };
            using RelayTransport relay = RelayTransport.For(relayInfo, new Uri($"ws://{o.RelayHost}/"));
            MatchSettings template = Program.Template(o);
            Program.ReplayBuild = $"match host {OnlineMatch.VersionKey(romKey)}";
            using NetMatchSession net = NetMatchSession.Host(relay, template, romKey, o.Seats);
            Program.Netcode(net); // (the snapshot rate the server set: Program.Netcode.cs)
            net.OwnerSlot = o.Owner;
            using var stop = new CancellationTokenSource();
            var reader = new Thread(() => ReadServer(net, stop)) { IsBackground = true, Name = "control" };
            reader.Start();
            Program.Log($"served game: {template.Mode} in {template.Arena}, up to {o.Seats} seats, owner slot {o.Owner}, relay {relay.Relay}");
            Say($"ready {OnlineMatch.VersionKey(romKey)}");
            string bye = "stopped";
            bool findStart = o.Find && o.Expect > 0;
            long readyAt = Environment.TickCount64, startAt = 0;
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    bool go = net.TakeOwnerStart(out string reason);
                    if (!go && findStart)
                    {
                        long now = Environment.TickCount64;
                        int joined = net.Joined;
                        bool all = joined >= o.Expect && net.StillChoosing().Count == 0 && net.NotBack().Count == 0;
                        if (!all) startAt = 0;
                        else if (startAt == 0) startAt = now + FindStartDelayMs;
                        bool late = now - readyAt >= FindStartLateMs;
                        if (startAt > 0 && now >= startAt || late && joined >= 2)
                        {
                            Program.Log($"find: starting with {joined}/{o.Expect}");
                            go = true;
                        }
                        else if (late)
                        {
                            Program.Log($"find: only {joined}/{o.Expect} came in {FindStartLateMs / 1000} s");
                            bye = "nobody came";
                            break;
                        }
                    }
                    if (go)
                    {
                        string arena = net.Settings!.Arena;
                        if (net.Lacking(arena) is { Count: > 0 } lacking)
                        {
                            // (the owner's START waits: LOBBY shows the arena; TODO D4: tell the owner who lacks it)
                            Program.Log($"start refused: {string.Join(", ", lacking)} don't have {arena} (or have another copy of it)");
                            continue;
                        }
                        net.KickNotBack(reason.Length > 0 ? reason : "the game started without you.");
                        if (net.Joined == 0)
                        {
                            continue;
                        }
                        net.SendStart();
                        findStart = false; // (the next matches: the lobby's owner starts them, the server names one now)
                        Say("started");
                        Program.Play(net, o.Stats, stop.Token, ended =>
                        {
                            if (ended)
                            {
                                Say("result " + Result(net));
                            }
                        });
                        net.TakeOwnerStart(out _); // (a late copy of that START is no start for the next match)
                        Say("lobby");
                    }
                    Thread.Sleep(20);
                }
            }
            catch (Exception ex)
            {
                bye = "crashed: " + ex.GetType().Name;
                Program.Log(ex.ToString());
                return 1;
            }
            finally
            {
                Program.CloseReplayLink(5000); // (the last match's recording to its players first)
                Say($"bye {bye}");
            }
            return 0;
        }

        // the finished match for the server's head to head (its own word, not a player's): each human seat's relay slot, place
        // and team as RESULTS shows them (a team mode: the team's best place)
        private static string Result(NetMatchSession net)
        {
            MatchResults r = MatchResults.Capture(0);
            var rows = new List<object>();
            foreach (MatchResults.Player p in r.Rows)
            {
                int slot = p.Bot ? -1 : net.SlotOfSeat(p.Seat);
                if (slot < 0)
                {
                    continue;
                }
                int place = r.Teams ? r.Rows.Where(x => x.Team == p.Team).Min(x => x.Place) : p.Place;
                rows.Add(new { slot, place, team = r.Teams ? p.Team : -1 });
            }
            return JsonSerializer.Serialize(new { teams = r.Teams, rows });
        }

        private static void ReadServer(NetMatchSession net, CancellationTokenSource stop)
        {
            try
            {
                while (Console.In.ReadLine() is string line)
                {
                    string[] parts = line.Trim().Split(' ', 2);
                    if (parts[0] == "stop")
                    {
                        break;
                    }
                    if (parts[0] == "owner" && parts.Length == 2 && Int32.TryParse(parts[1], out int slot))
                    {
                        net.OwnerSlot = slot;
                        Program.Log($"owner: slot {slot}");
                    }
                }
            }
            catch (Exception ex) when (ex is System.IO.IOException or ObjectDisposedException)
            {
            }
            stop.Cancel();
        }

        private static void Say(string line)
        {
            lock (Console.Out)
            {
                Console.WriteLine("!" + line);
                Console.Out.Flush();
            }
        }
    }
}
