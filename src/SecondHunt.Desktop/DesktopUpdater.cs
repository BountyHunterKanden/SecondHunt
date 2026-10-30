using System.Diagnostics;
using System.IO.Compression;
using MphRecomp.App.Platform;
using MphRecomp.Update;

namespace SecondHunt.Desktop;

// The Windows half of the in-game update (Core Update/UpdateInstall.cs; owner 2026-10-06): Core has downloaded the release
// zip and checked it against the signed update file. Its "Second Hunt" folder is unpacked into the data folder, then each
// game file is renamed to *.old (Windows lets a running exe and its loaded DLLs be renamed, not overwritten), the new one
// moves into its place, and the new SecondHunt.exe starts with this one's arguments plus --after-update <pid>: it waits
// for this process to end and deletes the *.old files. A failure before the restart puts every old file back. The data
// folder (saves, settings, the unpacked game) isn't touched; the firewall rule (by program path) keeps working.
internal sealed class DesktopUpdater : IUpdateInstaller
{
    const string ZipFolder = "Second Hunt/";
    const string Exe = "SecondHunt.exe";
    const string AfterUpdateArg = "--after-update";

    readonly string _installDir = AppContext.BaseDirectory;
    readonly string[] _args;

    // ends this process once the new one has started
    public Action Exit { get; set; } = () => Environment.Exit(0);

    public DesktopUpdater(string[] args)
    {
        _args = args;
        DownloadDir = Path.Combine(DesktopPaths.Root, "update");
    }

    public string DownloadDir { get; }

    public string? Blocker(UpdateResult offer, out Action? fix)
    {
        fix = null;
        // a dev build runs from bin\ (SecondHunt.dll beside the exe); a release is the single SecondHunt.exe
        if (File.Exists(Path.Combine(_installDir, "SecondHunt.dll")) || !File.Exists(Path.Combine(_installDir, Exe)))
        {
            return "this copy isn't a release build, so it can't update itself.";
        }
        if (!Writable(_installDir))
        {
            return "windows won't let the game change its own folder. move the second hunt folder somewhere you own, like documents, and try again.";
        }
        return null;
    }

    public void Install(string zipPath, UpdateResult offer, Action<string> done)
    {
        Task.Run(() =>
        {
            string staging = Path.Combine(DownloadDir, "staging");
            var swapped = new List<(string Target, string? Old)>();
            try
            {
                if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
                Directory.CreateDirectory(staging);
                string root = Path.GetFullPath(staging) + Path.DirectorySeparatorChar;
                using (ZipArchive zip = ZipFile.OpenRead(zipPath))
                {
                    if (zip.GetEntry(ZipFolder + Exe) == null)
                    {
                        done($"the download has no {Exe}, so it wasn't installed.");
                        return;
                    }
                    foreach (ZipArchiveEntry entry in zip.Entries)
                    {
                        if (!entry.FullName.StartsWith(ZipFolder, StringComparison.Ordinal) || entry.FullName.EndsWith('/')) continue;
                        string dest = Path.GetFullPath(Path.Combine(staging, entry.FullName[ZipFolder.Length..]));
                        if (!dest.StartsWith(root, StringComparison.OrdinalIgnoreCase)) continue; // nothing outside the folder
                        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                        entry.ExtractToFile(dest, overwrite: true);
                    }
                }
                foreach (string file in Directory.GetFiles(staging, "*", SearchOption.AllDirectories))
                {
                    string target = Path.Combine(_installDir, Path.GetRelativePath(staging, file));
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    string? old = null;
                    if (File.Exists(target))
                    {
                        old = FreeOldName(target);
                        File.Move(target, old);
                    }
                    swapped.Add((target, old));
                    File.Move(file, target);
                }
                Directory.Delete(staging, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                Log.Warn("MPHUpdate", "install failed, rolling back: " + ex);
                RollBack(swapped);
                done("the update couldn't be installed, so the game is unchanged. try again later.");
                return;
            }

            try
            {
                var start = new ProcessStartInfo(Path.Combine(_installDir, Exe))
                {
                    UseShellExecute = false,
                    WorkingDirectory = Environment.CurrentDirectory
                };
                foreach (string a in _args) start.ArgumentList.Add(a);
                start.ArgumentList.Add(AfterUpdateArg);
                start.ArgumentList.Add(Environment.ProcessId.ToString());
                Process.Start(start);
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException)
            {
                Log.Warn("MPHUpdate", "restart failed: " + ex.Message);
                done($"updated to {offer.Latest}. close the game and open it again to play it.");
                return;
            }
            Log.Info("MPHUpdate", $"updated to {offer.Latest}; restarting");
            Exit();
        });
    }

    // the new exe's first step: wait for the old process, then clear what it left. Returns the arguments without ours.
    public static string[] AfterUpdate(string[] args)
    {
        int i = Array.IndexOf(args, AfterUpdateArg);
        if (i >= 0 && i + 1 < args.Length && int.TryParse(args[i + 1], out int pid))
        {
            try
            {
                using Process old = Process.GetProcessById(pid);
                old.WaitForExit(15000);
            }
            catch (ArgumentException) { } // already gone
            catch (InvalidOperationException) { }
            args = args.Where((_, k) => k != i && k != i + 1).ToArray();
        }
        DeleteOldFiles(AppContext.BaseDirectory);
        return args;
    }

    // the *.old / *.old1... files an update left (only those names; a file still locked waits for the next start)
    static void DeleteOldFiles(string dir)
    {
        try
        {
            foreach (string file in Directory.EnumerateFiles(dir, "*.old*", SearchOption.AllDirectories))
            {
                if (!IsOldName(file)) continue;
                try
                {
                    File.Delete(file);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    static bool IsOldName(string path)
    {
        int dot = path.LastIndexOf(".old", StringComparison.Ordinal);
        return dot > 0 && path[(dot + 4)..].All(char.IsAsciiDigit);
    }

    static string FreeOldName(string target)
    {
        for (int n = 0; ; n++)
        {
            string old = target + ".old" + (n == 0 ? "" : n.ToString());
            if (!File.Exists(old)) return old;
            try
            {
                File.Delete(old);
                return old;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } // still in use: next name
        }
    }

    static void RollBack(List<(string Target, string? Old)> swapped)
    {
        for (int i = swapped.Count - 1; i >= 0; i--)
        {
            (string target, string? old) = swapped[i];
            try
            {
                if (File.Exists(target)) File.Delete(target);
                if (old != null) File.Move(old, target);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Warn("MPHUpdate", $"roll back {target}: {ex.Message}");
            }
        }
    }

    static bool Writable(string dir)
    {
        string probe = Path.Combine(dir, $".update-probe-{Environment.ProcessId}");
        try
        {
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
