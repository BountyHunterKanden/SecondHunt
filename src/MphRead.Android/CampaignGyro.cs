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
// While zoomed the gyro also gets focal-length scaling (GyroMph.ZoomScale) so the Imperialist's zoom stays steady.
// Dev overrides: --es gyro on|off|zoom, --es gyrosens 150, --es gyroaxis roll, --es gyrozoom off (MphRead's own zoom
// scaling only, for A/B), --es gyrolog 1 (logcat MPHGyro: once a second, raw rate stats and the degrees handed to
// the game -- lay the device down to read its noise floor).
internal static class CampaignGyro
{
    public static void Attach(Activity activity, CampaignRenderer renderer, string settingsPath, Intent? intent)
    {
        var sensor = new GyroSensor(activity, settingsPath, intent);
        renderer.Gyro = sensor;
        if (!sensor.HasGyroscope)
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
        string axis = "yaw";
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
        ZoomScaling = _intent?.GetStringExtra("gyrozoom") != "off";

        _enabled = mode is "on" or "zoom" or "1" or "true" && _gyroscope != null;
        OnlyWhenZoomed = mode == "zoom";
        Aim.Options.Sensitivity = Math.Clamp(percent, 10, 1000) / 100f;
        Aim.Options.TurnAxis = axis.ToLowerInvariant() switch
        {
            "player" => GyroTurnAxis.Player,
            "roll" => GyroTurnAxis.Roll,
            _ => GyroTurnAxis.Yaw
        };
        Log.Info(Tag, $"gyro aim {(_enabled ? (OnlyWhenZoomed ? "ZOOM ONLY" : "ON") : "off")}: sensitivity "
            + $"{Aim.Options.Sensitivity:0.00}x, turn axis {Aim.Options.TurnAxis}, zoom scaling {(ZoomScaling ? "on" : "off")}");
    }

    void Start()
    {
        if (!_enabled || _registered || _manager == null || _gyroscope == null)
        {
            return;
        }
        if (_thread == null)
        {
            _thread = new HandlerThread("MPHGyro", (int)Android.OS.ThreadPriority.Display);
            _thread.Start();
            _handler = new Handler(_thread.Looper!);
        }
        _rotation = DisplayRotation();
        _lastGyroNs = _lastAccelNs = 0;
        Aim.Reset();
        _manager.RegisterListener(this, _gyroscope, (SensorDelay)GyroPeriodUs, _handler);
        if (_accelerometer != null)
        {
            _manager.RegisterListener(this, _accelerometer, (SensorDelay)AccelPeriodUs, _handler);
        }
        _registered = true;
        Log.Info(Tag, $"sensors on (display {_rotation})");
    }

    void Stop()
    {
        if (!_registered)
        {
            return;
        }
        _manager?.UnregisterListener(this);
        _registered = false;
        Aim.Reset();
    }

    public void Shutdown()
    {
        lock (_stateLock)
        {
            _resumed = false;
            Stop();
        }
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
        var v = ToScreen(_rotation, values[0], values[1], values[2]);
        (values as IDisposable)?.Dispose();
        SensorType type = sensor.Type;
        if (type == SensorType.Gyroscope)
        {
            float dt = _lastGyroNs == 0 ? 0f : (e.Timestamp - _lastGyroNs) * 1e-9f;
            _lastGyroNs = e.Timestamp;
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
