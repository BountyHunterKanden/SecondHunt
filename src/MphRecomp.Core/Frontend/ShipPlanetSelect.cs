using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using MphRead;
using MphRead.Text;
using MphRecomp.Frontend.Redraw;

// The gunship's PLANET SELECT (vanilla: ShipInSpace state 1 -- overlay9_0 enter 0x2104a5c, process 0x2104094, draw
// 0x2103d2c; decoded 2026-10-01, handoffs/start-menu-ui-rebuild_2026-10-01_0103-files/re3_planetselect/spec.md) as ONE
// 16:9 screen: the top screen's cockpit (the deep-space view through the window is the host's 3D -- DeepSpaceView,
// drawn first and moved by CameraX/Y; the planet banner; the zoom panel), with the touch screen's star map, sliders and
// LAND SHIP rebuilt into the console strip below the window.
//
// The map keeps its vanilla proportions (owner, 2026-10-01: the crosshair drives the window's view, so the map must not
// be stretched): the whole touch-screen layout is scaled by one factor, MapScale. Everything runs in the DS touch
// screen's own pixels -- crosshair X 89..238 / Y 22..130, planets at their sprite positions, the hit rects -- and only
// drawing maps them into the strip. The grid and the radar arcs are drawn as clean lines from the ROM's own map
// (shipSpace/ship_map: 20 px grid, arcs of radius 54/77/100/121/151 about the beacon at (92, 30)); sprites are the ROM's.
//
// Vanilla flow (game logic at 30 Hz), every entry: the radar sweep (an arc every 3 ticks from tick 15), the planets
// already known fade in; a planet found since the last visit gets the "scanning ALIMBIC CLUSTER" box, then fades in and
// the crosshair glides onto it (3 px a tick per axis). Then the crosshair: D-pad 4 px a tick, the two sliders (dragged
// from where they're grabbed); a planet under it is selected (box, PRE_SELECT; once not dragging the box blinks and
// LAND_SHIP_SCR plays) and its name shows in the banner. The window's camera follows the crosshair on a spring; once it's
// within 32 units the zoom panel and LAND SHIP come up. LAND SHIP (tap or A, no confirm): the corners, then Land.
// Added here (owner, 2026-10-01): tap a planet to select it (the nearest within reach; the crosshair glides there), and
// drag on the map to move the crosshair. Not yet: the enemy panel (hunters on the planet, spec section 5). The slider
// sounds (0x1f1..0x1f3, 0x1f2 with pan/pitch) are not voiced: their sound-table volume is 0, silent on the DS (BizHawk:
// their channels play at volume 0); only the slider loop's handle matters, it gates the thrust (see ThrustLoop).
namespace MphRecomp.Frontend
{
    public sealed class ShipPlanetSelect
    {
        public enum Phase { Closed, Scan, Select, Landing, Done }
        private enum Step { Sweep, Known, Found, Reveal, Glide, Select, Landing, Done }

        // the five planets in the game's order (sprites 15..19): Alinos, Celestial Archives, Vesper Defense Outpost,
        // Arcterra, Oubliette -- their sprite files and touch-screen positions (0x2143fdc..); StorySave.Areas bit 1 << 2p
        public const int PlanetCount = 5;
        private static readonly string[] Files = { "alinos", "archives", "outpost", "murk", "prison" };
        private static readonly (int X, int Y)[] PlanetAt = { (116, 48), (134, 94), (196, 28), (217, 70), (165, 61) };
        // a newly found planet becomes the selection in this order (0x21437a0)
        private static readonly int[] FoundOrder = { 1, 0, 2, 3, 4 };
        private const int AreaMask = 0x155;

        // touch-screen layout (DS pixels): the map frame, the crosshair's range, the beacon
        private const float FrameX = 80, FrameY = 18, FrameW = 168, FrameH = 126;
        private const int MinX = 89, MaxX = 238, MinY = 22, MaxY = 130;
        private const float BeaconX = 92, BeaconY = 30;
        private static readonly float[] ArcRadii = { 54, 76.5f, 100, 121, 151 };
        // the strip: the map scaled to fit between the window sill and the bottom, LAND SHIP at the console buttons'
        // scale to its right
        private const float MapScale = 0.435f, FrameTop = 124, LandGap = 10, LandW = 72, LandH = 52;
        // vanilla timings (game ticks)
        private const int ArcStart = 15, ArcStep = 3, SweepTicks = 33, RevealTicks = 60, RevealStagger = 5, BoxTicks = 60;
        private const int PadStep = 4, GlideStep = 3, LandSlide = 16, ConfirmBlink = 15, CornerTicks = 15, FadeTicks = 8;
        // the camera spring (0x2105790, fx 1.0 = 0x1000): v' = V + e/2, P += v' * 0x88, V = v' * 0xccc; LAND within 32
        private const int SpringPull = 0x800, SpringStep = 0x88, SpringKeep = 0xccc, LandReach = 0x20000;
        // a finger's room around the small grips (DS pixels), and a tapped planet's reach
        private const int GripMargin = 8;
        private const float TapReach = 22;
        private const int PreSelect = 0x1f0, SliderDown = 0x1f1, SliderUp = 0x1f3, LandShipSound = 0x1f4;
        // the camera's thrust (rev 0 0x2105790 / rev 1 0x2106258; traced in BizHawk 2026-10-01,
        // handoffs/start-menu-ui-rebuild_2026-10-01-files/sound_census/thrust): SHIP_THRUST_LOOP (table volume 80, centre,
        // pitch 1.0, never updated while it plays) starts on the first tick the camera trails its target by 3.0 or more
        // on an axis while the slider loop's handle (+0x6C) is set, and is restarted if its voice died. On the 6th tick
        // in a row without that it is stopped -- StopSfxHandle, which the DS turns into the channel's release (about
        // 55 ms, -18 dB a frame), not a cut -- and SHIP_THRUST_RELEASE plays (table volume 0: silent). What carries the
        // sound on after the stop (the "drift") is the echo the planet select switches on at entry (SetEcho(0)): 125 ms
        // repeats, each 0.75x and darker, about 1.5 s to fade out. The slider loop's handle is set by a slider drag, a
        // D-pad move or the glide, and only a released slider drag clears it -- so after a slider release the thrust
        // stops 6 ticks later even while the camera is still flying (vanilla does the same).
        private const int ThrustLoop = 0x1f5, ThrustRelease = 0x1f6, ThrustLag = 0x3000, ThrustSettle = 5;
        // the echo preset the planet select selects at entry (SetEchoPreset 0x20715ec rev 0, 0x2071e40 rev 1, called at
        // 0x2104c48 rev 0 / 0x21056c8 rev 1): preset 0 of the table at 0x20cb2dc rev 0 / 0x20cbb64 rev 1
        private const int ShipEcho = 0;
        private const int EnterShipScr = 14 | 0x4000, LandShipScr = 15 | 0x4000, PlanetFoundScr = 16 | 0x4000;

        private static readonly (float R, float G, float B) MapBack = (16 / 255f, 24 / 255f, 41 / 255f);
        private static readonly (float R, float G, float B) GridLine = (24 / 255f, 41 / 255f, 65 / 255f);
        private static readonly (float R, float G, float B) ArcLine = (16 / 255f, 90 / 255f, 123 / 255f);
        private static readonly (float R, float G, float B) BeaconLine = (16 / 255f, 123 / 255f, 173 / 255f);
        private static readonly (float R, float G, float B) Border = (106 / 255f, 172 / 255f, 74 / 255f);
        private static readonly (float R, float G, float B) Cross = (156 / 255f, 246 / 255f, 24 / 255f);

        private enum Drag { None, Map, Vertical, Horizontal }

        private readonly string _space, _top;
        private readonly UiTextureCache _textures;
        private readonly MenuFont _font;
        private readonly DsSprite _colours;
        private readonly int _frame1, _console, _reticle, _banner, _map, _land, _zoomFrame;
        private readonly int[] _box = new int[2], _corners = new int[2], _textbox = new int[4], _gripV = new int[3], _gripH = new int[3];
        private readonly int[,] _planets = new int[PlanetCount, 4];
        private readonly int[] _zoom = new int[PlanetCount];
        private DsImage? _reticleSrc;
        private Task<DsImage>? _reticleJob;
        private float _reticleJobScale;
        private int _reticleHi = -1;

        private Step _step = Step.Done;
        private bool _open;
        private int _timer, _clock, _frame;
        private int _areas, _known, _new;
        private readonly bool[] _visible = new bool[PlanetCount];
        private readonly int[] _revealAt = new int[PlanetCount];
        private string[] _names = new string[PlanetCount];
        private int _x, _y, _target;
        private int _px, _py, _vx, _vy;
        private int _selected = -1, _confirmAt = -1;
        private bool _gripsShown, _landOk, _glide;
        private int _gripsAt, _glideX, _glideY, _glideStep;
        private int _landVofs = -64, _zoomCount, _landTick;
        private Drag _drag;
        private int _offX, _offY, _holdX, _holdY;
        private float _scale = 1, _w = 256, _fx;

        public UiDrawList DrawList { get; }
        public bool IsOpen => _open;
        public Phase State => !_open ? Phase.Closed : _step switch
        {
            Step.Select => Phase.Select,
            Step.Landing => Phase.Landing,
            Step.Done => Phase.Done,
            _ => Phase.Scan
        };
        // the crosshair in DS touch-screen pixels, the selected planet (-1: none), LAND SHIP available
        public int CrosshairX => _x;
        public int CrosshairY => _y;
        public int Selected => _selected;
        public bool LandReady => _landOk;
        // the window's camera, in model units (= DS top-screen pixels): the deepspace model is drawn moved by this, so the
        // model point at the top screen's middle (128, 96) is (128 - CameraX, 96 - CameraY)
        public float CameraX => _px / 4096f;
        public float CameraY => _py / 4096f;
        public bool OublietteFound => _visible[4];
        // the intro is over and the map takes input
        public bool Interactive => _step == Step.Select;
        // LAND SHIP was confirmed and the corners are done: land on this planet (0..4, the game's order)
        public Action<int>? Land;
        // the planets now known to the star map (vanilla e8c50+0x14, kept with the save): set when the scan box runs
        public Action<int>? KnownAreasChanged;
        public Action<int>? PlaySound;
        public Action? StopScripts;
        // a looping sample: start -> handle (-1 none), stop
        public Func<int, int>? PlayLoop;
        public Action<int>? StopLoop;
        // the DS's stop of a sound (StopSfxHandle): the channel's release, about 55 ms (-18 dB a 60 Hz frame) down to
        // silence, not a cut; null: StopLoop
        public Action<int>? ReleaseLoop;
        // whether a handle's voice still plays (vanilla SfxHandleAlive, 0x2071c10 rev 0); null: assume it does
        public Func<int, bool>? LoopAlive;
        // the post-mix echo preset (vanilla SetEchoPreset, a 30-frame crossfade to the preset): 0 = the ship's echo
        public Action<int>? SetEcho;
        public Action<string>? Log;
        public bool RedrawArt { get; set; } = true;
        public bool RedrawReady => !RedrawArt || _reticleHi >= 0;
        // PC previews: a texture drawn full-screen where the host draws the 3D view (-1: none)
        public int PreviewTexture { get; set; } = -1;

        public ShipPlanetSelect(string fileSystemRoot, UiTextureCache textures)
        {
            _space = Path.Combine(fileSystemRoot, "_archives", "shipSpace");
            _top = Path.Combine(fileSystemRoot, "_archives", "shipTop");
            _textures = textures;
            DrawList = new UiDrawList(textures);
            _font = new MenuFont(textures);
            _colours = DsGraphics.LoadSprite(Path.Combine(_space, "fontcolors.bin"));
            _frame1 = Bg(_top, "ship_topBG1");
            _console = Bg(_top, "ship_topBG2");
            _reticle = Bg(_top, "ship_topBG3");
            _map = Bg(_space, "ship_map");
            _land = Bg(_space, "launch_ship");
            _banner = Sprite(_top, "screen", 0);
            _zoomFrame = Sprite(_space, "zoom", 0);
            for (int i = 0; i < 3; i++)
            {
                _gripV[i] = Sprite(_space, "slider", i);
                _gripH[i] = Sprite(_space, "slider_bottom", i);
            }
            for (int i = 0; i < 2; i++)
            {
                _box[i] = Sprite(_space, "planetbox", i);
                // bank 4 = red, as LAUNCH SHIP's (BizHawk rev 1: lit (251,65,65) = colour 7 of bank 4; bank 0 is lime)
                _corners[i] = Sprite(_space, "ani_corners", i, 4);
            }
            for (int i = 0; i < 4; i++)
            {
                _textbox[i] = Sprite(_space, "textbox", i);
            }
            for (int p = 0; p < PlanetCount; p++)
            {
                _zoom[p] = Sprite(_space, "zoom_" + Files[p], 0);
                // images 1-2: the flash growing, 3: white dot, 4: blue dot. Each sprite carries its own patch of the map
                // behind it (image 0 is only that); only the planet is kept (the map here is redrawn)
                for (int i = 1; i <= 4; i++)
                {
                    _planets[p, i - 1] = PlanetSprite(Files[p], i);
                }
            }
        }

        private int Bg(string dir, string name) => _textures.GetOrAdd("ship/" + name, () =>
        {
            DsImage im = DsGraphics.LoadBg(Path.Combine(dir, name + ".bin"));
            return (im.Width, im.Height, im.Rgba);
        }).Id;

        private int Sprite(string dir, string name, int image, int bank = 0) => _textures.GetOrAdd($"ship/{name}/{image}/{bank}", () =>
        {
            DsImage im = DsGraphics.LoadSprite(Path.Combine(dir, name + ".bin")).Image(image, bank);
            return (im.Width, im.Height, im.Rgba);
        }).Id;

        private int PlanetSprite(string name, int image) => _textures.GetOrAdd($"ship/planet_{name}/{image}/lit", () =>
        {
            DsImage im = DsGraphics.LoadSprite(Path.Combine(_space, "planet_" + name + ".bin")).Image(image, 0);
            byte[] rgba = (byte[])im.Rgba.Clone();
            for (int i = 0; i < rgba.Length; i += 4)
            {
                // the map's own colours stay under 130 in every channel (back, grid, arcs); the planets are brighter
                if (Math.Max(rgba[i], Math.Max(rgba[i + 1], rgba[i + 2])) < 130) rgba[i + 3] = 0;
            }
            return (im.Width, im.Height, rgba);
        }).Id;

        private static string M(int id) => Strings.GetMessage('M', id, StringTables.ShipInSpace);
        private static int AreaBit(int p) => 1 << (2 * p);
        private static (int X, int Y) Centre(int p) => (PlanetAt[p].X + 16, PlanetAt[p].Y + 16);

        // ---------------------------------------------------------------- flow

        // areas: StorySave.Areas (the planets found); known: the planets the star map has shown before (a new file: 0);
        // current: where the ship is (the first selection)
        public void Open(int areas, int known, string[] names, int current)
        {
            _open = true;
            _areas = areas & AreaMask;
            _known = known & AreaMask;
            _new = _areas & ~_known;
            _names = names;
            for (int p = 0; p < PlanetCount; p++)
            {
                _visible[p] = (_areas & AreaBit(p)) != 0;
                _revealAt[p] = Int32.MaxValue;
            }
            _step = Step.Sweep;
            _timer = _clock = _frame = 0;
            _selected = _confirmAt = -1;
            _gripsShown = _landOk = _glide = false;
            _landVofs = -64;
            _zoomCount = 0;
            _drag = Drag.None;
            _holdX = _holdY = 0;
            // the crosshair and the camera start on the current planet (vanilla: the current area's planet)
            int start = current >= 0 && current < PlanetCount && _visible[current] ? current : Array.IndexOf(_visible, true);
            _target = start;
            (_x, _y) = start >= 0 ? Centre(start) : ((MinX + MaxX) / 2, (MinY + MaxY) / 2);
            (_px, _py) = Target();
            // the enter (0x2104a5c): the ship's echo (0x2104c48), the two handles cleared and the settle count zeroed
            // (0x2104c84), then ENTER_SHIP_SCR (0x2104c98); no StopFreeSfxScripts before it
            SetEcho?.Invoke(ShipEcho);
            _sliderLoop = false;
            _thrustSettle = 0;
            StopThrust();
            PlaySound?.Invoke(EnterShipScr);
        }

        public void Close()
        {
            StopThrust();
            _open = false;
        }

        // vanilla's slider-loop handle (+0x6C) != -1, see ThrustLoop
        private bool _sliderLoop;
        private int _thrustHandle = -1, _thrustSettle, _preSelectAt = -1;

        // release: the DS's stop (the channel's ~55 ms release); otherwise cut (leaving the screen)
        private void StopThrust(bool release = false)
        {
            if (_thrustHandle >= 0)
            {
                if (release && ReleaseLoop != null) ReleaseLoop(_thrustHandle);
                else StopLoop?.Invoke(_thrustHandle);
            }
            _thrustHandle = -1;
        }

        // the D-pad / stick held this frame (-1, 0, 1; y up)
        public void Hold(int dx, int dy)
        {
            _holdX = dx;
            _holdY = dy;
        }

        public void Press(MenuKeys key)
        {
            if (key == MenuKeys.A && _step == Step.Select && _landOk) StartLanding();
        }

        // once per displayed frame (60 Hz); the screen runs on the game's 30 Hz tick
        public void Tick()
        {
            if (!_open || _step == Step.Done) return;
            if (++_frame % 2 != 0) return;
            _clock++;
            _timer++;
            switch (_step)
            {
            case Step.Sweep:
                if (_timer >= SweepTicks)
                {
                    // the planets already known fade in, staggered; with any, the sliders and lines come up now
                    NextStep(Step.Known);
                    Reveal(_known, 0);
                    if (_known != 0) ShowGrips();
                }
                break;
            case Step.Known:
                if (_timer >= RevealEnd(_known))
                {
                    if (_new != 0)
                    {
                        // a planet found since the last visit: "scanning ALIMBIC CLUSTER", and it becomes the selection
                        NextStep(Step.Found);
                        StopScripts?.Invoke(); // 0x21042a4
                        PlaySound?.Invoke(PlanetFoundScr);
                        foreach (int p in FoundOrder)
                        {
                            if ((_new & AreaBit(p)) != 0)
                            {
                                _target = p;
                                break;
                            }
                        }
                        _known = _areas;
                        KnownAreasChanged?.Invoke(_known);
                    }
                    else
                    {
                        StartGlide();
                    }
                }
                break;
            case Step.Found:
                if (_timer > BoxTicks)
                {
                    NextStep(Step.Reveal);
                    Reveal(_new, 1);
                }
                break;
            case Step.Reveal:
                if (_timer >= RevealEnd(_new) + 1) StartGlide();
                break;
            case Step.Glide:
            {
                // the glide runs in the interactive substate's body (substate 8 falls into 9 at 0x2104484): it starts
                // the slider loop, and the camera spring (with its thrust sound) runs every tick of it
                (int gx, int gy) = _target >= 0 ? Centre(_target) : (_x, _y);
                _x += Math.Clamp(gx - _x, -GlideStep, GlideStep);
                _y += Math.Clamp(gy - _y, -GlideStep, GlideStep);
                _sliderLoop = true;
                Spring();
                if (_x == gx && _y == gy) NextStep(Step.Select);
                break;
            }
            case Step.Select:
                SelectTick();
                Spring();
                break;
            case Step.Landing:
                Spring();
                if (++_landTick >= CornerTicks + FadeTicks)
                {
                    _step = Step.Done;
                    Land?.Invoke(_selected);
                }
                break;
            }
        }

        private void NextStep(Step step)
        {
            _step = step;
            _timer = 0;
        }

        private void StartGlide()
        {
            NextStep(Step.Glide);
            ShowGrips();
        }

        private void ShowGrips()
        {
            if (_gripsShown) return;
            _gripsShown = true;
            _gripsAt = _clock;
        }

        // each planet in the mask fades in (its reveal animation), (index * 5) ticks apart, from `delay` ticks on
        private void Reveal(int mask, int delay)
        {
            for (int p = 0; p < PlanetCount; p++)
            {
                if ((mask & AreaBit(p)) != 0) _revealAt[p] = _clock + delay + p * RevealStagger;
            }
        }

        private static int RevealEnd(int mask)
        {
            int last = -1;
            for (int p = 0; p < PlanetCount; p++)
            {
                if ((mask & AreaBit(p)) != 0) last = p;
            }
            return last < 0 ? 0 : last * RevealStagger + RevealTicks;
        }

        // the reveal animation (0x2143a88, 60 ticks): the flash growing, white, blue, a quick then a slow white/blue
        // blink, then blue. -1: not shown yet; else the image 1..4
        private int PlanetImage(int p)
        {
            int age = _clock - _revealAt[p];
            if (_revealAt[p] == Int32.MaxValue || age < 1) return -1;
            if (age < 13) return 1 + (age - 1) / 3;
            if (age < 21) return (age - 13) / 2 % 2 == 0 ? 3 : 4;
            if (age < 61) return (age - 21) / 10 % 2 == 0 ? 3 : 4;
            return 4;
        }

        private void SelectTick()
        {
            if (_holdX != 0 || _holdY != 0)
            {
                // the D-pad lets go of a slider; it's ignored while a tapped planet's glide runs. A D-pad move starts
                // the slider loop; with a slider held, the release that follows stops it again (0x2104734)
                if (_drag is Drag.Vertical or Drag.Horizontal) ReleaseDrag();
                else if (!_glide) _sliderLoop = true;
                if (_drag == Drag.None && !_glide)
                {
                    _x = Math.Clamp(_x + PadStep * _holdX, MinX, MaxX);
                    _y = Math.Clamp(_y - PadStep * _holdY, MinY, MaxY);
                }
            }
            if (_drag != Drag.None) _sliderLoop = true; // a held slider (0x2105ba4 / 0x2105c1c)
            if (_glide)
            {
                _x += Math.Clamp(_glideX - _x, -_glideStep, _glideStep);
                _y += Math.Clamp(_glideY - _y, -_glideStep, _glideStep);
                _glide = _x != _glideX || _y != _glideY;
                _sliderLoop = true;
                return;
            }
            int hit = HitTest(_x, _y);
            if (hit != _selected)
            {
                _selected = hit;
                _confirmAt = -1;
                if (hit >= 0)
                {
                    PlaySound?.Invoke(PreSelect);
                    _preSelectAt = _clock;
                    Log?.Invoke($"planet select: {_names[hit]}");
                }
            }
            // the confirm comes on a later tick than the pre-select (0x2105dfc returns before it), without a drag
            if (_selected >= 0 && _confirmAt < 0 && _drag == Drag.None && _clock > _preSelectAt)
            {
                _confirmAt = _clock;
                StopScripts?.Invoke(); // 0x2105e3c
                PlaySound?.Invoke(LandShipScr);
            }
        }

        // the window's camera: a spring toward the point the crosshair picks (0x2106a3c), in the game's fixed point
        private (int X, int Y) Target() =>
            ((128 - (_x - 163) * 225 / 74) << 12, (104 + (_y - 76) * 125 / 54) << 12);

        private static int FxMul(int a, int b) => (int)(((long)a * b + 0x800) >> 12);

        private void Spring()
        {
            (int tx, int ty) = Target();
            Thrust(tx - _px, ty - _py);
            int vx = _vx + FxMul(tx - _px, SpringPull), vy = _vy + FxMul(ty - _py, SpringPull);
            _px += FxMul(vx, SpringStep);
            _py += FxMul(vy, SpringStep);
            _vx = FxMul(vx, SpringKeep);
            _vy = FxMul(vy, SpringKeep);
            bool ok = _step == Step.Select && _selected >= 0 && _confirmAt >= 0
                && Math.Abs(tx - _px) < LandReach && Math.Abs(ty - _py) < LandReach;
            if (_step == Step.Select) _landOk = ok;
            _zoomCount = _landOk ? _zoomCount + 1 : 0;
            _landVofs = Math.Clamp(_landVofs + (_landOk ? LandSlide : -LandSlide), -64, 0);
        }

        // 0x2105790 (rev 0 0x210588c..0x2105950), every tick of the spring: the lag is measured before the step
        private void Thrust(int ex, int ey)
        {
            if ((Math.Abs(ex) >= ThrustLag || Math.Abs(ey) >= ThrustLag) && _sliderLoop)
            {
                // 0x2105914: the count zeroed; SfxHandleAlive(+0x70) == -1 -> PlaySfx(0x1F5), the handle kept in +0x70
                _thrustSettle = 0;
                if (_thrustHandle < 0 || LoopAlive?.Invoke(_thrustHandle) == false)
                {
                    _thrustHandle = PlayLoop?.Invoke(ThrustLoop) ?? -1;
                }
            }
            else if (_thrustSettle < ThrustSettle)
            {
                _thrustSettle++;
            }
            else if (_thrustHandle >= 0)
            {
                // 0x21058f0: StopSfxHandle(+0x70) (StopSfxHandleEx(h, fade 0) -> the channel's release), +0x70 = -1,
                // PlaySfx(0x1F6) (table volume 0)
                StopThrust(release: true);
                PlaySound?.Invoke(ThrustRelease);
            }
        }

        private int HitTest(int x, int y)
        {
            for (int p = 0; p < PlanetCount; p++)
            {
                if (PlanetImage(p) < 0) continue;
                (int px, int py) = PlanetAt[p];
                if (x > px + 4 && x < px + 28 && y > py + 4 && y < py + 28) return p;
            }
            return -1;
        }

        private void StartLanding()
        {
            _step = Step.Landing;
            _landTick = 0;
            PlaySound?.Invoke(LandShipSound);
            Log?.Invoke($"planet select: LAND SHIP at {_names[_selected]}");
        }

        // ---------------------------------------------------------------- touch (canvas pixels)

        private (int X, int Y) ToDs(float x, float y) =>
            ((int)MathF.Round(FrameX + (x / _scale - _fx) / MapScale), (int)MathF.Round(FrameY + (y / _scale - FrameTop) / MapScale));

        private (float X, float Y, float W, float H) LandRect => (_fx + FrameW * MapScale + LandGap, 128 + (64 - LandH) / 2 - _landVofs, LandW, LandH);

        // for the PC comparison against vanilla (Tools -sndtrace), in canvas pixels of the last Build: a DS touch-screen
        // point of the map (the inverse of ToDs) and the LAND SHIP button's centre
        public (float X, float Y) DsToCanvas(float tx, float ty) => (U(tx) * _scale, V(ty) * _scale);
        public (float X, float Y) LandCenter => ((LandRect.X + LandRect.W / 2) * _scale, (LandRect.Y + LandRect.H / 2) * _scale);

        private static bool In(int x, int y, int rx, int ry, int rw, int rh) =>
            x >= rx - GripMargin && x < rx + rw + GripMargin && y >= ry - GripMargin && y < ry + rh + GripMargin;

        public void Touch(float x, float y)
        {
            if (_step != Step.Select) return;
            float ux = x / _scale, uy = y / _scale;
            var l = LandRect;
            if (_landOk && ux >= l.X && ux < l.X + l.W && uy >= l.Y && uy < l.Y + l.H)
            {
                StartLanding();
                return;
            }
            (int tx, int ty) = ToDs(x, y);
            // the grips (50, Y-7, 32, 16) and (X-7, 140, 16, 32): dragged from where they're held
            if (In(tx, ty, 50, _y - 7, 32, 16))
            {
                _drag = Drag.Vertical;
                _offY = _y - ty;
                _glide = false;
                PlaySound?.Invoke(SliderDown);
                return;
            }
            if (In(tx, ty, _x - 7, 140, 16, 32))
            {
                _drag = Drag.Horizontal;
                _offX = _x - tx;
                _glide = false;
                PlaySound?.Invoke(SliderDown);
                return;
            }
            if (tx < FrameX || tx >= FrameX + FrameW || ty < FrameY || ty >= FrameY + FrameH) return;
            // a planet within reach: the crosshair glides onto it
            int best = -1;
            float bestD = TapReach * TapReach;
            for (int p = 0; p < PlanetCount; p++)
            {
                if (PlanetImage(p) < 0) continue;
                (int cx, int cy) = Centre(p);
                float d = (cx - tx) * (cx - tx) + (cy - ty) * (cy - ty);
                if (d < bestD)
                {
                    bestD = d;
                    best = p;
                }
            }
            if (best >= 0)
            {
                (_glideX, _glideY) = Centre(best);
                _glideStep = Math.Max(GlideStep, Math.Max(Math.Abs(_glideX - _x), Math.Abs(_glideY - _y)) / 10);
                _glide = true;
                return;
            }
            _drag = Drag.Map;
            _glide = false;
            TouchMove(x, y);
        }

        public void TouchMove(float x, float y)
        {
            if (_step != Step.Select || _drag == Drag.None) return;
            (int tx, int ty) = ToDs(x, y);
            if (_drag == Drag.Vertical) _y = Math.Clamp(ty + _offY, MinY, MaxY);
            else if (_drag == Drag.Horizontal) _x = Math.Clamp(tx + _offX, MinX, MaxX);
            else
            {
                _x = Math.Clamp(tx, MinX, MaxX);
                _y = Math.Clamp(ty, MinY, MaxY);
            }
        }

        public void TouchUp(float x, float y)
        {
            TouchMove(x, y);
            ReleaseDrag();
        }

        private void ReleaseDrag()
        {
            if (_drag is Drag.Vertical or Drag.Horizontal && HitTest(_x, _y) < 0) PlaySound?.Invoke(SliderUp);
            if (_drag != Drag.None) _sliderLoop = false; // 0x2105d34: the slider loop stops
            _drag = Drag.None;
        }

        // ---------------------------------------------------------------- drawing

        public void Build(int width, int height)
        {
            DrawList.Clear();
            DrawList.Clip = UiRect.None;
            _scale = height / 192f;
            _w = width / _scale;
            if (!_open) return;
            float cx = _w / 2;
            if (PreviewTexture >= 0)
            {
                Quad(PreviewTexture, 0, 0, _w, 192, 0, 0, 1, 1, 1, 1, 1, 1);
            }
            // the cockpit: frame and reticle over the 3D view, the console below
            Sliced(_frame1, 0, 128);
            Sliced(RedrawArt ? Reticle() : _reticle, 0, 128);
            Sliced(_console, 128, 192);
            // the banner names the selected planet (shipTop/screen.bin and its mirror; LocationNames, wrap 114,
            // y = 20 - 4 per line)
            Quad(_banner, cx - 64, 4, cx, 36, 0, 0, 1, 1, 1, 1, 1, 1);
            Quad(_banner, cx, 4, cx + 64, 36, 1, 0, 0, 1, 1, 1, 1, 1);
            bool live = _step is Step.Select or Step.Landing or Step.Done;
            if (live && _selected >= 0)
            {
                string name = _names[_selected];
                Text(name, cx, 20 - 4 * _font.Lines(name, 114).Count, 2, 114, 0, 1, 9);
            }
            // the zoom panel while LAND is available (top screen (96, 56)): the frame blinks, then the planet's picture
            if (_zoomCount > 0 && _selected >= 0)
            {
                if (_zoomCount >= 7) Quad(_zoom[_selected], cx - 32, 56, cx + 32, 120, 0, 0, 1, 1, 1, 1, 1, 1);
                else if ((_zoomCount & 2) == 0) Quad(_zoomFrame, cx - 32, 56, cx + 32, 120, 0, 0, 1, 1, 1, 1, 1, 1);
            }
            // the glare (0x2105970): the top screen brightens toward white near (270, 40) -- only Alinos is close enough
            int d = Math.Abs(270 - (_px >> 12)) + Math.Abs(40 - (_py >> 12));
            if (d < 128)
            {
                int b = (16 - Math.Clamp(d / 2 / 4, 0, 16)) * 12 / 16;
                if (b > 0) Quad(-1, 0, 0, _w, 128, 0, 0, 0, 0, 1, 1, 1, b / 16f);
            }
            float total = 30 * MapScale + FrameW * MapScale + LandGap + LandW;
            _fx = (_w - total) / 2 + 30 * MapScale;
            DrawMap();
            if (_step == Step.Found) DrawScanBox();
            DrawLand();
            if (_step is Step.Landing or Step.Done)
            {
                float fade = Math.Clamp((_landTick - CornerTicks) / (float)FadeTicks, 0, 1);
                if (fade > 0) Quad(-1, 0, 0, _w, 192, 0, 0, 0, 0, 1, 1, 1, fade);
            }
        }

        private float U(float tx) => _fx + (tx - FrameX) * MapScale;
        private float V(float ty) => FrameTop + (ty - FrameY) * MapScale;

        private void DrawMap()
        {
            const float k = MapScale;
            float x0 = U(FrameX), y0 = V(FrameY), x1 = U(FrameX + FrameW), y1 = V(FrameY + FrameH);
            // the sliders' bars (ship_map pixels): the vertical one re-cut to the frame's height, the bottom one as is
            UiTexture map = _textures[_map];
            Bar(map, 7, 5, 16, 143, U(55), y0 - 3, (y1 - y0 + 6) / k, vertical: true);
            Bar(map, 24, 151, 184, 16, U(72), V(152), 184, vertical: false);
            Quad(-1, x0, y0, x1, y1, 0, 0, 0, 0, MapBack.R, MapBack.G, MapBack.B, 1);
            for (float gx = 93; gx < FrameX + FrameW - 1; gx += 20)
            {
                Quad(-1, U(gx), y0, U(gx) + k, y1, 0, 0, 0, 0, GridLine.R, GridLine.G, GridLine.B, 1);
            }
            for (float gy = 29; gy < FrameY + FrameH - 1; gy += 20)
            {
                Quad(-1, x0, V(gy), x1, V(gy) + k, 0, 0, 0, 0, GridLine.R, GridLine.G, GridLine.B, 1);
            }
            // the radar sweep: ship_map's six BG frames -- the beacon at tick 15, then an arc every 3 ticks
            int frames = _step == Step.Sweep ? Math.Clamp((_timer - ArcStart) / ArcStep + 1, 0, ArcRadii.Length + 1) : ArcRadii.Length + 1;
            DrawList.Clip = new UiRect(x0 * _scale, y0 * _scale, (x1 - x0) * _scale, (y1 - y0) * _scale);
            if (frames > 0)
            {
                Ring(U(BeaconX), V(BeaconY), 8 * k, 1.8f * k, 0, 360, BeaconLine);
            }
            for (int i = 0; i < frames - 1; i++)
            {
                Ring(U(BeaconX), V(BeaconY), ArcRadii[i] * k, 1.2f * k, 0, 90, ArcLine);
            }
            // the crosshair's lines (line_horiz / line_vert, 1 px) come up with the sliders
            if (_gripsShown)
            {
                Quad(-1, x0, V(_y), x1, V(_y) + k, 0, 0, 0, 0, Cross.R, Cross.G, Cross.B, 1);
                Quad(-1, U(_x), y0, U(_x) + k, y1, 0, 0, 0, 0, Cross.R, Cross.G, Cross.B, 1);
            }
            DrawList.Clip = UiRect.None;
            // the 2 px frame
            float b = 2 * k;
            Quad(-1, x0 - b / 2, y0 - b / 2, x1 + b / 2, y0 + b / 2, 0, 0, 0, 0, Border.R, Border.G, Border.B, 1);
            Quad(-1, x0 - b / 2, y1 - b / 2, x1 + b / 2, y1 + b / 2, 0, 0, 0, 0, Border.R, Border.G, Border.B, 1);
            Quad(-1, x0 - b / 2, y0, x0 + b / 2, y1, 0, 0, 0, 0, Border.R, Border.G, Border.B, 1);
            Quad(-1, x1 - b / 2, y0, x1 + b / 2, y1, 0, 0, 0, 0, Border.R, Border.G, Border.B, 1);
            for (int p = 0; p < PlanetCount; p++)
            {
                int image = PlanetImage(p);
                if (image < 0) continue;
                (int px, int py) = PlanetAt[p];
                Quad(_planets[p, image - 1], U(px), V(py), U(px + 32), V(py + 32), 0, 0, 1, 1, 1, 1, 1, 1);
            }
            if (_selected >= 0 && _step is Step.Select or Step.Landing)
            {
                // the planet box: still while dragging, then a blink every 3 ticks for 15 (0x2143aac / 0x2143aa6)
                int age = _clock - _confirmAt;
                int box = _confirmAt >= 0 && age < ConfirmBlink ? age / 3 % 2 : 1;
                (int px, int py) = PlanetAt[_selected];
                Quad(_box[box], U(px + 1), V(py), U(px + 33), V(py + 32), 0, 0, 1, 1, 1, 1, 1, 1);
            }
            if (!_gripsShown) return;
            // the grips at (50, Y-7) and (X-7, 140): a blink as they come up (21 ticks), flashing while held
            int intro = _clock - _gripsAt;
            int gv = _drag == Drag.Vertical ? 1 + _clock / 2 % 2 : intro < 21 ? 1 + intro / 3 % 2 : 0;
            int gh = _drag == Drag.Horizontal ? 1 + _clock / 2 % 2 : intro < 21 ? 1 + intro / 3 % 2 : 0;
            Quad(_gripV[gv], U(50), V(_y - 7), U(82), V(_y + 9), 0, 0, 1, 1, 1, 1, 1, 1);
            Quad(_gripH[gh], U(_x - 7), V(140), U(_x + 9), V(172), 0, 0, 1, 1, 1, 1, 1, 1);
        }

        // a slider bar from ship_map: its 14 px end caps kept, the middle repeated to the new length (in map pixels)
        private void Bar(UiTexture map, int sx, int sy, int sw, int sh, float x, float y, float length, bool vertical)
        {
            const float k = MapScale;
            const int cap = 14;
            int srcLen = vertical ? sh : sw;
            void Piece(int from, int len, float at)
            {
                if (vertical)
                {
                    Quad(map.Id, x, y + at * k, x + sw * k, y + (at + len) * k, (float)sx / map.Width, (float)(sy + from) / map.Height,
                        (float)(sx + sw) / map.Width, (float)(sy + from + len) / map.Height, 1, 1, 1, 1);
                }
                else
                {
                    Quad(map.Id, x + at * k, y, x + (at + len) * k, y + sh * k, (float)(sx + from) / map.Width, (float)sy / map.Height,
                        (float)(sx + from + len) / map.Width, (float)(sy + sh) / map.Height, 1, 1, 1, 1);
                }
            }
            Piece(0, cap, 0);
            float pos = cap, end = length - cap;
            int mid = srcLen - 2 * cap;
            while (pos < end)
            {
                int len = (int)Math.Min(mid, MathF.Ceiling(end - pos));
                Piece(cap, len, pos);
                pos += len;
            }
            Piece(srcLen - cap, cap, end);
        }

        // a circle's arc as a thin band of triangles (degrees, y down)
        private void Ring(float cx, float cy, float r, float width, float from, float to, (float R, float G, float B) c)
        {
            int steps = Math.Max(8, (int)((to - from) / 4));
            float r0 = r - width / 2, r1 = r + width / 2;
            for (int i = 0; i < steps; i++)
            {
                float a0 = (from + (to - from) * i / steps) * MathF.PI / 180, a1 = (from + (to - from) * (i + 1) / steps) * MathF.PI / 180;
                UiVertex P(float rr, float a) => new UiVertex((cx + rr * MathF.Cos(a)) * _scale, (cy + rr * MathF.Sin(a)) * _scale, 0, 0, c.R, c.G, c.B, 1);
                UiVertex p00 = P(r0, a0), p01 = P(r1, a0), p10 = P(r0, a1), p11 = P(r1, a1);
                DrawList.Triangle(-1, UiWrap.Clamp, UiWrap.Clamp, UiBlend.Alpha, p00, p01, p11);
                DrawList.Triangle(-1, UiWrap.Clamp, UiWrap.Clamp, UiBlend.Alpha, p00, p11, p10);
            }
        }

        // "scanning ALIMBIC CLUSTER": the ROM's box (textbox.bin: images 0-3 = a quarter opening from the middle, four
        // quarters mirrored into 128x64) for 60 ticks, at full size over the map's middle (the DS has it at (64, 48));
        // the text is green (bank 0), centred, dimmed by the box's blend
        private void DrawScanBox()
        {
            float cx = U(FrameX + FrameW / 2), cy = Math.Max(V(FrameY + FrameH / 2), 120 + 32);
            int image = Math.Clamp(_timer - 1, 0, 3);
            for (int q = 0; q < 4; q++)
            {
                bool right = (q & 1) != 0, bottom = (q & 2) != 0;
                float qx0 = right ? cx : cx - 64, qy0 = bottom ? cy : cy - 32;
                float u0 = right ? 1 : 0, u1 = right ? 0 : 1, v0 = bottom ? 1 : 0, v1 = bottom ? 0 : 1;
                Blended(_textbox[image], qx0, qy0, qx0 + 64, qy0 + 32, u0, v0, u1, v1);
            }
            if (_timer >= 4)
            {
                Text(M(1), cx, cy - 11, 2, 100, 0, 0.8f, 11);
            }
        }

        // LAND SHIP: the button (launch_ship, blended as the DS does) and its label (M002 at the button's (5, 10)); it
        // rises into place 16 px a tick, and the corners blink on it when it's pressed
        private void DrawLand()
        {
            var l = LandRect;
            if (l.Y >= 192) return;
            UiTexture t = _textures[_land];
            Blended(_land, l.X, l.Y, l.X + LandW, l.Y + LandH, 0, 0, LandW / t.Width, LandH / t.Height);
            Text(M(2), l.X + 5, l.Y + 10, 0, 0, 4, 1, 15);
            if (_step is Step.Landing or Step.Done && _landTick < CornerTicks)
            {
                // ani_corners at (2, 142) (38, 142) (2, 149) (38, 149) of the touch screen = the button's (2, 6)...
                int image = _landTick / 2 % 2;
                for (int q = 0; q < 4; q++)
                {
                    bool right = (q & 1) != 0, bottom = (q & 2) != 0;
                    float x = l.X + (right ? 38 : 2), y = l.Y + (bottom ? 13 : 6);
                    Quad(_corners[image], x, y, x + 32, y + 32, right ? 1 : 0, bottom ? 1 : 0, right ? 0 : 1, bottom ? 0 : 1, 1, 1, 1, 1);
                }
            }
        }

        // the DS blend: the piece plus 6/16 of what's behind
        private void Blended(int tex, float x0, float y0, float x1, float y1, float u0, float v0, float u1, float v1)
        {
            Quad(tex, x0, y0, x1, y1, u0, v0, u1, v1, 0, 0, 0, 10 / 16f);
            Quad(tex, x0, y0, x1, y1, u0, v0, u1, v1, 1, 1, 1, 1, UiBlend.Additive);
        }

        // the shipTop layers are 256x256: edges and centre 1:1, the two spans between stretch to the canvas width
        private void Sliced(int tex, float y0, float y1)
        {
            float[] slices = { 0, 56, 96, 160, 200, 256 };
            float extra = (_w - 256) / 2;
            float x = 0;
            for (int i = 0; i < slices.Length - 1; i++)
            {
                float srcW = slices[i + 1] - slices[i];
                float dstW = i == 1 || i == 3 ? Math.Max(0, srcW + extra) : srcW;
                Quad(tex, x, y0, x + dstW, y1, slices[i] / 256f, y0 / 256f, slices[i + 1] / 256f, y1 / 256f, 1, 1, 1, 1);
                x += dstW;
            }
        }

        // the window reticle redrawn as clean lines (shared with the ship menu through the texture cache)
        private int Reticle()
        {
            float scale = MathF.Ceiling(_scale * 4) / 4;
            string key = $"bg/redraw/ship_topBG3@{scale}";
            if (_reticleJobScale != scale)
            {
                _reticleJobScale = scale;
                _reticleJob = null;
                _reticleHi = _textures.TryGet(key, out UiTexture? done) ? done!.Id : -1;
            }
            if (_reticleHi >= 0) return _reticleHi;
            if (_reticleJob == null)
            {
                _reticleSrc ??= DsGraphics.LoadBg(Path.Combine(_top, "ship_topBG3.bin"));
                DsImage src = _reticleSrc;
                _reticleJob = Task.Run(() => PixelRedraw.Redraw(src, scale));
            }
            if (_reticleJob.IsCompletedSuccessfully)
            {
                DsImage hi = _reticleJob.Result;
                _reticleHi = _textures.GetOrAdd(key, () => (hi.Width, hi.Height, hi.Rgba)).Id;
            }
            else if (_reticleJob.IsFaulted)
            {
                Log?.Invoke("planet select: reticle redraw failed: " + _reticleJob.Exception?.GetBaseException().Message);
                _reticleHi = _reticle;
            }
            return _reticleHi >= 0 ? _reticleHi : _reticle;
        }

        private void Quad(int tex, float x0, float y0, float x1, float y1, float u0, float v0, float u1, float v1,
            float r, float g, float b, float a, UiBlend blend = UiBlend.Alpha)
        {
            DrawList.Quad(tex, x0 * _scale, y0 * _scale, x1 * _scale, y1 * _scale, u0, v0, u1, v1, r, g, b, a, blend);
        }

        private readonly List<WidgetTri> _glyphs = new();

        // a text record: (x, top) = the first line's cell top; align 0 left, 1 right, 2 centred; colour = shipSpace
        // fontcolors bank `pal`
        private void Text(string text, float x, float top, int align, int wrap, int pal, float alpha = 1, float lineHeight = 12)
        {
            if (string.IsNullOrWhiteSpace(text) || alpha <= 0) return;
            (byte R, byte G, byte B) c = _colours.Colour(pal, 2);
            _glyphs.Clear();
            _font.Emit(text, x, 192 - top - lineHeight, align, wrap, lineHeight, c.R / 255f, c.G / 255f, c.B / 255f, alpha, 0, _glyphs);
            foreach (WidgetTri t in _glyphs)
            {
                DrawList.Triangle(t.TextureId, UiWrap.Clamp, UiWrap.Clamp, UiBlend.Alpha, Map(t.A), Map(t.B), Map(t.C));
            }
        }

        private UiVertex Map(UiVertex v) => new UiVertex(v.X * _scale, -v.Y * _scale, v.U, v.V, v.R, v.G, v.B, v.A);
    }
}
