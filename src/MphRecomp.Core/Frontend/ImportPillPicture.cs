using System;

namespace MphRecomp.Frontend
{
    // Options' IMPORT GAMES button's picture (owner 2026-10-08: "add a dedicated importer button"): ours, drawn here on
    // the pill it's cloned from (the CONTROLS pill, a 64x32 texture, RecompMenus.DrawOptions). The pill's own glyphs are
    // painted over in its orange, its rim kept, and two glyphs of ours go on it in the game's black, as AUDIO's speaker +
    // note and CONTROLS' d-pad + buttons do: a game disc, and an arrow down into a tray (import). The edges are smoothed:
    // 8x8 samples per texel.
    internal static class ImportPillPicture
    {
        public const string Key = "import games pill";
        private const int Samples = 8;
        // the pills' orange and the glyphs' black
        private static readonly (byte R, byte G, byte B) Orange = (230, 99, 16), Black = (0, 0, 0);
        // the disc (the left glyph's place on the CONTROLS pill: centre x ~15, the right one's ~49)
        private const float DiscX = 16.5f, DiscY = 16, DiscR = 11.5f, HoleR = 2.8f, ShineR0 = 5.6f, ShineR1 = 7.2f;
        // the arrow and the tray, centred where CONTROLS' buttons are (x 47) and inside the pill's curve with 3+ texels of
        // orange round them (owner 2026-10-08: the first try's tray corner was cut off by the curve), its bottom corners
        // rounded as the pill is
        private const float ArrowX = 47, ShaftW = 6, ShaftTop = 4, HeadTop = 11, HeadW = 16, HeadTip = 19;
        private const float TrayX0 = 37.5f, TrayX1 = 56.5f, TrayTop = 17.5f, TrayBottom = 26.5f, TrayWall = 3.5f, TrayFloor = 3.5f,
            TrayCorner = 2.5f;

        // whether a point (texels, y down) is black: the disc without its hole and shine, the arrow, the tray
        private static bool Glyph(float x, float y)
        {
            float dx = x - DiscX, dy = y - DiscY, d2 = dx * dx + dy * dy;
            if (d2 <= DiscR * DiscR)
            {
                if (d2 <= HoleR * HoleR) return false;
                // the shine: an arc of the disc's surface catching the light, upper left
                float a = MathF.Atan2(dy, dx) * 180 / MathF.PI; // -180..180, 0 = right, -90 = up
                if (d2 >= ShineR0 * ShineR0 && d2 <= ShineR1 * ShineR1 && a >= -165 && a <= -105) return false;
                return true;
            }
            // the arrow: a shaft and a head pointing down
            if (MathF.Abs(x - ArrowX) <= ShaftW / 2 && y >= ShaftTop && y <= HeadTop) return true;
            if (y >= HeadTop && y <= HeadTip && MathF.Abs(x - ArrowX) <= HeadW / 2 * (HeadTip - y) / (HeadTip - HeadTop)) return true;
            // the tray: two walls and a floor, the outer bottom corners rounded
            if (x < TrayX0 || x > TrayX1 || y < TrayTop || y > TrayBottom) return false;
            float cx = x < TrayX0 + TrayCorner ? TrayX0 + TrayCorner : x > TrayX1 - TrayCorner ? TrayX1 - TrayCorner : x;
            float cy = y > TrayBottom - TrayCorner ? TrayBottom - TrayCorner : y;
            if ((x - cx) * (x - cx) + (y - cy) * (y - cy) > TrayCorner * TrayCorner) return false;
            return y >= TrayBottom - TrayFloor || x <= TrayX0 + TrayWall || x >= TrayX1 - TrayWall;
        }

        public static (int Width, int Height, byte[] Rgba) Draw(UiTexture pill)
        {
            int w = pill.Width, h = pill.Height;
            byte[] src = pill.Rgba, rgba = new byte[w * h * 4];
            bool Opaque(int x, int y) => x >= 0 && y >= 0 && x < w && y < h && src[(y * w + x) * 4 + 3] > 0;
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    int o = (y * w + x) * 4;
                    if (!Opaque(x, y)) continue;
                    // the pill: its rim (next to the outside) as it is, inside it the orange
                    bool rim = !Opaque(x - 1, y) || !Opaque(x + 1, y) || !Opaque(x, y - 1) || !Opaque(x, y + 1);
                    (byte r, byte g, byte b) = rim ? (src[o], src[o + 1], src[o + 2]) : Orange;
                    int hits = 0;
                    for (int sy = 0; sy < Samples; sy++)
                    {
                        for (int sx = 0; sx < Samples; sx++)
                        {
                            if (Glyph(x + (sx + 0.5f) / Samples, y + (sy + 0.5f) / Samples)) hits++;
                        }
                    }
                    float k = hits / (float)(Samples * Samples);
                    rgba[o] = (byte)MathF.Round(r + (Black.R - r) * k);
                    rgba[o + 1] = (byte)MathF.Round(g + (Black.G - g) * k);
                    rgba[o + 2] = (byte)MathF.Round(b + (Black.B - b) * k);
                    rgba[o + 3] = src[o + 3];
                }
            }
            return (w, h, rgba);
        }
    }
}
