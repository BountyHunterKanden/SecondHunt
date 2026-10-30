using System;
using System.Buffers.Binary;
using System.Text;
using MphRead.Entities;

namespace MphRecomp.Multiplayer.Net
{
    // The shot report (2026-10-19, live report: point-blank hits not landing for one player a match). Each online guest sends
    // its host, once a second, how many shots its own player fired here and how many of its beams hit another player here
    // (as its screen showed them), over the side channel as [TypeShots][i32 fired][i32 hits]; the host keeps them by seat and
    // its log puts them beside its own count for that seat (shots fired, presses refused and why, beam hits:
    // PlayerEntity.NetShotsFired / NetShotsRefused / NetBeamHits). Few shots on the host = the shots never went out there;
    // the same shots but few hits = they went out and missed (the rewind, the shot's origin). An older app sends none and an
    // older host's side reader ignores the type, so no protocol bump.
    public static class ShotReport
    {
        public const byte TypeShots = 4; // (ReplayLink 1-2, SeatPings 3)
        private const int EveryMs = 1000;
        private static readonly int[] _seenFired = new int[16], _seenHits = new int[16];
        private static IMatchNet? _listening;
        private static long _sentAt;

        // MatchNetDriver, a match starting: every count from zero; the host listens for its guests' reports
        public static void Start(IMatchNet net)
        {
            PlayerEntity.ResetNetShots();
            Array.Fill(_seenFired, -1);
            Array.Fill(_seenHits, -1);
            _sentAt = 0;
            if (_listening != null)
            {
                _listening.SideReceived -= OnSide;
                _listening = null;
            }
            if (net is NetMatchSession && net.IsHost)
            {
                net.SideReceived += OnSide;
                _listening = net;
            }
        }

        // MatchNetDriver, an online guest's step: its own player's counts to the host every second
        public static void GuestTick(IMatchNet net, int seat)
        {
            long now = Environment.TickCount64;
            if (now - _sentAt < EveryMs || seat < 0 || seat >= PlayerEntity.NetShotsFired.Length)
            {
                return;
            }
            _sentAt = now;
            byte[] packet = new byte[9];
            packet[0] = TypeShots;
            BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(1), PlayerEntity.NetShotsFired[seat]);
            BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(5), PlayerEntity.NetBeamHits[seat]);
            try
            {
                net.SendSide(0, packet);
            }
            catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException or System.IO.IOException
                or System.Net.Sockets.SocketException)
            {
            }
        }

        // (the session's receive thread) the host: a guest's counts, by the seat it came from
        private static void OnSide(int fromSeat, byte[] data)
        {
            if (data.Length < 9 || data[0] != TypeShots || fromSeat < 0 || fromSeat >= _seenFired.Length)
            {
                return;
            }
            _seenFired[fromSeat] = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(1));
            _seenHits[fromSeat] = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(5));
        }

        // the host's log: "seat 1: fired 120 (its device 131), refused cooldown 3 gun 0 none 0, beam hits 12 (its device 40)"
        public static string HostLine(int seats)
        {
            var line = new StringBuilder();
            for (int i = 0; i < seats && i < PlayerEntity.NetShotsFired.Length; i++)
            {
                if (!PlayerEntity.Players[i].HostRemote)
                {
                    continue;
                }
                if (line.Length > 0) line.Append("; ");
                line.Append($"seat {i}: fired {PlayerEntity.NetShotsFired[i]} (its device {Seen(_seenFired[i])}), refused cooldown "
                    + $"{PlayerEntity.NetShotsRefused[i, PlayerEntity.NetShotCooldown]} gun {PlayerEntity.NetShotsRefused[i, PlayerEntity.NetShotGunLowered]} "
                    + $"none {PlayerEntity.NetShotsRefused[i, PlayerEntity.NetShotNoSpawn]}, beam hits {PlayerEntity.NetBeamHits[i]} "
                    + $"(its device {Seen(_seenHits[i])})");
            }
            return line.Length > 0 ? line.ToString() : "no remote seats";
        }

        private static string Seen(int value) => value < 0 ? "?" : value.ToString();
    }
}
