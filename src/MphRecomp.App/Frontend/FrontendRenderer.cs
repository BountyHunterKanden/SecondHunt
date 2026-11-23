using System.Collections.Concurrent;
using System.Diagnostics;
using MphRead;
using MphRead.Formats.Sound;
using MphRecomp.Frontend;
using MphRecomp.Media;
using MphRecomp.PrimeMusic;

namespace MphRecomp.App;

// Runs the front end (FrontendSession) on this GL thread and draws its UiDrawList (UiGlDrawer). Input arrives from
// the UI thread through a queue; requests (start Adventure...) go back out through RequestSink. Movies the menus ask
// for (the intro after the logos) play right here (MoviePlayer + MoviePresenter): the menus wait on the title until
// the movie ends, and any button or touch skips it, as on the DS.
internal sealed class FrontendRenderer : IGlScreen
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
    GameMusicPlayer? _music;
    // seconds of menu music rendered ahead (GameMusicPlayer's bufferSeconds; the campaign keeps 0.25)
    const float MenuBufferSeconds = 1f;
    GameSfxPlayer? _sfx;
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
    // CONTROLS > TOUCH > EDIT TOUCH LAYOUT's opener (GameActivity: the Android touch layout editor); null hides the row
    public Action? EditTouchLayout { get; set; }
    MenuSamusRender? _menuSamus;
    int _menuArtVersion;
    // IMPORT GAMES' pictures of imported models (ImportItemRender.cs; Importer menu visual design's, reached in 2026-10-08)
    readonly ImportItemRender _importRender = new();
    // the character + skin picker's pictures (CharacterPictureRender.cs; Character + skin picker (D9)'s, reached in 2026-10-08)
    readonly CharacterPictureRender _characterRender = new();
    // LOOK > MENUS ARENA BACKGROUND (MenuArenaBackdrop.cs): the live arena behind the menus; dropped before anything that
    // starts a game (one MphRead scene at a time) and made again a while after the menus come back
    MenuArenaBackdrop? _arenaBackdrop;
    bool _arenaBackdropFailed;
    // (on its own clock: _clock restarts with the surface and after each movie, which pushed a hold back by its length)
    double _arenaBackdropHoldUntil;
    bool _arenaBackdropAway; // a game was started from the menus: the next resume is a return from it
    readonly Random _arenaRng = new();
    // the hunters' models and effects read ahead on a worker (StepPrewarm), so the first match loads like a later one
    Task? _prewarm;
    CancellationTokenSource? _prewarmCancel;
    double _prewarmBegun;
    static bool s_prewarmDone; // (or a game was started: from then on its own loads are kept)
    static double HoldClock => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;

    // settingsPath: the recomp settings (RECOMP SETTINGS under Options); modsDir: the installed mods (MODS);
    // savesDir: the campaign's saves (the file select's files A/B/C)
    public FrontendRenderer(string filesDir, string settingsPath, string modsDir, string savesDir)
    {
        _filesDir = filesDir;
        _settingsPath = settingsPath;
        _modsDir = modsDir;
        ImportRenders.HostRenders = true;
        CharacterPictures.HostRenders = true;
        _savesDir = savesDir;
    }

    public void Post(Action<FrontendSession> action) => _input.Enqueue(action);

    // the control customizer is waiting for a button ("pad" / "keys", else null): the host posts its next press as
    // s.Recomp.CaptureInput(name) instead of Press / Navigate (read off the GL thread: a reference, at worst a frame old)
    public string? CaptureDevice => _session?.Recomp.CaptureDevice;

    // the name keyboard / address keypad is up (FrontendSession.Typing): a PC keyboard's keys go to TypeChar / TypeKey
    // (read off the GL thread as CaptureDevice: at worst a frame old)
    public bool Typing => _session?.Typing == true;

    public void OnSurfaceCreated()
    {
        // an arena background a game began as it left (MenuArenaBackdrop.BeginEarly) finishes before the front end loads
        // (one MphRead scene at a time; the first frame waits for it anyway)
        if (MenuArenaBackdrop.EarlyPending) MphRecomp.Campaign.CampaignHost.WaitMenuBackdrop();
        // a new context: the program names a match's context linked are gone (the HD Samus backdrop, the character
        // pictures and the import renders drew nothing after a match until the app restarted, 2026-10-17)
        CampaignGlCache.Reset();
        _drawer.Init();
        DropArenaBackdrop(); // (its GL objects were the old context's; the next frame makes a new one)
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
                // START's lead-in: a local match builds on a worker meanwhile (Campaign/MatchPrebuild.cs)
                _session.Multiplayer.LeadInSetup = OnLeadIn;
                _session.Multiplayer.LeadInFinish = MatchPrebuild.Wait;
                _session.Recomp.EditTouchLayout = EditTouchLayout;
                _sfx = new GameSfxPlayer();
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
                // a 1 s music buffer: a Mono major GC after a match froze the pump ~0.45 s, which ran a 0.25 s buffer
                // dry (menu music skipped in the LAN lobby, 2026-11-06). Menu fades go through the track volume, so
                // they stay immediate
                _music = new GameMusicPlayer(_session.Settings.Music, MenuBufferSeconds);
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

    public void OnSurfaceChanged(int width, int height)
    {
        _width = Math.Max(1, width);
        _height = Math.Max(1, height);
        GLES30.GlViewport(0, 0, _width, _height);
    }

    // RECOMP SETTINGS "menu art" HD: the backdrop's picture, made once (a few frames of warm-up, then one draw)
    void StepMenuSamus(FrontendSession session)
    {
        // public builds too since 2026-10-07 (owner: "include that"; the Beta 1 hold kept the DS picture)
        if (HdRoot == null || CacheDir == null || !session.Settings.MenuArtHd || _prewarm != null) return;
        // a new "menu art" pick (FrontendSession.MenuArtVersion): drawn again once the one in hand is finished
        if (_menuSamus is { Done: true } && _menuArtVersion != session.MenuArtVersion) _menuSamus = null;
        if (_menuSamus == null)
        {
            if (session.HasHdBackdrop && _menuArtVersion == session.MenuArtVersion) return;
            _menuArtVersion = session.MenuArtVersion;
            _menuSamus = new MenuSamusRender(HdRoot, CacheDir, System.Math.Max(_width, _height), session.Settings.MenuArtSuit);
        }
        if (_menuSamus.Done) return;
        if (_menuSamus.Step() is { } pic) session.SetHdBackdrop(pic.W, pic.H, pic.Rgba);
    }

    public void OnDrawFrame()
    {
        GLES30.GlClearColor(0, 0, 0, 1);
        GLES30.GlClear(GLES30.GlColorBufferBit);
        FrontendSession? session = _session;
        if (session == null) return;
        ThreadAllocProbe.Report("render"); // dev probe: allocations by thread, every 10 s (Diagnostics/ThreadAllocProbe.cs)
        double now = _clock.Elapsed.TotalSeconds;
        double dt = now - _last;
        _last = now;
        long frameStart = Stopwatch.GetTimestamp();
        if (_backFrames > 0) _backFrameDt = dt;
        // (as OnSurfaceCreated: a build a game began as it left is done before the queued page start reads MphRead)
        if (MenuArenaBackdrop.EarlyPending) MphRecomp.Campaign.CampaignHost.WaitMenuBackdrop();
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
        float musicGain = GameMusicPlayer.MasterGain(session.Audio?.MusicVolume ?? 9);
        if (_music != null) _music.MasterVolume = musicGain;
        if (_sfx != null)
        {
            _sfx.SfxMasterVolume = GameMusicPlayer.MasterGain(session.Audio?.SfxVolume ?? 9);
            _sfx.StreamMasterVolume = musicGain; // the title theme is a stream
            // the audio page's SFX test has played out: its stop button goes back to play
            if (session.Audio is { SfxTestPlaying: true } audio
                && !(_sfxTestStream ? _sfx.StreamPlaying : _sfxTestId >= 0 && _sfx.CountPlayingSfx(_sfxTestId) > 0))
            {
                audio.OnSfxTestEnded();
            }
        }
        StopPrewarmIfNeeded(session);
        // (a match building during START's lead-in has MphRead: these wait for the menus' next visit)
        bool leadIn = MatchPrebuild.Holding;
        if (!leadIn) StepMenuSamus(session);
        StepArenaBackdrop(session);
        if (_prewarm == null && !leadIn) // (MphRead is one scene at a time: these wait for a prewarm still going)
        {
            _importRender.Step(_width, _height);
            _characterRender.Step(_width, _height);
        }
        if (!leadIn) StartPrewarm(session);
        try
        {
            session.Update(dt);
            // nothing else drives the front end's sound player: without this, sound scripts (every 0x4000 id: touch to
            // start, the menu icons, back, create/cancel) never got past their first Update and stayed silent, and
            // finished instances were never cleared
            // a slow frame (a page's first build) must not make a playing sound script catch up in one step: its notes would
            // all fire at once, "only parts of the sound" (owner 10-26, ONLINE SETTINGS button)
            if (dt > 0.1) Log.Info("MPHFrontend", $"slow frame: {dt * 1000:F0} ms (page {session.Menu.Page?.Index})");
            _sfx?.Update((float)Math.Min(dt, 1.0 / 30));
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
        // START into a match: the music fades out with the screen's fade to black (MultiplayerMenu.IntoGameDim; vanilla
        // StopMusic(20) on page 17, C1 2026-10-24); a start that fails puts the menus back, and the music with them
        float intoDim = session.Multiplayer.IntoGameDim;
        if (intoDim > 0 && !_intoFading) // (0 = page 17's hold before the fade)
        {
            _intoFading = true;
            _music?.FadeOut(IntoGameMusicFade);
        }
        else if (intoDim < 0 && _intoFading)
        {
            _intoFading = false;
            _music?.ResetFade();
        }
        UpdateMenuMusic(DesiredMenuMusic(session), session);
        if (_arenaBackdrop != null && session.ArenaBackdrop)
        {
            _arenaBackdrop.Draw(dt, _width, _height, session.Menu.Page?.Index ?? -1, _drawer);
            GLES30.GlViewport(0, 0, _width, _height);
        }
        _drawer.Draw(session.DrawList, _width, _height, false); // (owner 2026-10-26: always the sharp menu art, SMOOTH MENUS row removed)
        if (_backFrames > 0)
        {
            _backFrames--;
            Log.Info("MPHMenu", $"back from a game, frame {4 - _backFrames}: {_backFrameDt * 1000:0} ms since the last,"
                + $" {Stopwatch.GetElapsedTime(frameStart).TotalMilliseconds:0} ms of work");
        }
    }

    // the menus' first frames back from a game, timed (where a hitch on the way back goes, owner 11-21)
    int _backFrames;
    double _backFrameDt;

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

    // a MusicId (InterMusicInfo: a SEQ and the tracks it plays) as a tune
    PageTune MusicIdTune(int musicId, int serial = 0)
    {
        _musicInfo ??= SoundRead.ReadInterMusicInfo();
        return musicId >= 0 && musicId < _musicInfo.Count
            ? new PageTune((int)_musicInfo[musicId].SeqId, false, _musicInfo[musicId].Tracks, serial)
            : new PageTune((int)SeqId.CREDITS, false, Test: serial);
    }

    // OPTIONS > AUDIO's music test: a BGMSELECTLIST entry as a tune (kind 0 a SEQ, 1 a MusicId = a SEQ + tracks, 2 a STRM)
    PageTune TestTune(SoundTestEntry e, int serial)
    {
        if (e.Kind == 2) return new PageTune(e.Id, true, Test: serial);
        if (e.Kind == 1)
        {
            _musicInfo ??= SoundRead.ReadInterMusicInfo();
            if (e.Id >= 0 && e.Id < _musicInfo.Count) return MusicIdTune(e.Id, serial);
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
            if (id == 0 && index == GameEnding.CreditsPage && session.Ending.Current is { } ending)
            {
                // after an ending the front end opens on the credits with the ending's own music (Reports R6: there was
                // none): ov0 0x2125354, as it enters page 6 from the game, plays MusicId 66 SEQ_CREDITS_M66 after the good
                // ending, 65 SEQ_CREDITS_M65 after the bad one (0x206e078); pages 7-12 keep it, the results page (49 / 50)
                // has its own (SEQ 6 RESULTS). Options' CREDITS keep the menu theme, as vanilla's.
                _pageTune = MusicIdTune(ending.Good ? 66 : 65);
            }
            else if (id != 0)
            {
                _pageTune = new PageTune(id, (flags & 2) != 0);
            }
            else if (_pageTune == null && index >= 13)
            {
                // started mid-menus on a page that keeps the music, with none chosen yet: the menu theme
                _pageTune = new PageTune((int)SeqId.CHUTNEY, false);
            }
            else if (_pageTune is { Id: (int)SeqId.RESULTS, Stream: false } && MphRecomp.Frontend.RecompMenus.IsOnlinePageIndex(index))
            {
                // the ONLINE PLAY pages (not in the ROM's table: they keep what plays) after an online match's RESULTS
                // (QUIT, or a license or chat page opened from there and back to the hub): the menu theme again, not the
                // results music for good (release test N2)
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
    // The menus' theme (SEQ 43 CHUTNEY) gives way to the player's own song (RecompSettings.MenuMusic: a Prime song
    // imported from their discs, or "shuffle": another each start; docs/PRIME_MUSIC.md) on the music player's stream
    // player; a shuffle draws from MENU SONGS (MenuHiddenSongs). A choice is resolved once, so a shuffle keeps its song
    // for the run. A song list's play box (RecompMenus.PreviewSong, owner 2026-10-29) plays its song in place of either
    // until it's paused or the list is left.
    Track? _menuSong, _menuSongPlaying;
    string? _menuSongChoice, _badPreview;

    Track? MenuSong(FrontendSession session)
    {
        string choice = session.Settings.MenuMusic ?? "";
        if (choice != _menuSongChoice)
        {
            _menuSongChoice = choice;
            _menuSong = MusicLibrary.Resolve(choice, Path.GetDirectoryName(_settingsPath), session.Settings.MenuHiddenSongs(), null, new Random());
        }
        return _menuSong;
    }

    void UpdateMenuMusic(PageTune? desired, FrontendSession session)
    {
        Track? preview = session.Recomp.PreviewSong is string id && id != _badPreview ? MusicLibrary.Find(id) : null;
        Track? song = preview ?? (desired is { Stream: false, Id: (int)SeqId.CHUTNEY } ? MenuSong(session) : null);
        if (song is { Hunters: true } own)
        {
            // MPH's own songs (HuntersMusic): the menus' sequence player plays them from the ROM, as a page's tune
            var (seq, tracks) = HuntersMusic.Tune(own);
            if (seq != SeqId.None) desired = new PageTune((int)seq, false, tracks);
            song = null;
        }
        if (desired == _currentTune && song == _menuSongPlaying)
        {
            return;
        }
        PageTune? old = _currentTune;
        _currentTune = desired;
        if (old is { Stream: true })
        {
            _sfx?.StopStream();
        }
        if (_menuSongPlaying != null && song != _menuSongPlaying)
        {
            _music?.Stream.Stop(0.5f);
            _menuSongPlaying = null;
        }
        if (song != null && _music != null)
        {
            try
            {
                _music.Stop();
                _music.Stream.Start(TrackCatalog.Open(Path.GetDirectoryName(_settingsPath)!, song), song.Gain, fadeIn: 0.01f, delay: 0,
                    oldFadeOut: 0.5f, repeat: true);
                _menuSongPlaying = song;
                Log.Info("MPHAudio", $"menu music: {song.Title} ({song.Id})");
                return;
            }
            catch (Exception ex)
            {
                Log.Warn("MPHAudio", $"menu music: {song.Id} won't open ({ex.Message}); the menu theme plays");
                if (song == preview) _badPreview = song.Id; // (not tried again each frame)
                else _menuSong = null;
            }
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

    // the screen is closing for good (the Windows host closes the menus while a game runs; Android finishes the
    // activity instead): its movie stops; the music fades out (or finishes the fade page 17 started) and the sound that
    // started the game plays to its end before the players stop -- Windows closes the menus the moment a match starts,
    // which cut both dead (C1 2026-10-24, owner: "the music does a hard cut and I'd like it to fade")
    public void Dispose()
    {
        StopPrewarm("the menus closed");
        DropArenaBackdrop();
        _presenter?.Stop();
        _movie?.Dispose();
        _movie = null;
        var music = _music;
        var sfx = _sfx;
        _music = null;
        _sfx = null;
        _pauseGeneration++; // (a pending PauseAudio step leaves them to this)
        if (music != null && !_intoFading) music.FadeOut(IntoGameMusicFade);
        _intoFading = false;
        System.Threading.Tasks.Task.Delay(SfxTailMs).ContinueWith(_ =>
        {
            music?.Dispose();
            sfx?.Dispose();
        });
    }

    // GameActivity.OnPause / OnResume
    // The menu sound that started the game (START GAME, a replay's PLAY) is still playing when the game's activity takes
    // the foreground; the sound mixer runs on its own thread, so it keeps going for SfxTailMs before it freezes. Freezing
    // at once cut the sound mid-note, and the rest of it came out when the menus came back (C1/C2, 2026-10-23).
    const int SfxTailMs = 1500;
    int _pauseGeneration;

    // The music fades out instead of stopping dead (owner 2026-10-24: "the music does a hard cut and I'd like it to fade"):
    // the fade started on page 17 carries on, an online start (no page 17) starts its own, and the player pauses once
    // it's silent. Vanilla: StopMusic(20), 20 frames.
    const float IntoGameMusicFade = 20 / 60f;
    bool _intoFading;

    public void PauseAudio()
    {
        int gen = ++_pauseGeneration;
        var music = _music;
        if (music != null && !_intoFading)
        {
            _intoFading = true;
            music.FadeOut(IntoGameMusicFade);
        }
        System.Threading.Tasks.Task.Delay((int)(IntoGameMusicFade * 1000) + 50).ContinueWith(_ =>
        {
            if (gen != _pauseGeneration) return; // resumed meanwhile
            music?.Pause();
        });
        var sfx = _sfx;
        if (sfx == null) return;
        System.Threading.Tasks.Task.Delay(SfxTailMs).ContinueWith(_ =>
        {
            if (gen != _pauseGeneration) return; // resumed meanwhile
            sfx.StopAll();
            sfx.SetPaused(true);
        });
    }

    public void ResumeAudio()
    {
        _pauseGeneration++;
        _intoFading = false;
        _music?.DropFadedQueue(); // (before ResetFade: the buffer written during the fade out is silence)
        _music?.ResetFade();
        if (_currentTune is { Stream: false } && _music != null)
        {
            _music.Play(_music.Volume);
        }
        _sfx?.SetPaused(false);
        // back from a game: the arena loads as soon as the game's scene is gone (CampaignRenderer.GameOpen; Android's
        // CampaignActivity drops it as it pauses to finish), not after a timed hold (owner 11-21: the 2 s hold left the menu
        // up on Samus, then loaded)
        if (_arenaBackdropAway) _arenaBackdropHoldUntil = 0;
    }

    const double ArenaBackdropRequestHold = 5;
    // a game whose Shutdown never came (it shouldn't happen): the arena loads anyway after this
    const double ArenaBackdropGameWait = 3;
    double _arenaBackdropGameSeen = -1;

    // a game's scene still holds MphRead (one scene at a time): the arena waits for its Shutdown
    bool GameSceneOpen()
    {
        if (!CampaignRenderer.GameOpen)
        {
            _arenaBackdropGameSeen = -1;
            return false;
        }
        if (_arenaBackdropGameSeen < 0) _arenaBackdropGameSeen = HoldClock;
        if (HoldClock - _arenaBackdropGameSeen < ArenaBackdropGameWait) return true;
        if (_arenaBackdropGameSeen > 0) Log.Warn("MPHMenu", $"arena background: a game still open after {ArenaBackdropGameWait} s, loading anyway");
        _arenaBackdropGameSeen = 0;
        return false;
    }

    // GL thread, each menu frame (setting on, not the in-game pause, else dropped): the arena's room starts building on
    // a worker from the first menu frame, whatever the startup screens do (owner 11-21: "everything is just sitting
    // waiting to be handed off"); it's taken once built, its GPU copies go up while the startup screens are still on,
    // and it shows from the title on (the Samus art meanwhile, if the build isn't done yet). Pages past the title
    // read MphRead, so leaving it waits for a build still going (one scene at a time).
    void StepArenaBackdrop(FrontendSession session)
    {
        int page = session.Menu.Page?.Index ?? 0;
        bool keep = session.Settings.MenuArenaBackdrop && !session.Paused;
        bool show = keep && page >= 13;
        // the still held since a game started (OnRequest) goes once the menus are back from it (ResumeAudio) or the start
        // failed (the hold ran out): the next arena loads in its place, this frame
        if (_arenaBackdrop is { Frozen: true } && _arenaBackdropAway && HoldClock >= _arenaBackdropHoldUntil) DropArenaBackdrop();
        if (!keep || (_arenaBackdrop != null && !_arenaBackdrop.Alive))
        {
            if (!keep) MenuArenaBackdrop.DropEarly();
            if (_arenaBackdrop != null) DropArenaBackdrop();
            if (!keep) _arenaBackdropFailed = false; // (turned off and on again: another try)
        }
        else if (_arenaBackdrop == null && !_arenaBackdropFailed && HoldClock >= _arenaBackdropHoldUntil && _width > 0
            && !GameSceneOpen())
        {
            _arenaBackdropAway = false;
            var sw = Stopwatch.StartNew();
            _arenaBackdrop = MenuArenaBackdrop.Prepare(Path.GetDirectoryName(_settingsPath), session.Textures, _arenaRng);
            _arenaBackdropFailed = _arenaBackdrop == null;
            if (_arenaBackdrop != null)
            {
                _arenaBackdrop.BeginLoad(_filesDir, _width, _height);
                _arenaBackdropBegun = _clock.Elapsed.TotalSeconds;
                Log.Info("MPHMenu", $"arena background {_arenaBackdrop.Arena} loading from page {page} ({sw.ElapsedMilliseconds} ms)");
                // begun on the title or past it (back from a game, no startup screens): taken in this frame, so the menu's
                // first frame has the arena, never a frame of Samus before it (owner 11-21)
                if (page >= 13) TakeArenaBackdrop(page);
            }
        }
        else if (_arenaBackdrop is { Loading: true } && (_arenaBackdrop.LoadDone || page > 13))
        {
            TakeArenaBackdrop(page);
        }
        else if (_arenaBackdrop is { IsLive: true } && !show)
        {
            _arenaBackdrop.Warm(); // (its first shown frame then has nothing to upload)
        }
        session.ArenaBackdrop = show && _arenaBackdrop is { IsLive: true };
    }

    // GL thread: the arena's room taken from its worker (waited for, if it's still building) and made live
    void TakeArenaBackdrop(int page)
    {
        if (_arenaBackdrop is not { Loading: true }) return;
        if (_arenaBackdrop.TookEarly || page > 13) _backFrames = 4; // (time the frames that follow)
        var sw = Stopwatch.StartNew();
        bool waited = !_arenaBackdrop.LoadDone;
        string arena = _arenaBackdrop.Arena;
        if (!_arenaBackdrop.GoLive(_arenaRng))
        {
            DropArenaBackdrop();
            _arenaBackdropFailed = true;
        }
        Directory.SetCurrentDirectory(_filesDir);
        Log.Info("MPHMenu", $"arena background {(_arenaBackdrop != null ? arena : arena + " failed")} live on page {page},"
            + $" {(_clock.Elapsed.TotalSeconds - _arenaBackdropBegun) * 1000:0} ms after it began"
            + $" (taken in {sw.ElapsedMilliseconds} ms{(waited ? ", waited for the worker" : "")})");
        _last = _clock.Elapsed.TotalSeconds; // (a wait or the HD finish isn't a frame's time)
    }

    double _arenaBackdropBegun;

    // Faster first match (11-21, SceneSetup.HostPrewarmHunterResources): while the menus show no MphRead page (the startup
    // screens and the title) and nothing else has MphRead (no arena background, the HD Samus picture finished, no game
    // yet), a worker parses the 8 hunters' models and effects and decodes their textures; Read keeps them, so the first
    // match's room load finds them as a later match does. Best effort, never waited for: leaving the title, a request or
    // the arena background setting cancels it before its next model (what it read stays kept). The look-alike and colour
    // sets go on first (HunterColours.Install, as the match's own skins do), or the match would drop what it read
    void StartPrewarm(FrontendSession session)
    {
        if (_prewarm is { IsCompleted: true } done)
        {
            bool ran = done.Status == TaskStatus.RanToCompletion;
            if (ran || done.Exception?.InnerException is not OperationCanceledException) s_prewarmDone = true;
            Log.Info("MPHMenu", ran ? $"hunters read ahead in {(HoldClock - _prewarmBegun) * 1000:0} ms"
                : $"hunters read ahead: {done.Exception?.InnerException?.Message ?? "stopped"}");
            _prewarm = null;
            _prewarmCancel?.Dispose();
            _prewarmCancel = null;
            return;
        }
        if (_prewarm != null || s_prewarmDone || !PrewarmAllowed(session) || _arenaBackdrop != null) return;
        if (_menuSamus is { Done: false }) return;
        var cts = _prewarmCancel = new CancellationTokenSource();
        CancellationToken ct = cts.Token;
        _prewarmBegun = HoldClock;
        _prewarm = Task.Factory.StartNew(() =>
        {
            try { Thread.CurrentThread.Priority = ThreadPriority.BelowNormal; } catch (Exception) { }
            MphRecomp.Multiplayer.HunterColours.Install();
            SceneSetup.HostPrewarmHunterResources(ct);
        }, ct, TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }

    static bool PrewarmAllowed(FrontendSession session) =>
        (session.Menu.Page?.Index ?? 0) <= 13 && session.Menu.PendingPage <= 13 && !session.Paused
        && !session.Settings.MenuArenaBackdrop;

    void StopPrewarmIfNeeded(FrontendSession session)
    {
        if (_prewarm != null && !PrewarmAllowed(session)) StopPrewarm($"page {session.Menu.Page?.Index}");
    }

    // cancelled and waited for (at most the model it's on): MphRead is the caller's from here
    void StopPrewarm(string why)
    {
        if (_prewarm == null) return;
        var sw = Stopwatch.StartNew();
        _prewarmCancel?.Cancel();
        try { _prewarm.Wait(); } catch (AggregateException) { }
        if (!_prewarm.IsCompletedSuccessfully)
        {
            Log.Info("MPHMenu", $"hunters read ahead: stopped by {why} after {(HoldClock - _prewarmBegun) * 1000:0} ms"
                + $" (waited {sw.ElapsedMilliseconds} ms)");
        }
        else
        {
            s_prewarmDone = true;
            Log.Info("MPHMenu", $"hunters read ahead in {(HoldClock - _prewarmBegun) * 1000:0} ms (taken by {why})");
        }
        _prewarm = null;
        _prewarmCancel?.Dispose();
        _prewarmCancel = null;
        _last = _clock.Elapsed.TotalSeconds; // (a wait isn't a frame's time)
    }

    void DropArenaBackdrop()
    {
        _arenaBackdrop?.Dispose();
        _arenaBackdrop = null;
        if (_session != null) _session.ArenaBackdrop = false;
    }

    // GL thread, START's tap (MultiplayerMenu.LeadInSetup; null: that start failed): the match builds on a worker while the
    // lead-in plays (MatchPrebuild). MphRead is the worker's from here: the hunters' read-ahead stops, and the arena
    // background's scene goes, its last blurred picture held still under the lead-in (MenuArenaBackdrop.Freeze)
    void OnLeadIn(string? line)
    {
        if (line == null)
        {
            MatchPrebuild.Drop("the start failed");
            if (_arenaBackdrop is { Frozen: true }) DropArenaBackdrop(); // (a new one loads for the lobby)
            return;
        }
        if (MatchPrebuild.Dirs == null) return; // (a shell that doesn't take one)
        StopPrewarm("START's lead-in");
        _arenaBackdrop?.Freeze();
        MatchPrebuild.Begin(line, _width, _height);
    }

    // on the GL thread (the session raises requests from inside its tick)
    void OnRequest(FrontendRequest r)
    {
        // a match built for the lead-in is the match screen's only for its own start
        if (MatchPrebuild.Holding && !(r.Kind == FrontendRequestKind.StartMatch && r.Arg == MatchPrebuild.Line))
        {
            MatchPrebuild.Drop($"request {r.Kind}");
        }
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
        StopPrewarm($"request {r.Kind}");
        // a game starts (or the pause's tools open over one): its scene needs MphRead to itself
        if (r.Kind is not (FrontendRequestKind.CheckUpdates or FrontendRequestKind.OnlineRules or FrontendRequestKind.ShareCrashLog
            or FrontendRequestKind.ShareLog or FrontendRequestKind.ShareReplay or FrontendRequestKind.SaveLog
            or FrontendRequestKind.ExportDsSave or FrontendRequestKind.ControlsDone or FrontendRequestKind.QuitPressed))
        {
            // the arena's scene goes, its last blurred picture held still under the menus until they leave (as START's
            // lead-in does: a start with none, TRY CONTROLS, showed the Samus art until the game came up; owner 11-22)
            if (_arenaBackdrop is { CanHold: true }) _arenaBackdrop.Freeze();
            else DropArenaBackdrop();
            s_prewarmDone = true;
            // (the menus go behind the game within it; ResumeAudio holds it again on the way back. A start that fails
            // leaves the menus up: the arena comes back after this)
            _arenaBackdropHoldUntil = HoldClock + ArenaBackdropRequestHold;
            _arenaBackdropAway = true;
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
        // the input still runs, with the session told a movie is up: only a press (button, D-pad, key, touch down) asks
        // to skip it. Not any input at all: the release of the A or tap that started a MOVIES tile's movie (and the
        // push-to-talk check every pad button's release posts) skipped it straight back to the menu
        bool skip = false;
        if (_session is { } session)
        {
            session.MovieUp = true;
            while (_input.TryDequeue(out var action))
            {
                try { action(session); }
                catch (Exception ex) { Log.Error("MPHFrontend", "input: " + ex); }
            }
            session.MovieUp = false;
            skip = session.SkipMovieAsked;
            session.SkipMovieAsked = false;
        }
        else if (!_input.IsEmpty)
        {
            while (_input.TryDequeue(out _)) { }
            skip = true;
        }
        if (skip)
        {
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
            if (_session is { } s) s.Movies.OnMovieDone(s.Menu); // a MOVIES tile's movie: its page comes in again
        }
    }
}
