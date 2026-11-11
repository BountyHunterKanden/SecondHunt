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
        public int[] PendingLosses { get; set; } = Array.Empty<int>(); // +0x1E4.. (0x80 | level per seat): the quit penalty
        public int[] HunterMatches { get; set; } = new int[7]; // +0x74, by Hunter
        public int[] WeaponKills { get; set; } = new int[9];   // +0xAC, by beam
        public int[] ModeMatches { get; set; } = new int[7];   // +0x1B8: battle, bounty, capture, defender, nodes, prime hunter, survival
        public Dictionary<int, int> ArenaWins { get; set; } = new();    // +0xD0, by room id (vanilla: room id - 93)
        public Dictionary<int, int> ArenaMatches { get; set; } = new(); // +0x144
        public int FavoriteHunter { get; set; }           // Field18 bits 3-6
        // The card's hunter of choice can be a character too (owner 2026-11-07: Trooper or Dark Samus, picked the same way
        // as a hunter). CharacterMatches counts a character's matches by id; they're in its base hunter's HunterMatches as
        // well, so the vanilla fields above stay vanilla (cart import, the license check). A hunter's own count is its
        // HunterMatches less its characters'. ChoiceHunter -1 = no character played yet: the card shows FavoriteHunter.
        public Dictionary<string, int> CharacterMatches { get; set; } = new();
        public int ChoiceHunter { get; set; } = -1;       // the base hunter shown
        public string? ChoiceCharacter { get; set; }      // its character id, null = the hunter itself
        // The skin it's shown in (owner 2026-11-07: the most used one, e.g. Echoes or Corruption Dark Samus, the Ice
        // Trooper, Prime 4 Sylux). SkinMatches counts "<character id>/<skin>" ("original" = the hunter's own model);
        // SkinChoice is each character's most played skin, the one just played keeping a tie
        public Dictionary<string, int> SkinMatches { get; set; } = new();
        public Dictionary<string, string> SkinChoice { get; set; } = new();
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
        // A file imported from a DS save passes the bits it came in with (CampaignSaveFile.ImportedArenas/Hunters): those
        // aren't counted, only what's done in SH since (the DS save's license comes in by the license's own IMPORT only).
        public bool FromStory(MphRead.StorySave save, int importedArenas = 0, int importedHunters = 0)
        {
            (int story, int defeated) = StoryUnlocks(save);
            story &= ~importedArenas;
            defeated &= ~importedHunters;
            int arenas = Arenas | story, hunters = Hunters | defeated;
            bool opened = arenas != Arenas || hunters != Hunters;
            // earned by own play: a later import's undo keeps them (even where the import had opened them already)
            Imported?.Own(story, defeated, false, false);
            Arenas = arenas;
            Hunters = hunters;
            return opened;
        }

        // the arena and hunter bits a campaign story opens (FromStory)
        public static (int Arenas, int Hunters) StoryUnlocks(MphRead.StorySave save)
        {
            int arenas = 0;
            foreach ((int room, int arena) in GatewayArenas)
            {
                if (save.CheckVisitedRoom(room)) arenas |= ArenaBit(arena);
            }
            return (arenas, save.DefeatedHunters & 0x7F);
        }

        // the good ending (ov1 0x021045C8): Oubliette opens, and the card shows the game beaten (100%: the octolith)
        public bool GoodEnding(int? percent)
        {
            bool opened = !ArenaOpen(OublietteArena);
            Arenas |= ArenaBit(OublietteArena);
            BeatGame = true;
            if (percent >= 100) Completed = true;
            Imported?.Own(ArenaBit(OublietteArena), 0, true, percent >= 100); // earned here: a later import's undo keeps them
            return opened;
        }

        // The license card's IMPORT (owner 2026-11-06: "in the license menu we do a new importer where you select a save to
        // import all of your license data ... ask if you want to import your name, and if you want to import your
        // stats/data on top of any existing license data or ... just the license as is in the save"; "it automatically
        // imports your rank and license markers like the beat game swirl or octolith"). A vanilla cart's license: merge =
        // its counters added to ours, the best streaks and rank points the higher of the two, the favourites worked out
        // again from the summed counts; replace = the cart's license as it is (the arena counts of our own non-vanilla
        // arenas are dropped too). Either way the rank comes with it, the beaten / 100% marks and the cart's arena and
        // hunter unlocks are added (never taken away), and the notice and the last address stay. Returns a log line.
        // One imported license at a time (owner 2026-11-07: "there should be a notice that you can only import one license
        // and any other import should overwrite data from another import"): what the last import added and what it
        // changed, so the next import takes it back out first (UndoImport) instead of stacking on it. Licenses imported
        // before this record existed have none, and their data stays as it is.
        public sealed class LicenseImport
        {
            public string Save { get; set; } = "";
            public bool Merge { get; set; }
            // the counts it added (replace: the cart's whole counts, ours were set aside)
            public int WifiWins { get; set; }
            public int OnlineMatches { get; set; }
            public int WirelessWins { get; set; }
            public int LocalMatches { get; set; }
            public int BipedKills { get; set; }
            public int AltFormKills { get; set; }
            public int HeadshotKills { get; set; }
            public double WirelessSeconds { get; set; }
            public double WifiSeconds { get; set; }
            public double TotalSeconds { get; set; }
            public int[] HunterMatches { get; set; } = new int[7];
            public int[] WeaponKills { get; set; } = new int[9];
            public int[] ModeMatches { get; set; } = new int[7];
            public int[] ArenaWins { get; set; } = new int[29];
            public int[] ArenaMatches { get; set; } = new int[29];
            // the higher-of values: ours before it (replace: 0) and the license's right after it
            public int PointsBefore { get; set; }
            public int PointsAfter { get; set; }
            public int[] BestsBefore { get; set; } = new int[4]; // Wi-Fi win, Wi-Fi kill, wireless win, wireless kill
            public int[] BestsAfter { get; set; } = new int[4];
            // the marks and unlocks only it opened (own play since then takes them out of here: Own)
            public bool AddedBeat { get; set; }
            public bool AddedCompleted { get; set; }
            public int AddedArenas { get; set; }
            public int AddedHunters { get; set; }

            public void Own(int arenas, int hunters, bool beat, bool completed)
            {
                AddedArenas &= ~arenas;
                AddedHunters &= ~hunters;
                if (beat) AddedBeat = false;
                if (completed) AddedCompleted = false;
            }
        }
        public LicenseImport? Imported { get; set; }

        private int[] Bests() => new[] { WifiBestWinStreak, WifiBestKillStreak, WirelessBestWinStreak, WirelessBestKillStreak };

        // Takes the last import's data back out: its counts subtracted, the rank points and best streaks back to ours plus
        // whatever own play has added since, the marks and unlocks only it opened closed again, the favourites worked out
        // from what's left. The name stays. Returns a log line (null: no import to undo).
        public string? UndoImport()
        {
            if (Imported is not LicenseImport r) return null;
            static int Sub(int v, int n) => Math.Max(0, v - n);
            WifiWins = Sub(WifiWins, r.WifiWins);
            OnlineMatches = Sub(OnlineMatches, r.OnlineMatches);
            WirelessWins = Sub(WirelessWins, r.WirelessWins);
            LocalMatches = Sub(LocalMatches, r.LocalMatches);
            BipedKills = Sub(BipedKills, r.BipedKills);
            AltFormKills = Sub(AltFormKills, r.AltFormKills);
            HeadshotKills = Sub(HeadshotKills, r.HeadshotKills);
            WirelessSeconds = Math.Max(0, WirelessSeconds - r.WirelessSeconds);
            WifiSeconds = Math.Max(0, WifiSeconds - r.WifiSeconds);
            TotalSeconds = Math.Max(0, TotalSeconds - r.TotalSeconds);
            for (int i = 0; i < 7; i++) HunterMatches[i] = Sub(HunterMatches[i], r.HunterMatches.ElementAtOrDefault(i));
            for (int i = 0; i < 9; i++) WeaponKills[i] = Sub(WeaponKills[i], r.WeaponKills.ElementAtOrDefault(i));
            for (int i = 0; i < 7; i++) ModeMatches[i] = Sub(ModeMatches[i], r.ModeMatches.ElementAtOrDefault(i));
            for (int i = 0; i < 29; i++)
            {
                int room = FirstArenaRoom + i;
                if (ArenaWins.TryGetValue(room, out int w)) ArenaWins[room] = Sub(w, r.ArenaWins.ElementAtOrDefault(i));
                if (ArenaMatches.TryGetValue(room, out int n)) ArenaMatches[room] = Sub(n, r.ArenaMatches.ElementAtOrDefault(i));
                if (ArenaWins.GetValueOrDefault(room) == 0) ArenaWins.Remove(room);
                if (ArenaMatches.GetValueOrDefault(room) == 0) ArenaMatches.Remove(room);
            }
            // points: ours before it plus what matches since have gained or lost; a best: a newer own one stays
            RankPoints = Math.Clamp(r.PointsBefore + (RankPoints - r.PointsAfter), 0, RankThresholds[4] - 1);
            RankLevel = RankThresholds.Count(t => RankPoints >= t);
            int[] now = Bests();
            int Back(int i) => now[i] > r.BestsAfter.ElementAtOrDefault(i) ? now[i] : r.BestsBefore.ElementAtOrDefault(i);
            (WifiBestWinStreak, WifiBestKillStreak, WirelessBestWinStreak, WirelessBestKillStreak) = (Back(0), Back(1), Back(2), Back(3));
            if (r.AddedBeat) BeatGame = false;
            if (r.AddedCompleted) Completed = false;
            Arenas &= ~r.AddedArenas;
            Hunters &= ~r.AddedHunters;
            FavoriteHunter = Array.IndexOf(HunterMatches, HunterMatches.Max());
            PickChoice(reset: true);
            FavoriteMode = Array.IndexOf(ModeMatches, ModeMatches.Max());
            FavoriteWeapon = Array.IndexOf(WeaponKills, WeaponKills.Max());
            long Ratio(int room) => ((long)ArenaWins.GetValueOrDefault(room) << 12) / (ArenaMatches.GetValueOrDefault(room) + 1);
            LuckyArena = FirstArenaRoom;
            foreach (int room in ArenaMatches.Keys.Order())
            {
                if (Ratio(room) > Ratio(LuckyArena)) LuckyArena = room;
            }
            Imported = null;
            return $"undid the import of {r.Save}: rank {RankLevel} ({RankPoints} pts), {Wins}/{Matches} wins/matches";
        }

        public string ImportCart(MphRecomp.Campaign.CartUnlocks cart, bool name, bool merge, string save = "")
        {
            string? undone = UndoImport();
            MphRecomp.Campaign.CartLicense c = cart.License ?? new();
            static int Add(int v, int n) => (int)Math.Min((long)v + n, Int32.MaxValue - 1);
            int pointsBefore = merge ? RankPoints : 0;
            int[] bestsBefore = merge ? Bests() : new int[4];
            bool beatBefore = BeatGame, completedBefore = Completed;
            int arenasBefore = Arenas, huntersBefore = Hunters;
            if (name && Clean(c.Name) != MultiplayerMenu.DefaultNickname) Name = Clean(c.Name);
            int[] Sum(int[] ours, int[] theirs) => ours.Select((v, i) => merge ? Add(v, theirs[i]) : theirs[i]).ToArray();
            int Plus(int ours, int theirs) => merge ? Add(ours, theirs) : theirs;
            int Best(int ours, int theirs) => merge ? Math.Max(ours, theirs) : theirs;
            WifiWins = Plus(WifiWins, c.WifiWins);
            OnlineMatches = Plus(OnlineMatches, c.OnlineMatches);
            WirelessWins = Plus(WirelessWins, c.WirelessWins);
            LocalMatches = Plus(LocalMatches, c.LocalMatches);
            BipedKills = Plus(BipedKills, c.BipedKills);
            AltFormKills = Plus(AltFormKills, c.AltFormKills);
            HeadshotKills = Plus(HeadshotKills, c.HeadshotKills);
            // (the disconnects, the card's connection history, aren't imported: owner 2026-11-07, left alone until SH's own
            // way of counting them is worked out)
            WirelessSeconds = (merge ? WirelessSeconds : 0) + c.WirelessSeconds;
            WifiSeconds = (merge ? WifiSeconds : 0) + c.WifiSeconds;
            TotalSeconds = (merge ? TotalSeconds : 0) + c.TotalSeconds;
            WifiBestWinStreak = Best(WifiBestWinStreak, c.WifiBestWinStreak);
            WifiBestKillStreak = Best(WifiBestKillStreak, c.WifiBestKillStreak);
            WirelessBestWinStreak = Best(WirelessBestWinStreak, c.WirelessBestWinStreak);
            WirelessBestKillStreak = Best(WirelessBestKillStreak, c.WirelessBestKillStreak);
            if (!merge)
            {
                // the running streaks: merged, ours go on (the cart's ended on the DS)
                (WifiWinStreak, WifiKillStreak, WirelessWinStreak, WirelessKillStreak) =
                    (c.WifiWinStreak, c.WifiKillStreak, c.WirelessWinStreak, c.WirelessKillStreak);
            }
            HunterMatches = Sum(HunterMatches, c.HunterMatches);
            WeaponKills = Sum(WeaponKills, c.WeaponKills);
            ModeMatches = Sum(ModeMatches, c.ModeMatches);
            if (!merge)
            {
                ArenaWins = new();
                ArenaMatches = new();
            }
            for (int i = 0; i < 29; i++)
            {
                int room = FirstArenaRoom + i;
                if (c.ArenaWins[i] > 0) ArenaWins[room] = Add(ArenaWins.GetValueOrDefault(room), c.ArenaWins[i]);
                if (c.ArenaMatches[i] > 0) ArenaMatches[room] = Add(ArenaMatches.GetValueOrDefault(room), c.ArenaMatches[i]);
            }
            RankPoints = Math.Clamp(Best(RankPoints, c.RankPoints), 0, RankThresholds[4] - 1);
            RankLevel = RankThresholds.Count(t => RankPoints >= t);
            if (merge)
            {
                // the after-match rules over the summed counts: the hunter and the mode keep a tie, the weapon and the
                // lucky arena only a strictly better one
                for (int h = 0; h < 7; h++) if (HunterMatches[h] > HunterMatches[FavoriteHunter]) FavoriteHunter = h;
                PickChoice(reset: false);
                for (int m = 0; m < 7; m++) if (ModeMatches[m] > ModeMatches[FavoriteMode]) FavoriteMode = m;
                for (int b = 0; b < 9; b++) if (WeaponKills[b] > WeaponKills[FavoriteWeapon]) FavoriteWeapon = b;
                long Ratio(int room) => ((long)ArenaWins.GetValueOrDefault(room) << 12) / (ArenaMatches.GetValueOrDefault(room) + 1);
                foreach (int room in ArenaMatches.Keys.Order())
                {
                    if (Ratio(room) > Ratio(LuckyArena)) LuckyArena = room;
                }
            }
            else
            {
                FavoriteHunter = Math.Clamp(c.FavoriteHunter, 0, 6);
                // (the cart's counts replace ours: our characters' matches go with them)
                CharacterMatches.Clear();
                (ChoiceHunter, ChoiceCharacter) = (-1, null);
                SkinMatches.Clear();
                SkinChoice.Clear();
                FavoriteWeapon = Math.Clamp(c.FavoriteWeapon, 0, 8);
                FavoriteMode = Math.Clamp(c.FavoriteMode, 0, 6);
                LuckyArena = FirstArenaRoom + Math.Clamp(c.LuckyArena, 0, 28);
            }
            BeatGame |= c.Beaten || cart.Ending;
            Completed |= c.Completed || cart.Ending100;
            Arenas |= cart.Arenas ?? 0;
            Hunters |= cart.Hunters ?? 0;
            Imported = new LicenseImport
            {
                Save = save, Merge = merge,
                WifiWins = c.WifiWins, OnlineMatches = c.OnlineMatches, WirelessWins = c.WirelessWins, LocalMatches = c.LocalMatches,
                BipedKills = c.BipedKills, AltFormKills = c.AltFormKills, HeadshotKills = c.HeadshotKills,
                WirelessSeconds = c.WirelessSeconds, WifiSeconds = c.WifiSeconds, TotalSeconds = c.TotalSeconds,
                HunterMatches = c.HunterMatches.ToArray(), WeaponKills = c.WeaponKills.ToArray(), ModeMatches = c.ModeMatches.ToArray(),
                ArenaWins = c.ArenaWins.ToArray(), ArenaMatches = c.ArenaMatches.ToArray(),
                PointsBefore = pointsBefore, PointsAfter = RankPoints, BestsBefore = bestsBefore, BestsAfter = Bests(),
                AddedBeat = BeatGame && !beatBefore, AddedCompleted = Completed && !completedBefore,
                AddedArenas = Arenas & ~arenasBefore, AddedHunters = Hunters & ~huntersBefore,
            };
            return (undone != null ? undone + "; " : "") + $"{(merge ? "merged" : "replaced")}{(name ? ", name " + Name : "")}: rank {RankLevel} ({RankPoints} pts), "
                + $"{Wins}/{Matches} wins/matches, beaten {BeatGame}, 100% {Completed}, arenas 0x{Arenas:X}, hunters 0x{Hunters:X}";
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
        // Character: the picked character's name (MatchResults.Player.Character: TROOPER, DARK SAMUS), null = the hunter
        public sealed record MatchRecord(bool Wifi, GameMode Mode, int ArenaRoom, double Seconds, Hunter Hunter, int Place,
            bool Teams, int Team, IReadOnlyList<Opponent> Others, string? Character = null, string? Skin = null);

        // the card's hunter of choice: the base hunter (its portrait) and the character, null = the hunter itself
        public (int Hunter, string? Character) HunterOfChoice() =>
            ChoiceHunter is >= 0 and < 7 ? (ChoiceHunter, ChoiceCharacter) : (Math.Clamp(FavoriteHunter, 0, 6), null);

        // the hunter of choice's most played skin, null = its own look (the card's portrait): TROOPER / DARK SAMUS / the Zero
        // Suit, Samus's suits (owner 2026-11-07) and Sylux in Prime 4; a device without that skin's picture keeps the vanilla one
        public string? ChoiceSkin()
        {
            (int hunter, string? id) = HunterOfChoice();
            MphRecomp.Multiplayer.Character? c = id != null ? MphRecomp.Multiplayer.Characters.All.FirstOrDefault(k => k.Id == id)
                : MphRecomp.Multiplayer.Characters.All.FirstOrDefault(k => k.HasOriginal && (int)k.Base == hunter);
            if (c == null) return null;
            return SkinChoice.TryGetValue(c.Id, out string? skin) && !MphRecomp.Multiplayer.Characters.IsOriginal(skin) ? skin : null;
        }

        // a character's most played skin (an HD skin id), null = its own look or none counted yet
        public string? SkinOf(string characterId) =>
            SkinChoice.TryGetValue(characterId, out string? skin) && !MphRecomp.Multiplayer.Characters.IsOriginal(skin) ? skin : null;

        // a hunter's or a character's own matches (a hunter's: less its characters')
        public int OwnMatches(int hunter, string? character)
        {
            if (character != null) return CharacterMatches.GetValueOrDefault(character);
            int n = HunterMatches[hunter];
            foreach (MphRecomp.Multiplayer.Character c in MphRecomp.Multiplayer.Characters.All)
            {
                if ((int)c.Base == hunter && !c.HasOriginal) n -= CharacterMatches.GetValueOrDefault(c.Id);
            }
            return Math.Max(0, n);
        }

        // the hunter of choice over every hunter and character (after an import): the most own matches, the shown one
        // keeping a tie unless reset; no character played = the vanilla FavoriteHunter (ChoiceHunter -1)
        private void PickChoice(bool reset)
        {
            CharacterMatches = CharacterMatches.Where(kv => kv.Value > 0).ToDictionary(kv => kv.Key, kv => kv.Value);
            if (CharacterMatches.Count == 0)
            {
                (ChoiceHunter, ChoiceCharacter) = (-1, null);
                return;
            }
            (int bestHunter, string? bestCharacter) = reset ? (0, null) : HunterOfChoice();
            int best = reset ? -1 : OwnMatches(bestHunter, bestCharacter);
            var all = Enumerable.Range(0, 7).Select(h => (h, (string?)null)).Concat(MphRecomp.Multiplayer.Characters.All
                .Where(c => !c.HasOriginal && CharacterMatches.ContainsKey(c.Id)).Select(c => ((int)c.Base, (string?)c.Id)));
            foreach ((int h, string? c) in all)
            {
                int n = OwnMatches(h, c);
                if (n > best) (bestHunter, bestCharacter, best) = (h, c, n);
            }
            (ChoiceHunter, ChoiceCharacter) = (bestHunter, bestCharacter);
        }

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
                (int choiceHunter, string? choiceCharacter) = HunterOfChoice();
                HunterMatches[h] = Add(HunterMatches[h]);
                if (HunterMatches[h] >= HunterMatches[FavoriteHunter]) FavoriteHunter = h;
                // the hunter of choice: the same rule over own counts (the one just played keeps a tie)
                string? played = MphRecomp.Multiplayer.Characters.All
                    .FirstOrDefault(c => !c.HasOriginal && (int)c.Base == h && c.Name == m.Character)?.Id;
                if (played != null) CharacterMatches[played] = Add(CharacterMatches.GetValueOrDefault(played));
                // the skin worn (its character's: the played one, else the hunter's own with its skins)
                MphRecomp.Multiplayer.Character? worn = played != null ? MphRecomp.Multiplayer.Characters.All.First(c => c.Id == played)
                    : MphRecomp.Multiplayer.Characters.Of(m.Hunter, m.Skin);
                if (worn != null && (int)worn.Base == h)
                {
                    string skin = MphRecomp.Multiplayer.Characters.IsOriginal(m.Skin) || !worn.Claims(m.Skin!)
                        ? MphRecomp.Multiplayer.Characters.Original : m.Skin!;
                    if (skin != MphRecomp.Multiplayer.Characters.Original || worn.HasOriginal)
                    {
                        string key = worn.Id + "/" + skin;
                        SkinMatches[key] = Add(SkinMatches.GetValueOrDefault(key));
                        if (!SkinChoice.TryGetValue(worn.Id, out string? shown) || SkinMatches[key] >= SkinMatches.GetValueOrDefault(worn.Id + "/" + shown))
                        {
                            SkinChoice[worn.Id] = skin;
                        }
                    }
                }
                if (CharacterMatches.Count > 0)
                {
                    if (ChoiceHunter < 0) (choiceHunter, choiceCharacter) = (FavoriteHunter, null);
                    if (OwnMatches(h, played) >= OwnMatches(choiceHunter, choiceCharacter)) (choiceHunter, choiceCharacter) = (h, played);
                    (ChoiceHunter, ChoiceCharacter) = (choiceHunter, choiceCharacter);
                }
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

        // the quit penalty (owner 2026-11-07: vanilla's, without its cheese). Vanilla's match start (ov0 0x02122704) marks
        // every other human seat's level (no team check) and saves; a match left without results is charged on the next
        // Wi-Fi entry (arm9 0x0205A9F4): each mark loses the rank table's points against that level, then the level from
        // the points (vanilla also counts a disconnect there: not here, owner: connection rate left alone). Vanilla's
        // AfterMatch cleared the marks for any finished match, so a bot match before going online undid it: here only
        // the marked match's own results (CountMatch) clear them, and every match start charges the old ones first
        public void ArmQuitPenalty(IEnumerable<int> levels) => PendingLosses = levels.Where(l => l is >= 0 and <= 4).ToArray();

        public void ClearQuitPenalty() => PendingLosses = Array.Empty<int>();

        // the points taken (0: nothing was pending)
        public int ChargeQuitPenalty()
        {
            if (PendingLosses.Length == 0) return 0;
            int level = Math.Clamp(RankLevel, 0, 4), points = RankPoints;
            foreach (int o in PendingLosses) points = Math.Max(points - RankTable[level, Math.Clamp(o, 0, 4)].Loss, 0);
            int lost = RankPoints - points;
            RankPoints = points;
            RankLevel = RankThresholds.Count(t => points >= t);
            ClearQuitPenalty();
            return lost;
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
            license.PendingLosses = (license.PendingLosses ?? Array.Empty<int>()).Where(l => l is >= 0 and <= 4).Take(7).ToArray();
            license.FavoriteHunter = Math.Clamp(license.FavoriteHunter, 0, 6);
            license.CharacterMatches ??= new();
            license.SkinMatches ??= new();
            license.ClampChoice();
            license.FavoriteWeapon = Math.Clamp(license.FavoriteWeapon, 0, 8);
            license.FavoriteMode = Math.Clamp(license.FavoriteMode, 0, 6);
            license.RankPoints = Math.Clamp(license.RankPoints, 0, RankThresholds[4] - 1);
            license.RankLevel = RankThresholds.Count(t => license.RankPoints >= t);
            return license;

            static int[] Sized(int[]? a, int n) => a?.Length == n ? a : (a ?? Array.Empty<int>()).Concat(new int[n]).Take(n).ToArray();
        }

        // a loaded or another device's license: a choice that isn't a known hunter or character shows FavoriteHunter
        public void ClampChoice(bool counted = true)
        {
            CharacterMatches ??= new();
            // a skin choice only for a known character and a skin it claims (another device's: anything it sent)
            SkinChoice = (SkinChoice ?? new()).Where(kv => kv.Value is { Length: > 0 and < 64 } && kv.Value.All(k => char.IsAsciiLetterOrDigit(k) || k == '_')
                    && MphRecomp.Multiplayer.Characters.All.FirstOrDefault(c => c.Id == kv.Key) is { } c
                    && (MphRecomp.Multiplayer.Characters.IsOriginal(kv.Value) ? c.HasOriginal : c.Claims(kv.Value)))
                .Take(16).ToDictionary(kv => kv.Key, kv => kv.Value);
            bool known = ChoiceCharacter == null ? ChoiceHunter is >= 0 and < 7
                : MphRecomp.Multiplayer.Characters.All.Any(c => !c.HasOriginal && c.Id == ChoiceCharacter && (int)c.Base == ChoiceHunter);
            if (!known || counted && CharacterMatches.Count == 0) (ChoiceHunter, ChoiceCharacter) = (-1, null);
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
