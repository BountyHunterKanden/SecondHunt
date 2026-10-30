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
            }
            catch (System.Exception ex) { Android.Util.Log.Warn("MPHReplayPrep", "can't start the prepare: " + ex.Message); }
        };
    }

    readonly BlockingCollection<(string Replay, string Sidecar, int StartId)> _queue = new();
    Thread? _worker;

    public override IBinder? OnBind(Intent? intent) => null;

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
        }
    }
}
