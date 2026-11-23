using System;
using Android.App;
using Android.Content;
using Android.OS;
using Android.Views;

[assembly: UsesPermission(Android.Manifest.Permission.Vibrate)]

namespace MphRecomp.App;

// The game's Rumble Pak (MphRead/Rumble.cs, OPTIONS > RUMBLE PAK) on Android: a connected controller's motor when it
// has one, else the phone's. The game asks for a strength (1-100) and a time; the pak turns strength into a pulse rate
// (ARM9 0x2077da8), here it is the motor's amplitude. Installed by AndroidPlatform.Install; the motor stops while no
// activity of ours is in front (the game's lid-close pause, 0x205d178) and picks up again when one comes back.
internal sealed class AndroidRumble : Java.Lang.Object, MphRead.IRumbleOutput, Application.IActivityLifecycleCallbacks
{
    readonly Vibrator? _phone;
    Vibrator? _running; // the motor the last Start used
    int _resumed;

    public AndroidRumble(Context context)
    {
        if (OperatingSystem.IsAndroidVersionAtLeast(31))
        {
            _phone = ((VibratorManager?)context.GetSystemService(Context.VibratorManagerService))?.DefaultVibrator;
        }
        else
        {
#pragma warning disable CA1422 // (the API 31 VibratorManager above replaces it)
            _phone = (Vibrator?)context.GetSystemService(Context.VibratorService);
#pragma warning restore CA1422
        }
        if (_phone?.HasVibrator != true) _phone = null;
        (context as Application)?.RegisterActivityLifecycleCallbacks(this);
    }

    // the first connected controller with a motor (looked up each time: pads come and go, and rumbles are rare)
    static Vibrator? PadMotor()
    {
        foreach (int id in InputDevice.GetDeviceIds() ?? Array.Empty<int>())
        {
            InputDevice? device = InputDevice.GetDevice(id);
            if (device == null || device.IsVirtual || (device.Sources & InputSourceType.Gamepad) != InputSourceType.Gamepad) continue;
            Vibrator? motor = OperatingSystem.IsAndroidVersionAtLeast(31) ? device.VibratorManager.DefaultVibrator
#pragma warning disable CA1422
                : device.Vibrator;
#pragma warning restore CA1422
            if (motor?.HasVibrator == true) return motor;
        }
        return null;
    }

    public void Start(int strength, int ms)
    {
        try
        {
            Vibrator? motor = PadMotor() ?? _phone;
            if (motor == null) return;
            if (_running != null && _running != motor) _running.Cancel();
            _running = motor;
            int amplitude = motor.HasAmplitudeControl ? Math.Clamp(strength * 255 / 100, 1, 255) : VibrationEffect.DefaultAmplitude;
            motor.Vibrate(VibrationEffect.CreateOneShot(ms, amplitude));
        }
        catch (Exception ex)
        {
            MphRecomp.App.Platform.Log.Warn("MPHRumble", $"rumble failed: {ex.Message}");
        }
    }

    public void Stop()
    {
        try
        {
            _running?.Cancel();
        }
        catch (Exception) { }
        _running = null;
    }

    public void OnActivityResumed(Activity activity)
    {
        if (++_resumed == 1) MphRead.Rumble.SetPaused(false);
    }

    public void OnActivityPaused(Activity activity)
    {
        if (_resumed > 0 && --_resumed == 0) MphRead.Rumble.SetPaused(true);
    }

    public void OnActivityCreated(Activity activity, Bundle? savedInstanceState) { }
    public void OnActivityDestroyed(Activity activity) { }
    public void OnActivitySaveInstanceState(Activity activity, Bundle outState) { }
    public void OnActivityStarted(Activity activity) { }
    public void OnActivityStopped(Activity activity) { }
}
