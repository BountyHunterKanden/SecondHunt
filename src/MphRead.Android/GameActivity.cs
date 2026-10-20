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
    float _trigL, _trigR; // the triggers last motion event (the control customizer's capture)
    float _stickRy; // the right stick's up / down: scrolls settings and CONTROLS (FrontendSession.ScrollStick)

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
            _renderer.StartPage = FrontendSession.ReturnPage(); // the main menu, or the multiplayer lobby after a match
        }
        if (Intent?.GetStringExtra("fedump") == "1") _renderer.DumpPath = System.IO.Path.Combine(ExternalDir, "frontend_dump.md");
        _renderer.RequestSink = r => RunOnUiThread(() => OnRequest(r));
        MultiplayerMenu.MulticastLock = () => MenuLan.AcquireMulticast(this); // AVAILABLE GAMES hears LAN beacons (M3)
        _renderer.HdRoot = System.IO.Path.Combine(ExternalDir, "hd");
        _renderer.CacheDir = System.IO.Path.Combine(ExternalDir, "cache");
        // IMPORT GAMES (RECOMP SETTINGS): the system's document picker, and its files read in place (never copied)
        ImportHost.Pick = (request, picked) => RunOnUiThread(() => PickImportFile(request, picked));
        ImportHost.Open = OpenImportFile;
        ImportHost.OpenFolder = (token, marker) => ImportTreeSource.Open(Application.Context.ContentResolver!, token, marker);
        _view = new GLSurfaceView(this);
        _view.SetEGLContextClientVersion(3);
        _view.SetEGLConfigChooser(8, 8, 8, 8, 0, 0);
        _view.PreserveEGLContextOnPause = true;
        _view.SetRenderer(new GlScreenRenderer(_renderer));
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
            _renderer?.Post(s => s.Start(FrontendSession.ReturnPage())); // the main menu, the lobby after a match, or an ending's credits
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
        case FrontendRequestKind.StartMatch:
        {
            // START GAME in the vanilla multiplayer menu: the match on the campaign screen; back here afterwards, where
            // the menus open at FrontendSession.ReturnPage() (the lobby)
            var intent = new Intent(this, typeof(CampaignActivity));
            foreach ((string key, string value) in MatchSetupText.LaunchArgs(request.Arg!)) intent.PutExtra(key, value);
            Log.Info("MPHFrontend", "StartMatch -> CampaignActivity " + request.Arg);
            AppNav.ReturnToGame = true;
            StartActivity(intent);
            break;
        }
        case FrontendRequestKind.PlayReplay:
        {
            // a recorded match from the REPLAYS list, played back on the campaign screen (MatchReplay.cs); back here after
            var intent = new Intent(this, typeof(CampaignActivity)).PutExtra("replay", request.Arg!);
            Log.Info("MPHFrontend", "PlayReplay -> CampaignActivity " + request.Arg);
            AppNav.ReturnToGame = true;
            StartActivity(intent);
            break;
        }
        case FrontendRequestKind.Credits:
            StartActivity(new Intent(this, typeof(CreditsActivity)));
            break;
        case FrontendRequestKind.OnlineRules: // Options' privacy / code of conduct / 13+ line: the same viewer, that text
            StartActivity(new Intent(this, typeof(CreditsActivity)).PutExtra("doc", "online"));
            break;
        case FrontendRequestKind.ShareCrashLog:
            ShareCrashLog(this);
            break;
        case FrontendRequestKind.ShareLog:
            ShareLog(this);
            break;
        case FrontendRequestKind.ShareReplay: // REPLAYS' SHARE (ReplaySharing.cs)
            ReplaySharing.Share(this, request.Arg);
            break;
        case FrontendRequestKind.SaveLog: // RECOMP SETTINGS > SAVE LOG: into Downloads/Second Hunt (DeviceSave.cs)
            SaveLog(this);
            break;
        case FrontendRequestKind.FirstHunt: // RECOMP SETTINGS' extras row: First Hunt's own menus (FirstHuntActivity)
            StartActivity(new Intent(this, typeof(FirstHuntActivity)));
            break;
        }
    }

    // ---- IMPORT GAMES: the player's own disc images and Wii key file ----

    const int ImportPickRequest = 0x4d50;
    Action<ImportPicked?>? _importPicked;

    // the system's document picker (any file: disc images have no MIME type of their own), or its folder picker for a
    // game imported from its files (Prime 4's RomFS: ImportTreeSource); the pick keeps its read access across restarts
    // (TakePersistableUriPermission), so importing again doesn't ask for the disc again
    void PickImportFile(ImportPickRequest request, Action<ImportPicked?> picked)
    {
        _importPicked?.Invoke(null); // an earlier picker that never answered
        _importPicked = picked;
        var intent = new Intent(request.Kind == ImportPickKind.Folder ? Intent.ActionOpenDocumentTree : Intent.ActionOpenDocument);
        if (request.Kind != ImportPickKind.Folder)
        {
            intent.AddCategory(Intent.CategoryOpenable);
            intent.SetType("*/*");
        }
        try
        {
            StartActivityForResult(intent, ImportPickRequest);
        }
        catch (ActivityNotFoundException)
        {
            _importPicked = null;
            picked(null);
        }
    }

    protected override void OnActivityResult(int requestCode, Result resultCode, Intent? data)
    {
        base.OnActivityResult(requestCode, resultCode, data);
        if (requestCode != ImportPickRequest) return;
        Action<ImportPicked?>? picked = _importPicked;
        _importPicked = null;
        Android.Net.Uri? uri = resultCode == Result.Ok ? data?.Data : null;
        if (uri == null)
        {
            picked?.Invoke(null);
            return;
        }
        try
        {
            ContentResolver!.TakePersistableUriPermission(uri, ActivityFlags.GrantReadUriPermission);
        }
        catch (Java.Lang.SecurityException ex)
        {
            Log.Warn("MPHFrontend", "import: no lasting access to the picked file: " + ex.Message);
        }
        Log.Info("MPHFrontend", "import: picked " + uri);
        // (a folder's name: its own document's, under the tree)
        bool tree = Android.Provider.DocumentsContract.IsTreeUri(uri);
        Android.Net.Uri named = tree ? Android.Provider.DocumentsContract.BuildDocumentUriUsingTree(uri, Android.Provider.DocumentsContract.GetTreeDocumentId(uri))! : uri;
        picked?.Invoke(new ImportPicked(uri.ToString()!, DisplayName(named)));
    }

    string DisplayName(Android.Net.Uri uri)
    {
        try
        {
            using Android.Database.ICursor? c = ContentResolver!.Query(uri, new[] { "_display_name" }, null, null, null);
            if (c != null && c.MoveToFirst() && c.GetString(0) is string name && name.Length > 0) return name;
        }
        catch (Exception ex) when (ex is Java.Lang.Exception or InvalidOperationException)
        {
            Log.Warn("MPHFrontend", "import: no name for " + uri + ": " + ex.Message);
        }
        return uri.LastPathSegment ?? "disc image";
    }

    // a picked content:// file as a seekable stream over its own file descriptor (the disc image is read in place); a
    // plain path as a file. null when it's gone or its access was withdrawn
    static System.IO.Stream? OpenImportFile(string token)
    {
        if (!token.StartsWith("content://", StringComparison.Ordinal)) return ImportHost.OpenPath(token);
        if (Android.Provider.DocumentsContract.IsTreeUri(Android.Net.Uri.Parse(token)!)) return null; // a picked folder, not a file
        ParcelFileDescriptor? pfd;
        try
        {
            pfd = Application.Context.ContentResolver!.OpenFileDescriptor(Android.Net.Uri.Parse(token)!, "r");
        }
        catch (Exception ex) when (ex is Java.IO.FileNotFoundException or Java.Lang.SecurityException or Java.Lang.IllegalArgumentException)
        {
            Log.Warn("MPHFrontend", $"import: can't open {token}: {ex.Message}");
            return null;
        }
        if (pfd == null) return null;
        int fd = pfd.DetachFd(); // ours now: the stream closes it
        pfd.Close();
        var stream = new System.IO.FileStream(new Microsoft.Win32.SafeHandles.SafeFileHandle((IntPtr)fd, ownsHandle: true),
            System.IO.FileAccess.Read, 1 << 16);
        if (!stream.CanSeek)
        {
            stream.Dispose();
            throw new System.IO.IOException("this file can't be read in place (its app streams it); copy it to the device first");
        }
        return stream;
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
        activity.StartActivity(TextSheet(activity, share, "Share the crash report", $"second-hunt-crash-{DateTime.Now:yyyyMMdd-HHmmss}.txt"));
    }

    // RECOMP SETTINGS > SHARE LOG: this run's log through the system share sheet (CrashLog.ShareLogIntent)
    internal static void ShareLog(Activity activity)
    {
        Intent? share = CrashLog.ShareLogIntent();
        if (share == null)
        {
            Toast.MakeText(activity, "The log couldn't be read.", ToastLength.Short)!.Show();
            return;
        }
        activity.StartActivity(TextSheet(activity, share, "Share the log", $"second-hunt-log-{DateTime.Now:yyyyMMdd-HHmmss}.txt"));
    }

    // a text share's sheet, SAVE TO DOWNLOADS among its targets (the text as a .txt: DeviceSave.Chooser)
    static Intent TextSheet(Activity activity, Intent send, string title, string name)
    {
        byte[] bytes = System.Text.Encoding.UTF8.GetBytes(send.GetStringExtra(Intent.ExtraText) ?? "");
        return DeviceSave.Chooser(activity, send, title, name, "text/plain", s => s.Write(bytes));
    }

    // SAVE LOG: this run's log as a .txt in Downloads/Second Hunt (the share sheet on Android 8-9)
    internal static void SaveLog(Activity activity)
    {
        string text;
        try
        {
            text = CrashLog.LogReport();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Toast.MakeText(activity, "The log couldn't be read.", ToastLength.Short)!.Show();
            return;
        }
        byte[] bytes = System.Text.Encoding.UTF8.GetBytes(text);
        if (!DeviceSave.ToDownloads(activity, $"second-hunt-log-{DateTime.Now:yyyyMMdd-HHmmss}.txt", "text/plain", s => s.Write(bytes)))
        {
            ShareLog(activity);
        }
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
        // the control customizer waits for a controller button (OPTIONS > CONTROLS): every press and release goes to it (Select
        // and R3 too: customizer functions since 2026-10-11), Back / Start keep the buttons as they are; nothing reaches the
        // menu meanwhile
        if (_renderer.CaptureDevice != null)
        {
            if (e?.RepeatCount == 0)
            {
                if ((keyCode == Keycode.ButtonSelect ? PadButton.Select : CampaignActivity.PadFor(keyCode)) is PadButton b)
                {
                    string name = b.ToString();
                    _renderer.Post(s => s.Recomp.CaptureInput(name));
                }
                else if (keyCode is Keycode.Back or Keycode.ButtonStart or Keycode.Escape)
                {
                    _renderer.Post(s => s.Recomp.CancelCapture());
                }
            }
            return true;
        }
        if (keyCode == Keycode.ButtonSelect) _selectHeld = true;
        // online voice chat (O8): the push to talk button held (R3 unless moved) = push to talk in an online game's lobby
        if (e?.RepeatCount == 0) LobbyTalk(keyCode, true);
        if (keyCode == Keycode.ButtonThumbr) return true; // (the menus don't use R3)
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
        // a button let go while the customizer waits: the capture commits once every pressed button is up (combos)
        if (_renderer?.CaptureDevice != null
            && (keyCode == Keycode.ButtonSelect ? PadButton.Select : CampaignActivity.PadFor(keyCode)) is PadButton released)
        {
            string name = "-" + released;
            _renderer.Post(s => s.Recomp.CaptureInput(name));
            return true;
        }
        LobbyTalk(keyCode, false);
        if (keyCode == Keycode.ButtonThumbr) return true;
        return KeyFor(keyCode) != MenuKeys.None || base.OnKeyUp(keyCode, e);
    }

    // a controller button that is the customizer's push to talk (RECOMP SETTINGS > CONTROLS) talks in a lobby; checked on
    // the menus' thread, where the settings live
    void LobbyTalk(Keycode keyCode, bool down)
    {
        if ((keyCode == Keycode.ButtonSelect ? PadButton.Select : CampaignActivity.PadFor(keyCode)) is not PadButton b) return;
        string name = b.ToString();
        _renderer?.Post(s =>
        {
            if (Array.IndexOf(MphRecomp.Config.ControlBinds.Get(s.Settings, false, MphRecomp.Config.ControlBinds.Talk), name) >= 0)
            {
                MphRecomp.Online.Voice.VoiceChat.HoldToTalk(down);
            }
        });
    }

    void Direction(int dx, int dy) => _renderer?.Post(s => s.Navigate(dx, dy));

    public override bool OnGenericMotionEvent(MotionEvent? e)
    {
        if (e != null && e.Source.HasFlag(InputSourceType.Joystick) && e.Action == MotionEventActions.Move)
        {
            int hx = Step(e.GetAxisValue(Axis.HatX)), hy = Step(e.GetAxisValue(Axis.HatY));
            float l2 = e.GetAxisValue(Axis.Ltrigger), r2 = e.GetAxisValue(Axis.Rtrigger);
            if (_renderer?.CaptureDevice != null)
            {
                // the customizer's capture: a trigger past half way, or a D-pad push (the Odin's D-pad is a HAT axis)
                CampaignActivity.CaptureAxes(name => _renderer.Post(s => s.Recomp.CaptureInput(name)), l2, r2, hx, hy,
                    _trigL, _trigR, _hatX, _hatY);
                _hatX = hx; _hatY = hy; _trigL = l2; _trigR = r2;
                return true;
            }
            _trigL = l2; _trigR = r2;
            float ry = e.GetAxisValue(Axis.Rz);
            if (Math.Abs(ry - _stickRy) > 0.05f || (Math.Abs(ry) < 0.3f) != (Math.Abs(_stickRy) < 0.3f))
            {
                _stickRy = ry;
                _renderer?.Post(s => s.ScrollStick(ry));
            }
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
        // down, move, up, cancel: a drag scrolls settings and CONTROLS, a tap is a tap (FrontendSession.TouchCanvas)
        int kind = e?.ActionMasked switch
        {
            MotionEventActions.Down => 0, MotionEventActions.Move => 1, MotionEventActions.Up => 2, MotionEventActions.Cancel => 3, _ => -1
        };
        if (e != null && kind >= 0 && _renderer != null)
        {
            float x = e.GetX(), y = e.GetY();
            _renderer.Post(s => s.TouchCanvas(x, y, kind));
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
