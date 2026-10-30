using System;
using System.Text.Json;
using Android.App;
using Android.Content;
using Android.Hardware;
using Android.OS;
using Android.Runtime;
using Android.Util;
using Android.Views;
using MphRead.Entities;
using MphRecomp.Input;

namespace MphRecomp.App;

// Gyro aiming for the campaign: the device's own gyroscope, added on top of the right stick (the stick still turns the
// camera exactly as before; the gyro is for fine aim). The math lives in MphRecomp.Core (Input/GyroAim.cs); this file
// is only the Android sensor plumbing: CampaignActivity calls CampaignGyro.Attach once, from OnCreate, and the
// renderer reads the sensor (IGyroAimSource) once per sim step (MphRecomp.App CampaignGyroAim.cs). Sensor start/stop
// follows the activity's resume/pause through lifecycle callbacks registered here, so nothing else in the activity
// changes.
//
// Settings (recomp_settings.json, read by name so this works before and after the settings screen grows the rows):
//   "Gyro": "off" | "on" (always) | "zoom" (only while zoomed or in the scan visor, like Zelda's bow aiming)
//   "GyroSensitivity": percent (150 = the view turns 1.5x as far as the device did)
//   "GyroAxis": "yaw" (the screen's own vertical: GyroWiki's handheld pick) | "player" (the world's vertical) | "roll"
//   tuning (thread D 10-24, all off or the old fixed value by default): "GyroPitchSensitivity" percent (0 = same as
//   GyroSensitivity), "GyroInvertPitch"/"GyroInvertYaw", "GyroDeadband" deg/s tightening, "GyroSmoothing" percent (100 = soft tiered below 30 deg/s), "GyroAccel"
//   percent (slow motion scaled down by that much, full speed by 75 deg/s)
// While zoomed the gyro also gets focal-length scaling (GyroMph.ZoomScale) so the Imperialist's zoom stays steady.
// A controller with its own gyroscope (DualSense, DualShock 4, Switch Pro, an 8BitDo in Switch mode; Android 12+ reads
// their motion sensors through InputDevice.SensorManager) takes over from the handheld's while it's connected
// (PadGyros below). Its axes are the pad's own, so no screen rotation, and it turns about "PadGyroAxis" ("player" by
// default, the PC pad's setting too: owner 10-30, a pad's yaw tilts with the grip). A pad's gyro isn't factory
// trimmed like a phone's, so a second held still takes its drift off (as SecondHunt.Desktop/DesktopGyro.cs).
// Dev overrides: --es gyro on|off|zoom, --es gyrosens 150, --es gyroaxis roll, --es gyrozoom off (MphRead's own zoom
// scaling only, for A/B), --es gyrolog 1 (logcat MPHGyro: once a second, raw rate stats and the degrees handed to
// the game -- lay the device down to read its noise floor).
internal static class CampaignGyro
{
    public static void Attach(Activity activity, CampaignRenderer renderer, string settingsPath, Intent? intent)
    {
        PadGyros.Init(activity);
        var sensor = new GyroSensor(activity, settingsPath, intent);
        renderer.Gyro = sensor;
        if (!sensor.HasGyroscope && !PadGyros.Supported)
        {
            Log.Info(GyroSensor.Tag, "no gyroscope on this device: gyro aim unavailable");
            return;
        }
        activity.Application!.RegisterActivityLifecycleCallbacks(new GyroLifecycle(activity, sensor));
        // RECOMP SETTINGS in the pause menu may have flipped gyro on/off or changed its sensitivity
        if (renderer.PauseMenu != null)
        {
            renderer.PauseMenu.Resumed += sensor.ReloadSettings;
        }
    }
}

internal sealed class GyroSensor : Java.Lang.Object, ISensorEventListener, IGyroAimSource
{
    public const string Tag = "MPHGyro";
    const int GyroPeriodUs = 5000;   // 200 Hz: the most Android 12+ allows without HIGH_SAMPLING_RATE_SENSORS
    const int AccelPeriodUs = 10000; // gravity only needs to be roughly right

    readonly Activity _activity;
    readonly SensorManager? _manager;
    readonly Sensor? _gyroscope, _accelerometer;
    SensorManager? _listening; // the manager the listener is on: the handheld's or the pad's
    volatile bool _onPad;      // samples come from a controller (its own axes, its drift taken off)
    float _padScale = 1f;      // to rad/s (a driver reporting deg/s)
    GyroTurnAxis _deviceAxis = GyroTurnAxis.Yaw, _padAxis = GyroTurnAxis.Player;
    readonly string _settingsPath;
    readonly Intent? _intent;
    HandlerThread? _thread;
    Handler? _handler;
    SurfaceOrientation _rotation;
    long _lastGyroNs, _lastAccelNs;
    bool _enabled, _registered, _resumed, _log;
    readonly object _stateLock = new(); // the UI thread (lifecycle) and the GL thread (pause menu) both start/stop

    public readonly GyroAim Aim = new();
    public bool HasGyroscope => _gyroscope != null;
    public bool Running => _registered;
    public volatile bool OnlyWhenZoomed; // "zoom" mode
    public volatile bool ZoomScaling = true;
    GyroAim IGyroAimSource.Aim => Aim;
    bool IGyroAimSource.OnlyWhenZoomed => OnlyWhenZoomed;
    bool IGyroAimSource.ZoomScaling => ZoomScaling;

    public GyroSensor(Activity activity, string settingsPath, Intent? intent)
    {
        _activity = activity;
        _settingsPath = settingsPath;
        _intent = intent;
        _manager = (SensorManager?)activity.GetSystemService(Context.SensorService);
        _gyroscope = _manager?.GetDefaultSensor(SensorType.Gyroscope);
        _accelerometer = _manager?.GetDefaultSensor(SensorType.Accelerometer);
        _log = intent?.GetStringExtra("gyrolog") == "1";
        ApplySettings();
        PadGyros.Changed += PadsChanged;
        if (_gyroscope != null)
        {
            Log.Info(Tag, $"gyroscope: {_gyroscope.Name} ({_gyroscope.Vendor}), max {_gyroscope.MaximumRange:0.#} rad/s, "
                + $"resolution {_gyroscope.Resolution:0.#####}; accelerometer: {_accelerometer?.Name ?? "none"}");
        }
    }

    // called on the GL thread when the pause menu closes
    public void ReloadSettings()
    {
        lock (_stateLock)
        {
            ApplySettings();
            Update();
        }
    }

    // the activity came to the front / left it (GyroLifecycle)
    public void SetResumed(bool resumed)
    {
        lock (_stateLock)
        {
            _resumed = resumed;
            Update();
        }
    }

    void Update()
    {
        if (_enabled && _resumed) Start();
        else Stop();
    }

    void ApplySettings()
    {
        string mode = "off";
        int percent = 150;
        string axis = "yaw", padAxis = "player";
        // smoothing 0 when the key is missing: a save from before it keeps its old feel (owner 10-26; fresh saves write 20)
        int pitch = 0, smoothing = 0, accel = 0;
        bool invertPitch = false, invertYaw = false;
        float deadband = 2f;
        try
        {
            if (System.IO.File.Exists(_settingsPath))
            {
                using JsonDocument doc = JsonDocument.Parse(System.IO.File.ReadAllText(_settingsPath));
                foreach (JsonProperty p in doc.RootElement.EnumerateObject())
                {
                    if (p.NameEquals("Gyro"))
                        mode = p.Value.ValueKind switch
                        {
                            JsonValueKind.String => p.Value.GetString() ?? mode,
                            JsonValueKind.True => "on",
                            JsonValueKind.False => "off",
                            _ => mode
                        };
                    else if (p.NameEquals("GyroSensitivity") && p.Value.TryGetInt32(out int s))
                        percent = s;
                    else if (p.NameEquals("GyroAxis") && p.Value.ValueKind == JsonValueKind.String)
                        axis = p.Value.GetString() ?? axis;
                    else if (p.NameEquals("PadGyroAxis") && p.Value.ValueKind == JsonValueKind.String)
                        padAxis = p.Value.GetString() ?? padAxis;
                    else if (p.NameEquals("GyroPitchSensitivity") && p.Value.TryGetInt32(out int ps))
                        pitch = ps;
                    else if (p.NameEquals("GyroSmoothing") && p.Value.TryGetInt32(out int sm))
                        smoothing = sm;
                    else if (p.NameEquals("GyroAccel") && p.Value.TryGetInt32(out int ac))
                        accel = ac;
                    else if (p.NameEquals("GyroDeadband") && p.Value.TryGetSingle(out float db))
                        deadband = db;
                    else if (p.NameEquals("GyroInvertPitch") && p.Value.ValueKind is JsonValueKind.True or JsonValueKind.False)
                        invertPitch = p.Value.GetBoolean();
                    else if (p.NameEquals("GyroInvertYaw") && p.Value.ValueKind is JsonValueKind.True or JsonValueKind.False)
                        invertYaw = p.Value.GetBoolean();
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn(Tag, "settings unreadable, gyro defaults: " + ex.Message);
        }
        // an explicit dev-launcher extra wins over the settings file
        mode = (_intent?.GetStringExtra("gyro") ?? mode).ToLowerInvariant();
        if (Int32.TryParse(_intent?.GetStringExtra("gyrosens"), out int sens)) percent = sens;
        axis = _intent?.GetStringExtra("gyroaxis") ?? axis;
        padAxis = _intent?.GetStringExtra("gyroaxis") ?? padAxis;
        ZoomScaling = _intent?.GetStringExtra("gyrozoom") != "off";

        // (the source, handheld or pad, is picked when the sensors start: Start)
        _enabled = mode is "on" or "zoom" or "1" or "true";
        OnlyWhenZoomed = mode == "zoom";
        Aim.Options.Sensitivity = Math.Clamp(percent, 10, 1000) / 100f;
        static GyroTurnAxis Axis(string a) => a.ToLowerInvariant() switch
        {
            "player" => GyroTurnAxis.Player,
            "roll" => GyroTurnAxis.Roll,
            _ => GyroTurnAxis.Yaw
        };
        _deviceAxis = Axis(axis);
        _padAxis = Axis(padAxis);
        Aim.Options.TurnAxis = _onPad ? _padAxis : _deviceAxis;
        GyroAimOptions o = Aim.Options;
        o.PitchSensitivity = pitch > 0 ? Math.Clamp(pitch, 10, 1000) / 100f : null;
        o.InvertPitch = invertPitch;
        o.InvertYaw = invertYaw;
        o.TightenDps = Math.Clamp(deadband, 0f, 30f);
        o.SmoothDps = 0.3f * Math.Clamp(smoothing, 0, 100);
        o.AccelSlowScale = 1f - Math.Clamp(accel, 0, 95) / 100f;
        o.AccelSlowDps = 0f;
        o.AccelFastDps = 75f;
        Log.Info(Tag, $"gyro aim {(_enabled ? (OnlyWhenZoomed ? "ZOOM ONLY" : "ON") : "off")}: sensitivity "
            + $"{o.Sensitivity:0.00}x (pitch {(o.PitchSensitivity ?? o.Sensitivity):0.00}x), turn axis {_deviceAxis} (pad {_padAxis}), "
            + $"invert {(o.InvertPitch ? "P" : "")}{(o.InvertYaw ? "Y" : "")}, "
            + $"deadband {o.TightenDps:0.0} deg/s, smoothing {o.SmoothDps:0} deg/s, accel slow x{o.AccelSlowScale:0.00}, "
            + $"zoom scaling {(ZoomScaling ? "on" : "off")}");
    }

    void Start()
    {
        if (!_enabled || _registered)
        {
            return;
        }
        // a connected controller's gyro first, else the handheld's own
        SensorManager? manager = _manager;
        Sensor? gyro = _gyroscope, accel = _accelerometer;
        bool pad = false;
        string padName = "";
        if (PadGyros.Find() is { } device && OperatingSystem.IsAndroidVersionAtLeast(31))
        {
            manager = device.SensorManager;
            gyro = manager.GetDefaultSensor(SensorType.Gyroscope);
            accel = manager.GetDefaultSensor(SensorType.Accelerometer);
            pad = gyro != null;
            padName = device.Name ?? "";
        }
        if (!pad)
        {
            (manager, gyro, accel) = (_manager, _gyroscope, _accelerometer);
        }
        if (manager == null || gyro == null)
        {
            return;
        }
        if (_thread == null)
        {
            _thread = new HandlerThread("MPHGyro", (int)Android.OS.ThreadPriority.Display);
            _thread.Start();
            _handler = new Handler(_thread.Looper!);
        }
        _onPad = pad;
        // (a rad/s gyro tops out near 35; a range in the hundreds or thousands is deg/s)
        _padScale = pad && gyro.MaximumRange > 100f ? MathF.PI / 180f : 1f;
        Aim.Options.TurnAxis = pad ? _padAxis : _deviceAxis;
        _rotation = DisplayRotation();
        _lastGyroNs = _lastAccelNs = 0;
        _calCount = 0;
        _calSum = default;
        _calDt = 0;
        Aim.Reset();
        Aim.Bias = default;
        manager.RegisterListener(this, gyro, (SensorDelay)GyroPeriodUs, _handler);
        if (accel != null)
        {
            manager.RegisterListener(this, accel, (SensorDelay)AccelPeriodUs, _handler);
        }
        _listening = manager;
        _registered = true;
        Log.Info(Tag, pad
            ? $"sensors on: controller \"{padName}\" ({gyro.Name}, max {gyro.MaximumRange:0.#}{(_padScale != 1f ? " deg/s" : " rad/s")}; "
                + $"accelerometer {accel?.Name ?? "none"}), turn axis {_padAxis}"
            : $"sensors on (display {_rotation})");
    }

    void Stop()
    {
        if (!_registered)
        {
            return;
        }
        _listening?.UnregisterListener(this);
        _listening = null;
        _registered = false;
        _onPad = false;
        Aim.Reset();
        Aim.Bias = default;
    }

    // a controller came or went (main thread): move to or off its gyro
    void PadsChanged()
    {
        lock (_stateLock)
        {
            if (_registered && _onPad == (PadGyros.Find() != null)) return;
            Stop();
            Update();
        }
    }

    public void Shutdown()
    {
        lock (_stateLock)
        {
            _resumed = false;
            Stop();
        }
        PadGyros.Changed -= PadsChanged;
        _thread?.QuitSafely();
        _thread = null;
        _handler = null;
    }

    SurfaceOrientation DisplayRotation()
    {
        if (OperatingSystem.IsAndroidVersionAtLeast(30))
        {
            return _activity.Display?.Rotation ?? SurfaceOrientation.Rotation0;
        }
#pragma warning disable CA1422 // Display.Rotation via WindowManager: the only way before API 30
        return _activity.WindowManager?.DefaultDisplay?.Rotation ?? SurfaceOrientation.Rotation0;
#pragma warning restore CA1422
    }

    // Sensor axes are fixed to the device's natural orientation; the game wants them fixed to the screen as it is
    // drawn. Z (out of the screen) never changes. (Android's canonical remap; a landscape-natural handheld is Rotation0.)
    static System.Numerics.Vector3 ToScreen(SurfaceOrientation rotation, float x, float y, float z) => rotation switch
    {
        SurfaceOrientation.Rotation90 => new(-y, x, z),
        SurfaceOrientation.Rotation180 => new(-x, -y, z),
        SurfaceOrientation.Rotation270 => new(y, -x, z),
        _ => new(x, y, z)
    };

    public void OnSensorChanged(SensorEvent? e)
    {
        // e.Sensor and e.Values are JNI field reads, and each Values read wraps the Java array in a NEW managed peer:
        // read each once (300 events a second; the peers kept the GC bridge busy -- 2026-09-30 frame-drop check)
        if (e == null)
        {
            return;
        }
        Sensor? sensor = e.Sensor;
        IList<float>? values = e.Values;
        if (sensor == null || values == null || values.Count < 3)
        {
            return;
        }
        bool pad = _onPad;
        var v = pad ? new System.Numerics.Vector3(values[0], values[1], values[2])
            : ToScreen(_rotation, values[0], values[1], values[2]);
        (values as IDisposable)?.Dispose();
        SensorType type = sensor.Type;
        if (type == SensorType.Gyroscope)
        {
            float dt = _lastGyroNs == 0 ? 0f : (e.Timestamp - _lastGyroNs) * 1e-9f;
            _lastGyroNs = e.Timestamp;
            if (pad)
            {
                v *= _padScale;
                Calibrate(v, dt);
            }
            Aim.AddSample(v, dt);
            if (_log) LogSample(v, dt);
        }
        else if (type == SensorType.Accelerometer)
        {
            float dt = _lastAccelNs == 0 ? 0f : (e.Timestamp - _lastAccelNs) * 1e-9f;
            _lastAccelNs = e.Timestamp;
            Aim.AddAccelerometer(v, dt);
        }
    }

    public void OnAccuracyChanged(Sensor? sensor, [GeneratedEnum] SensorStatus accuracy) { }

    // a controller's gyro: a second of samples all within 1.5 deg/s of each other and near zero means the pad is lying
    // still, and their mean is its drift (SecondHunt.Desktop/DesktopGyro.cs Calibrate; a phone's gyro is trimmed already)
    System.Numerics.Vector3 _calMin, _calMax, _calSum;
    int _calCount;
    double _calDt;

    void Calibrate(System.Numerics.Vector3 rad, float dt)
    {
        if (!(dt > 0f && dt <= 0.1f)) return;
        if (_calCount == 0)
        {
            _calMin = _calMax = rad;
        }
        _calMin = System.Numerics.Vector3.Min(_calMin, rad);
        _calMax = System.Numerics.Vector3.Max(_calMax, rad);
        _calSum += rad;
        _calCount++;
        _calDt += dt;
        var range = (_calMax - _calMin) * (180f / MathF.PI);
        if (MathF.Max(range.X, MathF.Max(range.Y, range.Z)) > 1.5f)
        {
            _calCount = 0;
            _calSum = default;
            _calDt = 0;
            return;
        }
        if (_calDt < 1.0) return;
        var mean = _calSum / _calCount;
        if ((mean * (180f / MathF.PI)).Length() < 3f)
        {
            if (_log || (mean - Aim.Bias).Length() * (180f / MathF.PI) > 0.2f)
            {
                var d = mean * (180f / MathF.PI);
                Log.Info(Tag, $"controller held still: drift ({d.X:0.00}, {d.Y:0.00}, {d.Z:0.00}) deg/s taken off");
            }
            Aim.Bias = mean;
        }
        _calCount = 0;
        _calSum = default;
        _calDt = 0;
    }

    // --es gyrolog 1: one line a second -- sample rate, mean/peak raw rate (deg/s; the mean of a device lying still is
    // its residual bias, the peak its noise), and the aim degrees the game actually received
    int _logSamples;
    System.Numerics.Vector3 _logSum;
    float _logPeak;
    double _logDt;
    System.Numerics.Vector2 _logTaken;
    readonly object _logLock = new();

    void LogSample(System.Numerics.Vector3 rad, float dt)
    {
        var dps = rad * (180f / MathF.PI);
        lock (_logLock)
        {
            _logSamples++;
            _logSum += dps;
            _logPeak = MathF.Max(_logPeak, dps.Length());
            _logDt += dt;
            if (_logDt < 1.0)
            {
                return;
            }
            var mean = _logSum / _logSamples;
            var up = Aim.Up;
            Log.Info(Tag, $"{_logSamples / _logDt:0} Hz  mean ({mean.X:0.00}, {mean.Y:0.00}, {mean.Z:0.00}) deg/s  peak {_logPeak:0.00}  "
                + $"up ({up.X:0.00}, {up.Y:0.00}, {up.Z:0.00})  game got turn {_logTaken.X:0.00} pitch {_logTaken.Y:0.00} deg");
            _logSamples = 0;
            _logSum = default;
            _logPeak = 0;
            _logDt = 0;
            _logTaken = default;
        }
    }

    public void NoteTaken(System.Numerics.Vector2 degrees)
    {
        if (!_log) return;
        lock (_logLock) _logTaken += degrees;
    }
}

// start the sensors when the campaign is in front, stop them when it isn't (they cost battery and wake the SoC)
internal sealed class GyroLifecycle : Java.Lang.Object, Application.IActivityLifecycleCallbacks
{
    readonly Activity _activity;
    readonly GyroSensor _sensor;

    public GyroLifecycle(Activity activity, GyroSensor sensor)
    {
        _activity = activity;
        _sensor = sensor;
    }

    public void OnActivityResumed(Activity activity) { if (activity == _activity) _sensor.SetResumed(true); }
    public void OnActivityPaused(Activity activity) { if (activity == _activity) _sensor.SetResumed(false); }

    public void OnActivityDestroyed(Activity activity)
    {
        if (activity != _activity) return;
        _sensor.Shutdown();
        activity.Application?.UnregisterActivityLifecycleCallbacks(this);
    }

    public void OnActivityCreated(Activity activity, Bundle? savedInstanceState) { }
    public void OnActivitySaveInstanceState(Activity activity, Bundle outState) { }
    public void OnActivityStarted(Activity activity) { }
    public void OnActivityStopped(Activity activity) { }
}

// Controllers with a gyroscope (Android 12+: InputDevice.SensorManager). Init once per activity is fine (the listener is
// registered once); Changed fires on the main thread when one comes or goes. The menus' gyro axis row edits PadGyroAxis
// while one is connected (RecompMenus.PadGyro).
internal static class PadGyros
{
    static Listener? s_listener;
    static volatile bool s_connected;
    public static event Action? Changed;
    public static bool Supported => OperatingSystem.IsAndroidVersionAtLeast(31);
    public static bool Connected => s_connected;

    public static void Init(Context context)
    {
        if (!Supported || s_listener != null) return;
        var input = (Android.Hardware.Input.InputManager?)context.ApplicationContext?.GetSystemService(Context.InputService);
        if (input == null) return;
        s_listener = new Listener();
        input.RegisterInputDeviceListener(s_listener, new Handler(Looper.MainLooper!));
        MphRecomp.Frontend.RecompMenus.PadGyro = () => s_connected;
        Refresh();
    }

    // the first real (non-virtual) input device with a gyroscope of its own
    public static InputDevice? Find()
    {
        if (!OperatingSystem.IsAndroidVersionAtLeast(31)) return null;
        foreach (int id in InputDevice.GetDeviceIds() ?? Array.Empty<int>())
        {
            InputDevice? device = InputDevice.GetDevice(id);
            if (device == null || device.IsVirtual) continue;
            if (device.SensorManager.GetDefaultSensor(SensorType.Gyroscope) != null) return device;
        }
        return null;
    }

    static void Refresh()
    {
        InputDevice? pad = Find();
        bool connected = pad != null;
        if (connected != s_connected)
        {
            Log.Info(GyroSensor.Tag, connected ? $"controller gyro connected: {pad!.Name}" : "controller gyro gone");
        }
        s_connected = connected;
        Changed?.Invoke();
    }

    sealed class Listener : Java.Lang.Object, Android.Hardware.Input.InputManager.IInputDeviceListener
    {
        public void OnInputDeviceAdded(int deviceId) => Refresh();
        public void OnInputDeviceRemoved(int deviceId) => Refresh();
        public void OnInputDeviceChanged(int deviceId) => Refresh();
    }
}
