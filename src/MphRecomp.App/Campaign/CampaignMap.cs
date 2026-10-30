using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading.Tasks;
using MphRead;
using MphRead.Entities;
using MphRead.Formats;
using MphRecomp.Campaign;
using MphRecomp.Config;
using MphRecomp.Frontend;

// The pause map on Select (owner 2026-09-29 + 2026-10-03; was the third-person toggle, now a RECOMP SETTINGS row):
// Core Frontend/PauseMap.cs (the ROM's map frame, area map models, legend and sprites, as one 16:9 screen) over the
// frozen game. Vanilla's own pause screen: the game waits (no simulation steps), every sound stops (StopAllSfx) and
// the map's open sound + the room name's letter blips play; the music goes on. B or Select close it; Start closes it
// and opens the pause menu; touch: drag turns, QUIT -> the recomp pause menu (where QUIT lives); Y switches the
// DETAILED look (saved to the settings); L / R (or the triggers) zoom in / out.
// - PRIME mode (RECOMP SETTINGS "map mode", the default): Core Frontend/PrimeMap.cs, Metroid Prime 1's map screen
//   (owner 2026-10-14): right stick rotates, left stick moves, R / L zoom in / out, A the planet map, Y the panels,
//   X the DETAILED look, B or the map button close it (Prime's close: back to Samus, zoom in, fade); over the paused
//   game blurred (PrimeMapBlur).
// - HUNTERS mode (vanilla): D-pad / left stick pan, right stick turns.
// While Samus plays, the connector she's walking through is marked (AreaMapIndex) for the PRIME map; the marks are kept
// in the campaign save (CampaignSaveFile.MapConnectors).
namespace MphRecomp.App;

internal sealed partial class CampaignRenderer
{
    PauseMap? _map;
    PrimeMap? _prime;
    readonly PrimeMapBlur _mapBlur = new();
    readonly MphRecomp.Frontend.UiTextureCache _mapTextures = new();
    readonly UiGlDrawer _mapDrawer = new();
    volatile bool _mapToggle;
    readonly ConcurrentQueue<(float X, float Y, int Kind)> _mapTouches = new();
    readonly Stopwatch _mapClock = new();
    double _mapLast, _mapAccum;
    bool _mapPrevB, _mapPrevA, _mapPrevX, _mapPrevY, _mapPrevButton;
    // PRIME map presses held until a 60 Hz tick takes them: a frame that runs no tick must not lose one
    bool _primeA, _primeB, _primeX, _primeY;
    // the PRIME map's split tools (PrimeMap.Report.cs): D-pad up / down, L3 or R3; latched like A/B/X/Y
    bool _primeUp, _primeDown, _primeReport, _mapPrevUp, _mapPrevDown, _mapPrevStick;
    // where the PRIME map's split reports go (CampaignLaunch: the player's files) and the text box for a typed name
    // (Android: CampaignActivity; null = none)
    public string? MapReportPath { get; set; }
    public Action<string, Action<string?>>? MapTextRequested { get; set; }
    int _mapPrevHopX, _mapPrevHopY;
    object? _mapBuildKey;
    // connector tracking: the map models' index (built once on a worker), the marks, the room being tracked
    Task<AreaMapIndex>? _mapIndexTask;
    AreaMapIndex? _mapIndex;
    uint[]? _mapConnectors;
    int _mapTrackRoom = -1;
    string? _mapTrackName;

    public bool MapOpen => _map?.IsOpen == true || _prime?.IsOpen == true;

    // UI thread: the touch controls' MAP, a PC's M (a controller's map button: MapFrame)
    public void SelectPressed() => _mapToggle = true;

    // UI thread: touches while the map is open (kind 0 down, 1 move, 2 up), in view pixels
    public void MapTouch(float x, float y, int kind) => _mapTouches.Enqueue((x, y, kind));

    // OnSurfaceCreated: the drawer's GL objects belong to the old context
    void MapGlReset()
    {
        _mapDrawer.Init();
        _mapBlur.Reset();
        _mapBuildKey = null;
    }

    // GL thread, every frame before the simulation: Select's toggle, Start (the pause menu) closes the map, and the
    // connector Samus is in gets marked
    void MapFrame()
    {
        // the controller's map button: the customizer's MAP (Select unless moved; owner 2026-10-11, its own row since
        // 2026-10-17). The touch controls' MAP and a PC's M still come through SelectPressed
        bool mapButton = _padBinds.PhysicalNow(Pad, ControlBinds.Map);
        if (mapButton && !_mapPrevButton && PauseMenu?.IsOpen != true && !EnemyPlacerOwnsSelect) _mapToggle = true; // (Hard Mode's enemy placer has Select, EnemyPlacer.cs)
        _mapPrevButton = mapButton;
        if (_mapToggle)
        {
            _mapToggle = false;
            if (_prime?.IsOpen == true) _prime.RequestClose(); // Prime's way out (animated)
            else if (MapOpen) CloseMap();
            else if (CanOpenMap()) OpenMap();
        }
        if (_openPauseMenu && MapOpen) CloseMap();
        if (!MapOpen) MapTrack();
    }

    // never in a multiplayer match, bots or LAN (owner 2026-10-04: "the map can be opened in multiplayer lets get rid of that")
    bool CanOpenMap() => _host != null && _error == null && !_host.Ended && !ShipOpen && !_paused && PauseMenu?.IsOpen != true
        && !MovieShowing && CameraSequence.Current == null && _matchSettings == null;

    void OpenMap()
    {
        try
        {
            if (_map == null)
            {
                _map = new PauseMap(Paths.FileSystem, _mapTextures);
                _map.PlaySound = id => _sfx?.PlayUi(id, scriptRate: 30);
                _map.Quit = () =>
                {
                    CloseMap();
                    _openPauseMenu = true; // QUIT lives in the recomp pause menu
                };
                _map.Log = m => Log.Info("MPHCampaign", m);
                _map.DetailedChanged = SaveMapLook;
            }
            RecompSettings settings = RecompSettings.Load(SettingsPath);
            _map.Prime = settings.MapPrime;
            _map.SetDetailed(settings.MapDetailed);
            // Prime 1's map screen isn't in the next release (owner 2026-10-17): a public build's PRIME
            // mode is PauseMap's own, as in 0.2.1
            bool primeMap = settings.MapPrime && !RecompSettings.PublicBuild;
            if (primeMap && _prime == null)
            {
                _prime = new PrimeMap(Paths.FileSystem, _mapTextures);
                _prime.PlaySound = id => _sfx?.PlayUi(id, scriptRate: 30);
                _prime.Log = m => Log.Info("MPHCampaign", m);
                _prime.DetailedChanged = SaveMapLook;
                _prime.ReportPath = MapReportPath;
                _prime.TextRequested = MapTextRequested;
            }
            _prime?.SetDetailed(settings.MapDetailed);
            CampaignHost host = _host!;
            PlayerEntity p = host.Player;
            RoomMetadata? meta = Metadata.GetRoomById(host.RoomId);
            StorySave save = MphRead.GameState.StorySave;
            if (_mapIndex == null && _mapIndexTask != null)
            {
                _mapIndexTask.Wait(3000); // started with the campaign; long done by now
                TakeMapIndex();
            }
            _sfx?.StopAll(); // the game's StopAllSfx when the map opens (overlay 8; BizHawk f20)
            var info = new PauseMap.Info
            {
                Room = meta?.Name ?? "",
                Title = meta?.InGameName ?? meta?.Name ?? "",
                Position = p.Position,
                Facing = p.FacingVector,
                Up = p.UpVector,
                Visited = id => save.CheckVisitedRoom(id),
                RoomState = (room, entity) => save.GetRoomState(room, entity),
                Areas = save.Areas,
                Artifacts = save.Artifacts,
                Octoliths = save.CurrentOctoliths,
                LostOctoliths = save.LostOctoliths,
                Weapons = save.Weapons,
                Connectors = MapConnectorBits(),
                BossFlags = (int)save.BossFlags
            };
            if (primeMap) _prime!.Open(info);
            else _map.Open(info);
            _mapAccum = 0;
            _mapLast = _mapClock.Elapsed.TotalSeconds;
            if (!_mapClock.IsRunning) _mapClock.Start();
            // buttons still held from play mustn't act at once
            PadState pad = Pad;
            _mapPrevB = true;
            _mapPrevA = pad[PadButton.A];
            _mapPrevX = pad[PadButton.X];
            _mapPrevY = pad[PadButton.Y];
            _primeA = _primeB = _primeX = _primeY = _primeUp = _primeDown = _primeReport = false;
            (_mapPrevUp, _mapPrevDown, _mapPrevStick) = (true, true, true);
            (_mapPrevHopX, _mapPrevHopY) = Hop(pad);
            _mapBuildKey = null;
            while (_mapTouches.TryDequeue(out _)) { }
        }
        catch (Exception ex)
        {
            Log.Error("MPHCampaign", "map open failed: " + ex);
            _map?.Close();
            _prime?.Close();
        }
    }

    void CloseMap()
    {
        _map?.Close();
        _prime?.Close();
        _accum = 0; // no catch-up burst of steps for the time the game waited
    }

    // Y in the map: the look goes to the settings, as if switched in RECOMP SETTINGS
    void SaveMapLook(bool detailed)
    {
        try
        {
            RecompSettings settings = RecompSettings.Load(SettingsPath);
            settings.MapDetailed = detailed;
            settings.Save(SettingsPath);
        }
        catch (Exception ex)
        {
            Log.Warn("MPHCampaign", "map look not saved: " + ex.Message);
        }
    }

    // GL thread, instead of the simulation while the map is open (a failure closes the map, never the game)
    void MapInput()
    {
        try
        {
            MapInputCore();
        }
        catch (Exception ex)
        {
            Log.Error("MPHCampaign", "map input failed: " + ex);
            CloseMap();
        }
    }

    static (int X, int Y) Hop(PadState pad)
    {
        int x = pad.HatX <= -0.5f || pad[PadButton.DpadLeft] ? -1 : pad.HatX >= 0.5f || pad[PadButton.DpadRight] ? 1 : 0;
        int y = pad.HatY <= -0.5f || pad[PadButton.DpadUp] ? 1 : pad.HatY >= 0.5f || pad[PadButton.DpadDown] ? -1 : 0;
        return (x, y);
    }

    void MapInputCore()
    {
        if (_prime?.IsOpen == true)
        {
            PrimeInput(_prime);
            return;
        }
        if (_map == null) return;
        while (_mapTouches.TryDequeue(out var t))
        {
            _map.Touch(t.X, t.Y, t.Kind);
            if (!MapOpen) return;
        }
        PadState pad = Pad;
        bool b = pad[PadButton.B];
        bool pressB = b && !_mapPrevB;
        _mapPrevB = b;
        if (pressB)
        {
            CloseMap();
            return;
        }
        bool a = pad[PadButton.A], x = pad[PadButton.X], y = pad[PadButton.Y];
        (int hx, int hy) = Hop(pad);
        var input = new PauseMap.Input
        {
            ZoomIn = pad[PadButton.L1] || pad.L2 > 0.5f,
            ZoomOut = pad[PadButton.R1] || pad.R2 > 0.5f,
            ToggleLook = y && !_mapPrevY
        };
        if (_map.Prime)
        {
            // Metroid Prime's map: the left stick turns, the right stick moves, the D-pad hops room to room
            input.TurnX = pad.Lx;
            input.TurnY = -pad.Ly;
            input.PanX = pad.Rx;
            input.PanY = -pad.Ry;
            if ((hx, hy) != (_mapPrevHopX, _mapPrevHopY) && (hx != 0 || hy != 0))
            {
                input.HopX = hx;
                input.HopY = hy;
            }
            input.Centre = a && !_mapPrevA;
            input.Home = x && !_mapPrevX;
        }
        else
        {
            input.PanX = hx != 0 ? hx : pad.Lx;
            input.PanY = hy != 0 ? hy : -pad.Ly;
            input.TurnX = pad.Rx;
            input.TurnY = pad.Ry;
        }
        _mapPrevA = a;
        _mapPrevX = x;
        _mapPrevY = y;
        (_mapPrevHopX, _mapPrevHopY) = (hx, hy);
        double now = _mapClock.Elapsed.TotalSeconds, dt = Math.Min(now - _mapLast, 0.1);
        _mapLast = now;
        _mapAccum = Math.Min(_mapAccum + dt, 4 / 60.0);
        // the host doesn't step while the map is up: its sounds advance here (as the ship screens do)
        _sfx?.Update((float)dt);
        while (_mapAccum >= 1 / 60.0)
        {
            _mapAccum -= 1 / 60.0;
            _map.Tick(input);
            // a press acts once, however many ticks this frame runs
            input.HopX = input.HopY = 0;
            input.Centre = input.Home = input.ToggleLook = false;
        }
    }

    // the PRIME map (Prime 1's controls, owner's sticks): right stick rotates, left stick moves, R in / L out (the
    // triggers too), A planet map, Y panels, X look, B out
    void PrimeInput(PrimeMap map)
    {
        while (_mapTouches.TryDequeue(out var t))
        {
            map.Touch(t.X, t.Y, t.Kind);
        }
        PadState pad = Pad;
        bool a = pad[PadButton.A], b = pad[PadButton.B], x = pad[PadButton.X], y = pad[PadButton.Y];
        var input = new PrimeMap.Input
        {
            MoveX = pad.Lx,
            MoveY = -pad.Ly,
            RotateX = pad.Rx,
            RotateY = -pad.Ry,
            ZoomIn = pad[PadButton.R1] || pad.R2 > 0.5f,
            ZoomOut = pad[PadButton.L1] || pad.L2 > 0.5f,
        };
        _primeA |= a && !_mapPrevA;
        _primeB |= b && !_mapPrevB;
        _primeX |= x && !_mapPrevX;
        _primeY |= y && !_mapPrevY;
        (_mapPrevA, _mapPrevB, _mapPrevX, _mapPrevY) = (a, b, x, y);
        bool up = pad.HatY <= -0.5f || pad[PadButton.DpadUp], down = pad.HatY >= 0.5f || pad[PadButton.DpadDown];
        bool stick = pad[PadButton.ThumbL] || pad[PadButton.ThumbR];
        _primeUp |= up && !_mapPrevUp;
        _primeDown |= down && !_mapPrevDown;
        _primeReport |= stick && !_mapPrevStick;
        (_mapPrevUp, _mapPrevDown, _mapPrevStick) = (up, down, stick);
        double now = _mapClock.Elapsed.TotalSeconds, dt = Math.Min(now - _mapLast, 0.1);
        _mapLast = now;
        _mapAccum = Math.Min(_mapAccum + dt, 4 / 60.0);
        _sfx?.Update((float)dt);
        while (_mapAccum >= 1 / 60.0 && map.IsOpen)
        {
            _mapAccum -= 1 / 60.0;
            // a press acts once, on the first tick after it, however many ticks this frame runs
            (input.World, input.Close, input.ToggleLook, input.Legend) = (_primeA, _primeB, _primeX, _primeY);
            (input.Up, input.Down, input.Report) = (_primeUp, _primeDown, _primeReport);
            _primeA = _primeB = _primeX = _primeY = _primeUp = _primeDown = _primeReport = false;
            map.Tick(input);
        }
        if (!map.IsOpen) CloseMap(); // its close animation ended: no catch-up burst of steps
    }

    // after Render(): the map covers the whole view; rebuilt only when its picture changed
    void MapDraw()
    {
        if (_prime?.IsOpen == true)
        {
            try
            {
                // the paused game behind, blurred: this frame's picture, before the map goes over it
                if (_prime.Blur > 0)
                {
                    int blurred = _mapBlur.Capture(_width, _height);
                    if (blurred != 0) _mapDrawer.UseGlTexture(_prime.BlurTexture, blurred);
                }
                object primeKey = _prime.BuildKey(_width, _height);
                if (!primeKey.Equals(_mapBuildKey))
                {
                    _prime.Build(_width, _height);
                    _mapBuildKey = primeKey;
                }
                _mapDrawer.Draw(_prime.DrawList, _width, _height, smooth: false);
                MapHelmet();
                _mapDrawer.Draw(_prime.Overlay, _width, _height, smooth: false);
            }
            catch (Exception ex)
            {
                Log.Error("MPHCampaign", "map draw failed: " + ex);
                CloseMap();
            }
            return;
        }
        if (_map == null || !_map.IsOpen) return;
        try
        {
            object key = _map.BuildKey(_width, _height);
            if (!key.Equals(_mapBuildKey))
            {
                _map.Build(_width, _height);
                _mapBuildKey = key;
            }
            _mapDrawer.Draw(_map.DrawList, _width, _height, smooth: false);
        }
        catch (Exception ex)
        {
            Log.Error("MPHCampaign", "map draw failed: " + ex);
            CloseMap();
        }
    }

    // while the PRIME map is up the helmet's layers stay out of the game's HUD pass, so the blur behind the map is only
    // the world and the HUD (owner 2026-10-14: "the blur should never touch these"); MapHelmet draws them over the map
    bool MapLiftsHelmet(Scene.HostHudItem item, Scene scene) => _prime?.IsOpen == true && item.SpriteIndex < 0
        && item.BindingId != -1 && (item.BindingId == scene.Layer3Info.BindingId || item.BindingId == scene.Layer2Info.BindingId);

    // Prime keeps Samus's helmet over its map screen (owner 2026-10-14: "the images that represent the view of her visor
    // edges are still there"): MphRead's helmet layers (back, then front; the visor's lines and the HUD stay under the
    // map), drawn again from the HUD items the last step recorded (the game waits while the map is up, so they stand),
    // sharp and at full strength the whole time the map is open, as the game's HUD pass would have drawn them
    void MapHelmet()
    {
        Scene? scene = _host?.Scene;
        if (scene == null) return;
        int back = scene.Layer3Info.BindingId, front = scene.Layer2Info.BindingId;
        if (back == -1 && front == -1) return;
        GLES30.GlDisable(GLES30.GlDepthTest);
        GLES30.GlEnable(GLES30.GlBlend);
        GLES30.GlBlendFunc(GLES30.GlSrcAlpha, GLES30.GlOneMinusSrcAlpha);
        GLES30.GlUseProgram(_hudProgram);
        GLES30.GlBindVertexArray(_hudVao);
        GLES30.GlUniform1f(HU("view_width"), _width);
        GLES30.GlUniform1f(HU("view_height"), _height);
        GLES30.GlUniform4f(HU("fade_color"), 0, 0, 0, 0);
        GLES30.GlBindSampler(0, _samplers[0]);
        GlNative.Uniform1i(HU("use_mask"), 0);
        for (int i = 0; i < scene.HudItemCount; i++)
        {
            Scene.HostHudItem item = scene.GetHudItem(i);
            if (item.SpriteIndex >= 0 || (item.BindingId != back && item.BindingId != front)) continue;
            if (!BindTexture(item.BindingId, scene)) continue;
            GlNative.Uniform1f(HU("alpha"), item.Alpha);
            DrawHudQuad(item.Left, item.Right, item.Top, item.Bottom);
        }
        GLES30.GlBindSampler(0, 0);
        GLES30.GlBindVertexArray(0);
        GLES30.GlUseProgram(_program);
        GLES30.GlEnable(GLES30.GlDepthTest);
    }

    // ---------------------------------------------------------------- connectors walked through

    // every frame while Samus plays: the map models' index is built once on a worker, then the connector she is in (if
    // any) is marked
    void MapTrack()
    {
        if (_host == null || _error != null || _host.Ended) return;
        if (_mapIndexTask == null)
        {
            if (_host.Frame < 60) return; // clear of the first room's load
            string root = Paths.FileSystem;
            _mapIndexTask = Task.Run(() => AreaMapIndex.Build(root));
            return;
        }
        if (_mapIndex == null)
        {
            if (!_mapIndexTask.IsCompleted) return;
            if (!TakeMapIndex()) return;
        }
        uint[]? bits = MapConnectorBits();
        if (bits == null) return;
        if (_host.RoomId != _mapTrackRoom)
        {
            _mapTrackRoom = _host.RoomId;
            _mapTrackName = Metadata.GetRoomById(_host.RoomId, noThrow: true)?.Name;
        }
        if (_mapTrackName == null) return;
        if (_mapIndex!.ConnectorAt(_mapTrackName, _host.Player.Position) is (int pair, int number) && AreaMapIndex.Set(bits, pair, number))
        {
            Log.Info("MPHCampaign", $"map: Con{number:00} of area {pair} walked through (in {_mapTrackName})");
        }
    }

    bool TakeMapIndex()
    {
        if (_mapIndexTask == null || !_mapIndexTask.IsCompleted) return false;
        if (_mapIndexTask.IsFaulted || _mapIndexTask.IsCanceled)
        {
            Log.Error("MPHCampaign", "map index failed: " + _mapIndexTask.Exception?.GetBaseException());
            _mapIndexTask = Task.FromResult(new AreaMapIndex()); // tracking off; the map still opens
        }
        _mapIndex = _mapIndexTask.Result;
        return true;
    }

    // the marks; a save from before they were kept starts with every connector between two visited rooms
    uint[]? MapConnectorBits()
    {
        if (_mapConnectors == null && _mapIndex != null)
        {
            _mapConnectors = new uint[8];
            _mapIndex.Seed(_mapConnectors, id => MphRead.GameState.StorySave.CheckVisitedRoom(id));
        }
        return _mapConnectors;
    }

    // CampaignShip: a save loaded / written
    void MapAdoptConnectors(uint[]? bits)
    {
        if (bits == null)
        {
            _mapConnectors = null;
            return;
        }
        _mapConnectors = new uint[8];
        Array.Copy(bits, _mapConnectors, Math.Min(bits.Length, 8));
    }

    uint[]? MapConnectorsForSave() => _mapConnectors == null ? null : (uint[])_mapConnectors.Clone();
}
