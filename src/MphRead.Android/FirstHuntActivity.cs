using Android.App;
using Android.Content;
using Android.Opengl;
using Android.OS;
using Android.Views;
using Android.Widget;
using System;
using MphRecomp.FirstHunt;

namespace MphRecomp.App;

// First Hunt's own menus (docs/FIRST_HUNT_PORT.md P4; FirstHuntMenuRenderer.cs): the shared renderer in a GLSurfaceView
// with this device's input -- touch on the right-hand (touch screen) half, the D-pad / left stick to move, A / Start to
// select, B / Back for the page's BACK. A training game opens the campaign screen (fhlevel + fhmenu) over this one; when
// it closes, OnResume brings back its results.
//   am start -n com.mphrecomp.app/com.mphrecomp.app.FirstHuntActivity
[Activity(Name = "com.mphrecomp.app.FirstHuntActivity", Label = "First Hunt",
    Exported = BuildFlags.ExportDevActivities, ScreenOrientation = Android.Content.PM.ScreenOrientation.Landscape,
    Theme = "@android:style/Theme.Black.NoTitleBar.Fullscreen",
    ConfigurationChanges = Android.Content.PM.ConfigChanges.Orientation | Android.Content.PM.ConfigChanges.ScreenSize
        | Android.Content.PM.ConfigChanges.ScreenLayout | Android.Content.PM.ConfigChanges.Keyboard
        | Android.Content.PM.ConfigChanges.KeyboardHidden | Android.Content.PM.ConfigChanges.Navigation)]
public class FirstHuntActivity : Activity
{
    FirstHuntMenuRenderer _renderer = null!;
    GLSurfaceView _view = null!;
    TextView _status = null!;
    bool _inGame;
    FhPad _stickHeld;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        string ext = GetExternalFilesDir(null)?.AbsolutePath ?? FilesDir!.AbsolutePath;
        _renderer = new FirstHuntMenuRenderer(FilesDir!.AbsolutePath, ext, System.IO.Path.Combine(ext, "recomp_settings.json"));
        _view = new GLSurfaceView(this);
        _view.SetEGLContextClientVersion(3);
        _view.SetEGLConfigChooser(8, 8, 8, 8, 24, 8);
        _view.PreserveEGLContextOnPause = true;
        _view.SetRenderer(new GlScreenRenderer(_renderer));
        _view.Touch += (_, e) =>
        {
            e.Handled = true;
            MotionEvent? m = e.Event;
            if (m == null) return;
            switch (m.ActionMasked)
            {
            case MotionEventActions.Down:
            case MotionEventActions.Move:
                _renderer.Touch(true, m.GetX(), m.GetY());
                break;
            case MotionEventActions.Up:
            case MotionEventActions.Cancel:
                _renderer.Touch(false, m.GetX(), m.GetY());
                break;
            }
        };
        var root = new FrameLayout(this);
        root.AddView(_view, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent));
        _status = new TextView(this) { TextSize = 14f };
        _status.SetTextColor(Android.Graphics.Color.White);
        _status.SetPadding(24, 16, 24, 16);
        root.AddView(_status, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent)
        { Gravity = GravityFlags.Top | GravityFlags.Left });
        _renderer.StatusSink = s => RunOnUiThread(() => _status.Text = s);
        _renderer.ExitSink = () => RunOnUiThread(Finish);
        _renderer.GameSink = g => RunOnUiThread(() =>
        {
            _inGame = true;
            var intent = new Intent(this, typeof(CampaignActivity)).PutExtra("fhmenu", "1").PutExtra("gun", "none");
            // a deathmatch (P5): a LAN match on LanSession.Current (its settings are the match), as the vanilla menu's
            if (g.Net != null) intent.PutExtra("net", g.Net).PutExtra("menu", "1").PutExtra("hunter", "Samus");
            else intent.PutExtra("fhlevel", g.Room);
            StartActivity(intent);
        });
        SetContentView(root);
        HideSystemUi();
    }

    protected override void OnPause()
    {
        base.OnPause();
        _view.OnPause();
        _renderer.Pause();
    }

    protected override void OnResume()
    {
        base.OnResume();
        _view.OnResume();
        HideSystemUi();
        if (_inGame)
        {
            _inGame = false;
            _renderer.ReturnFromGame();
        }
        else
        {
            _renderer.Resume();
        }
    }

    protected override void OnDestroy()
    {
        _renderer.Dispose();
        base.OnDestroy();
    }

    public override void OnWindowFocusChanged(bool hasFocus)
    {
        base.OnWindowFocusChanged(hasFocus);
        if (hasFocus) HideSystemUi();
    }

    void HideSystemUi()
    {
        Window!.DecorView.SystemUiVisibility = (StatusBarVisibility)(SystemUiFlags.ImmersiveSticky
            | SystemUiFlags.HideNavigation | SystemUiFlags.Fullscreen | SystemUiFlags.LayoutStable
            | SystemUiFlags.LayoutHideNavigation | SystemUiFlags.LayoutFullscreen);
    }

    static FhPad KeyPad(Keycode key) => key switch
    {
        Keycode.DpadUp => FhPad.Up,
        Keycode.DpadDown => FhPad.Down,
        Keycode.DpadLeft => FhPad.Left,
        Keycode.DpadRight => FhPad.Right,
        Keycode.ButtonA or Keycode.ButtonStart or Keycode.Enter or Keycode.DpadCenter => FhPad.Confirm,
        Keycode.ButtonB or Keycode.Back or Keycode.Escape => FhPad.Back,
        _ => FhPad.None
    };

    public override bool OnKeyDown(Keycode keyCode, KeyEvent? e)
    {
        FhPad pad = KeyPad(keyCode);
        if (pad == FhPad.None) return base.OnKeyDown(keyCode, e);
        if (e?.RepeatCount == 0 || (pad & (FhPad.Confirm | FhPad.Back)) == 0) _renderer.Pad(pad);
        return true;
    }

    public override bool OnKeyUp(Keycode keyCode, KeyEvent? e) => KeyPad(keyCode) != FhPad.None || base.OnKeyUp(keyCode, e);

    // the left stick and the hat as a D-pad: a push past half way moves once
    public override bool OnGenericMotionEvent(MotionEvent? e)
    {
        if (e != null && e.Source.HasFlag(InputSourceType.Joystick) && e.Action == MotionEventActions.Move)
        {
            float x = e.GetAxisValue(Axis.X) + e.GetAxisValue(Axis.HatX), y = e.GetAxisValue(Axis.Y) + e.GetAxisValue(Axis.HatY);
            FhPad held = (y < -0.5f ? FhPad.Up : y > 0.5f ? FhPad.Down : FhPad.None)
                | (x < -0.5f ? FhPad.Left : x > 0.5f ? FhPad.Right : FhPad.None);
            FhPad pressed = held & ~_stickHeld;
            _stickHeld = held;
            if (pressed != FhPad.None) _renderer.Pad(pressed);
            return true;
        }
        return base.OnGenericMotionEvent(e);
    }
}
