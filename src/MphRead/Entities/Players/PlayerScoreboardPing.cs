using System;
using MphRead.Hud;
using MphRead.Text;

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
        private void DrawScoreboardPing(int slot, float x, float y, float barsCenterX)
        {
            int ping = ScoreboardPingValue(slot);
            if (ping < 0)
            {
                return;
            }
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
            // (barsCenterX: left of the board's widest ping number, measured, so the column lines up)
            inst.PositionX = barsCenterX / 256f;
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

        // a seat's ping as the board shows it (capped at 999), -1 when unknown
        private static int ScoreboardPingValue(int slot)
        {
            int ping = slot >= 0 && slot < HostSeatPingMs.Length ? HostSeatPingMs[slot] : -1;
            return ping < 0 ? -1 : Math.Min(ping, 999);
        }

        // width of a line in the board's font, DS pixels: the font is proportional (digits aren't 8 px wide), and the
        // columns have to be laid out from what is drawn, not from assumed widths, or ping runs into kills (some devices)
        private float MeasureScoreboardText(ReadOnlySpan<char> text)
        {
            if (text.Length == 0)
            {
                return 0;
            }
            Font font = SetUpFont(text[0], set: false);
            float width = 0;
            for (int i = 0; i < text.Length; i++)
            {
                int ch = text[i];
                if ((ch & 0x80) != 0 && i + 1 < text.Length)
                {
                    ch = text[++i] & 0x3F | ((ch & 0x1F) << 6);
                }
                int index = ch - font.MinCharacter;
                if (index >= 0 && index < font.Widths.Count)
                {
                    width += font.Widths[index];
                }
            }
            return width;
        }

        public const float ScoreboardPingRight = 252; // right edge of the ping column (number and "ms" header)

        // The kills/deaths column normally sits at x 215. With pings on the board, the bars + number (right edge 252) and the
        // "ms" header need the room, so the column moves left just far enough to clear them, and the bars sit left of the
        // widest ping number. Returns (x of the kills/deaths column, x of the bars' centre; 0 when there are no pings).
        private (float value2X, float barsX) LayoutScoreboardPing(string header2,
            Func<int, string> slotValue2, Func<int, string> teamValue2)
        {
            const float defaultX = 215;
            if (!AnySeatPing())
            {
                return (defaultX, 0);
            }
            float numWidth = 0;
            float valueWidth = MeasureScoreboardText(header2);
            for (int i = 0; i < GameState.ActivePlayers; i++)
            {
                int slot = GameState.ResultSlots[i];
                if (!Players[slot].LoadFlags.TestFlag(LoadFlags.Active))
                {
                    continue;
                }
                int ping = ScoreboardPingValue(slot);
                if (ping >= 0)
                {
                    numWidth = Math.Max(numWidth, MeasureScoreboardText(ping.ToString()));
                }
                valueWidth = Math.Max(valueWidth, MeasureScoreboardText(slotValue2(slot)));
                if (GameState.Teams)
                {
                    valueWidth = Math.Max(valueWidth, MeasureScoreboardText(teamValue2(Players[slot].TeamIndex)));
                }
            }
            float barsLeft = ScoreboardPingRight - numWidth - 2 - _pingBarSize;
            float limit = Math.Min(barsLeft, ScoreboardPingRight - MeasureScoreboardText("ms"));
            return (Math.Min(defaultX, limit - 3 - valueWidth / 2), barsLeft + _pingBarSize / 2);
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
