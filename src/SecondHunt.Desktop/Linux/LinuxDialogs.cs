using System.Diagnostics;
using MphRecomp.App.Platform;

namespace SecondHunt.Desktop;

// The Linux build's few desktop dialogs (Windows: WinForms' file, folder and message boxes): the desktop's own, through
// zenity (GNOME, and most others carry it) or kdialog (KDE). With neither, a file pick is cancelled and a message goes to
// the log and stderr (a .nds can still be dropped on the window, and --rom given at the command line).
internal static class LinuxDialogs
{
    static string? _tool;
    static bool _looked;

    static string? Tool()
    {
        if (_looked) return _tool;
        _looked = true;
        foreach (string t in new[] { "zenity", "kdialog" })
        {
            if (OnPath(t) != null) return _tool = t;
        }
        Log.Warn("MPHDesktop", "no zenity or kdialog: install zenity for the file pickers and message boxes");
        return null;
    }

    static string? OnPath(string name)
    {
        foreach (string dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(':', StringSplitOptions.RemoveEmptyEntries))
        {
            string p = Path.Combine(dir, name);
            if (File.Exists(p)) return p;
        }
        return null;
    }

    // runs the dialog and waits; its printed answer, or null when it was cancelled or wouldn't run
    static string? Run(string tool, IEnumerable<string> args)
    {
        try
        {
            var psi = new ProcessStartInfo(tool) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (string a in args) psi.ArgumentList.Add(a);
            using Process p = Process.Start(psi)!;
            string output = p.StandardOutput.ReadToEnd();
            p.StandardError.ReadToEnd();
            p.WaitForExit();
            return p.ExitCode == 0 ? output.TrimEnd('\n', '\r') : null;
        }
        catch (Exception ex)
        {
            Log.Warn("MPHDesktop", $"{tool} didn't run: {ex.Message}");
            return null;
        }
    }

    // a file to open; extensions like ".nds" (either case matches)
    public static string? OpenFile(string title, string what, IReadOnlyList<string> extensions)
    {
        var globs = extensions.SelectMany(e => new[] { "*" + e.ToLowerInvariant(), "*" + e.ToUpperInvariant() }).Distinct().ToList();
        string? path = Tool() switch
        {
            "zenity" => Run("zenity", new[] { "--file-selection", "--title=" + title,
                $"--file-filter={what} | {string.Join(' ', globs)}", "--file-filter=All files | *" }),
            "kdialog" => Run("kdialog", new[] { "--title", title, "--getopenfilename", Home(), $"{string.Join(' ', globs)}|{what}" }),
            _ => null,
        };
        return string.IsNullOrEmpty(path) || !File.Exists(path) ? null : path;
    }

    public static string? OpenFolder(string title)
    {
        string? path = Tool() switch
        {
            "zenity" => Run("zenity", new[] { "--file-selection", "--directory", "--title=" + title }),
            "kdialog" => Run("kdialog", new[] { "--title", title, "--getexistingdirectory", Home() }),
            _ => null,
        };
        return string.IsNullOrEmpty(path) || !Directory.Exists(path) ? null : path;
    }

    public static void Message(string text, string title, bool error = false)
    {
        Console.Error.WriteLine($"{title}: {text}");
        switch (Tool())
        {
            case "zenity":
                Run("zenity", new[] { error ? "--error" : "--info", "--title=" + title, "--text=" + Escape(text) });
                break;
            case "kdialog":
                Run("kdialog", new[] { "--title", title, error ? "--error" : "--msgbox", text });
                break;
        }
    }

    // one of the choices (tag, label), or null when the dialog was closed or there's no dialog tool
    public static string? Choose(string title, string text, IReadOnlyList<(string Tag, string Label)> choices)
    {
        var args = new List<string>();
        switch (Tool())
        {
            case "zenity":
                args.AddRange(new[] { "--list", "--title=" + title, "--text=" + Escape(text), "--column=", "--column=",
                    "--hide-column=1", "--print-column=1", "--hide-header", "--width=640", "--height=260" });
                foreach ((string tag, string label) in choices) { args.Add(tag); args.Add(label); }
                return Run("zenity", args);
            case "kdialog":
                args.AddRange(new[] { "--title", title, "--menu", text });
                foreach ((string tag, string label) in choices) { args.Add(tag); args.Add(label); }
                return Run("kdialog", args);
        }
        return null;
    }

    // zenity reads Pango markup in --text
    static string Escape(string s) => s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    static string Home() => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) is { Length: > 0 } h ? h + "/" : "/";

    // a folder in the desktop's file manager
    public static void ShowFolder(string folder)
    {
        try
        {
            Process.Start(new ProcessStartInfo("xdg-open") { UseShellExecute = false, ArgumentList = { folder } })?.Dispose();
        }
        catch (Exception ex)
        {
            Log.Warn("MPHDesktop", $"couldn't open {folder}: {ex.Message}");
        }
    }
}
