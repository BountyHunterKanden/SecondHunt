using System;
using MphRead.Entities;
using MphRecomp.Arenas;
using MphRecomp.Campaign;
using MphRecomp.Replay;

namespace MphRead
{
    // -replayseat <file> <seat> <fromSec> <toSec> [every frames=1]: plays a replay and prints one seat's trace line (bots
    // and people alike) with the Echoes turret's state, occupant, held ball and the seat's distance to it (11-06: the
    // owner's "sd" replay, pushed through the Shooting Gallery floor by the turret)
    internal static partial class ReplayTest
    {
        private static readonly System.Reflection.FieldInfo _leaverField = typeof(EchoesTurretEntity).GetField("_leaver", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;

        public static void SeatTrace(string[] args)
        {
            if (args.Length < 5)
            {
                Console.WriteLine("usage: -replayseat <file> <seat> <fromSec> <toSec> [every frames]");
                return;
            }
            int seat = Int32.Parse(args[2]);
            float from = Single.Parse(args[3]);
            float to = Single.Parse(args[4]);
            int every = args.Length > 5 ? Int32.Parse(args[5]) : 1;
            if (!Setup())
            {
                return;
            }
            ReplayFile file = ReplayFile.Load(args[1]);
            Console.WriteLine($"{file.Settings.Mode} in {file.Settings.Arena}, {file.Steps.Count} steps, local seat {file.Settings.LocalSeat}");
            _beforeStep = host =>
            {
                float t = host.Frame / 60f;
                if (t < from || t > to || host.Frame % every != 0)
                {
                    return;
                }
                PlayerEntity p = PlayerEntity.Players[seat];
                string turret = "";
                foreach (EntityBase e in host.Scene.Entities)
                {
                    if (e is EchoesTurretEntity tu)
                    {
                        turret += $" | turret {tu.State} occ {(tu.Occupant?.SlotIndex.ToString() ?? "-")} held {(tu.Held?.SlotIndex.ToString() ?? "-")}"
                            + $" at {tu.Position.X:0.00} {tu.Position.Y:0.00} {tu.Position.Z:0.00} d {(p.Position - tu.Position).Xz.Length:0.00}"
                            + $" leaver {(_leaverField.GetValue(tu) is PlayerEntity lv ? lv.SlotIndex : -1)} shell {(tu.EntityCollision[0]?.Collision.Active ?? false)}";
                    }
                }
                Console.WriteLine(PlayerEntity.HostBotTraceLine(p, (int)host.Frame) + $" ms {p.Flags1}" + turret);
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
