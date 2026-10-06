using MphRecomp.App;
using MphRecomp.App.Platform;
using MphRecomp.Frontend;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.Desktop;
using OpenTK.Windowing.GraphicsLibraryFramework;
using SecondHunt.Desktop.Screens;
using SecondHunt.Desktop.Ui;

namespace SecondHunt.Desktop;

// Keyboard, mouse and gamepads, to whichever screen is on top.
//   In the game: MphRead's own PC controls (KeyboardMouseInput: WASD, mouse aim, left click fire, right click zoom / scan, Space
//     jump, C morph, Q bomb / scan, E scan visor, wheel / middle click / 1-9 weapons), Tab the pause menu (MphRead's
//     pause key), Esc as Android's Back (the pause menu, or back out of a menu), M the map (the controller's Select),
//     F3 the status line (L3). The mouse is captured while playing and free over the game's own screens.
//   In the game's screens and menus: arrows / WASD move, Enter / Space choose (A), Backspace back (B), Q / E page (L / R),
//     X / Y, the mouse clicks (the DS touch screen).
//   A gamepad (any GLFW knows, Xbox layout) works everywhere exactly as on Android: the same buttons, sticks and
//     triggers, Start the pause menu, Back / View the map.
//   F11 or Alt+Enter: full screen.
internal sealed class DesktopInput
{
    readonly DesktopApp _app;
    readonly NativeWindow _w;
    public readonly KeyboardMouseInput KeyboardMouse = new();
    bool _captured;
    // the gamepad's previous buttons / pushes, for presses and once-per-push moves
    readonly bool[] _padPrev = new bool[15];
    int _hatX, _hatY, _stickX, _stickY;
    float _prevL2, _prevR2; // the triggers (0..1) last poll, for the control customizer's capture
    int _wheelFrames; // mouse wheel in the map: L1 / R1 held a few frames

    public DesktopInput(DesktopApp app, NativeWindow window)
    {
        _app = app;
        _w = window;
        KeyboardMouse.KeyDown = key => _captured && _w.KeyboardState.IsKeyDown(key);
        KeyboardMouse.MouseDown = button => _captured && _w.MouseState.IsButtonDown(button);
        _w.KeyDown += OnKeyDown;
        _w.KeyUp += OnKeyUp;
        _w.TextInput += e =>
        {
            if (_app.TopScreen is PageScreen page) page.Text((char)e.Unicode);
        };
        _w.MouseDown += OnMouseDown;
        _w.MouseUp += OnMouseUp;
        _w.MouseMove += OnMouseMove;
        _w.MouseWheel += OnMouseWheel;
        _w.FocusedChanged += e =>
        {
            if (!e.IsFocused) Release();
        };
        unsafe
        {
            if (GLFW.RawMouseMotionSupported())
            {
                GLFW.SetInputMode(_w.WindowPtr, RawMouseMotionAttribute.RawMouseMotion, true);
            }
        }
    }

    // a new screen on top: nothing held carries over
    public void ScreenChanged()
    {
        Release();
        Array.Clear(_padPrev);
        _hatX = _hatY = _stickX = _stickY = 0;
    }

    void Release()
    {
        _app.Game?.Pad.Clear();
        SetCapture(false);
    }

    void SetCapture(bool on)
    {
        if (on == _captured) return;
        _captured = on;
        _w.CursorState = on ? CursorState.Grabbed : CursorState.Normal;
        _app.Game?.Pad.TakeAim(); // nothing banked from before
    }

    // once a frame, before drawing: capture follows what has the input, the gamepad is read
    public void Poll()
    {
        CampaignRenderer? game = _app.Game;
        SetCapture(game != null && game.InGameplay && _w.IsFocused);
        PollGamepad();
        if (game != null && _wheelFrames > 0 && --_wheelFrames == 0)
        {
            game.Pad.Set(PadButton.L1, false);
            game.Pad.Set(PadButton.R1, false);
        }
    }

    // --script "<frame>:<key>,<frame>:click@<x>;<y>,<frame>:wheeldown,...": synthetic key presses (down that frame, up the
    // next), wheel notches and clicks
    // through the same handlers, for scripted test runs of the menus and screens (dev builds)
    readonly List<(int Frame, string Action)> _script = new();
    readonly List<Keys> _scriptHeld = new();
    int _frame;

    public void SetScript(string spec)
    {
        foreach (string part in spec.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            int colon = part.IndexOf(':');
            if (colon > 0 && int.TryParse(part[..colon], out int frame)) _script.Add((frame, part[(colon + 1)..]));
        }
    }

    public void AfterFrame()
    {
        _frame++;
        foreach (Keys held in _scriptHeld) OnKeyUp(new KeyboardKeyEventArgs(held, 0, 0, false));
        _scriptHeld.Clear();
        foreach ((int frame, string action) in _script)
        {
            if (frame != _frame) continue;
            Log.Info("MPHDesktop", $"script frame {frame}: {action}");
            if (action.StartsWith("click@", StringComparison.Ordinal))
            {
                string[] xy = action[6..].Split(';');
                float x = float.Parse(xy[0], System.Globalization.CultureInfo.InvariantCulture);
                float y = float.Parse(xy[1], System.Globalization.CultureInfo.InvariantCulture);
                ScriptClick(x, y);
            }
            else if (action is "wheelup" or "wheeldown")
            {
                KeyboardMouse.AddScroll(action == "wheelup" ? 1 : -1); // one notch, as OnMouseWheel in gameplay
            }
            else if (Enum.TryParse(action, ignoreCase: true, out Keys key))
            {
                OnKeyDown(new KeyboardKeyEventArgs(key, 0, 0, false));
                _scriptHeld.Add(key);
            }
        }
    }

    void ScriptClick(float x, float y)
    {
        switch (_app.TopScreen)
        {
        case FrontendRenderer front: front.Post(s => s.TouchCanvas(x, y)); break;
        case CampaignRenderer game when game.PauseMenuOpen: game.PauseMenuInput(p => p.Touch(x, y)); break;
        case CampaignRenderer game when game.ShipMenuOpen: game.ShipTouch(x, y, 0); game.ShipTouch(x, y, 2); break;
        case PageScreen page: page.Click(x, y); break;
        }
    }

    // ---- keyboard ----

    // ---- the control customizer waiting for a button (RecompMenus.CaptureDevice) ----

    // "pad" / "keys" while the customizer on the top screen (the front end, or the campaign's pause overlay) waits
    string? CaptureDevice() => _app.TopScreen switch
    {
        FrontendRenderer front => front.CaptureDevice,
        CampaignRenderer game => game.PauseMenu?.CaptureDevice,
        _ => null,
    };

    void CaptureButton(string name)
    {
        switch (_app.TopScreen)
        {
        case FrontendRenderer front: front.Post(s => s.Recomp.CaptureInput(name)); break;
        case CampaignRenderer game: game.PauseMenuInput(p => p.CaptureInput(name)); break;
        }
    }

    void CancelCapture()
    {
        switch (_app.TopScreen)
        {
        case FrontendRenderer front: front.Post(s => s.Recomp.CancelCapture()); break;
        case CampaignRenderer game: game.PauseMenuInput(p => p.CancelCapture()); break;
        }
    }

    static string? MouseName(MouseButton b) => b switch
    {
        MouseButton.Left => "Mouse.Left", MouseButton.Right => "Mouse.Right", MouseButton.Middle => "Mouse.Middle",
        MouseButton.Button4 => "Mouse.Button4", MouseButton.Button5 => "Mouse.Button5", _ => null,
    };

    void OnKeyDown(KeyboardKeyEventArgs e)
    {
        // the customizer waits: a key is the answer (the keyboard & mouse page), Esc keeps the buttons as they are
        if (CaptureDevice() is string device)
        {
            if (!e.IsRepeat)
            {
                if (e.Key == Keys.Escape) CancelCapture();
                else if (device == "keys") CaptureButton(e.Key.ToString());
            }
            return;
        }
        if ((e.Key == Keys.F11 || (e.Key == Keys.Enter && e.Alt)) && !e.IsRepeat)
        {
            _app.ToggleFullscreen();
            return;
        }
        switch (_app.TopScreen)
        {
        case FrontendRenderer front:
            MenuKey(e, key => front.Post(s => s.Press(key)), (dx, dy) => front.Post(s => s.Navigate(dx, dy)));
            break;
        case CampaignRenderer game:
            GameKeyDown(game, e);
            break;
        case PageScreen page:
            if (page is LobbyScreen lobby && lobby.WantsBackspace && e.Key == Keys.Backspace)
            {
                page.Key(e.Key);
                return;
            }
            page.Key(e.Key);
            if (e.Key == Keys.Escape || e.Key == Keys.Backspace)
            {
                if (!e.IsRepeat) page.Back();
                return;
            }
            MenuKey(e, key =>
            {
                if (key == MenuKeys.A) page.Press();
                else if (key == MenuKeys.B) page.Back();
            }, (dx, dy) => page.Move(dx, -dy));
            break;
        }
    }

    void OnKeyUp(KeyboardKeyEventArgs e)
    {
        if (_app.TopScreen is CampaignRenderer game)
        {
            if (e.Key == Keys.F3) game.Pad.Set(PadButton.ThumbL, false);
            if (PadKey(e.Key) is PadButton b) game.Pad.Set(b, false);
        }
    }

    // the menus' keys: a press once, moves on every repeat (as a held D-pad on Android)
    static void MenuKey(KeyboardKeyEventArgs e, Action<MenuKeys> press, Action<int, int> navigate)
    {
        switch (e.Key)
        {
        case Keys.Up or Keys.W: navigate(0, 1); return;
        case Keys.Down or Keys.S: navigate(0, -1); return;
        case Keys.Left or Keys.A: navigate(-1, 0); return;
        case Keys.Right or Keys.D: navigate(1, 0); return;
        }
        if (e.IsRepeat) return;
        MenuKeys key = e.Key switch
        {
            Keys.Enter or Keys.KeyPadEnter or Keys.Space => MenuKeys.A,
            Keys.Escape or Keys.Backspace => MenuKeys.B,
            Keys.X => MenuKeys.X,
            Keys.Y => MenuKeys.Y,
            Keys.Q or Keys.PageUp => MenuKeys.L,
            Keys.E or Keys.PageDown => MenuKeys.R,
            Keys.Tab => MenuKeys.Start,
            _ => MenuKeys.None,
        };
        if (key != MenuKeys.None) press(key);
    }

    // the keys that stand in for controller buttons on the game's own screens (the map, the ship, a cutscene): the
    // arrows are the D-pad everywhere; the rest only off the game itself, where MphRead's controls don't apply
    PadButton? PadKey(Keys key)
    {
        switch (key)
        {
        case Keys.Up: return PadButton.DpadUp;
        case Keys.Down: return PadButton.DpadDown;
        case Keys.Left: return PadButton.DpadLeft;
        case Keys.Right: return PadButton.DpadRight;
        }
        if (_app.Game is { InGameplay: true }) return null;
        return key switch
        {
            Keys.Enter or Keys.KeyPadEnter or Keys.Space => PadButton.A,
            Keys.Backspace => PadButton.B,
            Keys.X => PadButton.X,
            Keys.Y => PadButton.Y,
            Keys.Q => PadButton.L1,
            Keys.E => PadButton.R1,
            _ => null,
        };
    }

    void GameKeyDown(CampaignRenderer game, KeyboardKeyEventArgs e)
    {
        if (e.Key == Keys.Escape)
        {
            if (!e.IsRepeat) game.BackPressed(); // Android's Back: B on the game's screens, else the pause menu
            return;
        }
        if (e.Key == Keys.Tab)
        {
            if (!e.IsRepeat) game.StartPressed(); // MphRead's pause key: the pause menu (Start)
            return;
        }
        if (e.Key == Keys.M && !game.PauseMenuOpen)
        {
            if (!e.IsRepeat) game.SelectPressed(); // the map (Select)
            return;
        }
        if (game.PauseMenuOpen)
        {
            MenuKey(e, key => game.PauseMenuInput(p => p.Press(key)), (dx, dy) => game.PauseMenuInput(p => p.Navigate(dx, dy)));
            return;
        }
        if (e.Key == Keys.F3)
        {
            game.Pad.Set(PadButton.ThumbL, true); // the status line cycles on the release (as L3)
            return;
        }
        if (PadKey(e.Key) is PadButton b)
        {
            game.Pad.Set(b, true);
        }
    }

    // ---- mouse ----

    void OnMouseDown(MouseButtonEventArgs e)
    {
        if (CaptureDevice() is string device)
        {
            // a mouse button is the answer on the keyboard & mouse page; waiting for a controller button, a click is a
            // touch elsewhere: it keeps the buttons as they are
            if (device == "keys" && MouseName(e.Button) is string name) CaptureButton(name);
            else if (device == "pad") CancelCapture();
            return;
        }
        float x = _w.MouseState.X, y = _w.MouseState.Y;
        switch (_app.TopScreen)
        {
        case FrontendRenderer front:
            if (e.Button == MouseButton.Left) front.Post(s => s.TouchCanvas(x, y)); // the DS touch screen
            else if (e.Button == MouseButton.Right) front.Post(s => s.Press(MenuKeys.B));
            break;
        case CampaignRenderer game:
            if (_captured) break; // playing: KeyboardMouseInput reads the buttons
            if (e.Button != MouseButton.Left)
            {
                if (e.Button == MouseButton.Right && game.PauseMenuOpen) game.PauseMenuInput(p => p.Press(MenuKeys.B));
                break;
            }
            if (game.PauseMenuOpen) game.PauseMenuInput(p => p.Touch(x, y));
            else if (game.MapOpen) game.MapTouch(x, y, 0);
            else if (game.ShipMenuOpen) game.ShipTouch(x, y, 0);
            else game.Pad.Set(PadButton.A, true); // a cutscene or a ship page: a click is A (skips, next)
            break;
        case PageScreen page:
            if (e.Button == MouseButton.Left) page.Click(x, y);
            else if (e.Button == MouseButton.Right) page.Back();
            break;
        }
    }

    void OnMouseUp(MouseButtonEventArgs e)
    {
        if (_app.TopScreen is not CampaignRenderer game || _captured || e.Button != MouseButton.Left) return;
        float x = _w.MouseState.X, y = _w.MouseState.Y;
        if (game.MapOpen) game.MapTouch(x, y, 2);
        else if (game.ShipMenuOpen) game.ShipTouch(x, y, 2);
        game.Pad.Set(PadButton.A, false);
    }

    void OnMouseMove(MouseMoveEventArgs e)
    {
        switch (_app.TopScreen)
        {
        case CampaignRenderer game:
            if (_captured)
            {
                game.Pad.AddAim(e.DeltaX, e.DeltaY); // MphRead's mouse units: one per pixel
            }
            else if (_w.MouseState.IsButtonDown(MouseButton.Left))
            {
                if (game.MapOpen) game.MapTouch(e.X, e.Y, 1); // drags turn the map
                else if (game.ShipMenuOpen) game.ShipTouch(e.X, e.Y, 1); // the ship's slider and weapon drags
            }
            break;
        case PageScreen page:
            page.Hover(e.X, e.Y);
            break;
        }
    }

    void OnMouseWheel(MouseWheelEventArgs e)
    {
        if (CaptureDevice() == "keys")
        {
            CaptureButton(e.OffsetY > 0 ? "Wheel.Up" : "Wheel.Down");
            return;
        }
        switch (_app.TopScreen)
        {
        case CampaignRenderer game:
            if (_captured)
            {
                KeyboardMouse.AddScroll(e.OffsetY); // MphRead: wheel up / down = previous / next weapon
            }
            else if (game.MapOpen)
            {
                // the map zooms with L / R (L2 / R2)
                game.Pad.Set(e.OffsetY > 0 ? PadButton.L1 : PadButton.R1, true);
                _wheelFrames = 4;
            }
            break;
        case PageScreen page:
            page.Wheel(e.OffsetY);
            break;
        }
    }

    // ---- gamepads (the first one connected) ----

    static float Dead(float v) => Math.Abs(v) < 0.15f ? 0f : v;
    static int Step(float v, float threshold) => v >= threshold ? 1 : v <= -threshold ? -1 : 0;

    // GLFW's gamepad layout (SDL's database: Xbox names)
    enum GamepadButton { A, B, X, Y, LeftBumper, RightBumper, Back, Start, Guide, LeftThumb, RightThumb, DPadUp, DPadRight, DPadDown, DPadLeft }
    enum GamepadAxis { LeftX, LeftY, RightX, RightY, LeftTrigger, RightTrigger }

    readonly bool[] _now = new bool[15];
    readonly float[] _axes = new float[6];

    unsafe bool ReadGamepad()
    {
        for (int jid = 0; jid <= 15; jid++)
        {
            if (GLFW.JoystickIsGamepad(jid) && GLFW.GetGamepadState(jid, out GamepadState state))
            {
                for (int i = 0; i < 15; i++) _now[i] = state.Buttons[i] != 0;
                for (int i = 0; i < 6; i++) _axes[i] = state.Axes[i];
                return true;
            }
        }
        return false;
    }

    void PollGamepad()
    {
        if (!ReadGamepad()) return;
        bool[] now = _now;
        bool Btn(GamepadButton b) => now[(int)b];
        float Axis(GamepadAxis a) => _axes[(int)a];
        bool Pressed(GamepadButton b) => now[(int)b] && !_padPrev[(int)b];
        float lx = Axis(GamepadAxis.LeftX), ly = Axis(GamepadAxis.LeftY), rx = Axis(GamepadAxis.RightX), ry = Axis(GamepadAxis.RightY);
        float l2 = (Axis(GamepadAxis.LeftTrigger) + 1) / 2, r2 = (Axis(GamepadAxis.RightTrigger) + 1) / 2;
        int hx = (Btn(GamepadButton.DPadRight) ? 1 : 0) - (Btn(GamepadButton.DPadLeft) ? 1 : 0);
        int hy = (Btn(GamepadButton.DPadDown) ? 1 : 0) - (Btn(GamepadButton.DPadUp) ? 1 : 0); // +1 = down
        int sx = Step(lx, 0.6f), sy = Step(ly, 0.6f);
        if (CaptureDevice() is string device)
        {
            // the customizer waits: on the controller page the first new press (a trigger past half way) is the answer;
            // Start / Back keep the buttons as they are. Nothing reaches the menu meanwhile.
            if (device == "pad")
            {
                string? name = null;
                foreach ((GamepadButton b, PadButton pb) in PadMap)
                {
                    if (Pressed(b))
                    {
                        name = pb.ToString();
                        break;
                    }
                }
                if (name == null && l2 >= 0.5f && _prevL2 < 0.5f) name = nameof(PadButton.L2);
                if (name == null && r2 >= 0.5f && _prevR2 < 0.5f) name = nameof(PadButton.R2);
                if (name != null) CaptureButton(name);
                else if (Pressed(GamepadButton.Start) || Pressed(GamepadButton.Back)) CancelCapture();
            }
            else if (Pressed(GamepadButton.Start) || Pressed(GamepadButton.Back)) CancelCapture();
            _hatX = hx; _hatY = hy; _stickX = sx; _stickY = sy;
            _prevL2 = l2; _prevR2 = r2;
            Array.Copy(now, _padPrev, 15);
            return;
        }
        // once per push, like the game's menus on Android (the D-pad and the left stick)
        void Pushes(Action<int, int> move)
        {
            if (hx != _hatX && hx != 0) move(hx, 0);
            if (hy != _hatY && hy != 0) move(0, hy);
            if (sx != _stickX && sx != 0) move(sx, 0);
            if (sy != _stickY && sy != 0) move(0, sy);
        }
        switch (_app.TopScreen)
        {
        case FrontendRenderer front:
            Pushes((dx, dy) => front.Post(s => s.Navigate(dx, -dy)));
            foreach ((GamepadButton b, MenuKeys key) in MenuButtons)
            {
                if (Pressed(b)) front.Post(s => s.Press(key));
            }
            break;
        case CampaignRenderer game:
            GamepadGame(game, now, Pressed, lx, ly, rx, ry, l2, r2, hx, hy, sx, sy);
            break;
        case PageScreen page:
            Pushes((dx, dy) => page.Move(dx, dy));
            if (Pressed(GamepadButton.A)) page.Press();
            if (Pressed(GamepadButton.B)) page.Back();
            if (page is CreditsScreen credits && Math.Abs(ry) > 0.3f) credits.ScrollBy(ry * 0.5f);
            break;
        }
        _hatX = hx; _hatY = hy; _stickX = sx; _stickY = sy;
        _prevL2 = l2; _prevR2 = r2;
        Array.Copy(now, _padPrev, 15);
    }

    // the gamepad's buttons the game reads, as the hosts' PadButtons (the triggers are axes; Start / Back go on their own)
    static readonly (GamepadButton Button, PadButton Pad)[] PadMap =
    {
        (GamepadButton.A, PadButton.A), (GamepadButton.B, PadButton.B), (GamepadButton.X, PadButton.X),
        (GamepadButton.Y, PadButton.Y), (GamepadButton.LeftBumper, PadButton.L1), (GamepadButton.RightBumper, PadButton.R1),
        (GamepadButton.LeftThumb, PadButton.ThumbL), (GamepadButton.RightThumb, PadButton.ThumbR),
        (GamepadButton.DPadUp, PadButton.DpadUp), (GamepadButton.DPadDown, PadButton.DpadDown),
        (GamepadButton.DPadLeft, PadButton.DpadLeft), (GamepadButton.DPadRight, PadButton.DpadRight),
    };

    static readonly (GamepadButton, MenuKeys)[] MenuButtons =
    {
        (GamepadButton.A, MenuKeys.A), (GamepadButton.B, MenuKeys.B), (GamepadButton.X, MenuKeys.X), (GamepadButton.Y, MenuKeys.Y),
        (GamepadButton.Start, MenuKeys.Start), (GamepadButton.Back, MenuKeys.Select),
        (GamepadButton.LeftBumper, MenuKeys.L), (GamepadButton.RightBumper, MenuKeys.R),
    };

    // the Android CampaignActivity's controller handling, with GLFW's gamepad in place of Android's key and motion events
    void GamepadGame(CampaignRenderer game, bool[] now, Func<GamepadButton, bool> pressed, float lx, float ly, float rx, float ry,
        float l2, float r2, int hx, int hy, int sx, int sy)
    {
        if (pressed(GamepadButton.Start)) game.StartPressed();
        if (pressed(GamepadButton.Back)) game.SelectPressed();
        bool l3 = now[(int)GamepadButton.LeftThumb], r3 = now[(int)GamepadButton.RightThumb];
        if ((pressed(GamepadButton.LeftThumb) && r3) || (pressed(GamepadButton.RightThumb) && l3)) game.ToggleGunMenu();
        if (game.PauseMenuOpen)
        {
            // the pause menu: buttons as menu keys, the D-pad and left stick move the focus once per push
            foreach ((GamepadButton b, MenuKeys key) in MenuButtons)
            {
                if (b is GamepadButton.Start or GamepadButton.Back) continue;
                if (pressed(b)) game.PauseMenuInput(p => p.Press(key));
            }
            if (hx != _hatX && hx != 0) game.PauseMenuInput(p => p.Navigate(hx, 0));
            if (hy != _hatY && hy != 0) game.PauseMenuInput(p => p.Navigate(0, -hy));
            if (sx != _stickX && sx != 0) game.PauseMenuInput(p => p.Navigate(sx, 0));
            if (sy != _stickY && sy != 0) game.PauseMenuInput(p => p.Navigate(0, -sy));
            return;
        }
        PadState pad = game.Pad;
        pad.Lx = Dead(lx);
        pad.Ly = Dead(ly);
        pad.RawRx = rx;
        pad.RawRy = ry;
        pad.Rx = Dead(rx);
        pad.Ry = Dead(ry);
        pad.L2 = l2;
        pad.R2 = r2;
        foreach ((GamepadButton b, PadButton p) in PadMap)
        {
            bool down = now[(int)b];
            if (down != _padPrev[(int)b]) pad.Set(p, down);
        }
    }
}
