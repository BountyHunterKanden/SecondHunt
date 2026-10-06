using MphRead;
using MphRecomp.Config;

namespace MphRecomp.App;

internal sealed partial class CampaignRenderer
{
    // For a host with a keyboard and mouse: true while the player is in the game itself (the mouse aims, the keys play;
    // dialogs included), false while one of the game's screens has the input (the pause menu, the gun-adjust pause,
    // the map, the ship's screens, a cutscene), where the pointer clicks and the keys navigate.
    public bool InGameplay => _host != null && _error == null && !ShipOpen && !MapOpen && !MovieShowing && !PauseMenuOpen && !_paused;

    // The campaign or match screen the launch args ask for. filesDir: the extracted ROM (MphRead's working directory);
    // dataDir: the player's own files (recomp_settings.json, saves/, mods/, and the dev builds' guns/ and hd/). The host
    // then wires what is its own: StatusSink, ShipSink (the centred text panel), ExitSink, the pause menu's Credits /
    // ShareCrashLog / DevTools, and input. Launch args (Android: intent extras; dev shortcuts in brackets):
    //   room       the room to start in (UNIT2_LAND: the Celestial Archives landing site)
    //   hunter     play as another hunter (MphRead supports it; Features.AlternateHunters1P adds what they need)
    //   save/slot  continue a save file / the file a new game saves to (the front end's files A/B/C = 1/2/3)
    //   gun        [mp1 / none] the first-person gun from <data>/guns/<id>; default: RECOMP SETTINGS' suit
    //   layout     [reference] the first on-device control layout; default: Prime-style
    //   loadout    [all] every weapon, nothing saved (dev "Boss fights"); boss [down] (dev "Escape tests")
    //   shipmenu   [1] start in the in-ship menu; movies [off]; view [wide|ds|prime|panini]
    //   match / arena / bots / botlevel / time / points, or net host|client: a multiplayer match (MatchSession.cs)
    public static CampaignRenderer Create(string filesDir, string dataDir, LaunchArgs args)
    {
        string room = args.Get("room") ?? "UNIT2_LAND";
        var layout = args.Get("layout") == "reference" ? ControlLayout.Reference : ControlLayout.Prime;
        Hunter hunter = Enum.TryParse(args.Get("hunter"), out Hunter h) ? h : Hunter.Samus;
        string gunsRoot = Path.Combine(dataDir, "guns");
        string settingsPath = Path.Combine(dataDir, "recomp_settings.json");
        // gun mp1: the first-person Prime arm cannon from <data>/guns/mp1 in place of MPH's (an explicit arg wins at
        // launch, dev-launcher style); gun none keeps MPH's own. With no arg (or gun settings, the dev launcher's
        // default): Samus takes whatever suit RECOMP SETTINGS has (default MP1PowerSuit, "original" = MPH's own); other
        // hunters keep MP1PowerSuit.
        string? explicitGun = args.Get("gun") is string g && g != "settings" ? g : null;
        string gunId = explicitGun ?? (hunter == Hunter.Samus ? RecompSettings.Load(settingsPath).SamusSuit : "MP1PowerSuit");
        // a public build (Beta 1: imports and arm cannons held, owner 2026-10-04) has no HD gear at all: MPH's own gun and
        // Samus, whatever the settings file or the guns/ and hd/ folders hold
        if (RecompSettings.PublicBuild) gunId = "original";
        string? gunDir = gunId is "none" or "original" ? null : Path.Combine(gunsRoot, gunId);
        // RECOMP SETTINGS -> pause -> resume hot-swaps Samus's suit whenever the player changes it there, even over an
        // explicit arg (cheap: a small file load, no GL work until the next draw); see ReloadGunFromSettings
        var renderer = new CampaignRenderer(filesDir, room, layout, hunter, gunDir, gunsRoot, settingsPath);
        // Start: the recomp's pause menu (resume / recomp settings / mod settings / quit to main menu), the same settings
        // file and mods folder as the front end; L3 + R3 together: the gun-adjust pause
        renderer.PauseMenu = new PauseOverlay(settingsPath, Path.Combine(dataDir, "mods"));
        renderer.SettingsPath = settingsPath; // CampaignAudio.cs: RecompSettings.Music
        renderer.ReloadAimFromSettings();
        renderer.PauseMenu.Resumed += () => { renderer.ReloadGunFromSettings(); renderer.ReloadAimFromSettings(); };
        renderer.PauseMenu.ControlsClosed += renderer.ReloadAimFromSettings; // the customizer from the ship's OPTIONS
        renderer.PauseMenu.QuitToMenu += () => renderer.ExitSink?.Invoke();
        // the gunship between planets (save, star map, travel): CampaignShip.cs
        renderer.ConfigureShip(Path.Combine(dataDir, "saves"), args);
        // loadout all (dev launcher "Boss fights"): a fresh story with every weapon, full ammo and 799 energy, and nothing
        // saved (the ship's save prompt would otherwise write slot 1, the player's file A). Both are dev-only: a public
        // build never honours them, whatever starts it (owner 2026-10-05: "be sure no cheats are enabled")
        if (args.Get("loadout") == "all" && !RecompSettings.PublicBuild)
        {
            renderer.UseTestLoadout();
        }
        // boss down (dev launcher "Escape tests"): the boss is beaten for the player once its intro is over (CampaignBossSkip.cs)
        if (args.Get("boss") == "down" && !RecompSettings.PublicBuild) renderer.UseBossSkip();
        // in-game cutscenes (CampaignMovie.cs); movies off skips them
        ConfigureMovies(args);
        // a multiplayer match with bots instead of the campaign (MatchSession.cs)
        renderer.ConfigureMatch(args);
        // view framing: WIDE (CampaignView.cs; dev only: view wide|ds|prime|panini)
        ConfigureView(args);
        return renderer;
    }
}
