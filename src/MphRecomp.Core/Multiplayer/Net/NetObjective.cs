using System;
using System.Buffers.Binary;
using System.Linq;
using MphRead;
using MphRead.Entities;
using OpenTK.Mathematics;

namespace MphRecomp.Multiplayer.Net
{
    // The mode's state in a snapshot (every mode but Battle): the timers (prime hunter / survival / defender time),
    // the prime hunter, the mode stats the results show, each octolith (holder, place, the host's newest events) and
    // each node (who stands on it, who owns it, capture progress). The host decides all of it; clients replay it.
    public sealed class NetObjective
    {
        public const int StatCount = 7;

        // the seats it carries: MphRead's MaxPlayers, 4 or 8 (P8: LAN protocol 11 / online 10 carry this count first)
        public int SeatCount = PlayerEntity.VanillaMaxPlayers;
        public readonly float[] Time = new float[PlayerEntity.SlotCapacity];
        public readonly float[] TeamTime = new float[PlayerEntity.SlotCapacity];
        public sbyte PrimeHunter = -1;
        // OctolithScores, OctolithDrops, OctolithStops, NodesCaptured, NodesLost, KillsAsPrime, PrimesKilled
        public readonly short[,] Stats = new short[StatCount, PlayerEntity.SlotCapacity];
        public OctolithFlagEntity.NetState[] Octoliths = Array.Empty<OctolithFlagEntity.NetState>();
        public NodeDefenseEntity.NetState[] Nodes = Array.Empty<NodeDefenseEntity.NetState>();

        private const int OctolithSize = 2 + 1 + 1 + 1 + 12 + 4 + 4 + 2 + OctolithFlagEntity.NetEventCount * 2;
        private const int NodeSize = 2 + 1 + 1 + 1 + 1 + 1 + 4 + 4 + 1;

        public int Size => 1 + SeatCount * 8 + 1 + StatCount * SeatCount * 2 + 1 + Octoliths.Length * OctolithSize + 1
            + Nodes.Length * NodeSize;

        private static int[] StatArray(int index) => index switch
        {
            0 => GameState.OctolithScores,
            1 => GameState.OctolithDrops,
            2 => GameState.OctolithStops,
            3 => GameState.NodesCaptured,
            4 => GameState.NodesLost,
            5 => GameState.KillsAsPrime,
            _ => GameState.PrimesKilled
        };

        // host: after the frame
        public static NetObjective Capture(Scene scene)
        {
            var obj = new NetObjective { PrimeHunter = (sbyte)GameState.PrimeHunter, SeatCount = PlayerEntity.MaxPlayers };
            for (int i = 0; i < obj.SeatCount; i++)
            {
                obj.Time[i] = GameState.Time[i];
                obj.TeamTime[i] = GameState.TeamTime[i];
                for (int s = 0; s < StatCount; s++)
                {
                    obj.Stats[s, i] = (short)Math.Clamp(StatArray(s)[i], Int16.MinValue, Int16.MaxValue);
                }
            }
            var octoliths = new System.Collections.Generic.List<OctolithFlagEntity.NetState>();
            foreach (OctolithFlagEntity octolith in scene.GetOctolithFlagEntities())
            {
                octoliths.Add(octolith.NetCapture());
            }
            obj.Octoliths = octoliths.ToArray();
            var nodes = new System.Collections.Generic.List<NodeDefenseEntity.NetState>();
            foreach (NodeDefenseEntity node in scene.GetNodeDefenseEntities())
            {
                nodes.Add(node.NetCapture());
            }
            obj.Nodes = nodes.ToArray();
            return obj;
        }

        // client: the entities first (their events add to the stats and points, which the host's numbers then replace)
        public void Apply(Scene scene)
        {
            foreach (OctolithFlagEntity octolith in scene.GetOctolithFlagEntities())
            {
                for (int i = 0; i < Octoliths.Length; i++)
                {
                    if (Octoliths[i].Id == (ushort)octolith.Id)
                    {
                        octolith.NetApply(Octoliths[i]);
                        break;
                    }
                }
            }
            foreach (NodeDefenseEntity node in scene.GetNodeDefenseEntities())
            {
                for (int i = 0; i < Nodes.Length; i++)
                {
                    if (Nodes[i].Id == (ushort)node.Id)
                    {
                        node.NetApply(Nodes[i]);
                        break;
                    }
                }
            }
            for (int i = 0; i < SeatCount && i < PlayerEntity.MaxPlayers; i++)
            {
                GameState.Time[i] = Time[i];
                GameState.TeamTime[i] = TeamTime[i];
                for (int s = 0; s < StatCount; s++)
                {
                    StatArray(s)[i] = Stats[s, i];
                }
            }
            GameState.PrimeHunter = PrimeHunter;
        }

        public int Write(Span<byte> d)
        {
            int o = 0;
            int seats = SeatCount;
            d[o++] = (byte)seats;
            for (int i = 0; i < seats; i++)
            {
                BinaryPrimitives.WriteSingleLittleEndian(d[o..], Time[i]);
                BinaryPrimitives.WriteSingleLittleEndian(d[(o + seats * 4)..], TeamTime[i]);
                o += 4;
            }
            o = 1 + seats * 8;
            d[o++] = (byte)PrimeHunter;
            for (int s = 0; s < StatCount; s++)
            {
                for (int i = 0; i < seats; i++)
                {
                    BinaryPrimitives.WriteInt16LittleEndian(d[o..], Stats[s, i]);
                    o += 2;
                }
            }
            d[o++] = (byte)Octoliths.Length;
            foreach (OctolithFlagEntity.NetState f in Octoliths)
            {
                BinaryPrimitives.WriteUInt16LittleEndian(d[o..], f.Id);
                d[o + 2] = (byte)f.Carrier;
                d[o + 3] = (byte)f.LastCarrier;
                d[o + 4] = (byte)((f.AtBase ? 1 : 0) | (f.Grounded ? 2 : 0));
                WriteVec(d[(o + 5)..], f.Position);
                BinaryPrimitives.WriteSingleLittleEndian(d[(o + 17)..], f.ResetTimer);
                BinaryPrimitives.WriteSingleLittleEndian(d[(o + 21)..], f.Gravity);
                BinaryPrimitives.WriteUInt16LittleEndian(d[(o + 25)..], f.EventSeq);
                for (int e = 0; e < OctolithFlagEntity.NetEventCount; e++)
                {
                    d[o + 27 + e * 2] = (byte)f.EventKinds[e];
                    d[o + 28 + e * 2] = (byte)f.EventSlots[e];
                }
                o += OctolithSize;
            }
            d[o++] = (byte)Nodes.Length;
            foreach (NodeDefenseEntity.NetState n in Nodes)
            {
                BinaryPrimitives.WriteUInt16LittleEndian(d[o..], n.Id);
                d[o + 2] = n.Presence;
                d[o + 3] = (byte)n.CurrentTeam;
                d[o + 4] = (byte)n.OccupyingTeam;
                d[o + 5] = n.OccupiedBy;
                d[o + 6] = (byte)((n.Contested ? 1 : 0) | (n.InProgress ? 2 : 0));
                BinaryPrimitives.WriteSingleLittleEndian(d[(o + 7)..], n.Progress);
                BinaryPrimitives.WriteSingleLittleEndian(d[(o + 11)..], n.ScoreTimer);
                d[o + 15] = (byte)n.CapturedPlayer;
                o += NodeSize;
            }
            return o;
        }

        public static NetObjective Read(ReadOnlySpan<byte> d, out int size)
        {
            var obj = new NetObjective();
            int o = 0;
            int n = Math.Clamp((int)d[o++], 0, PlayerEntity.SlotCapacity);
            obj.SeatCount = n;
            for (int i = 0; i < n; i++)
            {
                obj.Time[i] = BinaryPrimitives.ReadSingleLittleEndian(d[o..]);
                obj.TeamTime[i] = BinaryPrimitives.ReadSingleLittleEndian(d[(o + n * 4)..]);
                o += 4;
            }
            o = 1 + n * 8;
            obj.PrimeHunter = (sbyte)d[o++];
            for (int s = 0; s < StatCount; s++)
            {
                for (int i = 0; i < n; i++)
                {
                    obj.Stats[s, i] = BinaryPrimitives.ReadInt16LittleEndian(d[o..]);
                    o += 2;
                }
            }
            obj.Octoliths = new OctolithFlagEntity.NetState[d[o++]];
            for (int k = 0; k < obj.Octoliths.Length; k++)
            {
                var f = new OctolithFlagEntity.NetState
                {
                    Id = BinaryPrimitives.ReadUInt16LittleEndian(d[o..]),
                    Carrier = (sbyte)d[o + 2],
                    LastCarrier = (sbyte)d[o + 3],
                    AtBase = (d[o + 4] & 1) != 0,
                    Grounded = (d[o + 4] & 2) != 0,
                    Position = ReadVec(d[(o + 5)..]),
                    ResetTimer = BinaryPrimitives.ReadSingleLittleEndian(d[(o + 17)..]),
                    Gravity = BinaryPrimitives.ReadSingleLittleEndian(d[(o + 21)..]),
                    EventSeq = BinaryPrimitives.ReadUInt16LittleEndian(d[(o + 25)..]),
                    EventKinds = new OctolithFlagEntity.NetEventKind[OctolithFlagEntity.NetEventCount],
                    EventSlots = new sbyte[OctolithFlagEntity.NetEventCount]
                };
                for (int e = 0; e < OctolithFlagEntity.NetEventCount; e++)
                {
                    f.EventKinds[e] = (OctolithFlagEntity.NetEventKind)d[o + 27 + e * 2];
                    f.EventSlots[e] = (sbyte)d[o + 28 + e * 2];
                }
                obj.Octoliths[k] = f;
                o += OctolithSize;
            }
            obj.Nodes = new NodeDefenseEntity.NetState[d[o++]];
            for (int k = 0; k < obj.Nodes.Length; k++)
            {
                obj.Nodes[k] = new NodeDefenseEntity.NetState
                {
                    Id = BinaryPrimitives.ReadUInt16LittleEndian(d[o..]),
                    Presence = d[o + 2],
                    CurrentTeam = (sbyte)d[o + 3],
                    OccupyingTeam = (sbyte)d[o + 4],
                    OccupiedBy = d[o + 5],
                    Contested = (d[o + 6] & 1) != 0,
                    InProgress = (d[o + 6] & 2) != 0,
                    Progress = BinaryPrimitives.ReadSingleLittleEndian(d[(o + 7)..]),
                    ScoreTimer = BinaryPrimitives.ReadSingleLittleEndian(d[(o + 11)..]),
                    CapturedPlayer = (sbyte)d[o + 15]
                };
                o += NodeSize;
            }
            size = o;
            return obj;
        }

        // a text form of the end state (the PC net tests compare it across devices)
        public static string Summary()
        {
            var parts = new System.Collections.Generic.List<string>();
            parts.Add("prime " + GameState.PrimeHunter);
            for (int s = 0; s < StatCount; s++)
            {
                int[] a = StatArray(s);
                parts.Add(String.Join(",", a.Take(PlayerEntity.MaxPlayers)));
            }
            return String.Join(" ", parts);
        }

        private static void WriteVec(Span<byte> d, Vector3 v)
        {
            BinaryPrimitives.WriteSingleLittleEndian(d, v.X);
            BinaryPrimitives.WriteSingleLittleEndian(d[4..], v.Y);
            BinaryPrimitives.WriteSingleLittleEndian(d[8..], v.Z);
        }

        private static Vector3 ReadVec(ReadOnlySpan<byte> d)
        {
            return new Vector3(BinaryPrimitives.ReadSingleLittleEndian(d), BinaryPrimitives.ReadSingleLittleEndian(d[4..]),
                BinaryPrimitives.ReadSingleLittleEndian(d[8..]));
        }
    }
}
