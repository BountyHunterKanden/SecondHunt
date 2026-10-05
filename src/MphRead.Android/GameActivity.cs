using Android.App;
using Android.Content;
using Android.Opengl;
using Android.OS;
using Android.Util;
using Android.Views;
using Android.Widget;
using MphRecomp.Frontend;

namespace MphRecomp.App;

// The game's own boot -- logos, title, main menu -- drawn by our front end from the user's ROM (FrontendSession +
// FrontendRenderer), laid out for this screen's shape; the one-time ROM setup (RomSetupActivity) comes first when
// nothing has been extracted yet. A public build (MPH_PUBLIC) boots straight into this (owner, 2026-09-29: "for the
// final version"; Beta 1, 2026-10-04); a dev build opens it from the launcher's "Play game" (MainActivity). Hold
// Select and press Start anywhere in the menus to go back to the dev launcher (dev builds, or developer mode on).
//   am start -n com.mphrecomp.app/com.mphrecomp.app.GameActivity [--es page 18]
[Activity(Name = "com.mphrecomp.app.GameActivity", MainLauncher = BuildFlags.Public, Exported = true, // label = the app's
    LaunchMode = Android.Content.PM.LaunchMode.SingleTask,
    ScreenOrientation = Android.Content.PM.ScreenOrientation.SensorLandscape,
    Theme = "@android:style/Theme.Black.NoTitleBar.Fullscreen",
    ConfigurationChanges = Android.Content.PM.ConfigChanges.Orientation | Android.Content.PM.ConfigChanges.ScreenSize
        | Android.Content.PM.ConfigChanges.ScreenLayout | Android.Content.PM.ConfigChanges.Keyboard
        | Android.Content.PM.ConfigChanges.KeyboardHidden | Android.Content.PM.ConfigChanges.Navigation)]
public class GameActivity : Activity
{
    GLSurfaceView? _view;
    FrontendRenderer? _renderer;
    TextView? _status;
    bool _selectHeld;
    int _hatX, _hatY, _stickX, _stickY;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        if (!System.IO.File.Exists(System.IO.Path.Combine(FilesDir!.AbsolutePath, ".extracted")))
        {
            StartActivity(new Intent(this, typeof(RomSetupActivity)));
            Finish();
            return;
        }
        _renderer = new FrontendRenderer(FilesDir!.AbsolutePath, SettingsPath, ModsDir, System.IO.Path.Combine(ExternalDir, "saves"));
        if (Int32.TryParse(Intent?.GetStringExtra("page"), out int page)) _renderer.StartPage = page;
        // back from a campaign: the launcher now sits under this activity, and the campaign's exit (ClearTop to
        // MainActivity) finishes this one, so the redirect makes a fresh instance -- open on the main menu, not the logos
        if (AppNav.ReturnedFromGame)
        {
            AppNav.ReturnedFromGame = false;
            _renderer.StartPage = 18;
        }
        if (Intent?.GetStringExtra("fedump") == "1") _renderer.DumpPath = System.IO.Path.Combine(ExternalDir, "frontend_dump.md");
        _renderer.RequestSink = r => RunOnUiThread(() => OnRequest(r));
        _renderer.HdRoot = System.IO.Path.Combine(ExternalDir, "hd");
        _renderer.CacheDir = System.IO.Path.Combine(ExternalDir, "cache");
        _view = new GLSurfaceView(this);
        _view.SetEGLContextClientVersion(3);
        _view.SetEGLConfigChooser(8, 8, 8, 8, 0, 0);
        _view.PreserveEGLContextOnPause = true;
        _view.SetRenderer(_renderer);
        var root = new FrameLayout(this);
        root.AddView(_view, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent));
        _status = new TextView(this) { TextSize = 13f, Visibility = ViewStates.Gone };
        _status.SetTextColor(Android.Graphics.Color.White);
        _status.SetPadding(32, 24, 32, 24);
        root.AddView(_status, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent));
        _renderer.StatusSink = s => RunOnUiThread(() => { _status.Text = s; _status.Visibility = ViewStates.Visible; });
        SetContentView(root);
        HideSystemUi();
    }

    // beside the user's other files (<external files>/, reachable over USB/adb): the recomp settings and the mods folder
    string ExternalDir => GetExternalFilesDir(null)?.AbsolutePath ?? FilesDir!.AbsolutePath;
    string SettingsPath => System.IO.Path.Combine(ExternalDir, "recomp_settings.json");
    string ModsDir => System.IO.Path.Combine(ExternalDir, "mods");

    protected override void OnPause() { base.OnPause(); _view?.OnPause(); _renderer?.PauseAudio(); }

    protected override void OnResume()
    {
        base.OnResume();
        _view?.OnResume();
        _renderer?.ResumeAudio();
        HideSystemUi();
        ApplyFrameRate();
    }

    // RECOMP SETTINGS > FRAME RATE: ask the display for that refresh rate (MAX = whatever it does best)
    void ApplyFrameRate()
    {
        var settings = MphRecomp.Config.RecompSettings.Load(SettingsPath);
        var attrs = Window!.Attributes!;
        attrs.PreferredRefreshRate = settings.FrameRate;
        Window.Attributes = attrs;
    }

    // back from a game: the main menu
    protected override void OnRestart()
    {
        base.OnRestart();
        // the pause menu may have changed settings meanwhile (its own copy, saved to the same file)
        _renderer?.Post(s => s.Settings.ReloadFrom(SettingsPath));
        if (AppNav.ReturnedFromGame)
        {
            AppNav.ReturnedFromGame = false;
            _renderer?.Post(s => s.Menu.Enter(18));
        }
    }

    public override void OnWindowFocusChanged(bool hasFocus)
    {
        base.OnWindowFocusChanged(hasFocus);
        if (hasFocus) HideSystemUi();
    }

    void HideSystemUi() => Window!.DecorView.SystemUiVisibility = (StatusBarVisibility)(
        SystemUiFlags.ImmersiveSticky | SystemUiFlags.HideNavigation | SystemUiFlags.Fullscreen |
        SystemUiFlags.LayoutStable | SystemUiFlags.LayoutHideNavigation | SystemUiFlags.LayoutFullscreen);

    // ---- leaving the menus ----

    void OnRequest(FrontendRequest request)
    {
        switch (request.Kind)
        {
        case FrontendRequestKind.StartAdventure:
        {
            // files A/B/C = the campaign's save slots 1/2/3: a file with a save continues it, an empty one starts a new
            // game in the Celestial Archives
            int slot = request.Slot + 1;
            string baseDir = GetExternalFilesDir(null)?.AbsolutePath ?? FilesDir!.AbsolutePath;
            bool saved = System.IO.File.Exists(System.IO.Path.Combine(baseDir, "saves", $"campaign{slot}.json"));
            var intent = new Intent(this, typeof(CampaignActivity)).PutExtra("room", "UNIT2_LAND");
            // save=<n> continues that file (and saves back to it); slot=<n> is the file a new game saves to
            intent.PutExtra(saved ? "save" : "slot", slot.ToString());
            Log.Info("MPHFrontend", $"StartAdventure -> CampaignActivity slot {slot} ({(saved ? "save" : "new")})");
            AppNav.ReturnToGame = true;
            StartActivity(intent);
            break;
        }
        case FrontendRequestKind.OpenDeveloperMenu:
            OpenDeveloperMenu();
            break;
        case FrontendRequestKind.PlayMovie:
            break; // played by FrontendRenderer itself
        case FrontendRequestKind.Multiplayer:
            StartActivity(new Intent(this, typeof(MatchLobbyActivity)));
            break;
        case FrontendRequestKind.Credits:
            StartActivity(new Intent(this, typeof(CreditsActivity)));
            break;
        case FrontendRequestKind.ShareCrashLog:
            ShareCrashLog(this);
            break;
        }
    }

    // RECOMP SETTINGS > CRASH LOG (S29): the newest crash report through the system share sheet
    internal static void ShareCrashLog(Activity activity)
    {
        Intent? share = CrashLog.ShareIntent();
        if (share == null)
        {
            Toast.MakeText(activity, "No crash reports. Nothing has crashed.", ToastLength.Short)!.Show();
            return;
        }
        CrashLog.MarkSeen();
        activity.StartActivity(Intent.CreateChooser(share, "Share the crash report"));
    }

    void OpenDeveloperMenu()
    {
        AppNav.ReturnToGame = false;
        // back to the launcher underneath (not a second copy of it on top)
        StartActivity(new Intent(this, typeof(MainActivity)).PutExtra("dev", "1")
            .AddFlags(ActivityFlags.ClearTop | ActivityFlags.SingleTop));
    }

    // ---- input: gamepad, D-pad (the Odin reports it as a HAT axis), stick, touch ----

    static MenuKeys KeyFor(Keycode k) => k switch
    {
        Keycode.ButtonA or Keycode.DpadCenter or Keycode.Enter => MenuKeys.A,
        Keycode.ButtonB or Keycode.Back or Keycode.Escape => MenuKeys.B,
        Keycode.ButtonX => MenuKeys.X,
        Keycode.ButtonY => MenuKeys.Y,
        Keycode.ButtonStart => MenuKeys.Start,
        Keycode.ButtonSelect => MenuKeys.Select,
        Keycode.ButtonL1 => MenuKeys.L,
        Keycode.ButtonR1 => MenuKeys.R,
        _ => MenuKeys.None
    };

    public override bool OnKeyDown(Keycode keyCode, KeyEvent? e)
    {
        if (_renderer == null) return base.OnKeyDown(keyCode, e);
        if (keyCode == Keycode.ButtonSelect) _selectHeld = true;
        if (keyCode == Keycode.ButtonStart && _selectHeld && !BuildFlags.Public)
        {
            OpenDeveloperMenu();
            return true;
        }
        switch (keyCode)
        {
        case Keycode.DpadUp: Direction(0, 1); return true;
        case Keycode.DpadDown: Direction(0, -1); return true;
        case Keycode.DpadLeft: Direction(-1, 0); return true;
        case Keycode.DpadRight: Direction(1, 0); return true;
        }
        MenuKeys key = KeyFor(keyCode);
        if (key != MenuKeys.None)
        {
            if (e?.RepeatCount == 0) _renderer.Post(s => s.Press(key));
            return true;
        }
        return base.OnKeyDown(keyCode, e);
    }

    public override bool OnKeyUp(Keycode keyCode, KeyEvent? e)
    {
        if (keyCode == Keycode.ButtonSelect) _selectHeld = false;
        return KeyFor(keyCode) != MenuKeys.None || base.OnKeyUp(keyCode, e);
    }

    void Direction(int dx, int dy) => _renderer?.Post(s => s.Navigate(dx, dy));

    public override bool OnGenericMotionEvent(MotionEvent? e)
    {
        if (e != null && e.Source.HasFlag(InputSourceType.Joystick) && e.Action == MotionEventActions.Move)
        {
            int hx = Step(e.GetAxisValue(Axis.HatX)), hy = Step(e.GetAxisValue(Axis.HatY));
            if (hx != _hatX && hx != 0) Direction(hx, 0);
            if (hy != _hatY && hy != 0) Direction(0, -hy);
            _hatX = hx; _hatY = hy;
            int sx = Step(e.GetAxisValue(Axis.X), 0.6f), sy = Step(e.GetAxisValue(Axis.Y), 0.6f);
            if (sx != _stickX && sx != 0) Direction(sx, 0);
            if (sy != _stickY && sy != 0) Direction(0, -sy);
            _stickX = sx; _stickY = sy;
            return true;
        }
        return base.OnGenericMotionEvent(e);
    }

    static int Step(float v, float threshold = 0.5f) => v >= threshold ? 1 : v <= -threshold ? -1 : 0;

    public override bool OnTouchEvent(MotionEvent? e)
    {
        if (e != null && e.ActionMasked == MotionEventActions.Down && _renderer != null)
        {
            float x = e.GetX(), y = e.GetY();
            _renderer.Post(s => s.TouchCanvas(x, y));
            return true;
        }
        return base.OnTouchEvent(e);
    }
}

// Where "exit" in a game goes: the campaign leaves through the developer menu (MainActivity); when it was started from
// the front end, that bounces straight back here.
internal static class AppNav
{
    public static bool ReturnToGame;
    public static bool ReturnedFromGame;
}
