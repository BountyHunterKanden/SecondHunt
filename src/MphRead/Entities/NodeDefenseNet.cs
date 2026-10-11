namespace MphRead.Entities
{
    // Network play (MphRecomp's multiplayer host). Who stands on a node is the host's call: the host records each
    // player's presence as it tests it, a client tests the host's bits instead of its own (PlayerEntity.NetClient), runs
    // the same logic on them (so captures, sounds and messages happen there too) and takes the host's state each snapshot.
    public partial class NodeDefenseEntity
    {
        public struct NetState
        {
            public ushort Id;
            public byte Presence; // bit per slot: alive and inside the volume
            public sbyte CurrentTeam;
            public sbyte OccupyingTeam;
            public byte OccupiedBy; // bit per slot
            public bool Contested;
            public bool InProgress;
            public float Progress;
            public float ScoreTimer;
            public sbyte CapturedPlayer;
        }

        // (PC net tests: every device should count the same)
        public static int NetCompletes { get; private set; }
        private byte _netPresence = 0;
        private bool _netHostPresence = false;

        private bool IsPresent(PlayerEntity player)
        {
            int bit = 1 << player.SlotIndex;
            if (PlayerEntity.NetClient && _netHostPresence)
            {
                return (_netPresence & bit) != 0;
            }
            bool present = player.Health > 0 && _volume.TestPoint(player.Volume.SpherePosition);
            _netPresence = (byte)(present ? _netPresence | bit : _netPresence & ~bit);
            return present;
        }

        // host: after the frame
        public NetState NetCapture()
        {
            byte occupiedBy = 0;
            for (int i = 0; i < PlayerEntity.SlotCapacity; i++)
            {
                if (_occupiedBy[i])
                {
                    occupiedBy |= (byte)(1 << i);
                }
            }
            return new NetState
            {
                Id = (ushort)Id,
                Presence = _netPresence,
                CurrentTeam = (sbyte)_currentTeam,
                OccupyingTeam = (sbyte)_occupyingTeam,
                OccupiedBy = occupiedBy,
                Contested = _contested,
                InProgress = _inProgress,
                Progress = _progress,
                ScoreTimer = _scoreTimer,
                CapturedPlayer = (sbyte)(_capturedPlayer?.SlotIndex ?? -1)
            };
        }

        // client: the host's presence for the next frames and its state after the frame it sent
        public void NetApply(in NetState state)
        {
            _netPresence = state.Presence;
            _netHostPresence = true;
            _currentTeam = state.CurrentTeam;
            _occupyingTeam = state.OccupyingTeam;
            for (int i = 0; i < PlayerEntity.SlotCapacity; i++)
            {
                _occupiedBy[i] = (state.OccupiedBy & (1 << i)) != 0;
            }
            _contested = state.Contested;
            _inProgress = state.InProgress;
            _progress = state.Progress;
            _scoreTimer = state.ScoreTimer;
            int captured = state.CapturedPlayer;
            _capturedPlayer = captured >= 0 && captured < PlayerEntity.MaxPlayers ? PlayerEntity.Players[captured] : null;
        }
    }
}
