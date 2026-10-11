using System;
using System.Collections.Generic;

// Places the DS menu's two screens on one screen of ANY aspect ratio. The menu engine produces triangles in menu space
// (both DS screens, Y up, the seam at y = 0); a layout cuts that space into regions and puts each region on the canvas
// with an anchor (a point of the canvas as a fraction of its size), a pivot (the matching point of the region), an
// offset and a scale. Sizes are in canvas UNITS: the canvas is always VirtualHeight units tall and as wide as its
// aspect ratio makes it (426.7 at 16:9, 533 at 20:9, 320 at 4:3), so anchoring to edges is what makes a layout adapt.
// Each menu item goes wholly to the first region that contains its centre, so no widget or label is ever cut in two.
namespace MphRecomp.Frontend
{
    public enum LayoutKind { SideBySide, Overlay, MainFamily, Stacked, Setup, Ending }

    public sealed class LayoutRegion
    {
        public string Name { get; init; } = "";
        // source rectangle in menu space (Y up)
        public float X0 { get; init; }
        public float Y0 { get; init; }
        public float X1 { get; init; }
        public float Y1 { get; init; }
        // destination: canvas anchor (0..1, Y down), region pivot (0..1 from the region's top-left), offset + scale
        public float AnchorX { get; init; } = 0.5f;
        public float AnchorY { get; init; } = 0.5f;
        public float PivotX { get; init; } = 0.5f;
        public float PivotY { get; init; } = 0.5f;
        public float OffsetX { get; init; }
        public float OffsetY { get; init; }
        public float Scale { get; init; } = 1;
        // only texts (true) or only widgets (false) go to this region; null: both
        public bool? Texts { get; init; }

        public bool Contains(float x, float y) => x >= X0 && x <= X1 && y >= Y0 && y <= Y1;
    }

    // A resolved placement: canvas pixel = (menu point - SrcOrigin) * (Sx, -Sy) + Dst
    public readonly record struct Placement(float SrcX, float SrcY, float Scale, float DstX, float DstY)
    {
        public (float X, float Y) ToCanvas(float x, float y) => (DstX + (x - SrcX) * Scale, DstY - (y - SrcY) * Scale);
        public (float X, float Y) ToMenu(float cx, float cy) => (SrcX + (cx - DstX) / Scale, SrcY - (cy - DstY) / Scale);
    }

    public sealed class MenuLayout
    {
        public const float VirtualHeight = 240;

        public LayoutKind Kind { get; }
        public IReadOnlyList<LayoutRegion> Regions { get; }

        // the canvas width (units) the composition needs at its regions' own scales: a narrower canvas shrinks every
        // region's scale and offset by the same factor (0 = never)
        private readonly float _needWidth;

        private MenuLayout(LayoutKind kind, IReadOnlyList<LayoutRegion> regions, float needWidth = 0)
        {
            Kind = kind;
            Regions = regions;
            _needWidth = needWidth;
        }

        // the two DS screens in menu space
        public const float TopY0 = 0, TopY1 = 192, BottomY0 = -192, BottomY1 = 0;

        // Pick a layout for a page from its data: the pages that carry the game's logo (title, main menu, options,
        // file select, records...) get the combined main-menu composition; the boot logos overlay; the rest sit side
        // by side until they get their own composition.
        // `dual`: the player chose the side-by-side menu layout in RECOMP SETTINGS
        public static MenuLayout For(MenuFile file, MenuPage page, bool dual = false)
        {
            if (page.Index == 0) return Stacked(); // the white screen shows both screens at once (Nintendo / ACTIMAGINE)
            if (page.Index <= 5) return Overlay();
            if (page.Index == FrontendSession.OptionsPage && RecompMenus.OptionsWidened) return Options(dual);
            // one wide screen in either menu layout: the cards don't fit a DS screen
            if (RecompMenus.IsImportCardsPage(page)) return ImportCards();
            bool extra = false; // the recomp's own additions to a ROM page, placed right of the touch screen
            foreach (MenuItem item in page.Items)
            {
                if (item.X >= 256) extra = true;
            }
            if (dual) return SideBySide(extra);
            // 14..17 are overlays the menu shows on top of its logo pages (BEGIN GAME and its fade, the connection
            // pages): they go where the logo pages' controls go
            if (page.Index >= 14 && page.Index <= 17) return MainFamily();
            if (MultiplayerMenu.IsSetupPage(page)) return Setup();
            if (MultiplayerMenu.IsCardPage(page)) return License();
            if (RecompMenus.IsReplaysPage(page)) return Replays();
            if (RecompMenus.IsOnlinePage(page)) return Replays(); // ONLINE PLAY's pages: the same widened setup look (OnlineMenus.cs)
            // the credits (6-12) and the ending's results (49 / 50) as one screen each (owner 2026-10-07, Reports R3 / R6)
            if (page.Index is >= GameEnding.CreditsPage and <= 12) return Credits();
            if (page.Index is GameEnding.GoodPage or GameEnding.BadPage) return EndingResults();
            // the keypad and the name keyboard (owner 2026-10-07 ~22:27: "the enter address [and the keyboard] should both be
            // wide one screen as well")
            if (MultiplayerMenu.IsKeypadPage(page)) return Keypad();
            if (MultiplayerMenu.IsKeyboardPage(page)) return Keyboard();
            foreach (MenuItem item in page.Items)
            {
                foreach (MenuItemState s in item.States)
                {
                    if (s.WidgetIndex >= 0 && file.Widgets[s.WidgetIndex].ModelPath.EndsWith("toplogo_Model.bin", StringComparison.OrdinalIgnoreCase))
                    {
                        return MainFamily(actionsAboveRight: page.Index == FileSelect.Page, pagingLeft: page.Index == MovieGallery.SecondPage);
                    }
                }
            }
            return SideBySide(extra);
        }

        // The two DS screens next to each other; with `extra`, smaller, and the recomp's column after the touch screen.
        public static MenuLayout SideBySide(bool extra = false)
        {
            if (!extra)
            {
                return new(LayoutKind.SideBySide, new[]
                {
                    new LayoutRegion { Name = "top", X0 = 0, Y0 = TopY0, X1 = 256, Y1 = TopY1, AnchorX = 0.5f, PivotX = 1, OffsetX = -4, Scale = 0.82f },
                    new LayoutRegion { Name = "bottom", X0 = 0, Y0 = BottomY0, X1 = 256, Y1 = BottomY1, AnchorX = 0.5f, PivotX = 0, OffsetX = 4, Scale = 0.82f },
                });
            }
            const float s = 0.68f;
            return new(LayoutKind.SideBySide, new[]
            {
                new LayoutRegion { Name = "top", X0 = 0, Y0 = TopY0, X1 = 256, Y1 = TopY1, AnchorX = 0.5f, PivotX = 1, OffsetX = -4 - 38 * s, Scale = s },
                new LayoutRegion { Name = "bottom", X0 = 0, Y0 = BottomY0, X1 = 256, Y1 = BottomY1, AnchorX = 0.5f, PivotX = 0, OffsetX = 4 - 38 * s, Scale = s },
                new LayoutRegion { Name = "extra", X0 = 256, Y0 = BottomY0, X1 = 332, Y1 = BottomY1, AnchorX = 0.5f, PivotX = 0, OffsetX = 4 + 218 * s, Scale = s },
            });
        }

        // The two screens one above the other, as the DS holds them, fitting the canvas height.
        public static MenuLayout Stacked() => new(LayoutKind.Stacked, new[]
        {
            new LayoutRegion { Name = "top", X0 = 0, Y0 = TopY0, X1 = 256, Y1 = TopY1, PivotY = 1, Scale = 0.62f },
            new LayoutRegion { Name = "bottom", X0 = 0, Y0 = BottomY0, X1 = 256, Y1 = BottomY1, PivotY = 0, Scale = 0.62f },
        });

        public static MenuLayout Overlay() => new(LayoutKind.Overlay, new[]
        {
            new LayoutRegion { Name = "top", X0 = 0, Y0 = TopY0, X1 = 256, Y1 = TopY1, Scale = 1.2f },
            new LayoutRegion { Name = "bottom", X0 = 0, Y0 = BottomY0, X1 = 256, Y1 = BottomY1, Scale = 1.2f },
        });

        // Logo across the top, the touch screen's controls in the middle, the top screen's description band at the
        // bottom, and the touch screen's footer row (back / save / paging) pinned to the canvas corners.
        // actionsAboveRight: the file select's COPY / DELETE (the middle of its footer row, under the description band
        // otherwise) go in a row above MOVIES, DELETE over it, in the game's left-to-right order.
        // pagingLeft: MOVIES' second page, whose PREVIOUS arrow (x ~148-179) sits left of its label (x 187): the corner
        // takes both, so the arrow stays beside its label (it fell in the footer's middle before)
        public static MenuLayout MainFamily(bool actionsAboveRight = false, bool pagingLeft = false) => new(LayoutKind.MainFamily, new[]
        {
            new LayoutRegion { Name = "footer-left", X0 = 0, Y0 = -192, X1 = 64, Y1 = -160, AnchorX = 0, AnchorY = 1, PivotX = 0, PivotY = 1, OffsetX = 6, OffsetY = -4, Scale = 0.9f },
            new LayoutRegion { Name = "footer-right", X0 = pagingLeft ? 140 : 176, Y0 = -192, X1 = 256, Y1 = -160, AnchorX = 1, AnchorY = 1, PivotX = 1, PivotY = 1, OffsetX = -6, OffsetY = -4, Scale = 0.9f },
            actionsAboveRight
                // x 161 (DELETE) lands where footer-right puts x 228 (MOVIES): 13 menu pixels further in than its right edge
                ? new LayoutRegion { Name = "footer-mid", X0 = 64, Y0 = -192, X1 = 176, Y1 = -160, AnchorX = 1, AnchorY = 1, PivotX = 1, PivotY = 1, OffsetX = -6 - 13 * 0.9f, OffsetY = -4 - 40 * 0.9f, Scale = 0.9f }
                : new LayoutRegion { Name = "footer-mid", X0 = 64, Y0 = -192, X1 = 176, Y1 = -160, AnchorX = 0.5f, AnchorY = 1, PivotX = 0.5f, PivotY = 1, OffsetY = -4, Scale = 0.9f },
            new LayoutRegion { Name = "extra", X0 = 256, Y0 = -160, X1 = 332, Y1 = 0, AnchorX = 1, AnchorY = 0, PivotX = 1, PivotY = 0, OffsetX = -8, OffsetY = 64, Scale = 0.76f },
            new LayoutRegion { Name = "controls", X0 = 0, Y0 = -160, X1 = 256, Y1 = 0, AnchorX = 0.5f, AnchorY = 0, PivotX = 0.5f, PivotY = 0, OffsetY = 64, Scale = 0.76f },
            new LayoutRegion { Name = "logo", X0 = 0, Y0 = 60, X1 = 256, Y1 = 192, AnchorX = 0.5f, AnchorY = 0, PivotX = 0.5f, PivotY = 0, Scale = 0.5f },
            new LayoutRegion { Name = "info", X0 = 0, Y0 = 0, X1 = 256, Y1 = 60, AnchorX = 0.5f, AnchorY = 1, PivotX = 0.5f, PivotY = 1, OffsetY = -2, Scale = 0.86f },
        });

        // The multiplayer setup pages (frame group 7, CREATE OR JOIN to the lobby) as ONE combined screen (owner
        // 2026-10-07): the touch screen's controls in the middle, the top screen's left boxes (SETTINGS / DESCRIPTION,
        // ADVANCED) pinned to the left edge and its right boxes (GAME MODE, ARENA with the preview, PLAYERS) to the right,
        // the menus' backdrop over the whole canvas. Below ~20:9 everything shrinks together to fit the width.
        public const float SetupSplitX = 124;

        public static MenuLayout Setup()
        {
            const float touch = 1.1f, side = 0.7f, margin = 4;
            return new(LayoutKind.Setup, new[]
            {
                new LayoutRegion { Name = "controls", X0 = 0, Y0 = BottomY0, X1 = 256, Y1 = BottomY1, AnchorX = 0.5f, AnchorY = 0.5f, Scale = touch },
                new LayoutRegion { Name = "left", X0 = 0, Y0 = TopY0, X1 = SetupSplitX, Y1 = TopY1, AnchorX = 0, PivotX = 0, OffsetX = margin, Scale = side },
                new LayoutRegion { Name = "right", X0 = SetupSplitX, Y0 = TopY0, X1 = 256, Y1 = TopY1, AnchorX = 1, PivotX = 1, OffsetX = -margin, Scale = side },
            }, needWidth: 256 * touch + 256 * side + 4 * margin);
        }

        // HUNTER LICENSE (page 60 as the multiplayer menu's card) as ONE combined screen (owner 2026-10-07: "I wanted a 16:9
        // screen"; 2026-10-08: "center everything ... center the license, reduce the size of the ranking points and next rank
        // info area ... put the edit name button in the corner in the right opposite the back button", "completely
        // eliminating the you are part"): the whole top screen (title, card, tabs, ranking points) in the middle at the
        // setup pages' touch scale (MultiplayerMenu.BindCard narrows the ranking box and hides "you are:"), the back arrow
        // and EDIT NAME in the bottom corners as the main menus' footer. The touch screen's empty roster panel (no roster
        // yet) has no region, so it isn't drawn; a one-point region at the popup's centre puts the game's box in the middle
        // of the canvas. Kind Setup: the same backdrop over the whole canvas. Below its width it all shrinks to fit.
        public static MenuLayout License()
        {
            const float scale = 1.1f, margin = 4;
            return new(LayoutKind.Setup, new[]
            {
                new LayoutRegion { Name = "back", X0 = 0, Y0 = -192, X1 = 64, Y1 = -150, AnchorX = 0, AnchorY = 1, PivotX = 0, PivotY = 1, OffsetX = 6, OffsetY = -4, Scale = 0.9f },
                // EDIT NAME (label x 81..135, y -193..-185, its icon above): 10 menu px in from the corner, as back's icon
                new LayoutRegion { Name = "edit", X0 = 70, Y0 = -203, X1 = 145, Y1 = -145, AnchorX = 1, AnchorY = 1, PivotX = 1, PivotY = 1, OffsetX = -6, OffsetY = -4, Scale = 0.9f },
                new LayoutRegion { Name = "popup", X0 = 127.5f, Y0 = -88.5f, X1 = 128.5f, Y1 = -87.5f, Scale = scale },
                new LayoutRegion { Name = "card", X0 = 0, Y0 = TopY0, X1 = 256, Y1 = TopY1, Scale = scale },
            }, needWidth: 256 * scale + 2 * margin);
        }

        // REPLAYS' list and details pages (RecompMenus.Replays.cs, Replay mode; owner 2026-10-08: the setup look without its
        // side panels, "we can take up more space there", and "a setting in the corner to turn replays on or off"): the
        // touch screen widened to x -60..316 (the CREATE OR JOIN panel stretched across it) in the middle at the setup
        // pages' touch scale, the back arrow and the record-replays setting in the bottom corners as HUNTER LICENSE's
        // footer. Kind Setup: the same backdrop over the whole canvas. Below its width it all shrinks to fit.
        public static MenuLayout Replays()
        {
            const float scale = 1.1f, margin = 4;
            return new(LayoutKind.Setup, new[]
            {
                // (back ends at y -164: the online 8-seat lobby's bottom-left TEAM box, centre y -158, stays with its card)
                new LayoutRegion { Name = "back", X0 = 0, Y0 = -192, X1 = 64, Y1 = -164, AnchorX = 0, AnchorY = 1, PivotX = 0, PivotY = 1, OffsetX = 6, OffsetY = -4, Scale = 0.9f },
                new LayoutRegion { Name = "corner", X0 = 330, Y0 = -200, X1 = 462, Y1 = -182, AnchorX = 1, AnchorY = 1, PivotX = 1, PivotY = 1, OffsetX = -6, OffsetY = -4, Scale = scale },
                new LayoutRegion { Name = "rows", X0 = -60, Y0 = -188, X1 = 316, Y1 = -4, Scale = scale },
            }, needWidth: 376 * scale + 2 * margin);
        }

        // IMPORT GAMES as five game cards (RecompMenus.ImportCards.cs; owner 2026-10-08, mockup B): the touch screen widened
        // to x -60..316 as REPLAYS' in the middle at the setup pages' touch scale (the cards and the title), the back arrow
        // and DELETE ALL in the bottom corners as the main menus' footer. The logo and the description band have no region,
        // so they aren't drawn. Kind MainFamily: the main menus' backdrop. Below its width it all shrinks to fit.
        public static MenuLayout ImportCards()
        {
            const float scale = 1.1f, margin = 4;
            return new(LayoutKind.MainFamily, new[]
            {
                new LayoutRegion { Name = "footer-left", X0 = 0, Y0 = -192, X1 = 64, Y1 = -160, AnchorX = 0, AnchorY = 1, PivotX = 0, PivotY = 1, OffsetX = 6, OffsetY = -4, Scale = 0.9f },
                new LayoutRegion { Name = "footer-right", X0 = 176, Y0 = -192, X1 = 256, Y1 = -160, AnchorX = 1, AnchorY = 1, PivotX = 1, PivotY = 1, OffsetX = -6, OffsetY = -4, Scale = 0.9f },
                new LayoutRegion { Name = "cards", X0 = -60, Y0 = -188, X1 = 316, Y1 = -4, Scale = scale },
            }, needWidth: 376 * scale + 2 * margin);
        }

        // OPTIONS (page 21) with its panel widened for IMPORT GAMES' own button (owner 2026-10-08: "make the options menu box
        // bigger and add a dedicated importer button"; RecompMenus.AddOptionsButtons): the main menus' composition with the
        // touch screen's controls region widened to x -60..316 as REPLAYS' (no "extra" column: the panel's right end is in
        // it). Side by side, the widened touch screen shrinks to a DS screen's width beside the top screen.
        public const float OptionsX0 = -60, OptionsX1 = 316;
        // footer-right starts at x 150, not 176: the privacy / code of conduct / 13+ line sits right-aligned there and is
        // wider than the ROM footer (owner 2026-10-10; RecompMenus.OnlineRules.cs). A tap only counts inside its region
        public const float OptionsFooterRightX0 = 150;

        public static MenuLayout Options(bool dual)
        {
            const float width = OptionsX1 - OptionsX0;
            if (dual)
            {
                const float s = 0.82f;
                return new(LayoutKind.SideBySide, new[]
                {
                    new LayoutRegion { Name = "top", X0 = 0, Y0 = TopY0, X1 = 256, Y1 = TopY1, AnchorX = 0.5f, PivotX = 1, OffsetX = -4, Scale = s },
                    new LayoutRegion { Name = "bottom", X0 = OptionsX0, Y0 = BottomY0, X1 = OptionsX1, Y1 = BottomY1, AnchorX = 0.5f, PivotX = 0, OffsetX = 4, Scale = s * 256 / width },
                });
            }
            const float controls = 0.76f, margin = 8;
            return new(LayoutKind.MainFamily, new[]
            {
                new LayoutRegion { Name = "footer-left", X0 = 0, Y0 = -192, X1 = 64, Y1 = -160, AnchorX = 0, AnchorY = 1, PivotX = 0, PivotY = 1, OffsetX = 6, OffsetY = -4, Scale = 0.9f },
                new LayoutRegion { Name = "footer-right", X0 = OptionsFooterRightX0, Y0 = -192, X1 = 256, Y1 = -160, AnchorX = 1, AnchorY = 1, PivotX = 1, PivotY = 1, OffsetX = -6, OffsetY = -4, Scale = 0.9f },
                new LayoutRegion { Name = "footer-mid", X0 = 64, Y0 = -192, X1 = 176, Y1 = -160, AnchorX = 0.5f, AnchorY = 1, PivotX = 0.5f, PivotY = 1, OffsetY = -4, Scale = 0.9f },
                new LayoutRegion { Name = "controls", X0 = OptionsX0, Y0 = -160, X1 = OptionsX1, Y1 = 0, AnchorX = 0.5f, AnchorY = 0, PivotX = 0.5f, PivotY = 0, OffsetY = 64, Scale = controls },
                new LayoutRegion { Name = "logo", X0 = 0, Y0 = 60, X1 = 256, Y1 = 192, AnchorX = 0.5f, AnchorY = 0, PivotX = 0.5f, PivotY = 0, Scale = 0.5f },
                new LayoutRegion { Name = "info", X0 = 0, Y0 = 0, X1 = 256, Y1 = 60, AnchorX = 0.5f, AnchorY = 1, PivotX = 0.5f, PivotY = 1, OffsetY = -2, Scale = 0.86f },
            }, needWidth: width * controls + 2 * margin);
        }

        // compositions shown to the owner before they're switched on (the tools' FE_PROPOSE=1)
        public static bool Proposals { get; set; }

        // The ending's results page (49 good / 50 bad) as ONE screen (owner 2026-10-07, Reports R6: "combine them so [we]
        // have all elements on one screen and for the background cycle back and forth between the two every five seconds
        // with a one and a half second fade"): the touch screen (the numbers, OK) at the canvas's full height, the top
        // screen's title band ("congratulations", "mission complete!") along its top, and behind them both DS pictures in
        // turn at the touch screen's size (the second one has the numbers' boxes drawn in: FrontendSession.DrawBackground).
        public const float EndingTitleY1 = 40;

        // The credits (pages 6-12) as ONE screen (owner 2026-10-07, Reports R3: "combine them like the nintendo presents
        // intro video did. they blended both screens ... put the text on one wide screen and make it left and right
        // columns"): their art (the logo and flare animations, drawn across both DS screens) blended into one, both
        // screens' parts in the middle of the canvas as Overlay() does for the boot logos; the top screen's text the left
        // column and the touch screen's the right, as wide as the canvas allows.
        public static MenuLayout Credits()
        {
            const float column = 0.82f, art = 1.2f;
            return new(LayoutKind.Overlay, new[]
            {
                new LayoutRegion { Name = "left", X0 = 0, Y0 = TopY0, X1 = 256, Y1 = TopY1, AnchorX = 0.5f, PivotX = 1, OffsetX = -4, Scale = column, Texts = true },
                new LayoutRegion { Name = "right", X0 = 0, Y0 = BottomY0, X1 = 256, Y1 = BottomY1, AnchorX = 0.5f, PivotX = 0, OffsetX = 4, Scale = column, Texts = true },
                new LayoutRegion { Name = "top art", X0 = 0, Y0 = TopY0, X1 = 256, Y1 = TopY1, Scale = art, Texts = false },
                new LayoutRegion { Name = "bottom art", X0 = 0, Y0 = BottomY0, X1 = 256, Y1 = BottomY1, Scale = art, Texts = false },
            });
        }

        public static MenuLayout EndingResults()
        {
            const float scale = 1.2f, half = 96 * scale;
            return new(LayoutKind.Ending, new[]
            {
                new LayoutRegion { Name = "numbers", X0 = 0, Y0 = BottomY0, X1 = 256, Y1 = BottomY1, Scale = scale },
                new LayoutRegion { Name = "title", X0 = 0, Y0 = TopY0, X1 = 256, Y1 = EndingTitleY1, PivotY = 0, OffsetY = -half, Scale = scale },
            });
        }

        // The join-by-address keypad (page 61, Vanilla multiplayer menu; Reports R3) as ONE screen: the touch screen
        // (ENTER ADDRESS, the keys, back and OK) in the middle at DS size, the top screen's YOUR ADDRESS box pinned left and
        // HOST ADDRESS right (as Setup's side boxes), its "enter the address..." band under the keys. Kind Setup: the menus'
        // backdrop over the whole canvas. Below ~18:9 it all shrinks together to fit the width.
        public static MenuLayout Keypad()
        {
            const float keys = 1, side = 0.7f, band = 0.9f, margin = 4;
            const float keysCentreY = margin + 96 * keys; // (the side boxes level with the keys)
            return new(LayoutKind.Setup, new[]
            {
                new LayoutRegion { Name = "keys", X0 = 0, Y0 = BottomY0, X1 = 256, Y1 = BottomY1, AnchorX = 0.5f, AnchorY = 0, PivotX = 0.5f, PivotY = 0, OffsetY = margin, Scale = keys },
                new LayoutRegion { Name = "band", X0 = 0, Y0 = 12, X1 = 256, Y1 = 52, AnchorX = 0.5f, AnchorY = 1, PivotX = 0.5f, PivotY = 1, OffsetY = -margin, Scale = band },
                new LayoutRegion { Name = "left", X0 = 0, Y0 = 52, X1 = 128, Y1 = TopY1, AnchorX = 0, AnchorY = 0, PivotX = 0, OffsetX = margin, OffsetY = keysCentreY, Scale = side },
                new LayoutRegion { Name = "right", X0 = 128, Y0 = 52, X1 = 256, Y1 = TopY1, AnchorX = 1, AnchorY = 0, PivotX = 1, OffsetX = -margin, OffsetY = keysCentreY, Scale = side },
            }, needWidth: 256 * keys + 256 * side + 4 * margin);
        }

        // The name keyboard (page 62, Vanilla multiplayer menu; Reports R3) as ONE screen: the touch screen (the
        // name box, CANCEL / OK, the keys) at the top at DS size, the top screen's "enter the new name..." band under it;
        // the top screen's empty grid has no region, so it isn't drawn: the mask's grid tiles (35 DS px squares centred on the
        // seam and at y 35, both screens) fall outside both regions, whose edges stop half a pixel short of those centres
        // (the keys region's top kept at the seam's place by its offset).
        public static MenuLayout Keyboard()
        {
            const float keys = 1, band = 0.9f, margin = 4, seam = 0.5f;
            // (kind MainFamily: the same backdrop as Setup, without Setup's per-triangle split of wide top-screen items, which
            // would cut the band's two lines apart)
            return new(LayoutKind.MainFamily, new[]
            {
                new LayoutRegion { Name = "keys", X0 = 0, Y0 = BottomY0, X1 = 256, Y1 = BottomY1 - seam, AnchorX = 0.5f, AnchorY = 0, PivotX = 0.5f, PivotY = 0, OffsetY = margin + seam * keys, Scale = keys },
                new LayoutRegion { Name = "band", X0 = 0, Y0 = 12, X1 = 256, Y1 = 34.5f, AnchorX = 0.5f, AnchorY = 1, PivotX = 0.5f, PivotY = 1, OffsetY = -margin, Scale = band },
            });
        }

        // Resolve the regions for a canvas of `width` x `height` pixels.
        public Placement[] Resolve(float width, float height)
        {
            float unit = height / VirtualHeight;
            float fit = _needWidth > 0 ? MathF.Min(1, width / unit / _needWidth) : 1;
            var result = new Placement[Regions.Count];
            for (int i = 0; i < Regions.Count; i++)
            {
                LayoutRegion r = Regions[i];
                float scale = r.Scale * unit * fit;
                float srcX = r.X0 + r.PivotX * (r.X1 - r.X0);
                float srcY = r.Y1 - r.PivotY * (r.Y1 - r.Y0);
                float dstX = r.AnchorX * width + r.OffsetX * unit * fit;
                float dstY = r.AnchorY * height + r.OffsetY * unit * fit;
                result[i] = new Placement(srcX, srcY, scale, dstX, dstY);
            }
            return result;
        }

        public int RegionOf(float x, float y) => RegionOf(x, y, text: null);

        // text: whether the item is a text (regions for only texts or only widgets skip the other); null: any region
        public int RegionOf(float x, float y, bool? text)
        {
            for (int i = 0; i < Regions.Count; i++)
            {
                if (text != null && Regions[i].Texts is bool only && only != text) continue;
                if (Regions[i].Contains(x, y)) return i;
            }
            return -1;
        }
    }
}
