using System;
using System.Collections.Generic;
using System.Linq;

namespace MphRecomp.Multiplayer
{
    // Samus's imported HD suits (Characters.Samus.Known) in a team mode or as a same-hunter copy (owner 2026-10-12: "they
    // should probably mirror existing Samus recolors"): the seat's variant (LookAlikes.VariantFor: Samus seats count
    // together, suited or not, so the variant is the MPH palette other devices draw her in) in that palette's colours.
    // MPH's Samus copies are three groups of colour -- a main armour colour, a second one on other parts, and the lights
    // (visor, cannon) -- measured from Samus_lod0's own palettes (pal_02-04, pal_team01/02) and HunterColours' 5th-8th.
    // Each suit names which of its own colours are its main, second and lights (hues; its greys go with one of them or
    // stay), and GxShader's suit mode (uTint w 11+k) moves each group to the variant's colour by brightness. Drawing only.
    public static class SuitColours
    {
        // a suit's colour groups, measured on its paint textures (hues in degrees, -1 = none): Main, Second and Third (drawn
        // in the second colour), its Lights; Greys: 0 its greys stay, 1 they're its main armour, 2 its second; Ref: the main,
        // second, grey and helmet/chest groups' mean texel brightness (a group's own shading kept relative to the new colour); Swap:
        // variants whose main and second colours trade places on this suit (where the main one is the suit's own look)
        readonly record struct Groups(float Main, float Second, float Third, float Lights, int Greys,
            (float Main, float Second, float Grey, float Chest) Ref, float Chest = -1, params LookAlikes.Variant[] Swap);

        static readonly Dictionary<string, Groups> Suits = new(StringComparer.OrdinalIgnoreCase)
        {
            // as MPH Samus (owner 2026-10-12: "carefully examine which parts of armor are which colors"): plates (shoulders,
            // arms, outer legs, boots) the main colour, helmet + chest a deeper main (Chest), torso + inner legs the second.
            // Varia-type: orange plates, red helmet/chest, yellow torso/inner legs, green visor
            ["MP1PowerSuit"] = new(38, -1, -1, 100, 0, (0.77f, 0.5f, 0.29f, 0.59f), 5),
            ["MP1VariaSuit"] = new(30, 47, -1, 100, 0, (0.73f, 0.77f, 0.3f, 0.55f), 5),
            ["MP2VariaSuit"] = new(30, 48, -1, 100, 0, (0.78f, 0.79f, 0.29f, 0.6f), 5),
            ["MP3VariaSuit"] = new(30, 48, -1, 100, 0, (0.74f, 0.67f, 0.3f, 0.49f), 5),
            ["MP1GravitySuit"] = new(248, 42, -1, 185, 0, (0.34f, 0.76f, 0.29f, 0.6f), 8), // purple plates, red helmet/chest, gold torso/inner legs, cyan lights
            ["MP1BetaPhazonSuit"] = new(221, 200, -1, 25, 0, (0.32f, 0.5f, 0.3f, 0.47f), 169), // navy plates, blue body, teal helmet/shoulders, orange visor
            ["MP1FusionSuit"] = new(182, 50, -1, 100, 0, (0.53f, 0.65f, 0.31f, 0.59f), 330), // teal suit, yellow plates, pink helmet
            ["Phazonsuit"] = new(-1, -1, -1, 15, 1, (0.5f, 0.5f, 0.26f, 0.5f), -1, LookAlikes.Variant.Fourth, LookAlikes.Variant.Eighth), // dark grey, orange-red lights
            ["MP2DarkSuit"] = new(-1, 15, -1, 25, 1, (0.5f, 0.23f, 0.32f, 0.5f)),          // silver-grey plates (main), brown body (second), orange visor
            ["MP2LightSuit"] = new(-1, -1, -1, 50, 1, (0.5f, 0.5f, 0.37f, 0.5f), -1, LookAlikes.Variant.Fifth), // white armour, gold glows
            ["MP3PEDSuit"] = new(205, 26, -1, 140, 1, (0.39f, 0.7f, 0.4f, 0.48f), 8, LookAlikes.Variant.Fifth), // blue-steel + silver armour (shoulders, limbs), red helmet, gold torso, green lights
            ["MP4DefaultSuit"] = new(24, 50, -1, 95, 0, (0.3f, 0.24f, 0.12f, 0.29f), 0), // bronze plates, red helmet/chest, olive body, green visor
            ["MP4PsychicSuit"] = new(24, 50, -1, 280, 0, (0.29f, 0.23f, 0.1f, 0.28f), 0),   // ... violet lights
            ["MP4PsychicGlove"] = new(24, 50, -1, 280, 0, (0.29f, 0.23f, 0.1f, 0.28f), 0),
            ["MP4ViolaSuit"] = new(355, 207, -1, 280, 2, (0.27f, 0.16f, 0.15f, 0.5f)),     // red plates, dark blue-grey suit, violet lights
            ["MP4ViolaSuitIC"] = new(355, 207, -1, 285, 2, (0.28f, 0.16f, 0.15f, 0.5f)),
            ["MP4LegacySuit"] = new(-1, 35, -1, 285, 1, (0.5f, 0.34f, 0.32f, 0.5f), -1, LookAlikes.Variant.Fifth), // silver + black armour, gold trim, violet lights
        };
        static readonly Groups Default = new(30, 47, -1, 100, 0, (0.73f, 0.77f, 0.30f, 0.55f), 5);

        // per variant (LookAlikes.Variant order): MPH Samus's plates (main), torso/inner legs (second), lights and helmet/chest
        // (null = the suit as imported: the 1st Samus)
        static readonly ((float, float, float) Main, (float, float, float) Second, (float, float, float) Lights, (float, float, float) Chest)?[] Variants =
        {
            null,
            ((0.50f, 0.27f, 0.48f), (0.30f, 0.62f, 0.67f), (0.70f, 0.18f, 1.00f), (0.31f, 0.07f, 0.21f)),   // pal_02 mauve, teal, magenta-violet, dark magenta
            ((0.17f, 0.38f, 0.62f), (0.66f, 0.35f, 0.59f), (1.00f, 0.40f, 0.06f), (0.10f, 0.16f, 0.27f)),   // pal_03 blue, pink, orange, dark blue
            ((0.13f, 0.12f, 0.14f), (0.30f, 0.26f, 0.30f), (1.00f, 0.06f, 0.03f), (0.11f, 0.03f, 0.03f)),   // pal_04 black, grey-mauve, red, black-red
            ((0.62f, 0.23f, 0.02f), (0.80f, 0.60f, 0.21f), (1.00f, 0.80f, 0.20f), (0.48f, 0.11f, 0.01f)),   // pal_team01 deep orange, tan-gold, yellow, red-orange
            ((0.46f, 0.64f, 0.00f), (0.48f, 0.58f, 0.44f), (0.35f, 1.00f, 0.20f), (0.18f, 0.24f, 0.01f)),   // pal_team02 yellow-green, grey-green, green, dark green
            ((0.72f, 0.74f, 0.78f), (0.40f, 0.70f, 0.74f), (0.55f, 0.30f, 1.00f), (0.62f, 0.16f, 0.10f)),   // 5th silver, pale teal, violet, red helmet
            ((0.85f, 0.80f, 0.16f), (0.75f, 0.40f, 0.38f), (0.95f, 1.00f, 0.25f), (0.15f, 0.38f, 0.72f)),   // 6th lemon (gold read as the Varia's own), rose, yellow, blue helmet
            ((0.22f, 0.62f, 0.18f), (0.56f, 0.37f, 0.70f), (1.00f, 0.08f, 0.30f), (0.62f, 0.12f, 0.30f)),   // 7th green, violet, red, magenta-crimson helmet
            ((0.11f, 0.12f, 0.13f), (0.78f, 0.70f, 0.40f), (0.10f, 1.00f, 0.08f), (0.55f, 0.08f, 0.05f)),   // 8th black, tan-gold, green, red helmet
        };

        // The Fusion Suit follows its disc palettes' rule (body, torso/trim and helmet three different hues, the helmet the
        // contrasting accent: cyan/yellow/pink, lime/magenta/pink, purple/teal/pink, orange/yellow/red) in the team colours and
        // the 5th-8th (owner 2026-10-24: ours were shades of one colour); index = LookAlikes.Variant, null = the shared table
        static readonly ((float, float, float) Main, (float, float, float) Second, (float, float, float) Lights, (float, float, float) Chest)?[] FusionVariants =
        {
            null, null, null, null,
            ((0.78f, 0.30f, 0.03f), (0.22f, 0.20f, 0.20f), (1.00f, 0.80f, 0.20f), (0.85f, 0.70f, 0.15f)),   // team orange: orange body, charcoal torso, yellow helmet
            ((0.25f, 0.58f, 0.08f), (0.18f, 0.22f, 0.15f), (0.35f, 1.00f, 0.20f), (0.70f, 0.18f, 0.45f)),   // team green: green body, dark torso, magenta helmet
            ((0.70f, 0.72f, 0.76f), (0.08f, 0.45f, 0.52f), (0.55f, 0.30f, 1.00f), (0.80f, 0.15f, 0.10f)),   // 5th silver body, teal torso, red helmet
            ((0.85f, 0.80f, 0.16f), (0.45f, 0.20f, 0.62f), (0.95f, 1.00f, 0.25f), (0.12f, 0.45f, 0.85f)),   // 6th lemon body, violet torso, blue helmet
            ((0.22f, 0.62f, 0.18f), (0.85f, 0.62f, 0.18f), (1.00f, 0.08f, 0.30f), (0.85f, 0.12f, 0.40f)),   // 7th green body, gold torso, crimson helmet
            ((0.11f, 0.12f, 0.13f), (0.78f, 0.62f, 0.20f), (0.10f, 1.00f, 0.08f), (0.75f, 0.08f, 0.05f)),   // 8th black body, gold torso, red helmet
        };

        const float Strength = 0.95f;

        // The Fusion Suit's own disc palettes (Prime 1's Fusion_Varia, Fusion_Gravity, Fusion_Phazon: the same model with their own
        // textures) stand in for the 2nd-4th colour; imported as hidden extras that go with the Fusion Suit's card
        static readonly string[] FusionPalettes = { "MP1FusionSuitVaria", "MP1FusionSuitGravity", "MP1FusionSuitPhazon" };

        public static bool IsPalette(string? skin) => skin != null && FusionPalettes.Contains(skin, StringComparer.OrdinalIgnoreCase);

        // the skin a seat draws for its colour: the Fusion Suit's 2nd-4th seat wear the disc's palettes, else the skin itself
        public static string Palette(string skin, LookAlikes.Variant v) =>
            skin.Equals("MP1FusionSuit", StringComparison.OrdinalIgnoreCase) && v >= LookAlikes.Variant.Second && v <= LookAlikes.Variant.Fourth
                ? FusionPalettes[(int)v - 1] : skin;

        // the skin a palette is a colour of (its morph ball is the base suit's)
        // the suits that share a default colour (owner 2026-10-24: the three Varia Suits are one look, so two of them in a
        // match take different colours); others are their own
        public static string LookOf(string skin) =>
            skin.Equals("MP1VariaSuit", StringComparison.OrdinalIgnoreCase) || skin.Equals("MP2VariaSuit", StringComparison.OrdinalIgnoreCase)
            || skin.Equals("MP3VariaSuit", StringComparison.OrdinalIgnoreCase) ? "variasuit" : skin;

        // the suits that share the copy colours (1-9) (owner 2026-10-24: the Gravity Suit's recolours look like the Varia
        // Suits', so they never repeat between them; each keeps its own default, LookAlikes.CopyIndex). The Phazon Suits
        // stay out (distinct enough). Colours only: nothing in a match reads it
        public static string FamilyOf(string skin) =>
            skin.Equals("MP1GravitySuit", StringComparison.OrdinalIgnoreCase) ? "variasuit" : LookOf(skin);

        public static string BaseOf(string skin) => IsPalette(skin) ? "MP1FusionSuit" : skin;

        // the skin is one of Samus's suits (a seat wearing it gets these tints)
        public static bool IsSuit(string? skin) => skin != null && Characters.Samus.Known.Contains(skin, StringComparer.OrdinalIgnoreCase);

        // a suit's colour group hues (main, second, third, lights, helmet/chest): what SuitClasses sorts its texels by
        public static (float Main, float Second, float Third, float Lights, float Chest) Hues(string skin)
        {
            Groups g = Suits.TryGetValue(skin, out Groups s) ? s : Default;
            return (g.Main, g.Second, g.Third, g.Lights, g.Chest);
        }

        // a suit's extra class-map rules (SuitClasses.Rules); owner 2026-10-24: the parts between the armour stay dark, the
        // Dark Suit's red dots keep a colour of their own, the Echoes Varia chest plate (texture e8af7174) recolours whole
        static readonly Dictionary<string, SuitClasses.Rules> Extra = new(StringComparer.OrdinalIgnoreCase)
        {
            // the chest plate (collar blade + plate), outlined from the model: which texels the torso front and neck cover
            ["MP2VariaSuit"] = new(Regions: new SuitClasses.Region("e8af7174", 0, 0, 1, 1, new[] { 224, 0, 223, 16, 222, 48, 221, 56, 219, 64, 217, 72, 215, 80, 212, 88, 210, 96, 207, 104, 204, 112, 200, 120, 195, 128, 190, 136, 179, 144, 157, 144, 157, 216, 194, 216, 194, 208, 200, 208, 200, 200, 206, 200, 206, 192, 242, 192, 242, 184, 257, 184, 257, 176, 267, 176, 267, 168, 272, 168, 272, 152, 266, 152, 266, 144, 262, 144, 262, 136, 258, 136, 258, 128, 255, 128, 255, 120, 251, 120, 251, 112, 238, 112, 238, 104, 237, 104, 236, 96, 236, 80, 237, 64, 240, 64, 240, 56, 242, 56, 242, 48, 244, 48, 244, 40, 246, 40, 246, 32, 248, 32, 248, 24, 249, 24, 249, 16, 242, 16, 242, 8, 232, 8, 232, 0 })),
            // Prime 3 Varia: the Echoes layout (same UVs), softer paint
            ["MP3VariaSuit"] = new(Split: true, Regions: new[] { new SuitClasses.Region("77e3c4b7", 0, 0, 1, 1, new[] { 224, 0, 223, 16, 222, 48, 221, 56, 219, 64, 217, 72, 215, 80, 212, 88, 210, 96, 207, 104, 204, 112, 200, 120, 195, 128, 190, 136, 179, 144, 157, 144, 157, 216, 194, 216, 194, 208, 200, 208, 200, 200, 206, 200, 206, 192, 242, 192, 242, 184, 257, 184, 257, 176, 267, 176, 267, 168, 272, 168, 272, 152, 266, 152, 266, 144, 262, 144, 262, 136, 258, 136, 258, 128, 255, 128, 255, 120, 251, 120, 251, 112, 238, 112, 238, 104, 237, 104, 236, 96, 236, 80, 237, 64, 240, 64, 240, 56, 242, 56, 242, 48, 244, 48, 244, 40, 246, 40, 246, 32, 248, 32, 248, 24, 249, 24, 249, 16, 242, 16, 242, 8, 232, 8, 232, 0 }),
                // its helmet (the red pieces at the bottom left, outlined from the paint) is shaded with orange highlights:
                // helmet colour whole, as on the other Varias (owner 2026-10-24: the helmet top went two colours)
                new SuitClasses.Region("77e3c4b7", 0, 0, 1, 1, new[] { 126, 344, 126, 352, 112, 352, 112, 360, 100, 360, 100, 368, 87, 368, 87, 376, 75, 376, 75, 384, 66, 384, 66, 392, 56, 392, 56, 400, 47, 400, 47, 408, 42, 408, 42, 424, 47, 424, 47, 432, 53, 432, 53, 440, 61, 440, 61, 448, 46, 448, 46, 456, 35, 456, 35, 464, 28, 464, 28, 512, 245, 512, 245, 384, 237, 384, 237, 376, 237, 368, 220, 368, 220, 360, 193, 360, 193, 352, 157, 352, 157, 344 }),
                // the visor glass (cabf17b3's lower left, its u = 0 edge mirrored down the visor's middle): its near-white centre
                // highlight read as the second colour, a line down the visor (owner 2026-10-29); the glass's lights colour whole
                new SuitClasses.Region("cabf17b3", 0, 58 / 128f, 34 / 128f, 124 / 128f, Glass: true),
                // and the glass's added shine layer (5bf34cff, same UVs): its pale centre strip read as the second colour too
                new SuitClasses.Region("5bf34cff", 0, 58 / 128f, 34 / 128f, 124 / 128f, Glass: true) }),
            // Beta Phazon: recoloured piece by piece on the CPU (SuitPieces). Its seams are found from the glow layer drawn over
            // them, not by hue: the teal-tinted ribs share the seams' hue, and the seams' soft edges pass through the second
            // colour's, so hue groups drew thick jagged bands beside every line (owner 2026-10-29)
            ["MP1BetaPhazonSuit"] = new(Glow: new[] { "1360cda7", "5d2a2923", "7b95650a", "9804aba5" },
                Pieces: new SuitPieces.Spec(
                    Pairs: new[] { ("bcf6e6f5", "9804aba5"), ("9fb766de", "5d2a2923"), ("455d7b51", "1360cda7") },
                    Whole: new[] { "3a4659b6" }, Chest: new[] { "bd92e4be" },
                    Glow: new[] { "1360cda7", "5d2a2923", "7b95650a", "9804aba5" }),
                // the collar ring is a grey tube on the helmet texture, grey in every colour like vanilla
                Regions: new[] { new SuitClasses.Region("3a4659b6", 356 / 512f, 0, 408 / 512f, 56 / 512f, Keep: true) }),
            ["MP2DarkSuit"] = new(Dots: 0, Dim: 0.35f),
            // Phazon: the thigh speck layer (604da35a, dark plates with phazon veins) keeps its own colour, as vanilla (owner
            // 2026-10-24: circled leg spots still lit)
            ["Phazonsuit"] = new(Plain: new[] { "129e6a95", "a5d742bf", "36057a7a", "32357eec", "604da35a" }),
            ["MP2LightSuit"] = new(Dark: 0.22f),
            // PED: the gold band and the knee pad (1d5f031c; the pad's lower V and its brown underside read as helmet) are second whole like the other gold leg plates; the grey helmet tube
            // segment and the navy neck ribbing (63344da1 rects; the ribbing's bright ticks read as main) keep their own colour (owner 2026-10-24: coloured spots in
            // the helmet tubes, colour in the neck)
            ["MP3PEDSuit"] = new(Dark: 0.28f, Glow: new[] { "74a5e113", "a14bde60" },
                Regions: new[] { new SuitClasses.Region("1d5f031c", 0, 0, 1, 1, new[] { 390, 397, 497, 397, 482, 442, 456, 452, 428, 452, 404, 442 }, Second: true),
                    new SuitClasses.Region("1d5f031c", 0, 0, 1, 1, new[] { 183, 262, 222, 264, 240, 290, 254, 300, 254, 336, 110, 336, 120, 306, 150, 286, 170, 272 }, Second: true),
                    new SuitClasses.Region("1d5f031c", 256 / 512f, 8 / 512f, 318 / 512f, 46 / 512f, Second: true),
                    new SuitClasses.Region("63344da1", 288 / 512f, 158 / 512f, 328 / 512f, 182 / 512f, Keep: true),
                    new SuitClasses.Region("63344da1", 0, 0, 256 / 512f, 40 / 512f, Keep: true), new SuitClasses.Region("63344da1", 70 / 512f, 212 / 512f, 200 / 512f, 246 / 512f, Keep: true) }),
        };

        static readonly Dictionary<string, LookAlikes.Variant[]> ContrastVisor = new(StringComparer.OrdinalIgnoreCase)
        {
            ["MP3PEDSuit"] = new[] { LookAlikes.Variant.Second, LookAlikes.Variant.Fourth, LookAlikes.Variant.Seventh },
        };

        static float Bright((float R, float G, float B) c) => Math.Max(c.R, Math.Max(c.G, c.B));

        static float HueOf((float R, float G, float B) c)
        {
            float v = Bright(c), d = v - Math.Min(c.R, Math.Min(c.G, c.B));
            if (d <= 0) return 0;
            float h = v == c.R ? ((c.G - c.B) / d % 6 + 6) % 6 : v == c.G ? (c.B - c.R) / d + 2 : (c.R - c.G) / d + 4;
            return h * 60;
        }

        static float HueGap((float, float, float) a, (float, float, float) b)
        {
            float d = Math.Abs(HueOf(a) - HueOf(b)) % 360;
            return Math.Min(d, 360 - d);
        }

        public static SuitClasses.Rules? Rules(string skin) => Extra.TryGetValue(skin, out SuitClasses.Rules? r) ? r : null;

        // the GxShader values for a suit in a variant: uTint (main, w 11+k), uTint2 (second), uTint3 (lights), uTintHue (main,
        // second, third and lights hues), uTintRef (main, second, grey brightness; greys); null = drawn as imported
        public static ((float R, float G, float B, float W) Tint, (float R, float G, float B) Second, (float R, float G, float B, float ChestRef) Lights,
            (float Main, float Second, float Third, float Lights) Hue, (float Main, float Second, float Grey, float Greys) Ref,
            (float R, float G, float B, float Hue) Chest)? For(string skin, LookAlikes.Variant v)
        {
            if ((int)v < 0 || (int)v >= Variants.Length) return null;
            var cv = skin.Equals("MP1FusionSuit", StringComparison.OrdinalIgnoreCase) && FusionVariants[(int)v] is { } fv ? fv : Variants[(int)v];
            if (cv is not { } c) return null;
            Groups g = Suits.TryGetValue(skin, out Groups s) ? s : Default;
            var (main, second) = g.Swap.Contains(v) ? (c.Second, c.Main) : (c.Main, c.Second);
            var lights = c.Lights;
            // a big visor next to the helmet: in the variants whose lights read as the helmet's colour on this suit (pal_02's
            // violet in a magenta helmet, pal_04's red in a dark red one, the 7th's red in a crimson one), the visor takes the
            // main or second colour that stands apart from the helmet, else the suit's own green (owner 2026-10-24: the PED
            // visor went the helmet's purple in one colour, contrasting in the next)
            if (ContrastVisor.TryGetValue(skin, out var vs) && vs.Contains(v))
            {
                var pick = (0.3f, 1f, 0.4f);
                float best = 60;
                foreach (var cand in new[] { main, second })
                {
                    float m = Bright(cand), sat = m > 0 ? 1 - Math.Min(cand.Item1, Math.Min(cand.Item2, cand.Item3)) / m : 0, gap = HueGap(cand, c.Chest);
                    if (m > 0.2f && sat > 0.3f && gap > best) { best = gap; pick = (cand.Item1 / m, cand.Item2 / m, cand.Item3 / m); }
                }
                lights = pick;
            }
            return ((main.Item1, main.Item2, main.Item3, 11 + Strength), second, (lights.Item1, lights.Item2, lights.Item3, g.Ref.Chest), (g.Main, g.Second, g.Third, g.Lights),
                (g.Ref.Main, g.Ref.Second, g.Ref.Grey, g.Greys), (c.Chest.Item1, c.Chest.Item2, c.Chest.Item3, g.Chest));
        }
    }
}
