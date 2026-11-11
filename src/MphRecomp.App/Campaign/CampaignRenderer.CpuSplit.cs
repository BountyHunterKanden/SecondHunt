using System;
using System.Diagnostics;

namespace MphRecomp.App;

// GL-thread CPU time and managed allocation per frame section, one MPHPerf "cpusplit" line a second (Mining Plaza frame
// dips, 2026-11-01): MPHPerf's pace line has sim + draw only, and the draw half held ~5 ms nobody could place.
// Sections: pre (input, menus, audio), sim (the steps), post (talk, gear, movie), clear (Render up to glClear's return: a
// frame waiting on the swap chain's next buffer waits here), setup (uniforms, quads, gun/body/skins setup), then the GPU
// timer's passes (opaque ... hud, CampaignRenderer.GpuMark), overlays (ship/map/replay bar/menu + the GPU timer's
// readback), tail (status line, eviction) and simlooks (a step's app-side looks after the host's step: viewmodel, smooth
// frames, suit, skins). Dev only: launch arg cpusplit 1 (MatchReplayBench.ConfigureBench).
internal sealed partial class CampaignRenderer
{
    bool _cpuSplit;
    const int CpuSegs = 16, CpuSegClear = 3, CpuSegSetup = 4, CpuSegOverlays = 13, CpuSegTail = 14, CpuSegSimLooks = 15;
    static readonly string[] CpuSegNames = { "pre", "sim", "post", "clear", "setup", "opaque", "decal", "stencil", "depth",
        "behind", "front", "suit", "hud", "overlays", "tail", "simlooks" };
    readonly long[] _cpuTicks = new long[CpuSegs], _cpuAlloc = new long[CpuSegs];
    int _cpuCur = -1, _cpuFrames, _cpuSecond = -1, _cpuClearSlow;
    long _cpuMarkTick, _cpuMarkAlloc, _cpuFrameClear;
    double _cpuClearMax;

    // closes the running section and starts seg (-1: none)
    void CpuMark(int seg)
    {
        if (!_cpuSplit) return;
        long t = Stopwatch.GetTimestamp(), a = GC.GetAllocatedBytesForCurrentThread();
        if (_cpuCur >= 0)
        {
            _cpuTicks[_cpuCur] += t - _cpuMarkTick;
            _cpuAlloc[_cpuCur] += a - _cpuMarkAlloc;
            if (_cpuCur == CpuSegClear) _cpuFrameClear += t - _cpuMarkTick;
        }
        _cpuCur = seg;
        _cpuMarkTick = t;
        _cpuMarkAlloc = a;
    }

    void CpuFrameEnd()
    {
        if (!_cpuSplit) return;
        CpuMark(-1);
        double clearMs = _cpuFrameClear * 1000.0 / Stopwatch.Frequency;
        _cpuFrameClear = 0;
        _cpuFrames++;
        if (clearMs > 2) _cpuClearSlow++;
        _cpuClearMax = Math.Max(_cpuClearMax, clearMs);
        int second = (int)_clock.Elapsed.TotalSeconds;
        if (second == _cpuSecond) return;
        if (_cpuSecond >= 0 && _cpuFrames > 0)
        {
            var sb = new System.Text.StringBuilder(400);
            sb.Append("cpusplit room ").Append(_host?.RoomId ?? -1).Append(": ").Append(_cpuFrames).Append(" frames, ms/frame");
            double total = 0;
            for (int i = 0; i < CpuSegs; i++)
            {
                double ms = _cpuTicks[i] * 1000.0 / Stopwatch.Frequency / _cpuFrames;
                total += ms;
                sb.Append(' ').Append(CpuSegNames[i]).Append(' ').Append(ms.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture));
            }
            sb.Append(" = ").Append(total.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture));
            sb.Append("; clear >2 ms ").Append(_cpuClearSlow).Append(" max ").Append(_cpuClearMax.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture));
            sb.Append("; alloc KB/s");
            for (int i = 0; i < CpuSegs; i++)
            {
                if (_cpuAlloc[i] >= 1024) sb.Append(' ').Append(CpuSegNames[i]).Append(' ').Append(_cpuAlloc[i] / 1024);
            }
            Log.Info("MPHPerf", sb.ToString());
        }
        _cpuSecond = second;
        _cpuFrames = _cpuClearSlow = 0;
        _cpuClearMax = 0;
        Array.Clear(_cpuTicks);
        Array.Clear(_cpuAlloc);
    }
}
