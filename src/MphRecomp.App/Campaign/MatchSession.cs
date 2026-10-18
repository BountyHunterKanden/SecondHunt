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
// or --es net online for an online match (MatchOnline.cs; OnlineMatch.Current, through the server's relay),
// plus the campaign's own --es hunter / gun / layout / gyro, and --es touch auto|on|off (MatchTouchOverlay: auto = only
// when no game controller is connected). When the match ends the scoreboard stays up until A, Start or a tap (then
// back to the menu); a match set up in the vanilla multiplayer menu (--es setup, or --es net with --es menu 1 for its
// LAN games) goes straight back to its RESULTS page instead.
internal sealed partial class CampaignRenderer
{
    MatchSettings? _matchSettings;
    // set up in the vanilla multiplayer menu: its RESULTS page (47) shows the results, not this screen's panel
    bool _matchFromMenu;
    string? _matchResults;
    double _matchEndedAt;
    volatile bool _matchContinue;
    int _matchShownSecond;
    const double MatchResultsSeconds = 15;
    IMatchNet? _netSession;
    // what closes the match's connection (the LanSession, or the OnlineMatch's screen hold: its server and relay sockets)
    IDisposable? _netOwner;
    // an online match (the ONLINE PLAY menus' are handed back to them when they end: RESULTS, then PLAY AGAIN)
    OnlineMatch? _online;
    MatchNetDriver? _netDriver;

    // the results are up (the touch overlay turns a tap into "continue")
    public bool MatchOver => _host?.Match != null && _matchResults != null;
    public void RequestMatchContinue() => _matchContinue = true;

    // the LAN session / online match this match plays over (the host closes it when the screen goes, so the other
    // devices hear BYE)
    public IDisposable? NetSession => _netOwner;

    void ConfigureMatch(LaunchArgs args)
    {
        CampaignRenderer renderer = this;
        // TRY CONTROLS' and the solo run's statics off for every match (menu, LAN, online, replay, dev); only ConfigureTry /
        // ConfigureSolo turn them on again (owner 10-17: the try mode must not touch real multiplayer; SoloArena.cs)
        ResetSoloStatics();
        // dev --es turrettest 1: Shooting Gallery's bots go straight for the turret (EchoesTurretBots.ForceSeat); off otherwise
        MphRecomp.Arenas.EchoesTurretBots.ForceSeat = args.Get("turrettest") == "1" && !MphRecomp.Config.RecompSettings.PublicBuild;
        // dev --es dronetest 1: Trooper bots fire their drone whenever they have a target (PlayerEntity.HostDroneTest)
        MphRead.Entities.PlayerEntity.HostDroneTest = args.Get("dronetest") == "1" && !MphRecomp.Config.RecompSettings.PublicBuild;
        if (ConfigureReplay(args)) return; // --es replay: a recorded match played back (MatchReplay.cs)
        string? mode = args.Get("match");
        string? net = args.Get("net");
        // --es setup <MatchSetupText>: a match set up in the vanilla multiplayer menu (Core Frontend/MultiplayerMenu)
        string? setup = args.Get(MphRecomp.Frontend.MatchSetupText.ArgName);
        if (string.IsNullOrEmpty(mode) && string.IsNullOrEmpty(net) && string.IsNullOrEmpty(setup))
        {
            return;
        }
        if (!string.IsNullOrEmpty(setup))
        {
            try
            {
                MatchSettings menuSettings = MphRecomp.Frontend.MatchSetupText.Read(setup);
                if (args.Get("try") != null) ConfigureSolo(args, menuSettings); // TRY CONTROLS' practice run (SoloArena.cs)
                menuSettings.Validate();
                renderer._matchSettings = menuSettings;
                renderer._hunter = menuSettings.Players[menuSettings.LocalSeat].Hunter;
                renderer._matchFromMenu = true;
            }
            catch (Exception ex)
            {
                renderer._error = "match setup: " + ex.Message;
                Log.Error("MPHMatch", ex.ToString());
            }
            return;
        }
        // the touch controls (match layout; --es touch over RECOMP SETTINGS) come from CampaignActivity.OnCreate:
        // MatchTouchOverlay.Attach, shared with the campaign
        if (net == "online")
        {
            OnlineMatch? online = OnlineMatch.Current;
            if (online?.Net.Settings == null)
            {
                renderer._error = "online match: the connection from the launcher is gone -- host or join again";
                return;
            }
            renderer._netSession = online.Net;
            renderer.SetThirdPerson(false); // ReloadAimFromSettings ran before this (CampaignLaunch): drop a saved THIRD
            if (renderer.PauseMenu != null)
            {
                renderer.PauseMenu.NetMatch = true;
                renderer.PauseMenu.OnlineChat = online; // the pause's CHAT (PauseOverlay)
            }
            renderer._netOwner = online.TakeScreenHold();
            renderer._online = online;
            renderer._matchSettings = online.Net.Settings;
            renderer._matchFromMenu = args.Get("menu") == "1";
            return;
        }
        if (!string.IsNullOrEmpty(net))
        {
            LanSession? session = LanSession.Current;
            if (session?.Settings == null)
            {
                renderer._error = "LAN match: the connection from the launcher is gone -- host or join again";
                return;
            }
            renderer._netSession = session; // LAN: the match camera setting, third person too (owner 2026-10-17; CampaignLaunch)
            if (renderer.PauseMenu != null) renderer.PauseMenu.NetMatch = true;
            renderer._netOwner = session;
            renderer._matchSettings = session.Settings;
            renderer._matchFromMenu = args.Get("menu") == "1";
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
            ConfigureSolo(args, settings); // dev --es solo 1: alone in the arena, third person (SoloArena.cs)
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
        _host = StartMatchReplayed(_matchSettings, width, height); // CampaignHost.StartMatch, recorded or a replay (MatchReplay.cs)
        if (_host == null) return true; // a replay refused at its start (_error says why)
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
            RecordNetStep(input); // MatchReplay.cs: a network match's host records it (O10, Replays track)
        }
        else
        {
            StepReplayed(input); // _host.Step, recorded or played back and checked (MatchReplay.cs)
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
            EndReplay(complete: true); // MatchReplay.cs: saves the recording
            Log.Info("MPHMatch", "results: " + MatchStatus.Scoreboard().Replace('\n', '|'));
            if (_matchFromMenu)
            {
                // as vanilla: the game's fade to black, then the menus' RESULTS page (MultiplayerMenu); a LAN match's
                // session ends here (the other device hears BYE once its own match is over too)
                MphRecomp.Frontend.MatchResults.Last = MphRecomp.Frontend.MatchResults.Capture(_matchSettings!.LocalSeat);
                RecordFirstHuntMatch(); // a First Hunt deathmatch: its results page instead (FirstHuntSession.cs)
                _matchEndedAt = double.MaxValue; // once
                ShipSink?.Invoke("");
                _online?.EndMatch(); // (an online match goes on in the menus: RESULTS, PLAY AGAIN; its hold then lets it be)
                _netOwner?.Dispose();
                ExitSink?.Invoke();
                return true;
            }
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
                _netOwner?.Dispose();
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
            return _paused ? PauseText() : "";
        }
        RoomMetadata? meta = Metadata.GetRoomById(_host.RoomId);
        string net = _netDriver != null ? "\n" + _netDriver.StatusLine() : ReplayStatusLine() + SoloStatusLine();
        // the gun-adjust pause's lines (L3 + R3) show in a match too, above the scoreboard
        string pause = _paused ? "\n" + PauseText() : "";
        return $"{meta?.InGameName}  {_fps:0} fps  sim {_simMs:0.0} ms  draw {_drawMs:0.0} ms{net}{pause}\n" + MatchStatus.Scoreboard();
    }
}
