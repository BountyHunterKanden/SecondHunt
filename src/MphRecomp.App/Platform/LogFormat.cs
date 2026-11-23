using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Text;
using MphRecomp.Config;

namespace MphRecomp.App.Platform;

// One log format on every platform (owner 11-14: "whatever gives us the most helpful logs", Windows = Android). Every
// line: local ISO 8601 time with ms and the UTC offset, the thread id, the level, the area tag, the message
//   2026-11-14T10:52:03.417-06:00  1234 I MPHNet: ...
// and every log file starts with its run: a title line with a short run id, then MPHRun lines with the app, the
// device/OS, the ROM revision and the settings, so any chunk of a log can be placed (which run, which day, which build).
// One complete log, everything always written: no switches. Each line's area is followed by its tags (Edngravy's idea:
// a tag list per line rather than one switch per kind of debug line), e.g.
//   2026-11-17T21:40:54.120-06:00  1234 I MPHPlay[pickup,weapon,omega]: P1 Samus (you) picked up the Omega Cannon
//   2026-11-17T21:40:55.031-06:00  1234 D MPHPlay[damage,weapon,omega]: P2 Kanden took 150 from P1 Samus's Omega Cannon
//   2026-11-17T21:40:56.502-06:00  1234 I MPHAudio[sound]: ...
// The area's own tags (AreaTags) are added to every line, so any line can be picked out by what it's about when the log
// is read: "sound or weapon but not input" = lines whose [...] holds sound or weapon and not input (docs/LOGS.md).
// D lines are the busy detail (every shot, hit, form change); they are written too.
public static class LogFormat
{
    // this process's run: 8 hex digits, in the title line, the MPHRun lines and SAVE LOG's section titles
    public static readonly string RunId = Guid.NewGuid().ToString("N")[..8];
    public static readonly DateTimeOffset Started = DateTimeOffset.Now;

    public static string Stamp(DateTimeOffset t) => t.ToString("yyyy-MM-dd'T'HH:mm:ss.fffzzz", CultureInfo.InvariantCulture);

    public static string Stamp() => Stamp(DateTimeOffset.Now);

    public static char Letter(Log.Level level) =>
        level switch { Log.Level.Debug => 'D', Log.Level.Info => 'I', Log.Level.Warn => 'W', _ => 'E' };

    public static string Line(Log.Level level, int tid, string tag, string message) =>
        Line(Stamp(), tid.ToString(CultureInfo.InvariantCulture), Letter(level), tag, message);

    // tid "" for lines that aren't from one of our threads (markers, exit records)
    public static string Line(string stamp, string tid, char level, string tag, string message) =>
        $"{stamp} {tid,5} {level} {WithTags(tag)}: {message}";

    // "area[a,b]" (Log's tagged lines); no tags = the area alone
    public static string Tagged(string area, string[] tags) => tags.Length == 0 ? area : area + "[" + string.Join(',', tags) + "]";

    // the area part of a tag (logcat's tag on Android), and the [..] part's tags
    public static string Area(string tag)
    {
        int open = tag.IndexOf('[');
        return open < 0 ? tag : tag[..open];
    }

    // the tag as written: the line's own tags, then its area's (AreaTags) not already there
    public static string WithTags(string tag)
    {
        string area = Area(tag);
        if (!AreaTags.TryGetValue(area, out string[]? more) || more.Length == 0)
        {
            return tag;
        }
        if (area.Length == tag.Length)
        {
            return area + "[" + string.Join(',', more) + "]";
        }
        var all = new List<string>(tag[(area.Length + 1)..^1].Split(','));
        foreach (string t in more)
        {
            if (!all.Contains(t)) all.Add(t);
        }
        return area + "[" + string.Join(',', all) + "]";
    }

    // what each area's lines are about, so every line carries tags (lowercase words; a line's own come first). DOTNET (the
    // sim's Console lines) gets "net" when it's a network line (ConsoleTag).
    public static readonly IReadOnlyDictionary<string, string[]> AreaTags = new Dictionary<string, string[]>
    {
        ["MPHPlay"] = [],
        ["MPHAudio"] = ["sound"], ["MPHMusic"] = ["sound", "music"], ["MPHVoice"] = ["sound", "voice", "net"], ["VoiceHud"] = ["voice", "hud"],
        ["MPHInput"] = ["input"], ["MPHPad"] = ["input", "pad"], ["MPHTouch"] = ["input", "touch"], ["MPHRumble"] = ["input", "rumble"], ["MPHGyro"] = ["input", "gyro"],
        ["MPHNet"] = ["net"], ["MPHOnline"] = ["net", "online"], ["MPHLobby"] = ["net", "lobby"],
        ["MPHRender"] = ["render"], ["MPHGl"] = ["render", "gpu"], ["MPHGpu"] = ["render", "gpu"], ["MPHView"] = ["render", "camera"],
        ["MPHHud"] = ["hud", "render"], ["MPHVis"] = ["render", "player"], ["MPHSmooth"] = ["render", "net"], ["MPHIce"] = ["render", "ice"],
        ["MPHReplay"] = ["replay"], ["MPHReplayPrep"] = ["replay"], ["MPHReplayBench"] = ["replay", "perf"], ["MPHReplayDump"] = ["replay"], ["MPHKfTime"] = ["replay", "perf"],
        ["MPHCampaign"] = ["campaign"], ["MPHSpeedrun"] = ["campaign", "timer"], ["MPHFirstHunt"] = ["campaign", "firsthunt"],
        ["MPHMatch"] = ["match"], ["MPHArena"] = ["match", "arena"], ["MPHBot"] = ["match", "bot"], ["MPHShot"] = ["weapon"],
        ["MPHPerf"] = ["perf"], ["MPHFrameStats"] = ["perf"],
        ["MPHFrontend"] = ["menu"], ["MPHPause"] = ["menu"], ["MPHMenu"] = ["menu"],
        ["MPHSave"] = ["app", "save"], ["MPHUpdate"] = ["app", "update"], ["MPHCrash"] = ["app", "crash"], ["MPHLog"] = ["app"],
        ["MPHRun"] = ["app"], ["MPHDesktop"] = ["app"], ["MPHExtract"] = ["app", "import"], ["MPHMods"] = ["mods"],
        ["MPHPlace"] = ["editor"], ["MPHPhoto"] = ["capture"], ["MPHMovie"] = ["capture", "sound"],
    };

    // a Console line's tag: the sim's and the online code's lines all come through Console (DOTNET); the network ones
    // ("online: ...", "relay: ...") are tagged so they can be found with the rest of the network lines
    public static string ConsoleTag(string text) =>
        text.StartsWith("online:", StringComparison.Ordinal) || text.StartsWith("relay:", StringComparison.Ordinal)
        || text.StartsWith("TURN:", StringComparison.Ordinal) || text.StartsWith("hit claims:", StringComparison.Ordinal)
        || text.StartsWith("shared replays:", StringComparison.Ordinal) || text.StartsWith("  online hub", StringComparison.Ordinal)
        ? "DOTNET[net]" : text.StartsWith("match:", StringComparison.Ordinal) ? "DOTNET[match]" : "DOTNET";

    // a log file's first line; process: "main", "replayprep"
    public static string Title(string app, string process) => $"--- {app} run {RunId}, {process}, started {Stamp(Started)} ---";

    // the MPHRun lines after the title (app, system, ROM, settings); SAVE LOG's header has the same lines
    public static void LogRun(string app, string system, string? gameDir, string? settingsPath)
    {
        foreach (string line in RunLines(app, system, gameDir, settingsPath))
        {
            Log.Info("MPHRun", line);
        }
    }

    public static IEnumerable<string> RunLines(string app, string system, string? gameDir, string? settingsPath)
    {
        yield return $"run {RunId}, started {Stamp(Started)}";
        yield return "app: " + app;
        yield return "system: " + system;
        yield return "rom: " + RomRevision(gameDir);
        foreach (string line in Settings(settingsPath))
        {
            yield return line;
        }
    }

    // the imported ROM, from the game files' .extracted marker (RomSetup.Extract): "rom=AMHE1" = the US cart rev 1 (1.1)
    public static string RomRevision(string? gameDir)
    {
        if (gameDir == null)
        {
            return "(unknown)";
        }
        try
        {
            string marker = RomSetup.MarkerPath(gameDir);
            if (!File.Exists(marker))
            {
                return "not imported yet";
            }
            string? root = null, files = null, version = null;
            foreach (string raw in File.ReadAllLines(marker))
            {
                string line = raw.Trim();
                if (line.StartsWith("rom=")) root = line[4..];
                else if (line.StartsWith("files=")) files = line[6..];
                else if (line.StartsWith("version=")) version = line[8..];
            }
            string rev = root is { Length: 5 } && root.StartsWith("AMH") && char.IsDigit(root[4])
                ? $"rev {root[4]} (1.{root[4]})" : "revision unknown";
            return $"{root ?? "?"} = {rev}, {files ?? "?"} files, imported by {version ?? "?"}";
        }
        catch (Exception ex)
        {
            return "(unreadable: " + ex.Message + ")";
        }
    }

    // the settings that matter most when reading a log, always; then every other one the player changed from its
    // default (lists and tables as their size only: binds, layouts, mod settings)
    static readonly string[] _key =
    {
        nameof(RecompSettings.FrameRate), nameof(RecompSettings.FpsLimit), nameof(RecompSettings.SmoothFrames),
        nameof(RecompSettings.RenderScale), nameof(RecompSettings.HorizontalFov), nameof(RecompSettings.AimView),
        nameof(RecompSettings.PrimeHud), nameof(RecompSettings.Gyro), nameof(RecompSettings.PadLayout),
        nameof(RecompSettings.TouchControls), nameof(RecompSettings.RecordReplays), nameof(RecompSettings.PrepareReplays),
        nameof(RecompSettings.Speakers), nameof(RecompSettings.DeveloperMode),
    };

    public static IEnumerable<string> Settings(string? settingsPath)
    {
        RecompSettings now, defaults;
        try
        {
            now = RecompSettings.Load(settingsPath);
            defaults = new RecompSettings();
        }
        catch (Exception ex)
        {
            return new[] { "settings: (unreadable: " + ex.Message + ")" };
        }
        var key = new StringBuilder();
        var changed = new StringBuilder();
        foreach (string name in _key)
        {
            PropertyInfo? p = typeof(RecompSettings).GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
            if (p != null) Append(key, name, Show(p.GetValue(now)));
        }
        IEnumerable<PropertyInfo> props = typeof(RecompSettings).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead && p.CanWrite && p.GetIndexParameters().Length == 0 && Array.IndexOf(_key, p.Name) < 0)
            .OrderBy(p => p.Name, StringComparer.Ordinal);
        foreach (PropertyInfo p in props)
        {
            string value = Show(p.GetValue(now));
            if (value != Show(p.GetValue(defaults))) Append(changed, p.Name, value);
        }
        return new[]
        {
            "settings: " + key,
            "settings changed from default: " + (changed.Length == 0 ? "none" : changed.ToString()),
        };
    }

    static void Append(StringBuilder sb, string name, string value) =>
        sb.Append(sb.Length == 0 ? "" : ", ").Append(name).Append('=').Append(value);

    static string Show(object? value) => value switch
    {
        null => "null",
        string s => s.Length == 0 ? "\"\"" : s,
        bool b => b ? "on" : "off",
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        ICollection c => $"({c.Count} entries)",
        _ => value.GetType().Name,
    };
}
