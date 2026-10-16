using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

// The recomp's own options (not the game's -- those stay in the game's save): what the RECOMP SETTINGS screen under
// Options edits, and which mods are switched off. One small JSON file beside the user's other files; a missing or
// unreadable file is just the defaults.
namespace MphRecomp.Config
{
    public sealed class RecompSettings
    {
        public static readonly int[] FrameRates = { 30, 60, 90, 120, 0 }; // 0 = as fast as the display goes
        public static readonly int[] RenderScales = { 50, 75, 100, 125, 150, 200 }; // percent of the display
        // GameAudio.cs MusicMode: orig (the DS's own playback), orig + fixes (the default), hq + fixes, hq + fixes + tone
        public static readonly string[] MusicSources = { "plain", "original", "hq", "hqtone" };
        public static readonly string[] MenuLayouts = { "combined", "sidebyside" };
        public static readonly string[] GyroModes = { "off", "on", "zoom" };
        public static readonly int[] GyroSensitivities = { 50, 75, 100, 125, 150, 200, 250, 300, 400, 500 }; // percent
        public static readonly string[] GyroAxes = { "yaw", "player", "roll" };
        public static readonly string[] TouchModes = { "auto", "on", "off" };

        // A public build (the Android app's MPH_PUBLIC, set once at startup by its Application class): RECOMP SETTINGS
        // hides the rows for features that aren't in that build (imports, developer tools). Not saved.
        public static bool PublicBuild { get; set; }
        // the app's package id (dev and public builds differ), for paths shown to the player
        public static string AppId { get; set; } = "com.mphrecomp.app";
        // the Windows host (set once at startup): RECOMP SETTINGS leaves out the rows a PC has no use for (gyro, touch
        // controls) and the crash log opens its folder instead of a share sheet. Not saved.
        public static bool DesktopHost { get; set; }

        public int FrameRate { get; set; } = 60;
        public int RenderScale { get; set; } = 100;
        public string Music { get; set; } = "original";
        public string MenuLayout { get; set; } = "combined";
        public bool SmoothMenus { get; set; }
        // The menus' backdrop: Samus as an HD render of the user's own imported suit (Frontend/MenuSamus.cs: Echoes' Varia
        // first, else another Prime suit; rendered once on the device) or false = the DS art (sourceimages/bg main1/main2)
        public bool MenuArtHd { get; set; } = true;
        // The title's TOUCH TO START bar across the whole canvas (its striped ends at the screen's edges, as it spans the DS
        // touch screen; FrontendSession.EmitWideBar) or false = its DS size in the combined menu layout
        public bool WideTouchBar { get; set; } = true;
        // HUD/menu line art redrawn as clean geometry from the user's ROM (Frontend/Redraw, PixelRedraw); off = the DS pixels
        public bool RedrawArt { get; set; } = true;
        public bool DeveloperMode { get; set; }
        // dev only: the upper-left status text in gameplay (fps, scoreboard, L3 cycles it); off in every release (owner 2026-10-10)
        public bool StatusText { get; set; }
        // Samus's first-person arm cannon (and, suit permitting, her HD morph ball) in the Adventure campaign: a
        // guns/<folder> exported on the device, or "original" for MPH's own DS gun (no HD). An explicit --es gun
        // intent extra (the dev launcher) still overrides this; see CampaignActivity.
        // "mp1" (guns/mp1, the 2026-09-28 first export of the Prime 1 Power Suit's gun, still on devices) reads as
        // MP1PowerSuit: the same suit, with its newer gun, HD body and morph ball (Campaign mode assembly, owner 10-01).
        public string SamusSuit
        {
            get => _samusSuit;
            set => _samusSuit = value != null && value.Equals("mp1", StringComparison.OrdinalIgnoreCase) ? "MP1PowerSuit" : value!;
        }
        private string _samusSuit = "MP1PowerSuit";
        // The campaign's camera: first person (the game's own) or third person (the suit's body seen from behind; render
        // only, CampaignActivity.UpdateThirdPerson). Was a Select toggle until 2026-10-03; Select opens the map now.
        public bool ThirdPerson { get; set; }
        // The pause map (Select; Core Frontend/PauseMap.cs). MapDetailed: rooms drawn from their collision meshes instead of
        // the game's map models (Y in the map switches it too). MapPrime: Metroid Prime map behaviour with the Hunters look
        // (only rooms and connectors Samus has been in, free moving, the room in the middle selected); off = vanilla's.
        public bool MapDetailed { get; set; }
        public bool MapPrime { get; set; } = true;
        // Gyro aim in the campaign (MphRead.Android/CampaignGyro.cs, math in Input/GyroAim.cs), added on top of the right
        // stick: "off", "on" (always) or "zoom" (only while zoomed or in the scan visor). GyroSensitivity: the view turns
        // this percent of the device's own rotation. GyroAxis: which rotation turns -- "yaw" (the screen's own vertical),
        // "player" (the world's vertical, however it's held) or "roll" (steering-wheel tilt).
        public string Gyro { get; set; } = "off";
        public int GyroSensitivity { get; set; } = 150;
        public string GyroAxis { get; set; } = "yaw";
        // On-screen touch controls in the campaign and in matches (MphRead.Android/MatchTouchOverlay.cs): "auto" shows
        // them only while no game controller is connected, "on" always, "off" never.
        public string TouchControls { get; set; } = "auto";
        // The game's own OPTIONS page in the gunship (the DS keeps these in its save; our campaign saves are per file, so
        // they live here): control type 0 stylus right / 1 stylus left / 2 dual right / 3 dual left, aim sensitivity
        // 1-15 (8 = the DS default spot on the slider), look invert. The campaign applies sensitivity and invert to aiming.
        public int GameControlType { get; set; }
        public int GameSensitivity { get; set; } = 8;
        public bool GameLookInvert { get; set; }
        // A PC mouse's own aim speed: OPTIONS > CONTROLS under KEYBOARD & MOUSE and the gunship's OPTIONS (owner
        // 2026-10-05; the Game* pair above is the controller's; the mouse has no look invert). The Source engine's scale
        // (0.022 degrees a count x this, 0.1-20, default 2.5; Config/MouseScale.cs) from owner 2026-10-15: the old
        // 1-15 "MouseSensitivity" (0.14-0.36 degrees a count, default 0.25) was too fast even at 1, so it isn't carried over
        public float MouseSens { get; set; } = MouseScale.Default;
        // The control customizer (OPTIONS > CONTROLS and the gunship's OPTIONS; Config/ControlBinds.cs): function id ->
        // button names, for a controller and for a PC's keyboard and mouse. A function that isn't listed has its default.
        public Dictionary<string, string[]> PadBinds { get; set; } = new();
        public Dictionary<string, string[]> KeyBinds { get; set; } = new();
        // Dev builds: OPTIONS > CONTROLS and the ship's OPTIONS as the game has them (the four stylus / dual mode boxes,
        // kept for dual-screen Thor testing) in place of the customizer buttons. Public builds ignore it.
        public bool ClassicControlsPage { get; set; }
        // The game's OPTIONS > AUDIO page (Frontend/AudioOptions.cs; the DS keeps these in its save): the SFX and music
        // volumes 0-9 and the speaker type (one of SpeakerTypes). The front end and the campaign play at these.
        public static readonly string[] SpeakerTypes = { "stereo", "surround", "headphones" };
        public int SfxVolume { get; set; } = 9;
        public int MusicVolume { get; set; } = 9;
        public string Speakers { get; set; } = "stereo";
        // Every local match against bots is recorded and the last 10 kept in <data>/replays (Replay/ReplayLibrary.cs;
        // docs/REPLAY_MODE.md); off = nothing recorded (owner 2026-10-07: "automatic keep last ten and toggle it off")
        public bool RecordReplays { get; set; } = true;
        // Multiplayer's hunters and arenas: false = vanilla's unlocks (the Adventure and matches open them; HunterLicense),
        // true = every one open (owner 2026-10-07: vanilla by default, a confirmed switch to open everything)
        public bool UnlockEverything { get; set; }
        // UPDATES at launch (owner 2026-10-11): once per run the game asks GitHub in the background and, only if a signed
        // update is out, asks on the main menu whether to install it (FrontendSession.TickLaunchCheck); off = CHECK only
        public bool CheckUpdatesAtLaunch { get; set; } = true;
        public List<string> DisabledMods { get; set; } = new();
        // mod id -> setting id -> the value the player picked (settings a mod declares in its mod.json)
        public Dictionary<string, Dictionary<string, string>> ModSettings { get; set; } = new();

        public string? GetModSetting(string modId, string settingId)
            => ModSettings.TryGetValue(modId, out var values) && values.TryGetValue(settingId, out string? v) ? v : null;

        public void SetModSetting(string modId, string settingId, string value)
        {
            if (!ModSettings.TryGetValue(modId, out var values)) ModSettings[modId] = values = new();
            values[settingId] = value;
        }

        private static readonly JsonSerializerOptions Options = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };

        public static RecompSettings Load(string? path)
        {
            if (path == null || !File.Exists(path)) return new RecompSettings();
            try
            {
                return JsonSerializer.Deserialize<RecompSettings>(File.ReadAllText(path), Options) ?? new RecompSettings();
            }
            catch (Exception)
            {
                return new RecompSettings();
            }
        }

        // Re-read the saved file into THIS instance. The front end's menus keep theirs for the app's whole life while
        // the campaign's pause menu loads and saves its own copy: without this the front end went on with (and its
        // next save wrote back) the values from before the campaign. Every public get/set property, so a new setting
        // needs nothing here (app assemblies aren't trimmed).
        public void ReloadFrom(string? path)
        {
            if (path == null || !File.Exists(path)) return;
            RecompSettings saved = Load(path);
            foreach (System.Reflection.PropertyInfo p in typeof(RecompSettings).GetProperties(
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
            {
                if (p.CanRead && p.CanWrite && p.GetIndexParameters().Length == 0) p.SetValue(this, p.GetValue(saved));
            }
        }

        public void Save(string? path)
        {
            if (path == null) return;
            string? dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, Options));
            File.Move(tmp, path, overwrite: true);
        }
    }
}
