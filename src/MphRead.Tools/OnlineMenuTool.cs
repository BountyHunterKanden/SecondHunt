using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using MphRecomp.Campaign;
using MphRecomp.Frontend;
using MphRecomp.Multiplayer;
using MphRecomp.Multiplayer.Net;
using MphRecomp.Online;
using MphRecomp.Online.Voice;
using MphRecomp.Replay;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace MphRead
{
    // ONLINE PLAY's menus (Core Frontend/OnlineMenus.cs), on the PC:
    //   -onlinemenu                 the hub, BROWSE, a game's details, WHO CAN JOIN, the friends pages, HUNTER LICENSE, ONLINE
    //                               SETTINGS and the first-visit notice with
    //                               sample data (no server), 1920 x 1080 -> extract_out/online_menus/build/online_<page>.png,
    //                               to set beside the picked mockups
    //   -onlinemenutest [server=]   live: a PC host opens a game on the server (default the live one), then the menus (a
    //                               FrontendSession) find it on BROWSE, open it, press JOIN, pick Kanden on SELECT HUNTER,
    //                               get a seat and wait; the host starts; PASS = the menus ask to play it (StartMatch,
    //                               net=online) as the joiner's seat, as Kanden, and after it (results handed in as the
    //                               match screen leaves them) open on RESULTS, count a Wi-Fi win and head to head, and
    //                               PLAY AGAIN goes back to the same lobby; the host's START plays the rematch.
    //                               Renders the live hub / browse / details beside the sample ones (online_live_<page>.png)
    //   -onlinefriendstest [server=] live: the menus (a FrontendSession with its own key) and two PC hunters on the server:
    //                               FIND A HUNTER searches one by name and sends ADD FRIEND (they accept); the other enters
    //                               the menus' code while the menus type theirs on the keypad (friends at once, vanilla);
    //                               the first sends a rival request the menus accept on REQUESTS; a friend's row opens their
    //                               HUNTER LICENSE (the license A uploaded), BLOCK / UNBLOCK from it; the menus' own license
    //                               is on the server. PASS = both rosters agree. Renders every tab, FIND A HUNTER page and
    //                               the license (online_live_friends_<tab>.png, _add_<tab>.png, online_live_license.png)
    //   -onlinecreatetest [seats=3] [bots=1] [who=0] [teams=0] [look=trooper|darksamus [skin=] [hd=] [pictures=]] [lobbyedit=1] [server=]
    //                               (lobbyedit=1, release 0.2.0 F2: in the lobby the host's bot card steps the bot's hunter, LEVEL its
    //                               level, REMOVE takes it out and ADD BOT on the freed seat puts one back, the joiner seeing each;
    //                               with teams=1 the joiner asks for the other team (RequestTeam), the host's TEAM swaps it back
    //                               and the joiner's repeating LOOKs don't undo that)
    //                               (rulesedit=1, owner 2026-10-15: the host taps its own seat card, SELECT HUNTER's MODE tab
    //                               opens the mode page (it used to be dead), BOUNTY -> its rules -> SELECT HUNTER -> a pick
    //                               -> the same lobby; the joiner and the server's card see Bounty, the game still hosted)
    //                               (served: a server from 0.8.0 with --matchhost (DEDICATED_SERVERS.md D4) runs the game: the
    //                               menus own its lobby (a joiner with the host's controls); lobbyedit / rulesedit go to the
    //                               server's match host as OWNER messages; START is the server's; the rematch isn't checked)
    //                               live: the menus CREATE a game, a PC joiner joins, START; then the rematch: both matches
    //                               over, the menus on RESULTS, PLAY AGAIN -> the same lobby (the joiner shown on the
    //                               results until its LOOK), the joiner back as Spire, START again
    //   -onlinehost [joiners=1] [bots=1] [mode=Battle] [arena=Combat Hall] [time=120] [wait=900] [server=]
    //                               a PC host for a device to JOIN from the menus: opens a public game, waits up to `wait` s
    //                               for the joiners, starts 3 s after the last one and plays the match (bots as filler)
    internal static class OnlineMenuTool
    {
        private static string RomKey => LanSession.RomKeyFor(Paths.MphKey);
        private static T Wait<T>(System.Threading.Tasks.Task<T> task) => task.GetAwaiter().GetResult();

        private static string OutDir()
        {
            string dir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "extract_out", "online_menus", "build"));
            Directory.CreateDirectory(dir);
            return dir;
        }

        private static Dictionary<string, string> Args(string[] args) => args.Skip(1).Select(a => a.Split('=', 2))
            .Where(p => p.Length == 2).ToDictionary(p => p[0], p => p[1]);

        private static FrontendSession Session(string dir)
        {
            RecompMenus.OnlineNoticeSkipped = true; // (the first-visit notice would take the tests' key presses: -onlinemenu shows it)
            Directory.CreateDirectory(dir);
            return FrontendSession.Load(Paths.FileSystem, settingsPath: Path.Combine(dir, "recomp_settings.json"));
        }

        // `frames` menu frames at 60 Hz in real time (the network answers in real time)
        private static void Run(FrontendSession s, int frames)
        {
            for (int f = 0; f < frames; f++)
            {
                s.Tick();
                Thread.Sleep(16);
            }
        }

        private static bool RunUntil(FrontendSession s, Func<bool> done, int seconds)
        {
            var clock = Stopwatch.StartNew();
            while (!done() && clock.Elapsed.TotalSeconds < seconds)
            {
                s.Tick();
                Thread.Sleep(16);
            }
            return done();
        }

        private static string Shot(FrontendSession s, string name)
        {
            s.Build(1920, 1080);
            using Image<Rgba32> img = FrontendTool.RasterCanvas(s.DrawList, 1920, 1080);
            string path = Path.Combine(OutDir(), name + ".png");
            img.SaveAsPng(path);
            Console.WriteLine($"  page {s.Menu.Page?.Index} -> {path}");
            return path;
        }

        private static PlayerCard Card(string name, int tag, string presence = Presence.Online, bool friend = false, bool rival = false,
            string? game = null) => new()
        {
            Code = (name.GetHashCode() & 0x7fffffff).ToString("D12")[..12], Name = name, Tag = tag, Presence = presence, Friend = friend,
            Rival = rival, GameId = game, License = SamplePick(name),
        };

        // the preview's licenses (their FavoriteHunter = the players' round icons, as their HUNTER LICENSE cards show it): a
        // hunter's name plays that hunter; ONYX and ECHO never uploaded a license (the person disc)
        private static string? SamplePick(string name)
        {
            if (name is "ONYX" or "ECHO") return null;
            int hunter = Enum.TryParse(name, true, out Hunter h) && h <= Hunter.Weavel ? (int)h : (name.Length * 7 + name[0]) % 7;
            return "{\"V\":1,\"OnlineMatches\":3,\"FavoriteHunter\":" + hunter + "}";
        }

        public static void Preview(string[] args)
        {
            if (!ToolPaths.UseDesktopExtraction()) return;
            // noextra=1: ONLINE SETTINGS' NO EXTRA ARENA GAMES on (the Echoes game's JOINs greyed, "extra arenas off", where
            // Echoes is imported; without Echoes they grey anyway: "needs echoes import")
            string previewDir = Path.Combine(Path.GetTempPath(), "online-menu-preview");
            Directory.CreateDirectory(previewDir);
            string onlineSettings = Path.Combine(previewDir, "online_settings.json");
            if (args.Contains("noextra=1")) File.WriteAllText(onlineSettings, "{\"NoExtraArenas\":true}");
            else File.Delete(onlineSettings);
            FrontendSession s = Session(previewDir);
            string version = OnlineMatch.VersionKey(RomKey);
            PlayerCard kanden = Card("KANDEN", 4412, Presence.Lobby, friend: true, game: "g1");
            PlayerCard noxus = Card("NOXUS", 193, Presence.Lobby, rival: true, game: "g2");
            GameCard Game(string id, PlayerCard host, string mode, string arena, int max, int players, bool pub, string? rules, params PlayerCard[] others)
                => new()
                {
                    Id = id, Host = host, Players = players, Members = new[] { host }.Concat(others).ToList(),
                    Info = new GameInfo { Mode = mode, Arena = arena, MaxPlayers = max, Public = pub, Version = version, Rules = rules },
                };
            var games = new List<GameCard>
            {
                Game("g1", kanden, "Battle", "Combat Hall", 4, 2, true, "600 s", Card("SAMUS", 815)),
                Game("g2", noxus, "Capture", "Harvester", 4, 3, false, "600 s", Card("TRACE", 3350), Card("WEAVEL", 6021)),
                Game("g3", Card("RAVEN", 7781), "PrimeHunter", "Sanctorus", 4, 1, true, "300 s"),
                Game("g4", Card("VOID", 9012), "Survival", "Elder Passage", 8, 5, true, "900 s", Card("A", 1), Card("B", 2), Card("C", 3), Card("D", 4)),
                // (release 0.2.0 F2: a friend's game in an Echoes arena; where Echoes isn't imported it's not counted on the hub and
                // FRIENDS ONLINE / the license / FRIENDS grey its JOIN with the reason)
                Game("g5", Card("AURORA", 5530, Presence.Lobby, friend: true, game: "g5"), "Battle", "ECHOES SPIRES", 4, 1, true, "600 s"),
            };
            games[1].Info.Friends = true;
            games[1].Info.Rivals = true;
            games[2].Info.ExtraPickups = games[2].Info.ExtraCharacters = false; // (the browse rows' extra rules icons)
            games[3].Info.ExtraCharacters = false;
            var friends = new List<PlayerCard> { kanden, Card("ZEPHYR", 2207, Presence.Match, friend: true), Card("AURORA", 5530, Presence.Lobby, friend: true, game: "g5") };
            Console.WriteLine($"  ECHOES SPIRES here: {(ExtraArenas.Have("ECHOES SPIRES") ? "imported (JOIN stays live)" : "not imported (JOIN greyed)")}");
            s.Recomp.OnlinePreview(Card("SAMUS", 815), games, friends, 214, 41);
            (int hub, int browse, int detail) = RecompMenus.OnlinePages;
            if (hub < 0)
            {
                Console.WriteLine("FAIL onlinemenu: the online pages aren't installed (no multiplayer menu on this ROM?)");
                return;
            }
            s.Recomp.OpenOnline(s.Menu);
            Run(s, 90);
            Shot(s, "online_hub");
            s.Menu.GoTo(browse);
            Run(s, 90);
            Shot(s, "online_browse");
            s.Navigate(0, -1); // the second row (a rival's, friends + rivals only)
            Run(s, 10);
            s.Press(MenuKeys.A);
            Run(s, 90);
            Shot(s, "online_detail");
            (int who, _) = RecompMenus.OnlineCreatePages;
            s.Menu.GoTo(who);
            Run(s, 90);
            Shot(s, "online_who");

            // FRIENDS AND RIVALS (F_A / F_B / F_C) and FIND A HUNTER (A_A / A_B / from recent)
            var roster = new RosterMsg
            {
                Friends = new List<PlayerCard>
                {
                    kanden, Card("ZEPHYR", 2207, Presence.Match, friend: true), Card("AURORA", 5530, friend: true),
                    Card("MAGMOOR", 42, Presence.Offline, friend: true), Card("VESPER", 3318, Presence.Offline, friend: true),
                    Card("ONYX", 1201, Presence.Offline, friend: true),
                },
                Rivals = new List<PlayerCard> { noxus },
                Requests = new List<RequestInfo>
                {
                    new() { Player = Card("NOXUS", 193, rival: true), Kind = LinkKind.Rival, Incoming = true },
                    new() { Player = Card("LUMINA", 7710), Kind = LinkKind.Friend, Incoming = true },
                    new() { Player = Card("RAVEN", 7781), Kind = LinkKind.Friend, Incoming = false },
                },
            };
            var recent = new List<PlayerCard>
            {
                Card("ZEPHYR", 2207, Presence.Match, friend: true), noxus, Card("BLITZ", 6604), Card("ECHO", 2961, Presence.Offline),
                Card("NOVA", 1050), Card("RAVEN", 7781, Presence.Offline),
            };
            var found = new List<PlayerCard> { kanden, Card("KANDY", 777), Card("KANDOR", 3109, Presence.Offline) };
            s.Recomp.OnlinePreviewFriends(roster, recent, found, "kand", "330177");
            (int friendsPage, int addPage) = RecompMenus.OnlineFriendsPages;
            s.Menu.GoTo(friendsPage);
            Run(s, 90);
            string[] tabs = { "friends", "rivals", "requests", "recent" };
            for (int t = 0; t < tabs.Length; t++)
            {
                s.Recomp.OnCall(s.Menu, 2600 + t, inPause: false);
                Run(s, 20);
                Shot(s, "online_friends_" + tabs[t]);
            }
            s.Recomp.OnCall(s.Menu, 2604, inPause: false); // ADD
            Run(s, 90);
            string[] ways = { "search", "code", "recent" };
            for (int t = 0; t < ways.Length; t++)
            {
                s.Recomp.OnCall(s.Menu, 2640 + t, inPause: false);
                Run(s, 20);
                Shot(s, "online_add_" + ways[t]);
            }

            // HUNTER LICENSE (V_A): a friend in an open lobby (JOIN) with a license and head to head, the card's second tab, and a
            // hunter from RECENT who never uploaded one
            kanden.License = RecompMenus.OnlineLicenseJson(SampleLicense());
            s.Recomp.OnlinePreviewHeadToHead(kanden.Code, new RecompMenus.HeadToHead(12, 7, RecompMenus.HeadToHead.Victory));
            s.Menu.GoTo(friendsPage);
            Run(s, 60);
            s.Recomp.OnCall(s.Menu, 2600, inPause: false);
            s.Recomp.OnlineOpenLicense(s.Menu, kanden);
            Run(s, 90);
            Shot(s, "online_license");
            s.Recomp.OnCall(s.Menu, 2687, inPause: false); // R: the card's next tab
            Run(s, 20);
            Shot(s, "online_license_tab2");
            s.Recomp.OnlineOpenLicense(s.Menu, Card("BLITZ", 6604));
            Run(s, 30);
            Shot(s, "online_license_stranger");

            // ONLINE SETTINGS, its backup code shown, and the first-visit notice over the hub
            s.Menu.GoTo(RecompMenus.OnlineSettingsPage);
            Run(s, 90);
            Shot(s, "online_settings");
            s.Recomp.OnCall(s.Menu, 2708, inPause: false); // BACKUP CODE: show (row 8 since the VOICE / PUSH TO TALK / CHAT rows)
            Run(s, 20);
            Shot(s, "online_settings_backup");
            s.Recomp.OpenOnline(s.Menu);
            Run(s, 90);
            s.Recomp.OnlineShowNotice();
            Run(s, 40);
            Shot(s, "online_notice");
            s.Popup?.Close();
        }

        // a license with something on every tab (an elite hunter who plays Kanden)
        private static HunterLicense SampleLicense() => new()
        {
            RankPoints = 512, RankLevel = 3, WifiWins = 61, OnlineMatches = 140, WirelessWins = 12, LocalMatches = 30, BipedKills = 905,
            AltFormKills = 121, HeadshotKills = 214, WifiBestWinStreak = 7, WifiBestKillStreak = 11, WifiSeconds = 151200,
            WirelessSeconds = 20500, TotalSeconds = 360000, FavoriteHunter = (int)Hunter.Kanden, FavoriteWeapon = 3, FavoriteMode = 0,
            LuckyArena = 104,
        };

        // yes in an Ask box (A focuses YES, A again presses it)
        private static void Yes(FrontendSession s)
        {
            s.Press(MenuKeys.A);
            Run(s, 3);
            s.Press(MenuKeys.A);
            Run(s, 40);
        }

        // live: the menus CREATE a game (hub CREATE -> mode -> rules -> SELECT HUNTER -> WHO CAN JOIN -> CREATE), a PC joiner
        // joins it, the host's lobby shows them, START; PASS = the menus ask to play it as the host (net=online, seat 0)
        public static void CreateTest(string[] args)
        {
            if (!ToolPaths.UseDesktopExtraction()) return;
            Dictionary<string, string> a = Args(args);
            var uri = new Uri(a.GetValueOrDefault("server", RecompMenus.OnlineServer));
            int seats = int.Parse(a.GetValueOrDefault("seats", "3")), bots = int.Parse(a.GetValueOrDefault("bots", "1"));
            int whoRow = int.Parse(a.GetValueOrDefault("who", "0"));
            bool teamPlay = a.GetValueOrDefault("teams", "0") == "1"; // (T8: TEAM PLAY on, the rules page's last row)
            if (a.ContainsKey("server")) RecompMenus.OnlineServerOverride = uri.ToString(); // (the menus too, not just the PC joiner)
            Console.WriteLine($"onlinecreatetest: {uri}, {seats} seats, {bots} bots, who {whoRow}");
            string? fail = null;
            var requests = new List<FrontendRequest>();
            string dir = Path.Combine(Path.GetTempPath(), "online-create-test-" + Guid.NewGuid().ToString("N")[..6]);
            FrontendSession s = Session(dir);
            s.Request = r => requests.Add(r);
            OnlineMatch? joiner = null;
            OnlineIdentity joinerId = OnlineIdentity.Create();
            try
            {
                (int hub, _, _) = RecompMenus.OnlinePages;
                (int whoPage, int lobby) = RecompMenus.OnlineCreatePages;
                s.Recomp.OpenOnline(s.Menu);
                if (!RunUntil(s, () => s.Recomp.OnlineConnected, 20))
                {
                    fail = "the menus didn't connect";
                    return;
                }
                Run(s, 30);
                s.Recomp.OnCall(s.Menu, 2502, inPause: false); // CREATE
                if (!RunUntil(s, () => s.Menu.Page?.Index == MultiplayerMenu.ModePage && s.Menu.PendingPage == -1, 5))
                {
                    fail = $"CREATE didn't open the mode page (page {s.Menu.Page?.Index})";
                    return;
                }
                Run(s, 30);
                s.TouchDs(44, 68); // battle (MultiplayerMenuTool's taps) -> its rules
                Run(s, 40);
                if (teamPlay)
                {
                    s.TouchDs(223, 126);
                    Run(s, 20);
                }
                s.TouchDs(212, 173); // -> SELECT HUNTER
                if (!RunUntil(s, () => s.Menu.Page?.Index == MultiplayerMenu.HunterPage && s.Menu.PendingPage == -1, 5))
                {
                    fail = $"the rules page didn't go on to SELECT HUNTER (page {s.Menu.Page?.Index})";
                    return;
                }
                Run(s, 30);
                s.Multiplayer.OnCall(s.Menu, new MenuAction(), 132, -1); // Samus
                if (!RunUntil(s, () => s.Menu.Page?.Index == whoPage && s.Menu.PendingPage == -1, 5))
                {
                    fail = $"SELECT HUNTER didn't go on to WHO CAN JOIN (page {s.Menu.Page?.Index})";
                    return;
                }
                s.Recomp.OnCall(s.Menu, 2560 + whoRow, inPause: false);
                for (int i = 0; i < 4; i++) s.Recomp.OnCall(s.Menu, 2565, inPause: false); // PLAYERS down to 2
                for (int i = 2; i < seats; i++) s.Recomp.OnCall(s.Menu, 2566, inPause: false);
                for (int i = 0; i < bots; i++) s.Recomp.OnCall(s.Menu, 2569, inPause: false);
                Run(s, 60);
                Shot(s, "online_live_who");
                s.Recomp.OnCall(s.Menu, 2570, inPause: false); // CREATE
                if (!RunUntil(s, () => s.Menu.Page?.Index == lobby && s.Recomp.OnlineRunner != null && s.Menu.PendingPage == -1, 20))
                {
                    fail = $"CREATE didn't open the lobby (page {s.Menu.Page?.Index})";
                    return;
                }
                // (a served game: the menus own its lobby, the server's match host runs it)
                OnlineMatch host = s.Recomp.OnlineRunner!;
                bool served = host.Served;
                GameInfo info = host.Game.Info;
                Console.WriteLine($"  {(served ? "owning served game" : "hosting")} {host.Game.Id}: {info.Mode} in {info.Arena}, max {info.MaxPlayers}, public {info.Public}, friends {info.Friends}, rivals {info.Rivals}");
                if (a.GetValueOrDefault("served") == "1" && !(served && host.Net.IsOwner && !host.Net.IsHost))
                {
                    fail = "served=1: the server didn't run the game (or the menus don't own its lobby)";
                    return;
                }
                Run(s, 60);
                Shot(s, "online_live_lobby_host");
                // a PC joiner (its own key) joins it by id
                OnlineClient jc = Wait(OnlineClient.ConnectAsync(uri, joinerId, "PCJOIN", "onlinecreatetest"));
                joiner = Wait(OnlineMatch.JoinAsync(jc, uri, RomKey, host.Game.Id));
                // look=trooper / darksamus: the PC joiner plays that character (the lobby seat shows its icon, or without
                // hd=/pictures= the look-alike icon, not Weavel's / Samus's)
                string? look = a.GetValueOrDefault("look");
                bool trooper = look == "trooper", dark = look == "darksamus";
                // hd= / pictures=: where SELECT HUNTER's skin pictures come from (CharacterPictures; none on the PC by default)
                if (a.TryGetValue("hd", out string? hdRoot)) MultiplayerMenu.CharacterHdRoot = hdRoot;
                if (a.TryGetValue("pictures", out string? picDir)) MultiplayerMenu.CharacterPicturesDir = picDir;
                if (!joiner.Net.Join(trooper ? Hunter.Weavel : dark ? Hunter.Samus : Hunter.Kanden, "PCJOIN", 3, timeoutMs: 15000,
                    character: trooper ? "trooper" : dark ? "darksamus" : null,
                    skin: trooper ? a.GetValueOrDefault("skin", "MP1PowerTrooper") : dark ? "MP2DarkSamus" : null))
                {
                    fail = "the PC joiner got no seat: " + joiner.Net.Status;
                    return;
                }
                if (!RunUntil(s, () => host.Net.Joined >= 1, 10))
                {
                    fail = "the host's lobby never saw the joiner";
                    return;
                }
                Run(s, 90);
                Shot(s, "online_live_lobby_joined");
                if (a.GetValueOrDefault("lobbyedit") == "1")
                {
                    fail = LobbyEdit(s, host, joiner, seats - bots, teamPlay);
                    if (fail != null) return;
                }
                if (a.GetValueOrDefault("rulesedit") == "1")
                {
                    fail = LobbyRulesEdit(s, host, joiner, lobby, uri, joiner.Client);
                    if (fail != null) return;
                }
                if (a.GetValueOrDefault("talk") == "1")
                {
                    fail = LobbyTalk(s);
                    if (fail != null) return;
                }
                s.Recomp.OnCall(s.Menu, 2591, inPause: false); // START
                if (!RunUntil(s, () => requests.Any(r => r.Kind == FrontendRequestKind.StartMatch), 10))
                {
                    fail = "START didn't start the match";
                    return;
                }
                FrontendRequest start = requests.First(r => r.Kind == FrontendRequestKind.StartMatch);
                Dictionary<string, string> launch = MatchSetupText.LaunchArgs(start.Arg!);
                Console.WriteLine($"  StartMatch: {start.Arg}");
                bool joinerStarted = joiner.Net.WaitForStart(5000);
                MatchSettings? hs = OnlineMatch.Current?.Net.Settings;
                Console.WriteLine("  seats " + string.Join(", ", hs?.Players.Select((p, i) => $"{i}:{p.Hunter}{(p.Bot ? " bot" : p.Remote ? " " + p.Nickname : " local")}") ?? Array.Empty<string>()));
                int expect = Math.Min(8, 2 + bots); // (P8: up to 8 seats)
                if (launch.GetValueOrDefault("net") != "online") fail = "the launch isn't an online match";
                else if (served && (OnlineMatch.Current != host || host.Net.IsHost || hs == null || hs.LocalSeat < 0)) fail = "OnlineMatch.Current isn't the owned served match";
                else if (!served && (OnlineMatch.Current != host || !host.Net.IsHost || hs == null || hs.LocalSeat != 0)) fail = "OnlineMatch.Current isn't the hosted match as seat 0";
                else if (hs.Players.Count != expect) fail = $"{hs.Players.Count} seats, expected {expect} (2 people + {bots} bots)";
                else if (!joinerStarted) fail = "the joiner never got START";
                else if (FrontendSession.ReturnPage() != hub) fail = $"after the match the menus wouldn't come back to the hub (ReturnPage {FrontendSession.ReturnPage()})";
                // (served: the server's match runs to its time limit before its lobby comes back: -onlinetest served=hub matches=2 checks that)
                if (fail == null && !served) fail = HostRematch(dir, lobby, host, joiner, hs!, requests);
            }
            finally
            {
                joiner?.Dispose();
                OnlineMatch.Current?.Dispose();
                s.Recomp.OnlineRunner?.Dispose();
                Thread.Sleep(500);
                DeleteTestProfiles(uri, dir, (joinerId, "PCJOIN"));
                Console.WriteLine(fail == null ? "PASS onlinecreatetest: CREATE -> setup -> WHO CAN JOIN -> lobby -> a joiner -> START -> the host plays it (net=online) -> RESULTS -> PLAY AGAIN -> the same lobby -> rematch"
                    : "FAIL onlinecreatetest: " + fail);
            }
        }

        // lobbyedit=1: the host's bot controls (the card's seat k = the first bot's: the people first, no open seats) and the
        // joiner's team ask; null = all good
        // talk=1 (owner 2026-10-11: the lobby's TAP TO TALK "doesnt appear to be real"): voice on in push to talk, then the
        // TALK bar tapped where it's drawn on a 1920x1080 canvas (TouchCanvas: the device's layout-region path) turns push to
        // talk on, a second tap off
        private static string? LobbyTalk(FrontendSession s)
        {
            VoiceSettings.Current.SetMode(VoiceMode.PushToTalk);
            VoiceChat.Start();
            if (!RunUntil(s, () => VoiceChat.Current != null, 5)) return "no voice chat followed the lobby";
            Run(s, 30);
            Shot(s, "online_live_lobby_talk");
            s.Build(1920, 1080);
            float x = 1640, y = 957; // the TALK bar's centre (mockup px 1450..1830, 922..992 = canvas px at 1920x1080)
            s.TouchCanvas(x, y);
            Run(s, 10);
            bool on = VoiceChat.Current?.PushToTalkHeld == true;
            Console.WriteLine($"  talk: tap at ({x}, {y}) -> push to talk {(on ? "on" : "off")}");
            if (!on) return "a tap on the lobby's TALK didn't turn push to talk on";
            Shot(s, "online_live_lobby_talking");
            s.Build(1920, 1080);
            s.TouchCanvas(x, y);
            Run(s, 10);
            if (VoiceChat.Current?.PushToTalkHeld == true) return "a second tap on TALK didn't stop push to talk";
            Console.WriteLine("  talk ok: TALK tap on / tap off");
            return null;
        }

        // rulesedit=1: the host changes the made game's mode from its own seat card; null = all good
        private static string? LobbyRulesEdit(FrontendSession s, OnlineMatch host, OnlineMatch joiner, int lobby, Uri uri, OnlineClient jc)
        {
            GameMode before = host.Net.Settings!.Mode;
            s.Recomp.OnCall(s.Menu, 2588, inPause: false); // the GAME panel's EDIT RULES (the host's own card, 2580, does the same)
            if (!RunUntil(s, () => s.Menu.Page?.Index == MultiplayerMenu.HunterPage && s.Menu.PendingPage == -1, 5))
                return $"rulesedit: EDIT RULES didn't open SELECT HUNTER (page {s.Menu.Page?.Index})";
            Run(s, 30);
            s.Multiplayer.OnCall(s.Menu, new MenuAction { TargetPage = 0xFF }, 0, -1); // the MODE tab
            if (!RunUntil(s, () => s.Menu.Page?.Index == MultiplayerMenu.ModePage && s.Menu.PendingPage == -1, 5))
                return $"rulesedit: SELECT HUNTER's MODE tab didn't open the mode page (page {s.Menu.Page?.Index})";
            Run(s, 30);
            Shot(s, "online_live_host_modetab");
            // (owner 2026-10-16: the focused mode's description in its own box under the panel, not over the settings)
            s.Navigate(0, -1); // (from the MODE tab into the modes)
            Run(s, 20);
            s.Navigate(1, 0);
            Run(s, 30);
            Shot(s, "online_live_host_modedesc");
            s.TouchDs(167, 54); // bounty -> its rules
            Run(s, 40);
            if (s.Menu.Page?.Index is not (> MultiplayerMenu.ModePage and < MultiplayerMenu.HunterPage))
                return $"rulesedit: BOUNTY didn't open its rules (page {s.Menu.Page?.Index})";
            Shot(s, "online_live_host_rules");
            s.TouchDs(212, 173); // -> SELECT HUNTER
            if (!RunUntil(s, () => s.Menu.Page?.Index == MultiplayerMenu.HunterPage && s.Menu.PendingPage == -1, 5))
                return $"rulesedit: the rules page didn't go on to SELECT HUNTER (page {s.Menu.Page?.Index})";
            Run(s, 30);
            s.Multiplayer.OnCall(s.Menu, new MenuAction(), 132, -1); // Samus -> the lobby
            if (!RunUntil(s, () => s.Menu.Page?.Index == lobby && s.Menu.PendingPage == -1, 5))
                return $"rulesedit: the pick didn't come back to the lobby (page {s.Menu.Page?.Index})";
            if (s.Recomp.OnlineRunner != host) return "rulesedit: the hosted game was closed on the way";
            // (a served game: the server's match host takes it and its LOBBY comes back)
            if (!RunUntil(s, () => host.Net.Settings?.Mode == GameMode.Bounty, 5)) return $"rulesedit: the hosted match is {host.Net.Settings?.Mode}, not Bounty";
            if (!RunUntil(s, () => joiner.Net.Settings?.Mode == GameMode.Bounty, 5)) return $"rulesedit: the joiner still sees {joiner.Net.Settings?.Mode}";
            if (!RunUntil(s, () => host.Game.Info.Mode == nameof(GameMode.Bounty), 5)) return $"rulesedit: the server's card still says {host.Game.Info.Mode}";
            GamesMsg list = Wait(jc.RequestAsync<GamesMsg>(new ListGamesReq()));
            string? listed = list.Games.FirstOrDefault(g => g.Id == host.Game.Id)?.Info.Mode;
            Console.WriteLine($"  rulesedit: {before} -> {host.Net.Settings.Mode} (joiner sees it; the server's list says {listed ?? "not listed"})");
            Run(s, 60);
            Shot(s, "online_live_lobby_rulesedit");
            return null;
        }

        private static string? LobbyEdit(FrontendSession s, OnlineMatch host, OnlineMatch joiner, int k, bool teams)
        {
            MatchSettings H() => host.Net.IsHost ? host.Net.Settings! : host.Net.OwnerLobby()!; // (served: the match host's lobby)
            int BotSeat() => H().Players.FindIndex(p => p.Bot);
            bool JoinerSees(Func<MatchSettings, bool> ok) => RunUntil(s, () => joiner.Net.Settings is MatchSettings js && ok(js), 5);
            int b = BotSeat();
            if (b < 0) return "lobbyedit: no bot in the game";
            Hunter before = H().Players[b].Hunter;
            int level = H().Players[b].BotLevel, count = H().Players.Count;
            s.Recomp.OnCall(s.Menu, 2580 + k, inPause: false); // the bot's card: its hunter
            if (!RunUntil(s, () => H().Players[BotSeat()].Hunter != before, 5)) return "lobbyedit: the bot card's tap didn't change its hunter";
            Hunter after = H().Players[BotSeat()].Hunter;
            if (!JoinerSees(js => js.Players.Count > b && js.Players[b].Hunter == after)) return "lobbyedit: the joiner didn't see the bot's new hunter";
            Console.WriteLine($"  lobbyedit: bot hunter {before} -> {after} (joiner sees it)");
            s.Recomp.OnCall(s.Menu, 2810 + k, inPause: false); // LEVEL
            if (!RunUntil(s, () => H().Players[BotSeat()].BotLevel == (level + 1) % 3, 5)) return "lobbyedit: LEVEL didn't step the bot's level";
            Console.WriteLine($"  lobbyedit: bot level {level + 1} -> {H().Players[BotSeat()].BotLevel + 1}");
            Run(s, 30);
            Shot(s, "online_live_lobby_botedit");
            s.Recomp.OnCall(s.Menu, 2820 + k, inPause: false); // REMOVE
            if (!RunUntil(s, () => H().Players.Count == count - 1, 5)) return "lobbyedit: REMOVE didn't take the bot out";
            if (!JoinerSees(js => js.Players.Count == count - 1)) return "lobbyedit: the joiner still sees the removed bot";
            Console.WriteLine($"  lobbyedit: REMOVE -> {H().Players.Count} seats (joiner sees it)");
            Run(s, 30);
            Shot(s, "online_live_lobby_botremoved");
            s.Recomp.OnCall(s.Menu, 2830 + k, inPause: false); // ADD BOT on the freed seat
            if (!RunUntil(s, () => H().Players.Count == count && BotSeat() >= 0, 5)) return "lobbyedit: ADD BOT didn't add a bot on the freed seat";
            if (!JoinerSees(js => js.Players.Count == count)) return "lobbyedit: the joiner didn't see the added bot";
            Console.WriteLine($"  lobbyedit: ADD BOT -> {H().Players.Count} seats, {H().Players[BotSeat()].Hunter} (joiner sees it)");
            if (teams)
            {
                int j = joiner.Net.Settings!.LocalSeat;
                int now = H().TeamAssignment()[j];
                joiner.Net.RequestTeam(1 - now);
                if (!RunUntil(s, () => H().TeamAssignment()[j] == 1 - now, 5)) return "lobbyedit: the joiner's team ask didn't change its team on the host";
                if (!JoinerSees(js => js.TeamAssignment()[j] == 1 - now)) return "lobbyedit: the joiner didn't see its new team";
                Console.WriteLine($"  lobbyedit: the joiner asked for team {1 - now}: host + joiner agree");
                s.Recomp.OnCall(s.Menu, 2596 + 1, inPause: false); // the host's TEAM on the joiner's card (seat 1): back
                if (!RunUntil(s, () => H().TeamAssignment()[j] == now, 5)) return "lobbyedit: the host's TEAM didn't swap the joiner back";
                for (int i = 0; i < 3; i++)
                {
                    joiner.Net.LobbyTick(inLobby: true); // (the joiner's LOOK repeats: it mustn't undo the host's swap)
                    Run(s, 70);
                }
                if (H().TeamAssignment()[j] != now) return "lobbyedit: the joiner's repeating LOOK undid the host's TEAM swap";
                Console.WriteLine("  lobbyedit: the host's TEAM swap held through the joiner's LOOKs");
            }
            return null;
        }

        // the menus hosted a match that just ended (no match runs here: both sides end it as their match screens would): back
        // on RESULTS, PLAY AGAIN -> the same game's lobby, the PC joiner back (as Spire) -> START again. Null = all as expected.
        private static string? HostRematch(string dir, int lobby, OnlineMatch host, OnlineMatch joiner, MatchSettings played,
            List<FrontendRequest> requests)
        {
            host.EndMatch();
            joiner.EndMatch();
            MatchResults.Last = new MatchResults
            {
                Mode = played.Mode, LocalSeat = 0,
                Rows = played.Players.Select((p, i) => new MatchResults.Player(i, p.Hunter, p.Nickname ?? "", p.Bot, 0, i, 10 - i, 0, 10 - i, i,
                    0, 0, 0, 0, 0, 0, 0, 0, 50, 0)).ToArray(),
            };
            FrontendSession menus = Session(dir);
            menus.Request = r => requests.Add(r);
            menus.Start(MultiplayerMenu.ResultsPage);
            if (!RunUntil(menus, () => menus.Menu.Page?.Index == MultiplayerMenu.ResultsPage && menus.Menu.PendingPage == -1, 10))
                return $"the menus didn't open on RESULTS (page {menus.Menu.Page?.Index})";
            Run(menus, 30);
            menus.Multiplayer.OnCall(menus.Menu, new MenuAction(), 196, -1); // PLAY AGAIN
            if (!RunUntil(menus, () => menus.Menu.Page?.Index == lobby && menus.Recomp.OnlineHosting == host && menus.Menu.PendingPage == -1, 10))
                return $"PLAY AGAIN didn't bring the host back to its lobby (page {menus.Menu.Page?.Index})";
            if (host.Net.NotBack().Count != 1) return $"the joiner should still be on the results ({host.Net.NotBack().Count} not back)";
            Run(menus, 60);
            Shot(menus, "online_live_rematch_waiting");
            joiner.Net.SetLook(Hunter.Spire, null, null, choosing: false); // (the joiner's PLAY AGAIN: back in the lobby)
            if (!RunUntil(menus, () =>
            {
                joiner.Net.LobbyTick(true);
                return host.Net.NotBack().Count == 0 && host.Net.Settings?.Players.Any(p => p.Remote && p.Hunter == Hunter.Spire) == true
                    && joiner.Net.Settings?.Players.Count == played.Players.Count;
            }, 10))
                return $"the joiner never came back as Spire ({host.Net.Status}; not back {host.Net.NotBack().Count})";
            Run(menus, 60);
            Shot(menus, "online_live_rematch_lobby");
            int starts = requests.Count(r => r.Kind == FrontendRequestKind.StartMatch);
            menus.Recomp.OnCall(menus.Menu, 2591, inPause: false); // START
            if (!RunUntil(menus, () => requests.Count(r => r.Kind == FrontendRequestKind.StartMatch) > starts, 10))
                return "START didn't start the rematch";
            if (!joiner.Net.WaitForStart(5000)) return "the joiner never got the rematch's START";
            MatchSettings js = joiner.Net.Settings!;
            if (js.Players[js.LocalSeat].Hunter != Hunter.Spire) return $"the joiner plays {js.Players[js.LocalSeat].Hunter} in the rematch, not Spire";
            Console.WriteLine($"  rematch: same game {host.Game.Id}, the joiner as Spire, started");
            MatchResults.Last = null;
            return null;
        }

        // live: FRIENDS AND RIVALS / FIND A HUNTER against two PC hunters (A: name search + ADD FRIEND, they accept; B: both
        // enter each other's code; A: a rival request the menus accept on REQUESTS); PASS = both sides' rosters agree
        public static void FriendsTest(string[] args)
        {
            if (!ToolPaths.UseDesktopExtraction()) return;
            Dictionary<string, string> a = Args(args);
            var uri = new Uri(a.GetValueOrDefault("server", RecompMenus.OnlineServer));
            // fixed words + digits (random letters can spell words nobody wants on the live server)
            int run = Random.Shared.Next(10000, 100000);
            string nameA = $"TESTA{run}", nameB = $"TESTB{run}", nameC = $"TESTC{run}";
            if (a.ContainsKey("server")) RecompMenus.OnlineServerOverride = uri.ToString(); // (the menus too, not just the PC hunters)
            Console.WriteLine($"onlinefriendstest: {uri}, PC hunters {nameA} and {nameB}");
            string? fail = null;
            FrontendSession s = Session(Path.Combine(Path.GetTempPath(), "online-friends-test-" + Guid.NewGuid().ToString("N")[..6]));
            OnlineClient? ca = null, cb = null, cc = null;
            void Shoot(string name)
            {
                s.Popup?.Close();
                Run(s, 30);
                Shot(s, name);
            }
            try
            {
                (int friendsPage, int addPage) = RecompMenus.OnlineFriendsPages;
                if (friendsPage < 0)
                {
                    fail = "the friends pages aren't installed";
                    return;
                }
                ca = Wait(OnlineClient.ConnectAsync(uri, OnlineIdentity.Create(), nameA, "onlinefriendstest"));
                cb = Wait(OnlineClient.ConnectAsync(uri, OnlineIdentity.Create(), nameB, "onlinefriendstest"));
                PlayerCard meA = ca.Me!, meB = cb.Me!;
                s.Recomp.OpenOnline(s.Menu);
                if (!RunUntil(s, () => s.Recomp.OnlineConnected && s.Recomp.OnlineMeCard?.Code.Length > 0, 20))
                {
                    fail = "the menus didn't connect";
                    return;
                }
                string me = s.Recomp.OnlineMeCard!.Code;
                Console.WriteLine($"  menus: {s.Recomp.OnlineMeCard}, code {FriendCodes.Format(me)}; A {meA} {meA.Code}; B {meB} {meB.Code}");
                RosterMsg? Roster() => s.Recomp.OnlineRoster;
                s.Recomp.OnCall(s.Menu, 2503, inPause: false); // ALL FRIENDS
                if (!RunUntil(s, () => s.Menu.Page?.Index == friendsPage && s.Menu.PendingPage == -1 && Roster() != null, 15))
                {
                    fail = $"ALL FRIENDS didn't open FRIENDS AND RIVALS with a roster (page {s.Menu.Page?.Index})";
                    return;
                }
                Shoot("online_live_friends_empty");

                // 1: FIND A HUNTER > SEARCH NAME, ADD FRIEND; A accepts
                s.Recomp.OnCall(s.Menu, 2604, inPause: false);
                if (!RunUntil(s, () => s.Menu.Page?.Index == addPage && s.Menu.PendingPage == -1, 5))
                {
                    fail = "ADD didn't open FIND A HUNTER";
                    return;
                }
                s.Recomp.OnCall(s.Menu, 2640, inPause: false);
                s.Recomp.OnlineSearch(nameA);
                if (!RunUntil(s, () => s.Recomp.OnlineFound?.Any(c => c.Code == meA.Code) == true, 10))
                {
                    fail = $"searching {nameA} didn't find A ({s.Recomp.OnlineFound?.Count.ToString() ?? "no answer"})";
                    return;
                }
                Shoot("online_live_add_search");
                int row = s.Recomp.OnlineFound!.ToList().FindIndex(c => c.Code == meA.Code);
                s.Recomp.OnCall(s.Menu, 2665 + row, inPause: false); // ADD FRIEND
                if (!RunUntilEvent(s, ca, ev => ev is RequestEvent r && r.From.Code == me && r.Kind == LinkKind.Friend, 10))
                {
                    fail = "A never got the friend request";
                    return;
                }
                Wait(ca.RequestAsync<OkMsg>(new AnswerReq { Code = me, Kind = LinkKind.Friend, Accept = true }));
                if (!RunUntil(s, () => Roster()?.Friends.Any(c => c.Code == meA.Code) == true, 10))
                {
                    fail = "after A accepted, the menus' FRIENDS didn't list A";
                    return;
                }
                Console.WriteLine("  1 ok: search + ADD FRIEND + accept");

                // 2: FRIEND CODE: B enters the menus' code, the menus type B's on the keypad and OK: friends at once
                Wait(cb.RequestAsync<OkMsg>(new AddReq { Code = me, Kind = LinkKind.Friend }));
                s.Recomp.OnCall(s.Menu, 2641, inPause: false);
                foreach (char d in meB.Code) s.Recomp.OnCall(s.Menu, 2650 + (d - '0'), inPause: false);
                if (!RunUntil(s, () => s.Recomp.OnlineCodeName != null, 10))
                {
                    fail = "the typed code was never looked up";
                    return;
                }
                if (s.Recomp.OnlineCodeName != meB.ToString().ToLowerInvariant())
                {
                    fail = $"NEW CODE showed '{s.Recomp.OnlineCodeName}', expected {meB.ToString().ToLowerInvariant()}";
                    return;
                }
                Shoot("online_live_add_code");
                s.Recomp.OnCall(s.Menu, 2645, inPause: false); // OK
                if (!RunUntil(s, () => Roster()?.Friends.Any(c => c.Code == meB.Code) == true, 10))
                {
                    fail = "after both entered the codes, the menus' FRIENDS didn't list B";
                    return;
                }
                Console.WriteLine("  2 ok: both codes -> friends");

                // 3: A asks to be rivals, the menus accept on REQUESTS (OK on the row). From server 0.7.0 a pair is a friend OR
                // a rival (T3, vanilla): A is a friend, so A's ask is refused and a third hunter C (no link yet) asks instead
                bool oneLink = ca.ServerAtLeast(MatchResultReq.SinceServer);
                OnlineClient rivalAsker = ca;
                PlayerCard rivalCard = meA;
                if (oneLink)
                {
                    try
                    {
                        Wait(ca.RequestAsync<OkMsg>(new AddReq { Code = me, Kind = LinkKind.Rival }));
                        fail = "A (a friend) could ask to be a rival too (server 0.7.0+: friend OR rival)";
                        return;
                    }
                    catch (OnlineException ex) when (ex.Code == "already_friends")
                    {
                        Console.WriteLine("  3a ok: ADD RIVAL from a friend refused (already_friends)");
                    }
                    cc = Wait(OnlineClient.ConnectAsync(uri, OnlineIdentity.Create(), nameC, "onlinefriendstest"));
                    rivalAsker = cc;
                    rivalCard = cc.Me!;
                }
                Wait(rivalAsker.RequestAsync<OkMsg>(new AddReq { Code = me, Kind = LinkKind.Rival }));
                s.Menu.GoTo(friendsPage);
                RunUntil(s, () => s.Menu.Page?.Index == friendsPage && s.Menu.PendingPage == -1, 5);
                s.Recomp.OnCall(s.Menu, 2602, inPause: false);
                if (!RunUntil(s, () => Roster()?.Requests.Any(q => q.Incoming && q.Kind == LinkKind.Rival && q.Player.Code == rivalCard.Code) == true, 10))
                {
                    fail = "the rival request never showed on REQUESTS";
                    return;
                }
                Shoot("online_live_friends_requests");
                s.Recomp.OnCall(s.Menu, 2630, inPause: false); // OK on the first (incoming) row
                if (RecompMenus.ShowRivals)
                {
                    if (!RunUntil(s, () => Roster()?.Rivals.Any(c => c.Code == rivalCard.Code) == true, 10))
                    {
                        fail = $"after OK, the menus' RIVALS didn't list {(oneLink ? "C" : "A")}";
                        return;
                    }
                    Console.WriteLine("  3 ok: rival request accepted");
                }
                else
                {
                    // rivals hidden in the menus (release 0.2.0): the request isn't a row, so OK on row 0 accepts nothing;
                    // the server keeps it until the asker takes it back
                    Run(s, 30);
                    if (Roster()?.Rivals.Any(c => c.Code == rivalCard.Code) == true)
                    {
                        fail = "rivals hidden, but OK on REQUESTS accepted a rival request";
                        return;
                    }
                    Wait(rivalAsker.RequestAsync<OkMsg>(new RemoveReq { Code = me, Kind = LinkKind.Rival }));
                    if (!RunUntil(s, () => Roster()?.Requests.Any(q => q.Kind == LinkKind.Rival) == false, 10))
                    {
                        fail = "the taken-back rival request stayed in the roster";
                        return;
                    }
                    Console.WriteLine("  3 ok: rival request hidden on REQUESTS (not accepted), taken back");
                }
                (int Call, string Name)[] tabs = RecompMenus.ShowRivals
                    ? new[] { (2600, "friends"), (2601, "rivals"), (2602, "requests"), (2603, "recent") }
                    : new[] { (2600, "friends"), (2602, "requests"), (2603, "recent") };
                foreach ((int call, string name) in tabs)
                {
                    s.Recomp.OnCall(s.Menu, call, inPause: false);
                    Shoot("online_live_friends_" + name);
                }
                s.Recomp.OnCall(s.Menu, 2604, inPause: false);
                RunUntil(s, () => s.Menu.Page?.Index == addPage && s.Menu.PendingPage == -1, 5);
                s.Recomp.OnCall(s.Menu, 2642, inPause: false);
                Shoot("online_live_add_recent");

                // 4: HUNTER LICENSE: A uploads one, a tap on A's row on FRIENDS opens it; BLOCK B from B's, then UNBLOCK
                Wait(ca.RequestAsync<OkMsg>(new SetLicenseReq { License = RecompMenus.OnlineLicenseJson(SampleLicense()) }));
                s.Menu.GoTo(friendsPage);
                RunUntil(s, () => s.Menu.Page?.Index == friendsPage && s.Menu.PendingPage == -1, 5);
                s.Recomp.OnCall(s.Menu, 2600, inPause: false);
                Run(s, 10);
                int rowA = Roster()!.Friends.OrderBy(c => c.Presence switch { Presence.Lobby => 0, Presence.Match => 1, Presence.Online => 2, _ => 3 })
                    .ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToList().FindIndex(c => c.Code == meA.Code);
                s.Recomp.OnCall(s.Menu, 2610 + rowA, inPause: false); // A's row
                int licensePage = RecompMenus.OnlineLicensePage;
                if (!RunUntil(s, () => s.Menu.Page?.Index == licensePage && s.Recomp.OnlineLicenseCard?.Code == meA.Code
                    && s.Recomp.OnlineLicenseShown?.RankPoints == 512, 10))
                {
                    fail = $"A's row didn't open A's license with A's numbers (page {s.Menu.Page?.Index}, card {s.Recomp.OnlineLicenseCard}, "
                        + $"rank points {s.Recomp.OnlineLicenseShown?.RankPoints.ToString() ?? "none"})";
                    return;
                }
                Shoot("online_live_license");
                s.Recomp.OnCall(s.Menu, 2592, inPause: false); // back
                if (!RunUntil(s, () => s.Menu.Page?.Index == friendsPage && s.Menu.PendingPage == -1, 5))
                {
                    fail = $"back from the license didn't return to FRIENDS AND RIVALS (page {s.Menu.Page?.Index})";
                    return;
                }
                s.Recomp.OnlineOpenLicense(s.Menu, meB);
                RunUntil(s, () => s.Menu.Page?.Index == licensePage && s.Menu.PendingPage == -1 && !s.Recomp.OnlineLicenseLoading, 10);
                s.Recomp.OnCall(s.Menu, 2684, inPause: false); // BLOCK (asks)
                Run(s, 10);
                Yes(s);
                if (!RunUntil(s, () => Roster()?.Blocked.Any(c => c.Code == meB.Code) == true && Roster()?.Friends.All(c => c.Code != meB.Code) == true, 10))
                {
                    fail = "BLOCK on B's license didn't block B (or B is still a friend)";
                    return;
                }
                s.Popup?.Close();
                Shoot("online_live_license_blocked");
                s.Recomp.OnCall(s.Menu, 2684, inPause: false); // UNBLOCK
                if (!RunUntil(s, () => Roster()?.Blocked.All(c => c.Code != meB.Code) == true, 10))
                {
                    fail = "UNBLOCK on B's license didn't unblock B";
                    return;
                }
                s.Popup?.Close();
                // REPORT B's name: a 0.2.0+ server keeps it (yes in the box); an older one gets nothing (the box says it needs an
                // update), and either way the connection stays
                bool reports = cb.ServerAtLeast(ReportReason.SinceServer);
                s.Recomp.OnCall(s.Menu, 2685, inPause: false);
                Run(s, 10);
                if (reports) Yes(s);
                Run(s, 60);
                Shoot("online_live_license_report");
                if (!s.Recomp.OnlineConnected)
                {
                    fail = $"REPORT dropped the menus' connection (server {cb.ServerVersion})";
                    return;
                }
                Console.WriteLine($"  REPORT: server {cb.ServerVersion} {(reports ? "took the report" : "too old: the box says so")}, still connected");
                // the menus' own license went up as they connected
                PlayerCard mine = Wait(cb.RequestAsync<PlayerMsg>(new GetPlayerReq { Code = me })).Player;
                if (mine.License?.Contains("\"RankPoints\"", StringComparison.Ordinal) != true)
                {
                    fail = $"the menus' license isn't on the server ({mine.License ?? "none"})";
                    return;
                }
                Console.WriteLine("  4 ok: a row opens the license (A's numbers), BLOCK / UNBLOCK, the menus' license uploaded");

                // 5: ONLINE SETTINGS' FINDABLE BY NAME off: A can't find the menus by name#tag (a code still finds them); on again
                string meName = OnlineNames.Display(s.Recomp.OnlineMeCard!.Name, s.Recomp.OnlineMeCard.Tag);
                bool Findable() => Wait(ca.RequestAsync<PlayersMsg>(new SearchReq { Query = meName })).Players.Any(c => c.Code == me);
                s.Menu.GoTo(RecompMenus.OnlineSettingsPage);
                RunUntil(s, () => s.Menu.Page?.Index == RecompMenus.OnlineSettingsPage && s.Menu.PendingPage == -1, 5);
                if (!Findable())
                {
                    fail = $"A can't find the menus as {meName} before the switch";
                    return;
                }
                s.Recomp.OnCall(s.Menu, 2700, inPause: false); // FINDABLE BY NAME: off
                Run(s, 60);
                Shoot("online_live_settings_unfindable");
                if (Findable())
                {
                    fail = "FINDABLE BY NAME off, but A still finds the menus by name";
                    return;
                }
                s.Recomp.OnCall(s.Menu, 2700, inPause: false); // on again
                Run(s, 60);
                if (!Findable())
                {
                    fail = "FINDABLE BY NAME on again, but A can't find the menus";
                    return;
                }
                Console.WriteLine($"  5 ok: FINDABLE BY NAME off hides {meName} from search, on shows it again");
                // B was a friend before the block: friends again for the check below (codes both ways)
                Wait(cb.RequestAsync<OkMsg>(new AddReq { Code = me, Kind = LinkKind.Friend }));
                s.Recomp.OnlineOpenLicense(s.Menu, meB);
                RunUntil(s, () => s.Menu.Page?.Index == licensePage && !s.Recomp.OnlineLicenseLoading && Roster()?.Requests.Any(q => q.Incoming && q.Player.Code == meB.Code) == true, 10);
                s.Recomp.OnCall(s.Menu, 2681, inPause: false); // ACCEPT FRIEND
                if (!RunUntil(s, () => Roster()?.Friends.Any(c => c.Code == meB.Code) == true, 10))
                {
                    fail = "ACCEPT FRIEND on B's license didn't make B a friend again";
                    return;
                }
                s.Popup?.Close();

                // both sides agree
                RosterMsg ra = Wait(ca.RequestAsync<RosterMsg>(new RosterReq())), rb = Wait(cb.RequestAsync<RosterMsg>(new RosterReq()));
                if (!ra.Friends.Any(c => c.Code == me)) fail = "A's roster has no menus friend";
                else if (RecompMenus.ShowRivals && !oneLink && !ra.Rivals.Any(c => c.Code == me)) fail = "A's roster has no menus rival";
                else if (oneLink && ra.Rivals.Any(c => c.Code == me)) fail = "A has the menus as a friend AND a rival";
                else if (RecompMenus.ShowRivals && oneLink && !Wait(cc!.RequestAsync<RosterMsg>(new RosterReq())).Rivals.Any(c => c.Code == me)) fail = "C's roster has no menus rival";
                else if (!rb.Friends.Any(c => c.Code == me)) fail = "B's roster has no menus friend";
                else if (Roster()!.Requests.Count != 0) fail = $"{Roster()!.Requests.Count} requests left over";

                // 6: clean up (server 0.2.0+): the menus delete their profile from ONLINE SETTINGS (two yeses), A and B theirs
                if (fail == null && ca.ServerAtLeast(DeleteProfileReq.SinceServer))
                {
                    s.Menu.GoTo(RecompMenus.OnlineSettingsPage);
                    RunUntil(s, () => s.Menu.Page?.Index == RecompMenus.OnlineSettingsPage && s.Menu.PendingPage == -1, 5);
                    s.Recomp.OnCall(s.Menu, 2711, inPause: false); // DELETE ONLINE PROFILE (row 11)
                    Run(s, 10);
                    Yes(s);
                    Yes(s);
                    if (!RunUntil(s, () => s.Menu.Page?.Index == MultiplayerMenu.TypePage, 15))
                        fail = $"DELETE ONLINE PROFILE didn't leave ONLINE PLAY (page {s.Menu.Page?.Index})";
                    else if (Wait(ca.RequestAsync<PlayersMsg>(new SearchReq { Query = me })).Players.Count != 0)
                        fail = "the menus' profile is still on the server after DELETE ONLINE PROFILE";
                    else
                    {
                        Wait(ca.RequestAsync<OkMsg>(new DeleteProfileReq()));
                        Wait(cb.RequestAsync<OkMsg>(new DeleteProfileReq()));
                        if (cc != null) Wait(cc.RequestAsync<OkMsg>(new DeleteProfileReq()));
                        Console.WriteLine($"  6 ok: DELETE ONLINE PROFILE (menus), A and B{(cc != null ? " and C" : "")} deleted too");
                    }
                }
                else if (fail == null) Console.WriteLine($"  6 skipped: server {ca.ServerVersion} can't delete profiles (deploy 0.2.0)");
            }
            catch (Exception ex) when (ex is OnlineException or System.Net.WebSockets.WebSocketException or IOException or InvalidOperationException)
            {
                fail = ex.GetType().Name + ": " + ex.Message;
            }
            finally
            {
                if (ca != null) Wait(ca.DisposeAsync().AsTask().ContinueWith(_ => 0));
                if (cb != null) Wait(cb.DisposeAsync().AsTask().ContinueWith(_ => 0));
                if (cc != null) Wait(cc.DisposeAsync().AsTask().ContinueWith(_ => 0));
                Console.WriteLine(fail == null ? "PASS onlinefriendstest: search + ADD FRIEND, both codes, REQUESTS OK; both rosters agree"
                    : "FAIL onlinefriendstest: " + fail);
            }
        }

        // after the joined match (no match runs here: both sides end it as their match screens would; results as the match
        // screen would leave them, the joiner first, the host second): back on RESULTS, the menus made again as the shell does;
        // the license counts a Wi-Fi win, head to head against the host; PLAY AGAIN -> the same lobby, waiting; the host back
        // in it sees the menus back; the host's START -> the menus play the rematch. Null = all as expected, else what wasn't.
        private static string? AfterOnlineMatch(string dir, int hub, string hostCode, OnlineMatch host, List<FrontendRequest> requests,
            bool serverRecords)
        {
            host.EndMatch();
            OnlineMatch.Current?.EndMatch();
            static MatchResults.Player Row(int seat, string name, int place) => new(seat, Hunter.Kanden, name, false, 0, place, 10 - place * 5,
                0, 10 - place * 5, place * 5, 0, 0, 0, 0, 0, 0, 0, 0, 50, 0);
            MatchResults.Last = new MatchResults { Mode = GameMode.Battle, LocalSeat = 1, Rows = new[] { Row(1, "HUNTER", 0), Row(0, "PCHOST", 1) } };
            int page = FrontendSession.ReturnPage();
            if (page != MultiplayerMenu.ResultsPage) return $"after the match the menus wouldn't open on RESULTS (ReturnPage {page})";
            FrontendSession menus = Session(dir);
            menus.Request = r => requests.Add(r);
            menus.Start(page);
            if (!RunUntil(menus, () => menus.Menu.Page?.Index == MultiplayerMenu.ResultsPage && menus.Menu.PendingPage == -1, 10))
                return $"the menus didn't open on RESULTS (page {menus.Menu.Page?.Index})";
            Run(menus, 60);
            Shot(menus, "online_live_results");
            // the host's name on RESULTS (its second row) opens the host's license; back returns to RESULTS
            menus.Recomp.OnCall(menus.Menu, 2694 + 1, inPause: false);
            if (!RunUntil(menus, () => menus.Menu.Page?.Index == RecompMenus.OnlineLicensePage && menus.Recomp.OnlineLicenseCard?.Code == hostCode
                && !menus.Recomp.OnlineLicenseLoading, 10))
                return $"the host's name on RESULTS didn't open the host's license (page {menus.Menu.Page?.Index})";
            Run(menus, 30);
            Shot(menus, "online_live_results_license");
            menus.Recomp.OnCall(menus.Menu, 2592, inPause: false); // back
            if (!RunUntil(menus, () => menus.Menu.Page?.Index == MultiplayerMenu.ResultsPage && menus.Menu.PendingPage == -1, 10))
                return $"back from the license didn't return to RESULTS (page {menus.Menu.Page?.Index})";
            HunterLicense l = menus.Multiplayer.License;
            if (l.OnlineMatches != 1 || l.WifiWins != 1) return $"RESULTS didn't count a Wi-Fi win ({l.WifiWins} of {l.OnlineMatches} wi-fi, {l.LocalMatches} wireless)";
            // (server 0.7.0+, T3: head to head is the server's, from the host's report; this PC host's faked RESULTS sends none, so
            // the license shows no record. The counted case: -onlinerecordtest)
            var h = menus.Recomp.OnlineHeadToHead(hostCode);
            if (serverRecords ? h != null : h is not { Rounds: 1, Victories: 1, Last: RecompMenus.HeadToHead.Victory })
                return $"head to head against the host isn't {(serverRecords ? "none (the server's, nothing reported)" : "1 round, 1 victory")} ({h})";
            Console.WriteLine($"  RESULTS: wi-fi {l.WifiWins} of {l.OnlineMatches}; head to head vs the host {h}");
            menus.Multiplayer.OnCall(menus.Menu, new MenuAction(), 196, -1); // PLAY AGAIN
            (_, int lobby) = RecompMenus.OnlineCreatePages;
            if (!RunUntil(menus, () => menus.Menu.Page?.Index == lobby && menus.Recomp.OnlineInLobby && menus.Menu.PendingPage == -1, 10))
                return $"RESULTS' PLAY AGAIN didn't go back to the game's lobby (page {menus.Menu.Page?.Index})";
            host.BackToLobby();
            if (!RunUntil(menus, () =>
            {
                host.Net.LobbyTick(true);
                return host.Net.NotBack().Count == 0;
            }, 10))
                return "the host never saw the menus back in the lobby (their LOOK)";
            Run(menus, 60);
            Shot(menus, "online_live_rematch_joiner");
            int starts = requests.Count(r => r.Kind == FrontendRequestKind.StartMatch);
            host.Start();
            if (!RunUntil(menus, () => requests.Count(r => r.Kind == FrontendRequestKind.StartMatch) > starts, 15))
                return "the menus didn't play the rematch after the host's START";
            Console.WriteLine("  rematch: PLAY AGAIN -> the same lobby -> the host's START -> the menus play it");
            MatchResults.Last = null;
            _ = hub;
            return null;
        }

        // the test's hunters off the server (0.2.0+: delete_profile; an older one keeps them): the PC ones by their keys, the
        // menus' by the key in its profile folder (its license name: logging in with another would rename it)
        private static void DeleteTestProfiles(Uri uri, string? menusDir, params (OnlineIdentity Id, string Name)[] pcs)
        {
            var all = pcs.ToList();
            if (menusDir != null && OnlineIdentity.Load(menusDir) is OnlineIdentity menus) all.Add((menus, OnlineNames.Default));
            int deleted = 0;
            foreach ((OnlineIdentity id, string name) in all)
            {
                try
                {
                    OnlineClient c = Wait(OnlineClient.ConnectAsync(uri, id, name, "onlinetests"));
                    if (c.ServerAtLeast(DeleteProfileReq.SinceServer))
                    {
                        Wait(c.RequestAsync<OkMsg>(new DeleteProfileReq()));
                        deleted++;
                    }
                    Wait(c.DisposeAsync().AsTask().ContinueWith(_ => 0));
                }
                catch (Exception ex) when (ex is OnlineException or System.Net.WebSockets.WebSocketException or IOException
                    or InvalidOperationException)
                {
                    Console.WriteLine($"  cleanup: {name}: {ex.Message}");
                }
            }
            Console.WriteLine(deleted > 0 ? $"  cleanup: {deleted} test hunter(s) deleted from the server" : "  cleanup: the server keeps test hunters (before 0.2.0)");
        }

        // the menus keep ticking while a PC client waits for a push
        private static bool RunUntilEvent(FrontendSession s, OnlineClient c, Func<OnlineMsg, bool> match, int seconds)
        {
            bool seen = false;
            return RunUntil(s, () =>
            {
                while (!seen && c.Events.TryDequeue(out OnlineMsg? ev)) seen = match(ev);
                return seen;
            }, seconds);
        }

        public static void Test(string[] args)
        {
            if (!ToolPaths.UseDesktopExtraction()) return;
            Dictionary<string, string> a = Args(args);
            var uri = new Uri(a.GetValueOrDefault("server", RecompMenus.OnlineServer));
            Console.WriteLine($"onlinemenutest: {uri}, version {OnlineMatch.VersionKey(RomKey)}");
            // the host: its own key, a 2-player public game
            OnlineIdentity hostId = OnlineIdentity.Create();
            OnlineClient hostClient = Wait(OnlineClient.ConnectAsync(uri, hostId, "PCHOST", "onlinemenutest"));
            MatchSettings template = MatchSettings.Quick(GameMode.Battle, "Combat Hall", Hunter.Kanden, 0);
            template.Players[0].Nickname = "PCHOST";
            template.TimeLimitSeconds = 120;
            OnlineMatch host = Wait(OnlineMatch.HostAsync(hostClient, uri, template, RomKey, 2));
            Console.WriteLine($"  host {hostClient.Me} opened game {host.Game.Id}");
            string? fail = null;
            var requests = new List<FrontendRequest>();
            string dir = Path.Combine(Path.GetTempPath(), "online-menu-test-" + Guid.NewGuid().ToString("N")[..6]);
            FrontendSession s = Session(dir);
            s.Request = r => requests.Add(r);
            try
            {
                (int hub, int browse, int detail) = RecompMenus.OnlinePages;
                s.Recomp.OpenOnline(s.Menu);
                if (!RunUntil(s, () => s.Recomp.OnlineConnected && s.Recomp.OnlineGames.Any(g => g.Id == host.Game.Id), 20))
                {
                    fail = "the menus didn't connect or didn't list the host's game";
                    return;
                }
                Run(s, 30);
                Shot(s, "online_live_hub");
                s.Menu.GoTo(browse);
                if (!RunUntil(s, () => s.Menu.Page?.Index == browse && s.Recomp.OnlineShownIndex(host.Game.Id) >= 0, 10))
                {
                    fail = "BROWSE didn't show the host's game";
                    return;
                }
                Run(s, 60);
                Shot(s, "online_live_browse");
                int at = s.Recomp.OnlineShownIndex(host.Game.Id);
                for (int i = 0; i < at % 3; i++)
                {
                    s.Navigate(0, -1);
                    Run(s, 6);
                }
                for (int i = 0; i < at / 3; i++)
                {
                    s.Press(MenuKeys.R);
                    Run(s, 6);
                }
                s.Press(MenuKeys.A); // the row -> its details
                if (!RunUntil(s, () => s.Menu.Page?.Index == detail, 5))
                {
                    fail = "the row didn't open the game's details";
                    return;
                }
                Run(s, 60);
                Shot(s, "online_live_detail");
                s.Press(MenuKeys.A); // JOIN (focused as the page comes in): SELECT HUNTER first
                if (!RunUntil(s, () => s.Menu.Page?.Index == MultiplayerMenu.HunterPage && s.Menu.PendingPage == -1, 5))
                {
                    fail = $"JOIN didn't ask for a hunter first (page {s.Menu.Page?.Index})";
                    return;
                }
                Run(s, 30);
                Shot(s, "online_live_join_hunter");
                s.Multiplayer.OnCall(s.Menu, new MenuAction(), 133, -1); // Kanden (a new license has Samus, Kanden, Spire)
                if (!RunUntil(s, () => host.Net.Joined >= 1, 20))
                {
                    fail = $"the host never saw the joiner ({host.Net.Status})";
                    return;
                }
                Console.WriteLine($"  host: {host.Net.Joined} joined ({host.Net.Status}); the menus wait for START");
                Run(s, 60);
                Shot(s, "online_live_waiting");
                host.Start();
                if (!RunUntil(s, () => requests.Any(r => r.Kind == FrontendRequestKind.StartMatch), 15))
                {
                    fail = "the menus didn't start the match after the host's START";
                    return;
                }
                FrontendRequest start = requests.First(r => r.Kind == FrontendRequestKind.StartMatch);
                Dictionary<string, string> launch = MatchSetupText.LaunchArgs(start.Arg!);
                OnlineMatch? joined = OnlineMatch.Current;
                Console.WriteLine($"  StartMatch: {start.Arg}");
                Console.WriteLine($"  launch: {string.Join(" ", launch.Select(p => $"--es {p.Key} {p.Value}"))}");
                if (launch.GetValueOrDefault("net") != "online" || launch.GetValueOrDefault("menu") != "1")
                    fail = "the launch isn't an online match from the menus";
                else if (joined?.Net.Settings is not MatchSettings js || js.LocalSeat != 1 || js.Players.Count != 2)
                    fail = $"OnlineMatch.Current isn't the joined 2-player match as seat 1 ({joined?.Net.Settings?.LocalSeat})";
                else if (js.Players[1].Hunter != Hunter.Kanden)
                    fail = $"the joiner's seat isn't the hunter picked on SELECT HUNTER ({js.Players[1].Hunter}, picked Kanden)";
                if (fail == null) fail = AfterOnlineMatch(dir, hub, hostClient.Me!.Code, host, requests,
                    hostClient.ServerAtLeast(MatchResultReq.SinceServer));
            }
            finally
            {
                OnlineMatch.Current?.Dispose();
                host.Dispose();
                Thread.Sleep(500);
                DeleteTestProfiles(uri, dir, (hostId, "PCHOST"));
                Console.WriteLine(fail == null ? "PASS onlinemenutest: BROWSE -> details -> JOIN -> SELECT HUNTER -> seat -> host START -> the menus play it (net=online) -> RESULTS -> PLAY AGAIN -> rematch"
                    : "FAIL onlinemenutest: " + fail);
            }
        }

        public static void Host(string[] args)
        {
            if (!ToolPaths.UseDesktopExtraction()) return;
            Dictionary<string, string> a = Args(args);
            var uri = new Uri(a.GetValueOrDefault("server", RecompMenus.OnlineServer));
            int joiners = int.Parse(a.GetValueOrDefault("joiners", "1"));
            int bots = int.Parse(a.GetValueOrDefault("bots", "1"));
            float time = float.Parse(a.GetValueOrDefault("time", "120"), System.Globalization.CultureInfo.InvariantCulture);
            int wait = int.Parse(a.GetValueOrDefault("wait", "900"));
            GameMode mode = Enum.Parse<GameMode>(a.GetValueOrDefault("mode", "Battle").Replace(" ", ""), ignoreCase: true);
            RoomMetadata arena = MatchArenas.Find(a.GetValueOrDefault("arena", "Combat Hall")) ?? throw new ArgumentException("no such arena");
            MatchSettings template = MatchSettings.Quick(mode, arena.Name, Hunter.Kanden, bots);
            template.Players[0].Nickname = "PC host";
            template.TimeLimitSeconds = time;
            OnlineClient client = Wait(OnlineClient.ConnectAsync(uri, OnlineIdentity.LoadOrCreate(Path.Combine(OutDir(), "pchost")), "PCHOST",
                "onlinehost"));
            OnlineMatch match = Wait(OnlineMatch.HostAsync(client, uri, template, RomKey, 1 + bots + joiners));
            Console.WriteLine($"onlinehost: {client.Me} hosts game {match.Game.Id} ({mode} in {arena.Name}, version "
                + $"{OnlineMatch.VersionKey(RomKey)}); waiting up to {wait} s for {joiners} joiner(s)");
            try
            {
                var clock = Stopwatch.StartNew();
                int seen = 0;
                while (match.Net.Joined < joiners && clock.Elapsed.TotalSeconds < wait)
                {
                    if (match.Net.Joined != seen) Console.WriteLine($"  {seen = match.Net.Joined} joined ({match.Net.Status})");
                    Thread.Sleep(50);
                }
                if (match.Net.Joined < joiners)
                {
                    Console.WriteLine("onlinehost: nobody joined in time");
                    return;
                }
                Console.WriteLine($"  {match.Net.Joined} joined; START in 3 s");
                Thread.Sleep(3000);
                match.Start();
                MatchSettings settings = match.Net.Settings!;
                Console.WriteLine("  seats " + string.Join(", ", settings.Players.Select((p, i) => $"{i}:{p.Hunter}{(p.Bot ? " bot" : p.Remote ? " " + p.Nickname : " local")}")));
                CampaignHost host = CampaignHost.StartMatch(settings);
                var driver = new MatchNetDriver(match.Net, host);
                var real = Stopwatch.StartNew();
                long frames = 0;
                while (!host.Ended && real.Elapsed.TotalSeconds < time + 20)
                {
                    long due = (long)(real.Elapsed.TotalSeconds * 60);
                    while (frames < due && !host.Ended)
                    {
                        driver.Step(new CampaignInput { SelectWeapon = BeamType.None });
                        frames++;
                    }
                    Thread.Sleep(1);
                }
                Console.WriteLine($"onlinehost: match over after {frames} frames ({driver.StatusLine()})");
                Console.WriteLine(MatchStatus.Scoreboard());
                host.Dispose();
            }
            finally
            {
                match.Dispose();
                Thread.Sleep(500);
            }
        }
    }
}
