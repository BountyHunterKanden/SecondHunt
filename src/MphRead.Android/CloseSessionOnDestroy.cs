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

// closes a LAN session (or an online match) when the activity playing it is destroyed. The session is looked up then,
// not when the activity was made: the match screen wraps it once the match starts (MatchReplay's ReplayLinkHold, which
// keeps it open while a quitter's replay comes in), and closing the one from before skipped that wait (2026-10-19, match
// BF87SN: the guest asked for its recording and its link closed at once).
internal sealed class CloseSessionOnDestroy : Java.Lang.Object, Android.App.Application.IActivityLifecycleCallbacks
{
    readonly Android.App.Activity _activity;
    readonly Func<IDisposable?> _session;

    public CloseSessionOnDestroy(Android.App.Activity activity, Func<IDisposable?> session)
    {
        _activity = activity;
        _session = session;
    }

    public void OnActivityDestroyed(Android.App.Activity activity)
    {
        if (activity == _activity)
        {
            _session()?.Dispose();
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
