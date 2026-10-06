namespace MphRecomp.App.Platform;

// What this shared app layer needs from the platform it runs on, set once by the host (MphRead.Android's MphApp, the
// Windows host's Program) before any screen starts. Everything else here is plain .NET plus the GL binding (GLES30).
public static class AppPlatform
{
    // PCM output for music, sound effects and movie audio (Android: AudioTrack; Windows: one miniaudio device)
    public static IAudioOutput Audio { get; set; } = NullAudioOutput.Instance;

    // A PNG file as RGBA8 pixels, rows top first, packed one texel per int (memory order R, G, B, A, which is what
    // GL_RGBA / GL_UNSIGNED_BYTE reads); null if it can't be read. Called on worker threads.
    public static Func<string, (int[] Pixels, int Width, int Height)?> DecodeImage { get; set; } = _ => null;

    // private storage for caches that survive restarts (GL program binaries): Android's files dir, Windows' local
    // app data folder
    public static string CacheDir { get; set; } = Path.GetTempPath();

    // A PNG file from RGBA8 bytes (rows top first), or false if it couldn't be written. Worker threads.
    public static Func<string, byte[], int, int, bool> EncodePng { get; set; } = (_, _, _, _) => false;

    // the runtime's own counters for the perf log (Android: ART's "art.gc.gc-count" / "art.gc.gc-time"); 0 if none
    public static Func<string, long> RuntimeStat { get; set; } = _ => 0;
}

// One line of the app's log. The host's sink writes it (Android: logcat; Windows: the log file); with no sink set,
// lines are dropped.
public static class Log
{
    public enum Level { Debug, Info, Warn, Error }

    public static Action<Level, string, string>? Sink { get; set; }

    public static void Debug(string tag, string message) => Sink?.Invoke(Level.Debug, tag, message);
    public static void Info(string tag, string message) => Sink?.Invoke(Level.Info, tag, message);
    public static void Warn(string tag, string message) => Sink?.Invoke(Level.Warn, tag, message);
    public static void Error(string tag, string message) => Sink?.Invoke(Level.Error, tag, message);
}
