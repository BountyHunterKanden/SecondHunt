using System;
using MphRead.Entities;
using MphRecomp.Input;

namespace MphRecomp.App;

// Gyro aiming for the campaign: a device's own gyroscope, added on top of the right stick (the stick still turns the
// camera exactly as before; the gyro is for fine aim). The math lives in MphRecomp.Core (Input/GyroAim.cs); the
// sensor is the host's (Android: MphRead.Android/CampaignGyro.cs, which sets Gyro). BuildInput calls GyroAimDelta once
// per sim step. While zoomed the gyro also gets focal-length scaling (GyroMph.ZoomScale) so the Imperialist's zoom
// stays steady.
internal sealed partial class CampaignRenderer
{
    // the host's gyroscope, if it has one
    public IGyroAimSource? Gyro { get => _gyro; set => _gyro = value; }
    IGyroAimSource? _gyro;
    double _gyroLastTake = -1;

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
        System.Numerics.Vector2 degrees = g.Aim.TakeDegrees();
        if (stale)
        {
            g.Aim.Reset();
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
        g.NoteTaken(degrees);
        return GyroAim.ToMphAim(degrees);
    }
}

// A gyroscope's aim for the campaign, read on the GL thread once per simulation step
public interface IGyroAimSource
{
    bool Running { get; }
    // the degrees turned since the last take (GyroAim.TakeDegrees)
    GyroAim Aim { get; }
    // "zoom" mode: only while zoomed or in the scan visor
    bool OnlyWhenZoomed { get; }
    bool ZoomScaling { get; }
    // the degrees handed to the game (the sensor's own log)
    void NoteTaken(System.Numerics.Vector2 degrees);
}
