using System;
using System.Collections.Generic;
using MphRead.Formats;
using MphRead.Formats.Collision;
using MphRead.Hud;
using OpenTK.Mathematics;

namespace MphRead.Entities
{
    // recomp: the Prime-style HUD (owner 2026-10-14: "take all the different hunter HUDs ... more like the prime remastered
    // 16:9 HUD. I mostly want to keep everything the same. the main thing is adding a mini map and radar"). Two pieces over
    // MphRead's own HUD, everything else as it was:
    //   - RADAR, top left, every hunter, adventure + multiplayer: a round instrument drawn in the hunter's own HUD colour
    //     (as Prime's grey-white lines are its visor's), facing up, a view cone, and MPH's own radar dots on it
    //     (the DS bottom screen's sprites: common/rad_radplyred for enemies and opponents, radar_octolithSMALL for
    //     octoliths, rad_NodesOG/RB for nodes, rad_key for pickups in a match). Who shows follows the game's rules: in adventure the enemies MphRead marks
    //     OnRadar and the other hunters; in a match the opponents when the RADAR rule is on, or when a camper is revealed,
    //     carries the octolith or is the Prime Hunter (the same test the bots use, PlayerAi Func3_213A9B8).
    //   - MINI MAP, top right, adventure only: the room Samus is in, from its collision (floors filled, their edges and
    //     ledges as lines, floors on other levels dimmer), turning with her so her facing is always up.
    // Draw only: reads the sim, never writes it. The pieces are textures made here and drawn through DrawHudObject like
    // the HUD's own sprites, so the Android HUD pass and the PC tools (-primehud) show the same. The map texture is
    // re-drawn as she turns or moves (the HUD's quads can't rotate).
    public partial class PlayerEntity
    {
        // RECOMP SETTINGS "hud" (PRIME / DS), set by the host for its own step (CampaignRenderer.SetPlayerHooks): any piece
        // drawn; the campaign's pieces apart (owner 2026-10-26: "campaign radar" its own setting, the mini map dev only)
        public static bool HostPrimeHud { get; set; }
        public static bool HostPrimeRadar { get; set; } = true;
        // the radar's pickup dots in a match (vanilla), off by the host's "radar pickups" setting
        public static bool HostPrimeRadarPickups { get; set; } = true;
        public static bool HostPrimeMiniMap { get; set; } = true;

        private const int _radarTexels = 96; // the radar texture's side
        private const float _radarSize = 38; // drawn diameter in DS pixels of the screen's height (mode 1)
        // vanilla's touch radar (arm9 0x2037518, owner 2026-10-26 "act just like the vanilla"): 3 px a world unit, the rim
        // at 41 px, nothing drawn from 64 px; flat distance only, no height test
        private const float _radarRimUnits = 41 / 3f; // world units at the rim
        private const float _radarReachUnits = 64 / 3f; // past the rim out to this a blip sits on the rim; further, none
        private const float _radarConeDegrees = 36; // half the view cone
        private const float _radarBlipScale = 0.55f;
        private const int _mapTexels = 96;
        private const float _mapSize = 38;
        private const float _mapRange = 22; // world units from the middle to the window's side
        private const int _map3DTexels = 192; // the 3D room's own texture: ~1 texel per screen pixel at 1080p, so thin parts keep their shape
        private const float _mapBaseTexelsPerUnit = 4; // the room's top-down picture (north up), sampled each frame
        private const int _mapBaseMaxSide = 1024;
        private const float _mapLevelHeight = 3; // floors further above / below than this are dimmer

        private HudObjectInstance? _radarFrameInst;
        private HudObjectInstance _radarBlipInst = null!;
        private HudObjectInstance _radarOctolithInst = null!;
        private HudObjectInstance _radarNodeInst = null!;
        private HudObjectInstance _radarPickupInst = null!;
        private HudObjectInstance _mapInst = null!;
        private HudObjectInstance _mapFrameInst = null!;
        private HudObjectInstance _map3DInst = null!;
        private bool _mapIs3D;
        private ColorRgba _primeHudColor;
        // the mini map's 3D room (owner 2026-10-14: "the 3d room floating that exists in the select screen map viewer", on
        // Metroid Prime 1's mini map rules): MphRecomp.Core's HudMiniMap paints the pause map's area model of the room into
        // the map texture (room name, position, the camera's forward, the time in seconds for its glide between rooms, paint
        // or only place the player, pixels, side; out: the player's spot in texels, still gliding); false = no map model for
        // this room (multiplayer arenas, the Oubliette, First Hunt) and the collision floor picture is drawn instead
        // roomPart = the room part the game has her in (NodeRef; its door-bounded pieces are areas of their own), null when
        // not known
        public delegate bool PrimeMapPainter(string room, string? roomPart, Vector3 position, Vector3 facing, float time,
            bool paint, ColorRgba[] pixels, int size, out Vector2 player, out bool gliding);
        public static PrimeMapPainter? HostPrimeMap { get; set; }
        // every frame, whatever the HUD style: the door-bounded piece she's in counts as entered (HudMiniMap.MarkPiece), so
        // the PRIME pause map shows the corridors/chambers she walked through with the mini map off too
        public delegate void PrimeMapPieceTracker(string room, string? roomPart, Vector3 position);
        public static PrimeMapPieceTracker? HostMapPieceSeen { get; set; }
        // the mini map's room made ahead, a step a call (HudMiniMap.Warm): step starts at 0 and is moved on; true = done
        public delegate bool PrimeMapWarmer(string room, ref int step);
        public static PrimeMapWarmer? HostPrimeMapWarm { get; set; }
        // the player marker (the pause map's), pointing up, filling a square texture; share = its width / the map's width.
        // Prime turns its marker with the camera's yaw, as the map, so on screen it always points up: a sprite drawn over
        // the map at the player's spot, pulsing in size (CAutoMapper: 0.75 to 1, sin(5t))
        public delegate bool PrimeMarkerPainter(ColorRgba[] pixels, int size, out float share);
        public static PrimeMarkerPainter? HostPrimeMarker { get; set; }
        private ColorRgba[] _mapRoom3D = new ColorRgba[_map3DTexels * _map3DTexels];
        private const int _mapMarkerTexels = 64;
        private HudObjectInstance? _mapMarkerInst;
        private float _mapMarkerShare;
        private Vector2 _mapPlayer;
        private bool _mapGliding;

        private bool _radarNodesTeams;
        private float _radarSizeNow = _radarSize;

        // the mini map's room picture: per texel the highest floor's height (NaN = no floor) and whether it's an edge
        private CollisionInstance? _mapRoom;
        private float[] _mapHeights = Array.Empty<float>();
        private bool[] _mapEdges = Array.Empty<bool>();
        private int _mapBaseW, _mapBaseH;
        private Vector2 _mapBaseMin;
        private Vector3 _mapLastPos = new Vector3(Single.NaN);
        private Vector2 _mapLastFacing;
        private int _mapFrameTick;

        // the radar's and map's middles and sizes per hunter, DS pixels (x of 256 across the width, y of 192 down the height,
        // size = diameter of 192 down): the match spots are the owner's, placed by hand on each 16:9 match HUD (radar
        // placement page, 2026-10-14; Trace and Sylux kept the first guesses); the campaign's are placed on its own HUD
        // (no POINTS, the mini map top right), also by the owner on the page
        private (float X, float Y, float Size) PrimeRadarSpot()
        {
            if (GameState.SinglePlayer)
            {
                return Hunter switch
                {
                    Hunter.Kanden => (37, 31.5f, _radarSize),
                    Hunter.Trace => (36, 50, _radarSize),
                    Hunter.Sylux => (30, 60, _radarSize),
                    Hunter.Noxus => (52.5f, 37, _radarSize),
                    Hunter.Spire => (39, 39.5f, _radarSize),
                    Hunter.Weavel => (61.5f, 51.5f, _radarSize),
                    _ => (25, 46, _radarSize),
                };
            }
            return Hunter switch
            {
                Hunter.Kanden => (37.5f, 42.5f, _radarSize),
                Hunter.Trace => (36, 50, _radarSize),
                Hunter.Sylux => (30, 60, _radarSize),
                Hunter.Noxus => (55, 49, _radarSize),
                Hunter.Spire => (41.5f, 53.5f, _radarSize),
                Hunter.Weavel => (61.5f, 51.5f, _radarSize),
                _ => (35.5f, 64, _radarSize),
            };
        }

        private (float X, float Y, float Size) PrimeMapSpot() => Hunter switch
        {
            Hunter.Kanden => (221, 32.5f, _mapSize),
            Hunter.Trace => (215.5f, 49, _mapSize),
            Hunter.Sylux => (224.5f, 57, _mapSize),
            Hunter.Noxus => (205, 39, _mapSize),
            Hunter.Spire => (209.5f, 43, _mapSize),
            Hunter.Weavel => (196.5f, 47.5f, _mapSize),
            _ => (229, 45.5f, _mapSize),
        };

        // PlayerHud.DrawHudObjects, with the HUD's other pieces (alive, no dialog pause)
        private void DrawPrimeHud()
        {
            if (_radarFrameInst == null)
            {
                SetUpPrimeHud();
            }
            if (!GameState.SinglePlayer || HostPrimeRadar)
            {
                DrawRadar();
            }
            if (GameState.SinglePlayer && HostPrimeMiniMap)
            {
                DrawMiniMap();
            }
        }

        private void SetUpPrimeHud()
        {
            _primeHudColor = PrimeHudLineColor();
            _radarFrameInst = new HudObjectInstance(_radarTexels, _radarTexels);
            FillRadarFrame(_radarFrameInst.Texture, _primeHudColor);
            _radarFrameInst.BindingId = _scene.BindGetTexture(_radarFrameInst.Texture, _radarTexels, _radarTexels);
            // Live: drawn from their own textures, not interned by content each frame (a new sprite per map repaint)
            _radarFrameInst.Live = true;
            _mapFrameInst = new HudObjectInstance(_mapTexels, _mapTexels);
            FillMapFrame(_mapFrameInst.Texture, _primeHudColor);
            _mapFrameInst.BindingId = _scene.BindGetTexture(_mapFrameInst.Texture, _mapTexels, _mapTexels);
            _mapFrameInst.Live = true;
            _mapInst = new HudObjectInstance(_mapTexels, _mapTexels);
            _mapInst.BindingId = _scene.BindGetTexture(_mapInst.Texture, _mapTexels, _mapTexels);
            _mapInst.Live = true;
            _map3DInst = new HudObjectInstance(_map3DTexels, _map3DTexels);
            _map3DInst.BindingId = _scene.BindGetTexture(_map3DInst.Texture, _map3DTexels, _map3DTexels);
            _map3DInst.Live = true;
            _radarBlipInst = NewRadarSprite(@"_archives\common\rad_radplyred.bin");
            _radarOctolithInst = NewRadarSprite(@"_archives\commonMP\radar_octolithSMALL.bin");
            _radarPickupInst = NewRadarSprite(@"_archives\common\rad_key.bin");
            _radarNodesTeams = GameState.Teams;
            _radarNodeInst = NewRadarSprite(_radarNodesTeams ? HudElements.NodesOG : HudElements.NodesRB);
        }

        private HudObjectInstance NewRadarSprite(string file)
        {
            HudObject obj = HudInfo.GetHudObject(file);
            var inst = new HudObjectInstance(obj.Width, obj.Height);
            inst.SetCharacterData(obj.CharacterData, _scene);
            inst.SetPaletteData(obj.PaletteData, _scene);
            inst.Center = true;
            inst.Enabled = true;
            return inst;
        }

        // the hunter's HUD colour, standing in for Prime's grey-white lines: the main colour of its energy bar (the most
        // used bright colour of hud_energybar's first palette: Samus green, Kanden yellow, Trace red, Sylux green, Noxus
        // blue, Spire orange, Weavel gold). The visor layer's own most used colour isn't reliable (Trace's is a pale glass
        // cyan), nor is the text palette's brightest (pastel, Trace cyan); the HUD text's is the fallback.
        private ColorRgba PrimeHudLineColor()
        {
            HudObject bar = HudInfo.GetHudObject(_hudObjects.HealthBarA);
            var counts = new int[16];
            foreach (byte index in bar.CharacterData)
            {
                if (index > 0 && index < 16)
                {
                    counts[index]++;
                }
            }
            int best = -1;
            for (int i = 1; i < 16 && i < bar.PaletteData.Count; i++)
            {
                ColorRgba c = bar.PaletteData[i];
                if (counts[i] > 0 && Math.Max(c.Red, Math.Max(c.Green, c.Blue)) >= 140
                    && (best == -1 || counts[i] > counts[best]))
                {
                    best = i;
                }
            }
            if (best != -1)
            {
                ColorRgba c = bar.PaletteData[best];
                return new ColorRgba(c.Red, c.Green, c.Blue, 255);
            }
            return DroneDialHudColor();
        }

        private float PrimeHudPosX(float x) => (x + _objShiftX) / 256f;
        private float PrimeHudPosY(float y) => (y + _objShiftY) / 192f;

        // ---------------------------------------------------------------- radar

        private void DrawRadar()
        {
            (float cx, float cy, float size) = PrimeRadarSpot();
            _radarSizeNow = size;
            HudObjectInstance frame = _radarFrameInst!;
            frame.Center = true;
            frame.Enabled = true;
            frame.Scale = size / _radarTexels;
            frame.PositionX = PrimeHudPosX(cx);
            frame.PositionY = PrimeHudPosY(cy);
            frame.Alpha = Features.HudOpacity;
            _scene.DrawHudObject(frame, mode: 1);
            (Vector2 forward, Vector2 right) = PrimeHudAxes();
            if (!GameState.SinglePlayer && HostPrimeRadarPickups)
            {
                // vanilla 0x2037da8, gated on 0x20df044 bit 0 (set by every multiplayer mode's HUD, never in adventure):
                // every pickup lying in the arena, rad_key's 0 inside the rim, its 1 on the rim
                foreach (ItemInstanceEntity item in _scene.GetItemInstanceEntities())
                {
                    DrawRadarBlip(_radarPickupInst, item.Position, cx, cy, forward, right, frame: 0, rimFrame: 1);
                }
            }
            if (GameState.SinglePlayer)
            {
                // vanilla 0x20378C8: alive, visible and on radar; rad_radplyred's bold cross (3) inside the rim, its dot with
                // corner marks (2) on the rim
                foreach (EnemyInstanceEntity enemy in _scene.GetEnemyInstanceEntities())
                {
                    if (enemy.Health > 0 && enemy.Flags.TestFlag(EnemyFlags.Visible)
                        && enemy.Flags.TestFlag(EnemyFlags.OnRadar))
                    {
                        DrawRadarBlip(_radarBlipInst, enemy.Position, cx, cy, forward, right, frame: 3, rimFrame: 2);
                    }
                }
            }
            else if (GameState.Mode == GameMode.Capture || GameState.Mode == GameMode.Bounty
                || GameState.Mode == GameMode.BountyTeams)
            {
                bool bounty = GameState.Mode != GameMode.Capture;
                foreach (OctolithFlagEntity flag in _scene.GetOctolithFlagEntities())
                {
                    // vanilla 0x2037650: radar_octolithSMALL 0 grey, or with teams 3 orange / 4 green by the flag's team;
                    // the same frame carried or not, and on the rim
                    int sprite = bounty ? 0 : flag.Data.TeamId == 0 ? 3 : 4;
                    DrawRadarBlip(_radarOctolithInst, flag.Position, cx, cy, forward, right, sprite, rimFrame: sprite);
                }
            }
            else if (GameState.Mode == GameMode.Nodes || GameState.Mode == GameMode.NodesTeams)
            {
                if (_radarNodesTeams != GameState.Teams)
                {
                    _radarNodesTeams = GameState.Teams;
                    _radarNodeInst = NewRadarSprite(_radarNodesTeams ? HudElements.NodesOG : HudElements.NodesRB);
                }
                foreach (NodeDefenseEntity defense in _scene.GetNodeDefenseEntities())
                {
                    if (defense.Type == EntityType.NodeDefense)
                    {
                        int nodeFrame = RadarNodeFrame(defense);
                        DrawRadarBlip(_radarNodeInst, defense.Position, cx, cy, forward, right, nodeFrame,
                            rimFrame: nodeFrame);
                    }
                }
            }
            foreach (PlayerEntity player in _scene.GetPlayerEntities())
            {
                if (player == this || player.Health == 0 || !player.LoadFlags.TestFlag(LoadFlags.Active))
                {
                    continue;
                }
                // vanilla 0x2037B44: every other hunter, teammates too
                if (!GameState.SinglePlayer)
                {
                    if (!GameState.RadarPlayers && !player.Flags2.TestFlag(PlayerFlags2.RadarReveal)
                        && player.OctolithFlag == null && !player.IsPrimeHunter && !player.SquadMarkSeenBy(this)) // (+ Squad Mark)
                    {
                        continue;
                    }
                }
                // rad_radplyred's hollow ring (0) inside the rim, its dot with corner marks (2) on the rim
                DrawRadarBlip(_radarBlipInst, player.Position, cx, cy, forward, right, frame: 0, rimFrame: 2);
            }
        }

        // the view's forward and right on the ground (x, z)
        // the screen's view, not the body's: in alt form and while dead the camera trails or orbits the player and its yaw
        // drifts from _facingVector (the radar's sides came out flipped vs the view for Noxus / Spire in -primehud)
        private (Vector2 Forward, Vector2 Right) PrimeHudAxes()
        {
            var forward = new Vector2(CameraInfo.Facing.X, CameraInfo.Facing.Z);
            if (forward.LengthSquared < 1e-6f)
            {
                forward = new Vector2(_facingVector.X, _facingVector.Z);
            }
            forward = forward.LengthSquared > 1e-6f ? forward.Normalized() : new Vector2(0, -1);
            return (forward, new Vector2(-forward.Y, forward.X));
        }

        // vanilla: inside the rim at its place as frame, out to the reach on the rim as rimFrame, past that not drawn
        private void DrawRadarBlip(HudObjectInstance inst, Vector3 position, float cx, float cy, Vector2 forward,
            Vector2 right, int frame, int rimFrame)
        {
            Vector3 rel = position - Position;
            var flat = new Vector2(rel.X, rel.Z);
            float x = Vector2.Dot(flat, right);
            float y = Vector2.Dot(flat, forward);
            float dist = MathF.Sqrt(x * x + y * y);
            if (dist >= _radarReachUnits)
            {
                return;
            }
            if (dist > _radarRimUnits)
            {
                x *= _radarRimUnits / dist;
                y *= _radarRimUnits / dist;
                frame = rimFrame;
            }
            float rim = 1 - 5f / _radarTexels * 2; // inside the ring
            x *= rim / _radarRimUnits;
            y *= rim / _radarRimUnits;
            // mode 1 sizes by the screen's height (radius in DS pixels of 192); positions are fractions of width / height
            float radius = _radarSizeNow / 2;
            float aspect = _scene.Size.X > 0 && _scene.Size.Y > 0 ? _scene.Size.X / (float)_scene.Size.Y : 16 / 9f;
            inst.PositionX = PrimeHudPosX(cx) + x * radius / 192f / aspect;
            inst.PositionY = PrimeHudPosY(cy) - y * radius / 192f;
            inst.Scale = _radarBlipScale;
            inst.Alpha = Features.HudOpacity;
            inst.SetIndex(frame, _scene);
            _scene.DrawHudObject(inst, mode: 1);
        }

        // PlayerHud.DrawNodesIcons's frame for a node (rad_NodesOG/RB)
        private int RadarNodeFrame(NodeDefenseEntity defense)
        {
            if (defense.CurrentTeam == NodeDefenseEntity.NoTeam)
            {
                if (!defense.Blinking)
                {
                    return 0;
                }
                if (GameState.Teams)
                {
                    return defense.OccupyingTeam == 0 ? 2 : 4;
                }
                return defense.OccupyingTeam == TeamIndex ? 4 : 2;
            }
            if (GameState.Teams)
            {
                return (defense.Blinking ? defense.OccupyingTeam : defense.CurrentTeam) == 0 ? 2 : 4;
            }
            if (defense.CurrentTeam == TeamIndex)
            {
                return !defense.Blinking || defense.OccupyingTeam == TeamIndex ? 4 : 2;
            }
            return defense.Blinking && defense.OccupyingTeam == TeamIndex ? 4 : 2;
        }

        private static void FillRadarFrame(ColorRgba[] pixels, ColorRgba line)
        {
            const int size = _radarTexels;
            const float center = (size - 1) / 2f;
            const float outer = size / 2f - 1.5f;
            const float ringWidth = 3;
            float coneCos = MathF.Cos(MathHelper.DegreesToRadians(_radarConeDegrees));
            var black = new ColorRgba(0, 0, 0, 255);
            for (int py = 0; py < size; py++)
            {
                for (int px = 0; px < size; px++)
                {
                    float dx = px - center;
                    float dy = py - center;
                    float dist = MathF.Sqrt(dx * dx + dy * dy);
                    // clockwise from the top, degrees
                    float angle = MathHelper.RadiansToDegrees(MathF.Atan2(dx, -dy));
                    if (angle < 0)
                    {
                        angle += 360;
                    }
                    var layers = new Layering();
                    // the dark glass behind
                    layers.Add(black, 0.38f * Coverage(outer - ringWidth / 2 - dist));
                    // the view cone: a soft fill and its two edges
                    if (dist > 0.5f)
                    {
                        float cos = -dy / dist;
                        float inside = Coverage((cos - coneCos) * dist);
                        float fade = 0.75f + 0.25f * (1 - dist / outer);
                        layers.Add(line, 0.16f * inside * fade * Coverage(outer - ringWidth - dist));
                        float edgeAngle = MathF.Abs(MathF.Abs(angle > 180 ? 360 - angle : angle) - _radarConeDegrees);
                        float edgeDist = MathF.Sin(MathHelper.DegreesToRadians(edgeAngle)) * dist;
                        if (edgeAngle < 30)
                        {
                            layers.Add(line, 0.55f * Coverage(0.75f - edgeDist) * Coverage(outer - ringWidth - dist));
                        }
                    }
                    // the half-range ring
                    float half = outer / 2;
                    layers.Add(line, 0.35f * Coverage(0.6f - MathF.Abs(dist - half)));
                    // the cardinal ticks inside the rim
                    float tickAngle = angle % 90;
                    tickAngle = MathF.Min(tickAngle, 90 - tickAngle);
                    float tickSide = MathF.Sin(MathHelper.DegreesToRadians(tickAngle)) * dist;
                    if (tickAngle < 20)
                    {
                        float tick = Coverage(1.0f - tickSide) * Coverage(dist - (outer - ringWidth - 6))
                            * Coverage(outer - ringWidth + 0.5f - dist);
                        layers.Add(line, 0.85f * tick);
                    }
                    // the rim: MPH's broken ring (its reticles' look), gaps at the diagonals, a dark edge outside
                    float gap = MathF.Abs(((angle + 45) % 90) - 45); // 0 at a diagonal
                    float open = Coverage(gap - 7);
                    layers.Add(black, 0.5f * open * Coverage(outer + 1 - dist) * Coverage(dist - (outer - ringWidth - 1)));
                    layers.Add(line, open * Coverage(outer - dist) * Coverage(dist - (outer - ringWidth)));
                    // Samus: a small arrow in the middle, pointing the way she faces
                    layers.Add(line, ArrowCover(dx, dy, 6f));
                    pixels[py * size + px] = layers.Result;
                }
            }
        }

        // an upward arrow head around the middle, size = half its height in texels
        private static float ArrowCover(float dx, float dy, float size)
        {
            // triangle: tip (0, -size), base corners (+-0.8 size, +size)
            float y = dy + size; // 0 at the tip, 2 size at the base
            if (y < -1 || y > 2 * size + 1)
            {
                return 0;
            }
            float halfWidth = 0.75f * size * Math.Clamp(y, 0, 2 * size) / (2 * size);
            // a shallow notch in the base, MPH's arrow shape
            float bottom = 2 * size - y - MathF.Max(0, size * 0.6f - MathF.Abs(dx) * 0.8f);
            return Coverage(halfWidth - MathF.Abs(dx)) * Coverage(y) * Coverage(bottom);
        }

        // ---------------------------------------------------------------- mini map

        // the room part her NodeRef names: the room model's node whose children the ref points at (null in a door's
        // connector or before she has one); cached per room
        private string? MapRoomPart(string roomName)
        {
            MphRead.Formats.Culling.NodeRef nodeRef = NodeRef;
            if (nodeRef.PartIndex < 0 || nodeRef.ModelIndex != 0)
            {
                return null;
            }
            if (_mapPartRoom != roomName)
            {
                _mapPartRoom = roomName;
                _mapPartNames.Clear();
            }
            if (!_mapPartNames.TryGetValue(nodeRef.NodeIndex, out string? name))
            {
                name = null;
                try
                {
                    foreach (Node node in Read.GetRoomModelInstance(roomName).Model.Nodes)
                    {
                        if (node.ChildIndex == nodeRef.NodeIndex)
                        {
                            name = node.Name;
                            break;
                        }
                    }
                }
                catch (Exception)
                {
                    name = null;
                }
                _mapPartNames[nodeRef.NodeIndex] = name;
            }
            return name;
        }

        // PlayerProcess.ProcessPlayer, every frame: the campaign player's piece, seen whether or not the mini map is drawn;
        // and the HUD made ahead while it isn't shown yet (the landing's intro camera)
        private void PrimeMapPieceFrame()
        {
            if (HostMapPieceSeen != null && GameState.SinglePlayer && this == Main && _scene.Room?.Meta?.Name is string roomName)
            {
                HostMapPieceSeen(roomName, MapRoomPart(roomName), Position);
            }
            PrimeHudWarmStep();
        }

        // The radar and mini map's first show did all their set-up in one frame (2026-10-15, measured at 36-47 ms
        // on the Odin at control start): the textures, the room's map (its area's model loading on a worker), framing,
        // doors and the first paint are made a step a frame ahead instead -- during the landing movie (WarmPrimeHud, from
        // the campaign renderer while the game waits) and the frames before the HUD shows. A room the HUD already draws is
        // left to DrawMiniMap.
        private CollisionInstance? _warmRoom;
        private int _warmStage, _warmMapStep;
        private const int _warmDone = 100;
        private bool _mapBaseDue;

        public static void WarmPrimeHud()
        {
            if (Main is PlayerEntity main && main.LoadFlags.TestFlag(LoadFlags.Active))
            {
                main.PrimeHudWarmStep();
            }
        }

        private void PrimeHudWarmStep()
        {
            if (!HostPrimeHud || !GameState.SinglePlayer || this != Main || _hudObjects == null)
            {
                return;
            }
            CollisionInstance? room = _scene.Room is { RoomCollision.Count: > 0 } r ? r.RoomCollision[0] : null;
            if (room == null || _scene.Room?.Meta?.Name is not string roomName)
            {
                return;
            }
            if (room != _warmRoom)
            {
                _warmRoom = room;
                _warmStage = _warmMapStep = 0;
            }
            switch (_warmStage)
            {
            case 0:
                if (_radarFrameInst == null)
                {
                    SetUpPrimeHud();
                }
                _warmStage = HostPrimeMiniMap ? _warmStage + 1 : _warmDone; // the radar alone: nothing more to make
                break;
            case 1:
                SetUpMapMarker();
                _warmStage++;
                break;
            case 2:
                if (HostPrimeMapWarm == null || HostPrimeMapWarm(roomName, ref _warmMapStep))
                {
                    _warmStage++;
                }
                break;
            case 3:
                // the first paint, as DrawMiniMap's: it then repaints only if the view turns before the HUD shows
                (Vector2 forward, Vector2 _) = PrimeHudAxes();
                _mapRoom = room;
                _mapBaseDue = true;
                bool gliding = false;
                _mapIs3D = HostPrimeMap != null && HostPrimeMap(roomName, MapRoomPart(roomName), Position,
                    new Vector3(forward.X, 0, forward.Y), _scene.GlobalElapsedTime, true, _mapRoom3D, _map3DTexels,
                    out _mapPlayer, out gliding);
                if (_mapIs3D)
                {
                    Fill3DMap();
                    _scene.BindTexture(_map3DInst.Texture, _map3DTexels, _map3DTexels, _map3DInst.BindingId);
                    _mapLastPos = Position;
                    _mapLastFacing = forward;
                    _mapGliding = gliding; // a glide from the last room goes on in DrawMiniMap
                }
                else
                {
                    _mapLastPos = new Vector3(Single.NaN);
                }
                _warmStage = _warmDone;
                break;
            }
        }

        private string? _mapPartRoom;
        private readonly Dictionary<int, string?> _mapPartNames = new Dictionary<int, string?>();

        private void DrawMiniMap()
        {
            // the room's own collision (index 0; connectors follow it): a new one = a new room (or a reload)
            CollisionInstance? room = _scene.Room is { RoomCollision.Count: > 0 } r ? r.RoomCollision[0] : null;
            if (room != _mapRoom)
            {
                _mapRoom = room;
                _mapBaseDue = true; // the floor picture, built only for a room without the 3D map
                _mapLastPos = new Vector3(Single.NaN);
                _warmRoom = room; // drawn now: nothing left to warm
                _warmStage = _warmDone;
            }
            (float cx, float cy, float size) = PrimeMapSpot();
            (Vector2 forward, Vector2 right) = PrimeHudAxes();
            // the 3D room (Prime's mini map): framed on the room, so it's repainted when the view turns, the room changes or
            // the glide between rooms runs, at most every other frame (~1-2 ms of CPU); the player is a sprite moved over it
            _mapFrameTick++;
            string? roomName = _scene.Room?.Meta?.Name;
            if (HostPrimeMap != null && roomName != null)
            {
                bool repaint = (_mapLastFacing != forward || _mapGliding || Single.IsNaN(_mapLastPos.X))
                    && ((_mapFrameTick & 1) == 0 || !_mapIs3D);
                _mapIs3D = HostPrimeMap(roomName, MapRoomPart(roomName), Position, new Vector3(forward.X, 0, forward.Y),
                    _scene.GlobalElapsedTime, repaint, _mapRoom3D, _map3DTexels, out _mapPlayer, out bool gliding);
                // an area change (walking into a connector) repaints on its own and reports the glide it starts
                bool changed = _mapIs3D && gliding && !_mapGliding;
                _mapGliding = _mapIs3D && gliding;
                if (_mapIs3D && (repaint || changed))
                {
                    _mapLastPos = Position;
                    _mapLastFacing = forward;
                    Fill3DMap();
                    _scene.BindTexture(_map3DInst.Texture, _map3DTexels, _map3DTexels, _map3DInst.BindingId);
                }
            }
            else
            {
                _mapIs3D = false;
            }
            if (!_mapIs3D && (_mapBaseDue || _mapLastPos != Position || _mapLastFacing != forward))
            {
                if (_mapBaseDue)
                {
                    _mapBaseDue = false;
                    BuildMapBase();
                }
                _mapLastPos = Position;
                _mapLastFacing = forward;
                FillMap(forward, right);
                _scene.BindTexture(_mapInst.Texture, _mapTexels, _mapTexels, _mapInst.BindingId);
            }
            // the 3D room floats alone; the frame (and its arrow) only goes round the flat floor picture
            foreach (HudObjectInstance inst in _mapIs3D ? new[] { _map3DInst } : new[] { _mapInst, _mapFrameInst })
            {
                inst.Center = true;
                inst.Enabled = true;
                inst.Scale = size / (inst == _map3DInst ? _map3DTexels : _mapTexels);
                inst.PositionX = PrimeHudPosX(cx);
                inst.PositionY = PrimeHudPosY(cy);
                inst.Alpha = Features.HudOpacity;
                _scene.DrawHudObject(inst, mode: 1);
            }
            if (_mapIs3D)
            {
                DrawMapMarker(cx, cy, size);
            }
        }

        // the marker's texture (the pause map's marker model, painted once)
        private void SetUpMapMarker()
        {
            if (_mapMarkerInst != null)
            {
                return;
            }
            _mapMarkerInst = new HudObjectInstance(_mapMarkerTexels, _mapMarkerTexels);
            if (HostPrimeMarker == null || !HostPrimeMarker(_mapMarkerInst.Texture, _mapMarkerTexels, out _mapMarkerShare))
            {
                _mapMarkerShare = 0;
            }
            _mapMarkerInst.BindingId = _scene.BindGetTexture(_mapMarkerInst.Texture, _mapMarkerTexels, _mapMarkerTexels);
            _mapMarkerInst.Live = true;
        }

        // the player marker over the 3D room: at the player's spot, pulsing like Prime's (CAutoMapper: scale x (0.75 +
        // 0.25 p), p = (1 + sin(5t - pi/2)) / 2, about every 1.26 s)
        private void DrawMapMarker(float cx, float cy, float size)
        {
            SetUpMapMarker();
            if (_mapMarkerShare <= 0)
            {
                return;
            }
            float p = (1 + MathF.Sin(5 * _scene.GlobalElapsedTime - MathF.PI / 2)) / 2;
            float width = _mapMarkerShare * size * (0.75f + 0.25f * p);
            _mapMarkerInst.Center = true;
            _mapMarkerInst.Enabled = true;
            _mapMarkerInst.Scale = width / _mapMarkerTexels;
            // mode 1 sizes by the screen's height but places by fractions of width / height (as the radar blips): the
            // offset across is in height units over the screen's aspect, else it stretches 4:3 -> 16:9 and the marker
            // slides off the room whenever it lies to her side
            float aspect = _scene.Size.X > 0 && _scene.Size.Y > 0 ? _scene.Size.X / (float)_scene.Size.Y : 16 / 9f;
            _mapMarkerInst.PositionX = PrimeHudPosX(cx) + (_mapPlayer.X / _map3DTexels - 0.5f) * size / 192f / aspect;
            _mapMarkerInst.PositionY = PrimeHudPosY(cy + (_mapPlayer.Y / _map3DTexels - 0.5f) * size);
            _mapMarkerInst.Alpha = Features.HudOpacity;
            _scene.DrawHudObject(_mapMarkerInst, mode: 1);
        }

        // the room's floors from above (north up): every floor face of the room's own collision, the highest at each texel
        private void BuildMapBase()
        {
            _mapBaseW = _mapBaseH = 0;
            if (_scene.Room is not RoomEntity roomEntity || roomEntity.RoomCollision.Count == 0
                || roomEntity.RoomCollision[0].Info is not MphCollisionInfo info)
            {
                return;
            }
            Vector3 translation = roomEntity.RoomCollision[0].Translation;
            var floors = new List<Vector3[]>();
            var min = new Vector2(Single.MaxValue);
            var max = new Vector2(Single.MinValue);
            foreach (CollisionData data in info.Data)
            {
                if (data.IgnorePlayers || info.Planes[data.PlaneIndex].Y < 0.6f)
                {
                    continue;
                }
                var verts = new Vector3[data.PointIndexCount];
                for (int j = 0; j < verts.Length; j++)
                {
                    verts[j] = info.Points[info.PointIndices[data.PointStartIndex + j]] + translation;
                    min = Vector2.ComponentMin(min, verts[j].Xz);
                    max = Vector2.ComponentMax(max, verts[j].Xz);
                }
                floors.Add(verts);
            }
            if (floors.Count == 0)
            {
                return;
            }
            float tpu = _mapBaseTexelsPerUnit;
            Vector2 extent = max - min;
            tpu = MathF.Min(tpu, (_mapBaseMaxSide - 4) / MathF.Max(extent.X, extent.Y));
            _mapBaseTexelsPerUnitNow = tpu;
            _mapBaseMin = min - new Vector2(2 / tpu);
            int w = (int)MathF.Ceiling(extent.X * tpu) + 4;
            int h = (int)MathF.Ceiling(extent.Y * tpu) + 4;
            _mapBaseW = w;
            _mapBaseH = h;
            _mapHeights = new float[w * h];
            Array.Fill(_mapHeights, Single.NaN);
            foreach (Vector3[] verts in floors)
            {
                RasterFloor(verts, tpu);
            }
            // edges: a floor texel next to no floor, or next to a floor more than a step higher / lower (a ledge)
            _mapEdges = new bool[w * h];
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    float here = _mapHeights[y * w + x];
                    if (Single.IsNaN(here))
                    {
                        continue;
                    }
                    bool edge = false;
                    for (int n = 0; n < 4 && !edge; n++)
                    {
                        int nx = x + (n == 0 ? 1 : n == 1 ? -1 : 0);
                        int ny = y + (n == 2 ? 1 : n == 3 ? -1 : 0);
                        float there = nx < 0 || ny < 0 || nx >= w || ny >= h ? Single.NaN : _mapHeights[ny * w + nx];
                        edge = Single.IsNaN(there) || there < here - 1.2f;
                    }
                    _mapEdges[y * w + x] = edge;
                }
            }
        }

        private float _mapBaseTexelsPerUnitNow = _mapBaseTexelsPerUnit;

        // scanline fill of one convex-or-not floor polygon into the height map (texel centres inside)
        private void RasterFloor(Vector3[] verts, float tpu)
        {
            int w = _mapBaseW, h = _mapBaseH;
            float minY = Single.MaxValue, maxY = Single.MinValue;
            var pts = new Vector2[verts.Length];
            for (int i = 0; i < verts.Length; i++)
            {
                pts[i] = (verts[i].Xz - _mapBaseMin) * tpu;
                minY = MathF.Min(minY, pts[i].Y);
                maxY = MathF.Max(maxY, pts[i].Y);
            }
            // the floor's height at a texel: the plane through the first three points (y up)
            Vector3 a = verts[0], b = verts[1], c = verts[2];
            Vector3 normal = Vector3.Cross(b - a, c - a);
            bool flat = MathF.Abs(normal.Y) < 1e-6f;
            int y0 = Math.Max(0, (int)MathF.Floor(minY)), y1 = Math.Min(h - 1, (int)MathF.Ceiling(maxY));
            Span<float> xs = stackalloc float[16];
            for (int y = y0; y <= y1; y++)
            {
                float sy = y + 0.5f;
                int count = 0;
                for (int i = 0; i < pts.Length && count < xs.Length; i++)
                {
                    Vector2 p = pts[i], q = pts[(i + 1) % pts.Length];
                    if ((p.Y <= sy && q.Y > sy) || (q.Y <= sy && p.Y > sy))
                    {
                        xs[count++] = p.X + (sy - p.Y) / (q.Y - p.Y) * (q.X - p.X);
                    }
                }
                xs[..count].Sort();
                for (int k = 0; k + 1 < count; k += 2)
                {
                    int x0 = Math.Max(0, (int)MathF.Ceiling(xs[k] - 0.5f));
                    int x1 = Math.Min(w - 1, (int)MathF.Floor(xs[k + 1] - 0.5f));
                    for (int x = x0; x <= x1; x++)
                    {
                        float height = a.Y;
                        if (!flat)
                        {
                            float wx = (x + 0.5f) / tpu + _mapBaseMin.X;
                            float wz = sy / tpu + _mapBaseMin.Y;
                            height = a.Y - (normal.X * (wx - a.X) + normal.Z * (wz - a.Z)) / normal.Y;
                        }
                        ref float cell = ref _mapHeights[y * w + x];
                        if (Single.IsNaN(cell) || height > cell)
                        {
                            cell = height;
                        }
                    }
                }
            }
        }

        // Prime's HUD map: the 3D room floating on its own, no window or frame, fading out near the texture's edge so a big
        // room isn't cut square
        private void Fill3DMap()
        {
            const int side = _map3DTexels;
            const float middle = (side - 1) / 2f;
            const float fadeEnd = side / 2f - 1;
            const float fadeWidth = side / 10f;
            ColorRgba[] room = _map3DInst.Texture;
            for (int py = 0; py < side; py++)
            {
                for (int px = 0; px < side; px++)
                {
                    float dx = px - middle;
                    float dy = py - middle;
                    float fade = Math.Clamp((fadeEnd - MathF.Sqrt(dx * dx + dy * dy)) / fadeWidth, 0, 1);
                    ColorRgba texel = _mapRoom3D[py * side + px];
                    float alpha = texel.Alpha / 255f * fade * fade * (3 - 2 * fade);
                    room[py * side + px] = new ColorRgba(texel.Red, texel.Green, texel.Blue, (byte)MathF.Round(alpha * 255));
                }
            }
        }

        // the map window: the room picture turned so the view's forward is up, Samus in the middle
        private void FillMap(Vector2 forward, Vector2 right)
        {
            const int size = _mapTexels;
            const float center = (size - 1) / 2f;
            ColorRgba[] pixels = _mapInst.Texture;
            ColorRgba line = _primeHudColor;
            var black = new ColorRgba(0, 0, 0, 255);
            float unitsPerTexel = _mapRange / (size / 2f);
            float tpu = _mapBaseTexelsPerUnitNow;
            for (int py = 0; py < size; py++)
            {
                for (int px = 0; px < size; px++)
                {
                    float dx = px - center;
                    float dy = py - center;
                    float window = MapWindowCover(dx, dy, inset: 3);
                    if (window <= 0)
                    {
                        pixels[py * size + px] = new ColorRgba(0, 0, 0, 0);
                        continue;
                    }
                    var layers = new Layering();
                    layers.Add(black, 0.38f * window);
                    if (_mapBaseW > 0)
                    {
                        // screen right = the view's right, screen up = forward
                        Vector2 world = Position.Xz + (right * dx - forward * dy) * unitsPerTexel;
                        int bx = (int)MathF.Floor((world.X - _mapBaseMin.X) * tpu);
                        int by = (int)MathF.Floor((world.Y - _mapBaseMin.Y) * tpu);
                        if (bx >= 0 && by >= 0 && bx < _mapBaseW && by < _mapBaseH)
                        {
                            float height = _mapHeights[by * _mapBaseW + bx];
                            if (!Single.IsNaN(height))
                            {
                                bool level = MathF.Abs(height - Position.Y) < _mapLevelHeight
                                    || height < Position.Y && height > Position.Y - _mapLevelHeight * 2;
                                bool edge = _mapEdges[by * _mapBaseW + bx];
                                float alpha = edge ? (level ? 0.95f : 0.4f) : (level ? 0.3f : 0.12f);
                                layers.Add(line, alpha * window);
                            }
                        }
                    }
                    pixels[py * size + px] = layers.Result;
                }
            }
        }

        // the map window's shape: a square with cut corners (MPH's chamfered boxes), inset texels in from the frame
        private static float MapWindowCover(float dx, float dy, float inset)
        {
            float half = _mapTexels / 2f - 1.5f - inset;
            float cut = _mapTexels * 0.16f;
            float ax = MathF.Abs(dx), ay = MathF.Abs(dy);
            float box = MathF.Min(half - ax, half - ay);
            float corner = (2 * half - cut - ax - ay) / MathF.Sqrt(2);
            return Coverage(MathF.Min(box, corner));
        }

        private static void FillMapFrame(ColorRgba[] pixels, ColorRgba line)
        {
            const int size = _mapTexels;
            const float center = (size - 1) / 2f;
            const float width = 3;
            var black = new ColorRgba(0, 0, 0, 255);
            for (int py = 0; py < size; py++)
            {
                for (int px = 0; px < size; px++)
                {
                    float dx = px - center;
                    float dy = py - center;
                    var layers = new Layering();
                    float outer = MapWindowCover(dx, dy, inset: 0);
                    float inner = MapWindowCover(dx, dy, inset: width);
                    float outline = MapWindowCover(dx, dy, inset: -1);
                    float innerDark = MapWindowCover(dx, dy, inset: width + 1);
                    // the frame, broken in the middle of each side (MPH's boxes have gaps like its reticles)
                    float ax = MathF.Abs(dx), ay = MathF.Abs(dy);
                    float open = Coverage(MathF.Min(ax, ay) - 7);
                    layers.Add(black, 0.5f * open * (outline - innerDark));
                    layers.Add(line, open * (outer - inner));
                    // Samus in the middle
                    layers.Add(line, ArrowCover(dx, dy, 6f));
                    pixels[py * size + px] = layers.Result;
                }
            }
        }

        // "over" compositing of a few coloured layers into one straight-alpha texel
        private struct Layering
        {
            private float _r, _g, _b, _a;

            public void Add(ColorRgba color, float alpha)
            {
                alpha = Math.Clamp(alpha, 0, 1);
                if (alpha <= 0)
                {
                    return;
                }
                float outA = alpha + _a * (1 - alpha);
                _r = (color.Red * alpha + _r * _a * (1 - alpha)) / outA;
                _g = (color.Green * alpha + _g * _a * (1 - alpha)) / outA;
                _b = (color.Blue * alpha + _b * _a * (1 - alpha)) / outA;
                _a = outA;
            }

            public readonly ColorRgba Result => new ColorRgba((byte)MathF.Round(_r), (byte)MathF.Round(_g),
                (byte)MathF.Round(_b), (byte)MathF.Round(_a * 255));
        }
    }
}
