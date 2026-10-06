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
// - PRIME mode (RECOMP SETTINGS "map mode", the default): left stick turns, right stick moves, D-pad hops to the next
//   room, A centres the selected room, X goes back to Samus, a tap selects a room.
// - HUNTERS mode (vanilla): D-pad / left stick pan, right stick turns.
// While Samus plays, the connector she's walking through is marked (AreaMapIndex) for the PRIME map; the marks are kept
// in the campaign save (CampaignSaveFile.MapConnectors).
namespace MphRecomp.App;

internal sealed partial class CampaignRenderer
{
    PauseMap? _map;
    readonly MphRecomp.Frontend.UiTextureCache _mapTextures = new();
    readonly UiGlDrawer _mapDrawer = new();
    volatile bool _mapToggle;
    readonly ConcurrentQueue<(float X, float Y, int Kind)> _mapTouches = new();
    readonly Stopwatch _mapClock = new();
    double _mapLast, _mapAccum;
    bool _mapPrevB, _mapPrevA, _mapPrevX, _mapPrevY;
    int _mapPrevHopX, _mapPrevHopY;
    object? _mapBuildKey;
    // connector tracking: the map models' index (built once on a worker), the marks, the room being tracked
    Task<AreaMapIndex>? _mapIndexTask;
    AreaMapIndex? _mapIndex;
    uint[]? _mapConnectors;
    int _mapTrackRoom = -1;
    string? _mapTrackName;

    public bool MapOpen => _map?.IsOpen == true;

    // UI thread: Select
    public void SelectPressed() => _mapToggle = true;

    // UI thread: touches while the map is open (kind 0 down, 1 move, 2 up), in view pixels
    public void MapTouch(float x, float y, int kind) => _mapTouches.Enqueue((x, y, kind));

    // OnSurfaceCreated: the drawer's GL objects belong to the old context
    void MapGlReset()
    {
        _mapDrawer.Init();
        _mapBuildKey = null;
    }

    // GL thread, every frame before the simulation: Select's toggle, Start (the pause menu) closes the map, and the
    // connector Samus is in gets marked
    void MapFrame()
    {
        if (_mapToggle)
        {
            _mapToggle = false;
            if (MapOpen) CloseMap();
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
            _map.Open(new PauseMap.Info
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
            });
            _mapAccum = 0;
            _mapLast = _mapClock.Elapsed.TotalSeconds;
            if (!_mapClock.IsRunning) _mapClock.Start();
            // buttons still held from play mustn't act at once
            PadState pad = Pad;
            _mapPrevB = true;
            _mapPrevA = pad[PadButton.A];
            _mapPrevX = pad[PadButton.X];
            _mapPrevY = pad[PadButton.Y];
            (_mapPrevHopX, _mapPrevHopY) = Hop(pad);
            _mapBuildKey = null;
            while (_mapTouches.TryDequeue(out _)) { }
        }
        catch (Exception ex)
        {
            Log.Error("MPHCampaign", "map open failed: " + ex);
            _map?.Close();
        }
    }

    void CloseMap()
    {
        _map?.Close();
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

    // after Render(): the map covers the whole view; rebuilt only when its picture changed
    void MapDraw()
    {
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
