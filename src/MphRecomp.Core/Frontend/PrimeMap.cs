using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MphRead;
using MphRead.Formats;
using MphRead.Text;
using MphRecomp.Campaign;
using OpenTK.Mathematics;

// The PRIME map mode (RECOMP SETTINGS "map mode"): Metroid Prime 1's map screen (CAutoMapper) rebuilt 1:1 on MPH's own
// map models, owner 2026-10-14. Behaviour, numbers and colours come from the Prime 1 disc's AutoMapper tweak and the
// decomp (spec: docs/PRIME_MAP.md); MPH rooms measure the same as Prime's (median map room 59 vs 60 units), so the
// tweak's camera distances and speeds are used as they are.
//
// Flow: opening zooms out from the minimap's framing (distance 120 -> 175, field of view 45 -> 75 degrees, 0.5 s) over
// the paused game, which blurs in at 2.0/s (the App draws the blurred frame into BlurTexture). Closing first pans back
// to Samus's room (0.25 s), then zooms back in and fades (0.5 s). A goes to the planet (universe) map and, on a planet,
// back to its room map (0.5 s each way); Y cycles the panels (legend + help / help / none); B or the map button close.
// Owner's changes from GameCube: the RIGHT stick rotates and the LEFT stick moves, with no button to hold (GC: main
// stick rotates, C-stick moves). X switches the DETAILED look (a recomp option; X does nothing in Prime).
//
// Camera (per 60 Hz tick): rotate 3 degrees at full tilt, only the strongest of the four stick directions each tick (no
// diagonals); pitch -89.9 (top down) .. 0 (level), stick up = towards level; yaw unbounded. Zoom 3 units a tick between
// 50 and 700 (planet map: 12, 400..2000), the button held first keeps priority. Move 3 units a tick x distance / 175,
// along the screen's right and up; the point stays inside the shown rooms' enclosing circle (flat) and their height
// slab. The selected room is the one whose centre lies nearest the line of sight through the point; with no move input
// the point slides to its centre at 15 units a tick.
//
// Drawing: every shown room and walked connector, all their faces both sides, back to front over the whole map; visited
// orange (fill 0.30 alpha, outline 0.30), the selected room in the select colours (0.902 / 1.0), Samus's room pulsing to
// white (0.75 s triangle); faces shaded 0.4 + 0.4 x how much their outside faces the camera. Doors: the selected room's
// in their beam colour, the others' green once walked through (Prime's open-door colour), else hidden. Teleporters are
// MPH's [P]/[B] icons, 5.2 units x distance / 50; Samus is MPH's map marker, pulsing (0.75 + 0.25 x a 1.26 s sine).
//
// Everything is projected on the CPU into one draw list (as PauseMap), so the PC tool renders what the device shows.
namespace MphRecomp.Frontend
{
    public sealed partial class PrimeMap
    {
        // one 60 Hz tick's input
        public struct Input
        {
            public float MoveX, MoveY; // left stick: move the view (+y = up / away)
            public float RotateX, RotateY; // right stick: rotate (+x right, +y up)
            public bool ZoomIn, ZoomOut; // held: R / L
            public bool World, Legend, Close, ToggleLook; // presses: A, Y, B (or the map button), X
            public bool Up, Down, Report; // presses: D-pad up (the splits) / down, L3 or R3 (the report menu): PrimeMap.Report.cs
        }

        private enum Mode { Closed, Opening, Area, ToUniverse, Universe, FromUniverse, ClosePan, Closing }

        // ---------------------------------------------------------------- the AutoMapper tweak (Prime 1 disc)

        private const float CamDist = 175, MinCamDist = 50, MaxCamDist = 700, MinPitch = -89.9f, MaxPitch = 0, CamFov = 75;
        private const float MiniCamDist = 120, MiniPitch = -45, MiniFov = 45;
        private const float OpenTime = 0.5f, CloseTime = 0.5f, ClosePanTime = 0.25f, UniverseTime = 0.5f;
        private const float ZoomPerTick = 3, RotatePerTick = 3, MovePerTick = 3, RecentrePerTick = 5 * 3;
        private const float LeaveZoom = 4; // how far the map going away backs off while it fades (x its distance)
        private const float UniverseDist = 800, MinUniverseDist = 400, MaxUniverseDist = 2000, UniverseZoom = 4;
        private const float IconSize = 5.2f, MarkerCap = 0.6f * 700 / 50, PulsePeriod = 0.75f, BlurRate = 2, PaneRate = 2;
        private const float ShadeConstant = 0.4f, ShadeLinear = 0.4f;
        private const float AlphaSurface = 0.30f, AlphaOutline = 0.30f;
        private static readonly Vector4 SurfVisited = new(0.957f, 0.408f, 0.020f, 1);
        private static readonly Vector4 OutlineVisited = new(1.000f, 0.529f, 0.024f, 1);
        private static readonly Vector4 SurfSelect = new(0.957f, 0.675f, 0.012f, 0.902f);
        private static readonly Vector4 OutlineSelect = new(0.980f, 0.840f, 0.750f, 1.0f);
        private static readonly Vector4 FlashColour = new(1, 1, 1, 1);
        private static readonly Vector3 OpenDoor = new(0.016f, 0.725f, 0.075f);

        // ---------------------------------------------------------------- recomp layout (DS pixels of a 192-high canvas)

        private const float Near = 1; // the near plane (map units)
        private const float LineWidth = 0.45f; // outline width (canvas units; Prime's map screen draws its outlines 2x)
        private const float PlanetSize = 190; // a planet on the planet map (universe units)
        private const float PlanetSpread = 6; // the ship's star map: DS pixels -> universe units
        private static readonly (float R, float G, float B) LabelColour = (0.66f, 0.86f, 1.0f);
        private static readonly (float R, float G, float B) KeyColour = (1, 1, 1);
        private const int OpenSound = 0x19; // MPH's map open
        // MPH's beam door colours (ov8 0x2133150, 5-bit), > 7: grey 20
        private static readonly Vector3[] DoorColour =
        {
            new(28, 28, 28), new(31, 31, 0), new(30, 18, 10), new(0, 31, 0), new(31, 0, 0), new(20, 9, 31), new(31, 16, 0), new(0, 16, 31)
        };
        // legend rows (ov8 0x21331c8): beam doors (HudMessagesSP B00n, map_legendDoors frame), the others (M00n, sprite, frame)
        private static readonly (int Name, int Frame)[] BeamRows = { (4, 4), (2, 5), (8, 1), (5, 0), (6, 3), (7, 2) };
        private static readonly (int Name, bool Others, int Frame)[] OtherRows = { (1, false, 7), (3, false, 8), (2, true, 0), (4, true, 1) };
        // the planets in the game's order (ShipPlanetSelect: shipSpace sprites, star-map positions in DS pixels)
        private static readonly string[] PlanetFiles = { "alinos", "archives", "outpost", "murk", "prison" };
        private static readonly (int X, int Y)[] PlanetAt = { (116, 48), (134, 94), (196, 28), (217, 70), (165, 61) };
        private static readonly string[] Hunters = { "samus", "kanden", "trace", "sylux", "noxus", "spyre", "weavel" };
        private static readonly (int X, int Y)[] ArtifactAt = { (-10, -7), (10, -7), (0, 12) };

        // ---------------------------------------------------------------- state

        private readonly string _root;
        private readonly UiTextureCache _textures;
        private readonly MenuFont _font;
        private readonly List<WidgetTri> _glyphs = new();
        private readonly int[] _doorKeys = new int[9], _others = new int[4], _planetTex = new int[5];
        private readonly int[] _portal = new int[2], _crystal = new int[3], _red = new int[2], _art = new int[8], _hunterFaces = new int[7];
        private PauseMap.SmallModel? _door, _marker;

        private sealed class Shown
        {
            public AreaMapModel.Part Part = null!;
            public AreaMapModel Model = null!;
            public Vector3 Offset;
            // the room's middle, as Prime's area point of interest (its box's centre): the camera settles on it after a
            // move and turns round it, so the room stays in the middle of the screen (owner 2026-10-14)
            public Vector3 Centre => Middle + Offset;
            public Vector3 Middle;
            // a door-bounded corridor or chamber split off its room (CorridorMap, owner 2026-10-14: an area of its own,
            // "Corridor A", shown once entered): its index in CorridorMap.Of(room); -1 = the room, less its pieces
            public int Piece = -1;
            public CorridorMap.Piece? PieceInfo;
            public sbyte[]? PolyPieces; // per Nav poly: its piece (-1 = the room); null = the room has no pieces
            public bool Draws(int poly) => PolyPieces == null || poly >= PolyPieces.Length || PolyPieces[poly] == Piece;
            public List<(Vector3 Pos, int Frame)> Teleporters = new();
        }

        private PauseMap.Info _info = new();
        private Mode _mode;
        private float _t; // seconds into the current transition
        private int _tick;
        private float _time;
        // the planet pair on screen (area ids 2p, 2p + 1) and Samus's; -1 = none (the Oubliette)
        private int _pair = -1, _ownPair = -1;
        private AreaMapModel? _modelA, _modelB;
        private readonly List<Shown> _shown = new();
        private Shown? _selected, _playerRoom;
        private Vector3 _playerPos; // Samus on the map (her marker)
        private readonly List<(Vector3 Pos, Matrix3 Rot, Vector3 Colour)> _doors = new();
        private string _title = "";
        // camera: yaw / pitch in degrees, distance, field of view, the point looked at (area or universe space)
        private float _yaw, _pitch = MiniPitch, _dist = CamDist, _fov = CamFov;
        private Vector3 _focus;
        // the map going away while A switches maps: it keeps its own point and backs straight away from it (owner
        // 2026-10-14: the room map slid to the bottom left as the planets came in, drawn round the planet's point)
        private Vector3 _leaveFocus;
        private float _leaveFrom, _leaveDist;
        private float _fromDist, _toDist, _fromFov;
        private Vector3 _fromFocus, _toFocus;
        private float _areaAlpha, _universeAlpha;
        private int _zoomState; // 0 none, 1 in, 2 out (the first held keeps priority)
        private int _planet = -1, _ownPlanet = -1; // the selected planet on the planet map; Samus's
        // Y: 0 legend + help, 1 help, 2 none (kept between openings, as Prime's system state); the panes' slide 0..1
        private int _keyState;
        private float _leftPane, _yPane, _bottomPane;
        // touch: a drag rotates; a tap on a room selects it, on a label presses its button
        private bool _dragging;
        private float _downX, _downY, _lastX, _lastY, _travel;
        private Shown? _forced; // a tapped room: selected until the view reaches it
        private float _scale = 1, _w = 341;
        private readonly int _blurTex;

        public UiDrawList DrawList { get; }
        // the panels (names, help, legend, slots): drawn after DrawList with the helmet between (Prime keeps Samus's
        // helmet over its map screen, owner 2026-10-14; the App draws MphRead's helmet layers between the two)
        public UiDrawList Overlay { get; }
        private UiDrawList _out = null!;
        public bool IsOpen => _mode != Mode.Closed;
        public bool Detailed { get; private set; }
        public Action<int>? PlaySound;
        public Action<string>? Log;
        public Action<bool>? DetailedChanged; // X switched the look
        // the paused game behind, blurred (0..1, ramps at 2.0/s); the App fills BlurTexture's GL texture
        public float Blur { get; private set; }
        public int BlurTexture => _blurTex;

        public PrimeMap(string fileSystemRoot, UiTextureCache textures)
        {
            _root = fileSystemRoot;
            _textures = textures;
            DrawList = new UiDrawList(textures);
            Overlay = new UiDrawList(textures);
            _out = DrawList;
            _font = new MenuFont(textures);
            string sp = Path.Combine(fileSystemRoot, "_archives", "spSamus");
            for (int i = 0; i < 9; i++) _doorKeys[i] = Sprite(sp, "map_legendDoors", i);
            for (int i = 0; i < 4; i++) _others[i] = Sprite(sp, "map_legendOthers", i);
            for (int i = 0; i < 2; i++) _portal[i] = Sprite(sp, "map_portal", i);
            for (int i = 0; i < 3; i++) _crystal[i] = Sprite(sp, "map_crystalbig", i);
            for (int i = 0; i < 2; i++) _red[i] = Sprite(sp, "map_crystalred", i);
            for (int i = 0; i < 8; i++) _art[i] = Sprite(sp, "map_art_" + (i + 1), 0);
            string common = Path.Combine(fileSystemRoot, "_archives", "common");
            for (int i = 0; i < 7; i++) _hunterFaces[i] = Sprite(common, "enemy_" + Hunters[i], 0);
            string space = Path.Combine(fileSystemRoot, "_archives", "shipSpace");
            for (int p = 0; p < 5; p++) _planetTex[p] = Planet(space, PlanetFiles[p]);
            // a placeholder the App swaps for its blurred copy of the game's frame ("bg/": drawn smoothed)
            _blurTex = _textures.GetOrAdd("bg/primemap-blur", () => (1, 1, new byte[4])).Id;
        }

        private int Sprite(string dir, string name, int image) => _textures.GetOrAdd($"map/{name}/{image}", () =>
        {
            DsImage im = DsGraphics.LoadSprite(Path.Combine(dir, name + ".bin")).Image(image, 0);
            return (im.Width, im.Height, im.Rgba);
        }).Id;

        // the star map's close-up of a planet (shipSpace zoom_<name>), cut round: its square frame and corners dropped,
        // the rim softened
        private int Planet(string dir, string name) => _textures.GetOrAdd($"bg/primemap-planet/{name}", () =>
        {
            DsImage im = DsGraphics.LoadSprite(Path.Combine(dir, "zoom_" + name + ".bin")).Image(0, 0);
            byte[] rgba = (byte[])im.Rgba.Clone();
            float cx = im.Width / 2f, cy = im.Height / 2f, r = Math.Min(im.Width, im.Height) * 0.45f;
            for (int y = 0; y < im.Height; y++)
            {
                for (int x = 0; x < im.Width; x++)
                {
                    float d = MathF.Sqrt((x + 0.5f - cx) * (x + 0.5f - cx) + (y + 0.5f - cy) * (y + 0.5f - cy));
                    float k = Math.Clamp((r - d) / 2, 0, 1);
                    int i = (y * im.Width + x) * 4 + 3;
                    rgba[i] = (byte)(rgba[i] * k);
                }
            }
            return (im.Width, im.Height, rgba);
        }).Id;

        private static string Hud(char type, int id) => Strings.GetMessage(type, id, StringTables.HudMessagesSP);

        // ---------------------------------------------------------------- open / close

        public void Open(PauseMap.Info info)
        {
            _info = info;
            _entities.Clear();
            _tick = 0;
            _time = 0;
            _dragging = false;
            _forced = null;
            _menu = null;
            _typing = false;
            _toastTime = 0;
            _zoomState = 0;
            Blur = 0;
            AreaMapModel? own = AreaMapModel.ForRoom(_root, info.Room);
            _ownPair = own == null ? -1 : (AreaMapModel.UnitHalf(own.File).Unit - 1) * 2;
            _ownPlanet = _ownPair >= 0 ? _ownPair / 2 : PlanetOfRoom(info.Room);
            // facing the way Samus looks (Prime: the player camera's yaw)
            _yaw = MathHelper.RadiansToDegrees(MathF.Atan2(-info.Facing.X, -info.Facing.Z));
            _pitch = MiniPitch;
            _fov = _fromFov = MiniFov;
            if (_ownPair >= 0)
            {
                LoadPair(_ownPair);
                _selected = _playerRoom;
                _focus = _selected?.Centre ?? Vector3.Zero;
                _toDist = CamDist;
                _areaAlpha = 0;
                _universeAlpha = 0;
            }
            else
            {
                // no room map here (the Oubliette): straight to the planet map
                _pair = -1;
                _shown.Clear();
                _planet = _ownPlanet >= 0 ? _ownPlanet : 4;
                _focus = PlanetPos(_planet);
                _toDist = UniverseDist;
                _areaAlpha = 0;
                _universeAlpha = 0;
            }
            _fromDist = _toDist * MiniCamDist / CamDist;
            _dist = _fromDist;
            SetTitle();
            _mode = Mode.Opening;
            _t = 0;
            Log?.Invoke($"primemap: {info.Room} -> pair {_ownPair}, {_shown.Count} parts shown, {_doors.Count} doors{(Detailed ? ", detailed" : "")}");
            PlaySound?.Invoke(OpenSound);
        }

        // at once (the pause menu took over)
        public void Close()
        {
            _mode = Mode.Closed;
            _dragging = false;
        }

        // B / the map button: Prime's way out (pan home, zoom back in, fade)
        public void RequestClose()
        {
            if (_mode == Mode.Area && _pair == _ownPair && _playerRoom != null)
            {
                _fromFocus = _focus;
                _toFocus = _playerRoom.Centre;
                Begin(Mode.ClosePan);
            }
            else if (_mode is Mode.Area or Mode.Universe or Mode.Opening)
            {
                BeginClosing();
            }
        }

        private void BeginClosing()
        {
            _fromDist = _dist;
            _toDist = _dist * MiniCamDist / CamDist;
            _fromFov = _fov;
            Begin(Mode.Closing);
        }

        private void Begin(Mode mode)
        {
            _mode = mode;
            _t = 0;
            _dragging = false;
        }

        public void SetDetailed(bool on)
        {
            if (Detailed == on) return;
            Detailed = on;
            if (on && IsOpen) LoadDetail();
        }

        private void LoadDetail()
        {
            foreach (AreaMapModel? m in new[] { _modelA, _modelB })
            {
                if (m != null && !m.DetailLoaded) m.LoadDetail(Log);
            }
        }

        // ---------------------------------------------------------------- what's shown

        private static int PlanetOfRoom(string room)
        {
            (RoomMetadata? meta, int id) = Metadata.GetRoomByName(room.ToUpperInvariant());
            if (meta == null) return -1;
            int area = Metadata.GetAreaInfo(id);
            return area < 0 ? -1 : Math.Min(area / 2, 4);
        }

        private bool PlanetFound(int p) => p >= 0 && p < 5 && (_info.Areas & (1 << (2 * p))) != 0 || p == _ownPlanet;

        // both halves of a planet (unitN_1, unitN_2), the rooms visited and connectors walked through
        private void LoadPair(int pair)
        {
            if (pair != _pair || _modelA == null)
            {
                _pair = pair;
                int unit = pair / 2 + 1;
                AreaMapModel? Half(int h)
                {
                    string? file = AreaMapModel.FindFiles(_root).FirstOrDefault(f => AreaMapModel.UnitHalf(f) == (unit, h));
                    return file == null ? null : AreaMapModel.Load(file);
                }
                _modelA = Half(1);
                _modelB = Half(2);
            }
            if (Detailed) LoadDetail();
            _shown.Clear();
            _playerRoom = null;
            // the piece Samus stands in counts as entered now, whatever the HUD shows
            if (pair == _ownPair)
            {
                foreach (AreaMapModel.Part part in new[] { _modelA, _modelB }.Where(m => m != null).SelectMany(m => m!.Parts))
                {
                    if (part.IsConnector || !part.Name.Equals(_info.Room, StringComparison.OrdinalIgnoreCase)) continue;
                    if (CorridorMap.At(part.Name, null, _info.Position + part.Origin) is CorridorMap.Piece entered) CorridorMap.MarkSeen(entered);
                }
            }
            // the connectors past an unseen piece's own doors stay hidden with it
            var hiddenCons = new HashSet<string>(CorridorMap.Pieces.Where(c => !CorridorMap.Seen(c)).SelectMany(c => c.Connectors),
                StringComparer.OrdinalIgnoreCase);
            void Add(AreaMapModel? model, Vector3 offset)
            {
                if (model == null) return;
                foreach (AreaMapModel.Part part in model.Parts)
                {
                    bool own = pair == _ownPair && part.Name.Equals(_info.Room, StringComparison.OrdinalIgnoreCase);
                    bool show = part.IsConnector ? AreaMapIndex.Has(_info.Connectors, pair, part.ConnectorNumber) && !hiddenCons.Contains(part.Name)
                        : own || part.RoomId >= 0 && _info.Visited(part.RoomId);
                    if (!show) continue;
                    var s = new Shown { Part = part, Model = model, Offset = offset };
                    CorridorMap.Piece[] pieces = part.IsConnector ? Array.Empty<CorridorMap.Piece>() : CorridorMap.Of(part.Name);
                    if (pieces.Length > 0) s.PolyPieces = CorridorMap.PolyPieces(model, part);
                    s.Middle = Middle(s);
                    if (!part.IsConnector) s.Teleporters.AddRange(Teleporters(part));
                    // a teleporter in a piece not entered yet stays hidden with it
                    s.Teleporters.RemoveAll(t => pieces.Any(c => !CorridorMap.Seen(c) && t.Pos.X >= c.Min.X - 1 && t.Pos.X <= c.Max.X + 1
                        && t.Pos.Y >= c.Min.Y - 2 && t.Pos.Y <= c.Max.Y + 2 && t.Pos.Z >= c.Min.Z - 1 && t.Pos.Z <= c.Max.Z + 1));
                    _shown.Add(s);
                    Vector3 samus = _info.Position + part.Origin;
                    CorridorMap.Piece? inPiece = own ? CorridorMap.At(part.Name, null, samus) : null;
                    if (own)
                    {
                        _playerRoom = s;
                        _playerPos = samus + Vector3.UnitY + offset;
                    }
                    for (int i = 0; i < pieces.Length; i++)
                    {
                        if (!CorridorMap.Seen(pieces[i])) continue;
                        var ps = new Shown { Part = part, Model = model, Offset = offset, Piece = i, PieceInfo = pieces[i], PolyPieces = s.PolyPieces };
                        ps.Middle = Middle(ps);
                        _shown.Add(ps);
                        if (pieces[i] == inPiece) _playerRoom = ps;
                    }
                }
            }
            // the middle of what a part draws (Prime's area point of interest: its box's centre)
            static Vector3 Middle(Shown s)
            {
                MapGeometry g = s.Part.Nav;
                if (g.Points.Count == 0) return s.Part.Centre;
                if (s.PolyPieces == null) return (s.Part.Min + s.Part.Max) / 2;
                Vector3 min = new(float.MaxValue), max = new(float.MinValue);
                for (int pi = 0; pi < g.Polys.Count; pi++)
                {
                    if (!s.Draws(pi)) continue;
                    MapGeometry.Poly p = g.Polys[pi];
                    for (int k = 0; k < p.Count; k++)
                    {
                        Vector3 v = g.Points[g.Indices[p.Start + k]];
                        min = Vector3.ComponentMin(min, v);
                        max = Vector3.ComponentMax(max, v);
                    }
                }
                return min.X <= max.X ? (min + max) / 2 : (s.Part.Min + s.Part.Max) / 2;
            }
            Add(_modelA, Vector3.Zero);
            Add(_modelB, AreaMapModel.HalfOffsets[pair + 1]);
            ComputeBounds();
            BuildDoors();
        }

        private bool AnyRoomVisited(int planet)
        {
            if (planet < 0 || planet > 3) return false;
            if (planet * 2 == _ownPair) return true;
            int unit = planet + 1;
            foreach (string file in AreaMapModel.FindFiles(_root).Where(f => AreaMapModel.UnitHalf(f).Unit == unit))
            {
                foreach (AreaMapModel.Part p in AreaMapModel.Load(file).Parts)
                {
                    if (!p.IsConnector && p.RoomId >= 0 && _info.Visited(p.RoomId)) return true;
                }
            }
            return false;
        }

        private readonly Dictionary<string, IReadOnlyList<Entity>> _entities = new(StringComparer.OrdinalIgnoreCase);

        // the room's entities on the layer the game loads it with now (SceneSetup: by the area's boss flags)
        private IReadOnlyList<Entity> RoomEntities(string room)
        {
            if (_entities.TryGetValue(room, out IReadOnlyList<Entity>? list)) return list;
            list = Array.Empty<Entity>();
            try
            {
                (RoomMetadata? meta, int id) = Metadata.GetRoomByName(room.ToUpperInvariant());
                if (meta?.EntityPath != null)
                {
                    int area = Metadata.GetAreaInfo(id);
                    int layer = _info.BossFlags < 0 || area < 0 || area >= 8 ? -1 : (_info.BossFlags >> (2 * area)) & 3;
                    list = Read.GetEntities(meta.EntityPath, layer, firstHunt: false);
                }
            }
            catch (Exception ex)
            {
                Log?.Invoke($"primemap: entities of {room}: {ex.Message}");
            }
            _entities[room] = list;
            return list;
        }

        // teleporters (entity type 14): [B] for an artifact portal, else [P]; entity position + the room node's + 1 up
        private List<(Vector3, int)> Teleporters(AreaMapModel.Part room)
        {
            var list = new List<(Vector3, int)>();
            foreach (Entity e in RoomEntities(room.Name))
            {
                if (e.Type != EntityType.Teleporter || e is not Entity<TeleporterEntityData> tp) continue;
                Vector3 pos = e.Position + room.Origin + Vector3.UnitY;
                if (list.Any(t => (t.Item1 - pos).LengthSquared < 0.01f)) continue;
                list.Add((pos, tp.Data.ArtifactId < 8 && tp.Data.Invisible == 0 ? 1 : 0));
            }
            return list;
        }

        // doors: the selected room's in their beam colour (grey until known, as MPH's map), every other room's green once
        // a connector at it was walked through (Prime: visited doors in the open-door colour, the rest hidden)
        private void BuildDoors()
        {
            _doors.Clear();
            foreach (Shown s in _shown)
            {
                if (s.Part.IsConnector || s.Piece >= 0) continue;
                bool sel = _selected != null && _selected.Part == s.Part;
                var hidden = new HashSet<int>(CorridorMap.Of(s.Part.Name).Where(c => !CorridorMap.Seen(c)).SelectMany(c => c.OwnDoors));
                foreach (Entity e in RoomEntities(s.Part.Name))
                {
                    if (e.Type != EntityType.Door || e is not Entity<DoorEntityData> door || hidden.Contains(e.EntityId)) continue;
                    Vector3 pos = PauseMap.DoorPoint(e, door.Data, s.Part.Name) + s.Part.Origin + s.Offset; // at its lock (ov8)
                    if (_doors.Any(d => (d.Pos - pos).LengthSquared < 0.01f)) continue;
                    Vector3 colour;
                    if (sel)
                    {
                        int state = _info.RoomState(s.Part.RoomId, e.EntityId);
                        bool coloured = state >= 1 || (state < 0 && door.Data.Locked != 0);
                        uint pal = door.Data.PaletteId;
                        colour = (!coloured ? DoorColour[0] : pal < 8 ? DoorColour[pal] : new Vector3(20)) / 31f;
                    }
                    else if (WalkedThrough(pos))
                    {
                        colour = OpenDoor;
                    }
                    else
                    {
                        continue;
                    }
                    _doors.Add((pos, PauseMap.Basis(e.FacingVector, e.UpVector), colour));
                }
            }
        }

        private bool WalkedThrough(Vector3 door)
        {
            const float Reach = 3;
            foreach (Shown c in _shown)
            {
                if (!c.Part.IsConnector) continue;
                Vector3 min = c.Part.Min + c.Offset - new Vector3(Reach), max = c.Part.Max + c.Offset + new Vector3(Reach);
                if (door.X >= min.X && door.X <= max.X && door.Y >= min.Y && door.Y <= max.Y && door.Z >= min.Z && door.Z <= max.Z) return true;
            }
            return false;
        }

        // the map's volume for the move clamp: the smallest circle round every shown part's box (flat), and their height
        private Vector2 _circleCentre;
        private float _circleRadius, _slabMin, _slabMax;

        private void ComputeBounds()
        {
            var points = new List<Vector2>();
            _slabMin = float.MaxValue;
            _slabMax = float.MinValue;
            foreach (Shown s in _shown)
            {
                Vector3 min = s.Part.Min + s.Offset, max = s.Part.Max + s.Offset;
                points.Add(new Vector2(min.X, min.Z));
                points.Add(new Vector2(min.X, max.Z));
                points.Add(new Vector2(max.X, min.Z));
                points.Add(new Vector2(max.X, max.Z));
                _slabMin = Math.Min(_slabMin, min.Y);
                _slabMax = Math.Max(_slabMax, max.Y);
            }
            if (points.Count == 0)
            {
                _circleCentre = Vector2.Zero;
                _circleRadius = 0;
                _slabMin = _slabMax = 0;
                return;
            }
            (_circleCentre, _circleRadius) = EnclosingCircle(points);
        }

        // Welzl's smallest enclosing circle, iteratively (the points in their given order are fine for a few hundred)
        private static (Vector2, float) EnclosingCircle(List<Vector2> p)
        {
            Vector2 c = p[0];
            float r = 0;
            bool Inside(Vector2 q) => (q - c).Length <= r + 1e-3f;
            for (int i = 1; i < p.Count; i++)
            {
                if (Inside(p[i])) continue;
                c = p[i];
                r = 0;
                for (int j = 0; j < i; j++)
                {
                    if (Inside(p[j])) continue;
                    c = (p[i] + p[j]) / 2;
                    r = (p[i] - c).Length;
                    for (int k = 0; k < j; k++)
                    {
                        if (Inside(p[k])) continue;
                        (c, r) = Circumcircle(p[i], p[j], p[k]);
                    }
                }
            }
            return (c, r);
        }

        private static (Vector2, float) Circumcircle(Vector2 a, Vector2 b, Vector2 c)
        {
            float d = 2 * (a.X * (b.Y - c.Y) + b.X * (c.Y - a.Y) + c.X * (a.Y - b.Y));
            if (MathF.Abs(d) < 1e-6f)
            {
                // in a line: the two farthest apart
                (Vector2 p, Vector2 q) = new[] { (a, b), (a, c), (b, c) }.OrderByDescending(t => (t.Item1 - t.Item2).LengthSquared).First();
                return ((p + q) / 2, (p - q).Length / 2);
            }
            float a2 = a.LengthSquared, b2 = b.LengthSquared, c2 = c.LengthSquared;
            var centre = new Vector2((a2 * (b.Y - c.Y) + b2 * (c.Y - a.Y) + c2 * (a.Y - b.Y)) / d,
                (a2 * (c.X - b.X) + b2 * (a.X - c.X) + c2 * (b.X - a.X)) / d);
            return (centre, (a - centre).Length);
        }

        private void SetTitle()
        {
            string t = "";
            if (_mode is Mode.Universe or Mode.ToUniverse || _pair < 0)
            {
                if (_planet >= 0) t = ShipNavigation.Planets[_planet].Name;
            }
            else if (_selected != null && RenamedTitle(_selected) is string renamed)
            {
                t = renamed; // a name the owner reported (PrimeMap.Report.cs)
            }
            else if (_selected?.PieceInfo != null)
            {
                t = _selected.PieceInfo.Name;
            }
            else if (_selected != null)
            {
                if (_selected == _playerRoom && _info.Title.Length > 0)
                {
                    t = _info.Title;
                }
                else
                {
                    RoomMetadata? meta = Metadata.GetRoomById(_selected.Part.RoomId, noThrow: true);
                    t = meta?.InGameName ?? meta?.Name ?? _selected.Part.Name;
                }
            }
            _title = t.ToUpperInvariant();
        }

        // ---------------------------------------------------------------- camera

        private Vector3 Eye => _focus + Back * _dist;

        private Vector3 Back
        {
            get
            {
                float a = MathHelper.DegreesToRadians(_yaw), e = MathHelper.DegreesToRadians(-_pitch);
                return new Vector3(MathF.Cos(e) * MathF.Sin(a), MathF.Sin(e), MathF.Cos(e) * MathF.Cos(a));
            }
        }

        private Vector3 Right
        {
            get
            {
                float a = MathHelper.DegreesToRadians(_yaw);
                return new Vector3(MathF.Cos(a), 0, -MathF.Sin(a));
            }
        }

        private Vector3 Up => Vector3.Cross(Back, Right);

        // view space: x right, y up, z back (the camera looks down -z)
        private struct Cam
        {
            public Vector3 Eye, X, Y, Z;
            public float F, Cx, Cy;
            public Vector3 View(Vector3 p)
            {
                Vector3 d = p - Eye;
                return new Vector3(Vector3.Dot(d, X), Vector3.Dot(d, Y), Vector3.Dot(d, Z));
            }
            public Vector2 Screen(Vector3 v) => new(Cx + v.X / -v.Z * F, Cy - v.Y / -v.Z * F);
        }

        private Cam Camera(float dist) => Camera(_focus, dist);

        private Cam Camera(Vector3 focus, float dist)
        {
            Vector3 back = Back;
            return new Cam
            {
                Eye = focus + back * dist, X = Right, Y = Vector3.Cross(back, Right), Z = back,
                F = 96 / MathF.Tan(MathHelper.DegreesToRadians(_fov) / 2), Cx = _w / 2, Cy = 96
            };
        }

        // ---------------------------------------------------------------- input (60 Hz)

        private static float Dead(float v) => MathF.Abs(v) < 0.2f ? 0 : (MathF.Abs(v) - 0.2f) / 0.8f * MathF.Sign(v);

        public void Tick(Input input)
        {
            if (_mode == Mode.Closed) return;
            const float Dt = 1 / 60f;
            _tick++;
            _time += Dt;
            Blur = Math.Clamp(Blur + (_mode == Mode.Closing ? -BlurRate : BlurRate) * Dt, 0, 1);
            // the panes slide at 2.0/s: the left one (legend) out in 1 and 2, the Y one and the bottom one out in 2
            float step = PaneRate * Dt;
            _leftPane = Math.Clamp(_leftPane + (_keyState >= 1 ? step : -step), 0, 1);
            _yPane = Math.Clamp(_yPane + (_keyState == 2 ? step : -step), 0, 1);
            _bottomPane = Math.Clamp(_bottomPane + (_keyState == 2 ? step : -step), 0, 1);
            _t += Dt;
            _toastTime = Math.Max(0, _toastTime - Dt);
            switch (_mode)
            {
                case Mode.Opening:
                {
                    float k = Math.Min(1, _t / OpenTime);
                    _dist = Lerp(_fromDist, _toDist, k);
                    _fov = Lerp(_fromFov, CamFov, k);
                    if (_pair >= 0) _areaAlpha = k;
                    else _universeAlpha = k;
                    if (k >= 1) _mode = _pair >= 0 ? Mode.Area : Mode.Universe;
                    break;
                }
                case Mode.Area:
                case Mode.Universe:
                    if (_menu != null || _typing) MenuTick(input);
                    else Interactive(input);
                    break;
                case Mode.ToUniverse:
                {
                    // the room map backs away and fades, the planets come in round the planet it belongs to
                    float k = Math.Min(1, _t / UniverseTime);
                    _areaAlpha = 1 - k;
                    _universeAlpha = k;
                    _dist = Lerp(_fromDist, _toDist, EaseOut(k));
                    _leaveDist = _leaveFrom * Lerp(1, LeaveZoom, EaseOut(k));
                    if (k >= 1)
                    {
                        _mode = Mode.Universe;
                        _areaAlpha = 0;
                    }
                    break;
                }
                case Mode.FromUniverse:
                {
                    float k = Math.Min(1, _t / UniverseTime);
                    _universeAlpha = 1 - k;
                    _areaAlpha = k;
                    _dist = Lerp(_fromDist, _toDist, EaseOut(k));
                    _leaveDist = _leaveFrom * Lerp(1, 1 / LeaveZoom, EaseOut(k)); // into the planet picked
                    if (k >= 1)
                    {
                        _mode = Mode.Area;
                        _universeAlpha = 0;
                    }
                    break;
                }
                case Mode.ClosePan:
                {
                    float k = Math.Min(1, _t / ClosePanTime);
                    _focus = Vector3.Lerp(_fromFocus, _toFocus, k);
                    Select(_playerRoom);
                    if (k >= 1) BeginClosing();
                    break;
                }
                case Mode.Closing:
                {
                    float k = Math.Min(1, _t / CloseTime);
                    _dist = Lerp(_fromDist, _toDist, EaseOut(k));
                    _fov = Lerp(_fromFov, MiniFov, EaseOut(k));
                    if (_pair >= 0 && _mode != Mode.Universe && _universeAlpha <= 0) _areaAlpha = 1 - k;
                    else _universeAlpha = 1 - k;
                    if (k >= 1) Close();
                    break;
                }
            }
        }

        private static float Lerp(float a, float b, float k) => a + (b - a) * k;
        private static float EaseOut(float t) => 1 - (1 - t) * (1 - t) * (1 - t);

        private void Interactive(Input input)
        {
            bool universe = _mode == Mode.Universe;
            if (input.Legend)
            {
                _keyState = (_keyState + 1) % 3;
            }
            if (input.ToggleLook && !universe)
            {
                SetDetailed(!Detailed);
                DetailedChanged?.Invoke(Detailed);
                BuildDoors();
            }
            if (input.Up && !universe && _pair >= 0) CycleSplits();
            if (input.Report && !universe && _selected != null)
            {
                OpenReport();
                return;
            }
            if (input.Close)
            {
                RequestClose();
                return;
            }
            if (input.World)
            {
                if (universe) OpenPlanet();
                else GoUniverse();
                return;
            }
            // rotate: the strongest of the four directions only
            float rx = Dead(input.RotateX), ry = Dead(input.RotateY);
            float up = Math.Max(0, ry), down = Math.Max(0, -ry), left = Math.Max(0, -rx), right = Math.Max(0, rx);
            float max = Math.Max(Math.Max(up, down), Math.Max(left, right));
            if (max > 0)
            {
                if (max == up) _pitch += RotatePerTick * up; // towards level
                else if (max == down) _pitch -= RotatePerTick * down; // towards top down
                else if (max == left) _yaw -= RotatePerTick * left;
                else _yaw += RotatePerTick * right;
                _pitch = Math.Clamp(_pitch, MinPitch, MaxPitch);
            }
            // zoom: R in, L out; the one held first keeps priority
            _zoomState = _zoomState switch
            {
                1 => input.ZoomIn ? 1 : input.ZoomOut ? 2 : 0,
                2 => input.ZoomOut ? 2 : input.ZoomIn ? 1 : 0,
                _ => input.ZoomIn ? 1 : input.ZoomOut ? 2 : 0
            };
            float zoom = ZoomPerTick * (universe ? UniverseZoom : 1);
            float minD = universe ? MinUniverseDist : MinCamDist, maxD = universe ? MaxUniverseDist : MaxCamDist;
            if (_zoomState == 1) _dist = Math.Max(minD, _dist - zoom);
            else if (_zoomState == 2) _dist = Math.Min(maxD, _dist + zoom);
            // move: along the screen's right and up, faster the farther out
            float mx = Dead(input.MoveX), my = Dead(input.MoveY);
            bool moving = mx != 0 || my != 0;
            if (moving)
            {
                _forced = null;
                _focus += (Right * mx + Up * my) * (MovePerTick * _dist / CamDist);
                Clamp(universe);
            }
            // the selection: nearest the line of sight through the point; with no move input the point slides onto it
            if (universe)
            {
                SelectPlanet(NearestPlanet());
                if (!moving && _planet >= 0) Recentre(PlanetPos(_planet));
            }
            else
            {
                if (_forced != null)
                {
                    Select(_forced);
                }
                else
                {
                    Select(NearestRoom());
                }
                if (!moving && _selected != null)
                {
                    Recentre(_selected.Centre);
                    if (_forced != null && (_focus - _forced.Centre).LengthSquared < 0.01f) _forced = null;
                }
            }
        }

        private void Recentre(Vector3 to)
        {
            Vector3 d = to - _focus;
            float len = d.Length;
            _focus = len <= RecentrePerTick ? to : _focus + d / len * RecentrePerTick;
        }

        private void Clamp(bool universe)
        {
            if (universe)
            {
                Vector3 c = UniverseCentre;
                float r = UniverseRadius;
                Vector3 d = _focus - c;
                if (d.Length > r) _focus = c + d.Normalized() * r;
                return;
            }
            // above or below the slab: back along the line of sight onto it; then into the circle
            Vector3 look = -Back;
            if ((_focus.Y > _slabMax || _focus.Y < _slabMin) && MathF.Abs(look.Y) > 1e-4f)
            {
                float bound = _focus.Y > _slabMax ? _slabMax : _slabMin;
                _focus -= look * ((_focus.Y - bound) / look.Y);
            }
            _focus.Y = Math.Clamp(_focus.Y, _slabMin, _slabMax);
            var flat = new Vector2(_focus.X, _focus.Z);
            Vector2 off = flat - _circleCentre;
            if (off.Length > _circleRadius && _circleRadius > 0)
            {
                flat = _circleCentre + off.Normalized() * _circleRadius;
                _focus.X = flat.X;
                _focus.Z = flat.Y;
            }
        }

        private float RayDistance(Vector3 p)
        {
            Vector3 look = -Back, d = p - _focus;
            return (d - look * Vector3.Dot(d, look)).LengthSquared;
        }

        private Shown? NearestRoom()
        {
            Shown? best = null;
            float bestD = float.MaxValue;
            foreach (Shown s in _shown)
            {
                if (s.Part.IsConnector) continue;
                // the selected room keeps it on a tie (rooms in line along the line of sight)
                float d = RayDistance(s.Centre) - (s == _selected ? 1 : 0);
                if (d < bestD)
                {
                    bestD = d;
                    best = s;
                }
            }
            return best;
        }

        private void Select(Shown? room)
        {
            if (room == null || room == _selected) return;
            _selected = room;
            SetTitle();
            BuildDoors();
        }

        // ---------------------------------------------------------------- the planet map

        private static Vector3 PlanetPos(int p) => new((PlanetAt[p].X - 165) * PlanetSpread, 0, (PlanetAt[p].Y - 61) * PlanetSpread);

        private Vector3 UniverseCentre => new(0, 0, 0);
        private float UniverseRadius => 70 * PlanetSpread;

        private int NearestPlanet()
        {
            int best = -1;
            float bestD = float.MaxValue;
            for (int p = 0; p < 5; p++)
            {
                if (!PlanetFound(p)) continue;
                float d = RayDistance(PlanetPos(p));
                if (d < bestD)
                {
                    bestD = d;
                    best = p;
                }
            }
            return best;
        }

        private void SelectPlanet(int p)
        {
            if (p == _planet) return;
            _planet = p;
            SetTitle();
        }

        // A on the room map: out to the planets, centred on this one (the room map's point carried into planet space)
        private void GoUniverse()
        {
            _planet = _pair >= 0 ? _pair / 2 : _ownPlanet;
            _forced = null;
            (_leaveFocus, _leaveFrom, _leaveDist) = (_focus, _dist, _dist);
            Begin(Mode.ToUniverse);
            _focus = PlanetPos(Math.Max(0, _planet));
            _fromDist = MinUniverseDist;
            _toDist = UniverseDist;
            _dist = _fromDist;
            SetTitle();
            PlaySound?.Invoke(OpenSound);
        }

        // A on the planet map: that planet's room map (only a planet with a map model and a room visited)
        private void OpenPlanet()
        {
            if (_planet < 0 || !AnyRoomVisited(_planet)) return;
            (_leaveFocus, _leaveFrom, _leaveDist) = (_focus, _dist, _dist);
            LoadPair(_planet * 2);
            _selected = null;
            Shown? start = _playerRoom;
            if (start == null)
            {
                Vector3 mid = new((_circleCentre.X), (_slabMin + _slabMax) / 2, _circleCentre.Y);
                start = _shown.Where(s => !s.Part.IsConnector).OrderBy(s => (s.Centre - mid).LengthSquared).FirstOrDefault();
            }
            _selected = start;
            BuildDoors();
            _focus = start?.Centre ?? Vector3.Zero;
            _fromDist = MaxCamDist;
            _toDist = CamDist;
            _dist = _fromDist;
            Begin(Mode.FromUniverse);
            SetTitle();
            PlaySound?.Invoke(OpenSound);
        }

        // ---------------------------------------------------------------- touch (canvas pixels: 0 down, 1 move, 2 up)

        public void Touch(float x, float y, int kind)
        {
            if (_mode is not (Mode.Area or Mode.Universe)) return;
            float ux = x / _scale, uy = y / _scale;
            if (MenuTouch(ux, uy, kind)) return;
            if (kind == 0)
            {
                _dragging = true;
                _downX = _lastX = ux;
                _downY = _lastY = uy;
                _travel = 0;
                return;
            }
            if (!_dragging) return;
            float dx = ux - _lastX, dy = uy - _lastY;
            _lastX = ux;
            _lastY = uy;
            _travel += MathF.Abs(dx) + MathF.Abs(dy);
            if (kind == 1)
            {
                // drag right = the right stick right, drag up = the stick up
                _yaw += dx * 0.6f;
                _pitch = Math.Clamp(_pitch - dy * 0.6f, MinPitch, MaxPitch);
                return;
            }
            _dragging = false;
            if (_travel > 4) return;
            if (Hit(_exitRect, ux, uy)) { RequestClose(); return; }
            if (Hit(_worldRect, ux, uy)) { if (_mode == Mode.Universe) OpenPlanet(); else GoUniverse(); return; }
            if (Hit(_legendRect, ux, uy)) { _keyState = (_keyState + 1) % 3; return; }
            // a tap on a room or a planet: select it, the view slides there
            Cam cam = Camera(_dist);
            if (_mode == Mode.Universe)
            {
                for (int p = 0; p < 5; p++)
                {
                    if (!PlanetFound(p)) continue;
                    Vector3 v = cam.View(PlanetPos(p));
                    if (v.Z >= -Near) continue;
                    Vector2 s = cam.Screen(v);
                    float r = PlanetSize / 2 / -v.Z * cam.F;
                    if ((s - new Vector2(ux, uy)).Length <= r)
                    {
                        _focus = PlanetPos(p) - (PlanetPos(p) - _focus) * 0.999f;
                        SelectPlanet(p);
                        if (_planet == p) _focus += (PlanetPos(p) - _focus) * 0.001f;
                        return;
                    }
                }
                return;
            }
            Shown? hit = PickAt(cam, new Vector2(ux, uy));
            if (hit != null) _forced = hit;
        }

        private static bool Hit((float X0, float Y0, float X1, float Y1) r, float x, float y) => x >= r.X0 && x <= r.X1 && y >= r.Y0 && y <= r.Y1;

        private Shown? PickAt(Cam cam, Vector2 at)
        {
            Shown? best = null;
            float bestZ = float.MinValue;
            foreach (Shown s in _shown)
            {
                if (s.Part.IsConnector) continue;
                MapGeometry g = s.Part.Nav;
                for (int pi = 0; pi < g.Polys.Count; pi++)
                {
                    if (!s.Draws(pi)) continue;
                    MapGeometry.Poly p = g.Polys[pi];
                    Vector3 a = cam.View(g.Points[g.Indices[p.Start]] + s.Offset);
                    for (int i = 1; i + 1 < p.Count; i++)
                    {
                        Vector3 b = cam.View(g.Points[g.Indices[p.Start + i]] + s.Offset);
                        Vector3 c = cam.View(g.Points[g.Indices[p.Start + i + 1]] + s.Offset);
                        if (a.Z >= -Near || b.Z >= -Near || c.Z >= -Near) continue;
                        if (!InsideTri(at, cam.Screen(a), cam.Screen(b), cam.Screen(c))) continue;
                        float z = (a.Z + b.Z + c.Z) / 3;
                        if (z > bestZ)
                        {
                            bestZ = z;
                            best = s;
                        }
                    }
                }
            }
            return best;
        }

        private static bool InsideTri(Vector2 p, Vector2 a, Vector2 b, Vector2 c)
        {
            static float Side(Vector2 p, Vector2 a, Vector2 b) => (p.X - b.X) * (a.Y - b.Y) - (a.X - b.X) * (p.Y - b.Y);
            float d1 = Side(p, a, b), d2 = Side(p, b, c), d3 = Side(p, c, a);
            bool neg = d1 < 0 || d2 < 0 || d3 < 0, pos = d1 > 0 || d2 > 0 || d3 > 0;
            return !(neg && pos);
        }

        // ---------------------------------------------------------------- drawing

        // what Build draws changes every tick while open (the pulses): the tick is the key
        public (int, int, int, bool) BuildKey(int width, int height) => (width, height, _tick, IsOpen);

        private (float, float, float, float) _exitRect, _worldRect, _legendRect;

        public void Build(int width, int height)
        {
            DrawList.Clear();
            Overlay.Clear();
            _out = DrawList;
            _scale = height / 192f;
            _w = width / _scale;
            if (!IsOpen) return;
            // the paused game, blurred (the App's copy of the frame)
            if (Blur > 0) DrawList.Quad(_blurTex, 0, 0, width, height, 0, 1, 1, 0, 1, 1, 1, Blur); // GL rows: bottom up
            if (_areaAlpha > 0 && _pair >= 0) DrawArea();
            if (_universeAlpha > 0) DrawUniverse();
            _out = Overlay;
            DrawPanes();
            DrawReport(PanelAlpha);
            _out = DrawList;
        }

        private float PanelAlpha => _mode switch
        {
            Mode.Opening => Math.Min(1, _t / OpenTime),
            Mode.Closing => 1 - Math.Min(1, _t / CloseTime),
            _ => 1
        };

        // a sortable piece of the map: a face (fill + its outline), a door, an icon
        private struct Item
        {
            public float Depth;
            public int Kind; // 0 face, 1 door, 2 icon
            public int Index;
        }

        private readonly List<Item> _items = new();
        private readonly List<(Shown S, MapGeometry G, int Poly)> _faces = new();
        private readonly List<(Vector3 Pos, int Tex, float Size, float Alpha)> _icons = new();
        private readonly Dictionary<MapGeometry, List<(int A, int B)>[]> _outlines = new();
        private readonly List<Vector3> _clip = new(), _clipOut = new();

        private void DrawArea()
        {
            bool leaving = _mode == Mode.ToUniverse;
            float dist = leaving ? _leaveDist : _dist;
            Cam cam = leaving ? Camera(_leaveFocus, dist) : Camera(_dist);
            float alpha = _areaAlpha;
            _items.Clear();
            _faces.Clear();
            _icons.Clear();
            Vector3 look = -Back;
            foreach (Shown s in _shown)
            {
                MapGeometry g = Detailed && s.Part.Detail != null ? s.Part.Detail : s.Part.Nav;
                for (int pi = 0; pi < g.Polys.Count; pi++)
                {
                    // the room less its pieces, each piece on its own (the DETAILED look isn't split: the room draws it)
                    if (g == s.Part.Nav ? !s.Draws(pi) : s.Piece >= 0) continue;
                    MapGeometry.Poly p = g.Polys[pi];
                    Vector3 c = Vector3.Zero;
                    for (int i = 0; i < p.Count; i++) c += g.Points[g.Indices[p.Start + i]];
                    c = c / p.Count + s.Offset;
                    _items.Add(new Item { Depth = cam.View(c).Z, Kind = 0, Index = _faces.Count });
                    _faces.Add((s, g, pi));
                }
            }
            for (int d = 0; d < _doors.Count; d++)
            {
                _items.Add(new Item { Depth = cam.View(_doors[d].Pos).Z, Kind = 1, Index = d });
            }
            float objectScale = dist / MinCamDist;
            foreach (Shown s in _shown)
            {
                foreach ((Vector3 pos, int frame) in s.Teleporters)
                {
                    _items.Add(new Item { Depth = cam.View(pos + s.Offset).Z, Kind = 2, Index = _icons.Count });
                    _icons.Add((pos + s.Offset, _others[frame], IconSize * objectScale, alpha));
                }
            }
            // back to front
            _items.Sort((a, b) => a.Depth.CompareTo(b.Depth));
            _door ??= PauseMap.SmallModel.Load("hud/Door_NAV_Model.bin", Log);
            float pulse = _time % PulsePeriod;
            pulse = pulse < PulsePeriod / 2 ? pulse / (PulsePeriod / 2) : (PulsePeriod - pulse) / (PulsePeriod / 2);
            var verts = new List<UiVertex>();
            foreach (Item item in _items)
            {
                if (item.Kind == 0)
                {
                    (Shown s, MapGeometry g, int pi) = _faces[item.Index];
                    bool sel = s == _selected && _mode != Mode.ToUniverse;
                    Vector4 surf = sel ? SurfSelect : SurfVisited with { W = AlphaSurface };
                    Vector4 line = sel ? OutlineSelect : OutlineVisited with { W = AlphaOutline };
                    if (s == _playerRoom)
                    {
                        surf = Vector4.Lerp(surf, FlashColour, pulse);
                        line = Vector4.Lerp(line, FlashColour, pulse);
                    }
                    surf.W *= alpha;
                    line.W *= alpha;
                    Face(verts, cam, s, g, pi, surf, line);
                }
                else if (item.Kind == 2)
                {
                    Flush(verts);
                    (Vector3 pos, int tex, float size, float a) = _icons[item.Index];
                    Billboard(cam, pos, tex, size, 1, a);
                }
            }
            Flush(verts);
            // the doors over the rooms, back to front (owner 2026-10-14: sorted in among the faces, a door on the far wall
            // of the selected room hid behind its near walls, nearly opaque, until the view turned; the mini map shows them)
            if (_door != null)
            {
                foreach (Item item in _items)
                {
                    if (item.Kind != 1) continue;
                    (Vector3 pos, Matrix3 rot, Vector3 colour) = _doors[item.Index];
                    Small(cam, _door, pos, rot, 1, colour, alpha);
                }
            }
            // Samus, over the map, on her own planet
            if (_pair == _ownPair && _playerRoom != null)
            {
                _marker ??= PauseMap.SmallModel.Load("hud/PlayerPos_NAV_Model.bin", Log);
                if (_marker != null)
                {
                    float beat = 0.5f * (1 + MathF.Sin(5 * _time - MathF.PI / 2));
                    float size = 1.25f * Math.Min(objectScale, MarkerCap) * (0.75f + 0.25f * beat);
                    Vector3 pos = _playerPos;
                    Small(cam, _marker, pos, PauseMap.Basis(_info.Facing, _info.Up), size, null, alpha);
                }
            }
        }

        private void Flush(List<UiVertex> verts)
        {
            if (verts.Count == 0) return;
            _out.Triangles(verts);
            verts.Clear();
        }

        // one face: its fill (shaded by how much its outside faces the camera) and its outline edges
        private void Face(List<UiVertex> verts, Cam cam, Shown s, MapGeometry g, int pi, Vector4 surf, Vector4 line)
        {
            MapGeometry.Poly p = g.Polys[pi];
            _clip.Clear();
            for (int i = 0; i < p.Count; i++) _clip.Add(cam.View(g.Points[g.Indices[p.Start + i]] + s.Offset));
            // the outside: a map model's winding gives it (the rooms are modelled as interiors, DS front faces inward);
            // a collision face's normal points into the room
            Vector3 outward;
            if (g.IsCollision)
            {
                outward = -(cam.X * 0 + new Vector3(Vector3.Dot(p.Normal, cam.X), Vector3.Dot(p.Normal, cam.Y), Vector3.Dot(p.Normal, cam.Z)));
            }
            else
            {
                outward = p.Count >= 3 ? Vector3.Cross(_clip[1] - _clip[0], _clip[2] - _clip[0]) : Vector3.UnitZ;
            }
            float shade = ShadeConstant;
            if (outward.LengthSquared > 1e-12f) shade += ShadeLinear * Math.Max(0, outward.Normalized().Z);
            Vector4 fill = new(surf.X * shade, surf.Y * shade, surf.Z * shade, surf.W);
            if (fill.W > 0.004f)
            {
                ClipNear(_clip, _clipOut);
                if (_clipOut.Count >= 3)
                {
                    Vector2 a = cam.Screen(_clipOut[0]);
                    for (int i = 1; i + 1 < _clipOut.Count; i++)
                    {
                        verts.Add(V(a, fill));
                        verts.Add(V(cam.Screen(_clipOut[i]), fill));
                        verts.Add(V(cam.Screen(_clipOut[i + 1]), fill));
                    }
                }
            }
            if (line.W <= 0.004f) return;
            foreach ((int ia, int ib) in Outline(g)[pi])
            {
                Vector3 va = cam.View(g.Points[ia] + s.Offset), vb = cam.View(g.Points[ib] + s.Offset);
                if (va.Z >= -Near && vb.Z >= -Near) continue;
                if (va.Z >= -Near) va = Vector3.Lerp(vb, va, (vb.Z + Near) / (vb.Z - va.Z));
                else if (vb.Z >= -Near) vb = Vector3.Lerp(va, vb, (va.Z + Near) / (va.Z - vb.Z));
                Line(verts, cam.Screen(va), cam.Screen(vb), LineWidth, line);
            }
        }

        // the polygon clipped to the near plane (z <= -Near)
        private static void ClipNear(List<Vector3> poly, List<Vector3> output)
        {
            output.Clear();
            for (int i = 0; i < poly.Count; i++)
            {
                Vector3 a = poly[i], b = poly[(i + 1) % poly.Count];
                bool ina = a.Z <= -Near, inb = b.Z <= -Near;
                if (ina) output.Add(a);
                if (ina != inb) output.Add(Vector3.Lerp(a, b, (a.Z + Near) / (a.Z - b.Z)));
            }
        }

        // each face's outline edges (point indices): its edges no face of the same plane shares (so a quad's diagonal
        // isn't drawn); for the collision look, its creases
        private List<(int A, int B)>[] Outline(MapGeometry g)
        {
            if (_outlines.TryGetValue(g, out List<(int, int)>[]? list)) return list;
            list = new List<(int, int)>[g.Polys.Count];
            for (int i = 0; i < list.Length; i++) list[i] = new List<(int, int)>();
            if (g.IsCollision)
            {
                foreach ((int a, int b, int poly) in g.Creases) list[poly].Add((a, b));
            }
            else
            {
                static (long, long) Key(Vector3 a, Vector3 b)
                {
                    long Ka(Vector3 v) => ((long)MathF.Round(v.X * 64) * 73856093) ^ ((long)MathF.Round(v.Y * 64) * 19349663) ^ ((long)MathF.Round(v.Z * 64) * 83492791);
                    long x = Ka(a), y = Ka(b);
                    return x < y ? (x, y) : (y, x);
                }
                var normals = new Vector3[g.Polys.Count];
                var edges = new Dictionary<(long, long), List<int>>();
                for (int pi = 0; pi < g.Polys.Count; pi++)
                {
                    MapGeometry.Poly p = g.Polys[pi];
                    if (p.Count >= 3)
                    {
                        Vector3 a = g.Points[g.Indices[p.Start]];
                        Vector3 n = Vector3.Cross(g.Points[g.Indices[p.Start + 1]] - a, g.Points[g.Indices[p.Start + 2]] - a);
                        normals[pi] = n.LengthSquared > 1e-12f ? n.Normalized() : Vector3.Zero;
                    }
                    for (int i = 0; i < p.Count; i++)
                    {
                        var k = Key(g.Points[g.Indices[p.Start + i]], g.Points[g.Indices[p.Start + (i + 1) % p.Count]]);
                        if (!edges.TryGetValue(k, out List<int>? owners)) edges[k] = owners = new List<int>();
                        owners.Add(pi);
                    }
                }
                for (int pi = 0; pi < g.Polys.Count; pi++)
                {
                    MapGeometry.Poly p = g.Polys[pi];
                    for (int i = 0; i < p.Count; i++)
                    {
                        int ia = g.Indices[p.Start + i], ib = g.Indices[p.Start + (i + 1) % p.Count];
                        List<int> owners = edges[Key(g.Points[ia], g.Points[ib])];
                        bool flat = owners.Any(q => q != pi && Vector3.Dot(normals[q], normals[pi]) > 0.99f);
                        if (!flat) list[pi].Add((ia, ib));
                    }
                }
            }
            _outlines[g] = list;
            return list;
        }

        // a small MPH model (door, marker), opaque, its own faces back to front
        private readonly List<(float Z, Vector2 A, Vector2 B, Vector2 C, Vector4 Col)> _smallTris = new();

        private void Small(Cam cam, PauseMap.SmallModel model, Vector3 pos, Matrix3 rot, float size, Vector3? colour, float alpha)
        {
            _smallTris.Clear();
            for (int t = 0; t + 2 < model.Corners.Count; t += 3)
            {
                Vector3 W(Vector3 p) => cam.View(pos + (p * size) * rot);
                Vector3 a = W(model.Corners[t]), b = W(model.Corners[t + 1]), d = W(model.Corners[t + 2]);
                if (a.Z >= -Near || b.Z >= -Near || d.Z >= -Near) continue;
                Vector3 n = Vector3.Cross(b - a, d - a);
                if (n.LengthSquared < 1e-12f) continue;
                n.Normalize();
                Material mat = model.Model.Materials[model.Materials[t / 3]];
                if (mat.Culling == CullingMode.Back && n.Z <= 0) continue; // n.Z > 0 faces the camera (see PauseMap.DrawSmall)
                Vector3 col = (colour ?? model.Colours[t / 3]) * (0.8f + 0.2f * MathF.Abs(n.Z));
                _smallTris.Add(((a.Z + b.Z + d.Z) / 3, cam.Screen(a), cam.Screen(b), cam.Screen(d), new Vector4(col, alpha)));
            }
            _smallTris.Sort((p, q) => p.Z.CompareTo(q.Z));
            var verts = new List<UiVertex>(_smallTris.Count * 3);
            foreach (var t in _smallTris)
            {
                verts.Add(V(t.A, t.Col));
                verts.Add(V(t.B, t.Col));
                verts.Add(V(t.C, t.Col));
            }
            _out.Triangles(verts);
        }

        // a camera-facing sprite, size in map units
        private void Billboard(Cam cam, Vector3 pos, int tex, float size, float bright, float alpha)
        {
            Vector3 v = cam.View(pos);
            if (v.Z >= -Near) return;
            Vector2 s = cam.Screen(v);
            float h = size / 2 / -v.Z * cam.F;
            Rect(tex, s.X - h, s.Y - h, s.X + h, s.Y + h, bright, bright, bright, alpha);
        }

        // the planet map's sky: fixed stars round the system (universe units), drawn as points
        private static readonly Vector3[] Stars = MakeStars();

        private static Vector3[] MakeStars()
        {
            var rng = new Random(0x5A3);
            var stars = new Vector3[400];
            for (int i = 0; i < stars.Length; i++)
            {
                // on a big sphere round the system
                float u = (float)rng.NextDouble() * 2 - 1, t = (float)rng.NextDouble() * MathF.Tau;
                float rr = MathF.Sqrt(1 - u * u);
                stars[i] = new Vector3(rr * MathF.Cos(t), u, rr * MathF.Sin(t)) * 6000;
            }
            return stars;
        }

        private void DrawUniverse()
        {
            bool leaving = _mode == Mode.FromUniverse;
            Vector3 focus = leaving ? _leaveFocus : _focus;
            Cam cam = Camera(focus, leaving ? _leaveDist : _dist);
            var sky = new List<UiVertex>();
            for (int i = 0; i < Stars.Length; i++)
            {
                Vector3 v = cam.View(Stars[i] + focus);
                if (v.Z >= -Near) continue;
                Vector2 at = cam.Screen(v);
                if (at.X < -2 || at.X > _w + 2 || at.Y < -2 || at.Y > 194) continue;
                float b = 0.35f + 0.65f * ((i * 37) % 11) / 10f, size = 0.35f + 0.25f * ((i * 13) % 3);
                Vector4 c = new(0.75f * b, 0.85f * b, b, _universeAlpha);
                Line(sky, at - new Vector2(size / 2, 0), at + new Vector2(size / 2, 0), size, c);
            }
            if (sky.Count > 0) _out.Triangles(sky);
            var order = Enumerable.Range(0, 5).Where(PlanetFound).OrderBy(p => cam.View(PlanetPos(p)).Z).ToList();
            float blink = 0.5f + 0.5f * MathF.Cos(_time * 2 * MathF.PI / PulsePeriod);
            foreach (int p in order)
            {
                bool sel = p == _planet;
                float a = _universeAlpha * (p == _ownPlanet ? 0.55f + 0.45f * blink : 1);
                Billboard(cam, PlanetPos(p), _planetTex[p], PlanetSize, sel ? 1 : 0.55f, a);
                if (sel)
                {
                    // Prime's select colour round the chosen planet
                    Vector3 v = cam.View(PlanetPos(p));
                    if (v.Z < -Near)
                    {
                        Vector2 s = cam.Screen(v);
                        float r = PlanetSize * 0.62f / -v.Z * cam.F;
                        var verts = new List<UiVertex>();
                        Vector4 c = OutlineSelect with { W = _universeAlpha };
                        for (int i = 0; i < 48; i++)
                        {
                            float a0 = i / 48f * MathF.Tau, a1 = (i + 1) / 48f * MathF.Tau;
                            Line(verts, s + r * new Vector2(MathF.Cos(a0), MathF.Sin(a0)), s + r * new Vector2(MathF.Cos(a1), MathF.Sin(a1)), 0.8f, c);
                        }
                        _out.Triangles(verts);
                    }
                }
            }
        }

        // ---------------------------------------------------------------- panes (Prime's FRME MapScreen, MPH font)

        // text sizes (1 = MPH's 8-pixel font at DS scale): Prime's map text is small next to the map
        private const float TitleK = 0.8f, TextK = 0.6f, LegendK = 0.55f;
        // inside the helmet: below its top bar, in from the sides, the Y pane clear of its bottom-left corner
        private const float Top = 18, Side = 14, YPaneX = 44;

        private void DrawPanes()
        {
            float a = PanelAlpha;
            if (a <= 0) return;
            // the area (or planet) name, top middle
            // rows kept inside the helmet's edges (its top bar and bottom corners stay over the map)
            if (_title.Length > 0) Text(_title, _w / 2, Top, 2, TitleK, LabelColour, a);
            bool universe = _mode is Mode.Universe or Mode.ToUniverse || _pair < 0;
            // the right pane: A to the planet map / back to the room map (Prime 0x2c "World Map" / 0x2d "Level Map"; owner
            // 2026-10-14: "system map", the planets being one star system)
            string world = universe ? "LEVEL MAP" : "SYSTEM MAP";
            bool worldOn = !universe || (_planet >= 0 && AnyRoomVisited(_planet));
            float rw = KeyLabelWidth("A", world);
            _worldRect = (_w - Side - rw, Top - 3, _w - Side, Top + 7);
            if (worldOn) KeyLabel("A", world, _w - Side - rw, Top + 1, a);
            // the planet's slots (MPH: octolith / portal + artifacts), under it
            if (!universe) DrawSlots(_w - Side - 12, Top + 26, a);
            // the left pane: exit + the legend; slides out left in states 1 and 2
            float lx = Side - 120 * EaseInOut(_leftPane);
            _exitRect = (lx, Top - 3, lx + 40, Top + 7);
            if (lx > -110)
            {
                KeyLabel("B", "EXIT", lx, Top + 1, a);
                if (!universe) DrawLegend(lx, Top + 16, a);
            }
            // the Y pane (Prime 0x2b "Key/Legend") and the bottom pane (0x2e-0x30), down out of sight in state 2
            float yy = 192 - 24 + 34 * EaseInOut(_yPane);
            _legendRect = (YPaneX, yy - 3, YPaneX + KeyLabelWidth("Y", "KEY/LEGEND"), yy + 7);
            KeyLabel("Y", "KEY/LEGEND", YPaneX, yy, a);
            if (!universe)
            {
                // the split tools (PrimeMap.Report.cs)
                float hx = YPaneX + KeyLabelWidth("Y", "KEY/LEGEND") + 12;
                string splits = "SPLITS " + SplitsState;
                KeyLabel("D-UP", splits, hx, yy, a);
                KeyLabel("L3/R3", "REPORT", hx + KeyLabelWidth("D-UP", splits) + 12, yy, a);
            }
            float by = 192 - 9 + 20 * EaseInOut(_bottomPane);
            string[] keys = { "R STICK", "L STICK", "L/R" };
            string[] labels = { "ROTATE", "MOVE", "ZOOM" };
            const float Gap = 16;
            float total = 0;
            for (int i = 0; i < 3; i++) total += KeyLabelWidth(keys[i], labels[i]) + (i < 2 ? Gap : 0);
            float x = (_w - total) / 2;
            for (int i = 0; i < 3; i++)
            {
                KeyLabel(keys[i], labels[i], x, by, a);
                x += KeyLabelWidth(keys[i], labels[i]) + Gap;
            }
        }

        private static float EaseInOut(float t) => t * t * (3 - 2 * t);

        private float KeyLabelWidth(string key, string label) => (_font.Measure(key) + 5 + _font.Measure(label)) * TextK;

        private void KeyLabel(string key, string label, float x, float y, float a)
        {
            Text(key, x, y, 0, TextK, KeyColour, a);
            Text(label, x + (_font.Measure(key) + 5) * TextK, y, 0, TextK, LabelColour, a);
        }

        // MPH's legend as Prime's list: beam doors (names, or ??? until owned), the open-door green, the others
        private void DrawLegend(float x, float top, float a)
        {
            const float Pitch = 6.5f, Box = 5;
            float tx = x + Box + 3;
            float y = top;
            foreach ((int name, int frame) in BeamRows)
            {
                string text = (_info.Weapons & (1 << (name - 1))) != 0 ? Hud('B', name).ToUpperInvariant() : "???";
                Rect(_doorKeys[frame], x, y, x + Box, y + Box, 1, 1, 1, a);
                Text(text, tx, y, 0, LegendK, LabelColour, a);
                y += Pitch;
            }
            Rect(-1, x + 0.5f, y + 0.5f, x + Box - 0.5f, y + Box - 0.5f, OpenDoor.X, OpenDoor.Y, OpenDoor.Z, a);
            Text("OPENED", tx, y, 0, LegendK, LabelColour, a);
            y += Pitch;
            foreach ((int name, bool others, int frame) in OtherRows)
            {
                if (others) Rect(_others[frame], x - 2.5f, y - 2.5f, x + Box + 2.5f, y + Box + 2.5f, 1, 1, 1, a);
                else Rect(_doorKeys[frame], x, y, x + Box, y + Box, 1, 1, 1, a);
                Text(Hud('M', name).ToUpperInvariant(), tx, y, 0, LegendK, LabelColour, a);
                y += Pitch;
            }
        }

        // the shown planet's two area slots (PauseMap's side columns): octolith, a lost one + its hunter, or the portal
        // with its artifacts
        private void DrawSlots(float cx, float cy, float a)
        {
            if (_pair < 0) return;
            int crystalImage = (_tick % 36) switch { < 30 => 0, < 32 => 1, < 34 => 2, _ => 1 };
            bool redOn = (_tick / 10 & 1) == 0;
            for (int h = 0; h < 2; h++)
            {
                int slot = _pair + h;
                float x = cx, y = cy + h * 34 * SlotK;
                int lost = (int)(_info.LostOctoliths >> (4 * slot) & 0xF);
                if ((_info.Octoliths & (1 << slot)) != 0)
                {
                    Centred(_crystal[crystalImage], x, y, 16, 32, a);
                }
                else if (lost < 8)
                {
                    if (redOn) Centred(_red[0], x + 6 * SlotK, y, 16, 32, a);
                    if (lost < 7) Centred(_hunterFaces[lost], x, y, 32, 32, a);
                }
                else
                {
                    bool all = true;
                    for (int k = 0; k < 3; k++)
                    {
                        if ((_info.Artifacts & (1u << (3 * slot + k))) == 0) all = false;
                    }
                    Centred(_portal[all ? 1 : 0], x, y, 32, 32, a);
                    for (int k = 0; k < 3; k++)
                    {
                        if ((_info.Artifacts & (1u << (3 * slot + k))) != 0) Centred(_art[slot], x + ArtifactAt[k].X * SlotK, y + ArtifactAt[k].Y * SlotK, 8, 8, a);
                    }
                }
            }
        }

        private const float SlotK = 0.6f; // the slot sprites next to Prime's small text

        private void Centred(int tex, float cx, float cy, int w, int h, float a) => Rect(tex, cx - w * SlotK / 2, cy - h * SlotK / 2, cx + w * SlotK / 2, cy + h * SlotK / 2, 1, 1, 1, a);

        // ---------------------------------------------------------------- primitives (canvas units: 192 high)

        private UiVertex V(Vector2 p, Vector4 c) => new(p.X * _scale, p.Y * _scale, 0, 0, c.X, c.Y, c.Z, c.W);

        private void Rect(int tex, float x0, float y0, float x1, float y1, float r, float g, float b, float a)
        {
            _out.Quad(tex, x0 * _scale, y0 * _scale, x1 * _scale, y1 * _scale, 0, 0, 1, 1, r, g, b, a);
        }

        private void Line(List<UiVertex> verts, Vector2 a, Vector2 b, float width, Vector4 c)
        {
            Vector2 d = b - a;
            if (d.LengthSquared < 1e-8f) return;
            Vector2 n = new Vector2(-d.Y, d.X).Normalized() * (width / 2);
            verts.Add(V(a - n, c));
            verts.Add(V(b - n, c));
            verts.Add(V(b + n, c));
            verts.Add(V(a - n, c));
            verts.Add(V(b + n, c));
            verts.Add(V(a + n, c));
        }

        // text at (x, top) in canvas units, align 0 left / 1 right / 2 centred, k = size (1 = DS pixels), a soft shadow
        private void Text(string text, float x, float top, int align, float k, (float R, float G, float B) colour, float alpha)
        {
            if (string.IsNullOrEmpty(text)) return;
            for (int pass = 0; pass < 2; pass++)
            {
                _glyphs.Clear();
                const float LineHeight = 8;
                (float r, float g, float b) = pass == 0 ? (0f, 0f, 0f) : colour;
                _font.Emit(text, 0, 192 - LineHeight, align, 1000, LineHeight, r, g, b, alpha * (pass == 0 ? 0.6f : 1), 0, _glyphs);
                float ox = pass == 0 ? 0.6f : 0, oy = pass == 0 ? 0.6f : 0;
                UiVertex M(UiVertex v) => new((x + ox + v.X * k) * _scale, (top + oy - v.Y * k) * _scale, v.U, v.V, v.R, v.G, v.B, v.A);
                foreach (WidgetTri t in _glyphs)
                {
                    _out.Triangle(t.TextureId, UiWrap.Clamp, UiWrap.Clamp, UiBlend.Alpha, M(t.A), M(t.B), M(t.C));
                }
            }
        }
    }
}
