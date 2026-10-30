using System;
using MphRead.Formats;
using MphRead.Hud;

namespace MphRead.Entities
{
    // recomp: the Scout Drone's two attack waits on the HUD (owner 2026-10-12: "an on screen visual for the drone attack
    // cool downs ... a circle or icon of some kind that hollows out or greys out when used and then fills back in with a
    // timer count down in it"). Two dials where vanilla's alt form shows BOOST (left) and BOMBS (right), which the drone
    // has neither of, sliding in with them (_boostBombsYOffset): LASER on the left (its own 10 s, PlayerDroneLaser.cs) and
    // BURST on the right (its 2 s wait after every fold, PlayerScoutDrone.cs; owner 10-12: swapped sides).
    //   - ready: a lit disc with the attack's mark (three volley dots / a beam), a short pop when it comes back
    //   - in use (unfolding, firing, folding / the laser on): a hollow grey ring
    //   - waiting: the grey dial refilling clockwise from the top, the whole seconds left in the middle
    // Draw only: reads the sim's waits, so replays, another seat's view and online joiners (NetDrone carries both waits)
    // show the same. SWAP DRONE DIALS (OPTIONS > CONTROLS, SCOUT DRONE; 2026-10-19) puts BURST on the left and LASER on
    // the right: the dials only, the buttons stay as they are. The textures are made here (MphRead's HUD colour from the text palette), one per state, and go
    // through DrawHudObject like any HUD piece.
    public partial class PlayerEntity
    {
        private const int _droneDialTexels = 64; // the dial texture's side
        private const float _droneDialSize = 22; // drawn diameter, DS pixels (mode 0, as the HUD's text)
        private const int _droneDialSteps = 60; // refill steps per wait
        private const int _droneDialPopFrames = 12;
        private const int _droneBurstWaitFrames = 120; // _droneCooldownFrames
        private const int _droneLaserWaitFrames = 600; // _droneLaserCooldownFrames

        private sealed class DroneDial
        {
            public readonly HudObjectInstance Inst = new HudObjectInstance(_droneDialTexels, _droneDialTexels);
            public int Key = -1; // the state the texture holds
            public bool WasWaiting;
            public int Pop;
        }

        // the host's own pick (CampaignRenderer.ReloadAimFromSettings, RecompSettings.DroneDialsSwapped)
        public static bool HostDroneDialsSwapped { get; set; }

        private DroneDial? _droneBurstDial;
        private DroneDial? _droneLaserDial;
        private ColorRgba _droneDialColor;
        private static readonly string[] _droneDialSeconds = MakeDroneDialSeconds();

        private static string[] MakeDroneDialSeconds()
        {
            var seconds = new string[100];
            for (int i = 0; i < seconds.Length; i++)
            {
                seconds[i] = i.ToString();
            }
            return seconds;
        }

        // PlayerHud.DrawHudObjects, the alt-form block, for a drone seat
        private void DrawDroneDials()
        {
            if (_droneBurstDial == null)
            {
                _droneBurstDial = new DroneDial();
                _droneLaserDial = new DroneDial();
                _droneDialColor = DroneDialHudColor();
            }
            float posY = _boostBombsYOffset + 4;
            bool burstBusy = _droneFireState != 0;
            float burstX = HostDroneDialsSwapped ? 29 : 227;
            float laserX = HostDroneDialsSwapped ? 227 : 29;
            DrawDroneDial(_droneBurstDial, laser: false, burstX, posY, burstBusy, burstBusy ? 0 : _droneCooldown,
                _droneBurstWaitFrames, "BURST");
            bool laserBusy = _droneLaserFrame >= 0;
            DrawDroneDial(_droneLaserDial!, laser: true, laserX, posY, laserBusy, laserBusy ? 0 : _droneLaserWait,
                _droneLaserWaitFrames, "LASER");
        }

        private void DrawDroneDial(DroneDial dial, bool laser, float x, float y, bool busy, int wait, int full,
            string label)
        {
            int state; // 0 ready, 1 in use, 2.. waiting (2 + refill step)
            if (busy)
            {
                state = 1;
            }
            else if (wait > 0)
            {
                float done = 1 - Math.Clamp(wait / (float)full, 0, 1);
                state = 2 + Math.Min((int)(done * _droneDialSteps), _droneDialSteps - 1);
            }
            else
            {
                state = 0;
            }
            bool waiting = state >= 2;
            if (dial.WasWaiting && state == 0)
            {
                dial.Pop = _droneDialPopFrames;
            }
            dial.WasWaiting = waiting;
            HudObjectInstance inst = dial.Inst;
            if (dial.Key != state)
            {
                FillDroneDial(inst.Texture, laser, state, _droneDialColor);
                if (inst.BindingId == -1)
                {
                    inst.BindingId = _scene.BindGetTexture(inst.Texture, _droneDialTexels, _droneDialTexels);
                }
                else
                {
                    _scene.BindTexture(inst.Texture, _droneDialTexels, _droneDialTexels, inst.BindingId);
                }
                dial.Key = state;
            }
            float pop = 0;
            if (dial.Pop > 0)
            {
                pop = MathF.Sin(dial.Pop / (float)_droneDialPopFrames * MathF.PI) * 0.25f;
                dial.Pop--;
            }
            inst.Enabled = true;
            inst.Center = true;
            inst.Scale = _droneDialSize / _droneDialTexels * (1 + pop);
            inst.PositionX = x / 256f;
            inst.PositionY = y / 192f;
            inst.Alpha = Features.HudOpacity;
            _scene.DrawHudObject(inst);
            if (waiting)
            {
                int seconds = Math.Min((wait + 59) / 60, _droneDialSeconds.Length - 1);
                DrawText2D(x, y - 4, Align.Center, palette: 0, _droneDialSeconds[seconds],
                    new ColorRgba(255, 255, 255, 255), alpha: Features.HudOpacity);
            }
            DrawText2D(x, y + 14, Align.Center, palette: 0, label, alpha: Features.HudOpacity);
        }

        // the HUD's own colour: the brightest of the text palette (MphRead's HUD text, from the ammo bar's palette)
        private ColorRgba DroneDialHudColor()
        {
            var color = new ColorRgba(80, 255, 200, 255);
            int best = -1;
            if (_textInst.PaletteData is { } palette)
            {
                for (int i = 1; i < Math.Min(16, palette.Count); i++)
                {
                    ColorRgba c = palette[i];
                    int sum = c.Red + c.Green + c.Blue;
                    if (sum > best && sum < 3 * 250) // not plain white
                    {
                        best = sum;
                        color = new ColorRgba(c.Red, c.Green, c.Blue, 255);
                    }
                }
            }
            return color;
        }

        private static void FillDroneDial(ColorRgba[] pixels, bool laser, int state, ColorRgba hud)
        {
            const int size = _droneDialTexels;
            const float center = (size - 1) / 2f;
            const float outer = size / 2f - 1;
            const float inner = outer - _droneDialTexels / 9.5f;
            var grey = new ColorRgba(110, 110, 110, 255);
            var shade = new ColorRgba(0, 0, 0, 255);
            var mark = new ColorRgba((byte)((hud.Red + 255) / 2), (byte)((hud.Green + 255) / 2),
                (byte)((hud.Blue + 255) / 2), 255);
            float done = state >= 2 ? (state - 2 + 1) / (float)_droneDialSteps : 0;
            for (int py = 0; py < size; py++)
            {
                for (int px = 0; px < size; px++)
                {
                    float dx = px - center;
                    float dy = py - center;
                    float dist = MathF.Sqrt(dx * dx + dy * dy);
                    // clockwise from the top, 0-1
                    float turn = MathF.Atan2(dx, -dy) / (2 * MathF.PI);
                    if (turn < 0)
                    {
                        turn += 1;
                    }
                    bool filled = state == 0 || (state >= 2 && turn < done);
                    float ringCover = Coverage(outer - dist) * Coverage(dist - inner);
                    float discCover = Coverage(inner - dist);
                    // the ring: lit when ready / refilled this far, grey otherwise
                    ColorRgba ring = filled ? hud : grey;
                    float ringAlpha = filled ? 1f : 0.85f;
                    // the disc: a soft lit fill when ready / refilled this far, a dark backing otherwise (hollow in use)
                    ColorRgba disc = filled ? hud : shade;
                    float discAlpha = state == 1 ? 0.15f : filled ? (state == 0 ? 0.45f : 0.3f) : 0.5f;
                    float markCover = state == 0 ? DroneDialMark(laser, dx / center, dy / center) : 0;
                    ColorRgba c = disc;
                    float a = discAlpha * discCover;
                    if (markCover > 0)
                    {
                        c = Blend(c, mark, markCover);
                        a += (1 - a) * markCover * discCover;
                    }
                    if (ringCover > 0)
                    {
                        float ra = ringAlpha * ringCover;
                        c = Blend(c, ring, a > 0 ? ra / (a + ra * (1 - a)) : 1);
                        a = ra + a * (1 - ra);
                    }
                    pixels[py * size + px] = new ColorRgba(c.Red, c.Green, c.Blue, (byte)MathF.Round(Math.Clamp(a, 0, 1) * 255));
                }
            }
        }

        // the attack's mark on a ready dial, in units of the dial's radius (y down): three volley dots in a row / a
        // diagonal beam
        private static float DroneDialMark(bool laser, float x, float y)
        {
            const float px = 2f / (_droneDialTexels - 1); // one texel
            if (laser)
            {
                // a bar from lower left to upper right, round ends
                float ax = -0.42f, ay = 0.42f, bx = 0.42f, by = -0.42f;
                float vx = bx - ax, vy = by - ay;
                float t = Math.Clamp(((x - ax) * vx + (y - ay) * vy) / (vx * vx + vy * vy), 0, 1);
                float ex = x - (ax + vx * t), ey = y - (ay + vy * t);
                return Coverage((0.13f - MathF.Sqrt(ex * ex + ey * ey)) / px);
            }
            float best = 0;
            for (int i = -1; i <= 1; i++)
            {
                float ex = x - i * 0.36f, ey = y;
                best = MathF.Max(best, Coverage((0.15f - MathF.Sqrt(ex * ex + ey * ey)) / px));
            }
            return best;
        }

        // one texel of antialiasing at an edge (d = texels inside)
        private static float Coverage(float d) => Math.Clamp(d + 0.5f, 0, 1);

        private static ColorRgba Blend(ColorRgba a, ColorRgba b, float t)
        {
            return new ColorRgba((byte)(a.Red + (b.Red - a.Red) * t), (byte)(a.Green + (b.Green - a.Green) * t),
                (byte)(a.Blue + (b.Blue - a.Blue) * t), 255);
        }
    }
}
