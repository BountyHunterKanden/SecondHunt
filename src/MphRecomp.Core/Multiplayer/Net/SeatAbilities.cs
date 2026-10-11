using System;
using System.Buffers.Binary;
using MphRead.Entities;

namespace MphRecomp.Multiplayer.Net
{
    // The characters' abilities in a network match (owner 2026-10-09, MatchSettings.Abilities): each seat's Wall Cling
    // (MphRead PlayerWallCling.NetCling) and Scout Drone (PlayerScoutDrone.NetDrone) as the host has them. In every
    // snapshot (the cling starts and ends in a frame), as the last block of Snapshot.Write's bytes: LAN protocol 9 and
    // online 8 (where SnapshotDelta sends it inside the mode block's bytes, which run to the end). Absent when no seat
    // has an ability, so matches without a Trooper send one byte more than before.
    public sealed class SeatAbilities
    {
        public const int SeatSize = 13; // cling 5 + drone 8

        public PlayerNetCling[] Cling = Array.Empty<PlayerNetCling>();
        public PlayerNetDrone[] Drone = Array.Empty<PlayerNetDrone>();

        public int Size => 1 + Cling.Length * SeatSize;

        // host: null when no seat has an ability
        public static SeatAbilities? Capture(int seats)
        {
            bool any = false;
            for (int i = 0; i < seats; i++)
            {
                PlayerEntity p = PlayerEntity.Players[i];
                any |= p.HostWallCling || p.HostScoutDrone;
            }
            if (!any)
            {
                return null;
            }
            var a = new SeatAbilities { Cling = new PlayerNetCling[seats], Drone = new PlayerNetDrone[seats] };
            for (int i = 0; i < seats; i++)
            {
                PlayerEntity p = PlayerEntity.Players[i];
                a.Cling[i] = p.NetCling;
                a.Drone[i] = p.NetDrone;
            }
            return a;
        }

        // client: puppets take the host's state at once, the local seat corrects only a lasting mismatch (MphRead's hooks;
        // each is a no-op on a seat without that ability)
        public void Apply(int local)
        {
            for (int i = 0; i < Cling.Length && i < PlayerEntity.MaxPlayers; i++)
            {
                PlayerEntity p = PlayerEntity.Players[i];
                bool pose = i != local;
                p.NetApplyCling(Cling[i], pose);
                p.NetApplyDrone(Drone[i], pose);
            }
        }

        public int Write(Span<byte> d)
        {
            d[0] = (byte)Cling.Length;
            int o = 1;
            for (int i = 0; i < Cling.Length; i++)
            {
                PlayerNetCling c = Cling[i];
                d[o] = c.Clinging ? (byte)1 : (byte)0;
                d[o + 1] = c.Timer;
                d[o + 2] = c.Chain;
                d[o + 3] = (byte)c.NormalX;
                d[o + 4] = (byte)c.NormalZ;
                PlayerNetDrone r = Drone[i];
                d[o + 5] = r.FireState;
                d[o + 6] = r.FireFrame;
                d[o + 7] = r.Cooldown;
                d[o + 8] = r.LaserFrame;
                d[o + 9] = r.LaserPattern;
                BinaryPrimitives.WriteUInt16LittleEndian(d[(o + 10)..], r.LaserWait);
                d[o + 12] = r.LaserDist;
                o += SeatSize;
            }
            return o;
        }

        public static SeatAbilities Read(ReadOnlySpan<byte> d, out int size)
        {
            int seats = d[0];
            var a = new SeatAbilities { Cling = new PlayerNetCling[seats], Drone = new PlayerNetDrone[seats] };
            int o = 1;
            for (int i = 0; i < seats; i++)
            {
                a.Cling[i] = new PlayerNetCling
                {
                    Clinging = d[o] != 0, Timer = d[o + 1], Chain = d[o + 2], NormalX = (sbyte)d[o + 3], NormalZ = (sbyte)d[o + 4]
                };
                a.Drone[i] = new PlayerNetDrone
                {
                    FireState = d[o + 5], FireFrame = d[o + 6], Cooldown = d[o + 7], LaserFrame = d[o + 8], LaserPattern = d[o + 9],
                    LaserWait = BinaryPrimitives.ReadUInt16LittleEndian(d[(o + 10)..]), LaserDist = d[o + 12]
                };
                o += SeatSize;
            }
            size = o;
            return a;
        }
    }
}
