namespace MphRecomp.Multiplayer
{
    // Prime 4 Sylux's (skin MP4Sylux, a native body) team and copy colours (owner 2026-10-29: "Start the Prime 4 Sylux
    // recolors"): the same tints the troopers' real bodies take (LookAlikes.BodyTint: w 7+k team, w 9+k two-tone copy,
    // GxShader uTint / uTint2), on her body, Lockjaw and first-person gun. Copies 2-8 follow MPH Sylux's own seat
    // palettes (body, lights: 2 olive/yellow, 3 purple/magenta, 4 black/cyan, 5 red, 6 grey/white, 7 orange, 8 blue), so
    // the nth Sylux reads as the same colour whichever Sylux she wears.
    public static class BeyondSyluxColours
    {
        public const string Skin = "MP4Sylux";

        public static bool Is(string? skin) => skin != null && skin.Equals(Skin, System.StringComparison.OrdinalIgnoreCase);

        static readonly ((float R, float G, float B), (float R, float G, float B))[] Copy =
        {
            default, default,
            ((0.62f, 0.66f, 0.16f), (1f, 0.95f, 0.10f)),  // 2 olive, yellow lights
            ((0.62f, 0.22f, 0.85f), (1f, 0.12f, 0.92f)),  // 3 purple, magenta
            ((0.30f, 0.31f, 0.36f), (0.10f, 1f, 1f)),     // 4 charcoal, cyan
            ((1f, 0.22f, 0.20f), (1f, 0.10f, 0.08f)),     // 5 red, red
            ((0.70f, 0.76f, 0.95f), (1f, 1f, 1f)),        // 6 grey-blue, white
            ((1f, 0.48f, 0.10f), (1f, 0.58f, 0.10f)),     // 7 orange, orange
            ((0.22f, 0.32f, 1f), (0.30f, 0.45f, 1f)),     // 8 blue, blue
        };

        static int? CopyOf(LookAlikes.Variant v) => v switch
        {
            LookAlikes.Variant.Second => 2, LookAlikes.Variant.Third => 3, LookAlikes.Variant.Fourth => 4,
            LookAlikes.Variant.Fifth => 5, LookAlikes.Variant.Sixth => 6, LookAlikes.Variant.Seventh => 7,
            LookAlikes.Variant.Eighth => 8, _ => null,
        };

        // the draw tint (uTint) for a variant; null = drawn as imported (the 1st)
        public static (float R, float G, float B, float W)? Tint(LookAlikes.Variant v)
        {
            if (v is LookAlikes.Variant.OrangeTeam or LookAlikes.Variant.GreenTeam)
            {
                var c = LookAlikes.TeamColor(v);
                float k = v == LookAlikes.Variant.OrangeTeam ? 1.8f : 1f;
                return (c.R * k, c.G * k, c.B * k, 7.9f);
            }
            if (CopyOf(v) is not int n) return null;
            var b = Copy[n].Item1;
            return (b.R, b.G, b.B, 9.9f);
        }

        // a copy's lights colour (uTint2), zero otherwise
        public static (float R, float G, float B) Light(LookAlikes.Variant v) => CopyOf(v) is int n ? Copy[n].Item2 : (0f, 0f, 0f);

        // which texels are her lights (uTint2.w = hue + 1, GxShader's two-tone path): vanilla's teal, hue ~155-175 (her blue
        // armour, hue 210-220, is as saturated and bright as lights, so brightness alone can't tell them apart); teams pass it
        // too, as GxShader's flag that this is her (only her saturated armour takes the colour, her grey underlayer kept); 0 = the 1st
        public static float LightHue(LookAlikes.Variant v) => Tint(v) != null ? 166f : 0f;
    }
}
