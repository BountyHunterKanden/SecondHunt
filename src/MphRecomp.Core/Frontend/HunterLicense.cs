using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using GameMode = MphRead.GameMode;
using Hunter = MphRead.Hunter;

namespace MphRecomp.Frontend
{
    // The player's profile (Vanilla multiplayer menu M6, decision 5): the nickname other players see, "HUNTER" until the
    // player types one (never the device's or the OS user's name), vanilla's multiplayer unlocks and its Hunter License
    // (rank, stats, hunter of choice; the card on page 60), in its own file beside recomp_settings.json. The friends and
    // rivals roster comes later. No folder (the PC tools): kept in memory only.
    public sealed class HunterLicense
    {
        public const string FileName = "hunter_license.json";
        // the longest name the keyboard takes (vanilla's limit: see MultiplayerMenu's keyboard)
        public static int MaxName { get; set; } = 10;

        public string Name { get; set; } = MultiplayerMenu.DefaultNickname;
        // vanilla's first-visit notice ("your nickname is ...") has been shown
        public bool NoticeSeen { get; set; }
        // the address last joined on the ENTER NEW CODE keypad (join by address): the keypad's start next time
        public string LastAddress { get; set; } = "";

        // Vanilla's multiplayer unlocks (decision 11, owner 2026-10-07: as vanilla; US rev 1 code in docs/MULTIPLAYER_MENU.md
        // M6). Vanilla keeps them cart-wide in the options save (cfg 0x020E8318 +4 arenas, +8 hunters), never locked again:
        // here, with the license. Arenas by bit = room id - 93 (the retail arenas 93-119), hunters by bit = Hunter. A new
        // cart's (arm9 0x0205C328): 9 arenas, Samus + Kanden + Spire.
        public const int NewArenas = 0x161295, NewHunters = 0x23;
        public const int FirstArenaRoom = 93, LastArenaRoom = 119;
        public int Arenas { get; set; } = NewArenas;
        public int Hunters { get; set; } = NewHunters;
        // matches played: vanilla's license +0x2C (wireless) and +0x24 (Wi-Fi). Vanilla's two buckets, by one flag (Nintendo
        // WFC or not); owner 2026-10-07: Wi-Fi = internet battles, wireless = everything else (LAN with people, bot matches)
        public int LocalMatches { get; set; }
        public int OnlineMatches { get; set; }

        // ---- the license card (page 60; vanilla's license 0x020EB8A8, US rev 1, read 2026-10-07: docs/MULTIPLAYER_MENU.md M6) ----
        public int WifiWins { get; set; }                 // +0x20 (a win = first place: arm9 0x020553FC)
        public int WirelessWins { get; set; }             // +0x28
        public int BipedKills { get; set; }               // +0x30 (the kill code, arm9 0x020184F8)
        public int AltFormKills { get; set; }             // +0x34
        public int HeadshotKills { get; set; }            // +0x38
        public int Disconnects { get; set; }              // +0x3C (Wi-Fi only: none until online play)
        public double WirelessSeconds { get; set; }       // +0x40 (each match's clock, arm9 0x02054FC4)
        public double WifiSeconds { get; set; }           // +0x44
        public double TotalSeconds { get; set; }          // +0x48 (vanilla: the main loop's; here the menus' and matches')
        public int WifiBestWinStreak { get; set; }        // +0x4C
        public int WifiBestKillStreak { get; set; }       // +0x50
        public int WirelessBestWinStreak { get; set; }    // +0x54
        public int WirelessBestKillStreak { get; set; }   // +0x58
        public int WifiWinStreak { get; set; }            // +0x1D4 (the current ones: a kill streak runs on into the next
        public int WifiKillStreak { get; set; }           // +0x1D8  match until the player dies or kills a teammate)
        public int WirelessWinStreak { get; set; }        // +0x1DC
        public int WirelessKillStreak { get; set; }       // +0x1E0
        public int RankPoints { get; set; }               // +0x1C
        public int RankLevel { get; set; }                // Field18 bits 24-26: 0 bounty, 1 super, 2 elite, 3 master, 4 legendary
        public int[] HunterMatches { get; set; } = new int[7]; // +0x74, by Hunter
        public int[] WeaponKills { get; set; } = new int[9];   // +0xAC, by beam
        public int[] ModeMatches { get; set; } = new int[7];   // +0x1B8: battle, bounty, capture, defender, nodes, prime hunter, survival
        public Dictionary<int, int> ArenaWins { get; set; } = new();    // +0xD0, by room id (vanilla: room id - 93)
        public Dictionary<int, int> ArenaMatches { get; set; } = new(); // +0x144
        public int FavoriteHunter { get; set; }           // Field18 bits 3-6
        public int FavoriteWeapon { get; set; }           // bits 7-11
        public int FavoriteMode { get; set; }             // bits 12-14
        public int LuckyArena { get; set; } = FirstArenaRoom; // bits 15-19
        public bool BeatGame { get; set; }                // bit 28: the card's alimbic swirl
        public bool Completed { get; set; }               // bit 29: 100%, the octolith instead

        // rank: the points each level starts at (arm9 0x020CB4DC; points stop at 850), and per my level and an opponent's
        // the points a better finish gains and a worse one loses (0x020CB560)
        public static readonly int[] RankThresholds = { 40, 140, 390, 750, 851 };
        private static readonly (int Gain, int Loss)[,] RankTable =
        {
            { (2, 1), (4, 1), (7, 1), (10, 0), (15, 0) },
            { (1, 2), (3, 3), (6, 1), (10, 0), (15, 0) },
            { (1, 4), (2, 3), (4, 4), (8, 3), (12, 2) },
            { (1, 8), (1, 8), (4, 6), (5, 5), (8, 4) },
            { (1, 15), (1, 12), (2, 10), (4, 8), (6, 6) },
        };
        [System.Text.Json.Serialization.JsonIgnore] public int Wins => WifiWins + WirelessWins;
        [System.Text.Json.Serialization.JsonIgnore] public int Matches => OnlineMatches + LocalMatches;

        // the card's mode index of a game mode (arm9 0x020CB0A4, by mode - 3)
        public static int ModeIndex(GameMode mode) => mode switch
        {
            GameMode.Bounty or GameMode.BountyTeams => 1,
            GameMode.Capture => 2,
            GameMode.Defender or GameMode.DefenderTeams => 3,
            GameMode.Nodes or GameMode.NodesTeams => 4,
            GameMode.PrimeHunter => 5,
            GameMode.Survival or GameMode.SurvivalTeams => 6,
            _ => 0,
        };

        // Adventure's arena unlocks (arm9 0x02057000, on entering the room): each planet's gateway room opens its arena;
        // the good ending (ov1 0x021045C8) opens Oubliette
        private static readonly (int Room, int Arena)[] GatewayArenas = { (27, 114), (45, 115), (65, 116), (77, 117) };
        public const int OublietteArena = 118;
        // every second match (local + online even) the next of these opens (arm9 0x02056810, table 0x020CB098)
        private static readonly int[] MatchArenaOrder = { 104, 98, 103, 109, 107, 94, 101, 112, 96, 106, 108 };
        private const int HeadShotArena = 99, StasisBunkerArena = 119;

        public bool ArenaOpen(int room) => room < FirstArenaRoom || room > LastArenaRoom || (Arenas & ArenaBit(room)) != 0;
        public bool HunterOpen(MphRead.Hunter hunter) => (int)hunter is < 0 or > 6 || (Hunters & (1 << (int)hunter)) != 0;
        private static int ArenaBit(int room) => 1 << (room - FirstArenaRoom);

        // What a campaign file has done (vanilla sets these as they happen; ours reads the saves): the hunters it defeated
        // (arm9 0x02018EB8: story +0x1087, MphRead DefeatedHunters) and the gateway rooms it visited. True when anything opened.
        public bool FromStory(MphRead.StorySave save)
        {
            int arenas = Arenas, hunters = Hunters | (save.DefeatedHunters & 0x7F);
            foreach ((int room, int arena) in GatewayArenas)
            {
                if (save.CheckVisitedRoom(room)) arenas |= ArenaBit(arena);
            }
            bool opened = arenas != Arenas || hunters != Hunters;
            Arenas = arenas;
            Hunters = hunters;
            return opened;
        }

        // the good ending (ov1 0x021045C8): Oubliette opens, and the card shows the game beaten (100%: the octolith)
        public bool GoodEnding(int? percent)
        {
            bool opened = !ArenaOpen(OublietteArena);
            Arenas |= ArenaBit(OublietteArena);
            BeatGame = true;
            if (percent >= 100) Completed = true;
            return opened;
        }

        // The kill code's live counts for one match, kept while it runs and counted with it: the main player killed an
        // opponent (arm9 0x02018460: alt form or biped, headshot, the beam's kills), or its kill streak ended (it died,
        // 0x02017D9C, or killed a teammate, 0x020183D4)
        public sealed class MatchKills
        {
            public int Biped, AltForm, Headshots;
            public double Seconds; // the match's own clock (while it's in progress: vanilla's 0x020E553C)
            public readonly int[] Weapons = new int[9];
            public readonly List<bool> Streak = new(); // in order: true = a kill, false = the streak ended

            public void Kill(bool altForm, bool headshot, int beam)
            {
                if (altForm) AltForm++;
                else Biped++;
                if (headshot) Headshots++;
                if (beam is >= 0 and < 9) Weapons[beam]++;
                Streak.Add(true);
            }

            public void StreakEnd() => Streak.Add(false);
        }

        // one counted match: the local player's place (0 = first; a team's for team modes) and every other player's, in
        // seat order; an opponent's rank level -1 = not known (no points against it)
        public sealed record Opponent(Hunter Hunter, int Place, bool Bot, int Team, int RankLevel);
        public sealed record MatchRecord(bool Wifi, GameMode Mode, int ArenaRoom, double Seconds, Hunter Hunter, int Place,
            bool Teams, int Team, IReadOnlyList<Opponent> Others);

        // After a match (arm9 0x020562A4, the kills first as the game counts them live). Returns what opened (log text).
        public string AfterMatch(MatchRecord m, MatchKills? kills)
        {
            static int Add(int v, int n = 1) => (int)Math.Min((long)v + n, Int32.MaxValue - 1);
            bool wifi = m.Wifi;
            if (kills != null)
            {
                BipedKills = Add(BipedKills, kills.Biped);
                AltFormKills = Add(AltFormKills, kills.AltForm);
                HeadshotKills = Add(HeadshotKills, kills.Headshots);
                for (int b = 0; b < 9; b++) WeaponKills[b] = Add(WeaponKills[b], kills.Weapons[b]);
                int streak = wifi ? WifiKillStreak : WirelessKillStreak, best = wifi ? WifiBestKillStreak : WirelessBestKillStreak;
                foreach (bool kill in kills.Streak)
                {
                    streak = kill ? Add(streak) : 0;
                    best = Math.Max(best, streak);
                }
                if (wifi) (WifiKillStreak, WifiBestKillStreak) = (streak, best);
                else (WirelessKillStreak, WirelessBestKillStreak) = (streak, best);
            }
            // a win = first place (0x020553FC); the win streak and the arena's wins
            if (m.Place == 0)
            {
                if (wifi)
                {
                    WifiWins = Add(WifiWins);
                    WifiWinStreak = Add(WifiWinStreak);
                    WifiBestWinStreak = Math.Max(WifiBestWinStreak, WifiWinStreak);
                }
                else
                {
                    WirelessWins = Add(WirelessWins);
                    WirelessWinStreak = Add(WirelessWinStreak);
                    WirelessBestWinStreak = Math.Max(WirelessBestWinStreak, WirelessWinStreak);
                }
                ArenaWins[m.ArenaRoom] = Add(ArenaWins.GetValueOrDefault(m.ArenaRoom));
            }
            else if (wifi) WifiWinStreak = 0;
            else WirelessWinStreak = 0;
            // the first player who placed below the local one with a locked hunter opens that hunter (0x02056424, one a match)
            var opened = new List<string>();
            foreach (Opponent o in m.Others)
            {
                if (o.Place > m.Place && !HunterOpen(o.Hunter))
                {
                    Hunters |= 1 << (int)o.Hunter;
                    opened.Add(o.Hunter.ToString());
                    break;
                }
            }
            if (wifi) OnlineMatches = Add(OnlineMatches);
            else LocalMatches = Add(LocalMatches);
            ArenaMatches[m.ArenaRoom] = Add(ArenaMatches.GetValueOrDefault(m.ArenaRoom));
            if (wifi) WifiSeconds += m.Seconds;
            else WirelessSeconds += m.Seconds;
            TotalSeconds += m.Seconds;
            // the favourites: the mode and the hunter just played win a tie (>=), the lucky arena starts from this one and
            // only a better wins-per-match ratio takes over (wins * 4096 / (matches + 1)), the weapon only a strictly higher count
            int mode = ModeIndex(m.Mode);
            ModeMatches[mode] = Add(ModeMatches[mode]);
            if (ModeMatches[mode] >= ModeMatches[FavoriteMode]) FavoriteMode = mode;
            long Ratio(int room) => ((long)ArenaWins.GetValueOrDefault(room) << 12) / (ArenaMatches.GetValueOrDefault(room) + 1);
            LuckyArena = m.ArenaRoom;
            long lucky = Ratio(m.ArenaRoom);
            foreach (int room in Enumerable.Range(FirstArenaRoom, 29).Concat(ArenaMatches.Keys.Where(r => r is < FirstArenaRoom or > 121).Order()))
            {
                if (Ratio(room) > lucky)
                {
                    LuckyArena = room;
                    lucky = Ratio(room);
                }
            }
            int h = (int)m.Hunter;
            if (h is >= 0 and < 7)
            {
                HunterMatches[h] = Add(HunterMatches[h]);
                if (HunterMatches[h] >= HunterMatches[FavoriteHunter]) FavoriteHunter = h;
            }
            // every second match the next arena of the order opens; a 4-player match opens Head Shot; a Wi-Fi match, or 40
            // wireless ones, Stasis Bunker
            void Open(int room)
            {
                if (ArenaOpen(room)) return;
                Arenas |= ArenaBit(room);
                opened.Add("arena " + room);
            }
            if (Matches % 2 == 0)
            {
                int next = Array.Find(MatchArenaOrder, r => !ArenaOpen(r));
                if (next != 0) Open(next);
            }
            if (m.Others.Count + 1 >= 4) Open(HeadShotArena); // vanilla: a 4-player match (P8: 5-8 count as the 4-player value)
            if (wifi || LocalMatches >= 40) Open(StasisBunkerArena);
            // ranking points (0x02056928): against every other human not on the local player's team, by both levels; a
            // better place gains, a worse one loses, the same nothing; then the level from the points
            int level = Math.Clamp(RankLevel, 0, 4), points = RankPoints;
            foreach (Opponent o in m.Others)
            {
                if (o.Bot || o.RankLevel is < 0 or > 4 || m.Teams && o.Team == m.Team) continue;
                (int gain, int loss) = RankTable[level, o.RankLevel];
                if (m.Place < o.Place) points = Math.Min(points + gain, RankThresholds[4] - 1);
                else if (m.Place > o.Place) points = Math.Max(points - loss, 0);
            }
            RankPoints = points;
            RankLevel = RankThresholds.Count(t => points >= t);
            for (int b = 0; b < 9; b++)
            {
                if (WeaponKills[b] > WeaponKills[FavoriteWeapon]) FavoriteWeapon = b;
            }
            return String.Join(", ", opened);
        }

        static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

        public static HunterLicense Load(string? dir)
        {
            string? path = dir == null ? null : Path.Combine(dir, FileName);
            HunterLicense license = new();
            try
            {
                if (path != null && File.Exists(path)) license = JsonSerializer.Deserialize<HunterLicense>(File.ReadAllText(path)) ?? new();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"hunter license: {path}: {ex.Message}");
            }
            license.Name = Clean(license.Name);
            license.LastAddress ??= "";
            license.HunterMatches = Sized(license.HunterMatches, 7);
            license.WeaponKills = Sized(license.WeaponKills, 9);
            license.ModeMatches = Sized(license.ModeMatches, 7);
            license.ArenaWins ??= new();
            license.ArenaMatches ??= new();
            license.FavoriteHunter = Math.Clamp(license.FavoriteHunter, 0, 6);
            license.FavoriteWeapon = Math.Clamp(license.FavoriteWeapon, 0, 8);
            license.FavoriteMode = Math.Clamp(license.FavoriteMode, 0, 6);
            license.RankPoints = Math.Clamp(license.RankPoints, 0, RankThresholds[4] - 1);
            license.RankLevel = RankThresholds.Count(t => license.RankPoints >= t);
            return license;

            static int[] Sized(int[]? a, int n) => a?.Length == n ? a : (a ?? Array.Empty<int>()).Concat(new int[n]).Take(n).ToArray();
        }

        public void Save(string? dir)
        {
            if (dir == null) return;
            try
            {
                Directory.CreateDirectory(dir);
                File.WriteAllText(Path.Combine(dir, FileName), JsonSerializer.Serialize(this, Options));
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"hunter license: not saved: {ex.Message}");
            }
        }

        // a name as kept: printable characters only, no spaces at the ends, at most MaxName; empty = the default
        public static string Clean(string? name)
        {
            string s = new string((name ?? "").Where(c => c >= ' ' && c <= '~').ToArray()).Trim();
            if (s.Length > MaxName) s = s[..MaxName].TrimEnd();
            return s.Length == 0 ? MultiplayerMenu.DefaultNickname : s;
        }
    }
}
