using Android.Util;
using Android.Views;
using System;
using MphRead;
using MphRecomp.Media;

namespace MphRecomp.App;

// In-game cutscenes: planet landings and take-offs, boss intros and defeats, the octolith, Gorea, the endings. With
// Scene.HostPlaysMovies on, MphRead leaves each movie "playing" instead of skipping it; this plays it with MoviePlayer
// + MoviePresenter while the sim is held (the game is paused during movies anyway, and no stray input reaches it), then
// SkipMovie hands back to MphRead, which runs the movie's own after-fade and after-action (room load, the ship, the
// end of the game) -- a chained second movie arrives as a new HostMovieSerial.
// Pad: A / B / R2 skip, Y cycles the two-screen layout (side by side / top screen / stacked).
// --es movies off: skip them as before (quick testing).
internal sealed partial class CampaignRenderer
{
    MoviePlayer? _movie;
    MoviePresenter? _moviePresenter;
    int _movieSerial;
    // the scene _movieSerial counts in: every session (a landing, the cockpit view) is a new Scene whose HostMovieSerial
    // starts again at 0, so a second landing's movie #1 would otherwise look already shown and never play (black screen)
    Scene? _movieScene;
    bool _movieHolds;
    // the clock isn't running (paused, or the pause menu is open): the sound waits too
    bool _movieFrozen;
    bool _mPrevSkip = true, _mPrevY = true;
    static MoviePresenter.Layout _movieLayout = MoviePresenter.Layout.SideBySide;

    public static void ConfigureMovies(Android.Content.Intent? intent)
    {
        Scene.HostPlaysMovies = intent?.GetStringExtra("movies") != "off";
    }

    // true while a movie is on screen and not yet finished: the step loop leaves the sim alone
    bool MovieHoldsSim => _movieHolds;
    bool MovieShowing => _movie != null;

    // once per rendered frame, after the sim steps (seconds = 0 while paused)
    void MovieUpdate(double seconds)
    {
        if (_movieHolds)
        {
            // the sim is held: don't bank the time (the step loop would fast-forward it at 4 steps a frame afterwards)
            _accum = 0;
        }
        if (_host == null || _error != null)
        {
            StopMovie();
            _movieScene = null;
            return;
        }
        Scene scene = _host.Scene;
        if (scene != _movieScene)
        {
            _movieScene = scene;
            _movieSerial = 0;
            StopMovie();
        }
        if (scene.MoviePlaying && scene.HostMovieSerial != _movieSerial)
        {
            _movieSerial = scene.HostMovieSerial;
            StopMovie();
            try
            {
                _movie = MoviePlayer.Open(scene.HostMovie);
                _movieHolds = true;
                _mPrevSkip = _mPrevY = true; // a button still held from gameplay doesn't skip it
                Log.Info("MPHCampaign", $"movie {scene.HostMovie}");
            }
            catch (Exception ex)
            {
                Log.Warn("MPHCampaign", $"movie {scene.HostMovie} unavailable ({ex.Message}): skipped");
                scene.SkipMovie();
                return;
            }
        }
        if (_movie == null)
        {
            return;
        }
        // paused / pause menu open (the renderer passes 0): no skip or layout input -- the menu's own A ("Resume")
        // mustn't skip the movie -- and a button still held when it closes needs a release first
        _movieFrozen = seconds <= 0 && _movie.Started;
        if (_movieFrozen)
        {
            _mPrevSkip = _mPrevY = true;
        }
        else
        {
            PadState pad = Pad;
            bool skip = pad[Keycode.ButtonA] || pad[Keycode.ButtonB] || pad[Keycode.ButtonR2] || pad.R2 >= 0.5f;
            bool y = pad[Keycode.ButtonY];
            if (_movieHolds && skip && !_mPrevSkip)
            {
                _movie.Skip();
            }
            if (y && !_mPrevY)
            {
                _movieLayout = (MoviePresenter.Layout)(((int)_movieLayout + 1) % 3);
            }
            _mPrevSkip = skip;
            _mPrevY = y;
        }
        _movie.Update(seconds);
        if (_movieHolds && _movie.Finished)
        {
            if (_movie.Error != null)
            {
                Log.Warn("MPHCampaign", $"movie {scene.HostMovie}: {_movie.Error}");
            }
            Log.Info("MPHCampaign", $"movie {scene.HostMovie} {(_movie.Skipped ? "skipped" : "ended")} at frame "
                + $"{_movie.FrameIndex + 1}/{_movie.FrameCount}, {_movie.Position:0.00} of {_movie.Duration:0.00} s");
            // the sim runs again: MphRead fades out and runs the after-action; the last frame stays up meanwhile
            _movieHolds = false;
            scene.SkipMovie();
        }
        if (!_movieHolds && !scene.MoviePlaying)
        {
            StopMovie();
        }
    }

    void MovieDraw()
    {
        _moviePresenter ??= new MoviePresenter();
        _moviePresenter.DualLayout = _movieLayout;
        _moviePresenter.Draw(_movie!, _width, _height, paused: _movieFrozen);
    }

    string MovieStatus() => _movie == null || !_movieHolds ? ""
        : $"{_host?.Scene.HostMovie}  A skip   Y layout ({_movieLayout})";

    void StopMovie()
    {
        _moviePresenter?.Stop();
        _movie?.Dispose();
        _movie = null;
        _movieHolds = false;
    }

    // the GL context was recreated: the presenter's textures/program are gone
    void ResetMovieGl()
    {
        _moviePresenter?.Stop();
        _moviePresenter = null;
    }
}
