using System.IO;
using Android.App;
using Android.Graphics;
using Android.OS;
using Android.Views;
using Android.Widget;

namespace MphRecomp.App;

// RECOMP SETTINGS > CREDITS (public beta audit S29): the app's version, what it is built on, and every third-party
// notice the APK must carry (THIRD_PARTY_NOTICES.md at the repo root, embedded at build time as "notices.md").
// Scrolls with touch, the D-pad or the stick; B / Back closes it.
[Activity(Name = "com.mphrecomp.app.CreditsActivity", Label = "Credits", Exported = false,
    ScreenOrientation = Android.Content.PM.ScreenOrientation.SensorLandscape,
    Theme = "@android:style/Theme.Black.NoTitleBar.Fullscreen")]
public class CreditsActivity : Activity
{
    ScrollView? _scroll;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        string version = PackageManager?.GetPackageInfo(PackageName!, 0)?.VersionName ?? "";
        var text = new TextView(this)
        {
            Text = Header(version) + Notices(),
            TextSize = 13f,
            Typeface = Typeface.Monospace,
        };
        text.SetTextColor(Color.Rgb(0xD8, 0xE8, 0xF0));
        text.SetPadding(48, 32, 48, 48);
        _scroll = new ScrollView(this) { Focusable = true, FocusableInTouchMode = true };
        _scroll.SetBackgroundColor(Color.Rgb(0x05, 0x0A, 0x14));
        _scroll.AddView(text);
        SetContentView(_scroll);
        _scroll.RequestFocus();
        Window!.DecorView.SystemUiVisibility = (StatusBarVisibility)(SystemUiFlags.ImmersiveSticky
            | SystemUiFlags.HideNavigation | SystemUiFlags.Fullscreen);
    }

    static string Header(string version) =>
        $"{Application.Context.ApplicationInfo?.LoadLabel(Application.Context.PackageManager!)} {version}\n\n"
        + "A fan-made recompilation project built on MphRead by NoneGiven (MIT licence).\n"
        + "Metroid Prime Hunters is (c) Nintendo. This app contains no game code or data: it runs from the ROM\n"
        + "you provide. It is not affiliated with or endorsed by Nintendo.\n\n"
        + "----------------------------------------------------------------------------------------------\n\n";

    static string Notices()
    {
        using Stream? s = typeof(CreditsActivity).Assembly.GetManifestResourceStream("notices.md");
        if (s == null) return "(notices missing from this build)";
        using var r = new StreamReader(s);
        return r.ReadToEnd();
    }

    public override bool OnKeyDown(Keycode keyCode, KeyEvent? e)
    {
        switch (keyCode)
        {
        case Keycode.ButtonB:
            Finish();
            return true;
        case Keycode.DpadDown:
            _scroll?.SmoothScrollBy(0, 400);
            return true;
        case Keycode.DpadUp:
            _scroll?.SmoothScrollBy(0, -400);
            return true;
        case Keycode.ButtonR1:
            _scroll?.PageScroll(FocusSearchDirection.Down);
            return true;
        case Keycode.ButtonL1:
            _scroll?.PageScroll(FocusSearchDirection.Up);
            return true;
        }
        return base.OnKeyDown(keyCode, e);
    }

    public override bool OnGenericMotionEvent(MotionEvent? e)
    {
        if (e != null && e.Source.HasFlag(InputSourceType.Joystick) && e.Action == MotionEventActions.Move)
        {
            float y = e.GetAxisValue(Axis.Y) + e.GetAxisValue(Axis.HatY) + e.GetAxisValue(Axis.Rz);
            if (System.Math.Abs(y) > 0.3f) _scroll?.ScrollBy(0, (int)(y * 60));
            return true;
        }
        return base.OnGenericMotionEvent(e);
    }
}
