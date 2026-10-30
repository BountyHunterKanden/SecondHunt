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
// input once per sim step. The host itself handles Esc (the pause menu) and the map key; Tab is the scoreboard (owner
// 2026-10-11: no longer MphRead's pause key here).
// The control customizer moves these (OPTIONS > CONTROLS > KEYBOARD & MOUSE; Config/ControlBinds.cs, LoadBinds); the
// 1-9 weapon keys and the dialog keys stay MphRead's.
public sealed class KeyboardMouseInput
{
    readonly PlayerControls _binds = PlayerControls.GetDefault();
    static readonly ControlBinds.Function[] Functions = ControlBinds.KeyFunctions;
    static readonly Dictionary<string, int> FunctionIndex = Functions.Select((f, i) => (f.Id, i)).ToDictionary(p => p.Id, p => p.i);
    Keybind[][][] _functions = Build(null);
    public Func<Keys, bool> KeyDown { get; set; } = _ => false;
    public Func<MouseButton, bool> MouseDown { get; set; } = _ => false;
    int _scrollUp, _scrollDown;
    bool _prevConfirm, _prevBack, _prevLeft, _prevRight;
    bool _prevNextHeld, _prevPrevHeld; // next / previous weapon's keys and mouse buttons last step (one step a press)
    System.Numerics.Vector2 _wheel;
    readonly object _gate = new();
    static readonly int[] ZoomIndexes = { FunctionIndex[ControlBinds.ZoomIn], FunctionIndex[ControlBinds.ZoomOut] };
    readonly List<Keybind> _zoomTook = new(); // keys pressed as zoom in / out while ZoomStepping, until let go

    // set before AddTo: zoomed with ZOOM STEP on (owner 2026-10-17; CampaignRenderer.BuildInput). Then the zoom in / out
    // keys do only that until they're let go, as PadBindings does with the controller's
    public bool ZoomStepping { get; set; }

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
    public void LoadBinds(RecompSettings settings)
    {
        ControlBinds.Tidy(settings); // (older saved lists in today's layout)
        _functions = Build(settings);
    }

    // each function's bindings: one key or button, or a combo's (owner 2026-10-11)
    static Keybind[][][] Build(RecompSettings? settings)
    {
        var all = new Keybind[Functions.Length][][];
        for (int i = 0; i < Functions.Length; i++)
        {
            string[] names = settings == null ? Functions[i].Defaults : ControlBinds.Get(settings, true, Functions[i].Id);
            all[i] = names.Select(n => ControlBinds.Parts(n).Select(Parse).ToArray())
                .Where(parts => parts.Length > 0 && parts.All(p => p is not null)).Select(parts => parts.Select(p => p!).ToArray()).ToArray();
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

    // online voice chat's push to talk (V by default; CampaignRenderer.BuildInput passes it on with the pad's R3)
    public bool TalkHeld => Held(ControlBinds.Talk, false, false);

    // a match's live scoreboard, held (Tab by default; MatchChat.MatchScoreboardHeld, with the pad's)
    public bool ScoreboardHeld => Held(ControlBinds.Scoreboard, false, false);

    // a combo is held while all its keys are; while it is, its keys don't do their own single-key functions
    bool Held(string function, bool scrollUp, bool scrollDown)
    {
        foreach (Keybind[] combo in _functions[FunctionIndex[function]])
        {
            if (ComboHeld(function, combo, scrollUp, scrollDown)) return true;
        }
        return false;
    }

    bool ComboHeld(string function, Keybind[] combo, bool scrollUp, bool scrollDown)
        => combo.Length > 1 ? AllDown(combo) : Down(combo[0], scrollUp, scrollDown) && !InHeldCombo(combo[0])
            && (function is ControlBinds.ZoomIn or ControlBinds.ZoomOut || !ZoomTook(combo[0]));

    // held through a key (or combo) that isn't also one of `other`'s (zoom on a scan key, with the scan visor on: the scan)
    bool HeldUnless(string function, string? other, bool scrollUp, bool scrollDown)
    {
        foreach (Keybind[] combo in _functions[FunctionIndex[function]])
        {
            if (ComboHeld(function, combo, scrollUp, scrollDown) && (other == null || !Bound(other, combo))) return true;
        }
        return false;
    }

    bool Bound(string function, Keybind[] combo)
    {
        foreach (Keybind[] x in _functions[FunctionIndex[function]])
        {
            if (x.Length == combo.Length && x.All(k => Array.Exists(combo, c => Same(c, k)))) return true;
        }
        return false;
    }

    bool AllDown(Keybind[] combo)
    {
        foreach (Keybind k in combo)
        {
            if (!Down(k, false, false)) return false;
        }
        return true;
    }

    bool InHeldCombo(Keybind key)
    {
        foreach (Keybind[][] binds in _functions)
        {
            foreach (Keybind[] combo in binds)
            {
                if (combo.Length > 1 && Array.Exists(combo, k => Same(k, key)) && AllDown(combo)) return true;
            }
        }
        return false;
    }

    bool ZoomTook(Keybind key)
    {
        foreach (Keybind k in _zoomTook)
        {
            if (Same(k, key)) return true;
        }
        return false;
    }

    // AddTo, first: zoom in / out's keys pressed while zoom stepping are taken until let go
    void UpdateZoomTook(bool scrollUp, bool scrollDown)
    {
        for (int i = _zoomTook.Count - 1; i >= 0; i--)
        {
            if (!Down(_zoomTook[i], scrollUp, scrollDown)) _zoomTook.RemoveAt(i);
        }
        if (!ZoomStepping) return;
        foreach (int z in ZoomIndexes)
        {
            foreach (Keybind[] combo in _functions[z])
            {
                if (combo.Length == 1 && Down(combo[0], scrollUp, scrollDown) && !ZoomTook(combo[0])) _zoomTook.Add(combo[0]);
            }
        }
    }

    static bool Same(Keybind a, Keybind b) => a.Type == b.Type && a.Key == b.Key && a.MouseButton == b.MouseButton;

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
        UpdateZoomTook(up, down);
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
        // the Trooper's Scout Drone out: its own keys instead of the hunter's (owner 2026-10-11); the mouse aims it
        if (player.HostScoutDrone && alt)
        {
            if (Held(ControlBinds.DroneFire, up, down)) b |= CampaignButtons.Shoot | CampaignButtons.AltAttack;
            if (Held(ControlBinds.DroneLaser, up, down)) b |= CampaignButtons.Zoom;
            if (Held(ControlBinds.DroneRise, up, down)) b |= CampaignButtons.Jump | CampaignButtons.Boost;
            if (Held(ControlBinds.Morph, up, down)) b |= CampaignButtons.Morph; // recall: the hunter's morph key (2026-10-19)
            _wheel = System.Numerics.Vector2.Zero;
            // (a weapon key held across the recall doesn't step on it)
            _prevNextHeld = Held(ControlBinds.NextWeapon, false, false); _prevPrevHeld = Held(ControlBinds.PrevWeapon, false, false);
            aim += pointer;
            input.Buttons |= b;
            return;
        }
        if (Held(ControlBinds.Fire, up, down)) b |= CampaignButtons.Shoot;
        // right click zooms, and scans with the scan visor on, like the pad's L2 (owner, 2026-10-05); MphRead's Q scans too
        // (one function a row since owner 2026-10-17, C10: zoom, scan and bombs)
        if (Held(ControlBinds.Scan, up, down)) b |= CampaignButtons.Scan;
        if (HeldUnless(ControlBinds.Zoom, player.ScanVisor ? ControlBinds.Scan : null, up, down)) b |= CampaignButtons.Zoom;
        if (Held(ControlBinds.Jump, up, down)) b |= CampaignButtons.Jump | CampaignButtons.Boost; // MphRead: both Space
        // (the Trooper's morph is his drone: the morph key deploys it)
        if (Held(ControlBinds.Morph, up, down)) b |= CampaignButtons.Morph;
        if (Held(ControlBinds.Bombs, up, down)) b |= CampaignButtons.AltAttack; // (MphRead: Q, which scans too)
        if (Held(ControlBinds.ScanVisor, up, down)) b |= CampaignButtons.ScanVisor;
        if (Held(ControlBinds.ZoomIn, up, down)) b |= CampaignButtons.ZoomIn; // ZOOM STEP (the game reads them only zoomed)
        if (Held(ControlBinds.ZoomOut, up, down)) b |= CampaignButtons.ZoomOut;
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
        // the weapons' keys (the customizer's WEAPONS list; 1-9 and 0 by default, owner 2026-10-15)
        if (input.SelectWeapon == BeamType.None)
        {
            foreach ((string id, int beam) in ControlBinds.WeaponPicks)
            {
                if (Held(id, up, down))
                {
                    input.SelectWeapon = (BeamType)beam;
                    break;
                }
            }
        }
        // the wheel steps through every weapon you have that has ammo, down = next (owner 2026-10-05: MphRead's own
        // next / previous weapon only cycle the pickup weapons, and only while one is equipped, so on the Power Beam or
        // missiles, which every match and the campaign start with, the wheel did nothing)
        // A key or mouse button steps once when pressed (GitHub #2, 10-24: a side button held stepped every sim step); a
        // wheel notch is one step each.
        bool nextHeld = Held(ControlBinds.NextWeapon, false, false), prevHeld = Held(ControlBinds.PrevWeapon, false, false);
        bool next = nextHeld ? !_prevNextHeld : Held(ControlBinds.NextWeapon, up, down);
        bool prev = prevHeld ? !_prevPrevHeld : Held(ControlBinds.PrevWeapon, up, down);
        _prevNextHeld = nextHeld;
        _prevPrevHeld = prevHeld;
        int step = next ? 1 : prev ? -1 : 0;
        if (step != 0 && input.SelectWeapon == BeamType.None)
        {
            input.SelectWeapon = CycleWeapon(player, step);
        }
    }

    // the cycling order: the weapon keys' (1-9), with the Pirate Blaster after the Shock Coil as in MphRead's own
    // _weaponOrder (T4 N22: D-pad left / right never reached it)
    static readonly BeamType[] WeaponOrder =
    {
        BeamType.PowerBeam, BeamType.Missile, BeamType.VoltDriver, BeamType.Battlehammer, BeamType.Imperialist,
        BeamType.Judicator, BeamType.Magmaul, BeamType.ShockCoil, BeamType.PirateBlaster, BeamType.OmegaCannon,
    };

    // the next (+1) / previous (-1) weapon after the current one that the player has and can fire; also the pad's D-pad
    // left / right (CampaignRenderer.BuildInput). With the Omega Cannon in the third slot (T4 N15) only the Power Beam,
    // missiles, the Omega Cannon and the Pirate Blaster (its own slot: PlayerEntity.UpdateAffinityWeaponSlot): as on the
    // DS's touch screen, where those are the weapons you hold, and stepping onto another special weapon would take the
    // Omega Cannon's slot and throw it away
    internal static BeamType CycleWeapon(PlayerEntity player, int step)
    {
        bool omega = player.HostThirdSlotWeapon == BeamType.OmegaCannon && player.AvailableWeapons[BeamType.OmegaCannon];
        int n = WeaponOrder.Length;
        int at = Math.Max(0, Array.IndexOf(WeaponOrder, player.CurrentWeapon));
        for (int i = 1; i < n; i++)
        {
            BeamType beam = WeaponOrder[((at + step * i) % n + n) % n];
            if (omega && beam != BeamType.PowerBeam && beam != BeamType.Missile && beam != BeamType.OmegaCannon
                && beam != BeamType.PirateBlaster) continue;
            if (player.CanEquipWeapon(beam)) return beam;
        }
        return BeamType.None;
    }
}
