using System;
using MphRead;
using MphRead.Entities;
using OpenTK.Mathematics;

namespace MphRecomp.Multiplayer.Net
{
    // N4 (O5): the online host tests a remote player's shots against where the targets stood in the frame that player was
    // looking at, not where they stand on the host now (a round trip plus the input buffer later). Each input carries the
    // host frame its view showed (NetInput.ViewTick: the newest snapshot + the frames stepped since); the host's rewind for
    // that seat is its frame minus that, capped at MaxRewind (45 frames; was 30 before the hit-claim work), set in CampaignHost.SetRemoteInput so replays redo it
    // (the host records each input with its view already capped: a replay rewinds the same under any cap).
    // This keeps each seat's position at the start of every host frame (= what that frame's snapshot showed); a target that
    // respawned or teleported since (NetAimEpoch changed) or wasn't up then is tested where it is.
    // MphRead's beam (BeamProjectileEntity) asks through PlayerEntity.HostLagShift; with no rewind it's the same test as ever.
    public sealed class LagCompensation
    {
        // frames (500 ms, 2026-10-19; was 12 = 200 ms, 2026-10-09, and 9 before that). The rewind a shot needs is the
        // round trip plus its input's wait in the host's queue (up to MaxBacklog, re-measured each match) plus the client's
        // own frame, so a player with a deep queue went past 200 ms at any ping and had shots tested against targets that had
        // moved on (live report: point-blank hits not landing, a different player each match; two other MphRead online forks
        // measured most shots clamped at 24 frames and use 45). The PC tests may change it (-onlinetest maxrewind=)
        public static int MaxRewind { get; set; } = 45;
        private const int Size = 64; // (the pose ring: past MaxRewind)

        private struct Entry
        {
            public long Frame;
            public Vector3 Position;
            public int Epoch;
            public bool Up;
        }

        private Entry[,] _hist = new Entry[Size, 4];
        private long _frame = -1;

        // shots tested against an older pose (and by how much in all), for the PC tests
        public int Shifted { get; private set; }

        // the host's frame `frame` is about to run: every seat as it stands now
        public void Record(long frame)
        {
            int seats = PlayerEntity.MaxPlayers;
            if (_hist.GetLength(1) < seats)
            {
                _hist = new Entry[Size, seats];
            }
            int slot = (int)(frame % Size);
            for (int i = 0; i < seats; i++)
            {
                PlayerEntity p = PlayerEntity.Players[i];
                _hist[slot, i] = new Entry
                {
                    Frame = frame,
                    Position = p.Position,
                    Epoch = p.NetAimEpoch,
                    Up = p.Health > 0 && p.LoadFlags.TestFlag(LoadFlags.Spawned)
                };
            }
            _frame = frame;
        }

        // where `target` stood when `shooter` saw it, minus where it stands now (zero when that isn't known)
        public Vector3 Shift(PlayerEntity shooter, PlayerEntity target)
        {
            int rewind = Math.Min(shooter.HostLagRewind, MaxRewind);
            int seat = target.SlotIndex;
            if (rewind <= 0 || _frame < 0 || seat < 0 || seat >= _hist.GetLength(1))
            {
                return Vector3.Zero;
            }
            long frame = _frame - rewind;
            ref Entry e = ref _hist[(int)(frame % Size), seat];
            if (frame < 0 || e.Frame != frame || !e.Up || e.Epoch != target.NetAimEpoch)
            {
                return Vector3.Zero;
            }
            Vector3 shift = e.Position - target.Position;
            if (shift != Vector3.Zero)
            {
                Shifted++;
            }
            return shift;
        }

        // where `seat` stood at the start of host frame `frame`, when still kept (the shot check: Net/ShotCheck.cs)
        public bool TryPositionAt(int seat, long frame, out Vector3 position)
        {
            position = default;
            if (frame < 0 || frame > _frame || seat < 0 || seat >= _hist.GetLength(1)) return false;
            ref Entry e = ref _hist[(int)(frame % Size), seat];
            if (e.Frame != frame || !e.Up) return false;
            position = e.Position;
            return true;
        }

        // the same, with the seat's aim epoch then (a respawn or teleport since changes it: Net/HitClaims.cs)
        public bool TryPoseAt(int seat, long frame, out Vector3 position, out int epoch)
        {
            epoch = 0;
            if (!TryPositionAt(seat, frame, out position)) return false;
            epoch = _hist[(int)(frame % Size), seat].Epoch;
            return true;
        }

        // the rewind for an input whose view showed host frame viewTick, applied at host frame `frame` (0 = no view: LAN)
        public static int RewindFor(long frame, uint viewTick)
        {
            return viewTick == 0 ? 0 : (int)Math.Clamp(frame - viewTick, 0, MaxRewind);
        }
    }
}
