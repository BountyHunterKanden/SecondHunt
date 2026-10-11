using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MphRecomp.Frontend;
using MphRecomp.Online;

namespace MphRead
{
    // -footertaptest: taps swept across the bottom strip of OPTIONS (the privacy / code of conduct / 13+ line, lower right)
    // and ONLINE PLAY's hub (the back arrow and the dim age line above it), at several screen shapes, in canvas pixels as a
    // finger lands (FrontendSession.TouchCanvas). Prints each row's runs of "rules" (the rules request) and "back" (a page
    // change), then checks: every point on the rules line opens the rules; nothing on the hub's age line goes back.
    internal static class FooterTapTool
    {
        public static void Test(string[] args)
        {
            if (!ToolPaths.UseDesktopExtraction()) return;
            RecompMenus.OnlineNoticeSkipped = true;
            string dir = Path.Combine(Path.GetTempPath(), "footer-tap-test");
            Directory.CreateDirectory(dir);
            if (args.Length >= 2 && args[1] == "sweep")
            {
                Sweep(dir);
                return;
            }
            var fails = new List<string>();
            foreach ((int w, int h) in new[] { (1920, 1080), (2400, 1080), (1440, 1080), (1280, 720) })
            {
                Options(dir, w, h, fails);
                Hub(dir, w, h, fails);
            }
            Console.WriteLine(fails.Count == 0 ? "PASS footertaptest" : "FAIL footertaptest:\n  " + String.Join("\n  ", fails));
        }

        private static FrontendSession Load(string dir, out List<FrontendRequest> requests)
        {
            var reqs = new List<FrontendRequest>();
            FrontendSession s = FrontendSession.Load(Paths.FileSystem, settingsPath: Path.Combine(dir, "recomp_settings.json"));
            s.Request = r => reqs.Add(r);
            requests = reqs;
            return s;
        }

        private static void Settle(FrontendSession s, int page)
        {
            if (s.Menu.Page?.Index != page || s.Menu.PendingPage != -1) s.Menu.GoTo(page);
            for (int f = 0; f < 600 && (s.Menu.Page?.Index != page || s.Menu.PendingPage != -1); f++) s.Tick();
            for (int f = 0; f < 30; f++) s.Tick();
        }

        // one tap: "rules", "back" (left the page) or "" (nothing)
        private static string Tap(FrontendSession s, List<FrontendRequest> requests, int page, int w, int h, float x, float y)
        {
            requests.Clear();
            s.Build(w, h);
            s.TouchCanvas(x, y);
            bool rules = requests.Any(r => r.Kind == FrontendRequestKind.OnlineRules);
            bool left = s.Menu.PendingPage != -1 && s.Menu.PendingPage != page || s.Menu.Page?.Index != page;
            if (left) Settle(s, page);
            return rules ? "rules" : left ? "back" : "";
        }

        private static string Runs(Func<float, string> at, float x0, float x1, float step, out List<(float A, float B, string What)> runs)
        {
            runs = new();
            for (float x = x0; x <= x1; x += step)
            {
                string what = at(x);
                if (what == "") continue;
                if (runs.Count > 0 && runs[^1].What == what && runs[^1].B == x - step) runs[^1] = (runs[^1].A, x, what);
                else runs.Add((x, x, what));
            }
            return runs.Count == 0 ? "-" : String.Join(", ", runs.Select(r => $"{r.What} {r.A:0}..{r.B:0}"));
        }

        // a menu-space rectangle (y up) in canvas pixels, placed by the region its centre falls in (as the layout places
        // a whole item)
        // (`at`: place it as an item centred there, for a line drawn as part of a bigger item)
        private static (float X0, float Y0, float X1, float Y1) Canvas(FrontendSession s, int w, int h, float x0, float y0, float x1, float y1,
            (float X, float Y)? at = null)
        {
            MenuLayout layout = MenuLayout.For(s.Menu.File, s.Menu.Page!);
            Placement[] places = layout.Resolve(w, h);
            (float cx, float cy) = at ?? ((x0 + x1) / 2, (y0 + y1) / 2);
            int region = layout.RegionOf(cx, cy);
            (float ax, float ay) = places[region].ToCanvas(x0, y1);
            (float bx, float by) = places[region].ToCanvas(x1, y0);
            return (ax, ay, bx, by);
        }

        // an item's menu-space bounds (its collected triangles) in canvas pixels
        private static (float X0, float Y0, float X1, float Y1)? ItemCanvas(FrontendSession s, int w, int h, Func<int, bool> item)
        {
            var tris = new List<WidgetTri>();
            s.Menu.Collect(tris);
            var v = tris.Where(t => item(t.Item)).SelectMany(t => new[] { t.A, t.B, t.C }).ToList();
            if (v.Count == 0) return null;
            return Canvas(s, w, h, v.Min(p => p.X), v.Min(p => p.Y), v.Max(p => p.X), v.Max(p => p.Y));
        }

        // tap along a line's top, middle and bottom rows; every tap must give `want`
        private static void Along(FrontendSession s, List<FrontendRequest> requests, int page, int w, int h,
            (float X0, float Y0, float X1, float Y1) r, string want, string what, List<string> fails, bool bottom = true)
        {
            float step = MathF.Max(2, w / 640f);
            foreach (float y in bottom ? new[] { r.Y0 + 1, (r.Y0 + r.Y1) / 2, r.Y1 - 1 } : new[] { r.Y0 + 1, (r.Y0 + r.Y1) / 2 })
            {
                for (float x = r.X0 + 1; x <= r.X1 - 1; x += step)
                {
                    string got = Tap(s, requests, page, w, h, x, y);
                    if (got != want)
                    {
                        fails.Add($"{w}x{h} {what} at ({x:0}, {y:0}) -> '{got}', not '{want}'");
                        return;
                    }
                }
            }
        }

        private static void Options(string dir, int w, int h, List<string> fails)
        {
            FrontendSession s = Load(dir, out var requests);
            s.Start(FrontendSession.OptionsPage);
            Settle(s, FrontendSession.OptionsPage);
            s.Build(w, h);
            // the line as RecompMenus.OnlineRules.cs draws it: right end x 246, top y -183, glyphs ~9 tall at 0.45
            float width = s.Menu.Font.Measure("privacy, code of conduct, ages 13+") * 0.45f;
            var r = Canvas(s, w, h, 246 - width, -183 - 9 * 0.45f, 246, -183);
            Console.WriteLine($"  measured width {width:0.0} menu units");
            Console.WriteLine($"{w}x{h} OPTIONS rules line ~x {r.X0:0}..{r.X1:0}, y {r.Y0:0}..{r.Y1:0}");
            float step = MathF.Max(4, w / 320f);
            for (float y = h - 3; y >= h * 0.86f; y -= h / 72f)
                Console.WriteLine($"  y {y:0}: {Runs(x => Tap(s, requests, FrontendSession.OptionsPage, w, h, x, y), 0, w - 1, step, out _)}");
            Along(s, requests, FrontendSession.OptionsPage, w, h, r, "rules", "options: a tap on the rules line", fails);
            if (r.X0 < w / 2f || r.X1 > w) fails.Add($"{w}x{h} options: the rules line isn't in the lower right ({r.X0:0}..{r.X1:0})");
            var arrow = ItemCanvas(s, w, h, i => s.Menu.ItemModelPath(i).Contains("backicon", StringComparison.OrdinalIgnoreCase));
            if (arrow is { } ar && Tap(s, requests, FrontendSession.OptionsPage, w, h, (ar.X0 + ar.X1) / 2, (ar.Y0 + ar.Y1) / 2) != "back")
                fails.Add($"{w}x{h} options: a tap on the back arrow doesn't go back");
        }

        // OnlineMenus' mock pixels (1920x1080 mockups) in menu space (OnlineMenus.Mx/My)
        private static float Mx(float px) => (px - 72) / 4.933f - 52;
        private static float My(float py) => -24.6f - (py - 197) / 4.933f;

        private static void Hub(string dir, int w, int h, List<string> fails)
        {
            FrontendSession s = Load(dir, out var requests);
            PlayerCard me = new() { Code = "000000000815", Name = "SAMUS", Tag = 815, Presence = Presence.Online };
            s.Recomp.OnlinePreview(me, new List<GameCard>(), new List<PlayerCard>(), 214, 41);
            (int hub, _, _) = RecompMenus.OnlinePages;
            if (hub < 0)
            {
                fails.Add("no online pages on this ROM");
                return;
            }
            s.Recomp.OpenOnline(s.Menu);
            Settle(s, hub);
            s.Build(w, h);
            // the age line (OnlineMenus.DrawHub: mock x 96..665, y 908..931 on the 1920x1080 mock)
            var age = Canvas(s, w, h, Mx(96), My(931), Mx(665), My(908), at: (Mx(670), My(545))); // (tagged with the panel)
            Console.WriteLine($"{w}x{h} HUB age line ~x {age.X0:0}..{age.X1:0}, y {age.Y0:0}..{age.Y1:0}");
            float step = MathF.Max(4, w / 320f);
            for (float y = h * 0.82f; y <= h - 3; y += h / 108f)
                Console.WriteLine($"  y {y:0}: {Runs(x => Tap(s, requests, hub, w, h, x, y), 0, w * 0.45f, step, out _)}");
            // (top and middle: the back arrow's own vanilla box reaches the bottom few pixels of the line's first word)
            Along(s, requests, hub, w, h, age, "", "hub: a tap on the age line", fails, bottom: false);
            var arrow = ItemCanvas(s, w, h, i => s.Menu.ItemModelPath(i).Contains("backicon", StringComparison.OrdinalIgnoreCase));
            if (arrow is not { } ar) fails.Add($"{w}x{h} hub: no back arrow drawn");
            else
            {
                Console.WriteLine($"  back arrow x {ar.X0:0}..{ar.X1:0}, y {ar.Y0:0}..{ar.Y1:0}");
                Along(s, requests, hub, w, h, ar, "back", "hub: a tap on the back arrow", fails);
            }
        }

        // -footertaptest sweep: every page's layout at several screen shapes, a grid of taps; for each tap the region it
        // lands in (as FrontendSession.TouchCanvas picks it) and every item touch rectangle it falls in. Lists the hits the
        // region check (FrontendSession.TouchInRegion) turns down: the taps whose outcome changed. Geometry only, no input.
        private static void Sweep(string dir)
        {
            FrontendSession s = Load(dir, out _);
            MenuFile file = s.Menu.File;
            int changed = 0;
            foreach ((int w, int h) in new[] { (1920, 1080), (2400, 1080), (1440, 1080) })
            {
                for (int page = 2; page < file.Pages.Count; page++)
                {
                    MenuPage pg = file.Pages[page];
                    MenuLayout layout = MenuLayout.For(file, pg);
                    Placement[] places = layout.Resolve(w, h);
                    var turned = new Dictionary<string, int>();
                    // the top-most item (Touch goes from the last item down) whose rectangle holds the point; `region` >= 0:
                    // only rectangles the region check lets that region take
                    int Hit(float mx, float my, int region)
                    {
                        float ty = my + 192;
                        for (int it = pg.Items.Count - 1; it >= 0; it--)
                        {
                            foreach (MenuAction act in pg.Items[it].Actions)
                            {
                                if (!MenuEngine.TryRect(act, pg.Items[it], out float x0, out float y0, out float x1, out float y1)) continue;
                                if (mx < x0 || mx > x1 || ty < y0 || ty > y1) continue;
                                if (region < 0) return it;
                                float cy0 = y0 - 192, cy1 = y1 - 192;
                                int home = layout.RegionOf((x0 + x1) / 2, (cy0 + cy1) / 2);
                                if (home < 0 || home == region) return it;
                                LayoutRegion r = layout.Regions[home];
                                if (!(x0 >= r.X0 - 4 && x1 <= r.X1 + 4 && cy0 >= r.Y0 - 4 && cy1 <= r.Y1 + 4)) return it;
                            }
                        }
                        return -1;
                    }
                    if (layout.Kind == LayoutKind.Overlay) continue;
                    for (int y = 4; y < h; y += 8)
                    {
                        for (int x = 4; x < w; x += 8)
                        {
                            int before = -1, after = -1;
                            bool firstRegion = true;
                            for (int i = 0; i < places.Length; i++)
                            {
                                (float mx, float my) = places[i].ToMenu(x, y);
                                if (!layout.Regions[i].Contains(mx, my)) continue;
                                if (firstRegion) before = Hit(mx, my, -1);
                                firstRegion = false;
                                after = Hit(mx, my, i);
                                if (after >= 0) break;
                            }
                            if (before == after) continue;
                            string Name(int it) => it < 0 ? "nothing" : $"item {it} ({System.IO.Path.GetFileName(ItemName(s, pg, it))})";
                            string key = $"{Name(before)} -> {Name(after)}";
                            turned[key] = turned.TryGetValue(key, out int n) ? n + 1 : 1;
                        }
                    }
                    foreach ((string key, int n) in turned)
                    {
                        changed++;
                        Console.WriteLine($"{w}x{h} page {page} ({layout.Regions.Count} regions): {key}: {n} grid taps");
                    }
                }
            }
            Console.WriteLine($"sweep: {changed} (page, before -> after) cases changed");
        }

        private static string ItemName(FrontendSession s, MenuPage page, int item)
        {
            foreach (MenuItemState st in page.Items[item].States)
            {
                if (st.WidgetIndex < s.Menu.File.Widgets.Count) return s.Menu.File.Widgets[st.WidgetIndex].ModelPath;
            }
            return page.Items[item].IsText ? "text" : "-";
        }
    }
}
