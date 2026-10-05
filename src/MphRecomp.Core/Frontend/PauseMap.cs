using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MphRead;
using MphRead.Formats;
using MphRead.Text;
using OpenTK.Mathematics;

// The in-game pause map as ONE 16:9 screen (owner's design, 2026-09-29: vanilla's map + legend merged, legend at the
// bottom; opened with Select in the campaign, Start keeps the recomp pause menu). Everything comes from the user's ROM:
//
// Top: the map frame (localSamus/bg_top_ovl.bin rows 512-703, the visor overlay's map section), widened by repeating its
//   uniform centre band (DS columns 113-142) and scaled to leave room for the legend. In its window the area map: both
//   halves of the current planet's map models (AreaMapModel; the second half moved by ov8's table at 0x21330e4), drawn
//   the way overlay 8 does (0x2130258 per frame): orthographic, 0.75 units per DS pixel to start (L/R zoom 0.4375-3),
//   the camera 28 degrees up, behind Samus, looking at the room's centRoom point + 3.75 up; visited rooms only (save
//   bits); the "selected" room (the player's at first, then the one nearest the middle after a pan) drawn brighter (light
//   colour (247,153,52), alpha 20/31; the others (247,123,22), alpha 4/31, twice) with an orange wireframe pass
//   (emission (247,123,22), 'T'op materials skipped); its doors in their beam colour (hud/Door_NAV_Model.bin, ov8
//   0x2133150), Samus as hud/PlayerPos_NAV_Model.bin (x2) at her room position + the room node's position, teleporters as
//   [P]/[B] sprites (map_legendOthers frames 0/1). Under the window the room's name, typed in (LETTER_BLIP per letter).
//   The side columns: one slot per area half (ov8 0x212fcd0): the planet's two slots once it's found -- the area's
//   portal (map_portal, lit when its 3 artifacts are in) with its artifacts (map_art_<area>), or its octolith
//   (map_crystalbig, sparkling) once taken, or a red octolith + the hunter holding it.
// Bottom: the legend (map_legendDoors / map_legendOthers, HudMessagesSP B00n beam names or "???" until the beam is owned,
//   M001-M004) and QUIT (map_quit).
//
// Recomp options (owner 2026-10-03, RECOMP SETTINGS):
// - DETAILED look: every room and connector drawn from its collision mesh (AreaMapModel.LoadDetail) in place of the map
//   model's boxes, with the same lighting, colours, selection and outline. Y switches it live.
// - PRIME mode (a Metroid Prime map with the Hunters look): only rooms Samus has been in and connectors she has walked
//   through; the view moves and turns freely and stays where it's left; the room under the middle of the view is the
//   selected one (highlighted, its name typed in); the D-pad hops to the next room that way, A centres the selected
//   room, X goes back to Samus; every shown room's doors; a tap selects a room. The map steps every frame (vanilla: every
//   other frame). Vanilla mode = overlay 8 as measured.
//
// The 3D map is projected and shaded on the CPU into the same draw list as the 2D parts, so the PC tools render exactly
// what the device shows; it is rebuilt only when the view or the selection changes.
namespace MphRecomp.Frontend
{
    public sealed class PauseMap
    {
        // ---------------------------------------------------------------- what the game hands over

        public sealed class Info
        {
            public string Room { get; init; } = ""; // MphRead room name (UNIT2_LAND)
            public string Title { get; init; } = ""; // the room's in-game name
            public Vector3 Position { get; init; } // Samus, room space
            public Vector3 Facing { get; init; } = -Vector3.UnitZ;
            public Vector3 Up { get; init; } = Vector3.UnitY;
            public Func<int, bool> Visited { get; init; } = _ => false; // room id
            public Func<int, int, int> RoomState { get; init; } = (_, _) => -1; // StorySave.GetRoomState(room id, entity id)
            public int Areas { get; init; } // StorySave.Areas
            public uint Artifacts { get; init; }
            public int Octoliths { get; init; } // StorySave.CurrentOctoliths
            public uint LostOctoliths { get; init; } = UInt32.MaxValue;
            public int Weapons { get; init; } // StorySave.Weapons
            // the connectors walked through (AreaMapIndex layout); the Prime mode shows only these
            public uint[]? Connectors { get; init; }
            // StorySave.BossFlags: picks each room's entity layer the way SceneSetup does (doors, teleporters); -1 = all
            public int BossFlags { get; init; } = -1;
        }

        // one frame's pad input
        public struct Input
        {
            public float PanX, PanY; // move the view (-1..1, +y = up)
            public float TurnX, TurnY; // turn it (-1..1; like a drag that way: +x right, +y down)
            public bool ZoomIn, ZoomOut; // held
            public int HopX, HopY; // Prime: a D-pad press, the next room that way (+y = up)
            public bool Centre, Home, ToggleLook; // presses: centre the selected room (Prime), back to Samus (Prime), DETAILED on/off
        }

        // ---------------------------------------------------------------- constants (DS pixels / ov8)

        private const float FrameScale = 0.78f; // the top screen's share of the height; the legend gets the rest
        private const int FrameRow = 512, BandX0 = 113, BandX1 = 143; // bg_top_ovl's map section; its uniform centre band
        private const int WinX0 = 40, WinX1 = 216, WinY0 = 25, WinY1 = 160; // the frame's window
        private const float NameTop = 168;
        private static readonly (int X, int Y)[] SlotAt = { (15, 102), (15, 134), (15, 38), (15, 70), (241, 38), (241, 70), (241, 102), (241, 134) };
        private static readonly (int X, int Y)[] ArtifactAt = { (-10, -7), (10, -7), (0, 12) };
        private static readonly string[] Hunters = { "samus", "kanden", "trace", "sylux", "noxus", "spyre", "weavel" };
        private const float ZoomStart = 0xc00 / 4096f, ZoomMin = 0x700 / 4096f, ZoomMax = 0x3000 / 4096f, ZoomStep = 1 / 16f;
        private const int PitchStart = 0x1400, PitchLimit = 0x32c8, TurnPerUnit = 64;
        private const float PanStep = 0x4999 / 4096f, LookUp = 3.75f;
        private static readonly Vector3 SelectedLight = new(247, 153, 52), OtherLight = new(247, 123, 22), Outline = new(247, 123, 22);
        private const int SelectedAlpha = 20, OtherAlpha = 4;
        // door colours by palette (ov8 0x2133150, 5-bit), > 7: grey 20
        private static readonly Vector3[] DoorColour =
        {
            new(28, 28, 28), new(31, 31, 0), new(30, 18, 10), new(0, 31, 0), new(31, 0, 0), new(20, 9, 31), new(31, 16, 0), new(0, 16, 31)
        };
        // legend rows (ov8 0x21331c8): beam doors (B00n names, legendDoors frame) then the others (M00n, sprite, frame)
        private static readonly (int Name, int Frame)[] BeamRows = { (4, 4), (2, 5), (8, 1), (5, 0), (6, 3), (7, 2) };
        private static readonly (int Name, bool Others, int Frame)[] OtherRows = { (1, false, 7), (3, false, 8), (2, true, 0), (4, true, 1) };
        private static readonly (float R, float G, float B) TextColour = (121 / 255f, 243 / 255f, 65 / 255f);
        private static readonly (float R, float G, float B) PanelColour = (40 / 255f, 40 / 255f, 40 / 255f);
        private static readonly (float R, float G, float B) RuleColour = (56 / 255f, 65 / 255f, 56 / 255f);
        private const int OpenSound = 0x19, LetterBlip = 0x1e0;
        // Prime mode, per frame (60 Hz): stick turn speed (angle units at full tilt), pan (map units at the starting zoom),
        // a drag's turn per canvas unit and its momentum (vanilla's 64 and 0.8 per 30 Hz update, spread over two frames)
        private const int PrimeTurn = 0x140;
        // the DETAILED look's outline width (DS pixels; the map model's is 0.9): its many edges read better thinner
        private const float DetailLine = 0.5f;
        private const float PrimePan = PanStep / 2, PrimeDragTurn = 33.8f, PrimeMomentum = 0.8944f, GlideRate = 0.2f;

        // ---------------------------------------------------------------- state

        private readonly string _root;
        private readonly UiTextureCache _textures;
        private readonly MenuFont _font;
        private readonly List<WidgetTri> _glyphs = new();
        private readonly int _frame, _quit;
        private readonly int[] _portal = new int[2], _crystal = new int[3], _red = new int[2], _art = new int[8], _faces = new int[7];
        private readonly int[] _doorKeys = new int[9], _others = new int[4];
        private AreaMapModel? _modelA, _modelB;
        private int _areaPair = -1;
        private SmallModel? _door, _marker;

        private sealed class Shown
        {
            public AreaMapModel.Part Part = null!;
            public AreaMapModel Model = null!;
            public Vector3 Offset; // the half's offset
            public List<(Vector3 Pos, int Frame)> Teleporters = new();
        }

        private readonly List<Shown> _shown = new();
        private Shown? _selected, _playerRoom;
        private readonly List<(Vector3 Pos, Matrix3 Rot, Vector3 Colour)> _doors = new();
        private Info _info = new();
        private bool _open;
        private int _tick, _typed, _typeFrom;
        private string _title = "";
        private int _yaw, _pitch;
        private float _zoom = ZoomStart;
        private Vector3 _target, _pan;
        private Vector3? _glideTo; // Prime: the pan the view is gliding to
        private float _glide;
        private bool _panned;
        private float _scale = 1, _w = 341, _s = FrameScale;
        // touch: a drag turns the map (with momentum, _turnX/_turnY in angle units per update); a tap selects (Prime)
        private bool _dragging;
        private float _dragX0, _dragY0, _dragX, _dragY, _turnX, _turnY;
        private float _downX, _downY, _travel;
        private int _downTick;
        private const float Momentum = 0.8f;
        private int _version; // bumped when what's shown changes (the 3D cache's key)

        public UiDrawList DrawList { get; }
        public bool IsOpen => _open;
        public bool Detailed { get; private set; }
        public bool Prime { get; set; }
        public Action<int>? PlaySound;
        public Action? Quit;
        public Action<string>? Log;
        public Action<bool>? DetailedChanged; // Y switched the look

        public PauseMap(string fileSystemRoot, UiTextureCache textures)
        {
            _root = fileSystemRoot;
            _textures = textures;
            DrawList = new UiDrawList(textures);
            _font = new MenuFont(textures);
            string sp = Path.Combine(fileSystemRoot, "_archives", "spSamus");
            _frame = _textures.GetOrAdd("map/frame", () =>
            {
                DsImage im = DsGraphics.LoadBg(Path.Combine(fileSystemRoot, "_archives", "localSamus", "bg_top_ovl.bin"));
                var rgba = new byte[256 * 192 * 4];
                Array.Copy(im.Rgba, FrameRow * im.Width * 4, rgba, 0, rgba.Length);
                return (256, 192, rgba);
            }).Id;
            _quit = Sprite(sp, "map_quit", 0);
            for (int i = 0; i < 2; i++) _portal[i] = Sprite(sp, "map_portal", i);
            for (int i = 0; i < 3; i++) _crystal[i] = Sprite(sp, "map_crystalbig", i);
            for (int i = 0; i < 2; i++) _red[i] = Sprite(sp, "map_crystalred", i);
            for (int i = 0; i < 8; i++) _art[i] = Sprite(sp, "map_art_" + (i + 1), 0);
            for (int i = 0; i < 9; i++) _doorKeys[i] = Sprite(sp, "map_legendDoors", i);
            for (int i = 0; i < 4; i++) _others[i] = Sprite(sp, "map_legendOthers", i);
            string common = Path.Combine(fileSystemRoot, "_archives", "common");
            for (int i = 0; i < 7; i++) _faces[i] = Sprite(common, "enemy_" + Hunters[i], 0);
        }

        private int Sprite(string dir, string name, int image) => _textures.GetOrAdd($"map/{name}/{image}", () =>
        {
            DsImage im = DsGraphics.LoadSprite(Path.Combine(dir, name + ".bin")).Image(image, 0);
            return (im.Width, im.Height, im.Rgba);
        }).Id;

        private static string Hud(char type, int id) => Strings.GetMessage(type, id, StringTables.HudMessagesSP);

        // ---------------------------------------------------------------- open / close / options

        public void Open(Info info)
        {
            _info = info;
            _open = true;
            _entities.Clear(); // the layers follow the boss flags
            _tick = 0;
            _dragging = false;
            _pan = Vector3.Zero;
            _glideTo = null;
            _glide = 0;
            _panned = false;
            _turnX = _turnY = 0;
            _zoom = ZoomStart;
            LoadArea(info.Room);
            if (Detailed) LoadDetail();
            BuildShown();
            _selected = _playerRoom;
            _target = _selected != null ? _selected.Part.Centre + _selected.Offset : Vector3.Zero;
            // behind Samus: yaw = atan2(-facing.x, -facing.z) (ov8), 28 degrees up
            _yaw = Angle(MathF.Atan2(-info.Facing.X, -info.Facing.Z));
            _pitch = PitchStart;
            BuildDoors();
            TypeTitle((info.Title ?? "").ToUpperInvariant());
            _version++;
            Log?.Invoke($"map: {info.Room} -> {(_modelA?.File ?? "no map model")}{(_modelB != null ? " + " + _modelB.File : "")}, {_shown.Count} parts shown, {_doors.Count} doors{(Prime ? ", prime" : "")}{(Detailed ? ", detailed" : "")}");
            PlaySound?.Invoke(OpenSound);
        }

        public void Close()
        {
            _open = false;
            _dragging = false;
        }

        // the DETAILED look on or off (loads the collision meshes the first time)
        public void SetDetailed(bool on)
        {
            if (Detailed == on) return;
            Detailed = on;
            if (on && _open) LoadDetail();
            _version++;
        }

        private void LoadDetail()
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            bool any = false;
            foreach (AreaMapModel? m in new[] { _modelA, _modelB })
            {
                if (m == null || m.DetailLoaded) continue;
                m.LoadDetail(Log);
                any = true;
            }
            if (any) Log?.Invoke($"map: detailed look loaded in {watch.ElapsedMilliseconds} ms");
        }

        private void TypeTitle(string title)
        {
            _title = title;
            _typed = 0;
            _typeFrom = _tick;
        }

        private static int Angle(float radians) => (int)MathF.Round(radians / (2 * MathF.PI) * 0x10000);
        private static float Rad(int angle) => angle / (float)0x10000 * 2 * MathF.PI;

        // both halves of the room's planet: unitN_1 and unitN_2 (Arcterra has one model)
        private void LoadArea(string room)
        {
            AreaMapModel? own = AreaMapModel.ForRoom(_root, room);
            if (own == null)
            {
                _modelA = _modelB = null;
                _areaPair = -1;
                return;
            }
            (int unit, int half) = AreaMapModel.UnitHalf(own.File);
            int pair = (unit - 1) * 2;
            if (pair == _areaPair && _modelA != null) return;
            _areaPair = pair;
            AreaMapModel? Other(int h)
            {
                string? file = AreaMapModel.FindFiles(_root).FirstOrDefault(f => AreaMapModel.UnitHalf(f) == (unit, h));
                return file == null ? null : file == own.File ? own : AreaMapModel.Load(file);
            }
            _modelA = half == 1 ? own : Other(1);
            _modelB = half == 2 ? own : Other(2);
        }

        private void BuildShown()
        {
            _shown.Clear();
            _playerRoom = null;
            void Add(AreaMapModel? model, Vector3 offset)
            {
                if (model == null) return;
                var rooms = model.Parts.Where(p => !p.IsConnector).ToList();
                foreach (AreaMapModel.Part part in model.Parts)
                {
                    bool own = part.Name.Equals(_info.Room, StringComparison.OrdinalIgnoreCase);
                    bool show;
                    if (part.IsConnector && Prime)
                    {
                        // Prime: the connectors Samus has walked through (tracked while she plays)
                        show = AreaMapIndex.Has(_info.Connectors, _areaPair, part.ConnectorNumber);
                    }
                    else if (part.IsConnector)
                    {
                        // vanilla keeps a bit per connector (save +0x30) that MphRead doesn't track: shown here once the
                        // rooms on both sides of it are visited
                        var near = rooms.Where(r => Touches(part, r)).ToList();
                        show = near.Count >= 2 && near.All(r => RoomSeen(r));
                    }
                    else
                    {
                        show = own || RoomSeen(part);
                    }
                    if (!show) continue;
                    var shown = new Shown { Part = part, Model = model, Offset = offset };
                    if (!part.IsConnector) shown.Teleporters.AddRange(Teleporters(part));
                    _shown.Add(shown);
                    if (own) _playerRoom = shown;
                }
            }
            Add(_modelA, Vector3.Zero);
            Add(_modelB, _areaPair >= 0 ? AreaMapModel.HalfOffsets[_areaPair + 1] : Vector3.Zero);
        }

        private bool RoomSeen(AreaMapModel.Part room) => room.RoomId >= 0 && _info.Visited(room.RoomId);

        private static bool Touches(AreaMapModel.Part a, AreaMapModel.Part b)
        {
            const float Reach = 1.5f;
            return a.Min.X <= b.Max.X + Reach && a.Max.X >= b.Min.X - Reach && a.Min.Y <= b.Max.Y + Reach && a.Max.Y >= b.Min.Y - Reach
                && a.Min.Z <= b.Max.Z + Reach && a.Max.Z >= b.Min.Z - Reach;
        }

        // the room's teleporters (entity type 14), in map space: entity position + the room node's position + 1 up;
        // [B] (frame 1) for an artifact portal (ArtifactId < 8, visible), else [P]
        private IEnumerable<(Vector3, int)> Teleporters(AreaMapModel.Part room)
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

        private readonly Dictionary<string, IReadOnlyList<Entity>> _entities = new(StringComparer.OrdinalIgnoreCase);

        private IReadOnlyList<Entity> RoomEntities(string room)
        {
            if (_entities.TryGetValue(room, out IReadOnlyList<Entity>? list)) return list;
            list = Array.Empty<Entity>();
            try
            {
                (RoomMetadata? meta, int id) = MphRead.Metadata.GetRoomByName(room.ToUpperInvariant());
                if (meta?.EntityPath != null)
                {
                    // the layer the game loads the room with now (SceneSetup: by the area's boss flags)
                    int area = MphRead.Metadata.GetAreaInfo(id);
                    int layer = _info.BossFlags < 0 || area < 0 || area >= 8 ? -1 : (_info.BossFlags >> (2 * area)) & 3;
                    list = Read.GetEntities(meta.EntityPath, layer, firstHunt: false);
                }
            }
            catch (Exception ex)
            {
                Log?.Invoke($"map: entities of {room}: {ex.Message}");
            }
            _entities[room] = list;
            return list;
        }

        // doors (entity type 3): Door_NAV_Model in the door's colour (grey until its colour is known: room state >= 1, or
        // locked while the state is unset -- ov8). Vanilla: the selected room's; Prime: every shown room's
        private void BuildDoors()
        {
            _doors.Clear();
            if (Prime)
            {
                foreach (Shown s in _shown) AddDoors(s);
            }
            else if (_selected != null)
            {
                AddDoors(_selected);
            }
            _version++;
        }

        private void AddDoors(Shown s)
        {
            if (s.Part.IsConnector) return;
            AreaMapModel.Part room = s.Part;
            foreach (Entity e in RoomEntities(room.Name))
            {
                if (e.Type != EntityType.Door || e is not Entity<DoorEntityData> door) continue;
                Vector3 pos = e.Position + room.Origin + s.Offset;
                if (_doors.Any(d => (d.Pos - pos).LengthSquared < 0.01f)) continue;
                int state = _info.RoomState(room.RoomId, e.EntityId);
                bool coloured = state >= 1 || (state < 0 && door.Data.Locked != 0);
                uint pal = door.Data.PaletteId;
                Vector3 c = !coloured ? DoorColour[0] : pal < 8 ? DoorColour[pal] : new Vector3(20);
                _doors.Add((pos, Basis(e.FacingVector, e.UpVector), c / 31f));
            }
        }

        private static Matrix3 Basis(Vector3 facing, Vector3 up)
        {
            Vector3 f = facing.LengthSquared > 1e-6f ? facing.Normalized() : -Vector3.UnitZ;
            Vector3 u = up.LengthSquared > 1e-6f ? up.Normalized() : Vector3.UnitY;
            Vector3 r = Vector3.Cross(u, f);
            if (r.LengthSquared < 1e-6f) r = Vector3.UnitX;
            r.Normalize();
            u = Vector3.Cross(f, r);
            return new Matrix3(r, u, f); // rows: model x, y, z axes in map space
        }

        private void Select(Shown? room)
        {
            if (room == null || room == _selected) return;
            _selected = room;
            if (Prime)
            {
                TypeTitle(TitleOf(room));
                _version++;
            }
            else
            {
                BuildDoors();
            }
        }

        private string TitleOf(Shown room)
        {
            if (room == _playerRoom) return (_info.Title ?? "").ToUpperInvariant();
            RoomMetadata? meta = MphRead.Metadata.GetRoomById(room.Part.RoomId, noThrow: true);
            return (meta?.InGameName ?? meta?.Name ?? room.Part.Name).ToUpperInvariant();
        }

        // ---------------------------------------------------------------- input (60 Hz; vanilla's map steps at 30 Hz)

        public void Tick(Input input)
        {
            if (!_open) return;
            _tick++;
            // the name types in: a letter every other frame, LETTER_BLIP for each (vanilla f24-f53 for CELESTIAL GATEWAY)
            int t = _tick - _typeFrom;
            if (_typed < _title.Length && t >= 4 && (t & 1) == 0)
            {
                _typed++;
                if (_title[_typed - 1] != ' ') PlaySound?.Invoke(LetterBlip);
            }
            if (input.ToggleLook)
            {
                SetDetailed(!Detailed);
                DetailedChanged?.Invoke(Detailed);
            }
            if (Prime)
            {
                TickPrime(input);
            }
            else if ((_tick & 1) == 0)
            {
                TickVanilla(input); // ov8 updates the map every other frame
            }
        }

        private void TickVanilla(Input input)
        {
            if (input.ZoomIn) _zoom = Math.Max(ZoomMin, _zoom - ZoomStep);
            if (input.ZoomOut) _zoom = Math.Min(ZoomMax, _zoom + ZoomStep);
            // turning: the stylus's movement since the last update (64 per pixel), with momentum -- vanilla keeps turning
            // for ~10 updates after the stylus stops and holds still once it has (BizHawk: a 24 px drag turns ~45 deg);
            // the right stick pushes like a steady 2 px-per-update drag
            float dx = _dragX - _dragX0 + input.TurnX * 2, dy = _dragY - _dragY0 + input.TurnY * 2;
            _dragX0 = _dragX;
            _dragY0 = _dragY;
            _turnX = _turnX * Momentum + dx * TurnPerUnit;
            _turnY = _turnY * Momentum + dy * TurnPerUnit;
            if (MathF.Abs(_turnX) < 1) _turnX = 0;
            if (MathF.Abs(_turnY) < 1) _turnY = 0;
            _yaw -= (int)_turnX;
            _pitch = Math.Clamp(_pitch + (int)_turnY, -PitchLimit, PitchLimit);
            Matrix4 view = View();
            Vector3 right = new(view.M11, view.M21, view.M31), up = new(view.M12, view.M22, view.M32);
            if (MathF.Abs(input.PanX) > 0.2f || MathF.Abs(input.PanY) > 0.2f)
            {
                _pan += right * (input.PanX * PanStep) + up * (input.PanY * PanStep);
                _glide = 0;
                _panned = true;
            }
            else if (_pan.LengthSquared > 1e-6f)
            {
                // no pan input: the view glides back (t up 1/16 per update to 1; offset *= 1 - 0.1 t)
                _glide = Math.Min(1, _glide + 1 / 16f);
                _pan *= 1 - 0.1f * _glide;
                if (_pan.LengthSquared < 1e-4f) _pan = Vector3.Zero;
            }
            if (_panned)
            {
                // the selected room follows the middle of the view once it has been panned
                Select(NearestToCentre(View()));
            }
        }

        // Prime: free moving and turning at 60 Hz, the view stays put; the room under the middle is the selected one
        private void TickPrime(Input input)
        {
            if (input.ZoomIn) _zoom = Math.Max(ZoomMin, _zoom - ZoomStep / 2);
            if (input.ZoomOut) _zoom = Math.Min(ZoomMax, _zoom + ZoomStep / 2);
            // a drag turns as in vanilla (spread over two frames); the stick turns steadily
            float dx = _dragX - _dragX0, dy = _dragY - _dragY0;
            _dragX0 = _dragX;
            _dragY0 = _dragY;
            _turnX = _turnX * PrimeMomentum + dx * PrimeDragTurn;
            _turnY = _turnY * PrimeMomentum + dy * PrimeDragTurn;
            if (MathF.Abs(_turnX) < 1) _turnX = 0;
            if (MathF.Abs(_turnY) < 1) _turnY = 0;
            float sx = Dead(input.TurnX), sy = Dead(input.TurnY);
            _yaw -= (int)(_turnX + sx * PrimeTurn);
            _pitch = Math.Clamp(_pitch + (int)(_turnY + sy * PrimeTurn), -PitchLimit, PitchLimit);
            bool moved = _turnX != 0 || _turnY != 0 || sx != 0 || sy != 0 || input.ZoomIn || input.ZoomOut;
            Matrix4 view = View();
            Vector3 right = new(view.M11, view.M21, view.M31), up = new(view.M12, view.M22, view.M32);
            float px = Dead(input.PanX), py = Dead(input.PanY);
            if (px != 0 || py != 0)
            {
                // the same speed on screen at any zoom
                _pan += (right * px + up * py) * (PrimePan * _zoom / ZoomStart);
                _glideTo = null;
                moved = true;
            }
            if (input.HopX != 0 || input.HopY != 0)
            {
                Shown? next = Neighbour(view, input.HopX, input.HopY);
                if (next != null)
                {
                    Select(next);
                    GlideTo(next);
                }
            }
            if (input.Centre && _selected != null) GlideTo(_selected);
            if (input.Home && _playerRoom != null)
            {
                Select(_playerRoom);
                _glideTo = Vector3.Zero;
            }
            if (_glideTo is Vector3 goal)
            {
                _pan += (goal - _pan) * GlideRate;
                if ((goal - _pan).LengthSquared < 0.0025f)
                {
                    _pan = goal;
                    _glideTo = null;
                }
            }
            else if (moved)
            {
                // the room under the middle of the view (kept while nothing is there)
                Select(PickAt(View(), Vector2.Zero));
            }
        }

        private static float Dead(float v) => MathF.Abs(v) < 0.2f ? 0 : v;

        private void GlideTo(Shown room) => _glideTo = room.Part.Centre + room.Offset - _target;

        private Shown? NearestToCentre(Matrix4 view)
        {
            Shown? best = null;
            float bestD = float.MaxValue;
            foreach (Shown s in _shown)
            {
                if (s.Part.IsConnector) continue;
                Vector3 v = Vector3.TransformPosition(s.Part.Centre + s.Offset, view);
                float d = v.X * v.X + v.Y * v.Y;
                if (d < bestD)
                {
                    bestD = d;
                    best = s;
                }
            }
            return best;
        }

        // the room whose map model is under a point of the view (view space, map units), the one nearest the camera
        private Shown? PickAt(Matrix4 view, Vector2 at)
        {
            Shown? best = null;
            float bestZ = float.MinValue;
            foreach (Shown s in _shown)
            {
                if (s.Part.IsConnector) continue;
                MapGeometry g = s.Part.Nav;
                foreach (MapGeometry.Poly p in g.Polys)
                {
                    Vector3 a = Vector3.TransformPosition(g.Points[g.Indices[p.Start]] + s.Offset, view);
                    for (int i = 1; i + 1 < p.Count; i++)
                    {
                        Vector3 b = Vector3.TransformPosition(g.Points[g.Indices[p.Start + i]] + s.Offset, view);
                        Vector3 c = Vector3.TransformPosition(g.Points[g.Indices[p.Start + i + 1]] + s.Offset, view);
                        if (!Inside(at, a.Xy, b.Xy, c.Xy)) continue;
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

        private static bool Inside(Vector2 p, Vector2 a, Vector2 b, Vector2 c)
        {
            static float Side(Vector2 p, Vector2 a, Vector2 b) => (p.X - b.X) * (a.Y - b.Y) - (a.X - b.X) * (p.Y - b.Y);
            float d1 = Side(p, a, b), d2 = Side(p, b, c), d3 = Side(p, c, a);
            bool neg = d1 < 0 || d2 < 0 || d3 < 0, pos = d1 > 0 || d2 > 0 || d3 > 0;
            return !(neg && pos);
        }

        // the next room from the selected one in a screen direction (+y up): the nearest within ~55 degrees of it
        private Shown? Neighbour(Matrix4 view, int dirX, int dirY)
        {
            if (_selected == null) return _playerRoom;
            Vector2 from = Vector3.TransformPosition(_selected.Part.Centre + _selected.Offset, view).Xy;
            Vector2 dir = new Vector2(dirX, dirY).Normalized();
            Shown? best = null;
            float bestScore = float.MaxValue;
            foreach (Shown s in _shown)
            {
                if (s.Part.IsConnector || s == _selected) continue;
                Vector2 v = Vector3.TransformPosition(s.Part.Centre + s.Offset, view).Xy - from;
                float along = Vector2.Dot(v, dir), across = MathF.Abs(v.X * dir.Y - v.Y * dir.X);
                if (along <= 0.5f || across > along * 1.4f) continue;
                float score = along + 2 * across;
                if (score < bestScore)
                {
                    bestScore = score;
                    best = s;
                }
            }
            return best;
        }

        // canvas-pixel touch: kind 0 down, 1 move, 2 up. QUIT (the legend's corner) asks to quit; anywhere else turns the
        // map; in the Prime mode a tap (no drag) on a room selects it
        public void Touch(float x, float y, int kind)
        {
            if (!_open) return;
            float ux = x / _scale, uy = y / _scale;
            if (kind == 0)
            {
                (float qx, float qy, float qw, float qh) = QuitRect();
                if (ux >= qx && ux < qx + qw && uy >= qy && uy < qy + qh)
                {
                    Quit?.Invoke();
                    return;
                }
                _dragging = true;
                _dragX0 = _dragX = ux;
                _dragY0 = _dragY = uy;
                _downX = ux;
                _downY = uy;
                _travel = 0;
                _downTick = _tick;
            }
            else if (kind == 1 && _dragging)
            {
                _travel = Math.Max(_travel, MathF.Max(MathF.Abs(ux - _downX), MathF.Abs(uy - _downY)));
                _dragX = ux;
                _dragY = uy;
            }
            else if (kind == 2)
            {
                bool tap = _dragging && _travel < 3 && _tick - _downTick < 30;
                _dragging = false;
                if (tap && Prime && InWindow(ux, uy))
                {
                    float k = _s / _zoom;
                    Shown? room = PickAt(View(), new Vector2((ux - _w / 2) / k, (96 * _s - uy) / k));
                    if (room != null)
                    {
                        Select(room);
                        GlideTo(room);
                    }
                }
            }
        }

        private bool InWindow(float x, float y) => x >= LeftX(WinX0) && x < RightX(WinX1) && y >= WinY0 * _s && y < WinY1 * _s;

        // ---------------------------------------------------------------- camera

        private Matrix4 View()
        {
            float y = Rad(_yaw), p = Rad(_pitch);
            Vector3 at = _target + _pan + new Vector3(0, LookUp, 0);
            Vector3 eye = at + 90 * new Vector3(MathF.Cos(p) * MathF.Sin(y), MathF.Sin(p), MathF.Cos(p) * MathF.Cos(y));
            return Matrix4.LookAt(eye, at, Vector3.UnitY);
        }

        // ---------------------------------------------------------------- drawing

        private float LeftX(float dsX) => dsX * _s; // DS x in the frame's left part -> canvas
        private float RightX(float dsX) => _w - (256 - dsX) * _s; // DS x in the frame's right part -> canvas

        public void Build(int width, int height)
        {
            DrawList.Clear();
            DrawList.Clip = UiRect.None;
            _scale = height / 192f;
            _w = width / _scale;
            _s = Math.Min(FrameScale, _w / 256f);
            if (!_open) return;
            float top = 192 * _s;
            // the window's black, then the map, then the frame over it
            Rect(-1, LeftX(WinX0), WinY0 * _s, RightX(WinX1), WinY1 * _s, 0, 0, 0, 1);
            DrawList.Clip = new UiRect(LeftX(WinX0) * _scale, WinY0 * _s * _scale, (RightX(WinX1) - LeftX(WinX0)) * _scale, (WinY1 - WinY0) * _s * _scale);
            Draw3D();
            DrawTeleporters();
            DrawHints();
            DrawList.Clip = UiRect.None;
            DrawFrame();
            DrawSlots();
            if (_typed > 0) Text(_title[.._typed], _w / 2, NameTop * _s, 2, _s, 1);
            // the legend strip
            Rect(-1, 0, top, _w, 192, PanelColour.R, PanelColour.G, PanelColour.B, 1);
            Rect(-1, 0, top, _w, top + 1, RuleColour.R, RuleColour.G, RuleColour.B, 1);
            DrawLegend(top);
        }

        // what Build draws, as a value: the same key twice = the same picture (the platform may skip the rebuild)
        public (int, int, int, int, float, Vector3, int, int, int, int, bool) BuildKey(int width, int height)
        {
            int crystal = (_tick % 36) switch { < 30 => 0, < 32 => 1, < 34 => 2, _ => 1 };
            return (width, height, _yaw, _pitch, _zoom, _pan, _typed, crystal * 2 + (_tick / 10 & 1), _version,
                _selected?.GetHashCode() ?? 0, _open);
        }

        private void DrawFrame()
        {
            // left part, the centre band stretched, right part (the band's columns are all the same)
            float bandL = LeftX(BandX0), bandR = RightX(BandX1);
            Rect(_frame, 0, 0, bandL, 192 * _s, 1, 1, 1, 1, 0, 0, BandX0 / 256f, 1);
            Rect(_frame, bandL, 0, bandR, 192 * _s, 1, 1, 1, 1, BandX0 / 256f, 0, BandX1 / 256f, 1);
            Rect(_frame, bandR, 0, _w, 192 * _s, 1, 1, 1, 1, BandX1 / 256f, 0, 1, 1);
        }

        private void DrawSlots()
        {
            int crystalImage = (_tick % 36) switch { < 30 => 0, < 32 => 1, < 34 => 2, _ => 1 };
            bool redOn = (_tick / 10 & 1) == 0;
            for (int a = 0; a < 8; a++)
            {
                if ((_info.Areas & (1 << (a & ~1))) == 0) continue; // the planet found (its first half's bit)
                float cx = SlotAt[a].X < 128 ? LeftX(SlotAt[a].X) : RightX(SlotAt[a].X), cy = SlotAt[a].Y * _s;
                int lost = (int)(_info.LostOctoliths >> (4 * a) & 0xF);
                if ((_info.Octoliths & (1 << a)) != 0)
                {
                    SpriteAt(_crystal[crystalImage], cx, cy, 16, 32);
                }
                else if (lost < 8)
                {
                    if (redOn) SpriteAt(_red[0], cx + 6 * _s, cy, 16, 32);
                    // the hunter's face: ov8 puts its corner at slot - (16, 16), i.e. centred on the slot
                    if (lost < 7) SpriteAt(_faces[lost], cx, cy, 32, 32);
                }
                else
                {
                    bool all = true;
                    for (int k = 0; k < 3; k++)
                    {
                        if ((_info.Artifacts & (1u << (3 * a + k))) == 0) all = false;
                    }
                    SpriteAt(_portal[all ? 1 : 0], cx, cy, 32, 32);
                    for (int k = 0; k < 3; k++)
                    {
                        if ((_info.Artifacts & (1u << (3 * a + k))) != 0)
                        {
                            SpriteAt(_art[a], cx + ArtifactAt[k].X * _s, cy + ArtifactAt[k].Y * _s, 8, 8);
                        }
                    }
                }
            }
        }

        // a sprite centred at (cx, cy), DS size w x h at the frame's scale
        private void SpriteAt(int tex, float cx, float cy, int w, int h)
        {
            float hw = w * _s / 2, hh = h * _s / 2;
            Rect(tex, cx - hw, cy - hh, cx + hw, cy + hh, 1, 1, 1, 1);
        }

        private (float X, float Y, float W, float H) QuitRect()
        {
            float top = 192 * _s, h = 192 - top;
            float k = Math.Min(1, (h - 4) / 32);
            return (4, top + (h - 32 * k) / 2, 64 * k, 32 * k);
        }

        private void DrawLegend(float top)
        {
            (float qx, float qy, float qw, float qh) = QuitRect();
            Rect(_quit, qx, qy, qx + qw, qy + qh, 1, 1, 1, 1);
            float h = 192 - top;
            const float Pitch = 11;
            float k = Math.Min(1, (h - 6) / (3 * Pitch));
            float rowTop = top + (h - 3 * Pitch * k) / 2;
            // columns: beams 1-3, beams 4-6 (name right-aligned before the key), then ANY BEAM / MISSILE, PORTAL / BOSS
            // PORTAL (key before the name)
            string[] beamNames = BeamRows.Select(r => (_info.Weapons & (1 << (r.Name - 1))) != 0 ? Hud('B', r.Name).ToUpperInvariant() : "???").ToArray();
            string[] otherNames = OtherRows.Select(r => Hud('M', r.Name).ToUpperInvariant()).ToArray();
            float beamW = beamNames.Max(n => _font.Measure(n)) + 12;
            float otherW = otherNames.Max(n => _font.Measure(n)) + 14;
            float total = 2 * beamW + 2 * otherW + 3 * 8;
            float x0 = qx + qw + 8;
            float avail = _w - 4 - x0;
            k = Math.Min(k, avail / total);
            float x = x0 + Math.Max(0, (avail - total * k) / 2);
            for (int c = 0; c < 2; c++)
            {
                for (int r = 0; r < 3; r++)
                {
                    int i = c * 3 + r;
                    float y = rowTop + r * Pitch * k;
                    float keyX = x + (beamW - 9) * k;
                    Text(beamNames[i], keyX - 3 * k, y + 1 * k, 1, k, 1);
                    Rect(_doorKeys[BeamRows[i].Frame], keyX, y + 1 * k, keyX + 8 * k, y + 9 * k, 1, 1, 1, 1);
                }
                x += (beamW + 8) * k;
            }
            Rect(-1, x - 5 * k, rowTop, x - 4 * k, rowTop + 3 * Pitch * k - 2 * k, RuleColour.R, RuleColour.G, RuleColour.B, 1);
            for (int c = 0; c < 2; c++)
            {
                for (int r = 0; r < 2; r++)
                {
                    int i = c * 2 + r;
                    float y = rowTop + r * Pitch * k + Pitch * k / 2;
                    (int _, bool others, int frame) = OtherRows[i];
                    if (others) Rect(_others[frame], x - 4 * k, y - 3 * k, x + 12 * k, y + 13 * k, 1, 1, 1, 1);
                    else Rect(_doorKeys[frame], x, y + 1 * k, x + 8 * k, y + 9 * k, 1, 1, 1, 1);
                    Text(otherNames[i], x + 11 * k, y + 1 * k, 0, k, 1);
                }
                x += (otherW + 8) * k;
            }
        }

        // ---------------------------------------------------------------- the 3D map

        private struct Tri
        {
            public Vector2 A, B, C;
            public float Depth;
            public float R, G, Bl, Al;
        }

        private readonly List<Tri> _tris = new();
        // Build3D's triangles, and their depths sorted with their indices (Array.Sort with keys: no comparison delegate)
        private Tri[] _triBuf = new Tri[1024];
        private float[] _depthBuf = new float[1024];
        private int[] _orderBuf = new int[1024];
        private int _triCount;
        private readonly List<UiVertex> _cache3D = new();
        private (int, int, float, Vector3, Shown?, float, float, bool, int, bool) _cacheKey;
        private Vector3[] _viewPoints = new Vector3[256];
        private bool[] _outlined = new bool[256];

        // the 3D part, rebuilt only when the view, the size, the look or what's shown changed
        private void Draw3D()
        {
            if (_shown.Count == 0) return;
            var key = (_yaw, _pitch, _zoom, _target + _pan, _selected, _w, _scale, Detailed, _version, Prime);
            if (!key.Equals(_cacheKey) || _cache3D.Count == 0)
            {
                _cacheKey = key;
                _cache3D.Clear();
                Build3D();
            }
            DrawList.Triangles(_cache3D);
        }

        private void Build3D()
        {
            Matrix4 view = View();
            float k = _s / _zoom; // canvas units per map unit (ortho: 1 DS pixel = zoom units)
            float cx = _w / 2, cy = 96 * _s;
            Vector2 Screen(Vector3 v) => new(cx + v.X * k, cy - v.Y * k);
            // farthest parts first; each part's own polygons far to near, then (selected) its outline
            var order = _shown.Select(s => (s, Vector3.TransformPosition(s.Part.Centre + s.Offset, view).Z)).OrderBy(t => t.Z).Select(t => t.s).ToList();
            foreach (Shown s in order)
            {
                bool sel = s == _selected;
                MapGeometry g = Detailed && s.Part.Detail != null ? s.Part.Detail : s.Part.Nav;
                Vector3 light = (sel ? SelectedLight : OtherLight) / 255f;
                // the others are drawn twice: the map model's few triangles really are; the collision's many in one pass at
                // the alpha two passes of one colour come to
                int passes = sel || g.IsCollision ? 1 : 2;
                float alpha = (((31 + 1) * ((sel ? SelectedAlpha : OtherAlpha) + 1) >> 5) - 1) / 31f;
                if (!sel && g.IsCollision) alpha = 1 - (1 - alpha) * (1 - alpha);
                if (_viewPoints.Length < g.Points.Count) _viewPoints = new Vector3[g.Points.Count * 2];
                for (int i = 0; i < g.Points.Count; i++) _viewPoints[i] = Vector3.TransformPosition(g.Points[i] + s.Offset, view);
                _triCount = 0;
                var triEdges = new List<(Vector2, Vector2, float)>();
                if (_outlined.Length < g.Polys.Count) _outlined = new bool[g.Polys.Count * 2];
                IReadOnlyList<Material> materials = s.Model.Model.Materials;
                for (int pi = 0; pi < g.Polys.Count; pi++)
                {
                    MapGeometry.Poly p = g.Polys[pi];
                    _outlined[pi] = false;
                    Material mat = materials[p.Material];
                    Vector3 a = _viewPoints[g.Indices[p.Start]];
                    Vector3 ln = Vector3.TransformVector(p.Normal, view); // the view is a rotation + a move: no inverse needed
                    bool front;
                    if (g.IsCollision)
                    {
                        // the side the game collides from faces the camera: a room's far walls and its floors show
                        front = ln.Z > 0;
                        if (!front) continue;
                    }
                    else
                    {
                        Vector3 n = Vector3.Cross(_viewPoints[g.Indices[p.Start + 1]] - a, _viewPoints[g.Indices[p.Start + 2]] - a);
                        if (n.LengthSquared < 1e-12f) continue;
                        n.Normalize();
                        // the DS's front faces wind the other way from this cross product (checked against BizHawk: vanilla
                        // culls a room's near walls and shows the far ones -- the rooms are modelled as interiors)
                        front = n.Z < 0;
                        if (mat.Culling == CullingMode.Back && !front) continue;
                        if (mat.Culling == CullingMode.Front && front) continue;
                        if (ln.LengthSquared < 1e-12f) ln = n;
                    }
                    // light 0 along the view axis: the face's share of it, either side (vanilla lights the far walls the
                    // near-wall cull leaves showing); vanilla's fill reads as an even ~half of the light colour (measured
                    // (81,48,16) at alpha 20/31)
                    float lit = 0.4f + 0.2f * MathF.Abs(ln.LengthSquared > 1e-12f ? ln.Normalized().Z : 0);
                    Vector3 diffuse = new Vector3(mat.Diffuse.Red, mat.Diffuse.Green, mat.Diffuse.Blue) / 31f;
                    Vector3 col = Vector3.Clamp(light * diffuse * lit + new Vector3(mat.Ambient.Red, mat.Ambient.Green, mat.Ambient.Blue) / 31f * light, Vector3.Zero, Vector3.One);
                    float al = alpha * (mat.Alpha / 31f);
                    if (al * 31 <= 1) continue;
                    float depth = 0;
                    for (int i = 0; i < p.Count; i++) depth += _viewPoints[g.Indices[p.Start + i]].Z;
                    depth /= p.Count;
                    Vector2 sa = Screen(a);
                    for (int i = 1; i + 1 < p.Count; i++)
                    {
                        if (_triCount == _triBuf.Length)
                        {
                            Array.Resize(ref _triBuf, _triCount * 2);
                            Array.Resize(ref _depthBuf, _triCount * 2);
                            Array.Resize(ref _orderBuf, _triCount * 2);
                        }
                        _depthBuf[_triCount] = depth;
                        _orderBuf[_triCount] = _triCount;
                        _triBuf[_triCount++] = new Tri
                        {
                            A = sa, B = Screen(_viewPoints[g.Indices[p.Start + i]]), C = Screen(_viewPoints[g.Indices[p.Start + i + 1]]),
                            Depth = depth, R = col.X, G = col.Y, Bl = col.Z, Al = al
                        };
                    }
                    if (sel && front && !p.Top)
                    {
                        if (g.IsCollision)
                        {
                            _outlined[pi] = true;
                        }
                        else
                        {
                            Vector2 sb = Screen(_viewPoints[g.Indices[p.Start + 1]]), sc = Screen(_viewPoints[g.Indices[p.Start + 2]]);
                            triEdges.Add((sa, sb, depth));
                            triEdges.Add((sb, sc, depth));
                            triEdges.Add((sc, sa, depth));
                        }
                    }
                }
                Array.Sort(_depthBuf, _orderBuf, 0, _triCount);
                for (int pass = 0; pass < passes; pass++)
                {
                    for (int i = 0; i < _triCount; i++) Emit(_triBuf[_orderBuf[i]]);
                }
                if (sel)
                {
                    Vector3 c = Outline / 255f;
                    if (g.IsCollision)
                    {
                        // the creases along a shown, outlined face
                        foreach ((int a, int b, int poly) in g.Creases)
                        {
                            if (!_outlined[poly]) continue;
                            Line(Screen(_viewPoints[a]), Screen(_viewPoints[b]), DetailLine * _s, c.X, c.Y, c.Z, 1);
                        }
                    }
                    else
                    {
                        DrawOutline(triEdges);
                    }
                }
            }
            // Samus and the doors, over the map
            _marker ??= SmallModel.Load("hud/PlayerPos_NAV_Model.bin", Log);
            _door ??= SmallModel.Load("hud/Door_NAV_Model.bin", Log);
            if (_door != null)
            {
                foreach ((Vector3 pos, Matrix3 rot, Vector3 colour) in _doors)
                {
                    DrawSmall(_door, view, pos, rot, 1, colour, Screen);
                }
            }
            if (_marker != null && _playerRoom != null)
            {
                Vector3 pos = _info.Position + Vector3.UnitY + _playerRoom.Part.Origin + _playerRoom.Offset;
                // ov8 doubles the marker's axes; matched by eye against BizHawk it reads ~2.2x that here
                DrawSmall(_marker, view, pos, Basis(_info.Facing, _info.Up), 4.4f, null, Screen);
            }
        }

        private void Emit(in Tri t)
        {
            _cache3D.Add(V(t.A, t.R, t.G, t.Bl, t.Al));
            _cache3D.Add(V(t.B, t.R, t.G, t.Bl, t.Al));
            _cache3D.Add(V(t.C, t.R, t.G, t.Bl, t.Al));
        }

        // DS wireframe: polygon edges, so a quad's diagonal (an edge two coplanar triangles share) isn't drawn
        private void DrawOutline(List<(Vector2 A, Vector2 B, float Z)> edges)
        {
            static (Vector2, Vector2) Key(Vector2 a, Vector2 b) => a.X < b.X || (a.X == b.X && a.Y < b.Y) ? (a, b) : (b, a);
            var count = new Dictionary<(Vector2, Vector2), int>();
            foreach ((Vector2 a, Vector2 b, float _) in edges)
            {
                var key = Key(a, b);
                count[key] = count.TryGetValue(key, out int n) ? n + 1 : 1;
            }
            Vector3 c = Outline / 255f;
            float width = 0.9f * _s;
            foreach (var key in count.Where(e => e.Value == 1).Select(e => e.Key))
            {
                Line(key.Item1, key.Item2, width, c.X, c.Y, c.Z, 1);
            }
        }

        private void DrawSmall(SmallModel model, Matrix4 view, Vector3 pos, Matrix3 rot, float size, Vector3? colour, Func<Vector3, Vector2> screen)
        {
            _tris.Clear();
            for (int t = 0; t + 2 < model.Corners.Count; t += 3)
            {
                Vector3 W(Vector3 p) => Vector3.TransformPosition(pos + (p * size) * rot, view);
                Vector3 a = W(model.Corners[t]), b = W(model.Corners[t + 1]), d = W(model.Corners[t + 2]);
                Vector3 n = Vector3.Cross(b - a, d - a);
                if (n.LengthSquared < 1e-12f) continue;
                n.Normalize();
                Material mat = model.Model.Materials[model.Materials[t / 3]];
                if (mat.Culling == CullingMode.Back && n.Z >= 0) continue; // the DS's winding, as in Build3D
                Vector3 col = colour ?? model.Colours[t / 3];
                float lit = 0.8f + 0.2f * MathF.Abs(n.Z);
                _tris.Add(new Tri { A = screen(a), B = screen(b), C = screen(d), Depth = (a.Z + b.Z + d.Z) / 3, R = col.X * lit, G = col.Y * lit, Bl = col.Z * lit, Al = 1 });
            }
            _tris.Sort((p, q) => p.Depth.CompareTo(q.Depth));
            foreach (Tri t in _tris) Emit(t);
        }

        private void DrawTeleporters()
        {
            Matrix4 view = View();
            float k = _s / _zoom, cx = _w / 2, cy = 96 * _s;
            foreach (Shown s in _shown)
            {
                foreach ((Vector3 pos, int frame) in s.Teleporters)
                {
                    Vector3 v = Vector3.TransformPosition(pos + s.Offset, view);
                    float x = cx + v.X * k, y = cy - v.Y * k;
                    Rect(_others[frame], x - 8 * _s, y - 8 * _s, x + 8 * _s, y + 8 * _s, 1, 1, 1, 1);
                }
            }
        }

        // ---------------------------------------------------------------- primitives (canvas units)

        private UiVertex V(Vector2 p, float r, float g, float b, float a) => new(p.X * _scale, p.Y * _scale, 0, 0, r, g, b, a);

        private void Rect(int tex, float x0, float y0, float x1, float y1, float r, float g, float b, float a,
            float u0 = 0, float v0 = 0, float u1 = 1, float v1 = 1)
        {
            DrawList.Quad(tex, x0 * _scale, y0 * _scale, x1 * _scale, y1 * _scale, u0, v0, u1, v1, r, g, b, a);
        }

        // a line into the 3D cache
        private void Line(Vector2 a, Vector2 b, float width, float r, float g, float bl, float al)
        {
            Vector2 d = b - a;
            if (d.LengthSquared < 1e-8f) return;
            Vector2 n = new Vector2(-d.Y, d.X).Normalized() * (width / 2);
            _cache3D.Add(V(a - n, r, g, bl, al));
            _cache3D.Add(V(b - n, r, g, bl, al));
            _cache3D.Add(V(b + n, r, g, bl, al));
            _cache3D.Add(V(a - n, r, g, bl, al));
            _cache3D.Add(V(b + n, r, g, bl, al));
            _cache3D.Add(V(a + n, r, g, bl, al));
        }

        // The controls, on a dark band along the bottom of the map window (owner 2026-10-04: hint text in the map): this
        // mode's pad buttons (MphRead.Android CampaignMap.cs MapInputCore), wrapped to the window's width.
        private static readonly string[] PrimeHints =
            { "L STICK: TURN", "R STICK: MOVE", "D-PAD: NEXT ROOM", "A: CENTER", "X: SAMUS", "L / R: ZOOM", "Y: LOOK", "B: CLOSE" };
        private static readonly string[] HuntersHints =
            { "L STICK / D-PAD: MOVE", "R STICK: TURN", "L / R: ZOOM", "Y: LOOK", "B: CLOSE" };

        private void DrawHints()
        {
            const float Size = 0.7f, LineHeight = 10;
            const string Gap = "   ";
            float x0 = LeftX(WinX0), x1 = RightX(WinX1), k = Size * _s;
            float avail = (x1 - x0) / k - 8; // in DS pixels at text size 1
            var lines = new List<string>();
            string line = "";
            foreach (string hint in Prime ? PrimeHints : HuntersHints)
            {
                string next = line.Length == 0 ? hint : line + Gap + hint;
                if (line.Length > 0 && _font.Measure(next) > avail)
                {
                    lines.Add(line);
                    line = hint;
                }
                else
                {
                    line = next;
                }
            }
            if (line.Length > 0) lines.Add(line);
            float bottom = WinY1 * _s, top = bottom - (lines.Count * LineHeight + 5) * k;
            Rect(-1, x0, top, x1, bottom, 0, 0, 0, 0.65f);
            for (int i = 0; i < lines.Count; i++)
            {
                Text(lines[i], (x0 + x1) / 2, top + (2 + i * LineHeight) * k, 2, k, 0.9f);
            }
        }

        // HUD text: (x, top) in canvas units, align 0 left / 1 right / 2 centred, k = size (1 = DS pixels)
        private void Text(string text, float x, float top, int align, float k, float alpha)
        {
            if (string.IsNullOrEmpty(text)) return;
            _glyphs.Clear();
            const float LineHeight = 8;
            _font.Emit(text, 0, 192 - LineHeight, align, 1000, LineHeight, TextColour.R, TextColour.G, TextColour.B, alpha, 0, _glyphs);
            UiVertex M(UiVertex v) => new((x + v.X * k) * _scale, (top - v.Y * k) * _scale, v.U, v.V, v.R, v.G, v.B, v.A);
            foreach (WidgetTri t in _glyphs)
            {
                DrawList.Triangle(t.TextureId, UiWrap.Clamp, UiWrap.Clamp, UiBlend.Alpha, M(t.A), M(t.B), M(t.C));
            }
        }

        // ---------------------------------------------------------------- the marker / door models

        private sealed class SmallModel
        {
            public Model Model = null!;
            public List<Vector3> Corners = new();
            public List<int> Materials = new();
            public List<Vector3> Colours = new();

            public static SmallModel? Load(string path, Action<string>? log)
            {
                try
                {
                    Model model = Read.ReadModelFile(Path.GetFileNameWithoutExtension(path), path, null);
                    model.ComputeNodeMatrices(0);
                    var m = new SmallModel { Model = model };
                    float scale = model.Scale.X;
                    foreach (Node node in model.Nodes)
                    {
                        foreach (int meshId in node.GetMeshIds())
                        {
                            Mesh mesh = model.Meshes[meshId];
                            Material mat = model.Materials[mesh.MaterialId];
                            float[] v = MphRecomp.Render.DsDisplayList.Decode(model, mesh, isRoom: false);
                            int stride = MphRecomp.Render.DsDisplayList.Stride;
                            for (int k = 0; k + 3 * stride <= v.Length; k += 3 * stride)
                            {
                                Vector3 colour = Vector3.Zero;
                                for (int c = 0; c < 3; c++)
                                {
                                    int o = k + c * stride;
                                    m.Corners.Add(Vector3.TransformPosition(new Vector3(v[o], v[o + 1], v[o + 2]), node.Transform) * scale);
                                    // vertex colour when the list sets one (alpha 1), else the material's diffuse
                                    colour += v[o + 9] == 1 ? new Vector3(v[o + 6], v[o + 7], v[o + 8])
                                        : new Vector3(mat.Diffuse.Red, mat.Diffuse.Green, mat.Diffuse.Blue) / 31f;
                                }
                                m.Materials.Add(mesh.MaterialId);
                                m.Colours.Add(colour / 3);
                            }
                        }
                    }
                    return m;
                }
                catch (Exception ex)
                {
                    log?.Invoke($"map: {path}: {ex.Message}");
                    return null;
                }
            }
        }
    }
}
