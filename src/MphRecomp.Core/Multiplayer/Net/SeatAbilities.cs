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
        public const int SeatSize = 17; // cling 5 + drone 8 + Dark Samus's Hyper Ball 2 (LAN 18 / online 16)
                                        // + Zero Suit's Paralyzer stun on this seat 1 (LAN 19 / online 17)
                                        // + the Squad Mark on this seat 1 (LAN 20 / online 18)

        public PlayerNetCling[] Cling = Array.Empty<PlayerNetCling>();
        public PlayerNetDrone[] Drone = Array.Empty<PlayerNetDrone>();
        // Dark Samus's Hyper Ball: on, and its two targets' seats (PlayerEntity.NetHyperBall / NetHyperTargets)
        public byte[] Hyper = Array.Empty<byte>();
        public byte[] HyperTargets = Array.Empty<byte>();
        // the Paralyzer stun frames left on each seat (PlayerEntity.HostParalyzedTimer; any seat can be stunned by Zero Suit)
        public byte[] Paralyzed = Array.Empty<byte>();
        // the seats whose Squad Mark is on each seat (PlayerEntity.NetSquadMark, a bit a seat; the Trooper's blaster)
        public byte[] SquadMark = Array.Empty<byte>();

        // (a block made without them, as a test's: none)
        private static byte HyperAt(byte[] a, int i) => i < a.Length ? a[i] : (byte)0;

        public int Size => 1 + Cling.Length * SeatSize;

        // host: null when no seat has an ability
        public static SeatAbilities? Capture(int seats)
        {
            bool any = false;
            for (int i = 0; i < seats; i++)
            {
                PlayerEntity p = PlayerEntity.Players[i];
                any |= p.HostWallCling || p.HostScoutDrone || p.HostHyperBall || p.HostAffinityBeam == MphRead.BeamType.Paralyzer
                    || p.HostAffinityBeam == MphRead.BeamType.PirateBlaster;
            }
            if (!any)
            {
                return null;
            }
            var a = new SeatAbilities { Cling = new PlayerNetCling[seats], Drone = new PlayerNetDrone[seats], Hyper = new byte[seats],
                HyperTargets = new byte[seats], Paralyzed = new byte[seats], SquadMark = new byte[seats] };
            for (int i = 0; i < seats; i++)
            {
                PlayerEntity p = PlayerEntity.Players[i];
                a.Paralyzed[i] = (byte)Math.Min(p.HostParalyzedTimer, 255);
                a.SquadMark[i] = p.NetSquadMark;
                a.Cling[i] = p.NetCling;
                a.Drone[i] = p.NetDrone;
                a.Hyper[i] = p.NetHyperBall;
                a.HyperTargets[i] = p.NetHyperTargets;
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
                p.NetApplyHyperBall(HyperAt(Hyper, i), HyperAt(HyperTargets, i), pose);
                p.NetApplyParalyzed(HyperAt(Paralyzed, i), pose);
                p.NetApplySquadMark(HyperAt(SquadMark, i));
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
                d[o + 13] = HyperAt(Hyper, i);
                d[o + 14] = HyperAt(HyperTargets, i);
                d[o + 15] = HyperAt(Paralyzed, i);
                d[o + 16] = HyperAt(SquadMark, i);
                o += SeatSize;
            }
            return o;
        }

        public static SeatAbilities Read(ReadOnlySpan<byte> d, out int size)
        {
            int seats = d[0];
            var a = new SeatAbilities { Cling = new PlayerNetCling[seats], Drone = new PlayerNetDrone[seats], Hyper = new byte[seats],
                HyperTargets = new byte[seats], Paralyzed = new byte[seats], SquadMark = new byte[seats] };
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
                a.Hyper[i] = d[o + 13];
                a.HyperTargets[i] = d[o + 14];
                a.Paralyzed[i] = d[o + 15];
                a.SquadMark[i] = d[o + 16];
                o += SeatSize;
            }
            size = o;
            return a;
        }
    }
}
