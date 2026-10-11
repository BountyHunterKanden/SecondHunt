using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MphRecomp.Frontend;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;


namespace MphRead
{
    // Replay mode: ROM menu art for the REPLAYS page mockups (owner 2026-10-08: rows from the multiplayer setup screens'
    // pieces). -replaymock [dir/name ...]: each model's textures as PNGs in extract_out/replay/mock_art
    // (<dir>_<name>_<texture>-<palette>.png). Default: the SELECT HUNTER round icons (lit + off), the arena (stage)
    // pictures, the RESULTS medals and the AVAILABLE GAMES row bar.
    internal static partial class ReplayMockTool
    {
        private static readonly string[] Hunters = { "samus", "kanden", "trace", "sylux", "noxus", "spire", "weavel" };

        public static void Run(string[] args)
        {
            if (!ToolPaths.UseDesktopExtraction()) return;
            if (args.Length >= 3 && args[1] == "list")
            {
                // -replaymock list <data dir>: the REPLAYS list's facts per replay, one JSON object a line
                foreach (MphRecomp.Replay.ReplayInfo r in MphRecomp.Replay.ReplayLibrary.List(args[2]))
                {
                    var o = new Dictionary<string, object?>
                    {
                        ["title"] = r.Title, ["pinned"] = r.Pinned, ["recorded"] = r.Recorded?.ToString("s"), ["seconds"] = r.Seconds,
                        ["mode"] = r.Mode?.ToString(), ["arena"] = r.Arena, ["complete"] = r.Complete, ["refused"] = r.Refused,
                        ["localSeat"] = r.LocalSeat, ["localPlace"] = r.LocalPlace,
                        ["players"] = r.Players.Select(p => new { hunter = p.Hunter.ToString(), p.Team, p.Bot, p.Remote, p.Nickname }).ToArray(),
                        ["places"] = r.Header?.Places.Select(p => new { p.Seat, p.Place, p.Points }).ToArray()
                    };
                    Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(o));
                }
                return;
            }
            if (args.Length >= 3 && args[1] == "places")
            {
                // -replaymock places <dir>: give a SCRATCH copy's complete replays (format < 3) made-up places, the n-th
                // file's recorder placing n mod 4 (win, 2nd, 3rd, last), so the list's medals + win / loss can be looked at
                int n = 0;
                foreach (string path in Directory.GetFiles(args[2], "*.mphreplay", SearchOption.AllDirectories).OrderBy(p => p))
                {
                    MphRecomp.Replay.ReplayFile file;
                    try { file = MphRecomp.Replay.ReplayFile.Load(path); }
                    catch (InvalidDataException) { continue; }
                    if (!file.Complete) continue;
                    int seats = file.Settings.Players.Count, mine = n++ % 4;
                    file.Places.Clear();
                    for (int seat = 0; seat < seats; seat++)
                    {
                        int place = seat == 0 ? Math.Min(mine, seats - 1) : (seat <= Math.Min(mine, seats - 1) ? seat - 1 : seat);
                        file.Places.Add(new MphRecomp.Replay.ReplayPlace(seat, place, 10 - place));
                    }
                    DateTime when = File.GetLastWriteTimeUtc(path);
                    using (FileStream fs = File.Create(path)) file.Write(fs);
                    File.SetLastWriteTimeUtc(path, when);
                    Console.WriteLine($"{Path.GetFileName(path)}: seat 0 place {file.Places[0].Place}");
                }
                return;
            }
            if (args.Length >= 3 && args[1] == "frame")
            {
                Frame(args);
                return;
            }
            if (args.Length >= 2 && args[1] == "licensecheck")
            {
                LicenseCheck(); // (ReplayMockTool.License.cs: O10, a replay's names and license)
                return;
            }
            if (args.Length >= 2 && args[1] == "renamecheck")
            {
                RenameCheck();
                return;
            }
            if (args.Length >= 2 && args[1] == "font")
            {
                Font();
                return;
            }
            var names = new List<string>();
            for (int i = 1; i < args.Length; i++) names.Add(args[i]);
            if (names.Count == 0)
            {
                foreach (string h in Hunters)
                {
                    names.Add("characterselect/" + h);
                    names.Add("characterselect/" + h + "off");
                }
                foreach (string s in new[] { "mp1", "mp2", "mp3", "mp4", "mp4_dm1", "mp5", "mp6", "mp7", "mp8", "mp9", "mp10", "mp11",
                    "mp12", "mp13", "mp14", "ctf1", "ctf1_dm1", "ad1", "ad1_dm1", "ad2", "ad2_dm1", "e3level", "unit1land", "unit2land",
                    "unit3land", "unit4land", "goreab2", "random" })
                {
                    names.Add("stage/" + s);
                }
                foreach (string m in new[] { "medalgold", "medalsilver", "medalbronze", "medallast", "medalbg", "lightning" })
                {
                    names.Add("results/" + m);
                }
                names.Add("createjoin/redbar");
            }
            string outDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "extract_out", "replay", "mock_art"));
            Directory.CreateDirectory(outDir);
            foreach (string full in names)
            {
                string[] parts = full.Split('/');
                MetaDir dir = parts[0] switch
                {
                    "characterselect" => MetaDir.CharSelect, "stage" => MetaDir.Stage, "results" => MetaDir.Results,
                    "createjoin" => MetaDir.CreateJoin, "startgame" => MetaDir.StartGame, "gameoptions" => MetaDir.GameOption,
                    "gamerscard" => MetaDir.GamersCard, _ => MetaDir.MainMenu
                };
                Model model;
                try { model = Read.GetModelInstance(parts[1], dir: dir, noCache: true).Model; }
                catch (Exception ex) { Console.WriteLine($"{full}: {ex.Message}"); continue; }
                Recolor recolor = model.Recolors[0];
                var done = new HashSet<(int, int)>();
                foreach (Material m in model.Materials)
                {
                    if (m.TextureId < 0 || !done.Add((m.TextureId, m.PaletteId))) continue;
                    Texture tex = recolor.Textures[m.TextureId];
                    IReadOnlyList<ColorRgba> px = recolor.GetPixels(m.TextureId, m.PaletteId);
                    if (tex.Width == 0 || px.Count != tex.Width * tex.Height) continue;
                    using var img = new SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgba32>(tex.Width, tex.Height);
                    for (int i = 0; i < px.Count; i++)
                    {
                        img[i % tex.Width, i / tex.Width] = new SixLabors.ImageSharp.PixelFormats.Rgba32(px[i].Red, px[i].Green, px[i].Blue, px[i].Alpha);
                    }
                    string path = Path.Combine(outDir, $"{parts[0]}_{parts[1]}_{m.TextureId}-{m.PaletteId}.png");
                    SixLabors.ImageSharp.ImageExtensions.SaveAsPng(img, path);
                    Console.WriteLine($"{full}: material {m.Name} {tex.Width}x{tex.Height} -> {path}");
                }
            }
        }
    
        private static string OutDir()
        {
            string dir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "extract_out", "replay", "mock_art"));
            Directory.CreateDirectory(dir);
            return dir;
        }

        // -replaymock frame <page> [w=1920] [h=1080] [hide=a,b,...] [frames=120]: a page through the full FrontendSession at
        // one size with those items hidden (an empty setup frame to lay mockup rows into) -> mock_art/frame_<page>.png;
        // prints every item's menu-space bounds
        private static void Frame(string[] args)
        {
            int page = args[2] == "replays" ? -1 : Int32.Parse(args[2]);
            int w = 1920, h = 1080, frames = 120;
            var hide = new HashSet<int>();
            foreach (string a in args.Skip(3))
            {
                if (a.StartsWith("w=")) w = Int32.Parse(a[2..]);
                else if (a.StartsWith("h=")) h = Int32.Parse(a[2..]);
                else if (a.StartsWith("frames=")) frames = Int32.Parse(a[7..]);
                else if (a.StartsWith("hide=")) foreach (string n in a[5..].Split(',', StringSplitOptions.RemoveEmptyEntries)) hide.Add(Int32.Parse(n));
            }
            FrontendSession session = FrontendSession.Load(Paths.FileSystem, settingsPath: Environment.GetEnvironmentVariable("FE_SETTINGS"));
            session.Log = _ => { };
            if (page < 0)
            {
                // "replays": the REPLAYS list page (its index depends on the pages installed before it)
                page = session.Recomp.ReplaysPage;
                Console.WriteLine($"REPLAYS list page = {page}");
            }
            session.Start(page);
            for (int f = 0; f < frames; f++)
            {
                if (f >= frames - 60) foreach (int i in hide) session.Menu.SetState(i, MenuState.Hidden);
                session.Tick();
            }
            var tris = new List<WidgetTri>();
            session.Menu.Collect(tris);
            foreach (IGrouping<int, WidgetTri> g in tris.GroupBy(t => t.Item).OrderBy(g => g.Key))
            {
                float x0 = g.Min(t => MathF.Min(t.A.X, MathF.Min(t.B.X, t.C.X))), x1 = g.Max(t => MathF.Max(t.A.X, MathF.Max(t.B.X, t.C.X)));
                float y0 = g.Min(t => MathF.Min(t.A.Y, MathF.Min(t.B.Y, t.C.Y))), y1 = g.Max(t => MathF.Max(t.A.Y, MathF.Max(t.B.Y, t.C.Y)));
                Console.WriteLine(FormattableString.Invariant($"item {g.Key} state {session.Menu.ItemState(g.Key)}: x {x0:0.#}..{x1:0.#} y {y0:0.#}..{y1:0.#} ({g.Count()} tris)"));
            }
            session.Build(w, h);
            using Image<Rgba32> img = FrontendTool.RasterCanvas(session.DrawList, w, h);
            string path = Path.Combine(OutDir(), $"frame_{page}.png");
            img.SaveAsPng(path);
            Console.WriteLine("-> " + path);
        }

        // -replaymock font: the menu font's atlas (mock_art/font_atlas.png) + each character's cell, advance and drop
        // (mock_art/font_metrics.json), so mockup text uses the game's own letters
        private static void Font()
        {
            FrontendSession session = FrontendSession.Load(Paths.FileSystem);
            MenuFont font = session.Menu.Font;
            UiTexture atlas = session.Textures[font.TextureId];
            using (var img = new Image<Rgba32>(atlas.Width, atlas.Height))
            {
                for (int i = 0; i < atlas.Width * atlas.Height; i++)
                {
                    img[i % atlas.Width, i / atlas.Width] = new Rgba32(atlas.Rgba[i * 4], atlas.Rgba[i * 4 + 1], atlas.Rgba[i * 4 + 2], atlas.Rgba[i * 4 + 3]);
                }
                img.SaveAsPng(Path.Combine(OutDir(), "font_atlas.png"));
            }
            var metrics = new Dictionary<string, object>();
            for (int c = MenuFont.MinCharacter; c < 127; c++)
            {
                string ch = ((char)c).ToString();
                var tris = new List<WidgetTri>();
                font.Emit(ch, 0, 192, 0, 0, 0, 1, 1, 1, 1, 0, tris);
                float adv = font.Measure(ch);
                if (tris.Count == 0)
                {
                    metrics[ch] = new { adv };
                    continue;
                }
                WidgetTri t = tris[0];
                metrics[ch] = new
                {
                    adv, cx = (int)MathF.Round(t.A.U * atlas.Width), cy = (int)MathF.Round(t.A.V * atlas.Height), drop = -t.A.Y
                };
            }
            File.WriteAllText(Path.Combine(OutDir(), "font_metrics.json"), System.Text.Json.JsonSerializer.Serialize(metrics));
            Console.WriteLine($"font atlas {atlas.Width}x{atlas.Height}, {metrics.Count} chars");
        }

        // -replaymock renamecheck: RENAME end to end on a scratch copy of the sample replays (handoffs/replay-mode_2026-10-08_0150-files/
        // fe_testdata): REPLAYS -> the newest replay -> rename -> the name keyboard (MultiplayerMenu.EditText) -> a new name -> OK:
        // back on the REPLAY page with the new title, the file renamed, WATCH asks for the new file, and the list shows it
        private static void RenameCheck()
        {
            string src = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "handoffs",
                "replay-mode_2026-10-08_0150-files", "fe_testdata"));
            string dir = Path.Combine(Path.GetTempPath(), $"replay_renamecheck_{Environment.ProcessId}");
            foreach (string f in Directory.GetFiles(src, "*", SearchOption.AllDirectories))
            {
                string to = Path.Combine(dir, Path.GetRelativePath(src, f));
                Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                File.Copy(f, to);
            }
            FrontendSession s = FrontendSession.Load(Paths.FileSystem, settingsPath: Path.Combine(dir, "recomp_settings.json"));
            var log = new List<string>();
            s.Log = log.Add;
            string? request = null;
            s.Request = r => request = r.Arg;
            void Run(int frames)
            {
                for (int i = 0; i < frames; i++)
                {
                    s.Build(640, 360);
                    s.Tick();
                }
            }
            void Tap(int call)
            {
                MenuPage page = s.Menu.Page!;
                for (int i = 0; i < page.Items.Count; i++)
                {
                    foreach (MenuAction a in page.Items[i].Actions)
                    {
                        if (!a.Calls.Any(c => c.B == call) || !MenuEngine.TryRect(a, page.Items[i], out float x0, out float y0, out float x1, out float y1)) continue;
                        s.TouchDs((x0 + x1) / 2, 192 - (y0 + y1) / 2);
                        Run(6);
                        return;
                    }
                }
                Console.WriteLine($"FAIL no key with call {call} on page {page.Index}");
            }
            List<string> Texts() => Enumerable.Range(0, s.Menu.Page!.Items.Count)
                .Where(i => s.Menu.Page.Items[i].IsText && s.Menu.ItemState(i) != MenuState.Hidden)
                .Select(i => s.Menu.Strings[s.Menu.Page.Items[i].States[0].Text!.StringId]).ToList();
            int fails = 0;
            void Check(bool ok, string what)
            {
                Console.WriteLine($"{(ok ? "PASS" : "FAIL")} {what}");
                if (!ok) fails++;
            }
            string Files() => String.Join(" | ", Directory.GetFiles(Path.Combine(dir, "replays")).Select(Path.GetFileName));
            // the list opens on its first row (the newest replay): A -> its details page
            s.Start(s.Recomp.ReplaysPage);
            Run(60);
            int list = s.Menu.Page!.Index;
            s.Press(MenuKeys.A);
            Run(40);
            int one = s.Menu.Page!.Index;
            Check(one != list && Texts().Any(t => t.Contains("COMBAT HALL", StringComparison.Ordinal)),
                $"A on the first row opens its details (page {one}: {String.Join(" / ", Texts().Take(3))})");
            Tap(2142); // RENAME
            Run(60);
            Check(s.Menu.Page?.Index == MultiplayerMenu.NamePage && Texts().Contains("Battle Combat Hall_"), // (the box shows a cursor after the text)
                $"RENAME opens the keyboard with the title (page {s.Menu.Page?.Index}: {String.Join(" / ", Texts())})");
            for (int i = 0; i < 20; i++) Tap(343);
            const string lower = "1234567890-=qwertyuiopasdfghjklzxcvbnm,./;'[]";
            foreach (char c in "my best match")
            {
                if (c == ' ') Tap(346);
                else Tap(297 + lower.IndexOf(c));
            }
            Run(10);
            Tap(6); // OK
            Run(60);
            Check(s.Menu.Page?.Index == one, $"OK: back on the details page (page {s.Menu.Page?.Index})");
            Check(Texts().Any(t => t.Contains("MY BEST MATCH", StringComparison.Ordinal)), $"its title is the new name ({String.Join(" / ", Texts().Take(4))})");
            Check(File.Exists(Path.Combine(dir, "replays", "20261008-005600 my best match.mphreplay")) &&
                !File.Exists(Path.Combine(dir, "replays", "20261008-005600 Battle Combat Hall.mphreplay")), $"the file renamed ({Files()})");
            // PLAY (focused on the details page) asks, then plays the renamed file (A focuses YES, A presses it)
            Tap(2140);
            Run(20);
            s.Press(MenuKeys.A);
            Run(10);
            s.Press(MenuKeys.A);
            Run(30);
            Check(request != null && request.EndsWith("my best match.mphreplay", StringComparison.Ordinal), $"PLAY plays the renamed file ({request ?? "no request"})");
            // back to the list: the new name first (the list's rows draw the library's titles)
            Run(30);
            s.Start(s.Recomp.ReplaysPage);
            Run(60);
            string first = MphRecomp.Replay.ReplayLibrary.List(dir).FirstOrDefault()?.Title ?? "";
            Check(first == "my best match" && s.Menu.Page?.Index == list, $"the list's first replay is the new name ({first})");
            foreach (string l in log.Where(l => l.Contains("replay", StringComparison.OrdinalIgnoreCase))) Console.WriteLine("  log: " + l);
            Directory.Delete(dir, recursive: true);
            Console.WriteLine(fails == 0 ? "PASS renamecheck" : $"FAIL renamecheck ({fails})");
        }
}
}
