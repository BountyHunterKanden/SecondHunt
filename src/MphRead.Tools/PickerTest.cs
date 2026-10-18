using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MphRecomp.Frontend;
using MphRecomp.Multiplayer;
using SixLabors.ImageSharp;

// -pickertest: the D9 character + skin picker (docs/CHARACTERS_AND_SKINS.md) in the real menus, on a new cart's profile in
// a temp folder with the PC's HD models (brawl_extract/Converted) as the device's hd/: SELECT HUNTER's nine icons,
// TROOPER's info box and skin chooser, a pick into the lobby, the lobby badge's chooser for this player and a bot, the
// arrows over all nine, D7's bot names and START GAME's setup; then the same page with no HD models (TROOPER and DARK
// SAMUS greyed, Samus straight to the lobby). MP_SHOTS=1: picker_*.png (extract_out/frontend) at MP_SIZE (default
// 1920x1080), the pictures taken from the mockup's renders (testdata/picker_renders).
namespace MphRead
{
    public static class PickerTest
    {
        private static int s_fails;

        private static void Check(bool ok, string what)
        {
            Console.WriteLine($"{(ok ? "PASS" : "FAIL")} {what}");
            if (!ok) s_fails++;
        }

        public static void Run(string[] args)
        {
            if (!ToolPaths.UseDesktopExtraction()) return;
            string? hd = FindUp(AppContext.BaseDirectory, Path.Combine("brawl_extract", "Converted"));
            if (hd == null)
            {
                Console.WriteLine("FAIL brawl_extract/Converted not found");
                Environment.ExitCode = 1;
                return;
            }
            string dir = Path.Combine(Path.GetTempPath(), $"pickertest_{Environment.ProcessId}");
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
            string pictures = Path.Combine(dir, "pictures");
            Directory.CreateDirectory(pictures);
            if (Environment.GetEnvironmentVariable("MP_SHOTS") == "1") FillPictures(hd, pictures);
            MultiplayerMenu.CharacterHdRoot = hd;
            MultiplayerMenu.CharacterPicturesDir = pictures;
            WithModels(dir);
            MenuArt(dir);
            // the lobby pictures' fades (owner 2026-10-08 22:30), with pictures to draw
            string fadePictures = Path.Combine(dir, "fade_pictures");
            Directory.CreateDirectory(fadePictures);
            FillPictures(hd, fadePictures);
            MultiplayerMenu.CharacterPicturesDir = fadePictures;
            CharacterPictures.Invalidate(); // (the skins' pictures were looked up in the empty folder)
            Fades(dir);
            Reload(dir);
            MultiplayerMenu.CharacterPicturesDir = pictures;
            // no HD models on this device
            string empty = Path.Combine(dir, "empty_hd");
            Directory.CreateDirectory(empty);
            MultiplayerMenu.CharacterHdRoot = empty;
            WithoutModels(dir);
            // pictures drawn ahead: at the menus' start, and again when an import ends (a device shell renders them)
            MultiplayerMenu.CharacterHdRoot = hd;
            MultiplayerMenu.CharacterPicturesDir = Path.Combine(dir, "warm_pictures");
            Warm(dir);
            MultiplayerMenu.CharacterHdRoot = null;
            MultiplayerMenu.CharacterPicturesDir = null;
            Console.WriteLine(s_fails == 0 ? "PASS pickertest" : $"FAIL pickertest ({s_fails} failed)");
            if (s_fails > 0) Environment.ExitCode = 1;
        }

        private sealed class Harness
        {
            public readonly FrontendSession S;
            public readonly List<string> Log = new();
            public string? Setup;

            public Harness(string dir, string name)
            {
                string profile = Path.Combine(dir, name);
                Directory.CreateDirectory(profile);
                S = FrontendSession.Load(Paths.FileSystem, settingsPath: Path.Combine(profile, "recomp_settings.json"),
                    savesDir: Path.Combine(profile, "saves"));
                S.Log = m =>
                {
                    if (!m.StartsWith("multiplayer")) return;
                    Log.Add(m);
                    Console.WriteLine($"{S.Frame}: {m}");
                };
                S.Request = r => { if (r.Kind == FrontendRequestKind.StartMatch) Setup = r.Arg; };
            }

            public void Run(int frames)
            {
                for (int i = 0; i < frames; i++)
                {
                    S.Build(640, 360);
                    S.Tick();
                }
            }

            public void Tap(float x, float y, int wait = 20)
            {
                S.TouchDs(x, y);
                Run(wait);
            }

            public int Page => S.Menu.Page?.Index ?? -1;

            // the page's visible texts
            public List<string> Texts()
            {
                MenuPage page = S.Menu.Page!;
                return Enumerable.Range(0, page.Items.Count).Where(i => page.Items[i].IsText && S.Menu.ItemState(i) != MenuState.Hidden
                    && page.Items[i].States.Count > 0 && page.Items[i].States[0].Text != null)
                    .Select(i => S.Menu.Strings[page.Items[i].States[0].Text!.StringId]).ToList();
            }

            public bool Shown(string widget) => Enumerable.Range(0, S.Menu.Page!.Items.Count).Any(i =>
                S.Menu.ItemModelPath(i).EndsWith("\\" + widget + "_Model.bin", StringComparison.OrdinalIgnoreCase) && S.Menu.ItemState(i) != MenuState.Hidden);

            public int CountShown(string widget) => Enumerable.Range(0, S.Menu.Page!.Items.Count).Count(i =>
                S.Menu.ItemModelPath(i).EndsWith("\\" + widget + "_Model.bin", StringComparison.OrdinalIgnoreCase) && S.Menu.ItemState(i) != MenuState.Hidden);

            // a tap on the page's item whose action makes this call
            public bool TapCall(int call, int wait = 10)
            {
                MenuPage page = S.Menu.Page!;
                for (int i = 0; i < page.Items.Count; i++)
                {
                    foreach (MenuAction a in page.Items[i].Actions)
                    {
                        if (!a.Calls.Any(c => c.B == call) || !MenuEngine.TryRect(a, page.Items[i], out float x0, out float y0, out float x1, out float y1)) continue;
                        Tap((x0 + x1) / 2, 192 - (y0 + y1) / 2, wait);
                        return true;
                    }
                }
                return false;
            }

            // MULTIPLAYER -> LOCAL PLAY -> CREATE GAME -> Battle -> SELECT HUNTER
            public void ToSelectHunter()
            {
                S.Start(18);
                Run(30);
                Tap(172, 72, 60);
                S.Press(MenuKeys.A); // (the name notice of a new profile)
                Run(30);
                foreach ((int x, int y) in new[] { (48, 72), (48, 72), (44, 68) }) Tap(x, y, 40);
                Tap(212, 173, 60);
            }

            // the lobby seat (1-3, not that one) whose bot arrows show (the lobby's arrows: 174 + 3 * seat left, +1 right)
            public int BotSeatShowingArrows(int not)
            {
                MenuPage page = S.Menu.Page!;
                for (int seat = 1; seat < 4; seat++)
                {
                    if (seat == not) continue;
                    int call = BotRight(seat);
                    for (int i = 0; i < page.Items.Count; i++)
                    {
                        if (S.Menu.ItemState(i) != MenuState.Hidden && page.Items[i].Actions.Any(a => a.RectW != 0 && a.Calls.Any(c => c.B == call))) return seat;
                    }
                }
                return -1;
            }

            public void Shot(string name)
            {
                if (Environment.GetEnvironmentVariable("MP_SHOTS") != "1") return;
                (int w, int h) = (1920, 1080);
                string[]? size = Environment.GetEnvironmentVariable("MP_SIZE")?.Split('x');
                if (size?.Length == 2 && Int32.TryParse(size[0], out int sw) && Int32.TryParse(size[1], out int sh)) (w, h) = (sw, sh);
                S.Build(w, h);
                using var img = FrontendTool.RasterCanvas(S.DrawList, w, h);
                string path = Path.Combine(FrontendTool.OutDir(), $"picker_{name}.png");
                img.SaveAsPng(path);
                Console.WriteLine($"shot {path}");
                S.Build(640, 360);
            }
        }

        // RECOMP SETTINGS "menu art" (owner 2026-10-13): A on its row opens the skin chooser over the page -- Samus's skins
        // (ORIGINAL first) and SHUFFLE last -- and a pick sets MenuArtHd / MenuArtSuit and bumps the backdrop's version
        private static void MenuArt(string dir)
        {
            var h = new Harness(dir, "menuart");
            h.S.Start(18);
            h.Run(30);
            h.S.Menu.GoTo(h.S.Recomp.SettingsPage);
            h.Run(40);
            for (int i = 0; i < 30 && h.S.Recomp.FocusedSetting(h.S.Menu) != "menu\nart"; i++)
            {
                h.S.Navigate(0, -1);
                h.Run(4);
            }
            Check(h.S.Recomp.FocusedSetting(h.S.Menu) == "menu\nart", $"menu art row focused ({h.S.Recomp.FocusedSetting(h.S.Menu)?.Replace('\n', ' ')})");
            MphRecomp.Config.RecompSettings set = h.S.Recomp.Settings;
            void Open()
            {
                h.S.Press(MenuKeys.A);
                h.Run(10);
            }
            Open();
            var probe = h.S.Multiplayer.ChooserProbe;
            Check(probe is { Title: "menu art" }, $"A opens the menu art chooser ({probe?.Title})");
            if (probe is not { } p) return;
            Check(p.Skins[0] == Characters.Original && p.Skins[^1] == MultiplayerMenu.ShuffleChoice && p.Skins.Count > 3,
                $"ORIGINAL first, SHUFFLE last ({string.Join(" ", p.Skins)})");
            // nothing picked yet: on the suit the recipe's order shows (Echoes' Varia first)
            Check(p.Skins[p.Focus] == "MP2VariaSuit", $"on the recipe's first suit before a pick ({p.Skins[p.Focus]})");
            h.Shot("menu_art");
            int next = p.Focus + 1;
            h.S.Navigate(1, 0);
            h.Run(4);
            h.Shot("menu_art_focus");
            h.S.Press(MenuKeys.A);
            h.Run(10);
            Check(h.S.Multiplayer.ChooserProbe == null && set.MenuArtHd && set.MenuArtSuit == p.Skins[next] && h.S.MenuArtVersion == 1,
                $"A picks {p.Skins[next]} (hd {set.MenuArtHd}, suit '{set.MenuArtSuit}', version {h.S.MenuArtVersion})");
            Open();
            Check(h.S.Multiplayer.ChooserProbe?.Focus == next, $"reopened on the pick (focus {h.S.Multiplayer.ChooserProbe?.Focus})");
            var last = h.S.Multiplayer.ChooserProbe!.Value.Taps[^1];
            h.Tap(last.X, last.Y, 10);
            Check(set.MenuArtHd && set.MenuArtSuit == MphRecomp.Config.RecompSettings.MenuArtShuffle, $"SHUFFLE picked ('{set.MenuArtSuit}')");
            Open();
            Check(h.S.Multiplayer.ChooserProbe?.Focus == p.Skins.Count - 1, $"reopened on SHUFFLE (focus {h.S.Multiplayer.ChooserProbe?.Focus})");
            var tap = h.S.Multiplayer.ChooserProbe!.Value.Taps[0];
            h.Tap(tap.X, tap.Y, 10);
            Check(!set.MenuArtHd && h.S.Multiplayer.ChooserProbe == null && h.S.MenuArtVersion == 3, $"a tap on ORIGINAL: the DS picture (hd {set.MenuArtHd}, version {h.S.MenuArtVersion})");
            Open();
            h.Tap(4, 4, 10);
            Check(h.S.Multiplayer.ChooserProbe == null && !set.MenuArtHd, "a tap outside closes it, nothing changed");
        }

        private static void WithModels(string dir)
        {
            var h = new Harness(dir, "models");
            h.ToSelectHunter();
            Check(h.Page == MultiplayerMenu.HunterPage, $"SELECT HUNTER (page {h.Page})");
            // a new cart: Samus, Kanden, Spire, Trace lit; Noxus, Sylux, Weavel dark; TROOPER + DARK SAMUS lit (Weavel's
            // icon copied twice), no greyed copies
            Check(h.CountShown("weavel") == 2 && h.CountShown("weaveloff") == 1,
                $"TROOPER + DARK SAMUS lit beside the seven (weavel icons {h.CountShown("weavel")}, weaveloff {h.CountShown("weaveloff")})");
            h.Shot("select");
            // TROOPER: the first tap shows its info box, the second opens the chooser
            var trooper = MultiplayerMenu.HunterIconTap(7);
            h.Tap(trooper.X, trooper.Y);
            List<string> texts = h.Texts();
            Check(texts.Contains("TROOPER") && texts.Any(t => t.Contains("SCOUT DRONE")), $"TROOPER's info box: {String.Join(" | ", texts.Where(t => t.Length > 0).Take(12))}");
            Check(h.S.Multiplayer.ChooserProbe == null && h.Page == MultiplayerMenu.HunterPage, "one tap: no chooser yet");
            h.Shot("trooper_info");
            h.Tap(trooper.X, trooper.Y);
            var ch = h.S.Multiplayer.ChooserProbe;
            Check(ch != null && ch.Value.Skins.SequenceEqual(new[] { "MP1PowerTrooper", "MP1IceTrooper", "MP1WaveTrooper", "MP1PlasmaTrooper" }),
                $"second tap: TROOPER's chooser ({(ch == null ? "none" : ch.Value.Title + ": " + String.Join(", ", ch.Value.Skins))})");
            h.Shot("trooper_chooser");
            // B closes it; the page stays
            h.S.Press(MenuKeys.B);
            h.Run(10);
            Check(h.S.Multiplayer.ChooserProbe == null && h.Page == MultiplayerMenu.HunterPage, $"B closes the chooser (page {h.Page})");
            // DARK SAMUS: two skins
            var dark = MultiplayerMenu.HunterIconTap(8);
            h.Tap(dark.X, dark.Y);
            h.Tap(dark.X, dark.Y);
            ch = h.S.Multiplayer.ChooserProbe;
            Check(ch != null && ch.Value.Skins.SequenceEqual(new[] { "MP2DarkSamus", "MP3DarkSamus" }) && h.Texts().Contains("DARK SAMUS"),
                $"DARK SAMUS's chooser ({(ch == null ? "none" : String.Join(", ", ch.Value.Skins))})");
            // a tap outside the box closes it
            h.Tap(60, 160);
            Check(h.S.Multiplayer.ChooserProbe == null && h.Page == MultiplayerMenu.HunterPage, "a tap outside closes the chooser");
            // Samus: ORIGINAL first, then her suits
            var samus = MultiplayerMenu.HunterIconTap(0);
            h.Tap(samus.X, samus.Y);
            h.Tap(samus.X, samus.Y);
            ch = h.S.Multiplayer.ChooserProbe;
            Check(ch != null && ch.Value.Skins.Count > 4 && ch.Value.Skins[0] == Characters.Original
                && !ch.Value.Skins.Any(s => s.Contains("Trooper") || s.Contains("DarkSamus")),
                $"Samus's chooser: ORIGINAL first, suits only ({(ch == null ? "none" : ch.Value.Skins.Count + ": " + String.Join(", ", ch.Value.Skins.Take(6)))}...)");
            h.Shot("samus_chooser");
            h.S.Press(MenuKeys.B);
            h.Run(10);
            // TROOPER in WAVE: a tap on its round icon picks it -> the lobby, seat 0 that look
            h.Tap(trooper.X, trooper.Y);
            h.Tap(trooper.X, trooper.Y);
            ch = h.S.Multiplayer.ChooserProbe;
            if (ch != null) h.Tap(ch.Value.Taps[2].X, ch.Value.Taps[2].Y, 60);
            Check(h.Page == MultiplayerMenu.LobbyPage && h.Log.Any(l => l.Contains("picked Weavel in MP1WaveTrooper (trooper)")),
                $"WAVE TROOPER's icon picks it -> lobby (page {h.Page})");
            h.Shot("lobby");
            // the lobby badge on seat 0: the chooser on WAVE; a tap outside closes it
            var badge = h.S.Multiplayer.BadgeTap(0);
            Check(badge.R > 0, $"seat 0's skin badge at {badge.X:0},{badge.Y:0} r {badge.R:0.#}");
            h.Tap(badge.X, badge.Y);
            ch = h.S.Multiplayer.ChooserProbe;
            Check(ch != null && ch.Value.Skins.Count == 4 && ch.Value.Focus == 2, $"seat 0's badge: the chooser on its skin ({(ch == null ? "none" : ch.Value.Title + ", focus " + ch.Value.Focus)})");
            h.Shot("lobby_chooser");
            h.Tap(128, 185);
            Check(h.S.Multiplayer.ChooserProbe == null && h.Page == MultiplayerMenu.LobbyPage, "a tap outside closes the lobby chooser");
            // a bot (vanilla seats it in the first free seat from the right): its arrows step all nine; to TROOPER, then its
            // badge picks PLASMA
            h.Tap(223, 170);
            int bot1 = h.BotSeatShowingArrows(-1);
            bool trooperBot = false;
            for (int n = 0; n < 12 && bot1 > 0 && !trooperBot; n++)
            {
                trooperBot = h.Texts().Contains("trooper");
                if (!trooperBot) h.TapCall(BotRight(bot1));
            }
            Check(trooperBot, $"seat {bot1}'s bot: its arrows reach TROOPER (named \"trooper\", D7)");
            var botBadge = bot1 > 0 ? h.S.Multiplayer.BadgeTap(bot1) : default;
            if (botBadge.R > 0)
            {
                h.Tap(botBadge.X, botBadge.Y);
                ch = h.S.Multiplayer.ChooserProbe;
                if (ch != null) h.Tap(ch.Value.Taps[3].X, ch.Value.Taps[3].Y);
            }
            Check(h.Log.Any(l => l.Contains($"seat {bot1} in MP1PlasmaTrooper")), $"the bot's badge: PLASMA (badge r {botBadge.R:0.#})");
            // a second bot stepped to DARK SAMUS
            h.Tap(223, 170);
            int bot2 = h.BotSeatShowingArrows(bot1);
            bool darkBot = false;
            for (int n = 0; n < 12 && bot2 > 0 && !darkBot; n++)
            {
                darkBot = h.Texts().Contains("dark samus");
                if (!darkBot) h.TapCall(BotRight(bot2));
            }
            Check(darkBot, $"seat {bot2}'s bot: its arrows reach DARK SAMUS (named \"dark samus\")");
            h.Shot("lobby_bots");
            // START GAME: the setup carries each seat's look
            foreach ((int x, int y, int wait) in new[] { (98, 139, 10), (154, 63, 10), (212, 139, 40), (195, 41, 140) }) h.Tap(x, y, wait);
            MatchSettings? settings = h.Setup == null ? null : MatchSetupText.Read(h.Setup);
            string looks = settings == null ? "no START GAME" : String.Join(" | ", settings.Players.Select(p => $"{p.Hunter}/{p.Character}/{p.Skin}/{p.Nickname}"));
            Check(settings != null && settings.Players.Count == 3
                && settings.Players[0] is { Hunter: Hunter.Weavel, Character: "trooper", Skin: "MP1WaveTrooper" }
                && settings.Players.Any(p => p is { Bot: true, Hunter: Hunter.Weavel, Character: "trooper", Skin: "MP1PlasmaTrooper", Nickname: "TROOPER" })
                && settings.Players.Any(p => p is { Bot: true, Hunter: Hunter.Samus, Character: "darksamus", Nickname: "DARK SAMUS" }
                    && p.Skin is "MP2DarkSamus" or "MP3DarkSamus"),
                $"START GAME: {looks}");
        }

        private static int BotRight(int seat) => 175 + 3 * seat;

        // the lobby's arrows on seat 0 between Samus, DARK SAMUS (drawn over Samus's picture) and TROOPER (over Weavel's):
        // every step fades like vanilla's hunters (the new look in over 17 frames, the old one out over 23), and a picture
        // on its way out keeps its look (no vanilla Samus / Weavel showing first)
        private static void Fades(string dir)
        {
            var h = new Harness(dir, "fades");
            h.ToSelectHunter();
            var samus = MultiplayerMenu.HunterIconTap(0);
            h.Tap(samus.X, samus.Y);
            h.Tap(samus.X, samus.Y);
            var ch = h.S.Multiplayer.ChooserProbe;
            if (ch != null) h.Tap(ch.Value.Taps[0].X, ch.Value.Taps[0].Y, 60);
            Check(h.Page == MultiplayerMenu.LobbyPage && h.Log.Any(l => l.EndsWith("picked Samus", StringComparison.Ordinal)),
                $"fades: ORIGINAL Samus in the lobby (page {h.Page})");
            if (h.Page != MultiplayerMenu.LobbyPage) return;
            MenuPage page = h.S.Menu.Page!;
            // (by its Idle look: a hidden item shows no model)
            int Picture(string widget) => Enumerable.Range(0, page.Items.Count).FirstOrDefault(i => MathF.Abs(page.Items[i].X - 10) < 1
                && page.Items[i].GetState((int)MenuState.Idle) is { WidgetIndex: >= 0 } idle
                && h.S.Menu.File.Widgets[idle.WidgetIndex].ModelPath.EndsWith("\\" + widget + "_Model.bin", StringComparison.OrdinalIgnoreCase), -1);
            int samusPic = Picture("choicesamus"), weavelPic = Picture("choiceweavel");
            Check(samusPic >= 0 && weavelPic >= 0, $"fades: seat 0's Samus / Weavel pictures (items {samusPic}, {weavelPic})");
            // a picture item's alpha this frame: its ROM look, or a skin's body picture (the texture's key names the skin)
            float Alpha(int item, string? skin)
            {
                var tris = new List<WidgetTri>();
                h.S.Menu.Collect(tris);
                h.S.Multiplayer.ReplacePreview(h.S.Menu, tris, 0);
                float a = 0;
                foreach (WidgetTri t in tris.Where(t => t.Item == item && t.TextureId >= 0))
                {
                    string key = h.S.Menu.Widgets.Textures[t.TextureId].Key;
                    bool body = key.StartsWith("character body ", StringComparison.Ordinal);
                    if (skin == null ? !body : body && key.Contains(skin, StringComparison.OrdinalIgnoreCase)) a = MathF.Max(a, t.A.A);
                }
                return a;
            }
            // one arrow step, then 26 frames: (frame, each look's alpha)
            List<float[]> Step(int call, params (int Item, string? Skin)[] looks)
            {
                h.TapCall(call, wait: 0);
                var rows = new List<float[]>();
                for (int f = 0; f < 26; f++)
                {
                    h.Run(1);
                    rows.Add(looks.Select(l => Alpha(l.Item, l.Skin)).ToArray());
                }
                return rows;
            }
            string Show(List<float[]> rows) => String.Join(" ", rows.Where((_, i) => i % 4 == 0).Select(r => "(" + String.Join("/", r.Select(a => a.ToString("0.00"))) + ")"));
            static bool Rises(List<float[]> rows, int k) => rows[0][k] < 0.5f && rows[^1][k] > 0.95f;
            static bool Falls(List<float[]> rows, int k) => rows[0][k] > 0.5f && rows[^1][k] == 0;
            // Samus -> DARK SAMUS (left, the same picture item): Dark Samus fades in over vanilla Samus fading out
            var rowsA = Step(174, (samusPic, "DarkSamus"), (samusPic, null));
            Check(Rises(rowsA, 0) && Falls(rowsA, 1), $"fades: Samus -> DARK SAMUS: in / Samus out {Show(rowsA)}");
            // DARK SAMUS -> TROOPER (Samus's item out, Weavel's in): Dark Samus fades out as herself, never vanilla Samus
            var rowsB = Step(174, (weavelPic, "Trooper"), (samusPic, "DarkSamus"), (samusPic, null));
            Check(Rises(rowsB, 0) && Falls(rowsB, 1) && rowsB.All(r => r[2] == 0), $"fades: DARK SAMUS -> TROOPER: in / Dark Samus out / no vanilla Samus {Show(rowsB)}");
            // TROOPER -> DARK SAMUS: the Trooper fades out as itself, never vanilla Weavel
            var rowsC = Step(175, (samusPic, "DarkSamus"), (weavelPic, "Trooper"), (weavelPic, null));
            Check(Rises(rowsC, 0) && Falls(rowsC, 1) && rowsC.All(r => r[2] == 0), $"fades: TROOPER -> DARK SAMUS: in / Trooper out / no vanilla Weavel {Show(rowsC)}");
            // DARK SAMUS -> Samus (right, wraps; the same item): vanilla Samus fades in, Dark Samus out
            var rowsD = Step(175, (samusPic, null), (samusPic, "DarkSamus"));
            Check(Rises(rowsD, 0) && Falls(rowsD, 1),
                $"fades: DARK SAMUS -> Samus: in / Dark Samus out {Show(rowsD)}");
            h.TapCall(174, wait: 30); // (DARK SAMUS again, for Reload)
        }

        // back from a match the menus load straight into the lobby (FrontendSession.ReturnPage; SELECT HUNTER never drawn):
        // seat 0's badge and its chooser still show their round icons (owner 2026-10-08 23:08: both were blank)
        private static void Reload(string dir)
        {
            var h = new Harness(dir, "reload");
            h.S.Start(MultiplayerMenu.LobbyPage);
            h.Run(40);
            int Icons()
            {
                var tris = new List<WidgetTri>();
                h.S.Menu.Collect(tris);
                h.S.Multiplayer.ReplacePreview(h.S.Menu, tris, 0);
                return tris.Count(t => t.TextureId >= 0 && h.S.Menu.Widgets.Textures[t.TextureId].Key.StartsWith("character icon ", StringComparison.Ordinal)) / 2;
            }
            var badge = h.S.Multiplayer.BadgeTap(0);
            int badgeIcons = Icons();
            Check(h.Page == MultiplayerMenu.LobbyPage && badge.R > 0 && badgeIcons == 1,
                $"reload into the lobby: seat 0's badge drawn (page {h.Page}, badge r {badge.R:0.#}, {badgeIcons} icon)");
            h.Tap(badge.X, badge.Y);
            var ch = h.S.Multiplayer.ChooserProbe;
            int shown = Icons();
            Check(ch != null && ch.Value.Skins.Count == 2 && shown == 3,
                $"... its chooser draws both DARK SAMUS icons ({shown} icons with the badge, chooser {(ch == null ? "none" : String.Join(", ", ch.Value.Skins))})");
        }

        private static void WithoutModels(string dir)
        {
            var h = new Harness(dir, "nomodels");
            h.ToSelectHunter();
            Check(h.Page == MultiplayerMenu.HunterPage, $"no models: SELECT HUNTER (page {h.Page})");
            Check(h.CountShown("weavel") == 0 && h.CountShown("weaveloff") == 3,
                $"no models: TROOPER + DARK SAMUS greyed (weavel icons {h.CountShown("weavel")}, weaveloff {h.CountShown("weaveloff")})");
            h.Shot("select_nomodels");
            var trooper = MultiplayerMenu.HunterIconTap(7);
            h.Tap(trooper.X, trooper.Y);
            h.Tap(trooper.X, trooper.Y);
            Check(h.S.Multiplayer.ChooserProbe == null && h.Page == MultiplayerMenu.HunterPage, $"no models: TROOPER can't be picked (page {h.Page})");
            var samus = MultiplayerMenu.HunterIconTap(0);
            h.Tap(samus.X, samus.Y);
            h.Tap(samus.X, samus.Y, 60);
            Check(h.S.Multiplayer.ChooserProbe == null && h.Page == MultiplayerMenu.LobbyPage && h.S.Multiplayer.BadgeTap(0).R == 0,
                $"no models: Samus straight to the lobby, no badge (page {h.Page})");
        }

        private static void Warm(string dir)
        {
            List<string> Drain()
            {
                var taken = new List<string>();
                while (CharacterPictures.TryTake(out CharacterPictureJob job))
                {
                    taken.Add(job.Skin);
                    CharacterPictures.Finished(job, false); // (nothing draws on the PC)
                }
                return taken;
            }
            Drain();
            CharacterPictures.HostRenders = true;
            CharacterPictures.Invalidate();
            try
            {
                var h = new Harness(dir, "warm");
                h.S.Start(18);
                h.Run(30);
                List<string> first = Drain();
                string[] need = { "MP1PowerTrooper", "MP1IceTrooper", "MP1WaveTrooper", "MP1PlasmaTrooper", "MP2DarkSamus", "MP3DarkSamus", "MP1VariaSuit" };
                Check(need.All(first.Contains), $"the menus' start queues every skin's pictures before SELECT HUNTER ({first.Count}: {String.Join(", ", first.Take(8))}...)");
                h.Run(30);
                Check(Drain().Count == 0, "... once (nothing queued again while nothing changed)");
                h.S.Recomp.Import.Changed?.Invoke(); // (an import ended)
                h.Run(5);
                List<string> again = Drain();
                Check(need.All(again.Contains), $"an import's end queues them again ({again.Count})");
            }
            finally
            {
                CharacterPictures.HostRenders = false;
                CharacterPictures.Invalidate();
            }
        }

        // the mockup's renders as the device's pictures (the names CharacterPictures.For looks for)
        private static void FillPictures(string hd, string pictures)
        {
            string? renders = FindUp(AppContext.BaseDirectory, Path.Combine("testdata", "picker_renders"));
            if (renders == null) return;
            foreach (string folder in Directory.EnumerateDirectories(hd))
            {
                string skin = Path.GetFileName(folder);
                string head = Path.Combine(renders, $"menu_samus_head_{skin}.png");
                if (!File.Exists(head)) continue;
                string full = Path.Combine(renders, $"menu_samus_full_{skin}.png");
                string? dae = Directory.EnumerateFiles(folder, "*.dae").FirstOrDefault();
                if (dae == null) continue;
                string stem = Path.Combine(pictures, $"{skin}_{File.GetLastWriteTimeUtc(dae).Ticks:x}_{CharacterPictures.Look}");
                File.Copy(head, stem + "_head.png", overwrite: true);
                File.Copy(File.Exists(full) ? full : head, stem + "_full.png", overwrite: true);
            }
        }

        private static string? FindUp(string from, string relative)
        {
            for (DirectoryInfo? d = new(from); d != null; d = d.Parent)
            {
                string p = Path.Combine(d.FullName, relative);
                if (Directory.Exists(p)) return p;
            }
            return null;
        }
    }
}
