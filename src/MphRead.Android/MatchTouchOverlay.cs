using System;
using Android.App;
using Android.Content;
using Android.Graphics;
using Android.Hardware.Input;
using Android.Util;
using Android.Views;
using Android.Widget;

namespace MphRecomp.App;

// On-screen touch controls for phones without a gamepad. It plays a gamepad: it writes the same PadState the Odin's
// buttons do, so the Prime-style mapping (CampaignRenderer.BuildInput), dialogs, movies and pause work unchanged.
//   left half: a floating stick where the finger lands (move); right half: a floating stick (aim)
//   match (owner 2026-09-30, battle testing): FIRE (= B: shoot, NO in dialogs), JUMP (= A: jump/boost, OK/YES),
//     MORPH (= X), MISSILE (= R1: missiles <-> power beam; reads POWER BEAM while missiles are selected, owner
//     2026-10-04), WEAPON (= Y held: the weapon wheel -- drag from the button towards a weapon and let go, like the DS
//     stylus; owner 2026-10-04: "the touch overlay needs a weapon switcher"), PAUSE (Start); after the match a tap
//     anywhere continues
//   campaign (owner 2026-10-04: "touch overlay should be available for campaign"): the match's six plus
//     VISOR (= D-pad right: scan visor on/off), SCAN (= L2: hold to scan in the scan visor, zoom outside it),
//     MAP (Select: the pause map). In a dialog JUMP reads OK / YES and FIRE reads NO. FIRE / JUMP also skip movies and
//     the take-off; the left stick + JUMP / FIRE answer the ship's save prompt and game-over rows.
// It stands aside (draws nothing, passes taps on) while the pause menu, the pause map or an in-ship menu is open: those
// take their own taps in CampaignActivity.OnTouchEvent.
// Shown per RECOMP SETTINGS "touch controls" (RecompSettings.TouchControls): auto = only while no game controller is
// connected (follows a controller connecting / disconnecting live), on = always, off = never; re-read when the pause
// menu resumes. A match's --es touch auto|on|off overrides the setting. CampaignActivity.OnCreate calls Attach.
internal sealed class MatchTouchOverlay : View, InputManager.IInputDeviceListener
{
    readonly CampaignRenderer _renderer;
    readonly bool _campaign;
    readonly string _settingsPath;
    readonly string? _explicitMode;
    string _mode = "auto";
    readonly Paint _fill = new() { AntiAlias = true };
    readonly Paint _ring = new() { AntiAlias = true, StrokeWidth = 4 };
    readonly Paint _text = new() { AntiAlias = true, TextAlign = Paint.Align.Center, Color = Color.White };

    sealed class Stick { public int Pointer = -1; public float OX, OY, X, Y; }
    readonly Stick _move = new(), _aim = new(), _wheel = new();

    enum Press { Key, Start, Select, Wheel }

    sealed class TouchButton
    {
        public string Label = "";
        public string? OkLabel, YesNoLabel; // the label while a dialog is up (OK-type / YES-NO)
        public PadButton Key;
        public Press Kind;
        public float X, Y, R; // fractions of the view: centre and radius (of the height)
        public int Pointer = -1;
    }
    readonly TouchButton[] _buttons;

    static TouchButton[] MatchLayout() => new TouchButton[]
    {
        new() { Label = "FIRE", Key = PadButton.B, X = 0.90f, Y = 0.72f, R = 0.10f },
        new() { Label = "JUMP", Key = PadButton.A, X = 0.78f, Y = 0.86f, R = 0.08f },
        new() { Label = "MORPH", Key = PadButton.X, X = 0.78f, Y = 0.60f, R = 0.065f },
        new() { Label = "MISSILE", Key = PadButton.R1, X = 0.91f, Y = 0.45f, R = 0.06f },
        new() { Label = "WEAPON", Kind = Press.Wheel, Key = PadButton.Y, X = 0.80f, Y = 0.43f, R = 0.055f },
        new() { Label = "PAUSE", Kind = Press.Start, X = 0.50f, Y = 0.07f, R = 0.045f },
    };

    // right thumb: FIRE / JUMP / MORPH / MISSILE / WEAPON around the bottom-right corner; left thumb: VISOR / SCAN on the
    // left edge, above where the move stick lands (hold SCAN, aim with the right stick); MAP / PAUSE small at the top
    // middle. The reticle and the HUD's middle stay clear.
    static TouchButton[] CampaignLayout() => new TouchButton[]
    {
        new() { Label = "FIRE", YesNoLabel = "NO", Key = PadButton.B, X = 0.90f, Y = 0.74f, R = 0.095f },
        new() { Label = "JUMP", OkLabel = "OK", YesNoLabel = "YES", Key = PadButton.A, X = 0.775f, Y = 0.87f, R = 0.075f },
        new() { Label = "MORPH", Key = PadButton.X, X = 0.775f, Y = 0.625f, R = 0.06f },
        new() { Label = "MISSILE", Key = PadButton.R1, X = 0.925f, Y = 0.50f, R = 0.055f },
        new() { Label = "WEAPON", Kind = Press.Wheel, Key = PadButton.Y, X = 0.82f, Y = 0.41f, R = 0.05f },
        new() { Label = "VISOR", Key = PadButton.DpadUp, X = 0.055f, Y = 0.42f, R = 0.05f }, // the pad's visor key
        new() { Label = "SCAN", Key = PadButton.L2, X = 0.065f, Y = 0.58f, R = 0.06f },
        new() { Label = "MAP", Kind = Press.Select, X = 0.43f, Y = 0.065f, R = 0.042f },
        new() { Label = "PAUSE", Kind = Press.Start, X = 0.57f, Y = 0.065f, R = 0.042f },
    };

    MatchTouchOverlay(Context context, CampaignRenderer renderer, bool campaign, string settingsPath, string? explicitMode)
        : base(context)
    {
        _renderer = renderer;
        _campaign = campaign;
        _settingsPath = settingsPath;
        _explicitMode = explicitMode;
        _buttons = campaign ? CampaignLayout() : MatchLayout();
        _ring.SetStyle(Paint.Style.Stroke);
    }

    // CampaignActivity.OnCreate: on top of everything in the activity's layout (always added; hidden = Gone, so a
    // settings change or a controller leaving can bring it back). A match (--es match / --es net) gets the match layout.
    public static MatchTouchOverlay Attach(Activity activity, FrameLayout root, CampaignRenderer renderer, Intent? intent,
        string settingsPath)
    {
        bool match = !string.IsNullOrEmpty(intent?.GetStringExtra("match")) || !string.IsNullOrEmpty(intent?.GetStringExtra("net"));
        string? explicitMode = match ? intent?.GetStringExtra("touch") : null;
        var overlay = new MatchTouchOverlay(activity, renderer, campaign: !match, settingsPath, explicitMode);
        root.AddView(overlay, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent));
        overlay.ReloadSettings();
        return overlay;
    }

    // UI thread: (re)read the mode -- at start and whenever the pause menu resumes (RECOMP SETTINGS may have changed it)
    public void ReloadSettings()
    {
        string mode = (_explicitMode ?? LoadSetting()).ToLowerInvariant();
        _mode = mode is "on" or "off" ? mode : "auto";
        ApplyMode();
    }

    string LoadSetting()
    {
        try
        {
            return MphRecomp.Config.RecompSettings.Load(_settingsPath).TouchControls ?? "auto";
        }
        catch (Exception)
        {
            return "auto";
        }
    }

    void ApplyMode()
    {
        bool show = _mode == "on" || (_mode == "auto" && !HasGameController());
        if (show == (Visibility == ViewStates.Visible))
        {
            return;
        }
        if (!show)
        {
            ReleaseAll();
        }
        Visibility = show ? ViewStates.Visible : ViewStates.Gone;
        Log.Info("MPHTouch", $"touch controls ({(_campaign ? "campaign" : "match")}, {_mode}): {(show ? "shown" : "hidden")}");
    }

    // true when a real game controller is plugged in or paired (the Odin's built-in pad counts)
    public static bool HasGameController()
    {
        foreach (int id in InputDevice.GetDeviceIds() ?? Array.Empty<int>())
        {
            InputDevice? d = InputDevice.GetDevice(id);
            if (d != null && !d.IsVirtual && (d.Sources.HasFlag(InputSourceType.Gamepad) || d.Sources.HasFlag(InputSourceType.Joystick)))
            {
                return true;
            }
        }
        return false;
    }

    // "auto" follows controllers connecting / disconnecting while the game runs (callbacks on the UI thread)
    protected override void OnAttachedToWindow()
    {
        base.OnAttachedToWindow();
        (Context?.GetSystemService(Android.Content.Context.InputService) as InputManager)?.RegisterInputDeviceListener(this, null);
    }

    protected override void OnDetachedFromWindow()
    {
        (Context?.GetSystemService(Android.Content.Context.InputService) as InputManager)?.UnregisterInputDeviceListener(this);
        base.OnDetachedFromWindow();
    }

    public void OnInputDeviceAdded(int deviceId) => DevicesChanged();
    public void OnInputDeviceChanged(int deviceId) => DevicesChanged();
    public void OnInputDeviceRemoved(int deviceId) => DevicesChanged();

    void DevicesChanged()
    {
        if (_mode == "auto")
        {
            ApplyMode();
        }
    }

    float StickRadius => Height * 0.14f;

    // the pause menu, the pause map and the in-ship menus (menu, briefing, planet select) take their own taps
    bool MenuUp => _renderer.PauseMenuOpen || _renderer.ShipMenuOpen || _renderer.MapOpen;

    public override bool OnTouchEvent(MotionEvent? e)
    {
        if (e == null)
        {
            return false;
        }
        // a new touch goes to the activity (CampaignActivity.OnTouchEvent). The rest of a gesture this view already took
        // (the finger that pressed MAP / PAUSE, a stick held while the ship opened) stays here, so its lift doesn't land
        // in the menu as a stray tap.
        if (MenuUp)
        {
            ReleaseAll();
            return e.ActionMasked != MotionEventActions.Down;
        }
        if (_renderer.MatchOver)
        {
            if (e.ActionMasked == MotionEventActions.Down)
            {
                _renderer.RequestMatchContinue();
            }
            return true;
        }
        int index = e.ActionIndex;
        int pointer = e.GetPointerId(index);
        switch (e.ActionMasked)
        {
        case MotionEventActions.Down:
        case MotionEventActions.PointerDown:
            Down(pointer, e.GetX(index), e.GetY(index));
            break;
        case MotionEventActions.Move:
            for (int i = 0; i < e.PointerCount; i++)
            {
                Moved(e.GetPointerId(i), e.GetX(i), e.GetY(i));
            }
            break;
        case MotionEventActions.Up:
        case MotionEventActions.PointerUp:
            Up(pointer);
            break;
        case MotionEventActions.Cancel:
            ReleaseAll();
            break;
        }
        Invalidate();
        return true;
    }

    void Down(int pointer, float x, float y)
    {
        foreach (TouchButton b in _buttons)
        {
            float r = b.R * Height * 1.15f; // a little slack around the drawn circle
            if (b.Pointer == -1 && Sq(x - b.X * Width) + Sq(y - b.Y * Height) <= r * r)
            {
                b.Pointer = pointer;
                switch (b.Kind)
                {
                case Press.Start:
                    _renderer.StartPressed();
                    break;
                case Press.Select:
                    _renderer.SelectPressed();
                    break;
                case Press.Wheel:
                    // the wheel stick starts where the finger lands; the right stick follows it while Y is held
                    _wheel.Pointer = pointer;
                    _wheel.OX = _wheel.X = x;
                    _wheel.OY = _wheel.Y = y;
                    UpdateSticks();
                    _renderer.Pad.SetTouch(b.Key, true);
                    break;
                default:
                    _renderer.Pad.SetTouch(b.Key, true);
                    break;
                }
                return;
            }
        }
        Stick s = x < Width / 2f ? _move : _aim;
        if (s.Pointer == -1)
        {
            s.Pointer = pointer;
            s.OX = s.X = x;
            s.OY = s.Y = y;
            UpdateSticks();
        }
    }

    void Moved(int pointer, float x, float y)
    {
        foreach (Stick s in new[] { _move, _aim, _wheel })
        {
            if (s.Pointer == pointer)
            {
                s.X = x;
                s.Y = y;
            }
        }
        UpdateSticks();
    }

    void Up(int pointer)
    {
        foreach (TouchButton b in _buttons)
        {
            if (b.Pointer == pointer)
            {
                b.Pointer = -1;
                if (b.Kind is Press.Key or Press.Wheel)
                {
                    // Y goes up before the wheel stick recentres, as a gamepad's would (the wheel keeps its pick)
                    _renderer.Pad.SetTouch(b.Key, false);
                }
            }
        }
        foreach (Stick s in new[] { _move, _aim, _wheel })
        {
            if (s.Pointer == pointer)
            {
                s.Pointer = -1;
            }
        }
        UpdateSticks();
    }

    void ReleaseAll()
    {
        foreach (TouchButton b in _buttons)
        {
            if (b.Pointer != -1 && b.Kind is Press.Key or Press.Wheel)
            {
                _renderer.Pad.SetTouch(b.Key, false);
            }
            b.Pointer = -1;
        }
        _move.Pointer = _aim.Pointer = _wheel.Pointer = -1;
        UpdateSticks();
        Invalidate();
    }

    // a stick's deflection, -1..1 per axis, in the gamepad's convention (down = +y)
    (float X, float Y) Deflection(Stick s)
    {
        if (s.Pointer == -1)
        {
            return (0, 0);
        }
        float dx = (s.X - s.OX) / StickRadius, dy = (s.Y - s.OY) / StickRadius;
        float len = MathF.Sqrt(dx * dx + dy * dy);
        if (len > 1)
        {
            dx /= len;
            dy /= len;
        }
        return (dx, dy);
    }

    void UpdateSticks()
    {
        PadState pad = _renderer.Pad;
        (pad.Lx, pad.Ly) = Deflection(_move);
        (pad.Rx, pad.Ry) = Deflection(_wheel.Pointer != -1 ? _wheel : _aim);
    }

    protected override void OnDraw(Canvas canvas)
    {
        base.OnDraw(canvas);
        PostInvalidateDelayed(250); // menus / dialogs / the results screen come and go without a touch here
        if (MenuUp || _renderer.MatchOver)
        {
            return;
        }
        int dialog = _campaign ? _renderer.TouchDialog : 0;
        bool missile = _renderer.TouchMissileSelected;
        float textSize = Height * 0.03f;
        foreach (TouchButton b in _buttons)
        {
            bool down = b.Pointer != -1;
            float cx = b.X * Width, cy = b.Y * Height, r = b.R * Height;
            _fill.Color = Color.Argb(down ? 110 : 55, 255, 255, 255);
            canvas.DrawCircle(cx, cy, r, _fill);
            _ring.Color = Color.Argb(150, 255, 255, 255);
            canvas.DrawCircle(cx, cy, r, _ring);
            string label = (dialog == 2 ? b.YesNoLabel : dialog == 1 ? b.OkLabel : null)
                ?? (b.Key == PadButton.R1 && missile ? "POWER\nBEAM" : b.Label); // what R1 switches to
            string[] lines = label.Split('\n');
            _text.TextSize = textSize;
            float w = 0;
            foreach (string line in lines) w = MathF.Max(w, _text.MeasureText(line));
            if (w > r * 1.7f)
            {
                _text.TextSize = textSize * r * 1.7f / w; // long labels shrink to fit their circle
            }
            float lineHeight = _text.TextSize * 1.05f;
            float top = cy + _text.TextSize / 3 - lineHeight * (lines.Length - 1) / 2;
            for (int i = 0; i < lines.Length; i++)
            {
                canvas.DrawText(lines[i], cx, top + i * lineHeight, _text);
            }
        }
        foreach (Stick s in new[] { _move, _aim, _wheel })
        {
            if (s.Pointer == -1)
            {
                continue;
            }
            _ring.Color = Color.Argb(120, 255, 255, 255);
            canvas.DrawCircle(s.OX, s.OY, StickRadius, _ring);
            (float dx, float dy) = Deflection(s);
            _fill.Color = Color.Argb(110, 255, 255, 255);
            canvas.DrawCircle(s.OX + dx * StickRadius, s.OY + dy * StickRadius, StickRadius * 0.4f, _fill);
        }
    }

    static float Sq(float v) => v * v;
}
