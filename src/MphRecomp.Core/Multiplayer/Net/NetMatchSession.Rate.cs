using System;

namespace MphRecomp.Multiplayer.Net
{
    // How often each joiner gets a snapshot (0.3.6: 60 Hz snapshots). A host sends every joiner one each
    // SnapshotIntervalNormal frames: 1 (60 Hz) by default; a match host takes it from the server (SecondHunt.MatchHost
    // --snapshot-hz, the server's --served-snapshot-hz), so a served match can go back to 30 Hz with a server deploy.
    // A joiner on a struggling link drops on its own: 30 Hz on a middling one, 20 Hz on a weak one, measured per 2 s
    // window of its inputs (CountPacket), with a margin between the bands so it doesn't flip back and forth. Nothing on
    // the guest counts on the interval: its smoothing measures the gap between snapshots (RemoteSmoothing), its view tick
    // counts frames between them, and its snapshot-counted waits count frames online (PlayerEntity.NetApplyTicks).
    public sealed partial class NetMatchSession
    {
        private const int SnapshotIntervalDefault = 1, SnapshotIntervalMid = 2, SnapshotIntervalWeak = 3;

        // host: frames between snapshots to a joiner on a good link (1 = 60 Hz, 2 = 30 Hz, 3 = 20 Hz); set before SendStart
        public int SnapshotIntervalNormal
        {
            get => _snapshotInterval;
            set => _snapshotInterval = Math.Clamp(value, 1, SnapshotIntervalWeak);
        }
        private int _snapshotInterval = SnapshotIntervalDefault;

        // the frames between snapshots for this joiner after a 2 s window: 20 Hz past 8 % loss, at least 30 Hz past 4 %, back to
        // the normal rate under 2 % (to 30 Hz from 20 under 3 %); in between, as it was. Keyed on loss, not on ping
        // (2026-10-19): a long but clean link carries 60 Hz as well as a short one, and the measured round trip reads ~60 ms
        // over the wire time (an input goes out once a frame), so the old 180/200/250 ms bands held a ~200 ms Japan link at
        // 20 Hz. Only a round trip past RttWeakMs (a link queueing up behind what it is sent) still drops the rate.
        // Never faster than SnapshotIntervalNormal.
        private const int RttWeakMs = 450, RttMidMs = 400, RttBackMs = 350;

        private int RateFor(Joiner j)
        {
            int want = j.Loss > 0.08f || j.RttMs > RttWeakMs ? SnapshotIntervalWeak
                : j.Loss > 0.04f || j.RttMs > RttMidMs ? Math.Max(j.Interval, SnapshotIntervalMid)
                : j.Loss < 0.02f && j.RttMs < RttBackMs ? SnapshotIntervalNormal
                : j.Loss < 0.03f && j.RttMs < RttMidMs ? Math.Min(j.Interval, SnapshotIntervalMid)
                : j.Interval;
            return Math.Max(want, SnapshotIntervalNormal);
        }
    }
}
