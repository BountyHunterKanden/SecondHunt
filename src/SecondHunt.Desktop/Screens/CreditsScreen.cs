using System.Drawing;
using SecondHunt.Desktop.Ui;

namespace SecondHunt.Desktop.Screens;

// RECOMP SETTINGS > CREDITS (as the Android app's CreditsActivity): the app's version, what it's built on, and every
// third-party notice the app must carry (THIRD_PARTY_NOTICES.md, embedded at build time). Scrolls with the wheel, the
// arrow keys, Page Up / Down, the D-pad or the stick; Esc / B closes it.
internal sealed class CreditsScreen : PageScreen
{
    readonly string[] _lines;
    readonly Action _close;
    float _top; // first line shown

    public CreditsScreen(Action close)
    {
        _close = close;
        _lines = (Header() + Notices()).Replace("\r\n", "\n").Split('\n');
    }

    static string Header() =>
        $"{BuildFlags.AppName} {BuildFlags.Version} for Windows\n\n"
        + "A fan-made recompilation project built on MphRead by NoneGiven (MIT licence).\n"
        + "Metroid Prime Hunters is (c) Nintendo. This app contains no game code or data: it runs from the ROM\n"
        + "you provide. It is not affiliated with or endorsed by Nintendo.\n\n"
        + "----------------------------------------------------------------------------------------------\n\n";

    static string Notices()
    {
        using Stream? s = typeof(CreditsScreen).Assembly.GetManifestResourceStream("notices.md");
        if (s == null) return "(notices missing from this build)";
        using var r = new StreamReader(s);
        return r.ReadToEnd();
    }

    int LinesShown => Math.Max(1, (int)((Height - 64 * S) / LineHeight));
    float LineHeight => 19 * S;

    void Scroll(float lines)
    {
        float max = Math.Max(0, _lines.Length - LinesShown);
        float top = Math.Clamp(_top + lines, 0, max);
        if (top != _top)
        {
            _top = top;
            Invalidate();
        }
    }

    public override void Move(int dx, int dy) => Scroll(dy * 3);
    public override void Wheel(float steps) => Scroll(-steps * 3);
    public override void Back() => _close();
    public override void Press() { }

    public override void Key(OpenTK.Windowing.GraphicsLibraryFramework.Keys key)
    {
        switch (key)
        {
        case OpenTK.Windowing.GraphicsLibraryFramework.Keys.PageDown: Scroll(LinesShown - 2); break;
        case OpenTK.Windowing.GraphicsLibraryFramework.Keys.PageUp: Scroll(-(LinesShown - 2)); break;
        case OpenTK.Windowing.GraphicsLibraryFramework.Keys.Home: Scroll(-_lines.Length); break;
        case OpenTK.Windowing.GraphicsLibraryFramework.Keys.End: Scroll(_lines.Length); break;
        }
    }

    // the right stick / held keys scroll smoothly
    public void ScrollBy(float lines) => Scroll(lines);

    protected override void Paint(Graphics g)
    {
        float s = S, x = 48 * s, y = 32 * s;
        using Font mono = Font(13, mono: true);
        using var brush = new SolidBrush(Color.FromArgb(0xD8, 0xE8, 0xF0));
        int first = (int)_top;
        for (int i = first; i < _lines.Length && y < Height - 16 * s; i++)
        {
            g.DrawString(_lines[i], mono, brush, x, y);
            y += LineHeight;
        }
        using Font hint = Font(12);
        DrawText(g, "Wheel / arrows / Page Up, Page Down: scroll    Esc: back", hint, Muted,
            new RectangleF(x, Height - 26 * s, Width - 96 * s, 22 * s), StringAlignment.Far);
    }
}
