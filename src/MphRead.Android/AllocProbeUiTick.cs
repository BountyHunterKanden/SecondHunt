using Android.OS;

namespace MphRecomp.App;

// Dev probe (2026-11-16, menu GC leftovers): the Android UI thread's share in ThreadAllocProbe's "alloc by thread"
// lines -- input events, sensors and JNI callbacks land there, and the PC has no such thread. One reused Runnable a
// second on the main Looper (no allocation of its own after the first). GameActivity starts it on `--es allocprobe 1`;
// never in a public build. RELEASE GATE: remove or keep gated (memory project_next_release_gates).
internal sealed class AllocProbeUiTick : Java.Lang.Object, Java.Lang.IRunnable
{
    static AllocProbeUiTick? s_tick;
    readonly Handler _handler = new(Looper.MainLooper!);

    public static void Start()
    {
        ThreadAllocProbe.Enable();
        if (!ThreadAllocProbe.Enabled || s_tick != null)
        {
            return;
        }
        s_tick = new AllocProbeUiTick();
        s_tick._handler.Post(s_tick);
    }

    public void Run()
    {
        ThreadAllocProbe.Tick("android-ui");
        _handler.PostDelayed(this, 1000);
    }
}
