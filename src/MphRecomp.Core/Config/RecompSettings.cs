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
        public static readonly string[] TouchAimModes = { "stick", "stylus" };

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
        // First Hunt's menus (App FirstHuntMenuRenderer), apart from MPH's: "combined" = the one-screen 16:9 layout,
        // "sidebyside" = the DS's two screens
        public string FirstHuntMenuLayout { get; set; } = "combined";
        public bool SmoothMenus { get; set; }
        // The menus' backdrop: Samus as an HD render of the user's own imported suit (Frontend/MenuSamus.cs: Echoes' Varia
        // first, else another Prime suit; rendered once on the device) or false = the DS art (sourceimages/bg main1/main2)
        public bool MenuArtHd { get; set; } = true;
        // ... and which suit, picked in the "menu art" chooser (owner 2026-10-13): "" = the recipe's order, "shuffle" = another
        // imported suit each time the app starts, else a suit's id (one no longer imported falls back to the order)
        public string MenuArtSuit { get; set; } = "";
        public const string MenuArtShuffle = "shuffle";
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
        // Multiplayer matches' camera (SECOND HUNT SETTINGS > GAME > MULTIPLAYER, owner 2026-10-17: separate from the
        // campaign's): bot and LAN matches follow it; online matches are always first person (CampaignRenderer.SetThirdPerson).
        public bool MatchThirdPerson { get; set; }
        // The pause map (Select; Core Frontend/PauseMap.cs). MapDetailed: rooms drawn from their collision meshes instead of
        // the game's map models (Y in the map switches it too). MapPrime: Metroid Prime map behaviour with the Hunters look
        // (only rooms and connectors Samus has been in, free moving, the room in the middle selected); off = vanilla's.
        public bool MapDetailed { get; set; }
        public bool MapPrime { get; set; } = true;
        // The in-play HUD: PRIME adds a Metroid Prime style radar (top left, every hunter) and, in the campaign, a mini map of
        // the current room (top right) to each hunter's own HUD (MphRead PlayerPrimeHud.cs; owner 2026-10-14); off = the DS's.
        public bool PrimeHud { get; set; } = true;
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
        // How the touch controls' right-hand finger aims (owner 2026-10-16): "stick" = a floating virtual stick, "stylus" =
        // the DS's stylus aim (the finger's drag turns the view by the game's own rule; CampaignRenderer.StylusAimDelta).
        public string TouchAim { get; set; } = "stick";
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
        // The zoom's aim speed (the Imperialist; owner 2026-10-16, Input/ZoomAim.cs). Paired (default): zoomed aim is the
        // aim sensitivity above slowed by the game's own zoom factor, so any setting gets the same, finer zoomed feel.
        // Unpaired: ZoomSensitivity (the controller, 1-15 like GameSensitivity) and ZoomMouseSens (the mouse, MouseScale's
        // range) take the aim sensitivity's place while zoomed.
        public bool ZoomSensPaired { get; set; } = true;
        public int ZoomSensitivity { get; set; } = 8;
        public float ZoomMouseSens { get; set; } = MouseScale.Default;
        // ZOOM STEP (owner 2026-10-17): vanilla's two zooms, the second (the target zoom) only when asked: the zoom press goes
        // first zoom -> target zoom (when one is ready) -> out, zoom in / out step while zoomed, no auto zoom onto targets
        // (MphRead PlayerEntity.HostZoomButtons). ZoomReadyIcon: the reticle faint in the first zoom, solid once the target zoom is ready
        public bool ZoomStep { get; set; }
        public bool ZoomReadyIcon { get; set; } = true;
        // TRY CONTROLS' practice bots (owner 2026-10-17): false = idle targets that never die, true = bots that fight back
        public bool TryActiveBots { get; set; }
        // The control customizer (OPTIONS > CONTROLS and the gunship's OPTIONS; Config/ControlBinds.cs): function id ->
        // button names, for a controller and for a PC's keyboard and mouse. A function that isn't listed has its default.
        public Dictionary<string, string[]> PadBinds { get; set; } = new();
        public Dictionary<string, string[]> KeyBinds { get; set; } = new();
        // the two lists' layout (ControlBinds.Tidy): 0 = the old layout (scan / zoom, zoom / scan, bombs / scan and map /
        // scoreboard were one function each, up to four buttons), 2 = one function a row, two buttons each (2026-10-17)
        public int BindsVersion { get; set; }
        // the controller's CHAT button (online quick-message wheel): false = hold it, true = press to open, press to close
        // (owner 2026-10-11; the customizer's "chat mode" row under CHAT)
        public bool ChatToggle { get; set; }
        // Dev builds: OPTIONS > CONTROLS and the ship's OPTIONS as the game has them (the four stylus / dual mode boxes,
        // kept for dual-screen Thor testing) in place of the customizer buttons. Public builds ignore it.
        public bool ClassicControlsPage { get; set; }
        // The game's OPTIONS > AUDIO page (Frontend/AudioOptions.cs; the DS keeps these in its save): the SFX and music
        // volumes 0-9 and the speaker type (one of SpeakerTypes). The front end and the campaign play at these.
        public static readonly string[] SpeakerTypes = { "stereo", "surround", "headphones" };
        public int SfxVolume { get; set; } = 9;
        public int MusicVolume { get; set; } = 9;
        public string Speakers { get; set; } = "stereo";
        // The Prime songs imported from the player's own discs (docs/PRIME_MUSIC.md, owner 2026-10-14), each player's own:
        // MatchMusic "" = the arena's own music, "shuffle" = another shown song each match, else a song's id
        // ("tracks:MP1PhendranaDrifts"); MenuMusic the same for the menus' theme ("" = the game's, "shuffle" = another each
        // start); MusicPicker = a small MUSIC row on the match rules pages, like the arena picker; HiddenSongs = song ids
        // left out of the lists and the shuffles.
        public string MatchMusic { get; set; } = "";
        public string MenuMusic { get; set; } = "";
        public bool MusicPicker { get; set; }
        public List<string> HiddenSongs { get; set; } = new();
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
