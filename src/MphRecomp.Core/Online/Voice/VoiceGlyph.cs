using System;

namespace MphRecomp.Online.Voice
{
    // The speaking indicator's art (Thread C, C4, 2026-10-23): a small speaker with two sound waves, drawn in code (new art in
    // the menus' style, not a ROM asset). White on transparent so each screen tints it: the lobby's green "talking" beside
    // the seat word, the in-match pop-up beside the player's name. Anti-aliased by 4x4 supersampling; one texture per size.
    public static class VoiceGlyph
    {
        // n x n RGBA8, white with the shape in alpha
        public static byte[] Render(int n)
        {
            byte[] px = new byte[n * n * 4];
            const int ss = 4;
            for (int y = 0; y < n; y++)
            {
                for (int x = 0; x < n; x++)
                {
                    int hit = 0;
                    for (int sy = 0; sy < ss; sy++)
                    {
                        for (int sx = 0; sx < ss; sx++)
                        {
                            float fx = (x + (sx + 0.5f) / ss) / n, fy = (y + (sy + 0.5f) / ss) / n;
                            if (Inside(fx, fy)) hit++;
                        }
                    }
                    int i = (y * n + x) * 4;
                    px[i] = px[i + 1] = px[i + 2] = 255;
                    px[i + 3] = (byte)(255 * hit / (ss * ss));
                }
            }
            return px;
        }

        // the shape on a unit square (y down): the speaker body and cone on the left, two arcs opening to the right
        public static bool Inside(float x, float y)
        {
            // body
            if (x >= 0.06f && x <= 0.26f && y >= 0.37f && y <= 0.63f) return true;
            // cone: widens from the body to x = 0.48
            if (x > 0.26f && x <= 0.48f)
            {
                float half = 0.13f + (x - 0.26f) / 0.22f * 0.25f;
                if (MathF.Abs(y - 0.5f) <= half) return true;
            }
            // waves: arcs about the cone's mouth, +-48 degrees, two radii
            float dx = x - 0.44f, dy = y - 0.5f;
            if (dx > 0.02f)
            {
                float d = MathF.Sqrt(dx * dx + dy * dy);
                float angle = MathF.Abs(MathF.Atan2(dy, dx));
                if (angle <= 0.84f && (MathF.Abs(d - 0.26f) <= 0.035f || MathF.Abs(d - 0.43f) <= 0.035f)) return true;
            }
            return false;
        }
    }
}
