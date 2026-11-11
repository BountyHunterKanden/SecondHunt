using System;
using MphRead.Entities;
using MphRecomp.Config;
using MphRecomp.Input;

namespace MphRecomp.App;

// Gyro aiming for the campaign: a device's own gyroscope, added on top of the right stick (the stick still turns the
// camera exactly as before; the gyro is for fine aim). The math lives in MphRecomp.Core (Input/GyroAim.cs); the
// sensor is the host's (Android: MphRead.Android/CampaignGyro.cs, which sets Gyro). BuildInput calls GyroAimDelta once
// per sim step. While zoomed the gyro also gets focal-length scaling (GyroMph.ZoomScale) so the Imperialist's zoom
// stays steady. The GYRO button (CONTROLS > GYRO, RecompSettings.GyroButton) can hold, pause or toggle it.
internal sealed partial class CampaignRenderer
{
    // the host's gyroscope, if it has one
    public IGyroAimSource? Gyro { get => _gyro; set => _gyro = value; }
    IGyroAimSource? _gyro;
    double _gyroLastTake = -1;
    string _gyroButton = "always"; // RecompSettings.GyroButton (ReloadAimFromSettings)
    bool _gyroToggledOff; // "toggle": a press of the GYRO button turned it off

    // the GYRO button's say this step: is gyro aim on (always, with no button bound)
    bool GyroButtonOn()
    {
        if (_gyroButton == "always" || !_padBinds.Bound(ControlBinds.Gyro))
        {
            _gyroToggledOff = false;
            return true;
        }
        bool held = _padBinds.Held(ControlBinds.Gyro);
        switch (_gyroButton)
        {
            case "hold": return held;
            case "pause": return !held;
            default: // "toggle"
                if (_padBinds.Pressed(ControlBinds.Gyro)) _gyroToggledOff = !_gyroToggledOff;
                return !_gyroToggledOff;
        }
    }

    // Aim to add this sim step, in MphRead's mouse units (see GyroAim.ToMphAim). Motion from while the game wasn't
    // taking input (a dialog, the pause menu, the ship) is dropped, not delivered late as a jump.
    System.Numerics.Vector2 GyroAimDelta()
    {
        IGyroAimSource? g = _gyro;
        PlayerEntity? player = _host?.Player;
        if (g == null || !g.Running || player == null)
        {
            return default;
        }
        double now = _clock.Elapsed.TotalSeconds;
        bool stale = _gyroLastTake < 0 || now - _gyroLastTake > 0.1;
        _gyroLastTake = now;
        // this step's own share of the motion (GyroAim.TakePaced): its time is the frame's minus the catch-up still to run
        System.Numerics.Vector2 degrees = g.Aim.TakePaced(g.SensorNowNs, (long)(Math.Max(_accum, 0) * 1e9));
        if (stale)
        {
            g.Aim.Reset();
            return default;
        }
        // (after the take, so motion while it's off is dropped, not saved up for when it's back on)
        if (!GyroButtonOn())
        {
            return default;
        }
        if (g.OnlyWhenZoomed && !player.EquipInfo.Zoomed && !player.ScanVisor)
        {
            return default;
        }
        if (g.ZoomScaling)
        {
            degrees *= GyroMph.ZoomScale(player);
        }
        // IMPERIALIST GYRO SPEED when unpaired (ZoomAim)
        degrees *= MphRecomp.Input.ZoomAim.GyroScale(player.EquipInfo.Zoomed);
        g.NoteTaken(degrees);
        return GyroAim.ToMphAim(degrees);
    }
}

// A gyroscope's aim for the campaign, read on the GL thread once per simulation step
public interface IGyroAimSource
{
    bool Running { get; }
    // the degrees turned since the last take (GyroAim.TakeDegrees / TakePaced)
    GyroAim Aim { get; }
    // now on the clock the sensor stamps its samples with (Android: elapsedRealtimeNanos); 0 = none, takes are unpaced
    long SensorNowNs => 0;
    // "zoom" mode: only while zoomed or in the scan visor
    bool OnlyWhenZoomed { get; }
    bool ZoomScaling { get; }
    // the degrees handed to the game (the sensor's own log)
    void NoteTaken(System.Numerics.Vector2 degrees);
}
