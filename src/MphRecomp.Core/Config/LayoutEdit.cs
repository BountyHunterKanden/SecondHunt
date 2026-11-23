using System;
using System.Text.Json.Serialization;

// One player edit to an on-screen element's place, size and look (owner 2026-11-15, wave 2): the touch controls' layout
// editor (MphRead.Android/MatchTouchOverlay + TouchLayoutEditor, RecompSettings.TouchLayouts) and the HUD editor
// (RecompSettings.HudLayouts) store the same record. It holds overrides only: a null or default field means "as
// shipped", so an untouched element draws exactly as before.
//
// A moved element's place is an offset from one of nine anchor points of the screen (tl t tr / l c r / bl b br), in
// units of the screen's HEIGHT (+x right, +y down). Anchoring to the nearest edge or corner and measuring in heights
// keeps a layout made on one screen shape sensible on another: a button near the right edge stays that far from it
// on a wider screen, at the same size relative to the height.
namespace MphRecomp.Config
{
    public sealed class LayoutEdit
    {
        public const float MinScale = 0.5f;
        public const float MaxScale = 2f;
        public static readonly string[] Anchors = { "tl", "t", "tr", "l", "c", "r", "bl", "b", "br" };

        // the element's centre as an offset from Anchor in screen heights; null = where it ships
        public float? X { get; set; }
        public float? Y { get; set; }
        // tl..br (Anchors); null = the element's own default anchor
        public string? Anchor { get; set; }
        // size as a multiple of the shipped size (MinScale-MaxScale)
        public float Scale { get; set; } = 1;
        // 0-1, on top of the element's own look (and the touch controls' ALL BUTTONS opacity)
        public float Opacity { get; set; } = 1;
        public bool Hidden { get; set; }

        [JsonIgnore]
        public bool Moved => X != null && Y != null;

        [JsonIgnore]
        public bool IsDefault => X == null && Y == null && Anchor == null && Scale == 1 && Opacity == 1 && !Hidden;

        public LayoutEdit Copy()
        {
            return new LayoutEdit { X = X, Y = Y, Anchor = Anchor, Scale = Scale, Opacity = Opacity, Hidden = Hidden };
        }

        // the anchor point as fractions of the screen's width and height; an unknown name is the top left
        public static (float fx, float fy) AnchorPoint(string? anchor)
        {
            return anchor switch
            {
                "t" => (0.5f, 0f),
                "tr" => (1f, 0f),
                "l" => (0f, 0.5f),
                "c" => (0.5f, 0.5f),
                "r" => (1f, 0.5f),
                "bl" => (0f, 1f),
                "b" => (0.5f, 1f),
                "br" => (1f, 1f),
                _ => (0f, 0f)
            };
        }

        // a stored offset (x, y in screen heights from anchor) to pixels on a w x h screen
        public static (float px, float py) Place(string? anchor, float x, float y, float w, float h)
        {
            (float fx, float fy) = AnchorPoint(anchor);
            return (fx * w + x * h, fy * h + y * h);
        }

        // pixels on a w x h screen to an offset from anchor in screen heights (Place's inverse)
        public static (float x, float y) Unplace(string? anchor, float px, float py, float w, float h)
        {
            if (h <= 0) return (0, 0);
            (float fx, float fy) = AnchorPoint(anchor);
            return ((px - fx * w) / h, (py - fy * h) / h);
        }

        // the anchor a point at (px, py) belongs to: the screen split in thirds each way
        public static string NearestAnchor(float px, float py, float w, float h)
        {
            int col = w <= 0 ? 0 : px < w / 3 ? 0 : px > w * 2 / 3 ? 2 : 1;
            int row = h <= 0 ? 0 : py < h / 3 ? 0 : py > h * 2 / 3 ? 2 : 1;
            return Anchors[row * 3 + col];
        }

        public static float ClampScale(float scale)
        {
            return Math.Clamp(scale, MinScale, MaxScale);
        }
    }
}
