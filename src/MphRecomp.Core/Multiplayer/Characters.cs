using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MphRead;
using MphRecomp.Anim;

namespace MphRecomp.Multiplayer
{
    // Multiplayer characters and their skins (docs/CHARACTERS_AND_SKINS.md). A character is a base hunter -- everything
    // MphRead simulates: moves, weapons, alt form, bots -- plus its looks; a skin is one look, an HD model folder under
    // <data>/hd/ on the base hunter's rig (IMPORT GAMES puts them there). The seat's skin rides in MatchPlayer.Skin; the
    // match itself never depends on it, so a device without the import draws the base hunter's own DS model.
    public sealed class Character
    {
        public string Id { get; init; } = "";
        public string Name { get; init; } = "";  // as the menus write it
        public Hunter Base { get; init; }
        // ORIGINAL (the base hunter's own DS model) is one of its skins, listed first; a character without it (the
        // Trooper) needs one of its skins imported to be picked at all
        public bool HasOriginal { get; init; }
        // its known skins in menu order (more of its kind found in hd/ go after them, see Characters.Skins)
        public IReadOnlyList<string> Known { get; init; } = Array.Empty<string>();
        internal Func<string, bool> Claims { get; init; } = _ => false;
    }

    public static class Characters
    {
        public const string Original = "original";

        // Samus's suits (Samus's rig): Prime 1, Echoes, Corruption, then Beyond's where a device has them. Not hers: the
        // Brawl trophies (owner 2026-10-07: Brawl stays out for now), Dark Samus (her own character, owner 2026-10-08),
        // the Zero Suit (owner 2026-10-01), and the rig work folders (*Rest, *Tpose, MP1FusionTubes)
        public static readonly Character Samus = new()
        {
            Id = "samus", Name = "SAMUS", Base = Hunter.Samus, HasOriginal = true,
            Known = new[]
            {
                "MP1PowerSuit", "MP1VariaSuit", "MP1GravitySuit", "MP1FusionSuit", "Phazonsuit", "MP1BetaPhazonSuit",
                "MP2VariaSuit", "MP2DarkSuit", "MP2LightSuit", "MP3VariaSuit", "MP3PEDSuit",
                "MP4DefaultSuit", "MP4PsychicSuit", "MP4PsychicGlove", "MP4ViolaSuit", "MP4ViolaSuitIC", "MP4LegacySuit",
            },
            Claims = id => (id.StartsWith("MP", StringComparison.OrdinalIgnoreCase) || id.Equals("Phazonsuit", StringComparison.OrdinalIgnoreCase))
                && TrophyRigs.TryHunterFor(id, out Hunter h) && h == Hunter.Samus
                && !id.Contains("DarkSamus", StringComparison.OrdinalIgnoreCase) && !id.Contains("ZeroSuit", StringComparison.OrdinalIgnoreCase)
                && !id.EndsWith("Rest", StringComparison.OrdinalIgnoreCase) && !id.EndsWith("Tpose", StringComparison.OrdinalIgnoreCase)
                && !id.Equals("MP1FusionTubes", StringComparison.OrdinalIgnoreCase),
        };

        // Prime 1's beam troopers on Weavel's rig and moves (owner 2026-10-08: one character, four skins)
        public static readonly Character Trooper = new()
        {
            Id = "trooper", Name = "TROOPER", Base = Hunter.Weavel, HasOriginal = false,
            Known = new[] { "MP1PowerTrooper", "MP1IceTrooper", "MP1WaveTrooper", "MP1PlasmaTrooper" },
            Claims = id => id.StartsWith("MP1", StringComparison.OrdinalIgnoreCase) && id.EndsWith("Trooper", StringComparison.OrdinalIgnoreCase),
        };

        // Dark Samus: her own character (owner 2026-10-08, D2) with Samus's gameplay and her own look (D8: drawn hovering,
        // her own clips on her own game's skeleton, D18); one skin per game, either import alone gives all of her
        public static readonly Character DarkSamus = new()
        {
            Id = "darksamus", Name = "DARK SAMUS", Base = Hunter.Samus, HasOriginal = false,
            Known = new[] { "MP2DarkSamus", "MP3DarkSamus", "MP2DarkSamusFinal" },
            Claims = id => id.Contains("DarkSamus", StringComparison.OrdinalIgnoreCase) && !id.EndsWith("Ball", StringComparison.OrdinalIgnoreCase)
                && !id.EndsWith("Rest", StringComparison.OrdinalIgnoreCase) && !id.EndsWith("Tpose", StringComparison.OrdinalIgnoreCase),
        };

        // Zero Suit Samus: a new hunter (owner 2026-10-11, docs/ZERO_SUIT_SAMUS.md) with Samus's gameplay for now (her
        // weapons, sounds and morph ball as a placeholder), drawn in Prime 3's body on Weavel's rig and clips holding his gun.
        // Her skin's rig isn't her base hunter's: Normalize lets it through because she claims it
        public static readonly Character ZeroSuit = new()
        {
            Id = "zerosuit", Name = "ZERO SUIT SAMUS", Base = Hunter.Samus, HasOriginal = false,
            Known = new[] { "MP3ZeroSuit" },
            Claims = id => id.Contains("ZeroSuit", StringComparison.OrdinalIgnoreCase)
                && !id.EndsWith("Rest", StringComparison.OrdinalIgnoreCase) && !id.EndsWith("Tpose", StringComparison.OrdinalIgnoreCase),
        };

        // Sylux in Prime 4: Beyond's look (owner 2026-10-17: Prime 4 imports for the next release, Sylux only), on his
        // own DS rig: his own moves and weapons, so ORIGINAL stays his first skin
        public static readonly Character Sylux = new()
        {
            Id = "sylux", Name = "SYLUX", Base = Hunter.Sylux, HasOriginal = true,
            Known = new[] { "MP4Sylux" },
            Claims = id => id.StartsWith("MP", StringComparison.OrdinalIgnoreCase) && TrophyRigs.TryHunterFor(id, out Hunter h) && h == Hunter.Sylux
                && !id.EndsWith("Rest", StringComparison.OrdinalIgnoreCase) && !id.EndsWith("Tpose", StringComparison.OrdinalIgnoreCase),
        };

        public static readonly IReadOnlyList<Character> All = new[] { Samus, Trooper, DarkSamus, ZeroSuit, Sylux };

        // the character id a seat records (MatchPlayer.Character): set for the characters that aren't a hunter's own
        // (TROOPER, DARK SAMUS), null for a hunter in one of its own skins
        public static string? IdFor(Hunter hunter, string? skin) => Of(hunter, skin) is { HasOriginal: false } c ? c.Id : null;

        // the Trooper's hunter ability (owner 2026-10-08, D17): a seat playing him on Weavel has the Wall Cling
        public static bool HasWallCling(MatchPlayer seat) => IsTrooper(seat);

        // ... and his alt form is the Scout Drone (owner 2026-10-08, D10/D10a): Weavel's split, a hovering drone with twin
        // blasters, the half left behind his standing body (MphRead PlayerScoutDrone.cs)
        // (a switch for tests; on since -dronetest passed, 2026-10-08 17:3x)
        public static bool ScoutDroneOn { get; set; } = true;

        public static bool HasScoutDrone(MatchPlayer seat) => ScoutDroneOn && IsTrooper(seat);

        // the Shield Wall (D11) is PARKED for the shield-carrying pirates, a later character (owner 2026-10-08, D16): the
        // Trooper has it only while a test turns it on (-shieldtest), with the code kept as it was
        public static bool ShieldWallTest { get; set; }

        public static bool HasShieldWall(MatchPlayer seat) => ShieldWallTest && IsTrooper(seat);

        // Dark Samus walks hovering: no footsteps, no walk bob (T5 N13, MphRead PlayerHoverWalk.cs)
        public static bool IsDarkSamus(MatchPlayer seat) =>
            seat.Hunter == DarkSamus.Base && string.Equals(seat.Character, DarkSamus.Id, StringComparison.OrdinalIgnoreCase);

        public static bool IsZeroSuit(MatchPlayer seat) =>
            seat.Hunter == ZeroSuit.Base && string.Equals(seat.Character, ZeroSuit.Id, StringComparison.OrdinalIgnoreCase);

        static bool IsTrooper(MatchPlayer seat) =>
            seat.Hunter == Trooper.Base && string.Equals(seat.Character, Trooper.Id, StringComparison.OrdinalIgnoreCase);

        // character abilities are on in local matches and replays; in LAN/online matches when the host's settings say so
        // (MatchSettings.Abilities, owner 2026-10-09; snapshots carry the cling + drone state, Net/SeatAbilities). An older
        // host or a replay recorded before it leaves them off (CHARACTERS_AND_SKINS.md "Network matches")
        public static bool AbilitiesFor(MatchSettings settings) =>
            settings.IsClient || settings.Players.Any(p => p.Remote) ? settings.Abilities == true : true;

        // the seat's app affinity weapon (owner 2026-10-09: the Trooper's is the Pirate Blaster, MatchSettings.PirateBlaster);
        // null = its hunter's own
        public static BeamType? AffinityBeamFor(MatchSettings settings, MatchPlayer seat) =>
            settings.PirateBlaster != false && IsTrooper(seat) ? BeamType.PirateBlaster
            : settings.PirateBlaster != false && settings.Paralyzer != false && IsZeroSuit(seat) ? BeamType.Paralyzer : null;

        // Zero Suit Samus has no armour: 90 health where every other hunter has 100 (owner 2026-10-12, MatchSettings.Paralyzer)
        public static int HealthPercentFor(MatchSettings settings, MatchPlayer seat) =>
            settings.Paralyzer != false && IsZeroSuit(seat) ? MphRead.Paralyzer.HealthPercent : 100;

        // TROOPER and DARK SAMUS: the characters that aren't a hunter's own (the EXTRA CHARACTERS rule)
        public static bool IsExtra(MatchPlayer seat) => seat.Character != null || Of(seat.Hunter, seat.Skin) is { HasOriginal: false };

        // the ADVANCED box's EXTRA PICKUPS (MatchSettings.PirateBlaster) + EXTRA CHARACTERS (owner 2026-10-10) on a match
        // about to be sent or played: no pickups = no characters (the Trooper plays with his own blaster or not at all);
        // characters off = a seat playing one plays its base hunter in that hunter's own look. Called by whoever builds the
        // match (the menus, LAN and online hosts), never on a replay's or a host's match as it arrives
        public static void ApplyExtraRules(MatchSettings s)
        {
            if (s.PirateBlaster == false) s.ExtraCharacters = false;
            foreach (MatchPlayer p in s.Players)
            {
                // a public build's match has no Zero Suit seat (a dev joiner's pick plays Samus): no Paralyzer, no 90 health
                if (s.ExtraCharacters != false && (!IsZeroSuit(p) || Offered(ZeroSuit))) continue;
                if (!IsExtra(p)) continue;
                p.Character = null;
                p.Skin = null;
            }
        }

        // the character a seat plays: its skin's, else the hunter's own (null: a hunter with no skins yet)
        public static Character? Of(Hunter hunter, string? skin)
        {
            if (!string.IsNullOrEmpty(skin) && !IsOriginal(skin))
            {
                foreach (Character c in All)
                {
                    if (c.Base == hunter && c.Claims(skin)) return c;
                }
            }
            return All.FirstOrDefault(c => c.Base == hunter && c.HasOriginal);
        }

        public static bool IsOriginal(string? skin) => string.IsNullOrEmpty(skin) || skin.Equals(Original, StringComparison.OrdinalIgnoreCase);

        // the hunter a seat wearing this skin plays: its rig's, unless a character playing another hunter claims it
        // (Zero Suit: Samus on Weavel's rig)
        public static Hunter HunterFor(string skin, Hunter rig) =>
            All.FirstOrDefault(c => c.Base != rig && !c.HasOriginal && c.Claims(skin))?.Base ?? rig;

        // the skin a seat can wear on this hunter: the HD folder, or null for the hunter's own model (no skin, ORIGINAL,
        // or a skin made for another hunter's rig, unless a character on this hunter claims it: Zero Suit on Weavel's rig)
        public static string? Normalize(Hunter hunter, string? skin) =>
            IsOriginal(skin) || !TrophyRigs.TryHunterFor(skin!, out Hunter h)
            || h != hunter && !All.Any(c => c.Base == hunter && !c.HasOriginal && c.Claims(skin!)) ? null : skin;

        // the character's skins this device has, in menu order: ORIGINAL first where it has one, then its known skins,
        // then any other folder it claims (alphabetical). hdRoot null: the known list as is (no device to look at).
        public static List<string> Skins(Character c, string? hdRoot)
        {
            var list = new List<string>();
            if (!Offered(c)) return list;
            if (c.HasOriginal) list.Add(Original);
            if (hdRoot == null)
            {
                list.AddRange(c.Known);
                return list;
            }
            foreach (string id in c.Known)
            {
                if (Imported(hdRoot, id)) list.Add(id);
            }
            if (Directory.Exists(hdRoot))
            {
                var extra = new List<string>();
                foreach (string dir in Directory.EnumerateDirectories(hdRoot))
                {
                    string id = Path.GetFileName(dir);
                    if (c.Claims(id) && !c.Known.Contains(id, StringComparer.OrdinalIgnoreCase) && Imported(hdRoot, id)) extra.Add(id);
                }
                extra.Sort(StringComparer.OrdinalIgnoreCase);
                list.AddRange(extra);
            }
            return list;
        }

        // Zero Suit Samus isn't in the next release (owner 2026-10-17): a public build has no skins for
        // her, so no picker entry, TRY CONTROLS look or look-alike; dev builds keep her
        public static bool Offered(Character c) => c != ZeroSuit || !MphRecomp.Config.RecompSettings.PublicBuild;

        // a character can be picked when it has at least one skin to wear
        public static bool Available(Character c, string? hdRoot) => c.HasOriginal || Skins(c, hdRoot).Count > 0;

        // hd/<id>/ holds its body (a .dae; the rig caches beside it are checked when it loads), or the sibling
        // bodies/<id>/ holds it as a native body (its own game's skeleton and clips: the Trooper, Dark Samus)
        public static bool Imported(string hdRoot, string id)
        {
            // imported and up to date only (MphRecomp.Import.ImportedContent: a public build uses nothing pushed by hand)
            string dir = Path.Combine(hdRoot, id);
            if (Directory.Exists(dir) && Directory.EnumerateFiles(dir, "*.dae").Any() && MphRecomp.Import.ImportedContent.Usable(hdRoot, id)) return true;
            string bodies = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(hdRoot)) ?? "", "bodies");
            return File.Exists(Path.Combine(bodies, id, "body.bin")) && MphRecomp.Import.ImportedContent.Usable(bodies, id);
        }

        // the menus' name for a skin
        public static string Label(string skin)
        {
            if (IsOriginal(skin)) return "ORIGINAL";
            if (Labels.TryGetValue(skin, out string? label)) return label;
            return skin.ToUpperInvariant();
        }

        static readonly Dictionary<string, string> Labels = new(StringComparer.OrdinalIgnoreCase)
        {
            ["MP1PowerSuit"] = "PRIME POWER SUIT", ["MP1VariaSuit"] = "PRIME VARIA SUIT", ["MP1GravitySuit"] = "PRIME GRAVITY SUIT",
            ["MP1FusionSuit"] = "FUSION SUIT", ["Phazonsuit"] = "PHAZON SUIT", ["MP1BetaPhazonSuit"] = "BETA PHAZON SUIT",
            ["MP2VariaSuit"] = "ECHOES VARIA SUIT", ["MP2DarkSuit"] = "DARK SUIT", ["MP2LightSuit"] = "LIGHT SUIT",
            ["MP3VariaSuit"] = "CORRUPTION VARIA SUIT", ["MP3PEDSuit"] = "PED SUIT",
            ["MP4DefaultSuit"] = "BEYOND VARIA SUIT", ["MP4PsychicSuit"] = "PSYCHIC SUIT", ["MP4PsychicGlove"] = "PSYCHIC GLOVE",
            ["MP4ViolaSuit"] = "VI-O-LA SUIT", ["MP4ViolaSuitIC"] = "VI-O-LA SUIT IC", ["MP4LegacySuit"] = "LEGACY SUIT",
            ["MP1PowerTrooper"] = "POWER TROOPER", ["MP1IceTrooper"] = "ICE TROOPER", ["MP1WaveTrooper"] = "WAVE TROOPER",
            ["MP1PlasmaTrooper"] = "PLASMA TROOPER",
            ["MP2DarkSamus"] = "ECHOES DARK SAMUS", ["MP3DarkSamus"] = "CORRUPTION DARK SAMUS", ["MP2DarkSamusFinal"] = "OVERCHARGED DARK SAMUS",
            ["MP3ZeroSuit"] = "CORRUPTION ZERO SUIT",
            ["MP4Sylux"] = "BEYOND SYLUX",
        };
    }
}
