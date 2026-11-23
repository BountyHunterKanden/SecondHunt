using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;
using MphRead;
using MphRead.Entities;
using MphRecomp.Campaign;
using OpenTK.Mathematics;

namespace MphRecomp.Multiplayer.Net
{
    // Pickup claims (docs/FRUITY_PORT.md P3, 2026-11-13: Fruity Prime's pickup claims, ported for a served match). On a served
    // match every device moves its own player (FruitySync) and the host takes the newest position each device reports, so the
    // host's copy of a player can jump past an item the player ran through on their own screen: the pickup sound never came, or
    // the item stayed. Fruity Prime has the device say what its player touched and the server judge it; so does this:
    //   - the device (PickUpItems on a served match, PlayerEntity.NetPickupClaims) notes each item its own player touched and
    //     would take, and claims it by the host's id for it with the host frame its screen showed then (its view tick) and where
    //     its player stood;
    //   - the host judges it as Fruity Prime's server does: not too old (MaxContactAge), the player still in play, the item
    //     still there (else counted when this seat is the one that took it), the claimed spot within reach of the item, and the
    //     host's own history of that player (LagCompensation, the positions the device reported) passing within
    //     CorroborationRadius of that spot between the frame it names and when that report could have arrived (the seat's
    //     round trip + ArrivalSlack). With no history at all for that window, the player where they stand now must be near;
    //   - a claim that passes goes to the host's next step (CampaignHost.ClaimPickup): that seat's PickUpItems takes the item
    //     as if in reach, by the pickup's own rules, and every device learns who took it from the snapshot (ItemSync's Taken).
    // Ours, not Fruity's (until the P2 shot events): every item type is claimed and the host stays the judge of inventories
    // (Fruity leaves weapons and ammo to each device). The host's own copy still picks up by itself: a claim only adds what it
    // missed. Wire, guest -> host, side channel type 8 (an older host ignores it): [8][n] then n claims (u16 claim id, u16 the
    // host's item id, u32 view tick, 3 x f32 where the player stood); the pending ones in every packet, sent when one is new
    // and every 6 steps until the item is gone from the device's screen or Lapse steps pass. The host says it honours them
    // with FruitySync.PickupsMark ('P') in its match settings (a device predicts the pickup only then: PlayerPickupPredict).
    public static class PickupClaims
    {
        public const byte TypePickupClaim = 8; // (ReplayLink 1-2, SeatPings 3, ShotReport 4, ShotCheck 5, HitClaims 6-7)
        private const int ClaimBytes = 20, PacketMax = 12, ResendSteps = 6;
        // frames: Fruity Prime's limits (PickupClaimJudge): the oldest contact taken, and the slack past the round trip for the
        // device's report of that spot to reach the host
        private const int MaxContactAge = 120, ArrivalSlack = 20;
        private const int Lapse = MaxContactAge; // steps a device keeps claiming one item
        private const float CorroborationRadius = 2.0f;
        // the reach a claimed spot must be within (Fruity Prime's WithinPickupReach: 2 across, the item 2 below to 2.5 above)
        private const float ReachLateral = 2.0f, ReachBelow = -2.0f, ReachAbove = 2.5f;

        private readonly record struct Claim(ushort Id, ushort Item, uint View, Vector3 Position);

        // ---- guest ----
        private static readonly List<(Claim Claim, long Born)> _claims = new();
        private static ushort _nextId;
        private static long _sentStep = -100, _newStep = -100;
        private static int _made, _settled, _lapsed;
        public static string GuestLine() => $"pickup claims made {_made}, settled {_settled}, lapsed {_lapsed} (waiting {_claims.Count})";

        // ---- host ----
        private enum Verdict { Wait, Apply, Agreed, Taken, Old, Life, Reach, Far, Bad }

        private sealed class Pending
        {
            public int Seat;
            public Claim Claim;
            public long Arrived = -1;
        }

        private sealed class SeatClaims
        {
            public readonly Dictionary<ushort, byte> Seen = new();
            public readonly int[] Verdicts = new int[(int)Verdict.Bad + 1];
            public int Claims;
        }

        private static readonly List<Pending> _incoming = new(); // (the receive thread -> HostStep, under _lock)
        private static readonly List<Pending> _pending = new();
        private static readonly SeatClaims?[] _seats = new SeatClaims?[16];
        private static readonly object _lock = new();
        private static IMatchNet? _listening;

        // MatchNetDriver, a match starting (after HitClaims.Start)
        public static void Start(IMatchNet net)
        {
            lock (_lock)
            {
                Array.Clear(_seats);
                _incoming.Clear();
            }
            _pending.Clear();
            _claims.Clear();
            _sentStep = _newStep = -100;
            _made = _settled = _lapsed = 0;
            PlayerEntity.NetPickupTouches.Clear();
            PlayerEntity.HostClaimedPickups.Clear();
            if (_listening != null)
            {
                _listening.SideReceived -= OnSide;
                _listening = null;
            }
            if (net is NetMatchSession && net.IsHost && FruitySync.On)
            {
                net.SideReceived += OnSide;
                _listening = net;
            }
        }

        // a served match's device, after its step: what its own player touched this step, claimed; the ones settled (gone from
        // its screen: taken here once the host confirmed, or by someone else) or past Lapse dropped
        public static void GuestStep(IMatchNet net, CampaignHost host, uint view, long step)
        {
            List<PlayerEntity.NetPickupTouch> touches = PlayerEntity.NetPickupTouches;
            if (view != 0)
            {
                foreach (PlayerEntity.NetPickupTouch t in touches)
                {
                    ushort item = host.NetLocalItemId(t.Item);
                    if (item == 0 || _claims.Exists(x => x.Claim.Item == item)) continue;
                    _claims.Add((new Claim(_nextId++, item, view, t.Position), step));
                    _made++;
                    _newStep = step;
                }
            }
            touches.Clear();
            for (int i = _claims.Count - 1; i >= 0; i--)
            {
                (Claim c, long born) = _claims[i];
                if (host.NetLocalItem(c.Item) is not { DespawnTimer: not 0 })
                {
                    _claims.RemoveAt(i);
                    _settled++;
                }
                else if (step - born > Lapse)
                {
                    _claims.RemoveAt(i);
                    _lapsed++;
                }
            }
            while (_claims.Count > PacketMax)
            {
                _claims.RemoveAt(0);
                _lapsed++;
            }
            if (_claims.Count == 0 || step != _newStep && step - _sentStep < ResendSteps) return;
            _sentStep = step;
            byte[] p = new byte[2 + _claims.Count * ClaimBytes];
            p[0] = TypePickupClaim;
            p[1] = (byte)_claims.Count;
            Span<byte> d = p.AsSpan(2);
            foreach ((Claim c, long _) in _claims)
            {
                BinaryPrimitives.WriteUInt16LittleEndian(d, c.Id);
                BinaryPrimitives.WriteUInt16LittleEndian(d[2..], c.Item);
                BinaryPrimitives.WriteUInt32LittleEndian(d[4..], c.View);
                BinaryPrimitives.WriteSingleLittleEndian(d[8..], c.Position.X);
                BinaryPrimitives.WriteSingleLittleEndian(d[12..], c.Position.Y);
                BinaryPrimitives.WriteSingleLittleEndian(d[16..], c.Position.Z);
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

        // (the session's receive thread) the host: a device's claims
        private static void OnSide(int fromSeat, byte[] data)
        {
            if (data.Length < 2 || data[0] != TypePickupClaim || fromSeat < 0 || fromSeat >= _seats.Length) return;
            int n = data[1];
            if (data.Length < 2 + n * ClaimBytes) return;
            ReadOnlySpan<byte> d = data.AsSpan(2);
            lock (_lock)
            {
                SeatClaims seat = _seats[fromSeat] ??= new SeatClaims();
                for (int i = 0; i < n; i++, d = d[ClaimBytes..])
                {
                    ushort id = BinaryPrimitives.ReadUInt16LittleEndian(d);
                    if (!seat.Seen.TryAdd(id, 0)) continue; // (a repeat: already judged or waiting)
                    var c = new Claim(id, BinaryPrimitives.ReadUInt16LittleEndian(d[2..]), BinaryPrimitives.ReadUInt32LittleEndian(d[4..]),
                        new Vector3(BinaryPrimitives.ReadSingleLittleEndian(d[8..]), BinaryPrimitives.ReadSingleLittleEndian(d[12..]),
                            BinaryPrimitives.ReadSingleLittleEndian(d[16..])));
                    seat.Claims++;
                    _incoming.Add(new Pending { Seat = fromSeat, Claim = c });
                }
                if (seat.Seen.Count > 4096) seat.Seen.Clear(); // (ids wrap; the last few are what repeats)
            }
        }

        // the served host's step, after it ran: every claim judged; the ones that pass go to the next step
        public static void HostStep(CampaignHost host)
        {
            if (_listening == null) return;
            long frame = host.Frame;
            lock (_lock)
            {
                foreach (Pending p in _incoming)
                {
                    p.Arrived = frame;
                    _pending.Add(p);
                }
                _incoming.Clear();
                for (int k = 0; k < _pending.Count; k++)
                {
                    Pending p = _pending[k];
                    Verdict v = Judge(host, p, frame, out ItemInstanceEntity? item);
                    if (v == Verdict.Wait) continue;
                    _pending.RemoveAt(k--);
                    if (v == Verdict.Apply && !host.ClaimPickup(p.Seat, item!)) v = Verdict.Taken; // (another seat's claim, this step)
                    _seats[p.Seat]!.Verdicts[(int)v]++;
                }
            }
        }

        private static Verdict Judge(CampaignHost host, Pending p, long frame, out ItemInstanceEntity? item)
        {
            item = null;
            Claim c = p.Claim;
            if (p.Seat >= PlayerEntity.MaxPlayers || !float.IsFinite(c.Position.X) || !float.IsFinite(c.Position.Y)
                || !float.IsFinite(c.Position.Z))
            {
                return Verdict.Bad;
            }
            if (c.View == 0 || c.View > frame || frame - c.View > MaxContactAge) return Verdict.Old;
            PlayerEntity player = PlayerEntity.Players[p.Seat];
            if (player.Health == 0 || !player.LoadFlags.TestFlag(LoadFlags.Spawned)) return Verdict.Life;
            item = host.NetHostItem(c.Item);
            if (item == null || item.DespawnTimer == 0)
            {
                return host.NetHostTakenBy(c.Item) == p.Seat ? Verdict.Agreed : Verdict.Taken;
            }
            Vector3 between = item.Position - c.Position;
            if (between.X * between.X + between.Z * between.Z > ReachLateral * ReachLateral || between.Y < ReachBelow
                || between.Y > ReachAbove)
            {
                return Verdict.Reach;
            }
            // the host's history of this player near the claimed spot, from the frame its screen showed to when its report of
            // being there could have come in (a step between two reports counts by the line between them: the newest report
            // wins on the host, so a run through the item may land either side of it)
            int ping = p.Seat < PlayerEntity.HostSeatPingMs.Length ? PlayerEntity.HostSeatPingMs[p.Seat] : -1;
            int rtt = ping > 0 ? (ping * 60 + 999) / 1000 : 0;
            long due = Math.Min(c.View + rtt + ArrivalSlack, c.View + MaxContactAge);
            long end = Math.Min(due, frame);
            const float r2 = CorroborationRadius * CorroborationRadius;
            bool any = false, havePrev = false;
            Vector3 prev = default;
            if (host.Lag is LagCompensation lag)
            {
                for (long f = c.View; f <= end; f++)
                {
                    if (!lag.TryPoseAt(p.Seat, f, out Vector3 was, out int epoch) || epoch != player.NetAimEpoch)
                    {
                        havePrev = false;
                        continue;
                    }
                    any = true;
                    if ((havePrev ? SegmentDistanceSquared(prev, was, c.Position) : (was - c.Position).LengthSquared) <= r2)
                    {
                        return Verdict.Apply;
                    }
                    prev = was;
                    havePrev = true;
                }
            }
            if (frame < due) return Verdict.Wait;
            if (!any && (player.Position - c.Position).LengthSquared <= 4 * r2) return Verdict.Apply;
            return Verdict.Far;
        }

        private static float SegmentDistanceSquared(Vector3 a, Vector3 b, Vector3 point)
        {
            Vector3 ab = b - a;
            float len = ab.LengthSquared;
            float t = len > 1e-8f ? Math.Clamp(Vector3.Dot(point - a, ab) / len, 0, 1) : 0;
            return (a + ab * t - point).LengthSquared;
        }

        // the host's log: per seat that claimed, "seat 1: pickup claims 9, applied 2, the host took too 7, refused 0 (gone 0,
        // old 0, out of play 0, out of reach 0, not near 0, bad 0)"
        public static string HostLine()
        {
            var line = new StringBuilder();
            lock (_lock)
            {
                for (int i = 0; i < _seats.Length; i++)
                {
                    SeatClaims? s = _seats[i];
                    if (s == null || s.Claims == 0) continue;
                    int[] v = s.Verdicts;
                    int refused = v[(int)Verdict.Taken] + v[(int)Verdict.Old] + v[(int)Verdict.Life] + v[(int)Verdict.Reach]
                        + v[(int)Verdict.Far] + v[(int)Verdict.Bad];
                    if (line.Length > 0) line.Append("; ");
                    line.Append($"seat {i}: pickup claims {s.Claims}, applied {v[(int)Verdict.Apply]}, the host took too "
                        + $"{v[(int)Verdict.Agreed]}, refused {refused} (gone {v[(int)Verdict.Taken]}, old {v[(int)Verdict.Old]}, "
                        + $"out of play {v[(int)Verdict.Life]}, out of reach {v[(int)Verdict.Reach]}, not near {v[(int)Verdict.Far]}, "
                        + $"bad {v[(int)Verdict.Bad]})");
                }
            }
            return line.Length > 0 ? line.ToString() : "no pickup claims";
        }
    }
}
