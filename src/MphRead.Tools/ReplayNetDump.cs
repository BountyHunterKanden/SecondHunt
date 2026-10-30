using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using MphRead.Entities;
using MphRecomp.Campaign;
using MphRecomp.Frontend;
using MphRecomp.Multiplayer;
using MphRecomp.Replay;

namespace MphRead
{
    // -replaynetdump <file> [every=<steps>]: a device's network match recording as its host saw it (release 0.2.0 F1). Prints
    // the seats, each remote seat's drops and rejoins, how many inputs came from it in each window, and every seat's state
    // (active / spawned / health / drawn / kills / deaths) from the first drop on; at the end the RESULTS rows and the table
    // the page fills. For an owner's report (a rejoined seat invisible, a row missing from RESULTS) from the replay alone.
    // A format 47 host recording also carries the host's shot check (ReplayFile.ShotChecks): a summary per seat always, and
    // with shots=1 every shot and claimed hit (step, kind, gun position / aim off, victim and how far off), then it stops
    // (no playback needed). For "my hit didn't register" from a shared replay alone.
    internal static class ReplayNetDump
    {
        public static void Run(string[] args)
        {
            if (args.Length < 2)
            {
                Console.WriteLine("usage: -replaynetdump <file> [every=<steps>]");
                return;
            }
            int every = 60;
            bool shotsOnly = false; // shots=1: the shot check's every entry, no playback
            int fromArg = -1; // from=<step>: print from there (default: 4 windows before the first drop)
            bool raw = false; // raw=1: each remote input record that lands on a printed step
            foreach (string a in args.Skip(2))
            {
                if (a.StartsWith("every=")) every = Int32.Parse(a[6..]);
                if (a == "raw=1") raw = true;
                if (a.StartsWith("from=")) fromArg = Int32.Parse(a[5..]);
                if (a == "shots=1") shotsOnly = true;
            }
            Trace.Listeners.Clear();
            // (shots=1 reads only the file: no game files needed)
            if (!shotsOnly && !ToolPaths.UseDesktopExtraction())
            {
                return;
            }
            ReplayFile file = ReplayFile.Load(args[1]);
            Console.WriteLine($"{file.Settings.Mode} in {file.Settings.Arena}, {file.Steps.Count} steps, local seat "
                + $"{file.Settings.LocalSeat}, own seat {file.OwnSeat} ({file.Build})");
            for (int i = 0; i < file.Settings.Players.Count; i++)
            {
                MatchPlayer p = file.Settings.Players[i];
                Console.WriteLine($"  seat {i}: {p.Hunter} team {p.Team} {(p.Bot ? "bot" : p.Remote ? "remote" : "local")} '{p.Nickname}'");
            }
            if (file.ShotChecks.Count > 0)
            {
                Console.WriteLine("shot check (the host's view of each guest's reported shots and hits):");
                foreach (IGrouping<byte, ReplayShotCheck> seat in file.ShotChecks.GroupBy(c => c.Seat).OrderBy(g => g.Key))
                {
                    var shots = seat.Where(c => c.Kind is ReplayShotKind.ShotMatched or ReplayShotKind.ShotLost).ToList();
                    var matched = shots.Where(c => c.Kind == ReplayShotKind.ShotMatched).ToList();
                    var claims = seat.Where(c => c.Kind >= ReplayShotKind.ClaimAgrees).ToList();
                    Console.WriteLine($"  seat {seat.Key}: shots {shots.Count}, lost {shots.Count - matched.Count}, gun off avg "
                        + $"{(matched.Count > 0 ? matched.Average(c => c.OriginOff) : 0):0.00} max {(matched.Count > 0 ? matched.Max(c => c.OriginOff) : 0):0.00}, "
                        + $"aim off avg {(matched.Count > 0 ? matched.Average(c => c.AimOff) : 0):0.0} deg; hits claimed {claims.Count}, agree "
                        + $"{claims.Count(c => c.Kind == ReplayShotKind.ClaimAgrees)}, disagree {claims.Count(c => c.Kind == ReplayShotKind.ClaimDisagrees)}, "
                        + $"unknown {claims.Count(c => c.Kind == ReplayShotKind.ClaimUnknown)}");
                }
                if (shotsOnly)
                {
                    foreach (ReplayShotCheck c in file.ShotChecks)
                    {
                        Console.WriteLine($"    step {c.Step} ({c.Step / 60 / 60}:{c.Step / 60 % 60:00}) seat {c.Seat} {c.Kind}"
                            + (c.Kind == ReplayShotKind.ShotMatched ? $" gun off {c.OriginOff:0.00} aim off {c.AimOff:0.0} deg" : "")
                            + (c.Victim >= 0 ? $" victim seat {c.Victim}" + (c.Kind != ReplayShotKind.ClaimUnknown ? $" off {c.VictimOff:0.00}" : "") : ""));
                    }
                    return;
                }
            }
            else if (shotsOnly)
            {
                Console.WriteLine("no shot check in this file (a host recording from format 47 on, with guests on the new app)");
                return;
            }
            List<ReplayNetEvent> events = file.NetEvents;
            foreach (ReplayNetEvent e in events.Where(e => e.Kind is not ReplayNetKind.Input and not ReplayNetKind.Position))
            {
                Console.WriteLine($"  step {e.Step}: seat {e.Seat} {e.Kind}");
            }
            // (0.3.6: the guests' own positions the host took, one most steps: counted per seat, not listed)
            foreach (IGrouping<byte, ReplayNetEvent> g in events.Where(e => e.Kind == ReplayNetKind.Position).GroupBy(e => e.Seat))
            {
                Console.WriteLine($"  seat {g.Key}: {g.Count()} positions taken");
            }
            int firstDrop = events.Where(e => e.Kind == ReplayNetKind.Drop).Select(e => e.Step).DefaultIfEmpty(-1).First();
            if (ReplayPlayer.CheckBeforeStart(file) is string why)
            {
                Console.WriteLine("refused: " + why);
                return;
            }
            var player = new ReplayPlayer(file);
            CampaignHost host = player.Start();
            int seats = file.Settings.Players.Count;
            var inputs = new int[seats];
            var shoots = new int[seats];
            var moves = new int[seats];
            int from = fromArg >= 0 ? fromArg : firstDrop >= 0 ? Math.Max(0, firstDrop - 4 * every) : file.Steps.Count; // (no drop: only the end)
            while (!host.Ended && player.Position < file.Steps.Count)
            {
                int step = player.Position;
                foreach (ReplayNetEvent e in events.Where(e => e.Step == step && e.Kind == ReplayNetKind.Input))
                {
                    inputs[e.Seat]++;
                    var ni = MphRecomp.Multiplayer.Net.NetInput.ReadRecord(e.Input!);
                    if ((ni.Buttons & CampaignButtons.Shoot) != 0) shoots[e.Seat]++;
                    if (ni.MoveX != 0 || ni.MoveY != 0) moves[e.Seat]++;
                    if (raw && step >= from && step % every == 0)
                    {
                        Console.WriteLine($"    raw seat {e.Seat} seq {ni.Seq} buttons {ni.Buttons} move {ni.MoveX},{ni.MoveY} "
                            + $"yaw {ni.Yaw:0.0} pitch {ni.Pitch:0.0} view {ni.ViewTick} touching {ni.Touching}");
                    }
                }
                CampaignInput input = file.Steps[step].Input;
                player.BeforeStep();
                host.Step(input);
                if (!player.Check(host.Scene))
                {
                    Console.WriteLine($"  step {step}: state differs from the recording (stopping)");
                    break;
                }
                if (step >= from && step % every == 0)
                {
                    Console.WriteLine($"  step {step} {GameState.MatchState} inputs[{string.Join(",", inputs)}] shoot[{string.Join(",", shoots)}] "
                        + $"move[{string.Join(",", moves)}] " + string.Join(" ", Enumerable.Range(0, seats).Select(Seat)));
                    Array.Clear(inputs);
                    Array.Clear(shoots);
                    Array.Clear(moves);
                }
            }
            MatchResults r = MatchResults.Capture(file.Settings.LocalSeat);
            Console.WriteLine($"RESULTS rows {r.Rows.Count}: " + string.Join(", ", r.Rows.Select(x => $"seat {x.Seat} team {x.Team} place {x.Place}"))
                + $"; table rows " + string.Join(",", MultiplayerMenu.ResultsShown(r).Select(x => x == null ? "-" : x.Seat.ToString())));
            host.Dispose();
        }

        private static string Seat(int i)
        {
            PlayerEntity p = PlayerEntity.Players[i];
            return $"{i}:{(p.LoadFlags.TestFlag(LoadFlags.Active) ? "A" : "a")}{(p.LoadFlags.TestFlag(LoadFlags.Spawned) ? "S" : "s")}"
                + $"{(p.Flags2.TestFlag(PlayerFlags2.HideModel) ? "h" : "D")}{p.Health}k{GameState.Kills[i]}d{GameState.Deaths[i]}"
                + $"@{p.Position.X:0},{p.Position.Y:0},{p.Position.Z:0}";
        }
    }
}
