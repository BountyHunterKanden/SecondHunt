using MphRecomp.App.Platform;
using MphRecomp.Frontend;

namespace SecondHunt.Desktop;

// The Windows half of IMPORT GAMES (Core Frontend/ImportGames.cs, RECOMP SETTINGS > IMPORT GAMES): the file dialog for
// the player's disc images and Wii key file, shown between frames on the app's main thread (an STA thread, as Windows'
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
            : System.Globalization.CultureInfo.InvariantCulture.TextInfo.ToTitleCase(request.Title.ToLowerInvariant()) + " disc image";
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
    }

    // a game imported from its files (Prime 4: the RomFS folder of the player's own dump), read where it is
    static ImportPicked? PickFolder(ImportPickRequest request)
    {
        string title = System.Globalization.CultureInfo.InvariantCulture.TextInfo.ToTitleCase(request.Title.ToLowerInvariant());
        using var dialog = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = $"Select your {title} RomFS folder (the folder holding Preload, or the one above it)",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = false,
        };
        if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK) return null;
        string path = dialog.SelectedPath.TrimEnd(Path.DirectorySeparatorChar);
        Log.Info("MPHFrontend", "import: picked folder " + Path.GetFileName(path));
        return new ImportPicked(path, Path.GetFileName(path));
    }
}
