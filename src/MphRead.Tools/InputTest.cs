using System;
using System.IO;
using System.Numerics;
using MphRecomp.Input;
using MphRecomp.Sim;

namespace MphRead
{
    // Headless regression tests for the mph-recomp input-mapping layer (MphRecomp.Input). Run:
    // MphRead.Tools.dll -inputtest  (no ROM/device). Covers defaults, radial dead zone behaviour,
    // move/look mapping + sensitivity + invert, button bindings + rebinding, pressed-button
    // detection, and bindings JSON round-trip.
    internal static class InputTest
    {
        static int _pass, _fail;
        static void Check(string name, bool ok) { if (ok) _pass++; else _fail++; Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {name}"); }
        static bool Near(float a, float b, float e = 1e-4f) => Math.Abs(a - b) < e;

        public static void Run(string[] args)
        {
            var map = new InputMap();
            var raw = new RawInput();

            // 1) defaults
            Check("default: Jump bound to A", map.Bindings.Buttons[InputButtons.Jump] == RawButton.A);
            Check("default: Fire bound to R2", map.Bindings.Buttons[InputButtons.Fire] == RawButton.R2);
            Check("default: move stick = Left, look = Right",
                map.Bindings.MoveStick == Stick.Left && map.Bindings.LookStick == Stick.Right);

            // 2) radial dead zone: below dz -> zero; above -> magnitude rescaled; direction preserved
            Check("deadzone: below threshold -> zero", InputMap.RadialDeadZone(new Vector2(0.1f, 0f), 0.15f) == Vector2.Zero);
            var full = InputMap.RadialDeadZone(new Vector2(1f, 0f), 0.15f);
            Check("deadzone: full deflection -> magnitude ~1", Near(full.Length(), 1f));
            var diag = InputMap.RadialDeadZone(new Vector2(0.5f, 0.5f), 0.15f);
            Check("deadzone: direction preserved (x == y)", Near(diag.X, diag.Y));
            Check("deadzone: partial magnitude rescaled to (m-dz)/(1-dz)",
                Near(diag.Length(), (0.5f * MathF.Sqrt(2) - 0.15f) / 0.85f, 1e-3f));

            // 3) move mapping: stick up (raw -Y) = forward (+Y); +X = strafe right
            raw.Clear(); raw.Set(RawAxis.LeftY, -1f);
            Check("move: stick up -> forward (+Y)", map.Resolve(raw).Move.Y > 0.9f);
            raw.Clear(); raw.Set(RawAxis.LeftX, 1f);
            Check("move: stick right -> +X", map.Resolve(raw).Move.X > 0.9f);
            raw.Clear(); raw.Set(RawAxis.LeftX, 0.1f);
            Check("move: within dead zone -> no movement", map.Resolve(raw).Move == Vector2.Zero);

            // 4) look sensitivity + invert
            map.Bindings.LookSensitivity = 2f;
            raw.Clear(); raw.Set(RawAxis.RightX, 1f);
            Check("look: sensitivity scales X", Near(map.Resolve(raw).Look.X, 2f));
            raw.Clear(); raw.Set(RawAxis.RightY, -1f);
            float upNormal = map.Resolve(raw).Look.Y;
            map.Bindings.InvertLookY = true;
            float upInverted = map.Resolve(raw).Look.Y;
            Check("look: invert-Y flips the sign", Near(upNormal, -upInverted) && Math.Abs(upNormal) > 0.5f);
            map.Bindings.LookSensitivity = 1f; map.Bindings.InvertLookY = false;

            // 5) button bindings + rebinding
            raw.Clear(); raw.Set(RawButton.A, true); raw.Set(RawButton.R2, true);
            var st = map.Resolve(raw);
            Check("buttons: A -> Jump, R2 -> Fire", st.Down(InputButtons.Jump) && st.Down(InputButtons.Fire));
            Check("buttons: unpressed action not set", !st.Down(InputButtons.Boost));
            map.SetBind(InputButtons.Jump, RawButton.X);
            raw.Clear(); raw.Set(RawButton.X, true);
            Check("rebind: Jump now on X", map.Resolve(raw).Down(InputButtons.Jump));
            raw.Clear(); raw.Set(RawButton.A, true);
            Check("rebind: old button (A) no longer Jump", !map.Resolve(raw).Down(InputButtons.Jump));

            // 6) pressed-button detection (rebind UI)
            raw.Clear(); raw.Set(RawButton.B, true);
            Check("detect: reports first pressed button", InputMap.DetectPressedButton(raw) == RawButton.B);
            raw.Clear();
            Check("detect: none pressed -> null", InputMap.DetectPressedButton(raw) == null);

            // 7) bindings JSON round-trip
            string dir = Path.Combine(Path.GetTempPath(), "mphrecomp_inputtest_" + Guid.NewGuid().ToString("N"));
            string path = Path.Combine(dir, "bind.json");
            try
            {
                var b = InputBindings.Default();
                b.Buttons[InputButtons.Fire] = RawButton.X;
                b.MoveDeadZone = 0.22f; b.LookSensitivity = 1.5f; b.InvertLookY = true;
                InputMap.SaveBindings(b, path);
                var back = InputMap.LoadBindings(path);
                Check("json: custom bind preserved", back.Buttons[InputButtons.Fire] == RawButton.X);
                Check("json: tuning preserved", Near(back.MoveDeadZone, 0.22f) && Near(back.LookSensitivity, 1.5f) && back.InvertLookY);
                Check("json: missing file -> defaults", InputMap.LoadBindings(Path.Combine(dir, "nope.json")).Buttons[InputButtons.Jump] == RawButton.A);
            }
            finally { try { Directory.Delete(dir, recursive: true); } catch { } }

            Console.WriteLine($"\nINPUT TESTS: {_pass} passed, {_fail} failed  =>  {(_fail == 0 ? "ALL GREEN" : "FAILURES")}");
            Environment.ExitCode = _fail == 0 ? 0 : 1;
        }
    }
}
