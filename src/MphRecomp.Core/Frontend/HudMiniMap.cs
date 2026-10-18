using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MphRead;
using MphRead.Entities;
using MphRead.Formats;
using OpenTK.Mathematics;

// The Prime-style HUD's mini map (owner 2026-10-14: "the mini map part should just have the 3d room floating that exists
// in the select screen map viewer"; "find precisely how the mini map works" in Prime 1; "the same indicator showing the
// player the map screen does"; "copy the rest over"). The shapes are MPH's own: the pause map's area model parts (the
// ROM's hud/unitN_MNAV_Model.bin), near walls culled by material + winding as PauseMap.Build3D does, the outline on the
// edges one triangle owns, and Samus as the pause map's PlayerPos_NAV_Model marker. Everything else is Metroid Prime 1's
// mini map (CAutoMapper's mini map state, PrimeDecomp/prime + metaforce; values read from the disc's Tweaks.pak AutoMapper
// tweak):
// - orthographic, turned with the camera's yaw (not the body's), tilted down MiniCamXAngle = 45 degrees
// - centred on the map centre of the area Samus is in, not on her (owner 2026-10-14: "the minimap is always centered on
//   the room, not samus, and the samus marker moves through it"), framed so the area fits: half
//   height = MiniMapCamDistScale 0.85 x (0.25 |bounds diagonal| + 0.5 |farthest corner from the centre|); on an area
//   change the view glides to the new one over HintPanTime = 0.5 s. Only the area Samus is in sets the frame (the disc's
//   ShowOneMiniMapArea = 1; CAutoMapper re-targets only when the current area changes), so the small connector parts at
//   the doors never re-frame it (owner 2026-10-14: it zoomed in and out at every door piece)
// - a room's door-bounded pieces (CorridorMap: "Corridor A", "Chamber A", ...) are areas of their own, as Prime's halls:
//   in one, the frame is the piece; back in the room, the room without its pieces; a piece shows once she's been in it,
//   and its own doors and the connectors past them with it (owner 2026-10-14: "so that it doesnt show until you enter")
// - draw depth 2 (ShowOneMiniMapArea): the room plus the rooms one door away; MPH's doors are its connector parts, so the
//   connectors touching the room and the rooms past them, each shown by the pause map's own vanilla rule (a room once
//   visited, a connector once the rooms on both sides are). MPH's map only knows rooms you've been in, so Prime's blue
//   "unvisited" look never comes up
// - the visited look for every shown room: surface (0.957, 0.408, 0.020) at alpha 0.6, lit 0.4 + 0.4 x the face's turn
//   to the camera (MapSurfaceNormColorConstant / Linear); outline (1.0, 0.529, 0.024) at alpha 0.4, 1 pixel
// - doors as the map screen (PrimeMap.BuildDoors) draws them, hud/Door_NAV_Model.bin: the room's own in their beam colour
//   (grey until known: room state >= 1, or locked while unset), the shown rooms' around it green (Prime's open-door
//   colour) where a shown connector was walked through, else hidden (owner 2026-10-14: "the mini map needs to show doors")
// - the player marker at the player, turned with the camera's yaw, pulsing in size: since the map turns with the camera
//   too, on screen it always points up, so PlayerPrimeHud draws it as a sprite (PaintMarker) at the spot Paint gives
// MphRead's PlayerPrimeHud.cs asks for these through PlayerEntity.HostPrimeMap / HostPrimeMarker and fades the map's
// edge; this paints on the CPU so the PC tools record exactly what the device draws. Rooms without a map model
// (multiplayer arenas, the Oubliette, First Hunt) return false and PlayerPrimeHud keeps its collision floor picture.
namespace MphRecomp.Frontend
{
    public static class HudMiniMap
    {
        private const float PitchDegrees = 45; // Prime 1 MiniCamXAngle (-45)
        private const float FrameScale = 0.85f; // Prime 1 MiniMapCamDistScale
        private const float FrameMargin = 1.15f; // PlayerPrimeHud fades the outer tenth: keep the framed room inside it
        private const float MinHalf = 12; // map units: a small room isn't blown up past this
        private const float GlideSeconds = 0.5f; // Prime 1 HintPanTime
        private const int Super = 1; // samples per texel side: the 192 texture is ~1 texel per screen pixel
        private static readonly Vector3 Surface = new Vector3(0.957f, 0.408f, 0.020f); // Prime 1 SurfaceVisitedColor
        private const float SurfaceAlpha = 0.6f; // MiniAlphaSurfaceVisited
        private static readonly Vector3 Outline = new Vector3(1.0f, 0.529f, 0.024f); // OutlineVisitedColor
        private const float OutlineAlpha = 0.4f; // MiniAlphaOutlineVisited
        // the marker's size: its widest corner this share of the framing's half height (Prime sizes its marker by the
        // camera distance, so it keeps one size against the room's frame)
        private const float MarkerShare = 0.17f;
        private const float MarkerMargin = 1.05f; // the marker sprite's half width over its widest corner
        // MPH's beam door colours (ov8 0x2133150, 5-bit, as PrimeMap), > 7: grey 20; the open-door green (PrimeMap)
        private static readonly Vector3[] DoorColour =
        {
            new(28, 28, 28), new(31, 31, 0), new(30, 18, 10), new(0, 31, 0), new(31, 0, 0), new(20, 9, 31), new(31, 16, 0), new(0, 16, 31)
        };
        private static readonly Vector3 OpenDoor = new(0.016f, 0.725f, 0.075f);

        private sealed class Room
        {
            public AreaMapModel Model = null!;
            public AreaMapModel.Part Part = null!;
            public float Half; // the framing's half height, map units
            public Dictionary<AreaMapModel.Part, float> ConnectorHalf = new();
            // draw depth 2: the connectors touching the room (with the rooms each touches) and the rooms past them
            public List<(AreaMapModel.Part Connector, List<AreaMapModel.Part> Sides)> Connectors = new();
            public List<AreaMapModel.Part> Near = new();
        }

        private static string _root = "";
        private static readonly List<AreaMapModel> _models = new();
        private static readonly Dictionary<string, Room?> _rooms = new(StringComparer.OrdinalIgnoreCase);
        // an area's map model loading on a worker (Find starts it; ~13 ms on the PC, several frames' worth on the Odin, at
        // the first room of an area): the map shows nothing until it's in
        private static readonly Dictionary<string, Task<AreaMapModel?>> _loading = new(StringComparer.OrdinalIgnoreCase);
        private static float[] _buf = Array.Empty<float>();
        private static readonly List<(Vector2 A, Vector2 B, Vector2 C, float Depth, Vector3 Colour, float Alpha)> _tris = new();
        private static readonly Dictionary<(Vector2, Vector2), int> _edges = new();
        private static readonly List<(AreaMapModel.Part? Part, int Door, float Depth)> _draw = new();
        // a room's doors (entity type 3) from its entity file, by room name + boss layer
        private sealed record DoorInfo(Vector3 Position, Matrix3 Rot, int EntityId, uint Palette, bool Locked);
        private static readonly Dictionary<(string, int), List<DoorInfo>> _roomDoors = new();
        private static readonly List<(Vector3 Pos, Matrix3 Rot, Vector3 Colour)> _doors = new();
        private static PauseMap.SmallModel? _door;
        private static bool _doorTried;
        // the view last painted (the player is placed with it between paints) and the glide between areas
        private static object? _shown; // the framed area: a room part or a CorridorMap piece
        private static AreaMapModel? _shownModel;
        private static AreaMapModel.Part? _inside; // the connector (or room) she's standing in
        private static CorridorMap.Piece? _current; // the room's piece she's in
        private static int _seenVersion = -1;
        private static readonly Dictionary<object, (Vector3 Centre, float Half)> _frames = new();
        private static readonly bool[] _showPiece = new bool[16];
        private static bool _repaintDue;
        private static Vector3 _viewCentre, _glideFrom;
        private static float _viewHalf, _glideFromHalf, _glideStart = -1;
        private static Matrix4 _view;
        private static float _viewK, _viewC;

        // CampaignRenderer.SetPlayerHooks and the PC tools: the game's file system root (Paths.FileSystem)
        public static void Install(string fileSystemRoot)
        {
            if (_root != fileSystemRoot)
            {
                _root = fileSystemRoot;
                _models.Clear();
                _rooms.Clear();
                _loading.Clear();
                _roomDoors.Clear();
                _frames.Clear();
                _shown = null;
                _shownModel = null;
            }
            PlayerEntity.HostPrimeMap = Paint;
            PlayerEntity.HostMapPieceSeen = MarkPiece;
            PlayerEntity.HostPrimeMapWarm = Warm;
            PlayerEntity.HostPrimeMarker = PaintMarker;
        }

        // PlayerEntity.HostMapPieceSeen, every frame whatever the HUD: the door-bounded piece she's in is seen from now on
        // (Paint does the same when the mini map is on)
        public static void MarkPiece(string name, string? roomPart, Vector3 position)
        {
            if (Find(name) is Room room && CorridorMap.At(room.Part.Name, roomPart, position + room.Part.Origin) is CorridorMap.Piece piece)
            {
                CorridorMap.MarkSeen(piece);
            }
        }

        private static Room? Find(string name) => Find(name, out _);

        // PlayerEntity.HostPrimeMapWarm, a step at a time before the mini map first shows (the landing movie, the intro
        // camera): the room's map, framing, pieces, doors and models made ahead, so the first paint is only the drawing.
        // step: 0 at the start, moved on here; true = done (the same step again while the area's model loads)
        public static bool Warm(string name, ref int step)
        {
            if (Find(name, out bool pending) is not Room room)
            {
                return !pending;
            }
            switch (step++)
            {
            case 0:
                FrameOf(room, null);
                foreach (CorridorMap.Piece piece in CorridorMap.Of(room.Part.Name))
                {
                    FrameOf(room, piece);
                }
                if (CorridorMap.Of(room.Part.Name).Length > 0)
                {
                    CorridorMap.PolyPieces(room.Model, room.Part);
                }
                return false;
            case 1:
                LoadDoor();
                LoadMarker();
                DoorsOf(room.Part.Name, DoorLayer(room));
                return false;
            default:
                foreach (AreaMapModel.Part near in room.Near)
                {
                    DoorsOf(near.Name, DoorLayer(room));
                }
                return true;
            }
        }

        // pending = its area's map model is still loading (null till then)
        private static Room? Find(string name, out bool pending)
        {
            pending = false;
            if (_rooms.TryGetValue(name, out Room? known))
            {
                return known;
            }
            Room? found = null;
            try
            {
                AreaMapModel? model = _models.FirstOrDefault(m => PartOf(m, name) != null);
                if (model == null)
                {
                    if (!_loading.TryGetValue(name, out Task<AreaMapModel?>? load))
                    {
                        string root = _root;
                        _loading[name] = load = Task.Run(() => AreaMapModel.ForRoom(root, name));
                    }
                    if (!load.IsCompleted)
                    {
                        pending = true;
                        return null;
                    }
                    _loading.Remove(name);
                    if (load.IsCompletedSuccessfully && (model = load.Result) != null)
                    {
                        _models.Add(model);
                    }
                }
                else
                {
                    _loading.Remove(name);
                }
                AreaMapModel.Part? part = model == null ? null : PartOf(model, name);
                if (model != null && part != null && part.Nav.Points.Count > 0)
                {
                    found = new Room { Model = model, Part = part };
                    var rooms = model.Parts.Where(p => !p.IsConnector).ToList();
                    foreach (AreaMapModel.Part con in model.Parts.Where(p => p.IsConnector && PauseMap.Touches(p, part)))
                    {
                        var sides = rooms.Where(r => PauseMap.Touches(con, r)).ToList();
                        found.Connectors.Add((con, sides));
                        found.ConnectorHalf[con] = FrameHalf(con);
                        found.Near.AddRange(sides.Where(r => r != part && !found.Near.Contains(r)));
                    }
                    found.Half = FrameHalf(part);
                }
            }
            catch (Exception)
            {
                found = null; // no map models in this import: the floor picture stays
            }
            _rooms[name] = found;
            return found;
        }

        // the PC tools: the room's map part and the connectors around it, bounds in the map's space, and its origin
        public static string Describe(string name)
        {
            if (Find(name) is not Room room)
            {
                return $"{name}: no map part";
            }
            string Box(AreaMapModel.Part p) => $"{p.Name} min {p.Min} max {p.Max} centre {p.Centre}";
            return $"{Box(room.Part)} origin {room.Part.Origin} half {room.Half}\n"
                + string.Join("\n", room.Connectors.Select(c => "  con " + Box(c.Connector)
                    + " sides " + string.Join(",", c.Sides.Select(s => s.Name))));
        }

        // Prime's framing: the area's bounds about its map centre
        private static float FrameHalf(AreaMapModel.Part part) => FrameHalf(part.Min, part.Max, part.Centre);

        private static float FrameHalf(Vector3 min, Vector3 max, Vector3 centre)
        {
            Vector3 size = max - min;
            float farthest = 0;
            for (int i = 0; i < 8; i++)
            {
                var corner = new Vector3((i & 1) == 0 ? min.X : max.X, (i & 2) == 0 ? min.Y : max.Y, (i & 4) == 0 ? min.Z : max.Z);
                farthest = MathF.Max(farthest, (corner - centre).Length);
            }
            return MathF.Max(MinHalf, FrameScale * (0.25f * size.Length + 0.5f * farthest) * FrameMargin);
        }

        // the frame of a room with pieces (without them) or of one piece: the bounds of its own triangles, about their
        // middle (a room without pieces keeps its cent node's centre)
        private static (Vector3 Centre, float Half) FrameOf(Room room, CorridorMap.Piece? piece)
        {
            object key = piece ?? (object)room.Part;
            if (_frames.TryGetValue(key, out (Vector3 Centre, float Half) known))
            {
                return known;
            }
            (Vector3 Centre, float Half) frame = (room.Part.Centre, room.Half);
            CorridorMap.Piece[] list = CorridorMap.Of(room.Part.Name);
            if (list.Length > 0)
            {
                int want = piece == null ? -1 : Array.IndexOf(list, piece);
                sbyte[] polyPiece = CorridorMap.PolyPieces(room.Model, room.Part);
                MapGeometry g = room.Part.Nav;
                Vector3 min = new(float.MaxValue), max = new(float.MinValue);
                for (int i = 0; i < g.Polys.Count; i++)
                {
                    if (polyPiece[i] != want)
                    {
                        continue;
                    }
                    MapGeometry.Poly p = g.Polys[i];
                    for (int c = 0; c < p.Count; c++)
                    {
                        Vector3 v = g.Points[g.Indices[p.Start + c]];
                        min = Vector3.ComponentMin(min, v);
                        max = Vector3.ComponentMax(max, v);
                    }
                }
                if (min.X <= max.X)
                {
                    Vector3 centre = (min + max) / 2;
                    frame = (centre, FrameHalf(min, max, centre));
                }
            }
            _frames[key] = frame;
            return frame;
        }

        // which of a part's pieces to draw: the seen ones and the one she's in
        private static bool[] ShowPieces(AreaMapModel.Part part, CorridorMap.Piece? current)
        {
            CorridorMap.Piece[] list = CorridorMap.Of(part.Name);
            for (int i = 0; i < _showPiece.Length; i++)
            {
                _showPiece[i] = i < list.Length && (list[i] == current || CorridorMap.Seen(list[i]));
            }
            return _showPiece;
        }

        // a connector past an unseen piece's own door hides with it
        private static bool HiddenConnector(AreaMapModel.Part con, AreaMapModel.Part room, CorridorMap.Piece? current)
        {
            foreach (CorridorMap.Piece p in CorridorMap.Of(room.Name))
            {
                if (p != current && !CorridorMap.Seen(p) && Array.IndexOf(p.Connectors, con.Name) >= 0)
                {
                    return true;
                }
            }
            return false;
        }

        // a door only unseen pieces touch hides with them
        private static bool HiddenDoor(string room, int entityId)
        {
            bool own = false;
            foreach (CorridorMap.Piece p in CorridorMap.Of(room))
            {
                if (Array.IndexOf(p.OwnDoors, entityId) < 0)
                {
                    continue;
                }
                if (p == _current || CorridorMap.Seen(p))
                {
                    return false;
                }
                own = true;
            }
            return own;
        }

        // the area Samus is in: a connector of the room when she's inside its bounds (as AreaMapIndex.ConnectorAt), else the room
        private static AreaMapModel.Part AreaAt(Room room, Vector3 position)
        {
            const float Margin = 0.25f;
            Vector3 p = position + room.Part.Origin;
            foreach ((AreaMapModel.Part con, List<AreaMapModel.Part> _) in room.Connectors)
            {
                if (p.X >= con.Min.X - Margin && p.X <= con.Max.X + Margin && p.Y >= con.Min.Y - Margin
                    && p.Y <= con.Max.Y + Margin && p.Z >= con.Min.Z - Margin && p.Z <= con.Max.Z + Margin)
                {
                    return con;
                }
            }
            return room.Part;
        }

        private static AreaMapModel.Part? PartOf(AreaMapModel model, string room)
        {
            foreach (AreaMapModel.Part part in model.Parts)
            {
                if (!part.IsConnector && part.Name.Equals(room, StringComparison.OrdinalIgnoreCase))
                {
                    return part;
                }
            }
            return null;
        }

        private static bool Visited(AreaMapModel.Part room)
        {
            try
            {
                return room.RoomId >= 0 && GameState.StorySave != null && GameState.StorySave.CheckVisitedRoom(room.RoomId);
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static Matrix4 ViewOf(Vector3 centre, Vector3 forward)
        {
            float pitch = MathHelper.DegreesToRadians(PitchDegrees);
            Vector3 eye = centre - forward * (90 * MathF.Cos(pitch)) + Vector3.UnitY * (90 * MathF.Sin(pitch));
            return Matrix4.LookAt(eye, centre, Vector3.UnitY);
        }

        // the rooms around the player into pixels (size x size, straight alpha, transparent where there's nothing); facing =
        // the camera's forward (its yaw turns the map); time = seconds, for the glide between rooms; paint = false only
        // places the player in the view last painted. player = the player's spot in texels; gliding = paint again next
        // frame, the view is still moving to the room
        public static bool Paint(string name, string? roomPart, Vector3 position, Vector3 facing, float time, bool paint,
            ColorRgba[] pixels, int size, out Vector2 player, out bool gliding)
        {
            player = new Vector2(size / 2f);
            gliding = false;
            if (Find(name, out bool pending) is not Room room)
            {
                if (!pending)
                {
                    return false;
                }
                // its area's model is still loading: an empty map, asked for again next frame
                if (paint)
                {
                    Array.Clear(pixels);
                }
                gliding = true;
                return true;
            }
            // the door-bounded piece she's in (by the game's room part she's in), seen from now on, framed instead of the room
            CorridorMap.Piece? piece = CorridorMap.At(room.Part.Name, roomPart, position + room.Part.Origin);
            if (piece != null)
            {
                CorridorMap.MarkSeen(piece);
            }
            if (piece != _current)
            {
                _current = piece;
                _repaintDue = true;
            }
            if (CorridorMap.Version != _seenVersion)
            {
                _seenVersion = CorridorMap.Version;
                _repaintDue = true;
            }
            object area = piece ?? (object)room.Part;
            (Vector3 areaCentre, float areaHalf) = FrameOf(room, piece);
            AreaMapModel.Part inside = AreaAt(room, position); // a connector she's standing in is drawn, not framed
            if (inside != _inside)
            {
                _inside = inside;
                _repaintDue = true; // into or out of a connector: ask for a paint (as during a glide) until one runs
            }
            if (area != _shown)
            {
                // a new area: glide from the view on screen (same area model), else start there
                if (_shown != null && _shownModel == room.Model)
                {
                    _glideFrom = _viewCentre;
                    _glideFromHalf = _viewHalf;
                    _glideStart = time;
                }
                else
                {
                    _glideStart = -1;
                }
                _shown = area;
                _shownModel = room.Model;
                paint = true;
            }
            if (time < _glideStart)
            {
                _glideStart = time; // the clock restarted (a new scene)
            }
            float t = _glideStart < 0 ? 1 : Math.Clamp((time - _glideStart) / GlideSeconds, 0, 1);
            if (paint)
            {
                _repaintDue = false;
                float e = t * t * (3 - 2 * t);
                var forward = new Vector3(facing.X, 0, facing.Z);
                forward = forward.LengthSquared > 1e-6f ? forward.Normalized() : -Vector3.UnitZ;
                _viewCentre = t < 1 ? Vector3.Lerp(_glideFrom, areaCentre, e) : areaCentre;
                _viewHalf = t < 1 ? _glideFromHalf + (areaHalf - _glideFromHalf) * e : areaHalf;
                _view = ViewOf(_viewCentre, forward);
                PaintRooms(room, inside, size, pixels);
            }
            gliding = t < 1 || _repaintDue;
            Vector3 at = Vector3.TransformPosition(position + room.Part.Origin + Vector3.UnitY, _view);
            player = new Vector2(_viewC + at.X * _viewK, _viewC - at.Y * _viewK) / Super;
            return true;
        }

        private static void PaintRooms(Room room, AreaMapModel.Part area, int size, ColorRgba[] pixels)
        {
            int n = size * Super;
            if (_buf.Length != n * n * 4)
            {
                _buf = new float[n * n * 4];
            }
            else
            {
                Array.Clear(_buf);
            }
            Matrix4 view = _view;
            float k = _viewK = n / 2f / _viewHalf;
            float c = _viewC = n / 2f;
            Vector2 Screen(Vector3 v) => new Vector2(c + v.X * k, c - v.Y * k);
            // what's shown, farthest first (PauseMap.Build3D's order): the room, and around it what the map knows
            _draw.Clear();
            float DepthOf(AreaMapModel.Part p) => Vector3.TransformPosition(p.Centre, view).Z;
            _draw.Add((room.Part, -1, DepthOf(room.Part)));
            foreach ((AreaMapModel.Part con, List<AreaMapModel.Part> sides) in room.Connectors)
            {
                // the connector Samus is in always shows (Prime draws the area you're in); one past an unseen piece's door
                // doesn't
                if (con == area || sides.Count >= 2 && sides.All(r => r == room.Part || Visited(r))
                    && !sides.Any(r => HiddenConnector(con, r, r == room.Part ? _current : null)))
                {
                    _draw.Add((con, -1, DepthOf(con)));
                }
            }
            foreach (AreaMapModel.Part near in room.Near)
            {
                if (Visited(near))
                {
                    _draw.Add((near, -1, DepthOf(near)));
                }
            }
            if (LoadDoor())
            {
                BuildDoors(room);
                for (int d = 0; d < _doors.Count; d++)
                {
                    _draw.Add((null, d, Vector3.TransformPosition(_doors[d].Pos, view).Z));
                }
            }
            _draw.Sort((x, y) => x.Depth.CompareTo(y.Depth));
            foreach ((AreaMapModel.Part? part, int door, float _) in _draw)
            {
                if (part != null)
                {
                    bool pieces = !part.IsConnector && CorridorMap.Of(part.Name).Length > 0;
                    DrawPart(n, view, Screen, room.Model, part, pieces ? CorridorMap.PolyPieces(room.Model, part) : null,
                        pieces ? ShowPieces(part, part == room.Part ? _current : null) : null);
                }
                else
                {
                    DrawSmall(n, view, _door!, _doors[door].Pos, _doors[door].Rot, _doors[door].Colour, Screen);
                }
            }
            Resolve(n, size, pixels);
        }

        // the player marker alone, pointing up as it does over the map (the view's forward), filling the texture; share =
        // the sprite's width over the map's
        public static bool PaintMarker(ColorRgba[] pixels, int size, out float share)
        {
            share = 0;
            if (!LoadMarker())
            {
                return false;
            }
            int n = size * Super;
            _buf = new float[n * n * 4];
            Matrix4 view = ViewOf(Vector3.Zero, -Vector3.UnitZ);
            float k = n / 2f / (_markerRadius * MarkerMargin);
            float c = n / 2f;
            DrawMarker(n, view, Vector3.Zero, -Vector3.UnitZ, 1, v => new Vector2(c + v.X * k, c - v.Y * k));
            Resolve(n, size, pixels);
            share = MarkerShare * MarkerMargin;
            return true;
        }

        // Super x Super down, premultiplied -> straight alpha
        private static void Resolve(int n, int size, ColorRgba[] pixels)
        {
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float r = 0, gr = 0, b = 0, a = 0;
                    for (int sy = 0; sy < Super; sy++)
                    {
                        for (int sx = 0; sx < Super; sx++)
                        {
                            int i = ((y * Super + sy) * n + x * Super + sx) * 4;
                            r += _buf[i];
                            gr += _buf[i + 1];
                            b += _buf[i + 2];
                            a += _buf[i + 3];
                        }
                    }
                    const float samples = Super * Super;
                    a /= samples;
                    pixels[y * size + x] = a <= 1e-4f ? new ColorRgba(0, 0, 0, 0) : new ColorRgba(
                        ToByte(r / samples / a), ToByte(gr / samples / a), ToByte(b / samples / a), ToByte(a));
                }
            }
        }

        // one part in Prime's visited look: its surfaces far to near, then its outline (the edges one triangle owns)
        private static void DrawPart(int n, Matrix4 view, Func<Vector3, Vector2> screen, AreaMapModel model, AreaMapModel.Part part,
            sbyte[]? polyPiece = null, bool[]? showPiece = null)
        {
            MapGeometry g = part.Nav;
            var points = new Vector3[g.Points.Count];
            for (int i = 0; i < points.Length; i++)
            {
                points[i] = Vector3.TransformPosition(g.Points[i], view);
            }
            IReadOnlyList<Material> materials = model.Model.Materials;
            _tris.Clear();
            _edges.Clear();
            for (int pi = 0; pi < g.Polys.Count; pi++)
            {
                MapGeometry.Poly p = g.Polys[pi];
                if (polyPiece != null && showPiece != null && polyPiece[pi] >= 0 && !showPiece[polyPiece[pi]])
                {
                    continue; // a piece not seen yet
                }
                Material mat = materials[p.Material];
                Vector3 a = points[g.Indices[p.Start]];
                Vector3 normal = Vector3.Cross(points[g.Indices[p.Start + 1]] - a, points[g.Indices[p.Start + 2]] - a);
                if (normal.LengthSquared < 1e-12f)
                {
                    continue;
                }
                normal.Normalize();
                // as PauseMap.Build3D: the DS's front faces wind the other way from this cross product; the rooms are
                // interiors, so the near walls cull and the far ones show
                bool front = normal.Z < 0;
                if (mat.Culling == CullingMode.Back && !front || mat.Culling == CullingMode.Front && front)
                {
                    continue;
                }
                float al = SurfaceAlpha * (mat.Alpha / 31f);
                if (al * 31 <= 1)
                {
                    continue;
                }
                // Prime: 0.4 + 0.4 x the face's turn to the camera (the side shown faces it)
                float lit = 0.4f + 0.4f * MathF.Abs(normal.Z);
                Vector3 colour = Surface * lit;
                float depth = 0;
                for (int i = 0; i < p.Count; i++)
                {
                    depth += points[g.Indices[p.Start + i]].Z;
                }
                depth /= p.Count;
                Vector2 sa = screen(a);
                for (int i = 1; i + 1 < p.Count; i++)
                {
                    Vector2 sb = screen(points[g.Indices[p.Start + i]]);
                    Vector2 sc = screen(points[g.Indices[p.Start + i + 1]]);
                    _tris.Add((sa, sb, sc, depth, colour, al));
                    if (front && !p.Top)
                    {
                        AddEdge(sa, sb);
                        AddEdge(sb, sc);
                        AddEdge(sc, sa);
                    }
                }
            }
            _tris.Sort((x, y) => x.Depth.CompareTo(y.Depth));
            foreach (var t in _tris)
            {
                FillTriangle(n, t.A, t.B, t.C, t.Colour, t.Alpha);
            }
            foreach (KeyValuePair<(Vector2 A, Vector2 B), int> edge in _edges)
            {
                if (edge.Value == 1)
                {
                    DrawLine(n, edge.Key.A, edge.Key.B, 2 * Super, Outline, OutlineAlpha); // Prime: 1 pixel at 480 lines, ~2 here
                }
            }
        }

        // the map screen's doors round the room (PrimeMap.BuildDoors): the room's own in their beam colour, the shown
        // rooms' green where a drawn connector was walked through
        private static void BuildDoors(Room room)
        {
            _doors.Clear();
            int layer = DoorLayer(room);
            int shown = _draw.Count;
            AddDoors(room.Part, layer, own: true);
            for (int i = 0; i < shown; i++)
            {
                AreaMapModel.Part? part = _draw[i].Part;
                if (part != null && part != room.Part && !part.IsConnector)
                {
                    AddDoors(part, layer, own: false);
                }
            }
        }

        // the entity layer the area's doors come from (its boss state)
        private static int DoorLayer(Room room)
        {
            try
            {
                if (GameState.StorySave != null)
                {
                    int area = Metadata.GetAreaInfo(room.Part.RoomId);
                    return area < 0 || area >= 8 ? -1 : ((int)GameState.StorySave.BossFlags >> (2 * area)) & 3;
                }
            }
            catch (Exception)
            {
            }
            return -1;
        }

        private static void AddDoors(AreaMapModel.Part part, int layer, bool own)
        {
            foreach (DoorInfo door in DoorsOf(part.Name, layer))
            {
                Vector3 pos = door.Position + part.Origin;
                if (_doors.Any(d => (d.Pos - pos).LengthSquared < 0.01f) || HiddenDoor(part.Name, door.EntityId))
                {
                    continue;
                }
                Vector3 colour;
                if (own)
                {
                    int state = -1;
                    try
                    {
                        state = GameState.StorySave?.GetRoomState(part.RoomId, door.EntityId) ?? -1;
                    }
                    catch (Exception)
                    {
                        state = -1;
                    }
                    bool coloured = state >= 1 || state < 0 && door.Locked;
                    colour = (!coloured ? DoorColour[0] : door.Palette < 8 ? DoorColour[door.Palette] : new Vector3(20)) / 31f;
                }
                else if (WalkedThrough(pos))
                {
                    colour = OpenDoor;
                }
                else
                {
                    continue;
                }
                _doors.Add((pos, door.Rot, colour));
            }
        }

        // a door at a connector the map draws (PrimeMap.WalkedThrough: within 3 units of its bounds)
        private static bool WalkedThrough(Vector3 door)
        {
            const float Reach = 3;
            foreach ((AreaMapModel.Part? part, int _, float _) in _draw)
            {
                if (part == null || !part.IsConnector)
                {
                    continue;
                }
                Vector3 min = part.Min - new Vector3(Reach), max = part.Max + new Vector3(Reach);
                if (door.X >= min.X && door.X <= max.X && door.Y >= min.Y && door.Y <= max.Y && door.Z >= min.Z && door.Z <= max.Z)
                {
                    return true;
                }
            }
            return false;
        }

        private static List<DoorInfo> DoorsOf(string room, int layer)
        {
            if (_roomDoors.TryGetValue((room, layer), out List<DoorInfo>? list))
            {
                return list;
            }
            list = new List<DoorInfo>();
            try
            {
                (RoomMetadata? meta, int _) = Metadata.GetRoomByName(room.ToUpperInvariant());
                if (meta?.EntityPath != null)
                {
                    foreach (Entity e in Read.GetEntities(meta.EntityPath, layer, firstHunt: false))
                    {
                        if (e.Type == EntityType.Door && e is Entity<DoorEntityData> door)
                        {
                            // at the door's lock, Celestial Archives' squashed rooms stretched (as the map screens)
                            list.Add(new DoorInfo(PauseMap.DoorPoint(e, door.Data, room), PauseMap.Basis(e.FacingVector, e.UpVector), e.EntityId,
                                door.Data.PaletteId, door.Data.Locked != 0));
                        }
                    }
                }
            }
            catch (Exception)
            {
                list.Clear(); // no entity file: no doors
            }
            _roomDoors[(room, layer)] = list;
            return list;
        }

        private static bool LoadDoor()
        {
            if (!_doorTried)
            {
                _doorTried = true;
                _door = PauseMap.SmallModel.Load("hud/Door_NAV_Model.bin", null);
            }
            return _door != null;
        }

        // a small MPH model (a door) as PauseMap.DrawSmall: the DS's winding, its light, one colour, opaque
        private static void DrawSmall(int n, Matrix4 view, PauseMap.SmallModel model, Vector3 pos, Matrix3 rot, Vector3 colour,
            Func<Vector3, Vector2> screen)
        {
            _tris.Clear();
            for (int t = 0; t + 2 < model.Corners.Count; t += 3)
            {
                Vector3 W(Vector3 p) => Vector3.TransformPosition(pos + p * rot, view);
                Vector3 a = W(model.Corners[t]), b = W(model.Corners[t + 1]), d = W(model.Corners[t + 2]);
                Vector3 normal = Vector3.Cross(b - a, d - a);
                if (normal.LengthSquared < 1e-12f)
                {
                    continue;
                }
                normal.Normalize();
                Material mat = model.Model.Materials[model.Materials[t / 3]];
                if (mat.Culling == CullingMode.Back && normal.Z >= 0)
                {
                    continue;
                }
                float lit = 0.8f + 0.2f * MathF.Abs(normal.Z);
                _tris.Add((screen(a), screen(b), screen(d), (a.Z + b.Z + d.Z) / 3, colour * lit, 1));
            }
            _tris.Sort((x, y) => x.Depth.CompareTo(y.Depth));
            foreach (var t in _tris)
            {
                FillTriangle(n, t.A, t.B, t.C, t.Colour, t.Alpha);
            }
        }

        private static PauseMap.SmallModel? _marker;
        private static float _markerRadius;
        private static bool _markerTried;

        private static bool LoadMarker()
        {
            if (!_markerTried)
            {
                _markerTried = true;
                _marker = PauseMap.SmallModel.Load("hud/PlayerPos_NAV_Model.bin", null);
                if (_marker != null)
                {
                    foreach (Vector3 corner in _marker.Corners)
                    {
                        _markerRadius = MathF.Max(_markerRadius, corner.Length);
                    }
                }
            }
            return _marker != null && _markerRadius > 0;
        }

        // the pause map's marker (PauseMap.DrawSmall: the DS's winding, its light, its own colours) at scale
        private static void DrawMarker(int n, Matrix4 view, Vector3 at, Vector3 forward, float scale, Func<Vector3, Vector2> screen)
        {
            if (!LoadMarker())
            {
                return;
            }
            Matrix3 rot = PauseMap.Basis(forward, Vector3.UnitY);
            _tris.Clear();
            for (int t = 0; t + 2 < _marker!.Corners.Count; t += 3)
            {
                Vector3 W(Vector3 p) => Vector3.TransformPosition(at + (p * scale) * rot, view);
                Vector3 a = W(_marker.Corners[t]), b = W(_marker.Corners[t + 1]), d = W(_marker.Corners[t + 2]);
                Vector3 normal = Vector3.Cross(b - a, d - a);
                if (normal.LengthSquared < 1e-12f)
                {
                    continue;
                }
                normal.Normalize();
                Material mat = _marker.Model.Materials[_marker.Materials[t / 3]];
                if (mat.Culling == CullingMode.Back && normal.Z >= 0)
                {
                    continue;
                }
                float lit = 0.8f + 0.2f * MathF.Abs(normal.Z);
                _tris.Add((screen(a), screen(b), screen(d), (a.Z + b.Z + d.Z) / 3, _marker.Colours[t / 3] * lit, 1));
            }
            _tris.Sort((x, y) => x.Depth.CompareTo(y.Depth));
            foreach (var t in _tris)
            {
                FillTriangle(n, t.A, t.B, t.C, t.Colour, t.Alpha);
            }
        }

        private static byte ToByte(float v) => (byte)MathF.Round(Math.Clamp(v, 0, 1) * 255);

        private static void AddEdge(Vector2 a, Vector2 b)
        {
            (Vector2, Vector2) key = a.X < b.X || a.X == b.X && a.Y < b.Y ? (a, b) : (b, a);
            _edges[key] = _edges.TryGetValue(key, out int count) ? count + 1 : 1;
        }

        private static void Blend(int n, int x, int y, Vector3 colour, float alpha)
        {
            int i = (y * n + x) * 4;
            float keep = 1 - alpha;
            _buf[i] = colour.X * alpha + _buf[i] * keep;
            _buf[i + 1] = colour.Y * alpha + _buf[i + 1] * keep;
            _buf[i + 2] = colour.Z * alpha + _buf[i + 2] * keep;
            _buf[i + 3] = alpha + _buf[i + 3] * keep;
        }

        // a triangle by scanline spans: per row of sample centres, the x range all three edge functions keep >= 0
        private static void FillTriangle(int n, Vector2 a, Vector2 b, Vector2 c, Vector3 colour, float alpha)
        {
            float area = (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);
            if (MathF.Abs(area) < 1e-6f)
            {
                return;
            }
            float sign = MathF.Sign(area);
            int y0 = Math.Max(0, (int)MathF.Floor(MathF.Min(a.Y, MathF.Min(b.Y, c.Y))));
            int y1 = Math.Min(n - 1, (int)MathF.Ceiling(MathF.Max(a.Y, MathF.Max(b.Y, c.Y))));
            for (int y = y0; y <= y1; y++)
            {
                float py = y + 0.5f;
                float lo = 0, hi = n; // sample x + 0.5 in [lo, hi]
                if (!Clip(a, b, py, sign, ref lo, ref hi) || !Clip(b, c, py, sign, ref lo, ref hi)
                    || !Clip(c, a, py, sign, ref lo, ref hi))
                {
                    continue;
                }
                int x0 = Math.Max(0, (int)MathF.Ceiling(lo - 0.5f));
                int x1 = Math.Min(n - 1, (int)MathF.Floor(hi - 0.5f));
                for (int x = x0; x <= x1; x++)
                {
                    Blend(n, x, y, colour, alpha);
                }
            }
        }

        // the edge p->q's function at row py, ((q - p) x (s - p)) * sign >= 0, as a bound on s.x
        private static bool Clip(Vector2 p, Vector2 q, float py, float sign, ref float lo, ref float hi)
        {
            // w(x) = (q.X - p.X) * (py - p.Y) - (q.Y - p.Y) * (x - p.X), times sign
            float slope = -(q.Y - p.Y) * sign;
            float at = ((q.X - p.X) * (py - p.Y) + (q.Y - p.Y) * p.X) * sign;
            if (MathF.Abs(slope) < 1e-9f)
            {
                return at >= 0;
            }
            float x = -at / slope;
            if (slope > 0)
            {
                lo = MathF.Max(lo, x);
            }
            else
            {
                hi = MathF.Min(hi, x);
            }
            return lo <= hi;
        }

        // a line as a thin quad through the same filler
        private static void DrawLine(int n, Vector2 a, Vector2 b, float width, Vector3 colour, float alpha)
        {
            Vector2 d = b - a;
            if (d.LengthSquared < 1e-8f)
            {
                return;
            }
            Vector2 side = new Vector2(-d.Y, d.X).Normalized() * (width / 2);
            Vector2 along = d.Normalized() * (width / 2); // square caps, so joined edges meet without gaps
            Vector2 p0 = a - along - side, p1 = b + along - side, p2 = b + along + side, p3 = a - along + side;
            FillTriangle(n, p0, p1, p2, colour, alpha);
            FillTriangle(n, p0, p2, p3, colour, alpha);
        }
    }
}
