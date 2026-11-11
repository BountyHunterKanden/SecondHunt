using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using MphRead;
using MphRecomp.Online;

namespace MphRecomp.Frontend
{
    // Made-up GAMING HUB answers (docs/GAMING_HUB.md §8): the PC previews (MphRead.Tools -onlinemenu) and dev builds with a
    // sample_hub.txt beside the settings file. Each board's #1, everyone's favourites, online lately (9 hunters), LEADERBOARDS
    // (a tie, your place under the top 10, FRIENDS; each board's #1 and your places as the hub and your STATS show them), and
    // STATS / FAVOURITES on a license. "SAMUS" in the rows is you (your code, name and tag). Client-side only: the server
    // knows none of these hunters.
    internal static class HubSample
    {
        // the made-up hunters' codes (12 digits, as real ones)
        private const string CodePrefix = "919191";

        internal static string CodeOf(string name)
        {
            uint h = 2166136261;
            foreach (char ch in name) h = (h ^ ch) * 16777619;
            return CodePrefix + (h % 1000000).ToString("D6");
        }

        internal static bool IsSample(string? code) => code != null && code.Length == 12 && code.StartsWith(CodePrefix, StringComparison.Ordinal);

        // a made-up hunter's license (a hunter's name plays that hunter)
        internal static string License(string name)
        {
            int hunter = Enum.TryParse(name, true, out Hunter h) && h <= Hunter.Weavel ? (int)h : (name.Length * 7 + name[0]) % 7;
            int seed = (int)(uint.Parse(CodeOf(name)[6..]) % 97);
            return JsonSerializer.Serialize(new HunterLicense
            {
                RankPoints = 120 + seed * 7, RankLevel = 1 + seed % 4, WifiWins = 20 + seed, OnlineMatches = 60 + seed * 2,
                BipedKills = 300 + seed * 9, AltFormKills = 40 + seed, HeadshotKills = 60 + seed * 2, WifiBestWinStreak = 3 + seed % 9,
                WifiBestKillStreak = 5 + seed % 13, WifiSeconds = 40000 + seed * 900, TotalSeconds = 90000 + seed * 1500,
                FavoriteHunter = hunter, FavoriteWeapon = seed % 9, FavoriteMode = seed % 7, LuckyArena = 100 + seed % 8,
            });
        }

        internal static (HubMsg Hub, Dictionary<string, LeadersMsg> Leaders, StatsMsg Stats) Build(string meCode, string meName, int meTag,
            Func<string, string>? codeOf = null)
        {
            codeOf ??= CodeOf;
            LeaderRow Row(int place, string name, int tag, long value, int rank = 0) => name == "SAMUS"
                ? new() { Place = place, Code = meCode, Name = meName, Tag = meTag, Value = value, Rank = rank }
                : new() { Place = place, Code = codeOf(name), Name = name, Tag = tag, Value = value, Rank = rank };
            ShareRow Share(string key, int share) => new() { Key = key, Share = share };
            var hub = new HubMsg
            {
                Leaders = new Dictionary<string, LeaderRow>
                {
                    [HubCategory.Points] = Row(1, "KANDEN", 4412, 9840, 4), [HubCategory.Games] = Row(1, "ZEPHYR", 2207, 2140),
                    [HubCategory.WinRate] = Row(1, "SAMUS", 815, 871), [HubCategory.WinStreak] = Row(1, "NOXUS", 193, 31),
                    [HubCategory.KillStreak] = Row(1, "VESPER", 3318, 24), [HubCategory.BipedKills] = Row(1, "ZEPHYR", 2207, 48210),
                    [HubCategory.Headshots] = Row(1, "MAGMOOR", 42, 9934), [HubCategory.AltKills] = Row(1, "RAVEN", 7781, 3120),
                },
                Hunters = new() { Share("samus", 284), Share("kanden", 191), Share("noxus", 140), Share("trace", 102) },
                Weapons = new() { Share("power_beam", 301), Share("imperialist", 177), Share("shock_coil", 125) },
                Modes = new() { Share("battle", 512), Share("capture", 160), Share("survival", 91) },
                Arenas = new() { Share("mp1_sanctorus", 230), Share("mp7_processor_core", 188), Share("mp9_cryochasm", 75) },
                Recent = new()
                {
                    Row(0, "ZEPHYR", 2207, 9230, 4), Row(0, "SAMUS", 815, 942, 3), Row(0, "NOXUS", 193, 6120, 4), Row(0, "BLITZ", 6604, 310, 1),
                    Row(0, "ECHO", 2961, 88), Row(0, "NOVA", 1050, 1405, 2), Row(0, "RAVEN", 7781, 2210, 3), Row(0, "LUMINA", 7710, 640, 2),
                    Row(0, "KANDEN", 4412, 9840, 4),
                },
                Players = 1284, Matches = 20431,
            };
            string[] names = { "KANDEN", "ZEPHYR", "NOXUS", "VESPER", "RAVEN", "MAGMOOR", "LUMINA", "NOVA", "BLITZ", "AURORA" };
            var leaders = new Dictionary<string, LeadersMsg>();
            void Board(string cat, string scope, List<LeaderRow> rows, LeaderRow? mine)
                => leaders[cat + "/" + scope] = new LeadersMsg { Category = cat, Scope = scope, Rows = rows, Mine = mine };
            // OVERALL POINTS: a tie for 3rd, you 27th under the list
            Board(HubCategory.Points, LeadersReq.All, names.Select((n, i) => Row(i == 3 ? 3 : i + 1, n, 100 + i,
                i == 3 ? 9840 - 2 * 610 : 9840 - i * 610, 4 - i / 3)).ToList(), Row(27, "SAMUS", 815, 942, 3));
            Board(HubCategory.Points, LeadersReq.Friends, new() { Row(1, "KANDEN", 4412, 9840, 4), Row(2, "ZEPHYR", 2207, 9230, 4),
                Row(3, "SAMUS", 815, 942, 3), Row(4, "AURORA", 5530, 310, 1) }, Row(3, "SAMUS", 815, 942, 3));
            // WIN RATE: you on top (in the list: no row under it), tenths of a percent
            Board(HubCategory.WinRate, LeadersReq.All, new[] { "SAMUS" }.Concat(names.Take(9)).Select((n, i) => Row(i + 1, n, 815,
                871 - i * 41)).ToList(), Row(1, "SAMUS", 815, 871));
            // the other boards (for ALL BOARDS' cards), each topped by the hub's #1 and with your place as on your license (you
            // 9th on headshots)
            List<LeaderRow> Ranked(string cat, long step)
            {
                LeaderRow first = hub.Leaders[cat];
                return new[] { first.Name }.Concat(names.Where(n => n != first.Name)).Take(10)
                    .Select((n, i) => i == 0 ? first : Row(i + 1, n, 100 + i, first.Value - i * step)).ToList();
            }
            Board(HubCategory.Games, LeadersReq.All, Ranked(HubCategory.Games, 120), Row(88, "SAMUS", 815, 140));
            Board(HubCategory.WinStreak, LeadersReq.All, Ranked(HubCategory.WinStreak, 2), Row(140, "SAMUS", 815, 7));
            Board(HubCategory.KillStreak, LeadersReq.All, Ranked(HubCategory.KillStreak, 1), Row(61, "SAMUS", 815, 11));
            Board(HubCategory.AltKills, LeadersReq.All, Ranked(HubCategory.AltKills, 180), Row(412, "SAMUS", 815, 121));
            Board(HubCategory.BipedKills, LeadersReq.All, Ranked(HubCategory.BipedKills, 2900), Row(203, "SAMUS", 815, 905));
            List<LeaderRow> heads = Ranked(HubCategory.Headshots, 1215);
            heads[8] = Row(9, "SAMUS", 815, 214);
            heads[9] = Row(10, heads[9].Name, heads[9].Tag, 190);
            Board(HubCategory.Headshots, LeadersReq.All, heads, Row(9, "SAMUS", 815, 214));
            var stats = new StatsMsg
            {
                Code = meCode, Name = meName, Tag = meTag,
                Totals = new Dictionary<string, long>
                {
                    [HubCategory.Points] = 942, [HubCategory.RankPoints] = 512, [HubCategory.Games] = 140, [HubCategory.WinRate] = 871, ["wins"] = 122, ["kills"] = 1240,
                    ["deaths"] = 988, ["seconds"] = 151200, [HubCategory.WinStreak] = 7, [HubCategory.KillStreak] = 11,
                    [HubCategory.AltKills] = 121, [HubCategory.BipedKills] = 905, [HubCategory.Headshots] = 214,
                    [HubCategory.Shots] = 18420,
                },
                Hunters = new() { ["samus"] = 70, ["kanden"] = 41, ["trooper"] = 18, ["noxus"] = 11 },
                Weapons = new() { ["power_beam"] = 520, ["imperialist"] = 310, ["missile"] = 205, ["shock_coil"] = 140, ["judicator"] = 65 },
                Modes = new() { ["battle"] = 90, ["capture"] = 30, ["bounty"] = 20 },
                Arenas = new() { ["mp1_sanctorus"] = 44, ["mp4_highground"] = 39, ["mp9_cryochasm"] = 31, ["mp6_headshot"] = 26 },
                Places = new Dictionary<string, int>
                {
                    [HubCategory.Points] = 27, [HubCategory.Games] = 88, [HubCategory.WinRate] = 1, [HubCategory.WinStreak] = 140,
                    [HubCategory.KillStreak] = 61, [HubCategory.AltKills] = 412, [HubCategory.BipedKills] = 203, [HubCategory.Headshots] = 9,
                },
            };
            return (hub, leaders, stats);
        }

        // a made-up hunter's STATS / FAVOURITES: yours reshuffled, their own hunter on top
        internal static StatsMsg StatsFor(PlayerCard c, StatsMsg mine)
        {
            int seed = (int)(uint.Parse(c.Code[6..]) % 97);
            string[] hunters = { "samus", "kanden", "spire", "trace", "noxus", "sylux", "weavel", "trooper", "dark_samus" };
            string top = Enum.TryParse(c.Name, true, out Hunter h) && h <= Hunter.Weavel ? h.ToString().ToLowerInvariant() : hunters[seed % hunters.Length];
            long Scale(long v) => v * (60 + seed) / 100;
            return new StatsMsg
            {
                Code = c.Code, Name = c.Name, Tag = c.Tag,
                Totals = mine.Totals.ToDictionary(t => t.Key, t => t.Key == HubCategory.WinRate ? 300 + seed * 5 : Scale(t.Value)),
                Hunters = new() { [top] = 60 + seed, [hunters[(seed + 3) % hunters.Length]] = 30, [hunters[(seed + 5) % hunters.Length]] = 12 },
                Weapons = mine.Weapons.OrderBy(w => (w.Key.Length * 5 + seed) % 7).Select((w, i) => (w.Key, Value: 400 - i * 70 + seed))
                    .ToDictionary(w => w.Key, w => (int)w.Value),
                Modes = new() { ["battle"] = 50 + seed, ["survival"] = 20, ["capture"] = 9 },
                Arenas = mine.Arenas.Reverse().Select((a, i) => (a.Key, Value: 50 - i * 8)).ToDictionary(a => a.Key, a => a.Value),
                Places = mine.Places.ToDictionary(p => p.Key, p => 1 + (p.Value * 7 + seed) % 300),
            };
        }
    }
}
