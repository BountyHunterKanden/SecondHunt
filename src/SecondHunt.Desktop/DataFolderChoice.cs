using System.Windows.Forms;

namespace SecondHunt.Desktop;

// (GitHub #12, owner 2026-11-04) a fresh install's first question, before the window opens: where its files go. Closing
// it is APPDATA. SETTINGS > APP > FILES moves them later (DesktopPaths).
internal static class DataFolderChoice
{
    public static bool AskPortable()
    {
        try
        {
            var pc = new TaskDialogCommandLinkButton("AppData",
                "In your Windows user folder (AppData\\Local). Best for most players.");
            var portable = new TaskDialogCommandLinkButton("Portable",
                "A data folder beside SecondHunt.exe, so the game and everything it keeps can go on a USB stick or another PC together.");
            var page = new TaskDialogPage
            {
                Caption = BuildFlags.AppName,
                Heading = $"Where should {BuildFlags.AppName} keep its files?",
                Text = "Your settings, saves, replays, mods and online identity go there. You can move them later in SETTINGS > APP > FILES.",
                Buttons = { pc, portable },
                DefaultButton = pc,
                AllowCancel = true,
            };
            return TaskDialog.ShowDialog(page) == portable;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            DesktopPaths.Notes.Add("data folder question not shown: " + ex.Message);
            return false;
        }
    }
}
