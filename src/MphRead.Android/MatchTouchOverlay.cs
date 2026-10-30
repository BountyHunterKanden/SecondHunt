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
//     anywhere continues. While the player flies the Trooper's Scout Drone (owner 2026-10-08 / 2026-10-11) the buttons turn
//     into the drone's (the drone functions' default buttons; MORPH stays morph): FIRE reads BURST, JUMP RISE, MORPH RECALL, LASER (= L2)
//     shows, and the hunter-only ones (MISSILE, WEAPON, OMEGA, VISOR, SCAN) hide until it comes back
//   campaign (owner 2026-10-04: "touch overlay should be available for campaign"): the match's six plus
//     VISOR (= D-pad right: scan visor on/off), SCAN (= Y, L2 until 2026-10-09: hold to scan in the scan visor, zoom outside it),
//     MAP (Select: the pause map). In a dialog JUMP reads OK / YES and FIRE reads NO. FIRE / JUMP also skip movies and
//     the take-off; the left stick + JUMP / FIRE answer the ship's save prompt and game-over rows.
// The right half's touch area is also the DS touch screen (T11, owner 2026-10-10 N19 / N20: "morph ball touch control
// exactly as vanilla DS does it", "double tap to jump ... like vanilla for touch only users"): its finger is the stylus
// (PadState.StylusDown/Move/Up, in DS pixels by physical size), and MphRead plays vanilla's stylus rules on it
// (PlayerTouch.cs): a double tap jumps (JUMP stays), and in Samus's / Kanden's / Spire's / Noxus's morph ball a drag rolls
// the ball and a fast flick boosts, so there the finger is the stylus only (no aim stick; drawn as a dot).
// RECOMP SETTINGS "touch aim" (owner 2026-10-16: "the original vanilla stylus aiming as an option ... to replace virtual
// right stick"): STYLUS makes that finger the stylus everywhere: its drag turns the view by the game's own stylus rule
// (PadState.StylusAim -> CampaignRenderer.StylusAimDelta), with no stick; STICK (default) keeps the floating stick.
// "aim while firing" (thread D 10-24, default on): the thumb holding FIRE is also a floating stick from where it pressed
// (past a small slack ring, so a tap's wobble doesn't turn), added to the aim finger's; with either "touch aim".
// It stands aside (draws nothing, passes taps on) while the pause menu, the pause map or an in-ship menu is open: those
// take their own taps in CampaignActivity.OnTouchEvent.
// Shown per RECOMP SETTINGS "touch controls" (RecompSettings.TouchControls): auto = only while no game controller is
// connected (follows a controller connecting / disconnecting live), on = always, aim only = always but only the aim finger (no
// buttons, no move stick: the whole screen aims, for a controller player aiming by touch), off = never; re-read when the pause
// menu resumes. A match's --es touch auto|on|off overrides the setting. CampaignActivity.OnCreate calls Attach.
internal sealed class MatchTouchOverlay : View, InputManager.IInputDeviceListener
{
    readonly CampaignRenderer _renderer;
    readonly bool _campaign;
    readonly string _settingsPath;
    readonly string? _explicitMode;
    string _mode = "auto";
    bool _stylusAimSetting; // RECOMP SETTINGS "touch aim": stylus
    bool _fireAimSetting = true; // RECOMP SETTINGS "aim while firing" (thread D 10-24)
    readonly Paint _fill = new() { AntiAlias = true };
    readonly Paint _ring = new() { AntiAlias = true, StrokeWidth = 4 };
    readonly Paint _text = new() { AntiAlias = true, TextAlign = Paint.Align.Center, Color = Color.White };

    sealed class Stick { public int Pointer = -1; public float OX, OY, X, Y; }
    readonly Stick _move = new(), _aim = new(), _wheel = new();
    // AIM WHILE FIRING (thread D 10-24, owner: "while holding the on-screen fire button to charge, sliding that thumb aims
    // like a stick"): the thumb on FIRE is a floating stick from where it pressed; a slack ring keeps a tap's wobble still
    readonly Stick _fire = new();
    const float FireSlack = 0.15f;

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
        public bool HunterOnly; // hidden while the drone flies (owner 2026-10-11: the buttons turn into the drone's)
        public string? DroneLabel; // the label while the drone flies (its button is the drone function's default)
        public bool VoiceOnly; // shown only in an online game with voice chat in push-to-talk mode (O8)
        public bool ChatOnly; // shown only in an online game (O7: the quick-message wheel)
        public bool OmegaOnly; // shown only while the Omega Cannon waits in the third slot (T4 N15: vanilla's touch icon)
    }
    readonly TouchButton[] _buttons;

    static TouchButton[] MatchLayout() => new TouchButton[]
    {
        new() { Label = "FIRE", DroneLabel = "BURST", Key = PadButton.B, X = 0.90f, Y = 0.72f, R = 0.10f },
        new() { Label = "JUMP", DroneLabel = "RISE", Key = PadButton.A, X = 0.78f, Y = 0.86f, R = 0.08f },
        new() { Label = "MORPH", DroneLabel = "RECALL", Key = PadButton.X, X = 0.78f, Y = 0.60f, R = 0.065f },
        new() { Label = "MISSILE", HunterOnly = true, Key = PadButton.R1, X = 0.91f, Y = 0.45f, R = 0.06f },
        new() { Label = "WEAPON", HunterOnly = true, Kind = Press.Wheel, Key = PadButton.L2, X = 0.80f, Y = 0.43f, R = 0.055f },
        new() { Label = "OMEGA", HunterOnly = true, Kind = Press.Omega, OmegaOnly = true, X = 0.855f, Y = 0.29f, R = 0.05f },
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
        new() { Label = "FIRE", DroneLabel = "BURST", YesNoLabel = "NO", Key = PadButton.B, X = 0.90f, Y = 0.74f, R = 0.095f },
        new() { Label = "JUMP", DroneLabel = "RISE", OkLabel = "OK", YesNoLabel = "YES", Key = PadButton.A, X = 0.775f, Y = 0.87f, R = 0.075f },
        new() { Label = "MORPH", DroneLabel = "RECALL", Key = PadButton.X, X = 0.775f, Y = 0.625f, R = 0.06f },
        new() { Label = "MISSILE", HunterOnly = true, Key = PadButton.R1, X = 0.925f, Y = 0.50f, R = 0.055f },
        new() { Label = "WEAPON", HunterOnly = true, Kind = Press.Wheel, Key = PadButton.L2, X = 0.82f, Y = 0.41f, R = 0.05f },
        new() { Label = "VISOR", HunterOnly = true, Key = PadButton.DpadUp, X = 0.055f, Y = 0.42f, R = 0.05f }, // the pad's visor key
        new() { Label = "SCAN", HunterOnly = true, Key = PadButton.Y, X = 0.065f, Y = 0.58f, R = 0.06f },
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
        MphRecomp.Config.RecompSettings? settings = LoadSettings();
        string mode = (_explicitMode ?? settings?.TouchControls ?? "auto").ToLowerInvariant();
        _mode = mode is "on" or "aim only" or "off" ? mode : "auto";
        _stylusAimSetting = string.Equals(settings?.TouchAim, "stylus", StringComparison.OrdinalIgnoreCase);
        _fireAimSetting = settings?.TouchFireAim ?? true;
        StylusOnly(); // (a change re-centres the aim stick under a finger that is down)
        ApplyMode();
    }

    MphRecomp.Config.RecompSettings? LoadSettings()
    {
        try
        {
            return MphRecomp.Config.RecompSettings.Load(_settingsPath);
        }
        catch (Exception)
        {
            return null;
        }
    }

    void ApplyMode()
    {
        // a replay has nothing to steer: no touch controls (a tap pauses it, CampaignActivity.OnTouchEvent)
        bool show = !_renderer.ReplayPlaying && (_mode is "on" or "aim only" || (_mode == "auto" && !HasGameController()));
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

    // the aim finger is the stylus only (the morph ball's touch roll, or TOUCH AIM: STYLUS); a change re-centres the aim
    // stick under the finger
    bool _stylusOnly;
    bool StylusOnly()
    {
        bool only = _stylusAimSetting || _renderer.TouchStylusRolls;
        if (only != _stylusOnly)
        {
            _stylusOnly = only;
            _aim.OX = _aim.X;
            _aim.OY = _aim.Y;
        }
        return only;
    }

    // the aim finger's drag turns the view (TOUCH AIM: STYLUS): not while it rolls the morph ball (that drag rolls instead)
    bool StylusAims => _stylusAimSetting && !_renderer.TouchStylusRolls;

    // TOUCH CONTROLS: AIM ONLY: no buttons and no move stick, any finger on the screen is the aim finger
    bool AimOnly => _mode == "aim only";

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
        // the text panel's rows (the ship's save prompt, game over) and the gun-adjust pause's bot switch lines take a first
        // finger that misses the buttons: it goes on to CampaignActivity.OnTouchEvent (ShipPanelTap, BotSwitchTap)
        if (e.ActionMasked == MotionEventActions.Down && (_campaign && _renderer.ShipPanelUp || _renderer.GunPauseOpen))
        {
            bool drone = _renderer.TouchDrone, onButton = false;
            foreach (TouchButton b in _buttons)
            {
                onButton |= Hits(b, e.GetX(), e.GetY(), drone);
            }
            if (!onButton)
            {
                return false;
            }
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
        // a dialog's own OK / YES / NO / page arrows take the tap first (CampaignRenderer.DialogTouch.cs); the finger then
        // holds nothing, so its moves and lift do nothing
        if (_campaign && _renderer.TouchDialog != 0 && _renderer.DialogTouch(x, y))
        {
            return;
        }
        // with the weapon ring open, a tap on it picks a weapon (CampaignRenderer.WeaponRingTouch.cs), in AIM ONLY too
        if (_renderer.RingTouch(x, y))
        {
            return;
        }
        bool drone = _renderer.TouchDrone;
        foreach (TouchButton b in _buttons)
        {
            if (b.Pointer == -1 && Hits(b, x, y, drone))
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
                    if (b.Key == PadButton.B && _fireAimSetting)
                    {
                        _fire.Pointer = pointer;
                        _fire.OX = _fire.X = x;
                        _fire.OY = _fire.Y = y;
                    }
                    break;
                }
                return;
            }
        }
        Stick s = x < Width / 2f && !AimOnly ? _move : _aim;
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

    // a shown button under (x, y), with a little slack around the drawn circle
    bool Hits(TouchButton b, float x, float y, bool drone)
    {
        if (AimOnly || b.DroneOnly && !drone || b.HunterOnly && drone || b.VoiceOnly && !VoiceTalk || b.ChatOnly && !_renderer.TouchChat
            || b.OmegaOnly && !_renderer.TouchOmega)
        {
            return false;
        }
        float r = b.R * Height * 1.15f;
        return Sq(x - b.X * Width) + Sq(y - b.Y * Height) <= r * r;
    }

    void Moved(int pointer, float x, float y)
    {
        foreach (Stick s in new[] { _move, _aim, _wheel, _fire })
        {
            if (s.Pointer == pointer)
            {
                if (s == _aim)
                {
                    float dsX = (x - s.X) * DsPerPixel, dsY = (y - s.Y) * DsPerPixel;
                    _renderer.Pad.StylusMove(dsX, dsY);
                    if (StylusAims && _wheel.Pointer == -1)
                    {
                        _renderer.Pad.StylusAim(dsX, dsY); // (while the weapon ring is up the view holds, as with the stick)
                    }
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
        foreach (Stick s in new[] { _move, _aim, _wheel, _fire })
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
        _move.Pointer = _aim.Pointer = _wheel.Pointer = _fire.Pointer = -1;
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
        // (in the rolling morph ball, or with TOUCH AIM: STYLUS, the aim finger is the stylus: no right stick from it). The
        // touch sticks have their own slots, so a controller's sticks keep working alongside them.
        (float lx, float ly) = Deflection(_move);
        (float rx, float ry) = _wheel.Pointer != -1 ? Deflection(_wheel) : StylusOnly() ? (0, 0) : Deflection(_aim);
        if (_wheel.Pointer == -1 && _fire.Pointer != -1)
        {
            // the FIRE thumb adds its stick to the aim finger's (both at once stays within full tilt)
            (float fx, float fy) = FireDeflection();
            rx += fx;
            ry += fy;
            float len = MathF.Sqrt(rx * rx + ry * ry);
            if (len > 1)
            {
                rx /= len;
                ry /= len;
            }
        }
        _renderer.Pad.SetTouchSticks(lx, ly, rx, ry);
    }

    // the FIRE thumb's stick: nothing inside the slack ring (a tap fires as before), full range outside it
    (float X, float Y) FireDeflection()
    {
        (float dx, float dy) = Deflection(_fire);
        float len = MathF.Sqrt(dx * dx + dy * dy);
        if (len <= FireSlack)
        {
            return (0, 0);
        }
        float k = (len - FireSlack) / (1 - FireSlack) / len;
        return (dx * k, dy * k);
    }

    protected override void OnDraw(Canvas canvas)
    {
        base.OnDraw(canvas);
        PostInvalidateDelayed(250); // menus / dialogs / the results screen come and go without a touch here
        if (_stylusOnly != (_stylusAimSetting || _renderer.TouchStylusRolls))
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
        bool zoom = _renderer.TouchCanZoom; // the Imperialist: WEAPON's quick press zooms (CampaignRenderer.BuildInput)
        float textSize = Height * 0.03f;
        foreach (TouchButton b in _buttons)
        {
            if ((AimOnly || b.DroneOnly && !drone || b.HunterOnly && drone || b.VoiceOnly && !VoiceTalk || b.ChatOnly && !_renderer.TouchChat
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
                    : b.Kind == Press.Wheel && zoom && !drone ? "WEAPON/\nZOOM"
                    : drone && b.DroneLabel != null ? b.DroneLabel : b.Label); // the drone's: BURST, RISE, RECALL
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
        foreach (Stick s in new[] { _move, _aim, _wheel, _fire })
        {
            if (s.Pointer == -1 || s == _fire && FireDeflection() == (0, 0))
            {
                continue;
            }
            if (s == _aim && _wheel.Pointer == -1 && StylusOnly())
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
