using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MphRead;
using MphRecomp.Import;

// One game's IMPORT page as a strip and a grid of tiles (owner 2026-10-08: "I like both", the cards and mockup C,
// extract_out/import_mockups/mockups/C_game_*.png). As with the cards (RecompMenus.ImportCards.cs) the rows page it was
// stays the page -- its back, B, title and DELETE ALL corner are kept, its rows and panel are gone -- and our own items
// stand in for the rows. The strip: "your disc" (the game's emblem, which discs it takes or the one picked, PICK /
// CHANGE), the wii key box (greyed while the disc needs none) and IMPORT ALL (the lobby's START GAME pill, dimmed until
// a disc is picked) with how many are imported and a cell bar. Under it ten tiles a page, L / R (or the arrows beside the
// title) for the next ten: each item's pictogram on a dark plate, its name, its status in the setup boxes' outline
// (IMPORT / DONE / UPDATE ...; A imports it as its row did) and, once imported, an X box in its corner (a tap there or X
// deletes it after the ask box). The base look needs no import; imported renders on the tiles come later.
namespace MphRecomp.Frontend
{
    public sealed partial class RecompMenus
    {
        private static int s_gamePage = -1;

        private const int GameTiles = 10, GameColumns = 5;
        private const int GamePickCall = 2210, GameKeyCall = 2211, GameAllCall = 2212, GamePrevCall = 2213, GameNextCall = 2214, GameUpdateCall = 2215;
        private const int GameRightCall = 2240, GameLeftCall = 2241;
        private const int GameTileCall = 2220, GameTileDeleteCall = 2230; // + the tile

        // ---- geometry: the mockup's pixels (1920 x 1080, game_grid in compose_import.py), placed by MenuLayout.ImportCards
        // (the cards region's 1.1 scale at 1080 p = 4.95 px a menu unit, the canvas centre at menu (128, -96)) ----
        private const float MockPx = 4.95f;
        private static float GX(float px) => 128 + (px - 960) / MockPx;
        private static float GY(float py) => -96 - (py - 540) / MockPx;
        private static float GL(float px) => px / MockPx;

        private const float PanelX0 = 70, PanelY0 = 170, PanelX1 = 1850, PanelY1 = 916;
        private const float StripX = 94, StripY = 188, StripW = 880, StripH = 132;
        private const float PickX = 774, PickY = 222, PickW = 180, PickH = 64;
        private const float KeyX = 994, KeyW = 330;
        private const float AllX = 1344, AllW = 482;
        // IMPORT ALL and UPDATE ALL share its place (owner 2026-10-17: "an update all button by import all"): two pills
        private const float AllPillW = (AllW - 30) / 2, UpdX = AllX + 10 + AllPillW + 10;
        private const float TileX0 = 99, TileY0 = 342, TileW = 330, TileH = 268, TileGapX = 18, TileGapY = 16;
        private const float NameWidth = 530; // "your disc"'s second line: up to the PICK box

        // our items on the page (-1 = none)
        private int _gPick = -1, _gKey = -1, _gAll = -1, _gUpd = -1, _gPrev = -1, _gNext = -1, _gameSettle = -1;
        private readonly int[] _gTile = Enumerable.Repeat(-1, GameTiles).ToArray(), _gTileX = Enumerable.Repeat(-1, GameTiles).ToArray();
        private int _tileOffset, _tileFocus;
        private MenuSounds? _gameSounds;

        // what the page shows, read as it comes in and after every import (ImportGames.Game sorts: not each frame)
        private List<ImportItemInfo>? _gameItems;
        // every item of the game, its songs among them (the tiles show the songs as one MUSIC tile: ImportGames.SongsTile)
        private List<ImportItemInfo> _gameAll = new(), _gameSongs = new();
        private ImportPicked? _gameSource;
        private bool _keyShown, _keyNeeded, _hasKey, _switchKeys;
        private int _gameStale; // the items an update made stale (UPDATE ALL)

        private int GamePages => Math.Max(1, ((_gameItems?.Count ?? 0) + GameTiles - 1) / GameTiles);

        private static (float X, float Y) TileAt(int k) => (TileX0 + k % GameColumns * (TileW + TileGapX), TileY0 + k / GameColumns * (TileH + TileGapY));

        // an input item: no look until focused (then the lobby's glow, which DrawImportGamePage shapes round it); A or a tap
        // in its rectangle (menu space) runs `call`; `more`: its other keys
        private static MenuItem GameInput(int index, MenuItemState glow, float x0, float top, float x1, float bottom, int call,
            params MenuAction[] more) => new()
        {
            Index = index,
            Field34 = (byte)MenuState.Idle,
            Field35 = (byte)MenuState.Idle,
            States = new[]
            {
                new MenuItemState { Code = (int)MenuState.Focused, Kind = glow.Kind, Field2 = glow.Field2, WidgetIndex = glow.WidgetIndex, WidgetFlags = glow.WidgetFlags }
            },
            Links = Array.Empty<MenuLink>(),
            Actions = new List<MenuAction>
            {
                new()
                {
                    Kind = (ushort)MenuKeys.A, RectX = (short)MathF.Floor(x0), RectY = (short)MathF.Floor(bottom + 192),
                    RectW = (short)MathF.Ceiling(x1), RectH = (short)MathF.Ceiling(top + 192), Calls = new[] { (0, call) },
                    TargetPage = 0xFF, Item = 0xFFFF
                }
            }.Concat(more).ToList()
        };

        // a touch-only item (the X boxes, the page arrows)
        private static MenuItem GameTouch(int index, float x0, float top, float x1, float bottom, int call) => new()
        {
            Index = index,
            Field34 = (byte)MenuState.Idle,
            Field35 = (byte)MenuState.Idle,
            States = Array.Empty<MenuItemState>(),
            Links = Array.Empty<MenuLink>(),
            Actions = new[] { TouchRect(x0, bottom + 192, x1, top + 192, call) }
        };

        // InstallImportCards, after the cards: the game page's items
        private void InstallImportGamePage(MenuItemState glow)
        {
            if (_importGameRows == null) return;
            MenuPage page = _file.Pages[_importGameRows.Page];
            s_gamePage = page.Index;
            _importGameRows.Source = () => new List<RowModel>();
            string[] frame = { "blackdrop", "topdrop", "toplogo", "toplogoR" };
            HiddenOn(page.Index).AddRange(page.Items.Take(_p.Frame.Length)
                .Where(it => frame.Any(n => HasWidget(it, n)) || (it.IsText && it.Y >= 300)).Select(it => it.Index));
            _gameSettle = page.Items.Take(_p.Frame.Length).FirstOrDefault(it => HasWidget(it, "backicon"))?.Index ?? -1;

            _gPick = page.AddItem(i => GameInput(i, glow, GX(PickX), GY(PickY), GX(PickX + PickW), GY(PickY + PickH), GamePickCall)).Index;
            _gKey = page.AddItem(i => GameInput(i, glow, GX(KeyX), GY(StripY), GX(KeyX + KeyW), GY(StripY + StripH), GameKeyCall)).Index;
            _gAll = page.AddItem(i => GameInput(i, glow, GX(AllX + 10), GY(StripY + 6), GX(AllX + 10 + AllPillW), GY(StripY + 70), GameAllCall)).Index;
            _gUpd = page.AddItem(i => GameInput(i, glow, GX(UpdX), GY(StripY + 6), GX(UpdX + AllPillW), GY(StripY + 70), GameUpdateCall)).Index;
            for (int k = 0; k < GameTiles; k++)
            {
                (float x, float y) = TileAt(k);
                int tile = k;
                _gTile[k] = page.AddItem(i => GameInput(i, glow, GX(x), GY(y), GX(x + TileW), GY(y + TileH), GameTileCall + tile,
                    Call(MenuKeys.X, GameTileDeleteCall + tile))).Index;
            }
            for (int k = 0; k < GameTiles; k++)
            {
                // after the tiles, so a tap on the X box is the X box's (the engine tries the last items first)
                (float x, float y) = TileAt(k);
                int tile = k;
                _gTileX[k] = page.AddItem(i => GameTouch(i, GX(x + TileW - 60), GY(y + 8), GX(x + TileW - 8), GY(y + 60), GameTileDeleteCall + tile)).Index;
            }
            // the arrows beside the title move with its width: their taps take the band either side of it
            _gPrev = page.AddItem(i => GameTouch(i, 128 - 130, -2, 128 - 40, -26, GamePrevCall)).Index;
            _gNext = page.AddItem(i => GameTouch(i, 128 + 40, -2, 128 + 130, -26, GameNextCall)).Index;
        }

        // the page's own left / right (RecompMenus.cs, the game page's BuildRowsPage): along the strip and along a row of
        // tiles. Page actions, not the items': an item's action would make its select sound, and the D-pad's nearest
        // item right of PICK is a tile while the wii key box is away
        private static IEnumerable<MenuAction> ImportGamePageActions()
            => new[] { Call(MenuKeys.Right, GameRightCall), Call(MenuKeys.Left, GameLeftCall) };

        private void ReadGame()
        {
            string id = _importGame;
            List<ImportItemInfo>? items = Import.Game(id)?.Items.ToList();
            _gameAll = items ?? new();
            _gameSongs = _gameAll.Where(ImportGames.IsSong).ToList();
            _gameStale = _gameAll.Count(ImportGames.NeedsUpdate);
            _gameItems = items?.Where(i => !ImportGames.IsSong(i)).ToList();
            if (_gameItems != null && _gameSongs.Count > 0) _gameItems.Add(ImportGames.SongsTile(_gameSongs));
            _gameSource = Import.Source(id);
            // the key box: the Wii key for a Wii disc, the player's Switch keys for a Switch game file (Prime 4)
            _switchKeys = Import.TakesSwitchFiles(id);
            _keyShown = _switchKeys || Import.TakesWiiDiscs(id);
            _keyNeeded = _keyShown && (_switchKeys ? Import.SwitchKeysNeeded(id) : Import.WiiKeyNeeded(id));
            _hasKey = _switchKeys ? Import.HasSwitchKeys : Import.HasWiiKey;
        }

        // RecompMenus.OnPageEntered (by EnterImportCards): the first ten; the focus on PICK until a disc is picked, else
        // the first tile
        private void EnterImportGamePage(MenuEngine menu, int page)
        {
            if (page != s_gamePage || s_gamePage < 0) return;
            ApplyGameSounds(menu.Sounds);
            _tileOffset = 0;
            _tileFocus = 0;
            ReadGame();
            foreach (int item in GameItems()) menu.SetState(item, MenuState.Idle);
            int songs = _songsReturn ? _gameItems?.FindIndex(i => i.Kind == ImportGames.SongsTileKind) ?? -1 : -1;
            _songsReturn = false;
            if (songs >= 0)
            {
                // back from the song list: the MUSIC tile's page, the tile focused
                _tileOffset = songs / GameTiles * GameTiles;
                _tileFocus = songs % GameTiles;
                ShowGame(menu);
                menu.SetState(_gTile[_tileFocus], MenuState.Focused);
                return;
            }
            ShowGame(menu);
            menu.SetState(_gameSource == null || (_gameItems?.Count ?? 0) == 0 ? _gPick : _gTile[0], MenuState.Focused);
        }

        private IEnumerable<int> GameItems() => new[] { _gPick, _gKey, _gAll, _gUpd, _gPrev, _gNext }.Concat(_gTile).Concat(_gTileX).Where(i => i >= 0);

        // the tiles past the list's end hidden, the X boxes of the imported ones shown, the wii key box only while the disc
        // needs one, the arrows while there's more than one page; the title's page number. The focus stays where it is
        // unless its item went: then the tile it was on, or the last one there is
        private void ShowGame(MenuEngine menu)
        {
            int count = _gameItems?.Count ?? 0, pages = GamePages;
            if (_tileOffset >= pages * GameTiles) _tileOffset = (pages - 1) * GameTiles;
            void Show(int item, bool on)
            {
                if (item < 0) return;
                if (!on) menu.SetState(item, MenuState.Hidden);
                else if (menu.ItemState(item) == MenuState.Hidden) menu.SetState(item, MenuState.Idle);
            }
            for (int k = 0; k < GameTiles; k++)
            {
                int i = _tileOffset + k;
                Show(_gTile[k], i < count);
                Show(_gTileX[k], i < count && ImportGames.Imported(_gameItems![i]));
            }
            Show(_gKey, _keyNeeded);
            Show(_gPrev, pages > 1);
            Show(_gNext, pages > 1);
            if (_importGameRows is { Header: >= 0 } rows)
            {
                _strings.Set(rows.Header, pages > 1 ? $"{rows.Title} {_tileOffset / GameTiles + 1}/{pages}" : rows.Title);
            }
            int focused = menu.FocusedItem();
            if (focused < 0 || menu.ItemState(focused) == MenuState.Hidden) FocusTile(menu);
        }

        // the tile focused last, or the last one on the page; PICK when there are none
        private void FocusTile(MenuEngine menu)
        {
            int shown = Math.Clamp((_gameItems?.Count ?? 0) - _tileOffset, 0, GameTiles);
            menu.SetState(shown == 0 ? _gPick : _gTile[Math.Min(_tileFocus, shown - 1)], MenuState.Focused);
        }

        // as the cards: like the main menu's adventure
        private void ApplyGameSounds(MenuSounds sounds)
        {
            if (ReferenceEquals(sounds, _gameSounds)) return;
            _gameSounds = sounds;
            if (_soundCampaign < 0) return;
            foreach (int item in new[] { _gPick, _gKey, _gAll, _gUpd }.Concat(_gTile))
            {
                if (item >= 0) sounds.ItemLike(s_gamePage, item, MainMenuPage, _soundCampaign);
            }
        }

        // an import ended, a disc or the key was picked, something was deleted (Import.Changed, after the rows page's own
        // answer, which set the title back): the page reads again
        private void OnGamePageChanged()
        {
            if (s_gamePage < 0) return;
            MenuEngine? menu = _stepMenu;
            if (menu?.Page?.Index != s_gamePage) return;
            ReadGame();
            ShowGame(menu);
        }

        // Returns true when the call was the game page's (ImportCardsCall)
        private bool ImportGamePageCall(MenuEngine menu, int call)
        {
            if (s_gamePage < 0 || menu.Page?.Index != s_gamePage) return false;
            string id = _importGame;
            switch (call)
            {
            case DeleteAllUp:
                // up from DELETE ALL: a tile (the rows page's answer looks for a row)
                FocusTile(menu);
                return true;
            case PageNext:
            case PagePrev:
            case GameNextCall:
            case GamePrevCall:
                {
                    // wraps round, as the rows pages did
                    int pages = GamePages;
                    if (pages < 2) return true;
                    bool next = call is PageNext or GameNextCall;
                    _tileOffset = (_tileOffset / GameTiles + (next ? 1 : pages - 1)) % pages * GameTiles;
                    ShowGame(menu);
                    return true;
                }
            case GamePickCall:
                Import.PickDisc(id);
                return true;
            case GameKeyCall:
                if (_keyNeeded && _switchKeys) Import.PickSwitchKeys();
                else if (_keyNeeded) Import.PickWiiKey();
                return true;
            case GameAllCall:
                Import.ImportAll(id);
                return true;
            case GameUpdateCall:
                Import.UpdateAll(id);
                return true;
            case GameRightCall:
            case GameLeftCall:
                {
                    // PICK, the wii key box (while it's there), IMPORT ALL, UPDATE ALL; a tile to the next on its row. The
                    // ends stay put (the engine has already made the direction's sound)
                    int step = call == GameRightCall ? 1 : -1, focused = menu.FocusedItem();
                    int[] strip = _keyNeeded ? new[] { _gPick, _gKey, _gAll, _gUpd } : new[] { _gPick, _gAll, _gUpd };
                    int at = Array.IndexOf(strip, focused), tile = Array.IndexOf(_gTile, focused);
                    if (at >= 0 && at + step >= 0 && at + step < strip.Length) menu.SetState(strip[at + step], MenuState.Focused);
                    else if (tile >= 0 && tile % GameColumns + step >= 0 && tile % GameColumns + step < GameColumns
                        && menu.ItemState(_gTile[tile + step]) != MenuState.Hidden)
                    {
                        menu.SetState(_gTile[tile + step], MenuState.Focused);
                    }
                    return true;
                }
            }
            bool delete = call >= GameTileDeleteCall && call < GameTileDeleteCall + GameTiles;
            if (!delete && (call < GameTileCall || call >= GameTileCall + GameTiles)) return false;
            int k = call - (delete ? GameTileDeleteCall : GameTileCall), i = _tileOffset + k;
            if (_gameItems == null || i >= _gameItems.Count) return true;
            ImportItemInfo item = _gameItems[i];
            if (item.Kind == ImportGames.SongsTileKind)
            {
                // the MUSIC tile: A its song list, X every imported song (after the ask box)
                List<ImportItemInfo> made = _gameSongs.Where(ImportGames.Imported).ToList();
                if (!delete)
                {
                    _tileFocus = k;
                    OpenSongList(id);
                }
                else if (made.Count > 0) Import.Clear(id, made, made.Count == 1 ? ImportGames.ItemName(made[0]) : $"{made.Count} songs");
                return true;
            }
            if (!delete)
            {
                _tileFocus = k;
                Import.ImportOne(id, item);
            }
            else if (ImportGames.Imported(item)) Import.Clear(id, new[] { item }, ImportGames.ItemName(item));
            return true;
        }

        // ---- drawing (DrawImportCards) ----

        private void DrawImportGamePage(MenuEngine menu, List<WidgetTri> tris)
        {
            _drawMenu = menu;
            _out = tris;
            // our items' glows: lifted out of the engine's triangles, drawn again round each in its place in the order
            var mine = new HashSet<int>(new[] { _gPick, _gKey, _gAll, _gUpd }.Concat(_gTile));
            var glows = new Dictionary<int, List<WidgetTri>>();
            for (int t = tris.Count - 1; t >= 0; t--)
            {
                int it = tris[t].Item;
                if (!mine.Contains(it)) continue;
                if (!glows.TryGetValue(it, out List<WidgetTri>? g)) glows[it] = g = new List<WidgetTri>();
                g.Add(tris[t]);
                tris.RemoveAt(t);
            }
            bool settled = menu.PendingPage == -1 && (_gameSettle < 0
                || menu.ItemState(_gameSettle) == MenuState.Idle && menu.ItemCode(_gameSettle) == (int)MenuState.Idle);
            if (!settled) return;
            for (int k = 0; k < GameTiles; k++)
            {
                if (menu.ItemState(_gTile[k]) == MenuState.Focused) _tileFocus = k;
            }
            if (_gameItems == null) ReadGame();
            try
            {
                DrawGameStrip(glows);
                int count = _gameItems?.Count ?? 0;
                for (int k = 0; k < GameTiles && _tileOffset + k < count; k++) DrawTile(k, _gameItems![_tileOffset + k], glows);
                if (GamePages > 1 && _importGameRows != null)
                {
                    // beside the title, whatever its length
                    string title = $"{_importGameRows.Title} {_tileOffset / GameTiles + 1}/{GamePages}";
                    float gap = menu.Font.Measure(title) / 2 + TitleArrowGap;
                    Arrow(false, 128 - gap - ArrowW, TitleArrowTop, _gPrev);
                    Arrow(true, 128 + gap, TitleArrowTop, _gNext);
                }
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or ArgumentException or KeyNotFoundException)
            {
                // a missing ROM model (an older extraction): the page stays usable without its look
                Console.Error.WriteLine("import game page: can't draw (" + ex.Message + ")");
            }
        }

        private const float TitleArrowGap = 6, TitleArrowTop = -9.8f;

        // the panel, "your disc", the wii key box, IMPORT ALL and its bar (all on PICK's item: it's always there)
        private void DrawGameStrip(Dictionary<int, List<WidgetTri>> glows)
        {
            int tag = _gPick;
            ImportGames.GameRow row = ImportGames.Row(_importGame);
            // the panel: the backdrop darkened, a thin dark-orange edge (as the cards)
            float x0 = GX(PanelX0), top = GY(PanelY0), x1 = GX(PanelX1), bottom = GY(PanelY1);
            Fill(x0, top, x1, bottom, 0, 0, 0, 0.58f, tag);
            var edge = (R: 0.5f, G: 0.22f, B: 0.02f);
            Fill(x0, top, x1, top - CardEdge, edge.R, edge.G, edge.B, 0.85f, tag);
            Fill(x0, bottom + CardEdge, x1, bottom, edge.R, edge.G, edge.B, 0.85f, tag);
            Fill(x0, top - CardEdge, x0 + CardEdge, bottom + CardEdge, edge.R, edge.G, edge.B, 0.85f, tag);
            Fill(x1 - CardEdge, top - CardEdge, x1, bottom + CardEdge, edge.R, edge.G, edge.B, 0.85f, tag);

            // your disc: the emblem, which discs it takes (or the one picked), PICK / CHANGE
            Plate(StripX, StripY, StripW, StripH, 90, tag, greyed: false);
            DrawEmblem(row, GX(StripX + 14), GY(StripY + 14), GL(104), true, tag);
            // the picked disc's own banner (ImportBanners: a GameCube disc's) in place of "your disc", 2.4 times its size
            string? banner = ImportBanners.For(ImportRoot, _gameSource);
            bool shown = banner != null && DrawPng(banner, GX(StripX + 136), GY(StripY + 6), GX(StripX + 136 + 96 * 2.4f), GY(StripY + 6 + 32 * 2.4f), tag);
            // (Prime 4: its game file or a RomFS folder)
            bool folder = Import.FolderOnly(_importGame) || _gameSource?.Folder == true;
            if (!shown) ScaledText(folder || _switchKeys ? "your files" : "your disc", GX(StripX + 140), GY(StripY + 20), 0, Orange, tag, 1);
            string second = _gameSource == null ? row.Platform + (folder ? " romfs folder" : _switchKeys ? " game file" : " disc")
                : FitWidth(_gameSource.Name.ToLowerInvariant(), GL(NameWidth), 0.62f);
            ScaledText(second, GX(StripX + 140), GY(StripY + (shown ? 88 : 80)), 0, _gameSource == null ? Pale : White, tag, 0.62f);
            Box(GX(PickX), GY(PickY), GX(PickX + PickW), GY(PickY + PickH), _gPick);
            Glow(glows, _gPick, GX(PickX), GY(PickY), GX(PickX + PickW), GY(PickY + PickH), _gPick);
            ScaledText(_gameSource == null ? "pick" : "change", GX(PickX + PickW / 2), GY(PickY + 12), 2, Orange, _gPick, 0.8f);

            // the wii key (or the switch keys): greyed while the disc needs none (a GameCube disc, an .rvz / .wia, a game no
            // Wii disc makes, a dumped folder)
            bool key = _keyNeeded;
            int keyTag = key ? _gKey : tag;
            Plate(KeyX, StripY, KeyW, StripH, 90, keyTag, greyed: !key);
            if (key) Glow(glows, _gKey, GX(KeyX), GY(StripY), GX(KeyX + KeyW), GY(StripY + StripH), _gKey);
            Picture(ImportCardPictures.Key, GX(KeyX + 14), GY(StripY + 30), GL(72), off: !key, keyTag);
            ScaledText(_switchKeys ? "switch keys" : "wii key", GX(KeyX + 100), GY(StripY + 26), 0, key ? Orange : Grey, keyTag, _switchKeys ? 0.66f : 0.8f);
            ScaledText(key ? _hasKey ? "set" : "none" : "not needed", GX(KeyX + 100), GY(StripY + 76), 0, key ? White : Grey, keyTag, 0.62f);

            // IMPORT ALL and UPDATE ALL (the lobby's START GAME pill each; IMPORT ALL dimmed until there's a disc, UPDATE ALL
            // until an update made something stale: its red badge then), how many are imported, the bar
            bool live = _gameSource != null;
            float ax = GX(AllX + 10), atop = GY(StripY + 6), aw = GL(AllPillW), ah = GL(64);
            Model(@"startgame\startgame", ax, atop, aw, ah, _gAll, alpha: live ? 1 : 0.4f);
            Glow(glows, _gAll, ax, atop, ax + aw, atop - ah, _gAll);
            ScaledText("import all", ax + aw * 0.38f, GY(StripY + 27), 2, live ? Orange : Grey, _gAll, 0.5f);
            bool stale = _gameStale > 0;
            float ux = GX(UpdX);
            Model(@"startgame\startgame", ux, atop, aw, ah, _gUpd, alpha: stale ? 1 : 0.4f);
            Glow(glows, _gUpd, ux, atop, ux + aw, atop - ah, _gUpd);
            ScaledText("update all", ux + aw * 0.38f, GY(StripY + 27), 2, stale ? Orange : Grey, _gUpd, 0.5f);
            if (stale) Picture(ImportCardPictures.Alert, GX(UpdX + AllPillW - 22), GY(StripY - 10), GL(30), off: false, _gUpd); // (over the pill's right end)
            int done = _gameAll.Count(i => i.Status == ItemImportStatus.Complete), total = _gameAll.Count;
            ScaledText($"{done}/{total}", GX(AllX + AllW - 10), GY(StripY + 86), 1, White, tag, 0.8f);
            Bar(GX(AllX + 10), GY(StripY + 88), GX(AllX + AllW - 170), done, total, tag); // clear of a two-digit count
        }

        // a tile: the item's pictogram on a dark plate, its name (white once imported), its status, its X box once imported
        private void DrawTile(int k, ImportItemInfo item, Dictionary<int, List<WidgetTri>> glows)
        {
            int tag = _gTile[k];
            (float x, float y) = TileAt(k);
            bool made = ImportGames.Imported(item);
            Plate(x, y, TileW, TileH, 100, tag, greyed: false);
            Glow(glows, tag, GX(x), GY(y), GX(x + TileW), GY(y + TileH), tag);
            Fill(GX(x + 10), GY(y + 10), GX(x + TileW - 10), GY(y + 138), 0, 0, 0, 150 / 255f, tag);
            // its render once there is one (ImportRenders), else the kind's pictogram
            if (!TileRender(item, x + 10, y + 10, TileW - 20, 128, tag)) Picture(KindPicture(item), GX(x + (TileW - 112) / 2), GY(y + 18), GL(112), off: false, tag);
            // the row's two-line label on one line when it fits ("power suit"), else as two ("power suit / morph ball")
            string one = ImportGames.ItemName(item);
            string[] lines = _drawMenu!.Font.Measure(one) * 0.55f <= GL(TileW - 30) ? new[] { one } : ImportGames.ItemLabel(item).Split('\n');
            float ty = y + 146 + (lines.Length == 1 ? 14 : 0);
            for (int l = 0; l < lines.Length && l < 2; l++, ty += 30)
            {
                ScaledText(lines[l], GX(x + TileW / 2), GY(ty), 2, made ? White : Pale, tag, 0.55f);
            }
            float vy = y + TileH - 54;
            Box(GX(x + 50), GY(vy), GX(x + TileW - 50), GY(vy + 42), tag);
            string value = ImportGames.ItemValue(_importGame, item).ToLowerInvariant();
            ScaledText(value, GX(x + TileW / 2), GY(vy + 7), 2, item.Status == ItemImportStatus.Complete ? Pale : Orange, tag, 0.6f);
            // import again: the cards' red badge in the corner across from the X (the MUSIC tile: any of its songs)
            bool stale = item.Kind == ImportGames.SongsTileKind ? _gameSongs.Any(ImportGames.NeedsUpdate) : ImportGames.NeedsUpdate(item);
            if (stale) Picture(ImportCardPictures.Alert, GX(x + 12), GY(y + 12), GL(44), off: false, tag);
            if (!made) return;
            Fill(GX(x + TileW - 52), GY(y + 16), GX(x + TileW - 16), GY(y + 52), 0, 0, 0, 160 / 255f, tag);
            Box(GX(x + TileW - 52), GY(y + 16), GX(x + TileW - 16), GY(y + 52), tag);
            ScaledText("x", GX(x + TileW - 34), GY(y + 20), 2, Orange, tag, 0.6f);
        }

        // a render on a tile's plate (mockup pixels), per the mockup: a body's head and shoulders across the plate (rows
        // 78 on of its 384 x 576), anything else as high as the plate, centred
        private bool TileRender(ImportItemInfo item, float x, float y, float w, float h, int tag)
        {
            string? png = ImportRenders.For(ImportRoot, item);
            if (png == null || PngTexture(png) is not UiTexture tex) return false;
            float image = tex.Height / (float)tex.Width;
            if (image > 1.2f)
            {
                float v0 = 78f / 384 / image;
                Quad(tex.Id, GX(x), GY(y), GX(x + w), GY(y + h), 0, v0, 1, v0 + h / w / image, 1, 1, 1, 1, tag);
                return true;
            }
            float dw = MathF.Min(w, h / image);
            Quad(tex.Id, GX(x + (w - dw) / 2), GY(y), GX(x + (w + dw) / 2), GY(y + h), 0, 0, 1, 1, 1, 1, 1, 1, tag);
            return true;
        }

        private static string KindPicture(ImportItemInfo item) => ImportGames.IsSuit(item) ? ImportCardPictures.Suit
            : ImportGames.IsBall(item) ? ImportCardPictures.Ball
            : item.Kind == "arenas" ? ImportCardPictures.Radar
            : item.Kind is "music" or ImportGames.SongsTileKind ? ImportCardPictures.Music
            : ImportCardPictures.Creature;

        // a dark box in the setup boxes' outline (mockup pixels; alpha out of 255)
        private void Plate(float x, float y, float w, float h, float alpha, int tag, bool greyed)
        {
            Fill(GX(x), GY(y), GX(x + w), GY(y + h), 0, 0, 0, alpha / 255f, tag);
            Box(GX(x), GY(y), GX(x + w), GY(y + h), tag, greyed);
        }

        // `text` cut short ("...") to fit `width` units at `scale`
        private string FitWidth(string text, float width, float scale)
        {
            MenuFont font = _drawMenu!.Font;
            if (font.Measure(text) * scale <= width) return text;
            for (int n = text.Length - 1; n > 0; n--)
            {
                string cut = text[..n].TrimEnd() + "...";
                if (font.Measure(cut) * scale <= width) return cut;
            }
            return "";
        }
    }
}
