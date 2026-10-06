using System.Diagnostics;
using System.Text;
using MphRecomp.App.Platform;

namespace SecondHunt.Desktop;

// The app's log on Windows: logs\log.txt (the previous run's kept as log.prev.txt), the debugger's output, and the last
// lines in memory for a crash report. One writer thread-safe lock; lines are flushed as they come so a crash loses none.
internal static class DesktopLog
{
    const int Keep = 400;
    static readonly object _gate = new();
    static readonly Queue<string> _recent = new();
    static StreamWriter? _file;
    static readonly Stopwatch _clock = Stopwatch.StartNew();
    // the player's own profile folder (it holds their Windows user name) never goes into the log or a crash report:
    // it's written as %USERPROFILE%
    static readonly string _profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile).TrimEnd('\\', '/');

    public static string Scrub(string text) => _profile.Length <= 3 ? text : text
        .Replace(_profile, "%USERPROFILE%", StringComparison.OrdinalIgnoreCase)
        .Replace(_profile.Replace('\\', '/'), "%USERPROFILE%", StringComparison.OrdinalIgnoreCase);

    public static void Init(string logsDir)
    {
        try
        {
            string path = Path.Combine(logsDir, "log.txt");
            if (File.Exists(path))
            {
                File.Move(path, Path.Combine(logsDir, "log.prev.txt"), overwrite: true);
            }
            _file = new StreamWriter(new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite), new UTF8Encoding(false))
            {
                AutoFlush = true,
            };
        }
        catch (Exception ex)
        {
            Debug.WriteLine("log file not opened: " + ex.Message);
        }
        Log.Sink = Write;
        Write(Log.Level.Info, "SecondHunt", $"{BuildFlags.AppName} {BuildFlags.Version} on {Environment.OSVersion}, "
            + $".NET {Environment.Version}, {(Environment.Is64BitProcess ? "x64" : "x86")}, started {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
    }

    public static void Write(Log.Level level, string tag, string message)
    {
        char l = level switch { Log.Level.Debug => 'D', Log.Level.Info => 'I', Log.Level.Warn => 'W', _ => 'E' };
        string line = $"{_clock.Elapsed.TotalSeconds,9:0.000} {l} {tag}: {Scrub(message)}";
        lock (_gate)
        {
            _recent.Enqueue(line);
            while (_recent.Count > Keep) _recent.Dequeue();
            try { _file?.WriteLine(line); } catch { }
        }
        Debug.WriteLine(line);
    }

    public static string Recent()
    {
        lock (_gate)
        {
            return string.Join(Environment.NewLine, _recent);
        }
    }
}

// Crash reports for testers, as on Android (public beta audit S2): an exception that would end the app is written with
// the build, the system, the GPU and the last log lines to crash\crash-<time>.txt (the newest 10 are kept); then the
// app ends as it would have. RECOMP SETTINGS > CRASH LOG opens the folder with the newest one selected.
internal static class DesktopCrashLog
{
    const int Keep = 10;
    static int _writing;
    public static string Gpu = "(GL not started)";

    public static void Install()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Write(e.ExceptionObject as Exception, e.IsTerminating ? "unhandled" : "unhandled, not terminating");
        TaskScheduler.UnobservedTaskException += (_, e) => Log.Warn("MPHCrash", "unobserved task exception: " + e.Exception);
    }

    public static string? Write(Exception? ex, string kind)
    {
        if (ex == null || Interlocked.Exchange(ref _writing, 1) == 1) return null;
        try
        {
            var sb = new StringBuilder();
            sb.AppendLine($"{BuildFlags.AppName} crash report, {DateTime.Now:yyyy-MM-dd HH:mm:ss zzz}");
            sb.AppendLine($"kind: {kind}; thread: {Thread.CurrentThread.Name ?? "(unnamed)"} #{Environment.CurrentManagedThreadId}");
            sb.AppendLine($"app: {BuildFlags.AppName} {BuildFlags.Version} (Windows)");
            sb.AppendLine($"system: {Environment.OSVersion}, {Environment.ProcessorCount} CPUs, .NET {Environment.Version}");
            sb.AppendLine($"gpu: {Gpu}");
            sb.AppendLine().AppendLine(DesktopLog.Scrub(ex.ToString()));
            sb.AppendLine().AppendLine("--- last log lines ---").AppendLine(DesktopLog.Recent());
            string path = Path.Combine(DesktopPaths.Crash, $"crash-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
            File.WriteAllText(path, sb.ToString());
            Log.Error("MPHCrash", "crash report written: " + path);
            foreach (string old in Directory.GetFiles(DesktopPaths.Crash, "crash-*.txt").OrderByDescending(f => f, StringComparer.Ordinal).Skip(Keep))
            {
                try { File.Delete(old); } catch { }
            }
            return path;
        }
        catch (Exception e)
        {
            try { Log.Error("MPHCrash", "crash report failed: " + e.Message); } catch { }
            return null;
        }
        finally
        {
            Interlocked.Exchange(ref _writing, 0);
        }
    }

    public static string? LatestPath() => Directory.Exists(DesktopPaths.Crash)
        ? Directory.GetFiles(DesktopPaths.Crash, "crash-*.txt").OrderByDescending(f => f, StringComparer.Ordinal).FirstOrDefault()
        : null;

    // Explorer on the crash folder, the newest report selected (or the empty folder)
    public static void Open()
    {
        try
        {
            string? latest = LatestPath();
            var psi = latest != null
                ? new ProcessStartInfo("explorer.exe", $"/select,\"{latest}\"")
                : new ProcessStartInfo("explorer.exe", $"\"{DesktopPaths.Crash}\"");
            psi.UseShellExecute = true;
            Process.Start(psi);
        }
        catch (Exception ex)
        {
            Log.Warn("MPHCrash", "couldn't open the crash folder: " + ex.Message);
        }
    }
}
