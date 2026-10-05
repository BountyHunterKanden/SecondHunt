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
    public enum LayoutKind { SideBySide, Overlay, MainFamily, Stacked }

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

        private MenuLayout(LayoutKind kind, IReadOnlyList<LayoutRegion> regions)
        {
            Kind = kind;
            Regions = regions;
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
            bool extra = false; // the recomp's own additions to a ROM page, placed right of the touch screen
            foreach (MenuItem item in page.Items)
            {
                if (item.X >= 256) extra = true;
            }
            if (dual) return SideBySide(extra);
            // 14..17 are overlays the menu shows on top of its logo pages (BEGIN GAME and its fade, the connection
            // pages): they go where the logo pages' controls go
            if (page.Index >= 14 && page.Index <= 17) return MainFamily();
            foreach (MenuItem item in page.Items)
            {
                foreach (MenuItemState s in item.States)
                {
                    if (s.WidgetIndex >= 0 && file.Widgets[s.WidgetIndex].ModelPath.EndsWith("toplogo_Model.bin", StringComparison.OrdinalIgnoreCase))
                    {
                        return MainFamily(actionsAboveRight: page.Index == FileSelect.Page);
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
        public static MenuLayout MainFamily(bool actionsAboveRight = false) => new(LayoutKind.MainFamily, new[]
        {
            new LayoutRegion { Name = "footer-left", X0 = 0, Y0 = -192, X1 = 64, Y1 = -160, AnchorX = 0, AnchorY = 1, PivotX = 0, PivotY = 1, OffsetX = 6, OffsetY = -4, Scale = 0.9f },
            new LayoutRegion { Name = "footer-right", X0 = 176, Y0 = -192, X1 = 256, Y1 = -160, AnchorX = 1, AnchorY = 1, PivotX = 1, PivotY = 1, OffsetX = -6, OffsetY = -4, Scale = 0.9f },
            actionsAboveRight
                // x 161 (DELETE) lands where footer-right puts x 228 (MOVIES): 13 menu pixels further in than its right edge
                ? new LayoutRegion { Name = "footer-mid", X0 = 64, Y0 = -192, X1 = 176, Y1 = -160, AnchorX = 1, AnchorY = 1, PivotX = 1, PivotY = 1, OffsetX = -6 - 13 * 0.9f, OffsetY = -4 - 40 * 0.9f, Scale = 0.9f }
                : new LayoutRegion { Name = "footer-mid", X0 = 64, Y0 = -192, X1 = 176, Y1 = -160, AnchorX = 0.5f, AnchorY = 1, PivotX = 0.5f, PivotY = 1, OffsetY = -4, Scale = 0.9f },
            new LayoutRegion { Name = "extra", X0 = 256, Y0 = -160, X1 = 332, Y1 = 0, AnchorX = 1, AnchorY = 0, PivotX = 1, PivotY = 0, OffsetX = -8, OffsetY = 64, Scale = 0.76f },
            new LayoutRegion { Name = "controls", X0 = 0, Y0 = -160, X1 = 256, Y1 = 0, AnchorX = 0.5f, AnchorY = 0, PivotX = 0.5f, PivotY = 0, OffsetY = 64, Scale = 0.76f },
            new LayoutRegion { Name = "logo", X0 = 0, Y0 = 60, X1 = 256, Y1 = 192, AnchorX = 0.5f, AnchorY = 0, PivotX = 0.5f, PivotY = 0, Scale = 0.5f },
            new LayoutRegion { Name = "info", X0 = 0, Y0 = 0, X1 = 256, Y1 = 60, AnchorX = 0.5f, AnchorY = 1, PivotX = 0.5f, PivotY = 1, OffsetY = -2, Scale = 0.86f },
        });

        // Resolve the regions for a canvas of `width` x `height` pixels.
        public Placement[] Resolve(float width, float height)
        {
            float unit = height / VirtualHeight;
            var result = new Placement[Regions.Count];
            for (int i = 0; i < Regions.Count; i++)
            {
                LayoutRegion r = Regions[i];
                float scale = r.Scale * unit;
                float srcX = r.X0 + r.PivotX * (r.X1 - r.X0);
                float srcY = r.Y1 - r.PivotY * (r.Y1 - r.Y0);
                float dstX = r.AnchorX * width + r.OffsetX * unit;
                float dstY = r.AnchorY * height + r.OffsetY * unit;
                result[i] = new Placement(srcX, srcY, scale, dstX, dstY);
            }
            return result;
        }

        public int RegionOf(float x, float y)
        {
            for (int i = 0; i < Regions.Count; i++)
            {
                if (Regions[i].Contains(x, y)) return i;
            }
            return -1;
        }
    }
}
