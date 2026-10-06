using Android.App;
using Android.Content;
using Android.Graphics;
using Android.Graphics.Drawables;
using Android.OS;
using Android.Views;
using Android.Widget;
using MphRead;
using MphRecomp.Multiplayer;
using Button = Android.Widget.Button;
using GameMode = MphRead.GameMode;

namespace MphRecomp.App;

// The player-facing multiplayer screen (Beta 1): the front end's MULTIPLAYER opens it (GameActivity,
// FrontendRequestKind.Multiplayer). The choices and the rules are the shared MatchLobby (MphRecomp.App): a match against
// bots on this device, or a LAN match between two devices on one Wi-Fi network (MatchLan.cs; Beta 1 scope, board S35:
// host + one joiner, Battle only). Only the game's own arenas are listed. Works with a gamepad (D-pad / left stick move, left / right or A change a
// row, B / Back leave) and with touch (tap a row or its arrows). The last choices are kept in SharedPreferences.
// Leaving a match goes back to the game's menus: CampaignActivity exits through MainActivity, which bounces to
// GameActivity because AppNav.ReturnToGame is set here.
[Activity(Name = "com.mphrecomp.app.MatchLobbyActivity", Label = "Multiplayer", Exported = false,
    ScreenOrientation = Android.Content.PM.ScreenOrientation.SensorLandscape,
    Theme = "@android:style/Theme.DeviceDefault.NoActionBar.Fullscreen",
    ConfigurationChanges = Android.Content.PM.ConfigChanges.Orientation | Android.Content.PM.ConfigChanges.ScreenSize
        | Android.Content.PM.ConfigChanges.ScreenLayout | Android.Content.PM.ConfigChanges.Keyboard
        | Android.Content.PM.ConfigChanges.KeyboardHidden | Android.Content.PM.ConfigChanges.Navigation)]
public class MatchLobbyActivity : Activity
{
    const string PrefsName = "match_lobby";

    MatchLobby _lobby = null!;
    bool _leaving;
    int _hatX, _hatY, _stickX, _stickY;

    sealed class Row
    {
        public required Func<string> Value;
        public required Action<int> Step;
        public LinearLayout View = null!;
        public TextView ValueText = null!;
    }

    readonly List<Row> _rows = new();
    readonly List<Button> _actions = new();

    static readonly Color Background = Color.ParseColor("#0A0E14");
    static readonly Color Accent = Color.ParseColor("#4FC3F7");

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        _lobby = new MatchLobby(FilesDir!.AbsolutePath);
        Load();
        BuildUi();
        Refresh();
        _rows[0].View.RequestFocus();
        HideSystemUi();
    }

    // ---- UI ----

    int Dp(float v) => (int)(v * Resources!.DisplayMetrics!.Density + 0.5f);

    GradientDrawable Box(Color fill, Color stroke, float strokeDp)
    {
        var box = new GradientDrawable();
        box.SetColor(fill);
        box.SetStroke(Dp(strokeDp), stroke);
        box.SetCornerRadius(Dp(6));
        return box;
    }

    // dark box; focused (gamepad): lit with the accent edge; pressed (touch / A): brighter
    StateListDrawable FocusBackground()
    {
        var states = new StateListDrawable();
        states.AddState(new[] { Android.Resource.Attribute.StatePressed }, Box(Color.ParseColor("#24557A"), Accent, 2));
        states.AddState(new[] { Android.Resource.Attribute.StateFocused }, Box(Color.ParseColor("#17324A"), Accent, 2));
        states.AddState(Array.Empty<int>(), Box(Color.ParseColor("#151B24"), Color.ParseColor("#263241"), 1));
        return states;
    }

    TextView Text(string text, float sp, Color color, bool bold = false)
    {
        var view = new TextView(this) { Text = text, TextSize = sp };
        view.SetTextColor(color);
        if (bold)
        {
            view.SetTypeface(Typeface.DefaultBold, TypefaceStyle.Bold);
        }
        return view;
    }

    void BuildUi()
    {
        var page = new LinearLayout(this) { Orientation = Orientation.Vertical };
        page.SetPadding(Dp(32), Dp(14), Dp(32), Dp(12));

        page.AddView(Text("MULTIPLAYER", 28f, Color.White, bold: true));
        var subtitle = Text("LAN play is experimental: two players on the same Wi-Fi network, Battle mode only.",
            14f, Color.ParseColor("#FFB74D"));
        subtitle.SetPadding(0, 0, 0, Dp(8));
        page.AddView(subtitle);

        foreach (MatchLobby.Row r in _lobby.Rows())
        {
            AddRow(page, r.Label, r.Value, r.Step);
        }

        // not baseline-aligned: Android lines up the buttons' first text lines by default, which pushed the two-line
        // "Start match (vs bots)" down out of the row
        var buttons = new LinearLayout(this) { Orientation = Orientation.Horizontal, BaselineAligned = false };
        buttons.SetPadding(0, Dp(8), 0, 0);
        AddAction(buttons, "Start match\n(vs bots)", StartBotMatch);
        AddAction(buttons, "Host LAN match", HostLan);
        AddAction(buttons, "Join LAN match", JoinLan);
        AddAction(buttons, "Back", Finish);
        page.AddView(buttons, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));

        var hint = Text("D-pad / stick: move    left / right or A: change    A: press    B: back", 13f,
            Color.ParseColor("#8A96A8"));
        hint.SetPadding(0, Dp(8), 0, 0);
        page.AddView(hint);

        var scroll = new ScrollView(this) { FillViewport = true };
        scroll.SetBackgroundColor(Background);
        scroll.AddView(page);
        SetContentView(scroll);
    }

    void AddRow(LinearLayout page, string label, Func<string> value, Action<int> step)
    {
        var row = new Row { Value = value, Step = step };
        row.View = new LinearLayout(this) { Orientation = Orientation.Horizontal, Focusable = true, Clickable = true };
        row.View.SetGravity(GravityFlags.CenterVertical);
        row.View.Background = FocusBackground();
        row.View.SetPadding(Dp(16), 0, Dp(4), 0);
        row.View.Click += (_, _) => Change(row, 1);

        row.View.AddView(Text(label, 19f, Color.ParseColor("#C9D3E0")),
            new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
        // the arrows are their own touch targets (left = back, right = forward); the rest of the row steps forward
        row.View.AddView(Arrow("‹", () => Change(row, -1)), new LinearLayout.LayoutParams(Dp(52), ViewGroup.LayoutParams.MatchParent));
        row.ValueText = Text("", 19f, Color.White, bold: true);
        row.ValueText.Gravity = GravityFlags.Center;
        row.ValueText.SetSingleLine(true);
        row.ValueText.Ellipsize = Android.Text.TextUtils.TruncateAt.End;
        row.View.AddView(row.ValueText, new LinearLayout.LayoutParams(Dp(260), ViewGroup.LayoutParams.WrapContent));
        row.View.AddView(Arrow("›", () => Change(row, 1)), new LinearLayout.LayoutParams(Dp(52), ViewGroup.LayoutParams.MatchParent));

        var lp = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, Dp(46)) { BottomMargin = Dp(5) };
        page.AddView(row.View, lp);
        _rows.Add(row);
    }

    TextView Arrow(string glyph, Action onTap)
    {
        var arrow = Text(glyph, 26f, Accent, bold: true);
        arrow.Gravity = GravityFlags.Center;
        arrow.Focusable = false;
        arrow.Clickable = true;
        arrow.Click += (_, _) => onTap();
        return arrow;
    }

    void AddAction(LinearLayout parent, string label, Action onPress)
    {
        var button = new Button(this) { Text = label, TextSize = 17f, Focusable = true };
        button.SetAllCaps(false);
        button.SetTextColor(Color.White);
        button.Background = FocusBackground();
        button.StateListAnimator = null;
        button.SetPadding(Dp(8), Dp(6), Dp(8), Dp(6));
        button.Click += (_, _) => onPress();
        var lp = new LinearLayout.LayoutParams(0, Dp(62), 1f);
        lp.SetMargins(_actions.Count == 0 ? 0 : Dp(6), 0, 0, 0);
        parent.AddView(button, lp);
        _actions.Add(button);
    }

    void Change(Row row, int delta)
    {
        row.Step(delta);
        Refresh();
        Save();
    }

    void Refresh()
    {
        foreach (Row row in _rows)
        {
            row.ValueText.Text = row.Value();
        }
    }

    // ---- starting a match ----

    void StartBotMatch()
    {
        if (_leaving)
        {
            return;
        }
        var intent = new Intent(this, typeof(CampaignActivity));
        foreach ((string key, string value) in _lobby.BotMatchArgs())
        {
            intent.PutExtra(key, value);
        }
        StartActivity(intent);
        LeftForMatch();
    }

    // the match screen is up: its exit (through MainActivity) comes back to the game's menus, not to this lobby
    void LeftForMatch()
    {
        _leaving = true;
        Save();
        AppNav.ReturnToGame = true;
        Finish();
    }

    void HostLan()
    {
        if (_leaving)
        {
            return;
        }
        // Beta 1: Battle only, the host plus one joiner (no bots)
        MatchSettings? template = _lobby.LanTemplate(out bool modeChanged);
        Refresh();
        if (modeChanged)
        {
            Save();
            Toast.MakeText(this, "LAN matches are Battle only for now: mode set to Battle", ToastLength.Long)!.Show();
        }
        if (template == null)
        {
            Toast.MakeText(this, "Can't read the game's files, so LAN play can't start: restart the app and try again", ToastLength.Long)!.Show();
            return;
        }
        MatchLan.Host(this, template, Paths.MphKey, MatchLobby.Hunters[_lobby.Hunter], MatchLobby.Gun, LeftForMatch);
    }

    void JoinLan()
    {
        if (_leaving)
        {
            return;
        }
        if (!_lobby.EnsurePaths()) // the ROM revision the handshake compares
        {
            Toast.MakeText(this, "Can't read the game's files, so LAN play can't start: restart the app and try again", ToastLength.Long)!.Show();
            return;
        }
        MatchLan.Join(this, Paths.MphKey, MatchLobby.Hunters[_lobby.Hunter], MatchLobby.Gun, LeftForMatch);
    }

    // ---- remembered choices ----

    void Load()
    {
        try
        {
            ISharedPreferences prefs = GetSharedPreferences(PrefsName, FileCreationMode.Private)!;
            _lobby.Load(key => prefs.GetString(key, null), (key, fallback) => prefs.GetInt(key, fallback));
        }
        catch (Exception ex)
        {
            Android.Util.Log.Warn("MPHLobby", "load choices: " + ex.Message);
        }
    }

    void Save()
    {
        try
        {
            ISharedPreferencesEditor edit = GetSharedPreferences(PrefsName, FileCreationMode.Private)!.Edit()!;
            _lobby.Save((key, value) => edit.PutString(key, value), (key, value) => edit.PutInt(key, value));
            edit.Apply();
        }
        catch (Exception ex)
        {
            Android.Util.Log.Warn("MPHLobby", "save choices: " + ex.Message);
        }
    }

    // ---- input: gamepad (the Odin's D-pad is a HAT axis), keys, touch (the views' own clicks) ----

    void Focus(View view) => view.RequestFocusFromTouch();

    // dx: left / right (changes a row's value, or moves along the buttons); dy: up / down (+1 = down)
    void Move(int dx, int dy)
    {
        View? focused = CurrentFocus;
        int row = _rows.FindIndex(r => r.View == focused);
        int action = focused is Button b ? _actions.IndexOf(b) : -1;
        if (row < 0 && action < 0)
        {
            Focus(_rows[0].View);
            return;
        }
        if (row >= 0)
        {
            if (dx != 0)
            {
                Change(_rows[row], dx);
            }
            else if (dy > 0)
            {
                Focus(row + 1 < _rows.Count ? _rows[row + 1].View : _actions[0]);
            }
            else if (dy < 0 && row > 0)
            {
                Focus(_rows[row - 1].View);
            }
            return;
        }
        if (dx != 0)
        {
            Focus(_actions[Math.Clamp(action + dx, 0, _actions.Count - 1)]);
        }
        else if (dy < 0)
        {
            Focus(_rows[^1].View);
        }
    }

    void Press()
    {
        View? focused = CurrentFocus;
        if (focused == null || (_rows.All(r => r.View != focused) && !(focused is Button b && _actions.Contains(b))))
        {
            Focus(_rows[0].View);
            return;
        }
        focused.PerformClick();
    }

    public override bool DispatchKeyEvent(KeyEvent? e)
    {
        if (e == null)
        {
            return base.DispatchKeyEvent(e);
        }
        bool down = e.Action == KeyEventActions.Down;
        switch (e.KeyCode)
        {
        case Keycode.DpadUp:
            if (down) Move(0, -1);
            return true;
        case Keycode.DpadDown:
            if (down) Move(0, 1);
            return true;
        case Keycode.DpadLeft:
            if (down) Move(-1, 0);
            return true;
        case Keycode.DpadRight:
            if (down) Move(1, 0);
            return true;
        case Keycode.ButtonA:
        case Keycode.DpadCenter:
        case Keycode.Enter:
        case Keycode.NumpadEnter:
            // a press only (not a held key's repeats, nor the release of the A that opened this screen)
            if (down && e.RepeatCount == 0) Press();
            return true;
        case Keycode.ButtonB:
        case Keycode.Back:
        case Keycode.Escape:
            if (down && e.RepeatCount == 0) Finish();
            return true;
        }
        return base.DispatchKeyEvent(e);
    }

    static int Step(float v, float threshold) => v >= threshold ? 1 : v <= -threshold ? -1 : 0;

    public override bool DispatchGenericMotionEvent(MotionEvent? e)
    {
        if (e != null && e.Source.HasFlag(InputSourceType.Joystick) && e.Action == MotionEventActions.Move)
        {
            // once per push, like the game's menus (GameActivity)
            int hx = Step(e.GetAxisValue(Axis.HatX), 0.5f), hy = Step(e.GetAxisValue(Axis.HatY), 0.5f);
            int sx = Step(e.GetAxisValue(Axis.X), 0.6f), sy = Step(e.GetAxisValue(Axis.Y), 0.6f);
            if (hx != _hatX && hx != 0) Move(hx, 0);
            if (hy != _hatY && hy != 0) Move(0, hy);
            if (sx != _stickX && sx != 0) Move(sx, 0);
            if (sy != _stickY && sy != 0) Move(0, sy);
            _hatX = hx; _hatY = hy; _stickX = sx; _stickY = sy;
            return true;
        }
        return base.DispatchGenericMotionEvent(e);
    }

    public override void OnWindowFocusChanged(bool hasFocus)
    {
        base.OnWindowFocusChanged(hasFocus);
        if (hasFocus) HideSystemUi();
    }

    void HideSystemUi() => Window!.DecorView.SystemUiVisibility = (StatusBarVisibility)(
        SystemUiFlags.ImmersiveSticky | SystemUiFlags.HideNavigation | SystemUiFlags.Fullscreen |
        SystemUiFlags.LayoutStable | SystemUiFlags.LayoutHideNavigation | SystemUiFlags.LayoutFullscreen);
}
