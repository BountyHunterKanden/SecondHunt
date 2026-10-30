using System;
using System.Collections.Generic;
using System.Linq;

namespace MphRecomp.PrimeMusic
{
    // How the songs (Metroid Prime Hunters' own, HuntersMusic, and the Prime ones) are sorted and named in the menus (owner 2026-10-29, docs/PRIME_MUSIC.md): each game's songs by
    // area (a song heard in one area only goes under it, its bosses too), then the songs heard all over that game
    // (battles, menus and endings, the rest). Each song has a label that says what it is on its own ("phendrana drifts
    // theme", "thardus fight") and where it plays in its game. Kept out of the recipes (recipes/tracks/*.json): a recipe's
    // text is part of its hash, so a new name there would show every imported song as made an older way.
    public static class MusicCatalog
    {
        public sealed record Entry(string Name, string Game, string Category, string Label, string Where, int Order);

        public const string Battles = "battles", MenusEndings = "menus & endings", Other = "other";

        // each game's categories, in the menus' order: its areas in the order you reach them, then the rest
        static readonly Dictionary<string, string[]> s_categories = new()
        {
            ["mph"] = new[] { "celestial archives", "alinos", "vesper defense outpost", "arcterra", "oubliette", Battles, "multiplayer",
                MenusEndings, Other },
            ["mp1"] = new[] { "frigate orpheon", "tallon overworld", "chozo ruins", "magmoor caverns", "phendrana drifts", "phazon mines",
                "impact crater", Battles, MenusEndings, Other },
            ["mp2"] = new[] { "temple grounds", "great temple", "agon wastes", "torvus bog", "sanctuary fortress", "sky temple", Battles,
                "multiplayer", MenusEndings, Other },
            ["mp3"] = new[] { "g.f.s. olympus", "norion", "bryyo", "skytown elysia", "pirate homeworld", "g.f.s. valhalla", "phaaze",
                Battles, MenusEndings, Other },
        };

        public static readonly string[] Games = { "mph", "mp1", "mp2", "mp3" };

        public static IReadOnlyList<string> Categories(string game) => s_categories.TryGetValue(game, out string[]? c) ? c : new[] { Other };

        // (name, category, label, where it plays: a whole phrase starting "plays ..."); the order inside a category is the menus' order
        static readonly (string Name, string Category, string Label, string Where)[] s_songs =
        {
            // ---- Metroid Prime Hunters (rooms from the ROM's room music) ----
            ("MPHShadows", "celestial archives", "celestial archives theme", "plays in most of the celestial archives' rooms."),
            ("MPHTheArchives", "celestial archives", "celestial archives, halls", "plays in the celestial archives' fan room alpha and synergy core."),
            ("MPHSpaceDecay", "celestial archives", "space decay", "plays in tetra vista, new arrival registration, transfer lock, the incubation vaults and the docking bay."),
            ("MPHShipCelestial", "celestial archives", "landing site", "plays as samus' ship lands on the celestial archives."),
            ("MPHAlinos", "alinos", "alinos theme", "plays in most of alinos' rooms."),
            ("MPHTheElders", "alinos", "the elders", "plays in alimbic gardens, thermal vast, crash site, piston cave, alinos perch, council chamber and processor core."),
            ("MPHMagmaDrop", "alinos", "magma drop", "plays in alinos' magma drop."),
            ("MPHShipAlinos", "alinos", "landing site", "plays as samus' ship lands on alinos."),
            ("MPHTheOutpost", "vesper defense outpost", "vesper defense outpost theme", "plays in most of the vesper defense outpost's rooms."),
            ("MPHWatching", "vesper defense outpost", "watching", "plays in the outpost's ascension and stasis bunker."),
            ("MPHShipVdo", "vesper defense outpost", "landing site", "plays as samus' ship lands on the vesper defense outpost."),
            ("MPHDesolation", "arcterra", "arcterra theme", "plays in most of arcterra's rooms."),
            ("MPHArcterra", "arcterra", "arcterra, second theme", "plays in arcterra's other rooms."),
            ("MPHDesolationMaze", "arcterra", "desolation maze", "plays in arcterra's subterranean."),
            ("MPHShipArcterra", "arcterra", "landing site", "plays as samus' ship lands on arcterra."),
            ("MPHForeboding", "oubliette", "foreboding", "plays at the oubliette's landing site, before gorea."),
            ("MPHGorea", "oubliette", "gorea fight", "plays in the first gorea fight."),
            ("MPHOubliette", "oubliette", "gorea, final fight", "plays in the last gorea fight, and in the oubliette multiplayer arena."),
            ("MPHKanden", Battles, "kanden fight", "plays in fights with kanden in the story."),
            ("MPHSpire", Battles, "spire fight", "plays in fights with spire in the story."),
            ("MPHWeavel", Battles, "weavel fight", "plays in fights with weavel in the story."),
            ("MPHSylux", Battles, "sylux fight", "plays in fights with sylux in the story."),
            ("MPHNoxus", Battles, "noxus fight", "plays in fights with noxus in the story."),
            ("MPHTrace", Battles, "trace fight", "plays in fights with trace in the story."),
            ("MPHGuardians", Battles, "guardian fight", "plays when a hunter's guardian shows up to fight."),
            ("MPHCretaphid", Battles, "cretaphid fight", "plays in the cretaphid fights, in the celestial archives and the outpost."),
            ("MPHSlench", Battles, "slench fight", "plays in the slench fights, in alinos and arcterra."),
            ("MPHEnemies", Battles, "enemies", "a battle theme from the story's fights with enemies."),
            ("MPHDemonSpawn", Battles, "demon spawn", "a battle theme from the story's fights."),
            ("MPHCelestialVs", "multiplayer", "celestial archives vs.", "plays in the celestial archives' multiplayer arenas."),
            ("MPHAlinosVs", "multiplayer", "alinos vs.", "plays in alinos' multiplayer arenas."),
            ("MPHVdoVs", "multiplayer", "vesper defense outpost vs.", "plays in the outpost's multiplayer arenas."),
            ("MPHArcterraVs", "multiplayer", "arcterra vs.", "plays in arcterra's multiplayer arenas."),
            ("MPHIntro", MenusEndings, "intro", "plays in the intro, before the title screen."),
            ("MPHMenu", MenusEndings, "main menu", "plays in the main menu."),
            ("MPHResults", MenusEndings, "match results", "plays on the results screen after a match."),
            ("MPHStory", MenusEndings, "new game", "plays when a new story file starts."),
            ("MPHCredits", MenusEndings, "credits", "plays in the end credits."),
            ("MPHTetraGalaxy", Other, "tetra galaxy", "plays on the star map, flying between planets."),
            ("MPHPursuit", Other, "pursuit", "a tense theme from the story."),
            ("MPHAftermath", Other, "aftermath", "a theme from the story, after a big fight."),
            ("MPHEscape", Other, "escape", "plays on the timed escape after a boss."),
            ("MPHMenuUnused", Other, "unused menu theme", "a menu theme in the rom that the game doesn't play."),

            // ---- Metroid Prime ----
            ("MP1FrigateOrpheon", "frigate orpheon", "frigate orpheon theme", "plays in most of the space pirate frigate at the start, like biohazard containment and the evacuation area."),
            ("MP1FrigateOrpheonDeck", "frigate orpheon", "frigate orpheon, decks", "plays in the frigate's air lock, cargo lift and the halls up to the reactor core entrance."),
            ("MP1ParasiteQueen", "frigate orpheon", "parasite queen fight", "plays in the frigate's reactor core, fighting the parasite queen."),
            ("MP1FrigateOrpheonEscape", "frigate orpheon", "frigate escape", "plays after the parasite queen, on the timed run out of the frigate to the docking hangar."),
            ("MP1TallonOverworld", "tallon overworld", "tallon overworld theme", "plays in tallon overworld's transport rooms and around the artifact temple, the area's main theme."),
            ("MP1TallonOverworldRain", "tallon overworld", "tallon overworld, rain", "plays in tallon overworld's rainy outside rooms, like the landing site."),
            ("MP1FrigateOrpheonWreckApproach", "tallon overworld", "frigate crash site", "plays on the way into the sunken frigate in tallon overworld: its vent shafts and reactor core."),
            ("MP1CrashedShip", "tallon overworld", "inside the sunken frigate", "plays in the flooded rooms of the sunken frigate in tallon overworld, and the great tree hall."),
            ("MP1ChozoShrine", "tallon overworld", "artifact temple", "plays in tallon overworld's artifact temple, where the chozo artifacts go."),
            ("MP1MetaRidley", "tallon overworld", "meta ridley fight", "plays when meta ridley flies off the frigate at the start, and in the artifact temple fight at the end."),
            ("MP1ChozoRuinsRuinsEntrance", "chozo ruins", "ruins entrance, first time", "plays in the ruins entrance the first time you walk into the chozo ruins."),
            ("MP1ChozoRuinsExteriorB", "chozo ruins", "ruins entrance, before hive totem", "plays in the ruins entrance and nursery access on later visits, until the hive totem is beaten."),
            ("MP1ChozoRuinsExteriorA", "chozo ruins", "ruins entrance, after hive totem", "plays in the ruins entrance and nursery access once the hive totem is beaten."),
            ("MP1ChozoRuins", "chozo ruins", "chozo ruins theme", "plays in the chozo ruins' main rooms: the ruined shrine, the arboretum, the energy core and the furnace."),
            ("MP1ChozoRuinsInterior", "chozo ruins", "chozo ruins, inside", "plays in the chozo ruins' inside rooms: the hive totem, the sunchamber, the furnace and the transports."),
            ("MP1ChozoRuinsHallOfTheElders", "chozo ruins", "hall of the elders", "plays in the chozo ruins' hall of the elders, with its chozo statue puzzle."),
            ("MP1HiveTotemBattle", "chozo ruins", "hive totem fight", "plays in the chozo ruins' hive totem room, fighting the hive mecha."),
            ("MP1IncineratorDroneBattleBurnDome", "chozo ruins", "incinerator drone fight", "plays in the chozo ruins' burn dome, fighting the incinerator drone."),
            ("MP1Flaaghra", "chozo ruins", "flaaghra fight", "plays in the chozo ruins' sunchamber, fighting flaaghra."),
            ("MP1MagmoorCavernsLavaLake", "magmoor caverns", "magmoor caverns, north", "plays at magmoor's north end: the transport to the chozo ruins, save station a and the lava lake."),
            ("MP1MagmoorCaverns", "magmoor caverns", "magmoor caverns theme", "plays in magmoor caverns' lava lake, geothermal core, workstation and transport rooms."),
            ("MP1PhendranaDrifts", "phendrana drifts", "phendrana drifts theme", "plays in phendrana drifts' main snowy rooms: the shorelines, the ice ruins and specimen storage."),
            ("MP1PhendranaDriftsShorelines", "phendrana drifts", "ruined courtyard", "plays in phendrana drifts' ruined courtyard, the big ruin before the pirate labs."),
            ("MP1PhendranaDriftsIceTemple", "phendrana drifts", "chozo ice temple", "plays in phendrana drifts' chozo ice temple and the tunnel to the chapel of the elders."),
            ("MP1PhendranaDriftsPassages", "phendrana drifts", "phendrana halls", "plays in phendrana drifts' transport rooms and halls, like the way to the quarantine cave."),
            ("MP1SpacePiratesWarning", "phendrana drifts", "pirate labs, approach", "plays in the ruined courtyard and specimen storage, on the way into the pirate research labs."),
            ("MP1PhendranaBattle", "phendrana drifts", "phendrana fight", "plays in phendrana drifts' fights, like pike access and the transport to magmoor south."),
            ("MP1ThardusBattle", "phendrana drifts", "thardus fight", "plays in phendrana drifts' quarantine cave, fighting thardus."),
            ("MP1PhazonMines", "phazon mines", "phazon mines theme", "plays in the phazon mines' main quarry, mine security station, elite research and elevators."),
            ("MP1PhazonMinesEliteQuarters", "phazon mines", "elite quarters", "plays in the phazon mines' elite quarters, the omega pirate's room, before and after the fight."),
            ("MP1OmegaPirate", "phazon mines", "omega pirate fight", "plays in the phazon mines' elite quarters, fighting the omega pirate."),
            ("MP1ImpactCrater", "impact crater", "impact crater theme", "plays in the impact crater from the crater entry point down to metroid prime."),
            ("MP1MetroidPrimeBattleExoskeleton", "impact crater", "metroid prime fight 1", "plays in the impact crater's subchamber five, fighting metroid prime's first form."),
            ("MP1MetroidPrimeBattle", "impact crater", "metroid prime fight 2", "plays in the metroid prime lair, fighting metroid prime's last form, the core essence."),
            ("MP1BattleThemeA", Battles, "mini boss fight", "plays in mini boss fights: the ruined shrine beetle, the chapel of the elders sheegoth, the dynamo drones."),
            ("MP1BattleThemeB", Battles, "battle theme", "plays in short fights all over: the chozo ghosts in the ruins, the pirate labs, the mines' research rooms."),
            ("MP1SpacePirates", Battles, "space pirates", "plays in the pirate bases: phendrana's research labs and the mines' ore processing and elite control."),
            ("MP1SpacePirateBattle", Battles, "space pirate fight", "plays in space pirate fights in phendrana's research labs and the phazon mines."),
            ("MP1MetroidPrimeTheme", MenusEndings, "title theme", "plays on the title screen."),
            ("MP1FileSelect", MenusEndings, "file select", "plays on the file select and options menus."),
            ("MP1SamusTheme", MenusEndings, "samus theme", "plays in the opening scene, when samus first shows."),
            ("MP1EscapeFromImpactCrater", MenusEndings, "escape from impact crater", "plays in the ending, as samus gets out of the impact crater."),
            ("MP1MetroidPrimeCredits", MenusEndings, "credits", "plays in the end credits."),
            ("MP1PuzzleRoom", Other, "puzzle rooms", "plays in quiet puzzle rooms: the energy core, the observatory, the geothermal core, the central dynamo."),
            ("MP1SpacePiratesAftermath", Other, "pirate bases, after", "plays in phendrana's research labs and the phazon mines once their pirates are gone."),

            // ---- Metroid Prime 2: Echoes ----
            ("MP2SplinterHiveOpening", "temple grounds", "landing site", "plays at temple grounds' landing site at the start, down the hive access tunnel to hive chamber a."),
            ("MP2TempleGrounds", "temple grounds", "temple grounds theme", "plays in temple grounds' gfmc compound, industrial site, temple assembly site and sacred path."),
            ("MP2FallenGFTroopers", "temple grounds", "fallen troopers", "plays in temple grounds' hive tunnel and command chamber, where the dead federation troopers lie."),
            ("MP2SplinterHive", "temple grounds", "splinter hive", "plays in temple grounds' hive chambers, hive tunnel and hive storage."),
            ("MP2DarkTrooper", "temple grounds", "dark troopers fight", "plays in the hive chambers, command chamber and communication area, fighting the ing-taken troopers."),
            ("MP2LuminothTemple", "temple grounds", "luminoth halls", "plays in temple grounds' luminoth rooms: the path of honor, the meeting grounds and the hall of eyes."),
            ("MP2GreatTemple", "great temple", "great temple theme", "plays in the great temple's temple sanctuary and main energy controller, where u-mos waits."),
            ("MP2GreatTemplePassages", "great temple", "great temple halls", "plays in the great temple's halls and transport rooms."),
            ("MP2AgonWastes", "agon wastes", "agon wastes theme", "plays in agon wastes' rooms and the desert outside."),
            ("MP2DarkAgonWastes", "agon wastes", "dark agon wastes", "plays in dark agon wastes, agon's dark world side."),
            ("MP2Amorbis", "agon wastes", "amorbis fight", "plays in the dark agon temple, fighting amorbis."),
            ("MP2TorvusBog", "torvus bog", "torvus bog theme", "plays in torvus bog's swamps and rooms."),
            ("MP2TorvusCatacombs", "torvus bog", "torvus catacombs", "plays in torvus bog's underground catacombs."),
            ("MP2DarkTorvusBog", "torvus bog", "dark torvus bog", "plays in dark torvus bog, torvus' dark world side."),
            ("MP2DarkTorvusCatacombs", "torvus bog", "dark torvus catacombs", "plays in dark torvus bog's underground rooms."),
            ("MP2ChykkaBattleLarva", "torvus bog", "chykka fight 1", "plays in the dark torvus temple, fighting the chykka larva."),
            ("MP2ChykkaBattleAdult", "torvus bog", "chykka fight 2", "plays in the dark torvus temple, fighting the grown chykka."),
            ("MP2SanctuaryFortress", "sanctuary fortress", "sanctuary fortress theme", "plays in sanctuary fortress' main rooms, out on the cliffs."),
            ("MP2SanctuaryFortressInterior", "sanctuary fortress", "sanctuary fortress, inside", "plays in sanctuary fortress' inside rooms and halls."),
            ("MP2IngHive", "sanctuary fortress", "ing hive", "plays in the ing hive, sanctuary's dark world side."),
            ("MP2Quadraxis", "sanctuary fortress", "quadraxis fight", "plays in the ing hive's hive temple, fighting quadraxis."),
            ("MP2DarkTemple", "sky temple", "sky temple grounds", "plays in the sky temple grounds, temple grounds' dark world side, and the sky temple's energy controller."),
            ("MP2EmperorIngChrysalis", "sky temple", "emperor ing fight 1", "plays in the sky temple's sanctum at the end: the emperor ing's first two forms."),
            ("MP2EmperorIngSpider", "sky temple", "emperor ing fight 2", "plays in the sky temple's sanctum at the end: the emperor ing's last form."),
            ("MP2SkyTempleEscape", "sky temple", "sky temple escape", "plays after the emperor ing, on the timed run out of the sanctum through the sky temple gateway."),
            ("MP2BattleTheme", Battles, "battle theme", "plays in bigger fights, like the agon temple, torvus' bridges and grove, and sanctuary's main research."),
            ("MP2IngBattle", Battles, "ing fight", "plays in fights with the ing, all over the game."),
            ("MP2SpacePirateBattle", Battles, "space pirate fight", "plays in space pirate fights: agon's mining stations and command center, and torvus grove."),
            ("MP2SpacePiratesWarning", Battles, "space pirates, warning", "plays in agon's and torvus' space pirate bases once the pirates know you're there."),
            ("MP2BossApproachPrelude", Battles, "before a boss", "plays in the rooms just before a big boss, like the dark torvus temple and the hive temple."),
            ("MP2BossDefeatedAftermath", Battles, "after a boss", "plays in a boss's room once you beat the boss."),
            ("MP2DarkSamusEncounterPrelude", Battles, "dark samus shows up", "plays just before a dark samus fight, as she shows up."),
            ("MP2DarkSamus", Battles, "dark samus fight", "plays in the dark samus fights in agon's main reactor and sanctuary's aerie."),
            ("MP2MultiplayerTheme", "multiplayer", "multiplayer theme a", "plays in echoes' multiplayer matches."),
            ("MP2MultiplayerTheme2", "multiplayer", "multiplayer theme b", "plays in echoes' multiplayer matches."),
            ("MP2MultiplayerHurryUp", "multiplayer", "multiplayer, hurry up", "plays in echoes' multiplayer matches when time is almost up."),
            ("MP2EchoesTitle", MenusEndings, "title theme", "plays on the title screen and file select."),
            ("MP2MetroidPrime2EchoesCredits", MenusEndings, "credits", "plays in the end credits."),
            ("MP2TheLuminoth", Other, "the luminoth", "plays when the luminoth talk to you, like u-mos when you bring back a light energy."),
            ("MP2EnergyController", Other, "energy controllers", "plays in the light world's energy controller rooms, one in each area."),
            ("MP2DarkEnergyController", Other, "dark energy controllers", "plays in the dark world's energy controller rooms, one in each area."),
            ("MP2PuzzleRoom", Other, "puzzle rooms", "plays in quiet puzzle rooms: the portal terminal, bioenergy production and the aerie."),
            ("MP2SpacePiratesAftermath", Other, "pirate bases, after", "plays in the space pirate bases in agon and torvus once their pirates are gone."),

            // ---- Metroid Prime 3: Corruption ----
            ("MP3Calm1NorionOlympus", "g.f.s. olympus", "g.f.s. olympus theme", "plays on the g.f.s. olympus' bridge and medical bay at the start, and in norion's quiet rooms."),
            ("MP3GalacticFederation", "g.f.s. olympus", "galactic federation", "plays in the g.f.s. olympus' hangar and aurora unit room, at the briefing with the other hunters."),
            ("MP3CorruptedAmbience", "norion", "norion, under attack", "plays on norion during the pirate attack, like generator a, and on the olympus after."),
            ("MP3MetaRidleyBattleNorion", "norion", "meta ridley fight", "plays at norion's generator c, fighting meta ridley down the shaft."),
            ("MP3Bryyo", "bryyo", "bryyo theme", "plays at bryyo's landing site and the outside ruins."),
            ("MP3BryyoInterior", "bryyo", "bryyo fire", "plays in bryyo fire's rooms, like the pump control puzzle and the hall where you meet rundas."),
            ("MP3BryyoInterior2", "bryyo", "bryyo, inside", "plays in bryyo's inside rooms and halls."),
            ("MP3BryyoIce", "bryyo", "bryyo ice", "plays in bryyo's ice side."),
            ("MP3TheCorruption", "bryyo", "the corruption", "plays in bryyo's halls taken over by phazon."),
            ("MP3RundasBattle", "bryyo", "rundas fight", "plays on bryyo, fighting rundas, the ice hunter."),
            ("MP3ReptilicusBattle2", "bryyo", "reptilicus fight", "plays in bryyo fire, fighting the reptilicus after the pump puzzle."),
            ("MP3MogenarBattle", "bryyo", "mogenar fight", "plays in bryyo's leviathan seed, fighting mogenar."),
            ("MP3SkyTown", "skytown elysia", "skytown theme", "plays in skytown elysia's main rooms, the spire and the tram rides."),
            ("MP3SkyTownCorrupted", "skytown elysia", "skytown, corrupted", "plays in skytown elysia's rooms full of metroids and phazon, and its elevators."),
            ("MP3AuroraChamber", "skytown elysia", "aurora chamber", "plays in skytown elysia's aurora unit chamber."),
            ("MP3GhorBattle", "skytown elysia", "ghor fight", "plays in skytown elysia, fighting ghor."),
            ("MP3Helios", "skytown elysia", "helios fight", "plays in elysia's leviathan seed, fighting helios."),
            ("MP3PirateHomeworld", "pirate homeworld", "pirate homeworld theme", "plays on the pirate homeworld's skyways and walkways out in the acid rain."),
            ("MP3Calm2", "pirate homeworld", "pirate homeworld, calm", "plays in the pirate homeworld's quiet rooms, and the olympus meeting area."),
            ("MP3GalacticFederation2", "pirate homeworld", "federation assault", "plays on the pirate homeworld when the federation attacks, like the security door and the pirate hall."),
            ("MP3MetroidBattle", "pirate homeworld", "metroid fight", "plays in the pirate homeworld's metroid rooms, fighting metroids."),
            ("MP3Gandrayda", "pirate homeworld", "gandrayda fight", "plays on the pirate homeworld, fighting gandrayda."),
            ("MP3OmegaRidley", "pirate homeworld", "omega ridley fight", "plays in the pirate homeworld's leviathan seed, fighting omega ridley."),
            ("MP3Valhalla", "g.f.s. valhalla", "g.f.s. valhalla theme", "plays on the g.f.s. valhalla, the wrecked federation ship."),
            ("MP3CorruptionDread", "g.f.s. valhalla", "g.f.s. valhalla, dread", "plays all through the g.f.s. valhalla's dark halls."),
            ("MP3Phaaze", "phaaze", "phaaze theme", "plays on phaaze, the phazon planet at the end, all the way down to aurora unit 313."),
            ("MP3DarkSamusBattle", "phaaze", "dark samus fight", "plays on phaaze, fighting dark samus at the end."),
            ("MP3AuroraUnit313", "phaaze", "aurora unit 313 fight 1", "plays in phaaze's core, fighting aurora unit 313."),
            ("MP3AuroraUnit313Phase2", "phaaze", "aurora unit 313 fight 2", "plays in phaaze's core: aurora unit 313's second half."),
            ("MP3BattleThemeSmallEnemies", Battles, "small fights", "plays in fights with small enemies all over the game."),
            ("MP3BattleThemeHighIntensity", Battles, "big fights", "plays in big fights, like the pirate attack on norion."),
            ("MP3GFBattleTheme", Battles, "battle theme", "plays in fights on the g.f.s. olympus when the pirates board, and in fights later on."),
            ("MP3SpacePirates", Battles, "space pirates", "plays in rooms full of space pirates, when they come at you."),
            ("MP3RundasEncounterPrelude", Battles, "before a hunter fight", "plays just before a fight with one of the hunters, rundas and the rest."),
            ("MP3DarkSamusEncounterPrelude", Battles, "dark samus shows up", "plays just before a dark samus fight, as she shows up."),
            ("MP3Berserker", Battles, "berserker fight", "plays in berserker lord fights, like the olympus hangar and elysia."),
            ("MP3CorruptionTheme", MenusEndings, "title theme", "plays on the title screen."),
            ("MP3Ending", MenusEndings, "ending", "plays in the ending, after phaaze."),
            ("MP3Credits", MenusEndings, "credits", "plays in the end credits."),
            ("MP3InTheCockpit", Other, "in the cockpit", "plays in samus' ship cockpit at every landing site, and flying between planets."),
            ("MP3LeviathanSeed", Other, "leviathan seeds", "plays inside the leviathan seeds, before their guardians."),
            ("MP3GeneratorRoom", Other, "generator rooms", "plays in norion's generator rooms and the pirate homeworld's portal rooms."),
            ("MP3ItemRoom", Other, "item rooms", "plays in the rooms where samus gets a new item, in every area."),
        };

        static readonly Lazy<Dictionary<string, Entry>> s_byName = new(() =>
        {
            var d = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < s_songs.Length; i++)
            {
                var (name, cat, label, where) = s_songs[i];
                string game = name.Length > 3 ? name[..3].ToLowerInvariant() : "";
                d[name] = new Entry(name, game, cat, label, where, i);
            }
            return d;
        });

        // a song's entry; one the list doesn't have yet (a new recipe) goes under its game's "other", by its recipe title
        public static Entry Of(Track t) => s_byName.Value.TryGetValue(t.Name, out Entry? e) ? e
            : new Entry(t.Name, t.Game, Other, t.Title.ToLowerInvariant(), t.Group.Length > 0 ? "plays in " + t.Group.ToLowerInvariant() + "." : "", s_songs.Length);

        public static bool Listed(string name) => s_byName.Value.ContainsKey(name);

        public static string Label(string name) => s_byName.Value.TryGetValue(name, out Entry? e) ? e.Label : name;

        // the menus' order: game, category, then the list's order
        public static int SortKey(Track t)
        {
            Entry e = Of(t);
            int game = Array.IndexOf(Games, t.Game), cat = Categories(t.Game).ToList().IndexOf(e.Category);
            return (Math.Max(game, 0) * 100 + Math.Max(cat, 0)) * 1000 + e.Order;
        }

        public static List<Track> Sorted(IEnumerable<Track> tracks) => tracks.OrderBy(SortKey).ToList();

        // every song id of a category, imported or not (hiding a category hides the songs imported later too)
        public static IEnumerable<string> Ids(string game, string category) =>
            MusicLibrary.All.Where(t => t.Game == game && Of(t).Category == category).Select(t => t.Id);

        public static IEnumerable<string> Ids(string game) => MusicLibrary.All.Where(t => t.Game == game).Select(t => t.Id);
    }
}
