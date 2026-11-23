using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MphRecomp.Campaign;
using MphRecomp.Config;
using MphRecomp.Frontend;

namespace MphRecomp.Profile
{
    // A profile pack (docs/PROFILE_SYNC.md Phase 1, "A"): the player's license, synced settings and saves in one .shprofile
    // file (a zip) that another device imports, by hand through APP > FILES (no server; Phase 2 sends the same pack to
    // the cloud). Never in a pack: the online identity (online_identity.json: the backup code brings that), the device-
    // local settings (ProfileSettingsKeys.DeviceLocal), the last joined address, replays, mods, imports.
    //   manifest.json   what's in it, for the import's summary (ProfileManifest)
    //   license.json    the signed license (HunterLicense.PackJson); merged on import (HunterLicense.MergeProfile)
    //   settings.json   the synced settings (ProfileSettingsKeys.Pick); applied key by key
    //   saves/          campaign1-3.json with their speedrun1-3.json, firsthunt.sav, the campaign hunters' unlock marker
    // A save is never merged: a slot this device hasn't changed since the last export or import (profile_sync.json, the
    // files' hashes then) or has empty takes the pack's; a slot changed on both sides asks. Whatever is replaced goes to
    // saves/before_sync/ first.
    public static class ProfilePack
    {
        public const string Extension = ".shprofile";
        public const string Format = "second-hunt-profile";
        public const int Version = 1;
        public const string SyncFile = "profile_sync.json";
        public const string BeforeSyncFolder = "before_sync";
        public const string FirstHuntFile = "firsthunt.sav";
        const string UnlockMarker = "campaign_hunters_unlocked";
        // a pack bigger than this isn't one of ours (the saves are a few hundred KB)
        public const long MaxSize = 32 << 20;

        static readonly JsonSerializerOptions Options = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };

        // the save files a pack carries, by name in saves/ (slot 1-3, 0 = First Hunt's)
        public static IEnumerable<(string File, int Slot)> SaveFiles()
        {
            for (int s = 1; s <= 3; s++) yield return ($"campaign{s}.json", s);
            yield return (FirstHuntFile, 0);
        }

        public static string FileNameFor(string name, DateTime now)
        {
            string safe = new string((name ?? "").Where(c => char.IsAsciiLetterOrDigit(c) || c == '-' || c == '_').ToArray());
            return $"SecondHunt-profile-{(safe.Length > 0 ? safe : "HUNTER")}-{now:yyyy-MM-dd}{Extension}";
        }

        // ---- export ----

        // Writes the pack. Returns the manifest. profileDir = the folder of recomp_settings.json and the license.
        public static ProfileManifest Write(string path, HunterLicense license, RecompSettings settings, string savesDir,
            string profileDir, string appVersion)
        {
            string tmp = path + ".tmp";
            ProfileManifest m;
            using (FileStream fs = File.Create(tmp)) m = WriteTo(fs, license, settings, savesDir, appVersion);
            File.Move(tmp, path, overwrite: true);
            Remember(profileDir, savesDir);
            return m;
        }

        // The pack into a stream (CLOUD SAVE seals it: Profile/CloudSync.cs); profile_sync.json is left alone.
        public static ProfileManifest WriteTo(Stream stream, HunterLicense license, RecompSettings settings, string savesDir,
            string appVersion)
        {
            ProfileManifest m = new()
            {
                Format = Format, Version = Version, App = appVersion, CreatedUtc = DateTime.UtcNow,
                Device = license.EnsureProfileDevice(), Name = license.Name, RankLevel = license.RankLevel,
                RankPoints = license.RankPoints, Matches = license.Matches,
            };
            string licenseJson = license.PackJson();
            string settingsJson = ProfileSettingsKeys.Pick(settings);
            using (ZipArchive zip = new(stream, ZipArchiveMode.Create, leaveOpen: true))
            {
                Entry(zip, "license.json", Encoding.UTF8.GetBytes(licenseJson));
                m.Sections.Add("license");
                Entry(zip, "settings.json", Encoding.UTF8.GetBytes(settingsJson));
                m.Sections.Add("settings");
                CampaignSaves saves = new(savesDir);
                foreach ((string file, int slot) in SaveFiles())
                {
                    string from = Path.Combine(savesDir, file);
                    if (!File.Exists(from)) continue;
                    byte[] data = File.ReadAllBytes(from);
                    if (slot > 0)
                    {
                        CampaignSlotSummary sum = saves.Summarize(slot);
                        if (!sum.Readable) continue; // (a broken file stays here)
                        m.Slots.Add(new ProfileSlot { Slot = slot, Percent = sum.CompletionPercent, PlaySeconds = sum.PlaySeconds, SavedAtUtc = sum.SavedAtUtc });
                        string splits = SpeedrunFile.PathFor(savesDir, slot);
                        if (File.Exists(splits)) Entry(zip, "saves/" + Path.GetFileName(splits), File.ReadAllBytes(splits));
                    }
                    else
                    {
                        m.FirstHunt = true;
                    }
                    Entry(zip, "saves/" + file, data);
                }
                if (saves.HuntersUnlocked() || File.Exists(saves.UnlockMarkerPath))
                {
                    Entry(zip, "saves/" + UnlockMarker, Array.Empty<byte>());
                    m.HuntersUnlocked = true;
                }
                if (m.Slots.Count > 0 || m.FirstHunt) m.Sections.Add("saves");
                Entry(zip, "manifest.json", JsonSerializer.SerializeToUtf8Bytes(m, Options));
            }
            return m;

            static void Entry(ZipArchive zip, string name, byte[] data)
            {
                using Stream s = zip.CreateEntry(name, CompressionLevel.Optimal).Open();
                s.Write(data);
            }
        }

        // ---- import ----

        // Reads a pack (throws InvalidDataException when it isn't one).
        public static ProfileImport Read(Stream stream)
        {
            ProfileImport p = new();
            using ZipArchive zip = new(stream, ZipArchiveMode.Read);
            long total = 0;
            foreach (ZipArchiveEntry e in zip.Entries)
            {
                total += e.Length;
                if (e.Length > MaxSize || total > MaxSize) throw new InvalidDataException("the file is too big for a profile");
            }
            byte[]? Get(string name)
            {
                ZipArchiveEntry? e = zip.GetEntry(name);
                if (e == null) return null;
                using Stream s = e.Open();
                using MemoryStream ms = new();
                s.CopyTo(ms);
                return ms.ToArray();
            }
            byte[] manifest = Get("manifest.json") ?? throw new InvalidDataException("not a second hunt profile (no manifest)");
            try
            {
                p.Manifest = JsonSerializer.Deserialize<ProfileManifest>(manifest, Options) ?? throw new InvalidDataException("empty manifest");
            }
            catch (JsonException ex)
            {
                throw new InvalidDataException("not a second hunt profile: " + ex.Message);
            }
            if (p.Manifest.Format != Format) throw new InvalidDataException("not a second hunt profile");
            if (p.Manifest.Version > Version) throw new InvalidDataException($"made by a newer second hunt ({p.Manifest.App}): update this one first");
            if (Get("license.json") is byte[] l) p.LicenseJson = Text(l);
            if (Get("settings.json") is byte[] st) p.SettingsJson = Text(st);
            foreach ((string file, int slot) in SaveFiles())
            {
                if (Get("saves/" + file) is byte[] data) p.Saves[file] = data;
                if (slot > 0 && Get($"saves/speedrun{slot}.json") is byte[] splits) p.Saves[$"speedrun{slot}.json"] = splits;
            }
            p.HuntersUnlocked = zip.GetEntry("saves/" + UnlockMarker) != null;
            return p;

            // (without a BOM: a file edited in Notepad has one, and JsonDocument won't read it)
            static string Text(byte[] b) => Encoding.UTF8.GetString(b.AsSpan(b.AsSpan().StartsWith(Encoding.UTF8.Preamble) ? 3 : 0));
        }

        public static ProfileImport Read(string path)
        {
            using FileStream fs = File.OpenRead(path);
            return Read(fs);
        }

        // What happens to each save the pack has: taken, already the same, or a conflict to ask about (Plan's Use says
        // which side wins; a conflict starts at this device's).
        public static void Plan(ProfileImport p, string savesDir, string profileDir) => Plan(p, savesDir, LastSeen(profileDir));

        // seen = each save's hash at the last sync with that source (profile_sync.json for files, cloud_save.json for CLOUD SAVE)
        public static void Plan(ProfileImport p, string savesDir, IReadOnlyDictionary<string, string> seen)
        {
            p.Slots.Clear();
            CampaignSaves saves = new(savesDir);
            foreach ((string file, int slot) in SaveFiles())
            {
                if (!p.Saves.TryGetValue(file, out byte[]? data)) continue;
                SlotPlan plan = new() { File = file, Slot = slot };
                p.Slots.Add(plan);
                if (slot > 0)
                {
                    try
                    {
                        CampaignSaveFile f = CampaignSaves.FromJson(Encoding.UTF8.GetString(data));
                        plan.There = Describe(f.Story != null ? SummaryOf(data, savesDir, slot) : null, f.SavedAtUtc);
                    }
                    catch (Exception ex) when (ex is InvalidDataException or JsonException or ArgumentException)
                    {
                        plan.Action = SlotAction.Unreadable;
                        continue;
                    }
                }
                else
                {
                    plan.There = "first hunt records";
                }
                string here = Path.Combine(savesDir, file);
                if (!File.Exists(here))
                {
                    plan.Action = SlotAction.Take;
                    plan.UseIncoming = true;
                    continue;
                }
                string hash = Hash(File.ReadAllBytes(here));
                if (hash == Hash(data)) plan.Action = SlotAction.Same;
                else if (seen.TryGetValue(file, out string? last) && last == hash) plan.Action = SlotAction.Take;
                else plan.Action = SlotAction.Conflict;
                if (slot > 0)
                {
                    CampaignSlotSummary sum = saves.Summarize(slot);
                    plan.Here = sum.Readable ? Describe(sum, sum.SavedAtUtc) : "can't be read";
                    if (!sum.Readable && plan.Action == SlotAction.Conflict) plan.Action = SlotAction.Take;
                }
                else
                {
                    plan.Here = "first hunt records";
                }
                plan.UseIncoming = plan.Action == SlotAction.Take;
            }
        }

        // a pack's slot, summarized as the file select would (written to a temp folder for CampaignSaves.Summarize)
        static CampaignSlotSummary? SummaryOf(byte[] data, string savesDir, int slot)
        {
            string dir = Path.Combine(Path.GetTempPath(), "shprofile-" + Guid.NewGuid().ToString("N")[..8]);
            try
            {
                Directory.CreateDirectory(dir);
                File.WriteAllBytes(Path.Combine(dir, $"campaign{slot}.json"), data);
                return new CampaignSaves(dir).Summarize(slot);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return null;
            }
            finally
            {
                try
                {
                    Directory.Delete(dir, true);
                }
                catch (Exception)
                {
                }
            }
        }

        static string Describe(CampaignSlotSummary? sum, DateTime saved)
        {
            string when = saved == default ? "" : " " + saved.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
            if (sum is not { Readable: true }) return "saved" + when;
            long h = sum.PlaySeconds / 3600, mins = sum.PlaySeconds / 60 % 60;
            return $"{sum.CompletionPercent}%, {h}:{mins:00} played, saved{when}";
        }

        // Applies the parts picked: the license merged into the live one, the synced settings onto the live ones, the
        // saves each slot's plan picked. Saves nothing but the save files and profile_sync.json: the caller saves the
        // license and the settings (their own Save, with their own backups). profileDir null: profile_sync.json is left
        // alone (CLOUD SAVE keeps its own). Returns log lines.
        public static List<string> Apply(ProfileImport p, HunterLicense? license, RecompSettings? settings, string savesDir, string? profileDir)
        {
            List<string> log = new();
            if (license != null && p.LicenseJson != null)
            {
                HunterLicense.FileCheck check = HunterLicense.Check(p.LicenseJson);
                HunterLicense? other = check is HunterLicense.FileCheck.Signed or HunterLicense.FileCheck.Edited or HunterLicense.FileCheck.Unsigned
                    ? JsonSerializer.Deserialize<HunterLicense>(p.LicenseJson) : null;
                if (other != null) log.Add(license.MergeProfile(other, check == HunterLicense.FileCheck.Signed));
                else log.Add("profile: the pack's license can't be read: left out");
            }
            if (settings != null && p.SettingsJson != null)
            {
                List<string> changed = ProfileSettingsKeys.Apply(settings, p.SettingsJson);
                log.Add($"profile: {changed.Count} settings changed" + (changed.Count > 0 ? ": " + string.Join(", ", changed.Take(40)) : ""));
            }
            string stamp = DateTime.Now.ToString("yyyy-MM-dd_HHmmss");
            CampaignSaves saves = new(savesDir);
            foreach (SlotPlan plan in p.Slots.Where(s => s.UseIncoming && s.Action is SlotAction.Take or SlotAction.Conflict))
            {
                byte[] data = p.Saves[plan.File];
                Directory.CreateDirectory(savesDir);
                string? kept = KeepBeforeSync(savesDir, plan.File, stamp);
                if (plan.Slot > 0)
                {
                    // (byte for byte, so both devices hash it the same; Plan has read it)
                    CampaignSaveFile f = CampaignSaves.FromJson(Encoding.UTF8.GetString(data));
                    WriteFile(Path.Combine(savesDir, plan.File), data);
                    if (f.Cleared) File.WriteAllBytes(saves.UnlockMarkerPath, Array.Empty<byte>());
                    // the slot's speedrun splits ride with it (none in the pack: this device's go with what was replaced)
                    string splitsName = $"speedrun{plan.Slot}.json";
                    KeepBeforeSync(savesDir, splitsName, stamp, move: true);
                    if (p.Saves.TryGetValue(splitsName, out byte[]? splits)) WriteFile(Path.Combine(savesDir, splitsName), splits);
                }
                else
                {
                    WriteFile(Path.Combine(savesDir, plan.File), data);
                }
                log.Add($"profile: {plan.File} taken from the pack ({plan.There})" + (kept != null ? $", this device's kept as {BeforeSyncFolder}/{Path.GetFileName(kept)}" : ""));
            }
            if (p.HuntersUnlocked && !File.Exists(saves.UnlockMarkerPath))
            {
                Directory.CreateDirectory(savesDir);
                File.WriteAllBytes(saves.UnlockMarkerPath, Array.Empty<byte>());
                log.Add("profile: campaign hunters unlocked (from the pack)");
            }
            if (profileDir != null) Remember(profileDir, savesDir);
            return log;
        }

        // a copy of a save in saves/before_sync/ before it's replaced (move: the file goes there instead). Null = none.
        public static string? KeepBeforeSync(string savesDir, string file, string stamp, bool move = false)
        {
            string from = Path.Combine(savesDir, file);
            if (!File.Exists(from)) return null;
            string dir = Path.Combine(savesDir, BeforeSyncFolder);
            Directory.CreateDirectory(dir);
            string to = Path.Combine(dir, $"{Path.GetFileNameWithoutExtension(file)}_{stamp}{Path.GetExtension(file)}");
            for (int i = 2; File.Exists(to); i++) to = Path.Combine(dir, $"{Path.GetFileNameWithoutExtension(file)}_{stamp}~{i}{Path.GetExtension(file)}");
            if (move) File.Move(from, to);
            else File.Copy(from, to);
            return to;
        }

        static void WriteFile(string path, byte[] data)
        {
            string tmp = path + ".tmp";
            File.WriteAllBytes(tmp, data);
            File.Move(tmp, path, overwrite: true);
        }

        // ---- profile_sync.json: the saves' hashes at the last export or import (this device's own; never in a pack) ----

        public static string Hash(byte[] data) => Convert.ToHexString(SHA256.HashData(data));

        // each save file's hash now (by name in saves/)
        public static Dictionary<string, string> SaveHashes(string savesDir)
        {
            Dictionary<string, string> hashes = new();
            foreach ((string file, _) in SaveFiles())
            {
                string path = Path.Combine(savesDir, file);
                if (File.Exists(path)) hashes[file] = Hash(File.ReadAllBytes(path));
            }
            return hashes;
        }

        // what a pack of this profile would hold, as one hash (not the zip's bytes: those carry times): CLOUD SAVE puts
        // only when it changed
        public static string ContentHash(HunterLicense license, RecompSettings settings, string savesDir)
        {
            using IncrementalHash h = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            void Add(string name, byte[] data)
            {
                h.AppendData(Encoding.UTF8.GetBytes(name + ":" + data.Length + ":"));
                h.AppendData(data);
            }
            Add("license", Encoding.UTF8.GetBytes(license.PackJson()));
            Add("settings", Encoding.UTF8.GetBytes(ProfileSettingsKeys.Pick(settings)));
            foreach ((string file, int slot) in SaveFiles())
            {
                string path = Path.Combine(savesDir, file);
                if (File.Exists(path)) Add(file, File.ReadAllBytes(path));
                string splits = slot > 0 ? SpeedrunFile.PathFor(savesDir, slot) : "";
                if (slot > 0 && File.Exists(splits)) Add(Path.GetFileName(splits), File.ReadAllBytes(splits));
            }
            CampaignSaves saves = new(savesDir);
            Add(UnlockMarker, new[] { (byte)(saves.HuntersUnlocked() || File.Exists(saves.UnlockMarkerPath) ? 1 : 0) });
            return Convert.ToHexString(h.GetHashAndReset());
        }

        public static Dictionary<string, string> LastSeen(string profileDir)
        {
            try
            {
                string path = Path.Combine(profileDir, SyncFile);
                if (!File.Exists(path)) return new();
                return JsonSerializer.Deserialize<ProfileSyncState>(File.ReadAllText(path), Options)?.Hashes ?? new();
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                return new();
            }
        }

        static void Remember(string profileDir, string savesDir)
        {
            ProfileSyncState state = new() { SyncedUtc = DateTime.UtcNow, Hashes = SaveHashes(savesDir) };
            Directory.CreateDirectory(profileDir);
            WriteFile(Path.Combine(profileDir, SyncFile), JsonSerializer.SerializeToUtf8Bytes(state, Options));
        }
    }

    public sealed class ProfileManifest
    {
        public string Format { get; set; } = "";
        public int Version { get; set; }
        public string App { get; set; } = "";
        public DateTime CreatedUtc { get; set; }
        public string Device { get; set; } = "";
        public string Name { get; set; } = "";
        public int RankLevel { get; set; }
        public int RankPoints { get; set; }
        public int Matches { get; set; }
        public List<string> Sections { get; set; } = new();
        public List<ProfileSlot> Slots { get; set; } = new();
        public bool FirstHunt { get; set; }
        public bool HuntersUnlocked { get; set; }
    }

    public sealed class ProfileSlot
    {
        public int Slot { get; set; }
        public int Percent { get; set; }
        public long PlaySeconds { get; set; }
        public DateTime SavedAtUtc { get; set; }
    }

    public sealed class ProfileSyncState
    {
        public DateTime SyncedUtc { get; set; }
        public Dictionary<string, string> Hashes { get; set; } = new();
    }

    public enum SlotAction { Take, Same, Conflict, Unreadable }

    public sealed class SlotPlan
    {
        public string File { get; set; } = "";
        public int Slot { get; set; }
        public SlotAction Action { get; set; }
        public string Here { get; set; } = "";
        public string There { get; set; } = "";
        // the pack's copy replaces this device's
        public bool UseIncoming { get; set; }
    }

    public sealed class ProfileImport
    {
        public ProfileManifest Manifest { get; set; } = new();
        public string? LicenseJson { get; set; }
        public string? SettingsJson { get; set; }
        public Dictionary<string, byte[]> Saves { get; } = new();
        public bool HuntersUnlocked { get; set; }
        public List<SlotPlan> Slots { get; } = new();
    }
}
