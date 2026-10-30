using System;

namespace MphRecomp.App;

// Frame-time totals for perf runs (thread B, PREPARE REPLAYS OFF vs DURING MATCH on the Odin): every frame TracePace
// counts (not paused, no ship / movie) goes into a 0.25 ms histogram, and every 10 s one cumulative MPHFrameStats line
// gives frames, avg, p50 / p99, worst and how many took over 16.7 ms since the process started (`adb logcat -s MPHFrameStats`).
internal static class FrameStats
{
    const double Bin = 0.25;
    static readonly long[] _bins = new long[800]; // to 200 ms; slower frames go in the last bin (and in _worst)
    static long _n, _over16;
    static double _sum, _worst, _nextLog = 10;

    public static void Add(double intervalMs, double nowSeconds)
    {
        _n++;
        _sum += intervalMs;
        if (intervalMs > 16.7) _over16++;
        _worst = Math.Max(_worst, intervalMs);
        _bins[Math.Clamp((int)(intervalMs / Bin), 0, _bins.Length - 1)]++;
        if (nowSeconds < _nextLog) return;
        _nextLog = nowSeconds + 10;
        Log.Info("MPHFrameStats", $"t {nowSeconds:0} s: {_n} frames, avg {_sum / _n:0.00} ms, p50 {Percentile(0.5):0.00}, "
            + $"p99 {Percentile(0.99):0.00}, worst {_worst:0.0}, over 16.7 ms {_over16}");
    }

    static double Percentile(double p)
    {
        long want = (long)Math.Ceiling(_n * p), seen = 0;
        for (int i = 0; i < _bins.Length; i++)
        {
            seen += _bins[i];
            if (seen >= want) return (i + 1) * Bin;
        }
        return _worst;
    }
}
