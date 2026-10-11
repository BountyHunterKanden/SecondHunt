using System;
using MphRead;
using MphRead.Entities;
using OpenTK.Mathematics;

namespace MphRecomp.Multiplayer.Net
{
    // N4 (O5): the online host tests a remote player's shots against where the targets stood in the frame that player was
    // looking at, not where they stand on the host now (a round trip plus the input buffer later). Each input carries the
    // host frame its view showed (NetInput.ViewTick: the newest snapshot + the frames stepped since); the host's rewind for
    // that seat is its frame minus that, capped at MaxRewind (200 ms), set in CampaignHost.SetRemoteInput so replays redo it
    // (the host records each input with its view already capped: a replay rewinds the same under any cap).
    // This keeps each seat's position at the start of every host frame (= what that frame's snapshot showed); a target that
    // respawned or teleported since (NetAimEpoch changed) or wasn't up then is tested where it is.
    // MphRead's beam (BeamProjectileEntity) asks through PlayerEntity.HostLagShift; with no rewind it's the same test as ever.
    public sealed class LagCompensation
    {
        // frames (200 ms: covers 150 ms ping fully, owner 2026-10-09; was 9 = 150 ms, ~80% of hits); the PC tests may
        // change it (-onlinetest maxrewind=) to see how far the views really lag
        public static int MaxRewind { get; set; } = 12;
        private const int Size = 32;

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

        // the rewind for an input whose view showed host frame viewTick, applied at host frame `frame` (0 = no view: LAN)
        public static int RewindFor(long frame, uint viewTick)
        {
            return viewTick == 0 ? 0 : (int)Math.Clamp(frame - viewTick, 0, MaxRewind);
        }
    }
}
