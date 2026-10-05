using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using MphRecomp.Frontend;

namespace MphRead
{
    // -sndtrace <route.txt>... : drives our menu screens (Core Frontend) through a scripted route of taps and keys at
    // 60 Hz and logs every sound call they make, one line per call in the BizHawk tracer's format, so ours and vanilla's
    // can be diffed (handoffs/start-menu-ui-rebuild_2026-10-01-files/sound_census/compare.py):
    //   f00031 PlaySfx id=0x4 | PlayScript id=0x4017 | StopFreeScripts | PlaySfx id=0x1f5 h=1 (a loop the screen keeps)
    //   | StopHandle h=1 | UpdateHandle h=1 vol=0xff pan=0xff pitch=0x2400 (0xff / 0xffff = unchanged)
    // A menu script goes out through the game's dispatcher (ov0 0x21028d8 = StopFreeScripts(1) + PlayScript), which is
    // what the front end's PlaySound does with a script id, so its log shows both.
    // usage: -sndtrace [-root <game files>] <route.txt>... (the vanilla logs are USA rev 1: pass a rev 1 tree)
    // Route file (shared with the BizHawk side), one step per line, '#' starts a comment:
    //   screen ship [ask] | planet | brief | fe [page]   which of our screens (ship ask: the hatch's SAVE GAME question)
    //   state <name>                                     the vanilla savestate (BizHawk side only)
    //   planets <areas> <known> <current>               planet select: StorySave.Areas, KnownAreas, current planet
    //   last <frame>                                     run to this frame (default: the last step + 120)
    //   saves <ABC>                                      front end: these campaign files exist (new games)
    //   ours-pre tap <x> <y>                             front end: a tap before frame 0 (to match a vanilla state)
    //   <f> tap <x> <y> [hold]                           a DS touch-screen point, held `hold` frames (default 6)
    //   <f> tap <NAME> [hold]                            a named control (ship: SAVE EXIT LAUNCH WEAPON OPTIONS LOGBOOK
    //                                                    YES NO OK; planet: LAND) -- each side knows where its own is
    //   <f> drag <x0> <y0> <x1> <y1> <frames>            touch down, slide in a straight line, release
    //   <f> key <A|B|X|Y|L|R|Start|Select|Up|Down|Left|Right> [hold]
    // Frame 0 is the state's first frame; for our planet select that's after its intro (the vanilla states are
    // interactive).
    internal static class SoundTrace
    {
        private sealed record RouteStep(int Frame, string Verb, string[] Args);

        private sealed class Route
        {
            public string Name = "";
            public string Screen = "ship";
            public string[] ScreenArgs = Array.Empty<string>();
            public int Areas = 0x155, Known = 0x155, Current = 1;
            public int Last = -1;
            public string Saves = "";
            public bool Items; // "#!items": note the front end's item states (index:code hex) when they change
            // "ours-pre tap <x> <y>": front end, taps before frame 0 to reach a vanilla state's setup (fileB: A's dialog up)
            public readonly List<(float X, float Y)> PreTaps = new();
            public readonly List<RouteStep> Steps = new();
        }

        private static Route Parse(string path)
        {
            var r = new Route { Name = Path.GetFileNameWithoutExtension(path) };
            foreach (string raw in File.ReadAllLines(path))
            {
                if (raw.TrimStart().StartsWith("#!items")) r.Items = true;
                string line = raw.Split('#')[0].Trim();
                if (line.Length == 0) continue;
                string[] t = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                switch (t[0])
                {
                case "screen":
                    r.Screen = t[1];
                    r.ScreenArgs = t[2..];
                    break;
                case "state":
                    break;
                case "planets":
                    r.Areas = Num(t[1]);
                    r.Known = Num(t[2]);
                    r.Current = Num(t[3]);
                    break;
                case "last":
                    r.Last = Num(t[1]);
                    break;
                case "saves":
                    r.Saves = t[1].ToUpperInvariant();
                    break;
                case "ours-pre":
                    if (t[1] == "tap") r.PreTaps.Add((F(t[2]), F(t[3])));
                    break;
                default:
                    r.Steps.Add(new RouteStep(Num(t[0]), t[1].ToLowerInvariant(), t[2..]));
                    break;
                }
            }
            if (r.Last < 0) r.Last = (r.Steps.Count == 0 ? 0 : r.Steps.Max(s => s.Frame)) + 120;
            return r;
        }

        private static int Num(string s) => s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? Int32.Parse(s[2..], NumberStyles.HexNumber) : Int32.Parse(s, CultureInfo.InvariantCulture);

        private static bool IsNumber(string s) => Single.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out _);

        private static float F(string s) => Single.Parse(s, CultureInfo.InvariantCulture);

        // one screen behind a common face: touch in DS touch-screen pixels or by control name, keys, a tick
        private interface IScreen
        {
            // canvas: (x, y) is a canvas pixel of ours (a named control), else a DS touch-screen pixel
            void Down(float x, float y, bool canvas);
            void Move(float x, float y);
            void Up(float x, float y, bool canvas);
            (float X, float Y)? Named(string name);
            void Key(MenuKeys key);
            void Held(int dx, int dy, MenuKeys keys);
            void Tick();
        }

        // the game files our screens read: the desktop extraction (USA rev 0) unless "-root <dir>" names another tree --
        // the BizHawk traces are USA rev 1 (the Odin's revision), whose menu sound tables (IDS.DAT) and items differ
        private static string _root = "";

        public static void Run(string[] args)
        {
            if (!ToolPaths.UseDesktopExtraction()) return;
            _root = Paths.FileSystem;
            var routes = new List<string>();
            for (int i = 1; i < args.Length; i++)
            {
                if (args[i] == "-root" && i + 1 < args.Length) _root = args[++i];
                else routes.Add(args[i]);
            }
            Console.WriteLine($"game files: {_root}");
            foreach (string path in routes)
            {
                Route route = Parse(path);
                var log = new List<string>();
                int frame = -1;
                void Add(string s)
                {
                    if (frame >= 0) log.Add($"f{frame:00000} {s}");
                }
                IScreen screen = Make(route, Add, m => { if (frame >= 0) log.Add($"# f{frame:00000} {m}"); }, f => frame = f);
                // a held touch / key: (until frame, kind, data)
                (float X, float Y) touchAt = default;
                bool touchCanvas = false;
                int touchUntil = -1;
                (float X0, float Y0, float X1, float Y1, int Start, int Frames)? drag = null;
                var heldKeys = new Dictionary<MenuKeys, int>();
                string? lastItems = null;
                frame = 0;
                for (; frame <= route.Last; frame++)
                {
                    foreach (RouteStep st in route.Steps.Where(s => s.Frame == frame))
                    {
                        log.Add($"# f{frame:00000} INPUT {st.Verb} {String.Join(' ', st.Args)}");
                        switch (st.Verb)
                        {
                        case "tap":
                        {
                            (float X, float Y)? at = IsNumber(st.Args[0]) ? (F(st.Args[0]), F(st.Args[1])) : screen.Named(st.Args[0]);
                            int holdArg = IsNumber(st.Args[0]) ? 2 : 1;
                            int hold = st.Args.Length > holdArg ? Num(st.Args[holdArg]) : 6;
                            if (at == null)
                            {
                                log.Add($"# f{frame:00000} (control {st.Args[0]} isn't on screen)");
                                break;
                            }
                            touchAt = at.Value;
                            touchCanvas = !IsNumber(st.Args[0]);
                            touchUntil = frame + hold;
                            screen.Down(touchAt.X, touchAt.Y, touchCanvas);
                            break;
                        }
                        case "drag":
                            drag = (F(st.Args[0]), F(st.Args[1]), F(st.Args[2]), F(st.Args[3]), frame, Math.Max(1, Num(st.Args[4])));
                            screen.Down(drag.Value.X0, drag.Value.Y0, canvas: false);
                            break;
                        case "key":
                        {
                            MenuKeys key = Enum.Parse<MenuKeys>(st.Args[0], ignoreCase: true);
                            heldKeys[key] = frame + (st.Args.Length > 1 ? Num(st.Args[1]) : 6);
                            screen.Key(key);
                            break;
                        }
                        }
                    }
                    if (touchUntil >= 0 && frame == touchUntil)
                    {
                        screen.Up(touchAt.X, touchAt.Y, touchCanvas);
                        touchUntil = -1;
                    }
                    if (drag is { } d && frame > d.Start)
                    {
                        float t = Math.Min(1, (frame - d.Start) / (float)d.Frames);
                        float x = d.X0 + (d.X1 - d.X0) * t, y = d.Y0 + (d.Y1 - d.Y0) * t;
                        if (frame - d.Start >= d.Frames)
                        {
                            screen.Up(x, y, canvas: false);
                            drag = null;
                        }
                        else
                        {
                            screen.Move(x, y);
                        }
                    }
                    int dx = 0, dy = 0;
                    MenuKeys heldNow = 0;
                    foreach ((MenuKeys key, int until) in heldKeys)
                    {
                        if (frame >= until) continue;
                        heldNow |= key;
                        dx += key == MenuKeys.Right ? 1 : key == MenuKeys.Left ? -1 : 0;
                        dy += key == MenuKeys.Up ? 1 : key == MenuKeys.Down ? -1 : 0;
                    }
                    screen.Held(dx, dy, heldNow);
                    screen.Tick();
                    if (route.Items && screen is FrontEndScreen fes && fes.ItemStates() is { } items && items != lastItems)
                    {
                        log.Add($"# f{frame:00000} items {items}");
                        lastItems = items;
                    }
                }
                string outPath = Path.ChangeExtension(path, null) + "_ours.txt";
                File.WriteAllLines(outPath, log);
                Console.WriteLine($"{route.Name}: {log.Count(l => !l.StartsWith('#'))} sound calls -> {outPath}");
            }
        }

        // -sndpages [-root <game files>] <out.txt>: every front-end page's touch targets in DS touch-screen pixels (y down,
        // as route files take them): item index, centre, rect, what it does and its first text -- for writing routes
        public static void Pages(string[] args)
        {
            if (!ToolPaths.UseDesktopExtraction()) return;
            string root = Paths.FileSystem, outPath = "pages.txt";
            for (int i = 1; i < args.Length; i++)
            {
                if (args[i] == "-root" && i + 1 < args.Length) root = args[++i];
                else outPath = args[i];
            }
            MenuFile file = MenuFile.Load(Path.Combine(root, "frontend", "metroidhunters.bin"));
            MenuStrings strings = MenuStrings.Load(root);
            var sb = new System.Text.StringBuilder();
            foreach (MenuPage page in file.Pages)
            {
                sb.AppendLine($"P{page.Index}");
                void One(string who, MenuAction a, MenuItem? it)
                {
                    if (!MenuEngine.TryRect(a, it, out float x0, out float y0, out float x1, out float y1) || y1 > 192 || y0 < 0) return;
                    string text = "";
                    if (it != null)
                    {
                        foreach (MenuItemState st in it.States)
                        {
                            if (st.Text != null) { text = strings[st.Text.StringId].Replace('\n', ' '); break; }
                        }
                    }
                    sb.AppendLine($"  {who,-6} tap {(x0 + x1) / 2:0} {192 - (y0 + y1) / 2:0}   rect x {x0:0}-{x1:0} y {192 - y1:0}-{192 - y0:0}" +
                        $"  keys {a.Kind}{(a.TargetPage != 0xFF ? $" -> P{a.TargetPage}" : "")}{(a.Flags != 0 ? $" flags {a.Flags}" : "")}" +
                        $"{(a.Calls.Count > 0 ? " calls " + string.Join(" ", a.Calls.Select(c => $"({c.A},{c.B})")) : "")} {text}");
                }
                foreach (MenuAction a in page.Actions) One("page", a, null);
                foreach (MenuItem it in page.Items)
                {
                    foreach (MenuAction a in it.Actions) One($"[{it.Index}]", a, it);
                }
            }
            File.WriteAllText(outPath, sb.ToString());
            Console.WriteLine($"{file.Pages.Count} pages -> {outPath}");
            // and the whole menu graph of this tree (MenuDump) beside it
            string full = Path.ChangeExtension(outPath, null) + "_graph.md";
            File.WriteAllText(full, MenuDump.Describe(file, strings, null, root));
            Console.WriteLine($"menu graph -> {full}");
        }

        private static IScreen Make(Route route, Action<string> add, Action<string> note, Action<int> setFrame)
        {
            int handles = 0;
            int PlayLoop(int id)
            {
                add($"PlaySfx id=0x{id:x} h={++handles}");
                return handles;
            }
            void Play(int id) => add((id & 0x4000) != 0 ? $"PlayScript id=0x{id:x}" : $"PlaySfx id=0x{id:x}");
            void StopScripts() => add("StopFreeScripts");
            void StopLoop(int h) => add($"StopHandle h={h}");
            switch (route.Screen)
            {
            case "planet":
            {
                var sel = new ShipPlanetSelect(_root, new UiTextureCache())
                {
                    PlaySound = Play, StopScripts = StopScripts, PlayLoop = PlayLoop, StopLoop = StopLoop, Log = note,
                    Land = p => note($"LAND planet {p}")
                };
                string[] names = MphRecomp.Campaign.ShipNavigation.Planets.Select(p => p.Name).ToArray();
                setFrame(-1);
                sel.Open(route.Areas, route.Known, names, route.Current);
                // the vanilla states are idle and interactive: run our intro out, then let the preselect settle
                for (int i = 0, settled = 0; i < 2000 && settled < 60; i++)
                {
                    sel.Build(1280, 720);
                    sel.Tick();
                    if (sel.Interactive) settled++;
                }
                setFrame(0);
                return new PlanetScreen(sel);
            }
            case "brief":
            {
                var brief = new ShipBriefing(_root, new UiTextureCache())
                {
                    PlaySound = Play, StopScripts = StopScripts, Finished = () => note("briefing finished")
                };
                // "screen brief <timer>": the vanilla state is mid-page (briefing.State: page 1, page timer 109)
                setFrame(-1);
                brief.Start();
                int preroll = route.ScreenArgs.Length > 0 ? Num(route.ScreenArgs[0]) : 0;
                for (int i = 0; i < 2 * preroll; i++) brief.Tick();
                setFrame(0);
                return new BriefScreen(brief);
            }
            case "fe":
            {
                string? savesDir = null;
                if (route.Saves.Length > 0)
                {
                    savesDir = Path.Combine(Path.GetTempPath(), "mph_sndtrace_saves");
                    if (Directory.Exists(savesDir)) Directory.Delete(savesDir, recursive: true);
                    Directory.CreateDirectory(savesDir);
                    var saves = new MphRecomp.Campaign.CampaignSaves(savesDir);
                    foreach (char c in route.Saves) saves.CreateNew(c - 'A' + 1);
                }
                FrontendSession session = FrontendSession.Load(_root, savesDir: savesDir);
                // the game's dispatcher: a script id = StopFreeScripts(1) + PlayScript
                session.PlaySound = id =>
                {
                    if ((id & 0x4000) != 0) StopScripts();
                    Play(id);
                };
                session.Log = note;
                session.Request = r => note($"request {r}");
                setFrame(-1);
                session.Start(route.ScreenArgs.Length > 0 ? Num(route.ScreenArgs[0]) : 0);
                // the vanilla states are built pages: run ours until every item has arrived (at the menu's 30 Hz)
                for (int i = 0; i < 1200 && !session.Menu.Settled; i++) session.Tick();
                foreach ((float x, float y) in route.PreTaps)
                {
                    session.Menu.Touch(x, 192 - y);
                    for (int i = 0; i < 1200 && (i < 2 || !session.Menu.Settled); i++) session.Tick();
                }
                setFrame(0);
                return new FrontEndScreen(session);
            }
            default:
            {
                var menu = new ShipMenu(_root, new UiTextureCache())
                {
                    PlaySound = Play, StopScripts = StopScripts, PlayLoop = PlayLoop, StopLoop = StopLoop,
                    SetLoopPitch = (h, p) => add($"UpdateHandle h={h} vol=0xff pan=0xff pitch=0x{p:x}"),
                    Log = note, SaveGame = () => true, Request = r => note($"request {r}"),
                    Story = new StorySave(), RedrawArt = false
                };
                MphRecomp.Campaign.CampaignSaves.RepairLogbook(menu.Story);
                bool ask = route.ScreenArgs.Contains("ask");
                setFrame(-1);
                menu.Open("CELESTIAL ARCHIVES", askSave: ask);
                // the vanilla state *savepopup is saved with the prompt up and taking input (open + 31 frames)
                for (int i = 0; ask && i < 100 && menu.ControlAt("NO") == null; i++) { menu.Build(W, H); menu.Tick(); }
                for (int i = 0; ask && i < 32; i++) { menu.Build(W, H); menu.Tick(); }
                setFrame(0);
                return new ShipScreen(menu);
            }
            }
        }

        // -sndecho: the planet select's thrust tail through DsEcho (preset 0) and the stop's release, against the tail the
        // DS made (BizHawk USA rev 1, sound_census/thrust/report.md section 4: the isolated audio's level after the
        // stop, dB vs its steady state). Plays SHIP_THRUST_LOOP from audio frame 29, releases it at ~119.5 (the DS's
        // StopSfxHandle at f115, its channel release from f118, audio ~3 frames late), at 48 kHz (the Odin's mix rate).
        public static void Echo(string[] args)
        {
            if (!ToolPaths.UseDesktopExtraction()) return;
            const int rate = 48000, frame = rate / 60;
            Formats.Sound.SoundSample sample = Formats.Sound.SoundRead.ReadSoundSamples()[0x1f5];
            byte[] raw = sample.WaveData.Value;
            var pcm = new short[raw.Length / 2];
            Buffer.BlockCopy(raw, 0, pcm, 0, pcm.Length * 2);
            int loopStart = Math.Clamp(sample.LoopStart, 0, pcm.Length - 1);
            MphRecomp.Frontend.DsEcho.SetPreset(0);
            System.Threading.Thread.Sleep(600); // past the 30-frame crossfade: the state ran preset 0 already
            var echo = new MphRecomp.Frontend.DsEcho(rate);
            int frames = 320, n = frames * frame;
            var l = new float[n];
            var r = new float[n];
            double pos = 0, step = sample.SampleRate / (double)rate;
            float env = 1, releaseMul = MathF.Pow(10, -54f / rate);
            int start = 29 * frame, release = (int)(119.5 * frame);
            for (int i = start; i < n; i++)
            {
                if (pos >= pcm.Length) pos = loopStart + (pos - pcm.Length) % (pcm.Length - loopStart);
                int idx = (int)pos;
                float s = pcm[idx] * 80 / 127f;
                if (i >= release)
                {
                    s *= env;
                    env *= releaseMul;
                    if (env < 2.5e-4f) break;
                }
                l[i] = r[i] = s;
                pos += step;
            }
            echo.Process(l, r, n);
            double Rms(int f0, int f1)
            {
                double sum = 0;
                for (int i = f0 * frame; i < f1 * frame; i++) sum += (l[i] * l[i] + r[i] * r[i]) / 2;
                return Math.Sqrt(sum / ((f1 - f0) * frame));
            }
            double steady = Rms(60, 110);
            // past f200 the DS's tail flattens on its fixed-point maths (>> 12 truncation, the +0x20 output bias: rms 8 of
            // ~2100 at f300), which a float echo doesn't have -- shown, not checked
            (int F, double Db, bool Check)[] ds = { (125, -3.4, true), (140, -8.4, true), (150, -10.8, true), (170, -18.2, true),
                (200, -27.8, true), (240, -37.5, false), (300, -48.9, false) };
            int bad = 0;
            foreach ((int f, double want, bool check) in ds)
            {
                double got = 20 * Math.Log10(Math.Max(1e-9, Rms(f, f + 1) / steady));
                bool ok = Math.Abs(got - want) <= 3;
                if (check && !ok) bad++;
                Console.WriteLine($"{(!check ? "info" : ok ? "PASS" : "FAIL")} f{f}: ours {got:0.0} dB, DS {want:0.0} dB");
            }
            MphRecomp.Frontend.DsEcho.Stop();
            Console.WriteLine($"{ds.Count(d => d.Check) - bad} passed, {bad} failed (tolerance 3 dB)");
        }

        private const int W = 1280, H = 720;

        private sealed class ShipScreen : IScreen
        {
            private readonly ShipMenu _m;
            public ShipScreen(ShipMenu m)
            {
                _m = m;
                _m.Build(W, H);
            }
            // a DS point: on the open page as is; on the main page, the DS control under it isn't where ours is -- name it
            private (float X, float Y) Map(float x, float y) => _m.CardToCanvas(x, y) ?? (x * H / 192f, y * H / 192f);
            public void Down(float x, float y, bool canvas)
            {
                _m.Build(W, H);
                (float cx, float cy) = canvas ? (x, y) : Map(x, y);
                _m.Touch(cx, cy);
            }
            public void Move(float x, float y) { (float cx, float cy) = Map(x, y); _m.TouchMove(cx, cy); }
            public void Up(float x, float y, bool canvas) { (float cx, float cy) = canvas ? (x, y) : Map(x, y); _m.TouchUp(cx, cy); }
            public (float X, float Y)? Named(string name)
            {
                _m.Build(W, H);
                return _m.ControlAt(name);
            }
            public void Key(MenuKeys key)
            {
                if (key is MenuKeys.Up or MenuKeys.Down or MenuKeys.Left or MenuKeys.Right)
                {
                    _m.Navigate(key == MenuKeys.Right ? 1 : key == MenuKeys.Left ? -1 : 0, key == MenuKeys.Up ? 1 : key == MenuKeys.Down ? -1 : 0);
                }
                else
                {
                    _m.Press(key);
                }
            }
            public void Held(int dx, int dy, MenuKeys keys) { }
            public void Tick() { _m.Tick(); _m.Build(W, H); }
        }

        private sealed class PlanetScreen : IScreen
        {
            private readonly ShipPlanetSelect _s;
            public PlanetScreen(ShipPlanetSelect s) { _s = s; _s.Build(W, H); }
            public void Down(float x, float y, bool canvas) { (float cx, float cy) = canvas ? (x, y) : _s.DsToCanvas(x, y); _s.Touch(cx, cy); }
            public void Move(float x, float y) { (float cx, float cy) = _s.DsToCanvas(x, y); _s.TouchMove(cx, cy); }
            public void Up(float x, float y, bool canvas) { (float cx, float cy) = canvas ? (x, y) : _s.DsToCanvas(x, y); _s.TouchUp(cx, cy); }
            public (float X, float Y)? Named(string name) => name.ToUpperInvariant() == "LAND" ? _s.LandCenter : null;
            public void Key(MenuKeys key)
            {
                if (key is not (MenuKeys.Up or MenuKeys.Down or MenuKeys.Left or MenuKeys.Right)) _s.Press(key);
            }
            public void Held(int dx, int dy, MenuKeys keys) => _s.Hold(dx, dy);
            public void Tick() { _s.Tick(); _s.Build(W, H); }
        }

        private sealed class BriefScreen : IScreen
        {
            private readonly ShipBriefing _b;
            public BriefScreen(ShipBriefing b) => _b = b;
            public void Down(float x, float y, bool canvas) => _b.Touch(canvas ? x : x * H / 192f, canvas ? y : y * H / 192f);
            public void Move(float x, float y) { }
            public void Up(float x, float y, bool canvas) { }
            public (float X, float Y)? Named(string name) => null;
            public void Key(MenuKeys key)
            {
                if (key is MenuKeys.Start) _b.Skip(); // the device's pad shortcut; vanilla: B does nothing, only SKIP
            }
            // held A types fast (the page timer +2 a tick)
            public void Held(int dx, int dy, MenuKeys keys) => _b.Fast = (keys & MenuKeys.A) != 0;
            public void Tick() { _b.Tick(); _b.Build(W, H); }
        }

        private sealed class FrontEndScreen : IScreen
        {
            private readonly FrontendSession _s;
            public FrontEndScreen(FrontendSession s) => _s = s;
            // the menu engine takes DS text coordinates: y up from the touch screen's bottom
            public void Down(float x, float y, bool canvas)
            {
                if (canvas) _s.TouchCanvas(x, y);
                else _s.Menu.Touch(x, 192 - y);
            }
            public void Move(float x, float y) { }
            public void Up(float x, float y, bool canvas) { }
            public (float X, float Y)? Named(string name) => null;
            public void Key(MenuKeys key)
            {
                if (key is MenuKeys.Up or MenuKeys.Down or MenuKeys.Left or MenuKeys.Right)
                {
                    _s.Navigate(key == MenuKeys.Right ? 1 : key == MenuKeys.Left ? -1 : 0, key == MenuKeys.Up ? 1 : key == MenuKeys.Down ? -1 : 0);
                }
                else
                {
                    _s.Press(key);
                }
            }
            public void Held(int dx, int dy, MenuKeys keys) { }
            // the device ticks the menu at MenuEngine.FramesPerSecond (30) off 60 Hz frames, input applied as it comes
            private int _frames;
            public void Tick()
            {
                if (++_frames % (60 / MenuEngine.FramesPerSecond) == 0) _s.Tick();
            }
            // vanilla's "#!items" format: item index:state code (to*8+from for a transition)
            public string? ItemStates()
            {
                MenuEngine m = _s.Menu;
                if (m.Page == null) return null;
                return $"P{m.Page.Index} " + String.Join(' ', m.Page.Items.Select((it, i) => $"{it.Index}:{m.ItemCode(i):x2}"));
            }
        }
    }
}
