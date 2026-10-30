using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MphRead;
using MphRecomp.Import;

// IMPORT GAMES as five game cards (owner 2026-10-08: "make the importer menu a bit more visually lively", like the
// game-look REPLAYS page; picked mockup B, extract_out/import_mockups/mockups/B_cards_*.png). The rows page it was (page
// IMPORT GAMES, RecompMenus.cs) stays the page: its back, B, title and DELETE ALL corner are kept, its rows and panel are
// gone, and five card items stand in for the rows -- Metroid Prime, Prime 2: Echoes, Prime 3: Corruption, Metroid Prime
// Trilogy and Prime 4: Beyond (locked, "not yet"). A tap or A on a card does what its row did (the game's page, the
// Trilogy's picker, "not yet"'s box); there's no OPEN button (owner: "we can just click it to select it"). Each card
// shows the game's emblem big on a dark plate, its name, which discs it takes, what its disc makes (a round icon and a
// count for the suits, morph balls, characters, maps and music), how many are imported and a bar with a cell per item.
// The base look is ROM menu pieces (the setup boxes' outline, the lobby's glow, the menus' font) and our own pictograms
// (ImportCardPictures): nothing on it needs an import (owner: "the way it looks shouldn't solely rely on assets that can
// only be used after import"). Drawn by DrawImportCards (FrontendSession.Build) with REPLAYS' helpers
// (RecompMenus.Replays.cs); MenuLayout.ImportCards places the page.
namespace MphRecomp.Frontend
{
    public sealed partial class RecompMenus
    {
        private static int s_cardsPage = -1;
        // the cards page or a game's page (RecompMenus.ImportGamePage.cs): both placed by MenuLayout.ImportCards
        public static bool IsImportCardsPage(MenuPage page) => page.Index >= 0 && (page.Index == s_cardsPage || page.Index == s_gamePage);

        private const int ImportCardCall = 2200; // + the card
        private const int Cards = 5;

        // ---- geometry (menu units, Y up; MenuLayout.ImportCards shows x -60..316 at the setup pages' touch scale) ----
        private const float CardX0 = -45.94f, CardW = 66.67f, CardGap = 3.64f, CardTop = -22.26f, CardH = 148.48f, CardEdge = 0.4f;
        private const float PlateInset = 3.64f, PlateH = 60.61f, Emblem = 42.42f;
        private const float TitleGap = 3.64f, TitlePitch = 8.08f, TitleBlock = 18.59f, PlatformPitch = 8.08f;
        private const float KindIcon = 8.08f, KindAfterIcon = 1.62f, KindAfterCount = 4.44f, KindPitch = 10.1f;
        private const float TrilogyDisc = 8.89f, TrilogyDx = 15.35f, TrilogyPitch = 10.5f;
        private const float CountFromBottom = 22.63f, CardBarBelow = 10.5f, CardBarInset = 4.04f, CardBarH = 6.06f, CardBarPad = 1.01f;

        private static readonly (float R, float G, float B) Grey = (150 / 255f, 150 / 255f, 150 / 255f);

        // the card items on the page (-1 = none), the one focused last, the item whose look says the page has come in
        private readonly int[] _cardItems = { -1, -1, -1, -1, -1 };
        private int _cardFocus, _cardsSettle = -1;
        private bool _cardsChained;
        private MenuSounds? _cardSounds;
        private CardInfo[]? _cardInfos;

        // what a card shows, read as the page comes in and after every import (not each frame: ImportGames.Game sorts);
        // Stale: an update changed how some of its items are made (UPDATE on its page), or its copies have something to add
        // (UPDATE COPIES) -> the red badge in its corner
        private sealed record CardInfo(ImportGames.GameRow Row, bool Known, int Done, int Total, List<(string Kind, int Count)> Kinds,
            ImportPicked? Source = null, List<ImportItemInfo>? Shown = null, bool Stale = false);

        // IMPORT GAMES' folder (the settings file's): the picked discs' banners and the imports' renders are cached there
        private string? ImportRoot => _settingsPath == null ? null : Path.GetDirectoryName(_settingsPath);

        // the disc line under each name (the mockup's, shorter than ImportGames' own)
        private static string Platform(ImportGames.GameRow row) => row.Id switch
        {
            "mp3" => "trilogy or wii",
            ImportGames.TrilogyId => "one wii disc",
            _ => row.Platform
        };

        private static string[] TitleLines(ImportGames.GameRow row)
        {
            if (row.Id == ImportGames.TrilogyId) return new[] { "metroid prime", "trilogy" };
            string t = row.Title.ToLowerInvariant();
            int colon = t.IndexOf(": ", StringComparison.Ordinal);
            return t.Length > 13 && colon > 0 ? new[] { t[..(colon + 1)], t[(colon + 2)..] } : new[] { t };
        }

        // RecompMenus.Install, after the import pages: the cards on IMPORT GAMES' page
        private void InstallImportCards()
        {
            if (_importRows == null || _file.Pages.Count <= MultiplayerMenu.LobbyPage) return;
            MenuPage page = _file.Pages[_importRows.Page];
            MenuItemState? glow = _file.Pages[MultiplayerMenu.LobbyPage].Items.Where(it => HasWidget(it, "player_highlight"))
                .SelectMany(it => it.States).FirstOrDefault(s => s.Code == (int)MenuState.Idle && s.WidgetIndex >= 0);
            if (glow == null) return;
            s_cardsPage = page.Index;
            // no rows (an empty list hides them all); the panel, the description band and the logo hidden as it comes in
            _importRows.Source = () => new List<RowModel>();
            string[] frame = { "blackdrop", "topdrop", "toplogo", "toplogoR" };
            HiddenOn(page.Index).AddRange(page.Items.Take(_p.Frame.Length)
                .Where(it => frame.Any(n => HasWidget(it, n)) || (it.IsText && it.Y >= 300)).Select(it => it.Index));
            _cardsSettle = page.Items.Take(_p.Frame.Length).FirstOrDefault(it => HasWidget(it, "backicon"))?.Index ?? -1;
            for (int k = 0; k < Cards; k++)
            {
                (float x0, float x1) = CardSpan(k);
                int call = ImportCardCall + k;
                _cardItems[k] = page.AddItem(i => new MenuItem
                {
                    Index = i,
                    Field34 = (byte)MenuState.Idle,
                    Field35 = (byte)MenuState.Idle,
                    // no look until focused (then the lobby's glow, which DrawImportCards shapes round the card)
                    States = new[]
                    {
                        new MenuItemState { Code = (int)MenuState.Focused, Kind = glow.Kind, Field2 = glow.Field2, WidgetIndex = glow.WidgetIndex, WidgetFlags = glow.WidgetFlags }
                    },
                    Links = Array.Empty<MenuLink>(),
                    Actions = new List<MenuAction>
                    {
                        new()
                        {
                            Kind = (ushort)MenuKeys.A, RectX = (short)MathF.Floor(x0), RectY = (short)MathF.Floor(CardTop - CardH + 192),
                            RectW = (short)MathF.Ceiling(x1), RectH = (short)MathF.Ceiling(CardTop + 192), Calls = new[] { (0, call) },
                            TargetPage = 0xFF, Item = 0xFFFF
                        }
                    }
                }).Index;
            }
            InstallImportGamePage(glow); // a game's page as a strip and tiles (RecompMenus.ImportGamePage.cs)
            if (!_cardsChained)
            {
                // an import ended, a disc was picked, something was deleted: the cards read again
                _cardsChained = true;
                Action? before = Import.Changed;
                Import.Changed = () =>
                {
                    before?.Invoke();
                    ImportRenders.Forget();
                    OnCardsChanged();
                    OnGamePageChanged();
                };
            }
        }

        private static (float X0, float X1) CardSpan(int k)
        {
            float x0 = CardX0 + k * (CardW + CardGap);
            return (x0, x0 + CardW);
        }

        private void ReadCards()
        {
            var cards = new CardInfo[Cards];
            for (int k = 0; k < Cards && k < ImportGames.GameRows.Length; k++)
            {
                ImportGames.GameRow row = ImportGames.GameRows[k];
                if (row.Id == ImportGames.TrilogyId)
                {
                    (int tDone, int tTotal) = Import.TrilogyCount();
                    bool tStale = ImportGames.TrilogyGames.Any(id => Import.Game(id)?.Items.Any(ImportGames.NeedsUpdate) == true)
                        || Import.CopiesToAdd(row.Id) > 0;
                    cards[k] = new CardInfo(row, tTotal > 0, tDone, tTotal, new(), Import.Source(row.Id), Stale: tStale);
                    continue;
                }
                ImportGameInfo? g = row.Available ? Import.Game(row.Id) : null;
                if (g == null)
                {
                    cards[k] = new CardInfo(row, false, 0, 0, new());
                    continue;
                }
                var kinds = new List<(string, int)>();
                void Kind(string kind, int count)
                {
                    if (count > 0) kinds.Add((kind, count));
                }
                Kind(ImportCardPictures.Suit, g.Items.Count(ImportGames.IsSuit));
                Kind(ImportCardPictures.Ball, g.Items.Count(ImportGames.IsBall));
                Kind(ImportCardPictures.Creature, g.Items.Count(i => i.Kind == "hd" && !ImportGames.IsSuit(i) && !ImportGames.IsBall(i)));
                Kind(ImportCardPictures.Radar, g.Items.Count(i => i.Kind == "arenas"));
                Kind(ImportCardPictures.Music, g.Items.Count(i => i.Kind == "music"));
                // the card's picture: the first imported suit's render, else a character's, a morph ball's, an arena's
                var shown = g.Items.Where(ImportGames.Imported).OrderBy(i => ImportGames.IsSuit(i) ? 0 : i.Kind == "hd" && !ImportGames.IsBall(i) ? 1
                    : ImportGames.IsBall(i) ? 2 : 3).ToList();
                (int gDone, int gTotal) = ImportGames.TileCount(g);
                cards[k] = new CardInfo(row, true, gDone, gTotal, kinds,
                    Import.Source(row.Id), shown, g.Items.Any(ImportGames.NeedsUpdate) || Import.CopiesToAdd(row.Id) > 0);
            }
            _cardInfos = cards;
        }

        // RecompMenus.OnPageEntered: the cards read; the focus on the game just left (back from its page), else the last one
        private void EnterImportCards(MenuEngine menu, int page)
        {
            EnterImportGamePage(menu, page);
            if (page != s_cardsPage || s_cardsPage < 0) return;
            ApplyCardSounds(menu.Sounds);
            ReadCards();
            if (_lastPage == _importGameRows?.Page)
            {
                int k = Array.FindIndex(ImportGames.GameRows, r => r.Id == _importGame);
                if (k >= 0 && k < Cards) _cardFocus = k;
            }
            FocusCard(menu, _cardFocus);
            // come from outside IMPORT GAMES (not back from a game's page): the box asks about updates (FrontendSession)
            if (_lastPage != _importGameRows?.Page && _lastPage != _songRows?.Page && _lastPage != _songFilterRows?.Page
                && _lastPage != _songGameRows?.Page && _lastPage != _songAreaRows?.Page) UpdateNoticeDue = true;
        }

        // IMPORT GAMES was just come to: FrontendSession asks "n items need updates. update them all now?" once the page is
        // settled (owner 2026-10-17: "a notice when you go to the screen if you need updates ... with a check or x")
        public bool UpdateNoticeDue { get; set; }

        private void FocusCard(MenuEngine menu, int focus)
        {
            for (int k = 0; k < Cards; k++)
            {
                if (_cardItems[k] >= 0) menu.SetState(_cardItems[k], k == focus ? MenuState.Focused : MenuState.Idle);
            }
        }

        // a card opens like CREATE OR JOIN's list row (as REPLAYS' rows; the main menu's adventure ends like the back sound)
        private void ApplyCardSounds(MenuSounds sounds)
        {
            if (ReferenceEquals(sounds, _cardSounds)) return;
            _cardSounds = sounds;
            if (_soundListRow < 0) return;
            foreach (int item in _cardItems)
            {
                if (item >= 0) sounds.ItemLike(s_cardsPage, item, MultiplayerMenu.CreateJoinPage, _soundListRow);
            }
        }

        private void OnCardsChanged()
        {
            if (s_cardsPage < 0) return;
            ReadCards();
            // DELETE ALL hides itself once there's nothing left: its focus back to the card
            MenuEngine? menu = _stepMenu;
            if (menu?.Page?.Index != s_cardsPage) return;
            int focused = menu.FocusedItem();
            if (focused < 0 || menu.ItemState(focused) == MenuState.Hidden) FocusCard(menu, _cardFocus);
        }

        // Returns true when the call was one of the cards' (RecompMenus.OnCall, before its own)
        private bool ImportCardsCall(MenuEngine menu, int call)
        {
            if (ImportGamePageCall(menu, call)) return true;
            if (s_cardsPage < 0 || menu.Page?.Index != s_cardsPage) return false;
            if (call == DeleteAllUp)
            {
                // up from DELETE ALL: the card focused last (the rows page's answer looks for a row)
                FocusCard(menu, _cardFocus);
                return true;
            }
            if (call < ImportCardCall || call >= ImportCardCall + Cards) return false;
            int k = call - ImportCardCall;
            _cardFocus = k;
            if (k >= ImportGames.GameRows.Length) return true;
            ImportGames.GameRow row = ImportGames.GameRows[k];
            if (row.Id == ImportGames.TrilogyId)
            {
                // one disc, every game on it: the picker, then the ask box
                Import.ImportTrilogy();
                return true;
            }
            if (!row.Available || Import.Game(row.Id) == null || _importGameRows == null)
            {
                Import.Popup?.Inform(FrontendSession.WorkInProgressText);
                return true;
            }
            _importGame = row.Id;
            _importGameRows.Title = row.Title;
            menu.GoTo(_importGameRows.Page);
            return true;
        }

        // ---- drawing (FrontendSession.Build, after the page's own triangles) ----

        public void DrawImportCards(MenuEngine menu, List<WidgetTri> tris)
        {
            MenuPage? page = menu.Page;
            if (page == null || !IsImportCardsPage(page)) return;
            if (page.Index == s_gamePage)
            {
                DrawImportGamePage(menu, tris);
                return;
            }
            _drawMenu = menu;
            _out = tris;
            // the cards' glows: lifted out of the engine's triangles, drawn again round each card in its place in the order
            var glows = new Dictionary<int, List<WidgetTri>>();
            for (int t = tris.Count - 1; t >= 0; t--)
            {
                int item = tris[t].Item;
                if (Array.IndexOf(_cardItems, item) < 0) continue;
                if (!glows.TryGetValue(item, out List<WidgetTri>? g)) glows[item] = g = new List<WidgetTri>();
                g.Add(tris[t]);
                tris.RemoveAt(t);
            }
            bool settled = menu.PendingPage == -1 && (_cardsSettle < 0
                || menu.ItemState(_cardsSettle) == MenuState.Idle && menu.ItemCode(_cardsSettle) == (int)MenuState.Idle);
            if (!settled) return;
            for (int k = 0; k < Cards; k++)
            {
                if (_cardItems[k] >= 0 && menu.ItemState(_cardItems[k]) == MenuState.Focused) _cardFocus = k;
            }
            if (_cardInfos == null) ReadCards();
            try
            {
                for (int k = 0; k < Cards; k++)
                {
                    if (_cardInfos![k] is CardInfo info && _cardItems[k] >= 0) DrawCard(k, info, glows);
                }
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or ArgumentException or KeyNotFoundException)
            {
                // a missing ROM model (an older extraction): the page stays usable without its look
                Console.Error.WriteLine("import cards: can't draw (" + ex.Message + ")");
            }
        }

        private void DrawCard(int k, CardInfo info, Dictionary<int, List<WidgetTri>> glows)
        {
            int tag = _cardItems[k];
            bool soon = !info.Row.Available;
            (float x0, float x1) = CardSpan(k);
            float top = CardTop, bottom = CardTop - CardH, mid = (x0 + x1) / 2;
            // the card: the backdrop darkened, a thin dark-orange edge; the lobby's glow round it when focused
            Fill(x0, top, x1, bottom, 0, 0, 0, 0.58f, tag);
            var edge = (R: 0.5f, G: 0.22f, B: 0.02f);
            Fill(x0, top, x1, top - CardEdge, edge.R, edge.G, edge.B, 0.85f, tag);
            Fill(x0, bottom + CardEdge, x1, bottom, edge.R, edge.G, edge.B, 0.85f, tag);
            Fill(x0, top - CardEdge, x0 + CardEdge, bottom + CardEdge, edge.R, edge.G, edge.B, 0.85f, tag);
            Fill(x1 - CardEdge, top - CardEdge, x1, bottom + CardEdge, edge.R, edge.G, edge.B, 0.85f, tag);
            Glow(glows, tag, x0 + 1.21f, top - 1.21f, x1 - 1.21f, bottom + 1.21f, tag);

            // the picture plate: the game's emblem big; once something is imported, its render fills the plate and the
            // emblem goes small in its corner
            float px0 = x0 + PlateInset, px1 = x1 - PlateInset, ptop = top - PlateInset, pbottom = ptop - PlateH;
            Fill(px0, ptop, px1, pbottom, 0, 0, 0, 0.47f, tag);
            if (CardRender(info, px0, ptop, px1, pbottom, tag)) DrawEmblem(info.Row, px0 + CornerInset, ptop - CornerInset, CornerEmblem, true, tag);
            else DrawEmblem(info.Row, (px0 + px1) / 2 - Emblem / 2, ptop - (PlateH - Emblem) / 2, Emblem, !soon, tag);
            Box(px0, ptop, px1, pbottom, tag, greyed: soon);

            // name (the picked disc's own banner instead, once there is one: ImportBanners), discs
            float y = pbottom - TitleGap;
            string? banner = ImportBanners.For(ImportRoot, info.Source);
            if (banner != null && DrawPng(banner, mid - BannerCardW / 2, y + BannerCardLift, mid + BannerCardW / 2, y + BannerCardLift - BannerCardH, tag))
            {
                y -= BannerCardBlock;
            }
            else
            {
                string[] lines = TitleLines(info.Row);
                for (int i = 0; i < lines.Length && i < 2; i++) ScaledText(lines[i], mid, y - TitlePitch * i, 2, soon ? Grey : Orange, tag, 0.8f);
                y -= TitleBlock;
            }
            ScaledText(Platform(info.Row), mid, y, 2, Pale, tag, 0.5f);
            y -= PlatformPitch;

            // what the disc makes: three to a row, each row centred
            if (info.Row.Id == ImportGames.TrilogyId)
            {
                for (int g = 0; g < 3 && g < ImportGames.GameRows.Length; g++)
                {
                    DrawEmblem(ImportGames.GameRows[g], mid - TrilogyDx + g * TrilogyPitch, y, TrilogyDisc, true, tag);
                }
            }
            for (int r = 0; r * 3 < info.Kinds.Count; r++)
            {
                var row = info.Kinds.Skip(r * 3).Take(3).ToList();
                float width = row.Sum(kc => KindIcon + KindAfterIcon + CountWidth(kc.Count) * 0.7f) + KindAfterCount * (row.Count - 1);
                float x = mid - width / 2;
                foreach ((string kind, int count) in row)
                {
                    Picture(kind, x, y, KindIcon, off: false, tag);
                    x += KindIcon + KindAfterIcon;
                    ScaledText(count.ToString(), x, y - (KindIcon - 8 * 0.7f) / 2, 0, White, tag, 0.7f);
                    x += CountWidth(count) * 0.7f + KindAfterCount;
                }
                y -= KindPitch;
            }

            // import again: the red badge over the card's upper left corner (owner 2026-10-16), on top of the plate
            if (info.Stale) Picture(ImportCardPictures.Alert, x0 - AlertBadge / 2 + AlertIn, top + AlertBadge / 2 - AlertIn, AlertBadge, off: false, tag);

            // how many are imported and the bar; "not yet" on Prime 4
            float countTop = bottom + CountFromBottom;
            if (soon || !info.Known)
            {
                ScaledText("not yet", mid, countTop - 4.04f, 2, Grey, tag, 0.9f);
                return;
            }
            ScaledText($"{info.Done}/{info.Total}", mid, countTop, 2, White, tag, 0.9f);
            Bar(x0 + CardBarInset, countTop - CardBarBelow, x1 - CardBarInset, info.Done, info.Total, tag);
        }

        // the banner on a card: 2.9 times its 96 x 32 (the mockup's 278 x 93 px), its top 6 px above the title's
        private const float BannerCardW = 56.24f, BannerCardH = 18.75f, BannerCardLift = 1.21f, BannerCardBlock = 19.56f;

        // the import-again badge: its centre this far inside the card's corner
        private const float AlertBadge = 11.31f, AlertIn = 1.21f;

        // the card's render, the mockup's emblem in its corner (74 px, 8 in)
        private const float CornerEmblem = 14.95f, CornerInset = 1.62f;

        // the first of the card's imported things with a picture (ImportRenders), filling the plate: a body's from its
        // top, as wide as the plate; anything else centred, cut to the plate's shape. One still being rendered waits (the
        // emblem meanwhile) rather than showing a later one
        private bool CardRender(CardInfo info, float x0, float top, float x1, float bottom, int tag)
        {
            if (info.Shown == null) return false;
            foreach (ImportItemInfo item in info.Shown)
            {
                string? png = ImportRenders.For(ImportRoot, item, out bool pending);
                if (png == null)
                {
                    if (pending) return false;
                    continue;
                }
                if (PngTexture(png) is not UiTexture tex) continue;
                float plate = (top - bottom) / (x1 - x0), image = tex.Height / (float)tex.Width;
                if (image > 1.2f) Quad(tex.Id, x0, top, x1, bottom, 0, 0, 1, plate / image, 1, 1, 1, 1, tag);
                else if (image < plate)
                {
                    float cut = (1 - image / plate) / 2;
                    Quad(tex.Id, x0, top, x1, bottom, cut, 0, 1 - cut, 1, 1, 1, 1, 1, tag);
                }
                else Quad(tex.Id, x0, top, x1, bottom, 0, 0, 1, 1, 1, 1, 1, 1, tag);
                return true;
            }
            return false;
        }

        // a cached PNG (a disc's banner, an import's render) as a texture; null when it can't be read (then the base look
        // stands)
        private UiTexture? PngTexture(string png)
        {
            try
            {
                return _drawMenu!.Widgets.Textures.GetOrAdd($"import png {png} {File.GetLastWriteTimeUtc(png).Ticks}", () =>
                {
                    byte[] px = PngOut.Read(png, out int w, out int h);
                    return (w, h, px);
                });
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException or UnauthorizedAccessException)
            {
                Console.Error.WriteLine("import: can't show " + Path.GetFileName(png) + " (" + ex.Message + ")");
                return null;
            }
        }

        // the PNG stretched over the box
        private bool DrawPng(string png, float x0, float top, float x1, float bottom, int tag)
        {
            if (PngTexture(png) is not UiTexture tex) return false;
            Quad(tex.Id, x0, top, x1, bottom, 0, 0, 1, 1, 1, 1, 1, 1, tag);
            return true;
        }

        private float CountWidth(int count) => _drawMenu!.Font.Measure(count.ToString());

        // a game's emblem: its disc with its number on it, the trilogy's three discs, Prime 4's padlock (greyed)
        private void DrawEmblem(ImportGames.GameRow row, float x, float top, float d, bool lit, int tag)
        {
            string name = row.Id == ImportGames.TrilogyId ? ImportCardPictures.Discs3 : row.Available ? ImportCardPictures.Disc : ImportCardPictures.Lock;
            Picture(name, x, top, d, off: !lit, tag);
            if (name != ImportCardPictures.Disc) return;
            string digit = row.Id.Length > 2 && char.IsDigit(row.Id[2]) ? row.Id[2..] : "";
            float scale = d * 3 / 64;
            var colour = lit ? (230 / 255f, 99 / 255f, 16 / 255f) : (90 / 255f, 90 / 255f, 90 / 255f);
            ScaledText(digit, x + d / 2 + scale * 0.5f, top - (d / 2 - 4 * scale), 2, colour, tag, scale);
        }

        // one of ours (ImportCardPictures), made once a size: the big emblems finer than the small icons
        private void Picture(string name, float x, float top, float d, bool off, int tag)
        {
            int size = d > 20 ? 256 : 64, samples = d > 20 ? 2 : 4;
            (float, float)[]? suit = name == ImportCardPictures.Suit ? MedalS() : null;
            UiTexture tex = _drawMenu!.Widgets.Textures.GetOrAdd($"import card {name} {size}{(off ? " off" : "")}",
                () => ImportCardPictures.Draw(name, size, samples, off, suit));
            Quad(tex.Id, x, top, x + d, top - d, 0, 0, 1, 1, 1, 1, 1, 1, tag);
        }

        // the suits' S, made once from the ROM's results-screen gold medal (ImportCardPictures.SuitFromMedal)
        private (float, float)[]? _medalS;
        private bool _medalSRead;
        private (float, float)[]? MedalS()
        {
            if (_medalSRead) return _medalS;
            _medalSRead = true;
            int id = Still(@"results\medalgold").Select(t => t.TextureId).FirstOrDefault(t => t >= 0, -1);
            if (id < 0) return null;
            UiTexture medal = _drawMenu!.Widgets.Textures[id];
            _medalS = ImportCardPictures.SuitFromMedal(medal.Width, medal.Height, medal.Rgba);
            return _medalS;
        }

        // the progress bar (ours): a black track in the setup boxes' outline, a cell per item, the imported ones orange
        // with a pale top line, the rest dark
        private void Bar(float x0, float top, float x1, int done, int total, int tag)
        {
            float bottom = top - CardBarH;
            Fill(x0, top, x1, bottom, 0, 0, 0, 150 / 255f, tag);
            if (total > 0)
            {
                float cell = (x1 - x0 - 2 * CardBarPad) / total, px = 0.2f;
                for (int i = 0; i < total; i++)
                {
                    float cx = x0 + CardBarPad + i * cell + px, cw = cell - 3 * px;
                    float ct = top - CardBarPad, cb = bottom + CardBarPad;
                    if (i < done)
                    {
                        Fill(cx, ct, cx + cw, cb, 230 / 255f, 99 / 255f, 16 / 255f, 1, tag);
                        Fill(cx, ct, cx + cw, ct - 3 * px, 1, 190 / 255f, 110 / 255f, 1, tag);
                    }
                    else Fill(cx, ct, cx + cw, cb, 60 / 255f, 30 / 255f, 4 / 255f, 200 / 255f, tag);
                }
            }
            Box(x0, top, x1, bottom, tag);
        }

        // the setup boxes' outline (gameoptions box_type); greyed: the "not available" look (grey, 42% bright)
        private void Box(float x0, float top, float x1, float bottom, int tag, bool greyed)
        {
            if (!greyed)
            {
                Box(x0, top, x1, bottom, tag);
                return;
            }
            List<WidgetTri> model = Still(@"gameoptions\box_type");
            if (model.Count == 0) return;
            UiTextureCache textures = _drawMenu!.Widgets.Textures;
            int source = model[0].TextureId;
            if (source < 0) return;
            UiTexture grey = textures.GetOrAdd($"import card box off {source}", () =>
            {
                UiTexture t = textures[source];
                byte[] px = (byte[])t.Rgba.Clone();
                for (int i = 0; i < px.Length; i += 4)
                {
                    byte v = (byte)MathF.Round((0.299f * px[i] + 0.587f * px[i + 1] + 0.114f * px[i + 2]) * 0.42f);
                    px[i] = px[i + 1] = px[i + 2] = v;
                }
                return (t.Width, t.Height, px);
            });
            List<WidgetTri> off = model.Select(t =>
            {
                WidgetTri c = t;
                c.TextureId = grey.Id;
                return c;
            }).ToList();
            Nine(off, x0, top, x1, bottom, 3, 0.5f, tag);
        }

        // the menus' font at `scale` times its size (Text's line, grown from its anchor: the cells' top at `top`)
        private void ScaledText(string text, float x, float top, int align, (float R, float G, float B) colour, int tag, float scale)
        {
            int start = _out.Count;
            Text(text, x, top, align, colour, tag);
            for (int t = start; t < _out.Count; t++)
            {
                WidgetTri tri = _out[t];
                tri.A = Grow(tri.A);
                tri.B = Grow(tri.B);
                tri.C = Grow(tri.C);
                _out[t] = tri;
            }
            UiVertex Grow(UiVertex v)
            {
                v.X = x + (v.X - x) * scale;
                v.Y = top + (v.Y - top) * scale;
                return v;
            }
        }
    }
}
