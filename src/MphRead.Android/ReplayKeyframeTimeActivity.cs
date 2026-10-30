using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Android.App;
using Android.Content.PM;
using Android.OS;
using Android.Util;
using Android.Widget;
using MphRecomp.Replay;

namespace MphRecomp.App;

// Instant seek (thread B) step 2a: what portable keyframe work costs on this device, headless (Core ReplayKeyframeTiming).
// Dev only:
//   adb shell am start -S -n com.mphrecomp.app/com.mphrecomp.app.ReplayKeyframeTimeActivity
//       [--es dir <dir or one .mphreplay>] [--es names a,b] [--es every 60] [--es prio fg|bg|both]
// dir: default <external files>/replays/golden; names: only those files (without extension). prio: fg = a normal thread
// (a big core), bg = THREAD_PRIORITY_BACKGROUND (what the shadow service will run at), both = each replay twice.
// Results: logcat -s MPHKfTime.
[Activity(Name = "com.mphrecomp.app.ReplayKeyframeTimeActivity", Label = "mph-recomp keyframe timing",
    Exported = BuildFlags.ExportDevActivities, ScreenOrientation = ScreenOrientation.Landscape)]
public class ReplayKeyframeTimeActivity : Activity
{
    TextView _text = null!;
    readonly StringBuilder _lines = new();

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        _text = new TextView(this) { TextSize = 12 };
        _text.SetPadding(32, 32, 32, 32);
        var scroll = new ScrollView(this);
        scroll.AddView(_text);
        SetContentView(scroll);
        Window?.AddFlags(Android.Views.WindowManagerFlags.KeepScreenOn);
        string external = GetExternalFilesDir(null)?.AbsolutePath ?? FilesDir!.AbsolutePath;
        string dir = Intent?.GetStringExtra("dir") ?? Path.Combine(external, "replays", "golden");
        string? names = Intent?.GetStringExtra("names");
        int every = int.TryParse(Intent?.GetStringExtra("every"), out int e) ? e : 60;
        string prio = Intent?.GetStringExtra("prio") ?? "both";
        Directory.SetCurrentDirectory(FilesDir!.AbsolutePath);
        MphRead.Paths.UpdatePaths();
        MphRead.Paths.ChooseMphPath();
        MphRecomp.Arenas.EchoesArena.ImportRoot = external;
        MphRecomp.Arenas.EchoesArena.InstallStaged(Path.Combine(external, "arenas"));
        // a big stack, as the GL thread a match normally runs on
        new Thread(() => Run(dir, names, every, prio), 16 * 1024 * 1024).Start();
    }

    void Run(string dir, string? names, int every, string prio)
    {
        string[] files = Directory.Exists(dir)
            ? Directory.GetFiles(dir, "*" + ReplayLibrary.Extension).OrderBy(f => f, System.StringComparer.Ordinal).ToArray()
            : File.Exists(dir) ? new[] { dir } : System.Array.Empty<string>();
        if (names != null)
        {
            var want = names.Split(',');
            files = files.Where(f => want.Contains(Path.GetFileNameWithoutExtension(f))).ToArray();
        }
        Show($"keyframe timing: {files.Length} replay(s), every {every}, prio {prio} on {ReplayFile.DescribePlatform()}");
        foreach (string path in files)
        {
            foreach (bool bg in prio == "both" ? new[] { false, true } : new[] { prio == "bg" })
            {
                // each run on its own thread so the priority is that thread's alone
                string label = Path.GetFileNameWithoutExtension(path) + (bg ? " bg" : " fg");
                var t = new Thread(() =>
                {
                    Android.OS.Process.SetThreadPriority(bg ? Android.OS.ThreadPriority.Background : Android.OS.ThreadPriority.Default);
                    try
                    {
                        string? r = ReplayKeyframeTiming.Run(ReplayFile.Load(path), every, 6, label, Show);
                        Show($"[{label}] {(r == null ? "OK" : "FAILED: " + r)}");
                    }
                    catch (System.Exception ex)
                    {
                        Show($"[{label}] ERROR {ex.GetType().Name}: {ex.Message}");
                        Log.Error("MPHKfTime", ex.ToString());
                    }
                }, 16 * 1024 * 1024);
                t.Start();
                t.Join();
            }
        }
        Show("keyframe timing: all done");
    }

    void Show(string line)
    {
        Log.Info("MPHKfTime", line);
        lock (_lines)
        {
            _lines.AppendLine(line);
        }
        RunOnUiThread(() =>
        {
            lock (_lines)
            {
                _text.Text = _lines.ToString();
            }
        });
    }
}
