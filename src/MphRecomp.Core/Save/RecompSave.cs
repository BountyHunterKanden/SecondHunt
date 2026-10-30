using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

// mph-recomp save framework. Deliberately independent of MphRead's own GameState/StorySave:
// this is the versioned, MODE-AGNOSTIC format the recomp owns, so a single save works across
// presentation modes and survives format changes via migration. Pure data + logic (no engine
// or rendering deps) so it is unit-testable headlessly. Saves are the sim layer's concern only.
namespace MphRecomp.Save
{
    // Root save document. Everything the game persists hangs off this. Keep it POCO + additive:
    // new fields must default sensibly so old saves load without migration where possible.
    public sealed class SaveData
    {
        public int Version { get; set; } = SaveManager.CurrentVersion;
        public GameSettings Settings { get; set; } = new();
        public StoryProgress Story { get; set; } = new();
        // per game-mode records keyed by mode name ("SinglePlayer", "Battle", "Capture", ...)
        public Dictionary<string, ModeRecord> Records { get; set; } = new();
        // moddability hook: each mod stores arbitrary JSON under its own id, isolated from others
        // (mirrors the gen1recomp save.modData[modId] pattern). Never read another mod's slot.
        public Dictionary<string, JsonElement> ModData { get; set; } = new();
    }

    // Mode-agnostic user settings (presentation, input, audio). No gameplay state here.
    public sealed class GameSettings
    {
        public PresentationMode Presentation { get; set; } = PresentationMode.PrimeTrilogy;
        public float LookSensitivity { get; set; } = 1.0f;
        public bool InvertY { get; set; } = false;
        public float MasterVolume { get; set; } = 1.0f;
        public float MusicVolume { get; set; } = 0.8f;
        public float SfxVolume { get; set; } = 1.0f;
        public string Language { get; set; } = "en";
    }

    public enum PresentationMode { PrimeTrilogy = 0, DualScreen = 1 }

    // Single-player campaign progress. Mode-agnostic in the sense that other modes ignore it.
    public sealed class StoryProgress
    {
        public bool Started { get; set; }
        public string CurrentRoom { get; set; } = "";
        public int BossFlags { get; set; }            // bitfield, matches BossFlags semantics
        public int WeaponsUnlocked { get; set; }      // bitfield over BeamType
        public int EnergyTanks { get; set; }
        public int MissileExpansions { get; set; }
        public int UaExpansions { get; set; }
        public List<int> Octoliths { get; set; } = new();
        public List<int> ScanLog { get; set; } = new();
        public long PlaytimeSeconds { get; set; }
    }

    // Per-mode aggregate stats/records.
    public sealed class ModeRecord
    {
        public int Wins { get; set; }
        public int Losses { get; set; }
        public int Kills { get; set; }
        public int Deaths { get; set; }
        public long BestTimeMs { get; set; } = -1; // -1 = none
    }

    public static class SaveManager
    {
        // Bump when the schema changes in a way old saves need migrating for; add a case in Migrate.
        public const int CurrentVersion = 1;

        private static readonly JsonSerializerOptions Options = new()
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.Never
        };

        // Atomic write: serialize to a temp file, then replace the target, so a crash or power loss
        // mid-write can never corrupt an existing save. Creates the directory if needed.
        public static void Save(SaveData data, string path)
        {
            data.Version = CurrentVersion;
            string? dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(data, Options));
            if (File.Exists(path)) File.Replace(tmp, path, null);
            else File.Move(tmp, path);
        }

        // Load a save, migrating older versions up to CurrentVersion. Missing or corrupt files
        // yield a fresh default rather than throwing, so a bad save never bricks startup.
        public static SaveData Load(string path)
        {
            if (!File.Exists(path)) return new SaveData();
            try
            {
                SaveData? data = JsonSerializer.Deserialize<SaveData>(File.ReadAllText(path), Options);
                if (data == null) return new SaveData();
                return Migrate(data);
            }
            catch (Exception)
            {
                return new SaveData(); // corrupt/unparseable -> default (caller may warn)
            }
        }

        // Apply forward migrations until the document is at CurrentVersion. Each case upgrades by
        // exactly one version and falls through, so a v(N) save walks N -> N+1 -> ... -> current.
        public static SaveData Migrate(SaveData data)
        {
            while (data.Version < CurrentVersion)
            {
                switch (data.Version)
                {
                    // case 1: MigrateV1toV2(data); break;   // <- add when CurrentVersion becomes 2
                    default:
                        data.Version = CurrentVersion; // unknown older version: accept as-is at current
                        break;
                }
            }
            if (data.Version > CurrentVersion)
            {
                // save is from a NEWER build; keep the data but stamp current so we don't loop.
                data.Version = CurrentVersion;
            }
            return data;
        }

        // --- moddability: per-mod isolated storage (mirrors gen1recomp save.modData[modId]) ---

        // Read a mod's stored value, or `fallback` if the mod has nothing saved / it can't deserialize.
        // Always fetch fresh from the SaveData; never cache the returned object across saves.
        public static T GetModData<T>(SaveData save, string modId, T fallback)
        {
            if (save.ModData.TryGetValue(modId, out JsonElement el))
            {
                try { return el.Deserialize<T>(Options) ?? fallback; }
                catch (Exception) { return fallback; }
            }
            return fallback;
        }

        // Store a mod's value under its own id, replacing whatever was there. Other mods untouched.
        public static void SetModData<T>(SaveData save, string modId, T value)
        {
            save.ModData[modId] = JsonSerializer.SerializeToElement(value, Options);
        }

        public static bool HasModData(SaveData save, string modId) => save.ModData.ContainsKey(modId);
        public static void ClearModData(SaveData save, string modId) => save.ModData.Remove(modId);
    }
}
