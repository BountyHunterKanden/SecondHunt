using System.Collections.Concurrent;
using System.Diagnostics;
using MphRead;
using MphRecomp.FirstHunt;
using MphRecomp.FirstHunt.Movies;
using MphRecomp.Frontend;

namespace MphRecomp.App;

// First Hunt's own front end (docs/FIRST_HUNT_PORT.md P4; Core FirstHunt/Frontend/FhFrontend.cs) on one screen, laid
// out by FhScreenLayout: the one-screen layout (the touch screen's controls over the top screen's picture, its
// description at the bottom) or, with the MENU LAYOUT setting on side by side, the DS's two screens side by side. The pages' sounds and music play through the cart's banks (GameSfxPlayer.LoadFirstHunt). A training game
// picked on the Training page goes out through GameSink (the activity starts the campaign screen with fhlevel + fhmenu),
// a deathmatch from the lobby pages too (net + fhmenu, over LanSession.Current); the result comes back as
// <saves>/firsthunt_last.txt (FirstHuntSession.cs), read by ReturnFromGame. The cart's movies (P6) play here through the
// app's MoviePresenter (FhMoviePlayer decodes them), where the cart plays them: the opening on entering First Hunt (the
// cart's power-on, 0x2030de8) and after 40 s idle on the title, the teaser from the title's Movie hex and the training
// hi score's PLAY MOVIE, a spawn movie before every game (training: yellow; a deathmatch: the local slot's colour,
// 0x2046eb8) and the death movie after a Survivor run (0x202e800, before its hi score page). A / Start / a touch skips
// one (0x2036868); either way what follows is the same.
internal sealed class FirstHuntMenuRenderer : IGlScreen
{
    readonly string _filesDir, _dataDir, _settingsPath;
    readonly ConcurrentQueue<Action<FhFrontend>> _input = new();
    readonly Stopwatch _clock = new();
    readonly UiGlDrawer _drawer = new();
    readonly List<WidgetTri> _tris = new();
    FhFrontend? _menus;
    UiDrawList? _list;
    GameSfxPlayer? _sfx;
    string? _error;
    double _last, _accum;
    FhGameStart? _lastGame;
    readonly FhScreenLayout _layout = new();
    MoviePresenter? _presenter;
    FhMoviePlayer? _movie;
    volatile bool _skipMovie;
    Action? _afterMovie;
    bool _paused;

    public Action<FhGameStart>? GameSink;
    public Action? ExitSink;
    public Action<string>? StatusSink;

    public FirstHuntMenuRenderer(string filesDir, string dataDir, string settingsPath)
    {
        _filesDir = filesDir;
        _dataDir = dataDir;
        _settingsPath = settingsPath;
    }

    public string SavesDir => Path.Combine(_dataDir, "saves");
    public static string ResultFile(string savesDir) => Path.Combine(savesDir, "firsthunt_last.txt");

    public void Post(Action<FhFrontend> action) => _input.Enqueue(action);

    public void OnSurfaceCreated()
    {
        _drawer.Init();
        if (_menus == null && _error == null)
        {
            try
            {
                var sw = Stopwatch.StartNew();
                Directory.SetCurrentDirectory(_filesDir);
                Paths.UpdatePaths();
                Paths.ChooseMphPath();
                if (!FirstHuntGame.Register(_dataDir))
                {
                    throw new InvalidOperationException("First Hunt isn't imported (" + FirstHuntGame.Dir(_dataDir) + ")");
                }
                Directory.CreateDirectory(SavesDir);
                _sfx = new GameSfxPlayer();
                _sfx.LoadTables();
                _sfx.LoadFirstHunt();
                MphRead.Sound.Sfx.SetHost(_sfx);
                var settings = MphRecomp.Config.RecompSettings.Load(_settingsPath);
                _layout.Wide = settings.FirstHuntMenuLayout != "sidebyside";
                _sfx.SfxMasterVolume = GameMusicPlayer.MasterGain(settings.SfxVolume);
                var menus = new FhFrontend(Path.Combine(SavesDir, "firsthunt.sav"));
                menus.Sound = i =>
                {
                    int id = FirstHuntSounds.Menu(i);
                    if (id >= 0)
                    {
                        MphRead.Sound.Sfx.Instance.PlaySample(id, source: null, loop: null, noUpdate: false, recency: -1,
                            sourceOnly: false, cancellable: false);
                    }
                };
                menus.Music = (track, loop) =>
                {
                    if (track < 0) FirstHuntSounds.StopMusic();
                    else if (FirstHuntSounds.MusicTrack != track) FirstHuntSounds.PlayMusic(track, loop);
                };
                menus.StartGame = g =>
                {
                    FirstHuntSounds.StopMusic();
                    _lastGame = g;
                    int slot = g.Mode == FhMode.Multiplayer && FhLobby.Lan?.Settings is MphRecomp.Multiplayer.MatchSettings st ? st.LocalSeat : 0;
                    StartMovie(FhMovie.SpawnYellow + Math.Clamp(slot, 0, 3), () => GameSink?.Invoke(g));
                };
                menus.Exit = () => ExitSink?.Invoke();
                menus.PlayMovie = m => StartMovie(m, () => _menus?.Start());
                _list = new UiDrawList(menus.Textures);
                _menus = menus;
                (FhTrainingResult? training, FhMatchResult? match) = TakeResult();
                if (training == null && match == null)
                {
                    // entering First Hunt = the cart's power-on: the opening, then the title
                    menus.Start();
                    StartMovie(FhMovie.Opening, () => _menus?.Start());
                }
                else
                {
                    ShowResult(menus, training, match);
                }
                Log.Info("MPHFirstHunt", $"First Hunt menus loaded in {sw.ElapsedMilliseconds} ms");
            }
            catch (Exception ex)
            {
                _error = ex.ToString();
                Log.Error("MPHFirstHunt", _error);
                StatusSink?.Invoke("First Hunt menus failed to load:\n" + ex.Message);
            }
        }
        _presenter?.ResetGl();
        _clock.Restart();
        _last = 0;
    }

    // on the GL thread: the movie replaces the menus until it ends or is skipped, then 'after' runs (at once if the
    // movie can't be opened)
    void StartMovie(FhMovie which, Action after)
    {
        FhMoviePlayer? movie = FhMoviePlayer.Open(which);
        if (movie == null)
        {
            Log.Warn("MPHFirstHunt", FhMoviePlayer.OpenError ?? $"movie {which} didn't open");
            after();
            return;
        }
        FirstHuntSounds.StopMusic();
        _presenter ??= new MoviePresenter();
        _skipMovie = false;
        _movie = movie;
        _afterMovie = after;
        Log.Info("MPHFirstHunt", $"movie {which}: {movie.FrameCount} frames, {movie.Duration:0.0} s, volume {movie.DsVolume}");
    }

    void EndMovie()
    {
        if (_movie != null)
        {
            Log.Info("MPHFirstHunt", $"movie {_movie.Name} {(_movie.Skipped ? "skipped" : "ended")} at frame {_movie.FrameIndex}"
                + (_movie.BadFrames > 0 ? $", {_movie.BadFrames} bad frames" : ""));
        }
        _presenter?.Stop();
        _movie = null;
        _skipMovie = false;
        Action? after = _afterMovie;
        _afterMovie = null;
        after?.Invoke();
    }

    // a finished game's page; a Survivor run (the cart: its end, always by dying) shows the death movie first
    void ShowResult(FhFrontend menus, FhTrainingResult? training, FhMatchResult? match)
    {
        if (training?.Mode == FhMode.Survivor)
        {
            StartMovie(FhMovie.Death, () => menus.Start(training, match));
        }
        else
        {
            menus.Start(training, match);
        }
    }

    // a finished game's result, once (FirstHuntSession.cs writes it): a training game's, or a deathmatch's ("mp ...")
    (FhTrainingResult?, FhMatchResult?) TakeResult()
    {
        string path = ResultFile(SavesDir);
        if (!File.Exists(path)) return (null, null);
        try
        {
            string line = File.ReadAllText(path);
            File.Delete(path);
            return FhMatchResult.IsMatchLine(line) ? (null, FhMatchResult.Parse(line)) : (FhTrainingResult.Parse(line), null);
        }
        catch (Exception ex)
        {
            Log.Warn("MPHFirstHunt", "result: " + ex.Message);
            return (null, null);
        }
    }

    // back from a game (Activity.OnResume after the campaign screen closes): its results page, or the Training page
    // after a quit from the pause menu; reloads the save the game wrote
    public void ReturnFromGame()
    {
        Post(m =>
        {
            // the game screen had its own player as Sfx.Instance (and the music handle was its)
            if (_sfx != null) MphRead.Sound.Sfx.SetHost(_sfx);
            FirstHuntSounds.StopMusic();
            m.Save.Reload();
            (FhTrainingResult? result, FhMatchResult? match) = TakeResult();
            if (result != null || match != null) ShowResult(m, result, match);
            else m.Show(_lastGame?.Mode == FhMode.Multiplayer ? FhMenuData.Multiplayer : FhMenuData.Training);
            _sfx?.SetPaused(false);
        });
    }

    public void Pause()
    {
        _paused = true;
        _sfx?.SetPaused(true);
        if (_movie != null) _presenter?.Audio(_movie, paused: true);
    }

    public void Resume()
    {
        _paused = false;
        _sfx?.SetPaused(false);
    }

    public void Dispose()
    {
        FirstHuntSounds.StopMusic();
        _presenter?.Dispose();
        _presenter = null;
        _sfx?.Dispose();
        _sfx = null;
    }

    int _width = 1, _height = 1;

    public void OnSurfaceChanged(int width, int height)
    {
        _width = Math.Max(1, width);
        _height = Math.Max(1, height);
        _layout.Resize(_width, _height);
    }

    // a touch in canvas px, as the touch screen's pixels (FhScreenLayout.ToTouch: side by side, a press off the touch
    // screen is dropped)
    public void Touch(bool down, float x, float y)
    {
        if (_movie != null)
        {
            if (down) _skipMovie = true;
            return;
        }
        if (!_layout.ToTouch(x, y, down, out float sx, out float sy)) return;
        Post(m => m.Touch(down, sx, sy));
    }

    public void Pad(FhPad pad)
    {
        if (_movie != null)
        {
            // the cart skips on A or START (key mask 9, 0x2036868)
            if ((pad & FhPad.Confirm) != 0) _skipMovie = true;
            return;
        }
        Post(m => m.Pad(pad));
    }

    public void OnDrawFrame()
    {
        double now = _clock.Elapsed.TotalSeconds;
        float dt = (float)Math.Min(0.1, now - _last);
        _last = now;
        GLES30.GlClearColor(0, 0, 0, 1);
        GLES30.GlClear(GLES30.GlColorBufferBit | GLES30.GlDepthBufferBit);
        FhFrontend? menus = _menus;
        if (menus == null || _list == null) return;
        if (_movie is FhMoviePlayer movie && _presenter != null)
        {
            if (_skipMovie) movie.Skip();
            movie.Update(_paused ? 0 : dt);
            if (movie.Finished)
            {
                EndMovie();
            }
            else
            {
                _presenter.Draw(movie, _width, _height, _paused);
                return;
            }
        }
        while (_input.TryDequeue(out Action<FhFrontend>? action)) action(menus);
        _accum += dt;
        int steps = 0;
        while (_accum >= 1 / 60.0 && steps++ < 6)
        {
            menus.Tick();
            _accum -= 1 / 60.0;
        }
        if (steps >= 6) _accum = 0;
        _sfx?.Update(dt);
        _tris.Clear();
        menus.Evaluate(_tris);
        _list.Clear();
        _layout.Build(menus, _tris, _list, now);
        _drawer.Draw(_list, _width, _height, smooth: false);
    }
}
