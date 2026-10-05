using Android.App;
using Android.Content;
using Android.Opengl;
using Android.OS;
using Android.Util;
using Android.Views;
using Android.Widget;
using Javax.Microedition.Khronos.Opengles;
using System;
using System.Collections.Generic;
using Stopwatch = System.Diagnostics.Stopwatch;
using System.Runtime.InteropServices;
using MphRead;
using MphRead.Rendering;
using MphRead.Entities;
using MphRead.Formats;
using MphRecomp.Campaign;
using MphRecomp.Frontend;
using MphRecomp.Render;
using OpenTK.Mathematics;

namespace MphRecomp.App;

// Campaign mode (early): MphRead's OWN adventure simulation -- rooms, doors and corridors, locks, pickups, enemies,
// bosses, story save -- hosted headless by CampaignHost, and drawn here from MphRead's own per-frame draw list
// (Scene.CollectDrawItems) through the shared scene shaders and layered draw (MphRead/Rendering).
// Separate from RenderActivity so the room viewer / HD trophy work stays untouched.
//   am start -n com.mphrecomp.app/com.mphrecomp.app.CampaignActivity --es room UNIT2_LAND
[Activity(Name = "com.mphrecomp.app.CampaignActivity", Label = "mph-recomp campaign",
    Exported = BuildFlags.ExportDevActivities, ScreenOrientation = Android.Content.PM.ScreenOrientation.Landscape,
    Theme = "@android:style/Theme.Black.NoTitleBar.Fullscreen",
    // as GameActivity: a controller connecting or the share sheet / credits (pause menu) returning must not restart the game
    ConfigurationChanges = Android.Content.PM.ConfigChanges.Orientation | Android.Content.PM.ConfigChanges.ScreenSize
        | Android.Content.PM.ConfigChanges.ScreenLayout | Android.Content.PM.ConfigChanges.Keyboard
        | Android.Content.PM.ConfigChanges.KeyboardHidden | Android.Content.PM.ConfigChanges.Navigation)]
public class CampaignActivity : Activity
{
    CampaignRenderer _renderer = null!;
    GLSurfaceView _view = null!;
    TextView _status = null!;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        string room = Intent?.GetStringExtra("room") ?? "UNIT2_LAND";
        // --es layout reference: the first on-device layout (kept as the owner's reference); default: Prime-style
        var layout = Intent?.GetStringExtra("layout") == "reference" ? ControlLayout.Reference : ControlLayout.Prime;
        // --es hunter Kanden: play the campaign as another hunter (MphRead supports it; Features.AlternateHunters1P
        // adds the extra jump pads/teleporters they need)
        Hunter hunter = Enum.TryParse(Intent?.GetStringExtra("hunter"), out Hunter h) ? h : Hunter.Samus;
        string ext = GetExternalFilesDir(null)?.AbsolutePath ?? FilesDir!.AbsolutePath;
        string gunsRoot = System.IO.Path.Combine(ext, "guns");
        string settingsPath = System.IO.Path.Combine(ext, "recomp_settings.json");
        // --es gun mp1: the first-person Prime arm cannon from <external files>/guns/mp1 in place of MPH's (an explicit
        // extra wins at launch, dev-launcher style); --es gun none keeps MPH's own. With no extra (or --es gun settings, the
        // dev launcher's default): Samus takes whatever suit RECOMP SETTINGS has (default MP1PowerSuit, "original" = MPH's
        // own); other hunters keep MP1PowerSuit.
        string? explicitGun = Intent?.GetStringExtra("gun") is string g && g != "settings" ? g : null;
        string gunId = explicitGun ?? (hunter == Hunter.Samus
            ? MphRecomp.Config.RecompSettings.Load(settingsPath).SamusSuit : "MP1PowerSuit");
        // a public build (Beta 1: imports and arm cannons held, owner 2026-10-04) has no HD gear at all: MPH's own gun and
        // Samus, whatever the settings file or the device's guns/ and hd/ folders hold
        if (BuildFlags.Public) gunId = "original";
        string? gunDir = gunId is "none" or "original" ? null : System.IO.Path.Combine(gunsRoot, gunId);
        // RECOMP SETTINGS -> pause -> resume hot-swaps Samus's suit whenever the player changes it there, even over an
        // explicit extra (cheap: a small file load, no GL work until the next draw); see ReloadGunFromSettings
        _renderer = new CampaignRenderer(FilesDir!.AbsolutePath, room, layout, hunter, gunDir, gunsRoot, settingsPath);
        _view = new GLSurfaceView(this);
        _view.SetEGLContextClientVersion(3);
        // stencil: MphRead's translucency passes sort overlapping translucent surfaces with it
        _view.SetEGLConfigChooser(8, 8, 8, 8, 24, 8);
        _view.PreserveEGLContextOnPause = true;
        _view.SetRenderer(_renderer);
        var root = new FrameLayout(this);
        root.AddView(_view, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent));
        _status = new TextView(this) { TextSize = 12f, Text = "loading " + room + "..." };
        _status.SetTextColor(Android.Graphics.Color.White);
        _status.SetShadowLayer(3f, 1f, 1f, Android.Graphics.Color.Black);
        _status.SetPadding(24, 16, 24, 16);
        root.AddView(_status, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent)
        { Gravity = GravityFlags.Top | GravityFlags.Left });
        _renderer.StatusSink = s => RunOnUiThread(() => _status.Text = s);
        // Start: the recomp's pause menu (resume / recomp settings / mod settings / quit to main menu), the same settings
        // file and mods folder as the front end (GameActivity); L3 + R3 together: the gun-adjust pause below
        _renderer.PauseMenu = new PauseOverlay(settingsPath, System.IO.Path.Combine(ext, "mods"));
        _renderer.SettingsPath = settingsPath; // CampaignAudio.cs: RecompSettings.Music (original/hq)
        _renderer.ReloadAimFromSettings();
        _renderer.PauseMenu.Resumed += () => { _renderer.ReloadGunFromSettings(); _renderer.ReloadAimFromSettings(); };
        _renderer.PauseMenu.QuitToMenu += () => _renderer.ExitSink?.Invoke();
        _renderer.PauseMenu.DevTools += () => RunOnUiThread(() =>
        {
            StartActivity(new Intent(this, typeof(MainActivity)).PutExtra("dev", "1"));
            Finish();
        });
        // RECOMP SETTINGS > CREDITS / CRASH LOG from the pause menu: the front end's screens, over the paused game
        _renderer.PauseMenu.Credits += () => RunOnUiThread(() => StartActivity(new Intent(this, typeof(CreditsActivity))));
        _renderer.PauseMenu.ShareCrashLog += () => RunOnUiThread(() => GameActivity.ShareCrashLog(this));
        // gyro aim on top of the right stick (CampaignGyro.cs; RECOMP SETTINGS gyro rows, --es gyro on|zoom)
        CampaignRenderer.AttachGyro(this, _renderer, settingsPath, Intent);
        // the gunship between planets (save, star map, travel): CampaignShip.cs; --es save <slot> continues a save
        CampaignRenderer.AttachShip(this, root, _renderer, Intent);
        // --es loadout all (dev launcher "Boss fights"): a fresh story with every weapon, full ammo and 799 energy, and
        // nothing saved (the ship's save prompt would otherwise write slot 1, the player's file A)
        if (Intent?.GetStringExtra("loadout") == "all")
        {
            _renderer.UseTestLoadout();
        }
        // --es boss down (dev launcher "Escape tests"): the boss is beaten for the player once its intro is over (CampaignBossSkip.cs)
        if (Intent?.GetStringExtra("boss") == "down") _renderer.UseBossSkip();
        // in-game cutscenes (CampaignMovie.cs); --es movies off skips them
        CampaignRenderer.ConfigureMovies(Intent);
        // a multiplayer match with bots instead of the campaign (MatchSession.cs); --es match <mode>
        CampaignRenderer.AttachMatch(this, root, _renderer, Intent);
        // view framing: WIDE (CampaignView.cs; dev only: --es view wide|ds|prime|panini)
        CampaignRenderer.AttachView(Intent);
        // on-screen touch controls, on top of everything (MatchTouchOverlay.cs): RECOMP SETTINGS "touch controls"
        // auto|on|off (a match's --es touch overrides it), re-read whenever the pause menu resumes
        MatchTouchOverlay touch = MatchTouchOverlay.Attach(this, root, _renderer, Intent, settingsPath);
        _renderer.PauseMenu.Resumed += () => RunOnUiThread(touch.ReloadSettings);
        SetContentView(root);
        HideSystemUi();
    }

    protected override void OnPause() { base.OnPause(); _view.OnPause(); _renderer.PauseAudio(); }
    protected override void OnResume() { base.OnResume(); _view.OnResume(); _renderer.ResumeAudio(); }
    protected override void OnDestroy() { _renderer.Shutdown(); _renderer.DisposeAudio(); base.OnDestroy(); }

    public override void OnWindowFocusChanged(bool hasFocus)
    {
        base.OnWindowFocusChanged(hasFocus);
        if (hasFocus) HideSystemUi();
    }

    void HideSystemUi() => Window!.DecorView.SystemUiVisibility = (StatusBarVisibility)(
        SystemUiFlags.ImmersiveSticky | SystemUiFlags.HideNavigation | SystemUiFlags.Fullscreen |
        SystemUiFlags.LayoutStable | SystemUiFlags.LayoutHideNavigation | SystemUiFlags.LayoutFullscreen);

    static float Dead(float v) => Math.Abs(v) < 0.15f ? 0f : v;

    int _menuHatX, _menuHatY, _menuStickX, _menuStickY;

    static int Step(float v, float threshold) => v >= threshold ? 1 : v <= -threshold ? -1 : 0;

    public override bool OnGenericMotionEvent(MotionEvent? e)
    {
        if (e != null && e.Source.HasFlag(InputSourceType.Joystick) && e.Action == MotionEventActions.Move)
        {
            if (_renderer.PauseMenuOpen)
            {
                // the pause menu: D-pad (a HAT axis on the Odin) and left stick move the focus, once per push
                int hx = Step(e.GetAxisValue(Axis.HatX), 0.5f), hy = Step(e.GetAxisValue(Axis.HatY), 0.5f);
                int sx = Step(e.GetAxisValue(Axis.X), 0.6f), sy = Step(e.GetAxisValue(Axis.Y), 0.6f);
                if (hx != _menuHatX && hx != 0) _renderer.PauseMenuInput(p => p.Navigate(hx, 0));
                if (hy != _menuHatY && hy != 0) _renderer.PauseMenuInput(p => p.Navigate(0, -hy));
                if (sx != _menuStickX && sx != 0) _renderer.PauseMenuInput(p => p.Navigate(sx, 0));
                if (sy != _menuStickY && sy != 0) _renderer.PauseMenuInput(p => p.Navigate(0, -sy));
                _menuHatX = hx; _menuHatY = hy; _menuStickX = sx; _menuStickY = sy;
                return true;
            }
            PadState pad = _renderer.Pad;
            pad.Lx = Dead(e.GetAxisValue(Axis.X));
            pad.Ly = Dead(e.GetAxisValue(Axis.Y));
            pad.RawRx = e.GetAxisValue(Axis.Z);
            pad.RawRy = e.GetAxisValue(Axis.Rz);
            pad.Rx = Dead(pad.RawRx);
            pad.Ry = Dead(pad.RawRy);
            pad.L2 = e.GetAxisValue(Axis.Ltrigger);
            pad.R2 = e.GetAxisValue(Axis.Rtrigger);
            // this Odin reports the D-pad as a HAT axis, not KEYCODE_DPAD_*
            pad.HatX = e.GetAxisValue(Axis.HatX);
            pad.HatY = e.GetAxisValue(Axis.HatY);
            return true;
        }
        return base.OnGenericMotionEvent(e);
    }

    static bool IsPadKey(Keycode k) => k is Keycode.ButtonA or Keycode.ButtonB or Keycode.ButtonX or Keycode.ButtonY
        or Keycode.ButtonL1 or Keycode.ButtonR1 or Keycode.ButtonL2 or Keycode.ButtonR2 or Keycode.ButtonThumbl
        or Keycode.ButtonThumbr or Keycode.DpadUp or Keycode.DpadDown or Keycode.DpadLeft or Keycode.DpadRight;

    public override bool OnKeyDown(Keycode keyCode, KeyEvent? e)
    {
        // Select ("View"): the pause map (CampaignMap.cs; owner 2026-10-03 -- first / third person is a RECOMP SETTINGS row now)
        if (keyCode == Keycode.ButtonSelect)
        {
            if (e?.RepeatCount == 0 && !_renderer.PauseMenuOpen)
            {
                _renderer.SelectPressed();
            }
            return true;
        }
        // Android's Back button / gesture never leaves the game (owner 2026-10-04): B in the pause menu, the ship's
        // screens and the map, otherwise it opens the pause menu (CampaignRenderer.BackFrame); quitting stays a menu choice
        if (keyCode == Keycode.Back)
        {
            if (e?.RepeatCount == 0)
            {
                _renderer.BackPressed();
            }
            return true;
        }
        if (keyCode == Keycode.ButtonStart)
        {
            if (e?.RepeatCount == 0)
            {
                _renderer.StartPressed();
            }
            return true;
        }
        // L3 + R3 together: the gun-adjust pause (MPH / Prime gun, gun size)
        if ((keyCode == Keycode.ButtonThumbl && _renderer.Pad[Keycode.ButtonThumbr])
            || (keyCode == Keycode.ButtonThumbr && _renderer.Pad[Keycode.ButtonThumbl]))
        {
            if (e?.RepeatCount == 0)
            {
                _renderer.ToggleGunMenu();
            }
        }
        if (_renderer.PauseMenuOpen)
        {
            if (e?.RepeatCount == 0)
            {
                MenuInput(keyCode);
            }
            return true; // the game doesn't see presses meant for the menu (A = resume must not also jump)
        }
        if (IsPadKey(keyCode))
        {
            _renderer.Pad.Set(keyCode, true);
            return true;
        }
        return base.OnKeyDown(keyCode, e);
    }

    void MenuInput(Keycode keyCode)
    {
        MenuKeys key = keyCode switch
        {
            Keycode.ButtonA => MenuKeys.A,
            Keycode.ButtonB => MenuKeys.B,
            Keycode.ButtonX => MenuKeys.X,
            Keycode.ButtonY => MenuKeys.Y,
            Keycode.ButtonL1 => MenuKeys.L,
            Keycode.ButtonR1 => MenuKeys.R,
            _ => MenuKeys.None
        };
        if (key != MenuKeys.None)
        {
            _renderer.PauseMenuInput(p => p.Press(key));
            return;
        }
        switch (keyCode)
        {
        case Keycode.DpadUp: _renderer.PauseMenuInput(p => p.Navigate(0, 1)); break;
        case Keycode.DpadDown: _renderer.PauseMenuInput(p => p.Navigate(0, -1)); break;
        case Keycode.DpadLeft: _renderer.PauseMenuInput(p => p.Navigate(-1, 0)); break;
        case Keycode.DpadRight: _renderer.PauseMenuInput(p => p.Navigate(1, 0)); break;
        }
    }

    public override bool OnTouchEvent(MotionEvent? e)
    {
        if (e != null && e.ActionMasked == MotionEventActions.Down && _renderer.PauseMenuOpen)
        {
            float x = e.GetX(), y = e.GetY();
            _renderer.PauseMenuInput(p => p.Touch(x, y));
            return true;
        }
        if (e != null && _renderer.MapOpen && e.ActionMasked is MotionEventActions.Down or MotionEventActions.Move or MotionEventActions.Up)
        {
            // the pause map (CampaignMap.cs): drags turn it, QUIT
            _renderer.MapTouch(e.GetX(), e.GetY(), e.ActionMasked == MotionEventActions.Down ? 0 : e.ActionMasked == MotionEventActions.Move ? 1 : 2);
            return true;
        }
        if (e != null && _renderer.ShipMenuOpen && e.ActionMasked is MotionEventActions.Down or MotionEventActions.Move or MotionEventActions.Up)
        {
            // the in-ship menu (CampaignShip.cs): taps, plus moves/lifts for its slider and weapon drags
            _renderer.ShipTouch(e.GetX(), e.GetY(), e.ActionMasked == MotionEventActions.Down ? 0 : e.ActionMasked == MotionEventActions.Move ? 1 : 2);
            return true;
        }
        // taps during play belong to the touch controls (MatchTouchOverlay.cs, a view above the game: they never get
        // here while it is shown); the view stays WIDE (CampaignView.cs, board #30a)
        return base.OnTouchEvent(e);
    }

    public override bool OnKeyUp(Keycode keyCode, KeyEvent? e)
    {
        if (IsPadKey(keyCode))
        {
            _renderer.Pad.Set(keyCode, false);
            return true;
        }
        if (keyCode == Keycode.Back) return true; // handled on the press (OnKeyDown); the release must not finish us
        return base.OnKeyUp(keyCode, e);
    }
}

internal enum ControlLayout { Prime, Reference }

// Raw controller state: written on the UI thread, read on the GL thread once per simulation step.
internal sealed class PadState
{
    public volatile float Lx, Ly, Rx, Ry, L2, R2, HatX, HatY;
    public volatile float RawRx, RawRy; // the right stick before the dead zone (input trace)
    readonly bool[] _keys = new bool[512];
    public bool this[Keycode key] => (int)key >= 0 && (int)key < _keys.Length && _keys[(int)key];
    public void Set(Keycode key, bool down)
    {
        if ((int)key >= 0 && (int)key < _keys.Length)
        {
            _keys[(int)key] = down;
        }
    }
}

internal sealed partial class CampaignRenderer : Java.Lang.Object, GLSurfaceView.IRenderer
{
    readonly string _filesDir;
    readonly string _room;
    CampaignHost? _host;
    string? _error;
    public Action<string>? StatusSink;

    public readonly PadState Pad = new();
    readonly ControlLayout _layout;
    volatile bool _paused;
    public void TogglePause() => _paused = !_paused;

    // the recomp's pause menu (PauseOverlay; drawn over the dimmed game), opened by Start. L3 + R3 open the gun-adjust
    // pause above instead; Start closes that one.
    public PauseOverlay? PauseMenu;
    volatile bool _openPauseMenu;
    volatile bool _gunComboUsed; // L3's own action (status text) is skipped when it was part of the combo
    readonly System.Collections.Concurrent.ConcurrentQueue<Action<PauseOverlay>> _pauseInput = new();
    public bool PauseMenuOpen => _openPauseMenu || PauseMenu?.IsOpen == true;
    public void PauseMenuInput(Action<PauseOverlay> action) => _pauseInput.Enqueue(action);

    public void StartPressed()
    {
        if (_paused)
        {
            _paused = false; // Start leaves the gun-adjust pause
        }
        else if (PauseMenu?.IsOpen == true)
        {
            PauseMenuInput(p => p.Press(MenuKeys.Start)); // resume, as the menu's own Start does
        }
        else if (PauseMenu != null)
        {
            _openPauseMenu = true;
        }
    }

    // Android's Back (UI thread): one press, acted on at the start of the next frame (GL thread), where the open screen
    // is known: B in the pause menu, leaves the gun-adjust pause, B on the ship's screens, closes the map; otherwise it
    // opens the pause menu like Start. It never exits (owner 2026-10-04).
    volatile bool _backPressed;
    public void BackPressed() => _backPressed = true;

    void BackFrame()
    {
        if (!_backPressed) return;
        _backPressed = false;
        if (PauseMenu?.IsOpen == true) PauseMenu.Press(MenuKeys.B);
        else if (_paused) _paused = false;
        else if (ShipOpen) _shipBackTap = true; // ShipInput reads it as one B press (CampaignShip.cs)
        else if (MapOpen) CloseMap();
        else if (PauseMenu != null) _openPauseMenu = true;
    }

    public void ToggleGunMenu()
    {
        _gunComboUsed = true;
        if (PauseMenuOpen) return;
        _paused = !_paused;
    }
    const float AimSpeed = 12f; // game "mouse" units per frame at full stick

    // the game's own OPTIONS (in-ship menu, saved in RecompSettings): aim sensitivity 1-15 (8 = AimSpeed as before,
    // scaled 0.5 + level/16) and look invert (vertical). GameControlType (stylus/dual, left/right) has no gamepad meaning.
    // Gyro aim keeps its own Gyro* settings (CampaignGyro.cs).
    float _aimScale = 1f;
    bool _aimInvertY, _aimShipWasOpen;

    public void ReloadAimFromSettings()
    {
        var s = MphRecomp.Config.RecompSettings.Load(SettingsPath);
        _aimScale = 0.5f + Math.Clamp(s.GameSensitivity, 1, 15) / 16f;
        _aimInvertY = s.GameLookInvert;
        SetThirdPerson(s.ThirdPerson); // RECOMP SETTINGS "camera" (Select's toggle until 2026-10-03; Select = map now)
    }

    System.Numerics.Vector2 StickAim(PadState pad) =>
        new(pad.Rx * AimSpeed * _aimScale, (_aimInvertY ? -pad.Ry : pad.Ry) * AimSpeed * _aimScale);
    const int CullFaceCap = 0x0B44; // GL_CULL_FACE (the binding names the constant and glCullFace alike)

    Hunter _hunter; // a continued save plays as its own hunter (CampaignShip.cs)
    bool _allWeapons;

    // a test run (--es loadout all): MphRead's own StartWithAllUpgrades values on a fresh story, and saving off
    public void UseTestLoadout()
    {
        _allWeapons = true;
        _saves = null;
    }

    static void GiveAllWeapons(StorySave save)
    {
        save.Health = save.HealthMax = 799;
        save.Ammo[0] = save.AmmoMax[0] = 4000;
        save.Ammo[1] = save.AmmoMax[1] = 950;
        save.Weapons = 0xFF;
    }

    public CampaignRenderer(string filesDir, string room, ControlLayout layout, Hunter hunter, string? gunDir,
        string? gunsRoot = null, string? gunSettingsPath = null)
    {
        _filesDir = filesDir;
        _room = room;
        _layout = layout;
        _hunter = hunter;
        _gunDir = gunDir;
        _gunsRoot = gunsRoot;
        _gunSettingsPath = gunSettingsPath;
        // MphRead now refuses a double release of a pooled effect (the 10-01 Arcterra escape crash); log who did it
        MphRead.Scene.HostEffectPoolWarning = msg => Log.Warn("MPHCampaign", msg);
        // room entry (2026-10-03): the next room's textures are decoded on MphRead's room-load worker, and the forced
        // compacting GC at the end of every transition is skipped (both sat in the frame the room swaps in: 110-190 ms
        // on the Odin); MPHPerf gets the swap-in breakdown and any entity set-up of 5 ms or more
        MphRead.Scene.HostPredecodeTextures = true;
        MphRead.Entities.RoomEntity.HostTransitionGc = false;
        MphRead.Entities.RoomEntity.HostTransitionTrace = msg => Log.Info("MPHPerf", msg);
        MphRead.Scene.HostEntityInitTrace = msg => Log.Info("MPHPerf", "set-up: " + msg);
        _suitSeen = gunSettingsPath != null ? MphRecomp.Config.RecompSettings.Load(gunSettingsPath).SamusSuit : null;
        _suitId = hunter == Hunter.Samus && gunDir != null ? System.IO.Path.GetFileName(gunDir) : null;
    }

    // the first-person Prime gun (CampaignGun) replacing MPH's SamusGun; while paused the D-pad switches between the
    // two (left/right) and sizes the Prime one (up/down)
    string? _gunDir;
    // the settings file (RecompSettings): lets the pause menu's RECOMP SETTINGS hot-swap Samus's suit on resume
    // (ReloadGunFromSettings) instead of waiting for a fresh campaign start; _suitSeen = the setting as last applied
    readonly string? _gunsRoot, _gunSettingsPath;
    string? _suitSeen;
    CampaignGun? _gun;
    bool _primeGun = true, _gunActive;
    Vector3 _gunTint;
    bool _pPrevUp, _pPrevDown, _pPrevLeft, _pPrevRight;

    // called on the GL thread when the pause menu's RECOMP SETTINGS resumes (PauseOverlay.Resumed); a no-op unless
    // Samus is playing and the player actually changed the suit there (a change wins over an explicit --es gun too)
    public void ReloadGunFromSettings()
    {
        if (BuildFlags.Public || _gunSettingsPath == null || _gunsRoot == null || _hunter != Hunter.Samus) return;
        string suit = MphRecomp.Config.RecompSettings.Load(_gunSettingsPath).SamusSuit;
        if (suit == _suitSeen) return;
        _suitSeen = suit;
        string? dir = suit is "none" or "original" ? null : System.IO.Path.Combine(_gunsRoot, suit);
        _primeGun = true; // show the suit just picked, even if the gun-adjust pause had switched to MPH's gun
        _suitId = dir != null ? suit : null;
        bool gunChanged = dir != _gunDir;
        _gunDir = dir;
        StartGearLoad(gun: gunChanged);
    }

    // Samus's HD suit (CampaignSuit.cs): the suit's own body and morph ball from <external files>/hd/, the same folder
    // name as its gun; null for MPH's own Samus, another hunter, or a suit with neither
    CampaignSuit? _suit;
    string? _suitId;

    // The suit's gun and body/ball load on a worker thread, then make their GL objects a few steps per frame (Warm)
    // while the old ones stay on screen, and are swapped in once they draw without a stall -- at the start and when
    // RECOMP SETTINGS changes the suit. Both used to load and build GL inside one frame: a 0.3 s freeze at the first
    // ship exit, 0.25 s at the first suit draw, 2 s on a suit change (2026-10-03 recording).
    sealed class GearLoad
    {
        public bool Gun; // the gun is replaced too (else the current one stays)
        public System.Threading.Tasks.Task<(CampaignGun? Gun, CampaignSuit? Suit)> Task = null!;
        public long Started;
    }
    GearLoad? _gearLoad;
    const double WarmBudgetMs = 2;

    void StartGearLoad(bool gun)
    {
        DropGearLoad();
        string? gunDir = gun ? _gunDir : null, suitId = _suitId;
        string? hdRoot = _gunsRoot != null ? System.IO.Path.Combine(System.IO.Path.GetDirectoryName(_gunsRoot) ?? "", "hd") : null;
        Model? samus = null;
        if (suitId != null && hdRoot != null)
        {
            // MphRead's model cache is the GL thread's: look Samus up here, not on the worker
            try { samus = CampaignSuit.SamusModel(); }
            catch (Exception ex) { Log.Warn("MPHCampaign", "suit: Samus model: " + ex.Message); }
        }
        var load = new GearLoad { Gun = gun, Started = Stopwatch.GetTimestamp() };
        // a thread of its own: a pool thread can queue behind the game's own work for seconds
        load.Task = System.Threading.Tasks.Task.Factory.StartNew(() =>
        {
            CampaignGun? g = null;
            if (gunDir != null && System.IO.File.Exists(System.IO.Path.Combine(gunDir, "gun.bin")))
            {
                try { g = new CampaignGun(gunDir); }
                catch (Exception ex) { Log.Error("MPHCampaign", "gun load failed: " + ex); }
            }
            CampaignSuit? s = suitId != null && hdRoot != null ? CampaignSuit.TryLoad(hdRoot, suitId, samus) : null;
            return (g, s);
        }, System.Threading.Tasks.TaskCreationOptions.LongRunning);
        _gearLoad = load;
    }

    // campaign start only (OnSurfaceChanged, still behind the room load): wait for the gear's worker and make all its GL
    // objects now, so the first frame already shows the suit's gun; a load past 5 s goes on in UpdateGear as before
    void FinishGearLoad()
    {
        GearLoad? load = _gearLoad;
        if (load == null) return;
        long t0 = Stopwatch.GetTimestamp();
        try
        {
            if (!load.Task.Wait(5000))
            {
                Log.Warn("MPHCampaign", "gear still loading 5 s after the room: it goes in during play");
                return;
            }
            var (gun, suit) = load.Task.Result;
            while (true)
            {
                bool gunReady = gun == null || gun.Warm(100), suitReady = suit == null || suit.Warm(100);
                if (gunReady && suitReady) break;
                if (Stopwatch.GetElapsedTime(t0).TotalMilliseconds > 5000)
                {
                    Log.Warn("MPHCampaign", "gear GL set-up still going 5 s after the room: it goes in during play");
                    return;
                }
                System.Threading.Thread.Sleep(1); // the gun's textures still decoding on their threads
            }
        }
        catch (Exception ex)
        {
            // a failed load or GL step: UpdateGear reports it and drops the load, as during play
            Log.Warn("MPHCampaign", "gear set-up before the first frame failed: " + ex.GetBaseException().Message);
        }
        UpdateGear(); // the swap, with its "suit in place" line
        Log.Info("MPHCampaign", $"gear before the first frame: {Stopwatch.GetElapsedTime(t0).TotalMilliseconds:0} ms after the room");
    }

    // a newer request replaces a load still in flight: free what it made in GL so far
    void DropGearLoad()
    {
        if (_gearLoad?.Task.IsCompletedSuccessfully == true)
        {
            var (g, s) = _gearLoad.Task.Result;
            g?.DeleteGl();
            s?.DeleteGl();
        }
        _gearLoad = null;
    }

    // GL thread, every frame (the ship and pause screens too): a few GL steps for a loaded suit, then the swap; the gun
    // and suit in use make theirs here too after a context loss
    double _perfGearMs;
    void UpdateGear()
    {
        long t0 = Stopwatch.GetTimestamp();
        try
        {
            _gun?.Warm(WarmBudgetMs);
            _suit?.Warm(WarmBudgetMs);
            GearLoad? load = _gearLoad;
            if (load != null && load.Task.IsCompleted)
            {
                if (load.Task.IsFaulted)
                {
                    Log.Error("MPHCampaign", "suit load failed: " + load.Task.Exception);
                    _gearLoad = null;
                }
                else
                {
                    var (gun, suit) = load.Task.Result;
                    if ((gun == null || gun.Warm(WarmBudgetMs)) && (suit == null || suit.Warm(WarmBudgetMs)))
                    {
                        if (load.Gun && _gun != gun)
                        {
                            _gun?.DeleteGl();
                            _gun = gun;
                        }
                        if (_suit != suit)
                        {
                            _suit?.DeleteGl();
                            _suit = suit;
                        }
                        _gearLoad = null;
                        Log.Info("MPHCampaign", $"suit in place: gun {(load.Gun ? gun?.Name ?? "MPH's" : "kept")}, body/ball "
                            + $"{suit?.Id ?? "MPH's"}, {Stopwatch.GetElapsedTime(load.Started).TotalMilliseconds:0} ms after the request");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error("MPHCampaign", "suit GL setup failed: " + ex);
            _gearLoad = null;
        }
        _perfGearMs = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
    }

    bool SuitShown => _suit != null && _host != null && !ShipOpen && _host.Player.Hunter == Hunter.Samus;

    // MphRead's hooks for the main player's body (PlayerDraw.cs), on only while the campaign's own step runs: MphRead
    // leaves the body / ball the suit has to CampaignSuit, and draws the body in first person for the third-person view
    void SetPlayerHooks(bool on)
    {
        PlayerEntity.HostOwnMainBiped = on && SuitShown && _suit!.HasBody;
        PlayerEntity.HostOwnMainAlt = on && SuitShown && _suit!.HasBall;
        PlayerEntity.HostShowMainBiped = on && _thirdPerson && !ShipOpen;
    }

    void DrawSuit(Scene scene, SuitPass pass)
    {
        if (!SuitShown || scene != _host!.Scene) return;
        PlayerEntity p = _host.Player;
        if (p.IsAltForm)
        {
            if (_suit!.HasBall && p.Flags2.TestFlag(PlayerFlags2.DrawnThirdPerson)) _suit.DrawBall(p, scene.ViewMatrix, scene.PerspectiveMatrix, pass);
        }
        else if (_suit!.HasBody && PlayerEntity.HostMainBipedAlpha > 0 && !_bodyCut)
        {
            _suit.DrawBody(p, scene.ViewMatrix, scene.PerspectiveMatrix, pass, PlayerEntity.HostMainBipedAlpha);
        }
        GLES30.GlUseProgram(_program);
        GLES30.GlBindVertexArray(0);
        GLES30.GlActiveTexture(GLES30.GlTexture0);
        GLES30.GlEnable(GLES30.GlBlend);
        GLES30.GlBlendFunc(GLES30.GlSrcAlpha, GLES30.GlOneMinusSrcAlpha);
        GLES30.GlDisable(CullFaceCap);
    }

    // The camera in or right against Samus: the last part of MphRead's swing back into her eye after unmorphing (its
    // camera switch draws the body all the way in), or the Select third-person camera pulled in against a wall. The HD
    // body then fills the screen from the inside, many layers deep, each through GX's TEV shader twice (opaque + depth
    // rebuild): 13 -> 59 ms GPU frames, the 10-03 VDO 2 Slench waves (MPH's own low-poly biped just clips through the
    // near plane there). So it's left out while the camera is within her collision radius + a margin and below the top
    // of her head + a margin, outside cutscenes (their close-ups keep her). Samus: radius 0.5, eye 0.9, top 1.1.
    const float BodyCutRadius = 1.0f, BodyCutAboveTop = 0.5f, BodyCutBelow = 0.6f;
    bool _bodyCut;
    int _bodyCutFrames;
    float _bodyCutNearest;

    void UpdateBodyCut(Scene scene)
    {
        bool cut = false;
        if (SuitShown && _host != null && scene == _host.Scene && !_host.Player.IsAltForm
            && CameraSequence.Current == null)
        {
            PlayerEntity p = _host.Player;
            Vector3 cam = scene.CameraPosition, pos = p.Position;
            float dx = cam.X - pos.X, dz = cam.Z - pos.Z;
            float top = pos.Y + Fixed.ToFloat(p.Values.MaxPickupHeight) + BodyCutAboveTop;
            float flat = MathF.Sqrt(dx * dx + dz * dz);
            cut = flat < BodyCutRadius && cam.Y < top && cam.Y > pos.Y - BodyCutBelow
                && PlayerEntity.HostMainBipedAlpha > 0;
            if (cut)
            {
                _bodyCutNearest = _bodyCutFrames == 0 ? flat : MathF.Min(_bodyCutNearest, flat);
                _bodyCutFrames++;
            }
        }
        if (!cut && _bodyCutFrames > 0)
        {
            Log.Info("MPHGpu", $"HD body left out for {_bodyCutFrames} frames: camera inside it (nearest {_bodyCutNearest:0.00} from her centre line)");
            _bodyCutFrames = 0;
        }
        _bodyCut = cut;
    }

    // THIRD PERSON (RECOMP SETTINGS "camera"): render only. MphRead's camera, aim, HUD and logic stay first person; the scene is drawn from
    // a point behind, above and right of Samus's eye with the same rotation (Scene.HostCameraOffset: view, billboards,
    // culling), pulled in where the room's collision is closer. Off in the morph ball (MphRead's own third person),
    // in-game cutscenes and the ship.
    volatile bool _thirdPerson;
    float _tpDist;
    const float TpBack = 2.4f, TpUp = 0.35f, TpSide = 0.5f, TpMargin = 0.3f;

    public void SetThirdPerson(bool on)
    {
        if (on == _thirdPerson) return;
        _thirdPerson = on;
        _tpDist = float.MaxValue; // the first step after turning it on puts the view straight out, not from inside the head
        _statusCountdown = 0;
    }

    // after each sim step: the offset the next step's camera uses
    void UpdateThirdPerson()
    {
        Scene scene = _host!.Scene;
        PlayerEntity p = _host.Player;
        if (!_thirdPerson || ShipOpen || p.IsAltForm || CameraSequence.Current != null)
        {
            _tpDist = 0;
            scene.HostCameraOffset = Vector3.Zero;
            return;
        }
        // a full room load (a portal, a checkpoint respawn) makes a new main player whose camera is all zeros until it
        // spawns: nothing to offset yet. Inverting that gave NaN, which _tpDist's easing then kept forever (black screen)
        if (!(MathF.Abs(p.CameraInfo.ViewMatrix.Determinant) > 1e-3f))
        {
            _tpDist = float.MaxValue; // straight out again once it has a camera
            scene.HostCameraOffset = Vector3.Zero;
            return;
        }
        Matrix4 cam = p.CameraInfo.ViewMatrix.Inverted(); // rows: right, up, back, position
        Vector3 eye = cam.Row3.Xyz;
        Vector3 want = cam.Row2.Xyz.Normalized() * TpBack + cam.Row1.Xyz.Normalized() * TpUp + cam.Row0.Xyz.Normalized() * TpSide;
        float len = want.Length;
        Vector3 dir = want / len;
        float allowed = len;
        CollisionResult res = default;
        if (CollisionDetection.CheckBetweenPoints(eye, eye + dir * (len + TpMargin), TestFlags.Players, scene, ref res))
        {
            allowed = Math.Clamp(res.Distance * (len + TpMargin) - TpMargin, 0, len);
        }
        // snap in toward a wall, ease back out
        _tpDist = allowed < _tpDist ? allowed : _tpDist + (allowed - _tpDist) * 0.15f;
        scene.HostCameraOffset = dir * _tpDist;
    }

    // ---------------------------------------------------------------- GL objects

    int _program;
    int _width, _height;
    readonly Dictionary<string, int> _u = new();
    int U(string name) => _u.TryGetValue(name, out int loc) ? loc : _u[name] = GLES30.GlGetUniformLocation(_program, name);
    // the solid / decal / translucent passes (MphRead.Rendering.LayeredDraw) and this frame's scene for their DrawItem calls
    readonly GlesLayerGl _layerGl = new(-1);
    LayeredDraw<RenderItem>? _layers;
    Scene? _layerScene;

    sealed class GpuMesh { public int Vao, Vbo, Count; public long LastUsed; }
    sealed class GpuTexture { public int Tex, Version; public long LastUsed; }
    readonly Dictionary<int, GpuMesh> _meshes = new();
    readonly Dictionary<int, GpuTexture> _textures = new();
    readonly int[] _samplers = new int[9]; // [xRepeat * 3 + yRepeat]
    int _dynVao, _dynVbo;
    // HUD pass (MphRead's HUD, recorded by Scene.RecordHud): its own quad shader and one texture per interned sprite
    int _hudProgram, _hudVao, _hudVbo;
    Java.Nio.FloatBuffer _hudBuffer = null!;
    readonly float[] _hudVerts = new float[16];
    readonly Dictionary<string, int> _hu = new();
    int HU(string name) => _hu.TryGetValue(name, out int loc) ? loc : _hu[name] = GLES30.GlGetUniformLocation(_hudProgram, name);
    readonly Dictionary<int, int> _hudSpriteTex = new();
    Java.Nio.FloatBuffer _dynBuffer = null!;
    // every particle / single-trail quad of the frame, 4 vertices each, uploaded once per frame (UploadQuads)
    float[] _quadVerts = new float[64 * 4 * DsDisplayList.Stride];
    readonly Dictionary<RenderItem, (int First, int Count)> _quadIndex = new(ReferenceEqualityComparer.Instance);
    readonly float[] _mtx = new float[16];
    long _frame;

    public void OnSurfaceCreated(IGL10? gl, Javax.Microedition.Khronos.Egl.EGLConfig? config)
    {
        // a new context invalidates every GL object
        _meshes.Clear();
        _textures.Clear();
        _warmScene = null; // the movie warm-up starts over (#37b)
        _u.Clear();
        _hu.Clear();
        _hudSpriteTex.Clear();
        CampaignGlCache.Reset();
        _gun?.ResetGl();
        _suit?.ResetGl();
        if (_gearLoad?.Task.IsCompletedSuccessfully == true)
        {
            var (g, s) = _gearLoad.Task.Result;
            g?.ResetGl();
            s?.ResetGl();
        }
        ResetMovieGl();
        PauseMenu?.OnSurfaceCreated();
        ShipGlReset(); // CampaignShip.cs
        MapGlReset(); // CampaignMap.cs
        ViewGlReset(); // CampaignView.cs
        ArenaGlReset(); // CampaignArena.cs
        _program = Link(SceneShaderSource.EsVertex, SceneShaderSource.EsFragment);
        _hudProgram = Link(HudVertexShader, HudFragmentShader);
        {
            var hid = new int[1];
            GLES30.GlGenVertexArrays(1, hid, 0); _hudVao = hid[0];
            GLES30.GlGenBuffers(1, hid, 0); _hudVbo = hid[0];
            GLES30.GlBindVertexArray(_hudVao);
            GLES30.GlBindBuffer(GLES30.GlArrayBuffer, _hudVbo);
            GLES30.GlBufferData(GLES30.GlArrayBuffer, _hudVerts.Length * 4, null, GLES30.GlDynamicDraw);
            GLES30.GlVertexAttribPointer(0, 2, GLES30.GlFloat, false, 16, 0);
            GLES30.GlVertexAttribPointer(1, 2, GLES30.GlFloat, false, 16, 8);
            GLES30.GlEnableVertexAttribArray(0);
            GLES30.GlEnableVertexAttribArray(1);
            GLES30.GlBindVertexArray(0);
            var hb = Java.Nio.ByteBuffer.AllocateDirect(_hudVerts.Length * 4);
            hb.Order(Java.Nio.ByteOrder.NativeOrder());
            _hudBuffer = hb.AsFloatBuffer()!;
            GLES30.GlUseProgram(_hudProgram);
            GLES30.GlUniform1i(HU("tex"), 0);
            GLES30.GlUniform1i(HU("mask"), 1);
        }
        GLES30.GlGenSamplers(9, _samplers, 0);
        for (int x = 0; x < 3; x++)
        {
            for (int y = 0; y < 3; y++)
            {
                int s = _samplers[x * 3 + y];
                GLES30.GlSamplerParameteri(s, GLES30.GlTextureMinFilter, GLES30.GlNearest);
                GLES30.GlSamplerParameteri(s, GLES30.GlTextureMagFilter, GLES30.GlNearest);
                GLES30.GlSamplerParameteri(s, GLES30.GlTextureWrapS, Wrap(x));
                GLES30.GlSamplerParameteri(s, GLES30.GlTextureWrapT, Wrap(y));
            }
        }
        var ids = new int[1];
        GLES30.GlGenVertexArrays(1, ids, 0); _dynVao = ids[0];
        GLES30.GlGenBuffers(1, ids, 0); _dynVbo = ids[0];
        GLES30.GlBindVertexArray(_dynVao);
        GLES30.GlBindBuffer(GLES30.GlArrayBuffer, _dynVbo);
        GLES30.GlBufferData(GLES30.GlArrayBuffer, _quadVerts.Length * 4, null, GLES30.GlDynamicDraw);
        SetAttribs();
        GLES30.GlBindVertexArray(0);
        GpuTimerInit();
        var bb = Java.Nio.ByteBuffer.AllocateDirect(4 * DsDisplayList.Stride * 4);
        bb.Order(Java.Nio.ByteOrder.NativeOrder());
        _dynBuffer = bb.AsFloatBuffer()!;
        GLES30.GlUseProgram(_program);
        GLES30.GlUniform1i(U("u_texture"), 0);
        GLES30.GlUniform1i(U("u_vertexColors"), 1);
        var toon = new float[32 * 3];
        for (int i = 0; i < Metadata.ToonTable.Count && i < 32; i++)
        {
            toon[i * 3] = Metadata.ToonTable[i].X;
            toon[i * 3 + 1] = Metadata.ToonTable[i].Y;
            toon[i * 3 + 2] = Metadata.ToonTable[i].Z;
        }
        GLES30.GlUniform3fv(U("u_toonRamp"), 32, toon, 0);
    }

    static int Wrap(int mode) => mode switch
    {
        (int)RepeatMode.Repeat => GLES30.GlRepeat,
        (int)RepeatMode.Mirror => GLES30.GlMirroredRepeat,
        _ => GLES30.GlClampToEdge
    };

    static void SetAttribs()
    {
        const int stride = DsDisplayList.Stride * 4;
        GLES30.GlVertexAttribPointer(0, 3, GLES30.GlFloat, false, stride, 0);
        GLES30.GlVertexAttribPointer(1, 3, GLES30.GlFloat, false, stride, 3 * 4);
        GLES30.GlVertexAttribPointer(2, 4, GLES30.GlFloat, false, stride, 6 * 4);
        GLES30.GlVertexAttribPointer(3, 2, GLES30.GlFloat, false, stride, 10 * 4);
        GLES30.GlVertexAttribPointer(4, 1, GLES30.GlFloat, false, stride, 12 * 4);
        for (int i = 0; i < 5; i++)
        {
            GLES30.GlEnableVertexAttribArray(i);
        }
    }

    public void OnSurfaceChanged(IGL10? gl, int width, int height)
    {
        _width = width;
        _height = height;
        GLES30.GlViewport(0, 0, width, height);
        if (_host == null && _error == null && !ShipOpen)
        {
            try
            {
                var sw = Stopwatch.StartNew();
                System.IO.Directory.SetCurrentDirectory(_filesDir);
                Paths.UpdatePaths();
                Paths.ChooseMphPath();
                InitAudio(); // CampaignAudio.cs -- Music.Host must be set before the very first room's own music
                // the gun and suit load on a worker while the room loads below, and make their GL objects before the
                // first frame (FinishGearLoad): started after the room, they took 1.6 s with MPH's own arm cannon on
                // screen meanwhile (2026-10-03 VDO 2 Slench run)
                StartGearLoad(gun: true);
                if (!TryStartMatch(width, height) && !TryStartAtShip())
                {
                    _host = CampaignHost.Start(_room, setupSave: _allWeapons ? GiveAllWeapons : null, hunter: _hunter,
                        collectDrawItems: true, viewWidth: width, viewHeight: height);
                }
                Log.Info("MPHCampaign", $"started {_room} in {sw.ElapsedMilliseconds} ms ({width}x{height})");
                FinishGearLoad();
            }
            catch (Exception ex)
            {
                _error = $"start failed: {ex.GetType().Name}: {ex.Message}";
                Log.Error("MPHCampaign", ex.ToString());
                StatusSink?.Invoke(_error);
            }
        }
        else if (_host != null)
        {
            _host.Scene.Size = new Vector2i(width, height);
        }
    }

    // ---------------------------------------------------------------- frame

    double _accum;
    readonly Stopwatch _clock = Stopwatch.StartNew();
    double _lastTime;
    double _simMs, _drawMs;
    int _statusCountdown;
    int _fpsFrames;
    double _fpsStart;
    float _fps;

    public void OnDrawFrame(IGL10? gl)
    {
        _frame++;
        // the in-ship menu's OPTIONS may have changed the aim settings: reload them when it closes
        if (_aimShipWasOpen && !ShipOpen) ReloadAimFromSettings();
        _aimShipWasOpen = ShipOpen;
        double now = _clock.Elapsed.TotalSeconds;
        double frameInterval = now - _lastTime;
        double dt = Math.Min(frameInterval, 0.1);
        _lastTime = now;
        _perfSimMs = 0; _perfSteps = 0;
        _perfMeshN = 0; _perfMeshMs = 0; _perfTexN = 0; _perfTexMs = 0;
        BackFrame();
        if (_openPauseMenu)
        {
            _openPauseMenu = false;
            if (_host != null && !ShipOpen)
            {
                DumpInputTrace();
                PauseMenu?.Open();
            }
        }
        bool menuOpen = PauseMenu?.IsOpen == true;
        if (menuOpen)
        {
            while (_pauseInput.TryDequeue(out var action))
            {
                action(PauseMenu!);
            }
            menuOpen = PauseMenu!.IsOpen;
        }
        else
        {
            while (_pauseInput.TryDequeue(out _)) { }
        }
        ReassertSfxHost(); // CampaignAudio.cs
        UpdateAudioPause(_paused || menuOpen);
        MapFrame(); // Select's pause map opens / closes (CampaignMap.cs)
        if (ShipOpen)
        {
            _accum = 0;
            ShipInput();
        }
        else if (MapOpen)
        {
            _accum = 0; // the game waits under the map, as vanilla's pause screen
            MapInput();
        }
        else if (_paused)
        {
            _accum = 0;
            PausedInput();
        }
        else if (menuOpen && !NetKeepsRunning) // a LAN match runs on under the menu (MatchSession.cs)
        {
            _accum = 0; // the game waits under the pause menu
        }
        else if (_host != null && _error == null)
        {
            // MphRead's simulation runs at 60 steps per second (its frame counters assume it)
            _accum += dt;
            int steps = 0;
            var sim = Stopwatch.StartNew();
            long simAlloc = GC.GetAllocatedBytesForCurrentThread();
            while (_accum >= 1 / 60.0 && steps < 4 && !_host.Ended && !MovieHoldsSim)
            {
                _accum -= 1 / 60.0;
                steps++;
                try
                {
                    SetPlayerHooks(true);
                    try
                    {
                        NetStep(BuildInput()); // _host.Step (which also ticks Sfx.Update), plus a LAN match's networking (MatchSession.cs)
                    }
                    finally
                    {
                        SetPlayerHooks(false);
                    }
                    _gun?.Viewmodel.Update(_host.Player, 1 / 60f);
                    StepBossSkip(); // CampaignBossSkip.cs: dev "Escape tests" start, a no-op otherwise
                    if (!_host.Ended)
                    {
                        _suit?.Tick(_host.Player);
                        UpdateThirdPerson();
                    }
                }
                catch (Exception ex)
                {
                    _error = $"sim error: {ex.GetType().Name}: {ex.Message}";
                    Log.Error("MPHCampaign", ex.ToString());
                    break;
                }
            }
            _paceSimAlloc += GC.GetAllocatedBytesForCurrentThread() - simAlloc;
            _paceFrameSim = sim.Elapsed.TotalMilliseconds;
            if (steps > 0)
            {
                _simMs = sim.Elapsed.TotalMilliseconds / steps;
                _perfSimMs = sim.Elapsed.TotalMilliseconds;
                _perfSteps = steps;
            }
            // a shot door loads the next room on a worker thread; if that throws, the door never opens -- say why
            if (MphRead.Entities.RoomEntity.TransitionFailure is Exception roomLoad)
            {
                MphRead.Entities.RoomEntity.TransitionFailure = null;
                _error = $"room load failed: {roomLoad.GetType().Name}: {roomLoad.Message}";
                Log.Error("MPHCampaign", "room load failed: " + roomLoad);
            }
            if (_host.Ended && _error == null)
            {
                if (!TryEndMatch()) // a match holds its results (MatchSession.cs)
                    OnHostEnded(); // boarded the ship, an ending, or quit at game over
            }
        }
        UpdateGear(); // the suit / gun GL objects, a few per frame, and a suit change's swap
        MovieUpdate(_paused || menuOpen ? 0 : dt); // in-game cutscenes (CampaignMovie.cs)
        var draw = Stopwatch.StartNew();
        long drawAlloc = GC.GetAllocatedBytesForCurrentThread();
        GpuTimerBegin();
        Render();
        if (ShipOpen) ShipDraw(); // the in-ship menu / take-off movie (CampaignShip.cs)
        if (MapOpen) MapDraw(); // Select's pause map (CampaignMap.cs)
        if (PauseMenu?.IsOpen == true)
        {
            PauseMenu.Draw(_width, _height);
        }
        GpuTimerEnd();
        _drawMs = draw.Elapsed.TotalMilliseconds;
        _paceDrawAlloc += GC.GetAllocatedBytesForCurrentThread() - drawAlloc;
        if (++_fpsFrames >= 60)
        {
            _fps = (float)(_fpsFrames / (now - _fpsStart));
            _fpsStart = now;
            _fpsFrames = 0;
        }
        if (--_statusCountdown <= 0)
        {
            _statusCountdown = 30;
            StatusSink?.Invoke(Status());
        }
        int room = _host?.RoomId ?? -1;
        if (room != _evictRoom)
        {
            _evictRoom = room;
            _roomEnteredFrame = _frame;
        }
        if (_frame % 600 == 0)
        {
            Evict();
        }
        TracePerf(now, frameInterval);
    }

    // Slow-frame log (owner's Guardian-spawn drops, 2026-10-02): any frame that took over 16 ms of GL-thread work, or
    // came over 20 ms after the previous one, with where the time went -- sim steps vs drawing, the lazy mesh/texture
    // uploads (first draw of a model this room), models the sim registered (a spawn of a model not seen yet) and .NET
    // gen0 GCs. `adb logcat -s MPHPerf`. The interval line belongs to the PREVIOUS frame's work ("prev work").
    double _perfSimMs, _perfMeshMs, _perfTexMs, _perfPrevWork;
    int _perfSteps, _perfMeshN, _perfTexN, _perfMeshes, _perfTextures, _perfGc0, _perfSecond, _perfLines;

    void TracePerf(double frameStart, double frameInterval)
    {
        double work = (_clock.Elapsed.TotalSeconds - frameStart) * 1000;
        Scene? scene = _host?.Scene;
        int meshes = scene?.HostMeshes.Count ?? 0, textures = scene?.HostTextures.Count ?? 0;
        int gc0 = GC.CollectionCount(0);
        double interval = frameInterval * 1000;
        if (_frame > 2 && (work > 16 || interval > 20) && !ShipOpen && !_paused && PauseMenu?.IsOpen != true)
        {
            int second = (int)frameStart;
            if (second != _perfSecond)
            {
                _perfSecond = second;
                _perfLines = 0;
            }
            if (++_perfLines <= 20)
            {
                Log.Info("MPHPerf", $"frame {_frame} room {_host?.RoomId}: interval {interval:0} ms (prev work {_perfPrevWork:0}), " +
                    $"work {work:0} ms = sim {_perfSimMs:0} ({_perfSteps} steps) + draw {_drawMs:0} + suit setup {_perfGearMs:0}; quads {_perfQuads}; uploads {_perfMeshN} meshes " +
                    $"{_perfMeshMs:0} ms, {_perfTexN} textures {_perfTexMs:0} ms; new models: meshes +{meshes - _perfMeshes}, " +
                    $"textures +{textures - _perfTextures}; gc0 +{gc0 - _perfGc0}");
            }
        }
        TracePace(frameStart, interval, work);
        _perfPrevWork = work;
        _perfMeshes = meshes;
        _perfTextures = textures;
        _perfGc0 = gc0;
    }

    // Frame pacing, one MPHPerf "pace" line a second (owner 10-03: drops all through the Slench fight, mostly while it
    // rolls, hovers or fires from the ceiling; MPHPerf's slow-frame lines only catch > 20 ms). Per second: frames, how
    // many missed a 120 Hz refresh (interval > 9 ms) or two (> 17) or took > 25 ms; the GL thread's work (sim, draw) and
    // the GPU's (whole-frame timer); .NET GCs (gen0, gen1+) and ART GCs (count, ms: the Mono GC bridge runs one on the
    // thread that collected); managed bytes the sim and the drawing allocated on the GL thread; Slench's states that
    // second (a bitmask read through a cached field, so the trace itself allocates next to nothing).
    int _paceSecond = -1, _paceFrames, _paceMiss9, _paceMiss17, _paceMiss25, _paceGc0, _paceGc1, _paceQuadsMax,
        _paceItemsMax, _paceGpuN, _paceStates;
    double _paceFrameSim, _paceIntervalMax, _paceWorkSum, _paceWorkMax, _paceSimSum, _paceDrawSum, _paceGpuSum, _paceGpuMax;
    long _paceSimAlloc, _paceDrawAlloc, _paceArtCount = -1, _paceArtMs;
    EntityBase? _paceBoss;
    System.Reflection.FieldInfo? _paceBossField;

    void TracePace(double frameStart, double interval, double work)
    {
        bool counted = _frame > 2 && !ShipOpen && !_paused && PauseMenu?.IsOpen != true && !MovieShowing;
        if (counted)
        {
            _paceFrames++;
            if (interval > 9) _paceMiss9++;
            if (interval > 17) _paceMiss17++;
            if (interval > 25) _paceMiss25++;
            _paceIntervalMax = Math.Max(_paceIntervalMax, interval);
            _paceWorkSum += work;
            _paceWorkMax = Math.Max(_paceWorkMax, work);
            _paceSimSum += _paceFrameSim;
            _paceDrawSum += _drawMs;
            _paceQuadsMax = Math.Max(_paceQuadsMax, _perfQuads);
            _paceItemsMax = Math.Max(_paceItemsMax, _perfItems);
            if (_paceBoss != null && _paceBossField?.GetValue(_paceBoss) is byte state && state < 32)
            {
                _paceStates |= 1 << state;
            }
        }
        _paceFrameSim = 0;
        int second = (int)frameStart;
        if (second == _paceSecond)
        {
            return;
        }
        int gc0 = GC.CollectionCount(0), gc1 = GC.CollectionCount(1);
        long artCount = ArtStat("art.gc.gc-count"), artMs = ArtStat("art.gc.gc-time");
        if (_paceSecond >= 0 && _paceFrames > 0)
        {
            int n = _paceFrames;
            string states = "";
            if (_paceBoss is MphRead.Entities.Enemies.Enemy41Entity)
            {
                for (int i = 0; i < 32; i++)
                {
                    if ((_paceStates & (1 << i)) != 0)
                    {
                        states += (states.Length > 0 ? "/" : "") + (MphRead.Entities.Enemies.SlenchState)i;
                    }
                }
                states = "; Slench " + (states.Length > 0 ? states : "-");
            }
            else if (_paceBoss != null)
            {
                states = $"; Cretaphid states 0x{_paceStates:X}";
            }
            Log.Info("MPHPerf", $"pace room {_host?.RoomId ?? -1}: {n} frames, missed 120 Hz {_paceMiss9} (>17 ms {_paceMiss17}, "
                + $">25 ms {_paceMiss25}), max {_paceIntervalMax:0} ms; GL work avg {_paceWorkSum / n:0.0} max {_paceWorkMax:0.0} "
                + $"(sim {_paceSimSum / n:0.0} + draw {_paceDrawSum / n:0.0}); GPU avg "
                + (_paceGpuN > 0 ? $"{_paceGpuSum / _paceGpuN:0.0} max {_paceGpuMax:0.0}" : "-")
                + $"; GC .NET {gc0 - _paceGc0} (gen1+ {gc1 - _paceGc1}), ART {artCount - _paceArtCount} ({artMs - _paceArtMs} ms); "
                + $"alloc sim {_paceSimAlloc / 1024} KB, draw {_paceDrawAlloc / 1024} KB; quads max {_paceQuadsMax}, items max {_paceItemsMax}{states}");
        }
        _paceSecond = second;
        _paceFrames = _paceMiss9 = _paceMiss17 = _paceMiss25 = _paceQuadsMax = _paceItemsMax = _paceGpuN = _paceStates = 0;
        _paceIntervalMax = _paceWorkSum = _paceWorkMax = _paceSimSum = _paceDrawSum = _paceGpuSum = _paceGpuMax = 0;
        _paceSimAlloc = _paceDrawAlloc = 0;
        _paceGc0 = gc0;
        _paceGc1 = gc1;
        _paceArtCount = artCount;
        _paceArtMs = artMs;
        // the boss for next second's states: Slench (_state1 = SlenchState) or Cretaphid
        _paceBoss = null;
        if (_host?.Scene is Scene scene)
        {
            foreach (EntityBase e in scene.Entities)
            {
                if (e is MphRead.Entities.Enemies.Enemy41Entity || e is MphRead.Entities.Enemies.Enemy19Entity)
                {
                    _paceBoss = e;
                    break;
                }
            }
        }
        _paceBossField = null;
        for (Type? t = _paceBoss?.GetType(); t != null && _paceBossField == null; t = t.BaseType)
        {
            _paceBossField = t.GetField("_state1", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic
                | System.Reflection.BindingFlags.Public);
        }
    }

    static long ArtStat(string name)
    {
        try
        {
            return Int64.TryParse(Android.OS.Debug.GetRuntimeStat(name), out long v) ? v : 0;
        }
        catch (Exception)
        {
            return 0;
        }
    }

    // previous step's button states, for press edges
    bool _prevA, _prevB, _prevR1, _prevUp, _prevDown, _prevLeft, _prevRight;

    CampaignInput BuildInput()
    {
        PadState pad = Pad;
        PlayerEntity p = _host!.Player;
        bool a = pad[Keycode.ButtonA], b = pad[Keycode.ButtonB], r1 = pad[Keycode.ButtonR1];
        bool up = pad.HatY <= -0.5f || pad[Keycode.DpadUp], down = pad.HatY >= 0.5f || pad[Keycode.DpadDown];
        bool left = pad.HatX <= -0.5f || pad[Keycode.DpadLeft], right = pad.HatX >= 0.5f || pad[Keycode.DpadRight];
        bool pressA = a && !_prevA, pressB = b && !_prevB, pressR1 = r1 && !_prevR1;
        // L3 cycles the status text when it is let go -- unless it was half of L3 + R3 (the gun-adjust pause)
        bool l3 = pad[Keycode.ButtonThumbl];
        if (!l3 && _prevL3)
        {
            if (!_gunComboUsed) _statusMode = (_statusMode + 1) % 3;
            _gunComboUsed = false;
        }
        _prevL3 = l3;
        bool pressUp = up && !_prevUp, pressDown = down && !_prevDown, pressLeft = left && !_prevLeft, pressRight = right && !_prevRight;
        _prevA = a; _prevB = b; _prevR1 = r1; _prevUp = up; _prevDown = down; _prevLeft = left; _prevRight = right;
        bool r2 = pad.R2 >= 0.5f || pad[Keycode.ButtonR2], l2 = pad.L2 >= 0.5f || pad[Keycode.ButtonL2];

        var input = new CampaignInput { Move = new System.Numerics.Vector2(pad.Lx, -pad.Ly), SelectWeapon = BeamType.None };
        // dialogs (the DS's touch-screen OK / YES / NO / page arrows), same in every layout
        if (_host.DialogPaused)
        {
            bool yesNo = _host.Dialog == DialogType.YesNo;
            input.DialogButton = pressA ? (yesNo ? HostDialogButton.Yes : HostDialogButton.Advance)
                : pressB && yesNo ? HostDialogButton.No
                : pressLeft ? HostDialogButton.Left
                : pressRight ? HostDialogButton.Right
                : HostDialogButton.None;
            return input;
        }
        var buttons = CampaignButtons.None;
        if (_layout == ControlLayout.Prime)
        {
            // Metroid Prime Remastered "Dual Sticks", by button position (Odin: A bottom, B right, X left, Y top)
            bool wheel = pad[Keycode.ButtonY];
            if (wheel)
            {
                buttons |= CampaignButtons.WeaponMenu;
                input.WeaponWheel = new System.Numerics.Vector2(pad.Rx, -pad.Ry);
            }
            else
            {
                input.AimDelta = StickAim(pad);
            }
            if (r2 || b)
            {
                buttons |= CampaignButtons.Shoot;
            }
            if (a || pad[Keycode.ButtonL1])
            {
                buttons |= CampaignButtons.Jump | CampaignButtons.Boost;
            }
            if (pad[Keycode.ButtonX])
            {
                buttons |= CampaignButtons.Morph;
            }
            if (l2)
            {
                buttons |= p.ScanVisor ? CampaignButtons.Scan : CampaignButtons.Zoom;
            }
            if (pad[Keycode.ButtonThumbr])
            {
                buttons |= CampaignButtons.Zoom;
            }
            if (pressRight || pressUp && p.ScanVisor)
            {
                buttons |= CampaignButtons.ScanVisor;
            }
            else if (pressUp)
            {
                input.SelectWeapon = BeamType.PowerBeam;
            }
            if (pressDown)
            {
                input.SelectWeapon = BeamType.Missile;
            }
            if (pressLeft)
            {
                buttons |= CampaignButtons.AffinitySlot;
            }
            if (pressR1)
            {
                input.SelectWeapon = p.CurrentWeapon == BeamType.Missile ? BeamType.PowerBeam : BeamType.Missile;
            }
        }
        else
        {
            // the first on-device layout, kept for reference
            input.AimDelta = StickAim(pad);
            if (r2) buttons |= CampaignButtons.Shoot;
            if (a) buttons |= CampaignButtons.Jump | CampaignButtons.Boost;
            if (b) buttons |= CampaignButtons.Morph;
            if (pad[Keycode.ButtonY]) buttons |= CampaignButtons.ScanVisor;
            if (pad[Keycode.ButtonX]) buttons |= CampaignButtons.Scan;
            if (l2 || pad[Keycode.ButtonThumbr]) buttons |= CampaignButtons.Zoom;
            if (r1 || right) buttons |= CampaignButtons.NextWeapon;
            if (pad[Keycode.ButtonL1] || left) buttons |= CampaignButtons.PrevWeapon;
            if (up) input.SelectWeapon = BeamType.Missile;
            else if (down) input.SelectWeapon = BeamType.PowerBeam;
        }
        // in the morph ball the fire button lays bombs (MphRead: the alt attack control)
        if (p.IsAltForm && (buttons & CampaignButtons.Shoot) != 0)
        {
            buttons |= CampaignButtons.AltAttack;
        }
        input.Buttons = buttons;
        System.Numerics.Vector2 gyro = GyroAimDelta(); // gyro aim, added to the stick's (CampaignGyro.cs)
        TraceInput(pad, input.AimDelta, gyro);
        input.AimDelta += gyro;
        return input;
    }

    // Input trace (owner, 2026-10-02: "my right stick got stuck drifting left for a moment -- was that the Odin or a
    // glitch?"): the last 12 s of sim steps -- the right stick as the Odin reported it, the aim it gave, the gyro's aim --
    // written to logcat (tag MPHInput, 0.1 s rows, quiet rows skipped) when the pause menu opens. Pause right after a
    // drift and the log says which one turned the view.
    const int TraceSteps = 12 * 60;
    readonly double[] _trT = new double[TraceSteps];
    readonly float[] _trRawX = new float[TraceSteps], _trRawY = new float[TraceSteps];
    readonly System.Numerics.Vector2[] _trStick = new System.Numerics.Vector2[TraceSteps], _trGyro = new System.Numerics.Vector2[TraceSteps];
    int _trNext, _trCount;

    void TraceInput(PadState pad, System.Numerics.Vector2 stick, System.Numerics.Vector2 gyro)
    {
        _trT[_trNext] = _clock.Elapsed.TotalSeconds;
        _trRawX[_trNext] = pad.RawRx;
        _trRawY[_trNext] = pad.RawRy;
        _trStick[_trNext] = stick;
        _trGyro[_trNext] = gyro;
        _trNext = (_trNext + 1) % TraceSteps;
        _trCount = Math.Min(_trCount + 1, TraceSteps);
    }

    void DumpInputTrace()
    {
        if (_trCount == 0)
        {
            return;
        }
        double end = _clock.Elapsed.TotalSeconds;
        Log.Info("MPHInput", $"last {_trCount / 60.0:0.0} s of right stick + gyro before this pause (0.1 s rows; raw = the Odin's " +
            "stick reading, -1 left .. +1 right, dead zone 0.15; aim = what each gave the game; quiet rows skipped)");
        int start = (_trNext - _trCount + TraceSteps) % TraceSteps;
        int i = 0, rows = 0;
        while (i < _trCount && rows < 130)
        {
            int first = (start + i) % TraceSteps;
            double bucket = Math.Floor((_trT[first] - end) * 10);
            float minX = 1, maxX = -1, maxAbsY = 0;
            var stick = System.Numerics.Vector2.Zero;
            var gyro = System.Numerics.Vector2.Zero;
            for (; i < _trCount; i++)
            {
                int k = (start + i) % TraceSteps;
                if (Math.Floor((_trT[k] - end) * 10) != bucket) break;
                minX = Math.Min(minX, _trRawX[k]);
                maxX = Math.Max(maxX, _trRawX[k]);
                maxAbsY = Math.Max(maxAbsY, Math.Abs(_trRawY[k]));
                stick += _trStick[k];
                gyro += _trGyro[k];
            }
            bool quiet = Math.Abs(minX) < 0.02f && Math.Abs(maxX) < 0.02f && maxAbsY < 0.02f
                && stick.LengthSquared() < 1e-6f && gyro.LengthSquared() < 1e-4f;
            if (!quiet)
            {
                rows++;
                Log.Info("MPHInput", $"{bucket / 10:+0.0;-0.0} s: raw x {minX:+0.00;-0.00}..{maxX:+0.00;-0.00} |y|<={maxAbsY:0.00} " +
                    $"-> stick aim {stick.X:+0.00;-0.00},{stick.Y:+0.00;-0.00}  gyro aim {gyro.X:+0.00;-0.00},{gyro.Y:+0.00;-0.00}");
            }
        }
    }

    int _statusMode; // 0 compact, 1 full, 2 off (L3 cycles)
    bool _prevL3;

    string Status()
    {
        if (_error != null)
        {
            return _error;
        }
        if (MatchStatusText() is string match) // scoreboard / results (MatchSession.cs)
        {
            return match;
        }
        if (ShipOpen || MovieShowing || MapOpen)
        {
            return ShipOpen || MapOpen ? "" : MovieStatus();
        }
        if (_host == null)
        {
            return "loading " + _room + "...";
        }
        if (_statusMode == 2)
        {
            return _paused ? PauseText() : "";
        }
        Scene scene = _host.Scene;
        PlayerEntity p = _host.Player;
        RoomMetadata? meta = Metadata.GetRoomById(_host.RoomId);
        if (_statusMode == 0)
        {
            return $"{meta?.InGameName ?? meta?.Name}  {_fps:0} fps  (L3: details)" + (_paused ? "\n" + PauseText() : "");
        }
        int items = scene.OpaqueItems.Count + scene.DecalItems.Count + scene.TranslucentItems.Count;
        return $"{meta?.InGameName ?? meta?.Name} [{_host.RoomId}]  {_fps:0} fps  sim {_simMs:0.0} ms  draw {_drawMs:0.0} ms  items {items}\n"
            + $"{_hunter}  energy {p.Health}  {p.CurrentWeapon}{(p.IsAltForm ? "  (alt form)" : "")}"
            + (MphRead.GameState.TransitionState != TransitionState.None ? $"  [room load: {MphRead.GameState.TransitionState}]" : "")
            + $"\nmeshes {_meshes.Count}  textures {_textures.Count}"
            + (_paused ? "\n" + PauseText() : "")
            + (_host.DialogPaused ? $"\n[{_host.Dialog}] A {(_host.Dialog == DialogType.YesNo ? "yes, B no" : "next / OK")}, D-pad page" : "")
            + (_layout == ControlLayout.Prime
                ? "\nPrime: R2/B fire+bomb, A/L1 jump+boost, X morph, hold Y + R stick weapon wheel, R1 missile, L2 scan (visor) / zoom,"
                  + "\n  D-pad: right scan visor, up Power Beam / leave visor, down Missile, left last special weapon; Start pause, Select exit"
                : "\nReference: R2 fire, A jump/boost, B morph, Y scan visor, X scan, L1/R1 weapon, Select exit");
    }

    // ---------------------------------------------------------------- drawing (port of MphRead's OnRenderFrame passes)

    void Render()
    {
        Scene? view = ViewScene; // the session's scene, or the planet select's deep space (CampaignShip.cs)
        Color4 clear = view?.ClearColor ?? new Color4(0, 0, 0, 1);
        GLES30.GlClearColor(clear.R, clear.G, clear.B, 1);
        GLES30.GlClearStencil(0);
        GLES30.GlDepthMask(true);
        GLES30.GlColorMask(true, true, true, true);
        GLES30.GlClear(GLES30.GlColorBufferBit | GLES30.GlDepthBufferBit | GLES30.GlStencilBufferBit);
        if (view == null || _error != null)
        {
            return;
        }
        if (MovieShowing)
        {
            MovieDraw();
            WarmSceneGl(view); // the scene's meshes/textures go up while the movie hides it (#37b)
            return;
        }
        EndWarmSceneGl();
        Scene scene = view;
        bool viewWarp = BeginViewWarp(scene); // view framing test toggle (CampaignView.cs)
        GLES30.GlUseProgram(_program);
        GLES30.GlEnable(GLES30.GlDepthTest);
        GLES30.GlEnable(GLES30.GlBlend);
        GLES30.GlBlendFunc(GLES30.GlSrcAlpha, GLES30.GlOneMinusSrcAlpha);
        SetMatrix(U("u_projection"), scene.PerspectiveMatrix);
        SetMatrix(U("u_view"), scene.ViewMatrix);
        GLES30.GlUniform1i(U("u_fogOn"), scene.FogEnabled ? 1 : 0);
        Vector4 fog = scene.FogColor;
        GLES30.GlUniform4f(U("u_fogColor"), fog.X, fog.Y, fog.Z, fog.W);
        GLES30.GlUniform1f(U("u_fogNear"), scene.FogMin);
        GLES30.GlUniform1f(U("u_fogFar"), scene.FogMax);
        IReadOnlyList<RenderItem> opaque = scene.OpaqueItems, decal = scene.DecalItems, trans = scene.TranslucentItems;
        _layerGl.AlphaPassLocation = U("u_alphaPass");
        UploadQuads(opaque, decal, trans);
        _perfItems = opaque.Count + decal.Count + trans.Count;

        // the Prime gun replaces MPH's when MPH drew its first-person SamusGun this frame
        _gunActive = _gun != null && _primeGun && !ShipOpen && GunViewmodel.TryMphLightColour(scene, out _gunTint);

        UpdateBodyCut(scene);
        GpuNoteDrawn(opaque, decal, trans);
        // solids, decals, then translucent surfaces in two layers (MphRead.Rendering.LayeredDraw); the Prime gun, an
        // imported arena and the HD suit draw between the phases
        _layerScene = scene;
        LayeredDraw<RenderItem> layers = _layers ??= new LayeredDraw<RenderItem>(_layerGl, item => item.PolygonId, item => DrawItem(item, _layerScene!));
        GpuMark(GpuSegOpaque);
        layers.BeginSolid();
        // the gun's depth first: the room behind it and its own hidden faces skip their shading (CampaignGun.GunPass)
        if (_gunActive) DrawPrimeGun(scene, GLES30.GlLess, CampaignGun.GunPass.Prepass);
        DrawArena(scene, 0, GLES30.GlLess); // an imported Echoes arena's own look (CampaignArena.cs)
        layers.DrawSolidItems(opaque);
        if (_gunActive) DrawPrimeGun(scene, GLES30.GlLess, CampaignGun.GunPass.AfterPrepass);
        DrawSuit(scene, SuitPass.Opaque);
        GLES30.GlDepthFunc(GLES30.GlLess);
        layers.EndSolid();
        GpuMark(GpuSegDecal);
        layers.BeginDecal();
        layers.DrawDecalItems(decal);
        layers.EndDecal();
        GpuMark(GpuSegStencil);
        layers.BeginMarking();
        layers.DrawMarkingItems(trans);
        GpuMark(GpuSegDepth);
        layers.BeginDepthRebuild();
        DrawArena(scene, 0, GLES30.GlLequal); // CampaignArena.cs
        layers.DrawDepthRebuildItems(opaque);
        if (_gunActive) DrawPrimeGun(scene, GLES30.GlLequal, CampaignGun.GunPass.DepthRebuild);
        DrawSuit(scene, SuitPass.DepthRebuild); // the suit's depth only (CampaignSuit)
        GLES30.GlDepthFunc(GLES30.GlLequal);
        GpuMark(GpuSegBehind);
        layers.BeginBackLayer();
        layers.DrawBackLayerItems(trans);
        GpuMark(GpuSegFront);
        layers.BeginFrontLayer();
        layers.DrawFrontLayerItems(trans);
        layers.Finish();
        GLES30.GlDisable(CullFaceCap);
        GpuMark(GpuSegSuit);
        DrawArena(scene, 1, GLES30.GlLequal); // the Echoes arena's blended surfaces (CampaignArena.cs)
        // the HD suit's see-through and glowing parts, over everything else in the room
        DrawSuit(scene, SuitPass.Translucent);
        DrawSuit(scene, SuitPass.Additive);
        GLES30.GlDepthMask(true);
        GLES30.GlDepthFunc(GLES30.GlLess);
        if (viewWarp) EndViewWarp(scene); // the Panini warp onto the screen, under the HUD (CampaignView.cs)
        GpuMark(GpuSegHud);
        if (!ShipOpen) DrawHud(scene); // the cockpit view under the in-ship menu has no HUD (CampaignShip.cs)
        GLES30.GlBindVertexArray(0);
    }

    // MphRead's HUD pass (OnRenderFrame after the world): helmet/visor/dialog layers and every sprite, in the order
    // MphRead recorded them, then the screen fade on top
    void DrawHud(Scene scene)
    {
        GLES30.GlDisable(GLES30.GlDepthTest);
        GLES30.GlEnable(GLES30.GlBlend);
        GLES30.GlBlendFunc(GLES30.GlSrcAlpha, GLES30.GlOneMinusSrcAlpha);
        GLES30.GlUseProgram(_hudProgram);
        GLES30.GlBindVertexArray(_hudVao);
        GLES30.GlUniform1f(HU("view_width"), _width);
        GLES30.GlUniform1f(HU("view_height"), _height);
        GLES30.GlUniform4f(HU("fade_color"), 0, 0, 0, 0);
        GLES30.GlBindSampler(0, _samplers[0]);
        GLES30.GlBindSampler(1, _samplers[0]);
        bool maskBound = false;
        for (int i = 0; i < scene.HudItemCount; i++)
        {
            Scene.HostHudItem item = scene.GetHudItem(i);
            bool textured;
            if (item.SpriteIndex >= 0)
            {
                textured = BindHudSprite(item.SpriteIndex, scene);
            }
            else
            {
                textured = BindTexture(item.BindingId, scene);
            }
            if (!textured)
            {
                continue;
            }
            bool mask = item.UseMask && scene.HudMaskBindingId != -1;
            if (mask && !maskBound)
            {
                GlNative.ActiveTexture(GLES30.GlTexture1);
                maskBound = BindTexture(scene.HudMaskBindingId, scene, unit: 1);
                GlNative.ActiveTexture(GLES30.GlTexture0);
            }
            GlNative.Uniform1i(HU("use_mask"), mask && maskBound ? 1 : 0);
            GlNative.Uniform1f(HU("alpha"), item.Alpha);
            DrawHudQuad(item.Left, item.Right, item.Top, item.Bottom);
        }
        float fade = scene.HostFadeAmount;
        if (fade > 0)
        {
            float c = scene.HostFadeColor;
            GLES30.GlUniform4f(HU("fade_color"), c, c, c, fade);
            DrawHudQuad(-1, 1, 1, -1);
        }
        GLES30.GlUseProgram(_program);
        GLES30.GlEnable(GLES30.GlDepthTest);
    }

    void DrawHudQuad(float left, float right, float top, float bottom)
    {
        // triangle strip: top right, top left, bottom right, bottom left (texcoord (0,0) = the quad's top-left)
        _hudVerts[0] = right; _hudVerts[1] = top; _hudVerts[2] = 1; _hudVerts[3] = 0;
        _hudVerts[4] = left; _hudVerts[5] = top; _hudVerts[6] = 0; _hudVerts[7] = 0;
        _hudVerts[8] = right; _hudVerts[9] = bottom; _hudVerts[10] = 1; _hudVerts[11] = 1;
        _hudVerts[12] = left; _hudVerts[13] = bottom; _hudVerts[14] = 0; _hudVerts[15] = 1;
        GlNative.BindBuffer(GLES30.GlArrayBuffer, _hudVbo);
        GlNative.BufferSubData(GLES30.GlArrayBuffer, _hudVerts, _hudVerts.Length);
        GlNative.DrawArrays(GLES30.GlTriangleStrip, 0, 4);
    }

    bool BindHudSprite(int index, Scene scene)
    {
        if (!_hudSpriteTex.TryGetValue(index, out int tex))
        {
            Scene.HostHudSprite sprite = scene.HudSprites[index];
            var ids = new int[1];
            GLES30.GlGenTextures(1, ids, 0);
            tex = ids[0];
            GLES30.GlActiveTexture(GLES30.GlTexture0);
            GLES30.GlBindTexture(GLES30.GlTexture2d, tex);
            UploadPixels(sprite.Pixels, sprite.Width, sprite.Height);
            _hudSpriteTex[index] = tex;
            return true;
        }
        GlNative.ActiveTexture(GLES30.GlTexture0);
        GlNative.BindTexture(GLES30.GlTexture2d, tex);
        return true;
    }

    static void UploadPixels(ColorRgba[] pixels, int width, int height)
    {
        byte[] bytes = MemoryMarshal.AsBytes(pixels.AsSpan(0, width * height)).ToArray();
        var bb = Java.Nio.ByteBuffer.AllocateDirect(bytes.Length);
        bb.Order(Java.Nio.ByteOrder.NativeOrder());
        bb.Put(bytes);
        bb.Position(0);
        GLES30.GlTexImage2D(GLES30.GlTexture2d, 0, GLES30.GlRgba, width, height, 0, GLES30.GlRgba, GLES30.GlUnsignedByte, bb);
    }

    // pass: CampaignGun.GunPass (pass 1 = Prepass before the room + AfterPrepass after it, pass 4 = DepthRebuild); Full
    // (Panini, after the warp) runs the same depth-first order in one go
    void DrawPrimeGun(Scene scene, int restoreDepthFunc, CampaignGun.GunPass pass = CampaignGun.GunPass.Full)
    {
        if (GunWaitsForWarp) return; // Panini: drawn after the warp instead, unwarped (CampaignView.cs)
        Matrix4 model = _gun!.Viewmodel.ModelMatrix(_host!.Player, _gun.Scale, _gun.RestMuzzle);
        // GunProjection: the gun's own projection, so no view mode resizes it (CampaignView.cs)
        Matrix4 proj = GunProjection(scene);
        double seconds = _clock.Elapsed.TotalSeconds;
        if (pass == CampaignGun.GunPass.Full && _gun.HasDepthPass)
        {
            GLES30.GlColorMask(false, false, false, false);
            _gun.Draw(model, scene.ViewMatrix, proj, _gunTint, seconds, CampaignGun.GunPass.Prepass);
            GLES30.GlColorMask(true, true, true, true);
            pass = CampaignGun.GunPass.AfterPrepass;
        }
        if (pass == CampaignGun.GunPass.Prepass)
        {
            GLES30.GlColorMask(false, false, false, false);
            _gun.Draw(model, scene.ViewMatrix, proj, _gunTint, seconds, pass);
            GLES30.GlColorMask(true, true, true, true);
        }
        else _gun.Draw(model, scene.ViewMatrix, proj, _gunTint, seconds, pass);
        GLES30.GlUseProgram(_program);
        GLES30.GlBlendFunc(GLES30.GlSrcAlpha, GLES30.GlOneMinusSrcAlpha);
        GLES30.GlDepthFunc(restoreDepthFunc);
    }

    // while paused: D-pad left/right = MPH's gun / the Prime gun, up/down = the Prime gun's size
    void PausedInput()
    {
        PadState pad = Pad;
        bool up = pad.HatY <= -0.5f || pad[Keycode.DpadUp], down = pad.HatY >= 0.5f || pad[Keycode.DpadDown];
        bool left = pad.HatX <= -0.5f || pad[Keycode.DpadLeft], right = pad.HatX >= 0.5f || pad[Keycode.DpadRight];
        if (_gun != null)
        {
            if (left && !_pPrevLeft || right && !_pPrevRight)
            {
                _primeGun = !_primeGun;
                _statusCountdown = 0;
            }
            if (up && !_pPrevUp)
            {
                _gun.Scale *= 1.05f;
                _statusCountdown = 0;
            }
            if (down && !_pPrevDown)
            {
                _gun.Scale /= 1.05f;
                _statusCountdown = 0;
            }
        }
        _pPrevUp = up; _pPrevDown = down; _pPrevLeft = left; _pPrevRight = right;
    }

    string PauseText()
    {
        string text = "PAUSED (Start to resume)";
        if (_gun != null)
        {
            text += $"\nFirst-person gun: {(_primeGun ? $"Prime ({System.IO.Path.GetFileName(_gunDir)}), size {_gun.Scale:0.000}" : "MPH")}"
                + "  -- D-pad left/right: MPH / Prime gun, up/down: size";
        }
        return text;
    }

    void DrawItem(RenderItem item, Scene scene)
    {
        if (_gunActive && GunViewmodel.IsMphGunItem(scene, item))
        {
            return; // drawn by the Prime gun instead
        }
        bool mesh = item.Type == RenderItemType.Mesh;
        if (!mesh && !IsQuadItem(item.Type))
        {
            return; // debug volumes: not drawn
        }
        // per-draw GL calls go through GlNative (straight to the driver), not GLES30's JNI -- see GlNative.cs
        LightInfo light = item.LightInfo;
        GlNative.Uniform3f(U("u_light0Dir"), light.Light1Vector.X, light.Light1Vector.Y, light.Light1Vector.Z);
        GlNative.Uniform3f(U("u_light0Color"), light.Light1Color.X, light.Light1Color.Y, light.Light1Color.Z);
        GlNative.Uniform3f(U("u_light1Dir"), light.Light2Vector.X, light.Light2Vector.Y, light.Light2Vector.Z);
        GlNative.Uniform3f(U("u_light1Color"), light.Light2Color.X, light.Light2Color.Y, light.Light2Color.Z);
        if (item.MatrixStackCount > 0)
        {
            GlNative.UniformMatrix4(U("u_bones"), item.MatrixStackCount, item.MatrixStack);
        }
        else
        {
            SetMatrix(U("u_bones"), item.Transform);
        }
        SetMatrix(U("u_billboard"), item.BillboardMode == BillboardMode.Sphere ? scene.ViewInvRotMatrix
            : item.BillboardMode == BillboardMode.Cylinder ? scene.ViewInvRotYMatrix : Matrix4.Identity);
        // DoMaterial
        GlNative.Uniform1i(U("u_lit"), scene.RoomLighting && item.Lighting ? 1 : 0);
        GlNative.Uniform3f(U("u_matDiffuse"), item.Diffuse.X, item.Diffuse.Y, item.Diffuse.Z);
        GlNative.Uniform3f(U("u_matAmbient"), item.Ambient.X, item.Ambient.Y, item.Ambient.Z);
        GlNative.Uniform3f(U("u_matSpecular"), item.Specular.X, item.Specular.Y, item.Specular.Z);
        GlNative.Uniform3f(U("u_matEmission"), item.Emission.X, item.Emission.Y, item.Emission.Z);
        GlNative.Uniform1f(U("u_matAlpha"), item.Alpha);
        GlNative.Uniform1i(U("u_polyMode"), (int)item.PolygonMode);
        // DoTexture
        bool textured = item.HasTexture && BindTexture(item.TextureBindingId, scene);
        if (textured)
        {
            GlNative.BindSampler(0, _samplers[(int)item.XRepeat * 3 + (int)item.YRepeat]);
            GlNative.Uniform1i(U("u_texgen"), (int)item.TexgenMode);
            SetMatrix(U("u_texMatrix"), item.TexcoordMatrix);
        }
        GlNative.Uniform1i(U("u_textured"), textured ? 1 : 0);
        if (item.OverrideColor is Vector4 oc)
        {
            GlNative.Uniform1i(U("u_colorOverrideOn"), 1);
            GlNative.Uniform4f(U("u_colorOverride"), oc.X, oc.Y, oc.Z, oc.W);
        }
        else
        {
            GlNative.Uniform1i(U("u_colorOverrideOn"), 0);
        }
        if (item.PaletteOverride is Vector4 po)
        {
            GlNative.Uniform1i(U("u_paletteOverrideOn"), 1);
            GlNative.Uniform4f(U("u_paletteOverride"), po.X, po.Y, po.Z, po.W);
        }
        else
        {
            GlNative.Uniform1i(U("u_paletteOverrideOn"), 0);
        }
        if (item.CullingMode == CullingMode.Neither)
        {
            GlNative.Disable(CullFaceCap);
        }
        else
        {
            GlNative.Enable(CullFaceCap);
            GlNative.CullFace(item.CullingMode == CullingMode.Front ? GLES30.GlFront : GLES30.GlBack);
        }
        if (mesh)
        {
            GpuMesh? gpu = GetMesh(item.ListId, scene);
            if (gpu != null && gpu.Count > 0)
            {
                GlNative.BindVertexArray(gpu.Vao);
                GlNative.DrawArrays(GLES30.GlTriangles, 0, gpu.Count);
            }
        }
        else
        {
            DrawQuad(item);
        }
    }

    // the render items drawn from the frame's dynamic vertex buffer (UploadQuads) instead of a mesh. Multi-segment trails
    // (owner queue #13, 2026-10-04: the Shock Coil showed only its muzzle effect) were skipped until then: the Shock
    // Coil, charged Volt Driver and Battlehammer trails (BeamProjectileEntity TrailMulti) and the boost ball's trail
    // (PlayerDraw TrailStack)
    static bool IsQuadItem(RenderItemType type) => type is RenderItemType.Particle or RenderItemType.TrailSingle
        or RenderItemType.TrailMulti or RenderItemType.TrailStack;

    // particles: 4 corners (a GL quad = triangle fan); single and multi trails: a quad strip (= triangle strip); trail
    // stacks: separate quads, written as 2 triangles each
    void DrawQuad(RenderItem item)
    {
        if (!_quadIndex.TryGetValue(item, out (int First, int Count) q))
        {
            return;
        }
        int mode = item.Type == RenderItemType.Particle ? GLES30.GlTriangleFan
            : item.Type == RenderItemType.TrailStack ? GLES30.GlTriangles : GLES30.GlTriangleStrip;
        GlNative.BindVertexArray(_dynVao);
        GlNative.DrawArrays(mode, q.First, q.Count);
    }

    // Every particle / single-trail quad of the frame into the dynamic buffer in ONE upload, before the passes; DrawQuad
    // then draws each from its offset. DrawQuad used to rewrite that one small buffer before every quad, and each
    // translucent item is drawn three times (passes 3, 5, 6): ~250 rewrites a frame of a buffer the GPU still needed
    // during the 10-03 Slench fight's tear volleys (44-82 particles), when frames came 20-60 ms apart while the GL
    // thread's own work was 3-5 ms. The fresh store each frame (orphaning) never waits on the GPU either.
    void UploadQuads(IReadOnlyList<RenderItem> opaque, IReadOnlyList<RenderItem> decal, IReadOnlyList<RenderItem> trans)
    {
        _quadIndex.Clear();
        int n = 0, verts = 0;
        AddQuads(opaque, ref n, ref verts);
        AddQuads(decal, ref n, ref verts);
        AddQuads(trans, ref n, ref verts);
        _perfQuads = n;
        if (n == 0)
        {
            return;
        }
        GlNative.BindBuffer(GLES30.GlArrayBuffer, _dynVbo);
        GLES30.GlBufferData(GLES30.GlArrayBuffer, _quadVerts.Length * 4, null, GLES30.GlDynamicDraw);
        GlNative.BufferSubData(GLES30.GlArrayBuffer, _quadVerts, verts * DsDisplayList.Stride);
    }

    void AddQuads(IReadOnlyList<RenderItem> list, ref int n, ref int verts)
    {
        for (int i = 0; i < list.Count; i++)
        {
            RenderItem item = list[i];
            if (!IsQuadItem(item.Type) || _quadIndex.ContainsKey(item))
            {
                continue;
            }
            int first = verts;
            // Points = (texcoord, position) pairs, as MphRead's Renderer reads them (RenderParticle / RenderTrailSingle /
            // RenderTrailMulti / RenderTrailStack); a trail stack's texcoord z is its matrix in the item's MatrixStack
            if (item.Type == RenderItemType.TrailMulti)
            {
                int count = Math.Min(item.ItemCount, item.Points.Length) / 2;
                if (count < 2)
                {
                    continue;
                }
                for (int v = 0; v < count; v++)
                {
                    PutQuadVertex(ref verts, item, v, scale: false, mtx: false);
                }
            }
            else if (item.Type == RenderItemType.TrailStack)
            {
                int quads = Math.Min(item.ItemCount, item.Points.Length / 8);
                for (int k = 0; k < quads; k++)
                {
                    // GL_QUADS v0 v1 v2 v3 -> triangles v0 v1 v2, v0 v2 v3
                    PutQuadVertex(ref verts, item, k * 4, scale: false, mtx: true);
                    PutQuadVertex(ref verts, item, k * 4 + 1, scale: false, mtx: true);
                    PutQuadVertex(ref verts, item, k * 4 + 2, scale: false, mtx: true);
                    PutQuadVertex(ref verts, item, k * 4, scale: false, mtx: true);
                    PutQuadVertex(ref verts, item, k * 4 + 2, scale: false, mtx: true);
                    PutQuadVertex(ref verts, item, k * 4 + 3, scale: false, mtx: true);
                }
            }
            else if (item.Points.Length >= 8)
            {
                // particles: 4 corners (a GL quad); single trails: a 4-vertex quad strip (= triangle strip)
                bool particle = item.Type == RenderItemType.Particle;
                for (int v = 0; v < 4; v++)
                {
                    PutQuadVertex(ref verts, item, v, scale: particle, mtx: false);
                }
            }
            if (verts > first)
            {
                _quadIndex[item] = (first, verts - first);
                n++;
            }
        }
    }

    // vertex v of the item: its texcoord (scaled by the material's S/T for particles, as RenderParticle) and position
    void PutQuadVertex(ref int verts, RenderItem item, int v, bool scale, bool mtx)
    {
        while ((verts + 1) * DsDisplayList.Stride > _quadVerts.Length)
        {
            Array.Resize(ref _quadVerts, _quadVerts.Length * 2);
        }
        Vector3 tc = item.Points[v * 2];
        Vector3 pos = item.Points[v * 2 + 1];
        int o = verts++ * DsDisplayList.Stride;
        _quadVerts[o] = pos.X; _quadVerts[o + 1] = pos.Y; _quadVerts[o + 2] = pos.Z;
        _quadVerts[o + 3] = 0; _quadVerts[o + 4] = 0; _quadVerts[o + 5] = 1;
        _quadVerts[o + 6] = 1; _quadVerts[o + 7] = 1; _quadVerts[o + 8] = 1; _quadVerts[o + 9] = DsDisplayList.MaterialColor;
        _quadVerts[o + 10] = scale ? tc.X * item.ScaleS : tc.X;
        _quadVerts[o + 11] = scale ? tc.Y * item.ScaleT : tc.Y;
        _quadVerts[o + 12] = mtx ? tc.Z : 0;
    }

    // GPU time per frame (GL_EXT_disjoint_timer_query: TIME_ELAPSED queries over the frame's drawing). MPHGpu logs each
    // frame the GPU itself took over 12 ms to draw, with that frame's particle quads and draw items: tells a GPU-bound
    // frame from one the GL thread or the driver held up. Results come back a few frames later.
    // 10-03 (session 10): a frame over 20 ms also logs what it drew (models per pass), the player's form and the boss.
    // GpuSplitTiming times every other frame in segments, one query each, back to back (clear, opaque, decal, stencil
    // mark, depth rebuild, translucent behind / in front, HD suit see-through + view warp, HUD + menus; 10 s averages of
    // split vs whole frames): it found the Slench waves (opaque + depth rebuild = the HD body, see UpdateBodyCut), but on
    // the Odin's Adreno it costs ~2.5 ms a frame by itself and one near-empty segment often reads back wrapped (~4295
    // ms, dropped), so it's off unless a GPU question needs it again.
    const bool GpuSplitTiming = false;
    const int TimeElapsedExt = 0x88BF, QueryResult = 0x8866, QueryResultAvailable = 0x8867, GpuDisjointExt = 0x8FBB;
    const int GpuSlots = 8, GpuSegs = 9, GpuSegClear = 0, GpuSegOpaque = 1, GpuSegDecal = 2, GpuSegStencil = 3,
        GpuSegDepth = 4, GpuSegBehind = 5, GpuSegFront = 6, GpuSegSuit = 7, GpuSegHud = 8, GpuDrawnMax = 1024;
    static readonly string[] GpuSegNames = { "clear", "opaque", "decal", "stencil", "depth", "behind", "front", "suit/warp", "hud" };
    readonly int[] _gpuQ = new int[GpuSlots * GpuSegs], _gpuQuads = new int[GpuSlots], _gpuItems = new int[GpuSlots],
        _gpuRoom = new int[GpuSlots], _gpuSegUsed = new int[GpuSlots];
    readonly bool[] _gpuSplitSlot = new bool[GpuSlots];
    readonly long[] _gpuFrame = new long[GpuSlots];
    readonly double[] _gpuSegMs = new double[GpuSegs];
    // per slot: the drawn items' mesh list ids, opaque then decal then translucent (-1 - type for a non-mesh item)
    readonly int[][] _gpuDrawn = NewGpuDrawn();
    readonly int[] _gpuDrawnO = new int[GpuSlots], _gpuDrawnD = new int[GpuSlots], _gpuDrawnT = new int[GpuSlots];
    readonly int[] _gpuTmp = new int[1];
    int _gpuHead, _gpuCount, _gpuLogSecond, _gpuLogLines, _perfQuads, _perfItems, _gpuSlot, _gpuAvgWindow = -1;
    int _gpuWholeN, _gpuSplitN;
    double _gpuWholeSum, _gpuSplitSum;
    bool _gpuTimer, _gpuBegun, _gpuSplit, _gpuSplitNext;

    static int[][] NewGpuDrawn()
    {
        var a = new int[GpuSlots][];
        for (int i = 0; i < a.Length; i++)
        {
            a[i] = new int[GpuDrawnMax];
        }
        return a;
    }

    void GpuTimerInit()
    {
        _gpuTimer = _gpuBegun = false;
        _gpuHead = _gpuCount = 0;
        string ext = GLES30.GlGetString(GLES30.GlExtensions) ?? "";
        if (!ext.Contains("GL_EXT_disjoint_timer_query"))
        {
            Log.Info("MPHGpu", "no GL_EXT_disjoint_timer_query: GPU frame times not logged");
            return;
        }
        GLES30.GlGenQueries(_gpuQ.Length, _gpuQ, 0);
        _gpuTimer = true;
    }

    void GpuTimerBegin()
    {
        _perfQuads = _perfItems = 0;
        _gpuBegun = _gpuTimer && _gpuCount < GpuSlots;
        if (_gpuBegun)
        {
            _gpuSlot = (_gpuHead + _gpuCount) % GpuSlots;
            _gpuSplit = GpuSplitTiming && _gpuSplitNext;
            _gpuSplitNext = !_gpuSplitNext;
            _gpuSplitSlot[_gpuSlot] = _gpuSplit;
            _gpuSegUsed[_gpuSlot] = 1 << GpuSegClear;
            _gpuDrawnO[_gpuSlot] = _gpuDrawnD[_gpuSlot] = _gpuDrawnT[_gpuSlot] = 0;
            GLES30.GlBeginQuery(TimeElapsedExt, _gpuQ[_gpuSlot * GpuSegs + GpuSegClear]);
        }
    }

    // a pass boundary in Render: on a split frame, ends the running segment's query and starts segment seg's
    void GpuMark(int seg)
    {
        if (!_gpuBegun || !_gpuSplit)
        {
            return;
        }
        GLES30.GlEndQuery(TimeElapsedExt);
        GLES30.GlBeginQuery(TimeElapsedExt, _gpuQ[_gpuSlot * GpuSegs + seg]);
        _gpuSegUsed[_gpuSlot] |= 1 << seg;
    }

    // what this frame draws (Render, once the lists are known): only list ids, the names are looked up when it's logged
    void GpuNoteDrawn(IReadOnlyList<RenderItem> opaque, IReadOnlyList<RenderItem> decal, IReadOnlyList<RenderItem> trans)
    {
        if (!_gpuBegun)
        {
            return;
        }
        int[] ids = _gpuDrawn[_gpuSlot];
        int n = NoteDrawn(ids, 0, opaque);
        _gpuDrawnO[_gpuSlot] = n;
        n = NoteDrawn(ids, n, decal);
        _gpuDrawnD[_gpuSlot] = n;
        _gpuDrawnT[_gpuSlot] = NoteDrawn(ids, n, trans);
    }

    static int NoteDrawn(int[] ids, int n, IReadOnlyList<RenderItem> items)
    {
        for (int i = 0; i < items.Count && n < ids.Length; i++)
        {
            RenderItem it = items[i];
            ids[n++] = it.Type == RenderItemType.Mesh ? it.ListId : -1 - (int)it.Type;
        }
        return n;
    }

    void GpuTimerEnd()
    {
        if (_gpuBegun)
        {
            GLES30.GlEndQuery(TimeElapsedExt);
            int slot = _gpuSlot;
            _gpuFrame[slot] = _frame;
            _gpuQuads[slot] = _perfQuads;
            _gpuItems[slot] = _perfItems;
            _gpuRoom[slot] = _host?.RoomId ?? -1;
            _gpuCount++;
            _gpuBegun = false;
        }
        while (_gpuCount > 0)
        {
            int slot = _gpuHead;
            int used = _gpuSegUsed[slot];
            bool ready = true;
            for (int s = 0; s < GpuSegs && ready; s++)
            {
                if ((used & (1 << s)) != 0)
                {
                    GLES30.GlGetQueryObjectuiv(_gpuQ[slot * GpuSegs + s], QueryResultAvailable, _gpuTmp, 0);
                    ready = _gpuTmp[0] != 0;
                }
            }
            if (!ready)
            {
                break;
            }
            double ms = 0;
            for (int s = 0; s < GpuSegs; s++)
            {
                _gpuSegMs[s] = 0;
                if ((used & (1 << s)) != 0)
                {
                    GLES30.GlGetQueryObjectuiv(_gpuQ[slot * GpuSegs + s], QueryResult, _gpuTmp, 0);
                    _gpuSegMs[s] = (uint)_gpuTmp[0] / 1e6;
                    if (_gpuSegMs[s] > 1000)
                    {
                        _gpuSegMs[s] = 0; // a wrapped (negative) reading of a near-empty segment
                    }
                    ms += _gpuSegMs[s];
                }
            }
            _gpuHead = (_gpuHead + 1) % GpuSlots;
            _gpuCount--;
            GLES30.GlGetIntegerv(GpuDisjointExt, _gpuTmp, 0);
            if (_gpuTmp[0] != 0)
            {
                continue; // disjoint: a GPU clock change made the time meaningless
            }
            bool split = _gpuSplitSlot[slot];
            GpuAverage(split, ms);
            if (!split)
            {
                _paceGpuSum += ms;
                _paceGpuN++;
                _paceGpuMax = Math.Max(_paceGpuMax, ms);
            }
            if (ms <= 12)
            {
                continue;
            }
            int second = (int)_clock.Elapsed.TotalSeconds;
            if (second != _gpuLogSecond)
            {
                _gpuLogSecond = second;
                _gpuLogLines = 0;
            }
            if (++_gpuLogLines <= 20)
            {
                string how = split ? $" ({GpuSplitText()})" : " (whole)";
                Log.Info("MPHGpu", $"frame {_gpuFrame[slot]} room {_gpuRoom[slot]}: GPU {ms:0.0} ms{how}, {_gpuQuads[slot]} particle quads, {_gpuItems[slot]} items");
                if (ms > 20)
                {
                    Log.Info("MPHGpu", $"  frame {_gpuFrame[slot]} drew: {GpuDrawnText(slot)}");
                }
            }
        }
    }

    // every 10 s: the average GPU frame time of whole-timed vs split-timed frames (what splitting costs by itself)
    void GpuAverage(bool split, double ms)
    {
        if (split)
        {
            _gpuSplitSum += ms;
            _gpuSplitN++;
        }
        else
        {
            _gpuWholeSum += ms;
            _gpuWholeN++;
        }
        int window = (int)(_clock.Elapsed.TotalSeconds / 10);
        if (window == _gpuAvgWindow)
        {
            return;
        }
        if (_gpuAvgWindow >= 0 && _gpuWholeN > 0 && _gpuSplitN > 0)
        {
            Log.Info("MPHGpu", $"10 s average room {_host?.RoomId ?? -1}: GPU {_gpuWholeSum / _gpuWholeN:0.00} ms whole-timed "
                + $"({_gpuWholeN} frames), {_gpuSplitSum / _gpuSplitN:0.00} ms split-timed ({_gpuSplitN})");
        }
        _gpuAvgWindow = window;
        _gpuWholeSum = _gpuSplitSum = 0;
        _gpuWholeN = _gpuSplitN = 0;
    }

    string GpuSplitText()
    {
        var sb = new System.Text.StringBuilder();
        for (int s = 0; s < GpuSegs; s++)
        {
            if (_gpuSegMs[s] >= 0.05)
            {
                sb.Append(sb.Length > 0 ? ", " : "").Append(GpuSegNames[s]).Append(' ').Append(_gpuSegMs[s].ToString("0.0"));
            }
        }
        return sb.ToString();
    }

    // a slow frame's context: the player's form, the boss (state, distance from the camera), the models drawn per pass
    string GpuDrawnText(int slot)
    {
        var sb = new System.Text.StringBuilder();
        Scene? scene = _host?.Scene;
        if (_host != null && scene != null)
        {
            PlayerEntity p = _host.Player;
            sb.Append(p.IsAltForm ? "ball" : "biped");
            if (p.Flags2.TestFlag(PlayerFlags2.DrawnThirdPerson))
            {
                sb.Append(" (third person)");
            }
            foreach (EntityBase e in scene.Entities)
            {
                if (e is MphRead.Entities.Enemies.Enemy41Entity || e is MphRead.Entities.Enemies.Enemy19Entity)
                {
                    sb.Append($", {(e is MphRead.Entities.Enemies.Enemy41Entity ? "Slench" : "Cretaphid")} {BossState(e)} "
                        + $"{Vector3.Distance(e.Position, scene.CameraPosition):0.0} from the camera");
                    break;
                }
            }
        }
        int[] ids = _gpuDrawn[slot];
        GpuDrawnPass(sb, "opaque", ids, 0, _gpuDrawnO[slot], scene);
        GpuDrawnPass(sb, "decal", ids, _gpuDrawnO[slot], _gpuDrawnD[slot], scene);
        GpuDrawnPass(sb, "translucent", ids, _gpuDrawnD[slot], _gpuDrawnT[slot], scene);
        return sb.ToString();
    }

    static void GpuDrawnPass(System.Text.StringBuilder sb, string pass, int[] ids, int from, int to, Scene? scene)
    {
        if (to <= from)
        {
            return;
        }
        var counts = new Dictionary<string, int>();
        for (int i = from; i < to; i++)
        {
            int id = ids[i];
            string name = id < 0 ? ((RenderItemType)(-1 - id)).ToString()
                : scene != null && id <= scene.HostMeshes.Count ? scene.HostMeshes[id - 1].Model.Name : "mesh?";
            counts[name] = counts.GetValueOrDefault(name) + 1;
        }
        var list = new List<KeyValuePair<string, int>>(counts);
        list.Sort((a, b) => b.Value.CompareTo(a.Value));
        sb.Append("; ").Append(pass).Append(' ').Append(to - from).Append(':');
        for (int i = 0; i < list.Count && i < 10; i++)
        {
            sb.Append(' ').Append(list[i].Key).Append(" x").Append(list[i].Value);
        }
    }

    static string BossState(EntityBase boss)
    {
        var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public;
        for (Type? t = boss.GetType(); t != null; t = t.BaseType)
        {
            System.Reflection.FieldInfo? f = t.GetField("_state1", flags);
            if (f != null)
            {
                object? v = f.GetValue(boss);
                return boss is MphRead.Entities.Enemies.Enemy41Entity && v is byte b
                    ? ((MphRead.Entities.Enemies.SlenchState)b).ToString() : $"state {v}";
            }
        }
        return "";
    }

    GpuMesh? GetMesh(int listId, Scene scene)
    {
        if (_meshes.TryGetValue(listId, out GpuMesh? gpu))
        {
            gpu.LastUsed = _frame;
            return gpu;
        }
        if (listId < 1 || listId > scene.HostMeshes.Count)
        {
            return null;
        }
        long upload = Stopwatch.GetTimestamp();
        (Model model, Mesh m, bool isRoom) = scene.HostMeshes[listId - 1];
        float[] verts = DsDisplayList.Decode(model, m, isRoom);
        gpu = new GpuMesh { Count = verts.Length / DsDisplayList.Stride, LastUsed = _frame };
        if (gpu.Count > 0)
        {
            var ids = new int[1];
            GLES30.GlGenVertexArrays(1, ids, 0); gpu.Vao = ids[0];
            GLES30.GlGenBuffers(1, ids, 0); gpu.Vbo = ids[0];
            GLES30.GlBindVertexArray(gpu.Vao);
            GLES30.GlBindBuffer(GLES30.GlArrayBuffer, gpu.Vbo);
            var bb = Java.Nio.ByteBuffer.AllocateDirect(verts.Length * 4);
            bb.Order(Java.Nio.ByteOrder.NativeOrder());
            var fb = bb.AsFloatBuffer()!;
            fb.Put(verts);
            fb.Position(0);
            GLES30.GlBufferData(GLES30.GlArrayBuffer, verts.Length * 4, fb, GLES30.GlStaticDraw);
            SetAttribs();
        }
        _meshes[listId] = gpu;
        _perfMeshN++;
        _perfMeshMs += Stopwatch.GetElapsedTime(upload).TotalMilliseconds;
        return gpu;
    }

    // Owner queue #37b (2026-10-04): the room's first draw after a movie uploaded everything in view in one frame (CA
    // landing skip on the Odin: 114 meshes + 31 textures, 27 ms; 79 + 31 more when the landing camseq cut in). While a
    // movie is up the GL thread only draws the movie (~1 ms a frame), so the scene's registered meshes (the room's parts
    // first) and textures go up then, MovieWarmMs a frame; whatever isn't reached still uploads on its first draw.
    const double MovieWarmMs = 4;
    Scene? _warmScene;
    int _warmPhase, _warmIndex, _warmMeshesDone, _warmTexCount = -1, _warmMeshN, _warmTexN, _warmFrames;
    double _warmMs;

    void WarmSceneGl(Scene scene)
    {
        if (scene != _warmScene)
        {
            _warmScene = scene;
            _warmPhase = _warmIndex = _warmMeshesDone = 0;
            _warmTexCount = -1;
        }
        IReadOnlyList<(Model Model, Mesh Mesh, bool IsRoom)> meshes = scene.HostMeshes;
        if (_warmPhase == 2 && _warmMeshesDone == meshes.Count && _warmTexCount == scene.HostTextures.Count)
        {
            return; // all up, nothing registered since
        }
        long start = Stopwatch.GetTimestamp();
        bool Spent() => Stopwatch.GetElapsedTime(start).TotalMilliseconds >= MovieWarmMs;
        try
        {
            if (_warmPhase == 2 && _warmMeshesDone != meshes.Count)
            {
                // meshes registered since (a room load): scan again, the ones already up are skipped
                _warmPhase = _warmIndex = 0;
            }
            // phase 0: the room's parts, 1: every other mesh (list ids are 1-based), 2: textures
            while (_warmPhase < 2 && !Spent())
            {
                if (_warmIndex >= meshes.Count)
                {
                    _warmPhase++;
                    _warmIndex = 0;
                    _warmMeshesDone = meshes.Count;
                    continue;
                }
                int id = ++_warmIndex;
                if (meshes[id - 1].IsRoom == (_warmPhase == 0) && !_meshes.ContainsKey(id))
                {
                    GetMesh(id, scene);
                    _warmMeshN++;
                }
            }
            if (_warmPhase == 2 && !Spent())
            {
                bool done = true;
                foreach (KeyValuePair<int, Scene.HostTexture> kv in scene.HostTextures)
                {
                    if (kv.Value.Width != 0 && (!_textures.TryGetValue(kv.Key, out GpuTexture? tex) || tex.Version != kv.Value.Version))
                    {
                        if (Spent())
                        {
                            done = false;
                            break;
                        }
                        BindTexture(kv.Key, scene);
                        _warmTexN++;
                    }
                }
                _warmTexCount = done ? scene.HostTextures.Count : -1;
            }
        }
        catch (InvalidOperationException)
        {
            // the textures changed under the scan (a room load's worker): carry on next frame
        }
        GLES30.GlBindVertexArray(0);
        GLES30.GlBindBuffer(GLES30.GlArrayBuffer, 0);
        GlNative.BindTexture(GLES30.GlTexture2d, 0);
        _warmMs += Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        _warmFrames++;
    }

    // the first frame without a movie: how far the warm-up got (MPHPerf)
    void EndWarmSceneGl()
    {
        if (_warmFrames == 0)
        {
            return;
        }
        Scene? scene = _warmScene;
        Log.Info("MPHPerf", $"movie warm-up: {_warmMeshN} meshes + {_warmTexN} textures uploaded in {_warmMs:0} ms over {_warmFrames} "
            + $"frames; {(_warmPhase == 2 && _warmTexCount >= 0 ? "all" : "NOT all")} of the scene's {scene?.HostMeshes.Count} meshes / "
            + $"{scene?.HostTextures.Count} textures up");
        _warmMeshN = _warmTexN = _warmFrames = 0;
        _warmMs = 0;
    }

    bool BindTexture(int bindingId, Scene scene, int unit = 0)
    {
        if (!scene.HostTextures.TryGetValue(bindingId, out Scene.HostTexture? src) || src.Width == 0)
        {
            return false;
        }
        if (!_textures.TryGetValue(bindingId, out GpuTexture? tex))
        {
            var ids = new int[1];
            GLES30.GlGenTextures(1, ids, 0);
            _textures[bindingId] = tex = new GpuTexture { Tex = ids[0], Version = -1 };
        }
        GlNative.ActiveTexture(unit == 0 ? GLES30.GlTexture0 : GLES30.GlTexture1);
        GlNative.BindTexture(GLES30.GlTexture2d, tex.Tex);
        if (tex.Version != src.Version)
        {
            long upload = Stopwatch.GetTimestamp();
            UploadPixels(src.Pixels, src.Width, src.Height);
            tex.Version = src.Version;
            _perfTexN++;
            _perfTexMs += Stopwatch.GetElapsedTime(upload).TotalMilliseconds;
        }
        GlNative.ActiveTexture(GLES30.GlTexture0);
        tex.LastUsed = _frame;
        return true;
    }

    int _evictRoom = -1;
    long _roomEnteredFrame;

    // drop GPU copies of rooms left behind: anything not drawn since the current room was entered, after 2400 frames.
    // Anything the current room has drawn stays: the old rule (not drawn for 2400 frames, only 20 s at 120 Hz) also
    // dropped out-of-view room parts, enemies between waves, rarely used effects..., and each came back with a
    // display-list decode + upload on the GL thread mid-fight -- a several-frame hitch (2026-09-30)
    void Evict()
    {
        long cutoff = Math.Min(_frame - 2400, _roomEnteredFrame);
        var dead = new List<int>();
        foreach (var kv in _meshes)
        {
            if (kv.Value.LastUsed < cutoff)
            {
                dead.Add(kv.Key);
            }
        }
        foreach (int id in dead)
        {
            GpuMesh m = _meshes[id];
            if (m.Vbo != 0)
            {
                GLES30.GlDeleteBuffers(1, new[] { m.Vbo }, 0);
                GLES30.GlDeleteVertexArrays(1, new[] { m.Vao }, 0);
            }
            _meshes.Remove(id);
        }
        dead.Clear();
        foreach (var kv in _textures)
        {
            if (kv.Value.LastUsed < cutoff)
            {
                dead.Add(kv.Key);
            }
        }
        foreach (int id in dead)
        {
            GLES30.GlDeleteTextures(1, new[] { _textures[id].Tex }, 0);
            _textures.Remove(id);
        }
    }

    void SetMatrix(int location, Matrix4 m)
    {
        // OpenTK's memory layout, uploaded untransposed -- exactly as MphRead does with GL.UniformMatrix4
        _mtx[0] = m.M11; _mtx[1] = m.M12; _mtx[2] = m.M13; _mtx[3] = m.M14;
        _mtx[4] = m.M21; _mtx[5] = m.M22; _mtx[6] = m.M23; _mtx[7] = m.M24;
        _mtx[8] = m.M31; _mtx[9] = m.M32; _mtx[10] = m.M33; _mtx[11] = m.M34;
        _mtx[12] = m.M41; _mtx[13] = m.M42; _mtx[14] = m.M43; _mtx[15] = m.M44;
        GlNative.UniformMatrix4(location, 1, _mtx);
    }

    public void Shutdown()
    {
        StopMovie();
        ResetViewHooks(); // CampaignView.cs
        try
        {
            _host?.Dispose();
        }
        catch (Exception ex)
        {
            Log.Warn("MPHCampaign", "shutdown: " + ex.Message);
        }
        _host = null;
    }

    static int Link(string vs, string fs)
    {
        int p = GLES30.GlCreateProgram();
        GLES30.GlAttachShader(p, Compile(GLES30.GlVertexShader, vs));
        GLES30.GlAttachShader(p, Compile(GLES30.GlFragmentShader, fs));
        GLES30.GlLinkProgram(p);
        var st = new int[1];
        GLES30.GlGetProgramiv(p, GLES30.GlLinkStatus, st, 0);
        if (st[0] == 0)
        {
            Log.Error("MPHCampaign", "link: " + GLES30.GlGetProgramInfoLog(p));
        }
        return p;
    }

    static int Compile(int type, string src)
    {
        int s = GLES30.GlCreateShader(type);
        GLES30.GlShaderSource(s, src);
        GLES30.GlCompileShader(s);
        var st = new int[1];
        GLES30.GlGetShaderiv(s, GLES30.GlCompileStatus, st, 0);
        if (st[0] == 0)
        {
            Log.Error("MPHCampaign", "compile: " + GLES30.GlGetShaderInfoLog(s));
        }
        return s;
    }

    const string HudVertexShader = @"#version 300 es
layout(location = 0) in vec2 a_pos;
layout(location = 1) in vec2 a_uv;
out vec2 v_uv;
void main()
{
    v_uv = a_uv;
    gl_Position = vec4(a_pos, 0.0, 1.0);
}
";

    const string HudFragmentShader = @"#version 300 es
precision highp float;
uniform sampler2D tex;
uniform sampler2D mask;
uniform bool use_mask;
uniform float alpha;
uniform float view_width;
uniform float view_height;
uniform vec4 fade_color;
in vec2 v_uv;
out vec4 frag;
void main()
{
    if (fade_color.a > 0.0) {
        frag = fade_color;
        return;
    }
    vec4 c = texture(tex, v_uv);
    if (use_mask) {
        float maskY = gl_FragCoord.y + (view_width - view_height) / 2.0;
        vec2 maskTexcoord = vec2(gl_FragCoord.x / view_width, 1.0 - maskY / view_width);
        if (texture(mask, maskTexcoord).a > 0.0) {
            c.a = 0.0;
        }
    }
    c.a *= alpha;
    frag = c;
}
";
}
