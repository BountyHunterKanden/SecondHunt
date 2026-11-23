using System;
using MphRead;
using MphRead.Entities;
using MphRecomp.Campaign;
using MphRecomp.Frontend;
using OpenTK.Mathematics;

namespace MphRecomp.App;

// The menus' arena backdrop (LOOK > MENUS ARENA BACKGROUND, owner 2026-11-20; MenuArenaBackdrop.cs drives it): a
// multiplayer arena with nobody in it (CampaignHost.StartMenuBackdrop), drawn by this renderer from a fixed camera
// (MenuBackdropShots, else a spawn point) with no HUD, no gear, no audio of its own. It lives on the menus' GL thread
// and is dropped before a match or campaign starts (one MphRead scene at a time).
internal sealed partial class CampaignRenderer
{
    bool _menuBackdrop;
    (Vector3 Eye, Vector3 Target, float Fov) _backdropShot;

    public (Vector3 Eye, Vector3 Target, float Fov) BackdropShot => _backdropShot;
    public int Width => _width;
    public int Height => _height;

    public static CampaignRenderer ForMenuBackdrop(string filesDir) =>
        new(filesDir, "", ControlLayout.Prime, Hunter.Samus, gunDir: null) { _menuBackdrop = true };

    System.Threading.Tasks.Task<CampaignHost>? _backdropLoad;
    long _backdropLoadStart;

    // GL thread, after OnSurfaceCreated: the arena's scene starts building on a worker (CampaignHost.BeginMenuBackdrop);
    // BackdropFinish takes it once BackdropLoaded
    // (started: its build begun already, by a game leaving for the menus, MenuArenaBackdrop.BeginEarly)
    public void BackdropBegin(string arena, int width, int height,
        System.Threading.Tasks.Task<CampaignHost>? started = null, long startedAt = 0)
    {
        _width = width;
        _height = height;
        System.IO.Directory.SetCurrentDirectory(_filesDir);
        Paths.UpdatePaths();
        Paths.ChooseMphPath();
        _backdropLoadStart = started != null ? startedAt : System.Diagnostics.Stopwatch.GetTimestamp();
        if (started == null && SettingsPath != null)
        {
            // HOLIDAY MODE as the settings have it now, before the worker reads it (as a game's ReloadAimFromSettings): else
            // only a game's start applied it, so at app start the arena had no snow, and a change made in the menus
            // waited for the next game
            var s = MphRecomp.Config.RecompSettings.Load(SettingsPath);
            MphRecomp.Holiday.HolidaySnow.Apply(s.HolidayMode);
            MphRecomp.Holiday.HolidayBells.Apply();
            try { MphRecomp.Holiday.HolidayGround.CacheRoot ??= AppPlatform.CacheDir; } catch (Exception) { }
        }
        _backdropLoad = started ?? CampaignHost.BeginMenuBackdrop(arena, width, height);
        // the offscreen target it always draws into (CampaignRenderScale.cs), and its picked shot's depth of field
        // (CampaignRenderer.Dof.cs: the program, and textures for the target), made now as the scene's own programs
        // are (OnSurfaceCreated), so its first frame makes nothing
        int pct = RenderScalePct();
        (int w, int h) = RenderScaleSize(pct);
        bool dof = MenuBackdropShots.DofFor(arena).Blur > 0 && EnsureDof();
        if (dof && !AllocRenderScaleTarget(w, h, pct, textures: true))
        {
            _dofBroken = true;
            dof = false;
        }
        if (!dof) AllocRenderScaleTarget(w, h, pct, textures: false);
        // holiday mode's falling snow (CampaignRenderer.HolidaySnow.cs): its effects program and flake made now too
        if (MphRecomp.Holiday.HolidaySnow.Enabled && FxGl() && (_hsTex == 0 || !_u.ContainsKey("holidaysnow"))) MakeHolidayFlake();
    }

    public bool BackdropLoaded => _backdropLoad?.IsCompleted == true;

    // GL thread, once BackdropLoaded: the scene taken, its HD look made ready; false if it failed
    public bool BackdropFinish(string arena, Random rng, int spawn = -1)
    {
        var load = _backdropLoad;
        _backdropLoad = null;
        if (load == null) return false;
        var (music, roomMusic) = (MusicPlayer.Host, Music.RoomHost);
        try
        {
            _host = load.Result; // (throws the worker's failure)
            double built = System.Diagnostics.Stopwatch.GetElapsedTime(_backdropLoadStart).TotalMilliseconds;
            MusicPlayer.Host = null; // silent: the menus keep their own music (FrontendRenderer)
            Music.RoomHost = null;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            // (no shot: always the first spawn's view; a picked or built-in shot, with its FOV and depth of field, wins)
            _backdropShot = MenuBackdropShots.For(arena, _host.SpawnPoints(), rng,
                spawn >= 0 ? spawn : MenuBackdropShots.HasShot(arena) ? -1 : 0);
            _backdropDof = MenuBackdropShots.DofFor(_backdropShot);
            FinishArenaHdNow(); // an Echoes arena opens finished (CampaignArena.cs)
            long hdMs = sw.ElapsedMilliseconds;
            // a Prime 1 room's own snow (Shorelines; CampaignRenderer.Prime1Fx.cs), read now and its flake up, so the
            // first frame snows as a match does and reads nothing (BackdropFrame ticks it)
            StopPrime1Fx();
            _p1FxRoom = _host.RoomId;
            _p1Fx = LoadPrime1Fx();
            (_p1Time, _p1Step, _p1Drift) = (0, 0, Vector3.Zero);
            if (_p1Fx is { FlakeTex: not 0 } p1 && FxGl()) FxTexture(p1.FlakeTex);
            _backdropAccum = 1 / 60.0; // (its first drawn frame has a step behind it)
            _backdropTimes = $"worker {CampaignHost.LoadTimes} (taken {built:0} ms after it started), HD {hdMs} ms, room fx {sw.ElapsedMilliseconds - hdMs} ms";
            Log.Info("MPHMenu", $"menu backdrop: {arena} ({_host.SpawnPoints().Count} spawns{(spawn >= 0 ? $", spawn {spawn}" : "")})");
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn("MPHMenu", $"menu backdrop {arena} failed: {(ex is AggregateException { InnerException: { } inner } ? inner : ex)}");
            BackdropDispose();
            return false;
        }
        finally
        {
            MusicPlayer.Host = music;
            Music.RoomHost = roomMusic;
        }
    }

    // GL thread, each frame it's live but not on screen yet (the startup screens): the scene's meshes and textures go up
    // a few ms a frame, so its first drawn frame has nothing left to upload (WarmSceneGl, as under a movie)
    public bool BackdropWarm()
    {
        if (_host == null) return true;
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        bool done = WarmSceneGl(_host.Scene, MovieWarmMs);
        // the frame's first GL call waits out the last swap (~16 ms on PC), which spends a call's whole budget on one
        // upload: the budget counts again from after it (PC: 418 frames -> a few)
        long t1 = System.Diagnostics.Stopwatch.GetTimestamp();
        while (!done && System.Diagnostics.Stopwatch.GetElapsedTime(t1).TotalMilliseconds < MovieWarmMs)
        {
            done = WarmSceneGl(_host.Scene, MovieWarmMs - System.Diagnostics.Stopwatch.GetElapsedTime(t1).TotalMilliseconds);
        }
        _backdropWarmMs += System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
        _backdropWarmFrames++;
        if (done && !_backdropWarmLogged)
        {
            _backdropWarmLogged = true;
            _backdropTimes += $", warm-up {_backdropWarmMs:0} ms over {_backdropWarmFrames} frames";
            Log.Info("MPHMenu", $"menu backdrop warmed: {_backdropWarmMs:0} ms over {_backdropWarmFrames} frames ({_warmMeshN} meshes + {_warmTexN} textures"
                + $" of {_host.Scene.HostMeshes.Count} / {_host.Scene.HostTextures.Count})");
        }
        return done;
    }

    double _backdropWarmMs;
    int _backdropWarmFrames;
    bool _backdropWarmLogged;

    double _backdropAccum;
    string _backdropTimes = "";
    int _backdropFramesTimed;

    // GL thread: the room's steps for dt (60 a second as a match's, at most 4 a frame; none on a frame shorter than a step:
    // one every frame ran it fast on a faster screen, owner 11-21 "pickups move faster and bounce pads are animated
    // faster"), then the frame drawn onto the screen, by way of an offscreen target with depth + stencil
    // (BeginRenderScale: the menus' screen has neither)
    public void BackdropFrame(double dt, int width, int height)
    {
        if (_host == null) return;
        if (width != _width || height != _height)
        {
            _width = width;
            _height = height;
            _host.Scene.Size = new Vector2i(width, height);
        }
        var (music, roomMusic) = (MusicPlayer.Host, Music.RoomHost);
        MusicPlayer.Host = null;
        Music.RoomHost = null;
        PlayerEntity.HostNoViewer = true; // nobody's eyes: no fade, turret or grapple HUD
        try
        {
            _backdropAccum = Math.Min(_backdropAccum + dt, 4 / 60.0);
            while (_backdropAccum >= 1 / 60.0)
            {
                _host.Scene.SetHostCamera(_backdropShot.Eye, _backdropShot.Target, Vector3.UnitY, _backdropShot.Fov);
                _host.StepBackdrop();
                // the falling snow's clocks, as a match's TickSkins steps them: a Prime 1 room's own snow, holiday
                // mode's (owner 2026-11-22: the menus' arenas had ground snow but no snow falling)
                TickPrime1Fx();
                TickHolidaySnow();
                _backdropAccum -= 1 / 60.0;
            }
            GLES30.GlViewport(0, 0, width, height);
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            Render();
            if (_backdropFramesTimed < 3)
            {
                _backdropFramesTimed++;
                GLES30.GlReadPixels(0, 0, 1, 1, GLES30.GlRgba, GLES30.GlUnsignedByte, new byte[4]); // (waits for the draw: timing only, 3 frames)
                _backdropTimes += $", draw {_backdropFramesTimed} {System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds:0} ms";
                if (_backdropFramesTimed == 3) Log.Info("MPHMenu", "menu backdrop load times: " + _backdropTimes);
            }
        }
        catch (Exception ex)
        {
            Log.Warn("MPHMenu", "menu backdrop frame: " + ex);
            BackdropDispose();
        }
        finally
        {
            MusicPlayer.Host = music;
            Music.RoomHost = roomMusic;
            PlayerEntity.HostNoViewer = false;
        }
    }

    public bool BackdropAlive => _host != null;

    // GL thread: the scene and its GPU copies go (before a match starts, or when the menus close)
    public void BackdropDispose()
    {
        if (_backdropLoad != null)
        {
            // still building: it finishes (MphRead is one scene at a time), then goes
            CampaignHost.WaitMenuBackdrop();
            if (_backdropLoad.IsCompletedSuccessfully) _host = _backdropLoad.Result;
            _backdropLoad = null;
        }
        if (_host != null)
        {
            try { _host.Dispose(); } catch (Exception ex) { Log.Warn("MPHMenu", "menu backdrop dispose: " + ex.Message); }
            _host = null;
        }
        _arenaHd = null;
        _arenaHdLoad = null;
        _arenaHdRoom = -1;
        ReleaseSceneGpu();
        ReleaseBackdropTarget();
        PlayerEntity.HostNoViewer = false;
    }

    // the screen-sized target it drew into (BeginRenderScale: the menus' screen has no depth buffer), freed with it
    // (the depth of field's program stays: CampaignGlCache's)
    void ReleaseBackdropTarget()
    {
        if (_rsFbo != 0) GLES30.GlDeleteFramebuffers(1, new[] { _rsFbo }, 0);
        DeleteRenderScaleAttachments();
        if (_dofVao != 0) GLES30.GlDeleteVertexArrays(1, new[] { _dofVao }, 0);
        RenderScaleGlReset();
    }
}
