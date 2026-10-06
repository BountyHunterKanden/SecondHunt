using System.Drawing;

namespace SecondHunt.Desktop.Ui;

// A line or block of text over a screen, as the Android app's TextViews over its GLSurfaceView: the status line (white
// with a shadow, top left), the centred panel (monospace on a dark box: game over, match results) and the notes (a dark
// strip, bottom). Set from the screens' sinks; drawn with whichever context's OverlayDrawer is current (each context
// gets its own copy of the picture).
internal sealed class TextOverlay
{
    public enum Kind { Status, Panel }

    readonly Kind _kind;
    readonly object _gate = new();
    string _text = "";
    int _version;
    TextImage? _image;
    int _imageVersion = -1;
    float _imageScale;
    readonly Dictionary<OverlayDrawer, (int Texture, int Version)> _uploaded = new();

    public TextOverlay(Kind kind)
    {
        _kind = kind;
    }

    public void Set(string text)
    {
        lock (_gate)
        {
            if (text == _text) return;
            _text = text;
            _version++;
        }
    }

    // a context went away: its pictures with it
    public void Forget(OverlayDrawer drawer) => _uploaded.Remove(drawer);

    // area: x, y, width, height of the region to place it in (window pixels); s: the window's scale against 720p
    public void Draw(OverlayDrawer drawer, float x, float y, int width, int height, float s, bool center)
    {
        string text;
        int version;
        lock (_gate)
        {
            text = _text;
            version = _version;
        }
        if (text.Length == 0) return;
        if (_image == null || _imageVersion != version || _imageScale != s)
        {
            _image = Render(text, s);
            _imageVersion = version;
            _imageScale = s;
        }
        int stamp = HashCode.Combine(version, s);
        if (!_uploaded.TryGetValue(drawer, out var up) || up.Version != stamp)
        {
            int tex = up.Texture;
            OverlayDrawer.Upload(ref tex, _image);
            _uploaded[drawer] = (tex, stamp);
            up = (tex, stamp);
        }
        float w = _image.Width, h = _image.Height;
        float px = center ? x + (width - x - w) / 2 : x, py = center ? (height - h) / 2 : y;
        drawer.Draw(up.Texture, px, py, w, h, width, height);
    }

    TextImage Render(string text, float s)
    {
        if (_kind == Kind.Panel)
        {
            using var font = new Font("Consolas", 22 * s, FontStyle.Regular, GraphicsUnit.Pixel);
            return Gdi.Text(text, font, Color.FromArgb(200, 255, 220), Color.FromArgb(215, 4, 18, 24),
                (int)(36 * s), (int)(27 * s), shadow: false);
        }
        using var small = new Font("Segoe UI", 16 * s, FontStyle.Regular, GraphicsUnit.Pixel);
        return Gdi.Text(text, small, Color.White, null, (int)(18 * s), (int)(12 * s), shadow: true);
    }
}
