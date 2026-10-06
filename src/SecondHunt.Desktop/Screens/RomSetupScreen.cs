using System.Drawing;
using MphRecomp.App;
using SecondHunt.Desktop.Ui;

namespace SecondHunt.Desktop.Screens;

// First launch only (as the Android app's RomSetupActivity): the player picks their own Metroid Prime Hunters ROM (the
// file picker, or a file dropped on the window) and it is unpacked into the app's own folder (RomSetup, MphRead's
// Extract); then straight into the game. Nothing of the game ships with the app or is downloaded.
internal sealed class RomSetupScreen : PageScreen
{
    readonly string _filesDir;
    readonly Action _done;
    readonly List<string> _log = new();
    readonly object _gate = new();
    volatile bool _busy, _finished;
    bool _hover, _pressed;
    RectangleF _button;

    public RomSetupScreen(string filesDir, Action done)
    {
        _filesDir = filesDir;
        _done = done;
    }

    protected override void Update()
    {
        if (_finished)
        {
            _finished = false;
            _done();
        }
    }

    void Log(string line)
    {
        lock (_gate)
        {
            _log.Add(line);
            if (_log.Count > 200) _log.RemoveAt(0);
        }
        MphRecomp.App.Platform.Log.Info("MPHExtract", line);
        Invalidate();
    }

    public override void Press() => Pick();
    public override void Key(OpenTK.Windowing.GraphicsLibraryFramework.Keys key) { }

    public override void Click(float x, float y)
    {
        if (_button.Contains(x, y)) Pick();
    }

    public override void Hover(float x, float y)
    {
        bool hover = _button.Contains(x, y);
        if (hover != _hover)
        {
            _hover = hover;
            Invalidate();
        }
    }

    // the file picker (modal, on this thread: it's the app's STA main thread)
    void Pick()
    {
        if (_busy) return;
        _pressed = true;
        Invalidate();
        string? path = null;
        using (var dialog = new System.Windows.Forms.OpenFileDialog
        {
            Title = "Select your Metroid Prime Hunters ROM",
            Filter = "DS ROM (*.nds)|*.nds|All files (*.*)|*.*",
            CheckFileExists = true,
            RestoreDirectory = true,
        })
        {
            if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK) path = dialog.FileName;
        }
        _pressed = false;
        Invalidate();
        if (path != null) Start(path);
    }

    // a .nds dropped on the window
    public void Dropped(string path)
    {
        if (!_busy) Start(path);
    }

    void Start(string path)
    {
        _busy = true;
        Invalidate();
        Task.Run(() =>
        {
            try
            {
                Log($"Reading {Path.GetFileName(path)}…");
                if (RomSetup.Unsupported(path) is string why)
                {
                    Log(why);
                    return;
                }
                if (RomSetup.Extract(path, _filesDir, Log))
                {
                    _finished = true;
                }
            }
            catch (Exception ex)
            {
                Log("Could not read the ROM: " + ex.Message);
            }
            finally
            {
                _busy = false;
                Invalidate();
            }
        });
    }

    protected override void Paint(Graphics g)
    {
        float s = S, x = 48 * s, y = 48 * s, w = Width - 96 * s;
        using (Font title = Font(30, bold: true))
        {
            DrawText(g, BuildFlags.AppName, title, Color.White, new RectangleF(x, y, w, 50 * s));
        }
        y += 58 * s;
        string info = "Select your own Metroid Prime Hunters (USA) ROM, version 1.0 or 1.1. It is read once and unpacked into "
            + "this app's own folder; nothing is bundled with the app or downloaded. You can also drop the .nds file on this window.";
        using (Font body = Font(17))
        {
            float h = MeasureHeight(g, info, body, w);
            DrawText(g, info, body, Label, new RectangleF(x, y, w, h + 4));
            y += h + 24 * s;
        }
        _button = new RectangleF(x, y, 260 * s, 56 * s);
        Box(g, _button, focused: !_busy, pressed: _pressed || (_hover && !_busy));
        using (Font button = Font(19, bold: true))
        {
            DrawText(g, _busy ? "Working…" : "Select ROM…", button, _busy ? Muted : Color.White, _button,
                StringAlignment.Center, StringAlignment.Center);
        }
        y += 56 * s + 24 * s;
        string log;
        lock (_gate)
        {
            log = string.Join("\n", _log.TakeLast(Math.Max(1, (int)((Height - y - 24 * s) / (20 * s)))));
        }
        using Font mono = Font(14, mono: true);
        DrawText(g, log, mono, Muted, new RectangleF(x, y, w, Height - y - 24 * s));
    }
}
