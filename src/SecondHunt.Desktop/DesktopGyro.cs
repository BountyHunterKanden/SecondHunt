using System.Numerics;
using System.Text.Json;
using MphRecomp.App;
using MphRecomp.App.Platform;
using MphRecomp.Input;

namespace SecondHunt.Desktop;

// Gyro aim on a PC (thread D's D6b): a controller's own gyroscope through SDL3 (Sdl3Pads reads it), with the same
// settings, math and feel as the Android handhelds' (MphRead.Android/CampaignGyro.cs; Input/GyroAim.cs). The pad's
// sensors run only while gyro aim is on and a game is up (Wanted; DesktopInput hands this to Sdl3Pads.Motion then).
// Pads that SDL reads with their gyro: DualSense, DualShock 4, Switch Pro, Joy-Cons, 8BitDo pads in Switch mode, the
// Steam Controller. Steam Input or DS4Windows in between hide it (they present an Xbox pad, which has none).
// Some pads drift a little at rest and don't calibrate themselves: a second held dead still (on a desk) re-learns the
// offset (Aim.Bias).
// Dev: --gyrolog logs (MPHGyro) once a second: sample rate, mean/peak raw rate, the degrees the game got.
internal sealed class DesktopGyro : IGyroAimSource
{
    public const string Tag = "MPHGyro";

    public readonly GyroAim Aim = new();
    volatile bool _enabled, _inGame;
    long _lastSampleMs = long.MinValue / 2;
    readonly bool _log = Environment.GetCommandLineArgs().Contains("--gyrolog");

    public DesktopGyro() => ApplySettings();

    // the pad's sensors should run: DesktopInput sets Sdl3Pads.Motion while this holds
    public bool Wanted => _enabled && _inGame;
    // samples are arriving (the renderer reads no gyro otherwise)
    public bool Running => Wanted && Environment.TickCount64 - _lastSampleMs < 200;
    public bool OnlyWhenZoomed { get; private set; } // "zoom" mode
    public bool ZoomScaling => true;
    GyroAim IGyroAimSource.Aim => Aim;

    // a game started (true) or ended (false); a start re-reads the settings, which the menus may have changed
    public void SetInGame(bool inGame)
    {
        if (inGame) ApplySettings();
        _inGame = inGame;
        Aim.Reset();
    }

    // the pause menu closed: RECOMP SETTINGS may have changed gyro aim
    public void ReloadSettings() => ApplySettings();

    void ApplySettings()
    {
        string mode = "off";
        int percent = 150;
        string axis = "player"; // (PadGyroAxis: a PC pad's own; Android's GyroAxis is the handheld's)
        // smoothing 0 when the key is missing, as on Android (a save from before it keeps its old feel)
        int pitch = 0, smoothing = 0, accel = 0;
        bool invertPitch = false, invertYaw = false;
        float deadband = 2f;
        try
        {
            if (File.Exists(DesktopPaths.Settings))
            {
                using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(DesktopPaths.Settings));
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
                    else if (p.NameEquals("PadGyroAxis") && p.Value.ValueKind == JsonValueKind.String)
                        axis = p.Value.GetString() ?? axis;
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
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            Log.Warn(Tag, "settings unreadable, gyro defaults: " + ex.Message);
        }
        mode = mode.ToLowerInvariant();
        _enabled = mode is "on" or "zoom";
        OnlyWhenZoomed = mode == "zoom";
        GyroAimOptions o = Aim.Options;
        o.Sensitivity = Math.Clamp(percent, 10, 1000) / 100f;
        o.TurnAxis = axis.ToLowerInvariant() switch
        {
            "player" => GyroTurnAxis.Player,
            "roll" => GyroTurnAxis.Roll,
            _ => GyroTurnAxis.Yaw
        };
        o.PitchSensitivity = pitch > 0 ? Math.Clamp(pitch, 10, 1000) / 100f : null;
        o.InvertPitch = invertPitch;
        o.InvertYaw = invertYaw;
        o.TightenDps = Math.Clamp(deadband, 0f, 30f);
        o.SmoothDps = 0.3f * Math.Clamp(smoothing, 0, 100);
        o.AccelSlowScale = 1f - Math.Clamp(accel, 0, 95) / 100f;
        o.AccelSlowDps = 0f;
        o.AccelFastDps = 75f;
        Log.Info(Tag, $"gyro aim {(_enabled ? (OnlyWhenZoomed ? "ZOOM ONLY" : "ON") : "off")}: sensitivity "
            + $"{o.Sensitivity:0.00}x (pitch {(o.PitchSensitivity ?? o.Sensitivity):0.00}x), turn axis {o.TurnAxis}, "
            + $"invert {(o.InvertPitch ? "P" : "")}{(o.InvertYaw ? "Y" : "")}, "
            + $"deadband {o.TightenDps:0.0} deg/s, smoothing {o.SmoothDps:0} deg/s, accel slow x{o.AccelSlowScale:0.00}");
    }

    // one gyro sample from the pad (rad/s, GyroAim's axes), held dt seconds (0: the first after a gap)
    public void AddGyro(Vector3 rad, float dt)
    {
        _lastSampleMs = Environment.TickCount64;
        Calibrate(rad, dt);
        Aim.AddSample(rad, dt);
        if (_log) LogSample(rad, dt);
    }

    public void AddAccel(Vector3 accel, float dt) => Aim.AddAccelerometer(accel, dt);

    // a second of samples all within 1.5 deg/s of each other and near zero: the pad is lying still, their mean is its
    // drift
    Vector3 _calMin, _calMax, _calSum;
    int _calCount;
    double _calDt;

    void Calibrate(Vector3 rad, float dt)
    {
        if (!(dt > 0f && dt <= 0.1f)) return;
        if (_calCount == 0)
        {
            _calMin = _calMax = rad;
        }
        _calMin = Vector3.Min(_calMin, rad);
        _calMax = Vector3.Max(_calMax, rad);
        _calSum += rad;
        _calCount++;
        _calDt += dt;
        Vector3 range = (_calMax - _calMin) * (180f / MathF.PI);
        if (MathF.Max(range.X, MathF.Max(range.Y, range.Z)) > 1.5f)
        {
            _calCount = 0;
            _calSum = default;
            _calDt = 0;
            return;
        }
        if (_calDt < 1.0) return;
        Vector3 mean = _calSum / _calCount;
        if ((mean * (180f / MathF.PI)).Length() < 3f)
        {
            if (_log || (mean - Aim.Bias).Length() * (180f / MathF.PI) > 0.2f)
            {
                Vector3 d = mean * (180f / MathF.PI);
                Log.Info(Tag, $"held still: drift ({d.X:0.00}, {d.Y:0.00}, {d.Z:0.00}) deg/s taken off");
            }
            Aim.Bias = mean;
        }
        _calCount = 0;
        _calSum = default;
        _calDt = 0;
    }

    // --gyrolog: one line a second, as Android's --es gyrolog 1
    int _logSamples;
    Vector3 _logSum;
    float _logPeak;
    double _logDt;
    Vector2 _logTaken;

    void LogSample(Vector3 rad, float dt)
    {
        var dps = rad * (180f / MathF.PI);
        _logSamples++;
        _logSum += dps;
        _logPeak = MathF.Max(_logPeak, dps.Length());
        _logDt += dt;
        if (_logDt < 1.0) return;
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

    public void NoteTaken(Vector2 degrees)
    {
        if (_log) _logTaken += degrees;
    }
}
