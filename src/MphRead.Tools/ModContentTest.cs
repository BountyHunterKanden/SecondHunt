using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using MphRecomp.Mods;

namespace MphRead
{
    // Headless tests for mod content (MphRecomp.Mods.ModContent, docs/MOD_API.md phase 3): a mod's files/ folder
    // replaces game files while the mod runs. Run: MphRead.Tools.dll -modcontenttest (no ROM: a fake game tree).
    internal static class ModContentTest
    {
        static int _pass, _fail;
        static void Check(string name, bool ok) { if (ok) _pass++; else _fail++; Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {name}"); }

        const string Text = "stringTables/ScanLog.bin", Model = "models/Foo_Model.bin", Hud = "hud/meter.bin";

        public static void Run(string[] args)
        {
            string root = Path.Combine(Path.GetTempPath(), "mphrecomp_modcontent_" + Guid.NewGuid().ToString("N"));
            string? oldPath = Paths.AllPaths.TryGetValue(Paths.MphKey, out string? p) ? p : null;
            try
            {
                string game = Path.Combine(root, "game");
                Put(game, Text, "GAME TEXT");
                Put(game, Model, "GAME MODEL");
                Put(game, Hud, "GAME HUD");
                Paths.SetPath(Paths.MphKey, game);

                string data = Path.Combine(root, "data");
                string cos = Path.Combine(data, "mods", "a_cosmetic");
                Mod(cos, "a_cosmetic", "cosmetic", "");
                Put(Path.Combine(cos, "files"), Text, "COSMETIC TEXT");
                Put(Path.Combine(cos, "files"), Model, "COSMETIC MODEL");
                Put(Path.Combine(cos, "files"), Hud, "COSMETIC HUD");
                string play = Path.Combine(data, "mods", "b_gameplay");
                Mod(play, "b_gameplay", "gameplay",
                    "mod.on('start', function() mod.log('asset=' .. tostring(mod.asset('" + Text + "'))"
                    + " .. '|own=' .. tostring(mod.asset('notes.txt')) .. '|none=' .. tostring(mod.asset('nope/none.bin'))) end)");
                File.WriteAllText(Path.Combine(play, "notes.txt"), "OWN NOTES");
                Put(Path.Combine(play, "files"), Model.ToLowerInvariant(), "GAMEPLAY MODEL"); // case differs from the game's
                Put(Path.Combine(play, "files"), Text, "GAMEPLAY TEXT");

                Check("no mods: no hook, the game's own files", Read.HostReadFile == null && Get(Text) == "GAME TEXT" && Get(Model) == "GAME MODEL");

                var logs = new List<string>();
                LiveMods.Attach(data, null, logs.Add, match: true);
                Check("cosmetic mod: its text and HUD files replace the game's", Get(Text) == "COSMETIC TEXT" && Get(Hud) == "COSMETIC HUD");
                Check("cosmetic mod: its model file is refused (game's own), with a note in the mod log",
                    Get(Model) == "GAME MODEL" && logs.Any(l => l.Contains("a_cosmetic") && l.Contains("skipped") && l.Contains("models/")));
                Check("match: gameplay files wait for StartMatch", ModContent.GameplayCount == 0 && ModContent.CosmeticCount == 2);
                Check("lookup ignores case (Android's file system is case-sensitive)", Get("STRINGTABLES/scanlog.BIN") == "COSMETIC TEXT");

                LiveMods.StartMatch(null, out _);
                Check("match started: the gameplay mod's model replaces the game's (its file named in other case)", Get(Model) == "GAMEPLAY MODEL");
                Check("a gameplay mod's file wins over a cosmetic mod's", Get(Text) == "GAMEPLAY TEXT" && ModContent.SourceMod(Text) == "b_gameplay");
                Check("cosmetic files stay mounted", Get(Hud) == "COSMETIC HUD");
                Check("mod.asset: mounted game file, then the mod's own file, nil for neither",
                    logs.Any(l => l.Contains("asset=GAMEPLAY TEXT|own=OWN NOTES|none=nil")));
                Check("Strings path (ReadFileBytes) sees the same copy",
                    Encoding.UTF8.GetString(Read.ReadFileBytes(Path.Combine(game, Text))) == "GAMEPLAY TEXT");
                Check("a path outside the game folder isn't redirected",
                    Encoding.UTF8.GetString(Read.ReadFileBytes(Path.Combine(play, "notes.txt"))) == "OWN NOTES");

                LiveMods.Detach();
                Check("detached: hook off, the game's own files again", Read.HostReadFile == null && Get(Text) == "GAME TEXT" && Get(Model) == "GAME MODEL");

                Check("online / LAN games: nothing mounted", LiveMods.Attach(data, "network game") == 0 && Read.HostReadFile == null);

                // turning the cosmetic mod off unmounts its files
                File.WriteAllText(Path.Combine(data, "recomp_settings.json"), "{\"DisabledMods\":[\"a_cosmetic\"]}");
                LiveMods.Attach(data, null, logs.Add, match: false);
                Check("outside a match: gameplay files mount at Attach; a mod switched off mounts nothing",
                    Get(Model) == "GAMEPLAY MODEL" && Get(Hud) == "GAME HUD" && ModContent.CosmeticCount == 0);
                LiveMods.Detach();

                // a replay records the mod's sha: its files/ folder is part of it
                var mgr = new ModManager();
                mgr.Discover(Path.Combine(data, "mods"));
                ModEntry e = mgr.Available.First(x => x.Manifest.Id == "b_gameplay");
                string before = e.ComputeSha();
                Put(Path.Combine(play, "files"), Model.ToLowerInvariant(), "GAMEPLAY MODEL 2");
                Check("replays: changing a file under files/ changes the mod's sha", before != e.ComputeSha());

                ModContent.Clear();
                Check("Clear: hook off", Read.HostReadFile == null);
            }
            catch (Exception ex)
            {
                _fail++;
                Console.WriteLine("  [FAIL] threw: " + ex);
            }
            finally
            {
                LiveMods.Detach();
                ModContent.Clear();
                if (oldPath != null) Paths.SetPath(Paths.MphKey, oldPath);
                try { Directory.Delete(root, true); } catch (Exception) { }
            }
            Console.WriteLine($"MOD CONTENT TESTS: {_pass} passed, {_fail} failed{(_fail == 0 ? "  =>  ALL GREEN" : "")}");
            if (_fail > 0) Environment.ExitCode = 1;
        }

        static string Get(string rel) => Encoding.UTF8.GetString(Read.ReadBytes(rel, firstHunt: false));

        static void Put(string root, string rel, string text)
        {
            string path = Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, text);
        }

        static void Mod(string dir, string id, string kind, string lua)
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "mod.json"),
                $"{{ \"Id\": \"{id}\", \"Name\": \"{id}\", \"Version\": \"1.0.0\", \"Kind\": \"{kind}\", \"Api\": 1, \"Entry\": \"main.lua\" }}");
            File.WriteAllText(Path.Combine(dir, "main.lua"), lua);
        }
    }
}
