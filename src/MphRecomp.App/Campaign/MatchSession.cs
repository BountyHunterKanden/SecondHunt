using System;
using MphRead;
using MphRecomp.Campaign;
using MphRecomp.Multiplayer;
using MphRecomp.Multiplayer.Net;


namespace MphRecomp.App;

// A multiplayer match on the campaign renderer (same drawing, HUD, controls, gun, audio and pause menu): the host
// runs MphRead's own match logic and bots (CampaignHost.StartMatch). Launch extras:
//   --es match <Battle|BattleTeams|Survival|...>  --es arena <room or in-game name, default Combat Hall>
//   --es bots <1-3, default 3>  --es botlevel <0-2, default 1>  --es time <seconds>  --es points <goal>
// or --es net host|client for a LAN match set up in the launcher (MatchLan.cs; the match comes from LanSession.Current),
// plus the campaign's own --es hunter / gun / layout / gyro, and --es touch auto|on|off (MatchTouchOverlay: auto = only
// when no game controller is connected). When the match ends the scoreboard stays up until A, Start or a tap (then
// back to the menu).
internal sealed partial class CampaignRenderer
{
    MatchSettings? _matchSettings;
    string? _matchResults;
    double _matchEndedAt;
    volatile bool _matchContinue;
    int _matchShownSecond;
    const double MatchResultsSeconds = 15;
    LanSession? _netSession;
    MatchNetDriver? _netDriver;

    // the results are up (the touch overlay turns a tap into "continue")
    public bool MatchOver => _host?.Match != null && _matchResults != null;
    public void RequestMatchContinue() => _matchContinue = true;

    // the LAN session this match plays over (the host closes it when the screen goes, so the other device hears BYE)
    public LanSession? NetSession => _netSession;

    void ConfigureMatch(LaunchArgs args)
    {
        CampaignRenderer renderer = this;
        string? mode = args.Get("match");
        string? net = args.Get("net");
        if (string.IsNullOrEmpty(mode) && string.IsNullOrEmpty(net))
        {
            return;
        }
        // the touch controls (match layout; --es touch over RECOMP SETTINGS) come from CampaignActivity.OnCreate:
        // MatchTouchOverlay.Attach, shared with the campaign
        if (!string.IsNullOrEmpty(net))
        {
            LanSession? session = LanSession.Current;
            if (session?.Settings == null)
            {
                renderer._error = "LAN match: the connection from the launcher is gone -- host or join again";
                return;
            }
            renderer._netSession = session;
            renderer._matchSettings = session.Settings;
            return;
        }
        try
        {
            MphRead.GameMode gameMode = Enum.Parse<MphRead.GameMode>(mode!.Replace(" ", ""), ignoreCase: true);
            string arenaName = args.Get("arena") ?? "Combat Hall";
            // the arena table is plain metadata (no ROM access), so it can be resolved before the ROM paths are set
            RoomMetadata arena = MatchArenas.Find(arenaName) ?? throw new ArgumentException($"no arena {arenaName}");
            int bots = int.TryParse(args.Get("bots"), out int b) ? b : 3;
            int level = int.TryParse(args.Get("botlevel"), out int l) ? l : 1;
            MatchSettings settings = MatchSettings.Quick(gameMode, arena.Name, renderer._hunter, bots, level);
            if (float.TryParse(args.Get("time"), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out float time)) settings.TimeLimitSeconds = time;
            if (int.TryParse(args.Get("points"), out int points)) settings.PointGoal = points;
            settings.Validate();
            renderer._matchSettings = settings;
        }
        catch (Exception ex)
        {
            renderer._error = "match setup: " + ex.Message;
            Log.Error("MPHMatch", ex.ToString());
        }
    }

    // OnSurfaceChanged: start the match instead of a campaign room
    bool TryStartMatch(int width, int height)
    {
        if (_matchSettings == null)
        {
            return false;
        }
        _host = CampaignHost.StartMatch(_matchSettings, collectDrawItems: true, viewWidth: width, viewHeight: height);
        _netDriver = _netSession != null ? new MatchNetDriver(_netSession, _host) : null;
        Log.Info("MPHMatch", $"match {_matchSettings.Mode} in {_matchSettings.Arena}, {_matchSettings.Players.Count} players");
        return true;
    }

    // a LAN match keeps running under the pause menu (as vanilla multiplayer does): the other players can't wait
    public bool NetKeepsRunning => _netDriver != null;

    // OnDrawFrame's simulation step: a LAN match does its networking around it (MatchNetDriver)
    void NetStep(CampaignInput input)
    {
        if (_netDriver != null)
        {
            if (PauseMenuOpen)
            {
                input = new CampaignInput { SelectWeapon = MphRead.BeamType.None }; // stands still under the menu
            }
            _netDriver.Step(input);
        }
        else
        {
            _host!.Step(input);
        }
    }

    // OnDrawFrame, while the host has ended: a match shows its results (the campaign's ship flow never sees it)
    bool TryEndMatch()
    {
        if (_host?.Match == null)
        {
            return false;
        }
        double now = _clock.Elapsed.TotalSeconds;
        if (_matchResults == null)
        {
            string why = _netDriver?.PeerGone == true && !_netSession!.IsHost ? "THE HOST LEFT\n" : "";
            _matchResults = why + "MATCH OVER\n\n" + MatchStatus.Scoreboard();
            _matchContinue = false;
            _matchEndedAt = now;
            _matchShownSecond = -1;
            _statusCountdown = 0;
            Log.Info("MPHMatch", "results: " + MatchStatus.Scoreboard().Replace('\n', '|'));
        }
        else if (_matchEndedAt != double.MaxValue)
        {
            // Start opens the pause menu before this sees it: here it means "continue" (the menu closes again)
            bool start = PauseMenu?.IsOpen == true;
            if (start)
            {
                PauseMenu!.Close();
            }
            double shown = now - _matchEndedAt;
            bool pressed = Pad[PadButton.A] || Pad[PadButton.B] || start || _matchContinue;
            if ((shown > 1.0 && pressed) || shown > MatchResultsSeconds)
            {
                _matchEndedAt = double.MaxValue; // once
                ShipSink?.Invoke("");
                _netSession?.Dispose();
                ExitSink?.Invoke();
                return true;
            }
            // the results in the big centred panel, with a countdown (refreshed once a second)
            int left = (int)Math.Ceiling(MatchResultsSeconds - shown);
            if (left != _matchShownSecond)
            {
                _matchShownSecond = left;
                ShipSink?.Invoke(_matchResults + $"\n\nA / B / Start / tap: back to the menu ({left})");
            }
        }
        return true;
    }

    // Status(): the scoreboard during a match, the results after it
    string? MatchStatusText()
    {
        if (_host?.Match == null)
        {
            return null;
        }
        if (_matchResults != null)
        {
            return ""; // the results are in the centred panel
        }
        if (_statusMode == 2)
        {
            return "";
        }
        RoomMetadata? meta = Metadata.GetRoomById(_host.RoomId);
        string net = _netDriver != null ? "\n" + _netDriver.StatusLine() : "";
        return $"{meta?.InGameName}  {_fps:0} fps  sim {_simMs:0.0} ms  draw {_drawMs:0.0} ms{net}\n" + MatchStatus.Scoreboard();
    }
}
