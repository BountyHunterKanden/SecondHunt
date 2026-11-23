using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using MphRecomp.Campaign;
using MphRecomp.Config;
using MphRecomp.Frontend;
using MphRecomp.Profile;

namespace MphRead
{
    // -profilepacktest: the .shprofile file (docs/PROFILE_SYNC.md Phase 1). A pack goes round two devices (temp folders):
    // a blank device takes it all; re-importing doesn't count anything twice; two devices' matches add up; rank never goes
    // down; a tampered license brings only its name and unlocks; device-local settings stay; a save both devices changed
    // is a conflict and the one replaced goes to before_sync; one unchanged here is taken without asking; speedrun splits
    // and the hunters-unlocked marker come along; a file that isn't a pack is refused. Needs the desktop ROM extraction.
    internal static class ProfilePackTest
    {
        public static void Run()
        {
            if (!ToolPaths.UseDesktopExtraction()) return; // (a campaign save's logbook reads the game's scan strings)
            int fails = 0;
            void Check(bool ok, string what)
            {
                Console.WriteLine($"{(ok ? "PASS" : "FAIL")} {what}");
                if (!ok) fails++;
            }
            string root = Path.Combine(Path.GetTempPath(), "profilepacktest-" + Guid.NewGuid().ToString("N")[..8]);
            try
            {
                Device a = new(Path.Combine(root, "a")), b = new(Path.Combine(root, "b"));
                string pack = Path.Combine(root, "a.shprofile"), back = Path.Combine(root, "b.shprofile");

                var unsorted = ProfileSettingsKeys.Unsorted().ToList();
                Check(unsorted.Count == 0 || Report(unsorted), "every setting is synced or device-local");

                a.License.Name = "SAMUS";
                a.License.RankPoints = 400;
                a.License.RankLevel = 3;
                a.License.OnlineMatches = 50;
                a.License.LocalMatches = 10;
                a.License.BipedKills = 300;
                a.License.HunterMatches[2] = 60;
                a.License.Hunters |= 1 << 3;
                a.License.WifiBestKillStreak = 9;
                a.License.Save(a.Profile);
                a.Settings.MouseSens = 3.25f;
                a.Settings.SfxVolume = 4;
                a.Settings.RenderScale = 50;
                a.Settings.FrameRate = 30;
                var saveA = a.Saves.CreateNew(1);
                saveA.PlaySeconds = 3600;
                a.Saves.Save(1, saveA);
                File.WriteAllText(SpeedrunFile.PathFor(a.SavesDir, 1), "{\"Version\":1}");
                File.WriteAllBytes(a.Saves.UnlockMarkerPath, Array.Empty<byte>());

                ProfileManifest m = ProfilePack.Write(pack, a.License, a.Settings, a.SavesDir, a.Profile, "test");
                Check(m.Slots.Count == 1 && m.HuntersUnlocked && m.RankLevel == 3 && m.Sections.Contains("saves"), "the manifest lists the license, settings, slot 1, the marker");
                using (ZipArchive z = ZipFile.OpenRead(pack))
                {
                    Check(z.GetEntry("license.json") != null && z.GetEntry("saves/campaign1.json") != null && z.GetEntry("saves/speedrun1.json") != null
                        && z.Entries.All(e => !e.FullName.Contains("online_identity")), "the pack holds the files, never online_identity.json");
                }

                // a blank device takes it all
                b.Settings.RenderScale = 75;
                b.Settings.FrameRate = 120;
                ProfileImport p = ProfilePack.Read(pack);
                ProfilePack.Plan(p, b.SavesDir, b.Profile);
                Check(p.Slots.Single().Action == SlotAction.Take, "a slot missing here is taken");
                ProfilePack.Apply(p, b.License, b.Settings, b.SavesDir, b.Profile);
                Check(b.License.Name == "SAMUS" && b.License.RankLevel == 3 && b.License.RankPoints == 400, "name and rank come over");
                Check(b.License.Matches == 60 && b.License.BipedKills == 300 && b.License.HunterMatches[2] == 60 && b.License.WifiBestKillStreak == 9,
                    "the counters and best streaks come over");
                Check(b.License.HunterOpen((Hunter)3) && b.License.FavoriteHunter == 2, "unlocks and the favourite from the counts");
                Check(Math.Abs(b.Settings.MouseSens - 3.25f) < 0.001f && b.Settings.SfxVolume == 4, "synced settings come over");
                Check(b.Settings.RenderScale == 75 && b.Settings.FrameRate == 120, "device-local settings are untouched");
                Check(File.Exists(Path.Combine(b.SavesDir, "campaign1.json")) && b.Saves.Summarize(1).PlaySeconds == 3600, "the save comes over");
                Check(File.Exists(SpeedrunFile.PathFor(b.SavesDir, 1)) && File.Exists(b.Saves.UnlockMarkerPath), "its splits and the unlock marker come over");

                // the same pack again counts nothing twice
                p = ProfilePack.Read(pack);
                ProfilePack.Plan(p, b.SavesDir, b.Profile);
                Check(p.Slots.Single().Action == SlotAction.Same, "an identical save is left alone");
                ProfilePack.Apply(p, b.License, b.Settings, b.SavesDir, b.Profile);
                Check(b.License.Matches == 60 && b.License.BipedKills == 300, "re-importing doesn't double-count");

                // both play on: the counts add up, rank is the higher
                b.License.LocalMatches += 5;
                b.License.BipedKills += 20;
                b.License.RankPoints = 300; // (lower than a's)
                b.License.SavedAt = DateTime.UtcNow.AddMinutes(-5);
                a.License.OnlineMatches += 7;
                a.License.SavedAt = DateTime.UtcNow;
                ProfilePack.Write(back, b.License, b.Settings, b.SavesDir, b.Profile, "test");
                p = ProfilePack.Read(back);
                ProfilePack.Plan(p, a.SavesDir, a.Profile);
                ProfilePack.Apply(p, a.License, a.Settings, a.SavesDir, a.Profile);
                Check(a.License.Matches == 72 && a.License.BipedKills == 320, $"two devices' counts add up ({a.License.Matches} matches, 72 expected)");
                Check(a.License.RankPoints == 400 && a.License.RankLevel == 3, "rank never goes down");
                // and round again: still no double count either way
                p = ProfilePack.Read(back);
                ProfilePack.Apply(p, a.License, null, a.SavesDir, a.Profile);
                Check(a.License.Matches == 72, "the same pack twice still adds nothing");
                ProfilePack.Write(pack, a.License, a.Settings, a.SavesDir, a.Profile, "test");
                p = ProfilePack.Read(pack);
                ProfilePack.Apply(p, b.License, null, b.SavesDir, b.Profile);
                Check(b.License.Matches == 72 && b.License.BipedKills == 320, "back the other way, both devices agree");

                // a tampered license: name and unlocks only
                Device c = new(Path.Combine(root, "c"));
                string tampered = Path.Combine(root, "t.shprofile");
                Rewrite(pack, tampered, "license.json", s => System.Text.RegularExpressions.Regex.Replace(s, "\"RankPoints\":\\s*400", "\"RankPoints\": 850"));
                p = ProfilePack.Read(tampered);
                Check(HunterLicense.Check(p.LicenseJson!) != HunterLicense.FileCheck.Signed, "an edited license fails its check");
                c.License.SavedAt = DateTime.MinValue;
                ProfilePack.Apply(p, c.License, null, c.SavesDir, c.Profile);
                Check(c.License.RankPoints == 0 && c.License.Matches == 0 && c.License.Name == "SAMUS" && c.License.HunterOpen((Hunter)3),
                    "a tampered license brings its name and unlocks only");

                // a save both devices changed is a conflict; one only the pack changed is taken
                ProfilePack.Write(pack, a.License, a.Settings, a.SavesDir, a.Profile, "test");
                p = ProfilePack.Read(pack);
                ProfilePack.Apply(p, b.License, null, b.SavesDir, b.Profile); // (in step)
                var fa = a.Saves.Load(1)!;
                fa.PlaySeconds = 7200;
                a.Saves.Save(1, fa);
                ProfilePack.Write(pack, a.License, a.Settings, a.SavesDir, a.Profile, "test");
                p = ProfilePack.Read(pack);
                ProfilePack.Plan(p, b.SavesDir, b.Profile);
                Check(p.Slots.Single().Action == SlotAction.Take, "a save unchanged here since the last sync is taken without asking");
                var fb = b.Saves.Load(1)!;
                fb.PlaySeconds = 5400;
                b.Saves.Save(1, fb);
                ProfilePack.Plan(p, b.SavesDir, b.Profile);
                SlotPlan conflict = p.Slots.Single();
                Check(conflict.Action == SlotAction.Conflict && !conflict.UseIncoming, "a save both devices changed is a conflict (starting at this device's)");
                ProfilePack.Apply(p, null, null, b.SavesDir, b.Profile);
                Check(b.Saves.Summarize(1).PlaySeconds == 5400, "kept: this device's stays");
                conflict.UseIncoming = true;
                ProfilePack.Apply(p, null, null, b.SavesDir, b.Profile);
                string before = Path.Combine(b.SavesDir, ProfilePack.BeforeSyncFolder);
                Check(b.Saves.Summarize(1).PlaySeconds == 7200, "taken: the pack's replaces it");
                Check(Directory.Exists(before) && Directory.GetFiles(before, "campaign1_*.json").Length == 1, "the one replaced is kept in before_sync");

                // not a pack
                string junk = Path.Combine(root, "junk.shprofile");
                File.WriteAllText(junk, "not a zip");
                Check(Refused(junk), "a file that isn't a zip is refused");
                using (ZipArchive z = ZipFile.Open(Path.Combine(root, "empty.shprofile"), ZipArchiveMode.Create)) z.CreateEntry("hello.txt");
                Check(Refused(Path.Combine(root, "empty.shprofile")), "a zip without a manifest is refused");
            }
            finally
            {
                try
                {
                    Directory.Delete(root, true);
                }
                catch (Exception)
                {
                }
            }
            Console.WriteLine(fails == 0 ? "profilepacktest: all passed" : $"profilepacktest: {fails} FAILED");
            Environment.ExitCode = fails == 0 ? 0 : 1;

            static bool Report(System.Collections.Generic.List<string> keys)
            {
                Console.WriteLine("  unsorted: " + string.Join(", ", keys));
                return false;
            }
            static bool Refused(string path)
            {
                try
                {
                    ProfilePack.Read(path);
                    return false;
                }
                catch (InvalidDataException)
                {
                    return true;
                }
            }
            static void Rewrite(string from, string to, string entry, Func<string, string> edit)
            {
                File.Copy(from, to, true);
                using ZipArchive z = ZipFile.Open(to, ZipArchiveMode.Update);
                ZipArchiveEntry e = z.GetEntry(entry)!;
                string text;
                using (StreamReader r = new(e.Open())) text = r.ReadToEnd();
                e.Delete();
                using StreamWriter w = new(z.CreateEntry(entry).Open(), Encoding.UTF8);
                w.Write(edit(text));
            }
        }

        private sealed class Device
        {
            public readonly string Profile, SavesDir;
            public readonly HunterLicense License = new();
            public readonly RecompSettings Settings = new();
            public readonly CampaignSaves Saves;

            public Device(string dir)
            {
                Profile = dir;
                SavesDir = Path.Combine(dir, "saves");
                Directory.CreateDirectory(SavesDir);
                Saves = new CampaignSaves(SavesDir);
            }
        }
    }
}
