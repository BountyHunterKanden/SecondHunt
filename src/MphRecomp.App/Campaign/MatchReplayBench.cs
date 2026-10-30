using System;
using System.Collections.Generic;
using MphRecomp.Replay;

namespace MphRecomp.App;

// Seek timings on a device with no taps (bug-fix batch 2026-10-23, B1): launch extra --es replaybench 1 next to
// --es replay <path> runs a fixed script of the bar's seeks once the replay has started, and logs each one's wall time
// (from the seek to the frame it's there) under MPHReplayBench, then a summary. At 25 / 50 / 75 % of the replay (each
// reached by a seek from where the last left off): back 10 s, back 60 s, forward 10 s, forward 60 s; then the scrub bar's
// longest drags: to the start, to (just short of) the end, and back to the start; then the match's end (B2). Dev only: nothing else reads it.
internal sealed partial class CampaignRenderer
{
    bool _bench;
    int _benchNext, _benchWait = -1;
    string? _benchLabel;
    int _benchFrom, _benchFrames;
    double _benchStartMs, _benchLastMs, _benchMaxFrameMs;
    int _benchCpCount;
    double _benchCpMs, _benchCpMax;
    readonly List<string> _benchResults = new();
    // frames between one seek arriving and the next (the picture settles, any hitch after arriving shows in the log)
    const int BenchSettle = 45;

    (string Label, Func<int, int, int> Target)[]? _benchPlan;

    void ConfigureBench(LaunchArgs args)
    {
        _bench = args.Get("replaybench") is string s && s.Length > 0 && s != "0";
    }

    // TakeCheckpoint: one was taken
    void BenchCheckpoint(double ms)
    {
        if (!_bench) return;
        _benchCpCount++;
        _benchCpMs += ms;
        _benchCpMax = Math.Max(_benchCpMax, ms);
    }

    // ReplayRate, once a frame (before it notices a seek arriving): the script's next seek once the last one has settled
    void BenchTick()
    {
        if (!_bench || _replayPlayer == null || _host == null) return;
        double now = _replayClock.Elapsed.TotalMilliseconds;
        if (_benchLabel != null)
        {
            _benchFrames++;
            _benchMaxFrameMs = Math.Max(_benchMaxFrameMs, now - _benchLastMs);
            _benchLastMs = now;
            return;
        }
        if (_benchPlan == null)
        {
            if (_replayPlayer.Position < 120) return; // (the match settles in first)
            int n = _replayPlayer.File.Steps.Count;
            var plan = new List<(string, Func<int, int, int>)>();
            foreach (int pct in new[] { 25, 50, 75 })
            {
                plan.Add(($"to {pct}%", (_, all) => all * pct / 100));
                plan.Add(($"{pct}%: back 10 s", (pos, _) => pos - 10 * 60));
                plan.Add(($"{pct}%: back 60 s", (pos, _) => pos - 60 * 60));
                plan.Add(($"{pct}%: forward 10 s", (pos, _) => pos + 10 * 60));
                plan.Add(($"{pct}%: forward 60 s", (pos, _) => pos + 60 * 60));
            }
            plan.Add(("scrub to start", (_, _) => 0));
            plan.Add(("scrub start -> end", (_, all) => all - 60)); // (short of the end: the match's end isn't a seek)
            plan.Add(("scrub end -> start", (_, _) => 0));
            // B2: the match's own end holds (the bar live), a seek back from there, the end again, play again
            plan.Add(("to the end", (_, all) => all));
            plan.Add(("end: back 10 s", (pos, _) => pos - 10 * 60));
            plan.Add(("back to the end", (_, all) => all));
            plan.Add(("end: play again", (_, _) => 0));
            _benchPlan = plan.ToArray();
            _benchWait = 0;
            Log.Info("MPHReplayBench", $"start: {n} steps ({ReplayClock(n)}), "
                + $"{_replayPlayer.File.Settings.Players.Count} players");
        }
        if (_benchWait > 0)
        {
            _benchWait--;
            return;
        }
        if (_benchNext >= _benchPlan.Length)
        {
            if (_benchWait == 0) BenchSummary();
            _benchWait = -1;
            return;
        }
        (string label, Func<int, int, int> target) = _benchPlan[_benchNext++];
        _benchLabel = label;
        _benchFrom = _replayPlayer.Position;
        _benchFrames = 0;
        _benchMaxFrameMs = 0;
        _benchStartMs = _benchLastMs = now;
        _replayHeld = false;
        ReplaySeek(target(_replayPlayer.Position, _replayPlayer.File.Steps.Count));
    }

    // ReplayRate: a seek got there
    void BenchArrived()
    {
        if (_benchLabel == null || _replayPlayer == null) return;
        double ms = _replayClock.Elapsed.TotalMilliseconds - _benchStartMs;
        string line = $"{_benchLabel,-20} {ReplayClock(_benchFrom)} -> {ReplayClock(_replayPlayer.Position)} "
            + $"({_replayPlayer.Position - _benchFrom:+0;-0} steps): {ms,7:0} ms, {_benchFrames} frames, worst frame {_benchMaxFrameMs:0} ms";
        _benchResults.Add(line);
        Log.Info("MPHReplayBench", line);
        _benchLabel = null;
        _benchWait = BenchSettle;
    }

    void BenchSummary()
    {
        Log.Info("MPHReplayBench", "---- summary ----");
        foreach (string line in _benchResults) Log.Info("MPHReplayBench", line);
        Log.Info("MPHReplayBench", $"checkpoints taken: {_benchCpCount}, {(_benchCpCount > 0 ? _benchCpMs / _benchCpCount : 0):0.0} ms "
            + $"each on average, worst {_benchCpMax:0.0} ms; kept {_replayCheckpoints?.Count ?? 0} "
            + $"({(_replayCheckpoints?.Bytes ?? 0) >> 20} MB, every {_replayCheckpoints?.Every ?? 0} steps)"
            + (_replayCheckpoints?.Broken == true ? " BROKEN" : ""));
        Log.Info("MPHReplayBench", "done");
    }
}
