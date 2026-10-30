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
            // look invert, up/down and left/right apart, per control scheme (owner 2026-10-26)
            ["lookinvert.off"] = "push the right stick up to look up.",
            ["lookinvert.on"] = "push the right stick up to look down.",
            ["lookinvertx.off"] = "push the right stick right to turn right.",
            ["lookinvertx.on"] = "push the right stick right to turn left.",
            ["lookinvert.off.keys"] = "move the mouse forward to look up.",
            ["lookinvert.on.keys"] = "move the mouse forward to look down.",
            ["yspeed"] = "how fast the right stick looks up and down compared to left and right.",
            ["yspeed.keys"] = "how fast the mouse looks up and down compared to left and right.",
            ["yspeed.100%"] = "right stick looks up and down at the same speed as left and right.",
            ["yspeed.100%.keys"] = "mouse looks up and down at the same speed as left and right.",
            ["lookinvertx.off.keys"] = "move the mouse right to turn right.",
            ["lookinvertx.on.keys"] = "move the mouse right to turn left.",
            ["touchinvert.off"] = "drag or push the aim finger up to look up.",
            ["touchinvert.on"] = "drag or push the aim finger up to look down.",
            ["touchinvertx.off"] = "drag or push the aim finger right to turn right.",
            ["touchinvertx.on"] = "drag or push the aim finger right to turn left.",
            ["zoomstep.off"] = "one press zooms in, and the game automatically zooms in further when over targets.",
            ["zoomstep.on"] = "toggle the second zoom manually when the reticle is lit up. uses same zoom button or assigned "
                + "button. zooms back out to first step when target is lost.",
            ["zoomstep.on.keys"] = "toggle the second zoom manually when the reticle is lit up. uses same zoom button or assigned "
                + "key. zooms back out to first step when target is lost.",
            ["zoomicon.on"] = "while zoomed, the reticle is faint, and turns solid when the target zoom is ready.",
            ["zoomicon.off"] = "the reticle stays solid. the target zoom still works when a target is in sight.",
            ["dronedials.on"] = "drone's burst cooldown on the left, laser cooldown on the right. buttons stay the same.",
            ["dronedials.off"] = "drone's laser cooldown on the left, burst cooldown on the right. buttons stay the same.",
            ["trycontrols"] = "pick a hunter and test your controls in alinos gateway's sniping perch with practice bots, all weapons and infinite ammo. press start to come back.",
            ["practicebots.idle"] = "bots stand still and can't die.",
            ["practicebots.active"] = "bots move and fight back.",
            ["scanzoomnote"] = "zoom and scan can be set to the same button without conflict.",
            ["chatmode.hold"] = "hold chat, point the right stick at a message, let go to send.",
            ["chatmode.toggle"] = "press chat to open quick messages, press again to send. press with nothing picked to type.",
            ["reset"] = "puts every button on this page back to default.",
            ["reset.keys"] = "puts every key and mouse button on this page back to default.",
            // CONTROLS' sub-tabs (thread D 10-24, M8): the strip over the rows and each tab's own reset
            ["subtabs"] = "left and right, l2 / r2, or tap to switch controls tabs.",
            ["subtabs.keys"] = "left and right, z / c, or click to switch controls tabs.",
            // SETTINGS' GAME sub-tabs (owner 2026-10-28)
            ["settingsubtabs"] = "left and right, l2 / r2, or tap to switch between campaign and multiplayer settings.",
            ["appsubtabs"] = "left and right, l2 / r2, or tap to switch between startup, updates and help.",
            ["settingsubtabs.keys"] = "left and right, z / c, or click to switch between campaign and multiplayer settings.",
            ["resetbuttons"] = "puts every button, chat mode and drone dials back to default.",
            ["resetbuttons.keys"] = "puts every key and mouse button, chat mode and drone dials back to default.",
            ["resetaim"] = "puts sensitivity, imperialist sensitivity, invert and zoom settings back to default.",
            ["resetaim.keys"] = "puts mouse sensitivity, imperialist sensitivity, invert and zoom settings back to default.",
            ["resetgyro"] = "puts every gyro setting back to default except gyro aim.",
            ["resettouch"] = "puts every touch setting back to default.",
            // added to a hint: a sensitivity's RESET (controller), a button's CLEAR / RESET cell, a button waiting for its new one
            ["resetline"] = "y: reset to default.",
            ["clearcell"] = "clear all buttons.",
            ["clearcell.keys"] = "clear all keys.",
            ["resetcell"] = "back to its default buttons.",
            ["resetcell.keys"] = "back to its default keys.",
            ["waiting"] = "hold the new button, or two for a combo, then let go. wait to keep it as it is.",
            ["waiting.keys"] = "hold the new key, or two for a combo, then let go. esc keeps it as it is.",

            // the gyro and touch rows on SECOND HUNT SETTINGS' CONTROLS tab: each row's "<label>.<value>" (the label
            // without spaces), or "<label>"; the other settings rows' hints are SettingHints.cs'
            ["gyroaim.off"] = "gyro aim is off.",
            ["gyroaim.on"] = "move the device to fine-tune your aim. right stick still works as normal.",
            // a PC (".desktop"): the controller's own gyroscope, read through SDL3 (steam input or ds4windows hide it)
            ["gyroaim.on.desktop"] = "move the controller to fine-tune your aim. right stick still works as normal. no response: turn off steam input for this controller.",
            ["gyroaim.zoom.desktop"] = "gyro aim only while zoomed or scanning. move the controller to aim.",
            ["gyrospeed.desktop"] = "how much the view turns left and right compared to the controller.",
            ["gyrospeed.100%.desktop"] = "view turns left and right exactly as much as the controller does.",
            ["gyroyspeed.desktop"] = "how much the view looks up and down when you tilt the controller forward or back.",
            ["gyroaxis.yaw.desktop"] = "turn the controller left and right to aim, about its own face. tilted in your hands, this mixes in roll: player suits a controller better.",
            ["gyroaxis.player.desktop"] = "turn left and right to aim based on real up and down, however you hold the controller. best for a controller.",
            ["gyroaxis.roll.desktop"] = "tilt the controller like a steering wheel to aim left and right.",
            ["invertgyropitch.on.desktop"] = "tilting the front of the controller up looks down.",
            ["invertgyropitch.off.desktop"] = "tilting the front of the controller up looks up.",
            ["invertgyroyaw.on.desktop"] = "turning the controller left turns the view right.",
            ["invertgyroyaw.off.desktop"] = "turning the controller left turns the view left.",
            ["gyrodeadband.off.desktop"] = "all controller motion moves the view.",
            ["gyrosmoothing.off.desktop"] = "no smoothing. view follows the controller exactly.",
            ["gyroaim.zoom"] = "gyro aim only while zoomed or scanning.",
            ["gyrospeed"] = "how much the view turns left and right compared to the device.",
            ["gyrospeed.100%"] = "view turns left and right exactly as much as the device does.",
            ["gyroyspeed"] = "how much the view looks up and down when you tilt the device forward or back.",
            ["gyroyspeed.follow"] = "matches gyro speed. change it to set up and down separately.",
            ["gyroaxis.yaw"] = "turn the device left and right to aim, based on the screen's up direction.",
            ["gyroaxis.player"] = "turn left and right to aim based on real up and down, however you hold the device.",
            ["gyroaxis.roll"] = "tilt the device like a steering wheel to aim left and right.",
            ["invertgyropitch.on"] = "tilting the top of the device toward you looks down.",
            ["invertgyropitch.off"] = "tilting the top of the device toward you looks up.",
            ["invertgyroyaw.on"] = "turning the device left turns the view right.",
            ["invertgyroyaw.off"] = "turning the device left turns the view left.",
            ["gyrodeadband"] = "slow motion under this many degrees a second is ignored so a steady hand stays still. higher hides more shake but makes small aim adjustments stiffer.",
            ["gyrodeadband.off"] = "all device motion moves the view.",
            ["gyrosmoothing"] = "smooths out small, slow shakes like the bump of a trigger press. quick turns are never smoothed or delayed. higher smooths bigger bumps.",
            ["gyrosmoothing.off"] = "no smoothing. view follows the device exactly.",
            ["gyroacceleration"] = "slow motion turns this much less for fine aim at high speeds. fast turns keep full gyro speed.",
            ["gyroacceleration.off"] = "slow and fast turns use the same gyro speed.",
            ["imperialistgyrospeed"] = "gyro speed while zoomed with the imperialist.",
            ["imperialistgyrospeed.paired"] = "follows gyro speed, slowed by the zoom, while auto imp gyro is on. turn that off to set this separately.",
            ["autoimpgyro.on"] = "both gyro speeds are paired. imperialist is always finer than normal gyro aim.",
            ["autoimpgyro.off"] = "gyro speed and imperialist gyro speed are set independently.",
            ["aimwhilefiring.on"] = "while holding fire to charge, slide that thumb to aim. a tap still fires as normal.",
            ["aimwhilefiring.off"] = "holding fire only fires. sliding that thumb doesn't aim.",
            ["touchcontrols.auto"] = "on-screen controls in campaign and matches, only when no controller is connected.",
            ["touchcontrols.on"] = "on-screen controls in campaign and matches, always.",
            ["touchcontrols.off"] = "no on-screen controls.",
            ["touchcontrols.aim only"] = "no buttons, the whole screen aims.",
            ["touchaim.stylus"] = "drag your right finger to aim, like the ds stylus.",
            ["touchaim.stick"] = "your right finger controls a floating stick.",
            ["touchaimspeed"] = "how far stylus aim turns per drag. 1 = 10 degrees per centimetre on any screen.",
            ["touchaimyspeed.ds"] = "vanilla stylus up and down speed, about half of left and right.",
            ["touchaimyspeed.100%"] = "touch aim looks up and down at the same speed as left and right.",
            ["touchaimyspeed.stylus"] = "how fast stylus aim looks up and down compared to left and right.",
            ["touchaimyspeed.stick"] = "how fast the touch stick looks up and down compared to left and right.",
            ["imperialisttouchspeed"] = "touch aim speed while zoomed with the imperialist, for stylus and touch stick.",
            ["imperialisttouchspeed.paired"] = "follows touch aim speed, slowed by the zoom, while auto imp touch is on. turn that off to set this separately.",
            ["autoimptouch.on"] = "both touch speeds are paired. imperialist is always finer than normal touch aim.",
            ["autoimptouch.off"] = "touch aim speed and imperialist touch speed are set independently.",

            // the CONTROLS page's PC-only nintendo layout row
            ["nintendolayout.auto"] = "switch pro, joy-con and gamecube controllers go by their button letters, a on the right. other controllers go by position.",
            ["nintendolayout.on"] = "buttons go by their letters, a on the right like a switch. a/b and x/y swap on an xbox pad.",
            ["nintendolayout.off"] = "buttons go by position. a is always the bottom button, like xbox. b on a nintendo pad acts as a.",
        };

        // the hint for `key` (with ".keys" for the keyboard and mouse when it has its own)
        public static string For(string key, bool keys = false) =>
            keys && All.TryGetValue(key + ".keys", out string? k) ? k : All.TryGetValue(key, out string? s) ? s : "";
    }
}
