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
        pause.ShareLog += () => RunOnUiThread(() => GameActivity.ShareLog(this));
        pause.SaveLog += () => RunOnUiThread(() => GameActivity.SaveLog(this));
        // gyro aim on top of the right stick (CampaignGyro.cs; RECOMP SETTINGS gyro rows, --es gyro on|zoom)
        CampaignGyro.Attach(this, _renderer, settingsPath, Intent);
        // the centred text panel (game over, match results) and leaving the game
        AttachPanel(root);
        // a LAN or online match: however the match screen closes (Select, Back, quit), the other devices hear BYE at once
        if (_renderer.NetSession is IDisposable session)
        {
            Application!.RegisterActivityLifecycleCallbacks(new CloseSessionOnDestroy(this, () => _renderer.NetSession ?? session));
            MatchWifiLock.Attach(this); // (Wi-Fi power save off while the match is in front: MatchWifiLock.cs)
        }
        // on-screen touch controls, on top of everything (MatchTouchOverlay.cs): RECOMP SETTINGS "touch controls"
        // auto|on|off (a match's --es touch overrides it), re-read whenever the pause menu resumes
        MatchTouchOverlay touch = MatchTouchOverlay.Attach(this, root, _renderer, Intent, settingsPath);
        pause.Resumed += () => RunOnUiThread(touch.ReloadSettings);
        SetContentView(root);
        HideSystemUi();
    }

    TextView? _panel; // the centred text panel (the ship's save prompt, game over, a match's results)

    // the panel's line (of its text, by newlines) under window pixel (x, y); null off its text
    int? PanelLine(float x, float y) => TextLine(_panel, x, y);

    // the line (of its text, by newlines) of text view p under window pixel (x, y); null off its text
    static int? TextLine(TextView? p, float x, float y)
    {
        if (p == null || p.Visibility != ViewStates.Visible || p.Layout is not Android.Text.Layout layout)
        {
            return null;
        }
        var at = new int[2];
        p.GetLocationInWindow(at);
        float px = x - at[0] - p.TotalPaddingLeft, py = y - at[1] - p.TotalPaddingTop + p.ScrollY;
        if (px < 0 || py < 0 || px >= layout.Width || py >= layout.Height)
        {
            return null;
        }
        string text = p.Text ?? "";
        int start = Math.Min(layout.GetLineStart(layout.GetLineForVertical((int)py)), text.Length);
        return text.AsSpan(0, start).Count('\n');
    }

    void AttachPanel(FrameLayout root)
    {
        var panel = _panel = new TextView(this) { TextSize = 17f, Visibility = ViewStates.Gone };
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
            // a First Hunt game started from its own menus (FirstHuntActivity) goes back to them
            if (Intent?.GetStringExtra("fhmenu") == null)
                StartActivity(new Intent(this, typeof(MainActivity)).AddFlags(ActivityFlags.ClearTop | ActivityFlags.SingleTop));
            Finish();
        });
        _renderer.ChatBoxRequested = () => RunOnUiThread(ShowChatBox); // an online match's typed chat (MatchChat.cs)
        _renderer.MapTextRequested = (hint, done) => RunOnUiThread(() => ShowNameBox(hint, done)); // PRIME map split reports
    }

    // the PRIME map's split report (PrimeMap.Report.cs): a name typed with the soft keyboard; done gets the text, or null
    // when cancelled (on this thread: the map picks it up on its next tick)
    void ShowNameBox(string hint, Action<string?> done)
    {
        var edit = new EditText(this) { Hint = hint };
        edit.SetSingleLine(true);
        edit.ImeOptions = Android.Views.InputMethods.ImeAction.Done;
        edit.SetFilters(new Android.Text.IInputFilter[] { new Android.Text.InputFilterLengthFilter(32) });
        string? typed = null;
        AlertDialog dialog = new AlertDialog.Builder(this)
            .SetView(edit)!
            .SetPositiveButton("OK", (_, _) => typed = edit.Text)!
            .SetNegativeButton("CANCEL", (_, _) => { })!
            .Create()!;
        edit.EditorAction += (_, e) =>
        {
            e.Handled = e.ActionId == Android.Views.InputMethods.ImeAction.Done
                || e.Event?.KeyCode is Keycode.Enter && e.Event.Action == KeyEventActions.Down;
            if (!e.Handled) return;
            typed = edit.Text;
            dialog.Dismiss();
        };
        dialog.DismissEvent += (_, _) =>
        {
            done(typed);
            HideSystemUi();
        };
        dialog.Window!.SetGravity(GravityFlags.Top);
        dialog.Window.SetSoftInputMode(SoftInput.StateAlwaysVisible);
        dialog.Show();
        edit.RequestFocus();
    }

    // an online match's typed chat (Social O7, MatchChat.cs: a short Select / CHAT tap): one line with the soft keyboard, at the
    // top so the match stays in view; the match runs on underneath (the player stands still while it's open)
    void ShowChatBox()
    {
        var edit = new EditText(this) { Hint = "say something" };
        edit.SetSingleLine(true);
        edit.ImeOptions = Android.Views.InputMethods.ImeAction.Send;
        edit.SetFilters(new Android.Text.IInputFilter[] { new Android.Text.InputFilterLengthFilter(MphRecomp.Online.ChatReq.MaxChat) });
        string? sent = null;
        AlertDialog dialog = new AlertDialog.Builder(this)
            .SetView(edit)!
            .SetPositiveButton("SEND", (_, _) => sent = edit.Text)!
            .SetNegativeButton("CANCEL", (_, _) => { })!
            .Create()!;
        edit.EditorAction += (_, e) =>
        {
            e.Handled = e.ActionId == Android.Views.InputMethods.ImeAction.Send
                || e.Event?.KeyCode is Keycode.Enter && e.Event.Action == KeyEventActions.Down;
            if (!e.Handled) return;
            sent = edit.Text;
            dialog.Dismiss();
        };
        dialog.DismissEvent += (_, _) =>
        {
            _renderer.SendTypedChat(sent);
            HideSystemUi();
        };
        dialog.Window!.SetGravity(GravityFlags.Top);
        dialog.Window.SetSoftInputMode(SoftInput.StateAlwaysVisible);
        dialog.Show();
        edit.RequestFocus();
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
    readonly MenuTriggerKey _menuL2 = new(MenuKeys.L2), _menuR2 = new(MenuKeys.R2); // L2 / R2 pulled, as menu keys

    // L2 / R2 as menu keys (CONTROLS' sub-tabs, owner 2026-10-24): one press per pull, whether the pad sends a trigger as
    // a button, an axis or both (the Odin's "both" trigger mode: the button and the axis each come and go on their own,
    // so the trigger is down while either is, owner 2026-10-24: "pressing the trigger makes several tab/page changes");
    // the axis lets go a little lower than it presses, so one resting near half way doesn't repeat
    internal sealed class MenuTriggerKey
    {
        readonly MenuKeys _key;
        bool _button, _axis, _down;

        public MenuTriggerKey(MenuKeys key) => _key = key;

        public void Button(bool down, Action<MenuKeys> press)
        {
            _button = down;
            Update(press);
        }

        public void Axis(float v, Action<MenuKeys> press)
        {
            _axis = v >= (_axis ? 0.3f : 0.5f);
            Update(press);
        }

        void Update(Action<MenuKeys> press)
        {
            bool down = _button || _axis;
            if (down && !_down) press(_key);
            _down = down;
        }
    }

    void MenuPress(MenuKeys k) => _renderer.PauseMenuInput(p => p.Press(k));
    float _menuStickRy; // the right stick's up / down there: scrolls settings and CONTROLS (PauseOverlay.ScrollStick)

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
                    CaptureAxes(name => _renderer.PauseMenuInput(p => p.CaptureInput(name)), l2, r2, hx, hy,
                        _menuTrigL, _menuTrigR, _menuHatX, _menuHatY);
                    _menuHatX = hx; _menuHatY = hy; _menuStickX = sx; _menuStickY = sy; _menuTrigL = l2; _menuTrigR = r2;
                    return true;
                }
                _menuL2.Axis(l2, MenuPress);
                _menuR2.Axis(r2, MenuPress);
                _menuTrigL = l2; _menuTrigR = r2;
                float ry = e.GetAxisValue(Axis.Rz);
                if (Math.Abs(ry - _menuStickRy) > 0.05f || (Math.Abs(ry) < 0.3f) != (Math.Abs(_menuStickRy) < 0.3f))
                {
                    _menuStickRy = ry;
                    _renderer.PauseMenuInput(p => p.ScrollStick(ry));
                }
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

    // the customizer's capture from the motion axes: each trigger crossing half way and each D-pad (HAT) push is a press,
    // and going back is its release ("-" + name), so held buttons make a combo that commits once all are let go
    internal static void CaptureAxes(Action<string> send, float l2, float r2, int hx, int hy, float prevL2, float prevR2,
        int prevHx, int prevHy)
    {
        if ((l2 >= 0.5f) != (prevL2 >= 0.5f)) send(l2 >= 0.5f ? "L2" : "-L2");
        if ((r2 >= 0.5f) != (prevR2 >= 0.5f)) send(r2 >= 0.5f ? "R2" : "-R2");
        if (hx != prevHx)
        {
            if (prevHx != 0) send(prevHx > 0 ? "-DpadRight" : "-DpadLeft");
            if (hx != 0) send(hx > 0 ? "DpadRight" : "DpadLeft");
        }
        if (hy != prevHy)
        {
            if (prevHy != 0) send(prevHy > 0 ? "-DpadDown" : "-DpadUp");
            if (hy != 0) send(hy > 0 ? "DpadDown" : "DpadUp");
        }
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
        // the control customizer (from the ship's OPTIONS or RECOMP SETTINGS) waits for a controller button: every
        // press and release goes to it, held buttons make a combo (Select and R3 too since 2026-10-11), Back / Start keep the buttons as
        // they are
        if (_renderer.PauseMenu?.CaptureDevice != null)
        {
            if (e?.RepeatCount == 0)
            {
                if ((keyCode == Keycode.ButtonSelect ? PadButton.Select : PadFor(keyCode)) is PadButton b)
                {
                    string name = b.ToString();
                    _renderer.PauseMenuInput(p => p.CaptureInput(name));
                }
                else if (keyCode is Keycode.Back or Keycode.ButtonStart)
                {
                    _renderer.PauseMenuInput(p => p.CancelCapture());
                }
            }
            return true;
        }
        // Select ("View"), held: the customizer's "map / scoreboard" unless moved -- the pause map in the campaign
        // (CampaignMap.MapFrame), a match's live scoreboard (MatchChat.cs MatchScoreboardHeld)
        if (keyCode == Keycode.ButtonSelect)
        {
            _renderer.Pad.Set(PadButton.Select, true);
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
        if (keyCode is Keycode.ButtonL2 or Keycode.ButtonR2)
        {
            (keyCode == Keycode.ButtonR2 ? _menuR2 : _menuL2).Button(true, MenuPress);
            return;
        }
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
        // the gun-adjust pause's bot switches (BotStuckToggle.cs): a tap on a switch's line of the status text flips it
        if (e != null && e.ActionMasked == MotionEventActions.Down && _renderer.GunPauseOpen
            && TextLine(_status, e.GetX(), e.GetY()) is int statusLine && _renderer.BotSwitchTap(_status!.Text ?? "", statusLine))
        {
            return true;
        }
        if (e != null && _renderer.PauseMenuOpen && e.ActionMasked is MotionEventActions.Down or MotionEventActions.Move
            or MotionEventActions.Up or MotionEventActions.Cancel)
        {
            // down, move, up, cancel: a drag scrolls settings and CONTROLS, a tap is a tap (FrontendSession.TouchCanvas)
            float x = e.GetX(), y = e.GetY();
            int kind = e.ActionMasked switch { MotionEventActions.Down => 0, MotionEventActions.Move => 1, MotionEventActions.Up => 2, _ => 3 };
            _renderer.PauseMenuInput(p => p.Touch(x, y, kind));
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
        // a replay (MatchReplay.cs): its bar's buttons and scrub drags (a tap elsewhere pauses / plays), or a tap leaves
        // the final scoreboard. Every finger goes by its pointer id: two fingers fly the free cam (move + look)
        if (e != null && _renderer.ReplayPlaying && e.ActionMasked is MotionEventActions.Down or MotionEventActions.Move or MotionEventActions.Up
            or MotionEventActions.PointerDown or MotionEventActions.PointerUp)
        {
            if (_renderer.MatchOver)
            {
                if (e.ActionMasked == MotionEventActions.Down) _renderer.RequestMatchContinue();
            }
            else if (e.ActionMasked == MotionEventActions.Move)
            {
                for (int i = 0; i < e.PointerCount; i++) _renderer.ReplayTouch(e.GetX(i), e.GetY(i), 1, e.GetPointerId(i));
            }
            else
            {
                int i = e.ActionIndex;
                bool down = e.ActionMasked is MotionEventActions.Down or MotionEventActions.PointerDown;
                _renderer.ReplayTouch(e.GetX(i), e.GetY(i), down ? 0 : 2, e.GetPointerId(i));
            }
            return true;
        }
        // a match's results panel ("tap: back to the menu"; the touch controls do this while they are shown)
        if (e != null && e.ActionMasked == MotionEventActions.Down && _renderer.MatchOver)
        {
            _renderer.RequestMatchContinue();
            return true;
        }
        // the panel's rows: the ship's save prompt YES / NO, game over's rows (CampaignShip.cs ShipPanelTap; the touch
        // controls pass these taps on)
        if (e != null && e.ActionMasked == MotionEventActions.Down && _renderer.ShipPanelUp
            && PanelLine(e.GetX(), e.GetY()) is int line && _renderer.ShipPanelTap(_panel!.Text ?? "", line))
        {
            return true;
        }
        // a dialog's own OK / YES / NO / page arrows (CampaignRenderer.DialogTouch.cs; the touch controls test them first
        // while they are shown)
        if (e != null && e.ActionMasked == MotionEventActions.Down && _renderer.DialogTouch(e.GetX(), e.GetY()))
        {
            return true;
        }
        // taps during play belong to the touch controls (MatchTouchOverlay.cs, a view above the game: they never get
        // here while it is shown); the view stays WIDE (CampaignView.cs, board #30a)
        return base.OnTouchEvent(e);
    }

    public override bool OnKeyUp(Keycode keyCode, KeyEvent? e)
    {
        // a button let go while the customizer waits: the capture commits once every pressed button is up (combos)
        if (_renderer.PauseMenu?.CaptureDevice != null
            && (keyCode == Keycode.ButtonSelect ? PadButton.Select : PadFor(keyCode)) is PadButton released)
        {
            string name = "-" + released;
            _renderer.PauseMenuInput(p => p.CaptureInput(name));
            return true;
        }
        if (keyCode is Keycode.ButtonL2 or Keycode.ButtonR2) (keyCode == Keycode.ButtonR2 ? _menuR2 : _menuL2).Button(false, MenuPress);
        if (PadFor(keyCode) is PadButton up)
        {
            _renderer.Pad.Set(up, false);
            return true;
        }
        if (keyCode == Keycode.ButtonSelect)
        {
            _renderer.Pad.Set(PadButton.Select, false);
            return true;
        }
        if (keyCode == Keycode.Back) return true; // handled on the press (OnKeyDown); the release must not finish us
        return base.OnKeyUp(keyCode, e);
    }
}
