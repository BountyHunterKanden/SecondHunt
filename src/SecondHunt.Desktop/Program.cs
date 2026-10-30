using MphRecomp.App.Platform;
using MphRecomp.Config;
using MphRecomp.Update;

namespace SecondHunt.Desktop;

internal static class Program
{
    [STAThread] // the ROM file picker
    static int Main(string[] args)
    {
        // every text format is invariant, like the PC tools and the Android app (10-16: Finnish -1 = "−1" crash)
        System.Globalization.CultureInfo.DefaultThreadCurrentCulture = System.Globalization.CultureInfo.InvariantCulture;
        System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.InvariantCulture;
        // after an in-game update: wait for the old process, clear its *.old files (DesktopUpdater)
        args = DesktopUpdater.AfterUpdate(args);
        // dev builds: --padtest [seconds] prints what SDL3 sees of the first pad (thread D, D6a)
        if (!BuildFlags.Public && args.Contains("--padtest")) return Sdl3Pads.Test(args);
        AppOptions options = AppOptions.Parse(args);
#if MPH_PUBLIC
        options.Launch = null; // a public build starts at the menus only (the launch shortcuts are for dev builds)
#endif
        DesktopPaths.Init(options.Data);
        // --replayprep <replay> --sidecar <file>: this app as a replay's keyframe prepare, started by the game itself at a
        // low priority (ReplayPrepHost; thread B fast seeking): no window, its own log, gone when done
        int prep = Array.IndexOf(args, "--replayprep"), side = Array.IndexOf(args, "--sidecar");
        if (prep >= 0 && side >= 0 && prep + 1 < args.Length && side + 1 < args.Length)
        {
            DesktopLog.Init(DesktopPaths.Logs, "replayprep.txt");
            RecompSettings.DesktopHost = true;
            return MphRecomp.App.ReplayPrepHost.Run(DesktopPaths.Game, DesktopPaths.Root, args[prep + 1], args[side + 1]) == null ? 0 : 1;
        }
        DesktopLog.Init(DesktopPaths.Logs);
        DesktopCrashLog.Install();
        RecompSettings.PublicBuild = BuildFlags.Public;
        RecompSettings.DesktopHost = true;
        MphRecomp.Frontend.RecompMenus.PadConnected = () => DesktopInput.PadSeen;
        MphRecomp.Frontend.RecompMenus.PadGyro = () => DesktopInput.PadGyroSeen;
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
                // a Discord invite opens in the Discord app when it's installed (its own invite screen, no browser step)
                string? invite = MphRecomp.Frontend.RecompMenus.DiscordInviteCode(url);
                if (invite != null && DiscordAppInstalled()) url = "discord://-/invite/" + invite;
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
                DesktopImport.Install(app.Defer); // IMPORT GAMES' file dialog (between frames, on this STA thread)
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

    // the Discord app registers discord:// as `"...\Discord.exe" --url -- "%1"` and opens the link's path in its window
    // (discord://-/invite/CODE = its invite screen); an uninstalled app can leave the key behind, so the exe must exist
    private static bool DiscordAppInstalled()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.ClassesRoot.OpenSubKey(@"discord\shell\open\command");
            string cmd = (key?.GetValue(null) as string ?? "").Trim();
            string exe = cmd.StartsWith('"') ? cmd.Substring(1, Math.Max(0, cmd.IndexOf('"', 1) - 1)) : cmd.Split(' ')[0];
            return exe.Length > 0 && File.Exists(exe);
        }
        catch (Exception) { return false; }
    }
}
