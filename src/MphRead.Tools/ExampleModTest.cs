using System;
using System.Collections.Generic;
using System.IO;
using MphRead;
using MphRead.Entities;
using MphRecomp.Assets;
using MphRecomp.Config;
using MphRecomp.Game;
using MphRecomp.Input;
using MphRecomp.Mods;
using MphRecomp.Save;

namespace MphRead
{
    // End-to-end test that loads the REAL shipped example mod (docs/mods/example_stat_tracker) via
    // the mod manager into a full GameSession and drives it -- so the example, the docs, and the
    // whole mod+game stack are proven to work together. Run: MphRead.Tools.dll -exampletest.
    internal static class ExampleModTest
    {
        static int _pass, _fail;
        static void Check(string name, bool ok) { if (ok) _pass++; else _fail++; Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {name}"); }

        public static void Run(string[] args)
        {
            string? docs = FindDocsDir();
            if (docs == null) { Console.WriteLine("  [FAIL] could not locate docs/ (shipped example mod)"); Environment.ExitCode = 1; return; }
            string modsDir = Path.Combine(docs, "mods");

            var logs = new List<string>();
            void Log(string id, string msg) => logs.Add($"[{id}] {msg}");

            // discover the shipped mod; mount docs/ so its asset path resolves
            var mgr = new ModManager();
            mgr.Discover(modsDir);
            Check("discovered the shipped example mod", mgr.Available.Count >= 1
                && System.Linq.Enumerable.Any(mgr.Available, m => m.Manifest.Id == "example_stat_tracker"));

            var vfs = new AssetVfs();
            vfs.AddLayer("base", docs);

            var save = new SaveData();
            // session first (wires config/assets/world into its ModHost) THEN load mods into it
            var session = new GameSession(config: ConfigManager.Default(), save: save, mods: new ModHost(Log), assets: vfs);
            mgr.LoadInto(session.Mods, Log);

            var mod = session.Mods.Get("example_stat_tracker");
            Check("example mod loaded without error", mod != null && mod.Enabled && mod.Error == null);

            session.Start();                              // fires 'start' -> launch #, config + asset reads
            session.EnterRoom("MP1 SANCTORUS");           // fires 'room_enter' -> visit count
            var idle = new RawInput();
            for (int i = 0; i < 30; i++) session.Update(1.0 / 60.0, idle); // 30 ticks -> playtime

            Check("mod tracked a launch", mod!.Data["launches"].GetDouble() == 1);
            Check("mod counted the room visit", mod.Data["visits"].GetProperty("MP1 SANCTORUS").GetDouble() == 1);
            Check("mod accumulated playtime over ticks", mod.Data["playSeconds"].GetDouble() > 0.4);
            Check("mod read config (FOV 62 logged)", logs.Exists(l => l.Contains("field of view is 62")));
            Check("mod read its asset (welcome text logged)", logs.Exists(l => l.Contains("Thanks for trying")));

            // persistence: save, then a fresh session sees the launch count carry forward (1 -> 2)
            string tmp = Path.Combine(Path.GetTempPath(), "mphrecomp_example_" + Guid.NewGuid().ToString("N") + ".json");
            try
            {
                session.SaveTo(tmp);
                var save2 = SaveManager.Load(tmp);
                var session2 = new GameSession(save: save2, mods: new ModHost(), assets: vfs);
                mgr.LoadInto(session2.Mods);
                session2.Start();
                var mod2 = session2.Mods.Get("example_stat_tracker");
                Check("launch count persisted across sessions (1 -> 2)", mod2!.Data["launches"].GetDouble() == 2);
            }
            finally { try { File.Delete(tmp); } catch { } }

            PartyModes(docs);

            Console.WriteLine($"\nEXAMPLE-MOD (end-to-end) TESTS: {_pass} passed, {_fail} failed  =>  {(_fail == 0 ? "ALL GREEN" : "FAILURES")}");
            Environment.ExitCode = _fail == 0 ? 0 : 1;
        }

        // the shipped mode example (docs/mods/example_party_modes): its three modes reach the GAME MODE list, and in a
        // match playing each one its filters do what its description says (and nothing in the other two)
        static void PartyModes(string docs)
        {
            string data = Path.Combine(Path.GetTempPath(), "mphrecomp_partymodes_" + Guid.NewGuid().ToString("N"));
            try
            {
                string src = Path.Combine(docs, "mods", "example_party_modes");
                string dst = Path.Combine(data, "mods", "example_party_modes");
                Directory.CreateDirectory(dst);
                foreach (string f in Directory.GetFiles(src)) File.Copy(f, Path.Combine(dst, Path.GetFileName(f)));
                var logs = new List<string>();
                LiveMods.Attach(data, null, logs.Add, match: true);
                List<LiveMods.ModeChoice> modes = LiveMods.Modes();
                Check("party modes: INSTAGIB, HEADHUNTER, MOON BOUNCE in the mode list, all on Battle with their goals",
                    modes.Count == 3 && modes[0].Name == "INSTAGIB" && modes[1].Name == "HEADHUNTER" && modes[2].Name == "MOON BOUNCE"
                    && modes.TrueForAll(m => m.Base == GameMode.Battle)
                    && modes[0].PointGoal == 10 && modes[1].PointGoal == 15 && modes[2].PointGoal == 7);

                PlayerValues samus = Metadata.PlayerValues[0];
                (uint hit, uint self, ushort wait, int head, int body, int gravity, int jump) Play(string? id)
                {
                    LiveMods.StartMatch(null, out _, new MphRecomp.Multiplayer.MatchSettings { ModMode = id == null ? null : "example_party_modes:" + id });
                    GameState.Mode = GameMode.Battle; // (a match is on: the mod API answers only in one)
                    GameState.PointGoal = modes.Find(m => m.ModId + ":" + id == m.Key)?.PointGoal ?? 7;
                    LiveMods.MatchSetup();
                    GameState.Points[1] = 0;
                    GameFilters.KillPoints(0, 1, "PowerBeam", headshot: true, vanillaPoints: 1);
                    int head = GameState.Points[1] + 1; // (only the filter's extra points land here; vanilla's 1 is added)
                    GameState.Points[1] = 0;
                    GameFilters.KillPoints(0, 1, "PowerBeam", headshot: false, vanillaPoints: 1);
                    int body = GameState.Points[1] + 1;
                    PlayerValues v = GameFilters.PatchValues(0, "Samus", samus);
                    return (GameFilters.PlayerDamage(0, 1, 8, "PowerBeam", false), GameFilters.PlayerDamage(0, 0, 8, "PowerBeam", false),
                        GameFilters.RespawnFrames(0, 1, 180), head, body, v.BipedGravity, v.JumpSpeed);
                }
                var insta = Play("instagib");
                bool setupLogged = logs.Exists(l => l.Contains("instagib: first to 10"));
                var hunter = Play("headhunter");
                var moon = Play("moon");
                var plain = Play(null);
                Check("party modes: INSTAGIB hits kill (999), self-damage stays, respawn 1 s",
                    insta.hit == 999 && insta.self == 8 && insta.wait == 60 && insta.head == 1 && insta.gravity == samus.BipedGravity);
                Check("party modes: HEADHUNTER headshot kill = 3 points, body kill = 1, damage unchanged",
                    hunter.head == 3 && hunter.body == 1 && hunter.hit == 8 && hunter.wait == 180);
                Check("party modes: MOON BOUNCE 40% gravity, 140% jump, nothing else",
                    moon.gravity == (int)Math.Floor(samus.BipedGravity * 0.4) && moon.jump == (int)Math.Floor(samus.JumpSpeed * 1.4)
                    && moon.hit == 8 && moon.head == 1 && moon.wait == 180);
                Check("party modes: a plain Battle match is untouched",
                    plain.hit == 8 && plain.wait == 180 && plain.head == 1 && plain.gravity == samus.BipedGravity && plain.jump == samus.JumpSpeed);
                Check("party modes: mode.setup ran with the mode's goal" + (setupLogged ? "" : " (log: " + string.Join(" | ", logs) + ")"), setupLogged);
            }
            finally
            {
                LiveMods.Detach();
                LiveGame.CurrentModMode = null;
                Array.Clear(GameState.Points);
                GameState.Mode = GameMode.SinglePlayer;
                GameState.PointGoal = 0;
                try { Directory.Delete(data, recursive: true); } catch { }
            }
        }

        // walk up from the tools binary until we find docs/mods/example_stat_tracker/mod.json
        static string? FindDocsDir()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            for (int i = 0; i < 8 && dir != null; i++, dir = dir.Parent)
            {
                string candidate = Path.Combine(dir.FullName, "docs", "mods", "example_stat_tracker", "mod.json");
                if (File.Exists(candidate)) return Path.Combine(dir.FullName, "docs");
            }
            return null;
        }
    }
}
