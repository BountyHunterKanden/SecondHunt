using System;
using OpenTK.Mathematics;

namespace MphRead.Entities
{
    // Network play (MphRecomp's multiplayer host). The host decides who holds the octolith and when it drops, resets,
    // goes home or scores, and records each as an event; a client skips those checks (PlayerEntity.NetClient) and
    // replays the host's newest events from each snapshot through the same methods, so the vanilla sounds, messages
    // and music play there too, then takes the host's state.
    public partial class OctolithFlagEntity
    {
        public enum NetEventKind : byte
        {
            None,
            Pickup,
            Return,
            Drop,
            DropReset,
            Reset,
            Capture
        }

        public const int NetEventCount = 4;

        public struct NetState
        {
            public ushort Id;
            public sbyte Carrier;
            public sbyte LastCarrier;
            public bool AtBase;
            public bool Grounded;
            public Vector3 Position;
            public float ResetTimer;
            public float Gravity;
            public ushort EventSeq;
            // the last NetEventCount events, oldest first (Events[^1] is event EventSeq)
            public NetEventKind[] EventKinds;
            public sbyte[] EventSlots;
        }

        private ushort _netEventSeq = 0;
        private readonly NetEventKind[] _netEventKinds = new NetEventKind[NetEventCount];
        private readonly sbyte[] _netEventSlots = new sbyte[NetEventCount];
        private ushort _netAppliedSeq = 0;
        private bool _netSynced = false;
        // (PC net tests: every device should count the same)
        public static int NetEventsRecorded { get; private set; }
        public static int NetEventsPlayed { get; private set; }

        private void NetEvent(NetEventKind kind, PlayerEntity? player)
        {
            _netEventSeq++;
            if (!PlayerEntity.NetClient)
            {
                NetEventsRecorded++;
            }
            _netEventKinds[_netEventSeq % NetEventCount] = kind;
            _netEventSlots[_netEventSeq % NetEventCount] = (sbyte)(player?.SlotIndex ?? -1);
        }

        // host: after the frame
        public NetState NetCapture()
        {
            var state = new NetState
            {
                Id = (ushort)Id,
                Carrier = (sbyte)(_carrier?.SlotIndex ?? -1),
                LastCarrier = (sbyte)(_lastCarrier?.SlotIndex ?? -1),
                AtBase = _atBase,
                Grounded = _grounded,
                Position = Position,
                ResetTimer = _resetTimer,
                Gravity = _gravity,
                EventSeq = _netEventSeq,
                EventKinds = new NetEventKind[NetEventCount],
                EventSlots = new sbyte[NetEventCount]
            };
            for (int i = 0; i < NetEventCount; i++)
            {
                int seq = _netEventSeq - (NetEventCount - 1) + i;
                if (seq <= 0)
                {
                    continue;
                }
                state.EventKinds[i] = _netEventKinds[seq % NetEventCount];
                state.EventSlots[i] = _netEventSlots[seq % NetEventCount];
            }
            return state;
        }

        // client: play the host's events since the last snapshot (when there are more than fit, only the state is
        // taken: stale sounds would mislead), then take the host's state
        public void NetApply(in NetState state)
        {
            if (_netSynced)
            {
                int missed = (ushort)(state.EventSeq - _netAppliedSeq);
                if (missed > 0 && missed <= NetEventCount)
                {
                    for (int i = NetEventCount - missed; i < NetEventCount; i++)
                    {
                        ApplyNetEvent(state.EventKinds[i], state.EventSlots[i]);
                    }
                }
            }
            _netSynced = true;
            _netAppliedSeq = state.EventSeq;
            PlayerEntity? carrier = SlotPlayer(state.Carrier);
            if (carrier != _carrier)
            {
                if (_carrier != null)
                {
                    _carrier.OctolithFlag = null;
                }
                _carrier = carrier;
                if (_carrier != null)
                {
                    _carrier.OctolithFlag = this;
                }
            }
            _lastCarrier = SlotPlayer(state.LastCarrier);
            _atBase = state.AtBase;
            _grounded = state.Grounded;
            Position = state.Position;
            _resetTimer = state.ResetTimer;
            _gravity = state.Gravity;
        }

        private void ApplyNetEvent(NetEventKind kind, sbyte slot)
        {
            PlayerEntity? player = SlotPlayer(slot);
            NetEventsPlayed++;
            switch (kind)
            {
            case NetEventKind.Pickup:
            case NetEventKind.Return:
                if (player != null && OnTouched(player))
                {
                    OnPickedUp();
                }
                break;
            case NetEventKind.Drop:
            case NetEventKind.DropReset:
                if (NetHold(player))
                {
                    OnDropped(reset: kind == NetEventKind.DropReset);
                }
                break;
            case NetEventKind.Reset:
                Reset();
                break;
            case NetEventKind.Capture:
                if (NetHold(player))
                {
                    OnCaptured();
                }
                break;
            }
        }

        // a drop or capture by a carrier this device never saw pick it up (a lost snapshot)
        private bool NetHold(PlayerEntity? player)
        {
            if (_carrier == null && player != null)
            {
                player.OctolithFlag = this;
                _carrier = player;
                _lastCarrier = player;
            }
            return _carrier != null;
        }

        private static PlayerEntity? SlotPlayer(sbyte slot)
        {
            return slot >= 0 && slot < PlayerEntity.MaxPlayers ? PlayerEntity.Players[slot] : null;
        }
    }
}
