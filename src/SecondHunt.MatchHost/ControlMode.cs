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
    internal static class ControlMode
    {
        public static int Run(Program.Options o, string romKey)
        {
            var relayInfo = new RelayInfo { Host = o.RelayHost, Port = o.RelayPort, Token = o.Token, Slot = 0 };
            using RelayTransport relay = RelayTransport.For(relayInfo, new Uri($"ws://{o.RelayHost}/"));
            MatchSettings template = Program.Template(o);
            using NetMatchSession net = NetMatchSession.Host(relay, template, romKey, o.Seats);
            net.OwnerSlot = o.Owner;
            using var stop = new CancellationTokenSource();
            var reader = new Thread(() => ReadServer(net, stop)) { IsBackground = true, Name = "control" };
            reader.Start();
            Program.Log($"served game: {template.Mode} in {template.Arena}, up to {o.Seats} seats, owner slot {o.Owner}, relay {relay.Relay}");
            Say($"ready {OnlineMatch.VersionKey(romKey)}");
            string bye = "stopped";
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    if (net.TakeOwnerStart(out string reason))
                    {
                        string arena = net.Settings!.Arena;
                        if (net.Lacking(arena) is { Count: > 0 } lacking)
                        {
                            // (the owner's START waits: LOBBY shows the arena; TODO D4: tell the owner who lacks it)
                            Program.Log($"start refused: {string.Join(", ", lacking)} don't have {arena}");
                            continue;
                        }
                        net.KickNotBack(reason.Length > 0 ? reason : "the game started without you.");
                        if (net.Joined == 0)
                        {
                            continue;
                        }
                        net.SendStart();
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
