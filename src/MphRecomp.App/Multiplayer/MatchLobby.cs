using MphRead;
using MphRecomp.Multiplayer;
using GameMode = MphRead.GameMode;

namespace MphRecomp.App;

// The multiplayer screen's choices (Beta 1), shared by the hosts' lobby screens (Android: MatchLobbyActivity; Windows:
// LobbyScreen): a match against bots on this device (every mode the dev launcher offers), or a LAN match between two
// devices on one network (MatchLan / the Windows lobby: host + one joiner, Battle only). Only the game's own arenas are
// listed. The hosts keep the last choices (Load / Save over their own key-value store) and start the match screen with
// the launch args BotMatchArgs / NetMatchArgs give (CampaignRenderer.Create).
internal sealed class MatchLobby
{
    public static readonly string[] Hunters = { "Samus", "Kanden", "Trace", "Sylux", "Noxus", "Spire", "Weavel" };
    public static readonly (string Label, float? Seconds)[] Times =
    {
        ("Mode default", null), ("10 min", 600), ("5 min", 300), ("3 min", 180), ("1 min", 60),
    };
    // MPH's own arm cannon (the campaign screen's "none" / "original" = no Prime gun), whatever RECOMP SETTINGS has
    public const string Gun = "original";

    public static IReadOnlyList<GameMode> Modes => MatchSettings.Modes;

    public int Mode, Hunter, Bots = 3, Level = 1, Time;
    public string? Arena; // the arena's RoomMetadata name (kept by name: each mode lists different arenas)
    readonly string _filesDir;
    bool _pathsReady;

    // filesDir: the extracted ROM, for the arenas' entity census and the LAN handshake's ROM revision
    public MatchLobby(string filesDir)
    {
        _filesDir = filesDir;
    }

    public sealed record Row(string Label, Func<string> Value, Action<int> Step);

    // the rows, top to bottom; Step(+1 / -1) changes one
    public IReadOnlyList<Row> Rows() => new Row[]
    {
        new("Mode", () => ModeLabel(Modes[Mode]), d => Mode = Wrap(Mode + d, Modes.Count)),
        new("Arena", () => { RoomMetadata a = CurrentArena(BotArenas()); return a.InGameName ?? a.Name; }, StepArena),
        new("Play as", () => Hunters[Hunter], d => Hunter = Wrap(Hunter + d, Hunters.Length)),
        // a match on this device needs at least one bot; a LAN match has none (host + one joiner)
        new("Bots (vs bots only)", () => Bots.ToString(), d => Bots = Wrap(Bots - 1 + d, 3) + 1),
        new("Bot level", () => $"{Level + 1} of 3", d => Level = Wrap(Level + d, 3)),
        new("Time limit", () => Times[Time].Label, d => Time = Wrap(Time + d, Times.Length)),
    };

    public static string ModeLabel(GameMode mode) => mode switch
    {
        GameMode.PrimeHunter => "Prime Hunter",
        _ when mode.ToString().EndsWith("Teams") => mode.ToString()[..^"Teams".Length] + " (teams)",
        _ => mode.ToString()
    };

    // the census reads the extracted ROM's entity files (the same paths the match screen sets up); this also tells the
    // LAN handshake which ROM revision this is
    public bool EnsurePaths()
    {
        if (_pathsReady)
        {
            return true;
        }
        try
        {
            Directory.SetCurrentDirectory(_filesDir);
            Paths.UpdatePaths();
            Paths.ChooseMphPath();
            _pathsReady = true;
        }
        catch (Exception ex)
        {
            Log.Warn("MPHLobby", "ROM paths: " + ex);
        }
        return _pathsReady;
    }

    // the retail arenas only (MatchArenas' table range); imported ones (Echoes, Metadata.HostRooms) are left out
    static List<RoomMetadata> RetailArenas() => Metadata.RoomList
        .Where(r => r.Multiplayer && !r.FirstHunt && r.Id >= MatchArenas.FirstId && r.Id <= MatchArenas.LastId
            && !Metadata.HostRooms.Contains(r))
        .OrderBy(r => r.Id)
        .ToList();

    // the arenas whose entity data supports the mode for that many players
    public List<RoomMetadata> Arenas(GameMode mode, int players)
    {
        List<RoomMetadata> retail = RetailArenas();
        if (EnsurePaths())
        {
            try
            {
                var list = retail.Where(a => MatchArenas.Supports(a, mode, players)).ToList();
                if (list.Count > 0)
                {
                    return list;
                }
            }
            catch (Exception ex)
            {
                Log.Warn("MPHLobby", "arena census failed: " + ex.Message);
            }
        }
        return retail;
    }

    List<RoomMetadata> BotArenas() => Arenas(Modes[Mode], Bots + 1);

    // the chosen arena if the list has it, else the list's first (and that becomes the choice)
    public RoomMetadata CurrentArena(List<RoomMetadata> list)
    {
        RoomMetadata? arena = list.FirstOrDefault(a => a.Name == Arena);
        if (arena == null)
        {
            arena = list[0];
            Arena = arena.Name;
        }
        return arena;
    }

    static int Wrap(int value, int count) => ((value % count) + count) % count;

    void StepArena(int delta)
    {
        List<RoomMetadata> list = BotArenas();
        int index = list.IndexOf(CurrentArena(list));
        Arena = list[Wrap(index + delta, list.Count)].Name;
    }

    // the match screen's launch args for a match against bots on this device
    public Dictionary<string, string> BotMatchArgs()
    {
        GameMode mode = Modes[Mode];
        RoomMetadata arena = CurrentArena(BotArenas());
        var args = new Dictionary<string, string>
        {
            ["match"] = mode.ToString(),
            ["arena"] = arena.Name,
            ["bots"] = Bots.ToString(),
            ["botlevel"] = Level.ToString(),
            ["hunter"] = Hunters[Hunter],
            ["gun"] = Gun,
        };
        if (Times[Time].Seconds is float s)
        {
            args["time"] = s.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        return args;
    }

    // Beta 1's LAN match: Battle only, the host plus one joiner (no bots). Switches the mode to Battle (true = it
    // changed, so the host can say so) and gives the settings to host with, or null if the game's files can't be read.
    public MatchSettings? LanTemplate(out bool modeChanged)
    {
        int battle = Modes.ToList().IndexOf(GameMode.Battle);
        modeChanged = Mode != battle;
        Mode = battle;
        List<RoomMetadata> lanArenas = Arenas(GameMode.Battle, 2);
        RoomMetadata arena = CurrentArena(lanArenas);
        if (!_pathsReady)
        {
            return null;
        }
        MatchSettings template = MatchSettings.Quick(GameMode.Battle, arena.Name, Enum.Parse<Hunter>(Hunters[Hunter]), bots: 0, Level);
        template.TimeLimitSeconds = Times[Time].Seconds;
        return template;
    }

    // the match screen's launch args for a LAN match set up here (it plays LanSession.Current)
    public Dictionary<string, string> NetMatchArgs(bool host) => new()
    {
        ["net"] = host ? "host" : "client",
        ["hunter"] = Hunters[Hunter],
        ["gun"] = Gun,
    };

    // the revision check both sides of a LAN match compare (LanSession JOIN): US rev 0 and rev 1 share every file a
    // match uses (docs/ROM_VERSIONS.md 3e), so they count as one
    public static string LanRomKey(string romKey) => romKey is Ver.AMHE0 or Ver.AMHE1 ? "MPH USA" : romKey;

    // ---- remembered choices, over the host's own store ----

    public void Load(Func<string, string?> getString, Func<string, int, int> getInt)
    {
        int mode = Modes.ToList().FindIndex(m => m.ToString() == getString("mode"));
        Mode = mode >= 0 ? mode : 0;
        Arena = getString("arena");
        int hunter = Array.IndexOf(Hunters, getString("hunter"));
        Hunter = hunter >= 0 ? hunter : 0;
        Bots = Math.Clamp(getInt("bots", 3), 1, 3);
        Level = Math.Clamp(getInt("botlevel", 1), 0, 2);
        Time = Math.Clamp(getInt("time", 0), 0, Times.Length - 1);
    }

    public void Save(Action<string, string?> putString, Action<string, int> putInt)
    {
        putString("mode", Modes[Mode].ToString());
        putString("arena", Arena);
        putString("hunter", Hunters[Hunter]);
        putInt("bots", Bots);
        putInt("botlevel", Level);
        putInt("time", Time);
    }
}
