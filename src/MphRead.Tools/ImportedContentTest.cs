using System;
using System.IO;
using System.Linq;
using MphRecomp.Config;
using MphRecomp.Import;

namespace MphRead
{
    // -importedcontenttest: the ship-safe gate (MphRecomp.Import.ImportedContent; docs/SHIP_SAFE_AUDIT_2026-10-10.md) on a
    // scratch import root holding one imported hd item (a complete record from today's recipe) and one hand folder
    // (hd/Kanden: no recipe, like the Brawl trophies). Public: only the imported one; dev: both. A record from another
    // importer version or recipe is stale (unusable) in both. Exit code = failures.
    public static class ImportedContentTest
    {
        public static int Run(string[] args)
        {
            RecipeStore store = RecipeStore.Embedded();
            ImportRecipe r = store.All.First(x => x.Kind == "hd");
            string root = Path.Combine(Path.GetTempPath(), "importedcontenttest_" + Environment.ProcessId);
            int fails = 0;
            void Check(string what, bool got, bool want)
            {
                Console.WriteLine($"{(got == want ? "PASS" : "FAIL")} {what}: {got} (want {want})");
                if (got != want) fails++;
            }
            string state = Path.Combine(root, "import", r.Game + ".json");
            void Write(string sha1, int importer, string status = "complete")
            {
                File.WriteAllText(state, $"{{\"items\":{{\"{r.Id}\":{{\"status\":\"{status}\",\"recipeSha1\":\"{sha1}\",\"importer\":{importer}}}}}}}");
                File.SetLastWriteTimeUtc(state, DateTime.UtcNow.AddSeconds(++_tick)); // a new mtime each write (the gate caches by it)
            }
            bool saved = RecompSettings.PublicBuild;
            try
            {
                Directory.CreateDirectory(Path.Combine(root, "hd", r.Name));
                Directory.CreateDirectory(Path.Combine(root, "hd", "Kanden"));
                Directory.CreateDirectory(Path.Combine(root, "import"));
                File.WriteAllText(Path.Combine(root, "hd", "Kanden", "Kanden.dae"), "hand-made");
                string hd = Path.Combine(root, "hd");
                Console.WriteLine($"recipe {r.Id} ({r.Game}), importer {ImporterVersions.For("hd")}");

                Write(r.Sha1, ImporterVersions.For("hd"));
                RecompSettings.PublicBuild = true;
                Check("public, imported item", ImportedContent.Usable(hd, r.Name), true);
                Check("public, hand folder hd/Kanden", ImportedContent.Usable(hd, "Kanden"), false);
                Check("public, missing folder", ImportedContent.Usable(hd, "NoSuchSuit"), false);
                Check("public, UsableNames", String.Join(",", ImportedContent.UsableNames(hd, new[] { r.Name, "Kanden" })) == r.Name, true);
                RecompSettings.PublicBuild = false;
                Check("dev, imported item", ImportedContent.Usable(hd, r.Name), true);
                Check("dev, hand folder hd/Kanden", ImportedContent.Usable(hd, "Kanden"), true);

                Write(r.Sha1, 0);
                Check("dev, importer 0 (stale)", ImportedContent.Usable(hd, r.Name), false);
                RecompSettings.PublicBuild = true;
                Check("public, importer 0 (stale)", ImportedContent.Usable(hd, r.Name), false);
                Write("0000000000000000000000000000000000000000", ImporterVersions.For("hd"));
                Check("public, old recipe (stale)", ImportedContent.Usable(hd, r.Name), false);
                Write(r.Sha1, ImporterVersions.For("hd"), "missing");
                Check("public, status missing", ImportedContent.Usable(hd, r.Name), false);
                File.Delete(state);
                Check("public, no state file", ImportedContent.Usable(hd, r.Name), false);
                RecompSettings.PublicBuild = false;
                Check("dev, no state file", ImportedContent.Usable(hd, r.Name), true);

                // the skin picker (Characters.Skins): a hand-pushed Prime 4 suit (no in-app producer yet, audit B5) is listed
                // in a dev build only; the imported suit in both
                Write(r.Sha1, ImporterVersions.For("hd"));
                Directory.CreateDirectory(Path.Combine(hd, "MP4DefaultSuit"));
                File.WriteAllText(Path.Combine(hd, "MP4DefaultSuit", "MP4DefaultSuit.dae"), "hand-pushed");
                File.WriteAllText(Path.Combine(hd, r.Name, r.Name + ".dae"), "imported");
                MphRecomp.Multiplayer.Character samus = MphRecomp.Multiplayer.Characters.All.First(c => c.Known.Contains("MP4DefaultSuit"));
                RecompSettings.PublicBuild = true;
                var pub = MphRecomp.Multiplayer.Characters.Skins(samus, hd);
                Check("public, picker skins: no MP4DefaultSuit", pub.Contains("MP4DefaultSuit"), false);
                Check($"public, picker skins: {r.Name}", pub.Contains(r.Name) || !samus.Known.Contains(r.Name), true);
                RecompSettings.PublicBuild = false;
                Check("dev, picker skins: MP4DefaultSuit", MphRecomp.Multiplayer.Characters.Skins(samus, hd).Contains("MP4DefaultSuit"), true);
                Console.WriteLine($"  public skins: {String.Join(", ", pub)}");
            }
            finally
            {
                RecompSettings.PublicBuild = saved;
                try { Directory.Delete(root, true); } catch (IOException) { }
            }
            Console.WriteLine(fails == 0 ? "importedcontenttest: all PASS" : $"importedcontenttest: {fails} FAIL");
            return fails;
        }

        private static int _tick;
    }
}
