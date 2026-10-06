using MphRead;
using MphRead.Entities;
using MphRecomp.Campaign;
using MphRecomp.Config;
using OpenTK.Windowing.GraphicsLibraryFramework;

namespace MphRecomp.App;

// A PC's keyboard and mouse in the campaign and matches, on MphRead's own PC controls (PlayerControls.GetDefault, what
// MphRead's desktop build plays with): WASD move and roll, the mouse aims, left click fires, right click zooms (scans
// with the scan visor on), Space jumps and boosts, C morphs, Q (or left click in the morph ball) lays bombs and scans,
// E toggles the scan visor, the wheel steps weapons, the middle
// button holds the weapon menu (the mouse picks), 1-9 pick weapons. The host keeps the keys and buttons current
// (KeyDown / MouseDown, AddScroll; mouse movement goes through PadState.AddAim) and BuildInput adds this to the pad's
// input once per sim step. The host itself handles Tab (MphRead's pause: the pause menu), Esc and the map key.
// The control customizer moves these (OPTIONS > CONTROLS > KEYBOARD & MOUSE; Config/ControlBinds.cs, LoadBinds); the
// 1-9 weapon keys and the dialog keys stay MphRead's.
public sealed class KeyboardMouseInput
{
    readonly PlayerControls _binds = PlayerControls.GetDefault();
    static readonly ControlBinds.Function[] Functions = ControlBinds.KeyFunctions;
    static readonly Dictionary<string, int> FunctionIndex = Functions.Select((f, i) => (f.Id, i)).ToDictionary(p => p.Id, p => p.i);
    Keybind[][] _functions = Build(null);
    public Func<Keys, bool> KeyDown { get; set; } = _ => false;
    public Func<MouseButton, bool> MouseDown { get; set; } = _ => false;
    int _scrollUp, _scrollDown;
    bool _prevConfirm, _prevBack, _prevLeft, _prevRight;
    System.Numerics.Vector2 _wheel;
    readonly object _gate = new();

    // mouse wheel steps since the last sim step (+ up, - down)
    public void AddScroll(float steps)
    {
        lock (_gate)
        {
            if (steps > 0) _scrollUp++;
            else if (steps < 0) _scrollDown++;
        }
    }

    // the customizer's keys and buttons for each function (the host reloads them when the settings may have changed)
    public void LoadBinds(RecompSettings settings) => _functions = Build(settings);

    static Keybind[][] Build(RecompSettings? settings)
    {
        var all = new Keybind[Functions.Length][];
        for (int i = 0; i < Functions.Length; i++)
        {
            string[] names = settings == null ? Functions[i].Defaults : ControlBinds.Get(settings, true, Functions[i].Id);
            all[i] = names.Select(Parse).OfType<Keybind>().ToArray();
        }
        return all;
    }

    // a ControlBinds button name: an OpenTK Keys name, Mouse.<button> or Wheel.Up / Wheel.Down
    static Keybind? Parse(string name)
    {
        if (name.StartsWith("Mouse.", StringComparison.Ordinal))
        {
            return Enum.TryParse(name[6..], out MouseButton m) ? new Keybind(m) : null;
        }
        if (name == "Wheel.Up") return new Keybind(ButtonType.ScrollUp);
        if (name == "Wheel.Down") return new Keybind(ButtonType.ScrollDown);
        return Enum.TryParse(name, out Keys k) ? new Keybind(k) : null;
    }

    bool Held(string function, bool scrollUp, bool scrollDown)
    {
        foreach (Keybind bind in _functions[FunctionIndex[function]])
        {
            if (Down(bind, scrollUp, scrollDown)) return true;
        }
        return false;
    }

    bool Down(Keybind bind, bool scrollUp, bool scrollDown) => bind.Type switch
    {
        ButtonType.Key => bind.Key != Keys.Unknown && KeyDown(bind.Key),
        ButtonType.Mouse => MouseDown(bind.MouseButton),
        ButtonType.ScrollUp => scrollUp,
        ButtonType.ScrollDown => scrollDown,
        _ => false,
    };

    // a dialog's buttons (the DS's touch-screen OK / YES / NO / page arrows): Space, Enter or a left click answers yes /
    // next, Backspace or a right click no, the arrow keys page
    public HostDialogButton DialogButton(bool yesNo)
    {
        bool confirm = KeyDown(Keys.Space) || KeyDown(Keys.Enter) || KeyDown(Keys.KeyPadEnter) || MouseDown(MouseButton.Left);
        bool back = KeyDown(Keys.Backspace) || MouseDown(MouseButton.Right);
        bool left = KeyDown(Keys.Left) || KeyDown(Keys.A), right = KeyDown(Keys.Right) || KeyDown(Keys.D);
        bool pressConfirm = confirm && !_prevConfirm, pressBack = back && !_prevBack;
        bool pressLeft = left && !_prevLeft, pressRight = right && !_prevRight;
        _prevConfirm = confirm;
        _prevBack = back;
        _prevLeft = left;
        _prevRight = right;
        return pressConfirm ? (yesNo ? HostDialogButton.Yes : HostDialogButton.Advance)
            : pressBack && yesNo ? HostDialogButton.No
            : pressLeft ? HostDialogButton.Left
            : pressRight ? HostDialogButton.Right
            : HostDialogButton.None;
    }

    // this sim step's keys and buttons, added to what the pad gave; pointer: the mouse's movement this step (aim, or
    // the weapon menu's pick while it is held)
    public void AddTo(ref CampaignInput input, PlayerEntity player, System.Numerics.Vector2 pointer, ref System.Numerics.Vector2 aim)
    {
        bool up, down;
        lock (_gate)
        {
            up = _scrollUp > 0;
            down = _scrollDown > 0;
            if (up) _scrollUp--;
            if (down) _scrollDown--;
        }
        PlayerControls c = _binds;
        bool alt = player.IsAltForm;
        float x = 0, y = 0;
        // (MphRead's walk and roll keys are the same W A S D; one function each moves both)
        if (Held(ControlBinds.Right, up, down)) x += 1;
        if (Held(ControlBinds.Left, up, down)) x -= 1;
        if (Held(ControlBinds.Forward, up, down)) y += 1;
        if (Held(ControlBinds.Back, up, down)) y -= 1;
        if (x != 0 || y != 0)
        {
            input.Move = new System.Numerics.Vector2(Math.Clamp(input.Move.X + x, -1, 1), Math.Clamp(input.Move.Y + y, -1, 1));
        }
        var b = CampaignButtons.None;
        if (Held(ControlBinds.Fire, up, down)) b |= CampaignButtons.Shoot;
        // right click scans with the scan visor on, like the pad's L2 (owner, 2026-10-05); MphRead's Q still scans too
        if (Held(ControlBinds.ZoomScan, up, down)) b |= player.ScanVisor ? CampaignButtons.Scan : CampaignButtons.Zoom;
        if (Held(ControlBinds.Jump, up, down)) b |= CampaignButtons.Jump | CampaignButtons.Boost; // MphRead: both Space
        if (Held(ControlBinds.Morph, up, down)) b |= CampaignButtons.Morph;
        if (Held(ControlBinds.BombScan, up, down)) b |= CampaignButtons.AltAttack | CampaignButtons.Scan; // MphRead: both Q
        if (Held(ControlBinds.ScanVisor, up, down)) b |= CampaignButtons.ScanVisor;
        if (Down(c.AffinitySlot, up, down)) b |= CampaignButtons.AffinitySlot;
        // in the morph ball left click lays bombs too, like the pad's fire button (owner, 2026-10-05)
        if (alt && (b & CampaignButtons.Shoot) != 0) b |= CampaignButtons.AltAttack;
        bool menu = Held(ControlBinds.WeaponMenu, up, down);
        if (menu)
        {
            // the weapon menu follows the mouse while it's held: the direction it has moved since the menu opened
            b |= CampaignButtons.WeaponMenu;
            _wheel += new System.Numerics.Vector2(pointer.X, -pointer.Y);
            if (_wheel.Length() > 60) _wheel = System.Numerics.Vector2.Normalize(_wheel) * 60;
            input.WeaponWheel = _wheel.Length() > 12 ? _wheel / 60 : System.Numerics.Vector2.Zero;
        }
        else
        {
            _wheel = System.Numerics.Vector2.Zero;
            aim += pointer;
        }
        input.Buttons |= b;
        if (input.SelectWeapon == BeamType.None)
        {
            (Keybind Bind, BeamType Beam)[] weapons =
            {
                (c.PowerBeam, BeamType.PowerBeam), (c.Missile, BeamType.Missile), (c.VoltDriver, BeamType.VoltDriver),
                (c.Battlehammer, BeamType.Battlehammer), (c.Imperialist, BeamType.Imperialist), (c.Judicator, BeamType.Judicator),
                (c.Magmaul, BeamType.Magmaul), (c.ShockCoil, BeamType.ShockCoil), (c.OmegaCannon, BeamType.OmegaCannon),
            };
            foreach ((Keybind bind, BeamType beam) in weapons)
            {
                if (Down(bind, up, down))
                {
                    input.SelectWeapon = beam;
                    break;
                }
            }
        }
        // the wheel steps through every weapon you have that has ammo, down = next (owner 2026-10-05: MphRead's own
        // next / previous weapon only cycle the pickup weapons, and only while one is equipped, so on the Power Beam or
        // missiles, which every match and the campaign start with, the wheel did nothing)
        int step = Held(ControlBinds.NextWeapon, up, down) ? 1 : Held(ControlBinds.PrevWeapon, up, down) ? -1 : 0;
        if (step != 0 && input.SelectWeapon == BeamType.None)
        {
            input.SelectWeapon = CycleWeapon(player, step);
        }
    }

    // the weapon keys' order (1-9)
    static readonly BeamType[] WeaponOrder =
    {
        BeamType.PowerBeam, BeamType.Missile, BeamType.VoltDriver, BeamType.Battlehammer, BeamType.Imperialist,
        BeamType.Judicator, BeamType.Magmaul, BeamType.ShockCoil, BeamType.OmegaCannon,
    };

    // the next (+1) / previous (-1) weapon after the current one that the player has and can fire; also the pad's D-pad
    // left / right (CampaignRenderer.BuildInput)
    internal static BeamType CycleWeapon(PlayerEntity player, int step)
    {
        int n = WeaponOrder.Length;
        int at = Math.Max(0, Array.IndexOf(WeaponOrder, player.CurrentWeapon));
        for (int i = 1; i < n; i++)
        {
            BeamType beam = WeaponOrder[((at + step * i) % n + n) % n];
            if (player.CanEquipWeapon(beam)) return beam;
        }
        return BeamType.None;
    }
}
