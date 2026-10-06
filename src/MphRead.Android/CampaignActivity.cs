using Android.App;
using Android.Content;
using Android.Graphics;
using Android.Opengl;
using Android.OS;
using Android.Views;
using Android.Widget;
using System;
using MphRecomp.Frontend;
using MphRecomp.Multiplayer.Net;

namespace MphRecomp.App;

// Campaign mode on Android: the shared CampaignRenderer (MphRecomp.App: MphRead's own adventure simulation and its draw
// list, the ship, map, movies, HUD, pause menu, and multiplayer matches) in a GLSurfaceView, with this device's own
// parts: the controller and keys, the Back button, the gyroscope (CampaignGyro.cs), the touch controls
// (MatchTouchOverlay.cs), the status line and the centred text panel. Intent extras are the launch args
// (CampaignRenderer.Create lists them).
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
        string ext = GetExternalFilesDir(null)?.AbsolutePath ?? FilesDir!.AbsolutePath;
        string settingsPath = System.IO.Path.Combine(ext, "recomp_settings.json");
        var args = new LaunchArgs(key => Intent?.GetStringExtra(key));
        _renderer = CampaignRenderer.Create(FilesDir!.AbsolutePath, ext, args);
        _view = new GLSurfaceView(this);
        _view.SetEGLContextClientVersion(3);
        // stencil: MphRead's translucency passes sort overlapping translucent surfaces with it
        _view.SetEGLConfigChooser(8, 8, 8, 8, 24, 8);
        _view.PreserveEGLContextOnPause = true;
        _view.SetRenderer(new GlScreenRenderer(_renderer));
        var root = new FrameLayout(this);
        root.AddView(_view, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent));
        _status = new TextView(this) { TextSize = 12f, Text = "loading " + (args.Get("room") ?? "UNIT2_LAND") + "..." };
        _status.SetTextColor(Android.Graphics.Color.White);
        _status.SetShadowLayer(3f, 1f, 1f, Android.Graphics.Color.Black);
        _status.SetPadding(24, 16, 24, 16);
        root.AddView(_status, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent)
        { Gravity = GravityFlags.Top | GravityFlags.Left });
        _renderer.StatusSink = s => RunOnUiThread(() => _status.Text = s);
        PauseOverlay pause = _renderer.PauseMenu!;
        pause.DevTools += () => RunOnUiThread(() =>
        {
            StartActivity(new Intent(this, typeof(MainActivity)).PutExtra("dev", "1"));
            Finish();
        });
        // RECOMP SETTINGS > CREDITS / CRASH LOG from the pause menu: the front end's screens, over the paused game
        pause.Credits += () => RunOnUiThread(() => StartActivity(new Intent(this, typeof(CreditsActivity))));
        pause.ShareCrashLog += () => RunOnUiThread(() => GameActivity.ShareCrashLog(this));
        // gyro aim on top of the right stick (CampaignGyro.cs; RECOMP SETTINGS gyro rows, --es gyro on|zoom)
        CampaignGyro.Attach(this, _renderer, settingsPath, Intent);
        // the centred text panel (game over, match results) and leaving the game
        AttachPanel(root);
        // a LAN match: however the match screen closes (Select, Back, quit), the other device hears BYE at once
        if (_renderer.NetSession is LanSession session)
        {
            Application!.RegisterActivityLifecycleCallbacks(new CloseSessionOnDestroy(this, session));
        }
        // on-screen touch controls, on top of everything (MatchTouchOverlay.cs): RECOMP SETTINGS "touch controls"
        // auto|on|off (a match's --es touch overrides it), re-read whenever the pause menu resumes
        MatchTouchOverlay touch = MatchTouchOverlay.Attach(this, root, _renderer, Intent, settingsPath);
        pause.Resumed += () => RunOnUiThread(touch.ReloadSettings);
        SetContentView(root);
        HideSystemUi();
    }

    void AttachPanel(FrameLayout root)
    {
        var panel = new TextView(this) { TextSize = 17f, Visibility = ViewStates.Gone };
        panel.SetTypeface(Typeface.Monospace, TypefaceStyle.Normal);
        panel.SetTextColor(Color.Rgb(200, 255, 220));
        panel.SetBackgroundColor(Color.Argb(215, 4, 18, 24));
        panel.SetPadding(48, 36, 48, 36);
        root.AddView(panel, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent)
        { Gravity = GravityFlags.Center });
        _renderer.ShipSink = text => RunOnUiThread(() =>
        {
            panel.Text = text;
            panel.Visibility = text.Length == 0 ? ViewStates.Gone : ViewStates.Visible;
        });
        _renderer.ExitSink = () => RunOnUiThread(() =>
        {
            StartActivity(new Intent(this, typeof(MainActivity)).AddFlags(ActivityFlags.ClearTop | ActivityFlags.SingleTop));
            Finish();
        });
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
    float _menuTrigL, _menuTrigR; // the triggers while the pause menu is open (the control customizer's capture)

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
                float l2 = e.GetAxisValue(Axis.Ltrigger), r2 = e.GetAxisValue(Axis.Rtrigger);
                if (_renderer.PauseMenu?.CaptureDevice != null)
                {
                    // the control customizer's capture: a trigger past half way, or a D-pad push (HAT on the Odin)
                    string? name = l2 >= 0.5f && _menuTrigL < 0.5f ? "L2" : r2 >= 0.5f && _menuTrigR < 0.5f ? "R2"
                        : hx != _menuHatX && hx != 0 ? (hx > 0 ? "DpadRight" : "DpadLeft")
                        : hy != _menuHatY && hy != 0 ? (hy > 0 ? "DpadDown" : "DpadUp") : null;
                    if (name != null) _renderer.PauseMenuInput(p => p.CaptureInput(name));
                    _menuHatX = hx; _menuHatY = hy; _menuStickX = sx; _menuStickY = sy; _menuTrigL = l2; _menuTrigR = r2;
                    return true;
                }
                _menuTrigL = l2; _menuTrigR = r2;
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

    // the controller buttons the game reads (Start, Select and Back are handled on their own)
    internal static PadButton? PadFor(Keycode k) => k switch
    {
        Keycode.ButtonA => PadButton.A,
        Keycode.ButtonB => PadButton.B,
        Keycode.ButtonX => PadButton.X,
        Keycode.ButtonY => PadButton.Y,
        Keycode.ButtonL1 => PadButton.L1,
        Keycode.ButtonR1 => PadButton.R1,
        Keycode.ButtonL2 => PadButton.L2,
        Keycode.ButtonR2 => PadButton.R2,
        Keycode.ButtonThumbl => PadButton.ThumbL,
        Keycode.ButtonThumbr => PadButton.ThumbR,
        Keycode.DpadUp => PadButton.DpadUp,
        Keycode.DpadDown => PadButton.DpadDown,
        Keycode.DpadLeft => PadButton.DpadLeft,
        Keycode.DpadRight => PadButton.DpadRight,
        _ => null,
    };

    public override bool OnKeyDown(Keycode keyCode, KeyEvent? e)
    {
        // the control customizer (from the ship's OPTIONS or RECOMP SETTINGS) waits for a controller button: the first
        // press is the answer, Back / Start / Select keep the buttons as they are
        if (_renderer.PauseMenu?.CaptureDevice != null)
        {
            if (e?.RepeatCount == 0)
            {
                if (PadFor(keyCode) is PadButton b)
                {
                    string name = b.ToString();
                    _renderer.PauseMenuInput(p => p.CaptureInput(name));
                }
                else if (keyCode is Keycode.Back or Keycode.ButtonStart or Keycode.ButtonSelect)
                {
                    _renderer.PauseMenuInput(p => p.CancelCapture());
                }
            }
            return true;
        }
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
        if ((keyCode == Keycode.ButtonThumbl && _renderer.Pad[PadButton.ThumbR])
            || (keyCode == Keycode.ButtonThumbr && _renderer.Pad[PadButton.ThumbL]))
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
        if (PadFor(keyCode) is PadButton down)
        {
            _renderer.Pad.Set(down, true);
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
        if (PadFor(keyCode) is PadButton up)
        {
            _renderer.Pad.Set(up, false);
            return true;
        }
        if (keyCode == Keycode.Back) return true; // handled on the press (OnKeyDown); the release must not finish us
        return base.OnKeyUp(keyCode, e);
    }
}
