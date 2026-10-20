using System.Diagnostics;
using MphRead;
using MphRecomp.Config;
using MphRecomp.Frontend;

namespace MphRecomp.App;

// The in-game pause menu, for any GLSurfaceView renderer that runs a game (CampaignActivity...): the front end's PAUSED
// list -- resume, RECOMP SETTINGS, MOD SETTINGS, quit to the main menu -- drawn over the dimmed game with the same menu
// engine and ROM pieces as the menus. Everything is on the renderer's GL thread:
//
//   _pause = new PauseOverlay(settingsPath, modsDir);   // once MphRead's Paths are set up (loads lazily on first Open)
//   OnSurfaceCreated:  _pause.OnSurfaceCreated();
//   Start pressed:     _pause.Open();                    // then stop stepping the game while _pause.IsOpen
//   while open:        _pause.Press(MenuKeys.A) / Navigate(dx, dy) / Touch(x, y) instead of game input
//   end of OnDrawFrame: if (_pause.IsOpen) _pause.Draw(width, height);
//   _pause.Resumed / QuitToMenu / DevTools fire on the GL thread; Settings has the (saved) choices.
internal sealed class PauseOverlay
{
    readonly string _settingsPath, _modsDir;
    readonly UiGlDrawer _drawer = new();
    readonly Stopwatch _clock = new();
    FrontendSession? _session;
    // the host's ROM sound player (the campaign's GameSfxPlayer); the menus' own clicks go through it
    public Action<int>? PlaySound;
    double _last;

    public bool IsOpen { get; private set; }
    // a LAN / online match: set by the match (MatchSession.ConfigureMatch); online (OnlineChat set) is first person only
    public bool NetMatch { get; set; }
    // an online match (MatchSession.ConfigureMatch): the pause gets CHAT, the lobby's CHAT page on this match's connection
    public MphRecomp.Multiplayer.Net.OnlineMatch? OnlineChat { get; set; }
    public event Action? Resumed, QuitToMenu, QuitToDesktop, DevTools;
    // RECOMP SETTINGS > CREDITS / CRASH LOG: the host opens the same screens as the front end does (GameActivity)
    public event Action? Credits, ShareCrashLog, ShareLog, SaveLog;
    // the control customizer opened on its own (OpenControls, the ship's OPTIONS) was closed: the host reloads the buttons
    public event Action? ControlsClosed;

    public PauseOverlay(string settingsPath, string modsDir)
    {
        _settingsPath = settingsPath;
        _modsDir = modsDir;
    }

    // the choices as they are now (the pause menu saves every change to the settings file at once)
    public RecompSettings Settings => _session?.Settings ?? RecompSettings.Load(_settingsPath);

    // the cursor is on SECOND HUNT SETTINGS > SOUND's music quality or volume: the host lets the paused music play to hear a
    // change live
    public bool MusicRowFocused => IsOpen && _session != null
        && _session.Recomp.FocusedSetting(_session.Menu) is "music\nquality" or "music\nvolume";

    public void OnSurfaceCreated() => _drawer.Init();

    public void Open() => OpenWith(s => s.OpenPause());

    // the control customizer by itself (the ship's OPTIONS); its back closes it again (ControlsClosed)
    public void OpenControls(bool keys) => OpenWith(s => s.OpenControls(keys));

    void OpenWith(Action<FrontendSession> open)
    {
        try
        {
            if (_session == null)
            {
                _session = FrontendSession.Load(Paths.FileSystem, settingsPath: _settingsPath, modsDir: _modsDir);
                _session.Log = m => Log.Info("MPHPause", m);
                _session.PlaySound = id =>
                {
                    if (_holdSounds) _heldSounds.Add(id);
                    else PlaySound?.Invoke(id);
                };
                _session.Request = OnRequest;
            }
            else
            {
                // the ship's OPTIONS saves the file too: start from it, so a save here doesn't put old values back
                _session.Settings.ReloadFrom(_settingsPath);
            }
            _session.Recomp.InNetMatch = NetMatch;
            _session.Recomp.InOnlineMatch = NetMatch && OnlineChat != null; // the camera row greys out (RecompMenus); LAN can switch it
            _session.Recomp.PauseChat = OnlineChat is { Disposed: false } chat ? chat : null; // CHAT (RecompMenus.PausePage)
            open(_session);
            IsOpen = true;
            _clock.Restart();
            _last = 0;
        }
        catch (Exception ex)
        {
            Log.Error("MPHPause", "open: " + ex);
            IsOpen = false;
        }
    }

    // the control customizer is waiting for a button ("pad" / "keys", else null): the host sends its next press to
    // CaptureInput (a ControlBinds name) instead of Press / Navigate
    public string? CaptureDevice => IsOpen ? _session?.Recomp.CaptureDevice : null;
    public void CaptureInput(string button) => _session?.Recomp.CaptureInput(button);
    public void CancelCapture() => _session?.Recomp.CancelCapture();

    public void Close()
    {
        _session?.ClosePause();
        IsOpen = false;
    }

    void OnRequest(FrontendRequest r)
    {
        switch (r.Kind)
        {
        case FrontendRequestKind.Resume:
            Close();
            Resumed?.Invoke();
            break;
        case FrontendRequestKind.QuitToMenu:
            Close();
            QuitToMenu?.Invoke();
            break;
        case FrontendRequestKind.QuitToDesktop: // (Windows)
            Close();
            QuitToDesktop?.Invoke();
            break;
        case FrontendRequestKind.OpenDeveloperMenu:
            DevTools?.Invoke();
            break;
        case FrontendRequestKind.Credits:
            Credits?.Invoke();
            break;
        case FrontendRequestKind.ShareCrashLog:
            ShareCrashLog?.Invoke();
            break;
        case FrontendRequestKind.ShareLog:
            ShareLog?.Invoke();
            break;
        case FrontendRequestKind.SaveLog: // (2026-10-19: the log as a file on the device)
            SaveLog?.Invoke();
            break;
        case FrontendRequestKind.ControlsDone:
            Close();
            ControlsClosed?.Invoke();
            break;
        }
    }

    public void Press(MenuKeys keys) => HoldSounds(() => _session?.Press(keys));
    public void Navigate(int dx, int dy) => _session?.Navigate(dx, dy);
    public void Touch(float x, float y) => HoldSounds(() => _session?.TouchCanvas(x, y));
    // a touch's down / move / up / cancel (0-3): settings and CONTROLS scroll by a drag (FrontendSession.TouchCanvas)
    public void Touch(float x, float y, int kind) => HoldSounds(() => _session?.TouchCanvas(x, y, kind));
    public void ScrollWheel(float notches) => _session?.ScrollWheel(notches);
    public void ScrollStick(float y) => _session?.ScrollStick(y);

    // The menu's click for a press sounds before the press acts, so it's held until we know what the press did: one that
    // closed the menu (resume, quit) is silent -- no unpause noise (owner 2026-10-04) -- any other plays as before.
    readonly List<int> _heldSounds = new();
    bool _holdSounds;

    void HoldSounds(Action input)
    {
        _heldSounds.Clear();
        _holdSounds = true;
        try
        {
            input();
        }
        finally
        {
            _holdSounds = false;
        }
        if (IsOpen)
        {
            foreach (int id in _heldSounds) PlaySound?.Invoke(id);
        }
        _heldSounds.Clear();
    }

    public void Draw(int width, int height)
    {
        if (!IsOpen || _session == null) return;
        double now = _clock.Elapsed.TotalSeconds;
        _session.Update(now - _last);
        _last = now;
        if (!IsOpen) return; // a request closed it
        _session.Build(width, height);
        _drawer.Draw(_session.DrawList, width, height, _session.Settings.SmoothMenus);
    }
}
