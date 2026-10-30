namespace SecondHunt.Desktop;

// Where the Windows app keeps things. Nothing of the game ships with it: the player's ROM is unpacked once into Game.
//   %LOCALAPPDATA%\Second Hunt\                 (a dev build: MPH Recomp; --data <dir> or a portable.txt beside the
//     recomp_settings.json, saves\, mods\        exe: that folder / data\ beside the exe)
//     game\        the unpacked ROM (MphRead's working directory: files\, .extracted)
//     cache\       GL program binaries
//     crash\, logs\
internal static class DesktopPaths
{
    public static string Root { get; private set; } = "";
    public static string Game => Path.Combine(Root, "game");
    public static string Cache => Path.Combine(Root, "cache");
    public static string Saves => Path.Combine(Root, "saves");
    public static string Mods => Path.Combine(Root, "mods");
    public static string Crash => Path.Combine(Root, "crash");
    public static string Logs => Path.Combine(Root, "logs");
    public static string Settings => Path.Combine(Root, "recomp_settings.json");
    public static string Extracted => Path.Combine(Game, ".extracted");

    public static void Init(string? dataArg)
    {
        string exeDir = AppContext.BaseDirectory;
        if (dataArg != null)
        {
            Root = Path.GetFullPath(dataArg);
        }
        else if (File.Exists(Path.Combine(exeDir, "portable.txt")))
        {
            Root = Path.Combine(exeDir, "data");
        }
        else
        {
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            Root = Path.Combine(local, BuildFlags.Public ? "Second Hunt" : "MPH Recomp");
        }
        foreach (string dir in new[] { Root, Game, Cache, Saves, Mods, Crash, Logs })
        {
            Directory.CreateDirectory(dir);
        }
    }
}

internal static class BuildFlags
{
#if MPH_PUBLIC
    public const bool Public = true;
#else
    public const bool Public = false;
#endif

    public static string AppName => Public ? "Second Hunt" : "MPH Recomp";

    public static string Version =>
        typeof(BuildFlags).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            is [System.Reflection.AssemblyInformationalVersionAttribute v, ..] ? v.InformationalVersion : "dev";
}
