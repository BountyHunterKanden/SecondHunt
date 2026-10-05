using Android.Opengl;
using Android.Util;
using Javax.Microedition.Khronos.Opengles;
using System.Collections.Concurrent;
using System.Diagnostics;
using MphRead;
using MphRead.Formats.Sound;
using MphRecomp.Frontend;
using MphRecomp.Media;

namespace MphRecomp.App;

// Runs the front end (FrontendSession) on this GL thread and draws its UiDrawList (UiGlDrawer). Input arrives from
// the UI thread through a queue; requests (start Adventure...) go back out through RequestSink. Movies the menus ask
// for (the intro after the logos) play right here (MoviePlayer + MoviePresenter): the menus wait on the title until
// the movie ends, and any button or touch skips it, as on the DS.
internal sealed class FrontendRenderer : Java.Lang.Object, GLSurfaceView.IRenderer
{
    readonly string _filesDir;
    readonly string _settingsPath, _modsDir, _savesDir;
    FrontendSession? _session;
    string? _error;
    readonly ConcurrentQueue<Action<FrontendSession>> _input = new();
    readonly Stopwatch _clock = new();
    readonly UiGlDrawer _drawer = new();
    MoviePlayer? _movie;
    MoviePresenter? _presenter;
    double _last;
    int _width = 1, _height = 1;

    // the boot/logo + menu themes + UI feedback sounds; see GameAudio.cs. Neither the front end's Music.* game state
    // nor a Scene/SoundSource exists here, so these are driven directly rather than through MusicPlayer.Host /
    // Sfx.SetHost (the campaign's seam -- see CampaignActivity.cs)
    AndroidMusicPlayer? _music;
    AndroidSfxPlayer? _sfx;
    PageTune? _currentTune;

    public Action<FrontendRequest>? RequestSink;
    public Action<string>? StatusSink;
    public int StartPage { get; set; }
    // --es fedump 1: write this device's menu graph (MenuDump) here once loaded, to compare ROM revisions
    public string? DumpPath { get; set; }
    // the HD Samus backdrop (MenuSamusRender.cs): the imported HD models (<external files>/hd) and where its picture is
    // cached; set by GameActivity
    public string? HdRoot { get; set; }
    public string? CacheDir { get; set; }
    MenuSamusRender? _menuSamus;

    // settingsPath: the recomp settings (RECOMP SETTINGS under Options); modsDir: the installed mods (MODS);
    // savesDir: the campaign's saves (the file select's files A/B/C)
    public FrontendRenderer(string filesDir, string settingsPath, string modsDir, string savesDir)
    {
        _filesDir = filesDir;
        _settingsPath = settingsPath;
        _modsDir = modsDir;
        _savesDir = savesDir;
    }

    public void Post(Action<FrontendSession> action) => _input.Enqueue(action);

    public void OnSurfaceCreated(IGL10? gl, Javax.Microedition.Khronos.Egl.EGLConfig? config)
    {
        _drawer.Init();
        _presenter?.ResetGl();
        if (_session == null && _error == null)
        {
            try
            {
                var sw = Stopwatch.StartNew();
                Directory.SetCurrentDirectory(_filesDir);
                Paths.UpdatePaths();
                Paths.ChooseMphPath();
                _session = FrontendSession.Load(Paths.FileSystem, settingsPath: _settingsPath, modsDir: _modsDir, savesDir: _savesDir);
                _session.Log = m => Log.Info("MPHFrontend", m);
                _session.Request = OnRequest;
                _sfx = new AndroidSfxPlayer();
                _sfx.LoadTables();
                // the front end steps sound scripts every DS frame (60 Hz; BizHawk, USA rev 1: ADVENTURE_MODE_SCR's
                // entries at delays 1/5/9/13/17/18 start 2/6/10/14/18/19 frames after the press), the game proper
                // every other frame -- the script delays MphRead reads are in 30 Hz units. Its sound call (ov0
                // 0x21028d8) gives a script the one free-script slot: StopFreeScripts(1), then PlayScript.
                _session.PlaySound = id =>
                {
                    if ((id & 0x4000) != 0) _sfx?.StopUiScripts();
                    _sfx?.PlayUi(id, scriptRate: 60);
                };
                // the title menus run no echo (the capture effect isn't started there); a campaign may have left it on
                MphRecomp.Frontend.DsEcho.Stop();
                _music = new AndroidMusicPlayer(_session.Settings.Music);
                Log.Info("MPHFrontend", $"audio ready (music {_session.Settings.Music})");
                if (_session.Audio != null) _session.Audio.SfxTest = PlaySfxTest;
                _session.Start(StartPage);
                if (StartPage == 0 && FirstLogoSeq(_session) is SeqId logoSeq)
                {
                    _music.Prepare(logoSeq); // loaded during the Nintendo logo, so it starts on the PRESENTS page change
                }
                if (DumpPath != null)
                {
                    string root = Paths.FileSystem;
                    MenuFile romFile = MenuFile.Load(Path.Combine(root, "frontend", "metroidhunters.bin"));
                    File.WriteAllText(DumpPath, MenuDump.Describe(romFile, MenuStrings.Load(root),
                        new MenuWidgets(romFile, new UiTextureCache()), $"ROM {Paths.MphKey} (device)"));
                    Log.Info("MPHFrontend", "menu dump -> " + DumpPath);
                }
                Log.Info("MPHFrontend", $"front end loaded in {sw.ElapsedMilliseconds} ms (ROM {Paths.MphKey}, " +
                    $"{_session.Menu.File.Pages.Count} pages, page 5 has {_session.Menu.File.Pages[5].Items.Count} items)");
            }
            catch (Exception ex)
            {
                _error = ex.ToString();
                Log.Error("MPHFrontend", _error);
                StatusSink?.Invoke("Front end failed to load:\n" + ex.Message);
            }
        }
        _clock.Restart();
        _last = 0;
    }

    public void OnSurfaceChanged(IGL10? gl, int width, int height)
    {
        _width = Math.Max(1, width);
        _height = Math.Max(1, height);
        GLES30.GlViewport(0, 0, _width, _height);
    }

    // RECOMP SETTINGS "menu art" HD: the backdrop's picture, made once (a few frames of warm-up, then one draw)
    void StepMenuSamus(FrontendSession session)
    {
        // a public build (Beta 1, imports held) keeps the DS picture
        if (BuildFlags.Public || HdRoot == null || CacheDir == null || session.HasHdBackdrop || !session.Settings.MenuArtHd) return;
        _menuSamus ??= new MenuSamusRender(HdRoot, CacheDir, System.Math.Max(_width, _height));
        if (_menuSamus.Done) return;
        if (_menuSamus.Step() is { } pic) session.SetHdBackdrop(pic.W, pic.H, pic.Rgba);
    }

    public void OnDrawFrame(IGL10? gl)
    {
        GLES30.GlClearColor(0, 0, 0, 1);
        GLES30.GlClear(GLES30.GlColorBufferBit);
        FrontendSession? session = _session;
        if (session == null) return;
        double now = _clock.Elapsed.TotalSeconds;
        double dt = now - _last;
        _last = now;
        if (_movie != null)
        {
            UpdateMenuMusic(null, session); // never overlap the intro movie's own audio
            DrawMovie(dt);
            return;
        }
        while (_input.TryDequeue(out var action))
        {
            _idleSince = -1; // any input restarts the title's idle count (StepAttract)
            try { action(session); }
            catch (Exception ex) { Log.Error("MPHFrontend", "input: " + ex); }
        }
        // RECOMP SETTINGS > MUSIC and OPTIONS > AUDIO apply live (a few compares a frame): the music quality (the menu theme
        // carries on in the new mode) and the volumes -- the audio page's own values while it's open, else the saved ones
        _music?.SetQuality(session.Audio?.Quality ?? session.Settings.Music);
        float musicGain = AndroidMusicPlayer.MasterGain(session.Audio?.MusicVolume ?? 9);
        if (_music != null) _music.MasterVolume = musicGain;
        if (_sfx != null)
        {
            _sfx.SfxMasterVolume = AndroidMusicPlayer.MasterGain(session.Audio?.SfxVolume ?? 9);
            _sfx.StreamMasterVolume = musicGain; // the title theme is a stream
            // the audio page's SFX test has played out: its stop button goes back to play
            if (session.Audio is { SfxTestPlaying: true } audio
                && !(_sfxTestStream ? _sfx.StreamPlaying : _sfxTestId >= 0 && _sfx.CountPlayingSfx(_sfxTestId) > 0))
            {
                audio.OnSfxTestEnded();
            }
        }
        StepMenuSamus(session);
        try
        {
            session.Update(dt);
            // nothing else drives the front end's sound player: without this, sound scripts (every 0x4000 id: touch to
            // start, the menu icons, back, create/cancel) never got past their first Update and stayed silent, and
            // finished instances were never cleared
            _sfx?.Update((float)dt);
            session.Build(_width, _height);
        }
        catch (Exception ex)
        {
            Log.Error("MPHFrontend", "frame: " + ex);
            return;
        }
        if (_movie != null || StepAttract(session))
        {
            UpdateMenuMusic(null, session);
            return; // the logos just ended, or the title sat idle: straight to the movie, no title frame first
        }
        UpdateMenuMusic(DesiredMenuMusic(session), session);
        _drawer.Draw(session.DrawList, _width, _height, session.Settings.SmoothMenus);
    }

    // Boot/logo music the way the game picks it: each page's music comes from the ROM's page table (IDS.DAT +0,
    // MenuSounds.PageMusic), id 0 = keep what plays. The logos: P0/P1 (the Nintendo logo) have none, P2 (PRESENTS)
    // starts SEQ 16 DRONE (~28.5 s: it ends just before the intro movie), P3-P5 keep it. BizHawk USA rev 1 from
    // power-on (2026-10-01; same table in rev 0): P0 f64, P1 f324, P2 f516 -> PlaySeq 16 at f517, P3 f994, P4 f1380,
    // P5 f1768, StopMusic + intro movie f2269-2271; in our 30 Hz ticks (f / 2): P2 258, movie ~1135 (ours: P2 246,
    // movie 1107). DRONE used to start with page 0, 7.3 s before P2, so it ran that far ahead of the logos and was
    // over ~7 s before the movie (the owner's "starts at the wrong part"). The same table for every page after: the
    // title (P13) plays STRM 11 BGM_TITLE_MUSIC (21 s, no loop; vanilla's title leaves for the attract movie after 630
    // idle frames, 10.5 s), P14-P17 keep it, the menus from P18 play SEQ 43 CHUTNEY, the results pages P47-P50 SEQ 6
    // RESULTS. The game's MusicMgrFrame (ov0 0x2102E00) starts a page's music once the old one has stopped (each fade
    // in the table is 0: at once), by PlayStream(id, 0) for a STRM (flags & 2) or PlaySeqSimple(id).
    const long FlashTickTarget = 1135;
    int _musicPage = -1;
    PageTune? _pageTune;

    // a page's music: a SEQ on the music player, or a STRM on the SFX mixer's stream player (GameAudio.cs); Tracks: a
    // MusicId's tracks of its SEQ; Test: the audio page's music test (its PLAY count, so PLAY again starts it over)
    readonly record struct PageTune(int Id, bool Stream, ushort Tracks = 0xFFFF, int Test = 0);

    IReadOnlyList<MusicTrack>? _musicInfo;

    // OPTIONS > AUDIO's music test: a BGMSELECTLIST entry as a tune (kind 0 a SEQ, 1 a MusicId = a SEQ + tracks, 2 a STRM)
    PageTune TestTune(SoundTestEntry e, int serial)
    {
        if (e.Kind == 2) return new PageTune(e.Id, true, Test: serial);
        if (e.Kind == 1)
        {
            _musicInfo ??= SoundRead.ReadInterMusicInfo();
            if (e.Id >= 0 && e.Id < _musicInfo.Count) return new PageTune((int)_musicInfo[e.Id].SeqId, false, _musicInfo[e.Id].Tracks, serial);
        }
        return new PageTune(e.Id, false, Test: serial);
    }

    // OPTIONS > AUDIO's SFX test: one at a time (the next, or STOP, ends the last); a sample, a DGN at its listed curve
    // input, a script at its listed rate (full = the front end's 60 Hz, else half), or kind 3: an announcer voice, SDAT
    // STRM 0-9 (VOICE_CONSECUTIVE_KILLS ... VOICE_MULTI_NODE) on the stream player
    int _sfxTestId = -1;
    bool _sfxTestStream;

    void PlaySfxTest(SoundTestEntry? e)
    {
        if (_sfx == null) return;
        if (_sfxTestId >= 0) _sfx.StopSoundById(_sfxTestId);
        if (_sfxTestStream) _sfx.StopStream();
        _sfxTestId = -1;
        _sfxTestStream = false;
        if (e is not SoundTestEntry t) return;
        switch (t.Kind)
        {
        case 0:
            _sfxTestId = t.Id;
            _sfx.PlayUi(t.Id);
            break;
        case 1:
            _sfxTestId = 0x8000 | t.Id;
            _sfx.PlayDgn(_sfxTestId, source: null, loop: false, noUpdate: false, recency: -1, cancellable: false, t.Param, t.Param);
            break;
        case 2:
            _sfxTestId = 0x4000 | t.Id;
            _sfx.StopUiScripts();
            _sfx.PlayUi(_sfxTestId, scriptRate: t.Param != 0 ? 60 : 30);
            break;
        case 3:
            _sfxTestStream = true;
            _sfx.PlayFreeStream(t.Id);
            break;
        }
    }

    PageTune? DesiredMenuMusic(FrontendSession session)
    {
        MenuPage? page = session.Menu.Page;
        if (page == null)
        {
            return null;
        }
        // the game's SetPage starts the new page's music at once, while the old page's items still play out (P1's ESRB
        // notice fades for 16 frames after DRONE starts)
        int index = session.Menu.PendingPage >= 0 ? session.Menu.PendingPage : page.Index;
        // the audio page's music test owns the music while it plays (and after STOP, silence) until the page is left
        if (index == AudioOptions.Page && session.Audio is AudioOptions audio)
        {
            if (audio.MusicTest is SoundTestEntry test) return TestTune(test, audio.MusicTestSerial);
            if (audio.MusicTestStopped) return null;
        }
        if (index != _musicPage)
        {
            _musicPage = index;
            (int id, _, int flags) = session.Menu.Sounds.PageMusic(index);
            if (id != 0)
            {
                _pageTune = new PageTune(id, (flags & 2) != 0);
            }
            else if (_pageTune == null && index >= 13)
            {
                // started mid-menus on a page that keeps the music, with none chosen yet: the menu theme
                _pageTune = new PageTune((int)SeqId.CHUTNEY, false);
            }
        }
        return _pageTune;
    }

    // the first music the boot pages ask for (P2's DRONE), to load ahead
    static SeqId? FirstLogoSeq(FrontendSession session)
    {
        for (int page = 0; page < 13; page++)
        {
            (int id, _, int flags) = session.Menu.Sounds.PageMusic(page);
            if (id != 0)
            {
                return (flags & 2) == 0 ? (SeqId)id : null;
            }
        }
        return null;
    }

    // the page's music: changes only with the table's entry (same id + kind = no change), stops for the intro movie
    // (above; null) and pauses while the campaign owns the foreground (PauseAudio, below -- GameActivity.OnPause fires
    // when CampaignActivity starts, same as when the whole app backgrounds)
    void UpdateMenuMusic(PageTune? desired, FrontendSession session)
    {
        if (desired == _currentTune)
        {
            return;
        }
        PageTune? old = _currentTune;
        _currentTune = desired;
        if (old is { Stream: true })
        {
            _sfx?.StopStream();
        }
        if (desired is not PageTune tune)
        {
            _music?.Stop();
            return;
        }
        if (tune.Stream)
        {
            _music?.Stop();
            _sfx?.PlayFreeStream(tune.Id);
            Log.Info("MPHAudio", $"page music: stream {tune.Id} at our tick {session.Frame}");
            return;
        }
        if ((SeqId)tune.Id == SeqId.DRONE)
        {
            Log.Info("MPHAudio", $"logo music start: our tick {session.Frame} (vanilla: P2 + 1 frame = tick ~258)");
        }
        _music?.PlaySeq((SeqId)tune.Id, tune.Tracks);
    }

    // GameActivity.OnPause / OnResume
    public void PauseAudio()
    {
        _music?.Pause();
        _sfx?.SetPaused(true);
    }

    public void ResumeAudio()
    {
        if (_currentTune is { Stream: false } && _music != null)
        {
            _music.Play(_music.Volume);
        }
        _sfx?.SetPaused(false);
    }

    // on the GL thread (the session raises requests from inside its tick)
    void OnRequest(FrontendRequest r)
    {
        if (r.Kind == FrontendRequestKind.PlayMovie && r.Arg != null)
        {
            if (StartMovie(r.Arg))
            {
                // page 5 "logofinal" reached Idle and fired the (0,8) call that starts this movie -- vanilla starts it
                // at tick ~1135 (see DesiredMenuMusic's comment)
                Log.Info("MPHAudio", $"logo end (movie start): our tick {_session?.Frame} (vanilla ~{FlashTickTarget})");
            }
            return;
        }
        RequestSink?.Invoke(r);
    }

    bool StartMovie(string name)
    {
        try
        {
            _movie?.Dispose();
            _movie = MoviePlayer.Open(name);
            _presenter ??= new MoviePresenter();
            Log.Info("MPHFrontend", $"movie {name}: {_movie.FrameCount} frames, {_movie.Duration:0.0} s");
            while (_input.TryDequeue(out _)) { } // a button that skipped the logos doesn't also skip the movie
            return true;
        }
        catch (Exception ex)
        {
            Log.Error("MPHFrontend", $"movie {name}: {ex.Message}");
            _movie = null;
            return false;
        }
    }

    // The title's ATTRACT, as vanilla (BizHawk USA rev 1 from title.State, no input; ov0 0x21249DC, ov1 mode 4): the
    // title page (P13) left alone for more than 630 front-end ticks (30 Hz: 21.0 s, the title stream's own length, so
    // the stream has just played out) -> MenuSndMute, StopStream(5), StopMusic(5) -> mode 4 plays movie 0 "Opening"
    // (01_top/01_bot, 82.6 s) -> the front end starts over straight on P13 (the title comes in from white, its stream
    // from the top) -> and again, for as long as nobody touches it. Any button or touch skips the movie back to the
    // title, the same as its end. The count starts with the title settled and restarts on any input.
    const long AttractIdleTicks = 630;
    long _idleSince = -1;

    bool StepAttract(FrontendSession session)
    {
        if (session.Menu.Page?.Index != 13 || session.Menu.PendingPage >= 0)
        {
            _idleSince = -1;
            return false;
        }
        if (_idleSince < 0)
        {
            _idleSince = session.Frame;
            return false;
        }
        if (session.Frame - _idleSince <= AttractIdleTicks)
        {
            return false;
        }
        _idleSince = -1;
        Log.Info("MPHAudio", $"title idle {AttractIdleTicks} ticks: attract movie at our tick {session.Frame}");
        if (_currentTune is { Stream: true })
        {
            _sfx?.StopStream(5);
        }
        _music?.Stop();
        _currentTune = null; // so the title's stream starts over once the movie is done
        if (!StartMovie("01"))
        {
            return false;
        }
        session.Menu.Enter(13); // the title starts over behind the movie, as vanilla's front end restarts on P13
        return true;
    }

    void DrawMovie(double dt)
    {
        MoviePlayer movie = _movie!;
        if (!_input.IsEmpty)
        {
            while (_input.TryDequeue(out _)) { }
            movie.Skip();
            Log.Info("MPHFrontend", $"movie skipped at {movie.Position:0.0} s");
        }
        movie.Update(dt);
        _presenter!.Draw(movie, _width, _height);
        if (movie.Finished || movie.Error != null)
        {
            var sw = Stopwatch.StartNew();
            _presenter.Stop();
            movie.Dispose();
            Log.Info("MPHFrontend", $"movie done ({(movie.Error ?? (movie.Skipped ? "skipped" : "ended"))}), teardown {sw.ElapsedMilliseconds} ms");
            _movie = null;
            _clock.Restart(); // the menus pick up where they were, without catching up
            _last = 0;
        }
    }
}
