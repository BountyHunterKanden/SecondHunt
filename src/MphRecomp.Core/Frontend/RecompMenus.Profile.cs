using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using MphRecomp.Profile;

// SECOND HUNT SETTINGS > APP > FILES: EXPORT PROFILE / IMPORT PROFILE (docs/PROFILE_SYNC.md Phase 1, owner 2026-11-16).
// The license, the synced settings and the saves in one .shprofile file (Profile/ProfilePack.cs), taken to another device
// by hand: export saves it where the DS save export does (Android Downloads/Second Hunt, the PC's Downloads), import
// opens the platform's file picker.
namespace MphRecomp.Frontend
{
    public sealed partial class RecompMenus
    {
        // the live license (FrontendSession: the multiplayer menu's; it lives in ProfileDir)
        public Func<HunterLicense?>? ProfileLicense { get; set; }
        // hands a written file to the platform (FrontendSession: the DS save export's request)
        public Action<string>? ExportFile { get; set; }
        // after an import: the multiplayer menu re-reads what it shows from the license
        public Action? ProfileImported { get; set; }

        private readonly ConcurrentQueue<Action> _profilePosted = new();
        private bool _profileBusy;

        private RowModel ExportProfileRow() => new()
        {
            Label = "export\nprofile",
            Description = "save your hunter license, settings and campaign files as one file, to import on another device.",
            Value = () => "SAVE",
            Step = _ => ExportProfile(),
            Action = true,
            Disabled = () => _inPause || _profileBusy
        };

        private RowModel ImportProfileRow() => new()
        {
            Label = "import\nprofile",
            Description = "bring in a profile file from another device: its license is merged with this one, its settings and campaign files replace these (you're asked about any file both devices changed).",
            Value = () => "OPEN",
            Step = _ => ImportProfile(),
            Action = true,
            Disabled = () => _inPause || _profileBusy
        };

        private void TickProfile()
        {
            while (_profilePosted.TryDequeue(out Action? a)) a();
        }

        private void ProfileLog(string line)
        {
            Console.Error.WriteLine(line);
            if (_settingsPath != null) Config.RecompSettings.ChangeLog(_settingsPath, line);
        }

        private void ExportProfile()
        {
            HunterLicense? license = ProfileLicense?.Invoke();
            if (Import.Popup == null) return;
            if (ExportFile == null || license == null || SavesDir == null || ProfileDir == null)
            {
                Import.Popup.Inform(FrontendSession.WorkInProgressText);
                return;
            }
            try
            {
                string path = Path.Combine(Path.GetTempPath(), ProfilePack.FileNameFor(license.Name, DateTime.Now));
                ProfileManifest m = ProfilePack.Write(path, license, _settings, SavesDir, ProfileDir, Update.UpdateHost.AppVersion);
                license.Save(ProfileDir); // (its ProfileDevice id, made on the first export)
                ProfileLog($"profile: exported {Path.GetFileName(path)} (rank {m.RankLevel}, {m.Slots.Count} campaign files{(m.FirstHunt ? ", first hunt" : "")})");
                ExportFile(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                ProfileLog($"profile: export failed ({ex.Message})");
                Import.Popup.Inform($"the profile couldn't be saved: {ex.Message.ToLowerInvariant()}");
            }
        }

        private void ImportProfile()
        {
            MenuPopup? popup = Import.Popup;
            if (popup == null) return;
            if (ImportHost.Pick == null || ProfileLicense?.Invoke() == null || SavesDir == null || ProfileDir == null)
            {
                popup.Inform("this version of the game can't open a file picker.");
                return;
            }
            _profileBusy = true;
            ImportHost.Pick(new ImportPickRequest(ImportPickKind.Profile, "profile", "SECOND HUNT PROFILE", new[] { ProfilePack.Extension }),
                picked => _profilePosted.Enqueue(() =>
                {
                    _profileBusy = false;
                    if (picked == null) return;
                    ProfileImport p;
                    try
                    {
                        using Stream? s = ImportHost.Open(picked.Token);
                        if (s == null) throw new FileNotFoundException(picked.Name);
                        if (s.Length > ProfilePack.MaxSize) throw new InvalidDataException("too big for a profile");
                        p = ProfilePack.Read(s);
                        ProfilePack.Plan(p, SavesDir!, ProfileDir!);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
                    {
                        ProfileLog($"profile: {picked.Name} refused ({ex.Message})");
                        popup.Inform($"{picked.Name.ToLowerInvariant()} isn't a second hunt profile: {ex.Message.ToLowerInvariant()}.");
                        return;
                    }
                    AskProfile(popup, p);
                }));
        }

        // the questions, one box after another: the summary, then each campaign file both devices changed
        private void AskProfile(MenuPopup popup, ProfileImport p)
        {
            ProfileManifest m = p.Manifest;
            List<string> parts = new() { $"license: {m.Name.ToLowerInvariant()}, rank {m.RankLevel} (merged with yours)" };
            if (p.SettingsJson != null) parts.Add("settings: controls, looks, sound");
            if (p.Slots.Count > 0) parts.Add($"saves: {p.Slots.Count} file{(p.Slots.Count == 1 ? "" : "s")}");
            string when = m.CreatedUtc == default ? "" : $" from {m.CreatedUtc.ToLocalTime():yyyy-MM-dd HH:mm}";
            popup.Ask($"import this profile{when}?\n\n" + string.Join("\n", parts), yes =>
            {
                if (!yes) return;
                Queue<SlotPlan> conflicts = new(p.Slots.Where(s => s.Action == SlotAction.Conflict));
                Next();

                void Next()
                {
                    if (conflicts.Count == 0)
                    {
                        Finish(popup, p);
                        return;
                    }
                    SlotPlan c = conflicts.Dequeue();
                    string name = c.Slot > 0 ? $"campaign file {c.Slot}" : "the first hunt records";
                    popup.Ask($"{name} changed on both devices.\nhere: {c.Here}\nfile: {c.There}\ntake the file's? (no keeps this one. the one replaced is kept in before_sync)",
                        take =>
                        {
                            c.UseIncoming = take;
                            Next();
                        });
                }
            });
        }

        private void Finish(MenuPopup popup, ProfileImport p)
        {
            HunterLicense? license = ProfileLicense?.Invoke();
            List<string> log;
            try
            {
                log = ProfilePack.Apply(p, license, _settings, SavesDir!, ProfileDir!);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or JsonException)
            {
                ProfileLog($"profile: import failed ({ex.Message})");
                popup.Inform($"the profile couldn't be imported: {ex.Message.ToLowerInvariant()}");
                return;
            }
            license?.Save(ProfileDir);
            SaveSettings();
            foreach (string line in log) ProfileLog(line);
            ProfileImported?.Invoke();
            ShowControlValues();
            int taken = p.Slots.Count(s => s.UseIncoming && s.Action is SlotAction.Take or SlotAction.Conflict);
            popup.Inform("profile imported." + (taken > 0 ? $" {taken} save file{(taken == 1 ? "" : "s")} taken from it." : "")
                + "\nfriends and your online card come with your backup code (online play).");
        }
    }
}
