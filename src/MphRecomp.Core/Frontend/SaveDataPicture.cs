using System;

namespace MphRecomp.Frontend
{
    // The file select's SAVE DATA button picture (owner 2026-10-29: "change the little icons in them to up and down arrows"):
    // ours, drawn here, not taken from the ROM. An up arrow and a down arrow side by side (import, export) in the style of the
    // bar's COPY and DELETE icons: an orange ring around a black disc, with the figures in the same orange. The ring, size
    // and place in a 32x32 texture are the MOVIES icon's, so the button (a clone of that icon, FileSelect.BindTransfer)
    // draws this texture with its own triangles and UVs (FileSelect.DrawSaveData). The edges are smoothed: 8x8
    // samples per texel.
    internal static class SaveDataPicture
    {
        public const string Key = "save data button";
        public const int Size = 32;
        private const int Samples = 8;
        // the ring: its outer edge, where the orange starts, and where it has faded to black (COPY / DELETE's measures)
        private const float CX = 16, CY = 16, Outer = 13, Rim = 12, Black = 11.3f;
        private static readonly (byte R, byte G, byte B) Orange = (230, 99, 8);

        // one arrow, its shaft 2 texels wide: point up (dir -1) or down (dir 1), the tip at y tip, the far end at y tail
        private static bool InArrow(float x, float y, float cx, float tip, float tail, int dir)
        {
            const float HeadHalf = 4, HeadLength = 5, ShaftHalf = 1;
            float along = (y - tip) * -dir; // 0 at the tip, growing towards the tail
            float length = MathF.Abs(tail - tip);
            if (along < 0 || along > length) return false;
            if (along <= HeadLength) return MathF.Abs(x - cx) <= HeadHalf * along / HeadLength;
            return MathF.Abs(x - cx) <= ShaftHalf;
        }

        public static (int Width, int Height, byte[] Rgba) Draw()
        {
            var rgba = new byte[Size * Size * 4];
            for (int py = 0; py < Size; py++)
            {
                for (int px = 0; px < Size; px++)
                {
                    float r = 0, g = 0, b = 0, a = 0;
                    for (int sy = 0; sy < Samples; sy++)
                    {
                        for (int sx = 0; sx < Samples; sx++)
                        {
                            float x = px + (sx + 0.5f) / Samples, y = py + (sy + 0.5f) / Samples;
                            float d = MathF.Sqrt((x - CX) * (x - CX) + (y - CY) * (y - CY));
                            if (d > Outer) continue;
                            // the ring fades from orange to black inwards; the arrows are orange
                            float t = Math.Clamp((d - Black) / (Rim - Black), 0, 1);
                            if (InArrow(x, y, 11, 9, 23, -1) || InArrow(x, y, 21, 23, 9, 1)) t = 1;
                            r += Orange.R * t;
                            g += Orange.G * t;
                            b += Orange.B * t;
                            a += 1;
                        }
                    }
                    int i = (py * Size + px) * 4;
                    if (a == 0)
                    {
                        // see-through, its colour the ring's so filtering doesn't darken the edge
                        (rgba[i], rgba[i + 1], rgba[i + 2], rgba[i + 3]) = (Orange.R, Orange.G, Orange.B, 0);
                        continue;
                    }
                    rgba[i] = (byte)MathF.Round(r / a);
                    rgba[i + 1] = (byte)MathF.Round(g / a);
                    rgba[i + 2] = (byte)MathF.Round(b / a);
                    rgba[i + 3] = (byte)MathF.Round(255 * a / (Samples * Samples));
                }
            }
            return (Size, Size, rgba);
        }
    }
}
