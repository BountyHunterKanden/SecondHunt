using System.Collections.Generic;

// SECOND HUNT SETTINGS' and ONLINE SETTINGS' hint texts (owner 2026-10-17: "instead of having the hint text explain all
// options, it should just explain the one that is selected"; 2026-10-23 F2: every setting, not only CONTROLS), one table
// so the owner's edits land in one place. CONTROLS' own (and the gyro / touch rows on its tab) stay in ControlHints.cs.
// A key per row, the label without spaces or line breaks ("menu\nscreens" = "menuscreens"): "<key>.<value>" is the hint of
// the value the row is on (the value as the row shows it, lower case: "framerate.display"), "<key>" the hint of any other
// value or of a button row. ".public" after either: a public build's own wording; ".desktop": the PC's. "{name}" is the
// picked suit's or song's full name (RecompMenus.SettingsPages.cs). ONLINE SETTINGS' rows are "online.<key>.<on|off>".
// Lower case as the menus write them (the font's small capitals).
namespace MphRecomp.Frontend
{
    public static class SettingHints
    {
        public static readonly IReadOnlyDictionary<string, string> All = new Dictionary<string, string>
        {
            // ---- GAME ----
            ["samussuit.orig"] = "samus uses her vanilla suit in campaign.",
            ["samussuit"] = "samus wears {name} in campaign. changes her model and morph ball.",
            ["importgames"] = "make hd suits, morph balls and arm cannons from your own metroid prime discs.",
            ["importgames.public"] = "make hd suits and morph balls from your own metroid prime discs.",
            ["camera.first"] = "vanilla first person view in campaign.",
            ["camera.third"] = "third person camera behind samus in campaign. shows her suit's body.",
            ["maplook.map"] = "pause map uses the game's own map models. press y in the map to switch.",
            ["maplook.detailed"] = "pause map shows every room's real shape. press y in the map to switch.",
            ["mapmode.prime"] = "only shows rooms you've been in. move freely, d-pad jumps between rooms.",
            ["mapmode.hunters"] = "vanilla pause map.",
            ["matchcamera.first"] = "vanilla first person view in matches. online is always first person.",
            ["matchcamera.third"] = "third person camera behind your hunter in bot and lan matches. online is always first person.",
            ["unlocks.vanilla"] = "hunters and arenas unlock like vanilla.",
            ["unlocks.all"] = "all hunters and arenas are unlocked.",
            ["recordreplays.on"] = "records every bot match. keeps the last ten.",
            ["recordreplays.off"] = "matches aren't recorded.",
            ["preparereplays.during match"] = "sets up each replay for instant skipping while you play, in the background.",
            ["preparereplays.after match"] = "sets up each replay for instant skipping when the match ends.",
            ["preparereplays.off"] = "a replay gets set up for instant skipping the first time you watch it.",

            // ---- STREAMING (owner 10-28) ----
            ["showipaddress.on"] = "ip addresses show in the lan menus.",
            ["showipaddress.off"] = "hides every ip address on screen so it never shows on stream. you can still host and join listed games, typing an address needs this on.",
            ["showname.on"] = "your name shows as normal.",
            ["showname.off"] = "your name shows as HUNTER on your screen only. others still see your real name. editing it needs this on.",
            ["showfriendcode.on"] = "your friend code shows as normal.",
            ["showfriendcode.off"] = "hides your friend code and backup code on your screen. friends still work as normal.",

            // ---- LOOK ----
            ["menuart.original"] = "the original ds samus art behind the menus.",
            ["menuart.shuffle"] = "a random imported suit behind the menus each time you start the game.",
            ["menuart"] = "samus in {name} behind the menus.",
            ["hud.prime"] = "adds a mini map of the room you're in, top right, in campaign.",
            ["hud.ds"] = "vanilla hud in campaign, no mini map.",
            ["campaignradar.on"] = "radar top left shows nearby enemies in campaign. in multiplayer it follows show hunters on radar.",
            ["radarpickups.on"] = "multiplayer radar shows all nearby pickups, even with show hunters on radar off. campaign radar never shows pickups.",
            ["radarpickups.off"] = "radar shows no pickups. with show hunters on radar also off, there's no radar in multiplayer.",
            ["campaignradar.off"] = "no radar in campaign. in multiplayer it follows show hunters on radar.",
            // HUD + view (thread I 10-23, GitHub #1)
            ["hudsway.on"] = "hud sways slightly when you turn, like vanilla.",
            ["hudsway.off"] = "hud stays still when you turn.",
            ["shotindicator.true"] = "points at whoever shot you and follows them as you turn.",
            ["shotindicator.ds"] = "vanilla hit flash in one of four spots around the screen.",
            ["shotindicator.off"] = "no hit direction flash.",
            ["helmetopacity.100%"] = "helmet frame is solid, like vanilla.",
            ["helmetopacity.0%"] = "no helmet frame. readouts have their own opacity.",
            ["helmetopacity"] = "helmet frame is see-through. readouts have their own opacity.",
            ["visoropacity.100%"] = "vanilla visor tint.",
            ["visoropacity.0%"] = "no visor tint. scan visor isn't affected.",
            ["visoropacity"] = "visor tint is lighter than vanilla. scan visor isn't affected.",
            ["readoutopacity.100%"] = "health, ammo and beam readouts at full strength, like vanilla.",
            ["readoutopacity.0%"] = "health, ammo and beam readouts are hidden.",
            ["readoutopacity"] = "health, ammo and beam readouts are see-through.",
            ["lockreadoutstovisor.on"] = "readouts use visor opacity. readout opacity follows it.",
            ["lockreadoutstovisor.off"] = "readouts have their own opacity, separate from the visor.",
            ["fieldofview.110"] = "vanilla view, 110 degrees on a 16:9 screen. zoom and third person stay the same.",
            ["fieldofview"] = "how wide the view is in degrees on a 16:9 screen (vanilla is 110). zoom and third person scale with it.",
            ["lineart.smooth"] = "hud line art redrawn as clean lines, traced from your rom.",
            ["lineart.pixel"] = "hud line art as the original ds pixels.",
            ["framerate.display"] = "frame rate matches your screen (vsync). game logic always runs at 60.",
            ["framerate.unlimited"] = "draws as many frames as your device can. game logic always runs at 60.",
            ["framerate"] = "caps the frame rate at this number. game logic always runs at 60.",
            ["smoothframes.on"] = "smooths aim, camera and movement above 60 fps.",
            ["smoothframes.off"] = "no smoothing above 60 fps. each game frame holds until the next.",

            // ---- SOUND ----
            ["sfxvolume"] = "sound effects volume, 0 to 9. changes live.",
            ["musicvolume"] = "music volume, 0 to 9. changes live.",
            ["voicevolume"] = "how loud other players' voice chat is, 0 to 200%. changes live.",
            ["musicquality.orig"] = "original ds music, as is. changes live.",
            ["musicquality.orig+fix"] = "original ds music with clicks and clipping fixed. changes live.",
            ["musicquality.hq+fix"] = "higher quality music with clicks and clipping fixed. changes live.",
            ["musicquality.hq+tone"] = "higher quality music with clicks and clipping fixed, keeping the ds's treble. changes live.",
            ["matchmusic.arena"] = "each arena plays its own music.",
            ["matchmusic.shuffle"] = "a random song each match, from the songs you have on in song list.",
            ["matchmusic"] = "every match plays {name}.",
            ["menumusic.original"] = "main menu plays its vanilla music.",
            ["menumusic.shuffle"] = "main menu plays a random song each time, from the songs you have on in song list.",
            ["menumusic"] = "main menu plays {name}.",
            ["musicpicker.on"] = "adds a music row to match rules so you can pick each match's song.",
            ["musicpicker.off"] = "no music row in match rules.",

            // ---- FIRST HUNT ----
            ["extras"] = "play the first hunt demo you imported as its own game.",
            ["firsthuntpieces.vanilla"] = "first hunt plays like the original cart, with its own music and objects.",
            ["firsthuntpieces.second hunt"] = "everything below uses second hunt's version.",
            ["firsthuntpieces.mixed"] = "some settings below use first hunt's own, some use second hunt's.",
            ["fhmenumusic.first hunt"] = "first hunt's menus play their own music.",
            ["fhmenumusic.second hunt"] = "first hunt's menus play your second hunt menu music.",
            ["fhtrainingmusic.first hunt"] = "first hunt's training levels (regulator, morph ball, survivor) play their own music.",
            ["fhtrainingmusic.second hunt"] = "first hunt's training levels (regulator, morph ball, survivor) play second hunt's music.",
            ["fhmatchmusic.first hunt"] = "first hunt's multiplayer levels play their own music.",
            ["fhmatchmusic.second hunt"] = "first hunt's multiplayer levels play your second hunt match music.",
            ["fhobjects.first hunt"] = "first hunt's objects, like jump pads, use their own models.",
            ["fhobjects.second hunt"] = "first hunt's objects, like jump pads, use second hunt's models.",
            ["fhmenuscreens.one"] = "first hunt's two ds screens combined into one.",
            ["fhmenuscreens.two"] = "first hunt's two ds screens side by side.",

            // ---- APP ----
            ["skipbootlogos.on"] = "the game starts without the nintendo and actimagine screen and the esrb notice.",
            ["skipbootlogos.off"] = "the nintendo and actimagine screen and the esrb notice show when the game starts, like on the ds.",
            ["skipintro.on"] = "also skips the nintendo presents logos and the intro movie. the game starts at touch to start.",
            ["skipintro.off"] = "the nintendo presents logos and the intro movie play when the game starts.",
            ["updatealerts.on"] = "checks github for a newer second hunt when the game starts and shows it on the main menu.",
            ["updatealerts.off"] = "only checks for updates when you press check for updates.",
            ["updates"] = "checks github for a newer second hunt now and installs it.",
            ["friendreplays.on"] = "friends can send you replays online. they wait in the shared section of replays until you download or delete them.",
            ["friendreplays.off"] = "nobody can send you replays.",
            ["crashlog"] = "if the app crashed, share the latest crash report with the developer. no personal data.",
            ["crashlog.desktop"] = "if the game crashed, opens the folder with the latest crash report to send to the developer. no personal data.",
            ["sharelog"] = "share this session's log with the developer, like right after a bad online match. no personal data.",
            ["sharelog.desktop"] = "saves this session's log and opens its folder to send to the developer, like after a bad match. no personal data.",
            ["credits"] = "who made the tools and libraries this app uses, and their licences.",

            // ---- DEVELOPER ----
            ["controlspage.new"] = "options > controls and the ship's options open the new control customizer.",
            ["controlspage.classic"] = "options > controls and the ship's options open the vanilla stylus and dual mode boxes.",
            ["statustext.on"] = "fps, timings and scoreboard at the top left during gameplay. l3 cycles it.",
            ["statustext.off"] = "no status text during gameplay.",
            ["developer.on"] = "shows the dev tools button below.",
            ["developer.off"] = "hides the dev tools button.",
            ["devtools"] = "developer launcher: trophy viewers, rooms, animations, music a/b.",

            // ---- ONLINE SETTINGS (ONLINE PLAY's hub) ----
            ["online.findablebyname.on"] = "others can find you by searching your name.",
            ["online.findablebyname.off"] = "you don't show up when others search your name.",
            ["online.voicechat.on"] = "talk and listen in online lobbies and matches.",
            ["online.voicechat.off"] = "voice chat off. you can't hear others and they can't hear you.",
            ["online.pushtotalk.on"] = "hold r3 or tap talk to speak.",
            ["online.pushtotalk.off"] = "open mic. your voice sends whenever you speak.",
            ["online.chat.on"] = "see and send chat in online lobbies and matches.",
            ["online.chat.off"] = "chat off. you won't see messages and can't send any.",
            ["online.duckmusic.on"] = "music lowers while others talk, then comes back.",
            ["online.duckmusic.off"] = "music volume stays the same while others talk.",
            ["online.noextrapickupgames.on"] = "hides games with extra pickups (pirate blaster). you can't join them.",
            ["online.noextrapickupgames.off"] = "games with extra pickups (pirate blaster) show up and can be joined.",
            ["online.noextracharactergames.on"] = "hides games with trooper or dark samus. you can't join them.",
            ["online.noextracharactergames.off"] = "games with trooper or dark samus show up and can be joined.",
            ["online.noextraarenagames.on"] = "hides games in imported arenas (echoes). you can't join or search for them.",
            ["online.noextraarenagames.off"] = "games in imported arenas (echoes) show up and can be joined or searched for.",
        };

        // the hint for a row's key on `value` (as the row shows it): ".public" / ".desktop" wording first, then the
        // value's, then the row's; null when the table has none
        public static string? For(IReadOnlyDictionary<string, string> table, string key, string value, bool publicBuild, bool desktop)
        {
            string v = key + "." + value.ToLowerInvariant();
            foreach (string k in new[] { v, key })
            {
                if (publicBuild && table.TryGetValue(k + ".public", out string? p)) return p;
                if (desktop && table.TryGetValue(k + ".desktop", out string? d)) return d;
                if (table.TryGetValue(k, out string? h)) return h;
            }
            return null;
        }

        // an ONLINE SETTINGS row's line (`label` as the page writes it), else its own
        public static string Online(string label, string value, string own) =>
            All.TryGetValue("online." + label.Replace(" ", "") + "." + value, out string? h) ? h : own;
    }
}
