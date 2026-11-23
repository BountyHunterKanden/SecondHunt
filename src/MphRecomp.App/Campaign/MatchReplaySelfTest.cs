using System;
using System.Collections.Generic;
using System.IO;
using MphRecomp.Config;
using MphRecomp.Replay;

namespace MphRecomp.App;

// Instant seeking, checked on the device after every install (owner 2026-11-21: "all new replays going forward
// ... ALWAYS HAVE INSTANT SEEKING right away"). Launch extra --es replayselftest 1 with a bot match (match Battle, bots 1,
// time 75: docs/RELEASE.md) records it with its keyframes prepared as it's played (ReplayFeed -> :replayprep), and at
// its end opens the saved replay in its place (SelfTestReplaySink, replayselftest seek). That one seeks at once, no
// waiting -- 80 %, back to 30 %, 60 %, the end, back to 10 % -- and needs each seek to load a prepared keyframe and get
// there within SelfTestSeekMs. One line under MPHReplaySelfTest says how it went: REPLAYSELFTEST PASS <seek times> or
// REPLAYSELFTEST FAIL <reason>, a refusal's walk diff on the lines after it. Dev builds only.
internal sealed partial class CampaignRenderer
{
    // the host: open this saved replay in place of the match screen, launch extra replayselftest seek
    public Action<string>? SelfTestReplaySink;

    const string SelfTestTag = "MPHReplaySelfTest";
    // a seek slower than this isn't instant (the Odin's keyframe loads run ~0.1-0.3 s; the steps after it to the target, less)
    const double SelfTestSeekMs = 1000;
    // a seek not there after this many frames: stuck
    const int SelfTestStuckFrames = 20 * 60;
    static readonly (string Label, int Percent)[] SelfTestPlan =
        [("to 80%", 80), ("back to 30%", 30), ("to 60%", 60), ("to the end", 100), ("back to 10%", 10)];

    string? _selfTest; // null, "match" (recording) or "seek" (the replay)
    bool _selfTestDone, _selfTestFed, _selfTestPrepAtOpen;
    int _selfTestNext, _selfTestFrom, _selfTestFrames, _selfTestKeyframeAt = -1;
    string? _selfTestLabel;
    double _selfTestStartMs, _selfTestKeyframeMs;
    readonly List<string> _selfTestLines = new(), _selfTestTimes = new();

    // ConfigureMatch, before anything else reads the launch
    void ConfigureSelfTest(LaunchArgs args)
    {
        _selfTest = RecompSettings.PublicBuild || args.Get("replayselftest") is not { Length: > 0 } s || s == "0" ? null
            : s == "seek" ? "seek" : "match";
        if (_selfTest != null) Log.Info(SelfTestTag, $"REPLAYSELFTEST {(_selfTest == "match" ? "recording a match" : "seeking the replay")}");
    }

    void SelfTestResult(bool pass, string reason)
    {
        if (_selfTest == null || _selfTestDone) return;
        _selfTestDone = true;
        foreach (string line in _selfTestLines) Log.Info(SelfTestTag, line);
        string[] lines = reason.Split('\n');
        if (pass) Log.Info(SelfTestTag, "REPLAYSELFTEST PASS " + lines[0]);
        else Log.Warn(SelfTestTag, "REPLAYSELFTEST FAIL " + lines[0]);
        for (int i = 1; i < lines.Length; i++) Log.Info(SelfTestTag, "  " + lines[i]);
    }

    // StartMatchReplayed: a self-test match is recorded, its keyframes prepared as it's played
    void SelfTestMatchStarted(bool recorded, int mods)
    {
        if (_selfTest != "match") return;
        string prepare = SettingsPath != null ? RecompSettings.Load(SettingsPath).PrepareReplays : "?";
        string? why = !recorded ? "this match isn't recorded (RECORD REPLAYS off, or a solo / First Hunt / guest match)"
            : _replayFeed != null ? null
            : mods > 0 ? $"{mods} gameplay mod(s) on: a modded match isn't prepared as it's played"
            : ReplayPrepHost.Start == null ? "no prepare process on this platform"
            : $"PREPARE REPLAYS is {prepare.ToUpperInvariant()}: nothing is prepared as the match is played";
        if (why != null) SelfTestResult(false, why);
        else Log.Info(SelfTestTag, "recording; keyframes prepared as it's played");
    }

    // FeedStep: the live prepare started (true) or the feed failed (its message)
    void SelfTestFed(string? failed)
    {
        if (_selfTest != "match") return;
        if (failed != null) SelfTestResult(false, "no live keyframes for this match: " + failed);
        else _selfTestFed = true;
    }

    // EndReplay, the recording saved: the replay opened in place of the match
    void SelfTestSaved(string path, bool complete)
    {
        if (_selfTest != "match" || _selfTestDone) return;
        string? why = !complete ? "the match was left before its end"
            : !_selfTestFed ? "the live prepare never started"
            : SelfTestReplaySink == null ? "this host can't open the replay" : null;
        if (why != null)
        {
            SelfTestResult(false, why);
            return;
        }
        Log.Info(SelfTestTag, $"saved {Path.GetFileName(path)}; opening it now");
        _selfTestDone = true; // (the replay's own run gives the result)
        SelfTestReplaySink!(path);
    }

    // a replay refused, or keyframes turned off: the test's failure (the reason carries the walk diff when there is one)
    void SelfTestRefused(string why)
    {
        if (_selfTest == "seek") SelfTestResult(false, (_selfTestLabel != null ? $"seek {_selfTestLabel}: " : "") + why);
    }

    // StartKeyframes: the viewer had to start a prepare itself (the live one left nothing to read)
    void SelfTestPrepAtOpen() => _selfTestPrepAtOpen = true;

    // LoadKeyframe: a keyframe loaded for the seek
    void SelfTestKeyframe(int at, double ms)
    {
        _selfTestKeyframeAt = at;
        _selfTestKeyframeMs = ms;
    }

    // ReplayRate, once a frame (BenchTick): the next seek as soon as the replay has started, each the moment the last arrives
    void SelfTestTick()
    {
        if (_selfTest != "seek" || _selfTestDone || _replayPlayer == null || _host == null) return;
        if (_selfTestLabel != null)
        {
            if (++_selfTestFrames > SelfTestStuckFrames)
            {
                SelfTestResult(false, $"seek {_selfTestLabel}: not there after {SelfTestStuckFrames / 60} s (at step {_replayPlayer.Position})");
            }
            return;
        }
        if (_selfTestNext == 0)
        {
            if (_selfTestPrepAtOpen)
            {
                SelfTestResult(false, "the replay had no keyframes when it opened: they weren't prepared as the match was played");
                return;
            }
            if (_replayKeyframes == null)
            {
                SelfTestResult(false, "no keyframes for this replay" + (_keyframesOff ? " (keyframes off)" : ""));
                return;
            }
            Log.Info(SelfTestTag, $"replay open at step {_replayPlayer.Position} of {_replayPlayer.File.Steps.Count}: seeking now");
        }
        if (_selfTestNext >= SelfTestPlan.Length)
        {
            SelfTestResult(true, string.Join(", ", _selfTestTimes));
            return;
        }
        (string label, int percent) = SelfTestPlan[_selfTestNext++];
        int all = _replayPlayer.File.Steps.Count;
        int target = percent >= 100 ? all - 60 : all * percent / 100; // (the end: short of it, the match's end isn't a seek)
        _selfTestLabel = label;
        _selfTestFrom = _replayPlayer.Position;
        _selfTestFrames = 0;
        _selfTestKeyframeAt = -1;
        _selfTestStartMs = _replayClock.Elapsed.TotalMilliseconds;
        _replayHeld = false;
        ReplaySeek(target);
        if (_selfTestDone) return; // (refused there)
        if (_selfTestKeyframeAt < 0)
        {
            int before = _replayKeyframes?.Before(target) ?? -1;
            SelfTestResult(false, $"seek {label} (step {_selfTestFrom} -> {target}): no keyframe used"
                + (before < 0 ? ", none prepared before it" : $", the nearest is step {before}"));
        }
    }

    // ReplayRate: the seek got there
    void SelfTestArrived()
    {
        if (_selfTest != "seek" || _selfTestDone || _selfTestLabel == null || _replayPlayer == null) return;
        double ms = _replayClock.Elapsed.TotalMilliseconds - _selfTestStartMs;
        string line = $"{_selfTestLabel}: step {_selfTestFrom} -> {_replayPlayer.Position}, keyframe {_selfTestKeyframeAt} "
            + $"({_selfTestKeyframeMs:0} ms load), {_selfTestFrames} frames, {ms:0} ms";
        _selfTestLines.Add(line);
        _selfTestTimes.Add($"{_selfTestLabel} {ms:0} ms");
        string label = _selfTestLabel;
        _selfTestLabel = null;
        if (ms > SelfTestSeekMs) SelfTestResult(false, $"seek {label} took {ms:0} ms (over {SelfTestSeekMs:0})");
    }
}
