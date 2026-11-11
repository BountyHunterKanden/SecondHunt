using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using Android.App;
using Android.Content;
using Android.OS;

namespace MphRecomp.App;

// Fast seeking (thread B, step 2): the replay's keyframes made in their own process (:replayprep) at background
// priority, so a slow prepare never takes the viewer's frames. The app asks for one per replay (ReplayPrepHost.Start);
// prepares run one at a time, and the service stops itself when none is left.
[Service(Name = "com.mphrecomp.app.ReplayPrepService", Process = ":replayprep", Exported = false)]
public class ReplayPrepService : Service
{
    // (the app's process): how ReplayPrepHost starts a prepare here
    public static void Install(Context context)
    {
        Context app = context.ApplicationContext ?? context;
        ReplayPrepHost.Start = (replay, sidecar) =>
        {
            try
            {
                var intent = new Intent(app, typeof(ReplayPrepService));
                intent.PutExtra("replay", replay);
                intent.PutExtra("sidecar", sidecar);
                app.StartService(intent);
                Keep(app);
            }
            catch (System.Exception ex) { Android.Util.Log.Warn("MPHReplayPrep", "can't start the prepare: " + ex.Message); }
        };
    }

    // The app stays bound to the service while its prepares run. A started service alone sat at a cached process's
    // importance on the Odin (adj 905-925), and Android killed it for "excessive cpu" five minutes into a Mining Plaza
    // prepare (11-06), so its keyframes never finished. Bound, it ranks with the app while that's in front;
    // NotForeground keeps its threads on the background CPUs, out of the match's way. Each prepare done sends DoneAction;
    // the last one unbinds.
    const string DoneAction = "com.mphrecomp.app.REPLAY_PREP_DONE";
    static readonly object _keepLock = new();
    static Connection? _connection;
    static int _pending;

    sealed class Connection : Java.Lang.Object, IServiceConnection
    {
        public void OnServiceConnected(ComponentName? name, IBinder? service) { }

        // the process died (killed, or crashed): its prepares are gone, so let go (the next open starts them again)
        public void OnServiceDisconnected(ComponentName? name) => Release(Application.Context, all: true);
    }

    sealed class DoneReceiver : BroadcastReceiver
    {
        public override void OnReceive(Context? context, Intent? intent) => Release(Application.Context, all: false);
    }

    static bool _receiverOn;

    static void Keep(Context app)
    {
        lock (_keepLock)
        {
            if (!_receiverOn)
            {
                var filter = new IntentFilter(DoneAction);
                if (Build.VERSION.SdkInt >= BuildVersionCodes.Tiramisu) app.RegisterReceiver(new DoneReceiver(), filter, ReceiverFlags.NotExported);
                else app.RegisterReceiver(new DoneReceiver(), filter);
                _receiverOn = true;
            }
            _pending++;
            if (_connection != null) return;
            var connection = new Connection();
            if (app.BindService(new Intent(app, typeof(ReplayPrepService)), connection, Bind.AutoCreate | Bind.NotForeground)) _connection = connection;
            else Android.Util.Log.Warn("MPHReplayPrep", "can't bind the prepare");
        }
    }

    static void Release(Context app, bool all)
    {
        lock (_keepLock)
        {
            _pending = all ? 0 : System.Math.Max(0, _pending - 1);
            if (_pending > 0 || _connection == null) return;
            try { app.UnbindService(_connection); } catch (System.Exception) { }
            _connection = null;
        }
    }

    readonly BlockingCollection<(string Replay, string Sidecar, int StartId)> _queue = new();
    Thread? _worker;

    public override IBinder? OnBind(Intent? intent) => new Binder(); // (the app's binding: see Keep)

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        string? replay = intent?.GetStringExtra("replay");
        string? sidecar = intent?.GetStringExtra("sidecar");
        if (replay == null || sidecar == null)
        {
            StopSelf(startId);
            return StartCommandResult.NotSticky;
        }
        _queue.Add((replay, sidecar, startId));
        if (_worker == null)
        {
            // a big stack, as the GL thread a match normally runs on
            _worker = new Thread(Work, 16 * 1024 * 1024) { IsBackground = true, Name = "replayprep" };
            _worker.Start();
        }
        return StartCommandResult.NotSticky; // killed mid-prepare: the viewer's next open starts it again
    }

    void Work()
    {
        Android.OS.Process.SetThreadPriority(Android.OS.ThreadPriority.Background);
        string files = FilesDir!.AbsolutePath;
        string external = GetExternalFilesDir(null)?.AbsolutePath ?? files;
        bool staged = false;
        while (true)
        {
            var job = _queue.Take();
            if (!staged)
            {
                MphRecomp.Arenas.EchoesArena.ImportRoot = external;
                MphRecomp.Arenas.EchoesArena.InstallStaged(Path.Combine(external, "arenas"));
                staged = true;
            }
            ReplayPrepHost.Run(files, external, job.Replay, job.Sidecar);
            // StopSelf(id) only stops once the newest start has been handled
            StopSelf(job.StartId);
            try { SendBroadcast(new Intent(DoneAction).SetPackage(PackageName)); } catch (System.Exception) { }
        }
    }
}
