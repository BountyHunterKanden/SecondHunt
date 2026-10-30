using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;
using MphRead;
using MphRead.Entities;
using MphRead.Formats;
using MphRecomp.Campaign;
using OpenTK.Mathematics;

namespace MphRecomp.Multiplayer.Net
{
    // Hit claims (2026-10-19: the same idea as Fruity Prime's, built our own way, for accuracy
    // online). Fruity Prime lets the shooter's device decide its hits and the
    // server only refuses the implausible ones. Ours keeps the host the judge: its own copy of every shot still hits as before
    // (lag compensated), and a guest's claim only adds a hit the host's copy of that very shot missed, once the host checked
    // it against its own history. What a claim must pass (MatchSettings.HitClaims, PlayerEntity.HostHitClaims):
    //   - it names a real shot: one the host fired for that seat from the same input (the claim can't make a shot up), a
    //     straight one (no homing, continuous or arcing shot: those stay the host copy's call), one beam per shot;
    //   - the host's copy of that shot didn't hit that player (else it's already counted: nothing to add);
    //   - the frame the guest's screen showed is no older than the rewind cap + ExtraAge (0.6 s: the most a player can be hit
    //     after reaching cover, as with lag compensation; Fruity Prime allows ~2 s), and the player hadn't respawned since;
    //   - the player was where the guest drew them, at that frame, in the host's history (within VictimReach);
    //   - the frame it names is one the guest could have seen while that shot flew: from the view its input went out with
    //     to that plus the beam's lifespan (within ExtraAge), not any older frame in the window (2026-10-19 review);
    //   - the hit point is on their body there, on the shot's line from where the host's copy left the gun no further than the
    //     beam can get (its speed over its lifespan), with nothing solid in between (TestFlags.Beams, as the beam itself tests);
    //   - the host's own beams of that shot have landed or gone (they may still hit by themselves: waits up to FlightWait).
    // Then the host applies it at the start of its next step (CampaignHost.ClaimHit: the shot's stand-in hits with the shot's
    // own damage, falloff, headshot and effects), recorded in its replay. Kills stay the host's: a claim only deals the hit.
    // Wire, guest -> host, side channel type 6 (old hosts ignore it: no protocol bump): [6][n] then n claims (u16 id, u16
    // input seq the shot went out with, u8 player hit, u8 flags (1 = headshot on its screen), u32 view tick, 3 x f32 where
    // the player was, 3 x f32 hit point); the last 4 in every packet, sent when one is new and every 6 steps while recent.
    // Verdicts, host -> guest, side channel type 7 (instant hit feedback, as Fruity Prime answers each claim): [7][n] then n x
    // (u16 claim id, u8 1 = counted / 0 = refused), the last 8 in every packet, sent when one is new and every 6 steps for a
    // second. A refusal takes that hit back off the guest's screen at once (PlayerEntity.NetRollbackPredicted); one is sent
    // only when it's final (the host's own copy of the shot has landed or never existed: a shot still flying may yet hit by
    // itself, and then the snapshot confirms it). An older guest ignores type 7; an older host never sends it (the guest's
    // prediction then times out).
    public static class HitClaims
    {
        public const byte TypeHitClaim = 6; // (ReplayLink 1-2, SeatPings 3, ShotReport 4, ShotCheck 5)
        public const byte TypeVerdicts = 7;
        private const int Repeat = 4, ClaimBytes = 34;
        // frames a claim waits for its shot's input to reach the host: as long as the rewind window, so a shot whose input
        // stalled on a phone's mobile data still counts if it lands in time (was 30; 2026-10-20 test on data refused 5 hits)
        private static int ShotWait => LagCompensation.MaxRewind + ExtraAge;
        private const int FlightWait = 45; // frames it waits for the host's own beams of that shot to land or miss
        private const int ExtraAge = 6; // frames past the rewind cap a claim's view may be
        private const float VictimReach = 1.0f, BodyRadius = 1.2f, BodyBelow = 0.5f, BodyAbove = 2.5f, LineReach = 1.0f;

        private readonly record struct Claim(ushort Id, ushort Seq, byte Victim, bool Head, uint View, Vector3 VictimPosition, Vector3 Point,
            int Predicted = 0);

        // ---- guest ----
        private static readonly List<Claim> _claims = new();
        private static readonly Dictionary<int, ushort> _shotSeq = new(); // its own shots: number -> input seq
        private static readonly Queue<int> _shotOrder = new();
        private static ushort _nextId;
        // P2 (FRUITY_PORT.md): claims kept until the host's verdict comes back; OutboxOff = the old repeat-4 path (A/B in the tools)
        public static bool OutboxOff;
        private static bool Outbox => FruitySync.On && !OutboxOff;
        private const int FoldSpan = 12; // inputs a folded press may sit past its own seq
        private const int OutboxMax = 12, OutboxLife = 240, LedgerSize = 64;
        private static int VerdictList => FruitySync.On && !OutboxOff ? 16 : 8;
        private static readonly Dictionary<ushort, long> _born = new();
        private static int _made, _answered, _unanswered;
        // (a verdict may be missing on purpose: the host holds none back while its own beam is still flying, then the shot
        // simply hit or missed; "unheard" is what the loss test gates)
        public static int Unheard => _made - _answered;
        public static string GuestLine() => $"claims made {_made}, host verdict heard {_answered}, unheard {Unheard} (given up {_unanswered}, waiting {_claims.Count})";
        private static long _sentStep = -100, _newStep = -100;
        private static readonly Dictionary<ushort, (byte Victim, int Predicted)> _claimPredictions = new(); // (the last 64)
        private static readonly Queue<ushort> _claimOrder = new();
        private static readonly System.Collections.Concurrent.ConcurrentQueue<(ushort Id, bool Counted)> _verdictsIn = new();
        // host: the verdicts to send each seat (the last 8), and when the newest came
        private static readonly List<(ushort Id, bool Counted)>?[] _verdictsOut = new List<(ushort, bool)>?[16];
        private static readonly long[] _verdictNew = new long[16], _verdictSent = new long[16];

        // ---- host ----
        private enum Verdict { Wait, Agreed, Apply, Old, Far, Body, Line, Wall, NoShot, Kind, Gone, Time, Reach, Other }

        private sealed class Pending
        {
            public int Seat;
            public Claim Claim;
            public long Arrived = -1;
        }

        private sealed class SeatClaims
        {
            public readonly Dictionary<ushort, byte> Seen = new();
            public readonly Dictionary<ushort, bool> Ledger = new(); // (P2) settled verdicts sent: id -> counted (the last LedgerSize)
            public readonly Queue<ushort> LedgerOrder = new();
            public readonly List<ushort> Repeats = new(); // (P2) claims the guest sent again
            public int Answered; // (P2) repeats answered from the ledger
            public readonly List<(ushort Seq, int Number, uint View, long Frame)> Shots = new(); // (View: its input's view tick, 0 = none)
            public readonly int[] Verdicts = new int[(int)Verdict.Other + 1];
            public int Claims;
        }

        private static readonly List<Pending> _incoming = new(); // (the receive thread -> HostStep, under _lock)
        private static readonly List<Pending> _pending = new();
        private static readonly SeatClaims?[] _seats = new SeatClaims?[16];
        private static readonly object _lock = new();
        private static IMatchNet? _listening;

        // MatchNetDriver, a match starting (after ShotCheck.Start: online only, as the shot log)
        public static void Start(IMatchNet net)
        {
            lock (_lock)
            {
                Array.Clear(_seats);
                _incoming.Clear();
            }
            _pending.Clear();
            _claims.Clear();
            _shotSeq.Clear();
            _shotOrder.Clear();
            _claimPredictions.Clear();
            _claimOrder.Clear();
            _verdictsIn.Clear();
            Array.Clear(_verdictsOut);
            PlayerEntity.NetHitClaimLog.Clear();
            _sentStep = _newStep = -100;
            _born.Clear();
            _made = _answered = _unanswered = 0;
            if (_listening != null)
            {
                _listening.SideReceived -= OnSide;
                _listening.SideReceived -= OnVerdicts;
                _listening = null;
            }
            if (net is NetMatchSession)
            {
                net.SideReceived += net.IsHost ? OnSide : OnVerdicts;
                _listening = net;
            }
        }

        // (the session's receive thread) a guest: its host's verdicts on its claims, applied on the match's thread (GuestStep)
        private static void OnVerdicts(int fromSeat, byte[] data)
        {
            if (data.Length < 2 || data[0] != TypeVerdicts) return;
            int n = Math.Min(data[1], (data.Length - 2) / 3);
            for (int i = 0; i < n; i++)
            {
                _verdictsIn.Enqueue((BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(2 + i * 3)), data[4 + i * 3] != 0));
            }
        }

        // a guest's step, after it ran (before ShotCheck.GuestStep drains the shot log): its own shots' input seqs, and its
        // own shots' hits on its screen as claims
        public static void GuestStep(IMatchNet net, int seat, int seq, uint view, long step)
        {
            // the host's refusals: those hits off this screen now (repeats find nothing left)
            while (_verdictsIn.TryDequeue(out var v))
            {
                if (_born.Remove(v.Id)) _answered++;
                if (Outbox) _claims.RemoveAll(x => x.Id == v.Id);
                if (!_claimPredictions.Remove(v.Id, out var pred)) continue;
                if (!v.Counted && pred.Victim < PlayerEntity.MaxPlayers) PlayerEntity.Players[pred.Victim].NetRollbackPredicted(pred.Predicted);
            }
            foreach (PlayerEntity.NetShotOut s in PlayerEntity.NetShotLog)
            {
                if (s.Seat != seat || !_shotSeq.TryAdd(s.Number, (ushort)seq)) continue;
                _shotOrder.Enqueue(s.Number);
                if (_shotOrder.Count > 64) _shotSeq.Remove(_shotOrder.Dequeue());
            }
            foreach (PlayerEntity.NetHitClaim h in PlayerEntity.NetHitClaimLog)
            {
                if (!_shotSeq.TryGetValue(h.Shot, out ushort shotSeq) || h.Victim < 0 || h.Victim > 255) continue;
                ushort id = _nextId++;
                _claims.Add(new Claim(id, shotSeq, (byte)h.Victim, h.Head, view, h.VictimPosition, h.Point, h.Predicted));
                _made++;
                _born[id] = step;
                if (_born.Count > 512) _born.Clear();
                if (h.Predicted != 0)
                {
                    _claimPredictions[id] = ((byte)h.Victim, h.Predicted);
                    _claimOrder.Enqueue(id);
                    if (_claimOrder.Count > 64) _claimPredictions.Remove(_claimOrder.Dequeue());
                }
                if (_claims.Count > (Outbox ? OutboxMax : Repeat)) _claims.RemoveAt(0);
                _newStep = step;
            }
            PlayerEntity.NetHitClaimLog.Clear();
            if (Outbox)
            {
                // P2 outbox: a claim stays, and goes again every 6 steps, until its verdict comes back (the host answers a repeat
                // from its ledger); one never answered by OutboxLife steps is given up
                if (_claims.RemoveAll(x => step - _born.GetValueOrDefault(x.Id, step) > OutboxLife) is int gone and > 0) _unanswered += gone;
                if (_claims.Count == 0 || step != _newStep && step - _sentStep < 6) return;
            }
            else if (_claims.Count == 0 || step != _newStep && (step - _sentStep < 6 || step - _newStep > 60)) return;
            _sentStep = step;
            byte[] p = new byte[2 + _claims.Count * ClaimBytes];
            p[0] = TypeHitClaim;
            p[1] = (byte)_claims.Count;
            Span<byte> d = p.AsSpan(2);
            foreach (Claim c in _claims)
            {
                BinaryPrimitives.WriteUInt16LittleEndian(d, c.Id);
                BinaryPrimitives.WriteUInt16LittleEndian(d[2..], c.Seq);
                d[4] = c.Victim;
                d[5] = c.Head ? (byte)1 : (byte)0;
                BinaryPrimitives.WriteUInt32LittleEndian(d[6..], c.View);
                WriteV(d[10..], c.VictimPosition);
                WriteV(d[22..], c.Point);
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

        // (the session's receive thread) the host: a guest's claims
        private static void OnSide(int fromSeat, byte[] data)
        {
            if (data.Length < 2 || data[0] != TypeHitClaim || fromSeat < 0 || fromSeat >= _seats.Length) return;
            int n = data[1];
            if (data.Length < 2 + n * ClaimBytes) return;
            ReadOnlySpan<byte> d = data.AsSpan(2);
            lock (_lock)
            {
                SeatClaims seat = _seats[fromSeat] ??= new SeatClaims();
                for (int i = 0; i < n; i++, d = d[ClaimBytes..])
                {
                    var c = new Claim(BinaryPrimitives.ReadUInt16LittleEndian(d), BinaryPrimitives.ReadUInt16LittleEndian(d[2..]), d[4],
                        (d[5] & 1) != 0, BinaryPrimitives.ReadUInt32LittleEndian(d[6..]), ReadV(d[10..]), ReadV(d[22..]));
                    if (!seat.Seen.TryAdd(c.Id, 0))
                    {
                        // (a repeat: the guest still waits for its verdict, so the ledger answers it in HostStep)
                        if (FruitySync.On && seat.Repeats.Count < 64) seat.Repeats.Add(c.Id);
                        continue;
                    }
                    seat.Claims++;
                    _incoming.Add(new Pending { Seat = fromSeat, Claim = c });
                }
                if (seat.Seen.Count > 4096) seat.Seen.Clear(); // (ids wrap; the last few are what repeats)
            }
        }

        // the host's step, after it ran (before ShotCheck.HostStep drains the shot log): each remote seat's shots this step
        // with the input they went out with, then every claim checked; the ones that pass go to the next step
        public static void HostStep(CampaignHost host)
        {
            if (!PlayerEntity.NetShotLogOn) return;
            long frame = host.Frame;
            lock (_lock)
            {
                foreach (PlayerEntity.NetShotOut s in PlayerEntity.NetShotLog)
                {
                    if (s.Seat < 0 || s.Seat >= _seats.Length) continue;
                    SeatClaims seat = _seats[s.Seat] ??= new SeatClaims();
                    seat.Shots.Add((host.RemoteSeq[s.Seat], s.Number, host.RemoteView[s.Seat], frame));
                    if (seat.Shots.Count > 64) seat.Shots.RemoveAt(0);
                }
                foreach (Pending p in _incoming)
                {
                    p.Arrived = frame;
                    _pending.Add(p);
                }
                _incoming.Clear();
                for (int s = 0; s < _seats.Length; s++)
                {
                    if (_seats[s] is not { Repeats.Count: > 0 } rs) continue;
                    foreach (ushort id in rs.Repeats)
                    {
                        if (!rs.Ledger.TryGetValue(id, out bool counted)) continue; // (still being checked: its verdict comes by itself)
                        List<(ushort Id, bool Counted)> list = _verdictsOut[s] ??= new();
                        if (list.Exists(x => x.Id == id)) { _verdictNew[s] = frame; continue; }
                        list.Add((id, counted));
                        if (list.Count > VerdictList) list.RemoveAt(0);
                        _verdictNew[s] = frame;
                        rs.Answered++;
                    }
                    rs.Repeats.Clear();
                }
                for (int k = _pending.Count - 1; k >= 0; k--)
                {
                    Pending p = _pending[k];
                    SeatClaims seat = _seats[p.Seat]!;
                    CampaignHost.ClaimedHit hit = default;
                    Verdict v = PlayerEntity.HostHitClaims ? Check(host, seat, p, frame, out hit) : Verdict.Kind;
                    if (v == Verdict.Wait) continue;
                    _pending.RemoveAt(k);
                    seat.Verdicts[(int)v]++;
                    if (v == Verdict.Apply) host.ClaimHit(hit);
                    if (PlayerEntity.HostHitClaims) QueueVerdict(p.Seat, p.Claim.Id, v, frame);
                }
            }
            SendVerdicts(frame);
        }

        // a settled claim's verdict for its guest: counted (the host's copy hit, or the claim taken), or refused when that's
        // final (no host shot carries it, or none of the ones that do is still flying)
        private static void QueueVerdict(int seat, ushort id, Verdict v, long frame)
        {
            bool counted = v is Verdict.Agreed or Verdict.Apply;
            if (!counted && v != Verdict.NoShot)
            {
                PlayerEntity shooter = PlayerEntity.Players[seat];
                foreach (int number in _candidates)
                {
                    if (shooter.TryClaimShot(number, out PlayerEntity.ClaimShotInfo info) && info.BeamsInFlight > 0) return;
                }
            }
            List<(ushort Id, bool Counted)> list = _verdictsOut[seat] ??= new();
            list.Add((id, counted));
            if (list.Count > VerdictList) list.RemoveAt(0);
            _verdictNew[seat] = frame;
            if (FruitySync.On && _seats[seat] is { } sc && sc.Ledger.TryAdd(id, counted))
            {
                sc.LedgerOrder.Enqueue(id);
                if (sc.LedgerOrder.Count > LedgerSize) sc.Ledger.Remove(sc.LedgerOrder.Dequeue());
            }
        }

        private static void SendVerdicts(long frame)
        {
            if (_listening is not NetMatchSession net) return;
            for (int seat = 0; seat < _verdictsOut.Length; seat++)
            {
                if (_verdictsOut[seat] is not { Count: > 0 } list) continue;
                bool fresh = _verdictNew[seat] == frame;
                if (!fresh && (frame - _verdictSent[seat] < 6 || frame - _verdictNew[seat] > 60)) continue;
                _verdictSent[seat] = frame;
                byte[] packet = new byte[2 + list.Count * 3];
                packet[0] = TypeVerdicts;
                packet[1] = (byte)list.Count;
                for (int i = 0; i < list.Count; i++)
                {
                    BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(2 + i * 3), list[i].Id);
                    packet[4 + i * 3] = list[i].Counted ? (byte)1 : (byte)0;
                }
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

        private static Verdict Check(CampaignHost host, SeatClaims seat, Pending p, long frame, out CampaignHost.ClaimedHit hit)
        {
            hit = default;
            Claim c = p.Claim;
            if (c.Victim >= PlayerEntity.MaxPlayers || c.Victim == p.Seat || p.Seat >= PlayerEntity.MaxPlayers) return Verdict.Kind;
            // the shots the host fired for this seat from the claim's input, newest first, then from the inputs either side: a
            // late input repeats on the host (the same seq on two steps), so more than one can carry it; each is tried
            List<int> shots = _candidates;
            shots.Clear();
            for (int i = seat.Shots.Count - 1; i >= 0; i--)
            {
                if (seat.Shots[i].Seq == c.Seq) shots.Add(seat.Shots[i].Number);
            }
            for (int i = seat.Shots.Count - 1; i >= 0; i--)
            {
                ushort seq = seat.Shots[i].Seq;
                // (P2: the host runs only the newest of the inputs waiting and folds the older ones' presses into it, so a shot
                // fired from the claim's input may carry a later seq)
                if (seq != c.Seq && ((ushort)(seq - c.Seq) <= (FruitySync.On && !OutboxOff ? FoldSpan : 1) || (ushort)(c.Seq - seq) <= 1))
                {
                    shots.Add(seat.Shots[i].Number);
                }
            }
            PlayerEntity shooter = PlayerEntity.Players[p.Seat], victim = PlayerEntity.Players[c.Victim];
            if (shots.Count == 0)
            {
                if (frame - p.Arrived < ShotWait) return Verdict.Wait;
                return FruitySync.On && !OutboxOff ? Rescue(host, seat, shooter, victim, p, c, frame, out hit) : Verdict.NoShot;
            }
            foreach (int number in shots)
            {
                if (shooter.TryClaimShot(number, out PlayerEntity.ClaimShotInfo info) && (info.HitMask & (1u << c.Victim)) != 0)
                {
                    return Verdict.Agreed;
                }
            }
            // the frame the guest's screen showed: recent enough, and the player there then (not since respawned)
            if (frame - c.View > LagCompensation.MaxRewind + ExtraAge || host.Lag == null
                || !host.Lag.TryPoseAt(c.Victim, c.View, out Vector3 was, out int epoch))
            {
                return Verdict.Old;
            }
            if (epoch != victim.NetAimEpoch || victim.Health == 0) return Verdict.Gone;
            if ((was - c.VictimPosition).Length > VictimReach) return Verdict.Far;
            Vector3 off = c.Point - was;
            if (MathF.Sqrt(off.X * off.X + off.Z * off.Z) > BodyRadius || off.Y < -BodyBelow || off.Y > BodyAbove) return Verdict.Body;
            Verdict first = Verdict.NoShot;
            foreach (int number in shots)
            {
                Verdict v = CheckShot(host, seat, shooter, victim, p, c, number, was, frame, out hit);
                if (v is Verdict.Apply or Verdict.Wait) return v;
                if (first == Verdict.NoShot) first = v; // (the claim's own input's shot says why, if none passes)
            }
            return first;
        }

        private static readonly List<int> _candidates = new();

        private const int RescueShotAge = 900; // frames: how old the newest shot lent for its weapon's damage may be

        // P2 rescue (Fruity Prime's authority-side claim check, Net/NETWORK-HITCLAIMS.md): the input carrying the shot never reached
        // the host, so no host shot to check the line against. The claim stands on the victim's body in the host's own history at
        // the frame the guest saw, within reach of the shooter, with the damage capped to the seat's own weapon (the newest shot it
        // fired lends it: a claim can't hit harder than that), and not when the host's own shots since that frame already hit them
        private static Verdict Rescue(CampaignHost host, SeatClaims seat, PlayerEntity shooter, PlayerEntity victim, Pending p, Claim c,
            long frame, out CampaignHost.ClaimedHit hit)
        {
            hit = default;
            if (frame - c.View > LagCompensation.MaxRewind + ExtraAge || host.Lag == null
                || !host.Lag.TryPoseAt(c.Victim, c.View, out Vector3 was, out int epoch))
            {
                return Verdict.Old;
            }
            if (epoch != victim.NetAimEpoch || victim.Health == 0) return Verdict.Gone;
            if ((was - c.VictimPosition).Length > VictimReach) return Verdict.Far;
            Vector3 off = c.Point - was;
            if (MathF.Sqrt(off.X * off.X + off.Z * off.Z) > BodyRadius || off.Y < -BodyBelow || off.Y > BodyAbove) return Verdict.Body;
            for (int i = seat.Shots.Count - 1; i >= 0 && seat.Shots[i].Frame >= (long)c.View - ExtraAge; i--)
            {
                if (shooter.TryClaimShot(seat.Shots[i].Number, out PlayerEntity.ClaimShotInfo done) && (done.HitMask & (1u << c.Victim)) != 0)
                {
                    return Verdict.Agreed; // (the host's own shot hit them in the window: counted already)
                }
            }
            for (int i = seat.Shots.Count - 1; i >= 0 && frame - seat.Shots[i].Frame <= RescueShotAge; i--)
            {
                int number = seat.Shots[i].Number;
                if (!shooter.TryClaimShot(number, out PlayerEntity.ClaimShotInfo shot) || !shot.Claimable) continue;
                if ((c.Point - shooter.Position).Length > shot.Reach + LineReach) return Verdict.Reach;
                bool head = c.Head && BeamProjectileEntity.HostClaimHeadshot(shot.Beam, shooter.Position, c.Point, was,
                    Fixed.ToFloat(victim.Values.MaxPickupHeight), victim.IsAltForm);
                hit = new CampaignHost.ClaimedHit(p.Seat, number, c.Victim, c.Point, head, Rescue: true);
                return Verdict.Apply;
            }
            return Verdict.NoShot;
        }

        // one of the host's shots against the claim: the shot's own checks
        private static Verdict CheckShot(CampaignHost host, SeatClaims seat, PlayerEntity shooter, PlayerEntity victim, Pending p,
            Claim c, int number, Vector3 was, long frame, out CampaignHost.ClaimedHit hit)
        {
            hit = default;
            if (!shooter.TryClaimShot(number, out PlayerEntity.ClaimShotInfo shot)) return Verdict.NoShot;
            if (shot.HitMask != 0) return Verdict.Other; // (the host's copy hit someone else: a beam stops at the first player)
            if (!shot.Claimable) return Verdict.Kind;
            // the frame the claim names: one the guest saw while this shot flew (from its input's view, a step either way)
            uint fired = 0;
            foreach ((ushort _, int n, uint view, long _) in seat.Shots)
            {
                if (n == number) fired = view;
            }
            if (fired != 0 && c.View != 0)
            {
                long late = (long)c.View - fired;
                if (late < -ExtraAge || late > shot.FlightFrames + ExtraAge) return Verdict.Time;
            }
            // on the shot's line (within the beam's own radius more), with nothing solid between the gun and the hit
            Vector3 dir = shot.Direction.LengthSquared > 1e-8f ? shot.Direction.Normalized() : Vector3.UnitZ;
            Vector3 toPoint = c.Point - shot.Origin;
            float along = Vector3.Dot(toPoint, dir);
            if (along < 0 || (toPoint - dir * along).Length > LineReach + shot.Radius) return Verdict.Line;
            if (along > shot.Reach + LineReach + shot.Radius) return Verdict.Reach; // (further than the beam can fly)
            CollisionResult res = default;
            if (along > 0.3f && CollisionDetection.CheckBetweenPoints(shot.Origin, shot.Origin + dir * (along - 0.3f), TestFlags.Beams,
                host.Scene, ref res))
            {
                return Verdict.Wall;
            }
            if (shot.BeamsInFlight > 0 && frame - p.Arrived < FlightWait) return Verdict.Wait; // (it may still hit by itself)
            bool head = c.Head && BeamProjectileEntity.HostClaimHeadshot(shot.Beam, shot.Origin, c.Point, was,
                Fixed.ToFloat(victim.Values.MaxPickupHeight), victim.IsAltForm);
            hit = new CampaignHost.ClaimedHit(p.Seat, number, c.Victim, c.Point, head);
            return Verdict.Apply;
        }

        // the host's log: per seat that claimed, "seat 1: claims 40, the host hit too 36, applied 3, refused 1 (old 0, far 0, body 0,
        // line 0, wall 1, no shot 0, kind 0, gone 0, hit another 0)"
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
                    int refused = v[(int)Verdict.Old] + v[(int)Verdict.Far] + v[(int)Verdict.Body] + v[(int)Verdict.Line]
                        + v[(int)Verdict.Wall] + v[(int)Verdict.NoShot] + v[(int)Verdict.Kind] + v[(int)Verdict.Gone] + v[(int)Verdict.Other]
                        + v[(int)Verdict.Time] + v[(int)Verdict.Reach];
                    if (line.Length > 0) line.Append("; ");
                    line.Append($"seat {i}: claims {s.Claims}, the host hit too {v[(int)Verdict.Agreed]}, applied {v[(int)Verdict.Apply]}, "
                        + $"refused {refused} (old {v[(int)Verdict.Old]}, far {v[(int)Verdict.Far]}, body {v[(int)Verdict.Body]}, line "
                        + $"{v[(int)Verdict.Line]}, wall {v[(int)Verdict.Wall]}, no shot {v[(int)Verdict.NoShot]}, kind {v[(int)Verdict.Kind]}, "
                        + $"gone {v[(int)Verdict.Gone]}, hit another {v[(int)Verdict.Other]}, not then {v[(int)Verdict.Time]}, "
                        + $"too far {v[(int)Verdict.Reach]})" + (s.Answered > 0 ? $", {s.Answered} repeats answered from the ledger" : ""));
                }
            }
            return line.Length > 0 ? line.ToString() : "no claims";
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
