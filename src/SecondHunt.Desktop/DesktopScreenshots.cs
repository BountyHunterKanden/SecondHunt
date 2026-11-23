namespace SecondHunt.Desktop;

// Photo mode's / F12's screenshots on a PC (CampaignRenderer.PhotoMode.cs): PNGs in <data>/Screenshots, next to the logs
// and saves (DesktopPaths.Root)
internal static class DesktopScreenshots
{
    public static string Folder => Path.Combine(DesktopPaths.Root, "Screenshots");

    public static string? Save(byte[] rgba, int w, int h, string name)
    {
        Directory.CreateDirectory(Folder);
        string path = Path.Combine(Folder, name);
        return DesktopPlatform.EncodePng(path, rgba, w, h) ? path : null;
    }
}
