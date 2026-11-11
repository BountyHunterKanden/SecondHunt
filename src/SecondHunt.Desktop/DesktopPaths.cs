namespace SecondHunt.Desktop;

// Where the Windows app keeps things. Nothing of the game ships with it: the player's ROM is unpacked once into Game.
//   %LOCALAPPDATA%\Second Hunt\                 (a dev build: MPH Recomp; --data <dir> or a portable.txt beside the
//     recomp_settings.json, saves\, mods\        exe: that folder / data\ beside the exe)
//     game\        the unpacked ROM (MphRead's working directory: files\, .extracted)
//     cache\       GL program binaries
//     crash\, logs\
// (GitHub #12, owner 2026-11-04) a fresh install asks which of the two (DataFolderChoice), and SETTINGS > APP > FILES moves
// everything between them: the ask leaves a move_pending.txt in the current folder and the app restarts; the next start
// moves the folder before anything in it is open (Init), then flips portable.txt
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

    // the data folder beside the exe (portable.txt) rather than the user's AppData; Fixed: --data chose it
    public static bool Portable { get; private set; }
    public static bool Fixed { get; private set; }
    // a move asked for in SETTINGS: the app starts itself again once it has closed (Program)
    public static bool RestartPending { get; private set; }
    // what Init did before the log was open (Program logs these)
    public static readonly List<string> Notes = new();
    // a finished or failed move, told once the app is up
    public static string? MoveResult { get; private set; }

    static string ExeDir => AppContext.BaseDirectory;
    static string PortableFlag => Path.Combine(ExeDir, "portable.txt");
    static string PortableRoot => Path.Combine(ExeDir, "data");
    // (a dev build's tests can stand in another AppData: MPH_TEST_LOCALAPPDATA, so a move test never touches the real one)
    static string LocalRoot => Path.Combine(
        !BuildFlags.Public && Environment.GetEnvironmentVariable("MPH_TEST_LOCALAPPDATA") is { Length: > 0 } test ? test
            : Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        BuildFlags.Public ? "Second Hunt" : "MPH Recomp");
    const string MoveMarker = "move_pending.txt";

    // ask: a fresh install may ask where its files go (not for a test run or a replay prepare)
    public static void Init(string? dataArg, bool ask)
    {
        if (dataArg != null)
        {
            Root = Path.GetFullPath(dataArg);
            Fixed = true;
        }
        else
        {
            if (ask && !File.Exists(PortableFlag) && Fresh(LocalRoot) && Writable(ExeDir) && DataFolderChoice.AskPortable())
            {
                try { WriteFlag(); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Notes.Add("portable.txt: " + ex.Message); }
            }
            Portable = File.Exists(PortableFlag);
            Root = Portable ? PortableRoot : LocalRoot;
            if (File.Exists(Path.Combine(Root, MoveMarker))) FinishMove();
        }
        foreach (string dir in new[] { Root, Game, Cache, Saves, Mods, Crash, Logs })
        {
            Directory.CreateDirectory(dir);
        }
    }

    // SETTINGS > APP > FILES: the row's value, null = no row (--data set the folder)
    public static string? Place => Fixed ? null : Portable ? "PORTABLE" : "APPDATA";

    // the question before a move, or (Blocked) why it can't happen
    public static (string Text, bool Blocked) MoveQuestion()
    {
        if (!Portable)
        {
            if (!Writable(ExeDir))
            {
                return ("windows won't let the game write to its own folder, so its files can't go there. move the second hunt folder somewhere you own, like documents, and try again.", true);
            }
            return ("move your files into a data folder beside the game, to carry them with it? the game closes and opens again to do it."
                + (HasFiles(PortableRoot) ? " the data folder that's already there is kept, renamed to \"data (old)\"." : ""), false);
        }
        return ("move your files back to appdata (your windows user folder)? the game closes and opens again to do it."
            + (HasFiles(LocalRoot) ? " the files already there are kept, in a folder named \"(old)\"." : ""), false);
    }

    // after yes: the move waits for the next start, which this app makes itself once it has closed
    public static void RequestMove()
    {
        try
        {
            File.WriteAllText(Path.Combine(Root, MoveMarker), Portable ? "local" : "portable");
            RestartPending = true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MphRecomp.App.Platform.Log.Warn("MPHDesktop", "couldn't ask for the data move: " + ex.Message);
        }
    }

    // the move itself, before anything in either folder is open: the whole folder (settings, saves, mods, the unpacked game,
    // replays, the online identity...) in one rename on the same drive, else a copy checked file by file and the old one
    // deleted. Whatever already sits at the other place is kept beside it as "<name> (old)". On any failure nothing changes.
    static void FinishMove()
    {
        string from = Root, marker = Path.Combine(from, MoveMarker);
        bool toPortable = !Portable;
        string to = toPortable ? PortableRoot : LocalRoot;
        string? aside = null;
        bool moved = false;
        try
        {
            if (toPortable && !Writable(ExeDir)) throw new UnauthorizedAccessException("the game's folder can't be written to");
            File.Delete(marker);
            if (HasFiles(to))
            {
                aside = FreeName(to + " (old)");
                Directory.Move(to, aside);
            }
            else if (Directory.Exists(to))
            {
                Directory.Delete(to, recursive: true); // (only empty folders)
            }
            if (SameDrive(from, to))
            {
                // right after the restart something can still hold the folder for a moment (11-09: "access is denied"),
                // so a few more tries over ~5 s before giving up
                for (int tries = 1; ; tries++)
                {
                    try
                    {
                        Directory.Move(from, to);
                        if (tries > 1) Notes.Add($"data move: worked on try {tries}");
                        break;
                    }
                    catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException) && tries < 20)
                    {
                        System.Threading.Thread.Sleep(250);
                    }
                }
                moved = true;
            }
            else
            {
                Copy(from, to);
            }
            if (toPortable) WriteFlag();
            else File.Delete(PortableFlag);
            Portable = toPortable;
            Root = to;
            if (!moved)
            {
                try { Directory.Delete(from, recursive: true); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Notes.Add($"old data folder left at {from}: {ex.Message}"); }
            }
            Notes.Add($"data moved: {from} -> {to}" + (aside != null ? $" (what was there: {aside})" : ""));
            MoveResult = $"your files are now in\n{to}"
                + (aside != null ? $"\n\nwhat was there before is kept in\n{aside}" : "");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Notes.Add($"data move {from} -> {to} failed: {ex}");
            if (moved) // (moved, but portable.txt wouldn't change: back where the flag says)
            {
                try { Directory.Move(to, from); } catch (Exception) { }
            }
            else if (!SamePath(to, from))
            {
                try { if (Directory.Exists(to)) Directory.Delete(to, recursive: true); } catch (Exception) { } // (the partial copy)
            }
            if (aside != null && !Directory.Exists(to))
            {
                try { Directory.Move(aside, to); } catch (Exception) { }
            }
            try { File.Delete(marker); } catch (Exception) { }
            MoveResult = $"your files couldn't be moved ({ex.Message}), so they're still in\n{Root}";
        }
    }

    // every file, then the same count and sizes read back
    static void Copy(string from, string to)
    {
        long files = 0, bytes = 0;
        foreach (string dir in Directory.EnumerateDirectories(from, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(to, Path.GetRelativePath(from, dir)));
        }
        Directory.CreateDirectory(to);
        foreach (string file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            string dest = Path.Combine(to, Path.GetRelativePath(from, file));
            File.Copy(file, dest, overwrite: false);
            files++;
            bytes += new FileInfo(file).Length;
        }
        long gotFiles = 0, gotBytes = 0;
        foreach (string file in Directory.EnumerateFiles(to, "*", SearchOption.AllDirectories))
        {
            gotFiles++;
            gotBytes += new FileInfo(file).Length;
        }
        if (gotFiles != files || gotBytes != bytes) throw new IOException($"the copy came out different ({gotFiles}/{files} files)");
    }

    static void WriteFlag() => File.WriteAllText(PortableFlag,
        "this file keeps the game's files in the data folder beside it (settings, saves, mods, the unpacked game).\r\n"
        + "move them back to your windows user folder in SETTINGS > APP > FILES rather than deleting this file.\r\n");

    // nothing of the game's yet: no folder, or only the empty folders a start that stopped at the ROM picker left
    static bool Fresh(string root) => !HasFiles(root);

    static bool HasFiles(string root)
    {
        try { return Directory.Exists(root) && Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Any(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return true; }
    }

    static string FreeName(string path)
    {
        string name = path;
        for (int i = 2; Directory.Exists(name) || File.Exists(name); i++) name = $"{path} {i}";
        return name;
    }

    static bool SameDrive(string a, string b) =>
        string.Equals(Path.GetPathRoot(Path.GetFullPath(a)), Path.GetPathRoot(Path.GetFullPath(b)), StringComparison.OrdinalIgnoreCase);

    static bool SamePath(string a, string b) =>
        string.Equals(Path.GetFullPath(a).TrimEnd('\\'), Path.GetFullPath(b).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

    public static bool Writable(string dir)
    {
        string probe = Path.Combine(dir, $".write-probe-{Environment.ProcessId}");
        try
        {
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
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
