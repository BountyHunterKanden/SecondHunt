using System;
using System.Collections.Generic;

namespace MphRecomp.Multiplayer.Net
{
    // A guest's own view of how its match's snapshots arrive: the gaps between them (a late snapshot is when remote players
    // freeze or snap, RemoteSmoothing.cs), their tick steps, the ones it couldn't rebuild, and its round trip as the host
    // measures it. Written to the client log (log.txt) every 30 s and as a summary when the match ends, so a bot or remote
    // desync report can be read against the link: "gaps over 100 ms: 40" says the link, not the sim, was late.
    //   net: 30s window: snaps 892 (29.7/s) avg gap 33 ms, max 412 ms; gaps >50 ms 31, >100 ms 6, >250 ms 1;
    //        tick step avg 2.0 max 14; stale 3, missing base 2; rtt 48 ms
    internal sealed class GuestNetStats
    {
        private struct Counts
        {
            public int Snaps, Over50, Over100, Over250, Stale, TickGaps;
            public long GapSum, MaxGap, TickGapSum, MaxTickGap;
            public long Bytes;
        }

        private const long ReportMs = 30_000;
        private Counts _window, _total;
        private long _matchStart, _windowStart, _lastArrival;
        private uint _lastTick;
        private int _missingSeen, _missingStart;
        private bool _active;

        // missingBase = the receiver's running count (it isn't cleared between matches)
        public void Reset(long now, int missingBase)
        {
            _window = default;
            _total = default;
            _matchStart = _windowStart = now;
            _lastArrival = 0;
            _lastTick = 0;
            _missingSeen = _missingStart = missingBase;
            _active = true;
        }

        // one snapshot rebuilt here (tick = the host's frame it holds; newest = it was newer than the last one kept)
        public void OnSnapshot(long now, uint tick, bool newest, int bytes)
        {
            if (!_active)
            {
                return;
            }
            Add(ref _window, now, tick, newest, bytes);
            Add(ref _total, now, tick, newest, bytes);
            if (newest)
            {
                _lastArrival = now;
                _lastTick = tick;
            }
        }

        private void Add(ref Counts c, long now, uint tick, bool newest, int bytes)
        {
            c.Snaps++;
            c.Bytes += bytes;
            if (!newest)
            {
                c.Stale++; // (an old or repeated tick: arrived out of order)
                return;
            }
            if (_lastArrival != 0)
            {
                long gap = now - _lastArrival;
                c.GapSum += gap;
                c.MaxGap = Math.Max(c.MaxGap, gap);
                if (gap > 50) c.Over50++;
                if (gap > 100) c.Over100++;
                if (gap > 250) c.Over250++;
            }
            if (_lastTick != 0 && tick > _lastTick)
            {
                long step = tick - _lastTick;
                c.TickGaps++;
                c.TickGapSum += step;
                c.MaxTickGap = Math.Max(c.MaxTickGap, step);
            }
        }

        // the periodic line (null until a window is due); missingBase = the receiver's running count, rttMs = -1 when unknown
        public string? Tick(long now, int missingBase, int rttMs)
        {
            if (!_active || now - _windowStart < ReportMs)
            {
                return null;
            }
            string line = Format("30s window", _window, now - _windowStart, missingBase - _missingSeen, rttMs);
            _missingSeen = missingBase;
            _window = default;
            _windowStart = now;
            return line;
        }

        // the match-end line (null when no match was counted); stops counting until the next Reset
        public string? Finish(long now, int missingBase, int rttMs)
        {
            if (!_active || _total.Snaps == 0)
            {
                _active = false;
                return null;
            }
            _active = false;
            return Format("match total", _total, now - _matchStart, missingBase - _missingStart, rttMs);
        }

        private static string Format(string label, in Counts c, long ms, int missing, int rttMs)
        {
            double secs = Math.Max(0.001, ms / 1000.0);
            int gaps = Math.Max(1, c.Snaps - c.Stale - 1);
            string rtt = rttMs >= 0 ? $"{rttMs} ms" : "n/a";
            return $"net: {label} ({secs:0}s): snaps {c.Snaps} ({c.Snaps / secs:0.0}/s, {c.Bytes / 1024.0:0.0} KB), "
                 + $"avg gap {c.GapSum / (double)gaps:0} ms, max {c.MaxGap} ms; gaps >50 ms {c.Over50}, >100 ms {c.Over100}, >250 ms {c.Over250}; "
                 + $"tick step avg {(c.TickGaps > 0 ? c.TickGapSum / (double)c.TickGaps : 0):0.0} max {c.MaxTickGap}; "
                 + $"stale {c.Stale}, missing base {missing}; rtt {rtt}";
        }
    }
}
