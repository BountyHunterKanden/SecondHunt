using System;
using System.Collections.Generic;
using System.Linq;
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

        // a seat's version of its look, MPH's own palette slots: First-Fourth = the first to fourth seat in the match with
        // that look (MPH's same-hunter palettes pal_01-04), OrangeTeam/GreenTeam in a team mode (pal_Team01/02). The same
        // number as the seat's MPH Recolor (MatchSettings.AssignRecolors, GameState's team recolor)
        // Fifth-Eighth (8-player matches, owner 10-12): the 5th-8th seat with the look, our own colours (MPH has four); their
        // MPH Recolor is 0-3 again (gameplay), the variant is drawing only (VariantFor)
        public enum Variant { First, Second, Third, Fourth, OrangeTeam, GreenTeam, Fifth, Sixth, Seventh, Eighth }

        // the variant for a seat's MPH recolor (0-5; 6-9 = Fifth-Eighth)
        public static Variant VariantOf(int recolor) => (Variant)Math.Clamp(recolor, 0, 9);

        // a seat's drawn variant: its team's in a team mode, else the nth seat with its look (or hunter: SameLookKey),
        // First-Fourth then Fifth-Eighth (MatchSettings.AssignRecolors gives the same seats MPH's 0-3, round again)
        public static Variant VariantFor(MatchSettings settings, int seat)
        {
            int slot = settings.RecolorSlot(seat);
            if (slot >= 4 || seat < 0 || seat >= settings.Players.Count) return VariantOf(slot);
            int n = CopyIndex(settings.Players, seat, settings.RecolorPerSkin != false) % 8;
            return n < 4 ? (Variant)n : (Variant)(n + 2);
        }

        // the 5th-8th's index (0-3) of a variant, -1 for the others (HunterColours' sets)
        public static int NewCopy(Variant v) => v >= Variant.Fifth ? v - Variant.Fifth : -1;

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
            // Limoki's Dark Samus stand-in (DarkSamusStandin.cs): her team and copy colours made from her own paint
            ["DarkSamusStandin_lod0"] = DarkLooks, ["DarkSamusStandinAlt_lod0"] = DarkLooks, ["DarkSamusStandinAltOriginal_lod0"] = DarkLooks,
            // the first-person guns (owner 10-11: the arm cannon in the seat's colours too; PlayerEntity.HostGunLookRecolor)
            ["WeavelGun"] = TrooperLooks, ["SamusGun"] = DarkLooks,
        };

        // MPH's own recolor count per model and the (look, variant) sets appended after it, recorded as the model loads
        static readonly Dictionary<string, (int Count, List<(Look, Variant)> Sets)> _built = new(StringComparer.Ordinal);
        static readonly object _lock = new();
        // the (look, variant) sets models get as they load (Register)
        static readonly List<(Look, Variant)> _sets = new();
        static bool _registered;

        // dev/test switch: draw look-alikes even where the skin is imported (pictures next to the real looks)
        public static bool Force { get; set; }

        // the look-alikes and the character names (D7) are on for this match (set before it starts)
        public static bool On { get; set; }

        // before any of these models loads (models are cached: ones already loaded are dropped so they reload with them).
        // No argument: every look in every variant (tests); else the sets a match needs, kept with the ones registered
        // before (a later match needing none new reloads nothing)
        // the recolor hook (these sets and HunterColours') without registering any look
        public static void InstallHook() => Read.HostExtraRecolors = Build;

        public static void Register() =>
            Register(Enum.GetValues<Look>().Where(l => l != Look.None).SelectMany(l => Enum.GetValues<Variant>().Select(v => (l, v))));

        public static void Register(IEnumerable<(Look Look, Variant Variant)> wanted)
        {
            lock (_lock)
            {
                var add = wanted.Where(w => w.Look != Look.None && !_sets.Contains(w)).Distinct().ToList();
                if (_registered && add.Count == 0) return;
                _registered = true;
                _sets.AddRange(add);
                _sets.Sort((a, b) => ((int)a.Item1 * 16 + (int)a.Item2).CompareTo((int)b.Item1 * 16 + (int)b.Item2));
                Read.HostExtraRecolors = Build;
                _built.Clear();
            }
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
        public static int RecolorFor(Look look, string model, int phase = 0) => RecolorFor(look, Variant.First, model, phase);

        public static int RecolorFor(Look look, Variant variant, string model, int phase = 0)
        {
            if (look == Look.None) return -1;
            lock (_lock)
            {
                if (!_built.TryGetValue(model, out var built)) return -1;
                int n = built.Count;
                foreach ((Look l, Variant v) in built.Sets)
                {
                    if (l == look && v == variant) return n + Math.Clamp(phase, 0, SetsOf(look) - 1);
                    n += SetsOf(l);
                }
                return -1;
            }
        }

        static IEnumerable<Recolor>? Build(string name, IReadOnlyList<Recolor> recolors)
        {
            // the hunters' 5th-8th colours first, right after MPH's own (HunterColours), then the looks'
            List<Recolor> list = HunterColours.Build(name, recolors);
            if (!ModelLooks.TryGetValue(name, out Look[]? looks) || recolors.Count == 0) return list.Count > 0 ? list : null;
            List<(Look, Variant)> sets;
            lock (_lock) sets = _sets.Where(s => Array.IndexOf(looks, s.Item1) >= 0).ToList();
            int first = recolors.Count + list.Count;
            foreach ((Look look, Variant variant) in sets)
            {
                for (int phase = 0; phase < SetsOf(look); phase++) list.Add(Recolored(name, recolors[0], look, variant, phase));
            }
            lock (_lock) _built[name] = (first, sets);
            return list;
        }

        // palettes that aren't armour, kept as they are (owner 10-27, Samsung: the Dark Samus cannon). SamusGun's palette 2
        // is the white of the beam strip, tinted per beam by the game, and palette 0 its translucent edge glow, drawn
        // through a fixed amber: mapped, the strip went a dull yellow
        static readonly Dictionary<string, int[]> KeptPalettes = new(StringComparer.Ordinal)
        {
            ["SamusGun"] = new[] { 0, 2 },
        };

        // Dark Samus's strip texture (SamusGun palette 0) in grey, so the beam colour MPH puts over it shows as it is: her
        // Power Beam's deep phazon blue (owner 10-27, PlayerEntity.HostGunLightColor), where blue over the red-orange texels
        // drew a dark red
        static readonly Dictionary<string, int[]> GreyPalettesDarkSamus = new(StringComparer.Ordinal)
        {
            ["SamusGun"] = new[] { 0 },
        };

        // a palette's brightness as grey, its brightest colour white (alpha bit kept)
        static IReadOnlyList<PaletteData> Greyed(IReadOnlyList<PaletteData> pal)
        {
            float top = 0;
            foreach (PaletteData c in pal) top = MathF.Max(top, Lum(c.Data));
            var p = new PaletteData[pal.Count];
            for (int i = 0; i < pal.Count; i++)
            {
                int v = top > 0 ? Math.Clamp((int)MathF.Round(Lum(pal[i].Data) / top * 31), 0, 31) : 0;
                p[i] = new PaletteData((ushort)(pal[i].Data & 0x8000 | v | v << 5 | v << 10));
            }
            return p;

            static float Lum(ushort d) => 0.3f * (d & 31) + 0.59f * (d >> 5 & 31) + 0.11f * (d >> 10 & 31);
        }

        static Recolor Recolored(string model, Recolor src, Look look, Variant variant, int phase)
        {
            Scheme s = SchemeOf(look, variant) with { Phase = SetsOf(look) > 1 ? phase / (float)SetsOf(look) : -1 };
            int[] kept = KeptPalettes.TryGetValue(model, out int[]? k0) ? k0 : Array.Empty<int>();
            var palettes = new List<IReadOnlyList<PaletteData>>(src.PaletteData.Count);
            for (int k = 0; k < src.PaletteData.Count; k++)
            {
                IReadOnlyList<PaletteData> pal = src.PaletteData[k];
                if (look == Look.DarkSamus && GreyPalettesDarkSamus.TryGetValue(model, out int[]? grey) && Array.IndexOf(grey, k) >= 0)
                {
                    palettes.Add(Greyed(pal));
                    continue;
                }
                if (Array.IndexOf(kept, k) >= 0)
                {
                    palettes.Add(pal);
                    continue;
                }
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
            string name = variant == Variant.First ? $"look_{look}" : $"look_{look}_{variant}";
            return new Recolor(phase == 0 ? name : $"{name}_{phase}", src.Textures, src.Palettes, textures, palettes);
        }

        // a look's colours: the source's brightness through a ramp (shadow -> armour -> highlight); its saturated bright
        // colours (lights, visor, glowing panels) to the glow colour, keeping their brightness. Samus's armour is itself
        // saturated (orange, red): for her only the green lights (and the morph ball's hot core) glow.
        readonly record struct Scheme((float R, float G, float B) Shadow, (float R, float G, float B) Armour,
            (float R, float G, float B) Highlight, (float R, float G, float B) Glow, float Gamma, bool GreenGlowOnly = false,
            float Phase = -1, (float R, float G, float B)? Lift = null);

        // the team colours (MPH Metadata.TeamColors orange 31,19,0 / green 0,31,0, a little softened for armour)
        // the orange is redder and drawn brighter (owner 10-11: MPH's orange read as gold on Dark Samus, the first
        // redder one as brown; the troopers' as vivid as hers)
        public static (float R, float G, float B) TeamColor(Variant v, bool darkSamus = false) =>
            v == Variant.GreenTeam ? (0.30f, 1f, 0.30f) : (1f, 0.45f, 0.04f);

        // a look in a seat's variant. A team seat is that team's colour over the look's dark armour; a trooper duplicate
        // the same in its own colour (owner 10-11: armour-only duplicates read too alike); a Dark Samus duplicate keeps
        // her phazon lights on another armour
        static Scheme SchemeOf(Look look, Variant v)
        {
            Scheme s = SchemeOf(look);
            bool dark = look == Look.DarkSamus;
            if (Copy(v) is int n)
            {
                // two-tone copies (owner 10-12, sheet_looks_v6): a body colour over the armour by brightness, the lights
                // in their own colour
                var (a, b) = dark ? DarkCopy[n] : TrooperCopy[n];
                return s with { Shadow = Lerp(Scale(a, 0.08f), (0.04f, 0.04f, 0.04f), 0.4f), Armour = Scale(a, 0.44f),
                    Highlight = Scale(a, 0.97f), Glow = b, Lift = b, Gamma = 0.9f };
            }
            if (v is Variant.OrangeTeam or Variant.GreenTeam)
            {
                var c = TeamColor(v, dark);
                // (her orange brighter all over: vivid, not brown; the troopers' lights well above their plates, white-hot,
                // owner 10-11)
                return dark
                    ? v == Variant.OrangeTeam
                        ? s with { Shadow = Scale(c, 0.07f), Armour = Scale(c, 0.42f), Highlight = Scale(c, 0.95f), Glow = Lerp(c, (1f, 1f, 1f), 0.25f), Lift = c }
                        : s with { Shadow = Scale(c, 0.03f), Armour = Scale(c, 0.20f), Highlight = Scale(c, 0.62f), Glow = Lerp(c, (1f, 1f, 1f), 0.25f), Lift = c }
                    : v == Variant.OrangeTeam
                        ? s with { Shadow = Scale(c, 0.07f), Armour = Scale(c, 0.40f), Highlight = Scale(c, 0.85f), Glow = Lerp(c, (1f, 1f, 1f), 0.40f) }
                        : s with { Shadow = Lerp(Scale(c, 0.05f), (0.05f, 0.05f, 0.05f), 0.5f), Armour = Scale(c, 0.22f), Highlight = Scale(c, 0.68f), Glow = Lerp(c, (1f, 1f, 1f), 0.40f) };
            }
            return s;
        }

        // the copy number (2-8) of a same-look variant, null for the 1st and the teams
        static int? Copy(Variant v) => v switch
        {
            Variant.Second => 2, Variant.Third => 3, Variant.Fourth => 4,
            Variant.Fifth => 5, Variant.Sixth => 6, Variant.Seventh => 7, Variant.Eighth => 8, _ => null,
        };

        // copies 2-8 (body, lights). Troopers: one colour on the parts the vanilla trooper has coloured, his black kept
        // black (owner 2026-10-29; GxShader's uTint2.w -1), counted across all four troopers like the Varia family
        // (SameLookKey), so the colours stay clear of the four troopers' own yellow, white, purple and red
        static readonly ((float R, float G, float B), (float R, float G, float B))[] TrooperCopy =
        {
            default, default,
            ((0.15f, 0.40f, 1f), (0.15f, 0.40f, 1f)),     // 2 blue
            ((0.10f, 0.90f, 0.20f), (0.10f, 0.90f, 0.20f)), // 3 green
            ((1f, 0.48f, 0.05f), (1f, 0.48f, 0.05f)),     // 4 orange
            ((0.10f, 0.90f, 1f), (0.10f, 0.90f, 1f)),     // 5 cyan
            ((1f, 0.35f, 0.70f), (1f, 0.35f, 0.70f)),     // 6 pink
            ((0.70f, 1f, 0.10f), (0.70f, 1f, 0.10f)),     // 7 lime
            ((1f, 0.70f, 0.0f), (1f, 0.70f, 0.0f)),       // 8 gold (was bronze: dull; 0.62 read orange, 0.80 read as Power, owner 2026-10-29)
        };
        static readonly ((float R, float G, float B), (float R, float G, float B))[] DarkCopy =
        {
            default, default,
            ((0.55f, 0.20f, 1f), (1f, 0.90f, 0.20f)),     // 2 violet, yellow lights
            ((0.15f, 0.90f, 0.20f), (1f, 1f, 1f)),        // 3 green, white
            ((1f, 0.12f, 0.10f), (0.45f, 0.85f, 1f)),     // 4 crimson, her cyan
            ((0.90f, 0.92f, 0.95f), (1f, 0.15f, 0.10f)),  // 5 white, red
            ((0.15f, 0.35f, 1f), (1f, 0.90f, 0.20f)),     // 6 royal blue, yellow (was yellow-gold: too close to 5's white, owner 2026-10-29)
            ((1f, 0.40f, 0.02f), (0.30f, 1f, 0.20f)),     // 7 orange, green
            ((1f, 0.45f, 0.85f), (1f, 1f, 1f)),           // 8 light pink, white
        };

        // a copy's lights colour on a real body (GxShader uTint2 with w 9+k), zero otherwise
        public static (float R, float G, float B) BodyLight(bool darkSamus, Variant v) =>
            Copy(v) is int n ? (darkSamus ? DarkCopy[n] : TrooperCopy[n]).Item2 : (0f, 0f, 0f);

        // the same variants on a REAL (imported) body, as a draw tint (GxShader uTint): w 3+k pulls the whole colour toward
        // the team's by its brightness (7+k: its bright parts lit well above the rest), w 9+k the copies' two tones (uTint2 =
        // BodyLight); null = drawn as imported
        public static (float R, float G, float B, float W)? BodyTint(bool darkSamus, Variant v)
        {
            // (troopers: w 7+k, the same with their bright parts lit well above the plates; Dark Samus's orange: 1.8x
            // and k 0.9, vivid rather than brown)
            if (v is Variant.OrangeTeam or Variant.GreenTeam)
            {
                var c = TeamColor(v, darkSamus);
                if (v == Variant.OrangeTeam) return (c.R * 1.8f, c.G * 1.8f, c.B * 1.8f, darkSamus ? 3.9f : 7.9f);
                return (c.R, c.G, c.B, darkSamus ? 3.72f : 7.9f);
            }
            if (Copy(v) is not int n) return null;
            // copies: w 9+k two-tone, the body colour (white and yellow drawn brighter to read as such) with the lights in
            // BodyLight's
            var bc = (darkSamus ? DarkCopy[n] : TrooperCopy[n]).Item1;
            float wk = bc.R > 0.8f && bc.G > 0.8f ? (bc.B > 0.8f ? 1.7f : 1.35f) : 1f;
            return (bc.R * wk, bc.G * wk, bc.B * wk, 9.9f);
        }

        // the key MPH's same-hunter palettes count by (MatchSettings.AssignRecolors): the same character in the same skin
        // (perSkin, 2026-10-17: Echoes and Corruption Dark Samus, Samus's HD suits and her DS model, Zero
        // Suit, Prime 4 Sylux are each their own look, so a different skin keeps its default colour). A trooper counts by
        // his look (no skin = the Power Trooper). Only the seat's own strings: every device and replay gets the same key
        // whatever it has imported. perSkin false (replays before format 43): by look, others by hunter
        // family: the suits sharing copy colours (SuitColours.FamilyOf) instead of a default (SuitColours.LookOf)
        public static string SameLookKey(MatchPlayer seat, bool perSkin = true, bool family = false)
        {
            Look look = For(seat);
            if (!perSkin) return look != Look.None ? "look:" + look : seat.Hunter.ToString();
            // (the four troopers are one family: their copies never share a colour, owner 2026-10-29)
            if (look is not (Look.None or Look.DarkSamus)) return family && Array.IndexOf(TrooperLooks, look) >= 0 ? "family:trooper" : "look:" + look;
            string skin = Characters.IsOriginal(seat.Skin) ? ""
                : (family ? SuitColours.FamilyOf(seat.Skin!) : SuitColours.LookOf(seat.Skin!)).ToLowerInvariant();
            return seat.Hunter + "/" + (seat.Character?.ToLowerInvariant() ?? "") + "/" + skin;
        }

        // the colours a seat can wear outside team modes: its look's default (0) and seven copy colours (Variant First-Fourth,
        // Fifth-Eighth; MatchPlayer.ColourPick)
        public const int Colours = 8;

        // a seat's same-hunter copy number (0 = its default colour): the first seat of its look keeps 0, the later ones
        // count on through its family, so a family's copies never share a colour (owner 2026-10-24: a Gravity Suit next to
        // a Varia keeps its purple, but their copies would look alike). One look to a family = the nth seat with the look.
        // Lobby picks (MatchPlayer.ColourPick, owner 2026-10-29) go first, in seat order: 0 is held per look, a copy
        // colour per family, and a pick someone earlier already holds falls back to automatic. The automatic seats then
        // take, in seat order, their look's default if it's free, else the family's lowest free copy colour (with no picks
        // that's the count above, so older replays draw the same)
        public static int CopyIndex(IReadOnlyList<MatchPlayer> players, int seat, bool perSkin = true)
            => CopyIndices(players, perSkin)[seat];

        public static int[] CopyIndices(IReadOnlyList<MatchPlayer> players, bool perSkin = true)
        {
            int n = players.Count;
            var looks = new string[n];
            var families = new string[n];
            var result = new int[n];
            var defaults = new HashSet<string>(StringComparer.Ordinal);
            var copies = new HashSet<(string, int)>();
            for (int i = 0; i < n; i++)
            {
                looks[i] = SameLookKey(players[i], perSkin);
                families[i] = SameLookKey(players[i], perSkin, true);
                int pick = players[i].ColourPick;
                result[i] = pick == 0 ? (defaults.Add(looks[i]) ? 0 : -1)
                    : pick > 0 && pick < Colours && copies.Add((families[i], pick)) ? pick : -1;
            }
            for (int i = 0; i < n; i++)
            {
                if (result[i] >= 0) continue;
                if (defaults.Add(looks[i]))
                {
                    result[i] = 0;
                    continue;
                }
                int c = 1;
                while (!copies.Add((families[i], c))) c++;
                result[i] = c;
            }
            return result;
        }

        // the colours a seat may pick in the lobby: those no other seat of its look (default) or family (copies) has
        // picked and holds, plus its own (CopyIndices with this seat's pick taken out, so it doesn't hold one while
        // choosing). An automatic seat holds nothing here: a pick moves it on to its next free colour (owner 2026-10-29:
        // "if someone confirms the same hunter on another color that color is immediately not available")
        public static List<int> FreeColours(IReadOnlyList<MatchPlayer> players, int seat, bool perSkin = true)
        {
            var free = new List<int>();
            if (seat < 0 || seat >= players.Count) return free;
            int own = players[seat].ColourPick;
            players[seat].ColourPick = -1;
            try
            {
                int[] held = CopyIndices(players, perSkin);
                string look = SameLookKey(players[seat], perSkin), family = SameLookKey(players[seat], perSkin, true);
                for (int c = 0; c < Colours; c++)
                {
                    bool taken = false;
                    for (int j = 0; j < players.Count && !taken; j++)
                    {
                        if (j == seat || held[j] != c || players[j].ColourPick != c) continue;
                        taken = c == 0 ? SameLookKey(players[j], perSkin) == look : SameLookKey(players[j], perSkin, true) == family;
                    }
                    if (!taken) free.Add(c);
                }
            }
            finally
            {
                players[seat].ColourPick = own;
            }
            return free;
        }

        static (float R, float G, float B) Scale((float R, float G, float B) c, float k) => (c.R * k, c.G * k, c.B * k);

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
            // (Samus's lights: bright greens. Her cannon's darkest shades are green too, 7,11,8 of 31 lit up as speckles
            // at max > 0.35, owner 10-27)
            bool glow = s.GreenGlowOnly
                ? sat > 0.35f && max > 0.42f && g >= r && g >= b || lum > 0.88f && sat > 0.2f
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
                var lc = s.Lift ?? (0.40f, 0.70f, 1.00f);
                o = (o.R + lc.R * lift, o.G + lc.G * lift, o.B + lc.B * lift);
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
