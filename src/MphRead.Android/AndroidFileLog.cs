using System.Collections.Concurrent;
using System.Text;
using Android.Content;
using AndroidLog = Android.Util.Log;
using AppLog = MphRecomp.App.Platform.Log;

namespace MphRecomp.App;

// The app's own log file on Android (owner 11-04): logcat is a shared ring buffer and the system's own lines push ours
// out within minutes, so a whole match was never recoverable. Everything the app logs (Log.* and Console.WriteLine)
// still goes to logcat and is also written to <external files>/logs/log.txt with the wall-clock time; the last few runs
// are kept as log.1.txt (newest) .. log.4.txt. `adb pull /sdcard/Android/data/<package>/files/logs`.
// Lines are written by a background thread, so the game and render threads never wait on the disk.
// The main process also copies its own logcat lines that didn't come through here (11-04, "are we losing anything":
// ART's GC lines, Choreographer's "Skipped N frames", OpenGLRenderer / libEGL, the audio HAL, native crashes, and the
// Android-side lines that call Android.Util.Log directly, e.g. RenderActivity's MPHRender) into the file, each marked
// "[logcat]", so the file has everything the old pid-filtered logcat SHARE LOG had.
// (11-06) Each run's file starts with the date, pid and process, every line carries its thread id like logcat's
// threadtime ("HH:MM:SS.mmm  tid L tag: message"), and lines dropped on a full queue leave a "N lines dropped" marker.
// The main process also writes how the app's last processes ended (Android 11+ exit reasons: crash, native crash, ANR,
// low memory, killed, swiped away), since a dying process can't log its own last moments and logcat's --pid never
// had the system's side of it.
internal static class AndroidFileLog
{
    const int KeepRuns = 4; // older runs besides this one
    const long MaxBytes = 64L * 1024 * 1024; // per run; a runaway logger stops at this, the game goes on
    static readonly BlockingCollection<string> _lines = new(new ConcurrentQueue<string>(), 20000);
    static string? _path;
    static volatile bool _copying;
    static int _dropped; // lines lost to a full queue since the last marker
    // lines Write put in the file that logcat will echo (tag + line -> how many still to come), so the copy skips them
    static readonly ConcurrentDictionary<string, int> _echo = new();
    static readonly System.Text.RegularExpressions.Regex _logcatLine =
        new(@"^\d\d-\d\d (\S+)\s+\d+\s+(\d+) (\w) (.*?)\s*: (.*)$", System.Text.RegularExpressions.RegexOptions.Compiled);

    public static string? Path => _path;

    // process: the app's process name suffix ("" for the main process, "replayprep" for the keyframe service), so the
    // service doesn't rotate away the game's log
    public static void Install(Context context, string process)
    {
        try
        {
            string dir = System.IO.Path.Combine(context.GetExternalFilesDir(null)?.AbsolutePath ?? context.FilesDir!.AbsolutePath, "logs");
            Directory.CreateDirectory(dir);
            string name = process.Length == 0 ? "log" : "log-" + process;
            string path = System.IO.Path.Combine(dir, name + ".txt");
            for (int i = KeepRuns; i >= 1; i--)
            {
                string from = i == 1 ? path : System.IO.Path.Combine(dir, $"{name}.{i - 1}.txt");
                if (File.Exists(from))
                {
                    File.Move(from, System.IO.Path.Combine(dir, $"{name}.{i}.txt"), overwrite: true);
                }
            }
            var file = new StreamWriter(new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite), new UTF8Encoding(false));
            file.WriteLine($"--- {DateTime.Now:yyyy-MM-dd HH:mm:ss zzz}, pid {Android.OS.Process.MyPid()}, process {(process.Length == 0 ? "main" : process)} ---");
            _path = path;
            var writer = new Thread(() => Drain(file)) { IsBackground = true, Name = "filelog" };
            writer.Start();
            if (process.Length == 0)
            {
                _copying = true;
                new Thread(CopyLogcat) { IsBackground = true, Name = "filelog-logcat" }.Start();
                new Thread(() => LastExits(context)) { IsBackground = true, Name = "filelog-exits" }.Start();
            }
        }
        catch (Exception ex)
        {
            AndroidLog.Warn("MPHLog", "log file not opened: " + ex.Message);
        }
    }

    public static void Write(AppLog.Level level, string tag, string message)
    {
        if (_path == null)
        {
            return;
        }
        if (_copying && message.Length < 4000) // (logcat splits longer ones; those few come in twice)
        {
            if (_echo.Count > 20000)
            {
                _echo.Clear(); // (logcat dropped some: forget, at worst a few lines twice)
            }
            foreach (string part in message.Split('\n'))
            {
                _echo.AddOrUpdate(EchoKey(tag, part), 1, (_, n) => n + 1);
            }
        }
        char l = level switch { AppLog.Level.Debug => 'D', AppLog.Level.Info => 'I', AppLog.Level.Warn => 'W', _ => 'E' };
        Add($"{DateTime.Now:HH:mm:ss.fff} {Android.OS.Process.MyTid(),5} {l} {tag}: {message}");
    }

    // full queue (disk stuck): the line is dropped and counted, and Drain writes the count once it catches up
    static void Add(string line)
    {
        if (!_lines.TryAdd(line))
        {
            Interlocked.Increment(ref _dropped);
        }
    }

    // this run's whole log so far (SHARE LOG, crash reports): waits a moment for queued lines to reach the file; null
    // when there's no log file
    public static string? ReadAll(int maxChars = 0)
    {
        if (_path == null)
        {
            return null;
        }
        for (int i = 0; i < 50 && _lines.Count > 0; i++)
        {
            Thread.Sleep(10);
        }
        Thread.Sleep(20); // (the writer flushes once its queue is empty)
        try
        {
            using var reader = new StreamReader(new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite), Encoding.UTF8);
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

    // the run before this one (log.1.txt; its last maxChars when bigger), or null when there's none: SHARE LOG sends it
    // too, since a player often restarts the app after a bad match before sharing
    public static string? PreviousPath => _path == null ? null
        : System.IO.Path.Combine(System.IO.Path.GetDirectoryName(_path)!, System.IO.Path.GetFileNameWithoutExtension(_path) + ".1.txt");

    public static string? ReadPrevious(int maxChars) => ReadTail(PreviousPath, maxChars);

    // the keyframe service's log (its own process, log-replayprep.txt beside this one), for SHARE LOG
    public static string? ServicePath => _path == null ? null : System.IO.Path.Combine(System.IO.Path.GetDirectoryName(_path)!, "log-replayprep.txt");

    public static string? ReadService(int maxChars) => ReadTail(ServicePath, maxChars);

    static string? ReadTail(string? path, int maxChars)
    {
        if (path == null || !File.Exists(path))
        {
            return null;
        }
        try
        {
            using var reader = new StreamReader(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite), Encoding.UTF8);
            if (reader.BaseStream.Length > maxChars)
            {
                reader.BaseStream.Seek(-maxChars, SeekOrigin.End);
                reader.ReadLine(); // (a part line)
                return $"(its first {reader.BaseStream.Length - maxChars:N0} bytes left out)\n" + reader.ReadToEnd();
            }
            return reader.ReadToEnd();
        }
        catch (Exception ex)
        {
            return $"(log file unreadable: {ex.Message})";
        }
    }

    // `logcat -v threadtime --pid=<ours>` (from this process's first line, then live) into the file, minus the lines
    // Write already put there. Lines look like "MM-DD HH:MM:SS.mmm  pid  tid L tag: message"; they go in as
    // "HH:MM:SS.mmm  tid L tag: message [logcat]" (their own time, so they can sit a few ms out of order)
    static void CopyLogcat()
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("logcat", $"-v threadtime --pid={Android.OS.Process.MyPid()}")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
            };
            using var p = System.Diagnostics.Process.Start(psi);
            if (p == null)
            {
                return;
            }
            string? line;
            while ((line = p.StandardOutput.ReadLine()) != null)
            {
                System.Text.RegularExpressions.Match m = _logcatLine.Match(line);
                if (!m.Success)
                {
                    continue; // ("--------- beginning of main" and the like)
                }
                string tag = m.Groups[4].Value, text = m.Groups[5].Value, key = EchoKey(tag, text);
                if (_echo.TryGetValue(key, out int n))
                {
                    if (n <= 1) _echo.TryRemove(key, out _);
                    else _echo[key] = n - 1;
                    continue;
                }
                Add($"{m.Groups[1].Value} {m.Groups[2].Value,5} {m.Groups[3].Value} {tag}: {text} [logcat]");
            }
        }
        catch (Exception ex)
        {
            AndroidLog.Warn("MPHLog", "logcat copy stopped: " + ex.Message);
        }
    }

    // the app's last few process exits as Android recorded them (ApplicationExitInfo, API 30+): reason, signal or exit
    // code, memory, and for an ANR the system's thread dump (text; native crashes' trace is a binary tombstone, left out)
    static void LastExits(Context context)
    {
        if (!OperatingSystem.IsAndroidVersionAtLeast(30))
        {
            return;
        }
        try
        {
            var am = (Android.App.ActivityManager?)context.GetSystemService(Context.ActivityService);
            var exits = am?.GetHistoricalProcessExitReasons(context.PackageName, 0, 4);
            if (exits == null)
            {
                return;
            }
            foreach (Android.App.ApplicationExitInfo e in exits)
            {
                int reason = (int)e.Reason;
                string name = reason switch
                {
                    1 => "exited itself", 2 => "killed by a signal", 3 => "low memory kill", 4 => "crash (Java/.NET)",
                    5 => "native crash", 6 => "ANR (not responding)", 7 => "start-up failed", 8 => "permission changed",
                    9 => "too much resource use", 10 => "user requested", 11 => "user stopped (swiped/force stop)",
                    12 => "a dependency died", 13 => "other", 14 => "frozen", 15 => "package state changed",
                    16 => "app updated", _ => "reason " + reason,
                };
                Add($"{DateTime.Now:HH:mm:ss.fff} {"",5} I MPHExit: {DateTimeOffset.FromUnixTimeMilliseconds(e.Timestamp).LocalDateTime:yyyy-MM-dd HH:mm:ss} "
                    + $"{e.ProcessName} pid {e.Pid}: {name}, status {e.Status}, importance {e.Importance}, pss {e.Pss / 1024} MB, rss {e.Rss / 1024} MB"
                    + (string.IsNullOrEmpty(e.Description) ? "" : ", " + e.Description));
                if (reason == 6 && OperatingSystem.IsAndroidVersionAtLeast(31))
                {
                    using Stream? trace = e.TraceInputStream;
                    if (trace != null)
                    {
                        using var reader = new StreamReader(trace);
                        string text = reader.ReadToEnd();
                        if (text.Length > 200_000)
                        {
                            text = text.Substring(0, 200_000) + "\n(trace cut at 200 KB)";
                        }
                        foreach (string line in text.Split('\n'))
                        {
                            Add($"{DateTime.Now:HH:mm:ss.fff} {"",5} I MPHExit:   {line.TrimEnd('\r')}");
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Add($"{DateTime.Now:HH:mm:ss.fff} {"",5} W MPHExit: exit reasons unavailable: {ex.Message}");
        }
    }

    static string EchoKey(string tag, string line) => tag + "\u0001" + line;

    static void Drain(StreamWriter file)
    {
        long bytes = 0;
        try
        {
            foreach (string line in _lines.GetConsumingEnumerable())
            {
                if (bytes > MaxBytes)
                {
                    continue;
                }
                int dropped = Interlocked.Exchange(ref _dropped, 0);
                if (dropped > 0)
                {
                    file.WriteLine($"{DateTime.Now:HH:mm:ss.fff} {"",5} W MPHLog: {dropped} lines dropped here (the log queue was full)");
                }
                file.WriteLine(line);
                bytes += line.Length + 1;
                if (bytes > MaxBytes)
                {
                    file.WriteLine($"{DateTime.Now:HH:mm:ss.fff} {"",5} W MPHLog: log file full ({MaxBytes >> 20} MB), the rest of this run is in logcat only");
                }
                if (_lines.Count == 0)
                {
                    file.Flush(); // a crash or a kill loses at most the lines still queued
                }
            }
        }
        catch (Exception ex)
        {
            AndroidLog.Warn("MPHLog", "log file writer stopped: " + ex.Message);
        }
    }

    // Console.WriteLine (the sim's own debug lines): to logcat as before (tag DOTNET) and to the file
    public sealed class ConsoleWriter : TextWriter
    {
        readonly StringBuilder _line = new();
        public override Encoding Encoding => Encoding.UTF8;

        public override void Write(char value)
        {
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

        public override void WriteLine(string? value)
        {
            Write(value);
            Emit();
        }

        public override void Flush()
        {
            if (_line.Length > 0)
            {
                Emit();
            }
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
            AndroidFileLog.Write(AppLog.Level.Info, "DOTNET", text);
            AndroidLog.Info("DOTNET", text);
        }
    }
}
