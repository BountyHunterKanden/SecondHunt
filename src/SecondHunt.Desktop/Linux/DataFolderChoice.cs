namespace SecondHunt.Desktop;

// (GitHub #12) the Linux build's first-start question (Windows: ../DataFolderChoice.cs, a task dialog): where its files
// go. Closing it, or no dialog tool, is the user folder (~/.local/share). SETTINGS > APP > FILES moves them later.
internal static class DataFolderChoice
{
    public static bool AskPortable()
    {
        string? pick = LinuxDialogs.Choose(BuildFlags.AppName,
            $"Where should {BuildFlags.AppName} keep its files?\n\nYour settings, saves, replays, mods and online identity go there. "
            + "You can move them later in SETTINGS > APP > FILES.",
            new[]
            {
                ("home", "In your home folder (~/.local/share). Best for most players."),
                ("portable", "A data folder beside the game, so the game and everything it keeps can go on a USB stick or another PC together."),
            });
        return pick == "portable";
    }
}
