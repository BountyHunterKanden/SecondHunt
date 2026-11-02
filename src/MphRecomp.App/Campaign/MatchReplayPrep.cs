using System;
using System.Diagnostics;
using System.IO;
using MphRead;
using MphRecomp.Replay;

namespace MphRecomp.App;

// The other process of fast seeking (thread B, step 2): it plays a replay headless and writes its keyframes
// (ReplayPrep.Run) while the viewer reads them as they come. Each platform starts it its own way at a low priority --
// Android a :replayprep service, the desktop this app again with --replayprep -- and both run Run here.
public static class ReplayPrepHost
{
    // set by the platform: starts a prepare of (replay file, its sidecar) in another process
    public static Action<string, string>? Start;

    // the prepare itself, in its own process: the game files as a match start sets them up (CampaignRenderer
    // OnSurfaceChanged), then the replay played through (null = done; else why it stopped)
    public static string? Run(string filesDir, string dataDir, string replayPath, string sidecar, Func<bool>? cancel = null)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            Scene.Headless = true;
            Directory.SetCurrentDirectory(filesDir);
            Paths.UpdatePaths();
            Paths.ChooseMphPath();
            MphRecomp.Arenas.EchoesArena.ImportRoot = dataDir;
            MphRecomp.Replay.ReplayData.ReplaysRoot = dataDir; // (kept arena files: ReplayData.Use)
            ReplayFile file = ReplayFile.Load(replayPath);
            ReplayFeed.Follower? feed = ReplayFeed.IsLive(replayPath) ? ReplayFeed.Follow(file, replayPath, cancel) : null; // (a match still being played)
            string? why = ReplayPrep.Run(file, sidecar, cancel, feed == null ? null : feed.More, log: l => Log.Info("MPHReplayPrep", l));
            feed?.Close(drain: why == null);
            // a replay seen not to play exactly is refused up front from now on (ReplayVerdict); a match still being played has no file to mark
            const string differs = "differs from the recording at step ";
            if (feed == null && why != null && why.StartsWith(differs, StringComparison.Ordinal)
                && int.TryParse(why.AsSpan(differs.Length), out int failedAt))
            {
                ReplayVerdict.MarkFailed(replayPath, file, failedAt);
            }
            Log.Info("MPHReplayPrep", (why == null ? "done" : "stopped: " + why) + $" in {sw.Elapsed.TotalSeconds:0.0} s, "
                + Path.GetFileName(replayPath));
            return why;
        }
        catch (Exception ex)
        {
            Log.Warn("MPHReplayPrep", $"failed: {ex.GetType().Name}: {ex.Message}");
            return ex.Message;
        }
    }
}
