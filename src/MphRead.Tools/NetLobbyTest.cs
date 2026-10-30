using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using MphRecomp.Multiplayer;
using MphRecomp.Multiplayer.Net;

namespace MphRead
{
    // -netlobbytest (no ROM): NetMatchSession's lobby and rematch (match protocol 4, Online play menus 2026-10-09) over an
    // in-memory relay, a host and two joiners: JOIN, LOOK (hunter, choosing), pings, SetTeam, START, inputs and snapshots,
    // EndMatch (the old match's last snapshot arriving after it is dropped), one back (LOOK) and one on the results, KICK, the
    // rematch's START (its inputs and snapshots counted from 0 again get through), BYE; a bot seat flagged in LOBBY.
    internal static class NetLobbyTest
    {
        public static void Run(string[] args)
        {
            int fails = 0;
            void Check(bool ok, string what)
            {
                Console.WriteLine((ok ? "PASS  " : "FAIL  ") + what);
                if (!ok) fails++;
            }
            static bool Until(Func<bool> done, int ms = 3000)
            {
                long end = Environment.TickCount64 + ms;
                while (Environment.TickCount64 < end)
                {
                    if (done()) return true;
                    Thread.Sleep(5);
                }
                return false;
            }

            _ = Paths.AllPaths; // no ROM here: empty paths, so the JOIN's arena prints (ExtraArenas.WritePrints) find nothing instead of throwing
            var room = new Room();
            var t0 = room.Slot(0); var t1 = room.Slot(1); var t2 = room.Slot(2);
            var template = MatchSettings.Quick(GameMode.BattleTeams, "Combat Hall", Hunter.Samus, 0);
            template.Players[0].Nickname = "HOST";
            template.Players.Add(new MatchPlayer { Hunter = Hunter.Noxus, Bot = true, BotLevel = 1, Nickname = "NOXUS" });
            var host = NetMatchSession.Host(t0, template, "rom");
            host.LocalPingMs = () => 30;
            var a = NetMatchSession.Client(t1, "rom"); a.LocalPingMs = () => 40;
            var b = NetMatchSession.Client(t2, "rom"); b.LocalPingMs = () => 50;
            int changes = 0; host.PlayersChanged += () => Interlocked.Increment(ref changes);
            Check(a.Join(Hunter.Kanden, "ALPHA", 1), "A joins");
            Check(b.Join(Hunter.Spire, "BRAVO", 2), "B joins");
            Check(Until(() => a.Settings?.Players.Count == 4 && a.Lobby.Count == 4), $"A's lobby shows all four (LOBBY on B's join): {a.Settings?.Players.Count}");
            Check(a.Lobby[3].Bot && !a.Lobby[1].Bot && !a.Settings!.Players[3].Bot, "the bot's seat is flagged in LOBBY (the joiner's match has no bots)");
            // look change + choosing
            a.SetLook(Hunter.Trace, null, null, choosing: true);
            Check(Until(() => host.Settings!.Players[1].Hunter == Hunter.Trace && host.StillChoosing().Contains("ALPHA")), "A's LOOK: Trace, choosing (host)");
            Check(Until(() => b.Settings!.Players[1].Hunter == Hunter.Trace && b.Lobby[1].Choosing), "B sees A as Trace, choosing");
            a.SetLook(Hunter.Trace, null, null, choosing: false);
            Check(Until(() => host.StillChoosing().Count == 0), "A done choosing");
            // pings
            host.LobbyTick(true); a.LobbyTick(true); b.LobbyTick(true);
            Check(Until(() => b.Lobby.Count == 4 && b.Lobby[0].PingMs == 30 && b.Lobby[1].PingMs == 40, 4000),
                $"pings by seat: {string.Join(",", b.Lobby.Select(s => s.PingMs))}");
            // teams
            int[] teams = host.Settings!.TeamAssignment();
            host.SetTeam(2, 1 - teams[2]);
            Check(Until(() => a.Settings!.TeamAssignment()[2] == 1 - teams[2]), $"SetTeam(2): A sees B on team {1 - teams[2]}");
            // start
            host.SendStart();
            Check(Until(() => a.Started && b.Started), "START: both started");
            // the first match: inputs and snapshots well into it
            a.SendInput(new NetInput { Seq = 5000 });
            Check(Until(() => host.TryTakeInput(1, out NetInput i1) && i1.Seq == 5000), "match 1: A's input 5000 reaches the host");
            host.SendSnapshot(new Snapshot { Tick = 9000 });
            Check(Until(() => a.TryTakeSnapshot(out Snapshot s1) && s1.Tick == 9000), "match 1: snapshot tick 9000 reaches A");
            Check(a.Settings!.Players[1].Hunter == Hunter.Trace && a.Settings.LocalSeat == 1, "A plays seat 1 as Trace");
            // the match ends: everyone back in the lobby; A comes back, B stays on the results
            host.EndMatch(); a.EndMatch(); b.EndMatch();
            host.SendSnapshot(new Snapshot { Tick = 9001 }); // (the old match's last one, still on its way)
            Thread.Sleep(100);
            Check(!a.TryTakeSnapshot(out _) && a.SnapshotsReceived == 0, "the old match's late snapshot is dropped after the match ended");
            Check(!a.Started && !b.Started, "clients ready for the next START");
            Check(host.NotBack().Count == 2, $"host: nobody back yet ({string.Join(",", host.NotBack())})");
            Thread.Sleep(1100); a.LobbyTick(true); b.LobbyTick(false);
            Check(Until(() => host.NotBack().SequenceEqual(new[] { "BRAVO" })), $"A back, B on the results: {string.Join(",", host.NotBack())}");
            host.LobbyChanged();
            host.KickNotBack("the game started without you.");
            Check(Until(() => b.Kicked != null), $"B kicked: {b.Kicked}");
            Check(host.Joined == 1 && host.Settings!.Players.Count == 3, $"host: one joiner left, {host.Settings!.Players.Count} seats");
            host.SendStart();
            Check(Until(() => a.Started) && !b.Started, "the rematch starts for A only");
            Check(a.Settings!.Players.Count == 3 && a.Settings.LocalSeat == 1 && a.SnapshotsReceived == 0, "A's rematch: 3 seats, seat 1, a fresh count");
            // the rematch's driver counts from 0 again: its first input and snapshot must get through
            a.SendInput(new NetInput { Seq = 0 });
            Check(Until(() => host.TryTakeInput(1, out NetInput i2) && i2.Seq == 0), "rematch: A's input 0 reaches the host (not taken for an old one)");
            host.SendSnapshot(new Snapshot { Tick = 1 });
            Check(Until(() => a.TryTakeSnapshot(out Snapshot s2) && s2.Tick == 1), "rematch: snapshot tick 1 reaches A");
            // BYE
            a.Dispose();
            Check(Until(() => host.Joined == 0), "A's BYE");
            host.Dispose();
            // a served game's relay slot reused (2026-10-24: Yaba took AB's lobby seat and name): AB never sends a BYE
            {
                var room2 = new Room();
                var h0 = room2.Slot(0); var s1 = room2.Slot(1);
                var tmpl = MatchSettings.Quick(GameMode.Battle, "Combat Hall", Hunter.Samus, 0);
                tmpl.Players[0].Nickname = "HOST";
                var host2 = NetMatchSession.Host(h0, tmpl, "rom");
                var ab = NetMatchSession.Client(s1, "rom");
                Check(ab.Join(Hunter.Kanden, "AB", 1) && Until(() => host2.Joined == 1 && host2.Settings!.Players[1].Nickname == "AB"), "slot reuse: AB sits in slot 1");
                // the slot goes to someone else, AB's BYE lost: a JOIN from the same slot under another name
                var yaba = NetMatchSession.Client(room2.Slot(1), "rom");
                Check(yaba.Join(Hunter.Spire, "YABA", 1) && Until(() => host2.Joined == 1 && host2.Settings!.Players[1].Nickname == "YABA"),
                    $"slot reuse: YABA takes slot 1 as YABA, not AB ({host2.Joined} joined, seat 1 '{host2.Settings!.Players[1].Nickname}')");
                Check(yaba.Settings?.LocalSeat == 1 && yaba.Settings!.Players[1].Nickname == "YABA", "slot reuse: YABA's WELCOME gives them a seat of their own");
                // the same player's JOIN repeated (a WELCOME that got lost) still answers the same seat, nobody new
                yaba.Join(Hunter.Spire, "YABA", 1);
                Thread.Sleep(100);
                Check(host2.Joined == 1 && host2.Settings!.Players.Count == 2, $"slot reuse: a repeated JOIN is no new seat ({host2.Settings!.Players.Count} seats)");
                // the server's word that a slot is free (HubGames.LeaveGame -> "left <slot>"): the seat closes up
                host2.SlotLeft(1);
                Check(Until(() => host2.Joined == 0 && host2.Settings!.Players.Count == 1), $"slot reuse: SlotLeft(1) closes the seat ({host2.Settings!.Players.Count} seats)");
                host2.Dispose(); ab.Dispose(); yaba.Dispose();
            }
            Console.WriteLine(fails == 0 ? "PASS netlobbytest" : $"FAIL netlobbytest: {fails} failed");
        }

        private sealed class Room
        {
            public readonly Dictionary<int, Port> Ports = new();
            public Port Slot(int s) => Ports[s] = new Port(this, s);
        }

        // a relay slot: datagrams to a slot or to everyone else, delivered on the thread pool (another thread, as the relay's)
        private sealed class Port : IDatagramTransport
        {
            private readonly Room _room;
            public Port(Room room, int slot)
            {
                _room = room;
                LocalSlot = slot;
            }
            public int LocalSlot { get; }
            public int MaxPayload => 1400;
            public event DatagramHandler? Received;
            public void Send(int slot, ReadOnlySpan<byte> payload)
            {
                byte[] copy = payload.ToArray();
                if (_room.Ports.TryGetValue(slot, out Port? p)) ThreadPool.QueueUserWorkItem(_ => p.Received?.Invoke(LocalSlot, copy));
            }
            public void SendAll(ReadOnlySpan<byte> payload)
            {
                byte[] copy = payload.ToArray();
                foreach (Port p in _room.Ports.Values.Where(p => p != this)) ThreadPool.QueueUserWorkItem(_ => p.Received?.Invoke(LocalSlot, copy));
            }
            public void Dispose()
            {
            }
        }
    }
}
