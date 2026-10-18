using System.Collections.Generic;

// CONTROLS' hint texts (owner 2026-10-17: "instead of having the hint text explain all options, it should
// just explain the one that is selected"), one table so the owner's edits land in one place: a key per row, ".<option>"
// for a row with choices (the hint of the option it's on), ".keys" for the keyboard and mouse's own wording. The
// buttons' hints are their functions' (ControlBinds.Functions). Lower case as the menus write them (the font's small
// capitals).
namespace MphRecomp.Frontend
{
    public static class ControlHints
    {
        public static readonly IReadOnlyDictionary<string, string> All = new Dictionary<string, string>
        {
            ["controlsfor.controller"] = "settings and buttons for the controller.",
            ["controlsfor.keys"] = "settings and keys for the keyboard and mouse.",
            ["sensitivity"] = "how quickly you turn and aim.",
            ["impsensitivity"] = "how quickly you turn and aim zoomed in with the imperialist.",
            ["autoimp.off"] = "normal aim and imperialist sensitivity are set independently. imperialist has finer aiming than "
                + "normal aiming at default values.",
            ["autoimp.on"] = "both sensitivities are paired. imperialist is always finer than normal aim.",
            ["lookinvert.off"] = "push the right stick up to look up.",
            ["lookinvert.on"] = "push the right stick up to look down.",
            ["zoomstep.off"] = "one press zooms in, and the game automatically zooms in further when over targets.",
            ["zoomstep.on"] = "toggle the second zoom manually when the reticle is lit up. uses same zoom button or assigned "
                + "button. zooms back out to first step when target is lost.",
            ["zoomstep.on.keys"] = "toggle the second zoom manually when the reticle is lit up. uses same zoom button or assigned "
                + "key. zooms back out to first step when target is lost.",
            ["zoomicon.on"] = "while zoomed, the reticle is faint, and turns solid when the target zoom is ready.",
            ["zoomicon.off"] = "the reticle stays solid. the target zoom still works when a target is in sight.",
            ["trycontrols"] = "pick a hunter and play in alinos gateway's sniping perch with practice bots, every weapon and "
                + "endless ammo. press start to come back here.",
            ["practicebots.idle"] = "the bots stand still and never die.",
            ["practicebots.active"] = "the bots move and fight back.",
            ["scanzoomnote"] = "zoom and scan can be set to the same button without conflict.",
            ["chatmode.hold"] = "hold chat, point the right stick at a message, let go to send.",
            ["chatmode.toggle"] = "press chat to open the quick messages, press again to send (or to type, with none picked).",
            ["reset"] = "puts every button on this page back to default.",
            ["reset.keys"] = "puts every key and mouse button on this page back to default.",
            // added to a hint: a sensitivity's RESET (controller), a button's CLEAR cell, a button waiting for its new one
            ["resetline"] = "y: reset to default.",
            ["clearcell"] = "clear all buttons.",
            ["clearcell.keys"] = "clear all keys.",
            ["waiting"] = "hold the new button, or two for a combo, then let go. wait to keep it as it is.",
            ["waiting.keys"] = "hold the new key, or two for a combo, then let go. esc keeps it as it is.",

            // SECOND HUNT SETTINGS (owner 2026-10-17: "reorganize the whole second hunt settings menu in the same way/style",
            // RecompMenus.SettingsPages.cs): each row's "<label>.<value>" (the label without spaces: "menu
            // screens" = "menuscreens"), or "<label>" when its values don't change the hint; a row with neither keeps its own
            ["menuscreens.one"] = "both ds screens' menus together on one screen.",
            ["menuscreens.two"] = "the two ds screens' menus side by side.",
            ["menuart.original"] = "the ds picture of samus behind the menus.",
            ["menuart.shuffle"] = "another of your imported suits behind the menus each time the game starts.",
            ["menuart"] = "samus in this imported suit behind the menus.",
            ["smoothmenus.on"] = "the menu art drawn clean and sharp.",
            ["smoothmenus.off"] = "the menu art filtered softer.",
            ["lineart.smooth"] = "the hud's line art redrawn as clean lines and shapes, traced from your rom.",
            ["lineart.pixel"] = "the hud's line art as the ds's pixels.",
            ["musicquality.orig"] = "the ds's music as it was. changes live.",
            ["musicquality.orig+fix"] = "the ds's music without its clicks or clipping. changes live.",
            ["musicquality.hq+fix"] = "smoother music, without clicks or clipping. changes live.",
            ["musicquality.hq+tone"] = "smoother music without clicks or clipping, with the ds's treble. changes live.",
            ["sfxvolume"] = "how loud the sound effects are, 0 to 9. heard at once.",
            ["musicvolume"] = "how loud the music is, 0 to 9. heard at once.",
            ["musicpicker.on"] = "a small music row under each match's rules, to pick that match's song as you set it up.",
            ["musicpicker.off"] = "no music row in the match rules.",
            ["camera.first"] = "the campaign in the game's own first person view.",
            ["camera.third"] = "in the campaign the camera follows samus from behind, wearing her suit's body.",
            ["matchcamera.first"] = "multiplayer matches in the game's own first person view. online matches are always first person.",
            ["matchcamera.third"] = "in bot and lan matches the camera follows your hunter from behind. online matches are always "
                + "first person.",
            ["maplook.map"] = "the pause map draws the game's own map models. y switches it in the map.",
            ["maplook.detailed"] = "the pause map draws every room from its real shape. y switches it in the map.",
            ["mapmode.prime"] = "only rooms and corridors you've been in. move freely, the d-pad hops rooms.",
            ["mapmode.hunters"] = "the game's own pause map.",
            ["hud.prime"] = "in the campaign: a radar plus a mini map of the room you're in. in multiplayer the radar comes with show hunters on radar.",
            ["hud.ds"] = "in the campaign: the game's own hud. in multiplayer the radar comes with show hunters on radar.",
            ["unlocks.vanilla"] = "multiplayer hunters and arenas are earned as in the original game.",
            ["unlocks.all"] = "every multiplayer hunter and arena is open.",
            ["recordreplays.on"] = "every match against bots is recorded, and the last ten are kept.",
            ["recordreplays.off"] = "no matches are recorded.",
            ["gyroaim.off"] = "turning the device does nothing.",
            ["gyroaim.on"] = "turn the device to fine-tune your aim. the right stick still turns as usual.",
            ["gyroaim.zoom"] = "turn the device to fine-tune your aim, only while zoomed or scanning.",
            ["gyrospeed"] = "how far the view turns for each turn of the device. 100% turns it the same angle.",
            ["gyroaxis.yaw"] = "turning round the screen's own up axis steers.",
            ["gyroaxis.player"] = "turning round true up steers, however you hold the device.",
            ["gyroaxis.roll"] = "tilt the device to steer.",
            ["touchcontrols.auto"] = "on-screen controls in the campaign and in matches, only while no controller is connected.",
            ["touchcontrols.on"] = "on-screen controls in the campaign and in matches, always.",
            ["touchcontrols.off"] = "no on-screen controls.",
            ["touchaim.stylus"] = "drag the right-hand finger to turn the view, as with the ds stylus.",
            ["touchaim.stick"] = "the right-hand finger works a floating stick.",
            ["updatealerts.on"] = "when the game starts, it asks github.com for a newer second hunt and offers it on the main menu.",
            ["updatealerts.off"] = "updates are looked for only when you press check for updates.",
            ["controlspage.new"] = "options > controls and the ship's options open the control customizer.",
            ["controlspage.classic"] = "options > controls and the ship's options open the ds's stylus and dual mode boxes.",
            ["developer.on"] = "the dev tools button shows below.",
            ["developer.off"] = "no dev tools button.",
            ["statustext.on"] = "fps, timings and the scoreboard at the upper left in gameplay. l3 cycles it.",
            ["statustext.off"] = "no status text in gameplay.",
        };

        // the hint for `key` (with ".keys" for the keyboard and mouse when it has its own)
        public static string For(string key, bool keys = false) =>
            keys && All.TryGetValue(key + ".keys", out string? k) ? k : All.TryGetValue(key, out string? s) ? s : "";
    }
}
