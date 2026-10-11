namespace MphRecomp.App;

// How a game screen was asked to start, as named string values: Android passes its activity's intent extras (so the
// dev `am start --es room ...` shortcuts keep working), Windows the menus' requests and its command line. The names
// are the ones the screens document (CampaignRenderer.Create: room, hunter, save, slot, match, net, ...).
public sealed class LaunchArgs
{
    readonly Func<string, string?> _get;

    public LaunchArgs(Func<string, string?> get)
    {
        _get = get;
    }

    public LaunchArgs(IReadOnlyDictionary<string, string> values)
    {
        _get = key => values.TryGetValue(key, out string? value) ? value : null;
    }

    public string? Get(string key) => _get(key);

    public static readonly LaunchArgs Empty = new(_ => null);
}

// A game controller's buttons, by position (Xbox naming of the face buttons on the Odin's layout: A bottom, B right,
// X left, Y top). The hosts map their own key codes onto these.
public enum PadButton
{
    A, B, X, Y, L1, R1, L2, R2, ThumbL, ThumbR, DpadUp, DpadDown, DpadLeft, DpadRight, Start, Select, Count
}

// Raw controller state: written by the host's input thread, read on the GL thread once per simulation step.
public sealed class PadState
{
    public volatile float Lx, Ly, Rx, Ry, L2, R2, HatX, HatY;
    public volatile float RawRx, RawRy; // the right stick before the dead zone (input trace)
    readonly bool[] _keys = new bool[(int)PadButton.Count];
    readonly bool[] _touch = new bool[(int)PadButton.Count];

    // the controller's button, or the touch controls' stand-in for it
    public bool this[PadButton button] => _keys[(int)button] || _touch[(int)button];

    public void Set(PadButton button, bool down) => _keys[(int)button] = down;

    // the controller alone / the touch controls alone (MatchTouchOverlay.cs): the control customizer moves the
    // controller's buttons, a touch button always does its default function (CampaignRenderer's PadBindings)
    public bool Physical(PadButton button) => _keys[(int)button];
    public bool Touch(PadButton button) => _touch[(int)button];
    public void SetTouch(PadButton button, bool down) => _touch[(int)button] = down;

    // pointer aim (the Windows mouse) in MphRead's mouse units, added to the sticks' aim at the next simulation step
    float _aimX, _aimY;
    readonly object _aimGate = new();

    public void AddAim(float x, float y)
    {
        lock (_aimGate)
        {
            _aimX += x;
            _aimY += y;
        }
    }

    public System.Numerics.Vector2 TakeAim()
    {
        lock (_aimGate)
        {
            var aim = new System.Numerics.Vector2(_aimX, _aimY);
            _aimX = _aimY = 0;
            return aim;
        }
    }

    // the touch controls' stylus (MatchTouchOverlay.cs, T11): a finger on the touch area and how far it moved, in DS
    // touch-screen pixels; the next simulation step takes it (CampaignRenderer.BuildInput -> CampaignInput.TouchDown /
    // TouchDx / TouchDy, PlayerTouch.cs). A touch that starts and ends between two steps still counts as down for one,
    // and the step after it sees it up (the game's double tap needs both). Whole pixels go; the rest waits.
    bool _stylusDown, _stylusSeen;
    float _stylusX, _stylusY;

    public void StylusDown()
    {
        lock (_aimGate)
        {
            _stylusDown = _stylusSeen = true;
        }
    }

    public void StylusMove(float dsX, float dsY)
    {
        lock (_aimGate)
        {
            if (_stylusDown)
            {
                _stylusX += dsX;
                _stylusY += dsY;
            }
        }
    }

    public void StylusUp()
    {
        lock (_aimGate)
        {
            _stylusDown = false;
        }
    }

    public (bool Down, sbyte Dx, sbyte Dy) TakeStylus()
    {
        lock (_aimGate)
        {
            bool down = _stylusDown || _stylusSeen;
            _stylusSeen = false;
            float dx = Math.Clamp(MathF.Truncate(_stylusX), -127, 127), dy = Math.Clamp(MathF.Truncate(_stylusY), -127, 127);
            _stylusX = down && _stylusDown ? _stylusX - dx : 0;
            _stylusY = down && _stylusDown ? _stylusY - dy : 0;
            return (down, (sbyte)dx, (sbyte)dy);
        }
    }

    // everything released (focus lost, a screen change)
    public void Clear()
    {
        Array.Clear(_keys);
        Array.Clear(_touch);
        Lx = Ly = Rx = Ry = L2 = R2 = HatX = HatY = RawRx = RawRy = 0;
        TakeAim();
        lock (_aimGate)
        {
            _stylusDown = _stylusSeen = false;
            _stylusX = _stylusY = 0;
        }
    }
}

// A GL screen the host drives on its render thread with the platform's context current: the Android GLSurfaceView
// callbacks, or the Windows window loop.
public interface IGlScreen
{
    void OnSurfaceCreated();
    void OnSurfaceChanged(int width, int height);
    void OnDrawFrame();
}
