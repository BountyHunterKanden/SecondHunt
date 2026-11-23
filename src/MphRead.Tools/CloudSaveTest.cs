using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using MphRecomp.Campaign;
using MphRecomp.Config;
using MphRecomp.Frontend;
using MphRecomp.Online;
using MphRecomp.Profile;

namespace MphRead
{
    // -cloudsavetest [server=ws://...:8790/v1/ws iddir=<folder>]: CLOUD SAVE (docs/PROFILE_SYNC.md Phase 2). Offline: the seal
    // opens only for its identity; the settings merge keeps what this device changed since the last sync and takes the
    // rest; Plan with the cloud's "seen" hashes; the content hash ignores device-local settings. With server= (the test hub
    // only): two devices of one player (one identity in iddir, which needs a started online match on that hub) go round
    // CloudSync: the first put, the second device's pull and merge, a put from an old rev refused as a conflict that asks
    // for a pull, and the merge back. Needs the desktop ROM extraction. Takes ~2 minutes online (30 s between puts).
    internal static class CloudSaveTest
    {
        public static void Run(string[] args)
        {
            if (!ToolPaths.UseDesktopExtraction()) return;
            string? server = args.FirstOrDefault(a => a.StartsWith("server=", StringComparison.Ordinal))?[7..];
            string? idDir = args.FirstOrDefault(a => a.StartsWith("iddir=", StringComparison.Ordinal))?[6..];
            int fails = 0;
            void Check(bool ok, string what)
            {
                Console.WriteLine($"{(ok ? "PASS" : "FAIL")} {what}");
                if (!ok) fails++;
            }
            string root = Path.Combine(Path.GetTempPath(), "cloudsavetest-" + Guid.NewGuid().ToString("N")[..8]);
            try
            {
                Offline(root, Check);
                if (server != null) Online(root, new Uri(server), idDir, Check).GetAwaiter().GetResult();
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
            Console.WriteLine(fails == 0 ? "cloudsavetest: all passed" : $"cloudsavetest: {fails} FAILED");
            Environment.ExitCode = fails == 0 ? 0 : 1;
        }

        private static void Offline(string root, Action<bool, string> Check)
        {
            OnlineIdentity me = OnlineIdentity.Create(), other = OnlineIdentity.Create();
            byte[] plain = Enumerable.Range(0, 5000).Select(i => (byte)i).ToArray();
            byte[] blob = CloudSeal.Seal(me, plain);
            Check(CloudSeal.Open(me, blob)?.SequenceEqual(plain) == true && CloudSeal.Open(other, blob) == null
                && CloudSaveRules.LooksSealed(blob), "the seal opens for its identity only");
            Check(!CloudSeal.Seal(me, plain).SequenceEqual(blob), "each seal has its own nonce");

            // settings merge: here changed MouseSens since the last sync, the cloud changed SfxVolume and MouseSens
            RecompSettings then = new() { MouseSens = 2f, SfxVolume = 3 };
            RecompSettings here = new() { MouseSens = 4f, SfxVolume = 3 };
            RecompSettings cloud = new() { MouseSens = 9f, SfxVolume = 6 };
            string merged = ProfileSettingsKeys.Merge(ProfileSettingsKeys.Pick(here), ProfileSettingsKeys.Pick(then), ProfileSettingsKeys.Pick(cloud));
            RecompSettings result = new() { MouseSens = 4f, SfxVolume = 3 };
            ProfileSettingsKeys.Apply(result, merged);
            Check(Math.Abs(result.MouseSens - 4f) < 0.001f && result.SfxVolume == 6, "merge: this device's change stays, the cloud's other change comes over");
            JsonObject first = (JsonObject)JsonNode.Parse(ProfileSettingsKeys.Merge(ProfileSettingsKeys.Pick(here), null, ProfileSettingsKeys.Pick(cloud)))!;
            Check(first.Count == ((JsonObject)JsonNode.Parse(ProfileSettingsKeys.Pick(cloud))!).Count, "merge: the first sync takes all the cloud's");
            Check(ProfileSettingsKeys.DeviceLocal.Contains(nameof(RecompSettings.CloudSave)) && !first.ContainsKey(nameof(RecompSettings.CloudSave)),
                "CLOUD SAVE itself is device-local, never synced");

            // content hash: changes with a synced setting or a save, not with a device-local one
            Device a = new(Path.Combine(root, "off"));
            string h0 = ProfilePack.ContentHash(a.License, a.Settings, a.SavesDir);
            a.Settings.RenderScale = 33;
            string h1 = ProfilePack.ContentHash(a.License, a.Settings, a.SavesDir);
            a.Settings.SfxVolume = 1;
            string h2 = ProfilePack.ContentHash(a.License, a.Settings, a.SavesDir);
            a.Saves.Save(1, a.Saves.CreateNew(1));
            string h3 = ProfilePack.ContentHash(a.License, a.Settings, a.SavesDir);
            Check(h0 == h1 && h1 != h2 && h2 != h3, "the content hash ignores device-local settings and follows synced ones and saves");

            // Plan with the cloud's seen hashes: unchanged here since the sync -> taken; changed both sides -> conflict
            using MemoryStream ms = new();
            Device b = new(Path.Combine(root, "off-b"));
            var f = a.Saves.Load(1)!;
            f.PlaySeconds = 100;
            a.Saves.Save(1, f);
            b.Saves.Save(1, b.Saves.CreateNew(1));
            Dictionary<string, string> seen = ProfilePack.SaveHashes(b.SavesDir);
            ProfilePack.WriteTo(ms, a.License, a.Settings, a.SavesDir, "test");
            ProfileImport p = ProfilePack.Read(new MemoryStream(ms.ToArray()));
            ProfilePack.Plan(p, b.SavesDir, seen);
            Check(p.Slots.Single().Action == SlotAction.Take, "plan: a slot unchanged here since the last cloud sync is taken");
            var fb = b.Saves.Load(1)!;
            fb.PlaySeconds = 50;
            b.Saves.Save(1, fb);
            ProfilePack.Plan(p, b.SavesDir, seen);
            Check(p.Slots.Single().Action == SlotAction.Conflict, "plan: a slot changed on both sides is asked");
        }

        // two devices of one player against a real hub (the test hub only)
        private static async Task Online(string root, Uri server, string? idDir, Action<bool, string> Check)
        {
            idDir ??= Path.Combine(root, "id");
            OnlineIdentity id = OnlineIdentity.LoadOrCreate(idDir);
            Device a = new(Path.Combine(root, "a")), b = new(Path.Combine(root, "b"));
            id.Save(a.Profile);
            id.Save(b.Profile);
            a.License.Name = "CLOUDA";
            a.License.OnlineMatches = 5;
            a.License.Save(a.Profile); // (signed, with its SavedAt, as the app's are)
            a.Settings.CloudSave = b.Settings.CloudSave = true;
            a.Settings.MouseSens = 3.25f;
            a.Settings.SfxVolume = 4;
            var s1 = a.Saves.CreateNew(1);
            s1.PlaySeconds = 1234;
            a.Saves.Save(1, s1);
            CloudSync ca = a.Sync(server), cb = b.Sync(server);
            ca.TakeSyncWanted(); // (clears the app-start pull)

            await using (OnlineClient c = await OnlineClient.ConnectAsync(server, id, "CLOUDA", "cloudsavetest", default, "", cloudOnly: true))
            {
                Console.WriteLine($"  identity {c.Me.Code} on {server} (server {c.ServerVersion})");
                Check(CloudSaveRules.Supported(c), "the hub has cloud save");
            }
            await ca.DeleteAsync(); // (a clean start; the rev stays)

            // A: nothing there, the first put
            CloudSync.Pulled none = await ca.FetchAsync();
            Check(none.Empty, $"a: no copy yet (rev {none.Rev})");
            ca.Adopt(none.Rev, null, null, "first put");
            ca.Flush("test", 45000);
            Check(ca.Rev == none.Rev + 1 && ca.LastError == null, $"a: the first put is rev {ca.Rev} ({ca.LastError ?? "ok"})");
            if (ca.LastError != null) return; // (no_matches: give the identity a match on the hub and run again with iddir=)

            // B: pull, first sync takes it all, changes a setting and adds slot 2, puts (waits out the 30 s gap)
            CloudSync.Pulled pb = await cb.FetchAsync();
            Check(!pb.Empty && !pb.Unopenable && pb.Rev == ca.Rev, "b: the copy opens with the same identity");
            ProfileImport p = ProfilePack.Read(new MemoryStream(pb.Plain!));
            ProfilePack.Plan(p, b.SavesDir, cb.Hashes);
            string cloudSettings = p.SettingsJson!;
            p.SettingsJson = ProfileSettingsKeys.Merge(ProfileSettingsKeys.Pick(b.Settings), cb.SettingsBase, cloudSettings);
            ProfilePack.Apply(p, b.License, b.Settings, b.SavesDir, null);
            Check(b.License.Name == "CLOUDA" && b.Saves.Summarize(1).PlaySeconds == 1234 && Math.Abs(b.Settings.MouseSens - 3.25f) < 0.001f,
                $"b: license, slot 1 and settings came over ({b.License.Name}, {b.Saves.Summarize(1).PlaySeconds} s, sens {b.Settings.MouseSens})");
            b.Settings.SfxVolume = 7;
            b.Saves.Save(2, b.Saves.CreateNew(2));
            cb.Adopt(pb.Rev, cloudSettings, HashesOf(p), "after a pull");
            cb.Flush("test", 75000);
            Check(cb.Rev == pb.Rev + 1 && cb.LastError == null, $"b: put rev {cb.Rev} after the gap ({cb.LastError ?? "ok"})");

            // A: changes a setting, puts from its old rev: a conflict that asks for a pull
            a.Settings.MouseSens = 5f;
            ca.Push("test");
            ca.Flush("test", 45000);
            Check(ca.Rev == pb.Rev && ca.TakeSyncWanted(), "a: a put from an old rev is refused and a pull is wanted");
            CloudSync.Pulled pa = await ca.FetchAsync();
            p = ProfilePack.Read(new MemoryStream(pa.Plain!));
            ProfilePack.Plan(p, a.SavesDir, ca.Hashes);
            Check(p.Slots.Single(s => s.Slot == 2).Action == SlotAction.Take && p.Slots.Single(s => s.Slot == 1).Action == SlotAction.Same,
                "a: b's new slot 2 is taken, slot 1 is the same");
            cloudSettings = p.SettingsJson!;
            p.SettingsJson = ProfileSettingsKeys.Merge(ProfileSettingsKeys.Pick(a.Settings), ca.SettingsBase, cloudSettings);
            ProfilePack.Apply(p, a.License, a.Settings, a.SavesDir, null);
            Check(Math.Abs(a.Settings.MouseSens - 5f) < 0.001f && a.Settings.SfxVolume == 7 && File.Exists(Path.Combine(a.SavesDir, "campaign2.json")),
                "a: its own change stays, b's setting and slot come over");
            ca.Adopt(pa.Rev, cloudSettings, HashesOf(p), "after a pull");
            ca.Flush("test", 75000);
            Check(ca.Rev == pa.Rev + 1, $"a: the merge goes up as rev {ca.Rev}");

            // the state file
            CloudSync again = a.Sync(server);
            Check(again.Rev == ca.Rev && again.SettingsBase == ca.SettingsBase && again.Hashes.Count == 2, "cloud_save.json keeps the rev, base and hashes");
            Check(await ca.DeleteAsync() && (await ca.FetchAsync()).Empty, "delete drops the copy");
        }

        private static Dictionary<string, string> HashesOf(ProfileImport p)
        {
            Dictionary<string, string> h = new();
            foreach ((string file, _) in ProfilePack.SaveFiles())
            {
                if (p.Saves.TryGetValue(file, out byte[]? data)) h[file] = ProfilePack.Hash(data);
            }
            return h;
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

            public CloudSync Sync(Uri server) => new(Profile, SavesDir, null, () => License, () => Settings, () => server, "cloudsavetest");
        }
    }
}
