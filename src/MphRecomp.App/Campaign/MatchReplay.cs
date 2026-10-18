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
    ReplayPlayer? _replayPlayer;
    // seeking back (T9): the match as it was every few seconds of the replay so far, restored in place of a restart
    ReplayCheckpoints? _replayCheckpoints;
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
            if (args.Get("replaydump") is string dump && dump.Length > 0)
            {
                string[] range = dump.Split('-');
                _replayDumpFrom = int.Parse(range[0]);
                _replayDumpTo = int.Parse(range[^1]);
            }
            _matchSettings = file.Settings;
            _hunter = file.Settings.Players[file.Settings.LocalSeat].Hunter;
            // a guest's copy of the host's recording: the camera starts on the guest's own seat (the host's view is the
            // recording's own first-person one)
            if (file.OwnSeat >= 0 && file.OwnSeat != file.Settings.LocalSeat) _replaySeat = file.OwnSeat;
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
            return played;
        }
        bool keep = ReplayDataDir != null && RecompSettings.Load(SettingsPath).RecordReplays
            && !MphRecomp.Multiplayer.MatchSettings.IsFirstHuntArena(settings.Arena); // (First Hunt's deathmatches aren't MPH replays)
        // a network match is recorded by its host only (a guest gets the host's file: StartReplayLink)
        bool record = keep && (_netSession == null || _netSession.IsHost)
            && !settings.Solo; // the dev launcher's solo run (SoloArena.cs) isn't a match worth keeping
        var recorder = record ? new ReplayRecorder(settings, MphRecomp.Update.UpdateHost.AppVersion) : null;
        CampaignHost host = CampaignHost.StartMatch(settings, collectDrawItems: true, viewWidth: width, viewHeight: height);
        if (_netSession != null) StartReplayLink(settings, keep);
        if (recorder != null)
        {
            if (_netSession != null) recorder.Started(host, _netSession.Kind);
            else recorder.Started(host.Scene);
            // room for the whole match up front (no list growth mid-match): its time limit, else 10 minutes
            float seconds = Math.Min(settings.TimeLimitSeconds ?? 600, 3600);
            recorder.File.Steps.Capacity = (int)(seconds * 60) + 600;
            _replayRecorder = recorder;
        }
        return host;
    }

    // NetStep (a local match): one step, recorded, or played back and checked before it can be drawn
    void StepReplayed(CampaignInput input)
    {
        if (_replayPlayer == null)
        {
            _host!.Step(input);
            _replayRecorder?.Step(input, _host.Scene);
            return;
        }
        if (_error != null || _replayDone)
        {
            return;
        }
        if (!_replayPlayer.TryNext(out CampaignInput recorded))
        {
            // the steps ran out before the match ended: a recording cut short (the match was left early)
            _replayDone = true;
            _error = $"REPLAY OVER at {ReplayClock(_replayPlayer.Position)}: the recording ends here (the match was left early)";
            Log.Info("MPHReplay", $"played to the end of an incomplete recording: {_replayPlayer.Position} steps, every one exact");
            return;
        }
        _host!.Step(recorded);
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
            // _error stops the sim and the drawing before this step is shown (CampaignRenderer.Render)
            _replayDone = true;
            _error = $"REPLAY STOPPED at {ReplayClock(_replayPlayer.FailedAt)}: from here it wouldn't play exactly as "
                + "recorded on this build and device";
            Log.Warn("MPHReplay", $"step {_replayPlayer.FailedAt} of {_replayPlayer.File.Steps.Count} differs from the recording");
        }
    }

    // TryEndMatch (complete: the match ended) and Shutdown (complete: false, the screen went first)
    void EndReplay(bool complete)
    {
        PlayerEntity.HostNoRespawnPrompt = false;
        ResetReplayViewHooks();
        // a guest waits for the host's recording only after a match that ran to its end with the host still there
        if (!complete || _netDriver?.PeerGone == true) _replayWanted = false;
        if (_replayPlayer != null)
        {
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
            // a network match's host: its recording to each guest that keeps replays (a complete match only)
            if (_replayLink != null && complete)
            {
                byte[] bytes = File.ReadAllBytes(path);
                foreach (int seat in _replayLink.SeatsWantingReplay)
                {
                    _replayLink.SendReplay(seat, bytes);
                }
                Log.Info("MPHReplay", $"sending the recording to {_replayLink.SeatsWantingReplay.Count} guest(s)");
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
        _netOwner = new ReplayLinkHold(_netOwner, link, () => link.IsHost ? link.Sending : _replayWanted && !_replayArrived
            && (_netSession as MphRecomp.Multiplayer.Net.NetMatchSession)?.Migrations is not > 0, link.IsHost ? 6000 : 8000);
    }

    // NetStep (MatchSession.cs), after a network match's step: its host records the step (its own input; the remote seats'
    // came through the host's taps during the step)
    void RecordNetStep(CampaignInput input)
    {
        if (_replayRecorder != null && _host != null) _replayRecorder.Step(input, _host.Scene);
    }

    // The match screen's hold on a network match's session: disposing it waits (off the caller's thread) while the replay link
    // still has its recording to send / receive, up to a limit, then closes the link and the session; with nothing in flight,
    // at once (the others hear BYE as before).
    sealed class ReplayLinkHold : IDisposable
    {
        readonly IDisposable? _inner;
        readonly ReplayLink _link;
        readonly Func<bool> _busy;
        readonly int _maxMs;
        int _disposed;

        public ReplayLinkHold(IDisposable? inner, ReplayLink link, Func<bool> busy, int maxMs)
        {
            (_inner, _link, _busy, _maxMs) = (inner, link, busy, maxMs);
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
                while (_busy() && sw.ElapsedMilliseconds < _maxMs) await System.Threading.Tasks.Task.Delay(50);
                Log.Info("MPHReplay", $"replay link done after {sw.ElapsedMilliseconds} ms{(_busy() ? " (gave up)" : "")}");
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
    // Seeking forward runs the steps undrawn, as fast as SeekBurst allows; back restarts the match from the recording
    // and runs up to there (no savestates: every step is still checked, so a seek is exact or refused like playback).
    static readonly double[] ReplaySpeeds = { 0.25, 0.5, 1, 2, 4 };
    const int ReplayNormalSpeed = 2;
    const int SkipSteps = 10 * 60, SeekBurst = 240;
    int _replaySpeed = ReplayNormalSpeed;
    bool _replayHeld;
    float _replaySfxVolume = -1;
    int _seekTarget = -1;
    readonly ConcurrentQueue<(float X, float Y, int Phase)> _replayTouches = new();
    readonly Stopwatch _replayClock = Stopwatch.StartNew();
    double _replayLastFrame;

    // the bar's buttons, in D-pad order
    enum HudButton { Camera, Back, Play, Forward, Slower, Faster, PrevSeat, NextSeat, Count }
    int _hudFocus = (int)HudButton.Play;
    readonly (float X0, float Y0, float X1, float Y1)[] _hudRects = new (float, float, float, float)[(int)HudButton.Count];
    (float X0, float Y0, float X1, float Y1) _scrubRect;
    bool _scrubbing;
    float _scrubFrac;
    readonly bool[] _replayPrev = new bool[12];

    // paused by the viewer (OnDrawFrame pauses the audio with it)
    public bool ReplayHeld => _replayPlayer != null && _replayHeld && !_replayDone && _seekTarget < 0;

    // a touch on the game (UI thread; CampaignActivity / the desktop's mouse): phase 0 down, 1 move, 2 up
    public void ReplayTouch(float x, float y, int phase) => _replayTouches.Enqueue((x, y, phase));

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
        if (!MatchOver)
        {
            if (left) _hudFocus = Math.Max(0, _hudFocus - 1);
            if (right) _hudFocus = Math.Min((int)HudButton.Count - 1, _hudFocus + 1);
            if (a) HudPress((HudButton)_hudFocus);
            if (x) HudPress(HudButton.Play);
            if (l1) HudPress(HudButton.Back);
            if (r1) HudPress(HudButton.Forward);
            if (l2) HudPress(HudButton.Slower);
            if (r2) HudPress(HudButton.Faster);
            if (up) HudPress(HudButton.PrevSeat);
            if (down) HudPress(HudButton.NextSeat);
            if (cam) HudPress(HudButton.Camera);
            while (_replayTouches.TryDequeue(out var t)) HudTouch(t.X, t.Y, t.Phase);
        }
        else
        {
            while (_replayTouches.TryDequeue(out _)) { }
        }
        if (_replayCam == ReplayCamKind.Free && _host != null)
        {
            FlyFreeCam(_host.Player, pad, dt); // flies paused too
        }
        if (_host != null) PlaceReplayView(_host.Scene); // a seat or the free cam, paused or not
        bool seeking = _seekTarget >= 0 && _seekTarget > _replayPlayer.Position && !_replayDone;
        if (_seekTarget >= 0 && !seeking)
        {
            _seekTarget = -1;
            _accum = 0;
            Log.Info("MPHReplay", $"at step {_replayPlayer.Position}");
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
            return SeekBurst * 60.0; // OnDrawFrame's dt times this: always more than ReplayStepCap's steps
        }
        return _replayHeld ? 0 : ReplaySpeeds[_replaySpeed];
    }

    // the most steps one frame may run: 4, or 4 per 1x of a faster replay; seeking, up to SeekBurst (not past the target)
    int ReplayStepCap => _replayPlayer == null ? 4
        : _seekTarget > _replayPlayer.Position ? Math.Min(SeekBurst, _seekTarget - _replayPlayer.Position)
        : Math.Max(4, (int)(4 * ReplaySpeeds[_replaySpeed]));

    string ReplaySpeedText => ReplaySpeeds[_replaySpeed] switch { 0.25 => "1/4x", 0.5 => "1/2x", double s => $"{s:0}x" };

    void HudPress(HudButton button)
    {
        if (_replayPlayer == null) return;
        _hudFocus = (int)button;
        switch (button)
        {
        case HudButton.Play:
            _replayHeld = !_replayHeld;
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
            break;
        }
        _statusCountdown = 0;
        Log.Info("MPHReplay", $"{button}: {(_replayHeld ? "paused" : "playing")} at {ReplaySpeedText}, {ReplayCamText}");
    }

    void HudTouch(float x, float y, int phase)
    {
        if (_replayPlayer == null) return;
        var s = _scrubRect;
        float frac = s.X1 > s.X0 ? Math.Clamp((x - s.X0) / (s.X1 - s.X0), 0, 1) : 0;
        if (phase == 0)
        {
            if (x >= s.X0 && x <= s.X1 && y >= s.Y0 && y <= s.Y1)
            {
                (_scrubbing, _scrubFrac) = (true, frac);
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
        else if (_scrubbing && phase == 1)
        {
            _scrubFrac = frac;
        }
        else if (_scrubbing && phase == 2)
        {
            _scrubbing = false;
            ReplaySeek((int)(frac * _replayPlayer.File.Steps.Count));
        }
    }

    // go to a step: forward runs there; back restarts the match from the recording first
    void ReplaySeek(int step)
    {
        if (_replayPlayer == null || _host == null) return;
        step = Math.Clamp(step, 0, _replayPlayer.File.Steps.Count);
        if (step < _replayPlayer.Position && !RestoreCheckpoint(step) && !RestartReplay()) return;
        _seekTarget = step;
        _replayDone = false;
        Log.Info("MPHReplay", $"seek to step {step} (from {_replayPlayer.Position})");
    }

    // after a checked step: a checkpoint when one's due (ReplayCheckpoints: every 5 s of play, once each)
    void TakeCheckpoint(CampaignHost host)
    {
        if (_replayCheckpoints == null || _replayPlayer == null) return;
        int before = _replayCheckpoints.Count;
        _replayCheckpoints.AfterStep(_replayPlayer.Position, host);
        if (_replayCheckpoints.Count != before && _replayCheckpoints.Count <= 3)
        {
            Log.Info("MPHReplay", $"checkpoint at step {_replayPlayer.Position}: {_replayCheckpoints.LastTakeMs:0.0} ms, "
                + $"{_replayCheckpoints.Count} kept, {_replayCheckpoints.Bytes >> 10} KB");
        }
    }

    // the step a restored match was put back to (-1: none since the last restart); a check failing after one breaks them
    int _restoredFrom = -1;

    // seek back: the latest checkpoint at or before the step, restored into the same match (false: none, restart)
    bool RestoreCheckpoint(int step)
    {
        if (_replayCheckpoints?.Before(step) is not SimSnapshot snap || _host == null || _replayPlayer == null) return false;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        snap.Restore();
        _replayPlayer.Rewind(snap.Step);
        _replayMarkTracker.Reset();
        _sfx?.StopAll(); // the later sounds stop (the restored match plays its own again)
        ResetDroneFx();
        _replayDone = false;
        _error = null;
        _restoredFrom = snap.Step;
        Log.Info("MPHReplay", $"seek to step {step}: checkpoint at step {snap.Step} restored ({sw.Elapsed.TotalMilliseconds:0.0} ms)");
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

    int WatchedSeat => _replaySeat >= 0 ? _replaySeat : _replayPlayer?.File.Settings.LocalSeat ?? 0;
    bool WatchingOwnView => _replayCam == ReplayCamKind.FirstPerson && WatchedSeat == (_replayPlayer?.File.Settings.LocalSeat ?? 0);

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

    bool PlaceReplayView(Scene scene)
    {
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
            if (!seatHead)
            {
                scene.HostView = new HostView(eye.Position, eye.Target, Vector3.UnitY, fov);
                return true;
            }
            Vector3 look = eye.Facing.LengthSquared > 0.0001f ? eye.Facing.Normalized() : Vector3.UnitZ;
            Vector3 view = p.IsBot ? BotEye(p, look) : look;
            PlayerEntity.HostFirstPersonTurn = Turn(look, view);
            scene.HostView = new HostView(eye.Position, eye.Position + view, Vector3.UnitY, fov);
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
        _freeCamYaw -= pad.Rx * FreeCamLookSpeed * dt;
        // the sticks as in play (BuildInput): stick Y is + down, so forward = -Ly; look up/down follows the invert setting
        float ry = _aimInvertY ? -pad.Ry : pad.Ry;
        _freeCamPitch = Math.Clamp(_freeCamPitch - ry * FreeCamLookSpeed * dt, -1.5f, 1.5f);
        Vector3 facing = FreeCamFacing;
        var right = Vector3.Cross(facing, Vector3.UnitY).Normalized();
        _freeCamPos += (facing * -pad.Ly + right * pad.Lx) * FreeCamSpeed * dt;
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
        if (_replayPlayer == null || MatchOver) return;
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
        float rowW = camW + transportW + speedW + whoW + 3 * gap;
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
            Tri(list, c - s * 0.8f, mid - s * 1.1f, c - s * 0.8f, mid + s * 1.1f, c + s, mid, HudWhite, _replayDone ? 0.4f : 1);
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
