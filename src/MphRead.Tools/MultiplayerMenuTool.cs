using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using MphRead;
using MphRecomp.Campaign;
using MphRecomp.Frontend;
using MphRecomp.Multiplayer;
using SixLabors.ImageSharp;

// -mpmenu "<tick>:<input>,..." [end]: the front end from the main menu (FE_START, default 18) driven like -feflow (A, B,
// left/right/up/down, ds@x/y = a DS touch pixel, y down), then the current page's visible items: state, menu-space
// bounds, text. For the vanilla multiplayer menu (docs/MULTIPLAYER_MENU.md): checking our pages against the BizHawk runs.
// MP_ITEMS=<a>,<b>,...: only those items.
// -mpmenu check: every mode (and its team play) set up through the menu to START GAME, the setup line it hands the shell
// read back and played headless for 10 s (CampaignHost.StartMatch): the menu only ever asks for matches that run.
// -mpmenu results [mode] [teams] [quit] [bots=N] [arena=<name>]: a bot match (N bots, default 3) set up through the menu with its goal row at
// the lowest value and TIME LIMIT 3, played headless to its real end (GAME OVER, the fade, the exit), its results captured as the match screen does
// (MatchResults), then the menus reopened where the shell reopens them (FrontendSession.ReturnPage = RESULTS): the page's
// items once settled, R through every column, then PLAY AGAIN (or QUIT and the question's check). MP_SHOTS=1 saves
// results_<name>.png (extract_out/frontend) at MP_SIZE (default 1280x720).
// -mpmenu lan [hunter]: a LAN game set up in the menus of two processes on this PC (this one hosts, a second copy joins
// as <hunter>, default Kanden), played over the real LanSession to its end, then both RESULTS (LanHost below).
// -mpmenu lan [hunter] addr: the same, the joiner typing this PC's address on the "enter address" keypad (Windows'
// join by address) instead of picking the game from the list.
// -mpmenu lan [hunter] [addr] again: then RESULTS' PLAY AGAIN on both (the host 5 s after the joiner), the joiner seated
// again in the same host's lobby, a second match to both RESULTS (owner report R2).
// -mpmenu back: every multiplayer page's back arrow by touch, and B (owner report R1). -mpmenu lobby: bots added, confirmed,
// stepped and removed; empty seats show nothing (owner report R9). MP_TRIS=<item>: that item's triangles in the page dump.
namespace MphRead
{
    public static class MultiplayerMenuTool
    {
        public static void Run(string[] args)
        {
            if (!ToolPaths.UseDesktopExtraction()) return;
            // FE_PROPOSE=1: the compositions waiting for the owner's pick (MenuLayout.Proposals), as -feflow
            MenuLayout.Proposals = Environment.GetEnvironmentVariable("FE_PROPOSE") == "1";
            if (args.Length >= 2 && args[1] == "check")
            {
                Check();
                return;
            }
            if (args.Length >= 2 && args[1] == "name")
            {
                Name(args.Length >= 3 ? args[2] : "Kanden~7");
                return;
            }
            if (args.Length >= 2 && args[1] == "unlocks")
            {
                Unlocks();
                return;
            }
            if (args.Length >= 2 && args[1] == "doorshot")
            {
                DoorShot();
                return;
            }
            if (args.Length >= 2 && args[1] == "icons")
            {
                Icons(args.Skip(2).ToArray());
                return;
            }
            if (args.Length >= 2 && args[1] == "license")
            {
                License();
                return;
            }
            if (args.Length >= 2 && args[1] == "back")
            {
                Back();
                return;
            }
            if (args.Length >= 2 && args[1] == "lobby")
            {
                LobbyCheck();
                return;
            }
            if (args.Length >= 2 && args[1] == "thumbs")
            {
                Thumbs(args.Skip(2).FirstOrDefault(a => a != "compare"), args.Contains("compare"));
                return;
            }
            if (args.Length >= 2 && args[1] == "lan")
            {
                LanHost(args);
                return;
            }
            if (args.Length >= 2 && args[1] == "lanjoin")
            {
                LanJoin(args);
                return;
            }
            if (args.Length >= 2 && args[1] == "results")
            {
                string? bots = args.FirstOrDefault(a => a.StartsWith("bots="));
                string? arena = args.FirstOrDefault(a => a.StartsWith("arena="));
                Results(args.Length >= 3 ? args[2] : "battle", args.Contains("teams"), args.Contains("quit"),
                    bots == null ? 3 : Int32.Parse(bots[5..]), arena?[6..].Replace('_', ' '));
                return;
            }
            string script = args.Length >= 2 ? args[1] : "";
            var steps = script.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(t => t.Split(':'))
                .Select(p => (Frame: Int32.Parse(p[0]), Input: p[1])).ToList();
            int end = args.Length >= 3 ? Int32.Parse(args[2]) : (steps.Count == 0 ? 60 : steps.Max(t => t.Frame) + 60);
            FrontendSession session = FrontendSession.Load(Paths.FileSystem, savesDir: Environment.GetEnvironmentVariable("FE_SAVES")); // FE_SAVES: a campaign saves folder (file select)
            session.Log = m => Console.WriteLine($"{session.Frame}: {m}");
            session.Request = r => Console.WriteLine($"{session.Frame}: request {r}");
            if (Environment.GetEnvironmentVariable("MP_NUMPAD") == "1") MphRecomp.Frontend.MultiplayerMenu.NumpadPopup = true; // the Android pop-up
            // FE_HD: an HD models folder (the file select's hunter box offers the Trooper when it has one of his skins)
            if (Environment.GetEnvironmentVariable("FE_HD") is string feHd) MphRecomp.Frontend.MultiplayerMenu.CharacterHdRoot = feHd;
            session.Start(Int32.TryParse(Environment.GetEnvironmentVariable("FE_START"), out int start) ? start : 18);
            for (int f = 0; f <= end; f++)
            {
                foreach (var st in steps.Where(t => t.Frame == f))
                {
                    switch (st.Input.ToLowerInvariant())
                    {
                    case "a": session.Press(MenuKeys.A); break;
                    case "b": session.Press(MenuKeys.B); break;
                    case "y": session.Press(MenuKeys.Y); break;
                    case "up": session.Navigate(0, 1); break;
                    case "down": session.Navigate(0, -1); break;
                    case "left": session.Navigate(-1, 0); break;
                    case "right": session.Navigate(1, 0); break;
                    case "bksp": session.TypeKey(true); break;
                    case "enter": session.TypeKey(false); break;
                    default:
                        if (st.Input.StartsWith("t=")) foreach (char c in st.Input[2..]) session.TypeChar(c); // PC keyboard text
                        else if (st.Input.StartsWith("ds@"))
                        {
                            string[] xy = st.Input[3..].Split('/');
                            session.TouchDs(Single.Parse(xy[0], CultureInfo.InvariantCulture), Single.Parse(xy[1], CultureInfo.InvariantCulture));
                        }
                        break;
                    }
                }
                session.Build(640, 360);
                session.Tick();
            }
            if (Environment.GetEnvironmentVariable("MP_SHOT") is string shot && shot.Length > 0)
            {
                session.Build(1280, 720);
                using var img = FrontendTool.RasterCanvas(session.DrawList, 1280, 720);
                img.SaveAsPng(shot);
                Console.WriteLine($"shot {shot}");
            }
            DumpPage(session);
        }

        // -mpmenu thumbs [arena] [compare]: the imported (Echoes) arenas' pictures for the ARENA box (ArenaThumbnail, as the
        // import makes them: inside the arena), from the HD looks registered on this PC, focused on each arena's spawns and pickups; written
        // where the menu reads them (thumb.png beside the HD look) and copied to extract_out/frontend/thumb_<key>.png.
        // compare: both camera styles side by side instead (thumb_<key>_any.png / _inside.png; thumb.png left alone)
        private static void Thumbs(string? only, bool compare = false)
        {
            MphRecomp.Arenas.EchoesArena.EnsureRegistered();
            foreach (MphRecomp.Arenas.EchoesArena.Def d in MphRecomp.Arenas.EchoesArena.All)
            {
                if (only != null && MphRecomp.Arenas.EchoesArena.Find(only) != d) continue;
                RoomMetadata? meta = Metadata.GetRoomByName(d.Room).Item1;
                string? dir = MphRecomp.Arenas.EchoesArena.HdDir(d.Room);
                if (meta?.EntityPath == null || dir == null)
                {
                    Console.WriteLine($"{d.InGameName}: not imported here");
                    continue;
                }
                // the import's focus points: its spawns and pickups (the jump pads come after them in the file)
                var focus = Read.GetEntities(meta.EntityPath, 0, firstHunt: false)
                    .Where(e => e.Type is EntityType.PlayerSpawn or EntityType.ItemSpawn)
                    .Select(e => new System.Numerics.Vector3(e.Position.X, e.Position.Y, e.Position.Z)).ToList();
                if (compare)
                {
                    foreach (bool inside in new[] { false, true })
                    {
                        string path = System.IO.Path.Combine(FrontendTool.OutDir(), $"thumb_{d.Key}_{(inside ? "inside" : "any")}.png");
                        MphRecomp.Arenas.ArenaThumbnail.Write(dir, focus, path, m => Console.WriteLine($"{d.InGameName} {(inside ? "inside" : "any")}: {m}"), inside);
                    }
                    continue;
                }
                var r = MphRecomp.Arenas.ArenaThumbnail.Write(dir, focus, log: m => Console.WriteLine($"{d.InGameName}: {m}"), inside: true);
                if (r == null)
                {
                    Console.WriteLine($"{d.InGameName}: FAIL no HD look in {dir}");
                    continue;
                }
                string copy = System.IO.Path.Combine(FrontendTool.OutDir(), $"thumb_{d.Key}.png");
                System.IO.File.Copy(System.IO.Path.Combine(dir, MphRecomp.Arenas.ArenaThumbnail.FileName), copy, overwrite: true);
                Console.WriteLine($"{d.InGameName}: {copy}");
            }
        }

        // -mpmenu unlocks: M6's unlocks on the PC (decision 11) with a fresh profile and campaign folder in a scratch folder:
        // file A has defeated Trace and visited Alinos Gateway, so MULTIPLAYER opens Trace and the Alinos Gateway arena;
        // Battle's ARENA arrows step only the open arenas (a new cart's + that one + imported + RANDOM); SELECT HUNTER shows
        // the dark icons of the locked ones and a tap on one doesn't show it; the lobby's bots are open hunters; vanilla's
        // after-match rules (HunterLicense.AfterMatch); RECOMP SETTINGS > UNLOCKS: NO keeps vanilla, YES opens everything
        // (every Battle arena in the arrows), VANILLA again at once; the good ending's results page opens Oubliette.
        // MP_SHOTS=1: unlocks_hunters.png (extract_out/frontend)
        // a bot match counted on a license: the local player's place and each bot's (hunter, place), in seat order
        private static string Counted(HunterLicense t, bool wifi, int place, params (Hunter Hunter, int Place)[] others) =>
            t.AfterMatch(new HunterLicense.MatchRecord(wifi, GameMode.Battle, 95, 60, Hunter.Samus, place, false, 0,
                others.Select((o, k) => new HunterLicense.Opponent(o.Hunter, o.Place, true, k + 1, -1)).ToList()), null);

        private static void Unlocks()
        {
            string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"mpmenu_unlocks_{Environment.ProcessId}");
            if (System.IO.Directory.Exists(dir)) System.IO.Directory.Delete(dir, recursive: true);
            string saves = System.IO.Path.Combine(dir, "saves"), settingsPath = System.IO.Path.Combine(dir, "recomp_settings.json");
            var story = new MphRead.StorySave { DefeatedHunters = 1 << (int)Hunter.Trace };
            story.SetVisitedRoom(27); // Alinos Gateway
            new CampaignSaves(saves).Save(1, new CampaignSaveFile { Story = story });
            FrontendSession s = FrontendSession.Load(Paths.FileSystem, settingsPath: settingsPath, savesDir: saves);
            s.Log = m => { if (m.StartsWith("multiplayer")) Console.WriteLine($"{s.Frame}: {m}"); };
            string? setup = null;
            s.Request = r => { if (r.Kind == FrontendRequestKind.StartMatch) setup = r.Arg; };
            int fails = 0;
            void Check(bool ok, string what)
            {
                Console.WriteLine($"{(ok ? "PASS" : "FAIL")} {what}");
                if (!ok) fails++;
            }
            void Run(int frames)
            {
                for (int i = 0; i < frames; i++)
                {
                    s.Build(640, 360);
                    s.Tick();
                }
            }
            string Count() => s.Menu.Page!.Items.Where(it => it.IsText).Select(it => s.Menu.Strings[it.States[0].Text!.StringId])
                .FirstOrDefault(t => System.Text.RegularExpressions.Regex.IsMatch(t, @"^\d+/\d+$")) ?? "?";
            string ArenaName() => s.Menu.Page!.Items.Select((it, i) => (it, i))
                .Where(p => p.it.IsText && p.it.X > 128 && p.it.Y > 128 && p.it.Y < 150 && s.Menu.ItemState(p.i) != MenuState.Hidden)
                .Select(p => s.Menu.Strings[p.it.States[0].Text!.StringId]).FirstOrDefault(t => t.Length > 0) ?? "";
            bool Shown(string widget) => Enumerable.Range(0, s.Menu.Page!.Items.Count).Any(i =>
                s.Menu.ItemModelPath(i).EndsWith("\\" + widget + "_Model.bin", StringComparison.OrdinalIgnoreCase) && s.Menu.ItemState(i) != MenuState.Hidden);
            // MULTIPLAYER -> LOCAL PLAY -> CREATE GAME -> Battle, then the ARENA arrow round the list: the names it shows
            List<string> BattleArenas(bool notice)
            {
                s.Start(18);
                Run(30);
                s.TouchDs(172, 72);
                Run(60);
                if (notice)
                {
                    s.Press(MenuKeys.A);
                    Run(30);
                }
                var battle = ModeTaps.First(t => t.Name == "battle");
                foreach ((int x, int y) in new[] { LocalDoor, (48, 72), (battle.X, battle.Y) })
                {
                    s.TouchDs(x, y);
                    Run(40);
                }
                var names = new List<string>();
                string first = Count();
                for (int n = 0; n < 60; n++)
                {
                    names.Add(ArenaName());
                    s.TouchDs(235, 51);
                    Run(8);
                    if (Count() == first) break;
                }
                Console.WriteLine($"battle arenas {first}: {String.Join(", ", names)}");
                return names;
            }
            int imported = MatchArenas.All.Count(r => r.Id > MatchArenas.LastId && MatchArenas.Supports(r, GameMode.Battle));
            int vanillaBattle = 27 - 1; // (Transfer Lock's BT version has no Battle)
            List<string> open = BattleArenas(notice: true);
            HunterLicense lic = s.Multiplayer.License;
            Check(lic.HunterOpen(Hunter.Trace) && lic.HunterOpen(Hunter.Kanden) && lic.HunterOpen(Hunter.Spire)
                && !lic.HunterOpen(Hunter.Noxus) && !lic.HunterOpen(Hunter.Sylux) && !lic.HunterOpen(Hunter.Weavel),
                $"hunters open: a new cart's + file A's Trace (bits {lic.Hunters:X2})");
            Check(lic.ArenaOpen(114) && !lic.ArenaOpen(115), $"file A's Alinos Gateway opened its arena (bits {lic.Arenas:X7})");
            Check(open.Count == 9 + imported + 1, $"battle arenas {open.Count} = 8 of a new cart + Alinos Gateway + {imported} imported + random");
            Check(!open.Any(n => n.Contains("sanctorus", StringComparison.OrdinalIgnoreCase)), "a locked arena (sanctorus) isn't in the arrows");
            // SELECT HUNTER
            s.TouchDs(212, 173);
            Run(60);
            Check(s.Menu.Page?.Index == MultiplayerMenu.HunterPage, $"SELECT HUNTER (page {s.Menu.Page?.Index})");
            string dark = String.Join(" ", new[] { "kanden", "spire", "trace", "noxus", "sylux", "weavel" }.Where(n => Shown(n + "off")));
            string lit = String.Join(" ", new[] { "kanden", "spire", "trace", "noxus", "sylux", "weavel" }.Where(n => Shown(n)));
            Check(dark == "noxus sylux weavel" && lit == "kanden spire trace", $"dark icons: {dark}; lit: {lit}");
            if (Environment.GetEnvironmentVariable("MP_SHOTS") == "1")
            {
                s.Build(1280, 720);
                using var img = FrontendTool.RasterCanvas(s.DrawList, 1280, 720);
                string path = System.IO.Path.Combine(FrontendTool.OutDir(), "unlocks_hunters.png");
                img.SaveAsPng(path);
                Console.WriteLine($"shot {path}");
            }
            s.TouchDs(MultiplayerMenu.HunterIconTap(4).X, MultiplayerMenu.HunterIconTap(4).Y); // Noxus's icon (locked)
            Run(20);
            Check(!Shown("big_noxus") && s.Menu.Page?.Index == MultiplayerMenu.HunterPage, "a tap on locked Noxus doesn't show or pick him");
            s.TouchDs(MultiplayerMenu.HunterIconTap(3).X, MultiplayerMenu.HunterIconTap(3).Y); // Trace's (opened by file A)
            Run(20);
            Check(Shown("big_trace"), "a tap on Trace shows him");
            // Samus, three bots, START GAME: every bot an open hunter
            s.TouchDs(26, 134);
            Run(20);
            s.TouchDs(70, 77);
            Run(60);
            for (int i = 0; i < 3; i++)
            {
                s.TouchDs(223, 170);
                Run(20);
            }
            foreach ((int x, int y, int wait) in new[] { (98, 139, 10), (154, 63, 10), (212, 139, 40), (195, 41, 140) })
            {
                s.TouchDs(x, y);
                Run(wait);
            }
            MatchSettings? settings = setup == null ? null : MatchSetupText.Read(setup);
            string hunters = settings == null ? "no START GAME" : String.Join(" ", settings.Players.Select(p => p.Hunter));
            Check(settings != null && settings.Players.Count == 4 && settings.Players.All(p => lic.HunterOpen(p.Hunter)),
                $"the lobby's bots are open hunters: {hunters}");
            // vanilla's after-match rules, on a new cart's license
            var t = new HunterLicense();
            string o1 = Counted(t, false, 1, (Hunter.Noxus, 2));
            Check(t.HunterOpen(Hunter.Noxus) && !t.ArenaOpen(104), $"match 1 (beat Noxus, 2 players): opened {o1}");
            string o2 = Counted(t, false, 2, (Hunter.Sylux, 1), (Hunter.Weavel, 3), (Hunter.Trace, 4));
            Check(!t.HunterOpen(Hunter.Sylux) && t.HunterOpen(Hunter.Weavel) && !t.HunterOpen(Hunter.Trace) && t.ArenaOpen(104)
                && t.ArenaOpen(99) && !t.ArenaOpen(119), $"match 2 (4 players, Weavel the first below): opened {o2}");
            string o3 = Counted(t, true, 2, (Hunter.Trace, 1));
            Check(t.ArenaOpen(119) && !t.HunterOpen(Hunter.Trace) && !t.ArenaOpen(98), $"match 3 (online, lost to Trace): opened {o3}");
            for (int i = 0; i < 21; i++) Counted(t, false, 1);
            Check(t.LocalMatches == 23 && t.OnlineMatches == 1 && new[] { 104, 98, 103, 109, 107, 94, 101, 112, 96, 106, 108 }.All(t.ArenaOpen),
                $"24 matches: every match arena open (local {t.LocalMatches}, online {t.OnlineMatches}, arenas {t.Arenas:X7})");
            // RECOMP SETTINGS > UNLOCKS: the confirmation (NO, then YES), everything open, then VANILLA again
            var current = s.Recomp.Settings;
            var ask = typeof(RecompMenus).GetMethod("AskUnlockEverything", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
            ask.Invoke(s.Recomp, null);
            Run(10);
            Check(s.Popup?.IsOpen == true && !current.UnlockEverything, "UNLOCKS -> ALL asks first");
            s.Press(MenuKeys.B);
            Run(40);
            Check(s.Popup?.IsOpen != true && !current.UnlockEverything, "NO keeps VANILLA");
            ask.Invoke(s.Recomp, null);
            Run(10);
            s.Press(MenuKeys.A);
            Run(5);
            s.Press(MenuKeys.A);
            Run(40);
            bool saved = System.IO.File.Exists(settingsPath) && System.IO.File.ReadAllText(settingsPath).Contains("\"UnlockEverything\": true", StringComparison.Ordinal);
            Check(current.UnlockEverything && saved, $"YES: ALL, saved {saved}");
            List<string> all = BattleArenas(notice: false);
            Check(all.Count == vanillaBattle + imported + 1, $"ALL: battle arenas {all.Count} = {vanillaBattle} vanilla + {imported} imported + random");
            ask.Invoke(s.Recomp, null);
            Run(10);
            Check(s.Popup?.IsOpen != true && !current.UnlockEverything, "ALL -> VANILLA at once, no question");
            // the good ending's results page
            Check(!lic.ArenaOpen(118), "oubliette locked before the good ending");
            s.Start(GameEnding.GoodPage);
            Run(10);
            Check(lic.ArenaOpen(118) && HunterLicense.Load(dir).ArenaOpen(118), "the good ending's results page opened oubliette (saved)");
            Console.WriteLine(fails == 0 ? "PASS unlocks" : $"FAIL unlocks: {fails} check(s)");
            try { System.IO.Directory.Delete(dir, recursive: true); } catch (System.IO.IOException) { }
        }

        // -mpmenu name [text]: M6 on the PC with a fresh profile (a scratch folder as the settings folder): MULTIPLAYER shows
        // the nickname notice once, HUNTER LICENSE opens the name keyboard (page 62), the old name is deleted and <text>
        // (default "Kanden~7") typed key by key (SHIFT for capitals and shifted symbols), an empty name tried (vanilla's
        // "invalid name."), then OK keeps it (hunter_license.json); MULTIPLAYER again shows no notice. MP_SHOTS=1:
        // name_notice.png, name_keyboard.png, name_shift.png, name_typed.png (extract_out/frontend)
        private static void Name(string text)
        {
            string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"mpmenu_license_{Environment.ProcessId}");
            if (System.IO.Directory.Exists(dir)) System.IO.Directory.Delete(dir, recursive: true);
            FrontendSession s = FrontendSession.Load(Paths.FileSystem);
            s.Multiplayer.ProfileDir = dir;
            s.Log = m => { if (m.StartsWith("multiplayer") || m.StartsWith("work")) Console.WriteLine($"{s.Frame}: {m}"); };
            bool shots = Environment.GetEnvironmentVariable("MP_SHOTS") == "1";
            void Run(int frames)
            {
                for (int i = 0; i < frames; i++)
                {
                    s.Build(640, 360);
                    s.Tick();
                }
            }
            void Shot(string name)
            {
                if (!shots) return;
                // MP_SIZE (default 1280x720): other aspects get the size in the file name
                string sizeText = Environment.GetEnvironmentVariable("MP_SIZE") ?? "1280x720";
                string[] size = sizeText.Split('x');
                int w = Int32.Parse(size[0], CultureInfo.InvariantCulture), h = Int32.Parse(size[1], CultureInfo.InvariantCulture);
                s.Build(w, h);
                using var img = FrontendTool.RasterCanvas(s.DrawList, w, h);
                string path = System.IO.Path.Combine(FrontendTool.OutDir(), sizeText == "1280x720" ? $"{name}.png" : $"{name}_{sizeText}.png");
                img.SaveAsPng(path);
                Console.WriteLine($"shot {path}");
            }
            // a key by its call: the middle of its touch rectangle (y up from the touch screen's bottom)
            void Tap(int call)
            {
                MenuPage page = s.Menu.Page!;
                for (int i = 0; i < page.Items.Count; i++)
                {
                    foreach (MenuAction a in page.Items[i].Actions)
                    {
                        if (!a.Calls.Any(c => c.B == call) || !MenuEngine.TryRect(a, page.Items[i], out float x0, out float y0, out float x1, out float y1)) continue;
                        s.TouchDs((x0 + x1) / 2, 192 - (y0 + y1) / 2);
                        Run(6);
                        return;
                    }
                }
                Console.WriteLine($"FAIL no key with call {call} on page {page.Index}");
            }
            string Page() => $"page {s.Menu.Page?.Index}, box {(s.Popup?.IsOpen == true ? "open" : "closed")}";
            s.Start(18);
            Run(30);
            s.TouchDs(172, 72); // MULTIPLAYER
            Run(60);
            Console.WriteLine($"MULTIPLAYER: {Page()}");
            Shot("name_notice");
            s.Press(MenuKeys.A);
            Run(30);
            Tap(90); // HUNTER LICENSE: the card
            Run(60);
            Console.WriteLine($"HUNTER LICENSE: {Page()}");
            // EDIT NAME shows from the start (owner 2026-10-08: no "you are:" on the license page)
            {
                // EDIT NAME: its highlight (no call; it opens the keyboard's mask, which calls 281)
                MenuPage card = s.Menu.Page!;
                for (int i = 0; i < card.Items.Count; i++)
                {
                    MenuItem it = card.Items[i];
                    if (s.Menu.ItemState(i) == MenuState.Hidden || !s.Menu.ItemModelPath(i).Contains("small_highlight")) continue;
                    MenuAction? act = it.Actions.FirstOrDefault(x => x.Calls.Any(c => c.B == 281) && x.RectW != 0); // (EDIT NAME calls the keyboard itself)
                    if (act == null || !MenuEngine.TryRect(act, it, out float x0, out float y0, out float x1, out float y1)) continue;
                    s.TouchDs((x0 + x1) / 2, 192 - (y0 + y1) / 2);
                    break;
                }
            }
            Run(60);
            Console.WriteLine($"EDIT NAME: {Page()}");
            Shot("name_keyboard");
            const string lower = "1234567890-=qwertyuiopasdfghjklzxcvbnm,./;'[]", shift = "!@#$%^&*()_+QWERTYUIOPASDFGHJKLZXCVBNM<>?:~{}";
            for (int i = 0; i < 12; i++) Tap(343); // backspace: the old name gone
            Tap(344); // ENTER on nothing: vanilla's "invalid name."
            Console.WriteLine($"ENTER empty: {Page()}");
            s.Press(MenuKeys.A);
            Run(20);
            Tap(345);
            Run(20);
            Shot("name_shift");
            Tap(345);
            foreach (char c in text)
            {
                if (c == ' ') Tap(346);
                else if (lower.IndexOf(c) is int k and >= 0) Tap(297 + k);
                else if (shift.IndexOf(c) is int j and >= 0)
                {
                    Tap(345);
                    Tap(297 + j);
                }
                else Console.WriteLine($"(no key for '{c}')");
            }
            Run(20);
            Shot("name_typed");
            Tap(6); // OK
            Run(40);
            Console.WriteLine($"OK: {Page()}, nickname \"{s.Multiplayer.License.Name}\"");
            string file = System.IO.Path.Combine(dir, HunterLicense.FileName);
            Console.WriteLine(System.IO.File.Exists(file) ? System.IO.File.ReadAllText(file) : "FAIL no " + file);
            // a second visit: no notice (OK came back to the card: back twice to the main menu)
            s.Press(MenuKeys.B);
            Run(60);
            s.Press(MenuKeys.B);
            Run(60);
            s.TouchDs(172, 72);
            Run(60);
            Console.WriteLine($"MULTIPLAYER again: {Page()}");
            Console.WriteLine(s.Multiplayer.License.Name == HunterLicense.Clean(text) ? "PASS name kept" : $"FAIL name \"{s.Multiplayer.License.Name}\"");
            // EditText (Replay mode's RENAME, owner 2026-10-08): the keyboard with the caller's text, up to its most letters, the
            // prompt without "for METROID PRIME HUNTERS."; OK gives the text back, CANCEL and B null, each back to the caller's
            // page; the nickname untouched, and its keyboard's prompt the ROM's again. MP_SHOTS=1: rename_wide.png, rename_title.png
            int fails = 0;
            void Check(bool ok, string what)
            {
                Console.WriteLine($"{(ok ? "PASS" : "FAIL")} {what}");
                if (!ok) fails++;
            }
            List<string> Texts() => Enumerable.Range(0, s.Menu.Page!.Items.Count)
                .Where(i => s.Menu.Page.Items[i].IsText && s.Menu.ItemState(i) != MenuState.Hidden)
                .Select(i => s.Menu.Strings[s.Menu.Page.Items[i].States[0].Text!.StringId]).ToList();
            bool PromptFull() => Texts().Any(t => t.StartsWith("enter the new name", StringComparison.Ordinal) && t.Contains("METROID", StringComparison.Ordinal));
            int caller = s.Menu.Page?.Index ?? -1;
            string? got = "unset";
            s.Multiplayer.EditText(s.Menu, "mmmmmmmmmmwwwwwwwwwwxx", 20, caller, t => got = t); // (the widest letters, 2 past the most)
            Run(60);
            Check(s.Menu.Page?.Index == MultiplayerMenu.NamePage && Texts().Contains("mmmmmmmmmmwwwwwwwwww"),
                $"RENAME: the keyboard with the text, cut to 20 letters, no cursor at the most ({Page()})");
            Check(Texts().Contains("enter the new name\n\nyou wish to use."), "RENAME: its prompt (\"enter the new name / you wish to use.\")");
            Shot("rename_wide");
            Tap(lower.IndexOf('k') + 297); // (full: no more)
            Tap(343);
            Tap(lower.IndexOf('k') + 297);
            Tap(6); // OK
            Run(60);
            Check(got == "mmmmmmmmmmwwwwwwwwwk" && s.Menu.Page?.Index == caller, $"RENAME OK: the text back (\"{got}\"), on the caller's page ({Page()})");
            s.Multiplayer.EditText(s.Menu, "sanctorus 4 bots", 20, caller, t => got = t);
            Run(60);
            Shot("rename_title");
            Tap(5); // CANCEL
            Run(60);
            Check(got == null && s.Menu.Page?.Index == caller, $"RENAME CANCEL: null, on the caller's page ({Page()})");
            got = "unset";
            s.Multiplayer.EditText(s.Menu, "sanctorus 4 bots", 20, caller, t => got = t);
            Run(60);
            s.Press(MenuKeys.B);
            Run(60);
            Check(got == null && s.Menu.Page?.Index == caller, $"RENAME B: null, on the caller's page ({Page()})");
            Check(s.Multiplayer.License.Name == HunterLicense.Clean(text), $"RENAME: the nickname untouched (\"{s.Multiplayer.License.Name}\")");
            Tap(90); // HUNTER LICENSE -> EDIT NAME: the nickname's keyboard as before
            Run(60);
            MenuPage card2 = s.Menu.Page!;
            for (int i = 0; i < card2.Items.Count; i++)
            {
                MenuAction? act = card2.Items[i].Actions.FirstOrDefault(x => x.Calls.Any(c => c.B == 281) && x.RectW != 0);
                if (s.Menu.ItemState(i) == MenuState.Hidden || act == null || !MenuEngine.TryRect(act, card2.Items[i], out float x0, out float y0, out float x1, out float y1)) continue;
                s.TouchDs((x0 + x1) / 2, 192 - (y0 + y1) / 2);
                break;
            }
            Run(60);
            Check(s.Menu.Page?.Index == MultiplayerMenu.NamePage && PromptFull() && Texts().Contains("_"),
                $"EDIT NAME after a RENAME: the ROM's whole prompt, an empty box ({Page()})");
            Console.WriteLine(fails == 0 ? "PASS rename" : $"FAIL rename ({fails})");
        }

        // The Hunter License card (page 60): the vanilla BizHawk run's test licenses A and B (kit m6/license/vanilla: the same
        // values poked into vanilla's license), every tab's lines against what vanilla printed, EDIT NAME -> the keyboard and
        // back, back to the doors; then the after-match rules (wins, streaks, favourites, rank points) on a scratch license.
        private static void License()
        {
            string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"mpmenu_card_{Environment.ProcessId}");
            if (System.IO.Directory.Exists(dir)) System.IO.Directory.Delete(dir, recursive: true);
            var a = new HunterLicense
            {
                Name = "melonDS", NoticeSeen = false, RankPoints = 150, FavoriteHunter = 1, FavoriteWeapon = 5, FavoriteMode = 2,
                LuckyArena = 95, BeatGame = true, WifiWins = 3, OnlineMatches = 7, WirelessWins = 120, LocalMatches = 250,
                BipedKills = 456, AltFormKills = 78, HeadshotKills = 21, Disconnects = 2, WirelessSeconds = 3725, WifiSeconds = 59,
                TotalSeconds = 99960, WifiBestWinStreak = 2, WifiBestKillStreak = 4, WirelessBestWinStreak = 5, WirelessBestKillStreak = 9,
            };
            a.Save(dir);
            FrontendSession s = FrontendSession.Load(Paths.FileSystem);
            s.Multiplayer.ProfileDir = dir;
            s.Log = m => { if (m.StartsWith("multiplayer") || m.StartsWith("work")) Console.WriteLine($"{s.Frame}: {m}"); };
            bool shots = Environment.GetEnvironmentVariable("MP_SHOTS") == "1";
            int fails = 0;
            void Check(bool ok, string what)
            {
                Console.WriteLine($"{(ok ? "PASS" : "FAIL")} {what}");
                if (!ok) fails++;
            }
            void Run(int frames)
            {
                for (int i = 0; i < frames; i++)
                {
                    s.Build(640, 360);
                    s.Tick();
                }
            }
            void Shot(string name)
            {
                if (!shots) return;
                // MP_SIZE (default 1280x720): other aspects get the size in the file name
                string sizeText = Environment.GetEnvironmentVariable("MP_SIZE") ?? "1280x720";
                string[] size = sizeText.Split('x');
                int w = Int32.Parse(size[0], CultureInfo.InvariantCulture), h = Int32.Parse(size[1], CultureInfo.InvariantCulture);
                s.Build(w, h);
                using var img = FrontendTool.RasterCanvas(s.DrawList, w, h);
                string path = System.IO.Path.Combine(FrontendTool.OutDir(), sizeText == "1280x720" ? $"{name}.png" : $"{name}_{sizeText}.png");
                img.SaveAsPng(path);
                Console.WriteLine($"shot {path}");
            }
            // an item's touch rectangle by what it is (y up from the touch screen's bottom)
            bool TapWhere(Func<MenuItem, bool> match, int frames = 6)
            {
                MenuPage page = s.Menu.Page!;
                for (int i = 0; i < page.Items.Count; i++)
                {
                    if (!match(page.Items[i]) || s.Menu.ItemState(i) == MenuState.Hidden) continue;
                    foreach (MenuAction act in page.Items[i].Actions)
                    {
                        if (!MenuEngine.TryRect(act, page.Items[i], out float x0, out float y0, out float x1, out float y1)) continue;
                        s.TouchDs((x0 + x1) / 2, 192 - (y0 + y1) / 2);
                        Run(frames);
                        return true;
                    }
                }
                return false;
            }
            bool Tap(int call, int frames = 6) => TapWhere(it => it.Actions.Any(act => act.Calls.Any(c => c.B == call)), frames);
            bool Shown(string widget) => Enumerable.Range(0, s.Menu.Page!.Items.Count).Any(i =>
                s.Menu.ItemModelPath(i).EndsWith("\\" + widget + "_Model.bin", StringComparison.OrdinalIgnoreCase) && s.Menu.ItemState(i) != MenuState.Hidden);
            // the texts shown, top to bottom (the card's lines and numbers)
            List<string> Texts() => Enumerable.Range(0, s.Menu.Page!.Items.Count)
                .Where(i => s.Menu.Page.Items[i].IsText && s.Menu.ItemState(i) != MenuState.Hidden)
                .OrderByDescending(i => s.Menu.Page.Items[i].Y)
                .Select(i => s.Menu.Strings[s.Menu.Page.Items[i].States[0].Text!.StringId]).Where(t => t.Length > 0).ToList();
            bool Has(params string[] want) => want.All(w => Texts().Contains(w));
            s.Start(18);
            Run(30);
            s.TouchDs(172, 72); // MULTIPLAYER
            Run(60);
            Check(s.Popup?.IsOpen == true, "the first visit: the nickname notice");
            s.Press(MenuKeys.A);
            Run(30);
            Check(s.Popup?.IsOpen == false, "A closes it");
            Tap(90); // HUNTER LICENSE
            Run(60);
            int Page() => s.Menu.Page?.Index ?? -1;
            Check(Page() == MultiplayerMenu.LicensePage, $"HUNTER LICENSE opens the card (page {Page()})");
            // straight back out (owner 2026-10-07 20:05, rev 1: B, the back arrow and the device's back did nothing): B, the
            // arrow, and B after flipping tabs (the device's back is B)
            s.Press(MenuKeys.B);
            Run(60);
            Check(Page() == MultiplayerMenu.TypePage, $"B straight away: the doors (page {Page()})");
            Tap(90);
            Run(60);
            Check(Tap(17), "the back arrow is there");
            Run(60);
            Check(Page() == MultiplayerMenu.TypePage, $"the back arrow straight away: the doors (page {Page()})");
            Tap(90);
            Run(60);
            foreach (MenuKeys k in new[] { MenuKeys.R, MenuKeys.R, MenuKeys.L })
            {
                s.Press(k);
                Run(20);
            }
            s.Press(MenuKeys.B);
            Run(60);
            Check(Page() == MultiplayerMenu.TypePage, $"B after flipping tabs with L/R: the doors (page {Page()})");
            Tap(90);
            Run(60);
            Check(Page() == MultiplayerMenu.LicensePage, $"and in again (page {Page()})");
            if (Environment.GetEnvironmentVariable("MP_DUMP") == "1") DumpPage(s);
            Console.WriteLine("texts: " + String.Join(" | ", Texts()).Replace("\n", " "));
            Check(Has("melonDS", "elite hunter", "150", "390") && Shown("silverstar") && Shown("silvermedal") && Shown("alimbicswirl")
                && Shown("kandenmost") && !Shown("octolith") && !Shown("cardheadergold") && !Shown("frame200") && !Shown("bigpanel"),
                "license A: name, elite hunter (silver stars), 150 / 390, silver medal, alimbic swirl, Kanden, no roster");
            // vanilla's lines for A (BizHawk, kit m6/license/vanilla/v5_*.png)
            string[][] expect =
            {
                new[] { "wi-fi win record", "3 of 7", "wireless win record", "120 of 250", "connection history", "99.2%" },
                new[] { "win ratio", "47.9%", "win streak", "7", "lucky arena", "combat hall" },
                new[] { "favorite weapon", "judicator", "headshot kills", "21", "favorite mode", "capture" },
                new[] { "biped kills", "456", "alt-form kills", "78", "kill streak", "13" },
                new[] { "wi-fi play time", "0h 00m 59s", "wireless play time", "1h 02m 05s", "total game time" },
            };
            for (int tab = 0; tab < 5; tab++)
            {
                Shot($"license_a_tab{tab}");
                List<string> t = Texts();
                Check(expect[tab].All(t.Contains), $"tab {tab}: {String.Join(" / ", expect[tab])}");
                s.Press(MenuKeys.R);
                Run(20);
            }
            Check(Texts().Contains("wi-fi win record"), "R after the last tab: the first again");
            // L / R click like a tap on a tab (owner 2026-10-24)
            var clicks = new List<int>();
            s.PlaySound = id => clicks.Add(id); 
            s.TouchDs(38 + 25 * 4 + 12, 192 - (62.5f + 192)); // (the last tab: R then wraps, L comes back to it)
            Run(20);
            List<int> tapClicks = clicks.ToList();
            clicks.Clear();
            s.Press(MenuKeys.R);
            Run(20);
            Check(tapClicks.Count > 0 && clicks.Count > 0 && clicks[0] == tapClicks[0],
                $"R clicks like a tap on a tab (tap {string.Join(",", tapClicks.Select(x => x.ToString("X")))}, R {string.Join(",", clicks.Select(x => x.ToString("X")))})");
            s.PlaySound = null;
            s.Press(MenuKeys.L);
            Run(20);
            Check(Texts().Contains("wi-fi play time") && Texts().Any(x => x.StartsWith("27h 46m", StringComparison.Ordinal)),
                "L: back round to the last tab (total game time 27h 46m and the menus' seconds)");
            // a tap on a tab picks it (owner 2026-10-08): its strip under the card, 25 DS px apart from x 38, y 53..72 (top screen)
            for (int tab = 0; tab < 5; tab++)
            {
                int pick = (tab + 2) % 5;
                s.TouchDs(38 + 25 * pick + 12, 192 - (62.5f + 192));
                Run(20);
                Check(Texts().Contains(expect[pick][0]), $"a tap on tab {pick + 1}: {expect[pick][0]}");
            }
            // a double tap (owner 2026-10-23: the tabs vanished or showed another one's look): the tab stays picked and lit,
            // the others unlit, the first tab's outline never hidden
            MenuState TabState(int n) => Enumerable.Range(0, s.Menu.Page!.Items.Count)
                .Where(i => s.Menu.ItemModelPath(i).EndsWith("\\tab" + n + "_Model.bin", StringComparison.OrdinalIgnoreCase))
                .Select(i => s.Menu.ItemState(i)).FirstOrDefault();
            for (int pick = 0; pick < 5; pick++)
            {
                for (int tap = 0; tap < 3; tap++)
                {
                    s.TouchDs(38 + 25 * pick + 12, 192 - (62.5f + 192));
                    Run(tap == 0 ? 2 : 1); // (the second tap lands while the first is still settling)
                }
                Run(20);
                string states = String.Join(" ", Enumerable.Range(1, 5).Select(n => TabState(n).ToString()));
                bool ok = Texts().Contains(expect[pick][0]) && TabState(1) != MenuState.Hidden;
                for (int n = 2; n <= 5; n++) ok &= TabState(n) == (n == pick + 1 ? MenuState.Selected : MenuState.Idle);
                Check(ok, $"a triple tap on tab {pick + 1}: still picked, lit once (tab states {states})");
            }
            s.TouchDs(38 + 12, 192 - (62.5f + 192));
            Run(20);
            Check(!s.Menu.FocusableItems().Any(i => s.Menu.ItemModelPath(i).Contains("\\tab", StringComparison.Ordinal)),
                "the tabs take taps, not D-pad focus (focusable: " + String.Join(", ", s.Menu.FocusableItems()) + ")");
            // EDIT NAME: shown from the start in the corner, no "you are:" or its panel (owner 2026-10-08), then the keyboard,
            // and CANCEL comes back here
            Check(Shown("edit") && !Shown("yourpanel") && !Shown("blackdrop") && !Texts().Any(x => x.StartsWith("you are", StringComparison.Ordinal)),
                "EDIT NAME shown, no \"you are:\" or its panel");
            if (Environment.GetEnvironmentVariable("MP_DUMP") == "1") DumpPage(s);
            Shot("license_edit");
            // no page change animations between the card and its keyboard (owner 2026-10-08: "the license still disappears for a
            // moment like youre paging away both when you click edit name and then go back"): the keyboard is up on the next
            // tick, and back on the card (CANCEL, OK, B) it's whole at once, on the tab it was left on
            s.Press(MenuKeys.R); // (tab 2: win ratio)
            Run(20);
            bool EditName() => TapWhere(it => it.Actions.Any(act => act.Calls.Any(c => c.B == 281) && act.RectW != 0)
                && it.States.Any(st => st.WidgetIndex >= 0 && s.Menu.File.Widgets[st.WidgetIndex].ModelPath.Contains("small_highlight")), frames: 2);
            // a tap on the action that makes this call (the keyboard's keys share an item), then `frames` ticks
            void TapCall(int call, int frames)
            {
                MenuPage page = s.Menu.Page!;
                for (int i = 0; i < page.Items.Count; i++)
                {
                    if (s.Menu.ItemState(i) == MenuState.Hidden) continue;
                    foreach (MenuAction a in page.Items[i].Actions)
                    {
                        if (!a.Calls.Any(c => c.B == call) || !MenuEngine.TryRect(a, page.Items[i], out float x0, out float y0, out float x1, out float y1)) continue;
                        s.TouchDs((x0 + x1) / 2, 192 - (y0 + y1) / 2);
                        Run(frames);
                        return;
                    }
                }
                Check(false, $"a key with call {call} on page {page.Index}");
            }
            void BackOnCard(string how)
            {
                Check(s.Menu.Page?.Index == MultiplayerMenu.LicensePage && s.Menu.Settled && s.Menu.PageFrame <= 2,
                    $"{how}: the card, whole, at once (page {s.Menu.Page?.Index}, settled {s.Menu.Settled}, frame {s.Menu.PageFrame})");
                Check(Texts().Contains("win ratio"), $"{how}: on the tab it was left on (win ratio)");
            }
            EditName();
            Check(s.Menu.Page?.Index == MultiplayerMenu.NamePage && s.Menu.PageFrame <= 2,
                $"EDIT NAME opens the keyboard at once (page {s.Menu.Page?.Index}, frame {s.Menu.PageFrame})");
            Shot("license_keyboard_now");
            Run(30);
            Shot("license_keyboard");
            TapCall(5, 2); // CANCEL
            BackOnCard("CANCEL");
            Shot("license_back_now");
            Run(20);
            EditName();
            Run(30);
            s.Press(MenuKeys.B);
            Run(2);
            BackOnCard("the keyboard's B");
            Run(20);
            EditName();
            Run(30);
            foreach (char c in "kanden") TapCall(297 + "1234567890-=qwertyuiopasdfghjklzxcvbnm,./;'[]".IndexOf(c), 2);
            TapCall(6, 2); // OK
            BackOnCard("OK");
            Check(Texts().Contains("kanden"), "OK: the card shows the name typed");
            s.Multiplayer.License.Name = "melonDS"; // (as it was, for what follows)
            s.Multiplayer.License.Save(dir);
            Run(20);
            s.Press(MenuKeys.B);
            Run(60);
            Check(s.Menu.Page?.Index == MultiplayerMenu.TypePage, $"back: the doors (page {s.Menu.Page?.Index})");
            // license B: legendary, 850 points, 210 wins of 520, the game at 100%, Weavel
            var b = new HunterLicense
            {
                Name = "melonDS", NoticeSeen = true, RankPoints = 850, FavoriteHunter = 6, FavoriteWeapon = 8, FavoriteMode = 6,
                LuckyArena = 119, BeatGame = true, Completed = true, WifiWins = 150, OnlineMatches = 300, WirelessWins = 60, LocalMatches = 220,
            };
            b.Save(dir);
            s.Multiplayer.ProfileDir = dir;
            Tap(90);
            Run(60);
            Console.WriteLine("texts: " + String.Join(" | ", Texts()).Replace("\n", " "));
            Check(Has("legendary hunter", "850", "--", "150 of 300", "60 of 220") && Shown("blackstar") && Shown("goldmedal")
                && Shown("cardheadergold") && Shown("frame200gold") && Shown("octolith") && !Shown("alimbicswirl") && Shown("weavelmost"),
                "license B: legendary (black stars, gold header and frame), 850 / --, gold medal, octolith, Weavel");
            Shot("license_b_tab0");
            s.Press(MenuKeys.R);
            Run(20);
            Check(Has("40.4%", "0", "stasis bunker"), "license B tab 1: 40.4% / 0 / stasis bunker");
            Shot("license_b_tab1");

            // the after-match rules (arm9 0x020562A4) on a new license
            var l = new HunterLicense();
            HunterLicense.Opponent Bot(Hunter h, int place) => new(h, place, true, 1, -1);
            HunterLicense.Opponent Human(Hunter h, int place, int rank, int team = 1) => new(h, place, false, team, rank);
            HunterLicense.MatchRecord M(int place, bool wifi = false, params HunterLicense.Opponent[] o) =>
                new(wifi, GameMode.Battle, 95, 125.5, Hunter.Kanden, place, false, 0, o);
            var k1 = new HunterLicense.MatchKills();
            k1.Kill(false, true, 0);
            k1.Kill(true, false, 9);
            k1.Kill(false, false, 5);
            k1.StreakEnd();
            k1.Kill(false, false, 5);
            l.AfterMatch(M(0, false, Bot(Hunter.Samus, 1)), k1);
            Check(l.WirelessWins == 1 && l.LocalMatches == 1 && l.WirelessWinStreak == 1 && l.WirelessBestWinStreak == 1
                && l.BipedKills == 3 && l.AltFormKills == 1 && l.HeadshotKills == 1 && l.WirelessBestKillStreak == 3 && l.WirelessKillStreak == 1
                && l.FavoriteWeapon == 5 && l.FavoriteHunter == (int)Hunter.Kanden && l.RankPoints == 0 && l.LuckyArena == 95
                && Math.Abs(l.WirelessSeconds - 125.5) < 0.01,
                $"a bot win: 1 of 1 wireless, kills 3 biped + 1 alt-form, 1 headshot, best kill streak 3 (now 1), judicator, Kanden, no rank points ({l.RankPoints})");
            var k2 = new HunterLicense.MatchKills();
            k2.Kill(false, false, 0);
            k2.Kill(false, false, 0);
            l.AfterMatch(M(1, false, Bot(Hunter.Samus, 0)), k2);
            Check(l.WirelessWins == 1 && l.LocalMatches == 2 && l.WirelessWinStreak == 0 && l.WirelessBestWinStreak == 1
                && l.WirelessKillStreak == 3 && l.WirelessBestKillStreak == 3 && l.FavoriteWeapon == 0,
                "a loss: the win streak ends; the kill streak runs on across matches (1 + 2 = 3); power beam takes over (3 > 2)");
            // rank: a level-0 player beating a level-2 human gains 7, losing to a level-0 human loses 1, a teammate or bot nothing
            l.AfterMatch(M(0, false, Human(Hunter.Trace, 1, 2), Bot(Hunter.Spire, 2)), null);
            Check(l.RankPoints == 7, $"beat a level-2 human: +7 ({l.RankPoints})");
            l.AfterMatch(M(1, false, Human(Hunter.Trace, 0, 0)), null);
            Check(l.RankPoints == 6, $"lost to a level-0 human: -1 ({l.RankPoints})");
            l.AfterMatch(new HunterLicense.MatchRecord(true, GameMode.BattleTeams, 95, 60, Hunter.Kanden, 0, true, 0,
                new[] { Human(Hunter.Trace, 0, 0, team: 0), Human(Hunter.Sylux, 1, 4, team: 1) }), null);
            Check(l.RankPoints == 21 && l.OnlineMatches == 1 && l.WifiWins == 1 && l.ArenaOpen(119),
                $"a Wi-Fi team win: the teammate skipped, beat a legendary: +15 ({l.RankPoints}); Stasis Bunker opens");
            l.RankPoints = 840;
            l.AfterMatch(M(0, false, Human(Hunter.Trace, 1, 4)), null);
            Check(l.RankPoints == 850 && l.RankLevel == 4, $"points stop at 850, level 4 from 750 ({l.RankPoints}, level {l.RankLevel})");
            Console.WriteLine(fails == 0 ? "PASS license" : $"FAIL license ({fails})");
        }

        // -mpmenu back (owner report R1 2026-10-07: "can't back out of multiplayer with touch"): every multiplayer page's back
        // ARROW by touch, then B, from the doors to the lobby, the keypad and the card. Rev 1's arrows (and some B actions)
        // carry no page: vanilla's code leaves on call 17. A page that asks first ("this will end wireless communications")
        // is answered NO by touch (stays), then YES by touch (leaves).
        private static void Back()
        {
            string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"mpmenu_back_{Environment.ProcessId}");
            if (System.IO.Directory.Exists(dir)) System.IO.Directory.Delete(dir, recursive: true);
            new HunterLicense { Name = "melonDS", NoticeSeen = true }.Save(dir);
            FrontendSession s = FrontendSession.Load(Paths.FileSystem);
            s.Multiplayer.ProfileDir = dir;
            s.Log = m => { if (m.Contains("unhandled") || m.StartsWith("work")) Console.WriteLine($"{s.Frame}: {m}"); };
            int fails = 0;
            void Check(bool ok, string what)
            {
                Console.WriteLine($"{(ok ? "PASS" : "FAIL")} {what}");
                if (!ok) fails++;
            }
            void Run(int frames)
            {
                for (int i = 0; i < frames; i++)
                {
                    s.Build(640, 360);
                    s.Tick();
                }
            }
            int Page() => s.Menu.Page?.Index ?? -1;
            bool TapWhere(Func<MenuItem, MenuAction, bool> match)
            {
                MenuPage page = s.Menu.Page!;
                for (int i = page.Items.Count - 1; i >= 0; i--)
                {
                    if (s.Menu.ItemState(i) is MenuState.Hidden or MenuState.State5) continue;
                    foreach (MenuAction act in page.Items[i].Actions)
                    {
                        if (!match(page.Items[i], act)) continue;
                        if (!MenuEngine.TryRect(act, page.Items[i], out float x0, out float y0, out float x1, out float y1)) continue;
                        s.TouchDs((x0 + x1) / 2, 192 - (y0 + y1) / 2);
                        Run(6);
                        return true;
                    }
                }
                return false;
            }
            bool Tap(int call) => TapWhere((it, act) => act.Calls.Any(c => c.B == call));
            // the back arrow: the touch screen's top-left button (rect 4,162 - 36,194)
            bool TapArrow() => TapWhere((it, act) => act.RectX == 4 && act.RectY == 162);
            bool Asking() => s.Popup?.IsOpen == true;
            void Answer(bool yes)
            {
                s.TouchDs(yes ? 108 : 148, 124); // the box's YES / NO (MenuPopup ButtonY 108, YesX 92, NoX 132, 32 px)
                Run(60);
            }
            // leave `page` by touch (or B), answering the question if the page asks, and land on `expect`
            void Leave(string what, int page, int expect, bool asks, bool byB = false)
            {
                if (Page() != page)
                {
                    Check(false, $"{what}: not on page {page} (page {Page()})");
                    return;
                }
                if (byB) s.Press(MenuKeys.B);
                else if (!TapArrow())
                {
                    Check(false, $"{what}: no back arrow on page {page}");
                    return;
                }
                Run(60);
                if (asks)
                {
                    Check(Asking() && Page() == page, $"{what}: {(byB ? "B" : "the arrow")} asks first (page {Page()}, box {Asking()})");
                    if (!Asking()) return;
                    Answer(false);
                    Check(!Asking() && Page() == page, $"{what}: NO by touch stays (page {Page()})");
                    if (byB) s.Press(MenuKeys.B);
                    else TapArrow();
                    Run(60);
                    Answer(true);
                }
                Check(!Asking() && Page() == expect, $"{what}: {(byB ? "B" : "the arrow")} -> page {expect} (page {Page()})");
            }
            void Go(params (int X, int Y)[] taps)
            {
                foreach ((int x, int y) in taps)
                {
                    s.TouchDs(x, y);
                    Run(40);
                }
            }
            (int, int) multiplayer = (172, 72), local = LocalDoor, create = (48, 72), rulesOk = (212, 173), icon = (26, 134), portrait = (70, 77);
            s.Start(18);
            Run(30);
            Go(multiplayer);
            Leave("CHOOSE MULTIPLAYER TYPE (25)", MultiplayerMenu.TypePage, 18, asks: false);
            Go(multiplayer);
            Leave("CHOOSE MULTIPLAYER TYPE (25)", MultiplayerMenu.TypePage, 18, asks: false, byB: true);
            Go(multiplayer, local);
            Leave("CREATE OR JOIN (26)", MultiplayerMenu.CreateJoinPage, MultiplayerMenu.TypePage, asks: false);
            Go(local);
            Leave("CREATE OR JOIN (26)", MultiplayerMenu.CreateJoinPage, MultiplayerMenu.TypePage, asks: false, byB: true);
            Go(local, create);
            Leave("game mode (27)", MultiplayerMenu.ModePage, MultiplayerMenu.TypePage, asks: true);
            Go(local, create);
            Leave("game mode (27)", MultiplayerMenu.ModePage, MultiplayerMenu.TypePage, asks: true, byB: true);
            foreach (var m in ModeTaps)
            {
                Go(local, create, (m.X, m.Y));
                Leave($"rules: {m.Name} ({Page()})", Page() is >= 28 and <= 34 ? Page() : 28, MultiplayerMenu.TypePage, asks: true);
            }
            Go(local, create, (ModeTaps[0].X, ModeTaps[0].Y));
            Leave("rules: battle (28)", 28, MultiplayerMenu.TypePage, asks: true, byB: true);
            Go(local, create, (ModeTaps[0].X, ModeTaps[0].Y), rulesOk);
            Leave("SELECT HUNTER (35)", MultiplayerMenu.HunterPage, MultiplayerMenu.TypePage, asks: true);
            Go(local, create, (ModeTaps[0].X, ModeTaps[0].Y), rulesOk);
            Leave("SELECT HUNTER (35)", MultiplayerMenu.HunterPage, MultiplayerMenu.TypePage, asks: true, byB: true);
            Go(local, create, (ModeTaps[0].X, ModeTaps[0].Y), rulesOk, icon, portrait);
            Leave("lobby (38)", MultiplayerMenu.LobbyPage, MultiplayerMenu.TypePage, asks: true);
            Go(local, create, (ModeTaps[0].X, ModeTaps[0].Y), rulesOk, icon, portrait);
            Leave("lobby (38)", MultiplayerMenu.LobbyPage, MultiplayerMenu.TypePage, asks: true, byB: true);
            // a joiner's pages (36 SELECT HUNTER, 39 "please wait") without a host: opened directly, as the list would
            foreach (int p in new[] { MultiplayerMenu.JoinHunterPage, MultiplayerMenu.JoinLobbyPage })
            {
                foreach (bool b in new[] { false, true })
                {
                    Go(local);
                    s.Menu.GoTo(p);
                    Run(40);
                    Leave($"joiner page {p}", p, MultiplayerMenu.TypePage, asks: true, byB: b);
                }
            }
            // the "enter address" keypad (61): AVAILABLE GAMES' address row, tap and tap again; back to the list
            foreach (bool b in new[] { false, true })
            {
                Go(local);
                Tap(98);
                Run(10);
                Tap(98);
                Run(40);
                if (!b) LanShot(s, "keypad"); // (MP_SHOTS=1)
                Leave("ENTER ADDRESS keypad (61)", MultiplayerMenu.AddressPage, MultiplayerMenu.CreateJoinPage, asks: false, byB: b);
                Leave("CREATE OR JOIN (26) after the keypad", MultiplayerMenu.CreateJoinPage, MultiplayerMenu.TypePage, asks: false);
            }
            // HUNTER LICENSE (60) and its keyboard (62): the keyboard's B / CANCEL back to the card, the card's back to the doors
            foreach (bool b in new[] { false, true })
            {
                Tap(90);
                Run(40);
                Leave("HUNTER LICENSE (60)", MultiplayerMenu.LicensePage, MultiplayerMenu.TypePage, asks: false, byB: b);
            }
            Console.WriteLine(fails == 0 ? "PASS back" : $"FAIL back ({fails})");
        }

        // how many of the page's items showing now draw this widget (P8 lobby checks)
        private static int VisibleCount(FrontendSession s, string piece)
        {
            var tris = new List<WidgetTri>();
            s.Menu.Collect(tris);
            return tris.Select(t => t.Item).Distinct().Count(i => !s.Menu.Page!.Items[i].IsText
                && System.IO.Path.GetFileNameWithoutExtension(s.Menu.ItemModelPath(i)).Replace("_Model", "") == piece);
        }

        // -mpmenu lobby (owner report R9 2026-10-07): bots added, confirmed, their stars stepped and removed in turn; an empty seat
        // shows nothing but its grid (no arrows, stars, check, remove button, name), a bot's seat its own pieces only.
        private static void LobbyCheck()
        {
            FrontendSession s = FrontendSession.Load(Paths.FileSystem);
            s.Log = m => { if (m.Contains("unhandled")) Console.WriteLine($"{s.Frame}: {m}"); };
            int fails = 0;
            void Check(bool ok, string what)
            {
                Console.WriteLine($"{(ok ? "PASS" : "FAIL")} {what}");
                if (!ok) fails++;
            }
            void Run(int frames)
            {
                for (int i = 0; i < frames; i++)
                {
                    s.Build(640, 360);
                    s.Tick();
                }
            }
            void Go(params (int X, int Y)[] taps)
            {
                foreach ((int x, int y) in taps)
                {
                    s.TouchDs(x, y);
                    Run(40);
                }
            }
            // each seat's column (its grid's centre, DS px) and the visible pieces in it, between the seat names (y -150..-50)
            float[] column = { 42, 99, 156, 212 };
            List<string> Pieces(int seat)
            {
                var tris = new List<WidgetTri>();
                s.Menu.Collect(tris);
                var found = new List<string>();
                foreach (var g in tris.GroupBy(t => t.Item))
                {
                    var v = g.SelectMany(t => new[] { t.A, t.B, t.C }).ToList();
                    float cx = (v.Min(p => p.X) + v.Max(p => p.X)) / 2, cy = (v.Min(p => p.Y) + v.Max(p => p.Y)) / 2;
                    if (MathF.Abs(cx - column[seat]) > 28 || cy < -150 || cy > -50 || v.Max(p => p.X) - v.Min(p => p.X) > 120) continue; // (not the backdrop)
                    MenuItem it = s.Menu.Page!.Items[g.Key];
                    string what = it.IsText ? s.Menu.Strings[it.States[0].Text!.StringId]
                        : System.IO.Path.GetFileNameWithoutExtension(s.Menu.ItemModelPath(g.Key)).Replace("_Model", "");
                    if (what != "playergrid") found.Add(what);
                }
                return found;
            }
            void Seats(string when, params bool[] used)
            {
                for (int seat = 1; seat < 4; seat++)
                {
                    List<string> p = Pieces(seat);
                    string list = p.Count == 0 ? "nothing" : String.Join(", ", p);
                    if (!used[seat - 1]) Check(p.Count == 0, $"{when}: empty seat {seat} shows {list}");
                    else Check(p.Count(x => x.StartsWith("spicy")) <= 1 && p.Any(x => x.StartsWith("choice")),
                        $"{when}: bot seat {seat} shows {list}");
                }
            }
            // seat 1-3: the check (OK) and the remove button, DS touch px (y down)
            (int, int)[] ok = { (0, 0), (98, 139), (154, 63), (212, 139) }, remove = { (0, 0), (116, 119), (173, 119), (229, 119) };
            // the star bars' touch spots: under the grid for seats 1 and 3, above it for seat 2
            (int, int)[] stars = { (0, 0), (99, 135), (156, 72), (212, 135) };
            (int, int) addBot = (223, 170);
            s.Start(18);
            Run(30);
            // MP_TEAMS=1: battle with TEAM PLAY on (the rules page's last row), for the team buttons' pictures (T8)
            bool teamPlay = Environment.GetEnvironmentVariable("MP_TEAMS") == "1";
            if (teamPlay) Go((172, 72), LocalDoor, (48, 72), (44, 68), (223, 126), (212, 173), (26, 134), (70, 77));
            else Go((172, 72), LocalDoor, (48, 72), (44, 68), (212, 173), (26, 134), (70, 77));
            Check(s.Menu.Page?.Index == MultiplayerMenu.LobbyPage, $"the lobby (page {s.Menu.Page?.Index})");
            Seats("no bots", false, false, false);
            Go(addBot, addBot, addBot);
            Run(30);
            Seats("three bots", true, true, true);
            if (Environment.GetEnvironmentVariable("MP_DUMP") == "1") DumpPage(s);
            Go(ok[1], ok[2], ok[3]);
            Run(30);
            Seats("three bots confirmed", true, true, true);
            if (Environment.GetEnvironmentVariable("MP_DUMP") == "1") DumpPage(s);
            LanShot(s, "lobby_full"); // (P8: MP_SHOTS=1, the 4-seat lobby for the 5-8 seat mockups)
            Go(stars[2], stars[2], stars[1]);
            Run(30);
            Seats("stars stepped", true, true, true);
            Go(remove[1]);
            Run(30);
            Seats("seat 1 removed", false, true, true);
            LanShot(s, "lobby_three"); // (P8: ADD BOT showing)
            Go(remove[2]);
            Run(30);
            Seats("seats 1 and 2 removed", false, false, true);
            Go(remove[3]);
            Run(30);
            Seats("every bot removed", false, false, false);
            Go(addBot, addBot);
            Run(30);
            Seats("two bots again, not confirmed", false, true, true);
            Go(remove[2]);
            Run(30);
            Seats("an unconfirmed bot removed", false, false, true);
            Go(ok[3], stars[3]);
            Run(30);
            Seats("seat 3 confirmed, two stars", false, false, true);
            // P8 (MultiplayerMenu.Seats8.cs): ADD BOT up to 8 seats, two rows at 0.56; a seat's pieces move to its column
            // (DS x 42/99/156/212) and row (y 81 / 129.5) from its ROM seat's (seat 5-8 = ROM seat 1, 3, 1, 3; block y 102)
            const float k8 = 0.56f;
            float[] row8 = { 81, 129.5f };
            (int, int) At8(int seat, (int X, int Y) rom)
            {
                int src = seat < 4 ? seat : 1 + 2 * ((seat - 4) % 2);
                return ((int)MathF.Round(column[seat % 4] + (rom.X - column[src]) * k8), (int)MathF.Round(row8[seat / 4] + (rom.Y - 102) * k8));
            }
            Go(addBot, addBot, addBot, addBot, addBot, addBot);
            Run(30);
            int Visible(string piece) => VisibleCount(s, piece);
            Check(Visible("adbot") == 0 && Visible("playergrid") == 8, $"8 seats: ADD BOT gone, 8 grids ({Visible("playergrid")})");
            LanShot(s, "lobby_eight");
            // confirm the 5th seat's bot by its (small) check, then take seats 5-8's bots away by their remove buttons
            Go(At8(4, ok[1]));
            Run(30);
            LanShot(s, "lobby_eight_confirmed");
            if (Environment.GetEnvironmentVariable("MP_DUMP") == "2") DumpPage(s);
            Go(At8(7, remove[3]), At8(6, remove[1]), At8(5, remove[3]));
            Run(30);
            Check(Visible("playergrid") == 8, $"5 seats: still two rows ({Visible("playergrid")} grids)");
            Go(At8(1, remove[1]));
            Run(30);
            // 4 seats again: seat 5's bot moved down into seat 2, the rows gone (vanilla's lobby)
            Check(Visible("playergrid") == 4, $"4 seats: one row again ({Visible("playergrid")} grids)");
            Seats("4 seats again", true, true, true);
            LanShot(s, "lobby_back_to_four");
            Console.WriteLine(fails == 0 ? "PASS lobby" : $"FAIL lobby ({fails})");
        }

        private static void DumpPage(FrontendSession session)
        {
            MenuEngine menu = session.Menu;
            if (menu.Page == null) return;
            var only = new HashSet<int>((Environment.GetEnvironmentVariable("MP_ITEMS") ?? "")
                .Split(',', StringSplitOptions.RemoveEmptyEntries).Select(Int32.Parse));
            var tris = new List<WidgetTri>();
            menu.Collect(tris);
            Console.WriteLine($"page {menu.Page.Index} at tick {session.Frame}, focus {menu.FocusedItem()}");
            for (int i = 0; i < menu.Page.Items.Count; i++)
            {
                if (only.Count > 0 ? !only.Contains(i) : menu.ItemState(i) == MenuState.Hidden && Environment.GetEnvironmentVariable("MP_XY") != "1") continue;
                MenuItem it = menu.Page.Items[i];
                var mine = tris.Where(t => t.Item == i).SelectMany(t => new[] { t.A, t.B, t.C }).ToList();
                string bounds = mine.Count == 0 ? "no tris"
                    : FormattableString.Invariant($"x {mine.Min(v => v.X):0.#}..{mine.Max(v => v.X):0.#} y {mine.Min(v => v.Y):0.#}..{mine.Max(v => v.Y):0.#}");
                if (Environment.GetEnvironmentVariable("MP_TRIS") == i.ToString(CultureInfo.InvariantCulture)) // one item's triangles
                {
                    foreach (WidgetTri t in tris.Where(t => t.Item == i))
                    {
                        Console.WriteLine(FormattableString.Invariant($"      tri x {MathF.Min(t.A.X, MathF.Min(t.B.X, t.C.X)):0.#}..{MathF.Max(t.A.X, MathF.Max(t.B.X, t.C.X)):0.#} y {MathF.Min(t.A.Y, MathF.Min(t.B.Y, t.C.Y)):0.#}..{MathF.Max(t.A.Y, MathF.Max(t.B.Y, t.C.Y)):0.#} rgba {t.A.R:0.##},{t.A.G:0.##},{t.A.B:0.##},{t.A.A:0.##} tex {t.TextureId}"));
                    }
                }
                if (Environment.GetEnvironmentVariable("MP_Z") == "1" && mine.Count > 0) // the draw order (WidgetTri.Z, larger = nearer) and the item's depth
                {
                    var zs = tris.Where(t => t.Item == i).Select(t => t.Z).ToList();
                    bounds += FormattableString.Invariant($" z {zs.Min():0.##}..{zs.Max():0.##} d {it.Depth:0.#}");
                }
                if (Environment.GetEnvironmentVariable("MP_XY") == "1") bounds += FormattableString.Invariant($" @{it.X:0.#},{it.Y:0.#}"); // the item's own place
                string what = it.IsText ? $"\"{menu.Strings[it.States[0].Text!.StringId].Replace("\n", "|")}\"" : menu.ItemModelPath(i);
                Console.WriteLine($"  {i,3} {menu.ItemState(i),-8} {MenuStateCode.Name(menu.ItemCode(i)),-14} {bounds,-34} {what}");
            }
        }

        // the rules rows' left arrows (DS touch px): the goal row, TIME LIMIT, TEAM PLAY (0 = none), and how many steps
        // back reach the lowest goal and 3 minutes from the mode's defaults
        private static readonly Dictionary<string, (int GoalY, int TimeY, int TeamY, int GoalBack, int TimeBack)> Rows = new()
        {
            ["battle"] = (84, 105, 126, 6, 4), ["survival"] = (84, 105, 126, 2, 12), ["bounty"] = (76, 97, 139, 2, 12),
            ["defender"] = (84, 105, 126, 5, 12), ["prime hunter"] = (84, 105, 0, 5, 12), ["capture"] = (84, 105, 0, 4, 12),
            ["nodes"] = (84, 105, 126, 30, 12), // the rules step by 1 (time goal by 6 s) since 10-24 (Thread F, F1)
        };

        // arena: step the rules page's ARENA arrows (right) until the arena box reads that name (M5: an imported arena),
        // printing each step's "n/N" and name; MP_SHOTS=1 also saves arena_<name>.png of the rules page there
        private static void Results(string modeName, bool teams, bool quit, int bots, string? arena = null)
        {
            modeName = modeName.Replace('_', ' ').ToLowerInvariant();
            var m = ModeTaps.First(t => t.Name == modeName);
            var rows = Rows[modeName];
            var taps = new List<(int F, int X, int Y)> { (20, 172, 72), (60, LocalDoor.X, LocalDoor.Y), (100, 48, 72), (140, m.X, m.Y) };
            int f0 = 190;
            int arenaFrom = f0, arenaSteps = 0;
            if (arena != null) f0 += 8 * 40; // room for up to 40 steps (the taps below wait until the arena is found)
            // MP_GOALEXTRA=<n>: n more taps past the lowest goal (a press wraps to the top: F1)
            int goalTaps = rows.GoalBack + (Int32.TryParse(Environment.GetEnvironmentVariable("MP_GOALEXTRA"), out int extra) ? extra : 0);
            for (int i = 0; i < goalTaps; i++) taps.Add((f0 += 8, 184, rows.GoalY));
            for (int i = 0; i < rows.TimeBack; i++) taps.Add((f0 += 8, 184, rows.TimeY));
            if (teams && rows.TeamY > 0) taps.Add((f0 += 8, 223, rows.TeamY));
            int ok = f0 + 20;
            taps.AddRange(new[] { (ok, 212, 173), (ok + 40, 26, 134), (ok + 60, 70, 77) });
            for (int i = 0; i < bots; i++) taps.Add((ok + 100 + 20 * i, 223, 170)); // ADD BOT
            taps.AddRange(new[] { (ok + 200, 98, 139), (ok + 210, 154, 63), (ok + 220, 212, 139), (ok + 260, 195, 41) });
            string? setup = null;
            FrontendSession menus = FrontendSession.Load(Paths.FileSystem);
            menus.Log = msg => { if (msg.StartsWith("multiplayer")) Console.WriteLine($"{menus.Frame}: {msg}"); };
            menus.Request = r => { if (r.Kind == FrontendRequestKind.StartMatch) setup = r.Arg; };
            menus.Start(18);
            string ArenaBox() => String.Join(" / ", menus.Menu.Page!.Items.Select((it, i) => (it, i))
                .Where(p => p.it.IsText && p.it.X > 128 && p.it.Y > 128 && p.it.Y < 150 && menus.Menu.ItemState(p.i) != MenuState.Hidden)
                .Select(p => menus.Menu.Strings[p.it.States[0].Text!.StringId]).Where(t => t.Length > 0));
            string Count() => menus.Menu.Page!.Items.Where(it => it.IsText).Select(it => menus.Menu.Strings[it.States[0].Text!.StringId])
                .FirstOrDefault(t => System.Text.RegularExpressions.Regex.IsMatch(t, @"^\d+/\d+$")) ?? "?";
            bool found = arena == null;
            int shotAt = -1;
            for (int f = 0; f <= ok + 400 && setup == null; f++)
            {
                if (!found && f >= arenaFrom && (f - arenaFrom) % 8 == 0)
                {
                    string box = ArenaBox();
                    if (f > arenaFrom) Console.WriteLine($"arena {Count()}: {box}");
                    if (box.StartsWith(arena!, StringComparison.OrdinalIgnoreCase))
                    {
                        found = true;
                        shotAt = f + (Int32.TryParse(Environment.GetEnvironmentVariable("MP_SHOT_DELAY"), out int delay) ? delay : 30);
                        // the rest of the taps run from here
                        int shift = shotAt + 8 - (arenaFrom + 8 * 40);
                        taps = taps.Select(t => t.F >= arenaFrom + 8 * 40 ? (t.F + shift, t.X, t.Y) : t).ToList();
                        ok += shift;
                    }
                    else if (++arenaSteps > 40)
                    {
                        Console.WriteLine($"FAIL arena \"{arena}\" not in the list (page {menus.Menu.Page?.Index})");
                        return;
                    }
                    else
                    {
                        menus.TouchDs(235, 51); // ARENA's right arrow
                    }
                }
                if (f == shotAt && Environment.GetEnvironmentVariable("MP_SHOTS") == "1")
                {
                    string[] size = (Environment.GetEnvironmentVariable("MP_SIZE") ?? "1280x720").Split('x');
                    int w = Int32.Parse(size[0]), h = Int32.Parse(size[1]);
                    menus.Build(w, h);
                    using var img = FrontendTool.RasterCanvas(menus.DrawList, w, h);
                    string path = System.IO.Path.Combine(FrontendTool.OutDir(), $"arena_{arena!.Replace(' ', '_')}.png");
                    img.SaveAsPng(path);
                    Console.WriteLine($"shot {path}");
                }
                foreach (var t in taps.Where(t => t.F == f)) menus.TouchDs(t.X, t.Y);
                menus.Tick();
            }
            if (setup == null)
            {
                Console.WriteLine($"FAIL no START GAME (page {menus.Menu.Page?.Index})");
                return;
            }
            Console.WriteLine("setup: " + setup);
            MatchSettings settings = MatchSetupText.Read(setup);
            // MP_SEATS=N (P8): bots added up to N seats after the menu's setup (the menu seats 4)
            if (Int32.TryParse(Environment.GetEnvironmentVariable("MP_SEATS"), out int seatsWanted))
            {
                MatchSettings more = MatchSettings.Quick(settings.Mode, settings.Arena, MphRead.Hunter.Samus, seatsWanted - 1, 2);
                for (int i = settings.Players.Count; i < seatsWanted && i < more.Players.Count; i++) settings.Players.Add(more.Players[i]);
            }
            settings.Validate();
            CampaignHost host = CampaignHost.StartMatch(settings);
            var input = new CampaignInput { SelectWeapon = MphRead.BeamType.None };
            // MP_FIRE=1: the local player holds fire and turns slowly (kills for the license's live counts)
            bool fire = Environment.GetEnvironmentVariable("MP_FIRE") == "1";
            if (fire)
            {
                settings.PointGoal = 15; // (a longer match: a few kills of its own)
                host.Dispose();
                host = CampaignHost.StartMatch(settings);
                input.Buttons = CampaignButtons.Shoot;
            }
            var clock = System.Diagnostics.Stopwatch.StartNew();
            while (!host.Ended && host.Frame < 60 * 60 * 4)
            {
                if (fire && host.Frame % 4 == 0)
                {
                    // right behind the nearest live opponent, facing it
                    var me = MphRead.Entities.PlayerEntity.Main;
                    var target = MphRead.Entities.PlayerEntity.Players.Take(settings.Players.Count)
                        .Where(p => p != me && p.Health > 0).OrderBy(p => (p.Position - me.Position).LengthSquared).FirstOrDefault();
                    if (target != null && me.Health > 0)
                    {
                        OpenTK.Mathematics.Vector3 d = target.Position - me.Position;
                        d.Y = 0;
                        if (d.LengthSquared > 0.01f) d.Normalize();
                        else d = OpenTK.Mathematics.Vector3.UnitZ;
                        host.PlacePlayer(target.Position - d * 3, d);
                    }
                }
                if (fire) input.Buttons = host.Frame % 6 < 3 ? CampaignButtons.Shoot : CampaignButtons.None; // (taps: a held beam charges)
                host.Step(input);
            }
            if (fire)
            {
                int me = settings.LocalSeat;
                Console.WriteLine($"local player: kills {MphRead.GameState.Kills[me]}, headshots {MphRead.GameState.HeadshotKills[me]}, "
                    + $"alt-form {MphRead.GameState.AltDamageCount[me]}, beams {String.Join(",", Enumerable.Range(0, 9).Select(b => MphRead.GameState.BeamKills[me, b]))}, "
                    + $"deaths {MphRead.GameState.Deaths[me]}");
            }
            Console.WriteLine($"match: ended {host.Ended} after {host.Frame} frames ({host.Frame / 60f:0.0} s, {clock.Elapsed.TotalSeconds:0.0} s real)");
            Console.WriteLine(MatchStatus.Scoreboard());
            if (!host.Ended) return;
            MatchResults.Last = MatchResults.Capture(settings.LocalSeat); // as MatchSession does
            int page = FrontendSession.ReturnPage();
            Console.WriteLine($"return page {page}");
            // (rev 1 moved RESULTS' name column 4 px: the bind once missed it there and every match came back to the lobby)
            if (page != MultiplayerMenu.ResultsPage) Console.WriteLine($"FAIL results: the match returns to page {page}, not RESULTS");
            FrontendSession session = FrontendSession.Load(Paths.FileSystem);
            session.Log = msg => { if (msg.StartsWith("multiplayer") || msg.StartsWith("ui sound") || msg.StartsWith("work")) Console.WriteLine($"{session.Frame}: {msg}"); };
            bool shots = Environment.GetEnvironmentVariable("MP_SHOTS") == "1";
            void Shot(string name)
            {
                if (!shots) return;
                string[] size = (Environment.GetEnvironmentVariable("MP_SIZE") ?? "1280x720").Split('x');
                int w = Int32.Parse(size[0]), h = Int32.Parse(size[1]);
                session.Build(w, h);
                using var img = FrontendTool.RasterCanvas(session.DrawList, w, h);
                string path = System.IO.Path.Combine(FrontendTool.OutDir(), $"results_{name}.png");
                img.SaveAsPng(path);
                Console.WriteLine($"shot {path}");
            }
            session.Start(page);
            for (int f = 0; f < 150; f++)
            {
                session.Build(640, 360);
                session.Tick();
            }
            Shot("settled");
            if (seatsWanted > 4)
            {
                // P8: down shows rows 5-8 (MultiplayerMenu.ResultsRows); up back to 1-4
                session.Navigate(0, -1);
                for (int f = 0; f < 20; f++) session.Tick();
                Shot("rows5to8");
                session.Navigate(0, 1);
                for (int f = 0; f < 20; f++) session.Tick();
            }
            DumpPage(session);
            string Headers()
            {
                MenuEngine menu = session.Menu;
                return String.Join(" | ", new[] { 151f }.SelectMany(y => menu.Page!.Items.Where(it => it.IsText && MathF.Abs(it.Y - y) < 1)
                    .OrderBy(it => it.X).Select(it => menu.Strings[it.States[0].Text!.StringId].Replace("\n", " "))));
            }
            Console.WriteLine("columns: " + Headers());
            for (int r = 0; r < 9; r++)
            {
                session.Press(MenuKeys.R);
                for (int f = 0; f < 10; f++) session.Tick();
                Console.WriteLine($"R -> {Headers()}");
                if (r == 2) Shot("scrolled");
            }
            session.Press(MenuKeys.L);
            for (int f = 0; f < 10; f++) session.Tick();
            Console.WriteLine($"L -> {Headers()}");
            if (quit)
            {
                session.TouchDs(111, 180);
                for (int f = 0; f < 30; f++) session.Tick();
                Shot("quit");
                session.TouchDs(108, 123);
            }
            else
            {
                session.TouchDs(198, 180);
            }
            for (int f = 0; f < 120; f++) session.Tick();
            Console.WriteLine($"after {(quit ? "QUIT + check" : "PLAY AGAIN")}: page {session.Menu.Page?.Index}");
        }

        // ---- -mpmenu lan: a LAN game set up in the menus of two processes (M3) ----
        //
        // This process is the host: main menu -> MULTIPLAYER -> LOCAL PLAY -> CREATE GAME -> Battle (POINT GOAL 1, TIME LIMIT
        // 3) -> Samus -> lobby, which beacons. It starts a second copy of the tool (-mpmenu lanjoin [hunter]) as the joiner:
        // CREATE OR JOIN until the game shows in AVAILABLE GAMES, its row, SELECT HUNTER, the "please wait" lobby. Once the
        // joiner is seated the host taps START GAME; both ask their shells for the match (FrontendRequestKind.StartMatch, the
        // setup line with net=host / net=client), play it over the real LanSession at 60 Hz of wall-clock time (the joiner
        // hunts, the host stands still) to its end, and reopen the menus where the shells do (RESULTS). The menus tick at
        // their 30 Hz on the wall clock too, so the network threads keep up as on a device.
        private static void LanHost(string[] args)
        {
            string hunter = args.Length >= 3 && !args[2].StartsWith("addr") ? args[2] : "Kanden";
            string addr = args.FirstOrDefault(a => a.StartsWith("addr")) is string a0 ? " " + a0 : "";
            bool again = args.Contains("again");
            if (again) addr += " again";
            // bots=N (release 0.2.0 F3): the host adds N bots once the joiner is seated (6 = 8 seats), and both lobbies are
            // shot before START GAME (lan_host_lobby.png / lan_join_wait8.png with MP_SHOTS=1)
            int lanBots = args.FirstOrDefault(a => a.StartsWith("bots=")) is string nb ? Int32.Parse(nb[5..]) : 0;
            if (lanBots > 0) addr += " late";
            string dll = typeof(MultiplayerMenuTool).Assembly.Location;
            // (under dotnet the dll is the first argument; the apphost .exe takes the tool's own)
            bool dotnet = System.IO.Path.GetFileNameWithoutExtension(Environment.ProcessPath!).Equals("dotnet", StringComparison.OrdinalIgnoreCase);
            var psi = new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!, $"{(dotnet ? $"\"{dll}\" " : "")}-mpmenu lanjoin {hunter}{addr}")
            {
                UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
                WorkingDirectory = System.IO.Path.GetDirectoryName(dll)!
            };
            using var joiner = System.Diagnostics.Process.Start(psi)!;
            var joinerOut = new List<string>();
            joiner.OutputDataReceived += (_, e) => { if (e.Data != null) lock (joinerOut) joinerOut.Add(e.Data); };
            joiner.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (joinerOut) joinerOut.Add("stderr: " + e.Data); };
            joiner.BeginOutputReadLine();
            joiner.BeginErrorReadLine();
            try
            {
                var clock = System.Diagnostics.Stopwatch.StartNew();
                string? setup = null;
                bool joined = false;
                FrontendSession menus = FrontendSession.Load(Paths.FileSystem);
                menus.Log = m =>
                {
                    if (m.StartsWith("multiplayer")) Console.WriteLine($"[host {clock.Elapsed.TotalSeconds,5:0.0}] {menus.Frame}: {m}");
                    if (m.Contains("joined, seat")) joined = true;
                };
                menus.Request = r => { if (r.Kind == FrontendRequestKind.StartMatch) setup = r.Arg; };
                menus.Start(18);
                // MULTIPLAYER, LOCAL PLAY, CREATE GAME, BATTLE, POINT GOAL down to 1, TIME LIMIT down to 3, OK, Samus
                var taps = new List<(int F, int X, int Y)> { (20, 172, 72), (60, LocalDoor.X, LocalDoor.Y), (100, 48, 72), (140, 44, 68),
                    (198, 184, 84), (206, 184, 84), (214, 184, 105), (222, 184, 105), (242, 212, 173), (282, 26, 134), (302, 70, 77) };
                int startAt = -1;
                for (int f = 0; setup == null && clock.Elapsed.TotalSeconds < 90; f++)
                {
                    foreach (var t in taps.Where(t => t.F == f)) menus.TouchDs(t.X, t.Y);
                    if (joined && startAt < 0) startAt = f + (lanBots > 0 ? 420 : 120); // (4 s: the joiner's lobby settles)
                    for (int k = 0; k < lanBots && startAt >= 0; k++)
                    {
                        if (f == startAt - 420 + 60 + k * 15) menus.TouchDs(223, 170); // ADD BOT
                    }
                    if (f == startAt)
                    {
                        Console.WriteLine($"[host] page {menus.Menu.Page?.Index}: START GAME");
                        DumpPage(menus);
                        LanShot(menus, "lan_host_lobby");
                        menus.TouchDs(195, 41);
                    }
                    menus.Tick();
                    Pace(clock, f);
                }
                if (setup == null)
                {
                    Console.WriteLine($"[host] FAIL no START GAME (page {menus.Menu.Page?.Index}, joined {joined})");
                    return;
                }
                FrontendSession results = PlayLanMatch("host", setup, clock);
                if (again)
                {
                    // the host comes back later than the joiner (5 s): the joiner waits on its "please wait" lobby
                    string? setup2 = PlayAgain("host", results, Hunter.Samus, delay: 150);
                    if (setup2 != null) PlayLanMatch("host", setup2, System.Diagnostics.Stopwatch.StartNew());
                }
            }
            finally
            {
                if (!joiner.WaitForExit(60000)) joiner.Kill();
                joiner.WaitForExit();
                lock (joinerOut)
                {
                    foreach (string line in joinerOut) Console.WriteLine("  " + line);
                }
            }
        }

        // the join-by-address keypad (61): its keys' DS touch pixels (y down), and OK
        private static readonly Dictionary<char, (int X, int Y)> KeypadKeys = new()
        {
            ['7'] = (94, 46), ['8'] = (128, 46), ['9'] = (162, 46), ['4'] = (94, 75), ['5'] = (128, 75), ['6'] = (162, 75),
            ['1'] = (94, 103), ['2'] = (128, 103), ['3'] = (162, 103), ['0'] = (94, 133), ['.'] = (128, 133), ['<'] = (162, 133),
            ['='] = (220, 125),
        };

        private static void LanJoin(string[] args)
        {
            Hunter hunter = Enum.Parse<Hunter>(args.Length >= 3 ? args[2] : "Kanden", ignoreCase: true);
            // addr: CREATE OR JOIN's "enter address" row and the keypad, typing this PC's address (the host's), not the list
            // (addr=<address>: type that one instead: 127.0.0.1 = a game whose beacon isn't heard there, or one nobody hosts)
            string? addrArg = args.FirstOrDefault(a => a == "addr" || a.StartsWith("addr="));
            bool byAddress = addrArg != null;
            Queue<char>? typing = null;
            var clock = System.Diagnostics.Stopwatch.StartNew();
            string? setup = null;
            FrontendSession menus = FrontendSession.Load(Paths.FileSystem);
            bool seated = false;
            menus.Log = m =>
            {
                if (m.StartsWith("multiplayer")) Console.WriteLine($"[join {clock.Elapsed.TotalSeconds,5:0.0}] {menus.Frame}: {m}");
                if (m.Contains("seated as seat")) seated = true;
            };
            menus.Request = r => { if (r.Kind == FrontendRequestKind.StartMatch) setup = r.Arg; };
            // addr: a fresh profile, to see the typed address kept (hunter_license.json) for the keypad next time
            string? profile = byAddress ? System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"mpmenu_join_{Environment.ProcessId}") : null;
            if (profile != null) menus.Multiplayer.ProfileDir = profile;
            menus.Start(MultiplayerMenu.CreateJoinPage);
            // the hunter icons on SELECT HUNTER (DS touch px, icon order), then the big portrait
            int icon = Array.IndexOf(new[] { Hunter.Samus, Hunter.Kanden, Hunter.Spire, Hunter.Trace, Hunter.Noxus, Hunter.Sylux,
                Hunter.Weavel }, hunter);
            int rowAt = -1, step = 0;
            for (int f = 0; setup == null && clock.Elapsed.TotalSeconds < 90; f++)
            {
                MenuEngine menu = menus.Menu;
                int page = menu.Page?.Index ?? -1;
                if (byAddress && step == 0 && page == MultiplayerMenu.CreateJoinPage && menu.PendingPage == -1 && f >= 450) // (15 s: hosting)
                {
                    LanShot(menus, "lan_addr_row");
                    menus.TouchDs(198, 99); // the last row, "enter address" (one tap: the ROM's 4th row doesn't wait for a second)
                    step = 10;
                }
                else if (step == 10 && page == MultiplayerMenu.AddressPage && menu.PendingPage == -1)
                {
                    // clear what it starts with, type the address, OK
                    string address = addrArg!.StartsWith("addr=") ? addrArg[5..] : MultiplayerMenu.LocalAddresses().First();
                    typing = new Queue<char>(new string('<', 16) + address + "=");
                    Console.WriteLine($"[join] keypad: typing {address}");
                    rowAt = f + 20;
                    step = 11;
                }
                else if (step == 11 && f >= rowAt && (f - rowAt) % 4 == 0 && typing!.Count > 0)
                {
                    char key = typing.Dequeue();
                    if (key == '=')
                    {
                        Console.WriteLine("[join] keypad before OK:");
                        DumpPage(menus);
                        LanShot(menus, "lan_addr_keypad");
                        step = 1;
                    }
                    menus.TouchDs(KeypadKeys[key].X, KeypadKeys[key].Y);
                }
                else if (!byAddress && step == 0 && page == MultiplayerMenu.CreateJoinPage && menu.PendingPage == -1)
                {
                    // the first AVAILABLE GAMES row once it has come in (its red bar; not the "enter address" row)
                    int bar = Enumerable.Range(0, menu.Page!.Items.Count).FirstOrDefault(i => menu.ItemModelPath(i)
                        .EndsWith("redbar_Model.bin", StringComparison.OrdinalIgnoreCase) && menu.ItemState(i) != MenuState.Hidden
                        && menu.Page.Items[i].Actions.Any(a => a.Calls.Any(c => c.B == 95)), -1);
                    if (bar >= 0 && rowAt < 0)
                    {
                        rowAt = f + 20;
                        Console.WriteLine($"[join] the game is listed (row bar {bar}); the page:");
                        DumpPage(menus);
                        LanShot(menus, "lan_join_list");
                    }
                    if (rowAt >= 0 && f == rowAt) menus.TouchDs(198, 56); // the row: its red bar (focus)
                    if (rowAt >= 0 && f == rowAt + 15)
                    {
                        Console.WriteLine($"[join] row tapped: focus {menu.FocusedItem()}; tapped again");
                        LanShot(menus, "lan_join_row");
                        menus.TouchDs(198, 56); // the second tap on the row joins (its action waits for one, as a save file)
                        step = 1;
                    }
                }
                else if (step == 1 && page == MultiplayerMenu.JoinHunterPage && menu.PendingPage == -1)
                {
                    rowAt = f + 30;
                    step = 2;
                }
                else if (step == 2 && f == rowAt)
                {
                    menus.TouchDs(MultiplayerMenu.HunterIconTap(icon).X, MultiplayerMenu.HunterIconTap(icon).Y);
                    rowAt = f + 20;
                    step = 3;
                }
                else if (step == 3 && f == rowAt)
                {
                    menus.TouchDs(70, 77);
                    step = 4;
                }
                else if (step == 4 && page == MultiplayerMenu.JoinLobbyPage && seated && menu.PendingPage == -1)
                {
                    rowAt = f + 45; // the seats come in
                    step = 5;
                }
                else if (step == 5 && f == rowAt && page == MultiplayerMenu.JoinLobbyPage && menu.PendingPage == -1)
                {
                    Console.WriteLine($"[join] waiting on page {page}");
                    DumpPage(menus);
                    LanShot(menus, "lan_join_wait");
                    if (args.Contains("late")) { rowAt = f + 270; step = 6; } // (the host's bots=N: its bots come in)
                }
                else if (step == 6 && f == rowAt && page == MultiplayerMenu.JoinLobbyPage)
                {
                    Console.WriteLine($"[join] waiting on page {page}, the host's bots in");
                    DumpPage(menus);
                    LanShot(menus, "lan_join_wait8");
                }
                menus.Tick();
                Pace(clock, f);
            }
            if (setup == null)
            {
                Console.WriteLine($"[join] FAIL no match (page {menus.Menu.Page?.Index}, step {step})");
                MphRecomp.Multiplayer.Net.LanSession.Current?.Dispose();
                return;
            }
            if (profile != null)
            {
                string kept = HunterLicense.Load(profile).LastAddress;
                string typed = addrArg!.StartsWith("addr=") ? addrArg[5..] : MultiplayerMenu.LocalAddresses().First();
                Console.WriteLine($"[join] profile: last address \"{kept}\" {(kept == typed ? "PASS kept" : "FAIL not kept")}");
                try { System.IO.Directory.Delete(profile, recursive: true); } catch (System.IO.IOException) { }
            }
            FrontendSession results = PlayLanMatch("join", setup, clock);
            if (args.Contains("again"))
            {
                string? setup2 = PlayAgain("join", results, hunter, delay: 0);
                if (setup2 != null) PlayLanMatch("join", setup2, System.Diagnostics.Stopwatch.StartNew());
            }
        }

        // RESULTS' PLAY AGAIN after a LAN match (owner report R2, 2026-10-07: "guest is kicked out of lobby after match"): the
        // host through SELECT HUNTER (35) back to its lobby, the joiner to SELECT HUNTER (36) and the "please wait" lobby of
        // the SAME host (not the list), seated once the host's lobby is back; the host's START GAME starts both again.
        // Returns the second match's setup line, or null (FAIL printed)
        private static string? PlayAgain(string who, FrontendSession menus, Hunter hunter, int delay)
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            string? setup = null;
            bool joined = false, seated = false;
            menus.Request = r => { if (r.Kind == FrontendRequestKind.StartMatch) setup = r.Arg; };
            menus.Log = m =>
            {
                if (m.StartsWith("multiplayer")) Console.WriteLine($"[{who} again {clock.Elapsed.TotalSeconds,5:0.0}] {menus.Frame}: {m}");
                if (m.Contains("joined, seat")) joined = true;
                if (m.Contains("seated as seat")) seated = true;
            };
            int icon = Array.IndexOf(new[] { Hunter.Samus, Hunter.Kanden, Hunter.Spire, Hunter.Trace, Hunter.Noxus, Hunter.Sylux,
                Hunter.Weavel }, hunter);
            int hunterPage = who == "host" ? MultiplayerMenu.HunterPage : MultiplayerMenu.JoinHunterPage;
            int at = delay, step = 0, startAt = -1;
            for (int f = 0; setup == null && clock.Elapsed.TotalSeconds < 120; f++)
            {
                MenuEngine menu = menus.Menu;
                int page = menu.Page?.Index ?? -1;
                bool settled = menu.PendingPage == -1;
                if (step == 0 && f >= at && page == MultiplayerMenu.ResultsPage && settled)
                {
                    Console.WriteLine($"[{who}] PLAY AGAIN");
                    if (!TapCall(menus, 196)) Console.WriteLine($"[{who}] FAIL no PLAY AGAIN on RESULTS");
                    step = 1;
                }
                else if (step == 1 && page == hunterPage && settled)
                {
                    Console.WriteLine($"[{who}] PASS PLAY AGAIN -> SELECT HUNTER (page {page})");
                    at = f + 30;
                    step = 2;
                }
                else if (step == 1 && page == MultiplayerMenu.CreateJoinPage)
                {
                    Console.WriteLine($"[{who}] FAIL PLAY AGAIN went to the list (page {page})");
                    return null;
                }
                else if (step == 2 && f == at)
                {
                    menus.TouchDs(MultiplayerMenu.HunterIconTap(icon).X, MultiplayerMenu.HunterIconTap(icon).Y);
                    at = f + 20;
                    step = 3;
                }
                else if (step == 3 && f == at)
                {
                    menus.TouchDs(70, 77);
                    step = 4;
                }
                else if (step == 4 && who == "host" && joined && startAt < 0)
                {
                    Console.WriteLine($"[host] PASS the joiner is back in the lobby (page {page})");
                    startAt = f + 120; // (4 s: the joiner's lobby settles)
                }
                else if (step == 4 && who == "join" && seated)
                {
                    Console.WriteLine($"[join] PASS seated again in the same host's lobby (page {page})");
                    step = 5;
                }
                if (f == startAt)
                {
                    Console.WriteLine($"[host] page {page}: START GAME again");
                    DumpPage(menus);
                    LanShot(menus, "lan_host_lobby_again");
                    menus.TouchDs(195, 41);
                }
                menus.Build(640, 360);
                menus.Tick();
                Pace(clock, f);
            }
            if (setup == null) Console.WriteLine($"[{who}] FAIL no second match (page {menus.Menu.Page?.Index}, step {step}, joined {joined}, seated {seated})");
            else Console.WriteLine($"[{who}] PASS second match starting");
            return setup;
        }

        // a tap on the visible item whose touch action makes `call`
        private static bool TapCall(FrontendSession s, int call)
        {
            MenuPage page = s.Menu.Page!;
            for (int i = page.Items.Count - 1; i >= 0; i--)
            {
                if (s.Menu.ItemState(i) is MenuState.Hidden or MenuState.State5) continue;
                foreach (MenuAction act in page.Items[i].Actions)
                {
                    if (!act.Calls.Any(c => c.B == call)) continue;
                    if (!MenuEngine.TryRect(act, page.Items[i], out float x0, out float y0, out float x1, out float y1)) continue;
                    s.TouchDs((x0 + x1) / 2, 192 - (y0 + y1) / 2);
                    return true;
                }
            }
            return false;
        }

        // the menus' 30 Hz on the wall clock
        private static void Pace(System.Diagnostics.Stopwatch clock, int frame)
        {
            double due = (frame + 1) / 30.0;
            double wait = due - clock.Elapsed.TotalSeconds;
            if (wait > 0) System.Threading.Thread.Sleep(TimeSpan.FromSeconds(wait));
        }

        // as the shells and the match screen do with a menu's LAN setup line (MatchSetupText.LaunchArgs -> MatchSession):
        // the match from LanSession.Current, MatchNetDriver at 60 Hz, then RESULTS from the reopened menus
        private static FrontendSession PlayLanMatch(string who, string setup, System.Diagnostics.Stopwatch clock)
        {
            Dictionary<string, string> launch = MatchSetupText.LaunchArgs(setup);
            Console.WriteLine($"[{who}] launch args: {string.Join(" ", launch.Select(kv => kv.Key + "=" + kv.Value))}");
            var lan = MphRecomp.Multiplayer.Net.LanSession.Current;
            if (!launch.TryGetValue("net", out string? net) || lan?.Settings == null)
            {
                Console.WriteLine($"[{who}] FAIL not a LAN match (net {net}, session {lan != null})");
                return FrontendSession.Load(Paths.FileSystem);
            }
            MatchSettings settings = lan.Settings;
            Console.WriteLine($"[{who}] match {settings.Mode} in {settings.Arena}, local seat {settings.LocalSeat}: "
                + string.Join(", ", settings.Players.Select((p, i) => $"{i}:{p.Hunter} {p.Nickname}{(p.Remote ? " remote" : "")}{(p.Bot ? " bot" : "")}")));
            CampaignHost host = CampaignHost.StartMatch(settings);
            var driver = new MatchNetDriver(lan, host);
            var match = System.Diagnostics.Stopwatch.StartNew();
            long frames = 0;
            while (!host.Ended && match.Elapsed.TotalSeconds < 200)
            {
                long due = (long)(match.Elapsed.TotalSeconds * 60);
                while (frames < due && !host.Ended)
                {
                    driver.Step(who == "join" ? Hunt(host) : new CampaignInput { SelectWeapon = BeamType.None });
                    frames++;
                }
                System.Threading.Thread.Sleep(1);
            }
            Console.WriteLine($"[{who}] match over after {host.Frame} frames ({match.Elapsed.TotalSeconds:0.0} s): ended {host.Ended}, "
                + driver.StatusLine());
            Console.WriteLine(MatchStatus.Scoreboard());
            // MatchSession's menu path: the results, the session closed, the menus at ReturnPage
            MatchResults.Last = MatchResults.Capture(settings.LocalSeat);
            lan.Dispose();
            host.Dispose();
            int page = FrontendSession.ReturnPage();
            FrontendSession menus = FrontendSession.Load(Paths.FileSystem);
            menus.Log = m => { if (m.StartsWith("multiplayer")) Console.WriteLine($"[{who}] {m}"); };
            menus.Start(page);
            for (int f = 0; f < 150; f++)
            {
                menus.Build(640, 360);
                menus.Tick();
            }
            Console.WriteLine($"[{who}] back in the menus on page {page}:");
            DumpPage(menus);
            LanShot(menus, $"lan_{who}_results");
            return menus;
        }

        // MP_SHOTS=1: the menus at MP_SIZE (default 1280x720) into extract_out/frontend/<name>.png
        private static void LanShot(FrontendSession session, string name)
        {
            if (Environment.GetEnvironmentVariable("MP_SHOTS") != "1") return;
            string[] size = (Environment.GetEnvironmentVariable("MP_SIZE") ?? "1280x720").Split('x');
            int w = Int32.Parse(size[0]), h = Int32.Parse(size[1]);
            session.Build(w, h);
            using var img = FrontendTool.RasterCanvas(session.DrawList, w, h);
            string path = System.IO.Path.Combine(FrontendTool.OutDir(), (Environment.GetEnvironmentVariable("MP_TEAMS") == "1" ? "teams_" : "") + name + ".png");
            img.SaveAsPng(path);
        }

        // the joiner's player: turn to the nearest opponent, walk at it and fire (FIRE also respawns)
        private static CampaignInput Hunt(CampaignHost host)
        {
            var input = new CampaignInput { SelectWeapon = BeamType.None };
            MphRead.Entities.PlayerEntity me = host.Player;
            if (me.Health == 0)
            {
                input.Buttons = CampaignButtons.Shoot;
                return input;
            }
            MphRead.Entities.PlayerEntity? target = null;
            float best = Single.MaxValue;
            for (int i = 0; i < MphRead.Entities.PlayerEntity.MaxPlayers; i++)
            {
                MphRead.Entities.PlayerEntity p = MphRead.Entities.PlayerEntity.Players[i];
                if (p == me || !p.LoadFlags.TestFlag(MphRead.Entities.LoadFlags.Active) || p.Health == 0) continue;
                float d = (p.Position - me.Position).LengthSquared;
                if (d < best)
                {
                    best = d;
                    target = p;
                }
            }
            if (target == null) return input;
            OpenTK.Mathematics.Vector3 to = target.Position - me.Position;
            float yaw = OpenTK.Mathematics.MathHelper.RadiansToDegrees(MathF.Atan2(to.X, to.Z));
            float pitch = Math.Clamp(OpenTK.Mathematics.MathHelper.RadiansToDegrees(MathF.Atan2(to.Y, MathF.Sqrt(to.X * to.X + to.Z * to.Z))), -80, 80);
            float dyaw = yaw - me.NetYaw;
            dyaw -= 360 * MathF.Floor((dyaw + 180) / 360);
            float dpitch = pitch - me.NetPitch;
            input.AimDelta = new System.Numerics.Vector2(-4 * Math.Clamp(dyaw, -12, 12), -4 * Math.Clamp(dpitch, -6, 6));
            input.Move = new System.Numerics.Vector2(0, 1);
            if (MathF.Abs(dyaw) < 10 && host.Frame % 20 < 12) input.Buttons = CampaignButtons.Shoot;
            return input;
        }

        // -mpmenu icons [dir/name ...]: the menu models' textures as PNGs (extract_out/frontend/icons), for the HUNTER LICENSE
        // door picture (owner 2026-10-07: a new picture in the other doors' sepia style). Default: the doors' pictures and
        // the license card's pieces.
        private static void Icons(string[] names)
        {
            if (names.Length == 0)
            {
                names = new[] { "multiplayer/singlecart", "multiplayer/multiplayer", "multiplayer/wifi", "wifi_createjoin/wififriend",
                    "multiplayer/friendconfig", "multiplayer/wificonfig", "multiplayer/rivalradar" }
                    .Concat(new[] { "alimbicswirl", "goldstar", "silverstar", "bronzestar", "redstar", "blackstar", "goldmedal",
                        "octolith", "cardbgUS", "cardheadergold", "frame200gold", "samusmost", "kandenmost", "tracemost", "syluxmost",
                        "noxmost", "spiremost", "weavelmost", "lricons" }.Select(n => "gamerscard/" + n)).ToArray();
            }
            string outDir = System.IO.Path.Combine(FrontendTool.OutDir(), "icons");
            System.IO.Directory.CreateDirectory(outDir);
            foreach (string full in names)
            {
                string[] parts = full.Split('/');
                MetaDir dir = parts[0] switch
                {
                    "multiplayer" => MetaDir.Multiplayer, "wifi_createjoin" => MetaDir.WifiCreate, "gamerscard" => MetaDir.GamersCard,
                    _ => MetaDir.MainMenu
                };
                Model model;
                try { model = Read.GetModelInstance(parts[1], dir: dir, noCache: true).Model; }
                catch (Exception ex) { Console.WriteLine($"{full}: {ex.Message}"); continue; }
                Recolor recolor = model.Recolors[0];
                var done = new HashSet<(int, int)>();
                foreach (Material m in model.Materials)
                {
                    if (m.TextureId < 0 || !done.Add((m.TextureId, m.PaletteId))) continue;
                    Texture tex = recolor.Textures[m.TextureId];
                    IReadOnlyList<ColorRgba> px = recolor.GetPixels(m.TextureId, m.PaletteId);
                    if (tex.Width == 0 || px.Count != tex.Width * tex.Height) continue;
                    using var img = new SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgba32>(tex.Width, tex.Height);
                    for (int i = 0; i < px.Count; i++)
                    {
                        img[i % tex.Width, i / tex.Width] = new SixLabors.ImageSharp.PixelFormats.Rgba32(px[i].Red, px[i].Green, px[i].Blue, px[i].Alpha);
                    }
                    string path = System.IO.Path.Combine(outDir, $"{parts[0]}_{parts[1]}_{m.TextureId}-{m.PaletteId}.png");
                    img.SaveAsPng(path);
                    Console.WriteLine($"{full}: material {m.Name} {tex.Width}x{tex.Height} -> {path}");
                }
            }
        }

        // -mpmenu doorshot: CHOOSE MULTIPLAYER TYPE at 1280x720 (extract_out/frontend): doors_license.png as it is, then
        // doors_empty.png with the HUNTER LICENSE door's picture moved off the screen (its ring of focus stays), the
        // background for mockups of a new door picture.
        private static void DoorShot()
        {
            string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"mpmenu_doors_{Environment.ProcessId}");
            if (System.IO.Directory.Exists(dir)) System.IO.Directory.Delete(dir, recursive: true);
            FrontendSession s = FrontendSession.Load(Paths.FileSystem);
            s.Multiplayer.ProfileDir = dir;
            void Run(int frames)
            {
                for (int i = 0; i < frames; i++)
                {
                    s.Build(640, 360);
                    s.Tick();
                }
            }
            void Shot(string name)
            {
                s.Build(1280, 720);
                using var img = FrontendTool.RasterCanvas(s.DrawList, 1280, 720);
                string path = System.IO.Path.Combine(FrontendTool.OutDir(), $"{name}.png");
                img.SaveAsPng(path);
                Console.WriteLine($"shot {path}");
            }
            s.Start(18);
            Run(30);
            s.TouchDs(172, 72); // MULTIPLAYER
            Run(60);
            s.Press(MenuKeys.A); // the first visit's notice
            Run(90);
            Console.WriteLine($"page {s.Menu.Page?.Index}");
            Shot("doors_license");
            MenuPage page = s.Menu.Page!;
            MenuFile file = s.Menu.File;
            for (int i = 0; i < page.Items.Count; i++)
            {
                MenuItem it = page.Items[i];
                if (!it.States.Any(st => st.WidgetIndex >= 0 && file.Widgets[st.WidgetIndex].ModelPath
                    .EndsWith("\\singlecart_Model.bin", StringComparison.OrdinalIgnoreCase))) continue;
                page.ReplaceItem(i, n => RecompMenus.Clone(it, n, 2000, 0, links: it.Links, actions: it.Actions));
                Console.WriteLine($"moved item {i} off the screen");
            }
            Run(2);
            Shot("doors_empty");
        }

        // the modes' icons on the mode page (DS touch px) and whether the rules page has a TEAM PLAY row
        // the LOCAL PLAY door on CHOOSE MULTIPLAYER TYPE (DS touch coords): the left spot since the owner's door order
        // (2026-10-07 23:19, MultiplayerMenu.MoveDoors: LOCAL / ONLINE / HUNTER LICENSE; it was the middle spot, 128,72)
        private static readonly (int X, int Y) LocalDoor = (48, 72);

        private static readonly (string Name, int X, int Y, bool Teams)[] ModeTaps =
        {
            ("battle", 44, 68, true), ("survival", 113, 54, true), ("bounty", 167, 54, true), ("defender", 221, 54, true),
            ("prime hunter", 113, 113, false), ("capture", 167, 113, false), ("nodes", 221, 112, true)
        };

        private static void Check()
        {
            int pass = 0, total = 0;
            foreach (var m in ModeTaps)
            {
                foreach (bool teams in m.Teams ? new[] { false, true } : new[] { false })
                {
                    total++;
                    // TEAM PLAY is the rules page's last row (Bounty: the 4th, y 139; the rest: the 3rd, y 126)
                    string team = !teams ? "" : m.Name == "bounty" ? ",200:ds@223/139" : ",200:ds@223/126";
                    string script = $"20:ds@172/72,60:ds@{LocalDoor.X}/{LocalDoor.Y},100:ds@48/72,140:ds@{m.X}/{m.Y}{team},240:ds@212/173,"
                        + "280:ds@26/134,300:ds@70/77,340:ds@223/170,360:ds@223/170,380:ds@223/170,440:ds@98/139,450:ds@154/63,"
                        + "460:ds@212/139,500:ds@195/41";
                    string? setup = null;
                    FrontendSession session = FrontendSession.Load(Paths.FileSystem);
                    session.Request = r => { if (r.Kind == FrontendRequestKind.StartMatch) setup = r.Arg; };
                    session.Start(18);
                    var steps = script.Split(',').Select(t => t.Split(':')).Select(p => (F: Int32.Parse(p[0]), In: p[1][3..].Split('/'))).ToList();
                    for (int f = 0; f <= 620 && setup == null; f++)
                    {
                        foreach (var st in steps.Where(t => t.F == f)) session.TouchDs(Single.Parse(st.In[0]), Single.Parse(st.In[1]));
                        session.Tick();
                    }
                    string label = m.Name + (teams ? " + team play" : "");
                    if (setup == null)
                    {
                        Console.WriteLine($"FAIL {label,-26} no START GAME (page {session.Menu.Page?.Index})");
                        continue;
                    }
                    try
                    {
                        MatchSettings settings = MatchSetupText.Read(setup);
                        settings.Validate();
                        CampaignHost host = CampaignHost.StartMatch(settings);
                        var input = new CampaignInput { SelectWeapon = MphRead.BeamType.None };
                        for (int f = 0; f < 600 && !host.Ended; f++) host.Step(input);
                        Console.WriteLine($"PASS {label,-26} {settings.Mode} in {settings.Arena}, {settings.Players.Count} seats, "
                            + $"pg {settings.PointGoal} tl {settings.TimeLimitSeconds} tg {settings.TimeGoalSeconds} | {host.Frame} f");
                        pass++;
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"FAIL {label,-26} {ex.GetType().Name}: {ex.Message} | {setup}");
                    }
                }
            }
            Console.WriteLine($"{pass}/{total} pass");
            // every arena the game's own grid offers per mode against our entity census (MatchArenas.Supports)
            int[] rooms = { 95, 93, 100, 97, 102, 113, 105, 110, 111, 104, 98, 103, 109, 107, 94, 101, 112, 96, 106, 108, 119, 99, 115, 114, 116, 117, 118 };
            string[] grid = { "1001111", "1011111", "1001111", "1000111", "1011111", "1011111", "1011111", "0111000", "1000111", "1001111",
                "1000111", "1000111", "1001111", "1011111", "1011111", "1011111", "1111111", "1110111", "1000111", "1110011",
                "1011111", "1010111", "1001111", "1001111", "1011111", "1011111", "1001111" };
            MphRead.GameMode[] modes = { MphRead.GameMode.Battle, MphRead.GameMode.Bounty, MphRead.GameMode.Capture, MphRead.GameMode.Defender,
                MphRead.GameMode.Nodes, MphRead.GameMode.PrimeHunter, MphRead.GameMode.Survival };
            int offered = 0, bad = 0;
            for (int a = 0; a < rooms.Length; a++)
            {
                RoomMetadata room = Metadata.GetRoomById(rooms[a])!;
                for (int c = 0; c < modes.Length; c++)
                {
                    if (grid[a][c] != '1') continue;
                    offered++;
                    if (!MatchArenas.Supports(room, modes[c]))
                    {
                        bad++;
                        Console.WriteLine($"census says no: {modes[c]} in {room.Name} ({room.InGameName})");
                    }
                }
            }
            Console.WriteLine($"arena grid: {offered - bad}/{offered} offered pairs pass our census");
        }
    }
}
