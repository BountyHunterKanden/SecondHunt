using System;
using System.Buffers.Binary;
using System.Text;
using MphRead;
using MphRead.Entities;
using MphRead.Formats;
using MphRecomp.Campaign;
using OpenTK.Mathematics;

namespace MphRecomp.Multiplayer.Net
{
    // Client-reported position (0.3.6: an online guest sends its own predicted position with its input). Each online
    // guest's input packet carries where its own player was at the start of that input's step on its screen. The host takes
    // that in place of its own copy of the player, at the start of the step that input drives, when the report passes its
    // checks, so the mover is where they saw themselves for everyone (the host's copy no longer drifts from the guest's on a
    // late or repeated input). The host stays the judge of everything else: hits, damage, kills, pickups, its own moves.
    // What a report must pass (MatchSettings.ClientPosition, null = on; off per match from the server):
    //   - it was made after the guest saw the host's last move of that player made by the host itself (the ack): a
    //     knockback or hit push (Magmaul etc.), a jump pad, the Trooper's standing body pushing it out, a respawn, teleport or
    //     turret seat, a morph or unmorph, the drone's split or merge, a freeze or stun ending, a death. The report names the
    //     newest host snapshot the guest's prediction had taken in (compared or set its own player against: ReportBasis); one
    //     older than the first snapshot that showed the move is stale ("missed");
    //   - the player is alive and spawned here, not frozen or stunned, not mid-morph, in the same form, not in a turret seat;
    //   - it's within Limit (4 units) of the host's own copy, nothing solid between the two (TestFlags.Players), and the
    //     moves taken lately add up to no more than Limit, leaking LeakPerSecond (no running faster by reporting ahead).
    // Taken reports apply at the start of the host's next step (CampaignHost.TakeClientPosition, Multiplayer/MatchClientPosition.cs)
    // and go in the host's replay as NetEvents of kind Position (format 54), so playback is exact.
    // Wire, guest -> host: a trailer after the inputs of the online input packet (NetMatchSession.SendInput): 'P', u32 basis
    // tick, 3 x f32 position, u8 flags (1 = alt form), for the packet's newest input only (a lost packet's input comes again
    // in the next one, without its report: the host runs it on its own copy that frame). An older host ignores the trailer.
    // Knockback (0.3.6, as Fruity Prime does it): flag 2 = this device runs the host's pushes on its own player itself, and
    // 3 x f32 of its speed at the start of the step follow (30 bytes; an older host reads the first 18). The host then hands
    // a hit's push to the device in the snapshot (PlayerEntity.NetHandPush, SeatExtras' push section) instead of pushing its
    // own copy, so a push isn't a host move and reports keep coming through it; for a while after a push the host takes the
    // reported speed with the position (CampaignHost.ApplyClientPositions), so its copy slides as the device's does.
    public static class ClientPosition
    {
        public const int TrailerSize = 18;
        public const int TrailerMax = 30;
        private const byte TrailerMark = 0x50; // 'P'

        // the tunables (SecondHunt.MatchHost --position-limit; the PC test's poslimit=)
        public static float Limit { get; set; } = 4f;
        public static float LeakPerSecond { get; set; } = 1.5f;

        // Fruity Prime's player sync, server side (2026-10-19; each switchable: MatchHost --fruity-pin / --fruity-hold, the
        // PC test's fruitypin= / fruityhold=). The checks above (stale, form, far, wall, budget) stay on top of both: a report
        // that fails them is not trusted, and the pin below goes through the same distance, wall and budget checks.
        //   Pin: a taken report pins the seat before the step (as before) and again after it, at the report plus one step of the
        //   speed the gaps between its consecutive reports give, which is also the speed the seat steps with (Fruity: a player's
        //   place and speed are the device's word, not the host's own physics)
        //   Hold: a frame with no input from the seat holds it at its last pinned place (up to HoldFrames) instead of sliding it
        public static bool Pin { get; set; } = false; // off 10-20: a phone test showed split positions
        public static bool Hold { get; set; } = false;
        public static int PinAfterMode = 1; // (PC test fruitypinafter: 0 none, 1 report + gap, 2 report + gap on X/Z only)
        private const int HoldFrames = 30;
        private const int MaxReportGap = 4; // (inputs apart, for a speed from two reports)
        private const float SpeedMax = 3f;
        private const float SpeedAgree = 0.25f; // (X/Z, per frame: a device's reported speed this close to the gap speed is taken as is)

        public static int WriteTrailer(Span<byte> d, in NetInput input)
        {
            if (input.ReportBasis == 0 || d.Length < TrailerSize)
            {
                return 0;
            }
            d[0] = TrailerMark;
            BinaryPrimitives.WriteUInt32LittleEndian(d[1..], input.ReportBasis);
            BinaryPrimitives.WriteSingleLittleEndian(d[5..], input.Report.X);
            BinaryPrimitives.WriteSingleLittleEndian(d[9..], input.Report.Y);
            BinaryPrimitives.WriteSingleLittleEndian(d[13..], input.Report.Z);
            d[17] = (byte)(input.ReportAlt ? 1 : 0);
            if (!input.ReportPushes || d.Length < TrailerMax)
            {
                return TrailerSize;
            }
            d[17] |= 2;
            BinaryPrimitives.WriteSingleLittleEndian(d[18..], input.ReportSpeed.X);
            BinaryPrimitives.WriteSingleLittleEndian(d[22..], input.ReportSpeed.Y);
            BinaryPrimitives.WriteSingleLittleEndian(d[26..], input.ReportSpeed.Z);
            return TrailerMax;
        }

        public static void ReadTrailer(ReadOnlySpan<byte> d, ref NetInput input)
        {
            if (d.Length < TrailerSize || d[0] != TrailerMark)
            {
                return;
            }
            input.ReportBasis = BinaryPrimitives.ReadUInt32LittleEndian(d[1..]);
            input.Report = new Vector3(BinaryPrimitives.ReadSingleLittleEndian(d[5..]), BinaryPrimitives.ReadSingleLittleEndian(d[9..]),
                BinaryPrimitives.ReadSingleLittleEndian(d[13..]));
            input.ReportAlt = (d[17] & 1) != 0;
            if ((d[17] & 2) != 0 && d.Length >= TrailerMax)
            {
                input.ReportPushes = true;
                input.ReportSpeed = new Vector3(BinaryPrimitives.ReadSingleLittleEndian(d[18..]),
                    BinaryPrimitives.ReadSingleLittleEndian(d[22..]), BinaryPrimitives.ReadSingleLittleEndian(d[26..]));
            }
        }

        // ---- host ----

        // why a report wasn't taken; and the host's own moves (the kind of the last one a stale report missed)
        private enum Refusal { Missed, Bad, Dead, Frozen, Morphing, Form, Seat, Far, Wall, Budget, Count }
        private enum Move { Push, Pad, Body, Respawn, Form, Drone, Frozen, Death, Count }
        private static readonly string[] MoveNames = { "push", "pad", "body", "respawn/teleport", "form", "drone", "frozen", "death" };

        private sealed class SeatState
        {
            public bool Seen;
            public uint LastMoveTick;
            public Move LastMove = Move.Respawn; // (before the first step: the spawn)
            // (the state compared around each step)
            public int Moves, AimEpoch;
            public bool Alt, Morphing, Dead, Frozen, Half, Seated;
            public int Inputs, Reports, Accepted;
            public double MovedSum, LagSum;
            public float MovedMax;
            public float Budget;
            public long BudgetFrame;
            // (Pin / Hold) the last report taken and its input, the place the seat was last pinned at, and what the step ahead owes it
            public Vector3 PrevReport;
            public int PrevSeq = -1;
            public Vector3 LastPin;
            public long LastPinFrame = -1;
            public bool PinAfter, HoldAfter;
            public Vector3 Gap;
            public int Pins, Holds, Snaps;
            public readonly int[] Refused = new int[(int)Refusal.Count];
            public readonly int[] Missed = new int[(int)Move.Count];
            public readonly int[] MovesBy = new int[(int)Move.Count];
        }

        private static readonly SeatState?[] _seats = new SeatState?[16];
        private static bool _on;

        // MatchNetDriver, a match starting: a host of an online match with the rule on checks its guests' reports
        public static void Start(IMatchNet net, CampaignHost host)
        {
            Array.Clear(_seats);
            _on = net is NetMatchSession && net.IsHost && (host.Match?.ClientPosition ?? true);
        }

        // O5b: this device took the match over (its own copy of the match: every seat's state is counted from now)
        public static void TookOver(CampaignHost host)
        {
            Array.Clear(_seats);
            _on = host.Match?.ClientPosition ?? true;
        }

        // the host, before its step: a remote seat's input with its report (MatchNetDriver, right after SetRemoteInput)
        public static void HostOffer(CampaignHost host, int seat, in NetInput input)
        {
            if (!_on || seat < 0 || seat >= _seats.Length || seat >= PlayerEntity.MaxPlayers)
            {
                return;
            }
            SeatState st = _seats[seat] ??= new SeatState();
            st.Inputs++;
            if (input.ReportBasis == 0)
            {
                return;
            }
            st.Reports++;
            PlayerEntity p = PlayerEntity.Players[seat];
            Vector3 at = input.Report;
            if (FruitySync.On)
            {
                FruityOffer(host, seat, st, p, at, input);
                return;
            }
            Refusal? refused = null;
            if (!p.HostRemote || !Single.IsFinite(at.X) || !Single.IsFinite(at.Y) || !Single.IsFinite(at.Z) || input.ReportBasis > host.Frame)
            {
                refused = Refusal.Bad;
            }
            else if (!st.Seen || input.ReportBasis < st.LastMoveTick)
            {
                refused = Refusal.Missed;
                st.Missed[(int)st.LastMove]++;
            }
            else if (!p.LoadFlags.TestFlag(LoadFlags.Active) || !p.LoadFlags.TestFlag(LoadFlags.Spawned) || p.Health == 0)
            {
                refused = Refusal.Dead;
            }
            else if (p.NetFrozen || p.ParalyzerStunned)
            {
                refused = Refusal.Frozen;
            }
            else if (p.IsMorphing || p.IsUnmorphing)
            {
                refused = Refusal.Morphing;
            }
            else if (p.IsAltForm != input.ReportAlt)
            {
                refused = Refusal.Form;
            }
            else if (p.HostSeated)
            {
                refused = Refusal.Seat;
            }
            else
            {
                Vector3 off = at - p.Position;
                float moved = off.Length;
                // (the moves taken lately leak away at LeakPerSecond: honest ones are small and rare, a run ahead adds up)
                float budget = MathF.Max(0, st.Budget - LeakPerSecond * (host.Frame - st.BudgetFrame) / 60f);
                if (moved > Limit)
                {
                    refused = Refusal.Far;
                }
                else if (budget + moved > Limit)
                {
                    refused = Refusal.Budget;
                }
                else if (moved > 0.05f && Blocked(host, p, off))
                {
                    refused = Refusal.Wall;
                }
                else
                {
                    st.Budget = budget + moved;
                    st.BudgetFrame = host.Frame;
                    st.Accepted++;
                    st.MovedSum += moved;
                    st.MovedMax = MathF.Max(st.MovedMax, moved);
                    st.LagSum += host.Frame - input.ReportBasis;
                    Vector3 speed = input.ReportSpeed;
                    bool pushes = input.ReportPushes && Single.IsFinite(speed.X) && Single.IsFinite(speed.Y) && Single.IsFinite(speed.Z);
                    if (!Pin)
                    {
                        host.TakeClientPosition(seat, at, pushes, pushes ? speed : null);
                    }
                    else
                    {
                        // the speed from the gap to the seat's previous report (consecutive inputs); right after a push handed to its
                        // device, that device's own reported speed (the slide it runs) is the better word
                        int apart = input.Seq - st.PrevSeq;
                        Vector3? gap = null;
                        if (st.PrevSeq >= 0 && apart is >= 1 and <= MaxReportGap)
                        {
                            Vector3 v = (at - st.PrevReport) / apart;
                            if (v.Length <= SpeedMax)
                            {
                                gap = new Vector3(v.X, p.Speed.Y, v.Z);
                            }
                        }
                        st.PrevReport = at;
                        st.PrevSeq = input.Seq;
                        // otherwise the device's own speed when it agrees with the gap (the host's copy then runs the step the device
                        // ran; a gap speed that is a little off made the device's push check hold its reports), else the gap
                        Vector3? use = gap;
                        if (pushes && speed.Length <= SpeedMax)
                        {
                            if (host.PushRecent(seat))
                            {
                                use = speed;
                            }
                            else if (gap is not Vector3 g || new Vector2(speed.X - g.X, speed.Z - g.Z).Length <= SpeedAgree)
                            {
                                use = new Vector3(speed.X, p.Speed.Y, speed.Z);
                            }
                        }
                        st.LastPin = at;
                        st.LastPinFrame = host.Frame;
                        st.PinAfter = gap.HasValue;
                        st.Gap = gap ?? Vector3.Zero;
                        host.TakeClientPosition(seat, at, pushes, use, decided: true);
                    }
                }
            }
            if (refused is Refusal r)
            {
                st.Refused[(int)r]++;
            }
        }

        // Fruity Prime's P1 rule (docs/FRUITY_PORT.md; a FruitySync match): the report is the seat's place, refused only when it
        // can't be (not a number, past FruityBound, zero), the seat is dead, frozen, in another form or a turret seat, or the
        // report was made before a respawn/teleport, death, form change or drone split the host made (their life/generation
        // check). No distance, wall or budget check. The seat is placed hard there with the speed its reports' gap gives
        // (over FruitySnap units = a snap, speed 0; at most FruitySpeedMax); a frame with no input runs on its last input.
        private const float FruityBound = 100000f, FruitySnap = 15f, FruitySpeedMax = 5f;
        private const int FruityMaxApart = 8;

        private static bool FruityLife(Move m) => m is Move.Respawn or Move.Death or Move.Form or Move.Drone;

        private static void FruityOffer(CampaignHost host, int seat, SeatState st, PlayerEntity p, Vector3 at, in NetInput input)
        {
            Refusal? refused = null;
            if (!p.HostRemote || !Single.IsFinite(at.X) || !Single.IsFinite(at.Y) || !Single.IsFinite(at.Z) || MathF.Abs(at.X) >= FruityBound
                || MathF.Abs(at.Y) >= FruityBound || MathF.Abs(at.Z) >= FruityBound || at == Vector3.Zero || input.ReportBasis > host.Frame)
            {
                refused = Refusal.Bad;
            }
            else if (!st.Seen || input.ReportBasis < st.LastMoveTick && FruityLife(st.LastMove))
            {
                refused = Refusal.Missed;
                st.Missed[(int)st.LastMove]++;
            }
            else if (!p.LoadFlags.TestFlag(LoadFlags.Active) || !p.LoadFlags.TestFlag(LoadFlags.Spawned) || p.Health == 0)
            {
                refused = Refusal.Dead;
            }
            else if (p.NetFrozen || p.ParalyzerStunned)
            {
                refused = Refusal.Frozen;
            }
            else if (p.IsAltForm != input.ReportAlt)
            {
                refused = Refusal.Form;
            }
            else if (p.HostSeated)
            {
                refused = Refusal.Seat;
            }
            if (refused is Refusal r)
            {
                st.Refused[(int)r]++;
                return;
            }
            float moved = (at - p.Position).Length;
            st.Accepted++;
            st.MovedSum += moved;
            st.MovedMax = MathF.Max(st.MovedMax, moved);
            st.LagSum += host.Frame - input.ReportBasis;
            Vector3 speed = Vector3.Zero;
            int apart = input.Seq - st.PrevSeq;
            if (st.PrevSeq >= 0 && apart >= 1)
            {
                Vector3 step = at - st.PrevReport;
                if (step.Length > FruitySnap)
                {
                    st.Snaps++;
                }
                else
                {
                    speed = step / Math.Min(apart, FruityMaxApart);
                    if (speed.Length > FruitySpeedMax) speed = speed.Normalized() * FruitySpeedMax;
                }
            }
            st.PrevReport = at;
            st.PrevSeq = input.Seq;
            st.LastPin = at;
            st.LastPinFrame = host.Frame;
            st.PinAfter = true;
            Vector3 rs = input.ReportSpeed;
            bool pushes = input.ReportPushes && Single.IsFinite(rs.X) && Single.IsFinite(rs.Y) && Single.IsFinite(rs.Z);
            host.TakeClientPosition(seat, at, pushes, speed, decided: true);
        }

        // the host, after its step: the Pin and Hold pins. A seat that was pinned to a report is pinned again at the report plus one
        // step of its speed (the place its device has it at the end of that step), when that passes the same checks (Limit,
        // Wall, Budget); a seat held on a missing input is put back at its last pinned place. Queued for the next step's start too
        // (so the recording and a replay have it)
        public static void HostPin(CampaignHost host)
        {
            if (!_on)
            {
                return;
            }
            for (int seat = 0; seat < _seats.Length && seat < PlayerEntity.MaxPlayers; seat++)
            {
                SeatState? st = _seats[seat];
                if (st == null || (!st.PinAfter && !st.HoldAfter) || PlayerEntity.Players.Count <= seat)
                {
                    continue;
                }
                PlayerEntity p = PlayerEntity.Players[seat];
                bool held = st.HoldAfter;
                st.PinAfter = st.HoldAfter = false;
                if (FruitySync.On)
                {
                    if (st.LastMoveTick > st.LastPinFrame && FruityLife(st.LastMove))
                    {
                        st.LastPinFrame = -1;
                        continue;
                    }
                    // (their server pins back to the report and shows that; ours shows the stepped place, one step on along the
                    // same path, and a pin can't move it now without breaking replays (PinNow). So a frame with no input holds
                    // the seat where the report's step left it: holding at the report itself stepped it back and forth, ~5%
                    // more travel drawn than walked, PC gap test 10-20)
                    if (!held)
                    {
                        st.LastPin = p.Position;
                        st.LastPinFrame = host.Frame;
                        st.Pins++;
                    }
                    continue;
                }
                if (st.LastMoveTick > st.LastPinFrame)
                {
                    // the game moved the seat itself this step (a pad, a push, a body, a respawn...): that stands, and the pinned
                    // place is stale until the next report
                    st.LastPinFrame = -1;
                    continue;
                }
                if (!p.HostRemote || p.Health == 0 || !p.LoadFlags.TestFlag(LoadFlags.Spawned) || p.NetFrozen || p.ParalyzerStunned
                    || p.IsMorphing || p.IsUnmorphing || p.HostSeated)
                {
                    continue;
                }
                Vector3 target = held ? st.LastPin : st.LastPin + st.Gap;
                if (!held && PinAfterMode == 0)
                {
                    continue;
                }
                if (!held && PinAfterMode == 2)
                {
                    target.Y = p.Position.Y; // (the step's own height: a jump or fall isn't bent by the old vertical speed)
                }
                Vector3 off = target - p.Position;
                float moved = off.Length;
                if (!Single.IsFinite(moved) || moved > Limit)
                {
                    continue;
                }
                if (!held)
                {
                    float budget = MathF.Max(0, st.Budget - LeakPerSecond * (host.Frame - st.BudgetFrame) / 60f);
                    // (checked, not charged: the report it carries on from was charged when taken, and charging the pin too
                    // refused 100-300 honest reports a minute in the PC A/B)
                    if (budget + moved > Limit || (moved > 0.05f && Blocked(host, p, off)))
                    {
                        continue;
                    }
                    st.LastPin = target;
                    st.LastPinFrame = host.Frame;
                }
                // (pinned on: the speed the step ran with stays; held: stopped)
                host.PinNow(seat, target, held ? new Vector3(0, p.Speed.Y, 0) : null);
                st.Pins++;
            }
        }

        // the host, a frame with no input from the seat (MatchNet): Hold puts it at its last pinned place; false = not held (the
        // pinned place is too old, or the rule is off), the caller does what it did before
        public static bool HostHold(CampaignHost host, int seat)
        {
            // (Fruity: a frame with no input runs the seat on its last input, not stopped: a bursty link (mobile data: two
            // inputs one step, none the next) stopped and jumped it, a stair the others drew as jitter; phone test 10-20)
            if (!_on || !Hold || FruitySync.On || seat < 0 || seat >= _seats.Length || seat >= PlayerEntity.MaxPlayers || PlayerEntity.Players.Count <= seat)
            {
                return false;
            }
            SeatState? st = _seats[seat];
            PlayerEntity p = PlayerEntity.Players[seat];
            if (st == null || st.LastPinFrame < 0 || host.Frame - st.LastPinFrame > HoldFrames || !p.HostRemote)
            {
                return false;
            }
            if (st.LastMoveTick > st.LastPinFrame && (!FruitySync.On || FruityLife(st.LastMove)))
            {
                st.LastPinFrame = -1;
                return false;
            }
            if (p.Health == 0 || !p.LoadFlags.TestFlag(LoadFlags.Spawned) || p.NetFrozen || p.ParalyzerStunned || p.IsMorphing
                || p.IsUnmorphing || p.HostSeated)
            {
                return false;
            }
            host.TakeClientPosition(seat, st.LastPin, p.NetClientPushes, new Vector3(0, p.Speed.Y, 0), decided: true);
            st.HoldAfter = true;
            st.Holds++;
            return true;
        }

        // something solid between the host's copy and the report (from the collision volume's centre, as the player moves)
        private static bool Blocked(CampaignHost host, PlayerEntity p, Vector3 off)
        {
            Vector3 from = p.Volume.SpherePosition;
            CollisionResult res = default;
            return CollisionDetection.CheckBetweenPoints(from, from + off, TestFlags.Players, host.Scene, ref res);
        }

        // the host, after its step: each remote seat's moves the game made by itself this step (the hooks' count, and the state
        // that changes with a respawn, teleport, turret seat, form, the drone, a freeze or stun, a death). The first snapshot
        // that shows it has this tick (CaptureSnapshot comes next), so a report must be based on that one or a later one.
        public static void HostStep(CampaignHost host)
        {
            if (!_on || host.Match == null)
            {
                return;
            }
            int seats = Math.Min(Math.Min(host.Match.Players.Count, PlayerEntity.MaxPlayers), _seats.Length);
            for (int i = 0; i < seats; i++)
            {
                PlayerEntity p = PlayerEntity.Players[i];
                if (!p.HostRemote)
                {
                    continue;
                }
                SeatState st = _seats[i] ??= new SeatState();
                bool alt = p.IsAltForm, morphing = p.IsMorphing || p.IsUnmorphing;
                bool dead = p.Health == 0 || !p.LoadFlags.TestFlag(LoadFlags.Spawned) || !p.LoadFlags.TestFlag(LoadFlags.Active);
                bool frozen = p.NetFrozen || p.ParalyzerStunned, half = p.Flags2.TestFlag(PlayerFlags2.Halfturret), seated = p.HostSeated;
                Move? move = null;
                if (p.NetHostMoves != st.Moves)
                {
                    move = p.NetHostMoveKind switch { NetHostMove.Pad => Move.Pad, NetHostMove.Body => Move.Body, _ => Move.Push };
                }
                if (alt != st.Alt || morphing != st.Morphing) move = Move.Form;
                if (half != st.Half) move = Move.Drone;
                if (frozen != st.Frozen) move = Move.Frozen;
                if (p.NetAimEpoch != st.AimEpoch || seated != st.Seated || !st.Seen) move = Move.Respawn;
                if (dead != st.Dead) move = dead ? Move.Death : Move.Respawn;
                if (move is Move m)
                {
                    st.LastMoveTick = (uint)host.Frame;
                    st.LastMove = m;
                    if (st.Seen) st.MovesBy[(int)m]++;
                }
                st.Seen = true;
                st.Moves = p.NetHostMoves;
                st.AimEpoch = p.NetAimEpoch;
                st.Alt = alt;
                st.Morphing = morphing;
                st.Dead = dead;
                st.Frozen = frozen;
                st.Half = half;
                st.Seated = seated;
            }
        }

        // the host's log: per seat that reported, "seat 1: inputs 3600, reports 3550, accepted 3100 (moved avg 0.012 max 1.20,
        // basis 21 frames old), refused 450 (missed a host move 400 [push 30, pad 0, ...], bad 0, dead 40, ...); host moves ..."
        public static string HostLine()
        {
            if (!_on)
            {
                return "off";
            }
            var line = new StringBuilder();
            for (int i = 0; i < _seats.Length; i++)
            {
                SeatState? s = _seats[i];
                if (s == null || s.Inputs == 0)
                {
                    continue;
                }
                int refused = 0;
                foreach (int n in s.Refused) refused += n;
                int[] r = s.Refused;
                if (line.Length > 0) line.Append("; ");
                line.Append($"seat {i}: inputs {s.Inputs}, reports {s.Reports}, accepted {s.Accepted}");
                if (s.Accepted > 0)
                {
                    line.Append($" (moved avg {s.MovedSum / s.Accepted:0.000} max {s.MovedMax:0.00}, basis {s.LagSum / s.Accepted:0.0} frames old)");
                }
                line.Append($", refused {refused} (missed a host move {r[(int)Refusal.Missed]} [{Kinds(s.Missed)}], bad {r[(int)Refusal.Bad]}, "
                    + $"dead {r[(int)Refusal.Dead]}, frozen {r[(int)Refusal.Frozen]}, morphing {r[(int)Refusal.Morphing]}, form "
                    + $"{r[(int)Refusal.Form]}, seat {r[(int)Refusal.Seat]}, far {r[(int)Refusal.Far]}, wall {r[(int)Refusal.Wall]}, "
                    + $"budget {r[(int)Refusal.Budget]}); host moves [{Kinds(s.MovesBy)}]; pins {s.Pins}, held frames {s.Holds}"
                    + (FruitySync.On ? $", snaps {s.Snaps}" : ""));
            }
            return line.Length > 0 ? line.ToString() : "no reports";
        }

        private static string Kinds(int[] counts)
        {
            var b = new StringBuilder();
            for (int k = 0; k < (int)Move.Count; k++)
            {
                if (b.Length > 0) b.Append(", ");
                b.Append($"{MoveNames[k]} {counts[k]}");
            }
            return b.ToString();
        }
    }
}
