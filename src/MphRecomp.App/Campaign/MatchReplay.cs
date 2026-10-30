using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using MphRead;
using MphRead.Entities;
using MphRecomp.Campaign;
using MphRecomp.Config;
using MphRecomp.Frontend;
using MphRecomp.Replay;
using OpenTK.Mathematics;

namespace MphRecomp.App;

// Replays of local matches against bots (docs/REPLAY_MODE.md R2). Every such match is recorded unless RECOMP SETTINGS'
// RecordReplays is off, and saved to <data>/replays when it ends (or when the screen goes, marked incomplete); the
// last ReplayLibrary.KeepRecent are kept. A LAN / online match (O10) is recorded by its HOST, which runs the whole match
// (ReplayRecorder.Started(host, net) keeps each remote seat's applied input and drop); a guest sends the host its identity as
// the match starts and gets the host's recording back as it ends, over the session's side channel (ReplayLink), and saves it
// as its own copy (ReplayFile.OwnSeat). Every seat's NAME#tag and license card go in the file (ReplayFile.Identities).
// Launch extra --es replay newest|<path> plays one back through the same CampaignHost.Step, checking every step before
// it's drawn: the first step that differs stops the replay with a reason (owner's rule: exact or refused, never
// quietly wrong). MatchSession.cs calls in at the match's start, each step, its end, and Shutdown.
internal sealed partial class CampaignRenderer
{
    ReplayRecorder? _replayRecorder;
    // PREPARE REPLAYS: DURING MATCH: the recording fed to the keyframe prepare as it is played (ReplayFeed; a local match)
    ReplayFeed? _replayFeed;
    ReplayPlayer? _replayPlayer;
    // seeking back (T9): the match as it was every few seconds of the replay so far, restored in place of a restart
    ReplayCheckpoints? _replayCheckpoints;
    // seeking anywhere at once (thread B, steps 2-4): keyframes another process prepared from this replay (ReplayPrep), a
    // whole one every 10 s and a delta every second; one refused or not playing on exactly turns them off for this replay
    ReplayKeyframes? _replayKeyframes;
    bool _keyframesOff;
    // a LAN / online match's replay link (host: the guests' identities in, its recording out; guest: the other way)
    ReplayLink? _replayLink;
    // guest: the host's recording is wanted, and arrived (the match screen's session stays up a moment for it: LinkHold)
    volatile bool _replayWanted, _replayArrived;
    string? _replayPath;
    bool _replayDone;
    // --es replaydump a-b: log the raw state of steps a..b (ReplayState.Dump) under MPHReplayDump, to diff against the
    // PC's dump of the same replay (Tools -replayplay <file> dump=a-b) when a replay plays on one device but not another
    int _replayDumpFrom = -1, _replayDumpTo = -1;
    // an incomplete recording shorter than this isn't kept (a match quit at once shouldn't push out a real one)
    const int MinIncompleteSteps = 10 * 60;

    public bool ReplayPlaying => _replayPlayer != null;

    // the bar's hotkey names: the keyboard's (the desktop, until a controller is used) or the pad's
    public bool KeyboardHints { get; set; } = !OperatingSystem.IsAndroid();

    // an older recording's kill / death marks, found as it plays (a format 18 file has them all: ReplayFile.Marks)
    readonly ReplayMarkTracker _replayMarkTracker = new();
    readonly List<ReplayMark> _replayFound = new(), _replayFoundMarks = new();
    readonly HashSet<ReplayMark> _replayFoundSet = new();

    string? ReplayDataDir => SettingsPath != null ? Path.GetDirectoryName(SettingsPath) : null;

    // ConfigureMatch: --es replay newest|<path> sets the match up from a recording (true = handled)
    bool ConfigureReplay(LaunchArgs args)
    {
        string? which = args.Get("replay");
        if (string.IsNullOrEmpty(which))
        {
            return false;
        }
        try
        {
            string? path = which == "newest" ? (ReplayDataDir is string dir ? ReplayLibrary.Newest(dir) : null) : which;
            if (path == null)
            {
                _error = "REPLAY: no replays yet -- play a match against bots first";
                return true;
            }
            // (its checks wait for StartMatchReplayed: the imported arenas are only known once the game's files are)
            ReplayFile file = ReplayFile.Load(path);
            _replayPlayer = new ReplayPlayer(file);
            _replayPath = path;
            ConfigureBench(args);
            if (args.Get("replaydump") is string dump && dump.Length > 0)
            {
                string[] range = dump.Split('-');
                _replayDumpFrom = int.Parse(range[0]);
                _replayDumpTo = int.Parse(range[^1]);
            }
            _matchSettings = file.Settings;
            _hunter = file.Settings.Players[file.Settings.LocalSeat].Hunter;
            // a guest's copy of the host's recording: the camera starts on the guest's own seat (the host's view is the
            // recording's own first-person one); a served match's (nobody's own view: ReplaySeatless) on the player's
            // seat, else seat 1
            if (file.Settings.ServedHost) _replaySeat = file.OwnSeat >= 0 && file.OwnSeat < file.Settings.Players.Count ? file.OwnSeat : 0;
            else if (file.OwnSeat >= 0 && file.OwnSeat != file.Settings.LocalSeat) _replaySeat = file.OwnSeat;
            Log.Info("MPHReplay", $"playing {Path.GetFileName(path)}: {file.Settings.Mode} in {file.Settings.Arena}, "
                + $"{file.Steps.Count} steps{(file.Complete ? "" : " (incomplete)")}, recorded on {file.Platform} ({file.Build})");
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            _error = "REPLAY REFUSED: " + ex.Message;
            Log.Warn("MPHReplay", $"can't read {which}: {ex.Message}");
        }
        return true;
    }

    // TryStartMatch: the match itself, recorded or played back (null = a replay refused before it started: _error says why)
    CampaignHost? StartMatchReplayed(MphRecomp.Multiplayer.MatchSettings settings, int width, int height)
    {
        PlayerEntity.HostNoRespawnPrompt = _replayPlayer != null; // nobody watching a replay can press FIRE
        if (_replayPlayer != null)
        {
            // here, not in ConfigureReplay: MatchArenas knows the imported (Echoes) arenas only after Paths is set up,
            // so a replay of one was refused as "not on this device" (Odin 2026-10-08 01:01)
            if (ReplayPlayer.CheckBeforeStart(_replayPlayer.File) is string refused)
            {
                _error = "REPLAY REFUSED: " + refused;
                Log.Warn("MPHReplay", $"refused {_replayPath}: {refused}");
                return null;
            }
            CampaignHost played = _replayPlayer.Start(collectDrawItems: true, viewWidth: width, viewHeight: height);
            if (_replayPlayer.CheckAfterStart() is string why)
            {
                _error = "REPLAY REFUSED: " + why;
                Log.Warn("MPHReplay", "refused: " + why);
                return played;
            }
            // a new match: new checkpoints (one that went wrong stays off for this replay), the first one at its start
            if (_replayCheckpoints?.Broken != true) _replayCheckpoints = new ReplayCheckpoints();
            TakeCheckpoint(played);
            StartKeyframes(played);
            return played;
        }
        bool keep = ReplayDataDir != null && RecompSettings.Load(SettingsPath).RecordReplays
            && !MphRecomp.Multiplayer.MatchSettings.IsFirstHuntArena(settings.Arena); // (First Hunt's deathmatches aren't MPH replays)
        // a network match is recorded by its host only (a guest gets the host's file: StartReplayLink)
        bool record = keep && (_netSession == null || _netSession.IsHost)
            && !settings.Solo; // the dev launcher's solo run (SoloArena.cs) isn't a match worth keeping
        var recorder = record ? new ReplayRecorder(settings, MphRecomp.Update.UpdateHost.AppVersion) : null;
        // a bot match the setting keeps from being recorded says so for its first seconds (owner 10-27: one he needed
        // was lost unseen), and goes in settings_changes.log
        _recordOffNoticeMs = 0;
        if (ReplayDataDir != null && !keep && _netSession == null && !settings.Solo
            && !MphRecomp.Multiplayer.MatchSettings.IsFirstHuntArena(settings.Arena))
        {
            _recordOffNoticeMs = Environment.TickCount64 + 8000;
            RecompSettings.ChangeLog(SettingsPath, "a bot match started with RecordReplays off");
            Log.Warn("MPHReplay", "RECORD REPLAYS is off: this match isn't recorded");
        }
        CampaignHost host = CampaignHost.StartMatch(settings, collectDrawItems: true, viewWidth: width, viewHeight: height);
        if (_netSession != null) StartReplayLink(settings, keep);
        if (recorder != null)
        {
            if (_netSession != null) recorder.Started(host, _netSession.Kind);
            else recorder.Started(host.Scene);
            // an online host's shot check of its guests' shots and claimed hits, in the file (format 47)
            ReplayFile recording = recorder.File;
            MphRecomp.Multiplayer.Net.ShotCheck.Settled = _netSession != null ? check => recording.ShotChecks.Add(check) : null;
            // room for the whole match up front (no list growth mid-match): its time limit, else 10 minutes
            float seconds = Math.Min(settings.TimeLimitSeconds ?? 600, 3600);
            recorder.File.Steps.Capacity = (int)(seconds * 60) + 600;
            _replayRecorder = recorder;
            _replayFeed?.End();
            _replayFeed = _netSession == null && ReplayDataDir is string feedDir && ReplayPrepHost.Start != null
                && RecompSettings.Load(SettingsPath).PrepareReplays is not ("after" or "off")
                ? new ReplayFeed(recorder.File, feedDir) : null;
        }
        return host;
    }

    long _recordOffNoticeMs;
    string RecordOffNotice() => Environment.TickCount64 < _recordOffNoticeMs ? "RECORD REPLAYS IS OFF: this match isn't recorded" : "";

    // NetStep (a local match): one step, recorded, or played back and checked before it can be drawn
    void StepReplayed(CampaignInput input)
    {
        _replayStepUnseen = false;
        if (_replayPlayer == null)
        {
            _host!.Step(input);
            _replayRecorder?.Step(input, _host.Scene);
            if (_replayFeed != null) FeedStep();
            return;
        }
        if (_error != null || _replayDone)
        {
            return;
        }
        if (!_replayPlayer.TryNext(out CampaignInput recorded))
        {
            // the steps ran out before the match ended: a recording cut short (the match was left early)
            ReplayHoldEnd("END OF THE RECORDING: THE MATCH WAS LEFT HERE");
            Log.Info("MPHReplay", $"played to the end of an incomplete recording: {_replayPlayer.Position} steps, every one exact");
            return;
        }
        _replayStepUnseen = SeekStepUnseen();
        _host!.Scene.HostSkipDrawList = _replayStepUnseen;
        try
        {
            _host.Step(recorded);
        }
        finally
        {
            _host.Scene.HostSkipDrawList = false;
        }
        int step = _replayPlayer.Position; // Check moves it on
        if (step >= _replayDumpFrom && step <= _replayDumpTo)
        {
            foreach (string line in ReplayState.Dump(_host.Scene, step).Split('\n'))
            {
                Log.Info("MPHReplayDump", line);
            }
        }
        bool exact = _replayPlayer.Check(_host.Scene);
        if (!_replayPlayer.File.HasMarks)
        {
            // an older recording: its kills / deaths found as it plays (kept across a rewind's restart)
            _replayFound.Clear();
            _replayMarkTracker.Observe(_replayPlayer.Position, _replayFound);
            foreach (ReplayMark mark in _replayFound)
            {
                if (_replayFoundSet.Add(mark)) _replayFoundMarks.Add(mark);
            }
        }
        if (exact)
        {
            TakeCheckpoint(_host);
        }
        else if (_restoredFrom >= 0 && _restoredKeyframe)
        {
            // a prepared keyframe that didn't play on exactly: keyframes off for this replay, and the same step from the
            // match's start (its checkpoints then come from the restart's own run)
            int failed = _replayPlayer.FailedAt;
            Log.Warn("MPHReplay", $"step {failed} differs after loading the keyframe at step {_restoredFrom}: "
                + "keyframes off, restarting instead");
            KeyframesOff("step " + failed + " differed");
            if (RestartReplay())
            {
                _seekTarget = Math.Max(_seekTarget, failed + 1);
            }
        }
        else if (_restoredFrom >= 0 && _replayCheckpoints is { Broken: false } cps)
        {
            // a restored checkpoint that didn't play on exactly: checkpoints off, and the same step the old way (exact
            // or refused: the restart's run decides)
            int failed = _replayPlayer.FailedAt;
            Log.Warn("MPHReplay", $"step {failed} differs after restoring the checkpoint at step {_restoredFrom}: "
                + "checkpoints off, restarting instead");
            cps.Break();
            _restoredFrom = -1;
            if (RestartReplay())
            {
                _seekTarget = Math.Max(_seekTarget, failed + 1);
            }
        }
        else
        {
            // it stops here, the bar live (back, scrub, play again); nothing plays past this step (ReplaySeek)
            ReplayHoldEnd($"REPLAY STOPPED AT {ReplayClock(_replayPlayer.FailedAt)}: PAST HERE IT WOULDN'T PLAY EXACTLY AS RECORDED");
            Log.Warn("MPHReplay", $"step {_replayPlayer.FailedAt} of {_replayPlayer.File.Steps.Count} differs from the recording");
        }
    }

    // TryEndMatch (complete: the match ended) and Shutdown (complete: false, the screen went first)
    void EndReplay(bool complete)
    {
        PlayerEntity.HostNoRespawnPrompt = false;
        ResetReplayViewHooks();
        if (_host != null) _host.Scene.HostBeforeCamera = null;
        // a guest waits for the host's recording after a match that ran to its end with the host still there, and (0.3.6,
        // 2026-10-19) after leaving one still going, having asked for the
        // recording so far (a host before 0.3.6 ignores the ask: the hold then gives up after its limit)
        if (_netDriver?.PeerGone == true)
        {
            if (_replayWanted && _replayLink is { IsHost: false }) Log.Info("MPHReplay", "no recording asked for: the host is gone");
            _replayWanted = false;
        }
        else if (!complete && _replayWanted && _matchResults == null && _replayLink is { IsHost: false } asking
            && (_host?.Frame ?? 0) >= MinIncompleteSteps)
        {
            try
            {
                asking.RequestReplay();
                Log.Info("MPHReplay", $"left the match at frame {_host?.Frame}: asked the host for the recording so far");
            }
            catch (Exception ex)
            {
                _replayWanted = false;
                Log.Warn("MPHReplay", "no recording asked for: " + ex.Message);
            }
        }
        else if (!complete && _matchResults == null)
        {
            // (why a guest leaving early got no recording: match BF87SN, the Odin left no line at all)
            if (_replayLink is { IsHost: false })
            {
                Log.Info("MPHReplay", "no recording asked for: " + (!_replayWanted ? "RECORD REPLAYS is off"
                    : $"left at frame {_host?.Frame ?? 0}, under {MinIncompleteSteps}"));
            }
            _replayWanted = false;
        }
        if (_replayPlayer != null)
        {
            if (!complete)
            {
                _replayKeyframes?.Dispose();
                _replayKeyframes = null;
            }
            if (complete && !_replayDone)
            {
                _replayDone = true;
                Log.Info("MPHReplay", $"played to the end: {_replayPlayer.Position} of {_replayPlayer.File.Steps.Count} steps, "
                    + "every one exact");
            }
            return;
        }
        ReplayRecorder? recorder = _replayRecorder;
        _replayRecorder = null;
        _replayFeed?.End(); // (its prepare finishes the steps so far; the saved replay has the same key)
        _replayFeed = null;
        MphRecomp.Multiplayer.Net.ShotCheck.Settled = null;
        if (recorder == null || ReplayDataDir is not string dir)
        {
            return;
        }
        int steps = recorder.File.Steps.Count;
        if (!complete && steps < MinIncompleteSteps)
        {
            Log.Info("MPHReplay", $"not kept: left after {steps} steps");
            return;
        }
        try
        {
            recorder.End(complete);
            if (complete)
            {
                // how each seat placed, for the REPLAYS list's win / loss (format 3)
                foreach (MphRecomp.Frontend.MatchResults.Player p in MphRecomp.Frontend.MatchResults.Capture(recorder.File.Settings.LocalSeat).Rows)
                {
                    recorder.File.Places.Add(new ReplayPlace(p.Seat, p.Place, p.Points));
                }
            }
            // every human seat's identity (O10, REPLAYS: tap a name -> its license as it was, add friend / rival): this
            // device's own, and in a network match each guest's as its device sent it (ReplayLink)
            try
            {
                recorder.File.Identities.Add(ReplayLink.OwnIdentity(dir, recorder.File.Settings.LocalSeat, OnlineMe));
            }
            catch (Exception ex)
            {
                Log.Warn("MPHReplay", "no seat identity saved: " + ex.Message); // the replay still saves without it
            }
            if (_replayLink != null)
            {
                foreach (ReplaySeatIdentity id in _replayLink.Identities)
                {
                    if (id.Seat != recorder.File.Settings.LocalSeat) recorder.File.Identities.Add(id);
                }
            }
            var sw = Stopwatch.StartNew();
            string path = ReplayLibrary.Save(dir, recorder.File, DateTime.Now);
            Log.Info("MPHReplay", $"saved {Path.GetFileName(path)}: {steps} steps{(complete ? "" : " (incomplete)")}, "
                + $"{recorder.File.Identities.Count} identities, {recorder.File.NetEvents.Count} network events, "
                + $"{new FileInfo(path).Length / 1024} KB in {sw.ElapsedMilliseconds} ms");
            if (_netSession == null || complete) PrepareAfter(dir, path, recorder.File);
            // a network match's host: its recording to each guest that keeps replays (a complete match only)
            if (_replayLink != null && complete)
            {
                byte[] bytes = File.ReadAllBytes(path);
                List<int> seats = _replayLink.SeatsWantingReplay.Where(s => !_replayGiven.Contains(s)).ToList();
                foreach (int seat in seats)
                {
                    _replayLink.SendReplay(seat, bytes);
                }
                Log.Info("MPHReplay", $"sending the recording to {seats.Count} guest(s)");
            }
        }
        catch (Exception ex)
        {
            Log.Error("MPHReplay", "save failed: " + ex);
        }
    }

    // ---- LAN / online matches (O10) ----

    // this device's online card when the match is an online one (NAME#tag as the server has it)
    MphRecomp.Online.PlayerCard? OnlineMe => _online?.Client.Me;

    // StartMatchReplayed, a network match: the link over its session; a guest sends its identity now (and whether it keeps
    // replays); the match screen's hold on the session then stays up a moment past the end while the recording goes across
    void StartReplayLink(MphRecomp.Multiplayer.MatchSettings settings, bool keep)
    {
        if (_netSession == null || ReplayDataDir is not string dir) return;
        ReplayLink link;
        try
        {
            link = new ReplayLink(_netSession);
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException)
        {
            Log.Warn("MPHReplay", "no replay link: " + ex.Message);
            return;
        }
        _replayLink = link;
        int localSeat = settings.LocalSeat;
        if (_netSession.IsHost)
        {
            link.IdentityReceived += id => Log.Info("MPHReplay", $"seat {id.Seat}: {id.Name}#{id.Tag:0000} "
                + $"({(id.FriendCode != null ? "friend code" : "no friend code")}{(id.LicenseJson != null ? ", license" : "")})");
            _replayAsked.Clear();
            _replayGiven.Clear();
            link.ReplayRequested += seat => _replayAsked.Enqueue(seat); // (answered between steps, RecordNetStep)
        }
        else
        {
            _replayWanted = keep;
            _replayArrived = false;
            link.ReplayReceived += bytes =>
            {
                try
                {
                    ReplayFile file = ReplayFile.Read(new MemoryStream(bytes)); // (checked whole, as any file)
                    file.OwnSeat = localSeat;
                    string path = ReplayLibrary.Save(dir, file, DateTime.Now);
                    Log.Info("MPHReplay", $"saved the host's recording as {Path.GetFileName(path)}: {file.Steps.Count} steps, "
                        + $"{bytes.Length / 1024} KB, this player at seat {localSeat}");
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
                {
                    Log.Warn("MPHReplay", "the host's recording didn't save: " + ex.Message);
                }
                _replayArrived = true;
            };
            try
            {
                link.SendIdentity(ReplayLink.OwnIdentity(dir, localSeat, OnlineMe), wantReplay: keep);
            }
            catch (Exception ex)
            {
                Log.Warn("MPHReplay", "identity not sent: " + ex.Message);
            }
        }
        // (a guest whose match changed host mid-way (O5b) waits for nothing: nobody recorded the whole match)
        // (0.3.8: both ends wait while the recording is still moving, not a fixed time: the host until its link has every piece
        // acked or gives up (ReplayLink StallMs), the guest until no new piece came for HoldStallMs; a fixed 20 s cut a long
        // recording off on a slower link, 2026-10-22. MaxHoldMs only stops a trickle from holding the session for ever.)
        _netOwner = new ReplayLinkHold(_netOwner, link, () => link.IsHost ? link.Sending : _replayWanted && !_replayArrived
            && (_netSession as MphRecomp.Multiplayer.Net.NetMatchSession)?.Migrations is not > 0, link.IsHost ? 0 : HoldStallMs);
    }

    // NetStep (MatchSession.cs), after a network match's step: its host records the step (its own input; the remote seats'
    // came through the host's taps during the step)
    void RecordNetStep(CampaignInput input)
    {
        if (_replayRecorder != null && _host != null) _replayRecorder.Step(input, _host.Scene);
        AnswerReplayRequests();
    }

    // a network match's host: a guest leaving mid-match asked for the recording so far (0.3.6); it goes at once, cut short
    readonly ConcurrentQueue<int> _replayAsked = new();
    readonly HashSet<int> _replayGiven = new(); // (had theirs as they left: not sent again at the end)

    void AnswerReplayRequests()
    {
        while (_replayAsked.TryDequeue(out int seat))
        {
            ReplayRecorder? recorder = _replayRecorder;
            ReplayLink? link = _replayLink;
            if (recorder == null || link == null || ReplayDataDir is not string dir
                || recorder.File.Steps.Count < MinIncompleteSteps)
            {
                Log.Info("MPHReplay", $"seat {seat} left and asked for the recording: none to send");
                continue;
            }
            try
            {
                int local = recorder.File.Settings.LocalSeat;
                var ids = new List<ReplaySeatIdentity> { ReplayLink.OwnIdentity(dir, local, OnlineMe) };
                ids.AddRange(link.Identities.Where(i => i.Seat != local));
                byte[] bytes = recorder.Partial(ids);
                link.SendReplay(seat, bytes);
                _replayGiven.Add(seat);
                Log.Info("MPHReplay", $"seat {seat} left: sent it the recording so far ({recorder.File.Steps.Count} steps, "
                    + $"{bytes.Length / 1024} KB)");
            }
            catch (Exception ex)
            {
                Log.Warn("MPHReplay", $"seat {seat} left: recording not sent: " + ex.Message);
            }
        }
    }

    // The match screen's hold on a network match's session: disposing it waits (off the caller's thread) while the replay link
    // still has its recording to send / receive, up to a limit, then closes the link and the session; with nothing in flight,
    // at once (the others hear BYE as before).
    const int HoldStallMs = 8000, HoldFirstPieceMs = 10000, MaxHoldMs = 120000;

    sealed class ReplayLinkHold : IDisposable
    {
        readonly IDisposable? _inner;
        readonly ReplayLink _link;
        readonly Func<bool> _busy;
        readonly int _stallMs;
        int _disposed;

        // stallMs: give up once no new piece came in for that long (0: wait on busy alone, up to MaxHoldMs)
        public ReplayLinkHold(IDisposable? inner, ReplayLink link, Func<bool> busy, int stallMs)
        {
            (_inner, _link, _busy, _stallMs) = (inner, link, busy, stallMs);
        }

        bool Quiet(long start)
        {
            if (_stallMs <= 0) return false;
            long last = _link.LastPieceAt;
            long now = Environment.TickCount64;
            return last >= start ? now - last > _stallMs : now - start > HoldFirstPieceMs;
        }

        public void Dispose()
        {
            if (System.Threading.Interlocked.Exchange(ref _disposed, 1) != 0) return;
            if (!_busy())
            {
                Close();
                return;
            }
            System.Threading.Tasks.Task.Run(async () =>
            {
                var sw = Stopwatch.StartNew();
                long start = Environment.TickCount64;
                while (_busy() && !Quiet(start) && sw.ElapsedMilliseconds < MaxHoldMs) await System.Threading.Tasks.Task.Delay(50);
                Log.Info("MPHReplay", $"replay link done after {sw.ElapsedMilliseconds} ms{(_busy() ? " (gave up, received " + _link.Progress + ")" : "")}");
                Close();
            });
        }

        void Close()
        {
            _link.Dispose();
            _inner?.Dispose();
        }
    }

    // ---- playback controls (docs/REPLAY_MODE.md R3/R4; owner 2026-10-09, a Fortnite replay bar in our look: every
    // function is a button you can touch / click, pick with the D-pad + A, or hit with its own hotkey, named under it):
    //   D-pad left / right: move between the buttons; A: press the picked one.
    //   X: pause / play.  L1 / R1: back / forward 10 s.  L2 / R2: slower / faster (0.25x 0.5x 1x 2x 4x).
    //   D-pad up / down: previous / next player.  R3 or Y: camera (the player's view <-> free cam).  Start: the menu.
    //   Touch: the buttons; drag the scrub bar to go anywhere; a tap anywhere else pauses / plays.
    // Away from 1x (or seeking) the sound effects are muted (the music plays on); paused, the music and sound pause too.
    // A seek restores the nearest checkpoint at or before its step (back, or forward past one), else restarts the match
    // from the recording (back with none), then runs the rest unseen in bursts (below); every step is still checked, so
    // a seek is exact or refused like playback.
    static readonly double[] ReplaySpeeds = { 0.25, 0.5, 1, 2, 4 };
    const int ReplayNormalSpeed = 2;
    const int SkipSteps = 10 * 60;
    // B4 (bug-fix batch 2026-10-23): a seek runs as many steps as fit in SeekFrameMs of each frame (wall time, no guess at
    // a step's cost), unseen: no draw list, HUD, gun or smooth frames. The burst's last SeekSeenSteps steps are drawn as
    // usual (the frame's picture, and the two steps smooth frames blend), and a burst stops on the target itself.
    // Before: 240 drawn steps a frame (2.3 ms each on the Odin), and a cap that halved near the target (seeks crawled in).
    const double SeekFrameMs = 100;
    const int SeekSeenSteps = 2;
    long _seekBurstStart;
    bool _seekBurstEnding, _seekBurstDone;
    int _seekSeenLeft;
    bool _replayStepUnseen;
    // the step just run was a seek's unseen one (CampaignRenderer's step loop skips the looks: gun, smooth frames, skins)
    bool ReplayStepUnseen => _replayStepUnseen;
    // dragging the scrub bar moved the finger since the last frame (the picture follows it: ScrubPreview)
    bool _scrubMoved;
    int _replaySpeed = ReplayNormalSpeed;
    bool _replayHeld;
    float _replaySfxVolume = -1;
    int _seekTarget = -1;
    readonly ConcurrentQueue<(float X, float Y, int Phase, int Id)> _replayTouches = new();
    readonly Stopwatch _replayClock = Stopwatch.StartNew();
    double _replayLastFrame;

    // the bar's buttons, in D-pad order
    enum HudButton { Camera, Back, Play, Forward, Slower, Faster, PrevSeat, NextSeat, Hide, Count }
    int _hudFocus = (int)HudButton.Play;
    readonly (float X0, float Y0, float X1, float Y1)[] _hudRects = new (float, float, float, float)[(int)HudButton.Count];
    (float X0, float Y0, float X1, float Y1) _scrubRect;
    bool _scrubbing;
    float _scrubFrac;
    readonly bool[] _replayPrev = new bool[13];
    // HIDE (B / H): no bar and no game HUD, only the picture (the fades stay); A or a tap brings them back
    bool _replayUiHidden;
    // the free cam is flying (it's placed every picture: CampaignRenderer's frame loop, CampaignSmoothFrames.cs)
    bool ReplayFreeCam => _replayPlayer != null && _replayCam == ReplayCamKind.Free && _freeCamInit;

    // paused by the viewer (OnDrawFrame pauses the audio with it)
    public bool ReplayHeld => _replayPlayer != null && _replayHeld && !_replayDone && _seekTarget < 0;

    // a touch on the game (UI thread; CampaignActivity / the desktop's mouse): phase 0 down, 1 move, 2 up; id = the finger
    // (Android's pointer id, so two fingers can fly the free cam; the mouse is always 0)
    public void ReplayTouch(float x, float y, int phase, int id = 0) => _replayTouches.Enqueue((x, y, phase, id));

    // touch free cam (owner 2026-10-27: "no way to control free cam in replays with touch only"): a finger put down on
    // the picture's left half is a move stick from where it landed, on the right half dragging looks around; two fingers
    // do both at once. A finger that lifts without moving is still a tap (pause / play, or brings a hidden bar back).
    int _fcMoveId = -1, _fcLookId = -1, _scrubId = -1;
    float _fcMoveX0, _fcMoveY0, _fcMoveX, _fcMoveY, _fcLookX, _fcLookY, _fcLookDX, _fcLookDY;
    float _fcDownX, _fcDownY;
    bool _fcMoved;
    const float FreeCamTouchStick = 40, FreeCamTouchSlop = 6, FreeCamTouchLook = 3f; // u (height / 360); radians per screen height

    void ResetFreeCamTouch() => (_fcMoveId, _fcLookId, _fcLookDX, _fcLookDY) = (-1, -1, 0, 0);

    // OnDrawFrame, before the steps: the bar's input, and how fast the match runs this frame (1 = not a replay; 0 = paused)
    double ReplayRate()
    {
        if (_replayPlayer == null)
        {
            return 1;
        }
        double now = _replayClock.Elapsed.TotalSeconds;
        float dt = (float)Math.Min(now - _replayLastFrame, 0.1);
        _replayLastFrame = now;
        PadState pad = Pad;
        bool Edge(int i, bool down)
        {
            bool pressed = down && !_replayPrev[i];
            _replayPrev[i] = down;
            return pressed;
        }
        bool left = Edge(0, pad[PadButton.DpadLeft] || pad.HatX < -0.5f);
        bool right = Edge(1, pad[PadButton.DpadRight] || pad.HatX > 0.5f);
        bool up = Edge(2, pad[PadButton.DpadUp] || pad.HatY <= -0.5f);
        bool down = Edge(3, pad[PadButton.DpadDown] || pad.HatY >= 0.5f);
        bool a = Edge(4, pad[PadButton.A]);
        bool x = Edge(5, pad[PadButton.X]);
        bool l1 = Edge(6, pad[PadButton.L1]), r1 = Edge(7, pad[PadButton.R1]);
        bool l2 = Edge(8, pad[PadButton.L2] || pad.L2 >= 0.5f), r2 = Edge(9, pad[PadButton.R2] || pad.R2 >= 0.5f);
        bool cam = Edge(10, pad[PadButton.ThumbR]) | Edge(11, pad[PadButton.Y]);
        bool b = Edge(12, pad[PadButton.B]);
        if (!MatchOver)
        {
            if (left && !_replayUiHidden) _hudFocus = Math.Max(0, _hudFocus - 1);
            if (right && !_replayUiHidden) _hudFocus = Math.Min((int)HudButton.Count - 1, _hudFocus + 1);
            if (a && _replayUiHidden) _replayUiHidden = false;
            else if (a) HudPress((HudButton)_hudFocus);
            if (b) HudPress(HudButton.Hide);
            if (x) HudPress(HudButton.Play);
            if (l1) HudPress(HudButton.Back);
            if (r1) HudPress(HudButton.Forward);
            if (l2) HudPress(HudButton.Slower);
            if (r2) HudPress(HudButton.Faster);
            if (up) HudPress(HudButton.PrevSeat);
            if (down) HudPress(HudButton.NextSeat);
            if (cam) HudPress(HudButton.Camera);
            while (_replayTouches.TryDequeue(out var t)) HudTouch(t.X, t.Y, t.Phase, t.Id);
            if (_scrubbing && _scrubMoved) ScrubPreview();
        }
        else
        {
            while (_replayTouches.TryDequeue(out _)) { }
            ResetFreeCamTouch();
            _replayUiHidden = false; // the end's scoreboard
        }
        if (_replayCam == ReplayCamKind.Free && _host != null)
        {
            FlyFreeCam(_host.Player, pad, dt); // flies paused too
        }
        if (_host != null) PlaceReplayView(_host.Scene); // a seat or the free cam, paused or not
        BenchTick(); // (--es replaybench: MatchReplayBench.cs)
        bool seeking = _seekTarget >= 0 && _seekTarget > _replayPlayer.Position && !_replayDone;
        if (_seekTarget >= 0 && !seeking)
        {
            _seekTarget = -1;
            _accum = 0;
            Log.Info("MPHReplay", $"at step {_replayPlayer.Position}");
            BenchArrived();
        }
        if (_sfx != null)
        {
            if (_replaySfxVolume < 0)
            {
                _replaySfxVolume = _sfx.SfxMasterVolume;
            }
            _sfx.SfxMasterVolume = _replaySpeed == ReplayNormalSpeed && !seeking ? _replaySfxVolume : 0;
        }
        if (seeking)
        {
            _accum = 0;
            _seekBurstStart = Stopwatch.GetTimestamp();
            _seekBurstEnding = _seekBurstDone = false;
            return 1e7; // OnDrawFrame's dt times this: always more steps than the burst runs (ReplayStepCap ends it)
        }
        return _replayHeld || _scrubbing ? 0 : ReplaySpeeds[_replaySpeed]; // (still while the scrub bar's held)
    }

    // the most steps one frame may run: 4, or 4 per 1x of a faster replay; seeking, up to the target until the burst is
    // done (SeekStepUnseen), or a hard stop well past its time (a restart's load, a slow step)
    int ReplayStepCap => _replayPlayer == null ? 4
        : _seekTarget >= 0 ? (_seekBurstDone || SeekBurstMs > 4 * SeekFrameMs ? 0 : Math.Max(0, _seekTarget - _replayPlayer.Position))
        : Math.Max(4, (int)(4 * ReplaySpeeds[_replaySpeed]));

    double SeekBurstMs => Stopwatch.GetElapsedTime(_seekBurstStart).TotalMilliseconds;

    // StepReplayed, before a step: whether a seek runs it unseen. Unseen until the burst's time is up or the target is
    // SeekSeenSteps away; then SeekSeenSteps seen ones end the burst (or the target does).
    bool SeekStepUnseen()
    {
        if (_replayPlayer == null || _seekTarget <= _replayPlayer.Position) return false;
        if (!_seekBurstEnding && SeekBurstMs >= SeekFrameMs)
        {
            _seekBurstEnding = true;
            _seekSeenLeft = SeekSeenSteps;
        }
        if (_seekBurstEnding)
        {
            if (--_seekSeenLeft <= 0) _seekBurstDone = true;
            return false;
        }
        return _seekTarget - _replayPlayer.Position > SeekSeenSteps;
    }

    // ReplayRate, once a frame while the scrub bar's dragged: the picture goes to the finger (a seek like any, so a
    // checkpoint near there shows at once and the rest catches up in bursts; one that would restart the match waits for
    // the release). Exact as ever: the release seeks to its step.
    void ScrubPreview()
    {
        _scrubMoved = false;
        if (_replayPlayer == null) return;
        int step = (int)(_scrubFrac * _replayPlayer.File.Steps.Count);
        int at = _seekTarget >= 0 ? _seekTarget : _replayPlayer.Position;
        if (Math.Abs(step - at) < 30) return;
        ReplaySeek(step, preview: true);
    }

    string ReplaySpeedText => ReplaySpeeds[_replaySpeed] switch { 0.25 => "1/4x", 0.5 => "1/2x", double s => $"{s:0}x" };

    void HudPress(HudButton button)
    {
        if (_replayPlayer == null) return;
        _hudFocus = (int)button;
        switch (button)
        {
        case HudButton.Play:
            if (_replayEndText != null)
            {
                ReplaySeek(0); // at the end: play it again
                _replayHeld = false;
            }
            else
            {
                _replayHeld = !_replayHeld;
            }
            break;
        case HudButton.Back:
            ReplaySeek(_replayPlayer.Position - SkipSteps);
            break;
        case HudButton.Forward:
            ReplaySeek(_replayPlayer.Position + SkipSteps);
            break;
        case HudButton.Slower:
            _replaySpeed = Math.Max(0, _replaySpeed - 1);
            break;
        case HudButton.Faster:
            _replaySpeed = Math.Min(ReplaySpeeds.Length - 1, _replaySpeed + 1);
            break;
        case HudButton.PrevSeat:
        case HudButton.NextSeat:
            if (_replayCam == ReplayCamKind.Free) _replayCam = ReplayCamKind.ThirdPerson; // picking a player follows them
            CycleReplaySeat(button == HudButton.NextSeat);
            break;
        case HudButton.Camera:
            _replayCam = (ReplayCamKind)(((int)_replayCam + 1) % 3); // first person -> third person -> free cam
            _freeCamInit = false;
            ResetFreeCamTouch();
            break;
        case HudButton.Hide:
            _replayUiHidden = !_replayUiHidden;
            if (!_replayUiHidden) _hudFocus = (int)HudButton.Play;
            break;
        }
        _statusCountdown = 0;
        Log.Info("MPHReplay", $"{button}: {(_replayHeld ? "paused" : "playing")} at {ReplaySpeedText}, {ReplayCamText}");
    }

    void HudTouch(float x, float y, int phase, int id)
    {
        if (_replayPlayer == null) return;
        if (FreeCamTouch(x, y, phase, id)) return;
        if (_replayUiHidden)
        {
            if (phase == 0) HudPress(HudButton.Hide); // hidden, a tap anywhere only brings the bar back
            return;
        }
        var s = _scrubRect;
        float frac = s.X1 > s.X0 ? Math.Clamp((x - s.X0) / (s.X1 - s.X0), 0, 1) : 0;
        if (phase == 0)
        {
            if (x >= s.X0 && x <= s.X1 && y >= s.Y0 && y <= s.Y1)
            {
                (_scrubbing, _scrubFrac, _scrubMoved, _scrubId) = (true, frac, true, id);
                return;
            }
            for (int i = 0; i < _hudRects.Length; i++)
            {
                var r = _hudRects[i];
                if (x >= r.X0 && x <= r.X1 && y >= r.Y0 && y <= r.Y1)
                {
                    HudPress((HudButton)i);
                    return;
                }
            }
            HudPress(HudButton.Play); // a tap on the game itself
        }
        else if (_scrubbing && id != _scrubId)
        {
            // another finger while scrubbing: ignored
        }
        else if (_scrubbing && phase == 1)
        {
            _scrubMoved |= frac != _scrubFrac;
            _scrubFrac = frac;
        }
        else if (_scrubbing && phase == 2)
        {
            _scrubbing = _scrubMoved = false;
            _scrubId = -1;
            ReplaySeek((int)(frac * _replayPlayer.File.Steps.Count));
        }
    }

    // a touch while the free cam flies: true when it's the free cam's (a move or look finger), false for the bar's
    // buttons and scrub, which keep theirs
    bool FreeCamTouch(float x, float y, int phase, int id)
    {
        if (_replayCam != ReplayCamKind.Free || MatchOver)
        {
            ResetFreeCamTouch();
            return false;
        }
        float u = _height / 360f;
        if (phase == 0)
        {
            if (_scrubbing) return false;
            if (!_replayUiHidden)
            {
                var s = _scrubRect;
                if (x >= s.X0 && x <= s.X1 && y >= s.Y0 && y <= s.Y1) return false;
                foreach (var r in _hudRects)
                {
                    if (x >= r.X0 && x <= r.X1 && y >= r.Y0 && y <= r.Y1) return false;
                }
            }
            if (_fcMoveId < 0 && _fcLookId < 0) (_fcDownX, _fcDownY, _fcMoved) = (x, y, false); // a tap is one finger
            else _fcMoved = true;
            if (x < _width / 2f && _fcMoveId < 0) (_fcMoveId, _fcMoveX0, _fcMoveY0, _fcMoveX, _fcMoveY) = (id, x, y, x, y);
            else if (_fcLookId < 0) (_fcLookId, _fcLookX, _fcLookY) = (id, x, y);
            return true;
        }
        if (id != _fcMoveId && id != _fcLookId) return false; // not a free cam finger (the scrub's, or a third)
        if (MathF.Abs(x - _fcDownX) > FreeCamTouchSlop * u || MathF.Abs(y - _fcDownY) > FreeCamTouchSlop * u) _fcMoved = true;
        if (id == _fcMoveId) (_fcMoveX, _fcMoveY) = (x, y);
        else
        {
            _fcLookDX += x - _fcLookX;
            _fcLookDY += y - _fcLookY;
            (_fcLookX, _fcLookY) = (x, y);
        }
        if (phase == 2)
        {
            if (id == _fcMoveId) _fcMoveId = -1;
            else _fcLookId = -1;
            if (_fcMoveId < 0 && _fcLookId < 0 && !_fcMoved)
            {
                HudPress(_replayUiHidden ? HudButton.Hide : HudButton.Play); // a tap: as off the free cam
            }
        }
        return true;
    }

    // go to a step: from the latest checkpoint at or before it when that's nearer than here (back, or forward past one),
    // else forward runs there and back restarts the match from the recording first (preview: the scrub bar's drag, which
    // never restarts)
    void ReplaySeek(int step, bool preview = false)
    {
        if (_replayPlayer == null || _host == null) return;
        step = Math.Clamp(step, 0, _replayPlayer.File.Steps.Count);
        int from = _replayPlayer.Position;
        if (step >= from && (_replayPlayer.FailedAt >= 0 || _replayPlayer.Finished || _host.Ended)) return; // (stopped / the end)
        if (LoadKeyframe(step, from, preview))
        {
            // (a prepared keyframe nearer than any checkpoint or here, at most a second's steps short of it; or one refused
            // and the match restarted, run to the step from its start)
            if (_host == null) return;
        }
        else if (step < from)
        {
            if (!RestoreCheckpoint(step, preview) && (preview || !RestartReplay())) return;
        }
        else if (_replayCheckpoints?.Before(step) is SimSnapshot ahead && ahead.Step > from + SeekSeenSteps + 30)
        {
            RestoreCheckpoint(step, preview); // B4: a checkpoint already played past (seeking back left it there)
        }
        _seekTarget = step;
        _replayDone = false;
        _replayEndText = null;
        if (!preview) Log.Info("MPHReplay", $"seek to step {step} (from {from})");
    }

    // B2 (bug-fix batch 2026-10-23): a replay's end -- the match's own end, a recording cut short, or a step that wouldn't
    // play exactly -- pauses on its last picture with every control live (back, seek, scrub, play again, seats, cameras);
    // it leaves only from the menu. Before, the match's end put up the results and went back to the menus after a few
    // seconds, and the other two blanked the picture behind a message.
    string? _replayEndText; // the note over the bar at an end (null: not at one)

    void ReplayHoldEnd(string text)
    {
        if (_replayEndText == null) Log.Info("MPHReplay", $"held at the end, step {_replayPlayer?.Position}: {text}");
        _replayDone = true;
        _replayHeld = true;
        _replayEndText = text;
        _statusCountdown = 0;
    }

    // TryEndMatch, each frame the match has ended: a replay holds there instead of showing the results (true: held)
    bool ReplayHoldsEnd()
    {
        if (_replayPlayer == null) return false;
        if (_replayEndText == null)
        {
            Log.Info("MPHReplay", $"played to the end: {_replayPlayer.Position} of {_replayPlayer.File.Steps.Count} steps, "
                + "every one exact");
            ReplayHoldEnd("END OF THE MATCH");
        }
        return true;
    }

    // after a checked step: a checkpoint when one's due (ReplayCheckpoints: every 5 s of play, once each)
    void TakeCheckpoint(CampaignHost host)
    {
        if (_replayCheckpoints == null || _replayPlayer == null) return;
        int before = _replayCheckpoints.Count;
        _replayCheckpoints.AfterStep(_replayPlayer.Position, host);
        if (_replayCheckpoints.Count != before) BenchCheckpoint(_replayCheckpoints.LastTakeMs);
        if (_replayCheckpoints.Count != before && _replayCheckpoints.Count <= 3)
        {
            Log.Info("MPHReplay", $"checkpoint at step {_replayPlayer.Position}: {_replayCheckpoints.LastTakeMs:0.0} ms, "
                + $"{_replayCheckpoints.Count} kept, {_replayCheckpoints.Bytes >> 10} KB");
        }
    }

    // the step a restored match was put back to (-1: none since the last restart); a check failing after one breaks them
    int _restoredFrom = -1;

    // a seek: the latest checkpoint at or before the step, restored into the same match (false: none)
    bool RestoreCheckpoint(int step, bool quiet = false)
    {
        if (_replayCheckpoints?.Before(step) is not SimSnapshot snap || _host == null || _replayPlayer == null) return false;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        snap.Restore();
        AfterRestore(snap.Step, keyframe: false);
        if (!quiet) Log.Info("MPHReplay", $"seek to step {step}: checkpoint at step {snap.Step} restored ({sw.Elapsed.TotalMilliseconds:0.0} ms)");
        return true;
    }

    // a restored match put back at `step` (a checkpoint or a keyframe): the player, the found marks and the sounds with it
    void AfterRestore(int step, bool keyframe)
    {
        _replayPlayer!.Rewind(step);
        _replayMarkTracker.Reset();
        _sfx?.StopAll(); // the later sounds stop (the restored match plays its own again)
        ResetDroneFx();
        foreach (PlayerEntity p in PlayerEntity.Players) p.HostAfterRestore(); // the reticles' pixel data with their restored size
        _replayDone = false;
        _error = null;
        _restoredFrom = step;
        _restoredKeyframe = keyframe;
        _replayEndText = null;
    }

    // whether _restoredFrom came from a prepared keyframe (its failing check turns keyframes off, not checkpoints)
    bool _restoredKeyframe;

    // StartMatchReplayed, at the match's step 0: the viewer's side of the prepared keyframes (ReplayPrep), the prepare
    // started when they're missing or unfinished
    // a step of a fed match: the feed begun (its prepare started), or its new steps written
    void FeedStep()
    {
        try
        {
            if (_replayFeed!.Step() && ReplayDataDir is string dir && _replayFeed.Sidecar is string sidecar
                && ReplayPrep.NeedsPrep(sidecar))
            {
                ReplayPrep.Prune(dir);
                ReplayPrepHost.Start?.Invoke(_replayFeed.ReplayPath!, sidecar);
                Log.Info("MPHReplay", "preparing keyframes as the match is played: " + Path.GetFileName(sidecar));
            }
        }
        catch (Exception ex)
        {
            Log.Warn("MPHReplay", "no live keyframes for this match: " + ex.Message);
            _replayFeed?.End();
            _replayFeed = null;
        }
    }

    // a saved match (PREPARE REPLAYS: AFTER MATCH, or a network match's): its keyframes prepared now, in another process;
    // DURING MATCH's feed has its prepare running already (NeedsPrep false), OFF waits for the first viewing
    void PrepareAfter(string dir, string path, ReplayFile file)
    {
        try
        {
            if (ReplayPrepHost.Start is not { } start || RecompSettings.Load(SettingsPath).PrepareReplays == "off"
                || !ReplayPrep.Worth(file)) return;
            string sidecar = ReplayPrep.SidecarPath(dir, file);
            if (!ReplayPrep.NeedsPrep(sidecar, file)) return;
            ReplayPrep.Prune(dir);
            start(Path.GetFullPath(path), sidecar);
            Log.Info("MPHReplay", "preparing keyframes after the match: " + Path.GetFileName(sidecar));
        }
        catch (Exception ex)
        {
            Log.Warn("MPHReplay", "no keyframes prepared after the match: " + ex.Message);
        }
    }

    void StartKeyframes(CampaignHost played)
    {
        _replayKeyframes?.Dispose();
        _replayKeyframes = null;
        if (_keyframesOff || _replayPlayer == null || ReplayDataDir is not string dir || !ReplayPrep.Worth(_replayPlayer.File)) return;
        try
        {
            string sidecar = ReplayPrep.SidecarPath(dir, _replayPlayer.File);
            if (_replayPath != null && ReplayPrepHost.Start is { } start && ReplayPrep.NeedsPrep(sidecar, _replayPlayer.File))
            {
                ReplayPrep.Prune(dir);
                start(Path.GetFullPath(_replayPath), sidecar);
                Log.Info("MPHReplay", "preparing keyframes in another process: " + Path.GetFileName(sidecar));
            }
            _replayKeyframes = new ReplayKeyframes(played, sidecar, _replayPlayer.File); // (it opens the sidecar at the first seek)
        }
        catch (Exception ex)
        {
            KeyframesOff(ex.GetType().Name + ": " + ex.Message);
        }
    }

    void KeyframesOff(string why)
    {
        if (!_keyframesOff) Log.Warn("MPHReplay", "keyframes off for this replay: " + why);
        _keyframesOff = true;
        _replayKeyframes?.Dispose();
        _replayKeyframes = null;
    }

    // a seek through a prepared keyframe, when one is nearer the step than the nearest checkpoint (and, forward, than
    // here) by more than a keyframe's spacing (false: none used; true also when one was refused: the match restarted,
    // keyframes off)
    bool LoadKeyframe(int step, int from, bool preview)
    {
        if (_replayKeyframes == null || _host == null || _replayPlayer == null) return false;
        int at = _replayKeyframes.Before(step);
        if (_replayKeyframes.Broken)
        {
            KeyframesOff("the prepared keyframes were refused: " + _replayKeyframes.LastRefusal);
            return false;
        }
        if (at < 0) return false;
        int best = _replayCheckpoints?.Before(step) is SimSnapshot cp ? cp.Step : 0;
        if (step >= from) best = Math.Max(best, from);
        if (at <= best + ReplayPrep.Every) return false;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        if (!_replayKeyframes.Load(at))
        {
            // the match may be half-loaded: from its start again (the caller's seek target stands)
            KeyframesOff($"the keyframe at step {at} was refused: {_replayKeyframes?.LastRefusal}");
            RestartReplay();
            return true;
        }
        AfterRestore(at, keyframe: true);
        if (!preview) Log.Info("MPHReplay", $"seek to step {step}: keyframe at step {at} loaded ({sw.Elapsed.TotalMilliseconds:0.0} ms)");
        return true;
    }

    // the match from its start again, the same recording (the ship flow's host swap: dispose, drop the GPU copies, start)
    bool RestartReplay()
    {
        ReplayFile file = _replayPlayer!.File;
        try
        {
            _host?.Dispose();
        }
        catch (Exception ex)
        {
            Log.Warn("MPHReplay", "restart dispose: " + ex.Message);
        }
        _host = null;
        ReleaseSceneGpu();
        ResetDroneFx(); // the old run's drone effects (CampaignRenderer.DroneFx.cs)
        _restoredFrom = -1;
        _replayPlayer = new ReplayPlayer(file);
        _replayMarkTracker.Reset();
        _replayEndText = null;
        _replayDone = false;
        _error = null;
        _freeCamInit = _replayCam == ReplayCamKind.Free; // a free cam stays where it is
        _host = StartMatchReplayed(_matchSettings!, _width, _height);
        return _host != null;
    }

    // MatchStatusText's extra line (the status line)
    string ReplayStatusLine()
    {
        if (_replayPlayer != null)
        {
            string state = _replayDone ? "" : _seekTarget >= 0 ? "  SEEKING" : _replayHeld ? "  PAUSED" : "  " + ReplaySpeedText;
            string cam = ReplayCamText.Length > 0 ? "  " + ReplayCamText : "";
            return $"\nREPLAY {ReplayClock(_replayPlayer.Position)} / {ReplayClock(_replayPlayer.File.Steps.Count)}{state}{cam}, every step exact";
        }
        return _replayRecorder != null ? "\nrecording" : "";
    }

    static string ReplayClock(int step)
    {
        int seconds = step / 60;
        return $"{seconds / 60}:{seconds % 60:00}";
    }

    // ---- R4 spectator cameras (docs/REPLAY_MODE.md): any player in first or third person, or a free-flying camera.
    // Render and sound only (Scene.HostView, MphRead's HostHideSeat / HostHideReticle / HostMainSoundsPlaced hooks): the
    // recorded input, aim and match logic never see this. The bar's input (ReplayRate, once a frame) picks the camera;
    // UpdateThirdPerson (each step) and ReplayRate both place it.
    //   First person on the recording's own seat is the game's own view (gun, HUD, reticle), as before this feature;
    //   on another seat, the view from their eyes (their body hidden, no reticle). Third person: a chase camera behind
    //   them. Free cam: the left stick flies along the view (forward / back, strafe), the right stick looks around.
    //   Sound is heard from the camera: the recording's own player sounds like anyone else unless it's their own view.
    enum ReplayCamKind { FirstPerson, ThirdPerson, Free }
    ReplayCamKind _replayCam;
    int _replaySeat = -1; // -1: the recording's own seat
    Vector3 _freeCamPos;
    float _freeCamYaw, _freeCamPitch;
    bool _freeCamInit;

    // a served match's recording (docs/DEDICATED_SERVERS.md): it plays with no local seat as it was recorded (MatchStart:
    // HostNoLocalSeat), so there's no "own view": every seat is watched like another seat, its HUD too (HostSeatlessHud)
    bool ReplaySeatless => _replayPlayer?.File.Settings.ServedHost == true;
    int WatchedSeat => _replaySeat >= 0 ? _replaySeat : ReplaySeatless ? 0 : _replayPlayer?.File.Settings.LocalSeat ?? 0;
    bool WatchingOwnView => _replayCam == ReplayCamKind.FirstPerson && !ReplaySeatless
        && WatchedSeat == (_replayPlayer?.File.Settings.LocalSeat ?? 0);

    string ReplayCamText => _replayCam == ReplayCamKind.Free ? "FREE CAM"
        : WatchingOwnView ? "" : $"watching {ReplaySeatName(WatchedSeat)}";

    string ReplaySeatName(int seat)
    {
        // a network match's seat: its NAME#tag as the match had it (O10)
        if (_replayPlayer?.File.Identities.FirstOrDefault(i => i.Seat == seat) is ReplaySeatIdentity id && id.Name is { Length: > 0 })
        {
            return id.Tag > 0 ? $"{id.Name}#{id.Tag:0000}" : id.Name;
        }
        var players = _replayPlayer?.File.Settings.Players;
        return players != null && seat >= 0 && seat < players.Count && players[seat].Nickname is string n && n.Length > 0
            ? n : $"seat {seat + 1}";
    }

    // UpdateThirdPerson: true when this placed the view (HostView); the caller's own camera logic is skipped that step
    bool UpdateReplayCamera(Scene scene, PlayerEntity mainPlayer) => _replayPlayer != null && PlaceReplayView(scene);

    // (B3, 10-23) placed again in each step just before MphRead works the camera out (Scene.HostBeforeCamera): placed
    // only after the step, a seat's view was a step behind its eyes while its gun (and the culling, the HUD's aim) were
    // this step's, so the gun swung about the picture by a step's turn whenever the seat turned
    Action? _replayBeforeCamera;

    bool PlaceReplayView(Scene scene)
    {
        scene.HostBeforeCamera = _replayBeforeCamera ??= () =>
        {
            if (_replayPlayer != null && _host != null) PlaceReplayView(_host.Scene);
        };
        bool own = WatchingOwnView;
        int seat = WatchedSeat;
        PlayerEntity p = PlayerEntity.Players[seat];
        // another seat's first person goes to its own ball camera while it's morphed, like play (MphRead switches every
        // player's camera on morph), and back in the head when it unmorphs
        bool seatHead = p.CameraType == CameraType.First;
        bool seatEyes = _replayCam == ReplayCamKind.FirstPerson && !own && seatHead;
        PlayerEntity.HostHideReticle = !own && !seatEyes;
        PlayerEntity.HostMainSoundsPlaced = !own && !(_replayCam == ReplayCamKind.ThirdPerson && seat == _replayPlayer!.File.Settings.LocalSeat);
        PlayerEntity.HostHideSeat = -1;
        // T9: from another seat's eyes its own first-person gun (or arm cannon) and HUD; anywhere but the main player's
        // own eyes, the main player's body (MphRead draws only its gun in first person)
        PlayerEntity.HostFirstPersonSeat = seatEyes ? seat : -1;
        PlayerEntity.HostMainSpectated = !own;
        PlayerEntity.HostHudSeat = _replayCam != ReplayCamKind.Free && !own ? p : null;
        PlayerEntity.HostSeatlessHud = ReplaySeatless; // (seat 1, Main, gets its HUD like any watched seat: Renderer)
        // free cam is nobody's eyes: no HUD, health box, damage flashes, kill messages or own death sounds; third person
        // is outside the head: no helmet frame (owner 2026-10-27, PlayerHud.cs)
        PlayerEntity.HostNoViewer = _replayCam == ReplayCamKind.Free;
        PlayerEntity.HostOutsideHead = _replayCam == ReplayCamKind.ThirdPerson;
        // Zero Suit: no helmet frame round the screen when the HUD shown is a Zero Suit seat's (MainHasNoHelmet: the main player's)
        int hudSlot = PlayerEntity.HudOwner.SlotIndex;
        PlayerEntity.HostNoHelmet = hudSlot >= 0 && hudSlot < _seatSkin.Length
            && _seatSkin[hudSlot]?.Contains("ZeroSuit", System.StringComparison.OrdinalIgnoreCase) == true;
        if (!seatEyes) PlayerEntity.HostFirstPersonTurn = Quaternion.Identity;
        if (_replayCam == ReplayCamKind.Free && _freeCamInit)
        {
            var facing = FreeCamFacing;
            scene.HostCameraOffset = Vector3.Zero;
            scene.HostView = new HostView(_freeCamPos, _freeCamPos + facing, Vector3.UnitY, FovY: 0);
            return true;
        }
        if (own)
        {
            scene.HostView = null; // the recording's own camera: unchanged from before R4
            return false;
        }
        scene.HostCameraOffset = Vector3.Zero;
        CameraInfo eye = p.CameraInfo;
        float fov = eye.Fov > 0 ? eye.Fov : 0; // the seat's own field of view (an Imperialist zoom)
        if (_replayCam == ReplayCamKind.FirstPerson)
        {
            // (B3) the seat's own view exactly: its eye, target (with any shake) and up, as CameraInfo.ViewMatrix has them
            Vector3 up = eye.TrueUp.LengthSquared > 0.0001f ? eye.TrueUp : Vector3.UnitY;
            if (!seatHead)
            {
                scene.HostView = new HostView(eye.Position, eye.Target, up, fov);
                return true;
            }
            Vector3 look = eye.Facing.LengthSquared > 0.0001f ? eye.Facing.Normalized() : Vector3.UnitZ;
            if (!p.IsBot || PlayerEntity.HostBotRoamLook) // (format 57 on: a roaming bot's own view is level, thread A's A6)
            {
                PlayerEntity.HostFirstPersonTurn = Quaternion.Identity;
                scene.HostView = new HostView(eye.Position, eye.Target, up, fov);
                return true;
            }
            Vector3 view = BotEye(p, look);
            Quaternion turn = Turn(look, view);
            PlayerEntity.HostFirstPersonTurn = turn;
            scene.HostView = new HostView(eye.Position, eye.Position + view, Vector3.Transform(up, turn), fov);
            return true;
        }
        PlayerNetState s = p.NetCapture();
        var back = new Vector3(s.Facing.X, 0, s.Facing.Z);
        back = back.LengthSquared > 0.0001f ? back.Normalized() : Vector3.UnitZ;
        scene.HostView = new HostView(s.Position - back * 3 + Vector3.UnitY * 1.5f,
            s.Position + Vector3.UnitY * 0.5f, Vector3.UnitY, FovY: 0);
        return true;
    }

    // A bot's eyes (T9, owner 2026-10-10: "the angle is weird"): MPH's bots carry their aim tilted far up or down while
    // they roam with nobody to shoot (their sim camera looks 30-50 degrees into the sky; a human's eyes are their aim),
    // so a bot's first person keeps its own turn but is level while it has no target, and takes its real aim's tilt
    // while it fights. The tilt eases (per step), so the view doesn't snap. Drawing only: the gun and reticle turn with it.
    // Replays before format 57 only: from 57 the bots' own roaming look is level (MatchSettings.BotRoamLook, bug-fix batch
    // A6), so a bot's first person is its real view, as a human's (B3).
    const float BotEyeEase = 0.12f, BotEyeMaxTilt = 1.2f; // (radians: about 70 degrees)
    float _botEyePitch;
    int _botEyeSeat = -1, _botEyeStep = -1;

    Vector3 BotEye(PlayerEntity p, Vector3 look)
    {
        float yaw = MathF.Atan2(look.X, look.Z);
        float aimPitch = MathF.Asin(Math.Clamp(look.Y, -1, 1));
        bool fighting = p.AiData?.HostTargetPlayer is PlayerEntity t && t.Health > 0;
        float want = fighting ? Math.Clamp(aimPitch, -BotEyeMaxTilt, BotEyeMaxTilt) : 0;
        int step = _replayPlayer?.Position ?? 0;
        if (p.SlotIndex != _botEyeSeat || step < _botEyeStep)
        {
            (_botEyeSeat, _botEyePitch) = (p.SlotIndex, want); // a new seat, or a seek back: straight on
        }
        else
        {
            for (int i = _botEyeStep; i < step && i < _botEyeStep + 60; i++) _botEyePitch += (want - _botEyePitch) * BotEyeEase;
        }
        _botEyeStep = step;
        float cos = MathF.Cos(_botEyePitch);
        return new Vector3(MathF.Sin(yaw) * cos, MathF.Sin(_botEyePitch), MathF.Cos(yaw) * cos);
    }

    // the rotation taking one unit direction to another
    static Quaternion Turn(Vector3 from, Vector3 to)
    {
        float dot = Math.Clamp(Vector3.Dot(from, to), -1, 1);
        Vector3 axis = Vector3.Cross(from, to);
        if (axis.LengthSquared < 1e-10f) return Quaternion.Identity;
        return Quaternion.FromAxisAngle(axis.Normalized(), MathF.Acos(dot));
    }

    // MatchArmCannon: the seat a replay watches from its eyes (its first-person gun / arm cannon), else null
    PlayerEntity? ReplayFirstPersonSeat => _replayPlayer != null && PlayerEntity.HostFirstPersonSeat >= 0
        ? PlayerEntity.Players[PlayerEntity.HostFirstPersonSeat] : null;

    // the replay's view hooks off (EndReplay / a non-replay match)
    static void ResetReplayViewHooks()
    {
        PlayerEntity.HostHideReticle = false;
        PlayerEntity.HostMainSoundsPlaced = false;
        PlayerEntity.HostHideSeat = -1;
        PlayerEntity.HostFirstPersonSeat = -1;
        PlayerEntity.HostFirstPersonTurn = Quaternion.Identity;
        PlayerEntity.HostMainSpectated = false;
        PlayerEntity.HostHudSeat = null;
        PlayerEntity.HostSeatlessHud = false;
        PlayerEntity.HostNoViewer = false;
        PlayerEntity.HostOutsideHead = false;
    }

    // the next / previous seat NetCapture reports Active, wrapping
    void CycleReplaySeat(bool next)
    {
        int total = PlayerEntity.MaxPlayers;
        int cur = WatchedSeat;
        for (int i = 1; i <= total; i++)
        {
            int seat = ((next ? cur + i : cur - i) % total + total) % total;
            if ((PlayerEntity.Players[seat].NetCapture().Flags & PlayerNetState.Active) != 0)
            {
                _replaySeat = seat;
                break;
            }
        }
    }

    const float FreeCamSpeed = 6f, FreeCamLookSpeed = 2.2f;

    Vector3 FreeCamFacing => new(MathF.Sin(_freeCamYaw) * MathF.Cos(_freeCamPitch), MathF.Sin(_freeCamPitch),
        MathF.Cos(_freeCamYaw) * MathF.Cos(_freeCamPitch));

    // free cam, once a frame in real time: started from the current view (the followed seat's chase spot, or the
    // main player's), then the sticks fly it
    void FlyFreeCam(PlayerEntity mainPlayer, PadState pad, float dt)
    {
        if (!_freeCamInit)
        {
            PlayerEntity seatPlayer = PlayerEntity.Players[WatchedSeat];
            PlayerNetState s = seatPlayer.NetCapture();
            var flat = new Vector3(s.Facing.X, 0, s.Facing.Z);
            flat = flat.LengthSquared > 0.0001f ? flat.Normalized() : Vector3.UnitZ;
            _freeCamPos = s.Position + Vector3.UnitY * 1.5f - flat * 3;
            _freeCamYaw = MathF.Atan2(flat.X, flat.Z);
            _freeCamPitch = 0;
            _freeCamInit = true;
        }
        // touch (FreeCamTouch): the look finger's drag since last frame turns it, the move finger is a stick
        float look = _height > 0 ? FreeCamTouchLook / _height : 0;
        float ty = _aimInvertY ? -_fcLookDY : _fcLookDY;
        _freeCamYaw -= pad.Rx * FreeCamLookSpeed * dt + _fcLookDX * look;
        // the sticks as in play (BuildInput): stick Y is + down, so forward = -Ly; look up/down follows the invert setting
        float ry = _aimInvertY ? -pad.Ry : pad.Ry;
        _freeCamPitch = Math.Clamp(_freeCamPitch - ry * FreeCamLookSpeed * dt - ty * look, -1.5f, 1.5f);
        (_fcLookDX, _fcLookDY) = (0, 0);
        float lx = pad.Lx, ly = pad.Ly;
        if (_fcMoveId >= 0 && _height > 0)
        {
            float r = FreeCamTouchStick * _height / 360f;
            float sx = (_fcMoveX - _fcMoveX0) / r, sy = (_fcMoveY - _fcMoveY0) / r;
            float len = MathF.Sqrt(sx * sx + sy * sy);
            if (len > 1) (sx, sy) = (sx / len, sy / len);
            (lx, ly) = (Math.Clamp(lx + sx, -1, 1), Math.Clamp(ly + sy, -1, 1));
        }
        Vector3 facing = FreeCamFacing;
        var right = Vector3.Cross(facing, Vector3.UnitY).Normalized();
        _freeCamPos += (facing * -ly + right * lx) * FreeCamSpeed * dt;
    }

    // ---- the playback bar (owner 2026-10-08: "a more real interface than the text in the upper left"; 2026-10-09: laid
    // out like a Fortnite replay bar, in our look). A scrub row (time pill, the recording's bar, length pill) over a row
    // of pills -- camera; back 10 s, play / pause, forward 10 s; slower, speed, faster; previous player, who, next --
    // each button's hotkey on a key cap under it. The menus' font and colours (REPLAYS' orange, frame orange, pale
    // yellow) on drawn panels, not a ROM screen. Sizes in u = height / 360, so it reads the same on the Odin and a PC.
    readonly UiTextureCache _replayHudTextures = new();
    readonly UiGlDrawer _replayHudDrawer = new();
    MenuFont? _replayHudFont;
    UiDrawList? _replayHudList;

    static readonly (float R, float G, float B) HudOrange = (1, 165 / 255f, 66 / 255f), HudFrame = (230 / 255f, 99 / 255f, 8 / 255f),
        HudPale = (1, 247 / 255f, 156 / 255f), HudWhite = (1, 1, 1);

    void ReplayHudGlReset()
    {
        _replayHudDrawer.Init();
        _replayHudFont = null; // lazily rebuilt: Extract/Paths may not be ready yet at surface creation
    }

    void ReplayHudDraw(int width, int height)
    {
        if (_replayPlayer == null || MatchOver || _replayUiHidden) return;
        try
        {
            _replayHudFont ??= new MenuFont(_replayHudTextures);
            _replayHudList ??= new UiDrawList(_replayHudTextures);
        }
        catch (Exception ex)
        {
            Log.Warn("MPHReplay", "HUD font unavailable: " + ex.Message); // plain status text still shows
            return;
        }
        MenuFont font = _replayHudFont;
        UiDrawList list = _replayHudList;
        list.Clear();
        float u = height / 360f;
        float text = 6.5f * u / MenuFont.Cell, small = 4.4f * u / MenuFont.Cell;
        float pillH = 17 * u, gap = 5 * u, hintH = 8 * u, btn = 18 * u;
        float hintTop = height - 6 * u - hintH, pillTop = hintTop - 2.5f * u - pillH;
        float scrubH = 11 * u, scrubTop = pillTop - 6 * u - scrubH;
        float mid = pillTop + pillH / 2;

        string cam = _replayCam switch { ReplayCamKind.Free => "FREE CAM", ReplayCamKind.ThirdPerson => "THIRD PERSON", _ => "FIRST PERSON" };
        string who = _replayCam == ReplayCamKind.Free ? "FREE CAM"
            : ReplaySeatName(WatchedSeat).ToUpperInvariant();
        string speed = ReplaySpeedText.ToUpperInvariant();
        float camW = Math.Max(font.MeasureCanvas("THIRD PERSON", text), font.MeasureCanvas(cam, text)) + 12 * u;
        float transportW = btn * 3;
        float speedTextW = font.MeasureCanvas("1/4X", text) + 6 * u;
        float speedW = btn * 2 + speedTextW;
        float whoTextW = Math.Max(font.MeasureCanvas("PLAYER 4", text), font.MeasureCanvas(who, text)) + 6 * u;
        float whoW = btn * 2 + whoTextW;
        float hideW = font.MeasureCanvas("HIDE", text) + 12 * u;
        float rowW = camW + transportW + speedW + whoW + hideW + 4 * gap;
        float clusterW = Math.Max(rowW, width * 0.55f);
        float left = (width - clusterW) / 2;

        // scrub row: elapsed pill, the bar (a drag previews where it lets go), length pill
        int total = Math.Max(1, _replayPlayer.File.Steps.Count);
        float shownFrac = _scrubbing ? _scrubFrac
            : Math.Clamp((_seekTarget >= 0 ? _seekTarget : _replayPlayer.Position) / (float)total, 0, 1);
        int shownStep = _scrubbing || _seekTarget >= 0 ? (int)(shownFrac * total) : _replayPlayer.Position;
        float timeW = font.MeasureCanvas("00:00", text) + 10 * u;
        Pill(list, left, scrubTop, left + timeW, scrubTop + scrubH, u, false);
        Label(font, list, ReplayClock(shownStep), left + timeW / 2, scrubTop, scrubH, text, HudWhite, 1);
        Pill(list, left + clusterW - timeW, scrubTop, left + clusterW, scrubTop + scrubH, u, false);
        Label(font, list, ReplayClock(total), left + clusterW - timeW / 2, scrubTop, scrubH, text, HudWhite, 1);
        float bx0 = left + timeW + gap, bx1 = left + clusterW - timeW - gap;
        float bt = scrubTop + scrubH / 2 - 2.5f * u, bb = bt + 5 * u;
        _scrubRect = (bx0, scrubTop - 4 * u, bx1, scrubTop + scrubH + 2 * u); // a finger-sized strip
        Rect(list, bx0 - 0.7f * u, bt - 0.7f * u, bx1 + 0.7f * u, bb + 0.7f * u, HudFrame, 0.9f);
        Rect(list, bx0, bt, bx1, bb, (0, 0, 0), 0.65f);
        float played = bx0 + (bx1 - bx0) * Math.Clamp(_replayPlayer.Position / (float)total, 0, 1);
        Rect(list, bx0, bt, played, bb, HudOrange, 0.95f);
        float at = bx0 + (bx1 - bx0) * shownFrac;
        Rect(list, at - 1.3f * u, bt - 3 * u, at + 1.3f * u, bb + 3 * u, HudWhite, 1);
        if (_seekTarget >= 0)
        {
            Label(font, list, "SEEKING", (bx0 + bx1) / 2, scrubTop - 16 * u, 8 * u, small, HudPale, 1);
        }
        else if (_replayEndText != null)
        {
            Label(font, list, _replayEndText + K("  -  X: PLAY AGAIN", "  -  SPACE: PLAY AGAIN"), (bx0 + bx1) / 2,
                scrubTop - 16 * u, 8 * u, small, HudWhite, 1);
        }
        // how to reach the buttons: over the time pills (the marks have the bar's own width)
        font.EmitCanvas(K("D-PAD LEFT / RIGHT + A: PICK", "ARROWS + ENTER: PICK"), left, scrubTop - 8 * u,
            0, small, HudPale.R, HudPale.G, HudPale.B, 0.75f, list);
        font.EmitCanvas(K("START: MENU", "ESC: MENU"), left + clusterW, scrubTop - 8 * u,
            1, small, HudPale.R, HudPale.G, HudPale.B, 0.75f, list);

        // the watched player's kills (an orange diamond) and deaths (a white cross) along the bar, Fortnite style
        int markSeat = WatchedSeat;
        List<ReplayMark> marks = _replayPlayer.File.HasMarks ? _replayPlayer.File.Marks : _replayFoundMarks;
        float my = bt - 6 * u, mr = 2.6f * u;
        foreach (ReplayMark mark in marks)
        {
            if (mark.Seat != markSeat) continue;
            float mx = bx0 + (bx1 - bx0) * Math.Clamp(mark.Step / (float)total, 0, 1);
            if (mark.Death)
            {
                Line(list, mx - mr, my - mr, mx + mr, my + mr, 0.9f * u, HudWhite, 0.95f);
                Line(list, mx - mr, my + mr, mx + mr, my - mr, 0.9f * u, HudWhite, 0.95f);
            }
            else
            {
                Tri(list, mx, my - mr * 1.2f, mx + mr, my, mx - mr, my, (0, 0, 0), 0.6f);
                Tri(list, mx, my - mr, mx + mr * 0.8f, my, mx - mr * 0.8f, my, HudOrange, 1);
                Tri(list, mx, my + mr * 1.2f, mx - mr, my, mx + mr, my, (0, 0, 0), 0.6f);
                Tri(list, mx, my + mr, mx - mr * 0.8f, my, mx + mr * 0.8f, my, HudOrange, 1);
            }
        }

        float x = (width - rowW) / 2;

        // camera
        Pill(list, x, pillTop, x + camW, pillTop + pillH, u, false);
        Button(list, HudButton.Camera, x, pillTop, x + camW, pillTop + pillH, u);
        Label(font, list, cam, x + camW / 2, pillTop, pillH, text, HudOrange, 1);
        Hint(font, list, K("R3 / Y", "Y"), x + camW / 2, hintTop, hintH, small, u);
        x += camW + gap;

        // back 10 s, play / pause, forward 10 s
        Pill(list, x, pillTop, x + transportW, pillTop + pillH, u, false);
        float s = 4 * u;
        Button(list, HudButton.Back, x, pillTop, x + btn, pillTop + pillH, u);
        float c = x + btn / 2;
        Tri(list, c, mid - s, c, mid + s, c - s * 1.2f, mid, HudWhite, 1);
        Tri(list, c + s * 1.2f, mid - s, c + s * 1.2f, mid + s, c, mid, HudWhite, 1);
        Hint(font, list, K("L1", "Q"), c, hintTop, hintH, small, u);
        Button(list, HudButton.Play, x + btn, pillTop, x + btn * 2, pillTop + pillH, u);
        c = x + btn * 1.5f;
        if (_replayHeld || _replayDone)
        {
            Tri(list, c - s * 0.8f, mid - s * 1.1f, c - s * 0.8f, mid + s * 1.1f, c + s, mid, HudWhite, 1);
        }
        else
        {
            Rect(list, c - s * 0.9f, mid - s, c - s * 0.25f, mid + s, HudWhite, 1);
            Rect(list, c + s * 0.25f, mid - s, c + s * 0.9f, mid + s, HudWhite, 1);
        }
        Hint(font, list, K("X", "SPACE"), c, hintTop, hintH, small, u);
        Button(list, HudButton.Forward, x + btn * 2, pillTop, x + btn * 3, pillTop + pillH, u);
        c = x + btn * 2.5f;
        Tri(list, c, mid - s, c, mid + s, c + s * 1.2f, mid, HudWhite, 1);
        Tri(list, c - s * 1.2f, mid - s, c - s * 1.2f, mid + s, c, mid, HudWhite, 1);
        Hint(font, list, K("R1", "E"), c, hintTop, hintH, small, u);
        x += transportW + gap;

        // slower, the speed, faster
        Pill(list, x, pillTop, x + speedW, pillTop + pillH, u, false);
        float bar = 3.5f * u, th = 0.8f * u;
        bool slowest = _replaySpeed == 0, fastest = _replaySpeed == ReplaySpeeds.Length - 1;
        Button(list, HudButton.Slower, x, pillTop, x + btn, pillTop + pillH, u);
        c = x + btn / 2;
        Rect(list, c - bar, mid - th, c + bar, mid + th, HudWhite, slowest ? 0.35f : 1);
        Hint(font, list, K("L2", "Z"), c, hintTop, hintH, small, u);
        Label(font, list, speed, x + btn + speedTextW / 2, pillTop, pillH, text, HudPale, 1);
        Button(list, HudButton.Faster, x + btn + speedTextW, pillTop, x + speedW, pillTop + pillH, u);
        c = x + speedW - btn / 2;
        Rect(list, c - bar, mid - th, c + bar, mid + th, HudWhite, fastest ? 0.35f : 1);
        Rect(list, c - th, mid - bar, c + th, mid + bar, HudWhite, fastest ? 0.35f : 1);
        Hint(font, list, K("R2", "C"), c, hintTop, hintH, small, u);
        x += speedW + gap;

        // previous player, who's watched, next player
        Pill(list, x, pillTop, x + whoW, pillTop + pillH, u, false);
        float ah = 3.5f * u;
        Button(list, HudButton.PrevSeat, x, pillTop, x + btn, pillTop + pillH, u);
        c = x + btn / 2;
        Tri(list, c + ah * 0.6f, mid - ah, c + ah * 0.6f, mid + ah, c - ah * 0.8f, mid, HudWhite, 1);
        Hint(font, list, K("UP", "UP"), c, hintTop, hintH, small, u);
        Label(font, list, who, x + btn + whoTextW / 2, pillTop, pillH, text, HudWhite, _replayCam == ReplayCamKind.Free ? 0.5f : 1);
        Button(list, HudButton.NextSeat, x + whoW - btn, pillTop, x + whoW, pillTop + pillH, u);
        c = x + whoW - btn / 2;
        Tri(list, c - ah * 0.6f, mid - ah, c - ah * 0.6f, mid + ah, c + ah * 0.8f, mid, HudWhite, 1);
        Hint(font, list, K("DOWN", "DOWN"), c, hintTop, hintH, small, u);
        x += whoW + gap;

        // hide the bar and the HUD
        Pill(list, x, pillTop, x + hideW, pillTop + pillH, u, false);
        Button(list, HudButton.Hide, x, pillTop, x + hideW, pillTop + pillH, u);
        Label(font, list, "HIDE", x + hideW / 2, pillTop, pillH, text, HudOrange, 1);
        Hint(font, list, K("B", "H"), x + hideW / 2, hintTop, hintH, small, u);

        _replayHudDrawer.Draw(list, width, height, smooth: false); // crisp: the font atlas bleeds its neighbours when filtered
    }

    // a button's touch area, and (picked with the D-pad) its highlight: a lit fill inside an orange edge
    void Button(UiDrawList list, HudButton b, float x0, float y0, float x1, float y1, float u)
    {
        _hudRects[(int)b] = (x0, y0, x1, y1);
        if (_hudFocus != (int)b) return;
        float e = Math.Max(1, 0.9f * u);
        Rect(list, x0, y0, x1, y1, HudOrange, 0.95f);
        Rect(list, x0 + e, y0 + e, x1 - e, y1 - e, (0.35f, 0.16f, 0.02f), 0.9f);
    }

    // a hotkey's name: the pad's, or on a keyboard the key's
    string K(string pad, string key) => KeyboardHints ? key : pad;

    // a thick line (two triangles)
    static void Line(UiDrawList list, float x0, float y0, float x1, float y1, float w, (float R, float G, float B) c, float a)
    {
        float dx = x1 - x0, dy = y1 - y0, len = MathF.Max(MathF.Sqrt(dx * dx + dy * dy), 1e-3f);
        float nx = -dy / len * w / 2, ny = dx / len * w / 2;
        Tri(list, x0 + nx, y0 + ny, x1 + nx, y1 + ny, x1 - nx, y1 - ny, c, a);
        Tri(list, x0 + nx, y0 + ny, x1 - nx, y1 - ny, x0 - nx, y0 - ny, c, a);
    }

    // a pill: the REPLAYS buttons' dark fill inside a thin frame-orange edge
    static void Pill(UiDrawList list, float x0, float y0, float x1, float y1, float u, bool lit)
    {
        float e = Math.Max(1, 0.7f * u);
        Rect(list, x0, y0, x1, y1, lit ? HudOrange : HudFrame, 0.85f);
        Rect(list, x0 + e, y0 + e, x1 - e, y1 - e, (0.02f, 0.03f, 0.05f), 0.8f);
    }

    // a button's hotkey under it, on a small dark key cap
    static void Hint(MenuFont font, UiDrawList list, string key, float cx, float top, float h, float scale, float u)
    {
        float w = font.MeasureCanvas(key, scale) + 5 * u;
        Rect(list, cx - w / 2, top, cx + w / 2, top + h, (0, 0, 0), 0.65f);
        Label(font, list, key, cx, top, h, scale, HudPale, 0.95f);
    }

    static void Label(MenuFont font, UiDrawList list, string s, float cx, float top, float h, float scale,
        (float R, float G, float B) c, float a)
        => font.EmitCanvas(s, cx, top + (h - MenuFont.Cell * scale) / 2, 2, scale, c.R, c.G, c.B, a, list);

    static void Rect(UiDrawList list, float x0, float y0, float x1, float y1, (float R, float G, float B) c, float a)
        => list.Quad(-1, x0, y0, x1, y1, 0, 0, 0, 0, c.R, c.G, c.B, a);

    static void Tri(UiDrawList list, float x0, float y0, float x1, float y1, float x2, float y2, (float R, float G, float B) c, float a)
        => list.Triangle(-1, UiWrap.Clamp, UiWrap.Clamp, UiBlend.Alpha, new UiVertex(x0, y0, 0, 0, c.R, c.G, c.B, a),
            new UiVertex(x1, y1, 0, 0, c.R, c.G, c.B, a), new UiVertex(x2, y2, 0, 0, c.R, c.G, c.B, a));
}
