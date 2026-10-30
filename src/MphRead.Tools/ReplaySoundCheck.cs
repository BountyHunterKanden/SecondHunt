using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MphRecomp.Frontend;

namespace MphRead
{
    // -replaysound: walks the REPLAYS menu on a scratch copy of the sample replays and prints every UI sound the engine
    // asks for (frame, page, id, script or sample), so the borrowed ROM item sounds (RecompMenus.Replays ApplyReplaySounds)
    // can be checked against what the ROM menus play (Thread C, C2, 2026-10-23).
    internal static class ReplaySoundCheck
    {
        public static void Run(string[] args)
        {
            if (!ToolPaths.UseDesktopExtraction()) return;
            // -replaysound [replays folder]: the repo's sample replays unless given (a rev 1 test root has none of its own)
            string src = args.Length > 1 && args[1] != "rom" ? Path.GetFullPath(args[1])
                : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "testdata", "replays"));
            string dir = Path.Combine(Path.GetTempPath(), $"replay_soundcheck_{Environment.ProcessId}");
            foreach (string f in Directory.GetFiles(src, "*", SearchOption.AllDirectories))
            {
                string to = Path.Combine(dir, Path.GetRelativePath(src, f));
                Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                File.Copy(f, to);
            }
            FrontendSession s = FrontendSession.Load(Paths.FileSystem, settingsPath: Path.Combine(dir, "recomp_settings.json"));
            // the ROM's own item sounds, before any recomp page re-assigns them: vanilla's adventure and Wi-Fi pages use
            // ADVENTURE_MODE_SCR themselves, so only an item changed to it is borrowing
            var romSounds = new System.Collections.Generic.Dictionary<(int, int), int>();
            for (int p = 0; p < s.Menu.File.Pages.Count; p++)
            {
                foreach (var it in s.Menu.File.Pages[p].Items) romSounds[(p, it.Index)] = s.Menu.Sounds.Item(p, it.Index).Sound;
            }
            string step = "";
            s.PlaySound = id =>
            {
                string kind = (id & 0x4000) != 0 ? "SCRIPT (stops the UI scripts)" : "sample";
                Console.WriteLine($"  [{step}] f{s.Frame} page {s.Menu.Page?.Index} -> 0x{id:X} {kind}");
            };
            s.Request = r => Console.WriteLine($"  request {r.Arg}");
            void Run(int frames)
            {
                for (int i = 0; i < frames; i++)
                {
                    s.Build(640, 360);
                    s.Tick();
                }
            }
            void Press(string what, MenuKeys k, int frames = 40)
            {
                step = what;
                Console.WriteLine($"{what} (page {s.Menu.Page?.Index})");
                s.Press(k);
                Run(frames);
            }
            // every item with an input action and the sound its action plays (0 = silent). A fault: any item on the main menu's
            // ADVENTURE_MODE_SCR (0x4017, the big mode doors; it ends like TOUCH_BACK_SCR, so it sounds like "back"), or more than
            // one back-sound (0x401C) item on a page (owner 2026-10-24: "Back sound always plays now")
            int faults = 0;
            void Items(string what)
            {
                var page = s.Menu.Page;
                if (page == null) return;
                Console.WriteLine($"items of {what} (page {page.Index}):");
                int backs = 0;
                foreach (var it in page.Items)
                {
                    if (it.Actions.Count == 0) continue;
                    var (sound, flags) = s.Menu.Sounds.Item(page.Index, it.Index);
                    string bad = "";
                    if (sound == 0x4017) { bad = "  <-- FAULT: ADVENTURE_MODE_SCR sounds like back"; faults++; }
                    if (sound == 0x401C) backs++;
                    Console.WriteLine($"  item {it.Index}: action sound 0x{sound:X} {flags}{bad}");
                }
                if (backs > 1)
                {
                    Console.WriteLine($"  <-- FAULT: {backs} items play the back sound");
                    faults++;
                }
            }
            if (args.Length > 1 && args[1] == "rom")
            {
                // every ROM item with a sound: page, item, widget, where its A action goes, sound and flags -- what vanilla
                // plays for each kind of button, to pick the replay buttons' equivalents from
                var file = s.Menu.File;
                // each page's music (id 0 = keep what plays; fade in 30 Hz frames) and, for the way into a game (15 loading, 17
                // fade; file select's BEGIN GAME), its widgets: what vanilla draws and plays after START (C1)
                for (int p = 0; p < file.Pages.Count; p++)
                {
                    var (mid, fade, mflags) = s.Menu.Sounds.PageMusic(p);
                    Console.WriteLine($"p{p} music {mid} fade {fade} flags {mflags}");
                }
                foreach (int p in new[] { 14, 15, 16, 17 })
                {
                    if (p >= file.Pages.Count) continue;
                    foreach (var it in file.Pages[p].Items)
                    {
                        string ws = string.Join(",", it.States.Where(st => st.WidgetIndex >= 0).Select(st => Path.GetFileNameWithoutExtension(
                            file.Widgets[st.WidgetIndex].ModelPath.Replace('\\', '/'))).Distinct());
                        string to = string.Join("/", it.Actions.Select(a => $"k{a.Kind:X}>{(a.TargetPage == 0xFF ? "-" : a.TargetPage.ToString())}{(a.Calls.Count > 0 ? "c" + string.Join("+", a.Calls) : "")}"));
                        Console.WriteLine($"  page {p} item {it.Index}: {ws} [{to}]");
                    }
                }
                for (int p = 0; p < file.Pages.Count; p++)
                {
                    foreach (var it in file.Pages[p].Items)
                    {
                        var (sound, flags) = s.Menu.Sounds.Item(p, it.Index);
                        if (sound == 0) continue;
                        string widget = it.States.Where(st => st.WidgetIndex >= 0).Select(st => Path.GetFileNameWithoutExtension(
                            file.Widgets[st.WidgetIndex].ModelPath.Replace('\\', '/'))).FirstOrDefault() ?? "-";
                        string to = string.Join("/", it.Actions.Select(a => $"k{a.Kind:X}>{(a.TargetPage == 0xFF ? "-" : a.TargetPage.ToString())}{(a.Calls.Count > 0 ? "c" : "")}"));
                        string name = (sound & 0x4000) != 0 ? $"script {sound & 0x3FFF}" : ((SfxId)sound).ToString();
                        Console.WriteLine($"p{p} i{it.Index} {widget} [{to}] 0x{sound:X} {name} {flags}");
                    }
                }
                return;
            }
            // what each menu script plays (sample, delay in 30 Hz frames): a script that ends in the back sound sounds like
            // "back" on every press
            var scripts = MphRead.Formats.Sound.SoundRead.ReadSfxScriptFiles();
            foreach (int id in new[] { 23, 24, 25, 26, 27, 28, 29 })
            {
                var f = scripts[id];
                Console.WriteLine($"script 0x{id | 0x4000:X} {f.Name}: " + string.Join(", ", f.Entries.Select(e =>
                    $"{((e.SfxData & 0x8000) != 0 ? "stop " : "")}{(SfxId)(e.SfxData & 0x3FFF)}({e.SfxData & 0x3FFF}) @{e.Delay * 30:0}")));
            }
            s.Start(s.Recomp.ReplaysPage);
            step = "open";
            Run(60);
            Items("the list");
            Press("A on the first row (details)", MenuKeys.A);
            Items("the details");
            // the D-pad as the shells send it (FrontendSession.Navigate, not a key press): each move must make the focus sound
            void Nav(string what, int dx, int dy)
            {
                step = what;
                int before = s.Menu.FocusedItem();
                Console.WriteLine($"{what} (page {s.Menu.Page?.Index}, focused item {before})");
                bool heard = false;
                var play = s.PlaySound;
                s.PlaySound = id => { heard = true; play!(id); };
                s.Navigate(dx, dy);
                Run(10);
                s.PlaySound = play;
                int after = s.Menu.FocusedItem();
                if (after != before && !heard)
                {
                    Console.WriteLine($"  <-- FAULT: focus moved {before} -> {after} with no sound");
                    faults++;
                }
            }
            Nav("Down", 0, -1);
            Nav("Down", 0, -1);
            Nav("Right", 1, 0);
            Nav("Up", 0, 1);
            Press("B (back to the list)", MenuKeys.B);
            Press("Down (list)", MenuKeys.Down, 10);
            Press("A (details of the next)", MenuKeys.A);
            Press("B", MenuKeys.B);
            Press("B (leave REPLAYS)", MenuKeys.B);
            // every other recomp page that borrows ROM sounds (the online menus, IMPORT GAMES' cards and game page): applied as
            // their pages do on entry, then no item the ROM doesn't give ADVENTURE_MODE_SCR may play it
            foreach (string apply in new[] { "ApplyOnlineSounds", "ApplyCardSounds", "ApplyGameSounds" })
            {
                var m = typeof(RecompMenus).GetMethod(apply, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                if (m == null) { Console.WriteLine($"  <-- FAULT: {apply} not found"); faults++; continue; }
                m.Invoke(s.Recomp, new object[] { s.Menu.Sounds });
            }
            const int mainMenuPage = 18;
            int borrowed = 0;
            for (int p = 0; p < s.Menu.File.Pages.Count; p++)
            {
                if (p == mainMenuPage) continue;
                foreach (var it in s.Menu.File.Pages[p].Items)
                {
                    if (s.Menu.Sounds.Item(p, it.Index).Sound != 0x4017 || romSounds.GetValueOrDefault((p, it.Index)) == 0x4017) continue;
                    Console.WriteLine($"  <-- FAULT: page {p} item {it.Index} plays ADVENTURE_MODE_SCR (sounds like back)");
                    faults++;
                    borrowed++;
                }
            }
            Console.WriteLine($"other pages: {borrowed} item(s) borrowing the main menu's adventure sound");
            // two sessions in one process (the front-end and the pause menu): each must keep its own online page numbers
            {
                FrontendSession s2 = FrontendSession.Load(Paths.FileSystem, settingsPath: Path.Combine(dir, "recomp_settings.json"), savesDir: Path.Combine(dir, "saves"));
                Console.WriteLine($"two sessions: pages {s.Menu.File.Pages.Count} / {s2.Menu.File.Pages.Count}");
                RecompMenus.OnlinePageSnapshotCheck(s.Recomp, s2.Recomp, ref faults);
            }
            Directory.Delete(dir, recursive: true);
            Console.WriteLine(faults == 0 ? "PASS: every replay button has its vanilla twin's sound" : $"FAIL: {faults} sound fault(s)");
            if (faults > 0) Environment.ExitCode = 1;
        }
    }
}
