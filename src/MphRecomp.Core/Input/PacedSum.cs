using System;
using System.Numerics;

// Motion that arrives in bursts (a gyroscope's sample batches, a touch screen's per-frame reports), handed to the 60 Hz
// simulation one step's worth at a time. "Whatever arrived since the last step" gave a steady turn uneven steps -- a
// display frame can run two steps back to back, and deliveries don't line up with the steps -- which looked shaky next
// to the stick (owner 2026-11-02: "gyro aim is for some reason shakey while stick aim is smooth"). Each add carries its
// own time; a paced take hands over the motion up to the step's own time, less how far behind the source has recently
// been delivering, and keeps the rest for the next step. Not thread-safe: the owner locks.
namespace MphRecomp.Input
{
    public sealed class PacedSum
    {
        // never hold motion back more than 30 ms. A PC's Bluetooth pad gets more (owner 2026-11-09, 8BitDo on a 60 Hz
        // monitor: "jumps around or feels jittery when moving left to right"): its packets come 0-70 ms late, in
        // spells of a second or so, and past the cap each late burst landed in one step
        public const long DefaultLagCapNs = 30_000_000;
        const long IdleNs = 50_000_000; // nothing added for this long: the source is idle (a finger held still), not late
        private readonly long _lagCapNs, _idleNs; // (idle: past the cap, so a late burst isn't taken for a rest)

        public PacedSum(long lagCapNs = DefaultLagCapNs)
        {
            _lagCapNs = lagCapNs;
            _idleNs = Math.Max(IdleNs, lagCapNs + 20_000_000);
        }
        const long ClockSlackNs = 500_000_000; // times this far from the clock: not the same clock

        private readonly long[] _ns = new long[128];
        private readonly Vector2[] _at = new Vector2[128]; // the pending sum up to each time, oldest first
        private int _start, _count;
        private long _takenNs; // the time the last paced take ran up to
        private float _lagNs; // how far behind the clock the newest add has recently been (a slowly decaying max)
        private long _seenNs, _waitNs; // the newest add the takes have seen, and how long they've waited past it since

        public Vector2 Pending { get; private set; }

        // motion that ended at timeNs (0 = no time: a paced take then hands over everything)
        public void Add(Vector2 v, long timeNs)
        {
            Pending += v;
            if (timeNs <= 0) return;
            if (_count == _ns.Length)
            {
                _start = (_start + 1) % _ns.Length;
                _count--;
            }
            int i = (_start + _count) % _ns.Length;
            _ns[i] = timeNs;
            _at[i] = Pending;
            _count++;
        }

        // Everything pending, and starts over.
        public Vector2 TakeAll()
        {
            Vector2 d = Pending;
            Pending = Vector2.Zero;
            if (_count > 0) _takenNs = Math.Max(_takenNs, _ns[(_start + _count - 1) % _ns.Length]);
            _count = 0;
            return d;
        }

        // The motion up to (nowNs - behindNs - the recent delivery lag), on the adds' own clock; behindNs: how far before
        // now this step's time is (the frame's catch-up still to run). Everything when there are no times or they're on
        // another clock.
        public Vector2 TakePaced(long nowNs, long behindNs)
        {
            if (nowNs <= 0) return TakeAll();
            if (_count == 0)
            {
                long w = nowNs - _seenNs; // (waiting with everything taken)
                if (_seenNs > 0 && w >= 0 && w <= ClockSlackNs) _waitNs = Math.Max(_waitNs, w);
                return Vector2.Zero;
            }
            long newest = _ns[(_start + _count - 1) % _ns.Length];
            long lag = nowNs - newest;
            if (lag < -ClockSlackNs || lag > ClockSlackNs) return TakeAll();
            // a wait is lateness only if the motion then goes on from where it stopped: a finger that rested and moves
            // again leaves a gap in the times, and the rest mustn't be learned as lateness (it'd hold the next drag back)
            if (newest != _seenNs)
            {
                long firstNew = newest;
                for (int k = _count - 1; k >= 0 && _ns[(_start + k) % _ns.Length] > _seenNs; k--) firstNew = _ns[(_start + k) % _ns.Length];
                if (_seenNs > 0 && (firstNew - _seenNs) * 2 <= _waitNs) Learn(_waitNs);
                _seenNs = newest;
                _waitNs = 0;
                Learn(lag); // just delivered: how late it came
            }
            else
            {
                _waitNs = Math.Max(_waitNs, lag);
            }
            // forgets slowly (half in ~6 s at 60 takes a second): a lag that wandered with each burst would move the
            // steps' times about and bring the unevenness back
            _lagNs *= 0.998f;
            if (lag > _idleNs) return TakeAll(); // idle (a finger held still), not late
            long target = nowNs - Math.Max(behindNs, 0) - (long)_lagNs;
            if (target < _takenNs) target = _takenNs;
            if (target >= newest) return TakeAll();
            // the pending sum at target: between the adds either side of it
            Vector2 at = Vector2.Zero, prevAt = Vector2.Zero;
            long prevNs = _takenNs;
            int keep = _count;
            for (int k = 0; k < _count; k++)
            {
                int i = (_start + k) % _ns.Length;
                long t = _ns[i];
                if (t <= target)
                {
                    prevNs = t;
                    prevAt = at = _at[i];
                    keep = _count - k - 1;
                    continue;
                }
                float f = t > prevNs ? Math.Clamp((float)(target - prevNs) / (t - prevNs), 0f, 1f) : 1f;
                at = Vector2.Lerp(prevAt, _at[i], f);
                break;
            }
            // what's left is pending from here on
            _start = (_start + _count - keep) % _ns.Length;
            _count = keep;
            for (int k = 0; k < _count; k++) _at[(_start + k) % _ns.Length] -= at;
            Pending -= at;
            _takenNs = target;
            return at;
        }

        void Learn(long lagNs) => _lagNs = Math.Max(_lagNs, Math.Clamp(lagNs, 0, _lagCapNs));

        public void Clear()
        {
            Pending = Vector2.Zero;
            _count = 0;
            _takenNs = 0;
            _lagNs = 0;
            _seenNs = _waitNs = 0;
        }
    }
}
