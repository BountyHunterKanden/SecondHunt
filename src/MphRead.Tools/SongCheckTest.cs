using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using MphRecomp.Frontend;

namespace MphRead
{
    // -songchecktest: SONG LIST's checkboxes (owner 2026-10-29: "The "all" checkmarks don't show and something happens
    // when you start selecting various ones and toggling the all that completely breaks the rest of the checkmarks").
    // Opens SONG LIST > HUNTERS and one of its areas headless (the HUNTERS songs need no import), toggles rows through
    // RecompMenus.OnCall as A presses do, and after each step checks every shown checkbox's drawn mark (tick, dash or
    // none) against its row's state, and that the hidden-songs list has no repeats.
    internal static class SongCheckTest
    {
        const BindingFlags Any = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        const int RowNext = 1100;

        public static void Run()
        {
            if (!ToolPaths.UseDesktopExtraction()) return;
            RecompMenus.OnlineNoticeSkipped = true;
            string dir = Path.Combine(Path.GetTempPath(), "song-check-test");
            if (Directory.Exists(dir)) Directory.Delete(dir, true); // (the .bak and settings_backups too: a fresh start)
            Directory.CreateDirectory(dir);
            FrontendSession s = FrontendSession.Load(Paths.FileSystem, settingsPath: Path.Combine(dir, "recomp_settings.json"));
            s.Request = _ => { };
            RecompMenus r = s.Recomp;
            Type t = typeof(RecompMenus);
            object Get(object o, string name) => o.GetType().GetField(name, Any)!.GetValue(o)!;
            void Call(string name, params object[] a) => t.GetMethod(name, Any)!.Invoke(r, a);
            t.GetField("_stepMenu", Any)!.SetValue(r, s.Menu);
            Type pickType = t.GetField("_songPick", Any)!.FieldType;
            t.GetField("_songPick", Any)!.SetValue(r, Enum.Parse(pickType, "Hide"));
            var settings = (MphRecomp.Config.RecompSettings)Get(r, "_settings");
            int fails = 0;
            void Settle(int page)
            {
                for (int f = 0; f < 600 && (s.Menu.Page?.Index != page || s.Menu.PendingPage != -1); f++) s.Tick();
                for (int f = 0; f < 10; f++) s.Tick();
            }
            // the marks drawn on each shown checkbox: 4 untextured triangles on its box = tick, 2 = dash
            void Check(string what, object rows)
            {
                var tris = new List<WidgetTri>();
                s.Menu.Collect(tris);
                int from = tris.Count;
                r.DrawOptions(s.Menu, tris);
                var list = (IList)Get(rows, "Rows");
                int offset = (int)Get(rows, "Offset");
                var box = (int[])Get(rows, "Box");
                var hidden = settings.HiddenSongs;
                if (hidden.Count != hidden.Distinct().Count())
                {
                    Console.WriteLine($"  FAIL {what}: hidden songs repeat ({hidden.Count} entries, {hidden.Distinct().Count()} distinct)");
                    fails++;
                }
                for (int i = 0; i < Math.Min(list.Count - offset, box.Length); i++)
                {
                    object m = list[offset + i]!;
                    var check = (Func<bool>?)Get(m, "Check");
                    if (check == null) continue;
                    var partly = (Func<bool>?)m.GetType().GetField("Partly", Any)!.GetValue(m);
                    int marks = tris.Skip(from).Count(x => x.Item == box[i] && x.TextureId < 0);
                    int want = check() ? 4 : partly?.Invoke() == true ? 2 : 0;
                    string label = ((string)Get(m, "Label")).Replace('\n', ' ');
                    if (Environment.GetEnvironmentVariable("SONGCHECK_DEBUG") != null)
                        Console.WriteLine($"    box {box[i]} {s.Menu.ItemState(box[i])}: {tris.Take(from).Count(x => x.Item == box[i])} tris");
                    bool ok = marks == want;
                    if (!ok) fails++;
                    Console.WriteLine($"  {(ok ? "ok  " : "FAIL")} {what}: row {i} '{label}' marks {marks}, want {want}");
                }
            }
            void Press(object rows, int row) => r.OnCall(s.Menu, RowNext + row, false);

            object gameRows = Get(r, "_songGameRows"), areaRows = Get(r, "_songAreaRows");
            Call("OpenBrowseGame", "mph");
            Settle((int)Get(gameRows, "Page"));
            Check("HUNTERS, all on", gameRows);
            string area = MphRecomp.PrimeMusic.MusicCatalog.Categories("mph")
                .First(a => MphRecomp.PrimeMusic.HuntersMusic.All.Count(x => MphRecomp.PrimeMusic.MusicCatalog.Of(x).Category == a) >= 3);
            Call("OpenBrowseArea", area);
            Settle((int)Get(areaRows, "Page"));
            Console.WriteLine($"area '{area}'");
            Check("all on", areaRows);
            Press(areaRows, 1); Check("song 1 off", areaRows);
            Press(areaRows, 2); Check("song 2 off", areaRows);
            Press(areaRows, 0); Check("all: off", areaRows);
            Press(areaRows, 0); Check("all: on", areaRows);
            Press(areaRows, 1); Check("song 1 off again", areaRows);
            Press(areaRows, 1); Check("song 1 on again", areaRows);
            Press(areaRows, 0); Check("all: off again", areaRows);
            Press(areaRows, 2); Check("song 2 on alone", areaRows);
            Press(areaRows, 0); Check("all: off from some", areaRows);
            Press(areaRows, 0); Check("all: on from none", areaRows);
            Call("OpenBrowseGame", "mph");
            Settle((int)Get(gameRows, "Page"));
            Check("HUNTERS after", gameRows);
            Press(gameRows, 0); Check("HUNTERS all: off", gameRows);
            Press(gameRows, 0); Check("HUNTERS all: on", gameRows);
            Console.WriteLine(fails == 0 ? "song check test: PASS" : $"song check test: FAIL ({fails})");
        }
    }
}
