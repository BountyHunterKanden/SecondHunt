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
//     2026-10-04), WEAPON (= L2 held, Y until 2026-10-09: the weapon ring -- drag from the button towards a weapon and let go, like the DS
//     stylus; owner 2026-10-04: "the touch overlay needs a weapon switcher"), PAUSE (Start); after the match a tap
//     anywhere continues. While the player flies the Trooper's Scout Drone (owner 2026-10-08): FIRE reads BURST and
//     LASER (= L2, the weapon ring's button, which is the laser in drone form) shows
//   campaign (owner 2026-10-04: "touch overlay should be available for campaign"): the match's six plus
//     VISOR (= D-pad right: scan visor on/off), SCAN (= Y, L2 until 2026-10-09: hold to scan in the scan visor, zoom outside it),
//     MAP (Select: the pause map). In a dialog JUMP reads OK / YES and FIRE reads NO. FIRE / JUMP also skip movies and
//     the take-off; the left stick + JUMP / FIRE answer the ship's save prompt and game-over rows.
// The right half's touch area is also the DS touch screen (T11, owner 2026-10-10 N19 / N20: "morph ball touch control
// exactly as vanilla DS does it", "double tap to jump ... like vanilla for touch only users"): its finger is the stylus
// (PadState.StylusDown/Move/Up, in DS pixels by physical size), and MphRead plays vanilla's stylus rules on it
// (PlayerTouch.cs): a double tap jumps (JUMP stays), and in Samus's / Kanden's / Spire's / Noxus's morph ball a drag rolls
// the ball and a fast flick boosts, so there the finger is the stylus only (no aim stick; drawn as a dot).
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

    enum Press { Key, Start, Select, Wheel, Omega }

    sealed class TouchButton
    {
        public string Label = "";
        public string? OkLabel, YesNoLabel; // the label while a dialog is up (OK-type / YES-NO)
        public PadButton Key;
        public Press Kind;
        public float X, Y, R; // fractions of the view: centre and radius (of the height)
        public int Pointer = -1;
        public bool DroneOnly; // shown (and pressable) only while the player flies the Trooper's Scout Drone
        public bool VoiceOnly; // shown only in an online game with voice chat in push-to-talk mode (O8)
        public bool ChatOnly; // shown only in an online game (O7: the quick-message wheel)
        public bool OmegaOnly; // shown only while the Omega Cannon waits in the third slot (T4 N15: vanilla's touch icon)
    }
    readonly TouchButton[] _buttons;

    static TouchButton[] MatchLayout() => new TouchButton[]
    {
        new() { Label = "FIRE", Key = PadButton.B, X = 0.90f, Y = 0.72f, R = 0.10f },
        new() { Label = "JUMP", Key = PadButton.A, X = 0.78f, Y = 0.86f, R = 0.08f },
        new() { Label = "MORPH", Key = PadButton.X, X = 0.78f, Y = 0.60f, R = 0.065f },
        new() { Label = "MISSILE", Key = PadButton.R1, X = 0.91f, Y = 0.45f, R = 0.06f },
        new() { Label = "WEAPON", Kind = Press.Wheel, Key = PadButton.L2, X = 0.80f, Y = 0.43f, R = 0.055f },
        new() { Label = "OMEGA", Kind = Press.Omega, OmegaOnly = true, X = 0.855f, Y = 0.29f, R = 0.05f },
        new() { Label = "PAUSE", Kind = Press.Start, X = 0.50f, Y = 0.07f, R = 0.045f },
        // the Trooper's Scout Drone (owner 2026-10-08): its cutting laser, the pad's L2 (zoom, which the drone has no use
        // for); left of JUMP / MORPH, clear of FIRE (which reads BURST meanwhile)
        new() { Label = "LASER", Key = PadButton.L2, DroneOnly = true, X = 0.665f, Y = 0.73f, R = 0.07f },
        // online voice chat (O8): hold to talk = the pad's R3 (CampaignRenderer.BuildInput passes it on); top left of PAUSE
        new() { Label = "TALK", Key = PadButton.ThumbR, VoiceOnly = true, X = 0.36f, Y = 0.07f, R = 0.045f },
        // online chat (O7, MatchChat.cs): drag from it towards a quick message and let go = the pad's Select held + right stick
        new() { Label = "CHAT", Kind = Press.Wheel, Key = PadButton.Select, ChatOnly = true, X = 0.64f, Y = 0.07f, R = 0.045f },
    };

    static bool VoiceTalk => MphRecomp.Online.Voice.VoiceChat.Current != null
        && MphRecomp.Online.Voice.VoiceSettings.Current.Mode == MphRecomp.Online.Voice.VoiceMode.PushToTalk;

    // right thumb: FIRE / JUMP / MORPH / MISSILE / WEAPON around the bottom-right corner; left thumb: VISOR / SCAN on the
    // left edge, above where the move stick lands (hold SCAN, aim with the right stick); MAP / PAUSE small at the top
    // middle. The reticle and the HUD's middle stay clear.
    static TouchButton[] CampaignLayout() => new TouchButton[]
    {
        new() { Label = "FIRE", YesNoLabel = "NO", Key = PadButton.B, X = 0.90f, Y = 0.74f, R = 0.095f },
        new() { Label = "JUMP", OkLabel = "OK", YesNoLabel = "YES", Key = PadButton.A, X = 0.775f, Y = 0.87f, R = 0.075f },
        new() { Label = "MORPH", Key = PadButton.X, X = 0.775f, Y = 0.625f, R = 0.06f },
        new() { Label = "MISSILE", Key = PadButton.R1, X = 0.925f, Y = 0.50f, R = 0.055f },
        new() { Label = "WEAPON", Kind = Press.Wheel, Key = PadButton.L2, X = 0.82f, Y = 0.41f, R = 0.05f },
        new() { Label = "VISOR", Key = PadButton.DpadUp, X = 0.055f, Y = 0.42f, R = 0.05f }, // the pad's visor key
        new() { Label = "SCAN", Key = PadButton.Y, X = 0.065f, Y = 0.58f, R = 0.06f },
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
        // "setup": a vanilla multiplayer menu match on this device (bots / local; MatchSetupText.LaunchArgs) -- no VISOR / SCAN
        bool match = !string.IsNullOrEmpty(intent?.GetStringExtra("match")) || !string.IsNullOrEmpty(intent?.GetStringExtra("net"))
            || !string.IsNullOrEmpty(intent?.GetStringExtra(MphRecomp.Frontend.MatchSetupText.ArgName));
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
        // a replay has nothing to steer: no touch controls (a tap pauses it, CampaignActivity.OnTouchEvent)
        bool show = !_renderer.ReplayPlaying && (_mode == "on" || (_mode == "auto" && !HasGameController()));
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

    // DS touch-screen pixels per screen pixel: the DS's bottom screen is 256 px over 61.4 mm, so a drag of the same length
    // on the phone moves the stylus as far as on the DS (xdpi; devices that report nonsense fall back to their density)
    float _dsPerPixel;
    float DsPerPixel
    {
        get
        {
            if (_dsPerPixel == 0)
            {
                DisplayMetrics? m = Resources?.DisplayMetrics;
                float dpi = m == null ? 160 : m.Xdpi >= 100 && m.Xdpi <= 1200 ? m.Xdpi : (float)m.DensityDpi;
                _dsPerPixel = 256 / 61.4f * 25.4f / dpi;
            }
            return _dsPerPixel;
        }
    }

    // the aim finger is the stylus only (the morph ball's touch roll); a change re-centres the aim stick under the finger
    bool _stylusRolls;
    bool StylusRolls()
    {
        bool rolls = _renderer.TouchStylusRolls;
        if (rolls != _stylusRolls)
        {
            _stylusRolls = rolls;
            _aim.OX = _aim.X;
            _aim.OY = _aim.Y;
        }
        return rolls;
    }

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
        bool drone = _renderer.TouchDrone;
        foreach (TouchButton b in _buttons)
        {
            float r = b.R * Height * 1.15f; // a little slack around the drawn circle
            if (b.DroneOnly && !drone || b.VoiceOnly && !VoiceTalk || b.ChatOnly && !_renderer.TouchChat
                || b.OmegaOnly && !_renderer.TouchOmega)
            {
                continue;
            }
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
                case Press.Omega:
                    _renderer.TouchOmegaPressed();
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
            if (s == _aim)
            {
                _renderer.Pad.StylusDown();
            }
            UpdateSticks();
        }
    }

    void Moved(int pointer, float x, float y)
    {
        foreach (Stick s in new[] { _move, _aim, _wheel })
        {
            if (s.Pointer == pointer)
            {
                if (s == _aim)
                {
                    _renderer.Pad.StylusMove((x - s.X) * DsPerPixel, (y - s.Y) * DsPerPixel);
                }
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
                    // the WEAPON button (L2) goes up before the wheel stick recentres, as a gamepad's would (the wheel keeps its pick)
                    _renderer.Pad.SetTouch(b.Key, false);
                }
            }
        }
        foreach (Stick s in new[] { _move, _aim, _wheel })
        {
            if (s.Pointer == pointer)
            {
                s.Pointer = -1;
                if (s == _aim)
                {
                    _renderer.Pad.StylusUp();
                }
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
        _renderer.Pad.StylusUp();
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
        // (in the rolling morph ball the aim finger is the stylus: no right stick from it)
        (pad.Rx, pad.Ry) = _wheel.Pointer != -1 ? Deflection(_wheel) : StylusRolls() ? (0, 0) : Deflection(_aim);
    }

    protected override void OnDraw(Canvas canvas)
    {
        base.OnDraw(canvas);
        PostInvalidateDelayed(250); // menus / dialogs / the results screen come and go without a touch here
        if (_stylusRolls != _renderer.TouchStylusRolls)
        {
            UpdateSticks(); // morphed / unmorphed with the finger still: the aim stick goes or comes back now
        }
        if (MenuUp || _renderer.MatchOver)
        {
            return;
        }
        int dialog = _campaign ? _renderer.TouchDialog : 0;
        bool missile = _renderer.TouchMissileSelected;
        bool drone = _renderer.TouchDrone;
        float textSize = Height * 0.03f;
        foreach (TouchButton b in _buttons)
        {
            if ((b.DroneOnly && !drone || b.VoiceOnly && !VoiceTalk || b.ChatOnly && !_renderer.TouchChat
                || b.OmegaOnly && !_renderer.TouchOmega) && b.Pointer == -1)
            {
                continue;
            }
            bool down = b.Pointer != -1;
            float cx = b.X * Width, cy = b.Y * Height, r = b.R * Height;
            _fill.Color = Color.Argb(down ? 110 : 55, 255, 255, 255);
            canvas.DrawCircle(cx, cy, r, _fill);
            _ring.Color = Color.Argb(150, 255, 255, 255);
            canvas.DrawCircle(cx, cy, r, _ring);
            string label = (dialog == 2 ? b.YesNoLabel : dialog == 1 ? b.OkLabel : null)
                ?? (b.Key == PadButton.R1 && missile ? "POWER\nBEAM" // what R1 switches to
                    : b.Key == PadButton.B && drone ? "BURST" : b.Label); // the drone's blasters
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
            if (s == _aim && _wheel.Pointer == -1 && StylusRolls())
            {
                // the stylus: a dot under the finger, no stick
                _fill.Color = Color.Argb(110, 255, 255, 255);
                canvas.DrawCircle(s.X, s.Y, StickRadius * 0.3f, _fill);
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
