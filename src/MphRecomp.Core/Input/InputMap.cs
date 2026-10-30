using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;
using MphRecomp.Sim;

// mph-recomp input-mapping layer: turns raw controller state into the sim's action-based
// InputState (Move/Look + button flags). Configurable button bindings, per-stick radial dead
// zones, look sensitivity + Y inversion, and rebinding. Decouples gameplay from hardware: the
// sim never sees a physical button/axis, and bindings are JSON-serializable so they persist and
// are moddable/rebindable. Pure data + logic -> headlessly testable.
namespace MphRecomp.Input
{
    public enum RawButton
    {
        A, B, X, Y, L1, R1, L2, R2, Start, Select,
        LeftThumb, RightThumb, DpadUp, DpadDown, DpadLeft, DpadRight
    }
    public enum RawAxis { LeftX, LeftY, RightX, RightY, LeftTrigger, RightTrigger }
    public enum Stick { Left, Right }

    // A reusable snapshot of the physical controller, filled by the presentation layer each frame
    // (allocate once -> zero per-frame alloc). Axes are in [-1,1]; triggers in [0,1].
    public sealed class RawInput
    {
        public readonly bool[] Buttons = new bool[Enum.GetValues<RawButton>().Length];
        public readonly float[] Axes = new float[Enum.GetValues<RawAxis>().Length];

        public bool Down(RawButton b) => Buttons[(int)b];
        public float Axis(RawAxis a) => Axes[(int)a];
        public void Set(RawButton b, bool down) => Buttons[(int)b] = down;
        public void Set(RawAxis a, float v) => Axes[(int)a] = v;
        public void Clear() { Array.Clear(Buttons); Array.Clear(Axes); }
    }

    // Serializable bindings. Digital game actions reuse the sim's InputButtons flags; each maps to
    // one physical button. Sticks + dead zones + look feel are configurable.
    public sealed class InputBindings
    {
        public Dictionary<InputButtons, RawButton> Buttons { get; set; } = new();
        public Stick MoveStick { get; set; } = Stick.Left;
        public Stick LookStick { get; set; } = Stick.Right;
        public float MoveDeadZone { get; set; } = 0.15f;
        public float LookDeadZone { get; set; } = 0.15f;
        public float LookSensitivity { get; set; } = 1.0f;
        public bool InvertLookY { get; set; } = false;

        public static InputBindings Default() => new()
        {
            Buttons = new Dictionary<InputButtons, RawButton>
            {
                [InputButtons.Jump] = RawButton.A,
                [InputButtons.Fire] = RawButton.R2,
                [InputButtons.Morph] = RawButton.L2,
                [InputButtons.AltForm] = RawButton.Y,
                [InputButtons.Boost] = RawButton.B,
                [InputButtons.WeaponNext] = RawButton.R1,
                [InputButtons.WeaponPrev] = RawButton.L1,
                [InputButtons.Back] = RawButton.Select
            }
        };
    }

    public sealed class InputMap
    {
        public InputBindings Bindings { get; set; }

        public InputMap(InputBindings? bindings = null) => Bindings = bindings ?? InputBindings.Default();

        // Map the current raw controller state to the sim's action input for this frame.
        public InputState Resolve(RawInput raw)
        {
            var state = new InputState();

            Vector2 mv = RadialDeadZone(ReadStick(raw, Bindings.MoveStick), Bindings.MoveDeadZone);
            state.Move = new Vector2(mv.X, -mv.Y); // stick up (raw -Y) = forward (+Y)

            Vector2 lk = RadialDeadZone(ReadStick(raw, Bindings.LookStick), Bindings.LookDeadZone) * Bindings.LookSensitivity;
            state.Look = new Vector2(lk.X, Bindings.InvertLookY ? lk.Y : -lk.Y);

            foreach (var kv in Bindings.Buttons)
                if (raw.Down(kv.Value)) state.Buttons |= kv.Key;

            return state;
        }

        // Rebind an action to a physical button (a button may serve multiple actions; last write wins).
        public void SetBind(InputButtons action, RawButton button) => Bindings.Buttons[action] = button;

        // For a "press a button to bind" flow: the first pressed physical button, or null if none.
        public static RawButton? DetectPressedButton(RawInput raw)
        {
            foreach (RawButton b in Enum.GetValues<RawButton>())
                if (raw.Down(b)) return b;
            return null;
        }

        private static Vector2 ReadStick(RawInput raw, Stick stick) => stick == Stick.Left
            ? new Vector2(raw.Axis(RawAxis.LeftX), raw.Axis(RawAxis.LeftY))
            : new Vector2(raw.Axis(RawAxis.RightX), raw.Axis(RawAxis.RightY));

        // Radial dead zone: below `dz` -> zero; above -> direction preserved, magnitude rescaled so
        // the usable range [dz,1] maps to [0,1] (no snap at the edge of the dead zone).
        internal static Vector2 RadialDeadZone(Vector2 v, float dz)
        {
            dz = Math.Clamp(dz, 0f, 0.99f);
            float m = v.Length();
            if (m <= dz || m <= 1e-6f) return Vector2.Zero;
            float scaled = Math.Min(1f, (m - dz) / (1f - dz));
            return v * (scaled / m);
        }

        // --- persistence (bindings live in user settings; JSON so they round-trip + are moddable) ---
        // JsonStringEnumConverter serializes enum dictionary keys AND values as readable names
        // (e.g. { "Jump": "A" }) rather than opaque integers.
        private static readonly JsonSerializerOptions Options = new()
        {
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter() }
        };

        public static void SaveBindings(InputBindings b, string path)
        {
            string? dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(path, JsonSerializer.Serialize(b, Options));
        }

        public static InputBindings LoadBindings(string path)
        {
            if (!File.Exists(path)) return InputBindings.Default();
            try { return JsonSerializer.Deserialize<InputBindings>(File.ReadAllText(path), Options) ?? InputBindings.Default(); }
            catch (Exception) { return InputBindings.Default(); }
        }
    }
}
