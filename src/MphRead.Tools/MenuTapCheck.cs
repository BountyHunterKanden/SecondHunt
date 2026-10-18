using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MphRecomp.Frontend;
using MphRecomp.Online;

namespace MphRead
{
    // -menutapcheck [w h]: every vanilla touch button on every front-end page reachable where it is drawn (owner 2026-10-14:
    // "make all on screen touch buttons from vanilla also touchable on second hunt"). For each page (shown, so its items
    // are drawn) and screen shape: each drawn item with a touch rectangle is placed by the region its drawing goes to
    // (FrontendSession's item regions: the centre of its triangles), the centre of its touch rectangle is mapped to the
    // canvas through that region, and that canvas point is run through TouchCanvas's choice (each region the point is in,
    // in order, the top-most rectangle the region rule lets it take). Lists the drawn buttons a tap on them doesn't reach
    // ("-> nothing" or "-> item N": something else takes it). Geometry only, no input.
    internal static class MenuTapCheck
    {
        public static void Run(string[] args)
        {
            if (!ToolPaths.UseDesktopExtraction()) return;
            RecompMenus.OnlineNoticeSkipped = true;
            string dir = Path.Combine(Path.GetTempPath(), "menu-tap-check");
            Directory.CreateDirectory(dir);
            var shapes = args.Length >= 3 ? new[] { (Int32.Parse(args[1]), Int32.Parse(args[2])) }
                : new[] { (1920, 1080), (2400, 1080), (1440, 1080) };
            int checkedButtons = 0, missed = 0;
            foreach ((int w, int h) in shapes)
            {
                FrontendSession s = FrontendSession.Load(Paths.FileSystem, settingsPath: Path.Combine(dir, "recomp_settings.json"));
                s.Request = _ => { };
                MenuFile file = s.Menu.File;
                for (int page = 2; page < file.Pages.Count; page++)
                {
                    s.Menu.GoTo(page);
                    for (int f = 0; f < 600 && (s.Menu.Page?.Index != page || s.Menu.PendingPage != -1); f++) s.Tick();
                    for (int f = 0; f < 30; f++) s.Tick();
                    if (s.Menu.Page?.Index != page || s.Popup?.IsOpen == true) continue;
                    MenuPage pg = s.Menu.Page!;
                    MenuLayout layout = MenuLayout.For(file, pg);
                    if (layout.Kind == LayoutKind.Overlay) continue; // (boot logos, credits: any tap is a touch-screen tap)
                    s.Build(w, h);
                    Placement[] places = layout.Resolve(w, h);
                    var tris = new List<WidgetTri>();
                    s.Menu.Collect(tris);
                    // the recomp's own drawing of a page's items, as FrontendSession.Build (REPLAYS' rows and corner setting...)
                    s.Multiplayer.ReplacePreview(s.Menu, tris, 0);
                    s.Recomp.DrawReplays(s.Menu, tris);
                    s.Recomp.DrawOnline(s.Menu, tris);
                    s.Recomp.DrawOptions(s.Menu, tris);
                    s.Recomp.DrawImportCards(s.Menu, tris);
                    var drawn = new Dictionary<int, (float X0, float Y0, float X1, float Y1)>();
                    foreach (WidgetTri t in tris)
                    {
                        float x0 = MathF.Min(t.A.X, MathF.Min(t.B.X, t.C.X)), x1 = MathF.Max(t.A.X, MathF.Max(t.B.X, t.C.X));
                        float y0 = MathF.Min(t.A.Y, MathF.Min(t.B.Y, t.C.Y)), y1 = MathF.Max(t.A.Y, MathF.Max(t.B.Y, t.C.Y));
                        drawn[t.Item] = drawn.TryGetValue(t.Item, out var b)
                            ? (MathF.Min(b.X0, x0), MathF.Min(b.Y0, y0), MathF.Max(b.X1, x1), MathF.Max(b.Y1, y1)) : (x0, y0, x1, y1);
                    }

                    // TouchCanvas's choice for canvas point (cx, cy): the first region (in order) holding it with a rectangle
                    // the region rule lets it take, else the first region's top-most rectangle
                    int Choose(float cx, float cy)
                    {
                        int first = -1;
                        for (int i = 0; i < places.Length; i++)
                        {
                            (float mx, float my) = places[i].ToMenu(cx, cy);
                            if (!layout.Regions[i].Contains(mx, my)) continue;
                            int hit = Hit(mx, my, i);
                            if (hit >= 0) return hit;
                            if (first < 0) first = Hit(mx, my, -1) is int any && any >= 0 ? any : -2;
                        }
                        return first < 0 ? -1 : first;
                    }
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

                    for (int it = 0; it < pg.Items.Count; it++)
                    {
                        if (!drawn.TryGetValue(it, out var b) || b.Y1 - b.Y0 > 200) continue; // not drawn / spans screens
                        int region = layout.RegionOf((b.X0 + b.X1) / 2, (b.Y0 + b.Y1) / 2, pg.Items[it].IsText);
                        if (region < 0) continue;
                        foreach (MenuAction act in pg.Items[it].Actions)
                        {
                            if (!MenuEngine.TryRect(act, pg.Items[it], out float x0, out float y0, out float x1, out float y1)) continue;
                            // a rectangle off every screen (page 77's player_highlight at DS x 343..458): no tap reaches it
                            // on the DS either
                            if (layout.RegionOf((x0 + x1) / 2, (y0 + y1) / 2 - 192) < 0) continue;
                            checkedButtons++;
                            // a 7x7 grid over the touch rectangle where it is drawn: reached if any point takes it (the
                            // middle of a box may sit under its own arrows); else the points' takers
                            var takers = new Dictionary<int, int>();
                            bool reached = false;
                            (float CX, float CY) mid = places[region].ToCanvas((x0 + x1) / 2, (y0 + y1) / 2 - 192);
                            for (int gy = 0; gy < 7 && !reached; gy++)
                            {
                                for (int gx = 0; gx < 7 && !reached; gx++)
                                {
                                    float mx = x0 + (x1 - x0) * (gx + 0.5f) / 7, my = y0 + (y1 - y0) * (gy + 0.5f) / 7 - 192;
                                    (float cx, float cy) = places[region].ToCanvas(mx, my);
                                    if (cx < 0 || cy < 0 || cx >= w || cy >= h) continue; // off the canvas
                                    int got = Choose(cx, cy);
                                    // as vanilla would: the DS touch screen's top-most rectangle at this point, no regions
                                    int ds = Hit(mx, my, -1);
                                    reached = got == it || got == ds;
                                    takers[got] = takers.TryGetValue(got, out int n) ? n + 1 : 1;
                                }
                            }
                            if (reached) break;
                            missed++;
                            string name = Path.GetFileName(s.Menu.ItemModelPath(it));
                            string Who(int got) => got < 0 ? "nothing" : $"item {got} ({Path.GetFileName(s.Menu.ItemModelPath(got))}"
                                + $"{(pg.Items[got].IsText ? " text" : "")}, kind {pg.Items[got].Actions.FirstOrDefault()?.Kind}, page {pg.Items[got].Actions.FirstOrDefault()?.TargetPage})";
                            string others = takers.Count == 0 ? "all off the canvas" : String.Join(", ", takers.Select(t => $"{Who(t.Key)} x{t.Value}"));
                            Console.WriteLine($"{w}x{h} page {page} ({layout.Kind}): item {it} ({name}, kind {act.Kind}, page {act.TargetPage}) drawn in region {region} around ({mid.CX:0},{mid.CY:0}) -> {others}; drawn {b.X0:0},{b.Y0:0}..{b.X1:0},{b.Y1:0} rect {x0:0},{y0 - 192:0}..{x1:0},{y1 - 192:0}");
                            break;
                        }
                    }
                }
            }
            Console.WriteLine($"menutapcheck: {checkedButtons} drawn buttons checked, {missed} not reached where drawn");
        }
    }
}
