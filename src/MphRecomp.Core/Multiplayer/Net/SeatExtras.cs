using System;
using System.Buffers.Binary;
using MphRead;
using MphRead.Entities;

namespace MphRecomp.Multiplayer.Net
{
    // Online snapshots only (O5 step 4): what a client can't work out from the host's hits and pickups, per seat. A client
    // replays the host's damage without the beam (PlayerEntity.NetApply), so a freeze, a disruption or a burn never started
    // there; puppets never pick up, so a cloak or double damage never showed; Weavel's turret took the split without its
    // health; and the detail stats (streaks, headshots, beam kills, damage) only counted what the client saw. The host
    // sends them here; PlayerEntity.NetApplyExtras starts and ends each the game's own way. Not on LAN (Snapshot.Write
    // never writes this block; since LAN 14 a LAN snapshot carries the detail stats alone, Snapshot.Stats); SnapshotDelta
    // sends it XOR'd against the base.
    public sealed class SeatExtras
    {
        public PlayerNetExtras[] Seats = Array.Empty<PlayerNetExtras>();
        // the detail stats of the first four seats (GameState keeps four)
        public SeatStats[] Stats = Array.Empty<SeatStats>();

        public const int SeatSize = 15;
        public const int StatsSize = 23; // (online 13: + BeamDamageMax, the efficiency's divisor)
        public const int StatSeats = PlayerEntity.SlotCapacity; // (P8: the detail stats of up to 8 seats; was 4)

        public struct SeatStats
        {
            public byte KillStreak, Suicides, FriendlyKills, HeadshotKills;
            public ushort BeamDamageDealt, DamageCount, AltDamageCount;
            public byte[] BeamKills; // 9
            // the beam damage the seat fired (efficiency = dealt / this). A client counted it from the beams spawned
            // there, and puppets' shots and charges aren't the host's: KANBOT read 12% on the host, 36% on the guest
            // (release 0.2.0 F1)
            public uint BeamDamageMax;
        }

        public int Size => 2 + Seats.Length * SeatSize + Stats.Length * StatsSize;

        // host: every seat after this frame
        public static SeatExtras Capture(int seats)
        {
            int stats = Math.Min(seats, StatSeats);
            var x = new SeatExtras { Seats = new PlayerNetExtras[seats] };
            for (int i = 0; i < seats; i++)
            {
                x.Seats[i] = PlayerEntity.Players[i].NetCaptureExtras();
            }
            x.Stats = CaptureStats(stats);
            return x;
        }

        // client: the host's afflictions, power-ups and turrets on every seat, and its stats
        public void Apply()
        {
            for (int i = 0; i < Seats.Length && i < PlayerEntity.MaxPlayers; i++)
            {
                PlayerEntity.Players[i].NetApplyExtras(Seats[i]);
            }
            ApplyStats(Stats);
        }

        public byte[] ToBytes()
        {
            byte[] d = new byte[Size];
            d[0] = (byte)Seats.Length;
            d[1] = (byte)Stats.Length;
            int o = 2;
            for (int i = 0; i < Seats.Length; i++)
            {
                ref PlayerNetExtras e = ref Seats[i];
                BinaryPrimitives.WriteUInt16LittleEndian(d.AsSpan(o), e.Frozen);
                BinaryPrimitives.WriteUInt16LittleEndian(d.AsSpan(o + 2), e.Disrupted);
                BinaryPrimitives.WriteUInt16LittleEndian(d.AsSpan(o + 4), e.Burn);
                d[o + 6] = (byte)e.BurnedBy;
                BinaryPrimitives.WriteUInt16LittleEndian(d.AsSpan(o + 7), e.DoubleDamage);
                BinaryPrimitives.WriteUInt16LittleEndian(d.AsSpan(o + 9), e.Cloak);
                BinaryPrimitives.WriteUInt16LittleEndian(d.AsSpan(o + 11), e.Deathalt);
                BinaryPrimitives.WriteUInt16LittleEndian(d.AsSpan(o + 13), e.Turret);
                o += SeatSize;
            }
            for (int i = 0; i < Stats.Length; i++)
            {
                WriteStats(d.AsSpan(o), Stats[i]);
                o += StatsSize;
            }
            return d;
        }

        // null for an empty block (a host without extras)
        public static SeatExtras? FromBytes(ReadOnlySpan<byte> d)
        {
            if (d.Length < 2)
            {
                return null;
            }
            var x = new SeatExtras { Seats = new PlayerNetExtras[d[0]], Stats = new SeatStats[d[1]] };
            if (d.Length < x.Size)
            {
                return null;
            }
            int o = 2;
            for (int i = 0; i < x.Seats.Length; i++)
            {
                x.Seats[i] = new PlayerNetExtras
                {
                    Frozen = BinaryPrimitives.ReadUInt16LittleEndian(d[o..]),
                    Disrupted = BinaryPrimitives.ReadUInt16LittleEndian(d[(o + 2)..]),
                    Burn = BinaryPrimitives.ReadUInt16LittleEndian(d[(o + 4)..]),
                    BurnedBy = (sbyte)d[o + 6],
                    DoubleDamage = BinaryPrimitives.ReadUInt16LittleEndian(d[(o + 7)..]),
                    Cloak = BinaryPrimitives.ReadUInt16LittleEndian(d[(o + 9)..]),
                    Deathalt = BinaryPrimitives.ReadUInt16LittleEndian(d[(o + 11)..]),
                    Turret = BinaryPrimitives.ReadUInt16LittleEndian(d[(o + 13)..])
                };
                o += SeatSize;
            }
            for (int i = 0; i < x.Stats.Length; i++)
            {
                x.Stats[i] = ReadStats(d[o..]);
                o += StatsSize;
            }
            return x;
        }
            // ---- the detail stats alone: online inside this block, LAN in its own snapshot block (Snapshot.Stats, LAN 14)

        // host: the detail stats of the first seats (up to StatSeats)
        public static SeatStats[] CaptureStats(int seats)
        {
            var stats = new SeatStats[Math.Min(seats, StatSeats)];
            for (int i = 0; i < stats.Length; i++)
            {
                var kills = new byte[9];
                for (int b = 0; b < 9; b++)
                {
                    kills[b] = (byte)Math.Clamp(GameState.BeamKills[i, b], 0, 255);
                }
                stats[i] = new SeatStats
                {
                    KillStreak = (byte)Math.Clamp(GameState.KillStreak[i], 0, 255),
                    Suicides = (byte)Math.Clamp(GameState.Suicides[i], 0, 255),
                    FriendlyKills = (byte)Math.Clamp(GameState.FriendlyKills[i], 0, 255),
                    HeadshotKills = (byte)Math.Clamp(GameState.HeadshotKills[i], 0, 255),
                    BeamDamageDealt = (ushort)Math.Clamp(GameState.BeamDamageDealt[i], 0, 65535),
                    DamageCount = (ushort)Math.Clamp(GameState.DamageCount[i], 0, 65535),
                    AltDamageCount = (ushort)Math.Clamp(GameState.AltDamageCount[i], 0, 65535),
                    BeamKills = kills,
                    BeamDamageMax = (uint)Math.Max(GameState.BeamDamageMax[i], 0)
                };
            }
            return stats;
        }

        // client: the host's detail stats
        public static void ApplyStats(SeatStats[] stats)
        {
            for (int i = 0; i < stats.Length; i++)
            {
                ref SeatStats s = ref stats[i];
                GameState.KillStreak[i] = s.KillStreak;
                GameState.Suicides[i] = s.Suicides;
                GameState.FriendlyKills[i] = s.FriendlyKills;
                GameState.HeadshotKills[i] = s.HeadshotKills;
                GameState.BeamDamageDealt[i] = s.BeamDamageDealt;
                GameState.DamageCount[i] = s.DamageCount;
                GameState.AltDamageCount[i] = s.AltDamageCount;
                GameState.BeamDamageMax[i] = (int)Math.Min(s.BeamDamageMax, Int32.MaxValue);
                for (int b = 0; b < 9; b++)
                {
                    GameState.BeamKills[i, b] = s.BeamKills[b];
                }
            }
        }

        public static void WriteStats(Span<byte> d, in SeatStats s)
        {
            d[0] = s.KillStreak;
            d[1] = s.Suicides;
            d[2] = s.FriendlyKills;
            d[3] = s.HeadshotKills;
            BinaryPrimitives.WriteUInt16LittleEndian(d[4..], s.BeamDamageDealt);
            BinaryPrimitives.WriteUInt16LittleEndian(d[6..], s.DamageCount);
            BinaryPrimitives.WriteUInt16LittleEndian(d[8..], s.AltDamageCount);
            s.BeamKills.AsSpan(0, 9).CopyTo(d[10..]);
            BinaryPrimitives.WriteUInt32LittleEndian(d[19..], s.BeamDamageMax);
        }

        public static SeatStats ReadStats(ReadOnlySpan<byte> d) => new()
        {
            KillStreak = d[0],
            Suicides = d[1],
            FriendlyKills = d[2],
            HeadshotKills = d[3],
            BeamDamageDealt = BinaryPrimitives.ReadUInt16LittleEndian(d[4..]),
            DamageCount = BinaryPrimitives.ReadUInt16LittleEndian(d[6..]),
            AltDamageCount = BinaryPrimitives.ReadUInt16LittleEndian(d[8..]),
            BeamKills = d.Slice(10, 9).ToArray(),
            BeamDamageMax = BinaryPrimitives.ReadUInt32LittleEndian(d[19..])
        };
    }
}
