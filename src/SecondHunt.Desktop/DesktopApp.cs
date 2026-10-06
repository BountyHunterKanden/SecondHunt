using System.Diagnostics;
using System.Drawing;
using MphRecomp.App;
using MphRecomp.App.Gl;
using MphRecomp.App.Platform;
using MphRecomp.Frontend;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.Common.Input;
using OpenTK.Windowing.Desktop;
using OpenTK.Windowing.GraphicsLibraryFramework;
using SecondHunt.Desktop.Screens;
using SecondHunt.Desktop.Ui;

namespace SecondHunt.Desktop;

// The Windows app: one window, and the screens Android opens as activities, here a stack drawn top-first. The game's
// menus (FrontendRenderer), the game itself (CampaignRenderer: the campaign and matches), and this host's own setup,
// lobby and credits. Each screen gets its own GL context (GlWindow) that dies with it. The hand-offs match the Android
// app's: starting a game closes the menus and leaving it opens them fresh on the main menu; the lobby and the credits
// sit over what opened them (its sound paused) and go back to it.
internal sealed class DesktopApp : IDisposable
{
    sealed class Entry
    {
        public required IGlScreen Screen;
        public IntPtr Context;
        public int Width = -1, Height = -1;
        public bool Created;
        public OverlayDrawer? Overlay; // the campaign's status line and centred panel
    }

    readonly GlWindow _gl;
    readonly DesktopInput _input;
    readonly List<Entry> _stack = new();
    readonly Queue<Action> _deferred = new();
    readonly AppOptions _options;
    FrontendRenderer? _front;
    CampaignRenderer? _game;
    readonly TextOverlay _status = new(TextOverlay.Kind.Status), _panel = new(TextOverlay.Kind.Panel), _note = new(TextOverlay.Kind.Status);
    double _noteUntil;
    readonly Stopwatch _clock = Stopwatch.StartNew();
    bool _minimized, _audioPausedForMinimize;
    int _frames;

    public DesktopApp(AppOptions options)
    {
        _options = options;
        WindowIcon? icon = DesktopPlatform.WindowIcon();
        _gl = new GlWindow(BuildFlags.AppName, icon);
        DesktopCrashLog.Gpu = _gl.Renderer;
        _input = new DesktopInput(this, _gl.Window);
        if (options.Script != null) _input.SetScript(options.Script);
        NativeWindow w = _gl.Window;
        w.Minimized += e => _minimized = e.IsMinimized;
        w.FileDrop += e =>
        {
            if (Top?.Screen is RomSetupScreen setup && e.FileNames.Length > 0) setup.Dropped(e.FileNames[0]);
        };
        w.Closing += _ => Log.Info("MPHDesktop", "window closing");
        WindowPrefs.Apply(w);
        if (!RomSetup.IsExtracted(DesktopPaths.Game))
        {
            var setup = new RomSetupScreen(DesktopPaths.Game, () => Defer(() => { Pop(); OpenFrontend(0); }));
            Push(setup);
            if (options.Rom != null) setup.Dropped(options.Rom); // --rom: no picker (as Android's dev rom.nds)
        }
        else if (options.Launch is { } launch && launch.TryGetValue("screen", out string? screen))
        {
            // --screen credits|lobby: one of this host's own screens on its own (dev builds; testing)
            OpenFrontend(18);
            if (screen == "credits") PushOver(new CreditsScreen(() => Defer(PopBack)));
            else PushOver(new LobbyScreen(DesktopPaths.Game, Path.Combine(DesktopPaths.Root, "lobby.json"),
                args => Defer(() => StartGame(args)), () => Defer(PopBack)));
        }
        else if (options.Launch != null)
        {
            StartGame(options.Launch);
        }
        else
        {
            OpenFrontend(options.StartPage);
        }
    }

    Entry? Top => _stack.Count > 0 ? _stack[^1] : null;
    public IGlScreen? TopScreen => Top?.Screen;
    public FrontendRenderer? Front => Top?.Screen == _front ? _front : null;
    public CampaignRenderer? Game => Top?.Screen == _game ? _game : null;

    public void Defer(Action action) => _deferred.Enqueue(action);

    // ---- the loop ----

    public void Run()
    {
        NativeWindow w = _gl.Window;
        w.IsVisible = true;
        w.Focus();
        long lastSwap = Stopwatch.GetTimestamp();
        while (!w.IsExiting)
        {
            w.NewInputFrame();
            NativeWindow.ProcessWindowEvents(false);
            if (w.IsExiting) break;
            _input.Poll();
            while (_deferred.TryDequeue(out Action? action)) action();
            if (_stack.Count == 0)
            {
                w.Close();
                break;
            }
            if (_minimized || w.FramebufferSize.X <= 0 || w.FramebufferSize.Y <= 0)
            {
                // as an Android app in the background: nothing drawn, the sound paused
                if (!_audioPausedForMinimize)
                {
                    _audioPausedForMinimize = true;
                    PauseTopAudio();
                }
                Thread.Sleep(30);
                continue;
            }
            if (_audioPausedForMinimize)
            {
                _audioPausedForMinimize = false;
                ResumeTopAudio();
            }
            DrawFrame();
            _gl.Swap();
            // no vsync from the driver: don't run the GPU flat out (about 250 frames a second at most)
            double ms = Stopwatch.GetElapsedTime(lastSwap).TotalMilliseconds;
            if (ms < 4) Thread.Sleep(1);
            lastSwap = Stopwatch.GetTimestamp();
            _frames++;
            if (_frames % 600 == 0) Log.Info("MPHAudio", "sound device: " + DesktopPlatform.AudioStats());
            if (_options.ShotPath != null && _options.ShotFrames.Contains(_frames))
            {
                Screenshot(_options.ShotFrames.Count == 1 ? _options.ShotPath
                    : Path.ChangeExtension(_options.ShotPath, null) + $"_{_frames}.png");
            }
            if (_options.QuitAfterFrames > 0 && _frames >= _options.QuitAfterFrames) w.Close();
        }
        while (_stack.Count > 0) Pop();
    }

    void DrawFrame()
    {
        Entry top = Top!;
        _gl.MakeCurrent(top.Context);
        int width = _gl.Window.FramebufferSize.X, height = _gl.Window.FramebufferSize.Y;
        if (!top.Created)
        {
            top.Created = true;
            top.Screen.OnSurfaceCreated();
            top.Overlay = new OverlayDrawer();
            top.Overlay.Init();
        }
        if (width != top.Width || height != top.Height)
        {
            top.Width = width;
            top.Height = height;
            top.Screen.OnSurfaceChanged(width, height);
        }
        top.Screen.OnDrawFrame();
        // this host's text over the game: the status line (top left) and the centred panel (game over, match results);
        // the menus' own status (a load failure) the same way
        float s = Math.Max(0.6f, Math.Min(width / 1280f, height / 720f));
        if (top.Screen == _game || top.Screen == _front)
        {
            _status.Draw(top.Overlay!, 0, 0, width, height, s, center: false);
            _panel.Draw(top.Overlay!, 0, 0, width, height, s, center: true);
        }
        if (_clock.Elapsed.TotalSeconds < _noteUntil)
        {
            _note.Draw(top.Overlay!, 0, height - 60 * s, width, height, s, center: false);
        }
        _input.AfterFrame();
    }

    // a line at the bottom for a few seconds (Android's toasts)
    public void Note(string text, double seconds = 4)
    {
        _note.Set(text);
        _noteUntil = _clock.Elapsed.TotalSeconds + seconds;
    }

    // ---- the stack ----

    void Push(IGlScreen screen)
    {
        IntPtr context = _gl.CreateContext();
        _stack.Add(new Entry { Screen = screen, Context = context });
        _input.ScreenChanged();
    }

    void Pop()
    {
        Entry top = _stack[^1];
        _stack.RemoveAt(_stack.Count - 1);
        _gl.MakeCurrent(top.Context);
        Close(top.Screen);
        if (top.Overlay != null)
        {
            _status.Forget(top.Overlay);
            _panel.Forget(top.Overlay);
            _note.Forget(top.Overlay);
        }
        _gl.DeleteContext(top.Context); // frees everything the screen made in GL
        _input.ScreenChanged();
    }

    void Close(IGlScreen screen)
    {
        try
        {
            switch (screen)
            {
            case FrontendRenderer front:
                front.Dispose();
                if (_front == front) _front = null;
                break;
            case CampaignRenderer game:
                game.Shutdown();
                game.DisposeAudio();
                game.NetSession?.Dispose(); // the other player hears BYE at once
                if (_game == game) _game = null;
                break;
            case LobbyScreen lobby:
                lobby.Close();
                break;
            }
        }
        catch (Exception ex)
        {
            Log.Warn("MPHDesktop", $"closing {screen.GetType().Name}: {ex.Message}");
        }
    }

    void PauseTopAudio()
    {
        if (Top?.Screen is FrontendRenderer f) f.PauseAudio();
        else if (Top?.Screen is CampaignRenderer g) g.PauseAudio();
    }

    void ResumeTopAudio()
    {
        if (Top?.Screen is FrontendRenderer f) f.ResumeAudio();
        else if (Top?.Screen is CampaignRenderer g) g.ResumeAudio();
    }

    // an overlay screen (lobby, credits) over the current one: the one underneath stops its sound
    void PushOver(IGlScreen screen)
    {
        PauseTopAudio();
        Push(screen);
    }

    // back from an overlay screen: the one underneath carries on (the menus re-read settings the pause menu may have saved)
    void PopBack()
    {
        Pop();
        if (Top?.Screen is FrontendRenderer f)
        {
            f.Post(s => s.Settings.ReloadFrom(DesktopPaths.Settings));
        }
        ResumeTopAudio();
    }

    // ---- the menus ----

    void OpenFrontend(int startPage)
    {
        var front = new FrontendRenderer(DesktopPaths.Game, DesktopPaths.Settings, DesktopPaths.Mods, DesktopPaths.Saves)
        {
            StartPage = startPage,
        };
        if (!BuildFlags.Public)
        {
            // the HD backdrop (dev builds; imports held in Beta 1): the imported models' folder, and where its picture is kept
            front.HdRoot = Path.Combine(DesktopPaths.Root, "hd");
            front.CacheDir = DesktopPaths.Cache;
        }
        front.RequestSink = r => Defer(() => OnFrontRequest(r));
        front.StatusSink = s => _panel.Set(s);
        _front = front;
        Push(front);
    }

    void OnFrontRequest(FrontendRequest request)
    {
        switch (request.Kind)
        {
        case FrontendRequestKind.StartAdventure:
        {
            // files A/B/C = the campaign's save slots 1/2/3: a file with a save continues it, an empty one starts a new
            // game in the Celestial Archives
            int slot = request.Slot + 1;
            bool saved = File.Exists(Path.Combine(DesktopPaths.Saves, $"campaign{slot}.json"));
            var args = new Dictionary<string, string> { ["room"] = "UNIT2_LAND", [saved ? "save" : "slot"] = slot.ToString() };
            Log.Info("MPHFrontend", $"StartAdventure -> campaign slot {slot} ({(saved ? "save" : "new")})");
            StartGame(args);
            break;
        }
        case FrontendRequestKind.Multiplayer:
            PushOver(new LobbyScreen(DesktopPaths.Game, Path.Combine(DesktopPaths.Root, "lobby.json"),
                args => Defer(() => StartGame(args)), () => Defer(PopBack)));
            break;
        case FrontendRequestKind.Credits:
            PushOver(new CreditsScreen(() => Defer(PopBack)));
            break;
        case FrontendRequestKind.ShareCrashLog:
            ShareCrashLog();
            break;
        case FrontendRequestKind.OpenDeveloperMenu:
            Note("The developer tools are in the Android build only.");
            break;
        }
    }

    void ShareCrashLog()
    {
        if (DesktopCrashLog.LatestPath() == null)
        {
            Note("No crash reports. Nothing has crashed.");
            return;
        }
        DesktopCrashLog.Open();
    }

    // ---- the game ----

    // a campaign or a match (args: CampaignRenderer.Create's launch args); the menus close meanwhile, as on Android
    void StartGame(Dictionary<string, string> args)
    {
        while (_stack.Count > 0) Pop();
        CampaignRenderer game = CampaignRenderer.Create(DesktopPaths.Game, DesktopPaths.Root, new LaunchArgs(args));
        game.StatusSink = s => _status.Set(s);
        game.ShipSink = s => _panel.Set(s);
        game.ExitSink = () => Defer(ExitGame);
        game.KeyboardMouse = _input.KeyboardMouse;
        game.DetailsKey = "F3";
        PauseOverlay pause = game.PauseMenu!;
        pause.Credits += () => Defer(() =>
        {
            game.PauseAudio();
            Push(new CreditsScreen(() => Defer(PopBack)));
        });
        pause.ShareCrashLog += () => Defer(ShareCrashLog);
        pause.DevTools += () => Defer(() => Note("The developer tools are in the Android build only."));
        _game = game;
        _status.Set("loading " + (args.TryGetValue("room", out string? room) ? room : "") + "...");
        _panel.Set("");
        Push(game);
    }

    // the game's quit (pause menu QUIT, the end of a match, game over QUIT): back to the main menu, opened fresh
    void ExitGame()
    {
        while (_stack.Count > 0) Pop();
        _status.Set("");
        _panel.Set("");
        OpenFrontend(18);
    }

    // ---- window ----

    public void ToggleFullscreen()
    {
        WindowPrefs.Toggle(_gl.Window);
    }

    void Screenshot(string path)
    {
        try
        {
            int w = _gl.Window.FramebufferSize.X, h = _gl.Window.FramebufferSize.Y;
            var raw = new byte[w * h * 4];
            GLES30.GlPixelStorei(GLES30.GlPackAlignment, 4);
            GLES30.GlReadPixels(0, 0, w, h, GLES30.GlRgba, GLES30.GlUnsignedByte, raw);
            var flipped = new byte[raw.Length];
            for (int y = 0; y < h; y++) Buffer.BlockCopy(raw, (h - 1 - y) * w * 4, flipped, y * w * 4, w * 4);
            DesktopPlatform.EncodePng(path, flipped, w, h);
            Log.Info("MPHDesktop", $"screenshot {path} ({w}x{h}) at frame {_frames}");
        }
        catch (Exception ex)
        {
            Log.Warn("MPHDesktop", "screenshot failed: " + ex.Message);
        }
    }

    public void Dispose()
    {
        while (_stack.Count > 0) Pop();
        WindowPrefs.Save(_gl.Window);
        _gl.Dispose();
    }
}

// What the command line asked for (dev and testing): --data <dir>, --page <n> (the menus' first page), any other
// --<name> <value> pairs start the game straight away with those launch args (--room UNIT2_LAND, --save 1,
// --match Battle --arena "Combat Hall"), --shot <png> [--shot-frame <n>[,<n>...]] saves pictures of those frames,
// --quit-after <n> closes after n frames, --rom <file> unpacks that ROM on a first launch without the picker,
// --script "<frame>:<key>,<frame>:click@<x>;<y>" plays key presses and clicks (DesktopInput.SetScript).
internal sealed class AppOptions
{
    public string? Data;
    public string? Rom;
    public string? Script;
    public int StartPage;
    public Dictionary<string, string>? Launch;
    public string? ShotPath;
    public List<int> ShotFrames = new() { 300 };
    public int QuitAfterFrames;

    public static AppOptions Parse(string[] args)
    {
        var o = new AppOptions();
        var launch = new Dictionary<string, string>();
        for (int i = 0; i + 1 < args.Length; i += 2)
        {
            string key = args[i].TrimStart('-', '/'), value = args[i + 1];
            switch (key)
            {
            case "data": o.Data = value; break;
            case "rom": o.Rom = Path.GetFullPath(value); break;
            case "script": o.Script = value; break;
            case "page": int.TryParse(value, out o.StartPage); break;
            case "shot": o.ShotPath = Path.GetFullPath(value); break;
            case "shot-frame": o.ShotFrames = value.Split(',').Select(v => int.TryParse(v, out int n) ? n : 0).ToList(); break;
            case "quit-after": int.TryParse(value, out o.QuitAfterFrames); break;
            default: launch[key] = value; break;
            }
        }
        if (launch.Count > 0) o.Launch = launch;
        return o;
    }
}
