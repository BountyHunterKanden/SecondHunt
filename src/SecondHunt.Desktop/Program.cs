using MphRecomp.App.Platform;
using MphRecomp.Config;
using MphRecomp.Update;

namespace SecondHunt.Desktop;

internal static class Program
{
    [STAThread] // the ROM file picker
    static int Main(string[] args)
    {
        // after an in-game update: wait for the old process, clear its *.old files (DesktopUpdater)
        args = DesktopUpdater.AfterUpdate(args);
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
        // RECOMP SETTINGS > UPDATES (Core Update/): this build's version and its installer; a dev build reads a test
        // server's base URL from update_feed.txt in its data folder
        UpdateHost.AppVersion = BuildFlags.Version;
        UpdateHost.Platform = UpdatePlatform.Windows;
        UpdateHost.DevFeedFile = BuildFlags.Public ? null : Path.Combine(DesktopPaths.Root, "update_feed.txt");
        UpdateHost.Installer = new DesktopUpdater(args); // the game installs updates itself (owner 10-06)
        UpdateHost.OpenUrl = url =>
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
            }
            catch (Exception ex) { Log.Warn("MPHUpdate", $"couldn't open {url}: {ex.Message}"); }
        };
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
