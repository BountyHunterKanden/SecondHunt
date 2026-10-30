using System.Runtime.InteropServices;
using MphRecomp.App.Platform;
using OpenTK.Windowing.GraphicsLibraryFramework;

namespace SecondHunt.Desktop;

// Gamepads through SDL3 (thread D 10-24, D6a; docs/SDL3_CONTROLLERS.md, GitHub #6). SDL's HIDAPI drivers do the
// Switch Pro controller's USB handshake and calibrate its sticks, which GLFW's DirectInput mapping can't. SDL3.dll is
// loaded at run time from the app's own folder only; without it (or with --glfw-pads) Open returns null and
// DesktopInput reads pads through GLFW as before. One reader or the other, never both: GLFW's joystick code only starts
// on its first joystick call, which DesktopInput no longer makes once this is open.
// SDL is started for gamepads alone (no video, no window), its gamepad events off: each poll calls SDL_UpdateGamepads
// and reads the state, so nothing queues up but the gyro's samples (D6b), drained every poll. GLFW keeps the window,
// keyboard and mouse.
// Read fills DesktopInput's arrays in GLFW's gamepad layout (Xbox names by position: A = the bottom button), sticks and
// triggers -1..1 as GLFW gives them, so the rest of DesktopInput is unchanged.
internal sealed unsafe class Sdl3Pads : IDisposable
{
    const uint InitGamepad = 0x00002000; // SDL_INIT_GAMEPAD (brings in joystick + events)

    // SDL_GamepadButton in GLFW's order: A B X Y LB RB Back Start Guide L3 R3 Up Right Down Left
    static readonly int[] GlfwOrder = { 0, 1, 2, 3, 9, 10, 4, 6, 5, 7, 8, 11, 14, 12, 13 };
    const int ButtonSouth = 0, LabelB = 2;

    readonly IntPtr _lib;
    readonly delegate* unmanaged[Cdecl]<void> _update;
    readonly delegate* unmanaged[Cdecl]<int*, uint*> _getGamepads;
    readonly delegate* unmanaged[Cdecl]<uint, IntPtr> _open;
    readonly delegate* unmanaged[Cdecl]<IntPtr, void> _close;
    readonly delegate* unmanaged[Cdecl]<IntPtr, byte> _connected;
    readonly delegate* unmanaged[Cdecl]<IntPtr, int, byte> _button;
    readonly delegate* unmanaged[Cdecl]<IntPtr, int, short> _axis;
    readonly delegate* unmanaged[Cdecl]<IntPtr, int, int> _label;
    readonly delegate* unmanaged[Cdecl]<IntPtr, int> _type;
    readonly delegate* unmanaged[Cdecl]<IntPtr, byte*> _name;
    readonly delegate* unmanaged[Cdecl]<void*, void> _free;
    readonly delegate* unmanaged[Cdecl]<void> _quit;
    // the pad's motion sensors (D6b): SDL_GamepadHasSensor, SDL_SetGamepadSensorEnabled, SDL_GetGamepadSensorDataRate,
    // SDL_GetGamepadSensorData, SDL_GetGamepadID, SDL_PeepEvents
    readonly delegate* unmanaged[Cdecl]<IntPtr, int, byte> _hasSensor;
    readonly delegate* unmanaged[Cdecl]<IntPtr, int, byte, byte> _setSensor;
    readonly delegate* unmanaged[Cdecl]<IntPtr, int, float> _sensorRate;
    readonly delegate* unmanaged[Cdecl]<IntPtr, int, float*, int, byte> _sensorData;
    readonly delegate* unmanaged[Cdecl]<IntPtr, uint> _padId;
    readonly delegate* unmanaged[Cdecl]<void*, int, int, uint, uint, int> _peep;

    IntPtr _pad; // the pad being read (the first one SDL lists)
    int _lookIn; // polls until the next look for a pad (none open)

    public int Version { get; }
    // the open pad's own name and type (SDL_GamepadType), for the log, --padtest and AUTO layout
    public string? PadName { get; private set; }
    public int PadType { get; private set; }
    // the pad's A is on the right (SDL labels its bottom button B): Switch Pro, Joy-Cons, GameCube...
    public bool NintendoLabels { get; private set; }

    // gyro aim (D6b): the open pad has a gyroscope (DualSense, DualShock 4, Switch Pro, Joy-Cons, 8BitDo Ultimate in
    // Switch mode...). Its sensors run only while Motion is set (DesktopGyro: gyro aim on, in a game); each poll hands it
    // every sample SDL queued since the last one, with the sensor's own timestamps. SDL's gamepad sensor axes are the
    // screen's as GyroAim takes them (X right, Y up, Z toward the player; rad/s, counterclockwise positive).
    public bool HasGyro { get; private set; }
    public DesktopGyro? Motion { get; set; }
    // the latest gyro sample (rad/s) and how many have come since Open, for --padtest
    public System.Numerics.Vector3 LastGyro { get; private set; }
    public long GyroSamples { get; private set; }
    const int SensorAccel = 1, SensorGyro = 2;
    const uint EventSensorUpdate = 0x659; // SDL_EVENT_GAMEPAD_SENSOR_UPDATE
    const int EventSize = 128, PeepGet = 2; // sizeof(SDL_Event), SDL_GETEVENT
    uint _id; // the open pad's SDL_JoystickID
    bool _sensorsOn, _testGyro;
    ulong _lastGyroNs, _lastAccelNs;
    long _lastEventMs; // when the last sensor event came; with none for half a second the sensors are polled instead
    readonly System.Diagnostics.Stopwatch _pollClock = new();

    Sdl3Pads(IntPtr lib)
    {
        _lib = lib;
        void* Fn(string name) => (void*)NativeLibrary.GetExport(lib, name);
        _update = (delegate* unmanaged[Cdecl]<void>)Fn("SDL_UpdateGamepads");
        _getGamepads = (delegate* unmanaged[Cdecl]<int*, uint*>)Fn("SDL_GetGamepads");
        _open = (delegate* unmanaged[Cdecl]<uint, IntPtr>)Fn("SDL_OpenGamepad");
        _close = (delegate* unmanaged[Cdecl]<IntPtr, void>)Fn("SDL_CloseGamepad");
        _connected = (delegate* unmanaged[Cdecl]<IntPtr, byte>)Fn("SDL_GamepadConnected");
        _button = (delegate* unmanaged[Cdecl]<IntPtr, int, byte>)Fn("SDL_GetGamepadButton");
        _axis = (delegate* unmanaged[Cdecl]<IntPtr, int, short>)Fn("SDL_GetGamepadAxis");
        _label = (delegate* unmanaged[Cdecl]<IntPtr, int, int>)Fn("SDL_GetGamepadButtonLabel");
        _type = (delegate* unmanaged[Cdecl]<IntPtr, int>)Fn("SDL_GetGamepadType");
        _name = (delegate* unmanaged[Cdecl]<IntPtr, byte*>)Fn("SDL_GetGamepadName");
        _free = (delegate* unmanaged[Cdecl]<void*, void>)Fn("SDL_free");
        _quit = (delegate* unmanaged[Cdecl]<void>)Fn("SDL_Quit");
        _hasSensor = (delegate* unmanaged[Cdecl]<IntPtr, int, byte>)Fn("SDL_GamepadHasSensor");
        _setSensor = (delegate* unmanaged[Cdecl]<IntPtr, int, byte, byte>)Fn("SDL_SetGamepadSensorEnabled");
        _sensorRate = (delegate* unmanaged[Cdecl]<IntPtr, int, float>)Fn("SDL_GetGamepadSensorDataRate");
        _sensorData = (delegate* unmanaged[Cdecl]<IntPtr, int, float*, int, byte>)Fn("SDL_GetGamepadSensorData");
        _padId = (delegate* unmanaged[Cdecl]<IntPtr, uint>)Fn("SDL_GetGamepadID");
        _peep = (delegate* unmanaged[Cdecl]<void*, int, int, uint, uint, int>)Fn("SDL_PeepEvents");
        Version = ((delegate* unmanaged[Cdecl]<int>)Fn("SDL_GetVersion"))();
    }

    // SDL3 from the app's folder, started for gamepads; null (GLFW reads the pads) when it isn't there or won't start
    public static Sdl3Pads? Open(string[]? args = null)
    {
        if (args != null && args.Contains("--glfw-pads")) return null;
        string path = Path.Combine(AppContext.BaseDirectory, "SDL3.dll");
        if (!File.Exists(path) || !NativeLibrary.TryLoad(path, out IntPtr lib))
        {
            Log.Info("MPHPad", "no SDL3.dll next to the game: pads through GLFW");
            return null;
        }
        try
        {
            var setHint = (delegate* unmanaged[Cdecl]<byte*, byte*, byte>)(void*)NativeLibrary.GetExport(lib, "SDL_SetHint");
            var init = (delegate* unmanaged[Cdecl]<uint, byte>)(void*)NativeLibrary.GetExport(lib, "SDL_Init");
            var getError = (delegate* unmanaged[Cdecl]<byte*>)(void*)NativeLibrary.GetExport(lib, "SDL_GetError");
            var events = (delegate* unmanaged[Cdecl]<byte, void>)(void*)NativeLibrary.GetExport(lib, "SDL_SetGamepadEventsEnabled");
            var joyEvents = (delegate* unmanaged[Cdecl]<byte, void>)(void*)NativeLibrary.GetExport(lib, "SDL_SetJoystickEventsEnabled");
            // the window is GLFW's, so SDL never sees it get focus: read the pad regardless (we only poll while it's ours)
            Hint(setHint, "SDL_JOYSTICK_ALLOW_BACKGROUND_EVENTS", "1");
            if (init(InitGamepad) == 0)
            {
                Log.Warn("MPHPad", $"SDL3 wouldn't start ({Marshal.PtrToStringUTF8((IntPtr)getError())}): pads through GLFW");
                NativeLibrary.Free(lib);
                return null;
            }
            events(0);
            joyEvents(0);
            // ...but the gyro's samples queued (D6b): a poll's one reading would miss most of a 250-1000 Hz sensor's.
            // Only sensors the game turned on send them, and every poll drains the queue (ReadMotion)
            var eventOn = (delegate* unmanaged[Cdecl]<uint, byte, void>)(void*)NativeLibrary.GetExport(lib, "SDL_SetEventEnabled");
            eventOn(EventSensorUpdate, 1);
            var pads = new Sdl3Pads(lib);
            int v = pads.Version;
            Log.Info("MPHPad", $"pads through SDL {v / 1000000}.{v / 1000 % 1000}.{v % 1000}");
            return pads;
        }
        catch (EntryPointNotFoundException ex)
        {
            Log.Warn("MPHPad", $"SDL3.dll is missing {ex.Message}: pads through GLFW");
            NativeLibrary.Free(lib);
            return null;
        }
    }

    static void Hint(delegate* unmanaged[Cdecl]<byte*, byte*, byte> setHint, string name, string value)
    {
        fixed (byte* n = System.Text.Encoding.UTF8.GetBytes(name + "\0"), v = System.Text.Encoding.UTF8.GetBytes(value + "\0"))
        {
            setHint(n, v);
        }
    }

    // the first pad's buttons (GLFW's 15) and axes (GLFW's 6); false while none is connected
    public bool Read(bool[] buttons, float[] axes)
    {
        _update();
        if (_pad != IntPtr.Zero && _connected(_pad) == 0)
        {
            Log.Info("MPHPad", $"pad gone: {PadName}");
            _close(_pad);
            _pad = IntPtr.Zero;
            PadName = null;
            HasGyro = _sensorsOn = false;
        }
        if (_pad == IntPtr.Zero)
        {
            if (--_lookIn > 0) return false;
            _lookIn = 30; // twice a second (SDL_GetGamepads allocates)
            if (!OpenFirst()) return false;
        }
        for (int i = 0; i < GlfwOrder.Length; i++) buttons[i] = _button(_pad, GlfwOrder[i]) != 0;
        for (int i = 0; i < 4; i++) axes[i] = Math.Max(-1f, _axis(_pad, i) / 32767f); // y positive down, as GLFW
        for (int i = 4; i < 6; i++) axes[i] = _axis(_pad, i) / 32767f * 2f - 1f; // triggers 0..32767 -> GLFW's -1..1
        ReadMotion();
        return true;
    }

    // the gyro and accelerometer while something wants them (Motion, or --padtest): every queued sample, oldest first;
    // the latest reading each poll when the pad sends no sensor events
    void ReadMotion()
    {
        bool want = HasGyro && (Motion != null || _testGyro);
        if (want != _sensorsOn)
        {
            _setSensor(_pad, SensorGyro, want ? (byte)1 : (byte)0);
            if (_hasSensor(_pad, SensorAccel) != 0) _setSensor(_pad, SensorAccel, want ? (byte)1 : (byte)0);
            _sensorsOn = want;
            _lastGyroNs = _lastAccelNs = 0;
            _lastEventMs = Environment.TickCount64;
            _pollClock.Reset();
            if (want) Log.Info("MPHPad", $"gyro on: {_sensorRate(_pad, SensorGyro):0} Hz");
        }
        byte* events = stackalloc byte[16 * EventSize];
        bool any = false;
        int n;
        do
        {
            n = _peep(events, 16, PeepGet, EventSensorUpdate, EventSensorUpdate); // (always drained: none pile up)
            for (int i = 0; i < n; i++)
            {
                byte* e = events + i * EventSize; // SDL_GamepadSensorEvent
                if (!_sensorsOn || *(uint*)(e + 16) != _id) continue;
                ulong ns = *(ulong*)(e + 40); // sensor_timestamp; the event's own when a driver gives none
                if (ns == 0) ns = *(ulong*)(e + 8);
                float* d = (float*)(e + 24);
                Sample(*(int*)(e + 20), new System.Numerics.Vector3(d[0], d[1], d[2]), ns);
                any = true;
            }
        }
        while (n == 16);
        if (!_sensorsOn) return;
        long now = Environment.TickCount64;
        if (any)
        {
            _lastEventMs = now;
            return;
        }
        if (now - _lastEventMs < 500) return;
        // no events: this poll's reading, held since the last poll
        float dt = _pollClock.IsRunning ? (float)_pollClock.Elapsed.TotalSeconds : 0f;
        _pollClock.Restart();
        _lastGyroNs = _lastAccelNs = 0;
        float* v = stackalloc float[3];
        if (_sensorData(_pad, SensorGyro, v, 3) != 0) Polled(SensorGyro, new(v[0], v[1], v[2]), dt);
        if (_sensorData(_pad, SensorAccel, v, 3) != 0) Polled(SensorAccel, new(v[0], v[1], v[2]), dt);
    }

    void Sample(int sensor, System.Numerics.Vector3 v, ulong ns)
    {
        ref ulong last = ref (sensor == SensorGyro ? ref _lastGyroNs : ref _lastAccelNs);
        float dt = last == 0 || ns <= last ? 0f : (ns - last) * 1e-9f;
        last = ns;
        Polled(sensor, v, dt);
    }

    void Polled(int sensor, System.Numerics.Vector3 v, float dt)
    {
        if (sensor == SensorGyro)
        {
            LastGyro = v;
            GyroSamples++;
            Motion?.AddGyro(v, dt);
        }
        else if (sensor == SensorAccel)
        {
            Motion?.AddAccel(v, dt);
        }
    }

    bool OpenFirst()
    {
        int count;
        uint* ids = _getGamepads(&count);
        if (ids == null) return false;
        try
        {
            for (int i = 0; i < count && _pad == IntPtr.Zero; i++) _pad = _open(ids[i]);
        }
        finally
        {
            _free(ids);
        }
        if (_pad == IntPtr.Zero) return false;
        PadName = Marshal.PtrToStringUTF8((IntPtr)_name(_pad)) ?? "gamepad";
        PadType = _type(_pad);
        NintendoLabels = _label(_pad, ButtonSouth) == LabelB;
        _id = _padId(_pad);
        HasGyro = _hasSensor(_pad, SensorGyro) != 0;
        _sensorsOn = false;
        Log.Info("MPHPad", $"pad: {PadName} (SDL type {PadType}{(NintendoLabels ? ", Nintendo labels" : "")}"
            + $"{(HasGyro ? ", gyro" : ", no gyro")})");
        return true;
    }

    public void Dispose()
    {
        if (_pad != IntPtr.Zero) _close(_pad);
        _pad = IntPtr.Zero;
        _quit();
        NativeLibrary.Free(_lib);
    }

    static readonly string[] ButtonNames = { "A", "B", "X", "Y", "LB", "RB", "Back", "Start", "Guide", "L3", "R3", "Up", "Right", "Down", "Left" };

    static string StateLine(bool[] buttons, float[] axes) =>
        $"L {axes[0]:+0.00;-0.00} {axes[1]:+0.00;-0.00}  R {axes[2]:+0.00;-0.00} {axes[3]:+0.00;-0.00}  "
        + $"LT {axes[4]:+0.00;-0.00} RT {axes[5]:+0.00;-0.00}  [{string.Join(" ", ButtonNames.Where((_, b) => buttons[b]))}]";

    static double TestSeconds(string[] args)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == "--padtest" && double.TryParse(args[i + 1], System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out double s)) return s;
        }
        return 10;
    }

    // --padtest (dev builds): what SDL sees, a few seconds of the first pad's state, to stdout (run it redirected).
    // --padtest --glfw-pads: the same through GLFW, the way builds before D6a read pads, to compare a pad old vs new
    public static int Test(string[] args)
    {
        if (args.Contains("--glfw-pads")) return GlfwTest(TestSeconds(args));
        using Sdl3Pads? pads = Open(args);
        if (pads == null)
        {
            Console.WriteLine($"SDL3.dll not found or not started in {AppContext.BaseDirectory}: the game reads pads through GLFW");
            return 1;
        }
        int v = pads.Version;
        Console.WriteLine($"SDL {v / 1000000}.{v / 1000 % 1000}.{v % 1000}");
        var buttons = new bool[15];
        var axes = new float[6];
        string last = "";
        pads._lookIn = 0;
        pads._testGyro = true;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        double seconds = TestSeconds(args);
        bool seen = false;
        double gyroAt = 0;
        long gyroSamples = 0;
        while (clock.Elapsed.TotalSeconds < seconds)
        {
            if (pads.Read(buttons, axes))
            {
                if (!seen)
                {
                    seen = true;
                    Console.WriteLine($"pad: {pads.PadName}, SDL type {pads.PadType}, bottom button labelled "
                        + (pads.NintendoLabels ? "B (Nintendo: AUTO layout swaps A/B, X/Y)" : "A (Xbox positions)"));
                }
                string line = StateLine(buttons, axes);
                if (line != last) Console.WriteLine(line);
                last = line;
                // the gyro twice a second: deg/s on each axis (pitch X, yaw Y, roll Z) and the samples a second
                double t = clock.Elapsed.TotalSeconds;
                if (pads.HasGyro && t - gyroAt >= 0.5)
                {
                    var g = pads.LastGyro * (180f / MathF.PI);
                    Console.WriteLine($"gyro pitch {g.X:+0.0;-0.0} yaw {g.Y:+0.0;-0.0} roll {g.Z:+0.0;-0.0} deg/s  "
                        + $"{(pads.GyroSamples - gyroSamples) / (t - gyroAt):0} Hz");
                    gyroAt = t;
                    gyroSamples = pads.GyroSamples;
                }
            }
            else if (seen)
            {
                seen = false;
                Console.WriteLine("pad gone");
            }
            Thread.Sleep(50);
        }
        if (!seen && last == "") Console.WriteLine("no pad seen");
        return 0;
    }

    // every joystick GLFW sees and whether it has a gamepad mapping (the old reader skipped any that didn't), then the
    // first mapped one's state as the game read it; for the rest, just whether any raw input arrives at all
    static int GlfwTest(double seconds)
    {
        if (!GLFW.Init())
        {
            Console.WriteLine("GLFW wouldn't start");
            return 1;
        }
        Console.WriteLine("GLFW (the pre-D6a reader)");
        var buttons = new bool[15];
        var axes = new float[6];
        var last = new string[16];
        var listed = new bool[16];
        bool any = false;
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (clock.Elapsed.TotalSeconds < seconds)
        {
            GLFW.PollEvents();
            bool read = false;
            for (int jid = 0; jid <= 15; jid++)
            {
                if (!GLFW.JoystickPresent(jid))
                {
                    if (listed[jid]) Console.WriteLine($"joystick {jid} gone");
                    listed[jid] = false;
                    last[jid] = "";
                    continue;
                }
                bool mapped = GLFW.JoystickIsGamepad(jid);
                if (!listed[jid])
                {
                    listed[jid] = any = true;
                    Console.WriteLine($"joystick {jid}: {GLFW.GetJoystickName(jid)}, GUID {GLFW.GetJoystickGUID(jid)}, "
                        + (mapped ? $"gamepad mapping '{GLFW.GetGamepadName(jid)}'" : "NO gamepad mapping: the old reader ignored it"));
                }
                string line;
                if (mapped && !read && GLFW.GetGamepadState(jid, out GamepadState state))
                {
                    read = true;
                    for (int i = 0; i < 15; i++) buttons[i] = state.Buttons[i] != 0;
                    for (int i = 0; i < 6; i++) axes[i] = state.Axes[i];
                    line = StateLine(buttons, axes);
                }
                else
                {
                    float[] raw = GLFW.GetJoystickAxes(jid).ToArray();
                    JoystickInputAction[] rawButtons = GLFW.GetJoystickButtons(jid).ToArray();
                    line = $"raw axes {string.Join(" ", raw.Select(a => a.ToString("+0.00;-0.00", inv)))}  buttons held "
                        + string.Join(" ", rawButtons.Select((b, i) => (b, i)).Where(t => t.b != JoystickInputAction.Release).Select(t => t.i))
                        + (mapped ? "  (not read: the game only read the first mapped pad)" : "");
                }
                if (line != last[jid]) Console.WriteLine($"[{jid}] {line}");
                last[jid] = line;
            }
            Thread.Sleep(50);
        }
        if (!any) Console.WriteLine("no joystick seen");
        GLFW.Terminate();
        return 0;
    }
}
