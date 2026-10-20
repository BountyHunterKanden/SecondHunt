using System;
using System.Buffers.Binary;
using MphRead.Entities;
using OpenTK.Mathematics;

namespace MphRecomp.Multiplayer.Net
{
    // The host's view of the match after one frame, sent host -> client. Every seat's pose, view, health, weapon
    // and the buttons it held (bots included), plus the match clock and scores. About 60 bytes per seat.
    public sealed class Snapshot
    {
        public uint Tick;
        public float MatchTime;
        public byte MatchState;
        public PlayerNetState[] Seats = Array.Empty<PlayerNetState>();
        public short[] Points = Array.Empty<short>();
        public short[] Kills = Array.Empty<short>();
        public short[] Deaths = Array.Empty<short>();
        // the arena's items, every few frames and whenever they change (null = not in this snapshot)
        public NetItem[]? Items;
        // the mode's state (timers, octoliths, nodes, mode stats) in every mode but Battle (null = none)
        public NetObjective? Objective;
        // online only (SnapshotDelta, per joiner): the newest input of the receiving device's seat the host had applied when
        // it took this snapshot (-1 = none yet), and the frames it has stepped since on that input held (it hadn't come yet)
        public int InputAck = -1;
        public int InputHeld;
        // online: the host's pace byte for this device (NetMatchSession.PaceByte: serial + frames to skip or double); -1 none
        public int InputPace = -1;
        // online only: afflictions, power-ups, turrets and detail stats (SeatExtras; sent by SnapshotDelta beside the bytes
        // Write writes, so the LAN wire is unchanged)
        public SeatExtras? Extras;
        // online only (O5b): the item spawners and drop timers, for a client that becomes the host (HostHandoff)
        public HostHandoff? Handoff;
        // the Trooper's cling + drone per seat (SeatAbilities: LAN protocol 9 / online 8; null = no seat has an ability).
        // The last block of Write's bytes, so online's SnapshotDelta carries it inside the mode block (which runs to the end)
        public SeatAbilities? Abilities;
        // LAN only (14): the detail stats as the host counts them (online sends them in Extras); null = not this snapshot
        public SeatExtras.SeatStats[]? Stats;
        // the Echoes arenas' crates + breakables (NetWorld: LAN protocol 16 / online 15; null = the arena has none) and
        // Shooting Gallery's turret (NetWorld.Turret: LAN 21 / online 19)
        public NetWorld? World;

        public const int HeaderSize = 10;
        public const int SeatSize = 62;
        public int Size => HeaderSize + Seats.Length * SeatSize + 1 + (Items == null ? 0 : 1 + Items.Length * NetItem.Size)
            + 1 + (Objective?.Size ?? 0) + 1 + (Abilities?.Size ?? 0) + 1 + (Stats == null ? 0 : 1 + Stats.Length * SeatExtras.StatsSize)
            + 1 + (World?.Size ?? 0);

        public int Write(Span<byte> dest)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(dest, Tick);
            BinaryPrimitives.WriteSingleLittleEndian(dest[4..], MatchTime);
            dest[8] = MatchState;
            dest[9] = (byte)Seats.Length;
            int o = HeaderSize;
            for (int i = 0; i < Seats.Length; i++)
            {
                WriteSeat(dest[o..], Seats[i], Points[i], Kills[i], Deaths[i]);
                o += SeatSize;
            }
            if (Items == null)
            {
                dest[o++] = 0;
            }
            else
            {
                int count = Math.Min(Items.Length, 255);
                dest[o++] = 1;
                dest[o++] = (byte)count;
                for (int i = 0; i < count; i++)
                {
                    Items[i].Write(dest[o..]);
                    o += NetItem.Size;
                }
            }
            if (Objective == null)
            {
                dest[o++] = 0;
            }
            else
            {
                dest[o++] = 1;
                o += Objective.Write(dest[o..]);
            }
            if (Abilities == null)
            {
                dest[o++] = 0;
            }
            else
            {
                dest[o++] = 1;
                o += Abilities.Write(dest[o..]);
            }
            if (Stats == null)
            {
                dest[o++] = 0;
            }
            else
            {
                dest[o++] = 1;
                dest[o++] = (byte)Stats.Length;
                for (int i = 0; i < Stats.Length; i++)
                {
                    SeatExtras.WriteStats(dest[o..], Stats[i]);
                    o += SeatExtras.StatsSize;
                }
            }
            if (World == null)
            {
                dest[o++] = 0;
            }
            else
            {
                dest[o++] = 1;
                o += World.Write(dest[o..]);
            }
            return o;
        }

        public static Snapshot Read(ReadOnlySpan<byte> src)
        {
            int count = src[9];
            var snap = new Snapshot
            {
                Tick = BinaryPrimitives.ReadUInt32LittleEndian(src),
                MatchTime = BinaryPrimitives.ReadSingleLittleEndian(src[4..]),
                MatchState = src[8],
                Seats = new PlayerNetState[count],
                Points = new short[count],
                Kills = new short[count],
                Deaths = new short[count]
            };
            int o = HeaderSize;
            for (int i = 0; i < count; i++)
            {
                snap.Seats[i] = ReadSeat(src[o..], out snap.Points[i], out snap.Kills[i], out snap.Deaths[i]);
                o += SeatSize;
            }
            if (o < src.Length && src[o++] == 1)
            {
                int items = src[o++];
                snap.Items = new NetItem[items];
                for (int i = 0; i < items; i++)
                {
                    snap.Items[i] = NetItem.Read(src[o..]);
                    o += NetItem.Size;
                }
            }
            if (o < src.Length && src[o++] == 1)
            {
                snap.Objective = NetObjective.Read(src[o..], out int size);
                o += size;
            }
            if (o < src.Length && src[o++] == 1)
            {
                snap.Abilities = SeatAbilities.Read(src[o..], out int size);
                o += size;
            }
            if (o < src.Length && src[o++] == 1 && o < src.Length)
            {
                int stats = src[o++];
                if (src.Length - o >= stats * SeatExtras.StatsSize)
                {
                    snap.Stats = new SeatExtras.SeatStats[stats];
                    for (int i = 0; i < stats; i++)
                    {
                        snap.Stats[i] = SeatExtras.ReadStats(src[o..]);
                        o += SeatExtras.StatsSize;
                    }
                }
            }
            if (o < src.Length && src[o++] == 1)
            {
                snap.World = NetWorld.Read(src[o..], out int size);
                o += size;
            }
            return snap;
        }

        // one seat's SeatSize bytes (the online delta compares these byte ranges: SnapshotDelta.SeatGroups)
        public static void WriteSeat(Span<byte> d, in PlayerNetState s, short points, short kills, short deaths)
        {
            d[0] = s.Flags;
            WriteVec(d[1..], s.Position);
            WriteVec(d[13..], s.Speed);
            WriteUnit(d[25..], s.Facing);
            WriteUnit(d[31..], s.Up);
            BinaryPrimitives.WriteInt16LittleEndian(d[37..], (short)Math.Clamp(MathF.Round(Wrap(s.Yaw) * 65536f / 360f), -32768, 32767));
            BinaryPrimitives.WriteInt16LittleEndian(d[39..], (short)Math.Clamp(MathF.Round(s.Pitch * 100), -18000, 18000));
            d[41] = s.AimEpoch;
            BinaryPrimitives.WriteUInt16LittleEndian(d[42..], s.Health);
            d[44] = (byte)s.Weapon;
            BinaryPrimitives.WriteUInt16LittleEndian(d[45..], s.Weapons);
            BinaryPrimitives.WriteInt16LittleEndian(d[47..], s.Ammo0);
            BinaryPrimitives.WriteInt16LittleEndian(d[49..], s.Ammo1);
            BinaryPrimitives.WriteUInt16LittleEndian(d[51..], s.Controls);
            BinaryPrimitives.WriteInt16LittleEndian(d[53..], points);
            BinaryPrimitives.WriteInt16LittleEndian(d[55..], kills);
            BinaryPrimitives.WriteInt16LittleEndian(d[57..], deaths);
            d[59] = (byte)s.LastAttacker;
            d[60] = (byte)s.LastBeam;
            d[61] = s.LastFlags;
        }

        public static PlayerNetState ReadSeat(ReadOnlySpan<byte> d, out short points, out short kills, out short deaths)
        {
            points = BinaryPrimitives.ReadInt16LittleEndian(d[53..]);
            kills = BinaryPrimitives.ReadInt16LittleEndian(d[55..]);
            deaths = BinaryPrimitives.ReadInt16LittleEndian(d[57..]);
            return new PlayerNetState
            {
                Flags = d[0],
                Position = ReadVec(d[1..]),
                Speed = ReadVec(d[13..]),
                Facing = ReadUnit(d[25..]),
                Up = ReadUnit(d[31..]),
                Yaw = BinaryPrimitives.ReadInt16LittleEndian(d[37..]) * 360f / 65536f,
                Pitch = BinaryPrimitives.ReadInt16LittleEndian(d[39..]) / 100f,
                AimEpoch = d[41],
                Health = BinaryPrimitives.ReadUInt16LittleEndian(d[42..]),
                Weapon = (sbyte)d[44],
                Weapons = BinaryPrimitives.ReadUInt16LittleEndian(d[45..]),
                Ammo0 = BinaryPrimitives.ReadInt16LittleEndian(d[47..]),
                Ammo1 = BinaryPrimitives.ReadInt16LittleEndian(d[49..]),
                Controls = BinaryPrimitives.ReadUInt16LittleEndian(d[51..]),
                LastAttacker = (sbyte)d[59],
                LastBeam = (sbyte)d[60],
                LastFlags = d[61]
            };
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

        // unit vectors as three 16-bit components (~0.00003 precision)
        private static void WriteUnit(Span<byte> d, Vector3 v)
        {
            BinaryPrimitives.WriteInt16LittleEndian(d, (short)Math.Clamp(MathF.Round(v.X * 32767), -32767, 32767));
            BinaryPrimitives.WriteInt16LittleEndian(d[2..], (short)Math.Clamp(MathF.Round(v.Y * 32767), -32767, 32767));
            BinaryPrimitives.WriteInt16LittleEndian(d[4..], (short)Math.Clamp(MathF.Round(v.Z * 32767), -32767, 32767));
        }

        private static Vector3 ReadUnit(ReadOnlySpan<byte> d)
        {
            var v = new Vector3(BinaryPrimitives.ReadInt16LittleEndian(d) / 32767f,
                BinaryPrimitives.ReadInt16LittleEndian(d[2..]) / 32767f, BinaryPrimitives.ReadInt16LittleEndian(d[4..]) / 32767f);
            return v.LengthSquared > 0 ? v.Normalized() : v;
        }

        private static float Wrap(float degrees) => degrees - 360 * MathF.Floor((degrees + 180) / 360);
    }
}
