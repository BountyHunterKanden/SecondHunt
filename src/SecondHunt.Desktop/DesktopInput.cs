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
//     X / Y, the mouse clicks (the DS touch screen). On the name keyboard and the address keypad the keys type (Backspace
//     deletes, Enter is ENTER / OK, Esc backs out; the arrows still move over the on-screen keys).
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
    float _menuRy; // the right stick's up / down in the menus: scrolls settings and CONTROLS (FrontendSession.ScrollStick)
    float _prevL2, _prevR2; // the triggers (0..1) last poll, for the control customizer's capture
    int _wheelFrames; // mouse wheel in the map: L1 / R1 held a few frames

    public DesktopInput(DesktopApp app, NativeWindow window)
    {
        _app = app;
        _w = window;
        KeyboardMouse.KeyDown = key => _captured && _w.KeyboardState.IsKeyDown(key);
        KeyboardMouse.MouseDown = button => button == MouseButton.Left && _scriptFire > 0 || _captured && _w.MouseState.IsButtonDown(button);
        _w.KeyDown += OnKeyDown;
        _w.KeyUp += OnKeyUp;
        _w.TextInput += e => OnText((char)e.Unicode);
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
        SetCapture(game != null && game.InGameplay && !game.DialogUp && _w.IsFocused); // a dialog frees it for its buttons
        PollGamepad();
        if (game != null && _wheelFrames > 0 && --_wheelFrames == 0)
        {
            game.Pad.Set(PadButton.L1, false);
            game.Pad.Set(PadButton.R1, false);
        }
    }

    // --script "<frame>:<key>,<frame>:click@<x>;<y>,<frame>:wheeldown,<frame>:text:<chars>,...": synthetic key presses (down
    // that frame, up the next), wheel notches, clicks and typed characters (no ',' in a text:)
    // through the same handlers, for scripted test runs of the menus and screens (dev builds)
    readonly List<(int Frame, string Action)> _script = new();
    readonly List<Keys> _scriptHeld = new();
    int _frame;
    int _scriptFire; // "fire": the game's left click held this many more frames (a replay test's "press FIRE to begin")

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
        if (_scriptFire > 0) _scriptFire--;
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
            else if (action.StartsWith("text:", StringComparison.Ordinal))
            {
                foreach (char c in action[5..]) OnText(c); // typed, as the window's TextInput
            }
            else if (action == "fire")
            {
                _scriptFire = 3;
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

    // a typed character (the window's TextInput, or a script's text:)
    void OnText(char c)
    {
        if (_app.TopScreen is FrontendRenderer front && front.Typing) front.Post(s => s.TypeChar(c)); // the name keyboard / keypad
        else if (_app.TopScreen is PageScreen page) page.Text(c);
        else if (_app.TopScreen is CampaignRenderer game && game.ChatLineOpen) game.ChatLineChar(c); // MatchChat.cs
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
            if (front.Typing && TypingKey(front, e)) break;
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
        // a key let go while the customizer waits: the capture commits once every pressed key is up (combos)
        if (CaptureDevice() == "keys")
        {
            CaptureButton("-" + e.Key);
            return;
        }
        if (_app.TopScreen is CampaignRenderer game)
        {
            if (e.Key == Keys.F3) game.Pad.Set(PadButton.ThumbL, false);
            if (e.Key == Keys.F4) game.Pad.Set(PadButton.ThumbR, false);
            if (PadKey(e.Key) is PadButton b) game.Pad.Set(b, false);
            if (ReplayKey(game, e.Key) is PadButton rb) game.Pad.Set(rb, false);
        }
    }

    // a replay's bar (MatchReplay.cs) from the keyboard: Enter A (press the picked button), Space X (pause / play),
    // Q / E L1 / R1 (10 s back / forward), Z / C L2 / R2 (slower / faster), Y the camera, H B (hide the bar and HUD);
    // the arrows are the D-pad
    static PadButton? ReplayKey(CampaignRenderer game, Keys key) => !game.ReplayPlaying ? null : key switch
    {
        Keys.Enter or Keys.KeyPadEnter => PadButton.A,
        Keys.Space => PadButton.X,
        Keys.Q => PadButton.L1,
        Keys.E => PadButton.R1,
        Keys.Z => PadButton.L2,
        Keys.C => PadButton.R2,
        Keys.Y => PadButton.Y,
        Keys.H => PadButton.B,
        _ => null,
    };

    // the name keyboard / address keypad up (FrontendSession.Typing): characters come through TextInput (TypeChar), so
    // the letter keys don't move the focus or press X / Y / L / R; Backspace deletes (repeating), Enter is ENTER / OK, Esc
    // backs out (B: CANCEL); the arrows still move over the on-screen keys. True when the key was taken here.
    static bool TypingKey(FrontendRenderer front, KeyboardKeyEventArgs e)
    {
        switch (e.Key)
        {
        case Keys.Up or Keys.Down or Keys.Left or Keys.Right:
            return false;
        case Keys.Backspace:
            front.Post(s => s.TypeKey(backspace: true));
            return true;
        case Keys.Enter or Keys.KeyPadEnter:
            if (!e.IsRepeat) front.Post(s => s.TypeKey(backspace: false));
            return true;
        case Keys.Escape:
            if (!e.IsRepeat) front.Post(s => s.Press(MenuKeys.B));
            return true;
        default:
            return true; // (typed: TextInput)
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
        game.KeyboardHints = true; // a replay bar names the keys (MatchReplay.cs)
        // an online match's typed chat (MatchChat.cs): Enter / T opens a line on the HUD; while it's open the keys are its
        if (game.ChatLineOpen)
        {
            if (e.Key is Keys.Enter or Keys.KeyPadEnter or Keys.Escape or Keys.Backspace)
                game.ChatLineKey(enter: e.Key is Keys.Enter or Keys.KeyPadEnter, escape: e.Key == Keys.Escape);
            return;
        }
        if (e.Key is Keys.Enter or Keys.KeyPadEnter or Keys.T && game.TouchChat && !game.PauseMenuOpen)
        {
            if (!e.IsRepeat) game.OpenMatchChat();
            return;
        }
        if (e.Key == Keys.Escape)
        {
            if (!e.IsRepeat) game.BackPressed(); // Android's Back: B on the game's screens, else the pause menu
            return;
        }
        // Tab is the keyboard SCOREBOARD key since 2026-10-11 (KeyboardMouseInput); Esc opens the pause menu
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
        if (e.Key == Keys.F4)
        {
            game.Pad.Set(PadButton.ThumbR, true); // R3: a replay's free cam toggle (MatchReplay.cs; no desktop R3 key otherwise)
            return;
        }
        if (ReplayKey(game, e.Key) is PadButton rb)
        {
            game.Pad.Set(rb, true);
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
            if (e.Button == MouseButton.Left) front.Post(s => s.TouchCanvas(x, y, 0)); // the DS touch screen (a drag scrolls settings)
            else if (e.Button == MouseButton.Right) front.Post(s => s.Press(MenuKeys.B));
            break;
        case CampaignRenderer game:
            game.KeyboardHints = true;
            if (_captured) break; // playing: KeyboardMouseInput reads the buttons
            if (game.DialogUp && !game.PauseMenuOpen && !game.MapOpen && !game.ShipMenuOpen)
            {
                // a dialog's OK / YES / NO / page arrows (CampaignRenderer.DialogTouch.cs)
                if (e.Button is MouseButton.Left or MouseButton.Right) game.DialogClick(x, y, e.Button == MouseButton.Right);
                break;
            }
            if (e.Button != MouseButton.Left)
            {
                if (e.Button == MouseButton.Right && game.PauseMenuOpen) game.PauseMenuInput(p => p.Press(MenuKeys.B));
                break;
            }
            if (game.PauseMenuOpen) game.PauseMenuInput(p => p.Touch(x, y, 0));
            else if (game.ReplayPlaying && !game.MatchOver) game.ReplayTouch(x, y, 0); // the replay bar (MatchReplay.cs)
            else if (game.MapOpen) game.MapTouch(x, y, 0);
            else if (game.ShipMenuOpen) game.ShipTouch(x, y, 0);
            else if (game.ShipPanelUp)
            {
                // the save prompt's YES / NO, game over's rows: a click on one chooses it
                if (_app.PanelLineAt(x, y) is { } hit) game.ShipPanelTap(hit.Text, hit.Line);
            }
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
        if (CaptureDevice() == "keys")
        {
            if (MouseName(e.Button) is string name) CaptureButton("-" + name);
            return;
        }
        if (_app.TopScreen is FrontendRenderer front && e.Button == MouseButton.Left)
        {
            float fx = _w.MouseState.X, fy = _w.MouseState.Y;
            front.Post(s => s.TouchCanvas(fx, fy, 2)); // (a click held on settings' rows lands here)
            return;
        }
        if (_app.TopScreen is not CampaignRenderer game || _captured || e.Button != MouseButton.Left) return;
        float x = _w.MouseState.X, y = _w.MouseState.Y;
        if (game.PauseMenuOpen) game.PauseMenuInput(p => p.Touch(x, y, 2));
        else if (game.MapOpen) game.MapTouch(x, y, 2);
        else if (game.ShipMenuOpen) game.ShipTouch(x, y, 2);
        else if (game.ReplayPlaying) game.ReplayTouch(x, y, 2);
        game.Pad.Set(PadButton.A, false);
    }

    void OnMouseMove(MouseMoveEventArgs e)
    {
        switch (_app.TopScreen)
        {
        case FrontendRenderer front when _w.MouseState.IsButtonDown(MouseButton.Left):
            front.Post(s => s.TouchCanvas(e.X, e.Y, 1)); // a held click drags settings and CONTROLS
            break;
        case CampaignRenderer game:
            if (_captured)
            {
                game.Pad.AddAim(e.DeltaX, e.DeltaY); // MphRead's mouse units: one per pixel
            }
            else if (_w.MouseState.IsButtonDown(MouseButton.Left))
            {
                if (game.PauseMenuOpen) game.PauseMenuInput(p => p.Touch(e.X, e.Y, 1)); // drags scroll settings
                else if (game.MapOpen) game.MapTouch(e.X, e.Y, 1); // drags turn the map
                else if (game.ShipMenuOpen) game.ShipTouch(e.X, e.Y, 1); // the ship's slider and weapon drags
                else if (game.ReplayPlaying) game.ReplayTouch(e.X, e.Y, 1); // the replay bar's scrub drag
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
        case FrontendRenderer front:
            front.Post(s => s.ScrollWheel(e.OffsetY)); // settings and CONTROLS scroll
            break;
        case CampaignRenderer game:
            if (_captured)
            {
                KeyboardMouse.AddScroll(e.OffsetY); // MphRead: wheel up / down = previous / next weapon
            }
            else if (game.PauseMenuOpen)
            {
                game.PauseMenuInput(p => p.ScrollWheel(e.OffsetY)); // settings and CONTROLS scroll
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

    // a gamepad was there at the last poll (the settings tabs show L1 / R1 then: RecompMenus.PadConnected, Program.cs)
    public static volatile bool PadSeen;

    unsafe bool ReadGamepad()
    {
        for (int jid = 0; jid <= 15; jid++)
        {
            if (GLFW.JoystickIsGamepad(jid) && GLFW.GetGamepadState(jid, out GamepadState state))
            {
                for (int i = 0; i < 15; i++) _now[i] = state.Buttons[i] != 0;
                for (int i = 0; i < 6; i++) _axes[i] = state.Axes[i];
                return PadSeen = true;
            }
        }
        return PadSeen = false;
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
            // the customizer waits: on the controller page every press (a trigger past half way; Back = Select, a
            // customizer function since 2026-10-11) and every release goes to it ("-" + name), so held buttons make a
            // combo that commits once all are let go; Start keeps the buttons as they are. Nothing reaches the menu meanwhile.
            if (device == "pad")
            {
                bool any = false;
                void Send(bool down, bool was, string name)
                {
                    if (down && !was) { CaptureButton(name); any = true; }
                    else if (!down && was) CaptureButton("-" + name);
                }
                foreach ((GamepadButton b, PadButton pb) in PadMap) Send(now[(int)b], _padPrev[(int)b], pb.ToString());
                Send(Btn(GamepadButton.Back), _padPrev[(int)GamepadButton.Back], nameof(PadButton.Select));
                Send(l2 >= 0.5f, _prevL2 >= 0.5f, nameof(PadButton.L2));
                Send(r2 >= 0.5f, _prevR2 >= 0.5f, nameof(PadButton.R2));
                if (!any && Pressed(GamepadButton.Start)) CancelCapture();
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
            if (MenuStick(ry)) front.Post(s => s.ScrollStick(ry));
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

    // the right stick's up / down for the menus, sent when it changes (FrontendSession.ScrollStick scrolls while it's held)
    bool MenuStick(float ry)
    {
        if (Math.Abs(ry - _menuRy) <= 0.05f && (Math.Abs(ry) < 0.3f) == (Math.Abs(_menuRy) < 0.3f)) return false;
        _menuRy = ry;
        return true;
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
        // Back = Select, held: the customizer's "map / scoreboard" unless moved -- the campaign's map (CampaignMap.MapFrame), a
        // match's live scoreboard (MatchChat.cs MatchScoreboardHeld)
        game.Pad.Set(PadButton.Select, now[(int)GamepadButton.Back]);
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
            if (MenuStick(ry)) game.PauseMenuInput(p => p.ScrollStick(ry));
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
            if (down && !_padPrev[(int)b]) game.KeyboardHints = false; // a replay bar names the pad's buttons
        }
        if (hx != 0 || hy != 0) game.KeyboardHints = false;
    }
}
