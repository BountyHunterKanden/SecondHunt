using MphRecomp.Frontend;

namespace MphRecomp.App;

internal sealed partial class CampaignRenderer
{
    // the host's: leaving this game opens the main menus (Android CampaignActivity, SecondHunt.Desktop StartGame)
    public bool LeavesToMenus { get; set; }

    bool _leavePending, _leaving;
    int _leaveFbo, _leaveTex, _leaveW, _leaveH;
    UiTextureCache? _leaveTextures;
    UiGlDrawer? _leaveDrawer;
    UiDrawList? _leaveList;
    int _leaveTexId;

    // GL thread, the pause menu just opened (PauseOverlay.Opened): the menus' next arena background picked and its
    // scene-free parts read on workers while the game is paused (MenuArenaBackdrop.ReadAhead; owner 11-22: back from a
    // game on a snowy arena as instant as launch). The same games as LeaveForMenusPressed
    public void LeaveAhead()
    {
        if (!LeavesToMenus || _leaving || _host == null || NetSession != null || SettingsPath == null) return;
        MenuArenaBackdrop.ReadAhead(SettingsPath);
    }

    // GL thread (the pause session's request): QUIT's YES pressed
    public void LeaveForMenusPressed()
    {
        if (!LeavesToMenus || _leaving || _host == null || NetSession != null || SettingsPath == null) return;
        if (!MphRecomp.Config.RecompSettings.Load(SettingsPath).MenuArenaBackdrop) return; // (nothing for the menus to load)
        _leavePending = true;
    }

    // GL thread, right after the scene's draw (before the HUD and menus over it): the picture kept
    void LeaveCapture()
    {
        if (!_leavePending || _leaveTex != 0 || _width < 16 || _height < 16) return;
        var ids = new int[1];
        GLES30.GlGenTextures(1, ids, 0);
        _leaveTex = ids[0];
        GLES30.GlBindTexture(GLES30.GlTexture2d, _leaveTex);
        GLES30.GlTexImage2D(GLES30.GlTexture2d, 0, GLES30.GlRgba, _width, _height, 0, GLES30.GlRgba, GLES30.GlUnsignedByte, (byte[]?)null);
        GLES30.GlTexParameteri(GLES30.GlTexture2d, GLES30.GlTextureMinFilter, GLES30.GlLinear);
        GLES30.GlTexParameteri(GLES30.GlTexture2d, GLES30.GlTextureMagFilter, GLES30.GlLinear);
        GLES30.GlTexParameteri(GLES30.GlTexture2d, GLES30.GlTextureWrapS, GLES30.GlClampToEdge);
        GLES30.GlTexParameteri(GLES30.GlTexture2d, GLES30.GlTextureWrapT, GLES30.GlClampToEdge);
        GLES30.GlGenFramebuffers(1, ids, 0);
        _leaveFbo = ids[0];
        GLES30.GlBindFramebuffer(GLES30.GlFramebuffer, _leaveFbo);
        GLES30.GlFramebufferTexture2D(GLES30.GlFramebuffer, GLES30.GlColorAttachment0, GLES30.GlTexture2d, _leaveTex, 0);
        GLES30.GlDisable(GLES30.GlScissorTest); // (a blit and a clear both obey it)
        GLES30.GlBindFramebuffer(GLES30.GlReadFramebuffer, 0);
        GLES30.GlBindFramebuffer(GLES30.GlDrawFramebuffer, _leaveFbo);
        GLES30.GlBlitFramebuffer(0, 0, _width, _height, 0, 0, _width, _height, GLES30.GlColorBufferBit, GLES30.GlNearest);
        // the frame's alpha isn't always 1 (as PhotoCapture found): the copy's set to 1, or it draws see-through
        GLES30.GlBindFramebuffer(GLES30.GlFramebuffer, _leaveFbo);
        GLES30.GlColorMask(false, false, false, true);
        GLES30.GlClearColor(0, 0, 0, 1);
        GLES30.GlClear(GLES30.GlColorBufferBit);
        GLES30.GlColorMask(true, true, true, true);
        GLES30.GlBindFramebuffer(GLES30.GlFramebuffer, 0);
        (_leaveW, _leaveH) = (_width, _height);
    }

    // GL thread, top of each frame: true while leaving (the frame drawn here: the kept picture and the pause menu)
    bool LeaveFrame()
    {
        if (_leavePending && _leaveTex != 0)
        {
            _leavePending = false;
            _leaving = true;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            Shutdown();
            double shut = sw.Elapsed.TotalMilliseconds;
            bool begun = MenuArenaBackdrop.BeginEarly(SettingsPath!, _filesDir, _width, _height);
            Log.Info("MPHMenu", $"leaving for the menus: game shut down in {shut:0} ms{(begun ? ", arena background building" : "")}");
        }
        if (!_leaving) return false;
        if (PauseMenu?.IsOpen == true)
        {
            while (_pauseInput.TryDequeue(out var action)) action(PauseMenu);
        }
        else
        {
            while (_pauseInput.TryDequeue(out _)) { }
        }
        GLES30.GlBindFramebuffer(GLES30.GlFramebuffer, 0);
        GLES30.GlViewport(0, 0, _width, _height);
        GLES30.GlClearColor(0, 0, 0, 1);
        GLES30.GlClear(GLES30.GlColorBufferBit);
        if (_leaveTex != 0)
        {
            _leaveTextures ??= new UiTextureCache();
            if (_leaveDrawer == null) { _leaveDrawer = new UiGlDrawer(); _leaveDrawer.Init(); }
            _leaveList ??= new UiDrawList(_leaveTextures);
            if (_leaveTexId == 0) _leaveTexId = _leaveTextures.GetOrAdd("leave/still", () => (1, 1, new byte[4])).Id;
            _leaveDrawer.UseGlTexture(_leaveTexId, _leaveTex);
            _leaveList.Clear();
            _leaveList.Quad(_leaveTexId, 0, 0, _width, _height, 0, 1, 1, 0, 1, 1, 1, 1); // (rows bottom up)
            _leaveDrawer.Draw(_leaveList, _width, _height, false);
        }
        PauseMenu?.Draw(_width, _height); // (its flash ends here: QuitToMenu -> ExitSink)
        return true;
    }
}
