using System;
using System.Buffers.Binary;
using MphRead.Entities;

namespace MphRecomp.Multiplayer.Net
{
    // Each seat's ping on the live scoreboard (2026-10-19; MphRead PlayerScoreboardPing.cs draws
    // PlayerEntity.HostSeatPingMs). An online match's host measures every joiner's round trip (NetMatchSession.SeatRtts: its
    // snapshot to the joiner's ack) and sends them all to every joiner once a second over the session's side channel, as
    // [TypePings][count][i16 per seat] (-1 = none: a bot; the host's own seat is 0, a served host has none). An older app's
    // side channel reader (ReplayLink) takes only its own two types, so it ignores these: no protocol bump.
    public static class SeatPings
    {
        public const byte TypePings = 3; // (ReplayLink: 1 chunk, 2 ack)
        private const int EveryMs = 1000;
        private static IMatchNet? _listening;
        private static long _sentAt;

        // MatchNetDriver, a match starting: none known yet; a guest listens for its host's
        public static void Start(IMatchNet net)
        {
            PlayerEntity.ResetSeatPings();
            _sentAt = 0;
            if (_listening != null)
            {
                _listening.SideReceived -= OnSide;
                _listening = null;
            }
            if (net is NetMatchSession && !net.IsHost)
            {
                net.SideReceived += OnSide;
                _listening = net;
            }
        }

        // MatchNetDriver, an online host's step: every second, the seats' pings here and to each joiner
        public static void HostTick(NetMatchSession net)
        {
            long now = Environment.TickCount64;
            if (now - _sentAt < EveryMs || net.Settings is not MatchSettings s)
            {
                return;
            }
            _sentAt = now;
            int seats = Math.Min(s.Players.Count, PlayerEntity.HostSeatPingMs.Length);
            int[] rtts = net.SeatRtts(seats);
            if (!s.ServedHost && s.LocalSeat >= 0 && s.LocalSeat < seats)
            {
                rtts[s.LocalSeat] = 0; // (the host plays at no ping)
            }
            byte[] packet = new byte[2 + seats * 2];
            packet[0] = TypePings;
            packet[1] = (byte)seats;
            for (int i = 0; i < seats; i++)
            {
                PlayerEntity.HostSeatPingMs[i] = rtts[i];
                BinaryPrimitives.WriteInt16LittleEndian(packet.AsSpan(2 + i * 2), (short)Math.Clamp(rtts[i], -1, short.MaxValue));
            }
            foreach (int seat in net.RemoteSeats)
            {
                try
                {
                    net.SendSide(seat, packet);
                }
                catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException or System.IO.IOException
                    or System.Net.Sockets.SocketException)
                {
                    return;
                }
            }
        }

        // (the session's receive thread) a guest: its host's pings
        private static void OnSide(int fromSeat, byte[] data)
        {
            if (data.Length < 2 || data[0] != TypePings)
            {
                return;
            }
            int n = Math.Min(Math.Min(data[1], (data.Length - 2) / 2), PlayerEntity.HostSeatPingMs.Length);
            for (int i = 0; i < n; i++)
            {
                PlayerEntity.HostSeatPingMs[i] = Math.Max(-1, (int)BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(2 + i * 2)));
            }
        }
    }
}
