using System;
using System.Linq;
using System.Reflection;
using MphRead.Entities;
using MphRecomp.Replay;

namespace MphRead
{
    // -replayhpbox <file> [seat] [free]: plays a replay headless and logs the opponent health box of a seat (default the recorder's)
    // each time it opens or switches player, with every player's health and Weavel's half turret, so a box that shows the
    // wrong player or the wrong amount can be matched to what happened (status sweep, owner 2026-10-27).
    internal static partial class ReplayTest
    {
        public static void HealthBox(string[] args)
        {
            if (args.Length < 2)
            {
                Console.WriteLine("usage: -replayhpbox <file> [seat] [free]");
                return;
            }
            if (!Setup())
            {
                return;
            }
            ReplayFile file = ReplayFile.Load(args[1]);
            bool free = args.Contains("free"); // (free: a replay's free cam, nobody's eyes; logs any HUD drawn, which should be none)
            args = args.Where(a => a != "free").ToArray();
            int seat = args.Length > 2 ? Int32.Parse(args[2]) : file.Settings.LocalSeat;
            int hudSteps = 0;
            Console.WriteLine($"{file.Settings.Mode} in {file.Settings.Arena}, {file.Settings.Players.Count} players, watching seat {seat}");
            FieldInfo idx = typeof(PlayerEntity).GetField("_opponentIndex", BindingFlags.NonPublic | BindingFlags.Instance)!;
            FieldInfo timer = typeof(PlayerEntity).GetField("_opponentHealthbarTimer", BindingFlags.NonPublic | BindingFlags.Instance)!;
            int lastIdx = -1;
            float lastTimer = 0;
            int[] lastHealth = new int[8];
            int[] lastTurret = new int[8];
            bool first = true;
            SimSnapshot? snap = null;
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
                if (free)
                {
                    // (a seek in free cam: the checkpoint taken from the recorder's eyes, restored at 900 with its HUD list,
                    // which the viewer's AfterRestore empties, as MatchReplay does; the restored match then plays on)
                    if (host.Frame == 599)
                    {
                        ReplayCheckpoints.SetUpOnce();
                        snap = SimSnapshot.Take(599, ReplayCheckpoints.Roots(host));
                    }
                    else if (host.Frame == 900 && snap != null)
                    {
                        PlayerEntity.HostNoViewer = true;
                        snap.Restore();
                        Console.WriteLine($"  seek in free cam: restored step 599, HUD {host.Scene.HudItemCount} items before the clear"
                            + $", no viewer {PlayerEntity.HostNoViewer}");
                        host.Scene.HostClearHud();
                        snap = null;
                    }
                    // (the first 600 steps from the recorder's eyes: the draw list has a HUD to miss)
                    PlayerEntity.HostNoViewer = host.Frame >= 600;
                    if (host.Scene.HudItemCount > 0 && (host.Frame < 600 ? host.Frame == 599 : hudSteps++ < 20))
                    {
                        Console.WriteLine($"  {t} {(host.Frame < 600 ? "own view" : "FREE CAM")} HUD: {host.Scene.HudItemCount} items");
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
                Run r = Replay(file, free ? View.Draw : View.NoDraw, types: false, reference: null, stopOnFail: false);
                Console.WriteLine(r.Verdict());
                if (free) Console.WriteLine($"free cam: {hudSteps} steps drew a HUD");
            }
            finally
            {
                PlayerEntity.HostNoViewer = false;
                _beforeStep = null;
            }
        }
    }
}
