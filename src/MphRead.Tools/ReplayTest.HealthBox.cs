using System;
using System.Linq;
using System.Reflection;
using MphRead.Entities;
using MphRecomp.Replay;

namespace MphRead
{
    // -replayhpbox <file> [seat]: plays a replay headless and logs the opponent health box of a seat (default the recorder's)
    // each time it opens or switches player, with every player's health and Weavel's half turret, so a box that shows the
    // wrong player or the wrong amount can be matched to what happened (status sweep, owner 2026-10-27).
    internal static partial class ReplayTest
    {
        public static void HealthBox(string[] args)
        {
            if (args.Length < 2)
            {
                Console.WriteLine("usage: -replayhpbox <file> [seat]");
                return;
            }
            if (!Setup())
            {
                return;
            }
            ReplayFile file = ReplayFile.Load(args[1]);
            int seat = args.Length > 2 ? Int32.Parse(args[2]) : file.Settings.LocalSeat;
            Console.WriteLine($"{file.Settings.Mode} in {file.Settings.Arena}, {file.Settings.Players.Count} players, watching seat {seat}");
            FieldInfo idx = typeof(PlayerEntity).GetField("_opponentIndex", BindingFlags.NonPublic | BindingFlags.Instance)!;
            FieldInfo timer = typeof(PlayerEntity).GetField("_opponentHealthbarTimer", BindingFlags.NonPublic | BindingFlags.Instance)!;
            int lastIdx = -1;
            float lastTimer = 0;
            int[] lastHealth = new int[8];
            int[] lastTurret = new int[8];
            bool first = true;
            _beforeStep = host =>
            {
                var players = PlayerEntity.Players;
                int count = file.Settings.Players.Count;
                string Who(int i) => $"{i}:{GameState.Nicknames[i]}({players[i].Hunter})";
                if (first)
                {
                    first = false;
                    Console.WriteLine("  seats: " + String.Join("  ", Enumerable.Range(0, count).Select(Who)));
                }
                string t = $"f{host.Frame,6} ({host.Frame / 60f,6:0.0}s)";
                for (int i = 0; i < count; i++)
                {
                    PlayerEntity p = players[i];
                    int turret = p.Flags2.TestFlag(PlayerFlags2.Halfturret) ? p.Halfturret.Health : -1;
                    if (turret != lastTurret[i])
                    {
                        Console.WriteLine($"  {t} {Who(i)} turret {lastTurret[i]} -> {turret} (owner health {p.Health})");
                        lastTurret[i] = turret;
                    }
                    if (p.Health != lastHealth[i])
                    {
                        if (p.Health < lastHealth[i] || (i == lastIdx && lastTimer > 0))
                        {
                            Console.WriteLine($"  {t} {Who(i)} health {lastHealth[i]} -> {p.Health}");
                        }
                        lastHealth[i] = p.Health;
                    }
                }
                PlayerEntity me = players[seat];
                int oi = (int)idx.GetValue(me)!;
                float ot = (float)timer.GetValue(me)!;
                if (oi != lastIdx || ot > lastTimer + 0.001f)
                {
                    Console.WriteLine(oi < 0 ? $"  {t} BOX closed"
                        : $"  {t} BOX {(oi == lastIdx ? "refreshed" : "opens")} on {Who(oi)}: health {players[oi].Health}/{players[oi].HealthMax}"
                            + (players[oi].Flags2.TestFlag(PlayerFlags2.Halfturret) ? $", turret {players[oi].Halfturret.Health}" : ""));
                }
                (lastIdx, lastTimer) = (oi, ot);
            };
            try
            {
                Run r = Replay(file, View.NoDraw, types: false, reference: null, stopOnFail: false);
                Console.WriteLine(r.Verdict());
            }
            finally
            {
                _beforeStep = null;
            }
        }
    }
}
