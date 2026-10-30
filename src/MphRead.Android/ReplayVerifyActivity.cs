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

// Replay mode R2b (docs/REPLAY_MODE.md): plays replays with no drawing, as fast as the sim runs, and says whether each
// is exact on this device (Core ReplayVerify; the same lines as Tools -replayverify on the PC, to diff). Dev only:
//   adb shell am start -S -n com.mphrecomp.app/com.mphrecomp.app.ReplayVerifyActivity [--es dir <dir>] [--es dump a-b]
// dir: default <external files>/replays/golden (push tests/replays/*.mphreplay there); every .mphreplay in it, by name.
// dump a-b: each step's raw state (ReplayState.Dump) under MPHReplayDump. Results: logcat -s MPHReplay.
[Activity(Name = "com.mphrecomp.app.ReplayVerifyActivity", Label = "mph-recomp replay verify",
    Exported = BuildFlags.ExportDevActivities, ScreenOrientation = ScreenOrientation.Landscape)]
public class ReplayVerifyActivity : Activity
{
    TextView _text = null!;
    readonly StringBuilder _lines = new();

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        _text = new TextView(this) { TextSize = 14 };
        _text.SetPadding(32, 32, 32, 32);
        var scroll = new ScrollView(this);
        scroll.AddView(_text);
        SetContentView(scroll);
        Window?.AddFlags(Android.Views.WindowManagerFlags.KeepScreenOn);
        string external = GetExternalFilesDir(null)?.AbsolutePath ?? FilesDir!.AbsolutePath;
        string dir = Intent?.GetStringExtra("dir") ?? Path.Combine(external, "replays", "golden");
        string? dumpArg = Intent?.GetStringExtra("dump");
        int dumpFrom = -1, dumpTo = -1;
        if (!string.IsNullOrEmpty(dumpArg))
        {
            string[] range = dumpArg.Split('-');
            dumpFrom = int.Parse(range[0]);
            dumpTo = int.Parse(range[^1]);
        }
        // the ROM paths and imported arenas, as the dev launcher sets them up for a match (MainActivity.Arenas)
        Directory.SetCurrentDirectory(FilesDir!.AbsolutePath);
        MphRead.Paths.UpdatePaths();
        MphRead.Paths.ChooseMphPath();
        MphRecomp.Arenas.EchoesArena.ImportRoot = external;
        MphRecomp.Arenas.EchoesArena.InstallStaged(Path.Combine(external, "arenas"));
        // a big stack, as the GL thread a match normally runs on
        new Thread(() => Run(dir, dumpFrom, dumpTo), 16 * 1024 * 1024).Start();
    }

    void Run(string dir, int dumpFrom, int dumpTo)
    {
        string[] files;
        try
        {
            files = Directory.Exists(dir)
                ? Directory.GetFiles(dir, "*" + ReplayLibrary.Extension).OrderBy(f => f, System.StringComparer.Ordinal).ToArray()
                : File.Exists(dir) ? new[] { dir } : System.Array.Empty<string>();
        }
        catch (System.Exception ex)
        {
            // e.g. a folder adb made (shell's, rwxrws---) that Android 13 won't list for the app: a result, not a crash
            Show($"verify: can't read {dir}: {ex.GetType().Name}: {ex.Message}");
            return;
        }
        Show($"verifying {files.Length} replay(s) in {dir} on {ReplayFile.DescribePlatform()}");
        int exact = 0;
        foreach (string path in files)
        {
            string line;
            try
            {
                ReplayVerify.Result r = ReplayVerify.Run(ReplayFile.Load(path), dumpFrom < 0 ? null : (step, scene) =>
                {
                    if (step >= dumpFrom && step <= dumpTo)
                    {
                        foreach (string l in ReplayState.Dump(scene, step).Split('\n'))
                        {
                            Log.Info("MPHReplayDump", l);
                        }
                    }
                });
                exact += r.Exact ? 1 : 0;
                line = $"verify {Path.GetFileName(path)}: {r.Verdict}";
            }
            catch (System.Exception ex)
            {
                line = $"verify {Path.GetFileName(path)}: ERROR {ex.GetType().Name}: {ex.Message}";
                Log.Error("MPHReplay", ex.ToString());
            }
            Show(line);
        }
        Show($"verify: {exact}/{files.Length} exact on {ReplayFile.DescribePlatform()}");
    }

    void Show(string line)
    {
        Log.Info("MPHReplay", line);
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
