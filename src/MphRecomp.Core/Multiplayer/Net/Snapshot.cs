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

        public const int HeaderSize = 10;
        public const int SeatSize = 62;
        public int Size => HeaderSize + Seats.Length * SeatSize + 1 + (Items == null ? 0 : 1 + Items.Length * NetItem.Size);

        public int Write(Span<byte> dest)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(dest, Tick);
            BinaryPrimitives.WriteSingleLittleEndian(dest[4..], MatchTime);
            dest[8] = MatchState;
            dest[9] = (byte)Seats.Length;
            int o = HeaderSize;
            for (int i = 0; i < Seats.Length; i++)
            {
                ref PlayerNetState s = ref Seats[i];
                Span<byte> d = dest[o..];
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
                BinaryPrimitives.WriteInt16LittleEndian(d[53..], Points[i]);
                BinaryPrimitives.WriteInt16LittleEndian(d[55..], Kills[i]);
                BinaryPrimitives.WriteInt16LittleEndian(d[57..], Deaths[i]);
                d[59] = (byte)s.LastAttacker;
                d[60] = (byte)s.LastBeam;
                d[61] = s.LastFlags;
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
                ReadOnlySpan<byte> d = src[o..];
                snap.Seats[i] = new PlayerNetState
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
                snap.Points[i] = BinaryPrimitives.ReadInt16LittleEndian(d[53..]);
                snap.Kills[i] = BinaryPrimitives.ReadInt16LittleEndian(d[55..]);
                snap.Deaths[i] = BinaryPrimitives.ReadInt16LittleEndian(d[57..]);
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
            return snap;
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
