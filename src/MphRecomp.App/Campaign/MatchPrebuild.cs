using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using MphRecomp.Frontend;

namespace MphRecomp.App;

// A local match built during START's lead-in (owner 2026-11-21, "make the wait from tapping START ... shorter"):
// vanilla's way out of the lobby (the panels leaving, the loading page, the fade to black: ~2.3 s) used to come first and
// the match screen's load (0.7 s warm on the Odin, 2.5 s for the first match after a launch) after it, on a black screen.
// Now the tap hands the match's setup line over (MultiplayerMenu.LeadInSetup) and a worker runs the match screen's own
// start (CampaignLaunch.Create, then CampaignRenderer.PrebuildStart: the room, players, gear and skins; headless, no GL)
// while the lead-in plays; the match screen takes it (Take) and only makes its GPU copies and audio. The lead-in itself
// is vanilla's, unchanged. MphRead is one scene at a time: the menus stay off it meanwhile (FrontendRenderer: the arena
// background holds its last picture, the lead-in's pages are read before the worker starts, the picture renders wait).
// LAN and online matches start the old way (their seats and sessions are only settled at the fade's end); the lobby's
// own LAN host session with nobody joined counts as local (StartMatch ends it), and a joiner arriving during the lead-in
// makes StartMatch send another line (net host), so the build is dropped.
internal static class MatchPrebuild
{
    // the shell's CampaignRenderer.Create folders (Android: FilesDir + the external dir; desktop: DesktopPaths); unset =
    // no building ahead (a match starts the old way)
    public static (string FilesDir, string DataDir)? Dirs;

    sealed record Build(string Line, Task<CampaignRenderer> Task, long Started);
    static Build? s_build;

    // a build begun and not yet taken or dropped: the menus keep off MphRead
    public static bool Holding => Volatile.Read(ref s_build) != null;
    public static string? Line => Volatile.Read(ref s_build)?.Line;

    // the menus' GL thread, at START's tap: the match for this setup line starts building on a worker
    public static void Begin(string line, int width, int height)
    {
        Drop("another start");
        if (Dirs is not (string files, string data) || width <= 0 || height <= 0) return;
        var args = new LaunchArgs(MatchSetupText.LaunchArgs(line));
        long started = Stopwatch.GetTimestamp();
        var task = Task.Factory.StartNew(() =>
        {
            CampaignRenderer renderer;
            CampaignRenderer.t_leadInBuild = true; // (the lobby's LAN host session, nobody joined: StartMatch ends it)
            try { renderer = CampaignRenderer.Create(files, data, args); }
            finally { CampaignRenderer.t_leadInBuild = false; }
            try
            {
                renderer.PrebuildStart(width, height);
                return renderer;
            }
            catch
            {
                renderer.Shutdown();
                renderer.DisposeAudio();
                throw;
            }
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        Volatile.Write(ref s_build, new Build(line, task, started));
        Log.Info("MPHMatch", $"lead-in: the match builds meanwhile ({width}x{height})");
    }

    // the menus' GL thread, the fade over and StartMatch next (MultiplayerMenu.LeadInFinish): the build is finished first,
    // so the menus' own start (its statics, the request) never runs beside it
    public static void Wait()
    {
        Build? b = Volatile.Read(ref s_build);
        if (b == null) return;
        var sw = Stopwatch.StartNew();
        try { b.Task.Wait(); }
        catch (AggregateException) { } // (Take reports it)
        Log.Info("MPHMatch", $"lead-in build {(b.Task.IsCompletedSuccessfully ? "ready" : "failed")}"
            + $" {Stopwatch.GetElapsedTime(b.Started).TotalMilliseconds:0} ms after the tap (the fade's end waited {sw.ElapsedMilliseconds} ms)");
    }

    // the match screen's start (Android CampaignActivity.OnCreate, desktop StartGame), for its setup line: the match built
    // for it, or null (none, another one: dropped; it failed) and the screen is made the old way
    public static CampaignRenderer? Take(string? line)
    {
        Build? b = Volatile.Read(ref s_build);
        if (b == null) return null;
        if (line == null || line != b.Line)
        {
            Drop("the match screen asked for another start");
            return null;
        }
        if (Interlocked.CompareExchange(ref s_build, null, b) != b) return null;
        try
        {
            b.Task.Wait();
            Log.Info("MPHMatch", $"lead-in build taken {Stopwatch.GetElapsedTime(b.Started).TotalMilliseconds:0} ms after the tap");
            return b.Task.Result;
        }
        catch (AggregateException ex)
        {
            Log.Warn("MPHMatch", "lead-in build failed, starting the old way: " + (ex.InnerException ?? ex));
            return null;
        }
    }

    // a build nobody will take (the start failed, another request): it finishes, then goes
    public static void Drop(string why)
    {
        Build? b = Interlocked.Exchange(ref s_build, null);
        if (b == null) return;
        try { b.Task.Wait(); }
        catch (AggregateException) { }
        if (b.Task.IsCompletedSuccessfully)
        {
            try
            {
                b.Task.Result.Shutdown();
                b.Task.Result.DisposeAudio();
            }
            catch (Exception ex) { Log.Warn("MPHMatch", "lead-in build dispose: " + ex.Message); }
        }
        Log.Info("MPHMatch", "lead-in build dropped: " + why);
    }
}
