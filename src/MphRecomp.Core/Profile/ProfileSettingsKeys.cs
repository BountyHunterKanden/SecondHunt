using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using MphRecomp.Config;

namespace MphRecomp.Profile
{
    // Which RecompSettings ride a profile pack (docs/PROFILE_SYNC.md Phase 1, §4): copied key by key, never the whole
    // file. DeviceLocal = this device's screen, speed, imports, mods and developer switches; Synced = the
    // player's controls, looks, sound levels and game options. Every setting is in exactly one set: ProfilePackTest fails
    // on a new setting that's in neither, so whoever adds one picks its side.
    public static class ProfileSettingsKeys
    {
        public static readonly IReadOnlySet<string> DeviceLocal = new HashSet<string>(StringComparer.Ordinal)
        {
            // the screen and the speed of this device
            "RenderScale", "FrameRate", "FpsLimit", "SmoothFrames", "WideTouchBar", "MenuLayout", "FirstHuntMenuLayout",
            // its speakers
            "Speakers",
            // what this device has imported (the Prime songs, the HD menu suits): another device may not have them
            "MusicPicker", "HiddenSongs", "HiddenMenuSongs", "MatchMusic", "MenuMusic", "MenuArtHd", "MenuArtSuit",
            "SamusSuit",
            // its mods
            "DisabledMods", "ModOrder", "ModSettings",
            // its replay recording, updater and developer switches
            "RecordReplays", "PrepareReplays", "CheckUpdatesAtLaunch", "CloudSave", "DeveloperMode", "UnlockEverything", "StatusText",
            "ClassicControlsPage",
        };

        public static readonly IReadOnlySet<string> Synced = new HashSet<string>(StringComparer.Ordinal)
        {
            "AimView", "StickCurve", "MouseRoll", "SlowMovePercent", "LeftStickDeadZone", "RightStickDeadZone", "AimCurve",
            "ScoreboardToggle", "SpiderToggle", "SlowMoveToggle", "Music", "FhShMenuMusic", "FhShTrainingMusic",
            "FhShMatchMusic", "FhShObjects", "SmoothMenus", "RedrawArt", "TrooperSkin", "SyluxSkin", "ThirdPerson",
            "ThirdPersonLeft", "MatchThirdPerson", "MapDetailed", "MapPrime", "LogbookAllScans", "CampaignAltHop", "PrimeHud",
            "CampaignRadar", "RadarPickups", "HudSway", "ShotIndicator", "HelmetOpacity", "VisorOpacity", "ReadoutOpacity",
            "ReadoutOpacityLocked", "HorizontalFov", "Gyro", "GyroSensitivity", "GyroAxis", "GyroButton", "PadGyroAxis",
            "GyroPitchSensitivity", "GyroInvertPitch", "GyroInvertYaw", "GyroDeadband", "GyroSmoothing", "GyroAccel",
            "GyroZoomPaired", "GyroZoomSensitivity", "PadLayout", "TouchControls", "TouchAim", "TouchSens", "TouchZoomPaired",
            "TouchZoomSens", "TouchPitchPercent", "TouchFireAim", "TouchLayouts", "TouchOpacity", "HudLayouts",
            "GameControlType", "GameSensitivity", "GameLookInvert", "GameLookInvertX", "MouseInvertY", "MouseInvertX",
            "TouchInvertY", "TouchInvertX", "AimPitchPercent", "MousePitchPercent", "MouseSens", "ZoomSensPaired",
            "ZoomSensitivity", "ZoomMouseSens", "ZoomStep", "ZoomReadyIcon", "DroneDialsSwapped", "TryActiveBots", "PadBinds",
            "KeyBinds", "BindsVersion", "ChatToggle", "Rumble", "SfxVolume", "MusicVolume", "ShowAddress", "ShowName",
            "ShowFriendCode", "SkipBootLogos", "SecondHuntLogo", "MenuArenaBackdrop","HolidayMode", "DeathAnimation", "DeathVanillaOrbs", "HunterIce", "SkipIntro",
            "ImportSavesOnNewFile", "SpeedrunTimer", "SpeedrunSplits",
        };

        // the settings' JSON (the same flags as RecompSettings' own file)
        private static readonly JsonSerializerOptions Options = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };

        // every saved setting: the public instance get/set properties (what ReloadFrom copies)
        public static IEnumerable<PropertyInfo> All() => typeof(RecompSettings)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead && p.CanWrite && p.GetIndexParameters().Length == 0);

        // the settings in neither set (or in both): empty when every setting has its side
        public static IEnumerable<string> Unsorted() => All().Select(p => p.Name)
            .Where(n => DeviceLocal.Contains(n) == Synced.Contains(n));

        // the synced settings as a pack's settings.json
        public static string Pick(RecompSettings settings)
        {
            JsonObject all = JsonSerializer.SerializeToNode(settings, Options)!.AsObject();
            JsonObject picked = new();
            foreach (var kv in all)
            {
                if (Synced.Contains(kv.Key)) picked[kv.Key] = kv.Value?.DeepClone();
            }
            return picked.ToJsonString(Options);
        }

        // CLOUD SAVE's three-way merge (Profile/CloudSync.cs): the keys of the cloud's settings to take, as a settings.json
        // for Apply. A key this device changed since the last sync (here != then) keeps this device's value; the others
        // take the cloud's. then null (the first sync): the cloud's all.
        public static string Merge(string here, string? then, string cloud)
        {
            if (JsonNode.Parse(cloud) is not JsonObject c) throw new JsonException("settings.json isn't an object");
            if (then == null) return cloud;
            JsonObject h = JsonNode.Parse(here) as JsonObject ?? new(), t = JsonNode.Parse(then) as JsonObject ?? new();
            JsonObject take = new();
            foreach (var kv in c)
            {
                string? hv = h[kv.Key]?.ToJsonString(), tv = t[kv.Key]?.ToJsonString();
                if (t.ContainsKey(kv.Key) && hv != tv) continue;
                take[kv.Key] = kv.Value?.DeepClone();
            }
            return take.ToJsonString(Options);
        }

        // A pack's settings.json onto the live settings: its synced keys only (a device-local or unknown key in it is
        // ignored, a synced key it lacks keeps this device's value). Returns the names that changed.
        public static List<string> Apply(RecompSettings settings, string json)
        {
            List<string> changed = new();
            if (JsonNode.Parse(json) is not JsonObject incoming) throw new JsonException("settings.json isn't an object");
            JsonObject merged = JsonSerializer.SerializeToNode(settings, Options)!.AsObject();
            foreach (var kv in incoming)
            {
                // (case as RecompSettings reads it: a pack from another build may differ)
                string? key = merged.Select(m => m.Key).FirstOrDefault(k => string.Equals(k, kv.Key, StringComparison.OrdinalIgnoreCase));
                if (key != null && Synced.Contains(key)) merged[key] = kv.Value?.DeepClone();
            }
            RecompSettings read = merged.Deserialize<RecompSettings>(Options) ?? throw new JsonException("settings.json is empty");
            foreach (PropertyInfo p in All().Where(p => Synced.Contains(p.Name)))
            {
                object? now = p.GetValue(settings), then = p.GetValue(read);
                if (JsonSerializer.Serialize(now, p.PropertyType) == JsonSerializer.Serialize(then, p.PropertyType)) continue;
                p.SetValue(settings, then);
                changed.Add(p.Name);
            }
            return changed;
        }
    }
}
