using MphRecomp.App.Platform;
using MphRecomp.Config;

namespace SecondHunt.Desktop;

internal static class Program
{
    [STAThread] // the ROM file picker
    static int Main(string[] args)
    {
        AppOptions options = AppOptions.Parse(args);
#if MPH_PUBLIC
        options.Launch = null; // a public build starts at the menus only (the launch shortcuts are for dev builds)
#endif
        DesktopPaths.Init(options.Data);
        DesktopLog.Init(DesktopPaths.Logs);
        DesktopCrashLog.Install();
        RecompSettings.PublicBuild = BuildFlags.Public;
        RecompSettings.DesktopHost = true;
        RecompSettings.AppId = BuildFlags.AppName;
        // this app draws and plays everything itself: MphRead's own window, sound device and OpenAL stay closed
        MphRead.Scene.Headless = true;
        Log.Info("MPHDesktop", $"data folder: {DesktopPaths.Root}");
        try
        {
            DesktopPlatform.Install();
            using (var app = new DesktopApp(options))
            {
                app.Run();
            }
            DesktopPlatform.Shutdown();
            return 0;
        }
        catch (Exception ex)
        {
            string? report = DesktopCrashLog.Write(ex, "unhandled (main loop)");
            Log.Error("MPHDesktop", ex.ToString());
            System.Windows.Forms.MessageBox.Show(
                $"{BuildFlags.AppName} closed because of an error:\n\n{ex.Message}\n\n"
                + (report != null ? $"A crash report was saved to\n{report}" : "No crash report could be saved."),
                BuildFlags.AppName, System.Windows.Forms.MessageBoxButtons.OK, System.Windows.Forms.MessageBoxIcon.Error);
            return 1;
        }
    }
}
