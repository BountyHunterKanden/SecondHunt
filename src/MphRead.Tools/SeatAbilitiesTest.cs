using System;
using System.Collections.Generic;
using MphRead.Entities;
using MphRecomp.Multiplayer.Net;

namespace MphRead
{
    // -seatabilitiestest: the Trooper's cling + drone state on the wire (Net/SeatAbilities, LAN protocol 9 / online 8).
    // A snapshot's bytes read back the same (LAN), with and without a mode block, and online deltas rebuild them from no
    // base and from a base with other values; a snapshot with no ability seat reads back with none. No game data needed.
    internal static class SeatAbilitiesTest
    {
        public static void Run()
        {
            var errors = new List<string>();
            var rng = new Random(7);
            Snapshot? prev = null;
            var receiver = new SnapshotDelta.Receiver();
            SnapshotDelta.Encoded? prevEncoded = null;
            for (int round = 0; round < 40; round++)
            {
                Snapshot snap = Make(rng, (uint)(round + 1), withAbilities: round % 5 != 4, withObjective: round % 2 == 0);
                // LAN: the bytes as they are
                byte[] buffer = new byte[snap.Size + 8];
                int length = snap.Write(buffer);
                if (length != snap.Size)
                {
                    errors.Add($"round {round}: wrote {length} bytes, Size says {snap.Size}");
                }
                Compare(errors, $"round {round} LAN", snap, Snapshot.Read(buffer.AsSpan(0, length)));
                // online: a delta from the previous snapshot (when the seat count matches), else everything literal
                SnapshotDelta.Encoded cur = SnapshotDelta.Encode(snap);
                byte[] packet = new byte[SnapshotDelta.MaxSize(cur)];
                int size = SnapshotDelta.Write(packet, cur, prevEncoded, inputAck: round, held: 0);
                Snapshot? back = receiver.Read(packet.AsSpan(0, size));
                if (back == null)
                {
                    errors.Add($"round {round} online: the receiver lacked the base");
                }
                else
                {
                    Compare(errors, $"round {round} online ({(prevEncoded == null ? "literal" : "delta")})", snap, back);
                }
                prev = snap;
                prevEncoded = cur;
            }
            foreach (string e in errors)
            {
                Console.WriteLine("  WRONG " + e);
            }
            Console.WriteLine(errors.Count == 0 ? "seatabilitiestest PASS (40 snapshots: LAN bytes + online deltas, with and without abilities / mode)"
                : $"seatabilitiestest FAIL ({errors.Count})");
        }

        private static Snapshot Make(Random rng, uint tick, bool withAbilities, bool withObjective)
        {
            const int seats = 4;
            var snap = new Snapshot
            {
                Tick = tick, MatchTime = 100 - tick, MatchState = 1, Seats = new PlayerNetState[seats],
                Points = new short[seats], Kills = new short[seats], Deaths = new short[seats]
            };
            for (int i = 0; i < seats; i++)
            {
                snap.Seats[i] = new PlayerNetState { Health = (ushort)rng.Next(0, 200), Weapon = 0 };
                snap.Points[i] = (short)rng.Next(0, 10);
            }
            if (withObjective)
            {
                snap.Objective = new NetObjective();
            }
            if (withAbilities)
            {
                var a = new SeatAbilities { Cling = new PlayerNetCling[seats], Drone = new PlayerNetDrone[seats], SquadMark = new byte[seats] };
                for (int i = 0; i < seats; i++)
                {
                    a.Cling[i] = new PlayerNetCling
                    {
                        Clinging = rng.Next(2) == 1, Timer = (byte)rng.Next(256), Chain = (byte)rng.Next(4),
                        NormalX = (sbyte)rng.Next(-127, 128), NormalZ = (sbyte)rng.Next(-127, 128)
                    };
                    a.Drone[i] = new PlayerNetDrone
                    {
                        FireState = (byte)rng.Next(4), FireFrame = (byte)rng.Next(256), Cooldown = (byte)rng.Next(121),
                        LaserFrame = rng.Next(3) == 0 ? (byte)255 : (byte)rng.Next(255), LaserPattern = (byte)rng.Next(4),
                        LaserWait = (ushort)rng.Next(601), LaserDist = (byte)rng.Next(256)
                    };
                    a.SquadMark[i] = (byte)rng.Next(256); // the Squad Mark: the marking seats (LAN 20 / online 18)
                }
                snap.Abilities = a;
            }
            return snap;
        }

        private static void Compare(List<string> errors, string what, Snapshot a, Snapshot b)
        {
            if (b.Seats.Length != a.Seats.Length || b.Points[^1] != a.Points[^1] || b.Seats[^1].Health != a.Seats[^1].Health)
            {
                errors.Add($"{what}: the seats differ");
            }
            if ((a.Objective == null) != (b.Objective == null))
            {
                errors.Add($"{what}: mode block {(a.Objective == null ? "none" : "present")} read as {(b.Objective == null ? "none" : "present")}");
            }
            if (a.Abilities == null || b.Abilities == null)
            {
                if ((a.Abilities == null) != (b.Abilities == null))
                {
                    errors.Add($"{what}: abilities {(a.Abilities == null ? "none" : "present")} read as {(b.Abilities == null ? "none" : "present")}");
                }
                return;
            }
            if (a.Abilities.Cling.Length != b.Abilities.Cling.Length)
            {
                errors.Add($"{what}: {a.Abilities.Cling.Length} ability seats read as {b.Abilities.Cling.Length}");
                return;
            }
            for (int i = 0; i < a.Abilities.Cling.Length; i++)
            {
                if (!a.Abilities.Cling[i].Equals(b.Abilities.Cling[i]))
                {
                    errors.Add($"{what}: seat {i} cling differs");
                }
                if (!a.Abilities.Drone[i].Equals(b.Abilities.Drone[i]))
                {
                    errors.Add($"{what}: seat {i} drone differs");
                }
                if (b.Abilities.SquadMark.Length <= i || a.Abilities.SquadMark[i] != b.Abilities.SquadMark[i])
                {
                    errors.Add($"{what}: seat {i} squad mark differs");
                }
            }
        }
    }
}
