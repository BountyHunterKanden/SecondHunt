using System;
using Android.App;
using Android.Content;
using Android.Util;
using Android.Views;
using Android.Widget;
using MphRead;
using MphRecomp.Campaign;
using MphRecomp.Multiplayer;
using MphRecomp.Multiplayer.Net;


namespace MphRecomp.App;

// closes a LAN session when the activity playing it is destroyed
internal sealed class CloseSessionOnDestroy : Java.Lang.Object, Android.App.Application.IActivityLifecycleCallbacks
{
    readonly Android.App.Activity _activity;
    readonly LanSession _session;

    public CloseSessionOnDestroy(Android.App.Activity activity, LanSession session)
    {
        _activity = activity;
        _session = session;
    }

    public void OnActivityDestroyed(Android.App.Activity activity)
    {
        if (activity == _activity)
        {
            _session.Dispose();
            activity.Application!.UnregisterActivityLifecycleCallbacks(this);
        }
    }

    public void OnActivityCreated(Android.App.Activity activity, Android.OS.Bundle? savedInstanceState) { }
    public void OnActivityPaused(Android.App.Activity activity) { }
    public void OnActivityResumed(Android.App.Activity activity) { }
    public void OnActivitySaveInstanceState(Android.App.Activity activity, Android.OS.Bundle outState) { }
    public void OnActivityStarted(Android.App.Activity activity) { }
    public void OnActivityStopped(Android.App.Activity activity) { }
}
