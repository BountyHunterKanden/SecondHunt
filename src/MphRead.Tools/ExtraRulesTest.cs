using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using MphRecomp.Frontend;
using MphRecomp.Multiplayer;
using SixLabors.ImageSharp;

// -extrarules shot <name> "<tick>:ds@x/y,..." [end]: the front end from the main menu driven by DS taps, then the screen at
// MP_SIZE (default 1280x720) saved as extract_out/frontend/<name>.png (Extra pickups + Extra characters rules, owner 2026-10-10).
namespace MphRead
{
    public static class ExtraRulesTest
    {
        public static void Run(string[] args)
        {
            if (!ToolPaths.UseDesktopExtraction()) return;
            if (args.Length >= 2 && args[1] == "check")
            {
                Check();
            }
            else if (args.Length >= 4 && args[1] == "shot")
            {
                Shot(args[2], args[3], args.Length >= 5 ? Int32.Parse(args[4]) : -1);
            }
            else if (args.Length >= 4 && args[1] == "links")
            {
                // -extrarules links "<script>" <first>-<last>: those items' links and actions on the page the script ends on
                FrontendSession session = Drive(args[2], -1);
                string[] range = args[3].Split('-');
                MenuPage page = session.Menu.Page!;
                for (int i = Int32.Parse(range[0]); i <= Int32.Parse(range[1]) && i < page.Items.Count; i++)
                {
                    MenuItem it = page.Items[i];
                    Console.WriteLine($"{i}: xy {it.X},{it.Y} state {session.Menu.ItemState(i)} f34 {it.Field34} f35 {it.Field35} flags {it.Flags}");
                    foreach (MenuLink l in it.Links) Console.WriteLine($"   link ev {l.Event:X} -> {l.Target} {l.State}");
                    foreach (MenuAction a in it.Actions)
                    {
                        Console.WriteLine($"   action kind {a.Kind} f2 {a.Field2} flags {a.Flags} f4 {a.Field4:X} rect {a.RectX},{a.RectY},{a.RectW},{a.RectH} item {a.Item} calls "
                            + String.Join(" ", a.Calls.Select(c => $"({c.A},{c.B})")));
                    }
                }
            }
        }

        // -extrarules check: the rules' forcing both ways, extra_rules.json round trip, banned characters off the seats, the
        // setup line's xp / xc, and every rules page's ADVANCED box carrying the two rows (shown and hidden with the box)
        private static void Check()
        {
            int pass = 0, fail = 0;
            void Expect(bool ok, string what)
            {
                if (ok) pass++;
                else fail++;
                Console.WriteLine((ok ? "PASS " : "FAIL ") + what);
            }
            var r = new ExtraRules();
            Expect(r.Pickups && r.Characters, "defaults: both on");
            r.SetPickups(false);
            Expect(!r.Pickups && !r.Characters, "pickups off turns characters off");
            r.SetCharacters(true);
            Expect(r.Pickups && r.Characters, "characters on turns pickups on");
            r.SetCharacters(false);
            Expect(r.Pickups && !r.Characters, "characters off alone keeps pickups");
            string dir = Path.Combine(Path.GetTempPath(), "mph_extrarules_" + Environment.ProcessId);
            Directory.CreateDirectory(dir);
            try
            {
                Expect(ExtraRules.Load(dir) is { Pickups: true, Characters: true }, "no file: both on");
                r.Save(dir);
                Expect(ExtraRules.Load(dir) is { Pickups: true, Characters: false }, "saved rules load back");
                File.WriteAllText(Path.Combine(dir, ExtraRules.FileName), "{\"Pickups\":false,\"Characters\":true}");
                Expect(ExtraRules.Load(dir) is { Pickups: false, Characters: false }, "a file with characters but no pickups loads both off");
                File.WriteAllText(Path.Combine(dir, ExtraRules.FileName), "not json");
                Expect(ExtraRules.Load(dir) is { Pickups: true, Characters: true }, "a bad file: both on");
            }
            finally
            {
                Directory.Delete(dir, recursive: true);
            }
            MatchSettings Match(bool pickups, bool characters)
            {
                var s = new MatchSettings { Mode = MphRead.GameMode.Battle, Arena = "Combat Hall" };
                s.Players.Add(new MatchPlayer { Hunter = MphRead.Hunter.Weavel, Skin = "MP1PowerTrooper", Character = "trooper" });
                s.Players.Add(new MatchPlayer { Hunter = MphRead.Hunter.Samus, Skin = "MP2DarkSamus", Character = "darksamus", Bot = true });
                s.Players.Add(new MatchPlayer { Hunter = MphRead.Hunter.Samus, Skin = "MP1VariaSuit", Bot = true });
                s.Players.Add(new MatchPlayer { Hunter = MphRead.Hunter.Kanden, Bot = true });
                new ExtraRules { Pickups = pickups, Characters = characters }.ApplyTo(s);
                Characters.ApplyExtraRules(s);
                return s;
            }
            MatchSettings on = Match(true, true), noChars = Match(true, false), off = Match(false, true);
            Expect(on.PirateBlaster == true && on.ExtraCharacters == true && on.Players[0].Character == "trooper"
                && on.Players[1].Character == "darksamus", "both on: the Trooper and Dark Samus stay");
            Expect(noChars.PirateBlaster == true && noChars.ExtraCharacters == false && noChars.Players[0] is { Character: null, Skin: null }
                && noChars.Players[1] is { Character: null, Skin: null } && noChars.Players[0].Hunter == MphRead.Hunter.Weavel,
                "characters off: Trooper -> Weavel, Dark Samus -> Samus, own looks");
            Expect(noChars.Players[2].Skin == "MP1VariaSuit" && noChars.Players[3].Hunter == MphRead.Hunter.Kanden,
                "characters off: a hunter's own skins stay");
            Expect(off.PirateBlaster == false && off.ExtraCharacters == false && off.Players[0].Character == null,
                "pickups off: no blaster, no characters");
            MatchSettings line = MatchSetupText.Read(MatchSetupText.Write(noChars));
            Expect(line.PirateBlaster == true && line.ExtraCharacters == false, "setup line: xp / xc round trip");
            MphRead.Entities.PlayerEntity.HostSevenWeaponSlots = off.PirateBlaster != false; // (as MatchStart)
            MphRead.GameMode mode = GameState.Mode;
            GameState.Mode = MphRead.GameMode.Battle;
            Expect(MphRead.Entities.PlayerEntity.WeaponSlotCount == 6, "pickups off: a 6-slot weapon ring");
            MphRead.Entities.PlayerEntity.HostSevenWeaponSlots = on.PirateBlaster != false;
            Expect(MphRead.Entities.PlayerEntity.WeaponSlotCount == 7, "pickups on: a 7-slot weapon ring");
            GameState.Mode = mode;
            // the menu: every rules page's box
            FrontendSession session = Drive("", 5);
            MenuFile file = session.Menu.File;
            foreach (int index in new[] { 28, 29, 30, 31, 32, 33, 34 })
            {
                MenuPage page = file.Pages[index];
                int Box(int call) => Enumerable.Range(0, page.Items.Count)
                    .FirstOrDefault(i => page.Items[i].Actions.Any(a => a.Calls.Any(c => c.B == call)), -1);
                int pickups = Box(2340), chars = Box(2341);
                MenuItem? popup = page.Items.FirstOrDefault(it => it.Links.Any(l => l.Target == pickups) && it.Links.Any(l => l.Event == 0xA));
                Expect(pickups >= 0 && chars >= 0 && popup != null
                    && popup.Links.Count(l => l.Target == chars) == 2, $"page {index}: both rows, shown + hidden with the box");
            }
            Console.WriteLine($"extrarules check: {pass} passed, {fail} failed");
        }

        private static FrontendSession Drive(string script, int end)
        {
            var steps = script.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(t => t.Split(':'))
                .Select(p => (F: Int32.Parse(p[0]), In: p[1])).ToList();
            if (end < 0) end = steps.Count == 0 ? 60 : steps.Max(t => t.F) + 60;
            FrontendSession session = FrontendSession.Load(Paths.FileSystem);
            session.Start(18);
            for (int f = 0; f <= end; f++)
            {
                foreach (var st in steps.Where(t => t.F == f))
                {
                    if (st.In.StartsWith("ds@"))
                    {
                        string[] xy = st.In[3..].Split('/');
                        session.TouchDs(Single.Parse(xy[0], CultureInfo.InvariantCulture), Single.Parse(xy[1], CultureInfo.InvariantCulture));
                    }
                    else if (st.In == "b") session.Press(MenuKeys.B);
                    else if (st.In == "a") session.Press(MenuKeys.A);
                }
                session.Build(640, 360);
                session.Tick();
            }
            return session;
        }

        private static void Shot(string name, string script, int end)
        {
            FrontendSession session = Drive(script, end);
            string[] size = (Environment.GetEnvironmentVariable("MP_SIZE") ?? "1280x720").Split('x');
            int w = Int32.Parse(size[0]), h = Int32.Parse(size[1]);
            session.Build(w, h);
            using var img = FrontendTool.RasterCanvas(session.DrawList, w, h);
            string path = Path.Combine(FrontendTool.OutDir(), name + ".png");
            img.SaveAsPng(path);
            Console.WriteLine($"page {session.Menu.Page?.Index}, shot {path}");
        }
    }
}
