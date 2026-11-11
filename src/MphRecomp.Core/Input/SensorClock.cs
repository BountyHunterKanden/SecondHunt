using System;

// A controller's sample times moved onto the local clock, so PacedSum can pace them (owner 2026-11-09: PC pad gyro
// "really shakey like we already solved for on odin" -- the samples had no times, so every step took whatever the last
// poll had read). Pads stamp samples on their own count: SDL's Switch driver numbers them from 0 at the rate it
// measures, a DualSense on its own timer. Both are evenly spaced, while their arrival is not (Bluetooth packets of
// three, read once a display frame). The offset aims at the smallest (arrival - sample time) of the last tenth of a
// second or so: the sample read soonest after it was made. A minimum over a window, not of all time, so a count that falls
// behind the clock (a lost packet, a rate estimate a little off) catches up. It moves there slowly (a step jumps by a
// few milliseconds' worth of motion otherwise), except a long way down (the count running fast, as SDL's does until it
// has measured the rate: 5 ms a sample against the pad's ~3.6), and starts over after a big jump up (the sensors paused).
namespace MphRecomp.Input
{
    public sealed class SensorClock
    {
        const long WindowNs = 50_000_000; // the minimum is over this window and the one before it (5-10 packets)
        const long SlewNsPerSecond = 30_000_000; // how fast the offset moves to it (3% of the motion's speed)
        const long SnapDownNs = 20_000_000; // this far above it, the offset goes straight there
        const long SettleNs = 500_000_000; // the first half second, it goes straight down to it (it starts at the first sample)
        const long JumpNs = 250_000_000; // read this much later than the offset says: a pause, not a late packet

        private long _startedNs, _windowStartNs, _minNs, _prevMinNs, _offsetNs, _arrivedNs;
        private bool _set;

        // sampleNs on the pad's count, read at arrivedNs on the local clock: the sample's time on the local clock
        public long ToLocal(long sampleNs, long arrivedNs)
        {
            long c = arrivedNs - sampleNs;
            if (!_set || c > _offsetNs + JumpNs)
            {
                _set = true;
                _startedNs = _windowStartNs = _arrivedNs = arrivedNs;
                _minNs = _prevMinNs = _offsetNs = c;
                return sampleNs + _offsetNs;
            }
            if (arrivedNs - _windowStartNs >= WindowNs)
            {
                _windowStartNs = arrivedNs;
                _prevMinNs = _minNs;
                _minNs = c;
            }
            else
            {
                _minNs = Math.Min(_minNs, c);
            }
            long target = Math.Min(_minNs, _prevMinNs);
            if (_offsetNs - target > SnapDownNs || arrivedNs - _startedNs < SettleNs)
            {
                _offsetNs = target;
            }
            else if (arrivedNs > _arrivedNs)
            {
                long step = Math.Min(arrivedNs - _arrivedNs, 1_000_000_000) * SlewNsPerSecond / 1_000_000_000;
                _offsetNs = target > _offsetNs ? Math.Min(target, _offsetNs + step) : Math.Max(target, _offsetNs - step);
            }
            _arrivedNs = Math.Max(_arrivedNs, arrivedNs);
            return sampleNs + _offsetNs;
        }

        public void Reset() => _set = false;
    }
}
