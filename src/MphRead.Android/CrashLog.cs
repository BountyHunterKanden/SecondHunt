using System.Text;
using Android.Content;
using Android.Runtime;

namespace MphRecomp.App;

// The app's Application: installs the crash log before any activity starts (public beta audit S2).
[Application(Name = "com.mphrecomp.app.MphApp")]
public class MphApp : Application
{
    public MphApp(IntPtr handle, JniHandleOwnership ownership) : base(handle, ownership) { }

    public override void OnCreate()
    {
        base.OnCreate();
        CrashLog.Install(this);
        AndroidPlatform.Install(this); // the shared app layer's logging, audio output, images and caches
        MphRecomp.Config.RecompSettings.PublicBuild = BuildFlags.Public;
        MphRecomp.Config.RecompSettings.AppId = PackageName ?? MphRecomp.Config.RecompSettings.AppId;
        InstallUpdateHost();
    }

    // RECOMP SETTINGS > UPDATES (Core Update/): this build's version and its installer (AndroidUpdater)
    void InstallUpdateHost()
    {
        try
        {
            string name = PackageName!;
            Android.Content.PM.PackageInfo info = OperatingSystem.IsAndroidVersionAtLeast(33)
                ? PackageManager!.GetPackageInfo(name, Android.Content.PM.PackageManager.PackageInfoFlags.Of(0L))!
                : PackageManager!.GetPackageInfo(name, 0)!;
            MphRecomp.Update.UpdateHost.AppVersion = info.VersionName ?? "0.0.0";
        }
        catch (Exception ex) { Android.Util.Log.Warn("MPHUpdate", "no version name: " + ex.Message); }
        MphRecomp.Update.UpdateHost.Platform = MphRecomp.Update.UpdatePlatform.Android;
        // dev builds: `adb push` a one-line update_feed.txt (a test server's base URL) into the external files dir
        MphRecomp.Update.UpdateHost.DevFeedFile = BuildFlags.Public
            ? null
            : Path.Combine(GetExternalFilesDir(null)?.AbsolutePath ?? FilesDir!.AbsolutePath, "update_feed.txt");
        Context app = ApplicationContext!;
        MphRecomp.Update.UpdateHost.Installer = new AndroidUpdater(app); // the game installs updates itself (owner 10-06)
        MphRecomp.Update.UpdateHost.OpenUrl = url =>
        {
            try
            {
                var view = new Intent(Intent.ActionView, Android.Net.Uri.Parse(url));
                view.AddFlags(ActivityFlags.NewTask); // started from the application, not an activity
                app.StartActivity(view);
            }
            catch (ActivityNotFoundException) { Android.Util.Log.Warn("MPHUpdate", "no browser for " + url); }
        };
    }
}

// Crash reports for beta testers (public beta audit S2). An exception that would kill the app is written, with the
// build, the device and this process's last log lines, to <external files>/crash/crash-<time>.txt; then the app dies
// exactly as before (nothing is swallowed or retried). The owner pulls them from
// /sdcard/Android/data/com.mphrecomp.app/files/crash/; testers share the newest from the app (ShareIntent: plain text,
// so no FileProvider is needed; the UI's "share log" entry, S29). Keeps the newest 10. Only this process's own log is
// read (logcat --pid), so no other app's data ends up in a report.
public static class CrashLog
{
    const string Tag = "MPHCrash";
    const int Keep = 10;
    static string? _dir;
    static string _header = "";
    static Exception? _last;
    static int _writing;

    public static string? Folder => _dir;

    public static void Install(Context context)
    {
        try
        {
            _dir = Path.Combine(context.GetExternalFilesDir(null)?.AbsolutePath ?? context.FilesDir!.AbsolutePath, "crash");
            _header = Header(context);
        }
        catch (Exception e) { Android.Util.Log.Warn(Tag, "crash log not set up: " + e.Message); }
        // the same exception can arrive through both: the JNI boundary first (a throw inside a Java callback such as
        // GLSurfaceView.Renderer.OnDrawFrame), then the AppDomain as the process goes down
        AndroidEnvironment.UnhandledExceptionRaiser += (_, e) => Write(e.Exception, "unhandled, crossing into Java");
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Write(e.ExceptionObject as Exception, e.IsTerminating ? "unhandled" : "unhandled, not terminating");
        // not fatal (the runtime ignores these): log only
        TaskScheduler.UnobservedTaskException += (_, e) => Android.Util.Log.Warn(Tag, "unobserved task exception: " + e.Exception);
    }

    // the newest report's path, or null
    public static string? LatestPath()
    {
        if (_dir == null || !System.IO.Directory.Exists(_dir)) return null;
        return System.IO.Directory.GetFiles(_dir, "crash-*.txt").OrderByDescending(f => f, StringComparer.Ordinal).FirstOrDefault();
    }

    // a report newer than the last one the player was shown (for a "the game closed unexpectedly, share the report?"
    // prompt on the next start); MarkSeen after offering it
    public static bool HasUnseen()
    {
        string? latest = LatestPath();
        if (latest == null) return false;
        string seen = Path.Combine(_dir!, "seen.txt");
        return !File.Exists(seen) || File.ReadAllText(seen).Trim() != Path.GetFileName(latest);
    }

    public static void MarkSeen()
    {
        string? latest = LatestPath();
        if (latest != null) File.WriteAllText(Path.Combine(_dir!, "seen.txt"), Path.GetFileName(latest));
    }

    // a share sheet carrying the newest report as plain text, or null if there is none
    public static Intent? ShareIntent()
    {
        string? latest = LatestPath();
        if (latest == null) return null;
        var send = new Intent(Intent.ActionSend);
        send.SetType("text/plain");
        send.PutExtra(Intent.ExtraSubject, "MPH Recomp crash report " + Path.GetFileNameWithoutExtension(latest));
        send.PutExtra(Intent.ExtraText, File.ReadAllText(latest));
        return Intent.CreateChooser(send, "Share crash report");
    }

    static void Write(Exception? ex, string kind)
    {
        if (ex == null || _dir == null || ReferenceEquals(ex, _last)) return;
        if (Interlocked.Exchange(ref _writing, 1) == 1) return;
        try
        {
            _last = ex;
            System.IO.Directory.CreateDirectory(_dir);
            var sb = new StringBuilder();
            sb.AppendLine($"MPH Recomp crash report, {DateTime.Now:yyyy-MM-dd HH:mm:ss zzz}");
            sb.AppendLine($"kind: {kind}; thread: {Thread.CurrentThread.Name ?? "(unnamed)"} #{Environment.CurrentManagedThreadId}");
            sb.Append(_header);
            sb.AppendLine().AppendLine(ex.ToString());
            sb.AppendLine().AppendLine("--- last log lines (this process only) ---").AppendLine(LastLogLines(400));
            string path = Path.Combine(_dir, $"crash-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
            File.WriteAllText(path, sb.ToString());
            Android.Util.Log.Error(Tag, "crash report written: " + path);
            Prune();
        }
        catch (Exception e)
        {
            try { Android.Util.Log.Error(Tag, "crash report failed: " + e.Message); } catch { }
        }
        finally { Interlocked.Exchange(ref _writing, 0); }
    }

    static string Header(Context context)
    {
        var sb = new StringBuilder();
        try
        {
            string name = context.PackageName!;
            Android.Content.PM.PackageInfo info = OperatingSystem.IsAndroidVersionAtLeast(33)
                ? context.PackageManager!.GetPackageInfo(name, Android.Content.PM.PackageManager.PackageInfoFlags.Of(0L))!
                : context.PackageManager!.GetPackageInfo(name, 0)!;
            sb.AppendLine($"app: {name} {info.VersionName} ({info.LongVersionCode})");
        }
        catch { sb.AppendLine("app: (version unknown)"); }
        sb.AppendLine($"device: {Android.OS.Build.Manufacturer} {Android.OS.Build.Model} ({Android.OS.Build.Device}, {Android.OS.Build.Hardware})");
        sb.AppendLine($"android: {Android.OS.Build.VERSION.Release} (API {(int)Android.OS.Build.VERSION.SdkInt}), "
            + $"abis {string.Join(",", Android.OS.Build.SupportedAbis ?? Array.Empty<string>())}");
        return sb.ToString();
    }

    static string LastLogLines(int lines)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("logcat", $"-d -t {lines} --pid={Android.OS.Process.MyPid()}")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
            };
            using var p = System.Diagnostics.Process.Start(psi);
            if (p == null) return "(logcat unavailable)";
            Task<string> read = p.StandardOutput.ReadToEndAsync();
            if (!read.Wait(2000))
            {
                try { p.Kill(); } catch { }
                return "(logcat timed out)";
            }
            return read.Result;
        }
        catch (Exception e) { return "(logcat unavailable: " + e.Message + ")"; }
    }

    static void Prune()
    {
        foreach (string old in System.IO.Directory.GetFiles(_dir!, "crash-*.txt").OrderByDescending(f => f, StringComparer.Ordinal).Skip(Keep))
        {
            try { File.Delete(old); } catch { }
        }
    }
}
