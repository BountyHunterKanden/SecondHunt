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
    public sealed class ClientPrediction
    {
        private const int Size = 128; // inputs remembered (~2 s)
        private const float DeadZone = 0.01f; // float noise across devices
        private const float SnapNow = 2f; // errors this big go in at once
        private const float BlendPerFrame = 0.3f;

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

        // a snapshot was applied: compare the host's pose of the local seat at its acked input with this device's own
        public void Reconcile(Snapshot snap, int local, PlayerEntity me)
        {
            if (snap.InputAck < 0 || local >= snap.Seats.Length || me.Health == 0)
            {
                return;
            }
            ref PlayerNetState s = ref snap.Seats[local];
            if ((s.Flags & PlayerNetState.Spawned) == 0 || s.Health == 0)
            {
                return;
            }
            // the acked 16-bit sequence -> the full one remembered
            int newest = NewestSeq();
            if (newest < 0)
            {
                return;
            }
            int ack = newest + (short)(snap.InputAck - (ushort)newest);
            int compare = ack + snap.InputHeld;
            if (ack < _validFrom || compare > newest || newest - ack >= Size)
            {
                Skipped++;
                return;
            }
            ref Entry acked = ref _history[ack & (Size - 1)];
            ref Entry at = ref _history[compare & (Size - 1)];
            if (acked.Seq != ack || at.Seq != compare)
            {
                Skipped++;
                return;
            }
            // held frames: the host ran the acked input again; that matches here only if this device's inputs were the same
            for (int seq = ack + 1; seq <= compare; seq++)
            {
                if (_history[seq & (Size - 1)].InputKey != acked.InputKey)
                {
                    Skipped++;
                    return;
                }
            }
            Vector3 error = s.Position - at.Position;
            float length = error.Length;
            if (length < DeadZone)
            {
                return;
            }
            Vector3 speedError = s.Speed - at.Speed;
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
            + $"max {ErrorMax:0.00}), {Skipped} skipped, {Restarts} restarts";
    }
}
