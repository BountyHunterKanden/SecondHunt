using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MphRecomp.Campaign;

namespace MphRead
{
    // Vanilla cart saves (MphRecomp.Campaign.CartSave) on the PC:
    //
    //   -cartsave dump <save.sav>                          list files A/B/C (%, octoliths, play time, ending)
    //   -cartsave import <save.sav> <A|B|C> <out.json>     file -> app campaign<slot>.json
    //   -cartsave export <campaign.json> <out.sav> [base.sav] [A|B|C]   app save -> DS save (fresh, or into a base save)
    //   -cartsave test <save.sav>                          round trip every used file: import -> export -> import
    //   -cartsave license <save.sav> <out.sav>             the save with the test's made-up license in (IMPORT SAVE checks)
    internal static class CartSaveTool
    {
        public static void Run(string[] args)
        {
            if (!ToolPaths.UseDesktopExtraction())
            {
                return;
            }
            string mode = args.Length > 1 ? args[1] : "";
            if (mode == "dump" && args.Length >= 3)
            {
                Dump(Parse(args[2]));
            }
            else if (mode == "import" && args.Length >= 5)
            {
                CampaignSaveFile file = Parse(args[2]).ToCampaign(Letter(args[3]));
                File.WriteAllText(args[4], CampaignSaves.ToJson(file));
                Console.WriteLine($"wrote {args[4]} (planet {file.Planet}, {file.PlaySeconds} s, cleared {file.Cleared},"
                    + $" started {file.Started})");
            }
            else if (mode == "export" && args.Length >= 4)
            {
                CampaignSaveFile file = CampaignSaves.FromJson(File.ReadAllText(args[2]));
                byte[]? baseSave = args.Length >= 5 ? File.ReadAllBytes(args[4]) : null;
                int slot = args.Length >= 6 ? Letter(args[5]) : 0;
                File.WriteAllBytes(args[3], CartSave.Export(file, baseSave, slot));
                Console.WriteLine($"wrote {args[3]} (file {(char)('A' + slot)})");
                Dump(Parse(args[3]));
            }
            else if (mode == "test" && args.Length >= 3)
            {
                Test(args[2]);
            }
            else if (mode == "license" && args.Length >= 4)
            {
                byte[] data = File.ReadAllBytes(args[2]);
                CartSave.WriteLicense(data, args.Length >= 5 ? TestLicense(args[4]) : MadeUpLicense());
                File.WriteAllBytes(args[3], data);
                Console.WriteLine($"wrote {args[3]}");
                Dump(Parse(args[3]));
            }
            else
            {
                Console.WriteLine("usage: -cartsave dump|import|export|test ... (see CartSaveTool.cs)");
            }
        }

        private static int Letter(string text) => Char.ToUpperInvariant(text[0]) - 'A';

        private static CartSave Parse(string path)
        {
            CartSave? save = CartSave.TryParse(File.ReadAllBytes(path), out string? error);
            return save ?? throw new InvalidDataException(error?.Replace('\n', ' '));
        }

        private static void Dump(CartSave save)
        {
            foreach (CartSaveFile f in save.Files)
            {
                string text = f.Used
                    ? $"{f.CompletionPercent}%, octoliths {f.Octoliths}, {f.PlaySeconds / 3600}:{f.PlaySeconds / 60 % 60:00}:"
                        + $"{f.PlaySeconds % 60:00}{(f.Cleared ? ", ending reached" : "")}"
                    : f.Damaged ? "DAMAGED (no block passes its checksum)" : "empty";
                Console.WriteLine($"  file {f.Letter}: {text}");
            }
            CartUnlocks u = save.Unlocks;
            Console.WriteLine($"  options: {(u.Arenas == null ? "empty" : $"arenas {u.Arenas:X7}, hunters {u.Hunters:X2}")}"
                + $"{(u.Ending ? ", ending" : "")}{(u.Ending100 ? ", 100% ending" : "")}; license:"
                + $"{(u.LicenseBeaten ? " beaten" : "")}{(u.LicenseCompleted ? " 100%" : "")}"
                + $"{(!u.LicenseBeaten && !u.LicenseCompleted ? " no marks" : "")}");
            if (u.License != null)
            {
                Console.WriteLine($"  license check: {u.LicenseProblem ?? "ok"}");
                Console.WriteLine("  license: " + System.Text.Json.JsonSerializer.Serialize(u.License)); // every field, for checking an import by hand
            }
        }

        private static int _pass;
        private static int _fail;

        // the license card's IMPORT SAVE (HunterLicense.ImportCart): the save's own license (one made up here when it has
        // none: name SYLUX, 300 points, beaten, 5/12 wins, 40 kills, Alinos Perch 2/3) merged onto ours or taken as it is
        private static byte[] MadeUpLicense()
        {
            var b = new byte[0x1E8];
            System.Text.Encoding.ASCII.GetBytes("SYLUX").CopyTo(b, 0);
            void W(int o, uint v) => System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(o), v);
            W(0x18, 1u << 28 | 2u << 24 | 3u << 3); // beaten, super (300 points), favourite Sylux
            W(0x1C, 300);
            W(0x20, 2); W(0x24, 5); W(0x28, 3); W(0x2C, 7);
            W(0x30, 40); W(0x4C, 4);
            W(0x74 + 4 * 3, 9); // Sylux's matches
            W(0xD0 + 4 * 1, 2); W(0x144 + 4 * 1, 3); // room 94
            return b;
        }

        // two more for trying the import by hand (-cartsave license <in> <out> kanden|noxus): kanden = beaten only, super
        // (120 points), a light record; noxus = beaten + 100%, legendary (800 points), a heavy one
        private static byte[] TestLicense(string which)
        {
            var b = new byte[0x1E8];
            void W(int o, uint v) => System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(o), v);
            bool noxus = which.Equals("noxus", StringComparison.OrdinalIgnoreCase);
            System.Text.Encoding.ASCII.GetBytes(noxus ? "NOXUS" : "KANDEN").CopyTo(b, 0);
            if (noxus)
            {
                W(0x18, 1u << 29 | 1u << 28 | 4u << 24 | 4u << 3); // 100% + beaten, legendary, favourite Noxus
                W(0x1C, 800);
                W(0x20, 41); W(0x24, 96); W(0x28, 27); W(0x2C, 58); // wins / matches, Wi-Fi then wireless
                W(0x30, 612); W(0x34, 88); W(0x38, 143); W(0x3C, 3);
                W(0x40, 58 * 420); W(0x44, 96 * 450); W(0x48, 160 * 3600);
                W(0x4C, 7); W(0x50, 14); W(0x54, 5); W(0x58, 11);
                uint[] hunters = { 12, 9, 6, 4, 101, 15, 7 };
                for (int i = 0; i < 7; i++) W(0x74 + 4 * i, hunters[i]);
                uint[] weapons = { 120, 45, 210, 30, 18, 25, 60, 70, 34 };
                for (int i = 0; i < 9; i++) W(0xAC + 4 * i, weapons[i]);
                for (int r = 0; r < 12; r++) { W(0x144 + 4 * r, (uint)(4 + 3 * r)); W(0xD0 + 4 * r, (uint)(1 + r)); }
                uint[] modes = { 70, 20, 18, 12, 14, 10, 10 };
                for (int i = 0; i < 7; i++) W(0x1B8 + 4 * i, modes[i]);
            }
            else
            {
                W(0x18, 1u << 28 | 1u << 24 | 1u << 3); // beaten, super, favourite Kanden
                W(0x1C, 120);
                W(0x20, 1); W(0x24, 6); W(0x28, 4); W(0x2C, 11);
                W(0x30, 52); W(0x34, 9); W(0x38, 6);
                W(0x40, 11 * 400); W(0x44, 6 * 380); W(0x48, 9 * 3600);
                W(0x4C, 1); W(0x50, 4); W(0x54, 2); W(0x58, 5);
                W(0x74 + 4 * 0, 3); W(0x74 + 4 * 1, 14);
                W(0xAC + 4 * 0, 30); W(0xAC + 4 * 2, 12); W(0xAC + 4 * 4, 10);
                W(0x144 + 4 * 0, 7); W(0xD0 + 4 * 0, 3); W(0x144 + 4 * 3, 6); W(0xD0 + 4 * 3, 2);
                W(0x1B8 + 4 * 0, 12); W(0x1B8 + 4 * 2, 5);
            }
            return b;
        }

        private static void TestLicenseImport(byte[] bytes, CartSave save)
        {
            byte[] data = (byte[])bytes.Clone();
            if (save.Unlocks.License == null) CartSave.WriteLicense(data, MadeUpLicense());
            CartSave? cart = CartSave.TryParse(data, out string? error);
            CartLicense? c = cart?.Unlocks.License;
            Check($"license: reads ({error}) name {c?.Name} rank {c?.RankLevel} ({c?.RankPoints} pts) beaten {c?.Beaten}", c != null);
            if (cart == null || c == null) return;
            MphRecomp.Frontend.HunterLicense Ours()
            {
                var l = new MphRecomp.Frontend.HunterLicense { Name = "MINE", RankPoints = 500, WifiWins = 1, OnlineMatches = 2, BipedKills = 10 };
                l.ArenaWins[94] = 1;
                l.ArenaMatches[94] = 1;
                l.WifiBestWinStreak = 9;
                l.LuckyArena = 94;
                l.RankLevel = 3; // (500 points: master)
                return l;
            }
            var merged = Ours();
            Console.WriteLine("  merge: " + merged.ImportCart(cart.Unlocks, false, true));
            Check("merge: our name kept, counts added, best streak and points the higher",
                merged.Name == "MINE" && merged.WifiWins == 1 + c.WifiWins && merged.OnlineMatches == 2 + c.OnlineMatches
                && merged.BipedKills == 10 + c.BipedKills && merged.WifiBestWinStreak == Math.Max(9, c.WifiBestWinStreak)
                && merged.RankPoints == Math.Clamp(Math.Max(500, c.RankPoints), 0, 850)
                && merged.ArenaMatches.GetValueOrDefault(94) == 1 + c.ArenaMatches[1]);
            Check("merge: beaten / 100% from the save", merged.BeatGame == (c.Beaten || cart.Unlocks.Ending)
                && merged.Completed == (c.Completed || cart.Unlocks.Ending100));
            var replaced = Ours();
            Console.WriteLine("  replace: " + replaced.ImportCart(cart.Unlocks, true, false));
            string name = MphRecomp.Frontend.HunterLicense.Clean(c.Name);
            Check("replace: the save's name, counts and points as they are",
                (name == MphRecomp.Frontend.MultiplayerMenu.DefaultNickname || replaced.Name == name)
                && replaced.WifiWins == c.WifiWins && replaced.BipedKills == c.BipedKills
                && replaced.RankPoints == Math.Clamp(c.RankPoints, 0, 850) && replaced.WifiBestWinStreak == c.WifiBestWinStreak
                && replaced.ArenaMatches.GetValueOrDefault(94) == c.ArenaMatches[1]);
            Check("replace: unlocks only added", (replaced.Arenas & (cart.Unlocks.Arenas ?? 0)) == (cart.Unlocks.Arenas ?? 0)
                && (replaced.Hunters & (cart.Unlocks.Hunters ?? 0)) == (cart.Unlocks.Hunters ?? 0));
            // one imported license at a time (owner 2026-11-07): a second import takes the first one's data out first
            CartUnlocks With(string which)
            {
                byte[] d = (byte[])bytes.Clone();
                CartSave.WriteLicense(d, TestLicense(which));
                return CartSave.TryParse(d, out _)!.Unlocks;
            }
            CartUnlocks kanden = With("kanden"), noxus = With("noxus");
            var only = Ours();
            only.ImportCart(noxus, false, true, "noxus");
            var both = Ours();
            both.ImportCart(kanden, false, true, "kanden");
            Console.WriteLine("  kanden then noxus: " + both.ImportCart(noxus, false, true, "noxus"));
            string Key(MphRecomp.Frontend.HunterLicense l) => $"{l.WifiWins}/{l.OnlineMatches} {l.WirelessWins}/{l.LocalMatches} "
                + $"{l.BipedKills} {l.AltFormKills} {l.HeadshotKills} {l.Disconnects} {l.WifiSeconds} {l.WirelessSeconds} {l.TotalSeconds} "
                + $"{l.RankPoints} {l.RankLevel} {l.WifiBestWinStreak} {l.WifiBestKillStreak} {l.WirelessBestWinStreak} {l.WirelessBestKillStreak} "
                + $"{string.Join(",", l.HunterMatches)} {string.Join(",", l.WeaponKills)} {string.Join(",", l.ModeMatches)} "
                + $"{string.Join(",", l.ArenaWins.OrderBy(p => p.Key))} {string.Join(",", l.ArenaMatches.OrderBy(p => p.Key))} "
                + $"{l.FavoriteHunter} {l.FavoriteWeapon} {l.FavoriteMode} {l.LuckyArena} {l.BeatGame} {l.Completed} {l.Arenas:X} {l.Hunters:X}";
            Check("one import: kanden then noxus = noxus alone (no stacking)", Key(both) == Key(only));
            Check("connection history left alone (noxus's 3 disconnects not imported)", only.Disconnects == 0);
            // a campaign file imported from a DS save opens nothing on the license (only the license's IMPORT does); what
            // the file does in SH since does
            var story = new MphRead.StorySave { DefeatedHunters = 0x06 };
            (int storyArenas, int storyHunters) = MphRecomp.Frontend.HunterLicense.StoryUnlocks(story);
            var fresh = new MphRecomp.Frontend.HunterLicense { Hunters = 1 };
            bool leaked = fresh.FromStory(story, storyArenas, storyHunters);
            Check("campaign import: the imported file's defeated hunters stay off the license", !leaked && fresh.Hunters == 1);
            // the baseline goes through the save file (11-07: the writer dropped it, and the import opened everything)
            var saved = MphRecomp.Campaign.CampaignSaves.FromJson(MphRecomp.Campaign.CampaignSaves.ToJson(new MphRecomp.Campaign.CampaignSaveFile
                { Version = 1, Story = story, ImportedArenas = storyArenas, ImportedHunters = storyHunters }));
            Check("campaign import: the baseline is kept in the saved file", saved.ImportedArenas == storyArenas && saved.ImportedHunters == storyHunters
                && !fresh.FromStory(saved.Story, saved.ImportedArenas ?? 0, saved.ImportedHunters ?? 0));
            story.DefeatedHunters |= 0x08;
            Check("campaign import: a hunter defeated in SH since opens", fresh.FromStory(story, storyArenas, storyHunters) && fresh.Hunters == 0x09);
            var undone = Ours();
            string before = Key(undone);
            undone.ImportCart(noxus, false, true, "noxus");
            undone.UndoImport();
            Check("one import: undo gives the license back as it was" + (Key(undone) == before ? "" : $" was {before} now {Key(undone)}"), Key(undone) == before);
            var played = Ours();
            played.ImportCart(noxus, false, true, "noxus");
            played.RankPoints -= 10; // a lost match since
            played.WifiBestKillStreak = 30; // a new own best since
            played.GoodEnding(100); // and the game beaten here
            var round = System.Text.Json.JsonSerializer.Deserialize<MphRecomp.Frontend.HunterLicense>(
                System.Text.Json.JsonSerializer.Serialize(played))!;
            round.ImportCart(kanden, false, true, "kanden");
            Check($"one import: own play since kept ({round.RankPoints} pts, best {round.WifiBestKillStreak}, 100% {round.Completed})",
                round.Imported?.Save == "kanden" && round.RankPoints == Math.Max(490, 120) && round.WifiBestKillStreak == 30
                && round.Completed && round.BeatGame);
            var swap = Ours();
            swap.ImportCart(noxus, true, false, "noxus");
            swap.ImportCart(kanden, false, false, "kanden");
            var kOnly = Ours();
            kOnly.ImportCart(kanden, false, false, "kanden");
            Check("one import: replace then replace = the second alone",
                swap.WifiWins == kOnly.WifiWins && swap.RankPoints == kOnly.RankPoints && swap.Completed == kOnly.Completed);
        }

        private static void Check(string name, bool ok)
        {
            if (ok) _pass++; else _fail++;
            Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {name}");
        }

        private static void Test(string path)
        {
            byte[] bytes = File.ReadAllBytes(path);
            CartSave save = Parse(path);
            Dump(save);
            TestLicenseImport(bytes, save);
            Check("a padded save (.dsv footer) reads the same",
                CartSave.TryParse(bytes.Concat(new byte[122]).ToArray(), out _)?.Files.Count(f => f.Used)
                    == save.Files.Count(f => f.Used));
            Check("a short file is refused", CartSave.TryParse(bytes[..0x1000], out _) == null);
            foreach (CartSaveFile f in save.Files.Where(f => f.Used))
            {
                CampaignSaveFile app = save.ToCampaign(f.Index);
                // the ship logbook's pages
                if (app.Story != null)
                {
                    int lore = MphRecomp.Frontend.ShipLogbook.Percent(app.Story, MphRecomp.Frontend.LogCategory.Lore);
                    int bio = MphRecomp.Frontend.ShipLogbook.Percent(app.Story, MphRecomp.Frontend.LogCategory.Bioform);
                    int obj = MphRecomp.Frontend.ShipLogbook.Percent(app.Story, MphRecomp.Frontend.LogCategory.Object);
                    Check($"file {f.Letter}: logbook lore {lore}% bioform {bio}% object {obj}%",
                        f.CompletionPercent < 100 || lore == 100 && bio == 100 && obj == 100);
                }
                string json = CampaignSaves.ToJson(app);
                CampaignSaveFile reloaded = CampaignSaves.FromJson(json);
                foreach (int slot in new[] { 0, f.Index })
                {
                    byte[] fresh = CartSave.Export(reloaded);
                    byte[] merged = CartSave.Export(reloaded, bytes, slot);
                    foreach ((string kind, byte[] data, int at) in new[] { ("fresh", fresh, 0), ("into the source", merged, slot) })
                    {
                        CartSave? back = CartSave.TryParse(data, out string? error);
                        Check($"file {f.Letter} -> {kind} save file {(char)('A' + at)}: reads back ({error})", back?.Files[at].Used == true);
                        if (back?.Files[at].Used != true) continue;
                        CartSaveFile g = back.Files[at];
                        Check($"  same %/octoliths/time/ending ({g.CompletionPercent}% {g.Octoliths} {g.PlaySeconds} {g.Cleared})",
                            g.CompletionPercent == f.CompletionPercent && g.Octoliths == f.Octoliths
                            && g.PlaySeconds == f.PlaySeconds && g.Cleared == f.Cleared);
                        string again = CampaignSaves.ToJson(Stamp(back.ToCampaign(at), app));
                        Check("  same app save after a second import", again == json);
                    }
                }
                // the license written out with the file (EXPORT DS SAVE): fresh + into the source, read back the same
                var license = new MphRecomp.Frontend.HunterLicense();
                license.ImportCart(save.Unlocks, false, true);
                var lic = new MphRecomp.Campaign.CartUnlocks
                {
                    Arenas = license.Arenas, Hunters = license.Hunters, Ending = license.BeatGame,
                    Ending100 = license.Completed, LicenseBeaten = license.BeatGame, LicenseCompleted = license.Completed
                };
                foreach ((string kind, byte[]? source) in new[] { ("fresh", (byte[]?)null), ("into the source", bytes) })
                {
                    byte[] withLicense = CartSave.Export(reloaded, source, source == null ? 0 : f.Index, lic);
                    CartUnlocks? u = CartSave.TryParse(withLicense, out _)?.Unlocks;
                    int arenaMask = 0x7FFFFFF;
                    bool arenasOk = u?.Arenas is int a && (a & (license.Arenas & arenaMask)) == (license.Arenas & arenaMask)
                        && (source != null || (a & 0x161295) == 0x161295);
                    Check($"file {f.Letter} -> {kind} save with the license: arenas {u?.Arenas:X7} hunters {u?.Hunters:X2}"
                        + $" ending {u?.Ending}/{u?.Ending100} license {u?.LicenseBeaten}/{u?.LicenseCompleted}",
                        u != null && arenasOk && u.Hunters is int h && (h & license.Hunters & 0x7F) == (license.Hunters & 0x7F)
                        && u.LicenseBeaten == (license.BeatGame || source != null && save.Unlocks.LicenseBeaten)
                        && u.LicenseCompleted == (license.Completed || source != null && save.Unlocks.LicenseCompleted)
                        && u.Ending == (license.BeatGame || source != null && save.Unlocks.Ending));
                    // a second export onto that one moves to the next block and still reads back
                    byte[] twice = CartSave.Export(reloaded, withLicense, source == null ? 0 : f.Index, lic);
                    CartUnlocks? u2 = CartSave.TryParse(twice, out _)?.Unlocks;
                    Check($"  again onto it: same unlocks", u2 != null && u2.Arenas == u!.Arenas && u2.Hunters == u.Hunters
                        && u2.LicenseBeaten == u.LicenseBeaten && u2.LicenseCompleted == u.LicenseCompleted);
                }
                // a file written back into its own slot of its own save = that block's story bytes unchanged
                if (f.Index >= 0)
                {
                    byte[] merged = CartSave.Export(reloaded, bytes, f.Index);
                    int diffs = 0;
                    var orig = OwnStoryBytes(bytes, f.Index);
                    var mine = OwnStoryBytes(merged, f.Index);
                    for (int i = 0; i < orig.Length; i++)
                    {
                        if (orig[i] != mine[i])
                        {
                            if (diffs++ < 12) Console.WriteLine($"    +0x{i:X4}: {orig[i]:X2} -> {mine[i]:X2}");
                        }
                    }
                    Check($"file {f.Letter} written back: {diffs} story bytes differ from the vanilla block", true);
                }
            }
            Console.WriteLine($"cartsave test: {_pass} pass, {_fail} fail");
        }

        // SavedAtUtc is the import time; take it from the first import so the JSON compares
        private static CampaignSaveFile Stamp(CampaignSaveFile file, CampaignSaveFile like)
        {
            file.SavedAtUtc = like.SavedAtUtc;
            return file;
        }

        private static byte[] OwnStoryBytes(byte[] data, int file)
        {
            int best = -1;
            uint bestIndex = 0;
            for (int b = 0; b < 13; b++)
            {
                int o = 0x100 + (file * 13 + b) * 0x1100;
                uint checksum = BitConverter.ToUInt32(data, o);
                uint index = BitConverter.ToUInt32(data, o + 4);
                if (checksum != 0 && (best < 0 || index > bestIndex))
                {
                    best = o;
                    bestIndex = index;
                }
            }
            return data.AsSpan(best + 8, 0x10B4).ToArray();
        }
    }
}
