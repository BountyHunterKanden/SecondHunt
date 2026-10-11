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
        string patterns = String.Join(";", request.Extensions.Select(e => "*" + e));
        string what = request.Kind == ImportPickKind.WiiKey
            ? "Wii key file"
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
}
