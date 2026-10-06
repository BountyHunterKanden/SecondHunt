using MphRecomp.Config;

namespace MphRecomp.App;

// The controller's side of the control customizer (Config/ControlBinds.cs): which PadButtons do each game function, from
// RecompSettings.PadBinds, and once per sim step whether each function is held or was just pressed. A trigger counts past
// half way and the Odin's HAT D-pad as the D-pad buttons. The touch controls' buttons (PadState.Touch) always count for
// their default functions, whatever the controller's are now. No allocation per step.
internal sealed class PadBindings
{
    static readonly ControlBinds.Function[] Functions = ControlBinds.PadFunctions;
    readonly PadButton[][] _binds = new PadButton[Functions.Length][];
    readonly PadButton[][] _defaults = new PadButton[Functions.Length][];
    readonly bool[] _held = new bool[Functions.Length], _prev = new bool[Functions.Length];
    static readonly Dictionary<string, int> Index = Functions.Select((f, i) => (f.Id, i)).ToDictionary(p => p.Id, p => p.i);

    public PadBindings()
    {
        for (int i = 0; i < Functions.Length; i++)
        {
            _defaults[i] = Parse(Functions[i].Defaults);
            _binds[i] = _defaults[i];
        }
    }

    public void Load(RecompSettings settings)
    {
        for (int i = 0; i < Functions.Length; i++) _binds[i] = Parse(ControlBinds.Get(settings, false, Functions[i].Id));
    }

    static PadButton[] Parse(string[] names)
    {
        var list = new List<PadButton>();
        foreach (string n in names)
        {
            if (Enum.TryParse(n, out PadButton b) && b < PadButton.Count) list.Add(b);
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

    // once per sim step, before Held / Pressed (also while a dialog is up, so a press there isn't seen after it)
    public void Update(PadState pad)
    {
        for (int i = 0; i < Functions.Length; i++)
        {
            bool held = false;
            foreach (PadButton b in _binds[i]) held |= Physical(pad, b);
            foreach (PadButton b in _defaults[i]) held |= pad.Touch(b);
            _prev[i] = _held[i];
            _held[i] = held;
        }
    }

    public bool Held(string id) => _held[Index[id]];
    public bool Pressed(string id) => _held[Index[id]] && !_prev[Index[id]];
}
