using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;
using MphRead.Entities;
using MphRecomp.Campaign;
using OpenTK.Mathematics;

namespace MphRecomp.Multiplayer.Net
{
    // The shot check, in shadow (2026-10-19; netcode plan part 2: shots as events with their own gun position and view, hit
    // claims, both ideas the other MphRead online forks use, built our own way). It only logs: nothing it learns changes a
    // match yet. Each online guest sends its host, over the side channel, every shot its own player fired (the input it went
    // out with, the frame its screen showed, where it left the gun, its direction, the weapon) and every hit its beams made
    // on its screen (the player hit, where they were, the frame shown). The host pairs each reported shot with the shot it
    // fired for that seat from the same input, and checks each claimed hit against where the victim stood at that frame in
    // its own history (LagCompensation). Its log line per seat says: shots the device fired that never went out on the host
    // ("lost"), how far the two gun positions and aims were apart, and how many claimed hits the host's history agrees with
    // against the hits the host itself counted. That says which of the missed-hit causes it is before any of them is fixed:
    // lost shots (input), shots from elsewhere (the host's copy of the shooter drifted), claims plausible but not hits (the
    // rewind), claims implausible (the victim wasn't where the guest drew them).
    // Wire: [TypeShotCheck][n shots][n claims] then shots (u16 id, u16 input seq, u32 view tick, 3 x f32 origin, 3 x f32 dir,
    // i8 weapon) and claims (u16 id, u8 victim, u32 view tick, 3 x f32 victim position); the last 4 of each in every packet
    // (a lost packet loses nothing), sent when one is new and every 6 steps while any is recent. Older apps send none; an
    // older host's side reader ignores the type: no protocol bump.
    public static class ShotCheck
    {
        public const byte TypeShotCheck = 5; // (ReplayLink 1-2, SeatPings 3, ShotReport 4)
        private const int Repeat = 4, ShotBytes = 33, ClaimBytes = 19, MatchWindow = 90;
        private const float PlausibleReach = 1.5f; // (a claimed victim this close to the host's history at that frame)

        private readonly record struct Shot(ushort Id, ushort Seq, uint View, Vector3 Origin, Vector3 Dir, sbyte Weapon);
        private readonly record struct Claim(ushort Id, byte Victim, uint View, Vector3 Position);

        // ---- guest ----
        private static readonly List<Shot> _shots = new();
        private static readonly List<Claim> _claims = new();
        private static ushort _nextShot, _nextClaim;
        private static long _sentStep = -100, _newStep = -100;

        // ---- host ----
        private sealed class SeatCheck
        {
            public readonly Dictionary<ushort, long> SeenShots = new(), SeenClaims = new();
            public readonly List<(Shot Shot, long At)> Pending = new();
            public readonly List<(ushort Seq, long Frame, Vector3 Origin, Vector3 Dir)> HostShots = new();
            public int Reported, Matched, Lost, Claims, Plausible, Unknown;
            public double OriginSum, AimSum, VictimSum;
            public float OriginMax, AimMax;
        }

        private static readonly SeatCheck?[] _seats = new SeatCheck?[16];

        // each shot and claim as it's settled, for the host's recording (Replay/ReplayFile.cs ShotChecks, format 47): set by
        // whoever records the match; called on the match's own thread (HostStep)
        public static Action<Replay.ReplayShotCheck>? Settled { get; set; }
        private static readonly object _lock = new();
        private static IMatchNet? _listening;

        // MatchNetDriver, a match starting (an online one: on; else off)
        public static void Start(IMatchNet net)
        {
            lock (_lock)
            {
                Array.Clear(_seats);
                _shots.Clear();
                _claims.Clear();
            }
            _sentStep = _newStep = -100;
            if (_listening != null)
            {
                _listening.SideReceived -= OnSide;
                _listening = null;
            }
            PlayerEntity.NetShotLogOn = net is NetMatchSession;
            if (net is NetMatchSession && net.IsHost)
            {
                net.SideReceived += OnSide;
                _listening = net;
            }
        }

        // a guest's step, after it ran: its player's new shots (from the input `seq`, its screen at `view`) and hits seen
        public static void GuestStep(IMatchNet net, int seat, int seq, uint view, long step)
        {
            foreach (PlayerEntity.NetShotOut s in PlayerEntity.NetShotLog)
            {
                if (s.Seat != seat) continue;
                Keep(_shots, new Shot(_nextShot++, (ushort)seq, view, s.Origin, s.Direction, s.Weapon));
                _newStep = step;
            }
            foreach (PlayerEntity.NetHitSeen h in PlayerEntity.NetHitLog)
            {
                Keep(_claims, new Claim(_nextClaim++, (byte)h.Victim, view, h.VictimPosition));
                _newStep = step;
            }
            PlayerEntity.NetShotLog.Clear();
            PlayerEntity.NetHitLog.Clear();
            if (_shots.Count + _claims.Count == 0 || step != _newStep && (step - _sentStep < 6 || step - _newStep > 60)) return;
            _sentStep = step;
            byte[] p = new byte[3 + _shots.Count * ShotBytes + _claims.Count * ClaimBytes];
            p[0] = TypeShotCheck;
            p[1] = (byte)_shots.Count;
            p[2] = (byte)_claims.Count;
            Span<byte> d = p.AsSpan(3);
            foreach (Shot s in _shots)
            {
                BinaryPrimitives.WriteUInt16LittleEndian(d, s.Id);
                BinaryPrimitives.WriteUInt16LittleEndian(d[2..], s.Seq);
                BinaryPrimitives.WriteUInt32LittleEndian(d[4..], s.View);
                WriteV(d[8..], s.Origin);
                WriteV(d[20..], s.Dir);
                d[32] = (byte)s.Weapon;
                d = d[ShotBytes..];
            }
            foreach (Claim c in _claims)
            {
                BinaryPrimitives.WriteUInt16LittleEndian(d, c.Id);
                d[2] = c.Victim;
                BinaryPrimitives.WriteUInt32LittleEndian(d[3..], c.View);
                WriteV(d[7..], c.Position);
                d = d[ClaimBytes..];
            }
            try
            {
                net.SendSide(0, p);
            }
            catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException or System.IO.IOException
                or System.Net.Sockets.SocketException)
            {
            }
        }

        private static void Keep<T>(List<T> list, T item)
        {
            list.Add(item);
            if (list.Count > Repeat) list.RemoveAt(0);
        }

        // (the session's receive thread) the host: a guest's report
        private static void OnSide(int fromSeat, byte[] data)
        {
            if (data.Length < 3 || data[0] != TypeShotCheck || fromSeat < 0 || fromSeat >= _seats.Length) return;
            int shots = data[1], claims = data[2];
            if (data.Length < 3 + shots * ShotBytes + claims * ClaimBytes) return;
            ReadOnlySpan<byte> d = data.AsSpan(3);
            lock (_lock)
            {
                SeatCheck seat = _seats[fromSeat] ??= new SeatCheck();
                for (int i = 0; i < shots; i++, d = d[ShotBytes..])
                {
                    var s = new Shot(BinaryPrimitives.ReadUInt16LittleEndian(d), BinaryPrimitives.ReadUInt16LittleEndian(d[2..]),
                        BinaryPrimitives.ReadUInt32LittleEndian(d[4..]), ReadV(d[8..]), ReadV(d[20..]), (sbyte)d[32]);
                    if (!seat.SeenShots.TryAdd(s.Id, 0)) continue;
                    seat.Reported++;
                    seat.Pending.Add((s, -1));
                }
                for (int i = 0; i < claims; i++, d = d[ClaimBytes..])
                {
                    var c = new Claim(BinaryPrimitives.ReadUInt16LittleEndian(d), d[2], BinaryPrimitives.ReadUInt32LittleEndian(d[3..]),
                        ReadV(d[7..]));
                    if (!seat.SeenClaims.TryAdd(c.Id, 0)) continue;
                    seat.Claims++;
                    _claimQueue.Add((fromSeat, c));
                }
                if (seat.SeenShots.Count > 4096) seat.SeenShots.Clear(); // (ids wrap; the last few are what repeats)
                if (seat.SeenClaims.Count > 4096) seat.SeenClaims.Clear();
            }
        }

        private static readonly List<(int Seat, Claim Claim)> _claimQueue = new();

        // the host's step, after it ran: its remote seats' shots this step (with the input each seat had), then the reports
        public static void HostStep(CampaignHost host)
        {
            if (!PlayerEntity.NetShotLogOn) return;
            long frame = host.Frame;
            lock (_lock)
            {
                foreach (PlayerEntity.NetShotOut s in PlayerEntity.NetShotLog)
                {
                    if (s.Seat < 0 || s.Seat >= _seats.Length) continue;
                    SeatCheck seat = _seats[s.Seat] ??= new SeatCheck();
                    seat.HostShots.Add((host.RemoteSeq[s.Seat], frame, s.Origin, s.Direction));
                    if (seat.HostShots.Count > 64) seat.HostShots.RemoveAt(0);
                }
                PlayerEntity.NetShotLog.Clear();
                PlayerEntity.NetHitLog.Clear();
                foreach (SeatCheck? seat in _seats)
                {
                    if (seat == null) continue;
                    for (int k = seat.Pending.Count - 1; k >= 0; k--)
                    {
                        (Shot shot, long at) = seat.Pending[k];
                        if (at < 0) seat.Pending[k] = (shot, at = frame);
                        int found = seat.HostShots.FindIndex(h => (ushort)(h.Seq - shot.Seq) <= 2 || (ushort)(shot.Seq - h.Seq) <= 2);
                        if (found >= 0)
                        {
                            var h = seat.HostShots[found];
                            seat.HostShots.RemoveAt(found);
                            float origin = (h.Origin - shot.Origin).Length;
                            float aim = AngleDeg(h.Dir, shot.Dir);
                            seat.Matched++;
                            seat.OriginSum += origin;
                            seat.AimSum += aim;
                            seat.OriginMax = MathF.Max(seat.OriginMax, origin);
                            seat.AimMax = MathF.Max(seat.AimMax, aim);
                            seat.Pending.RemoveAt(k);
                            Settle(frame, Array.IndexOf(_seats, seat), Replay.ReplayShotKind.ShotMatched, origin, aim, -1, 0);
                        }
                        else if (frame - at > MatchWindow)
                        {
                            seat.Lost++;
                            seat.Pending.RemoveAt(k);
                            Settle(frame, Array.IndexOf(_seats, seat), Replay.ReplayShotKind.ShotLost, 0, 0, -1, 0);
                        }
                    }
                }
                foreach ((int shooter, Claim c) in _claimQueue)
                {
                    SeatCheck seat = _seats[shooter]!;
                    if (host.Lag == null || !host.Lag.TryPositionAt(c.Victim, c.View, out Vector3 was))
                    {
                        seat.Unknown++; // (too old, or the victim wasn't up then)
                        Settle(frame, shooter, Replay.ReplayShotKind.ClaimUnknown, 0, 0, (sbyte)c.Victim, 0);
                        continue;
                    }
                    float off = (was - c.Position).Length;
                    seat.VictimSum += off;
                    if (off <= PlausibleReach) seat.Plausible++;
                    Settle(frame, shooter, off <= PlausibleReach ? Replay.ReplayShotKind.ClaimAgrees : Replay.ReplayShotKind.ClaimDisagrees,
                        0, 0, (sbyte)c.Victim, off);
                }
                _claimQueue.Clear();
            }
        }

        // the host's log: per seat that reported, "seat 1: device shots 120, matched 118 (origin off avg 0.12 max 0.9, aim off
        // avg 0.4 max 3.1 deg), lost 2; hits claimed 40, the host's history agrees 38 (victim off avg 0.3), unknown 1; host hits 12"
        public static string HostLine()
        {
            var line = new StringBuilder();
            lock (_lock)
            {
                for (int i = 0; i < _seats.Length; i++)
                {
                    SeatCheck? s = _seats[i];
                    if (s == null || s.Reported + s.Claims == 0) continue;
                    if (line.Length > 0) line.Append("; ");
                    line.Append($"seat {i}: device shots {s.Reported}, matched {s.Matched} (origin off avg {Avg(s.OriginSum, s.Matched):0.00} max "
                        + $"{s.OriginMax:0.00}, aim off avg {Avg(s.AimSum, s.Matched):0.0} max {s.AimMax:0.0} deg), lost {s.Lost}; hits claimed "
                        + $"{s.Claims}, history agrees {s.Plausible} (victim off avg {Avg(s.VictimSum, s.Claims - s.Unknown):0.00}), unknown "
                        + $"{s.Unknown}; host hits {PlayerEntity.NetBeamHits[i]}");
                }
            }
            return line.Length > 0 ? line.ToString() : "no reports";
        }

        private static void Settle(long frame, int seat, Replay.ReplayShotKind kind, float origin, float aim, sbyte victim, float off)
        {
            try
            {
                Settled?.Invoke(new Replay.ReplayShotCheck((int)frame, (byte)seat, kind, origin, aim, victim, off));
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("shot check: not recorded: " + ex.Message); // (never stops the match)
            }
        }

        private static double Avg(double sum, int n) => n > 0 ? sum / n : 0;

        private static float AngleDeg(Vector3 a, Vector3 b)
        {
            float la = a.Length, lb = b.Length;
            if (la < 1e-6f || lb < 1e-6f) return 0;
            return MathHelper.RadiansToDegrees(MathF.Acos(Math.Clamp(Vector3.Dot(a, b) / (la * lb), -1, 1)));
        }

        private static void WriteV(Span<byte> d, Vector3 v)
        {
            BinaryPrimitives.WriteSingleLittleEndian(d, v.X);
            BinaryPrimitives.WriteSingleLittleEndian(d[4..], v.Y);
            BinaryPrimitives.WriteSingleLittleEndian(d[8..], v.Z);
        }

        private static Vector3 ReadV(ReadOnlySpan<byte> d) => new(BinaryPrimitives.ReadSingleLittleEndian(d),
            BinaryPrimitives.ReadSingleLittleEndian(d[4..]), BinaryPrimitives.ReadSingleLittleEndian(d[8..]));
    }
}
