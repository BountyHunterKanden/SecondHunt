using System;
using MphRead.Hud;

namespace MphRead.Entities
{
    // recomp (2026-10-19): each seat's ping on the live scoreboard of an online match,
    // as its host measures it (MphRecomp Net/SeatPings.cs fills HostSeatPingMs; -1 = none: a bot, the host's own seat in a
    // served match, a local match or a replay). Drawing only: the match never reads it.
    public partial class PlayerEntity
    {
        public static readonly int[] HostSeatPingMs = NewSeatPings();

        private static int[] NewSeatPings()
        {
            int[] pings = new int[16];
            Array.Fill(pings, -1);
            return pings;
        }

        public static void ResetSeatPings() => Array.Fill(HostSeatPingMs, -1);

        private const int _pingBarTexels = 16;
        private const float _pingBarSize = 8; // drawn side, DS pixels
        private HudObjectInstance?[]? _pingBarInsts;

        // a seat's ping in its row on the value line: the lobby's four rising bars (2026-10-20) then the number right-aligned
        // at x (the ms column), green / yellow / red; nothing when it isn't known
        private void DrawScoreboardPing(int slot, float x, float y)
        {
            int ping = slot >= 0 && slot < HostSeatPingMs.Length ? HostSeatPingMs[slot] : -1;
            if (ping < 0)
            {
                return;
            }
            ping = Math.Min(ping, 999);
            int level = ping < 60 ? 4 : ping < 100 ? 3 : ping < 160 ? 2 : 1; // (as the lobby's)
            ColorRgba color = level >= 3 ? new ColorRgba(96, 255, 96, 255)
                : level == 2 ? new ColorRgba(255, 224, 80, 255) : new ColorRgba(255, 88, 88, 255);
            string text = ping.ToString();
            DrawText2D(x, y, Align.Right, 0, text, color, fontSpacing: 8);
            _pingBarInsts ??= new HudObjectInstance?[5];
            HudObjectInstance? inst = _pingBarInsts[level];
            if (inst == null)
            {
                inst = _pingBarInsts[level] = new HudObjectInstance(_pingBarTexels, _pingBarTexels);
                FillPingBars(inst.Texture, level, color);
                inst.BindingId = _scene.BindGetTexture(inst.Texture, _pingBarTexels, _pingBarTexels);
            }
            inst.Enabled = true;
            inst.Center = true;
            inst.Scale = _pingBarSize / _pingBarTexels;
            // (left of the widest number, 3 digits of 8 px)
            inst.PositionX = (x - 3 * 8 - _pingBarSize / 2 - 1) / 256f;
            inst.PositionY = (y + 4) / 192f;
            inst.Alpha = 1;
            _scene.DrawHudObject(inst);
        }

        private static void FillPingBars(ColorRgba[] pixels, int level, ColorRgba lit)
        {
            const int size = _pingBarTexels;
            Array.Fill(pixels, new ColorRgba(0, 0, 0, 0));
            var dim = new ColorRgba(70, 70, 70, 255);
            for (int i = 0; i < 4; i++)
            {
                int left = i * 4, height = 4 + i * 4; // (rising, bottom-aligned; 3 texels wide, 1 apart)
                for (int py = size - height; py < size; py++)
                {
                    for (int px = left; px < left + 3; px++)
                    {
                        pixels[py * size + px] = i < level ? lit : dim;
                    }
                }
            }
        }

        // whether any seat has a ping (the 5-8 seat board's column header)
        private static bool AnySeatPing()
        {
            foreach (int ping in HostSeatPingMs)
            {
                if (ping >= 0) return true;
            }
            return false;
        }
    }
}
