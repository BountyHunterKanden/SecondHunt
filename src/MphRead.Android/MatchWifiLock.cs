using Android.App;
using Android.Content;
using Android.Net.Wifi;
using Android.OS;

// a Wi-Fi lock needs the wake lock permission (a normal one: granted at install)
[assembly: UsesPermission(Android.Manifest.Permission.WakeLock)]

namespace MphRecomp.App;

// Keeps the Wi-Fi radio out of its power saving while a LAN or online match is on screen (2026-10-19, after match B3H0BA:
// a phone sent its inputs in bunches, the host starved for them 205 frames in two minutes against the other's 13). It does
// nothing on mobile data or to which network is used. A phone's Wi-Fi power save holds packets and sends them together; the low-latency
// lock (Android 10+, what game-streaming apps take) turns that off while the app is in front with the screen on, the
// high-performance one before Android 10. Held from resume to pause, let go when the match screen closes.
internal sealed class MatchWifiLock : Java.Lang.Object, Application.IActivityLifecycleCallbacks
{
    readonly Activity _activity;
    readonly WifiManager.WifiLock? _lock;

    MatchWifiLock(Activity activity)
    {
        _activity = activity;
        var wifi = (WifiManager?)activity.ApplicationContext!.GetSystemService(Context.WifiService);
#pragma warning disable CA1422, CA1416 // (FullHighPerf: deprecated from Android 10, used only below it)
        _lock = wifi?.CreateWifiLock(Build.VERSION.SdkInt >= BuildVersionCodes.Q ? Android.Net.WifiMode.FullLowLatency : Android.Net.WifiMode.FullHighPerf,
            "SecondHunt match");
#pragma warning restore CA1422, CA1416
        _lock?.SetReferenceCounted(false);
    }

    // the match screen: held now (it's resumed next) and while it's in front
    public static void Attach(Activity activity)
    {
        var l = new MatchWifiLock(activity);
        if (l._lock == null) return;
        l.Hold(true);
        activity.Application!.RegisterActivityLifecycleCallbacks(l);
    }

    void Hold(bool on)
    {
        try
        {
            if (on && !_lock!.IsHeld) _lock.Acquire();
            else if (!on && _lock!.IsHeld) _lock.Release();
        }
        catch (Java.Lang.Exception ex)
        {
            Android.Util.Log.Warn("SecondHunt", $"match Wi-Fi lock: {ex.Message}");
        }
    }

    public void OnActivityResumed(Activity activity) { if (activity == _activity) Hold(true); }
    public void OnActivityPaused(Activity activity) { if (activity == _activity) Hold(false); }
    public void OnActivityDestroyed(Activity activity)
    {
        if (activity == _activity)
        {
            Hold(false);
            activity.Application!.UnregisterActivityLifecycleCallbacks(this);
        }
    }

    public void OnActivityCreated(Activity activity, Bundle? savedInstanceState) { }
    public void OnActivitySaveInstanceState(Activity activity, Bundle outState) { }
    public void OnActivityStarted(Activity activity) { }
    public void OnActivityStopped(Activity activity) { }
}
