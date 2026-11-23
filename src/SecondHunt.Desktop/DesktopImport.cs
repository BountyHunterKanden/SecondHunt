using MphRecomp.App.Platform;
using MphRecomp.Frontend;

namespace SecondHunt.Desktop;

// The Windows half of IMPORT GAMES (Core Frontend/ImportGames.cs, RECOMP SETTINGS > IMPORT GAMES): the file dialog for
// the player's ROMs and Wii key file, shown between frames on the app's main thread (an STA thread, as Windows'
// dialogs want). The picked file is read where it is (ImportHost.Open's default: a file path), never copied.
internal static class DesktopImport
{
    public static void Install(Action<Action> defer)
    {
        ImportHost.Pick = (request, picked) => defer(() => picked(Pick(request)));
    }

    static ImportPicked? Pick(ImportPickRequest request)
    {
        if (request.Kind == ImportPickKind.Folder) return PickFolder(request);
        string patterns = String.Join(";", request.Extensions.Select(e => "*" + e));
        string what = request.Kind == ImportPickKind.WiiKey ? "Wii key file"
            : request.Kind == ImportPickKind.SwitchKeys ? "Switch prod.keys file"
            : request.Kind == ImportPickKind.DsSave ? "Metroid Prime Hunters DS save"
            : request.Kind == ImportPickKind.Profile ? "Second Hunt profile"
            : System.Globalization.CultureInfo.InvariantCulture.TextInfo.ToTitleCase(request.Title.ToLowerInvariant()) + " ROM";
#if MPH_LINUX
        string? file = LinuxDialogs.OpenFile($"Select your {what}", what, request.Extensions.ToList());
        if (file == null) return null;
        Log.Info("MPHFrontend", "import: picked " + Path.GetFileName(file));
        return new ImportPicked(file, Path.GetFileName(file));
#else
        using var dialog = new System.Windows.Forms.OpenFileDialog
        {
            Title = $"Select your {what}",
            Filter = $"{what} ({patterns})|{patterns}|All files (*.*)|*.*",
            CheckFileExists = true,
            RestoreDirectory = true,
        };
        if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK) return null;
        Log.Info("MPHFrontend", "import: picked " + Path.GetFileName(dialog.FileName));
        return new ImportPicked(dialog.FileName, Path.GetFileName(dialog.FileName));
#endif
    }

    // a game imported from its files (Prime 4: the RomFS folder of the player's own dump), read where it is
    static ImportPicked? PickFolder(ImportPickRequest request)
    {
        string title = System.Globalization.CultureInfo.InvariantCulture.TextInfo.ToTitleCase(request.Title.ToLowerInvariant());
#if MPH_LINUX
        string? picked = LinuxDialogs.OpenFolder($"Select your {title} RomFS folder (the folder holding Preload, or the one above it)");
        if (picked == null) return null;
        string path = picked.TrimEnd(Path.DirectorySeparatorChar);
#else
        using var dialog = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = $"Select your {title} RomFS folder (the folder holding Preload, or the one above it)",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = false,
        };
        if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK) return null;
        string path = dialog.SelectedPath.TrimEnd(Path.DirectorySeparatorChar);
#endif
        Log.Info("MPHFrontend", "import: picked folder " + Path.GetFileName(path));
        return new ImportPicked(path, Path.GetFileName(path));
    }
}
