using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using MphRecomp.Frontend;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace MphRead
{
    // Front-end (menus) from the user's ROM, headless:
    //   -fedump                       every page of frontend/metroidhunters.bin as readable text ->
    //                                 extract_out/frontend/menus.md (widgets, text, states, links, actions, timers)
    //   -ferender <page|all> [frames] run the menu engine on a page for `frames` ticks (default 120) and draw the two
    //                                 DS screens stacked (top above touch), 3x -> extract_out/frontend/page_<n>.png
    //   -ferender <page> gif          the page's first 4 seconds as an animated GIF (page_<n>.gif)
    internal static class FrontendTool
    {
        internal static string OutDir()
        {
            string dir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "extract_out", "frontend"));
            Directory.CreateDirectory(dir);
            return dir;
        }

        private sealed class LogHost : IMenuHost
        {
            public readonly List<string> Log = new();
            public bool OnCall(MenuEngine menu, MenuAction action, int a, int b, int item)
            {
                Log.Add($"  frame {menu.PageFrame}: page {menu.Page?.Index} item {item} call ({a}, {b})");
                return true;
            }
            public void OnPageEntered(MenuEngine menu, int page) => Log.Add($"  frame {menu.PageFrame}: entered page {page}");
            public void OnLog(string message) => Log.Add("  " + message);
        }

        private static (MenuEngine Engine, UiTextureCache Textures, LogHost Host) Load()
        {
            string root = Paths.FileSystem;
            MenuFile file = MenuFile.Load(Path.Combine(root, "frontend", "metroidhunters.bin"));
            var textures = new UiTextureCache();
            var engine = new MenuEngine(file, MenuStrings.Load(root), new MenuWidgets(file, textures), new MenuFont(textures));
            var host = new LogHost();
            engine.Host = host;
            return (engine, textures, host);
        }

        public static void Dump(string[] args)
        {
            if (!ToolPaths.UseDesktopExtraction()) return;
            string root = Paths.FileSystem;
            MenuFile file = MenuFile.Load(Path.Combine(root, "frontend", "metroidhunters.bin"));
            MenuStrings strings = MenuStrings.Load(root);
            string text = MenuDump.Describe(file, strings, new MenuWidgets(file, new UiTextureCache()), $"ROM {Paths.MphKey}");
            string path = Path.Combine(OutDir(), args.Length >= 2 ? args[1] : "menus.md");
            File.WriteAllText(path, text);
            Console.WriteLine($"{file.Pages.Count} pages, {file.Widgets.Count} widget refs, {strings.Strings.Count} strings -> {path}");
        }

        // -fesaves <dir>: test saves for the file select -- file A a real campaign save with some pickups (3 missile
        // expansions, 2 UA expansions, 2 energy tanks, 2 octoliths), file C a damaged one
        public static void Saves(string[] args)
        {
            if (!ToolPaths.UseDesktopExtraction()) return;
            var saves = new MphRecomp.Campaign.CampaignSaves(args[1]);
            var story = new StorySave();
            story.AmmoMax[1] += 3 * 100;
            story.AmmoMax[0] += 2 * 300;
            story.HealthMax += 2 * Metadata.PlayerValues[0].EnergyTank;
            story.FoundOctoliths = story.CurrentOctoliths = 0b11;
            saves.Save(1, new MphRecomp.Campaign.CampaignSaveFile
            {
                Hunter = Hunter.Samus, Planet = MphRecomp.Campaign.Planet.CelestialArchives,
                SavedAtUtc = DateTime.UtcNow, PlaySeconds = 4321, Story = story
            });
            File.WriteAllText(saves.PathFor(3), "{ this is not a save");
            foreach (var sum in saves.SummarizeSlots(1, 3))
                Console.WriteLine($"slot {sum.Slot}: exists {sum.Exists} readable {sum.Readable} {sum.CompletionPercent}% octoliths {sum.Octoliths}" +
                    $" missile exp {sum.MissileExpansions} UA exp {sum.UaExpansions} tanks {sum.EnergyTanks} {sum.Error}");
        }

        public static void Render(string[] args)
        {
            if (!ToolPaths.UseDesktopExtraction()) return;
            (MenuEngine engine, UiTextureCache textures, LogHost host) = Load();
            bool gif = args.Length >= 3 && args[2] == "gif";
            int frames = !gif && args.Length >= 3 ? Int32.Parse(args[2]) : 120;
            IEnumerable<int> pages = args[1] == "all" ? Enumerable.Range(0, engine.File.Pages.Count) : new[] { Int32.Parse(args[1]) };
            const int scale = 3;
            foreach (int page in pages)
            {
                host.Log.Clear();
                engine.Enter(page);
                var tris = new List<WidgetTri>();
                if (gif)
                {
                    using var anim = new Image<Rgba32>(256 * scale / 2, 384 * scale / 2);
                    anim.Frames.RemoveFrame(0);
                    for (int f = 0; f < 4 * MenuEngine.FramesPerSecond; f++)
                    {
                        if (f % 3 == 0)
                        {
                            tris.Clear();
                            engine.Collect(tris);
                            using Image<Rgba32> img = Raster(tris, textures, scale / 2f);
                            img.Frames.RootFrame.Metadata.GetGifMetadata().FrameDelay = 5;
                            anim.Frames.AddFrame(img.Frames.RootFrame);
                        }
                        engine.Tick();
                    }
                    string gp = Path.Combine(OutDir(), $"page_{page}.gif");
                    anim.SaveAsGif(gp);
                    Console.WriteLine($"page {page} -> {gp}");
                }
                else
                {
                    for (int f = 0; f < frames && engine.Page?.Index == page; f++) engine.Tick();
                    tris.Clear();
                    engine.Collect(tris);
                    using Image<Rgba32> img = Raster(tris, textures, scale);
                    if (Environment.GetEnvironmentVariable("FE_VERBOSE") == "1")
                    {
                        foreach (var g in tris.GroupBy(t => t.Item))
                        {
                            var all = g.SelectMany(t => new[] { t.A, t.B, t.C }).ToList();
                            Console.WriteLine($"  item {g.Key} code {MenuStateCode.Name(engine.ItemCode(g.Key))} tris {g.Count()} " +
                                $"x {all.Min(v => v.X):0.#}..{all.Max(v => v.X):0.#} y {all.Min(v => v.Y):0.#}..{all.Max(v => v.Y):0.#} " +
                                $"z {g.Min(t => t.Z):0.#} alpha {all.Max(v => v.A):0.##} tex {g.First().TextureId}");
                        }
                        for (int i = 0; i < engine.Page!.Items.Count; i++)
                            if (!tris.Any(t => t.Item == i)) Console.WriteLine($"  item {i} code {MenuStateCode.Name(engine.ItemCode(i))} (nothing drawn)");
                    }
                    string path = Path.Combine(OutDir(), $"page_{page}.png");
                    img.SaveAsPng(path);
                    Console.WriteLine($"page {page}: {tris.Count} tris, now on page {engine.Page?.Index} -> {path}");
                }
                foreach (string line in host.Log) Console.WriteLine(line);
            }
        }

        // -fecanvas <page> [frames]: the page through the full FrontendSession (layout, backgrounds, tint) on canvases of
        // several aspect ratios -> extract_out/frontend/canvas_<page>.png (16:9, 20:9, 16:10, 4:3, 21:9)
        // FE_ROOT=<game files>: another ROM's tree (e.g. USA rev 1) -> canvas_<page>_<tree folder name>.png
        public static void Canvas(string[] args)
        {
            if (!ToolPaths.UseDesktopExtraction()) return;
            bool pause = args[1] == "pause";
            int page = pause ? -1 : Int32.Parse(args[1]);
            int frames = args.Length >= 3 ? Int32.Parse(args[2]) : 120;
            string? root = Environment.GetEnvironmentVariable("FE_ROOT");
            if (root != null)
            {
                // the widgets' models load through MphRead's own paths: point its rev 1 key at the tree (this run only;
                // paths.txt is untouched)
                Paths.SetPath(Ver.AMHE1, root);
                Paths.MphKey = Ver.AMHE1;
            }
            // FE_SETTINGS=<recomp_settings.json>: those settings (a scratch copy: RECOMP SETTINGS saves changes at once);
            // FE_PUBLIC=1: as a public build (RECOMP SETTINGS without the rows a public build leaves out)
            MphRecomp.Config.RecompSettings.PublicBuild = Environment.GetEnvironmentVariable("FE_PUBLIC") == "1";
            // FE_DESKTOP=1: as the Windows build (its own rows and buttons: keyboard & mouse, no gyro / touch rows)
            MphRecomp.Config.RecompSettings.DesktopHost = Environment.GetEnvironmentVariable("FE_DESKTOP") == "1";
            FrontendSession session = FrontendSession.Load(root ?? Paths.FileSystem, settingsPath: Environment.GetEnvironmentVariable("FE_SETTINGS"),
                modsDir: Environment.GetEnvironmentVariable("FE_MODS"), savesDir: Environment.GetEnvironmentVariable("FE_SAVES"));
            var log = new List<string>();
            session.Log = log.Add;
            session.Request = r => log.Add("request " + r);
            if (pause) session.OpenPause();
            else session.Start(page);
            string? measure = Environment.GetEnvironmentVariable("FE_MEASURE");
            if (measure != null)
            {
                // FE_WRAP=<px>: also the line count at that wrap width (230 = the rows' description band)
                int wrap = Int32.TryParse(Environment.GetEnvironmentVariable("FE_WRAP"), out int w) ? w : 30;
                foreach (string s in measure.Split('|'))
                    Console.WriteLine($"measure \"{s}\" = {session.Menu.Font.Measure(s)}px, {session.Menu.Font.Lines(s, wrap).Count} line(s) at wrap {wrap} (30 = SENSITIVITY value width)");
            }
            // FE_PRESS=r/l/a/b/start: one button press a few frames in (e.g. r to page a rows page over) before the shot;
            // a comma list (also up/down/left/right) presses each 20 frames apart, e.g. "down,a" = pause -> recomp settings
            string[] presses = (Environment.GetEnvironmentVariable("FE_PRESS") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries);
            for (int f = 0; f < frames; f++)
            {
                if (f >= 5 && (f - 5) % 20 == 0 && (f - 5) / 20 < presses.Length)
                {
                    string key = presses[(f - 5) / 20].Trim().ToLowerInvariant();
                    if (key.StartsWith("cap:", StringComparison.Ordinal))
                    {
                        // cap:<button>: the control customizer's next press (a ControlBinds name, e.g. cap:L1, cap:Mouse.Left)
                        session.Recomp.CaptureInput(presses[(f - 5) / 20].Trim()[4..]);
                    }
                    else if (key.StartsWith("tap@", StringComparison.Ordinal))
                    {
                        // tap@<x>;<y>: a touch at DS touch-screen pixel (x, y), y down
                        string[] xy = key[4..].Split(';');
                        session.TouchDs(Single.Parse(xy[0], System.Globalization.CultureInfo.InvariantCulture),
                            Single.Parse(xy[1], System.Globalization.CultureInfo.InvariantCulture));
                    }
                    else if (key is "up" or "down" or "left" or "right")
                    {
                        session.Navigate(key == "right" ? 1 : key == "left" ? -1 : 0, key == "up" ? 1 : key == "down" ? -1 : 0);
                    }
                    else
                    {
                        session.Press(key switch
                        {
                            "r" => MenuKeys.R, "l" => MenuKeys.L, "a" => MenuKeys.A, "b" => MenuKeys.B, "x" => MenuKeys.X, "start" => MenuKeys.Start,
                            _ => MenuKeys.None
                        });
                    }
                }
                session.Tick();
            }
            if (Environment.GetEnvironmentVariable("FE_VERBOSE") == "1" && session.Menu.Page != null)
            {
                for (int i = 0; i < session.Menu.Page.Items.Count; i++)
                    Console.WriteLine($"  item {i} state {session.Menu.ItemState(i)} code {MenuStateCode.Name(session.Menu.ItemCode(i))}");
            }
            // FE_TRIS=<item>: that item's triangles in menu space (x, y, u, v per corner; texture size)
            if (Int32.TryParse(Environment.GetEnvironmentVariable("FE_TRIS"), out int trisItem))
            {
                var tris = new List<WidgetTri>();
                session.Menu.Collect(tris);
                foreach (WidgetTri t in tris.Where(t => t.Item == trisItem))
                {
                    string tex = t.TextureId < 0 ? "none" : $"{session.Textures[t.TextureId].Width}x{session.Textures[t.TextureId].Height}";
                    Console.WriteLine(FormattableString.Invariant($"  tri tex {tex}: ({t.A.X:0.#},{t.A.Y:0.#} uv {t.A.U:0.###},{t.A.V:0.###}) ({t.B.X:0.#},{t.B.Y:0.#} uv {t.B.U:0.###},{t.B.V:0.###}) ({t.C.X:0.#},{t.C.Y:0.#} uv {t.C.U:0.###},{t.C.V:0.###})"));
                }
            }
            (int W, int H)[] sizes = { (960, 540), (1200, 540), (864, 540), (720, 540), (1260, 540) };
            int total = sizes.Sum(s => s.W) + 10 * (sizes.Length - 1);
            using var sheet = new Image<Rgba32>(total, 540, new Rgba32(60, 60, 60, 255));
            int x = 0;
            foreach ((int w, int h) in sizes)
            {
                session.Build(w, h);
                using Image<Rgba32> img = RasterCanvas(session.DrawList, w, h);
                sheet.Mutate(c => c.DrawImage(img, new SixLabors.ImageSharp.Point(x, 0), 1f));
                x += w + 10;
            }
            string tag = root == null ? "" : "_" + new DirectoryInfo(root).Name;
            string path = Path.Combine(OutDir(), $"canvas_{page}{tag}.png");
            sheet.SaveAsPng(path);
            Console.WriteLine($"page {page} (now {session.Menu.Page?.Index}) -> {path}");
            foreach (string line in log) Console.WriteLine("  " + line);
        }

        // -feflow [script]: the whole front end from boot (page 0) at 16:9 through FrontendSession, driven by a script
        // of "<frame>:<input>" steps (input = A, B, Start, up/down/left/right, touch@x,y in canvas pixels of 640x360);
        // a snapshot every 30 frames -> extract_out/frontend/flow.png (contact sheet) plus the page/request log
        public static void Flow(string[] args)
        {
            if (!ToolPaths.UseDesktopExtraction()) return;
            string script = args.Length >= 2 ? args[1] : "1500:A,1700:A,1800:right,1860:left,1900:A,2050:A";
            var steps = script.Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(t => t.Split(':')).Select(p => (Frame: Int32.Parse(p[0]), Input: p[1])).ToList();
            int end = args.Length >= 3 ? Int32.Parse(args[2]) : steps.Max(t => t.Frame) + 240;
            string? root = Environment.GetEnvironmentVariable("FE_ROOT"); // another ROM's tree, as -fecanvas
            if (root != null)
            {
                Paths.SetPath(Ver.AMHE1, root);
                Paths.MphKey = Ver.AMHE1;
            }
            FrontendSession session = FrontendSession.Load(root ?? Paths.FileSystem, modsDir: Environment.GetEnvironmentVariable("FE_MODS"), savesDir: Environment.GetEnvironmentVariable("FE_SAVES"));
            var log = new List<string>();
            session.Log = m => log.Add($"{session.Frame}: {m}");
            session.Request = r => log.Add($"{session.Frame}: request {r}");
            if (Environment.GetEnvironmentVariable("FE_START") == "pause") session.OpenPause();
            else session.Start(Int32.TryParse(Environment.GetEnvironmentVariable("FE_START"), out int startPage) ? startPage : 0);
            // FE_EVERY=<ticks>: the snapshot interval; FE_SHOTS=<t>,<t>,...: extra snapshots at those ticks
            int every = Int32.TryParse(Environment.GetEnvironmentVariable("FE_EVERY"), out int e) && e > 0 ? e : 30;
            var shotAt = new HashSet<int>((Environment.GetEnvironmentVariable("FE_SHOTS") ?? "")
                .Split(',', StringSplitOptions.RemoveEmptyEntries).Select(Int32.Parse));
            // FE_SIZE=<w>x<h>: the canvas (default 640x360); FE_FRAMES=1: every snapshot also saved full size as
            // flow_<tick>.png
            int w = 640, h = 360;
            string[]? size = Environment.GetEnvironmentVariable("FE_SIZE")?.Split('x');
            if (size?.Length == 2) { w = Int32.Parse(size[0]); h = Int32.Parse(size[1]); }
            bool frames = Environment.GetEnvironmentVariable("FE_FRAMES") == "1";
            var shots = new List<(long Frame, Image<Rgba32> Img)>();
            for (int f = 0; f <= end; f++)
            {
                foreach (var st in steps.Where(t => t.Frame == f))
                {
                    int before = session.Menu.FocusedItem();
                    log.Add($"{f}: input {st.Input} (focus was {before})");
                    switch (st.Input.ToLowerInvariant())
                    {
                    case "a": session.Press(MenuKeys.A); break;
                    case "b": session.Press(MenuKeys.B); break;
                    case "start": session.Press(MenuKeys.Start); break;
                    case "up": session.Navigate(0, 1); break;
                    case "down": session.Navigate(0, -1); break;
                    case "left": session.Navigate(-1, 0); break;
                    case "right": session.Navigate(1, 0); break;
                    case "l": session.Press(MenuKeys.L); break;
                    case "r": session.Press(MenuKeys.R); break;
                    default:
                        if (st.Input.StartsWith("touch@"))
                        {
                            var xy = st.Input[6..].Split('/');
                            session.TouchCanvas(Single.Parse(xy[0]), Single.Parse(xy[1]));
                        }
                        else if (st.Input.StartsWith("ds@")) // a DS touch-screen pixel, y down (as BizHawk routes give them)
                        {
                            var xy = st.Input[3..].Split('/');
                            session.TouchDs(Single.Parse(xy[0]), Single.Parse(xy[1]));
                        }
                        break;
                    }
                }
                if (f % every == 0 || shotAt.Contains(f))
                {
                    session.Build(w, h);
                    shots.Add((f, RasterCanvas(session.DrawList, w, h)));
                    if (frames) shots[^1].Img.SaveAsPng(Path.Combine(OutDir(), $"flow_{f}.png"));
                }
                session.Tick();
            }
            const int cols = 8, tw = 320, th = 180;
            int rows = (shots.Count + cols - 1) / cols;
            using var sheet = new Image<Rgba32>(cols * (tw + 4), rows * (th + 4), new Rgba32(40, 40, 40, 255));
            for (int i = 0; i < shots.Count; i++)
            {
                using Image<Rgba32> small = shots[i].Img.Clone(c => c.Resize(tw, th));
                sheet.Mutate(c => c.DrawImage(small, new SixLabors.ImageSharp.Point(i % cols * (tw + 4), i / cols * (th + 4)), 1f));
                shots[i].Img.Dispose();
            }
            string path = Path.Combine(OutDir(), Environment.GetEnvironmentVariable("FE_OUT") ?? "flow.png");
            sheet.SaveAsPng(path);
            foreach (string line in log) Console.WriteLine(line);
            Console.WriteLine($"{shots.Count} snapshots every {every} frames -> {path}");
        }

        // -feship ["<frame>:<input>,..." [end]]: the in-ship menu (Core ShipMenu) at 16:9 -> ship_flow.png (a snapshot per
        // input, after it settles) + ship_aspects.png (the main page at 5 aspect ratios). Inputs as -feflow; touch@x/y is
        // in 1280x720 canvas pixels.
        public static void Ship(string[] args)
        {
            if (!ToolPaths.UseDesktopExtraction()) return;
            var textures = new UiTextureCache();
            var menu = new ShipMenu(Paths.FileSystem, textures);
            // the 3D window is the host's; a preview stands in with the real game's top screen (FE_SHIPVIEW=<png>,
            // taken as a 1920x1080 capture whose left 1440 px are the top screen)
            string? view = Environment.GetEnvironmentVariable("FE_SHIPVIEW");
            if (view != null && File.Exists(view))
            {
                using Image<Rgba32> cap = Image.Load<Rgba32>(view);
                cap.Mutate(c => c.Crop(new Rectangle(0, 0, cap.Width * 3 / 4, cap.Height)).Resize(256, 192));
                var px = new byte[256 * 192 * 4];
                cap.CopyPixelDataTo(px);
                menu.PreviewTexture = textures.GetOrAdd("bg/shipview", () => (256, 192, px)).Id;
            }
            var log = new List<string>();
            menu.Log = m => log.Add(m);
            menu.SaveGame = () => { log.Add("save requested"); return true; };
            menu.Request = r => log.Add($"request {r}");
            menu.Story = new StorySave();
            // FE_SHIPWEAPONS=1: the test save owns Volt Driver, Battlehammer and Judicator
            if (Environment.GetEnvironmentVariable("FE_SHIPWEAPONS") == "1")
            {
                menu.Story.Weapons |= (ushort)(1 << (int)BeamType.VoltDriver | 1 << (int)BeamType.Battlehammer | 1 << (int)BeamType.Judicator);
                menu.Story.HealthMax += 3 * Metadata.PlayerValues[0].EnergyTank;
                menu.Story.FoundOctoliths = 0b1011;
            }
            // FE_SHIPWEAPONS=all: a full inventory (the six affinity weapons, 7 tanks, every octolith), as a 100% file
            else if (Environment.GetEnvironmentVariable("FE_SHIPWEAPONS") == "all")
            {
                menu.Story.Weapons = 0xFF;
                menu.Story.HealthMax = 99 + 7 * Metadata.PlayerValues[0].EnergyTank;
                menu.Story.FoundOctoliths = 0xFF;
            }
            menu.OptionsChanged = () => log.Add($"options: control {menu.ControlType} sensitivity {menu.Sensitivity} invert {menu.LookInvert}");
            menu.WeaponsChanged = () => log.Add($"equipped {(BeamType)menu.Story.WeaponSlots[2]}");
            menu.Open("CELESTIAL ARCHIVES");
            var fresh = new StorySave();
            MphRecomp.Campaign.CampaignSaves.RepairLogbook(fresh);
            log.Add($"logbook (new game): lore {ShipLogbook.Percent(fresh, LogCategory.Lore)}% bioform {ShipLogbook.Percent(fresh, LogCategory.Bioform)}%"
                + $" object {ShipLogbook.Percent(fresh, LogCategory.Object)}% equipment {ShipLogbook.Percent(fresh, LogCategory.Equipment)}%"
                + $" scans {ShipLogbook.Percent(fresh, LogCategory.Lore, LogCategory.Bioform, LogCategory.Object)}%; equipment entries: "
                + String.Join(", ", ShipLogbook.Entries(fresh, LogCategory.Equipment).Select(e => e.Name)));
            menu.RedrawArt = Environment.GetEnvironmentVariable("FE_PIXELART") != "1";
            // FE_DESKTOP=1: OPTIONS as the Windows build has it (controller and keyboard & mouse tabs)
            menu.KeyboardControls = Environment.GetEnvironmentVariable("FE_DESKTOP") == "1";
            // FE_CLASSIC=1: OPTIONS with the game's four control types (RecompSettings.ClassicControlsPage)
            menu.ClassicControls = Environment.GetEnvironmentVariable("FE_CLASSIC") == "1";
            for (int i = 0; i < 300 && !menu.RedrawReady; i++)
            {
                menu.Build(1280, 720);
                System.Threading.Thread.Sleep(20);
            }
            var aspects = new[] { (1280, 720), (1560, 720), (1152, 720), (960, 720), (1680, 720) };
            int total = aspects.Sum(a => a.Item1) + 4 * (aspects.Length - 1);
            using (var sheet = new Image<Rgba32>(total, 720, new Rgba32(40, 40, 40, 255)))
            {
                int x = 0;
                foreach ((int w, int h) in aspects)
                {
                    menu.Build(w, h);
                    using Image<Rgba32> img = RasterCanvas(menu.DrawList, w, h);
                    sheet.Mutate(c => c.DrawImage(img, new SixLabors.ImageSharp.Point(x, 0), 1f));
                    x += w + 4;
                }
                sheet.SaveAsPng(Path.Combine(OutDir(), "ship_aspects.png"));
            }
            string script = args.Length >= 2 ? args[1] : "";
            var steps = script.Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(t => t.Split(':')).Select(p => (Frame: Int32.Parse(p[0]), Input: p[1])).ToList();
            int end = args.Length >= 3 ? Int32.Parse(args[2]) : (steps.Count == 0 ? 0 : steps.Max(t => t.Frame) + 60);
            var shots = new List<Image<Rgba32>>();
            // FE_SHIPH=<px>: a taller canvas for the snapshots (960 = 4:3, the whole OPTIONS card with LOOK INVERT)
            const int W = 1280;
            int H = Int32.TryParse(Environment.GetEnvironmentVariable("FE_SHIPH"), out int shipH) ? shipH : 720;
            for (int f = 0; f <= end; f++)
            {
                foreach (var st in steps.Where(t => t.Frame == f))
                {
                    log.Add($"{f}: {st.Input}");
                    switch (st.Input.ToLowerInvariant())
                    {
                    case "a": menu.Press(MenuKeys.A); break;
                    case "b": menu.Press(MenuKeys.B); break;
                    case "up": menu.Navigate(0, 1); break;
                    case "down": menu.Navigate(0, -1); break;
                    case "left": menu.Navigate(-1, 0); break;
                    case "right": menu.Navigate(1, 0); break;
                    case "shot": break;
                    default:
                        // tap@<NAME>: a control by name (SAVE, EXIT, LAUNCH, WEAPON, OPTIONS, LOGBOOK, YES, NO, OK)
                        if (st.Input.StartsWith("tap@"))
                        {
                            menu.Build(W, H);
                            if (menu.ControlAt(st.Input[4..]) is { } at)
                            {
                                menu.Touch(at.X, at.Y);
                                menu.TouchUp(at.X, at.Y);
                            }
                            else log.Add($"{f}: no control {st.Input[4..]}");
                        }
                        else if (st.Input.StartsWith("touch@") || st.Input.StartsWith("move@") || st.Input.StartsWith("up@"))
                        {
                            var xy = st.Input[(st.Input.IndexOf('@') + 1)..].Split('/');
                            float tx = Single.Parse(xy[0]), ty = Single.Parse(xy[1]);
                            menu.Build(W, H);
                            if (st.Input.StartsWith("touch@")) menu.Touch(tx, ty);
                            else if (st.Input.StartsWith("move@")) menu.TouchMove(tx, ty);
                            else menu.TouchUp(tx, ty);
                        }
                        break;
                    }
                }
                menu.Tick();
                if (steps.Any(t => t.Frame + 10 == f))
                {
                    menu.Build(W, H);
                    shots.Add(RasterCanvas(menu.DrawList, W, H));
                }
            }
            if (shots.Count > 0)
            {
                const int cols = 3, tw = 640;
                int th = H / 2;
                using var sheet = new Image<Rgba32>(cols * (tw + 4), (shots.Count + cols - 1) / cols * (th + 4), new Rgba32(40, 40, 40, 255));
                for (int i = 0; i < shots.Count; i++)
                {
                    using Image<Rgba32> small = shots[i].Clone(c => c.Resize(tw, th));
                    sheet.Mutate(c => c.DrawImage(small, new SixLabors.ImageSharp.Point(i % cols * (tw + 4), i / cols * (th + 4)), 1f));
                    shots[i].Dispose();
                }
                sheet.SaveAsPng(Path.Combine(OutDir(), "ship_flow.png"));
            }
            foreach (string line in log) Console.WriteLine(line);
            Console.WriteLine($"-> {OutDir()} ship_aspects.png" + (shots.Count > 0 ? $", ship_flow.png ({shots.Count} snapshots)" : ""));
        }

        // -fecockpit: the in-ship menu's cockpit views (CampaignShip.cs) in every landing room: a revisit's LAND camseq
        // must finish (the parked loop then takes over and keeps going), LAUNCH SHIP's take-off runs its 150 + 60 ticks;
        // prints each sequence's length in 60 Hz ticks and where the camera went
        public static void Cockpit(string[] args)
        {
            if (!ToolPaths.UseDesktopExtraction()) return;
            int failed = 0;
            static string F(OpenTK.Mathematics.Vector3 p) => $"({p.X:0.0}, {p.Y:0.0}, {p.Z:0.0})";
            foreach (MphRecomp.Campaign.PlanetInfo planet in MphRecomp.Campaign.ShipNavigation.Planets)
            {
                var seqs = MphRecomp.Campaign.ShipNavigation.CockpitSeqs(planet.Id);
                try
                {
                    var save = new StorySave();
                    save.SetVisitedRoom(planet.LandingRoomId);
                    using var host = MphRecomp.Campaign.CampaignHost.Start(planet.LandingRoom, save: save, arriving: false);
                    MphRead.Music.PlaySeq(MphRecomp.Campaign.ShipNavigation.CockpitMusic(planet.Id));
                    var cam = host.Player.CameraInfo;
                    Formats.CameraSequence Start(int id, bool loop)
                    {
                        var seq = Formats.CameraSequence.Load(id, host.Scene);
                        seq.Initialize();
                        seq.SetUp(cam, 0);
                        seq.Flags |= Formats.CamSeqFlags.BlockInput | (loop ? Formats.CamSeqFlags.Loop : Formats.CamSeqFlags.None);
                        cam.Update();
                        return seq;
                    }
                    // ticks until the sequence completes (-1: still running after max)
                    int Run(Formats.CameraSequence seq, int max)
                    {
                        for (int t = 1; t <= max; t++)
                        {
                            if (Formats.CameraSequence.Current == seq) seq.Process();
                            host.Step(new MphRecomp.Campaign.CampaignInput());
                            if (seq.Flags.TestFlag(Formats.CamSeqFlags.Complete)) return t;
                        }
                        return -1;
                    }
                    var land = Start(seqs.Land, loop: false);
                    var from = cam.Position;
                    int landTicks = Run(land, 1800);
                    var down = cam.Position;
                    var loop = Start(seqs.Loop, loop: true);
                    int loopEnd = Run(loop, 600);
                    var takeoff = Start(seqs.Takeoff, loop: false);
                    var before = cam.Position;
                    int takeoffEnd = Run(takeoff, 210);
                    var up = cam.Position;
                    bool ok = landTicks > 0 && loopEnd < 0 && !host.Ended && Formats.CameraSequence.Current == takeoff;
                    if (!ok) failed++;
                    Console.WriteLine($"{(ok ? "PASS" : "FAIL")} {planet.Name} ({planet.LandingRoom}): land camseq {seqs.Land} "
                        + $"{landTicks} ticks ({landTicks / 60f:0.00} s) {F(from)} -> {F(down)}; loop {seqs.Loop} "
                        + $"{(loopEnd < 0 ? "still running after 600" : "ENDED at " + loopEnd)}; take-off {seqs.Takeoff} "
                        + $"{F(before)} -> {F(up)} after 210{(takeoffEnd > 0 ? " (ended at " + takeoffEnd + ")" : "")}");
                }
                catch (Exception ex)
                {
                    failed++;
                    Console.WriteLine($"FAIL {planet.Name}: {ex.GetType().Name}: {ex.Message}");
                    Console.WriteLine(ex.StackTrace);
                }
            }
            Console.WriteLine($"{MphRecomp.Campaign.ShipNavigation.Planets.Count - failed} passed, {failed} failed");
        }

        // -feredraw <archive-relative .bin> [scale=5.625] [row0 row1]: a DS BG layer redrawn as clean vector line art
        // (MphRecomp.Frontend.Redraw.PixelRedraw, the port of the approved trace_v11.py) ->
        // extract_out/frontend/redraw_<name>.png: the original nearest-scaled above, the redraw below, both over the
        // dark backdrop (20, 24, 34). Prints the time of a cold (JIT) and a warm call.
        public static void Redraw(string[] args)
        {
            if (!ToolPaths.UseDesktopExtraction()) return;
            if (args.Length < 2)
            {
                Console.WriteLine(@"usage: -feredraw <_archives\shipTop\ship_topBG3.bin> [scale=5.625] [row0 row1]");
                return;
            }
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            string rel = args[1].Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);
            float scale = args.Length >= 3 ? Single.Parse(args[2], inv) : 5.625f;
            DsImage src = DsGraphics.LoadBg(Path.Combine(Paths.FileSystem, rel));
            string name = Path.GetFileNameWithoutExtension(rel);
            if (args.Length >= 5)
            {
                int r0 = Math.Clamp(Int32.Parse(args[3]), 0, src.Height), r1 = Math.Clamp(Int32.Parse(args[4]), r0, src.Height);
                var rows = new byte[src.Width * (r1 - r0) * 4];
                Buffer.BlockCopy(src.Rgba, r0 * src.Width * 4, rows, 0, rows.Length);
                src = new DsImage { Width = src.Width, Height = r1 - r0, Rgba = rows };
                name += $"_rows{r0}-{r1}";
            }
            var sw = System.Diagnostics.Stopwatch.StartNew();
            DsImage vec = MphRecomp.Frontend.Redraw.PixelRedraw.Redraw(src, scale);
            long cold = sw.ElapsedMilliseconds;
            sw.Restart();
            MphRecomp.Frontend.Redraw.PixelRedraw.Redraw(src, scale);
            long warm = sw.ElapsedMilliseconds;
            int w = vec.Width, h = vec.Height, gap = 12;
            var px = new byte[w * (h * 2 + gap) * 4];
            for (int i = 0; i < px.Length; i += 4)
            {
                px[i] = px[i + 1] = px[i + 2] = 60;
                px[i + 3] = 255;
            }
            void Over(byte[] s, int si, int di)
            {
                int a = s[si + 3];
                px[di] = (byte)((s[si] * a + 20 * (255 - a) + 127) / 255);
                px[di + 1] = (byte)((s[si + 1] * a + 24 * (255 - a) + 127) / 255);
                px[di + 2] = (byte)((s[si + 2] * a + 34 * (255 - a) + 127) / 255);
            }
            for (int y = 0; y < h; y++)
            {
                int sy = Math.Min(src.Height - 1, (int)((y + 0.5) * src.Height / h));
                for (int x = 0; x < w; x++)
                {
                    int sx = Math.Min(src.Width - 1, (int)((x + 0.5) * src.Width / w));
                    Over(src.Rgba, (sy * src.Width + sx) * 4, (y * w + x) * 4);
                    Over(vec.Rgba, (y * w + x) * 4, ((y + h + gap) * w + x) * 4);
                }
            }
            using Image<Rgba32> sheet = Image.LoadPixelData<Rgba32>(px, w, h * 2 + gap);
            string path = Path.Combine(OutDir(), $"redraw_{name}.png");
            sheet.SaveAsPng(path);
            Console.WriteLine($"{name}: {src.Width}x{src.Height} -> {w}x{h} at scale {scale.ToString(inv)}: " +
                $"{cold} ms cold (incl. JIT), {warm} ms warm -> {path}");
        }

        // -febrief [frame,frame,...]: the new game's mission briefing (Core ShipBriefing) at 16:9 at those 60 Hz frames
        // -> extract_outrontendrief_sheet.png
        public static void Brief(string[] args)
        {
            if (!ToolPaths.UseDesktopExtraction()) return;
            var b = new ShipBriefing(Paths.FileSystem, new UiTextureCache());
            int[] at = (args.Length >= 2 ? args[1] : "90,400,900,1500,2100,2300").Split(',').Select(Int32.Parse).ToArray();
            bool ended = false;
            b.Finished = () => ended = true;
            b.Start();
            var shots = new List<Image<Rgba32>>();
            for (int f = 0; f <= at.Max() && !ended; f++)
            {
                b.Tick();
                if (at.Contains(f))
                {
                    b.Build(1280, 720);
                    shots.Add(RasterCanvas(b.DrawList, 1280, 720));
                }
            }
            Console.WriteLine(ended ? "briefing ended" : "briefing still running");
            using var sheet = new Image<Rgba32>(2 * 644, (shots.Count + 1) / 2 * 364, new Rgba32(40, 40, 40, 255));
            for (int i = 0; i < shots.Count; i++)
            {
                using Image<Rgba32> small = shots[i].Clone(c => c.Resize(640, 360));
                sheet.Mutate(c => c.DrawImage(small, new SixLabors.ImageSharp.Point(i % 2 * 644, i / 2 * 364), 1f));
                shots[i].Dispose();
            }
            sheet.SaveAsPng(Path.Combine(OutDir(), "brief_sheet.png"));
        }

        // -feplanet ["<frame>:<input>,..." [end]]: the planet select (Core ShipPlanetSelect) at 1920x1080 -> planet_flow.png
        // (a snapshot 10 frames after each input) + planet_last.png (the last one, full size). Inputs: a, shot,
        // touch@x/y, move@x/y, up@x/y (canvas pixels), hold@dx/dy (D-pad, y up; hold@0/0 releases). Env: FE_PLANETS /
        // FE_KNOWN = 5-char masks in the game's order of the planets found / already shown (defaults "01000" / "00000":
        // a new game's first visit, with the scanning box).
        public static void Planet(string[] args)
        {
            if (!ToolPaths.UseDesktopExtraction()) return;
            var sel = new ShipPlanetSelect(Paths.FileSystem, new UiTextureCache());
            var log = new List<string>();
            sel.Log = m => log.Add(m);
            sel.PlaySound = id => log.Add($"sound 0x{id:x}");
            sel.Land = p => log.Add($"LAND planet {p}");
            static int Areas(string? mask) => Enumerable.Range(0, 5).Where(i => mask != null && i < mask.Length && mask[i] == '1').Sum(i => 1 << (2 * i));
            string[] names = { "ALINOS", "CELESTIAL ARCHIVES", "VESPER DEFENSE OUTPOST", "ARCTERRA", "OUBLIETTE" };
            sel.KnownAreasChanged = k => log.Add($"known areas 0x{k:x}");
            sel.Open(Areas(Environment.GetEnvironmentVariable("FE_PLANETS") ?? "01000"), Areas(Environment.GetEnvironmentVariable("FE_KNOWN") ?? "00000"), names, 1);
            const int W = 1920, H = 1080;
            for (int i = 0; i < 300 && !sel.RedrawReady; i++)
            {
                sel.Build(W, H);
                System.Threading.Thread.Sleep(20);
            }
            string script = args.Length >= 2 ? args[1] : "40:shot,70:shot,120:shot,220:shot,300:shot,330:shot,380:shot,420:a,440:shot";
            var steps = script.Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(t => t.Split(':')).Select(p => (Frame: Int32.Parse(p[0]), Input: p[1])).ToList();
            int end = args.Length >= 3 ? Int32.Parse(args[2]) : steps.Max(t => t.Frame) + 60;
            var shots = new List<Image<Rgba32>>();
            for (int f = 0; f <= end; f++)
            {
                foreach (var st in steps.Where(t => t.Frame == f))
                {
                    log.Add($"{f}: {st.Input}");
                    string input = st.Input.ToLowerInvariant();
                    if (input == "a") sel.Press(MenuKeys.A);
                    else if (input.Contains('@'))
                    {
                        var xy = input[(input.IndexOf('@') + 1)..].Split('/');
                        float tx = Single.Parse(xy[0]), ty = Single.Parse(xy[1]);
                        sel.Build(W, H);
                        if (input.StartsWith("touch@")) sel.Touch(tx, ty);
                        else if (input.StartsWith("move@")) sel.TouchMove(tx, ty);
                        else if (input.StartsWith("up@")) sel.TouchUp(tx, ty);
                        else if (input.StartsWith("hold@")) sel.Hold((int)tx, (int)ty);
                    }
                }
                sel.Tick();
                if (steps.Any(t => t.Frame + 10 == f))
                {
                    sel.Build(W, H);
                    shots.Add(RasterCanvas(sel.DrawList, W, H));
                    log.Add($"{f}: shot {shots.Count} ({sel.State}, crosshair {sel.CrosshairX},{sel.CrosshairY}, camera {sel.CameraX:0.#},{sel.CameraY:0.#}, selected {sel.Selected}, land {sel.LandReady})");
                }
            }
            if (shots.Count > 0)
            {
                const int cols = 3, tw = 640, th = 360;
                using var sheet = new Image<Rgba32>(cols * (tw + 4), (shots.Count + cols - 1) / cols * (th + 4), new Rgba32(40, 40, 40, 255));
                for (int i = 0; i < shots.Count; i++)
                {
                    using Image<Rgba32> small = shots[i].Clone(c => c.Resize(tw, th));
                    sheet.Mutate(c => c.DrawImage(small, new SixLabors.ImageSharp.Point(i % cols * (tw + 4), i / cols * (th + 4)), 1f));
                }
                sheet.SaveAsPng(Path.Combine(OutDir(), "planet_flow.png"));
                shots[^1].SaveAsPng(Path.Combine(OutDir(), "planet_last.png"));
                foreach (var s in shots) s.Dispose();
            }
            foreach (string line in log) Console.WriteLine(line);
            Console.WriteLine($"-> {OutDir()} planet_flow.png ({shots.Count} snapshots), planet_last.png");
        }

        public static Image<Rgba32> RasterCanvas(UiDrawList list, int w, int h)
        {
            var img = new Image<Rgba32>(w, h, new Rgba32(0, 0, 0, 255));
            foreach (UiBatch batch in list.Batches)
            {
                UiTexture? tex = batch.TextureId >= 0 ? list.Textures[batch.TextureId] : null;
                for (int i = batch.Start; i < batch.Start + batch.Count; i += 3)
                {
                    FillTri(img, list.Vertices[i], list.Vertices[i + 1], list.Vertices[i + 2], tex, batch.WrapS, batch.WrapT,
                        linear: tex != null && tex.Key.StartsWith("bg/"), batch.Blend == UiBlend.Additive, batch.Clip);
                }
            }
            return img;
        }

        private static void FillTri(Image<Rgba32> img, UiVertex a, UiVertex b, UiVertex c, UiTexture? tex, UiWrap wrapS, UiWrap wrapT, bool linear,
            bool additive = false, UiRect clip = default)
        {
            int w = img.Width, h = img.Height;
            UiVertex[] v = { a, b, c };
            float area = (b.X - a.X) * (c.Y - a.Y) - (c.X - a.X) * (b.Y - a.Y);
            if (MathF.Abs(area) < 1e-6f) return;
            int x0 = Math.Max(0, (int)MathF.Floor(MathF.Min(a.X, MathF.Min(b.X, c.X))));
            int x1 = Math.Min(w - 1, (int)MathF.Ceiling(MathF.Max(a.X, MathF.Max(b.X, c.X))));
            int y0 = Math.Max(0, (int)MathF.Floor(MathF.Min(a.Y, MathF.Min(b.Y, c.Y))));
            int y1 = Math.Min(h - 1, (int)MathF.Ceiling(MathF.Max(a.Y, MathF.Max(b.Y, c.Y))));
            for (int y = y0; y <= y1; y++)
            {
                for (int x = x0; x <= x1; x++)
                {
                    float sx = x + 0.5f, sy = y + 0.5f;
                    float w0 = ((b.X - sx) * (c.Y - sy) - (c.X - sx) * (b.Y - sy)) / area;
                    float w1 = ((c.X - sx) * (a.Y - sy) - (a.X - sx) * (c.Y - sy)) / area;
                    float w2 = 1 - w0 - w1;
                    if (w0 < -1e-4f || w1 < -1e-4f || w2 < -1e-4f) continue;
                    float r = w0 * a.R + w1 * b.R + w2 * c.R;
                    float g = w0 * a.G + w1 * b.G + w2 * c.G;
                    float bl = w0 * a.B + w1 * b.B + w2 * c.B;
                    float al = w0 * a.A + w1 * b.A + w2 * c.A;
                    if (tex != null)
                    {
                        float u = w0 * a.U + w1 * b.U + w2 * c.U;
                        float vv = w0 * a.V + w1 * b.V + w2 * c.V;
                        int tx = Wrap((int)MathF.Floor(u * tex.Width), tex.Width, wrapS);
                        int ty = Wrap((int)MathF.Floor(vv * tex.Height), tex.Height, wrapT);
                        int o = (ty * tex.Width + tx) * 4;
                        r *= tex.Rgba[o] / 255f;
                        g *= tex.Rgba[o + 1] / 255f;
                        bl *= tex.Rgba[o + 2] / 255f;
                        al *= tex.Rgba[o + 3] / 255f;
                    }
                    if (al <= 0) continue;
                    if (!clip.IsNone && (sx < clip.X || sx >= clip.X + clip.W || sy < clip.Y || sy >= clip.Y + clip.H)) continue;
                    al = Math.Min(1, al);
                    Rgba32 d = img[x, y];
                    if (additive)
                    {
                        img[x, y] = new Rgba32(Math.Min(1, d.R / 255f + Math.Clamp(r, 0, 1) * al), Math.Min(1, d.G / 255f + Math.Clamp(g, 0, 1) * al),
                            Math.Min(1, d.B / 255f + Math.Clamp(bl, 0, 1) * al), 1f);
                        continue;
                    }
                    img[x, y] = new Rgba32(
                        Math.Clamp(r, 0, 1) * al + d.R / 255f * (1 - al),
                        Math.Clamp(g, 0, 1) * al + d.G / 255f * (1 - al),
                        Math.Clamp(bl, 0, 1) * al + d.B / 255f * (1 - al), 1f);
                }
            }
        }

        // Software rasteriser for menu-space triangles: DS screens stacked (top screen above the touch screen),
        // nearest-neighbour texturing with the DS wrap modes, alpha blended over black.
        public static Image<Rgba32> Raster(List<WidgetTri> tris, UiTextureCache textures, float scale)
        {
            int w = (int)(256 * scale), h = (int)(384 * scale);
            var img = new Image<Rgba32>(w, h, new Rgba32(0, 0, 0, 255));
            // a thin line between the screens
            for (int x = 0; x < w; x++) img[x, (int)(192 * scale)] = new Rgba32(40, 40, 60, 255);
            foreach (WidgetTri t in tris)
            {
                UiTexture? tex = t.TextureId >= 0 ? textures[t.TextureId] : null;
                UiVertex[] v = { t.A, t.B, t.C };
                float[] px = new float[3], py = new float[3];
                for (int i = 0; i < 3; i++)
                {
                    px[i] = v[i].X * scale;
                    py[i] = (192 - v[i].Y) * scale;
                }
                float area = (px[1] - px[0]) * (py[2] - py[0]) - (px[2] - px[0]) * (py[1] - py[0]);
                if (MathF.Abs(area) < 1e-6f) continue;
                int x0 = Math.Max(0, (int)MathF.Floor(px.Min())), x1 = Math.Min(w - 1, (int)MathF.Ceiling(px.Max()));
                int y0 = Math.Max(0, (int)MathF.Floor(py.Min())), y1 = Math.Min(h - 1, (int)MathF.Ceiling(py.Max()));
                for (int y = y0; y <= y1; y++)
                {
                    for (int x = x0; x <= x1; x++)
                    {
                        float sx = x + 0.5f, sy = y + 0.5f;
                        float w0 = ((px[1] - sx) * (py[2] - sy) - (px[2] - sx) * (py[1] - sy)) / area;
                        float w1 = ((px[2] - sx) * (py[0] - sy) - (px[0] - sx) * (py[2] - sy)) / area;
                        float w2 = 1 - w0 - w1;
                        if (w0 < -1e-4f || w1 < -1e-4f || w2 < -1e-4f) continue;
                        float r = w0 * v[0].R + w1 * v[1].R + w2 * v[2].R;
                        float g = w0 * v[0].G + w1 * v[1].G + w2 * v[2].G;
                        float b = w0 * v[0].B + w1 * v[1].B + w2 * v[2].B;
                        float a = w0 * v[0].A + w1 * v[1].A + w2 * v[2].A;
                        if (tex != null)
                        {
                            float u = w0 * v[0].U + w1 * v[1].U + w2 * v[2].U;
                            float vv = w0 * v[0].V + w1 * v[1].V + w2 * v[2].V;
                            int tx = Wrap((int)MathF.Floor(u * tex.Width), tex.Width, t.WrapS);
                            int ty = Wrap((int)MathF.Floor(vv * tex.Height), tex.Height, t.WrapT);
                            int o = (ty * tex.Width + tx) * 4;
                            r *= tex.Rgba[o] / 255f;
                            g *= tex.Rgba[o + 1] / 255f;
                            b *= tex.Rgba[o + 2] / 255f;
                            a *= tex.Rgba[o + 3] / 255f;
                        }
                        if (a <= 0) continue;
                        a = Math.Min(1, a);
                        Rgba32 d = img[x, y];
                        img[x, y] = new Rgba32(
                            Math.Clamp(r, 0, 1) * a + d.R / 255f * (1 - a),
                            Math.Clamp(g, 0, 1) * a + d.G / 255f * (1 - a),
                            Math.Clamp(b, 0, 1) * a + d.B / 255f * (1 - a), 1f);
                    }
                }
            }
            return img;
        }

        private static int Wrap(int i, int n, UiWrap mode)
        {
            switch (mode)
            {
            case UiWrap.Repeat:
                return ((i % n) + n) % n;
            case UiWrap.Mirror:
                int m = ((i % (2 * n)) + 2 * n) % (2 * n);
                return m < n ? m : 2 * n - 1 - m;
            default:
                return Math.Clamp(i, 0, n - 1);
            }
        }
    }
}
