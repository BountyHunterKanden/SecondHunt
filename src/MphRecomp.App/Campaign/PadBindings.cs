using MphRecomp.Config;

namespace MphRecomp.App;

// The controller's side of the control customizer (Config/ControlBinds.cs): which PadButtons do each game function, from
// RecompSettings.PadBinds, and once per sim step whether each function is held or was just pressed. A trigger counts past
// half way and the Odin's HAT D-pad as the D-pad buttons. The touch controls' buttons (PadState.Touch) always count for
// their default functions (and a function's own Touch ones: the touch SCAN zooms too), whatever the controller's are now
// (not the map or the scoreboard: the touch CHAT presses Select). One button can do several functions (owner 2026-10-17:
// L2 = weapon menu, scan and zoom); BuildInput says which with Shares / HeldUnless. A combo
// (two or three buttons, owner 2026-10-11) is held while all its buttons are; from then until each of its buttons is let
// go, those buttons don't do their own single-button functions (so Select + D-up for chat doesn't also pick the scan
// visor; the first button's own function may show for the moment before the second goes down). ZOOM STEP's zoom in / zoom
// out (owner 2026-10-17) take their buttons the same way while ZoomStepping (zoomed with the option on): pressed then, a
// button does only that until it's let go (so D-down stepping out of the zoom doesn't pick missiles as the zoom ends);
// the rest of the time it does its own functions, and zoom in / out are held as usual (the game ignores them unzoomed).
// No allocation per step.
internal sealed class PadBindings
{
    static readonly ControlBinds.Function[] Functions = ControlBinds.PadFunctions;
    readonly PadButton[][][] _binds = new PadButton[Functions.Length][][];
    readonly PadButton[][] _defaults = new PadButton[Functions.Length][]; // the touch buttons that count for each
    readonly bool[] _touch = new bool[(int)PadButton.Count]; // the touch controls' buttons this step
    readonly bool[] _held = new bool[Functions.Length], _prev = new bool[Functions.Length];
    readonly bool[] _down = new bool[(int)PadButton.Count], _eaten = new bool[(int)PadButton.Count];
    readonly bool[] _zoomTook = new bool[(int)PadButton.Count]; // pressed as zoom in / out while zoom stepping, until let go
    static readonly Dictionary<string, int> Index = Functions.Select((f, i) => (f.Id, i)).ToDictionary(p => p.Id, p => p.i);
    static readonly int ScoreboardIndex = Index[ControlBinds.Scoreboard], MapIndex = Index[ControlBinds.Map];
    static readonly int ZoomInIndex = Index[ControlBinds.ZoomIn], ZoomOutIndex = Index[ControlBinds.ZoomOut];
    static readonly int[] ZoomIndexes = { ZoomInIndex, ZoomOutIndex };

    // set before Update: zoomed with ZOOM STEP on (CampaignRenderer.BuildInput)
    public bool ZoomStepping;

    public PadBindings()
    {
        for (int i = 0; i < Functions.Length; i++)
        {
            _binds[i] = Parse(Functions[i].Defaults);
            _defaults[i] = i == ScoreboardIndex || i == MapIndex ? Array.Empty<PadButton>()
                : _binds[i].Where(c => c.Length == 1).Select(c => c[0]).Concat(Parse(Functions[i].Touch ?? Array.Empty<string>())
                    .Select(c => c[0])).Distinct().ToArray();
        }
    }

    public void Load(RecompSettings settings)
    {
        ControlBinds.Tidy(settings); // (older saved lists in today's layout)
        for (int i = 0; i < Functions.Length; i++) _binds[i] = Parse(ControlBinds.Get(settings, false, Functions[i].Id));
    }

    // each binding: its buttons (one, or a combo's)
    static PadButton[][] Parse(string[] names)
    {
        var list = new List<PadButton[]>();
        foreach (string n in names)
        {
            var parts = new List<PadButton>();
            foreach (string part in ControlBinds.Parts(n))
            {
                if (Enum.TryParse(part, out PadButton b) && b < PadButton.Count) parts.Add(b);
                else parts.Clear();
                if (parts.Count == 0) break;
            }
            if (parts.Count > 0) list.Add(parts.ToArray());
        }
        return list.ToArray();
    }

    static bool Physical(PadState pad, PadButton b) => b switch
    {
        PadButton.L2 => pad.L2 >= 0.5f || pad.Physical(b),
        PadButton.R2 => pad.R2 >= 0.5f || pad.Physical(b),
        PadButton.DpadUp => pad.HatY <= -0.5f || pad.Physical(b),
        PadButton.DpadDown => pad.HatY >= 0.5f || pad.Physical(b),
        PadButton.DpadLeft => pad.HatX <= -0.5f || pad.Physical(b),
        PadButton.DpadRight => pad.HatX >= 0.5f || pad.Physical(b),
        _ => pad.Physical(b),
    };

    bool AllDown(PadButton[] combo)
    {
        foreach (PadButton b in combo)
        {
            if (!_down[(int)b]) return false;
        }
        return true;
    }

    // once per sim step, before Held / Pressed (also while a dialog is up, so a press there isn't seen after it)
    public void Update(PadState pad)
    {
        for (int b = 0; b < _down.Length; b++)
        {
            _down[b] = Physical(pad, (PadButton)b);
            _touch[b] = pad.Touch((PadButton)b);
            if (!_down[b]) _eaten[b] = _zoomTook[b] = false;
        }
        if (ZoomStepping)
        {
            foreach (int z in ZoomIndexes)
            {
                foreach (PadButton[] combo in _binds[z])
                {
                    if (combo.Length == 1 && _down[(int)combo[0]]) _zoomTook[(int)combo[0]] = true;
                }
            }
        }
        // the combos held now take their buttons
        foreach (PadButton[][] binds in _binds)
        {
            foreach (PadButton[] combo in binds)
            {
                if (combo.Length > 1 && AllDown(combo))
                {
                    foreach (PadButton b in combo) _eaten[(int)b] = true;
                }
            }
        }
        for (int i = 0; i < Functions.Length; i++)
        {
            bool held = false;
            foreach (PadButton[] combo in _binds[i]) held |= ComboHeld(i, combo);
            foreach (PadButton b in _defaults[i]) held |= _touch[(int)b];
            _prev[i] = _held[i];
            _held[i] = held;
        }
    }

    bool ComboHeld(int i, PadButton[] combo) => combo.Length == 1
        ? _down[(int)combo[0]] && !_eaten[(int)combo[0]] && (!_zoomTook[(int)combo[0]] || i == ZoomInIndex || i == ZoomOutIndex)
        : AllDown(combo);

    // two functions on the same button (or the same combo): the campaign's scan visor gives a button that is both the
    // weapon menu and scan to the scan (CampaignRenderer.BuildInput)
    public bool Shares(string a, string b)
    {
        foreach (PadButton[] x in _binds[Index[a]])
        {
            foreach (PadButton[] y in _binds[Index[b]])
            {
                if (x.Length == y.Length && x.All(y.Contains)) return true;
            }
        }
        return false;
    }

    public bool Held(string id) => _held[Index[id]];

    // held through a button (or combo, or touch button) that isn't also one of `other`'s or `other2`'s: zoom on a button
    // of its own, not the weapon menu's (whose quick press is its zoom) nor, with the scan visor on, a scan button's
    public bool HeldUnless(string id, string other, string? other2 = null)
    {
        int i = Index[id], o = Index[other], o2 = other2 != null ? Index[other2] : -1;
        foreach (PadButton[] combo in _binds[i])
        {
            if (ComboHeld(i, combo) && !Has(o, combo) && (o2 < 0 || !Has(o2, combo))) return true;
        }
        foreach (PadButton b in _defaults[i])
        {
            if (_touch[(int)b] && Array.IndexOf(_defaults[o], b) < 0 && (o2 < 0 || Array.IndexOf(_defaults[o2], b) < 0)) return true;
        }
        return false;
    }

    bool Has(int function, PadButton[] combo)
    {
        foreach (PadButton[] x in _binds[function])
        {
            if (x.Length == combo.Length && x.All(combo.Contains)) return true;
        }
        return false;
    }
    public bool Pressed(string id) => _held[Index[id]] && !_prev[Index[id]];

    // a function's controller buttons held right now, outside the sim step (the campaign's map: CampaignMap.MapFrame;
    // which buttons a combo has taken is as of the last step)
    public bool PhysicalNow(PadState pad, string id)
    {
        foreach (PadButton[] combo in _binds[Index[id]])
        {
            bool all = true;
            foreach (PadButton b in combo) all &= Physical(pad, b) && (combo.Length > 1 || !_eaten[(int)b]);
            if (all) return true;
        }
        return false;
    }
}
