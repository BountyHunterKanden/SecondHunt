using System;
using MphRead.Entities;
using OpenTK.Mathematics;

namespace MphRecomp.Multiplayer.Net
{
    // N3 (O5): the online client's own player runs on its own input at once (MphRead's full ProcessPlayer, as on LAN), and
    // the host's word on it is checked like for like: each snapshot says which of this device's inputs the host had
    // applied last (Snapshot.InputAck), and this device remembers where its own player was right after that same input.
    // The difference is the error; it's added to the player's pose now, spread over a few frames (big ones at once), and
    // to every pose remembered after that input, so it's counted once. Comparing against the host's pose of a round trip
    // ago (LAN's 0.5-unit pull-back) would rubber-band a moving player on every internet snapshot.
    // A frame the host held this seat's last input (it hadn't come) counts too when this device's inputs in those frames
    // were the same; otherwise that snapshot is skipped. A pose the host set outright (respawn, a big pull) starts over.
    public enum ReconcileResult { NotCompared, Compared, Snap }

    public sealed class ClientPrediction
    {
        private const int Size = 128; // inputs remembered (~2 s)
        private const float DeadZone = 0.01f; // float noise across devices
        private const float SnapNow = 2f; // errors this big go in at once
        private const float BlendPerFrame = 0.3f;
        // in alt form, height errors smaller than this are left alone, and the vertical speed never (2026-10-19, alt
        // forms bouncing up and down as they moved online): a ball's bounce, Lockjaw's hover spring, Spire's climb
        // and the alt forms' 3x gravity all act on its height, so a vertical kick a round trip late each snapshot set it
        // bobbing at the snapshot rate; a real height drift (a ledge taken differently) grows past it and is corrected
        private const float AltHeightDeadZone = 0.25f;
        public static bool AltHeightFix { get; set; } = true; // (the PC test turns it off to compare: -onlinetest ball=1 altfix=0)
        // high-ping fixes (2026-10-19, Japan ping sim): no speed correction, and the local pose is set onto the host's only when
        // the like-for-like error is past SnapError (not when the host's round-trip-old pose is far from the current one: a
        // fast player at 200 ms is that far all the time). Off = the earlier behaviour (-onlinetest speedfix=0 snapfix=0)
        public static bool SpeedFix { get; set; } = true;
        public static bool SnapFix { get; set; } = true;
        private const float SnapError = 4f;
        // Fruity Prime's client rule (2026-10-19; PC test fruitycorrect=0 to compare): the host trusts this device's
        // position (Net/ClientPosition.cs Pin), so the device doesn't correct toward the host's word: only an error past
        // DivergeUnits that lasts DivergeFrames snapshots running sets the player onto the host's pose
        private static bool _fruityCorrect = false; // off 10-20: a phone test showed split positions
        public static bool FruityCorrect { get => _fruityCorrect || FruitySync.On; set => _fruityCorrect = value; }
        public const float DivergeUnits = 30f;
        private const int DivergeFrames = 60;
        private int _diverged;
        public int Diverges { get; private set; }
        // knockback (2026-10-19, match PA26AE: each hit left the target shaking back and forth
        // for a long time): the host pushes the seat's speed and this device never gets that speed, so its
        // reported positions pulled the host back against its own slide, a round trip late each time. When the host's speed
        // at the compared input is this far (side to side) from this device's, the host is moving the seat itself (a push, a
        // pad): no position reports go up until HoldFrames after the last such gap, so the host's slide runs as it does with
        // reports off, and this device follows it by the position corrections alone
        private const float PushGap = 0.08f;
        private const int HoldFrames = 30;
        private int _holdUntil = -1;
        public bool HoldReports => !FruitySync.On && _newest < _holdUntil; // (Fruity: every report goes up)
        public int Holds { get; private set; }
        // a host that hands this device its pushes (0.3.6, as Fruity Prime does it: SeatExtras' push section) doesn't push
        // its own copy; it takes this device's speed with its reports instead. A push this device ran itself opens a window
        // in which a speed gap isn't the host moving the seat, so it holds no reports (the host's copy follows them)
        private const int GuestPushFrames = 150;
        // and in that window a like-for-like error under this is left alone: the host's copy follows this device's reports
        // then, and one off is an input that reached it without its report (jitter reordered the packet: the input came as a
        // later packet's spare copy), stepped on the host's own speed; the next report puts it right again, so correcting it
        // here only to correct back a snapshot later set the slide wobbling (PC test, 200 ms + 50 jitter: 4-7 turns a hit)
        private const float GuestPushDeadZone = 1f;
        public int GuestPushIgnored { get; private set; }
        private int _guestPushUntil = -1;
        public int GuestPushes { get; private set; }
        public static bool ClientPushes { get; set; } = true; // (off = say nothing of it: the PC test compares, clientpush=0)
        public void GuestPush()
        {
            _guestPushUntil = _newest + GuestPushFrames;
            GuestPushes++;
        }

        private struct Entry
        {
            public int Seq;
            public Vector3 Position;
            public Vector3 Speed;
            public int InputKey;
        }

        private readonly Entry[] _history = new Entry[Size];
        private int _validFrom; // inputs before this one don't count (the pose was set outright after them)
        private Vector3 _pending;
        // the error still to go in over the next frames (the player's pose plus this = where the prediction keeps it)
        public Vector3 Pending => _pending;

        // for the tests and the status line
        public int Corrections { get; private set; }
        public int Skipped { get; private set; }
        public int Restarts { get; private set; }
        public float ErrorSum { get; private set; }
        public float ErrorMax { get; private set; }

        public ClientPrediction()
        {
            for (int i = 0; i < Size; i++)
            {
                _history[i].Seq = -1;
            }
        }

        // the host set the pose outright (respawn, a big drift): nothing remembered before `nextSeq` compares any more
        public void Restart(int nextSeq)
        {
            _validFrom = nextSeq;
            _pending = Vector3.Zero;
            Restarts++;
        }

        // a snapshot was applied: compare the host's pose of the local seat at its acked input with this device's own.
        // Compared = the like-for-like check was made (small errors are folded in); Snap = this device's player is further
        // from the host's word than SnapError even like for like, so the caller sets it onto the host's pose; NotCompared =
        // no check could be made (no ack, held frames that differ, history too old), so the caller can only judge by the raw pose
        public ReconcileResult Reconcile(Snapshot snap, int local, PlayerEntity me)
        {
            if (snap.InputAck < 0 || local >= snap.Seats.Length || me.Health == 0)
            {
                return ReconcileResult.NotCompared;
            }
            ref PlayerNetState s = ref snap.Seats[local];
            if ((s.Flags & PlayerNetState.Spawned) == 0 || s.Health == 0)
            {
                return ReconcileResult.NotCompared;
            }
            // the acked 16-bit sequence -> the full one remembered
            int newest = NewestSeq();
            if (newest < 0)
            {
                return ReconcileResult.NotCompared;
            }
            int ack = newest + (short)(snap.InputAck - (ushort)newest);
            int compare = ack + snap.InputHeld;
            if (ack < _validFrom || compare > newest || newest - ack >= Size)
            {
                Skipped++;
                return ReconcileResult.NotCompared;
            }
            ref Entry acked = ref _history[ack & (Size - 1)];
            ref Entry at = ref _history[compare & (Size - 1)];
            if (acked.Seq != ack || at.Seq != compare)
            {
                Skipped++;
                return ReconcileResult.NotCompared;
            }
            // held frames: the host ran the acked input again; that matches here only if this device's inputs were the same
            for (int seq = ack + 1; seq <= compare; seq++)
            {
                if (_history[seq & (Size - 1)].InputKey != acked.InputKey)
                {
                    Skipped++;
                    return ReconcileResult.NotCompared;
                }
            }
            float gapX = s.Speed.X - at.Speed.X, gapZ = s.Speed.Z - at.Speed.Z;
            if (gapX * gapX + gapZ * gapZ > PushGap * PushGap && _newest >= _guestPushUntil)
            {
                if (!HoldReports)
                {
                    Holds++;
                }
                _holdUntil = newest + HoldFrames;
            }
            Vector3 error = s.Position - at.Position;
            // (a speed error was taken from a round trip ago and added to the speed of now: at 200 ms that fed back on itself
            // and set a flying player correcting at every snapshot; the position error alone carries the correction, the
            // speed follows from the same inputs. SpeedFix=false = the old behaviour, for the PC test's comparison)
            Vector3 speedError = SpeedFix ? Vector3.Zero : s.Speed - at.Speed;
            if (me.IsAltForm && AltHeightFix)
            {
                speedError.Y = 0;
                if (MathF.Abs(error.Y) < AltHeightDeadZone)
                {
                    error.Y = 0;
                }
            }
            float length = error.Length;
            if (FruityCorrect)
            {
                if (length > DivergeUnits && ++_diverged >= DivergeFrames)
                {
                    _diverged = 0;
                    Diverges++;
                    return ReconcileResult.Snap;
                }
                if (length <= DivergeUnits)
                {
                    _diverged = 0;
                }
                return ReconcileResult.Compared;
            }
            if (length < DeadZone)
            {
                return ReconcileResult.Compared;
            }
            if (_newest < _guestPushUntil && length < GuestPushDeadZone)
            {
                GuestPushIgnored++;
                return ReconcileResult.Compared;
            }
            // far off even like for like (a respawn, a wall taken differently): the host's pose wins, as it always did
            if (length > SnapError && SnapFix)
            {
                return ReconcileResult.Snap;
            }
            for (int seq = compare + 1; seq <= newest; seq++)
            {
                ref Entry e = ref _history[seq & (Size - 1)];
                if (e.Seq == seq)
                {
                    e.Position += error;
                    e.Speed += speedError;
                }
            }
            _pending += error;
            me.NetCorrect(Vector3.Zero, speedError);
            Corrections++;
            ErrorSum += length;
            ErrorMax = MathF.Max(ErrorMax, length);
            return ReconcileResult.Compared;
        }

        // before each step: part of the error goes in (all of it when it's big or nearly done)
        public void PreStep(PlayerEntity me)
        {
            if (_pending == Vector3.Zero)
            {
                return;
            }
            float length = _pending.Length;
            Vector3 move = length > SnapNow || length < 0.02f ? _pending : _pending * BlendPerFrame;
            _pending -= move;
            me.NetCorrect(move, Vector3.Zero);
        }

        // after each step: where this device's player is once the error still to come is in, after input `seq`
        public void Record(int seq, PlayerEntity me, int inputKey)
        {
            ref Entry e = ref _history[seq & (Size - 1)];
            e.Seq = seq;
            e.Position = me.Position + _pending;
            e.Speed = me.Speed;
            e.InputKey = inputKey;
            _newest = seq;
        }

        private int _newest = -1;

        private int NewestSeq() => _newest;

        // what a held input must equal to count: the buttons, the move and the view
        public static int KeyOf(in NetInput input) => HashCode.Combine(input.Buttons, input.MoveX, input.MoveY, input.SelectWeapon,
            (int)MathF.Round(input.Yaw * 100), (int)MathF.Round(input.Pitch * 100), HashCode.Combine(input.Touching, input.TouchDx, input.TouchDy));

        public string Summary() => $"prediction: {Corrections} corrections (mean {(Corrections > 0 ? ErrorSum / Corrections : 0):0.000}, "
            + $"max {ErrorMax:0.00}), {Skipped} skipped, {Restarts} restarts, {Holds} report holds, {Diverges} divergence snaps, {GuestPushes} pushes run here ({GuestPushIgnored} small errors left in them)";
    }
}
