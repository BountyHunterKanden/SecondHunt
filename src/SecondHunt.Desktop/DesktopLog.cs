using System.Diagnostics;
using System.Text;
using MphRecomp.App.Platform;

namespace SecondHunt.Desktop;

// The app's log on Windows, as on Android (AndroidFileLog; owner 11-14 "the most helpful logs"): the whole run in
// logs\log.txt, the last runs kept as log.1.txt (newest) .. log.4.txt, a 64 MB cap a runaway logger stops at; a replay's
// keyframe prepare (its own process) writes logs\log-replayprep.txt the same way. Lines are LogFormat's (ISO time, thread,
// level, area tag) and the file starts with the run's title and MPHRun lines (RunInfo). Console.WriteLine goes in too
// (tag DOTNET) and still reaches stdout. Lines are flushed as they come so a crash loses none; the last ones are also
// kept in memory for a crash report once the file is full.
internal static class DesktopLog
{
    const int Keep = 400;
    const int KeepRuns = 4; // older runs besides this one
    // per run; a runaway logger stops at this, the game goes on (a field, not a const, so a log test can lower it)
    static long MaxBytes = 64L * 1024 * 1024;
    static readonly object _gate = new();
    static readonly Queue<string> _recent = new();
    static StreamWriter? _file;
    static string? _path;
    static long _bytes;
    // the player's own profile folder (it holds their Windows user name) never goes into the log or a crash report:
    // it's written as %USERPROFILE%
    static readonly string _profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile).TrimEnd('\\', '/');

    public static string Scrub(string text) => _profile.Length <= 3 ? text : text
        .Replace(_profile, "%USERPROFILE%", StringComparison.OrdinalIgnoreCase)
        .Replace(_profile.Replace('\\', '/'), "%USERPROFILE%", StringComparison.OrdinalIgnoreCase);

    // this run's log file (null when it couldn't be opened)
    public static string? FilePath => _path;

    // name: "log" (the app) or "log-replayprep" (the keyframe prepare)
    public static void Init(string logsDir, string name = "log")
    {
        try
        {
            Directory.CreateDirectory(logsDir);
            string path = Path.Combine(logsDir, name + ".txt");
            try
            {
                for (int i = KeepRuns; i >= 1; i--)
                {
                    string from = i == 1 ? path : Path.Combine(logsDir, $"{name}.{i - 1}.txt");
                    if (File.Exists(from))
                    {
                        File.Move(from, Path.Combine(logsDir, $"{name}.{i}.txt"), overwrite: true);
                    }
                }
            }
            catch (IOException)
            {
                // another copy of the game is running and holds log.txt: this one writes log-<pid>.txt beside it (not
                // rotated; a normal start deletes ones older than a week)
                path = Path.Combine(logsDir, $"{name}-{Environment.ProcessId}.txt");
            }
            if (name == "log")
            {
                // the old layout (one kept run each, before 11-16)
                foreach (string old in new[] { "log.prev.txt", "replayprep.txt", "replayprep.prev.txt" })
                {
                    try { File.Delete(Path.Combine(logsDir, old)); } catch { }
                }
                if (path.EndsWith("log.txt", StringComparison.Ordinal))
                {
                    // second copies' logs (log-<pid>.txt) from runs before this one
                    foreach (string extra in Directory.GetFiles(logsDir, "log-*.txt"))
                    {
                        string stem = Path.GetFileNameWithoutExtension(extra)[4..];
                        if (stem.Length > 0 && stem.All(char.IsAsciiDigit) && File.GetLastWriteTimeUtc(extra) < DateTime.UtcNow.AddDays(-7))
                        {
                            try { File.Delete(extra); } catch { }
                        }
                    }
                }
            }
            _file = new StreamWriter(new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite), new UTF8Encoding(false))
            {
                AutoFlush = true,
            };
            _path = path;
            _file.WriteLine(LogFormat.Title(BuildFlags.AppName, $"{(name == "log" ? "main" : name.Replace("log-", ""))}, pid {Environment.ProcessId}"));
        }
        catch (Exception ex)
        {
            Debug.WriteLine("log file not opened: " + ex.Message);
        }
        Log.Sink = Write;
        Console.SetOut(new ConsoleTee(Console.Out, Log.Level.Info));
        Console.SetError(new ConsoleTee(Console.Error, Log.Level.Warn));
    }

    // the MPHRun lines (app, system, ROM, settings) after the title line; Program calls it once the paths are set
    public static void RunInfo() => LogFormat.LogRun(App(), SystemLine(), DesktopPaths.Game, DesktopPaths.Settings);

    // Windows builds have no version code: the build's time stands in for it
    public static string App()
    {
        string built = "?";
        try
        {
            string? file = typeof(DesktopLog).Assembly.Location is { Length: > 0 } dll ? dll : Environment.ProcessPath;
            if (file != null) built = LogFormat.Stamp(File.GetLastWriteTime(file));
        }
        catch { }
        return $"{BuildFlags.AppName} {BuildFlags.Version} ({(BuildFlags.Public ? "public" : "dev")} build, built {built})";
    }

    public static string SystemLine() => $"Windows: {Environment.OSVersion}, {Environment.ProcessorCount} CPUs, "
        + $"{(Environment.Is64BitProcess ? "x64" : "x86")}, .NET {Environment.Version}";

    public static void Write(Log.Level level, string tag, string message)
    {
        string line = LogFormat.Line(level, Environment.CurrentManagedThreadId, tag, Scrub(message));
        lock (_gate)
        {
            _recent.Enqueue(line);
            while (_recent.Count > Keep) _recent.Dequeue();
            if (_file != null)
            {
                try
                {
                    _bytes += Encoding.UTF8.GetByteCount(line) + 2;
                    if (_bytes > MaxBytes)
                    {
                        _file.WriteLine(LogFormat.Line(LogFormat.Stamp(), "", 'W', "MPHLog", $"log file full ({MaxBytes >> 20} MB), "
                            + $"the rest of this run isn't written (a crash report still gets the last {Keep} lines)"));
                        _file.Dispose();
                        _file = null;
                    }
                    else
                    {
                        _file.WriteLine(line);
                    }
                }
                catch { }
            }
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

    public static bool Full
    {
        get { lock (_gate) return _path != null && _file == null; }
    }

    // this run's whole log so far (its last maxChars when bigger)
    public static string? ReadAll(int maxChars = 0) => ReadTail(_path, maxChars);

    // the run before this one (log.1.txt): SAVE LOG sends it too, since a player often restarts after a bad match
    public static string? PreviousPath => _path == null ? null
        : Path.Combine(Path.GetDirectoryName(_path)!, Path.GetFileNameWithoutExtension(_path) + ".1.txt");

    // the keyframe prepare's latest run, beside this one
    public static string? ReplayPrepPath => _path == null ? null : Path.Combine(Path.GetDirectoryName(_path)!, "log-replayprep.txt");

    public static string? ReadTail(string? path, int maxChars)
    {
        if (path == null || !File.Exists(path))
        {
            return null;
        }
        try
        {
            using var reader = new StreamReader(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete), Encoding.UTF8);
            if (maxChars > 0 && reader.BaseStream.Length > maxChars)
            {
                reader.BaseStream.Seek(-maxChars, SeekOrigin.End);
                reader.ReadLine(); // (a part line)
            }
            return reader.ReadToEnd();
        }
        catch (Exception ex)
        {
            return $"(log file unreadable: {ex.Message})";
        }
    }

    // Console.Out / Console.Error: still written where they went (a test's redirected stdout), and each line into the
    // log as DOTNET (Console.SetOut makes it thread-safe)
    sealed class ConsoleTee : TextWriter
    {
        readonly TextWriter _inner;
        readonly Log.Level _level;
        readonly StringBuilder _line = new();

        public ConsoleTee(TextWriter inner, Log.Level level)
        {
            _inner = inner;
            _level = level;
        }

        public override Encoding Encoding => _inner.Encoding;

        public override void Write(char value)
        {
            try { _inner.Write(value); } catch { }
            if (value == '\n')
            {
                Emit();
            }
            else if (value != '\r')
            {
                _line.Append(value);
            }
        }

        public override void Write(string? value)
        {
            try { _inner.Write(value); } catch { }
            Feed(value);
        }

        public override void WriteLine(string? value)
        {
            try { _inner.WriteLine(value); } catch { }
            Feed(value);
            Emit();
        }

        void Feed(string? value)
        {
            if (value == null)
            {
                return;
            }
            int start = 0;
            for (int i = 0; i < value.Length; i++)
            {
                if (value[i] == '\n')
                {
                    _line.Append(value, start, i - start);
                    Emit();
                    start = i + 1;
                }
            }
            _line.Append(value, start, value.Length - start);
        }

        public override void Flush()
        {
            try { _inner.Flush(); } catch { }
        }

        void Emit()
        {
            int n = _line.Length;
            if (n > 0 && _line[n - 1] == '\r')
            {
                _line.Length = n - 1;
            }
            string text = _line.ToString();
            _line.Clear();
            DesktopLog.Write(_level, LogFormat.ConsoleTag(text), text); // (network lines get [net])
        }
    }
}

// Crash reports for testers, as on Android (public beta audit S2): an exception that would end the app is written with
// the build, the system, the GPU and this run's log to crash\crash-<time>.txt (the newest 10 are kept); then the app
// ends as it would have. RECOMP SETTINGS > CRASH LOG opens the folder with the newest one selected.
internal static class DesktopCrashLog
{
    const int Keep = 10;
    const int CrashLogMax = 200_000;      // a crash report's tail of this run's log
    const int PreviousRunMax = 8_000_000; // SAVE LOG: the run before, its last 8 MB when bigger
    const int ReplayPrepMax = 1_000_000;  // SAVE LOG: the keyframe prepare's latest run
    static int _writing;
    public static string Gpu = "(GL not started)";

    public static void Install()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Write(e.ExceptionObject as Exception, e.IsTerminating ? "unhandled" : "unhandled, not terminating");
        TaskScheduler.UnobservedTaskException += (_, e) => Log.Warn("MPHCrash", "unobserved task exception: " + e.Exception);
    }

    // the report's first lines: what it is, the run, the build, the system, the GPU
    static void Header(StringBuilder sb, string what)
    {
        sb.AppendLine($"{BuildFlags.AppName} {what}, {LogFormat.Stamp()}");
        sb.AppendLine($"run: {LogFormat.RunId}, started {LogFormat.Stamp(LogFormat.Started)}");
        sb.AppendLine("app: " + DesktopLog.App());
        sb.AppendLine("system: " + DesktopLog.SystemLine());
        sb.AppendLine($"gpu: {Gpu}");
    }

    public static string? Write(Exception? ex, string kind)
    {
        if (ex == null || Interlocked.Exchange(ref _writing, 1) == 1) return null;
        try
        {
            var sb = new StringBuilder();
            Header(sb, "crash report");
            sb.AppendLine($"kind: {kind}; thread: {Thread.CurrentThread.Name ?? "(unnamed)"} #{Environment.CurrentManagedThreadId}");
            sb.AppendLine().AppendLine(DesktopLog.Scrub(ex.ToString()));
            string? log = DesktopLog.ReadAll(CrashLogMax);
            if (log != null)
            {
                sb.AppendLine().AppendLine($"--- this run's log, its last {CrashLogMax / 1000} KB ({DesktopLog.Scrub(DesktopLog.FilePath!)}) ---").AppendLine(log);
            }
            if (log == null || DesktopLog.Full)
            {
                sb.AppendLine().AppendLine("--- last log lines ---").AppendLine(DesktopLog.Recent());
            }
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

    // RECOMP SETTINGS > SAVE LOG (2026-10-19; whole runs 11-16, as Android's LogReport): the header with the run's
    // settings now, the run before this one, the keyframe prepare's latest run, then this whole run. Saved to
    // Downloads\Second Hunt\second-hunt-log-<time>.txt as Android's Save to Downloads does (11-17, owner: it's not a
    // crash log, so not in the crash folder); the player's own folder, so nothing there is pruned
    public static string? WriteLog()
    {
        try
        {
            string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "Second Hunt");
            Directory.CreateDirectory(folder);
            var sb = new StringBuilder();
            Header(sb, "log (saved by the player, no crash)");
            foreach (string line in LogFormat.RunLines(DesktopLog.App(), DesktopLog.SystemLine(), DesktopPaths.Game, DesktopPaths.Settings).Skip(3))
            {
                sb.AppendLine(DesktopLog.Scrub(line)); // rom and settings, as they are now
            }
            if (DesktopLog.ReadTail(DesktopLog.PreviousPath, PreviousRunMax) is string previous)
            {
                sb.AppendLine().AppendLine($"--- the run before this one ({Path.GetFileName(DesktopLog.PreviousPath)}) ---").Append(previous);
            }
            if (DesktopLog.ReadTail(DesktopLog.ReplayPrepPath, ReplayPrepMax) is string prep)
            {
                sb.AppendLine().AppendLine($"--- replay keyframe prepare, its latest run ({Path.GetFileName(DesktopLog.ReplayPrepPath)}) ---").Append(prep);
            }
            string? log = DesktopLog.ReadAll();
            sb.AppendLine().AppendLine($"--- this run's log ({Path.GetFileName(DesktopLog.FilePath) ?? "not opened"}) ---")
                .Append(log ?? DesktopLog.Recent());
            if (log != null && DesktopLog.Full)
            {
                sb.AppendLine().AppendLine("--- last log lines (the file is full) ---").AppendLine(DesktopLog.Recent());
            }
            string path = Path.Combine(folder, $"second-hunt-log-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
            File.WriteAllText(path, sb.ToString());
            Log.Info("MPHCrash", "log saved: " + DesktopLog.Scrub(path));
            return path;
        }
        catch (Exception ex)
        {
            Log.Warn("MPHCrash", "log report failed: " + ex.Message);
            return null;
        }
    }

    // Explorer with a file selected (a saved log, a replay to send on)
    public static void Show(string? path)
    {
        if (path == null || !File.Exists(path)) return;
        try
        {
#if MPH_LINUX
            LinuxDialogs.ShowFolder(Path.GetDirectoryName(path)!);
#else
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
#endif
        }
        catch (Exception ex)
        {
            Log.Warn("MPHCrash", "couldn't show " + path + ": " + ex.Message);
        }
    }

    // Explorer on the crash folder, the newest report selected (or the empty folder)
    public static void Open()
    {
        try
        {
#if MPH_LINUX
            LinuxDialogs.ShowFolder(DesktopPaths.Crash);
#else
            string? latest = LatestPath();
            var psi = latest != null
                ? new ProcessStartInfo("explorer.exe", $"/select,\"{latest}\"")
                : new ProcessStartInfo("explorer.exe", $"\"{DesktopPaths.Crash}\"");
            psi.UseShellExecute = true;
            Process.Start(psi);
#endif
        }
        catch (Exception ex)
        {
            Log.Warn("MPHCrash", "couldn't open the crash folder: " + ex.Message);
        }
    }
}
