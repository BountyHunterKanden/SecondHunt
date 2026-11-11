using System;
using System.Collections.Generic;

namespace MphRecomp.Online
{
    // The Gaming Hub's content keys (docs/GAMING_HUB.md): the stable names the server counts hunters, weapons, modes and
    // arenas by. Shared with the server (it compiles Core/Online only), so no MphRead types here. A key the registry
    // doesn't know still counts and shows (Display: upper case, "_" as spaces), so a new hunter, weapon or arena needs no
    // server change; adding it here only gives it a nicer name and its place in the lists.
    public static class ContentKeys
    {
        public const string Hunter = "hunter", Weapon = "weapon", Mode = "mode", Arena = "arena";
        public const string AltForm = "alt_form";

        public sealed record Entry(string Key, string Name, bool Vanilla, int Order);

        private static readonly Entry[] Hunters =
        {
            new("samus", "SAMUS", true, 0), new("kanden", "KANDEN", true, 1), new("trace", "TRACE", true, 2),
            new("sylux", "SYLUX", true, 3), new("noxus", "NOXUS", true, 4), new("spire", "SPIRE", true, 5),
            new("weavel", "WEAVEL", true, 6), new("guardian", "GUARDIAN", true, 7),
            new("trooper", "TROOPER", false, 8), new("dark_samus", "DARK SAMUS", false, 9),
            new("zero_suit_samus", "ZERO SUIT SAMUS", false, 10), new("phazon_suit", "PHAZON SUIT", false, 11),
            new("beta_phazon_suit", "BETA PHAZON SUIT", false, 12),
        };

        // by MphRead's BeamType number (9-10 are Platform/Enemy, never a kill)
        private static readonly string?[] BeamKeys =
        {
            "power_beam", "volt_driver", "missile", "battlehammer", "imperialist", "judicator", "magmaul", "shock_coil",
            "omega_cannon", null, null, "pirate_blaster", "hyper_beam", "paralyzer",
        };

        private static readonly Entry[] Weapons =
        {
            new("power_beam", "POWER BEAM", true, 0), new("volt_driver", "VOLT DRIVER", true, 1),
            new("missile", "MISSILES", true, 2), new("battlehammer", "BATTLEHAMMER", true, 3),
            new("imperialist", "IMPERIALIST", true, 4), new("judicator", "JUDICATOR", true, 5),
            new("magmaul", "MAGMAUL", true, 6), new("shock_coil", "SHOCK COIL", true, 7),
            new("omega_cannon", "OMEGA CANNON", true, 8), new(AltForm, "ALT FORM", true, 9),
            new("pirate_blaster", "PIRATE BLASTER", false, 10), new("hyper_beam", "HYPER BEAM", false, 11),
            new("paralyzer", "PARALYZER", false, 12),
        };

        // HunterLicense.ModeIndex's groups (a team mode counts with its solo mode, as on the license)
        private static readonly string[] ModeKeys =
            { "battle", "bounty", "capture", "defender", "nodes", "prime_hunter", "survival" };

        private static readonly Entry[] Modes =
        {
            new("battle", "BATTLE", true, 0), new("bounty", "BOUNTY", true, 1), new("capture", "CAPTURE", true, 2),
            new("defender", "DEFENDER", true, 3), new("nodes", "NODES", true, 4),
            new("prime_hunter", "PRIME HUNTER", true, 5), new("survival", "SURVIVAL", true, 6),
        };

        // a seat's hunter key: the picked character's name when it isn't a vanilla hunter (TROOPER, DARK SAMUS), else
        // the hunter's name. Character ids that aren't the registry's key map to it (Characters.cs "darksamus")
        private static readonly Dictionary<string, string> CharacterKeys = new()
        {
            ["darksamus"] = "dark_samus", ["zerosuit"] = "zero_suit_samus",
        };

        public static string HunterKey(string? character, string hunter)
        {
            string key = Key(string.IsNullOrEmpty(character) ? hunter : character);
            return CharacterKeys.GetValueOrDefault(key, key);
        }

        public static string? WeaponKey(int beam) => beam >= 0 && beam < BeamKeys.Length ? BeamKeys[beam] : "beam_" + beam;

        public static string ModeKey(int modeIndex) => modeIndex >= 0 && modeIndex < ModeKeys.Length ? ModeKeys[modeIndex] : "mode_" + modeIndex;

        // an arena's key: its room name (MatchSettings.Arena, "MP1 SANCTORUS"), lower case
        public static string ArenaKey(string room) => Key(room);

        // lower case, spaces and dashes as "_", only a-z 0-9 _ kept, at most 40 characters
        public static string Key(string? name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "";
            var chars = new List<char>(name.Length);
            foreach (char c in name.Trim().ToLowerInvariant())
            {
                if (c is >= 'a' and <= 'z' or >= '0' and <= '9' or '_') chars.Add(c);
                else if (c is ' ' or '-' && chars.Count > 0 && chars[^1] != '_') chars.Add('_');
                if (chars.Count == 40) break;
            }
            return new string(chars.ToArray()).TrimEnd('_');
        }

        public static bool Valid(string? key) => !string.IsNullOrEmpty(key) && key == Key(key);

        private static Entry[] Table(string cat) => cat switch
        {
            Hunter => Hunters,
            Weapon => Weapons,
            Mode => Modes,
            _ => Array.Empty<Entry>(),
        };

        public static Entry? Find(string cat, string key) => Array.Find(Table(cat), e => e.Key == key);

        public static IReadOnlyList<Entry> Known(string cat) => Table(cat);

        public static string Display(string cat, string key) => Find(cat, key)?.Name ?? key.Replace('_', ' ').ToUpperInvariant();

        // the list order: the registry's order, then unknown keys by name
        public static int Order(string cat, string key) => Find(cat, key)?.Order ?? 1000;
    }

    // Vanilla's Wi-Fi rank rule, shared so the server can keep each player's star points ("rank_points", the stars and
    // rank titles; docs/GAMING_HUB.md section 3): the same numbers as HunterLicense (arm9 0x020CB4DC / 0x020CB560;
    // MphRead.Tools -hubselftest checks they match). Overall Points are our own capped activity total (Overall below).
    public static class HubRules
    {
        public static readonly int[] RankThresholds = { 40, 140, 390, 750, 851 };
        public const int MaxPoints = 850;

        public static readonly (int Gain, int Loss)[,] RankTable =
        {
            { (2, 1), (4, 1), (7, 1), (10, 0), (15, 0) },
            { (1, 2), (3, 3), (6, 1), (10, 0), (15, 0) },
            { (1, 4), (2, 3), (4, 4), (8, 3), (12, 2) },
            { (1, 8), (1, 8), (4, 6), (5, 5), (8, 4) },
            { (1, 15), (1, 12), (2, 10), (4, 8), (6, 6) },
        };

        public static int Level(int points)
        {
            int level = 0;
            foreach (int t in RankThresholds)
            {
                if (points >= t) level++;
            }
            return Math.Min(level, 4);
        }

        // one player's new points after a match: against every other row not on their team, a better place gains, a
        // worse one loses, a tie nothing (levels from the points before the match)
        public static int After(int points, int place, int team, bool teams, IEnumerable<(int Points, int Place, int Team)> others)
        {
            int level = Level(points), result = points;
            foreach ((int oPoints, int oPlace, int oTeam) in others)
            {
                if (teams && oTeam == team) continue;
                (int gain, int loss) = RankTable[level, Level(oPoints)];
                if (place < oPlace) result = Math.Min(result + gain, MaxPoints);
                else if (place > oPlace) result = Math.Max(result - loss, 0);
            }
            return result;
        }

        // Overall Points: the cart never sent its star points, so Nintendo's board was a server-side total with per-part
        // caps (cheaters with ~1e6 in every stat sat at 21,010) that ranked by play first, then wins. This is our own
        // total built the same way (docs/GAMING_HUB.md section 3); the parts add up to 21,010 at most
        public const int OverallGamesCap = 10000, OverallWinsCap = 6000, OverallKillsCap = 4000, OverallHeadshotsCap = 1000,
            OverallRateCap = 10;
        public const int MaxOverall = OverallGamesCap + OverallWinsCap + OverallKillsCap + OverallHeadshotsCap + OverallRateCap;

        // winRate in tenths of a percent (HubCategory.WinRate), 0 under HubCategory.WinRateMinGames games
        public static int Overall(long games, long wins, long kills, long headshots, long winRate)
        {
            static long Part(long value, long cap) => Math.Clamp(value, 0, cap);
            return (int)(Part(games * 4, OverallGamesCap) + Part(wins * 4, OverallWinsCap) + Part(kills / 10, OverallKillsCap)
                + Part(headshots / 20, OverallHeadshotsCap) + Part(winRate / 100, OverallRateCap));
        }
    }
}
