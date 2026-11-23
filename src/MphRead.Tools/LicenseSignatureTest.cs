using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using MphRecomp.Campaign;
using MphRecomp.Frontend;

namespace MphRead
{
    // -licensesign: the signed hunter_license.json (HunterLicense.Signature.cs, owner 11-16): a save is signed and mirrored
    // to .bak; a reload checks it (whitespace and key order don't matter); a hand edit is caught, kept aside and the last
    // good copy loaded; an unsigned (older) file is signed on its first load; a cart import still makes a valid file.
    // No ROM needed: works in a temp folder.
    internal static class LicenseSignatureTest
    {
        public static void Run()
        {
            string root = Path.Combine(Path.GetTempPath(), $"licensesign_{Environment.ProcessId}");
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            int fails = 0, n = 0;
            void Check(bool ok, string what)
            {
                Console.WriteLine($"{(ok ? "PASS" : "FAIL")} {what}");
                if (!ok) fails++;
            }
            string NewDir()
            {
                string d = Path.Combine(root, (n++).ToString());
                Directory.CreateDirectory(d);
                return d;
            }
            static string FileIn(string d) => Path.Combine(d, HunterLicense.FileName);
            static HunterLicense.FileCheck CheckOf(string file) => HunterLicense.Check(File.ReadAllText(file));
            static string LogOf(string d) => File.Exists(Path.Combine(d, "settings_changes.log")) ? File.ReadAllText(Path.Combine(d, "settings_changes.log")) : "";
            static string[] Aside(string d, string kind) => Directory.GetFiles(d, HunterLicense.FileName + "." + kind + "-*");
            static HunterLicense Sample() => new()
            {
                Name = "KANDEN", NoticeSeen = true, LastAddress = "10.0.0.7", Arenas = 0x7FF, Hunters = 0x3F,
                RankPoints = 234, RankLevel = 2, WifiWins = 17, OnlineMatches = 40, BipedKills = 321, HeadshotKills = 12,
                WirelessSeconds = 1234.5678901, WifiSeconds = 0.1 + 0.2, TotalSeconds = 98765.4321,
                WifiBestKillStreak = 9, PendingLosses = new[] { 2 }, HunterMatches = new[] { 1, 2, 3, 4, 5, 6, 7 },
                ArenaWins = new() { [117] = 3, [93] = 5, [101] = 1 }, ArenaMatches = new() { [101] = 4, [93] = 9 },
                CharacterMatches = new() { ["trooper"] = 2, ["darksamus"] = 1 }, BeatGame = true,
            };
            static bool LikeSample(HunterLicense l) => l.Name == "KANDEN" && l.RankPoints == 234 && l.WifiWins == 17
                && l.BipedKills == 321 && l.WirelessSeconds == 1234.5678901 && l.WifiSeconds == 0.1 + 0.2
                && l.TotalSeconds == 98765.4321 && l.ArenaWins[117] == 3 && l.ArenaMatches[93] == 9 && l.BeatGame
                && l.PendingLosses.SequenceEqual(new[] { 2 }) && l.CharacterMatches["trooper"] == 2;
            static string Tampered(string json, string field, string value)
            {
                var node = System.Text.Json.Nodes.JsonNode.Parse(json)!.AsObject();
                node[field] = System.Text.Json.Nodes.JsonNode.Parse(value);
                return node.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
            }
            try
            {
                // 1. sign: a save carries a signature, the .bak mirrors it, no temp file left over
                string d = NewDir();
                Sample().Save(d);
                string signed = File.ReadAllText(FileIn(d));
                Check(signed.Contains("\"Signature\": \"v1:"), "a save is signed");
                Check(CheckOf(FileIn(d)) == HunterLicense.FileCheck.Signed, "its signature checks");
                Check(File.ReadAllText(FileIn(d) + ".bak") == signed, ".bak mirrors the save");
                Check(Directory.GetFiles(d, "*.tmp").Length == 0, "no temp file left");

                // 2. reload: every value back (doubles, dictionaries), nothing logged, the file untouched
                HunterLicense back = HunterLicense.Load(d);
                Check(LikeSample(back), "a reload gives every value back");
                Check(LogOf(d) == "" && File.ReadAllText(FileIn(d)) == signed, "a good file: nothing logged or rewritten");
                back.Save(d);
                Check(File.ReadAllText(FileIn(d)) == signed, "saving it again writes the same file");

                // 3. whitespace and key order don't matter (an editor reformatting it is not an edit)
                var reordered = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(signed)!.Reverse()
                    .ToDictionary(kv => kv.Key, kv => kv.Value);
                File.WriteAllText(FileIn(d), JsonSerializer.Serialize(reordered));
                Check(CheckOf(FileIn(d)) == HunterLicense.FileCheck.Signed, "compact, keys reversed: still signed");
                Check(LikeSample(HunterLicense.Load(d)) && Aside(d, "edited").Length == 0, "... and loads as it is");

                // 4. tamper: rank points edited; the .bak's values load, the edited file kept aside, the file re-signed
                File.WriteAllText(FileIn(d), signed);
                string edited = Tampered(signed, "RankPoints", "9999");
                File.WriteAllText(FileIn(d), edited);
                Check(CheckOf(FileIn(d)) == HunterLicense.FileCheck.Edited, "an edited rank fails the check");
                HunterLicense l = HunterLicense.Load(d);
                Check(LikeSample(l), $"the .bak's values load (rank points {l.RankPoints}, want 234)");
                Check(Aside(d, "edited").Length == 1 && File.ReadAllText(Aside(d, "edited")[0]) == edited, "the edited file kept aside as it was");
                Check(CheckOf(FileIn(d)) == HunterLicense.FileCheck.Signed && LikeSample(HunterLicense.Load(d)), "the good copy is put back, signed");
                Check(LogOf(d).Contains("edited (its signature doesn't match): kept as hunter_license.json.edited-")
                    && LogOf(d).Contains("loaded the license from hunter_license.json.bak"), "the log says what happened");

                // 5. other edits: a stat, a dictionary entry, a nested array, a duplicated key, the signature itself
                foreach ((string what, string text) in new[]
                {
                    ("Wi-Fi wins", Tampered(signed, "WifiWins", "500")),
                    ("an arena's wins", Tampered(signed, "ArenaWins", "{\"117\":30,\"93\":5,\"101\":1}")),
                    ("matches by hunter", Tampered(signed, "HunterMatches", "[1,2,3,4,5,6,70]")),
                    ("a time, same value written 1234.56789010", signed.Replace("1234.5678901", "1234.56789010")),
                    ("a duplicated key", signed.Replace("\"RankPoints\": 234,", "\"RankPoints\": 234,\n  \"RankPoints\": 800,")),
                    ("the signature", Tampered(signed, "Signature", "\"v1:00\"")),
                })
                {
                    Check(text != signed && HunterLicense.Check(text) == HunterLicense.FileCheck.Edited, $"edited {what}: caught");
                }

                // 6. the signature taken out, with this install's signed .bak there: edited, not an old file
                File.WriteAllText(FileIn(d), JsonSerializer.Serialize(JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
                    Tampered(signed, "RankPoints", "8000"))!.Where(kv => kv.Key != "Signature").ToDictionary(kv => kv.Key, kv => kv.Value)));
                Check(CheckOf(FileIn(d)) == HunterLicense.FileCheck.Unsigned, "signature removed: no signature");
                l = HunterLicense.Load(d);
                Check(l.RankPoints == 234 && Aside(d, "edited").Length == 2, $"... but a signed .bak is there: caught (rank points {l.RankPoints})");

                // 7. edited, no good copy anywhere: the profile kept, rank/records/stats start again, the file kept aside
                d = NewDir();
                Sample().Save(d);
                signed = File.ReadAllText(FileIn(d));
                File.Delete(FileIn(d) + ".bak");
                File.WriteAllText(FileIn(d), Tampered(signed, "RankPoints", "9999"));
                l = HunterLicense.Load(d);
                Check(l.Name == "KANDEN" && l.NoticeSeen && l.LastAddress == "10.0.0.7" && l.Arenas == 0x7FF && l.Hunters == 0x3F,
                    "no good copy: name, notice, address, unlocks kept");
                Check(l.RankPoints == 0 && l.RankLevel == 0 && l.WifiWins == 0 && l.BipedKills == 0 && l.TotalSeconds == 0
                    && l.ArenaWins.Count == 0 && !l.BeatGame && l.PendingLosses.Length == 0, "... rank, records and stats start again");
                Check(Aside(d, "edited").Length == 1 && CheckOf(FileIn(d)) == HunterLicense.FileCheck.Signed, "... edited file kept aside, the new one signed");
                Check(LogOf(d).Contains("no good copy to go back to"), "... and logged");

                // 8. edited, the .bak edited too, the copy from before a cart import good: that one loads
                d = NewDir();
                Sample().Save(d);
                signed = File.ReadAllText(FileIn(d));
                File.Copy(FileIn(d), Path.Combine(d, MultiplayerMenu.BeforeImportFile));
                File.WriteAllText(FileIn(d), Tampered(signed, "RankPoints", "9999"));
                File.WriteAllText(FileIn(d) + ".bak", Tampered(signed, "RankPoints", "9999"));
                Check(LikeSample(HunterLicense.Load(d)) && LogOf(d).Contains("loaded the license from " + MultiplayerMenu.BeforeImportFile),
                    "the .bak edited too: the before-import copy loads");

                // 9. migrate: an unsigned file (from before signing) loads as it is and is signed at once; nobody loses one
                d = NewDir();
                string old = JsonSerializer.Serialize(Sample(), new JsonSerializerOptions { WriteIndented = true });
                Check(!old.Contains("Signature"), "an old-style save has no signature");
                File.WriteAllText(FileIn(d), old);
                l = HunterLicense.Load(d);
                Check(LikeSample(l), "an unsigned file loads its values");
                Check(CheckOf(FileIn(d)) == HunterLicense.FileCheck.Signed && File.Exists(FileIn(d) + ".bak"), "... and is signed on that load (.bak too)");
                Check(LikeSample(HunterLicense.Load(d)) && Aside(d, "edited").Length == 0, "... and reloads as it is");
                Check(LogOf(d).Contains("had no signature"), "... and logged");
                // an older build's file with only some fields (LicenseQuitTest's), and bad values cleaned as before
                d = NewDir();
                File.WriteAllText(FileIn(d), "{\"RankPoints\":50,\"RankLevel\":1,\"PendingLosses\":[1,9,-1,3]}");
                l = HunterLicense.Load(d);
                Check(l.RankPoints == 50 && l.PendingLosses.SequenceEqual(new[] { 1, 3 }) && CheckOf(FileIn(d)) == HunterLicense.FileCheck.Signed,
                    "a partial old file: loads, cleaned, signed");

                // 10. unreadable: moved aside as .unreadable-, the .bak loads; missing: the .bak restores it
                d = NewDir();
                Sample().Save(d);
                File.WriteAllText(FileIn(d), "{ not json");
                Check(LikeSample(HunterLicense.Load(d)) && Aside(d, "unreadable").Length == 1
                    && File.ReadAllText(Aside(d, "unreadable")[0]) == "{ not json", "not JSON: kept aside, the .bak loads");
                File.Delete(FileIn(d));
                Check(LikeSample(HunterLicense.Load(d)) && CheckOf(FileIn(d)) == HunterLicense.FileCheck.Signed, "missing: restored from the .bak");
                d = NewDir();
                l = HunterLicense.Load(d);
                Check(l.RankPoints == 0 && !File.Exists(FileIn(d)) && LogOf(d) == "", "a fresh install: a new license, nothing written");

                // 11. a vanilla cart import (CartSave's license, merged and taken as it is), undone: each save valid
                d = NewDir();
                Sample().Save(d);
                l = HunterLicense.Load(d);
                var cart = new CartUnlocks
                {
                    Arenas = 0x3FF, Hunters = 0x7F, LicenseBeaten = true,
                    License = new CartLicense { Name = "SAMUS", RankPoints = 900, WifiWins = 30, OnlineMatches = 50, TotalSeconds = 3600 },
                };
                File.Copy(FileIn(d), Path.Combine(d, MultiplayerMenu.BeforeImportFile), true);
                l.ImportCart(cart, name: true, merge: true, save: "TEST.sav");
                l.Save(d);
                HunterLicense merged = HunterLicense.Load(d);
                Check(CheckOf(FileIn(d)) == HunterLicense.FileCheck.Signed && merged.WifiWins == 47 && merged.Imported != null
                    && Aside(d, "edited").Length == 0, $"merged cart import: signed, loads (Wi-Fi wins {merged.WifiWins}, want 47)");
                merged.UndoImport();
                merged.Save(d);
                Check(CheckOf(FileIn(d)) == HunterLicense.FileCheck.Signed, "the import undone: signed");
                l = HunterLicense.Load(d); // (the name stays the cart's, as UndoImport says; seconds back within rounding)
                Check(l.Name == "SAMUS" && l.RankPoints == 234 && l.WifiWins == 17 && l.BipedKills == 321
                    && Math.Abs(l.TotalSeconds - 98765.4321) < 1e-6 && Aside(d, "edited").Length == 0,
                    $"... and loads the old values (rank points {l.RankPoints}, Wi-Fi wins {l.WifiWins}, seconds {l.TotalSeconds})");
                l = HunterLicense.Load(d);
                l.ImportCart(cart, name: true, merge: false, save: "TEST.sav");
                l.Save(d);
                l = HunterLicense.Load(d);
                Check(CheckOf(FileIn(d)) == HunterLicense.FileCheck.Signed && l.Name == "SAMUS" && l.WifiWins == 30, "the cart's license taken as it is: signed, loads");

                // 12. the license other players see (OnlineMenus.LicenseJson, HubSample) carries no signature
                Check(!JsonSerializer.Serialize(HunterLicense.Load(d)).Contains("Signature"), "the network license JSON has no signature");

                // 13. saves and loads from several threads at once (the menus and ReplayLink): never a bad file
                d = NewDir();
                Sample().Save(d);
                int bad = 0;
                Parallel.For(0, 64, i =>
                {
                    if (i % 2 == 0) Sample().Save(d);
                    else if (!LikeSample(HunterLicense.Load(d))) System.Threading.Interlocked.Increment(ref bad);
                });
                Check(bad == 0 && CheckOf(FileIn(d)) == HunterLicense.FileCheck.Signed && Aside(d, "edited").Length == 0
                    && Directory.GetFiles(d, "*.tmp").Length == 0, $"64 saves/loads at once: all good ({bad} bad)");
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
            Console.WriteLine(fails == 0 ? "licensesign: all passed" : $"licensesign: {fails} FAILED");
            Environment.ExitCode = fails == 0 ? 0 : 1;
        }
    }
}
