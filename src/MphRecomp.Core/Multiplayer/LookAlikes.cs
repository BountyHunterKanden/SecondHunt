using System;
using System.Collections.Generic;
using MphRead;
using MphRead.Formats;

namespace MphRecomp.Multiplayer
{
    // No-import look-alikes (docs/CHARACTERS_AND_SKINS.md D5 "Mixed imports"): a seat playing a character whose skin
    // this device hasn't imported draws as the base hunter's own DS model recoloured in that character's colours --
    // Weavel in each MP1 trooper's, his Scout Drone stand-in (MPH's Psycho Bit) to match, Samus dark blue with a cyan
    // glow for Dark Samus. Built on each device from MPH's OWN models (nothing travels between players), the way MPH
    // makes its team colours: extra palette sets appended to the model's recolors at load (MphRead Read.HostExtraRecolors),
    // picked per seat (PlayerEntity.HostLookRecolor). Drawing only.
    public static class LookAlikes
    {
        public enum Look { None = -1, PowerTrooper, IceTrooper, WaveTrooper, PlasmaTrooper, DarkSamus }

        static readonly Look[] TrooperLooks = { Look.PowerTrooper, Look.IceTrooper, Look.WaveTrooper, Look.PlasmaTrooper };
        static readonly Look[] DarkLooks = { Look.DarkSamus };

        // the models each look set goes on (all of a hunter's body/alt models carry the same count of MPH recolors, so
        // a seat's look has one index for all of them)
        static readonly Dictionary<string, Look[]> ModelLooks = new(StringComparer.Ordinal)
        {
            ["Weavel_lod0"] = TrooperLooks, ["Weavel_lod1"] = TrooperLooks,
            ["WeavelAlt_lod0"] = TrooperLooks, ["WeavelAlt_Turret_lod0"] = TrooperLooks,
            ["PsychoBit"] = TrooperLooks,
            ["Samus_lod0"] = DarkLooks, ["Samus_lod1"] = DarkLooks, ["SamusAlt_lod0"] = DarkLooks,
        };

        // MPH's own recolor count per model, recorded as the looks are appended
        static readonly Dictionary<string, int> _baseCount = new(StringComparer.Ordinal);
        static bool _registered;

        // dev/test switch: draw look-alikes even where the skin is imported (pictures next to the real looks)
        public static bool Force { get; set; }

        // the look-alikes and the character names (D7) are on for this match (set before it starts)
        public static bool On { get; set; }

        // before any of these models loads (models are cached: ones already loaded are dropped so they reload with them)
        public static void Register()
        {
            if (_registered) return;
            _registered = true;
            Read.HostExtraRecolors = Build;
            foreach (string name in ModelLooks.Keys) Read.RemoveModel(name);
        }

        // the look a seat wears when its skin isn't drawn here: by its character, the trooper's colours by its skin (a
        // skinless Trooper, the dev "1:trooper", in the Power Trooper's)
        public static Look For(MatchPlayer seat)
        {
            string? c = seat.Character;
            if (string.Equals(c, Characters.Trooper.Id, StringComparison.OrdinalIgnoreCase) && seat.Hunter == Characters.Trooper.Base)
            {
                return seat.Skin?.ToLowerInvariant() switch
                {
                    "mp1icetrooper" => Look.IceTrooper,
                    "mp1wavetrooper" => Look.WaveTrooper,
                    "mp1plasmatrooper" => Look.PlasmaTrooper,
                    _ => Look.PowerTrooper,
                };
            }
            if (string.Equals(c, Characters.DarkSamus.Id, StringComparison.OrdinalIgnoreCase) && seat.Hunter == Characters.DarkSamus.Base)
            {
                return Look.DarkSamus;
            }
            return Look.None;
        }

        // the seat's picked character's name where it isn't its hunter (D7: TROOPER, DARK SAMUS), else null
        public static string? CharacterName(MatchPlayer seat)
        {
            foreach (Character c in Characters.All)
            {
                if (!c.HasOriginal && c.Base == seat.Hunter && string.Equals(c.Id, seat.Character, StringComparison.OrdinalIgnoreCase)) return c.Name;
            }
            return null;
        }

        // Dark Samus's phazon shimmer: her look is this many palette sets, a light-blue wave moving through her armour's
        // brightness levels from one to the next; the drawing steps through them (ShimmerPhase)
        public const int ShimmerSets = 12;
        const int ShimmerFramesPerSet = 4; // 48 frames (0.8 s) a wave

        static int SetsOf(Look look) => look == Look.DarkSamus ? ShimmerSets : 1;

        // the shimmer's set for a draw frame (a seat's offset keeps two Dark Samuses apart)
        public static int ShimmerPhase(long frame, int seat) => (int)((frame / ShimmerFramesPerSet + seat * 5) % ShimmerSets);

        // the recolor index of a look on a model (-1: the model doesn't carry it, or it hasn't loaded yet); phase: which of
        // a shimmering look's sets
        public static int RecolorFor(Look look, string model, int phase = 0)
        {
            if (look == Look.None || !ModelLooks.TryGetValue(model, out Look[]? looks) || !_baseCount.TryGetValue(model, out int n)) return -1;
            int k = Array.IndexOf(looks, look);
            if (k < 0) return -1;
            for (int i = 0; i < k; i++) n += SetsOf(looks[i]);
            return n + Math.Clamp(phase, 0, SetsOf(look) - 1);
        }

        static IEnumerable<Recolor>? Build(string name, IReadOnlyList<Recolor> recolors)
        {
            if (!ModelLooks.TryGetValue(name, out Look[]? looks) || recolors.Count == 0) return null;
            _baseCount[name] = recolors.Count;
            var list = new List<Recolor>(looks.Length);
            foreach (Look look in looks)
            {
                for (int phase = 0; phase < SetsOf(look); phase++) list.Add(Recolored(recolors[0], look, phase));
            }
            return list;
        }

        static Recolor Recolored(Recolor src, Look look, int phase)
        {
            Scheme s = SchemeOf(look) with { Phase = SetsOf(look) > 1 ? phase / (float)SetsOf(look) : -1 };
            var palettes = new List<IReadOnlyList<PaletteData>>(src.PaletteData.Count);
            foreach (IReadOnlyList<PaletteData> pal in src.PaletteData)
            {
                var p = new PaletteData[pal.Count];
                for (int i = 0; i < pal.Count; i++) p[i] = new PaletteData(Map(pal[i].Data, s));
                palettes.Add(p);
            }
            var textures = new List<IReadOnlyList<TextureData>>(src.TextureData.Count);
            for (int t = 0; t < src.TextureData.Count; t++)
            {
                IReadOnlyList<TextureData> data = src.TextureData[t];
                if (src.Textures[t].Format != TextureFormat.DirectRgb)
                {
                    textures.Add(data);
                    continue;
                }
                var d = new TextureData[data.Count];
                for (int i = 0; i < data.Count; i++) d[i] = new TextureData(Map((ushort)data[i].Data, s), data[i].Alpha);
                textures.Add(d);
            }
            return new Recolor(phase == 0 ? $"look_{look}" : $"look_{look}_{phase}", src.Textures, src.Palettes, textures, palettes);
        }

        // a look's colours: the source's brightness through a ramp (shadow -> armour -> highlight); its saturated bright
        // colours (lights, visor, glowing panels) to the glow colour, keeping their brightness. Samus's armour is itself
        // saturated (orange, red): for her only the green lights (and the morph ball's hot core) glow.
        readonly record struct Scheme((float R, float G, float B) Shadow, (float R, float G, float B) Armour,
            (float R, float G, float B) Highlight, (float R, float G, float B) Glow, float Gamma, bool GreenGlowOnly = false,
            float Phase = -1);

        static Scheme SchemeOf(Look look) => look switch
        {
            // the troopers: near-black armour (MPH's darker half of Weavel; owner 10-09: a little lighter, toward a dark
            // grey), the skin's colour on its brightest plates and lights
            Look.PowerTrooper => new((0.06f, 0.06f, 0.06f), (0.20f, 0.20f, 0.19f), (0.95f, 0.85f, 0.25f), (1f, 0.93f, 0.34f), 1.55f),
            Look.IceTrooper => new((0.06f, 0.06f, 0.07f), (0.20f, 0.21f, 0.23f), (0.90f, 0.95f, 1f), (0.60f, 0.90f, 1f), 1.55f),
            Look.WaveTrooper => new((0.06f, 0.05f, 0.07f), (0.20f, 0.18f, 0.23f), (0.68f, 0.30f, 1f), (0.85f, 0.45f, 1f), 1.55f),
            Look.PlasmaTrooper => new((0.07f, 0.05f, 0.05f), (0.22f, 0.18f, 0.18f), (1f, 0.22f, 0.12f), (1f, 0.40f, 0.18f), 1.55f),
            // Dark Samus: navy armour, lights a lighter phazon blue, a light-blue shimmer moving through the armour (Phase)
            Look.DarkSamus => new((0.01f, 0.02f, 0.05f), (0.07f, 0.13f, 0.34f), (0.30f, 0.45f, 0.78f), (0.45f, 0.85f, 1f), 1.2f, GreenGlowOnly: true),
            _ => new((0, 0, 0), (0.5f, 0.5f, 0.5f), (1, 1, 1), (1, 1, 1), 1),
        };

        // test hook: one 15-bit colour through a look (LookAlikeTest's palette sheets)
        public static ushort MapColor(ushort c, Look look) => Map(c, SchemeOf(look));

        static ushort Map(ushort c, Scheme s)
        {
            float r = (c & 0x1F) / 31f, g = ((c >> 5) & 0x1F) / 31f, b = ((c >> 10) & 0x1F) / 31f;
            float max = MathF.Max(r, MathF.Max(g, b)), min = MathF.Min(r, MathF.Min(g, b));
            float sat = max > 0 ? (max - min) / max : 0;
            float lum = 0.299f * r + 0.587f * g + 0.114f * b;
            (float R, float G, float B) o;
            bool glow = s.GreenGlowOnly
                ? sat > 0.35f && max > 0.35f && g >= r && g >= b || lum > 0.88f && sat > 0.2f
                : sat > 0.55f && max > 0.6f;
            // the shimmer wave at this colour's brightness (0-1; none without a phase)
            float wave = s.Phase < 0 ? 0 : MathF.Pow(0.5f + 0.5f * MathF.Sin(2 * MathF.PI * (s.Phase - lum * 1.6f)), 3);
            if (glow)
            {
                float k = max * (s.Phase < 0 ? 1 : 0.93f + 0.12f * wave);
                o = (s.Glow.R * k, s.Glow.G * k, s.Glow.B * k);
            }
            else
            {
                float t = MathF.Pow(Math.Clamp(lum, 0, 1), s.Gamma);
                o = t < 0.5f ? Lerp(s.Shadow, s.Armour, t * 2) : Lerp(s.Armour, s.Highlight, (t - 0.5f) * 2);
                // the phazon shimmer: a light-blue lift riding the wave, more on the brighter (raised) plates (owner 10-09:
                // kept faint)
                float lift = wave * (0.04f + 0.09f * t);
                o = (o.R + 0.40f * lift, o.G + 0.70f * lift, o.B + 1.00f * lift);
            }
            int R = Math.Clamp((int)MathF.Round(o.R * 31), 0, 31);
            int G = Math.Clamp((int)MathF.Round(o.G * 31), 0, 31);
            int B = Math.Clamp((int)MathF.Round(o.B * 31), 0, 31);
            return (ushort)(R | G << 5 | B << 10 | (c & 0x8000));
        }

        static (float, float, float) Lerp((float R, float G, float B) a, (float R, float G, float B) b, float t)
            => (a.R + (b.R - a.R) * t, a.G + (b.G - a.G) * t, a.B + (b.B - a.B) * t);
    }
}
