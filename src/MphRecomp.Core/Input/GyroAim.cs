using System;
using System.Numerics;

// Gyro aiming, platform-neutral: raw angular velocity in, aim degrees out. The Android host feeds it the device's own
// gyroscope; a Windows host could feed it a controller's (SDL reports the same rad/s). The stick keeps turning the
// camera exactly as before and the gyro is ADDED on top of it -- the stick for big turns, the gyro for precision --
// the model GyroWiki (Jibb Smart, JoyShockMapper) recommends and Splatoon / Prime Remastered's hybrid scheme ship.
//
// Pipeline per sample: bias -> turn axis (yaw / player space / roll) + pitch -> tightening (fades out tiny rates, so a
// steady hand holds the reticle still) -> optional soft tiered smoothing (off by default: GyroWiki skips it for 3D
// games) -> real-world sensitivity (1.0 = the view turns exactly as far as the device did) -> integrated to degrees.
// No acceleration and no dead zone: the same wrist movement always turns the same amount.
// Sources: gyrowiki.jibbsmart.com (the gyro is a mouse; player space gyro and alternatives explained),
// JibbSmart/GamepadMotionHelpers (player space, relax factor 1.41), JoyShockMapper's README (smoothing defaults).
namespace MphRecomp.Input
{
    // which rotation of the device turns the view left/right (pitch is always the tilt about the screen's own X axis)
    public enum GyroTurnAxis
    {
        Yaw,    // turning about the screen's own vertical axis ("local space"; GyroWiki's pick for handhelds, where the
                // screen moves with your hands)
        Player, // turning about the real world's vertical, whatever angle it's held at ("player space"; GyroWiki's pick
                // for a controller held apart from the screen)
        Roll    // steering-wheel tilt: rotating the screen left/right turns
    }

    public sealed class GyroAimOptions
    {
        // in-game degrees per real degree of rotation
        public float Sensitivity { get; set; } = 1.5f;
        public GyroTurnAxis TurnAxis { get; set; } = GyroTurnAxis.Yaw;
        // below this rate (deg/s) the output is scaled down linearly toward zero: sensor noise and hand tremor. Kept low
        // (GyroWiki: tightening, never a hard cutoff; JoyShockMapper's examples use a few deg/s)
        public float TightenDps { get; set; } = 2f;
        // soft tiered smoothing: rates below SmoothDps / 2 are fully averaged over SmoothSeconds, rates above SmoothDps are
        // raw, a blend between. 0 = off (the default, as in JoyShockMapper)
        public float SmoothDps { get; set; }
        public float SmoothSeconds { get; set; } = 0.125f;
        public bool InvertPitch { get; set; }
    }

    public sealed class GyroAim
    {
        public GyroAimOptions Options;
        // subtracted from every sample (rad/s, same axes); controllers that don't calibrate themselves need it
        public Vector3 Bias;

        private readonly object _lock = new();
        private Vector2 _pending; // degrees not yet taken: X = turn left +, Y = look up +
        private Vector3 _up; // unit "away from gravity" in the screen's axes; zero until the first accelerometer sample
        private readonly Vector2[] _smooth = new Vector2[64];
        private int _smoothIndex, _smoothCount;

        public GyroAim(GyroAimOptions? options = null)
        {
            Options = options ?? new GyroAimOptions();
        }

        public Vector3 Up { get { lock (_lock) return _up; } }

        // Accelerometer (m/s^2, any length; at rest it points away from gravity), same axes as AddSample. The gyro keeps
        // the estimate turning with the device between samples; this only pulls it back toward the measured direction,
        // so a strafe or a jolt barely moves it.
        public void AddAccelerometer(Vector3 accel, float dt)
        {
            float len = accel.Length();
            if (len < 1e-3f) return;
            Vector3 measured = accel / len;
            lock (_lock)
            {
                if (_up == Vector3.Zero)
                {
                    _up = measured;
                    return;
                }
                float k = Math.Clamp(dt / 0.25f, 0f, 1f);
                _up = Vector3.Normalize(Vector3.Lerp(_up, measured, k));
            }
        }

        // One gyroscope sample: angular velocity in rad/s about the SCREEN's axes -- X right, Y up, Z out of the screen
        // toward the player, counterclockwise positive (Android's sensor convention after remapping for the display
        // rotation) -- held for dt seconds.
        public void AddSample(Vector3 gyroRad, float dt)
        {
            if (dt <= 0f || dt > 0.1f) return; // first sample, or a gap (sensor paused): nothing to integrate
            GyroAimOptions o = Options;
            Vector3 w = gyroRad - Bias;
            lock (_lock)
            {
                if (_up != Vector3.Zero)
                {
                    // a fixed world direction seen from a rotating body turns the other way: d(up)/dt = -w x up
                    _up = Vector3.Normalize(_up - Vector3.Cross(w, _up) * dt);
                }
                Vector3 d = w * (180f / MathF.PI);
                float turn = o.TurnAxis switch
                {
                    GyroTurnAxis.Yaw => d.Y,
                    GyroTurnAxis.Roll => d.Z,
                    _ => PlayerSpaceYaw(d, _up)
                };
                var rate = new Vector2(turn, o.InvertPitch ? -d.X : d.X);
                rate = Tighten(rate, o.TightenDps);
                rate = SoftTieredSmooth(rate, o.SmoothDps, o.SmoothSeconds, dt);
                _pending += rate * (o.Sensitivity * dt);
            }
        }

        // Degrees turned since the last call (X = turn left +, Y = look up +), and starts over.
        public Vector2 TakeDegrees()
        {
            lock (_lock)
            {
                Vector2 d = _pending;
                _pending = Vector2.Zero;
                return d;
            }
        }

        // Forgets pending motion and the smoothing history (e.g. after a pause, so nothing turned during it lands late).
        public void Reset()
        {
            lock (_lock)
            {
                _pending = Vector2.Zero;
                _smoothCount = 0;
                _smoothIndex = 0;
            }
        }

        // Extra gyro scale while zoomed so wrist-to-screen motion stays the same at any zoom: focal-length scaling,
        // tan(fov/2) / tan(normalFov/2) (kovaak.com/sens-scaling; Fortnite's gyro zoom scaling). MphRead already scales
        // every aim delta by its own zoom factor (`mphZoomFactor`, the DS stylus rule), so this returns the remainder.
        public static float ZoomScale(float fovDegrees, float normalFovDegrees, float mphZoomFactor)
        {
            if (fovDegrees <= 0f || normalFovDegrees <= 0f || fovDegrees >= normalFovDegrees || mphZoomFactor <= 0f) return 1f;
            float focal = MathF.Tan(fovDegrees * (MathF.PI / 360f)) / MathF.Tan(normalFovDegrees * (MathF.PI / 360f));
            return focal / mphZoomFactor;
        }

        // MphRead's aim input is in desktop mouse units: aim degrees = -delta / 4 on both axes (PlayerInput.cs
        // ProcessBiped: `aimX = -Input.MouseDeltaX / 4f`), turning right / looking down for positive deltas. Zoom and
        // the game's own clamps apply after this, exactly as they do for the stick.
        public static Vector2 ToMphAim(Vector2 degrees) => new(-4f * degrees.X, -4f * degrees.Y);

        // GyroWiki "player space": the turn is the rotation about the world's vertical (gravity), so it works however the
        // device is tilted, with the screen's local yaw/roll axes as a ceiling (the 1.41 relax factor lets a device held
        // at an angle still turn at full rate with its own yaw axis). Rotation about the screen's X axis never turns.
        public static float PlayerSpaceYaw(Vector3 dps, Vector3 up)
        {
            if (up == Vector3.Zero) return dps.Y; // no gravity yet: plain local yaw
            up = Vector3.Normalize(up);
            float worldYaw = up.Y * dps.Y + up.Z * dps.Z;
            const float yawRelaxFactor = 1.41f;
            float cap = MathF.Sqrt(dps.Y * dps.Y + dps.Z * dps.Z);
            return MathF.Sign(worldYaw) * MathF.Min(MathF.Abs(worldYaw) * yawRelaxFactor, cap);
        }

        // GyroWiki "tightening": rates under the threshold shrink in proportion to how far under it they are
        public static Vector2 Tighten(Vector2 rate, float threshold)
        {
            if (threshold <= 0f) return rate;
            float mag = rate.Length();
            return mag < threshold ? rate * (mag / threshold) : rate;
        }

        // GyroWiki "soft tiered smoothing": split the input into a direct part and a part averaged over a short window,
        // by speed -- slow, fiddly motion gets steadier, fast motion keeps zero latency
        private Vector2 SoftTieredSmooth(Vector2 rate, float threshold, float seconds, float dt)
        {
            if (threshold <= 0f || seconds <= 0f) return rate;
            float lo = threshold * 0.5f, hi = threshold;
            float directWeight = Math.Clamp((rate.Length() - lo) / (hi - lo), 0f, 1f);
            int window = Math.Clamp((int)MathF.Round(seconds / dt), 1, _smooth.Length);
            _smooth[_smoothIndex] = rate * (1f - directWeight);
            _smoothIndex = (_smoothIndex + 1) % _smooth.Length;
            _smoothCount = Math.Min(_smoothCount + 1, _smooth.Length);
            int n = Math.Min(window, _smoothCount);
            Vector2 sum = Vector2.Zero;
            for (int i = 1; i <= n; i++)
            {
                sum += _smooth[(_smoothIndex - i + _smooth.Length) % _smooth.Length];
            }
            return rate * directWeight + sum / n;
        }
    }

    // MphRead specifics for a gyro host (the Android campaign, or a desktop one later)
    public static class GyroMph
    {
        // the gyro-only zoom correction for this frame (1 when not zoomed); see GyroAim.ZoomScale
        public static float ZoomScale(MphRead.Entities.PlayerEntity p)
        {
            if (!p.EquipInfo.Zoomed) return 1f;
            float normal = MphRead.Fixed.ToFloat(p.Values.NormalFov) * 2;
            // PlayerInput.UpdateAimX/Y: sensitivity /= -Field70 * (Fov - NormalFov * 2)
            float fovFactor = p.CameraInfo.Fov - normal;
            if (fovFactor == 0f) return 1f; // the camera is on its normal FOV (a camera sequence overrode it)
            float mph = 1f / (-MphRead.Fixed.ToFloat(p.Values.Field70) * fovFactor);
            return GyroAim.ZoomScale(p.CameraInfo.Fov, normal, mph);
        }
    }
}
