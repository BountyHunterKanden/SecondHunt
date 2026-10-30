using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

// The recomp's own options (not the game's -- those stay in the game's save): what the RECOMP SETTINGS screen under
// Options edits, and which mods are switched off. One small JSON file beside the user's other files; a missing or
// unreadable file falls back to its backups, then the defaults (see Load).
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
        public static readonly string[] PadLayouts = { "auto", "on", "off" };
        // percent: 5s to 200, 10s to 500, 50s to 1000 (thread D 10-24: the old 50-500 list had no 180 and no 600+)
        public static readonly int[] GyroSensitivities = Enumerable.Range(0, 76)
            .Select(i => i <= 35 ? 25 + 5 * i : i <= 65 ? 200 + 10 * (i - 35) : 500 + 50 * (i - 65)).ToArray();
        // tightening threshold, degrees a second (GyroAim.TightenDps; 2 = what it always was)
        public static readonly float[] GyroDeadbands = { 0f, 0.5f, 1f, 1.5f, 2f, 2.5f, 3f, 4f, 5f, 6f, 8f, 10f, 12f, 15f, 20f, 30f };
        public static readonly int[] GyroPercents = Enumerable.Range(0, 21).Select(i => i * 5).ToArray(); // 0-100
        public static readonly string[] GyroAxes = { "yaw", "player", "roll" };
        public static readonly string[] TouchModes = { "auto", "on", "aim only", "off" };
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
        // the Windows host's frame rate (owner 2026-10-18, PC 240+): 0 = the display's (vsync), -1 = unlimited (no vsync,
        // no wait), else a cap in frames a second (no vsync, a precise wait). The game itself steps 60 times a second
        // whatever this is; only the pictures are more or fewer.
        public static readonly int[] FpsLimits = { 0, 30, 60, 120, 144, 165, 240, 360, -1 };
        public int FpsLimit { get; set; }
        // frames drawn between the game's 60 Hz steps show motion (App Campaign/SmoothFrames.cs): the aim turns, the view
        // and what moves carry on by their last step's motion. Drawing only: what the game does is the same either way.
        public bool SmoothFrames { get; set; } = true;
        public int RenderScale { get; set; } = 100;
        public string Music { get; set; } = "original";
        public string MenuLayout { get; set; } = "combined";
        // First Hunt's menus (App FirstHuntMenuRenderer), apart from MPH's: "combined" = the one-screen 16:9 layout,
        // "sidebyside" = the DS's two screens
        public string FirstHuntMenuLayout { get; set; } = "combined";
        // First Hunt's pieces (owner 2026-10-21: "first hunt, by default, to do everything it does on vanilla behavior, but
        // ... have the ability to swap out any first hunt specific piece with a second hunt counter part"; the FIRST HUNT
        // settings tab): false = First Hunt's own, true = Second Hunt's. Menu music: the cart's menu tracks or the menus'
        // theme / picked menu song; training and match music: the cart's level tracks or the picked match song (else MPH's
        // room music); objects: the cart's jump pad models or MPH's (FirstHunt/FirstHuntGame.ApplyOptions)
        public bool FhShMenuMusic { get; set; }
        public bool FhShTrainingMusic { get; set; }
        public bool FhShMatchMusic { get; set; }
        public bool FhShObjects { get; set; }
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
        // The Trooper's type on a Trooper campaign file (SECOND HUNT SETTINGS > GAME > TROOPER TYPE, owner 2026-10-28: a
        // picker like SAMUS SUIT): one of Characters.Trooper.Known; one that isn't imported leaves the first imported one
        // (CampaignRenderer.PlayAs). Changing it in the pause menu swaps him on resume.
        public string TrooperSkin { get; set; } = "MP1PowerTrooper";
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
        // The in-play HUD: PRIME adds, in the campaign, a mini map of the current room (top right) to each hunter's own HUD
        // (MphRead PlayerPrimeHud.cs; owner 2026-10-14; the radar was here too until 2026-10-26); off = the DS's.
        public bool PrimeHud { get; set; } = true;
        // The campaign's Prime radar on its own (owner 2026-10-26: "have its own toggle for campaign and be on by default since
        // vanilla has radar on touch screen for campagin"), every build; PrimeHud above is then the mini map (dev only for now).
        public bool CampaignRadar { get; set; } = true;
        // The radar's pickup dots (vanilla: every pickup in a match, none in the campaign); owner 2026-10-26: a switch to
        // turn them off, on by default.
        public bool RadarPickups { get; set; } = true;
        // HUD + view (thread I 10-23, GitHub #1); all draw-only, so replays and the sim don't see them.
        // HudSway: the HUD trails the aim as on the DS (MphRead Features.HudSway); off = it stays put.
        // HelmetOpacity / VisorOpacity: percent of vanilla's look (helmet frame 100% = solid, visor glass 100% = the DS's
        // half-clear tint); the readouts (meters, ammo, radar) keep full strength. The scan visor's glass is not affected.
        // HorizontalFov: the view's width in degrees as on a 16:9 screen, the number other games' sliders show (vanilla is
        // 110 there: 78 degrees tall); the game keeps the height fixed and widens the sides on wider screens. The zoom, the
        // VIEW looks and third person scale with it. (Replaces the earlier height-in-degrees FieldOfView, 10-26.)
        public bool HudSway { get; set; } = true;
        // ShotIndicator: the red flash showing where a hit came from (owner 2026-10-27). "true" = one flash aimed at the
        // shooter that turns as you do (Features.DamageTrueDirection), "ds" = vanilla's
        // 4-way pick, "off" = no flash (an 8-way value was dropped, owner 10-27: an old "8way" reads as true). Draw only.
        public string ShotIndicator { get; set; } = "true";
        public int HelmetOpacity { get; set; } = 100;
        public int VisorOpacity { get; set; } = 100;
        // ReadoutOpacity: the health, ammo and beam readouts (and the Prime radar + map), percent of full strength. Locked
        // (ReadoutOpacityLocked, off by default so old saves look as before) = they follow VisorOpacity and ReadoutOpacity sleeps.
        public int ReadoutOpacity { get; set; } = 100;
        public bool ReadoutOpacityLocked { get; set; }
        public int HorizontalFov { get; set; } = 110;
        public static readonly int[] OpacitySteps = { 0, 10, 20, 30, 40, 50, 60, 70, 80, 90, 100 };
        public static readonly int[] HorizontalFovs = { 80, 85, 90, 95, 100, 105, 110, 115, 120, 125, 130 };
        // Gyro aim in the campaign (MphRead.Android/CampaignGyro.cs, math in Input/GyroAim.cs), added on top of the right
        // stick: "off", "on" (always) or "zoom" (only while zoomed or in the scan visor). GyroSensitivity: the view turns
        // this percent of the device's own rotation. GyroAxis: which rotation turns -- "yaw" (the screen's own vertical),
        // "player" (the world's vertical, however it's held) or "roll" (steering-wheel tilt).
        public string Gyro { get; set; } = "off";
        public int GyroSensitivity { get; set; } = 150;
        public string GyroAxis { get; set; } = "yaw";
        // a PC controller's turn axis (SecondHunt.Desktop/DesktopGyro.cs; the PC's gyro axis row): "player" by default,
        // GyroWiki's pick for a pad held apart from the screen. A pad's own yaw is about its face, which tilts with the
        // grip (owner 10-30: "yaw is different on pc for that controller than it is my odin"). Android keeps GyroAxis.
        public string PadGyroAxis { get; set; } = "player";
        // gyro tuning (thread D 10-24; GyroAim.cs has the math and sources). Each filter acts only on slow motion and is off
        // or at the old fixed value by default, so a save from before plays exactly as it did.
        // GyroPitchSensitivity: looking up/down, percent; 0 = the same as GyroSensitivity (which is turning)
        public int GyroPitchSensitivity { get; set; }
        public bool GyroInvertPitch { get; set; }
        public bool GyroInvertYaw { get; set; }
        // deadband: rates under this (degrees a second) fade out smoothly (tightening, not a hard cut)
        public float GyroDeadband { get; set; } = 2f;
        // smoothing percent: motion slower than 0.3 x this (deg/s) is averaged over 1/8 s, faster is untouched. 0 = off.
        // Default 20 (6 deg/s, 10-24): steadies hand shake about as much as Dusklight's default, but where its smoothing
        // delays every turn, ours never touches a real turn
        public int GyroSmoothing { get; set; } = 20;
        // acceleration percent: slow motion turns this much less than the speed set, rising to the full speed by 75 deg/s
        public int GyroAccel { get; set; }
        // the Imperialist's gyro speed (finRatman 10-26, Input/ZoomAim.cs): paired (default) = zoomed gyro aim is the gyro
        // speed above with the zoom's own correction; unpaired = GyroZoomSensitivity (percent) takes its place while zoomed
        public bool GyroZoomPaired { get; set; } = true;
        public int GyroZoomSensitivity { get; set; } = 150;
        // PC pads' NINTENDO LAYOUT (thread D 10-24, D6c; SecondHunt.Desktop DesktopInput): "on" = each face button does
        // what its printed letter says on a Nintendo pad (A on the right: A/B and X/Y swapped from the Xbox positions),
        // "off" = by position (the bottom button is A), "auto" = on for a pad whose bottom button is labelled B (Switch
        // Pro, Joy-Cons, GameCube; SDL3 says, or GLFW's pad name).
        public string PadLayout { get; set; } = "auto";
        // On-screen touch controls in the campaign and in matches (MphRead.Android/MatchTouchOverlay.cs): "auto" shows
        // them only while no game controller is connected, "on" always, "aim only" always but with no buttons or move stick (the
        // whole screen aims, for playing on a controller and aiming by touch), "off" never.
        public string TouchControls { get; set; } = "auto";
        // How the touch controls' right-hand finger aims (owner 2026-10-16): "stick" = a floating virtual stick, "stylus" =
        // the DS's stylus aim (the finger's drag turns the view by the game's own rule; CampaignRenderer.StylusAimDelta).
        public string TouchAim { get; set; } = "stick";
        // the stylus aim's speed (Config/TouchScale.cs: 1.0 = 10 degrees a cm of drag, 0.01-18, default 2). 0 = a save from
        // before it (thread D 10-24): Load maps GameSensitivity onto the new range.
        public float TouchSens { get; set; }
        // the Imperialist's touch speed (owner 2026-10-26, Input/ZoomAim.cs): paired (default) = zoomed touch aim as before
        // (the touch aim speed, or the controller's imperialist sensitivity while that is set apart); unpaired =
        // TouchZoomSens (TouchScale's units) while zoomed, for the stylus and the touch stick alike
        public bool TouchZoomPaired { get; set; } = true;
        public float TouchZoomSens { get; set; } = TouchScale.Default;
        // the stylus aim's up/down speed as a percent of its left/right (owner 2026-10-26, Input/StylusAim.PitchPercents);
        // 0 = the DS's own ratio (about 51%), as before
        public int TouchPitchPercent { get; set; }
        // aim while firing (thread D 10-24): the thumb holding the on-screen FIRE button (to charge) aims like a stick as it
        // slides; a tap fires as before (MatchTouchOverlay)
        public bool TouchFireAim { get; set; } = true;
        // The game's own OPTIONS page in the gunship (the DS keeps these in its save; our campaign saves are per file, so
        // they live here): control type 0 stylus right / 1 stylus left / 2 dual right / 3 dual left, aim sensitivity
        // 1-15 (8 = the DS default spot on the slider), look invert. The campaign applies sensitivity and invert to aiming.
        public int GameControlType { get; set; }
        public int GameSensitivity { get; set; } = 8;
        public bool GameLookInvert { get; set; }
        // Look invert per control scheme, up/down (Y) and left/right (X) apart (owner 2026-10-26): GameLookInvert above is
        // the controller's Y (so old saves keep theirs), GameLookInvertX its X; the mouse's pair; the touch controls' pair
        // (the virtual stick and the stylus). Gyro has its own (GyroInvertPitch / GyroInvertYaw). A save from before these
        // had the touch controls follow the controller's invert: Load carries that into TouchInvertY.
        public bool GameLookInvertX { get; set; }
        public bool MouseInvertY { get; set; }
        public bool MouseInvertX { get; set; }
        public bool TouchInvertY { get; set; }
        public bool TouchInvertX { get; set; }
        // Y SPEED (owner 2026-10-26: "seperate y and x sensitivity for all modes"): the controller stick's and the mouse's
        // up/down speed as a percent of their left/right (Input/StylusAim.YPercents); 100 = the same, as before
        public int AimPitchPercent { get; set; } = 100;
        public int MousePitchPercent { get; set; } = 100;
        // A PC mouse's own aim speed: OPTIONS > CONTROLS under KEYBOARD & MOUSE and the gunship's OPTIONS (owner
        // 2026-10-05; the Game* pair above is the controller's). The Source engine's scale
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
        // SWAP DRONE DIALS (2026-10-19): the Scout Drone's two wait dials trade sides on screen (BURST left, LASER
        // right); the buttons don't change (MphRead PlayerDroneHud.cs HostDroneDialsSwapped)
        public bool DroneDialsSwapped { get; set; }
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
        // STREAMING (owner 2026-10-28: "toggle off their ip address, username, and friend code, hiding them anywhere they
        // appear in the game"): on = shown as usual, off = masked on screen (Config/StreamerPrivacy.cs). Draw only: the
        // other players, the server and the logs still get the real ones.
        public bool ShowAddress { get; set; } = true;
        public bool ShowName { get; set; } = true;
        public bool ShowFriendCode { get; set; } = true;
        // Every local match against bots is recorded and the last 10 kept in <data>/replays (Replay/ReplayLibrary.cs;
        // docs/REPLAY_MODE.md); off = nothing recorded (owner 2026-10-07: "automatic keep last ten and toggle it off")
        public bool RecordReplays { get; set; } = true;
        // It goes OFF only through the menu's confirmed switch (owner 10-27: it kept turning itself off and a bot match
        // needed for the bot work was lost): any other save keeps the file's ON. Every change to it, by a save or by
        // something writing the file from outside (a session pushing settings), goes in settings_changes.log beside the
        // file, a save's with its caller.
        private bool _recordOffConfirmed;
        public void TurnOffRecordReplays()
        {
            RecordReplays = false;
            _recordOffConfirmed = true;
        }
        private static bool? _lastLoadedRecord; // the last RecordReplays read or written, to catch the file changing under us
        // PREPARE REPLAYS (thread B, fast seeking): when a recorded match's seek keyframes are made, in another process at a
        // low priority. "during" = as the match is played (ready when it ends), "after" = once it is saved, "off" = only
        // when the replay is first watched (App Campaign/MatchReplay.cs StartPrepare). The default is per platform (perf runs
        // 2026-10-26): the Odin plays the same with it on, a PC's frame times got worse, so the PC prepares after the match
        public string PrepareReplays { get; set; } = PrepareReplaysDefault;
        public static string PrepareReplaysDefault => OperatingSystem.IsAndroid() ? "during" : "after";
        // Multiplayer's hunters and arenas: false = vanilla's unlocks (the Adventure and matches open them; HunterLicense),
        // true = every one open (owner 2026-10-07: vanilla by default, a confirmed switch to open everything)
        public bool UnlockEverything { get; set; }
        // UPDATES at launch (owner 2026-10-11): once per run the game asks GitHub in the background and, only if a signed
        // update is out, asks on the main menu whether to install it (FrontendSession.TickLaunchCheck); off = CHECK only
        public bool CheckUpdatesAtLaunch { get; set; } = true;
        // STARTUP (owner 2026-10-29): skip the white NINTENDO/ACTIMAGINE screen and the ESRB notice at launch; with
        // SkipIntro also on, the NINTENDO PRESENTS logos and the intro movie too, straight to TOUCH TO START
        // (FrontendSession.Start). SkipIntro only counts with SkipBootLogos on (its row only shows then)
        public bool SkipBootLogos { get; set; }
        public bool SkipIntro { get; set; }
        // owner 2026-10-26: making a new file in the campaign's file select first offers to fill it from a vanilla DS save
        // (an emulator's or a dumped cart's .sav: Campaign/CartSave.cs); off = new files start as before
        public bool ImportSavesOnNewFile { get; set; }
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

        // Owner 10-28: "i just dont want settings lost or overwritten unexpectedly ever". A settings file that is there
        // but can't be read (10-28: an adb-written file the app had no permission to read) or isn't valid JSON is never
        // saved over: it's moved aside as recomp_settings.json.unreadable-<time> and the last good copy is used instead.
        // recomp_settings.json.bak mirrors every good save; settings_backups/ keeps the first copy of each day and the
        // copy from before any save that changes many settings at once. Each of these goes in settings_changes.log.
        private const string BackupFolder = "settings_backups";
        private const int BigChange = 8; // this many settings changed by one save = a big change worth a backup
        // couldn't read the file and couldn't move it aside: this instance must never be saved over it
        private bool _unreadableFile;
        // came from a backup or the defaults because the file couldn't be read (ReloadFrom keeps its own values then)
        private bool _fellBack;

        public static RecompSettings Load(string? path)
        {
            if (path == null) return Carried(new RecompSettings());
            if (!File.Exists(path))
            {
                // no file: a fresh install, or the file went missing; the last good save's copy if there is one
                RecompSettings? restored = LoadBackup(path, "the settings file is missing");
                return restored ?? Carried(new RecompSettings());
            }
            if (TryRead(path, out RecompSettings? s, out string json, out string error))
            {
                if (_lastLoadedRecord != s!.RecordReplays && (_lastLoadedRecord != null || !s.RecordReplays))
                {
                    ChangeLog(path, $"loaded RecordReplays={s.RecordReplays} (last seen {(_lastLoadedRecord?.ToString() ?? "none: app start")})");
                }
                _lastLoadedRecord = s.RecordReplays;
                return Migrated(s, json);
            }
            bool moved = SetAside(path, error);
            RecompSettings result = LoadBackup(path, "the settings file was unreadable") ?? Carried(new RecompSettings());
            result._fellBack = true;
            result._unreadableFile = !moved;
            return result;
        }

        // a save from before a setting existed gets what that build did, so an update changes nothing the player set
        private static RecompSettings Migrated(RecompSettings s, string json)
        {
            // a save from before gyro smoothing (0.3.8 and older) had none: keep it off so an update changes nothing
            // the player set (owner 10-26). Fresh installs get the default.
            if (json.IndexOf("\"GyroSmoothing\"", StringComparison.OrdinalIgnoreCase) < 0) s.GyroSmoothing = 0;
            // the touch controls used the controller's look invert before they had their own (owner 10-26)
            if (json.IndexOf("\"TouchInvertY\"", StringComparison.OrdinalIgnoreCase) < 0) s.TouchInvertY = s.GameLookInvert;
            return Carried(s);
        }

        private static bool TryRead(string path, out RecompSettings? s, out string json, out string error)
        {
            s = null;
            json = "";
            error = "";
            try
            {
                json = File.ReadAllText(path);
                s = JsonSerializer.Deserialize<RecompSettings>(json, Options);
                if (s != null) return true;
                error = "empty";
            }
            catch (Exception e)
            {
                error = e.GetType().Name + ": " + e.Message;
            }
            return false;
        }

        // move an unreadable settings file out of the way (renaming needs only the folder, which is the app's own)
        private static bool SetAside(string path, string error)
        {
            string aside = path + ".unreadable-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
            try
            {
                File.Move(path, aside);
                ChangeLog(path, $"settings file unreadable ({error}): kept as {Path.GetFileName(aside)}", stack: true);
                return true;
            }
            catch (Exception e)
            {
                ChangeLog(path, $"settings file unreadable ({error}) and couldn't be moved aside ({e.Message}): not saving over it", stack: true);
                return false;
            }
        }

        // the newest readable copy: the .bak mirror, then settings_backups/ newest first
        private static RecompSettings? LoadBackup(string path, string why)
        {
            List<string> copies = new() { path + ".bak" };
            try
            {
                string folder = Path.Combine(Path.GetDirectoryName(path) ?? "", BackupFolder);
                if (Directory.Exists(folder))
                {
                    copies.AddRange(new DirectoryInfo(folder).GetFiles("*.json")
                        .OrderByDescending(f => f.LastWriteTimeUtc).Select(f => f.FullName));
                }
            }
            catch (Exception)
            {
            }
            foreach (string copy in copies)
            {
                if (File.Exists(copy) && TryRead(copy, out RecompSettings? s, out string json, out _))
                {
                    ChangeLog(path, $"{why}: loaded the settings from {Path.GetFileName(copy)}");
                    _lastLoadedRecord = s!.RecordReplays;
                    return Migrated(s, json);
                }
            }
            if (copies.Count > 1 || File.Exists(copies[0])) ChangeLog(path, $"{why}: no readable backup, using the defaults");
            return null;
        }

        // a value a newer build added, filled from the older settings it replaces
        private static RecompSettings Carried(RecompSettings s)
        {
            if (!(s.TouchSens > 0)) s.TouchSens = TouchScale.FromLevel(s.GameSensitivity);
            StreamerPrivacy.Apply(s); // (every load: the process-wide switches follow the file)
            return s;
        }

        // Re-read the saved file into THIS instance. The front end's menus keep theirs for the app's whole life while
        // the campaign's pause menu loads and saves its own copy: without this the front end went on with (and its
        // next save wrote back) the values from before the campaign. Every public get/set property, so a new setting
        // needs nothing here (app assemblies aren't trimmed).
        public void ReloadFrom(string? path)
        {
            if (path == null || !File.Exists(path)) return;
            RecompSettings saved = Load(path);
            if (saved._fellBack) return; // unreadable: this instance's values are newer than any backup
            foreach (System.Reflection.PropertyInfo p in typeof(RecompSettings).GetProperties(
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
            {
                if (p.CanRead && p.CanWrite && p.GetIndexParameters().Length == 0) p.SetValue(this, p.GetValue(saved));
            }
        }

        public void Save(string? path)
        {
            if (path == null) return;
            StreamerPrivacy.Apply(this);
            string? dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            if (_unreadableFile)
            {
                ChangeLog(path, "save skipped: the settings file couldn't be read or moved aside", stack: true);
                return;
            }
            string? diskJson = null;
            if (File.Exists(path))
            {
                if (TryRead(path, out _, out string json, out string error)) diskJson = json;
                else if (!SetAside(path, error)) return;
            }
            bool? onDisk = OnDiskRecordReplays(diskJson);
            if (onDisk == true && !RecordReplays && !_recordOffConfirmed)
            {
                RecordReplays = true;
                ChangeLog(path, "an unconfirmed RecordReplays=false save was kept ON", stack: true);
            }
            else if (onDisk != null && onDisk != RecordReplays)
            {
                ChangeLog(path, $"saved RecordReplays {onDisk} -> {RecordReplays}", stack: true);
            }
            _recordOffConfirmed = false;
            string newJson = JsonSerializer.Serialize(this, Options);
            if (diskJson != null && diskJson != newJson) KeepHistory(path, diskJson, newJson);
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, newJson);
            File.Move(tmp, path, overwrite: true);
            try
            {
                File.WriteAllText(path + ".bak", newJson);
            }
            catch (Exception)
            {
            }
            _lastLoadedRecord = RecordReplays;
        }

        // before a save replaces a good file: the day's first copy, and a copy when one save changes many settings
        private static void KeepHistory(string path, string diskJson, string newJson)
        {
            try
            {
                string folder = Path.Combine(Path.GetDirectoryName(path) ?? "", BackupFolder);
                Directory.CreateDirectory(folder);
                string daily = Path.Combine(folder, $"recomp_settings-{DateTime.Now:yyyy-MM-dd}.json");
                if (!File.Exists(daily)) File.WriteAllText(daily, diskJson);
                List<string> changed = ChangedKeys(diskJson, newJson);
                if (changed.Count >= BigChange)
                {
                    string before = Path.Combine(folder, $"before-big-change-{DateTime.Now:yyyy-MM-dd-HHmmss}.json");
                    File.WriteAllText(before, diskJson);
                    ChangeLog(path, $"one save changed {changed.Count} settings ({string.Join(", ", changed.Take(12))}"
                        + $"{(changed.Count > 12 ? ", ..." : "")}): the old file is kept as {BackupFolder}/{Path.GetFileName(before)}", stack: true);
                }
                // keep the newest 20 (about 2 KB each)
                foreach (FileInfo old in new DirectoryInfo(folder).GetFiles("*.json").OrderByDescending(f => f.LastWriteTimeUtc).Skip(20))
                {
                    old.Delete();
                }
            }
            catch (Exception)
            {
            }
        }

        // top-level settings in both files whose values differ
        private static List<string> ChangedKeys(string oldJson, string newJson)
        {
            List<string> changed = new();
            try
            {
                using JsonDocument a = JsonDocument.Parse(oldJson);
                using JsonDocument b = JsonDocument.Parse(newJson);
                Dictionary<string, string> was = new(StringComparer.OrdinalIgnoreCase);
                foreach (JsonProperty p in a.RootElement.EnumerateObject()) was[p.Name] = p.Value.GetRawText();
                foreach (JsonProperty p in b.RootElement.EnumerateObject())
                {
                    if (was.TryGetValue(p.Name, out string? v) && v != p.Value.GetRawText()) changed.Add(p.Name);
                }
            }
            catch (Exception)
            {
            }
            return changed;
        }

        // the file's RecordReplays (true for a file from before the setting), null = no file
        private static bool? OnDiskRecordReplays(string? json)
        {
            if (json == null) return null;
            try
            {
                using JsonDocument doc = JsonDocument.Parse(json);
                foreach (JsonProperty p in doc.RootElement.EnumerateObject())
                {
                    if (string.Equals(p.Name, nameof(RecordReplays), StringComparison.OrdinalIgnoreCase)) return p.Value.ValueKind == JsonValueKind.True;
                }
                return true;
            }
            catch (Exception)
            {
                return null;
            }
        }

        // settings_changes.log beside the settings file: when, what, and (stack) the caller's own frames
        public static void ChangeLog(string? settingsPath, string what, bool stack = false)
        {
            try
            {
                string? dir = Path.GetDirectoryName(settingsPath);
                if (string.IsNullOrEmpty(dir)) return;
                string log = Path.Combine(dir, "settings_changes.log");
                if (File.Exists(log) && new FileInfo(log).Length > 256 * 1024) File.Delete(log);
                string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {what}\n";
                if (stack)
                {
                    IEnumerable<string> frames = Environment.StackTrace.Split('\n').Select(f => f.Trim())
                        .Where(f => f.StartsWith("at Mph") && !f.Contains("RecompSettings.")).Take(10);
                    line += string.Concat(frames.Select(f => "    " + f + "\n"));
                }
                File.AppendAllText(log, line);
            }
            catch (Exception)
            {
            }
        }
    }
}
