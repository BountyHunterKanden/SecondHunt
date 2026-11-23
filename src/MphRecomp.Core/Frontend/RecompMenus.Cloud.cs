using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using MphRecomp.Online;
using MphRecomp.Profile;

// SECOND HUNT SETTINGS > APP > FILES: CLOUD SAVE (docs/PROFILE_SYNC.md Phase 2, owner 2026-11-17). OFF by default; on, this
// device keeps an encrypted copy of the profile (the pack EXPORT PROFILE writes) on the online hub and syncs with it
// (Profile/CloudSync.cs). The pull and its questions are here: when CloudSync wants one (app start, a hub connect, a put the
// hub refused as a conflict) and no box is open, the copy is fetched in the background, then merged on the menu thread
// (the license by MergeProfile, the settings key by key, a campaign file both sides changed asked) and the result put.
namespace MphRecomp.Frontend
{
    public sealed partial class RecompMenus
    {
        private bool _cloudBusy;

        private RowModel CloudSaveRow() => new()
        {
            Label = "cloud save",
            Description = "on: an encrypted copy of your license, settings and campaign files is kept on the online server (after your first online match) and synced with your other devices that have this on. off: nothing is sent; you're asked whether to delete the copy there.",
            Value = () => _settings.CloudSave ? "ON" : "OFF",
            Step = _ => ToggleCloudSave(),
            Disabled = () => _inPause || _cloudBusy || _profileBusy
        };

        private void ToggleCloudSave()
        {
            CloudSync? cloud = CloudSync.Current;
            MenuPopup? popup = Import.Popup;
            if (cloud == null || popup == null)
            {
                popup?.Inform(FrontendSession.WorkInProgressText);
                return;
            }
            if (!_settings.CloudSave)
            {
                if (!CloudSeal.Available)
                {
                    popup.Inform("this device can't encrypt a cloud save.");
                    return;
                }
                _settings.CloudSave = true;
                SaveSettings();
                cloud.Restart();
                ProfileLog("cloud save: on");
                return;
            }
            _settings.CloudSave = false;
            SaveSettings();
            cloud.Stop();
            ProfileLog("cloud save: off");
            popup.Ask("cloud save is off on this device.\nalso delete the copy on the server? (your other devices lose it too)", yes =>
            {
                if (!yes) return;
                _cloudBusy = true;
                Task.Run(async () =>
                {
                    string? error = null;
                    try
                    {
                        await cloud.DeleteAsync();
                    }
                    catch (Exception ex)
                    {
                        error = ex is OnlineException oe ? oe.Message : "no connection";
                    }
                    _profilePosted.Enqueue(() =>
                    {
                        _cloudBusy = false;
                        if (error != null) ProfileLog($"cloud save: delete failed ({error})");
                        Import.Popup?.Inform(error == null ? "the cloud copy is deleted." : $"the cloud copy couldn't be deleted: {error.ToLowerInvariant()}");
                    });
                });
            });
        }

        // every menu frame: a pull CloudSync wants, once nothing else is on screen
        private void TickCloud()
        {
            CloudSync? cloud = CloudSync.Current;
            if (cloud == null || _cloudBusy || _profileBusy || _inPause || Import.Popup is not { IsOpen: false }) return;
            if (ProfileLicense?.Invoke() == null || SavesDir == null || !cloud.TakeSyncWanted()) return;
            if (OnlineIdentity.Load(cloud.ProfileDir) == null) return; // (none yet: made at the first online visit)
            _cloudBusy = true;
            Task.Run(async () =>
            {
                CloudSync.Pulled? pulled = null;
                string? error = null;
                try
                {
                    pulled = await cloud.FetchAsync();
                }
                catch (OnlineException ex)
                {
                    error = ex.Code == "old_server" ? null : $"{ex.Code}: {ex.Message}";
                }
                catch (Exception ex)
                {
                    error = $"no connection ({ex.Message})";
                }
                _profilePosted.Enqueue(() =>
                {
                    _cloudBusy = false;
                    if (error != null) ProfileLog($"cloud save: pull failed ({error})");
                    if (pulled != null && _settings.CloudSave) Pulled(cloud, pulled);
                });
            });
        }

        private void Pulled(CloudSync cloud, CloudSync.Pulled pulled)
        {
            if (pulled.Empty)
            {
                cloud.Adopt(pulled.Rev, null, null, "first put");
                return;
            }
            if (pulled.Rev == cloud.Rev)
            {
                cloud.Push("in sync");
                return;
            }
            if (pulled.Unopenable)
            {
                ProfileLog($"cloud save: the copy there (rev {pulled.Rev}) doesn't open with this identity: replaced");
                cloud.Adopt(pulled.Rev, null, null, "replace unopenable");
                return;
            }
            ProfileImport p;
            try
            {
                p = ProfilePack.Read(new MemoryStream(pulled.Plain!));
                ProfilePack.Plan(p, SavesDir!, cloud.Hashes);
            }
            catch (InvalidDataException ex) when (ex.Message.StartsWith("made by a newer", StringComparison.Ordinal))
            {
                cloud.Hold($"cloud save: the copy there is {ex.Message}; not synced");
                Import.Popup?.Inform("your cloud save was made by a newer second hunt. update this one to sync with it.");
                return;
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException)
            {
                ProfileLog($"cloud save: the copy there (rev {pulled.Rev}) can't be read ({ex.Message}): replaced");
                cloud.Adopt(pulled.Rev, null, null, "replace unreadable");
                return;
            }
            IReadOnlyDictionary<string, string> seen = cloud.Hashes;
            Dictionary<string, string> cloudHashes = new();
            foreach ((string file, _) in ProfilePack.SaveFiles())
            {
                if (p.Saves.TryGetValue(file, out byte[]? data)) cloudHashes[file] = ProfilePack.Hash(data);
            }
            // a file only this device changed since the last sync (the cloud's is the one synced then): this one stays
            foreach (SlotPlan s in p.Slots.Where(s => s.Action == SlotAction.Conflict))
            {
                if (seen.TryGetValue(s.File, out string? last) && cloudHashes.TryGetValue(s.File, out string? there) && last == there)
                {
                    s.Action = SlotAction.Same;
                    s.UseIncoming = false;
                }
            }
            string? cloudSettings = p.SettingsJson;
            string? baseSettings = cloud.SettingsBase;
            if (cloudSettings != null)
            {
                try
                {
                    p.SettingsJson = ProfileSettingsKeys.Merge(ProfileSettingsKeys.Pick(_settings), baseSettings, cloudSettings);
                }
                catch (JsonException ex)
                {
                    ProfileLog($"cloud save: the copy's settings can't be read ({ex.Message}): left out");
                    p.SettingsJson = null;
                    cloudSettings = null;
                }
            }
            MenuPopup? popup = Import.Popup;
            if (popup == null)
            {
                return;
            }
            Queue<SlotPlan> conflicts = new(p.Slots.Where(s => s.Action == SlotAction.Conflict));
            if (baseSettings == null)
            {
                // the first sync on this device with a copy there: say what happens, once
                ProfileManifest m = p.Manifest;
                string when = m.CreatedUtc == default ? "" : $" from {m.CreatedUtc.ToLocalTime():yyyy-MM-dd HH:mm}";
                popup.Ask($"a cloud save{when} is on the server ({m.Name.ToLowerInvariant()}, rank {m.RankLevel}).\nsync with it? its license is merged with yours and its settings replace these; you're asked about any campaign file both have.\n(no turns cloud save off)",
                    yes =>
                    {
                        if (yes)
                        {
                            Next();
                            return;
                        }
                        _settings.CloudSave = false;
                        SaveSettings();
                        cloud.Stop();
                        ProfileLog("cloud save: off (the first sync was declined)");
                    });
            }
            else
            {
                Next();
            }

            void Next()
            {
                if (!_settings.CloudSave) return;
                if (conflicts.Count == 0)
                {
                    FinishCloud(cloud, p, pulled.Rev, cloudSettings, cloudHashes);
                    return;
                }
                SlotPlan c = conflicts.Dequeue();
                string name = c.Slot > 0 ? $"campaign file {c.Slot}" : "the first hunt records";
                popup.Ask($"{name} changed here and in your cloud save.\nhere: {c.Here}\ncloud: {c.There}\ntake the cloud's? (no keeps this one. the one replaced is kept in before_sync)",
                    take =>
                    {
                        c.UseIncoming = take;
                        Next();
                    });
            }
        }

        private void FinishCloud(CloudSync cloud, ProfileImport p, long rev, string? cloudSettings, Dictionary<string, string> cloudHashes)
        {
            HunterLicense? license = ProfileLicense?.Invoke();
            List<string> log;
            try
            {
                log = ProfilePack.Apply(p, license, _settings, SavesDir!, null);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or JsonException)
            {
                ProfileLog($"cloud save: merge failed ({ex.Message})");
                return;
            }
            license?.Save(cloud.ProfileDir);
            SaveSettings();
            foreach (string line in log) ProfileLog(line.Replace("profile:", "cloud save:").Replace("the pack", "the cloud"));
            ProfileImported?.Invoke();
            ShowControlValues();
            cloud.Adopt(rev, cloudSettings, cloudHashes, "after a pull");
        }
    }
}
